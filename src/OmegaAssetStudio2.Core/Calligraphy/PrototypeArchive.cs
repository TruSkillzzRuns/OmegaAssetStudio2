using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>One file inside the archive.</summary>
public sealed record ArchiveEntry
{
    public required ulong NameHash { get; init; }
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public required int StoredSize { get; init; }
    public required int Size { get; init; }

    public override string ToString() => $"{Name} ({Size:N0} bytes)";
}

/// <summary>Thrown when the archive's bytes do not match the expected structure.</summary>
public sealed class InvalidArchiveException : Exception
{
    public InvalidArchiveException(string message) : base(message) { }
}

/// <summary>
/// The game's data archive, holding the definitions behind everything it ships.
/// </summary>
/// <remarks>
/// This is where the names players see are recorded, which is why it is read at
/// all: a character's or a skill's definition carries the number that names it,
/// and that number is looked up in the game's display text.
/// <para>
/// <b>Read only.</b> This class opens the archive for reading, shares it with
/// other readers, and has no method that writes. The archive is the spine of
/// the game's data — a damaged one breaks every character at once, and no
/// feature here is worth that risk.
/// </para>
/// </remarks>
public sealed class PrototypeArchive : IDisposable
{
    /// <summary>'K' 'A' 'P' 'G' as stored.</summary>
    private const uint Magic = 0x4750414B;

    private const int MaxEntries = 10_000_000;
    private const int MaxNameLength = 4096;

    private readonly FileStream _file;
    private readonly Dictionary<string, ArchiveEntry> _byName;

    private readonly long _dataStart;

    private PrototypeArchive(FileStream file, IReadOnlyList<ArchiveEntry> entries, int version, long dataStart)
    {
        _file = file;
        Entries = entries;
        Version = version;
        _dataStart = dataStart;

        _byName = new Dictionary<string, ArchiveEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveEntry entry in entries) _byName.TryAdd(entry.Name, entry);
    }

    public int Version { get; }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Finds an entry by its path inside the archive.</summary>
    public ArchiveEntry? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Opens the archive that ships with a game.
    /// </summary>
    /// <returns>Null when the game has no archive where one is expected.</returns>
    public static PrototypeArchive? Open(string installRoot)
    {
        string path = Path.Combine(installRoot, "Data", "Game", "Calligraphy.sip");

        return File.Exists(path) ? OpenFile(path) : null;
    }

    /// <summary>Opens an archive by path, for reading only.</summary>
    public static PrototypeArchive OpenFile(string path)
    {
        // Opened for reading and shared as such. The game may be running, and
        // nothing here has any reason to hold a write handle.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            var header = new byte[12];
            ReadExactly(file, header, "the archive header");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic != Magic)
                throw new InvalidArchiveException($"Not a data archive: magic 0x{magic:X8}.");

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));

            if (count < 0 || count > MaxEntries)
                throw new InvalidArchiveException($"The archive declares {count} files.");

            List<ArchiveEntry> entries = ReadEntries(file, count);

            // Each file's recorded position is measured from the end of the
            // table, not from the start of the archive. Treating it as absolute
            // reads the wrong bytes, which the decompressor rejects.
            return new PrototypeArchive(file, entries, version, file.Position);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static List<ArchiveEntry> ReadEntries(FileStream file, int count)
    {
        var entries = new List<ArchiveEntry>(count);
        var fixedPart = new byte[12];
        var sizes = new byte[16];

        for (int i = 0; i < count; i++)
        {
            ReadExactly(file, fixedPart, $"file {i}");

            ulong nameHash = BinaryPrimitives.ReadUInt64LittleEndian(fixedPart);
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(fixedPart.AsSpan(8));

            if (nameLength < 0 || nameLength > MaxNameLength)
                throw new InvalidArchiveException($"File {i} declares a name of {nameLength} bytes.");

            var nameBytes = new byte[nameLength];
            ReadExactly(file, nameBytes, $"the name of file {i}");

            ReadExactly(file, sizes, $"the sizes of file {i}");

            entries.Add(new ArchiveEntry
            {
                NameHash = nameHash,
                Name = Encoding.UTF8.GetString(nameBytes),

                // The first of these four is when the file was last changed,
                // which nothing here needs.
                Offset = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(4)),
                StoredSize = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(8)),
                Size = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(12)),
            });
        }

        return entries;
    }

    /// <summary>
    /// Reads one file out of the archive and expands it.
    /// </summary>
    public byte[] Read(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Size < 0 || entry.StoredSize < 0)
            throw new InvalidArchiveException($"{entry.Name} declares a negative size.");

        long start = _dataStart + entry.Offset;

        if (entry.Offset < 0 || start + entry.StoredSize > _file.Length)
            throw new InvalidArchiveException($"{entry.Name} lies outside the archive.");

        var stored = new byte[entry.StoredSize];

        _file.Seek(start, SeekOrigin.Begin);
        ReadExactly(_file, stored, entry.Name);

        // A file the same size stored as it is was not worth compressing.
        if (entry.StoredSize == entry.Size) return stored;

        var expanded = new byte[entry.Size];
        int written = LZ4Codec.Decode(stored, 0, stored.Length, expanded, 0, expanded.Length);

        if (written != entry.Size)
            throw new InvalidArchiveException($"{entry.Name} expanded to {written} bytes, not {entry.Size}.");

        return expanded;
    }

    /// <summary>Reads a file by name, or null when the archive has no such file.</summary>
    public byte[]? Read(string name)
    {
        ArchiveEntry? entry = Find(name);
        return entry is null ? null : Read(entry);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, string what)
    {
        int read = 0;

        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);

            if (got <= 0)
                throw new InvalidArchiveException($"The archive ends part-way through {what}.");

            read += got;
        }
    }

    public void Dispose() => _file.Dispose();
}
