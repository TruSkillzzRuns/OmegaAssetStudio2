namespace OmegaAssetStudio2.Core.Packages.Properties;

/// <summary>
/// Reads the tagged-property block at the start of a serialised object.
/// </summary>
/// <remarks>
/// The block is a sequence of tags terminated by one named "none". Each tag is
/// name, type, size, and array index, followed by that many value bytes. Three
/// types carry an extra field between the array index and the value:
/// <list type="bullet">
///   <item>a struct names its struct type,</item>
///   <item>an enum-valued byte names its enum,</item>
///   <item>a bool carries its value in the tag and has a size of zero.</item>
/// </list>
/// Getting any of those wrong desynchronises the stream, and the "none"
/// terminator then never appears — which is exactly what makes it a usable
/// correctness check rather than a silent misparse.
/// </remarks>
public static class PropertyReader
{
    /// <summary>The name that ends a property block.</summary>
    private const string Terminator = "none";

    /// <summary>Guards against a corrupt stream producing an unbounded loop.</summary>
    private const int MaxProperties = 65536;

    /// <summary>
    /// Reads the property block of an export.
    /// </summary>
    /// <param name="data">The export's serialised bytes.</param>
    /// <param name="names">The owning package's name table.</param>
    /// <param name="skipNetIndex">
    /// Whether a leading net index precedes the properties. Objects serialised by
    /// the engine carry one; it was observed as -1 on real exports.
    /// </param>
    public static PropertyBag Read(ReadOnlySpan<byte> data, NameTable names, bool skipNetIndex = true)
    {
        var cursor = new PackageCursor(data);

        if (skipNetIndex)
        {
            if (data.Length < sizeof(int))
                throw new InvalidPackageException("Object data is too short to contain a net index.");
            cursor.Skip(sizeof(int));
        }

        var tags = new List<PropertyTag>();

        while (true)
        {
            if (tags.Count > MaxProperties)
                throw new InvalidPackageException(
                    $"Property block exceeded {MaxProperties} entries; the stream is corrupt.");

            int tagOffset = cursor.Position;

            string name = ReadName(ref cursor, names, "property name");
            if (string.Equals(name, Terminator, StringComparison.OrdinalIgnoreCase))
            {
                // The terminator's own tag is not part of the payload.
                return new PropertyBag(tags.ToArray(), cursor.Position);
            }

            string typeName = ReadName(ref cursor, names, "property type");
            int size = cursor.ReadInt32("property size");
            int arrayIndex = cursor.ReadInt32("property array index");

            if (size < 0)
                throw new InvalidPackageException($"Property '{name}' declares a negative size {size}.");
            if (arrayIndex < 0)
                throw new InvalidPackageException($"Property '{name}' declares array index {arrayIndex}.");

            string innerName = string.Empty;
            byte[] value;

            switch (typeName.ToLowerInvariant())
            {
                case "structproperty":
                    // The struct's type name precedes its bytes.
                    innerName = ReadName(ref cursor, names, $"struct type of '{name}'");
                    value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    break;

                case "byteproperty":
                    // An enum-valued byte names its enum first. A plain byte does
                    // not, and is distinguishable by its size.
                    innerName = ReadName(ref cursor, names, $"enum of '{name}'");
                    if (size == sizeof(int) * 2)
                    {
                        // The value is itself a name: resolve it so callers do
                        // not need the name table to read an enum.
                        int valueIndex = cursor.ReadInt32($"enum value of '{name}'");
                        int valueNumber = cursor.ReadInt32($"enum value number of '{name}'");
                        innerName = names.Resolve(valueIndex, valueNumber);
                        value = BitConverter.GetBytes(valueIndex);
                    }
                    else
                    {
                        value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    }
                    break;

                case "boolproperty":
                    // The value lives in the tag rather than in a value block, so
                    // the declared size is zero — and it is a SINGLE BYTE, not a
                    // four-byte int.
                    //
                    // This was determined from the data, not assumed. On a real
                    // texture the property after "srgb" resolves to "neverstream"
                    // only when the value is one byte wide; reading zero or four
                    // bytes desynchronises the stream and every later property in
                    // the object decodes as garbage. Do not "correct" this to an
                    // int to match a generic engine reference.
                    value = [cursor.ReadBytes(1, $"value of '{name}'")[0]];
                    break;

                case "nameproperty":
                {
                    int valueIndex = cursor.ReadInt32($"name value of '{name}'");
                    int valueNumber = cursor.ReadInt32($"name value number of '{name}'");
                    innerName = names.Resolve(valueIndex, valueNumber);
                    value = BitConverter.GetBytes(valueIndex);
                    break;
                }

                default:
                    value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    break;
            }

            tags.Add(new PropertyTag
            {
                Name = name,
                TypeName = typeName,
                Size = size,
                ArrayIndex = arrayIndex,
                InnerName = innerName,
                ValueOffset = cursor.Position - value.Length,
                Value = value,
                TagOffset = tagOffset,
                TotalSize = cursor.Position - tagOffset,
            });
        }
    }

    /// <summary>
    /// Reads a property block without throwing. Returns null when the block does
    /// not parse, which is how a caller distinguishes "this object has no
    /// readable properties" from a hard failure.
    /// </summary>
    public static PropertyBag? TryRead(ReadOnlySpan<byte> data, NameTable names, bool skipNetIndex = true)
    {
        try
        {
            return Read(data, names, skipNetIndex);
        }
        catch (InvalidPackageException)
        {
            return null;
        }
    }

    private static string ReadName(ref PackageCursor cursor, NameTable names, string what)
    {
        int index = cursor.ReadInt32($"{what} index");
        int number = cursor.ReadInt32($"{what} number");

        if ((uint)index >= (uint)names.Count)
            throw new InvalidPackageException(
                $"{what} refers to name {index}, outside the {names.Count}-entry table.");

        return names.Resolve(index, number);
    }
}
