using System.Buffers.Binary;

namespace OmegaAssetStudio.TexturePreview;

public static class TextureFormatDetector
{
    /// <summary>
    /// Detects the texture file format from the file header.
    /// </summary>
    /// <param name="filePath">The texture file path.</param>
    /// <returns>The detected file format.</returns>
    public static TextureFileFormat DetectFormat(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return TextureFileFormat.Unknown;

        byte[] header = new byte[Math.Min(32, (int)new FileInfo(filePath).Length)];
        using FileStream stream = File.OpenRead(filePath);
        _ = stream.Read(header, 0, header.Length);
        return DetectFormat(header);
    }

    /// <summary>
    /// Detects the texture file format from a byte header.
    /// </summary>
    /// <param name="header">The file header bytes.</param>
    /// <returns>The detected file format.</returns>
    public static TextureFileFormat DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 &&
            header[0] == 0x89 &&
            header[1] == 0x50 &&
            header[2] == 0x4E &&
            header[3] == 0x47)
        {
            return TextureFileFormat.Png;
        }

        if (header.Length >= 4 &&
            header[0] == 0x44 &&
            header[1] == 0x44 &&
            header[2] == 0x53 &&
            header[3] == 0x20)
        {
            return TextureFileFormat.Dds;
        }

        if (header.Length >= 2 &&
            header[0] == 0x42 &&
            header[1] == 0x4D)
        {
            return TextureFileFormat.Bmp;
        }

        if (header.Length >= 3 &&
            header[0] == 0xFF &&
            header[1] == 0xD8 &&
            header[2] == 0xFF)
        {
            return TextureFileFormat.Jpeg;
        }

        if (LooksLikeTga(header))
            return TextureFileFormat.Tga;

        return TextureFileFormat.Unknown;
    }

    private static bool LooksLikeTga(ReadOnlySpan<byte> header)
    {
        if (header.Length < 18)
            return false;

        byte imageType = header[2];
        if (imageType is not 2 and not 10)
            return false;

        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(12, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(14, 2));
        byte pixelDepth = header[16];
        return width > 0 && height > 0 && pixelDepth is 24 or 32;
    }
}

