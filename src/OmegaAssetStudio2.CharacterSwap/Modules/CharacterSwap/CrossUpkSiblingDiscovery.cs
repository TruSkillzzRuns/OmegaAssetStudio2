using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio.WinUI.Services;
using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Walks a costume's IMPORT table, asks the cross-UPK reference index where
// each Material / MaterialInstanceConstant / Texture2D import actually
// LIVES (the real on-disk .upk that EXPORTS it), and pairs each sibling
// between source and target builds. Output drives the multi-UPK Phase 2
// transplant: every paired sibling gets its own Phase 2 run so the new
// 1.53 textures/MICs land in the right files.
//
// Why we need this: many 1.52 costumes don't carry their own MICs — they
// IMPORT them from a sibling cooked-content UPK. Phase 2 alone patches
// only the player UPK, leaving the sibling untouched → costume renders
// with the OLD material chain even when the new MICs are present locally.
public static class CrossUpkSiblingDiscovery
{
    public sealed record SiblingPair(
        string ImportPath,         // e.g. "<package>.<object>" from target's import table
        string ClassName,          // "MaterialInstanceConstant" / "Material" / "Texture2D"
        string TargetSiblingPath,  // absolute path of target build's sibling .upk
        string SourceSiblingPath); // absolute path of source build's sibling .upk

    public sealed record DiscoveryResult(
        IReadOnlyList<SiblingPair> Pairs,
        IReadOnlyList<string> UnresolvedImports);  // imports we couldn't locate

    private static readonly HashSet<string> RelevantImportClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Material",
        "MaterialInstanceConstant",
        "MaterialInstanceTimeVarying",
        "MaterialInterface",
        "Texture2D",
    };

    public static DiscoveryResult Discover(
        UnrealHeader targetHeader,
        string targetCookedDir,
        string sourceCookedDir,
        Action<string>? log = null)
    {
        var pairs = new List<SiblingPair>();
        var unresolved = new List<string>();

        // Collect the import paths we care about. Skip anything whose outer
        // chain ends in the target's own package — those are inner-export
        // references, not external sibling refs.
        string targetPackage = Path.GetFileNameWithoutExtension(
            targetHeader.FullFilename ?? "").Replace("_SF", "", StringComparison.OrdinalIgnoreCase);
        var relevantImports = new List<(string path, string cls)>();
        foreach (var im in targetHeader.ImportTable)
        {
            string cls = im.ClassNameIndex?.Name ?? string.Empty;
            if (!RelevantImportClasses.Contains(cls)) continue;
            string path = im.GetPathName() ?? string.Empty;
            if (string.IsNullOrEmpty(path)) continue;
            // Skip same-package self-imports.
            if (!string.IsNullOrEmpty(targetPackage)
                && path.StartsWith(targetPackage + ".", StringComparison.OrdinalIgnoreCase))
                continue;
            relevantImports.Add((path, cls));
        }
        log?.Invoke($"[crossupk] {relevantImports.Count} cross-package material/texture imports to resolve");

        if (relevantImports.Count == 0)
            return new DiscoveryResult(pairs, unresolved);

        // Look each import up in the cross-package reference index. The index
        // (mh152upk.db) is rooted at the TARGET cooked dir — it tells us
        // which target .upk exports each import path.
        var hits = PackageReferenceQueryService.FindExportLocations(
            targetCookedDir,
            relevantImports.Select(r => r.path));
        // Build path → upkFileName map. One import can hit multiple UPKs if
        // the same path is exported in several files (rare for these classes);
        // pick the first.
        var resolvedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
            if (!resolvedByPath.ContainsKey(hit.ObjectPath))
                resolvedByPath[hit.ObjectPath] = hit.UpkFileName;

        // For each resolved sibling UPK, pair it against the source build's
        // copy of the SAME file. Source must have an equivalent .upk at the
        // same leaf name for the transplant to have anything to source from.
        var dedupedSiblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, cls) in relevantImports)
        {
            if (!resolvedByPath.TryGetValue(path, out string? upkFile))
            {
                unresolved.Add($"{cls} {path}");
                continue;
            }
            if (!dedupedSiblings.Add(upkFile)) continue;

            string tgtSibling = Path.Combine(targetCookedDir, upkFile);
            string srcSibling = Path.Combine(sourceCookedDir, upkFile);
            if (!File.Exists(tgtSibling) || !File.Exists(srcSibling))
            {
                unresolved.Add($"{cls} {path} → {upkFile} (source twin missing)");
                continue;
            }
            pairs.Add(new SiblingPair(path, cls, tgtSibling, srcSibling));
            log?.Invoke($"[crossupk] sibling {upkFile} (covers '{path}')");
        }

        log?.Invoke($"[crossupk] discovered {pairs.Count} sibling pair(s); {unresolved.Count} unresolved");
        return new DiscoveryResult(pairs, unresolved);
    }
}
