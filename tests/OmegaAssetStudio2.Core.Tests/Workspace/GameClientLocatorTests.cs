using System;
using System.IO;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

public sealed class PackageFormatTests
{
    [Fact]
    public void SameFormatIsCompatible()
    {
        Assert.True(new PackageFormat(868, 3).IsCompatibleWith(new PackageFormat(868, 3)));
    }

    [Fact]
    public void DifferentFileVersionIsNotCompatible()
    {
        // Observed on disk: two installs read 868/3 while a third reads 894/3.
        // Content cannot move between the two groups without conversion.
        Assert.False(new PackageFormat(868, 3).IsCompatibleWith(new PackageFormat(894, 3)));
    }

    [Fact]
    public void DifferentLicenseeVersionIsNotCompatible()
    {
        Assert.False(new PackageFormat(868, 3).IsCompatibleWith(new PackageFormat(868, 30)));
    }

    [Fact]
    public void UnknownIsNeverCompatibleEvenWithItself()
    {
        // "I could not read the format" must never be treated as a match, or a
        // failed probe silently authorises a cross-format write.
        Assert.False(PackageFormat.Unknown.IsCompatibleWith(PackageFormat.Unknown));
        Assert.False(PackageFormat.Unknown.IsCompatibleWith(new PackageFormat(868, 3)));
        Assert.False(new PackageFormat(868, 3).IsCompatibleWith(PackageFormat.Unknown));
    }
}

public sealed class GameClientLocatorTests : IDisposable
{
    private readonly string _dir;

    public GameClientLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "oas2-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Writes a file with a valid package header and nothing else.</summary>
    private static void WritePackage(string path, short fileVersion, short licenseeVersion)
        => OmegaAssetStudio2.Core.Tests.Packages.TestPackageBuilder.WriteFile(path, fileVersion, licenseeVersion);

    [Fact]
    public void FindsCookedFolderNestedTwoLevelsDown()
    {
        // Matches the real layout: <root>\<engine>\<title>\CookedPCConsole
        string cooked = Path.Combine(_dir, "UnrealEngine3", "GameFolder", "CookedPCConsole");
        Directory.CreateDirectory(cooked);

        Assert.Equal(cooked, GameClientLocator.FindCookedFolder(_dir));
    }

    [Fact]
    public void AcceptsTheCookedFolderItself()
    {
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);

        Assert.Equal(cooked, GameClientLocator.FindCookedFolder(cooked));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoCookedFolder()
    {
        Assert.Null(GameClientLocator.FindCookedFolder(_dir));
        Assert.Null(GameClientLocator.FromRoot(_dir, "Nothing here"));
    }

    [Fact]
    public void ReturnsNullForAMissingFolder()
    {
        Assert.Null(GameClientLocator.FindCookedFolder(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void ReadsTheFormatFromPackageHeaders()
    {
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        for (int i = 0; i < 3; i++)
            WritePackage(Path.Combine(cooked, $"sample{i}.upk"), 868, 3);

        Assert.Equal(new PackageFormat(868, 3), GameClientLocator.ReadPackageFormat(cooked));
    }

    [Fact]
    public void ReportsUnknownWhenPackagesDisagree()
    {
        // A split sample means the install is not what it claims to be. Guessing
        // either answer would authorise a write against the wrong layout.
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        WritePackage(Path.Combine(cooked, "a.upk"), 868, 3);
        WritePackage(Path.Combine(cooked, "b.upk"), 894, 3);

        Assert.Equal(PackageFormat.Unknown, GameClientLocator.ReadPackageFormat(cooked));
    }

    [Fact]
    public void IgnoresFilesThatAreNotPackages()
    {
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        File.WriteAllText(Path.Combine(cooked, "notes.upk"), "this is not a package");

        Assert.Equal(PackageFormat.Unknown, GameClientLocator.ReadPackageFormat(cooked));
    }

    [Fact]
    public void BuildsAClientWithFormatAndPaths()
    {
        string cooked = Path.Combine(_dir, "UnrealEngine3", "GameFolder", "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        WritePackage(Path.Combine(cooked, "sample.upk"), 894, 3);

        GameClient? client = GameClientLocator.FromRoot(_dir, "Test Client");

        Assert.NotNull(client);
        Assert.Equal("Test Client", client!.DisplayName);
        Assert.Equal(cooked, client.CookedPath);
        Assert.Equal(new PackageFormat(894, 3), client.Format);
        Assert.True(client.Exists);
        Assert.False(client.HasTextureCacheManifest);
    }

    [Fact]
    public void FallsBackToTheFolderNameWhenNoDisplayNameIsGiven()
    {
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);

        GameClient? client = GameClientLocator.FromRoot(_dir, "   ");

        Assert.NotNull(client);
        Assert.Equal(new DirectoryInfo(_dir).Name, client!.DisplayName);
    }

    [Fact]
    public void DetectsTheTextureCacheManifest()
    {
        string cooked = Path.Combine(_dir, "CookedPCConsole");
        Directory.CreateDirectory(cooked);
        File.WriteAllBytes(Path.Combine(cooked, "TextureFileCacheManifest.bin"), [0]);

        GameClient? client = GameClientLocator.FromRoot(_dir, "With cache");

        Assert.NotNull(client);
        Assert.True(client!.HasTextureCacheManifest);
    }
}
