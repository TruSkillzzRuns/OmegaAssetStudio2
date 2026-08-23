using System;
using System.Collections.Generic;
using System.IO;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

// Specialised merger for MaterialInstanceConstant parameter arrays.
//
// MIC parameter arrays (ScalarParameterValues, VectorParameterValues,
// TextureParameterValues, FontParameterValues, StaticParameterValues) hold
// per-parameter overrides applied on top of the parent material. Each item
// is a tagged struct containing ParameterName + ParameterValue (+ a few
// other fields). Items are keyed by ParameterName at runtime.
//
// Naïve full-array replacement (source's items replace target's) breaks the
// cross-version transplant: target's MIC ships with a precomputed shader
// resource (bhasstaticpermutationresource=true) compiled against target's
// specific parameter values. Removing target's items leaves the precomputed
// shader misaligned → renders white.
//
// Item-level merge instead:
//   - Items in target only → kept.
//   - Items in source only (new parameters) → added.
//   - Items in both (same ParameterName) → source's wins (the actual
//     visual upgrade).
// This preserves target's params that the precomputed shader needs while
// letting source's new/changed params take effect.
internal static class MicParameterArrayMerger
{
    private static readonly HashSet<string> MicParameterArrayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "scalarparametervalues",
        "vectorparametervalues",
        "textureparametervalues",
        "fontparametervalues",
        "staticparametervalues",
    };

    public static bool IsMicParameterArray(string tagName) => MicParameterArrayNames.Contains(tagName);

    // Returns the merged ArrayProperty value bytes (count int32 + items
    // back-to-back). Returns null if either side's bytes can't be parsed
    // safely — caller should fall back to a non-item-level strategy.
    public static byte[]? TryMerge(
        byte[] sourceTranslatedBytes,
        int sourceValueOffset,
        int sourceValueLen,
        byte[] targetBytes,
        int targetValueOffset,
        int targetValueLen,
        IReadOnlyList<UnrealNameTableEntry> tgtNameTable)
    {
        var srcItems = ParseArrayItems(sourceTranslatedBytes, sourceValueOffset, sourceValueLen, tgtNameTable);
        var tgtItems = ParseArrayItems(targetBytes, targetValueOffset, targetValueLen, tgtNameTable);
        if (srcItems == null || tgtItems == null) return null;

        // Build target lookup so we can decide whether each source item's
        // override is safe to take. Walk target first to preserve item order.
        var srcByName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in srcItems)
            if (!srcByName.ContainsKey(name)) srcByName[name] = bytes;

        var merged = new List<byte[]>();
        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, tBytes) in tgtItems)
        {
            if (addedNames.Contains(name)) continue;
            if (srcByName.TryGetValue(name, out byte[]? sBytes))
            {
                // Both sides have this parameter. Prefer source UNLESS source's
                // item has a null ObjectProperty value — in UE3 MIC, a null
                // override actively forces "no texture/object" rather than
                // falling back to parent. We never want source's null to
                // wipe out target's working texture ref.
                if (ItemHasNullObjectRef(sBytes, tgtNameTable))
                    merged.Add(tBytes);
                else
                    merged.Add(sBytes);
            }
            else
            {
                merged.Add(tBytes);
            }
            addedNames.Add(name);
        }
        // Append source-only parameters (not in target). Skip ones with null
        // ObjectRefs — adding a null override produces no useful effect and
        // could destabilise the parent material's defaults.
        foreach (var (name, sBytes) in srcItems)
        {
            if (addedNames.Contains(name)) continue;
            if (ItemHasNullObjectRef(sBytes, tgtNameTable)) continue;
            merged.Add(sBytes);
            addedNames.Add(name);
        }

        using MemoryStream ms = new();
        ms.Write(BitConverter.GetBytes(merged.Count), 0, 4);
        foreach (var b in merged) ms.Write(b, 0, b.Length);
        return ms.ToArray();
    }

    // Walks an item's tag stream looking for any ObjectProperty (or related)
    // whose 4-byte value is 0 (= null FObject). Returns true on first hit.
    // Used to detect destructive null overrides that should not replace a
    // valid target value.
    private static bool ItemHasNullObjectRef(byte[] itemBytes, IReadOnlyList<UnrealNameTableEntry> nameTable)
    {
        int p = 0;
        int end = itemBytes.Length;
        while (true)
        {
            if (p + 8 > end) return false;
            int tagNameIdx = BitConverter.ToInt32(itemBytes, p);
            int tagNameNum = BitConverter.ToInt32(itemBytes, p + 4);
            string tagName = ResolveName(nameTable, tagNameIdx, tagNameNum);
            p += 8;
            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase)) return false;
            if (p + 8 > end) return false;
            int typeNameIdx = BitConverter.ToInt32(itemBytes, p);
            int typeNameNum = BitConverter.ToInt32(itemBytes, p + 4);
            string typeName = ResolveName(nameTable, typeNameIdx, typeNameNum);
            p += 8;
            if (p + 8 > end) return false;
            int valueSize = BitConverter.ToInt32(itemBytes, p);
            p += 8;
            if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase))
            {
                if (p + 8 > end) return false;
                p += 8;
            }
            else if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (p + 1 > end) return false;
                p += 1;
                continue;
            }
            if (IsObjectishProperty(typeName) && valueSize == 4 && p + 4 <= end)
            {
                int refVal = BitConverter.ToInt32(itemBytes, p);
                if (refVal == 0) return true;
            }
            if (p + valueSize > end) return false;
            p += valueSize;
        }
    }

    private static bool IsObjectishProperty(string typeName) =>
        string.Equals(typeName, "ObjectProperty",    StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "ClassProperty",     StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "ComponentProperty", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "InterfaceProperty", StringComparison.OrdinalIgnoreCase);

    // Parses an ArrayProperty value blob whose items are tagged structs
    // (each item ends with a "None" tag). Returns each item's bytes paired
    // with its ParameterName value. Returns null on any parse failure.
    private static List<(string Name, byte[] Bytes)>? ParseArrayItems(
        byte[] bytes, int valueOffset, int valueLen,
        IReadOnlyList<UnrealNameTableEntry> nameTable)
    {
        if (valueLen < 4) return null;
        int count = BitConverter.ToInt32(bytes, valueOffset);
        if (count < 0 || count > 4096) return null;
        if (count == 0) return new List<(string, byte[])>();
        var items = new List<(string, byte[])>(count);
        int p = valueOffset + 4;
        int end = valueOffset + valueLen;
        for (int i = 0; i < count; i++)
        {
            int itemStart = p;
            string? paramName = null;
            // Walk this item's tag stream until "None".
            while (true)
            {
                if (p + 8 > end) return null;
                int tagNameIdx = BitConverter.ToInt32(bytes, p);
                int tagNameNum = BitConverter.ToInt32(bytes, p + 4);
                string tagName = ResolveName(nameTable, tagNameIdx, tagNameNum);
                p += 8;
                if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase)) break;
                if (p + 8 > end) return null;
                int typeNameIdx = BitConverter.ToInt32(bytes, p);
                int typeNameNum = BitConverter.ToInt32(bytes, p + 4);
                string typeName = ResolveName(nameTable, typeNameIdx, typeNameNum);
                p += 8;
                if (p + 8 > end) return null;
                int valueSize = BitConverter.ToInt32(bytes, p);
                p += 8;
                if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase))
                {
                    if (p + 8 > end) return null;
                    p += 8;
                }
                else if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                {
                    if (p + 1 > end) return null;
                    p += 1;
                    continue;
                }
                if (string.Equals(tagName, "ParameterName", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(typeName, "NameProperty", StringComparison.OrdinalIgnoreCase)
                    && valueSize == 8 && p + 8 <= end)
                {
                    int paramIdx = BitConverter.ToInt32(bytes, p);
                    int paramNum = BitConverter.ToInt32(bytes, p + 4);
                    paramName = ResolveName(nameTable, paramIdx, paramNum);
                }
                if (p + valueSize > end) return null;
                p += valueSize;
            }
            int itemLen = p - itemStart;
            byte[] itemBytes = new byte[itemLen];
            Buffer.BlockCopy(bytes, itemStart, itemBytes, 0, itemLen);
            // If ParameterName wasn't captured (shouldn't happen for valid
            // MIC items), use a position-based fallback key. The fallback
            // means source's item at position i replaces target's item at i.
            string key = paramName ?? $"__unnamed_{i}";
            items.Add((key, itemBytes));
        }
        return items;
    }

    private static string ResolveName(IReadOnlyList<UnrealNameTableEntry> nameTable, int index, int numeric)
    {
        if (index < 0 || index >= nameTable.Count) return $"(bad{index})";
        string baseName = nameTable[index]?.Name?.String ?? "(null)";
        return numeric > 0 ? $"{baseName}_{numeric - 1}" : baseName;
    }
}
