using System.Buffers.Binary;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// In-place byte patcher for SAFE (scalar) delta fields. Refuses any delta
// that would change array sizes — those need full reserialization of the
// region that follows and are out of scope here.
public static class MaterialBodyBytePatcher
{
    public sealed record PatchResult(bool Ok, string? Refusal, int FieldsWritten);

    public static PatchResult ApplyScalarDelta(
        byte[] exportBytes,
        int bodyStart,
        SwitchDeltaEntry delta)
    {
        // Hard refusal: any structural-size change requires full reserialize.
        if (delta.TextureCountDelta != 0)   return new(false, "delta changes texture count — full reserialize needed", 0);
        if (delta.LookupCountDelta != 0)    return new(false, "delta changes lookup count — full reserialize needed", 0);
        if (delta.TexturesAdded.Count != 0) return new(false, "delta adds textures — full reserialize needed", 0);
        if (delta.TexturesRemoved.Count != 0) return new(false, "delta removes textures — full reserialize needed", 0);

        var offsets = MaterialBodyByteLocator.Walk(exportBytes, bodyStart);
        if (offsets is null) return new(false, "could not parse material body offsets", 0);

        int written = 0;
        var span = exportBytes.AsSpan();

        if (delta.NumTexCoordsDelta != 0)
        {
            int cur = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offsets.NumUserTexCoords, 4));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offsets.NumUserTexCoords, 4), cur + delta.NumTexCoordsDelta);
            written++;
        }
        if (delta.MaxTextureDependencyLengthDelta != 0)
        {
            int cur = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offsets.MaxTextureDependencyLength, 4));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offsets.MaxTextureDependencyLength, 4), cur + delta.MaxTextureDependencyLengthDelta);
            written++;
        }
        if (delta.UsingTransformsXor != 0)
        {
            uint cur = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offsets.UsingTransforms, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offsets.UsingTransforms, 4), cur ^ delta.UsingTransformsXor);
            written++;
        }
        written += MaybeWriteBool(span, offsets.UsesSceneColor,             delta.UsesSceneColorTo);
        written += MaybeWriteBool(span, offsets.UsesSceneDepth,             delta.UsesSceneDepthTo);
        written += MaybeWriteBool(span, offsets.UsesDynamicParameter,       delta.UsesDynamicParameterTo);
        written += MaybeWriteBool(span, offsets.UsesLightmapUVs,            delta.UsesLightmapUVsTo);
        written += MaybeWriteBool(span, offsets.UsesMaterialVertexPositionOffset, delta.UsesMaterialVertexPositionOffsetTo);
        written += MaybeWriteBool(span, offsets.IsBlendModeOverridden,      delta.IsBlendModeOverriddenTo);
        written += MaybeWriteBool(span, offsets.IsMaskedOverride,           delta.IsMaskedOverrideTo);
        if (delta.BlendModeValueTo is int newBm)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offsets.BlendModeOverrideValue, 4), newBm);
            written++;
        }
        return new(true, null, written);
    }

    private static int MaybeWriteBool(Span<byte> span, int offset, bool? to)
    {
        if (to is not bool v) return 0;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), v ? 1 : 0);
        return 1;
    }
}
