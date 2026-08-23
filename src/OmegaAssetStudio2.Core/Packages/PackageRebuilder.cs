using System.Buffers.Binary;

namespace OmegaAssetStudio2.Core.Packages;

/// <summary>Why a package could not be rebuilt.</summary>
public sealed class PackageRebuildException : Exception
{
    public PackageRebuildException(string message) : base(message) { }
}

/// <summary>
/// Writes a package back out with objects that have changed size.
/// </summary>
/// <remarks>
/// Nothing is shifted along. A shorter object is written where it already sat,
/// and a longer one goes on the end of the file; every other object keeps the
/// exact position it had. Only the changed object's entry in the table is
/// corrected, and the package is written uncompressed, so no chunk has to be
/// repacked.
/// <para>
/// Laying the objects out afresh — the obvious approach, and the one this began
/// as — produces a file the game loads and then hangs on. Objects store bulk
/// data with its position measured from the start of the whole file: one real
/// character package holds 173 texture mips recorded that way, and replacing a
/// model in it shifted 119 objects along, leaving every one of those mips
/// pointing at bytes that were no longer there. Leaving everything where it is
/// costs some dead space and nothing else.
/// </para>
/// <para>
/// Anything this cannot account for is refused rather than written. A package
/// whose header points at something stored after the objects is left alone,
/// because moving the objects would leave that pointer aimed at the wrong
/// bytes and nothing here could put it right.
/// </para>
/// </remarks>
public static class PackageRebuilder
{
    /// <summary>
    /// Where an object's size and position sit inside its table entry: after
    /// the class, parent, container, name, template and flags.
    /// </summary>
    private const int SizeFieldWithinEntry =
        (sizeof(int) * 3) + (sizeof(int) * 2) + sizeof(int) + sizeof(ulong);

    /// <summary>
    /// Builds the bytes of a package with the given objects replaced, whatever
    /// their new sizes.
    /// </summary>
    public static byte[] Build(Package package, IReadOnlyList<ExportPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(patches);

        PackageHeader header = package.Header;

        byte[] body = package.CopyBody(out int bodyStart);

        // Everything up to the first object is table, and stays put.
        int firstObject = FirstObjectOffset(package);

        Refuse(package, firstObject);

        if (firstObject < bodyStart || firstObject > bodyStart + body.Length)
            throw new PackageRebuildException($"The first object at {firstObject} lies outside the package body.");

        var newOffset = new int[package.Exports.Count];
        var newSize = new int[package.Exports.Count];

        for (int i = 0; i < package.Exports.Count; i++)
        {
            newOffset[i] = package.Exports[i].SerialOffset;
            newSize[i] = package.Exports[i].SerialSize;
        }

        // Every object stays exactly where it was. An object stores things —
        // texture mips above all — with their position measured from the start
        // of the file, and nothing here can find and correct those. Shifting a
        // package's objects along therefore leaves its textures pointing at the
        // wrong bytes, which is a file the game loads and then hangs on.
        var rebuilt = new MemoryStream(body.Length);
        rebuilt.Write(body, 0, body.Length);

        foreach (ExportPatch patch in patches)
        {
            int i = patch.ExportIndex;

            if (i < 0 || i >= package.Exports.Count)
                throw new PackageRebuildException($"There is no object {i} in this package.");

            ExportEntry export = package.Exports[i];
            ReadOnlySpan<byte> data = patch.Data.Span;

            if (data.Length <= export.SerialSize)
            {
                // It fits where it was. The few bytes left over at the end of
                // the old object are never read again: the table now says the
                // object is shorter.
                int start = export.SerialOffset - bodyStart;

                if (start < 0 || start + export.SerialSize > body.Length)
                    throw new PackageRebuildException($"Object {i} lies outside the package body.");

                rebuilt.Position = start;
                rebuilt.Write(data);
                newSize[i] = data.Length;
                continue;
            }

            // Too big for its old place, so it goes on the end, where it
            // disturbs nothing. The bytes it used to occupy are simply left
            // behind — dead space, and the price of not moving anything else.
            Moving(package, i);

            rebuilt.Position = rebuilt.Length;

            newOffset[i] = bodyStart + (int)rebuilt.Position;
            newSize[i] = data.Length;

            rebuilt.Write(data);
        }

        byte[] output = rebuilt.ToArray();

        // Now that every object's place is known, the table is corrected.
        WriteTable(output, package, bodyStart, newOffset, newSize);

        // Packed the way the package arrived. Every package this game installs
        // is compressed, and one written plainly hung the client at its loading
        // screen even when it held the game's own untouched model — so the body
        // goes back compressed rather than plain.
        if (header.IsCompressed) return PackageCompressor.Build(package, output);

        byte[] headerBytes = package.CopyHeaderBytes(bodyStart);

        var whole = new byte[headerBytes.Length + output.Length];
        headerBytes.CopyTo(whole, 0);
        output.CopyTo(whole, headerBytes.Length);

        return whole;
    }

    /// <summary>
    /// Refuses to move an object that records its own position in the file.
    /// </summary>
    /// <remarks>
    /// A block of bulk data stored inside an object — a texture mip, a block of
    /// audio — says where it is with an offset measured from the start of the
    /// whole file, and the loader checks that offset before believing the bytes
    /// that follow. An object holding one cannot be put anywhere else without
    /// every such offset being corrected, and this does not know where they all
    /// are, so it says so rather than writing a file that loads and then hangs.
    /// <para>
    /// The test is the same one the reader makes: a value that equals the
    /// position of the four bytes just past itself.
    /// </para>
    /// </remarks>
    private static void Moving(Package package, int index)
    {
        ReadOnlySpan<byte> data = package.GetExportData(index);
        int at = package.Exports[index].SerialOffset;

        for (int p = 4; p + sizeof(int) <= data.Length; p += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(data[p..]) != at + p + sizeof(int)) continue;

            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(p - sizeof(int))..]);
            if (size <= 0 || p + sizeof(int) + size > data.Length) continue;

            throw new PackageRebuildException(
                $"Object {index} stores {size:N0} bytes that record their own position in the file, so it " +
                "cannot be made larger without breaking them.");
        }
    }

    /// <summary>Where the earliest object is stored.</summary>
    private static int FirstObjectOffset(Package package)
    {
        int first = int.MaxValue;

        foreach (ExportEntry export in package.Exports.Entries)
        {
            // A zero-length object records no meaningful position.
            if (export.SerialSize > 0 && export.SerialOffset < first) first = export.SerialOffset;
        }

        return first == int.MaxValue ? package.Header.ExportOffset : first;
    }

    /// <summary>
    /// Refuses anything whose consequences cannot be worked out.
    /// </summary>
    /// <remarks>
    /// A package is only safe to rebuild when everything except the objects
    /// themselves sits before them. If the header points at something stored
    /// after the objects, moving the objects strands that pointer, and writing
    /// the file anyway would produce one that loads and then misreads.
    /// </remarks>
    private static void Refuse(Package package, int firstObject)
    {
        PackageHeader header = package.Header;

        foreach ((string what, int offset) in new (string, int)[]
                 {
                     ("the name table", header.NameOffset),
                     ("the import table", header.ImportOffset),
                     ("the object table", header.ExportOffset),
                 })
        {
            if (offset > firstObject)
                throw new PackageRebuildException($"This package stores {what} after its objects.");
        }

        foreach ((string what, int offset) in new (string, int)[]
                 {
                     ("a list of what depends on what", header.DependsOffset),
                     ("a table of identifiers", header.ImportExportGuidsOffset),
                     ("a table of thumbnails", header.ThumbnailTableOffset),
                 })
        {
            if (offset > firstObject)
            {
                throw new PackageRebuildException(
                    $"This package keeps {what} after its objects, and moving them would leave it " +
                    "pointing at the wrong bytes.");
            }
        }
    }

    /// <summary>
    /// Corrects every object's recorded size and position.
    /// </summary>
    /// <remarks>
    /// Entries vary in length, so each one's place is found by walking the
    /// table exactly as it was read, rather than by multiplying.
    /// </remarks>
    private static void WriteTable(
        byte[] output, Package package, int bodyStart, int[] offsets, int[] sizes)
    {
        int at = package.Header.ExportOffset - bodyStart;

        for (int i = 0; i < package.Exports.Count; i++)
        {
            ExportEntry export = package.Exports[i];

            int field = at + SizeFieldWithinEntry;

            if (field + (sizeof(int) * 2) > output.Length)
                throw new PackageRebuildException($"Object {i}'s table entry lies outside the package.");

            // Checked rather than trusted: if these do not read back as what
            // was there before, this is writing to the wrong place, and the
            // next two lines would corrupt the file.
            int wasSize = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(field, sizeof(int)));
            int wasOffset = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(field + sizeof(int), sizeof(int)));

            if (wasSize != export.SerialSize || wasOffset != export.SerialOffset)
            {
                throw new PackageRebuildException(
                    $"Object {i}'s entry does not hold the size and position it was read with " +
                    $"({wasSize} at {wasOffset}, expected {export.SerialSize} at {export.SerialOffset}).");
            }

            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field, sizeof(int)), sizes[i]);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field + sizeof(int), sizeof(int)), offsets[i]);

            at += export.EntrySize;
        }
    }

    /// <summary>
    /// Saves a rebuilt package, taking a backup and swapping it in atomically.
    /// </summary>
    public static async Task<string> SaveAsync(
        Package package,
        IReadOnlyList<ExportPatch> patches,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = Build(package, patches);

        return await Workspace.SafeFileWriter.WriteAsync(package.Path, bytes, cancellationToken)
                                             .ConfigureAwait(false);
    }
}
