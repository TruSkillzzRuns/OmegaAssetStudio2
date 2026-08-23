using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OmegaAssetStudio.WinUI.Services;

// Generic cross-package reference query, backed by the same SQLite index
// (Data/mh152upk.db) that TextureReferenceQueryService consults. Lets the
// user trace any export — material, mesh, particle system, animset, etc. —
// across the cooked content: who imports it, and where it lives.
//
// Schema (per UpkIndexingSystem.cs:20-50):
//   PackageImports (FullObjectPath, PackageName, ObjectName, ClassName, SourceUpkFile)
//   ObjectLocations(ObjectPath, UpkFileName, ExportIndex, FileSize)
//   ScannedFiles   (FileName, FileSize, LastScannedAt)
public sealed class ReferenceImportRow
{
    public string SourceUpkFile { get; init; } = string.Empty;
    public string FullObjectPath { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
}

public sealed class ReferenceExportRow
{
    public string UpkFileName { get; init; } = string.Empty;
    public string ObjectPath { get; init; } = string.Empty;
    public int ExportIndex { get; init; }
    public long FileSize { get; init; }
    // Best-effort class name resolved by cross-referencing
    // PackageImports.FullObjectPath where some other UPK imports this export.
    // Empty when nothing imports this export (orphan / power-fx / icon UPKs).
    public string ClassName { get; init; } = string.Empty;
}

public sealed class PackageReferenceResult
{
    // Set to the single token the service actually searched on, when the
    // strict AND-of-all-tokens query returned nothing and we narrowed to
    // just the longest token (usually the hero/proper-noun the user
    // cares about). Empty when AND succeeded as-is. UI surfaces this so
    // the user knows their literal phrase didn't hit but we showed them
    // the most useful subset instead.
    public string NarrowedToToken { get; init; } = string.Empty;

    public bool IndexAvailable { get; init; }
    public string IndexPath { get; init; } = string.Empty;
    public IReadOnlyList<ReferenceImportRow> Imports { get; init; } = Array.Empty<ReferenceImportRow>();
    public IReadOnlyList<ReferenceExportRow> Exports { get; init; } = Array.Empty<ReferenceExportRow>();
}

public static class PackageReferenceQueryService
{
    public static string GetDefaultIndexPath(string manifestFolder)
        => Path.Combine(manifestFolder, "Data", "mh152upk.db");

    public static bool IndexExists(string manifestFolder)
        => File.Exists(GetDefaultIndexPath(manifestFolder));

    // Split a free-form query into substring tokens. Cooked identifiers never
    // contain spaces, so "Hero Powers" or "Pet Summons" never literally
    // exists as one substring — but tokenising into ("Hero","Powers") and
    // AND-matching each token's %sub% catches "PowerHero_*", "Power_Hero_*",
    // etc. Tokens are de-duped and lowercased (LIKE is COLLATE NOCASE so the
    // case doesn't matter, but it keeps the param values stable).
    private static List<string> SplitTokens(string query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return tokens;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in query.Split(new[] { ' ', '\t', ',', ';' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.Trim();
            // Drop trailing 's' on tokens > 4 chars so "Powers" matches "Power_*"
            // and "Summons" matches "Summon*". Cheap, language-agnostic, and
            // it matches how the cooked schema uses singular tokens.
            if (t.Length > 4 && t.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - 1);
            if (t.Length == 0) continue;
            if (seen.Add(t)) tokens.Add(t);
        }
        return tokens;
    }

    // Builds an "AND" group for one column across N token params. Returns
    // SQL like  (col LIKE $t0 COLLATE NOCASE AND col LIKE $t1 COLLATE NOCASE)
    // The caller adds the parameters under names "$t0".."$tN".
    private static string BuildAndLike(string column, int tokenCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('(');
        for (int i = 0; i < tokenCount; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append(column).Append(" LIKE $t").Append(i).Append(" COLLATE NOCASE");
        }
        sb.Append(')');
        return sb.ToString();
    }

    // OR-flavored equivalent of BuildAndLike. Used as the fallback when the
    // strict AND query returns 0 rows: lets a casual query like "pet
    // summon" still surface every "pet*" identifier even when no row
    // literally contains both tokens.
    private static string BuildOrLike(string column, int tokenCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('(');
        for (int i = 0; i < tokenCount; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append(column).Append(" LIKE $t").Append(i).Append(" COLLATE NOCASE");
        }
        sb.Append(')');
        return sb.ToString();
    }

    // Encyclopedia-style autosuggest: return up to `max` distinct identifier
    // strings that contain `prefix` as a substring (case-insensitive), drawn
    // from every name-bearing column in the index PLUS the cooked dir's UPK
    // filenames. Used by the Reference Explorer search box so typing
    // a short query surfaces matching identifiers, per-power class names, costume
    // UPKs, etc. — without the user having to know the full identifier.
    public static IReadOnlyList<string> Suggest(string manifestFolder, string prefix, int max = 18)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim().Length < 2) return Array.Empty<string>();
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return Array.Empty<string>();
        var tokens = SplitTokens(prefix);
        if (tokens.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            // Union of every meaningful identifier column. ObjectName first
            // (tightest match) and so on. Each column is AND-ed across all
            // tokens — so "Hero Powers" requires both "Hero" AND "Power" to
            // appear in the candidate identifier.
            (string col, string table)[] cols = new[]
            {
                ("ObjectName",    "PackageImports"),
                ("PackageName",   "PackageImports"),
                ("ObjectPath",    "ObjectLocations"),
                ("UpkFileName",   "ObjectLocations"),
                ("SourceUpkFile", "PackageImports"),
            };
            // Pass 1: strict AND. Pass 2 (only if no results from pass 1):
            // broad OR. This is the casual-query saver — a query like "pet
            // summon" rarely has both tokens in a single identifier, but
            // either alone matches plenty.
            foreach (var (column, table) in cols)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT DISTINCT {column} AS s FROM {table} WHERE "
                                + BuildAndLike(column, tokens.Count)
                                + $" LIMIT {Math.Max(4, max)}";
                for (int i = 0; i < tokens.Count; i++)
                    cmd.Parameters.AddWithValue($"$t{i}", "%" + tokens[i] + "%");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    string s = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (seen.Add(s))
                    {
                        ordered.Add(s);
                        if (ordered.Count >= max) return ordered;
                    }
                }
            }
            // Narrow-to-longest fallback: same noob-saver as Find — retry
            // the suggestion using ONLY the longest token (usually the hero
            // name) so the dropdown surfaces the most relevant subset.
            if (ordered.Count == 0 && tokens.Count > 1)
            {
                string longest = tokens.OrderByDescending(t => t.Length).First();
                foreach (var (column, table) in cols)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT DISTINCT {column} AS s FROM {table} WHERE {column} LIKE $t COLLATE NOCASE LIMIT {Math.Max(4, max)}";
                    cmd.Parameters.AddWithValue("$t", "%" + longest + "%");
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0)) continue;
                        string s = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        if (seen.Add(s))
                        {
                            ordered.Add(s);
                            if (ordered.Count >= max) return ordered;
                        }
                    }
                }
            }
        }
        catch { /* return whatever we have */ }
        return ordered;
    }

    // Reverse lookup: given a set of FullObjectPath strings (typically the
    // targets of the imports we already displayed), find which UPKs actually
    // export those paths. Used by Reference Explorer to make the "Lives in"
    // panel reflect where the imports point — not just whatever happens to
    // match the search string textually.
    //
    // Chunked at 512 IN-clause items so we stay clear of SQLite's default
    // expression-tree limit while still doing it in 1–2 round trips for
    // typical (5000-row) import sets.
    public static IReadOnlyList<ReferenceExportRow> FindExportLocations(
        string manifestFolder, IEnumerable<string> fullObjectPaths, int perChunkLimit = 5000)
    {
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return Array.Empty<ReferenceExportRow>();
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in fullObjectPaths)
            if (!string.IsNullOrWhiteSpace(p)) distinct.Add(p);
        if (distinct.Count == 0) return Array.Empty<ReferenceExportRow>();

        var hits = new List<ReferenceExportRow>();
        var seenKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        const int chunkSize = 512;
        var paths = distinct.ToList();
        for (int start = 0; start < paths.Count; start += chunkSize)
        {
            int len = Math.Min(chunkSize, paths.Count - start);
            using SqliteCommand cmd = conn.CreateCommand();
            var sb = new System.Text.StringBuilder();
            sb.Append("SELECT UpkFileName, ObjectPath, ExportIndex, FileSize FROM ObjectLocations WHERE ObjectPath IN (");
            for (int i = 0; i < len; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('$').Append('p').Append(i);
                cmd.Parameters.AddWithValue($"$p{i}", paths[start + i]);
            }
            sb.Append(") COLLATE NOCASE LIMIT ").Append(perChunkLimit);
            cmd.CommandText = sb.ToString();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string upk = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string path = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                int idx = reader.IsDBNull(2) ? -1 : reader.GetInt32(2);
                long size = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                string key = upk + "|" + idx;
                if (!seenKey.Add(key)) continue;
                hits.Add(new ReferenceExportRow
                {
                    UpkFileName = upk,
                    ObjectPath = path,
                    ExportIndex = idx,
                    FileSize = size,
                });
            }
        }
        return hits;
    }

    public sealed class DependencyHit
    {
        public string UpkFileName { get; init; } = string.Empty;
        public int HopDepth { get; init; }
        // How many distinct objects from this UPK were referenced by the
        // crawl. Useful as a "weight" — heavily-shared shared/icon packages
        // sort high, single-prop UPKs sort low.
        public int ReferenceCount { get; init; }
    }

    // BFS the dependency graph from `startingUpkFile`. Each hop runs two
    // indexed queries:
    //   1) every FullObjectPath this UPK imports (from PackageImports)
    //   2) every UpkFileName that exports those paths (from ObjectLocations)
    // The second hop's UPKs become the next frontier. Hop cap + UPK cap are
    // hard bounds — the loop returns whatever it has if either trips.
    //
    // Result: deduped list of UPKs ordered by first-discovered hop, then by
    // descending ReferenceCount within a hop, so the heaviest hub UPKs
    // surface first per layer.
    public static IReadOnlyList<DependencyHit> FindAllDependencies(
        string manifestFolder, string startingUpkFile,
        int maxHops = 5, int maxUpks = 500)
    {
        if (string.IsNullOrWhiteSpace(startingUpkFile)) return Array.Empty<DependencyHit>();
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return Array.Empty<DependencyHit>();
        string startBase = Path.GetFileName(startingUpkFile);

        using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // Per-UPK: hop where we first discovered it + how many distinct
        // objects in this UPK we ended up touching.
        var byUpk = new Dictionary<string, (int Hop, int Refs)>(StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startBase };
        byUpk[startBase] = (0, 0);

        for (int hop = 1; hop <= maxHops && frontier.Count > 0 && byUpk.Count < maxUpks; hop++)
        {
            // Collect import paths from every UPK in the current frontier.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string upk in frontier)
            {
                using var imp = conn.CreateCommand();
                imp.CommandText = "SELECT FullObjectPath FROM PackageImports WHERE SourceUpkFile = $u COLLATE NOCASE";
                imp.Parameters.AddWithValue("$u", upk);
                using var r = imp.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    paths.Add(r.GetString(0));
                }
            }
            if (paths.Count == 0) break;

            // Look up each path's home UPK. Chunk the IN-clause at 512 to
            // stay well under SQLite's default expression-tree depth.
            var pathList = paths.ToList();
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const int chunk = 512;
            for (int start = 0; start < pathList.Count; start += chunk)
            {
                int len = Math.Min(chunk, pathList.Count - start);
                using var loc = conn.CreateCommand();
                var sb = new System.Text.StringBuilder();
                sb.Append("SELECT UpkFileName, ObjectPath FROM ObjectLocations WHERE ObjectPath IN (");
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('$').Append('p').Append(i);
                    loc.Parameters.AddWithValue($"$p{i}", pathList[start + i]);
                }
                sb.Append(") COLLATE NOCASE");
                loc.CommandText = sb.ToString();
                using var reader = loc.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    string home = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(home)) continue;
                    if (!byUpk.TryGetValue(home, out var existing))
                    {
                        if (byUpk.Count >= maxUpks) continue;
                        byUpk[home] = (hop, 1);
                        nextFrontier.Add(home);
                    }
                    else
                    {
                        byUpk[home] = (existing.Hop, existing.Refs + 1);
                    }
                }
            }
            frontier = nextFrontier;
        }

        // Drop the starting UPK from the output — the caller already knows it.
        byUpk.Remove(startBase);

        return byUpk
            .Select(kv => new DependencyHit
            {
                UpkFileName = kv.Key,
                HopDepth = kv.Value.Hop,
                ReferenceCount = kv.Value.Refs,
            })
            .OrderBy(d => d.HopDepth)
            .ThenByDescending(d => d.ReferenceCount)
            .ThenBy(d => d.UpkFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Distinct class names actually present in the index. Used by the UI to
    // populate the class-filter dropdown so it always matches reality.
    public static IReadOnlyList<string> ListClassNames(string manifestFolder, int max = 256)
    {
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return Array.Empty<string>();
        List<string> names = new();
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT ClassName, COUNT(*) AS Hits
                FROM PackageImports
                WHERE ClassName IS NOT NULL AND ClassName <> ''
                GROUP BY ClassName
                ORDER BY Hits DESC
                LIMIT {max}";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        catch { }
        return names;
    }

    // Imports: every UPK that references the given object name. Class filter
    // optional. Limited to keep the UI responsive against the 1.07M-row table.
    public static PackageReferenceResult Find(
        string manifestFolder,
        string objectName,
        string? classFilter = null,
        int rowLimit = 500)
    {
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath))
            return new PackageReferenceResult { IndexAvailable = false, IndexPath = dbPath };

        var tokens = SplitTokens(objectName);
        if (tokens.Count == 0) tokens.Add(objectName ?? string.Empty);

        List<ReferenceImportRow> imports = new();
        List<ReferenceExportRow> exports = new();

        using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // Imports: who references this object?
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            string classClause = string.IsNullOrWhiteSpace(classFilter)
                ? string.Empty
                : " AND ClassName = $cls COLLATE NOCASE";
            // Every input token must match SOMEWHERE — ObjectName OR
            // PackageName — so "Hero Powers" becomes
            //   (ObjectName LIKE %Hero% OR PackageName LIKE %Hero%)
            //   AND (ObjectName LIKE %Power% OR PackageName LIKE %Power%)
            var perTokenSql = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) perTokenSql.Append(" AND ");
                perTokenSql.Append("(ObjectName LIKE $t").Append(i)
                           .Append(" COLLATE NOCASE OR PackageName LIKE $t").Append(i)
                           .Append(" COLLATE NOCASE)");
            }
            cmd.CommandText = $@"
                SELECT SourceUpkFile, FullObjectPath, ClassName
                FROM PackageImports
                WHERE {perTokenSql}{classClause}
                ORDER BY SourceUpkFile
                LIMIT {rowLimit}";
            for (int i = 0; i < tokens.Count; i++)
                cmd.Parameters.AddWithValue($"$t{i}", "%" + tokens[i] + "%");
            if (!string.IsNullOrWhiteSpace(classFilter))
                cmd.Parameters.AddWithValue("$cls", classFilter);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                imports.Add(new ReferenceImportRow
                {
                    SourceUpkFile = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    FullObjectPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ClassName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                });
            }
        }

        // Exports: where does it actually live? We deliberately do NOT fetch
        // the export's ClassName here — PackageImports.FullObjectPath isn't
        // indexed in the shipped DB, so a correlated subquery against it
        // forces a per-row table scan over 1M+ import rows. The caller
        // classifies rows via the ObjectPath suffix + UPK filename, which is
        // fast and good enough for the tool-pill heuristic. Live-read UPK
        // results carry the real class (UnrealHeader gives it for free).
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            var perTokenSql = new System.Text.StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) perTokenSql.Append(" AND ");
                perTokenSql.Append("(ObjectPath LIKE $t").Append(i)
                           .Append(" COLLATE NOCASE OR UpkFileName LIKE $t").Append(i)
                           .Append(" COLLATE NOCASE)");
            }
            cmd.CommandText = $@"
                SELECT UpkFileName, ObjectPath, ExportIndex, FileSize
                FROM ObjectLocations
                WHERE {perTokenSql}
                ORDER BY UpkFileName
                LIMIT {rowLimit}";
            for (int i = 0; i < tokens.Count; i++)
                cmd.Parameters.AddWithValue($"$t{i}", "%" + tokens[i] + "%");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                exports.Add(new ReferenceExportRow
                {
                    UpkFileName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    ObjectPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ExportIndex = reader.IsDBNull(2) ? -1 : reader.GetInt32(2),
                    FileSize = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                });
            }
        }

        // Narrow-to-longest fallback: when the strict AND-of-tokens returned
        // nothing AND the user typed multiple words, retry using ONLY the
        // longest token. Proper nouns (character names) are usually the
        // longest and represent what the user actually wants to drill into
        // — so a two-word query becomes its longer token. Avoids the OR
        // flood that would surface every identifier matching ANY token.
        string narrowedTo = string.Empty;
        if (tokens.Count > 1 && imports.Count == 0 && exports.Count == 0)
        {
            string longest = tokens.OrderByDescending(t => t.Length).First();
            narrowedTo = longest;
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                string classClause = string.IsNullOrWhiteSpace(classFilter)
                    ? string.Empty
                    : " AND ClassName = $cls COLLATE NOCASE";
                cmd.CommandText = $@"
                    SELECT SourceUpkFile, FullObjectPath, ClassName
                    FROM PackageImports
                    WHERE (ObjectName LIKE $t COLLATE NOCASE OR PackageName LIKE $t COLLATE NOCASE){classClause}
                    ORDER BY SourceUpkFile
                    LIMIT {rowLimit}";
                cmd.Parameters.AddWithValue("$t", "%" + longest + "%");
                if (!string.IsNullOrWhiteSpace(classFilter))
                    cmd.Parameters.AddWithValue("$cls", classFilter);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    imports.Add(new ReferenceImportRow
                    {
                        SourceUpkFile = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                        FullObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                        ClassName = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                    });
                }
            }
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT UpkFileName, ObjectPath, ExportIndex, FileSize
                    FROM ObjectLocations
                    WHERE (ObjectPath LIKE $t COLLATE NOCASE OR UpkFileName LIKE $t COLLATE NOCASE)
                    ORDER BY UpkFileName
                    LIMIT {rowLimit}";
                cmd.Parameters.AddWithValue("$t", "%" + longest + "%");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    exports.Add(new ReferenceExportRow
                    {
                        UpkFileName = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                        ObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                        ExportIndex = r.IsDBNull(2) ? -1 : r.GetInt32(2),
                        FileSize = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    });
                }
            }
        }

        return new PackageReferenceResult
        {
            IndexAvailable = true,
            IndexPath = dbPath,
            Imports = imports,
            Exports = exports,
            NarrowedToToken = narrowedTo,
        };
    }

    // ----- UPK-centric query --------------------------------------------
    //
    // When the user is looking at a UPK rather than a single object, the
    // object-name search above returns nothing useful (no object literally
    // named e.g. "UC__PowerHero_Ability_SF"). FindUpk centers on the UPK
    // file name and pulls three relationship sets:
    //
    //   Hosted          — every object exported BY this UPK (rows from
    //                     ObjectLocations).
    //   ImportsFromOther— UPKs that this UPK depends on (rows from
    //                     PackageImports WHERE SourceUpkFile = upk; the
    //                     hosting UPK of each imported object is the link).
    //   ImportedByOther — UPKs that depend on this UPK (every object hosted
    //                     by this UPK, then PackageImports rows targeting
    //                     those objects — group by SourceUpkFile).
    //
    // Cheap to compute thanks to the existing indices; the rowLimit caps
    // each individual query.
    public sealed class UpkRelationshipResult
    {
        public bool IndexAvailable { get; init; }
        public string IndexPath { get; init; } = string.Empty;
        public string UpkFileName { get; init; } = string.Empty;
        public IReadOnlyList<ReferenceExportRow> Hosted { get; init; } = Array.Empty<ReferenceExportRow>();
        // UpkFileName here is the SOURCE of the import; ObjectPath is the
        // dependency object's full path; ClassName carries the dep's class.
        public IReadOnlyList<ReferenceImportRow> ImportsFromOther { get; init; } = Array.Empty<ReferenceImportRow>();
        public IReadOnlyList<ReferenceImportRow> ImportedByOther { get; init; } = Array.Empty<ReferenceImportRow>();
    }

    public static UpkRelationshipResult FindUpk(
        string manifestFolder,
        string upkFileName,
        int rowLimit = 5000)
    {
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath))
            return new UpkRelationshipResult { IndexAvailable = false, IndexPath = dbPath, UpkFileName = upkFileName };

        string normalized = upkFileName;
        if (!normalized.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
            normalized += ".upk";

        var hosted = new List<ReferenceExportRow>();
        var importsFromOther = new List<ReferenceImportRow>();
        var importedByOther = new List<ReferenceImportRow>();

        using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // 1. Hosted exports.
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT UpkFileName, ObjectPath, ExportIndex, FileSize
                FROM ObjectLocations
                WHERE UpkFileName = $upk COLLATE NOCASE
                ORDER BY ObjectPath
                LIMIT {rowLimit}";
            cmd.Parameters.AddWithValue("$upk", normalized);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                hosted.Add(new ReferenceExportRow
                {
                    UpkFileName = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    ObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    ExportIndex = r.IsDBNull(2) ? -1 : r.GetInt32(2),
                    FileSize = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                });
            }
        }

        // 2. What this UPK imports from elsewhere.
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT SourceUpkFile, FullObjectPath, ClassName, PackageName
                FROM PackageImports
                WHERE SourceUpkFile = $upk COLLATE NOCASE
                ORDER BY PackageName, ClassName
                LIMIT {rowLimit}";
            cmd.Parameters.AddWithValue("$upk", normalized);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                // For "imports from other UPKs" we store the SOURCE PackageName
                // in SourceUpkFile so the renderer can group by the dependency
                // UPK directly. PackageName is roughly the base filename of
                // the host UPK that owns the imported object.
                string srcPackage = r.IsDBNull(3) ? string.Empty : r.GetString(3);
                if (!srcPackage.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) && srcPackage.Length > 0)
                    srcPackage += ".upk";
                importsFromOther.Add(new ReferenceImportRow
                {
                    SourceUpkFile = srcPackage,
                    FullObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    ClassName = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                });
            }
        }

        // 3. What other UPKs import from this UPK. We need the basename
        //    (without extension) because PackageImports.PackageName uses
        //    that form. Match against PackageName.
        string baseName = Path.GetFileNameWithoutExtension(normalized);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT SourceUpkFile, FullObjectPath, ClassName
                FROM PackageImports
                WHERE PackageName = $base COLLATE NOCASE
                  AND SourceUpkFile <> $upk COLLATE NOCASE
                ORDER BY SourceUpkFile
                LIMIT {rowLimit}";
            cmd.Parameters.AddWithValue("$base", baseName);
            cmd.Parameters.AddWithValue("$upk", normalized);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                importedByOther.Add(new ReferenceImportRow
                {
                    SourceUpkFile = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    FullObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    ClassName = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                });
            }
        }

        return new UpkRelationshipResult
        {
            IndexAvailable = true,
            IndexPath = dbPath,
            UpkFileName = normalized,
            Hosted = hosted,
            ImportsFromOther = importsFromOther,
            ImportedByOther = importedByOther,
        };
    }

    // Resolve the cooked UPK that actually HOSTS a given object (by leaf name),
    // using the export index. Returns the UPK file name (e.g. "MarvelGame.upk")
    // or null if not found. Cheap single-row query — no UPK file is opened.
    // Used to tell the user where a cross-package shared material really lives
    // when it's cooked into a differently-named master package.
    public static string? ResolveHostUpkFileName(string manifestFolder, string objectLeafName)
    {
        if (string.IsNullOrWhiteSpace(objectLeafName)) return null;
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return null;
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            // Match the exact leaf: ObjectPath ends in ".<name>", or equals it.
            cmd.CommandText = @"
                SELECT UpkFileName FROM ObjectLocations
                WHERE ObjectPath = $n COLLATE NOCASE OR ObjectPath LIKE $suffix COLLATE NOCASE
                LIMIT 1";
            cmd.Parameters.AddWithValue("$n", objectLeafName);
            cmd.Parameters.AddWithValue("$suffix", "%." + objectLeafName);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    // ----- Keyword search for recolorable color targets -----------------
    //
    // Powers the Skill Recolor "Find effect by name" feature: given a keyword
    // (e.g. "lightning", "chain"), return distinct material / particle-system
    // exports whose path matches, classified by the cooked path group folder
    // (.materials. → Material, .particles. → ParticleSystem). DistinctHostCount
    // tells how many UPKs carry the object — a high count means it's a SHARED /
    // global asset (recoloring it changes every skill that uses it). SampleHost
    // is one UPK that contains it (preferring a non-master, non-level package so
    // the recolor opens something tractable rather than MarvelGame.upk).
    public sealed class ColorTargetRow
    {
        public string ObjectLeaf { get; init; } = string.Empty;
        public string ObjectPath { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;          // "Material" / "ParticleSystem" / "Other"
        public int DistinctHostCount { get; init; }
        public string SampleHostUpk { get; init; } = string.Empty;
        public bool IsShared => DistinctHostCount > 3;
    }

    public static IReadOnlyList<ColorTargetRow> SearchColorTargets(string manifestFolder, string keyword, int limit = 200)
    {
        var rows = new List<ColorTargetRow>();
        if (string.IsNullOrWhiteSpace(keyword)) return rows;
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return rows;
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            // Restrict to color-bearing groups via the path: cooked content keeps
            // materials under ".materials." and particle systems under ".particles.".
            cmd.CommandText = $@"
                SELECT ObjectPath,
                       COUNT(DISTINCT UpkFileName) AS hosts,
                       MIN(UpkFileName)            AS sampleHost
                FROM ObjectLocations
                WHERE ObjectPath LIKE $kw COLLATE NOCASE
                  AND (ObjectPath LIKE '%.materials.%' OR ObjectPath LIKE '%.particles.%')
                GROUP BY ObjectPath
                ORDER BY hosts DESC, ObjectPath
                LIMIT {limit}";
            cmd.Parameters.AddWithValue("$kw", "%" + keyword + "%");
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string path = r.IsDBNull(0) ? string.Empty : r.GetString(0);
                int hosts = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                string sample = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                int dot = path.LastIndexOf('.');
                string leaf = dot >= 0 && dot + 1 < path.Length ? path[(dot + 1)..] : path;
                string kind = path.Contains(".particles.", StringComparison.OrdinalIgnoreCase) ? "ParticleSystem"
                            : path.Contains(".materials.", StringComparison.OrdinalIgnoreCase) ? "Material"
                            : "Other";
                rows.Add(new ColorTargetRow
                {
                    ObjectLeaf = leaf,
                    ObjectPath = path,
                    Kind = kind,
                    DistinctHostCount = hosts,
                    SampleHostUpk = sample,
                });
            }
        }
        catch { }
        return rows;
    }

    // For a chosen color target, list the actual cooked UPK files that contain
    // it, smallest first (so the recolor opens the most tractable host rather
    // than MarvelGame.upk). Used after the user picks a search result.
    public static IReadOnlyList<ReferenceExportRow> ListHostsForObject(string manifestFolder, string objectPath, int limit = 50)
    {
        var rows = new List<ReferenceExportRow>();
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath) || string.IsNullOrWhiteSpace(objectPath)) return rows;
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT UpkFileName, ObjectPath, ExportIndex, FileSize
                FROM ObjectLocations
                WHERE ObjectPath = $p COLLATE NOCASE
                ORDER BY FileSize ASC
                LIMIT {limit}";
            cmd.Parameters.AddWithValue("$p", objectPath);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new ReferenceExportRow
                {
                    UpkFileName = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    ObjectPath = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    ExportIndex = r.IsDBNull(2) ? -1 : r.GetInt32(2),
                    FileSize = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                });
        }
        catch { }
        return rows;
    }

    // Counts only — fast path for "how widely referenced is this?" without
    // pulling row data.
    public static (int imports, int exports) Counts(string manifestFolder, string objectName, string? classFilter = null)
    {
        string dbPath = GetDefaultIndexPath(manifestFolder);
        if (!File.Exists(dbPath)) return (0, 0);
        int imp = 0, exp = 0;
        try
        {
            using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                string classClause = string.IsNullOrWhiteSpace(classFilter) ? string.Empty
                    : " AND ClassName = $cls COLLATE NOCASE";
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM PackageImports
                    WHERE (ObjectName LIKE $name COLLATE NOCASE
                           OR PackageName LIKE $name COLLATE NOCASE){classClause}";
                cmd.Parameters.AddWithValue("$name", "%" + objectName + "%");
                if (!string.IsNullOrWhiteSpace(classFilter))
                    cmd.Parameters.AddWithValue("$cls", classFilter);
                imp = System.Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM ObjectLocations
                    WHERE (ObjectPath LIKE $name COLLATE NOCASE
                           OR UpkFileName LIKE $name COLLATE NOCASE)";
                cmd.Parameters.AddWithValue("$name", "%" + objectName + "%");
                exp = System.Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
        }
        catch { }
        return (imp, exp);
    }
}
