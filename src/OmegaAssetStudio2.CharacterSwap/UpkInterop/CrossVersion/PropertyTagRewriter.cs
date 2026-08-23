using System;
using System.Collections.Generic;
using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.UpkInterop.CrossVersion;

// Rewrites the embedded name/import/export indexes in a UScript-style export
// body so that bytes copied from the source UPK make sense when re-emitted
// into the target UPK's table layout. Recurses into StructProperty values
// (which are themselves nested tagged property streams for non-atomic
// structs) — atomic structs like Vector/Color/Rotator are pure binary and
// get passed through.
//
// UE3 property-tag stream layout (per-tag):
//   int32 nameIdx, nameNum            // tag name (FName)
//   if (tagName == "None") stop
//   int32 typeIdx, typeNum            // property type (FName)
//   int32 size                         // size of the value blob
//   int32 arrayIdx                     // sub-index for arrays
//   --- type-specific extras BEFORE value ---
//   StructProperty : int32 structNameIdx, structNameNum (the struct's UScript class name)
//   ByteProperty   : int32 enumNameIdx,   enumNameNum   (the enum's name)
//   BoolProperty   : byte boolValue                     (no separate value blob)
//   --- value blob of `size` bytes ---
//   ObjectProperty / ClassProperty / ComponentProperty / InterfaceProperty:
//     int32 FObject ref                                 (size is 4)
//   NameProperty:
//     int32 nameIdx, nameNum                            (size is 8)
//   StructProperty: nested tagged stream if non-atomic, raw bytes if atomic.
//   ArrayProperty: int32 count + element data — NOT deep-translated (a soft
//     warning is emitted; future work).
//
// After the "None" tag at the top level, the rest of the body is class-
// specific binary data (StaticMesh vertex buffers, Texture2D mip chain etc.)
// and is copied verbatim.
public sealed class PropertyTagRewriter
{
    public sealed class Result
    {
        public bool Success { get; set; }
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public int PropertyTagsTranslated { get; set; }
        public int BinaryTailLength { get; set; }
        // Untranslated bytes at the start of the body before the property
        // tag stream (NetIndex + UComponent ObjectArchetype + etc.). These
        // are copied verbatim from source. Callers doing matched-translate
        // typically want to overlay target's prefix bytes back on top so
        // that target-package-local refs (NetIndex, ObjectArchetype) stay
        // valid in the destination package.
        public int PrefixLength { get; set; }
        // Set true when the rewriter flipped bHasStaticPermutationResource
        // True->False on a MaterialInstance body. When set, callers MUST trim
        // the binary tail off the returned body (Body = Body[..^BinaryTailLength])
        // and update the export's SerialDataSize accordingly — otherwise the
        // engine sees declared-size > actually-consumed-size and refuses to
        // load with "Serial size mismatch: Got X, Expected Y".
        public bool FlippedStaticPermutationFlag { get; set; }
        public List<string> Issues { get; } = new();
    }

    private readonly IndexTranslator translator;
    public PropertyTagRewriter(IndexTranslator translator) => this.translator = translator;

    // Whether a shader instance keeps the shaders baked into it.
    //
    // Normally it does not: they are baked for the newer game and the older one
    // reads them as rubbish, so the flag is cleared and the engine draws with
    // the parent material's own compiled shader instead, which is what makes a
    // carried instance render at all. Kept as a switch for the case where an
    // instance's parent has no compiled shader of its own.
    public bool KeepBakedShaders { get; set; }

    public Result RewriteBody(byte[] srcBody, string contextName)
    {
        // UObject bodies start with a NetIndex prefix (4 bytes) followed by
        // the property tag table. But some subclasses prepend additional
        // bytes before the property table:
        //   - UComponent subobjects often have an ObjectArchetype FObject
        //     (4 more bytes), giving an 8-byte prefix total
        //   - UClass meta exports have a completely different layout (script
        //     bytecode, parent class ref, etc.) and CANNOT be translated
        //     this way at all
        // We try a few well-known prefix sizes and pick the first one whose
        // parse runs cleanly to a "None" terminator. Worst case all fail and
        // we report the first attempt's issues so the user knows why.
        return TryPrefixSizes(srcBody, contextName, new[] { 4, 8, 12, 16, 0 });
    }

    private Result TryPrefixSizes(byte[] srcBody, string contextName, int[] candidatePrefixSizes)
    {
        Result? firstFailure = null;
        foreach (int prefix in candidatePrefixSizes)
        {
            if (prefix > srcBody.Length) continue;
            var attempt = new Result { Body = (byte[])srcBody.Clone() };
            int p = prefix;
            if (TranslateTaggedStream(attempt.Body, ref p, attempt.Body.Length, contextName, attempt))
            {
                attempt.PrefixLength = prefix;
                attempt.BinaryTailLength = attempt.Body.Length - p;
                attempt.Success = true;
                if (prefix != 4)
                    attempt.Issues.Insert(0, $"{contextName}: adaptive-prefix succeeded at {prefix} bytes (default 4 didn't parse)");

                // BUG FIX (MIC UniformExpressionTextures + similar): after the
                // property table's "None" terminator, some UE3 classes (most
                // notably UMaterialInstanceConstant) serialize a BINARY TAIL
                // that holds FObject refs the property-tag walker never saw.
                // For MICs this is a TArray<UTexture*> shader-uniform-texture
                // cache, whose source-side refs (e.g. src export idx 459 =
                // 'normalplaceholder') would otherwise leak unchanged into
                // the output → engine calls CreateExport(458) → "Bad export
                // index 458/N" at first costume load.
                //
                // Walk the binary tail looking for the pattern
                //   int32 count  (1..64)
                //   followed by `count` int32s, EVERY ONE of which is a
                //   plausible source-side FObject ref (in [-srcImports, srcExports])
                // When that pattern matches, translate each element through
                // the IndexTranslator. Out-of-pattern bytes are left alone,
                // so non-FObject binary data (counts, GUIDs, floats) is safe.
                TranslateBinaryTailFObjectArrays(attempt.Body, p, contextName, attempt);

                // If we flipped bHasStaticPermutationResource->False, the
                // engine will stop reading at the property stream's "None"
                // terminator (current cursor p). Truncate the binary tail
                // off the body so SerialDataSize (= body.Length when
                // Phase2TableExtender writes the export entry) matches what
                // the engine actually consumes. Otherwise the engine throws
                // "Serial size mismatch: Got <p>, Expected <body.Length>"
                // and refuses to load the MIC.
                if (attempt.FlippedStaticPermutationFlag && p < attempt.Body.Length)
                {
                    int dropped = attempt.Body.Length - p;
                    byte[] trimmed = new byte[p];
                    Buffer.BlockCopy(attempt.Body, 0, trimmed, 0, p);
                    attempt.Body = trimmed;
                    attempt.BinaryTailLength = 0;
                    attempt.Issues.Add($"{contextName}: trimmed {dropped} bytes of v894 compiled-shader tail after flipping bHasStaticPermutationResource");
                }

                return attempt;
            }
            firstFailure ??= attempt;
        }
        return firstFailure ?? new Result { Body = (byte[])srcBody.Clone() };
    }

    // Walks a tagged property stream starting at p and ending either at the
    // "None" tag or at endLimit. Returns false on truncation / unrecoverable
    // translation failure (sets r.Issues; r.Success stays false). On success,
    // p is advanced past the "None" tag (or to endLimit if no None found in a
    // recursive struct sub-stream and the sub-stream consumed exactly its
    // declared size).
    private bool TranslateTaggedStream(byte[] buf, ref int p, int endLimit, string contextName, Result r)
    {
        while (p < endLimit)
        {
            if (p + 8 > endLimit)
            {
                r.Issues.Add($"{contextName}: truncated reading tag name at offset {p} (limit {endLimit})");
                return false;
            }
            int tagNameIdx = ReadInt32(buf, p);
            string tagName = translator.ResolveSourceName(tagNameIdx);

            if (!TryRewriteNameIndex(buf, p, tagNameIdx, "tag", contextName, r)) return false;
            p += 8;

            if (string.Equals(tagName, "None", StringComparison.OrdinalIgnoreCase))
                return true;

            if (p + 8 > endLimit)
            {
                r.Issues.Add($"{contextName}: truncated reading tag type at offset {p}");
                return false;
            }
            int typeNameIdx = ReadInt32(buf, p);
            string typeName = translator.ResolveSourceName(typeNameIdx);
            if (!TryRewriteNameIndex(buf, p, typeNameIdx, "type", contextName, r)) return false;
            p += 8;

            if (p + 8 > endLimit)
            {
                r.Issues.Add($"{contextName}: truncated reading size/arrayIdx at offset {p}");
                return false;
            }
            int valueSize = ReadInt32(buf, p);
            p += 8; // skip size + arrayIdx

            // Type-specific extras BEFORE the value blob.
            string? innerName = null;
            if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (p + 8 > endLimit)
                {
                    r.Issues.Add($"{contextName}: truncated reading {typeName} inner name at offset {p}");
                    return false;
                }
                int innerNameIdx = ReadInt32(buf, p);
                innerName = translator.ResolveSourceName(innerNameIdx);
                if (!TryRewriteNameIndex(buf, p, innerNameIdx, $"{typeName}-inner", contextName, r)) return false;
                p += 8;
            }
            else if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (p + 1 > endLimit)
                {
                    r.Issues.Add($"{contextName}: truncated BoolProperty value at offset {p}");
                    return false;
                }
                // CROSS-VERSION FIX: `bHasStaticPermutationResource=True` tells
                // the engine to deserialize a v868/v894-specific FMaterialResource
                // (compiled shader bytecode) from the body's binary tail. When
                // we transplant a v894 MIC into a v868 target, source's
                // FMaterialResource bytes are NOT v868-binary-compatible — the
                // engine reads garbage and the MIC ends up either rendering
                // flat parent-defaults or crashing on shader bind. Flipping
                // this flag to False makes the v868 engine skip the binary
                // tail entirely and use the parent UMaterial's already-
                // compiled v868 shader, with our (translated) TextureParameter
                // Values overlaid on top — which is exactly what a native
                // v868 MIC does when bHasStaticPermutationResource=False.
                // Engine-verified: SerialDataSize stays unchanged (the unread
                // tail bytes are harmless slack since deserialization stops
                // at the property stream's "None" terminator).
                if (string.Equals(tagName, "bHasStaticPermutationResource", StringComparison.OrdinalIgnoreCase)
                    && buf[p] != 0
                    && !KeepBakedShaders)
                {
                    buf[p] = 0;
                    r.FlippedStaticPermutationFlag = true;
                    r.Issues.Add($"{contextName}: flipped bHasStaticPermutationResource True->False (caller MUST trim binary tail + update SerialDataSize)");
                }
                p += 1;
                r.PropertyTagsTranslated++;
                continue;
            }

            // Value blob translation. Subtraction-form bounds check so
            // a corrupted huge/negative valueSize can't overflow int:
            // `p + valueSize > endLimit` wraps when valueSize is near
            // Int32.MaxValue, the check passes, and downstream Read/Write
            // calls then ArgumentOutOfRangeException on the raw byte[].
            if (valueSize < 0 || valueSize > endLimit - p)
            {
                r.Issues.Add($"{contextName}: tag '{tagName}' value blob ({valueSize}b) overruns end at offset {p} (limit {endLimit})");
                return false;
            }
            int valueEnd = p + valueSize;

            if (string.Equals(typeName, "ObjectProperty",    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ClassProperty",     StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "ComponentProperty", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "InterfaceProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (valueSize != 4)
                {
                    r.Issues.Add($"{contextName}: {typeName} unexpected size {valueSize} (expected 4)");
                    return false;
                }
                int srcRef = ReadInt32(buf, p);
                int tgtRef = translator.TranslateObjectReference(srcRef);
                if (srcRef != 0 && tgtRef == 0)
                    r.Issues.Add($"{contextName}: {typeName} '{tagName}' -> object ref {srcRef} has no equivalent in target; wrote null");
                else if (srcRef != 0 && string.Equals(tagName, "parent", StringComparison.OrdinalIgnoreCase))
                {
                    // Diagnostic: for every MaterialInstance.Parent translation,
                    // log source ref + path → target ref + path. Untextured
                    // costumes usually trace to a Parent that translates to an
                    // unexpected target (different base UMaterial across game
                    // versions, parameter slot mismatch downstream).
                    string srcDesc = DescribeRef(translator.Source, srcRef);
                    string tgtDesc = tgtRef == 0 ? "(null)" : DescribeRef(translator.Target, tgtRef);
                    r.Issues.Add($"{contextName}: Parent src ref {srcRef} ({srcDesc}) -> tgt ref {tgtRef} ({tgtDesc})");
                }
                WriteInt32(buf, p, tgtRef);
                p = valueEnd;
            }
            else if (string.Equals(typeName, "NameProperty", StringComparison.OrdinalIgnoreCase))
            {
                if (valueSize != 8)
                {
                    r.Issues.Add($"{contextName}: NameProperty unexpected size {valueSize} (expected 8)");
                    return false;
                }
                int srcNameIdx = ReadInt32(buf, p);
                if (!TryRewriteNameIndex(buf, p, srcNameIdx, $"NameProperty '{tagName}'", contextName, r)) return false;
                p = valueEnd;
            }
            else if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase))
            {
                // Critical: structs are EITHER a nested tagged property stream
                // OR a binary atomic struct (Vector/Color/Rotator/Box/etc.).
                // We can't tell from binary alone, so we PROBE: if the first 4
                // bytes interpreted as an FName index resolve to a known target
                // name AND advancing as a tag stream consumes exactly valueSize
                // bytes ending in "None", treat as tagged. Otherwise treat as
                // atomic binary and leave the bytes alone.
                TryTranslateStructValue(buf, p, valueSize, innerName ?? "(struct)", contextName, r);
                p = valueEnd;
            }
            else if (string.Equals(typeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase))
            {
                // Probe whether this array is an array-of-tagged-structs
                // (where each element is a tagged property stream ending in
                // "None" — e.g. AggGeom.SphylElems, AggGeom.SphereElems,
                // AggGeom.BoxElems) and translate each element if so. Other
                // inner types (Int/Float/Object/Name primitives) are left
                // alone because we can't determine element size from binary.
                TryTranslateArrayValue(buf, p, valueSize, tagName, contextName, r);
                p = valueEnd;
            }
            else if (string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase)
                     && valueSize == 8)
            {
                // An 8-byte ByteProperty value is an FName: the enum entry the
                // property is set to. There is no ambiguity to probe for here —
                // a 1-byte value is a raw byte and an 8-byte value is a name.
                // Source's index for that name means nothing in target, so it
                // must be mapped like any other name.
                //
                // Texture2DBodyWalker already does this, but only for textures
                // it CARRIES. A texture the older costume already has takes the
                // matched path through here instead, and its Format was being
                // written with source's index unchanged. Verified on
                // Psylocke_Classic90sJacket -> Psylocke_ClassicVU:
                // psylocke_classic_vu_smspsk came out declaring its format to
                // be the name "psylocke_classic_vu_norm", and the renderer took
                // a fatal error decoding it. Any matched export with an enum
                // property was exposed to this, not just textures.
                if (!TryRewriteNameIndex(buf, p, ReadInt32(buf, p), $"ByteProperty '{tagName}' enum value", contextName, r))
                    return false;
                p = valueEnd;
            }
            else
            {
                // Int/Float/Str/Map/etc. — no embedded refs in the conservative
                // cases we hit. Bytes pass through unchanged.
                p = valueEnd;
            }

            r.PropertyTagsTranslated++;
        }
        // Reached endLimit without hitting "None". For top-level bodies that's
        // a parse failure; for nested struct sub-streams it's OK only if the
        // caller is happy with exact-size consumption.
        if (p == endLimit) return true;
        r.Issues.Add($"{contextName}: tag stream walked off end without 'None' at offset {p} (limit {endLimit})");
        return false;
    }

    // Probe a StructProperty value blob to decide if it's a tagged sub-stream
    // or an atomic binary struct. Tagged sub-streams are translated in place;
    // atomic structs are left alone. Translation failures inside a tagged
    // sub-stream are recorded as Issues but don't abort the outer walk —
    // the bytes are at worst left partially translated, which is no worse
    // than the previous behaviour.
    private void TryTranslateStructValue(byte[] buf, int p, int valueSize, string structTypeName, string contextName, Result r)
    {
        // Atomic-struct shortcut: a handful of struct types are always raw
        // binary in UE3. Skip the probe for those, both as an optimisation
        // and to avoid corrupting binary that happens to start with a valid
        // FName index by coincidence.
        if (IsKnownAtomicStruct(structTypeName))
            return;

        // Make a probe scratch copy so failed parses don't corrupt the live
        // buffer. If the probe succeeds, copy the translated bytes back.
        byte[] probe = new byte[valueSize];
        Array.Copy(buf, p, probe, 0, valueSize);
        var probeResult = new Result { Body = probe };
        int subP = 0;
        // First check: is the first 4 bytes plausibly a name index for a known
        // name? If not, this is binary.
        if (valueSize < 8) return;
        int firstNameIdx = ReadInt32(probe, 0);
        if (firstNameIdx < 0 || firstNameIdx >= translator.Source.NameTable.Count)
            return;
        string firstName = translator.ResolveSourceName(firstNameIdx);
        if (string.IsNullOrWhiteSpace(firstName) || firstName.StartsWith("(bad"))
            return;
        // It looks tagged. Walk the sub-stream within probe[0..valueSize].
        string subCtx = $"{contextName} > struct[{structTypeName}]";
        bool ok = TranslateTaggedStream(probe, ref subP, valueSize, subCtx, probeResult);
        if (!ok)
        {
            // The probe failed — likely an atomic struct that happens to start
            // with what looks like a name index. Leave the live bytes alone.
            // We DON'T propagate sub-probe issues since they're not real.
            return;
        }
        if (subP != valueSize)
        {
            // Tagged parse didn't consume the full blob — probably partially
            // tagged. Safer to leave bytes alone than to half-translate.
            return;
        }
        // Successful probe — copy translated bytes back and surface a couple
        // of sub-issues if any were recorded (e.g. object refs missing).
        Array.Copy(probe, 0, buf, p, valueSize);
        foreach (var subIssue in probeResult.Issues)
            r.Issues.Add(subIssue);
        r.PropertyTagsTranslated += probeResult.PropertyTagsTranslated;
    }

    // Probe an ArrayProperty value blob to decide if its elements are
    // tagged structs (each element a sub-stream ending in "None") and
    // translate each element if so. Cases handled:
    //   - Array of tagged structs: probe element 0 — if it starts with a
    //     known target-name FName index, walk N elements as tag sub-streams.
    //   - Array of primitives (Int/Float/Object/Name): we can't determine
    //     element size from binary alone, so leave the bytes unchanged and
    //     emit a soft warning.
    // Probe failures roll back via scratch buffer (same pattern as struct).
    private void TryTranslateArrayValue(byte[] buf, int p, int valueSize, string tagName, string contextName, Result r)
    {
        if (valueSize < 4)
            return;
        int count = ReadInt32(buf, p);
        if (count <= 0 || count > 1024) // sanity bound; 1024+ element arrays are unrealistic for our scope
        {
            if (count != 0) r.Issues.Add($"{contextName}: ArrayProperty '{tagName}' implausible count {count}; left unchanged");
            return;
        }
        // Work in a scratch copy so a failed probe doesn't corrupt the live
        // buffer. Same safety pattern as struct probe.
        byte[] probe = new byte[valueSize];
        Array.Copy(buf, p, probe, 0, valueSize);
        var probeResult = new Result { Body = probe };
        int sub = 4; // skip count

        // BUG FIX: before deciding the array is "tagged-struct" or "primitive",
        // check if it looks like a flat array of FObject refs. UE3 stores
        // arrays like ReferencedTextures, Materials, etc. as count + N int32s,
        // each int32 being a positive FObject (export ref) / negative
        // (import ref). Without this pass, source-side export indices leak
        // unchanged into the target file and the engine fails on first read
        // ("Bad export index N/M").
        //
        // Heuristic: valueSize must equal exactly 4 (count) + count * 4
        // (elements) AND every element must be a plausible source-side FObject
        // (in range [-importCount, exportCount]) AND at least one element
        // must translate to a non-zero target ref. That last bit guards
        // against false positives on int/float arrays (whose values usually
        // sit outside the small FObject range).
        if (valueSize == 4 + count * 4)
        {
            int srcExpCount = translator.Source.ExportTable.Count;
            int srcImpCount = translator.Source.ImportTable.Count;
            bool allPlausible = true;
            for (int k = 0; k < count; k++)
            {
                int v = ReadInt32(probe, 4 + k * 4);
                if (v == 0) continue; // null FObject is always fine
                if (v > srcExpCount || v < -srcImpCount) { allPlausible = false; break; }
            }
            if (allPlausible)
            {
                int translatedNonNull = 0;
                for (int k = 0; k < count; k++)
                {
                    int v = ReadInt32(probe, 4 + k * 4);
                    if (v == 0) continue;
                    int t = translator.TranslateObjectReference(v);
                    if (t != 0) translatedNonNull++;
                }
                // Require at least one successful translation; if NONE map
                // anywhere meaningful, this is probably a primitive int/float
                // array that coincidentally has small values.
                if (translatedNonNull > 0)
                {
                    int rewrittenInPlace = 0;
                    for (int k = 0; k < count; k++)
                    {
                        int v = ReadInt32(probe, 4 + k * 4);
                        if (v == 0) continue;
                        int t = translator.TranslateObjectReference(v);
                        // Write the translated value (which is 0/null if the
                        // src ref has no target counterpart — safer than
                        // leaking the raw source idx).
                        WriteInt32(probe, 4 + k * 4, t);
                        rewrittenInPlace++;
                    }
                    Array.Copy(probe, 0, buf, p, valueSize);
                    r.Issues.Add($"{contextName}: ArrayProperty '{tagName}' translated as FObject array ({rewrittenInPlace}/{count} refs rewritten, {translatedNonNull} target-resolved)");
                    r.PropertyTagsTranslated += count;
                    return;
                }
            }
        }

        // Probe element 0: peek the first 4 bytes as a name idx. If it's
        // not a plausible name, bail (array is primitive).
        if (sub + 4 > valueSize) return;
        int firstNameIdx = ReadInt32(probe, sub);
        if (firstNameIdx < 0 || firstNameIdx >= translator.Source.NameTable.Count)
        {
            r.Issues.Add($"{contextName}: ArrayProperty '{tagName}' ({valueSize}b, {count} items) appears to be primitive (first element doesn't start with a name index); not deep-translated");
            return;
        }
        string firstName = translator.ResolveSourceName(firstNameIdx);
        if (string.IsNullOrWhiteSpace(firstName) || firstName.StartsWith("(bad"))
        {
            r.Issues.Add($"{contextName}: ArrayProperty '{tagName}' element 0 first name resolves badly ('{firstName}'); not deep-translated");
            return;
        }

        // Walk each element as a tag sub-stream ending in "None".
        string subCtx = $"{contextName} > array[{tagName}]";
        for (int i = 0; i < count; i++)
        {
            int beforeElement = sub;
            bool ok = TranslateTaggedStream(probe, ref sub, valueSize, $"{subCtx}[{i}]", probeResult);
            if (!ok)
            {
                // Element parse failed: not a tagged-struct array. Abort and
                // leave the live bytes alone (probe scratch is discarded).
                return;
            }
            if (sub <= beforeElement)
            {
                // Defensive — empty element shouldn't happen; bail to avoid
                // infinite loop.
                return;
            }
        }
        if (sub != valueSize)
        {
            // Parsed elements consumed less than the declared value blob —
            // we likely misidentified the inner type. Safer to leave alone.
            return;
        }
        // Success: copy translated bytes back, propagate any sub-issues.
        Array.Copy(probe, 0, buf, p, valueSize);
        foreach (var subIssue in probeResult.Issues)
            r.Issues.Add(subIssue);
        r.PropertyTagsTranslated += probeResult.PropertyTagsTranslated;
    }

    // Scans the binary tail (bytes from `start` to end of `buf`) for FObject
    // arrays serialized as (count + N int32 refs). For each match, translates
    // the elements through the IndexTranslator. Conservative pattern detection
    // to avoid corrupting non-FObject binary data:
    //   - count must be 1..64 (real material UniformExpressionTextures arrays
    //     in MH UPKs top out around 10–20; 64 is comfortable headroom)
    //   - EVERY element must be a plausible source-side FObject ref AND
    //     either be 0 (null) OR resolve to a real source-side export/import
    //   - At least one element must translate to a non-zero target ref —
    //     guards against runs of small ints that happen to fit the range
    //     but aren't actually FObjects (e.g. an array of counts).
    private void TranslateBinaryTailFObjectArrays(byte[] buf, int start, string contextName, Result r)
    {
        int srcExp = translator.Source.ExportTable.Count;
        int srcImp = translator.Source.ImportTable.Count;
        int p = start;
        // 4-byte align to the body's int32 grid. Body offsets aren't always
        // aligned to file int32 grid, but property tags + binary tail
        // serialize as packed int32 streams from the body's start, so the
        // grid relative to body[0] is what matters here.
        while (p + 4 <= buf.Length)
        {
            int count = ReadInt32(buf, p);
            if (count <= 0 || count > 64) { p += 4; continue; }
            int neededBytes = 4 + count * 4;
            if (p + neededBytes > buf.Length) { p += 4; continue; }
            // Quick plausibility scan
            bool allPlausible = true;
            for (int k = 0; k < count; k++)
            {
                int v = ReadInt32(buf, p + 4 + k * 4);
                if (v == 0) continue;
                if (v > srcExp || v < -srcImp) { allPlausible = false; break; }
            }
            if (!allPlausible) { p += 4; continue; }
            // At least one element must translate to a non-zero target ref.
            int nonZeroTranslated = 0;
            for (int k = 0; k < count; k++)
            {
                int v = ReadInt32(buf, p + 4 + k * 4);
                if (v == 0) continue;
                int t = translator.TranslateObjectReference(v);
                if (t != 0) nonZeroTranslated++;
            }
            if (nonZeroTranslated == 0) { p += 4; continue; }
            // Apply translation. Source refs that don't resolve get written
            // as 0 (null) — safer than leaking the raw source idx.
            int writtenChanges = 0;
            for (int k = 0; k < count; k++)
            {
                int v = ReadInt32(buf, p + 4 + k * 4);
                if (v == 0) continue;
                int t = translator.TranslateObjectReference(v);
                WriteInt32(buf, p + 4 + k * 4, t);
                if (t != v) writtenChanges++;
            }
            r.Issues.Add($"{contextName}: binary-tail FObject array at body[0x{p:X}] (count {count}): {writtenChanges} ref(s) rewritten ({nonZeroTranslated} target-resolved)");
            r.PropertyTagsTranslated += count;
            p += neededBytes; // skip past this whole array
        }
    }

    // Struct types whose serialised form is pure binary (no inner tag stream).
    // Conservative list — UE3 has many more, but these are the ones common
    // enough to need a fast path. Anything else falls through to the probe.
    private static bool IsKnownAtomicStruct(string structTypeName) => structTypeName switch
    {
        "Vector" or "Vector2D" or "Vector4" or "Plane"
            or "Rotator" or "Quat" or "Matrix" or "Box" or "Box2D"
            or "Color" or "LinearColor" or "Guid"
            or "Sphere" or "BoxSphereBounds" or "IntPoint" or "IntRect"
            or "TwoVectors" or "Range" or "RangeVector" or "InterpCurvePointFloat"
            or "InterpCurvePointVector" or "InterpCurvePointVector2D"
            or "InterpCurvePointLinearColor" or "InterpCurveLinearColor"
            or "InterpCurveFloat" or "InterpCurveVector" or "InterpCurveVector2D"
            => true,
        _ => false,
    };

    private bool TryRewriteNameIndex(byte[] buf, int offsetOfNameIdx, int srcNameIdx, string kind, string contextName, Result r)
    {
        int tgtIdx = translator.TranslateNameIndex(srcNameIdx);
        if (tgtIdx < 0)
        {
            string srcName = translator.ResolveSourceName(srcNameIdx);
            r.Issues.Add($"{contextName}: {kind} name '{srcName}' (src idx {srcNameIdx}) is not present in target NameTable — cannot translate");
            return false;
        }
        WriteInt32(buf, offsetOfNameIdx, tgtIdx);
        return true;
    }

    // Best-effort description of an FObject ref against a given header.
    // Returns "EXPORT <class> '<path>'" for positive refs, "IMPORT <class>
    // '<path>'" for negative, or a diagnostic string on failure. Used by
    // Parent-property logging so MIC parent translations are auditable in
    // the saved report without having to reload the UPK.
    private static string DescribeRef(UpkManager.Models.UpkFile.UnrealHeader header, int rawRef)
    {
        try
        {
            if (rawRef == 0) return "(null)";
            var entry = header.GetObjectTableEntry(rawRef);
            if (entry == null) return $"(no entry for {rawRef})";
            string kind = rawRef > 0 ? "EXPORT" : "IMPORT";
            string cls = "?";
            if (entry is UpkManager.Models.UpkFile.Tables.UnrealExportTableEntry exp)
                cls = exp.ClassReferenceNameIndex?.Name ?? "?";
            else if (entry is UpkManager.Models.UpkFile.Tables.UnrealImportTableEntry imp)
                cls = imp.ClassNameIndex?.Name ?? "?";
            string path = entry.GetPathName();
            return $"{kind} {cls} '{path}'";
        }
        catch (Exception ex)
        {
            return $"(describe failed: {ex.GetType().Name})";
        }
    }

    private static int ReadInt32(byte[] buf, int offset) => BitConverter.ToInt32(buf, offset);
    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset    ] = (byte)( value        & 0xFF);
        buf[offset + 1] = (byte)((value >>  8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
