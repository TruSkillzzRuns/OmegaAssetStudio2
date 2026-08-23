namespace OmegaAssetStudio.WinUI;

/// <summary>
/// The diagnostics sink the copied pages write to.
/// </summary>
/// <remarks>
/// The Skill Recolor page was taken across from the first Omega Asset Studio
/// unchanged, and it logs what it finds by calling App.WriteDiagnosticsLog.
/// Rather than edit the copy, that call is answered here and forwarded to this
/// application's own log, so the copied code stays exactly as it was.
/// </remarks>
public static class App
{
    /// <summary>The window the copied toast service posts to.</summary>
    public static Microsoft.UI.Xaml.Window? MainWindow => OmegaAssetStudio2.App.App.MainWindow;

    public static void WriteDiagnosticsLog(string source, string details)
        => OmegaAssetStudio2.App.Services.CrashLog.Write(source, details);
}
