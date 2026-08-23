using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;

namespace OmegaAssetStudio2.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        _loading = true;
        GameFolderBox.Text = BuildClientSummary();
        VerboseToggle.IsOn = AppSettings.Current.VerboseDiagnostics;
        ThemeChoice.SelectedIndex = AppTheme.Current switch
        {
            AppThemeChoice.Dark => 1,
            AppThemeChoice.Light => 2,
            _ => 0,
        };
        _loading = false;

        VersionText.Text = "Version " + BuildVersion();
    }

    /// <summary>The version this build was stamped with.</summary>
    /// <remarks>
    /// Read from the assembly rather than written here, so it cannot drift from
    /// what actually shipped.
    /// </remarks>
    private static string BuildVersion()
    {
        System.Version? version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version;

        return version is null ? "unknown" : version.ToString(3);
    }

    private static string BuildClientSummary()
    {
        IReadOnlyList<OmegaAssetStudio2.Core.Workspace.GameClient> clients =
            AppSettings.Current.ResolvedClients;

        if (clients.Count == 0)
            return "None added yet — add one on the Home page.";

        Guid? activeId = AppSettings.Current.ActiveClient?.Id;
        return string.Join(Environment.NewLine, clients.Select(c =>
            $"{(c.Id == activeId ? "→ " : "   ")}{c.DisplayName}  ({c.Format})  {c.RootPath}"));
    }

    private void VerboseToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.VerboseDiagnostics = VerboseToggle.IsOn;
        AppSettings.Save();
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Seeding the control raises this too, and re-applying what is already
        // set would be harmless but writing the settings file would not.
        if (_loading) return;

        AppTheme.Current = ThemeChoice.SelectedIndex switch
        {
            1 => AppThemeChoice.Dark,
            2 => AppThemeChoice.Light,
            _ => AppThemeChoice.System,
        };
    }

    private void OpenLicences_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
            if (!File.Exists(path)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("SettingsPage.OpenLicences", ex);
        }
    }
}
