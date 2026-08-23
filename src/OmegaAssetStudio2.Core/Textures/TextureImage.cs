using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>Decoded pixels, ready to display.</summary>
public sealed record TextureImage(int Width, int Height, byte[] Rgba)
{
    public int ByteLength => Rgba.Length;
}

/// <summary>
/// Turns a texture export into displayable pixels, from wherever they are stored.
/// </summary>
public sealed class TextureReader
{
    private readonly TextureCacheReader _cache;
    private readonly Lazy<TextureCacheManifest?> _manifest;

    public TextureReader(string cookedPath)
    {
        _cache = new TextureCacheReader(cookedPath);

        // Several megabytes, and only needed once a cache-backed texture is
        // actually requested, so it loads on first use.
        _manifest = new Lazy<TextureCacheManifest?>(() => TextureCacheManifest.TryLoad(cookedPath));
    }

    /// <summary>The cache manifest, if one is present beside the packages.</summary>
    public TextureCacheManifest? Manifest => _manifest.Value;

    /// <summary>
    /// Decodes a texture's largest available mip.
    /// </summary>
    /// <returns>
    /// The decoded image, or null when the pixels cannot be reached — an
    /// unsupported format, or a cache file that is not present.
    /// </returns>
    /// <remarks>
    /// Prefers whichever mip is actually readable rather than insisting on the
    /// top one. A texture whose full-size mip lives in a missing cache file can
    /// still preview from a smaller inline mip, which is far better than showing
    /// the user nothing.
    /// </remarks>
    public TextureImage? TryDecode(Package package, TextureInfo info)
    {
        PropertyBag? properties = package.TryReadProperties(info.ExportIndex);
        if (properties is null) return null;

        TextureMipChain? chain = TextureMipChain.TryRead(package, info.ExportIndex, properties);
        if (chain is null || chain.Mips.Count == 0) return null;

        if (!BlockDecoder.CanDecode(info.Format)) return null;

        // Largest first, so the best available quality wins.
        foreach (TextureMipMap mip in chain.Mips.OrderByDescending(m => (long)m.Width * m.Height))
        {
            byte[]? pixels = TryGetMipBytes(package, info, mip);
            if (pixels is null) continue;

            int required = info.Format.MipByteSize(mip.Width, mip.Height);
            if (required > 0 && pixels.Length < required) continue;

            try
            {
                byte[] rgba = BlockDecoder.Decode(pixels, info.Format, mip.Width, mip.Height);
                return new TextureImage(mip.Width, mip.Height, rgba);
            }
            catch (ArgumentException)
            {
                // Try the next smaller mip rather than giving up entirely.
            }
        }

        return null;
    }

    private byte[]? TryGetMipBytes(Package package, TextureInfo info, TextureMipMap mip)
    {
        if (mip.IsInline)
        {
            ReadOnlySpan<byte> data = package.GetExportData(info.ExportIndex);
            int start = mip.Data.InlineDataOffset;
            int length = mip.Data.SizeOnDisk;

            if (start < 0 || length <= 0 || start + length > data.Length) return null;
            return data.Slice(start, length).ToArray();
        }

        if (!info.IsCacheBacked) return null;

        // The package records -1 for a cached mip's offset, so the location has
        // to come from the manifest that sits beside the packages.
        CachedTextureEntry? entry = Manifest?.Find(info.ObjectPath);
        if (entry is null) return null;

        // Match on dimensions rather than on index: the package's mip list and
        // the manifest's are not guaranteed to be numbered the same way, but the
        // byte count for a given mip is unambiguous.
        CachedMipLocation? location = entry.Mips
            .Where(m => m.Size > 0 && m.Offset >= 0)
            .OrderBy(m => m.MipIndex)
            .Cast<CachedMipLocation?>()
            .FirstOrDefault(m => MatchesMip(m!.Value, entry, mip));

        location ??= entry.LargestMip;
        if (location is null || location.Value.Size <= 0) return null;

        return _cache.TryReadMip(info.TextureCacheName, location.Value.Offset, location.Value.Size);
    }

    /// <summary>
    /// Whether a manifest entry describes the given mip. The manifest lists mips
    /// largest-first, so position within that ordering identifies which one.
    /// </summary>
    private static bool MatchesMip(CachedMipLocation location, CachedTextureEntry entry, TextureMipMap mip)
    {
        List<CachedMipLocation> ordered = entry.Mips.OrderBy(m => m.MipIndex).ToList();
        int position = ordered.IndexOf(location);
        return position == 0 && mip.IsExternal;
    }
}
