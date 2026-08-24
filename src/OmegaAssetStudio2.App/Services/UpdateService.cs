using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmegaAssetStudio2.App.Services;

/// <summary>What a check against the published releases found.</summary>
public sealed record UpdateCheck
{
    /// <summary>Whether the check reached the releases and understood them.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The version running now.</summary>
    public required string Current { get; init; }

    /// <summary>The newest version published, when one was found.</summary>
    public string Latest { get; init; } = string.Empty;

    /// <summary>Whether the published one is newer than the running one.</summary>
    public bool IsNewer { get; init; }

    /// <summary>The build to fetch, when the release carries one.</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>How big that download is, so somebody can decide before starting it.</summary>
    public long DownloadBytes { get; init; }

    /// <summary>The release page, for reading what changed.</summary>
    public string ReleaseUrl { get; init; } = string.Empty;

    /// <summary>What to tell the user, in one line.</summary>
    public required string Message { get; init; }
}

/// <summary>How far a download has got.</summary>
public sealed record DownloadProgress(long Received, long? Total)
{
    /// <summary>Zero to one, or null when the server did not say how big it is.</summary>
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

/// <summary>
/// Finds and installs a newer build.
/// </summary>
/// <remarks>
/// The build ships as a zip rather than an installer, so applying one is a file
/// copy rather than a program to run. The awkward part is that the files being
/// replaced are the ones running: Windows holds the executable open, and a
/// process cannot overwrite itself.
/// <para>
/// So the copy is done by something else. The new build is unpacked beside the
/// old one, a short script is written that waits for this process to end and
/// then copies the unpacked files over the top and starts the result, and this
/// process exits. Nothing is deleted: the copy overwrites and adds, so a file
/// somebody put in the folder themselves survives the update.
/// </para>
/// </remarks>
public static class UpdateService
{
    /// <summary>Where releases are published.</summary>
    private const string Repository = "TruSkillzzRuns/OmegaAssetStudio2";

    /// <summary>The version running now, as three numbers.</summary>
    public static string CurrentVersion()
    {
        Version? version = Assembly.GetEntryAssembly()?.GetName().Version;

        return version is null ? "0.0.0" : version.ToString(3);
    }

    /// <summary>Asks the releases what the newest published build is.</summary>
    public static async Task<UpdateCheck> CheckAsync(CancellationToken cancel = default)
    {
        string current = CurrentVersion();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Refused without one of these.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"OmegaAssetStudio2/{current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var response = await http
                .GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheck
                {
                    Succeeded = false,
                    Current = current,
                    Message = $"Could not reach the releases: {(int)response.StatusCode} {response.ReasonPhrase}.",
                };
            }

            string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            Release? release = JsonSerializer.Deserialize<Release>(body);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheck
                {
                    Succeeded = false,
                    Current = current,
                    Message = "The releases answered with nothing this understands.",
                };
            }

            string latest = release.TagName.Trim().TrimStart('v', 'V');
            ReleaseAsset? build = PickBuild(release);
            bool newer = IsNewer(latest, current);

            return new UpdateCheck
            {
                Succeeded = true,
                Current = current,
                Latest = latest,
                IsNewer = newer,
                DownloadUrl = build?.BrowserDownloadUrl ?? string.Empty,
                DownloadBytes = build?.Size ?? 0,
                ReleaseUrl = release.HtmlUrl ?? string.Empty,
                Message = newer
                    ? $"Version {latest} is available. You have {current}."
                    : $"You are on the newest version ({current}).",
            };
        }
        catch (Exception e)
        {
            return new UpdateCheck
            {
                Succeeded = false,
                Current = current,
                Message = $"The check did not finish: {e.Message}",
            };
        }
    }

    /// <summary>Fetches a build to a file of its own.</summary>
    public static async Task<string> DownloadAsync(
        string url, string version, IProgress<DownloadProgress>? progress, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentException("No build to fetch.", nameof(url));

        string safe = new(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
        string path = Path.Combine(Path.GetTempPath(), $"OmegaAssetStudio2-{safe}.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"OmegaAssetStudio2/{CurrentVersion()}");

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        {
            byte[] buffer = new byte[1 << 16];
            long received = 0;
            int since = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                received += read;

                // Reported every few megabytes rather than every block: the
                // progress goes to the interface thread, and a hundred and
                // twenty megabytes of blocks would be thousands of hops.
                if (++since < 64) continue;

                since = 0;
                progress?.Report(new DownloadProgress(received, total));
            }

            progress?.Report(new DownloadProgress(received, total));
        }

        return path;
    }

    /// <summary>
    /// Whether the folder the application runs from can be written to.
    /// </summary>
    /// <remarks>
    /// Worth knowing before a hundred megabytes are fetched: under Program
    /// Files the copy would need rights this process does not have, and the
    /// honest thing is to say so first.
    /// </remarks>
    public static bool CanWriteToInstallFolder(out string folder)
    {
        folder = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

        if (folder.Length == 0) return false;

        string probe = Path.Combine(folder, $".write-probe-{Guid.NewGuid():N}");

        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks a build and hands the copying to a script that outlives us.
    /// </summary>
    /// <remarks>
    /// Returns once the script is running. The caller must then close the
    /// application: the script is waiting for exactly that, and will not touch
    /// a file until this process is gone.
    /// </remarks>
    public static void ApplyAndRestart(string zipPath)
    {
        string folder = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty)
            ?? throw new InvalidOperationException("The application's own folder cannot be found.");

        string staging = Path.Combine(Path.GetTempPath(), $"OmegaAssetStudio2-staged-{Guid.NewGuid():N}");

        Directory.CreateDirectory(staging);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        string script = Path.Combine(Path.GetTempPath(), $"OmegaAssetStudio2-update-{Guid.NewGuid():N}.cmd");
        string exe = Environment.ProcessPath ?? Path.Combine(folder, "OmegaAssetStudio2.exe");

        // /E copies every folder, /IS overwrites files that are the same size,
        // and there is deliberately no /PURGE: a file somebody added to the
        // folder is theirs and is left alone.
        File.WriteAllText(script, $"""
            @echo off
            echo Waiting for Omega Asset Studio 2 to close...
            :wait
            tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            robocopy "{staging}" "{folder}" /E /IS /R:3 /W:1 >nul
            start "" "{exe}"
            rmdir /s /q "{staging}"
            del "%~f0"
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>The zip a release publishes, if it publishes one.</summary>
    private static ReleaseAsset? PickBuild(Release release) =>
        release.Assets?.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.BrowserDownloadUrl)
            && (a.Name ?? string.Empty).EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether one version is newer than another.
    /// </summary>
    /// <remarks>
    /// Compared as numbers rather than as text, so 2.0.10 is newer than 2.0.9
    /// rather than earlier as it would be alphabetically.
    /// </remarks>
    private static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out Version? a)
        && Version.TryParse(current, out Version? b)
        && a > b;

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public ReleaseAsset[]? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
