using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

// Merges source's translated property table with target's existing one to
// produce a body that:
//   1. Brings source's NEW/CHANGED property values into target (the visual
//      upgrade — new MIC ref, new physics asset ref, etc.).
//   2. KEEPS target's value when source's translated value would be null
//      because the underlying source export wasn't added to target (e.g.
//      source's SkeletalMesh ref → target didn't add the new mesh → would
//      be null → fall back to target's existing mesh ref).
//   3. KEEPS target's value for properties that exist in target but not in
//      source (target's Default__ had more explicit overrides than source's).
//
// Without this merger, replacing target's Default__ with source's translated
// body wipes out target's mesh/animset/etc. refs that source inherits from a
// parent class we can't fully migrate — costume goes invisible.
internal static class PropertyTableMerger
{
    // Merge produces a new body using:
    //   - sourceRawBody: source's body as it appears in source UPK (for
    //     broken-translation detection — we compare raw vs translated)
    //   - sourceTranslatedBody: source's body after PropertyTagRewriter
    //   - targetBody: target's body as it appears in target UPK
    //   - translator: built from source+target headers (with aliases)
    //
    // Returns: merged body bytes ready to write to target's export slot.
    // Property names that ALWAYS resolve to target's value during a merge,
    // even if source has a value for them. These hold the mesh + component
    // chain that target's costume needs intact — replacing them with source's
    // values typically points at source-only exports we didn't transplant
    // (e.g. new SkeletalMesh), nulling the chain → invisible character.
    public static readonly HashSet<string> CriticalPreferTargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mesh refs intentionally NOT on this list — source's translated
        // SkeletalMesh body is now wired (via the reference translator) so
        // source's mesh ref in Default__ and SkeletalMeshComponent should
        // win. Re-add these names to revert to target's mesh.
        //   "mesh", "initialskeletalmesh", "skeletalmesh",
        "components",
        "collisioncomponent",
        "physicsasset",
        "skeletalmeshcomponent",
        "physicsassetinstance",
        "lightenvironment",
        // ReplacementPrimitive on SkeletalMeshComponent points at another
        // component in the actor's component hierarchy that should render
        // INSTEAD of this mesh in certain LOD / occlusion conditions. Source's
        // value references source's own component graph; even when it
        // translates to a non-null target export it's pointing at the wrong
        // object class (typically nulls out or aliases to an unrelated
        // component), and powers / throwables walk this ref when spawning
        // attached primitives → null deref / segfault on activation. Keep
        // target's value so the engine's primitive replacement chain stays
        // pointed at a valid target-side component.
        "replacementprimitive",
        // Throwable power component arrays on Pawn's Default__. These hold
        // FObject refs to per-power child component objects that live ONLY
        // in source's package (cooked-in power component subobjects of the
        // Colossus_AoA pawn class). Source's translated values for these
        // arrays point at object refs that either fail translation (→ null)
        // or alias to unrelated target exports. When the player picks up a
        // throwable, the engine indexes into one of these arrays to spawn
        // the matching power component → null deref → segfault ~10s in.
        // Target's vanilla values point at target's own valid throw-power
        // components, which is what we want. Visual transplant is unaffected
        // because the costume look comes from the mesh + MIC, not from the
        // throw-power component class refs.
        "throwpowerweakcomponents",
        "throwpowerstrongcomponents",
        "throwputdownpowerweakcomponents",
        "throwputdownpowerstrongcomponents",
        // MIC parent material — source references a chbasematerials_v2
        // package that doesn't exist on disk in 1.52. Target's MIC parent
        // points at something that DOES exist (the older base material
        // package). Keep target's parent so the MIC has a working base
        // shader; source's scalar/vector/texture overrides still apply
        // on top.
        "parent",
        // A material expression's back-pointer to the material that owns it.
        // An expression being merged is sitting in one of TARGET's expression
        // slots, so by construction it belongs to TARGET's material and must
        // say so; source's value names source's material, which is a different
        // object even when the two share a path.
        //
        // This was previously safe only by accident. Source's value usually
        // failed to translate, so the broken-ref rule kept target's value and
        // the back-pointer stayed right. Once an alias makes source's value
        // resolve to something — as the masked-base alias does — the broken
        // rule stops firing and source's material wins, pointing every
        // expression of the chassis's own base material at a different
        // material. Verified on Gambit_Shirtless -> Gambit_Classic: six
        // expressions of chbasematerial_v2-1 flipped from their owner to the
        // aliased base. Naming it here makes the back-pointer right on
        // purpose rather than by luck.
        "material",
        // MIC static-permutation flags. When true, source expects a
        // precompiled shader payload appended in its binary tail. That
        // shader was compiled for source's parent material and is invalid
        // against target's parent. Forcing target's values for these flags
        // keeps the engine on the dynamic-permutation code path that uses
        // target's parent's shader (with source's parameter overrides).
        "bhasstaticpermutationresource",
        "bhasqualityswitch",
    };

    public static byte[] Merge(
        byte[] sourceRawBody,
        byte[] sourceTranslatedBody,
        byte[] targetBody,
        IndexTranslator translator,
        out List<string> diagnostics)
    {
        diagnostics = new List<string>();

        var (tgtSpans, tgtTail, tgtPrefixLen) = PropertyTableParser.Parse(targetBody, translator.Target.NameTable);

        // Source's bodies are read from the SAME offset target's properties
        // start at, not hunted for independently. The two are the same class
        // sitting in the same slot, so their headers are the same length; and
        // the adaptive search takes the first candidate that walks cleanly,
        // trying 4 before 16.
        //
        // Starting a component's body at 4 does not fail loudly. It reads a
        // word twelve bytes early, that word resolves to "None", and the parse
        // reports success with ZERO properties. The merge then has nothing of
        // source's to apply and keeps target's body entire — so the swap
        // silently does nothing.
        //
        // Measured on one costume pair: the rewriter
        // found source's properties at 16 while the merge read the same body
        // at 4 and saw none, reporting source-kept=0 and appending target's
        // three tags. The costume loaded wearing the chassis's mesh. Another,
        // whose body does not read as empty at 4, kept source's mesh reference
        // and looked right.
        var (srcRawSpans, srcRawTail, srcRawPrefixLen) =
            PropertyTableParser.ParseWithPrefix(sourceRawBody, translator.Source.NameTable, tgtPrefixLen);
        if (srcRawSpans.Count == 0)
            (srcRawSpans, srcRawTail, srcRawPrefixLen) =
                PropertyTableParser.Parse(sourceRawBody, translator.Source.NameTable);

        var (srcTransSpans, srcTransTail, srcTransPrefixLen) =
            PropertyTableParser.ParseWithPrefix(sourceTranslatedBody, translator.Target.NameTable, tgtPrefixLen);
        if (srcTransSpans.Count == 0)
            (srcTransSpans, srcTransTail, srcTransPrefixLen) =
                PropertyTableParser.Parse(sourceTranslatedBody, translator.Target.NameTable);

        _ = srcRawTail;
        _ = srcTransTail;
        _ = srcRawPrefixLen;

        // Index by key = "tagName::arrayIdx". Same tag at different array
        // indices is a separate property (e.g. Materials[0] vs Materials[1]).
        static string Key(PropertyTagSpan s) => $"{s.TagName}::{s.ArrayIdx}";
        var tgtByKey = new Dictionary<string, PropertyTagSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in tgtSpans) tgtByKey[Key(s)] = s;
        var srcRawByKey = new Dictionary<string, PropertyTagSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in srcRawSpans) srcRawByKey[Key(s)] = s;

        // Detect "broken" source keys: ones whose translation introduced a
        // null FObject ref where source's raw had a non-zero ref. Walks
        // recursively into ArrayProperty<Object> items and StructProperty
        // nested property tables so refs hidden inside compound values
        // also trigger the broken flag.
        var brokenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in srcRawByKey)
        {
            if (TagHasBrokenRefs(kv.Value, translator))
                brokenKeys.Add(kv.Key);
        }

        using MemoryStream ms = new();
        // Re-emit the FULL pre-property prefix from sourceTranslatedBody.
        // UObject = 4 bytes (NetIndex); UComponent subobjects like
        // SkeletalMeshComponent use 16 bytes (NetIndex + ObjectArchetype +
        // additional bookkeeping). The parser returns the adaptive prefix
        // length it consumed; we MUST mirror it byte-for-byte, otherwise
        // the engine reads the property tag stream as if it were the
        // component header and the resulting NetIndex/Archetype values are
        // garbage — UE3 then refuses to load with errors like
        // "AddNetObject ... invalid NetIndex N (max: 0)".
        //
        // TARGET's prefix length is the one that counts, not source's. The body
        // being built goes into target's slot and the engine reads it as
        // target's object, so the header it expects is target's — whatever
        // source's happened to be.
        //
        // Source's cannot be trusted for this because the parser is adaptive
        // and takes the FIRST candidate size that walks cleanly, trying 4
        // before 16. A translated component body that also happens to parse
        // from offset 4 therefore reports 4, and writing 4 where the engine
        // expects 16 leaves it reading twelve bytes of the property stream as
        // header and everything after it as rubbish.
        //
        // Measured on one costume pair: chassis and
        // costume both hold initialskeletalmesh with its properties at 16, and
        // the merged body came out with them at 4 — the whole of that costume's
        // freeze, and the only object at fault. Another pair, whose merge kept
        // 16, loads. Where the two agree, which is almost always, this changes
        // nothing.
        int prefixLen = tgtPrefixLen > 0 ? tgtPrefixLen
            : srcTransPrefixLen > 0 ? srcTransPrefixLen
            : 4;
        if (targetBody.Length >= prefixLen)
            ms.Write(targetBody, 0, prefixLen);
        else if (sourceTranslatedBody.Length >= prefixLen)
            ms.Write(sourceTranslatedBody, 0, prefixLen);
        else
            ms.Write(new byte[prefixLen], 0, prefixLen);

        // Walk source's translated spans first, preferring target's bytes
        // when source's translation broke the key OR when the property is
        // on the CriticalPreferTargetNames list. MIC parameter arrays get
        // a special item-level merge instead of all-or-nothing.
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int kept = 0, preferredTgt = 0, droppedNullNoTgt = 0, preferredTgtCritical = 0, micArraysMerged = 0;
        foreach (var s in srcTransSpans)
        {
            string key = Key(s);
            usedKeys.Add(key);
            bool isCritical = CriticalPreferTargetNames.Contains(s.TagName);
            // Broken is judged on what we are ABOUT TO WRITE as well as on the
            // raw source. The raw-side check compares each reference before and
            // after translation, which misses a list whose emptiness only shows
            // up in the finished bytes; and a list of object references with a
            // hole in it is not a usable list whatever produced it.
            //
            // The pawn's AnimSets is the case that showed it. A 1.53 costume
            // names three sets, the third of which lives only in its own
            // package and is never carried, so the translated list came out
            // [set, set, nothing]. Written as it stood, the character had no
            // animation at all and stood in the bind pose. The chassis names no
            // sets of its own, so dropping the property outright leaves it
            // taking its animation from wherever it did before the swap, which
            // is what a costume change should not disturb.
            bool isBroken = brokenKeys.Contains(key) || TranslatedObjectListHasAHole(s);
            bool isMicArray = string.Equals(s.TypeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase)
                              && MicParameterArrayMerger.IsMicParameterArray(s.TagName);

            // MIC parameter arrays: item-level merge keyed by ParameterName.
            // Source's item wins for matching names; source's new items added;
            // target's exclusive items kept (preserves precomputed-shader
            // compatibility).
            if (isMicArray && tgtByKey.TryGetValue(key, out var tArr))
            {
                byte[]? mergedValue = MicParameterArrayMerger.TryMerge(
                    s.Bytes, s.ValueOffsetInSpan, s.ValueLen,
                    tArr.Bytes, tArr.ValueOffsetInSpan, tArr.ValueLen,
                    translator.Target.NameTable);
                if (mergedValue != null)
                {
                    byte[] mergedSpan = BuildArrayTagWithNewValue(s, mergedValue);
                    ms.Write(mergedSpan, 0, mergedSpan.Length);
                    micArraysMerged++;
                    int srcCount = ArrayItemCount(s.Bytes, s.ValueOffsetInSpan, s.ValueLen);
                    int tgtCount = ArrayItemCount(tArr.Bytes, tArr.ValueOffsetInSpan, tArr.ValueLen);
                    int mergedCount = ArrayItemCount(mergedValue, 0, mergedValue.Length);
                    diagnostics.Add($"merge: '{s.TagName}[{s.ArrayIdx}]' item-merged ({srcCount} src + {tgtCount} tgt -> {mergedCount} merged items)");
                    continue;
                }
                diagnostics.Add($"merge: '{s.TagName}[{s.ArrayIdx}]' item-merge parse failed; falling back to source's array");
            }

            if ((isCritical || isBroken) && tgtByKey.TryGetValue(key, out var t))
            {
                ms.Write(t.Bytes, 0, t.Bytes.Length);
                if (isCritical) { preferredTgtCritical++; diagnostics.Add($"merge: '{s.TagName}[{s.ArrayIdx}]' is on critical-prefer-target list; kept target's value"); }
                else { preferredTgt++; diagnostics.Add($"merge: '{s.TagName}[{s.ArrayIdx}]' source translated to null; kept target's value"); }
            }
            else if (isBroken)
            {
                droppedNullNoTgt++;
                diagnostics.Add($"merge: '{s.TagName}[{s.ArrayIdx}]' source translated to null and target has no value; dropped");
            }
            else
            {
                ms.Write(s.Bytes, 0, s.Bytes.Length);
                kept++;
            }
        }

        // Append target's spans that source didn't override.
        int tgtAppended = 0;
        var tgtAppendedTags = new List<string>();
        foreach (var t in tgtSpans)
        {
            string key = Key(t);
            if (usedKeys.Contains(key)) continue;
            ms.Write(t.Bytes, 0, t.Bytes.Length);
            tgtAppendedTags.Add($"{t.TagName}[{t.ArrayIdx}]({t.TypeName})");
            tgtAppended++;
        }

        // Emit "None" terminator. Need target's "None" name index.
        int noneIdx = FindNameIndex(translator.Target.NameTable, "None");
        if (noneIdx < 0) noneIdx = 0;
        ms.Write(BitConverter.GetBytes(noneIdx), 0, 4);
        ms.Write(BitConverter.GetBytes(0), 0, 4);

        // Append target's binary tail (class-specific bytes after property
        // table). For UObject (Default__) this is typically empty. Using
        // target's keeps the file consistent with target's class layout.
        if (tgtTail.Length > 0)
            ms.Write(tgtTail, 0, tgtTail.Length);

        diagnostics.Add($"merge stats: source-kept={kept}, target-preferred(broken)={preferredTgt}, target-preferred(critical)={preferredTgtCritical}, source-dropped={droppedNullNoTgt}, target-appended={tgtAppended}, mic-arrays-item-merged={micArraysMerged}");
        if (tgtAppendedTags.Count > 0)
            diagnostics.Add($"merge target-appended tags: {string.Join(", ", tgtAppendedTags)}");
        // List source tags + their types so we can see what's being kept.
        if (srcTransSpans.Count > 0)
        {
            diagnostics.Add($"merge source tags: {string.Join(", ", srcTransSpans.Select(s => $"{s.TagName}[{s.ArrayIdx}]({s.TypeName})" + (brokenKeys.Contains(Key(s)) ? "!BROKEN" : string.Empty)))}");
        }
        return ms.ToArray();
    }

    // Replaces the value-blob portion of an ArrayProperty span with new bytes
    // and patches the size field in the tag header to match. Tag header
    // layout for ArrayProperty (no struct/byte/bool extras):
    //   [0..7]   tag name FName
    //   [8..15]  type name FName ("ArrayProperty")
    //   [16..19] valueSize int32     <-- must be rewritten
    //   [20..23] arrayIdx int32
    //   [24..]   value blob (count + items)
    private static byte[] BuildArrayTagWithNewValue(PropertyTagSpan origSpan, byte[] newValue)
    {
        byte[] result = new byte[origSpan.ValueOffsetInSpan + newValue.Length];
        Buffer.BlockCopy(origSpan.Bytes, 0, result, 0, origSpan.ValueOffsetInSpan);
        BitConverter.GetBytes(newValue.Length).CopyTo(result, 16);
        Buffer.BlockCopy(newValue, 0, result, origSpan.ValueOffsetInSpan, newValue.Length);
        return result;
    }

    private static int ArrayItemCount(byte[] bytes, int valueOffset, int valueLen)
    {
        if (valueLen < 4) return 0;
        return BitConverter.ToInt32(bytes, valueOffset);
    }

    private static bool IsObjectishProperty(string typeName) =>
        string.Equals(typeName, "ObjectProperty",    StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "ClassProperty",     StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "ComponentProperty", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "InterfaceProperty", StringComparison.OrdinalIgnoreCase);

    // Returns true if anywhere inside this tag's value bytes there is an
    // FObject reference whose source value is non-zero but translates to
    // zero in target's tables. Recurses into ArrayProperty items (assumed
    // Object-like when item-size matches 4 bytes) and StructProperty
    // nested property streams. The source name table is used to resolve
    // nested tag names during the struct walk.
    // Whether a translated list of object references has a null in it. Only a
    // list that is exactly a count followed by that many references is judged;
    // anything else is left alone, since guessing at an array's element size
    // from its length is how unrelated arrays get mangled.
    private static bool TranslatedObjectListHasAHole(PropertyTagSpan span)
    {
        if (!string.Equals(span.TypeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase)) return false;
        if (span.ValueLen < 4) return false;

        int count = BitConverter.ToInt32(span.Bytes, span.ValueOffsetInSpan);
        if (count <= 0 || count > 4096) return false;
        if (span.ValueLen - 4 != count * 4) return false;

        for (int i = 0; i < count; i++)
        {
            if (BitConverter.ToInt32(span.Bytes, span.ValueOffsetInSpan + 4 + (i * 4)) == 0) return true;
        }

        return false;
    }

    private static bool TagHasBrokenRefs(PropertyTagSpan span, IndexTranslator translator)
    {
        if (IsObjectishProperty(span.TypeName) && span.ValueLen == 4)
        {
            int rawRef = BitConverter.ToInt32(span.Bytes, span.ValueOffsetInSpan);
            return rawRef != 0 && translator.TranslateObjectReference(rawRef) == 0;
        }
        if (string.Equals(span.TypeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase))
        {
            return ArrayHasBrokenRefs(span.Bytes, span.ValueOffsetInSpan, span.ValueLen, translator);
        }
        if (string.Equals(span.TypeName, "StructProperty", StringComparison.OrdinalIgnoreCase))
        {
            return StructHasBrokenRefs(span.Bytes, span.ValueOffsetInSpan, span.ValueLen, translator);
        }
        return false;
    }

    private static bool ArrayHasBrokenRefs(byte[] bytes, int valueOffset, int valueLen, IndexTranslator translator)
    {
        if (valueLen < 4) return false;
        int count = BitConverter.ToInt32(bytes, valueOffset);
        if (count <= 0 || count > 4096) return false;
        int itemBytes = valueLen - 4;
        // Heuristic: only Object-like if items are exactly 4 bytes each
        // (FObject = int32). Skips e.g. FString arrays or primitive arrays.
        if (itemBytes != count * 4) return false;
        int itemBase = valueOffset + 4;
        for (int i = 0; i < count; i++)
        {
            int rawRef = BitConverter.ToInt32(bytes, itemBase + i * 4);
            if (rawRef != 0 && translator.TranslateObjectReference(rawRef) == 0) return true;
        }
        return false;
    }

    private static bool StructHasBrokenRefs(byte[] bytes, int valueOffset, int valueLen, IndexTranslator translator)
    {
        // Walks struct value as a nested property tag stream. Uses source
        // name table to resolve type/tag names. Mirrors PropertyTableParser
        // but only checks for broken refs — doesn't extract spans.
        var sourceNameTable = translator.Source.NameTable;
        int p = valueOffset;
        int end = valueOffset + valueLen;
        while (p + 8 <= end)
        {
            int tagNameIdx = BitConverter.ToInt32(bytes, p);
            // ignore tag numeric extension here
            p += 8;
            if (tagNameIdx < 0 || tagNameIdx >= sourceNameTable.Count) return false;
            string tagName = sourceNameTable[tagNameIdx]?.Name?.String ?? string.Empty;
            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase)) return false;
            if (p + 8 > end) return false;
            int typeNameIdx = BitConverter.ToInt32(bytes, p);
            string typeName = (typeNameIdx >= 0 && typeNameIdx < sourceNameTable.Count)
                ? sourceNameTable[typeNameIdx]?.Name?.String ?? string.Empty
                : string.Empty;
            p += 8;
            if (p + 8 > end) return false;
            int valueSize = BitConverter.ToInt32(bytes, p);
            p += 8;
            if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase))
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
            if (p + valueSize > end) return false;
            if (IsObjectishProperty(typeName) && valueSize == 4)
            {
                int rawRef = BitConverter.ToInt32(bytes, p);
                if (rawRef != 0 && translator.TranslateObjectReference(rawRef) == 0) return true;
            }
            else if (string.Equals(typeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (ArrayHasBrokenRefs(bytes, p, valueSize, translator)) return true;
            }
            else if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (StructHasBrokenRefs(bytes, p, valueSize, translator)) return true;
            }
            p += valueSize;
        }
        return false;
    }

    private static int FindNameIndex(IReadOnlyList<UpkManager.Models.UpkFile.Tables.UnrealNameTableEntry> nameTable, string name)
    {
        for (int i = 0; i < nameTable.Count; i++)
        {
            string n = nameTable[i]?.Name?.String ?? string.Empty;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
