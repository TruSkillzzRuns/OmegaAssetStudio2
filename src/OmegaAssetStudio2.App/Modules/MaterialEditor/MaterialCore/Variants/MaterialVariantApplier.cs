using UpkManager.Models.UpkFile.Engine.Material;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Replays a learned SwitchDeltaEntry onto a baseline material resource.
// Two modes:
//
//   1. ApplyToSnapshot — produces a new MaterialBodySnapshot (read-only
//      preview). Use this to show the user "if you toggle this switch,
//      here's what the body shape will become" before they commit.
//
//   2. ApplyToResource — mutates the FMaterialResource in place. Caller
//      then re-serializes via the existing MaterialUpkWriter path. Used
//      at actual commit time.
//
// Confidence gating is the caller's responsibility: deltas with low
// SampleCount or Confidence should be flagged in the UI before applying.
public static class MaterialVariantApplier
{
    public sealed record ApplyResult(bool Ok, string? Refusal, MaterialBodySnapshot? Projected);

    public static ApplyResult ApplyToSnapshot(MaterialBodySnapshot baseline, SwitchDeltaEntry delta)
    {
        // Sanity gates: a learned delta of +3 textures against a baseline
        // that has 0 textures is fine, but a -3 textures delta would
        // underflow — refuse rather than corrupt.
        int newTextureCount = baseline.TextureCount + delta.TextureCountDelta;
        int newLookupCount  = baseline.LookupCount + delta.LookupCountDelta;
        int newTexCoords    = baseline.NumTexCoords + delta.NumTexCoordsDelta;
        if (newTextureCount < 0) return new(false, "delta drops texture count below zero", null);
        if (newLookupCount < 0)  return new(false, "delta drops lookup count below zero", null);
        if (newTexCoords < 0)    return new(false, "delta drops tex-coord count below zero", null);

        var projected = baseline with
        {
            NumTexCoords = newTexCoords,
            TextureCount = newTextureCount,
            LookupCount = newLookupCount,
            UsingTransforms = baseline.UsingTransforms ^ delta.UsingTransformsXor,
            MaxTextureDependencyLength = baseline.MaxTextureDependencyLength + delta.MaxTextureDependencyLengthDelta,
            UsesSceneColor                    = delta.UsesSceneColorTo                    ?? baseline.UsesSceneColor,
            UsesSceneDepth                    = delta.UsesSceneDepthTo                    ?? baseline.UsesSceneDepth,
            UsesDynamicParameter              = delta.UsesDynamicParameterTo              ?? baseline.UsesDynamicParameter,
            UsesLightmapUVs                   = delta.UsesLightmapUVsTo                   ?? baseline.UsesLightmapUVs,
            UsesMaterialVertexPositionOffset  = delta.UsesMaterialVertexPositionOffsetTo  ?? baseline.UsesMaterialVertexPositionOffset,
            BlendModeValue                    = delta.BlendModeValueTo                    ?? baseline.BlendModeValue,
            IsBlendModeOverridden             = delta.IsBlendModeOverriddenTo             ?? baseline.IsBlendModeOverridden,
            IsMaskedOverride                  = delta.IsMaskedOverrideTo                  ?? baseline.IsMaskedOverride,
            // Texture paths get the added list appended and removed list
            // filtered out — the writer turns these back into UObject refs.
            TexturePaths = baseline.TexturePaths
                .Where(p => !delta.TexturesRemoved.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Concat(delta.TexturesAdded)
                .ToList(),
        };
        return new(true, null, projected);
    }

    // Mutates the parsed resource directly. Texture object refs are NOT
    // resolved here — caller must hand in the matching FObject list because
    // those come from the destination UPK's import table, not from us.
    public static ApplyResult ApplyToResource(
        FMaterialResource resource,
        SwitchDeltaEntry delta,
        IReadOnlyList<UpkManager.Models.UpkFile.Tables.FObject>? newTextureObjects = null)
    {
        if (resource is null) return new(false, "null resource", null);

        var baseline = MaterialBodyReader.FromResource(resource);
        var projection = ApplyToSnapshot(baseline, delta);
        if (!projection.Ok) return projection;
        var p = projection.Projected!;

        resource.NumUserTexCoords = p.NumTexCoords;
        resource.UsingTransforms = p.UsingTransforms;
        resource.MaxTextureDependencyLength = p.MaxTextureDependencyLength;
        resource.bUsesSceneColor = p.UsesSceneColor;
        resource.bUsesSceneDepth = p.UsesSceneDepth;
        resource.bUsesDynamicParameter = p.UsesDynamicParameter;
        resource.bUsesLightmapUVs = p.UsesLightmapUVs;
        resource.bUsesMaterialVertexPositionOffset = p.UsesMaterialVertexPositionOffset;
        resource.BlendModeOverrideValue = (EBlendMode)p.BlendModeValue;
        resource.bIsBlendModeOverrided = p.IsBlendModeOverridden;
        resource.bIsMaskedOverrideValue = p.IsMaskedOverride;

        // Texture-list rewrite only when the caller supplied resolved refs.
        // Without them, mismatched count vs array would corrupt the body.
        if (newTextureObjects is not null && newTextureObjects.Count == p.TextureCount)
        {
            resource.UniformExpressionTextures = new UpkManager.Models.UpkFile.Types.UArray<UpkManager.Models.UpkFile.Tables.FObject>();
            foreach (var t in newTextureObjects) resource.UniformExpressionTextures.Add(t);
        }
        return projection;
    }
}
