using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>One colour key inside a particle effect's colour curve.</summary>
public sealed record ParticleColourKey
{
    /// <summary>Position in the curve, zero-based.</summary>
    public required int Index { get; init; }

    public required MaterialColour Colour { get; init; }

    /// <summary>
    /// Offset of the three floats within the owning export's data, so the value
    /// can be rewritten without moving anything.
    /// </summary>
    public required int ValueOffset { get; init; }

    public override string ToString() => $"key {Index} = {Colour}";
}

/// <summary>A particle module that carries editable colour.</summary>
public sealed record ParticleColourModule
{
    public required string PackagePath { get; init; }
    public required int ExportIndex { get; init; }

    /// <summary>Module class, which says what the colour does.</summary>
    public required string ClassName { get; init; }

    public required string Name { get; init; }
    public required string ObjectPath { get; init; }

    /// <summary>Which property the colour came from.</summary>
    public required string PropertyName { get; init; }

    public required IReadOnlyList<ParticleColourKey> Keys { get; init; }

    public bool HasColours => Keys.Count > 0;

    public override string ToString() => $"{Name} ({ClassName}, {Keys.Count} colours)";
}

/// <summary>
/// Reads the colours out of particle effect modules.
/// </summary>
/// <remarks>
/// Effect colour is not stored as a plain value. It lives in a distribution whose
/// payload is a flat float array laid out as two leading values followed by
/// red-green-blue triplets. That layout is specific to this game's engine fork;
/// the generic stride rules produce wrong colours, so it is not inferred here but
/// taken as given and then checked.
/// <para>
/// Verified against real modules: an eight-float table decodes as two leading
/// values plus two colours, and the values that come out are the overbright
/// numbers effect art is authored with rather than arbitrary bytes.
/// </para>
/// </remarks>
public static class ParticleColourReader
{
    /// <summary>Module classes that carry an editable colour.</summary>
    public static readonly IReadOnlyList<string> ColourModuleClasses =
    [
        "particlemodulecolor",
        "particlemodulecoloroverlife",
        "particlemodulecolorscaleoverlife",
    ];

    /// <summary>Leading values in the table before the colours start.</summary>
    private const int TableHeaderFloats = 2;

    /// <summary>Floats per colour.</summary>
    private const int FloatsPerColour = 3;

    /// <summary>True when this class is one the reader understands.</summary>
    public static bool IsColourModule(string className) =>
        ColourModuleClasses.Contains(className, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a particle module's colours. Returns null when the export is not a
    /// colour module or its properties do not parse.
    /// </summary>
    public static ParticleColourModule? TryRead(Package package, int exportIndex)
    {
        string className = package.GetExportClassName(exportIndex);
        if (!IsColourModule(className)) return null;

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        // The colour-bearing property is whichever structure holds a vector
        // distribution; its name differs between module classes.
        PropertyTag? distribution = properties.Tags.FirstOrDefault(t =>
            t.TypeName.Equals("structproperty", StringComparison.OrdinalIgnoreCase) &&
            t.InnerName.Contains("distributionvector", StringComparison.OrdinalIgnoreCase));

        if (distribution is null) return null;

        IReadOnlyList<ParticleColourKey> keys = ReadKeys(distribution, package.Names);
        if (keys.Count == 0) return null;

        return new ParticleColourModule
        {
            PackagePath = package.Path,
            ExportIndex = exportIndex,
            ClassName = className,
            Name = package.GetExportName(exportIndex),
            ObjectPath = package.GetExportPath(exportIndex),
            PropertyName = distribution.Name,
            Keys = keys,
        };
    }

    /// <summary>
    /// Pulls the colour keys out of a vector distribution.
    /// </summary>
    private static IReadOnlyList<ParticleColourKey> ReadKeys(PropertyTag distribution, NameTable names)
    {
        // The distribution is itself a property block, with no leading net index.
        PropertyBag? inner = PropertyReader.TryRead(distribution.Value.Span, names, skipNetIndex: false);
        if (inner is null) return [];

        PropertyTag? table = inner.Tags.FirstOrDefault(t =>
            t.TypeName.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase));

        if (table is null || table.Value.Length < sizeof(int)) return [];

        ReadOnlySpan<byte> value = table.Value.Span;
        int floatCount = BitConverter.ToInt32(value);

        if (floatCount <= TableHeaderFloats) return [];
        if (sizeof(int) + (floatCount * sizeof(float)) > value.Length) return [];

        int colourFloats = floatCount - TableHeaderFloats;
        int colourCount = colourFloats / FloatsPerColour;
        if (colourCount <= 0) return [];

        // Where the table's floats begin within the owning export's data.
        int tableStart = distribution.ValueOffset + table.ValueOffset + sizeof(int);
        int firstColour = tableStart + (TableHeaderFloats * sizeof(float));

        var keys = new ParticleColourKey[colourCount];
        for (int i = 0; i < colourCount; i++)
        {
            int at = sizeof(int) + ((TableHeaderFloats + (i * FloatsPerColour)) * sizeof(float));

            float r = BitConverter.ToSingle(value[at..]);
            float g = BitConverter.ToSingle(value[(at + 4)..]);
            float b = BitConverter.ToSingle(value[(at + 8)..]);

            keys[i] = new ParticleColourKey
            {
                Index = i,
                // Effect colours carry no alpha here; that lives in a separate
                // distribution alongside them.
                Colour = new MaterialColour(r, g, b, 1f),
                ValueOffset = firstColour + (i * FloatsPerColour * sizeof(float)),
            };
        }

        return keys;
    }

    /// <summary>
    /// Builds the patched bytes of a module export with new colours applied.
    /// </summary>
    /// <remarks>
    /// Each edit replaces twelve bytes with twelve, so nothing moves. Alpha is
    /// ignored: these tables hold only red, green and blue.
    /// </remarks>
    public static byte[] BuildPatchedExport(
        Package package, int exportIndex, IReadOnlyList<ColourEdit> edits)
    {
        byte[] data = package.GetExportData(exportIndex).ToArray();

        foreach (ColourEdit edit in edits)
        {
            if (edit.ValueOffset < 0 || edit.ValueOffset + (FloatsPerColour * sizeof(float)) > data.Length)
            {
                throw new InvalidOperationException(
                    $"A colour edit at offset {edit.ValueOffset} lies outside the {data.Length}-byte object.");
            }

            BitConverter.GetBytes(edit.Colour.R).CopyTo(data, edit.ValueOffset);
            BitConverter.GetBytes(edit.Colour.G).CopyTo(data, edit.ValueOffset + 4);
            BitConverter.GetBytes(edit.Colour.B).CopyTo(data, edit.ValueOffset + 8);
        }

        return data;
    }
}
