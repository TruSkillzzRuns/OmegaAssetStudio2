using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>One texture a material binds, and the name of the slot it fills.</summary>
public sealed record MaterialTextureSlot
{
    public required string ParameterName { get; init; }
    public required ObjectReference Texture { get; init; }

    /// <summary>The texture's own name, for choosing between slots.</summary>
    public required string TextureName { get; init; }

    /// <summary>
    /// The package this slot was read from. A material inherited from another
    /// package numbers its textures against that package, not against the one
    /// that started the search, so the two have to travel together. Null means
    /// the package the search began in.
    /// </summary>
    public Package? Source { get; init; }

    public override string ToString() => $"{ParameterName} = {TextureName}";
}

/// <summary>
/// Finds the textures a material binds, and which of them is the one to show.
/// </summary>
/// <remarks>
/// A material instance overrides some of its parent's slots and inherits the
/// rest, so a slot missing from the instance is not missing from the material —
/// it is further up the chain. The parent is followed for exactly that reason.
/// </remarks>
public static class MaterialTextureReader
{
    private const string TextureArrayProperty = "TextureParameterValues";
    private const string ParameterNameProperty = "ParameterName";
    private const string ParameterValueProperty = "ParameterValue";
    private const string ParentProperty = "Parent";

    /// <summary>How far up the parent chain to look before giving up.</summary>
    private const int MaxParentDepth = 8;

    /// <summary>
    /// Names that mark a slot as the visible colour of a surface.
    /// </summary>
    private static readonly string[] SurfaceNames =
    [
        "diffuse", "diff", "albedo", "basecolor", "base_color", "color", "colour",
    ];

    /// <summary>
    /// Names that mark a slot as something other than colour. A slot matching
    /// one of these is never chosen by the fallback, because drawing a normal
    /// map or a mask as if it were colour looks worse than drawing nothing.
    /// </summary>
    private static readonly string[] NotSurfaceNames =
    [
        "spec", "specular", "normal", "norm", "nrml", "nrm",
        "emiss", "glow", "mask", "opacity", "alpha", "ao", "ambient",
        "cube", "env", "lookup", "ramp", "noise", "flow",
    ];

    /// <summary>
    /// Reads every texture slot a material binds, following its parents.
    /// </summary>
    /// <remarks>
    /// A slot set closer to the instance wins, which is what overriding means.
    /// </remarks>
    /// <param name="locator">
    /// Follows a parent that lives in another package. Without one, the chain
    /// stops at the edge of this package.
    /// </param>
    public static IReadOnlyList<MaterialTextureSlot> ReadSlots(
        Package package, int exportIndex, ObjectLocator? locator = null)
    {
        var slots = new List<MaterialTextureSlot>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<(string, int)>();

        Package current = package;
        int index = exportIndex;

        for (int depth = 0; depth < MaxParentDepth && index >= 0; depth++)
        {
            if (!visited.Add((current.Path, index))) break;
            if (index >= current.Exports.Count) break;

            PropertyBag? properties = current.TryReadProperties(index);
            if (properties is null) break;

            foreach (MaterialTextureSlot slot in ReadOwnSlots(current, properties))
            {
                if (claimed.Add(slot.ParameterName)) slots.Add(slot);
            }

            ObjectReference parent = properties.GetObject(ParentProperty);

            if (parent.IsNull)
            {
                // The end of the chain is the material itself, which holds the
                // default for every slot its instances did not set.
                foreach (MaterialTextureSlot slot in ReadDefaults(current, properties))
                {
                    if (claimed.Add(slot.ParameterName)) slots.Add(slot);
                }

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
                index = ResolveExport(current, parent);
            }
        }

        return slots;
    }

    /// <summary>Names a base material uses to mean "nothing bound here".</summary>
    /// <remarks>
    /// A base material gives every slot a default so its graph always has
    /// something to sample. Those defaults are placeholders, not content: a
    /// flat white or a flat normal. Taking them at face value would paint an
    /// unset costume white and call a surface fully reflective everywhere, so
    /// they are read as the absence they are.
    /// </remarks>
    private static readonly string[] Placeholders =
    [
        "whitetexture", "normalplaceholder", "blacktexture", "defaulttexture", "placeholder",
    ];

    /// <summary>
    /// The texture defaults a base material declares in its own graph.
    /// </summary>
    /// <remarks>
    /// An instance sets the slots that differ from its parent; everything else
    /// is whatever the base material's graph samples. Reading only the
    /// instances therefore misses real content - the skin base material
    /// declares reflectiontex as pano_mountains_small and ramp as tf2_ramp, and
    /// costumes that never override those were being drawn with no reflection
    /// and no lighting curve at all.
    /// </remarks>
    private static IReadOnlyList<MaterialTextureSlot> ReadDefaults(Package package, PropertyBag properties)
    {
        PropertyTag? expressions = properties.Find(ExpressionsProperty);
        if (expressions is null || expressions.Value.Length < sizeof(int)) return [];

        ReadOnlySpan<byte> value = expressions.Value.Span;

        int count = BitConverter.ToInt32(value);
        if (count <= 0 || count > MaxExpressions) return [];

        var found = new List<MaterialTextureSlot>();

        for (int i = 0; i < count; i++)
        {
            int at = sizeof(int) + (i * sizeof(int));
            if (at + sizeof(int) > value.Length) break;

            var reference = new ObjectReference(BitConverter.ToInt32(value[at..]));
            if (!reference.IsExport) continue;

            int index = reference.ExportIndex;
            if (index < 0 || index >= package.Exports.Count) continue;

            string kind;
            try { kind = package.GetExportClassName(index); }
            catch (InvalidPackageException) { continue; }

            // Only the nodes that sample a texture into a named slot.
            if (!kind.StartsWith(TextureExpressionPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            PropertyBag? inner = package.TryReadProperties(index);
            if (inner is null) continue;

            string name = inner.GetName(ParameterNameProperty);
            if (name.Length == 0) continue;

            ObjectReference texture = inner.GetObject(TextureProperty);
            if (texture.IsNull) continue;

            string textureName = package.ResolveName(texture);
            if (textureName.Length == 0) continue;

            bool placeholder = false;
            foreach (string word in Placeholders)
            {
                if (textureName.Equals(word, StringComparison.OrdinalIgnoreCase)) placeholder = true;
            }

            if (placeholder) continue;

            found.Add(new MaterialTextureSlot
            {
                ParameterName = name,
                Texture = texture,
                TextureName = textureName,
                Source = package,
            });
        }

        return found;
    }

    private const string ExpressionsProperty = "expressions";
    private const string TextureProperty = "Texture";
    private const string TextureExpressionPrefix = "materialexpressiontexture";

    /// <summary>Guards against a corrupt count producing an unbounded read.</summary>
    private const int MaxExpressions = 4096;

    private static IReadOnlyList<MaterialTextureSlot> ReadOwnSlots(Package package, PropertyBag properties)
    {
        PropertyTag? array = properties.Find(TextureArrayProperty);
        if (array is null) return [];

        var found = new List<MaterialTextureSlot>();

        foreach (StructArrayElement element in StructArray.ReadElements(array, package.Names))
        {
            string name = element.Properties.GetName(ParameterNameProperty);
            ObjectReference texture = element.Properties.GetObject(ParameterValueProperty);

            if (texture.IsNull) continue;

            found.Add(new MaterialTextureSlot
            {
                ParameterName = name.Length > 0 ? name : "(unnamed)",
                Texture = texture,
                TextureName = package.ResolveName(texture),
                Source = package,
            });
        }

        return found;
    }

    /// <summary>
    /// Picks the slot that carries the surface's colour.
    /// </summary>
    /// <remarks>
    /// Slot names are not standardised across this game's materials — the same
    /// channel appears as several different names — so a name match is tried
    /// first, then anything not positively identified as another channel, and
    /// only then the first slot there is.
    /// </remarks>
    public static MaterialTextureSlot? PickSurfaceSlot(IReadOnlyList<MaterialTextureSlot> slots)
        => PickSurfaceSlot(slots, corrected: false);

    /// <param name="corrected">
    /// Whether to prefer the full-quality picture over the low-quality one, and
    /// to refuse a panorama outright. Both were found against 1.53.0.203 and
    /// are asked for only by that build's models.
    /// </param>
    public static MaterialTextureSlot? PickSurfaceSlot(
        IReadOnlyList<MaterialTextureSlot> slots, bool corrected)
    {
        if (slots.Count == 0) return null;

        // The full-quality picture first. A handful of materials bind both, and
        // list the low-quality one first: four costumes across three characters
        // were all being drawn from their lq_diffusetex.
        // Whether the game reaches for it is its own uselqdiffusetex, which
        // those materials leave off.
        if (corrected)
        {
            foreach (MaterialTextureSlot slot in slots)
            {
                if (LowQuality(slot.ParameterName)) continue;

                if (Mentions(slot.ParameterName, SurfaceNames) || Mentions(slot.TextureName, SurfaceNames))
                    return slot;
            }
        }

        foreach (MaterialTextureSlot slot in slots)
        {
            if (Mentions(slot.ParameterName, SurfaceNames) || Mentions(slot.TextureName, SurfaceNames))
                return slot;
        }

        foreach (MaterialTextureSlot slot in slots)
        {
            if (!Mentions($"{slot.ParameterName} {slot.TextureName}", NotSurfaceNames))
                return slot;
        }

        // Every slot this material names is positively something else. Taking
        // the first of them anyway is right far more often than not - an ice
        // staff, an ice shield and a lightning trail each name nothing but
        // effect textures, and those textures are what they show.
        //
        // A panorama is the exception, because it is never what a surface
        // shows. One prop names a reflection cube and nothing else; taking
        // that as its colour looked wrong and stopped the material's compiled
        // form, which samples a plain researchfiles_diff, from being asked at
        // all.
        if (!corrected) return slots[0];

        foreach (MaterialTextureSlot slot in slots)
        {
            if (!Mentions($"{slot.ParameterName} {slot.TextureName}", PanoramaNames)) return slot;
        }

        return null;
    }

    /// <summary>
    /// Names that mark a slot as the panorama a surface reflects, which is the
    /// one thing a surface's colour is never read from.
    /// </summary>
    private static readonly string[] PanoramaNames = ["cube", "reflectiontex"];

    /// <summary>Whether a slot's name marks it as the low-quality picture.</summary>
    private static bool LowQuality(string parameterName) =>
        parameterName.StartsWith("lq_", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("_lq", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("lowquality", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The slot that holds the colour a surface's highlight and reflection take.
    /// </summary>
    /// <remarks>
    /// One name again, from the same survey: <c>speccolortex</c>, 586 uses. It
    /// is what makes gold armour reflect gold rather than reflect grey, and it
    /// is a separate thing from the packed mask that says how much reflection
    /// there is.
    /// <para>
    /// Matching on the word "spec" instead handed this the packed mask on any
    /// material that listed the mask first - the mask is data, not a colour, so
    /// tinting a reflection with it is meaningless.
    /// </para>
    /// </remarks>
    private const string SpecularColourParameter = "speccolortex";

    /// <summary>
    /// The slot that holds the picture a surface reflects.
    /// </summary>
    /// <remarks>
    /// One name, not a list of words. Surveyed across every character package
    /// in the 1.52 client - 929 models, 27 distinct texture parameters in all -
    /// the picture reflected is always bound to <c>reflectiontex</c>, and the
    /// textures it names are panoramas.
    /// <para>
    /// Matching the word "reflect" anywhere instead put green and blue blotches
    /// over one armoured costume. It binds no panorama at all; its only slot
    /// mentioning the word is <c>specmultrimmaskreflection</c>, the packed
    /// mask, which was then read as though it were a sky - so the surface
    /// reflected its own spec map, green channel and all.
    /// </para>
    /// </remarks>
    private const string EnvironmentParameter = "reflectiontex";

    /// <summary>Names that mark a slot as the normal map.</summary>
    private static readonly string[] NormalNames =
    [
        "normal", "norm", "nrml", "nrm", "bump",
    ];

    /// <summary>
    /// The slot carrying the packed surface mask, or null.
    /// </summary>
    /// <remarks>
    /// These materials pack several one-channel maps into one texture and say
    /// so in the parameter's own name - specmult_specpow_skinmask_reflectivity
    /// is four of them in R, G, B and A, in that order. So the name is the
    /// documentation, and which channel means what is read from it rather than
    /// assumed.
    /// </remarks>
    /// <summary>
    /// The packed-mask parameters this game uses, most informative first.
    /// </summary>
    /// <remarks>
    /// Taken from a survey of every character package in the 1.52 client rather
    /// than from a guess at what the names might be: across 929 models these
    /// seven are the whole vocabulary of packed masks, and the ones naming
    /// specmult first are preferred because that is the channel the highlight
    /// is read from.
    /// </remarks>
    private static readonly string[] MaskParameters =
    [
        "specmult_specpow_skinmask_reflectivity",
        "specmult_specpow_reflectivity_emissive",
        "specmultrimmaskreflection",
        "specemissivereflectheight1",
        "emissivespecpowambient",
        "emissivespecpow",
    ];

    public static MaterialTextureSlot? PickMaskSlot(IReadOnlyList<MaterialTextureSlot> slots)
    {
        foreach (string wanted in MaskParameters)
        {
            foreach (MaterialTextureSlot slot in slots)
            {
                if (string.Equals(slot.ParameterName, wanted, StringComparison.OrdinalIgnoreCase))
                    return slot;
            }
        }

        // A spelling the survey did not see still counts if it names the
        // channel the highlight comes from.
        return FirstMentioning(slots, ["specmult"]);
    }

    /// <summary>
    /// Which channel of the mask carries a named quantity, as R, G, B, A - or
    /// -1 when the name does not mention it.
    /// </summary>
    /// <remarks>
    /// Verified against the game's own textures. A mostly-cloth costume's mask
    /// is named specmult_specpow_skinmask_reflectivity and averages
    /// R=62 G=11 B=30 A=14 - its reflectivity, the fourth word and so the
    /// fourth channel, is the low one. An all-chrome costume's mask is named
    /// specmultrimmaskreflection and averages R=39 G=255 B=132, with
    /// reflection high. That one is stored without alpha, so its words count
    /// as three and reflection lands in B.
    /// </remarks>
    public static int ChannelFor(string parameterName, string quantity, int channelCount = 4)
    {
        if (string.IsNullOrWhiteSpace(parameterName)) return -1;

        string[] words = parameterName.Contains('_')
            ? parameterName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            : SplitRunTogether(parameterName);

        int available = Math.Clamp(channelCount, 1, 4);

        for (int i = 0; i < words.Length && i < 4; i++)
        {
            if (!words[i].Contains(quantity, StringComparison.OrdinalIgnoreCase)) continue;

            // A name can list more channels than its texture carries. When it
            // does, the names past the end describe nothing: a three-channel
            // texture has no fourth channel, so a fourth name is answered with
            // "not present" rather than folded onto the last real one.
            //
            // Folding was tried and is wrong. Under a four-name layout the
            // third channel is the skin mask, so folding "reflectivity" onto it
            // told the viewer that skin was reflective - which painted the
            // reflected picture over faces and bodies. One costume's mask is
            // DXT1 with its third channel at about half across the body, and
            // its material reflects a brown-gold panorama; folded, half of that
            // costume's colour was replaced by gold.
            //
            // The cost of reading it this way is that a mask which really does
            // carry reflection in its third channel, but is bound under a
            // four-name parameter, loses its shine. That is the safer of the
            // two mistakes: a surface that should gleam looks flat, rather than
            // a face being painted over with something the texture never said.
            return i < available ? i : -1;
        }

        return -1;
    }

    /// <summary>
    /// Which channel of a mask carries the highlight, or -1.
    /// </summary>
    /// <remarks>
    /// Named specmult where a material spells the channel out in full, and
    /// simply spec where it does not - specemissivereflectheight1 is the
    /// second spelling, and reading only the first left those surfaces with no
    /// highlight at all.
    /// </remarks>
    public static int GlossChannelFor(string parameterName, int channelCount = 4)
    {
        int named = ChannelFor(parameterName, "specmult", channelCount);
        return named >= 0 ? named : ChannelFor(parameterName, "spec", channelCount);
    }

    /// <summary>Which channel of a mask carries the reflectivity, or -1.</summary>
    public static int ReflectChannelFor(string parameterName, int channelCount = 4)
        => ChannelFor(parameterName, "reflect", channelCount);

    /// <summary>
    /// Which channel says where the rim light shows, or -1 when the mask does
    /// not carry one.
    /// </summary>
    /// <remarks>
    /// The commonest mask in the game is bound as specmultrimmaskreflection,
    /// whose middle channel is exactly this, and 1,363 of the material
    /// instances on listed models have userimmask switched on. Nothing has read
    /// it until now, so every one of those surfaces has had its rim light
    /// applied evenly instead of where its mask puts it.
    /// </remarks>
    public static int RimChannelFor(string parameterName, int channelCount = 4)
        => ChannelFor(parameterName, "rimmask", channelCount);

    /// <summary>
    /// Which channel says how sharp the shine is, or -1 when the mask does not
    /// carry one.
    /// </summary>
    /// <remarks>
    /// Named specpow, and it is a different thing from specmult beside it: one
    /// is how strong the shine is, the other how tight. Reading the strength as
    /// though it were the tightness gave polished surfaces a broad sheen and
    /// rough ones a hard glint, both backwards.
    /// <para>
    /// A mask bound under the three-name form carries no such channel, and says
    /// so by answering -1 rather than by handing back a neighbour.
    /// </para>
    /// </remarks>
    public static int SharpnessChannelFor(string parameterName, int channelCount = 4)
        => ChannelFor(parameterName, "specpow", channelCount);

    /// <summary>
    /// The words of a name written without separators. Only the spellings this
    /// game actually uses are recognised; anything else is left whole rather
    /// than chopped up on a guess.
    /// </summary>
    private static string[] SplitRunTogether(string name)
    {
        // Longest first, so specmult is never read as spec followed by mult.
        string[] known =
        [
            "reflectivity", "reflection", "specmult", "specpow", "rimmask", "skinmask",
            "emissive", "ambient", "diffuse", "reflect", "height", "spec", "rim", "mask",
        ];

        var words = new List<string>();
        int at = 0;

        while (at < name.Length)
        {
            string? hit = null;

            foreach (string word in known)
            {
                if (name.AsSpan(at).StartsWith(word, StringComparison.OrdinalIgnoreCase)) { hit = word; break; }
            }

            if (hit is null) break;

            words.Add(hit);
            at += hit.Length;
        }

        return words.Count > 0 ? words.ToArray() : [name];
    }

    /// <summary>
    /// The parameters that name a lighting ramp.
    /// </summary>
    /// <remarks>
    /// A ramp is a curve, not a picture of anything: the engine takes how
    /// square-on a surface is to the light, looks that up along the ramp, and
    /// lights by what it finds. It is why this game's characters have a soft
    /// wrap into shadow rather than the hard cosine falloff of plain lambert -
    /// the textures are even named for it, tf2_rampsofter and
    /// tf2_rampsofterwhite. Read across, they run from near black at 16,12,16
    /// to white, and the softer one stops at 219 rather than 255.
    /// <para>
    /// 302 of 5,260 character material slots bind one, so this is not the
    /// majority of surfaces - but on those it is how the game shades them, and
    /// it was being thrown away.
    /// </para>
    /// </remarks>
    private static readonly string[] RampParameters = ["lambertfallofframp", "ramp"];

    /// <summary>The slot carrying the lighting ramp, or null.</summary>
    public static MaterialTextureSlot? PickRampSlot(IReadOnlyList<MaterialTextureSlot> slots)
    {
        foreach (string wanted in RampParameters)
        {
            foreach (MaterialTextureSlot slot in slots)
            {
                if (string.Equals(slot.ParameterName, wanted, StringComparison.OrdinalIgnoreCase))
                    return slot;
            }
        }

        return null;
    }

    /// <summary>The slot carrying the normal map, or null.</summary>
    public static MaterialTextureSlot? PickNormalSlot(IReadOnlyList<MaterialTextureSlot> slots)
        => FirstMentioning(slots, NormalNames);

    /// <summary>The slot carrying the colour of the highlight, or null.</summary>
    public static MaterialTextureSlot? PickSpecularSlot(IReadOnlyList<MaterialTextureSlot> slots)
    {
        foreach (MaterialTextureSlot slot in slots)
        {
            if (string.Equals(slot.ParameterName, SpecularColourParameter, StringComparison.OrdinalIgnoreCase))
                return slot;
        }

        return null;
    }

    /// <summary>
    /// The slot carrying the picture the surface reflects, or null.
    /// </summary>
    /// <remarks>
    /// Null is the common and correct answer: most costumes reflect nothing,
    /// and a material that binds no panorama must be left reflecting nothing
    /// rather than handed whichever other map happens to mention the word.
    /// </remarks>
    public static MaterialTextureSlot? PickEnvironmentSlot(IReadOnlyList<MaterialTextureSlot> slots)
    {
        foreach (MaterialTextureSlot slot in slots)
        {
            if (string.Equals(slot.ParameterName, EnvironmentParameter, StringComparison.OrdinalIgnoreCase))
                return slot;
        }

        return null;
    }

    private static MaterialTextureSlot? FirstMentioning(IReadOnlyList<MaterialTextureSlot> slots, string[] words)
    {
        foreach (MaterialTextureSlot slot in slots)
        {
            if (Mentions(slot.ParameterName, words) || Mentions(slot.TextureName, words)) return slot;
        }

        return null;
    }

    private static bool Mentions(string value, string[] words)
    {
        foreach (string word in words)
        {
            if (value.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Turns a reference into an export in this package, or -1.
    /// </summary>
    /// <remarks>
    /// Character packages are cooked to stand alone, so an object they reference
    /// is usually also cooked into them — but the reference can still be written
    /// as an import. Matching an import by name against this package's own
    /// exports recovers those, and returning -1 for the rest is honest: the
    /// object genuinely is not here.
    /// </remarks>
    public static int ResolveExport(Package package, ObjectReference reference)
    {
        if (reference.IsNull) return -1;
        if (reference.IsExport) return reference.ExportIndex;

        string wanted = package.ResolveName(reference);
        if (wanted.Length == 0) return -1;

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (string.Equals(package.GetExportName(i), wanted, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
