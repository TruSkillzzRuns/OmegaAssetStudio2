namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class MaterialValidationResult
{
    public bool IsValid { get; init; }
    public string Message { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string MaterialPath { get; init; } = string.Empty;
    public int TextureSlotCount { get; init; }
    public int ScalarParameterCount { get; init; }
    public int VectorParameterCount { get; init; }
}

