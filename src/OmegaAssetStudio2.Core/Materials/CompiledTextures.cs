using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// The textures a material's compiled form actually samples, whether or not any
/// parameter names them.
/// </summary>
/// <remarks>
/// Cooking strips a material's node graph. A material that bound its picture
/// through a plain texture node rather than a named parameter is left with no
/// slot for anything to read, and shows as bare geometry - Black Bolt's default
/// costume is one material of this kind and came out as a grey figure.
/// <para>
/// The compiled resource still lists every texture its shaders sample, in the
/// order the shaders were built to read them, and that list is the only place
/// left that says what the material shows.
/// </para>
/// </remarks>
public static class CompiledTextures
{
    /// <summary>
    /// Every texture this material's compiled form samples, or an empty list
    /// when its resource will not read.
    /// </summary>
    public static IReadOnlyList<ObjectReference> Read(Package package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);

        var found = new List<ObjectReference>();

        PropertyBag? bag = package.TryReadProperties(exportIndex);
        if (bag is null) return found;

        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
        int at = bag.PayloadOffset;

        // An instance writes which quality levels it compiled before its
        // resource; a base material writes the resource straight away.
        if (bag.GetBool("bHasStaticPermutationResource")) at += 4;

        try
        {
            int errors = Int(data, ref at);
            for (int e = 0; e < errors; e++)
            {
                int count = Int(data, ref at);
                at += count >= 0 ? count : -count * 2;
            }

            int dependencies = Int(data, ref at);
            at += dependencies * 8;

            at += 4;                                     // the longest chain
            at += 16;                                    // what the cache files it under
            at += 4;                                     // how many coordinate sets

            int textures = Int(data, ref at);
            if (textures < 0 || textures > 256) return found;

            for (int t = 0; t < textures; t++) found.Add(new ObjectReference(Int(data, ref at)));
        }
        catch (InvalidOperationException)
        {
            found.Clear();
        }

        return found;
    }

    /// <summary>The name this reader gives a compiled colour map.</summary>
    public const string ColourParameter = "compiledcolour";

    /// <summary>The name this reader gives a compiled normal map.</summary>
    public const string NormalParameter = "compilednormal";

    /// <summary>
    /// The name this reader gives a compiled highlight colour, which is the
    /// name the materials that do bind one through a parameter use.
    /// </summary>
    public const string SpecularParameter = "speccolortex";

    /// <summary>
    /// The textures a material's compiled form samples, as slots, for the two
    /// roles a texture's own name settles.
    /// </summary>
    /// <remarks>
    /// Measured against every slot whose material does name its textures through
    /// a parameter, in the 1.53.0.203 client. Reading the last part of a
    /// texture's name and asking whether it mentions a colour - diff, dif,
    /// diffuse, diffnew, diffusered, furalphadiff and the rest of a long tail,
    /// or albedo - names 1,912 of the 2,206 colour maps and none at all of the
    /// 2,112 normals, 1,623 curves or 2,070 panoramas. Asking whether it
    /// mentions a normal - norm, nrm, nrml, bump, nm - names 2,084 of the 2,112
    /// normal maps and 5 of the 2,206 colour maps. Asking whether it ends in
    /// speccolor names 633 highlight colours and 2 masks and nothing else at
    /// all.
    /// <para>
    /// Nothing else is filled in from a name. Which channel of a packed mask
    /// means what is written in the parameter's name, not the texture's, and a
    /// material with no parameter has not said.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<MaterialTextureSlot> Slots(Package package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);

        var slots = new List<MaterialTextureSlot>();

        foreach (ObjectReference reference in Read(package, exportIndex))
        {
            if (reference.IsNull) continue;

            string named = package.ResolveName(reference);

            string? role = RoleOf(named);
            if (role is null) continue;

            slots.Add(new MaterialTextureSlot
            {
                ParameterName = role,
                Texture = reference,
                TextureName = named,
            });
        }

        // A material that samples exactly one texture shows that texture.
        //
        // There is nothing else it could be showing, whatever its name says -
        // and several props are in this position: a hunting knife whose only
        // texture is knife_hunting_a_tex, a whip whose only texture is a
        // lightning strip. An ending of tex is used by no parameter-named slot
        // in the game, so the name cannot settle it and the count does.
        //
        // A lone normal map is still left alone: drawing one as if it were
        // colour looks worse than drawing nothing.
        if (slots.Count == 0)
        {
            IReadOnlyList<ObjectReference> sampled = Read(package, exportIndex);

            if (sampled.Count == 1 && !sampled[0].IsNull)
            {
                string only = package.ResolveName(sampled[0]);

                if (RoleOf(only) is null)
                {
                    slots.Add(new MaterialTextureSlot
                    {
                        ParameterName = ColourParameter,
                        Texture = sampled[0],
                        TextureName = only,
                    });
                }
            }
        }

        return slots;
    }

    /// <summary>What a texture's own name says it is for, or nothing.</summary>
    private static string? RoleOf(string named)
    {
        int cut = named.LastIndexOf('_');
        if (cut < 0) return null;

        string ending = named[(cut + 1)..].ToLowerInvariant();

        // Checked before the colour, because a highlight colour's name ends in
        // speccolor and mentions no colour map at all - but the two are worth
        // keeping in a fixed order rather than relying on that.
        if (ending.Contains("speccolor", StringComparison.Ordinal)) return SpecularParameter;

        if (ending.Contains("diff", StringComparison.Ordinal) || ending == "dif" || ending == "albedo")
            return ColourParameter;

        if (ending.Contains("norm", StringComparison.Ordinal)
         || ending.Contains("nrm", StringComparison.Ordinal)
         || ending.Contains("nrml", StringComparison.Ordinal)
         || ending.Contains("bump", StringComparison.Ordinal)
         || ending == "nm")
        {
            return NormalParameter;
        }

        return null;
    }

    private static int Int(ReadOnlySpan<byte> data, ref int at)
    {
        if (at < 0 || at + 4 > data.Length) throw new InvalidOperationException("past the end");

        int value = System.BitConverter.ToInt32(data.Slice(at, 4));
        at += 4;
        return value;
    }
}
