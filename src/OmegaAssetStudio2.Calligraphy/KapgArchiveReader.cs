using K4os.Compression.LZ4;

namespace OmegaAssetStudio.Calligraphy;

// AUDIT-NOTE: kept as legacy infrastructure, not used by current VFX runtime path (2026-05-12).
// Clean-room reader for TargetClient Calligraphy .sip (KAPG) archives.
// Implemented from the publicly documented format specification:
//   Header  : "KAPG" magic (4 bytes) + version (4 bytes, int32)
//   Count   : int32 NumEntries
//   Entries : sorted by FileHash, each:
//       FileHash         : 8 bytes
//       FileNameLen      : int32
//       FileName         : UTF-8 bytes (FileNameLen long)
//       ModTime          : int32
//       Offset           : int32 (from start of file)
//       CompressedSize   : int32
//       UncompressedSize : int32
//   Data    : LZ4-compressed blobs at the offsets recorded in each entry.
public sealed class KapgArchiveReader : IDisposable
{
    private const uint MagicKapg = 0x4750414B; // bytes 'K','A','P','G' on disk -> 0x4750414B as little-endian uint32

    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly List<KapgEntry> _entries = [];
    private readonly Dictionary<ulong, int> _indexByHash = [];
    private readonly Dictionary<string, int> _indexByName = new(StringComparer.OrdinalIgnoreCase);
    private long _dataSectionStart;

    public int Version { get; private set; }
    public long DataSectionStart => _dataSectionStart;
    public IReadOnlyList<KapgEntry> Entries => _entries;

    public KapgArchiveReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);
        ReadHeader();
        ReadEntryTable();
    }

    private void ReadHeader()
    {
        uint magic = _reader.ReadUInt32();
        if (magic != MagicKapg)
            throw new InvalidDataException($"Not a KAPG archive (magic = 0x{magic:X8}). Expected 0x{MagicKapg:X8}.");

        Version = _reader.ReadInt32();
    }

    private void ReadEntryTable()
    {
        int numEntries = _reader.ReadInt32();
        if (numEntries < 0 || numEntries > 10_000_000)
            throw new InvalidDataException($"Implausible entry count: {numEntries}");

        _entries.Capacity = numEntries;

        for (int i = 0; i < numEntries; i++)
        {
            ulong fileHash = _reader.ReadUInt64();
            int nameLen = _reader.ReadInt32();
            if (nameLen < 0 || nameLen > 4096)
                throw new InvalidDataException($"Implausible filename length at entry {i}: {nameLen}");

            byte[] nameBytes = _reader.ReadBytes(nameLen);
            string name = System.Text.Encoding.UTF8.GetString(nameBytes);

            int modTime = _reader.ReadInt32();
            int offset = _reader.ReadInt32();
            int compressedSize = _reader.ReadInt32();
            int uncompressedSize = _reader.ReadInt32();

            KapgEntry entry = new(i, fileHash, name, modTime, offset, compressedSize, uncompressedSize);
            _entries.Add(entry);
            _indexByHash[fileHash] = i;
            _indexByName[name] = i;
        }

        // Data section begins immediately after the entry table; entry.Offset is relative to here.
        _dataSectionStart = _stream.Position;
    }

    public bool TryFindByName(string name, out KapgEntry entry)
    {
        if (_indexByName.TryGetValue(name, out int idx))
        {
            entry = _entries[idx];
            return true;
        }
        entry = default!;
        return false;
    }

    public bool TryFindByHash(ulong hash, out KapgEntry entry)
    {
        if (_indexByHash.TryGetValue(hash, out int idx))
        {
            entry = _entries[idx];
            return true;
        }
        entry = default!;
        return false;
    }

    public byte[] ExtractEntry(KapgEntry entry)
    {
        if (entry.CompressedSize < 0 || entry.UncompressedSize < 0)
            throw new InvalidDataException($"Entry '{entry.Name}' has invalid sizes (cmp={entry.CompressedSize}, ucp={entry.UncompressedSize}).");

        byte[] raw = ReadRawBytes(entry);

        if (entry.CompressedSize == entry.UncompressedSize)
            return raw;

        byte[] decompressed = new byte[entry.UncompressedSize];
        int decoded = LZ4Codec.Decode(raw, 0, raw.Length, decompressed, 0, decompressed.Length);
        if (decoded != entry.UncompressedSize)
            throw new InvalidDataException(
                $"LZ4 decode for '{entry.Name}' produced {decoded} bytes; expected {entry.UncompressedSize}.");

        return decompressed;
    }

    public byte[] ReadRawBytes(KapgEntry entry)
    {
        _stream.Seek(_dataSectionStart + entry.Offset, SeekOrigin.Begin);
        byte[] raw = new byte[entry.CompressedSize];
        _stream.ReadExactly(raw, 0, raw.Length);
        return raw;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

public readonly record struct KapgEntry(
    int Index,
    ulong FileHash,
    string Name,
    int ModTime,
    int Offset,
    int CompressedSize,
    int UncompressedSize);

