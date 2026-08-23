using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OmegaAssetStudio.WinUI.Services;

/// <summary>
/// A themed brush, resolved against the theme an element is actually drawn in.
/// </summary>
/// <remarks>
/// The naive call
/// <c>(Brush)Application.Current.Resources["OmegaAssetStudio.PanelBorderBrush"]</c>
/// resolves against the Windows system theme and ignores what the element it is
/// about to be used on is drawn in. On a machine whose Windows is Light while
/// the application is set to Dark, that returns the light brush and hands it to
/// dark chrome: pale cards with invisible text. That shipped once, and it is why
/// this asks the element rather than the application.
/// </remarks>
public static class OmegaThemeBrushes
{
    /// <summary>The brush named <paramref name="key"/>, in <paramref name="element"/>'s theme.</summary>
    public static Brush For(FrameworkElement element, string key) =>
        In(element.ActualTheme, key);

    /// <summary>
    /// The brush named <paramref name="key"/>, in the theme the window is drawn
    /// in.
    /// </summary>
    /// <remarks>
    /// For the callers that have no element to hand. One window carries the
    /// whole application's theme, so asking it is the same answer any element
    /// inside it would give.
    /// </remarks>
    public static Brush For(string key) =>
        In((App.MainWindow?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Dark, key);

    private static Brush In(ElementTheme theme, string key)
    {
        string name = theme == ElementTheme.Light ? "Light" : "Dark";
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[name];

        return (Brush)dictionary[key];
    }
}
