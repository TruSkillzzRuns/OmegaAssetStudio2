using OmegaAssetStudio.TextureManager;

namespace OmegaAssetStudio.TexturePreview;

public sealed class TfcInventoryEntry
{
    public string Name { get; init; } = string.Empty;        // e.g. "CharTextures"
    public string FilePath { get; init; } = string.Empty;    // full .tfc path
    public bool FileExists { get; init; }                    // tfc file actually present on disk
    public long FileSize { get; init; }                      // bytes on disk
    public int TextureCount { get; init; }                   // entries that reference this tfc
    public long UsedBytes { get; init; }                     // sum of all mip sizes referenced
    public long WastedBytes => Math.Max(0, FileSize - UsedBytes);
    public double FragmentationPercent => FileSize > 0 ? (double)WastedBytes / FileSize * 100.0 : 0.0;
    public bool IsOrphanOnDisk { get; init; }                // .tfc file exists but no manifest entries reference it
    public bool IsMissingOnDisk { get; init; }               // manifest references it but file isn't present

    public string SizeText => FormatBytes(FileSize);
    public string UsedText => FormatBytes(UsedBytes);
    public string WastedText => FormatBytes(WastedBytes);
    public string FragmentationText => $"{FragmentationPercent:F1}%";
    public string SummaryText =>
        IsMissingOnDisk ? "MISSING ON DISK"
        : IsOrphanOnDisk ? "ORPHAN (no manifest refs)"
        : $"{TextureCount} texture(s), {FragmentationText} fragmented";

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024L * 1024) return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024.0):F1} MB";
        return $"{b / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public static class TfcInventoryService
{
    /// <summary>
    /// Builds an inventory of every TFC file referenced by the loaded manifest plus any
    /// loose .tfc files in the manifest folder. Read-only — does not modify any file.
    /// </summary>
    public static IReadOnlyList<TfcInventoryEntry> Scan()
    {
        TextureManifest manifest = TextureManifest.Instance;
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.ManifestPath) || !Directory.Exists(manifest.ManifestPath))
            return Array.Empty<TfcInventoryEntry>();

        // Aggregate manifest references by TFC name.
        Dictionary<string, (int count, long used)> aggregates = new(StringComparer.OrdinalIgnoreCase);
        foreach (TextureEntry entry in manifest.Entries.Values)
        {
            if (entry?.Data == null || string.IsNullOrWhiteSpace(entry.Data.TextureFileName))
                continue;
            string name = entry.Data.TextureFileName;

            long used = 0;
            if (entry.Data.Maps != null)
                foreach (TextureMipMap m in entry.Data.Maps)
                    used += m.Size;

            if (aggregates.TryGetValue(name, out var existing))
                aggregates[name] = (existing.count + 1, existing.used + used);
            else
                aggregates[name] = (1, used);
        }

        // Enumerate .tfc files on disk in the manifest folder.
        HashSet<string> onDiskFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(manifest.ManifestPath, "*.tfc", SearchOption.TopDirectoryOnly))
            onDiskFiles.Add(Path.GetFileNameWithoutExtension(path));

        List<TfcInventoryEntry> results = new(aggregates.Count + onDiskFiles.Count);

        // Manifest-referenced TFCs (may or may not exist on disk).
        foreach (var (name, agg) in aggregates)
        {
            string filePath = Path.Combine(manifest.ManifestPath, name + ".tfc");
            bool exists = File.Exists(filePath);
            long fileSize = exists ? new FileInfo(filePath).Length : 0L;
            results.Add(new TfcInventoryEntry
            {
                Name = name,
                FilePath = filePath,
                FileExists = exists,
                FileSize = fileSize,
                TextureCount = agg.count,
                UsedBytes = agg.used,
                IsMissingOnDisk = !exists,
            });
            onDiskFiles.Remove(name);
        }

        // Orphan TFCs (on disk but not referenced by any manifest entry).
        foreach (string name in onDiskFiles)
        {
            string filePath = Path.Combine(manifest.ManifestPath, name + ".tfc");
            long fileSize = new FileInfo(filePath).Length;
            results.Add(new TfcInventoryEntry
            {
                Name = name,
                FilePath = filePath,
                FileExists = true,
                FileSize = fileSize,
                TextureCount = 0,
                UsedBytes = 0,
                IsOrphanOnDisk = true,
            });
        }

        results.Sort((a, b) => b.FileSize.CompareTo(a.FileSize));
        return results;
    }
}
