using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhMaterialInstance
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string SourceUpkPath { get; set; } = string.Empty;

    public string SourceMeshExportPath { get; set; } = string.Empty;

    public string ShaderName { get; set; } = string.Empty;

    public MhShaderReference ShaderReference { get; set; } = new();

    public List<MhMaterialParameter> Parameters { get; set; } = [];

    public List<MhMaterialGraphNode> GraphNodes { get; set; } = [];

    public List<MhMaterialGraphConnection> GraphConnections { get; set; } = [];

    public List<MhMaterialOverrideProfile> OverrideProfiles { get; set; } = [];
}

