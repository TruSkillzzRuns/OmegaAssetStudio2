using System.Numerics;
using OmegaAssetStudio.BackupManager;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile;
using UpkManager.Repository;

namespace OmegaAssetStudio.Cooked;

// Applies a uniform hue shift to every color slot a skill exposes, across
// every UPK the skill touches. Each modified UPK is backed up before the
// in-place byte patch is written and the package is repacked.
//
// What we patch (verified by MHO_HeroSkill_Color_DeepDive.md):
//   - MaterialExpressionVectorParameter.DefaultValue (FLinearColor, 16 bytes)
//   - ParticleModuleColor.StartColor LookupTable (float triplets)
//   - ParticleModuleColorOverLife.ColorOverLife LookupTable (float triplets,
//     potentially many keyframes for ConstantCurve distributions)
//
// Every patch is SIZE-PRESERVING in place; UPK export-table offsets stay
// valid, so UpkRepacker can splice the modified export bytes back without
// rebuilding the header.
//
// The caller passes a hue delta in [0,1] (turns) — i.e. the rotation needed
// to take the skill's current dominant hue to the user's chosen hue. Each
// slot's color has its hue rotated by that delta; saturation and value are
// preserved at every slot, so gradients, animations, and per-emitter
// intensity variations all keep their structure.
public sealed class SkillColorWriter
{
    public sealed record EditedExport(string UpkPath, string ExportPath, int FloatsPatched, string Kind);
    public sealed record WriteReport(IReadOnlyList<EditedExport> Edits, int UpksSaved, IReadOnlyList<string> Errors);

    private readonly UpkFileRepository _repo = new();

    // Patches every Material expression + particle distribution color in the
    // listed UPKs and saves each modified UPK with a backup. Returns a
    // structured report so the UI can surface what happened.
    // Direct-tint: replaces each color slot with the picked RGB scaled by the
    // slot's original brightness. Animation curves keep their intensity shape,
    // black stays black, white becomes full picked. Way more visible than the
    // old hue-rotation approach, which produced barely-visible tints when the
    // underlying particles stored dim low-saturation values.
    // The optional excludedExportPaths set lets the UI ship a "patch only the
    // slots the user checked" filter without making the writer aware of the
    // SkillColorEntry catalog. Every byte-patch branch consults it before
    // touching a body; missing-from-set means "skip this export".
    // includedExportPaths: OPT-IN allowlist. When non-null, an export is patched
    // ONLY if its path is in the set. This is how the cross-UPK shared-material
    // case stays surgical — a shared library (e.g. chbasematerials.upk) must have
    // ONLY the one referenced material's expressions touched, never every material
    // in the file. When null, the legacy opt-out behaviour applies (patch all
    // color-bearing exports except excludedExportPaths).
    public async Task<WriteReport> ApplyTintAsync(
        IEnumerable<string> upkPaths,
        Vector3 targetRgb,
        Action<string>? log = null,
        IReadOnlySet<string>? excludedExportPaths = null,
        IReadOnlySet<string>? includedExportPaths = null)
    {
        var edits = new List<EditedExport>();
        var errors = new List<string>();
        int upksSaved = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string upkPath in upkPaths)
        {
            if (!seen.Add(upkPath)) continue;
            if (string.IsNullOrEmpty(upkPath) || !File.Exists(upkPath))
            {
                errors.Add($"missing UPK: {upkPath}");
                continue;
            }

            try
            {
                var header = await _repo.LoadUpkFile(upkPath).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);

                // ReadHeaderAsync only reads the package header (name table,
                // import/export tables) — it does NOT populate each export's
                // raw byte buffer. Without this preload, UnrealObjectReader is
                // null and our walker sees zero bytes per export.
                foreach (var ex in header.ExportTable)
                    await header.ReadExportObjectAsync(ex, null).ConfigureAwait(false);

                // Walk every export, find ones that carry color data, patch.
                // We need the unmodified ExportBuffer list for every export so
                // UpkRepacker can rebuild the package. Then we replace just
                // the modified buffers.
                var buffers = header.ExportTable
                    .Select(e => new UpkRepacker.ExportBuffer(e.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>(), Array.Empty<UpkRepacker.BulkDataPatch>()))
                    .ToList();

                // DIAGNOSTIC: enumerate every export's class string and body size
                // so we can see exactly what the writer sees vs what's actually
                // in the UPK. Logged once per UPK at the top.
                {
                    var classHist = new Dictionary<string, (int count, int withBody)>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < header.ExportTable.Count; i++)
                    {
                        var ex = header.ExportTable[i];
                        string c = ex.ClassReferenceNameIndex?.Name ?? "(null)";
                        int len = buffers[i].Data.Length;
                        if (!classHist.TryGetValue(c, out var v)) v = (0, 0);
                        v.count++;
                        if (len > 0) v.withBody++;
                        classHist[c] = v;
                    }
                    foreach (var kv in classHist.OrderByDescending(kv => kv.Value.count).Take(40))
                        log?.Invoke($"[skill-recolor]   {Path.GetFileName(upkPath)} class='{kv.Key}' total={kv.Value.count} withBody={kv.Value.withBody}");
                }

                // SCOPE the dvec sub-export recoloring to ONLY distributions that
                // a COLOR particle module actually points at. The UDistributionVector*
                // classes are shared by velocity/size/location/spawn modules too;
                // patching those Constants with the user's RGB would corrupt
                // particle physics. So build an allowlist of dvec export paths by
                // resolving each color module's `Distribution` ObjectProperty refs.
                var colorDvecPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // When the page supplied an opt-in allowlist (cross-UPK / hero-wide
                // surgical scope), the per-slot filter at line ~158 rejects any
                // export not explicitly listed. Color modules whose RGB lives in
                // a DistributionVector* sub-export (no inline curve, only a
                // Distribution ObjectProperty — body=~237 in cooked content)
                // would then never get patched even though their parent module
                // IS in the allowlist. Track which dvec paths trace back to an
                // allowed module so we can wave them through alongside.
                var allowlistedDvecPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < header.ExportTable.Count; i++)
                {
                    var ex = header.ExportTable[i];
                    string c = ex.ClassReferenceNameIndex?.Name ?? string.Empty;
                    if (!IsColorBearingParticleModuleClass(c)) continue;
                    byte[] mb = buffers[i].Data;
                    if (mb.Length == 0) continue;
                    string modulePath = ex.GetPathName();
                    bool moduleIsAllowed = includedExportPaths is null || includedExportPaths.Contains(modulePath);
                    foreach (int objRef in SkillExportPatcher.LocateColorDistributionObjectRefs(mb, header))
                    {
                        if (objRef <= 0 || objRef - 1 >= header.ExportTable.Count) continue;
                        var tgt = header.ExportTable[objRef - 1];
                        string p = tgt.GetPathName();
                        if (string.IsNullOrEmpty(p)) continue;
                        colorDvecPaths.Add(p);
                        if (moduleIsAllowed) allowlistedDvecPaths.Add(p);
                    }
                }
                log?.Invoke($"[skill-recolor]   color-dvec allowlist ({Path.GetFileName(upkPath)}): {colorDvecPaths.Count} distribution(s){(includedExportPaths is not null ? $", {allowlistedDvecPaths.Count} via allowlisted modules" : string.Empty)}");

                bool changed = false;
                int considered = 0, matched = 0;
                bool dumpedMatExpr = false, dumpedPmc = false, dumpedPmcol = false;
                for (int i = 0; i < header.ExportTable.Count; i++)
                {
                    var export = header.ExportTable[i];
                    string cls = export.ClassReferenceNameIndex?.Name ?? string.Empty;
                    byte[] body = buffers[i].Data;
                    if (body.Length == 0) continue;
                    considered++;

                    // Per-slot user opt-out. The page sends us the full export
                    // path of every slot the user UNchecked; we compare each
                    // candidate export's path against that set before doing
                    // any work.
                    if (excludedExportPaths is not null && excludedExportPaths.Contains(export.GetPathName()))
                        continue;

                    // Opt-in allowlist (cross-UPK shared materials): when set,
                    // only the explicitly-listed exports are patched, so a shared
                    // library file never gets blanket-recolored. Linked
                    // DistributionVector* sub-exports of allowlisted color
                    // modules ride along — without that, modules whose RGB
                    // lives in a sub-export (no inline curve) silently miss.
                    if (includedExportPaths is not null
                        && !includedExportPaths.Contains(export.GetPathName())
                        && !allowlistedDvecPaths.Contains(export.GetPathName()))
                        continue;

                    if (string.Equals(cls, "MaterialExpressionVectorParameter", StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        var off = SkillExportPatcher.LocateMaterialExpressionDefaultValue(body, header);
                        if (off is null)
                        {
                            log?.Invoke($"[skill-recolor]   miss: matexpr {export.GetPathName()} body={body.Length}");
                            continue;
                        }
                        byte[] patched = (byte[])body.Clone();
                        PatchFLinearColorInPlace(patched, off.Offset, targetRgb);
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), 3, "MaterialExpression"));
                        changed = true;
                    }
                    else if (IsColorBearingParticleModuleClass(cls))
                    {
                        // STRICT allowlist of particle module classes that
                        // carry COLOR distributions. The previous StartsWith
                        // ("ParticleModule") branch matched everything from
                        // ParticleModuleAcceleration to ParticleModuleSpawn —
                        // those use RawDistributionVector for vectors that
                        // describe velocity, position, rotation rates etc.,
                        // NOT color. Hard-replacing them with the user's
                        // picked RGB silently corrupted particle physics on
                        // every save. Allowlist guarantees we only touch
                        // bytes that actually represent color.
                        var sitesList = SkillExportPatcher.LocateAllParticleModuleDistributionSites(body, header);
                        if (sitesList.Count == 0)
                        {
                            log?.Invoke($"[skill-recolor]   miss: pm-color {cls} {export.GetPathName()} body={body.Length}");
                            continue;
                        }
                        matched++;
                        byte[] patched = (byte[])body.Clone();
                        int floatsPatched = 0;
                        foreach (var sites in sitesList)
                            floatsPatched += PatchAllDistributionSites(patched, sites, targetRgb);
                        if (floatsPatched == 0)
                        {
                            log?.Invoke($"[skill-recolor]   miss-empty: pm-color {cls} {export.GetPathName()} sites={sitesList.Count}");
                            continue;
                        }
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), floatsPatched, cls));
                        changed = true;
                    }
                    else if (IsVectorDistributionClass(cls))
                    {
                        // Sub-export pointed at by a ParticleModule's
                        // ColorOverLife.Distribution / StartColor.Distribution
                        // ObjectProperty. The curve keys (Points) and Constant
                        // FVector live HERE — not inline in the ParticleModule.
                        // Patching only the ParticleModule's inline LookupTable
                        // doesn't stick: the engine re-samples Points from this
                        // sub-export on load, overwriting the cache.
                        // Physics-safety gate: only recolor this dvec if a COLOR
                        // particle module references it. Velocity/size/location
                        // distributions use the same classes and must stay intact.
                        if (!colorDvecPaths.Contains(export.GetPathName()))
                        {
                            log?.Invoke($"[skill-recolor]   skip non-color dvec {cls} {export.GetPathName()}");
                            continue;
                        }
                        matched++;
                        var sitesList = SkillExportPatcher.LocateAllVectorSitesInDistributionExport(body, header);
                        if (sitesList.Count == 0)
                        {
                            log?.Invoke($"[skill-recolor]   miss: dvec {cls} {export.GetPathName()} body={body.Length}");
                            continue;
                        }
                        byte[] patched = (byte[])body.Clone();
                        int floatsPatched = 0;
                        foreach (var sites in sitesList)
                            floatsPatched += PatchAllDistributionSites(patched, sites, targetRgb);
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), floatsPatched, cls));
                        changed = true;
                    }
                    else if (string.Equals(cls, "MaterialExpressionConstant3Vector", StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        var off = SkillExportPatcher.LocateConstant3VectorChannels(body, header);
                        if (off is null)
                        {
                            log?.Invoke($"[skill-recolor]   miss: c3v {export.GetPathName()} body={body.Length}");
                            continue;
                        }
                        byte[] patched = (byte[])body.Clone();
                        PatchConstantVectorChannelsInPlace(patched, off, targetRgb);
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), 3, "Constant3Vector"));
                        changed = true;
                    }
                    else if (string.Equals(cls, "MaterialExpressionConstant4Vector", StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        var off = SkillExportPatcher.LocateConstant4VectorChannels(body, header);
                        if (off is null)
                        {
                            log?.Invoke($"[skill-recolor]   miss: c4v {export.GetPathName()} body={body.Length}");
                            continue;
                        }
                        byte[] patched = (byte[])body.Clone();
                        PatchConstantVectorChannelsInPlace(patched, off, targetRgb);
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), off.AOffset.HasValue ? 4 : 3, "Constant4Vector"));
                        changed = true;
                    }
                    else if (IsMaterialBodyClass(cls))
                    {
                        // SAFE locator: walks tagged properties looking for
                        // EmissiveColor / DiffuseColor / similar StructProperty of
                        // type ColorMaterialInput, then descends into the struct
                        // to find the "Constant" FLinearColor (16 bytes). Returns
                        // the exact byte offset to patch. No byte guessing — uses
                        // the NameTable to verify every property name encountered.
                        var colorConstantOffsets = SkillExportPatcher.LocateMaterialBodyColorConstants(body, header);
                        if (colorConstantOffsets.Count == 0)
                        {
                            log?.Invoke($"[skill-recolor]   matbody-check: cls='{cls}' hits=0 path={export.GetPathName()}");
                            continue;
                        }
                        byte[] patched = (byte[])body.Clone();
                        foreach (int off in colorConstantOffsets)
                        {
                            float r = BitConverter.ToSingle(patched, off + 0);
                            float g = BitConverter.ToSingle(patched, off + 4);
                            float b = BitConverter.ToSingle(patched, off + 8);
                            var (nr, ng, nb) = Tint(r, g, b, targetRgb);
                            WriteFloat(patched, off + 0, nr);
                            WriteFloat(patched, off + 4, ng);
                            WriteFloat(patched, off + 8, nb);
                            // alpha (off+12) intentionally untouched
                            log?.Invoke($"[skill-recolor]     matbody: patched FLinearColor at offset {off} in {cls} {export.GetPathName()}");
                        }
                        matched++;
                        buffers[i] = new UpkRepacker.ExportBuffer(patched, Array.Empty<UpkRepacker.BulkDataPatch>());
                        edits.Add(new EditedExport(upkPath, export.GetPathName(), colorConstantOffsets.Count * 3, cls + "BodyColor"));
                        changed = true;
                        log?.Invoke($"[skill-recolor]   matbody-check: cls='{cls}' hits={colorConstantOffsets.Count} path={export.GetPathName()}");
                    }
                }
                log?.Invoke($"[skill-recolor]   summary {Path.GetFileName(upkPath)}: considered={considered} matched={matched} edits={edits.Count(e => e.UpkPath == upkPath)}");

                if (!changed) { log?.Invoke($"[skill-recolor] no editable color slots found in {Path.GetFileName(upkPath)}"); continue; }

                // Backup, then write the repacked UPK in place. Backup-on-save
                // is the standard pattern — Backup tool can restore from .bak
                // if the result misbehaves in-game.
                byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(false);
                // Written plainly even when the original was packed, and that
                // is deliberate. The repacker lays the exports out afresh and
                // writes absolute positions into the file for each export's
                // SerialOffset and for every bulk-data offset it patches.
                // Packing the result moves everything those positions point at,
                // and the game hangs on the next load screen. Trading the file
                // size for a file that loads is the right way round.
                byte[] repacked = header.CompressedChunks.Count > 0
                    ? UpkRepacker.RepackCompressed(originalBytes, header, buffers)
                    : UpkRepacker.Repack(originalBytes, header, buffers);

                BackupFileHelper.CreateBackup(upkPath);
                await File.WriteAllBytesAsync(upkPath, repacked).ConfigureAwait(false);
                upksSaved++;
                log?.Invoke($"[skill-recolor] wrote {Path.GetFileName(upkPath)} ({repacked.Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(upkPath)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new WriteReport(edits, upksSaved, errors);
    }

    // Every UDistributionVector* subclass that can carry color data. These
    // sub-exports are what the ParticleModule's Distribution ObjectProperty
    // points at; the actual curve / constant values live here, not in the
    // ParticleModule's inline RawDistributionVector struct.
    private static readonly HashSet<string> _vectorDistributionClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DistributionVectorConstant",
        "DistributionVectorConstantCurve",
        "DistributionVectorUniform",
        "DistributionVectorUniformCurve",
        "DistributionVectorUniformRange",

        // DistributionVectorParameterBase and DistributionVectorParticleParameter
        // are deliberately absent. They hold no colour: the game supplies one at
        // spawn and these carry the range it is mapped through. The reader has
        // always said so — it marks them Parameterized and the panel shows them
        // read-only — and the writer patching them anyway wrote a tint over an
        // input range, leaving min above max. One skill came back
        // with 0.58, 0.58, 4.37 rewritten as 4.37, 0, 0, and the game hung on
        // the next load screen.
    };

    private static bool IsVectorDistributionClass(string className)
        => !string.IsNullOrEmpty(className) && _vectorDistributionClasses.Contains(className);

    // Strict allowlist of ParticleModule classes that carry COLOR data.
    // Anything else — Acceleration, Velocity, Spawn, Rotation, Location,
    // SubUV, EventGenerator, RotationRate, Lifetime, etc. — also uses
    // RawDistributionVector but for non-color values; patching them with
    // a color RGB would corrupt particle physics. Confirmed by the
    // diagnostic log: pm-sites was hitting acceleration_2 with lookup=14
    // on every save before this gate was added.
    private static readonly HashSet<string> _colorBearingParticleModuleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ParticleModuleColor",
        "ParticleModuleColorOverLife",
        "ParticleModuleColorScaleOverLife",
        "ParticleModuleColorByParameter",
        "ParticleModuleColorOverLifeRandom",
    };

    private static bool IsColorBearingParticleModuleClass(string className)
        => !string.IsNullOrEmpty(className) && _colorBearingParticleModuleClasses.Contains(className);

    // Hard replace: every slot becomes the picked color exactly, regardless
    // of its original brightness or curve gradient. User explicitly asked
    // for this — they want "pure red means pure red on every emitter," not
    // "a range of reds based on original intensity." Black slots also flip
    // to target, which collapses dim-to-bright gradients into a flat color
    // — that's the trade-off they accepted to get predictable output.
    // Brightness-preserving recolor. These VFX use HDR colors (e.g. a bright-blue
    // bolt is (1,1,100)); hard-replacing with the raw target collapsed that to
    // (1,0,0) — a DIM red that additive blending barely shows, so the effect
    // looked unchanged/blue. Instead keep the ORIGINAL peak magnitude and only
    // swap the hue: a bright blue becomes an equally bright red, a dim sample
    // stays dim, so gradients keep their shape AND intensity.
    // Material-body classes whose color constants live inline (not as separate
    // MaterialExpression* exports). Covers stock UE3 Material + the fork's
    // DecalMaterial / MaterialInstance variants where applicable.
    private static bool IsMaterialBodyClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        string low = cls.ToLowerInvariant();
        return low is "material" or "decalmaterial";
    }

    // Scans a material export body for FLinearColor color constants embedded in
    // emissivecolor / colormaterialinput / similar input structs, and applies
    // brightness-preserving tint to each. Used for DecalMaterial and Material
    // exports whose color isn't a separate MaterialExpression export.
    //
    // CONSERVATIVE heuristics — only patch a 16-byte (R,G,B,A) candidate when:
    //   • all four floats are finite and in a sane range
    //   • alpha is EXACTLY 0.0 or 1.0 (cooked emissive constants ship with these;
    //     masks/selectors are 0 in three slots with one slot a small non-{0,1})
    //   • at least one of R/G/B is non-trivial (≥0.01)
    //   • the candidate is "color-like": max channel ≤ 1024, no negatives
    //   • the existing color is non-neutral (not pure white/gray) — neutrals
    //     are usually masks
    // After patching, advance the cursor by 16 bytes so we don't double-patch
    // overlapping reads of the same color.
    private static int PatchMaterialBodyColorConstants(byte[] bytes, Vector3 target,
        string exportPath = "", Action<string>? log = null)
    {
        // DISABLED — the aggressive byte-scan version corrupted a UPK by tinting
        // bytes that LOOKED like a FLinearColor but were actually a NameTable
        // index inside a property tag. The game refused to load the file with
        // "Bad Name Index" because we'd overwritten an int32 ref with 2.0f.
        //
        // A safer implementation needs to actually PARSE the cooked tagged-
        // property stream and only patch FLinearColors that live inside a
        // known struct (emissivecolor.colormaterialinput.Constant, etc.). Until
        // that's in place, NO body-byte scanning — the writer's existing
        // MaterialExpression* patches handle every material where the color
        // lives in a separate expression export (which is the safe case).
        //
        // The ground decal (dmat_burning_elemental_storm_fadeout_01) stays blue
        // for now because its color is embedded in the cooked property blob;
        // patching it requires the tagged-property parser. That's the next
        // build, not a byte scan.
        return 0;
    }

    private static bool IsColorCandidate(float r, float g, float b, float a)
    {
        if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b) || float.IsNaN(a)) return false;
        if (float.IsInfinity(r) || float.IsInfinity(g) || float.IsInfinity(b) || float.IsInfinity(a)) return false;

        // Alpha must be exactly 0 or 1 — cooked emissive color constants ship
        // with these values; channel mask / selector floats virtually never
        // land on an exact 0.0 / 1.0 by coincidence in the alpha slot.
        if (!(a == 0f || a == 1f)) return false;

        // No negatives (masks / vertex normals can have negative components).
        if (r < 0f || g < 0f || b < 0f) return false;

        // Sane HDR range. Cooked content tops out around (1000, 1000, 1000); a
        // float that's millions is almost certainly not a color.
        float maxRGB = MathF.Max(r, MathF.Max(g, b));
        if (maxRGB > 1024f) return false;

        // Non-trivial — at least one channel must be ≥ 0.01.
        if (maxRGB < 0.01f) return false;

        // Skip near-neutral colors (white/gray) — they're usually intensity
        // multipliers or masks, not real material colors.
        float minRGB = MathF.Min(r, MathF.Min(g, b));
        if (maxRGB - minRGB < 0.001f) return false; // all three channels equal

        return true;
    }

    private static (float r, float g, float b) Tint(float r, float g, float b, Vector3 target)
    {
        float origMax = MathF.Max(r, MathF.Max(g, b));
        if (origMax <= 0f) return (target.X, target.Y, target.Z);   // black original → take target as-is
        float tMax = MathF.Max(target.X, MathF.Max(target.Y, target.Z));
        if (tMax <= 0f) return (0f, 0f, 0f);                        // target is black → honor it
        float scale = origMax / tMax;
        return (target.X * scale, target.Y * scale, target.Z * scale);
    }

    // Constant3Vector / Constant4Vector store R, G, B (and optional A) as
    // separate FloatProperty tags. Apply the same brightness-preserving
    // tint as the other color slots so the channel that was dim stays dim.
    private static void PatchConstantVectorChannelsInPlace(byte[] bytes, SkillExportPatcher.ConstantVectorOffsets off, Vector3 target)
    {
        float r = BitConverter.ToSingle(bytes, off.ROffset);
        float g = BitConverter.ToSingle(bytes, off.GOffset);
        float b = BitConverter.ToSingle(bytes, off.BOffset);
        var (nr, ng, nb) = Tint(r, g, b, target);
        WriteFloat(bytes, off.ROffset, nr);
        WriteFloat(bytes, off.GOffset, ng);
        WriteFloat(bytes, off.BOffset, nb);
        // Alpha is intentionally left as-is. Tinting alpha would change the
        // material's apparent translucency which is not what "recolor" means.
    }

    private static void PatchFLinearColorInPlace(byte[] bytes, int offset, Vector3 target)
    {
        float r = BitConverter.ToSingle(bytes, offset + 0);
        float g = BitConverter.ToSingle(bytes, offset + 4);
        float b = BitConverter.ToSingle(bytes, offset + 8);
        var (nr, ng, nb) = Tint(r, g, b, target);
        WriteFloat(bytes, offset + 0, nr);
        WriteFloat(bytes, offset + 4, ng);
        WriteFloat(bytes, offset + 8, nb);
    }

    // LookupTable for FRawDistributionVector is packed as 4 floats per sample
    // (verified from real export bytes — a "ColorOverLife" distribution with
    // 2 keys produces count=8). Layout per sample: (time, X, Y, Z). We patch
    // X/Y/Z and leave time alone — otherwise the curve sampling breaks.
    //
    // For distributions stored as plain RGB triplets (no time component), the
    // stride is 3 floats. We auto-detect by checking divisibility: prefer the
    // 4-per-sample format when the count divides evenly by 4 AND not by 3
    // (count=8 -> 4-stride; count=3 -> 3-stride; count=6 could be either, we
    // try 3-stride first since that matches the "single Constant value at
    // each sample" case).
    // Patch every color region inside a RawDistributionVector — LookupTable
    // cache, Distribution.Points (FInterpCurveVector keys), and standalone
    // Constant (DT_Constant fallback). All three are needed because the
    // engine's load path can regenerate the LookupTable from Points,
    // silently undoing a lookup-only patch. Returns the number of floats
    // actually touched so the writer's edits report stays accurate.
    private static int PatchAllDistributionSites(byte[] bytes, SkillExportPatcher.DistributionPatchSites sites, Vector3 target)
    {
        int floatsPatched = 0;

        if (sites.Lookup is not null)
        {
            PatchLookupTableInPlace(bytes, sites.Lookup.Offset, sites.Lookup.FloatCount, target);
            floatsPatched += sites.Lookup.FloatCount;
        }

        if (sites.Points is not null)
        {
            // FInterpCurvePoint<FVector> layout: InVal(4) + OutVal(12) +
            // ArriveTangent(12) + LeaveTangent(12) + InterpMode(1). OutVal
            // sits at +4 from the point start. Patch only OutVal; tangents
            // are slopes (zeros for constant interpolation, scaled deltas
            // otherwise), and InterpMode is enum bytes — touching either
            // breaks the curve shape.
            for (int p = 0; p < sites.Points.Count; p++)
            {
                int pointStart = sites.Points.Offset + p * sites.Points.Stride;
                int outValStart = pointStart + 4;
                if (outValStart + 12 > bytes.Length) break;
                float x = BitConverter.ToSingle(bytes, outValStart + 0);
                float y = BitConverter.ToSingle(bytes, outValStart + 4);
                float z = BitConverter.ToSingle(bytes, outValStart + 8);
                var (nx, ny, nz) = Tint(x, y, z, target);
                WriteFloat(bytes, outValStart + 0, nx);
                WriteFloat(bytes, outValStart + 4, ny);
                WriteFloat(bytes, outValStart + 8, nz);
                floatsPatched += 3;
            }
        }

        if (sites.Constant is not null)
        {
            int o = sites.Constant.Offset;
            if (o + 12 <= bytes.Length)
            {
                float x = BitConverter.ToSingle(bytes, o + 0);
                float y = BitConverter.ToSingle(bytes, o + 4);
                float z = BitConverter.ToSingle(bytes, o + 8);
                var (nx, ny, nz) = Tint(x, y, z, target);
                WriteFloat(bytes, o + 0, nx);
                WriteFloat(bytes, o + 4, ny);
                WriteFloat(bytes, o + 8, nz);
                floatsPatched += 3;
            }
        }

        return floatsPatched;
    }

    private static void PatchLookupTableInPlace(byte[] bytes, int offset, int floatCount, Vector3 target)
    {
        // VERIFIED cooked-game RawDistributionVector LookupTable layout (from real
        // export bytes across counts 8/14/20): TWO leading header floats
        // (TimeBias, TimeScale) followed by N RGB triplets (stride 3). So
        // (floatCount - 2) is divisible by 3. This is the correct, primary path —
        // the old %4/%3 heuristics skipped count=14 entirely and were shifted one
        // float off (clobbering the header) on count=8/20.
        if (floatCount >= 5 && (floatCount - 2) % 3 == 0)
        {
            int triplets = (floatCount - 2) / 3;
            for (int t = 0; t < triplets; t++)
            {
                int p = offset + 8 + t * 12;   // +8 = skip the 2 header floats
                if (p + 12 > bytes.Length) break;
                TintSiteInPlace(bytes, p, target);
            }
            return;
        }

        // Fallbacks for any unexpected layout (kept defensive).
        if (floatCount > 0 && floatCount % 4 == 0)
        {
            int samples = floatCount / 4;
            for (int s = 0; s < samples; s++)
            {
                int p = offset + s * 16;
                if (p + 16 > bytes.Length) break;
                TintSiteInPlace(bytes, p + 4, target);   // (time, X, Y, Z) → X at +4
            }
            return;
        }
        if (floatCount % 3 == 0)
        {
            int triplets = floatCount / 3;
            for (int t = 0; t < triplets; t++)
            {
                int p = offset + t * 12;
                if (p + 12 > bytes.Length) break;
                TintSiteInPlace(bytes, p, target);
            }
        }
    }

    // Read an in-place RGB triplet, apply the brightness-preserving Tint, write
    // it back. Used by every lookup-table sample so HDR colors keep their peak
    // intensity instead of being flattened to the raw target.
    private static void TintSiteInPlace(byte[] bytes, int offset, Vector3 target)
    {
        float x = BitConverter.ToSingle(bytes, offset + 0);
        float y = BitConverter.ToSingle(bytes, offset + 4);
        float z = BitConverter.ToSingle(bytes, offset + 8);
        var (nx, ny, nz) = Tint(x, y, z, target);
        WriteFloat(bytes, offset + 0, nx);
        WriteFloat(bytes, offset + 4, ny);
        WriteFloat(bytes, offset + 8, nz);
    }

    private static void WriteFloat(byte[] dst, int offset, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        bytes.CopyTo(new Span<byte>(dst, offset, 4));
    }

    private static Vector4 ShiftHue(Vector4 rgb, float hueDelta)
    {
        Vector4 hsv = RgbToHsv(rgb);
        hsv.X = (hsv.X + hueDelta) % 1f;
        if (hsv.X < 0) hsv.X += 1f;
        return HsvToRgb(hsv);
    }

    private static Vector4 RgbToHsv(Vector4 rgb)
    {
        float r = Math.Clamp(rgb.X, 0f, 1f);
        float g = Math.Clamp(rgb.Y, 0f, 1f);
        float b = Math.Clamp(rgb.Z, 0f, 1f);
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float v = max;
        float d = max - min;
        float s = max <= 0f ? 0f : d / max;
        float h = 0f;
        if (d > 0f)
        {
            if (max == r)      h = (g - b) / d + (g < b ? 6f : 0f);
            else if (max == g) h = (b - r) / d + 2f;
            else               h = (r - g) / d + 4f;
            h /= 6f;
        }
        return new Vector4(h, s, v, rgb.W);
    }

    private static Vector4 HsvToRgb(Vector4 hsv)
    {
        float h = (hsv.X % 1f + 1f) % 1f;
        float s = Math.Clamp(hsv.Y, 0f, 1f);
        float v = Math.Clamp(hsv.Z, 0f, 1f);
        float c = v * s;
        float hp = h * 6f;
        float x = c * (1f - MathF.Abs(hp % 2f - 1f));
        float r1, g1, b1;
        if (hp < 1)      { r1 = c; g1 = x; b1 = 0; }
        else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
        else             { r1 = c; g1 = 0; b1 = x; }
        float m = v - c;
        return new Vector4(r1 + m, g1 + m, b1 + m, hsv.W);
    }
}
