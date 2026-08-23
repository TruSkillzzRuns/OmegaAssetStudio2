namespace OmegaAssetStudio2.Core.Packages.Properties;

/// <summary>
/// A single tagged property on a serialised object.
/// </summary>
/// <remarks>
/// Layout verified against a real texture export: name, type, size, and array
/// index, then <c>Size</c> bytes of value. The property named "sizex" occupied
/// bytes +004 to +01F — 8 for the name, 8 for the type, 4 for the size, 4 for
/// the array index, 4 for the value — and the next property began exactly at
/// +020.
/// </remarks>
public sealed record PropertyTag
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required int Size { get; init; }
    public required int ArrayIndex { get; init; }

    /// <summary>
    /// For a struct, the struct's type name. For an enum-valued byte, the enum
    /// name. Empty otherwise.
    /// </summary>
    public required string InnerName { get; init; }

    /// <summary>Offset of the value bytes within the object's data.</summary>
    public required int ValueOffset { get; init; }

    /// <summary>The raw value bytes.</summary>
    public required ReadOnlyMemory<byte> Value { get; init; }

    /// <summary>Offset of the whole tag, including its name.</summary>
    public required int TagOffset { get; init; }

    /// <summary>Total bytes the tag and its value occupied.</summary>
    public required int TotalSize { get; init; }

    public override string ToString() =>
        $"{Name}{(ArrayIndex > 0 ? $"[{ArrayIndex}]" : "")} : {TypeName} ({Size} bytes)";
}
