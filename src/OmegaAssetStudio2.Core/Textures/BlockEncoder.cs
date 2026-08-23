namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Encodes straight RGBA into the block-compressed formats the game uses.
/// </summary>
/// <remarks>
/// Each 4x4 block is fitted by taking the extremes of the colours present and
/// interpolating between them, then picking the nearest of the four resulting
/// entries for every pixel. This is the standard range-fit approach: fast,
/// predictable, and well suited to the flat shapes and hard edges that user
/// interface art is made of.
/// </remarks>
public static class BlockEncoder
{
    private const int BlockDimension = 4;

    /// <summary>True when <see cref="Encode"/> supports this format.</summary>
    public static bool CanEncode(PixelFormat format) => format is
        PixelFormat.Dxt1 or PixelFormat.Dxt5 or PixelFormat.A8R8G8B8 or PixelFormat.G8;

    /// <summary>
    /// Encodes RGBA pixels into <paramref name="format"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The format is unsupported or the input is the wrong size.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, PixelFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid dimensions {width}x{height}.");

        int expected = width * height * 4;
        if (rgba.Length < expected)
            throw new ArgumentException(
                $"Encoding {width}x{height} needs {expected} bytes of RGBA but got {rgba.Length}.");

        byte[] output = new byte[format.MipByteSize(width, height)];

        switch (format)
        {
            case PixelFormat.Dxt1: EncodeDxt1(rgba, output, width, height); break;
            case PixelFormat.Dxt5: EncodeDxt5(rgba, output, width, height); break;
            case PixelFormat.A8R8G8B8: EncodeBgra(rgba, output, width, height); break;
            case PixelFormat.G8: EncodeGrey(rgba, output, width, height); break;
            default:
                throw new ArgumentException($"No encoder for {format}.");
        }

        return output;
    }

    /// <summary>Packs 8-bit components into a 16-bit 5:6:5 value.</summary>
    private static ushort Pack565(byte r, byte g, byte b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    /// <summary>Reads a block's pixels, clamping reads at the image edge.</summary>
    private static void GatherBlock(
        ReadOnlySpan<byte> rgba, int width, int height, int blockX, int blockY, Span<byte> block)
    {
        for (int y = 0; y < BlockDimension; y++)
        {
            int sourceY = Math.Min(blockY + y, height - 1);
            for (int x = 0; x < BlockDimension; x++)
            {
                int sourceX = Math.Min(blockX + x, width - 1);
                int from = ((sourceY * width) + sourceX) * 4;
                int to = ((y * BlockDimension) + x) * 4;

                block[to] = rgba[from];
                block[to + 1] = rgba[from + 1];
                block[to + 2] = rgba[from + 2];
                block[to + 3] = rgba[from + 3];
            }
        }
    }

    /// <summary>
    /// Writes the colour half of a block: two endpoints and sixteen two-bit
    /// indices.
    /// </summary>
    private static void WriteColourHalf(ReadOnlySpan<byte> block, Span<byte> destination, bool allowAlphaCutout)
    {
        byte minR = 255, minG = 255, minB = 255;
        byte maxR = 0, maxG = 0, maxB = 0;
        bool anyTransparent = false;

        for (int i = 0; i < 16; i++)
        {
            byte a = block[(i * 4) + 3];
            if (a < 128) { anyTransparent = true; continue; }

            byte r = block[i * 4], g = block[(i * 4) + 1], b = block[(i * 4) + 2];
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
        }

        // A block that is entirely transparent has no colours to fit.
        if (maxR < minR) { minR = maxR = minG = maxG = minB = maxB = 0; }

        ushort high = Pack565(maxR, maxG, maxB);
        ushort low = Pack565(minR, minG, minB);

        // DXT1 uses the endpoint ordering to signal one-bit alpha: when the first
        // endpoint is NOT greater, the fourth entry means transparent. Force that
        // ordering when the block needs a cutout, and avoid it when it does not.
        bool cutout = allowAlphaCutout && anyTransparent;
        if (cutout == (high > low))
        {
            (high, low) = (low, high);
        }

        BitConverter.GetBytes(high).CopyTo(destination[..2]);
        BitConverter.GetBytes(low).CopyTo(destination.Slice(2, 2));

        // Rebuild the palette exactly as the decoder will see it, so index
        // selection matches what the hardware produces.
        Span<int> paletteR = stackalloc int[4];
        Span<int> paletteG = stackalloc int[4];
        Span<int> paletteB = stackalloc int[4];

        (paletteR[0], paletteG[0], paletteB[0]) = Unpack565(high);
        (paletteR[1], paletteG[1], paletteB[1]) = Unpack565(low);

        if (high > low)
        {
            paletteR[2] = ((2 * paletteR[0]) + paletteR[1]) / 3;
            paletteG[2] = ((2 * paletteG[0]) + paletteG[1]) / 3;
            paletteB[2] = ((2 * paletteB[0]) + paletteB[1]) / 3;

            paletteR[3] = (paletteR[0] + (2 * paletteR[1])) / 3;
            paletteG[3] = (paletteG[0] + (2 * paletteG[1])) / 3;
            paletteB[3] = (paletteB[0] + (2 * paletteB[1])) / 3;
        }
        else
        {
            paletteR[2] = (paletteR[0] + paletteR[1]) / 2;
            paletteG[2] = (paletteG[0] + paletteG[1]) / 2;
            paletteB[2] = (paletteB[0] + paletteB[1]) / 2;

            paletteR[3] = paletteG[3] = paletteB[3] = 0;
        }

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int selector;

            if (cutout && block[(i * 4) + 3] < 128)
            {
                selector = 3;   // the transparent entry
            }
            else
            {
                int r = block[i * 4], g = block[(i * 4) + 1], b = block[(i * 4) + 2];
                int best = 0, bestDistance = int.MaxValue;

                // The fourth entry is transparent in cutout mode, so it is not a
                // candidate for an opaque pixel.
                int candidates = cutout ? 3 : 4;
                for (int c = 0; c < candidates; c++)
                {
                    int dr = r - paletteR[c], dg = g - paletteG[c], db = b - paletteB[c];
                    int distance = (dr * dr) + (dg * dg) + (db * db);
                    if (distance < bestDistance) { bestDistance = distance; best = c; }
                }
                selector = best;
            }

            indices |= (uint)selector << (i * 2);
        }

        BitConverter.GetBytes(indices).CopyTo(destination.Slice(4, 4));
    }

    private static (int R, int G, int B) Unpack565(ushort packed)
    {
        int r = (packed >> 11) & 0x1F;
        int g = (packed >> 5) & 0x3F;
        int b = packed & 0x1F;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    private static void EncodeDxt1(ReadOnlySpan<byte> rgba, Span<byte> output, int width, int height)
    {
        Span<byte> block = stackalloc byte[64];
        int offset = 0;

        for (int y = 0; y < height; y += BlockDimension)
        {
            for (int x = 0; x < width; x += BlockDimension)
            {
                GatherBlock(rgba, width, height, x, y, block);
                WriteColourHalf(block, output.Slice(offset, 8), allowAlphaCutout: true);
                offset += 8;
            }
        }
    }

    private static void EncodeDxt5(ReadOnlySpan<byte> rgba, Span<byte> output, int width, int height)
    {
        // Both buffers are allocated once for the whole image. A stackalloc inside
        // the loop is never released until the method returns, so a large texture
        // would exhaust the thread stack.
        Span<byte> block = stackalloc byte[64];
        Span<int> palette = stackalloc int[8];
        int offset = 0;

        for (int y = 0; y < height; y += BlockDimension)
        {
            for (int x = 0; x < width; x += BlockDimension)
            {
                GatherBlock(rgba, width, height, x, y, block);

                byte minAlpha = 255, maxAlpha = 0;
                for (int i = 0; i < 16; i++)
                {
                    byte a = block[(i * 4) + 3];
                    if (a < minAlpha) minAlpha = a;
                    if (a > maxAlpha) maxAlpha = a;
                }

                Span<byte> alphaHalf = output.Slice(offset, 8);
                alphaHalf[0] = maxAlpha;
                alphaHalf[1] = minAlpha;

                // Eight-value mode: endpoints plus six interpolated steps.
                palette[0] = maxAlpha;
                palette[1] = minAlpha;
                for (int i = 1; i < 7; i++)
                    palette[i + 1] = (((7 - i) * maxAlpha) + (i * minAlpha)) / 7;

                ulong indices = 0;
                for (int i = 0; i < 16; i++)
                {
                    int a = block[(i * 4) + 3];
                    int best = 0, bestDistance = int.MaxValue;
                    for (int c = 0; c < 8; c++)
                    {
                        int distance = Math.Abs(a - palette[c]);
                        if (distance < bestDistance) { bestDistance = distance; best = c; }
                    }
                    indices |= (ulong)best << (i * 3);
                }

                for (int i = 0; i < 6; i++)
                    alphaHalf[2 + i] = (byte)((indices >> (8 * i)) & 0xFF);

                // DXT5 carries alpha separately, so the colour half never uses
                // the cutout encoding.
                WriteColourHalf(block, output.Slice(offset + 8, 8), allowAlphaCutout: false);
                offset += 16;
            }
        }
    }

    /// <summary>Writes 32-bit pixels blue-first, the order the engine stores.</summary>
    private static void EncodeBgra(ReadOnlySpan<byte> rgba, Span<byte> output, int width, int height)
    {
        for (int i = 0; i < width * height; i++)
        {
            output[i * 4] = rgba[(i * 4) + 2];
            output[(i * 4) + 1] = rgba[(i * 4) + 1];
            output[(i * 4) + 2] = rgba[i * 4];
            output[(i * 4) + 3] = rgba[(i * 4) + 3];
        }
    }

    /// <summary>Collapses colour to a single channel using perceptual weights.</summary>
    private static void EncodeGrey(ReadOnlySpan<byte> rgba, Span<byte> output, int width, int height)
    {
        for (int i = 0; i < width * height; i++)
        {
            output[i] = (byte)(((rgba[i * 4] * 77) + (rgba[(i * 4) + 1] * 150) + (rgba[(i * 4) + 2] * 29)) >> 8);
        }
    }

    /// <summary>
    /// Halves an image, averaging each 2x2 group. Used to rebuild a mip chain so
    /// that a replacement occupies exactly the same bytes as what it replaces.
    /// </summary>
    public static byte[] Downsample(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int newWidth = Math.Max(1, width / 2);
        int newHeight = Math.Max(1, height / 2);
        byte[] output = new byte[newWidth * newHeight * 4];

        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    int total = 0, samples = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int sourceY = (y * 2) + dy;
                        if (sourceY >= height) continue;

                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sourceX = (x * 2) + dx;
                            if (sourceX >= width) continue;

                            total += rgba[((((sourceY * width) + sourceX) * 4) + channel)];
                            samples++;
                        }
                    }
                    output[((((y * newWidth) + x) * 4) + channel)] = (byte)(total / Math.Max(1, samples));
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Scales an image to fit exactly <paramref name="targetWidth"/> by
    /// <paramref name="targetHeight"/>, preserving aspect ratio and centring the
    /// result on transparent padding.
    /// </summary>
    /// <remarks>
    /// A replacement must land on exactly the slot's dimensions, because anything
    /// else changes the byte count and the package will not accept it. Stretching
    /// to fit would distort art that was authored square; letterboxing keeps the
    /// proportions the author intended.
    /// </remarks>
    public static byte[] ResizeToFit(
        ReadOnlySpan<byte> rgba, int width, int height, int targetWidth, int targetHeight)
    {
        double scale = Math.Min(targetWidth / (double)width, targetHeight / (double)height);
        int drawWidth = Math.Max(1, (int)Math.Round(width * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(height * scale));
        int offsetX = (targetWidth - drawWidth) / 2;
        int offsetY = (targetHeight - drawHeight) / 2;

        byte[] output = new byte[targetWidth * targetHeight * 4];

        for (int y = 0; y < drawHeight; y++)
        {
            int sourceY = Math.Min(height - 1, (int)((y + 0.5) / scale));
            for (int x = 0; x < drawWidth; x++)
            {
                int sourceX = Math.Min(width - 1, (int)((x + 0.5) / scale));
                int from = ((sourceY * width) + sourceX) * 4;
                int to = ((((y + offsetY) * targetWidth) + x + offsetX) * 4);

                output[to] = rgba[from];
                output[to + 1] = rgba[from + 1];
                output[to + 2] = rgba[from + 2];
                output[to + 3] = rgba[from + 3];
            }
        }

        return output;
    }
}
