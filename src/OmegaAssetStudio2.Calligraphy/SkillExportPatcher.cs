using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.Cooked;

// Byte-level walker that locates the offsets of editable color values
// inside three kinds of game skill exports. Mirrors the proven pattern in
// MaterialBytePatcher (used by MaterialEditorService for MIC writes) but
// covers the two additional sources verified in
// MHO_HeroSkill_Color_DeepDive.md:
//
//   MaterialExpressionVectorParameter -> "DefaultValue" (FLinearColor, 16 B)
//   ParticleModuleColor               -> "StartColor"  (FRawDistributionVector)
//   ParticleModuleColorOverLife       -> "ColorOverLife" (FRawDistributionVector)
//
// All edits are SIZE-PRESERVING in-place byte patches; the package's
// export-table offsets stay valid and UpkRepacker can splice each modified
// export back without rewriting the header.
//
// UE3 tagged-property layout (repeating, terminated by name "None"):
//   FName  PropertyName        (8 bytes: name index + numeric instance)
//   FName  TypeName            (8 bytes)
//   int32  PropertySize        (size of the value payload in bytes)
//   int32  ArrayIndex          (unused for our targets)
//   FName  ExtraTypeHint       (only when TypeName is StructProperty/ByteProperty)
//   byte[] Value               (PropertySize bytes, or 1 byte for BoolProperty)
//
// FLinearColor value (StructProperty type "LinearColor") is 16 bytes:
//   float R, float G, float B, float A
//
// FRawDistributionVector value (StructProperty type "RawDistributionVector")
// is itself a nested tagged-property stream. Inside it we look for
// "LookupTable" (ArrayProperty containing FloatProperty) which is the
// runtime-sampled color cache — the floats here are what the renderer
// reads in the fast path.
public sealed class SkillExportPatcher
{
    public sealed record FLinearColorOffset(int Offset);            // R float starts here, 16 bytes total
    public sealed record DistributionLookupOffset(int Offset, int FloatCount);  // first float starts here

    // FInterpCurveVector.Points payload. Each point is `Stride` bytes; the
    // OutVal FVector (3 floats — the actual color key) starts at offset
    // (point*stride + 4) — after the InVal float.
    public sealed record InterpCurvePointsOffsets(int Offset, int Count, int Stride);

    // Distribution mode = Constant (DT_Constant / DT_Uniform). Single FVector
    // payload at this offset, 12 bytes (3 floats).
    public sealed record ConstantVectorOffset(int Offset);

    // Bundle of every patchable color region inside a single RawDistributionVector
    // property — LookupTable cache + Distribution.Points curve keys + standalone
    // Constant fallback. Any of these can be null on a given export depending on
    // which distribution mode + whether the runtime cache is populated.
    public sealed record DistributionPatchSites(
        DistributionLookupOffset? Lookup,
        InterpCurvePointsOffsets? Points,
        ConstantVectorOffset? Constant);

    // Temporary diagnostic — returns the names + types found in the tagged
    // property stream of an export body. Used to see why our walker isn't
    // finding "ColorOverLife" on ParticleModuleColorOverLife exports.
    public static List<string> DumpPropertyTags(byte[] exportBytes, UnrealHeader header, int max = 20)
    {
        var list = new List<string>();
        int pos = ResolvePropertyStart(exportBytes, header);
        while (pos + 8 <= exportBytes.Length && list.Count < max)
        {
            if (!TryReadName(exportBytes, pos, header, out string name)) { list.Add($"<name read fail at 0x{pos:X}>"); return list; }
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { list.Add("None"); return list; }
            if (pos + 16 > exportBytes.Length) { list.Add($"<short tag header for {name}>"); return list; }
            if (!TryReadName(exportBytes, pos, header, out string typeName)) { list.Add($"<type read fail for {name}>"); return list; }
            pos += 8;
            int size = BitConverter.ToInt32(exportBytes, pos); pos += 4;
            pos += 4; // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(exportBytes, pos, header, out structType)) { list.Add($"<extra-name fail for {name}>"); return list; }
                pos += 8;
            }
            list.Add($"{name}<{typeName}{(isStruct || isByte ? "/" + structType : "")}>={size}B");
            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return list;
    }

    // DIAGNOSTIC (read-only): brute name-scan + hex preview of an export body.
    // Walks every 4-byte offset, and whenever the int32 there resolves to a
    // valid NameTable entry it reports "@0xNN '<name>'". This reveals the real
    // tagged-property names (Constant, ParameterName, MaxOutput, …) and their
    // byte offsets WITHOUT assuming where the property stream starts — used to
    // decode the UComponent-derived DistributionVector* layout.
    public static List<string> DumpRawNameScan(byte[] body, UnrealHeader header, int maxNames = 60, int hexBytes = 64)
    {
        var list = new List<string>();
        var hex = new System.Text.StringBuilder();
        for (int i = 0; i < hexBytes && i < body.Length; i++) hex.Append(body[i].ToString("X2")).Append(' ');
        list.Add($"hex[0..{Math.Min(hexBytes, body.Length)}]: {hex}");
        int found = 0;
        for (int off = 0; off + 4 <= body.Length && found < maxNames; off += 4)
        {
            int idx = BitConverter.ToInt32(body, off);
            if (idx < 0 || idx >= header.NameTable.Count) continue;
            string nm = header.NameTable[idx].Name?.String ?? string.Empty;
            if (string.IsNullOrEmpty(nm)) continue;
            // Only report plausible identifiers (printable ASCII, len>=2) to cut noise.
            if (nm.Length < 2 || nm.Any(c => c < 32 || c > 126)) continue;
            found++;
            list.Add($"@0x{off:X}: [{idx}] '{nm}'");
        }
        return list;
    }

    // DIAGNOSTIC (read-only): decodes a ParticleSystem(Component)'s
    // InstanceParameters array — the FParticleSysParam structs that feed
    // PARAMETERIZED particle color distributions (UDistributionVectorParticleParameter)
    // at spawn time. Returns (paramName, Vector x/y/z) per entry. Empty list if
    // the export has no InstanceParameters property (→ the color is likely set
    // by UnrealScript instead, not baked here).
    public static List<(string Name, float X, float Y, float Z)> DumpInstanceParameters(byte[] bytes, UnrealHeader header)
    {
        var result = new List<(string, float, float, float)>();
        int pos = sizeof(int);
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string name)) break;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            if (pos + 16 > bytes.Length) break;
            if (!TryReadName(bytes, pos, header, out string typeName)) break;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4; // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            if (isStruct || isByte) { if (!TryReadName(bytes, pos, header, out _)) break; pos += 8; }
            int valueStart = pos;

            if (name.Equals("InstanceParameters", StringComparison.OrdinalIgnoreCase)
                && typeName.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)
                && valueStart + 4 <= bytes.Length)
            {
                int vp = valueStart;
                int count = BitConverter.ToInt32(bytes, vp); vp += 4;
                for (int el = 0; el < count && el < 256 && vp < valueStart + size; el++)
                {
                    string elemName = string.Empty;
                    float vx = 0, vy = 0, vz = 0;
                    int ep = vp;
                    // Walk this struct element's tagged property bag until "None".
                    while (ep + 8 <= bytes.Length)
                    {
                        if (!TryReadName(bytes, ep, header, out string pn)) { ep = -1; break; }
                        ep += 8;
                        if (pn.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
                        if (ep + 16 > bytes.Length) { ep = -1; break; }
                        if (!TryReadName(bytes, ep, header, out string pt)) { ep = -1; break; }
                        ep += 8;
                        int psize = BitConverter.ToInt32(bytes, ep); ep += 4;
                        ep += 4; // ArrayIndex
                        bool pStruct = pt.Equals("StructProperty", StringComparison.OrdinalIgnoreCase);
                        bool pByte = pt.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase);
                        if (pStruct || pByte) { if (!TryReadName(bytes, ep, header, out _)) { ep = -1; break; } ep += 8; }
                        int pval = ep;
                        if (pn.Equals("Name", StringComparison.OrdinalIgnoreCase)
                            && pt.Equals("NameProperty", StringComparison.OrdinalIgnoreCase))
                            TryReadName(bytes, pval, header, out elemName);
                        else if (pn.Equals("Vector", StringComparison.OrdinalIgnoreCase) && pStruct && psize >= 12 && pval + 12 <= bytes.Length)
                        {
                            vx = BitConverter.ToSingle(bytes, pval);
                            vy = BitConverter.ToSingle(bytes, pval + 4);
                            vz = BitConverter.ToSingle(bytes, pval + 8);
                        }
                        ep += pt.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : psize;
                    }
                    if (ep < 0) break;
                    result.Add((elemName, vx, vy, vz));
                    vp = ep;
                }
            }

            pos += typeName.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : size;
        }
        return result;
    }

    // Walks a MaterialExpressionVectorParameter export and returns the offset
    // of the FLinearColor "DefaultValue" property, or null if absent.
    public static FLinearColorOffset? LocateMaterialExpressionDefaultValue(byte[] exportBytes, UnrealHeader header)
    {
        return ScanForLinearColor(exportBytes, header, "DefaultValue");
    }

    // Walks a ParticleModuleColor export and returns the offset of the first
    // float in its StartColor.LookupTable, plus the float count. Null if not
    // present or empty.
    public static DistributionLookupOffset? LocateParticleStartColorLookup(byte[] exportBytes, UnrealHeader header)
    {
        return ScanForRawDistributionLookup(exportBytes, header, "StartColor");
    }

    // Walks a ParticleModuleColorOverLife export and returns the offset of
    // the first float in its ColorOverLife.LookupTable. Null if not present.
    public static DistributionLookupOffset? LocateParticleColorOverLifeLookup(byte[] exportBytes, UnrealHeader header)
    {
        return ScanForRawDistributionLookup(exportBytes, header, "ColorOverLife");
    }

    // Walks a ParticleModuleColorScaleOverLife export — same struct shape
    // as the other two color distributions, just a different outer name.
    public static DistributionLookupOffset? LocateParticleColorScaleOverLifeLookup(byte[] exportBytes, UnrealHeader header)
    {
        return ScanForRawDistributionLookup(exportBytes, header, "ColorScaleOverLife");
    }

    // ===== All-sites locators =====
    // These return EVERY patchable color region inside a RawDistributionVector —
    // the LookupTable cache (engine fast-path read), the FInterpCurveVector
    // Distribution.Points curve keys (engine re-samples from here on load when
    // the cache is empty or stale), AND any standalone Constant FVector
    // (DT_Constant mode).
    //
    // Why all three: patching LookupTable alone is NOT enough on cooked game
    // content. The engine often regenerates the lookup from Distribution.Points
    // at level/particle init, which silently undoes a lookup-only patch.
    // Result before this lift: writer reports "41 color(s) changed" but in
    // game only Material vector params and FLinearColor exports actually
    // change color — particle modules look untouched. Patching Points + Constant
    // alongside LookupTable closes that loop.

    public static DistributionPatchSites LocateParticleStartColorSites(byte[] exportBytes, UnrealHeader header)
        => LocateAllDistributionSites(exportBytes, header, "StartColor");

    public static DistributionPatchSites LocateParticleColorOverLifeSites(byte[] exportBytes, UnrealHeader header)
        => LocateAllDistributionSites(exportBytes, header, "ColorOverLife");

    public static DistributionPatchSites LocateParticleColorScaleOverLifeSites(byte[] exportBytes, UnrealHeader header)
        => LocateAllDistributionSites(exportBytes, header, "ColorScaleOverLife");

    private static DistributionPatchSites LocateAllDistributionSites(byte[] bytes, UnrealHeader header, string outerName)
    {
        int pos = sizeof(int);
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string name)) break;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            if (pos + 16 > bytes.Length) break;
            if (!TryReadName(bytes, pos, header, out string typeName)) break;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) break;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals(outerName, StringComparison.OrdinalIgnoreCase) && isStruct
                && structType.Equals("RawDistributionVector", StringComparison.OrdinalIgnoreCase)
                && valueStart + size <= bytes.Length)
            {
                int innerEnd = valueStart + size;
                DistributionLookupOffset? lookup = ScanLookupTableInStruct(bytes, valueStart, innerEnd, header);
                InterpCurvePointsOffsets? points = ScanInterpCurvePointsInStruct(bytes, valueStart, innerEnd, header);
                ConstantVectorOffset? constant = ScanConstantVectorInStruct(bytes, valueStart, innerEnd, header);
                return new DistributionPatchSites(lookup, points, constant);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return new DistributionPatchSites(null, null, null);
    }

    // Walks the inside of a FRawDistributionVector looking for the
    // Distribution StructProperty (type InterpCurveVector) and, inside that,
    // the Points ArrayProperty. Returns the per-point stride so the writer
    // can iterate without guessing the layout — stride is derived from
    // arrayPayloadSize / pointCount.
    private static InterpCurvePointsOffsets? ScanInterpCurvePointsInStruct(byte[] bytes, int structStart, int structEnd, UnrealHeader header)
    {
        int pos = structStart;
        while (pos + 8 <= structEnd)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > structEnd) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return null;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals("Distribution", StringComparison.OrdinalIgnoreCase) && isStruct
                && (structType.Equals("InterpCurveVector", StringComparison.OrdinalIgnoreCase)
                    || structType.Equals("InterpCurveFloat", StringComparison.OrdinalIgnoreCase)))
            {
                int innerEnd = valueStart + size;
                if (innerEnd > bytes.Length) return null;
                return ScanPointsArrayInInterpCurve(bytes, valueStart, innerEnd, header);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    private static InterpCurvePointsOffsets? ScanPointsArrayInInterpCurve(byte[] bytes, int structStart, int structEnd, UnrealHeader header)
    {
        int pos = structStart;
        while (pos + 8 <= structEnd)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > structEnd) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out _)) return null;
                pos += 8;
            }
            int valueStart = pos;
            bool isArray = string.Equals(typeName, "ArrayProperty", StringComparison.OrdinalIgnoreCase);

            if (name.Equals("Points", StringComparison.OrdinalIgnoreCase) && isArray)
            {
                if (valueStart + 4 > bytes.Length) return null;
                int count = BitConverter.ToInt32(bytes, valueStart);
                if (count <= 0 || count > 1024) return null;
                int dataStart = valueStart + 4;
                int dataLen = (valueStart + size) - dataStart;
                if (dataLen <= 0 || count == 0 || dataLen % count != 0) return null;
                int stride = dataLen / count;
                // FInterpCurvePoint<FVector> minimum layout: InVal(4) + OutVal(12) +
                // ArriveTangent(12) + LeaveTangent(12) + InterpMode(1) = 41B,
                // typically padded to 44. Reject anything too small to hold OutVal.
                if (stride < 4 + 12) return null;
                return new InterpCurvePointsOffsets(dataStart, count, stride);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    // DT_Constant mode: the distribution exposes a single FVector "Constant"
    // value instead of a curve. 12 raw bytes = 3 floats (X, Y, Z).
    // Permissive PMC/PMCOL/PMCSOL locator: walks the ParticleModule body for
    // EVERY top-level StructProperty of type RawDistributionVector, regardless
    // of the property name. game has color modules using non-standard property
    // names (we've seen "Color", custom suffixes, and module subclasses with
    // their own distribution field names). Without this fallback the writer
    // misses those slots — swatches stay original color after save even
    // though the writer ran. Use as a fallback when the named locators
    // (LocateParticleColorOverLifeSites etc.) return empty.
    public static List<DistributionPatchSites> LocateAllParticleModuleDistributionSites(byte[] exportBytes, UnrealHeader header)
    {
        var result = new List<DistributionPatchSites>();
        int pos = sizeof(int);
        while (pos + 8 <= exportBytes.Length)
        {
            if (!TryReadName(exportBytes, pos, header, out string name)) break;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            if (pos + 16 > exportBytes.Length) break;
            if (!TryReadName(exportBytes, pos, header, out string typeName)) break;
            pos += 8;
            int size = BitConverter.ToInt32(exportBytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(exportBytes, pos, header, out structType)) break;
                pos += 8;
            }
            int valueStart = pos;

            if (isStruct && structType.Equals("RawDistributionVector", StringComparison.OrdinalIgnoreCase)
                && valueStart + size <= exportBytes.Length)
            {
                int innerEnd = valueStart + size;
                DistributionLookupOffset? lookup = ScanLookupTableInStruct(exportBytes, valueStart, innerEnd, header);
                InterpCurvePointsOffsets? points = ScanInterpCurvePointsInStruct(exportBytes, valueStart, innerEnd, header);
                ConstantVectorOffset? constant = ScanConstantVectorInStruct(exportBytes, valueStart, innerEnd, header);
                if (lookup is not null || points is not null || constant is not null)
                    result.Add(new DistributionPatchSites(lookup, points, constant));
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return result;
    }

    // Locate every color site at the TOP LEVEL of a UDistributionVector*
    // sub-export's body. Unlike the ParticleModule case (where Distribution
    // lives in an inline RawDistributionVector struct), here the curve /
    // constant lives directly in the sub-export's tagged property stream:
    //
    //   DistributionVectorConstant       — Constant : FVector
    //   DistributionVectorConstantCurve  — ConstantCurve : FInterpCurveVector
    //   DistributionVectorUniform        — Min, Max : FVector
    //   DistributionVectorUniformCurve   — ConstantCurve, MaxConstantCurve : FInterpCurveVector
    //   DistributionVectorParameterBase  — Constant : FVector (+ optional override fields)
    //
    // We don't gate on property name — we patch any FInterpCurveVector struct
    // (curve points) and any FVector struct (single value). Robust to UE3
    // property-name variations between game and stock UE3.
    public static List<DistributionPatchSites> LocateAllVectorSitesInDistributionExport(byte[] exportBytes, UnrealHeader header)
    {
        var result = new List<DistributionPatchSites>();
        int pos = ResolvePropertyStart(exportBytes, header); // component header (16B) vs plain NetIndex (4B)
        while (pos + 8 <= exportBytes.Length)
        {
            if (!TryReadName(exportBytes, pos, header, out string name)) break;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            if (pos + 16 > exportBytes.Length) break;
            if (!TryReadName(exportBytes, pos, header, out string typeName)) break;
            pos += 8;
            int size = BitConverter.ToInt32(exportBytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(exportBytes, pos, header, out structType)) break;
                pos += 8;
            }
            int valueStart = pos;

            if (isStruct && valueStart + size <= exportBytes.Length)
            {
                bool isInterpCurveVec = structType.Equals("InterpCurveVector", StringComparison.OrdinalIgnoreCase);
                bool isVector = structType.Equals("Vector", StringComparison.OrdinalIgnoreCase) && size == 12;

                if (isInterpCurveVec)
                {
                    var pts = ScanPointsArrayInInterpCurve(exportBytes, valueStart, valueStart + size, header);
                    if (pts is not null) result.Add(new DistributionPatchSites(null, pts, null));
                }
                else if (isVector)
                {
                    result.Add(new DistributionPatchSites(null, null, new ConstantVectorOffset(valueStart)));
                }
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return result;
    }

    // For a COLOR-bearing ParticleModule body, return the UE3 object-reference
    // ints of every `Distribution` ObjectProperty nested inside its
    // RawDistributionVector structs. The writer maps these to the dvec
    // sub-exports so it ONLY recolors distributions that genuinely feed color —
    // never the velocity/size/location/spawn distributions that share the very
    // same UDistributionVector* classes. Without this gate, fixing the
    // component-header offset would make every dvec in the package patchable
    // and silently corrupt particle physics.
    public static List<int> LocateColorDistributionObjectRefs(byte[] moduleBytes, UnrealHeader header)
    {
        var refs = new List<int>();
        int pos = ResolvePropertyStart(moduleBytes, header);
        while (pos + 8 <= moduleBytes.Length)
        {
            if (!TryReadName(moduleBytes, pos, header, out string name)) break;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            if (pos + 16 > moduleBytes.Length) break;
            if (!TryReadName(moduleBytes, pos, header, out string typeName)) break;
            pos += 8;
            int size = BitConverter.ToInt32(moduleBytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(moduleBytes, pos, header, out structType)) break;
                pos += 8;
            }
            int valueStart = pos;

            if (isStruct && structType.Equals("RawDistributionVector", StringComparison.OrdinalIgnoreCase)
                && valueStart + size <= moduleBytes.Length)
            {
                ScanDistributionRefInStruct(moduleBytes, valueStart, valueStart + size, header, refs);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return refs;
    }

    private static void ScanDistributionRefInStruct(byte[] bytes, int structStart, int structEnd, UnrealHeader header, List<int> sink)
    {
        int pos = structStart;
        while (pos + 8 <= structEnd)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
            if (pos + 16 > structEnd) return;
            if (!TryReadName(bytes, pos, header, out string typeName)) return;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out _)) return;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals("Distribution", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeName, "ObjectProperty", StringComparison.OrdinalIgnoreCase)
                && size == 4 && valueStart + 4 <= bytes.Length)
            {
                int objRef = BitConverter.ToInt32(bytes, valueStart);
                if (objRef != 0) sink.Add(objRef);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
    }

    private static ConstantVectorOffset? ScanConstantVectorInStruct(byte[] bytes, int structStart, int structEnd, UnrealHeader header)
    {
        int pos = structStart;
        while (pos + 8 <= structEnd)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > structEnd) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return null;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals("Constant", StringComparison.OrdinalIgnoreCase) && isStruct
                && structType.Equals("Vector", StringComparison.OrdinalIgnoreCase)
                && size == 12 && valueStart + 12 <= bytes.Length)
            {
                return new ConstantVectorOffset(valueStart);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    // Per-channel float offsets for a Constant3Vector / Constant4Vector
    // material expression body. R/G/B are present on both; A is null on
    // Constant3Vector. Each value is a single float at the returned offset.
    public sealed record ConstantVectorOffsets(int ROffset, int GOffset, int BOffset, int? AOffset);

    // Walks a MaterialExpressionConstant3Vector export and returns the
    // offsets of its R, G, B FloatProperty values. Null if any of them is
    // missing — we refuse to claim a partial match because that would
    // produce a broken color.
    public static ConstantVectorOffsets? LocateConstant3VectorChannels(byte[] exportBytes, UnrealHeader header)
    {
        int? r = ScanForFloatProperty(exportBytes, header, "R");
        int? g = ScanForFloatProperty(exportBytes, header, "G");
        int? b = ScanForFloatProperty(exportBytes, header, "B");
        if (r is null || g is null || b is null) return null;
        return new ConstantVectorOffsets(r.Value, g.Value, b.Value, null);
    }

    // Same as above, plus the alpha channel. A may be authored = 1 and
    // omitted in the property stream — we still treat that as a valid
    // patchable export and just return AOffset=null, falling back to RGB.
    public static ConstantVectorOffsets? LocateConstant4VectorChannels(byte[] exportBytes, UnrealHeader header)
    {
        int? r = ScanForFloatProperty(exportBytes, header, "R");
        int? g = ScanForFloatProperty(exportBytes, header, "G");
        int? b = ScanForFloatProperty(exportBytes, header, "B");
        int? a = ScanForFloatProperty(exportBytes, header, "A");
        if (r is null || g is null || b is null) return null;
        return new ConstantVectorOffsets(r.Value, g.Value, b.Value, a);
    }

    // Locates a single FloatProperty tag by name and returns the offset of
    // its 4-byte float value. UE3 tagged property layout: name<FName 8B>
    // type<FName 8B "FloatProperty"> size<int32 4B = 4> arrayIndex<int32 4B>
    // value<float 4B>.
    private static int? ScanForFloatProperty(byte[] bytes, UnrealHeader header, string propertyName)
    {
        int pos = sizeof(int);
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > bytes.Length) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;  // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out _)) return null;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(typeName, "FloatProperty", StringComparison.OrdinalIgnoreCase) &&
                size == 4 &&
                valueStart + 4 <= bytes.Length)
            {
                return valueStart;
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    // ===== Internals =====

    private static FLinearColorOffset? ScanForLinearColor(byte[] bytes, UnrealHeader header, string propertyName)
    {
        // The export body begins with a 4-byte NetIndex, then the tagged
        // property stream. Same convention every UE3 UObject uses.
        int pos = sizeof(int);
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > bytes.Length) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;  // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return null;
                pos += 8;
            }
            int valueStart = pos;

            // The match: a StructProperty named e.g. "DefaultValue" of type "LinearColor",
            // 16 bytes payload. If shape doesn't match exactly, refuse to claim it.
            if (name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                isStruct &&
                structType.Equals("LinearColor", StringComparison.OrdinalIgnoreCase) &&
                size == 16 &&
                valueStart + 16 <= bytes.Length)
            {
                return new FLinearColorOffset(valueStart);
            }

            // Advance past the value payload.
            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    private static DistributionLookupOffset? ScanForRawDistributionLookup(byte[] bytes, UnrealHeader header, string propertyName)
    {
        int pos = sizeof(int);
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > bytes.Length) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return null;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                isStruct &&
                structType.Equals("RawDistributionVector", StringComparison.OrdinalIgnoreCase) &&
                valueStart + size <= bytes.Length)
            {
                // Walk inside the struct for the LookupTable array.
                return ScanLookupTableInStruct(bytes, valueStart, valueStart + size, header);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    // FRawDistributionVector IS a nested tagged-property stream
    // (confirmed by EngineProperty.ReadUnrealStructValue which calls
    // prop.ReadProperty in a loop). Inside the struct payload we expect:
    //
    //   Type<ByteProperty>              =1B
    //   Op<ByteProperty>                =1B
    //   LookupTableNumElements<ByteProperty>  =1B
    //   LookupTableChunkSize<ByteProperty>    =1B
    //   LookupTable<ArrayProperty/FloatProperty> = 4 + N*4 bytes
    //   LookupTableTimeScale<FloatProperty>   =4B
    //   LookupTableStartTime<FloatProperty>   =4B
    //   Distribution<ObjectProperty>          =4B
    //   None
    //
    // We walk the sub-stream looking for "LookupTable". Its payload begins
    // with int32 count, then count floats packed contiguously.
    private static DistributionLookupOffset? ScanLookupTableInStruct(byte[] bytes, int structStart, int structEnd, UnrealHeader header)
    {
        return ScanLookupTableInStruct(bytes, structStart, structEnd, header, dumpSink: null);
    }

    private static DistributionLookupOffset? ScanLookupTableInStruct(byte[] bytes, int structStart, int structEnd, UnrealHeader header, List<string>? dumpSink)
    {
        int pos = structStart;
        while (pos + 8 <= structEnd)
        {
            if (!TryReadName(bytes, pos, header, out string name)) return null;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            if (pos + 16 > structEnd) return null;
            if (!TryReadName(bytes, pos, header, out string typeName)) return null;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            bool isArray  = string.Equals(typeName, "ArrayProperty",  StringComparison.OrdinalIgnoreCase);
            string structOrEnum = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structOrEnum)) return null;
                pos += 8;
            }
            int valueStart = pos;

            dumpSink?.Add($"{name}<{typeName}{(structOrEnum.Length > 0 ? "/" + structOrEnum : string.Empty)}>={size}B@0x{valueStart:X}");

            if (name.Equals("LookupTable", StringComparison.OrdinalIgnoreCase) && isArray)
            {
                // ArrayProperty of FloatProperty: int32 count then count*4 bytes.
                if (valueStart + 4 > bytes.Length) return null;
                int count = BitConverter.ToInt32(bytes, valueStart);
                if (count <= 0 || count > 8192) return null;
                int firstFloat = valueStart + 4;
                if (firstFloat + count * 4 > bytes.Length) return null;
                if (firstFloat + count * 4 > valueStart + size) return null;
                return new DistributionLookupOffset(firstFloat, count);
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return null;
    }

    // Public diagnostic — exposes the inner struct walker so the writer can
    // log what's actually present inside a RawDistributionVector when the
    // LookupTable lookup misses.
    public static List<string> DumpDistributionStructInside(byte[] exportBytes, UnrealHeader header, string outerPropertyName)
    {
        var list = new List<string>();
        int pos = sizeof(int);
        while (pos + 8 <= exportBytes.Length)
        {
            if (!TryReadName(exportBytes, pos, header, out string name)) return list;
            pos += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return list;
            if (pos + 16 > exportBytes.Length) return list;
            if (!TryReadName(exportBytes, pos, header, out string typeName)) return list;
            pos += 8;
            int size = BitConverter.ToInt32(exportBytes, pos); pos += 4;
            pos += 4;
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(exportBytes, pos, header, out structType)) return list;
                pos += 8;
            }
            int valueStart = pos;

            if (name.Equals(outerPropertyName, StringComparison.OrdinalIgnoreCase) && isStruct)
            {
                // Dump the first 4 bytes of the struct — those are the inline
                // FRawDistribution header (Type, Op, LookupTableNumElements,
                // LookupTableChunkSize). NumElements * ChunkSize tells the
                // actual stride. Without this we'd just be guessing.
                if (valueStart + 4 <= exportBytes.Length)
                {
                    list.Add($"inline-header bytes=[Type={exportBytes[valueStart]}, Op={exportBytes[valueStart+1]}, NumElements={exportBytes[valueStart+2]}, ChunkSize={exportBytes[valueStart+3]}]");
                }
                ScanLookupTableInStruct(exportBytes, valueStart, valueStart + size, header, list);
                return list;
            }
            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos += size;
        }
        return list;
    }

    // UE3 component-derived exports (every UDistribution* derives from
    // UComponent) prefix the tagged-property stream with:
    //   TemplateOwnerClass  (object ref, 4 B)
    //   TemplateName        (FName, 8 B)
    //   NetIndex            (int32, 4 B)   = 16 B total
    // Plain UObject exports (ParticleModule*, MaterialExpression*) prefix only
    // NetIndex (4 B). We don't know the class here, so probe both: prefer the
    // 16-B component layout, fall back to 4-B, and validate that the candidate
    // start yields a real FName property tag whose TYPE is a known UE3 property
    // type. Returns the byte offset where the property stream begins.
    private static int ResolvePropertyStart(byte[] bytes, UnrealHeader header)
    {
        // Cooked game components use varying header sizes (the TemplateName FName
        // is often serialized as index=-1 / partially), so the property stream
        // can begin at 4, 8, 12, or 16. Probe each: the first offset whose tag
        // is a valid FName followed by a known property TYPE wins. (Names in game
        // packages are lowercased, so the type check is case-insensitive.)
        foreach (int start in new[] { sizeof(int), 8, 12, 16 })
        {
            if (start + 8 > bytes.Length) continue;
            if (!TryReadName(bytes, start, header, out string n)) continue;
            // An empty property bag is just "None" — accept it at this start.
            if (n.Equals("None", StringComparison.OrdinalIgnoreCase)) return start;
            if (start + 16 > bytes.Length) continue;
            if (!TryReadName(bytes, start + 8, header, out string t)) continue;
            if (IsKnownPropertyType(t)) return start;
        }
        return sizeof(int);
    }

    private static readonly HashSet<string> _knownPropertyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "StructProperty", "FloatProperty", "IntProperty", "BoolProperty",
        "ByteProperty", "NameProperty", "StrProperty", "ObjectProperty",
        "ArrayProperty", "ClassProperty", "InterfaceProperty",
        "DelegateProperty", "MapProperty", "ComponentProperty",
    };

    private static bool IsKnownPropertyType(string typeName) =>
        !string.IsNullOrEmpty(typeName) && _knownPropertyTypes.Contains(typeName);

    private static bool TryReadName(byte[] bytes, int pos, UnrealHeader header, out string name)
    {
        name = string.Empty;
        if (pos + 8 > bytes.Length) return false;
        int nameIndex = BitConverter.ToInt32(bytes, pos);
        if (nameIndex < 0 || nameIndex >= header.NameTable.Count) return false;
        name = header.NameTable[nameIndex].Name?.String ?? string.Empty;
        return !string.IsNullOrEmpty(name);
    }

    // ===== Material body color locators =====
    // Find every FLinearColor "Constant" field inside a ColorMaterialInput
    // tagged-property struct within a Material / DecalMaterial body. This is the
    // SAFE replacement for the previous byte-scan body patcher that corrupted
    // NameTable refs.
    //
    // Structure (UE3 cooked tagged-property stream):
    //   FName "EmissiveColor"             (or DiffuseColor / SpecularColor / Opacity)
    //   FName "StructProperty"
    //   int32 Size
    //   int32 ArrayIndex
    //   FName "ColorMaterialInput"        ← struct-type name (or similar)
    //   <Size bytes of nested tagged props>:
    //     FName "Expression"  ObjectProperty  int32 ref
    //     FName "UseConstant" BoolProperty    1 byte
    //     FName "Constant"    StructProperty  size=16  FName "LinearColor"  <16 bytes R,G,B,A>
    //     FName "Mask"/"MaskR"/...           (channel masks)
    //     FName "None"                       (end of inner stream)
    //
    // Returns the byte offset of the FLinearColor's first float (R). Caller
    // patches +0/+4/+8 (R/G/B); alpha at +12 is left alone.
    private static readonly System.Collections.Generic.HashSet<string> _materialColorInputNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "EmissiveColor", "DiffuseColor", "DiffusePower", "SpecularColor",
            "Opacity", "OpacityMask", "Normal",
        };
    private static readonly System.Collections.Generic.HashSet<string> _colorInputStructTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ColorMaterialInput", "VectorMaterialInput",
        };

    public static System.Collections.Generic.List<int> LocateMaterialBodyColorConstants(byte[] bytes, UnrealHeader header)
    {
        var hits = new System.Collections.Generic.List<int>();
        int pos = sizeof(int);    // skip NetIndex (4 bytes)
        while (pos + 8 <= bytes.Length)
        {
            if (!TryReadName(bytes, pos, header, out string propName)) return hits;
            pos += 8;
            if (propName.Equals("None", StringComparison.OrdinalIgnoreCase)) return hits;
            if (pos + 16 > bytes.Length) return hits;
            if (!TryReadName(bytes, pos, header, out string typeName)) return hits;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;  // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return hits;
                pos += 8;
            }
            int valueStart = pos;
            int valueEnd = valueStart + size;
            if (valueEnd > bytes.Length) return hits;

            // Is this an EmissiveColor / DiffuseColor / etc. ColorMaterialInput struct?
            if (isStruct
                && _materialColorInputNames.Contains(propName)
                && _colorInputStructTypes.Contains(structType))
            {
                int innerHit = LocateConstantLinearColorInsideStruct(bytes, valueStart, valueEnd, header);
                if (innerHit >= 0) hits.Add(innerHit);
            }

            // Advance past the value payload.
            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos = valueEnd;
        }
        return hits;
    }

    // Walk a nested tagged-property stream inside a ColorMaterialInput struct.
    // The stream lives between byte ranges [start, end). Returns the byte offset
    // of the FLinearColor "Constant" field's first float, or -1 if not present.
    private static int LocateConstantLinearColorInsideStruct(byte[] bytes, int start, int end, UnrealHeader header)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            if (!TryReadName(bytes, pos, header, out string propName)) return -1;
            pos += 8;
            if (propName.Equals("None", StringComparison.OrdinalIgnoreCase)) return -1;
            if (pos + 16 > end) return -1;
            if (!TryReadName(bytes, pos, header, out string typeName)) return -1;
            pos += 8;
            int size = BitConverter.ToInt32(bytes, pos); pos += 4;
            pos += 4;  // ArrayIndex
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte   = string.Equals(typeName, "ByteProperty",   StringComparison.OrdinalIgnoreCase);
            string structType = string.Empty;
            if (isStruct || isByte)
            {
                if (!TryReadName(bytes, pos, header, out structType)) return -1;
                pos += 8;
            }
            int valueStart = pos;
            int valueEnd = valueStart + size;
            if (valueEnd > end) return -1;

            if (propName.Equals("Constant", StringComparison.OrdinalIgnoreCase)
                && isStruct
                && structType.Equals("LinearColor", StringComparison.OrdinalIgnoreCase)
                && size == 16
                && valueStart + 16 <= bytes.Length)
            {
                return valueStart;
            }

            if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase)) pos += 1;
            else pos = valueEnd;
        }
        return -1;
    }
}
