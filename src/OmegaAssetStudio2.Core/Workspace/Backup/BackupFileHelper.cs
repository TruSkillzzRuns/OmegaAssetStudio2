namespace OmegaAssetStudio2.Core.Workspace.Backup;

/// <summary>
/// Pristine-backup policy for game files: exactly one copy per file, ever, kept
/// beside the file it protects.
/// </summary>
/// <remarks>
/// The first time any tool touches a file, the untouched original is copied to
/// <c>&lt;name&gt;.bak</c> in the same folder — for a game package, alongside
/// everything else in CookedPCConsole. Every later edit finds that copy already
/// present and leaves it alone, because it is the only guaranteed-pristine
/// state; replacing it with an already-modified file would destroy the only way
/// back.
/// <para>
/// This is what a backup is expected to be: the original, where the original
/// lives, restorable with or without this application.
/// </para>
/// <para>
/// An earlier version kept them in a vault under the user's application data.
/// Those are still found and restored from, so nothing already made is lost,
/// and <see cref="MoveVaultBackupsBesideTheirFiles"/> brings them home.
/// </para>
/// </remarks>
public static class BackupFileHelper
{
    private const string BackupSuffix = ".bak";

    /// <summary>Where a file's backup sits: beside it.</summary>
    public static string GetBackupPath(string sourcePath) => sourcePath + BackupSuffix;

    /// <summary>Where an earlier version kept backups. Still read, no longer written.</summary>
    /// <remarks>
    /// Redirectable through <c>OAS2_BACKUP_VAULT</c> so that a test run keeps
    /// its backups to itself. Tests write packages through the ordinary write
    /// path, which takes a pristine backup first; without this they land in the
    /// vault the application shows the user, who is then looking at a list of
    /// files they never touched — and, because the tests work on copies of real
    /// packages, at hundreds of megabytes of them.
    /// </remarks>
    public static string VaultRoot { get; } =
        Environment.GetEnvironmentVariable("OAS2_BACKUP_VAULT") is { Length: > 0 } redirected
            ? redirected
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmegaAssetStudio2", "backups");

    /// <summary>
    /// The vault path a file's backup would occupy.
    /// </summary>
    /// <remarks>
    /// The drive letter becomes a folder and the rest of the path is preserved,
    /// so two files with the same name in different places cannot collide and a
    /// human can find any backup by following the path.
    /// </remarks>
    public static string GetVaultPath(string sourcePath)
    {
        string full = Path.GetFullPath(sourcePath);
        string? root = Path.GetPathRoot(full);

        // "E:\" becomes "E", and a network path keeps its share name.
        string drive = string.IsNullOrEmpty(root)
            ? "unknown"
            : root.Replace(":", string.Empty).Replace(Path.DirectorySeparatorChar, '_').Trim('_', ' ');

        string relative = string.IsNullOrEmpty(root) ? full : full[root.Length..];

        return Path.Combine(VaultRoot, drive.Length == 0 ? "unknown" : drive, relative + BackupSuffix);
    }

    /// <summary>
    /// Whether a path sits in the system's scratch folder rather than anywhere
    /// worth protecting.
    /// </summary>
    public static bool IsScratch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string temp = Path.GetFullPath(Path.GetTempPath());

        return Path.GetFullPath(path)
            .StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the pristine backup for <paramref name="sourcePath"/>, creating it
    /// if this is the first time the file has been touched. Never overwrites an
    /// existing backup, wherever it lives.
    /// </summary>
    public static string CreateBackup(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));

        // Nothing in a scratch folder is worth protecting. The vault exists so a
        // game file can be put back, and a copy made for a moment's work is not
        // one — it is deleted as soon as the work is done, leaving an entry that
        // protects a file which no longer exists. A run of the tests, which
        // copy real packages out to scratch before editing them, left the vault
        // holding hundreds of megabytes of exactly that.
        if (IsScratch(sourcePath)) return sourcePath;

        // A backup is never itself backed up. There is one saved original per
        // game file - <name>.upk.bak, beside <name>.upk - and it is written
        // once and only ever read from. Handing a .bak here, which happens when
        // a build is pointed at the saved original instead of the live file,
        // used to produce <name>.upk.bak.bak: a second copy that protects
        // nothing and shows up as though it were a file of its own.
        //
        // Nor is a working file. A build leaves <name>.upk.building and
        // <name>.upk.building.lent beside the real one while it runs, and they
        // are gone by the end; a saved original of one protects a file that no
        // longer exists.
        if (IsBackupOrWorkingFile(sourcePath)) return sourcePath;

        string? existing = FindExistingBackup(sourcePath);
        if (existing is not null)
            return existing;

        string backupPath = GetBackupPath(sourcePath);

        try
        {
            // overwrite: false is the point. If two callers race, the loser throws
            // rather than replacing a pristine copy with a live one.
            File.Copy(sourcePath, backupPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Lost the race. The winner's backup is equally pristine.
        }

        return backupPath;
    }

    /// <summary>
    /// Whether a path is a saved original, or a file a build made while working.
    /// Neither is something to protect.
    /// </summary>
    public static bool IsBackupOrWorkingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string name = Path.GetFileName(path);

        return name.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".building", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lent", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".building.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns an existing backup for <paramref name="sourcePath"/>, or null.
    /// Beside the file first, then the vault an earlier version used.
    /// </summary>
    public static string? FindExistingBackup(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        string beside = GetBackupPath(sourcePath);
        if (File.Exists(beside)) return beside;

        string vault = GetVaultPath(sourcePath);
        return File.Exists(vault) ? vault : null;
    }

    /// <summary>
    /// Puts every backup the vault holds beside the file it protects, and
    /// removes it from the vault once it is safely there.
    /// </summary>
    /// <returns>How many were moved.</returns>
    public static int MoveVaultBackupsBesideTheirFiles()
    {
        int moved = 0;

        foreach (string original in ListProtectedFiles())
        {
            string vault = GetVaultPath(original);
            string beside = GetBackupPath(original);

            if (!File.Exists(vault)) continue;

            // Never over a backup that already sits there: that one is at least
            // as pristine as this, and may be the only copy of an older state.
            if (File.Exists(beside)) { File.Delete(vault); continue; }
            if (!Directory.Exists(Path.GetDirectoryName(beside))) continue;

            File.Copy(vault, beside, overwrite: false);
            File.Delete(vault);
            moved++;
        }

        return moved;
    }

    public static bool HasBackup(string sourcePath) => FindExistingBackup(sourcePath) is not null;

    /// <summary>
    /// Maps a backup path back to the file it protects.
    /// </summary>
    public static string ResolveOriginalPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return backupPath;

        // Match on the LAST occurrence, not the first. Cutting at the first ".bak"
        // mangles a legitimately-named file such as "my.bakery.upk.bak".
        int marker = backupPath.LastIndexOf(BackupSuffix, StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? backupPath[..marker] : backupPath;
    }

    /// <summary>
    /// Restores a file from its pristine backup. The backup is kept, so restore
    /// is repeatable.
    /// </summary>
    public static bool Restore(string sourcePath)
    {
        string? backupPath = FindExistingBackup(sourcePath);
        if (backupPath is null) return false;

        File.Copy(backupPath, sourcePath, overwrite: true);
        return true;
    }

    /// <summary>
    /// Every file the vault currently protects, as original paths.
    /// </summary>
    /// <remarks>
    /// Lets a caller show what can be restored, or clear backups for files that
    /// no longer exist.
    /// </remarks>
    public static IReadOnlyList<string> ListProtectedFiles()
    {
        if (!Directory.Exists(VaultRoot)) return [];

        var originals = new List<string>();

        foreach (string backup in Directory.EnumerateFiles(VaultRoot, "*" + BackupSuffix, SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(VaultRoot, backup);

            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator <= 0) continue;

            string drive = relative[..separator];
            string rest = relative[(separator + 1)..];

            originals.Add(ResolveOriginalPath(Path.Combine(drive + ":" + Path.DirectorySeparatorChar, rest)));
        }

        return originals;
    }
}
