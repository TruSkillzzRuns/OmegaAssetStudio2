using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Low-level byte-stream writer that grows a target UPK file by adding new
// names, imports, exports, depends slots, and export bodies. Produces a flat
// uncompressed output suitable for File.WriteAllBytes.
//
// Critical UE3 header layout (after Signature/Version/Licensee/Size/Group/Flags):
//   NameCount, NameOffset
//   ExportCount, ExportOffset
//   ImportCount, ImportOffset
//   DependsOffset
//   ImportExportGuidsOffset, ImportGuidsCount, ExportGuidsCount
//   ThumbnailOffset
//   GUID(16), GenerationCount, Generations[12 each],
//   EngineVersion, CookerVersion, CompressionFlags, CompressionCount,
//   CompressedChunks[16 each], PackageSource, AdditionalPackagesToCook,
//   TextureAllocations
//
// On-disk table order: NameTable -> ImportTable -> ExportTable -> DependsTable
// (-> bodies). Each insertion shifts every later offset by its size delta.
internal static class Phase2TableExtender
{
    private const int ImportEntryBytes = 28;       // 4 FName(8) + 4 FName(8) + 4 int + 4 FObject(8). Wait: 8+8+4+8 = 28.

    // Build a new export-table entry size: 68 bytes + 4 * NetObjects.Count.

    public static byte[] Build(
        byte[] originalTargetBytes,
        UnrealHeader tgtHeader,
        List<string> addedNames,
        List<UnrealImportTableEntry> addedImports,
        List<UnrealExportTableEntry> addedExports,
        List<byte[]> addedExportBodies,
        Dictionary<string, int> futureNameByText,
        UnrealHeader srcHeader,
        List<(int TgtIdx, byte[] Body)>? matchedOverrides = null,
        List<List<(int OffsetFieldInBody, int PayloadStartInBody)>>? addedBodyBulkPatches = null,
        List<List<(int OffsetFieldInBody, int PayloadStartInBody)>>? matchedOverridePatches = null,
        Dictionary<UnrealImportTableEntry, int>? syntheticImportOuterTargetRefs = null)
    {
        if (addedExports.Count != addedExportBodies.Count)
            throw new ArgumentException("Each added export must have a translated body.");
        matchedOverrides ??= new List<(int, byte[])>();
        addedBodyBulkPatches ??= new List<List<(int, int)>>();

        // ----- Pre-compute every byte size -----
        byte[] addedNameBlock = SerializeAddedNames(addedNames);
        byte[] addedImportBlock = SerializeAddedImports(addedImports, srcHeader, futureNameByText, tgtHeader, addedImports.Count, addedExports, syntheticImportOuterTargetRefs);

        // Compute future-export indices so addedImports' OuterRef translation
        // can reference them too. Already known via futureExportByPath? We
        // pass that via the extender path; here we just need a closure of
        // future import positions for the import entry serialization.

        // Export entries: layout = 68 + 4*NetObjects. Source's NetObjects may
        // differ; copy them verbatim.
        int addedExportBytes = 0;
        foreach (var e in addedExports)
            addedExportBytes += 68 + e.NetObjects.Count * 4;
        // Each added export needs a 4-byte depends slot too.
        int addedDependsBytes = addedExports.Count * 4;
        int addedNameBytes   = addedNameBlock.Length;
        int addedImportBytes = addedImportBlock.Length;

        int oldHeaderSize = tgtHeader.Size;
        int newHeaderSize = oldHeaderSize + addedNameBytes + addedImportBytes + addedExportBytes + addedDependsBytes;

        // Combined body-region delta — shifts every SerialDataOffset in the
        // existing export table by this much.
        int bodyRegionShift = addedNameBytes + addedImportBytes + addedExportBytes + addedDependsBytes;

        // Total body bytes to append: sum of original body sizes (already in
        // originalTargetBytes after the header) + sum of new body sizes
        // + sum of matched-override body sizes (overrides leave the old body
        // bytes as orphan dead-space in place and append the new body at the
        // end — simpler than splicing in-place, the file just has some wasted
        // bytes which the engine ignores).
        long totalBodyBytes = (long)originalTargetBytes.Length - oldHeaderSize;
        foreach (var body in addedExportBodies)
            totalBodyBytes += body.Length;
        foreach (var (_, body) in matchedOverrides)
            totalBodyBytes += body.Length;

        long totalOutBytes = (long)newHeaderSize + totalBodyBytes;
        byte[] outBuf = new byte[totalOutBytes];

        // ----- Lay down the header sections -----
        // Section 1: bytes 0..(NameTableOffset+oldNameBytes) — header start
        // through end of existing name table.
        int oldNameEnd = tgtHeader.ImportTableOffset > 0
            ? tgtHeader.ImportTableOffset
            : tgtHeader.NameTableOffset; // fallback (no imports? unusual)
        Buffer.BlockCopy(originalTargetBytes, 0, outBuf, 0, oldNameEnd);

        // (No in-place name-slot rewrites here. We previously experimented
        // with applying targetNameRenames after the bulk-copy, but the
        // working sibling-chassis baseline (Gambit_Classic chassis +
        // Classic_Jacketless source) keeps target's pawn-class name
        // UNCHANGED and just appends source's name as a new entry — so
        // the rename approach was wrong. The compare-on-disk-vs-in-memory
        // path also surfaced false-positive "rename" hits on Unicode-edge
        // names whose UpkManager .String getter doesn't byte-match disk.
        // If a future tool genuinely needs to change a target name slot,
        // add a SPECIFIC API for it rather than blanket-comparing every
        // slot.)

        // Section 2: addedNameBlock
        int cursor = oldNameEnd;
        if (addedNameBytes > 0)
        {
            Buffer.BlockCopy(addedNameBlock, 0, outBuf, cursor, addedNameBytes);
            cursor += addedNameBytes;
        }

        // Section 3: existing import table
        int oldImportLen = tgtHeader.ExportTableOffset - tgtHeader.ImportTableOffset;
        Buffer.BlockCopy(originalTargetBytes, tgtHeader.ImportTableOffset, outBuf, cursor, oldImportLen);
        cursor += oldImportLen;

        // Section 4: added imports
        if (addedImportBytes > 0)
        {
            Buffer.BlockCopy(addedImportBlock, 0, outBuf, cursor, addedImportBytes);
            cursor += addedImportBytes;
        }

        // Section 5: existing export table. We copy raw bytes and patch
        // SerialDataOffset fields in place after writing.
        int oldExportLen = tgtHeader.DependsTableOffset - tgtHeader.ExportTableOffset;
        int existingExportTableOutOffset = cursor;
        Buffer.BlockCopy(originalTargetBytes, tgtHeader.ExportTableOffset, outBuf, cursor, oldExportLen);
        cursor += oldExportLen;

        // Section 6: added export-table entries. SerialDataOffset filled in
        // shortly when we know the body-region cursor; we write entries now
        // with placeholder offsets, then patch them.
        int addedExportTableOutOffset = cursor;
        WriteAddedExportEntries(
            outBuf, cursor,
            addedExports, addedExportBodies,
            srcHeader, tgtHeader,
            futureNameByText,
            addedImports.Count);
        cursor += addedExportBytes;

        // Section 7: existing depends table — just zeros equal in count to
        // the existing export count (4 bytes each). Read from original.
        int oldDependsLen = originalTargetBytes.Length - tgtHeader.DependsTableOffset
                            - (originalTargetBytes.Length - oldHeaderSize); // exclude body bytes
        // Easier: oldDependsLen = ExportTable.Count * 4 (typical) but use the
        // header field if available.
        oldDependsLen = tgtHeader.ExportTable.Count * 4;
        Buffer.BlockCopy(originalTargetBytes, tgtHeader.DependsTableOffset, outBuf, cursor, oldDependsLen);
        cursor += oldDependsLen;

        // Section 8: added depends slots — zero bytes.
        if (addedDependsBytes > 0)
        {
            // Already zero-initialized; just advance.
            cursor += addedDependsBytes;
        }

        // We should now be exactly at newHeaderSize.
        if (cursor != newHeaderSize)
        {
            throw new InvalidOperationException(
                $"Phase2TableExtender header layout mismatch: cursor={cursor} expected={newHeaderSize}");
        }

        // Section 9: write each existing export's body to its shifted position.
        //
        // BUG FIX: the previous implementation bulk-copied
        // originalTargetBytes[oldHeaderSize..end] verbatim. That assumed
        // originalTargetBytes was a flat byte stream where each export's body
        // lives at its SerialDataOffset. For COMPRESSED target packages,
        // PrepareDecompressedHeaderBytes returns a buffer whose layout does
        // NOT match that assumption — body bytes land at offsets that don't
        // correspond to tgtHeader.ExportTable[i].SerialDataOffset values.
        // The bulk copy then wrote unrelated bytes into each export's
        // intended slot, surfacing as e.g. apex's body containing UObject
        // property-tag bytes (with embedded source-side FObject refs that
        // overshoot the export table — "Bad export index N/M" at engine load).
        //
        // Correct approach: rely on UpkManager having parsed each export's
        // body bytes into e.UnrealObjectReader.GetBytes() during header read.
        // Those are the authoritative decompressed body bytes regardless of
        // the on-disk compression layout. Write each one to its new offset.
        // We also pad/seed the rest of the body region with the bulk-copy
        // fallback so any byte ranges NOT owned by an export entry (rare in
        // practice but possible if the original had wasted gaps) carry over.
        int bodyRegionWriteStart = cursor;
        int origBodyLen = Math.Max(0, originalTargetBytes.Length - oldHeaderSize);
        if (origBodyLen > 0)
            Buffer.BlockCopy(originalTargetBytes, oldHeaderSize, outBuf, cursor, origBodyLen);
        // Now overwrite the slot for every export whose UnrealObjectReader has
        // authoritative bytes. SerialDataOffset is in the OLD (pre-shift)
        // address space; convert to NEW by adding bodyRegionShift, then write
        // the body's GetBytes() payload there.
        for (int idx = 0; idx < tgtHeader.ExportTable.Count; idx++)
        {
            var e = tgtHeader.ExportTable[idx];
            if (e.UnrealObjectReader is null) continue;
            byte[] bodyBytes = e.UnrealObjectReader.GetBytes();
            if (bodyBytes is null || bodyBytes.Length == 0) continue;
            int newOff = e.SerialDataOffset + bodyRegionShift;
            if (newOff < bodyRegionWriteStart) continue;
            if (newOff + bodyBytes.Length > outBuf.Length) continue;
            Buffer.BlockCopy(bodyBytes, 0, outBuf, newOff, bodyBytes.Length);
        }

        // Section 10: append new bodies and stamp each new export entry's
        // SerialDataOffset with its actual position.
        int bodyCursor = cursor + origBodyLen;
        int entryCursor = addedExportTableOutOffset;
        for (int i = 0; i < addedExports.Count; i++)
        {
            byte[] body = addedExportBodies[i];
            Buffer.BlockCopy(body, 0, outBuf, bodyCursor, body.Length);
            // Apply bulk-data offset patches (Texture2D inline mips).
            // Each patch's OffsetField is at body-relative position P; we
            // stamp the absolute file offset of the payload start there.
            if (i < addedBodyBulkPatches.Count)
            {
                foreach (var patch in addedBodyBulkPatches[i])
                {
                    int absPayload = bodyCursor + patch.PayloadStartInBody;
                    WriteInt32(outBuf, bodyCursor + patch.OffsetFieldInBody, absPayload);
                }
            }
            // Patch SerialDataSize (offset 32 from entry start) and
            // SerialDataOffset (offset 36 from entry start). Entry layout:
            //   0:  ClassReference int32
            //   4:  SuperReference int32
            //   8:  OuterReference int32
            //   12: ObjectNameIndex.NameIdx int32
            //   16: ObjectNameIndex.NumericExt int32
            //   20: ArchetypeReference int32
            //   24: ObjectFlags uint64 (8 bytes)
            //   32: SerialDataSize int32
            //   36: SerialDataOffset int32
            //   40: ExportFlags uint32
            //   44: NetObjectCount int32
            //   48..: NetObjects[N] int32 (N*4 bytes)
            //   48+N*4: PackageGuid (16 bytes)
            //   64+N*4: PackageFlags uint32
            // Total: 68 + 4N
            WriteInt32(outBuf, entryCursor + 32, body.Length);
            WriteInt32(outBuf, entryCursor + 36, bodyCursor);
            int entrySize = 68 + addedExports[i].NetObjects.Count * 4;
            entryCursor += entrySize;
            bodyCursor += body.Length;
        }

        // ----- Patch ALL existing export-table entries' SerialDataOffset -----
        // Every existing export's body has shifted by bodyRegionShift bytes.
        // Also record per-export entry offset so the override pass can find
        // the right entry to patch.
        var existingEntryOffsets = new int[tgtHeader.ExportTable.Count];
        int existingEntryCursor = existingExportTableOutOffset;
        for (int idx = 0; idx < tgtHeader.ExportTable.Count; idx++)
        {
            var e = tgtHeader.ExportTable[idx];
            existingEntryOffsets[idx] = existingEntryCursor;
            int oldOffset = ReadInt32(outBuf, existingEntryCursor + 36);
            WriteInt32(outBuf, existingEntryCursor + 36, oldOffset + bodyRegionShift);
            existingEntryCursor += 68 + e.NetObjects.Count * 4;
        }

        // ----- Apply matched overrides -----
        // bodyCursor currently points just past the last new-export body. For
        // each override, append the translated body there and rewrite the
        // matched existing entry's SerialDataSize + SerialDataOffset to point
        // at the new location. The original (now-stale) body bytes for that
        // export remain in the body region as orphan space, which the engine
        // ignores. This keeps the writer simple and avoids the offset-shift
        // hell of true in-place body replacement.
        for (int oi = 0; oi < matchedOverrides.Count; oi++)
        {
            var (tgtIdx, body) = matchedOverrides[oi];
            if (tgtIdx < 0 || tgtIdx >= existingEntryOffsets.Length) continue;
            Buffer.BlockCopy(body, 0, outBuf, bodyCursor, body.Length);
            // A body holding inline bulk data (texture mips) records where its
            // payloads start, and each record is stamped with the absolute
            // file offset of that payload — the same stamping the added-export
            // path does. Without it the engine seeks to a stale offset and
            // reads another object's bytes as pixels.
            if (matchedOverridePatches != null && oi < matchedOverridePatches.Count)
            {
                foreach (var patch in matchedOverridePatches[oi])
                {
                    int absPayload = bodyCursor + patch.PayloadStartInBody;
                    WriteInt32(outBuf, bodyCursor + patch.OffsetFieldInBody, absPayload);
                }
            }
            int entryOff = existingEntryOffsets[tgtIdx];
            WriteInt32(outBuf, entryOff + 32, body.Length); // SerialDataSize
            WriteInt32(outBuf, entryOff + 36, bodyCursor);  // SerialDataOffset
            bodyCursor += body.Length;
        }

        // ----- Patch header count + offset fields -----
        PatchHeaderFields(outBuf, tgtHeader, addedNameBytes, addedImportBytes, addedExportBytes,
            addedDependsBytes, addedNames.Count, addedImports.Count, addedExports.Count, newHeaderSize);

        // ----- Refresh the package-source CRC -----
        RefreshPackageSourceCrc(outBuf, tgtHeader);

        return outBuf;
    }

    private static byte[] SerializeAddedNames(List<string> names)
    {
        if (names.Count == 0) return Array.Empty<byte>();
        using MemoryStream ms = new();
        foreach (var name in names)
        {
            string s = name ?? string.Empty;
            int len = Encoding.ASCII.GetByteCount(s) + 1; // +null term
            ms.Write(BitConverter.GetBytes(len), 0, 4);
            ms.Write(Encoding.ASCII.GetBytes(s), 0, len - 1);
            ms.WriteByte(0);
            ms.Write(new byte[8], 0, 8); // Flags = 0
        }
        return ms.ToArray();
    }

    private static byte[] SerializeAddedImports(
        List<UnrealImportTableEntry> addedImports,
        UnrealHeader srcHeader,
        Dictionary<string, int> futureNameByText,
        UnrealHeader tgtHeader,
        int addedImportsCount,
        List<UnrealExportTableEntry> addedExports,
        Dictionary<UnrealImportTableEntry, int>? syntheticImportOuterTargetRefs = null)
    {
        if (addedImports.Count == 0) return Array.Empty<byte>();

        // Where each export being ADDED alongside these imports will sit. An
        // import's owner is frequently one of them - a costume that inlines a
        // package as an export of its own, with textures borrowed from it -
        // and looking only at what target already has leaves such an import
        // with no owner at all.
        //
        // The engine then looks for the object at the top of the package
        // rather than inside the one that holds it, finds nothing, and
        // substitutes white. Verified on JeanGrey_Horseman: her face and hair
        // diffuse is jeangrey_darkphoenixvu.jeangrey_phoenix_vu_red_diff, its
        // owner is a package the costume carries as an export, and the
        // borrowing arrived owned by nothing - so she wore a white face while
        // the texture beside it, owned by an imported package, was fine.
        var futureExportRefByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < addedExports.Count; i++)
        {
            string path = addedExports[i].GetPathName();
            if (!futureExportRefByPath.ContainsKey(path))
                futureExportRefByPath[path] = tgtHeader.ExportTable.Count + i + 1;
        }
        // Build future import lookup by source path so we can translate
        // OuterReference fields that may point at other added imports.
        var futureImportRefBySrcImportIdx = new Dictionary<int, int>();
        for (int i = 0; i < addedImports.Count; i++)
        {
            int srcIdx = srcHeader.ImportTable.IndexOf(addedImports[i]);
            if (srcIdx >= 0)
                futureImportRefBySrcImportIdx[srcIdx] = -(tgtHeader.ImportTable.Count + i + 1);
        }
        // Target import lookup by full path (so source imports that DO exist
        // in target by path can still translate).
        var tgtImportByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgtHeader.ImportTable.Count; i++)
            tgtImportByPath[Phase2MaterialExtender.ImportFullPath(tgtHeader, tgtHeader.ImportTable[i])] = -(i + 1);

        using MemoryStream ms = new();
        foreach (var imp in addedImports)
        {
            // Same FName (base idx + numeric) fix as WriteAddedExportEntries —
            // composed names like "Some_3" don't appear in the target name
            // table; we must look up the BASE and emit the numeric separately.
            int pkgBaseIdx = imp.PackageNameIndex?.Index ?? 0;
            int clsBaseIdx = imp.ClassNameIndex?.Index ?? 0;
            int objBaseIdx = imp.ObjectNameIndex?.Index ?? 0;
            string pkgName = (pkgBaseIdx >= 0 && pkgBaseIdx < srcHeader.NameTable.Count)
                ? srcHeader.NameTable[pkgBaseIdx]?.Name?.String ?? string.Empty : string.Empty;
            string clsName = (clsBaseIdx >= 0 && clsBaseIdx < srcHeader.NameTable.Count)
                ? srcHeader.NameTable[clsBaseIdx]?.Name?.String ?? string.Empty : string.Empty;
            string objName = (objBaseIdx >= 0 && objBaseIdx < srcHeader.NameTable.Count)
                ? srcHeader.NameTable[objBaseIdx]?.Name?.String ?? string.Empty : string.Empty;

            int pkgNameIdx = ResolveNameIdx(pkgName, tgtHeader, futureNameByText);
            int clsNameIdx = ResolveNameIdx(clsName, tgtHeader, futureNameByText);
            int objNameIdx = ResolveNameIdx(objName, tgtHeader, futureNameByText);
            int pkgNumeric = imp.PackageNameIndex?.Numeric ?? 0;
            int clsNumeric = imp.ClassNameIndex?.Numeric ?? 0;
            int objNumeric = imp.ObjectNameIndex?.Numeric ?? 0;

            // OuterReference translation:
            int outerRefOut;
            int outerRefSrc = imp.OuterReference;
            // A synthetic import may name its outer in the TARGET's frame
            // directly - the way one costume's masked-base import hangs off the
            // chassis's own chbasematerials_v2 package import, which is the
            // shape that provably resolves at runtime.
            if (syntheticImportOuterTargetRefs != null
                && syntheticImportOuterTargetRefs.TryGetValue(imp, out int directOuter))
            {
                outerRefOut = directOuter;
            }
            else if (outerRefSrc == 0)
            {
                outerRefOut = 0;
            }
            else if (outerRefSrc < 0)
            {
                int srcImportIdx = -outerRefSrc - 1;
                if (srcImportIdx >= 0 && srcImportIdx < srcHeader.ImportTable.Count)
                {
                    string outerPath = Phase2MaterialExtender.ImportFullPath(srcHeader, srcHeader.ImportTable[srcImportIdx]);
                    if (tgtImportByPath.TryGetValue(outerPath, out int existingRef))
                        outerRefOut = existingRef;
                    else if (futureImportRefBySrcImportIdx.TryGetValue(srcImportIdx, out int futureRef))
                        outerRefOut = futureRef;
                    else
                        outerRefOut = 0;
                }
                else outerRefOut = 0;
            }
            else
            {
                // OuterReference > 0 means the outer is a source-side EXPORT.
                // Originally rare (script struct nestings) — but the
                // synthetic-import-for-untransplantable-material flow uses
                // this pattern: the synthetic import for e.g.
                // chbasematerials_v2.chbasematerial_v2-1 has outerRef pointing
                // at SOURCE's chbasematerials_v2 package export. Translate
                // via path lookup against target's export table (the
                // package export almost certainly exists in target — most
                // costumes that share the chbasematerials_v2 namespace
                // re-export the package).
                int srcExportIdx = outerRefSrc - 1;
                if (srcExportIdx >= 0 && srcExportIdx < srcHeader.ExportTable.Count)
                {
                    string outerPath = srcHeader.ExportTable[srcExportIdx].GetPathName();
                    // Search target's export table by path.
                    int found = 0;
                    for (int ti = 0; ti < tgtHeader.ExportTable.Count; ti++)
                    {
                        if (string.Equals(tgtHeader.ExportTable[ti].GetPathName(), outerPath, StringComparison.OrdinalIgnoreCase))
                        { found = ti + 1; break; }
                    }
                    // ...and then among the exports being added in this same
                    // pass, which is where a costume's own inlined package
                    // ends up. See futureExportRefByPath above.
                    if (found == 0) futureExportRefByPath.TryGetValue(outerPath, out found);
                    outerRefOut = found;
                }
                else outerRefOut = 0;
            }

            // PackageName FName: int32 idx + int32 numeric (numeric = 0 default)
            ms.Write(BitConverter.GetBytes(pkgNameIdx), 0, 4);
            ms.Write(BitConverter.GetBytes(pkgNumeric), 0, 4);
            // ClassName FName
            ms.Write(BitConverter.GetBytes(clsNameIdx), 0, 4);
            ms.Write(BitConverter.GetBytes(clsNumeric), 0, 4);
            // OuterReference
            ms.Write(BitConverter.GetBytes(outerRefOut), 0, 4);
            // ObjectName FObject (FName-like: idx + numeric)
            ms.Write(BitConverter.GetBytes(objNameIdx), 0, 4);
            ms.Write(BitConverter.GetBytes(objNumeric), 0, 4);
        }
        return ms.ToArray();
    }

    private static void WriteAddedExportEntries(
        byte[] outBuf,
        int writeCursor,
        List<UnrealExportTableEntry> addedExports,
        List<byte[]> addedExportBodies,
        UnrealHeader srcHeader,
        UnrealHeader tgtHeader,
        Dictionary<string, int> futureNameByText,
        int addedImportsCount)
    {
        // Reuse the same lookups so reference translation is consistent.
        var tgtImportByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgtHeader.ImportTable.Count; i++)
            tgtImportByPath[Phase2MaterialExtender.ImportFullPath(tgtHeader, tgtHeader.ImportTable[i])] = -(i + 1);
        var tgtExportByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgtHeader.ExportTable.Count; i++)
            tgtExportByPath[tgtHeader.ExportTable[i].GetPathName()] = i + 1;
        // Future imports indexed by source import idx.
        var futureImportRefBySrcImportIdx = new Dictionary<int, int>();
        // We need the list of added imports here; reconstruct lookup via
        // index in srcHeader.ImportTable + tgtHeader.ImportTable.Count.
        // Caller has already built futureImportByPath — but we don't have
        // direct access to addedImports here. Build it from srcHeader and
        // the difference set:
        int futureImportCursor = tgtHeader.ImportTable.Count;
        // Iterate src in order and produce same future indices as the
        // upstream caller did. (Same logic as Phase2MaterialExtender.)
        for (int i = 0; i < srcHeader.ImportTable.Count; i++)
        {
            string path = Phase2MaterialExtender.ImportFullPath(srcHeader, srcHeader.ImportTable[i]);
            if (tgtImportByPath.ContainsKey(path)) continue;
            futureImportRefBySrcImportIdx[i] = -(futureImportCursor + 1);
            futureImportCursor++;
        }
        // Future exports by source export idx.
        var futureExportRefBySrcExportIdx = new Dictionary<int, int>();
        for (int i = 0; i < addedExports.Count; i++)
        {
            int srcExportIdx = srcHeader.ExportTable.IndexOf(addedExports[i]);
            if (srcExportIdx >= 0)
                futureExportRefBySrcExportIdx[srcExportIdx] = tgtHeader.ExportTable.Count + i + 1;
        }

        int p = writeCursor;
        for (int i = 0; i < addedExports.Count; i++)
        {
            var e = addedExports[i];

            int classRef = TranslateRef(e.ClassReference, srcHeader, tgtImportByPath, tgtExportByPath,
                futureImportRefBySrcImportIdx, futureExportRefBySrcExportIdx);
            int superRef = TranslateRef(e.SuperReference, srcHeader, tgtImportByPath, tgtExportByPath,
                futureImportRefBySrcImportIdx, futureExportRefBySrcExportIdx);
            int outerRef = TranslateRef(e.OuterReference, srcHeader, tgtImportByPath, tgtExportByPath,
                futureImportRefBySrcImportIdx, futureExportRefBySrcExportIdx);
            int archetypeRef = TranslateRef(e.ArchetypeReference, srcHeader, tgtImportByPath, tgtExportByPath,
                futureImportRefBySrcImportIdx, futureExportRefBySrcExportIdx);

            // FName is (BaseNameIdx, Numeric). For numbered names like
            // "skeletalmeshsocket_1", the BASE name in the name table is just
            // "skeletalmeshsocket" and Numeric=2 encodes the "_1" suffix
            // (Numeric > 0 means actual_number = Numeric - 1).
            //
            // We MUST resolve the base name idx in target and preserve the
            // numeric — looking up the composed name "skeletalmeshsocket_1"
            // in target's name table always fails (target never stores
            // composed numbered names) and ResolveNameIdx then falls back
            // to idx 0, which is whatever name target happens to have first
            // (usually "a"). That produced 43 sockets all named "a" in the
            // earlier output and made every FX socket lookup miss.
            int srcBaseIdx = e.ObjectNameIndex?.Index ?? 0;
            string baseName = (srcBaseIdx >= 0 && srcBaseIdx < srcHeader.NameTable.Count)
                ? srcHeader.NameTable[srcBaseIdx]?.Name?.String ?? string.Empty
                : string.Empty;
            int objNameIdx = ResolveNameIdx(baseName, tgtHeader, futureNameByText);
            int numericExt = e.ObjectNameIndex?.Numeric ?? 0;

            WriteInt32(outBuf, p +  0, classRef);
            WriteInt32(outBuf, p +  4, superRef);
            WriteInt32(outBuf, p +  8, outerRef);
            WriteInt32(outBuf, p + 12, objNameIdx);
            WriteInt32(outBuf, p + 16, numericExt);
            WriteInt32(outBuf, p + 20, archetypeRef);
            WriteUInt64(outBuf, p + 24, e.ObjectFlags);
            WriteInt32(outBuf, p + 32, addedExportBodies[i].Length); // SerialDataSize (final)
            WriteInt32(outBuf, p + 36, 0); // SerialDataOffset — stamped later
            WriteUInt32(outBuf, p + 40, e.ExportFlags);
            WriteInt32(outBuf, p + 44, e.NetObjects.Count);
            int q = p + 48;
            foreach (var n in e.NetObjects)
            {
                // BUG FIX: NetObjects values are FObject refs (positive = export,
                // negative = import, 0 = null), same shape as ClassRef/OuterRef
                // /ArchetypeRef. Writing them raw leaks source-side export indices
                // into the target file — the engine then reads e.g. index 458 in
                // a file that only has 455 exports and aborts with
                // "Bad export index 458/455" during load. Translate through the
                // same lookups the other refs use; fall back to 0 (null) if the
                // ref can't be resolved (better than a dangling index).
                int translatedNetObj = TranslateRef(n, srcHeader, tgtImportByPath, tgtExportByPath,
                    futureImportRefBySrcImportIdx, futureExportRefBySrcExportIdx);
                WriteInt32(outBuf, q, translatedNetObj);
                q += 4;
            }
            // PackageGuid 16 bytes — copy verbatim.
            if (e.PackageGuid != null && e.PackageGuid.Length == 16)
                Buffer.BlockCopy(e.PackageGuid, 0, outBuf, q, 16);
            q += 16;
            WriteUInt32(outBuf, q, e.PackageFlags);
            q += 4;

            int entrySize = 68 + e.NetObjects.Count * 4;
            p += entrySize;
        }
    }

    private static int TranslateRef(
        int srcRef,
        UnrealHeader srcHeader,
        Dictionary<string, int> tgtImportByPath,
        Dictionary<string, int> tgtExportByPath,
        Dictionary<int, int> futureImportRefBySrcImportIdx,
        Dictionary<int, int> futureExportRefBySrcExportIdx)
    {
        if (srcRef == 0) return 0;
        if (srcRef < 0)
        {
            int idx = -srcRef - 1;
            if (idx < 0 || idx >= srcHeader.ImportTable.Count) return 0;
            string path = Phase2MaterialExtender.ImportFullPath(srcHeader, srcHeader.ImportTable[idx]);
            if (tgtImportByPath.TryGetValue(path, out int existing)) return existing;
            if (futureImportRefBySrcImportIdx.TryGetValue(idx, out int future)) return future;
            return 0;
        }
        else
        {
            int idx = srcRef - 1;
            if (idx < 0 || idx >= srcHeader.ExportTable.Count) return 0;
            string path = srcHeader.ExportTable[idx].GetPathName();
            if (tgtExportByPath.TryGetValue(path, out int existing)) return existing;
            if (futureExportRefBySrcExportIdx.TryGetValue(idx, out int future)) return future;
            return 0;
        }
    }

    private static int ResolveNameIdx(string name, UnrealHeader tgtHeader, Dictionary<string, int> futureNameByText)
    {
        for (int i = 0; i < tgtHeader.NameTable.Count; i++)
        {
            string n = tgtHeader.NameTable[i]?.Name?.String ?? string.Empty;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        if (futureNameByText.TryGetValue(name, out int futureIdx)) return futureIdx;
        return 0; // fall back to "None" at index 0
    }

    private static void PatchHeaderFields(
        byte[] outBuf, UnrealHeader tgtHeader,
        int addedNameBytes, int addedImportBytes, int addedExportBytes, int addedDependsBytes,
        int addedNameCount, int addedImportCount, int addedExportCount,
        int newHeaderSize)
    {
        // Patch Size field.
        WriteInt32(outBuf, 8, newHeaderSize);
        // Locate count/offset fields via the same logic UpkRepacker uses.
        int pos = 12;
        int groupSize = ReadInt32(outBuf, pos); pos += 4;
        if (groupSize < 0) pos += -groupSize * 2;
        else if (groupSize > 0) pos += groupSize;
        pos += 4; // Flags

        int nameCountOff   = pos; pos += 4;
        int nameOffsetOff  = pos; pos += 4;
        int exportCountOff = pos; pos += 4;
        int exportOffsetOff= pos; pos += 4;
        int importCountOff = pos; pos += 4;
        int importOffsetOff= pos; pos += 4;
        int dependsOffsetOff = pos; pos += 4;
        int impExpGuidsOff = pos; pos += 4;
        pos += 8; // ImportGuidsCount, ExportGuidsCount
        int thumbnailOff = pos;

        // Update counts.
        WriteInt32(outBuf, nameCountOff,   tgtHeader.NameTable.Count   + addedNameCount);
        WriteInt32(outBuf, importCountOff, tgtHeader.ImportTable.Count + addedImportCount);
        WriteInt32(outBuf, exportCountOff, tgtHeader.ExportTable.Count + addedExportCount);

        // NameOffset doesn't change. ImportOffset shifts by addedNameBytes.
        ShiftIfPositive(outBuf, importOffsetOff, addedNameBytes);
        // ExportOffset shifts by addedNameBytes + addedImportBytes.
        ShiftIfPositive(outBuf, exportOffsetOff, addedNameBytes + addedImportBytes);
        // DependsOffset shifts by names+imports+exports.
        ShiftIfPositive(outBuf, dependsOffsetOff, addedNameBytes + addedImportBytes + addedExportBytes);
        // ImpExpGuidsOffset + ThumbnailOffset shift by the whole header growth
        // (names+imports+exports+depends).
        int totalHeaderGrowth = addedNameBytes + addedImportBytes + addedExportBytes + addedDependsBytes;
        ShiftIfPositive(outBuf, impExpGuidsOff, totalHeaderGrowth);
        ShiftIfPositive(outBuf, thumbnailOff,   totalHeaderGrowth);

        // (No GenerationInfo update. The user-confirmed-working
        // sibling-chassis output leaves Gen[0] stale at the ORIGINAL target
        // counts — and works in-game. Updating Gen[0] to match the new
        // header counts was an evidence-free hypothesis that turned out to
        // be the opposite of what the working baseline does. Leave it alone.)
    }

    private static void RefreshPackageSourceCrc(byte[] outBuf, UnrealHeader tgtHeader)
    {
        // Recompute PackageSource CRC the same way UpkRepacker does. Find the
        // PackageSource field offset by walking past compression-related
        // fields, then re-CRC the rest of the file with that field zeroed.
        // We approximate by locating the same fields used in
        // RefreshPackageSourceCrc(originalBytes, header) — but since header
        // may have changed offsets, we use header.CompressionTableCount and
        // assume zero compressed chunks in our output (we always decompress).
        int pos = 12;
        int groupSize = ReadInt32(outBuf, pos); pos += 4;
        if (groupSize < 0) pos += -groupSize * 2;
        else if (groupSize > 0) pos += groupSize;
        pos += 4; // Flags
        pos += 4 * 11; // 11 int32 fields for counts/offsets
        pos += 16;     // GUID
        int generationCount = ReadInt32(outBuf, pos); pos += 4;
        pos += generationCount * 12;
        pos += 8;      // engine + cooker
        // Compression flags + count
        int compressionFlagsOff = pos; pos += 4;
        int compressionCount = ReadInt32(outBuf, pos); pos += 4;
        pos += compressionCount * 16;
        int packageSourceOff = pos;
        if (packageSourceOff < 0 || packageSourceOff + 4 > outBuf.Length) return;

        // Zero compression flags so the engine doesn't try to decompress us
        // (we wrote uncompressed bytes).
        WriteUInt32(outBuf, compressionFlagsOff, 0);

        byte[] crcInput = (byte[])outBuf.Clone();
        Array.Clear(crcInput, packageSourceOff, 4);
        uint crc = OmegaAssetStudio.CrcUtility.Compute(crcInput);
        WriteUInt32(outBuf, packageSourceOff, crc);
    }

    private static int  ReadInt32 (byte[] b, int o) => BitConverter.ToInt32(b, o);
    private static void WriteInt32(byte[] b, int o, int v)
    {
        b[o    ] = (byte)( v        & 0xFF);
        b[o + 1] = (byte)((v >>  8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }
    private static void WriteUInt32(byte[] b, int o, uint v) => WriteInt32(b, o, unchecked((int)v));
    private static void WriteUInt64(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++) b[o + i] = (byte)((v >> (i * 8)) & 0xFF);
    }
    private static void ShiftIfPositive(byte[] b, int off, int delta)
    {
        int cur = ReadInt32(b, off);
        if (cur > 0) WriteInt32(b, off, cur + delta);
    }
}
