using System.Numerics;
using System.Text;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>One bone of a skeleton read from a mesh file.</summary>
public sealed record ImportedBone
{
    public required string Name { get; init; }
    public required int ParentIndex { get; init; }
    public required Quaternion Orientation { get; init; }
    public required Vector3 Position { get; init; }

    /// <summary>
    /// Where this bone stood when the model was bound to it, in the model''s own
    /// space.
    /// </summary>
    /// <remarks>
    /// This is the pose the skin weights were authored against, and it is not
    /// the same thing as where the bone sits in the file''s node arrangement —
    /// that is merely however the scene was last left. Working from the wrong
    /// one moves every vertex by roughly the size of the character.
    /// <para>
    /// Null when the file does not record one, in which case the arrangement is
    /// all there is to go on.
    /// </para>
    /// </remarks>
    public Matrix4x4? BindPose { get; init; }

    public override string ToString() => $"{Name} (parent {ParentIndex})";
}

/// <summary>A model read from a mesh file, ready to be fitted to a skeleton.</summary>
public sealed record ImportedMesh
{
    public required IReadOnlyList<Vector3> Positions { get; init; }

    /// <summary>Texture coordinates, one per drawn corner.</summary>
    public required IReadOnlyList<Vector2> TexCoords { get; init; }

    /// <summary>
    /// The surface frame at each drawn corner, as the file records it: the
    /// direction away from the surface, and the two along it.
    /// </summary>
    /// <remarks>
    /// Kept rather than worked out from the triangles. The frame is what a
    /// material's surface detail and its shine are measured against, and one
    /// invented from the geometry loses every smoothing decision the model was
    /// authored with — which shows up as a washed-out surface that changes as
    /// the model turns.
    /// </remarks>
    public IReadOnlyList<Vector3> Normals { get; init; } = [];

    public IReadOnlyList<Vector3> Tangents { get; init; } = [];

    public IReadOnlyList<Vector3> Bitangents { get; init; } = [];

    /// <summary>Which position each drawn corner uses.</summary>
    public required IReadOnlyList<int> WedgePoints { get; init; }

    /// <summary>Triangle corners, as indices into the wedges.</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    public required IReadOnlyList<string> Materials { get; init; }
    public required IReadOnlyList<ImportedBone> Bones { get; init; }

    /// <summary>Which bones each position follows, and how strongly.</summary>
    public required IReadOnlyList<IReadOnlyList<(int Bone, float Weight)>> Weights { get; init; }

    public int TriangleCount => Indices.Count / 3;

    public bool HasSkeleton => Bones.Count > 0;

    public override string ToString() =>
        $"{Positions.Count:N0} points, {TriangleCount:N0} triangles, {Bones.Count} bones";
}

/// <summary>Thrown when a mesh file's bytes do not match the expected structure.</summary>
public sealed class InvalidMeshFileException : Exception
{
    public InvalidMeshFileException(string message) : base(message) { }
}

/// <summary>
/// Reads a model out of a mesh file, so one made elsewhere can be fitted to a
/// skeleton from the game.
/// </summary>
/// <remarks>
/// The file is a run of named chunks, each stating how large one entry is and
/// how many there are. That pair is what makes the format safe to read: a
/// chunk this does not understand is stepped over exactly, and a writer that
/// pads an entry differently is still walked correctly, because the size is
/// taken from the file rather than assumed.
/// <para>
/// Positions are read exactly as stored. Some tools write a model lying on its
/// side or facing away, but correcting that here would silently move a model
/// that was already right — so it is left to be chosen deliberately.
/// </para>
/// </remarks>
public static class PskReader
{
    private const int ChunkHeaderSize = 32;   // 20-byte name, flags, entry size, entry count
    private const int MaxEntries = 8_000_000;

    /// <summary>Reads a model from a file.</summary>
    public static ImportedMesh Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Read(File.ReadAllBytes(path));
    }

    /// <summary>Reads a model from bytes.</summary>
    public static ImportedMesh Read(ReadOnlySpan<byte> data)
    {
        var positions = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var wedgePoints = new List<int>();
        var indices = new List<int>();
        var materials = new List<string>();
        var bones = new List<ImportedBone>();
        var weights = new Dictionary<int, List<(int Bone, float Weight)>>();

        int at = 0;
        bool sawHeader = false;

        while (at + ChunkHeaderSize <= data.Length)
        {
            string name = ReadName(data.Slice(at, 20));

            int entrySize = BitConverter.ToInt32(data.Slice(at + 24, 4));
            int entryCount = BitConverter.ToInt32(data.Slice(at + 28, 4));

            at += ChunkHeaderSize;

            if (entrySize < 0 || entryCount < 0 || entryCount > MaxEntries)
                throw new InvalidMeshFileException($"Chunk '{name}' declares {entryCount} entries of {entrySize} bytes.");

            long length = (long)entrySize * entryCount;
            if (at + length > data.Length)
                throw new InvalidMeshFileException($"Chunk '{name}' runs past the end of the file.");

            ReadOnlySpan<byte> body = data.Slice(at, (int)length);

            switch (name)
            {
                case "ACTRHEAD":
                    sawHeader = true;
                    break;

                case "PNTS0000":
                    for (int i = 0; i < entryCount; i++)
                    {
                        ReadOnlySpan<byte> entry = body.Slice(i * entrySize, entrySize);

                        positions.Add(new Vector3(
                            BitConverter.ToSingle(entry),
                            BitConverter.ToSingle(entry[4..]),
                            BitConverter.ToSingle(entry[8..])));
                    }
                    break;

                case "VTXW0000":
                    for (int i = 0; i < entryCount; i++)
                    {
                        ReadOnlySpan<byte> entry = body.Slice(i * entrySize, entrySize);

                        wedgePoints.Add(BitConverter.ToUInt16(entry));
                        texCoords.Add(new Vector2(
                            BitConverter.ToSingle(entry[4..]),
                            BitConverter.ToSingle(entry[8..])));
                    }
                    break;

                case "FACE0000":
                    for (int i = 0; i < entryCount; i++)
                    {
                        ReadOnlySpan<byte> entry = body.Slice(i * entrySize, entrySize);

                        indices.Add(BitConverter.ToUInt16(entry));
                        indices.Add(BitConverter.ToUInt16(entry[2..]));
                        indices.Add(BitConverter.ToUInt16(entry[4..]));
                    }
                    break;

                case "MATT0000":
                    for (int i = 0; i < entryCount; i++)
                        materials.Add(ReadName(body.Slice(i * entrySize, Math.Min(64, entrySize))));
                    break;

                case "REFSKELT":
                    for (int i = 0; i < entryCount; i++)
                    {
                        ReadOnlySpan<byte> entry = body.Slice(i * entrySize, entrySize);

                        // 64-byte name, flags, child count, parent, then the
                        // rest position as a rotation and an offset.
                        bones.Add(new ImportedBone
                        {
                            Name = ReadName(entry[..64]),
                            ParentIndex = BitConverter.ToInt32(entry[72..]),
                            Orientation = new Quaternion(
                                BitConverter.ToSingle(entry[76..]),
                                BitConverter.ToSingle(entry[80..]),
                                BitConverter.ToSingle(entry[84..]),
                                BitConverter.ToSingle(entry[88..])),
                            Position = new Vector3(
                                BitConverter.ToSingle(entry[92..]),
                                BitConverter.ToSingle(entry[96..]),
                                BitConverter.ToSingle(entry[100..])),
                        });
                    }
                    break;

                case "RAWWEIGHTS":
                    for (int i = 0; i < entryCount; i++)
                    {
                        ReadOnlySpan<byte> entry = body.Slice(i * entrySize, entrySize);

                        float weight = BitConverter.ToSingle(entry);
                        int point = BitConverter.ToInt32(entry[4..]);
                        int bone = BitConverter.ToInt32(entry[8..]);

                        if (weight <= 0f) continue;

                        if (!weights.TryGetValue(point, out List<(int, float)>? list))
                        {
                            list = [];
                            weights[point] = list;
                        }

                        list.Add((bone, weight));
                    }
                    break;

                default:
                    // Stepped over exactly. A file may carry chunks this does
                    // not need, and the stated sizes are what make that safe.
                    break;
            }

            at += (int)length;
        }

        if (!sawHeader && positions.Count == 0)
            throw new InvalidMeshFileException("This is not a mesh file this can read.");

        return new ImportedMesh
        {
            Positions = positions,
            TexCoords = texCoords,
            WedgePoints = wedgePoints,
            Indices = indices,
            Materials = materials,
            Bones = bones,
            Weights = Normalise(weights, positions.Count),
        };
    }

    /// <summary>
    /// Gathers each point's bones, strongest first, with the strengths brought
    /// to one.
    /// </summary>
    /// <remarks>
    /// Weights arrive as a loose list of point-and-bone pairs in no order.
    /// Sorting by strength matters because a model kept to four bones a vertex
    /// must drop the weakest, not whichever happened to be written last.
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<(int Bone, float Weight)>> Normalise(
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

    /// <summary>Reads a fixed-width name, stopping at the first null.</summary>
    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;

        return Encoding.ASCII.GetString(bytes[..end]).Trim();
    }
}
