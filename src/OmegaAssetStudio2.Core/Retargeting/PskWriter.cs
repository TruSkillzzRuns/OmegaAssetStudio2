using System.Numerics;
using System.Text;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>
/// Writes a model out to a file another tool can open.
/// </summary>
/// <remarks>
/// This is how a fitted model leaves the application. It writes beside the
/// game, never into it: the file goes wherever the user chose, and nothing in
/// the game folder is touched.
/// <para>
/// The format keeps positions and drawn corners apart — corners that sit in the
/// same place share a position — so the writer folds identical positions back
/// together. Writing one position per corner would work but would triple the
/// size and lose which corners are joined, which is what a modelling tool needs
/// to smooth a surface.
/// </para>
/// </remarks>
public static class PskWriter
{
    private const int NameWidth = 64;

    /// <summary>Bones a vertex may follow. The format allows more; tools expect four.</summary>
    private const int MaxInfluences = 4;

    /// <summary>
    /// Writes a model and its skeleton to a file.
    /// </summary>
    /// <param name="path">Where to write. Any existing file is replaced.</param>
    /// <param name="lod">The geometry.</param>
    /// <param name="bones">The skeleton it follows.</param>
    /// <param name="materials">Names for its material slots.</param>
    public static void Write(
        string path,
        SkeletalMeshLod lod,
        IReadOnlyList<MeshBone> bones,
        IReadOnlyList<string>? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(bones);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);

        // Corners sitting in the same place share one position.
        (List<Vector3> points, int[] pointOfCorner) = FoldPositions(lod.Positions);

        List<string> slots = BuildSlots(lod, materials);

        WriteHeader(writer);
        WritePoints(writer, points);
        WriteCorners(writer, lod, pointOfCorner);
        WriteFaces(writer, lod, slots.Count);
        WriteMaterials(writer, slots);
        WriteSkeleton(writer, bones);
        WriteWeights(writer, lod, pointOfCorner, points.Count);
    }

    /// <summary>
    /// Folds positions that sit in the same place into one, and records which
    /// one each corner uses.
    /// </summary>
    private static (List<Vector3> Points, int[] PointOfCorner) FoldPositions(IReadOnlyList<Vector3> positions)
    {
        var points = new List<Vector3>(positions.Count);
        var seen = new Dictionary<Vector3, int>(positions.Count);
        var pointOfCorner = new int[positions.Count];

        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 position = positions[i];

            if (!seen.TryGetValue(position, out int point))
            {
                point = points.Count;
                points.Add(position);
                seen[position] = point;
            }

            pointOfCorner[i] = point;
        }

        return (points, pointOfCorner);
    }

    private static List<string> BuildSlots(SkeletalMeshLod lod, IReadOnlyList<string>? materials)
    {
        int highest = 0;
        foreach (MeshSection section in lod.Sections)
            highest = Math.Max(highest, section.MaterialIndex);

        int count = Math.Max(1, Math.Max(highest + 1, materials?.Count ?? 0));

        var slots = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            slots.Add(materials is not null && i < materials.Count && materials[i].Length > 0
                ? materials[i]
                : $"material{i}");
        }

        return slots;
    }

    private static void Chunk(BinaryWriter writer, string name, int entrySize, int entryCount)
    {
        var padded = new byte[20];
        Encoding.ASCII.GetBytes(name).CopyTo(padded, 0);

        writer.Write(padded);
        writer.Write(1999801);   // the version every writer of this format uses
        writer.Write(entrySize);
        writer.Write(entryCount);
    }

    private static void WriteName(BinaryWriter writer, string name, int width)
    {
        var padded = new byte[width];

        byte[] bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, padded, Math.Min(bytes.Length, width - 1));

        writer.Write(padded);
    }

    private static void WriteHeader(BinaryWriter writer) => Chunk(writer, "ACTRHEAD", 0, 0);

    private static void WritePoints(BinaryWriter writer, List<Vector3> points)
    {
        Chunk(writer, "PNTS0000", 12, points.Count);

        foreach (Vector3 point in points)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
        }
    }

    private static void WriteCorners(BinaryWriter writer, SkeletalMeshLod lod, int[] pointOfCorner)
    {
        Chunk(writer, "VTXW0000", 16, pointOfCorner.Length);

        for (int i = 0; i < pointOfCorner.Length; i++)
        {
            Vector2 uv = i < lod.TexCoords.Count ? lod.TexCoords[i] : Vector2.Zero;

            writer.Write((ushort)pointOfCorner[i]);
            writer.Write((ushort)0);
            writer.Write(uv.X);
            writer.Write(uv.Y);
            writer.Write((byte)MaterialOf(lod, i));
            writer.Write((byte)0);
            writer.Write((ushort)0);
        }
    }

    private static void WriteFaces(BinaryWriter writer, SkeletalMeshLod lod, int slotCount)
    {
        int triangles = lod.Indices.Count / 3;

        Chunk(writer, "FACE0000", 12, triangles);

        for (int t = 0; t < triangles; t++)
        {
            int at = t * 3;

            writer.Write((ushort)lod.Indices[at]);
            writer.Write((ushort)lod.Indices[at + 1]);
            writer.Write((ushort)lod.Indices[at + 2]);

            int material = Math.Clamp(MaterialOf(lod, lod.Indices[at]), 0, Math.Max(0, slotCount - 1));

            writer.Write((byte)material);
            writer.Write((byte)0);      // auxiliary material
            writer.Write(1u);           // smoothing group
        }
    }

    /// <summary>Which material slot covers a given corner.</summary>
    private static int MaterialOf(SkeletalMeshLod lod, int corner)
    {
        foreach (MeshSection section in lod.Sections)
        {
            if (corner >= section.BaseIndex && corner < section.BaseIndex + section.IndexCount)
                return section.MaterialIndex;
        }

        return 0;
    }

    private static void WriteMaterials(BinaryWriter writer, List<string> slots)
    {
        Chunk(writer, "MATT0000", 88, slots.Count);

        for (int i = 0; i < slots.Count; i++)
        {
            WriteName(writer, slots[i], NameWidth);
            writer.Write(i);            // which texture
            writer.Write(new byte[20]); // flags and counts nothing here sets
        }
    }

    private static void WriteSkeleton(BinaryWriter writer, IReadOnlyList<MeshBone> bones)
    {
        Chunk(writer, "REFSKELT", 120, bones.Count);

        for (int i = 0; i < bones.Count; i++)
        {
            MeshBone bone = bones[i];

            int children = 0;
            for (int c = 0; c < bones.Count; c++)
            {
                if (c != 0 && bones[c].ParentIndex == i) children++;
            }

            WriteName(writer, bone.Name, NameWidth);
            writer.Write(0);            // flags
            writer.Write(children);
            writer.Write(bone.ParentIndex);

            writer.Write(bone.Orientation.X);
            writer.Write(bone.Orientation.Y);
            writer.Write(bone.Orientation.Z);
            writer.Write(bone.Orientation.W);

            writer.Write(bone.Position.X);
            writer.Write(bone.Position.Y);
            writer.Write(bone.Position.Z);

            writer.Write(1f);           // length
            writer.Write(1f);           // size
            writer.Write(1f);
            writer.Write(1f);
        }
    }

    private static void WriteWeights(
        BinaryWriter writer, SkeletalMeshLod lod, int[] pointOfCorner, int pointCount)
    {
        // Weights belong to positions, not corners, and corners that share a
        // position share its weights. Written once per position, or a modelling
        // tool sees the same influence several times over.
        var byPoint = new Dictionary<int, Dictionary<int, float>>(pointCount);

        for (int corner = 0; corner < pointOfCorner.Length && corner < lod.Influences.Count; corner++)
        {
            int point = pointOfCorner[corner];
            if (byPoint.ContainsKey(point)) continue;

            VertexInfluence influence = lod.Influences[corner];
            if (influence.Count == 0) continue;

            var weights = new Dictionary<int, float>(influence.Count);

            for (int i = 0; i < influence.Count && i < MaxInfluences; i++)
                weights[influence.Bones[i]] = weights.GetValueOrDefault(influence.Bones[i]) + influence.Weights[i];

            byPoint[point] = weights;
        }

        int total = byPoint.Values.Sum(w => w.Count);

        Chunk(writer, "RAWWEIGHTS", 12, total);

        foreach ((int point, Dictionary<int, float> weights) in byPoint)
        {
            foreach ((int bone, float weight) in weights)
            {
                writer.Write(weight);
                writer.Write(point);
                writer.Write(bone);
            }
        }
    }
}
