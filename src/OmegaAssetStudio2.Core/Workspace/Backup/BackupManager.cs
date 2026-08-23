namespace OmegaAssetStudio2.Core.Workspace.Backup;

/// <summary>One protected file: the pristine copy and what it protects.</summary>
public sealed record BackupEntry
{
    public required string BackupPath { get; init; }
    public required string OriginalPath { get; init; }

    public required DateTime TakenUtc { get; init; }
    public required long BackupSizeBytes { get; init; }

    /// <summary>False when the file this protects has since been deleted or moved.</summary>
    public required bool OriginalExists { get; init; }

    public required long? OriginalSizeBytes { get; init; }

    /// <summary>True when the backup sits beside the file, as earlier versions wrote it.</summary>
    public required bool IsLegacyLocation { get; init; }

    public string FileName => Path.GetFileName(OriginalPath);
    public string FolderPath => Path.GetDirectoryName(OriginalPath) ?? string.Empty;

    /// <summary>
    /// True when the live file differs in size from its backup, so it has almost
    /// certainly been edited. Equal sizes are not proof it is untouched — an
    /// edit that preserves size is exactly what most of this application does —
    /// so this is reported as a hint, never as a guarantee.
    /// </summary>
    public bool LooksModified => OriginalExists && OriginalSizeBytes != BackupSizeBytes;

    public override string ToString() => $"{FileName} ({BackupSizeBytes:N0} bytes)";
}

/// <summary>The outcome of restoring one or more files.</summary>
public sealed record RestoreReport(int Restored, IReadOnlyList<string> Failures)
{
    public bool AllSucceeded => Failures.Count == 0;
}

/// <summary>
/// Lists, restores, and removes the pristine backups this application has taken.
/// </summary>
/// <remarks>
/// Backups are kept in a vault outside the game folder, and older ones may sit
/// beside the files they protect. Both are listed together so nothing is
/// invisible to the user.
/// </remarks>
public sealed class BackupManager
{
    /// <summary>
    /// Every backup in the vault, newest first.
    /// </summary>
    public IReadOnlyList<BackupEntry> ScanVault()
    {
        if (!Directory.Exists(BackupFileHelper.VaultRoot)) return [];

        var entries = new List<BackupEntry>();

        foreach (string original in BackupFileHelper.ListProtectedFiles())
        {
            string backup = BackupFileHelper.GetVaultPath(original);
            if (!File.Exists(backup)) continue;

            entries.Add(Describe(backup, original, isLegacy: true));
        }

        return Sort(entries);
    }

    /// <summary>
    /// Backups sitting beside files in a folder, as earlier versions wrote them.
    /// </summary>
    /// <remarks>
    /// Offered so a user upgrading from an earlier version can still find, restore,
    /// and clear the backups it left in their game folder.
    /// </remarks>
    public IReadOnlyList<BackupEntry> ScanFolder(string folder, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var entries = new List<BackupEntry>();

        foreach (string backup in Directory.EnumerateFiles(folder, "*.bak*", option))
        {
            string original = BackupFileHelper.ResolveOriginalPath(backup);

            // A working file is not a file worth listing. Building a swap leaves
            // <name>.upk.building and <name>.upk.building.lent beside the real
            // one for as long as the work takes, and a backup taken of those
            // puts a second and third row in the list under the same costume's
            // name. There is one file the user can put back, and it is the one
            // the game loads.
            if (IsWorkingFile(original)) continue;

            // A copy of a copy is not a file either. <name>.upk.bak.bak resolves
            // to <name>.upk.bak, which would be listed as though the backup
            // itself were something to restore - the same costume twice, once
            // as the file and once as its saved original.
            if (original.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;

            entries.Add(Describe(backup, original, isLegacy: false));
        }

        return Sort(entries);
    }

    /// <summary>Whether a path is something a build made rather than a game file.</summary>
    private static bool IsWorkingFile(string path)
    {
        string name = Path.GetFileName(path);

        return name.EndsWith(".building", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lent", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".building.", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<BackupEntry> Sort(List<BackupEntry> entries) => entries
        .OrderByDescending(e => e.TakenUtc)
        .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static BackupEntry Describe(string backupPath, string originalPath, bool isLegacy)
    {
        var backupInfo = new FileInfo(backupPath);
        var originalInfo = new FileInfo(originalPath);

        return new BackupEntry
        {
            BackupPath = backupPath,
            OriginalPath = originalPath,
            TakenUtc = backupInfo.Exists ? backupInfo.LastWriteTimeUtc : DateTime.MinValue,
            BackupSizeBytes = backupInfo.Exists ? backupInfo.Length : 0,
            OriginalExists = originalInfo.Exists,
            OriginalSizeBytes = originalInfo.Exists ? originalInfo.Length : null,
            IsLegacyLocation = isLegacy,
        };
    }

    /// <summary>
    /// Puts a file back the way it was. The backup is kept, so a restore can be
    /// repeated and cannot itself destroy anything.
    /// </summary>
    public void Restore(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!File.Exists(entry.BackupPath))
            throw new FileNotFoundException("This backup no longer exists.", entry.BackupPath);

        string? directory = Path.GetDirectoryName(entry.OriginalPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
    }

    /// <summary>
    /// Restores several files, continuing past any that fail.
    /// </summary>
    /// <remarks>
    /// One unreadable backup must not abandon the rest: a user restoring after a
    /// bad edit needs as much put back as possible, and a list of what could not
    /// be.
    /// </remarks>
    public RestoreReport RestoreAll(IEnumerable<BackupEntry> entries)
    {
        var failures = new List<string>();
        int restored = 0;

        foreach (BackupEntry entry in entries)
        {
            try
            {
                Restore(entry);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                failures.Add($"{entry.FileName}: {ex.Message}");
            }
        }

        return new RestoreReport(restored, failures);
    }

    /// <summary>
    /// Deletes backups. The files they protect are never touched.
    /// </summary>
    /// <returns>How many were removed, and anything that could not be.</returns>
    public RestoreReport Delete(IEnumerable<BackupEntry> entries)
    {
        var failures = new List<string>();
        int deleted = 0;

        foreach (BackupEntry entry in entries)
        {
            try
            {
                if (File.Exists(entry.BackupPath))
                {
                    File.Delete(entry.BackupPath);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{entry.FileName}: {ex.Message}");
            }
        }

        return new RestoreReport(deleted, failures);
    }

    /// <summary>Total bytes the vault occupies.</summary>
    public long GetVaultSizeBytes()
    {
        if (!Directory.Exists(BackupFileHelper.VaultRoot)) return 0;

        return Directory
            .EnumerateFiles(BackupFileHelper.VaultRoot, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }
}
