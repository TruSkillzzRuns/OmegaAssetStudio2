using System.Buffers.Binary;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Walks the FMaterialResource bytes embedded in a UMaterialInstanceConstant
// export and records the byte offsets of every scalar field we may want
// to patch later. Mirrors UpkManager.FMaterial.ReadFields' read order
// exactly (the UpkManager parser is the authority on layout — if it reads
// fields in a sequence, those fields sit in that sequence in the bytes).
//
// Returns offsets relative to the start of the supplied buffer. Caller
// (MaterialBodyBytePatcher) overwrites bytes at those offsets in-place
// for fields whose width doesn't change.
public sealed class MaterialBodyByteLocator
{
    public sealed record FieldOffsets
    {
        // FMaterial fields (base class).
        public int MaxTextureDependencyLength { get; init; }    // int32, 4 bytes
        public int Id { get; init; }                             // FGuid, 16 bytes (NOT patched — identity)
        public int NumUserTexCoords { get; init; }               // int32, 4 bytes
        public int UniformExpressionTexturesCount { get; init; } // int32 count; array body starts at +4
        public int UniformExpressionTexturesBody { get; init; }  // first object ref
        public int UsesSceneColor { get; init; }                 // int32 (bool), 4 bytes
        public int UsesSceneDepth { get; init; }
        public int UsesDynamicParameter { get; init; }
        public int UsesLightmapUVs { get; init; }
        public int UsesMaterialVertexPositionOffset { get; init; }
        public int UsingTransforms { get; init; }                // uint32, 4 bytes
        public int TextureLookupsCount { get; init; }            // int32 count
        public int TextureLookupsBody { get; init; }             // first FTextureLookup
        public int DummyDroppedFallbackComponents { get; init; } // uint32, 4 bytes

        // FMaterialResource adds:
        public int BlendModeOverrideValue { get; init; }         // int32, 4 bytes
        public int IsBlendModeOverridden { get; init; }          // int32 (bool), 4 bytes
        public int IsMaskedOverride { get; init; }               // int32 (bool), 4 bytes

        // Counts captured at walk time so the patcher can sanity-check
        // before assuming a scalar-only patch is safe.
        public int TextureCount { get; init; }
        public int LookupCount { get; init; }

        // End of the resource block — useful when the caller wants to
        // know how much to copy verbatim after a scalar patch.
        public int ResourceEnd { get; init; }
    }

    // bodyStart is the absolute byte offset (in the supplied buffer) where
    // the FMaterialResource serial begins — for a quality-mask-gated MIC,
    // this is right after the qualityMask uint32.
    public static FieldOffsets? Walk(ReadOnlySpan<byte> buffer, int bodyStart)
    {
        try
        {
            int cur = bodyStart;

            // CompileErrors: int32 count + N strings.
            int compileErrors = Read32(buffer, ref cur);
            for (int i = 0; i < compileErrors; i++) SkipString(buffer, ref cur);

            // TextureDependencyLengthMap: int32 count + N * (int32 ref + int32 value).
            int depMapCount = Read32(buffer, ref cur);
            cur += depMapCount * 8;

            int maxTexDepLen = cur;       cur += 4;
            int idOff        = cur;       cur += 16;
            int numTexCoords = cur;       cur += 4;

            // UniformExpressionTextures: int32 count + count * int32 ref.
            int texCountOff = cur;
            int texCount = Read32(buffer, ref cur);
            int texBodyOff = cur;
            cur += texCount * 4;

            int usesScene = cur;          cur += 4;
            int usesDepth = cur;          cur += 4;
            int usesDyn   = cur;          cur += 4;
            int usesLm    = cur;          cur += 4;
            int usesVpo   = cur;          cur += 4;
            int xforms    = cur;          cur += 4;

            // TextureLookups: int32 count + N * 16-byte entries.
            int lookupsCountOff = cur;
            int lookupCount = Read32(buffer, ref cur);
            int lookupsBody = cur;
            cur += lookupCount * 16;

            int dummy     = cur;          cur += 4;

            // FMaterialResource trailer.
            int blendVal  = cur;          cur += 4;
            int isBlend   = cur;          cur += 4;
            int isMasked  = cur;          cur += 4;

            return new FieldOffsets
            {
                MaxTextureDependencyLength = maxTexDepLen,
                Id = idOff,
                NumUserTexCoords = numTexCoords,
                UniformExpressionTexturesCount = texCountOff,
                UniformExpressionTexturesBody = texBodyOff,
                UsesSceneColor = usesScene,
                UsesSceneDepth = usesDepth,
                UsesDynamicParameter = usesDyn,
                UsesLightmapUVs = usesLm,
                UsesMaterialVertexPositionOffset = usesVpo,
                UsingTransforms = xforms,
                TextureLookupsCount = lookupsCountOff,
                TextureLookupsBody = lookupsBody,
                DummyDroppedFallbackComponents = dummy,
                BlendModeOverrideValue = blendVal,
                IsBlendModeOverridden = isBlend,
                IsMaskedOverride = isMasked,
                TextureCount = texCount,
                LookupCount = lookupCount,
                ResourceEnd = cur,
            };
        }
        catch { return null; }
    }

    private static int Read32(ReadOnlySpan<byte> buf, ref int cur)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(cur, 4));
        cur += 4;
        return v;
    }

    private static void SkipString(ReadOnlySpan<byte> buf, ref int cur)
    {
        int size = Read32(buf, ref cur);
        if (size == 0) return;
        if (size < 0) cur += -size * 2; // UCS-2 + null
        else          cur += size;       // ASCII + null already counted in size
    }

    // Heuristic anchor finder for cases where bodyStart isn't known up front
    // (e.g. when called against raw export bytes that begin with the variable-
    // length tagged property block). Builds a 20-byte signature of the 5
    // contiguous bool-as-int32 fields from a parsed snapshot, searches the
    // bytes for it. A unique match locates the bool block; everything else
    // is reachable backward/forward from there at fixed offsets.
    public static FieldOffsets? LocateByBoolAnchor(ReadOnlySpan<byte> exportBytes, MaterialBodySnapshot snapshot)
    {
        Span<byte> pattern = stackalloc byte[20];
        WriteInt32(pattern[0..], snapshot.UsesSceneColor ? 1 : 0);
        WriteInt32(pattern[4..], snapshot.UsesSceneDepth ? 1 : 0);
        WriteInt32(pattern[8..], snapshot.UsesDynamicParameter ? 1 : 0);
        WriteInt32(pattern[12..], snapshot.UsesLightmapUVs ? 1 : 0);
        WriteInt32(pattern[16..], snapshot.UsesMaterialVertexPositionOffset ? 1 : 0);

        int boolBlockStart = -1;
        int matches = 0;
        for (int i = 0; i <= exportBytes.Length - pattern.Length; i++)
        {
            if (!exportBytes.Slice(i, pattern.Length).SequenceEqual(pattern)) continue;
            matches++;
            if (matches > 1) return null; // ambiguous — refuse rather than guess
            boolBlockStart = i;
        }
        if (boolBlockStart < 0) return null;

        // Compute backward to NumUserTexCoords + MaxTexDepLen + Id.
        // Layout (forward from texCount field):
        //   [int32 texCount][texCount * 4 bytes texture refs][5 * 4 bytes bool block]
        // Knowing snapshot.TextureCount lets us walk backward.
        int texBodyOff = boolBlockStart - snapshot.TextureCount * 4;
        int texCountOff = texBodyOff - 4;
        int numTexCoordsOff = texCountOff - 4;
        int idOff = numTexCoordsOff - 16;
        int maxTexDepLenOff = idOff - 4;

        // Forward from bool block (deterministic).
        int xforms = boolBlockStart + 20;
        int lookupsCountOff = xforms + 4;
        int lookupsBody = lookupsCountOff + 4;
        int dummy = lookupsBody + snapshot.LookupCount * 16;
        int blendVal = dummy + 4;
        int isBlend = blendVal + 4;
        int isMasked = isBlend + 4;
        int resourceEnd = isMasked + 4;

        // Defensive bounds check — if backward walk underflowed, refuse.
        if (maxTexDepLenOff < 0 || resourceEnd > exportBytes.Length) return null;

        return new FieldOffsets
        {
            MaxTextureDependencyLength = maxTexDepLenOff,
            Id = idOff,
            NumUserTexCoords = numTexCoordsOff,
            UniformExpressionTexturesCount = texCountOff,
            UniformExpressionTexturesBody = texBodyOff,
            UsesSceneColor = boolBlockStart,
            UsesSceneDepth = boolBlockStart + 4,
            UsesDynamicParameter = boolBlockStart + 8,
            UsesLightmapUVs = boolBlockStart + 12,
            UsesMaterialVertexPositionOffset = boolBlockStart + 16,
            UsingTransforms = xforms,
            TextureLookupsCount = lookupsCountOff,
            TextureLookupsBody = lookupsBody,
            DummyDroppedFallbackComponents = dummy,
            BlendModeOverrideValue = blendVal,
            IsBlendModeOverridden = isBlend,
            IsMaskedOverride = isMasked,
            TextureCount = snapshot.TextureCount,
            LookupCount = snapshot.LookupCount,
            ResourceEnd = resourceEnd,
        };
    }

    private static void WriteInt32(Span<byte> dst, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(dst, value);
}
