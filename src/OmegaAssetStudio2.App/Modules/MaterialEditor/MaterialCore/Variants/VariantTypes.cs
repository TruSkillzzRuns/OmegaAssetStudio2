namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Material-variant database schema. One JSON file at
//   %AppData%\OmegaAssetStudio\MaterialEditor\material_variants.db.json
// captures every parent material we've sampled plus the per-switch byte-
// deltas learned from comparing MICs that share that parent. SchemaVersion
// is bumped on incompatible field changes so the loader can refuse a stale
// file cleanly.
public sealed record MaterialVariantDatabase
{
    public int SchemaVersion { get; init; } = 1;
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public List<ParentMaterialEntry> Parents { get; init; } = new();
}

// A unique parent UMaterial. MICs derived from this parent share its shader
// permutation topology — switches just toggle between known compiled states.
public sealed record ParentMaterialEntry
{
    public Guid ParentId { get; init; }
    public string ParentName { get; init; } = "";
    public string ParentUpkPath { get; init; } = "";
    public MaterialBodySnapshot Baseline { get; init; } = new();
    public List<SwitchDeltaEntry> SwitchDeltas { get; init; } = new();
    // Switches we've observed in samples but whose toggle produced no
    // measurable byte change — flagged safe to flip with zero patch.
    public List<string> ZeroEffectSwitches { get; init; } = new();
    public int SampledMicCount { get; init; }
    public int DistinctPermutations { get; init; }
}

// Comparable snapshot of the relevant FMaterialResource fields. Derived
// from the existing UpkManager FMaterialResource parser — we don't walk
// raw bytes; the parser already exposes everything we need. Each numeric
// field becomes a candidate for delta computation.
public sealed record MaterialBodySnapshot
{
    public int NumTexCoords { get; init; }
    public int TextureCount { get; init; }              // UniformExpressionTextures
    public int LookupCount { get; init; }               // TextureLookups
    public uint UsingTransforms { get; init; }          // bitfield of which transforms are in use
    public int MaxTextureDependencyLength { get; init; }
    public bool UsesSceneColor { get; init; }
    public bool UsesSceneDepth { get; init; }
    public bool UsesDynamicParameter { get; init; }
    public bool UsesLightmapUVs { get; init; }
    public bool UsesMaterialVertexPositionOffset { get; init; }
    public int BlendModeValue { get; init; }
    public bool IsBlendModeOverridden { get; init; }
    public bool IsMaskedOverride { get; init; }
    // Resolved import paths of the textures so cross-UPK replay can match
    // names instead of stale indices (which differ between packages).
    public List<string> TexturePaths { get; init; } = new();
}

// What changes when a switch flips. Each field is a delta from baseline;
// adding the delta to a baseline snapshot reproduces the "switch on" body.
public sealed record SwitchDeltaEntry
{
    public string SwitchName { get; init; } = "";
    public int NumTexCoordsDelta { get; init; }
    public int TextureCountDelta { get; init; }
    public int LookupCountDelta { get; init; }
    public uint UsingTransformsXor { get; init; }       // XOR applied to UsingTransforms
    public int MaxTextureDependencyLengthDelta { get; init; }
    // Each bool gets a tri-state: null = no change, true = becomes true,
    // false = becomes false. Avoids ambiguity if a switch sets a bool
    // both ways in different samples (low confidence case).
    public bool? UsesSceneColorTo { get; init; }
    public bool? UsesSceneDepthTo { get; init; }
    public bool? UsesDynamicParameterTo { get; init; }
    public bool? UsesLightmapUVsTo { get; init; }
    public bool? UsesMaterialVertexPositionOffsetTo { get; init; }
    public int? BlendModeValueTo { get; init; }
    public bool? IsBlendModeOverriddenTo { get; init; }
    public bool? IsMaskedOverrideTo { get; init; }
    // Texture path lists added / removed by the switch (resolved at learn
    // time so the replay can match by name across packages).
    public List<string> TexturesAdded { get; init; } = new();
    public List<string> TexturesRemoved { get; init; } = new();
    public int SampleCount { get; init; }
    public double Confidence { get; init; }             // [0..1]
}

// One observation: a MIC's parsed body + which switches were on/off.
public sealed record CorpusSample
{
    public string UpkPath { get; init; } = "";
    public string ExportPath { get; init; } = "";
    public Guid ParentId { get; init; }
    public Dictionary<string, bool> SwitchValues { get; init; } = new();
    public MaterialBodySnapshot Snapshot { get; init; } = new();
}
