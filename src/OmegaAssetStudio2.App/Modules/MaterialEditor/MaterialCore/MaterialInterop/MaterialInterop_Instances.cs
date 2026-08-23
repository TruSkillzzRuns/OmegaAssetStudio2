using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;

public static class MaterialInterop_Instances
{
    public static MhMaterialInstance FromDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new MhMaterialInstance
        {
            Name = definition.Name,
            Path = definition.Path,
            SourceUpkPath = definition.SourceUpkPath,
            SourceMeshExportPath = definition.SourceMeshExportPath,
            ShaderName = definition.Type,
            ShaderReference = new MhShaderReference
            {
                Name = definition.Type,
                SourcePath = definition.Path,
                SourceUpkPath = definition.SourceUpkPath
            }
        };
    }
}

