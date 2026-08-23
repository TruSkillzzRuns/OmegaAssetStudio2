namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// One object stored inside this package.
/// </summary>
public sealed record ExportEntry
{
    public required ObjectReference Class { get; init; }
    public required ObjectReference Super { get; init; }
    public required ObjectReference Outer { get; init; }
    public required NameReference ObjectName { get; init; }
    public required ObjectReference Archetype { get; init; }
    public required ulong ObjectFlags { get; init; }

    /// <summary>Length of this object's serialised data, in bytes.</summary>
    public required int SerialSize { get; init; }

    /// <summary>Where that data begins, as a file offset.</summary>
    public required int SerialOffset { get; init; }

    public required uint ExportFlags { get; init; }
    public required IReadOnlyList<int> NetObjects { get; init; }
    public required Guid PackageGuid { get; init; }
    public required uint PackageFlags { get; init; }

    /// <summary>Bytes this entry occupied. Varies with the net-object count.</summary>
    public required int EntrySize { get; init; }
}

/// <summary>
/// The export table: every object the package contains.
/// </summary>
/// <remarks>
/// Entries are <em>variable length</em>. After the net-object count comes that
/// many integers, so a fixed stride silently misreads every entry after the
/// first one that has any.
/// <para>
/// Verified on a real package: two exports occupying 140 bytes total, which only
/// closes if the first is 68 bytes with no net objects and the second is 72 with
/// one. The corroborating check is that export 0 declares
/// <c>SerialSize 12, SerialOffset 581</c> and export 1 begins at offset 593 —
/// exactly where the previous object ends.
/// </para>
/// </remarks>
public sealed class ExportTable
{
    private readonly ExportEntry[] _entries;

    private ExportTable(ExportEntry[] entries) => _entries = entries;

    public int Count => _entries.Length;

    public ExportEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException(
                    $"Export index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    public IReadOnlyList<ExportEntry> Entries => _entries;

    public static ExportTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.ExportCount < 0)
            throw new InvalidPackageException($"Negative export count {header.ExportCount}.");

        int start = header.ExportOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException(
                $"Export table offset {header.ExportOffset} is outside the expanded body.");

        // Smallest possible entry, used only to reject an impossible count before
        // allocating. Real entries are this size or larger.
        const int minimumEntrySize = 68;
        if ((long)header.ExportCount * minimumEntrySize > body.Length - start)
            throw new InvalidPackageException(
                $"Export count {header.ExportCount} cannot fit in the {body.Length - start} bytes " +
                $"remaining after the export table offset.");

        var cursor = new PackageCursor(body, start);
        var entries = new ExportEntry[header.ExportCount];

        for (int i = 0; i < entries.Length; i++)
        {
            int entryStart = cursor.Position;

            var classRef = new ObjectReference(cursor.ReadInt32($"export {i} class"));
            var superRef = new ObjectReference(cursor.ReadInt32($"export {i} super"));
            var outerRef = new ObjectReference(cursor.ReadInt32($"export {i} outer"));

            var objectName = new NameReference(
                cursor.ReadInt32($"export {i} name index"),
                cursor.ReadInt32($"export {i} name number"));

            var archetype = new ObjectReference(cursor.ReadInt32($"export {i} archetype"));
            ulong objectFlags = cursor.ReadUInt64($"export {i} object flags");

            int serialSize = cursor.ReadInt32($"export {i} serial size");
            int serialOffset = cursor.ReadInt32($"export {i} serial offset");
            uint exportFlags = cursor.ReadUInt32($"export {i} export flags");

            if (serialSize < 0)
                throw new InvalidPackageException($"Export {i} declares a negative serial size {serialSize}.");
            if (serialOffset < 0)
                throw new InvalidPackageException($"Export {i} declares a negative serial offset {serialOffset}.");

            int netObjectCount = cursor.ReadInt32($"export {i} net object count");
            if (netObjectCount < 0)
                throw new InvalidPackageException($"Export {i} declares {netObjectCount} net objects.");
            if ((long)netObjectCount * sizeof(int) > cursor.Remaining)
                throw new InvalidPackageException(
                    $"Export {i} declares {netObjectCount} net objects, which does not fit in the " +
                    $"remaining {cursor.Remaining} bytes.");

            int[] netObjects = new int[netObjectCount];
            for (int n = 0; n < netObjectCount; n++)
                netObjects[n] = cursor.ReadInt32($"export {i} net object {n}");

            Guid packageGuid = cursor.ReadGuid($"export {i} package guid");
            uint packageFlags = cursor.ReadUInt32($"export {i} package flags");

            entries[i] = new ExportEntry
            {
                Class = classRef,
                Super = superRef,
                Outer = outerRef,
                ObjectName = objectName,
                Archetype = archetype,
                ObjectFlags = objectFlags,
                SerialSize = serialSize,
                SerialOffset = serialOffset,
                ExportFlags = exportFlags,
                NetObjects = netObjects,
                PackageGuid = packageGuid,
                PackageFlags = packageFlags,
                EntrySize = cursor.Position - entryStart,
            };
        }

        return new ExportTable(entries);
    }
}
