using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Swapping;

/// <summary>One costume to bring back, and the package it will be built on.</summary>
public sealed record SwapPair
{
    /// <summary>
    /// Give the costume being replaced this costume's name, so the game finds
    /// this one rather than the one it was built on.
    /// </summary>
    /// <remarks>
    /// Needed where a costume has no counterpart of its own and is built on a
    /// different costume of the same character. Off until the renaming is
    /// sound: it currently moves the name table without everything that is
    /// measured from it following.
    /// </remarks>
    public bool RenameChassisToCostume { get; init; }

    /// <summary>The costume in the newer game, as a package file name stem.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// The costume in the older game whose package is used as the body.
    /// </summary>
    /// <remarks>
    /// Called the chassis: the older game already loads it, so its tables,
    /// class references and layout are known to work there. The newer costume's
    /// parts are added to it rather than the whole newer package being handed
    /// over.
    /// </remarks>
    public required string Chassis { get; init; }

    public override string ToString() => $"{Source} on {Chassis}";
}

/// <summary>What one pair would need, and what stands in the way.</summary>
public sealed record SwapFinding
{
    public required SwapPair Pair { get; init; }

    public bool SourceExists { get; init; }
    public bool ChassisExists { get; init; }

    public int SourceVersion { get; init; }
    public int ChassisVersion { get; init; }

    public int SourceExports { get; init; }
    public int ChassisExports { get; init; }

    /// <summary>Names in the source that the chassis has never heard of.</summary>
    public int NewNames { get; init; }

    /// <summary>Objects the source borrows that the chassis does not.</summary>
    public int NewImports { get; init; }

    /// <summary>Objects the source holds that the chassis has no counterpart for.</summary>
    public int NewExports { get; init; }

    /// <summary>
    /// Classes of objects that must be carried over, which the older game has
    /// nowhere at all.
    /// </summary>
    /// <remarks>
    /// The one thing no amount of table work can fix. A class is code in the
    /// game's own program: if the older one does not have it, an object of that
    /// class cannot be loaded there, whatever the file says.
    /// <para>
    /// The character's own machinery is not counted, because it is not carried
    /// over. Beast_90s declares one object of class
    /// <c>marvelplayer_beast_90s</c> and two of
    /// <c>marveluihudbarconcarcomp</c>, none of which the older game has — and
    /// none of which are needed, since the chassis brings its own and the
    /// chassis is one the older game already loads. What is carried is what can
    /// be seen: meshes, materials, textures, the effects hung off them.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> MissingClasses { get; init; } = [];

    /// <summary>Objects that would be carried over.</summary>
    public int CarriedExports { get; init; }

    /// <summary>The character machinery left behind, which the chassis supplies.</summary>
    public int MachineryExports { get; init; }

    public string? Problem { get; init; }

    public bool Possible => SourceExists && ChassisExists && MissingClasses.Count == 0 && Problem is null;
}

/// <summary>
/// Reads a costume and its chassis and reports what a swap would involve,
/// without writing anything.
/// </summary>
/// <remarks>
/// Worth doing before any transplant: the work is in adding the source's names,
/// imports and exports to the chassis and rewriting every reference into the
/// chassis's numbering, and the size of that job — and whether it is possible
/// at all — differs per costume. A class the older game does not have is a
/// stop, not a difficulty.
/// </remarks>
public static class SwapSurvey
{
    private const string Prefix = "UC__MarvelPlayer_";
    private const string Suffix = "_SF.upk";

    /// <summary>The package a costume's name refers to, in a given install.</summary>
    public static string PathOf(GameClient client, string costume) =>
        Path.Combine(client.CookedPath, $"{Prefix}{costume}{Suffix}");

    /// <summary>Looks at one pair.</summary>
    public static SwapFinding Look(
        GameClient newer, GameClient older, SwapPair pair, IReadOnlySet<string>? classesOlderHas = null)
    {
        ArgumentNullException.ThrowIfNull(newer);
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(pair);

        string sourcePath = PathOf(newer, pair.Source);
        string chassisPath = PathOf(older, pair.Chassis);

        var finding = new SwapFinding
        {
            Pair = pair,
            SourceExists = File.Exists(sourcePath),
            ChassisExists = File.Exists(chassisPath),
        };

        if (!finding.SourceExists || !finding.ChassisExists) return finding;

        Package source, chassis;

        try
        {
            source = Package.Open(sourcePath);
            chassis = Package.Open(chassisPath);
        }
        catch (Exception ex) when (ex is InvalidPackageException or IOException)
        {
            return finding with { Problem = ex.Message };
        }

        // Names first: everything else is described in terms of them.
        var chassisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chassis.Names.Count; i++) chassisNames.Add(chassis.Names.GetName(i));

        int newNames = 0;
        for (int i = 0; i < source.Names.Count; i++)
            if (!chassisNames.Contains(source.Names.GetName(i))) newNames++;

        // Imports and exports are compared by the path they resolve to, not by
        // position: the two files number their tables differently, and a name
        // that means the same thing is the same thing.
        var chassisImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chassis.Imports.Count; i++) chassisImports.Add(chassis.GetImportPath(i));

        int newImports = 0;
        for (int i = 0; i < source.Imports.Count; i++)
            if (!chassisImports.Contains(source.GetImportPath(i))) newImports++;

        var chassisExports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chassis.Exports.Count; i++) chassisExports.Add(chassis.GetExportPath(i));

        int newExports = 0, carried = 0, machinery = 0;
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < source.Exports.Count; i++)
        {
            string path = source.GetExportPath(i);

            // The character object and everything under it stays behind. It is
            // the class machinery — what the character IS — and the chassis
            // already has a working one of those.
            if (path.Contains(".default__", StringComparison.OrdinalIgnoreCase))
            {
                machinery++;
                continue;
            }

            carried++;

            if (!chassisExports.Contains(path)) newExports++;

            string className = source.GetExportClassName(i);
            if (className.Length > 0) classes.Add(className);
        }

        var missing = classesOlderHas is null
            ? []
            : classes.Where(c => c.Length > 0 && !classesOlderHas.Contains(c))
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                     .ToList();

        return finding with
        {
            SourceVersion = source.Header.FileVersion,
            ChassisVersion = chassis.Header.FileVersion,
            SourceExports = source.Exports.Count,
            ChassisExports = chassis.Exports.Count,
            NewNames = newNames,
            NewImports = newImports,
            NewExports = newExports,
            CarriedExports = carried,
            MachineryExports = machinery,
            MissingClasses = missing,
        };
    }
}
