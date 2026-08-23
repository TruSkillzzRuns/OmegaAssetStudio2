using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Compression;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Checks this application's LZO against the reference implementation.
/// </summary>
/// <remarks>
/// Everything written here has so far been read back by the same code that
/// wrote it, which proves only that it agrees with itself. The game uses the
/// real LZO library, and version 1 calls that same library through
/// <c>lzo2_64.dll</c> — so decoding this application's output with it is the
/// only check that says anything about what the game will make of it.
/// <para>
/// Skipped when the library is not beside the tests, so nothing here depends on
/// a machine having it.
/// </para>
/// </remarks>
public sealed class LzoAgainstReferenceTests
{
    private const string Library = "lzo2_64.dll";

    [DllImport(Library, EntryPoint = "__lzo_init_v2")]
    private static extern int LzoInit(uint v, int s1, int s2, int s3, int s4, int s5, int s6, int s7, int s8, int s9);

    [DllImport(Library, EntryPoint = "lzo1x_decompress_safe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LzoDecompress(byte[] source, int sourceLength, byte[] destination, ref int destinationLength, byte[]? work);

    private readonly ITestOutputHelper _output;

    public LzoAgainstReferenceTests(ITestOutputHelper output) => _output = output;

    private static bool Available => File.Exists(
        Path.Combine(AppContext.BaseDirectory, Library));

    /// <summary>Decodes with the reference library, or says why it could not.</summary>
    private static (byte[] Result, int Code) Reference(byte[] compressed, int expandsTo)
    {
        LzoInit(1, -1, -1, -1, -1, -1, -1, -1, -1, -1);

        byte[] destination = new byte[expandsTo];
        int length = expandsTo;

        int code = LzoDecompress(compressed, compressed.Length, destination, ref length, null);

        return (destination, code);
    }

    [Fact]
    public void WhatThisApplicationCompressesTheRealLibraryCanRead()
    {
        if (!Available)
        {
            _output.WriteLine($"{Library} is not beside the tests; nothing checked.");
            return;
        }

        foreach (GameClient client in TestGames.Installed)
        {
            int blocks = 0, mismatches = 0, failures = 0;

            foreach (string path in Directory
                         .EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                         .Take(4))
            {
                Package package = Package.Open(path);
                byte[] body = package.CopyBody(out _);

                const int blockSize = 128 * 1024;

                for (int at = 0; at < body.Length; at += blockSize)
                {
                    int length = Math.Min(blockSize, body.Length - at);
                    ReadOnlySpan<byte> plain = body.AsSpan(at, length);

                    byte[] packed = Lzo1xCompressor.Compress(plain);

                    (byte[] result, int code) = Reference(packed, length);

                    blocks++;

                    if (code != 0) { failures++; continue; }
                    if (!plain.SequenceEqual(result)) mismatches++;
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {blocks} block(s) compressed here and decoded by the real library — " +
                $"{failures} it refused, {mismatches} it decoded to something else.");

            Assert.Equal(0, failures);
            Assert.Equal(0, mismatches);

            return;
        }
    }

    [Fact]
    public void TheRealLibraryAgreesWithThisApplicationOnTheGamesOwnData()
    {
        // The other direction, as a control: if this fails, the comparison above
        // is not evidence of anything, because the library is being called
        // wrongly rather than the compressor being wrong.
        if (!Available)
        {
            _output.WriteLine($"{Library} is not beside the tests; nothing checked.");
            return;
        }

        foreach (GameClient client in TestGames.Installed)
        {
            string path = Directory
                .EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;

            if (path.Length == 0) continue;

            byte[] raw = File.ReadAllBytes(path);
            PackageHeader header = PackageHeader.Read(raw);

            Assert.True(header.IsCompressed);

            PackageChunk chunk = header.Chunks[0];
            ChunkHeader chunkHeader = ChunkHeader.Read(raw, chunk.CompressedOffset);

            int readAt = chunk.CompressedOffset + chunkHeader.HeaderSize;
            ChunkBlock block = chunkHeader.Blocks[0];

            byte[] compressed = raw.AsSpan(readAt, block.CompressedSize).ToArray();

            (byte[] reference, int code) = Reference(compressed, block.UncompressedSize);

            byte[] ours = new byte[block.UncompressedSize];
            Lzo1x.Decompress(compressed, ours);

            _output.WriteLine(
                $"{client.DisplayName}: the game's own first block, {block.CompressedSize:N0} bytes to " +
                $"{block.UncompressedSize:N0} — library returned {code}, and the two decoders " +
                $"{(reference.SequenceEqual(ours) ? "agree" : "DISAGREE")}.");

            Assert.Equal(0, code);
            Assert.True(reference.SequenceEqual(ours), "the two decoders disagree on the game's own data.");

            return;
        }
    }
}
