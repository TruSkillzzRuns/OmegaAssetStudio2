using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>One mip level of a texture.</summary>
public sealed record TextureMipMap
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BulkData Data { get; init; }

    /// <summary>True when this mip's pixels are inside the package.</summary>
    public bool IsInline => Data.IsInline;

    /// <summary>True when this mip's pixels are in a shared texture cache.</summary>
    public bool IsExternal => !Data.IsInline && Data.ByteCount > 0;

    /// <summary>Bytes this mip occupies, wherever it is stored.</summary>
    public int ByteCount => Data.ByteCount;

    /// <summary>Bytes this mip should occupy for a given format.</summary>
    public int ExpectedByteSize(PixelFormat format) => format.MipByteSize(Width, Height);

    public override string ToString() =>
        $"{Width}x{Height} {(IsInline ? "inline" : "external")} {ByteCount} bytes";
}

/// <summary>
/// A texture's mip chain, read from the binary payload that follows its
/// properties.
/// </summary>
/// <remarks>
/// Layout derived from real textures and confirmed arithmetically. After the
/// property block comes an empty bulk-data block left over from the uncooked
/// source image, then the mip count, then each mip as a bulk-data block followed
/// by its own width and height.
/// <para>
/// The confirming check on a 40x40 DXT1 icon: the first mip's recorded size is
/// 800 bytes, which is exactly 10x10 blocks at 8 bytes each. That the declared
/// size independently matches the size computed from the format and dimensions
/// is what makes this layout trustworthy rather than merely plausible — and it
/// is asserted across thousands of real textures in the tests.
/// </para>
/// </remarks>
public sealed class TextureMipChain
{
    private TextureMipChain(IReadOnlyList<TextureMipMap> mips) => Mips = mips;

    public IReadOnlyList<TextureMipMap> Mips { get; }

    /// <summary>The largest mip whose pixels are inside the package, if any.</summary>
    public TextureMipMap? LargestInlineMip => Mips
        .Where(m => m.IsInline && m.Data.SizeOnDisk > 0)
        .OrderByDescending(m => (long)m.Width * m.Height)
        .FirstOrDefault();

    /// <summary>Guards against a corrupt count producing an unbounded read.</summary>
    private const int MaxMips = 32;

    /// <summary>
    /// Reads the mip chain of a texture export.
    /// </summary>
    public static TextureMipChain Read(Package package, int exportIndex, PropertyBag properties)
    {
        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
        int absoluteBase = package.Exports[exportIndex].SerialOffset;

        var cursor = new PackageCursor(data, properties.PayloadOffset);

        // Leftover source-image block. Empty in cooked content, but it is still
        // written, and skipping it is what puts the mip count at the right place.
        BulkData.Read(ref cursor, absoluteBase, "source image");

        int mipCount = cursor.ReadInt32("mip count");
        if (mipCount < 0 || mipCount > MaxMips)
            throw new InvalidPackageException($"Texture declares {mipCount} mips.");

        var mips = new TextureMipMap[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            BulkData bulk = BulkData.Read(ref cursor, absoluteBase, $"mip {i}");
            int width = cursor.ReadInt32($"mip {i} width");
            int height = cursor.ReadInt32($"mip {i} height");

            if (width <= 0 || height <= 0)
                throw new InvalidPackageException($"Mip {i} declares dimensions {width}x{height}.");

            mips[i] = new TextureMipMap { Width = width, Height = height, Data = bulk };
        }

        return new TextureMipChain(mips);
    }

    /// <summary>
    /// Reads the mip chain, returning null rather than throwing when the payload
    /// does not match the expected shape.
    /// </summary>
    public static TextureMipChain? TryRead(Package package, int exportIndex, PropertyBag properties)
    {
        try
        {
            return Read(package, exportIndex, properties);
        }
        catch (InvalidPackageException)
        {
            return null;
        }
    }
}
