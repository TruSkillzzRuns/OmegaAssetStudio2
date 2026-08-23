using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialParentResolver
{
    public void ResolveParent(MaterialDefinition material, IReadOnlyList<MaterialDefinition> allMaterials)
    {
        if (material is null)
            return;

        if (!material.Type.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrWhiteSpace(material.ParentMaterialPath))
            return;

        MaterialDefinition? candidate = allMaterials.FirstOrDefault(item =>
            !string.Equals(item.Path, material.Path, StringComparison.OrdinalIgnoreCase) &&
            item.Name.Contains("Master", StringComparison.OrdinalIgnoreCase));

        if (candidate is not null)
            material.ParentMaterialPath = candidate.Path;
    }
}
