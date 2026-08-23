namespace OmegaAssetStudio2.Core.Packages;

/// <summary>A replacement for one export's serialised bytes.</summary>
public readonly record struct ExportPatch(int ExportIndex, ReadOnlyMemory<byte> Data);

/// <summary>
/// Writes a modified package back out.
/// </summary>
/// <remarks>
/// Packages ship LZO-compressed, and are written back the same way. Saving one
/// plainly instead was tried, on the grounds that the engine reads either form;
/// the game hung at its loading screen even when the package held nothing but
/// its own untouched content, and every one of the 14,437 packages it installs
/// is compressed. So the body is edited in expanded form and packed again.
/// <para>
/// The expanded layout never changes. The same chunk boundaries are kept, so
/// every offset in the file — the name, import and export tables, each object's
/// own position, and the texture mips that record where they sit — still points
/// exactly where it did.
/// </para>
/// <para>
/// Patches must preserve each export's length. A size change would move every
/// later export and require rewriting the export table; that is a separate and
/// far riskier operation, so it is refused here rather than half-supported.
/// </para>
/// </remarks>
public static class PackageWriter
{
    /// <summary>
    /// Builds the bytes of an uncompressed package with the given patches applied.
    /// </summary>
    /// <exception cref="InvalidOperationException">A patch changes an export's length.</exception>
    public static byte[] Build(Package package, IReadOnlyList<ExportPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(patches);

        PackageHeader header = package.Header;

        // The body begins where the name table begins. Everything before that is
        // header, and it is the only part whose meaning changes when the package
        // stops being compressed.
        int bodyStart = header.IsCompressed ? header.Chunks[0].UncompressedOffset : header.NameOffset;

        byte[] body = package.CopyBody(out int actualBodyStart);
        if (actualBodyStart != bodyStart)
            throw new InvalidOperationException(
                $"Body starts at {actualBodyStart} but the header implies {bodyStart}.");

        foreach (ExportPatch patch in patches)
        {
            ExportEntry export = package.Exports[patch.ExportIndex];

            if (patch.Data.Length != export.SerialSize)
            {
                throw new InvalidOperationException(
                    $"Export {patch.ExportIndex} is {export.SerialSize} bytes but the replacement is " +
                    $"{patch.Data.Length}. Size-changing edits are not supported: they would move every " +
                    "later object in the package.");
            }

            int start = export.SerialOffset - bodyStart;
            if (start < 0 || start + export.SerialSize > body.Length)
                throw new InvalidOperationException(
                    $"Export {patch.ExportIndex} lies outside the package body.");

            patch.Data.Span.CopyTo(body.AsSpan(start, export.SerialSize));
        }

        // Packed back the way it arrived. Every package this game installs is
        // compressed, and a plain one hung the client at its loading screen even
        // when it held the game's own untouched content.
        if (header.IsCompressed) return PackageCompressor.Build(package, body);

        byte[] originalHeader = package.CopyHeaderBytes(bodyStart);
        ClearCompressionFields(originalHeader, header);

        byte[] output = new byte[originalHeader.Length + body.Length];
        originalHeader.CopyTo(output, 0);
        body.CopyTo(output, originalHeader.Length);

        return output;
    }

    /// <summary>
    /// Rewrites the compression fields so the package reads as uncompressed.
    /// </summary>
    /// <remarks>
    /// Only the flags and the chunk count change. The chunk table itself is left
    /// where it is: with a count of zero nothing reads it, and leaving it keeps
    /// the header exactly as long as it was, which is what preserves every offset
    /// in the rest of the file.
    /// </remarks>
    internal static void ClearCompressionFieldsInternal(byte[] headerBytes, PackageHeader header)
        => ClearCompressionFields(headerBytes, header);

    internal static int CompressionFlagsOffsetInternal(PackageHeader header)
        => LocateCompressionFlags(header);

    private static void ClearCompressionFields(byte[] headerBytes, PackageHeader header)
    {
        int compressionFlagsOffset = LocateCompressionFlags(header);

        if (compressionFlagsOffset + (sizeof(int) * 2) > headerBytes.Length)
            throw new InvalidOperationException("The compression fields lie outside the header block.");

        Verify(headerBytes, header, compressionFlagsOffset);

        BitConverter.GetBytes(0).CopyTo(headerBytes, compressionFlagsOffset);
        BitConverter.GetBytes(0).CopyTo(headerBytes, compressionFlagsOffset + sizeof(int));
    }

    /// <summary>
    /// Checks the computed position really is the compression flags.
    /// </summary>
    /// <remarks>
    /// The position is worked out by adding up the lengths of everything before
    /// it, and an error in that sum is silent: it writes over two other fields
    /// and produces a file this application still reads. So the bytes found
    /// there are compared against what the header was read as, and anything
    /// else stops the write.
    /// </remarks>
    internal static void Verify(byte[] headerBytes, PackageHeader header, int offset)
    {
        int flags = BitConverter.ToInt32(headerBytes, offset);
        int chunks = BitConverter.ToInt32(headerBytes, offset + sizeof(int));

        if (flags != (int)header.Compression || chunks != header.Chunks.Count)
        {
            throw new InvalidOperationException(
                $"The compression fields were expected at {offset}, where the file holds flags {flags} " +
                $"and {chunks} chunks rather than {(int)header.Compression} and {header.Chunks.Count}.");
        }
    }

    /// <summary>
    /// Computes where the compression flags sit, by walking the same
    /// variable-length fields the reader walks.
    /// </summary>
    private static int LocateCompressionFlags(PackageHeader header)
    {
        int offset = sizeof(uint)                    // magic
                   + sizeof(short) + sizeof(short)   // file and licensee version
                   + sizeof(int);                    // total header size

        // Folder name is length-prefixed and includes a terminator.
        offset += sizeof(int) + header.FolderName.Length + 1;

        offset += sizeof(uint);          // package flags

        // Eleven: the name, export and import counts and offsets, the depends
        // offset, the identifier offset and its two counts, and the thumbnail
        // table offset. Taken from version 1, which writes this header and is
        // known to produce files the game loads.
        offset += sizeof(int) * 11;
        offset += 16;                    // package guid

        offset += sizeof(int);                              // generation count
        offset += header.GenerationCount * sizeof(int) * 3; // generations

        offset += sizeof(int) * 2;       // engine and cooker version

        return offset;
    }

    /// <summary>
    /// Saves a patched copy of a package, taking a backup and swapping the file
    /// in atomically.
    /// </summary>
    /// <returns>The path of the pristine backup protecting the original.</returns>
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
