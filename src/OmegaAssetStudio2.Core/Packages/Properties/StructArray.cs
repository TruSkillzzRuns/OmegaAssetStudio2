namespace OmegaAssetStudio2.Core.Packages.Properties;

/// <summary>One element of an array of structures, with its own properties.</summary>
public sealed record StructArrayElement(PropertyBag Properties, int Offset);

/// <summary>
/// Reads an array property whose elements are structures.
/// </summary>
/// <remarks>
/// The value of such an array is an element count followed by that many complete
/// property blocks — the same shape as an object's own properties, each ending
/// with a name of "none". They therefore parse with the ordinary property reader,
/// with the one difference that an element has no leading net index.
/// <para>
/// Offsets are reported relative to the containing export's data, not to the
/// array, so a caller can patch a value in place without recomputing where the
/// array began.
/// </para>
/// </remarks>
public static class StructArray
{
    /// <summary>Guards against a corrupt count producing an unbounded read.</summary>
    private const int MaxElements = 4096;

    /// <summary>
    /// Reads the elements of a struct array.
    /// </summary>
    /// <param name="tag">The array property.</param>
    /// <param name="names">The owning package's name table.</param>
    /// <returns>One entry per element, or an empty list when it does not parse.</returns>
    public static IReadOnlyList<StructArrayElement> ReadElements(PropertyTag tag, NameTable names)
    {
        ArgumentNullException.ThrowIfNull(tag);

        ReadOnlySpan<byte> value = tag.Value.Span;
        if (value.Length < sizeof(int)) return [];

        int count = BitConverter.ToInt32(value);
        if (count <= 0 || count > MaxElements) return [];

        var elements = new List<StructArrayElement>(count);
        int position = sizeof(int);

        for (int i = 0; i < count; i++)
        {
            if (position >= value.Length) break;

            PropertyBag? element = PropertyReader.TryRead(value[position..], names, skipNetIndex: false);
            if (element is null) break;

            // Offsets inside the element are relative to where the element began;
            // shift them so callers get positions within the export's data.
            elements.Add(new StructArrayElement(element, tag.ValueOffset + position));

            if (element.PayloadOffset <= 0) break;
            position += element.PayloadOffset;
        }

        return elements;
    }
}
