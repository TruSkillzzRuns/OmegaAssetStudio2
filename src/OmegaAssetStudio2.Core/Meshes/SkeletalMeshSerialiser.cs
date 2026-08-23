using System.Buffers.Binary;
using System.Numerics;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>The geometry to put into a model, replacing what it had.</summary>
public sealed record MeshGeometry
{
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<Vector2> TexCoords { get; init; }

    /// <summary>Which bones each vertex follows, numbered against the skeleton.</summary>
    public required IReadOnlyList<VertexInfluence> Influences { get; init; }

    /// <summary>Triangle corners, as indices into the vertices.</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>The runs of triangles, each drawn with one material.</summary>
    public required IReadOnlyList<MeshSection> Sections { get; init; }

    /// <summary>
    /// Eight bytes of tangent frame per vertex, where they are already known.
    /// </summary>
    /// <remarks>
    /// Given for geometry that came out of the game, so a vertex that has not
    /// moved is written back byte for byte. Left empty, a frame is worked out
    /// from the vertex's own direction instead.
    /// </remarks>
    public IReadOnlyList<byte> TangentFrames { get; init; } = [];

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// Writes a whole level of detail into a model, replacing the geometry it had.
/// </summary>
/// <remarks>
/// Unlike moving vertices about, this can put a different model in altogether:
/// a different number of vertices, different triangles, different material
/// runs. The object changes size as a result, which is why it goes hand in hand
/// with the rebuilder that moves everything after it.
/// <para>
/// Only the levels of detail are rebuilt. Everything before them — the object's
/// properties, its size, its materials and its skeleton — and everything after
/// them is copied across untouched, because none of it is affected by which
/// vertices the model has and reproducing it would only risk getting it wrong.
/// </para>
/// </remarks>
public static class SkeletalMeshSerialiser
{
    /// <summary>
    /// How many bones one run of vertices may follow.
    /// </summary>
    /// <remarks>
    /// A vertex names its bones with a single byte, so a run cannot draw on
    /// more than this many. A model needing more has to be split into several
    /// runs, which this does not do — it says so instead.
    /// </remarks>
    private const int MaxBonesPerChunk = 256;

    /// <summary>
    /// Produces the object's bytes with its geometry replaced.
    /// </summary>
    /// <param name="package">The package the model was read from.</param>
    /// <param name="exportIndex">Which object in it.</param>
    /// <param name="mesh">The model as it was read, for the parts kept verbatim.</param>
    /// <param name="geometry">The geometry to put in.</param>
    /// <returns>The object's bytes, usually a different length from before.</returns>
    public static byte[] Replace(
        Package package, int exportIndex, SkeletalMesh mesh, MeshGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(geometry);

        if (mesh.LodsStart < 0 || mesh.LodsEnd < mesh.LodsStart)
            throw new MeshWriteException("This model does not record where its levels of detail sit.");

        if (mesh.HighestDetail is null)
            throw new MeshWriteException("This model has no level of detail to replace.");

        Check(mesh, geometry);

        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);

        var output = new MemoryStream(data.Length);

        // Everything up to the levels of detail, exactly as it was.
        output.Write(data[..mesh.LodsStart]);

        // As many levels of detail as the model had. The reduced ones it came
        // with described the geometry that has just been replaced, so they
        // cannot be kept; each level is written from the new geometry instead,
        // keeping that level's own settings.
        //
        // They are not simplified. Writing one level and dropping the rest was
        // tried first and is worse: the model's own lodinfo, which is copied
        // across untouched, still lists a setting for every level, and a model
        // whose geometry has fewer levels than its settings claim leaves the
        // game reading a level that is not there. Every level holding the full
        // model costs room and draws the same at any distance, which is a cost,
        // not a fault.
        WriteInt(output, mesh.Lods.Count);

        foreach (SkeletalMeshLod level in mesh.Lods)
            WriteLod(output, geometry, level, mesh.Bones.Count);

        // Everything after them, exactly as it was.
        output.Write(data[mesh.LodsEnd..]);

        return output.ToArray();
    }

    private static void Check(SkeletalMesh mesh, MeshGeometry geometry)
    {
        int vertices = geometry.Positions.Count;

        if (vertices == 0) throw new MeshWriteException("This model has no vertices.");

        if (geometry.Normals.Count != vertices || geometry.TexCoords.Count != vertices ||
            geometry.Influences.Count != vertices)
        {
            throw new MeshWriteException(
                "Every vertex needs a position, a direction, a texture coordinate and its bones.");
        }

        if (vertices > ushort.MaxValue + 1)
        {
            throw new MeshWriteException(
                $"This model has {vertices:N0} vertices. The game addresses them with two bytes, " +
                $"so it cannot hold more than {ushort.MaxValue + 1:N0}.");
        }

        if (geometry.Indices.Count % 3 != 0)
            throw new MeshWriteException("The triangles do not come in whole corners.");

        foreach (int index in geometry.Indices)
        {
            if (index < 0 || index >= vertices)
                throw new MeshWriteException("A triangle names a vertex this model does not have.");
        }

        var used = new HashSet<int>();

        foreach (VertexInfluence influence in geometry.Influences)
        {
            foreach (int bone in influence.Bones)
            {
                if (bone < 0 || bone >= mesh.Bones.Count)
                    throw new MeshWriteException("A vertex follows a bone this skeleton does not have.");

                used.Add(bone);
            }
        }

        // How many bones one run may draw on is checked while the layout is
        // planned, because that is where runs are decided.
        _ = used;
    }

    private static void WriteLod(
        MemoryStream output, MeshGeometry geometry, SkeletalMeshLod original, int boneCount)
    {


        // Laid out the way the game's own models are: one run of vertices per
        // section, each run owning its vertices outright with the singly-bound
        // ones first.
        MeshLayoutPlan plan = MeshLayoutPlanner.Build(geometry, original.Chunks);

        int vertices = plan.VertexCount;

        List<int> boneMap = plan.Chunks
            .SelectMany(c => c.BoneMap)
            .Distinct()
            .Order()
            .ToList();

        if (boneMap.Count == 0) boneMap.Add(0);

        WriteSections(output, plan);
        WriteIndexBuffer(output, plan, vertices, original.IndicesStayOnTheProcessor);

        // Which bones the level poses, and which must be present for it. Both
        // are the model's own lists with anything the new vertices reach added,
        // never lists worked out from the vertices alone: a bone that merely
        // carries another has no vertices of its own and would drop out, taking
        // everything below it out of position.
        List<int> active = Combine(original.ActiveBones, boneMap);
        List<int> required = Combine(original.RequiredBones, boneMap);

        WriteInt(output, active.Count);
        foreach (int bone in active) WriteUShort(output, (ushort)bone);

        WriteChunks(output, plan);

        // The number the level records about its own size, written back as it
        // was found. Measured on an untouched costume it is zero — neither the
        // vertex count this used to write nor the byte length version 1's
        // importer patches in — so the only safe answer is the model's own.
        WriteInt(output, checked((int)original.DeclaredSize));

        WriteInt(output, vertices);   // how many vertices

        if (required.Count > 0 && required[^1] > byte.MaxValue)
        {
            throw new MeshWriteException(
                $"This model needs bone {required[^1]}, and the file records those needed in a single " +
                "byte each, so it cannot be written.");
        }

        WriteInt(output, required.Count);
        foreach (int bone in required) output.WriteByte((byte)bone);

        // The block describing the raw point indices, written back exactly as
        // it was found rather than rebuilt.
        if (original.RawPointIndices.Length > 0) output.Write(original.RawPointIndices.Span);
        else WriteEmptyBulkData(output);

        int uvSets = Math.Max(1, original.Layout.UvSetCount);
        WriteInt(output, uvSets);

        WriteVertexBuffer(output, geometry, plan, original, uvSets);

        // Alternative skinning sets, and the extra indices used to smooth a
        // surface. Both describe the geometry that has just been replaced, so
        // they are written empty rather than carried over wrongly.
        WriteInt(output, 0);
        WriteEmptyIndexBuffer(output, original.AdjacencyIndicesStayOnTheProcessor);
    }

    private static void WriteSections(MemoryStream output, MeshLayoutPlan plan)
    {
        WriteInt(output, plan.Sections.Count);

        foreach (MeshSection section in plan.Sections)
        {
            WriteUShort(output, (ushort)section.MaterialIndex);
            WriteUShort(output, (ushort)section.ChunkIndex);
            WriteInt(output, section.BaseIndex);
            WriteInt(output, section.TriangleCount);
            output.WriteByte(0);                          // whether its triangles are sorted
        }
    }

    /// <summary>
    /// The model's own list of bones, in its own order, plus any the new
    /// vertices reach.
    /// </summary>
    /// <remarks>
    /// Not sorted. These lists are not in order of bone number in the game's
    /// own files — one costume lists 20, 21, 85, 86, 89, 87, 90, 88, 91, 46,
    /// 64, 15 — so sorting them rewrites a list that was already correct.
    /// Anything genuinely new goes on the end.
    /// </remarks>
    private static List<int> Combine(IReadOnlyList<int> original, IReadOnlyList<int> used)
    {
        var combined = new List<int>(original);
        var have = new HashSet<int>(original);

        foreach (int bone in used.Order())
        {
            if (have.Add(bone)) combined.Add(bone);
        }

        return combined;
    }

    private static void WriteIndexBuffer(
        MemoryStream output, MeshLayoutPlan plan, int vertices, bool staysOnProcessor)
    {
        IReadOnlyList<int> corners = plan.Indices;

        // Two bytes an index while the model is small enough, which is what the
        // game's own models use.
        bool wide = vertices > ushort.MaxValue;
        int width = wide ? sizeof(uint) : sizeof(ushort);

        WriteInt(output, staysOnProcessor ? 1 : 0);
        output.WriteByte((byte)width);

        WriteInt(output, width);
        WriteInt(output, corners.Count);

        foreach (int index in corners)
        {
            if (wide) WriteInt(output, index);
            else WriteUShort(output, (ushort)index);
        }
    }

    /// <summary>
    /// Writes the runs of vertices, one per section.
    /// </summary>
    /// <remarks>
    /// A run states how many of its vertices follow a single bone and how many
    /// follow several, and the two groups are written in that order — which is
    /// how every one of the game's own models is arranged.
    /// </remarks>
    private static void WriteChunks(MemoryStream output, MeshLayoutPlan plan)
    {
        WriteInt(output, plan.Chunks.Count);

        foreach (PlannedChunk chunk in plan.Chunks)
        {
            WriteInt(output, chunk.BaseVertexIndex);

            WriteInt(output, 0);              // vertices held on the processor: none
            WriteInt(output, 0);

            WriteInt(output, chunk.BoneMap.Count);
            foreach (int bone in chunk.BoneMap) WriteUShort(output, (ushort)bone);

            WriteInt(output, chunk.RigidCount);
            WriteInt(output, chunk.SoftCount);

            WriteInt(output, chunk.MaxInfluences);
        }
    }

    private static void WriteVertexBuffer(
        MemoryStream output,
        MeshGeometry geometry,
        MeshLayoutPlan plan,
        SkeletalMeshLod original,
        int uvSets)
    {
        // Positions in full and texture coordinates at half precision, which is
        // what the game's own character models use. Written plainly rather than
        // quantised, because a quantised position is measured against a range
        // that would have to be recomputed and is lossy for no gain here.
        var layout = new VertexLayout(PackedPosition: false, FullPrecisionUvs: false, uvSets);

        WriteInt(output, uvSets);
        WriteInt(output, 0);              // texture coordinates at half precision

        // Written back as found. A real costume carries a one here while its
        // vertices are plainly full precision, and a range of one in each
        // direction — so neither field is something to work out afresh.
        WriteUInt(output, original.PackedPositionFlag);
        WriteVector(output, original.PackedExtension);
        WriteVector(output, original.PackedOrigin);

        WriteInt(output, layout.Stride);
        WriteInt(output, plan.VertexCount);

        // Written run by run, because a vertex names its bones by their place in
        // its own run's list rather than in the skeleton.
        foreach (PlannedChunk chunk in plan.Chunks)
        {
            var localOf = new Dictionary<int, int>(chunk.BoneMap.Count);
            for (int i = 0; i < chunk.BoneMap.Count; i++) localOf[chunk.BoneMap[i]] = i;

            bool haveFrames =
                geometry.TangentFrames.Count >= geometry.Positions.Count * VertexLayout.TangentFrameBytes;

            foreach (int v in chunk.Vertices)
            {
                if (haveFrames)
                {
                    int at = v * VertexLayout.TangentFrameBytes;

                    for (int b = 0; b < VertexLayout.TangentFrameBytes; b++)
                        output.WriteByte(geometry.TangentFrames[at + b]);
                }
                else
                {
                    WritePackedNormal(output, Vector3.UnitX);       // along the surface
                    WritePackedNormal(output, geometry.Normals[v]); // away from it
                }

                WriteInfluence(output, geometry.Influences[v], localOf);

                WriteVector(output, geometry.Positions[v]);

                Vector2 uv = geometry.TexCoords[v];

                for (int set = 0; set < uvSets; set++)
                {
                    WriteHalf(output, uv.X);
                    WriteHalf(output, uv.Y);
                }
            }
        }
    }

    /// <summary>
    /// Writes which bones a vertex follows and how strongly.
    /// </summary>
    /// <remarks>
    /// Four of each, a byte apiece. The strengths are written so they add to
    /// exactly two hundred and fifty-five: a vertex adding to less than the
    /// whole shrinks toward the model's origin when it is posed, and one adding
    /// to more stretches away from it.
    /// </remarks>
    private static void WriteInfluence(
        MemoryStream output, VertexInfluence influence, Dictionary<int, int> localOf)
    {
        const int slots = 4;

        Span<byte> bones = stackalloc byte[slots];
        Span<byte> weights = stackalloc byte[slots];

        int count = Math.Min(slots, influence.Count);
        int given = 0;

        for (int i = 0; i < count; i++)
        {
            bones[i] = (byte)(localOf.TryGetValue(influence.Bones[i], out int local) ? local : 0);

            int weight = (int)MathF.Round(influence.Weights[i] * 255f);
            weight = Math.Clamp(weight, 0, 255 - given);

            weights[i] = (byte)weight;
            given += weight;
        }

        // Whatever rounding lost goes to the strongest bone.
        if (given < 255 && count > 0) weights[0] = (byte)Math.Min(255, weights[0] + (255 - given));

        output.Write(bones);
        output.Write(weights);
    }

    private static void WritePackedNormal(MemoryStream output, Vector3 direction)
    {
        float length = direction.Length();
        Vector3 unit = length > 0.000001f ? direction / length : Vector3.UnitZ;

        output.WriteByte(Encode(unit.X));
        output.WriteByte(Encode(unit.Y));
        output.WriteByte(Encode(unit.Z));
        output.WriteByte(255);            // handedness
    }

    private static byte Encode(float component) =>
        (byte)Math.Clamp(MathF.Round((component + 1f) * 127.5f), 0f, 255f);

    /// <summary>Writes a bulk-data header that says nothing was stored.</summary>
    private static void WriteEmptyBulkData(MemoryStream output)
    {
        const uint noPayload = 0x20;

        WriteUInt(output, noPayload);
        WriteInt(output, 0);              // how many

        // Minus one for both, meaning "nowhere", exactly as version 1's
        // importer writes it. Zeroes would name the very start of the file as
        // the payload's home and a size of nothing.
        WriteInt(output, -1);             // how large on disk
        WriteInt(output, -1);             // and where
    }

    private static void WriteEmptyIndexBuffer(MemoryStream output, bool staysOnProcessor)
    {
        WriteInt(output, staysOnProcessor ? 1 : 0);
        output.WriteByte(sizeof(ushort));
        WriteInt(output, sizeof(ushort));
        WriteInt(output, 0);
    }

    private static void WriteInt(MemoryStream output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt(MemoryStream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUShort(MemoryStream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteHalf(MemoryStream output, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, BitConverter.HalfToInt16Bits((Half)value));
        output.Write(bytes);
    }

    private static void WriteVector(MemoryStream output, Vector3 value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float) * 3];

        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.Z);

        output.Write(bytes);
    }
}
