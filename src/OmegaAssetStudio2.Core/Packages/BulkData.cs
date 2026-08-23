namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// A block of bulk data — pixel data, vertex data, audio — described by a small
/// header that says where the bytes actually live.
/// </summary>
/// <remarks>
/// Two storage modes were observed in real content, and they are distinguished by
/// the flags:
/// <list type="bullet">
///   <item>
///     <c>0x00000000</c> — the payload is written inline directly after this
///     header, and <c>SizeOnDisk</c> is its real length.
///   </item>
///   <item>
///     <c>0x00000011</c> — the payload lives in a shared texture cache file.
///     <c>SizeOnDisk</c> and <c>OffsetInFile</c> are both <b>-1</b>, and the byte
///     count is carried in <c>ElementCount</c> instead.
///   </item>
/// </list>
/// Treating -1 as a corrupt size is wrong; it is how the format says "not here".
/// The element count was confirmed against the format arithmetic: a 700x916 DXT5
/// mip reports 641,200, which is exactly 175x229 blocks at 16 bytes.
/// </remarks>
public readonly record struct BulkData(
    uint Flags,
    int ElementCount,
    int SizeOnDisk,
    int OffsetInFile,
    int InlineDataOffset)
{
    /// <summary>Bytes the header itself occupies.</summary>
    public const int HeaderSize = sizeof(uint) + (sizeof(int) * 3);

    /// <summary>Set when the payload is stored outside this package.</summary>
    public const uint StoredExternallyFlag = 0x00000010;

    /// <summary>
    /// Set when the payload lives in a separate file rather than after the
    /// header. Every external mip observed in real content carries it — 0x11
    /// and 0x21 both do — and every inline one has it clear.
    /// </summary>
    public const uint SeparateFileFlag = 0x00000001;

    /// <summary>True when the payload sits directly after the header.</summary>
    public bool IsInline => InlineDataOffset >= 0;

    /// <summary>True when the payload is in a separate cache file.</summary>
    public bool IsStoredExternally => (Flags & StoredExternallyFlag) != 0 || OffsetInFile < 0;

    /// <summary>
    /// Length of the payload, wherever it lives. Inline blocks report it as the
    /// on-disk size; external blocks report it as the element count.
    /// </summary>
    public int ByteCount => SizeOnDisk > 0 ? SizeOnDisk : Math.Max(0, ElementCount);

    public bool IsEmpty => ByteCount == 0;

    /// <summary>
    /// Reads a bulk-data header, advancing past any inline payload.
    /// </summary>
    /// <param name="cursor">Positioned at the header.</param>
    /// <param name="absoluteBase">
    /// File offset corresponding to position zero of the cursor's buffer, so the
    /// recorded offset can be compared against where we actually are. Comparing
    /// offsets is exact and does not depend on interpreting a flag bit.
    /// </param>
    /// <param name="what">Used in error messages.</param>
    public static BulkData Read(ref PackageCursor cursor, int absoluteBase, string what)
    {
        uint flags = cursor.ReadUInt32($"{what} bulk flags");
        int elementCount = cursor.ReadInt32($"{what} element count");
        int sizeOnDisk = cursor.ReadInt32($"{what} size on disk");
        int offsetInFile = cursor.ReadInt32($"{what} offset in file");

        if (elementCount < 0)
            throw new InvalidPackageException($"{what} declares {elementCount} elements.");

        // -1 is legitimate and means "stored elsewhere"; anything below that is
        // not something the format produces.
        if (sizeOnDisk < -1)
            throw new InvalidPackageException($"{what} declares a size of {sizeOnDisk}.");

        int positionAfterHeader = cursor.Position;
        bool fits = sizeOnDisk > 0 && positionAfterHeader + sizeOnDisk <= cursor.Length;

        // The recorded offset says where the block was when the file was last
        // written; the payload itself is always the bytes straight after the
        // header. Those agree in an untouched package, so the offset is used
        // first because it is exact.
        //
        // They stop agreeing once a package has been rewritten at a different
        // size — decompressing one moves every object along, and the offsets
        // inside them still describe the old layout. Insisting on the match
        // there made every texture in an installed costume unreadable and left
        // the model plain grey, which is worse than trusting the flag: a block
        // whose payload is in a separate file says so, and one that does not
        // has its bytes here.
        bool inline = fits
                   && (offsetInFile == absoluteBase + positionAfterHeader
                       || (flags & SeparateFileFlag) == 0);

        if (inline) cursor.Skip(sizeOnDisk);

        return new BulkData(
            flags,
            elementCount,
            sizeOnDisk,
            offsetInFile,
            inline ? positionAfterHeader : -1);
    }
}
