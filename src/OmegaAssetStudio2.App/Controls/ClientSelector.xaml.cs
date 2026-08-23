using Microsoft.UI.Xaml.Controls;
using OmegaAssetStudio2.App.Services;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.App.Controls;

/// <summary>
/// Picks which installed game client the hosting tool acts on.
/// </summary>
public sealed partial class ClientSelector : UserControl
{
    private bool _loading;

    public ClientSelector()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>Raised when the user picks a different install.</summary>
    public event EventHandler<GameClient?>? ClientChanged;

    /// <summary>The install currently selected, or null when none is configured.</summary>
    public GameClient? SelectedClient { get; private set; }

    /// <summary>Re-reads the configured installs. Call after settings change.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            AppSettings.Current.Invalidate();
            IReadOnlyList<GameClient> clients = AppSettings.Current.ResolvedClients;

            ClientBox.Items.Clear();
            foreach (GameClient client in clients)
                ClientBox.Items.Add(client.DisplayName);

            if (clients.Count == 0)
            {
                SelectedClient = null;
                FormatText.Text = "No game installed folders configured yet.";
                ClientBox.IsEnabled = false;
                return;
            }

            ClientBox.IsEnabled = true;
            GameClient? active = AppSettings.Current.ActiveClient;
            int index = active is null ? 0 : Math.Max(0, clients.ToList().FindIndex(c => c.Id == active.Id));
            ClientBox.SelectedIndex = index;
            Select(clients[index], persist: false);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ClientBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        IReadOnlyList<GameClient> clients = AppSettings.Current.ResolvedClients;
        int index = ClientBox.SelectedIndex;
        if (index < 0 || index >= clients.Count) return;

        Select(clients[index], persist: true);
    }

    private void Select(GameClient client, bool persist)
    {
        SelectedClient = client;
        FormatText.Text = client.Format.IsKnown
            ? $"package format {client.Format}"
            : "package format could not be read";

        if (persist) AppSettings.Current.SetActiveClient(client.Id);
        ClientChanged?.Invoke(this, client);
    }
}
