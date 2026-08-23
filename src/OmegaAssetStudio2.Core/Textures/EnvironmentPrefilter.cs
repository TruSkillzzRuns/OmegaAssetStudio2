using System.Numerics;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>
/// Blurs a reflected panorama the way a rough surface actually blurs it.
/// </summary>
/// <remarks>
/// A rough surface does not reflect one point of its surroundings; it reflects
/// a spread of them, wider the rougher it is. The usual shortcut is to let the
/// graphics card halve the picture a few times and read a smaller copy, which
/// is what this used to do, but that is the wrong average on a panorama: the
/// card weighs every pixel the same, while a row near the top of a panorama
/// covers a sliver of the sky and a row across the middle covers a band of it.
/// <para>
/// Measured against three of the game's own panoramas, the two answers differ
/// by as much as 67 of 255 - a quarter of the range, and plainly visible on
/// anything polished. So each level is worked out properly instead: every pixel
/// of the panorama weighed by how much of the surroundings it covers and by how
/// far it is from where the surface is looking.
/// </para>
/// </remarks>
public static class EnvironmentPrefilter
{
    /// <summary>
    /// How much of the panorama to read when working out a level. The answer is
    /// a blur, so reading a reduced copy changes it very little and keeps the
    /// work in the thousands rather than the billions.
    /// </summary>
    private const int MostWidthToReadFrom = 64;

    /// <summary>
    /// The smallest a level may get. Below this it can no longer say which way
    /// the surroundings lie, only how bright they are on average.
    /// </summary>
    private const int NarrowestLevel = 16;

    /// <summary>
    /// The narrowest and widest spread a surface here can ask for.
    /// </summary>
    /// <remarks>
    /// Not chosen here. These are the same two numbers the shader already turns
    /// a material's specpow channel into for its highlight, so a surface
    /// reflects over the same spread that it shines over. Picking a second,
    /// different curve for the reflection would have been inventing a
    /// relationship the material never states.
    /// </remarks>
    public const float NarrowestSpread = 90f;
    public const float WidestSpread = 12f;

    /// <summary>
    /// Builds the levels a surface reads as its shine widens: the first is the
    /// panorama itself, a mirror, and each after it is spread further.
    /// </summary>
    public static IReadOnlyList<TextureImage> Build(TextureImage panorama)
    {
        ArgumentNullException.ThrowIfNull(panorama);

        if (panorama.Width <= 0 || panorama.Height <= 0) return [panorama];

        // The same handful of panoramas are reflected by hundreds of costumes -
        // one of them by 198 surfaces - and working the levels out takes most of
        // a second for the larger ones. Doing that again for every costume
        // would be most of a second added to every model opened, for an answer
        // that never changes, so each panorama is worked out once.
        long mark = Fingerprint(panorama);

        lock (Built)
        {
            if (Built.TryGetValue(mark, out IReadOnlyList<TextureImage>? already)) return already;
        }

        var levels = new List<TextureImage> { panorama };

        // Every level after the first is half the size, because that is the run
        // of sizes a graphics card expects - but the run stops well before a
        // single pixel.
        //
        // A level has to be able to hold the spread it stands for. Checked
        // against the answer worked out at full size, a level 64 across is
        // within 2 of 255 and one 32 across within 4, but by 8 across it is out
        // by 12 and by 2 across it is out by 50: two pixels cannot describe
        // which way anything is. So the chain ends while its levels still mean
        // something, and the widest spread lives on the last of them.
        int count = 1;
        for (int w = panorama.Width, h = panorama.Height; w > NarrowestLevel && h > 1; count++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        TextureImage source = Reduce(panorama, MostWidthToReadFrom);

        for (int level = 1; level < count; level++)
        {
            int width = Math.Max(1, panorama.Width >> level);
            int height = Math.Max(1, panorama.Height >> level);

            // The first level is the panorama itself, so the spreads run from
            // the narrowest at the second level to the widest at the last.
            float across = count > 2 ? (level - 1) / (float)(count - 2) : 0f;
            float spread = NarrowestSpread + ((WidestSpread - NarrowestSpread) * across);

            levels.Add(Blur(source, width, height, spread));
        }

        lock (Built)
        {
            // Bounded, because a session can open a great many costumes and
            // there is no sense holding every panorama any of them ever used.
            if (Built.Count >= MostToRemember) Built.Clear();

            Built[mark] = levels;
        }

        return levels;
    }

    /// <summary>Panoramas already worked out, by what they contain.</summary>
    private static readonly Dictionary<long, IReadOnlyList<TextureImage>> Built = [];

    private const int MostToRemember = 64;

    /// <summary>
    /// Something that tells one panorama from another cheaply. Size and a walk
    /// across the pixels: two different panoramas of the same size would have
    /// to agree at every step sampled to collide, and the cost of a collision
    /// is a wrong reflection rather than anything unsafe.
    /// </summary>
    private static long Fingerprint(TextureImage image)
    {
        long mark = ((long)image.Width << 40) ^ ((long)image.Height << 20) ^ image.Rgba.Length;

        int step = Math.Max(4, image.Rgba.Length / 4096);

        for (int i = 0; i < image.Rgba.Length; i += step)
        {
            mark = (mark * 31) + image.Rgba[i];
        }

        return mark;
    }

    /// <summary>
    /// One level: for every direction it holds, the average of everything the
    /// surroundings show within the spread that roughness gives.
    /// </summary>
    private static TextureImage Blur(TextureImage source, int width, int height, float spread)
    {

        // Where each pixel of the panorama being read from points, and how much
        // of the surroundings it covers. Worked out once for the whole level.
        var towards = new Vector3[source.Width * source.Height];
        var covers = new float[source.Width * source.Height];

        for (int y = 0; y < source.Height; y++)
        {
            float down = ((y + 0.5f) / source.Height) * MathF.PI;
            float share = MathF.Sin(down);

            for (int x = 0; x < source.Width; x++)
            {
                float round = (((x + 0.5f) / source.Width) - 0.5f) * MathF.Tau;
                int at = (y * source.Width) + x;

                towards[at] = new Vector3(
                    MathF.Sin(down) * MathF.Cos(round),
                    MathF.Sin(down) * MathF.Sin(round),
                    MathF.Cos(down));

                covers[at] = share;
            }
        }

        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            float down = ((y + 0.5f) / height) * MathF.PI;

            for (int x = 0; x < width; x++)
            {
                float round = (((x + 0.5f) / width) - 0.5f) * MathF.Tau;

                var looking = new Vector3(
                    MathF.Sin(down) * MathF.Cos(round),
                    MathF.Sin(down) * MathF.Sin(round),
                    MathF.Cos(down));

                Vector3 total = Vector3.Zero;
                float weight = 0f;

                for (int i = 0; i < towards.Length; i++)
                {
                    float facing = Vector3.Dot(towards[i], looking);
                    if (facing <= 0f) continue;

                    float share = MathF.Pow(facing, spread) * covers[i];
                    if (share <= 0f) continue;

                    int from = i * 4;

                    total += new Vector3(
                        source.Rgba[from], source.Rgba[from + 1], source.Rgba[from + 2]) * share;

                    weight += share;
                }

                Vector3 colour = weight > 0f ? total / weight : Vector3.Zero;
                int to = ((y * width) + x) * 4;

                pixels[to + 0] = (byte)Math.Clamp((int)MathF.Round(colour.X), 0, 255);
                pixels[to + 1] = (byte)Math.Clamp((int)MathF.Round(colour.Y), 0, 255);
                pixels[to + 2] = (byte)Math.Clamp((int)MathF.Round(colour.Z), 0, 255);
                pixels[to + 3] = 255;
            }
        }

        return new TextureImage(width, height, pixels);
    }

    /// <summary>A smaller copy, for reading from rather than for showing.</summary>
    private static TextureImage Reduce(TextureImage image, int mostWidth)
    {
        if (image.Width <= mostWidth) return image;

        int width = mostWidth;
        int height = Math.Max(1, image.Height * mostWidth / image.Width);

        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp(x * image.Width / width, 0, image.Width - 1);
                int sy = Math.Clamp(y * image.Height / height, 0, image.Height - 1);

                int from = ((sy * image.Width) + sx) * 4;
                int to = ((y * width) + x) * 4;

                pixels[to + 0] = image.Rgba[from + 0];
                pixels[to + 1] = image.Rgba[from + 1];
                pixels[to + 2] = image.Rgba[from + 2];
                pixels[to + 3] = 255;
            }
        }

        return new TextureImage(width, height, pixels);
    }
}
