using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>What kind of model an entry is.</summary>
public enum MeshKind
{
    Static,
    Skeletal,
}

/// <summary>
/// What a mesh declares about itself.
/// </summary>
/// <remarks>
/// Covers the parts of a mesh that are reliably readable: its identity, the space
/// it occupies, and the materials it references. The packed vertex and index data
/// that follows is not decoded — see the notes on <see cref="MeshCatalog"/>.
/// </remarks>
public sealed record MeshInfo
{
    public required string PackagePath { get; init; }
    public required int ExportIndex { get; init; }
    public required MeshKind Kind { get; init; }
    public required string Name { get; init; }
    public required string ObjectPath { get; init; }

    /// <summary>Total bytes the object occupies in the package.</summary>
    public required int DataSize { get; init; }

    /// <summary>The volume it occupies, when that could be read.</summary>
    public required MeshBounds? Bounds { get; init; }

    /// <summary>Materials it references, by object path.</summary>
    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>Physics body it uses, when it has one.</summary>
    public required string PhysicsBody { get; init; }

    /// <summary>
    /// Triangles in the model's collision shape, when it could be read. This is
    /// the shape the game collides against, not the rendered surface.
    /// </summary>
    public required int CollisionTriangleCount { get; init; }

    /// <summary>Distinct material slots the collision triangles reference.</summary>
    public required int CollisionMaterialCount { get; init; }

    public bool HasBounds => Bounds is { IsPlausible: true };

    public bool HasCollision => CollisionTriangleCount > 0;

    public override string ToString() => $"{Name} ({Kind}, {Materials.Count} materials)";
}

/// <summary>
/// Reads what can be read reliably from a mesh object.
/// </summary>
public static class MeshReader
{
    public const string StaticMeshClass = "staticmesh";
    public const string SkeletalMeshClass = "skeletalmesh";

    /// <summary>
    /// Reads a mesh export. Returns null when its properties do not parse.
    /// </summary>
    public static MeshInfo? TryRead(Package package, int exportIndex, MeshKind kind)
    {
        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);

        MeshBounds? bounds = null;
        if (properties.PayloadOffset + MeshBounds.ByteSize <= data.Length)
        {
            MeshBounds candidate = MeshBounds.Read(data, properties.PayloadOffset);

            // Only report bounds that make sense. Reporting nonsense would be
            // worse than reporting nothing, because it looks like data.
            if (candidate.IsPlausible) bounds = candidate;
        }

        // The collision shape sits immediately after the bounds, so it can only
        // be located when the bounds themselves were found.
        CollisionMesh? collision = bounds is null
            ? null
            : CollisionMesh.TryRead(data, properties.PayloadOffset);

        return new MeshInfo
        {
            PackagePath = package.Path,
            ExportIndex = exportIndex,
            Kind = kind,
            Name = package.GetExportName(exportIndex),
            ObjectPath = package.GetExportPath(exportIndex),
            DataSize = package.Exports[exportIndex].SerialSize,
            Bounds = bounds,
            Materials = ReadMaterials(package, exportIndex),
            PhysicsBody = package.ResolveName(properties.GetObject("BodySetup")),
            CollisionTriangleCount = collision?.Triangles.Count ?? 0,
            CollisionMaterialCount = collision?.MaterialIndices.Count ?? 0,
        };
    }

    /// <summary>
    /// Finds the materials a mesh uses.
    /// </summary>
    /// <remarks>
    /// A mesh's material list lives inside its packed section data rather than in
    /// its properties, so it is recovered from the package's import and export
    /// tables instead: any material the mesh's own package references is a
    /// candidate. That is broader than the true per-section list, so it is
    /// presented as "materials in this package", not as a section mapping.
    /// </remarks>
    private static IReadOnlyList<string> ReadMaterials(Package package, int exportIndex)
    {
        var materials = new List<string>();

        for (int i = 0; i < package.Imports.Count; i++)
        {
            ImportEntry import = package.Imports[i];
            string className = import.ClassName.Resolve(package.Names);

            if (className.Contains("material", StringComparison.OrdinalIgnoreCase))
                materials.Add(import.ObjectName.Resolve(package.Names));
        }

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i).Contains("material", StringComparison.OrdinalIgnoreCase))
                materials.Add(package.GetExportName(i));
        }

        return materials.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
