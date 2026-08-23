using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OmegaAssetStudio;
using OmegaAssetStudio.BackupManager;
using OmegaAssetStudio2.Core.Packages;
using UpkManager.Helpers;
using UpkManager.Repository;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Actually produces an output .upk for a character swap. Phase 1: header
// downgrade only — load the v894 source, change its package version to v868,
// and re-emit the file. Body bytes for every export are preserved
// byte-for-byte by the UpkManager writer (UnrealExportTableEntry
// .WriteObjectBuffer just re-emits UnrealObjectReader.GetBytes()).
//
// Why this might actually work for the AoA Colossus case:
//   - 1.52 and 1.53 share Licensee=3, EngineVersion=10897, CompressionFlags=LZO.
//   - Only the package version field (894 vs 868) differs in the file header.
//   - If the engine's per-class body deserialisers happen to be backward
//     compatible to v894 layout for all classes this UPK actually contains,
//     the file will load.
// Why this might fail:
//   - The v894 cooker may have added new property tags or new bulk-data flags
//     that v868's loader doesn't recognise.
//   - The hard-blocker class 'marveluihudbarconcarcomp' (used by 2 source
//     exports) doesn't exist in 1.52's engine binary — when the loader hits
//     those exports it will fail to resolve the class.
// If it fails, the engine's crash log tells us which class's body format
// changed first, and Phase 2 (per-class body re-serializer for that class)
// becomes the targeted next step.
public sealed class CharacterSwapExecutor
{
    public sealed class Phase1Result
    {
        public string OutputPath { get; init; } = string.Empty;
        public string? TargetBackupPath { get; init; }
        public ushort OriginalVersion { get; init; }
        public ushort TargetVersion { get; init; }
        public int ExportCount { get; init; }
        public long InputBytes { get; init; }
        public long OutputBytes { get; init; }
        public string Summary { get; init; } = string.Empty;
    }

    // Loads source .upk, rewrites only the package version field, saves to
    // outputPath. No per-class re-serialisation. Pure header downgrade.
    // Also auto-backs-up the target file (the live game UPK being replaced)
    // so the user can always restore it after a failed swap.
    public async Task<Phase1Result> ExecutePhase1HeaderDowngradeAsync(
        string sourceUpkPath,
        string outputPath,
        ushort targetPackageVersion,
        Action<string>? log = null,
        string? targetUpkPath = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUpkPath)) throw new ArgumentException("source path", nameof(sourceUpkPath));
        if (string.IsNullOrWhiteSpace(outputPath))    throw new ArgumentException("output path", nameof(outputPath));
        if (!File.Exists(sourceUpkPath))              throw new FileNotFoundException("source upk not found", sourceUpkPath);

        // Don't let the user accidentally overwrite a live game file with a
        // half-baked Phase 1 attempt. The user must point at a fresh path
        // and copy it into place themselves once it's been smoke-tested.
        string sourceFull = Path.GetFullPath(sourceUpkPath);
        string outputFull = Path.GetFullPath(outputPath);
        if (string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output path must differ from source path.");
        if (!string.IsNullOrWhiteSpace(targetUpkPath))
        {
            string tgtFull = Path.GetFullPath(targetUpkPath);
            if (string.Equals(tgtFull, outputFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output path must differ from target path — write to a scratch location and copy in manually after smoke-testing.");
        }

        // Snapshot the live target before doing anything destructive-adjacent.
        // We back up even though we only WRITE to outputPath (not the target
        // itself), because the user's downstream step is to copy output -> target,
        // and a backup here means they always have a known-good restore point
        // sitting right next to the live file before that copy ever happens.
        //
        // Uses the shared BackupFileHelper which enforces the app-wide policy:
        // exactly one pristine .bak per source file, ever. If a .bak already
        // exists for this target it's left untouched and returned as-is —
        // re-running Phase 1 doesn't churn the backup or overwrite the
        // original pristine snapshot.
        string? backupPath = null;
        if (!string.IsNullOrWhiteSpace(targetUpkPath) && File.Exists(targetUpkPath))
        {
            string tgtFull = Path.GetFullPath(targetUpkPath);
            backupPath = BackupFileHelper.CreateBackup(tgtFull);
            string? prior = BackupFileHelper.FindExistingBackup(tgtFull);
            // CreateBackup returns the existing backup if one was already there;
            // log which case we're in so the user knows whether a fresh snapshot
            // was just taken or an existing pristine copy was reused.
            bool reused = prior is not null && string.Equals(prior, backupPath, StringComparison.OrdinalIgnoreCase)
                          && File.GetLastWriteTimeUtc(backupPath) < DateTime.UtcNow.AddSeconds(-2);
            log?.Invoke(reused
                ? $"Backup: existing pristine .bak reused -> {backupPath}"
                : $"Backup: target -> {backupPath}");
        }
        else if (!string.IsNullOrWhiteSpace(targetUpkPath))
        {
            log?.Invoke($"Backup: target file does not exist at {targetUpkPath} — nothing to back up.");
        }

        log?.Invoke($"Phase 1: load {sourceFull}");
        UpkFileRepository repo = new();
        var header = await repo.LoadUpkFile(sourceFull).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);
        // ReadHeaderAsync populates each export's UnrealObjectReader with the
        // decompressed body bytes; without it WriteObjectBuffer would emit
        // empty payloads.

        ushort originalVersion = header.Version;
        long inputBytes = new FileInfo(sourceFull).Length;
        int exportCount = header.ExportTable.Count;

        log?.Invoke($"Phase 1: rewriting version field {originalVersion} -> {targetPackageVersion}");
        header.Version = targetPackageVersion;

        // The save routine recomputes builder offsets via GetBuilderSize(),
        // re-emits the (now v868-tagged) header, and copies each export's
        // raw body bytes back through WriteObjectBuffer. Compression is
        // re-applied because CompressedChunks.Count > 0 trips the
        // SaveCompressedUpkFile branch.
        string? outDir = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);
        log?.Invoke($"Phase 1: save {outputFull}");
        await repo.SaveUpkFile(header, outputFull, log).ConfigureAwait(false);

        long outputBytes = new FileInfo(outputFull).Length;
        log?.Invoke($"Phase 1: done. {exportCount} exports, {inputBytes:N0} bytes -> {outputBytes:N0} bytes.");

        var summary =
            $"Phase 1 header downgrade complete.\n" +
            $"  Source : v{originalVersion}  {sourceFull}  ({inputBytes:N0} bytes)\n" +
            $"  Output : v{targetPackageVersion}  {outputFull}  ({outputBytes:N0} bytes)\n" +
            $"  Exports preserved : {exportCount}\n" +
            (backupPath is null
                ? "  Backup : (target not set — no automatic backup made)\n"
                : $"  Backup : {backupPath}\n  (Restore by copying this .bak file back over the live game file.)\n") +
            "\n" +
            "NEXT STEPS:\n" +
            "  1. Copy the Output file over the live game's file (rename to match the live filename if needed). The target was auto-backed up above.\n" +
            "  2. Launch 1.52. If the costume loads and renders, you're done.\n" +
            "  3. If it crashes or renders broken/black, restore from the backup and report what happened — that tells us which class's body format needs Phase 2 work (real v894->v868 per-class re-serializer).\n\n" +
            "KNOWN RISK: source uses class 'marveluihudbarconcarcomp' that doesn't exist in 1.52's engine binary (two exports: healthconcarui, primaryresourceconcar). If the costume fails to load entirely, this is the most likely cause.";
        return new Phase1Result
        {
            OutputPath = outputFull,
            TargetBackupPath = backupPath,
            OriginalVersion = originalVersion,
            TargetVersion = targetPackageVersion,
            ExportCount = exportCount,
            InputBytes = inputBytes,
            OutputBytes = outputBytes,
            Summary = summary,
        };
    }

    public sealed class Phase1bResult
    {
        public string OutputPath { get; init; } = string.Empty;
        public string? TargetBackupPath { get; init; }
        public int MergedExportCount { get; init; }
        public int SkippedSizeMismatch { get; init; }
        public int SkippedNoSourceMatch { get; init; }
        public int TotalTargetExports { get; init; }
        public List<string> MergedExports { get; } = new();
        public string Summary { get; init; } = string.Empty;
    }

    // Phase 1-B: bisect. Use the TARGET file (known-good v868) as the chassis.
    // For every target export that matches a source export by class+name AND
    // has the same SerialDataSize, splice the source's body bytes into the
    // target's export. Leave all other exports untouched. Re-emit as a new
    // file with the target's package version preserved.
    //
    // Test logic:
    //   - If the resulting file loads in the game = same-size body bytes are
    //     compatible between v894 and v868 for the merged classes. The
    //     visual upgrade still won't be present (because the size-changed
    //     exports — the new MIC, new mesh, new physics — are not merged in),
    //     but it tells us the byte-copy path works. Phase 2 then has to
    //     re-serialize only the size-changed exports.
    //   - If it crashes = even same-size bodies have format drift. Bisect
    //     further: comment out class buckets to narrow down which class is
    //     the culprit.
    // If classAllowlist is non-empty, only exports whose class name is in
    // the set get their body bytes spliced from source. Use this to bisect
    // which class's body is responsible for an in-game crash.
    public async Task<Phase1bResult> ExecutePhase1bExportBodyMergeAsync(
        string sourceUpkPath,
        string targetUpkPath,
        string outputPath,
        Action<string>? log = null,
        HashSet<string>? classAllowlist = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUpkPath)) throw new ArgumentException("source path", nameof(sourceUpkPath));
        if (string.IsNullOrWhiteSpace(targetUpkPath)) throw new ArgumentException("target path", nameof(targetUpkPath));
        if (string.IsNullOrWhiteSpace(outputPath))    throw new ArgumentException("output path", nameof(outputPath));
        if (!File.Exists(sourceUpkPath))              throw new FileNotFoundException("source upk not found", sourceUpkPath);
        if (!File.Exists(targetUpkPath))              throw new FileNotFoundException("target upk not found", targetUpkPath);

        string sourceFull = Path.GetFullPath(sourceUpkPath);
        string targetFull = Path.GetFullPath(targetUpkPath);
        string outputFull = Path.GetFullPath(outputPath);
        if (string.Equals(targetFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output path must differ from target path — write to a scratch location and copy in manually after smoke-testing.");
        if (string.Equals(sourceFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output path must differ from source path.");

        // Auto-backup the live target via the shared one-pristine-.bak policy.
        string? backupPath = null;
        if (File.Exists(targetFull))
        {
            backupPath = BackupFileHelper.CreateBackup(targetFull);
            log?.Invoke($"Backup: target -> {backupPath}");
        }

        log?.Invoke($"Phase 1-B: load source {sourceFull}");
        UpkFileRepository repo = new();
        var srcHeader = await repo.LoadUpkFile(sourceFull).ConfigureAwait(false);
        await srcHeader.ReadHeaderAsync(null).ConfigureAwait(false);

        log?.Invoke($"Phase 1-B: load target {targetFull}");
        var tgtHeader = await repo.LoadUpkFile(targetFull).ConfigureAwait(false);
        await tgtHeader.ReadHeaderAsync(null).ConfigureAwait(false);

        // Bucket source exports by (className::objectName). Same key shape as
        // the analyzer — same-name duplicates within a class go into a queue
        // and are popped in order.
        var srcBuckets = new Dictionary<string, Queue<UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in srcHeader.ExportTable)
        {
            string key = $"{s.ClassReferenceNameIndex?.Name ?? "(Class)"}::{s.ObjectNameIndex?.Name ?? string.Empty}";
            if (!srcBuckets.TryGetValue(key, out var q))
            {
                q = new Queue<UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry>();
                srcBuckets[key] = q;
            }
            q.Enqueue(s);
        }

        // Build the index translation tables ONCE for the file pair. Every
        // merged export reuses these maps so we don't rebuild per-export.
        var translator = new IndexTranslator(srcHeader, tgtHeader);
        log?.Invoke($"Index translator: {translator.NamesMissingFromTarget.Count} src names missing from target name table");
        log?.Invoke($"Index translator: {translator.ImportsMissingFromTarget.Count} src imports missing from target import table");
        log?.Invoke($"Index translator: {translator.ExportsMissingFromTarget.Count} src exports missing from target export table");

        var rewriter = new PropertyTagRewriter(translator);
        var mergedExports = new List<string>();
        var rewriteIssues = new List<string>();
        int merged = 0, sizeMismatch = 0, noMatch = 0, filteredOut = 0, rewriteFailed = 0;
        bool hasAllowlist = classAllowlist is { Count: > 0 };

        // Build the per-export body list that UpkRepacker consumes. Default
        // every export to its ORIGINAL bytes (TableIndex maps to ExportTable
        // position). We then overwrite specific entries with translated source
        // bytes for matched, allowlisted, same-size exports.
        var exportBuffers = new List<UpkRepacker.ExportBuffer>(tgtHeader.ExportTable.Count);
        foreach (var tgt in tgtHeader.ExportTable)
            exportBuffers.Add(new UpkRepacker.ExportBuffer(tgt.UnrealObjectReader.GetBytes(), Array.Empty<UpkRepacker.BulkDataPatch>()));

        for (int idx = 0; idx < tgtHeader.ExportTable.Count; idx++)
        {
            var tgt = tgtHeader.ExportTable[idx];
            string cls = tgt.ClassReferenceNameIndex?.Name ?? "(Class)";
            string nm  = tgt.ObjectNameIndex?.Name ?? string.Empty;
            string key = $"{cls}::{nm}";
            if (srcBuckets.TryGetValue(key, out var q) && q.Count > 0)
            {
                var src = q.Dequeue();
                if (src.SerialDataSize == tgt.SerialDataSize && src.UnrealObjectReader is not null)
                {
                    if (hasAllowlist && !classAllowlist!.Contains(cls))
                    {
                        filteredOut++;
                        continue;
                    }
                    byte[] srcBytes = src.UnrealObjectReader.GetBytes();
                    var rewrite = rewriter.RewriteBody(srcBytes, $"{cls}::{nm}");
                    if (!rewrite.Success)
                    {
                        rewriteFailed++;
                        foreach (var issue in rewrite.Issues.GetRange(0, Math.Min(rewrite.Issues.Count, 5)))
                            rewriteIssues.Add(issue);
                        continue;
                    }
                    // Overwrite the default (target's own bytes) with the
                    // translated source bytes for this export slot.
                    exportBuffers[idx] = new UpkRepacker.ExportBuffer(rewrite.Body, Array.Empty<UpkRepacker.BulkDataPatch>());
                    merged++;
                    mergedExports.Add($"{cls}::{nm}  ({src.SerialDataSize} bytes, {rewrite.PropertyTagsTranslated} tags translated, {rewrite.Issues.Count} warnings)");
                    foreach (var issue in rewrite.Issues.GetRange(0, Math.Min(rewrite.Issues.Count, 2)))
                        rewriteIssues.Add(issue);
                }
                else
                {
                    sizeMismatch++;
                }
            }
            else
            {
                noMatch++;
            }
        }
        log?.Invoke($"Phase 1-B: rewrite-failed (skipped) {rewriteFailed}");
        log?.Invoke($"Phase 1-B: merged {merged}, size-mismatch {sizeMismatch}, no-match {noMatch}");

        string? outDir = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);
        log?.Invoke($"Phase 1-B: save {outputFull}");
        // PROVEN WRITE PATH: UpkRepacker preserves the original file's header,
        // tables, and non-replaced bodies byte-for-byte. Only the spliced
        // export bodies change. This is the same code path the Texture and
        // Material editors use successfully today. We do NOT use SaveUpkFile
        // because its full re-serialization isn't clean-round-trip safe for
        // this file (confirmed via null-swap reproduction: load + save of an
        // unmodified target produces a file the engine rejects).
        byte[] originalTargetBytes = await File.ReadAllBytesAsync(targetFull).ConfigureAwait(false);

        // Written plainly even when the original was packed. The repacker
        // writes absolute positions into the file — each export's SerialOffset
        // and every bulk-data offset — and packing the result moves what they
        // point at. The file is larger than the one it replaces and that is the
        // price of one the game can load.
        byte[] repacked = tgtHeader.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalTargetBytes, tgtHeader, exportBuffers)
            : UpkRepacker.Repack(originalTargetBytes, tgtHeader, exportBuffers);

        await File.WriteAllBytesAsync(outputFull, repacked).ConfigureAwait(false);
        long outBytes = new FileInfo(outputFull).Length;

        var summaryLines = new List<string>
        {
            "Phase 1-B export-body merge complete.",
            $"  Source : {sourceFull}",
            $"  Target : {targetFull}",
            $"  Output : {outputFull}  ({outBytes:N0} bytes)",
            backupPath is null
                ? "  Backup : (target did not exist — no backup made)"
                : $"  Backup : {backupPath}",
            string.Empty,
            "-- Merge stats --",
            $"  Total target exports         : {tgtHeader.ExportTable.Count}",
            $"  Merged from source (same size): {merged}",
            $"  Skipped (size mismatch)      : {sizeMismatch}",
            $"  Skipped (no source match)    : {noMatch}",
            $"  Skipped (filtered by allowlist)     : {filteredOut}",
            $"  Skipped (index rewrite failed)      : {rewriteFailed}",
            hasAllowlist
                ? $"  Class allowlist active ({classAllowlist!.Count} classes): {string.Join(", ", classAllowlist)}"
                : "  Class allowlist: (none — all matching classes merged)",
            string.Empty,
            "-- Index translator coverage --",
            $"  Source names missing from target name table   : {translator.NamesMissingFromTarget.Count}",
            $"  Source imports missing from target imports    : {translator.ImportsMissingFromTarget.Count}",
            $"  Source exports missing from target exports    : {translator.ExportsMissingFromTarget.Count}",
            string.Empty,
            "WHAT THIS FILE IS:",
            "  Target chassis (v868) with same-size source body bytes spliced in",
            "  for every matching export. The visual upgrade (new MIC, new mesh,",
            "  new physics, new textures) is NOT included — those exports have",
            "  different sizes and were skipped.",
            string.Empty,
            "NEXT STEPS:",
            "  1. Copy Output over the live target file (backup already taken above).",
            "  2. Launch 1.52, equip the costume.",
            "  3. Report the result:",
            "     - LOADS OK = same-size body bytes are v894/v868 compatible.",
            "       Path forward: build per-class re-serializers only for the",
            "       size-changed exports (the visual-upgrade ones).",
            "     - STILL CRASHES = even same-size bodies drifted between versions.",
            "       Path forward: bisect further by class, or accept that 1.53",
            "       can't be made to work in 1.52 without much heavier engineering.",
        };
        // Tack on the most informative rewrite warnings — capped so the report
        // stays readable. Empty list means clean translation across all merged.
        if (rewriteIssues.Count > 0)
        {
            summaryLines.Add(string.Empty);
            summaryLines.Add($"-- Index-rewrite warnings (first 30 of {rewriteIssues.Count}) --");
            for (int i = 0; i < Math.Min(rewriteIssues.Count, 30); i++)
                summaryLines.Add("  " + rewriteIssues[i]);
        }
        var result = new Phase1bResult
        {
            OutputPath = outputFull,
            TargetBackupPath = backupPath,
            MergedExportCount = merged,
            SkippedSizeMismatch = sizeMismatch,
            SkippedNoSourceMatch = noMatch,
            TotalTargetExports = tgtHeader.ExportTable.Count,
            Summary = string.Join('\n', summaryLines),
        };
        foreach (var ml in mergedExports)
            result.MergedExports.Add(ml);
        foreach (var line in summaryLines)
            log?.Invoke(line);
        return result;
    }
}
