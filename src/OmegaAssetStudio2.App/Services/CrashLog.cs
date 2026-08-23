namespace OmegaAssetStudio2.App.Services;

// Crash-only logger. Writes to LocalAppData so it survives an app reinstall and
// never needs write access next to the exe.
public static class CrashLog
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio2", "crash");

    public static void Write(string source, Exception? ex)
        => Write(source, ex?.ToString() ?? "(no exception object)");

    public static void Write(string source, string details)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = Path.Combine(Directory, $"crash-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(path,
                $"=== {DateTime.UtcNow:O} — {source} ==={Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception writeFailure)
        {
            // Logging must never be the thing that takes the app down. Nothing
            // useful is left to do here, but the swallow is deliberate and named
            // rather than a bare `catch { }`.
            System.Diagnostics.Debug.WriteLine($"CrashLog write failed: {writeFailure.Message}");
        }
    }
}
