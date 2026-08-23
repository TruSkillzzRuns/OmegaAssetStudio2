namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Learns SwitchDeltaEntry from sample pairs. The protocol:
//   1. Caller picks two CorpusSamples that share a ParentId.
//   2. Caller identifies the single switch that differs between them.
//   3. ComputeDelta returns the byte-level change "from off to on" for
//      that switch.
//   4. Caller calls Merge() to fold the result into an accumulator; many
//      pairs converging on the same delta raise its Confidence.
//
// Output is consumable by MaterialVariantApplier. Sample-mismatched pairs
// (more than one switch differs, or different parents) are rejected so
// the learner never learns from a noisy pair.
public static class MaterialVariantLearner
{
    public sealed record PairCompareResult(
        bool Compatible,
        string? DifferingSwitch,
        SwitchDeltaEntry? Delta,
        string? RejectReason);

    public static PairCompareResult ComparePair(CorpusSample off, CorpusSample on)
    {
        if (off.ParentId != on.ParentId)
            return new(false, null, null, "different parents");

        // Locate the single switch where 'off' was false and 'on' was true.
        string? differing = null;
        foreach (var (k, v) in on.SwitchValues)
        {
            bool offVal = off.SwitchValues.TryGetValue(k, out var ov) ? ov : false;
            if (offVal != v)
            {
                if (differing is not null) return new(false, null, null, "multiple switches differ");
                differing = k;
            }
        }
        if (differing is null) return new(false, null, null, "no switch difference");

        var delta = ComputeDelta(off.Snapshot, on.Snapshot, differing);
        return new(true, differing, delta, null);
    }

    // Pure snapshot diff — no validation that the switch actually caused
    // the changes. Caller should only call this on a confirmed pair.
    public static SwitchDeltaEntry ComputeDelta(MaterialBodySnapshot off, MaterialBodySnapshot on, string switchName)
    {
        return new SwitchDeltaEntry
        {
            SwitchName = switchName,
            NumTexCoordsDelta = on.NumTexCoords - off.NumTexCoords,
            TextureCountDelta = on.TextureCount - off.TextureCount,
            LookupCountDelta = on.LookupCount - off.LookupCount,
            UsingTransformsXor = off.UsingTransforms ^ on.UsingTransforms,
            MaxTextureDependencyLengthDelta = on.MaxTextureDependencyLength - off.MaxTextureDependencyLength,
            UsesSceneColorTo               = off.UsesSceneColor               != on.UsesSceneColor               ? on.UsesSceneColor               : null,
            UsesSceneDepthTo               = off.UsesSceneDepth               != on.UsesSceneDepth               ? on.UsesSceneDepth               : null,
            UsesDynamicParameterTo         = off.UsesDynamicParameter         != on.UsesDynamicParameter         ? on.UsesDynamicParameter         : null,
            UsesLightmapUVsTo              = off.UsesLightmapUVs              != on.UsesLightmapUVs              ? on.UsesLightmapUVs              : null,
            UsesMaterialVertexPositionOffsetTo = off.UsesMaterialVertexPositionOffset != on.UsesMaterialVertexPositionOffset ? on.UsesMaterialVertexPositionOffset : null,
            BlendModeValueTo               = off.BlendModeValue               != on.BlendModeValue               ? on.BlendModeValue               : null,
            IsBlendModeOverriddenTo        = off.IsBlendModeOverridden        != on.IsBlendModeOverridden        ? on.IsBlendModeOverridden        : null,
            IsMaskedOverrideTo             = off.IsMaskedOverride             != on.IsMaskedOverride             ? on.IsMaskedOverride             : null,
            TexturesAdded   = on.TexturePaths.Except(off.TexturePaths, StringComparer.OrdinalIgnoreCase).ToList(),
            TexturesRemoved = off.TexturePaths.Except(on.TexturePaths, StringComparer.OrdinalIgnoreCase).ToList(),
            SampleCount = 1,
            Confidence = 1.0,
        };
    }

    // Folds a new delta into an existing accumulator. Confidence drops if
    // the new sample contradicts the old (different numeric delta, etc.).
    // Returns the merged entry.
    public static SwitchDeltaEntry Merge(SwitchDeltaEntry existing, SwitchDeltaEntry incoming)
    {
        if (existing.SwitchName != incoming.SwitchName) return existing;

        bool agrees =
            existing.NumTexCoordsDelta == incoming.NumTexCoordsDelta &&
            existing.TextureCountDelta == incoming.TextureCountDelta &&
            existing.LookupCountDelta == incoming.LookupCountDelta &&
            existing.UsingTransformsXor == incoming.UsingTransformsXor &&
            existing.MaxTextureDependencyLengthDelta == incoming.MaxTextureDependencyLengthDelta &&
            existing.UsesSceneColorTo == incoming.UsesSceneColorTo &&
            existing.UsesSceneDepthTo == incoming.UsesSceneDepthTo &&
            existing.UsesDynamicParameterTo == incoming.UsesDynamicParameterTo &&
            existing.UsesLightmapUVsTo == incoming.UsesLightmapUVsTo &&
            existing.UsesMaterialVertexPositionOffsetTo == incoming.UsesMaterialVertexPositionOffsetTo &&
            existing.BlendModeValueTo == incoming.BlendModeValueTo &&
            existing.IsBlendModeOverriddenTo == incoming.IsBlendModeOverriddenTo &&
            existing.IsMaskedOverrideTo == incoming.IsMaskedOverrideTo;

        int newCount = existing.SampleCount + 1;
        // Running average confidence: agreeing pair pushes toward 1.0,
        // disagreeing pair pulls toward 0.5 (could be parent-specific).
        double newConfidence = ((existing.Confidence * existing.SampleCount) + (agrees ? 1.0 : 0.5)) / newCount;
        return existing with { SampleCount = newCount, Confidence = newConfidence };
    }
}
