using System.Buffers.Binary;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Replaces a texture whose pixels live in a shared cache by storing the new
/// pixels in the texture's own package instead.
/// </summary>
/// <remarks>
/// Writing into a shared cache changes a file every package reads from and that
/// nothing but the whole install carries, so the change cannot travel with the
/// package it belongs to. Storing the pixels in the package makes it a single
/// file that holds everything about the texture.
/// <para>
/// The shape written is the one the game itself ships for the same kind of
/// texture. Counted across every icon package: 585 hero pictures and the
/// 300 x 420 store pictures in the main interface package keep their pixels in
/// their package, and each of them differs from a cached one in exactly three
/// ways - no cache name, the never-stream flag set, and each mip held in place
/// with its size and its position in the file rather than flagged as elsewhere.
/// Nothing else in the object changes, so everything else is copied as it is.
/// </para>
/// <para>
/// A mip held in place records its own position measured from the start of the
/// file, and the object grows, so it moves to the end of the package. Those
/// positions are therefore written once it is known where the object lands, and
/// the result is read back and checked before anything is saved.
/// </para>
/// </remarks>
public static class TextureInliner
{
    private const string CacheNameProperty = "TextureFileCacheName";
    private const string NeverStreamProperty = "NeverStream";
    private const string BoolType = "BoolProperty";

    /// <summary>Whether a cached texture can be written into its own package.</summary>
    public static ReplaceResult CanInline(Package package, TextureInfo info)
    {
        if (!info.IsCacheBacked)
            return ReplaceResult.Refuse(ReplaceRefusal.None, $"'{info.Name}' already keeps its pixels in its package.");

        if (!BlockEncoder.CanEncode(info.Format))
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.FormatNotSupported,
                $"'{info.Name}' is {info.FormatName}, which cannot be written yet.");
        }

        PropertyBag? properties = package.TryReadProperties(info.ExportIndex);
        if (properties is null || properties.Find(CacheNameProperty) is null)
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.PropertiesUnreadable,
                $"'{info.Name}' has properties that could not be read.");
        }

        if (TextureMipChain.TryRead(package, info.ExportIndex, properties) is not { Mips.Count: > 0 })
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.PropertiesUnreadable,
                $"'{info.Name}' has a mip list that could not be read.");
        }

        // The flag is added by name, and a name can only be used if the package
        // already lists it: adding one would move everything after the name
        // table.
        if (properties.Find(NeverStreamProperty) is null
            && (package.Names.IndexOf(NeverStreamProperty) < 0 || package.Names.IndexOf(BoolType) < 0))
        {
            return ReplaceResult.Refuse(
                ReplaceRefusal.PropertiesUnreadable,
                $"This package does not name the never-stream flag, so '{info.Name}' cannot be stored in it.");
        }

        return ReplaceResult.Ok(
            $"'{info.Name}' can be replaced. The new picture is stored in this package, so the shared " +
            $"cache '{info.TextureCacheName}' is left as it is.");
    }

    /// <summary>
    /// Builds the new package bytes with the texture's pixels stored inside it.
    /// </summary>
    public static byte[] Build(
        Package package, TextureInfo info, ReadOnlySpan<byte> rgba, int sourceWidth, int sourceHeight)
    {
        PropertyBag properties = package.TryReadProperties(info.ExportIndex)
            ?? throw new InvalidOperationException($"'{info.Name}' has unreadable properties.");

        TextureMipChain chain = TextureMipChain.Read(package, info.ExportIndex, properties);

        IReadOnlyList<byte[]> pixels = Encode(info, chain, rgba, sourceWidth, sourceHeight);

        // Where the object will land is known only once the package is built,
        // and building does not depend on the positions written inside it: the
        // object is the same length either way.
        byte[] trial = PackageRebuilder.Build(
            package, [new ExportPatch(info.ExportIndex, Compose(package, info, properties, chain, pixels, 0))]);

        int at = Package.Read(trial, package.Path).Exports[info.ExportIndex].SerialOffset;

        byte[] built = PackageRebuilder.Build(
            package, [new ExportPatch(info.ExportIndex, Compose(package, info, properties, chain, pixels, at))]);

        Verify(built, package.Path, info, pixels, at);

        return built;
    }

    /// <summary>Replaces the texture and saves the package, backing it up first.</summary>
    public static async Task<ReplaceResult> ReplaceAsync(
        Package package,
        TextureInfo info,
        ReadOnlyMemory<byte> rgba,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken = default)
    {
        ReplaceResult check = CanInline(package, info);
        if (!check.Succeeded) return check;

        try
        {
            byte[] built = Build(package, info, rgba.Span, sourceWidth, sourceHeight);

            string backup = await Workspace.SafeFileWriter
                .WriteAsync(package.Path, built, cancellationToken)
                .ConfigureAwait(false);

            return ReplaceResult.Ok(
                $"Replaced '{info.Name}' and stored it in this package; the shared cache was not touched. " +
                $"The original was backed up to {Path.GetFileName(backup)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidPackageException
                                       or PackageRebuildException or IOException)
        {
            return new ReplaceResult(false, $"Could not replace '{info.Name}': {ex.Message}");
        }
    }

    /// <summary>Encodes every mip of the chain from the one source image.</summary>
    private static IReadOnlyList<byte[]> Encode(
        TextureInfo info, TextureMipChain chain, ReadOnlySpan<byte> rgba, int sourceWidth, int sourceHeight)
    {
        var encoded = new byte[chain.Mips.Count][];

        // Scaled once to the top size, then halved, as the in-package path does.
        byte[] current = BlockEncoder.ResizeToFit(rgba, sourceWidth, sourceHeight, info.Width, info.Height);
        int currentWidth = info.Width;
        int currentHeight = info.Height;

        foreach (int i in Enumerable.Range(0, chain.Mips.Count)
                                    .OrderByDescending(i => (long)chain.Mips[i].Width * chain.Mips[i].Height))
        {
            TextureMipMap mip = chain.Mips[i];

            while (currentWidth > mip.Width || currentHeight > mip.Height)
            {
                current = BlockEncoder.Downsample(current, currentWidth, currentHeight);
                currentWidth = Math.Max(1, currentWidth / 2);
                currentHeight = Math.Max(1, currentHeight / 2);
            }

            encoded[i] = BlockEncoder.Encode(current, info.Format, mip.Width, mip.Height);

            int expected = info.Format.MipByteSize(mip.Width, mip.Height);
            if (encoded[i].Length != expected)
            {
                throw new InvalidOperationException(
                    $"Encoding mip {mip.Width}x{mip.Height} produced {encoded[i].Length} bytes, not {expected}.");
            }
        }

        return encoded;
    }

    /// <summary>
    /// The object's new bytes, with every position inside it measured from
    /// <paramref name="at"/>, where the object will start in the file.
    /// </summary>
    private static byte[] Compose(
        Package package,
        TextureInfo info,
        PropertyBag properties,
        TextureMipChain chain,
        IReadOnlyList<byte[]> pixels,
        int at)
    {
        ReadOnlySpan<byte> data = package.GetExportData(info.ExportIndex);

        var output = new MemoryStream(data.Length + pixels.Sum(p => p.Length) + 64);

        // ---- the properties, less the cache name and with the flag set
        IReadOnlyList<PropertyTag> tags = properties.Tags;

        int firstTag = tags[0].TagOffset;
        int lastTagEnd = tags.Max(t => t.TagOffset + t.TotalSize);

        output.Write(data[..firstTag]);

        PropertyTag? stream = properties.Find(NeverStreamProperty);

        // Where the flag goes if it has to be added: in the engine's own order,
        // after the colour-space flag and before the level-of-detail group, which
        // is where the game's own copies have it.
        int flagBefore = -1;
        if (stream is null)
        {
            flagBefore = tags.Count;
            for (int t = 0; t < tags.Count; t++)
            {
                if (tags[t].Name.Equals("LODGroup", StringComparison.OrdinalIgnoreCase)
                    || tags[t].Name.Equals("MipGenSettings", StringComparison.OrdinalIgnoreCase))
                {
                    flagBefore = t;
                    break;
                }
            }
        }

        for (int t = 0; t < tags.Count; t++)
        {
            if (t == flagBefore) output.Write(NeverStreamTag(package.Names));

            PropertyTag tag = tags[t];

            if (tag.Name.Equals(CacheNameProperty, StringComparison.OrdinalIgnoreCase)) continue;

            byte[] bytes = data.Slice(tag.TagOffset, tag.TotalSize).ToArray();

            // Already present but false: set it.
            if (ReferenceEquals(tag, stream) && tag.Size == 0)
                bytes[tag.ValueOffset - tag.TagOffset] = 1;

            output.Write(bytes);
        }

        if (flagBefore == tags.Count) output.Write(NeverStreamTag(package.Names));

        // The terminator, and anything else before the payload, unchanged.
        output.Write(data[lastTagEnd..properties.PayloadOffset]);

        // ---- the payload
        var cursor = new PackageCursor(data, properties.PayloadOffset);
        int absoluteBase = package.Exports[info.ExportIndex].SerialOffset;

        // The empty source-image block, which records where it sits.
        BulkData.Read(ref cursor, absoluteBase, "source image");
        WriteBulkHeader(output, 0, 0, 0, at + (int)output.Position + (sizeof(int) * 4));

        cursor.ReadInt32("mip count");
        WriteInt(output, chain.Mips.Count);

        for (int i = 0; i < chain.Mips.Count; i++)
        {
            BulkData.Read(ref cursor, absoluteBase, $"mip {i}");
            cursor.ReadInt32($"mip {i} width");
            cursor.ReadInt32($"mip {i} height");

            byte[] mip = pixels[i];

            WriteBulkHeader(output, 0, mip.Length, mip.Length, at + (int)output.Position + (sizeof(int) * 4));
            output.Write(mip);
            WriteInt(output, chain.Mips[i].Width);
            WriteInt(output, chain.Mips[i].Height);
        }

        // Everything after the mips - the identifier and the trailing lists -
        // is the same in the game's cached and in-package copies.
        output.Write(data[cursor.Position..]);

        return output.ToArray();
    }

    private static byte[] NeverStreamTag(NameTable names)
    {
        var tag = new byte[(sizeof(int) * 6) + 1];

        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), names.IndexOf(NeverStreamProperty));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), names.IndexOf(BoolType));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 0); // size
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(20), 0); // array index
        tag[24] = 1;

        return tag;
    }

    private static void WriteBulkHeader(Stream output, uint flags, int elements, int size, int offset)
    {
        Span<byte> header = stackalloc byte[sizeof(int) * 4];

        BinaryPrimitives.WriteUInt32LittleEndian(header, flags);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], elements);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], size);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], offset);

        output.Write(header);
    }

    private static void WriteInt(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    /// <summary>
    /// Reads the texture back out of the built bytes and refuses them unless it
    /// is exactly what was meant.
    /// </summary>
    private static void Verify(byte[] built, string path, TextureInfo before, IReadOnlyList<byte[]> pixels, int at)
    {
        Package staged = Package.Read(built, path);

        if (staged.Exports[before.ExportIndex].SerialOffset != at)
            throw new InvalidOperationException("The rebuilt package did not put the texture where it was expected.");

        TextureInfo after = TextureInfo.TryRead(staged, before.ExportIndex)
            ?? throw new InvalidOperationException("The rebuilt texture could not be read back.");

        if (after.IsCacheBacked || !after.NeverStream
            || after.Width != before.Width || after.Height != before.Height || after.Format != before.Format)
        {
            throw new InvalidOperationException("The rebuilt texture does not read back as intended.");
        }

        PropertyBag properties = staged.TryReadProperties(before.ExportIndex)!;
        TextureMipChain chain = TextureMipChain.Read(staged, before.ExportIndex, properties);
        ReadOnlySpan<byte> data = staged.GetExportData(before.ExportIndex);

        if (chain.Mips.Count != pixels.Count)
            throw new InvalidOperationException("The rebuilt texture has a different number of mips.");

        for (int i = 0; i < chain.Mips.Count; i++)
        {
            BulkData bulk = chain.Mips[i].Data;

            // In place, recording exactly where it is, holding exactly what was encoded.
            if (!bulk.IsInline || bulk.Flags != 0 || bulk.OffsetInFile != at + bulk.InlineDataOffset
                || !data.Slice(bulk.InlineDataOffset, bulk.SizeOnDisk).SequenceEqual(pixels[i]))
            {
                throw new InvalidOperationException($"Mip {i} of the rebuilt texture does not read back as written.");
            }
        }
    }
}
