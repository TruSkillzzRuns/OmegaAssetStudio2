using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor;

// Reads a material's real MaterialId (and a MIC's BaseMaterialId) straight from
// the cooked export bytes — the FMaterial.Id / FStaticParameterSet.BaseMaterialId
// inside the FMaterialResource permutation block. This mirrors the reference
// MHMaterialEditor's byte walker and is robust where the object-model parse
// yields empty GUIDs (the tagged-property path frequently does on cooked game MICs).
//
// Layout (after the tagged-property "None"): uint qualityMask, then per set bit:
//   CompileErrors[] (strings) → TextureDependencyLengthMap (count*8) →
//   MaxTextureDependencyLength(i32) → MaterialId(16) → [MIC only:] NumUserTexCoords →
//   UniformExpressionTextures(count*4) → +24 → TextureLookups(count*16) → +16 →
//   BaseMaterialId(16).
public static class MaterialIdReader
{
    public static (System.Guid? MaterialId, System.Guid? BaseMaterialId) Read(byte[] body, UnrealHeader header, bool isMic, System.Action<string>? log = null)
    {
        if (body is null || body.Length < 12) { log?.Invoke($"[matid] body too short ({body?.Length ?? 0})"); return (null, null); }
        int o = 4; // skip NetIndex

        // Walk tagged properties to "None".
        string lastName = string.Empty;
        while (o < body.Length - 8)
        {
            int nameIdx = RI32(body, ref o); RI32(body, ref o);
            lastName = Name(header, nameIdx);
            if (NameEq(header, nameIdx, "none")) break;
            if (o + 16 > body.Length) { log?.Invoke($"[matid] short tag header at {o} name={lastName}"); return (null, null); }
            int typeIdx = RI32(body, ref o); RI32(body, ref o);
            int size = RI32(body, ref o); RI32(body, ref o);
            string type = Name(header, typeIdx).ToLowerInvariant();
            log?.Invoke($"[matid] prop name={lastName} type={type} size={size} o->{o}");
            if (type == "boolproperty") o += 1;
            else if (type == "byteproperty")
            {
                int enumIdx = RI32(body, ref o); RI32(body, ref o);
                o += NameEq(header, enumIdx, "none") ? 1 : 8;
            }
            else if (type == "structproperty") o += 8 + size;
            else o += size;
            if (o < 0) { log?.Invoke("[matid] negative offset"); return (null, null); }
        }

        log?.Invoke($"[matid] walk end o={o} lastName={lastName} bodyLen={body.Length} isMic={isMic}");
        if (o + 4 > body.Length) { log?.Invoke("[matid] no room for mask"); return (null, null); }
        uint mask = RU32(body, ref o);
        log?.Invoke($"[matid] mask={mask} o={o}");
        if (mask == 0) return (null, null);   // no permutation resource → no own ids

        for (int i = 0; i < 2; i++)
        {
            if (o >= body.Length) break;
            if (((mask >> i) & 1) == 0) continue;

            if (o + 4 > body.Length) break;
            int numCompileErrors = RI32(body, ref o);
            for (int j = 0; j < numCompileErrors && o < body.Length; j++)
            {
                int len = RI32(body, ref o);
                if (len > 0) o += len; else if (len < 0) o += -len * 2;
            }
            if (o + 4 > body.Length) break;
            int numTexDep = RI32(body, ref o); o += numTexDep * 8;
            if (o + 4 > body.Length) break;
            RI32(body, ref o); // MaxTextureDependencyLength
            if (o + 16 > body.Length) break;
            System.Guid matId = ReadGuid(body, o); o += 16;
            log?.Invoke($"[matid] i={i} matId={matId} (after compileErr+texdep, o now {o})");
            if (!isMic) return (matId, null);

            if (o + 4 > body.Length) return (matId, null);
            RI32(body, ref o); // NumUserTexCoords
            if (o + 4 > body.Length) return (matId, null);
            int numUniformTex = RI32(body, ref o); o += numUniformTex * 4;
            o += 24; // 5 bools + UsingTransforms(u32) + DummyDroppedFallbackComponents(u32)
            if (o + 4 > body.Length) return (matId, null);
            int numTexLookups = RI32(body, ref o); o += numTexLookups * 16;
            o += 16;
            if (o + 16 > body.Length) return (matId, null);
            System.Guid baseId = ReadGuid(body, o);
            return (matId, baseId);
        }
        return (null, null);
    }

    private static int RI32(byte[] b, ref int o) { int v = System.BitConverter.ToInt32(b, o); o += 4; return v; }
    private static uint RU32(byte[] b, ref int o) { uint v = System.BitConverter.ToUInt32(b, o); o += 4; return v; }

    private static System.Guid ReadGuid(byte[] b, int o)
    {
        byte[] g = new byte[16];
        System.Array.Copy(b, o, g, 0, 16);
        return new System.Guid(g);
    }

    private static string Name(UnrealHeader header, int idx)
        => (idx >= 0 && idx < header.NameTable.Count) ? (header.NameTable[idx].Name?.String ?? string.Empty) : string.Empty;

    private static bool NameEq(UnrealHeader header, int idx, string lower)
        => Name(header, idx).Equals(lower, System.StringComparison.OrdinalIgnoreCase);
}
