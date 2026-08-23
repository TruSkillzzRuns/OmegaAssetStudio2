using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Pages;

namespace OmegaAssetStudio2.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Omega Asset Studio 2";
        Nav.SelectedItem = Nav.MenuItems[0];

        // After the content exists, because that is what carries the theme.
        Services.AppTheme.Apply();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            RootFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        Type page = tag switch
        {
            "skillrecolor" => typeof(ParticleRecolorizerPage),   // the tool taken across from the first studio, whole
            "voiceswapper" => typeof(VoiceSwapperPage),
            "iconeditor" => typeof(IconEditorPage),
            "mesh" => typeof(MeshPage),
            "retarget" => typeof(RetargetPage),
            "swap" => typeof(CharacterSwapPage),
            "backup" => typeof(BackupPage),
            _ => typeof(HomePage),
        };

        RootFrame.Navigate(page);
    }
}
