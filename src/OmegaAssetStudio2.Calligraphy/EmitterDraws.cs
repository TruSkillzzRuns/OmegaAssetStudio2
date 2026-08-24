using System.Buffers.Binary;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio.Calligraphy;

/// <summary>What one emitter draws, and how much of it.</summary>
public sealed record EmitterDrawing
{
    /// <summary>The material the emitter's sprites are drawn with.</summary>
    public required string Material { get; init; }

    /// <summary>How many particles it has at its busiest, as the package records.</summary>
    public required int PeakParticles { get; init; }
}

/// <summary>
/// The material behind each particle colour.
/// </summary>
/// <remarks>
/// A colour on its own says a number, not a thing. The same red is a puff of
/// smoke on one emitter and a lightning bolt on the next, and which it is
/// decides whether recolouring it is what somebody wanted.
/// <para>
/// The package says so plainly. Every emitter's detail level names the module
/// that carries its material, and lists the modules that belong to it — so a
/// colour module can be traced to the material its emitter draws with, without
/// leaving the file.
/// </para>
/// </remarks>
public static class EmitterDraws
{
    /// <summary>
    /// Maps every colour module in a package to what its emitter draws.
    /// </summary>
    /// <remarks>
    /// Keyed by the module's own export path, which is what a colour entry
    /// already carries, so the two line up without a second lookup.
    /// </remarks>
    public static IReadOnlyDictionary<string, EmitterDrawing> In(string upkPath)
    {
        var byModule = new Dictionary<string, EmitterDrawing>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(upkPath) || !File.Exists(upkPath)) return byModule;

        Package package;
        try { package = Package.Open(upkPath); }
        catch (Exception) { return byModule; }

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i).Equals("particlelodlevel", StringComparison.OrdinalIgnoreCase))
                continue;

            PropertyBag? level;
            try { level = package.TryReadProperties(i); }
            catch (Exception) { continue; }

            if (level is null) continue;

            var drawing = new EmitterDrawing
            {
                Material = MaterialOf(package, level),
                PeakParticles = level.GetInt("PeakActiveParticles"),
            };

            if (drawing.Material.Length == 0) continue;

            foreach (int module in ModulesOf(level))
            {
                if (module < 0 || module >= package.Exports.Count) continue;

                string path;
                try { path = package.GetExportPath(module); }
                catch (Exception) { continue; }

                byModule[path] = drawing;
            }
        }

        return byModule;
    }

    /// <summary>The material named by a detail level's required module.</summary>
    private static string MaterialOf(Package package, PropertyBag level)
    {
        ObjectReference required = level.GetObject("RequiredModule");
        if (!required.IsExport) return string.Empty;

        PropertyBag? module;
        try { module = package.TryReadProperties(required.ExportIndex); }
        catch (Exception) { return string.Empty; }

        if (module is null) return string.Empty;

        ObjectReference material = module.GetObject("Material");

        try
        {
            string path = material.IsImport ? package.GetImportPath(material.ImportIndex)
                        : material.IsExport ? package.GetExportPath(material.ExportIndex)
                        : string.Empty;

            int cut = path.LastIndexOf('.');
            return cut >= 0 ? path[(cut + 1)..] : path;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The modules a detail level owns.
    /// </summary>
    /// <remarks>
    /// An array of object references is a count followed by that many of them,
    /// each one an index that is positive for an export and negative for an
    /// import. Only exports are wanted here: a module lives in the package that
    /// uses it.
    /// </remarks>
    private static List<int> ModulesOf(PropertyBag level)
    {
        var owned = new List<int>();

        PropertyTag? modules = level.Find("Modules");
        if (modules is null) return owned;

        ReadOnlySpan<byte> value = modules.Value.Span;
        if (value.Length < 4) return owned;

        int count = BinaryPrimitives.ReadInt32LittleEndian(value);
        if (count <= 0 || value.Length < 4 + (count * 4)) return owned;

        for (int i = 0; i < count; i++)
        {
            int reference = BinaryPrimitives.ReadInt32LittleEndian(value[(4 + (i * 4))..]);
            if (reference > 0) owned.Add(reference - 1);
        }

        return owned;
    }
}
