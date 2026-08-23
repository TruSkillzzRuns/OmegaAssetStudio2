using UpkManager.Models.UpkFile;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Cloning;

// Read-only walker that locates the offset where a Material / Material
// Instance body's binary tail starts. The body layout:
//
//   [4 bytes NetIndex] [tagged-prop block ending in "None"] [binary tail]
//
// Returns the byte offset of the FIRST byte after the "None" terminator —
// that's where the FMaterialResource[2] tail begins (or where there's just
// EOF if the export has no tail). Used by Import Shaders to splice a donor
// tail onto a destination body while keeping the destination's properties.
public static class MaterialBodySplitter
{
    public sealed record SplitResult(
        byte[] PropertiesBytes,
        byte[] TailBytes,
        bool HasStaticPermutationResource);

    public static SplitResult Split(byte[] body, UnrealHeader header)
    {
        if (body.Length <= 4) return new(body, Array.Empty<byte>(), false);

        using var br = new BinaryReader(new MemoryStream(body, writable: false));
        br.ReadInt32(); // NetIndex
        bool hasStaticPermutation = false;
        long propsEnd = body.Length;

        // Build name lookup.
        var names = new string[header.NameTable.Count];
        for (int i = 0; i < names.Length; i++) names[i] = header.NameTable[i].Name?.String ?? "";

        try
        {
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                int nameIdx = br.ReadInt32();
                int nameNum = br.ReadInt32();
                string propertyName = (nameIdx >= 0 && nameIdx < names.Length) ? names[nameIdx] : "";
                if (string.Equals(propertyName, "None", StringComparison.OrdinalIgnoreCase))
                {
                    propsEnd = br.BaseStream.Position;
                    break;
                }
                int typeIdx = br.ReadInt32(); br.ReadInt32();
                string typeName = (typeIdx >= 0 && typeIdx < names.Length) ? names[typeIdx] : "";
                int size = br.ReadInt32(); br.ReadInt32();

                if (string.Equals(typeName, "BoolProperty", StringComparison.OrdinalIgnoreCase))
                {
                    byte b = br.ReadByte();
                    if (string.Equals(propertyName, "bHasStaticPermutationResource",
                                      StringComparison.OrdinalIgnoreCase) && b != 0)
                        hasStaticPermutation = true;
                    continue;
                }
                if (string.Equals(typeName, "ByteProperty", StringComparison.OrdinalIgnoreCase))
                {
                    br.ReadBytes(8);                    // Enum FName
                    br.ReadBytes(size);
                    continue;
                }
                if (string.Equals(typeName, "StructProperty", StringComparison.OrdinalIgnoreCase))
                {
                    br.ReadBytes(8);                    // StructName FName
                    br.ReadBytes(size);
                    continue;
                }
                br.ReadBytes(size);
            }
        }
        catch
        {
            // Walker failed — return the whole thing as properties, no tail.
            return new(body, Array.Empty<byte>(), hasStaticPermutation);
        }

        var props = new byte[propsEnd];
        Array.Copy(body, 0, props, 0, propsEnd);
        var tail = new byte[body.Length - propsEnd];
        Array.Copy(body, propsEnd, tail, 0, tail.Length);
        return new(props, tail, hasStaticPermutation);
    }
}
