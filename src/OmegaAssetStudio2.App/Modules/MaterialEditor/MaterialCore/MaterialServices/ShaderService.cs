using OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialModels;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

public sealed class ShaderService
{
    public MhShaderReference BuildReference(string shaderName, string sourcePath, string sourceUpkPath)
    {
        return new MhShaderReference
        {
            Name = shaderName,
            SourcePath = sourcePath,
            SourceUpkPath = sourceUpkPath,
            Permutations =
            [
                new MhShaderPermutation { Name = "Default", Value = shaderName, IsActive = true }
            ]
        };
    }

    public IReadOnlyList<string> BuildParameterBindings(MhMaterialInstance? material)
    {
        if (material is null)
            return [];

        return material.Parameters
            .Select(parameter => $"{parameter.Category}: {parameter.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> BuildUsagePaths(MhMaterialInstance? material)
    {
        if (material is null)
            return [];

        List<string> usages =
        [
            material.Path,
            $"{material.Name} ({material.SourceUpkPath})"
        ];

        if (!string.IsNullOrWhiteSpace(material.SourceMeshExportPath))
            usages.Add(material.SourceMeshExportPath);

        return usages
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
