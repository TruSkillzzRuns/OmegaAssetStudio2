using UpkManager.Models.UpkFile.Engine.Material;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Bridge between the existing UpkManager FMaterialResource parser and our
// variant-system snapshot. No raw byte walking — we lean on the parser's
// structured fields so any future schema change in game UPKs (rare, but
// possible across patches) is absorbed by the parser, not by us.
public static class MaterialBodyReader
{
    // Extracts the comparable shape of a parsed material resource. Null
    // input returns an empty snapshot; callers can detect that as
    // "no static permutation resource present" since unparsed MICs hit
    // this case.
    public static MaterialBodySnapshot FromResource(FMaterialResource? resource)
    {
        if (resource is null) return new();

        var texPaths = new List<string>();
        if (resource.UniformExpressionTextures is not null)
        {
            foreach (var t in resource.UniformExpressionTextures)
            {
                // ObjectName via the import/export table — already resolved
                // by the parser, just stringify.
                texPaths.Add(t?.ToString() ?? "<null>");
            }
        }

        return new MaterialBodySnapshot
        {
            NumTexCoords = resource.NumUserTexCoords,
            TextureCount = resource.UniformExpressionTextures?.Count ?? 0,
            LookupCount = resource.TextureLookups?.Count ?? 0,
            UsingTransforms = resource.UsingTransforms,
            MaxTextureDependencyLength = resource.MaxTextureDependencyLength,
            UsesSceneColor = resource.bUsesSceneColor,
            UsesSceneDepth = resource.bUsesSceneDepth,
            UsesDynamicParameter = resource.bUsesDynamicParameter,
            UsesLightmapUVs = resource.bUsesLightmapUVs,
            UsesMaterialVertexPositionOffset = resource.bUsesMaterialVertexPositionOffset,
            BlendModeValue = (int)resource.BlendModeOverrideValue,
            IsBlendModeOverridden = resource.bIsBlendModeOverrided,
            IsMaskedOverride = resource.bIsMaskedOverrideValue,
            TexturePaths = texPaths,
        };
    }

    // Convenience: extract baseline (qIndex=0 = standard quality, qIndex=1
    // = low-quality fallback). game usually only ships qIndex=0.
    public static MaterialBodySnapshot FromMaterialInstance(UMaterialInstance? mic, int qIndex = 0)
    {
        if (mic is null) return new();
        if (mic.StaticPermutationResources is null || qIndex >= mic.StaticPermutationResources.Length) return new();
        return FromResource(mic.StaticPermutationResources[qIndex]);
    }

    // Reads the static-switch parameter values into a flat name→bool map.
    // Used by the learner to know which switch flipped between two samples.
    public static Dictionary<string, bool> ReadSwitchValues(UMaterialInstance? mic, int qIndex = 0)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (mic is null || mic.StaticParameters is null || qIndex >= mic.StaticParameters.Length) return result;
        var set = mic.StaticParameters[qIndex];
        if (set?.StaticSwitchParameters is null) return result;
        foreach (var p in set.StaticSwitchParameters)
        {
            string name = p?.ParameterName?.ToString() ?? "";
            if (name.Length > 0) result[name] = p!.Value;
        }
        return result;
    }
}
