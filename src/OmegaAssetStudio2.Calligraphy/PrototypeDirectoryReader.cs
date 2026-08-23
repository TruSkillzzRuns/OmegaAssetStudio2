using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio.Calligraphy;

// Reads Calligraphy/Prototype.directory — the master index that maps the uint64 asset
// IDs used in prototype 'P', 'A', and 'S' fields to their actual archive paths.
//
// Without this, a prototype field like  `A IconPath = 0x1469B7BBA1A015BF`  is just an
// opaque hash. After parsing the directory, that hash resolves to e.g.
//   "UI/Powers/Icons/<Hero>/<UltimateSkill>.texture" or similar,
// giving us the human-readable name (and the UPK asset path for the icon).
//
// Format (reverse-engineered from byte inspection):
//   [4 bytes]  "PDR\x0B"                       — magic + version
//   [4 bytes]  uint32 LE                       — entry count
//   then per entry:
//     [8 bytes]   uint64 LE                    — asset ID
//     [17 bytes]  metadata                     — flags/parent/etc. (skipped)
//     [2 bytes]   uint16 LE                    — path byte length
//     [N bytes]   UTF-8 path                   — uses Windows-style backslashes
public sealed class PrototypeDirectoryReader
{
    private readonly Dictionary<ulong, string> _idToPath;
    // Per-entry raw byte spans into the original directory blob. Keyed by
    // asset ID. Captures the FULL entry layout (8 id + 17 metadata + 2
    // pathlen + N pathbytes) so a writer can append entries verbatim from
    // a source directory into a target directory without having to know
    // what's in the 17 metadata bytes — the format spec doesn't fully
    // document them, and preserving them byte-for-byte is the safe path.
    private readonly Dictionary<ulong, (int Offset, int Length)>? _rawEntrySpans;
    private readonly byte[]? _rawBlob;

    public IReadOnlyDictionary<ulong, string> IdToPath => _idToPath;
    public int EntryCount => _idToPath.Count;

    private PrototypeDirectoryReader(Dictionary<ulong, string> idToPath,
                                      byte[]? rawBlob,
                                      Dictionary<ulong, (int Offset, int Length)>? rawEntrySpans)
    {
        _idToPath = idToPath;
        _rawBlob = rawBlob;
        _rawEntrySpans = rawEntrySpans;
    }

    // Returns the raw 27+N-byte directory entry for an asset ID (id + 17
    // metadata + 2 pathlen + N pathbytes). Used by PrototypeDirectoryWriter
    // to copy entries verbatim from a source directory into a target. Returns
    // null if the ID isn't in the directory or if this reader wasn't built
    // with raw-blob capture (TryRead always captures; TryReadFromBlob below
    // is the explicit capturing entry point).
    public ReadOnlySpan<byte> GetRawEntryBytes(ulong id)
    {
        if (_rawBlob == null || _rawEntrySpans == null) return ReadOnlySpan<byte>.Empty;
        if (!_rawEntrySpans.TryGetValue(id, out var span)) return ReadOnlySpan<byte>.Empty;
        return _rawBlob.AsSpan(span.Offset, span.Length);
    }

    public static PrototypeDirectoryReader? TryRead(byte[] data)
    {
        if (data.Length < 8 ||
            data[0] != (byte)'P' || data[1] != (byte)'D' || data[2] != (byte)'R')
            return null;

        int pos = 8;
        var map = new Dictionary<ulong, string>();
        var spans = new Dictionary<ulong, (int Offset, int Length)>();
        while (pos + 27 <= data.Length)
        {
            int entryStart = pos;
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
            pos += 8;
            pos += 17; // metadata block; size empirically fixed at 17 bytes for v0x0B PDR.
            if (pos + 2 > data.Length) break;
            int pathLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
            if (pos + pathLen > data.Length || pathLen < 0 || pathLen > 4096) break;
            string path = Encoding.UTF8.GetString(data, pos, pathLen).Replace('\\', '/');
            pos += pathLen;

            // Many directory entries point at paths NOT in the archive (they reference
            // localization keys, type system nodes, etc.). Store them all — the caller
            // decides whether to resolve via the archive or just use the path string.
            map[id] = path;
            // Last-write-wins on duplicate ID is fine (rare; both rows are valid).
            spans[id] = (entryStart, pos - entryStart);
        }
        return new PrototypeDirectoryReader(map, data, spans);
    }

    public static PrototypeDirectoryReader? LoadFromArchive(KapgArchiveReader archive)
    {
        if (!archive.TryFindByName("Calligraphy/Prototype.directory", out var entry)) return null;
        try
        {
            byte[] data = archive.ExtractEntry(entry);
            return TryRead(data);
        }
        catch { return null; }
    }

    // Convenience: extract a likely readable name from a resolved path. Uses the last
    // path segment without extension. e.g.
    //   "Powers/Player/<Hero>/Names/<Skill>.prototype" -> "<Skill>"
    public bool TryGetReadableName(ulong assetId, out string name)
    {
        name = string.Empty;
        if (!_idToPath.TryGetValue(assetId, out var path)) return false;
        int slash = path.LastIndexOf('/');
        string file = slash >= 0 ? path.Substring(slash + 1) : path;
        int dot = file.LastIndexOf('.');
        name = dot > 0 ? file.Substring(0, dot) : file;
        return name.Length > 0;
    }
}
