using OmegaAssetStudio2.Core.Packages.Compression;

namespace OmegaAssetStudio2.Core.Packages;

/// <summary>One compressed block inside a chunk.</summary>
public readonly record struct ChunkBlock(int CompressedSize, int UncompressedSize);

/// <summary>
/// The header that precedes the compressed data of a chunk.
/// </summary>
/// <remarks>
/// Layout derived from real packages: magic, the maximum size of one block, the
/// compressed and uncompressed totals, then one size pair per block.
/// <para>
/// Verified on a real chunk: block sizes summed to exactly the declared totals,
/// and the chunk's compressed size in the package header exceeded the total here
/// by precisely the size of this header, which is how the two tables tie
/// together.
/// </para>
/// </remarks>
public sealed record ChunkHeader
{
    public required int BlockSize { get; init; }
    public required int TotalCompressedSize { get; init; }
    public required int TotalUncompressedSize { get; init; }
    public required IReadOnlyList<ChunkBlock> Blocks { get; init; }

    /// <summary>Bytes this header occupies, before the first block's data.</summary>
    public int HeaderSize => (sizeof(int) * 4) + (Blocks.Count * sizeof(int) * 2);

    public static ChunkHeader Read(ReadOnlySpan<byte> package, int offset)
    {
        var cursor = new PackageCursor(package, offset);

        uint magic = cursor.ReadUInt32("chunk magic");
        if (magic != PackageHeader.Magic)
            throw new InvalidPackageException(
                $"Chunk at offset {offset} has magic 0x{magic:X8}, expected 0x{PackageHeader.Magic:X8}.");

        int blockSize = cursor.ReadInt32("chunk block size");
        int totalCompressed = cursor.ReadInt32("chunk compressed size");
        int totalUncompressed = cursor.ReadInt32("chunk uncompressed size");

        if (blockSize <= 0)
            throw new InvalidPackageException($"Chunk at {offset} declares block size {blockSize}.");
        if (totalUncompressed < 0 || totalCompressed < 0)
            throw new InvalidPackageException(
                $"Chunk at {offset} declares negative sizes ({totalCompressed}/{totalUncompressed}).");

        // The block count is implied rather than stored.
        int blockCount = (totalUncompressed + blockSize - 1) / blockSize;

        // Bound before allocating, so a corrupt size cannot turn into a huge
        // allocation instead of a clear error.
        if ((long)blockCount * sizeof(int) * 2 > cursor.Remaining)
            throw new InvalidPackageException(
                $"Chunk at {offset} implies {blockCount} blocks, which does not fit in the remaining " +
                $"{cursor.Remaining} bytes.");

        var blocks = new ChunkBlock[blockCount];
        long sumCompressed = 0, sumUncompressed = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int compressed = cursor.ReadInt32($"block {i} compressed size");
            int uncompressed = cursor.ReadInt32($"block {i} uncompressed size");

            if (compressed < 0 || uncompressed < 0)
                throw new InvalidPackageException($"Block {i} of the chunk at {offset} has a negative size.");
            if (uncompressed > blockSize)
                throw new InvalidPackageException(
                    $"Block {i} of the chunk at {offset} expands to {uncompressed}, " +
                    $"larger than the declared block size {blockSize}.");

            blocks[i] = new ChunkBlock(compressed, uncompressed);
            sumCompressed += compressed;
            sumUncompressed += uncompressed;
        }

        // The two tables must agree. If they do not, the chunk table was misread
        // and expanding it would produce a body with holes in it.
        if (sumUncompressed != totalUncompressed)
            throw new InvalidPackageException(
                $"Chunk at {offset}: block sizes sum to {sumUncompressed} but the header declares " +
                $"{totalUncompressed}.");
        if (sumCompressed != totalCompressed)
            throw new InvalidPackageException(
                $"Chunk at {offset}: compressed block sizes sum to {sumCompressed} but the header " +
                $"declares {totalCompressed}.");

        return new ChunkHeader
        {
            BlockSize = blockSize,
            TotalCompressedSize = totalCompressed,
            TotalUncompressedSize = totalUncompressed,
            Blocks = blocks,
        };
    }
}

/// <summary>
/// Expands a package's compressed chunks into the contiguous body the tables and
/// exports are addressed against.
/// </summary>
public static class ChunkExpander
{
    /// <summary>
    /// Expands every chunk into one buffer, laid out so that offsets from the
    /// package header index straight into it.
    /// </summary>
    /// <remarks>
    /// The returned buffer starts at the package's first uncompressed offset, not
    /// at file offset zero. <paramref name="bodyStart"/> reports that origin so
    /// callers can convert a header offset into a body index.
    /// </remarks>
    public static byte[] ExpandBody(PackageHeader header, ReadOnlySpan<byte> package, out int bodyStart)
    {
        if (!header.IsCompressed)
        {
            // Nothing to expand; the file already is the body.
            bodyStart = 0;
            return package.ToArray();
        }

        bodyStart = header.Chunks[0].UncompressedOffset;

        long totalLength = 0;
        foreach (PackageChunk chunk in header.Chunks)
            totalLength += chunk.UncompressedSize;

        if (totalLength > int.MaxValue)
            throw new InvalidPackageException($"Package body is {totalLength} bytes, which is too large to expand.");

        byte[] body = new byte[totalLength];

        foreach (PackageChunk chunk in header.Chunks)
        {
            int writeAt = chunk.UncompressedOffset - bodyStart;
            if (writeAt < 0 || writeAt + chunk.UncompressedSize > body.Length)
                throw new InvalidPackageException(
                    $"Chunk at uncompressed offset {chunk.UncompressedOffset} does not fit the expanded body.");

            ExpandChunk(package, chunk, body.AsSpan(writeAt, chunk.UncompressedSize));
        }

        return body;
    }

    /// <summary>Expands one chunk into <paramref name="destination"/>.</summary>
    public static void ExpandChunk(ReadOnlySpan<byte> package, PackageChunk chunk, Span<byte> destination)
    {
        ChunkHeader chunkHeader = ChunkHeader.Read(package, chunk.CompressedOffset);

        if (chunkHeader.TotalUncompressedSize != destination.Length)
            throw new InvalidPackageException(
                $"Chunk at {chunk.CompressedOffset} expands to {chunkHeader.TotalUncompressedSize} bytes " +
                $"but the package chunk table reserves {destination.Length}.");

        int readAt = chunk.CompressedOffset + chunkHeader.HeaderSize;
        int writeAt = 0;

        for (int i = 0; i < chunkHeader.Blocks.Count; i++)
        {
            ChunkBlock block = chunkHeader.Blocks[i];

            if (readAt + block.CompressedSize > package.Length)
                throw new InvalidPackageException(
                    $"Block {i} of the chunk at {chunk.CompressedOffset} runs past the end of the file.");

            ReadOnlySpan<byte> compressed = package.Slice(readAt, block.CompressedSize);
            Span<byte> target = destination.Slice(writeAt, block.UncompressedSize);

            // Every block is an LZO stream, with no exception for one that did
            // not get smaller. Version 1's reader has been run over this whole
            // install and treats them all the same way, so the game does too;
            // reading equal sizes as "stored plainly" would be inventing a rule
            // the format does not have, and — worse — inviting this application
            // to write blocks that way.
            Lzo1x.Decompress(compressed, target);

            readAt += block.CompressedSize;
            writeAt += block.UncompressedSize;
        }
    }
}
