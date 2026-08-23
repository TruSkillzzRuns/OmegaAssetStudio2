using System.Buffers.Binary;
using OmegaAssetStudio2.Core.Packages.Compression;

namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// Writes a package back in the compressed form the game ships.
/// </summary>
/// <remarks>
/// Every one of the 14,437 packages this game installs is compressed; not one
/// is stored plainly. Writing a plain one and handing it to the game produced a
/// client that hung at the loading screen even when the contents were the
/// game's own, unaltered — so the plain form is not something this game reads,
/// whatever the engine is capable of in general.
/// <para>
/// The uncompressed layout is preserved exactly: the same chunk boundaries in
/// uncompressed space, so every offset recorded anywhere in the file — the
/// tables, each object's position, and the texture mips that record their own
/// position — still points where it did. Only how those bytes are packed into
/// the file changes, and the last chunk absorbs any change in the body's
/// length.
/// </para>
/// </remarks>
public static class PackageCompressor
{
    /// <summary>
    /// How much of the body one block holds, when the original does not say.
    /// </summary>
    private const int DefaultBlockSize = 128 * 1024;

    /// <summary>
    /// Packs a body back into a package file, compressed as it was found.
    /// </summary>
    /// <param name="package">The package the body came from.</param>
    /// <param name="body">The whole body, starting at the package's body offset.</param>
    public static byte[] Build(Package package, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(body);

        PackageHeader header = package.Header;

        if (!header.IsCompressed)
            throw new PackageRebuildException("This package was not compressed, so it is not written as if it were.");

        package.CopyBody(out int bodyStart);

        // The header on disk runs to where the first chunk's data begins, and
        // it holds the chunk table. That is longer than the body's own start,
        // because the table exists only on disk: in the uncompressed stream the
        // header is shorter by exactly the table, which is why every offset
        // recorded in the file is measured against the shorter one.
        int headerEnd = header.Chunks[0].CompressedOffset;

        byte[] headerBytes = package.CopyFilePrefix(headerEnd);

        if (headerBytes.Length != headerEnd)
            throw new PackageRebuildException("This package is shorter than its own header claims.");

        // Chunk boundaries are kept exactly as they were, so nothing that
        // records a position in the body has to move. Only the last chunk's
        // length changes, because that is where a body grows or shrinks.
        var bounds = new List<(int Offset, int Size)>(header.Chunks.Count);

        for (int i = 0; i < header.Chunks.Count; i++)
        {
            PackageChunk chunk = header.Chunks[i];
            int start = chunk.UncompressedOffset - bodyStart;

            int size = i == header.Chunks.Count - 1
                ? body.Length - start
                : chunk.UncompressedSize;

            if (start < 0 || size < 0 || start + size > body.Length)
            {
                throw new PackageRebuildException(
                    $"Chunk {i} covers {start}..{start + size} of a {body.Length}-byte body.");
            }

            bounds.Add((start, size));
        }

        int blockSize = BlockSizeOf(package, header) ?? DefaultBlockSize;

        int tableAt = PackageWriterInternals.CompressionFlagsOffset(header) + (sizeof(int) * 2);

        var file = new MemoryStream(body.Length);
        file.Write(headerBytes);

        var written = new PackageChunk[header.Chunks.Count];

        for (int i = 0; i < bounds.Count; i++)
        {
            (int start, int size) = bounds[i];

            int compressedOffset = (int)file.Position;
            int compressedSize = WriteChunk(file, body.AsSpan(start, size), blockSize);

            written[i] = new PackageChunk(
                UncompressedOffset: bodyStart + start,
                UncompressedSize: size,
                CompressedOffset: compressedOffset,
                CompressedSize: compressedSize);
        }

        byte[] output = file.ToArray();

        WriteChunkTable(output, header, written, tableAt);

        return output;
    }

    /// <summary>
    /// Writes one chunk: its own header, then each block's data.
    /// </summary>
    /// <returns>Bytes the chunk occupies, header included.</returns>

    /// <summary>
    /// Packs a package that is not packed, choosing fresh chunk boundaries.
    /// </summary>
    /// <remarks>
    /// The other way round from <see cref="Build"/>, which keeps a package
    /// exactly as it was found. This is for a package that has grown - one that
    /// has had objects added - where the old boundaries no longer describe
    /// anything, and where the file has no chunk table at all because it was
    /// written plainly.
    /// <para>
    /// The arrangement was measured on this game rather than assumed. A packed
    /// file is the plain header, then a table of sixteen bytes per chunk, then
    /// the packed data: one costume package begins its data at 357 with its names
    /// at 341, which is one chunk of sixteen bytes between them, and another at
    /// 261 with names at 213, which is three.
    /// </para>
    /// <para>
    /// Chunks are cut at a million bytes, which is about what this game's own
    /// files use - one costume's three cover a body of three million.
    /// </para>
    /// </remarks>
    public static byte[] Pack(Package plain, int chunkSize = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(plain);

        PackageHeader header = plain.Header;

        if (header.IsCompressed)
            throw new PackageRebuildException("This package is packed already.");

        if (chunkSize < DefaultBlockSize) chunkSize = DefaultBlockSize;

        // A package that is not packed has no separate body: the whole file is
        // one piece, and what becomes the packed part starts where the names do.
        byte[] whole = plain.CopyBody(out int began);

        if (began != 0)
            throw new PackageRebuildException($"A plain package should begin at 0, not {began}.");

        int bodyStart = header.NameOffset;

        if (bodyStart <= 0 || bodyStart > whole.Length)
            throw new PackageRebuildException($"The names are said to be at {bodyStart}.");

        byte[] headerBytes = whole.AsSpan(0, bodyStart).ToArray();
        byte[] body = whole.AsSpan(bodyStart).ToArray();

        int count = Math.Max(1, (body.Length + chunkSize - 1) / chunkSize);

        int flagsAt = PackageWriterInternals.CompressionFlagsOffset(header);
        int tableAt = flagsAt + (sizeof(int) * 2);
        int tableSize = count * sizeof(int) * 4;

        if (tableAt > headerBytes.Length)
        {
            throw new PackageRebuildException(
                $"The chunk table would go at {tableAt}, past the {headerBytes.Length}-byte header.");
        }

        // The table lives inside the header, right after the two fields saying
        // how the package is packed and how many chunks it has. A package
        // written plainly has no table, so room is made for one - which is why
        // a packed file's header is longer than the same package's plain one by
        // exactly sixteen bytes per chunk. Nothing recorded in the file moves,
        // because every offset in a package is measured against the plain
        // arrangement.
        var file = new MemoryStream(body.Length);

        file.Write(headerBytes.AsSpan(0, tableAt));
        file.Write(new byte[tableSize]);
        file.Write(headerBytes.AsSpan(tableAt));

        var written = new PackageChunk[count];

        for (int i = 0; i < count; i++)
        {
            int at = i * chunkSize;
            int size = Math.Min(chunkSize, body.Length - at);

            int compressedOffset = (int)file.Position;
            int compressedSize = WriteChunk(file, body.AsSpan(at, size), DefaultBlockSize);

            written[i] = new PackageChunk(
                UncompressedOffset: bodyStart + at,
                UncompressedSize: size,
                CompressedOffset: compressedOffset,
                CompressedSize: compressedSize);
        }

        byte[] output = file.ToArray();

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(flagsAt), (int)PackageCompression.Lzo);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(flagsAt + sizeof(int)), count);

        // The package also says of itself, among its own flags, that it is
        // stored packed. Every packed package in this game has that bit.
        // Its position is found the same way the reader finds it: past the
        // mark, the two format numbers, the total size, and the folder name.
        int packageFlagsAt = sizeof(uint) + sizeof(short) + sizeof(short) + sizeof(int);
        int folder = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(packageFlagsAt));

        packageFlagsAt += sizeof(int) + (folder >= 0 ? folder : -folder * 2);

        if (packageFlagsAt + sizeof(uint) <= output.Length)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(packageFlagsAt));

            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(packageFlagsAt), flags | 0x02000000u);
        }

        for (int i = 0; i < count; i++)
        {
            int at = tableAt + (i * sizeof(int) * 4);

            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), written[i].UncompressedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + 4), written[i].UncompressedSize);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + 8), written[i].CompressedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + 12), written[i].CompressedSize);
        }

        return output;
    }

    private static int WriteChunk(MemoryStream file, ReadOnlySpan<byte> data, int blockSize)
    {
        int blockCount = data.Length == 0 ? 0 : (data.Length + blockSize - 1) / blockSize;

        var blocks = new List<byte[]>(blockCount);
        var sizes = new List<(int Compressed, int Uncompressed)>(blockCount);

        for (int at = 0; at < data.Length; at += blockSize)
        {
            int length = Math.Min(blockSize, data.Length - at);
            ReadOnlySpan<byte> plain = data.Slice(at, length);

            // Always compressed, even when that makes the block bigger. The
            // game decompresses every block unconditionally — version 1's
            // reader, which has been run over the whole install, has no path
            // for a block stored as it is — so a block left plain is one the
            // game reads as an LZO stream and turns into rubbish. That is what
            // it does: the package loads, and the model comes apart into
            // splinters on the floor.
            byte[] packed = Lzo1xCompressor.Compress(plain);

            blocks.Add(packed);
            sizes.Add((packed.Length, length));
        }

        int totalCompressed = sizes.Sum(s => s.Compressed);
        int totalUncompressed = sizes.Sum(s => s.Uncompressed);

        int headerSize = (sizeof(int) * 4) + (blocks.Count * sizeof(int) * 2);

        Span<byte> chunkHeader = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader, PackageHeader.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(chunkHeader[4..], blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(chunkHeader[8..], totalCompressed);
        BinaryPrimitives.WriteInt32LittleEndian(chunkHeader[12..], totalUncompressed);

        file.Write(chunkHeader);

        Span<byte> pair = stackalloc byte[sizeof(int) * 2];

        foreach ((int compressed, int uncompressed) in sizes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pair, compressed);
            BinaryPrimitives.WriteInt32LittleEndian(pair[4..], uncompressed);
            file.Write(pair);
        }

        foreach (byte[] block in blocks) file.Write(block);

        return headerSize + totalCompressed;
    }

    /// <summary>
    /// Corrects the chunk table in place, where the header already holds one.
    /// </summary>
    /// <remarks>
    /// The table keeps the same number of entries, so the header stays exactly
    /// as long as it was. That length is what every offset in the rest of the
    /// file is measured against, and changing it would move all of them.
    /// </remarks>
    private static void WriteChunkTable(
        byte[] file, PackageHeader header, PackageChunk[] chunks, int at)
    {
        // The same check the plain writer makes, for the same reason: this
        // position is a sum of lengths, and being wrong about it would rewrite
        // some other part of the header instead.
        PackageWriterInternals.VerifyCompressionFields(
            file, header, at - (sizeof(int) * 2));

        foreach (PackageChunk chunk in chunks)
        {
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at, sizeof(int)), chunk.UncompressedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 4, sizeof(int)), chunk.UncompressedSize);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 8, sizeof(int)), chunk.CompressedOffset);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at + 12, sizeof(int)), chunk.CompressedSize);

            at += sizeof(int) * 4;
        }
    }

    /// <summary>
    /// The block size the package was originally packed with.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than assumed, so a package packed differently
    /// is repacked the same way it arrived.
    /// </remarks>
    private static int? BlockSizeOf(Package package, PackageHeader header)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(package.Path);
            return ChunkHeader.Read(raw, header.Chunks[0].CompressedOffset).BlockSize;
        }
        catch (Exception ex) when (ex is IOException or InvalidPackageException or ArgumentException)
        {
            return null;
        }
    }
}
