using System;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Workspace.Backup;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

public sealed class BackupManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly BackupManager _manager = new();

    public BackupManagerTests()
    {
        _dir = Scratch.NewFolder("oas2-backupmgr");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RestorePutsTheOriginalBackAndKeepsTheBackup()
    {
        string path = WriteFile("asset.upk", "original");
        BackupFileHelper.CreateBackup(path);
        File.WriteAllText(path, "edited");

        BackupEntry entry = _manager.ScanFolder(_dir).First(e =>
            e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));

        _manager.Restore(entry);

        Assert.Equal("original", File.ReadAllText(path));

        // Restoring must be repeatable, so the backup survives it.
        Assert.True(File.Exists(entry.BackupPath));
        File.WriteAllText(path, "edited again");
        _manager.Restore(entry);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void RestoreAllContinuesPastAFailure()
    {
        // One unreadable backup must not abandon the rest: someone restoring
        // after a bad edit needs as much put back as possible.
        string good = WriteFile("good.upk", "good original");
        BackupFileHelper.CreateBackup(good);
        File.WriteAllText(good, "broken");

        var missing = new BackupEntry
        {
            BackupPath = Path.Combine(_dir, "gone.upk.bak"),
            OriginalPath = Path.Combine(_dir, "gone.upk"),
            TakenUtc = DateTime.UtcNow,
            BackupSizeBytes = 0,
            OriginalExists = false,
            OriginalSizeBytes = null,
            IsLegacyLocation = false,
        };

        BackupEntry goodEntry = _manager.ScanFolder(_dir).First(e =>
            e.OriginalPath.Equals(good, StringComparison.OrdinalIgnoreCase));

        RestoreReport report = _manager.RestoreAll([missing, goodEntry]);

        Assert.Equal(1, report.Restored);
        Assert.Single(report.Failures);
        Assert.Equal("good original", File.ReadAllText(good));
    }

    [Fact]
    public void DeleteRemovesTheBackupButNeverTheFile()
    {
        string path = WriteFile("asset.upk", "original");
        BackupFileHelper.CreateBackup(path);

        BackupEntry entry = _manager.ScanFolder(_dir).First(e =>
            e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));

        RestoreReport report = _manager.Delete([entry]);

        Assert.Equal(1, report.Restored);
        Assert.False(File.Exists(entry.BackupPath));
        Assert.True(File.Exists(path), "Forgetting a backup must never delete the file it protected.");
    }

    [Fact]
    public void AMissingOriginalIsReportedRatherThanHidden()
    {
        string path = WriteFile("vanishing.upk", "original");
        BackupFileHelper.CreateBackup(path);
        File.Delete(path);

        BackupEntry entry = _manager.ScanFolder(_dir).First(e =>
            e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.False(entry.OriginalExists);

        // And restoring it must recreate the file.
        _manager.Restore(entry);
        Assert.True(File.Exists(path));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void AChangedFileIsFlagged()
    {
        string path = WriteFile("changed.upk", "original");
        BackupFileHelper.CreateBackup(path);
        File.WriteAllText(path, "a considerably longer edited version");

        BackupEntry entry = _manager.ScanFolder(_dir).First(e =>
            e.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));

        Assert.True(entry.LooksModified);
    }

    [Fact]
    public void ScanFolderFindsTheBackupsBesideTheFiles()
    {
        string path = WriteFile("legacy.upk", "current");
        File.WriteAllText(path + ".bak", "the original");

        BackupEntry entry = Assert.Single(_manager.ScanFolder(_dir));

        // Beside the file is where a backup belongs, so nothing about it is
        // out of place; the vault an earlier version used is what gets marked.
        Assert.False(entry.IsLegacyLocation);
        Assert.Equal(path, entry.OriginalPath);

        _manager.Restore(entry);
        Assert.Equal("the original", File.ReadAllText(path));
    }

    [Fact]
    public void ScanningAMissingFolderSaysSo()
        => Assert.Throws<DirectoryNotFoundException>(
            () => _manager.ScanFolder(Path.Combine(_dir, "not-there")));
}
