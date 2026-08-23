using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;
using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Texture;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.MaterialServices;

// Cross-package Texture2D catalogue for the Material Editor.
// Referenced from upstream MaterialResolver behavior: when a material is
// imported from a foreign UPK, the resolver opens that UPK,
// indexes every Texture2D export it contains, and registers any texture
// referenced by the material in a global ForeignTextures dictionary.
//
// Why this matters: when a Material Editor user is editing a MIC whose
// parent is in another package (e.g. chBaseMaterials.MAT_Skin), the
// available texture pool comes from THAT package. Without indexing the
// foreign package up front, slot-replacement / browse flows can't see
// any texture except the ones already explicitly bound.
//
// Discovery: scans sibling .upk files in the source-UPK's directory for
// any package name referenced by the loaded materials. Matches MHE's
// FindPackageFile sibling-scan approach.
public sealed class ForeignTextureCatalogService
{
    private readonly UpkFileRepository _repository = new();
    private readonly Dictionary<string, UnrealHeader> _upkCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ForeignTextureEntry> _foreignTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ForeignTextureEntry> _allReferencedTextures = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Log;

    /// <summary>
    /// Textures discovered in foreign UPKs (cross-package). Keyed by full
    /// object path (e.g. "chBaseMaterials.T_Skin_diff").
    /// </summary>
    public IReadOnlyDictionary<string, ForeignTextureEntry> ForeignTextures => _foreignTextures;

    /// <summary>
    /// All Texture2D exports referenced by the loaded materials, including
    /// same-package and foreign. Useful when the Material Editor needs to
    /// list every texture in scope regardless of package origin.
    /// </summary>
    public IReadOnlyDictionary<string, ForeignTextureEntry> AllReferencedTextures => _allReferencedTextures;

    public void Clear()
    {
        _upkCache.Clear();
        _foreignTextures.Clear();
        _allReferencedTextures.Clear();
    }

    /// <summary>
    /// Walk every TextureSlot on every material; resolve its source package;
    /// open foreign UPKs we haven't seen yet; index every Texture2D export
    /// they contain into the catalogue.
    /// </summary>
    public async Task PopulateAsync(IReadOnlyList<MaterialDefinition> materials, string sourceUpkPath)
    {
        if (materials is null || materials.Count == 0) return;

        string sourceDirectory = !string.IsNullOrWhiteSpace(sourceUpkPath)
            ? Path.GetDirectoryName(Path.GetFullPath(sourceUpkPath)) ?? string.Empty
            : string.Empty;
        string sourcePackageName = !string.IsNullOrWhiteSpace(sourceUpkPath)
            ? Path.GetFileNameWithoutExtension(sourceUpkPath)
            : string.Empty;

        // First pass: register all textures already known via material slots,
        // tagged by source package name (parsed from the texture path).
        foreach (MaterialDefinition material in materials)
        {
            foreach (MaterialTextureSlot slot in material.TextureSlots ?? new List<MaterialTextureSlot>())
            {
                if (string.IsNullOrWhiteSpace(slot.TexturePath) || slot.TexturePath == "<null>")
                    continue;
                RegisterReferencedTexture(slot.TexturePath, sourcePackageName, sourceUpkPath);
            }
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory)) return;

        // Second pass: for every distinct foreign package referenced by any
        // material in the set, try to open it on disk and enumerate its
        // Texture2D exports. This is the MHE behavior — index the WHOLE
        // foreign package, not just the textures we already knew about.
        HashSet<string> foreignPackages = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialDefinition material in materials)
        {
            CollectForeignPackageName(material.ParentMaterialPath, sourcePackageName, foreignPackages);
            foreach (MaterialTextureSlot slot in material.TextureSlots ?? new List<MaterialTextureSlot>())
                CollectForeignPackageName(slot.TexturePath, sourcePackageName, foreignPackages);
        }

        foreach (string packageName in foreignPackages)
        {
            string foreignUpkPath = ResolveSiblingUpk(sourceDirectory, packageName);
            if (foreignUpkPath is null) continue;
            await IndexForeignPackageAsync(foreignUpkPath, packageName).ConfigureAwait(true);
        }
    }

    private void RegisterReferencedTexture(string texturePath, string currentPackageName, string currentUpkPath)
    {
        string packageName = ParsePackageName(texturePath);
        string textureName = ParseObjectName(texturePath);
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(textureName)) return;

        bool isForeign = !string.Equals(packageName, currentPackageName, StringComparison.OrdinalIgnoreCase);
        var entry = new ForeignTextureEntry
        {
            TextureName = textureName,
            TexturePath = texturePath,
            SourcePackageName = packageName,
            SourcePackagePath = isForeign ? string.Empty : currentUpkPath,
        };
        _allReferencedTextures[texturePath] = entry;
        if (isForeign) _foreignTextures[texturePath] = entry;
    }

    private async Task IndexForeignPackageAsync(string foreignUpkPath, string packageName)
    {
        try
        {
            if (!_upkCache.TryGetValue(foreignUpkPath, out UnrealHeader header))
            {
                header = await _repository.LoadUpkFile(foreignUpkPath).ConfigureAwait(true);
                await header.ReadHeaderAsync(null).ConfigureAwait(true);
                _upkCache[foreignUpkPath] = header;
            }

            int added = 0;
            foreach (UnrealExportTableEntry export in header.ExportTable)
            {
                string cls = export.ClassReferenceNameIndex?.Name ?? string.Empty;
                if (!cls.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;

                string texturePath = export.GetPathName();
                if (string.IsNullOrWhiteSpace(texturePath)) continue;

                int sizeX = 0, sizeY = 0;
                string pixelFormat = string.Empty;
                try
                {
                    if (export.UnrealObject is null)
                        await export.ParseUnrealObject(true, true).ConfigureAwait(true);
                    if (export.UnrealObject is IUnrealObject uo && uo.UObject is UTexture2D tex)
                    {
                        sizeX = tex.SizeX;
                        sizeY = tex.SizeY;
                        pixelFormat = tex.Format.ToString();
                    }
                }
                catch { /* metadata is best-effort; entry still useful without it */ }

                var entry = new ForeignTextureEntry
                {
                    TextureName = export.ObjectNameIndex?.Name ?? string.Empty,
                    TexturePath = texturePath,
                    SourcePackagePath = foreignUpkPath,
                    SourcePackageName = packageName,
                    Width = sizeX,
                    Height = sizeY,
                    PixelFormat = pixelFormat,
                };

                _foreignTextures[texturePath] = entry;
                _allReferencedTextures[texturePath] = entry;
                added++;
            }

            Log?.Invoke($"[material-catalog] indexed {added} Texture2D export(s) from foreign UPK '{packageName}' ({Path.GetFileName(foreignUpkPath)})");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[material-catalog] failed to index foreign UPK '{packageName}' at '{foreignUpkPath}': {ex.Message}");
        }
    }

    private static void CollectForeignPackageName(string fullPath, string currentPackageName, HashSet<string> sink)
    {
        string pkg = ParsePackageName(fullPath);
        if (string.IsNullOrWhiteSpace(pkg)) return;
        if (string.Equals(pkg, currentPackageName, StringComparison.OrdinalIgnoreCase)) return;
        sink.Add(pkg);
    }

    private static string ResolveSiblingUpk(string directory, string packageName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(packageName)) return null;
        string candidate = Path.Combine(directory, packageName + ".upk");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string ParsePackageName(string fullObjectPath)
    {
        if (string.IsNullOrWhiteSpace(fullObjectPath)) return string.Empty;
        int dot = fullObjectPath.IndexOf('.');
        return dot > 0 ? fullObjectPath[..dot] : string.Empty;
    }

    private static string ParseObjectName(string fullObjectPath)
    {
        if (string.IsNullOrWhiteSpace(fullObjectPath)) return string.Empty;
        int lastDot = fullObjectPath.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < fullObjectPath.Length
            ? fullObjectPath[(lastDot + 1)..]
            : fullObjectPath;
    }
}
