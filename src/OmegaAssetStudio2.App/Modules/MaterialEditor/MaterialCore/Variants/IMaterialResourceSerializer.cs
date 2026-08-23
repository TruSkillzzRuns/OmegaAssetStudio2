using System.Text;
using UpkManager.Models.UpkFile.Core;
using UpkManager.Models.UpkFile.Engine.Material;
using UpkManager.Models.UpkFile.Tables;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// Inverse of UpkManager's FMaterial.ReadFields + FMaterialResource.ReadFields.
// Round-trip is the contract: serializing an unmodified parsed resource
// must produce bytes identical to what the parser read.
//
// UE3 reference encoding used throughout:
//   - export ref  =  TableIndex + 1   (positive)
//   - import ref  = -(TableIndex + 1) (negative)
//   - null ref    =  0
public interface IMaterialResourceSerializer
{
    byte[] Serialize(FMaterialResource resource);

    // For UMaterialInstance.bHasStaticPermutationResource: emits the
    // qualityMask uint32 + each set-bit's serialized FMaterialResource.
    // Caller separately appends the FStaticParameterSet bytes (a different
    // serializer's job — out of scope here).
    byte[] SerializeStaticPermutationResources(
        FMaterialResource?[] resources,
        uint qualityMask);
}

public sealed class MaterialResourceSerializer : IMaterialResourceSerializer
{
    public byte[] Serialize(FMaterialResource resource)
    {
        if (resource is null) throw new ArgumentNullException(nameof(resource));
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        WriteMaterialFields(bw, resource);
        WriteResourceTrailer(bw, resource);
        bw.Flush();
        return ms.ToArray();
    }

    public byte[] SerializeStaticPermutationResources(FMaterialResource?[] resources, uint qualityMask)
    {
        if (resources is null) throw new ArgumentNullException(nameof(resources));
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(qualityMask);
        for (int q = 0; q < resources.Length; q++)
        {
            if ((qualityMask & (1u << q)) == 0) continue;
            if (resources[q] is null) continue;
            WriteMaterialFields(bw, resources[q]!);
            WriteResourceTrailer(bw, resources[q]!);
            // NOTE: caller is responsible for emitting the matching
            // FStaticParameterSet that follows each FMaterialResource in
            // the UMaterialInstance binary layout.
        }
        bw.Flush();
        return ms.ToArray();
    }

    // -- field-by-field writers, mirroring FMaterial.ReadFields ----------

    private static void WriteMaterialFields(BinaryWriter bw, FMaterialResource r)
    {
        WriteStringArray(bw, r.CompileErrors);
        WriteObjectInt32Map(bw, r.TextureDependencyLengthMap);
        bw.Write(r.MaxTextureDependencyLength);
        WriteGuid(bw, r.Id);
        bw.Write(r.NumUserTexCoords);
        WriteObjectArray(bw, r.UniformExpressionTextures);
        WriteBool(bw, r.bUsesSceneColor);
        WriteBool(bw, r.bUsesSceneDepth);
        WriteBool(bw, r.bUsesDynamicParameter);
        WriteBool(bw, r.bUsesLightmapUVs);
        WriteBool(bw, r.bUsesMaterialVertexPositionOffset);
        bw.Write(r.UsingTransforms);
        WriteTextureLookupArray(bw, r.TextureLookups);
        bw.Write((uint)0); // DummyDroppedFallbackComponents — parser reads and discards
    }

    private static void WriteResourceTrailer(BinaryWriter bw, FMaterialResource r)
    {
        bw.Write((int)r.BlendModeOverrideValue);
        WriteBool(bw, r.bIsBlendModeOverrided);
        WriteBool(bw, r.bIsMaskedOverrideValue);
    }

    // -- primitives -----------------------------------------------------

    private static void WriteBool(BinaryWriter bw, bool v) => bw.Write(v ? 1 : 0);

    private static void WriteGuid(BinaryWriter bw, FGuid guid)
    {
        // FGuid is four int32 fields in UE3 little-endian order.
        bw.Write(guid.A);
        bw.Write(guid.B);
        bw.Write(guid.C);
        bw.Write(guid.D);
    }

    // Inverse of UnrealString.ReadString:
    //   size = 0 → empty
    //   size > 0 → ASCII, size bytes including trailing null
    //   size < 0 → UCS-2, -size * 2 bytes including trailing null pair
    private static void WriteString(BinaryWriter bw, string? s)
    {
        if (string.IsNullOrEmpty(s)) { bw.Write(0); return; }
        bool needsWide = false;
        foreach (var ch in s) if (ch > 0x7F) { needsWide = true; break; }
        if (needsWide)
        {
            int size = -(s.Length + 1); // +1 for null terminator
            bw.Write(size);
            byte[] utf16 = Encoding.Unicode.GetBytes(s);
            bw.Write(utf16);
            bw.Write((ushort)0); // null terminator
        }
        else
        {
            byte[] ascii = Encoding.ASCII.GetBytes(s);
            int size = ascii.Length + 1;
            bw.Write(size);
            bw.Write(ascii);
            bw.Write((byte)0); // null terminator
        }
    }

    // Compute UE3 object reference from an FObject's backing table entry.
    // FObjects with no TableEntry (rare) serialize as null (ref = 0).
    private static int ObjectRef(FObject? obj)
    {
        if (obj?.TableEntry is null) return 0;
        return obj.TableEntry switch
        {
            UnrealExportTableEntry exp => exp.TableIndex + 1,
            UnrealImportTableEntry imp => -(imp.TableIndex + 1),
            _ => 0,
        };
    }

    private static void WriteObject(BinaryWriter bw, FObject? obj) => bw.Write(ObjectRef(obj));

    // -- array / map helpers --------------------------------------------

    private static void WriteStringArray(BinaryWriter bw, IEnumerable<string>? items)
    {
        var list = items?.ToList() ?? new List<string>();
        bw.Write(list.Count);
        foreach (var s in list) WriteString(bw, s);
    }

    private static void WriteObjectArray(BinaryWriter bw, IEnumerable<FObject>? items)
    {
        var list = items?.ToList() ?? new List<FObject>();
        bw.Write(list.Count);
        foreach (var o in list) WriteObject(bw, o);
    }

    private static void WriteObjectInt32Map(BinaryWriter bw, IEnumerable<KeyValuePair<FObject, int>>? items)
    {
        var list = items?.ToList() ?? new List<KeyValuePair<FObject, int>>();
        bw.Write(list.Count);
        foreach (var kv in list) { WriteObject(bw, kv.Key); bw.Write(kv.Value); }
    }

    private static void WriteTextureLookupArray(BinaryWriter bw, IEnumerable<FTextureLookup>? items)
    {
        var list = items?.ToList() ?? new List<FTextureLookup>();
        bw.Write(list.Count);
        foreach (var lookup in list)
        {
            bw.Write(lookup.TexCoordIndex);
            bw.Write(lookup.TextureIndex);
            bw.Write(lookup.UScale);
            bw.Write(lookup.VScale);
        }
    }
}
