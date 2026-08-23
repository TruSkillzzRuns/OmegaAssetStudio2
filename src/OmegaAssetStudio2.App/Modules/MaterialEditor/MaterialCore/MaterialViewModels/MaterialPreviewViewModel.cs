using System.Collections.ObjectModel;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialRenderer;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

public sealed class MaterialPreviewViewModel : MaterialToolViewModelBase
{
    private readonly MaterialRendererService rendererService;
    private MhMaterialInstance? selectedMaterial;
    private MaterialRendererState previewState = new();
    private string activePermutation = "Default";

    public MaterialPreviewViewModel()
        : this(new MaterialCoreServices())
    {
    }

    public MaterialPreviewViewModel(MaterialCoreServices services)
    {
        rendererService = services.Renderer;
        Title = "Material Preview";
        Parameters = [];
    }

    public ObservableCollection<MhMaterialParameter> Parameters { get; }

    public MhMaterialInstance? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (!SetProperty(ref selectedMaterial, value))
                return;

            ReloadParameters();
            RefreshPreview();
        }
    }

    public MaterialRendererState PreviewState
    {
        get => previewState;
        private set => SetProperty(ref previewState, value);
    }

    public string ActivePermutation
    {
        get => activePermutation;
        set
        {
            if (!SetProperty(ref activePermutation, value))
                return;

            RefreshPreview();
        }
    }

    public void LoadMaterial(MhMaterialInstance? material)
    {
        SelectedMaterial = material;
    }

    private void ReloadParameters()
    {
        Parameters.Clear();

        if (SelectedMaterial is null)
        {
            StatusText = "No material selected.";
            return;
        }

        foreach (MhMaterialParameter parameter in SelectedMaterial.Parameters)
            Parameters.Add(parameter);

        StatusText = $"{Parameters.Count:N0} parameter(s) loaded.";
    }

    private void RefreshPreview()
    {
        PreviewState = rendererService.BuildState(SelectedMaterial, ActivePermutation);
    }
}

