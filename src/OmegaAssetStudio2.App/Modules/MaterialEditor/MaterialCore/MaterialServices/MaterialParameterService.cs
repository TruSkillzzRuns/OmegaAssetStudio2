using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialParameterService
{
    public IReadOnlyList<MhMaterialParameter> MergeParameters(IEnumerable<MhMaterialParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.ToArray();
    }
}

