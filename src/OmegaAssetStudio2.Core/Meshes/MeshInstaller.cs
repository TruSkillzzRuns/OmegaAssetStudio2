using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>
/// Exactly what putting a model into the game would change.
/// </summary>
/// <remarks>
/// Built before anything is written so it can be shown to the user and agreed
/// to. Everything in it is measured, not estimated.
/// </remarks>
public sealed record MeshInstallPlan
{
    /// <summary>The file on disk that would be rewritten.</summary>
    public required string PackagePath { get; init; }

    /// <summary>The object inside it that would be replaced.</summary>
    public required string ObjectName { get; init; }

    public required int VerticesBefore { get; init; }
    public required int VerticesAfter { get; init; }
    public required int TrianglesBefore { get; init; }
    public required int TrianglesAfter { get; init; }

    /// <summary>
    /// How many levels of detail the model has, all of which are rewritten.
    /// </summary>
    public required int DetailLevels { get; init; }

    /// <summary>
    /// The powers that reshape this model, renumbered onto the new one.
    /// </summary>
    public IReadOnlyList<MorphRemapReport> Morphs { get; init; } = [];

    public required long FileSizeBefore { get; init; }
    public required long FileSizeAfter { get; init; }

    /// <summary>
    /// The whole package as it would land on disk, already read back and
    /// checked. Committing writes these very bytes, so what was agreed to is
    /// what is written.
    /// </summary>
    public required byte[] Content { get; init; }

    public string FileName => Path.GetFileName(PackagePath);
}

/// <summary>The outcome of putting a model into the game.</summary>
public sealed record MeshInstallResult
{
    public required string PackagePath { get; init; }

    /// <summary>Where the pristine copy of the file now sits.</summary>
    public required string BackupPath { get; init; }
}

/// <summary>
/// Puts a model into a package in the user's game install.
/// </summary>
/// <remarks>
/// This is the only thing in the retarget that writes into the game, so the
/// order matters and is deliberate:
/// <list type="number">
/// <item>Build the new package in memory.</item>
/// <item>Read the model back out of those bytes with the ordinary reader. If
/// this application cannot read what it just wrote, neither can the game, and
/// nothing goes near the disk.</item>
/// <item>Only then commit, through the one write path that backs the file up
/// first and swaps the new one in atomically.</item>
/// </list>
/// Planning and committing are separate so the user can be told exactly which
/// file and which object will change, and agree to it, before step three.
/// </remarks>
public static class MeshInstaller
{
    /// <summary>
    /// Works out what would change, without touching anything.
    /// </summary>
    /// <exception cref="MeshWriteException">The model cannot be written at all.</exception>
    /// <exception cref="PackageRebuildException">The package cannot be rebuilt.</exception>
    public static MeshInstallPlan Plan(
        Package package, int exportIndex, SkeletalMesh mesh, MeshGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(geometry);

        if (string.IsNullOrWhiteSpace(package.Path) || !File.Exists(package.Path))
            throw new MeshWriteException("This package is not a file on disk, so nothing can be written to it.");

        SkeletalMeshLod before = mesh.HighestDetail
            ?? throw new MeshWriteException("This model has no geometry to replace.");

        byte[] written = SkeletalMeshSerialiser.Replace(package, exportIndex, mesh, geometry);

        var patches = new List<ExportPatch> { new(exportIndex, written) };

        // The powers that reshape this model name the vertices they move by
        // number, and those numbers have just changed. Left as they are, the
        // model stands correctly and comes apart the moment a power fires.
        IReadOnlyList<MorphRemapReport> morphs = Remap(package, exportIndex, mesh, written, patches);

        byte[] rebuilt = PackageRebuilder.Build(package, patches);

        Verify(rebuilt, package.Path, exportIndex, geometry);

        return new MeshInstallPlan
        {
            PackagePath = package.Path,
            ObjectName = mesh.Name,
            VerticesBefore = before.Positions.Count,
            VerticesAfter = geometry.Positions.Count,
            TrianglesBefore = before.TriangleCount,
            TrianglesAfter = geometry.TriangleCount,
            DetailLevels = mesh.Lods.Count,
            Morphs = morphs,
            FileSizeBefore = new FileInfo(package.Path).Length,
            FileSizeAfter = rebuilt.LongLength,
            Content = rebuilt,
        };
    }

    /// <summary>
    /// Renumbers every set of displacements in the package onto the new model.
    /// </summary>
    /// <remarks>
    /// Read back from the very bytes about to be written, so the numbering they
    /// are matched against is the one the game will see, not one worked out
    /// separately and hoped to agree.
    /// </remarks>
    private static IReadOnlyList<MorphRemapReport> Remap(
        Package package, int exportIndex, SkeletalMesh mesh, byte[] written, List<ExportPatch> patches)
    {
        IReadOnlyList<MorphTarget> targets = MorphTargetReader.ReadAll(package);

        if (targets.Count == 0 || mesh.HighestDetail is not { } original) return [];

        // Where every vertex of the new model ended up.
        Package staged = Package.Read(
            PackageRebuilder.Build(package, [new ExportPatch(exportIndex, written)]), package.Path);

        if (SkeletalMeshReader.TryRead(staged, exportIndex)?.HighestDetail is not { } after) return [];

        var reports = new List<MorphRemapReport>(targets.Count);

        foreach (MorphTarget target in targets)
        {
            if (target.DeltaCount == 0) continue;

            (IReadOnlyList<MorphLevel> levels, MorphRemapReport report) =
                MorphRemapper.Apply(original, after.Positions, target);

            patches.Add(new ExportPatch(
                target.ExportIndex, MorphTargetReader.Replace(package, target, levels)));

            reports.Add(report);
        }

        return reports;
    }

    /// <summary>
    /// Reads the model back out of the bytes about to be written.
    /// </summary>
    /// <remarks>
    /// The reader here is the same one the rest of the application uses on the
    /// game's own files, so it is standing in for the game. A model it cannot
    /// read, or that comes back describing something other than what was put
    /// in, is a model that would break the game — and that is caught here,
    /// before the file is touched, rather than by the user at a loading screen.
    /// </remarks>
    private static void Verify(byte[] rebuilt, string path, int exportIndex, MeshGeometry geometry)
    {
        Package reopened;

        try
        {
            reopened = Package.Read(rebuilt, path);
        }
        catch (Exception ex) when (ex is InvalidPackageException or IOException)
        {
            throw new MeshWriteException(
                $"The rebuilt package could not be read back, so it was not written: {ex.Message}");
        }

        string? problem = null;
        SkeletalMesh? read = SkeletalMeshReader.TryRead(reopened, exportIndex, why => problem = why);

        if (read?.HighestDetail is not { } after)
        {
            throw new MeshWriteException(
                $"The written model could not be read back, so it was not saved: {problem ?? "no geometry"}");
        }

        if (after.TriangleCount != geometry.TriangleCount)
        {
            throw new MeshWriteException(
                $"The written model came back with {after.TriangleCount:N0} triangles instead of " +
                $"{geometry.TriangleCount:N0}, so it was not saved.");
        }

        // Vertices are renumbered on the way out — each run of the model owns
        // its own, and one shared between two runs is written into both — so
        // the check is what the triangles actually draw, corner by corner,
        // rather than what sits at any particular index.
        for (int c = 0; c < after.Indices.Count; c++)
        {
            System.Numerics.Vector3 wanted = geometry.Positions[geometry.Indices[c]];
            System.Numerics.Vector3 got = after.Positions[after.Indices[c]];

            if (System.Numerics.Vector3.Distance(wanted, got) <= 0.001f) continue;

            throw new MeshWriteException(
                $"The written model draws a corner at {got.X:0.##},{got.Y:0.##},{got.Z:0.##} where " +
                $"{wanted.X:0.##},{wanted.Y:0.##},{wanted.Z:0.##} was asked for, so it was not saved.");
        }
    }

    /// <summary>
    /// Commits a plan: backs the file up, then swaps the new one in atomically.
    /// </summary>
    /// <remarks>
    /// Nothing is rebuilt here. What is written is the very bytes that were
    /// checked when the plan was made, so what the user agreed to is what lands.
    /// </remarks>
    public static async Task<MeshInstallResult> CommitAsync(
        MeshInstallPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string backup = await SafeFileWriter
            .WriteAsync(plan.PackagePath, plan.Content, cancellationToken)
            .ConfigureAwait(false);

        return new MeshInstallResult { PackagePath = plan.PackagePath, BackupPath = backup };
    }
}
