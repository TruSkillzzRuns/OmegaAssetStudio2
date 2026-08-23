using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Search;

// Reverse-lookup index: "which UPKs import this material/texture?"
// Builds by scanning every UPK's import table — imports are the
// cross-package references that name something defined elsewhere. Used by
// the editor to surface "this material is used by 47 other packages" so
// the user understands the blast radius of any rename / move / delete.
public sealed class CrossPackageMaterialSearch
{
    private readonly UpkFileRepository _repository = new();

    public sealed record IndexProgress(int ScannedUpks, int TotalUpks, string CurrentFile);
    public sealed record SearchResult(string ImportPath, IReadOnlyList<string> ReferencingUpks);

    // Map from "Group.Object" path → set of UPKs that import it. We build
    // it once per scan and let the UI ask for lookups; rebuilds are cheap
    // enough that we don't bother with incremental updates.
    public sealed class SearchIndex
    {
        public Dictionary<string, HashSet<string>> Imports { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ScannedUpks { get; init; }
        public DateTime BuiltUtc { get; init; } = DateTime.UtcNow;
    }

    public async Task<SearchIndex> BuildIndexAsync(
        string directory,
        IProgress<IndexProgress>? progress = null,
        CancellationToken ct = default)
    {
        var index = new SearchIndex();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return index;

        var upks = Directory.GetFiles(directory, "*.upk", SearchOption.AllDirectories);
        int scanned = 0;
        foreach (var upkPath in upks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexProgress(scanned, upks.Length, Path.GetFileName(upkPath)));
            try
            {
                var header = await _repository.LoadUpkFile(upkPath).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);
                foreach (var import in header.ImportTable)
                {
                    string path = import.GetPathName();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (!index.Imports.TryGetValue(path, out var set))
                        index.Imports[path] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(upkPath);
                }
            }
            catch { /* unreadable UPK — skip */ }
            scanned++;
        }
        return new SearchIndex { ScannedUpks = scanned, BuiltUtc = DateTime.UtcNow }
            .CopyImportsFrom(index);
    }

    // Substring / regex search over indexed import paths.
    public IReadOnlyList<SearchResult> Search(SearchIndex index, string query, bool isRegex = false)
    {
        if (index is null || string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchResult>();
        var results = new List<SearchResult>();
        System.Text.RegularExpressions.Regex? rx = null;
        if (isRegex)
        {
            try { rx = new System.Text.RegularExpressions.Regex(query, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return Array.Empty<SearchResult>(); }
        }
        foreach (var (path, upks) in index.Imports)
        {
            bool match = rx is not null ? rx.IsMatch(path) : path.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (match)
                results.Add(new SearchResult(path, upks.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return results.OrderByDescending(r => r.ReferencingUpks.Count).ToList();
    }
}

internal static class SearchIndexExtensions
{
    public static CrossPackageMaterialSearch.SearchIndex CopyImportsFrom(
        this CrossPackageMaterialSearch.SearchIndex dst,
        CrossPackageMaterialSearch.SearchIndex src)
    {
        foreach (var (k, v) in src.Imports) dst.Imports[k] = v;
        return dst;
    }
}
