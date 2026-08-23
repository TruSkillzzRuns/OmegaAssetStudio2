using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>Why a texture cannot be replaced.</summary>
public enum ReplaceRefusal
{
    None = 0,
    PixelsAreInTextureCache,
    FormatNotSupported,
    NoInlineMips,
    PropertiesUnreadable,
}

/// <summary>The outcome of checking or performing a replacement.</summary>
public sealed record ReplaceResult(bool Succeeded, string Message, ReplaceRefusal Refusal = ReplaceRefusal.None)
{
    public static ReplaceResult Refuse(ReplaceRefusal refusal, string message) => new(false, message, refusal);
    public static ReplaceResult Ok(string message) => new(true, message);
}

/// <summary>
/// Replaces the pixels of a texture stored inside a package.
/// </summary>
/// <remarks>
/// Only the dimensions and format already in the file are used. The replacement
/// is scaled to fit the slot and re-encoded, and every inline mip is regenerated,
/// so the object occupies exactly the bytes it did before. That is what allows
/// the package to be written without moving anything else in it.
/// <para>
/// Textures whose pixels live in the shared cache are refused rather than
/// half-handled: writing them inline would leave the package pointing at cache
/// bytes that no longer match, which shows up in game as corruption rather than
/// as an error.
/// </para>
/// </remarks>
public static class TextureReplacer
{
    /// <summary>
    /// Checks whether a texture can be replaced, without doing anything.
    /// </summary>
    /// <param name="cookedPath">
    /// Content folder, needed to reach the shared texture cache. When omitted,
    /// textures stored in the cache are refused.
    /// </param>
    public static ReplaceResult CanReplace(Package package, TextureInfo info, string? cookedPath = null)
    {
        if (info.IsCacheBacked)
        {
            if (cookedPath is null)
            {
                return ReplaceResult.Refuse(
                    ReplaceRefusal.PixelsAreInTextureCache,
                    $"'{info.Name}' keeps its pixels in the shared texture cache " +
                    $"('{info.TextureCacheName}').");
            }

            return CanReplaceCached(info, cookedPath);
        }

        if (!BlockEncoder.CanEncode(info.Format))
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.FormatNotSupported,
                $"'{info.Name}' is {info.FormatName}, which cannot be written yet.");
        }

        PropertyBag? properties = package.TryReadProperties(info.ExportIndex);
        if (properties is null)
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.PropertiesUnreadable,
                $"'{info.Name}' has properties that could not be read.");
        }

        TextureMipChain? chain = TextureMipChain.TryRead(package, info.ExportIndex, properties);
        if (chain is null || !chain.Mips.Any(m => m.IsInline))
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.NoInlineMips,
                $"'{info.Name}' has no pixel data stored in its package.");
        }

        return ReplaceResult.Ok($"'{info.Name}' can be replaced.");
    }

    /// <summary>
    /// Checks a texture whose pixels live in the shared cache.
    /// </summary>
    /// <remarks>
    /// Fitting cannot be known without encoding and compressing, because it
    /// depends on the replacement image. This reports what can be checked cheaply
    /// — the format, and that the cache and its manifest entry exist — and leaves
    /// the size question to the write itself, which refuses loudly.
    /// </remarks>
    private static ReplaceResult CanReplaceCached(TextureInfo info, string cookedPath)
    {
        if (!BlockEncoder.CanEncode(info.Format))
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.FormatNotSupported,
                $"'{info.Name}' is {info.FormatName}, which cannot be written yet.");
        }

        TextureCacheManifest? manifest = TextureCacheManifest.TryLoad(cookedPath);
        CachedTextureEntry? entry = manifest?.Find(info.ObjectPath);

        if (entry?.LargestMip is not { } slot || slot.Size <= 0)
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.NoInlineMips,
                $"'{info.Name}' has no entry in the texture cache manifest, so there is nowhere to write it.");
        }

        return ReplaceResult.Ok(
            $"'{info.Name}' can be replaced. Its cache slot holds {slot.Size:N0} bytes; " +
            "the new image has to compress into that.");
    }

    /// <summary>
    /// Replaces a texture whose pixels live in the shared cache.
    /// </summary>
    public static async Task<ReplaceResult> ReplaceCachedAsync(
        TextureInfo info,
        string cookedPath,
        ReadOnlyMemory<byte> rgba,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken = default)
    {
        ReplaceResult check = CanReplaceCached(info, cookedPath);
        if (!check.Succeeded) return check;

        TextureCacheManifest manifest = TextureCacheManifest.TryLoad(cookedPath)!;
        CachedMipLocation slot = manifest.Find(info.ObjectPath)!.LargestMip!.Value;

        try
        {
            // The cached mip is the texture's full size, so the replacement is
            // scaled to the dimensions the properties declare.
            byte[] fitted = BlockEncoder.ResizeToFit(
                rgba.Span, sourceWidth, sourceHeight, info.Width, info.Height);

            byte[] encoded = BlockEncoder.Encode(fitted, info.Format, info.Width, info.Height);

            var writer = new TextureCacheWriter(cookedPath);
            CacheWriteResult result = await writer
                .WriteAsync(info.TextureCacheName, slot.Offset, slot.Size, encoded, cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded
                ? ReplaceResult.Ok($"Replaced '{info.Name}'. {result.Message}")
                : new ReplaceResult(false, result.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new ReplaceResult(false, $"Could not replace '{info.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the patched bytes for a texture export whose pixels are replaced by
    /// <paramref name="rgba"/>.
    /// </summary>
    /// <param name="rgba">Replacement image, 8-bit RGBA.</param>
    /// <param name="sourceWidth">Width of that image.</param>
    /// <param name="sourceHeight">Height of that image.</param>
    /// <returns>The new export bytes, the same length as the original.</returns>
    public static byte[] BuildPatchedExport(
        Package package, TextureInfo info, ReadOnlySpan<byte> rgba, int sourceWidth, int sourceHeight)
    {
        PropertyBag properties = package.TryReadProperties(info.ExportIndex)
            ?? throw new InvalidOperationException($"'{info.Name}' has unreadable properties.");

        TextureMipChain chain = TextureMipChain.Read(package, info.ExportIndex, properties);

        byte[] patched = package.GetExportData(info.ExportIndex).ToArray();

        // Scale once to the top mip's size, then halve repeatedly. Re-scaling from
        // the source for every mip would produce a chain that shimmers when the
        // engine switches between levels.
        byte[] current = BlockEncoder.ResizeToFit(rgba, sourceWidth, sourceHeight, info.Width, info.Height);
        int currentWidth = info.Width;
        int currentHeight = info.Height;

        foreach (TextureMipMap mip in chain.Mips.OrderByDescending(m => (long)m.Width * m.Height))
        {
            // Step down until the working image matches this mip.
            while (currentWidth > mip.Width || currentHeight > mip.Height)
            {
                current = BlockEncoder.Downsample(current, currentWidth, currentHeight);
                currentWidth = Math.Max(1, currentWidth / 2);
                currentHeight = Math.Max(1, currentHeight / 2);
            }

            if (!mip.IsInline) continue;

            byte[] encoded = BlockEncoder.Encode(current, info.Format, mip.Width, mip.Height);

            if (encoded.Length != mip.Data.SizeOnDisk)
            {
                throw new InvalidOperationException(
                    $"Encoding mip {mip.Width}x{mip.Height} produced {encoded.Length} bytes but the " +
                    $"package reserves {mip.Data.SizeOnDisk}.");
            }

            encoded.CopyTo(patched.AsSpan(mip.Data.InlineDataOffset, encoded.Length));
        }

        return patched;
    }

    /// <summary>
    /// Replaces a texture and saves the package, taking a backup and swapping the
    /// file in atomically.
    /// </summary>
    public static async Task<ReplaceResult> ReplaceAsync(
        Package package,
        TextureInfo info,
        ReadOnlyMemory<byte> rgba,
        int sourceWidth,
        int sourceHeight,
        string? cookedPath = null,
        CancellationToken cancellationToken = default)
    {
        // Pixels in the shared cache take an entirely different write path.
        if (info.IsCacheBacked)
        {
            return cookedPath is null
                ? ReplaceResult.Refuse(
                    ReplaceRefusal.PixelsAreInTextureCache,
                    $"'{info.Name}' keeps its pixels in the shared texture cache.")
                : await ReplaceCachedAsync(info, cookedPath, rgba, sourceWidth, sourceHeight, cancellationToken)
                    .ConfigureAwait(false);
        }

        ReplaceResult check = CanReplace(package, info, cookedPath);
        if (!check.Succeeded) return check;

        try
        {
            byte[] patched = BuildPatchedExport(package, info, rgba.Span, sourceWidth, sourceHeight);

            string backup = await PackageWriter
                .SaveAsync(package, [new ExportPatch(info.ExportIndex, patched)], cancellationToken)
                .ConfigureAwait(false);

            return ReplaceResult.Ok(
                $"Replaced '{info.Name}'. The original was backed up to {Path.GetFileName(backup)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidPackageException or IOException)
        {
            return new ReplaceResult(false, $"Could not replace '{info.Name}': {ex.Message}");
        }
    }
}
