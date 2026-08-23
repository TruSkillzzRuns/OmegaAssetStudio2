using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Textures;

/// <summary>Progress while scanning a client's packages for textures.</summary>
public sealed record TextureScanProgress(int PackagesScanned, int PackageCount, int TexturesFound, string CurrentPackage);

/// <summary>
/// Finds textures across a client's cooked packages.
/// </summary>
public sealed class TextureCatalog
{
    /// <summary>Class name every texture export carries.</summary>
    private const string TextureClass = "texture2d";

    /// <summary>
    /// Scans packages under <paramref name="client"/> for textures.
    /// </summary>
    /// <param name="client">Which install to scan.</param>
    /// <param name="fileFilter">
    /// Filename pattern to narrow the scan, for example "ICO__*.upk". Defaults to
    /// every package, which takes considerably longer.
    /// </param>
    /// <remarks>
    /// A package that fails to open is skipped rather than aborting the scan — one
    /// bad file in fifteen thousand should not deny the user the other fourteen
    /// thousand. Failures are reported through <paramref name="onError"/>.
    /// </remarks>
    public async Task<IReadOnlyList<TextureInfo>> ScanAsync(
        GameClient client,
        string fileFilter = "*.upk",
        IProgress<TextureScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath))
            throw new DirectoryNotFoundException($"Content folder not found: {client.CookedPath}");

        string[] files = Directory.GetFiles(client.CookedPath, fileFilter);
        var found = new List<TextureInfo>();

        await Task.Run(() =>
        {
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string file = files[i];
                try
                {
                    Package package = Package.Open(file);

                    foreach (int index in package.FindExportsOfClass(TextureClass))
                    {
                        TextureInfo? info = TextureInfo.TryRead(package, index);
                        if (info is not null) found.Add(info);
                    }
                }
                catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke(file, ex);
                }

                // Reporting every package would flood the UI on a fifteen-thousand
                // file scan, so report on a cadence instead.
                if (i % 25 == 0 || i == files.Length - 1)
                {
                    progress?.Report(new TextureScanProgress(
                        i + 1, files.Length, found.Count, Path.GetFileName(file)));
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return found;
    }
}
