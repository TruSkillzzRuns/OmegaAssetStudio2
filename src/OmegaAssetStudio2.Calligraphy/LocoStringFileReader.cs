using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio.Calligraphy;

// Parses one Target Game Client `<lang>.all_<HHHHHHHHHHHHHHHH>.string` file.
//
// REVERSE-ENGINEERED FORMAT (verified against a known Ultimate DisplayName
// hash 0x3210183D24E6050B -> "God Blast"):
//
//   HEADER (8 bytes):
//     [4] "STR\x02" magic
//     [4] uint32 LE entry count
//
//   Index region: every entry is anchored by a 4-byte marker `01 00 FF FF`.
//   Relative to a marker at position M:
//     [M-2 .. M-1]    uint16 LE  high 16 bits of THIS entry's hash
//     [M    .. M+ 3]  marker     01 00 FF FF
//     [M+ 4 .. M+ 7]  uint32 LE  string offset (absolute byte offset into the file)
//     [M+ 8 .. M+13]  uint48 LE  low 48 bits of the NEXT entry's hash (lookahead)
//
//   So an entry "owns" 16 bytes (2 before + 14 after the marker). The hash for
//   entry i is reconstructed as:
//     hash[i] = (high16[i] << 48) | low48_lookahead[i-1]
//
//   The first entry's low48 sits in the 6 bytes immediately preceding the
//   first marker (i.e. file bytes [firstMarker-8 .. firstMarker-3]).
//
//   STRINGS BLOB starts after the last marker's 14-byte tail (or, equivalently,
//   at the first non-zero printable byte after index region). Strings are
//   NUL-terminated UTF-8.
//
// Some early entries lack the marker (a short "primer" / sentinel block). We
// skip those and only register entries that have a valid marker AND a string
// offset that points inside the strings blob.
public sealed class LocoStringFileReader
{
    public uint EntryCount { get; }
    public IReadOnlyList<LocoEntry> Entries { get; }
    public IReadOnlyDictionary<ulong, LocoEntry> ByHash { get; }
    private readonly byte[] _data;

    private const uint Marker = 0xFFFF0001;

    public LocoStringFileReader(byte[] data)
    {
        if (data.Length < 8 || data[0] != 'S' || data[1] != 'T' || data[2] != 'R')
            throw new InvalidDataException("Not a STR file.");
        _data = data;
        EntryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));

        // Bound the marker scan to the index region. With ~16 bytes/entry plus
        // some primer/header slack, count*16 + 64 is a safe upper bound.
        int indexEnd = Math.Min(data.Length, 8 + (int)EntryCount * 16 + 256);

        // Collect every marker position in the index region.
        List<int> markerPositions = new((int)EntryCount);
        for (int i = 8; i <= indexEnd - 4; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == Marker)
                markerPositions.Add(i);
        }

        // Decode entries.
        List<LocoEntry> entries = new(markerPositions.Count);
        ulong prevLow48 = 0;
        if (markerPositions.Count > 0)
        {
            int firstM = markerPositions[0];
            if (firstM >= 8) prevLow48 = ReadUInt48LE(data, firstM - 8);
        }
        foreach (int m in markerPositions)
        {
            // Need 2 bytes before and 10 bytes after the marker.
            if (m < 2 || m + 14 > data.Length) continue;
            ushort high16 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(m - 2, 2));
            uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(m + 4, 4));
            ulong nextLow48 = ReadUInt48LE(data, m + 8);
            ulong hash = ((ulong)high16 << 48) | prevLow48;
            entries.Add(new LocoEntry(m, hash, sOff));
            prevLow48 = nextLow48;
        }

        // De-duplicate (keep last) â€” the file does contain some duplicate hashes.
        var byHash = new Dictionary<ulong, LocoEntry>(entries.Count);
        foreach (var e in entries) byHash[e.Hash] = e;

        Entries = entries;
        ByHash = byHash;
    }

    private static ulong ReadUInt48LE(byte[] data, int offset)
    {
        ulong v = 0;
        for (int b = 0; b < 6; b++) v |= (ulong)data[offset + b] << (8 * b);
        return v;
    }

    public static LocoStringFileReader Read(string path)
        => new(File.ReadAllBytes(path));

    // Extract the NUL-terminated UTF-8 string starting at `offset`.
    public string ReadStringAt(uint offset)
    {
        if (offset >= _data.Length) return string.Empty;
        int end = (int)offset;
        int hardCap = (int)Math.Min((long)offset + 8192, _data.Length);
        while (end < hardCap && _data[end] != 0) end++;
        return Encoding.UTF8.GetString(_data, (int)offset, end - (int)offset);
    }

    public bool TryGetString(ulong hash, out string text)
    {
        if (ByHash.TryGetValue(hash, out var e))
        {
            text = ReadStringAt(e.StringOffset);
            return true;
        }
        text = string.Empty;
        return false;
    }
}

public sealed record LocoEntry(int MarkerOffset, ulong Hash, uint StringOffset);

