namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// A bounds-checked, little-endian read cursor over package bytes.
/// </summary>
/// <remarks>
/// Every field this reads comes from a file the application does not control.
/// A malformed or truncated package must produce a clear exception naming the
/// offset, never an out-of-range read or a silently wrong value.
/// </remarks>
public ref struct PackageCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PackageCursor(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        _position = position;
    }

    public readonly int Position => _position;
    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - _position;

    public void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
            throw new InvalidPackageException($"Seek to {position} is outside the package (length {_data.Length}).");
        _position = position;
    }

    public void Skip(int count) => Seek(_position + count);

    private readonly void Require(int count, string what)
    {
        if (count < 0)
            throw new InvalidPackageException($"Negative length {count} reading {what} at offset {_position}.");
        if (_position + count > _data.Length)
            throw new InvalidPackageException(
                $"Reading {what} at offset {_position} needs {count} bytes but only {Remaining} remain.");
    }

    public int ReadInt32(string what = "int32")
    {
        Require(sizeof(int), what);
        int value = BitConverter.ToInt32(_data.Slice(_position, sizeof(int)));
        _position += sizeof(int);
        return value;
    }

    public uint ReadUInt32(string what = "uint32")
    {
        Require(sizeof(uint), what);
        uint value = BitConverter.ToUInt32(_data.Slice(_position, sizeof(uint)));
        _position += sizeof(uint);
        return value;
    }

    public short ReadInt16(string what = "int16")
    {
        Require(sizeof(short), what);
        short value = BitConverter.ToInt16(_data.Slice(_position, sizeof(short)));
        _position += sizeof(short);
        return value;
    }

    public ushort ReadUInt16(string what = "uint16")
    {
        Require(sizeof(ushort), what);
        ushort value = BitConverter.ToUInt16(_data.Slice(_position, sizeof(ushort)));
        _position += sizeof(ushort);
        return value;
    }

    public float ReadSingle(string what = "float")
    {
        Require(sizeof(float), what);
        float value = BitConverter.ToSingle(_data.Slice(_position, sizeof(float)));
        _position += sizeof(float);
        return value;
    }

    /// <summary>Reads without moving, for walking a fixed-stride array.</summary>
    public readonly float PeekSingle(int at)
    {
        if (at < 0 || at + sizeof(float) > _data.Length)
            throw new InvalidPackageException($"Peek at {at} is outside the buffer.");

        return BitConverter.ToSingle(_data.Slice(at, sizeof(float)));
    }

    /// <summary>Reads without moving, for walking a fixed-stride array.</summary>
    public readonly uint PeekUInt32(int at)
    {
        if (at < 0 || at + sizeof(uint) > _data.Length)
            throw new InvalidPackageException($"Peek at {at} is outside the buffer.");

        return BitConverter.ToUInt32(_data.Slice(at, sizeof(uint)));
    }

    /// <summary>Reads one byte without moving, for packed vertex data.</summary>
    public readonly byte PeekByte(int at)
    {
        if (at < 0 || at >= _data.Length)
            throw new InvalidPackageException($"Peek at {at} is outside the buffer.");

        return _data[at];
    }

    /// <summary>Reads a half-width float without moving, for packed vertex data.</summary>
    public readonly Half PeekHalf(int at)
    {
        if (at < 0 || at + sizeof(ushort) > _data.Length)
            throw new InvalidPackageException($"Peek at {at} is outside the buffer.");

        return BitConverter.Int16BitsToHalf(BitConverter.ToInt16(_data.Slice(at, sizeof(ushort))));
    }

    public ulong ReadUInt64(string what = "uint64")
    {
        Require(sizeof(ulong), what);
        ulong value = BitConverter.ToUInt64(_data.Slice(_position, sizeof(ulong)));
        _position += sizeof(ulong);
        return value;
    }

    public Guid ReadGuid(string what = "guid")
    {
        Require(16, what);
        Guid value = new(_data.Slice(_position, 16));
        _position += 16;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count, string what = "bytes")
    {
        Require(count, what);
        ReadOnlySpan<byte> value = _data.Slice(_position, count);
        _position += count;
        return value;
    }

    /// <summary>
    /// Reads a length-prefixed string. A positive length is single-byte
    /// characters, a negative length is UTF-16 and counts characters, not bytes.
    /// Both forms include a trailing null that is stripped here.
    /// </summary>
    public string ReadString(string what = "string")
    {
        int length = ReadInt32($"{what} length");

        if (length == 0) return string.Empty;

        if (length > 0)
        {
            ReadOnlySpan<byte> raw = ReadBytes(length, what);
            // Trailing null is part of the stored length.
            if (raw.Length > 0 && raw[^1] == 0) raw = raw[..^1];
            return System.Text.Encoding.ASCII.GetString(raw);
        }

        int charCount = -length;
        ReadOnlySpan<byte> wide = ReadBytes(charCount * 2, what);
        string decoded = System.Text.Encoding.Unicode.GetString(wide);
        return decoded.Length > 0 && decoded[^1] == '\0' ? decoded[..^1] : decoded;
    }
}

/// <summary>Thrown when package bytes do not match the expected structure.</summary>
public sealed class InvalidPackageException : Exception
{
    public InvalidPackageException(string message) : base(message) { }
    public InvalidPackageException(string message, Exception inner) : base(message, inner) { }
}
