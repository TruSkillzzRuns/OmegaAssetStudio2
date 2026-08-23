namespace OmegaAssetStudio.TexturePreview;

public sealed class TextureToMaterialConverter
{
    public TexturePreviewMaterialSlot ResolveSlot(string sourceName, TexturePreviewMaterialSlot fallbackSlot)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return fallbackSlot;

        string name = sourceName.ToLowerInvariant();
        if (ContainsAny(name, "normal", "normalmap", "tangentnormal", "_nrm", "_norm", "_nm", "_n", "nrm", "norm", "bump"))
            return TexturePreviewMaterialSlot.Normal;
        if (ContainsAny(name, "mask", "opacity", "alpha", "ao", "occlusion", "orm", "specmask", "skinmask", "rimmask"))
            return TexturePreviewMaterialSlot.Mask;
        if (ContainsAny(name, "specular", "spec", "gloss", "refl", "reflection"))
            return TexturePreviewMaterialSlot.Specular;
        if (ContainsAny(name, "emiss", "glow", "illum", "selfillum"))
            return TexturePreviewMaterialSlot.Emissive;
        if (ContainsAny(name, "diffuse", "diff", "albedo", "basecolor", "base_color", "basecol", "maintex", "main"))
            return TexturePreviewMaterialSlot.Diffuse;

        return fallbackSlot;
    }

    public void ApplyToMaterial(TexturePreviewMaterialSet materialSet, TexturePreviewTexture texture)
    {
        materialSet.SetTexture(texture.Slot, texture);
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        foreach (string value in values)
        {
            if (source.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

