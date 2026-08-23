using System.Collections.ObjectModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public sealed class ShaderVariantViewModel : MaterialToolViewModelBase
{
    private MhShaderReference? selectedShader;
    private MhShaderPermutation? selectedPermutation;

    public ShaderVariantViewModel()
    {
        Title = "Shader Variant Tester";
        Variants = [];
    }

    public ObservableCollection<MhShaderPermutation> Variants { get; }

    public MhShaderReference? SelectedShader
    {
        get => selectedShader;
        set
        {
            if (!SetProperty(ref selectedShader, value))
                return;

            Variants.Clear();
            if (selectedShader is null)
                return;

            foreach (MhShaderPermutation permutation in selectedShader.Permutations)
                Variants.Add(permutation);
        }
    }

    public MhShaderPermutation? SelectedPermutation
    {
        get => selectedPermutation;
        set => SetProperty(ref selectedPermutation, value);
    }
}

