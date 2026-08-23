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

public sealed class CacheManifestTests
{
    /// <summary>Builds a manifest with the layout derived from the real file.</summary>
    private static byte[] BuildManifest(params (string Cache, string Path, (int Index, int Offset, int Size)[] Mips)[] entries)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(entries.Length);
        foreach ((string cache, string path, (int, int, int)[] mips) in entries)
        {
            byte[] cacheBytes = System.Text.Encoding.ASCII.GetBytes(cache + "\0");
            writer.Write(cacheBytes.Length);
            writer.Write(cacheBytes);

            byte[] pathBytes = System.Text.Encoding.ASCII.GetBytes(path + "\0");
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);

            writer.Write(Guid.NewGuid().ToByteArray());

            writer.Write(mips.Length);
            foreach ((int index, int offset, int size) in mips)
            {
                writer.Write(index);
                writer.Write(offset);
                writer.Write(size);
            }
        }

        writer.Flush();
        return buffer.ToArray();
    }

    [Fact]
    public void ReadsEntriesAndTheirMipLocations()
    {
        byte[] data = BuildManifest(
            ("CharTextures", "Hero.Hero_Diff", [(1, 95479, 227765), (2, 27460, 68531)]),
            ("Icons", "Ui.Ui_Icon", [(1, 0, 4096)]));

        TextureCacheManifest manifest = TextureCacheManifest.Read(data);

        Assert.Equal(2, manifest.Count);

        CachedTextureEntry? hero = manifest.Find("Hero.Hero_Diff");
        Assert.NotNull(hero);
        Assert.Equal("CharTextures", hero!.CacheName);
        Assert.Equal(2, hero.Mips.Count);
        Assert.Equal(new CachedMipLocation(1, 95479, 227765), hero.Mips[0]);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        // Object paths come out of the name table lower-cased, but the manifest
        // stores them with their original casing. A case-sensitive lookup would
        // find nothing at all.
        byte[] data = BuildManifest(("Icons", "MsMarvel_Skrull.MsMarvel_Skrull_Diff", [(1, 0, 64)]));

        TextureCacheManifest manifest = TextureCacheManifest.Read(data);

        Assert.NotNull(manifest.Find("msmarvel_skrull.msmarvel_skrull_diff"));
        Assert.NotNull(manifest.Find("MSMARVEL_SKRULL.MSMARVEL_SKRULL_DIFF"));
    }

    [Fact]
    public void LargestMipIsTheLowestIndex()
    {
        byte[] data = BuildManifest(("Icons", "A.B", [(4, 0, 100), (1, 900, 8000), (2, 400, 2000)]));

        TextureCacheManifest manifest = TextureCacheManifest.Read(data);
        CachedMipLocation? largest = manifest.Find("A.B")!.LargestMip;

        Assert.NotNull(largest);
        Assert.Equal(1, largest!.Value.MipIndex);
        Assert.Equal(8000, largest.Value.Size);
    }

    [Fact]
    public void MissingEntryReturnsNull()
    {
        TextureCacheManifest manifest = TextureCacheManifest.Read(BuildManifest(("Icons", "A.B", [(1, 0, 64)])));

        Assert.Null(manifest.Find("Nothing.Here"));
        Assert.Null(manifest.Find(""));
    }

    [Fact]
    public void RejectsAnImpossibleEntryCount()
    {
        byte[] data = new byte[64];
        BitConverter.GetBytes(int.MaxValue).CopyTo(data, 0);

        Assert.Throws<InvalidPackageException>(() => TextureCacheManifest.Read(data));
    }
}

/// <summary>Loads the real manifest from every installed client.</summary>
public sealed class RealCacheManifestTests
{
    private readonly ITestOutputHelper _output;

    public RealCacheManifestTests(ITestOutputHelper output) => _output = output;

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
    public void ParsesEveryEntryOfTheRealManifest()
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

            Assert.True(manifest is not null,
                $"{client.DisplayName}: the cache manifest did not parse. " +
                "Reaching the end of the file is the proof the layout is right, " +
                "because every entry is variable length.");

            Assert.True(manifest!.Count > 0);

            _output.WriteLine(
                $"{client.DisplayName}: {manifest.Count:N0} cached textures, layout {manifest.Layout}.");
        }
    }

    [Fact]
    public void CacheBackedTexturesAreFoundInTheManifest()
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

            int cacheBacked = 0, matched = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(6))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    TextureInfo? info = TextureInfo.TryRead(package, index);
                    if (info is null || !info.IsCacheBacked) continue;

                    cacheBacked++;
                    if (manifest.Find(info.ObjectPath) is not null) matched++;
                }
            }

            if (cacheBacked == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no cache-backed icons sampled.");
                continue;
            }

            double rate = matched / (double)cacheBacked;
            _output.WriteLine(
                $"{client.DisplayName}: {matched:N0} of {cacheBacked:N0} cache-backed icons " +
                $"located in the manifest ({rate:P0}).");

            Assert.True(rate > 0.5,
                $"{client.DisplayName}: only {rate:P0} of cache-backed icons were found in the manifest.");
        }
    }
}
