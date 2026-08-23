using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhMaterialOverrideProfile
{
    public string Name { get; set; } = string.Empty;

    public string TintColor { get; set; } = "#FFFFFFFF";

    public float RoughnessScale { get; set; } = 1.0f;

    public float EmissiveScale { get; set; } = 1.0f;

    public List<MhMaterialParameter> Overrides { get; set; } = [];
}

