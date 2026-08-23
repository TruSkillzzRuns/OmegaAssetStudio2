using System.Text.Json;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Snapshots;

// Safety net for every Material Editor write. Before any UPK rewrite, the
// caller hands the pristine bytes here; we drop them in
// %AppData%\OmegaAssetStudio\MaterialEditor\snapshots\<upkBaseName>\
// with an ISO-style timestamp and a sidecar JSON describing what's about to
// change. On disk:
//   <upkBaseName>__20260530-143025-187.upk        (raw original bytes)
//   <upkBaseName>__20260530-143025-187.json       (label, reason, export, hash)
//
// Restore = read the .upk back over the live game-file path. The store keeps
// up to MaxPerUpk snapshots per package (oldest pruned) so disk doesn't grow
// without bound. Entirely independent of the existing PinnedSnapshot used
// for A/B compare in the view model.
public sealed class MaterialAutoSnapshotStore
{
    public const int MaxPerUpk = 25;

    private static readonly string s_root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmegaAssetStudio", "MaterialEditor", "snapshots");

    public sealed record SnapshotMeta(
        string UpkPath,
        string ExportPath,
        string Label,
        string Reason,
        DateTime CapturedUtc,
        long OriginalSize);

    public sealed record SnapshotEntry(string BinPath, string MetaPath, SnapshotMeta Meta);

    public string Capture(string upkPath, string exportPath, string label, string reason)
    {
        if (string.IsNullOrWhiteSpace(upkPath) || !File.Exists(upkPath))
            throw new FileNotFoundException("UPK to snapshot not found.", upkPath);

        string baseName = SanitizeFolder(Path.GetFileNameWithoutExtension(upkPath));
        string dir = Path.Combine(s_root, baseName);
        Directory.CreateDirectory(dir);

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string binPath = Path.Combine(dir, $"{baseName}__{stamp}.upk");
        string metaPath = Path.Combine(dir, $"{baseName}__{stamp}.json");

        File.Copy(upkPath, binPath, overwrite: false);
        var meta = new SnapshotMeta(
            UpkPath: upkPath,
            ExportPath: exportPath ?? "",
            Label: label ?? "",
            Reason: reason ?? "",
            CapturedUtc: DateTime.UtcNow,
            OriginalSize: new FileInfo(binPath).Length);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        PruneOldest(dir, MaxPerUpk);
        return binPath;
    }

    public IReadOnlyList<SnapshotEntry> ListForUpk(string upkPath)
    {
        var result = new List<SnapshotEntry>();
        if (string.IsNullOrWhiteSpace(upkPath)) return result;
        string baseName = SanitizeFolder(Path.GetFileNameWithoutExtension(upkPath));
        string dir = Path.Combine(s_root, baseName);
        if (!Directory.Exists(dir)) return result;

        foreach (var bin in Directory.EnumerateFiles(dir, "*.upk").OrderByDescending(p => p))
        {
            string meta = Path.ChangeExtension(bin, ".json");
            if (!File.Exists(meta)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<SnapshotMeta>(File.ReadAllText(meta));
                if (m is not null) result.Add(new SnapshotEntry(bin, meta, m));
            }
            catch { /* skip corrupted sidecars */ }
        }
        return result;
    }

    public void Restore(SnapshotEntry entry)
    {
        if (!File.Exists(entry.BinPath))
            throw new FileNotFoundException("Snapshot binary missing.", entry.BinPath);
        // Restore writes via stage+rename for safety, identical to Omega Manager.
        string tmp = entry.Meta.UpkPath + ".omtmp";
        File.Copy(entry.BinPath, tmp, overwrite: true);
        File.Move(tmp, entry.Meta.UpkPath, overwrite: true);
    }

    public void Delete(SnapshotEntry entry)
    {
        try { if (File.Exists(entry.BinPath)) File.Delete(entry.BinPath); } catch { }
        try { if (File.Exists(entry.MetaPath)) File.Delete(entry.MetaPath); } catch { }
    }

    private static void PruneOldest(string dir, int keep)
    {
        var bins = Directory.EnumerateFiles(dir, "*.upk").OrderByDescending(p => p).ToList();
        if (bins.Count <= keep) return;
        foreach (var bin in bins.Skip(keep))
        {
            try { File.Delete(bin); } catch { }
            string meta = Path.ChangeExtension(bin, ".json");
            try { if (File.Exists(meta)) File.Delete(meta); } catch { }
        }
    }

    private static string SanitizeFolder(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "unnamed" : n;
    }
}
