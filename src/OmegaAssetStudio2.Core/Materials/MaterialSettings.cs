using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// Every value that decides how one material shades, gathered from the whole
/// chain: what the instance sets, what its parents set, and what the base
/// material declares for anything nobody set.
/// </summary>
public sealed record MaterialSettings
{
    /// <summary>The base material the chain ends at, by its full path.</summary>
    public required string BaseName { get; init; }

    public required IReadOnlyDictionary<string, float> Numbers { get; init; }

    public required IReadOnlyDictionary<string, MaterialColour> Colours { get; init; }

    public required IReadOnlyDictionary<string, bool> Choices { get; init; }

    /// <summary>
    /// Whether the material draws both sides of a surface.
    /// </summary>
    /// <remarks>
    /// A plain flag on the base material, not one of the compiled choices. It
    /// decides whether a run of triangles may be drawn from behind, which for
    /// anything built as a single sheet - a cape, a skirt, a strip of hair - is
    /// the difference between being there and not.
    /// </remarks>
    public bool TwoSided { get; init; }

    /// <summary>
    /// Whether the material cuts holes in itself rather than being solid
    /// everywhere.
    /// </summary>
    /// <remarks>
    /// Hair is built as flat cards with the strands painted on and the space
    /// between them cut away by the picture's own alpha. 432 of the game's
    /// character base materials blend this way. Drawn without the cut, every
    /// card is a solid slab, which is what was lying across faces.
    /// </remarks>
    /// <summary>
    /// Whether the material is lit at all.
    /// </summary>
    /// <remarks>
    /// 52 of the game's character base materials declare themselves unlit, and
    /// they mean it: an unlit material shows its own colour and nothing is
    /// allowed to shade it. Several of them are the props and weapons that came
    /// out muddy - a mine, a rocket launcher, a set of wings - because a
    /// lighting model was being applied to a surface that asked for none.
    /// </remarks>
    public bool Unlit { get; init; }

    public bool Cutout { get; init; }

    /// <summary>How opaque a point has to be to survive the cut.</summary>
    public float CutoutThreshold { get; init; } = 0.333f;

    public float Number(string name, float fallback) =>
        Numbers.TryGetValue(Plain(name), out float value) ? value : fallback;

    public MaterialColour Colour(string name, MaterialColour fallback) =>
        Colours.TryGetValue(Plain(name), out MaterialColour value) ? value : fallback;

    public bool On(string name, bool fallback = false) =>
        Choices.TryGetValue(Plain(name), out bool value) ? value : fallback;

    /// <summary>
    /// A parameter name with invisible characters taken out.
    /// </summary>
    /// <remarks>
    /// One of the character base material's own parameters is stored as
    /// d⁭iffusepower - the word "diffusepower" with an invisible
    /// formatting character sitting inside it, typed in by accident and cooked
    /// into every package that inherits from it. Looked up as spelt it is never
    /// found, and the material's diffuse power silently falls back to whatever
    /// the caller passed instead. Taking such characters out is reading the
    /// same name, not guessing at a different one.
    /// </remarks>
    internal static string Plain(string name)
    {
        if (name.Length == 0) return name;

        var kept = new System.Text.StringBuilder(name.Length);

        foreach (char letter in name)
        {
            if (char.GetUnicodeCategory(letter) != System.Globalization.UnicodeCategory.Format)
                kept.Append(letter);
        }

        return kept.Length == name.Length ? name : kept.ToString();
    }

    public static MaterialSettings Empty { get; } = new()
    {
        BaseName = string.Empty,
        Numbers = new Dictionary<string, float>(),
        Colours = new Dictionary<string, MaterialColour>(),
        Choices = new Dictionary<string, bool>(),
    };
}

/// <summary>
/// Gathers a material's settings the way the game resolves them.
/// </summary>
/// <remarks>
/// A material instance holds only what it changes. Everything else comes from
/// its parent, and finally from the base material, whose parameter nodes carry
/// the defaults. So the nearest setting wins and the base has the last word,
/// which is what this walk does.
/// <para>
/// The on-or-off choices are gathered the same way but need one extra care: an
/// instance lists every choice its base declares, not only the ones it made,
/// and marks which of them are its own. Taking the unmarked ones would let an
/// instance that never touched a choice overrule a parent that did.
/// </para>
/// </remarks>
public static class MaterialSettingsReader
{
    private const int MaxParentDepth = 16;
    private const string ParentProperty = "Parent";

    public static MaterialSettings Read(Package package, int exportIndex, ObjectLocator? locator = null)
    {
        var numbers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var colours = new Dictionary<string, MaterialColour>(StringComparer.OrdinalIgnoreCase);
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<(string, int)>();

        Package current = package;
        int index = exportIndex;
        string baseName = string.Empty;
        bool twoSided = false;
        bool cutout = false;
        bool unlit = false;
        float cutoutThreshold = 0.3333f;

        for (int depth = 0; depth < MaxParentDepth && index >= 0; depth++)
        {
            if (!visited.Add((current.Path, index))) break;
            if (index >= current.Exports.Count) break;

            PropertyBag? properties = current.TryReadProperties(index);
            if (properties is null) break;

            // Written only when it is true, as flags in this format are, so
            // seeing it anywhere up the chain settles it.
            if (properties.GetBool("TwoSided")) twoSided = true;

            // Written as the name of the blend mode, and only when it is not
            // the plain opaque one.
            if (properties.GetName("LightingModel").Contains("unlit", StringComparison.OrdinalIgnoreCase))
                unlit = true;

            string blend = properties.GetName("BlendMode");

            if (blend.Contains("mask", StringComparison.OrdinalIgnoreCase))
            {
                cutout = true;
                cutoutThreshold = properties.GetFloat("OpacityMaskClipValue", 0.3333f);
            }

            MaterialInstance? instance = MaterialParameterReader.TryRead(current, index);

            if (instance is not null)
            {
                foreach (ScalarParameter scalar in instance.Scalars)
                    numbers.TryAdd(MaterialSettings.Plain(scalar.Name), scalar.Value);

                foreach (ColourParameter colour in instance.Colours)
                    colours.TryAdd(MaterialSettings.Plain(colour.Name), colour.Colour);
            }

            (IReadOnlyList<StaticSwitch> made, BlendOverride blending) =
                MaterialStaticParameters.ReadAll(current, index);

            // An instance can replace the blend mode its base declares, and it
            // writes that into its compiled resource rather than beside its
            // properties. Costumes whose base is solid have their hair cut out
            // this way, so a reading that only looks at the base misses them.
            if (blending.Overrides && !cutout)
            {
                cutout = blending.Masked;
                if (cutout) cutoutThreshold = properties.GetFloat("OpacityMaskClipValue", 0.3333f);
            }

            foreach (StaticSwitch choice in made)
            {
                // Only the ones this instance actually made. The rest are its
                // record of what it inherited, and belong to whoever set them.
                if (choice.Overrides) choices.TryAdd(MaterialSettings.Plain(choice.Name), choice.Value);
            }

            ObjectReference parent = properties.GetObject(ParentProperty);

            if (parent.IsNull)
            {
                baseName = current.GetExportPath(index);
                ReadDeclared(current, index, numbers, colours, choices);
                break;
            }

            LocatedObject? found = locator?.TryLocate(current, parent);

            if (found is not null)
            {
                current = found.Value.Package;
                index = found.Value.ExportIndex;
            }
            else
            {
                index = MaterialTextureReader.ResolveExport(current, parent);
            }
        }

        return new MaterialSettings
        {
            BaseName = baseName,
            TwoSided = twoSided,
            Unlit = unlit,
            Cutout = cutout,
            CutoutThreshold = cutoutThreshold,
            Numbers = numbers,
            Colours = colours,
            Choices = choices,
        };
    }

    /// <summary>
    /// What the base material declares: each parameter node's own default, for
    /// every name no instance in the chain set.
    /// </summary>
    private static void ReadDeclared(
        Package package,
        int material,
        Dictionary<string, float> numbers,
        Dictionary<string, MaterialColour> colours,
        Dictionary<string, bool> choices)
    {
        string outer = package.GetExportPath(material);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            string kind = package.GetExportClassName(i);

            if (!kind.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)) continue;
            if (!kind.Contains("Parameter", StringComparison.OrdinalIgnoreCase)) continue;
            if (!package.GetExportPath(i).StartsWith(outer, StringComparison.OrdinalIgnoreCase)) continue;

            PropertyBag? bag = package.TryReadProperties(i);
            if (bag is null) continue;

            string name = MaterialSettings.Plain(bag.GetName("ParameterName"));
            if (name.Length == 0) continue;

            PropertyTag? value = bag.Find("DefaultValue");

            if (kind.Contains("Scalar", StringComparison.OrdinalIgnoreCase))
            {
                // A node with no written default means the default is zero, so
                // the name is still recorded.
                numbers.TryAdd(name, value is not null && value.Value.Length >= 4
                    ? BitConverter.ToSingle(value.Value.Span)
                    : 0f);
            }
            else if (kind.Contains("Vector", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadColour(value, out MaterialColour colour)) colours.TryAdd(name, colour);
            }
            else if (kind.Contains("Switch", StringComparison.OrdinalIgnoreCase)
                  || kind.Contains("Bool", StringComparison.OrdinalIgnoreCase))
            {
                choices.TryAdd(name, value is not null && value.Value.Length > 0 && value.Value.Span[0] != 0);
            }
        }
    }

    private static bool TryReadColour(PropertyTag? tag, out MaterialColour colour)
    {
        colour = default;

        if (tag is null) return false;

        ReadOnlySpan<byte> value = tag.Value.Span;
        if (value.Length < 16) return false;

        colour = new MaterialColour(
            BitConverter.ToSingle(value),
            BitConverter.ToSingle(value[4..]),
            BitConverter.ToSingle(value[8..]),
            BitConverter.ToSingle(value[12..]));

        return true;
    }
}
