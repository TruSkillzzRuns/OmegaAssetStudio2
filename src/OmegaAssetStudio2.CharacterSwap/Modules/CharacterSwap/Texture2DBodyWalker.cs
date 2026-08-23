using System;
using System.Collections.Generic;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Tables;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Re-emits a Texture2D export body with mips INLINED (no TFC dependency).
//
// Pattern mirrors SkeletalMeshBodyWalker: walk source bytes field-by-field
// at the parser's exact byte boundaries, copy binary primitives verbatim,
// translate FName indices + FObject refs through the IndexTranslator.
//
// The Texture2D-specific work:
//   * The property tag stream's TextureFileCacheName (FName) is rewritten
//     to "None" so the target engine doesn't try to look the texture up
//     in a TFC manifest that won't have it.
//   * The TextureFileCacheGuid field (binary tail, FGuid 16 bytes) is
//     zeroed for the same reason.
//   * Each entry in the Mips array has its bulk data REPLACED with an
//     inline-uncompressed bulk data block carrying the raw pixel bytes
//     supplied by Phase2SourceTextureLoader. Source's bulk data may have
//     pointed at a .tfc file (BulkDataFlags has StoreInSeparatefile set)
//     or been inline-LZO-compressed in source's UPK; either way the
//     output is the same simple inline-uncompressed format that the
//     existing InjectInlineAsync path uses for HUD textures.
//
// Layout (per UTexture2D.ReadBuffer):
//   UObject base: NetIndex int32 + property tag stream ending with "None"
//   Mips: int count + N * FTexture2DMipMap
//     FTexture2DMipMap = bulk data + SizeX int32 + SizeY int32
//   TextureFileCacheGuid: 16 bytes (FGuid)
//   CachedPVRTCMips: int count + N * FTexture2DMipMap (usually empty)
//   CachedFlashMipMaxResolution: int32
//   CachedATITCMips: int count + N * FTexture2DMipMap (usually empty)
//   CachedFlashMipData: bulk data (usually empty/StoreInSeparatefile)
//   CachedETCMips: int count + N * FTexture2DMipMap (usually empty)
public sealed class Texture2DBodyWalker
{
    private readonly byte[] _src;
    private int _srcPos;
    private readonly UpkManager.Helpers.ByteArrayWriter _dst;
    private readonly UnrealHeader _srcHeader;
    private readonly IndexTranslator _translator;
    private readonly Action<string>? _log;
    private readonly IReadOnlyList<Phase2SourceTextureLoader.MipBytes>? _replacementMips;

    public int NameRefsRewritten { get; private set; }
    public int ObjectRefsRewritten { get; private set; }
    public int NameRefsFailedTranslation { get; private set; }
    public int ObjectRefsFailedTranslation { get; private set; }
    public int MipsInlined { get; private set; }
    public List<string> Issues { get; } = new();
    public int BytesConsumed => _srcPos;

    // UE3 reads inline mip payloads from an ABSOLUTE file offset stored in
    // each mip's bulk-data header (CompressedOffset). We don't know the
    // absolute offset until Phase2TableExtender places this body in the
    // output file, so we record (offsetFieldOffsetInBody, payloadStartInBody)
    // here and the extender stamps the absolute offset after layout.
    public List<(int OffsetFieldInBody, int PayloadStartInBody)> BulkPatches { get; } = new();

    public Texture2DBodyWalker(
        byte[] srcBody,
        UnrealHeader srcHeader,
        IndexTranslator translator,
        IReadOnlyList<Phase2SourceTextureLoader.MipBytes>? replacementMips,
        Action<string>? log)
    {
        _src = srcBody;
        _srcPos = 0;
        // Output may be larger than source if mip bytes were TFC-stored
        // (source UPK had only ~16-byte header per mip, output will have
        // header + full payload). Allocate with margin.
        int margin = 0;
        if (replacementMips != null)
            foreach (var m in replacementMips) margin += m.Data.Length + 16;
        _dst = ByteArrayWriter.CreateNew(srcBody.Length + margin + 1024);
        _srcHeader = srcHeader;
        _translator = translator;
        _log = log;
        _replacementMips = replacementMips;
    }

    public byte[] GetBytes()
    {
        byte[] all = _dst.GetBytes();
        if (_dst.Index < all.Length)
        {
            byte[] trimmed = new byte[_dst.Index];
            Buffer.BlockCopy(all, 0, trimmed, 0, _dst.Index);
            return trimmed;
        }
        return all;
    }

    public void WalkTexture2DBody()
    {
        // NetIndex -> INDEX_NONE
        _ = BitConverter.ToInt32(_src, _srcPos);
        _dst.WriteInt32(-1);
        _srcPos += 4;

        // Property tag stream (UObject). TextureFileCacheName tag is rewritten to "None"
        // so the engine doesn't try to load from TFC.
        WalkPropertyStream("body");

        // After property stream we EMIT A CANONICAL binary tail and ignore
        // source's remaining bytes entirely. This avoids fragile parsing of
        // source's bulk-data chunk headers (SourceArt, CachedFlash etc.) and
        // produces the minimum valid Texture2D body the engine needs to
        // accept the texture:
        //   - SourceArt bulk data: empty header with StoreInSeparatefile flag
        //     (engine treats as "no source art available", which is fine for
        //     a cooked texture — SourceArt is editor-only data)
        //   - Mips array: count + N inline-uncompressed mips
        //   - TextureFileCacheGuid: 16 zero bytes (no TFC association)
        //   - All Cached*Mips arrays: count=0
        //   - CachedFlashMipData: empty bulk data
        EmitEmptyBulkData();                  // SourceArt
        // Mips
        int outMipCount = _replacementMips?.Count ?? 0;
        _dst.WriteInt32(outMipCount);
        for (int i = 0; i < outMipCount; i++)
            EmitInlineMip(_replacementMips![i]);
        // TextureFileCacheGuid: 16 zero bytes
        for (int i = 0; i < 16; i++) _dst.WriteByte(0);
        _dst.WriteInt32(0);                   // CachedPVRTCMips count
        _dst.WriteInt32(0);                   // CachedFlashMipMaxResolution
        _dst.WriteInt32(0);                   // CachedATITCMips count
        EmitEmptyBulkData();                  // CachedFlashMipData
        _dst.WriteInt32(0);                   // CachedETCMips count
        // We deliberately do NOT advance _srcPos further; the source's
        // binary-tail bytes after the property stream are discarded.
        // Set _srcPos to source length to satisfy the walker's "consumed
        // all bytes" sanity check at the caller.
        _srcPos = _src.Length;
    }

    private void EmitEmptyBulkData()
    {
        // BulkDataFlags = StoreInSeparatefile (0x01) — engine treats this
        // as "no payload here", which is the safest empty sentinel.
        _dst.WriteUInt32(0x01);
        _dst.WriteInt32(0);                   // UncompressedSize
        _dst.WriteInt32(-1);                  // CompressedSize (UE3 idiom: -1)
        _dst.WriteInt32(-1);                  // CompressedOffset
    }

    private void WalkPropertyStream(string ctx)
    {
        while (true)
        {
            int tagNameIdx = BitConverter.ToInt32(_src, _srcPos);
            string tagName = ResolveSourceName(tagNameIdx);
            TranslateName($"{ctx}/tag-name");

            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase))
                return;

            int typeNameIdx = BitConverter.ToInt32(_src, _srcPos);
            string typeName = ResolveSourceName(typeNameIdx);
            TranslateName($"{ctx}/{tagName}/type-name");

            int valueSize = CopyInt32();
            CopyInt32(); // arrayIdx

            string? innerName = null;
            if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                int innerNameIdx = BitConverter.ToInt32(_src, _srcPos);
                innerName = ResolveSourceName(innerNameIdx);
                TranslateName($"{ctx}/{tagName}/inner-name");
            }
            else if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                CopyByte();
                continue;
            }

            // SPECIAL: TextureFileCacheName -> rewrite to FName "None" so engine
            // skips the TFC lookup entirely.
            if (string.Equals(tagName, "TextureFileCacheName", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeName, "NameProperty", StringComparison.OrdinalIgnoreCase)
                && valueSize == 8)
            {
                _srcPos += 8;
                int noneIdx = FindNameInTarget("None");
                _dst.WriteInt32(noneIdx);
                _dst.WriteInt32(0);
                _log?.Invoke($"  Texture2D: TextureFileCacheName -> 'None' (cleared TFC link, target name idx {noneIdx})");
                continue;
            }

            // SPECIAL: FirstResourceMemMip -> 0.
            // Source's value (e.g. 4) means "the first 4 mips are streamed from
            // TFC, the rest are inline-resident". We've cleared TFC and inlined
            // a smaller set of mips (typically 4: 1024..128 px). If we keep
            // FirstResourceMemMip=4, the engine treats our 4 inlined mips as
            // mip indices 4..7 (the SMALLEST mips) and tries to stream mips 0..3
            // from a TFC that's no longer linked → falls back to garbage / wrong
            // pixel data → cape renders white-with-streaks instead of red.
            // Forcing 0 tells the engine "all mips in this bulk array start at
            // mip 0 (largest)" — which matches what we actually wrote.
            if (string.Equals(tagName, "FirstResourceMemMip", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeName, "IntProperty", StringComparison.OrdinalIgnoreCase)
                && valueSize == 4)
            {
                int srcVal = BitConverter.ToInt32(_src, _srcPos);
                _srcPos += 4;
                _dst.WriteInt32(0);
                _log?.Invoke($"  Texture2D: FirstResourceMemMip {srcVal} -> 0 (all inlined mips are largest-first now)");
                continue;
            }

            // SPECIAL: MipTailBaseIdx -> (inlined mip count - 1).
            // Source's value (e.g. 10) refers to source's 11-mip chain. With
            // only 4 inlined mips in target, the mip-tail base must point at
            // the last individually-addressable mip we actually wrote (index 3
            // for 4 mips), otherwise the engine's tail-packing math reads past
            // the end of the bulk array and crashes/garbles.
            if (string.Equals(tagName, "MipTailBaseIdx", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeName, "IntProperty", StringComparison.OrdinalIgnoreCase)
                && valueSize == 4)
            {
                int srcVal = BitConverter.ToInt32(_src, _srcPos);
                _srcPos += 4;
                int newVal = Math.Max(0, (_replacementMips?.Count ?? 1) - 1);
                _dst.WriteInt32(newVal);
                _log?.Invoke($"  Texture2D: MipTailBaseIdx {srcVal} -> {newVal} (matches actual inlined mip count)");
                continue;
            }

            if (string.Equals(typeName, "ObjectProperty",    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ClassProperty",     StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ComponentProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "InterfaceProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (valueSize == 4) TranslateObject($"{ctx}/{tagName}");
                else CopyBytes(valueSize);
            }
            else if (string.Equals(typeName, "NameProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (valueSize == 8) TranslateName($"{ctx}/{tagName}");
                else CopyBytes(valueSize);
            }
            else if (string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                // ByteProperty value is either:
                //   - 1 byte raw byte (valueSize == 1)
                //   - 8-byte FName (enum value, valueSize == 8) — MUST be
                //     translated to target's name index, e.g. source's
                //     "PF_DXT5" idx -> target's "PF_DXT5" idx. Skipping the
                //     translation here is what makes the renderer crash on
                //     transplanted textures: Format reads as a garbage enum,
                //     engine treats mip bytes as the wrong DXT variant,
                //     dxgi decode walks off the buffer.
                if (valueSize == 8) TranslateName($"{ctx}/{tagName}/enum-value");
                else CopyBytes(valueSize);
            }
            else
            {
                // Int/Float/Struct etc. — copy value blob verbatim.
                CopyBytes(valueSize);
            }
            _ = innerName;
        }
    }

    // Advances _srcPos past one source mip entry without writing to dst.
    private void SkipSourceMip(int idx)
    {
        // Bulk data header: 16 bytes (BulkDataFlags + UncompressedSize + CompressedSize + CompressedOffset)
        uint flags = BitConverter.ToUInt32(_src, _srcPos);
        int unc = BitConverter.ToInt32(_src, _srcPos + 4);
        int comp = BitConverter.ToInt32(_src, _srcPos + 8);
        _srcPos += 16;
        const uint Unused = 0x20;
        const uint StoreInSepFile = 0x01;
        if ((flags & (Unused | StoreInSepFile)) == 0 && comp > 0)
        {
            // Inline-stored payload follows the header.
            _srcPos += comp;
        }
        // SizeX + SizeY (always present even for TFC-stored mips)
        _srcPos += 8;
        _ = idx; _ = unc;
    }

    // Writes one mip as inline-uncompressed bulk data + SizeX + SizeY.
    // Mirrors TexturePreviewInjector.InjectInlineAsync's mip layout.
    private void EmitInlineMip(Phase2SourceTextureLoader.MipBytes mip)
    {
        _dst.WriteUInt32(0);                        // BulkDataFlags = 0 (uncompressed inline)
        _dst.WriteInt32(mip.Data.Length);           // UncompressedSize
        _dst.WriteInt32(mip.Data.Length);           // CompressedSize (same — uncompressed)
        int offsetFieldPos = _dst.Index;
        _dst.WriteInt32(0);                         // CompressedOffset placeholder — patched by extender
        int payloadStart = _dst.Index;
        foreach (byte b in mip.Data) _dst.WriteByte(b);
        BulkPatches.Add((offsetFieldPos, payloadStart));
        _dst.WriteInt32(mip.SizeX);
        _dst.WriteInt32(mip.SizeY);
        MipsInlined++;
    }

    // Mirrors source's mip array bytes verbatim (used for PVRTC / ATITC / ETC
    // arrays that we don't transplant payload for — they're typically empty).
    private void WalkMipArrayVerbatim(string ctx)
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
        {
            CopyUInt32();                                 // BulkDataFlags
            int unc = CopyInt32();                        // UncompressedSize
            int comp = CopyInt32();                       // CompressedSize
            CopyInt32();                                  // CompressedOffset
            const uint Unused = 0x20;
            const uint StoreInSepFile = 0x01;
            uint flags = BitConverter.ToUInt32(_dst.GetBytes(), _dst.Index - 16);
            if ((flags & (Unused | StoreInSepFile)) == 0 && comp > 0)
                CopyBytes(comp);
            CopyInt32(); // SizeX
            CopyInt32(); // SizeY
            _ = unc; _ = ctx;
        }
    }

    private void WalkBulkDataVerbatim(string ctx)
    {
        uint flags = CopyUInt32();
        CopyInt32(); // UncompressedSize
        int comp = CopyInt32();
        CopyInt32(); // CompressedOffset
        const uint Unused = 0x20;
        const uint StoreInSepFile = 0x01;
        if ((flags & (Unused | StoreInSepFile)) == 0 && comp > 0)
            CopyBytes(comp);
        _ = ctx;
    }

    // ---- Primitives ----
    private byte CopyByte()
    {
        byte v = _src[_srcPos]; _dst.WriteByte(v); _srcPos++; return v;
    }
    private int CopyInt32()
    {
        int v = BitConverter.ToInt32(_src, _srcPos); _dst.WriteInt32(v); _srcPos += 4; return v;
    }
    private uint CopyUInt32()
    {
        uint v = BitConverter.ToUInt32(_src, _srcPos); _dst.WriteUInt32(v); _srcPos += 4; return v;
    }
    private void CopyBytes(int n)
    {
        for (int i = 0; i < n; i++) _dst.WriteByte(_src[_srcPos + i]);
        _srcPos += n;
    }

    private void TranslateName(string ctx)
    {
        int srcIdx = BitConverter.ToInt32(_src, _srcPos);
        int srcNumeric = BitConverter.ToInt32(_src, _srcPos + 4);
        int tgtIdx;
        if (srcIdx < 0 || srcIdx >= _srcHeader.NameTable.Count)
        {
            tgtIdx = srcIdx;
            NameRefsFailedTranslation++;
            Issues.Add($"{ctx}: FName srcIdx={srcIdx} out of bounds; kept as-is");
        }
        else
        {
            tgtIdx = _translator.TranslateNameIndex(srcIdx);
            if (tgtIdx < 0)
            {
                tgtIdx = srcIdx;
                NameRefsFailedTranslation++;
                Issues.Add($"{ctx}: FName '{_srcHeader.NameTable[srcIdx]?.Name?.String}' no target equiv; kept source idx {srcIdx}");
            }
            else
            {
                NameRefsRewritten++;
            }
        }
        _dst.WriteInt32(tgtIdx);
        _dst.WriteInt32(srcNumeric);
        _srcPos += 8;
    }

    private void TranslateObject(string ctx)
    {
        int srcRef = BitConverter.ToInt32(_src, _srcPos);
        int tgtRef;
        if (srcRef == 0)
        {
            tgtRef = 0;
            ObjectRefsRewritten++;
        }
        else
        {
            tgtRef = _translator.TranslateObjectReference(srcRef);
            if (tgtRef == 0)
            {
                ObjectRefsFailedTranslation++;
                Issues.Add($"{ctx}: FObject srcRef={srcRef} no target equiv; wrote null");
            }
            else
            {
                ObjectRefsRewritten++;
            }
        }
        _dst.WriteInt32(tgtRef);
        _srcPos += 4;
    }

    private string ResolveSourceName(int idx)
    {
        if (idx < 0 || idx >= _srcHeader.NameTable.Count) return $"(bad#{idx})";
        return _srcHeader.NameTable[idx]?.Name?.String ?? "(null)";
    }

    private int FindNameInTarget(string nm)
    {
        for (int i = 0; i < _translator.Target.NameTable.Count; i++)
        {
            if (string.Equals(_translator.Target.NameTable[i]?.Name?.String, nm, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }
}
