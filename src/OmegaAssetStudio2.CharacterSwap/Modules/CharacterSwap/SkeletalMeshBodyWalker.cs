using System;
using System.Collections.Generic;
using UpkManager.Helpers;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Translating byte-stream walker for SkeletalMesh export bodies.
//
// Mirrors UpkManager's USkeletalMesh.ReadBuffer field-by-field but with one
// critical difference: at every read site it ALSO writes bytes to an output
// buffer, translating FName indices and FObject references through the
// supplied IndexTranslator. Binary primitives (int, float, byte arrays,
// vertex buffers, bulk data) are copied verbatim because they contain no
// cross-package references.
//
// Why a walker and not the existing FName/FObject recorder hooks alone?
// UpkManager parses some array sub-elements opaquely (no engine schema for
// the inner struct type), so reads inside those arrays bypass the recorder
// entirely. Worse, the v894 source body and the v868 target engine may
// disagree on field layout in subtle ways (a v894-only field, a moved
// member). A position-tracking walker that mirrors UpkManager's parse path
// closely enough to reach the same field boundaries can re-emit the body
// with every cross-package reference properly translated, while preserving
// the binary mesh data (vertex/index/bone buffers) exactly as source had it.
//
// This walker is NOT a full v894 -> v868 layout converter. It assumes the
// two formats are byte-compatible (which UpkManager's 100% byte coverage on
// both implies). If a future test reveals an actual layout drift, the fix
// is to insert a version branch at the offending field — the framework here
// makes that straightforward.
public sealed class SkeletalMeshBodyWalker
{
    private readonly byte[] _src;
    private int _srcPos;
    private readonly ByteArrayWriter _dst;
    private readonly UnrealHeader _srcHeader;
    private readonly IndexTranslator _translator;
    private readonly Action<string>? _log;

    public int NameRefsRewritten { get; private set; }
    public int ObjectRefsRewritten { get; private set; }
    public int NameRefsFailedTranslation { get; private set; }
    public int ObjectRefsFailedTranslation { get; private set; }
    public List<string> Issues { get; } = new();
    public int BytesConsumed => _srcPos;

    // Optional: when set, the WalkMaterialsArray path looks up each source
    // ref in this map (source UE3 FObject ref → target UE3 FObject ref). If
    // a Materials[i] source ref is in the map, the mapped target ref is
    // written. Otherwise the standard IndexTranslator path is used (which
    // typically returns 0/null for source-only parent UMaterials we didn't
    // transplant). This keeps mesh sections wired to the CORRECT picked MIC
    // (per source ref) rather than cycling MICs positionally, which
    // mis-matches slots when picked-MIC count != mesh.Materials count.
    public IReadOnlyDictionary<int, int>? OverrideMaterialsSrcToTgt { get; set; }

    public SkeletalMeshBodyWalker(byte[] srcBody, UnrealHeader srcHeader, IndexTranslator translator, Action<string>? log)
    {
        _src = srcBody;
        _srcPos = 0;
        _dst = ByteArrayWriter.CreateNew(srcBody.Length);
        _srcHeader = srcHeader;
        _translator = translator;
        _log = log;
    }

    public byte[] GetBytes()
    {
        byte[] all = _dst.GetBytes();
        // ByteArrayWriter may have over-allocated; trim to actual write index.
        if (_dst.Index < all.Length)
        {
            byte[] trimmed = new byte[_dst.Index];
            Buffer.BlockCopy(all, 0, trimmed, 0, _dst.Index);
            return trimmed;
        }
        return all;
    }

    // ---- Primitive cursor moves: read source, write same bytes to dst ----
    private byte CopyByte()
    {
        byte v = _src[_srcPos];
        _dst.WriteByte(v);
        _srcPos++;
        return v;
    }
    private short CopyInt16()
    {
        short v = BitConverter.ToInt16(_src, _srcPos);
        _dst.WriteInt16(v);
        _srcPos += 2;
        return v;
    }
    private ushort CopyUInt16()
    {
        ushort v = BitConverter.ToUInt16(_src, _srcPos);
        _dst.WriteUInt16(v);
        _srcPos += 2;
        return v;
    }
    private int CopyInt32()
    {
        int v = BitConverter.ToInt32(_src, _srcPos);
        _dst.WriteInt32(v);
        _srcPos += 4;
        return v;
    }
    private uint CopyUInt32()
    {
        uint v = BitConverter.ToUInt32(_src, _srcPos);
        _dst.WriteUInt32(v);
        _srcPos += 4;
        return v;
    }
    private float CopyFloat()
    {
        float v = BitConverter.ToSingle(_src, _srcPos);
        _dst.WriteSingle(v);
        _srcPos += 4;
        return v;
    }
    private void CopyBytes(int n)
    {
        for (int i = 0; i < n; i++) _dst.WriteByte(_src[_srcPos + i]);
        _srcPos += n;
    }

    // ---- Translating field reads ----
    // Reads an FName (idx int32 + numeric int32) at the current source cursor,
    // translates the index via the translator, writes (translatedIdx, numeric)
    // to dst, and advances source cursor by 8.
    private void TranslateName(string ctx)
    {
        int srcIdx = BitConverter.ToInt32(_src, _srcPos);
        int srcNumeric = BitConverter.ToInt32(_src, _srcPos + 4);
        int tgtIdx;
        if (srcIdx < 0 || srcIdx >= _srcHeader.NameTable.Count)
        {
            // Garbage — keep as-is (will likely be wrong on target side too,
            // but at least we don't crash here).
            tgtIdx = srcIdx;
            NameRefsFailedTranslation++;
            Issues.Add($"{ctx}: FName srcIdx={srcIdx} out of source NameTable bounds; kept as-is");
        }
        else
        {
            tgtIdx = _translator.TranslateNameIndex(srcIdx);
            if (tgtIdx < 0)
            {
                // No equivalent in target — keep source's index (safer than 0
                // which would resolve to a real name). Engine may show a
                // wrong name string but won't crash on bounds.
                tgtIdx = srcIdx;
                NameRefsFailedTranslation++;
                Issues.Add($"{ctx}: FName '{_srcHeader.NameTable[srcIdx]?.Name?.String}' has no target equivalent; kept source idx {srcIdx}");
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

    // Reads an FObject (int32 ref) at the current source cursor, translates,
    // writes translated ref to dst, advances cursor by 4.
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
                Issues.Add($"{ctx}: FObject srcRef={srcRef} has no target equivalent; wrote null");
            }
            else
            {
                ObjectRefsRewritten++;
            }
        }
        _dst.WriteInt32(tgtRef);
        _srcPos += 4;
    }

    // ---- High-level walker entry ----
    // Walks a SkeletalMesh body from offset 0 to end. Mirrors USkeletalMesh.ReadBuffer.
    public void WalkSkeletalMeshBody()
    {
        // NetIndex (int32) — UE3 INDEX_NONE for transplanted objects.
        // We DON'T copy source's NetIndex; we emit -1 directly.
        int srcNetIndex = BitConverter.ToInt32(_src, _srcPos);
        _ = srcNetIndex;
        _dst.WriteInt32(-1);
        _srcPos += 4;

        // Property tag stream — terminated by "None" tag.
        WalkPropertyStream("body");

        // ---- Binary tail (mirrors USkeletalMesh.ReadBuffer after base) ----
        WalkFBoxSphereBounds();                       // 28 bytes binary
        WalkMaterialsArray();                          // FObject[] with optional override
        WalkFVector();                                // Origin, 12 bytes
        WalkFRotator();                               // RotOrigin, 12 bytes
        WalkRefSkeleton();                            // FMeshBone[] with FName per bone
        CopyInt32();                                  // SkeletalDepth
        WalkLODModels();                              // FStaticLODModel[]
        WalkNameIndexMap();                           // UMap<FName,int>
        WalkPerPolyBoneKDOPs();                       // FPerPolyBoneCollisionData[]
        WalkStringArray("BoneBreakNames");            // string[]
        WalkByteArray();                              // BoneBreakOptions byte[]
        WalkObjectArray("ClothingAssets");            // FObject[]
        WalkFloatArray();                             // CachedStreamingTextureFactors
        WalkSourceData();                             // FSkeletalMeshSourceData
    }

    // ---- Property tag stream ----
    private void WalkPropertyStream(string ctx)
    {
        while (true)
        {
            // FName tagName
            int tagNameIdx = BitConverter.ToInt32(_src, _srcPos);
            string tagName = ResolveSourceName(tagNameIdx);
            TranslateName($"{ctx}/tag-name");

            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase))
                return;

            // FName typeName
            int typeNameIdx = BitConverter.ToInt32(_src, _srcPos);
            string typeName = ResolveSourceName(typeNameIdx);
            TranslateName($"{ctx}/{tagName}/type-name");

            int valueSize = CopyInt32();
            CopyInt32(); // arrayIdx

            // Type-specific extras BEFORE the value blob.
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

            // Value blob: translate refs based on type.
            int valueEnd = _srcPos + valueSize;
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
            else if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase))
            {
                // If the struct is a known atomic binary type, copy verbatim.
                // Otherwise probe: if the first 4 bytes look like a valid
                // source name index, walk it as a nested property stream.
                if (IsAtomicStruct(innerName))
                {
                    CopyBytes(valueSize);
                }
                else if (valueSize >= 8 && LooksLikeNameIdx(_srcPos))
                {
                    int subStartSrc = _srcPos;
                    int subStartDst = _dst.Index;
                    WalkPropertyStream($"{ctx}/{tagName}[{innerName}]");
                    int consumed = _srcPos - subStartSrc;
                    if (consumed == valueSize)
                    {
                        // Clean nested walk.
                    }
                    else if (consumed < valueSize)
                    {
                        // Under-consumed: pad remaining bytes verbatim.
                        CopyBytes(valueSize - consumed);
                    }
                    else
                    {
                        // OVER-consumed — nested walk ran past valueSize, which
                        // would desync the output from the engine's tag-driven
                        // parse. Roll back both cursors and copy the value
                        // blob verbatim (losing translation for any embedded
                        // refs inside this struct, but keeping the body
                        // structurally consistent so the engine reads the
                        // following fields at the correct offsets).
                        _srcPos = subStartSrc;
                        _dst.Seek(subStartDst);
                        Issues.Add($"{ctx}/{tagName}[{innerName}]: nested walk over-consumed ({consumed}/{valueSize}); rolled back and copied verbatim");
                        CopyBytes(valueSize);
                    }
                }
                else
                {
                    CopyBytes(valueSize);
                }
            }
            else if (string.Equals(typeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase))
            {
                // ArrayProperty value = int32 count + N * element.
                // The element format isn't recorded in the body — it lives
                // in the UProperty schema. We use an explicit per-tag-name
                // schema lookup for known SkeletalMesh array properties.
                // Unknown arrays fall back to verbatim (safe: source bytes
                // are correct for the array's inner type, just won't have
                // refs translated — which is fine if the array holds only
                // primitives like Int / Float).
                if (valueSize < 4)
                {
                    CopyBytes(valueSize);
                }
                else
                {
                    bool zeroAnimSets =
                        string.Equals(tagName, "animsets", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tagName, "anim_sets", StringComparison.OrdinalIgnoreCase);
                    int count;
                    int remaining;
                    if (zeroAnimSets)
                    {
                        // Cross-version transplant: the source SkeletalMesh's AnimSets
                        // array holds object refs pointing at a UAnimSet whose UPK
                        // exists in source's game version but either (a) does not
                        // exist in target's, or (b) does exist but in a different
                        // binary format. Translating these refs lands the engine on
                        // an invalid anim package and trips appError("unknown or
                        // unsupported animation format") at world entry.
                        //
                        // Force the new mesh to fall back to the SkeletalMeshComponent's
                        // AnimSets list (which Phase 2 keeps target-side via the
                        // ForceSameSizeMatchedTranslateClasses path) by writing
                        // count=0 here. The byte budget for the ArrayProperty's
                        // valueSize is preserved with zero padding so the engine's
                        // tag-driven parser reads the next tag at the correct offset.
                        int srcCount = BitConverter.ToInt32(_src, _srcPos);
                        _srcPos += 4;
                        _dst.WriteInt32(0);
                        int origElementBytes = (srcCount > 0 ? srcCount : 0) * 4;
                        if (origElementBytes > 0)
                        {
                            _srcPos += origElementBytes;
                            for (int i = 0; i < origElementBytes; i++) _dst.WriteByte(0);
                        }
                        Issues.Add($"{ctx}/{tagName}: zeroed AnimSets array (was {srcCount} refs) to avoid cross-version anim-format crash");
                        count = 0;
                        remaining = valueSize - 4 - origElementBytes;
                        if (remaining > 0) CopyBytes(remaining);
                        continue;
                    }
                    count = CopyInt32();
                    remaining = valueSize - 4;
                    var arrayKind = GetArrayElementKind(tagName);
                    if (arrayKind == ArrayElementKind.FObject && count > 0)
                    {
                        // Each element is a 4-byte FObject ref. Translate.
                        for (int i = 0; i < count; i++)
                            TranslateObject($"{ctx}/{tagName}[{i}]");
                        int consumed = count * 4;
                        if (consumed < remaining) CopyBytes(remaining - consumed);
                    }
                    else if (arrayKind == ArrayElementKind.TaggedStruct && count > 0)
                    {
                        // Each element is a nested property stream ending with "None".
                        int beforeWalkSrc = _srcPos;
                        int beforeWalkDst = _dst.Index;
                        for (int i = 0; i < count; i++)
                            WalkPropertyStream($"{ctx}/{tagName}[{i}]");
                        int consumed = _srcPos - beforeWalkSrc;
                        if (consumed == remaining)
                        {
                            // Clean.
                        }
                        else if (consumed < remaining)
                        {
                            CopyBytes(remaining - consumed);
                        }
                        else
                        {
                            // Over-consumed — same desync risk as the nested
                            // StructProperty path. Roll back and copy verbatim.
                            _srcPos = beforeWalkSrc;
                            _dst.Seek(beforeWalkDst);
                            Issues.Add($"{ctx}/{tagName}: tagged-struct array walk over-consumed ({consumed}/{remaining}); rolled back and copied verbatim");
                            CopyBytes(remaining);
                        }
                    }
                    else
                    {
                        // Unknown / primitive array — copy verbatim.
                        CopyBytes(remaining);
                    }
                }
            }
            else
            {
                // Int/Float/Str/Map etc. — no embedded refs we need to translate.
                CopyBytes(valueSize);
            }
            _ = valueEnd; // (kept for future bounds-check assertions)
        }
    }

    // ---- Binary-tail field walkers ----
    private void WalkFBoxSphereBounds()
    {
        // FVector Origin(12) + FVector BoxExtent(12) + float SphereRadius(4)
        CopyBytes(28);
    }
    private void WalkFVector() => CopyBytes(12);
    private void WalkFRotator() => CopyBytes(12); // 3 ints

    private void WalkObjectArray(string ctx)
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
            TranslateObject($"{ctx}[{i}]");
    }

    // Materials-specific walk. If OverrideMaterialsTargetRefs is set,
    // writes the override values instead of the (likely-null) translated
    // source refs. Source mesh.Materials typically points at base parent
    // UMaterials that we don't transplant (only their MIC children are
    // picked), so naive translation null-outs them and the mesh renders
    // with the engine's debug material. Override list is the list of
    // picked source MICs' target-side export indices; we cycle through
    // them to fill mesh slots in declaration order.
    //
    // Source's int32 count is preserved (we don't reshape the array).
    // If the override list is shorter than count, we cycle. If longer,
    // we truncate. If null/empty, behaves identically to WalkObjectArray.
    private void WalkMaterialsArray()
    {
        int count = CopyInt32();
        var map = OverrideMaterialsSrcToTgt;
        if (map is null || map.Count == 0)
        {
            for (int i = 0; i < count; i++)
                TranslateObject($"Materials[{i}]");
            return;
        }
        for (int i = 0; i < count; i++)
        {
            int srcRef = BitConverter.ToInt32(_src, _srcPos);
            if (map.TryGetValue(srcRef, out int chosen))
            {
                _dst.WriteInt32(chosen);
                _srcPos += 4;
                ObjectRefsRewritten++;
                Issues.Add($"Materials[{i}]: src ref {srcRef} mapped to picked-MIC target ref {chosen}");
            }
            else
            {
                // Source ref isn't a picked MIC — fall back to standard
                // translation (likely null for un-transplanted parents).
                TranslateObject($"Materials[{i}]");
                Issues.Add($"Materials[{i}]: src ref {srcRef} not in MIC map; used IndexTranslator fallback");
            }
        }
    }

    private void WalkRefSkeleton()
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
        {
            TranslateName($"RefSkeleton[{i}].Name");        // FName Name
            CopyUInt32();                                    // Flags
            CopyBytes(28);                                   // VJointPos = FQuat(16) + FVector(12)
            CopyInt32();                                     // NumChildren
            CopyInt32();                                     // ParentIndex
            CopyBytes(4);                                    // FColor BoneColor
        }
    }

    // FStaticLODModel — complex. Walks each LOD's sections, chunks, vertex
    // buffers, index buffers, etc. None of these contain FNames/FObjects, so
    // we copy them verbatim. The complexity is computing per-LOD byte lengths
    // to know what to copy. We mirror FStaticLODModel.ReadData directly.
    private void WalkLODModels()
    {
        // Need access to bHasVertexColors which is a property of the mesh —
        // not available at this level without a context object. We detect it
        // structurally by trying both ColorBuffer-present and absent layouts
        // is impractical. Instead, we use a heuristic: search the parsed
        // mesh's property stream we just emitted for bHasVertexColors=1, but
        // we don't carry that state. As a workable fallback, we use the
        // value of bHasVertexColors carried on the walker context if set.
        WalkLODModelsWithFlag(_bHasVertexColors);
    }

    private bool _bHasVertexColors;
    // Set by SkeletalMeshReferenceTranslator from the parsed USkeletalMesh
    // before invoking the walker. UpkManager already parsed source body and
    // produced the USkeletalMesh object; we just need its bHasVertexColors.
    public void SetMeshContext(bool bHasVertexColors)
    {
        _bHasVertexColors = bHasVertexColors;
    }

    private void WalkLODModelsWithFlag(bool hasVertexColors)
    {
        int lodCount = CopyInt32();
        for (int i = 0; i < lodCount; i++)
            WalkStaticLODModel(hasVertexColors, i);
    }

    private void WalkStaticLODModel(bool hasVertexColors, int lodIdx)
    {
        // Sections: count + N * (uint16 + uint16 + uint32 + uint32 + byte) = 13 bytes per section
        int sectionCount = CopyInt32();
        for (int s = 0; s < sectionCount; s++)
            CopyBytes(13);

        // MultiSizeIndexContainer: bool(4) + byte(1) + bulk array
        WalkMultiSizeIndexContainer($"LOD{lodIdx}.IndexBuffer");

        // ActiveBoneIndices: count + N * uint16
        int abCount = CopyInt32();
        CopyBytes(abCount * 2);

        // Chunks: count + N * FSkelMeshChunk (variable)
        int chunkCount = CopyInt32();
        for (int c = 0; c < chunkCount; c++) WalkSkelMeshChunk();

        CopyUInt32(); // Size
        CopyUInt32(); // NumVertices

        // RequiredBones: int count + N bytes
        int rbCount = CopyInt32();
        CopyBytes(rbCount);

        // RawPointIndices: bulk data (FIntBulkData)
        WalkBulkData($"LOD{lodIdx}.RawPointIndices");

        CopyUInt32(); // NumTexCoords

        // VertexBufferGPUSkin
        WalkVertexBufferGPUSkin($"LOD{lodIdx}.VertexBufferGPUSkin");

        if (hasVertexColors)
            WalkColorVertexBuffer($"LOD{lodIdx}.ColorVertexBuffer");

        // VertexInfluences: count + ...
        int viCount = CopyInt32();
        for (int v = 0; v < viCount; v++) WalkVertexInfluences();

        // AdjacencyMultiSizeIndexContainer
        WalkMultiSizeIndexContainer($"LOD{lodIdx}.AdjacencyIndexBuffer");
    }

    private void WalkSkelMeshChunk()
    {
        CopyUInt32(); // BaseVertexIndex
        // RigidVertices: count + N * FRigidSkinVertex
        int rvCount = CopyInt32();
        // FRigidSkinVertex = FVector(12) + 3 FPackedNormal(4 each) + 4 FVector2D(8 each) + FColor(4) + byte(1) = 12+12+32+4+1 = 61 bytes
        CopyBytes(rvCount * 61);
        // SoftVertices: count + N * FSoftSkinVertex
        int svCount = CopyInt32();
        // FSoftSkinVertex = FVector(12) + 3 FPackedNormal(4) + 4 FVector2D(8) + FColor(4) + 4 bytes bones + 4 bytes weights = 12+12+32+4+4+4 = 68 bytes
        CopyBytes(svCount * 68);
        // BoneMap: count + N * uint16
        int bmCount = CopyInt32();
        CopyBytes(bmCount * 2);
        CopyInt32(); // NumRigidVertices
        CopyInt32(); // NumSoftVertices
        CopyInt32(); // MaxBoneInfluences
    }

    private void WalkVertexBufferGPUSkin(string ctx)
    {
        CopyUInt32();           // NumTexCoords
        uint useFullPrecUVs = CopyUInt32(); // bUseFullPrecisionUVs (stored as int32)
        CopyUInt32();           // bUsePackedPosition (header field — actual flag is inferred from element size)
        CopyBytes(12);          // FVector MeshExtension
        CopyBytes(12);          // FVector MeshOrigin
        int serializedElementSize = CopyInt32();
        int vertexCount = CopyInt32();
        CopyBytes(serializedElementSize * vertexCount);
        _ = useFullPrecUVs;
        _ = ctx;
    }

    private void WalkColorVertexBuffer(string ctx)
    {
        // bulk array of FGPUSkinVertexColor (4 bytes each).
        int serializedElementSize = CopyInt32();
        int count = CopyInt32();
        CopyBytes(serializedElementSize * count);
        _ = ctx;
    }

    private void WalkVertexInfluences()
    {
        // Influences: count + N * (4 bytes bones + 4 bytes weights = 8 bytes)
        int infCount = CopyInt32();
        CopyBytes(infCount * 8);
        // VertexInfluenceMapping: UMap<BoneIndexPair (2 ints = 8 bytes), UArray<uint32>>
        int mapCount = CopyInt32();
        for (int m = 0; m < mapCount; m++)
        {
            CopyBytes(8); // BoneIndexPair
            int arrCount = CopyInt32();
            CopyBytes(arrCount * 4);
        }
        // Sections: same as LOD Sections
        int secCount = CopyInt32();
        CopyBytes(secCount * 13);
        // Chunks
        int chunkCount = CopyInt32();
        for (int c = 0; c < chunkCount; c++) WalkSkelMeshChunk();
        // RequiredBones: int + bytes
        int rbCount = CopyInt32();
        CopyBytes(rbCount);
        // Usage byte
        CopyByte();
    }

    private void WalkMultiSizeIndexContainer(string ctx)
    {
        // bool (4 bytes) + byte (1) + FBulkArrayData (elementSize int32 + count int32 + bytes)
        CopyBytes(4); // NeedsCPUAccess (int32 bool)
        CopyByte();   // DataTypeSize
        // Bulk-typed array: serializedElementSize + count + payload
        int elemSize = CopyInt32();
        int count = CopyInt32();
        CopyBytes(elemSize * count);
        _ = ctx;
    }

    // FIntBulkData layout:
    //   BulkDataFlags uint32
    //   UncompressedSize int32
    //   CompressedSize int32
    //   CompressedOffset int32
    //   if compressed-meaningful: compressed chunk header + payload
    //
    // For Character Swap source UPKs we observe BulkDataFlags = 0 (stored
    // uncompressed inline) which means the payload follows directly after
    // the 16-byte header at `CompressedOffset`. The simplest safe copy:
    // header (16 bytes) + CompressedSize bytes of payload.
    private void WalkBulkData(string ctx)
    {
        uint bulkFlags = CopyUInt32();
        int uncompressedSize = CopyInt32();
        int compressedSize = CopyInt32();
        CopyInt32(); // CompressedOffset
        const uint Unused = 0x20;       // BulkDataCompressionTypes.Unused
        const uint StoreInSepFile = 0x01;
        if ((bulkFlags & (Unused | StoreInSepFile)) != 0)
            return; // header-only
        // For both uncompressed-stored (flags==0) and compressed-LZO, the
        // payload begins at the source's current cursor (we already read the
        // 16-byte header). We copy `compressedSize` bytes verbatim.
        // For uncompressed-inline (flags==0) compressedSize == uncompressedSize.
        // For compressed-LZO, compressedSize is the on-wire payload length.
        if (compressedSize > 0)
        {
            // Safety: clamp to remaining source bytes.
            int avail = _src.Length - _srcPos;
            int n = Math.Min(compressedSize, avail);
            CopyBytes(n);
            if (n < compressedSize)
                Issues.Add($"{ctx}: bulk payload truncated ({n}/{compressedSize})");
        }
        _ = uncompressedSize;
    }

    private void WalkNameIndexMap()
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
        {
            TranslateName($"NameIndexMap[{i}]");
            CopyInt32();
        }
    }

    private void WalkPerPolyBoneKDOPs()
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
        {
            // FSkeletalKDOPTreeLegacy:
            //   Nodes: ReadArrayUnkElement = int32 elementSize + int32 count + N*elementSize bytes
            //   Triangles: same
            int nodeElemSize = CopyInt32();
            int nodeCount = CopyInt32();
            CopyBytes(nodeElemSize * nodeCount);
            int triElemSize = CopyInt32();
            int triCount = CopyInt32();
            CopyBytes(triElemSize * triCount);
            // CollisionVerts: count + N * FVector
            int cvCount = CopyInt32();
            CopyBytes(cvCount * 12);
        }
    }

    private void WalkStringArray(string ctx)
    {
        int count = CopyInt32();
        for (int i = 0; i < count; i++)
        {
            int len = BitConverter.ToInt32(_src, _srcPos);
            CopyInt32();
            if (len > 0)
                CopyBytes(len);          // ASCII null-terminated
            else if (len < 0)
                CopyBytes(-len * 2);     // UTF-16
        }
        _ = ctx;
    }

    private void WalkByteArray()
    {
        int count = CopyInt32();
        CopyBytes(count);
    }

    private void WalkFloatArray()
    {
        int count = CopyInt32();
        CopyBytes(count * 4);
    }

    private void WalkSourceData()
    {
        // bHaveSourceData (int32 bool)
        int b = CopyInt32();
        if (b == 1)
            WalkStaticLODModel(_bHasVertexColors, 999);
    }

    // ---- Helpers ----
    private string ResolveSourceName(int idx)
    {
        if (idx < 0 || idx >= _srcHeader.NameTable.Count) return $"(bad#{idx})";
        return _srcHeader.NameTable[idx]?.Name?.String ?? "(null)";
    }

    private enum ArrayElementKind { Unknown, FObject, TaggedStruct }

    // Explicit per-tag-name schema. ArrayProperty value blobs don't encode
    // their element type, so we must know from the class schema what each
    // named array contains.
    //
    // Covers: USkeletalMesh's PropertyField arrays + every tagged-struct
    // array we expect to see nested in source's property stream (LODInfo,
    // TriangleSortSettings inside LODInfo, AggGeom sub-arrays inside the
    // PhysicsAsset's per-bone collision data when SkeletalMesh embeds it
    // via inheritance, etc.). Unknown arrays fall through to verbatim copy
    // which is safe for primitive-element arrays.
    private static ArrayElementKind GetArrayElementKind(string tagName)
    {
        switch (tagName?.ToLowerInvariant())
        {
            // SkeletalMesh PropertyFields
            case "sockets":
            case "clothingassets":
            case "materials":                    // top-level array (in case)
            case "anim_sets":
            case "animsets":
                return ArrayElementKind.FObject;

            // Tagged-struct arrays whose items each contain a nested
            // property stream terminated by "None".
            case "lodinfo":
            case "trianglesortsettings":
            case "lodmaterialmap":               // primitive int array (Unknown -> verbatim)
            case "benableshadowcasting":         // primitive bool array (Unknown -> verbatim)
                return tagName.Equals("lodinfo", StringComparison.OrdinalIgnoreCase) ||
                       tagName.Equals("trianglesortsettings", StringComparison.OrdinalIgnoreCase)
                    ? ArrayElementKind.TaggedStruct
                    : ArrayElementKind.Unknown;

            default:
                return ArrayElementKind.Unknown;
        }
    }

    private static readonly HashSet<string> AtomicStructs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vector", "Vector2D", "Rotator", "Color", "LinearColor", "Quat",
        "Matrix", "Box", "BoxSphereBounds", "Plane", "Sphere",
        "Vector4", "TwoVectors", "IntPoint", "IntRect",
        "Guid", "PackedNormal", "PackedPosition",
        "RawDistribution",
    };
    private static bool IsAtomicStruct(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return AtomicStructs.Contains(name);
    }

    // Quick sanity probe: the bytes at the cursor look like a valid source
    // name index if the int32 is non-negative and within source's name
    // table count. Used to decide whether to walk array elements as tagged
    // structs vs copy verbatim.
    private bool LooksLikeNameIdx(int srcOff)
    {
        if (srcOff + 8 > _src.Length) return false;
        int idx = BitConverter.ToInt32(_src, srcOff);
        if (idx < 0 || idx >= _srcHeader.NameTable.Count) return false;
        string nm = _srcHeader.NameTable[idx]?.Name?.String ?? string.Empty;
        // Real property tag names typically aren't random ASCII; require
        // them to not start with a digit and to be non-empty.
        return !string.IsNullOrEmpty(nm) && !char.IsDigit(nm[0]);
    }
}
