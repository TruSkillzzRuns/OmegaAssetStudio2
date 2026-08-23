using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio.Calligraphy;

// Blueprint (BPT/v11) parser, derived purely from raw-byte inspection of 9 sample blueprints.
// Layout (post-4-byte header):
//   uint16 nameLength
//   utf8   name
//   uint64 parentBlueprintId
//   uint16 contributingCount
//   entry  contributingEntries[contributingCount]    // each: 8-byte id + 1-byte flag
//   uint16 runtimeBindingCount
//   entry  runtimeBindings[runtimeBindingCount]      // same 9-byte shape
//   uint16 newMemberCount
//   member newMembers[newMemberCount]
//
// Member:
//   uint64 memberId
//   uint16 nameLength
//   utf8   name
//   byte   baseTypeCode    ('A','B','C','D','L','P','R','S','T')
//   byte   structureTypeCode ('S' simple, 'L' list)
//   --- type-specific suffix ---
//   For baseType == 'P' (Prototype): uint64 subtypePrototypeRef
//   (Other types observed to have no suffix; further types may need rules added.)
public sealed class BlueprintParser
{
    private readonly byte[] _data;
    private int _pos;

    public BlueprintFile Result { get; } = new();

    public BlueprintParser(byte[] data)
    {
        // ~0.5% of blueprints carry a 2-byte `F0/F1/F2 XX` prefix before the canonical BPT
        // header (same variant pattern as prototypes). Strip transparently.
        if (data.Length > 5 && (data[0] & 0xF0) == 0xF0 &&
            data[2] == (byte)'B' && data[3] == (byte)'P' && data[4] == (byte)'T')
        {
            byte[] stripped = new byte[data.Length - 2];
            Array.Copy(data, 2, stripped, 0, stripped.Length);
            data = stripped;
        }
        _data = data;
    }

    public int BytesConsumed => _pos;
    public int FileLength => _data.Length;
    public bool FullyConsumed => _pos == _data.Length;

    public bool TryParse(out string error)
    {
        try
        {
            if (_data.Length < 4 || _data[0] != 'B' || _data[1] != 'P' || _data[2] != 'T')
                throw new InvalidDataException("not a BPT file");
            _pos = 4;

            Result.Name = ReadLengthPrefixedString();
            Result.ParentBlueprintId = ReadUInt64();

            int contributingCount = ReadUInt16();
            for (int i = 0; i < contributingCount; i++)
                Result.ContributingEntries.Add(ReadBlueprintRefEntry());

            int bindingCount = ReadUInt16();
            for (int i = 0; i < bindingCount; i++)
                Result.RuntimeBindings.Add(ReadBlueprintRefEntry());

            int memberCount = ReadUInt16();
            for (int i = 0; i < memberCount; i++)
                Result.NewMembers.Add(ReadMember());

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} at offset 0x{_pos:X4}: {ex.Message}";
            return false;
        }
    }

    private BlueprintRefEntry ReadBlueprintRefEntry()
    {
        ulong id = ReadUInt64();
        byte flag = ReadByte();
        return new BlueprintRefEntry(id, flag);
    }

    private BlueprintMember ReadMember()
    {
        var m = new BlueprintMember
        {
            MemberId = ReadUInt64(),
            Name = ReadLengthPrefixedString(),
            BaseTypeCode = (char)ReadByte(),
            StructureTypeCode = (char)ReadByte()
        };

        // Type-specific suffix: reference-style types carry an additional 8-byte
        // schema reference (subtype prototype, asset type, curve type, rhstruct schema, type ref).
        // Confirmed by exact match between residual-byte count and (Asset + RHStruct + Curve) member totals.
        if (IsReferenceType(m.BaseTypeCode))
            m.SubtypeRef = ReadUInt64();

        return m;
    }

    private static bool IsReferenceType(char typeCode) =>
        typeCode == 'P' || typeCode == 'A' || typeCode == 'C' || typeCode == 'R' || typeCode == 'T';

    private string ReadLengthPrefixedString()
    {
        int len = ReadUInt16();
        if (_pos + len > _data.Length) throw new InvalidDataException($"string length {len} overruns file");
        string s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    private byte ReadByte()
    {
        if (_pos >= _data.Length) throw new InvalidDataException("read past end");
        return _data[_pos++];
    }

    private ushort ReadUInt16()
    {
        if (_pos + 2 > _data.Length) throw new InvalidDataException("read past end (uint16)");
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    private ulong ReadUInt64()
    {
        if (_pos + 8 > _data.Length) throw new InvalidDataException("read past end (uint64)");
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }
}

public sealed class BlueprintFile
{
    public string Name { get; set; } = string.Empty;
    public ulong ParentBlueprintId { get; set; }
    public List<BlueprintRefEntry> ContributingEntries { get; } = new();
    public List<BlueprintRefEntry> RuntimeBindings { get; } = new();
    public List<BlueprintMember> NewMembers { get; } = new();
}

public readonly record struct BlueprintRefEntry(ulong BlueprintId, byte Flag);

public sealed class BlueprintMember
{
    public ulong MemberId { get; set; }
    public string Name { get; set; } = string.Empty;
    public char BaseTypeCode { get; set; }
    public char StructureTypeCode { get; set; }
    public ulong? SubtypeRef { get; set; }
}

// Global field-id → name lookup, built once by walking every .blueprint in Calligraphy.sip.
// Prototype decoders consult this to render `field_<hex>` rows as their actual names.
public sealed class FieldNameRegistry
{
    private readonly Dictionary<ulong, FieldDef> _byId = new();

    public int Count => _byId.Count;

    public void AddFromBlueprint(BlueprintFile bp)
    {
        foreach (var m in bp.NewMembers)
        {
            // First-seen wins so we don't clobber a name with a later same-id occurrence.
            if (!_byId.ContainsKey(m.MemberId))
                _byId[m.MemberId] = new FieldDef(m.Name, m.BaseTypeCode, m.StructureTypeCode, bp.Name);
        }
    }

    public bool TryGet(ulong fieldId, out FieldDef def) => _byId.TryGetValue(fieldId, out def!);

    public IReadOnlyDictionary<ulong, FieldDef> All => _byId;

    public readonly record struct FieldDef(string Name, char TypeCode, char ContainerCode, string DeclaringBlueprint);
}
