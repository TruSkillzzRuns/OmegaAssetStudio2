using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Variants;

// JSON file persistence for the variant database. Lives at
// %AppData%\OmegaAssetStudio\MaterialEditor\material_variants.db.json
// Idempotent: Save overwrites, Load returns an empty DB if the file is
// missing or unreadable rather than throwing.
public sealed class MaterialVariantStore
{
    private static readonly string s_defaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmegaAssetStudio", "MaterialEditor", "material_variants.db.json");

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public string PathOnDisk { get; }

    public MaterialVariantStore(string? path = null)
    {
        PathOnDisk = path ?? s_defaultPath;
    }

    public MaterialVariantDatabase Load()
    {
        if (!File.Exists(PathOnDisk)) return new();
        try
        {
            var db = JsonSerializer.Deserialize<MaterialVariantDatabase>(File.ReadAllText(PathOnDisk), s_opts);
            if (db is null) return new();
            // Reject incompatible schemas instead of silently corrupting.
            if (db.SchemaVersion != 1) return new();
            return db;
        }
        catch { return new(); }
    }

    public void Save(MaterialVariantDatabase db)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        // Stage + atomic move so a crashed write never leaves a half-file.
        string tmp = PathOnDisk + ".omtmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(db, s_opts));
        File.Move(tmp, PathOnDisk, overwrite: true);
    }

    // Quick lookup helper for the UI: "what do we know about this parent?"
    public ParentMaterialEntry? FindParent(MaterialVariantDatabase db, Guid parentId)
        => db.Parents.FirstOrDefault(p => p.ParentId == parentId);
}
