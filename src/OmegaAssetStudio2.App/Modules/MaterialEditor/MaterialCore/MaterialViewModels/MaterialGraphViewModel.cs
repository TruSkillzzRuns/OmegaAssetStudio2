using System.Collections.ObjectModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public sealed class MaterialGraphViewModel : MaterialToolViewModelBase
{
    private MhMaterialInstance? selectedMaterial;

    public MaterialGraphViewModel()
    {
        Title = "Material Graph Editor";
        Nodes = [];
        Connections = [];
    }

    public ObservableCollection<MhMaterialGraphNode> Nodes { get; }

    public ObservableCollection<MhMaterialGraphConnection> Connections { get; }

    public MhMaterialInstance? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (!SetProperty(ref selectedMaterial, value))
                return;

            Nodes.Clear();
            Connections.Clear();
            if (selectedMaterial is null)
                return;

            foreach (MhMaterialGraphNode node in selectedMaterial.GraphNodes)
                Nodes.Add(node);

            foreach (MhMaterialGraphConnection connection in selectedMaterial.GraphConnections)
                Connections.Add(connection);
        }
    }
}

