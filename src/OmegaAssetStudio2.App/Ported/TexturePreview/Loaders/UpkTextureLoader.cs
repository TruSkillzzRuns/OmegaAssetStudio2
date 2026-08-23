using DDSLib;
using OmegaAssetStudio.TextureManager;
using OmegaAssetStudio.TexturePreview.Cache;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.TexturePreview;

public sealed class UpkTextureLoader
{
    // Process-wide semaphore that serializes ALL UPK texture loads. The loader
    // touches singletons (TextureManifest.Instance, TextureFileCache.Instance)
    // and mutates UPK header export-tables via ParseUnrealObject; concurrent
    // callers (material resolver + skill cross-UPK anim resolver + particle
    // binding texture lookup) raced and produced "Collection was modified
    // after the enumerator was instantiated" — every texture would fail to
    // decode and the character rendered as pure white untextured geometry.
    // Cache hits skip the semaphore so the hot reload path stays free.
    private static readonly SemaphoreSlim s_loadLock = new(1, 1);

    // Reused across LoadFromUpkAsyncCore calls so a burst of loads from the same
    // few packages (e.g. a whole hero roster's icons living in 2-3 shared ICO
    // UPKs) reads each file ONCE instead of File.ReadAllBytes-ing a 100-300MB UPK
    // hundreds of times — the latter was spiking the Large Object Heap to ~16GB.
    // Only LoadFromUpkAsyncCore touches it, and that path is always serialized by
    // s_loadLock, so the repository stays single-threaded. Call
    // ReleaseCachedPackages() after a batch to drop the retained file bytes.
    private readonly UpkFileRepository _repository = new();

    // Releases the cached UPK headers (and their whole-file byte buffers) this
    // loader has accumulated. Safe to call between bursts; the next load simply
    // re-reads on demand.
    public void ReleaseCachedPackages() => _repository.ClearHeaderCache();

    public async Task<TexturePreviewTexture> LoadFromUpkAsync(string upkPath, string exportPath, TexturePreviewMaterialSlot fallbackSlot, Action<string> log = null, int? requestedMipIndex = null)
    {
        TexturePreviewTexture? cached = DecodedTextureCache.Shared.TryGet(upkPath, exportPath, requestedMipIndex, fallbackSlot);
        if (cached is not null)
            return cached;

        await s_loadLock.WaitAsync().ConfigureAwait(true);
        try
        {
            // Re-check the cache after acquiring the lock — another caller may
            // have decoded the same texture while we were waiting.
            cached = DecodedTextureCache.Shared.TryGet(upkPath, exportPath, requestedMipIndex, fallbackSlot);
            if (cached is not null)
                return cached;
            return await LoadFromUpkAsyncCore(upkPath, exportPath, fallbackSlot, log, requestedMipIndex).ConfigureAwait(true);
        }
        finally
        {
            s_loadLock.Release();
        }
    }

    private async Task<TexturePreviewTexture> LoadFromUpkAsyncCore(string upkPath, string exportPath, TexturePreviewMaterialSlot fallbackSlot, Action<string> log, int? requestedMipIndex)
    {
        // Reuse the shared repository (this path is always under s_loadLock) so
        // repeated loads from the same UPK don't re-read the whole file.
        var header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable
            .FirstOrDefault(e => string.Equals(e.GetPathName(), exportPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find texture export '{exportPath}'.");

        if (export.UnrealObject == null)
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UTexture2D texture)
            throw new InvalidOperationException($"Export '{exportPath}' is not a Texture2D.");

        EnsureManifestLoadedForPackage(upkPath, log);
        TextureEntry textureEntry = TextureManifest.Instance?.GetTextureEntryFromObject(export.ObjectNameIndex);
        if (textureEntry != null)
        {
            TextureFileCache.Instance.SetEntry(textureEntry, texture);
            TextureFileCache.Instance.LoadTextureCache();
        }

        IReadOnlyList<TexturePreviewMipLevel> mipLevels = BuildMipLevels(texture, textureEntry);
        Stream stream = ResolveTextureStream(texture, textureEntry, requestedMipIndex, mipLevels, out TexturePreviewMipLevel selectedMip, out int mipCount);
        if (stream == null)
            throw new InvalidOperationException($"Texture '{exportPath}' did not expose previewable mip data.");

        Stream containerStream = ResolveMipMapsStream(texture, textureEntry);

        byte[] containerBytes;
        using (MemoryStream copyStream = new())
        {
            containerStream.CopyTo(copyStream);
            containerBytes = copyStream.ToArray();
        }

        using MemoryStream ddsStream = new();
        stream.CopyTo(ddsStream);
        ddsStream.Position = 0;
        DdsFile dds = new();
        dds.Load(ddsStream);
        Bitmap bitmap = BitmapSourceToBitmap(dds.BitmapSource);
        byte[] rgbaPixels = ExtractRgba(bitmap);

        log?.Invoke($"Loaded UPK texture {exportPath} ({selectedMip.Width}x{selectedMip.Height}, {selectedMip.Format}, mip {selectedMip.AbsoluteIndex} from {selectedMip.Source}).");
        log?.Invoke($"Texture summary {exportPath}: {DescribeAverageColor(rgbaPixels, bitmap.Width, bitmap.Height)}");

        TexturePreviewTexture result = new()
        {
            Name = export.ObjectNameIndex.Name,
            SourcePath = upkPath,
            SourceDescription = $"UPK: {exportPath}",
            ExportPath = exportPath,
            Bitmap = bitmap,
            RgbaPixels = rgbaPixels,
            Width = bitmap.Width,
            Height = bitmap.Height,
            MipCount = mipCount,
            SelectedMipIndex = selectedMip.AbsoluteIndex,
            Format = selectedMip.Format,
            Compression = selectedMip.Format,
            ContainerType = "DDS",
            MipSource = selectedMip.Source,
            Slot = fallbackSlot,
            ContainerBytes = containerBytes,
            AvailableMipLevels = mipLevels
        };
        DecodedTextureCache.Shared.Put(upkPath, exportPath, requestedMipIndex, result);
        return result;
    }

    public async Task<TexturePreviewTexture> LoadFromObjectAsync(FObject textureObject, TexturePreviewMaterialSlot fallbackSlot, Action<string> log = null, int? requestedMipIndex = null)
    {
        ArgumentNullException.ThrowIfNull(textureObject);

        UnrealObjectTableEntryBase entry = textureObject.TableEntry;
        if (entry is UnrealImportTableEntry import)
            entry = import.GetExportEntry();

        if (entry is not UnrealExportTableEntry export)
            throw new InvalidOperationException($"Texture reference '{textureObject.GetPathName()}' could not be resolved to an export.");

        string upkPath = export.UnrealHeader?.FullFilename
            ?? throw new InvalidOperationException($"Texture reference '{textureObject.GetPathName()}' did not resolve to a source package path.");

        string cacheKeyExportPath = textureObject.GetPathName() ?? string.Empty;
        TexturePreviewTexture? cached = DecodedTextureCache.Shared.TryGet(upkPath, cacheKeyExportPath, requestedMipIndex, fallbackSlot);
        if (cached is not null)
            return cached;

        await s_loadLock.WaitAsync().ConfigureAwait(true);
        try
        {
            cached = DecodedTextureCache.Shared.TryGet(upkPath, cacheKeyExportPath, requestedMipIndex, fallbackSlot);
            if (cached is not null)
                return cached;
            return await LoadFromObjectAsyncCore(textureObject, export, upkPath, fallbackSlot, log, requestedMipIndex).ConfigureAwait(true);
        }
        finally
        {
            s_loadLock.Release();
        }
    }

    private async Task<TexturePreviewTexture> LoadFromObjectAsyncCore(FObject textureObject, UnrealExportTableEntry export, string upkPath, TexturePreviewMaterialSlot fallbackSlot, Action<string> log, int? requestedMipIndex)
    {
        if (export.UnrealObject == null)
            await export.UnrealHeader.ReadExportObjectAsync(export, null).ConfigureAwait(true);

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UTexture2D texture)
            throw new InvalidOperationException($"Texture reference '{textureObject.GetPathName()}' is not a Texture2D.");

        EnsureManifestLoadedForPackage(upkPath, log);
        TextureEntry textureEntry = TextureManifest.Instance?.GetTextureEntryFromObject(textureObject);
        if (textureEntry != null)
        {
            TextureFileCache.Instance.SetEntry(textureEntry, texture);
            TextureFileCache.Instance.LoadTextureCache();
        }

        IReadOnlyList<TexturePreviewMipLevel> mipLevels = BuildMipLevels(texture, textureEntry);
        Stream stream = ResolveTextureStream(texture, textureEntry, requestedMipIndex, mipLevels, out TexturePreviewMipLevel selectedMip, out int mipCount);
        if (stream == null)
            throw new InvalidOperationException($"Texture '{textureObject.GetPathName()}' did not expose previewable mip data.");

        Stream containerStream = ResolveMipMapsStream(texture, textureEntry);

        byte[] containerBytes;
        using (MemoryStream copyStream = new())
        {
            containerStream.CopyTo(copyStream);
            containerBytes = copyStream.ToArray();
        }

        using MemoryStream ddsStream = new();
        stream.CopyTo(ddsStream);
        ddsStream.Position = 0;
        DdsFile dds = new();
        dds.Load(ddsStream);
        Bitmap bitmap = BitmapSourceToBitmap(dds.BitmapSource);
        byte[] rgbaPixels = ExtractRgba(bitmap);

        string exportPath = textureObject.GetPathName();
        log?.Invoke($"Loaded UPK texture {exportPath} ({selectedMip.Width}x{selectedMip.Height}, {selectedMip.Format}, mip {selectedMip.AbsoluteIndex} from {selectedMip.Source}).");
        log?.Invoke($"Texture summary {exportPath}: {DescribeAverageColor(rgbaPixels, bitmap.Width, bitmap.Height)}");

        TexturePreviewTexture result = new()
        {
            Name = export.ObjectNameIndex.Name,
            SourcePath = upkPath,
            SourceDescription = $"UPK: {exportPath}",
            ExportPath = exportPath,
            Bitmap = bitmap,
            RgbaPixels = rgbaPixels,
            Width = bitmap.Width,
            Height = bitmap.Height,
            MipCount = mipCount,
            SelectedMipIndex = selectedMip.AbsoluteIndex,
            Format = selectedMip.Format,
            Compression = selectedMip.Format,
            ContainerType = "DDS",
            MipSource = selectedMip.Source,
            Slot = fallbackSlot,
            ContainerBytes = containerBytes,
            AvailableMipLevels = mipLevels
        };
        DecodedTextureCache.Shared.Put(upkPath, textureObject.GetPathName() ?? string.Empty, requestedMipIndex, result);
        return result;
    }

    public async Task<List<string>> GetTextureExportsAsync(string upkPath)
    {
        UpkFileRepository repository = new();
        var header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        return header.ExportTable
            .Where(export => string.Equals(export.ClassReferenceNameIndex?.Name, nameof(UTexture2D), StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(export.ClassReferenceNameIndex?.Name, "Texture2D", StringComparison.OrdinalIgnoreCase))
            .Select(static export => export.GetPathName())
            .OrderBy(static export => export)
            .ToList();
    }

    private static Stream ResolveTextureStream(
        UTexture2D texture,
        TextureEntry textureEntry,
        int? requestedMipIndex,
        IReadOnlyList<TexturePreviewMipLevel> mipLevels,
        out TexturePreviewMipLevel selectedMip,
        out int mipCount)
    {
        selectedMip = requestedMipIndex.HasValue
            ? mipLevels.FirstOrDefault(level => level.AbsoluteIndex == requestedMipIndex.Value)
                ?? throw new InvalidOperationException($"Texture did not expose requested mip level {requestedMipIndex.Value}.")
            : SelectHighestResolutionMip(mipLevels);

        mipCount = mipLevels.Count;
        if (string.Equals(selectedMip.Source, "TFC", StringComparison.OrdinalIgnoreCase))
            return TextureFileCache.Instance.Texture2D.GetObjectStream(selectedMip.RelativeIndex);

        return texture.GetObjectStream(selectedMip.AbsoluteIndex);
    }

    private static TexturePreviewMipLevel SelectHighestResolutionMip(IReadOnlyList<TexturePreviewMipLevel> mipLevels)
    {
        return mipLevels
            .OrderByDescending(level => level.Width * level.Height)
            .ThenByDescending(level => level.Width)
            .ThenByDescending(level => level.Height)
            .ThenBy(level => level.AbsoluteIndex)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Texture did not expose any previewable mip levels.");
    }

    private static Stream ResolveMipMapsStream(UTexture2D texture, TextureEntry textureEntry)
    {
        if (textureEntry != null && TextureFileCache.Instance.Texture2D.Mips.Count > 0)
            return TextureFileCache.Instance.Texture2D.GetMipMapsStream();

        return texture.GetMipMapsStream();
    }

    private static IReadOnlyList<TexturePreviewMipLevel> BuildMipLevels(UTexture2D texture, TextureEntry textureEntry)
    {
        List<TexturePreviewMipLevel> mipLevels = [];

        if (textureEntry != null && textureEntry.Data.Maps.Count > 0)
        {
            int absoluteIndex = (int)textureEntry.Data.Maps[0].Index;
            int relativeIndex = 0;
            foreach (FTexture2DMipMap mip in TextureFileCache.Instance.Texture2D.Mips)
            {
                if (mip.Data != null && mip.Data.Length > 0)
                {
                    mipLevels.Add(new TexturePreviewMipLevel
                    {
                        AbsoluteIndex = absoluteIndex,
                        RelativeIndex = relativeIndex,
                        Width = mip.SizeX,
                        Height = mip.SizeY,
                        DataSize = mip.Data.Length,
                        Source = "TFC",
                        Format = mip.OverrideFormat.ToString()
                    });
                }

                absoluteIndex++;
                relativeIndex++;
            }
        }

        for (int absoluteIndex = 0; absoluteIndex < texture.Mips.Count; absoluteIndex++)
        {
            FTexture2DMipMap mip = texture.Mips[absoluteIndex];
            if (mip.Data == null || mip.Data.Length == 0)
                continue;

            mipLevels.Add(new TexturePreviewMipLevel
            {
                AbsoluteIndex = absoluteIndex,
                RelativeIndex = absoluteIndex,
                Width = mip.SizeX,
                Height = mip.SizeY,
                DataSize = mip.Data.Length,
                Source = "UPK",
                Format = mip.OverrideFormat.ToString()
            });
        }

        return mipLevels
            .OrderBy(level => level.AbsoluteIndex)
            .ThenBy(level => level.Source)
            .ToArray();
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource bitmapSource)
    {
        using MemoryStream outStream = new();
        BitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        encoder.Save(outStream);
        outStream.Position = 0;
        using Bitmap temp = new(outStream);
        return new Bitmap(temp);
    }

    private static byte[] ExtractRgba(Bitmap bitmap)
    {
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[bitmap.Width * bitmap.Height * 4];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            for (int i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);

            return bgra;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void EnsureManifestLoadedForPackage(string upkPath, Action<string> log)
    {
        if (TextureManifest.Instance == null || string.IsNullOrWhiteSpace(upkPath))
            return;

        string packageDirectory = Path.GetDirectoryName(upkPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageDirectory))
            return;

        string manifestPath = Path.Combine(packageDirectory, TextureManifest.ManifestName);
        if (!File.Exists(manifestPath))
            return;

        bool sameManifest = string.Equals(TextureManifest.Instance.ManifestFilePath, manifestPath, StringComparison.OrdinalIgnoreCase);
        if (sameManifest && TextureManifest.Instance.Entries.Count > 0)
            return;

        try
        {
            int loadedCount = TextureManifest.Instance.LoadManifest(manifestPath);
            log?.Invoke($"Auto-loaded texture manifest for preview: {manifestPath} ({loadedCount:N0} entries).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Texture manifest load skipped for preview: {ex.Message}");
        }
    }

    private static string DescribeAverageColor(byte[] rgbaPixels, int width, int height)
    {
        if (rgbaPixels == null || rgbaPixels.Length < 4 || width <= 0 || height <= 0)
            return "avg=(n/a)";

        long r = 0;
        long g = 0;
        long b = 0;
        long a = 0;
        int pixels = Math.Min(rgbaPixels.Length / 4, width * height);

        for (int i = 0; i < pixels * 4; i += 4)
        {
            r += rgbaPixels[i + 0];
            g += rgbaPixels[i + 1];
            b += rgbaPixels[i + 2];
            a += rgbaPixels[i + 3];
        }

        if (pixels == 0)
            return "avg=(n/a)";

        return $"avg=({r / (double)pixels:0.0}, {g / (double)pixels:0.0}, {b / (double)pixels:0.0}, a={a / (double)pixels:0.0})";
    }
}


