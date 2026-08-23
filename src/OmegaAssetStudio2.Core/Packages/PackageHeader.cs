namespace OmegaAssetStudio2.Core.Packages;

/// <summary>One block of the package that is stored compressed on disk.</summary>
public readonly record struct PackageChunk(
    int UncompressedOffset,
    int UncompressedSize,
    int CompressedOffset,
    int CompressedSize);

/// <summary>How the body of a package is stored.</summary>
[Flags]
public enum PackageCompression
{
    None = 0,
    Zlib = 1,
    Lzo = 2,
    LzoEncrypted = 4,
}

/// <summary>
/// The fixed-layout header at the start of every cooked package.
/// </summary>
/// <remarks>
/// Field order was derived by decoding real packages from the installed clients
/// byte by byte, then checked against a second client whose format version
/// differs. Both read identically; only the values change. Do not reorder these
/// against a stock-engine reference — this game runs a fork, and matching the
/// generic layout is not evidence that it is correct here.
/// </remarks>
public sealed record PackageHeader
{
    /// <summary>Every cooked package starts with this.</summary>
    public const uint Magic = 0x9E2A83C1;

    public required int FileVersion { get; init; }
    public required int LicenseeVersion { get; init; }

    /// <summary>Bytes from the start of the file to the end of the header block.</summary>
    public required int TotalHeaderSize { get; init; }

    /// <summary>Observed as "None" on every package sampled; kept because it is length-prefixed and shifts every field after it.</summary>
    public required string FolderName { get; init; }

    public required uint PackageFlags { get; init; }

    public required int NameCount { get; init; }
    public required int NameOffset { get; init; }
    public required int ExportCount { get; init; }
    public required int ExportOffset { get; init; }
    public required int ImportCount { get; init; }
    public required int ImportOffset { get; init; }
    public required int DependsOffset { get; init; }

    public required int ImportExportGuidsOffset { get; init; }
    public required int ImportGuidsCount { get; init; }
    public required int ExportGuidsCount { get; init; }
    public required int ThumbnailTableOffset { get; init; }

    public required Guid PackageGuid { get; init; }

    public required int EngineVersion { get; init; }
    public required int CookerVersion { get; init; }

    /// <summary>
    /// How many generation records the header carried. Kept because the records
    /// are variable in number and shift every field after them, so anything that
    /// needs to locate a later field must know this.
    /// </summary>
    public required int GenerationCount { get; init; }

    public required PackageCompression Compression { get; init; }
    public required IReadOnlyList<PackageChunk> Chunks { get; init; }

    /// <summary>True when the body is stored compressed and must be expanded before parsing.</summary>
    public bool IsCompressed => Compression != PackageCompression.None && Chunks.Count > 0;

    public PackageFormat Format => new(FileVersion, LicenseeVersion);

    /// <summary>
    /// Reads the header from the start of a package.
    /// </summary>
    /// <exception cref="InvalidPackageException">
    /// The bytes are not a package, or are truncated part-way through the header.
    /// </exception>
    public static PackageHeader Read(ReadOnlySpan<byte> package)
    {
        var cursor = new PackageCursor(package);

        uint magic = cursor.ReadUInt32("magic");
        if (magic != Magic)
            throw new InvalidPackageException(
                $"Not a cooked package: expected magic 0x{Magic:X8} but found 0x{magic:X8}.");

        int fileVersion = cursor.ReadInt16("file version");
        int licenseeVersion = cursor.ReadInt16("licensee version");
        int totalHeaderSize = cursor.ReadInt32("total header size");
        string folderName = cursor.ReadString("folder name");

        uint packageFlags = cursor.ReadUInt32("package flags");

        int nameCount = cursor.ReadInt32("name count");
        int nameOffset = cursor.ReadInt32("name offset");
        int exportCount = cursor.ReadInt32("export count");
        int exportOffset = cursor.ReadInt32("export offset");
        int importCount = cursor.ReadInt32("import count");
        int importOffset = cursor.ReadInt32("import offset");
        int dependsOffset = cursor.ReadInt32("depends offset");

        int importExportGuidsOffset = cursor.ReadInt32("import/export guids offset");
        int importGuidsCount = cursor.ReadInt32("import guids count");
        int exportGuidsCount = cursor.ReadInt32("export guids count");
        int thumbnailTableOffset = cursor.ReadInt32("thumbnail table offset");

        Guid packageGuid = cursor.ReadGuid("package guid");

        // Generations record the table sizes at each save. Only the counts are
        // stored; nothing downstream needs them, so they are read to advance the
        // cursor and discarded.
        int generationCount = cursor.ReadInt32("generation count");
        if (generationCount < 0)
            throw new InvalidPackageException($"Negative generation count {generationCount}.");
        cursor.Skip(generationCount * sizeof(int) * 3);

        int engineVersion = cursor.ReadInt32("engine version");
        int cookerVersion = cursor.ReadInt32("cooker version");

        var compression = (PackageCompression)cursor.ReadInt32("compression flags");

        int chunkCount = cursor.ReadInt32("compressed chunk count");
        if (chunkCount < 0)
            throw new InvalidPackageException($"Negative compressed chunk count {chunkCount}.");

        // Validate against the bytes actually present BEFORE allocating. A
        // corrupt or hostile count would otherwise be an allocation of that many
        // entries, which fails as an OutOfMemoryException rather than as a clear
        // "this is not a valid package".
        const int bytesPerChunk = sizeof(int) * 4;
        if ((long)chunkCount * bytesPerChunk > cursor.Remaining)
            throw new InvalidPackageException(
                $"Compressed chunk count {chunkCount} needs {(long)chunkCount * bytesPerChunk} bytes " +
                $"but only {cursor.Remaining} remain — the header is corrupt.");

        var chunks = new PackageChunk[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = new PackageChunk(
                UncompressedOffset: cursor.ReadInt32($"chunk {i} uncompressed offset"),
                UncompressedSize: cursor.ReadInt32($"chunk {i} uncompressed size"),
                CompressedOffset: cursor.ReadInt32($"chunk {i} compressed offset"),
                CompressedSize: cursor.ReadInt32($"chunk {i} compressed size"));
        }

        return new PackageHeader
        {
            FileVersion = fileVersion,
            LicenseeVersion = licenseeVersion,
            TotalHeaderSize = totalHeaderSize,
            FolderName = folderName,
            PackageFlags = packageFlags,
            NameCount = nameCount,
            NameOffset = nameOffset,
            ExportCount = exportCount,
            ExportOffset = exportOffset,
            ImportCount = importCount,
            ImportOffset = importOffset,
            DependsOffset = dependsOffset,
            ImportExportGuidsOffset = importExportGuidsOffset,
            ImportGuidsCount = importGuidsCount,
            ExportGuidsCount = exportGuidsCount,
            ThumbnailTableOffset = thumbnailTableOffset,
            PackageGuid = packageGuid,
            EngineVersion = engineVersion,
            CookerVersion = cookerVersion,
            GenerationCount = generationCount,
            Compression = compression,
            Chunks = chunks,
        };
    }

    /// <summary>
    /// Reads just the header from a file, without loading the whole package.
    /// </summary>
    public static PackageHeader ReadFromFile(string path, int probeBytes = 64 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        int toRead = (int)Math.Min(probeBytes, stream.Length);
        byte[] buffer = new byte[toRead];
        int read = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);

        return Read(buffer.AsSpan(0, read));
    }
}
