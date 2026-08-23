using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Analyzes whether a "newer-version" character UPK can be made to drop in as
// a replacement for an "older-version" character UPK (e.g. transplant a 1.53
// AoA Colossus onto a 1.52 install). Read-only diagnostic — writes nothing.
//
// What it produces:
//   - Per-export transfer plan (AddNew / KeepFromTarget / DirectCopyViable /
//     NeedsReserializer / SameVersionSwap).
//   - Per-class population diff (source has N, target has M).
//   - List of classes source uses that the target's name table doesn't
//     contain (true blockers).
//   - Recommendation string aimed at a non-engine-dev user.
//
// What it does NOT do (yet):
//   - Actually rewrite the target file. That's tasks #14–16 (real per-class
//     re-serializers for SkeletalMesh, MaterialInstanceConstant, Texture2D).
//     This service is the precondition for those: it tells us which of those
//     re-serializers a given swap actually needs.
public sealed class CharacterSwapService
{
    // Classes whose binary body layout is known/assumed to be stable enough
    // across the 1.4x..1.5x UE3 fork that a byte-copy from
    // source export → new target export (with target-side name/import
    // remapping) is plausible. Conservative list — when in doubt, mark as
    // needing a re-serializer rather than silently corrupt data.
    private static readonly HashSet<string> VersionStableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "rb_bodyinstance",
        "rb_bodysetup",
        "rb_constraintinstance",
        "rb_constraintsetup",
        "physicalmaterial",
        "skeletalmeshsocket",
        "cylindercomponent",
        "package",
        "objectreferencer",
    };

    // Classes whose body layout is known to vary across UE3 versions enough
    // that a byte copy will corrupt them. These need real re-serializers.
    private static readonly HashSet<string> KnownFormatChangedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "skeletalmesh",
        "skeletalmeshcomponent",
        "texture2d",
        "material",
        "materialinstanceconstant",
        "physicsasset",
        "physicsassetinstance",
        "animset",
        "animsequence",
    };

    public async Task<CharacterSwapReport> AnalyzeAsync(string sourceUpkPath, string targetUpkPath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(sourceUpkPath)) throw new ArgumentException("source path", nameof(sourceUpkPath));
        if (string.IsNullOrWhiteSpace(targetUpkPath)) throw new ArgumentException("target path", nameof(targetUpkPath));
        if (!File.Exists(sourceUpkPath)) throw new FileNotFoundException("source upk not found", sourceUpkPath);
        if (!File.Exists(targetUpkPath)) throw new FileNotFoundException("target upk not found", targetUpkPath);

        log?.Invoke($"Character swap analyze: source = {sourceUpkPath}");
        log?.Invoke($"Character swap analyze: target = {targetUpkPath}");

        UpkFileRepository repo = new();
        var srcHeader = await repo.LoadUpkFile(sourceUpkPath).ConfigureAwait(false);
        var tgtHeader = await repo.LoadUpkFile(targetUpkPath).ConfigureAwait(false);
        await srcHeader.ReadHeaderAsync(null).ConfigureAwait(false);
        await tgtHeader.ReadHeaderAsync(null).ConfigureAwait(false);

        CharacterSwapReport report = new()
        {
            SourceUpkPath = sourceUpkPath,
            TargetUpkPath = targetUpkPath,
            SourceVersion = srcHeader.Version,
            TargetVersion = tgtHeader.Version,
            SourceExportCount = srcHeader.ExportTable.Count,
            TargetExportCount = tgtHeader.ExportTable.Count,
            SourceNameCount = srcHeader.NameTable.Count,
            TargetNameCount = tgtHeader.NameTable.Count,
            SourceImportCount = srcHeader.ImportTable.Count,
            TargetImportCount = tgtHeader.ImportTable.Count,
        };

        bool sameVersion = report.SourceVersion == report.TargetVersion;

        // Per-class population for both sides.
        var srcByClass = srcHeader.ExportTable
            .GroupBy(e => e.ClassReferenceNameIndex?.Name ?? "(Class)", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var tgtByClass = tgtHeader.ExportTable
            .GroupBy(e => e.ClassReferenceNameIndex?.Name ?? "(Class)", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var allClassNames = new HashSet<string>(srcByClass.Keys, StringComparer.OrdinalIgnoreCase);
        allClassNames.UnionWith(tgtByClass.Keys);
        foreach (var cls in allClassNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            report.ClassPopulations.Add(new ClassPopulation
            {
                ClassName = cls,
                SourceCount = srcByClass.GetValueOrDefault(cls),
                TargetCount = tgtByClass.GetValueOrDefault(cls),
                VersionStable = VersionStableClasses.Contains(cls) && !KnownFormatChangedClasses.Contains(cls),
            });
        }

        // Target name-table lookup for "does target know this class?" check.
        var tgtNames = new HashSet<string>(
            tgtHeader.NameTable.Select(n => n.Name?.String ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var srcCls in srcByClass.Keys)
        {
            if (!tgtNames.Contains(srcCls))
                report.SourceClassesMissingFromTarget.Add(srcCls);
        }

        // Bucket target exports by (class, objectName) so source can match.
        var tgtBuckets = new Dictionary<string, Queue<UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tgt in tgtHeader.ExportTable)
        {
            string key = MakeKey(tgt);
            if (!tgtBuckets.TryGetValue(key, out var q))
            {
                q = new Queue<UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry>();
                tgtBuckets[key] = q;
            }
            q.Enqueue(tgt);
        }

        // Walk source exports — produce a per-export feasibility entry.
        foreach (var src in srcHeader.ExportTable)
        {
            string objName = src.ObjectNameIndex?.Name ?? string.Empty;
            string clsName = src.ClassReferenceNameIndex?.Name ?? "(Class)";
            string key = MakeKey(src);

            int srcSize = src.SerialDataSize;
            if (tgtBuckets.TryGetValue(key, out var q) && q.Count > 0)
            {
                var tgt = q.Dequeue();
                int tgtSize = tgt.SerialDataSize;
                SwapFeasibility feas;
                string notes;
                if (sameVersion)
                {
                    feas = SwapFeasibility.SameVersionSwap;
                    notes = "Same package version — direct byte copy is safe.";
                }
                else if (KnownFormatChangedClasses.Contains(clsName))
                {
                    feas = SwapFeasibility.NeedsReserializer;
                    notes = $"Class '{clsName}' body layout differs v{report.SourceVersion}→v{report.TargetVersion}. Needs per-class re-serializer.";
                }
                else if (VersionStableClasses.Contains(clsName))
                {
                    feas = SwapFeasibility.DirectCopyViable;
                    notes = $"Class '{clsName}' assumed version-stable for MH 1.4x..1.5x.";
                }
                else
                {
                    feas = SwapFeasibility.NeedsReserializer;
                    notes = $"Class '{clsName}' not on the version-stable allowlist. Conservative: assume re-serializer needed.";
                }
                report.Entries.Add(new CharacterSwapEntry
                {
                    ObjectName = objName,
                    ClassName = clsName,
                    Feasibility = feas,
                    SourceSize = srcSize,
                    TargetSize = tgtSize,
                    Notes = notes,
                });
            }
            else
            {
                report.Entries.Add(new CharacterSwapEntry
                {
                    ObjectName = objName,
                    ClassName = clsName,
                    Feasibility = SwapFeasibility.AddNew,
                    SourceSize = srcSize,
                    TargetSize = 0,
                    Notes = report.SourceClassesMissingFromTarget.Contains(clsName)
                        ? $"BLOCKER: target's name table has no '{clsName}' — class can't be referenced."
                        : "New object in target. Will be appended; ensure referenced imports resolve.",
                });
            }
        }
        // Anything left in target buckets is target-only.
        foreach (var (key, q) in tgtBuckets)
        {
            while (q.Count > 0)
            {
                var tgt = q.Dequeue();
                report.Entries.Add(new CharacterSwapEntry
                {
                    ObjectName = tgt.ObjectNameIndex?.Name ?? string.Empty,
                    ClassName = tgt.ClassReferenceNameIndex?.Name ?? "(Class)",
                    Feasibility = SwapFeasibility.KeepFromTarget,
                    SourceSize = 0,
                    TargetSize = tgt.SerialDataSize,
                    Notes = "Present only in target — would have to be preserved from the original file.",
                });
            }
        }

        // Roll up.
        foreach (var e in report.Entries)
        {
            switch (e.Feasibility)
            {
                case SwapFeasibility.AddNew:             report.AddNewCount++; break;
                case SwapFeasibility.KeepFromTarget:     report.KeepFromTargetCount++; break;
                case SwapFeasibility.DirectCopyViable:   report.DirectCopyViableCount++; break;
                case SwapFeasibility.NeedsReserializer:  report.NeedsReserializerCount++; break;
                case SwapFeasibility.SameVersionSwap:    report.SameVersionSwapCount++; break;
            }
        }

        report.SummaryText =
            $"v{report.SourceVersion}→v{report.TargetVersion}. " +
            $"SameVersion {report.SameVersionSwapCount}, DirectCopy {report.DirectCopyViableCount}, " +
            $"NeedsReserializer {report.NeedsReserializerCount}, AddNew {report.AddNewCount}, " +
            $"KeepFromTarget {report.KeepFromTargetCount}.";

        report.Recommendation = BuildRecommendation(report);
        log?.Invoke($"Character swap analyze complete: {report.SummaryText}");
        return report;
    }

    private static string MakeKey(UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry e)
    {
        string cls = e.ClassReferenceNameIndex?.Name ?? "(Class)";
        string nm = e.ObjectNameIndex?.Name ?? string.Empty;
        return $"{cls}::{nm}";
    }

    private static string BuildRecommendation(CharacterSwapReport r)
    {
        if (!r.VersionMismatch)
        {
            if (r.SourceClassesMissingFromTarget.Count > 0)
                return "Same package version, but source references classes the target doesn't know. Likely art-only — investigate the listed classes before swapping.";
            return "Same package version on both files. A direct file replacement is the simplest path: back up the target, drop the source in renamed to the target's filename, launch and verify.";
        }
        if (r.NeedsReserializerCount == 0 && r.SourceClassesMissingFromTarget.Count == 0)
            return $"Versions differ (v{r.SourceVersion}→v{r.TargetVersion}) but every export is on the version-stable allowlist. Try a direct file replacement first; if it crashes, a re-serializer is needed.";
        return
            $"Versions differ (v{r.SourceVersion}→v{r.TargetVersion}). Direct file replacement will be rejected by the v{r.TargetVersion} package loader. " +
            $"To actually use the source's art, build re-serializers for the {r.NeedsReserializerCount} format-changed exports (see per-class breakdown). " +
            "The alternative is to keep the existing target file as-is — it's already a working v" + r.TargetVersion + " version of this asset.";
    }
}
