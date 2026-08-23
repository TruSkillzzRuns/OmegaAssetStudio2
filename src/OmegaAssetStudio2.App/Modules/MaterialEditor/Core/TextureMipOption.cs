namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class TextureMipOption
{
    public required int AbsoluteIndex { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required string Source { get; init; }

    public required string Format { get; init; }

    public string DisplayText => $"Mip {AbsoluteIndex} [{Width} x {Height}] [{Source}] [{Format}]";

    public override string ToString() => DisplayText;
}
