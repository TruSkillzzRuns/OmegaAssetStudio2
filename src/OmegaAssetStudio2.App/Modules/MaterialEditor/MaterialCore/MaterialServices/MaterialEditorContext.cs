using System.ComponentModel;
using System.Runtime.CompilerServices;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialViewModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialEditorContext : INotifyPropertyChanged
{
    private MhMaterialInstance? selectedMaterial;
    private MaterialWorkspaceTool activeTool = MaterialWorkspaceTool.LegacyWorkspace;

    public MaterialEditorContext()
        : this(new MaterialCoreServices())
    {
    }

    public MaterialEditorContext(MaterialCoreServices services)
    {
        Services = services;
        SharedPreview = new MaterialPreviewViewModel(services);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MaterialCoreServices Services { get; }

    public MaterialPreviewViewModel SharedPreview { get; }

    public MhMaterialInstance? SelectedMaterial
    {
        get => selectedMaterial;
        private set => SetProperty(ref selectedMaterial, value);
    }

    public MaterialWorkspaceTool ActiveTool
    {
        get => activeTool;
        private set => SetProperty(ref activeTool, value);
    }

    public void PublishMaterial(MaterialDefinition? materialDefinition)
    {
        SelectedMaterial = materialDefinition is null ? null : Services.MaterialInstances.BuildInstance(materialDefinition);
        SharedPreview.LoadMaterial(SelectedMaterial);
    }

    public void SetActiveTool(MaterialWorkspaceTool tool)
    {
        ActiveTool = tool;
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

