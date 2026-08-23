namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Reads pixel data out of the shared texture cache files that sit beside the
/// cooked packages.
/// </summary>
/// <remarks>
/// Large textures do not keep their pixels in their own package. The package
/// records which cache file holds them and at what offset, and the bytes live in
/// a <c>.tfc</c> file next to the content.
/// <para>
/// Cache files are opened read-only and shared, so a running game does not block
/// reading and nothing here can modify one by accident.
/// </para>
/// </remarks>
public sealed class TextureCacheReader
{
    private readonly string _cookedPath;

    public TextureCacheReader(string cookedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookedPath);
        _cookedPath = cookedPath;
    }

    /// <summary>Resolves a cache name to a file path, or null when absent.</summary>
    public string? ResolveCacheFile(string cacheName)
    {
        if (string.IsNullOrWhiteSpace(cacheName)) return null;

        string path = Path.Combine(_cookedPath, cacheName + ".tfc");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Reads a mip's bytes from a cache file.
    /// </summary>
    /// <remarks>
    /// Cached mips carry their own small header describing how the block is
    /// stored, mirroring the way compressed package chunks work. An uncompressed
    /// block is copied straight out; a compressed one is expanded.
    /// </remarks>
    public byte[]? TryReadMip(string cacheName, int offset, int byteCount)
    {
        if (byteCount <= 0 || offset < 0) return null;

        string? cacheFile = ResolveCacheFile(cacheName);
        if (cacheFile is null) return null;

        try
        {
            using var stream = new FileStream(
                cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (offset >= stream.Length) return null;

            stream.Seek(offset, SeekOrigin.Begin);

            // Read the block header first; it says whether the payload that
            // follows is compressed and how large it is.
            byte[] header = new byte[16];
            if (stream.Read(header, 0, header.Length) < header.Length) return null;

            uint magic = BitConverter.ToUInt32(header, 0);
            if (magic != Packages.PackageHeader.Magic)
            {
                // No header here: the bytes are stored plainly at this offset.
                stream.Seek(offset, SeekOrigin.Begin);
                byte[] plain = new byte[byteCount];
                return stream.ReadAtLeast(plain, byteCount, throwOnEndOfStream: false) == byteCount
                    ? plain
                    : null;
            }

            int blockSize = BitConverter.ToInt32(header, 4);
            int totalCompressed = BitConverter.ToInt32(header, 8);
            int totalUncompressed = BitConverter.ToInt32(header, 12);

            if (blockSize <= 0 || totalUncompressed <= 0 || totalCompressed <= 0) return null;

            // Bound the expansion absolutely, not as a multiple of the compressed
            // size. An earlier version capped it at four times the slot, which
            // silently rejected real textures: compression ratios in the caches
            // were measured up to 6.83x, so a 641,200-byte mip legitimately sits
            // in a 130,164-byte slot.
            const int MaxMipBytes = 64 * 1024 * 1024;
            if (totalUncompressed > MaxMipBytes) return null;

            int blockCount = (totalUncompressed + blockSize - 1) / blockSize;
            if (blockCount is <= 0 or > 4096) return null;

            byte[] table = new byte[blockCount * 8];
            if (stream.Read(table, 0, table.Length) < table.Length) return null;

            byte[] output = new byte[totalUncompressed];
            int written = 0;

            for (int i = 0; i < blockCount; i++)
            {
                int compressedSize = BitConverter.ToInt32(table, i * 8);
                int uncompressedSize = BitConverter.ToInt32(table, (i * 8) + 4);

                if (compressedSize <= 0 || uncompressedSize <= 0) return null;
                if (written + uncompressedSize > output.Length) return null;

                byte[] compressed = new byte[compressedSize];
                if (stream.ReadAtLeast(compressed, compressedSize, throwOnEndOfStream: false) < compressedSize)
                    return null;

                if (compressedSize == uncompressedSize)
                    compressed.CopyTo(output, written);
                else
                    Packages.Compression.Lzo1x.Decompress(compressed, output.AsSpan(written, uncompressedSize));

                written += uncompressedSize;
            }

            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Packages.InvalidPackageException)
        {
            // A cache that cannot be read is a missing preview, not a crash.
            return null;
        }
    }
}
