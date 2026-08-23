using System.Text.Json;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Safety;

public sealed class MaterialIndexSwapService
{
    private sealed class SwapEntry
    {
        public string UpkPath { get; set; } = string.Empty;
        public string NativeMaterialPath { get; set; } = string.Empty;
        public string ModdedMaterialPath { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public bool IsReverted { get; set; }
        public string BackupPath { get; set; } = string.Empty;
    }

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio",
        "MaterialEditor");

    private static readonly string SwapLogPath = Path.Combine(SettingsDirectory, "index-swap-log.json");

    public void RecordSwap(string upkPath, string nativeMaterialPath, string moddedMaterialPath, string backupPath)
    {
        List<SwapEntry> entries = LoadEntries();
        entries.Add(new SwapEntry
        {
            UpkPath = upkPath,
            NativeMaterialPath = nativeMaterialPath,
            ModdedMaterialPath = moddedMaterialPath,
            TimestampUtc = DateTime.UtcNow,
            IsReverted = false,
            BackupPath = backupPath
        });
        SaveEntries(entries);
    }

    public string? RevertLast(string upkPath)
    {
        List<SwapEntry> entries = LoadEntries();
        SwapEntry? last = entries.LastOrDefault(entry =>
            string.Equals(entry.UpkPath, upkPath, StringComparison.OrdinalIgnoreCase) &&
            !entry.IsReverted);
        if (last is null)
            return null;

        last.IsReverted = true;
        SaveEntries(entries);
        return last.BackupPath;
    }

    private static List<SwapEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(SwapLogPath))
                return [];

            string json = File.ReadAllText(SwapLogPath);
            return JsonSerializer.Deserialize<List<SwapEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveEntries(List<SwapEntry> entries)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SwapLogPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
