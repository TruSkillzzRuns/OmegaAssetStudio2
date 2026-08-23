namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhMaterialGraphConnection
{
    public string FromNodeId { get; set; } = string.Empty;

    public string FromPin { get; set; } = string.Empty;

    public string ToNodeId { get; set; } = string.Empty;

    public string ToPin { get; set; } = string.Empty;
}

