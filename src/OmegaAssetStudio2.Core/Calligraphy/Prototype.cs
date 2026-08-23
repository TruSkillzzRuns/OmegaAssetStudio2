using System.Buffers.Binary;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>One value a definition carries.</summary>
public sealed record PrototypeField
{
    public required ulong Id { get; init; }

    /// <summary>What kind of value it is: 'S' is a piece of display text.</summary>
    public required char Kind { get; init; }

    /// <summary>
    /// The value, as the number the file stores. For display text this is the
    /// key that names the words.
    /// </summary>
    public required ulong Value { get; init; }

    public override string ToString() => $"{Id:X16} {Kind} = {Value:X16}";
}

/// <summary>
/// One definition: what the game knows about a character, a skill, or anything
/// else it ships.
/// </summary>
/// <remarks>
/// Only the values needed to name things are kept. A definition also carries
/// numbers, flags, and whole definitions nested inside it; those are stepped
/// over exactly, because the fields after them cannot be found otherwise.
/// </remarks>
public sealed class Prototype
{
    private const int MaxDepth = 8;
    private const int MaxGroups = 4096;
    private const int MaxFields = 8192;

    private Prototype(ulong parentId, IReadOnlyList<PrototypeField> fields)
    {
        ParentId = parentId;
        Fields = fields;
    }

    /// <summary>The definition this one builds on, or zero.</summary>
    public ulong ParentId { get; }

    public IReadOnlyList<PrototypeField> Fields { get; }

    /// <summary>The value of a field, by its number.</summary>
    public ulong? Find(ulong fieldId)
    {
        foreach (PrototypeField field in Fields)
        {
            if (field.Id == fieldId) return field.Value;
        }

        return null;
    }

    /// <summary>
    /// Reads a definition. Returns null when the bytes are not one, or are a
    /// variant this does not read.
    /// </summary>
    public static Prototype? TryRead(ReadOnlySpan<byte> data) => TryRead(data, out _);

    /// <param name="failure">
    /// Why the definition could not be read. A silent null says nothing about
    /// which part of the format is not understood yet.
    /// </param>
    public static Prototype? TryRead(ReadOnlySpan<byte> data, out string failure)
    {
        failure = string.Empty;

        // A small number carry two extra bytes before the header, and the body
        // after them is encoded differently enough that reading it would
        // produce wrong values rather than none. Those are declined.
        if (data.Length > 5 && (data[0] & 0xF0) == 0xF0 &&
            data[2] == (byte)'P' && data[3] == (byte)'T' && data[4] == (byte)'P')
        {
            failure = "a variant this reader declines";
            return null;
        }

        if (data.Length < 5 || data[0] != 'P' || data[1] != 'T' || data[2] != 'P')
        {
            failure = "not a definition";
            return null;
        }

        try
        {
            var cursor = new Cursor(data, 4);
            var fields = new List<PrototypeField>();

            ulong parentId = ReadBody(ref cursor, fields, depth: 0);

            return new Prototype(parentId, fields);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Reads a definition's body, which a nested value repeats exactly.
    /// </summary>
    /// <returns>What this body builds on, or zero.</returns>
    private static ulong ReadBody(ref Cursor cursor, List<PrototypeField> fields, int depth)
    {
        byte flags = cursor.ReadByte();

        ulong parentId = (flags & 1) != 0 ? cursor.ReadUInt64() : 0;

        // Nothing more is stored unless it says so.
        if ((flags & 2) == 0) return parentId;

        int groups = cursor.ReadUInt16();
        if (groups > MaxGroups) throw new InvalidDataException($"{groups} groups.");

        for (int g = 0; g < groups; g++)
        {
            cursor.ReadUInt64();   // which kind of definition declared these
            cursor.ReadByte();     // which copy of it

            ReadFields(ref cursor, fields, depth, asList: false);
            ReadFields(ref cursor, fields, depth, asList: true);
        }

        return parentId;
    }

    private static void ReadFields(ref Cursor cursor, List<PrototypeField> fields, int depth, bool asList)
    {
        int count = cursor.ReadUInt16();
        if (count > MaxFields) throw new InvalidDataException($"{count} fields.");

        for (int f = 0; f < count; f++)
        {
            ulong id = cursor.ReadUInt64();
            char kind = (char)cursor.ReadByte();

            int values = asList ? cursor.ReadUInt16() : 1;
            if (values > MaxFields) throw new InvalidDataException($"{values} values.");

            for (int v = 0; v < values; v++)
            {
                ulong value = ReadValue(ref cursor, kind, fields, depth);

                // Only the first value of a list is kept: a name is a single
                // value, and keeping the rest would bury it.
                if (v == 0 && kind is not 'R')
                    fields.Add(new PrototypeField { Id = id, Kind = kind, Value = value });
            }
        }
    }

    private static ulong ReadValue(ref Cursor cursor, char kind, List<PrototypeField> fields, int depth)
    {
        switch (kind)
        {
            // Every plain value is eight bytes wide — a key, a reference, a
            // number, and a true-or-false alike. The last of those is worth
            // stating: it holds one byte of meaning and seven of padding, and
            // reading it as a single byte throws every later field out of step.
            // That was found by walking a definition that failed and watching
            // the next field's number arrive as seven zeroes and a stray byte.
            case 'A' or 'P' or 'C' or 'T' or 'S' or 'D' or 'L' or 'B':
                return cursor.ReadUInt64();

            // A whole definition stored inside this one. Its fields are read
            // too, because a name is sometimes kept there.
            case 'R':
                if (depth >= MaxDepth) throw new InvalidDataException("nested too deeply.");
                ReadBody(ref cursor, fields, depth + 1);
                return 0;

            default:
                throw new InvalidDataException(
                    $"unknown value kind 0x{(byte)kind:X2} at {cursor.Position} of {cursor.Length}.");
        }
    }

    private ref struct Cursor(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at = position;

        public readonly int Position => _at;
        public readonly int Length => _data.Length;

        public byte ReadByte() => _data[_at++];

        public ushort ReadUInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_at..]);
            _at += sizeof(ushort);
            return value;
        }

        public ulong ReadUInt64()
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_at..]);
            _at += sizeof(ulong);
            return value;
        }
    }
}
