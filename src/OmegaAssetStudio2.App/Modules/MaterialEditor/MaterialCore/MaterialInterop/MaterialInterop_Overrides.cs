using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;

public static class MaterialInterop_Overrides
{
    public static IReadOnlyList<MhMaterialOverrideProfile> FromDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [new MhMaterialOverrideProfile { Name = $"{definition.Name} Default Override" }];
    }
}

