using System.Numerics;
using UpkManager.Models.UpkFile.Engine.Material;

namespace OmegaAssetStudio.MaterialInspector;

public sealed class MaterialInspectorResult
{
    public string UpkPath { get; init; } = string.Empty;
    public string SkeletalMeshExportPath { get; init; } = string.Empty;
    public IReadOnlyList<MaterialInspectorSectionInfo> Sections { get; init; } = Array.Empty<MaterialInspectorSectionInfo>();
}

public sealed class MaterialInspectorSectionInfo
{
    public int SectionIndex { get; init; }
    public int MaterialIndex { get; init; }
    public string MaterialPath { get; init; } = string.Empty;
    public string MaterialType { get; init; } = string.Empty;
    public IReadOnlyList<MaterialInspectorMaterialNode> MaterialChain { get; init; } = Array.Empty<MaterialInspectorMaterialNode>();
}

public sealed class MaterialInspectorMaterialNode
{
    public string Path { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public EBlendMode? BlendMode { get; init; }
    public bool? TwoSided { get; init; }
    public IReadOnlyList<MaterialInspectorTextureParameter> TextureParameters { get; init; } = Array.Empty<MaterialInspectorTextureParameter>();
    public IReadOnlyList<MaterialInspectorScalarParameter> ScalarParameters { get; init; } = Array.Empty<MaterialInspectorScalarParameter>();
    public IReadOnlyList<MaterialInspectorVectorParameter> VectorParameters { get; init; } = Array.Empty<MaterialInspectorVectorParameter>();

    // Cooked-material textures pulled from FMaterial.UniformExpressionTextures.
    // Populated when the chain bottoms out at a base UMaterial whose Expression
    // graph was stripped at cook time (cooked base shaders). The actual UTexture
    // refs survive in the material's per-resource array; we classify each by
    // name-suffix into a Slot label (Diffuse/Normal/Specular/Emissive/Other).
    public IReadOnlyList<MaterialInspectorTextureParameter> UniformExpressionTextures { get; init; } = Array.Empty<MaterialInspectorTextureParameter>();
}

public sealed class MaterialInspectorTextureParameter
{
    public string Name { get; init; } = string.Empty;
    public string TexturePath { get; init; } = string.Empty;
}

public sealed class MaterialInspectorScalarParameter
{
    public string Name { get; init; } = string.Empty;
    public float Value { get; init; }
}

public sealed class MaterialInspectorVectorParameter
{
    public string Name { get; init; } = string.Empty;
    public Vector3 Value { get; init; }
}

