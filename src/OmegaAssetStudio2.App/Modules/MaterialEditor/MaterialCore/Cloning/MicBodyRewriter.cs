using System.Buffers.Binary;
using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Rewrites a UMaterial / UMaterialInstanceConstant / UMaterialInstanceTimeVarying
// export's serial bytes so every embedded FName / FObject reference points at
// the DESTINATION UPK's tables instead of the source UPK's. Uses
// CrossUpkReferenceTranslator for the actual index translation + new-name /
// import queueing.
//
// Walks the tagged-property block, translating:
//   - each property tag's Name + Type FName
//   - ObjectProperty / Interface / Component / Class payload (4-byte ref)
//   - NameProperty / ByteProperty(typed) payload (8-byte FName)
//   - ArrayProperty payloads for known MIC + UMaterial array shapes
//   - StructProperty payloads: atomic structs (Vector/Color/Guid/etc.) are
//     copied verbatim; everything else recurses into the tagged-property
//     walker so e.g. FColorMaterialInput's nested Expression FObject gets
//     translated.
//
// SCOPE: targets the property types + struct shapes seen on UMaterial,
// UMaterialInstanceConstant, and UMaterialInstanceTimeVarying. Truly unknown
// property types pass through verbatim — caller-side warnings flag this.
public sealed class MicBodyRewriter
{
    public sealed record RewriteResult(
        byte[] RewrittenBytes,
        IReadOnlyList<string> AddedNames,
        IReadOnlyList<OmegaAssetStudio.UpkRepacker.NewImportSpec> AddedImports);

    public static RewriteResult Rewrite(
        byte[] sourceBody,
        UnrealHeader sourceHeader,
        UnrealHeader destHeader)
        => Rewrite(sourceBody, sourceHeader, destHeader,
                   new CrossUpkReferenceTranslator(sourceHeader, destHeader));

    // Overload that lets the caller share a single translator across the
    // body walk AND any out-of-band ref translations (the cloned MIC's own
    // Class/Super/Outer/Archetype refs). One translator = one consistent
    // index space for both AddedNames and AddedImports — separate translators
    // would each compute future-index slots starting at the same base, so
    // their indices collide when merged.
    public static RewriteResult Rewrite(
        byte[] sourceBody,
        UnrealHeader sourceHeader,
        UnrealHeader destHeader,
        CrossUpkReferenceTranslator translator)
        => RewriteInternal(sourceBody, sourceHeader, destHeader, translator, sourceExportClass: null);

    // Class-aware overload: when the caller knows whether the export is a
    // UMaterial vs UMaterialInstance the trailing FMaterialResource[s] gets
    // walked structurally so its FObject + FName refs are translated. Without
    // a class hint we leave the tail verbatim (matches old behavior).
    public static RewriteResult Rewrite(
        byte[] sourceBody,
        UnrealHeader sourceHeader,
        UnrealHeader destHeader,
        CrossUpkReferenceTranslator translator,
        string sourceExportClass)
        => RewriteInternal(sourceBody, sourceHeader, destHeader, translator, sourceExportClass);

    private static RewriteResult RewriteInternal(
        byte[] sourceBody,
        UnrealHeader sourceHeader,
        UnrealHeader destHeader,
        CrossUpkReferenceTranslator translator,
        string? sourceExportClass)
    {
        // Source name lookup table — bypasses the bogus SafeName indirection.
        var sourceNames = new string[sourceHeader.NameTable.Count];
        for (int i = 0; i < sourceHeader.NameTable.Count; i++)
            sourceNames[i] = sourceHeader.NameTable[i].Name?.String ?? "";

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        var br = new BinaryReader(new MemoryStream(sourceBody, writable: false));

        // UE3 UObject bodies start with a 4-byte NetIndex *before* the
        // tagged-property block. Copy it through verbatim — it's a local
        // index inside the export, not a NameTable/ObjectTable ref.
        bw.Write(br.ReadInt32());

        // Track whether the tagged-prop walker saw bHasStaticPermutationResource
        // = true (only relevant for UMaterialInstance / MITV).
        bool hasStaticPermutation = false;
        WriteTaggedPropertyBlock(br, bw, translator, sourceNames,
            onBoolProperty: (name, val) =>
            {
                if (string.Equals(name, "bHasStaticPermutationResource",
                                  StringComparison.OrdinalIgnoreCase) && val)
                    hasStaticPermutation = true;
            });

        // Whatever bytes remain after the "None" terminator — the binary
        // tail. For UMaterial this is qualityMask + FMaterialResource[2].
        // For UMaterialInstance with bHasStaticPermutationResource=true it's
        // qualityMask + FMaterialResource[2] + FStaticParameterSet[2].
        long remaining = br.BaseStream.Length - br.BaseStream.Position;
        if (remaining > 0)
        {
            byte[] tail = br.ReadBytes((int)remaining);
            byte[] rewrittenTail = RewriteTail(tail, sourceExportClass, hasStaticPermutation, translator);
            bw.Write(rewrittenTail);
        }
        return new RewriteResult(ms.ToArray(), translator.AddedNames, translator.AddedImports);
    }

    private static byte[] RewriteTail(
        byte[] tail, string? sourceExportClass, bool hasStaticPermutation,
        CrossUpkReferenceTranslator translator)
    {
        if (tail.Length == 0) return tail;
        try
        {
            string cls = (sourceExportClass ?? "").ToLowerInvariant();
            if (cls == "material")
            {
                var result = MaterialResourceTailRewriter.RewriteUMaterialTail(tail, translator);
                return result.RewrittenBytes;
            }
            if (cls == "materialinstanceconstant" || cls == "materialinstancetimevarying")
            {
                var result = MaterialResourceTailRewriter.RewriteUMaterialInstanceTail(
                    tail, hasStaticPermutation, translator);
                return result.RewrittenBytes;
            }
        }
        catch { /* fall through to verbatim */ }
        return tail;
    }

    private static void WriteTaggedPropertyBlock(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t, string[] sourceNames,
        Action<string, bool>? onBoolProperty = null)
    {
        string lastProperty = "(none)";
        string lastType = "(none)";
        int lastSize = 0;
        long entryOffset = 0;
        try
        {
            while (true)
            {
                entryOffset = br.BaseStream.Position;
                // Some MIC bodies stop at the last real property instead of
                // emitting a "None" terminator — UnrealObjectReader.GetBytes()
                // returns the parsed-bytes window which may end exactly at the
                // last payload. EOF at this point is a clean end-of-block.
                if (br.BaseStream.Position >= br.BaseStream.Length)
                    return;
                // FName Name = 8 bytes (int32 index + int32 numeric)
                int nameIdx = br.ReadInt32();
                int nameNum = br.ReadInt32();
                int destNameIdx = t.TranslateName(nameIdx);
                bw.Write(destNameIdx);
                bw.Write(nameNum);

                string propertyName = (nameIdx >= 0 && nameIdx < sourceNames.Length) ? sourceNames[nameIdx] : "";
                lastProperty = propertyName;
                if (string.Equals(propertyName, "None", StringComparison.OrdinalIgnoreCase))
                    return; // end of block

                // FName Type
                int typeIdx = br.ReadInt32();
                int typeNum = br.ReadInt32();
                bw.Write(t.TranslateName(typeIdx));
                bw.Write(typeNum);
                string typeName = (typeIdx >= 0 && typeIdx < sourceNames.Length) ? sourceNames[typeIdx] : "";
                lastType = typeName;

                // int32 ElementSize + int32 ArrayIndex
                int size = br.ReadInt32(); bw.Write(size);
                int arrIdx = br.ReadInt32(); bw.Write(arrIdx);
                lastSize = size;

                // BoolProperty payload (1 byte) — peek the value so callers
                // can react to specific bool tags (e.g. bHasStaticPermutationResource).
                if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                {
                    byte v = br.ReadByte();
                    bw.Write(v);
                    onBoolProperty?.Invoke(propertyName, v != 0);
                    continue;
                }

                // Payload — translate refs per known type.
                RewritePayload(br, bw, t, typeName, size, propertyName, sourceNames);
            }
        }
        catch (EndOfStreamException eos)
        {
            throw new InvalidDataException(
                $"MIC body walker overshot the property block. Last property tag at " +
                $"offset 0x{entryOffset:X} was Name='{lastProperty}' Type='{lastType}' Size={lastSize}. " +
                $"Stream length = {br.BaseStream.Length}, position = {br.BaseStream.Position}. " +
                $"This means the walker's layout for type '{lastType}' is off — either the payload " +
                $"size is wrong, the type has an extra header FName, or there's a per-property prefix " +
                $"we're not consuming.", eos);
        }
    }

    private static void RewritePayload(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t,
        string typeName, int size, string propertyName, string[] sourceNames)
    {
        switch (typeName)
        {
            case "BoolProperty":
                // size = 0; value is 1 byte after the tag
                bw.Write(br.ReadByte());
                break;
            case "IntProperty":
            case "FloatProperty":
                bw.Write(br.ReadBytes(size)); // 4 bytes, no refs
                break;
            case "ObjectProperty":
            case "InterfaceProperty":
            case "ComponentProperty":
            case "ClassProperty":
                {
                    int sourceRef = br.ReadInt32();
                    bw.Write(t.TranslateObjectRef(sourceRef));
                    break;
                }
            case "NameProperty":
                {
                    int idx = br.ReadInt32(); int num = br.ReadInt32();
                    bw.Write(t.TranslateName(idx));
                    bw.Write(num);
                    break;
                }
            case "ByteProperty":
                // UE3 ByteProperty layout:
                //   FName Enum (8 bytes, always — "None" if untyped)
                //   then: if Enum is None → 1 byte value
                //         else            → FName EnumValueName (8 bytes)
                // The `size` field in the tag is just the value (1 or 8),
                // NOT including the Enum FName which is part of the header
                // extension.
                {
                    int enumIdx = br.ReadInt32(); int enumNum = br.ReadInt32();
                    bw.Write(t.TranslateName(enumIdx)); bw.Write(enumNum);
                    if (size == 8)
                    {
                        int idx = br.ReadInt32(); int num = br.ReadInt32();
                        bw.Write(t.TranslateName(idx)); bw.Write(num);
                    }
                    else { bw.Write(br.ReadBytes(size)); }
                }
                break;
            case "StrProperty":
                bw.Write(br.ReadBytes(size)); // string payload — no refs
                break;
            case "ArrayProperty":
                RewriteArrayPayload(br, bw, t, size, propertyName, sourceNames);
                break;
            case "StructProperty":
                RewriteStructPayload(br, bw, t, size, sourceNames);
                break;
            default:
                // Unknown property — pass through. References inside are
                // not translated; loud failure would be worse than partial
                // success for the common case.
                bw.Write(br.ReadBytes(size));
                break;
        }
    }

    private static void RewriteArrayPayload(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t,
        int size, string arrayName, string[] sourceNames)
    {
        long start = br.BaseStream.Position;
        int count = br.ReadInt32(); bw.Write(count);

        // Known MIC / UMaterial arrays — element layouts hardcoded from
        // UpkManager parsers (UMaterial.cs, UMaterialInstance.cs).
        switch (arrayName)
        {
            case "TextureParameterValues":
                // FTextureParameterValue: FName ParameterName + FObject ParameterValue +
                // FGuid ExpressionGUID. 28 bytes total.
                for (int i = 0; i < count; i++)
                {
                    CopyFName(br, bw, t);                              // ParameterName
                    bw.Write(t.TranslateObjectRef(br.ReadInt32()));    // texture obj ref
                    bw.Write(br.ReadBytes(16));                        // ExpressionGUID
                }
                break;
            case "ScalarParameterValues":
                // FScalarParameterValue: FName + float + FGuid. 28 bytes.
                for (int i = 0; i < count; i++)
                {
                    CopyFName(br, bw, t);                              // ParameterName
                    bw.Write(br.ReadSingle());                         // float value
                    bw.Write(br.ReadBytes(16));                        // ExpressionGUID
                }
                break;
            case "VectorParameterValues":
                // FVectorParameterValue: FName + FLinearColor (16) + FGuid (16). 40 bytes.
                for (int i = 0; i < count; i++)
                {
                    CopyFName(br, bw, t);                              // ParameterName
                    bw.Write(br.ReadBytes(16));                        // FLinearColor
                    bw.Write(br.ReadBytes(16));                        // ExpressionGUID
                }
                break;
            case "FontParameterValues":
                // FFontParameterValue: FName + FObject(UFont) + int FontPage + FGuid. 32 bytes.
                for (int i = 0; i < count; i++)
                {
                    CopyFName(br, bw, t);                              // ParameterName
                    bw.Write(t.TranslateObjectRef(br.ReadInt32()));    // FontValue obj ref
                    bw.Write(br.ReadInt32());                          // FontPage
                    bw.Write(br.ReadBytes(16));                        // ExpressionGUID
                }
                break;

            // ---------- UMaterial arrays ----------
            case "Expressions":
            case "FunctionExpressions":
            case "EditorComments":
            case "UniformExpressionTextures":
                // UArray<FObject> — bare 4-byte object refs.
                for (int i = 0; i < count; i++)
                    bw.Write(t.TranslateObjectRef(br.ReadInt32()));
                break;
            case "MaterialFunctionInfos":
                // UArray<FMaterialFunctionInfo>: FGuid StateId + FObject Function.
                // 20 bytes per element.
                for (int i = 0; i < count; i++)
                {
                    bw.Write(br.ReadBytes(16));                        // StateId
                    bw.Write(t.TranslateObjectRef(br.ReadInt32()));    // Function
                }
                break;

            default:
                {
                    // Unknown array — copy the remainder of the payload verbatim.
                    long consumed = br.BaseStream.Position - start;
                    int remaining = size - (int)consumed;
                    if (remaining > 0) bw.Write(br.ReadBytes(remaining));
                    break;
                }
        }
    }

    // UE3 atomic struct names — their payloads are raw binary with no FName /
    // FObject refs, so we copy them verbatim. Anything not in this set is
    // treated as a NESTED tagged-property block and recursed (e.g.
    // FColorMaterialInput, FMaterialFunctionInfo, FMaterialInput).
    private static readonly HashSet<string> AtomicStructNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vector", "Vector2D", "Vector4", "IntPoint", "IntVector",
        "Rotator", "Quat", "Plane", "Matrix",
        "Color", "LinearColor",
        "Guid",
        "Box", "BoxSphereBounds", "Sphere",
        "Range", "RangeVector",
        "TwoVectors",
    };

    private static void RewriteStructPayload(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t,
        int size, string[] sourceNames)
    {
        // UE3 StructProperty layout: FName StructName (8 bytes, header
        // extension, NOT included in the tag's `size`) followed by `size`
        // bytes of struct body.
        int idx = br.ReadInt32(); int num = br.ReadInt32();
        bw.Write(t.TranslateName(idx)); bw.Write(num);
        string structName = (idx >= 0 && idx < sourceNames.Length) ? sourceNames[idx] : "";

        if (size <= 0) return;

        if (AtomicStructNames.Contains(structName))
        {
            // Raw binary — copy verbatim.
            bw.Write(br.ReadBytes(size));
            return;
        }

        // Tagged struct — recurse into the walker so nested FName / FObject
        // refs get translated. The struct body's tagged-prop stream is
        // terminated by a "None" tag, but to bound-check against `size`
        // we wrap the substream.
        long endPos = br.BaseStream.Position + size;
        try
        {
            // Read the struct body as a self-contained tagged-prop block.
            byte[] subBytes = br.ReadBytes(size);
            using var subIn = new MemoryStream(subBytes, writable: false);
            using var subOut = new MemoryStream();
            using var subBr = new BinaryReader(subIn);
            using var subBw = new BinaryWriter(subOut);
            WriteTaggedPropertyBlock(subBr, subBw, t, sourceNames);
            // If the walker stopped before consuming everything, copy the
            // trailing bytes verbatim (some structs have a binary footer).
            long left = subIn.Length - subIn.Position;
            if (left > 0) subBw.Write(subBr.ReadBytes((int)left));
            // The block size MUST equal the input size — the substream is
            // a closed system, no extra bytes added/removed.
            byte[] outBytes = subOut.ToArray();
            if (outBytes.Length != size)
            {
                // Layout drift — fall back to verbatim copy to keep the file
                // structurally valid. The unmatched refs inside this struct
                // won't be translated.
                br.BaseStream.Position = endPos - size;
                bw.Write(br.ReadBytes(size));
                return;
            }
            bw.Write(outBytes);
        }
        catch
        {
            // Anything goes wrong → verbatim copy. The Material file stays
            // valid even if refs inside a weird struct stay stale.
            br.BaseStream.Position = endPos - size;
            bw.Write(br.ReadBytes(size));
        }
    }

    private static void CopyFName(BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t)
    {
        int idx = br.ReadInt32(); int num = br.ReadInt32();
        bw.Write(t.TranslateName(idx)); bw.Write(num);
    }

    private static string SafeName(CrossUpkReferenceTranslator _, int sourceIdx)
    {
        // The translator has already mapped this name; the caller wants the
        // original string. We'd need source NameTable access — translator
        // doesn't expose it. Keep a lightweight reverse via reflection on
        // source state would be overkill. Cheaper: use the dest name string
        // since we just queued/translated it.
        // We do this by peeking into AddedNames; if the name was just added,
        // it'll be at AddedNames[last]. Otherwise it's in dest table at
        // the index we got back. But we don't have direct access here.
        // For simplicity, return "" — caller compares against literal "None"
        // string elsewhere via a separate path. We special-case None below.
        return ""; // see direct-name check pattern in WriteTaggedPropertyBlock
    }
}
