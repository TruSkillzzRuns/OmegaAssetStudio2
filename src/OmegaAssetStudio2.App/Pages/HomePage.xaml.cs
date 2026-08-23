using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Workspace;
using Windows.Storage.Pickers;

namespace OmegaAssetStudio2.App.Pages;

/// <summary>One configured install, shaped for the list.</summary>
public sealed class ClientRow
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public required string Detail { get; init; }
    public required string ActiveLabel { get; init; }
}

public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<ClientRow> _rows = [];

    public HomePage()
    {
        InitializeComponent();
        ClientList.ItemsSource = _rows;
        Refresh();
    }

    private void Refresh()
    {
        AppSettings.Current.Invalidate();
        IReadOnlyList<GameClient> clients = AppSettings.Current.ResolvedClients;
        Guid? activeId = AppSettings.Current.ActiveClient?.Id;

        _rows.Clear();
        foreach (GameClient client in clients)
        {
            _rows.Add(new ClientRow
            {
                Id = client.Id,
                Name = client.DisplayName,
                RootPath = client.RootPath,
                Detail = BuildDetail(client),
                ActiveLabel = client.Id == activeId ? "In use" : string.Empty,
            });
        }

        int configured = AppSettings.Current.Clients.Count;
        int missing = configured - clients.Count;

        StatusText.Text = configured == 0
            ? "No game folders added yet."
            : missing > 0
                ? $"{clients.Count} of {configured} folders found. {missing} could not be read — the install may have moved."
                : $"{clients.Count} game folder(s) ready.";

        UpdateButtons();
    }

    private static string BuildDetail(GameClient client)
    {
        string format = client.Format.IsKnown
            ? $"package format {client.Format}"
            : "package format could not be read";
        string manifest = client.HasTextureCacheManifest
            ? "texture cache present"
            : "no texture cache manifest";
        return $"{format} · {manifest}";
    }

    private void UpdateButtons()
    {
        bool hasSelection = ClientList.SelectedItem is ClientRow;
        MakeActiveButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;
    }

    private void ClientList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void AddClient_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            GameClient? added = AppSettings.Current.AddClient(folder.Path, folder.Name);
            if (added is null)
            {
                StatusText.Text = $"No cooked content folder was found under '{folder.Path}'. " +
                                  "Pick the install folder that contains the engine folder.";
                return;
            }

            Refresh();
            StatusText.Text = $"Added {added.DisplayName} — package format {added.Format}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not add that folder: {ex.Message}";
            CrashLog.Write("HomePage.AddClient", ex);
        }
    }

    private void MakeActive_Click(object sender, RoutedEventArgs e)
    {
        if (ClientList.SelectedItem is not ClientRow row) return;
        AppSettings.Current.SetActiveClient(row.Id);
        Refresh();
        StatusText.Text = $"Tools will now use {row.Name}.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ClientList.SelectedItem is not ClientRow row) return;
        AppSettings.Current.RemoveClient(row.Id);
        Refresh();
        StatusText.Text = $"Removed {row.Name}. The game files themselves were not touched.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "Re-checked every configured folder.";
    }
}
