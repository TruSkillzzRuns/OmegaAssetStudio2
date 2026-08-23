namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// One object this package refers to but does not contain.
/// </summary>
/// <param name="ClassPackage">Package the referenced object's class lives in.</param>
/// <param name="ClassName">Class of the referenced object.</param>
/// <param name="Outer">What contains it — usually another import.</param>
/// <param name="ObjectName">Its name.</param>
public readonly record struct ImportEntry(
    NameReference ClassPackage,
    NameReference ClassName,
    ObjectReference Outer,
    NameReference ObjectName);

/// <summary>
/// The import table: everything the package references from elsewhere.
/// </summary>
/// <remarks>
/// Layout verified against a real package with four imports in 112 bytes —
/// exactly 28 each, in the order below. The check that proves it: import 0's
/// outer resolves to import 2, which is the "Core" package, and import 0 is the
/// class "Package" that genuinely lives in Core. A wrong field order produces
/// references that do not close like that.
/// </remarks>
public sealed class ImportTable
{
    /// <summary>Bytes per entry. Fixed, unlike the export table.</summary>
    public const int EntrySize = (sizeof(int) * 2 * 3) + sizeof(int);

    private readonly ImportEntry[] _entries;

    private ImportTable(ImportEntry[] entries) => _entries = entries;

    public int Count => _entries.Length;

    public ImportEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException(
                    $"Import index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    public IReadOnlyList<ImportEntry> Entries => _entries;

    public static ImportTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.ImportCount < 0)
            throw new InvalidPackageException($"Negative import count {header.ImportCount}.");

        int start = header.ImportOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException(
                $"Import table offset {header.ImportOffset} is outside the expanded body.");

        if ((long)header.ImportCount * EntrySize > body.Length - start)
            throw new InvalidPackageException(
                $"Import count {header.ImportCount} needs {(long)header.ImportCount * EntrySize} bytes " +
                $"but only {body.Length - start} remain.");

        var cursor = new PackageCursor(body, start);
        var entries = new ImportEntry[header.ImportCount];

        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new ImportEntry(
                ClassPackage: ReadName(ref cursor, i, "class package"),
                ClassName: ReadName(ref cursor, i, "class name"),
                Outer: new ObjectReference(cursor.ReadInt32($"import {i} outer")),
                ObjectName: ReadName(ref cursor, i, "object name"));
        }

        return new ImportTable(entries);
    }

    private static NameReference ReadName(ref PackageCursor cursor, int index, string what) =>
        new(cursor.ReadInt32($"import {index} {what} index"),
            cursor.ReadInt32($"import {index} {what} number"));
}
