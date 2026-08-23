namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.Core;

// A texture reference that lives in a different UPK than the material that
// uses it. Populated by ForeignTextureCatalogService when it walks the
// imported material chain, opens the foreign package header, and indexes
// all Texture2D exports. Lets the editor surface textures (and their basic
// metadata) without re-parsing the source package every lookup.
//
// Referenced from an upstream ForeignTextureEntry of the same name —
// same fields, same intent.
public sealed class ForeignTextureEntry
{
    public required string TextureName { get; init; }
    public required string TexturePath { get; init; }
    public required string SourcePackagePath { get; init; }
    public required string SourcePackageName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string PixelFormat { get; init; } = string.Empty;
}
