using System.Numerics;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Engine;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Engine.Particle;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.Calligraphy;

// Independent hero / skill catalog used by tools that want to drive a UI from
// the Calligraphy character data WITHOUT pulling in AnimationPreviewPage's
// internal state. Mirrors the data-access patterns AnimationPreviewPage uses
// (cooked-dir glob for hero UPKs, Calligraphy.sip for skill trees) but is
// self-contained: opens its own archive handle, caches per-hero results,
// disposes cleanly.
//
// Phase 1 scope:
//   - EnumerateHeroes(cookedDir)            roster from UC__MarvelPlayer_*_SF.upk
//   - GetSkillsAsync(hero token)            visible skill list via PowerCatalog
//   - ResolveSkillVfxAsync(power)           ParticleSystem bindings via PowerVfxResolver
//   - CollectParticleMaterialsAsync(...)    walk particles -> referenced material exports
//
// All methods are synchronous file-I/O wrapped in Task.Run for off-UI execution.
public sealed class HeroSkillCatalog : IDisposable
{
    public sealed record HeroSummary(
        string Token,           // lowercase short token, e.g. "hero"
        string Variant,         // lowercase variant token (empty for base costume)
        string DisplayName,     // "Hero" or "Hero — Variant"
        string UpkPath,         // full path to the hero UPK
        // ORIGINAL-CASE filename segment preserved for downstream consumers that
        // need CamelCase boundary info (icon resolution: a filename like
        // "FooBar2" → "drop_teamup_foo_bar2", not "drop_teamup_foobar2").
        // The lowercased Token loses this boundary; RawToken keeps it.
        string RawToken = "",
        string RawVariant = "");

    public sealed record SkillMaterialRef(
        string MaterialExportPath,  // "ChBaseMaterials.M_VFX_Foo"
        string SourceUpkPath,       // absolute path of the host particle's UPK
        bool IsCrossPackage = false);  // true if the particle's material ref was an Import (the actual material lives in another UPK)

    // Every color source the skill recolor pipeline knows about. The first
    // four are the canonical sources verified in the VFX deep-dive doc; the
    // last three (ColorScaleOverLife + the two baked Constant vectors) were
    // added once the byte-patcher caught up to the catalog.
    //
    // Editable=true means SkillColorWriter knows how to patch this kind.
    public enum SkillColorKind
    {
        MicVectorParam,                       // MaterialInstanceConstant.VectorParameterValues[i]
        MaterialExpressionVector,             // MaterialExpressionVectorParameter.DefaultValue
        ParticleStartColor,                   // ParticleModuleColor.StartColor (FRawDistributionVector)
        ParticleColorOverLife,                // ParticleModuleColorOverLife.ColorOverLife (FRawDistributionVector)
        ParticleColorScaleOverLife,           // ParticleModuleColorScaleOverLife.ColorScaleOverLife
        MaterialExpressionConstant3Vector,    // MaterialExpressionConstant3Vector R/G/B baked into a material graph
        MaterialExpressionConstant4Vector,    // MaterialExpressionConstant4Vector R/G/B/A baked into a material graph
    }

    // What shape the underlying distribution has — relevant for the two
    // particle-module kinds, irrelevant for the material kinds.
    public enum DistributionShape
    {
        NotApplicable,  // material parameter, no distribution
        Constant,       // DistributionVectorConstant — single fixed Vector3
        ConstantCurve,  // DistributionVectorConstantCurve — keypoints
        Uniform,        // DistributionVectorUniform — min/max range
        Parameterized,  // DistributionVectorParameterBase — runtime-resolved
        Unknown,        // couldn't determine
    }

    // One editable (or display-only) color slot the user can see in the UI.
    // The four Kinds correspond to the four verified sources from the deep-dive
    // doc; CurrentColor is the best-effort current RGB; OwnerLabel is what we
    // show under the swatch (material name + path / emitter name).
    public sealed record SkillColorEntry(
        SkillColorKind Kind,
        string ParameterName,       // "Color1" / "StartColor" / "ColorOverLife" / etc.
        string OwnerLabel,          // "Material: mat_lightning_bolt_complex" / "Emitter: lightning_core"
        string SourceUpkPath,       // absolute path of the UPK
        Vector4 CurrentColor,       // RGBA, alpha may be unused for some kinds
        DistributionShape Shape,
        bool Editable,              // true if SkillColorWriter knows how to patch this kind
        string ExportPath = "",     // full UPK export path-name of the export the writer would patch (module, expression, or MIC)
        bool IsCrossPackage = false);  // true when the underlying material/particle ref points outside its host UPK

    private readonly object _lock = new();
    private string? _calligraphyArchivePath;
    private KapgArchiveReader? _archive;
    private BlueprintRegistry? _registry;
    private PrototypeDirectoryReader? _protoDirectory;
    private PowerPackageGraph? _packageGraph;
    private TypeDirectoryReader? _powerUnrealClasses;
    private TypeDirectoryReader? _powerIconPaths;
    private LocoIndex? _loco;
    private readonly Dictionary<string, List<PowerEntry>> _skillCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly UpkFileRepository _repo = new();

    // Auto-invalidate this catalogue's UPK header cache whenever the recolor /
    // backup pipeline tells us files on disk have changed (via
    // PowerVfxResolver.ClearCache). Without this, the next skill load would
    // still parse the OLD bytes the repo cached before the write, which is
    // exactly why the user previously had to relaunch after Apply/Restore.
    public HeroSkillCatalog()
    {
        PowerVfxResolver.CacheCleared += OnExternalCacheCleared;
    }
    private void OnExternalCacheCleared()
    {
        try { _repo.ClearHeaderCache(); } catch { }
        // Skill-tree resolution depends on Calligraphy (read-only) plus per-power
        // UPKs (which DID change on disk). Clear the per-character skill cache
        // so the next load re-walks the avatar prototype with fresh per-power
        // canonical anim data, if any.
        try { _skillCache.Clear(); } catch { }
    }

    public bool IsArchiveOpen => _archive is not null;
    public int ArchiveEntryCount => _archive?.Entries.Count ?? 0;

    // Lazily opens the Calligraphy.sip + BlueprintRegistry. Returns true if
    // either was already open or just opened successfully. False means the
    // archive path couldn't be resolved (Settings → Game Install not configured).
    public bool TryOpenArchive(string? sipPath)
    {
        if (_archive is not null) return true;
        if (string.IsNullOrWhiteSpace(sipPath) || !File.Exists(sipPath)) return false;
        lock (_lock)
        {
            if (_archive is not null) return true;
            try
            {
                _calligraphyArchivePath = sipPath;
                _archive = new KapgArchiveReader(sipPath);
                _registry = BlueprintRegistry.Load(_archive);

                // PowerCatalog leaves PowerUnrealClassName empty unless we
                // hand it the asset-ID → class-name table. Load both type
                // directories + Prototype.directory so the skill list shows
                // real class names and so ResolveSkillVfxAsync can find the
                // per-power UPK from the class name.
                try { _protoDirectory = PrototypeDirectoryReader.LoadFromArchive(_archive); }
                catch { _protoDirectory = null; }
                try { _powerUnrealClasses = TypeDirectoryReader.LoadFromArchive(_archive, "Calligraphy/Powers/Types/PowerUnrealClass.type"); }
                catch { _powerUnrealClasses = null; }
                try { _powerIconPaths = TypeDirectoryReader.LoadFromArchive(_archive, "Calligraphy/Powers/Types/PowerIconPathType.type"); }
                catch { _powerIconPaths = null; }

                // Localized display names. Loco lives next to Calligraphy.sip
                // under Data/Game/Loco. English bucket only for now.
                try
                {
                    string? sipDir = Path.GetDirectoryName(sipPath);
                    string locoDir = sipDir is null ? string.Empty : Path.Combine(sipDir, "Loco");
                    if (Directory.Exists(locoDir))
                        _loco = LocoIndex.Open(locoDir, "eng");
                }
                catch { _loco = null; }

                return true;
            }
            catch
            {
                _archive?.Dispose();
                _archive = null;
                _packageGraph = null;
                _registry = null;
                _calligraphyArchivePath = null;
                return false;
            }
        }
    }

    // Enumerates every hero UPK in <cookedDir>/UC__MarvelPlayer_*_SF.upk and
    // splits the filename into (hero, variant) tokens. Returns one HeroSummary
    // per UPK — base costume gets an empty Variant, alts get a non-empty one.
    // The display name is friendly-cased ("FooBar" -> "Foo Bar").
    public Task<IReadOnlyList<HeroSummary>> EnumerateHeroesAsync(string cookedDir)
        => EnumerateByPrefixAsync(cookedDir, "UC__MarvelPlayer_");

    // Team-up companions follow the same UC__<prefix>_<token>[_<variant>]_SF.upk
    // pattern, just with a different package prefix. Reusing the splitter keeps
    // token/variant semantics identical for portrait lookups and tree grouping.
    public Task<IReadOnlyList<HeroSummary>> EnumerateTeamUpsAsync(string cookedDir)
        => EnumerateByPrefixAsync(cookedDir, "UC__MarvelTeamUp_");

    private Task<IReadOnlyList<HeroSummary>> EnumerateByPrefixAsync(string cookedDir, string upkPrefix)
    {
        return Task.Run<IReadOnlyList<HeroSummary>>(() =>
        {
            if (string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir))
                return Array.Empty<HeroSummary>();

            var rows = new List<HeroSummary>();
            foreach (string p in Directory.GetFiles(cookedDir, upkPrefix + "*_SF.upk"))
            {
                string n = Path.GetFileNameWithoutExtension(p)
                    .Replace(upkPrefix, "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_SF", "", StringComparison.OrdinalIgnoreCase);
                int us = n.IndexOf('_');
                string rawToken = us < 0 ? n : n.Substring(0, us);
                string rawVariant = us < 0 ? string.Empty : n.Substring(us + 1);
                string token = rawToken.ToLowerInvariant();
                string variant = rawVariant.ToLowerInvariant();

                string display = Prettify(token);
                if (!string.IsNullOrEmpty(variant))
                    display += " — " + Prettify(variant);

                rows.Add(new HeroSummary(token, variant, display, p, rawToken, rawVariant));
            }
            return (IReadOnlyList<HeroSummary>)rows
                .OrderBy(h => h.Token, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Variant, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    // Returns the visible skill set for a hero (those carrying both a
    // DisplayName and an IconPath — the in-game Powers UI set). Results are
    // cached per-token. Returns an empty list if the archive isn't open or the
    // hero has no avatar prototype.
    public Task<IReadOnlyList<PowerEntry>> GetSkillsAsync(string heroToken)
    {
        return Task.Run<IReadOnlyList<PowerEntry>>(() =>
        {
            if (string.IsNullOrWhiteSpace(heroToken) || _archive is null || _registry is null)
                return Array.Empty<PowerEntry>();

            lock (_lock)
            {
                if (_skillCache.TryGetValue(heroToken, out var cached))
                    return cached;
            }

            // PowerCatalog expects a properly-cased character token to match the
            // avatar prototype file. The convention is PascalCase ("Hero",
            // "FooBar"). Heroes UPK names already use that casing minus the
            // lowercasing we did for lookup — re-pascal-case the token.
            string pascal = PascalCase(heroToken);
            List<PowerEntry> powers;
            try
            {
                powers = PowerCatalog.LoadSkillTreeForCharacter(_archive, _registry, pascal);
            }
            catch
            {
                powers = new List<PowerEntry>();
            }

            // Resolve PowerUnrealClassName (needed for VFX resolution), Loco-
            // localized DisplayName, and IconAssetPath. Without this the
            // PowerVfxResolver call later can't open any per-power UPK because
            // it'd be looking for UC__<empty>_SF.upk.
            try
            {
                PowerCatalog.ResolveDisplayNamesAndIcons(
                    powers,
                    _loco,
                    _protoDirectory,
                    _powerUnrealClasses,
                    _powerIconPaths);
            }
            catch { /* swallow — visible names just stay heuristic */ }

            // Visible skills only — passives, buffs, mechanic helpers etc.
            // would dilute the picker. The PowerCatalog flag IsVisibleSkill is
            // already computed (DisplayName + IconPath both present).
            var filtered = powers.Where(p => p.IsVisibleSkill).ToList();

            lock (_lock) { _skillCache[heroToken] = filtered; }
            return (IReadOnlyList<PowerEntry>)filtered;
        });
    }

    // Resolves a skill's PowerFX bindings (ParticleSystem refs) by class name.
    // Returns null when the per-power UPK doesn't exist or has no bindings.
    public Task<PowerVfxResolver.ResolvedVfx?> ResolveSkillVfxAsync(PowerEntry power, string cookedDir)
    {
        if (power is null || string.IsNullOrWhiteSpace(power.PowerUnrealClassName))
            return Task.FromResult<PowerVfxResolver.ResolvedVfx?>(null);
        return PowerVfxResolver.ResolveAsync(power.PowerUnrealClassName, cookedDir, _repo);
    }

    // Walks every ParticleSystem binding for the given resolved VFX, follows
    // its emitter modules to find UMaterialInterface references, and returns
    // the deduped set of (material export path, source UPK path) tuples.
    // The source UPK is whichever per-power UPK the resolver pulled the
    // particle out of — that's where the material's home UPK actually lives.
    public Task<IReadOnlyList<SkillMaterialRef>> CollectParticleMaterialsAsync(PowerVfxResolver.ResolvedVfx vfx)
    {
        return Task.Run<IReadOnlyList<SkillMaterialRef>>(() =>
        {
            if (vfx is null) return Array.Empty<SkillMaterialRef>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refs = new List<SkillMaterialRef>();

            foreach (var binding in vfx.Bindings)
            {
                string sourceUpk = binding.SourceUpkFullPath ?? string.Empty;
                if (string.IsNullOrEmpty(sourceUpk)) continue;

                // Particle systems → emitters → materials.
                UParticleSystem? ps = binding.ResolvedParticleSystem;
                if (ps is not null)
                {
                    foreach (var (matName, isCrossPkg) in WalkParticleMaterialRefs(ps))
                    {
                        string key = sourceUpk + "|" + matName;
                        if (seen.Add(key))
                            refs.Add(new SkillMaterialRef(matName, sourceUpk, isCrossPkg));
                    }
                }

                // PowerFXDecal / EntityFXDecal: surface the decal material (often
                // a MaterialInstanceTimeVarying) so its vector params get pulled
                // into the recolor's _materials list and patched alongside MICs.
                // Without this the ground scorch / fadeout decals stay un-recolored.
                if (!string.IsNullOrEmpty(binding.DecalMaterialRef))
                {
                    string key = sourceUpk + "|" + binding.DecalMaterialRef;
                    if (seen.Add(key))
                        refs.Add(new SkillMaterialRef(binding.DecalMaterialRef!, sourceUpk, IsCrossPackage: false));
                }
            }

            return (IReadOnlyList<SkillMaterialRef>)refs;
        });
    }

    // Per-emitter color-source probe (read-only). For every emitter, reports the
    // material it uses (and whether that material is SHARED / cross-UPK) AND
    // whether the emitter carries a LOCAL particle color module
    // (Color/ColorOverLife/ColorScaleOverLife) with its constant value. This is
    // the decisive signal for "why is the tint not changing":
    //   - HasLocalColor=true  → the color is the particle module (local, already
    //     recolorable); if it persists, the recolor is skipping this emitter.
    //   - HasLocalColor=false → the emitter has no local color; the tint must be
    //     baked into the (often shared) material's compiled resource — needs the
    //     clone + material-body recolor, not the particle path.
    public sealed record EmitterColorProbe(
        string Emitter,
        string MaterialName,
        bool MaterialIsShared,
        bool HasLocalColor,
        string ColorModule,    // e.g. "ColorOverLife (0.20, 0.45, 1.00)" or ""
        string HostUpk,        // file that actually hosts the color module (where a recolor must write)
        bool BlueDominant,     // current color's B channel dominates → likely still blue
        string ParticleSystem, // PS name (so we can identify the lingering effect)
        float ParticleLifetime,// seconds — high values = persistent / lingering particles
        bool EmitterLoops);    // EmitterDuration == 0 → emitter loops forever

    public Task<IReadOnlyList<EmitterColorProbe>> ProbeEmitterColorSourcesAsync(PowerVfxResolver.ResolvedVfx vfx)
    {
        return Task.Run<IReadOnlyList<EmitterColorProbe>>(() =>
        {
            var result = new List<EmitterColorProbe>();
            if (vfx is null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var binding in vfx.Bindings)
            {
                UParticleSystem? ps = binding.ResolvedParticleSystem;
                if (ps?.Emitters is null) continue;
                string bUpk = binding.SourceUpkFullPath ?? string.Empty;
                string psName = binding.ParticleSystemRef ?? string.Empty;

                foreach (var emitterRef in ps.Emitters)
                {
                    if (emitterRef?.LoadObject<UObject>() is not UParticleEmitter emitter) continue;
                    string emitterName = emitter.GetType().GetProperty("EmitterName")?.GetValue(emitter)?.ToString() ?? "emitter";

                    FObject? firstLodRef = null;
                    if (emitter.LODLevels is not null)
                        foreach (FObject r in emitter.LODLevels) { firstLodRef = r; break; }
                    if (firstLodRef?.LoadObject<UObject>() is not UParticleLODLevel lod) continue;

                    // Material on this emitter + emitter duration (0 = loops forever).
                    string matName = string.Empty;
                    bool matShared = false;
                    bool emitterLoops = false;
                    if (lod.RequiredModule?.LoadObject<UObject>() is UParticleModuleRequired req)
                    {
                        if (req.Material is FObject matRef)
                        {
                            matName = matRef.Name ?? string.Empty;
                            matShared = matRef.TableEntry is UnrealImportTableEntry;
                        }
                        emitterLoops = req.EmitterDuration == 0f; // 0 → forever-loop
                    }

                    // Lifetime (per-particle): read from the first ParticleModuleLifetime
                    // on this LOD. Long lifetime + looping emitter = the lingering visual.
                    float lifetime = 0f;
                    if (lod.Modules is not null)
                    {
                        foreach (FObject mr in lod.Modules)
                        {
                            UObject? mo;
                            try { mo = mr.LoadObject<UObject>(); }
                            catch { continue; }
                            if (mo is UParticleModuleLifetime pml && pml.Lifetime?.LookupTable is { Count: > 0 } lk)
                            {
                                // Lookup table format: 2 header floats + samples; take the max sample.
                                int count = lk.Count;
                                int start = (count >= 5 && (count - 2) % 3 == 0) ? 2 : (count % 4 == 0 ? 1 : 0);
                                float max = 0f;
                                for (int k = start; k < count; k++) if (lk[k] > max) max = lk[k];
                                lifetime = max;
                                break;
                            }
                        }
                    }

                    // Local color module (if any) + the file that hosts it.
                    bool hasLocal = false;
                    string colorModule = string.Empty;
                    string hostUpk = bUpk;
                    bool blueDominant = false;
                    if (lod.Modules is not null)
                    {
                        foreach (FObject moduleRef in lod.Modules)
                        {
                            UObject? mo;
                            try { mo = moduleRef.LoadObject<UObject>(); }
                            catch { continue; }
                            FRawDistributionVector? raw = null;
                            string kind = string.Empty;
                            switch (mo)
                            {
                                case UParticleModuleColor c: raw = c.StartColor; kind = "StartColor"; break;
                                case UParticleModuleColorOverLife col: raw = col.ColorOverLife; kind = "ColorOverLife"; break;
                                case UParticleModuleColorScaleOverLife:
                                    raw = mo.GetType().GetProperty("ColorScaleOverLife")?.GetValue(mo) as FRawDistributionVector;
                                    kind = "ColorScaleOverLife";
                                    break;
                            }
                            if (raw is null) continue;
                            var col4 = ReadConstColor(raw);
                            colorModule = $"{kind} ({col4.X:0.00}, {col4.Y:0.00}, {col4.Z:0.00})";
                            hostUpk = ResolveModuleUpkPath(moduleRef, bUpk);
                            blueDominant = col4.Z > col4.X && col4.Z > col4.Y && col4.Z > 0.001f;
                            hasLocal = true;
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(matName)) continue;
                    string dedupe = emitterName + "|" + matName + "|" + colorModule + "|" + hostUpk;
                    if (!seen.Add(dedupe)) continue;
                    result.Add(new EmitterColorProbe(emitterName, matName, matShared, hasLocal, colorModule,
                        System.IO.Path.GetFileName(hostUpk), blueDominant,
                        psName, lifetime, emitterLoops));
                }
            }
            return (IReadOnlyList<EmitterColorProbe>)result;
        });
    }

    // Reads the first constant color out of a particle FRawDistributionVector's
    // baked LookupTable, using the SAME stride detection as the collector.
    private static Vector4 ReadConstColor(FRawDistributionVector raw)
    {
        Vector4 color = new(1f, 1f, 1f, 1f);
        if (raw.LookupTable is { Count: > 0 } lookup)
        {
            int count = lookup.Count;
            if (count >= 5 && (count - 2) % 3 == 0) color = new Vector4(lookup[2], lookup[3], lookup[4], 1f);
            else if (count % 4 == 0 && count >= 4) color = new Vector4(lookup[1], lookup[2], lookup[3], 1f);
            else if (count >= 3) color = new Vector4(lookup[0], lookup[1], lookup[2], 1f);
        }
        return color;
    }

    // DIAGNOSTIC (read-only): dump a power's full Calligraphy prototype field
    // graph (member names + values, recursing into nested prototypes, resolving
    // asset-id values to readable paths). Used to FIND which field references the
    // condition/secondary effect a power applies on hit — the source of VFX (e.g.
    // secondary FX) that lives outside the power's own PowerFX components.
    /// <summary>
    /// The packages a power draws from, as its own prototype says.
    /// </summary>
    /// <remarks>
    /// Returns nothing when the archive is closed or the power has no
    /// prototype, so a caller can fall back to what it did before rather than
    /// showing an empty panel.
    /// </remarks>
    public Task<IReadOnlyList<PowerPackageRef>> PackagesForPowerAsync(PowerEntry power)
    {
        return Task.Run<IReadOnlyList<PowerPackageRef>>(() =>
        {
            if (_archive is null || _protoDirectory is null) return Array.Empty<PowerPackageRef>();
            if (power is null || string.IsNullOrEmpty(power.PrototypePath)) return Array.Empty<PowerPackageRef>();

            try
            {
                _packageGraph ??= new PowerPackageGraph(_archive, _protoDirectory);
                return _packageGraph.Walk(power.PrototypePath);
            }
            catch (Exception)
            {
                return Array.Empty<PowerPackageRef>();
            }
        });
    }

    public Task<List<string>> DumpPowerPrototypeAsync(PowerEntry power)
    {
        return Task.Run(() =>
        {
            var lines = new List<string>();
            if (_archive is null || _registry is null) { lines.Add("(archive not open)"); return lines; }
            if (power is null || string.IsNullOrEmpty(power.PrototypePath)) { lines.Add("(no prototype path)"); return lines; }
            if (!_archive.TryFindByName(power.PrototypePath, out var entry)) { lines.Add($"(prototype not found: {power.PrototypePath})"); return lines; }
            byte[] data;
            try { data = _archive.ExtractEntry(entry); }
            catch (Exception e) { lines.Add($"(extract failed: {e.Message})"); return lines; }
            PrototypeBody body;
            try
            {
                var parser = new PrototypeParser(data);
                parser.TryParse(out _);
                body = parser.Result;
            }
            catch (Exception e) { lines.Add($"(parse failed: {e.Message})"); return lines; }
            lines.Add($"PROTO {power.PrototypePath}");
            DumpPrototypeBodyWithParents(body, 1, lines, 0);
            return lines;
        });
    }

    // Walk the prototype AND its parent chain (Calligraphy uses heavy
    // inheritance — a thin power prototype often carries no fields of its own;
    // the real data, including applied-condition references, lives in a parent).
    private void DumpPrototypeBodyWithParents(PrototypeBody body, int depth, List<string> lines, int parentHops)
    {
        string ind = new string(' ', depth * 2);
        lines.Add($"{ind}[meta] flags={body.Flags} groups={body.Groups.Count} partialF2={body.IsPartialF2Variant} parentId={(body.ParentPrototypeId?.ToString("X") ?? "none")}");
        DumpPrototypeBody(body, depth, lines, 0);

        if (parentHops >= 8) { lines.Add($"{ind}...(parent chain truncated)"); return; }
        if (body.ParentPrototypeId is not ulong pid || pid == 0) return;
        if (_protoDirectory is null || _archive is null) return;
        if (!_protoDirectory.IdToPath.TryGetValue(pid, out var parentPath)) { lines.Add($"{ind}parent id {pid:X} not in directory"); return; }
        if (!_archive.TryFindByName(parentPath, out var pe)) { lines.Add($"{ind}parent {parentPath} not in archive"); return; }
        byte[] pdata;
        try { pdata = _archive.ExtractEntry(pe); } catch { return; }
        PrototypeBody pbody;
        try
        {
            var pparser = new PrototypeParser(pdata);
            pparser.TryParse(out _);
            pbody = pparser.Result;
        }
        catch { return; }
        lines.Add($"{ind}PARENT => {parentPath}");
        DumpPrototypeBodyWithParents(pbody, depth + 1, lines, parentHops + 1);
    }

    private void DumpPrototypeBody(PrototypeBody body, int depth, List<string> lines, int guard)
    {
        string ind = new string(' ', depth * 2);
        if (guard > 6) { lines.Add(ind + "...(max depth)"); return; }
        if (body.ParentPrototypeId is ulong pid && pid != 0 && _protoDirectory is not null
            && _protoDirectory.TryGetReadableName(pid, out var pn))
            lines.Add($"{ind}parent => {pn}");
        foreach (var group in body.Groups)
        {
            foreach (var f in group.SimpleFields.Concat(group.ListFields))
            {
                string name = _registry!.TryGetMemberById(f.FieldId, out var m) && !string.IsNullOrEmpty(m.Name)
                    ? m.Name : $"field#{f.FieldId:X}";
                if (f.Values.Count == 0) { lines.Add($"{ind}{name} [{f.TypeCode}{(f.IsList ? "[]" : "")}] = (empty)"); continue; }
                foreach (var v in f.Values)
                {
                    if (v is PrototypeBody nb)
                    {
                        lines.Add($"{ind}{name} [{f.TypeCode}] => {{");
                        DumpPrototypeBody(nb, depth + 1, lines, guard + 1);
                        lines.Add($"{ind}}}");
                    }
                    else
                    {
                        string vs = v?.ToString() ?? "null";
                        string resolved = string.Empty;
                        if (v is ulong uid && uid != 0 && _protoDirectory is not null
                            && _protoDirectory.IdToPath.TryGetValue(uid, out var path))
                            resolved = $"   -> {path}";
                        lines.Add($"{ind}{name} [{f.TypeCode}{(f.IsList ? "[]" : "")}] = {vs}{resolved}");
                    }
                }
            }
        }
    }

    // DIAGNOSTIC (read-only): dump the full particle binding tree for a skill —
    // every ParticleSystem → emitter (type) → RequiredModule.Material (name,
    // local/import, resolved host UPK) → the module classes present. Used to
    // find beam/material-only emitters (e.g. secondary FX) whose color comes
    // from the material, not a Color module, so they don't show as color slots.
    public Task<List<string>> DumpVfxMaterialTreeAsync(PowerVfxResolver.ResolvedVfx vfx)
    {
        return Task.Run(async () =>
        {
            var lines = new List<string>();
            if (vfx is null) { lines.Add("(no vfx)"); return lines; }
            var upkPaths = vfx.Bindings
                .Where(b => !string.IsNullOrEmpty(b.SourceUpkFullPath))
                .Select(b => b.SourceUpkFullPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string upkPath in upkPaths)
            {
                lines.Add($"UPK {System.IO.Path.GetFileName(upkPath)}");
                UpkManager.Models.UpkFile.UnrealHeader header;
                try
                {
                    header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                    await header.ReadHeaderAsync(null).ConfigureAwait(false);
                }
                catch (Exception e) { lines.Add($"  load error: {e.Message}"); continue; }

                foreach (var export in header.ExportTable)
                {
                    string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                    if (cls != "particlesystem") continue;
                    try { if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false); }
                    catch { continue; }
                    if (export.UnrealObject is not IUnrealObject iuo || iuo.UObject is not UParticleSystem ps || ps.Emitters is null) continue;
                    lines.Add($"  PS {export.ObjectNameIndex?.Name}");
                    foreach (var emitterRef in ps.Emitters)
                    {
                        if (emitterRef?.LoadObject<UObject>() is not UParticleEmitter emitter) continue;
                        string emName = emitter.GetType().GetProperty("EmitterName")?.GetValue(emitter)?.ToString() ?? "?";
                        string emType = emitter.GetType().Name;
                        if (emitter.LODLevels is null) { lines.Add($"    EM {emName} ({emType}) [no LODs]"); continue; }
                        FObject? firstLod = null;
                        foreach (FObject r in emitter.LODLevels) { firstLod = r; break; }
                        if (firstLod?.LoadObject<UObject>() is not UParticleLODLevel lod) { lines.Add($"    EM {emName} ({emType}) [no LOD0]"); continue; }

                        string matInfo = "no-material";
                        FObject? reqRef = lod.RequiredModule;
                        if (reqRef?.LoadObject<UObject>() is UParticleModuleRequired req && req.Material is FObject matRef)
                        {
                            string kind = matRef.TableEntry is UnrealImportTableEntry ? "IMPORT" : "local";
                            string host = matRef.TableEntry is UnrealImportTableEntry imp
                                ? System.IO.Path.GetFileName(ResolveImportUpkPath(header, imp, upkPath))
                                : System.IO.Path.GetFileName(upkPath);
                            matInfo = $"mat='{matRef.Name}' {kind} host={host}";
                        }
                        var modClasses = new List<string>();
                        if (lod.Modules is not null)
                            foreach (FObject mref in lod.Modules)
                            {
                                try { if (mref?.LoadObject<UObject>() is UObject mo) modClasses.Add(mo.GetType().Name.Replace("UParticleModule", "")); }
                                catch { }
                            }
                        lines.Add($"    EM {emName} ({emType}) | {matInfo} | mods=[{string.Join(",", modClasses)}]");
                    }
                }
            }
            return lines;
        });
    }

    // PHASE 2 — surfaces every visible color slot for a resolved skill, across
    // all four sources documented in MHO_HeroSkill_Color_DeepDive.md:
    //
    //   1. MaterialInstanceConstant.VectorParameterValues
    //   2. Material.Expressions -> MaterialExpressionVectorParameter.DefaultValue
    //   3. ParticleModuleColor.StartColor
    //   4. ParticleModuleColorOverLife.ColorOverLife
    //
    // For each ParticleSystem the resolver gave us, we walk the host UPK once,
    // scanning every Material / MaterialInstanceConstant / ParticleSystem export
    // and emitting one SkillColorEntry per editable slot. Cross-UPK shared
    // materials are NOT followed yet — that's Phase 3.
    public Task<IReadOnlyList<SkillColorEntry>> CollectSkillColorsAsync(PowerVfxResolver.ResolvedVfx vfx)
    {
        return Task.Run<IReadOnlyList<SkillColorEntry>>(async () =>
        {
            if (vfx is null) return Array.Empty<SkillColorEntry>();

            // Group the resolver's bindings by source UPK so we open each UPK
            // only once even when a skill has many bindings into the same UPK.
            var upkPaths = vfx.Bindings
                .Where(b => !string.IsNullOrEmpty(b.SourceUpkFullPath))
                .Select(b => b.SourceUpkFullPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entries = new List<SkillColorEntry>();
            // Cross-UPK shared materials (Phase 3): particle RequiredModule.Material
            // refs that are Imports point at a material living in another UPK. We
            // record (foreignUpk, materialName) here and, after the local scan,
            // open each foreign UPK to collect ONLY that material's color
            // expressions — flagged IsCrossPackage so the writer scopes precisely.
            var crossMatRefs = new List<(string upk, string mat)>();
            var scannedUpks = new HashSet<string>(upkPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string upkPath in upkPaths)
            {
                try
                {
                    var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                    await header.ReadHeaderAsync(null).ConfigureAwait(false);
                    foreach (var export in header.ExportTable)
                    {
                        string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                        try
                        {
                            // Parse on-demand. Most skill UPKs are small — we
                            // tolerate a fair bit of parse work here because the
                            // alternative is to ask the user to figure this out
                            // by hand.
                            if (export.UnrealObject is null)
                                await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                        }
                        catch { continue; }

                        if (export.UnrealObject is not IUnrealObject iuo) continue;
                        UObject? obj = iuo.UObject as UObject;
                        if (obj is null) continue;

                        switch (cls)
                        {
                            case "materialinstanceconstant":
                            // MITV (animated decals / scorch / fade materials, 1,038 instances
                            // in cooked content) shares MIC's parameter schema — treat as MIC
                            // so its vector params surface for recolor. Otherwise every
                            // "ground flame" / "scorch fadeout" decal stays unmodified app-wide.
                            case "materialinstancetimevarying":
                                CollectFromMic(obj, export, upkPath, entries);
                                break;
                            case "material":
                                CollectFromMaterial(obj, export, upkPath, entries, header);
                                break;
                            case "particlesystem":
                                CollectFromParticleSystem(obj, export, upkPath, entries);
                                if (obj is UParticleSystem psForMats)
                                    CollectCrossPackageMaterialRefs(psForMats, upkPath, header, crossMatRefs);
                                break;
                        }
                    }
                }
                catch { /* skip this UPK on error; user will see fewer entries but no crash */ }
            }

            // Pass 1b: walk every RESOLVED particle system directly. The export
            // scan above only finds particle systems that are EXPORTS in the
            // scanned UPKs — but cooked per-power UPKs frequently IMPORT their
            // particle systems from vfx_<hero>.upk, so those systems' LOCAL
            // particle color modules (ColorOverLife / StartColor) were never
            // collected and therefore never recolored (the "blue/white won't
            // change" bug). Collect them here, flagged cross-package so the apply
            // pipeline scopes the patch to EXACTLY these module exports (not the
            // whole shared vfx_<hero>.upk).
            {
                var existingModulePaths = new HashSet<string>(
                    entries.Where(en => !string.IsNullOrEmpty(en.ExportPath)).Select(en => en.ExportPath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var binding in vfx.Bindings)
                {
                    if (binding.ResolvedParticleSystem is not UParticleSystem rps) continue;
                    string bUpk = binding.SourceUpkFullPath ?? string.Empty;
                    var tmp = new List<SkillColorEntry>();
                    try { CollectFromParticleSystemCore(rps, string.Empty, bUpk, tmp, markCrossPackage: true); }
                    catch { continue; }
                    foreach (var en in tmp)
                        if (string.IsNullOrEmpty(en.ExportPath) || existingModulePaths.Add(en.ExportPath))
                            entries.Add(en);
                }
            }

            // Second pass: follow cross-UPK material refs into their host UPK and
            // collect ONLY the referenced material's color expressions. Skip refs
            // whose host UPK was already scanned locally (those materials are
            // already covered) and dedupe by (upk, material).
            var distinctCross = crossMatRefs
                .Where(r => !string.IsNullOrEmpty(r.upk) && !scannedUpks.Contains(r.upk))
                .Select(r => (r.upk, r.mat))
                .Distinct()
                .ToList();
            foreach (var (foreignUpk, matName) in distinctCross)
            {
                if (string.IsNullOrEmpty(matName)) continue;
                try
                {
                    var fh = await _repo.LoadUpkFile(foreignUpk).ConfigureAwait(false);
                    await fh.ReadHeaderAsync(null).ConfigureAwait(false);
                    foreach (var export in fh.ExportTable)
                    {
                        // Only the one named UMaterial — never blanket-scan a shared
                        // library. (MaterialInstanceConstant cross-package edits are
                        // a separate, rarer path and intentionally not followed here.)
                        if (!string.Equals(export.ClassReferenceNameIndex?.Name, "Material", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.Equals(export.ObjectNameIndex?.Name, matName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            if (export.UnrealObject is null)
                                await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                        }
                        catch { continue; }
                        if (export.UnrealObject is not IUnrealObject iuo) continue;
                        if (iuo.UObject is not UObject obj) continue;
                        CollectFromMaterial(obj, export, foreignUpk, entries, fh, isCrossPackage: true);
                    }
                }
                catch { /* foreign UPK unreadable — skip; user just won't see those slots */ }
            }

            return (IReadOnlyList<SkillColorEntry>)entries;
        });
    }

    // Walks a ParticleSystem's emitters for RequiredModule.Material refs that are
    // IMPORTS (the material lives in another UPK) and records (foreignUpk, name).
    private static void CollectCrossPackageMaterialRefs(UParticleSystem ps, string hostUpkPath, UpkManager.Models.UpkFile.UnrealHeader header, List<(string upk, string mat)> sink)
    {
        if (ps.Emitters is null) return;
        foreach (var emitterRef in ps.Emitters)
        {
            if (emitterRef?.LoadObject<UObject>() is not UParticleEmitter emitter) continue;
            if (emitter.LODLevels is null) continue;
            foreach (FObject lodRef in emitter.LODLevels)
            {
                if (lodRef?.LoadObject<UObject>() is not UParticleLODLevel lod) continue;
                FObject? reqRef = lod.RequiredModule;
                if (reqRef?.LoadObject<UObject>() is not UParticleModuleRequired req) continue;
                FObject? matRef = req.Material;
                if (matRef is null) continue;
                // Only cross-package: a local material is already collected by the
                // main "material" export scan.
                if (matRef.TableEntry is not UnrealImportTableEntry imp) continue;
                string name = matRef.Name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;
                string foreignUpk = ResolveImportUpkPath(header, imp, hostUpkPath);
                if (string.Equals(foreignUpk, hostUpkPath, StringComparison.OrdinalIgnoreCase)) continue;
                sink.Add((foreignUpk, name));
            }
        }
    }

    // Resolves the .upk that hosts an IMPORT by walking its OuterReference chain
    // up to the top-level package import (whose ObjectName is the package name),
    // then looking for <package>.upk beside the host UPK. Import GetPathName()
    // returns only the leaf name, so the path-split heuristic used for modules
    // doesn't work here — we must consult the header's object table.
    private static string ResolveImportUpkPath(UpkManager.Models.UpkFile.UnrealHeader header, UnrealImportTableEntry import, string hostUpkPath)
    {
        UnrealImportTableEntry node = import;
        int guard = 0;
        while (guard++ < 32)
        {
            var outer = header.GetObjectTableEntry(node.OuterReference);
            if (outer is UnrealImportTableEntry oi) node = oi;
            else break;   // reached the top-level package import (outer is null/an export)
        }
        string pkg = node.ObjectNameIndex?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pkg)) return hostUpkPath;

        // Same-name → local (defensive).
        string hostPkg = System.IO.Path.GetFileNameWithoutExtension(hostUpkPath);
        if (string.Equals(pkg, hostPkg, StringComparison.OrdinalIgnoreCase)) return hostUpkPath;

        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(hostUpkPath)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) return hostUpkPath;
        string foreign = System.IO.Path.Combine(dir, pkg + ".upk");
        return System.IO.File.Exists(foreign) ? foreign : hostUpkPath;
    }

    private static void CollectFromMic(UObject obj, UnrealExportTableEntry export, string upkPath, List<SkillColorEntry> sink)
    {
        // The MaterialInstanceConstant export's VectorParameterValues array.
        // We use reflection here because UMaterialInstanceConstant lives in the
        // UpkManager namespace and the property layout there exposes the array
        // as a strongly-typed UArray<FScaledVectorParameterValue>-shaped struct.
        var t = obj.GetType();
        var vpProp = t.GetProperty("VectorParameterValues");
        if (vpProp is null) return;
        if (vpProp.GetValue(obj) is not System.Collections.IEnumerable list) return;
        string ownerLabel = "Material Instance: " + (export.ObjectNameIndex?.Name ?? string.Empty);
        foreach (var entry in list)
        {
            var nameP = entry.GetType().GetProperty("ParameterName");
            var valueP = entry.GetType().GetProperty("ParameterValue");
            string? paramName = (nameP?.GetValue(entry) as FName)?.Name ?? nameP?.GetValue(entry)?.ToString();
            if (string.IsNullOrEmpty(paramName)) continue;
            Vector4 color = ExtractFLinearColor(valueP?.GetValue(entry));
            sink.Add(new SkillColorEntry(
                Kind: SkillColorKind.MicVectorParam,
                ParameterName: paramName!,
                OwnerLabel: ownerLabel,
                SourceUpkPath: upkPath,
                CurrentColor: color,
                Shape: DistributionShape.NotApplicable,
                Editable: true,
                ExportPath: export.GetPathName()));
        }
    }

    private static void CollectFromMaterial(UObject obj, UnrealExportTableEntry export, string upkPath, List<SkillColorEntry> sink, UpkManager.Models.UpkFile.UnrealHeader header, bool isCrossPackage = false)
    {
        // The Material's Expressions[] is an array of FObject refs to its
        // sub-expressions. Each one is its own export — we load it and check
        // for any color-bearing expression type the writer can patch.
        var t = obj.GetType();
        var exprsProp = t.GetProperty("Expressions");
        if (exprsProp is null) return;
        if (exprsProp.GetValue(obj) is not System.Collections.IEnumerable expressions) return;
        string materialName = export.ObjectNameIndex?.Name ?? string.Empty;
        string ownerLabel = "Material: " + materialName;
        foreach (var exprRef in expressions)
        {
            if (exprRef is not FObject fo) continue;
            UObject? exprObj;
            try { exprObj = fo.LoadObject<UObject>(); }
            catch { continue; }
            if (exprObj is null) continue;

            switch (exprObj)
            {
                case UMaterialExpressionVectorParameter vp:
                {
                    string paramName = vp.ParameterName?.Name ?? "Color";
                    FLinearColor? c = vp.DefaultValue;
                    Vector4 color = c is null
                        ? new Vector4(1f, 1f, 1f, 1f)
                        : new Vector4(c.R, c.G, c.B, c.A);
                    sink.Add(new SkillColorEntry(
                        Kind: SkillColorKind.MaterialExpressionVector,
                        ParameterName: paramName,
                        OwnerLabel: ownerLabel,
                        SourceUpkPath: upkPath,
                        CurrentColor: color,
                        Shape: DistributionShape.NotApplicable,
                        Editable: true,
                        ExportPath: fo.GetPathName(),
                        IsCrossPackage: isCrossPackage));
                    break;
                }
                case UMaterialExpressionConstant3Vector c3:
                {
                    sink.Add(new SkillColorEntry(
                        Kind: SkillColorKind.MaterialExpressionConstant3Vector,
                        ParameterName: "Constant3Vector",
                        OwnerLabel: ownerLabel,
                        SourceUpkPath: upkPath,
                        CurrentColor: new Vector4(c3.R, c3.G, c3.B, 1f),
                        Shape: DistributionShape.NotApplicable,
                        Editable: true,
                        ExportPath: fo.GetPathName(),
                        IsCrossPackage: isCrossPackage));
                    break;
                }
                case UMaterialExpressionConstant4Vector c4:
                {
                    sink.Add(new SkillColorEntry(
                        Kind: SkillColorKind.MaterialExpressionConstant4Vector,
                        ParameterName: "Constant4Vector",
                        OwnerLabel: ownerLabel,
                        SourceUpkPath: upkPath,
                        CurrentColor: new Vector4(c4.R, c4.G, c4.B, c4.A),
                        Shape: DistributionShape.NotApplicable,
                        Editable: true,
                        ExportPath: fo.GetPathName(),
                        IsCrossPackage: isCrossPackage));
                    break;
                }
            }
        }
    }

    // Collect every recolorable color slot of a SINGLE named object (material,
    // MIC, or particle system) inside an arbitrary host UPK. Powers the Skill
    // Recolor "Find effect by name" feature — the user searched the reference
    // index, picked an effect, and we open its host UPK to surface its colors.
    // All entries are flagged IsCrossPackage so the writer scopes precisely via
    // the includedExportPaths allowlist (never blanket-recoloring the host).
    public Task<IReadOnlyList<SkillColorEntry>> CollectColorsFromObjectAsync(string upkPath, string objectLeaf)
    {
        return Task.Run<IReadOnlyList<SkillColorEntry>>(async () =>
        {
            var entries = new List<SkillColorEntry>();
            if (string.IsNullOrWhiteSpace(upkPath) || string.IsNullOrWhiteSpace(objectLeaf) || !System.IO.File.Exists(upkPath))
                return entries;
            try
            {
                var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);
                foreach (var export in header.ExportTable)
                {
                    if (!string.Equals(export.ObjectNameIndex?.Name, objectLeaf, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                    if (cls is not ("material" or "materialinstanceconstant" or "materialinstancetimevarying" or "particlesystem")) continue;
                    try { if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false); }
                    catch { continue; }
                    if (export.UnrealObject is not IUnrealObject iuo || iuo.UObject is not UObject obj) continue;

                    int before = entries.Count;
                    switch (cls)
                    {
                        case "materialinstanceconstant":
                        case "materialinstancetimevarying": // MITV uses MIC param schema; same collector
                            CollectFromMic(obj, export, upkPath, entries); break;
                        case "material": CollectFromMaterial(obj, export, upkPath, entries, header, isCrossPackage: true); break;
                        case "particlesystem": CollectFromParticleSystem(obj, export, upkPath, entries); break;
                    }
                    // Flag particle/MIC entries as cross-package too (CollectFromMaterial
                    // already does). Rewrite the just-added entries with IsCrossPackage=true.
                    for (int i = before; i < entries.Count; i++)
                        if (!entries[i].IsCrossPackage)
                            entries[i] = entries[i] with { IsCrossPackage = true };
                }
            }
            catch { }
            return entries;
        });
    }

    // Collect EVERY recolorable color slot in a whole UPK (every material / MIC /
    // particle system export). Powers the Skill Recolor "recolor a related power"
    // feature — some skills route secondary FX through a SEPARATE power UPK
    // (UC__PowerThor_HammerChain_SF.upk) that the VFX resolver never binds. The
    // user picks that UPK by name and we surface all its colors so its local
    // particle color modules can be recolored (no global shared-material edit).
    // Entries are LOCAL (not cross-package) so the writer's opt-out pass recolors
    // them in their own file — exactly what we want for a dedicated power UPK.
    public Task<IReadOnlyList<SkillColorEntry>> CollectColorsFromUpkAsync(string upkPath)
    {
        return Task.Run<IReadOnlyList<SkillColorEntry>>(async () =>
        {
            var entries = new List<SkillColorEntry>();
            if (string.IsNullOrWhiteSpace(upkPath) || !System.IO.File.Exists(upkPath)) return entries;
            try
            {
                var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);
                foreach (var export in header.ExportTable)
                {
                    string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                    if (cls is not ("material" or "materialinstanceconstant" or "materialinstancetimevarying" or "particlesystem")) continue;
                    try { if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false); }
                    catch { continue; }
                    if (export.UnrealObject is not IUnrealObject iuo || iuo.UObject is not UObject obj) continue;
                    switch (cls)
                    {
                        case "materialinstanceconstant": CollectFromMic(obj, export, upkPath, entries); break;
                        case "material": CollectFromMaterial(obj, export, upkPath, entries, header); break;
                        case "particlesystem": CollectFromParticleSystem(obj, export, upkPath, entries); break;
                    }
                }
            }
            catch { }
            return entries;
        });
    }

    private static void CollectFromParticleSystem(UObject obj, UnrealExportTableEntry export, string upkPath, List<SkillColorEntry> sink)
    {
        if (obj is not UParticleSystem ps) return;
        CollectFromParticleSystemCore(ps, export.ObjectNameIndex?.Name ?? string.Empty, upkPath, sink, markCrossPackage: false);
    }

    // Hero-wide augmentation: collects every recolorable color slot in the
    // hero's MarvelPlayer + dedicated vfx UPKs. The prototype-driven scan in
    // CollectSkillColorsAsync only reaches particle systems the power binds
    // through PowerFX components — weapon/anim-notify trails (animtrail_a,
    // dd_attack_trails_*, etc) live inside UC__MarvelPlayer_<Hero>_SF.upk
    // but are wired through AnimNotify_PSC at runtime, never the prototype,
    // so they were invisible to the recolor list. Surface them here so the
    // user sees the same emitter inventory the legacy MHModelEditor showed.
    //
    // All entries are flagged IsCrossPackage so the writer scopes each patch
    // to the exact module/expression export via the includedExportPaths
    // allowlist — never a blanket UPK rewrite.
    public Task<IReadOnlyList<SkillColorEntry>> CollectHeroPlayerColorsAsync(string heroToken, string cookedDir)
    {
        return Task.Run<IReadOnlyList<SkillColorEntry>>(async () =>
        {
            var entries = new List<SkillColorEntry>();
            if (string.IsNullOrWhiteSpace(heroToken) || string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir))
                return entries;

            var candidates = new List<string>();
            try
            {
                // Player UPKs: UC__MarvelPlayer_<Hero>*.upk (base + variant skins).
                foreach (string f in Directory.EnumerateFiles(cookedDir, $"UC__MarvelPlayer_{heroToken}*.upk", SearchOption.TopDirectoryOnly))
                    candidates.Add(f);
                // Dedicated VFX libs: vfx_<hero>*.upk (cooked content is lowercase).
                // Some heroes have these (e.g. trail-heavy heroes); many don't.
                string heroLower = heroToken.ToLowerInvariant();
                foreach (string f in Directory.EnumerateFiles(cookedDir, $"vfx_{heroLower}*.upk", SearchOption.TopDirectoryOnly))
                    candidates.Add(f);
                // Per-power UPKs: UC__Power<Hero>_*_SF.upk. Heroes WITHOUT a
                // dedicated vfx_* lib keep every
                // power's emitters in their per-power UPK instead. Without
                // these in scope a "recolor skill X" pass leaves every CHAINED
                // power (combo / cleanup / projectile-trail variants) at the
                // original color — visually identical to having done nothing.
                // The per-power write surface is the same one v1.0.30's CRC
                // fix verified for per-power UPK loads.
                foreach (string f in Directory.EnumerateFiles(cookedDir, $"UC__Power{heroToken}_*.upk", SearchOption.TopDirectoryOnly))
                    candidates.Add(f);
            }
            catch { return entries; }

            foreach (string upkPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                    await header.ReadHeaderAsync(null).ConfigureAwait(false);
                    int before = entries.Count;
                    foreach (var export in header.ExportTable)
                    {
                        string cls = (export.ClassReferenceNameIndex?.Name ?? string.Empty).ToLowerInvariant();
                        if (cls is not ("material" or "materialinstanceconstant" or "materialinstancetimevarying" or "particlesystem"))
                            continue;
                        try { if (export.UnrealObject is null) await export.ParseUnrealObject(false, false).ConfigureAwait(false); }
                        catch { continue; }
                        if (export.UnrealObject is not IUnrealObject iuo || iuo.UObject is not UObject obj) continue;
                        switch (cls)
                        {
                            case "materialinstanceconstant":
                            case "materialinstancetimevarying":
                                CollectFromMic(obj, export, upkPath, entries); break;
                            case "material":
                                CollectFromMaterial(obj, export, upkPath, entries, header, isCrossPackage: true); break;
                            case "particlesystem":
                                CollectFromParticleSystem(obj, export, upkPath, entries); break;
                        }
                    }
                    // Force IsCrossPackage on every entry collected from this UPK
                    // so the writer drops them into the surgical allowlist pass.
                    for (int i = before; i < entries.Count; i++)
                        if (!entries[i].IsCrossPackage)
                            entries[i] = entries[i] with { IsCrossPackage = true };
                }
                catch { /* one UPK fails, keep going */ }
            }
            return entries;
        });
    }

    // Core emitter walk. markCrossPackage=true flags the collected particle color
    // entries as cross-package so the apply pipeline scopes them through the
    // allowlist pass (patching ONLY these exact module exports), used when the
    // particle system was reached via a resolved cross-UPK binding rather than a
    // local export scan.
    private static void CollectFromParticleSystemCore(UParticleSystem ps, string psName, string upkPath, List<SkillColorEntry> sink, bool markCrossPackage)
    {
        if (ps.Emitters is null) return;
        foreach (var emitterRef in ps.Emitters)
        {
            if (emitterRef?.LoadObject<UObject>() is not UParticleEmitter emitter) continue;
            string emitterName = emitter.GetType().GetProperty("EmitterName")?.GetValue(emitter)?.ToString() ?? "emitter";
            if (emitter.LODLevels is null) continue;
            // Use LOD 0 only — LOD>0 are perf-fallback copies of the same modules.
            FObject? firstLodRef = null;
            foreach (FObject r in emitter.LODLevels) { firstLodRef = r; break; }
            if (firstLodRef is null) continue;
            UObject? lodObj = firstLodRef.LoadObject<UObject>();
            if (lodObj is not UParticleLODLevel lod) continue;
            if (lod.Modules is null) continue;

            foreach (FObject moduleRef in lod.Modules)
            {
                UObject? moduleObj;
                try { moduleObj = moduleRef.LoadObject<UObject>(); }
                catch { continue; }
                if (moduleObj is null) continue;

                // Module's export path so the writer can match against it
                // when the UI lets the user pick which slots to recolor.
                string moduleExportPath = moduleRef.GetPathName();

                // Cross-package resolution: when the particle module is an
                // IMPORT (cooked content frequently leaves PMCOL sub-objects
                // in their authoring UPK, e.g. vfx_thor.upk, while the
                // SKILL UPK only carries a reference), the entry's SourceUpkPath
                // must point at the foreign UPK so the writer opens THAT
                // file and finds the module exports there. Without this the
                // writer iterates the skill UPK's export table, sees no
                // ParticleModuleColorOverLife entries, and silently skips
                // every color slot — which is exactly the bug the diagnostic
                // log surfaced (considered=420 matched=122 zero PMCOL hits).
                string moduleUpkPath = ResolveModuleUpkPath(moduleRef, upkPath);

                switch (moduleObj)
                {
                    case UParticleModuleColor pmc:
                        AddDistributionEntry(sink, psName, emitterName, moduleUpkPath, pmc.StartColor, SkillColorKind.ParticleStartColor, "StartColor", moduleExportPath, markCrossPackage);
                        break;
                    case UParticleModuleColorOverLife pmcol:
                        AddDistributionEntry(sink, psName, emitterName, moduleUpkPath, pmcol.ColorOverLife, SkillColorKind.ParticleColorOverLife, "ColorOverLife", moduleExportPath, markCrossPackage);
                        break;
                    case UParticleModuleColorScaleOverLife pmcsol:
                    {
                        // Same FRawDistributionVector shape as the other two
                        // particle color modules — different outer property name.
                        var t = moduleObj.GetType();
                        var p = t.GetProperty("ColorScaleOverLife");
                        if (p?.GetValue(moduleObj) is FRawDistributionVector raw)
                            AddDistributionEntry(sink, psName, emitterName, moduleUpkPath, raw, SkillColorKind.ParticleColorScaleOverLife, "ColorScaleOverLife", moduleExportPath, markCrossPackage);
                        break;
                    }
                }
            }
        }
    }

    // For an FObject reference (a typed pointer at another UE3 object),
    // return the absolute path of the .upk file that ACTUALLY contains
    // its export body. Local refs return the current UPK; imports walk
    // up to the outermost package name and look for `<package>.upk` in
    // the same directory as the current UPK.
    private static string ResolveModuleUpkPath(FObject moduleRef, string currentUpkPath)
    {
        if (moduleRef?.TableEntry is UnrealExportTableEntry)
            return currentUpkPath;

        // Imports: walk the full path to extract the outermost package name.
        // GetPathName format example: "vfx_thor.particles.vfx_hammer_smash_launch_impact_01.ParticleModuleColorOverLife_0"
        // The package name is the first token before the first dot.
        string fullPath = moduleRef?.GetPathName() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fullPath)) return currentUpkPath;

        int firstDot = fullPath.IndexOf('.');
        string packageName = firstDot > 0 ? fullPath[..firstDot] : fullPath;
        if (string.IsNullOrWhiteSpace(packageName)) return currentUpkPath;

        // Same name → local (defensive, shouldn't reach here if TableEntry
        // wasn't an export, but guards against odd cooked-content edge cases).
        string currentPackage = System.IO.Path.GetFileNameWithoutExtension(currentUpkPath);
        if (string.Equals(packageName, currentPackage, StringComparison.OrdinalIgnoreCase))
            return currentUpkPath;

        // Sibling .upk lookup — cooked game content keeps cross-referenced
        // packages co-located. Mirrors the ForeignTextureCatalogService
        // discovery pattern. If the file doesn't exist, fall back to the
        // current UPK so we don't lose the entry entirely.
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(currentUpkPath)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) return currentUpkPath;

        string foreign = System.IO.Path.Combine(dir, packageName + ".upk");
        return System.IO.File.Exists(foreign) ? foreign : currentUpkPath;
    }

    private static void AddDistributionEntry(
        List<SkillColorEntry> sink,
        string psName, string emitterName, string upkPath,
        FRawDistributionVector? raw,
        SkillColorKind kind, string fieldName,
        string moduleExportPath = "",
        bool isCrossPackage = false)
    {
        if (raw is null) return;
        // Display color: read the LookupTable's first sample using the SAME
        // stride detection as SkillColorWriter.PatchLookupTableInPlace.
        //   count % 4 == 0  (and > 4) → 4-stride: (time, X, Y, Z) per sample;
        //                                we want floats [1..3] = (X, Y, Z).
        //   count % 3 == 0  → 3-stride: (X, Y, Z) per sample; floats [0..2].
        // Reading [0..2] unconditionally was the original bug — on a 4-stride
        // lookup it returned (time, R, G) and rendered as (R=time, G=R, B=G),
        // producing wildly wrong swatch colors after every recolor save.
        Vector4 color = new(1f, 1f, 1f, 1f);
        if (raw.LookupTable is { Count: > 0 } lookup)
        {
            int count = lookup.Count;
            // VERIFIED layout: 2 header floats (TimeBias, TimeScale) + RGB triplets.
            // The first colour sits at [2..4]. Must match SkillColorWriter's patch.
            if (count >= 5 && (count - 2) % 3 == 0)
                color = new Vector4(lookup[2], lookup[3], lookup[4], 1f);
            else if (count % 4 == 0 && count >= 4)
                color = new Vector4(lookup[1], lookup[2], lookup[3], 1f);
            else if (count >= 3)
                color = new Vector4(lookup[0], lookup[1], lookup[2], 1f);
        }

        DistributionShape shape = DistributionShape.Unknown;
        if (raw.Distribution is FObject distRef)
        {
            try
            {
                var distObj = distRef.LoadObject<UObject>();
                // Parameterized inherits from Constant; check it FIRST so we
                // don't misclassify a parameterized as a plain constant.
                // UniformCurve / UniformRange are explicit Uniform variants.
                shape = distObj switch
                {
                    UDistributionVectorParameterBase => DistributionShape.Parameterized,
                    UDistributionVectorConstantCurve => DistributionShape.ConstantCurve,
                    UDistributionVectorUniformCurve => DistributionShape.ConstantCurve,
                    UDistributionVectorUniformRange => DistributionShape.Uniform,
                    UDistributionVectorUniform => DistributionShape.Uniform,
                    UDistributionVectorConstant => DistributionShape.Constant,
                    _ => DistributionShape.Unknown,
                };
            }
            catch { /* keep Unknown */ }
        }

        // Fallback: the sub-distribution export sometimes isn't pre-parsed
        // (it lives as its own export and our walker doesn't open every
        // single one). In that case LoadObject returns null and the switch
        // above stays at Unknown — but the LookupTable on the raw wrapper
        // gives us a perfectly fine heuristic: 3 floats = single Vector3
        // (Constant), more floats = sampled curve (ConstantCurve).
        if (shape == DistributionShape.Unknown && raw.LookupTable is { Count: > 0 } lt)
        {
            shape = lt.Count <= 3 ? DistributionShape.Constant : DistributionShape.ConstantCurve;
        }

        // Parameterized distributions (UDistributionVectorParticleParameter)
        // have NO baked LookupTable — the value is normally supplied at spawn
        // time. But these game skills carry no InstanceParameters and no script
        // setter (verified via name-table probe), so the engine falls back to
        // the distribution's own `Constant` FVector default — which renders AND
        // is patchable UPK-side. Read that Constant for the swatch and mark the
        // slot editable so the user can recolor it (no .sip changes needed).
        if (shape == DistributionShape.Parameterized
            && raw.Distribution is FObject dref2
            && dref2.TableEntry is UnrealExportTableEntry dexp)
        {
            try
            {
                var hdr = dexp.UnrealHeader;
                byte[] dbody = dexp.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                if (hdr != null && dbody.Length > 0)
                {
                    var dsites = OmegaAssetStudio.Cooked.SkillExportPatcher.LocateAllVectorSitesInDistributionExport(dbody, hdr);
                    var cst = dsites.Select(s => s.Constant).FirstOrDefault(c => c is not null);
                    if (cst is not null && cst.Offset + 12 <= dbody.Length)
                        color = new Vector4(
                            BitConverter.ToSingle(dbody, cst.Offset),
                            BitConverter.ToSingle(dbody, cst.Offset + 4),
                            BitConverter.ToSingle(dbody, cst.Offset + 8), 1f);
                }
            }
            catch { /* keep lookup-derived color */ }
        }

        // The writer patches the LookupTable cache (curve/uniform shapes) AND
        // the dvec Constant FVector (parameterized fallback). Every shape is
        // now editable — parameterized included, since its rendered color is
        // the patchable Constant default.
        bool editable = true;
        sink.Add(new SkillColorEntry(
            Kind: kind,
            ParameterName: fieldName,
            OwnerLabel: $"Particle: {psName} → {emitterName}",
            SourceUpkPath: upkPath,
            CurrentColor: color,
            Shape: shape,
            Editable: editable,
            ExportPath: moduleExportPath,
            IsCrossPackage: isCrossPackage));
    }

    // DIAGNOSTIC (read-only): traces where a skill's PARAMETERIZED (locked) color
    // actually comes from. Lists each locked slot's parameter name, then every
    // InstanceParameters value baked in the skill's UPK(s), and concludes whether
    // the color is patchable (baked as an InstanceParameter) or script-set.
    public Task<string> InspectColorSourceAsync(PowerVfxResolver.ResolvedVfx vfx)
    {
        return Task.Run<string>(async () =>
        {
            var sb = new System.Text.StringBuilder();
            if (vfx is null) return "(no skill resolved)";

            var upkPaths = vfx.Bindings
                .Where(b => !string.IsNullOrEmpty(b.SourceUpkFullPath))
                .Select(b => b.SourceUpkFullPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var paramNames = new List<string>();
            foreach (var b in vfx.Bindings)
                if (b.ResolvedParticleSystem is UParticleSystem ps)
                    CollectParameterizedNames(ps, paramNames);
            var distinctParams = paramNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            sb.AppendLine("=== LOCKED (parameterized) color slots ===");
            if (distinctParams.Count == 0) sb.AppendLine("  none — no parameterized color slots in this skill.");
            else foreach (var pn in distinctParams) sb.AppendLine("  - parameter: " + pn);
            sb.AppendLine();

            sb.AppendLine("=== InstanceParameters baked in the skill's UPK(s) ===");
            var foundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyIP = false;
            // Definitive name-table probe — survives any export parse failure, since
            // every serialized property/function name MUST be in the name table.
            var probeFindings = new List<string>();
            bool sawInstanceParamsName = false;
            bool sawScriptSetter = false;
            // Dump the structure of the parameterized distribution exports so we can
            // find the patchable Constant/MaxOutput FVector the engine falls back to.
            var dvppDumps = new List<string>();
            int dvppCount = 0;
            foreach (string upkPath in upkPaths)
            {
                try
                {
                    var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                    await header.ReadHeaderAsync(null).ConfigureAwait(false);

                    var nameSet = new HashSet<string>(
                        header.NameTable.Select(n => n.Name?.String ?? string.Empty),
                        StringComparer.OrdinalIgnoreCase);
                    string fn0 = System.IO.Path.GetFileName(upkPath);
                    foreach (string probe in new[] { "InstanceParameters", "SetVectorParameter", "SetParticleParameter", "SetColorParameter" })
                    {
                        if (!nameSet.Contains(probe)) continue;
                        probeFindings.Add($"  [{fn0}] name-table contains: {probe}");
                        if (probe.Equals("InstanceParameters", StringComparison.OrdinalIgnoreCase)) sawInstanceParamsName = true;
                        if (probe.StartsWith("Set", StringComparison.OrdinalIgnoreCase)) sawScriptSetter = true;
                    }

                    foreach (var ex in header.ExportTable)
                    {
                        try { await header.ReadExportObjectAsync(ex, null).ConfigureAwait(false); }
                        catch { }
                    }
                    foreach (var ex in header.ExportTable)
                    {
                        byte[] body = ex.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
                        if (body.Length == 0) continue;

                        // Dump the parameterized-distribution export's property tags
                        // so we can see where its fallback Constant FVector lives.
                        string exCls = (ex.ClassReferenceNameIndex?.Name ?? string.Empty);
                        if (dvppCount < 3 && exCls.IndexOf("ParticleParameter", StringComparison.OrdinalIgnoreCase) >= 0
                            && exCls.IndexOf("Vector", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            dvppCount++;
                            var tags = OmegaAssetStudio.Cooked.SkillExportPatcher.DumpPropertyTags(body, header, 40);
                            dvppDumps.Add($"  [{fn0}] {ex.ObjectNameIndex?.Name} ({exCls}) body={body.Length}: {string.Join(" | ", tags)}");
                            var scan = OmegaAssetStudio.Cooked.SkillExportPatcher.DumpRawNameScan(body, header, 60, 48);
                            foreach (var s in scan) dvppDumps.Add($"      {s}");
                        }

                        var ips = OmegaAssetStudio.Cooked.SkillExportPatcher.DumpInstanceParameters(body, header);
                        if (ips.Count == 0) continue;
                        anyIP = true;
                        sb.AppendLine($"  [{System.IO.Path.GetFileName(upkPath)}] {ex.ObjectNameIndex?.Name} ({ex.ClassReferenceNameIndex?.Name}):");
                        foreach (var ip in ips)
                        {
                            if (!string.IsNullOrEmpty(ip.Name)) foundNames.Add(ip.Name);
                            sb.AppendLine($"       {ip.Name} = ({ip.X:0.###}, {ip.Y:0.###}, {ip.Z:0.###})");
                        }
                    }
                }
                catch (Exception e) { sb.AppendLine($"  [{System.IO.Path.GetFileName(upkPath)}] read error: {e.Message}"); }
            }
            if (!anyIP) sb.AppendLine("  none found.");
            sb.AppendLine();

            sb.AppendLine("=== Parameterized distribution structure (patch-target hunt) ===");
            if (dvppDumps.Count == 0) sb.AppendLine("  (no DistributionVectorParticleParameter exports dumped)");
            else foreach (var d in dvppDumps) sb.AppendLine(d);
            sb.AppendLine();

            sb.AppendLine("=== Name-table probe (definitive — survives parse failures) ===");
            if (probeFindings.Count == 0)
                sb.AppendLine("  NONE of: InstanceParameters / SetVectorParameter / SetParticleParameter / SetColorParameter");
            else
                foreach (var f in probeFindings) sb.AppendLine(f);
            sb.AppendLine();

            sb.AppendLine("=== Conclusion ===");
            if (anyIP)
                sb.AppendLine("  BAKED: InstanceParameters with RGB values exist in the UPK — patchable by writing the FParticleSysParam Vector. (Match names above to the locked params.)");
            else if (sawInstanceParamsName)
                sb.AppendLine("  LIKELY BAKED (but unread): the 'InstanceParameters' name IS in the name table, yet no values decoded — the component didn't parse. Worth a targeted raw decode; the value lives in the UPK, not script.");
            else if (sawScriptSetter)
                sb.AppendLine("  SCRIPT-SET: no InstanceParameters property exists, but a SetVectorParameter/SetParticleParameter function does — the color is a bytecode constant in this UPK's UnrealScript. Patchable IN THE UPK (no .sip), but requires locating the script operand.");
            else
                sb.AppendLine("  NEITHER baked InstanceParameters NOR a script setter found in these UPKs. The value may come from a parent/shared package or engine default — needs wider tracing. Still UPK-side (not .sip).");
            return sb.ToString();
        });
    }

    // Collects the ParameterName of every PARAMETERIZED color distribution in a
    // particle system (the locked slots' spawn-time parameter names).
    private static void CollectParameterizedNames(UParticleSystem ps, List<string> sink)
    {
        if (ps.Emitters is null) return;
        foreach (var emitterRef in ps.Emitters)
        {
            if (emitterRef?.LoadObject<UObject>() is not UParticleEmitter emitter) continue;
            if (emitter.LODLevels is null) continue;
            FObject? firstLod = null;
            foreach (FObject r in emitter.LODLevels) { firstLod = r; break; }
            if (firstLod?.LoadObject<UObject>() is not UParticleLODLevel lod || lod.Modules is null) continue;
            foreach (FObject moduleRef in lod.Modules)
            {
                UObject? mo;
                try { mo = moduleRef.LoadObject<UObject>(); }
                catch { continue; }
                FRawDistributionVector? raw = mo switch
                {
                    UParticleModuleColor pmc => pmc.StartColor,
                    UParticleModuleColorOverLife pmcol => pmcol.ColorOverLife,
                    _ => null,
                };
                if (raw is null && mo is not null)
                    raw = mo.GetType().GetProperty("ColorScaleOverLife")?.GetValue(mo) as FRawDistributionVector;
                if (raw?.Distribution is not FObject dref) continue;
                UObject? dobj;
                try { dobj = dref.LoadObject<UObject>(); }
                catch { continue; }
                if (dobj is UDistributionVectorParameterBase)
                {
                    object? pn = dobj.GetType().GetProperty("ParameterName")?.GetValue(dobj);
                    string name = (pn as FName)?.Name ?? pn?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(name)) sink.Add(name);
                }
            }
        }
    }

    private static Vector4 ExtractFLinearColor(object? colorObj)
    {
        if (colorObj is null) return new Vector4(1f, 1f, 1f, 1f);
        var t = colorObj.GetType();
        float r = SafeFloat(t.GetProperty("R")?.GetValue(colorObj));
        float g = SafeFloat(t.GetProperty("G")?.GetValue(colorObj));
        float b = SafeFloat(t.GetProperty("B")?.GetValue(colorObj));
        float a = SafeFloat(t.GetProperty("A")?.GetValue(colorObj));
        return new Vector4(r, g, b, a);
    }

    private static float SafeFloat(object? v) => v is float f ? f : (v is null ? 0f : 0f);

    // Best-effort scan of a ParticleSystem's emitters for material references.
    // Walks each emitter -> LODLevel -> RequiredModule.Material. LODLevels and
    // RequiredModule come back as FObject refs that must be LoadObject'd into
    // their concrete types before their fields are accessible.
    //
    // Returns (materialName, isCrossPackage). isCrossPackage=true means the
    // material's FObject table entry is an Import — the actual UMaterial /
    // UMaterialInstanceConstant lives in another UPK and editing the host UPK
    // alone won't affect this slot.
    private static IEnumerable<(string Name, bool IsCrossPackage)> WalkParticleMaterialRefs(UParticleSystem ps)
    {
        if (ps.Emitters is null) yield break;
        foreach (var emitterRef in ps.Emitters)
        {
            if (emitterRef?.LoadObject<UpkManager.Models.UpkFile.Classes.UObject>() is not UParticleEmitter emitter)
                continue;
            if (emitter.LODLevels is null) continue;
            foreach (FObject lodRef in emitter.LODLevels)
            {
                if (lodRef?.LoadObject<UpkManager.Models.UpkFile.Classes.UObject>() is not UParticleLODLevel lod)
                    continue;
                FObject? reqRef = lod.RequiredModule;
                if (reqRef?.LoadObject<UpkManager.Models.UpkFile.Classes.UObject>() is not UParticleModuleRequired req)
                    continue;
                FObject? matRef = req.Material;
                string n = matRef?.Name ?? string.Empty;
                if (string.IsNullOrEmpty(n)) continue;
                bool isImport = matRef!.TableEntry is UnrealImportTableEntry;
                yield return (n, isImport);
            }
        }
    }

    private static string Prettify(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        // "FooBar" -> "Foo Bar". "ironman" -> "Ironman".
        var sb = new System.Text.StringBuilder(token.Length + 4);
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (i == 0) sb.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c) && i > 0 && !char.IsUpper(token[i - 1])) { sb.Append(' '); sb.Append(c); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string PascalCase(string lower)
    {
        if (string.IsNullOrEmpty(lower)) return lower;
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _archive?.Dispose();
            _archive = null;
            _registry = null;
        }
    }
}
