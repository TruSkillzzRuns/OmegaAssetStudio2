using System.Text.Json;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialGraphService
{
    public IReadOnlyList<MhMaterialGraphNode> BuildNodes(IEnumerable<MhMaterialGraphNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes.ToArray();
    }

    public IReadOnlyList<MhMaterialGraphConnection> BuildConnections(IEnumerable<MhMaterialGraphConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return connections.ToArray();
    }

    public IReadOnlyDictionary<string, GraphNodeLayoutEntry> BuildDefaultLayout(IEnumerable<MhMaterialGraphNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        Dictionary<string, GraphNodeLayoutEntry> map = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (MhMaterialGraphNode node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
                continue;

            float x = 20f + ((index % 4) * 220f);
            float y = 20f + ((index / 4) * 160f);
            map[node.NodeId] = new GraphNodeLayoutEntry(node.NodeId, x, y);
            index++;
        }

        return map;
    }

    public string ExportLayoutMetadata(string materialPath, IEnumerable<MhMaterialGraphNode> nodes, IReadOnlyDictionary<string, GraphNodeLayoutEntry> layout)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(layout);
        GraphLayoutMetadata metadata = new()
        {
            MaterialPath = materialPath ?? string.Empty,
            Nodes = nodes
                .Where(node => !string.IsNullOrWhiteSpace(node.NodeId))
                .Select(node =>
                {
                    if (!layout.TryGetValue(node.NodeId, out GraphNodeLayoutEntry? entry))
                        entry = new GraphNodeLayoutEntry(node.NodeId, 0f, 0f);

                    return new GraphNodeLayoutRecord
                    {
                        NodeId = node.NodeId,
                        NodeType = node.NodeType,
                        Label = node.Label,
                        X = entry.X,
                        Y = entry.Y
                    };
                })
                .ToList()
        };

        return JsonSerializer.Serialize(metadata, GraphJsonOptions);
    }

    public IReadOnlyDictionary<string, GraphNodeLayoutEntry> ImportLayoutMetadata(string layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return new Dictionary<string, GraphNodeLayoutEntry>(StringComparer.OrdinalIgnoreCase);

        GraphLayoutMetadata? metadata = JsonSerializer.Deserialize<GraphLayoutMetadata>(layoutJson, GraphJsonOptions);
        Dictionary<string, GraphNodeLayoutEntry> map = new(StringComparer.OrdinalIgnoreCase);
        if (metadata?.Nodes is null)
            return map;

        foreach (GraphNodeLayoutRecord node in metadata.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
                continue;

            map[node.NodeId] = new GraphNodeLayoutEntry(node.NodeId, node.X, node.Y);
        }

        return map;
    }

    private static JsonSerializerOptions GraphJsonOptions { get; } = new()
    {
        WriteIndented = true
    };

    public sealed record GraphNodeLayoutEntry(string NodeId, float X, float Y);

    private sealed class GraphLayoutMetadata
    {
        public string MaterialPath { get; set; } = string.Empty;

        public List<GraphNodeLayoutRecord> Nodes { get; set; } = [];
    }

    private sealed class GraphNodeLayoutRecord
    {
        public string NodeId { get; set; } = string.Empty;

        public string NodeType { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public float X { get; set; }

        public float Y { get; set; }
    }
}
