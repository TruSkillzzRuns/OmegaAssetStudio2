using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

// Extracts per-tag byte spans from a UScript export body's property table.
// Companion to PropertyTagRewriter — the rewriter walks and mutates in place;
// the parser walks and returns immutable spans so a downstream merger can
// pick-and-choose which to keep, replace, or drop.
//
// Output:
//   - Spans: ordered list of (TagName, ArrayIdx, TypeName, full tag bytes,
//     value offset within those bytes, value length). The "None" tag is NOT
//     included in Spans — it terminates the property table.
//   - Tail: bytes after the "None" tag (class-specific binary). Caller
//     decides whether to keep target's tail, source's tail, or splice.
//   - NetIndexLen: length of the UObject NetIndex prefix consumed before
//     the property table (always 4 for our cases).
public sealed class PropertyTagSpan
{
    public string TagName  { get; init; } = string.Empty;
    public int    ArrayIdx { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public byte[] Bytes    { get; init; } = Array.Empty<byte>();
    public int    ValueOffsetInSpan { get; init; } // start of value blob within Bytes
    public int    ValueLen { get; init; }
}

internal static class PropertyTableParser
{
    // UE3 export body prefix sizes to try when the caller doesn't pre-know
    // the right one. UObject = 4 (NetIndex), UComponent subobjects often
    // include ObjectArchetype/Outer extras (8, 12, 16). 0 covers exports
    // with no prefix at all.
    private static readonly int[] CandidatePrefixSizes = { 4, 8, 12, 16, 0 };

    // How far past the end of a name table an index may sit and still be
    // believed. A body translated for a package that has not been written yet
    // names entries queued for addition, and those indices are exactly this
    // sort of overshoot. A walk that started at the wrong offset overshoots by
    // far more than a costume ever adds.
    private const int PendingNameAllowance = 4096;

    public static (List<PropertyTagSpan> Spans, byte[] Tail, int NetIndexLen) Parse(
        byte[] body,
        IReadOnlyList<UnrealNameTableEntry> nameTable)
        => ParseWithPrefix(body, nameTable, prefix: -1);

    // prefix=-1 enables adaptive detection: try each candidate size and pick
    // the first that walks cleanly to a "None" tag. prefix>=0 forces that
    // exact prefix size (useful when caller already knows it).
    public static (List<PropertyTagSpan> Spans, byte[] Tail, int NetIndexLen) ParseWithPrefix(
        byte[] body,
        IReadOnlyList<UnrealNameTableEntry> nameTable,
        int prefix)
    {
        if (body.Length < 4) return (new List<PropertyTagSpan>(), Array.Empty<byte>(), 0);
        int[] toTry = prefix >= 0 ? new[] { prefix } : CandidatePrefixSizes;
        foreach (int p0 in toTry)
        {
            if (p0 > body.Length) continue;
            var (ok, spans, tail) = TryParseAtPrefix(body, nameTable, p0);
            if (ok) return (spans, tail, p0);
        }
        return (new List<PropertyTagSpan>(), Array.Empty<byte>(), 0);
    }

    private static (bool ok, List<PropertyTagSpan> spans, byte[] tail) TryParseAtPrefix(
        byte[] body,
        IReadOnlyList<UnrealNameTableEntry> nameTable,
        int startOffset)
    {
        var spans = new List<PropertyTagSpan>();
        int p = startOffset;
        while (true)
        {
            if (p + 8 > body.Length) return (false, spans, Array.Empty<byte>());
            int tagStart = p;
            int tagNameIdx = BitConverter.ToInt32(body, p);
            int tagNameNum = BitConverter.ToInt32(body, p + 4);
            // A name index outside the table means this is not a property tag,
            // which means this prefix is the wrong one. Fail so the adaptive
            // loop moves on to the next candidate.
            //
            // Without this the walk carried on with ResolveName's placeholder
            // string standing in for a name that does not exist, and a parse
            // that had started at the wrong offset could still run into some
            // later word that happened to read as "None" and report success.
            // The spans it produced were slices of value data rather than
            // tags, and a merge that appended them wrote their bytes -
            // including the out-of-range index itself - into the output. The
            // engine then refuses the package outright: "Bad Name Index -1".
            // Verified on Ironman_Mark46Helmetless -> Ironman_Mark4, where a
            // sounds component parsed at the wrong prefix and appended a tag
            // whose name index was -1.
            // Negative is always wrong and is what the corruption looked like.
            // Past the end is NOT: a translated body names entries that are
            // still queued to be added, so their indices legitimately sit
            // beyond the table this is being read against. Rejecting those
            // threw away every property of such a body, the merge saw nothing
            // of source's to apply, and the swap silently kept target's -
            // measured on one costume, which then wore the chassis's mesh.
            // An absurd value is still refused, since a walk at the wrong
            // offset produces those and that is what must not be believed.
            if (tagNameIdx < 0 || tagNameIdx > nameTable.Count + PendingNameAllowance)
                return (false, spans, Array.Empty<byte>());
            string tagName = ResolveName(nameTable, tagNameIdx, tagNameNum);
            p += 8;

            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase))
            {
                byte[] tail = new byte[body.Length - p];
                Buffer.BlockCopy(body, p, tail, 0, tail.Length);
                return (true, spans, tail);
            }

            if (p + 8 > body.Length) return (false, spans, Array.Empty<byte>());
            int typeNameIdx = BitConverter.ToInt32(body, p);
            int typeNameNum = BitConverter.ToInt32(body, p + 4);
            // Same again for the type, on the same terms.
            if (typeNameIdx < 0 || typeNameIdx > nameTable.Count + PendingNameAllowance)
                return (false, spans, Array.Empty<byte>());
            string typeName = ResolveName(nameTable, typeNameIdx, typeNameNum);
            p += 8;

            if (p + 8 > body.Length) return (false, spans, Array.Empty<byte>());
            int valueSize = BitConverter.ToInt32(body, p);
            int arrayIdx  = BitConverter.ToInt32(body, p + 4);
            p += 8;

            // Type-specific extras BEFORE the value blob.
            if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase))
            {
                if (p + 8 > body.Length) return (false, spans, Array.Empty<byte>());
                p += 8;
            }
            else if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (p + 1 > body.Length) return (false, spans, Array.Empty<byte>());
                p += 1;
                // BoolProperty has the value as part of the extras (a single byte);
                // there is no separate value blob.
                int spanLen = p - tagStart;
                byte[] tagBytes = new byte[spanLen];
                Buffer.BlockCopy(body, tagStart, tagBytes, 0, spanLen);
                spans.Add(new PropertyTagSpan
                {
                    TagName  = tagName,
                    ArrayIdx = arrayIdx,
                    TypeName = typeName,
                    Bytes    = tagBytes,
                    ValueOffsetInSpan = spanLen - 1,
                    ValueLen = 1,
                });
                continue;
            }

            // Defensive: reject corrupted/garbage valueSize. The check was
            // previously `p + valueSize > body.Length` but `p + valueSize`
            // overflows int when valueSize is huge or negative — the check
            // passes, p moves backwards or past the end, and the eventual
            // `new byte[totalSpanLen]` throws OverflowException. Use a
            // subtraction form that can't overflow, and explicitly reject
            // negative valueSize (which would make p go backwards and
            // produce a negative span length).
            if (valueSize < 0 || valueSize > body.Length - p) return (false, spans, Array.Empty<byte>());
            int valueOffsetInBody = p;
            p += valueSize;
            int totalSpanLen = p - tagStart;
            byte[] spanBytes = new byte[totalSpanLen];
            Buffer.BlockCopy(body, tagStart, spanBytes, 0, totalSpanLen);
            spans.Add(new PropertyTagSpan
            {
                TagName  = tagName,
                ArrayIdx = arrayIdx,
                TypeName = typeName,
                Bytes    = spanBytes,
                ValueOffsetInSpan = valueOffsetInBody - tagStart,
                ValueLen = valueSize,
            });
        }
    }

    private static string ResolveName(IReadOnlyList<UnrealNameTableEntry> nameTable, int index, int numeric)
    {
        if (index < 0 || index >= nameTable.Count) return $"(badNameIdx={index})";
        string baseName = nameTable[index]?.Name?.String ?? "(null)";
        return numeric > 0 ? $"{baseName}_{numeric - 1}" : baseName;
    }
}
