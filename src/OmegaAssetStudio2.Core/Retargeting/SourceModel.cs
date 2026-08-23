using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>
/// A model brought in from a file, in the shape the rest of the tool works in.
/// </summary>
public sealed record SourceModel
{
    /// <summary>The geometry, one entry per drawn corner.</summary>
    public required SkeletalMeshLod Geometry { get; init; }

    /// <summary>The skeleton it was made for, or empty when it has none.</summary>
    public required IReadOnlyList<MeshBone> Bones { get; init; }

    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>How many bones the heaviest-bound corner follows.</summary>
    public required int MostInfluences { get; init; }

    /// <summary>
    /// How many bones came with a recorded bind pose, out of all of them.
    /// </summary>
    /// <remarks>
    /// Worth reporting, because which pose was used decides whether the model
    /// is fitted correctly, and it cannot be seen from the result alone.
    /// </remarks>
    public required int BonesWithBindPose { get; init; }

    /// <summary>
    /// Where the skeleton stood when the model was bound to it.
    /// </summary>
    /// <remarks>
    /// Taken from what the file records as its bind pose where it has one, and
    /// otherwise worked out by walking the bone chain. The two are not the same
    /// thing, and using the chain when a bind pose exists moves every vertex.
    /// </remarks>
    public required SkeletonPose Pose { get; init; }

    public bool HasSkeleton => Bones.Count > 0;

    public override string ToString() =>
        $"{Geometry.Positions.Count:N0} vertices, {Geometry.TriangleCount:N0} triangles, {Bones.Count} bones";
}

/// <summary>
/// Turns a model read from a file into the form the retarget works on.
/// </summary>
/// <remarks>
/// A file stores a model economically: positions are shared between corners
/// that sit in the same place, and each drawn corner points at one. Drawing and
/// skinning both need one entry per corner, so the two are joined here.
/// </remarks>
public static class SourceModelBuilder
{
    /// <summary>
    /// Builds a usable model from what was read out of a file.
    /// </summary>
    /// <param name="flipWinding">
    /// Reverses which way each triangle faces. Some tools write them the other
    /// way round, and the model then renders inside out.
    /// </param>
    public static SourceModel Build(ImportedMesh mesh, bool flipWinding = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int corners = mesh.WedgePoints.Count;

        var positions = new Vector3[corners];
        var texCoords = new Vector2[corners];
        var influences = new VertexInfluence[corners];

        int most = 0;

        for (int w = 0; w < corners; w++)
        {
            int point = mesh.WedgePoints[w];

            positions[w] = point >= 0 && point < mesh.Positions.Count
                ? mesh.Positions[point]
                : Vector3.Zero;

            texCoords[w] = w < mesh.TexCoords.Count ? mesh.TexCoords[w] : Vector2.Zero;

            IReadOnlyList<(int Bone, float Weight)> weights =
                point >= 0 && point < mesh.Weights.Count ? mesh.Weights[point] : [];

            var bones = new List<int>(weights.Count);
            var strengths = new List<float>(weights.Count);

            foreach ((int bone, float weight) in weights)
            {
                bones.Add(bone);
                strengths.Add(weight);
            }

            influences[w] = new VertexInfluence { Bones = bones, Weights = strengths };
            if (bones.Count > most) most = bones.Count;
        }

        IReadOnlyList<int> indices = Wind(mesh.Indices, flipWinding);

        // The surface frame as the file records it, rather than one worked out
        // from the triangles afterwards.
        bool haveFrames = mesh.Normals.Count >= corners && mesh.Tangents.Count >= corners;

        IReadOnlyList<Vector3> normals = mesh.Normals.Count >= corners
            ? mesh.Normals
            : BuildNormals(positions, indices);

        IReadOnlyList<byte> frames = haveFrames
            ? PackFrames(mesh, corners)
            : [];

        return new SourceModel
        {
            Geometry = new SkeletalMeshLod
            {
                Sections = [],
                Indices = indices,
                VertexCount = corners,
                Layout = default,
                Positions = positions,
                Normals = normals,
                TangentFrames = frames,
                TexCoords = texCoords,
                Influences = influences,
                Chunks = [],

                // This model came from a file of its own, not from bytes inside
                // a package, so there is nowhere to write it back to.
                VertexDataOffset = -1,
                PackedOrigin = Vector3.Zero,
                PackedExtension = Vector3.Zero,
            },
            Bones = mesh.Bones.Select(ToMeshBone).ToList(),
            Materials = mesh.Materials,
            MostInfluences = most,
            Pose = BuildPose(mesh),
            BonesWithBindPose = mesh.Bones.Count(b => b.BindPose is not null),
        };
    }

    /// <summary>
    /// The pose the weights mean: what the file recorded, or the bone chain
    /// when it recorded nothing.
    /// </summary>
    private static SkeletonPose BuildPose(ImportedMesh mesh)
    {
        List<MeshBone> bones = mesh.Bones.Select(ToMeshBone).ToList();

        if (mesh.Bones.Count == 0) return SkeletonPose.Rest(bones);

        // Not every bone carries a recorded bind pose, and demanding that all
        // of them do throws away the ones that do — which is how this first
        // went wrong. Only bones the skin actually hangs off are recorded; the
        // rest are there to keep the chain whole, a root or a joint between two
        // skinned ones, and those are placed relative to the recorded bone
        // above them.
        if (!mesh.Bones.Any(b => b.BindPose is not null)) return SkeletonPose.Rest(bones);

        var world = new Matrix4x4[mesh.Bones.Count];

        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            ImportedBone bone = mesh.Bones[i];

            if (bone.BindPose is { } bind)
            {
                world[i] = bind;
                continue;
            }

            Matrix4x4 local =
                Matrix4x4.CreateFromQuaternion(Safe(bone.Orientation)) *
                Matrix4x4.CreateTranslation(bone.Position);

            int parent = bone.ParentIndex;

            world[i] = parent >= 0 && parent < i ? local * world[parent] : local;
        }

        return SkeletonPose.FromBindPoses(world);
    }

    /// <summary>
    /// A rotation safe to build a transform from. One that is not unit length
    /// scales the bone as well as turning it.
    /// </summary>
    private static Quaternion Safe(Quaternion rotation)
    {
        float length = rotation.Length();

        return length > 0.0001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }

    private static MeshBone ToMeshBone(ImportedBone bone) => new()
    {
        Name = bone.Name,
        ParentIndex = bone.ParentIndex,
        ChildCount = 0,
        Orientation = bone.Orientation,
        Position = bone.Position,
    };

    /// <summary>
    /// Packs each corner's surface frame the way the game stores it.
    /// </summary>
    /// <remarks>
    /// Two directions, three bytes each with a fourth alongside. The fourth
    /// byte of the second is not padding: it records which way round the frame
    /// turns, and a surface whose frame turns the other way is lit as though
    /// its detail were carved the wrong way into it.
    /// </remarks>
    private static byte[] PackFrames(ImportedMesh mesh, int corners)
    {
        var frames = new byte[corners * 8];

        for (int w = 0; w < corners; w++)
        {
            Vector3 normal = mesh.Normals[w];
            Vector3 tangent = mesh.Tangents[w];
            Vector3 bitangent = w < mesh.Bitangents.Count ? mesh.Bitangents[w] : Vector3.Zero;

            if (tangent.LengthSquared() < 1e-10f) tangent = AnyPerpendicular(normal);
            if (bitangent.LengthSquared() < 1e-10f) bitangent = Vector3.Cross(normal, tangent);

            float turn = Vector3.Dot(Vector3.Cross(Normalised(normal), Normalised(tangent)),
                                     Normalised(bitangent)) < 0f ? -1f : 1f;

            int at = w * 8;

            Write(frames, at, tangent, 1f);
            Write(frames, at + 4, normal, turn);
        }

        return frames;
    }

    private static void Write(byte[] into, int at, Vector3 direction, float turn)
    {
        Vector3 unit = Normalised(direction);

        into[at] = Encode(unit.X);
        into[at + 1] = Encode(unit.Y);
        into[at + 2] = Encode(unit.Z);
        into[at + 3] = Encode(turn);
    }

    private static byte Encode(float value) =>
        (byte)Math.Clamp(MathF.Round((Math.Clamp(value, -1f, 1f) + 1f) * 127.5f), 0f, 255f);

    private static Vector3 Normalised(Vector3 value) =>
        value.LengthSquared() > 1e-10f ? Vector3.Normalize(value) : Vector3.UnitZ;

    private static Vector3 AnyPerpendicular(Vector3 normal)
    {
        Vector3 unit = Normalised(normal);
        Vector3 axis = MathF.Abs(Vector3.Dot(unit, Vector3.UnitX)) > 0.9f ? Vector3.UnitY : Vector3.UnitX;

        return Normalised(Vector3.Cross(axis, unit));
    }

    private static IReadOnlyList<int> Wind(IReadOnlyList<int> indices, bool flip)
    {
        if (!flip) return indices;

        var flipped = new int[indices.Count];

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            // Swapping any two corners turns the triangle around.
            flipped[i] = indices[i];
            flipped[i + 1] = indices[i + 2];
            flipped[i + 2] = indices[i + 1];
        }

        return flipped;
    }

    /// <summary>
    /// Works out which way the surface faces at each corner.
    /// </summary>
    /// <remarks>
    /// The file carries no such directions, and without them a model is lit as
    /// though it were flat. Each triangle's own direction is added to its three
    /// corners and the total normalised, so a corner shared between triangles
    /// ends up facing between them — which is what makes a curved surface look
    /// curved instead of faceted.
    /// <para>
    /// Triangle directions are added unscaled, which weights each by its area:
    /// a large triangle says more about which way the surface faces than a
    /// sliver does.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Vector3> BuildNormals(
        IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices)
    {
        var normals = new Vector3[positions.Count];

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];

            if (a < 0 || b < 0 || c < 0) continue;
            if (a >= positions.Count || b >= positions.Count || c >= positions.Count) continue;

            Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);

            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            float length = normals[i].Length();

            // A corner no triangle uses, or one whose triangles cancel out, is
            // given a usable direction rather than a zero that would light it
            // black.
            normals[i] = length > 0.0001f ? normals[i] / length : Vector3.UnitZ;
        }

        return normals;
    }
}
