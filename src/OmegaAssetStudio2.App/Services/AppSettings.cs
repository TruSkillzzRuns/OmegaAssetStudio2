using System.Text.Json;
using System.Text.Json.Serialization;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.App.Services;

/// <summary>
/// A configured game install, as persisted to disk.
/// </summary>
/// <remarks>
/// Only the identity, name, and root are stored. The cooked folder and package
/// format are re-derived on load, so moving or patching an install cannot leave
/// stale values behind in the settings file.
/// </remarks>
public sealed class GameClientSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
}

/// <summary>
/// Settings for the whole application, stored as JSON in LocalAppData.
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio2", "settings.json");

    private static AppSettings? _current;
    private List<GameClient>? _resolved;

    public static AppSettings Current => _current ??= Load();

    /// <summary>Every game install the user has configured.</summary>
    public List<GameClientSetting> Clients { get; set; } = [];

    /// <summary>Which install the tools act on by default.</summary>
    public Guid? ActiveClientId { get; set; }

    /// <summary>Log every handled error, not just crashes. Off by default — it is loud.</summary>
    public bool VerboseDiagnostics { get; set; }

    /// <summary>
    /// Where the user keeps the sound decoder, when they have told us.
    /// </summary>
    /// <remarks>
    /// vgmstream is not shipped with this application, so previewing sounds
    /// needs a copy the user fetched themselves. Remembering where it is means
    /// they are asked once rather than every time.
    /// </remarks>
    public string DecoderFolder { get; set; } = string.Empty;

    /// <summary>
    /// The build costumes are taken from, which is usually one kept aside
    /// rather than one installed, so it is remembered as a folder of its own.
    /// </summary>
    public string SwapSourceFolder { get; set; } = string.Empty;

    /// <summary>
    /// A model file to stand previewed models on, chosen by the user. Remembered
    /// because it is picked once and wanted every time after.
    /// </summary>
    public string StandPath { get; set; } = string.Empty;

    /// <summary>
    /// The configured installs, resolved against disk. Entries whose cooked
    /// folder cannot be found are dropped, so callers never receive a client
    /// that cannot be read.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<GameClient> ResolvedClients => _resolved ??= ResolveClients();

    /// <summary>The install tools act on, or null when none is usable.</summary>
    [JsonIgnore]
    public GameClient? ActiveClient
    {
        get
        {
            IReadOnlyList<GameClient> clients = ResolvedClients;
            if (clients.Count == 0) return null;

            return clients.FirstOrDefault(c => c.Id == ActiveClientId) ?? clients[0];
        }
    }

    /// <summary>
    /// Adds an install. Returns null when no cooked folder can be found under
    /// <paramref name="rootPath"/>, which is the caller's cue to tell the user
    /// the folder is not a game install.
    /// </summary>
    public GameClient? AddClient(string rootPath, string displayName)
    {
        GameClient? client = GameClientLocator.FromRoot(rootPath, displayName);
        if (client is null) return null;

        // Re-pointing an existing entry is far more common than wanting two
        // entries for one folder, so match on the root and update in place.
        GameClientSetting? existing = Clients.FirstOrDefault(
            c => string.Equals(c.RootPath, client.RootPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.DisplayName = client.DisplayName;
            client = client with { Id = existing.Id };
        }
        else
        {
            Clients.Add(new GameClientSetting
            {
                Id = client.Id,
                DisplayName = client.DisplayName,
                RootPath = client.RootPath,
            });
        }

        ActiveClientId ??= client.Id;
        Invalidate();
        Save();
        return client;
    }

    public void RemoveClient(Guid id)
    {
        Clients.RemoveAll(c => c.Id == id);
        if (ActiveClientId == id) ActiveClientId = Clients.FirstOrDefault()?.Id;
        Invalidate();
        Save();
    }

    public void SetActiveClient(Guid id)
    {
        if (!Clients.Any(c => c.Id == id)) return;
        ActiveClientId = id;
        Save();
    }

    /// <summary>Forces the next read of <see cref="ResolvedClients"/> to re-scan disk.</summary>
    public void Invalidate() => _resolved = null;

    private List<GameClient> ResolveClients()
    {
        var resolved = new List<GameClient>();
        foreach (GameClientSetting setting in Clients)
        {
            GameClient? client = GameClientLocator.FromRoot(setting.RootPath, setting.DisplayName, setting.Id);
            if (client is not null) resolved.Add(client);
        }
        return resolved;
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the app from starting.
            CrashLog.Write("AppSettings.Load", ex);
        }

        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            CrashLog.Write("AppSettings.Save", ex);
        }
    }
}
