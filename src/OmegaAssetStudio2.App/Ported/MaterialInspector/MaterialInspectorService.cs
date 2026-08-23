using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Engine.Mesh;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.MaterialInspector;

public sealed class MaterialInspectorService
{
    private readonly UpkFileRepository _repository = new();

    public async Task<List<string>> GetSkeletalMeshExportsAsync(string upkPath)
    {
        var header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadTablesAsync(null).ConfigureAwait(true);

        return header.ExportTable
            .Where(static export =>
                string.Equals(export.ClassReferenceNameIndex?.Name, nameof(USkeletalMesh), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(export.ClassReferenceNameIndex?.Name, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            .Select(static export => export.GetPathName())
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MaterialInspectorResult> InspectAsync(string upkPath, string skeletalMeshExportPath)
    {
        var header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(true);
        await header.ReadHeaderAsync(null).ConfigureAwait(true);

        UnrealExportTableEntry export = header.ExportTable
            .FirstOrDefault(entry => string.Equals(entry.GetPathName(), skeletalMeshExportPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Could not find SkeletalMesh export '{skeletalMeshExportPath}'.");

        if (export.UnrealObject == null)
            await export.ParseUnrealObject(false, false).ConfigureAwait(true);

        if (export.UnrealObject is not IUnrealObject unrealObject || unrealObject.UObject is not USkeletalMesh skeletalMesh)
            throw new InvalidOperationException($"Export '{skeletalMeshExportPath}' is not a SkeletalMesh.");

        var sections = new List<MaterialInspectorSectionInfo>();
        if (skeletalMesh.LODModels.Count == 0)
            return new MaterialInspectorResult { UpkPath = upkPath, SkeletalMeshExportPath = skeletalMeshExportPath, Sections = sections };

        var lod = skeletalMesh.LODModels[0];
        for (int i = 0; i < lod.Sections.Count; i++)
        {
            var section = lod.Sections[i];
            FObject materialObject = section.MaterialIndex >= 0 && section.MaterialIndex < skeletalMesh.Materials.Count
                ? skeletalMesh.Materials[section.MaterialIndex]
                : null;

            sections.Add(new MaterialInspectorSectionInfo
            {
                SectionIndex = i,
                MaterialIndex = section.MaterialIndex,
                MaterialPath = materialObject?.GetPathName() ?? "<missing>",
                MaterialType = ResolveMaterialType(materialObject),
                MaterialChain = BuildMaterialChain(materialObject)
            });
        }

        return new MaterialInspectorResult
        {
            UpkPath = upkPath,
            SkeletalMeshExportPath = skeletalMeshExportPath,
            Sections = sections
        };
    }

    private static string ResolveMaterialType(FObject materialObject)
    {
        object material = materialObject?.LoadObject<UObject>();
        return material?.GetType().Name ?? "<unresolved>";
    }

    private static IReadOnlyList<MaterialInspectorMaterialNode> BuildMaterialChain(FObject materialObject)
    {
        List<MaterialInspectorMaterialNode> chain = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        FObject current = materialObject;

        while (current != null)
        {
            string path = current.GetPathName() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path) && !seen.Add(path))
                break;

            object resolved = current.LoadObject<UObject>();
            if (resolved == null)
            {
                chain.Add(new MaterialInspectorMaterialNode
                {
                    Path = string.IsNullOrWhiteSpace(path) ? "<unresolved>" : path,
                    TypeName = "<unresolved>"
                });
                break;
            }

            if (resolved is UMaterialInstanceConstant instanceConstant)
            {
                UMaterial parentMaterial = instanceConstant.Parent?.LoadObject<UMaterial>();
                chain.Add(new MaterialInspectorMaterialNode
                {
                    Path = path,
                    TypeName = nameof(UMaterialInstanceConstant),
                    BlendMode = parentMaterial?.BlendMode,
                    TwoSided = parentMaterial?.TwoSided,
                    TextureParameters = (instanceConstant.TextureParameterValues ?? []).Select(static parameter => new MaterialInspectorTextureParameter
                    {
                        Name = parameter.ParameterName?.Name ?? "<unnamed>",
                        TexturePath = parameter.ParameterValue?.GetPathName() ?? "<null>"
                    }).OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                    ScalarParameters = (instanceConstant.ScalarParameterValues ?? []).Select(static parameter => new MaterialInspectorScalarParameter
                    {
                        Name = parameter.ParameterName?.Name ?? "<unnamed>",
                        Value = parameter.ParameterValue
                    }).OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                    VectorParameters = (instanceConstant.VectorParameterValues ?? []).Select(static parameter => new MaterialInspectorVectorParameter
                    {
                        Name = parameter.ParameterName?.Name ?? "<unnamed>",
                        Value = parameter.ParameterValue.ToVector3()
                    }).OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).ToList()
                });
                current = instanceConstant.Parent;
                continue;
            }

            if (resolved is UMaterialInstance instance)
            {
                chain.Add(new MaterialInspectorMaterialNode
                {
                    Path = path,
                    TypeName = nameof(UMaterialInstance)
                });
                current = instance.Parent;
                continue;
            }

            if (resolved is UMaterial material)
            {
                chain.Add(new MaterialInspectorMaterialNode
                {
                    Path = path,
                    TypeName = nameof(UMaterial),
                    BlendMode = material.BlendMode,
                    TwoSided = material.TwoSided,
                    UniformExpressionTextures = CollectUniformExpressionTextures(material)
                });
                break;
            }

            chain.Add(new MaterialInspectorMaterialNode
            {
                Path = path,
                TypeName = resolved.GetType().Name
            });
            break;
        }

        return chain;
    }

    // Walks UMaterial.MaterialResource[0..].UniformExpressionTextures (the
    // per-material cooked texture array indexed by the compiled shader's
    // TextureIndex). Classifies each texture by name-suffix into a slot
    // label (Diffuse/Normal/Specular/Emissive/Cube/Other) so the Material
    // Editor can show the actual textures a base UMaterial paints with —
    // these surface only when the Expression graph was stripped at cook
    // time and TextureParameterValues isn't available (no MIC override).
    private static IReadOnlyList<MaterialInspectorTextureParameter> CollectUniformExpressionTextures(UMaterial material)
    {
        if (material?.MaterialResource == null) return Array.Empty<MaterialInspectorTextureParameter>();
        List<FObject> pool = new();
        foreach (FMaterialResource quality in material.MaterialResource)
        {
            if (quality?.UniformExpressionTextures == null) continue;
            foreach (FObject t in quality.UniformExpressionTextures)
            {
                if (t == null) continue;
                if (pool.Any(p => string.Equals(p.GetPathName(), t.GetPathName(), StringComparison.OrdinalIgnoreCase))) continue;
                pool.Add(t);
            }
            if (pool.Count > 0) break;   // first non-empty quality level
        }
        if (pool.Count == 0) return Array.Empty<MaterialInspectorTextureParameter>();

        var result = new List<MaterialInspectorTextureParameter>(pool.Count);
        foreach (var t in pool)
            result.Add(new MaterialInspectorTextureParameter { Name = ClassifySlot(t.GetPathName()), TexturePath = t.GetPathName() ?? "<null>" });
        return result;
    }

    private static string ClassifySlot(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "Other";
        string lp = fullPath.ToLowerInvariant();
        if (lp.Contains("cube") || lp.Contains("reflection") || lp.EndsWith("_env") || lp.Contains("_refl")) return "Cube/Refl";
        if (lp.EndsWith("_nrml") || lp.EndsWith("_nrm") || lp.EndsWith("_norm") || lp.EndsWith("_n") || lp.Contains("normal") || lp.Contains("bump")) return "Normal";
        if (lp.EndsWith("_spec") || lp.EndsWith("_s") || lp.Contains("specular")) return "Specular";
        if (lp.Contains("emissive") || lp.Contains("emit") || lp.Contains("selfillum")) return "Emissive";
        if (lp.EndsWith("_diff") || lp.EndsWith("_d") || lp.Contains("diffuse") || lp.EndsWith("_color") || lp.EndsWith("_albedo")) return "Diffuse";
        return "Other";   // candidate for primary diffuse (no suffix)
    }
}

