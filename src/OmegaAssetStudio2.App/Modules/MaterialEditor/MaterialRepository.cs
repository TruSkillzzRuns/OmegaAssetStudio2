using System;
using System.Collections.Generic;
using System.Linq;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

public sealed class MaterialRepository
{
    private readonly Dictionary<string, MaterialDefinition> materials = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<MaterialDefinition> Materials => materials.Values.ToArray();

    public void Clear()
    {
        materials.Clear();
    }

    public void AddOrUpdate(MaterialDefinition material)
    {
        materials[GetKey(material)] = material;
    }

    public bool TryGetByName(string name, out MaterialDefinition? material)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            material = null;
            return false;
        }

        return materials.TryGetValue(name, out material);
    }

    public bool TryGetByPath(string path, out MaterialDefinition? material)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            material = null;
            return false;
        }

        return materials.TryGetValue(path, out material);
    }

    public static string GetKey(MaterialDefinition material)
    {
        if (!string.IsNullOrWhiteSpace(material.Path))
            return material.Path;

        if (!string.IsNullOrWhiteSpace(material.Name))
            return material.Name;

        return Guid.NewGuid().ToString("N");
    }
}

