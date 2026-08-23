using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Compression;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Textures;

public sealed class CachePayloadTests
{
    /// <summary>Decodes a payload the way the cache reader does, for verification.</summary>
    private static byte[] Unpack(byte[] payload)
    {
        Assert.Equal(PackageHeader.Magic, BitConverter.ToUInt32(payload, 0));

        int blockSize = BitConverter.ToInt32(payload, 4);
        int totalCompressed = BitConverter.ToInt32(payload, 8);
        int totalUncompressed = BitConverter.ToInt32(payload, 12);

        int blockCount = (totalUncompressed + blockSize - 1) / blockSize;
        var output = new byte[totalUncompressed];

        int tableAt = 16;
        int dataAt = 16 + (blockCount * 8);
        int written = 0;
        int compressedSeen = 0;

        for (int i = 0; i < blockCount; i++)
        {
            int compressedSize = BitConverter.ToInt32(payload, tableAt + (i * 8));
            int uncompressedSize = BitConverter.ToInt32(payload, tableAt + (i * 8) + 4);

            ReadOnlySpan<byte> block = payload.AsSpan(dataAt, compressedSize);
            Span<byte> target = output.AsSpan(written, uncompressedSize);

            if (compressedSize == uncompressedSize) block.CopyTo(target);
            else Lzo1x.Decompress(block, target);

            dataAt += compressedSize;
            written += uncompressedSize;
            compressedSeen += compressedSize;
        }

        Assert.Equal(totalCompressed, compressedSeen);
        Assert.Equal(totalUncompressed, written);
        return output;
    }

    [Fact]
    public void PayloadUnpacksToTheOriginalBytes()
    {
        // A realistic texture payload: mostly flat with some structure.
        byte[] raw = new byte[200_000];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i / 64) % 251);

        byte[] payload = TextureCacheWriter.BuildPayload(raw);

        Assert.True(raw.AsSpan().SequenceEqual(Unpack(payload)),
            "The payload did not unpack to what was put in.");
    }

    [Fact]
    public void PayloadSpanningManyBlocksUnpacks()
    {
        // Larger than one block, so the block table is exercised.
        var random = new Random(7);
        byte[] raw = new byte[600_000];
        random.NextBytes(raw);

        Assert.True(raw.AsSpan().SequenceEqual(Unpack(TextureCacheWriter.BuildPayload(raw))));
    }

    [Fact]
    public void IncompressibleBlocksAreStoredRatherThanGrown()
    {
        // Random data cannot compress. Storing it keeps the payload from growing
        // past the slot for no benefit.
        var random = new Random(11);
        byte[] raw = new byte[100_000];
        random.NextBytes(raw);

        byte[] payload = TextureCacheWriter.BuildPayload(raw);

        Assert.True(payload.Length < raw.Length * 1.05,
            $"An incompressible payload grew to {payload.Length:N0} from {raw.Length:N0}.");
        Assert.True(raw.AsSpan().SequenceEqual(Unpack(payload)));
    }

    [Fact]
    public void EmptyInputIsRejected()
        => Assert.Throws<ArgumentException>(() => TextureCacheWriter.BuildPayload([]));
}

/// <summary>Writes into copies of real texture caches.</summary>
public sealed class RealCacheWriteTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public RealCacheWriteTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Scratch.NewFolder("oas2-cachewrite");
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
    public void ReEncodingRealTexturesUsuallyFitsTheirSlot()
    {
        // The practical question: can a replacement realistically be written? This
        // re-encodes each texture's own pixels and asks whether the result fits
        // where they came from. A low rate would mean the feature is not viable.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            TextureCacheManifest? manifest = TextureCacheManifest.TryLoad(client.CookedPath);
            if (manifest is null) continue;

            var reader = new TextureReader(client.CookedPath);
            var writer = new TextureCacheWriter(client.CookedPath);

            int examined = 0, fits = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(4))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    if (examined >= 30) break;

                    TextureInfo? info = TextureInfo.TryRead(package, index);
                    if (info is null || !info.IsCacheBacked) continue;
                    if (!BlockEncoder.CanEncode(info.Format)) continue;

                    CachedTextureEntry? entry = manifest.Find(info.ObjectPath);
                    if (entry?.LargestMip is not { } slot || slot.Size <= 0) continue;

                    TextureImage? image = reader.TryDecode(package, info);
                    if (image is null) continue;

                    byte[] fitted = BlockEncoder.ResizeToFit(
                        image.Rgba, image.Width, image.Height, info.Width, info.Height);
                    byte[] encoded = BlockEncoder.Encode(fitted, info.Format, info.Width, info.Height);

                    CacheWriteResult check = writer.CanWrite(info.TextureCacheName, slot.Size, encoded);
                    examined++;
                    if (check.Succeeded) fits++;
                }
            }

            if (examined == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no cache-backed icons sampled.");
                continue;
            }

            double rate = fits / (double)examined;
            _output.WriteLine(
                $"{client.DisplayName}: {fits} of {examined} re-encoded textures fit their slot ({rate:P0}).");

            // Every sampled texture fits. This was 77% until the compressor
            // searched more than one candidate position per hash — a weaker ratio
            // is the difference between a texture being editable and being
            // refused, so a drop here is a real loss of capability.
            Assert.True(rate >= 0.95,
                $"{client.DisplayName}: only {rate:P0} of re-encoded textures fit, so the compressor " +
                "has regressed and textures that used to be replaceable no longer are.");
        }
    }

    [Fact]
    public async Task WritingIntoACacheCopyReadsBackAsTheNewImage()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            TextureCacheManifest? manifest = TextureCacheManifest.TryLoad(client.CookedPath);
            if (manifest is null) continue;

            // Find a cache-backed icon whose cache file is small enough to copy.
            (Package Package, TextureInfo Info, CachedMipLocation Slot, string Cache)? target = null;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(6))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    TextureInfo? info = TextureInfo.TryRead(package, index);
                    if (info is null || !info.IsCacheBacked || !BlockEncoder.CanEncode(info.Format)) continue;

                    CachedTextureEntry? entry = manifest.Find(info.ObjectPath);
                    if (entry?.LargestMip is not { } slot || slot.Size <= 0) continue;

                    string cacheFile = Path.Combine(client.CookedPath, info.TextureCacheName + ".tfc");
                    if (!File.Exists(cacheFile)) continue;
                    if (new FileInfo(cacheFile).Length > 400L * 1024 * 1024) continue;

                    target = (package, info, slot, cacheFile);
                    break;
                }
                if (target is not null) break;
            }

            if (target is null)
            {
                _output.WriteLine($"{client.DisplayName}: no suitable cache-backed icon.");
                continue;
            }

            // Work on a copy of the content folder's cache and manifest. Nothing
            // here may modify a game install.
            string sandbox = Path.Combine(_scratch, client.Id.ToString("N"));
            Directory.CreateDirectory(sandbox);

            string cacheCopy = Path.Combine(sandbox, Path.GetFileName(target.Value.Cache));
            File.Copy(target.Value.Cache, cacheCopy, overwrite: true);
            File.Copy(
                Path.Combine(client.CookedPath, TextureCacheManifest.FileName),
                Path.Combine(sandbox, TextureCacheManifest.FileName),
                overwrite: true);

            TextureInfo info2 = target.Value.Info;

            // A flat colour, so a successful write is unmistakable and compresses
            // well enough to fit.
            byte[] replacement = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++)
            {
                replacement[i * 4] = 0;
                replacement[(i * 4) + 1] = 255;
                replacement[(i * 4) + 2] = 0;
                replacement[(i * 4) + 3] = 255;
            }

            ReplaceResult result = await TextureReplacer.ReplaceCachedAsync(
                info2, sandbox, replacement, 64, 64);

            Assert.True(result.Succeeded, result.Message);
            Assert.True(OmegaAssetStudio2.Core.Workspace.Backup.BackupFileHelper.HasBackup(cacheCopy), "The cache was not backed up before writing.");

            // Read it back through the normal path and confirm the new pixels.
            var verifyReader = new TextureReader(sandbox);
            TextureImage? after = verifyReader.TryDecode(target.Value.Package, info2);

            Assert.NotNull(after);

            int centre = (((after!.Height / 2) * after.Width) + (after.Width / 2)) * 4;
            Assert.InRange(after.Rgba[centre + 1], 180, 255);   // green dominant
            Assert.InRange(after.Rgba[centre], 0, 90);          // little red

            _output.WriteLine(
                $"{client.DisplayName}: wrote '{info2.Name}' ({info2.Dimensions} {info2.FormatName}) " +
                $"into {Path.GetFileName(cacheCopy)} and read the new pixels back.");
        }
    }

    [Fact]
    public void ATextureThatDoesNotCompressEnoughIsRefused()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        GameClient client = clients[0];
        var writer = new TextureCacheWriter(client.CookedPath);

        TextureCacheManifest? manifest = TextureCacheManifest.TryLoad(client.CookedPath);
        if (manifest is null) return;

        // Incompressible data against a slot far too small must be refused, not
        // truncated — truncation would corrupt whatever follows it in the cache.
        var random = new Random(3);
        byte[] noise = new byte[200_000];
        random.NextBytes(noise);

        CacheWriteResult result = writer.CanWrite("icons", slotSize: 1024, noise);

        Assert.False(result.Succeeded);
        Assert.Contains("holds", result.Message);
        _output.WriteLine($"Correctly refused: {result.Message}");
    }
}
