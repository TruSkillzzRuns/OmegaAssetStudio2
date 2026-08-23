using Windows.Graphics.Imaging;
using Windows.Storage;

namespace OmegaAssetStudio2.App.Icons;

/// <summary>An image loaded from disk, as straight RGBA.</summary>
public sealed record LoadedImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// Loads a user-supplied image file into the plain RGBA the encoder expects.
/// </summary>
/// <remarks>
/// Decoding is delegated to the platform, so whatever formats the system can open
/// are accepted without this project carrying its own decoders for each of them.
/// </remarks>
public static class ImageFileLoader
{
    /// <summary>File types offered in the picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    /// <summary>
    /// Loads an image. Returns null when the file cannot be decoded.
    /// </summary>
    public static async Task<LoadedImage?> TryLoadAsync(string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using Windows.Storage.Streams.IRandomAccessStream stream =
                await file.OpenAsync(FileAccessMode.Read);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            // Ask for exactly the layout the encoder wants, so no conversion is
            // needed afterwards: 8-bit RGBA with alpha kept separate rather than
            // pre-multiplied into the colour.
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return new LoadedImage(
                (int)decoder.PixelWidth,
                (int)decoder.PixelHeight,
                pixels.DetachPixelData());
        }
        catch (Exception)
        {
            // A file the platform cannot decode is a user-facing message, not a
            // crash.
            return null;
        }
    }
}
