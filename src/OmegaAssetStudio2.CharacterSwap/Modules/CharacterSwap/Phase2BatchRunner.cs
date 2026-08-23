using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// Folder-mode batch runner for Phase 2. Scans a source folder for character
// UPKs, pairs each with the same-named UPK in the target folder, and runs
// Phase2MaterialExtender on each pair. Output goes to the output folder with
// the source's filename. Per-file results are aggregated into one report.
//
// Use this to validate the pipeline across many characters at once. Each
// character class has its own Default__ surface area and may surface a new
// crash-causing source-only ref (the throwpower*components pattern we hit
// on Colossus). Batch mode makes those visible in a single run instead of
// one-character-at-a-time iteration.
//
// Pairing rules:
//   * Source folder is the "newer version" (e.g. 1.53 cooked-data folder)
//   * Target folder is the "older / live" version (e.g. 1.52 cooked-data folder)
//   * Match key = filename (case-insensitive). No glob translation.
//   * Optional filename prefix filter (default "UC__" matches the
//     UC__MarvelPlayer_<Hero>_<Costume>_SF.upk costume packages).
//
// Output safety:
//   * NEVER writes back to the target folder. Output folder MUST differ
//     from both source and target folders. Caller checks; runner re-checks.
//   * Output filename matches source filename exactly so the user can
//     copy the folder contents straight into the live game's
//     cooked-data folder.
public sealed class Phase2BatchRunner
{
    public sealed class PerFileResult
    {
        public string FileName { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public long OutputBytes { get; init; }
        public int NamesAdded { get; init; }
        public int ImportsAdded { get; init; }
        public int ExportsAdded { get; init; }
        public string? Error { get; init; }
        // First N notable warnings from this file's run (truncated to keep
        // the batch report scannable; the per-file UPK still gets every
        // warning when run individually via the single-file UI).
        public List<string> KeyWarnings { get; } = new();
        // Set when this file had no exact-name target twin but was rebuilt
        // using a sibling costume of the same hero as the chassis (e.g.
        // a 1.53 costume built on top of a 1.52 sibling). Reported separately
        // so it's obvious the output is a hybrid and may warrant extra
        // smoke-testing in-game.
        public string? SiblingChassisName { get; init; }
    }

    public sealed class BatchResult
    {
        public int TotalSourceFiles { get; init; }
        public int MatchedPairs { get; init; }
        public int SucceededFiles { get; init; }
        public int FailedFiles { get; init; }
        public List<PerFileResult> Files { get; init; } = new();
        public string Summary { get; init; } = string.Empty;
    }

    // Runs the batch. Caller supplies the same options as the single-file
    // path (matched-translate, merge-with-target) — they're applied
    // uniformly to every pair. `logProgress` fires once per file with a
    // short status string ("12/47 Storm_Asgardian"), useful for the
    // progress label in the UI.
    // Pattern detail: the costume UPK filename is
    //   UC__MarvelPlayer[Audio]_<Hero>_<Costume>_SF.upk
    // ParseCostumeFileName splits hero (first token) from costume (remaining
    // tokens, underscore-joined). Returns null on filenames that don't fit
    // the costume pattern (e.g. shared assets, plain prefix files).
    public static (string Hero, string Costume, string Prefix)? ParseCostumeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        string lower = fileName.ToLowerInvariant();
        string prefix;
        string body;
        // The two known prefixes in cooked-data folder are UC__MarvelPlayer_ and
        // UC__MarvelPlayerAudio_. Anything else doesn't follow the costume
        // pattern and isn't a sibling-chassis candidate.
        if (lower.StartsWith("uc__marvelplayeraudio_", StringComparison.Ordinal))
        {
            prefix = fileName.Substring(0, "UC__MarvelPlayerAudio_".Length);
            body = fileName.Substring("UC__MarvelPlayerAudio_".Length);
        }
        else if (lower.StartsWith("uc__marvelplayer_", StringComparison.Ordinal))
        {
            prefix = fileName.Substring(0, "UC__MarvelPlayer_".Length);
            body = fileName.Substring("UC__MarvelPlayer_".Length);
        }
        else return null;
        // Strip trailing _SF.upk (case insensitive)
        const string suffix = "_SF.upk";
        if (body.Length <= suffix.Length) return null;
        if (!body.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        body = body.Substring(0, body.Length - suffix.Length);
        int u = body.IndexOf('_');
        if (u <= 0 || u >= body.Length - 1) return null; // no costume token
        string hero = body.Substring(0, u);
        string costume = body.Substring(u + 1);
        return (hero, costume, prefix);
    }

    public async Task<BatchResult> RunAsync(
        string sourceFolder,
        string targetFolder,
        string outputFolder,
        string? filenamePrefixFilter,
        bool translateMatchedSizeChanged,
        bool mergeMatchedWithTarget,
        Action<string>? logProgress,
        bool useSiblingChassisFallback = true)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            throw new DirectoryNotFoundException($"Target folder not found: {targetFolder}");
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("Output folder must be set.");
        // Output safety: refuse if it equals source or target. Prevents an
        // accidental overwrite of the live game's cooked-data folder.
        string srcFull = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tgtFull = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string outFull = Path.GetFullPath(outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(outFull, srcFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output folder must differ from source folder.");
        if (string.Equals(outFull, tgtFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output folder must differ from target folder (never write over the live game).");
        Directory.CreateDirectory(outFull);

        string prefix = filenamePrefixFilter ?? string.Empty;
        var allSourceUpks = Directory
            .EnumerateFiles(srcFull, "*.upk", SearchOption.TopDirectoryOnly)
            .Where(p => string.IsNullOrEmpty(prefix) ||
                        Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build a target lookup by lowercased filename for O(1) pair-up.
        var targetByName = Directory
            .EnumerateFiles(tgtFull, "*.upk", SearchOption.TopDirectoryOnly)
            .ToDictionary(p => Path.GetFileName(p).ToLowerInvariant(), p => p, StringComparer.OrdinalIgnoreCase);

        // Sibling-chassis lookup: hero -> list of target .upk paths whose
        // filename parses as (hero, costume). Used by the fallback path
        // when a 1.53 source file has no exact-name 1.52 twin.
        Dictionary<string, List<string>> targetsByHero = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in targetByName.Values)
        {
            var parsed = ParseCostumeFileName(Path.GetFileName(p));
            if (parsed == null) continue;
            if (!targetsByHero.TryGetValue(parsed.Value.Hero, out var list))
            {
                list = new List<string>();
                targetsByHero[parsed.Value.Hero] = list;
            }
            list.Add(p);
        }

        var files = new List<PerFileResult>();
        int matchedCount = 0;
        int succeeded = 0;
        int failed = 0;
        int siblingChassisUsed = 0;
        int idx = 0;
        foreach (string srcPath in allSourceUpks)
        {
            idx++;
            string fname = Path.GetFileName(srcPath);
            string? tgtPath = null;
            string? siblingChassisName = null;
            IReadOnlyDictionary<string, string>? renameMap = null;
            if (!targetByName.TryGetValue(fname.ToLowerInvariant(), out tgtPath))
            {
                // No exact-name twin. Try sibling-chassis fallback: pick any
                // 1.52 target whose filename parses as same-hero, use it as
                // the chassis, and tell Phase 2 to rename the chassis class
                // entries to the source costume's class names so the output
                // loads under the source's costume slot in-game.
                var srcParsed = ParseCostumeFileName(fname);
                if (!useSiblingChassisFallback || srcParsed == null
                    || !targetsByHero.TryGetValue(srcParsed.Value.Hero, out var siblings)
                    || siblings.Count == 0)
                {
                    files.Add(new PerFileResult
                    {
                        FileName = fname,
                        Succeeded = false,
                        Error = useSiblingChassisFallback
                            ? "(no matching file in target folder AND no same-hero sibling found, skipped)"
                            : "(no matching file in target folder, sibling fallback disabled, skipped)",
                    });
                    continue;
                }
                // Pick the sibling with the longest common filename prefix
                // (most similar costume — heuristic but stable). Falls back
                // to whichever appears first if all ties.
                string bestSibling = siblings[0];
                int bestPrefix = -1;
                foreach (var s in siblings)
                {
                    int common = 0;
                    string sName = Path.GetFileName(s);
                    int n = Math.Min(sName.Length, fname.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (char.ToLowerInvariant(sName[i]) != char.ToLowerInvariant(fname[i])) break;
                        common++;
                    }
                    if (common > bestPrefix) { bestPrefix = common; bestSibling = s; }
                }
                tgtPath = bestSibling;
                siblingChassisName = Path.GetFileName(bestSibling);
                var sibParsed = ParseCostumeFileName(siblingChassisName)!;
                // Build the rename map. The class name pattern in source
                // costume UPKs is `MarvelPlayer_<Hero>_<Costume>` (and the
                // `Default__` variant). Renaming target's name-table entries
                // for these strings makes target's class export effectively
                // become the source's class slot at FName-resolution time.
                renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [$"MarvelPlayer_{sibParsed.Value.Hero}_{sibParsed.Value.Costume}"]
                        = $"MarvelPlayer_{srcParsed.Value.Hero}_{srcParsed.Value.Costume}",
                    [$"Default__MarvelPlayer_{sibParsed.Value.Hero}_{sibParsed.Value.Costume}"]
                        = $"Default__MarvelPlayer_{srcParsed.Value.Hero}_{srcParsed.Value.Costume}",
                };
                siblingChassisUsed++;
            }
            matchedCount++;
            logProgress?.Invoke(siblingChassisName != null
                ? $"[{idx}/{allSourceUpks.Count}] {fname} (sibling: {siblingChassisName})"
                : $"[{idx}/{allSourceUpks.Count}] {fname}");
            string outPath = Path.Combine(outFull, fname);

            try
            {
                var ex = new Phase2MaterialExtender();
                var result = await ex.ExecuteAsync(
                    srcPath, tgtPath, outPath,
                    log: null,  // suppress per-file UI log spam in batch mode
                    classAllowlist: null,
                    aliases: null,
                    translateMatchedSizeChanged: translateMatchedSizeChanged,
                    mergeMatchedWithTarget: mergeMatchedWithTarget,
                    targetNameRenames: renameMap);

                var perFile = new PerFileResult
                {
                    FileName = fname,
                    Succeeded = true,
                    OutputBytes = result.OutputBytes,
                    NamesAdded = result.NamesAdded,
                    ImportsAdded = result.ImportsAdded,
                    ExportsAdded = result.ExportsAdded,
                    SiblingChassisName = siblingChassisName,
                };
                // Surface the warning categories that historically predicted
                // crashes — !BROKEN markers, badNameIdx, matched-translate
                // failures, source-kept tags on Default__. The full per-file
                // detail still lives in `result.Summary` if needed; we just
                // don't paste every line into the batch report.
                foreach (string w in result.Issues)
                {
                    // Surface markers that predict a visually-degraded output:
                    //   * !BROKEN — a source FObject ref couldn't translate.
                    //   * badNameIdx — a property tag's name index is invalid in target.
                    //   * "translation failed" — Phase 2 fell back to target's body
                    //     for an export. If this is on `matched-skeletalmeshcomponent`,
                    //     the mesh pointer wasn't swapped → in-game shows 1.52 mesh.
                    //   * "value blob overruns" — bounds-check bailout from corrupted
                    //     source body (the fix added today). Same degradation risk.
                    //   * default__ + merge source tags — the surviving source-kept
                    //     properties on Default__; useful for spotting new
                    //     throwpower-style refs that need critical-prefer-target.
                    if (w.IndexOf("!BROKEN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        w.IndexOf("badNameIdx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        w.IndexOf("translation failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        w.IndexOf("overruns end", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (w.IndexOf("default__", StringComparison.OrdinalIgnoreCase) >= 0
                            && w.IndexOf("merge source tags", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        perFile.KeyWarnings.Add(w);
                    }
                }
                files.Add(perFile);
                succeeded++;
            }
            catch (Exception e)
            {
                // Capture top frames of the stack so the batch report can
                // pinpoint WHICH parser/translator path blew up across the
                // 600+ characters. Without this every exception looks
                // identical from the outside and we have to re-run each
                // failing file singly to get the trace.
                var top = new StringBuilder();
                top.Append($"{e.GetType().Name}: {e.Message}");
                if (!string.IsNullOrEmpty(e.StackTrace))
                {
                    var frames = e.StackTrace.Split('\n');
                    int take = Math.Min(5, frames.Length);
                    for (int fi = 0; fi < take; fi++)
                    {
                        string fr = frames[fi].TrimEnd();
                        if (fr.Length > 0) top.Append("\n        ").Append(fr);
                    }
                }
                files.Add(new PerFileResult
                {
                    FileName = fname,
                    Succeeded = false,
                    Error = top.ToString(),
                });
                failed++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Phase 2 batch complete.");
        sb.AppendLine($"  Source folder : {srcFull}");
        sb.AppendLine($"  Target folder : {tgtFull}");
        sb.AppendLine($"  Output folder : {outFull}");
        sb.AppendLine($"  Filename filter: {(string.IsNullOrEmpty(prefix) ? "(none)" : prefix + "*")}");
        sb.AppendLine();
        sb.AppendLine($"  Source UPKs scanned     : {allSourceUpks.Count}");
        sb.AppendLine($"  Matched pairs           : {matchedCount}");
        sb.AppendLine($"    of which sibling-chassis : {siblingChassisUsed}");
        sb.AppendLine($"  Succeeded               : {succeeded}");
        sb.AppendLine($"  Failed                  : {failed}");
        sb.AppendLine($"  Unmatched (skipped)     : {allSourceUpks.Count - matchedCount}");
        sb.AppendLine();

        // Split failures into:
        //   * unmatched (expected, no twin) — usually a flat list of names
        //   * real exceptions — with stack traces
        var unmatched = files.Where(f => !f.Succeeded && (f.Error ?? string.Empty).StartsWith("(no matching", StringComparison.OrdinalIgnoreCase)).ToList();
        var exceptions = files.Where(f => !f.Succeeded && !((f.Error ?? string.Empty).StartsWith("(no matching", StringComparison.OrdinalIgnoreCase))).ToList();
        if (exceptions.Count > 0)
        {
            sb.AppendLine($"-- Exceptions ({exceptions.Count}) --");
            foreach (var f in exceptions)
            {
                sb.AppendLine($"  {f.FileName}");
                sb.AppendLine($"    {f.Error}");
                sb.AppendLine();
            }
        }
        if (unmatched.Count > 0)
        {
            sb.AppendLine($"-- Unmatched source files ({unmatched.Count}, 1.53-only — not a failure) --");
            foreach (var f in unmatched)
                sb.AppendLine($"  {f.FileName}");
            sb.AppendLine();
        }

        // Surface files where the SkeletalMeshComponent mesh-pointer translation
        // failed — those load fine in-game but render the TARGET's old mesh,
        // not the source's new one. Visually they'll look like the 1.52
        // costume even though the file is "in" 1.53's slot. These are the
        // ones the user most needs to know about before smoke-testing.
        var visuallyDegraded = files.Where(f => f.Succeeded && f.KeyWarnings.Any(w =>
            (w.IndexOf("matched-skeletalmeshcomponent", StringComparison.OrdinalIgnoreCase) >= 0
             || w.IndexOf("matched-skeletalmesh", StringComparison.OrdinalIgnoreCase) >= 0)
            && (w.IndexOf("translation failed", StringComparison.OrdinalIgnoreCase) >= 0
                || w.IndexOf("overruns end", StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
        if (visuallyDegraded.Count > 0)
        {
            sb.AppendLine($"-- Likely visually degraded ({visuallyDegraded.Count}) — output loads but renders TARGET's 1.52 mesh, not source's 1.53 --");
            foreach (var f in visuallyDegraded)
            {
                sb.AppendLine($"  {f.FileName}");
                foreach (var w in f.KeyWarnings.Where(w =>
                    (w.IndexOf("matched-skeletalmesh", StringComparison.OrdinalIgnoreCase) >= 0)
                    && (w.IndexOf("translation failed", StringComparison.OrdinalIgnoreCase) >= 0
                        || w.IndexOf("overruns end", StringComparison.OrdinalIgnoreCase) >= 0)))
                    sb.AppendLine($"      ! {w}");
            }
            sb.AppendLine();
        }

        // Surface sibling-chassis builds before the regular successes — they're
        // the new files that previously couldn't be built at all, and they're
        // also the ones that most warrant in-game smoke-testing.
        var siblingBuilds = files.Where(f => f.Succeeded && f.SiblingChassisName != null).ToList();
        if (siblingBuilds.Count > 0)
        {
            sb.AppendLine($"-- Sibling-chassis builds ({siblingBuilds.Count}) — 1.53-only costumes built on a sibling 1.52 chassis --");
            foreach (var f in siblingBuilds)
            {
                sb.AppendLine($"  {f.FileName}");
                sb.AppendLine($"    chassis : {f.SiblingChassisName}");
                sb.AppendLine($"    stats   : +{f.NamesAdded}n +{f.ImportsAdded}i +{f.ExportsAdded}e  ({f.OutputBytes:N0} bytes)");
                foreach (var w in f.KeyWarnings)
                    sb.AppendLine($"      ! {w}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("-- Successes --");
        foreach (var f in files.Where(f => f.Succeeded && f.SiblingChassisName == null))
        {
            sb.AppendLine($"  {f.FileName,-60}  +{f.NamesAdded}n +{f.ImportsAdded}i +{f.ExportsAdded}e  ({f.OutputBytes:N0} bytes)");
            foreach (var w in f.KeyWarnings)
                sb.AppendLine($"      ! {w}");
        }

        return new BatchResult
        {
            TotalSourceFiles = allSourceUpks.Count,
            MatchedPairs = matchedCount,
            SucceededFiles = succeeded,
            FailedFiles = failed,
            Summary = sb.ToString(),
            Files = files,
        };
    }
}
