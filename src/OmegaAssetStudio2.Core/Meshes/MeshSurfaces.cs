using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>The picture that covers one of a model's material slots.</summary>
public sealed record MeshSurface
{
    /// <summary>Which of the model's material slots this covers.</summary>
    public required int MaterialIndex { get; init; }

    public required string MaterialName { get; init; }

    /// <summary>The slot the picture came from, for showing what was chosen.</summary>
    public required string ParameterName { get; init; }

    public required string TextureName { get; init; }
    public required TextureImage Image { get; init; }

    /// <summary>
    /// The specular map, where the material binds one. It says how much of the
    /// reflection each part of the surface shows, which is what separates
    /// polished metal from the cloth beside it.
    /// </summary>
    public TextureImage? Specular { get; init; }

    /// <summary>
    /// The normal map, where the material binds one. Nearly all of a costume's
    /// detail lives here rather than in its shape - panel lines, stitching,
    /// muscle - so without it a model reads as smooth plastic however good its
    /// colour is.
    /// </summary>
    public TextureImage? NormalMap { get; init; }

    /// <summary>
    /// The curve this surface is lit along, where its material binds one.
    /// </summary>
    public TextureImage? Ramp { get; init; }

    /// <summary>
    /// The packed mask: how polished each part of the surface is, and how much
    /// of the reflection it shows. Several one-channel maps in one texture.
    /// </summary>
    public TextureImage? Mask { get; init; }

    /// <summary>Which channel of the mask is the specular amount, or -1.</summary>
    public int GlossChannel { get; init; } = -1;

    /// <summary>Which channel of the mask is the reflectivity, or -1.</summary>
    public int ReflectChannel { get; init; } = -1;

    /// <summary>
    /// Which channel says how sharp the shine is, or -1 when the mask has none.
    /// </summary>
    public int SharpnessChannel { get; init; } = -1;

    /// <summary>
    /// Which channel of the mask says where the rim light shows, or -1 when the
    /// mask carries no such channel.
    /// </summary>
    public int RimChannel { get; init; } = -1;

    /// <summary>
    /// The picture the surface reflects, where the material binds one. Without
    /// it a metal costume is drawn from its colour alone, and its colour is
    /// nearly black - which is why chrome armour came out dull.
    /// </summary>
    public TextureImage? Environment { get; init; }

    /// <summary>
    /// True when the stored values are already gamma-encoded. Drawing one of
    /// these as though it were linear washes the colour out, so the viewport
    /// has to be told which it is holding.
    /// </summary>
    public required bool IsSrgb { get; init; }

    /// <summary>
    /// The numbers, colours and on-or-off choices this material shades by,
    /// resolved through its whole chain. These are the game's own values, not
    /// anything decided here.
    /// </summary>
    public MaterialSettings Settings { get; init; } = MaterialSettings.Empty;

    /// <summary>
    /// Whether this material's compiled shader is handed the highlight colour
    /// the material states.
    /// </summary>
    public bool UsesSpecularColour { get; init; } = true;

    /// <summary>
    /// The parameters this material's compiled form hands its shaders, or
    /// nothing when the game folder's shader cache has no compiled form for it.
    /// </summary>
    /// <remarks>
    /// The material's own chain answers for the whole vocabulary whether or not
    /// this material's shader was built with it, so the compiled list is what
    /// separates a term the material runs from one it merely has a value for.
    /// </remarks>
    public IReadOnlySet<string>? Given { get; init; }

    /// <summary>
    /// Whether this surface's materials were read from the build's own
    /// compiled shaders rather than from its properties alone.
    /// </summary>
    /// <remarks>
    /// Says nothing about how the surface is then shaded. Which builds have
    /// their frame assembled from their own base pass is decided separately.
    /// </remarks>
    public bool ReadFromShaders { get; init; }

    /// <summary>
    /// True when the material or its texture had to be fetched from another
    /// package, which is worth showing: it is the difference between a model
    /// that stands alone and one that depends on the rest of the install.
    /// </summary>
    public required bool FromAnotherPackage { get; init; }

    public override string ToString() =>
        $"slot {MaterialIndex}: {MaterialName} → {TextureName} ({Image.Width}x{Image.Height})";
}

/// <summary>
/// Works out which picture covers each part of a model.
/// </summary>
/// <remarks>
/// A model names its material slots; each section says which slot it uses; each
/// material binds a set of textures; and one of those is the surface colour.
/// This walks that whole chain so the viewport can draw a model looking like
/// itself rather than like a grey statue.
/// </remarks>
public static class MeshSurfaceResolver
{
    /// <summary>
    /// Resolves the surface of every material slot a model declares.
    /// </summary>
    /// <param name="reader">
    /// Decodes the pixels. Textures whose pixels live in the shared cache need
    /// it to find the cache files, so it is built for a whole game folder.
    /// </param>
    /// <param name="onSkipped">
    /// Told why a slot has no picture. Silence would leave a half-textured model
    /// with no way to find out which part failed or why.
    /// </param>
    /// <param name="locator">
    /// Follows references that lead out of this package. Without one, a material
    /// the package does not itself contain is reported as missing instead of
    /// being fetched — which is correct, just less useful.
    /// </param>
    /// <summary>
    /// A surface of one colour, for a material that has a colour but no
    /// picture. Everything else about it - its normal map, its mask, what it
    /// reflects - is read as usual, because a material without a colour map may
    /// still bind those.
    /// </summary>
    private static MeshSurface Flat(
        int slot,
        string materialName,
        MaterialColour colour,
        Package materialPackage,
        IReadOnlyList<MaterialTextureSlot> slots,
        TextureReader reader,
        ObjectLocator locator)
    {
        var pixels = new byte[]
        {
            Byte(colour.R), Byte(colour.G), Byte(colour.B), 255,
        };

        MaterialTextureSlot? mask = MaterialTextureReader.PickMaskSlot(slots);
        TextureImage? maskImage = Decode(mask, materialPackage, reader, locator, out int maskChannels);

        return new MeshSurface
        {
            MaterialIndex = slot,
            MaterialName = materialName,
            ParameterName = string.Empty,
            TextureName = "(the colour it was compiled with)",
            Image = new TextureImage(1, 1, pixels),

            // Written as light already, not as a picture to be converted.
            IsSrgb = false,

            Specular = Decode(MaterialTextureReader.PickSpecularSlot(slots), materialPackage, reader, locator),
            NormalMap = Decode(MaterialTextureReader.PickNormalSlot(slots), materialPackage, reader, locator),
            Ramp = Decode(MaterialTextureReader.PickRampSlot(slots), materialPackage, reader, locator),
            Mask = maskImage,
            GlossChannel = MaterialTextureReader.GlossChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
            ReflectChannel = MaterialTextureReader.ReflectChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
            SharpnessChannel = MaterialTextureReader.SharpnessChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
            RimChannel = MaterialTextureReader.RimChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
            Environment = Decode(MaterialTextureReader.PickEnvironmentSlot(slots), materialPackage, reader, locator),
            FromAnotherPackage = false,
        };
    }

    private static byte Byte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary>
    /// Decodes one of a material's other maps, or null when it binds none.
    /// A missing specular or reflection is normal - most surfaces are not
    /// metal - so it is never reported as a fault.
    /// </summary>
    /// <summary>
    /// A material's settings with a cut it does not make taken back off.
    /// </summary>
    /// <remarks>
    /// A base material's BlendMode is what it was authored as; whether its
    /// shaders were built to cut holes is in the compiled form, and the two
    /// disagree on 8 of the 101 base materials the listed models use - 367 of
    /// their slots, 329 of them one material.
    /// <para>
    /// One armoured costume is such a case: it resolves to ChBaseMaterial_v2-1,
    /// whose base pass carries only the one texkill every shader has for the
    /// screen-door fade, while ChBaseMaterial_v2-1_masked beside it in the
    /// cache carries a second. Reading the BlendMode instead cut his cape away
    /// entirely, because 38 per cent of that costume's colour map has an alpha
    /// of nothing.
    /// </para>
    /// <para>
    /// Only ever takes a cut away, never adds one, and only where the cache
    /// has been read - so a material that already draws whole keeps drawing
    /// whole and a folder with no cache is untouched.
    /// </para>
    /// <para>
    /// And only for the one build. Every install cuts by the same reading
    /// until it is asked for; this one was asked for against 1.53.0.203 and
    /// nothing else is to move because of it.
    /// </para>
    /// </remarks>
    private static MaterialSettings Cutting(MaterialSettings settings, string? colours)
    {
        if (colours is null || !settings.Cutout) return settings;

        if (!ReadsItsOwn(colours)) return settings;

        return CompiledCuts.Cuts(colours, settings.BaseName)
            ? settings
            : settings with { Cutout = false };
    }

    /// <summary>
    /// The builds whose materials are read from their own compiled shaders.
    /// </summary>
    /// <remarks>
    /// Each measured against its own install before being listed. In
    /// 1.48.0.1712: 8 of its 88 base materials cut holes their compiled base
    /// pass does not, across 33 slots; naming a colour map by its own name
    /// finds 1,691 of 1,982 and no normal, curve or panorama; and 5 materials
    /// are drawn from their low-quality picture when they bind a full one
    /// beside it.
    /// </remarks>
    private static readonly string[] CutsReadFrom = ["1.53.0.203", "1.48.0.1712", "1.52.0.1700"];

    /// <summary>Whether this folder's build is one of them.</summary>
    private static bool ReadsItsOwn(string? cookedPath)
    {
        if (cookedPath is null) return false;

        string build = GameClientLocator.BuildBesideCooked(cookedPath);

        foreach (string wanted in CutsReadFrom)
        {
            if (GameClient.Reads(build, wanted)) return true;
        }

        return false;
    }

    /// <summary>The compiled slots standing for one role.</summary>
    private static IEnumerable<MaterialTextureSlot> Named(
        IReadOnlyList<MaterialTextureSlot> slots, string role)
    {
        foreach (MaterialTextureSlot slot in slots)
        {
            if (slot.ParameterName.Equals(role, StringComparison.Ordinal)) yield return slot;
        }
    }

    private static TextureImage? Decode(
        MaterialTextureSlot? slot,
        Package materialPackage,
        TextureReader reader,
        ObjectLocator locator)
        => Decode(slot, materialPackage, reader, locator, out _);

    /// <summary>
    /// As above, and also reports how many channels the texture carries. A
    /// material can name more channels than its texture has, so the reader of a
    /// packed mask needs to know which of the names can actually be there.
    /// </summary>
    private static TextureImage? Decode(
        MaterialTextureSlot? slot,
        Package materialPackage,
        TextureReader reader,
        ObjectLocator locator,
        out int channelCount)
    {
        channelCount = 4;

        if (slot is null) return null;

        LocatedObject? located = locator.TryLocate(slot.Source ?? materialPackage, slot.Texture);
        if (located is null) return null;

        TextureInfo? info = TextureInfo.TryRead(located.Value.Package, located.Value.ExportIndex);
        if (info is null) return null;

        channelCount = info.Format.ChannelCount();

        return reader.TryDecode(located.Value.Package, info);
    }

    // A channel holding one value everywhere used to be discarded here, on the
    // grounds that a fill is not a map. That was decided while a surface with
    // no panorama still reflected an invented flat grey, so a fill showed up as
    // a slab; the invention is long gone and the rule outlived it.
    //
    // It was wrong, and expensively so. Of the 1,072 packed masks the listed
    // models use, 475 hold one value throughout their rim channel, and those
    // values sit between 48 and 95 of 255 - a fifth to a third. Discarding them
    // left the rim light at its full strength instead, three to five times what
    // those materials ask for, and painted a pale blue wash over every costume
    // that has one. A constant is an instruction, and it is followed.

    public static IReadOnlyList<MeshSurface> Resolve(
        Package package,
        SkeletalMesh mesh,
        TextureReader reader,
        Action<int, string>? onSkipped = null,
        ObjectLocator? locator = null,
        string? colours = null)
        => Resolve(package, mesh.Materials, reader, onSkipped, locator, colours);

    /// <summary>
    /// As above, for anything that names materials rather than only a skeletal
    /// model - a prop, or a piece hung on a costume.
    /// </summary>
    /// <param name="colours">
    /// The game folder, so a material that carries no colour of its own can be
    /// looked up in the shader cache. Left out, such a material stays unpainted
    /// exactly as before.
    /// </param>
    public static IReadOnlyList<MeshSurface> Resolve(
        Package package,
        IReadOnlyList<ObjectReference> named,
        TextureReader reader,
        Action<int, string>? onSkipped = null,
        ObjectLocator? locator = null,
        string? colours = null)
    {
        locator ??= new ObjectLocator();

        var surfaces = new List<MeshSurface>();

        for (int slot = 0; slot < named.Count; slot++)
        {
            ObjectReference reference = named[slot];
            string materialName = package.ResolveName(reference);

            if (reference.IsNull)
            {
                onSkipped?.Invoke(slot, "the model leaves this slot empty");
                continue;
            }

            LocatedObject? material = locator.TryLocate(package, reference);
            if (material is null)
            {
                onSkipped?.Invoke(slot, locator.CanFollowAcrossPackages
                    ? $"the material '{materialName}' is not in this game folder"
                    : $"the material '{materialName}' is in another package");
                continue;
            }

            Package materialPackage = material.Value.Package;

            IReadOnlyList<MaterialTextureSlot> slots =
                MaterialTextureReader.ReadSlots(materialPackage, material.Value.ExportIndex, locator);

            // What the material's compiled form samples, for the roles its
            // parameters name nothing for. Cooking strips the node graph, so a
            // texture bound through a plain node rather than a named parameter
            // is invisible to every parameter there is - and one costume's
            // highlight colour is bound that way, which left its black armour
            // shining white and reading as grey.
            bool traced = ReadsItsOwn(colours);

            IReadOnlyList<MaterialTextureSlot> sampled = traced
                ? CompiledTextures.Slots(materialPackage, material.Value.ExportIndex)
                : [];

            if (sampled.Count > 0)
            {
                var filled = new List<MaterialTextureSlot>(slots);

                if (MaterialTextureReader.PickSpecularSlot(slots) is null)
                    filled.AddRange(Named(sampled, CompiledTextures.SpecularParameter));

                if (MaterialTextureReader.PickNormalSlot(slots) is null)
                    filled.AddRange(Named(sampled, CompiledTextures.NormalParameter));

                slots = filled;
            }

            MaterialTextureSlot? chosen = MaterialTextureReader.PickSurfaceSlot(slots, traced);

            // A material that names no picture may still sample one. Cooking
            // strips the node graph, so a material that bound its texture
            // through a plain node rather than a named parameter is left with
            // no slot for anything to read - Black Bolt's default costume is
            // one, and came out as a grey figure. The compiled resource still
            // lists every texture its shaders sample.
            if (chosen is null)
            {
                foreach (MaterialTextureSlot one in sampled)
                {
                    if (!one.ParameterName.Equals(CompiledTextures.ColourParameter, StringComparison.Ordinal))
                        continue;

                    chosen = one;
                    slots = [.. slots, .. sampled];
                    break;
                }
            }

            if (chosen is null)
            {
                // A material that binds no texture may still have been compiled
                // with a colour. Cooking strips a material's node graph, so one
                // whose colour was a constant in that graph is left with
                // nothing to show - but the constant survives in the compiled
                // shader, and the shader cache files it under the same identity
                // the material writes in its own resource.
                //
                // One costume's string of lights is four such materials. Without this
                // they are bare grey geometry.
                if (colours is not null
                    && ShaderColours.TryFind(colours, materialPackage, material.Value.ExportIndex,
                                             out MaterialColour compiled))
                {
                    surfaces.Add(Flat(slot, materialName, compiled, materialPackage, slots, reader, locator));
                    continue;
                }

                onSkipped?.Invoke(slot, $"the material '{materialName}' binds no texture");
                continue;
            }

            // The texture is numbered against whichever package declared the
            // slot, which after inheritance need not be the material's own.
            LocatedObject? texture = locator.TryLocate(chosen.Source ?? materialPackage, chosen.Texture);
            if (texture is null)
            {
                onSkipped?.Invoke(slot, $"the texture '{chosen.TextureName}' could not be found");
                continue;
            }

            Package texturePackage = texture.Value.Package;

            TextureInfo? info = TextureInfo.TryRead(texturePackage, texture.Value.ExportIndex);
            if (info is null)
            {
                onSkipped?.Invoke(slot, $"'{chosen.TextureName}' did not read as a texture");
                continue;
            }

            TextureImage? image = reader.TryDecode(texturePackage, info);
            if (image is null)
            {
                onSkipped?.Invoke(slot, $"'{chosen.TextureName}' could not be decoded");
                continue;
            }

            MaterialTextureSlot? mask = MaterialTextureReader.PickMaskSlot(slots);
            TextureImage? maskImage = Decode(mask, materialPackage, reader, locator, out int maskChannels);

            surfaces.Add(new MeshSurface
            {
                Specular = Decode(MaterialTextureReader.PickSpecularSlot(slots), materialPackage, reader, locator),
                NormalMap = Decode(MaterialTextureReader.PickNormalSlot(slots), materialPackage, reader, locator),
                Ramp = Decode(MaterialTextureReader.PickRampSlot(slots), materialPackage, reader, locator),
                Mask = maskImage,
                GlossChannel = MaterialTextureReader.GlossChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
                ReflectChannel = MaterialTextureReader.ReflectChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
                SharpnessChannel = MaterialTextureReader.SharpnessChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
                RimChannel = MaterialTextureReader.RimChannelFor(mask?.ParameterName ?? string.Empty, maskChannels),
                Environment = Decode(MaterialTextureReader.PickEnvironmentSlot(slots), materialPackage, reader, locator),
                Settings = Cutting(
                    MaterialSettingsReader.Read(materialPackage, material.Value.ExportIndex, locator), colours),
                // Left as stated. Whether a shader is handed this colour turns
                // out to depend on which lit variant is compiled - the plainest
                // one is not given it, the fully lit one is - so the binding
                // does not decide whether the material uses it.
                UsesSpecularColour = true,
                ReadFromShaders = traced,
                Given = colours is null
                    ? null
                    : ShaderColours.Given(colours, materialPackage, material.Value.ExportIndex),
                MaterialIndex = slot,
                MaterialName = materialName,
                ParameterName = chosen.ParameterName,
                TextureName = chosen.TextureName,
                Image = image,
                IsSrgb = info.IsSrgb,
                FromAnotherPackage = material.Value.CameFromElsewhere || texture.Value.CameFromElsewhere,
            });
        }

        return surfaces;
    }
}
