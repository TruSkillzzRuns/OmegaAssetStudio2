using System.Buffers.Binary;

namespace OmegaAssetStudio.Calligraphy;

// Calligraphy prototype body parser. Format reverse-engineered empirically by walking the
// raw bytes of multiple known-good prototype files end-to-end (e.g. SelectActGangCombatIdleAggro,
// SelectGameCenterSkrullDuoTalk) and verifying the decoded byte stream consumes exactly the
// file length.
//
// Layout (after the 4-byte PTP header has been validated by the caller):
//   [byte]       flags  (bit 0 = has parent, bit 1 = has field data)
//   [uint64]     parent prototype ID                                  (if flag bit 0)
//   --- if flag bit 1: ---
//   [uint16 LE]  field group count
//   for each group:
//      [uint64]     declaring blueprint ID
//      [byte]       blueprint copy number
//      [uint16 LE]  simple field count
//      for each simple field:
//          [uint64]   field id
//          [byte]     type code (A/B/C/D/L/P/R/S/T)
//          [value]    encoding depends on type
//      [uint16 LE]  list field count
//      for each list field:
//          [uint64]     field id
//          [byte]       type code
//          [uint16 LE]  element count
//          for each element: [value]
//
// Value encodings:
//   A,P,C,T,S  -> uint64 LE          (asset/prototype/curve/type/string id)
//   B          -> 1 byte             (bool)
//   D          -> 8 bytes double LE  (IEEE-754)
//   L          -> 8 bytes int64 LE
//   R          -> recursive prototype body (flags + optional parent + optional groups)
public sealed class PrototypeParser
{
    private readonly byte[] _data;
    private int _pos;
    private readonly List<string> _trace = new();
    private readonly bool _isF2Variant;

    public PrototypeBody Result { get; private set; } = new();
    public IReadOnlyList<string> Trace => _trace;
    public int FinalPosition => _pos;

    public PrototypeParser(byte[] data)
    {
        // ~0.8% of the corpus (UI/MetaGame, Mods/Omega/Bonuses, KismetSequences, some boss
        // powers, props) carries a 2-byte prefix (high-nibble usually 0xF) before the
        // canonical PTP header. Observed prefix-bytes: 0xF0, 0xF1, 0xF2. Strip transparently.
        // The body that follows uses a variant field encoding I haven't fully reverse-
        // engineered yet (B/L appear 6 bytes in some places, listCount handling differs);
        // the parser surfaces the header/parent/group preamble and skips the field section
        // rather than throwing.
        if (data.Length > 5 && (data[0] & 0xF0) == 0xF0 &&
            data[2] == (byte)'P' && data[3] == (byte)'T' && data[4] == (byte)'P')
        {
            byte[] stripped = new byte[data.Length - 2];
            Array.Copy(data, 2, stripped, 0, stripped.Length);
            data = stripped;
            _isF2Variant = true;
        }
        _data = data;
    }

    public bool IsF2Variant => _isF2Variant;

    public bool TryParse(out string error)
    {
        try
        {
            ParsePrototypeBody(Result, isRhStruct: false);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            if (_isF2Variant)
            {
                // The variant body encoding isn't fully decoded yet. Surface whatever the
                // canonical parser captured before the exception (header / parent / group
                // blueprint refs) and report success so callers can still walk references.
                Result.IsPartialF2Variant = true;
                error = string.Empty;
                return true;
            }
            error = $"{ex.GetType().Name} at offset 0x{_pos:X4}: {ex.Message}";
            return false;
        }
    }

    private void ParsePrototypeBody(PrototypeBody body, bool isRhStruct)
    {
        if (!isRhStruct)
        {
            if (_data.Length < 4) throw new InvalidDataException("data too short for header");
            _pos = 4;
        }

        byte flags = ReadByte();
        body.Flags = flags;

        if ((flags & 0x01) != 0)
            body.ParentPrototypeId = ReadUInt64();

        if ((flags & 0x02) == 0)
            return;

        int groupCount = ReadUInt16();
        body.FieldGroupCount = groupCount;

        for (int g = 0; g < groupCount; g++)
        {
            var group = new FieldGroup
            {
                DeclaringBlueprintId = ReadUInt64(),
                BlueprintCopyNumber = ReadByte()
            };

            int simpleCount = ReadUInt16();
            for (int i = 0; i < simpleCount; i++)
                group.SimpleFields.Add(ReadField(isList: false));

            int listCount = ReadUInt16();
            for (int i = 0; i < listCount; i++)
                group.ListFields.Add(ReadField(isList: true));

            body.Groups.Add(group);
        }
    }

    private Field ReadField(bool isList)
    {
        var f = new Field
        {
            IsList = isList,
            FieldId = ReadUInt64(),
        };

        byte type = ReadByte();
        f.TypeCode = (char)type;
        if (!IsValidTypeCode(type))
            throw new InvalidDataException($"unknown field type code 0x{type:X2} ('{(char)type}')");

        if (isList)
        {
            int n = ReadUInt16();
            for (int i = 0; i < n; i++)
                f.Values.Add(ReadValue(type));
        }
        else
        {
            f.Values.Add(ReadValue(type));
        }
        return f;
    }

    private object ReadValue(byte typeCode)
    {
        switch ((char)typeCode)
        {
            case 'A':
            case 'P':
            case 'C':
            case 'T':
            case 'S':
                return ReadUInt64();
            case 'B':
                // Bool values are stored 8-byte aligned: 1 byte payload + 7 bytes zero padding.
                bool b = ReadByte() != 0;
                if (_pos + 7 > _data.Length) throw new InvalidDataException("read past end of data (B padding)");
                _pos += 7;
                return b;
            case 'D':
                return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64()));
            case 'L':
                return unchecked((long)ReadUInt64());
            case 'R':
                var nested = new PrototypeBody();
                ParsePrototypeBody(nested, isRhStruct: true);
                return nested;
            default:
                throw new InvalidDataException($"unhandled type code '{(char)typeCode}'");
        }
    }

    private static bool IsValidTypeCode(byte b) =>
        b == (byte)'A' || b == (byte)'B' || b == (byte)'C' || b == (byte)'D' ||
        b == (byte)'L' || b == (byte)'P' || b == (byte)'R' || b == (byte)'S' || b == (byte)'T';

    private byte ReadByte()
    {
        if (_pos >= _data.Length) throw new InvalidDataException("read past end of data");
        return _data[_pos++];
    }

    private ushort ReadUInt16()
    {
        if (_pos + 2 > _data.Length) throw new InvalidDataException("read past end of data (uint16)");
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    private ulong ReadUInt64()
    {
        if (_pos + 8 > _data.Length) throw new InvalidDataException("read past end of data (uint64)");
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_pos, 8));
        _pos += 8;
        return v;
    }
}

public sealed class PrototypeBody
{
    public byte Flags { get; set; }
    public ulong? ParentPrototypeId { get; set; }
    public int FieldGroupCount { get; set; }
    public List<FieldGroup> Groups { get; } = new();
    // True when the body comes from an F2-prefixed variant whose field-section encoding
    // isn't fully decoded yet — header/parent/group blueprint refs are still valid.
    public bool IsPartialF2Variant { get; set; }
}

public sealed class FieldGroup
{
    public ulong DeclaringBlueprintId { get; set; }
    public int BlueprintCopyNumber { get; set; }
    public List<Field> SimpleFields { get; } = new();
    public List<Field> ListFields { get; } = new();
}

public sealed class Field
{
    public int FieldPrefix { get; set; } // unused under the new layout; kept for binary compat with existing callers.
    public ulong FieldId { get; set; }
    public char TypeCode { get; set; }
    public bool IsList { get; set; }
    public List<object> Values { get; } = new();
}
