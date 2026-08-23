using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;

public static class MaterialInterop_Shaders
{
    public static MhShaderReference FromDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new MhShaderReference
        {
            Name = definition.Type,
            SourcePath = definition.Path,
            SourceUpkPath = definition.SourceUpkPath,
            Permutations = [new MhShaderPermutation { Name = "Default", Value = definition.Type, IsActive = true }]
        };
    }
}

