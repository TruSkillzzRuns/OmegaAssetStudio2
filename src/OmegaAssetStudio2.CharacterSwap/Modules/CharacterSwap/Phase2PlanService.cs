using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Produces a detailed implementation plan for "Phase 2" — the actual visual
// transplant that Phase 1-B's diagnostic was a stepping stone toward.
//
// Phase 2 is intrinsically multi-step:
//   (a) Add source-only NAMES to target's NameTable.
//   (b) Add source-only IMPORTS to target's ImportTable.
//   (c) Add source-only EXPORTS to target's ExportTable + DependsTable.
//   (d) Translate the new exports' bodies using updated IndexTranslator (new
//       names+imports+exports are now in target's tables).
//   (e) Re-translate the size-changed MATCHED exports (e.g. the costume's
//       UClass meta and Default__ subobject) — these reference the newly-
//       added exports via embedded FObject refs.
//   (f) TFC manifest update + (optionally) .tfc payload append for new
//       streamed Texture2D mips.
//
// This service runs steps (a)-(e) as a read-only scan: it enumerates exactly
// what would need to be added/translated and any hard blockers, but writes
// nothing. Output is a single human-readable report. Use it before the
// destructive Phase 2 implementation lands to confirm the scope is sane.
public sealed class Phase2PlanService
{
    public sealed class Plan
    {
        public string Summary { get; init; } = string.Empty;
    }

    public async Task<Plan> AnalyzeAsync(string sourceUpkPath, string targetUpkPath, Action<string>? log = null)
    {
        if (!File.Exists(sourceUpkPath)) throw new FileNotFoundException("source upk not found", sourceUpkPath);
        if (!File.Exists(targetUpkPath)) throw new FileNotFoundException("target upk not found", targetUpkPath);

        log?.Invoke($"Phase 2 plan: loading source {sourceUpkPath}");
        UpkFileRepository repo = new();
        var srcHeader = await repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
        await srcHeader.ReadHeaderAsync(null).ConfigureAwait(false);
        log?.Invoke($"Phase 2 plan: loading target {targetUpkPath}");
        var tgtHeader = await repo.LoadUpkFile(targetUpkPath).ConfigureAwait(false);
        await tgtHeader.ReadHeaderAsync(null).ConfigureAwait(false);

        // Build the same kind of translator Phase 1-B uses, so we can tell
        // which source-only references would be "free" (already resolvable
        // via target's existing tables) vs "needs a new entry".
        var translator = new IndexTranslator(srcHeader, tgtHeader);

        // -- Step A: Names to add --
        // The translator already lists src names not present in target. Many
        // of these will be brand-new strings the visual upgrade introduces
        // ('colossus_aoavu_mat_v2', 'colossus_ageofapocalypsevu', etc.).
        var namesToAdd = translator.NamesMissingFromTarget
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // -- Step B: Imports to add --
        var importsToAdd = translator.ImportsMissingFromTarget
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // -- Step C: Exports to add --
        // "Source-only" exports = ones whose (class, name) doesn't exist on
        // target. These are the AddNew bucket from the analyzer.
        var tgtKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in tgtHeader.ExportTable)
            tgtKeys.Add($"{e.ClassReferenceNameIndex?.Name}::{e.ObjectNameIndex?.Name}");
        var exportsToAdd = new List<UnrealExportTableEntry>();
        foreach (var s in srcHeader.ExportTable)
        {
            string key = $"{s.ClassReferenceNameIndex?.Name}::{s.ObjectNameIndex?.Name}";
            if (!tgtKeys.Contains(key))
                exportsToAdd.Add(s);
        }

        // -- Step E: Size-changed matched exports --
        // Exports that exist on both sides but with different SerialDataSize.
        // After (a)-(d), these need their bodies re-translated from source's
        // version because the new content references the newly-added exports.
        var srcByKey = srcHeader.ExportTable
            .GroupBy(e => $"{e.ClassReferenceNameIndex?.Name}::{e.ObjectNameIndex?.Name}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sizeChangedMatched = new List<(UnrealExportTableEntry Src, UnrealExportTableEntry Tgt, int Delta)>();
        foreach (var t in tgtHeader.ExportTable)
        {
            string key = $"{t.ClassReferenceNameIndex?.Name}::{t.ObjectNameIndex?.Name}";
            if (!srcByKey.TryGetValue(key, out var s)) continue;
            if (s.SerialDataSize == t.SerialDataSize) continue;
            sizeChangedMatched.Add((s, t, s.SerialDataSize - t.SerialDataSize));
        }

        // -- Step F: TFC textures (read-only count for now) --
        // Future: cross-check against target's TextureFileCacheManifest.bin.
        int sourceTextureExports = srcHeader.ExportTable.Count(e =>
            string.Equals(e.ClassReferenceNameIndex?.Name, "Texture2D", StringComparison.OrdinalIgnoreCase));
        int newTextureExports = exportsToAdd.Count(e =>
            string.Equals(e.ClassReferenceNameIndex?.Name, "Texture2D", StringComparison.OrdinalIgnoreCase));

        // -- Hard blockers --
        // Source-only classes that target's name table doesn't know about.
        // Any export of such a class can't be added because the engine won't
        // resolve the class reference. Today only 'marveluihudbarconcarcomp'
        // is flagged for the AoA Colossus pair.
        var classNamesUsedBySource = exportsToAdd
            .Select(e => e.ClassReferenceNameIndex?.Name ?? string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hardBlockerClasses = classNamesUsedBySource
            .Where(c => translator.NamesMissingFromTarget.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // -- Build the report --
        var sb = new StringBuilder();
        sb.AppendLine("=== Phase 2 Plan — what the full transplant will need ===");
        sb.AppendLine();
        sb.AppendLine($"Source : v{srcHeader.Version}  {sourceUpkPath}");
        sb.AppendLine($"Target : v{tgtHeader.Version}  {targetUpkPath}");
        sb.AppendLine();
        sb.AppendLine("-- Headline scope --");
        sb.AppendLine($"  Names to add to target NameTable        : {namesToAdd.Count}");
        sb.AppendLine($"  Imports to add to target ImportTable    : {importsToAdd.Count}");
        sb.AppendLine($"  Source-only exports to add              : {exportsToAdd.Count}");
        sb.AppendLine($"    of which Texture2D                    : {newTextureExports}");
        sb.AppendLine($"  Matched exports needing size-change translation : {sizeChangedMatched.Count}");
        sb.AppendLine($"  Hard-blocker classes (target engine doesn't have them) : {hardBlockerClasses.Count}");
        sb.AppendLine();

        if (hardBlockerClasses.Count > 0)
        {
            sb.AppendLine("-- HARD BLOCKERS --");
            sb.AppendLine("  These classes are referenced by source-only exports but don't exist in target's");
            sb.AppendLine("  name table. The 1.52 engine binary doesn't know how to instantiate them, so the");
            sb.AppendLine("  exports that use them have to be DROPPED from the output (cosmetic UI-only ones");
            sb.AppendLine("  are typically safe to drop).");
            foreach (var c in hardBlockerClasses) sb.AppendLine($"    {c}");
            sb.AppendLine();
        }

        sb.AppendLine("-- Source-only exports (visual-upgrade payload) --");
        sb.AppendLine($"  Total: {exportsToAdd.Count}. These are the actual 1.53 content that today is");
        sb.AppendLine("  NOT in your target. After Phase 2 they will be appended to target's export");
        sb.AppendLine("  table. Cross-references inside their bodies are translated through the new");
        sb.AppendLine("  combined NameTable + ExportTable.");
        var addByClass = exportsToAdd
            .GroupBy(e => e.ClassReferenceNameIndex?.Name ?? "(Class)", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        foreach (var grp in addByClass)
            sb.AppendLine($"    {grp.Key,-40}  count={grp.Count(),4}  totalBytes={grp.Sum(e => e.SerialDataSize):N0}");
        if (exportsToAdd.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Per-export listing:");
            foreach (var e in exportsToAdd.OrderBy(e => e.ClassReferenceNameIndex?.Name).ThenBy(e => e.ObjectNameIndex?.Name))
                sb.AppendLine($"    {e.ClassReferenceNameIndex?.Name,-40}  {e.ObjectNameIndex?.Name,-50}  {e.SerialDataSize,8} bytes");
        }
        sb.AppendLine();

        sb.AppendLine("-- Matched exports needing size-change translation --");
        sb.AppendLine("  These exist on both sides with the same (class, name) but different body sizes.");
        sb.AppendLine("  They typically hold references to the newly-added exports above (e.g. the costume's");
        sb.AppendLine("  UClass + Default__ that point at the new MIC and mesh). Their bodies are taken from");
        sb.AppendLine("  source, translated through the post-extension index tables, and overwrite the target");
        sb.AppendLine("  slot — UpkRepacker handles SerialDataSize change automatically.");
        if (sizeChangedMatched.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var (s, t, d) in sizeChangedMatched)
                sb.AppendLine($"    {t.ClassReferenceNameIndex?.Name,-40}  {t.ObjectNameIndex?.Name,-50}  src={s.SerialDataSize,8}  tgt={t.SerialDataSize,8}  Δ={d:+#;-#;0}");
        }
        sb.AppendLine();

        sb.AppendLine("-- Names to add to NameTable --");
        sb.AppendLine($"  Total: {namesToAdd.Count}. (Showing first 60.)");
        foreach (var n in namesToAdd.Take(60)) sb.AppendLine($"    {n}");
        if (namesToAdd.Count > 60) sb.AppendLine($"    ... and {namesToAdd.Count - 60} more");
        sb.AppendLine();

        sb.AppendLine("-- Imports to add to ImportTable --");
        sb.AppendLine($"  Total: {importsToAdd.Count}. (Showing first 60.)");
        foreach (var n in importsToAdd.Take(60)) sb.AppendLine($"    {n}");
        if (importsToAdd.Count > 60) sb.AppendLine($"    ... and {importsToAdd.Count - 60} more");
        sb.AppendLine();

        sb.AppendLine("-- TFC follow-up --");
        sb.AppendLine($"  Source has {sourceTextureExports} Texture2D exports; {newTextureExports} are source-only.");
        sb.AppendLine("  After the UPK transplant, the new textures' TextureFileCacheGuid + TfcFileName values");
        sb.AppendLine("  must be reachable. Either:");
        sb.AppendLine("    (a) re-serialize each new Texture2D with mips inlined (no TFC dependency), or");
        sb.AppendLine("    (b) append the mip blobs to a target .tfc and add manifest entries for them.");
        sb.AppendLine("  Use the existing TFC Manifest Inspector to identify which textures need this.");
        sb.AppendLine();

        sb.AppendLine("-- Implementation order Phase 2 will follow --");
        sb.AppendLine("  1. Extend target's NameTable with the missing source names.");
        sb.AppendLine("  2. Extend target's ImportTable with the missing source imports (matched by full path).");
        sb.AppendLine("  3. Append source-only exports to target's ExportTable (with bodies translated against");
        sb.AppendLine("     the post-extension tables). DependsTable grows by 4 bytes per added export.");
        sb.AppendLine("  4. Overwrite size-changed matched exports with translated source bodies.");
        sb.AppendLine("  5. Repack: same UpkRepacker path, with grown header.");
        sb.AppendLine("  6. (Optional/later) TFC update for any new streamed textures.");
        sb.AppendLine();
        sb.AppendLine("=== End of Phase 2 plan ===");

        return new Plan { Summary = sb.ToString() };
    }
}
