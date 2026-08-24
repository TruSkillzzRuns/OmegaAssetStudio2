using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio.Calligraphy;

/// <summary>
/// The colours that can actually change what is drawn.
/// </summary>
/// <remarks>
/// Cooking strips a material's node graph out and bakes what it needs into the
/// compiled shader, which is kept in a cache of its own. A parameter the shader
/// was not built to read is a value nothing reads: editing it writes a number
/// into the package and changes nothing on screen.
/// <para>
/// Measured on a sky-strike power. Its two packages offer six vector parameters
/// to edit, and not one of their materials is handed a colour at all — what
/// their shaders are given is <c>SelectionColor</c>, which is the editor's, and
/// <c>MeshEmitterVertexColor</c>, which is the particle's own colour. Those
/// effects take their colour from the particle modules, so the six parameters
/// were offered, ticked, written, and inert.
/// </para>
/// <para>
/// Particle modules are read at runtime from the objects themselves rather than
/// baked, so they are always live and are never filtered here.
/// </para>
/// </remarks>
public static class LiveColours
{
    /// <summary>Whether a kind of colour is baked into a shader at cook time.</summary>
    private static bool IsMaterialSide(HeroSkillCatalog.SkillColorKind kind) =>
        kind is HeroSkillCatalog.SkillColorKind.MaterialExpressionVector
             or HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant3Vector
             or HeroSkillCatalog.SkillColorKind.MaterialExpressionConstant4Vector;

    /// <summary>
    /// Drops the colours whose material never receives them.
    /// </summary>
    /// <param name="cookedDir">The folder the compiled shader cache sits in.</param>
    /// <param name="entries">What the catalog offered.</param>
    /// <param name="dropped">How many were found to be read by nothing.</param>
    /// <remarks>
    /// Anything the cache cannot answer for is kept. A material whose compiled
    /// form is unknown might well use the value, and dropping it would hide a
    /// colour that works — the failure worth avoiding is the opposite of the one
    /// being fixed.
    /// </remarks>
    public static IReadOnlyList<HeroSkillCatalog.SkillColorEntry> Filter(
        string? cookedDir,
        IReadOnlyList<HeroSkillCatalog.SkillColorEntry> entries,
        out int dropped)
    {
        dropped = 0;

        if (string.IsNullOrEmpty(cookedDir) || !Directory.Exists(cookedDir)) return entries;

        var packages = new Dictionary<string, Package?>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<HeroSkillCatalog.SkillColorEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (!IsMaterialSide(entry.Kind) || string.IsNullOrEmpty(entry.SourceUpkPath))
            {
                kept.Add(entry);
                continue;
            }

            if (IsRead(cookedDir, packages, entry)) kept.Add(entry);
            else dropped++;
        }

        return kept;
    }

    /// <summary>Whether this material's compiled form is handed this parameter.</summary>
    private static bool IsRead(
        string cookedDir,
        Dictionary<string, Package?> packages,
        HeroSkillCatalog.SkillColorEntry entry)
    {
        if (!packages.TryGetValue(entry.SourceUpkPath!, out Package? package))
        {
            try { package = Package.Open(entry.SourceUpkPath!); }
            catch (Exception) { package = null; }

            packages[entry.SourceUpkPath!] = package;
        }

        if (package is null) return true;

        int material = OwningMaterial(package, entry.ExportPath);
        if (material < 0) return true;

        try { return ShaderColours.Uses(cookedDir, package, material, entry.ParameterName); }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// The material a colour expression belongs to.
    /// </summary>
    /// <remarks>
    /// An expression's path is the material's with the expression's own name on
    /// the end, so the material is that path without its last segment.
    /// </remarks>
    private static int OwningMaterial(Package package, string exportPath)
    {
        if (string.IsNullOrEmpty(exportPath)) return -1;

        int cut = exportPath.LastIndexOf('.');
        if (cut <= 0) return -1;

        string owner = exportPath[..cut];

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i).Equals("material", StringComparison.OrdinalIgnoreCase)) continue;
            if (package.GetExportPath(i).Equals(owner, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
