using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;

namespace OmegaAssetStudio2.App.Icons;

/// <summary>
/// Turns a texture into something the UI can show.
/// </summary>
/// <remarks>
/// Opening a package is the expensive part, so the most recently used ones are
/// kept. A scan can turn up thousands of icons spread across a few dozen
/// packages, and re-reading and re-decompressing a package for every thumbnail
/// would make the grid unusable.
/// </remarks>
public sealed class IconImageService
{
    private const int MaxCachedPackages = 8;

    private readonly Dictionary<string, Package> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, TextureReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>
    /// Decodes a texture to pixels. Returns null when the pixels are not
    /// reachable — for example when they live only in a texture cache.
    /// </summary>
    public TextureImage? TryGetPixels(TextureInfo info, string cookedPath)
    {
        lock (_gate)
        {
            try
            {
                Package package = GetPackage(info.PackagePath);
                TextureReader reader = GetReader(cookedPath);
                return reader.TryDecode(package, info);
            }
            catch (Exception ex) when (ex is InvalidPackageException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Decodes a texture into a bitmap ready for an Image control. Must be called
    /// on the UI thread, because the bitmap is a UI object.
    /// </summary>
    public async Task<WriteableBitmap?> TryGetBitmapAsync(TextureInfo info, string cookedPath)
    {
        TextureImage? image = await Task.Run(() => TryGetPixels(info, cookedPath)).ConfigureAwait(true);
        if (image is null) return null;

        var bitmap = new WriteableBitmap(image.Width, image.Height);

        // The bitmap buffer is blue-first; the decoder produced red-first.
        byte[] bgra = new byte[image.Rgba.Length];
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            bgra[i] = image.Rgba[i + 2];
            bgra[i + 1] = image.Rgba[i + 1];
            bgra[i + 2] = image.Rgba[i];
            bgra[i + 3] = image.Rgba[i + 3];
        }

        using (Stream buffer = bitmap.PixelBuffer.AsStream())
        {
            await buffer.WriteAsync(bgra).ConfigureAwait(true);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Drops every cached package, releasing their expanded bodies.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _packages.Clear();
            _recent.Clear();
            _readers.Clear();
        }
    }

    private Package GetPackage(string path)
    {
        if (_packages.TryGetValue(path, out Package? cached))
        {
            _recent.Remove(path);
            _recent.AddFirst(path);
            return cached;
        }

        Package package = Package.Open(path);
        _packages[path] = package;
        _recent.AddFirst(path);

        // An expanded package body can be tens of megabytes, so the cache is
        // deliberately small.
        while (_recent.Count > MaxCachedPackages)
        {
            string oldest = _recent.Last!.Value;
            _recent.RemoveLast();
            _packages.Remove(oldest);
        }

        return package;
    }

    private TextureReader GetReader(string cookedPath)
    {
        if (!_readers.TryGetValue(cookedPath, out TextureReader? reader))
        {
            reader = new TextureReader(cookedPath);
            _readers[cookedPath] = reader;
        }
        return reader;
    }
}
