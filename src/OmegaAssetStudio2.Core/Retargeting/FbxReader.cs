using System.Numerics;
using Assimp;
using AssimpMatrix = Assimp.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using AssimpMesh = Assimp.Mesh;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>
/// Reads a model out of the interchange formats modelling tools save.
/// </summary>
/// <remarks>
/// A file of this kind holds a whole scene: several models, a tree of nodes
/// positioning them, and a skeleton whose bones are nodes in that same tree.
/// What comes out here is one model with one skeleton, because that is what a
/// retarget acts on.
/// <para>
/// Positions are taken as they are stored, without turning the model upright or
/// scaling it. Tools disagree about which way is up, and quietly correcting for
/// one of them moves every model saved by the others.
/// </para>
/// </remarks>
public static class FbxReader
{
    /// <summary>Reads a model from a file.</summary>
    public static ImportedMesh Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("The file could not be found.", path);

        using var importer = new AssimpContext();

        Scene scene;

        try
        {
            // Triangulated because a retarget works in triangles, and limited to
            // four bones a vertex because that is all the game's own format
            // stores. Both are done here rather than later, so what is shown is
            // what would be kept.
            importer.SetConfig(new Assimp.Configs.VertexBoneWeightLimitConfig(4));

            // Corners that are identical in every respect are joined back
            // together. Files often arrive with every triangle carrying its own
            // three corners, which triples the vertex count and — more to the
            // point — stops the model matching the object it came from. On a
            // real file this brought 22,242 corners back to the 4,553 the game
            // itself stores, for the same 7,414 triangles.
            scene = importer.ImportFile(
                path,
                PostProcessSteps.Triangulate |
                PostProcessSteps.JoinIdenticalVertices |
                PostProcessSteps.LimitBoneWeights |
                PostProcessSteps.GenerateSmoothNormals |
                PostProcessSteps.CalculateTangentSpace |
                PostProcessSteps.ValidateDataStructure);
        }
        catch (AssimpException ex)
        {
            throw new InvalidMeshFileException($"This file could not be read: {ex.Message}");
        }

        if (scene is null || !scene.HasMeshes)
            throw new InvalidMeshFileException("This file holds no model.");

        return Build(scene);
    }

    private static ImportedMesh Build(Scene scene)
    {
        var positions = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector3>();
        var bitangents = new List<Vector3>();
        var wedgePoints = new List<int>();
        var indices = new List<int>();
        var materials = new List<string>();
        var weights = new Dictionary<int, List<(int Bone, float Weight)>>();

        // The skeleton is built from the whole scene, not per model, so two
        // models skinned to the same skeleton agree about which bone is which.
        (List<ImportedBone> bones, Dictionary<string, int> boneByName) = BuildSkeleton(scene);

        // Where each bone stood when the skin was bound to it. This is the pose
        // the weights mean, and it is recorded separately from the scene's node
        // arrangement precisely because the two need not agree.
        ApplyBindPoses(scene, bones, boneByName);

        foreach ((AssimpMesh mesh, Matrix4x4 placement) in Placed(scene))
        {
            int firstPoint = positions.Count;

            // A file holds a model's vertices in its own space, and the scene's
            // tree of nodes says where that space sits. Reading them without
            // applying it leaves the model wherever its exporter happened to
            // put it — turned, moved, or scaled — and nothing later puts that
            // right.
            foreach (Vector3D vertex in mesh.Vertices)
            {
                positions.Add(ToGameSpace(
                    Vector3.Transform(new Vector3(vertex.X, vertex.Y, vertex.Z), placement)));
            }

            List<Vector3D>? uvs = mesh.HasTextureCoords(0) ? mesh.TextureCoordinateChannels[0] : null;

            // Directions are carried through the same placement as the points,
            // but without its movement or its scale.
            Matrix4x4 forDirections = Matrix4x4.Invert(placement, out Matrix4x4 back)
                ? Matrix4x4.Transpose(back)
                : Matrix4x4.Identity;

            for (int v = 0; v < mesh.VertexCount; v++)
            {
                wedgePoints.Add(firstPoint + v);

                normals.Add(Direction(mesh.HasNormals ? mesh.Normals[v] : default, forDirections));

                tangents.Add(Direction(
                    mesh.HasTangentBasis ? mesh.Tangents[v] : default, forDirections));

                bitangents.Add(Direction(
                    mesh.HasTangentBasis ? mesh.BiTangents[v] : default, forDirections));

                // The second texture coordinate runs the opposite way here to
                // the way the game stores it.
                texCoords.Add(uvs is not null && v < uvs.Count
                    ? new Vector2(uvs[v].X, 1f - uvs[v].Y)
                    : Vector2.Zero);
            }

            // Which way round a triangle's corners go. Measured on the game's
            // own model rather than reasoned about: of its 7,414 triangles, not
            // one is wound the same way its vertices face — every single one
            // goes the other way. Exchanging two axes to reach the game's space
            // already turns every triangle over, which lands on that convention
            // with no further flip; a node that mirrors its model turns them
            // over again and has to be undone.
            //
            // Getting this backwards is not a small thing. The surface culls
            // inside out, and the only way to correct it is the option that
            // also reverses the directions the surface faces — which lights
            // every material as though from behind, and looks like a white film
            // over the whole model.
            bool reverse = placement.GetDeterminant() < 0f;

            foreach (Face face in mesh.Faces)
            {
                // Anything not a triangle is skipped rather than guessed at;
                // the file was asked for triangles, so this is a stray.
                if (face.IndexCount != 3) continue;

                indices.Add(firstPoint + face.Indices[0]);
                indices.Add(firstPoint + face.Indices[reverse ? 2 : 1]);
                indices.Add(firstPoint + face.Indices[reverse ? 1 : 2]);
            }

            foreach (Bone bone in mesh.Bones)
            {
                if (!boneByName.TryGetValue(bone.Name, out int index)) continue;

                foreach (VertexWeight weight in bone.VertexWeights)
                {
                    if (weight.Weight <= 0f) continue;

                    int point = firstPoint + weight.VertexID;

                    if (!weights.TryGetValue(point, out List<(int, float)>? list))
                    {
                        list = [];
                        weights[point] = list;
                    }

                    list.Add((index, weight.Weight));
                }
            }

            materials.Add(MaterialName(scene, mesh));
        }

        return new ImportedMesh
        {
            Positions = positions,
            TexCoords = texCoords,
            Normals = normals,
            Tangents = tangents,
            Bitangents = bitangents,
            WedgePoints = wedgePoints,
            Indices = indices,
            Materials = materials,
            Bones = bones,
            Weights = Gather(weights, positions.Count),
        };
    }

    /// <summary>
    /// Every model in the scene, with the transform that places it.
    /// </summary>
    /// <remarks>
    /// A model can be mentioned by more than one node, and each mention places
    /// it differently, which is why this walks the tree rather than reading the
    /// scene's flat list of models.
    /// </remarks>
    private static IEnumerable<(AssimpMesh Mesh, Matrix4x4 Placement)> Placed(Scene scene)
    {
        if (scene.RootNode is null) yield break;

        foreach ((AssimpMesh mesh, Matrix4x4 placement) in Under(scene, scene.RootNode, Matrix4x4.Identity))
            yield return (mesh, placement);
    }

    /// <summary>
    /// One node's models and everything below it, in the order the file lists
    /// them.
    /// </summary>
    /// <remarks>
    /// Order matters. A model made of several parts has them read back in the
    /// order they appear, and reversing that — which is what walking the tree
    /// with a stack does to every set of children — silently rearranges a
    /// model's parts.
    /// </remarks>
    private static IEnumerable<(AssimpMesh Mesh, Matrix4x4 Placement)> Under(
        Scene scene, Node node, Matrix4x4 parent)
    {
        Matrix4x4 here = parent * ToMatrix(node.Transform);

        foreach (int index in node.MeshIndices)
        {
            if (index >= 0 && index < scene.MeshCount) yield return (scene.Meshes[index], here);
        }

        foreach (Node child in node.Children)
        {
            foreach ((AssimpMesh mesh, Matrix4x4 placement) in Under(scene, child, here))
                yield return (mesh, placement);
        }
    }

    /// <summary>
    /// Turns a direction or a place from the way files store it into the way
    /// the game does.
    /// </summary>
    /// <remarks>
    /// The two upright axes are exchanged. This is a fixed convention, not
    /// something to measure: version 1's importer, which produces models this
    /// game draws correctly, applies exactly this to every position, normal and
    /// tangent it reads. Working it out from the skeleton instead — which this
    /// used to do — gets it right only when the two rigs disagree enough to be
    /// noticed, and a model exported from the very character it is going back
    /// into does not.
    /// </remarks>
    private static Vector3 ToGameSpace(Vector3 value) => new(value.X, value.Z, value.Y);

    /// <summary>One direction, placed and turned into the game's space.</summary>
    private static Vector3 Direction(Vector3D value, Matrix4x4 placement)
    {
        Vector3 turned = ToGameSpace(Vector3.TransformNormal(
            new Vector3(value.X, value.Y, value.Z), placement));

        return turned.LengthSquared() > 1e-10f ? Vector3.Normalize(turned) : Vector3.Zero;
    }

    /// <summary>
    /// Builds the skeleton from the scene's own tree of nodes.
    /// </summary>
    /// <remarks>
    /// The bones a model names are nodes in the scene, and their arrangement is
    /// the tree itself. Walking it in order puts every parent before its child,
    /// which is what everything downstream relies on.
    /// <para>
    /// Only nodes that lead to a bone are kept. A scene carries cameras, lights
    /// and empty groupings as nodes too, and keeping those would pad the
    /// skeleton with things nothing follows.
    /// </para>
    /// </remarks>
    private static (List<ImportedBone> Bones, Dictionary<string, int> ByName) BuildSkeleton(Scene scene)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (AssimpMesh mesh in scene.Meshes)
        {
            foreach (Bone bone in mesh.Bones) wanted.Add(bone.Name);
        }

        var bones = new List<ImportedBone>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);

        if (wanted.Count == 0 || scene.RootNode is null) return (bones, byName);

        // A bone's parents have to come with it, or the chain is broken.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        MarkKept(scene.RootNode, wanted, keep);

        // And so do its brothers and their children, weighted or not. A bone
        // only appears among a model's own bones when something is weighted to
        // it, so keeping just those throws away every bone the skin does not
        // happen to use — and then reports them missing. One real file was read
        // as having no legs at all: the leg bones were there, with nothing
        // weighted to them, which is a fault in the file worth naming rather
        // than a skeleton worth quietly shortening.
        KeepWholeSkeleton(scene.RootNode, keep);

        Walk(scene.RootNode, parent: -1, keep, bones, byName);

        return (bones, byName);
    }

    /// <summary>
    /// Records where each bone stood when the model was bound to it.
    /// </summary>
    /// <remarks>
    /// A file stores this the other way round — as the move that takes the
    /// model into the bone's own space — so it is turned around here to give
    /// where the bone was.
    /// </remarks>
    private static void ApplyBindPoses(
        Scene scene, List<ImportedBone> bones, Dictionary<string, int> byName)
    {
        foreach (AssimpMesh mesh in scene.Meshes)
        {
            foreach (Bone bone in mesh.Bones)
            {
                if (!byName.TryGetValue(bone.Name, out int index)) continue;
                if (bones[index].BindPose is not null) continue;

                Matrix4x4 intoBone = ToMatrix(bone.OffsetMatrix);

                if (!Matrix4x4.Invert(intoBone, out Matrix4x4 boneToModel)) continue;

                bones[index] = bones[index] with { BindPose = boneToModel };
            }
        }
    }

    private static Matrix4x4 ToMatrix(AssimpMatrix transform) => new(
        transform.A1, transform.B1, transform.C1, transform.D1,
        transform.A2, transform.B2, transform.C2, transform.D2,
        transform.A3, transform.B3, transform.C3, transform.D3,
        transform.A4, transform.B4, transform.C4, transform.D4);

    /// <summary>
    /// Keeps everything below a node that is already being kept.
    /// </summary>
    /// <remarks>
    /// The bones a skin uses mark out the skeleton; everything hanging off them
    /// belongs to it too, whether or not the skin reaches that far.
    /// </remarks>
    private static void KeepWholeSkeleton(Node node, HashSet<string> keep)
    {
        bool inside = keep.Contains(node.Name);

        foreach (Node child in node.Children)
        {
            // A model hangs off nodes too, and those are not bones.
            if (inside && child.MeshIndices.Count == 0) keep.Add(child.Name);

            KeepWholeSkeleton(child, keep);
        }
    }

    /// <summary>Marks a node when it, or anything under it, is a bone.</summary>
    private static bool MarkKept(Node node, HashSet<string> wanted, HashSet<string> keep)
    {
        bool needed = wanted.Contains(node.Name);

        foreach (Node child in node.Children)
        {
            if (MarkKept(child, wanted, keep)) needed = true;
        }

        if (needed) keep.Add(node.Name);

        return needed;
    }

    private static void Walk(
        Node node, int parent, HashSet<string> keep, List<ImportedBone> bones, Dictionary<string, int> byName)
    {
        int index = parent;

        if (keep.Contains(node.Name))
        {
            (Quaternion rotation, Vector3 position) = Decompose(node.Transform);

            index = bones.Count;

            bones.Add(new ImportedBone
            {
                Name = node.Name,

                // The root names itself, which is how the game's own skeletons
                // record it.
                ParentIndex = parent < 0 ? 0 : parent,
                Orientation = rotation,
                Position = position,
            });

            byName.TryAdd(node.Name, index);
        }

        foreach (Node child in node.Children) Walk(child, index, keep, bones, byName);
    }

    /// <summary>
    /// Takes a node's rotation and position out of its transform.
    /// </summary>
    /// <remarks>
    /// Any scaling is dropped. A skeleton carries none in the game's format, and
    /// a bone scaled here would stretch everything hanging off it.
    /// </remarks>
    private static (Quaternion Rotation, Vector3 Position) Decompose(AssimpMatrix transform)
    {
        var matrix = new Matrix4x4(
            transform.A1, transform.B1, transform.C1, transform.D1,
            transform.A2, transform.B2, transform.C2, transform.D2,
            transform.A3, transform.B3, transform.C3, transform.D3,
            transform.A4, transform.B4, transform.C4, transform.D4);

        if (!Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 position))
        {
            // A transform that cannot be taken apart — mirrored or collapsed —
            // keeps its position, which is better than losing the bone.
            return (Quaternion.Identity, matrix.Translation);
        }

        return (rotation, position);
    }

    private static string MaterialName(Scene scene, AssimpMesh mesh)
    {
        if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= scene.MaterialCount)
            return mesh.Name.Length > 0 ? mesh.Name : "material";

        Material material = scene.Materials[mesh.MaterialIndex];

        return material.HasName && material.Name.Length > 0 ? material.Name : $"material{mesh.MaterialIndex}";
    }

    /// <summary>Gathers each point's bones, strongest first, adding to one.</summary>
    private static IReadOnlyList<IReadOnlyList<(int Bone, float Weight)>> Gather(
        Dictionary<int, List<(int Bone, float Weight)>> weights, int pointCount)
    {
        var result = new List<(int Bone, float Weight)>[pointCount];

        for (int p = 0; p < pointCount; p++)
        {
            if (!weights.TryGetValue(p, out List<(int Bone, float Weight)>? list))
            {
                result[p] = [];
                continue;
            }

            list.Sort((a, b) => b.Weight.CompareTo(a.Weight));

            float total = 0f;
            foreach ((_, float weight) in list) total += weight;

            if (total > 0.0001f)
            {
                for (int i = 0; i < list.Count; i++)
                    list[i] = (list[i].Bone, list[i].Weight / total);
            }

            result[p] = list;
        }

        return result;
    }
}

/// <summary>Reads a model from whichever kind of file it is.</summary>
public static class MeshFile
{
    /// <summary>Extensions that can be opened, for a file picker to offer.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".fbx", ".psk", ".pskx", ".obj", ".dae", ".gltf", ".glb"];

    /// <summary>Reads a model, choosing how by the file's extension.</summary>
    public static ImportedMesh Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string extension = Path.GetExtension(path);

        return extension.Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pskx", StringComparison.OrdinalIgnoreCase)
            ? PskReader.Read(path)
            : FbxReader.Read(path);
    }
}
