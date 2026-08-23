using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Cross-UPK dependency inliner for Phase 2 character swap.
//
// Background: when Phase 2 transplants a costume from a newer-version source
// UPK (e.g. 1.53 UC__MarvelPlayer_<Hero>_<VariantA>_SF.upk) into an
// older-version target UPK (e.g. 1.52 UC__MarvelPlayer_<Hero>_<VariantB>_SF.upk),
// the source's import table references OTHER source-game UPKs (<Hero>_<VariantA>,
// <Hero>_<VariantC>, etc.) that 1.52 doesn't ship. IndexTranslator.BuildImportMap
// returns 0 for those — the engine then sees a null parent material / null
// AnimSet at load time, and the costume renders blue + T-poses.
//
// The inliner's job: instead of leaving those refs null, FETCH the referenced
// object from the source game's cooked-data folder and INLINE it into our
// output UPK as a new source-only export. Recursive (the inlined object's
// body has its own cross-UPK refs).
//
// This is the v1 skeleton — discovery + foreign-UPK loading only. No
// translation, no recursion, no output-table modification. The Phase 2
// orchestrator uses this to LOG which deps WOULD be inlined so we can verify
// path resolution against the a real cross-variant log before changing any output.
//
// Performance: loaded foreign UPK headers are cached for the session — every
// missing import sharing the same first dotted segment hits the cache.
public sealed class CrossUpkInliner
{
    private readonly string _sourceCookedFolder;
    private readonly UpkFileRepository _repo;
    private readonly Action<string>? _log;

    // Cache: foreign UPK file name (no extension, no path) -> loaded header.
    // Negative cache too — null entry means "we already tried and the file
    // doesn't exist", so repeated misses for the same package don't re-probe disk.
    private readonly Dictionary<string, UnrealHeader?> _foreignHeaderCache
        = new(StringComparer.OrdinalIgnoreCase);

    public sealed record ResolvedExport(
        UnrealHeader ForeignHeader,
        UnrealExportTableEntry Entry,
        string ForeignUpkName,
        string ExportPathInForeign);

    public CrossUpkInliner(string sourceCookedFolder, UpkFileRepository repo, Action<string>? log = null)
    {
        _sourceCookedFolder = sourceCookedFolder ?? throw new ArgumentNullException(nameof(sourceCookedFolder));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _log = log;
    }

    public int LoadedForeignUpkCount => _foreignHeaderCache.Values.Count(h => h != null);

    // Attempts to resolve a missing-import full path (e.g.
    // "<Hero>_<VariantA>.SkeletalMesh.RoadWornBody") to the actual export inside
    // the foreign UPK on disk. Returns null if the foreign UPK isn't present
    // or the export path can't be found inside it.
    public async Task<ResolvedExport?> TryResolveAsync(string missingImportPath)
    {
        if (string.IsNullOrWhiteSpace(missingImportPath)) return null;
        // Parse: leftmost dotted segment is the foreign UPK name.
        int firstDot = missingImportPath.IndexOf('.');
        if (firstDot <= 0) return null; // can't parse — not a package-qualified path
        string foreignUpkName = missingImportPath.Substring(0, firstDot);
        string pathInForeign = missingImportPath.Substring(firstDot + 1);

        var foreignHeader = await TryLoadForeignAsync(foreignUpkName).ConfigureAwait(false);
        if (foreignHeader == null) return null;

        // Walk the foreign UPK's export table to find an entry whose full
        // path matches pathInForeign. ExportTableEntry.GetPathName() returns
        // the full "Outer.Outer.Object" chain — same shape as our incoming
        // path-in-foreign value, so we compare directly.
        foreach (var ex in foreignHeader.ExportTable)
        {
            if (string.Equals(ex.GetPathName(), pathInForeign, StringComparison.OrdinalIgnoreCase))
                return new ResolvedExport(foreignHeader, ex, foreignUpkName, pathInForeign);
        }
        return null;
    }

    private async Task<UnrealHeader?> TryLoadForeignAsync(string foreignUpkName)
    {
        if (_foreignHeaderCache.TryGetValue(foreignUpkName, out var cached))
            return cached;

        string candidate = Path.Combine(_sourceCookedFolder, foreignUpkName + ".upk");
        if (!File.Exists(candidate))
        {
            _foreignHeaderCache[foreignUpkName] = null;
            _log?.Invoke($"CrossUpkInliner: foreign UPK NOT FOUND on disk: {candidate}");
            return null;
        }
        try
        {
            var header = await _repo.LoadUpkFile(candidate).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);
            _foreignHeaderCache[foreignUpkName] = header;
            _log?.Invoke($"CrossUpkInliner: loaded foreign UPK {foreignUpkName}.upk ({header.ExportTable.Count} exports)");
            return header;
        }
        catch (Exception ex)
        {
            _foreignHeaderCache[foreignUpkName] = null;
            _log?.Invoke($"CrossUpkInliner: failed to load {candidate}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
