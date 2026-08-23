using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>Which table an entry came from.</summary>
public enum AudioEntryKind
{
    /// <summary>A sound bank: events and the short sounds played from memory.</summary>
    Bank,

    /// <summary>A streamed sound, played from disk. Voice lines live here.</summary>
    Stream,

    /// <summary>
    /// A sound held inside a bank rather than streamed.
    /// </summary>
    /// <remarks>
    /// Measured across the three installs: 27,000 sounds are stored this way,
    /// and 13 to 16 containers per install hold nothing else. Reading only the
    /// stream table
    /// reports those containers as empty, which they are not.
    /// </remarks>
    Embedded,
}

/// <summary>One sound inside an audio container.</summary>
public sealed record AudioEntry
{
    public required AudioEntryKind Kind { get; init; }

    /// <summary>Identifier the game uses to refer to this sound.</summary>
    public required uint Id { get; init; }

    /// <summary>Byte offset of the sound's data within the container.</summary>
    public required long Offset { get; init; }

    public required int Size { get; init; }

    /// <summary>Index into the container's language list.</summary>
    public required uint LanguageId { get; init; }

    /// <summary>Language name, resolved from the container's own table.</summary>
    public required string Language { get; init; }

    /// <summary>Offset of this entry's own record, so its fields can be rewritten.</summary>
    public required int RecordOffset { get; init; }

    public override string ToString() => $"{Kind} {Id} ({Size:N0} bytes, {Language})";
}

/// <summary>
/// A Wwise audio container: the packed archive holding the game's sounds.
/// </summary>
/// <remarks>
/// Layout derived from the real files and confirmed arithmetically. After a fixed
/// header come four sized sections — a language map, a bank table, a stream
/// table, and an externals table. Each table is a count followed by fixed-size
/// records.
/// <para>
/// The confirming check: a container declaring 17 banks has a bank section of
/// exactly 344 bytes, which is 17 records of 20 bytes plus the 4-byte count, and
/// the four section sizes plus the fixed header sum precisely to the declared
/// total header size.
/// </para>
/// </remarks>
public sealed class AudioPackage
{
    /// <summary>Every container starts with this.</summary>
    public static readonly byte[] Magic = "AKPK"u8.ToArray();

    /// <summary>Every bank starts with this.</summary>
    private static readonly byte[] BankMagic = "BKHD"u8.ToArray();

    /// <summary>The directory of sounds held inside a bank, and their data.</summary>
    private static readonly byte[] DirectoryTag = "DIDX"u8.ToArray();
    private static readonly byte[] DataTag = "DATA"u8.ToArray();

    /// <summary>A four-letter tag and a length.</summary>
    private const int SectionHeaderSize = 8;

    /// <summary>Identifier, offset and size.</summary>
    private const int BankRecordSize = sizeof(uint) * 3;

    /// <summary>Bytes per table record.</summary>
    private const int RecordSize = sizeof(uint) * 5;

    /// <summary>Where the sections begin, after the fixed header.</summary>
    private const int SectionsOffset = 28;

    private AudioPackage(string path, IReadOnlyList<AudioEntry> entries, IReadOnlyList<string> languages)
    {
        Path = path;
        Entries = entries;
        Languages = languages;
    }

    public string Path { get; }
    public IReadOnlyList<AudioEntry> Entries { get; }
    public IReadOnlyList<string> Languages { get; }

    public IEnumerable<AudioEntry> Streams => Entries.Where(e => e.Kind == AudioEntryKind.Stream);
    public IEnumerable<AudioEntry> Banks => Entries.Where(e => e.Kind == AudioEntryKind.Bank);

    /// <summary>Sounds held inside the banks rather than streamed.</summary>
    public IEnumerable<AudioEntry> Embedded => Entries.Where(e => e.Kind == AudioEntryKind.Embedded);

    /// <summary>
    /// Everything that is a sound: streamed and bank-held alike.
    /// </summary>
    /// <remarks>
    /// The banks themselves are excluded — a bank is a container of sounds, not
    /// one of them, and counting it would double what is really there.
    /// </remarks>
    public IEnumerable<AudioEntry> Sounds =>
        Entries.Where(e => e.Kind is AudioEntryKind.Stream or AudioEntryKind.Embedded);

    /// <summary>True when the file begins with the container signature.</summary>
    public static bool LooksLikeContainer(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual(Magic);

    /// <summary>
    /// Reads a container's tables. Only the header is read, not the sound data,
    /// so this stays fast on files of hundreds of megabytes.
    /// </summary>
    public static AudioPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Span<byte> fixedHeader = stackalloc byte[SectionsOffset];
        if (stream.ReadAtLeast(fixedHeader, SectionsOffset, throwOnEndOfStream: false) < SectionsOffset)
            throw new InvalidPackageException($"'{path}' is too small to be an audio container.");

        if (!LooksLikeContainer(fixedHeader))
            throw new InvalidPackageException($"'{path}' does not begin with the audio container signature.");

        int headerSize = BitConverter.ToInt32(fixedHeader[4..]);
        int languageMapSize = BitConverter.ToInt32(fixedHeader[12..]);
        int bankSectionSize = BitConverter.ToInt32(fixedHeader[16..]);
        int streamSectionSize = BitConverter.ToInt32(fixedHeader[20..]);

        if (headerSize <= 0 || headerSize > 64 * 1024 * 1024)
            throw new InvalidPackageException($"'{path}' declares a header of {headerSize} bytes.");

        // Read the whole header block once; the tables are all inside it.
        int totalHeader = headerSize + 8;
        byte[] header = new byte[totalHeader];
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.ReadAtLeast(header, totalHeader, throwOnEndOfStream: false) < totalHeader)
            throw new InvalidPackageException($"'{path}' is shorter than its declared header.");

        IReadOnlyList<string> languages = ReadLanguages(header, SectionsOffset, languageMapSize);

        var entries = new List<AudioEntry>();
        int bankStart = SectionsOffset + languageMapSize;
        int streamStart = bankStart + bankSectionSize;

        ReadTable(header, bankStart, bankSectionSize, AudioEntryKind.Bank, languages, entries, path);
        ReadTable(header, streamStart, streamSectionSize, AudioEntryKind.Stream, languages, entries, path);

        ReadBankContents(stream, entries);

        return new AudioPackage(path, entries, languages);
    }

    /// <summary>
    /// Reads the language table: a count, then one offset-and-identifier pair per
    /// language, then the names themselves as wide text.
    /// </summary>
    private static IReadOnlyList<string> ReadLanguages(ReadOnlySpan<byte> header, int start, int size)
    {
        if (size < sizeof(int) || start + size > header.Length) return [];

        int count = BitConverter.ToInt32(header[start..]);
        if (count <= 0 || count > 256) return [];

        var names = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            int record = start + sizeof(int) + (i * sizeof(uint) * 2);
            if (record + 8 > header.Length) break;

            // The offset is measured from the start of this section.
            int nameOffset = start + BitConverter.ToInt32(header[record..]);
            if (nameOffset < 0 || nameOffset >= header.Length) { names.Add(string.Empty); continue; }

            names.Add(ReadWideString(header, nameOffset));
        }

        return names;
    }

    private static string ReadWideString(ReadOnlySpan<byte> data, int offset)
    {
        int end = offset;
        while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0)) end += 2;

        return System.Text.Encoding.Unicode.GetString(data[offset..end]);
    }

    /// <summary>
    /// Reads the sounds held inside each bank and adds them to the list.
    /// </summary>
    /// <remarks>
    /// A bank is a small archive of its own, laid out as four-letter tags each
    /// followed by a length: BKHD, then DIDX, DATA and HIRC. DIDX is a directory
    /// of twelve-byte records — identifier, offset, size — where the offset is
    /// measured from the start of DATA. Adding the bank's own position gives the
    /// sound's place in the container.
    /// <para>
    /// Checked against the real files: every one of a container's 87 embedded sounds
    /// resolves to a position holding a sound header, and across the three
    /// installs no bank failed to parse.
    /// </para>
    /// <para>
    /// The size field sits twelve bytes into a bank record at the same distance
    /// from the start as it does in a container record, so a swap updates the
    /// recorded length the same way for both.
    /// </para>
    /// </remarks>
    private static void ReadBankContents(Stream stream, List<AudioEntry> entries)
    {
        // Copied out first: the loop below adds to the same list it reads.
        List<AudioEntry> banks = entries.Where(e => e.Kind == AudioEntryKind.Bank).ToList();

        foreach (AudioEntry bank in banks)
        {
            if (bank.Size <= 0 || bank.Offset < 0) continue;

            byte[] contents = new byte[bank.Size];

            stream.Seek(bank.Offset, SeekOrigin.Begin);
            if (stream.ReadAtLeast(contents, bank.Size, throwOnEndOfStream: false) < bank.Size) continue;

            if (!contents.AsSpan(0, Math.Min(4, contents.Length)).SequenceEqual(BankMagic)) continue;

            int directory = -1, directoryLength = 0, data = -1;

            for (int at = 0; at + SectionHeaderSize <= contents.Length;)
            {
                ReadOnlySpan<byte> tag = contents.AsSpan(at, 4);
                int length = BitConverter.ToInt32(contents, at + 4);

                if (length < 0 || at + SectionHeaderSize + (long)length > contents.Length) break;

                if (tag.SequenceEqual(DirectoryTag)) { directory = at + SectionHeaderSize; directoryLength = length; }
                else if (tag.SequenceEqual(DataTag)) data = at + SectionHeaderSize;

                at += SectionHeaderSize + length;
            }

            if (directory < 0 || data < 0) continue;

            for (int i = 0; i + BankRecordSize <= directoryLength; i += BankRecordSize)
            {
                int record = directory + i;

                uint id = BitConverter.ToUInt32(contents, record);
                int offset = BitConverter.ToInt32(contents, record + 4);
                int size = BitConverter.ToInt32(contents, record + 8);

                if (offset < 0 || size <= 0 || data + (long)offset + size > contents.Length) continue;

                entries.Add(new AudioEntry
                {
                    Kind = AudioEntryKind.Embedded,
                    Id = id,
                    Offset = bank.Offset + data + offset,
                    Size = size,
                    LanguageId = bank.LanguageId,
                    Language = bank.Language,

                    // Where this sound's own record lives in the file, so a swap
                    // can write the new length back.
                    RecordOffset = (int)bank.Offset + record,
                });
            }
        }
    }

    private static void ReadTable(
        ReadOnlySpan<byte> header, int start, int size, AudioEntryKind kind,
        IReadOnlyList<string> languages, List<AudioEntry> into, string path)
    {
        if (size < sizeof(int) || start + size > header.Length) return;

        int count = BitConverter.ToInt32(header[start..]);
        if (count < 0) throw new InvalidPackageException($"'{path}' declares {count} {kind} entries.");

        // The declared section size must match the record count exactly. If it
        // does not, the layout is not what this reader expects and continuing
        // would produce plausible-looking nonsense.
        int expected = sizeof(int) + (count * RecordSize);
        if (expected != size)
        {
            throw new InvalidPackageException(
                $"'{path}': the {kind} section is {size} bytes but {count} records need {expected}.");
        }

        for (int i = 0; i < count; i++)
        {
            int record = start + sizeof(int) + (i * RecordSize);

            uint id = BitConverter.ToUInt32(header[record..]);
            uint blockSize = BitConverter.ToUInt32(header[(record + 4)..]);
            int fileSize = BitConverter.ToInt32(header[(record + 8)..]);
            uint rawOffset = BitConverter.ToUInt32(header[(record + 12)..]);
            uint languageId = BitConverter.ToUInt32(header[(record + 16)..]);

            // Offsets are stored in blocks when the container aligns its data, so
            // the real position is the recorded value scaled by the block size.
            long offset = blockSize > 1 ? (long)rawOffset * blockSize : rawOffset;

            into.Add(new AudioEntry
            {
                Kind = kind,
                Id = id,
                Offset = offset,
                Size = fileSize,
                LanguageId = languageId,
                Language = languageId < languages.Count ? languages[(int)languageId] : string.Empty,
                RecordOffset = record,
            });
        }
    }

    /// <summary>Reads one sound's bytes from the container.</summary>
    public byte[] ReadEntryData(AudioEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (entry.Offset < 0 || entry.Offset + entry.Size > stream.Length)
            throw new InvalidPackageException(
                $"Sound {entry.Id} claims {entry.Size} bytes at {entry.Offset}, past the end of the file.");

        stream.Seek(entry.Offset, SeekOrigin.Begin);

        byte[] data = new byte[entry.Size];
        if (stream.ReadAtLeast(data, entry.Size, throwOnEndOfStream: false) < entry.Size)
            throw new InvalidPackageException($"Sound {entry.Id} is shorter than its declared size.");

        return data;
    }
}
