using System.Globalization;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class MaterialOverrideService
{
    public IReadOnlyList<MhMaterialOverrideProfile> BuildProfiles(IEnumerable<MhMaterialOverrideProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.ToArray();
    }

    public MhMaterialOverrideProfile BuildDefaultProfile(MhMaterialInstance material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return new MhMaterialOverrideProfile
        {
            Name = $"{material.Name} Override",
            TintColor = "#FFFFFFFF",
            RoughnessScale = 1.0f,
            EmissiveScale = 1.0f,
            Overrides = material.Parameters
                .Where(parameter => parameter is MhScalarParameter || parameter is MhVectorParameter || parameter is MhSwitchParameter)
                .Take(4)
                .Select(CloneParameter)
                .ToList()
        };
    }

    public void ApplyProfile(MhMaterialInstance material, MhMaterialOverrideProfile profile)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(profile);

        foreach (MhMaterialParameter overrideParameter in profile.Overrides)
        {
            MhMaterialParameter? target = material.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, overrideParameter.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parameter.Category, overrideParameter.Category, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                continue;

            ApplyParameterOverride(target, overrideParameter);
        }

        ApplyScalarMultiplier(material.Parameters, "rough", profile.RoughnessScale);
        ApplyScalarMultiplier(material.Parameters, "emis", profile.EmissiveScale);

        string tint = string.IsNullOrWhiteSpace(profile.TintColor) ? "#FFFFFFFF" : profile.TintColor;
        foreach (MhVectorParameter vector in material.Parameters.OfType<MhVectorParameter>())
        {
            string name = vector.Name.ToLowerInvariant();
            if (name.Contains("tint") || name.Contains("color"))
                vector.Value = tint;
        }
    }

    public void ResetProfile(MhMaterialOverrideProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.TintColor = "#FFFFFFFF";
        profile.RoughnessScale = 1.0f;
        profile.EmissiveScale = 1.0f;
        foreach (MhMaterialParameter parameter in profile.Overrides)
            ResetParameter(parameter);
    }

    public MhMaterialParameter CloneParameter(MhMaterialParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return parameter switch
        {
            MhScalarParameter scalar => new MhScalarParameter
            {
                Name = scalar.Name,
                Category = scalar.Category,
                Value = scalar.Value,
                DefaultValue = scalar.DefaultValue
            },
            MhVectorParameter vector => new MhVectorParameter
            {
                Name = vector.Name,
                Category = vector.Category,
                Value = vector.Value,
                DefaultValue = vector.DefaultValue
            },
            MhSwitchParameter toggle => new MhSwitchParameter
            {
                Name = toggle.Name,
                Category = toggle.Category,
                Value = toggle.Value,
                DefaultValue = toggle.DefaultValue
            },
            MhTextureParameter texture => new MhTextureParameter
            {
                Name = texture.Name,
                Category = texture.Category,
                TextureName = texture.TextureName,
                TexturePath = texture.TexturePath,
                IsOverride = texture.IsOverride
            },
            _ => throw new InvalidOperationException($"Unsupported override parameter type {parameter.GetType().Name}.")
        };
    }

    public string GetParameterValueText(MhMaterialParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return parameter switch
        {
            MhScalarParameter scalar => scalar.Value.ToString("0.###", CultureInfo.InvariantCulture),
            MhVectorParameter vector => vector.Value,
            MhSwitchParameter toggle => toggle.Value ? "true" : "false",
            MhTextureParameter texture => texture.TexturePath,
            _ => string.Empty
        };
    }

    public bool TrySetParameterValueText(MhMaterialParameter parameter, string valueText)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        valueText ??= string.Empty;

        switch (parameter)
        {
            case MhScalarParameter scalar:
                if (!float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalarValue))
                    return false;
                scalar.Value = scalarValue;
                return true;

            case MhVectorParameter vector:
                vector.Value = valueText.Trim();
                return true;

            case MhSwitchParameter toggle:
                if (!bool.TryParse(valueText, out bool boolValue))
                    return false;
                toggle.Value = boolValue;
                return true;

            case MhTextureParameter texture:
                texture.TexturePath = valueText.Trim();
                return true;
        }

        return false;
    }

    private static void ApplyParameterOverride(MhMaterialParameter target, MhMaterialParameter source)
    {
        if (target is MhScalarParameter targetScalar && source is MhScalarParameter sourceScalar)
        {
            targetScalar.Value = sourceScalar.Value;
            return;
        }

        if (target is MhVectorParameter targetVector && source is MhVectorParameter sourceVector)
        {
            targetVector.Value = sourceVector.Value;
            return;
        }

        if (target is MhSwitchParameter targetSwitch && source is MhSwitchParameter sourceSwitch)
        {
            targetSwitch.Value = sourceSwitch.Value;
            return;
        }

        if (target is MhTextureParameter targetTexture && source is MhTextureParameter sourceTexture)
        {
            targetTexture.TexturePath = sourceTexture.TexturePath;
            targetTexture.TextureName = sourceTexture.TextureName;
            targetTexture.IsOverride = true;
        }
    }

    private static void ApplyScalarMultiplier(IEnumerable<MhMaterialParameter> parameters, string token, float multiplier)
    {
        foreach (MhScalarParameter scalar in parameters.OfType<MhScalarParameter>())
        {
            if (!scalar.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                continue;

            scalar.Value *= multiplier;
        }
    }

    private static void ResetParameter(MhMaterialParameter parameter)
    {
        switch (parameter)
        {
            case MhScalarParameter scalar:
                scalar.Value = scalar.DefaultValue;
                break;

            case MhVectorParameter vector:
                vector.Value = vector.DefaultValue;
                break;

            case MhSwitchParameter toggle:
                toggle.Value = toggle.DefaultValue;
                break;
        }
    }
}
