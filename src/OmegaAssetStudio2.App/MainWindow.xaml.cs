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

        // Before the first page is navigated to, because choosing a pane item
        // builds that page, and a page built under the wrong theme reads its
        // brushes under the wrong theme. Applied to the window's own content:
        // App.MainWindow is assigned from the result of this constructor and is
        // still null in here.
        Services.AppTheme.Apply(Content as FrameworkElement);

        Nav.SelectedItem = StartingItem();
    }

    /// <summary>Which tool the window opens on.</summary>
    /// <remarks>
    /// Home, unless a tool was named on the command line as
    /// <c>--page &lt;tag&gt;</c> — the same tags the pane uses. Useful for opening
    /// straight to the tool somebody is being helped with, and for looking at
    /// every page in turn without clicking through them.
    /// </remarks>
    private object StartingItem()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--page", StringComparison.OrdinalIgnoreCase)) continue;

            string wanted = args[i + 1];

            foreach (object item in Nav.MenuItems)
            {
                if (item is NavigationViewItem { Tag: string tag } && tag.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
        }

        return Nav.MenuItems[0];
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
