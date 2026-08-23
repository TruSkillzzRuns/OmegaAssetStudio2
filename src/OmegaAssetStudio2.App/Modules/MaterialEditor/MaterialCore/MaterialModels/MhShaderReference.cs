using System.Collections.Generic;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

public sealed class MhShaderReference
{
    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string SourceUpkPath { get; set; } = string.Empty;

    public List<MhShaderPermutation> Permutations { get; set; } = [];
}

