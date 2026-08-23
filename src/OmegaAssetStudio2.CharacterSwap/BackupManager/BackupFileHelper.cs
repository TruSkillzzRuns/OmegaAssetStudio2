namespace OmegaAssetStudio.BackupManager;

public static class BackupFileHelper
{
    /// <summary>
    /// Backup policy: at most one backup per original file ever exists. If any
    /// .bak (or *.bak.* timestamped variant) already exists next to the source,
    /// it is treated as the canonical pristine copy and is never overwritten or
    /// replaced; the path to the existing backup is returned.
    ///
    /// Only the Backup workspace consumes these files (for restore). Tools that
    /// modify game assets call this before writing changes; subsequent edits
    /// to the same file are no-ops here because the original pristine copy is
    /// already captured.
    /// </summary>
    public static string CreateBackup(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));

        // A saved original is never itself saved, and neither is a file a build
        // made while it worked. One .bak per game file, beside it, written once
        // and only ever read from. Pointing a build at the .bak instead of the
        // live file is what produced <name>.upk.bak.bak - a copy protecting
        // nothing, which then appeared in the backup list as its own file.
        if (IsBackupOrWorkingFile(sourcePath)) return sourcePath;

        string? existing = FindExistingBackup(sourcePath);
        if (existing is not null)
            return existing;

        string backupPath = sourcePath + ".bak";
        File.Copy(sourcePath, backupPath, overwrite: false);
        return backupPath;
    }

    /// <summary>Whether a path is a saved original, or a build's working file.</summary>
    public static bool IsBackupOrWorkingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string name = Path.GetFileName(path);

        return name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".building", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lent", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".building.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the path of an existing backup for <paramref name="sourcePath"/>
    /// if one is present, otherwise null. Recognises all known naming patterns:
    ///   &lt;file&gt;.bak              (canonical)
    ///   &lt;file&gt;.&lt;timestamp&gt;.bak  (legacy texture-injector pattern)
    ///   &lt;file&gt;.bak.&lt;timestamp&gt;  (older helper subsequent-backup pattern)
    /// </summary>
    public static string? FindExistingBackup(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        string primaryBackupPath = sourcePath + ".bak";
        if (File.Exists(primaryBackupPath))
            return primaryBackupPath;

        string? directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        string fileName = Path.GetFileName(sourcePath);

        try
        {
            // Single sweep: any file in the directory whose name starts with the
            // original name and contains ".bak" is treated as a backup of it.
            foreach (string candidate in Directory.EnumerateFiles(directory, fileName + "*", SearchOption.TopDirectoryOnly))
            {
                string candidateName = Path.GetFileName(candidate);
                if (candidateName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    continue; // skip the source file itself

                if (candidateName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                    candidateName.Contains(".bak.", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    public static string ResolveOriginalPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return backupPath;

        int markerIndex = backupPath.IndexOf(".bak", StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0 ? backupPath[..markerIndex] : backupPath;
    }
}

