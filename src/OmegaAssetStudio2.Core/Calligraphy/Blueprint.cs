using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>What one field of a definition is called, and what it holds.</summary>
public sealed record BlueprintField
{
    public required ulong Id { get; init; }
    public required string Name { get; init; }

    /// <summary>What kind of value it holds: 'S' is a piece of display text.</summary>
    public required char ValueKind { get; init; }

    /// <summary>'S' for a single value, 'L' for a list of them.</summary>
    public required char Shape { get; init; }

    public override string ToString() => $"{Name} ({ValueKind}{Shape})";
}

/// <summary>
/// Names the fields that definitions of one kind carry.
/// </summary>
/// <remarks>
/// A definition stores its fields by number, not by name — so on its own, a
/// character's definition is a list of numbers with no way to tell which one is
/// the name shown in the game. That mapping lives here.
/// </remarks>
public sealed class Blueprint
{
    private Blueprint(string name, ulong parentId, IReadOnlyList<BlueprintField> fields)
    {
        Name = name;
        ParentId = parentId;
        Fields = fields;
    }

    public string Name { get; }

    /// <summary>The definition this one builds on, or zero.</summary>
    public ulong ParentId { get; }

    public IReadOnlyList<BlueprintField> Fields { get; }

    /// <summary>
    /// Reads a blueprint. Returns null when the bytes are not one, or are a
    /// variant this does not read.
    /// </summary>
    public static Blueprint? TryRead(ReadOnlySpan<byte> data) => TryRead(data, out _);

    /// <param name="failure">Why it could not be read.</param>
    public static Blueprint? TryRead(ReadOnlySpan<byte> data, out string failure)
    {
        failure = string.Empty;

        // A small number carry two extra bytes before the header. Stepping over
        // them costs nothing; refusing them loses real definitions.
        if (data.Length > 5 && (data[0] & 0xF0) == 0xF0 &&
            data[2] == (byte)'B' && data[3] == (byte)'P' && data[4] == (byte)'T')
        {
            data = data[2..];
        }

        if (data.Length < 4 || data[0] != 'B' || data[1] != 'P' || data[2] != 'T')
        {
            failure = "not a blueprint";
            return null;
        }

        try
        {
            var cursor = new Cursor(data, 4);

            string name = cursor.ReadName();
            ulong parentId = cursor.ReadUInt64();

            // Two lists of references this definition draws on. Neither is
            // needed to name a field, and both are a fixed nine bytes each.
            cursor.SkipEntries();
            cursor.SkipEntries();

            int count = cursor.ReadUInt16();
            var fields = new List<BlueprintField>(count);

            for (int i = 0; i < count; i++)
            {
                ulong id = cursor.ReadUInt64();
                string fieldName = cursor.ReadName();

                char kind = (char)cursor.ReadByte();
                char shape = (char)cursor.ReadByte();

                // A field that refers to something else also names what it may
                // refer to: a definition it points at, one it contains
                // outright, a piece of content, or a curve. The four that do
                // are exactly the four whose values are references; the rest —
                // true-or-false, numbers, and text — carry no such name.
                // Missing it on any one of them throws the whole list out of
                // step, which is how each was found: the next field's name
                // arriving with a length of several thousand characters.
                if (kind is 'P' or 'R' or 'A' or 'C') cursor.ReadUInt64();

                fields.Add(new BlueprintField
                {
                    Id = id,
                    Name = fieldName,
                    ValueKind = kind,
                    Shape = shape,
                });
            }

            return new Blueprint(name, parentId, fields);
        }
        catch (Exception ex)
        {
            // A blueprint that does not read costs its own field names, not the
            // whole catalogue. Callers see fewer names, never an exception.
            failure = ex.Message;
            return null;
        }
    }

    private ref struct Cursor(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at = position;

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

        public string ReadName()
        {
            int length = ReadUInt16();
            string text = Encoding.UTF8.GetString(_data.Slice(_at, length));
            _at += length;
            return text;
        }

        /// <summary>Steps over a counted list of nine-byte references.</summary>
        /// <remarks>
        /// The count is read into a local first. Written as one expression, the
        /// position is loaded before the count advances it, and those two bytes
        /// are lost — which puts every field after this out of step.
        /// </remarks>
        public void SkipEntries()
        {
            int count = ReadUInt16();
            _at += count * 9;
        }
    }
}
