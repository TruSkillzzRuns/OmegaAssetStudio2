namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Pixel formats seen on textures in the supported game.
/// </summary>
/// <remarks>
/// Named from the engine's own enum values, which appear as the value of a
/// texture's "Format" property.
/// </remarks>
public enum PixelFormat
{
    Unknown = 0,
    A8R8G8B8,
    Dxt1,
    Dxt3,
    Dxt5,
    G8,
    V8U8,
    A1,
    FloatRgb,
    FloatRgba,
    DepthStencil,
    ShadowDepth,
    R32F,
    G16R16,
    G16R16F,
    G32R32F,
    A2B10G10R10,
    D24,
    R16F,
    A16B16G16R16,
    BC7,
}

public static class PixelFormatExtensions
{
    /// <summary>Parses the engine's format name.</summary>
    public static PixelFormat Parse(string? engineName) => engineName?.ToLowerInvariant() switch
    {
        "pf_a8r8g8b8" => PixelFormat.A8R8G8B8,
        "pf_dxt1" => PixelFormat.Dxt1,
        "pf_dxt3" => PixelFormat.Dxt3,
        "pf_dxt5" => PixelFormat.Dxt5,
        "pf_g8" => PixelFormat.G8,
        "pf_v8u8" => PixelFormat.V8U8,
        "pf_a1" => PixelFormat.A1,
        "pf_floatrgb" => PixelFormat.FloatRgb,
        "pf_floatrgba" => PixelFormat.FloatRgba,
        "pf_depthstencil" => PixelFormat.DepthStencil,
        "pf_shadowdepth" => PixelFormat.ShadowDepth,
        "pf_r32f" => PixelFormat.R32F,
        "pf_g16r16" => PixelFormat.G16R16,
        "pf_g16r16f" => PixelFormat.G16R16F,
        "pf_g32r32f" => PixelFormat.G32R32F,
        "pf_a2b10g10r10" => PixelFormat.A2B10G10R10,
        "pf_d24" => PixelFormat.D24,
        "pf_r16f" => PixelFormat.R16F,
        "pf_a16b16g16r16" => PixelFormat.A16B16G16R16,
        "pf_bc7" => PixelFormat.BC7,
        _ => PixelFormat.Unknown,
    };

    /// <summary>True when the format stores 4x4 blocks rather than single pixels.</summary>
    public static bool IsBlockCompressed(this PixelFormat format) =>
        format is PixelFormat.Dxt1 or PixelFormat.Dxt3 or PixelFormat.Dxt5 or PixelFormat.BC7;

    /// <summary>Bytes per 4x4 block, for block-compressed formats.</summary>
    public static int BlockBytes(this PixelFormat format) => format switch
    {
        PixelFormat.Dxt1 => 8,
        PixelFormat.Dxt3 or PixelFormat.Dxt5 or PixelFormat.BC7 => 16,
        _ => 0,
    };

    /// <summary>Bytes per pixel, for uncompressed formats.</summary>
    /// <summary>
    /// How many channels a format actually carries.
    /// </summary>
    /// <remarks>
    /// This matters because a material can name more channels than its texture
    /// has. One chrome-armour mask is bound in 1.52 as
    /// specmultrimmaskreflection and in 1.53 as
    /// specmult_specpow_skinmask_reflectivity - three names and four names for
    /// the byte-identical DXT1 texture, which has no alpha at all. Reading the
    /// fourth name out of a channel that does not exist gives a constant 1.0,
    /// which made every surface fully reflective.
    /// </remarks>
    public static int ChannelCount(this PixelFormat format) => format switch
    {
        PixelFormat.Dxt1 => 3,
        PixelFormat.G8 or PixelFormat.R32F or PixelFormat.R16F => 1,
        PixelFormat.V8U8 or PixelFormat.G16R16 or PixelFormat.G16R16F or PixelFormat.G32R32F => 2,
        PixelFormat.FloatRgb => 3,
        PixelFormat.Unknown => 4,
        _ => 4,
    };

    public static int BytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.A8R8G8B8 => 4,
        PixelFormat.G8 => 1,
        PixelFormat.V8U8 or PixelFormat.G16R16 or PixelFormat.G16R16F or PixelFormat.R16F => 2,
        PixelFormat.R32F or PixelFormat.A2B10G10R10 or PixelFormat.D24 => 4,
        PixelFormat.G32R32F or PixelFormat.A16B16G16R16 => 8,
        PixelFormat.FloatRgba => 8,
        _ => 0,
    };

    /// <summary>
    /// Bytes one mip level occupies. Block formats round up to whole blocks, so a
    /// 2x2 mip of a compressed texture still costs a full block.
    /// </summary>
    public static int MipByteSize(this PixelFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;

        if (format.IsBlockCompressed())
        {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            return blocksWide * blocksHigh * format.BlockBytes();
        }

        int bytesPerPixel = format.BytesPerPixel();
        return bytesPerPixel == 0 ? 0 : width * height * bytesPerPixel;
    }
}
