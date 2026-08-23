using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// What a texture declares about itself: where it lives, how big it is, and how
/// its pixels are stored.
/// </summary>
/// <remarks>
/// Read from the texture's tagged properties. This describes the texture; it does
/// not decode its pixels.
/// </remarks>
public sealed record TextureInfo
{
    /// <summary>Package file the texture lives in.</summary>
    public required string PackagePath { get; init; }

    /// <summary>Export index within that package.</summary>
    public required int ExportIndex { get; init; }

    /// <summary>Object name, without its containing path.</summary>
    public required string Name { get; init; }

    /// <summary>Full dotted object path.</summary>
    public required string ObjectPath { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Size before any cooking-time downscale.</summary>
    public required int OriginalWidth { get; init; }
    public required int OriginalHeight { get; init; }

    public required PixelFormat Format { get; init; }

    /// <summary>The engine's own format name, kept for display and diagnosis.</summary>
    public required string FormatName { get; init; }

    /// <summary>Whether the texture is treated as colour rather than data.</summary>
    public required bool IsSrgb { get; init; }

    /// <summary>Whether the engine keeps this resident rather than streaming it.</summary>
    public required bool NeverStream { get; init; }

    /// <summary>Streaming group, which drives how the engine budgets it.</summary>
    public required string LodGroup { get; init; }

    /// <summary>
    /// Name of the texture cache this texture's pixels are stored in, or empty
    /// when they are stored inside the package.
    /// </summary>
    public required string TextureCacheName { get; init; }

    /// <summary>
    /// True when the pixel data lives in a shared texture cache file rather than
    /// in the package. These need a different read and write path entirely, so
    /// the distinction matters before anything touches the pixels.
    /// </summary>
    public bool IsCacheBacked => TextureCacheName.Length > 0;

    /// <summary>Bytes the top mip occupies, given the format and dimensions.</summary>
    public int TopMipByteSize => Format.MipByteSize(Width, Height);

    public string Dimensions => $"{Width} x {Height}";

    /// <summary>
    /// Reads a texture export. Returns null when the export is not a texture or
    /// its properties do not parse.
    /// </summary>
    public static TextureInfo? TryRead(Package package, int exportIndex)
    {
        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        int width = properties.GetInt("SizeX");
        int height = properties.GetInt("SizeY");
        if (width <= 0 || height <= 0) return null;

        string formatName = properties.GetName("Format", "PF_A8R8G8B8");

        return new TextureInfo
        {
            PackagePath = package.Path,
            ExportIndex = exportIndex,
            Name = package.GetExportName(exportIndex),
            ObjectPath = package.GetExportPath(exportIndex),
            Width = width,
            Height = height,
            OriginalWidth = properties.GetInt("OriginalSizeX", width),
            OriginalHeight = properties.GetInt("OriginalSizeY", height),
            Format = PixelFormatExtensions.Parse(formatName),
            FormatName = formatName,
            // Absent means true. Counted across 120 character packages: the
            // property is written 299 times and every single one of them says
            // false - 191 of the 192 normal maps, and the masks - while 196 of
            // the 197 surface-colour textures do not write it at all. Cooked
            // content only stores what differs from the default, so the default
            // is true, and reading a missing property as false made every
            // costume's colour arrive as though it were raw numbers.
            IsSrgb = properties.GetBool("SRGB", fallback: true),
            NeverStream = properties.GetBool("NeverStream"),
            LodGroup = properties.GetName("LODGroup"),
            TextureCacheName = properties.GetName("TextureFileCacheName"),
        };
    }

    public override string ToString() => $"{Name} ({Dimensions}, {FormatName})";
}
