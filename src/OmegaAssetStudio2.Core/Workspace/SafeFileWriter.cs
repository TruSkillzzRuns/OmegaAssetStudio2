using OmegaAssetStudio2.Core.Workspace.Backup;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// The single write path for anything that lands on a file inside the user's
/// game install.
/// </summary>
/// <remarks>
/// Version 1 committed edits with a plain <c>File.WriteAllBytes</c> over the live
/// path. A crash, a power loss, or an antivirus lock partway through left a
/// truncated package and a game that would not load the asset. The backup made it
/// recoverable, but only for a user who knew to go looking.
/// <para>
/// The contract here: take a pristine backup if one does not exist yet, write to a
/// sibling temp file, flush it to the device, then swap it in with
/// <c>File.Replace</c>, which is atomic on NTFS. Any failure before the swap leaves
/// the original completely untouched.
/// </para>
/// </remarks>
public static class SafeFileWriter
{
    /// <summary>
    /// Atomically replaces <paramref name="targetPath"/> with <paramref name="content"/>.
    /// Returns the path of the pristine backup protecting that file.
    /// </summary>
    public static async Task<string> WriteAsync(
        string targetPath,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(content);

        if (!File.Exists(targetPath))
            throw new FileNotFoundException("Refusing to commit to a path that does not exist.", targetPath);

        string backupPath = BackupFileHelper.CreateBackup(targetPath);

        string directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Cannot resolve a directory for '{targetPath}'.");
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, useAsync: true))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // Force to the device before the swap, so a crash during Replace
                // cannot leave a half-written temp file in place.
                stream.Flush(flushToDisk: true);
            }

            // destinationBackupFileName is null because CreateBackup above already
            // covers rollback; asking Replace for a second backup would churn a
            // full copy of the original on every write.
            File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            return backupPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Synchronous form, for call sites that are not async.</summary>
    public static string Write(string targetPath, byte[] content)
        => WriteAsync(targetPath, content).GetAwaiter().GetResult();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* orphaned temp file, not fatal */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
