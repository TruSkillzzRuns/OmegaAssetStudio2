using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OmegaAssetStudio.TexturePreview.Cache;

/// <summary>
/// Process-wide LRU cache for decoded UPK textures. Keyed by
/// (upkPath, exportPath, mipIndex). Stores ONLY the immutable byte buffer +
/// dimensions/format so we never share a <see cref="System.Drawing.Bitmap"/>
/// across threads — a fresh wrapper is materialised on every retrieval.
/// </summary>
public sealed class DecodedTextureCache
{
    // Budget-bounded LRU. The previous 256-ENTRY cap mixed tiny 128px icons with
    // full 2048² character textures (~16MB decoded + container each), so 256 large
    // entries alone could pin ~7GB. Bounding by BYTES instead lets hundreds of
    // small icons stay resident while only a handful of big textures are kept,
    // which is what actually drives RAM. The count cap is kept generous so the
    // full hero roster (~560 icons, ~100MB total) never evicts on count.
    public static DecodedTextureCache Shared { get; } = new(capacity: 4096, maxBytes: 768L * 1024 * 1024);

    private readonly int _capacity;
    private readonly long _maxBytes;
    private long _currentBytes;
    private readonly object _sync = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.OrdinalIgnoreCase);

    public DecodedTextureCache(int capacity, long maxBytes = long.MaxValue)
    {
        if (capacity < 1) capacity = 1;
        _capacity = capacity;
        _maxBytes = maxBytes < 1 ? 1 : maxBytes;
    }

    private static string BuildKey(string upkPath, string exportPath, int? mipIndex)
    {
        return $"{upkPath}{exportPath}{mipIndex?.ToString() ?? "*"}";
    }

    public TexturePreviewTexture? TryGet(string upkPath, string exportPath, int? mipIndex, TexturePreviewMaterialSlot fallbackSlot)
    {
        if (string.IsNullOrWhiteSpace(upkPath) || string.IsNullOrWhiteSpace(exportPath))
            return null;

        string key = BuildKey(upkPath, exportPath, mipIndex);
        CacheEntry entry;
        lock (_sync)
        {
            if (!_index.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
                return null;

            // Move to MRU.
            _lru.Remove(node);
            _lru.AddFirst(node);
            entry = node.Value;
        }

        try
        {
            Bitmap bitmap = RebuildBitmap(entry);
            return new TexturePreviewTexture
            {
                Name = entry.Name,
                SourcePath = entry.SourcePath,
                SourceDescription = entry.SourceDescription,
                ExportPath = entry.ExportPath,
                Bitmap = bitmap,
                RgbaPixels = entry.RgbaPixels,
                Width = entry.Width,
                Height = entry.Height,
                MipCount = entry.MipCount,
                SelectedMipIndex = entry.SelectedMipIndex,
                Format = entry.Format,
                Compression = entry.Compression,
                ContainerType = entry.ContainerType,
                MipSource = entry.MipSource,
                Slot = fallbackSlot,
                ContainerBytes = entry.ContainerBytes,
                AvailableMipLevels = entry.AvailableMipLevels
            };
        }
        catch
        {
            // Defensive — if rebuild fails for any reason, just bypass the cache.
            return null;
        }
    }

    // Drops every cached mip variant for this (upkPath, exportPath). Used after
    // the IconReplaceService writes new bytes into a Texture2D so the next
    // load decodes from disk instead of replaying the old decoded buffer.
    // Without this, the grid card / preview pane shows the pre-replace bitmap
    // because TryGet returns the cached entry before the loader even touches
    // the file.
    public void Invalidate(string upkPath, string exportPath)
    {
        if (string.IsNullOrWhiteSpace(upkPath) || string.IsNullOrWhiteSpace(exportPath))
            return;
        string prefix = upkPath + exportPath;
        lock (_sync)
        {
            // The mip-index suffix can be "*" or a number, so we prefix-match.
            // Build a kill list first (can't mutate _index while enumerating).
            List<string>? kill = null;
            foreach (string key in _index.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    (kill ??= new()).Add(key);
            }
            if (kill is null) return;
            foreach (string key in kill)
            {
                if (_index.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
                {
                    _currentBytes -= node.Value.ByteCost;
                    _lru.Remove(node);
                    _index.Remove(key);
                }
            }
        }
    }

    public void Put(string upkPath, string exportPath, int? mipIndex, TexturePreviewTexture texture)
    {
        if (texture is null || texture.RgbaPixels is null || texture.RgbaPixels.Length == 0)
            return;
        if (texture.Width <= 0 || texture.Height <= 0)
            return;
        if (texture.ContainerBytes is null)
            return; // skip defensive — caller relies on this being present

        string key = BuildKey(upkPath, exportPath, mipIndex);
        CacheEntry entry = new()
        {
            Name = texture.Name,
            SourcePath = texture.SourcePath,
            SourceDescription = texture.SourceDescription,
            ExportPath = texture.ExportPath,
            RgbaPixels = texture.RgbaPixels,
            Width = texture.Width,
            Height = texture.Height,
            MipCount = texture.MipCount,
            SelectedMipIndex = texture.SelectedMipIndex,
            Format = texture.Format,
            Compression = texture.Compression,
            ContainerType = texture.ContainerType,
            MipSource = texture.MipSource,
            ContainerBytes = texture.ContainerBytes,
            AvailableMipLevels = texture.AvailableMipLevels
        };
        entry.ByteCost = (long)(entry.RgbaPixels?.Length ?? 0) + (entry.ContainerBytes?.Length ?? 0);

        lock (_sync)
        {
            if (_index.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                _currentBytes -= existing.Value.ByteCost;
                _lru.Remove(existing);
                _index.Remove(key);
            }

            LinkedListNode<CacheEntry> node = _lru.AddFirst(entry);
            _index[key] = node;
            _currentBytes += entry.ByteCost;

            // Evict oldest until BOTH the entry-count and byte budgets are satisfied.
            // Never evict the entry we just added (keep at least one), so a single
            // oversized texture is still cacheable.
            while ((_lru.Count > _capacity || _currentBytes > _maxBytes) && _lru.Count > 1)
            {
                LinkedListNode<CacheEntry>? oldest = _lru.Last;
                if (oldest is null) break;
                _lru.RemoveLast();
                _currentBytes -= oldest.Value.ByteCost;
                _index.Remove(BuildKey(oldest.Value.SourcePath, oldest.Value.ExportPath, oldest.Value.SelectedMipIndex));
            }
        }
    }

    private static Bitmap RebuildBitmap(CacheEntry entry)
    {
        Bitmap bitmap = new(entry.Width, entry.Height, PixelFormat.Format32bppArgb);
        Rectangle rect = new(0, 0, entry.Width, entry.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Cached buffer is RGBA; LockBits/Format32bppArgb wants BGRA.
            byte[] rgba = entry.RgbaPixels;
            byte[] bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i + 0] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i + 0];
                bgra[i + 3] = rgba[i + 3];
            }
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private sealed class CacheEntry
    {
        public string Name = string.Empty;
        public string SourcePath = string.Empty;
        public string SourceDescription = string.Empty;
        public string ExportPath = string.Empty;
        public byte[] RgbaPixels = Array.Empty<byte>();
        public int Width;
        public int Height;
        public int MipCount;
        public int SelectedMipIndex;
        public string Format = string.Empty;
        public string Compression = string.Empty;
        public string ContainerType = string.Empty;
        public string MipSource = string.Empty;
        public byte[] ContainerBytes = Array.Empty<byte>();
        public long ByteCost;
        public IReadOnlyList<TexturePreviewMipLevel> AvailableMipLevels = Array.Empty<TexturePreviewMipLevel>();
    }
}
