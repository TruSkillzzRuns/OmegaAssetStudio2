using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;

public static class MaterialInterop_Graph
{
    public static (List<MhMaterialGraphNode> Nodes, List<MhMaterialGraphConnection> Connections) FromDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<MhMaterialGraphNode> nodes =
        [
            new MhMaterialGraphNode
            {
                NodeId = "root",
                NodeType = "Material",
                Label = definition.Name,
                Inputs = ["BaseColor", "Specular", "Normal", "Emissive", "Mask"],
                Outputs = ["FinalMaterial"]
            }
        ];

        List<MhMaterialGraphConnection> connections = [];
        AddScalarNodes(definition, nodes, connections);
        AddVectorNodes(definition, nodes, connections);
        AddTextureNodes(definition, nodes, connections);
        return (nodes, connections);
    }

    private static void AddScalarNodes(MaterialDefinition definition, List<MhMaterialGraphNode> nodes, List<MhMaterialGraphConnection> connections)
    {
        foreach (MaterialParameter parameter in definition.ScalarParameters)
        {
            string id = $"scalar:{parameter.Name}";
            nodes.Add(new MhMaterialGraphNode
            {
                NodeId = id,
                NodeType = "ScalarParameter",
                Label = parameter.Name,
                Outputs = ["Value"]
            });

            connections.Add(new MhMaterialGraphConnection
            {
                FromNodeId = id,
                FromPin = "Value",
                ToNodeId = "root",
                ToPin = "Specular"
            });
        }
    }

    private static void AddVectorNodes(MaterialDefinition definition, List<MhMaterialGraphNode> nodes, List<MhMaterialGraphConnection> connections)
    {
        foreach (MaterialParameter parameter in definition.VectorParameters)
        {
            string id = $"vector:{parameter.Name}";
            nodes.Add(new MhMaterialGraphNode
            {
                NodeId = id,
                NodeType = "VectorParameter",
                Label = parameter.Name,
                Outputs = ["Color"]
            });

            connections.Add(new MhMaterialGraphConnection
            {
                FromNodeId = id,
                FromPin = "Color",
                ToNodeId = "root",
                ToPin = "BaseColor"
            });
        }
    }

    private static void AddTextureNodes(MaterialDefinition definition, List<MhMaterialGraphNode> nodes, List<MhMaterialGraphConnection> connections)
    {
        foreach (MaterialTextureSlot slot in definition.TextureSlots)
        {
            string id = $"texture:{slot.SlotName}";
            nodes.Add(new MhMaterialGraphNode
            {
                NodeId = id,
                NodeType = "TextureParameter",
                Label = slot.SlotName,
                Outputs = ["Texture"]
            });

            connections.Add(new MhMaterialGraphConnection
            {
                FromNodeId = id,
                FromPin = "Texture",
                ToNodeId = "root",
                ToPin = ResolveRootPin(slot.SlotName)
            });
        }
    }

    private static string ResolveRootPin(string slotName)
    {
        string value = slotName.ToLowerInvariant();
        if (value.Contains("norm"))
            return "Normal";
        if (value.Contains("spec"))
            return "Specular";
        if (value.Contains("emis") || value.Contains("glow"))
            return "Emissive";
        if (value.Contains("mask") || value.Contains("alpha"))
            return "Mask";
        return "BaseColor";
    }
}
