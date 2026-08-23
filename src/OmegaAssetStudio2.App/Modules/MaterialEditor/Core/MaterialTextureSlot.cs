namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

public sealed class MaterialTextureSlot : NotifyPropertyChangedBase
{
    private string slotName = string.Empty;
    private string textureName = string.Empty;
    private string texturePath = string.Empty;
    private bool isOverride;

    public string SlotName
    {
        get => slotName;
        set => SetProperty(ref slotName, value);
    }

    public string TextureName
    {
        get => textureName;
        set => SetProperty(ref textureName, value);
    }

    public string TexturePath
    {
        get => texturePath;
        set => SetProperty(ref texturePath, value);
    }

    public bool IsOverride
    {
        get => isOverride;
        set => SetProperty(ref isOverride, value);
    }

    public MaterialTextureSlot Clone()
    {
        return new MaterialTextureSlot
        {
            SlotName = SlotName,
            TextureName = TextureName,
            TexturePath = TexturePath,
            IsOverride = IsOverride
        };
    }

    public void CopyFrom(MaterialTextureSlot source)
    {
        SlotName = source.SlotName;
        TextureName = source.TextureName;
        TexturePath = source.TexturePath;
        IsOverride = source.IsOverride;
    }
}

