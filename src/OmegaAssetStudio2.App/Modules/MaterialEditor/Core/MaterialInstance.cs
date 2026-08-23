namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class MaterialInstance : NotifyPropertyChangedBase
{
    private MaterialDefinition definition = new();

    public MaterialDefinition Definition
    {
        get => definition;
        set => SetProperty(ref definition, value);
    }
}

