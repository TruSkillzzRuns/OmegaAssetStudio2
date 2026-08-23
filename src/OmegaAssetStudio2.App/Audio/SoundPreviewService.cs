using System.Diagnostics;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Audio;

namespace OmegaAssetStudio2.App.Audio;

/// <summary>
/// Decodes a game sound to a playable file.
/// </summary>
/// <remarks>
/// The game's sounds are in a codec Windows cannot open directly, so decoding is
/// delegated to vgmstream, which is invoked as a separate process and writes a
/// plain wave file to a temporary folder.
/// <para>
/// vgmstream is not shipped with this application. It carries codec libraries
/// under several licences — one of them from a standards body, with terms that
/// are not a recognised open-source licence — and passing all of that on is an
/// obligation this project has no need to take. The user fetches it once and
/// points at it, or drops it beside the executable, and it is theirs under its
/// own terms rather than redistributed under ours.
/// </para>
/// <para>
/// Temporary files are pooled by sound identifier: previewing the same line twice
/// decodes it once. Everything is written under the user's temp directory and
/// never beside the game.
/// </para>
/// </remarks>
public sealed class SoundPreviewService
{
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public SoundPreviewService()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "OmegaAssetStudio2", "audio");
        Directory.CreateDirectory(_workingDirectory);
    }

    /// <summary>What the decoder's own program is called.</summary>
    public const string DecoderExecutable = "vgmstream-cli.exe";

    /// <summary>Where to get it.</summary>
    public const string DecoderHomePage = "https://vgmstream.org/";

    /// <summary>
    /// The decoder, wherever the user keeps it, or null when it cannot be found.
    /// </summary>
    /// <remarks>
    /// Looked for in the places someone would plausibly put it, in the order
    /// they would expect to win: a folder they chose in the settings, then
    /// beside this application, then anywhere on the system path.
    /// </remarks>
    public static string? DecoderPath
    {
        get
        {
            foreach (string folder in Places())
            {
                if (folder.Length == 0) continue;

                string direct = Path.Combine(folder, DecoderExecutable);
                if (File.Exists(direct)) return direct;

                // Someone who unzips the release gets a folder with the program
                // inside it, so a parent folder is worth looking into.
                string nested = Path.Combine(folder, "vgmstream", DecoderExecutable);
                if (File.Exists(nested)) return nested;
            }

            return null;
        }
    }

    private static IEnumerable<string> Places()
    {
        yield return AppSettings.Current.DecoderFolder;
        yield return AppContext.BaseDirectory;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) yield break;

        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return folder.Trim();
        }
    }

    public static bool IsDecoderAvailable => DecoderPath is not null;

    /// <summary>
    /// Whether a folder holds the decoder, for checking one the user picked
    /// before it is kept.
    /// </summary>
    public static bool HoldsDecoder(string folder) =>
        folder.Length > 0 &&
        (File.Exists(Path.Combine(folder, DecoderExecutable)) ||
         File.Exists(Path.Combine(folder, "vgmstream", DecoderExecutable)));

    /// <summary>
    /// Extracts a sound and decodes it to a wave file.
    /// </summary>
    /// <returns>Path of the decoded file, or null when it could not be decoded.</returns>
    public async Task<string?> TryDecodeAsync(
        AudioPackage package, AudioEntry entry, CancellationToken cancellationToken = default)
    {
        string key = $"{Path.GetFileNameWithoutExtension(package.Path)}-{entry.Id}";

        if (_decoded.TryGetValue(key, out string? existing) && File.Exists(existing))
            return existing;

        string? decoder = DecoderPath;
        if (decoder is null) return null;

        string sourcePath = Path.Combine(_workingDirectory, key + ".wem");
        string wavePath = Path.Combine(_workingDirectory, key + ".wav");

        try
        {
            byte[] data = await Task.Run(() => package.ReadEntryData(entry), cancellationToken)
                                    .ConfigureAwait(false);
            await File.WriteAllBytesAsync(sourcePath, data, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = decoder,
                // -o names the output; the decoder is otherwise silent on success.
                ArgumentList = { "-o", wavePath, sourcePath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _workingDirectory,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(wavePath)) return null;

            _decoded[key] = wavePath;
            return wavePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            // The extracted source is only an input to the decoder.
            try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Extracts a sound to a file the user chose, without decoding it. This is the
    /// form the game itself uses, and the form a replacement must be in.
    /// </summary>
    public static async Task ExportAsync(
        AudioPackage package, AudioEntry entry, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        byte[] data = await Task.Run(() => package.ReadEntryData(entry), cancellationToken)
                                .ConfigureAwait(false);
        await File.WriteAllBytesAsync(destinationPath, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes everything this service decoded.</summary>
    public void Clear()
    {
        foreach (string path in _decoded.Values)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
        _decoded.Clear();
    }
}
