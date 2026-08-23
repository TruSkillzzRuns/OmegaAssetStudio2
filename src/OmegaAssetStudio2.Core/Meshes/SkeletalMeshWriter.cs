using System.Buffers.Binary;
using System.Numerics;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>Why a model could not be written back.</summary>
public sealed class MeshWriteException : Exception
{
    public MeshWriteException(string message) : base(message) { }
}

/// <summary>
/// Writes moved vertices back into the object they were read from.
/// </summary>
/// <remarks>
/// This changes where a model's vertices sit and nothing else. It does not
/// change how many there are, how they are laid out, which bones they follow,
/// or any of the structure around them — so the object stays exactly the size
/// it was, and every other object in the package stays where it is.
/// <para>
/// That is a deliberate limit rather than an unfinished one. A retarget of the
/// kind this application performs moves vertices and leaves the topology alone,
/// which is precisely the case this covers, and covering only it means the
/// riskiest part of writing to a game — moving everything else — never happens.
/// </para>
/// </remarks>
public static class SkeletalMeshWriter
{
    /// <summary>
    /// Produces the object's bytes with a level of detail's vertices moved.
    /// </summary>
    /// <param name="package">The package the model was read from.</param>
    /// <param name="exportIndex">Which object in it.</param>
    /// <param name="lod">The level of detail as it was read.</param>
    /// <param name="positions">Where each vertex should now sit.</param>
    /// <param name="normals">Which way the surface should now face, or null to leave them.</param>
    /// <returns>The object's bytes, the same length as before.</returns>
    public static byte[] MoveVertices(
        Package package,
        int exportIndex,
        SkeletalMeshLod lod,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? normals = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(positions);

        if (lod.VertexDataOffset < 0)
            throw new MeshWriteException("This model did not come from a package, so it cannot be written back.");

        if (positions.Count != lod.Positions.Count)
        {
            throw new MeshWriteException(
                $"This model has {lod.Positions.Count:N0} vertices but {positions.Count:N0} were given. " +
                "Writing back only moves vertices; it cannot add or remove them.");
        }

        if (normals is not null && normals.Count != positions.Count)
            throw new MeshWriteException("There must be one direction for each vertex, or none at all.");

        byte[] data = package.GetExportData(exportIndex).ToArray();

        int stride = lod.Layout.Stride;
        int at = lod.VertexDataOffset;

        if (stride <= 0)
            throw new MeshWriteException("This model does not say how large a vertex is.");

        if (at + ((long)stride * positions.Count) > data.Length)
            throw new MeshWriteException("The vertices do not lie inside this object.");

        bool packed = lod.Layout.PackedPosition;

        for (int i = 0; i < positions.Count; i++)
        {
            int vertex = at + (i * stride);

            if (normals is not null)
                WriteNormal(data, vertex + sizeof(uint), normals[i]);

            int positionAt = vertex + lod.Layout.PositionOffset;

            if (packed) WritePackedPosition(data, positionAt, positions[i], lod.PackedOrigin, lod.PackedExtension);
            else WritePosition(data, positionAt, positions[i]);
        }

        return data;
    }

    private static void WritePosition(byte[] data, int at, Vector3 position)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at, sizeof(float)), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 4, sizeof(float)), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 8, sizeof(float)), position.Z);
    }

    /// <summary>
    /// Writes a position back into the four bytes a quantised one occupies.
    /// </summary>
    /// <remarks>
    /// The range it is measured against cannot be changed without moving every
    /// other vertex in the model, so a position outside that range is clamped
    /// to its edge rather than wrapping round to the far side, which is what
    /// the arithmetic would otherwise do.
    /// </remarks>
    private static void WritePackedPosition(byte[] data, int at, Vector3 position, Vector3 origin, Vector3 extension)
    {
        int x = Quantise(position.X, origin.X, extension.X, 1023);
        int y = Quantise(position.Y, origin.Y, extension.Y, 1023);
        int z = Quantise(position.Z, origin.Z, extension.Z, 511);

        uint packed =
            ((uint)(x & 0x7FF)) |
            ((uint)(y & 0x7FF) << 11) |
            ((uint)(z & 0x3FF) << 22);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at, sizeof(uint)), packed);
    }

    private static int Quantise(float value, float origin, float extension, int limit)
    {
        if (MathF.Abs(extension) < 0.000001f) return 0;

        float scaled = (value - origin) / extension * limit;

        return (int)MathF.Round(Math.Clamp(scaled, -limit - 1, limit));
    }

    /// <summary>
    /// Writes a direction back into the four bytes it occupies.
    /// </summary>
    /// <remarks>
    /// The fourth byte carries the handedness of the surface, and nothing here
    /// changes that, so it is left exactly as it was found.
    /// </remarks>
    private static void WriteNormal(byte[] data, int at, Vector3 normal)
    {
        float length = normal.Length();
        Vector3 unit = length > 0.000001f ? normal / length : Vector3.UnitZ;

        data[at] = Encode(unit.X);
        data[at + 1] = Encode(unit.Y);
        data[at + 2] = Encode(unit.Z);
    }

    private static byte Encode(float component) =>
        (byte)Math.Clamp(MathF.Round((component + 1f) * 127.5f), 0f, 255f);
}
