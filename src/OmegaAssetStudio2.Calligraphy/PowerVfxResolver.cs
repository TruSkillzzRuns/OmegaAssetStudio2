using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Engine.Particle;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.Calligraphy;

// CANONICAL VFX-component resolver for an TargetClient power.
//
// Mirrors PowerAnimResolver, but for the OTHER PowerFX* components attached to
// a power's class default object:
//   - PowerFXParticle      â†’ character-attached particle effects
//                            (hammer glow, hand glow, cast aura, etc.)
//   - PowerFXHit           â†’ on-target impact VFX (basichit)
//   - PowerFXHit_Crit      â†’ on-target crit-hit VFX
//   - PowerFXBeam          â†’ beam emitters (secondary FX, lasers)
//   - PowerFXDecal         â†’ ground decals (scorch marks)
//   - PowerFXMeshAttachmentâ†’ static-mesh attachments (props, weapons)
//
// Each component instance lives as its own UPK export with class="PowerFX<...>"
// and outer="default__<powerclassname>". We read the particlesystemtemplate
// (and a few other useful props) per component and surface them as a flat list
// the playback layer can iterate. The particle system reference is given as
// the short export-or-import name; callers resolve it via the same UPK's
// import/export tables (it's usually an export inside the per-power UPK).
public static class PowerVfxResolver
{
    public sealed record VfxBinding(
        string ComponentClass,      // e.g. "PowerFXParticle"
        string ComponentName,       // e.g. "castvfx"
        string? ParticleSystemRef,  // e.g. "vfx_ball_lightning_cast" (nullable for non-particle components)
        string? ParticleSystemFullPath, // e.g. "vfx_thor.particles.vfx_ball_lightning_cast" (suitable for ResolveParticleSystemByPath)
        UParticleSystem? ResolvedParticleSystem, // pre-resolved if the particle is an export inside the per-power UPK
        string? AttachSocketName,   // bone/socket name to attach to (null = root)
        float ActivationOffset,     // seconds after skill start
        string? ActivationPoint,    // enum-byte name (e.g. "FXAP_OnActivation", "FXAP_OnHit")
        bool LoopIndefinitely,
        bool AttachToSubject,
        string SourceUpk,
        string SourceUpkFullPath,
        // PowerFXDecal/EntityFXDecal.decalmat — points at a DecalMaterial /
        // MaterialInstanceConstant / MaterialInstanceTimeVarying that drives the
        // ground decal's color. Captured so the recolor pipeline can pull this
        // material's vector params into scope alongside the particle materials.
        string? DecalMaterialRef = null);

    public sealed record ResolvedVfx(string PowerClassName, string SourceUpk, IReadOnlyList<VfxBinding> Bindings);

    private static readonly Dictionary<string, ResolvedVfx?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<ResolvedVfx?> ResolveAsync(string powerClassName, string canonicalCookedDir, UpkFileRepository repo)
        => ResolveAsync(powerClassName, canonicalCookedDir, repo, followSiblings: true);

    public static async Task<ResolvedVfx?> ResolveAsync(string powerClassName, string canonicalCookedDir, UpkFileRepository repo, bool followSiblings)
    {
        if (string.IsNullOrWhiteSpace(powerClassName) || string.IsNullOrWhiteSpace(canonicalCookedDir))
            return null;
        if (_cache.TryGetValue(powerClassName, out var cached)) return cached;

        string upkPath = Path.Combine(canonicalCookedDir, $"UC__{powerClassName}_SF.upk");
        if (!File.Exists(upkPath)) { _cache[powerClassName] = null; return null; }

        try
        {
            var header = await repo.LoadUpkFile(upkPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            string classLow = powerClassName.ToLowerInvariant();
            // The power's own components are outer'd to its class default
            // ("default__<powerclass>"). But many powers also carry a local
            // projectile class (e.g. MarvelProjectile_<powerclass>) whose
            // ProjectileFX* components are outer'd to THAT class's default.
            // Accept any "default__*" outer so both surface — without this the
            // resolver misses every ProjectileFX binding, which is where most
            // skill VFX colors actually live (the projectile itself, not the
            // optional cast-time PowerFXParticle).
            string powerDefaultOuter = "default__" + classLow;
            var bindings = new List<VfxBinding>();
            // PowerFXProjectile.projectileclass references — the ONE statically
            // followable cross-UPK gameplay link in cooked content (inventory:
            // 284 instances). Each leaf is a class whose UPK lives at
            // UC__<class>_SF.upk. We resolve the projectile package after the
            // main walk so its VFX get added to this skill's bindings.
            var projectileClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Pre-build a name â†’ ParticleSystem export lookup so each binding can
            // capture its resolved UParticleSystem directly (skipping the cross-UPK
            // fallback hunt for the most common case where the particle lives in
            // the same per-power UPK).
            var particleExports = new Dictionary<string, UnrealExportTableEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (UnrealExportTableEntry e in header.ExportTable)
            {
                string cls = (e.ClassReferenceNameIndex?.Name ?? "").ToLowerInvariant();
                if (cls != "particlesystem") continue;
                string nm = e.ObjectNameIndex?.Name ?? "";
                if (!string.IsNullOrEmpty(nm) && !particleExports.ContainsKey(nm))
                    particleExports[nm] = e;
            }

            foreach (UnrealExportTableEntry export in header.ExportTable)
            {
                string cls = export.ClassReferenceNameIndex?.Name ?? string.Empty;
                string clsLow = cls.ToLowerInvariant();
                // We only care about VFX-producing components, not anim/sound/physics.
                if (!IsVfxComponentClass(clsLow)) continue;
                string outer = (export.OuterReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                // Power-class default first (cheap exact match), then any
                // other class default — covers projectile / sub-power defaults
                // that live in the same per-power UPK.
                if (outer != powerDefaultOuter && !outer.StartsWith("default__", StringComparison.Ordinal)) continue;

                try
                {
                    if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                    if (export.UnrealObject is not IUnrealObject u || u.UObject is not UObject obj) continue;

                    string? particle = null;
                    string? decalmat = null;
                    string? socket = null;
                    string? activationPoint = null;
                    float activationOffset = 0f;
                    bool loop = false;
                    bool attachToSubject = false;

                    foreach (var prop in obj.Properties)
                    {
                        string pname = (prop.NameIndex?.Name ?? string.Empty).ToLowerInvariant();
                        switch (pname)
                        {
                            case "particlesystemtemplate":
                            case "particlesystem":
                                if (prop.Value is UObjectProperty op) particle = op.Object?.Name;
                                break;
                            case "decalmat":
                                // PowerFXDecal / EntityFXDecal: the material that
                                // colors the ground decal (often a MITV/MIC).
                                if (prop.Value is UObjectProperty dmop) decalmat = dmop.Object?.Name;
                                break;
                            case "projectileclass":
                                // PowerFXProjectile.projectileclass: leaf name of a
                                // projectile class whose UPK is UC__<class>_SF.upk.
                                // Verified statically followable (inventory).
                                if (prop.Value is UObjectProperty pcop && !string.IsNullOrEmpty(pcop.Object?.Name))
                                    projectileClasses.Add(pcop.Object!.Name!);
                                break;
                            case "projectileclasses":
                                // Plural variant — an array of projectile class refs.
                                if (prop.Value is UArrayProperty arr && arr.PropertyValue is System.Collections.IEnumerable items)
                                {
                                    foreach (var item in items)
                                        if (item is UObjectProperty iop && !string.IsNullOrEmpty(iop.Object?.Name))
                                            projectileClasses.Add(iop.Object!.Name!);
                                }
                                break;
                            case "attachsocketname":
                            case "socketname":
                                if (prop.Value is UNameProperty np)
                                    socket = (np.PropertyValue as FName)?.Name;
                                break;
                            case "activationpoint":
                                if (prop.Value is UByteProperty byp) activationPoint = byp.EnumValue;
                                break;
                            case "activationoffset":
                                if (prop.Value is UFloatProperty fp && fp.PropertyValue is float fv) activationOffset = fv;
                                break;
                            case "loopindefinitely":
                                if (prop.Value is UBoolProperty bp1 && bp1.PropertyValue is byte b1) loop = b1 != 0;
                                break;
                            case "attachtosubject":
                                if (prop.Value is UBoolProperty bp2 && bp2.PropertyValue is byte b2) attachToSubject = b2 != 0;
                                break;
                        }
                    }

                    // Resolve the particle system if it's an export in this same UPK.
                    UParticleSystem? resolvedPs = null;
                    string? psFullPath = null;
                    // The UPK that actually HOSTS the resolved particle system —
                    // defaults to this per-power UPK, but is repointed at the
                    // foreign package when the particle is a cross-UPK import so
                    // the recolor writes the color modules where they really live.
                    string bindingUpkFull = upkPath;
                    if (!string.IsNullOrEmpty(particle) && particleExports.TryGetValue(particle, out var psExport))
                    {
                        psFullPath = psExport.GetPathName();
                        try
                        {
                            if (psExport.UnrealObject is null)
                                await psExport.ParseUnrealObject(false, false).ConfigureAwait(false);
                            if (psExport.UnrealObject is IUnrealObject pu && pu.UObject is UParticleSystem ups)
                                resolvedPs = ups;
                        }
                        catch { /* fall back to path-only */ }
                    }

                    // CROSS-UPK fallback: the component references a particle system
                    // that is NOT a local export — it's an import pointing at
                    // another package (e.g. the per-target strike bolts living in
                    // vfx_thor.upk). Resolve it from that sibling .upk so its color
                    // modules become collectable + recolorable. Without this the
                    // binding has a null ResolvedParticleSystem and every tool
                    // (probe / collector / recolor) silently skips it — which is
                    // why those effects stayed un-recolored.
                    if (resolvedPs is null && !string.IsNullOrEmpty(particle))
                    {
                        // The component's particle ref is an import — find that import
                        // in the table and walk its outer chain to the package name.
                        var imp = header.ImportTable.FirstOrDefault(i =>
                            string.Equals(i.ObjectNameIndex?.Name, particle, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(i.ClassNameIndex?.Name, "ParticleSystem", StringComparison.OrdinalIgnoreCase));
                        string pkg = imp is null ? string.Empty : ResolveImportPackageName(header, imp);
                        string selfPkg = Path.GetFileNameWithoutExtension(upkPath);
                        if (!string.IsNullOrEmpty(pkg) && !string.Equals(pkg, selfPkg, StringComparison.OrdinalIgnoreCase))
                        {
                            string foreignUpk = Path.Combine(canonicalCookedDir, pkg + ".upk");
                            if (File.Exists(foreignUpk))
                            {
                                try
                                {
                                    var fh = await repo.LoadUpkFile(foreignUpk).ConfigureAwait(false);
                                    await fh.ReadHeaderAsync(null).ConfigureAwait(false);
                                    var fExp = fh.ExportTable.FirstOrDefault(e =>
                                                    string.Equals(e.ObjectNameIndex?.Name, particle, StringComparison.OrdinalIgnoreCase) &&
                                                    string.Equals(e.ClassReferenceNameIndex?.Name, "ParticleSystem", StringComparison.OrdinalIgnoreCase));
                                    if (fExp is not null)
                                    {
                                        if (fExp.UnrealObject is null)
                                            await fExp.ParseUnrealObject(false, false).ConfigureAwait(false);
                                        if (fExp.UnrealObject is IUnrealObject fpu && fpu.UObject is UParticleSystem fups)
                                        {
                                            resolvedPs = fups;
                                            psFullPath = fExp.GetPathName();
                                            bindingUpkFull = foreignUpk; // recolor must write the foreign UPK
                                        }
                                    }
                                }
                                catch { /* sibling unreadable — leave unresolved */ }
                            }
                        }
                    }

                    bindings.Add(new VfxBinding(
                        ComponentClass: cls,
                        ComponentName: export.ObjectNameIndex?.Name ?? string.Empty,
                        ParticleSystemRef: particle,
                        ParticleSystemFullPath: psFullPath,
                        ResolvedParticleSystem: resolvedPs,
                        AttachSocketName: socket,
                        ActivationOffset: activationOffset,
                        ActivationPoint: activationPoint,
                        LoopIndefinitely: loop,
                        AttachToSubject: attachToSubject,
                        SourceUpk: Path.GetFileName(bindingUpkFull),
                        SourceUpkFullPath: bindingUpkFull,
                        DecalMaterialRef: decalmat));
                }
                catch { /* skip individual component on parse error */ }
            }

            // Follow the power into the SIBLING effect packages it spawns — the
            // condition-effect / hotspot / projectile that linger AFTER the cast
            // (e.g. PowerElementalStorm_Thor → MarvelConditionEffect_ElementalStorm_Thor).
            // These are separate UPKs with their own PowerFX components, so their
            // VFX never get recolored unless we merge their bindings in here.
            if (followSiblings)
            {
                // Combine exact-stem siblings with token-based auto-discovery, then
                // CONTENT-GATE: only merge a candidate that actually resolves VFX
                // bindings (skips empty/irrelevant packages). This auto-finds the
                // lingering after-cast effect packages without the user naming them.
                var related = new HashSet<string>(SiblingEffectClasses(powerClassName, canonicalCookedDir), StringComparer.OrdinalIgnoreCase);
                foreach (string c in AutoDiscoverRelatedEffectClasses(powerClassName, canonicalCookedDir))
                    related.Add(c);
                // Statically-followable projectile classes from PowerFXProjectile —
                // these are guaranteed gameplay links (not heuristics).
                foreach (string pcls in projectileClasses) related.Add(pcls);

                foreach (string sibClass in related)
                {
                    if (string.Equals(sibClass, powerClassName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var sub = await ResolveAsync(sibClass, canonicalCookedDir, repo, followSiblings: false).ConfigureAwait(false);
                        if (sub is not null && sub.Bindings.Count > 0)
                            bindings.AddRange(sub.Bindings);
                    }
                    catch { /* a sibling failing to resolve must not break the main power */ }
                }
            }

            var result = new ResolvedVfx(powerClassName, Path.GetFileName(upkPath), bindings);
            _cache[powerClassName] = result;
            return result;
        }
        catch
        {
            _cache[powerClassName] = null;
            return null;
        }
    }

    // Fires when ClearCache() is called. Other services (HeroSkillCatalog's UPK
    // repo, MaterialEditorService's UPK repo) subscribe so a single ClearCache
    // call invalidates EVERY in-memory cache that was holding pre-write file
    // bytes. Lets Apply / Restore work without app relaunches.
    public static event Action? CacheCleared;

    public static void ClearCache()
    {
        _cache.Clear();
        try { CacheCleared?.Invoke(); } catch { /* don't let a subscriber break invalidation */ }
    }

    public static ResolvedVfx? TryGetCached(string powerClassName)
    {
        if (string.IsNullOrWhiteSpace(powerClassName)) return null;
        return _cache.TryGetValue(powerClassName, out var v) ? v : null;
    }

    // Sibling effect packages a power spawns that linger after the cast: the
    // condition-effect / area hotspot / projectile named for the same effect stem
    // (e.g. PowerElementalStorm_Thor → MarvelConditionEffect_ElementalStorm_Thor).
    // Only returns classes whose UPK actually exists on disk.
    private static IEnumerable<string> SiblingEffectClasses(string powerClassName, string cookedDir)
    {
        string stem = powerClassName.StartsWith("Power", StringComparison.OrdinalIgnoreCase)
            ? powerClassName[5..]
            : powerClassName;
        if (string.IsNullOrWhiteSpace(stem)) yield break;

        string[] prefixes = { "MarvelConditionEffect_", "MarvelEntity_Hotspot_", "MarvelProjectile_" };
        foreach (string pfx in prefixes)
        {
            string cls = pfx + stem;
            if (File.Exists(Path.Combine(cookedDir, $"UC__{cls}_SF.upk")))
                yield return cls;
        }
    }

    // Auto-discover related effect packages (condition / hotspot / projectile)
    // when the power→effect link is naming-convention-only (the common case —
    // the prototype carries no static reference). Matches on the hero token AND a
    // distinctive effect token from the power class, then the caller content-gates
    // each candidate by whether it actually resolves VFX. Example:
    //   Power<Skill>_<Hero>  → hero "<hero>", effect tokens {token1, token2}
    //   → matches MarvelConditionEffect_ElementalStorm_Thor AND ThorPBAoEStormEffect,
    //     skips ThorShockwave / ThorBerserker (no storm/elemental token).
    private static IEnumerable<string> AutoDiscoverRelatedEffectClasses(string powerClassName, string cookedDir)
    {
        if (!Directory.Exists(cookedDir)) yield break;
        var segs = powerClassName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) yield break;   // need at least <Effect>_<Hero> to derive tokens

        string heroLow = segs[^1].ToLowerInvariant();
        if (heroLow.Length < 3) yield break;

        // Distinctive effect tokens (CamelCase-split the non-hero segments).
        var effectTokens = new List<string>();
        for (int i = 0; i < segs.Length - 1; i++)
            foreach (string t in SplitCamel(segs[i]))
                if (t.Length >= 4) effectTokens.Add(t.ToLowerInvariant());
        if (effectTokens.Count == 0) yield break;

        string[] prefixes = { "MarvelConditionEffect_", "MarvelEntity_Hotspot_", "MarvelProjectile_" };
        foreach (string pfx in prefixes)
        {
            string[] files;
            try { files = Directory.GetFiles(cookedDir, $"UC__{pfx}*_SF.upk"); }
            catch { continue; }
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f); // UC__<class>_SF
                string low = name.ToLowerInvariant();
                if (!low.Contains(heroLow)) continue;
                if (!effectTokens.Any(t => low.Contains(t))) continue;

                string cls = name;
                if (cls.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) cls = cls[4..];
                if (cls.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) cls = cls[..^3];
                if (!string.IsNullOrEmpty(cls)) yield return cls;
            }
        }
    }

    // Split a token on lower→upper case boundaries: "ElementalStorm" → Elemental, Storm.
    private static IEnumerable<string> SplitCamel(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(s[i]);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // Walk an import's Outer chain (OuterReference is a NEGATIVE import ref, 0 at
    // the package root) up to the top and return the root import's object name —
    // the package name (e.g. "vfx_thor").
    private static string ResolveImportPackageName(UpkManager.Models.UpkFile.UnrealHeader header, UnrealImportTableEntry imp)
    {
        var cur = imp;
        int guard = 0;
        while (cur is not null && guard++ < 32)
        {
            int outer = cur.OuterReference;
            if (outer == 0) break;                 // cur is the root → package
            if (outer >= 0) break;                 // points at an export (unusual) → stop
            int idx = -outer - 1;
            if (idx < 0 || idx >= header.ImportTable.Count) break;
            cur = header.ImportTable[idx];
        }
        return cur?.ObjectNameIndex?.Name ?? string.Empty;
    }

    private static bool IsVfxComponentClass(string clsLow)
    {
        // EMPIRICAL allowlist (inventory of 6,939 power/condition/hotspot/projectile
        // UPKs in this fork — see docs/Powers_VFX_Color_Reference.md §FX class
        // family). The fork extends stock UE3's PowerFX* with parallel
        // ConditionFX* / EntityFX* / per-skill subclass families, all of which
        // carry the SAME `particlesystemtemplate` ObjectProperty (verified per
        // CDO property schema). The resolver must recognise the whole family or
        // every condition / hotspot / per-target effect is silently skipped —
        // which was the cause of "lingering blue after-cast" effects.

        // Stock-UE3-style PowerFX* + ProjectileFX* (the original allowlist).
        if (clsLow is "powerfxparticle" or "powerfxhit" or "powerfxhit_crit"
                    or "powerfxbeam" or "powerfxdecal" or "powerfxmeshattachment"
                    or "powerfxprojectile" or "powerfxanimatedactor"
                    or "projectilefxparticle" or "projectilefxhit" or "projectilefxhit_crit"
                    or "projectilefxbeam" or "projectilefxdecal" or "projectilefxmeshattachment")
            return true;

        // Per-skill SUBCLASSES of the above (e.g. powerfxparticle_carnagemeleebasic_arc,
        // projectilefxparticle_<x>). Inventory confirmed they share the same
        // particlesystemtemplate property schema, so the existing per-property
        // walk handles them — we just need to opt them in by prefix.
        if (clsLow.StartsWith("powerfxparticle_", StringComparison.Ordinal) ||
            clsLow.StartsWith("powerfxhit_", StringComparison.Ordinal) ||
            clsLow.StartsWith("powerfxbeam_", StringComparison.Ordinal) ||
            clsLow.StartsWith("powerfxdecal_", StringComparison.Ordinal) ||
            clsLow.StartsWith("powerfxmeshattachment_", StringComparison.Ordinal) ||
            clsLow.StartsWith("projectilefxparticle_", StringComparison.Ordinal))
            return true;

        // CONDITION FX components — outer'd to default__<MarvelConditionEffect_*>.
        // These produce the lingering on-target / after-cast visuals (e.g. for
        // Bring the Thunder: tgtfx1/tgtfx2/tgtfxground each is a ConditionFXParticle).
        if (clsLow is "conditionfxparticle" or "conditionfxbeam" or "conditionfxdecal"
                    or "conditionfxmeshattachment")
            return true;
        if (clsLow.StartsWith("conditionfxparticle_", StringComparison.Ordinal))
            return true;

        // ENTITY FX components — outer'd to default__<MarvelEntity_Hotspot_*>.
        // Drive persistent area hotspots (Thunderclap, Shockwave, etc.).
        if (clsLow is "entityfxparticle" or "entityfxbeam" or "entityfxdecal"
                    or "entityfxmeshattachment")
            return true;
        if (clsLow.StartsWith("entityfxparticle_", StringComparison.Ordinal))
            return true;

        // Excluded: *fxsound (audio), *fxcamerashake (camera, not in-world),
        // *fxanimation / *fxlooping (anim names — PowerAnimResolver handles),
        // *fxphysicalforce / *fxphysicsweight (physics, no color),
        // *fxhide / *fxmaterialparameter / *fxmaterialswap (material param
        // overrides — colors already surface via the bound material's MIC params).
        return false;
    }
}

