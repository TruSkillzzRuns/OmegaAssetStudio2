using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OmegaAssetStudio.WinUI.Services;

// Auto-snapshot UPKs before every destructive write. Lives alongside
// BackupFileHelper (one-shot pristine .bak) but does the opposite job:
// a rolling per-file history of timestamped snapshots so the user can scrub
// backwards through their edits, not just back to the original.
//
// Storage: %LocalAppData%\OmegaAssetStudio\EditHistory\<sha-of-upk-path>\<ticks>.upk
// Per-file cap: 10 newest snapshots (oldest pruned on add). Per-file metadata
// in index.json records original path, timestamp, byte size, and tool tag.
public static class EditHistoryService
{
    private const int PerFileSnapshotCap = 10;

    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio", "EditHistory");

    public sealed class HistoryEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string SnapshotPath { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public long SizeBytes { get; set; }
        public string ToolTag { get; set; } = string.Empty;

        // Pretty display for UI.
        public string DisplayTimestamp => TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string DisplaySize
        {
            get
            {
                long b = SizeBytes;
                if (b < 1024) return b + " B";
                if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
                if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024.0).ToString("0.#") + " MB";
                return (b / 1024.0 / 1024.0 / 1024.0).ToString("0.##") + " GB";
            }
        }
    }

    private sealed class IndexFile
    {
        public string OriginalPath { get; set; } = string.Empty;
        public List<HistoryEntry> Entries { get; set; } = new();
    }

    // Hash the absolute UPK path to a stable short token for the per-file
    // directory name. Avoids unsafe characters and casing pitfalls.
    private static string HashKey(string upkPath)
    {
        string norm = (upkPath ?? string.Empty).Trim().ToLowerInvariant().Replace('\\', '/');
        const uint fnvOffset = 2166136261;
        const uint fnvPrime = 16777619;
        uint hash = fnvOffset;
        foreach (char c in norm)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return hash.ToString("x8");
    }

    private static string GetFileDir(string upkPath) => Path.Combine(RootDir, HashKey(upkPath));
    private static string GetIndexPath(string upkPath) => Path.Combine(GetFileDir(upkPath), "index.json");

    // Captures a snapshot of `upkPath` into the history dir. Called before any
    // committing write. Returns the snapshot path, or null if no snapshot was
    // taken (e.g. file missing).
    public static string? Snapshot(string upkPath, string toolTag = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(upkPath) || !File.Exists(upkPath))
                return null;

            string dir = GetFileDir(upkPath);
            Directory.CreateDirectory(dir);

            long ticks = DateTime.UtcNow.Ticks;
            string ext = Path.GetExtension(upkPath);
            string snapshotPath = Path.Combine(dir, ticks.ToString() + ext);
            File.Copy(upkPath, snapshotPath, overwrite: false);

            IndexFile idx = LoadIndex(upkPath);
            idx.OriginalPath = upkPath;
            idx.Entries.Insert(0, new HistoryEntry
            {
                OriginalPath = upkPath,
                SnapshotPath = snapshotPath,
                TimestampUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(snapshotPath).Length,
                ToolTag = toolTag ?? string.Empty,
            });

            // Prune to per-file cap.
            while (idx.Entries.Count > PerFileSnapshotCap)
            {
                HistoryEntry old = idx.Entries[^1];
                idx.Entries.RemoveAt(idx.Entries.Count - 1);
                try { if (File.Exists(old.SnapshotPath)) File.Delete(old.SnapshotPath); }
                catch { }
            }

            SaveIndex(upkPath, idx);
            return snapshotPath;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<HistoryEntry> GetHistoryFor(string upkPath)
        => LoadIndex(upkPath).Entries;

    // Returns every history entry across every tracked UPK, newest first.
    public static IReadOnlyList<HistoryEntry> GetAllHistory()
    {
        List<HistoryEntry> all = new();
        try
        {
            if (!Directory.Exists(RootDir)) return all;
            foreach (string subdir in Directory.EnumerateDirectories(RootDir))
            {
                string idxPath = Path.Combine(subdir, "index.json");
                if (!File.Exists(idxPath)) continue;
                try
                {
                    IndexFile idx = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(idxPath)) ?? new IndexFile();
                    all.AddRange(idx.Entries);
                }
                catch { }
            }
        }
        catch { }
        all.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
        return all;
    }

    public static bool Restore(HistoryEntry entry)
    {
        try
        {
            if (entry is null || !File.Exists(entry.SnapshotPath) || string.IsNullOrEmpty(entry.OriginalPath))
                return false;
            // Snapshot the current state too (so a restore is itself reversible).
            Snapshot(entry.OriginalPath, "pre-restore");
            File.Copy(entry.SnapshotPath, entry.OriginalPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Delete(HistoryEntry entry)
    {
        try
        {
            if (entry is null) return false;
            IndexFile idx = LoadIndex(entry.OriginalPath);
            int removed = idx.Entries.RemoveAll(e => e.SnapshotPath == entry.SnapshotPath);
            if (File.Exists(entry.SnapshotPath)) File.Delete(entry.SnapshotPath);
            SaveIndex(entry.OriginalPath, idx);
            return removed > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IndexFile LoadIndex(string upkPath)
    {
        try
        {
            string path = GetIndexPath(upkPath);
            if (!File.Exists(path)) return new IndexFile { OriginalPath = upkPath };
            return JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path))
                   ?? new IndexFile { OriginalPath = upkPath };
        }
        catch
        {
            return new IndexFile { OriginalPath = upkPath };
        }
    }

    private static void SaveIndex(string upkPath, IndexFile idx)
    {
        try
        {
            string path = GetIndexPath(upkPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(idx,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
