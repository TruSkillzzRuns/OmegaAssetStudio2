namespace OmegaAssetStudio2.Core.Packages;

/// <summary>One entry in a package's name table.</summary>
/// <param name="Name">The stored text. Case is not meaningful — see <see cref="NameTable"/>.</param>
/// <param name="Flags">Engine object flags carried alongside the name.</param>
public readonly record struct NameEntry(string Name, ulong Flags)
{
    public override string ToString() => Name;
}

/// <summary>
/// The name table: every string a package refers to, stored once and referenced
/// by index from the import and export tables.
/// </summary>
/// <remarks>
/// Layout verified against real packages: a length-prefixed string followed by
/// eight bytes of flags, repeated <c>NameCount</c> times, starting at
/// <c>NameOffset</c>. An entry for "class" occupies 18 bytes — 4 length, 6 text
/// including its terminator, 8 flags — and the next entry begins immediately.
/// <para>
/// Names in the packages sampled are stored lower-cased. Nothing may compare
/// them case-sensitively; <see cref="IndexOf"/> and <see cref="Contains"/> fold
/// case for exactly this reason.
/// </para>
/// </remarks>
public sealed class NameTable
{
    private readonly NameEntry[] _entries;
    private readonly Dictionary<string, int> _byName;

    private NameTable(NameEntry[] entries)
    {
        _entries = entries;

        _byName = new Dictionary<string, int>(entries.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Length; i++)
        {
            // Duplicates are not expected, but a package is untrusted input.
            // Keep the first occurrence rather than throwing.
            _byName.TryAdd(entries[i].Name, i);
        }
    }

    public int Count => _entries.Length;

    public NameEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException(
                    $"Name index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    /// <summary>The text at <paramref name="index"/>.</summary>
    public string GetName(int index) => this[index].Name;

    /// <summary>
    /// Resolves a name reference: an index plus an optional numeric suffix. A
    /// non-zero suffix renders as "name_N" with the stored value minus one, which
    /// is how the engine disambiguates repeated names.
    /// </summary>
    public string Resolve(int index, int number) =>
        number > 0 ? $"{GetName(index)}_{number - 1}" : GetName(index);

    /// <summary>Index of <paramref name="name"/>, or -1. Case-insensitive.</summary>
    public int IndexOf(string name) => _byName.TryGetValue(name, out int index) ? index : -1;

    public bool Contains(string name) => _byName.ContainsKey(name);

    public IReadOnlyList<NameEntry> Entries => _entries;

    /// <summary>
    /// Reads the name table from an expanded package body.
    /// </summary>
    /// <param name="body">The expanded body.</param>
    /// <param name="header">The package header, for the offset and count.</param>
    /// <param name="bodyStart">
    /// File offset the body begins at, as reported by the chunk expander.
    /// </param>
    public static NameTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.NameCount < 0)
            throw new InvalidPackageException($"Negative name count {header.NameCount}.");

        int start = header.NameOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException(
                $"Name table offset {header.NameOffset} is outside the expanded body " +
                $"(body starts at {bodyStart}, length {body.Length}).");

        // Smallest possible entry is 4 bytes of length plus 8 of flags, so a
        // count larger than that bound cannot be real. Checked before allocating.
        const int minimumEntrySize = sizeof(int) + sizeof(ulong);
        if ((long)header.NameCount * minimumEntrySize > body.Length - start)
            throw new InvalidPackageException(
                $"Name count {header.NameCount} cannot fit in the {body.Length - start} bytes " +
                $"remaining after the name table offset.");

        var cursor = new PackageCursor(body, start);
        var entries = new NameEntry[header.NameCount];

        for (int i = 0; i < header.NameCount; i++)
        {
            string name = cursor.ReadString($"name {i}");
            ulong flags = cursor.ReadUInt64($"name {i} flags");
            entries[i] = new NameEntry(name, flags);
        }

        return new NameTable(entries);
    }
}
