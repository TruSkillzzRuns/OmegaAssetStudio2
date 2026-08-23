using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialValidator
{
    public IReadOnlyList<MaterialValidationIssue> Validate(MaterialDefinition material, IReadOnlyList<MaterialDefinition> allMaterials)
    {
        List<MaterialValidationIssue> issues = [];

        if (material.Type.Contains("MaterialInstance", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(material.ParentMaterialPath))
            {
                issues.Add(new MaterialValidationIssue
                {
                    Severity = "Error",
                    Message = "MIC parent is missing."
                });
            }
            else if (!allMaterials.Any(item => string.Equals(item.Path, material.ParentMaterialPath, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new MaterialValidationIssue
                {
                    Severity = "Error",
                    Message = $"MIC parent not found: {material.ParentMaterialPath}"
                });
            }
        }

        foreach (MaterialTextureSlot slot in material.TextureSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.TexturePath) ||
                string.Equals(slot.TexturePath, "<null>", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(slot.TexturePath, "<missing>", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(slot.TexturePath, "<unresolved>", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new MaterialValidationIssue
                {
                    Severity = "Warning",
                    Message = $"Texture slot unresolved: {slot.SlotName}"
                });
            }
        }

        HashSet<string> scalarNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialParameter scalar in material.ScalarParameters)
        {
            if (!scalarNames.Add(scalar.Name ?? string.Empty))
            {
                issues.Add(new MaterialValidationIssue
                {
                    Severity = "Warning",
                    Message = $"Duplicate scalar parameter: {scalar.Name}"
                });
            }
        }

        HashSet<string> vectorNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialParameter vector in material.VectorParameters)
        {
            if (!vectorNames.Add(vector.Name ?? string.Empty))
            {
                issues.Add(new MaterialValidationIssue
                {
                    Severity = "Warning",
                    Message = $"Duplicate vector parameter: {vector.Name}"
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(material.ParentMaterialPath) &&
            string.Equals(material.Path, material.ParentMaterialPath, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new MaterialValidationIssue
            {
                Severity = "Error",
                Message = "Circular reference detected (material parent points to itself)."
            });
        }

        return issues;
    }
}
