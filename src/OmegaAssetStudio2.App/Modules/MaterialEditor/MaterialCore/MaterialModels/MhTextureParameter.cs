namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhTextureParameter : MhMaterialParameter
{
    public string TextureName { get; set; } = string.Empty;

    public string TexturePath { get; set; } = string.Empty;

    public bool IsOverride { get; set; }
}

