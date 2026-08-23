using System.Text.Json;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Presets;

// Named slot-naming presets — the user can save the slot conventions they
// work with (e.g. "PBR-standard" → diffuse/normal/roughness/metallic) and
// apply them to a material's parameter rows for consistent labeling. Each
// preset maps a SLOT NAME REGEX → friendly label + role tag.
public sealed record TexturePresetSlot(string MatchPattern, string DisplayLabel, string Role);

public sealed record TexturePreset(string Name, List<TexturePresetSlot> Slots)
{
    // Stock presets ship out of the box so users have something to start
    // from without needing to author from scratch.
    public static IReadOnlyList<TexturePreset> StockPresets { get; } = new[]
    {
        new TexturePreset("PBR Standard", new()
        {
            new("(?i)(diffuse|albedo|base|_d$|_base$)", "Diffuse",   "diffuse"),
            new("(?i)(normal|nrm|_n$)",                 "Normal",    "normal"),
            new("(?i)(rough|_r$)",                      "Roughness", "roughness"),
            new("(?i)(metal|_m$)",                      "Metallic",  "metallic"),
            new("(?i)(emiss|glow|_e$)",                 "Emissive",  "emissive"),
            new("(?i)(opacity|alpha)",                  "Opacity",   "opacity"),
        }),
        new TexturePreset("Hero Costume", new()
        {
            new("(?i)diffuse",    "Diffuse",   "diffuse"),
            new("(?i)normal",     "Normal",    "normal"),
            new("(?i)mask",       "Mask",      "mask"),
            new("(?i)specular",   "Specular",  "specular"),
            new("(?i)emissive",   "Emissive",  "emissive"),
            new("(?i)detail",     "Detail",    "detail"),
        }),
        new TexturePreset("VFX / Particle", new()
        {
            new("(?i)(noise|distort)",   "Noise",       "noise"),
            new("(?i)(ramp|gradient)",   "Color Ramp",  "ramp"),
            new("(?i)flow",              "Flow Map",    "flow"),
            new("(?i)(mask|alpha)",      "Mask",        "mask"),
        }),
    };
}

// Persistence + lookup for user-defined presets. Stock ones are merged
// in at read-time so the user always sees them even if the file is empty.
public sealed class TexturePresetLibrary
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmegaAssetStudio", "MaterialEditor", "texture_presets.json");

    public IReadOnlyList<TexturePreset> LoadAll()
    {
        var user = LoadUserPresets();
        // Stock first, then any user presets that don't shadow a stock name.
        var byName = new Dictionary<string, TexturePreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in TexturePreset.StockPresets) byName[p.Name] = p;
        foreach (var p in user) byName[p.Name] = p; // user overrides stock if same name
        return byName.Values.OrderBy(p => p.Name).ToList();
    }

    public void SaveUserPreset(TexturePreset preset)
    {
        var user = LoadUserPresets().ToList();
        user.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        user.Add(preset);
        WriteUser(user);
    }

    public void DeleteUserPreset(string name)
    {
        var user = LoadUserPresets().Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteUser(user);
    }

    // Try to match a slot name against a preset's patterns; returns the
    // first matching slot, or null if nothing in the preset fits.
    public static TexturePresetSlot? MatchSlot(TexturePreset preset, string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName)) return null;
        foreach (var slot in preset.Slots)
        {
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(slotName, slot.MatchPattern))
                    return slot;
            }
            catch { /* malformed regex — skip */ }
        }
        return null;
    }

    private List<TexturePreset> LoadUserPresets()
    {
        if (!File.Exists(s_path)) return new();
        try
        {
            var data = JsonSerializer.Deserialize<List<TexturePreset>>(File.ReadAllText(s_path));
            return data ?? new();
        }
        catch { return new(); }
    }

    private void WriteUser(List<TexturePreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
        File.WriteAllText(s_path, JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true }));
    }
}
