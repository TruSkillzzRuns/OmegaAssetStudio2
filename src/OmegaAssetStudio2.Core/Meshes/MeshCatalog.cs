using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>Progress while scanning for meshes.</summary>
public sealed record MeshScanProgress(int PackagesScanned, int PackageCount, int MeshesFound, string CurrentPackage);

/// <summary>
/// Finds the models in a client's packages.
/// </summary>
/// <remarks>
/// This reports what a mesh <em>is</em> — its name, the space it occupies, and the
/// materials around it — not its geometry. The vertex and index buffers are
/// stored packed, in a layout that has not been derived yet, and nothing here
/// guesses at it. When that layout is worked out this is where geometry reading
/// will attach.
/// </remarks>
public sealed class MeshCatalog
{
    public async Task<IReadOnlyList<MeshInfo>> ScanAsync(
        GameClient client,
        string fileFilter = "*.upk",
        IProgress<MeshScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath))
            throw new DirectoryNotFoundException($"Content folder not found: {client.CookedPath}");

        string[] files = Directory.GetFiles(client.CookedPath, fileFilter);
        var found = new List<MeshInfo>();

        await Task.Run(() =>
        {
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string file = files[i];
                try
                {
                    Package package = Package.Open(file);

                    foreach (int index in package.FindExportsOfClass(MeshReader.StaticMeshClass))
                    {
                        MeshInfo? mesh = MeshReader.TryRead(package, index, MeshKind.Static);
                        if (mesh is not null) found.Add(mesh);
                    }

                    foreach (int index in package.FindExportsOfClass(MeshReader.SkeletalMeshClass))
                    {
                        MeshInfo? mesh = MeshReader.TryRead(package, index, MeshKind.Skeletal);
                        if (mesh is not null) found.Add(mesh);
                    }
                }
                catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }

                if (i % 25 == 0 || i == files.Length - 1)
                {
                    progress?.Report(new MeshScanProgress(
                        i + 1, files.Length, found.Count, Path.GetFileName(file)));
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return found;
    }
}
