using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialRenderer;

public sealed class MaterialRendererService
{
    public MaterialRendererState BuildState(MhMaterialInstance? material, string permutation, string previewMode = "MATERIAL")
    {
        return new MaterialRendererState
        {
            Material = material,
            PreviewMode = string.IsNullOrWhiteSpace(previewMode) ? "MATERIAL" : previewMode.Trim().ToUpperInvariant(),
            ShaderPermutation = string.IsNullOrWhiteSpace(permutation) ? "Default" : permutation.Trim()
        };
    }

    public MaterialRendererState BuildGraphOverlay(MhMaterialInstance? material)
    {
        return BuildState(material, "Graph", "GRAPH");
    }

    public MaterialRendererState BuildOverridePreview(MhMaterialInstance? material)
    {
        return BuildState(material, "Override", "OVERRIDE");
    }

    public MaterialRendererState BuildColorVariantPreview(MhMaterialInstance? material)
    {
        return BuildState(material, "Variant", "COLORVARIANT");
    }
}

