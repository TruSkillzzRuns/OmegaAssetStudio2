using System.Buffers.Binary;
using System.Numerics;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One vertex pushed out of place while a power plays.</summary>
public readonly record struct MorphDelta(Vector3 Move, uint PackedDirection, int Vertex);

/// <summary>The displacements for one level of detail.</summary>
public sealed record MorphLevel
{
    public required IReadOnlyList<MorphDelta> Deltas { get; init; }

    /// <summary>How many vertices the model had when these were recorded.</summary>
    public required int BaseVertexCount { get; init; }
}

/// <summary>
/// A set of per-vertex displacements that reshape a model while a power plays.
/// </summary>
/// <remarks>
/// A mouth that opens, a limb that stretches, a body that rolls into a ball:
/// these are not the skeleton but a recorded list of "move this vertex by this
/// much", switched on for the duration. Each names the vertex it moves **by
/// number**.
/// <para>
/// That is why writing a model back matters to them. Rewriting a model
/// renumbers its vertices — runs own their own copies, so a model can come out
/// with more than it went in with — and every displacement then lands on a
/// vertex it was never meant for. The model stands correctly at rest and comes
/// apart the moment the power fires.
/// </para>
/// </remarks>
public sealed record MorphTarget
{
    public required string Name { get; init; }
    public required int ExportIndex { get; init; }

    /// <summary>Where the displacements begin, within the object's own bytes.</summary>
    public required int TailStart { get; init; }

    public required IReadOnlyList<MorphLevel> Levels { get; init; }

    public int DeltaCount => Levels.Sum(l => l.Deltas.Count);
}

/// <summary>Reads and rewrites the displacements a power uses.</summary>
public static class MorphTargetReader
{
    /// <summary>The class the game gives these objects.</summary>
    public const string MorphTargetClass = "morphtarget";

    /// <summary>
    /// Bytes one displacement occupies: where it moves to, which way the
    /// surface then faces, and which vertex it belongs to.
    /// </summary>
    /// <remarks>
    /// Twenty, not the twenty-eight of stock UE3: this fork packs the direction
    /// into four bytes rather than three full numbers. Verified by arithmetic on
    /// four real sets, where the count times twenty plus twelve came to exactly
    /// the bytes present.
    /// </remarks>
    private const int DeltaBytes = 20;

    /// <summary>Every set of displacements in a package.</summary>
    public static IReadOnlyList<MorphTarget> ReadAll(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var found = new List<MorphTarget>();

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i).Equals(MorphTargetClass, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryRead(package, i) is { } target) found.Add(target);
        }

        return found;
    }

    /// <summary>Reads one, or nothing when its bytes do not describe any.</summary>
    public static MorphTarget? TryRead(Package package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);

        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);

        int at;

        try
        {
            PropertyBag properties = PropertyReader.Read(data, package.Names);
            at = properties.PayloadOffset;
        }
        catch (InvalidPackageException)
        {
            return null;
        }

        if (at < 0 || at + sizeof(int) > data.Length) return null;

        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += sizeof(int);

        // A count outside this is not a level count; it is bytes being read as
        // one, which means the displacements are not stored here at all.
        if (levelCount is < 0 or > 8) return null;

        var levels = new List<MorphLevel>(levelCount);

        for (int level = 0; level < levelCount; level++)
        {
            if (at + sizeof(int) > data.Length) return null;

            int count = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            at += sizeof(int);

            if (count < 0 || at + (count * DeltaBytes) + sizeof(int) > data.Length) return null;

            var deltas = new MorphDelta[count];

            for (int d = 0; d < count; d++)
            {
                int from = at + (d * DeltaBytes);

                deltas[d] = new MorphDelta(
                    new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(data[from..]),
                        BinaryPrimitives.ReadSingleLittleEndian(data[(from + 4)..]),
                        BinaryPrimitives.ReadSingleLittleEndian(data[(from + 8)..])),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(from + 12)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(data[(from + 16)..]));
            }

            at += count * DeltaBytes;

            levels.Add(new MorphLevel
            {
                Deltas = deltas,
                BaseVertexCount = BinaryPrimitives.ReadInt32LittleEndian(data[at..]),
            });

            at += sizeof(int);
        }

        return new MorphTarget
        {
            Name = package.GetExportName(exportIndex),
            ExportIndex = exportIndex,
            TailStart = 0,
            Levels = levels,
        };
    }

    /// <summary>
    /// Produces the object's bytes with its displacements replaced.
    /// </summary>
    /// <remarks>
    /// Everything before them is copied across untouched: the object's
    /// properties, including which model it belongs to, are unaffected by
    /// which vertices it moves.
    /// </remarks>
    public static byte[] Replace(Package package, MorphTarget target, IReadOnlyList<MorphLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(levels);

        ReadOnlySpan<byte> data = package.GetExportData(target.ExportIndex);

        PropertyBag properties = PropertyReader.Read(data, package.Names);

        var output = new MemoryStream(data.Length);
        output.Write(data[..properties.PayloadOffset]);

        var four = new byte[sizeof(int)];

        void WriteInt(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four, value);
            output.Write(four);
        }

        void WriteFloat(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(four, value);
            output.Write(four);
        }

        WriteInt(levels.Count);

        foreach (MorphLevel level in levels)
        {
            WriteInt(level.Deltas.Count);

            foreach (MorphDelta delta in level.Deltas)
            {
                WriteFloat(delta.Move.X);
                WriteFloat(delta.Move.Y);
                WriteFloat(delta.Move.Z);

                BinaryPrimitives.WriteUInt32LittleEndian(four, delta.PackedDirection);
                output.Write(four);

                WriteInt(delta.Vertex);
            }

            WriteInt(level.BaseVertexCount);
        }

        return output.ToArray();
    }
}
