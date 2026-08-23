using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmegaAssetStudio.WinUI.Services;

// Stores and exposes the user-selected target-game install root.
//
// Persisted at %LocalAppData%\OmegaAssetStudio\game_install_path.txt so the
// choice survives sessions. The cooked-data directory underneath the
// install root is AUTO-DISCOVERED at first access — see GetCookedDataDir.
// No JSON config to edit, no hardcoded directory names in source.
public static class GameInstallService
{
    private static readonly string _settingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio");
    private static readonly string _settingsFile = Path.Combine(_settingsDir, "game_install_path.txt");

    private static string? _installRoot;
    private static bool _loaded;

    public static event EventHandler? InstallRootChanged;

    public static string? InstallRoot
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try { if (File.Exists(_settingsFile)) _installRoot = File.ReadAllText(_settingsFile).Trim(); }
                catch { }
                if (string.IsNullOrWhiteSpace(_installRoot) || !Directory.Exists(_installRoot))
                    _installRoot = null;
            }
            return _installRoot;
        }
    }

    public static bool HasValidInstall =>
        !string.IsNullOrWhiteSpace(InstallRoot) &&
        File.Exists(GetCalligraphySipPath());

    public static string? GetCalligraphySipPath()
    {
        if (string.IsNullOrWhiteSpace(InstallRoot)) return null;
        return Path.Combine(InstallRoot, "Data", "Game", "Calligraphy.sip");
    }

    // Cached auto-discovered cooked-data dir. Cleared whenever the install
    // root changes.
    private static string? _cachedCookedDir;
    private static string? _cachedCookedDirInstallRoot;

    // Returns the cooked-game-data directory under the user's install
    // root. AUTO-DISCOVERED by convention: UE3 games always store their
    // cooked .upk files in a folder whose name starts with "Cooked".
    // We BFS up to 4 levels deep from the install root, return the
    // first such folder that actually contains .upk files. Cached per
    // install root.
    //
    // Returns null if no install root is set or no cooked dir was found.
    public static string? GetCookedDataDir()
    {
        string? root = InstallRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;

        if (_cachedCookedDir is not null &&
            string.Equals(_cachedCookedDirInstallRoot, root, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(_cachedCookedDir))
        {
            return _cachedCookedDir;
        }

        string? found = DiscoverCookedDataDir(root);
        _cachedCookedDir = found;
        _cachedCookedDirInstallRoot = root;
        return found;
    }

    // BFS from `root` up to 4 levels deep. Returns the first directory
    // whose leaf name starts with "Cooked" (case-insensitive) AND contains
    // at least one .upk file in its top level. Out of multiple matches,
    // picks the one with the most .upk files (handles games that ship
    // more than one Cooked* folder — the bigger one is the real data).
    private static string? DiscoverCookedDataDir(string root)
    {
        try
        {
            string? best = null;
            int bestCount = 0;
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (cur, depth) = queue.Dequeue();
                string leaf = Path.GetFileName(cur);
                if (leaf.StartsWith("Cooked", StringComparison.OrdinalIgnoreCase))
                {
                    int upkCount;
                    try
                    {
                        // Cap the count — we only need "more than the current best".
                        upkCount = Directory.EnumerateFiles(cur, "*.upk",
                            SearchOption.TopDirectoryOnly).Take(20_000).Count();
                    }
                    catch { upkCount = 0; }
                    if (upkCount > bestCount)
                    {
                        best = cur;
                        bestCount = upkCount;
                    }
                }
                if (depth >= 4) continue;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(cur); }
                catch { continue; }
                foreach (var s in subs) queue.Enqueue((s, depth + 1));
            }
            return best;
        }
        catch { return null; }
    }

    // Force re-discovery (call after the user changes the install root or
    // moves the game install).
    public static void InvalidateCookedDataCache()
    {
        _cachedCookedDir = null;
        _cachedCookedDirInstallRoot = null;
    }

    public static void SetInstallRoot(string? path)
    {
        string? sanitized = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (sanitized is not null && !Directory.Exists(sanitized)) sanitized = null;
        if (string.Equals(_installRoot, sanitized, StringComparison.OrdinalIgnoreCase)) return;
        _installRoot = sanitized;
        _loaded = true;
        InvalidateCookedDataCache();
        try
        {
            Directory.CreateDirectory(_settingsDir);
            if (sanitized is null && File.Exists(_settingsFile)) File.Delete(_settingsFile);
            else if (sanitized is not null) File.WriteAllText(_settingsFile, sanitized);
        }
        catch { /* persistence best-effort; in-memory value still updates */ }
        if (sanitized is not null) TryBackupCalligraphySip();
        InstallRootChanged?.Invoke(null, EventArgs.Empty);
    }

    // Suffix used for the pristine-copy backup we drop next to Calligraphy.sip the
    // first time the install path is set. The app never writes to the SIP itself, but
    // the backup is cheap insurance against accidental edits / corruption from any
    // other tool the user runs.
    public const string CalligraphyBackupSuffix = ".omegabackup";

    public static string? GetCalligraphyBackupPath()
    {
        string? sip = GetCalligraphySipPath();
        return sip is null ? null : sip + CalligraphyBackupSuffix;
    }

    // Creates a one-time backup of Calligraphy.sip next to the original. Skips if the
    // backup already exists (so we never overwrite a known-good copy with a possibly-
    // altered current one) or the SIP is missing. Returns the backup path on success.
    public static string? TryBackupCalligraphySip()
    {
        try
        {
            string? sip = GetCalligraphySipPath();
            if (sip is null || !File.Exists(sip)) return null;
            string backup = sip + CalligraphyBackupSuffix;
            if (File.Exists(backup)) return backup;     // preserve existing pristine copy
            File.Copy(sip, backup, overwrite: false);
            return backup;
        }
        catch { return null; }
    }


    // Restores Calligraphy.sip from the .omegabackup copy. Returns true on success.
    public static bool RestoreCalligraphySipFromBackup()
    {
        try
        {
            string? sip = GetCalligraphySipPath();
            string? backup = GetCalligraphyBackupPath();
            if (sip is null || backup is null || !File.Exists(backup)) return false;
            File.Copy(backup, sip, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    public static bool HasCalligraphyBackup()
    {
        string? backup = GetCalligraphyBackupPath();
        return backup is not null && File.Exists(backup);
    }
}

