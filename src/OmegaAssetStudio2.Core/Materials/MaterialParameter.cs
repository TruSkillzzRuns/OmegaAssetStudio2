using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>A colour parameter on a material instance.</summary>
public sealed record ColourParameter
{
    /// <summary>Name the material author gave it, such as "color rgb opacity a".</summary>
    public required string Name { get; init; }

    public required MaterialColour Colour { get; init; }

    /// <summary>
    /// Offset of the four floats within the owning export's data, so the value can
    /// be rewritten without moving anything.
    /// </summary>
    public required int ValueOffset { get; init; }

    public override string ToString() => $"{Name} = {Colour}";
}

/// <summary>A single numeric parameter on a material instance.</summary>
public sealed record ScalarParameter
{
    public required string Name { get; init; }
    public required float Value { get; init; }
    public required int ValueOffset { get; init; }

    public override string ToString() => $"{Name} = {Value:0.###}";
}

/// <summary>Every editable parameter found on one material instance.</summary>
public sealed record MaterialInstance
{
    public required string PackagePath { get; init; }
    public required int ExportIndex { get; init; }
    public required string Name { get; init; }
    public required string ObjectPath { get; init; }
    public required IReadOnlyList<ColourParameter> Colours { get; init; }
    public required IReadOnlyList<ScalarParameter> Scalars { get; init; }

    public bool HasEditableParameters => Colours.Count > 0 || Scalars.Count > 0;

    public override string ToString() =>
        $"{Name} ({Colours.Count} colours, {Scalars.Count} values)";
}

/// <summary>
/// Reads the parameters a material instance overrides.
/// </summary>
/// <remarks>
/// A material instance stores its overrides as arrays of small structures, each
/// holding a parameter name and a value. Colour values are four floats; numeric
/// values are one. Both are read together with the offset of their bytes, which
/// is what allows an edit to be written back without changing any size.
/// </remarks>
public static class MaterialParameterReader
{
    /// <summary>Class every material instance carries.</summary>
    public const string MaterialInstanceClass = "materialinstanceconstant";

    private const string ColourArrayProperty = "VectorParameterValues";
    private const string ScalarArrayProperty = "ScalarParameterValues";
    private const string ParameterNameProperty = "ParameterName";
    private const string ParameterValueProperty = "ParameterValue";

    /// <summary>
    /// Reads one material instance export. Returns null when it has no readable
    /// properties.
    /// </summary>
    public static MaterialInstance? TryRead(Package package, int exportIndex)
    {
        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        return new MaterialInstance
        {
            PackagePath = package.Path,
            ExportIndex = exportIndex,
            Name = package.GetExportName(exportIndex),
            ObjectPath = package.GetExportPath(exportIndex),
            Colours = ReadColours(properties, package.Names),
            Scalars = ReadScalars(properties, package.Names),
        };
    }

    private static IReadOnlyList<ColourParameter> ReadColours(PropertyBag properties, NameTable names)
    {
        PropertyTag? array = properties.Find(ColourArrayProperty);
        if (array is null) return [];

        var found = new List<ColourParameter>();

        foreach (StructArrayElement element in StructArray.ReadElements(array, names))
        {
            string name = element.Properties.GetName(ParameterNameProperty);
            PropertyTag? value = element.Properties.Find(ParameterValueProperty);

            if (name.Length == 0 || value is null || value.Value.Length < sizeof(float) * 4)
                continue;

            ReadOnlySpan<byte> bytes = value.Value.Span;

            found.Add(new ColourParameter
            {
                Name = name,
                Colour = new MaterialColour(
                    BitConverter.ToSingle(bytes),
                    BitConverter.ToSingle(bytes[4..]),
                    BitConverter.ToSingle(bytes[8..]),
                    BitConverter.ToSingle(bytes[12..])),

                // The element's own offsets are relative to the element; add the
                // element's position to get a position within the export.
                ValueOffset = element.Offset + value.ValueOffset,
            });
        }

        return found;
    }

    private static IReadOnlyList<ScalarParameter> ReadScalars(PropertyBag properties, NameTable names)
    {
        PropertyTag? array = properties.Find(ScalarArrayProperty);
        if (array is null) return [];

        var found = new List<ScalarParameter>();

        foreach (StructArrayElement element in StructArray.ReadElements(array, names))
        {
            string name = element.Properties.GetName(ParameterNameProperty);
            PropertyTag? value = element.Properties.Find(ParameterValueProperty);

            if (name.Length == 0 || value is null || value.Value.Length < sizeof(float))
                continue;

            found.Add(new ScalarParameter
            {
                Name = name,
                Value = BitConverter.ToSingle(value.Value.Span),
                ValueOffset = element.Offset + value.ValueOffset,
            });
        }

        return found;
    }
}
