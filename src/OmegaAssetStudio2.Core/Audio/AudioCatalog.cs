using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>An audio container found in a client, summarised.</summary>
public sealed record AudioPackageSummary
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required int StreamCount { get; init; }
    public required int BankCount { get; init; }

    /// <summary>Sounds held inside the banks rather than streamed.</summary>
    public required int EmbeddedCount { get; init; }

    /// <summary>Every sound in the container, however it is stored.</summary>
    public int SoundCount => StreamCount + EmbeddedCount;
    public required long TotalBytes { get; init; }

    /// <summary>
    /// The subject a container covers, taken from its file name — usually a
    /// character, sometimes a general category.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>Language suffix on the file name, when it carries one.</summary>
    public required string Language { get; init; }

    /// <summary>Which group this container belongs to.</summary>
    public required AudioCategory Category { get; init; }

    public override string ToString() => $"{Name} ({SoundCount} sounds)";
}

/// <summary>Progress while surveying a client's audio.</summary>
public sealed record AudioScanProgress(int Scanned, int Total, string Current);

/// <summary>
/// Finds and describes the audio containers in a client.
/// </summary>
public sealed class AudioCatalog
{
    /// <summary>
    /// Surveys every container beside the cooked packages.
    /// </summary>
    /// <remarks>
    /// Only headers are read, so a scan of two gigabytes of audio takes about as
    /// long as listing the directory.
    /// </remarks>
    public async Task<IReadOnlyList<AudioPackageSummary>> ScanAsync(
        GameClient client,
        IProgress<AudioScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath))
            throw new DirectoryNotFoundException($"Content folder not found: {client.CookedPath}");

        string[] files = Directory.GetFiles(client.CookedPath, "*.pck");
        var found = new List<AudioPackageSummary>();

        await Task.Run(() =>
        {
            // Asked once per scan, not once per container: it reads the whole
            // model-package listing.
            IReadOnlyDictionary<string, AudioCategory> characters = AudioCategories.NamesIn(client);

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string file = files[i];
                try
                {
                    AudioPackage package = AudioPackage.Open(file);
                    (string subject, string language) = DescribeFileName(file);

                    found.Add(new AudioPackageSummary
                    {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        StreamCount = package.Streams.Count(),
                        BankCount = package.Banks.Count(),
                        EmbeddedCount = package.Embedded.Count(),
                        TotalBytes = new FileInfo(file).Length,
                        Subject = subject,
                        Language = language,
                        Category = AudioCategories.Of(subject, characters),
                    });
                }
                catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }

                progress?.Report(new AudioScanProgress(i + 1, files.Length, Path.GetFileName(file)));
            }
        }, cancellationToken).ConfigureAwait(false);

        return found;
    }

    /// <summary>
    /// Splits a container's file name into what it covers and which language it
    /// is, using the naming the game itself uses.
    /// </summary>
    /// <remarks>
    /// Names take the shape <c>SFX_&lt;subject&gt;_&lt;language&gt;</c>. The
    /// language part is a three-letter code; anything else is treated as part of
    /// the subject, so an unexpected name degrades to "all of it is the subject"
    /// rather than losing information.
    /// </remarks>
    private static (string Subject, string Language) DescribeFileName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        string[] parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (name, string.Empty);

        string last = parts[^1];
        bool looksLikeLanguage = last.Length == 3 && last.All(char.IsLetter) && last.ToUpperInvariant() == last;

        string subject = looksLikeLanguage
            ? string.Join('_', parts[1..^1])
            : string.Join('_', parts[1..]);

        return (subject.Length == 0 ? name : subject, looksLikeLanguage ? last : string.Empty);
    }
}
