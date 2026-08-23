using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class ShaderVariantService
{
    public IReadOnlyList<MhShaderPermutation> BuildVariants(MhShaderReference shaderReference)
    {
        ArgumentNullException.ThrowIfNull(shaderReference);
        return shaderReference.Permutations.Count == 0
            ? [new MhShaderPermutation { Name = "Default", Value = shaderReference.Name, IsActive = true }]
            : shaderReference.Permutations.ToArray();
    }

    public MhShaderPermutation ResolveBaseline(IReadOnlyList<MhShaderPermutation> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return variants.FirstOrDefault(variant => variant.IsActive)
            ?? variants.FirstOrDefault()
            ?? new MhShaderPermutation { Name = "Default", Value = "Default", IsActive = true };
    }

    public void ActivateVariant(IReadOnlyList<MhShaderPermutation> variants, MhShaderPermutation selectedVariant)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(selectedVariant);
        foreach (MhShaderPermutation variant in variants)
            variant.IsActive = ReferenceEquals(variant, selectedVariant);
    }

    public string BuildComparisonSummary(MhShaderPermutation baseline, MhShaderPermutation selected)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(selected);
        bool sameName = string.Equals(baseline.Name, selected.Name, StringComparison.OrdinalIgnoreCase);
        bool sameValue = string.Equals(baseline.Value, selected.Value, StringComparison.OrdinalIgnoreCase);
        if (sameName && sameValue)
            return "Selected variant matches baseline.";

        return $"Baseline: {baseline.Name}={baseline.Value} | Selected: {selected.Name}={selected.Value}";
    }
}
