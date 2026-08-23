using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OmegaAssetStudio.WinUI.Services;

// Theme-locked brush lookups. The naive call
//   (Brush)Application.Current.Resources["OmegaAssetStudio.PanelBorderBrush"]
// silently mis-resolves on any install whose WINDOWS system theme differs
// from the in-app theme: the application-level resource lookup ignores
// page-level RequestedTheme overrides and returns the WRONG theme dict's
// brush, producing pale cards with invisible text (Light theme brush
// returned, dark XAML foreground rendered overtop). This shipped as v1.0.16's
// Skill Recolor page being unreadable for users whose Windows was set to
// Light while the app was set to Dark.
//
// The HUD-styled pages (Skill Recolor, Animation Preview, Config Editor,
// Diagnostics, Reference Graph) are designed dark-only — their other chrome
// uses hardcoded dark static brushes. Lock the runtime lookups to the Dark
// theme dictionary so they match.
public static class OmegaThemeBrushes
{
    public static Brush Dark(string key)
    {
        var themeDict = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries["Dark"];
        return (Brush)themeDict[key];
    }
}
