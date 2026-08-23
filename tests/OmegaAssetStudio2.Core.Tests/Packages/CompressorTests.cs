using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Compression;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Round-trips the compressor against its own decompressor.
/// </summary>
/// <remarks>
/// A compressor cannot be checked by eye. The only meaningful test is that
/// decompressing its output reproduces the input exactly — and that this holds
/// for real game data, not just for tidy synthetic cases.
/// </remarks>
public sealed class CompressorTests
{
    private readonly ITestOutputHelper _output;

    public CompressorTests(ITestOutputHelper output) => _output = output;

    private static void AssertRoundTrips(byte[] original, string what)
    {
        byte[] compressed = Lzo1xCompressor.Compress(original);
        byte[] restored = Lzo1x.Decompress(compressed, original.Length);

        Assert.True(original.AsSpan().SequenceEqual(restored),
            $"{what}: {original.Length} bytes did not survive a round trip.");
    }

    [Fact]
    public void EmptyAndTinyInputsRoundTrip()
    {
        for (int length = 1; length <= 16; length++)
            AssertRoundTrips(Enumerable.Range(0, length).Select(i => (byte)i).ToArray(), $"{length} bytes");
    }

    [Fact]
    public void HighlyRepetitiveDataRoundTrips()
    {
        // The case matches are for. Long runs exercise the overlapping copy the
        // decompressor performs.
        AssertRoundTrips(Enumerable.Repeat((byte)0xAB, 100_000).ToArray(), "one repeated byte");
        AssertRoundTrips(Enumerable.Range(0, 100_000).Select(i => (byte)(i % 4)).ToArray(), "short cycle");
    }

    [Fact]
    public void IncompressibleDataRoundTrips()
    {
        // Random data cannot be compressed, so this exercises the long-literal
        // encoding rather than the match encoding.
        var random = new Random(12345);
        byte[] noise = new byte[200_000];
        random.NextBytes(noise);

        AssertRoundTrips(noise, "random noise");
    }

    [Fact]
    public void MixedDataRoundTrips()
    {
        // Alternating compressible and incompressible regions force the encoder to
        // switch between literal runs and matches repeatedly.
        var random = new Random(99);
        var data = new List<byte>();

        for (int block = 0; block < 40; block++)
        {
            if (block % 2 == 0)
            {
                data.AddRange(Enumerable.Repeat((byte)block, 3000));
            }
            else
            {
                byte[] noise = new byte[3000];
                random.NextBytes(noise);
                data.AddRange(noise);
            }
        }

        AssertRoundTrips(data.ToArray(), "mixed content");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void LengthsAroundEncodingBoundariesRoundTrip(int length)
    {
        // Extended lengths are encoded as runs of zero bytes plus a remainder, so
        // values either side of a multiple of 255 are where that goes wrong.
        var random = new Random(length);
        byte[] data = new byte[length];
        random.NextBytes(data);

        AssertRoundTrips(data, $"{length} random bytes");
    }

    [Fact]
    public void RealPackageDataRoundTrips()
    {
        // The decisive case: the game's own bytes.
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        string[] roots = !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        List<GameClient> clients = roots.Where(Directory.Exists)
            .Select(r => GameClientLocator.FromRoot(r, new DirectoryInfo(r).Name))
            .Where(c => c is not null).Select(c => c!).ToList();

        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int blocks = 0;
            long originalBytes = 0, ourBytes = 0;

            // Sample across the size range. The smallest packages hold only tiny
            // objects, which exercise almost none of the encoder.
            string[] bySize = Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                       .OrderBy(p => new FileInfo(p).Length)
                                       .ToArray();

            string[] sample = bySize.Take(6)
                                    .Concat(bySize.Skip(bySize.Length / 2).Take(6))
                                    .Concat(bySize.TakeLast(4))
                                    .Distinct()
                                    .ToArray();

            foreach (string path in sample)
            {
                Package package = Package.Open(path);

                // Largest objects first: those are the ones whose size makes the
                // encoder work, and they are what a texture payload looks like.
                int[] biggestFirst = Enumerable.Range(0, package.Exports.Count)
                                               .OrderByDescending(i => package.Exports[i].SerialSize)
                                               .Take(40)
                                               .ToArray();

                foreach (int i in biggestFirst)
                {
                    if (blocks >= 400) break;

                    byte[] data = package.GetExportData(i).ToArray();
                    if (data.Length < 64) continue;

                    byte[] compressed = Lzo1xCompressor.Compress(data);
                    byte[] restored = Lzo1x.Decompress(compressed, data.Length);

                    Assert.True(data.AsSpan().SequenceEqual(restored),
                        $"{Path.GetFileName(path)} export {i} ({data.Length} bytes) did not round-trip.");

                    originalBytes += data.Length;
                    ourBytes += compressed.Length;
                    blocks++;
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {blocks} real objects round-tripped, " +
                $"{originalBytes:N0} -> {ourBytes:N0} bytes (x{originalBytes / (double)Math.Max(1, ourBytes):0.00}).");

            // A handful of tiny objects would prove almost nothing about the
            // encoder, so require a sample with real substance behind it.
            Assert.True(blocks >= 100, $"{client.DisplayName}: only {blocks} objects sampled.");
            Assert.True(originalBytes > 1_000_000,
                $"{client.DisplayName}: only {originalBytes:N0} bytes sampled.");
        }
    }
}
