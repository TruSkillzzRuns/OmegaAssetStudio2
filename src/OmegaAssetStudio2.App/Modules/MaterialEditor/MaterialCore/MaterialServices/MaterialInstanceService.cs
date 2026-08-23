using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialInstanceService
{
    public MhMaterialInstance BuildInstance(MaterialDefinition definition)
    {
        MhMaterialInstance instance = MaterialInterop_Instances.FromDefinition(definition);
        instance.Parameters = [.. MaterialInterop_Parameters.FromDefinition(definition)];
        instance.ShaderReference = MaterialInterop_Shaders.FromDefinition(definition);
        var graph = MaterialInterop_Graph.FromDefinition(definition);
        instance.GraphNodes = graph.Nodes;
        instance.GraphConnections = graph.Connections;
        instance.OverrideProfiles = [.. MaterialInterop_Overrides.FromDefinition(definition)];
        return instance;
    }
}

