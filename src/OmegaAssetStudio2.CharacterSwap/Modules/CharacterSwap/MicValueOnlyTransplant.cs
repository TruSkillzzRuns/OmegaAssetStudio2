using System;
using System.Collections.Generic;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Classes;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.Modules.CharacterSwap;

// MIC value-only transplant.
//
// Keep target's MIC body byte-for-byte (proven 1.52 v868 shader inheritance,
// intact FMaterialResource tail) and overwrite ONLY the ScalarParameter and
// TextureParameterValues' inner value bytes with source's values — translated
// into target's index namespace. Same chassis, new paint job.
//
// This avoids the failure mode where transplanting source's MIC body brings
// its 1.53 pre-compiled FMaterialResource tail along with it; the tail's
// uniform-expression sampler indices don't line up with target's v868 base
// shader and parts of the costume render as the bright-blue engine
// uncompiled-shader fallback.
internal static class MicValueOnlyTransplant
{
    internal sealed class Result
    {
        public byte[] PatchedBytes = Array.Empty<byte>();
        public int ScalarsPatched;
        public int TexturesPatched;
        public List<string> SkippedSourceNames = new();
        public List<string> UntouchedTargetNames = new();
        public List<string> Issues = new();
        public bool Success;
    }

    public static Result Apply(
        UnrealExportTableEntry sourceMicEntry,
        UnrealExportTableEntry targetMicEntry,
        UnrealHeader sourceHeader,
        UnrealHeader targetHeader,
        Func<int, int> translateSourceRefToTarget)
    {
        var result = new Result();

        if (sourceMicEntry.UnrealObject is not IUnrealObject su
            || su.UObject is not UMaterialInstanceConstant srcMic)
        {
            result.Issues.Add("source MIC: typed model not available — cannot read parameter values");
            return result;
        }
        if (targetMicEntry.UnrealObject is not IUnrealObject tu
            || tu.UObject is not UMaterialInstanceConstant)
        {
            result.Issues.Add("target MIC: typed model not available — cannot anchor parameter offsets");
            return result;
        }

        byte[] targetBody = targetMicEntry.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();
        if (targetBody.Length == 0)
        {
            result.Issues.Add("target MIC: zero-length body");
            return result;
        }
        byte[] sourceBody = sourceMicEntry.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>();

        var patcher = new MaterialBytePatcher();
        var tgtOffsets = patcher.Locate(targetBody, targetHeader);
        var srcOffsets = sourceBody.Length > 0
            ? patcher.Locate(sourceBody, sourceHeader)
            : new MaterialBytePatcher.ParameterOffsets();
        result.Issues.Add($"chassis body={targetBody.Length}B (scalars exposed={tgtOffsets.Scalars.Count}, textures exposed={tgtOffsets.Textures.Count}); source body={sourceBody.Length}B (scalars={srcOffsets.Scalars.Count}, textures={srcOffsets.Textures.Count})");

        byte[] patched = (byte[])targetBody.Clone();

        // -- Scalars --
        if (srcMic.ScalarParameterValues != null)
        {
            var sourceNamesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in srcMic.ScalarParameterValues)
            {
                string name = sp?.ParameterName?.Name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;
                sourceNamesTouched.Add(name);
                if (!tgtOffsets.Scalars.TryGetValue(name, out var so))
                {
                    result.SkippedSourceNames.Add($"scalar:{name} (target's MIC doesn't expose this param)");
                    continue;
                }
                BitConverter.GetBytes(sp!.ParameterValue).CopyTo(patched, so.ValueOffset);
                result.ScalarsPatched++;
            }
            foreach (var kv in tgtOffsets.Scalars)
                if (!sourceNamesTouched.Contains(kv.Key))
                    result.UntouchedTargetNames.Add($"scalar:{kv.Key} (kept target's value)");
        }

        // -- Textures --
        if (srcMic.TextureParameterValues != null)
        {
            var sourceNamesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tp in srcMic.TextureParameterValues)
            {
                string name = tp?.ParameterName?.Name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;
                sourceNamesTouched.Add(name);
                if (!tgtOffsets.Textures.TryGetValue(name, out var to))
                {
                    result.SkippedSourceNames.Add($"texture:{name} (target's MIC doesn't expose this param)");
                    continue;
                }
                if (!srcOffsets.Textures.TryGetValue(name, out var sourceTo))
                {
                    result.Issues.Add($"texture:{name}: source MIC body byte-offsets disagree with typed parse — skipped");
                    continue;
                }
                int srcRef = BitConverter.ToInt32(sourceBody, sourceTo.ValueOffset);
                int tgtRef = translateSourceRefToTarget(srcRef);
                if (tgtRef == 0)
                {
                    result.Issues.Add($"texture:{name}: source ref {srcRef} did not translate (writing null)");
                }
                BitConverter.GetBytes(tgtRef).CopyTo(patched, to.ValueOffset);
                result.TexturesPatched++;
            }
            foreach (var kv in tgtOffsets.Textures)
                if (!sourceNamesTouched.Contains(kv.Key))
                    result.UntouchedTargetNames.Add($"texture:{kv.Key} (kept target's value)");
        }

        // -- FMaterialResource tail: uniform-expression-texture refs --
        //
        // bHasStaticPermutationResource = True on costume MICs in this game.
        // That means the FMaterialResource tail carries a precompiled shader
        // whose uniform-expression-texture array lists the EXACT texture refs
        // the compiled shader will sample — overriding the property-stream
        // TextureParameterValues. Without patching this array, the chassis
        // (target MIC) renders with TARGET's textures regardless of the
        // property-stream overrides we just wrote, because the cached shader
        // samples through the cached uniform refs.
        //
        // Both MICs parent through the same base material (after the dblsided
        // alias), so the compiled shader's uniform-expression order is the
        // same in both. We can copy source MIC's array (each element
        // translated into target's namespace) on top of chassis's array at
        // the same offset.
        try
        {
            int chassisArrayOffset = FindUniformTextureArray(patched, tgtOffsets.PropertyTableEndOffset);
            int sourceArrayOffset = FindUniformTextureArray(sourceBody,  srcOffsets.PropertyTableEndOffset);
            if (chassisArrayOffset > 0 && sourceArrayOffset > 0)
            {
                int chassisCount = BitConverter.ToInt32(patched, chassisArrayOffset);
                int sourceCount  = BitConverter.ToInt32(sourceBody, sourceArrayOffset);
                if (chassisCount == sourceCount && chassisCount > 0 && chassisCount <= 64)
                {
                    int patchedTextures = 0;
                    for (int k = 0; k < chassisCount; k++)
                    {
                        int srcRef = BitConverter.ToInt32(sourceBody, sourceArrayOffset + 4 + k * 4);
                        if (srcRef == 0)
                        {
                            BitConverter.GetBytes(0).CopyTo(patched, chassisArrayOffset + 4 + k * 4);
                            continue;
                        }
                        int tgtRef = translateSourceRefToTarget(srcRef);
                        BitConverter.GetBytes(tgtRef).CopyTo(patched, chassisArrayOffset + 4 + k * 4);
                        if (tgtRef != 0) patchedTextures++;
                    }
                    result.Issues.Add($"tail uniform-texture array: patched {patchedTextures}/{chassisCount} refs (chassis@0x{chassisArrayOffset:X}, source@0x{sourceArrayOffset:X})");
                }
                else
                {
                    result.Issues.Add($"tail uniform-texture array: count mismatch chassis={chassisCount} vs source={sourceCount}; LEFT chassis tail untouched (may render with chassis textures on shader sample paths)");
                }
            }
            else
            {
                result.Issues.Add($"tail uniform-texture array: not located (chassisOffset={chassisArrayOffset}, sourceOffset={sourceArrayOffset}); chassis tail unpatched");
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"tail uniform-texture array patch threw: {ex.GetType().Name}: {ex.Message}");
        }

        // Flip bHasStaticPermutationResource: True -> False.
        //
        // The chassis body kept its original chassis bool=True. That tells
        // the engine "use the cached FMaterialResource tail bytes as the
        // compiled shader." Those bytes were baked for chassis's vertex
        // factor. the donor mesh has a different vertex factor (different
        // skin format, bone count, or morph targets) — the cached shader
        // fails to bind on the skin sections that need the alternate
        // vertex-factor permutation, and the engine falls back to the
        // bright-blue uncompiled-shader debug color.
        //
        // Flipping to False makes the engine *ignore* the tail and recompile
        // the shader at load time from the PARENT material's expression
        // graph (chbasematerial_v2-1) against the donor mesh's actual vertex
        // factor. Same expression graph, fresh permutation, correct skin
        // sampling.
        try
        {
            int boolOff = FindBoolPropertyValueOffset(patched, targetHeader, "bHasStaticPermutationResource");
            if (boolOff > 0 && boolOff < patched.Length)
            {
                byte oldVal = patched[boolOff];
                patched[boolOff] = 0;
                // Truncate the body to the end of the property stream. The
                // engine's UMaterialInstance::Serialize reads the tagged
                // properties (including bHasStaticPermutationResource=False),
                // then since the bool is False it does NOT consume any
                // FMaterialResource tail bytes. UE3's FArchive then verifies
                // that the total bytes consumed equals the export's
                // SerialDataSize — if there's extra slack, it errors with
                // "Serial size mismatch: Got N, Expected M". So we must
                // delete the cached chassis FMaterialResource tail bytes.
                int truncTo = tgtOffsets.PropertyTableEndOffset;
                if (truncTo > 0 && truncTo < patched.Length)
                {
                    byte[] trimmed = new byte[truncTo];
                    Buffer.BlockCopy(patched, 0, trimmed, 0, truncTo);
                    patched = trimmed;
                    result.Issues.Add($"bHasStaticPermutationResource flipped {oldVal}->0 at body[0x{boolOff:X}]; body truncated from {targetBody.Length} to {truncTo} bytes (FMaterialResource tail dropped). Engine recompiles shader from parent material's expression graph for the donor's vertex factor.");
                }
                else
                {
                    result.Issues.Add($"bHasStaticPermutationResource flipped {oldVal}->0 at body[0x{boolOff:X}] BUT body NOT truncated (PropertyTableEndOffset={truncTo}); engine will fail with Serial size mismatch.");
                }
            }
            else
            {
                result.Issues.Add($"bHasStaticPermutationResource not located in chassis body — engine will use cached chassis shader tail (likely renders skin as engine debug blue)");
            }
        }
        catch (Exception ex)
        {
            result.Issues.Add($"bHasStaticPermutationResource flip threw: {ex.GetType().Name}: {ex.Message}");
        }

        result.PatchedBytes = patched;
        result.Success = true;
        return result;
    }

    // Walk the body's tagged property stream and return the byte offset of
    // the named BoolProperty's value byte (the single byte after the 24-byte
    // tag header). Returns -1 if not found or any name lookup fails.
    private static int FindBoolPropertyValueOffset(byte[] body, UnrealHeader header, string targetName)
    {
        int pos = 4; // skip NetIndex
        while (pos + 24 <= body.Length)
        {
            int nameIdx = BitConverter.ToInt32(body, pos);
            if (nameIdx < 0 || nameIdx >= header.NameTable.Count) return -1;
            string propName = header.NameTable[nameIdx]?.Name?.String ?? string.Empty;
            pos += 8; // name + numeric suffix
            if (string.Equals(propName, "None", StringComparison.OrdinalIgnoreCase)) return -1;

            int typeIdx = BitConverter.ToInt32(body, pos);
            if (typeIdx < 0 || typeIdx >= header.NameTable.Count) return -1;
            string typeName = header.NameTable[typeIdx]?.Name?.String ?? string.Empty;
            pos += 8; // type + numeric suffix

            int size = BitConverter.ToInt32(body, pos); pos += 4;
            pos += 4; // arrayIdx

            bool isBool = string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase);
            bool isStruct = string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase);
            bool isByte = string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase);

            if (isStruct || isByte) pos += 8; // inner type/struct name

            if (isBool)
            {
                if (string.Equals(propName, targetName, StringComparison.OrdinalIgnoreCase))
                    return pos;
                pos += 1;
            }
            else
            {
                pos += size;
            }
        }
        return -1;
    }

    // Structural walker that mirrors MaterialProbe's FMaterialResource head
    // parse. Returns the byte offset of the int32 COUNT field for the
    // UniformExpressionTextures array (refs start 4 bytes later). Returns -1
    // if any field is out of range or the bit-0 of the mask is unset (no
    // resource follows).
    //
    // Layout from `tailStart` (= position right after the property table's
    // "None" terminator):
    //   uint32  mask                     — bit 0 = "resource follows"
    //   int32   stringCount
    //   for each string:
    //     int32 len  (positive=UTF8 byte count, negative=UTF16 char count*-1)
    //     byte[] data  (|len| bytes if positive, 2*|len| if negative)
    //   int32   mapCount                 (TMap<UMaterialExpression*, int>)
    //   skip    mapCount * 8 bytes       (4-byte ref + 4-byte value each)
    //   int32   maxTexDep
    //   byte[16] resourceId
    //   int32   numUv
    //   int32   uniformTexCount          ← we want this offset
    //   int32[] uniformTexRefs           (uniformTexCount entries)
    private static int FindUniformTextureArray(byte[] body, int tailStart)
    {
        if (tailStart < 0 || tailStart + 4 > body.Length) return -1;
        int p = tailStart;
        try
        {
            uint mask = BitConverter.ToUInt32(body, p); p += 4;
            if ((mask & 1) == 0) return -1;

            int stringCount = BitConverter.ToInt32(body, p); p += 4;
            if (stringCount < 0 || stringCount > 16) return -1;
            for (int i = 0; i < stringCount; i++)
            {
                if (p + 4 > body.Length) return -1;
                int len = BitConverter.ToInt32(body, p); p += 4;
                int byteCount = len >= 0 ? len : -len * 2;
                if (byteCount < 0 || byteCount > 4096) return -1;
                p += byteCount;
                if (p > body.Length) return -1;
            }

            if (p + 4 > body.Length) return -1;
            int mapCount = BitConverter.ToInt32(body, p); p += 4;
            if (mapCount < 0 || mapCount > 256) return -1;
            p += mapCount * 8;
            if (p > body.Length) return -1;

            if (p + 4 > body.Length) return -1;
            int maxTexDep = BitConverter.ToInt32(body, p); p += 4;
            if (maxTexDep < -1 || maxTexDep > 64) return -1;

            // GUID (16 bytes)
            p += 16;
            if (p > body.Length) return -1;

            if (p + 4 > body.Length) return -1;
            int numUv = BitConverter.ToInt32(body, p); p += 4;
            if (numUv < 0 || numUv > 8) return -1;

            // uniformTex count is the int32 at `p`. Caller writes into refs
            // starting at p+4 for `count` int32 entries.
            if (p + 4 > body.Length) return -1;
            int uniformTexCount = BitConverter.ToInt32(body, p);
            if (uniformTexCount < 0 || uniformTexCount > 64) return -1;
            if (p + 4 + uniformTexCount * 4 > body.Length) return -1;
            return p;
        }
        catch
        {
            return -1;
        }
    }
}
