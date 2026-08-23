using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Walks the BINARY tail that sits after a UMaterial / UMaterialInstance's
// tagged-property block:
//
//   UMaterial body:
//     ... tagged props ...
//     uint32 qualityMask
//     for q in {0,1}: if (qualityMask & (1<<q)) FMaterialResource
//
//   UMaterialInstance body (only when bHasStaticPermutationResource=true):
//     ... tagged props ...
//     uint32 qualityMask
//     for q in {0,1}: if (qualityMask & (1<<q))
//         FMaterialResource + FStaticParameterSet
//
// FMaterialResource layout (UpkManager UMaterial.cs FMaterial.ReadFields):
//   UArray<string>  CompileErrors
//   UMap<FObject,int> TextureDependencyLengthMap     ← FObject refs need translating
//   int             MaxTextureDependencyLength
//   FGuid           Id (16 bytes)
//   int             NumUserTexCoords
//   UArray<FObject> UniformExpressionTextures        ← FObject refs need translating
//   5 × bool (4-byte int32 each)
//   uint32          UsingTransforms
//   UArray<{int,int,float,float}> TextureLookups
//   uint32          DummyDroppedFallbackComponents
//   int             BlendModeOverrideValue (enum)
//   bool            bIsBlendModeOverrided
//   bool            bIsMaskedOverrideValue
//
// FStaticParameterSet layout (UMaterial.cs FStaticParameterSet.ReadData):
//   FGuid           BaseMaterialId
//   UArray<FStaticSwitchParameter>          (FName + bool + bool + FGuid = 32 bytes)
//   UArray<FStaticComponentMaskParameter>   (FName + 5×bool + FGuid = 44 bytes)
//   UArray<FNormalParameter>                (FName + byte + bool + FGuid = 29 bytes)
//   UArray<FStaticTerrainLayerWeightParameter> (FName + int + bool + FGuid = 32 bytes)
//
// All FName fields are 8 bytes (NameTableIndex int32 + NumericExtension int32).
public static class MaterialResourceTailRewriter
{
    public sealed record TailRewriteResult(byte[] RewrittenBytes, int ResourcesFound);

    // Rewrite a UMaterial's binary tail. The tagged-prop block has already
    // been consumed by the caller; `body` is just the leftover bytes after
    // the "None" terminator.
    public static TailRewriteResult RewriteUMaterialTail(
        byte[] tailBytes,
        CrossUpkReferenceTranslator translator)
    {
        if (tailBytes.Length < 4)
            return new(tailBytes, 0);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        using var br = new BinaryReader(new MemoryStream(tailBytes, writable: false));

        uint qualityMask = br.ReadUInt32();
        bw.Write(qualityMask);
        int resources = 0;
        for (int q = 0; q < 2; q++)
        {
            if ((qualityMask & (1u << q)) == 0) continue;
            if (!TryWriteFMaterialResource(br, bw, translator)) break;
            resources++;
        }
        // Copy any leftover bytes verbatim.
        long left = br.BaseStream.Length - br.BaseStream.Position;
        if (left > 0) bw.Write(br.ReadBytes((int)left));
        return new(ms.ToArray(), resources);
    }

    // Rewrite a UMaterialInstance's binary tail. `hasStaticPermutationResource`
    // is the tagged bool the caller read out of the property stream. When
    // false there is no binary tail at all.
    public static TailRewriteResult RewriteUMaterialInstanceTail(
        byte[] tailBytes,
        bool hasStaticPermutationResource,
        CrossUpkReferenceTranslator translator)
    {
        if (!hasStaticPermutationResource || tailBytes.Length < 4)
            return new(tailBytes, 0);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        using var br = new BinaryReader(new MemoryStream(tailBytes, writable: false));

        uint qualityMask = br.ReadUInt32();
        bw.Write(qualityMask);
        int resources = 0;
        for (int q = 0; q < 2; q++)
        {
            if ((qualityMask & (1u << q)) == 0) continue;
            if (!TryWriteFMaterialResource(br, bw, translator)) break;
            if (!TryWriteFStaticParameterSet(br, bw, translator)) break;
            resources++;
        }
        long left = br.BaseStream.Length - br.BaseStream.Position;
        if (left > 0) bw.Write(br.ReadBytes((int)left));
        return new(ms.ToArray(), resources);
    }

    // ----------------------------------------------------------------
    // FMaterialResource
    // ----------------------------------------------------------------
    private static bool TryWriteFMaterialResource(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t)
    {
        try
        {
            // CompileErrors: UArray<string>
            WriteStringArray(br, bw);

            // TextureDependencyLengthMap: count + N × (FObject + int)
            int mapCount = br.ReadInt32(); bw.Write(mapCount);
            for (int i = 0; i < mapCount; i++)
            {
                bw.Write(t.TranslateObjectRef(br.ReadInt32()));    // FObject key
                bw.Write(br.ReadInt32());                          // int value
            }

            // MaxTextureDependencyLength, Id, NumUserTexCoords
            bw.Write(br.ReadInt32());                              // MaxTextureDependencyLength
            bw.Write(br.ReadBytes(16));                            // Id (FGuid)
            bw.Write(br.ReadInt32());                              // NumUserTexCoords

            // UniformExpressionTextures: UArray<FObject>
            int texCount = br.ReadInt32(); bw.Write(texCount);
            for (int i = 0; i < texCount; i++)
                bw.Write(t.TranslateObjectRef(br.ReadInt32()));

            // 5 × bool (each 4 bytes)
            bw.Write(br.ReadBytes(5 * 4));

            // UsingTransforms (uint32)
            bw.Write(br.ReadUInt32());

            // TextureLookups: UArray<{int, int, float, float}> = count + N × 16
            int lookCount = br.ReadInt32(); bw.Write(lookCount);
            if (lookCount > 0) bw.Write(br.ReadBytes(lookCount * 16));

            // DummyDroppedFallbackComponents (uint32)
            bw.Write(br.ReadUInt32());

            // BlendModeOverrideValue (int32 enum), bIsBlendModeOverrided (4),
            // bIsMaskedOverrideValue (4)
            bw.Write(br.ReadInt32());                              // BlendModeOverride
            bw.Write(br.ReadInt32());                              // bIsBlendModeOverrided
            bw.Write(br.ReadInt32());                              // bIsMaskedOverrideValue
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------------
    // FStaticParameterSet
    // ----------------------------------------------------------------
    private static bool TryWriteFStaticParameterSet(
        BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t)
    {
        try
        {
            // BaseMaterialId
            bw.Write(br.ReadBytes(16));

            // StaticSwitchParameters: FName + bool(4) + bool(4) + FGuid(16) = 32 bytes
            int ss = br.ReadInt32(); bw.Write(ss);
            for (int i = 0; i < ss; i++)
            {
                CopyFName(br, bw, t);
                bw.Write(br.ReadBytes(4 + 4 + 16));
            }

            // StaticComponentMaskParameters: FName + 5×bool(4 each) + FGuid(16) = 44 bytes
            int cm = br.ReadInt32(); bw.Write(cm);
            for (int i = 0; i < cm; i++)
            {
                CopyFName(br, bw, t);
                bw.Write(br.ReadBytes(5 * 4 + 16));
            }

            // NormalParameters: FName + byte(1) + bool(4) + FGuid(16) = 29 bytes
            int np = br.ReadInt32(); bw.Write(np);
            for (int i = 0; i < np; i++)
            {
                CopyFName(br, bw, t);
                bw.Write(br.ReadBytes(1 + 4 + 16));
            }

            // TerrainLayerWeightParameters: FName + int(4) + bool(4) + FGuid(16) = 32 bytes
            int tl = br.ReadInt32(); bw.Write(tl);
            for (int i = 0; i < tl; i++)
            {
                CopyFName(br, bw, t);
                bw.Write(br.ReadBytes(4 + 4 + 16));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // UE3 strings: int Length; chars (ASCII when positive, UCS-2 LE when negative,
    // length × 2 bytes either way for negative, length bytes otherwise; both
    // include a null terminator).
    private static void WriteStringArray(BinaryReader br, BinaryWriter bw)
    {
        int count = br.ReadInt32(); bw.Write(count);
        for (int i = 0; i < count; i++)
            WriteString(br, bw);
    }

    private static void WriteString(BinaryReader br, BinaryWriter bw)
    {
        int len = br.ReadInt32(); bw.Write(len);
        if (len == 0) return;
        int byteLen = len > 0 ? len : -len * 2;
        bw.Write(br.ReadBytes(byteLen));
    }

    private static void CopyFName(BinaryReader br, BinaryWriter bw, CrossUpkReferenceTranslator t)
    {
        int idx = br.ReadInt32(); int num = br.ReadInt32();
        bw.Write(t.TranslateName(idx)); bw.Write(num);
    }
}
