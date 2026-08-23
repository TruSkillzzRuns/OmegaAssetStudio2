using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialRenderer;

public sealed class MaterialRendererState
{
    public MhMaterialInstance? Material { get; init; }

    public string PreviewMode { get; init; } = "MATERIAL";

    public string ShaderPermutation { get; init; } = "Default";
}

