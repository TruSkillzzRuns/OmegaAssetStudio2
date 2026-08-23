using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Textures;

public sealed class PixelFormatTests
{
    [Theory]
    [InlineData("PF_DXT1", PixelFormat.Dxt1)]
    [InlineData("pf_dxt5", PixelFormat.Dxt5)]
    [InlineData("PF_A8R8G8B8", PixelFormat.A8R8G8B8)]
    [InlineData("PF_G8", PixelFormat.G8)]
    [InlineData("something_else", PixelFormat.Unknown)]
    [InlineData(null, PixelFormat.Unknown)]
    public void ParsesEngineFormatNames(string? name, PixelFormat expected)
        => Assert.Equal(expected, PixelFormatExtensions.Parse(name));

    [Theory]
    [InlineData(PixelFormat.Dxt1, 40, 40, 800)]     // 10x10 blocks at 8 bytes
    [InlineData(PixelFormat.Dxt5, 40, 40, 1600)]    // 10x10 blocks at 16 bytes
    [InlineData(PixelFormat.Dxt1, 128, 128, 8192)]
    [InlineData(PixelFormat.A8R8G8B8, 16, 16, 1024)]
    [InlineData(PixelFormat.G8, 32, 32, 1024)]
    public void ComputesMipSize(PixelFormat format, int width, int height, int expected)
        => Assert.Equal(expected, format.MipByteSize(width, height));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void BlockFormatsRoundUpToWholeBlocks(int width, int height)
    {
        // A mip smaller than one block still costs a whole block. Computing this
        // as width*height/16 would under-read the data and shear the image.
        Assert.Equal(8, PixelFormat.Dxt1.MipByteSize(width, height));
        Assert.Equal(16, PixelFormat.Dxt5.MipByteSize(width, height));
    }

    [Fact]
    public void ZeroAndNegativeSizesCostNothing()
    {
        Assert.Equal(0, PixelFormat.Dxt1.MipByteSize(0, 16));
        Assert.Equal(0, PixelFormat.Dxt1.MipByteSize(16, -1));
    }

    [Fact]
    public void KnowsWhichFormatsAreBlockCompressed()
    {
        Assert.True(PixelFormat.Dxt1.IsBlockCompressed());
        Assert.True(PixelFormat.Dxt5.IsBlockCompressed());
        Assert.True(PixelFormat.BC7.IsBlockCompressed());
        Assert.False(PixelFormat.A8R8G8B8.IsBlockCompressed());
        Assert.False(PixelFormat.G8.IsBlockCompressed());
    }
}

/// <summary>Reads texture descriptions out of real icon packages.</summary>
public sealed class RealTextureInfoTests
{
    private readonly ITestOutputHelper _output;

    public RealTextureInfoTests(ITestOutputHelper output) => _output = output;

    private static List<GameClient> InstalledClients()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        string[] roots = !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return roots.Where(Directory.Exists)
                    .Select(r => GameClientLocator.FromRoot(r, new DirectoryInfo(r).Name))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();
    }

    [Fact]
    public async Task ScansIconPackagesAcrossEveryInstall()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        var catalog = new TextureCatalog();

        foreach (GameClient client in clients)
        {
            int skipped = 0;
            IReadOnlyList<TextureInfo> textures = await catalog.ScanAsync(
                client, "ICO__*.upk", onError: (_, _) => skipped++);

            Assert.NotEmpty(textures);

            foreach (TextureInfo texture in textures)
            {
                Assert.True(texture.Width > 0 && texture.Height > 0);
                Assert.False(string.IsNullOrWhiteSpace(texture.Name));
                Assert.False(string.IsNullOrWhiteSpace(texture.ObjectPath));
                Assert.NotEqual(PixelFormat.Unknown, texture.Format);
            }

            var formats = textures.GroupBy(t => t.FormatName)
                                  .OrderByDescending(g => g.Count())
                                  .Select(g => $"{g.Key} x{g.Count():N0}");

            int cacheBacked = textures.Count(t => t.IsCacheBacked);

            _output.WriteLine(
                $"{client.DisplayName}: {textures.Count:N0} icons, {cacheBacked:N0} cache-backed, " +
                $"{skipped} package(s) unreadable — {string.Join(", ", formats)}");
        }
    }
}
