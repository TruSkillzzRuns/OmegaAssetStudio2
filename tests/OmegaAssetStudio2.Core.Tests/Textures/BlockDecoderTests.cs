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

public sealed class BlockDecoderTests
{
    /// <summary>Builds one DXT1 block with both endpoints set to the same colour.</summary>
    private static byte[] SolidDxt1Block(ushort colour)
    {
        byte[] block = new byte[8];
        BitConverter.GetBytes(colour).CopyTo(block, 0);
        BitConverter.GetBytes(colour).CopyTo(block, 2);
        // All indices zero, so every pixel takes the first endpoint.
        return block;
    }

    [Fact]
    public void DecodesASolidRedBlock()
    {
        // 5:6:5 red is the top five bits set.
        byte[] rgba = BlockDecoder.Decode(SolidDxt1Block(0xF800), PixelFormat.Dxt1, 4, 4);

        Assert.Equal(4 * 4 * 4, rgba.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, rgba[(i * 4) + 0]);
            Assert.Equal(0, rgba[(i * 4) + 1]);
            Assert.Equal(0, rgba[(i * 4) + 2]);
            Assert.Equal(255, rgba[(i * 4) + 3]);
        }
    }

    [Fact]
    public void FullScaleWhiteDecodesToFullScale()
    {
        // Naively shifting 5-bit values left by three caps white at 248. The
        // decoder replicates high bits into low ones so white is really white.
        byte[] rgba = BlockDecoder.Decode(SolidDxt1Block(0xFFFF), PixelFormat.Dxt1, 4, 4);

        Assert.Equal(255, rgba[0]);
        Assert.Equal(255, rgba[1]);
        Assert.Equal(255, rgba[2]);
    }

    [Fact]
    public void HandlesImagesSmallerThanOneBlock()
    {
        // A 2x2 image still occupies a whole block; the decoder must clip rather
        // than write outside the image.
        byte[] rgba = BlockDecoder.Decode(SolidDxt1Block(0xF800), PixelFormat.Dxt1, 2, 2);

        Assert.Equal(2 * 2 * 4, rgba.Length);
        Assert.Equal(255, rgba[0]);
    }

    [Fact]
    public void RejectsTruncatedInput()
    {
        byte[] tooSmall = new byte[4];

        var ex = Assert.Throws<ArgumentException>(
            () => BlockDecoder.Decode(tooSmall, PixelFormat.Dxt1, 4, 4));
        Assert.Contains("needs", ex.Message);
    }

    [Fact]
    public void SwapsChannelOrderForUncompressedPixels()
    {
        // Stored blue-first; must come out red-first.
        byte[] source = [0x11, 0x22, 0x33, 0x44];

        byte[] rgba = BlockDecoder.Decode(source, PixelFormat.A8R8G8B8, 1, 1);

        Assert.Equal(0x33, rgba[0]);   // red
        Assert.Equal(0x22, rgba[1]);   // green
        Assert.Equal(0x11, rgba[2]);   // blue
        Assert.Equal(0x44, rgba[3]);   // alpha
    }

    [Fact]
    public void ExpandsGreyscaleToOpaqueRgba()
    {
        byte[] rgba = BlockDecoder.Decode([0x80], PixelFormat.G8, 1, 1);

        Assert.Equal(0x80, rgba[0]);
        Assert.Equal(0x80, rgba[1]);
        Assert.Equal(0x80, rgba[2]);
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void Dxt5AlphaEndpointsDecodeToTheirExactValues()
    {
        byte[] block = new byte[16];
        block[0] = 200;   // first alpha endpoint
        block[1] = 40;    // second
        // Alpha indices all zero -> every pixel takes the first endpoint.
        // Colour half: solid red.
        SolidDxt1Block(0xF800).CopyTo(block, 8);

        byte[] rgba = BlockDecoder.Decode(block, PixelFormat.Dxt5, 4, 4);

        Assert.Equal(200, rgba[3]);
        Assert.Equal(200, rgba[(15 * 4) + 3]);
    }

    [Fact]
    public void KnowsWhatItCanDecode()
    {
        Assert.True(BlockDecoder.CanDecode(PixelFormat.Dxt1));
        Assert.True(BlockDecoder.CanDecode(PixelFormat.Dxt5));
        Assert.True(BlockDecoder.CanDecode(PixelFormat.A8R8G8B8));
        Assert.False(BlockDecoder.CanDecode(PixelFormat.FloatRgba));
        Assert.False(BlockDecoder.CanDecode(PixelFormat.Unknown));
    }
}

/// <summary>Decodes real icons end to end.</summary>
public sealed class RealTextureDecodeTests
{
    private readonly ITestOutputHelper _output;

    public RealTextureDecodeTests(ITestOutputHelper output) => _output = output;

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
    public void DecodesRealIconsToPlausibleImages()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            var reader = new TextureReader(client.CookedPath);
            int attempted = 0, decoded = 0, fromCache = 0, nonBlank = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(4))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d").Take(60))
                {
                    TextureInfo? info = TextureInfo.TryRead(package, index);
                    if (info is null || !BlockDecoder.CanDecode(info.Format)) continue;

                    attempted++;

                    TextureImage? image = reader.TryDecode(package, info);
                    if (image is null) continue;

                    decoded++;
                    if (info.IsCacheBacked) fromCache++;

                    Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);

                    // A decode that silently produced an empty buffer would still
                    // have the right length, so check the pixels carry signal.
                    bool hasColour = false;
                    for (int i = 0; i < image.Rgba.Length && !hasColour; i += 4)
                    {
                        if (image.Rgba[i] != 0 || image.Rgba[i + 1] != 0 || image.Rgba[i + 2] != 0)
                            hasColour = true;
                    }
                    if (hasColour) nonBlank++;
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: decoded {decoded}/{attempted} icons " +
                $"({fromCache} from the texture cache, {nonBlank} with visible pixels).");

            Assert.True(decoded > 0, $"{client.DisplayName}: nothing decoded.");
            Assert.True(nonBlank > decoded * 0.8,
                $"{client.DisplayName}: only {nonBlank} of {decoded} decoded icons had any colour.");
        }
    }
}
