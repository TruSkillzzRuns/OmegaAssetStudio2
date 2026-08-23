namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// A reference to an object, encoded as a signed index.
/// </summary>
/// <remarks>
/// The sign selects the table: zero is null, a positive value is an export at
/// <c>value - 1</c>, and a negative value is an import at <c>-value - 1</c>.
/// Getting this convention wrong resolves an object to a completely unrelated
/// one rather than failing, so it is wrapped here instead of being open-coded
/// wherever a reference is read.
/// </remarks>
public readonly record struct ObjectReference(int Value)
{
    public static readonly ObjectReference Null = new(0);

    public bool IsNull => Value == 0;
    public bool IsExport => Value > 0;
    public bool IsImport => Value < 0;

    /// <summary>Index into the export table. Only valid when <see cref="IsExport"/>.</summary>
    public int ExportIndex => IsExport
        ? Value - 1
        : throw new InvalidOperationException($"Reference {Value} is not an export.");

    /// <summary>Index into the import table. Only valid when <see cref="IsImport"/>.</summary>
    public int ImportIndex => IsImport
        ? -Value - 1
        : throw new InvalidOperationException($"Reference {Value} is not an import.");

    public override string ToString() => Value switch
    {
        0 => "null",
        > 0 => $"export[{Value - 1}]",
        _ => $"import[{-Value - 1}]",
    };
}

/// <summary>
/// A name reference: an index into the name table plus a disambiguating number.
/// </summary>
public readonly record struct NameReference(int Index, int Number)
{
    public string Resolve(NameTable names) => names.Resolve(Index, Number);

    public override string ToString() => Number > 0 ? $"name[{Index}]_{Number - 1}" : $"name[{Index}]";
}
