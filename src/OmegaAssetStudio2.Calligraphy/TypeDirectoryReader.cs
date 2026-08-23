using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio.Calligraphy;

// Reads a Calligraphy `*.type` directory file (TYP\x0B). Maps the uint64 asset IDs
// used in prototype 'A' fields (PowerUnrealClass, PowerCategory, etc.) to their
// human-readable identifiers — typically UnrealScript class short names like
// "PowerThor_ThunderHammer" or "PowerCaptainAmerica_DeathFromAbove_TheCaptain".
//
// Without this, a prototype field like
//     A PowerUnrealClass = 0x8E7E4155616B14B0
// is opaque. After lookup it resolves to e.g. "PowerThor_ThunderHammer", which is
// the actual class that drives the power's animation in-game.
//
// Format (reverse-engineered from PowerUnrealClass.type by walking entry boundaries):
//   [4 bytes]   "TYP\x0B"                            -- magic + version
//   [2 bytes]   uint16 LE                            -- entry count
//   then per entry:
//     [8 bytes]   uint64 LE                          -- asset ID
//     [9 bytes]   metadata                           -- flags/parent/etc. (skipped)
//     [2 bytes]   uint16 LE                          -- name byte length
//     [N bytes]   UTF-8 name                         -- e.g. "PowerThor_ThunderHammer"
//
// Cross-check: PowerUnrealClass.type advertises 0x1E4A = 7754 entries, file is 376,526
// bytes, post-header body is 376,520 bytes, average entry ~49 bytes — matches the
// observed entries (8 ID + 9 meta + 2 len + ~30-byte names).
public sealed class TypeDirectoryReader
{
    private readonly Dictionary<ulong, string> _idToName;

    public IReadOnlyDictionary<ulong, string> IdToName => _idToName;
    public int EntryCount => _idToName.Count;

    private TypeDirectoryReader(Dictionary<ulong, string> map) => _idToName = map;

    public static TypeDirectoryReader? TryRead(byte[] data)
    {
        if (data.Length < 6 ||
            data[0] != (byte)'T' || data[1] != (byte)'Y' || data[2] != (byte)'P')
            return null;

        int declaredCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        int pos = 6;
        var map = new Dictionary<ulong, string>(declaredCount);
        while (pos + 8 + 9 + 2 <= data.Length && map.Count < declaredCount)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
            pos += 8;
            pos += 9; // metadata block (parent ref / flags — opaque, skipped).
            if (pos + 2 > data.Length) break;
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
            if (nameLen < 0 || nameLen > 4096 || pos + nameLen > data.Length) break;
            string name = Encoding.UTF8.GetString(data, pos, nameLen);
            pos += nameLen;
            map[id] = name;
        }
        return new TypeDirectoryReader(map);
    }

    public static TypeDirectoryReader? LoadFromArchive(KapgArchiveReader archive, string archivePath)
    {
        if (!archive.TryFindByName(archivePath, out var entry)) return null;
        try
        {
            byte[] data = archive.ExtractEntry(entry);
            return TryRead(data);
        }
        catch { return null; }
    }
}
