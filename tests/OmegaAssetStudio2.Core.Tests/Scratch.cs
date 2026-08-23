using System;
using System.IO;
using OmegaAssetStudio2.Core.Workspace.Backup;

namespace OmegaAssetStudio2.Core.Tests;

/// <summary>
/// A folder for tests to work in.
/// </summary>
/// <remarks>
/// Not the system's scratch folder. Backups are deliberately not taken for
/// anything living there — a copy made for a moment's work is not a game file
/// worth being able to put back — so a test of the backups themselves has to
/// work somewhere the application treats as real. Left under the application's
/// own data, and deleted by the test that made it.
/// </remarks>
public static class Scratch
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio2", "test-scratch");

    private static bool _swept;
    private static readonly object Gate = new();

    /// <summary>A fresh folder, made and returned.</summary>
    public static string NewFolder(string what)
    {
        Sweep();

        string path = Path.Combine(Root, $"{what}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// Clears what earlier runs left behind, including their backups.
    /// </summary>
    /// <remarks>
    /// Tests that write packages go through the ordinary write path, which
    /// takes a pristine backup first — so each run leaves entries in the vault
    /// protecting files that are about to be deleted. Cleared at the start of a
    /// run rather than the end, so a run that is interrupted still gets tidied
    /// up by the next one.
    /// </remarks>
    private static void Sweep()
    {
        lock (Gate)
        {
            if (_swept) return;
            _swept = true;

            Remove(Root);
            Remove(BackupFileHelper.GetVaultPath(Root)[..^".bak".Length]);
        }
    }

    private static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { /* something still holds it; the next run tries again */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
