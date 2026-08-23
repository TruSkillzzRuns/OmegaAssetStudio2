using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialInterop;

public static class MaterialInterop_Parameters
{
    public static IReadOnlyList<MhMaterialParameter> FromDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<MhMaterialParameter> parameters = [];
        parameters.AddRange(definition.ScalarParameters.Select(parameter => new MhScalarParameter
        {
            Name = parameter.Name,
            Category = parameter.Category,
            Value = parameter.ScalarValue ?? parameter.DefaultScalarValue ?? 0f,
            DefaultValue = parameter.DefaultScalarValue ?? 0f
        }));

        parameters.AddRange(definition.VectorParameters.Select(parameter => new MhVectorParameter
        {
            Name = parameter.Name,
            Category = parameter.Category,
            Value = parameter.VectorValue?.ToString() ?? parameter.DefaultVectorValue?.ToString() ?? string.Empty,
            DefaultValue = parameter.DefaultVectorValue?.ToString() ?? string.Empty
        }));

        parameters.AddRange(definition.TextureSlots.Select(slot => new MhTextureParameter
        {
            Name = slot.SlotName,
            Category = "Texture",
            TextureName = slot.TextureName,
            TexturePath = slot.TexturePath,
            IsOverride = slot.IsOverride
        }));

        return parameters;
    }
}

