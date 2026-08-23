namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Decodes block-compressed pixel data to straight RGBA.
/// </summary>
/// <remarks>
/// These formats store 4x4 pixel blocks. DXT1 keeps two reference colours and a
/// two-bit index per pixel; DXT3 adds four-bit alpha per pixel; DXT5 adds two
/// reference alphas and a three-bit index per pixel. Output is always 8-bit RGBA
/// so everything downstream deals with one representation.
/// </remarks>
public static class BlockDecoder
{
    private const int BlockDimension = 4;

    /// <summary>
    /// Decodes a mip to RGBA. Returns width*height*4 bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The format is not supported here.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> source, PixelFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid dimensions {width}x{height}.");

        int required = format.MipByteSize(width, height);
        if (required > 0 && source.Length < required)
            throw new ArgumentException(
                $"Decoding {width}x{height} {format} needs {required} bytes but only {source.Length} were given.");

        byte[] rgba = new byte[width * height * 4];

        switch (format)
        {
            case PixelFormat.Dxt1: DecodeDxt1(source, rgba, width, height); break;
            case PixelFormat.Dxt3: DecodeDxt3(source, rgba, width, height); break;
            case PixelFormat.Dxt5: DecodeDxt5(source, rgba, width, height); break;
            case PixelFormat.A8R8G8B8: DecodeBgra(source, rgba, width, height); break;
            case PixelFormat.G8: DecodeGrey(source, rgba, width, height); break;
            default:
                throw new ArgumentException($"No decoder for {format}.");
        }

        return rgba;
    }

    /// <summary>True when <see cref="Decode"/> supports this format.</summary>
    public static bool CanDecode(PixelFormat format) => format is
        PixelFormat.Dxt1 or PixelFormat.Dxt3 or PixelFormat.Dxt5 or
        PixelFormat.A8R8G8B8 or PixelFormat.G8;

    /// <summary>Expands a 16-bit 5:6:5 colour to 8-bit components.</summary>
    private static (byte R, byte G, byte B) Unpack565(ushort packed)
    {
        int r = (packed >> 11) & 0x1F;
        int g = (packed >> 5) & 0x3F;
        int b = packed & 0x1F;

        // Replicate the high bits into the low ones so full-scale input maps to
        // full-scale output. A plain shift would cap white at 248.
        return (
            (byte)((r << 3) | (r >> 2)),
            (byte)((g << 2) | (g >> 4)),
            (byte)((b << 3) | (b >> 2)));
    }

    /// <summary>Builds the four-entry colour table shared by all DXT variants.</summary>
    private static void BuildColourTable(
        ReadOnlySpan<byte> block, Span<byte> table, bool allowTransparentBlack)
    {
        ushort c0 = BitConverter.ToUInt16(block[..2]);
        ushort c1 = BitConverter.ToUInt16(block.Slice(2, 2));

        (byte r0, byte g0, byte b0) = Unpack565(c0);
        (byte r1, byte g1, byte b1) = Unpack565(c1);

        table[0] = r0; table[1] = g0; table[2] = b0; table[3] = 255;
        table[4] = r1; table[5] = g1; table[6] = b1; table[7] = 255;

        // When the first colour is not greater than the second, DXT1 switches to
        // a three-colour mode whose fourth entry is transparent black. DXT3 and
        // DXT5 always use four opaque colours because they carry alpha
        // separately.
        if (allowTransparentBlack && c0 <= c1)
        {
            table[8] = (byte)((r0 + r1) / 2);
            table[9] = (byte)((g0 + g1) / 2);
            table[10] = (byte)((b0 + b1) / 2);
            table[11] = 255;

            table[12] = 0; table[13] = 0; table[14] = 0; table[15] = 0;
        }
        else
        {
            table[8] = (byte)((2 * r0 + r1) / 3);
            table[9] = (byte)((2 * g0 + g1) / 3);
            table[10] = (byte)((2 * b0 + b1) / 3);
            table[11] = 255;

            table[12] = (byte)((r0 + 2 * r1) / 3);
            table[13] = (byte)((g0 + 2 * g1) / 3);
            table[14] = (byte)((b0 + 2 * b1) / 3);
            table[15] = 255;
        }
    }

    /// <summary>Writes one 4x4 colour block, clipping at the image edges.</summary>
    private static void WriteColourBlock(
        ReadOnlySpan<byte> block, Span<byte> table, Span<byte> rgba,
        int width, int height, int blockX, int blockY, bool keepBlockAlpha)
    {
        uint indices = BitConverter.ToUInt32(block.Slice(4, 4));

        for (int y = 0; y < BlockDimension; y++)
        {
            int pixelY = blockY + y;
            if (pixelY >= height) break;

            for (int x = 0; x < BlockDimension; x++)
            {
                int pixelX = blockX + x;
                if (pixelX >= width) continue;

                int selector = (int)((indices >> (2 * (4 * y + x))) & 0x3);
                int entry = selector * 4;
                int target = ((pixelY * width) + pixelX) * 4;

                rgba[target] = table[entry];
                rgba[target + 1] = table[entry + 1];
                rgba[target + 2] = table[entry + 2];
                if (keepBlockAlpha) rgba[target + 3] = table[entry + 3];
            }
        }
    }

    private static void DecodeDxt1(ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height)
    {
        Span<byte> table = stackalloc byte[16];
        int offset = 0;

        for (int blockY = 0; blockY < height; blockY += BlockDimension)
        {
            for (int blockX = 0; blockX < width; blockX += BlockDimension)
            {
                ReadOnlySpan<byte> block = source.Slice(offset, 8);
                BuildColourTable(block, table, allowTransparentBlack: true);
                WriteColourBlock(block, table, rgba, width, height, blockX, blockY, keepBlockAlpha: true);
                offset += 8;
            }
        }
    }

    private static void DecodeDxt3(ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height)
    {
        Span<byte> table = stackalloc byte[16];
        int offset = 0;

        for (int blockY = 0; blockY < height; blockY += BlockDimension)
        {
            for (int blockX = 0; blockX < width; blockX += BlockDimension)
            {
                ReadOnlySpan<byte> alphaBlock = source.Slice(offset, 8);
                ReadOnlySpan<byte> colourBlock = source.Slice(offset + 8, 8);

                BuildColourTable(colourBlock, table, allowTransparentBlack: false);
                WriteColourBlock(colourBlock, table, rgba, width, height, blockX, blockY, keepBlockAlpha: false);

                // Four bits of alpha per pixel, two pixels per byte.
                for (int y = 0; y < BlockDimension; y++)
                {
                    int pixelY = blockY + y;
                    if (pixelY >= height) break;

                    for (int x = 0; x < BlockDimension; x++)
                    {
                        int pixelX = blockX + x;
                        if (pixelX >= width) continue;

                        int bitIndex = (4 * y) + x;
                        byte packed = alphaBlock[bitIndex / 2];
                        int nibble = (bitIndex & 1) == 0 ? packed & 0x0F : packed >> 4;

                        rgba[(((pixelY * width) + pixelX) * 4) + 3] = (byte)(nibble * 17);
                    }
                }

                offset += 16;
            }
        }
    }

    private static void DecodeDxt5(ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height)
    {
        Span<byte> table = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        int offset = 0;

        for (int blockY = 0; blockY < height; blockY += BlockDimension)
        {
            for (int blockX = 0; blockX < width; blockX += BlockDimension)
            {
                ReadOnlySpan<byte> alphaBlock = source.Slice(offset, 8);
                ReadOnlySpan<byte> colourBlock = source.Slice(offset + 8, 8);

                BuildColourTable(colourBlock, table, allowTransparentBlack: false);
                WriteColourBlock(colourBlock, table, rgba, width, height, blockX, blockY, keepBlockAlpha: false);

                alphas[0] = alphaBlock[0];
                alphas[1] = alphaBlock[1];

                // Six interpolated alphas when the first endpoint is greater,
                // otherwise four interpolated plus explicit zero and full.
                if (alphas[0] > alphas[1])
                {
                    for (int i = 1; i < 7; i++)
                        alphas[i + 1] = (byte)((((7 - i) * alphas[0]) + (i * alphas[1])) / 7);
                }
                else
                {
                    for (int i = 1; i < 5; i++)
                        alphas[i + 1] = (byte)((((5 - i) * alphas[0]) + (i * alphas[1])) / 5);
                    alphas[6] = 0;
                    alphas[7] = 255;
                }

                // Three-bit indices packed across six bytes.
                ulong indices = 0;
                for (int i = 0; i < 6; i++)
                    indices |= (ulong)alphaBlock[2 + i] << (8 * i);

                for (int y = 0; y < BlockDimension; y++)
                {
                    int pixelY = blockY + y;
                    if (pixelY >= height) break;

                    for (int x = 0; x < BlockDimension; x++)
                    {
                        int pixelX = blockX + x;
                        if (pixelX >= width) continue;

                        int selector = (int)((indices >> (3 * ((4 * y) + x))) & 0x7);
                        rgba[(((pixelY * width) + pixelX) * 4) + 3] = alphas[selector];
                    }
                }

                offset += 16;
            }
        }
    }

    /// <summary>
    /// Copies 32-bit pixels, swapping channel order. The engine stores these
    /// blue-first; everything downstream expects red-first.
    /// </summary>
    private static void DecodeBgra(ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height)
    {
        for (int i = 0; i < width * height; i++)
        {
            int from = i * 4;
            int to = i * 4;
            rgba[to] = source[from + 2];
            rgba[to + 1] = source[from + 1];
            rgba[to + 2] = source[from];
            rgba[to + 3] = source[from + 3];
        }
    }

    /// <summary>Expands single-channel greyscale to opaque RGBA.</summary>
    private static void DecodeGrey(ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height)
    {
        for (int i = 0; i < width * height; i++)
        {
            byte value = source[i];
            int to = i * 4;
            rgba[to] = value;
            rgba[to + 1] = value;
            rgba[to + 2] = value;
            rgba[to + 3] = 255;
        }
    }
}
