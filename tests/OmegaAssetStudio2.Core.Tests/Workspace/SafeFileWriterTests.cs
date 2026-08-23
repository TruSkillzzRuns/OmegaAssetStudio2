using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Workspace.Backup;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

public sealed class SafeFileWriterTests : IDisposable
{
    private readonly string _dir;

    public SafeFileWriterTests()
    {
        _dir = Scratch.NewFolder("oas2-tests");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteFixture(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ReplacesContentAndLeavesNoTempFile()
    {
        string path = WriteFixture("asset.upk", "original");

        await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("modified"));

        Assert.Equal("modified", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task TakesAPristineBackupOnFirstWrite()
    {
        string path = WriteFixture("asset.upk", "original");

        string backup = await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("modified"));

        Assert.True(File.Exists(backup));
        Assert.Equal("original", File.ReadAllText(backup));
    }

    [Fact]
    public async Task TheBackupSitsBesideTheFile()
    {
        // A backup belongs where the file it protects lives, as <name>.bak, so
        // it can be found and put back with or without this application.
        string path = WriteFixture("asset.upk", "original");

        await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("modified"));

        Assert.True(File.Exists(path + ".bak"), "No backup was left beside the file.");
        Assert.Equal("original", File.ReadAllText(path + ".bak"));
        Assert.Equal("modified", File.ReadAllText(path));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingBackup()
    {
        // This is the property that matters most: the backup must keep holding the
        // ORIGINAL bytes, not the previous edit. Otherwise the second save quietly
        // destroys the user's only way back to a working file.
        string path = WriteFixture("asset.upk", "original");

        await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("edit one"));
        await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("edit two"));
        await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("edit three"));

        Assert.Equal("edit three", File.ReadAllText(path));
        Assert.Equal("original", File.ReadAllText(BackupFileHelper.FindExistingBackup(path)!));
    }

    [Fact]
    public async Task ThreeWritesLeaveExactlyOneBackup()
    {
        string path = WriteFixture("asset.upk", "original");

        for (int i = 0; i < 3; i++)
            await SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes($"edit {i}"));

        string[] backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "asset.upk.bak*");

        Assert.Single(backups);
    }

    [Fact]
    public void FilesWithTheSameNameInDifferentFoldersGetSeparateBackups()
    {
        // Each stays beside its own file, so two of the same name cannot
        // collide however many folders hold one.
        string a = Path.Combine(_dir, "a");
        string b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        Assert.NotEqual(
            BackupFileHelper.GetBackupPath(Path.Combine(a, "same.upk")),
            BackupFileHelper.GetBackupPath(Path.Combine(b, "same.upk")));
    }

    [Fact]
    public async Task RefusesToCreateAFileThatDoesNotExist()
    {
        string missing = Path.Combine(_dir, "not-there.upk");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SafeFileWriter.WriteAsync(missing, [1, 2, 3]));

        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task LeavesTheOriginalIntactWhenTheTargetIsLocked()
    {
        string path = WriteFixture("locked.upk", "original");
        // Take the backup first so the failure happens at the swap, not before it.
        BackupFileHelper.CreateBackup(path);

        using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(
                () => SafeFileWriter.WriteAsync(path, Encoding.UTF8.GetBytes("modified")));
        }

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}

public sealed class BackupFileHelperTests : IDisposable
{
    private readonly string _dir;

    public BackupFileHelperTests()
    {
        _dir = Scratch.NewFolder("oas2-tests");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CreateBackupIsIdempotent()
    {
        string path = Path.Combine(_dir, "asset.upk");
        File.WriteAllText(path, "original");

        string first = BackupFileHelper.CreateBackup(path);
        File.WriteAllText(path, "modified");
        string second = BackupFileHelper.CreateBackup(path);

        Assert.Equal(first, second);
        Assert.Equal("original", File.ReadAllText(second));
    }

    [Theory]
    [InlineData(@"C:\x\asset.upk.bak", @"C:\x\asset.upk")]
    [InlineData(@"C:\x\asset.upk.bak.20260101_000000", @"C:\x\asset.upk")]
    // A file whose own name contains ".bak" must not be truncated at the first hit.
    [InlineData(@"C:\x\my.bakery.upk.bak", @"C:\x\my.bakery.upk")]
    public void ResolveOriginalPathCutsAtTheLastMarker(string backup, string expected)
    {
        Assert.Equal(expected, BackupFileHelper.ResolveOriginalPath(backup));
    }

    [Fact]
    public void RestoreReturnsFalseWhenNoBackupExists()
    {
        string path = Path.Combine(_dir, "asset.upk");
        File.WriteAllText(path, "original");

        Assert.False(BackupFileHelper.Restore(path));
    }

    [Fact]
    public void RestoreBringsBackTheOriginalAndKeepsTheBackup()
    {
        string path = Path.Combine(_dir, "asset.upk");
        File.WriteAllText(path, "original");
        BackupFileHelper.CreateBackup(path);
        File.WriteAllText(path, "broken");

        Assert.True(BackupFileHelper.Restore(path));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.True(BackupFileHelper.HasBackup(path));
    }

    [Fact]
    public void ABackupLeftByAnEarlierVersionIsStillFoundAndUsed()
    {
        // Earlier versions wrote the backup beside the file. Those must keep
        // working, or an existing install silently loses its only way back.
        string path = Path.Combine(_dir, "legacy.upk");
        File.WriteAllText(path, "modified already");
        File.WriteAllText(path + ".bak", "the true original");

        Assert.True(BackupFileHelper.HasBackup(path));
        Assert.Equal(path + ".bak", BackupFileHelper.FindExistingBackup(path));

        // And it must not be superseded by a new one taken from modified content.
        string returned = BackupFileHelper.CreateBackup(path);
        Assert.Equal(path + ".bak", returned);

        Assert.True(BackupFileHelper.Restore(path));
        Assert.Equal("the true original", File.ReadAllText(path));
    }
}
