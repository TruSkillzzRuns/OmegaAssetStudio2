using Microsoft.UI.Xaml.Media.Imaging;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;

namespace OmegaAssetStudio2.App.Icons;

/// <summary>
/// Loads the pictures the game shows for a character and for their powers.
/// </summary>
/// <remarks>
/// A definition names its picture as a package and a texture together —
/// <c>MarvelUIIcons.Power_Thor_HammerSwing</c> — and the package is shipped as
/// ICO__MarvelUIIcons_SF.upk beside everything else. So the name is all that is
/// needed to find it; nothing here guesses at which file an icon might be in.
/// <para>
/// One icon package holds close to eight thousand textures, so which export
/// carries which name is worked out once per package and kept.
/// </para>
/// </remarks>
public sealed class GameIconLoader
{
    private const string Prefix = "ICO__";
    private const string Suffix = "_SF.upk";

    private readonly IconImageService _images = new();

    private readonly Dictionary<string, WriteableBitmap?> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, int>> _byPackage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The picture behind an asset name, or null where there is none to show.
    /// </summary>
    /// <remarks>
    /// Never throws. An icon that cannot be read is a row without a picture,
    /// which is a great deal better than a panel that will not open.
    /// </remarks>
    public async Task<WriteableBitmap?> TryLoadAsync(string asset, string cookedPath)
    {
        if (asset.Length == 0 || cookedPath.Length == 0) return null;

        await _gate.WaitAsync().ConfigureAwait(true);

        try
        {
            if (_loaded.TryGetValue(asset, out WriteableBitmap? already)) return already;

            WriteableBitmap? made = await LoadAsync(asset, cookedPath).ConfigureAwait(true);

            _loaded[asset] = made;
            return made;
        }
        catch (Exception)
        {
            _loaded[asset] = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WriteableBitmap?> LoadAsync(string asset, string cookedPath)
    {
        int cut = asset.IndexOf('.');
        if (cut <= 0 || cut == asset.Length - 1) return null;

        string packageName = asset[..cut];
        string textureName = asset[(cut + 1)..];

        string path = Path.Combine(cookedPath, Prefix + packageName + Suffix);
        if (!File.Exists(path)) return null;

        Package package = Package.Open(path);

        Dictionary<string, int> named = Inside(package, path);

        if (!named.TryGetValue(textureName, out int export)) return null;

        TextureInfo? info = TextureInfo.TryRead(package, export);
        if (info is null) return null;

        return await _images.TryGetBitmapAsync(info, cookedPath).ConfigureAwait(true);
    }

    private Dictionary<string, int> Inside(Package package, string path)
    {
        if (_byPackage.TryGetValue(path, out Dictionary<string, int>? already)) return already;

        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (!package.GetExportClassName(i).Contains("texture", StringComparison.OrdinalIgnoreCase))
                continue;

            named[package.GetExportName(i)] = i;
        }

        _byPackage[path] = named;
        return named;
    }

    /// <summary>Lets go of everything read, for a different game folder.</summary>
    public void Clear()
    {
        _loaded.Clear();
        _byPackage.Clear();
        _images.Clear();
    }
}
