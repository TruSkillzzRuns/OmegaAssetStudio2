namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// A cooked package, opened for reading: header, tables, and expanded body.
/// </summary>
/// <remarks>
/// Read-only. Nothing here writes; commits go through the workspace write path
/// so that a backup and an atomic swap always happen.
/// </remarks>
public sealed class Package
{
    private readonly byte[] _body;
    private readonly int _bodyStart;
    private readonly byte[] _file;

    private Package(
        string path, PackageHeader header, byte[] body, int bodyStart, byte[] file,
        NameTable names, ImportTable imports, ExportTable exports)
    {
        Path = path;
        Header = header;
        _body = body;
        _bodyStart = bodyStart;
        _file = file;
        Names = names;
        Imports = imports;
        Exports = exports;
    }

    /// <summary>Where the body begins, as a file offset.</summary>
    public int BodyStart => _bodyStart;

    /// <summary>A private copy of the expanded body, safe for the caller to modify.</summary>
    public byte[] CopyBody(out int bodyStart)
    {
        bodyStart = _bodyStart;
        return _body.ToArray();
    }

    /// <summary>The first <paramref name="count"/> bytes of the file as it sits on disk.</summary>
    public byte[] CopyFilePrefix(int count)
        => _file.AsSpan(0, Math.Clamp(count, 0, _file.Length)).ToArray();

    /// <summary>
    /// A private copy of the bytes before the body — the header block as it sits
    /// in the file.
    /// </summary>
    public byte[] CopyHeaderBytes(int bodyStart)
    {
        if (bodyStart < 0 || bodyStart > _file.Length)
            throw new InvalidPackageException(
                $"Header block of {bodyStart} bytes does not fit the {_file.Length}-byte file.");

        return _file.AsSpan(0, bodyStart).ToArray();
    }

    public string Path { get; }
    public PackageHeader Header { get; }
    public NameTable Names { get; }
    public ImportTable Imports { get; }
    public ExportTable Exports { get; }

    public PackageFormat Format => Header.Format;

    /// <summary>Opens a package from disk and reads its tables.</summary>
    public static Package Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path), path);
    }

    /// <summary>Reads a package already held in memory.</summary>
    public static Package Read(ReadOnlySpan<byte> file, string path = "")
    {
        PackageHeader header = PackageHeader.Read(file);
        byte[] body = ChunkExpander.ExpandBody(header, file, out int bodyStart);

        return new Package(
            path,
            header,
            body,
            bodyStart,
            file.ToArray(),
            NameTable.Read(body, header, bodyStart),
            ImportTable.Read(body, header, bodyStart),
            ExportTable.Read(body, header, bodyStart));
    }

    /// <summary>The serialised bytes of an export.</summary>
    public ReadOnlySpan<byte> GetExportData(int exportIndex)
    {
        ExportEntry export = Exports[exportIndex];

        int start = export.SerialOffset - _bodyStart;
        if (start < 0 || start + export.SerialSize > _body.Length)
            throw new InvalidPackageException(
                $"Export {exportIndex} claims {export.SerialSize} bytes at offset {export.SerialOffset}, " +
                $"which is outside the expanded body.");

        return _body.AsSpan(start, export.SerialSize);
    }

    /// <summary>The class name of an export, resolved through whichever table it points at.</summary>
    public string GetExportClassName(int exportIndex) => ResolveName(Exports[exportIndex].Class);

    /// <summary>The name of an export, without its containing path.</summary>
    public string GetExportName(int exportIndex) =>
        Exports[exportIndex].ObjectName.Resolve(Names);

    /// <summary>
    /// The full dotted path of an export, walking outward through its containers.
    /// </summary>
    /// <remarks>
    /// Guards against a reference cycle. A malformed package can point an outer
    /// back at its own descendant, and an unguarded walk would loop forever.
    /// </remarks>
    public string GetExportPath(int exportIndex)
    {
        var segments = new List<string>(4);
        var seen = new HashSet<int>();

        ObjectReference current = new(exportIndex + 1);
        while (!current.IsNull)
        {
            if (current.IsExport)
            {
                int index = current.ExportIndex;
                if (!seen.Add(index))
                    throw new InvalidPackageException(
                        $"Export {exportIndex} has a cyclic outer chain at export {index}.");

                ExportEntry entry = Exports[index];
                segments.Add(entry.ObjectName.Resolve(Names));
                current = entry.Outer;
            }
            else
            {
                ImportEntry entry = Imports[current.ImportIndex];
                segments.Add(entry.ObjectName.Resolve(Names));
                current = entry.Outer;
            }
        }

        segments.Reverse();
        return string.Join('.', segments);
    }

    /// <summary>
    /// The full dotted path of an import, walking outward through its containers.
    /// </summary>
    /// <remarks>
    /// The outermost segment names the package the object really lives in, which
    /// is how an object referenced from elsewhere gets found on disk.
    /// </remarks>
    public string GetImportPath(int importIndex)
    {
        var segments = new List<string>(4);
        var seen = new HashSet<int>();

        ObjectReference current = new(-(importIndex + 1));
        while (!current.IsNull)
        {
            if (current.IsImport)
            {
                int index = current.ImportIndex;
                if (!seen.Add(index))
                    throw new InvalidPackageException(
                        $"Import {importIndex} has a cyclic outer chain at import {index}.");

                ImportEntry entry = Imports[index];
                segments.Add(entry.ObjectName.Resolve(Names));
                current = entry.Outer;
            }
            else
            {
                segments.Add(Exports[current.ExportIndex].ObjectName.Resolve(Names));
                current = Exports[current.ExportIndex].Outer;
            }
        }

        segments.Reverse();
        return string.Join('.', segments);
    }

    /// <summary>Resolves any object reference to a readable name.</summary>
    public string ResolveName(ObjectReference reference)
    {
        if (reference.IsNull) return string.Empty;

        return reference.IsExport
            ? Exports[reference.ExportIndex].ObjectName.Resolve(Names)
            : Imports[reference.ImportIndex].ObjectName.Resolve(Names);
    }

    /// <summary>
    /// Reads the tagged properties of an export. Returns null when the block does
    /// not parse — some engine objects are pure binary with no property block.
    /// </summary>
    public Properties.PropertyBag? TryReadProperties(int exportIndex)
    {
        ReadOnlySpan<byte> data = GetExportData(exportIndex);

        Properties.PropertyBag? bag = Properties.PropertyReader.TryRead(data, Names);
        if (bag is not null && bag.Tags.Count > 0) return bag;

        // A component is written with more in front of its properties than an
        // ordinary object: the net index, then the name of the template it was
        // made from, then the class that owns that template - sixteen bytes
        // rather than four. Started four bytes in, the reader lands in the
        // middle of that and finds nothing, so the object looks as though it
        // has no properties at all.
        //
        // 27,910 exports in the game's own character packages are in this
        // shape, among them every entityfxparticle, every skeletalmeshcomponent
        // and every meshattachment - all the things that say what is hung on a
        // costume and where. None of them has ever been readable.
        //
        // The preamble is not assumed: an export that has one writes its own
        // name four bytes in, which is checked here for that export before its
        // properties are read from the further offset. Of the exports that
        // would not read, 84.7% pass that check.
        if (!HasComponentPreamble(data, exportIndex)) return bag;

        return Properties.PropertyReader.TryRead(data[ComponentPreamble..], Names, skipNetIndex: false)
            ?? bag;
    }

    /// <summary>The net index, the template's name, and the class that owns it.</summary>
    private const int ComponentPreamble = 16;

    /// <summary>
    /// How far into an export's bytes its properties begin.
    /// </summary>
    /// <remarks>
    /// A tag records where it sits within the run of properties it was read
    /// from, not within the export. For most exports those are the same place;
    /// for a component they are sixteen bytes apart, because of the preamble
    /// above. Anything meaning to write a tag's own bytes back has to know
    /// which, or it works on a window sixteen bytes adrift - and in a table
    /// whose tags happen to be all of one size, that still reads back cleanly
    /// while quietly having taken out the wrong one.
    /// </remarks>
    public int PropertiesBegin(int exportIndex)
    {
        ReadOnlySpan<byte> data = GetExportData(exportIndex);

        Properties.PropertyBag? bag = Properties.PropertyReader.TryRead(data, Names);

        if (bag is not null && bag.Tags.Count > 0) return 0;

        return HasComponentPreamble(data, exportIndex) ? ComponentPreamble : 0;
    }

    /// <summary>
    /// Whether this export writes its own name where a component writes the
    /// name of its template.
    /// </summary>
    private bool HasComponentPreamble(ReadOnlySpan<byte> data, int exportIndex)
    {
        if (data.Length < ComponentPreamble) return false;

        int index = BitConverter.ToInt32(data.Slice(4, 4));
        int number = BitConverter.ToInt32(data.Slice(8, 4));

        if ((uint)index >= (uint)Names.Count) return false;

        return Names.Resolve(index, number)
                    .Equals(GetExportName(exportIndex), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every export whose class matches <paramref name="className"/>.</summary>
    /// <remarks>Case-insensitive: the name table stores names lower-cased.</remarks>
    public IEnumerable<int> FindExportsOfClass(string className)
    {
        for (int i = 0; i < Exports.Count; i++)
        {
            if (string.Equals(GetExportClassName(i), className, StringComparison.OrdinalIgnoreCase))
                yield return i;
        }
    }
}
