using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Compression;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio;

/// <summary>
/// Shared UPK repack and decompression utility used by all export-replacement injectors.
/// </summary>
internal static class UpkRepacker
{
    public readonly record struct BulkDataPatch(int OffsetFieldPosition, int DataStartPosition);
    public sealed record ExportBuffer(byte[] Data, IReadOnlyList<BulkDataPatch> Patches);

    // Single-export convenience overloads ----------------------------------------

    /// <summary>
    /// Replaces one export in an uncompressed UPK, repacks, and returns the result.
    /// </summary>
    public static byte[] Repack(
        byte[] originalBytes,
        UnrealHeader header,
        int targetExportIndex,
        byte[] newExportData,
        IReadOnlyList<BulkDataPatch> patches = null)
    {
        List<ExportBuffer> buffers = BuildBufferList(header, targetExportIndex, newExportData, patches);
        return RepackCore(originalBytes, header, buffers);
    }

    /// <summary>
    /// Decompresses a fully-compressed UPK, replaces one export, and repacks as uncompressed.
    /// </summary>
    public static byte[] RepackCompressed(
        byte[] originalBytes,
        UnrealHeader header,
        int targetExportIndex,
        byte[] newExportData,
        IReadOnlyList<BulkDataPatch> patches = null)
    {
        List<ExportBuffer> buffers = BuildBufferList(header, targetExportIndex, newExportData, patches);
        return RepackCompressedCore(originalBytes, header, buffers);
    }

    // Multi-export overloads (used by mesh injector for bulk-data offset patches) --

    /// <summary>
    /// Replaces exports (with optional bulk-data offset patches) in an uncompressed UPK.
    /// </summary>
    public static byte[] Repack(
        byte[] originalBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> exportBuffers)
        => RepackCore(originalBytes, header, exportBuffers);

    /// <summary>
    /// Decompresses a fully-compressed UPK, replaces exports, and repacks as uncompressed.
    /// </summary>
    public static byte[] RepackCompressed(
        byte[] originalBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> exportBuffers)
        => RepackCompressedCore(originalBytes, header, exportBuffers);

    // -------------------------------------------------------------------------

    private static List<ExportBuffer> BuildBufferList(UnrealHeader header, int targetIndex, byte[] newData, IReadOnlyList<BulkDataPatch> patches = null)
    {
        List<ExportBuffer> buffers = header.ExportTable
            .Select(static e => new ExportBuffer(e.UnrealObjectReader.GetBytes(), []))
            .ToList();
        buffers[targetIndex] = new ExportBuffer(newData, patches ?? []);
        return buffers;
    }

    private static byte[] RepackCore(byte[] sourceBytes, UnrealHeader header, IReadOnlyList<ExportBuffer> exportBuffers)
        => RepackCoreWithAddedNames(sourceBytes, header, exportBuffers, Array.Empty<string>(), out _);

    /// <summary>
    /// Like RepackCore but appends new NameTable entries first. Returns the
    /// assigned NameTable index for each appended name via addedIndices. New
    /// entries always go at the END of the NameTable so existing FName indices
    /// stay valid.
    /// </summary>
    public static byte[] RepackWithAddedNames(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> exportBuffers,
        IReadOnlyList<string> addedNames,
        out int[] addedIndices)
        => RepackCoreWithAddedNames(sourceBytes, header, exportBuffers, addedNames, out addedIndices);

    // Specification for one new export to add. NameTable indices reference
    // the post-merge name table (i.e. you can point at an entry inside
    // `addedNames` by using `header.NameTable.Count + indexWithinAdded`).
    // Specification for one new import-table entry. PackageName / ClassName
    // / ObjectName indices reference the POST-merge NameTable (so callers
    // can point at names added in the same RepackWithAddedImports call).
    public sealed record NewImportSpec(
        int PackageNameTableIndex,
        int PackageNameNumeric,
        int ClassNameTableIndex,
        int ClassNameNumeric,
        int OuterRef,                       // UE3 ref encoding
        int ObjectNameTableIndex,
        int ObjectNameNumeric);

    /// <summary>
    /// Repack a UPK while appending new import-table entries (and optionally
    /// new NameTable entries). New imports are placed at the end of the
    /// existing import table. Each entry is a fixed 28 bytes:
    ///   FName PackageName (8) + FName ClassName (8) + int32 OuterRef + FName ObjectName (8).
    /// </summary>
    public static byte[] RepackWithAddedImports(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> existingExportBuffers,
        IReadOnlyList<NewImportSpec> addedImports,
        IReadOnlyList<string> addedNames,
        out int[] addedNameIndices,
        out int[] addedImportIndices)
    {
        sourceBytes = header.CompressedChunks.Count > 0
            ? PrepareDecompressedHeaderBytes(sourceBytes, header)
            : sourceBytes;

        int oldHeaderSize = header.Size;
        int addedNameCount = addedNames?.Count ?? 0;
        int addedImportCount = addedImports?.Count ?? 0;

        byte[] addedNamesBlock = BuildNameBlock(addedNames, header.NameTable.Count, out addedNameIndices);
        int addedNameBytes = addedNamesBlock.Length;

        byte[] addedImportsBlock = BuildImportEntriesBlock(addedImports, out addedImportIndices, header.ImportTable.Count);
        int addedImportBytes = addedImportsBlock.Length;

        int totalGrowth = addedNameBytes + addedImportBytes;
        int newHeaderSize = oldHeaderSize + totalGrowth;

        int nameTableEnd = header.ImportTableOffset > 0 ? header.ImportTableOffset : oldHeaderSize;
        int importTableEnd = ComputeImportTableEnd(header, oldHeaderSize);

        long existingBodySize = existingExportBuffers.Sum(b => (long)b.Data.Length);
        byte[] repacked = new byte[newHeaderSize + existingBodySize];

        int dst = 0;
        Buffer.BlockCopy(sourceBytes, 0, repacked, dst, nameTableEnd); dst += nameTableEnd;
        Buffer.BlockCopy(addedNamesBlock, 0, repacked, dst, addedNameBytes); dst += addedNameBytes;
        Buffer.BlockCopy(sourceBytes, nameTableEnd, repacked, dst, importTableEnd - nameTableEnd);
        dst += importTableEnd - nameTableEnd;
        Buffer.BlockCopy(addedImportsBlock, 0, repacked, dst, addedImportBytes);
        dst += addedImportBytes;
        int tail = oldHeaderSize - importTableEnd;
        if (tail > 0)
        {
            Buffer.BlockCopy(sourceBytes, importTableEnd, repacked, dst, tail);
            dst += tail;
        }

        // Patch header counts/offsets.
        HeaderTableOffsets t = LocateHeaderTableOffsets(repacked);
        if (addedNameCount > 0)
            WriteInt32(repacked, t.NameCountFieldOffset, header.NameTable.Count + addedNameCount);
        if (addedImportCount > 0)
            // ImportCount is at NameOffset+8 → t.ImportOffsetFieldOffset - 4
            WriteInt32(repacked, t.ImportOffsetFieldOffset - 4, header.ImportTable.Count + addedImportCount);
        ShiftIfPositive(repacked, t.ImportOffsetFieldOffset, addedNameBytes);
        ShiftIfPositive(repacked, t.ExportOffsetFieldOffset, addedNameBytes + addedImportBytes);
        ShiftIfPositive(repacked, t.DependsOffsetFieldOffset, addedNameBytes + addedImportBytes);
        ShiftIfPositive(repacked, t.ImportExportGuidsOffsetFieldOffset, totalGrowth);
        ShiftIfPositive(repacked, t.ThumbnailOffsetFieldOffset, totalGrowth);

        // Write exports as before. Their entries shifted by addedNameBytes
        // + addedImportBytes (both came before the export table).
        List<int> entryOffsets = LocateExportTableOffsets(repacked, header, headerOverrideStart: addedNameBytes + addedImportBytes);
        int cursor = newHeaderSize;
        for (int i = 0; i < existingExportBuffers.Count; i++)
        {
            byte[] data = existingExportBuffers[i].Data;
            Buffer.BlockCopy(data, 0, repacked, cursor, data.Length);
            foreach (var patch in existingExportBuffers[i].Patches)
                WriteInt32(repacked, cursor + patch.OffsetFieldPosition, cursor + patch.DataStartPosition);
            WriteInt32(repacked, entryOffsets[i] + 32, data.Length);
            WriteInt32(repacked, entryOffsets[i] + 36, cursor);
            cursor += data.Length;
        }

        WriteInt32(repacked, 8, newHeaderSize);
        RefreshPackageSourceCrc(repacked, header);
        return repacked;
    }

    private static byte[] BuildImportEntriesBlock(IReadOnlyList<NewImportSpec> specs, out int[] indices, int existingCount)
    {
        int count = specs?.Count ?? 0;
        indices = new int[count];
        if (count == 0) return Array.Empty<byte>();
        using MemoryStream ms = new();
        for (int i = 0; i < count; i++)
        {
            var s = specs![i];
            WriteI32(ms, s.PackageNameTableIndex);
            WriteI32(ms, s.PackageNameNumeric);
            WriteI32(ms, s.ClassNameTableIndex);
            WriteI32(ms, s.ClassNameNumeric);
            WriteI32(ms, s.OuterRef);
            WriteI32(ms, s.ObjectNameTableIndex);
            WriteI32(ms, s.ObjectNameNumeric);
            indices[i] = existingCount + i;
        }
        return ms.ToArray();
    }

    private static int ComputeImportTableEnd(UnrealHeader header, int oldHeaderSize)
    {
        // Each import entry is exactly 28 bytes: 3 FNames (8 each) + 1 int32.
        int end = header.ImportTableOffset + header.ImportTable.Count * 28;
        return Math.Min(end, oldHeaderSize);
    }

    public sealed record NewExportSpec(
        byte[] Data,
        IReadOnlyList<BulkDataPatch> Patches,
        int ClassRef,
        int SuperRef,
        int OuterRef,
        int ArchetypeRef,
        int ObjectNameTableIndex,
        int ObjectNameNumeric,
        ulong ObjectFlags,
        uint ExportFlags,
        IReadOnlyList<int> NetObjects,
        byte[] PackageGuid,                 // exactly 16 bytes
        uint PackageFlags);

    /// <summary>
    /// Repack a UPK while appending brand-new export-table entries.
    /// Each new entry's serial bytes go at the end of the file, behind the
    /// existing exports. Use addedNames to extend the NameTable for any new
    /// FName needed by the new entries' fields (ObjectName, class names, etc.).
    /// addedNameIndices and addedExportIndices receive the assigned 0-based
    /// table indices; caller converts to 1-based UE3 refs as needed.
    /// </summary>
    public static byte[] RepackWithAddedExports(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> existingExportBuffers,
        IReadOnlyList<NewExportSpec> addedExports,
        IReadOnlyList<string> addedNames,
        out int[] addedNameIndices,
        out int[] addedExportIndices)
        => RepackAddingExports(sourceBytes, header, existingExportBuffers, addedExports, addedNames,
                                out addedNameIndices, out addedExportIndices, compressed: false);

    public static byte[] RepackCompressedWithAddedExports(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> existingExportBuffers,
        IReadOnlyList<NewExportSpec> addedExports,
        IReadOnlyList<string> addedNames,
        out int[] addedNameIndices,
        out int[] addedExportIndices)
        => RepackAddingExports(sourceBytes, header, existingExportBuffers, addedExports, addedNames,
                                out addedNameIndices, out addedExportIndices, compressed: true);

    // Combined name + export + depends extension. Three header insertion
    // points, executed in source-byte order so byte offsets stay sane:
    //   A. End of NameTable    → insert addedNames block
    //   B. End of ExportTable  → insert new export-table entry block
    //   C. End of DependsTable → insert one int32 zero per added export
    //                            (each new export gets an empty depends list)
    // After splicing, patch the header's count + offset fields and write
    // the new export bodies after the (possibly grown) header.
    private static byte[] RepackAddingExports(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> existingExportBuffers,
        IReadOnlyList<NewExportSpec> addedExports,
        IReadOnlyList<string> addedNames,
        out int[] addedNameIndices,
        out int[] addedExportIndices,
        bool compressed)
    {
        if (compressed)
            sourceBytes = PrepareDecompressedHeaderBytes(sourceBytes, header);

        int oldHeaderSize = header.Size;
        int addedNameCount = addedNames?.Count ?? 0;
        int addedExportCount = addedExports?.Count ?? 0;

        // -- block A: added names ---------------------------------------
        byte[] addedNamesBlock = BuildNameBlock(addedNames, header.NameTable.Count, out addedNameIndices);
        int addedNameBytes = addedNamesBlock.Length;

        // -- block B: added export table entries ------------------------
        byte[] addedExportEntriesBlock = BuildExportEntriesBlock(addedExports, out addedExportIndices, header.ExportTable.Count);
        int addedExportEntryBytes = addedExportEntriesBlock.Length;

        // -- block C: depends rows for the new exports ------------------
        // Each row is "int32 count = 0". Only emit if the package already
        // has a DependsTable (offset > 0); some game packages omit it.
        bool hasDepends = header.DependsTableOffset > 0;
        int addedDependsBytes = hasDepends ? (addedExportCount * 4) : 0;
        byte[] addedDependsBlock = new byte[addedDependsBytes]; // zero-filled

        int totalHeaderGrowth = addedNameBytes + addedExportEntryBytes + addedDependsBytes;
        int newHeaderSize = oldHeaderSize + totalHeaderGrowth;

        // -- compute source insertion offsets in source-byte coordinates --
        int nameTableEnd = header.ImportTableOffset > 0 ? header.ImportTableOffset : oldHeaderSize;
        int exportTableEnd = ComputeExportTableEnd(header, oldHeaderSize);
        int dependsTableEnd = hasDepends ? ComputeDependsTableEnd(header, oldHeaderSize) : exportTableEnd;

        // -- assemble new bytes -----------------------------------------
        long existingBodySize = existingExportBuffers.Sum(b => (long)b.Data.Length);
        long addedBodySize = addedExports.Sum(e => (long)e.Data.Length);
        byte[] repacked = new byte[newHeaderSize + existingBodySize + addedBodySize];

        int dst = 0;
        // 0 .. nameTableEnd
        Buffer.BlockCopy(sourceBytes, 0, repacked, dst, nameTableEnd); dst += nameTableEnd;
        // added names
        Buffer.BlockCopy(addedNamesBlock, 0, repacked, dst, addedNameBytes); dst += addedNameBytes;
        // nameTableEnd .. exportTableEnd
        Buffer.BlockCopy(sourceBytes, nameTableEnd, repacked, dst, exportTableEnd - nameTableEnd);
        dst += exportTableEnd - nameTableEnd;
        // added export entries
        Buffer.BlockCopy(addedExportEntriesBlock, 0, repacked, dst, addedExportEntryBytes);
        dst += addedExportEntryBytes;
        // exportTableEnd .. dependsTableEnd
        Buffer.BlockCopy(sourceBytes, exportTableEnd, repacked, dst, dependsTableEnd - exportTableEnd);
        dst += dependsTableEnd - exportTableEnd;
        // added depends rows
        if (addedDependsBytes > 0)
        {
            Buffer.BlockCopy(addedDependsBlock, 0, repacked, dst, addedDependsBytes);
            dst += addedDependsBytes;
        }
        // dependsTableEnd .. oldHeaderSize (remaining header tail)
        int tail = oldHeaderSize - dependsTableEnd;
        if (tail > 0)
        {
            Buffer.BlockCopy(sourceBytes, dependsTableEnd, repacked, dst, tail);
            dst += tail;
        }
        // dst is now == newHeaderSize.

        // -- patch counts / offsets -------------------------------------
        HeaderTableOffsets t = LocateHeaderTableOffsets(repacked);
        if (addedNameCount > 0)
            WriteInt32(repacked, t.NameCountFieldOffset, header.NameTable.Count + addedNameCount);
        if (addedExportCount > 0)
            WriteInt32(repacked, t.NameCountFieldOffset + 8, header.ExportTable.Count + addedExportCount); // ExportCount is right after NameOffset
        // ImportTable offset shifts by addedNameBytes (because names came before).
        ShiftIfPositive(repacked, t.ImportOffsetFieldOffset, addedNameBytes);
        // Export offset shifts by addedNameBytes.
        ShiftIfPositive(repacked, t.ExportOffsetFieldOffset, addedNameBytes);
        // Depends offset shifts by addedNameBytes + addedExportEntryBytes.
        ShiftIfPositive(repacked, t.DependsOffsetFieldOffset, addedNameBytes + addedExportEntryBytes);
        // Everything past depends shifts by the full growth.
        ShiftIfPositive(repacked, t.ImportExportGuidsOffsetFieldOffset, totalHeaderGrowth);
        ShiftIfPositive(repacked, t.ThumbnailOffsetFieldOffset, totalHeaderGrowth);

        // -- write export bodies + patch SerialSize/Offset on each entry --
        List<int> existingEntryOffsets = LocateExportTableOffsets(repacked, header, headerOverrideStart: addedNameBytes);
        int cursor = newHeaderSize;
        // existing exports: same as the old path
        for (int i = 0; i < existingExportBuffers.Count; i++)
        {
            byte[] data = existingExportBuffers[i].Data;
            Buffer.BlockCopy(data, 0, repacked, cursor, data.Length);
            foreach (var patch in existingExportBuffers[i].Patches)
                WriteInt32(repacked, cursor + patch.OffsetFieldPosition, cursor + patch.DataStartPosition);
            WriteInt32(repacked, existingEntryOffsets[i] + 32, data.Length); // SerialSize
            WriteInt32(repacked, existingEntryOffsets[i] + 36, cursor);      // SerialOffset
            cursor += data.Length;
        }
        // added exports: their entries are immediately after the existing ones.
        // The block we built starts at the export-table-end-of-source + addedNameBytes
        // (because we shifted by addedNameBytes when splicing names earlier).
        int addedEntryCursor = exportTableEnd + addedNameBytes;
        for (int i = 0; i < addedExports.Count; i++)
        {
            var spec = addedExports[i];
            byte[] data = spec.Data;
            Buffer.BlockCopy(data, 0, repacked, cursor, data.Length);
            foreach (var patch in spec.Patches)
                WriteInt32(repacked, cursor + patch.OffsetFieldPosition, cursor + patch.DataStartPosition);
            // Patch SerialSize/Offset in the new entry. Entry size is
            // 68 + 4*NetObjects.Count; SerialSize at +32, SerialOffset at +36.
            WriteInt32(repacked, addedEntryCursor + 32, data.Length);
            WriteInt32(repacked, addedEntryCursor + 36, cursor);
            cursor += data.Length;
            addedEntryCursor += 68 + 4 * spec.NetObjects.Count;
        }

        WriteInt32(repacked, 8, newHeaderSize);
        RefreshPackageSourceCrc(repacked, header);
        return repacked;
    }

    private static byte[] BuildNameBlock(IReadOnlyList<string> addedNames, int baseIndex, out int[] indices)
    {
        int count = addedNames?.Count ?? 0;
        indices = new int[count];
        if (count == 0) return Array.Empty<byte>();
        using MemoryStream ms = new();
        for (int i = 0; i < count; i++)
        {
            string s = addedNames![i] ?? string.Empty;
            int len = System.Text.Encoding.ASCII.GetByteCount(s) + 1;
            ms.Write(BitConverter.GetBytes(len), 0, 4);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(s), 0, len - 1);
            ms.WriteByte(0);
            ms.Write(new byte[8], 0, 8);
            indices[i] = baseIndex + i;
        }
        return ms.ToArray();
    }

    private static byte[] BuildExportEntriesBlock(IReadOnlyList<NewExportSpec> specs, out int[] indices, int existingCount)
    {
        int count = specs?.Count ?? 0;
        indices = new int[count];
        if (count == 0) return Array.Empty<byte>();
        using MemoryStream ms = new();
        for (int i = 0; i < count; i++)
        {
            var s = specs![i];
            WriteI32(ms, s.ClassRef);
            WriteI32(ms, s.SuperRef);
            WriteI32(ms, s.OuterRef);
            WriteI32(ms, s.ObjectNameTableIndex);
            WriteI32(ms, s.ObjectNameNumeric);
            WriteI32(ms, s.ArchetypeRef);
            WriteU64(ms, s.ObjectFlags);
            WriteI32(ms, 0);                       // SerialSize placeholder (patched later)
            WriteI32(ms, 0);                       // SerialOffset placeholder
            WriteU32(ms, s.ExportFlags);
            WriteI32(ms, s.NetObjects?.Count ?? 0);
            foreach (var n in s.NetObjects ?? Array.Empty<int>()) WriteI32(ms, n);
            byte[] guid = s.PackageGuid ?? new byte[16];
            if (guid.Length != 16) throw new ArgumentException("PackageGuid must be exactly 16 bytes.");
            ms.Write(guid, 0, 16);
            WriteU32(ms, s.PackageFlags);
            indices[i] = existingCount + i;
        }
        return ms.ToArray();
    }

    private static void WriteI32(MemoryStream ms, int v) => ms.Write(BitConverter.GetBytes(v), 0, 4);
    private static void WriteU32(MemoryStream ms, uint v) => ms.Write(BitConverter.GetBytes(v), 0, 4);
    private static void WriteU64(MemoryStream ms, ulong v) => ms.Write(BitConverter.GetBytes(v), 0, 8);

    private static int ComputeExportTableEnd(UnrealHeader header, int oldHeaderSize)
    {
        int end = header.ExportTableOffset;
        foreach (var e in header.ExportTable)
            end += 68 + 4 * e.NetObjects.Count;
        return Math.Min(end, oldHeaderSize);
    }

    private static int ComputeDependsTableEnd(UnrealHeader header, int oldHeaderSize)
    {
        if (header.DependsTableOffset <= 0) return ComputeExportTableEnd(header, oldHeaderSize);
        // ImportExportGuidsOffset is the natural end if non-zero; else fall
        // back to the header end.
        if (header.ImportExportGuidsOffset > 0) return header.ImportExportGuidsOffset;
        if (header.ThumbnailTableOffset > 0) return header.ThumbnailTableOffset;
        return oldHeaderSize;
    }

    private static byte[] RepackCoreWithAddedNames(
        byte[] sourceBytes,
        UnrealHeader header,
        IReadOnlyList<ExportBuffer> exportBuffers,
        IReadOnlyList<string> addedNames,
        out int[] addedIndices)
    {
        int oldHeaderSize = header.Size;
        int addedCount = addedNames?.Count ?? 0;

        // 1. Serialize the new NameTable entries (ASCII length-prefixed string
        //    + 8-byte flags each). Wwise event names like "play_vox_..." are
        //    pure ASCII so we never need wide encoding here.
        addedIndices = new int[addedCount];
        byte[] addedBlock = Array.Empty<byte>();
        int addedBytes = 0;
        if (addedCount > 0)
        {
            using MemoryStream ms = new();
            for (int i = 0; i < addedCount; i++)
            {
                string s = addedNames![i] ?? string.Empty;
                int len = System.Text.Encoding.ASCII.GetByteCount(s) + 1; // +null term
                ms.Write(BitConverter.GetBytes(len), 0, 4);
                ms.Write(System.Text.Encoding.ASCII.GetBytes(s), 0, len - 1);
                ms.WriteByte(0);
                ms.Write(new byte[8], 0, 8); // Flags = 0
                addedIndices[i] = header.NameTable.Count + i;
            }
            addedBlock = ms.ToArray();
            addedBytes = addedBlock.Length;
        }

        int newHeaderSize = oldHeaderSize + addedBytes;
        byte[] repacked = new byte[newHeaderSize + exportBuffers.Sum(static b => b.Data.Length)];

        // 2. Build the new header bytes by splicing addedBlock into the
        //    NameTable region of the source header. NameTable runs from
        //    header.NameTableOffset to header.ImportTableOffset (always
        //    contiguous in UE3 packages).
        int nameTableEnd = header.ImportTableOffset > 0 ? header.ImportTableOffset : oldHeaderSize;
        Buffer.BlockCopy(sourceBytes, 0, repacked, 0, Math.Min(nameTableEnd, sourceBytes.Length));
        if (addedBytes > 0)
            Buffer.BlockCopy(addedBlock, 0, repacked, nameTableEnd, addedBytes);
        int tailLen = Math.Min(oldHeaderSize - nameTableEnd, sourceBytes.Length - nameTableEnd);
        if (tailLen > 0)
            Buffer.BlockCopy(sourceBytes, nameTableEnd, repacked, nameTableEnd + addedBytes, tailLen);

        // 3. Patch the count/offset fields in the new header bytes. We locate
        //    them dynamically (Group string is variable length).
        if (addedCount > 0)
        {
            HeaderTableOffsets t = LocateHeaderTableOffsets(repacked);
            WriteInt32(repacked, t.NameCountFieldOffset,     header.NameTable.Count + addedCount);
            // NameTable offset itself doesn't change (NameTable still starts at the same place).
            ShiftIfPositive(repacked, t.ExportOffsetFieldOffset,           addedBytes);
            ShiftIfPositive(repacked, t.ImportOffsetFieldOffset,           addedBytes);
            ShiftIfPositive(repacked, t.DependsOffsetFieldOffset,          addedBytes);
            ShiftIfPositive(repacked, t.ImportExportGuidsOffsetFieldOffset, addedBytes);
            ShiftIfPositive(repacked, t.ThumbnailOffsetFieldOffset,        addedBytes);
        }

        // 4. Write exports after the (possibly grown) header. SerialOffset
        //    inside each export-table entry is rewritten from the running
        //    `cursor`, so it automatically reflects the new header size.
        List<int> entryOffsets = LocateExportTableOffsets(repacked, header, headerOverrideStart: addedBytes);
        int cursor = newHeaderSize;
        for (int i = 0; i < exportBuffers.Count; i++)
        {
            byte[] exportData = exportBuffers[i].Data;
            Buffer.BlockCopy(exportData, 0, repacked, cursor, exportData.Length);

            foreach (BulkDataPatch patch in exportBuffers[i].Patches)
                WriteInt32(repacked, cursor + patch.OffsetFieldPosition, cursor + patch.DataStartPosition);

            WriteInt32(repacked, entryOffsets[i] + 32, exportData.Length);  // SerialSize
            WriteInt32(repacked, entryOffsets[i] + 36, cursor);             // SerialOffset
            cursor += exportData.Length;
        }

        WriteInt32(repacked, 8, newHeaderSize);  // Size field
        RefreshPackageSourceCrc(repacked, header);
        return repacked;
    }

    private static void ShiftIfPositive(byte[] buffer, int fieldOffset, int delta)
    {
        if (fieldOffset <= 0) return;
        int current = BitConverter.ToInt32(buffer, fieldOffset);
        if (current > 0) WriteInt32(buffer, fieldOffset, current + delta);
    }

    // Public helper for callers that need the decompressed flat bytes of a
    // compressed package (e.g. the rebuilder when also appending NameTable
    // entries via RepackWithAddedNames). Mirrors the decompression step that
    // RepackCompressedCore would otherwise do internally.
    public static byte[] PrepareDecompressedHeaderBytes(byte[] originalBytes, UnrealHeader header)
    {
        if (header.CompressedChunks.Count == 0) return originalBytes;
        byte[] decompressedBytes = DecompressFullPackage(header);
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(originalBytes);
        int compressionTableOffset = offsets.CompressionCountOffset + sizeof(int);
        int compressionTableLength = header.CompressionTableCount * 16;
        int compressedDataStart = header.CompressedChunks.Min(static chunk => chunk.CompressedOffset);

        Buffer.BlockCopy(originalBytes, 0, decompressedBytes, 0,
            Math.Min(compressionTableOffset, Math.Min(originalBytes.Length, decompressedBytes.Length)));

        int shiftedHeaderSourceOffset = compressionTableOffset + compressionTableLength;
        int shiftedHeaderLength = Math.Max(0, compressedDataStart - shiftedHeaderSourceOffset);
        if (shiftedHeaderLength > 0)
        {
            Buffer.BlockCopy(originalBytes, shiftedHeaderSourceOffset, decompressedBytes, compressionTableOffset,
                Math.Min(shiftedHeaderLength, Math.Min(
                    originalBytes.Length - shiftedHeaderSourceOffset,
                    decompressedBytes.Length - compressionTableOffset)));
        }

        ClearCompressionHeaderFlags(decompressedBytes);
        WriteInt32(decompressedBytes, offsets.CompressionCountOffset, 0);
        return decompressedBytes;
    }

    private static byte[] RepackCompressedCore(byte[] originalBytes, UnrealHeader header, IReadOnlyList<ExportBuffer> exportBuffers)
    {
        byte[] decompressedBytes = DecompressFullPackage(header);
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(originalBytes);
        int compressionTableOffset = offsets.CompressionCountOffset + sizeof(int);
        int compressionTableLength = header.CompressionTableCount * 16;
        int compressedDataStart = header.CompressedChunks.Min(static chunk => chunk.CompressedOffset);

        Buffer.BlockCopy(originalBytes, 0, decompressedBytes, 0,
            Math.Min(compressionTableOffset, Math.Min(originalBytes.Length, decompressedBytes.Length)));

        int shiftedHeaderSourceOffset = compressionTableOffset + compressionTableLength;
        int shiftedHeaderLength = Math.Max(0, compressedDataStart - shiftedHeaderSourceOffset);
        if (shiftedHeaderLength > 0)
        {
            Buffer.BlockCopy(
                originalBytes,
                shiftedHeaderSourceOffset,
                decompressedBytes,
                compressionTableOffset,
                Math.Min(shiftedHeaderLength, Math.Min(
                    originalBytes.Length - shiftedHeaderSourceOffset,
                    decompressedBytes.Length - compressionTableOffset)));
        }

        ClearCompressionHeaderFlags(decompressedBytes);
        WriteInt32(decompressedBytes, offsets.CompressionCountOffset, 0);
        byte[] repacked = RepackCore(decompressedBytes, header, exportBuffers);
        RefreshPackageSourceCrc(repacked, header);
        return repacked;
    }

    private static byte[] DecompressFullPackage(UnrealHeader header)
    {
        int start = header.CompressedChunks.Min(static chunk => chunk.UncompressedOffset);
        int totalSize = header.CompressedChunks
            .SelectMany(static chunk => chunk.Header.Blocks)
            .Sum(static block => block.UncompressedSize) + start;

        byte[] data = new byte[totalSize];
        foreach (UnrealCompressedChunk chunk in header.CompressedChunks)
        {
            int localOffset = 0;
            foreach (UnrealCompressedChunkBlock block in chunk.Header.Blocks)
            {
                byte[] decompressed = block.CompressedData.Decompress(block.UncompressedSize);
                Buffer.BlockCopy(decompressed, 0, data, chunk.UncompressedOffset + localOffset, decompressed.Length);
                localOffset += block.UncompressedSize;
            }
        }

        return data;
    }

    private static void ClearCompressionHeaderFlags(byte[] bytes)
    {
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(bytes);
        WriteUInt32(bytes, offsets.PackageFlagsOffset,
            ReadUInt32(bytes, offsets.PackageFlagsOffset) & ~(uint)(EPackageFlags.Compressed | EPackageFlags.FullyCompressed));
        WriteUInt32(bytes, offsets.CompressionFlagsOffset, 0);
    }

    private static HeaderPatchOffsets LocateHeaderPatchOffsets(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);

        stream.Position = 8;
        _ = reader.ReadInt32();  // Size field

        int groupSize = reader.ReadInt32();
        if (groupSize < 0)
            stream.Position += -groupSize * 2L;
        else if (groupSize > 0)
            stream.Position += groupSize;

        int packageFlagsOffset = checked((int)stream.Position);
        stream.Position += sizeof(uint);

        stream.Position += sizeof(int) * 11L;
        stream.Position += 16;  // GUID

        int generationCount = reader.ReadInt32();
        stream.Position += generationCount * 12L;
        stream.Position += sizeof(uint) * 2L;  // engine/cooker version

        int compressionFlagsOffset = checked((int)stream.Position);
        int compressionCountOffset = compressionFlagsOffset + sizeof(uint);

        return new HeaderPatchOffsets(packageFlagsOffset, compressionFlagsOffset, compressionCountOffset);
    }

    private static List<int> LocateExportTableOffsets(byte[] originalBytes, UnrealHeader header, int headerOverrideStart = 0)
    {
        List<int> offsets = new(header.ExportTable.Count);
        int cursor = header.ExportTableOffset + headerOverrideStart;
        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            offsets.Add(cursor);
            cursor += 68 + (export.NetObjects.Count * sizeof(int));
        }
        return offsets;
    }

    // Locates the byte offsets of the count/offset INT32 fields in the raw
    // package header. Used when growing the NameTable so we can patch every
    // subsequent table-offset field by the added byte count.
    //
    // Layout (skipping Group string which is variable):
    //   Signature(4) Version(2) Licensee(2) Size(4) Group(...) Flags(4)
    //   NameTableCount(4) NameTableOffset(4)
    //   ExportTableCount(4) ExportTableOffset(4)
    //   ImportTableCount(4) ImportTableOffset(4)
    //   DependsTableOffset(4)
    //   ImportExportGuidsOffset(4) ImportGuidsCount(4) ExportGuidsCount(4)
    //   ThumbnailTableOffset(4)
    private static HeaderTableOffsets LocateHeaderTableOffsets(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);

        stream.Position = 12;       // skip signature/version/licensee/size
        // Group: int32 length + payload (ASCII null-term OR -count UTF-16 pairs)
        int groupSize = reader.ReadInt32();
        if (groupSize < 0) stream.Position += -groupSize * 2L;
        else if (groupSize > 0) stream.Position += groupSize;

        stream.Position += sizeof(uint);  // Flags

        int nameCount = checked((int)stream.Position);
        stream.Position += sizeof(int) * 2;  // NameTableCount + NameTableOffset
        int exportCount = checked((int)stream.Position);
        stream.Position += sizeof(int);
        int exportOffset = checked((int)stream.Position);
        stream.Position += sizeof(int);
        int importCount = checked((int)stream.Position);
        stream.Position += sizeof(int);
        int importOffset = checked((int)stream.Position);
        stream.Position += sizeof(int);
        int dependsOffset = checked((int)stream.Position);
        stream.Position += sizeof(int);
        int impExpGuidsOffset = checked((int)stream.Position);
        stream.Position += sizeof(int) * 3;  // ImpExpGuidsOffset + ImportGuids + ExportGuids
        int thumbnailOffset = checked((int)stream.Position);

        return new HeaderTableOffsets(
            nameCount,
            exportOffset,
            importOffset,
            dependsOffset,
            impExpGuidsOffset,
            thumbnailOffset);
    }

    private readonly record struct HeaderTableOffsets(
        int NameCountFieldOffset,
        int ExportOffsetFieldOffset,
        int ImportOffsetFieldOffset,
        int DependsOffsetFieldOffset,
        int ImportExportGuidsOffsetFieldOffset,
        int ThumbnailOffsetFieldOffset);

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) => BitConverter.ToUInt32(buffer, offset);

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void RefreshPackageSourceCrc(byte[] repacked, UnrealHeader header)
    {
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(repacked);
        // PackageSource sits IMMEDIATELY after the CompressionTable in the header.
        // For a compressed package being rewritten as uncompressed,
        // PrepareDecompressedHeaderBytes already shifted the post-compression-table
        // bytes LEFT by `originalCount * 16` and wrote 0 to CompressionCount, so
        // the actual location of PackageSource is now `CompressionCountOffset + 4`
        // (skip zero entries). Reading `header.CompressionTableCount` instead
        // (the pre-shift count) places this CRC write 48+ bytes too far in,
        // CORRUPTING whatever post-PackageSource field sits there (typically
        // the count int of TextureAllocations / AdditionalPackagesToCook —
        // turning it into a huge value the engine then can't bounds-check,
        // tripping the TArray assertion on package load).
        int currentCompressionCount = BitConverter.ToInt32(repacked, offsets.CompressionCountOffset);
        if (currentCompressionCount < 0 || currentCompressionCount > 1024) return; // sanity
        int packageSourceOffset = offsets.CompressionCountOffset + sizeof(int) + currentCompressionCount * 16;
        if (packageSourceOffset < 0 || packageSourceOffset + sizeof(uint) > repacked.Length)
            return;

        byte[] crcBytes = (byte[])repacked.Clone();
        Array.Clear(crcBytes, packageSourceOffset, sizeof(uint));
        uint crc = CrcUtility.Compute(crcBytes);
        WriteUInt32(repacked, packageSourceOffset, crc);
    }

    private readonly record struct HeaderPatchOffsets(
        int PackageFlagsOffset,
        int CompressionFlagsOffset,
        int CompressionCountOffset);
}

