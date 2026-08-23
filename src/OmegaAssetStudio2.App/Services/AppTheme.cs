using Microsoft.UI.Xaml;

namespace OmegaAssetStudio2.App.Services;

/// <summary>Which set of colours the application draws itself in.</summary>
public enum AppThemeChoice
{
    /// <summary>Whatever Windows is set to, and follow it when it changes.</summary>
    System,

    /// <summary>Dark regardless of Windows.</summary>
    Dark,

    /// <summary>Light regardless of Windows.</summary>
    Light,
}

/// <summary>
/// Applies the chosen theme to the window, and remembers the choice.
/// </summary>
/// <remarks>
/// The theme is set on the window's root element rather than on
/// <c>Application.RequestedTheme</c>, which can only be assigned before the
/// first window exists and so cannot be changed while the app is running. Every
/// page lives inside that root, so setting it there re-themes all of them at
/// once — and immediately, without a restart.
/// </remarks>
public static class AppTheme
{
    /// <summary>What the user chose, whether or not a window exists yet.</summary>
    public static AppThemeChoice Current
    {
        get => Parse(AppSettings.Current.Theme);
        set
        {
            AppSettings.Current.Theme = value.ToString();
            AppSettings.Save();
            Apply();
        }
    }

    /// <summary>Draws the window in the chosen theme.</summary>
    /// <remarks>
    /// Safe to call before there is a window: it does nothing, and the theme is
    /// applied when the window sets itself up.
    /// </remarks>
    public static void Apply() => Apply(App.MainWindow?.Content as FrameworkElement);

    /// <summary>Draws <paramref name="root"/> and everything inside it in the chosen theme.</summary>
    /// <remarks>
    /// The window themes itself from its own constructor, where
    /// <c>App.MainWindow</c> has not been assigned yet — it is assigned from the
    /// result of that constructor. Taking the element as an argument is what
    /// lets the theme be right on the first frame instead of after the first
    /// visit to Settings.
    /// </remarks>
    public static void Apply(FrameworkElement? root)
    {
        if (root is null) return;

        root.RequestedTheme = Current switch
        {
            AppThemeChoice.Dark => ElementTheme.Dark,
            AppThemeChoice.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>
    /// Reads a stored choice, falling back to following Windows.
    /// </summary>
    /// <remarks>
    /// Stored as a name rather than a number so a settings file stays readable
    /// and so re-ordering the enum cannot silently change what somebody chose.
    /// </remarks>
    public static AppThemeChoice Parse(string? stored) =>
        Enum.TryParse(stored, ignoreCase: true, out AppThemeChoice choice)
            ? choice
            : AppThemeChoice.System;
}
