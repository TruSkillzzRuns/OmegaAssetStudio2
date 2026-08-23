using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One triangle of a model's collision shape.</summary>
/// <param name="VertexA">First corner, as an index into the collision vertices.</param>
/// <param name="VertexB">Second corner.</param>
/// <param name="VertexC">Third corner.</param>
/// <param name="MaterialIndex">Which of the model's materials this face uses.</param>
public readonly record struct CollisionTriangle(
    ushort VertexA, ushort VertexB, ushort VertexC, ushort MaterialIndex);

/// <summary>
/// The collision shape stored alongside a model: a bounding-volume tree and the
/// triangles it indexes.
/// </summary>
/// <remarks>
/// Layout derived by walking the payload and confirmed three ways. The two arrays
/// tile with no gap between them; their element sizes match a compact tree node
/// (six bytes) and a triangle of three sixteen-bit indices plus a material index
/// (eight bytes); and a mesh that is a plain 256-unit cube reports exactly twelve
/// triangles, which is two per face across six faces.
/// <para>
/// This is the collision shape, not the rendered surface. The visible geometry
/// lives further into the payload in a different structure that has not been
/// derived yet.
/// </para>
/// </remarks>
public sealed class CollisionMesh
{
    /// <summary>Bytes per bounding-volume tree node.</summary>
    private const int NodeSize = 6;

    /// <summary>Bytes per triangle.</summary>
    private const int TriangleSize = 8;

    /// <summary>Bytes of axis-aligned box that follow the mesh bounds.</summary>
    private const int RootBoxSize = sizeof(float) * 6;

    /// <summary>An identifier sits between the mesh bounds and the root box.</summary>
    private const int IdentifierSize = sizeof(int);

    private CollisionMesh(int nodeCount, IReadOnlyList<CollisionTriangle> triangles, int endOffset)
    {
        NodeCount = nodeCount;
        Triangles = triangles;
        EndOffset = endOffset;
    }

    /// <summary>How many nodes the bounding-volume tree has.</summary>
    public int NodeCount { get; }

    public IReadOnlyList<CollisionTriangle> Triangles { get; }

    /// <summary>Offset just past the collision data, where the render data begins.</summary>
    public int EndOffset { get; }

    /// <summary>Distinct material indices the triangles reference.</summary>
    public IReadOnlyList<int> MaterialIndices => Triangles
        .Select(t => (int)t.MaterialIndex)
        .Distinct()
        .Order()
        .ToList();

    /// <summary>
    /// Reads the collision shape that follows a mesh's bounds.
    /// </summary>
    /// <param name="data">The mesh export's bytes.</param>
    /// <param name="boundsOffset">Where the mesh bounds begin.</param>
    /// <returns>The collision shape, or null when it does not parse.</returns>
    public static CollisionMesh? TryRead(ReadOnlySpan<byte> data, int boundsOffset)
    {
        int at = boundsOffset + MeshBounds.ByteSize + IdentifierSize + RootBoxSize;

        if (at + 8 > data.Length) return null;

        int nodeSize = BitConverter.ToInt32(data[at..]);
        int nodeCount = BitConverter.ToInt32(data[(at + 4)..]);

        // The element size is fixed by the format. Anything else means the walk
        // has landed somewhere it should not have.
        if (nodeSize != NodeSize || nodeCount < 0) return null;

        long nodesEnd = at + 8L + ((long)nodeCount * NodeSize);
        if (nodesEnd + 8 > data.Length) return null;

        int triangleSize = BitConverter.ToInt32(data[(int)nodesEnd..]);
        int triangleCount = BitConverter.ToInt32(data[(int)(nodesEnd + 4)..]);

        if (triangleSize != TriangleSize || triangleCount < 0) return null;

        long trianglesEnd = nodesEnd + 8 + ((long)triangleCount * TriangleSize);
        if (trianglesEnd > data.Length) return null;

        var triangles = new CollisionTriangle[triangleCount];
        int cursor = (int)nodesEnd + 8;

        for (int i = 0; i < triangleCount; i++)
        {
            triangles[i] = new CollisionTriangle(
                BitConverter.ToUInt16(data[cursor..]),
                BitConverter.ToUInt16(data[(cursor + 2)..]),
                BitConverter.ToUInt16(data[(cursor + 4)..]),
                BitConverter.ToUInt16(data[(cursor + 6)..]));

            cursor += TriangleSize;
        }

        return new CollisionMesh(nodeCount, triangles, (int)trianglesEnd);
    }
}
