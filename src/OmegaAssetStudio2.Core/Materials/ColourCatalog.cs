using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>Where a colour lives, which decides how it is written back.</summary>
public enum ColourSourceKind
{
    /// <summary>A parameter overridden on a material instance.</summary>
    Material,

    /// <summary>A key in a particle effect's colour curve.</summary>
    ParticleEffect,
}

/// <summary>One editable colour, wherever it came from.</summary>
public sealed record ColourEntry
{
    public required string Name { get; init; }
    public required MaterialColour Colour { get; init; }
    public required int ValueOffset { get; init; }
}

/// <summary>An object holding colours that can be edited together.</summary>
public sealed record ColourTarget
{
    public required ColourSourceKind Kind { get; init; }
    public required string PackagePath { get; init; }
    public required int ExportIndex { get; init; }
    public required string Name { get; init; }
    public required string ObjectPath { get; init; }

    /// <summary>What kind of thing this is, for display.</summary>
    public required string Description { get; init; }

    public required IReadOnlyList<ColourEntry> Colours { get; init; }

    public override string ToString() => $"{Name} ({Colours.Count} colours)";
}

/// <summary>Progress while searching for colours.</summary>
public sealed record ColourScanProgress(int PackagesScanned, int PackageCount, int ColoursFound, string CurrentPackage);

/// <summary>
/// Finds every editable colour in a client, from both places colour is stored.
/// </summary>
/// <remarks>
/// Material instances hold overridden parameters; particle modules hold curves.
/// A tool that looked only at materials would miss most effect colour, which is
/// what the particle modules carry.
/// </remarks>
public sealed class ColourCatalog
{
    public async Task<IReadOnlyList<ColourTarget>> ScanAsync(
        GameClient client,
        string fileFilter = "*.upk",
        IProgress<ColourScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath))
            throw new DirectoryNotFoundException($"Content folder not found: {client.CookedPath}");

        return await ScanFilesAsync(
            Directory.GetFiles(client.CookedPath, fileFilter), progress, onError, cancellationToken);
    }

    /// <summary>
    /// Searches an exact set of packages rather than a whole folder.
    /// </summary>
    /// <remarks>
    /// What a skill looks like is spread across several packages that share no
    /// single name pattern, so the caller works out which ones matter and hands
    /// them over. Searching those few takes a moment instead of minutes.
    /// </remarks>
    public async Task<IReadOnlyList<ColourTarget>> ScanFilesAsync(
        IReadOnlyList<string> packagePaths,
        IProgress<ColourScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);

        string[] files = [.. packagePaths];
        var found = new List<ColourTarget>();

        await Task.Run(() =>
        {
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string file = files[i];
                try
                {
                    Package package = Package.Open(file);

                    foreach (int index in package.FindExportsOfClass(MaterialParameterReader.MaterialInstanceClass))
                    {
                        MaterialInstance? material = MaterialParameterReader.TryRead(package, index);
                        if (material is null || material.Colours.Count == 0) continue;

                        found.Add(new ColourTarget
                        {
                            Kind = ColourSourceKind.Material,
                            PackagePath = material.PackagePath,
                            ExportIndex = material.ExportIndex,
                            Name = material.Name,
                            ObjectPath = material.ObjectPath,
                            Description = "material",
                            Colours = material.Colours
                                .Select(c => new ColourEntry
                                {
                                    Name = c.Name,
                                    Colour = c.Colour,
                                    ValueOffset = c.ValueOffset,
                                })
                                .ToList(),
                        });
                    }

                    for (int index = 0; index < package.Exports.Count; index++)
                    {
                        ParticleColourModule? module = ParticleColourReader.TryRead(package, index);
                        if (module is not { HasColours: true }) continue;

                        found.Add(new ColourTarget
                        {
                            Kind = ColourSourceKind.ParticleEffect,
                            PackagePath = module.PackagePath,
                            ExportIndex = module.ExportIndex,
                            Name = module.Name,
                            ObjectPath = module.ObjectPath,
                            Description = DescribeModule(module.ClassName),
                            Colours = module.Keys
                                .Select(k => new ColourEntry
                                {
                                    Name = module.Keys.Count == 1 ? module.PropertyName : $"{module.PropertyName} {k.Index + 1}",
                                    Colour = k.Colour,
                                    ValueOffset = k.ValueOffset,
                                })
                                .ToList(),
                        });
                    }
                }
                catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }

                if (i % 25 == 0 || i == files.Length - 1)
                {
                    progress?.Report(new ColourScanProgress(
                        i + 1, files.Length, found.Sum(t => t.Colours.Count), Path.GetFileName(file)));
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return found;
    }

    /// <summary>Plain wording for what a module class does.</summary>
    private static string DescribeModule(string className) => className.ToLowerInvariant() switch
    {
        "particlemodulecolor" => "effect colour",
        "particlemodulecoloroverlife" => "effect colour over time",
        "particlemodulecolorscaleoverlife" => "effect brightness over time",
        _ => "effect colour",
    };

    /// <summary>
    /// Applies edits to a target and saves its package, taking a backup and
    /// swapping the file in atomically.
    /// </summary>
    /// <returns>The path of the pristine backup protecting the original.</returns>
    public static async Task<string> SaveAsync(
        ColourTarget target,
        IReadOnlyList<ColourEdit> edits,
        CancellationToken cancellationToken = default)
    {
        // Re-open from disk rather than trusting a cached copy: the package may
        // have changed since the scan, and writing stale content would undo it.
        Package package = Package.Open(target.PackagePath);

        byte[] patched = target.Kind switch
        {
            ColourSourceKind.Material =>
                MaterialParameterWriter.BuildPatchedExport(package, target.ExportIndex, edits, []),

            ColourSourceKind.ParticleEffect =>
                ParticleColourReader.BuildPatchedExport(package, target.ExportIndex, edits),

            _ => throw new InvalidOperationException($"Unknown colour source {target.Kind}."),
        };

        return await PackageWriter
            .SaveAsync(package, [new ExportPatch(target.ExportIndex, patched)], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Re-reads a target from disk, so listed values match the file.</summary>
    public static ColourTarget? Reload(ColourTarget target)
    {
        Package package = Package.Open(target.PackagePath);

        if (target.Kind == ColourSourceKind.Material)
        {
            MaterialInstance? material = MaterialParameterReader.TryRead(package, target.ExportIndex);
            if (material is null) return null;

            return target with
            {
                Colours = material.Colours
                    .Select(c => new ColourEntry { Name = c.Name, Colour = c.Colour, ValueOffset = c.ValueOffset })
                    .ToList(),
            };
        }

        ParticleColourModule? module = ParticleColourReader.TryRead(package, target.ExportIndex);
        if (module is null) return null;

        return target with
        {
            Colours = module.Keys
                .Select(k => new ColourEntry
                {
                    Name = module.Keys.Count == 1 ? module.PropertyName : $"{module.PropertyName} {k.Index + 1}",
                    Colour = k.Colour,
                    ValueOffset = k.ValueOffset,
                })
                .ToList(),
        };
    }
}
