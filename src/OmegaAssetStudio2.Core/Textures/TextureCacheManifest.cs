using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>Where one mip of a cached texture lives inside its cache file.</summary>
public readonly record struct CachedMipLocation(int MipIndex, int Offset, int Size);

/// <summary>One texture's entry in the cache manifest.</summary>
public sealed record CachedTextureEntry
{
    /// <summary>Cache file holding the pixels, without its extension.</summary>
    public required string CacheName { get; init; }

    /// <summary>Object path of the texture this describes.</summary>
    public required string ObjectPath { get; init; }

    public required Guid TextureGuid { get; init; }

    /// <summary>Mip locations, as recorded. Not necessarily in order.</summary>
    public required IReadOnlyList<CachedMipLocation> Mips { get; init; }

    /// <summary>The largest recorded mip, which is the lowest index.</summary>
    public CachedMipLocation? LargestMip => Mips.Count == 0
        ? null
        : Mips.OrderBy(m => m.MipIndex).First();
}

/// <summary>
/// The manifest that says where each cached texture's pixels live.
/// </summary>
/// <remarks>
/// Textures whose pixels are in a shared cache record <c>-1</c> for both offset
/// and size in their own package, so the package alone cannot locate them. This
/// file supplies the missing offsets.
/// <para>
/// Layout derived by hand from the real file: a count, then for each entry a
/// length-prefixed cache name, a length-prefixed object path, a 16-byte
/// identifier, a mip count, and that many <c>(index, offset, size)</c> triples.
/// Two independent checks confirmed it — consecutive mip offsets chain exactly
/// (each mip begins where the previous one ends), and the second entry starts at
/// precisely the byte where the first entry's last triple ends.
/// </para>
/// </remarks>
public sealed class TextureCacheManifest
{
    /// <summary>Filename beside the cooked packages.</summary>
    public const string FileName = "TextureFileCacheManifest.bin";

    private readonly Dictionary<string, CachedTextureEntry> _byObjectPath;

    private TextureCacheManifest(Dictionary<string, CachedTextureEntry> byObjectPath)
        => _byObjectPath = byObjectPath;

    public int Count => _byObjectPath.Count;

    /// <summary>Finds a texture's cache entry. Case-insensitive.</summary>
    public CachedTextureEntry? Find(string objectPath) =>
        string.IsNullOrWhiteSpace(objectPath) ? null : _byObjectPath.GetValueOrDefault(objectPath);

    /// <summary>
    /// Loads the manifest sitting beside the cooked packages, or null when it is
    /// absent or unreadable.
    /// </summary>
    public static TextureCacheManifest? TryLoad(string cookedPath)
    {
        string path = Path.Combine(cookedPath, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Field order within an entry, which differs between client versions.
    /// </summary>
    /// <remarks>
    /// Confirmed by decoding real manifests from more than one install. Neither
    /// order can be assumed, and there is no version marker in the file, so the
    /// reader tries both and keeps whichever parses cleanly to the end.
    /// </remarks>
    public enum EntryLayout
    {
        /// <summary>Cache name, then object path, then identifier.</summary>
        CacheNameFirst,

        /// <summary>Object path, then identifier, then cache name.</summary>
        ObjectPathFirst,
    }

    /// <summary>Which field order this manifest turned out to use.</summary>
    public EntryLayout Layout { get; private init; }

    /// <summary>
    /// Parses manifest bytes, determining the field order automatically.
    /// </summary>
    public static TextureCacheManifest Read(ReadOnlySpan<byte> data)
    {
        // Every entry is variable length, so a wrong field order desynchronises
        // almost immediately and cannot reach the declared entry count. That
        // makes "it parsed" a reliable way to identify the layout.
        InvalidPackageException? firstFailure = null;

        foreach (EntryLayout layout in new[] { EntryLayout.CacheNameFirst, EntryLayout.ObjectPathFirst })
        {
            try
            {
                return Read(data, layout);
            }
            catch (InvalidPackageException ex)
            {
                firstFailure ??= ex;
            }
        }

        throw new InvalidPackageException(
            "The texture cache manifest did not parse in any known field order. " +
            $"First attempt failed with: {firstFailure?.Message}");
    }

    /// <summary>Parses manifest bytes using a known field order.</summary>
    public static TextureCacheManifest Read(ReadOnlySpan<byte> data, EntryLayout layout)
    {
        var cursor = new PackageCursor(data);

        int entryCount = cursor.ReadInt32("manifest entry count");
        if (entryCount < 0)
            throw new InvalidPackageException($"Manifest declares {entryCount} entries.");

        // Smallest conceivable entry, used only to reject an impossible count
        // before allocating for it.
        const int minimumEntrySize = 4 + 1 + 4 + 1 + 16 + 4;
        if ((long)entryCount * minimumEntrySize > data.Length)
            throw new InvalidPackageException(
                $"Manifest declares {entryCount} entries, too many for a {data.Length}-byte file.");

        var byObjectPath = new Dictionary<string, CachedTextureEntry>(
            entryCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entryCount; i++)
        {
            string cacheName, objectPath;
            Guid guid;

            if (layout == EntryLayout.CacheNameFirst)
            {
                cacheName = cursor.ReadString($"entry {i} cache name");
                objectPath = cursor.ReadString($"entry {i} object path");
                guid = cursor.ReadGuid($"entry {i} guid");
            }
            else
            {
                objectPath = cursor.ReadString($"entry {i} object path");
                guid = cursor.ReadGuid($"entry {i} guid");
                cacheName = cursor.ReadString($"entry {i} cache name");
            }

            // A path with no separator, or an empty one, means the field order is
            // wrong — fail fast so the other layout gets tried.
            if (objectPath.Length == 0 || cacheName.Length == 0)
                throw new InvalidPackageException($"Entry {i} decoded an empty name; wrong field order.");

            int mipCount = cursor.ReadInt32($"entry {i} mip count");
            if (mipCount < 0 || (long)mipCount * 12 > cursor.Remaining)
                throw new InvalidPackageException($"Entry {i} declares {mipCount} mips.");

            var mips = new CachedMipLocation[mipCount];
            for (int m = 0; m < mipCount; m++)
            {
                mips[m] = new CachedMipLocation(
                    MipIndex: cursor.ReadInt32($"entry {i} mip {m} index"),
                    Offset: cursor.ReadInt32($"entry {i} mip {m} offset"),
                    Size: cursor.ReadInt32($"entry {i} mip {m} size"));
            }

            var entry = new CachedTextureEntry
            {
                CacheName = cacheName,
                ObjectPath = objectPath,
                TextureGuid = guid,
                Mips = mips,
            };

            // Duplicates are not expected; keep the first rather than throwing on
            // a file the application does not control.
            byObjectPath.TryAdd(objectPath, entry);
        }

        return new TextureCacheManifest(byObjectPath) { Layout = layout };
    }
}
