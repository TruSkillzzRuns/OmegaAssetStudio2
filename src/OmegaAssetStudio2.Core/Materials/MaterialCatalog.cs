using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>Progress while scanning for materials.</summary>
public sealed record MaterialScanProgress(int PackagesScanned, int PackageCount, int MaterialsFound, string CurrentPackage);

/// <summary>
/// Finds material instances with editable parameters across a client's packages.
/// </summary>
public sealed class MaterialCatalog
{
    /// <summary>
    /// Scans for material instances that override at least one parameter.
    /// </summary>
    /// <remarks>
    /// Instances that override nothing are skipped: they have no colour to edit,
    /// and listing them would bury the ones that do among thousands that do not.
    /// </remarks>
    public async Task<IReadOnlyList<MaterialInstance>> ScanAsync(
        GameClient client,
        string fileFilter = "*.upk",
        IProgress<MaterialScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath))
            throw new DirectoryNotFoundException($"Content folder not found: {client.CookedPath}");

        string[] files = Directory.GetFiles(client.CookedPath, fileFilter);
        var found = new List<MaterialInstance>();

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
                        if (material is not null && material.HasEditableParameters) found.Add(material);
                    }
                }
                catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }

                if (i % 25 == 0 || i == files.Length - 1)
                {
                    progress?.Report(new MaterialScanProgress(
                        i + 1, files.Length, found.Count, Path.GetFileName(file)));
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return found;
    }
}
