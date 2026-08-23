using System.Text.Json;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class ModNamespaceService
{
    private sealed class NamespaceSettings
    {
        public string UserTag { get; set; } = string.Empty;
    }

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio",
        "MaterialEditor");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "mod-namespace.json");

    public string GetOrCreateUserTag()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                NamespaceSettings? settings = JsonSerializer.Deserialize<NamespaceSettings>(json);
                if (!string.IsNullOrWhiteSpace(settings?.UserTag))
                    return settings.UserTag.Trim();
            }
        }
        catch
        {
            // fall through to regenerate
        }

        string tag = $"USR{Math.Abs(Environment.UserName.GetHashCode()) % 100000:D5}";
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new NamespaceSettings { UserTag = tag }));
        return tag;
    }

    public string BuildNamespaceTag(string upkPath, string assetType)
    {
        string upkName = Path.GetFileNameWithoutExtension(upkPath) ?? "UPK";
        string safeUpk = new string(upkName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).ToUpperInvariant();
        string safeType = string.Equals(assetType, "MIC", StringComparison.OrdinalIgnoreCase) ? "MIC" : "MAT";
        return $"{GetOrCreateUserTag()}_{safeType}_{safeUpk}";
    }

    public bool IsModdedPath(string path, string upkPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string matTag = BuildNamespaceTag(upkPath, "MAT");
        string micTag = BuildNamespaceTag(upkPath, "MIC");
        return path.Contains(matTag, StringComparison.OrdinalIgnoreCase) ||
               path.Contains(micTag, StringComparison.OrdinalIgnoreCase);
    }
}
