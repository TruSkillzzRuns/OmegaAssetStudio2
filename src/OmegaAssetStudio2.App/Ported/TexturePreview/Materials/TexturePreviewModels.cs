using System.Drawing;

namespace OmegaAssetStudio.TexturePreview;

public enum TexturePreviewMaterialSlot
{
    Diffuse,
    Normal,
    Specular,
    Emissive,
    Mask
}

public enum TexturePreviewMaterialMode
{
    DiffuseOnly,
    DiffuseAndNormal,
    FullMaterial
}

public enum TexturePreviewChannelView
{
    Rgba,
    Red,
    Green,
    Blue,
    Alpha
}

public sealed class TexturePreviewTexture : IDisposable
{
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string SourceDescription { get; init; } = string.Empty;
    public string ExportPath { get; init; } = string.Empty;
    public Bitmap Bitmap { get; init; } = new(1, 1);
    public byte[] RgbaPixels { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; } = 1;
    public int SelectedMipIndex { get; init; }
    public string Format { get; init; } = "RGBA8";
    public string Compression { get; init; } = "None";
    public string ContainerType { get; init; } = string.Empty;
    public string MipSource { get; init; } = string.Empty;
    public TexturePreviewMaterialSlot Slot { get; set; } = TexturePreviewMaterialSlot.Diffuse;
    public byte[] ContainerBytes { get; init; }
    public IReadOnlyList<TexturePreviewMipLevel> AvailableMipLevels { get; init; } = Array.Empty<TexturePreviewMipLevel>();
    public bool IsUpkTexture => !string.IsNullOrWhiteSpace(ExportPath);

    public string ResolutionText => $"{Width} x {Height}";

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

public sealed class TexturePreviewMipLevel
{
    public required int AbsoluteIndex { get; init; }
    public required int RelativeIndex { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int DataSize { get; init; }
    public required string Source { get; init; }
    public required string Format { get; init; }

    public override string ToString()
    {
        return $"Mip {AbsoluteIndex} [{Width} x {Height}] [{Source}]";
    }
}

public sealed class TexturePreviewMaterialSet
{
    private readonly Dictionary<TexturePreviewMaterialSlot, TexturePreviewTexture> _textures = [];

    public bool Enabled { get; set; }
    public int Revision { get; private set; }

    public IEnumerable<KeyValuePair<TexturePreviewMaterialSlot, TexturePreviewTexture>> Textures => _textures;

    public TexturePreviewTexture GetTexture(TexturePreviewMaterialSlot slot)
    {
        return _textures.TryGetValue(slot, out TexturePreviewTexture texture) ? texture : null;
    }

    public bool HasTexture(TexturePreviewMaterialSlot slot)
    {
        return _textures.ContainsKey(slot);
    }

    public void SetTexture(TexturePreviewMaterialSlot slot, TexturePreviewTexture texture)
    {
        _textures[slot] = texture;
        Revision++;
    }

    public void Clear()
    {
        _textures.Clear();
        Revision++;
    }

    public TexturePreviewMaterialMode ResolveMode()
    {
        if (HasTexture(TexturePreviewMaterialSlot.Specular) ||
            HasTexture(TexturePreviewMaterialSlot.Emissive) ||
            HasTexture(TexturePreviewMaterialSlot.Mask))
        {
            return TexturePreviewMaterialMode.FullMaterial;
        }

        if (HasTexture(TexturePreviewMaterialSlot.Normal))
            return TexturePreviewMaterialMode.DiffuseAndNormal;

        return TexturePreviewMaterialMode.DiffuseOnly;
    }
}

