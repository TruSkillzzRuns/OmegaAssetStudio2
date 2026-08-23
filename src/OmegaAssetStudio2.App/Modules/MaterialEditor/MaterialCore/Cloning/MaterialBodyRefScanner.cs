using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Read-only walker that enumerates every FObject reference embedded in a
// UMaterial / UMaterialInstance body. Mirrors the MicBodyRewriter dispatch
// tree but records refs to a callback instead of translating + emitting.
//
// Used by Import Full Material to identify which source-UPK exports the
// Material's body needs so they can be copied across as local dest exports
// (rather than left as Imports pointing back at the donor UPK).
public static class MaterialBodyRefScanner
{
    public sealed record ScanResult(IReadOnlyCollection<int> PositiveExportRefs);

    public static ScanResult Scan(byte[] body, UnrealHeader header, string? sourceExportClass = null)
    {
        var refs = new HashSet<int>();
        if (body.Length <= 4) return new(refs);

        var names = new string[header.NameTable.Count];
        for (int i = 0; i < names.Length; i++) names[i] = header.NameTable[i].Name?.String ?? "";

        using var br = new BinaryReader(new MemoryStream(body, writable: false));
        br.ReadInt32(); // NetIndex

        bool hasStaticPermutation = false;
        ScanTaggedPropertyBlock(br, refs, names, onBool: (name, val) =>
        {
            if (string.Equals(name, "bHasStaticPermutationResource",
                              StringComparison.OrdinalIgnoreCase) && val)
                hasStaticPermutation = true;
        });

        // Binary tail walk for known classes.
        long left = br.BaseStream.Length - br.BaseStream.Position;
        if (left > 0)
        {
            byte[] tail = br.ReadBytes((int)left);
            string cls = (sourceExportClass ?? "").ToLowerInvariant();
            try
            {
                if (cls == "material")
                    ScanFMaterialResourceArray(tail, refs, hasStaticParameters: false);
                else if (cls == "materialinstanceconstant" || cls == "materialinstancetimevarying")
                    ScanFMaterialResourceArray(tail, refs, hasStaticParameters: hasStaticPermutation);
            }
            catch { /* tail format unexpected — skip */ }
        }
        return new(refs);
    }

    private static void ScanTaggedPropertyBlock(
        BinaryReader br, HashSet<int> refs, string[] names,
        Action<string, bool>? onBool)
    {
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            int nameIdx = br.ReadInt32();
            br.ReadInt32();
            string propertyName = (nameIdx >= 0 && nameIdx < names.Length) ? names[nameIdx] : "";
            if (string.Equals(propertyName, "None", StringComparison.OrdinalIgnoreCase)) return;

            int typeIdx = br.ReadInt32(); br.ReadInt32();
            string typeName = (typeIdx >= 0 && typeIdx < names.Length) ? names[typeIdx] : "";
            int size = br.ReadInt32(); br.ReadInt32();

            switch (typeName)
            {
                case "BoolProperty":
                    {
                        byte v = br.ReadByte();
                        onBool?.Invoke(propertyName, v != 0);
                        break;
                    }
                case "ObjectProperty":
                case "InterfaceProperty":
                case "ComponentProperty":
                case "ClassProperty":
                    {
                        int sourceRef = br.ReadInt32();
                        if (sourceRef > 0) refs.Add(sourceRef);
                        break;
                    }
                case "NameProperty":
                    br.ReadBytes(8);
                    break;
                case "ByteProperty":
                    br.ReadBytes(8); // Enum FName
                    if (size == 8) br.ReadBytes(8); else br.ReadBytes(size);
                    break;
                case "ArrayProperty":
                    ScanArrayPayload(br, refs, size, propertyName, names);
                    break;
                case "StructProperty":
                    ScanStructPayload(br, refs, size, names);
                    break;
                default:
                    br.ReadBytes(size);
                    break;
            }
        }
    }

    private static void ScanArrayPayload(
        BinaryReader br, HashSet<int> refs, int size, string arrayName, string[] names)
    {
        long start = br.BaseStream.Position;
        int count = br.ReadInt32();
        switch (arrayName)
        {
            case "TextureParameterValues":
                for (int i = 0; i < count; i++)
                {
                    br.ReadBytes(8);
                    int r = br.ReadInt32(); if (r > 0) refs.Add(r);
                    br.ReadBytes(16);
                }
                break;
            case "ScalarParameterValues":
                for (int i = 0; i < count; i++) br.ReadBytes(28);
                break;
            case "VectorParameterValues":
                for (int i = 0; i < count; i++) br.ReadBytes(40);
                break;
            case "FontParameterValues":
                for (int i = 0; i < count; i++)
                {
                    br.ReadBytes(8);
                    int r = br.ReadInt32(); if (r > 0) refs.Add(r);
                    br.ReadBytes(20);
                }
                break;
            case "Expressions":
            case "FunctionExpressions":
            case "EditorComments":
            case "UniformExpressionTextures":
                for (int i = 0; i < count; i++)
                {
                    int r = br.ReadInt32();
                    if (r > 0) refs.Add(r);
                }
                break;
            case "MaterialFunctionInfos":
                for (int i = 0; i < count; i++)
                {
                    br.ReadBytes(16);
                    int r = br.ReadInt32(); if (r > 0) refs.Add(r);
                }
                break;
            default:
                {
                    long consumed = br.BaseStream.Position - start;
                    int remaining = size - (int)consumed;
                    if (remaining > 0) br.ReadBytes(remaining);
                    break;
                }
        }
    }

    private static readonly HashSet<string> AtomicStructNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vector", "Vector2D", "Vector4", "IntPoint", "IntVector",
        "Rotator", "Quat", "Plane", "Matrix",
        "Color", "LinearColor",
        "Guid",
        "Box", "BoxSphereBounds", "Sphere",
        "Range", "RangeVector",
        "TwoVectors",
    };

    private static void ScanStructPayload(
        BinaryReader br, HashSet<int> refs, int size, string[] names)
    {
        int idx = br.ReadInt32(); br.ReadInt32();
        string structName = (idx >= 0 && idx < names.Length) ? names[idx] : "";
        if (size <= 0) return;
        if (AtomicStructNames.Contains(structName))
        {
            br.ReadBytes(size);
            return;
        }
        // Nested tagged-prop block — recurse.
        long end = br.BaseStream.Position + size;
        try
        {
            byte[] sub = br.ReadBytes(size);
            using var subBr = new BinaryReader(new MemoryStream(sub, writable: false));
            ScanTaggedPropertyBlock(subBr, refs, names, onBool: null);
        }
        catch
        {
            br.BaseStream.Position = end;
        }
    }

    private static void ScanFMaterialResourceArray(
        byte[] tail, HashSet<int> refs, bool hasStaticParameters)
    {
        if (tail.Length < 4) return;
        using var br = new BinaryReader(new MemoryStream(tail, writable: false));
        uint qualityMask = br.ReadUInt32();
        for (int q = 0; q < 2; q++)
        {
            if ((qualityMask & (1u << q)) == 0) continue;
            if (!ScanOneFMaterialResource(br, refs)) return;
            if (hasStaticParameters)
                if (!ScanOneFStaticParameterSet(br, refs)) return;
        }
    }

    private static bool ScanOneFMaterialResource(BinaryReader br, HashSet<int> refs)
    {
        try
        {
            // CompileErrors UArray<string>
            int sc = br.ReadInt32();
            for (int i = 0; i < sc; i++)
            {
                int len = br.ReadInt32();
                if (len == 0) continue;
                int bytes = len > 0 ? len : -len * 2;
                br.ReadBytes(bytes);
            }
            // TextureDependencyLengthMap
            int mc = br.ReadInt32();
            for (int i = 0; i < mc; i++)
            {
                int r = br.ReadInt32(); if (r > 0) refs.Add(r);
                br.ReadInt32();
            }
            br.ReadInt32();          // MaxTextureDependencyLength
            br.ReadBytes(16);        // Id
            br.ReadInt32();          // NumUserTexCoords
            // UniformExpressionTextures
            int tc = br.ReadInt32();
            for (int i = 0; i < tc; i++)
            {
                int r = br.ReadInt32(); if (r > 0) refs.Add(r);
            }
            br.ReadBytes(5 * 4 + 4); // 5 bools + UsingTransforms
            int lc = br.ReadInt32(); if (lc > 0) br.ReadBytes(lc * 16);
            br.ReadUInt32();         // DummyDroppedFallbackComponents
            br.ReadBytes(12);        // BlendModeOverrideValue + 2 bools
            return true;
        }
        catch { return false; }
    }

    private static bool ScanOneFStaticParameterSet(BinaryReader br, HashSet<int> refs)
    {
        try
        {
            br.ReadBytes(16);                    // BaseMaterialId
            int ss = br.ReadInt32();
            for (int i = 0; i < ss; i++) br.ReadBytes(8 + 4 + 4 + 16);
            int cm = br.ReadInt32();
            for (int i = 0; i < cm; i++) br.ReadBytes(8 + 5 * 4 + 16);
            int np = br.ReadInt32();
            for (int i = 0; i < np; i++) br.ReadBytes(8 + 1 + 4 + 16);
            int tl = br.ReadInt32();
            for (int i = 0; i < tl; i++) br.ReadBytes(8 + 4 + 4 + 16);
            return true;
        }
        catch { return false; }
    }
}
