using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Walks a directory of UPKs (typically cooked-data folder), finds every
// MaterialInstanceConstant export, parses it, and emits CorpusSamples
// grouped by parent material. The grouped samples are then handed to
// MaterialVariantLearner to extract switch deltas.
//
// Designed to run once per game-version on the user's machine. Persistence
// of the resulting database is handled by MaterialVariantStore; this class
// is purely the scan + group + learn pipeline.
public sealed class MaterialVariantCorpusScanner
{
    private readonly UpkFileRepository _repository = new();

    public sealed record ScanProgress(int ScannedUpks, int TotalUpks, int FoundMics, string CurrentFile);
    public sealed record ScanResult(MaterialVariantDatabase Database, int ScannedUpks, int FoundMics, List<string> Errors);

    public async Task<ScanResult> ScanDirectoryAsync(
        string directory,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            errors.Add($"directory not found: {directory}");
            return new ScanResult(new(), 0, 0, errors);
        }

        var upks = Directory.GetFiles(directory, "*.upk", SearchOption.AllDirectories);
        var samplesByParent = new Dictionary<Guid, List<CorpusSample>>();
        int scanned = 0;
        int micCount = 0;

        foreach (var upkPath in upks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(scanned, upks.Length, micCount, Path.GetFileName(upkPath)));
            try
            {
                var found = await ScanOneUpkAsync(upkPath, ct).ConfigureAwait(false);
                foreach (var sample in found)
                {
                    if (!samplesByParent.TryGetValue(sample.ParentId, out var list))
                        samplesByParent[sample.ParentId] = list = new();
                    list.Add(sample);
                    micCount++;
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(upkPath)}: {ex.GetType().Name}: {ex.Message}"); }
            scanned++;
        }
        progress?.Report(new ScanProgress(scanned, upks.Length, micCount, "(learning deltas)"));

        // Pass 2: per-parent, run every (off, on) pair through the learner
        // and merge results. We keep one accumulator per (parentId, switchName).
        var parents = new List<ParentMaterialEntry>(samplesByParent.Count);
        foreach (var (parentId, samples) in samplesByParent)
        {
            ct.ThrowIfCancellationRequested();
            var deltasByName = new Dictionary<string, SwitchDeltaEntry>(StringComparer.OrdinalIgnoreCase);
            var zeroEffect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < samples.Count; i++)
            for (int j = i + 1; j < samples.Count; j++)
            {
                var a = samples[i]; var b = samples[j];
                // Try both directions — the differing switch determines which is "off".
                foreach (var (off, on) in new[] { (a, b), (b, a) })
                {
                    var result = MaterialVariantLearner.ComparePair(off, on);
                    if (!result.Compatible || result.Delta is null) continue;
                    // Zero-effect: snapshot is identical despite the switch flip.
                    if (IsZeroEffect(result.Delta))
                    {
                        zeroEffect.Add(result.DifferingSwitch!);
                        continue;
                    }
                    if (deltasByName.TryGetValue(result.DifferingSwitch!, out var existing))
                        deltasByName[result.DifferingSwitch!] = MaterialVariantLearner.Merge(existing, result.Delta);
                    else
                        deltasByName[result.DifferingSwitch!] = result.Delta;
                    break; // counted once per pair
                }
            }

            // Pick the lowest-permutation sample as the canonical baseline —
            // typically the one with all switches off.
            var baselineSample = samples
                .OrderBy(s => s.SwitchValues.Values.Count(v => v))
                .First();

            parents.Add(new ParentMaterialEntry
            {
                ParentId = parentId,
                ParentName = "", // unknown without cross-package resolve; UI can fill later
                ParentUpkPath = "",
                Baseline = baselineSample.Snapshot,
                SwitchDeltas = deltasByName.Values
                    .OrderByDescending(d => d.Confidence)
                    .ThenByDescending(d => d.SampleCount)
                    .ToList(),
                ZeroEffectSwitches = zeroEffect.ToList(),
                SampledMicCount = samples.Count,
                DistinctPermutations = samples
                    .Select(s => string.Join(",", s.SwitchValues.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")))
                    .Distinct()
                    .Count(),
            });
        }

        var db = new MaterialVariantDatabase
        {
            SchemaVersion = 1,
            GeneratedUtc = DateTime.UtcNow,
            Parents = parents.OrderByDescending(p => p.SampledMicCount).ToList(),
        };
        return new ScanResult(db, scanned, micCount, errors);
    }

    // Parses one UPK, returns every MIC export's CorpusSample. Skips
    // unreadable UPKs (mirrors the rest of the codebase's swallow-and-log
    // pattern so a single broken package can't abort the whole scan).
    private async Task<List<CorpusSample>> ScanOneUpkAsync(string upkPath, CancellationToken ct)
    {
        var result = new List<CorpusSample>();
        UnrealHeader header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);

        foreach (var export in header.ExportTable)
        {
            ct.ThrowIfCancellationRequested();
            string className = export.ClassReferenceNameIndex?.Name ?? "";
            if (!className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (export.UnrealObject is null)
                {
                    await header.ReadExportObjectAsync(export, null).ConfigureAwait(false);
                    await export.ParseUnrealObject(false, false).ConfigureAwait(false);
                }
                if (export.UnrealObject is not IUnrealObject uo) continue;
                if (uo.UObject is not UMaterialInstanceConstant mic) continue;

                // Need a parent GUID — derived from the first StaticParameterSet
                // since that's where the parent material's identity is recorded.
                Guid parentId = mic.StaticParameters is { Length: > 0 } sp && sp[0]?.BaseMaterialId is not null
                    ? sp[0].BaseMaterialId.ToSystemGuid()
                    : Guid.Empty;
                if (parentId == Guid.Empty) continue; // no baseline to anchor against

                var snapshot = MaterialBodyReader.FromMaterialInstance(mic);
                var switches = MaterialBodyReader.ReadSwitchValues(mic);

                result.Add(new CorpusSample
                {
                    UpkPath = upkPath,
                    ExportPath = export.GetPathName(),
                    ParentId = parentId,
                    SwitchValues = switches,
                    Snapshot = snapshot,
                });
            }
            catch { /* per-export parse failures don't abort the upk */ }
        }
        return result;
    }

    private static bool IsZeroEffect(SwitchDeltaEntry d) =>
        d.NumTexCoordsDelta == 0 &&
        d.TextureCountDelta == 0 &&
        d.LookupCountDelta == 0 &&
        d.UsingTransformsXor == 0 &&
        d.MaxTextureDependencyLengthDelta == 0 &&
        d.UsesSceneColorTo is null &&
        d.UsesSceneDepthTo is null &&
        d.UsesDynamicParameterTo is null &&
        d.UsesLightmapUVsTo is null &&
        d.UsesMaterialVertexPositionOffsetTo is null &&
        d.BlendModeValueTo is null &&
        d.IsBlendModeOverriddenTo is null &&
        d.IsMaskedOverrideTo is null &&
        d.TexturesAdded.Count == 0 &&
        d.TexturesRemoved.Count == 0;
}
