using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Compression;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>The outcome of writing into a texture cache.</summary>
public sealed record CacheWriteResult(bool Succeeded, string Message, int BytesWritten = 0, int SlotSize = 0)
{
    public static CacheWriteResult Fail(string message) => new(false, message);
}

/// <summary>
/// Writes pixel data back into a shared texture cache.
/// </summary>
/// <remarks>
/// A cache file holds the pixels of many textures back to back, each at a fixed
/// offset recorded in the manifest. There is nowhere to put extra bytes, so a
/// replacement must compress to no more than the slot it is replacing. Every
/// slot sampled in the real caches holds compressed data, so this always
/// compresses rather than looking for a stored-uncompressed path.
/// <para>
/// Writing here is more consequential than editing a package: one cache file
/// backs thousands of textures. The whole file is backed up before the first
/// change and swapped in atomically, and a replacement that does not fit is
/// refused outright rather than truncated.
/// </para>
/// </remarks>
public sealed class TextureCacheWriter
{
    /// <summary>Uncompressed bytes per block, matching what the caches use.</summary>
    private const int BlockSize = 128 * 1024;

    private readonly string _cookedPath;

    public TextureCacheWriter(string cookedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookedPath);
        _cookedPath = cookedPath;
    }

    /// <summary>
    /// Builds the block-structured payload a cache slot expects.
    /// </summary>
    /// <remarks>
    /// Layout mirrors what the reader consumes: a header naming the block size
    /// and the two totals, then one size pair per block, then the blocks. A block
    /// that fails to get smaller is stored as-is, which the reader recognises by
    /// its compressed and uncompressed sizes being equal.
    /// </remarks>
    public static byte[] BuildPayload(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) throw new ArgumentException("Nothing to write.", nameof(raw));

        int blockCount = (raw.Length + BlockSize - 1) / BlockSize;

        var blocks = new List<byte[]>(blockCount);
        var uncompressedSizes = new List<int>(blockCount);

        for (int offset = 0; offset < raw.Length; offset += BlockSize)
        {
            int length = Math.Min(BlockSize, raw.Length - offset);
            ReadOnlySpan<byte> slice = raw.Slice(offset, length);

            byte[] compressed = Lzo1xCompressor.Compress(slice);

            // Storing is better than growing. The reader treats equal sizes as
            // "not compressed" and copies the bytes straight through.
            blocks.Add(compressed.Length >= length ? slice.ToArray() : compressed);
            uncompressedSizes.Add(length);
        }

        int headerSize = (sizeof(int) * 4) + (blockCount * sizeof(int) * 2);
        int totalCompressed = blocks.Sum(b => b.Length);

        byte[] payload = new byte[headerSize + totalCompressed];
        int at = 0;

        BitConverter.GetBytes(PackageHeader.Magic).CopyTo(payload, at); at += 4;
        BitConverter.GetBytes(BlockSize).CopyTo(payload, at); at += 4;
        BitConverter.GetBytes(totalCompressed).CopyTo(payload, at); at += 4;
        BitConverter.GetBytes(raw.Length).CopyTo(payload, at); at += 4;

        for (int i = 0; i < blockCount; i++)
        {
            BitConverter.GetBytes(blocks[i].Length).CopyTo(payload, at); at += 4;
            BitConverter.GetBytes(uncompressedSizes[i]).CopyTo(payload, at); at += 4;
        }

        foreach (byte[] block in blocks)
        {
            block.CopyTo(payload, at);
            at += block.Length;
        }

        return payload;
    }

    /// <summary>
    /// Checks whether a replacement would fit its slot, without writing anything.
    /// </summary>
    public CacheWriteResult CanWrite(string cacheName, int slotSize, ReadOnlySpan<byte> raw)
    {
        string? cacheFile = ResolveCacheFile(cacheName);
        if (cacheFile is null)
            return CacheWriteResult.Fail($"The texture cache '{cacheName}.tfc' was not found.");

        if (slotSize <= 0)
            return CacheWriteResult.Fail("The manifest does not record a size for this texture.");

        byte[] payload = BuildPayload(raw);

        if (payload.Length > slotSize)
        {
            return new CacheWriteResult(
                false,
                $"The replacement compresses to {payload.Length:N0} bytes but the slot holds " +
                $"{slotSize:N0}. Try an image with flatter colour or less detail — it has to " +
                "compress at least as well as the original.",
                payload.Length,
                slotSize);
        }

        return new CacheWriteResult(
            true,
            $"Fits: {payload.Length:N0} of {slotSize:N0} bytes.",
            payload.Length,
            slotSize);
    }

    /// <summary>
    /// Writes a mip's pixels into a cache slot.
    /// </summary>
    /// <param name="cacheName">Cache to write into, without its extension.</param>
    /// <param name="offset">Where the slot begins.</param>
    /// <param name="slotSize">How many bytes the slot holds.</param>
    /// <param name="raw">The uncompressed pixel data.</param>
    public async Task<CacheWriteResult> WriteAsync(
        string cacheName,
        int offset,
        int slotSize,
        ReadOnlyMemory<byte> raw,
        CancellationToken cancellationToken = default)
    {
        CacheWriteResult check = CanWrite(cacheName, slotSize, raw.Span);
        if (!check.Succeeded) return check;

        string cacheFile = ResolveCacheFile(cacheName)!;

        try
        {
            byte[] payload = BuildPayload(raw.Span);
            byte[] cache = await File.ReadAllBytesAsync(cacheFile, cancellationToken).ConfigureAwait(false);

            if (offset < 0 || offset + slotSize > cache.Length)
            {
                return CacheWriteResult.Fail(
                    $"The slot at {offset:N0} runs past the end of '{Path.GetFileName(cacheFile)}'.");
            }

            payload.CopyTo(cache.AsSpan(offset, payload.Length));

            // Clear the tail of the slot so no fragment of the previous texture
            // can be read as part of the new one.
            cache.AsSpan(offset + payload.Length, slotSize - payload.Length).Clear();

            string backup = await SafeFileWriter.WriteAsync(cacheFile, cache, cancellationToken)
                                                .ConfigureAwait(false);

            return new CacheWriteResult(
                true,
                $"Wrote {payload.Length:N0} bytes into '{Path.GetFileName(cacheFile)}'. " +
                $"The cache was backed up to {Path.GetFileName(backup)}.",
                payload.Length,
                slotSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidPackageException)
        {
            return CacheWriteResult.Fail($"Could not write to the texture cache: {ex.Message}");
        }
    }

    private string? ResolveCacheFile(string cacheName)
    {
        if (string.IsNullOrWhiteSpace(cacheName)) return null;

        string path = Path.Combine(_cookedPath, cacheName + ".tfc");
        return File.Exists(path) ? path : null;
    }
}
