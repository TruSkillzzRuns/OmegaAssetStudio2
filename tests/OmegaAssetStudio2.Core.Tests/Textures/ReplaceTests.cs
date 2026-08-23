using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Textures;

public sealed class BlockEncoderTests
{
    private static byte[] SolidImage(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = a;
        }
        return rgba;
    }

    [Theory]
    [InlineData(PixelFormat.Dxt1)]
    [InlineData(PixelFormat.Dxt5)]
    [InlineData(PixelFormat.A8R8G8B8)]
    public void EncodedOutputIsExactlyTheSizeTheFormatDemands(PixelFormat format)
    {
        byte[] encoded = BlockEncoder.Encode(SolidImage(16, 16, 200, 100, 50), format, 16, 16);

        Assert.Equal(format.MipByteSize(16, 16), encoded.Length);
    }

    [Theory]
    [InlineData(PixelFormat.Dxt1)]
    [InlineData(PixelFormat.Dxt5)]
    [InlineData(PixelFormat.A8R8G8B8)]
    public void AColourSurvivesAnEncodeDecodeRoundTrip(PixelFormat format)
    {
        byte[] original = SolidImage(8, 8, 220, 120, 40);

        byte[] encoded = BlockEncoder.Encode(original, format, 8, 8);
        byte[] decoded = BlockDecoder.Decode(encoded, format, 8, 8);

        // Block compression is lossy, so this checks the colour is close rather
        // than identical. A wrong endpoint ordering or index mapping would be off
        // by far more than this tolerance.
        for (int i = 0; i < 8 * 8; i++)
        {
            Assert.InRange(decoded[i * 4], 200, 240);
            Assert.InRange(decoded[(i * 4) + 1], 100, 140);
            Assert.InRange(decoded[(i * 4) + 2], 20, 60);
            Assert.Equal(255, decoded[(i * 4) + 3]);
        }
    }

    [Fact]
    public void Dxt5PreservesPartialAlpha()
    {
        byte[] original = SolidImage(8, 8, 255, 255, 255, a: 128);

        byte[] decoded = BlockDecoder.Decode(
            BlockEncoder.Encode(original, PixelFormat.Dxt5, 8, 8), PixelFormat.Dxt5, 8, 8);

        for (int i = 0; i < 8 * 8; i++)
            Assert.InRange(decoded[(i * 4) + 3], 118, 138);
    }

    [Fact]
    public void Dxt1PreservesACutout()
    {
        // Half transparent, half opaque. DXT1 carries only one bit of alpha, so
        // the test is that transparency survives at all, in the right places.
        byte[] rgba = SolidImage(8, 8, 255, 0, 0);
        for (int i = 0; i < 32; i++) rgba[(i * 4) + 3] = 0;

        byte[] decoded = BlockDecoder.Decode(
            BlockEncoder.Encode(rgba, PixelFormat.Dxt1, 8, 8), PixelFormat.Dxt1, 8, 8);

        Assert.Equal(0, decoded[3]);              // first pixel transparent
        Assert.Equal(255, decoded[(60 * 4) + 3]); // last row opaque
    }

    [Fact]
    public void DownsampleHalvesEachDimension()
    {
        byte[] half = BlockEncoder.Downsample(SolidImage(16, 16, 100, 100, 100), 16, 16);

        Assert.Equal(8 * 8 * 4, half.Length);
        Assert.Equal(100, half[0]);
    }

    [Fact]
    public void DownsampleStopsAtOnePixel()
    {
        byte[] one = BlockEncoder.Downsample(SolidImage(1, 1, 50, 50, 50), 1, 1);
        Assert.Equal(4, one.Length);
    }

    [Fact]
    public void ResizePreservesAspectRatioAndPadsTheRest()
    {
        // A wide image into a square slot must keep its proportions and be
        // centred, not stretched.
        byte[] wide = SolidImage(64, 16, 10, 20, 30);

        byte[] fitted = BlockEncoder.ResizeToFit(wide, 64, 16, 32, 32);

        Assert.Equal(32 * 32 * 4, fitted.Length);

        // Middle row carries the image; the top row is padding.
        Assert.Equal(10, fitted[((16 * 32) + 16) * 4]);
        Assert.Equal(0, fitted[3]);
    }
}

/// <summary>Replaces textures in copies of real packages.</summary>
public sealed class RealReplaceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public RealReplaceTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Scratch.NewFolder("oas2-replace");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

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
    public async Task ReplacingAnIconWritesPixelsThatReadBack()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            // Work on a copy. Nothing in the test suite may modify a game install.
            string? source = Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk")
                                      .OrderBy(p => new FileInfo(p).Length)
                                      .FirstOrDefault(p =>
                                      {
                                          Package pkg = Package.Open(p);
                                          return pkg.FindExportsOfClass("texture2d")
                                                    .Select(i => TextureInfo.TryRead(pkg, i))
                                                    .Any(t => t is not null && !t.IsCacheBacked &&
                                                              BlockEncoder.CanEncode(t.Format));
                                      });

            if (source is null)
            {
                _output.WriteLine($"{client.DisplayName}: no replaceable icon found.");
                continue;
            }

            string copy = Path.Combine(_scratch, $"{client.Id:N}-{Path.GetFileName(source)}");
            File.Copy(source, copy, overwrite: true);

            Package package = Package.Open(copy);
            TextureInfo target = package.FindExportsOfClass("texture2d")
                                        .Select(i => TextureInfo.TryRead(package, i))
                                        .First(t => t is not null && !t.IsCacheBacked &&
                                                    BlockEncoder.CanEncode(t.Format))!;

            // A flat, unmistakable colour, so a successful write is obvious.
            const byte red = 255, green = 0, blue = 0;
            byte[] replacement = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++)
            {
                replacement[i * 4] = red;
                replacement[(i * 4) + 1] = green;
                replacement[(i * 4) + 2] = blue;
                replacement[(i * 4) + 3] = 255;
            }

            ReplaceResult result = await TextureReplacer.ReplaceAsync(
                package, target, replacement, 64, 64);

            Assert.True(result.Succeeded, result.Message);
            Assert.True(OmegaAssetStudio2.Core.Workspace.Backup.BackupFileHelper.HasBackup(copy), "No pristine backup was taken.");

            // Re-open from disk and decode: the pixels must be the new ones.
            Package reloaded = Package.Open(copy);
            TextureInfo reloadedInfo = TextureInfo.TryRead(reloaded, target.ExportIndex)!;
            var reader = new TextureReader(client.CookedPath);
            TextureImage? image = reader.TryDecode(reloaded, reloadedInfo);

            Assert.NotNull(image);

            // Sample the centre, away from any letterbox padding.
            int centre = (((image!.Height / 2) * image.Width) + (image.Width / 2)) * 4;
            Assert.InRange(image.Rgba[centre], 200, 255);
            Assert.InRange(image.Rgba[centre + 1], 0, 60);
            Assert.InRange(image.Rgba[centre + 2], 0, 60);

            // Dimensions and format must be untouched.
            Assert.Equal(target.Width, reloadedInfo.Width);
            Assert.Equal(target.Height, reloadedInfo.Height);
            Assert.Equal(target.FormatName, reloadedInfo.FormatName);

            _output.WriteLine(
                $"{client.DisplayName}: replaced '{target.Name}' ({target.Dimensions} {target.FormatName}) " +
                "and read the new pixels back.");
        }
    }

    [Fact]
    public void CacheBackedTexturesAreRefusedRatherThanCorrupted()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(6))
            {
                Package package = Package.Open(path);

                TextureInfo? cached = package.FindExportsOfClass("texture2d")
                                             .Select(i => TextureInfo.TryRead(package, i))
                                             .FirstOrDefault(t => t is not null && t.IsCacheBacked);
                if (cached is null) continue;

                ReplaceResult result = TextureReplacer.CanReplace(package, cached);

                Assert.False(result.Succeeded);
                Assert.Equal(ReplaceRefusal.PixelsAreInTextureCache, result.Refusal);

                _output.WriteLine($"{client.DisplayName}: correctly refused '{cached.Name}'.");
                break;
            }
        }
    }
}
