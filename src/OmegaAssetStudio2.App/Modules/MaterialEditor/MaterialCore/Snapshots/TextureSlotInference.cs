namespace OmegaAssetStudio.WinUI.Modules.MaterialEditor.MaterialCore.Snapshots;

// Heuristic: classify a Texture2D's role from common suffix / token
// conventions. Used to (a) auto-label the slot kind when a parameter has a
// non-obvious name, and (b) warn if the user drops a DDS whose suffix
// strongly suggests a different slot than the one they're targeting.
public enum InferredSlotKind { Unknown, Diffuse, Normal, Mask, Specular, Emissive, Roughness, Metallic, Opacity, Detail }

public static class TextureSlotInference
{
    // Suffix table — checked against the end of the texture base name AND
    // the parameter name. Underscore- or hyphen-prefixed forms only so we
    // don't false-positive on e.g. "_normal_data" containing "data".
    private static readonly (string Suffix, InferredSlotKind Kind)[] s_suffixes =
    {
        ("_d", InferredSlotKind.Diffuse),
        ("_diff", InferredSlotKind.Diffuse),
        ("_diffuse", InferredSlotKind.Diffuse),
        ("_albedo", InferredSlotKind.Diffuse),
        ("_base", InferredSlotKind.Diffuse),
        ("_basecolor", InferredSlotKind.Diffuse),
        ("_n", InferredSlotKind.Normal),
        ("_norm", InferredSlotKind.Normal),
        ("_normal", InferredSlotKind.Normal),
        ("_nrm", InferredSlotKind.Normal),
        ("_m", InferredSlotKind.Mask),
        ("_mask", InferredSlotKind.Mask),
        ("_msk", InferredSlotKind.Mask),
        ("_s", InferredSlotKind.Specular),
        ("_spec", InferredSlotKind.Specular),
        ("_specular", InferredSlotKind.Specular),
        ("_e", InferredSlotKind.Emissive),
        ("_emiss", InferredSlotKind.Emissive),
        ("_emissive", InferredSlotKind.Emissive),
        ("_glow", InferredSlotKind.Emissive),
        ("_r", InferredSlotKind.Roughness),
        ("_rough", InferredSlotKind.Roughness),
        ("_roughness", InferredSlotKind.Roughness),
        ("_metal", InferredSlotKind.Metallic),
        ("_metallic", InferredSlotKind.Metallic),
        ("_opacity", InferredSlotKind.Opacity),
        ("_alpha", InferredSlotKind.Opacity),
        ("_detail", InferredSlotKind.Detail),
    };

    public static InferredSlotKind Infer(string textureBaseName)
    {
        if (string.IsNullOrWhiteSpace(textureBaseName)) return InferredSlotKind.Unknown;
        string lower = Path.GetFileNameWithoutExtension(textureBaseName).ToLowerInvariant();
        foreach (var (suffix, kind) in s_suffixes.OrderByDescending(p => p.Suffix.Length))
            if (lower.EndsWith(suffix, StringComparison.Ordinal)) return kind;
        // Also try a contains-as-token check for diffuse/normal/mask spelled out.
        foreach (var (suffix, kind) in s_suffixes)
            if (suffix.Length > 3 && lower.Contains(suffix, StringComparison.Ordinal)) return kind;
        return InferredSlotKind.Unknown;
    }

    public static string LabelOf(InferredSlotKind kind) => kind switch
    {
        InferredSlotKind.Diffuse   => "Diffuse",
        InferredSlotKind.Normal    => "Normal",
        InferredSlotKind.Mask      => "Mask",
        InferredSlotKind.Specular  => "Specular",
        InferredSlotKind.Emissive  => "Emissive",
        InferredSlotKind.Roughness => "Roughness",
        InferredSlotKind.Metallic  => "Metallic",
        InferredSlotKind.Opacity   => "Opacity",
        InferredSlotKind.Detail    => "Detail",
        _                          => "Unknown",
    };

    // Returns true + a warning message when the dropped texture's inferred
    // kind disagrees with the destination slot's inferred kind. The caller
    // (UI) can pop a confirm dialog before proceeding.
    public static bool ShouldWarnSlotMismatch(string sourceTextureName, string destinationSlotName, out string message)
    {
        var src = Infer(sourceTextureName);
        var dst = Infer(destinationSlotName);
        if (src != InferredSlotKind.Unknown && dst != InferredSlotKind.Unknown && src != dst)
        {
            message = $"This texture looks like {LabelOf(src)} but you're dropping it onto a {LabelOf(dst)} slot. Continue?";
            return true;
        }
        message = "";
        return false;
    }
}
