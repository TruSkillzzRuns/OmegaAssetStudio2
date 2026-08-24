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
        UpdateStatusText.Text = $"You have version {BuildVersion()}.";
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

    /// <summary>What the last check found, so the install button knows what to fetch.</summary>
    private UpdateCheck? _update;

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        ReleaseNotesButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Looking for a newer version...";

        try
        {
            _update = await UpdateService.CheckAsync();
            UpdateStatusText.Text = _update.Message;

            if (_update.ReleaseUrl.Length > 0) ReleaseNotesButton.Visibility = Visibility.Visible;
            if (!_update.IsNewer) return;

            if (_update.DownloadUrl.Length == 0)
            {
                UpdateStatusText.Text = _update.Message + " That release publishes no build to install, so it has to be fetched by hand.";
                return;
            }

            // Said before anything is fetched rather than after: under Program
            // Files the copy needs rights this process does not have, and a
            // hundred and twenty megabytes is a long way to go to find out.
            if (!UpdateService.CanWriteToInstallFolder(out string folder))
            {
                UpdateStatusText.Text = _update.Message
                    + $" This copy sits in {folder}, which it cannot write to — move it somewhere it can, or update by hand.";
                return;
            }

            InstallUpdateButton.Content = _update.DownloadBytes > 0
                ? $"Download and install ({_update.DownloadBytes / 1024d / 1024d:N0} MB)"
                : "Download and install";

            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "The check did not finish: " + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null || _update.DownloadUrl.Length == 0) return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                UpdateProgress.IsIndeterminate = p.Fraction is null;
                if (p.Fraction is double f) UpdateProgress.Value = f;

                UpdateStatusText.Text = $"Downloading version {_update.Latest}... {p.Received / 1024d / 1024d:N0} MB";
            });

            string zip = await UpdateService.DownloadAsync(_update.DownloadUrl, _update.Latest, progress);

            UpdateStatusText.Text = "Unpacking. The application will close and reopen on the new version.";

            UpdateService.ApplyAndRestart(zip);

            // The script is waiting for this process to end before it touches a
            // file, so closing is the last step of installing.
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "The update did not install: " + ex.Message;
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null || _update.ReleaseUrl.Length == 0) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _update.ReleaseUrl,
            UseShellExecute = true,
        });
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
