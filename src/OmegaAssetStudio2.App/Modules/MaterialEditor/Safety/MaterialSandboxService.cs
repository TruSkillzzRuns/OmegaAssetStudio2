using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialSandboxService
{
    private readonly Dictionary<string, MaterialDefinition> sandbox = new(StringComparer.OrdinalIgnoreCase);

    public MaterialDefinition Begin(MaterialDefinition source)
    {
        string key = BuildKey(source);
        MaterialDefinition working = source.Clone();
        working.OriginalPath = string.IsNullOrWhiteSpace(source.OriginalPath) ? source.Path : source.OriginalPath;
        sandbox[key] = working;
        return working;
    }

    public void Clear() => sandbox.Clear();

    private static string BuildKey(MaterialDefinition material)
    {
        return string.IsNullOrWhiteSpace(material.Path) ? material.Name : material.Path;
    }
}
