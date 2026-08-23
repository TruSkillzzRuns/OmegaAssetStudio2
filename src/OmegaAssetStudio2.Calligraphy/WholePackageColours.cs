using System.Numerics;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio.Calligraphy;

/// <summary>
/// Every particle colour a package holds, rather than only those reachable
/// from the emitters a skill binds.
/// </summary>
/// <remarks>
/// The catalog finds colours by walking a skill's bindings out to their
/// emitters and reading the modules on the way. Anything it does not reach that
/// way is never offered, never ticked and never written — and it stays the
/// colour it was. In one condition effect behind a ground-slam skill the
/// walk reached 21 of the package's 33 colour modules, and the twelve it missed
/// were exactly the ones left blue on screen after a recolour.
/// <para>
/// So the package is also read straight through: every export that is a colour
/// module, whichever emitter or detail level it belongs to. The export paths
/// are written the same way the walk writes them, so the two sets merge on
/// path and the writer's allowlist takes them unchanged.
/// </para>
/// </remarks>
public static class WholePackageColours
{
    /// <summary>Reads every colour module in one package.</summary>
    public static IReadOnlyList<HeroSkillCatalog.SkillColorEntry> In(string upkPath)
    {
        var found = new List<HeroSkillCatalog.SkillColorEntry>();

        if (string.IsNullOrEmpty(upkPath) || !File.Exists(upkPath)) return found;

        Package package;
        try { package = Package.Open(upkPath); }
        catch (Exception) { return found; }

        for (int i = 0; i < package.Exports.Count; i++)
        {
            ParticleColourModule? module;
            try { module = ParticleColourReader.TryRead(package, i); }
            catch (Exception) { continue; }

            if (module is null || module.Keys.Count == 0) continue;

            MaterialColour first = module.Keys[0].Colour;

            found.Add(new HeroSkillCatalog.SkillColorEntry(
                Kind: KindOf(module.ClassName),
                ParameterName: module.PropertyName.Length > 0 ? module.PropertyName : module.ClassName,
                OwnerLabel: Owner(package.GetExportPath(i)),
                SourceUpkPath: upkPath,
                CurrentColor: new Vector4(first.R, first.G, first.B, 1f),
                Shape: module.Keys.Count > 1
                    ? HeroSkillCatalog.DistributionShape.ConstantCurve
                    : HeroSkillCatalog.DistributionShape.Constant,
                Editable: true,
                ExportPath: package.GetExportPath(i)));
        }

        return found;
    }

    private static HeroSkillCatalog.SkillColorKind KindOf(string className)
    {
        if (className.Contains("colorscaleoverlife", StringComparison.OrdinalIgnoreCase))
            return HeroSkillCatalog.SkillColorKind.ParticleColorScaleOverLife;

        if (className.Contains("coloroverlife", StringComparison.OrdinalIgnoreCase))
            return HeroSkillCatalog.SkillColorKind.ParticleColorOverLife;

        return HeroSkillCatalog.SkillColorKind.ParticleStartColor;
    }

    /// <summary>
    /// The particle system a module belongs to, read from its own path.
    /// </summary>
    /// <remarks>
    /// A module's path is the package, the particles folder, the system, then
    /// the module — so the system is the segment before the last. Labelled the
    /// way the walk labels what it finds, so both kinds of row group together
    /// under the effect they belong to.
    /// </remarks>
    private static string Owner(string exportPath)
    {
        string[] parts = exportPath.Split('.');

        return parts.Length >= 2 ? "Particle: " + parts[^2] : exportPath;
    }
}
