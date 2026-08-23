using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

// Builds source -> target lookup tables for the three reference systems a
// UE3 .upk file uses inside export bodies:
//   1. Name indexes (int32 into NameTable).
//   2. Import references (negative FObject: -idx-1 into ImportTable).
//   3. Export references (positive FObject: idx-1 into ExportTable).
//
// When an export body is copied from source to target without index
// translation, every embedded FName/FObject ends up pointing at the wrong
// row in the destination's tables. This class produces the mapping that a
// translator (PropertyTagRewriter, and future per-class re-serializers)
// applies to each embedded value.
//
// Translation failures (a source name not present in the target name table,
// a source object reference whose full path doesn't exist on the target side)
// are reported but do NOT throw. Callers decide what to do with them — for
// safe-set bisect we want to know which exports fail so we can skip them.
public sealed class IndexTranslator
{
    public UnrealHeader Source { get; }
    public UnrealHeader Target { get; }

    // src name index -> tgt name index. -1 means "not present in target's
    // name table" — body bytes referring to that index can't be translated.
    public int[] NameMap { get; }

    // src import index -> tgt FObject reference (negative int32 to be written
    // into the body), or 0 if the import doesn't exist on target's side.
    public int[] ImportMap { get; }

    // src export index -> tgt FObject reference (positive int32), or 0 if
    // no target export shares the source's full path name.
    public int[] ExportMap { get; }

    public List<string> NamesMissingFromTarget { get; } = new();
    public List<string> ImportsMissingFromTarget { get; } = new();
    public List<string> ExportsMissingFromTarget { get; } = new();

    // Optional alias map: source name/path component → target equivalent.
    // Used when source was re-parented to a class that doesn't exist in
    // target (e.g. AoA Colossus 1.53 references `marvelplayer_colossus_modern`
    // but target 1.52 only has `marvelplayer_colossus`). Aliases let us map
    // source's `default__marvelplayer_colossus_modern.initialskeletalmesh`
    // to target's `default__marvelplayer_colossus.initialskeletalmesh`
    // without having to re-author the source bytes.
    public IReadOnlyDictionary<string, string> Aliases { get; }

    public IndexTranslator(UnrealHeader source, UnrealHeader target)
        : this(source, target, null) { }

    public IndexTranslator(UnrealHeader source, UnrealHeader target, IReadOnlyDictionary<string, string>? aliases)
    {
        Source = source;
        Target = target;
        Aliases = aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        NameMap = BuildNameMap(source, target);
        ImportMap = BuildImportMap(source, target);
        ExportMap = BuildExportMap(source, target);
    }

    // Applies all configured aliases to a name or path. Substring substitution
    // — replaces every occurrence of each alias key with its value. Used both
    // for single names and for dotted import paths.
    public string ApplyAliases(string input)
    {
        if (string.IsNullOrEmpty(input) || Aliases.Count == 0) return input;
        string result = input;
        // CRITICAL: iterate aliases LONGEST KEY FIRST. Substring replacement
        // is greedy — if a shorter key (e.g. "chbasematerial_v2-1") runs
        // before a longer key that contains it ("chbasematerial_v2-1_skin"),
        // the short alias eats the substring and the longer alias never
        // matches. Empirically observed: a short non-skin alias was making
        // the skin-shader path translation fail entirely on certain costume pairs
        // even though the skin alias was correct. Sorting by descending key
        // length ensures the most-specific alias wins.
        foreach (var kv in Aliases.OrderByDescending(kv => kv.Key.Length))
            result = ReplaceCaseInsensitive(result, kv.Key, kv.Value);
        return result;
    }

    private static string ReplaceCaseInsensitive(string source, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue)) return source;
        int idx = source.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return source;
        var sb = new System.Text.StringBuilder(source.Length);
        int prev = 0;
        while (idx >= 0)
        {
            sb.Append(source, prev, idx - prev);
            sb.Append(newValue);
            prev = idx + oldValue.Length;
            idx = source.IndexOf(oldValue, prev, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(source, prev, source.Length - prev);
        return sb.ToString();
    }

    private int[] BuildNameMap(UnrealHeader src, UnrealHeader tgt)
    {
        // Target name -> first index. Names are case-insensitive in UE3.
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgt.NameTable.Count; i++)
        {
            string name = tgt.NameTable[i]?.Name?.String ?? string.Empty;
            if (!lookup.ContainsKey(name)) lookup[name] = i;
        }
        var map = new int[src.NameTable.Count];
        for (int i = 0; i < src.NameTable.Count; i++)
        {
            string name = src.NameTable[i]?.Name?.String ?? string.Empty;
            if (lookup.TryGetValue(name, out int j))
            {
                map[i] = j;
                continue;
            }
            // Alias fallback: maybe source's name (e.g. "marvelplayer_colossus_modern")
            // has an alias in target (e.g. "marvelplayer_colossus"). If so, point
            // source's name index at target's aliased entry.
            string aliased = ApplyAliases(name);
            if (!string.Equals(aliased, name, StringComparison.OrdinalIgnoreCase)
                && lookup.TryGetValue(aliased, out int aliasJ))
            {
                map[i] = aliasJ;
                continue;
            }
            map[i] = -1;
            NamesMissingFromTarget.Add(name);
        }
        return map;
    }

    private int[] BuildImportMap(UnrealHeader src, UnrealHeader tgt)
    {
        // Match imports by full dotted path. Import path = OuterPath + "." + ObjectName.
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgt.ImportTable.Count; i++)
        {
            string path = ImportFullPath(tgt, tgt.ImportTable[i]);
            if (!lookup.ContainsKey(path)) lookup[path] = -(i + 1); // negative = import FObject ref
        }
        var map = new int[src.ImportTable.Count];
        for (int i = 0; i < src.ImportTable.Count; i++)
        {
            string path = ImportFullPath(src, src.ImportTable[i]);
            if (lookup.TryGetValue(path, out int negRef))
            {
                map[i] = negRef;
                continue;
            }
            // Alias fallback (see BuildNameMap comment).
            string aliased = ApplyAliases(path);
            if (!string.Equals(aliased, path, StringComparison.OrdinalIgnoreCase)
                && lookup.TryGetValue(aliased, out int aliasNeg))
            {
                map[i] = aliasNeg;
                continue;
            }
            map[i] = 0;
            ImportsMissingFromTarget.Add(path);
        }
        return map;
    }

    private int[] BuildExportMap(UnrealHeader src, UnrealHeader tgt)
    {
        // Match exports by full path (same as import).
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgt.ExportTable.Count; i++)
        {
            string path = tgt.ExportTable[i].GetPathName();
            if (!lookup.ContainsKey(path)) lookup[path] = i + 1; // positive = export FObject ref
        }
        // Also build a target IMPORT lookup so we can cross-map: a source
        // EXPORT (own-package object) may correspond to a target IMPORT
        // (same object in a shared base UPK that the target references but
        // doesn't own). Critical for things like cross-version-renamed
        // shared base MICs (e.g. 1.53's `chbasematerial_v2-1_skin`
        // exported in source UPK == 1.52's `chbasematerial_v2_skin`
        // imported from the shared `chbasematerials_v2` package).
        var importLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgt.ImportTable.Count; i++)
        {
            string path = ImportFullPath(tgt, tgt.ImportTable[i]);
            if (!importLookup.ContainsKey(path)) importLookup[path] = -(i + 1);
        }
        var map = new int[src.ExportTable.Count];
        for (int i = 0; i < src.ExportTable.Count; i++)
        {
            string path = src.ExportTable[i].GetPathName();
            if (lookup.TryGetValue(path, out int posRef))
            {
                map[i] = posRef;
                continue;
            }
            // Cross-map: source export -> target import (with optional alias).
            if (importLookup.TryGetValue(path, out int negRef))
            {
                map[i] = negRef;
                continue;
            }
            string aliased = ApplyAliases(path);
            if (!string.Equals(aliased, path, StringComparison.OrdinalIgnoreCase))
            {
                if (lookup.TryGetValue(aliased, out int aliasedPos))
                {
                    map[i] = aliasedPos;
                    continue;
                }
                if (importLookup.TryGetValue(aliased, out int aliasedNeg))
                {
                    map[i] = aliasedNeg;
                    continue;
                }
            }
            map[i] = 0;
            ExportsMissingFromTarget.Add(path);
        }
        return map;
    }

    // FObject convention: 0 null, >0 export (idx-1), <0 import (-idx-1).
    // Returns the translated FObject reference, or 0 if the referenced object
    // has no equivalent in target.
    public int TranslateObjectReference(int srcRef)
    {
        if (srcRef == 0) return 0;
        if (srcRef > 0)
        {
            int idx = srcRef - 1;
            if (idx < 0 || idx >= ExportMap.Length) return 0;
            return ExportMap[idx];
        }
        else
        {
            int idx = -srcRef - 1;
            if (idx < 0 || idx >= ImportMap.Length) return 0;
            return ImportMap[idx];
        }
    }

    // Returns translated name index, or -1 if the source name doesn't exist
    // in target's name table.
    public int TranslateNameIndex(int srcNameIdx)
    {
        if (srcNameIdx < 0 || srcNameIdx >= NameMap.Length) return -1;
        return NameMap[srcNameIdx];
    }

    public string ResolveSourceName(int srcNameIdx)
    {
        if (srcNameIdx < 0 || srcNameIdx >= Source.NameTable.Count) return $"(bad#{srcNameIdx})";
        return Source.NameTable[srcNameIdx]?.Name?.String ?? "(null)";
    }

    private static string ImportFullPath(UnrealHeader header, UnrealImportTableEntry import)
    {
        // Walk outer chain via ObjectReference -> ObjectTableEntry. Mirrors
        // UnrealExportTableEntry.GetPathName().
        string name = import.ObjectNameIndex?.Name ?? "(noName)";
        int outerRef = import.OuterReference;
        if (outerRef == 0) return name;
        var outer = header.GetObjectTableEntry(outerRef);
        if (outer is null) return name;
        return ImportOuterPath(header, outer) + "." + name;
    }

    private static string ImportOuterPath(UnrealHeader header, UnrealObjectTableEntryBase entry)
    {
        if (entry is UnrealImportTableEntry imp)
        {
            string nm = imp.ObjectNameIndex?.Name ?? "(noName)";
            int outerRef = imp.OuterReference;
            if (outerRef == 0) return nm;
            var outer = header.GetObjectTableEntry(outerRef);
            return outer is null ? nm : ImportOuterPath(header, outer) + "." + nm;
        }
        if (entry is UnrealExportTableEntry exp)
        {
            return exp.GetPathName();
        }
        return "(?)";
    }
}
