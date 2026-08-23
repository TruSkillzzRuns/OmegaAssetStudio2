using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OmegaAssetStudio.TexturePreview;

public interface ITextureUpscaleProvider
{
    string Name { get; }

    TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize);
}

public abstract class TextureUpscaleProviderBase : ITextureUpscaleProvider
{
    public abstract string Name { get; }

    public abstract TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize);

    protected static (int Width, int Height) CalculateTargetDimensions(int sourceWidth, int sourceHeight, int targetSize)
    {
        float scale = targetSize / (float)Math.Max(sourceWidth, sourceHeight);
        int width = Math.Max(1, RoundPowerOfTwo((int)MathF.Round(sourceWidth * scale)));
        int height = Math.Max(1, RoundPowerOfTwo((int)MathF.Round(sourceHeight * scale)));
        return (Math.Min(width, targetSize), Math.Min(height, targetSize));
    }

    protected static int RoundPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;

        int lower = 1;
        while ((lower << 1) <= value)
            lower <<= 1;

        int upper = lower << 1;
        return (value - lower) <= (upper - value) ? lower : upper;
    }

    protected static TexturePreviewTexture CreateResult(TexturePreviewTexture source, Bitmap bitmap, byte[] rgba)
    {
        return new TexturePreviewTexture
        {
            Name = source.Name,
            SourcePath = source.SourcePath,
            SourceDescription = source.SourceDescription,
            ExportPath = source.ExportPath,
            Bitmap = bitmap,
            RgbaPixels = rgba,
            Width = bitmap.Width,
            Height = bitmap.Height,
            MipCount = 1,
            SelectedMipIndex = 0,
            Format = source.Format,
            Compression = source.Compression,
            ContainerType = source.ContainerType,
            MipSource = source.MipSource,
            Slot = source.Slot,
            ContainerBytes = source.ContainerBytes,
            AvailableMipLevels = source.AvailableMipLevels
        };
    }

    protected static Bitmap ResizeBitmap(Bitmap source, int targetWidth, int targetHeight, InterpolationMode interpolationMode)
    {
        Bitmap resized = new(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(resized);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = interpolationMode;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        return resized;
    }

    protected static byte[] BitmapToRgba(Bitmap bitmap)
    {
        Bitmap clone = new(bitmap);
        BitmapData data = clone.LockBits(new Rectangle(0, 0, clone.Width, clone.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
            clone.Dispose();
        }
    }

    protected static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = (byte[])rgba.Clone();
            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }

            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
            return bitmap;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    protected static byte[] ResizeWithLanczos(byte[] sourceRgba, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        const int radius = 3;
        KernelSample[] xKernels = BuildKernels(sourceWidth, targetWidth, radius);
        KernelSample[] yKernels = BuildKernels(sourceHeight, targetHeight, radius);
        byte[] output = new byte[targetWidth * targetHeight * 4];

        Parallel.For(0, targetHeight, y =>
        {
            KernelSample yKernel = yKernels[y];
            int outputRow = y * targetWidth * 4;

            for (int x = 0; x < targetWidth; x++)
            {
                KernelSample xKernel = xKernels[x];
                double r = 0;
                double g = 0;
                double b = 0;
                double a = 0;

                for (int yi = 0; yi < yKernel.Indices.Length; yi++)
                {
                    int sourceY = yKernel.Indices[yi];
                    float wy = yKernel.Weights[yi];
                    int sourceRow = sourceY * sourceWidth * 4;

                    for (int xi = 0; xi < xKernel.Indices.Length; xi++)
                    {
                        int sourceX = xKernel.Indices[xi];
                        float weight = wy * xKernel.Weights[xi];
                        int sourceIndex = sourceRow + (sourceX * 4);
                        r += sourceRgba[sourceIndex + 0] * weight;
                        g += sourceRgba[sourceIndex + 1] * weight;
                        b += sourceRgba[sourceIndex + 2] * weight;
                        a += sourceRgba[sourceIndex + 3] * weight;
                    }
                }

                int outputIndex = outputRow + (x * 4);
                output[outputIndex + 0] = ClampToByte(r);
                output[outputIndex + 1] = ClampToByte(g);
                output[outputIndex + 2] = ClampToByte(b);
                output[outputIndex + 3] = ClampToByte(a);
            }
        });

        return output;
    }

    protected static byte[] ApplyEdgeAwareSharpen(byte[] rgba, int width, int height)
    {
        byte[] output = (byte[])rgba.Clone();
        if (width < 3 || height < 3)
            return output;

        static float Luma(byte r, byte g, byte b) => (0.2126f * r) + (0.7152f * g) + (0.0722f * b);

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int index = ((y * width) + x) * 4;
                byte centerR = rgba[index + 0];
                byte centerG = rgba[index + 1];
                byte centerB = rgba[index + 2];
                byte centerA = rgba[index + 3];

                float blurR = 0;
                float blurG = 0;
                float blurB = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int sampleIndex = ((((y + oy) * width) + (x + ox)) * 4);
                        blurR += rgba[sampleIndex + 0];
                        blurG += rgba[sampleIndex + 1];
                        blurB += rgba[sampleIndex + 2];
                    }
                }

                blurR /= 9.0f;
                blurG /= 9.0f;
                blurB /= 9.0f;

                float centerLuma = Luma(centerR, centerG, centerB);
                float edgeLuma = Math.Max(Math.Abs(centerLuma - Luma(rgba[index - 4], rgba[index - 3], rgba[index - 2])),
                    Math.Max(Math.Abs(centerLuma - Luma(rgba[index + 4], rgba[index + 5], rgba[index + 6])),
                        Math.Max(Math.Abs(centerLuma - Luma(rgba[index - (width * 4)], rgba[index - (width * 4) + 1], rgba[index - (width * 4) + 2])),
                                 Math.Abs(centerLuma - Luma(rgba[index + (width * 4)], rgba[index + (width * 4) + 1], rgba[index + (width * 4) + 2])))));
                float edgeFactor = Math.Clamp(edgeLuma / 48.0f, 0.0f, 1.0f);
                float sharpen = 0.18f + (edgeFactor * 0.28f);

                output[index + 0] = ClampToByte(centerR + ((centerR - blurR) * sharpen));
                output[index + 1] = ClampToByte(centerG + ((centerG - blurG) * sharpen));
                output[index + 2] = ClampToByte(centerB + ((centerB - blurB) * sharpen));
                output[index + 3] = centerA;
            }
        }

        return output;
    }

    private sealed record KernelSample(int[] Indices, float[] Weights);

    private static KernelSample[] BuildKernels(int sourceLength, int targetLength, int radius)
    {
        KernelSample[] kernels = new KernelSample[targetLength];
        for (int coordinate = 0; coordinate < targetLength; coordinate++)
        {
            float center = ((coordinate + 0.5f) * sourceLength / targetLength) - 0.5f;
            int start = (int)MathF.Floor(center) - radius + 1;
            int sampleCount = radius * 2;
            int[] indices = new int[sampleCount];
            float[] weights = new float[sampleCount];
            float total = 0.0f;

            for (int i = 0; i < sampleCount; i++)
            {
                int sourceIndex = Clamp(start + i, 0, sourceLength - 1);
                float distance = center - (start + i);
                float weight = LanczosWeight(distance, radius);
                indices[i] = sourceIndex;
                weights[i] = weight;
                total += weight;
            }

            if (MathF.Abs(total) < 1e-6f)
            {
                weights[0] = 1.0f;
                total = 1.0f;
            }

            for (int i = 0; i < weights.Length; i++)
                weights[i] /= total;

            kernels[coordinate] = new KernelSample(indices, weights);
        }

        return kernels;
    }

    private static float LanczosWeight(float distance, int radius)
    {
        float absDistance = MathF.Abs(distance);
        if (absDistance < 1e-6f)
            return 1.0f;

        if (absDistance >= radius)
            return 0.0f;

        float piDistance = MathF.PI * distance;
        float lanczos = MathF.Sin(piDistance) / piDistance;
        float windowDistance = piDistance / radius;
        float window = MathF.Sin(windowDistance) / windowDistance;
        return lanczos * window;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)MathF.Round((float)value), 0, 255);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }
}

public sealed class BicubicTextureUpscaleProvider : TextureUpscaleProviderBase
{
    public override string Name => "Bicubic";

    public override TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("Source texture has invalid dimensions.");

        if (source.Width < 256 || source.Height < 256)
            throw new InvalidOperationException("Source texture must be at least 256x256 before upscale.");

        (int width, int height) = CalculateTargetDimensions(source.Width, source.Height, targetSize);
        Bitmap bitmap = ResizeBitmap(source.Bitmap, width, height, InterpolationMode.HighQualityBicubic);
        byte[] rgba = BitmapToRgba(bitmap);
        return CreateResult(source, bitmap, rgba);
    }
}

public sealed class LanczosTextureUpscaleProvider : TextureUpscaleProviderBase
{
    public override string Name => "Lanczos";

    public override TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("Source texture has invalid dimensions.");

        if (source.Width < 256 || source.Height < 256)
            throw new InvalidOperationException("Source texture must be at least 256x256 before upscale.");

        (int width, int height) = CalculateTargetDimensions(source.Width, source.Height, targetSize);
        byte[] rgba = ResizeWithLanczos(source.RgbaPixels, source.Width, source.Height, width, height);
        Bitmap bitmap = RgbaToBitmap(rgba, width, height);
        return CreateResult(source, bitmap, rgba);
    }
}

public sealed class EdgeAwareTextureUpscaleProvider : TextureUpscaleProviderBase
{
    public override string Name => "Edge-Aware";

    public override TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("Source texture has invalid dimensions.");

        if (source.Width < 256 || source.Height < 256)
            throw new InvalidOperationException("Source texture must be at least 256x256 before upscale.");

        (int width, int height) = CalculateTargetDimensions(source.Width, source.Height, targetSize);
        Bitmap resized = ResizeBitmap(source.Bitmap, width, height, InterpolationMode.HighQualityBicubic);
        byte[] rgba = BitmapToRgba(resized);
        byte[] sharpened = ApplyEdgeAwareSharpen(rgba, width, height);
        Bitmap bitmap = RgbaToBitmap(sharpened, width, height);
        resized.Dispose();
        return CreateResult(source, bitmap, sharpened);
    }
}

public sealed class TextureUpscaleService
{
    private readonly Dictionary<string, ITextureUpscaleProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public TextureUpscaleService(IEnumerable<ITextureUpscaleProvider>? providers = null)
    {
        RegisterProvider(new BicubicTextureUpscaleProvider());
        RegisterProvider(new LanczosTextureUpscaleProvider());
        RegisterProvider(new EdgeAwareTextureUpscaleProvider());

        if (providers != null)
        {
            foreach (ITextureUpscaleProvider provider in providers)
                RegisterProvider(provider);
        }
    }

    public IReadOnlyList<string> ProviderNames => _providers.Keys.ToArray();

    public void RegisterProvider(ITextureUpscaleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Name] = provider;
    }

    public TexturePreviewTexture Upscale(TexturePreviewTexture source, int targetSize, string? providerName = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            providerName = _providers.Keys.First();
        }

        if (!_providers.TryGetValue(providerName, out ITextureUpscaleProvider? provider))
            throw new InvalidOperationException($"Unknown upscale method '{providerName}'.");

        return provider.Upscale(source, targetSize);
    }
}

