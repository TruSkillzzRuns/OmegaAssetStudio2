using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhMaterialGraphNode
{
    public string NodeId { get; set; } = string.Empty;

    public string NodeType { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public List<string> Inputs { get; set; } = [];

    public List<string> Outputs { get; set; } = [];
}

