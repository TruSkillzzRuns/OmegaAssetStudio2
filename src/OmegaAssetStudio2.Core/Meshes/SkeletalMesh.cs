using System.Numerics;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One bone of a skeleton, naming its parent by index.</summary>
public sealed record MeshBone
{
    public required string Name { get; init; }
    public required int ParentIndex { get; init; }
    public required int ChildCount { get; init; }

    /// <summary>Rest orientation, relative to the parent.</summary>
    public required Quaternion Orientation { get; init; }

    /// <summary>Rest position, relative to the parent.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>True for the root, which is its own parent by convention.</summary>
    public bool IsRoot => ParentIndex <= 0;

    public override string ToString() => $"{Name} (parent {ParentIndex})";
}

/// <summary>A run of triangles drawn with one material.</summary>
public sealed record MeshSection
{
    public required int MaterialIndex { get; init; }
    public required int ChunkIndex { get; init; }

    /// <summary>Where this section starts in the index buffer.</summary>
    public required int BaseIndex { get; init; }

    public required int TriangleCount { get; init; }

    /// <summary>Indices this section covers.</summary>
    public int IndexCount => TriangleCount * 3;

    public override string ToString() => $"section: material {MaterialIndex}, {TriangleCount} triangles";
}

/// <summary>
/// A run of vertices that share one set of bones.
/// </summary>
/// <remarks>
/// A vertex names its bones by a number local to the chunk it sits in, because
/// a run of vertices only ever follows a handful of bones and a small number
/// takes less room. The bone map turns those local numbers into positions in
/// the skeleton.
/// </remarks>
public sealed record MeshChunk
{
    public required int BaseVertexIndex { get; init; }
    public required int VertexCount { get; init; }

    /// <summary>Local bone number to its position in the skeleton.</summary>
    public required IReadOnlyList<int> BoneMap { get; init; }

    /// <summary>Vertices in this run that follow exactly one bone.</summary>
    public int RigidVertexCount { get; init; }

    /// <summary>Vertices in this run that follow several bones at once.</summary>
    public int SoftVertexCount { get; init; }

    /// <summary>The most bones any one vertex here follows.</summary>
    public int MaxBoneInfluences { get; init; }

    public bool Covers(int vertexIndex) =>
        vertexIndex >= BaseVertexIndex && vertexIndex < BaseVertexIndex + VertexCount;

    public override string ToString() =>
        $"{VertexCount} vertices from {BaseVertexIndex}, {BoneMap.Count} bones";
}

/// <summary>Which bones a vertex follows, and how strongly.</summary>
public readonly record struct VertexInfluence
{
    /// <summary>Positions in the skeleton, already resolved through the bone map.</summary>
    public required IReadOnlyList<int> Bones { get; init; }

    /// <summary>How strongly each bone pulls, adding to one.</summary>
    public required IReadOnlyList<float> Weights { get; init; }

    /// <summary>How many bones actually pull on this vertex.</summary>
    public int Count => Bones.Count;
}

/// <summary>How a vertex buffer stores its positions and texture coordinates.</summary>
public readonly record struct VertexLayout(bool PackedPosition, bool FullPrecisionUvs, int UvSetCount)
{
    /// <summary>Two packed directions, which lead every vertex.</summary>
    public const int TangentFrameBytes = 8;

    private const int SkinningBytes = 8;          // four bone indices, four weights
    private const int PackedPositionBytes = 4;
    private const int FullPositionBytes = 12;
    private const int HalfUvBytes = 4;
    private const int FullUvBytes = 8;

    /// <summary>
    /// Bytes every vertex carries regardless of layout: the tangent frame and
    /// the skinning weights, which sit ahead of the position.
    /// </summary>
    public const int FixedBytes = TangentFrameBytes + SkinningBytes;

    /// <summary>Bytes one vertex occupies under this layout.</summary>
    public int Stride =>
        TangentFrameBytes + SkinningBytes +
        (PackedPosition ? PackedPositionBytes : FullPositionBytes) +
        (UvSetCount * (FullPrecisionUvs ? FullUvBytes : HalfUvBytes));

    /// <summary>Offset of the position within a vertex, after the tangent frame and skinning.</summary>
    public int PositionOffset => TangentFrameBytes + SkinningBytes;

    public override string ToString() =>
        $"{(PackedPosition ? "packed" : "full")} position, " +
        $"{(FullPrecisionUvs ? "full" : "half")} UVs x{UvSetCount}, {Stride} bytes";
}

/// <summary>One level of detail: geometry drawn as a set of sections.</summary>
public sealed record SkeletalMeshLod
{
    public required IReadOnlyList<MeshSection> Sections { get; init; }

    /// <summary>Triangle corners, as indices into the vertices.</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    public required int VertexCount { get; init; }
    public required VertexLayout Layout { get; init; }

    /// <summary>
    /// Whether the game keeps this level's triangle list readable by the
    /// processor after it has been handed to the graphics card.
    /// </summary>
    /// <remarks>
    /// Nothing here needs it, but it has to be written back as it was found: it
    /// is the model's own answer, and one real costume says yes where an
    /// otherwise identical one says no.
    /// </remarks>
    public bool IndicesStayOnTheProcessor { get; init; }

    /// <summary>The number this level records about its own size, as found.</summary>
    public long DeclaredSize { get; init; }

    /// <summary>
    /// Whether the extra indices used to smooth a surface stay readable by the
    /// processor. Carried separately because it is the adjacency buffer's own
    /// answer, not the triangle list's.
    /// </summary>
    public bool AdjacencyIndicesStayOnTheProcessor { get; init; }

    /// <summary>The vertex buffer's packed-position flag, exactly as found.</summary>
    public uint PackedPositionFlag { get; init; }

    /// <summary>
    /// The eight bytes of tangent frame each vertex carries, exactly as found.
    /// </summary>
    /// <remarks>
    /// Kept so a vertex that has not moved can be written back unchanged.
    /// Reading these into directions and encoding them again rounds each byte,
    /// and a constant in their place — which is what this wrote before — throws
    /// the surface's own frame away entirely.
    /// </remarks>
    public IReadOnlyList<byte> TangentFrames { get; init; } = [];

    /// <summary>
    /// The block describing the raw point indices, exactly as found.
    /// </summary>
    /// <remarks>
    /// Kept whole rather than rebuilt. An untouched costume records it as no
    /// flags, nothing stored, and a position in the file — not the "nothing
    /// here" form, and not anything derivable — so writing anything else
    /// rewrites a block that was already right.
    /// </remarks>
    public ReadOnlyMemory<byte> RawPointIndices { get; init; }

    /// <summary>
    /// The bones this level poses, exactly as the file lists them.
    /// </summary>
    /// <remarks>
    /// Kept rather than worked out again. Fitting a model changes which bones
    /// its vertices follow, but not which bones the skeleton has to pose, and a
    /// list derived from the new vertices alone leaves out every bone that only
    /// carries others — which is most of the skeleton above a hand or a foot.
    /// </remarks>
    public IReadOnlyList<int> ActiveBones { get; init; } = [];

    /// <summary>The bones that must be present for this level, as listed.</summary>
    public IReadOnlyList<int> RequiredBones { get; init; } = [];

    /// <summary>Vertex positions, with packed values already reconstructed.</summary>
    public required IReadOnlyList<Vector3> Positions { get; init; }

    /// <summary>
    /// The direction the texture runs in across each vertex, with the sign of
    /// the third axis in W.
    /// </summary>
    /// <remarks>
    /// Read from the tangent frame the file stores rather than worked out from
    /// the triangles: the frame leads every vertex, the first of its two packed
    /// directions is this one, and it is unpacked exactly as the normal beside
    /// it is. A normal map is written against these axes, so this is what turns
    /// what it says into a direction in the world.
    /// </remarks>
    public IReadOnlyList<Vector4> Tangents { get; init; } = [];

    /// <summary>Surface direction at each vertex, unpacked and normalised.</summary>
    public required IReadOnlyList<Vector3> Normals { get; init; }

    /// <summary>
    /// The first set of texture coordinates, which is the one materials sample
    /// their colour from. Later sets carry lightmap and detail coordinates and
    /// are not needed to draw the model.
    /// </summary>
    public required IReadOnlyList<Vector2> TexCoords { get; init; }

    /// <summary>
    /// Which bones each vertex follows, with the bone numbers already resolved
    /// against the skeleton rather than left local to a chunk.
    /// </summary>
    public required IReadOnlyList<VertexInfluence> Influences { get; init; }

    /// <summary>The runs of vertices this level is divided into.</summary>
    public required IReadOnlyList<MeshChunk> Chunks { get; init; }

    /// <summary>
    /// Where the vertices begin within the object''s own bytes, and how far
    /// apart they are.
    /// </summary>
    /// <remarks>
    /// Kept so a vertex can be written back exactly where it was read from.
    /// Moving a model without changing how many vertices it has, or how they
    /// are laid out, changes nothing about the object''s size — which is what
    /// makes writing it back safe.
    /// </remarks>
    public int VertexDataOffset { get; init; } = -1;

    /// <summary>
    /// The range packed positions are scaled into. Meaningless when positions
    /// are stored in full.
    /// </summary>
    public Vector3 PackedOrigin { get; init; }

    /// <summary>The size of that range.</summary>
    public Vector3 PackedExtension { get; init; }

    public int TriangleCount => Indices.Count / 3;

    /// <summary>
    /// True when vertex positions were recovered. The skeleton, sections and
    /// index buffer are read independently of this, so a model can be listed and
    /// described without being drawable.
    /// </summary>
    public bool HasGeometry => Positions.Count > 0;

    public override string ToString() =>
        $"{VertexCount} vertices, {TriangleCount} triangles, {Sections.Count} sections" +
        (HasGeometry ? string.Empty : " (positions not recovered)");
}

/// <summary>A skinned model: a skeleton plus one or more levels of detail.</summary>
public sealed record SkeletalMesh
{
    public required string Name { get; init; }
    public required string ObjectPath { get; init; }
    public required MeshBounds Bounds { get; init; }
    public required IReadOnlyList<MeshBone> Bones { get; init; }
    public required IReadOnlyList<SkeletalMeshLod> Lods { get; init; }

    /// <summary>Material slots, as object references into the owning package.</summary>
    public required IReadOnlyList<ObjectReference> Materials { get; init; }

    /// <summary>
    /// Where the levels of detail begin within the object''s bytes, and where
    /// they end.
    /// </summary>
    /// <remarks>
    /// Everything before the first is the object''s properties, its size, its
    /// materials and its skeleton; everything after the last is a tail of
    /// things this reader does not interpret. Both are kept verbatim when the
    /// object is written back, so only the part that is understood is rebuilt.
    /// </remarks>
    public int LodsStart { get; init; } = -1;

    /// <summary>Where the levels of detail end.</summary>
    public int LodsEnd { get; init; } = -1;

    public SkeletalMeshLod? HighestDetail => Lods.Count > 0 ? Lods[0] : null;

    public override string ToString() =>
        $"{Name} ({Bones.Count} bones, {Lods.Count} LOD(s))";
}

/// <summary>
/// Reads skinned models.
/// </summary>
/// <remarks>
/// The vertex buffer has no fixed layout: positions are either quantised into
/// four bytes or stored as three floats, texture coordinates are either half or
/// full precision, and there are one to four coordinate sets. That is why
/// scanning a mesh payload for an array of three-float positions finds nothing —
/// there isn't one.
/// <para>
/// The two layout flags are serialised in a width this reader does not assume.
/// Instead it computes the stride each candidate layout implies and keeps the one
/// matching the element size the file itself declares. A layout that cannot be
/// made to agree is reported as unreadable rather than guessed at.
/// </para>
/// </remarks>
public static class SkeletalMeshReader
{
    public const string SkeletalMeshClass = "skeletalmesh";

    /// <summary>Bytes per bone: name, flags, rest transform, links, colour.</summary>
    private const int BoneBytes = 52;

    private const int MaxBones = 4096;
    private const int MaxLods = 16;
    private const int MaxSections = 1024;

    /// <summary>
    /// Reads a skeletal mesh export. Returns null when the export is not one, or
    /// when its payload does not match the expected structure.
    /// </summary>
    /// <param name="onFailure">
    /// Receives why the read failed. A silent null tells a caller nothing about
    /// which field went wrong, which is the difference between a fixable report
    /// and a guess.
    /// </param>
    public static SkeletalMesh? TryRead(Package package, int exportIndex, Action<string>? onFailure = null)
    {
        if (!string.Equals(package.GetExportClassName(exportIndex), SkeletalMeshClass,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null)
        {
            onFailure?.Invoke("properties did not parse");
            return null;
        }

        try
        {
            return Read(package, exportIndex, properties);
        }
        catch (InvalidPackageException ex)
        {
            onFailure?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The materials a model names, without decoding anything else about it.
    /// </summary>
    /// <remarks>
    /// The material array sits at the head of the payload, straight after the
    /// bounds and before the bones and every level of detail. A caller that
    /// only wants to know what a model is painted with has no reason to read
    /// the vertex and index buffers of every level to find out - listing one
    /// package's models did exactly that, decoding each mesh in full and then
    /// decoding the chosen one a second time.
    /// </remarks>
    public static IReadOnlyList<ObjectReference>? TryReadMaterials(Package package, int exportIndex)
    {
        if (!string.Equals(package.GetExportClassName(exportIndex), SkeletalMeshClass,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        try
        {
            ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
            var cursor = new PackageCursor(data, properties.PayloadOffset);

            cursor.Skip(MeshBounds.ByteSize);

            return ReadObjectArray(ref cursor);
        }
        catch (InvalidPackageException)
        {
            return null;
        }
    }

    private static SkeletalMesh Read(Package package, int exportIndex, PropertyBag properties)
    {
        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
        var cursor = new PackageCursor(data, properties.PayloadOffset);

        MeshBounds bounds = MeshBounds.Read(data, cursor.Position);
        cursor.Skip(MeshBounds.ByteSize);

        IReadOnlyList<ObjectReference> materials = ReadObjectArray(ref cursor);

        cursor.Skip(sizeof(float) * 3);   // origin
        cursor.Skip(sizeof(int) * 3);     // rotation origin

        IReadOnlyList<MeshBone> bones = ReadBones(ref cursor, package.Names);

        cursor.Skip(sizeof(int));         // skeletal depth

        // Whether a colour buffer follows each vertex buffer is decided by a
        // property, not by anything in the payload, so it has to be read here and
        // carried down. Getting it wrong shifts every level after the first.
        bool hasVertexColours = properties.GetBool("bhasvertexcolors");

        int lodsStart = cursor.Position;

        int lodCount = cursor.ReadInt32("LOD count");
        if (lodCount < 0 || lodCount > MaxLods)
            throw new InvalidPackageException($"Mesh declares {lodCount} levels of detail.");

        var lods = new List<SkeletalMeshLod>(lodCount);
        for (int i = 0; i < lodCount; i++)
            lods.Add(ReadLod(ref cursor, i, hasVertexColours));

        return new SkeletalMesh
        {
            Name = package.GetExportName(exportIndex),
            ObjectPath = package.GetExportPath(exportIndex),
            Bounds = bounds,
            Bones = bones,
            Lods = lods,
            Materials = materials,
            LodsStart = lodsStart,
            LodsEnd = cursor.Position,
        };
    }

    private static IReadOnlyList<ObjectReference> ReadObjectArray(ref PackageCursor cursor)
    {
        int count = cursor.ReadInt32("material count");
        if (count < 0 || (long)count * sizeof(int) > cursor.Remaining)
            throw new InvalidPackageException($"Mesh declares {count} materials.");

        var references = new ObjectReference[count];
        for (int i = 0; i < count; i++)
            references[i] = new ObjectReference(cursor.ReadInt32($"material {i}"));

        return references;
    }

    private static IReadOnlyList<MeshBone> ReadBones(ref PackageCursor cursor, NameTable names)
    {
        int count = cursor.ReadInt32("bone count");
        if (count < 0 || count > MaxBones || (long)count * BoneBytes > cursor.Remaining)
            throw new InvalidPackageException($"Mesh declares {count} bones.");

        var bones = new MeshBone[count];

        for (int i = 0; i < count; i++)
        {
            int nameIndex = cursor.ReadInt32($"bone {i} name");
            int nameNumber = cursor.ReadInt32($"bone {i} name number");
            cursor.Skip(sizeof(uint));    // flags

            var orientation = new Quaternion(
                cursor.ReadSingle($"bone {i} orientation x"),
                cursor.ReadSingle($"bone {i} orientation y"),
                cursor.ReadSingle($"bone {i} orientation z"),
                cursor.ReadSingle($"bone {i} orientation w"));

            var position = new Vector3(
                cursor.ReadSingle($"bone {i} position x"),
                cursor.ReadSingle($"bone {i} position y"),
                cursor.ReadSingle($"bone {i} position z"));

            int childCount = cursor.ReadInt32($"bone {i} child count");
            int parentIndex = cursor.ReadInt32($"bone {i} parent");

            cursor.Skip(4);   // bone colour

            bones[i] = new MeshBone
            {
                Name = nameIndex >= 0 && nameIndex < names.Count
                    ? names.Resolve(nameIndex, nameNumber)
                    : $"bone{i}",
                ParentIndex = parentIndex,
                ChildCount = childCount,
                Orientation = orientation,
                Position = position,
            };
        }

        return bones;
    }

    private static SkeletalMeshLod ReadLod(ref PackageCursor cursor, int lodIndex, bool hasVertexColours)
    {
        IReadOnlyList<MeshSection> sections = ReadSections(ref cursor, lodIndex);
        IReadOnlyList<int> indices = ReadIndexBuffer(ref cursor, lodIndex, out bool indicesOnProcessor);

        IReadOnlyList<int> activeBones = ReadNumberArray(
            ref cursor, sizeof(ushort), $"LOD {lodIndex} active bones");

        IReadOnlyList<MeshChunk> chunks = ReadChunks(ref cursor, lodIndex);

        long declaredSize = cursor.ReadUInt32($"LOD {lodIndex} declared size");

        int vertexCount = (int)cursor.ReadUInt32($"LOD {lodIndex} vertex count");
        if (vertexCount < 0)
            throw new InvalidPackageException($"LOD {lodIndex} declares {vertexCount} vertices.");

        IReadOnlyList<int> requiredBones = ReadNumberArray(
            ref cursor, sizeof(byte), $"LOD {lodIndex} required bones");
        int pointIndicesAt = cursor.Position;
        SkipBulkData(ref cursor, $"LOD {lodIndex} point indices");

        // Kept whole so it can be written back as it was found.
        int pointIndicesLength = cursor.Position - pointIndicesAt;
        cursor.Seek(pointIndicesAt);
        byte[] pointIndices = cursor.ReadBytes(pointIndicesLength, "point indices block").ToArray();

        int uvSets = (int)cursor.ReadUInt32($"LOD {lodIndex} texture coordinate sets");
        if (uvSets is < 1 or > 4) uvSets = 1;

        (VertexLayout layout,
         IReadOnlyList<Vector3> positions,
         IReadOnlyList<Vector3> normals,
         IReadOnlyList<Vector4> tangents,
         IReadOnlyList<Vector2> texCoords,
         IReadOnlyList<VertexInfluence> influences,
         int vertexDataOffset,
         Vector3 packedOrigin,
         Vector3 packedExtension,
         uint packedPositionFlag,
         IReadOnlyList<byte> tangentFrames) = ReadVertexBuffer(ref cursor, lodIndex, uvSets, chunks);

        // Nothing below is needed to draw this level of detail, but the cursor
        // has to cross it intact or the next level starts in the wrong place.
        if (hasVertexColours) SkipBulkArray(ref cursor, $"LOD {lodIndex} vertex colours");

        SkipVertexInfluences(ref cursor, lodIndex);
        bool adjacencyOnProcessor = cursor.PeekUInt32(cursor.Position) != 0;
        SkipIndexContainer(ref cursor, $"LOD {lodIndex} adjacency indices");

        return new SkeletalMeshLod
        {
            Sections = sections,
            Indices = indices,
            VertexCount = vertexCount,
            Layout = layout,
            Positions = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = texCoords,
            Influences = influences,
            Chunks = chunks,
            VertexDataOffset = vertexDataOffset,
            PackedOrigin = packedOrigin,
            PackedExtension = packedExtension,
            PackedPositionFlag = packedPositionFlag,
            TangentFrames = tangentFrames,
            AdjacencyIndicesStayOnTheProcessor = adjacencyOnProcessor,
            IndicesStayOnTheProcessor = indicesOnProcessor,
            DeclaredSize = declaredSize,
            RawPointIndices = pointIndices,
            ActiveBones = activeBones,
            RequiredBones = requiredBones,
        };
    }

    private static IReadOnlyList<MeshSection> ReadSections(ref PackageCursor cursor, int lodIndex)
    {
        const int sectionBytes = (sizeof(ushort) * 2) + (sizeof(uint) * 2) + sizeof(byte);

        int count = cursor.ReadInt32($"LOD {lodIndex} section count");
        if (count < 0 || count > MaxSections || (long)count * sectionBytes > cursor.Remaining)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} sections.");

        var sections = new MeshSection[count];

        for (int i = 0; i < count; i++)
        {
            sections[i] = new MeshSection
            {
                MaterialIndex = cursor.ReadUInt16($"section {i} material"),
                ChunkIndex = cursor.ReadUInt16($"section {i} chunk"),
                BaseIndex = (int)cursor.ReadUInt32($"section {i} base index"),
                TriangleCount = (int)cursor.ReadUInt32($"section {i} triangle count"),
            };

            cursor.Skip(sizeof(byte));   // triangle sorting
        }

        return sections;
    }

    /// <summary>
    /// Reads the index buffer, which states its own element width so both
    /// sixteen and thirty-two bit indices are handled.
    /// </summary>
    private static IReadOnlyList<int> ReadIndexBuffer(
        ref PackageCursor cursor, int lodIndex, out bool staysOnProcessor)
    {
        // A four-byte flag saying whether the buffer stays resident for the CPU,
        // then a single byte naming the index width, then the array itself as a
        // width and a count. The width is stated twice; disagreement means the
        // cursor is not where it should be, which is worth failing on loudly.
        staysOnProcessor = cursor.ReadUInt32($"LOD {lodIndex} indices stay on the processor") != 0;

        int elementWidth = cursor.ReadBytes(1, $"LOD {lodIndex} index width")[0];
        int declaredWidth = cursor.ReadInt32($"LOD {lodIndex} index element size");
        int count = cursor.ReadInt32($"LOD {lodIndex} index count");

        if (elementWidth is not (2 or 4) || declaredWidth != elementWidth)
        {
            throw new InvalidPackageException(
                $"LOD {lodIndex} index buffer states a width of {elementWidth} and again {declaredWidth}.");
        }

        if (count < 0 || (long)count * elementWidth > cursor.Remaining)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} indices.");

        var indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = elementWidth == 2
                ? cursor.ReadUInt16($"index {i}")
                : cursor.ReadInt32($"index {i}");
        }

        return indices;
    }

    /// <summary>
    /// Reads the vertex buffer, determining its layout from the element size the
    /// file declares rather than assuming how the layout flags are stored.
    /// </summary>
    private static (VertexLayout Layout,
                    IReadOnlyList<Vector3> Positions,
                    IReadOnlyList<Vector3> Normals,
                    IReadOnlyList<Vector4> Tangents,
                    IReadOnlyList<Vector2> TexCoords,
                    IReadOnlyList<VertexInfluence> Influences,
                    int VertexDataOffset,
                    Vector3 PackedOrigin,
                    Vector3 PackedExtension,
                    uint PackedPositionFlag,
                    IReadOnlyList<byte> TangentFrames)
        ReadVertexBuffer(
            ref PackageCursor cursor, int lodIndex, int uvSets, IReadOnlyList<MeshChunk> chunks)
    {
        // The buffer repeats its texture coordinate count, then states its two
        // layout flags as full words, then the range packed positions are scaled
        // into, then the size of one vertex and how many there are.
        int repeatedUvSets = (int)cursor.ReadUInt32($"LOD {lodIndex} vertex buffer texture coordinate sets");
        if (repeatedUvSets is >= 1 and <= 4) uvSets = repeatedUvSets;

        bool fullPrecisionUvs = cursor.ReadUInt32("full precision texture coordinates") != 0;
        // The flag says packed on a costume whose vertices are plainly full
        // precision, so it is not what settles the layout — the element size
        // is. It is kept all the same, to be written back as it was found.
        uint packedPositionFlag = cursor.ReadUInt32("packed position flag");

        var extension = new Vector3(
            cursor.ReadSingle("mesh extension x"),
            cursor.ReadSingle("mesh extension y"),
            cursor.ReadSingle("mesh extension z"));
        var origin = new Vector3(
            cursor.ReadSingle("mesh origin x"),
            cursor.ReadSingle("mesh origin y"),
            cursor.ReadSingle("mesh origin z"));

        int elementSize = cursor.ReadInt32($"LOD {lodIndex} vertex element size");
        int count = cursor.ReadInt32($"LOD {lodIndex} vertex count");

        if (count < 0 || elementSize <= 0 || (long)count * elementSize > cursor.Remaining)
        {
            throw new InvalidPackageException(
                $"LOD {lodIndex} declares {count} vertices of {elementSize} bytes.");
        }

        // The flag says how positions are meant to be stored; the element size
        // says how much room they actually take. Where they disagree the size
        // wins, because it is what the byte offsets have to agree with.
        int positionBytes = elementSize - VertexLayout.FixedBytes - (uvSets * (fullPrecisionUvs ? 8 : 4));
        if (positionBytes is not (4 or 12))
        {
            throw new InvalidPackageException(
                $"LOD {lodIndex} leaves {positionBytes} bytes for a position in a {elementSize}-byte vertex.");
        }

        bool packedPosition = positionBytes == 4;
        var layout = new VertexLayout(packedPosition, fullPrecisionUvs, uvSets);

        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var tangents = new Vector4[count];
        var texCoords = new Vector2[count];
        var influences = new VertexInfluence[count];

        // Which run a vertex belongs to decides how its bone numbers are read.
        // Walked forward rather than searched, because the runs are in order and
        // a search per vertex would be the slowest part of reading a model.
        int chunkAt = 0;

        MeshChunk? ChunkFor(int vertex)
        {
            while (chunkAt < chunks.Count - 1 && !chunks[chunkAt].Covers(vertex)) chunkAt++;
            return chunks.Count > 0 && chunks[chunkAt].Covers(vertex) ? chunks[chunkAt] : null;
        }

        int vertexStart = cursor.Position;
        int uvOffset = layout.PositionOffset + positionBytes;

        var tangentFrames = new byte[count * VertexLayout.TangentFrameBytes];

        for (int i = 0; i < count; i++)
        {
            int vertex = vertexStart + (i * elementSize);
            int at = vertex + layout.PositionOffset;

            for (int b = 0; b < VertexLayout.TangentFrameBytes; b++)
                tangentFrames[(i * VertexLayout.TangentFrameBytes) + b] = cursor.PeekByte(vertex + b);

            positions[i] = packedPosition
                ? UnpackPosition(cursor.PeekUInt32(at), origin, extension)
                : new Vector3(
                    cursor.PeekSingle(at),
                    cursor.PeekSingle(at + 4),
                    cursor.PeekSingle(at + 8));

            // The tangent frame leads the vertex: the direction along the
            // surface first, then the direction away from it. Only the second
            // is needed to light the model.
            uint packedNormal = cursor.PeekUInt32(vertex + sizeof(uint));
            normals[i] = UnpackNormal(packedNormal);

            // The handedness rides on the direction away from the surface, not
            // on the one along it.
            //
            // It was read off the wrong one of the two, and the giveaway was
            // that it never came out negative: across two whole costumes, all
            // 10,796 and all 4,553 corners agreed. A sign that is never
            // negative is not a sign. A character is modelled once and
            // mirrored, so about half of it should disagree, and where it does
            // not the mirrored half is lit inside out - which shows up as a
            // seam straight down the middle of a face.
            uint packedTangent = cursor.PeekUInt32(vertex);
            Vector3 tangent = UnpackNormal(packedTangent);
            float handedness = ((((packedNormal >> 24) & 0xFF) / 127.5f) - 1f) < 0f ? -1f : 1f;

            tangents[i] = new Vector4(tangent, handedness);

            // After the tangent frame come the bones this vertex follows and
            // how strongly, four of each, a byte apiece.
            influences[i] = ReadInfluence(ref cursor, vertex + VertexLayout.TangentFrameBytes, ChunkFor(i));

            texCoords[i] = fullPrecisionUvs
                ? new Vector2(cursor.PeekSingle(uvOffset + vertex), cursor.PeekSingle(uvOffset + vertex + 4))
                : new Vector2(
                    (float)cursor.PeekHalf(uvOffset + vertex),
                    (float)cursor.PeekHalf(uvOffset + vertex + 2));
        }

        cursor.Skip(count * elementSize);
        return (layout, positions, normals, tangents, texCoords, influences, vertexStart, origin, extension, packedPositionFlag, tangentFrames);
    }

    /// <summary>
    /// Reads which bones a vertex follows and how strongly.
    /// </summary>
    /// <remarks>
    /// Four bones and four weights, a byte each. A weight of zero means the
    /// slot is unused, so those are dropped rather than kept as bones pulling
    /// with no strength. The bone numbers are local to the vertex's own run and
    /// are turned into positions in the skeleton here, because everywhere else
    /// would then have to carry the run around to make sense of them.
    /// </remarks>
    private static VertexInfluence ReadInfluence(ref PackageCursor cursor, int at, MeshChunk? chunk)
    {
        const int slots = 4;

        var bones = new List<int>(slots);
        var weights = new List<float>(slots);

        for (int i = 0; i < slots; i++)
        {
            int weight = cursor.PeekByte(at + slots + i);
            if (weight == 0) continue;

            int local = cursor.PeekByte(at + i);

            int bone = chunk is not null && local < chunk.BoneMap.Count
                ? chunk.BoneMap[local]
                : local;

            bones.Add(bone);
            weights.Add(weight / 255f);
        }

        // Rounding leaves the four bytes adding to a little more or less than
        // the whole; a surface skinned to slightly more than one bone's worth
        // is visibly stretched, so the total is brought back to one.
        float total = 0f;
        foreach (float weight in weights) total += weight;

        if (total > 0.0001f)
        {
            for (int i = 0; i < weights.Count; i++) weights[i] /= total;
        }

        return new VertexInfluence { Bones = bones, Weights = weights };
    }

    /// <summary>
    /// Expands a direction stored as four bytes. Each byte spans the range from
    /// minus one to plus one; the fourth carries handedness and is not needed
    /// here. A zero-length result is replaced with a usable direction so a bad
    /// vertex cannot black out a whole surface.
    /// </summary>
    private static Vector3 UnpackNormal(uint packed)
    {
        var direction = new Vector3(
            ((packed & 0xFF) / 127.5f) - 1f,
            (((packed >> 8) & 0xFF) / 127.5f) - 1f,
            (((packed >> 16) & 0xFF) / 127.5f) - 1f);

        float length = direction.Length();
        return length > 1e-6f ? direction / length : Vector3.UnitZ;
    }

    /// <summary>
    /// Reconstructs a quantised position. The three components share a single
    /// word and are scaled back into the mesh's own coordinate range.
    /// </summary>
    private static Vector3 UnpackPosition(uint packed, Vector3 origin, Vector3 extension)
    {
        // Eleven bits for X and Y, ten for Z, each signed and centred.
        int x = (int)(packed & 0x7FF);
        int y = (int)((packed >> 11) & 0x7FF);
        int z = (int)((packed >> 22) & 0x3FF);

        if (x > 1023) x -= 2048;
        if (y > 1023) y -= 2048;
        if (z > 511) z -= 1024;

        return new Vector3(
            (x / 1023.0f * extension.X) + origin.X,
            (y / 1023.0f * extension.Y) + origin.Y,
            (z / 511.0f * extension.Z) + origin.Z);
    }

    /// <summary>Reads an array of small whole numbers, one or two bytes each.</summary>
    private static IReadOnlyList<int> ReadNumberArray(
        ref PackageCursor cursor, int elementSize, string what)
    {
        int count = cursor.ReadInt32($"{what} count");
        if (count < 0 || (long)count * elementSize > cursor.Remaining)
            throw new InvalidPackageException($"{what} declares {count} entries.");

        var values = new int[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = elementSize == sizeof(byte)
                ? cursor.ReadBytes(1, what)[0]
                : cursor.ReadUInt16(what);
        }

        return values;
    }

    private static void SkipArray(ref PackageCursor cursor, int elementSize, string what)
    {
        int count = cursor.ReadInt32($"{what} count");
        if (count < 0 || (long)count * elementSize > cursor.Remaining)
            throw new InvalidPackageException($"{what} declares {count} entries.");

        cursor.Skip(count * elementSize);
    }

    private static void SkipBulkData(ref PackageCursor cursor, string what)
    {
        const uint storedElsewhere = 0x01;   // the payload lives in a separate file
        const uint noPayload = 0x20;         // nothing was stored at all

        uint flags = cursor.ReadUInt32($"{what} flags");
        cursor.Skip(sizeof(int));                                   // element count
        int sizeOnDisk = cursor.ReadInt32($"{what} size on disk");
        cursor.Skip(sizeof(int));                                   // offset

        // Only an inline payload occupies bytes here; the other forms are a
        // header and nothing more, and skipping their stated size would walk
        // the cursor straight past the fields that follow.
        if ((flags & (storedElsewhere | noPayload)) != 0) return;

        if (sizeOnDisk > 0)
        {
            if (sizeOnDisk > cursor.Remaining)
                throw new InvalidPackageException($"{what} claims {sizeOnDisk} bytes.");
            cursor.Skip(sizeOnDisk);
        }
    }

    /// <summary>
    /// Steps over an array that states the size of one element and how many
    /// there are, which is how every buffer destined for the graphics card is
    /// stored.
    /// </summary>
    private static void SkipBulkArray(ref PackageCursor cursor, string what)
    {
        int elementSize = cursor.ReadInt32($"{what} element size");
        int count = cursor.ReadInt32($"{what} count");

        if (elementSize < 0 || count < 0 || (long)elementSize * count > cursor.Remaining)
            throw new InvalidPackageException($"{what} declares {count} entries of {elementSize} bytes.");

        cursor.Skip(elementSize * count);
    }

    /// <summary>Steps over an index buffer without decoding it.</summary>
    private static void SkipIndexContainer(ref PackageCursor cursor, string what)
    {
        cursor.Skip(sizeof(int));   // needs CPU access
        cursor.Skip(sizeof(byte));  // index width
        SkipBulkArray(ref cursor, what);
    }

    /// <summary>
    /// Steps over the alternative influence sets a model can carry, used when
    /// parts of it are swapped at runtime. Each set repeats the section and
    /// chunk lists, so this mirrors those rather than assuming a fixed size.
    /// </summary>
    private static void SkipVertexInfluences(ref PackageCursor cursor, int lodIndex)
    {
        const int influenceBytes = 8;   // four bone indices, four weights
        const int sectionBytes = (sizeof(ushort) * 2) + (sizeof(uint) * 2) + sizeof(byte);

        int count = cursor.ReadInt32($"LOD {lodIndex} influence set count");
        if (count < 0 || count > MaxSections)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} influence sets.");

        for (int i = 0; i < count; i++)
        {
            SkipArray(ref cursor, influenceBytes, $"LOD {lodIndex} influence set {i}");

            int mapCount = cursor.ReadInt32($"LOD {lodIndex} influence map {i} count");
            if (mapCount < 0 || (long)mapCount * (sizeof(int) * 3) > cursor.Remaining)
                throw new InvalidPackageException($"LOD {lodIndex} influence map declares {mapCount} entries.");

            for (int m = 0; m < mapCount; m++)
            {
                cursor.Skip(sizeof(int) * 2);   // the pair of bones this entry keys on
                SkipArray(ref cursor, sizeof(uint), $"LOD {lodIndex} influence map {i} entry {m}");
            }

            SkipArray(ref cursor, sectionBytes, $"LOD {lodIndex} influence set {i} sections");
            SkipChunks(ref cursor, lodIndex);
            SkipArray(ref cursor, sizeof(byte), $"LOD {lodIndex} influence set {i} required bones");

            cursor.Skip(sizeof(byte));   // what the set is used for
        }
    }

    /// <summary>
    /// Steps over the chunk list. A chunk may carry its own CPU-side vertices —
    /// rigidly skinned ones at 61 bytes each and soft ones at 68 — before its
    /// bone map. Cooked content usually stores none, but the arrays are always
    /// present and their lengths must be honoured or every later field shifts.
    /// </summary>
    private static void SkipChunks(ref PackageCursor cursor, int lodIndex) =>
        ReadChunks(ref cursor, lodIndex);

    /// <summary>
    /// Reads the chunk list, keeping each chunk's bone map.
    /// </summary>
    /// <remarks>
    /// A vertex names the bones it follows by a number local to its chunk, not
    /// by a number into the skeleton. The bone map is what turns one into the
    /// other, so without it a vertex's weights cannot be attributed to any
    /// named bone.
    /// </remarks>
    private static IReadOnlyList<MeshChunk> ReadChunks(ref PackageCursor cursor, int lodIndex)
    {
        const int rigidVertexBytes = 61;
        const int softVertexBytes = 68;

        int count = cursor.ReadInt32($"LOD {lodIndex} chunk count");
        if (count < 0 || count > MaxSections)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} chunks.");

        var chunks = new List<MeshChunk>(count);

        for (int i = 0; i < count; i++)
        {
            int baseVertex = (int)cursor.ReadUInt32($"chunk {i} base vertex");

            SkipArray(ref cursor, rigidVertexBytes, $"chunk {i} rigid vertices");
            SkipArray(ref cursor, softVertexBytes, $"chunk {i} soft vertices");

            int boneCount = cursor.ReadInt32($"chunk {i} bone map count");
            if (boneCount < 0 || (long)boneCount * sizeof(ushort) > cursor.Remaining)
                throw new InvalidPackageException($"Chunk {i} declares {boneCount} bones.");

            var boneMap = new int[boneCount];
            for (int b = 0; b < boneCount; b++)
                boneMap[b] = cursor.ReadUInt16($"chunk {i} bone {b}");

            int rigid = cursor.ReadInt32($"chunk {i} rigid count");
            int soft = cursor.ReadInt32($"chunk {i} soft count");

            int mostInfluences = cursor.ReadInt32($"chunk {i} most bones any one vertex follows");

            chunks.Add(new MeshChunk
            {
                BaseVertexIndex = baseVertex,
                VertexCount = Math.Max(0, rigid) + Math.Max(0, soft),
                BoneMap = boneMap,
                RigidVertexCount = Math.Max(0, rigid),
                SoftVertexCount = Math.Max(0, soft),
                MaxBoneInfluences = mostInfluences,
            });
        }

        return chunks;
    }
}
