using System.Collections.ObjectModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public sealed class ShaderInspectorViewModel : MaterialToolViewModelBase
{
    private MhShaderReference? selectedShader;

    public ShaderInspectorViewModel()
    {
        Title = "Shader Inspector";
        Permutations = [];
    }

    public ObservableCollection<MhShaderPermutation> Permutations { get; }

    public MhShaderReference? SelectedShader
    {
        get => selectedShader;
        set
        {
            if (!SetProperty(ref selectedShader, value))
                return;

            Permutations.Clear();
            if (selectedShader is null)
                return;

            foreach (MhShaderPermutation permutation in selectedShader.Permutations)
                Permutations.Add(permutation);
        }
    }
}

