using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class TextureMetadata
{
    private string textureName = string.Empty;
    private string texturePath = string.Empty;
    private string sourceUpkPath = string.Empty;
    private string exportClass = string.Empty;
    private string format = string.Empty;
    private string compression = string.Empty;
    private string containerType = string.Empty;
    private string sourceDescription = string.Empty;
    private int width;
    private int height;
    private int mipCount;
    private bool isTexture2D;

    public string TextureName
    {
        get => textureName;
        set => textureName = value ?? string.Empty;
    }

    public string TexturePath
    {
        get => texturePath;
        set => texturePath = value ?? string.Empty;
    }

    public string SourceUpkPath
    {
        get => sourceUpkPath;
        set => sourceUpkPath = value ?? string.Empty;
    }

    public string ExportClass
    {
        get => exportClass;
        set => exportClass = value ?? string.Empty;
    }

    public string Format
    {
        get => format;
        set => format = value ?? string.Empty;
    }

    public string Compression
    {
        get => compression;
        set => compression = value ?? string.Empty;
    }

    public string ContainerType
    {
        get => containerType;
        set => containerType = value ?? string.Empty;
    }

    public string SourceDescription
    {
        get => sourceDescription;
        set => sourceDescription = value ?? string.Empty;
    }

    public int Width
    {
        get => width;
        set => width = value;
    }

    public int Height
    {
        get => height;
        set => height = value;
    }

    public int MipCount
    {
        get => mipCount;
        set => mipCount = value;
    }

    public bool IsTexture2D
    {
        get => isTexture2D;
        set => isTexture2D = value;
    }

    public IReadOnlyList<string> MipSummaries { get; set; } = [];
}

