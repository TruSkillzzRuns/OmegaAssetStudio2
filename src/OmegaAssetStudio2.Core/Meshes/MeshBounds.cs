namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>
/// The volume a mesh occupies: a centre, a half-size on each axis, and the radius
/// of a sphere enclosing it.
/// </summary>
/// <remarks>
/// Stored as seven floats at the start of a mesh's binary payload. Verified
/// against real meshes: a large prop reads an extent of roughly 341 by 150 by 341
/// with an enclosing radius of 390, which is self-consistent — the radius must be
/// at least the diagonal of the extent and comfortably is.
/// </remarks>
public readonly record struct MeshBounds(
    float OriginX, float OriginY, float OriginZ,
    float ExtentX, float ExtentY, float ExtentZ,
    float Radius)
{
    /// <summary>Bytes this structure occupies.</summary>
    public const int ByteSize = sizeof(float) * 7;

    public float Width => ExtentX * 2f;
    public float Depth => ExtentY * 2f;
    public float Height => ExtentZ * 2f;

    /// <summary>
    /// Whether the values are self-consistent, meaning the payload was read from
    /// the right place.
    /// </summary>
    /// <remarks>
    /// The sphere and the box are each fitted to the same geometry independently,
    /// so the sphere is <em>not</em> required to reach the box's corners — those
    /// corners are usually empty space. What must hold is that the sphere covers
    /// the longest axis and does not exceed the corner distance.
    /// <para>
    /// An earlier version of this check demanded the sphere enclose the whole box
    /// and rejected nearly half of all real meshes as unreadable. Their bounds
    /// were correct; the test was wrong. A mesh measuring 446 by 428 by 137 has a
    /// corner distance of 633 and a fitted sphere of 620, which is entirely
    /// normal and was being thrown away.
    /// </para>
    /// </remarks>
    public bool IsPlausible
    {
        get
        {
            foreach (float value in new[] { OriginX, OriginY, OriginZ, ExtentX, ExtentY, ExtentZ, Radius })
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            }

            if (ExtentX < 0 || ExtentY < 0 || ExtentZ < 0 || Radius < 0) return false;

            // A degenerate mesh — a flat plane, or a single point — is legitimate.
            float longestAxis = Math.Max(ExtentX, Math.Max(ExtentY, ExtentZ));
            if (longestAxis == 0 && Radius == 0) return true;
            if (longestAxis == 0) return false;

            double cornerDistance = Math.Sqrt((ExtentX * ExtentX) + (ExtentY * ExtentY) + (ExtentZ * ExtentZ));

            // Tolerances are generous on purpose: this is a sanity check that the
            // bytes were read from the right offset, not a validation of the
            // artist's geometry.
            return Radius >= longestAxis * 0.99
                && Radius <= cornerDistance * 1.01;
        }
    }

    public static MeshBounds Read(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + ByteSize > data.Length)
            throw new Packages.InvalidPackageException(
                $"Mesh bounds at offset {offset} lie outside the {data.Length}-byte object.");

        return new MeshBounds(
            BitConverter.ToSingle(data[offset..]),
            BitConverter.ToSingle(data[(offset + 4)..]),
            BitConverter.ToSingle(data[(offset + 8)..]),
            BitConverter.ToSingle(data[(offset + 12)..]),
            BitConverter.ToSingle(data[(offset + 16)..]),
            BitConverter.ToSingle(data[(offset + 20)..]),
            BitConverter.ToSingle(data[(offset + 24)..]));
    }

    public string Describe() =>
        $"{Width:0.#} x {Depth:0.#} x {Height:0.#} (radius {Radius:0.#})";

    public override string ToString() => Describe();
}
