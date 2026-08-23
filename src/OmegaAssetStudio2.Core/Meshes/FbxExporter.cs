using System.Numerics;
using Assimp;
using OmegaAssetStudio2.Core.Retargeting;
using AssimpMatrix = Assimp.Matrix4x4;
using AssimpMesh = Assimp.Mesh;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>Why a model could not be saved to a file.</summary>
public sealed class MeshExportException : Exception
{
    public MeshExportException(string message) : base(message) { }
}

/// <summary>
/// Saves a model from the game to a file a modelling tool can open.
/// </summary>
/// <remarks>
/// The exact reverse of reading one in, and deliberately so: a model taken out
/// here, opened in a modelling tool, and brought straight back must come back
/// as it left. Every step the reader takes is undone — the two upright axes are
/// exchanged again, the second texture coordinate is turned back round, and the
/// triangles are wound the other way — and each of those is its own opposite,
/// so the pair cancels exactly.
/// <para>
/// The skeleton goes with it, with each bone's place and the pose the skin was
/// bound in, so the model arrives skinned rather than as a bare shell.
/// </para>
/// </remarks>
public static class FbxExporter
{
    /// <summary>Extensions that can be written, for a file picker to offer.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".fbx", ".dae", ".obj"];

    /// <summary>
    /// Writes a model to a file.
    /// </summary>
    /// <param name="path">Where to write it. Its extension chooses the format.</param>
    /// <param name="mesh">The model, for its skeleton and its materials.</param>
    /// <param name="lod">Which level of detail to write.</param>
    /// <param name="materialNames">
    /// What to call each material, in the model's own order. The names live in
    /// the package rather than in the model, so they are passed in by whoever
    /// resolved them.
    /// </param>
    public static void Write(
        string path, SkeletalMesh mesh, SkeletalMeshLod lod, IReadOnlyList<string>? materialNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(lod);

        if (!lod.HasGeometry) throw new MeshExportException("This level of detail holds no geometry.");

        var scene = new Scene { RootNode = new Node("Scene") };

        SkeletonPose rest = SkeletonPose.Rest(mesh.Bones);

        Node[] boneNodes = BuildSkeleton(scene, mesh, rest);

        // One model per section, so each keeps its own material rather than
        // being merged into a single lump that cannot be told apart again.
        IReadOnlyList<MeshSection> sections = lod.Sections.Count > 0
            ? lod.Sections
            : [new MeshSection { MaterialIndex = 0, ChunkIndex = 0, BaseIndex = 0, TriangleCount = lod.TriangleCount }];

        for (int s = 0; s < sections.Count; s++)
        {
            AssimpMesh? part = BuildSection(mesh, lod, sections[s], rest, s);
            if (part is null) continue;

            part.MaterialIndex = scene.MaterialCount;

            scene.Materials.Add(new Material
            {
                Name = materialNames is not null && sections[s].MaterialIndex < materialNames.Count
                    ? materialNames[sections[s].MaterialIndex]
                    : $"material{sections[s].MaterialIndex}",
            });

            scene.Meshes.Add(part);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }

        if (scene.MeshCount == 0) throw new MeshExportException("This model has no triangles to write.");

        _ = boneNodes;

        Save(scene, path);
    }

    private static void Save(Scene scene, string path)
    {
        string format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dae" => "collada",
            ".obj" => "obj",
            _ => "fbx",
        };

        using var exporter = new AssimpContext();

        try
        {
            if (!exporter.ExportFile(scene, path, format))
                throw new MeshExportException($"The model could not be written as {format}.");
        }
        catch (AssimpException ex)
        {
            throw new MeshExportException($"The model could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a node for every bone, arranged as the skeleton is.
    /// </summary>
    /// <remarks>
    /// A node carries where it sits relative to its parent, so each bone's
    /// place in the model is turned back into a step down from the one above
    /// it.
    /// </remarks>
    private static Node[] BuildSkeleton(Scene scene, SkeletalMesh mesh, SkeletonPose rest)
    {
        var nodes = new Node[mesh.Bones.Count];

        for (int b = 0; b < mesh.Bones.Count; b++) nodes[b] = new Node(mesh.Bones[b].Name);

        for (int b = 0; b < mesh.Bones.Count; b++)
        {
            int parent = mesh.Bones[b].ParentIndex;
            bool isRoot = b == 0 || parent < 0 || parent == b || parent >= mesh.Bones.Count;

            Matrix4x4 local = isRoot
                ? rest.BoneToModel[b]
                : rest.BoneToModel[b] * rest.ModelToBone[parent];

            nodes[b].Transform = ToAssimp(ToFileSpace(local));

            if (isRoot) scene.RootNode.Children.Add(nodes[b]);
            else nodes[parent].Children.Add(nodes[b]);
        }

        return nodes;
    }

    /// <summary>
    /// Builds one drawn part, with only the vertices its triangles use.
    /// </summary>
    private static AssimpMesh? BuildSection(
        SkeletalMesh mesh, SkeletalMeshLod lod, MeshSection section, SkeletonPose rest, int number)
    {
        int firstCorner = section.BaseIndex;
        int corners = section.TriangleCount * 3;

        if (firstCorner < 0 || corners <= 0 || firstCorner + corners > lod.Indices.Count) return null;

        var used = new Dictionary<int, int>();
        var order = new List<int>();

        for (int c = firstCorner; c < firstCorner + corners; c++)
        {
            int vertex = lod.Indices[c];

            if (used.TryAdd(vertex, order.Count)) order.Add(vertex);
        }

        var part = new AssimpMesh($"part{number}", PrimitiveType.Triangle);

        foreach (int v in order)
        {
            Vector3 position = ToFileSpace(lod.Positions[v]);
            Vector3 normal = ToFileSpace(v < lod.Normals.Count ? lod.Normals[v] : Vector3.UnitZ);

            part.Vertices.Add(new Vector3D(position.X, position.Y, position.Z));
            part.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));

            Vector2 uv = v < lod.TexCoords.Count ? lod.TexCoords[v] : Vector2.Zero;

            // Turned back the way files store it, which is how it was read.
            part.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, 1f - uv.Y, 0f));
        }

        part.UVComponentCount[0] = 2;

        // Left wound as the game winds them. Exchanging the two upright axes
        // already turns every triangle over, and reading one back in exchanges
        // them again — so the pair cancels with no reversal at either end.
        // Reversing here as well sent the model back inside out: measured on a
        // round trip, all 7,414 triangles came home facing the wrong way.
        for (int c = firstCorner; c + 2 < firstCorner + corners; c += 3)
        {
            part.Faces.Add(new Face([
                used[lod.Indices[c]],
                used[lod.Indices[c + 1]],
                used[lod.Indices[c + 2]],
            ]));
        }

        AddSkinning(part, mesh, lod, order, rest);

        return part;
    }

    /// <summary>
    /// Attaches the skin: which bones pull on each vertex, and the pose the
    /// weights were measured in.
    /// </summary>
    private static void AddSkinning(
        AssimpMesh part, SkeletalMesh mesh, SkeletalMeshLod lod, List<int> order, SkeletonPose rest)
    {
        var byBone = new Dictionary<int, Bone>();

        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            if (v >= lod.Influences.Count) continue;

            VertexInfluence influence = lod.Influences[v];

            for (int w = 0; w < influence.Bones.Count; w++)
            {
                int bone = influence.Bones[w];
                float weight = influence.Weights[w];

                if (weight <= 0f || bone < 0 || bone >= mesh.Bones.Count) continue;

                if (!byBone.TryGetValue(bone, out Bone? entry))
                {
                    entry = new Bone
                    {
                        Name = mesh.Bones[bone].Name,

                        // The move that takes the model into this bone's own
                        // space, which is how a file records where the skin was
                        // bound.
                        OffsetMatrix = ToAssimp(ToFileSpace(rest.ModelToBone[bone])),
                    };

                    byBone[bone] = entry;
                }

                entry.VertexWeights.Add(new VertexWeight(i, weight));
            }
        }

        // Every bone gets an entry, even one nothing is weighted to. A
        // modelling tool decides what is a bone and what is a stray marker by
        // whether the skin reaches it: bones with no weights arrive as plain
        // objects instead, and cannot then be weighted to, painted on, or
        // exported as bones. A whole leg chain came back that way from one real
        // round trip, which read as the legs being missing when they were
        // simply not recognised.
        for (int b = 0; b < mesh.Bones.Count; b++)
        {
            if (byBone.ContainsKey(b)) continue;

            byBone[b] = new Bone
            {
                Name = mesh.Bones[b].Name,
                OffsetMatrix = ToAssimp(ToFileSpace(rest.ModelToBone[b])),
            };
        }

        foreach (Bone bone in byBone.OrderBy(p => p.Key).Select(p => p.Value)) part.Bones.Add(bone);
    }

    /// <summary>
    /// Turns a place from the game's space back into the way files store it.
    /// </summary>
    /// <remarks>
    /// The same exchange the reader makes. Doing it twice returns what was
    /// started with, which is what lets a model go out and come back unchanged.
    /// </remarks>
    private static Vector3 ToFileSpace(Vector3 value) => new(value.X, value.Z, value.Y);

    /// <summary>The same exchange, applied to a whole transform.</summary>
    private static Matrix4x4 ToFileSpace(Matrix4x4 m) => new(
        m.M11, m.M13, m.M12, m.M14,
        m.M31, m.M33, m.M32, m.M34,
        m.M21, m.M23, m.M22, m.M24,
        m.M41, m.M43, m.M42, m.M44);

    /// <summary>
    /// Hands a transform over the way this library expects it, which is the
    /// other way round from how it is held here.
    /// </summary>
    private static AssimpMatrix ToAssimp(Matrix4x4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44);
}
