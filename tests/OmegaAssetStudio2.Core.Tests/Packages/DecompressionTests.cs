using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Exercises decompression against real packages.
/// </summary>
/// <remarks>
/// There is no practical way to unit-test an LZO decoder from a hand-written
/// fixture — producing valid compressed input requires an encoder. The real
/// proof is that it expands the game's own data to exactly the size the package
/// declares, across thousands of blocks, and that the expanded bytes are
/// structurally what the header says they should be.
/// </remarks>
public sealed class DecompressionTests
{
    private readonly ITestOutputHelper _output;

    public DecompressionTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<string> CandidateRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [


        ];
    }

    private static List<GameClient> InstalledClients() =>
        CandidateRoots()
            .Where(Directory.Exists)
            .Select(r => GameClientLocator.FromRoot(r, new DirectoryInfo(r).Name))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    [Fact]
    public void ChunkHeadersAgreeWithThePackageChunkTable()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedChunks = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk").Take(40))
            {
                byte[] bytes = File.ReadAllBytes(path);
                PackageHeader header = PackageHeader.Read(bytes);
                if (!header.IsCompressed) continue;

                foreach (PackageChunk chunk in header.Chunks)
                {
                    ChunkHeader chunkHeader = ChunkHeader.Read(bytes, chunk.CompressedOffset);

                    // The two tables describe the same bytes from different
                    // directions; they must tie out exactly.
                    Assert.Equal(chunk.UncompressedSize, chunkHeader.TotalUncompressedSize);
                    Assert.Equal(chunk.CompressedSize, chunkHeader.TotalCompressedSize + chunkHeader.HeaderSize);

                    checkedChunks++;
                }
            }

            _output.WriteLine($"{client.DisplayName}: {checkedChunks} chunk headers tie out.");
        }
    }

    [Fact]
    public void ExpandsRealPackagesToTheDeclaredSize()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            // Sample across the whole size range, not just the small end. Tiny
            // packages are a single block and may not even be compressed, so on
            // their own they prove almost nothing about the decoder. The large
            // ones are where multi-block streams, long matches, and extended
            // length encodings actually occur.
            string[] bySize = Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                       .OrderBy(p => new FileInfo(p).Length)
                                       .ToArray();

            var sample = new List<string>();
            sample.AddRange(bySize.Take(10));                        // smallest
            sample.AddRange(bySize.Skip(bySize.Length / 2).Take(10)); // median
            sample.AddRange(bySize.TakeLast(10));                    // largest

            int packages = 0, multiBlockChunks = 0;
            long bytesOut = 0;

            foreach (string path in sample.Distinct())
            {
                byte[] bytes = File.ReadAllBytes(path);
                PackageHeader header = PackageHeader.Read(bytes);

                byte[] body = ChunkExpander.ExpandBody(header, bytes, out int bodyStart);

                long expected = header.Chunks.Sum(c => (long)c.UncompressedSize);
                Assert.Equal(expected, body.Length);
                Assert.Equal(header.NameOffset, bodyStart);

                foreach (PackageChunk chunk in header.Chunks)
                {
                    ChunkHeader chunkHeader = ChunkHeader.Read(bytes, chunk.CompressedOffset);
                    if (chunkHeader.Blocks.Count > 1) multiBlockChunks++;
                }

                packages++;
                bytesOut += body.Length;
            }

            // If nothing multi-block was seen, this test is not proving what it
            // claims to and the sample needs widening.
            Assert.True(multiBlockChunks > 0,
                $"{client.DisplayName}: no multi-block chunk was exercised.");

            _output.WriteLine(
                $"{client.DisplayName}: expanded {packages} packages to {bytesOut:N0} bytes " +
                $"({multiBlockChunks} multi-block chunks).");
        }
    }

    [Fact]
    public void ExpandsTheLargestPackagesInEachInstall()
    {
        // The heaviest real work the decoder will ever do.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string[] largest = Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                        .OrderByDescending(p => new FileInfo(p).Length)
                                        .Take(5)
                                        .ToArray();

            long bytesOut = 0;
            int blocks = 0;

            foreach (string path in largest)
            {
                byte[] bytes = File.ReadAllBytes(path);
                PackageHeader header = PackageHeader.Read(bytes);
                byte[] body = ChunkExpander.ExpandBody(header, bytes, out int bodyStart);

                Assert.Equal(header.Chunks.Sum(c => (long)c.UncompressedSize), body.Length);

                foreach (PackageChunk chunk in header.Chunks)
                    blocks += ChunkHeader.Read(bytes, chunk.CompressedOffset).Blocks.Count;

                // The expanded body must still parse as a name table. Size alone
                // would be satisfied by a buffer full of zeroes.
                NameTable names = NameTable.Read(body, header, bodyStart);
                Assert.Equal(header.NameCount, names.Count);
                Assert.True(names.Contains("none"));

                bytesOut += body.Length;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {largest.Length} largest packages, {blocks} blocks, " +
                $"{bytesOut:N0} bytes expanded and re-parsed.");
        }
    }

    [Fact]
    public void ExpandedBodyStartsWithAReadableNameTable()
    {
        // Size agreement alone would also be satisfied by garbage. The first
        // thing at the body's start is the name table, so decoding a plausible
        // run of names is what actually proves the bytes are right.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string path = Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                   .OrderBy(p => new FileInfo(p).Length)
                                   .First();

            byte[] bytes = File.ReadAllBytes(path);
            PackageHeader header = PackageHeader.Read(bytes);
            byte[] body = ChunkExpander.ExpandBody(header, bytes, out int bodyStart);

            NameTable names = NameTable.Read(body, header, bodyStart);

            Assert.Equal(header.NameCount, names.Count);

            foreach (NameEntry entry in names.Entries)
            {
                Assert.False(string.IsNullOrEmpty(entry.Name), $"{path}: a name decoded as empty.");
                Assert.True(entry.Name.Length < 512, $"{path}: name '{entry.Name}' is implausibly long.");
                Assert.All(entry.Name, c => Assert.True(
                    c is >= (char)32 and < (char)127,
                    $"{path}: name '{entry.Name}' contains a non-printable character."));
            }

            // "none" is the engine's null name and appears in every package.
            // Names are stored lower-cased, so this lookup must fold case — and
            // the table's own lookup is the thing being exercised here.
            Assert.True(names.Contains("None"), $"{path}: the null name is missing from the table.");
            Assert.True(names.Contains("none"));

            _output.WriteLine(
                $"{client.DisplayName} — {Path.GetFileName(path)}: {names.Count} names — " +
                string.Join(", ", names.Entries.Take(8).Select(e => e.Name)));
        }
    }
}
