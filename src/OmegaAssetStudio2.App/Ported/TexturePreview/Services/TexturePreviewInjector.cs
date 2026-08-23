using DDSLib;
using DDSLib.Constants;
using OmegaAssetStudio.BackupManager;
using OmegaAssetStudio.TextureManager;
using System.Linq;
using System.Numerics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using UpkManager.Constants;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.TexturePreview;

/// <summary>
/// Injects and resolves UPK/TFC texture targets for preview tooling.
/// </summary>
public sealed class TexturePreviewInjector
{
    private readonly TextureImportPolicy _importPolicy = new();

    /// <summary>
    /// Resolves the manifest and cache paths for a target texture export.
    /// </summary>
    /// <param name="upkPath">The source package path.</param>
    /// <param name="exportPath">The target texture export path.</param>
    /// <returns>The resolved target information.</returns>
    public async Task<TextureInjectionTargetInfo> ResolveTargetInfoAsync(string upkPath, string exportPath)
    {
        if (TextureManifest.Instance == null || TextureManifest.Instance.Entries.Count == 0 || string.IsNullOrWhiteSpace(TextureManifest.Instance.ManifestFilePath))
            throw new InvalidOperationException($"Load {TextureManifest.ManifestName} first before injecting textures.");

        (UTexture2D texture, TextureEntry textureEntry) = await LoadTargetAsync(upkPath, exportPath).ConfigureAwait(true);
        TextureImportDecision importDecision = _importPolicy.Resolve(texture, textureEntry);
        string sourceTfcPath = Path.Combine(TextureManifest.Instance.ManifestPath, textureEntry.Data.TextureFileName + ".tfc");
        string destinationTfcPath = Path.Combine(TextureManifest.Instance.ManifestPath, importDecision.TextureCacheName + ".tfc");

        return new TextureInjectionTargetInfo
        {
            ManifestFilePath = TextureManifest.Instance.ManifestFilePath,
            SourceTextureCachePath = sourceTfcPath,
            DestinationTextureCachePath = destinationTfcPath,
            ImportMode = importDecision.ImportType.ToString(),
            DestinationCacheName = importDecision.TextureCacheName,
            CurrentCacheName = textureEntry.Data.TextureFileName ?? string.Empty,
            CurrentCacheIsStandard = importDecision.CurrentCacheIsStandard,
            ImportReason = importDecision.Reason ?? string.Empty,
            TargetWidth = texture.SizeX,
            TargetHeight = texture.SizeY,
            TargetFormat = texture.Format.ToString(),
            TargetMipCount = textureEntry.Data.Maps.Count,
            TargetLODGroup = texture.LODGroup.ToString()
        };
    }

    /// <summary>
    /// Injects a replacement texture into a manifest-backed TFC texture target.
    /// </summary>
    /// <param name="upkPath">The source package path.</param>
    /// <param name="exportPath">The target texture export path.</param>
    /// <param name="sourceTexture">The replacement texture payload.</param>
    /// <param name="log">Optional log callback.</param>
    public async Task InjectAsync(string upkPath, string exportPath, TexturePreviewTexture sourceTexture, Action<string> log = null)
    {
        if (sourceTexture == null)
            throw new ArgumentNullException(nameof(sourceTexture));

        if (TextureManifest.Instance == null || TextureManifest.Instance.Entries.Count == 0 || string.IsNullOrWhiteSpace(TextureManifest.Instance.ManifestFilePath))
            throw new InvalidOperationException($"Load {TextureManifest.ManifestName} first before injecting textures.");

        log?.Invoke($"Opening package: {Path.GetFileName(upkPath)}");

        (UTexture2D texture, TextureEntry textureEntry) = await LoadTargetAsync(upkPath, exportPath).ConfigureAwait(true);

        FileFormat targetFormat = UTexture2D.ParseFileFormat(texture.Format);
        // Encode enough DDS mip levels so we have a valid entry at every
        // manifest mip Index. Highest manifest Index drives this — the
        // manifest may start at Index=1+ (top mip is NothingToDo in the UPK
        // for character textures), so a simple count won't suffice.
        // Example: a typical character _diff has Maps={Index=1,2,3,4} (mips 1..4 in TFC,
        // mip 0 stubbed). We need ddsHeader.MipMaps[1..4] populated → 5 levels.
        int maxMipIndex = textureEntry.Data.Maps.Count == 0
            ? 0
            : (int)textureEntry.Data.Maps.Max(m => m.Index);
        int targetMipCount = Math.Max(textureEntry.Data.Maps.Count, maxMipIndex + 1);
        bool targetLooksLikeNormalMap = IsLikelyNormalTarget(texture, exportPath);
        bool normalizeAsNormalMap = targetLooksLikeNormalMap || sourceTexture.Slot == TexturePreviewMaterialSlot.Normal;
        if (normalizeAsNormalMap)
            targetFormat = FileFormat.BC5;
        TextureImportDecision importDecision = _importPolicy.Resolve(texture, textureEntry);
        log?.Invoke(
            $"Target texture profile: format={texture.Format}, lodGroup={texture.LODGroup}, size={texture.SizeX}x{texture.SizeY}, mipCount={targetMipCount}, tfc={textureEntry.Data.TextureFileName}, sourceSlot={sourceTexture.Slot}, normalTarget={targetLooksLikeNormalMap}.");
        log?.Invoke($"Import cache policy: mode={importDecision.ImportType}, cache={importDecision.TextureCacheName}, standardCurrent={importDecision.CurrentCacheIsStandard}. {importDecision.Reason}");
        log?.Invoke($"Preparing texture for {exportPath}.");
        DdsFile dds = await Task.Run(() => BuildWritableTexture(sourceTexture, texture.SizeX, texture.SizeY, targetFormat, targetMipCount, normalizeAsNormalMap, log)).ConfigureAwait(true);

        if (dds.MipMaps.Count < targetMipCount)
            throw new InvalidOperationException($"Converted texture only produced {dds.MipMaps.Count} mipmaps, but target requires {targetMipCount}.");

        TextureFileCache.Instance.SetEntry(textureEntry, texture);

        string sourceTfcPath = Path.Combine(TextureManifest.Instance.ManifestPath, textureEntry.Data.TextureFileName + ".tfc");
        log?.Invoke($"Loading existing texture cache: {Path.GetFileName(sourceTfcPath)}");
        if (!TextureFileCache.Instance.LoadFromFile(sourceTfcPath, textureEntry))
            throw new InvalidOperationException($"Could not load existing texture cache data from {sourceTfcPath}.");

        EnsureBackupExists(TextureManifest.Instance.ManifestFilePath);
        EnsureBackupExists(sourceTfcPath);
        string destinationTfcPath = Path.Combine(TextureManifest.Instance.ManifestPath, importDecision.TextureCacheName + ".tfc");
        EnsureBackupExists(destinationTfcPath);

        log?.Invoke("Writing converted texture to cache.");
        WriteResult result = await Task.Run(() =>
            TextureFileCache.Instance.WriteTexture(TextureManifest.Instance.ManifestPath, importDecision.TextureCacheName, importDecision.ImportType, dds)).ConfigureAwait(true);
        switch (result)
        {
            case WriteResult.Success:
                log?.Invoke("Saving updated texture manifest.");
                TextureManifest.Instance.SaveManifest();
                log?.Invoke($"Injected DDS into {exportPath}.");
                log?.Invoke($"Updated texture cache: {destinationTfcPath}");
                log?.Invoke($"Saved manifest: {TextureManifest.Instance.ManifestFilePath}");
                return;
            case WriteResult.MipMapError:
                throw new InvalidOperationException("Texture injection failed while rebuilding mip data for the target texture cache.");
            case WriteResult.SizeReplaceError:
                throw new InvalidOperationException("Injected DDS payload is larger than the existing texture cache allocation. Resize/compress the DDS to fit or extend the writer to support relocation.");
            default:
                throw new InvalidOperationException($"Texture injection failed with result '{result}'.");
        }
    }

    private static DdsFile BuildWritableTexture(
        TexturePreviewTexture sourceTexture,
        int targetWidth,
        int targetHeight,
        FileFormat targetFormat,
        int targetMipCount,
        bool normalizeAsNormalMap,
        Action<string> log)
    {
        if (sourceTexture.Width <= 0 || sourceTexture.Height <= 0)
            throw new InvalidOperationException("Source texture has invalid dimensions.");

        if (sourceTexture.RgbaPixels == null || sourceTexture.RgbaPixels.Length != sourceTexture.Width * sourceTexture.Height * 4)
            throw new InvalidOperationException("Source texture does not contain a valid RGBA pixel buffer.");

        if (targetMipCount <= 0)
            targetMipCount = 1;

        byte[] preparedRgba = GetPreparedRgba(sourceTexture, targetWidth: targetWidth, targetHeight: targetHeight, normalizeAsNormalMap, log);

        if (string.Equals(sourceTexture.ContainerType, "DDS", StringComparison.OrdinalIgnoreCase) && sourceTexture.ContainerBytes != null)
        {
            DdsFile sourceDds = new();
            log?.Invoke("Decoding source DDS.");
            using MemoryStream stream = new(sourceTexture.ContainerBytes, writable: false);
            sourceDds.Load(stream);

            if (sourceDds.FileFormat != targetFormat)
                log?.Invoke($"Converting DDS from {sourceDds.FileFormat} to {targetFormat}.");

            if (sourceDds.MipMaps.Count < targetMipCount)
                log?.Invoke($"DDS mip count {sourceDds.MipMaps.Count} is lower than target mip count {targetMipCount}; regenerating mipmaps.");

            log?.Invoke($"Encoding texture as {targetFormat} with {targetMipCount} mip level(s).");
            byte[] ddsRgba = normalizeAsNormalMap
                ? PrepareNormalMapRgba(sourceDds.BitmapData, log)
                : sourceDds.BitmapData;
            if (sourceDds.Width != targetWidth || sourceDds.Height != targetHeight)
            {
                log?.Invoke($"Resizing DDS source from {sourceDds.Width}x{sourceDds.Height} to {targetWidth}x{targetHeight} for injection.");
                ddsRgba = ResizeRgba(ddsRgba, sourceDds.Width, sourceDds.Height, targetWidth, targetHeight, log);
            }
            return DdsFile.FromRgba(targetWidth, targetHeight, ddsRgba, targetFormat, targetMipCount);
        }

        if (normalizeAsNormalMap)
            log?.Invoke("Normal-map preprocessing: renormalizing tangent-space RGB before DDS conversion.");

        log?.Invoke($"Converting {sourceTexture.ContainerType} source to {targetFormat}.");
        log?.Invoke($"Encoding texture as {targetFormat} with {targetMipCount} mip level(s).");
        return DdsFile.FromRgba(targetWidth, targetHeight, preparedRgba, targetFormat, targetMipCount);
    }

    private static byte[] GetPreparedRgba(TexturePreviewTexture sourceTexture, int targetWidth, int targetHeight, bool normalizeAsNormalMap, Action<string> log)
    {
        if (sourceTexture.Width == targetWidth && sourceTexture.Height == targetHeight)
        {
            return normalizeAsNormalMap
                ? PrepareNormalMapRgba(sourceTexture.RgbaPixels, log)
                : (byte[])sourceTexture.RgbaPixels.Clone();
        }

        log?.Invoke($"Resizing source texture from {sourceTexture.Width}x{sourceTexture.Height} to {targetWidth}x{targetHeight} for injection.");

        // CRITICAL: resize from RgbaPixels (always populated by callers) instead
        // of sourceTexture.Bitmap (defaults to a blank 1x1 when callers only
        // supply pixel bytes — e.g. BundleTextureUpkReplacer.BuildPreviewTexture).
        // Using the stale 1x1 Bitmap would upscale a blank to the target,
        // producing all-zero RGBA → all-black DXT1 in the TFC. This bug only
        // surfaced for Bundle retarget writes because the standard Texture Editor
        // happens to populate Bitmap as well as RgbaPixels.
        byte[] rgba = ResizeRgba(sourceTexture.RgbaPixels, sourceTexture.Width, sourceTexture.Height, targetWidth, targetHeight, log);
        return normalizeAsNormalMap
            ? PrepareNormalMapRgba(rgba, log)
            : rgba;
    }

    private static Bitmap ResizeBitmap(Bitmap source, int targetWidth, int targetHeight)
    {
        Bitmap resized = new(targetWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(resized);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        return resized;
    }

    private static byte[] BitmapToRgba(Bitmap bitmap)
    {
        using Bitmap clone = new(bitmap);
        var data = clone.LockBits(new System.Drawing.Rectangle(0, 0, clone.Width, clone.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[clone.Width * clone.Height * 4];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }

            return bgra;
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }

    private static byte[] ResizeRgba(byte[] rgba, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, Action<string> log)
    {
        using Bitmap bitmap = new(sourceWidth, sourceHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = (byte[])rgba.Clone();
            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using Bitmap resized = ResizeBitmap(bitmap, targetWidth, targetHeight);
        return BitmapToRgba(resized);
    }

    private static bool IsLikelyNormalTarget(UTexture2D texture, string exportPath)
    {
        string exportName = exportPath ?? string.Empty;
        return texture.LODGroup is UTexture.TextureGroup.TEXTUREGROUP_WorldNormalMap
            or UTexture.TextureGroup.TEXTUREGROUP_CharacterNormalMap
            or UTexture.TextureGroup.TEXTUREGROUP_WeaponNormalMap
            or UTexture.TextureGroup.TEXTUREGROUP_VehicleNormalMap
            || exportName.Contains("normal", StringComparison.OrdinalIgnoreCase)
            || exportName.Contains("_n", StringComparison.OrdinalIgnoreCase)
            || exportName.Contains("_nm", StringComparison.OrdinalIgnoreCase)
            || exportName.Contains("norm", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] PrepareNormalMapRgba(byte[] rgba, Action<string> log)
    {
        byte[] prepared = (byte[])rgba.Clone();
        for (int i = 0; i < prepared.Length; i += 4)
        {
            float x = (prepared[i + 0] / 255.0f) * 2.0f - 1.0f;
            float y = (prepared[i + 1] / 255.0f) * 2.0f - 1.0f;
            float zSquared = MathF.Max(0.0f, 1.0f - (x * x) - (y * y));
            float z = MathF.Sqrt(zSquared);
            Vector3 normal = Vector3.Normalize(new Vector3(x, y, zSquared > 1e-8f ? z : 0.0f));

            prepared[i + 0] = EncodeNormalComponent(normal.X);
            prepared[i + 1] = EncodeNormalComponent(normal.Y);
            prepared[i + 2] = EncodeNormalComponent(normal.Z);
            prepared[i + 3] = 255;
        }

        return prepared;
    }

    private static byte EncodeNormalComponent(float value)
    {
        float encoded = ((Math.Clamp(value, -1.0f, 1.0f) * 0.5f) + 0.5f) * 255.0f;
        return (byte)Math.Clamp((int)MathF.Round(encoded), 0, 255);
    }

    // Backs up `path` before every write. First save → <path>.bak; subsequent saves (when
    // .bak already exists) → <path>.bak.<yyyyMMdd_HHmmss_fff> via the shared BackupFileHelper.
    // No-op when the source doesn't exist (e.g. a brand-new output file).
    private static void EnsureBackupExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        BackupFileHelper.CreateBackup(path);
    }

    private static async Task<(UTexture2D Texture, TextureEntry TextureEntry)> LoadTargetAsync(string upkPath, string exportPath)
    {
        UpkFileRepository repository = new();
        var header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable
            .FirstOrDefault(e => string.Equals(e.GetPathName(), exportPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find texture export '{exportPath}'.");

        // Two-stage parse: ReadExportObjectAsync populates the deep export
        // properties (notably UTexture2D.TextureFileCacheName) that the manifest
        // lookup keys on. ParseUnrealObject alone leaves those fields null, so
        // GetTextureEntryFromObject silently returns null and the inject path
        // wrongly reports "not found in TextureFileCacheManifest.bin" for
        // legitimately TFC-backed UI textures (e.g. store costume previews).
        if (export.UnrealObject == null)
            await header.ReadExportObjectAsync(export, null).ConfigureAwait(true);

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UTexture2D texture)
            throw new InvalidOperationException($"Export '{exportPath}' is not a Texture2D.");

        TextureEntry textureEntry = TextureManifest.Instance.GetTextureEntryFromObject(export.ObjectNameIndex);
        if (textureEntry == null)
            throw new InvalidOperationException($"Texture '{exportPath}' was not found in {TextureManifest.ManifestName}.");

        if (string.IsNullOrWhiteSpace(textureEntry.Data.TextureFileName))
            throw new InvalidOperationException($"Texture '{exportPath}' does not point at a writable texture cache file.");

        return (texture, textureEntry);
    }

    /// <summary>
    /// Injects a replacement texture directly into a UPK whose Texture2D mip data is stored
    /// inline (not in a .tfc file).  Used for HUD/UI textures that have no manifest entry.
    /// Dimensions and format must match the target; a .bak backup is created automatically.
    /// </summary>
    public async Task InjectInlineAsync(string upkPath, string exportPath, TexturePreviewTexture sourceTexture, Action<string> log = null)
    {
        if (sourceTexture == null) throw new ArgumentNullException(nameof(sourceTexture));

        log?.Invoke($"Opening package: {Path.GetFileName(upkPath)}");
        (UTexture2D texture, byte[] originalBytes, UnrealHeader header, UnrealExportTableEntry export) =
            await LoadTextureExportAsync(upkPath, exportPath).ConfigureAwait(true);

        int targetMipCount = texture.Mips.Count(m => m.Data != null && m.Data.Length > 0);
        if (targetMipCount == 0) targetMipCount = 1;

        bool isNormal = IsLikelyNormalTarget(texture, exportPath);
        FileFormat targetFormat = UTexture2D.ParseFileFormat(texture.Format);
        if (isNormal)
            targetFormat = FileFormat.BC5;
        log?.Invoke($"Target: format={texture.Format}, size={texture.SizeX}\u00d7{texture.SizeY}, mips={targetMipCount}.");

        DdsFile dds = await Task.Run(() => BuildWritableTexture(sourceTexture, texture.SizeX, texture.SizeY, targetFormat, targetMipCount, isNormal, log)).ConfigureAwait(true);

        if (dds.MipMaps.Count < targetMipCount)
            throw new InvalidOperationException($"DDS produced {dds.MipMaps.Count} mip(s) but target requires {targetMipCount}.");

        // Re-encode each mip as an LZO_ENC bulk data block in a new export buffer.
        // Layout mirrors FTexture2DMipMap: [BulkData header 16B] [raw data] [SizeX int32] [SizeY int32]
        // The original package stores mip data as uncompressed inline bulk data (flags=0) inside
        // package-level LZO chunks.  We output an uncompressed package, so we also write the mip
        // data as uncompressed inline bulk data â€” NOT as per-export LZO_ENC.
        byte[] prefix = GetTextureMipPrefix(export, texture);
        byte[] mipSuffix = GetTextureMipSuffix(export, texture);

        log?.Invoke($"[Diag] exportBytes={export.UnrealObjectReader.GetBytes().Length} prefix={prefix.Length} mipArrayOffset={texture.MipArrayOffset} mipCount={targetMipCount} suffix={mipSuffix.Length}");

        using MemoryStream mipStream = new();
        List<UpkRepacker.BulkDataPatch> bulkPatches = [];
        int mipRegionBase = prefix.Length + 4; // prefix + mipCount int32
        for (int i = 0; i < targetMipCount; i++)
        {
            byte[] mipData = dds.MipMaps[i].MipMap;

            // Build uncompressed inline bulk data:
            //   uint32 BulkDataFlags = 0          (uncompressed inline)
            //   int32  UncompressedSize            (= raw data length)
            //   int32  CompressedSize              (= same as uncompressed)
            //   int32  Offset                      (absolute file offset â€” patched by UpkRepacker)
            //   byte[] RawData[CompressedSize]
            //   int32  SizeX
            //   int32  SizeY
            int chunkStartInExport = mipRegionBase + (int)mipStream.Position;
            // offset field is at byte 12, data starts at byte 16
            bulkPatches.Add(new UpkRepacker.BulkDataPatch(chunkStartInExport + 12, chunkStartInExport + 16));

            mipStream.Write(BitConverter.GetBytes((uint)0), 0, 4);         // BulkDataFlags = 0 (uncompressed)
            mipStream.Write(BitConverter.GetBytes(mipData.Length), 0, 4);   // UncompressedSize
            mipStream.Write(BitConverter.GetBytes(mipData.Length), 0, 4);   // CompressedSize (same)
            mipStream.Write(BitConverter.GetBytes(0), 0, 4);               // Offset placeholder (patched later)
            mipStream.Write(mipData, 0, mipData.Length);                   // Raw pixel data
            mipStream.Write(BitConverter.GetBytes(dds.MipMaps[i].Width),  0, 4); // SizeX
            mipStream.Write(BitConverter.GetBytes(dds.MipMaps[i].Height), 0, 4); // SizeY

            log?.Invoke($"[Diag] mip[{i}] mipData={mipData.Length} bulkEntry={16 + mipData.Length + 8}");
        }

        byte[] mipCountBytes = BitConverter.GetBytes(targetMipCount);
        byte[] newMipRegion = mipStream.ToArray();
        log?.Invoke($"[Diag] newMipRegion={newMipRegion.Length} mipSuffix={mipSuffix.Length} newExportBuffer={prefix.Length + 4 + newMipRegion.Length + mipSuffix.Length}");
        byte[] newExportBuffer = new byte[prefix.Length + 4 + newMipRegion.Length + mipSuffix.Length];
        Buffer.BlockCopy(prefix,        0, newExportBuffer, 0,                                         prefix.Length);
        Buffer.BlockCopy(mipCountBytes, 0, newExportBuffer, prefix.Length,                             4);
        Buffer.BlockCopy(newMipRegion,  0, newExportBuffer, prefix.Length + 4,                         newMipRegion.Length);
        Buffer.BlockCopy(mipSuffix,     0, newExportBuffer, prefix.Length + 4 + newMipRegion.Length,  mipSuffix.Length);

        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, export.TableIndex - 1, newExportBuffer, bulkPatches)
            : UpkRepacker.Repack(originalBytes, header, export.TableIndex - 1, newExportBuffer, bulkPatches);

        EnsureBackupExists(upkPath);
        await SafeWriteAsync(upkPath, repacked).ConfigureAwait(true);
        log?.Invoke($"Inline texture injected into {exportPath} in {Path.GetFileName(upkPath)}.");
    }

    /// <summary>
    /// Batched inline injection: rewrites a UPK exactly ONCE with N texture
    /// replacements applied in a single repack pass. Replaces the per-texture
    /// loop pattern that caused 73+ sequential rewrites of the same UPK in
    /// big icon packs, which accumulated table-offset drift and corrupted
    /// unrelated content sharing the package. Per-texture failures are
    /// reported via the returned dictionary and don't abort the batch.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (bool Ok, string Message)>> InjectInlineBatchAsync(
        string upkPath,
        IReadOnlyList<(string ExportPath, TexturePreviewTexture Source)> replacements,
        Action<string> log = null)
    {
        var results = new Dictionary<string, (bool Ok, string Message)>(StringComparer.OrdinalIgnoreCase);
        if (replacements is null || replacements.Count == 0) return results;

        log?.Invoke($"Batch open: {Path.GetFileName(upkPath)} ({replacements.Count} requested)");

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(true);
        UpkFileRepository repository = new();
        UnrealHeader header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        // Start with every export's existing bytes; replaced exports overwrite
        // their slot. Unchanged ones pass through verbatim.
        var buffers = header.ExportTable
            .Select(e => new UpkRepacker.ExportBuffer(e.UnrealObjectReader.GetBytes(), Array.Empty<UpkRepacker.BulkDataPatch>()))
            .ToList();

        int applied = 0;
        foreach (var (exportPath, sourceTexture) in replacements)
        {
            try
            {
                UnrealExportTableEntry export = header.ExportTable
                    .FirstOrDefault(e => string.Equals(e.GetPathName(), exportPath, StringComparison.OrdinalIgnoreCase));
                if (export is null)
                {
                    results[exportPath] = (false, "export not found");
                    continue;
                }
                if (export.UnrealObject == null)
                    await export.ParseUnrealObject(false, false).ConfigureAwait(true);
                if (export.UnrealObject is not IUnrealObject uo || uo.UObject is not UTexture2D texture)
                {
                    results[exportPath] = (false, "not a Texture2D");
                    continue;
                }

                // TFC-backed textures CANNOT go through the inline batch:
                // their UPK only stores tiny metadata stubs and the mip data
                // lives in a separate .tfc file. Forcibly inlining them bloats
                // the UPK and leaves the texture caching system in an
                // inconsistent state (TextureFileCacheName still pointing at
                // the TFC, but the inline blob disagrees). Caller has to route
                // these through the TFC injector singly.
                string tfcName = texture.TextureFileCacheName?.Name;
                if (!string.IsNullOrWhiteSpace(tfcName))
                {
                    results[exportPath] = (false, $"tfc-backed:{tfcName}");
                    continue;
                }

                int mipCount = texture.Mips.Count(m => m.Data != null && m.Data.Length > 0);
                if (mipCount == 0) mipCount = 1;
                bool isNormal = IsLikelyNormalTarget(texture, exportPath);
                FileFormat targetFormat = UTexture2D.ParseFileFormat(texture.Format);
                if (isNormal) targetFormat = FileFormat.BC5;

                DdsFile dds = await Task.Run(() =>
                    BuildWritableTexture(sourceTexture, texture.SizeX, texture.SizeY, targetFormat, mipCount, isNormal, null))
                    .ConfigureAwait(true);
                if (dds.MipMaps.Count < mipCount)
                {
                    results[exportPath] = (false, $"DDS produced {dds.MipMaps.Count} mips, target needs {mipCount}");
                    continue;
                }

                byte[] prefix = GetTextureMipPrefix(export, texture);
                byte[] suffix = GetTextureMipSuffix(export, texture);

                using MemoryStream mipStream = new();
                List<UpkRepacker.BulkDataPatch> bulkPatches = new();
                int mipRegionBase = prefix.Length + 4; // prefix + mipCount int32
                for (int i = 0; i < mipCount; i++)
                {
                    byte[] mipData = dds.MipMaps[i].MipMap;
                    int chunkStartInExport = mipRegionBase + (int)mipStream.Position;
                    bulkPatches.Add(new UpkRepacker.BulkDataPatch(chunkStartInExport + 12, chunkStartInExport + 16));
                    mipStream.Write(BitConverter.GetBytes((uint)0), 0, 4);
                    mipStream.Write(BitConverter.GetBytes(mipData.Length), 0, 4);
                    mipStream.Write(BitConverter.GetBytes(mipData.Length), 0, 4);
                    mipStream.Write(BitConverter.GetBytes(0), 0, 4);
                    mipStream.Write(mipData, 0, mipData.Length);
                    mipStream.Write(BitConverter.GetBytes(dds.MipMaps[i].Width), 0, 4);
                    mipStream.Write(BitConverter.GetBytes(dds.MipMaps[i].Height), 0, 4);
                }

                byte[] mipCountBytes = BitConverter.GetBytes(mipCount);
                byte[] mipRegion = mipStream.ToArray();
                byte[] newExportBuffer = new byte[prefix.Length + 4 + mipRegion.Length + suffix.Length];
                Buffer.BlockCopy(prefix, 0, newExportBuffer, 0, prefix.Length);
                Buffer.BlockCopy(mipCountBytes, 0, newExportBuffer, prefix.Length, 4);
                Buffer.BlockCopy(mipRegion, 0, newExportBuffer, prefix.Length + 4, mipRegion.Length);
                Buffer.BlockCopy(suffix, 0, newExportBuffer, prefix.Length + 4 + mipRegion.Length, suffix.Length);

                buffers[export.TableIndex - 1] = new UpkRepacker.ExportBuffer(newExportBuffer, bulkPatches);
                results[exportPath] = (true, "queued");
                applied++;
            }
            catch (Exception ex)
            {
                results[exportPath] = (false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (applied == 0)
        {
            log?.Invoke($"Batch: nothing applicable, leaving {Path.GetFileName(upkPath)} untouched");
            return results;
        }

        log?.Invoke($"Batch repack: {Path.GetFileName(upkPath)} with {applied} export(s) in single pass");
        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, buffers)
            : UpkRepacker.Repack(originalBytes, header, buffers);

        EnsureBackupExists(upkPath);
        await SafeWriteAsync(upkPath, repacked).ConfigureAwait(true);
        log?.Invoke($"Batch wrote {Path.GetFileName(upkPath)} ({applied} export(s) modified, 1 rewrite)");
        return results;
    }

    // Crash-safe write: stage to .omtmp next to target, then atomic move.
    // If the process dies between WriteAllBytes and Move, the .omtmp is left
    // behind but the original UPK is untouched. Atomic Move-with-overwrite
    // (NTFS MoveFileEx) guarantees the target either points at the new file
    // or the old file — never a partial.
    private static async Task SafeWriteAsync(string targetPath, byte[] contents)
    {
        string tmp = targetPath + ".omtmp";
        await File.WriteAllBytesAsync(tmp, contents).ConfigureAwait(true);
        File.Move(tmp, targetPath, overwrite: true);
    }

    /// <summary>
    /// Replaces a single inline mip identified by the {texture}_mip{N}_{W}x{H}.dds naming convention.
    /// </summary>
    public async Task<string> ReplaceInlineMipFromDdsAsync(string upkPath, string ddsPath, bool inplace = false, Action<string> log = null)
    {
        if (string.IsNullOrWhiteSpace(upkPath))
            throw new ArgumentException("UPK path is required.", nameof(upkPath));
        if (string.IsNullOrWhiteSpace(ddsPath))
            throw new ArgumentException("DDS path is required.", nameof(ddsPath));
        if (!File.Exists(upkPath))
            throw new FileNotFoundException("UPK file not found.", upkPath);
        if (!File.Exists(ddsPath))
            throw new FileNotFoundException("DDS file not found.", ddsPath);

        (string textureName, int mipIndex, int expectedWidth, int expectedHeight) = ParseDdsFileName(ddsPath);

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(true);
        UpkFileRepository repository = new();
        UnrealHeader header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable.FirstOrDefault(static e => false);
        foreach (UnrealExportTableEntry candidate in header.ExportTable)
        {
            string pathName = candidate.GetPathName();
            string objectName = candidate.ObjectNameIndex?.Name ?? string.Empty;
            if (string.Equals(pathName, textureName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objectName, textureName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(pathName), textureName, StringComparison.OrdinalIgnoreCase))
            {
                export = candidate;
                break;
            }
        }

        if (export == null)
            throw new InvalidOperationException($"Could not find texture export '{textureName}' in {Path.GetFileName(upkPath)}.");

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UTexture2D texture)
            throw new InvalidOperationException($"Export '{textureName}' is not a Texture2D.");

        if (mipIndex < 0 || mipIndex >= texture.Mips.Count)
            throw new InvalidOperationException($"Mip index {mipIndex} is out of range for '{textureName}'.");

        FTexture2DMipMap targetMip = texture.Mips[mipIndex];
        if (mipIndex < texture.FirstResourceMemMip)
            throw new InvalidOperationException($"Mip[{mipIndex}] of '{textureName}' is stored in TFC, not inline.");

        byte[] rawDds = await File.ReadAllBytesAsync(ddsPath).ConfigureAwait(true);
        byte[] pixelData = StripDdsHeader(rawDds);
        if (pixelData.Length != targetMip.Data.Length)
            throw new InvalidOperationException($"Size mismatch for '{textureName}' mip[{mipIndex}]: got {pixelData.Length} bytes, expected {targetMip.Data.Length}.");
        if (targetMip.SizeX != expectedWidth || targetMip.SizeY != expectedHeight)
            throw new InvalidOperationException($"Dimension mismatch for '{textureName}' mip[{mipIndex}]: file says {expectedWidth}x{expectedHeight}, UPK has {targetMip.SizeX}x{targetMip.SizeY}.");

        byte[] exportBytes = export.UnrealObjectReader.GetBytes();
        int pixelOffset = LocateInlineMipPixelOffset(exportBytes, texture, mipIndex);
        if (pixelOffset < 0 || pixelOffset + pixelData.Length > exportBytes.Length)
            throw new InvalidOperationException($"Could not locate inline pixel data for '{textureName}' mip[{mipIndex}].");

        Buffer.BlockCopy(pixelData, 0, exportBytes, pixelOffset, pixelData.Length);

        List<UpkRepacker.ExportBuffer> buffers = header.ExportTable
            .Select(static entry => new UpkRepacker.ExportBuffer(entry.UnrealObjectReader.GetBytes(), []))
            .ToList();
        buffers[export.TableIndex - 1] = new UpkRepacker.ExportBuffer(exportBytes, []);

        byte[] repacked = header.CompressedChunks.Count > 0
            ? UpkRepacker.RepackCompressed(originalBytes, header, buffers)
            : UpkRepacker.Repack(originalBytes, header, buffers);

        string outputPath = inplace
            ? upkPath
            : Path.Combine(Path.GetDirectoryName(upkPath) ?? string.Empty, Path.GetFileNameWithoutExtension(upkPath) + "_patched" + Path.GetExtension(upkPath));

        EnsureBackupExists(upkPath);
        await File.WriteAllBytesAsync(outputPath, repacked).ConfigureAwait(true);
        log?.Invoke($"Patched '{textureName}' mip[{mipIndex}] in {Path.GetFileName(outputPath)}.");
        return outputPath;
    }

    private static async Task<(UTexture2D Texture, byte[] OriginalBytes, UnrealHeader Header, UnrealExportTableEntry Export)>
        LoadTextureExportAsync(string upkPath, string exportPath)
    {
        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(true);
        UpkFileRepository repository = new();
        UnrealHeader header = await repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable
            .FirstOrDefault(e => string.Equals(e.GetPathName(), exportPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find texture export '{exportPath}'.");

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not UTexture2D texture)
            throw new InvalidOperationException($"Export '{exportPath}' is not a Texture2D.");

        return (texture, originalBytes, header, export);
    }

    private static (string TextureName, int MipIndex, int Width, int Height) ParseDdsFileName(string ddsPath)
    {
        string baseName = Path.GetFileName(ddsPath);
        var match = System.Text.RegularExpressions.Regex.Match(baseName, "^(.+)_mip(\\d+)_(\\d+)x(\\d+)\\.dds$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"Cannot parse filename '{baseName}'. Expected format: {{texture_name}}_mip{{N}}_{{W}}x{{H}}.dds");

        return (
            match.Groups[1].Value,
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
            int.Parse(match.Groups[4].Value));
    }

    private static byte[] StripDdsHeader(byte[] raw)
    {
        if (raw.Length >= 4 && raw[0] == 0x44 && raw[1] == 0x44 && raw[2] == 0x53 && raw[3] == 0x20)
        {
            if (raw.Length < 128)
                throw new InvalidOperationException("File has DDS magic but is too short to contain a full header.");
            return raw[128..];
        }

        return raw;
    }

    private static int LocateInlineMipPixelOffset(byte[] exportBytes, UTexture2D texture, int mipIndex)
    {
        int cursor = texture.MipArrayOffset + 4;
        for (int i = 0; i < texture.Mips.Count; i++)
        {
            using MemoryStream scanner = new(exportBytes, cursor, exportBytes.Length - cursor, writable: false);
            using BinaryReader br = new(scanner);
            uint flags = br.ReadUInt32();
            int uncompressedSize = br.ReadInt32();
            int compressedSize = br.ReadInt32();
            _ = br.ReadInt32();
            int payloadSize = (flags & (uint)(BulkDataCompressionTypes.Unused | BulkDataCompressionTypes.StoreInSeparatefile)) != 0 ? 0 : compressedSize;
            int pixelOffset = cursor + 16;
            if (i == mipIndex)
                return (flags & 0x1) == 0 ? pixelOffset : -1;

            cursor += 16 + payloadSize + 4 + 4;
        }

        return -1;
    }

    // Returns the bytes of the export buffer up to (not including) the mip count int32.
    private static byte[] GetTextureMipPrefix(UnrealExportTableEntry export, UTexture2D texture)
    {
        byte[] exportBytes = export.UnrealObjectReader.GetBytes();
        // MipArrayOffset is the reader position right after the tagged properties (after the None
        // terminator), which is where the Mips count int32 begins.
        return exportBytes[..texture.MipArrayOffset];
    }

    // Returns the bytes after the last original mip's SizeY field, i.e. everything from the
    // TextureFileCacheGuid onward (GUID + cached platform mip arrays + flash mip data).
    private static byte[] GetTextureMipSuffix(UnrealExportTableEntry export, UTexture2D texture)
    {
        byte[] exportBytes = export.UnrealObjectReader.GetBytes();
        int cursor = texture.MipArrayOffset + 4; // skip mip count int32
        foreach (var mip in texture.Mips)
        {
            // Each FTexture2DMipMap bulk data header is: flags(4)+uncompSize(4)+compSize(4)+offset(4) = 16 bytes
            // followed by compressed data blocks, then SizeX(4)+SizeY(4).
            // We stored the original reader bytes so we can scan them directly.
            using MemoryStream scanner = new(exportBytes, cursor, exportBytes.Length - cursor, writable: false);
            using BinaryReader br = new(scanner);
            uint flags = br.ReadUInt32();
            int uncompSize = br.ReadInt32();
            int compSize = br.ReadInt32();
            int offset = br.ReadInt32(); // absolute data offset, ignore
            // If StoreInSeparatefile or Unused: no payload follows
            const uint nothingToDo = (uint)(BulkDataCompressionTypes.Unused | BulkDataCompressionTypes.StoreInSeparatefile);
            int payloadSize = (flags & nothingToDo) != 0 ? 0 : compSize;
            cursor += 16 + payloadSize + 4 + 4; // header + payload + SizeX + SizeY
        }
        return exportBytes[cursor..];
    }
}

/// <summary>
/// Describes the resolved target files for a texture injection operation, plus the import
/// decision (which cache the bytes will land in, why, and the target texture's on-disk shape).
/// </summary>
public sealed class TextureInjectionTargetInfo
{
    public string ManifestFilePath { get; init; } = string.Empty;
    public string SourceTextureCachePath { get; init; } = string.Empty;
    public string DestinationTextureCachePath { get; init; } = string.Empty;

    /// <summary>"New" / "Add" / "Replace" — the cache write mode.</summary>
    public string ImportMode { get; init; } = string.Empty;

    /// <summary>Cache name the new bytes will be written into (no .tfc suffix).</summary>
    public string DestinationCacheName { get; init; } = string.Empty;

    /// <summary>Cache name the texture currently lives in (no .tfc suffix).</summary>
    public string CurrentCacheName { get; init; } = string.Empty;

    /// <summary>True if the current cache is one of the shipped game caches (TFCLIst.txt).</summary>
    public bool CurrentCacheIsStandard { get; init; }

    /// <summary>Human-readable rationale for the import-mode decision.</summary>
    public string ImportReason { get; init; } = string.Empty;

    /// <summary>Target texture pixel dimensions.</summary>
    public int TargetWidth { get; init; }
    public int TargetHeight { get; init; }

    /// <summary>UE3 pixel format of the target (PF_DXT1 / PF_DXT5 / etc.).</summary>
    public string TargetFormat { get; init; } = string.Empty;

    /// <summary>Number of mip levels stored in the target's manifest entry.</summary>
    public int TargetMipCount { get; init; }

    /// <summary>LOD streaming group baked into the target.</summary>
    public string TargetLODGroup { get; init; } = string.Empty;
}

