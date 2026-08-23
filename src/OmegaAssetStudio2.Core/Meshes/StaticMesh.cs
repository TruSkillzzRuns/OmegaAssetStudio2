using System.Numerics;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One run of triangles in a static mesh, with the material it uses.</summary>
public sealed record StaticMeshPart
{
    public required int FirstIndex { get; init; }
    public required int TriangleCount { get; init; }
    public required ObjectReference Material { get; init; }

    public override string ToString() => $"{TriangleCount} triangles from {FirstIndex}";
}

/// <summary>
/// A model that does not bend: a prop, a weapon, or a piece hung on a costume.
/// </summary>
/// <remarks>
/// Read only as far as the first level of detail, because that is what is
/// drawn. Everything past it in the file - the collision tree, the optimisation
/// settings, the console layouts - is walked over rather than kept.
/// </remarks>
public sealed record StaticMesh
{
    public required string Name { get; init; }
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<Vector4> Tangents { get; init; }
    public required IReadOnlyList<Vector2> TexCoords { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>
    /// A colour painted on each vertex, where the mesh carries any.
    /// </summary>
    /// <remarks>
    /// A piece whose materials hold no picture and no readable colour can still
    /// be coloured this way, which is how a string of lights gets its different
    /// coloured bulbs from one mesh and one shape.
    /// </remarks>
    public IReadOnlyList<Vector4> Colours { get; init; } = [];

    public required IReadOnlyList<StaticMeshPart> Parts { get; init; }

    /// <summary>The materials the mesh names, in the order it names them.</summary>
    public required IReadOnlyList<ObjectReference> Materials { get; init; }

    public bool HasGeometry => Positions.Count > 0 && Indices.Count > 0;

    public override string ToString() =>
        $"{Name}: {Positions.Count} vertices, {Indices.Count / 3} triangles";
}

/// <summary>
/// Reads a static mesh the way this game's package format writes one.
/// </summary>
/// <remarks>
/// The layout is the one the reader that has been parsing these files correctly
/// for years defines, followed field for field rather than worked out afresh:
/// bounds, a collision tree, a version, an optional editor copy of the mesh,
/// optimisation settings, two flags, and then the levels of detail. Each level
/// is a bulk-data block of editor triangles, the runs of triangles and their
/// materials, a position buffer, a vertex buffer, a colour buffer, the vertex
/// count, and three index buffers.
/// </remarks>
public static class StaticMeshReader
{
    private const int MostVertices = 4_000_000;
    private const int MostIndices = 12_000_000;
    private const int MostParts = 4096;

    public static StaticMesh? TryRead(Package package, int exportIndex) =>
        TryRead(package, exportIndex, out _);

    /// <summary>As above, and says where the reading stopped when it does.</summary>
    public static StaticMesh? TryRead(Package package, int exportIndex, out string trouble)
    {
        ArgumentNullException.ThrowIfNull(package);

        trouble = string.Empty;

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        var materials = new List<ObjectReference>();

        try
        {
            ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
            var cursor = new PackageCursor(data, properties.PayloadOffset);

            // Where it sits and how big it is, then the tree used for hitting
            // it with things. Neither says anything about how it looks.
            cursor.Skip((sizeof(float) * 7) + sizeof(int));     // bounds, then the body setup

            SkipCollisionTree(ref cursor);

            cursor.Skip(sizeof(int));                            // which version wrote it

            // An editor copy of the mesh, kept in some packages and not others.
            if (cursor.ReadInt32("has an editor copy") != 0) SkipLevel(ref cursor);

            SkipOptimisation(ref cursor);

            cursor.Skip(sizeof(int) * 2);                        // simplified, and a proxy

            int levels = cursor.ReadInt32("levels of detail");
            if (levels <= 0 || levels > 64) return null;

            // Only the first is drawn.
            StaticMesh? made = ReadLevel(ref cursor, package, package.GetExportName(exportIndex), materials);

            if (made is null) trouble = "the level did not describe a model";

            return made;
        }
        catch (Exception ex)
        {
            trouble = ex.Message;
            return null;
        }
    }

    private static StaticMesh? ReadLevel(
        ref PackageCursor cursor, Package package, string name, List<ObjectReference> materials)
    {
        SkipBulk(ref cursor);

        var parts = new List<StaticMeshPart>();

        int partCount = cursor.ReadInt32("runs of triangles");
        if (partCount < 0 || partCount > MostParts) throw new InvalidPackageException($"runs of triangles = {partCount} at {cursor.Position}");

        for (int i = 0; i < partCount; i++)
        {
            var material = new ObjectReference(cursor.ReadInt32($"run {i} material"));

            cursor.Skip(sizeof(int) * 3);                        // three collision and shadow flags

            int firstIndex = cursor.ReadInt32($"run {i} first index");
            int triangles = cursor.ReadInt32($"run {i} triangles");

            cursor.Skip(sizeof(int) * 2);                        // the range of vertices it touches

            cursor.Skip(sizeof(int));                            // which material slot it is

            int fragments = cursor.ReadInt32($"run {i} fragments");
            if (fragments < 0 || fragments > MostParts) throw new InvalidPackageException($"fragments = {fragments} at {cursor.Position}");
            cursor.Skip(fragments * sizeof(int) * 2);

            // One byte, not four, and a layout for a console follows it when set.
            if (cursor.ReadBytes(1, $"run {i} has a console layout")[0] != 0) SkipConsole(ref cursor);

            materials.Add(material);

            parts.Add(new StaticMeshPart
            {
                FirstIndex = firstIndex,
                TriangleCount = triangles,
                Material = material,
            });
        }

        // Where each vertex is.
        int positionStride = cursor.ReadInt32("position stride");
        int positionCount = cursor.ReadInt32("position count");

        int positionSize = cursor.ReadInt32("position element size");
        int positions = cursor.ReadInt32("positions");

        if (positions < 0 || positions > MostVertices) throw new InvalidPackageException($"positions = {positions} at {cursor.Position}");
        if (positionSize <= 0 || positionSize > 64) throw new InvalidPackageException($"position element size = {positionSize} (stride {positionStride}, count {positionCount}) at {cursor.Position}");

        var places = new List<Vector3>(positions);

        for (int i = 0; i < positions; i++)
        {
            float x = cursor.ReadSingle("x");
            float y = cursor.ReadSingle("y");
            float z = cursor.ReadSingle("z");

            cursor.Skip(positionSize - (sizeof(float) * 3));

            places.Add(new Vector3(x, y, z));
        }

        // Which way each vertex faces, and where it sits on its picture.
        int uvSets = cursor.ReadInt32("texture coordinate sets");
        cursor.Skip(sizeof(int) * 2);                            // stride and count, restated

        bool fullPrecisionUvs = cursor.ReadInt32("full precision") != 0;

        int vertexSize = cursor.ReadInt32("vertex element size");
        int vertices = cursor.ReadInt32("vertices");

        if (vertices < 0 || vertices > MostVertices) throw new InvalidPackageException($"vertices = {vertices} at {cursor.Position}");
        if (vertexSize <= 0 || vertexSize > 256) throw new InvalidPackageException($"vertex element size = {vertexSize} at {cursor.Position}");
        if (uvSets < 0 || uvSets > 8) throw new InvalidPackageException($"uv sets = {uvSets} at {cursor.Position}");

        var normals = new List<Vector3>(vertices);
        var tangents = new List<Vector4>(vertices);
        var uvs = new List<Vector2>(vertices);

        for (int i = 0; i < vertices; i++)
        {
            int began = cursor.Position;

            uint packedTangent = cursor.ReadUInt32("tangent");
            uint packedNormal = cursor.ReadUInt32("normal");

            normals.Add(Unpack(packedNormal));

            Vector3 along = Unpack(packedTangent);
            float handed = ((((packedNormal >> 24) & 0xFF) / 127.5f) - 1f) < 0f ? -1f : 1f;
            tangents.Add(new Vector4(along, handed));

            // The first set of coordinates is the one the surface is painted
            // with; any others are for lighting that is not drawn here.
            float u = 0f, v = 0f;

            if (uvSets > 0)
            {
                u = fullPrecisionUvs ? cursor.ReadSingle("u") : (float)cursor.PeekHalf(cursor.Position);
                if (!fullPrecisionUvs) cursor.Skip(sizeof(ushort));

                v = fullPrecisionUvs ? cursor.ReadSingle("v") : (float)cursor.PeekHalf(cursor.Position);
                if (!fullPrecisionUvs) cursor.Skip(sizeof(ushort));
            }

            uvs.Add(new Vector2(u, v));

            cursor.Seek(began + vertexSize);
        }

        // Colours painted on the vertices, which nothing here uses. Written as
        // a width and a count, and then - only when the count is not nothing -
        // an array of its own.
        cursor.Skip(sizeof(int));                                // how wide each colour is
        int coloured = cursor.ReadInt32("coloured vertices");

        var painted = new List<Vector4>();

        if (coloured > 0)
        {
            int colourSize = cursor.ReadInt32("colour element size");
            int colours = cursor.ReadInt32("colours");
            if (colours < 0 || colours > MostVertices) throw new InvalidPackageException($"colours = {colours} at {cursor.Position}");

            // Stored as four bytes a vertex, blue first, which is how this
            // format writes a colour.
            for (int i = 0; i < colours; i++)
            {
                int began = cursor.Position;

                byte b = cursor.ReadBytes(1, "blue")[0];
                byte g = cursor.ReadBytes(1, "green")[0];
                byte r = cursor.ReadBytes(1, "red")[0];
                byte a = cursor.ReadBytes(1, "alpha")[0];

                painted.Add(new Vector4(r / 255f, g / 255f, b / 255f, a / 255f));

                cursor.Seek(began + colourSize);
            }
        }

        cursor.Skip(sizeof(int));                                // the vertex count, restated

        // The triangles.
        int indexSize = cursor.ReadInt32("index element size");
        int indices = cursor.ReadInt32("indices");

        if (indices < 0 || indices > MostIndices) throw new InvalidPackageException($"indices = {indices} at {cursor.Position}");
        if (indexSize != sizeof(ushort)) throw new InvalidPackageException($"index element size = {indexSize} at {cursor.Position}");

        var corners = new List<int>(indices);
        for (int i = 0; i < indices; i++) corners.Add(cursor.ReadUInt16($"index {i}"));

        return new StaticMesh
        {
            Name = name,
            Positions = places,
            Normals = normals,
            Tangents = tangents,
            TexCoords = uvs,
            Indices = corners,
            Colours = painted,
            Parts = parts,
            Materials = materials,
        };
    }

    private static Vector3 Unpack(uint packed)
    {
        float x = ((packed & 0xFF) / 127.5f) - 1f;
        float y = (((packed >> 8) & 0xFF) / 127.5f) - 1f;
        float z = (((packed >> 16) & 0xFF) / 127.5f) - 1f;

        var made = new Vector3(x, y, z);
        return made.LengthSquared() > 1e-8f ? Vector3.Normalize(made) : Vector3.UnitZ;
    }

    /// <summary>Walks past a level without keeping any of it.</summary>
    private static void SkipLevel(ref PackageCursor cursor)
    {
        var thrown = new List<ObjectReference>();
        ReadLevel(ref cursor, null!, string.Empty, thrown);
    }

    private static void SkipCollisionTree(ref PackageCursor cursor)
    {
        cursor.Skip(sizeof(float) * 6);                          // the box it sits in

        SkipSized(ref cursor);                                   // its nodes
        SkipSized(ref cursor);                                   // its triangles
    }

    /// <summary>An array written as its element size, its count, then its bytes.</summary>
    private static void SkipSized(ref PackageCursor cursor)
    {
        int size = cursor.ReadInt32("element size");
        int count = cursor.ReadInt32("count");

        if (size < 0 || count < 0) throw new InvalidPackageException("An array declares a negative size.");

        cursor.Skip(size * count);
    }

    private static void SkipOptimisation(ref PackageCursor cursor)
    {
        int count = cursor.ReadInt32("optimisation settings");
        if (count < 0 || count > 64) throw new InvalidPackageException("Too many optimisation settings.");

        // A byte, four numbers, a flag, a number, and three bytes.
        for (int i = 0; i < count; i++) cursor.Skip(1 + (sizeof(float) * 3) + sizeof(int) + sizeof(float) + 3);
    }

    private static void SkipConsole(ref PackageCursor cursor)
    {
        // Eight arrays of numbers, none of which is read on a desktop.
        for (int i = 0; i < 8; i++)
        {
            int count = cursor.ReadInt32($"console array {i}");
            if (count < 0 || count > MostVertices) throw new InvalidPackageException("A console array is too long.");

            cursor.Skip(count * (i < 2 ? sizeof(uint) : sizeof(ushort)));
        }
    }

    private static void SkipBulk(ref PackageCursor cursor)
    {
        cursor.Skip(sizeof(uint));                               // what kind of block it is

        int elements = cursor.ReadInt32("bulk elements");
        int onDisk = cursor.ReadInt32("bulk bytes");

        cursor.Skip(sizeof(int));                                // where it sits in the file

        if (onDisk < 0) throw new InvalidPackageException("A bulk block declares a negative size.");

        cursor.Skip(onDisk);
    }
}
