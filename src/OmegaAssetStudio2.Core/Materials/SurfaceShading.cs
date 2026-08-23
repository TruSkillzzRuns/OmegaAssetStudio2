namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// One surface's shading, as the material states it.
/// </summary>
/// <remarks>
/// The game's character materials come in two vocabularies. The older one
/// calls its diffuse exponent "diffusepower", offers two specular lobes at
/// once and asks for a fill light by "use_filllight"; the newer one calls the
/// exponent "lambertdiffusepower", has a single lobe whose tightness a mask can
/// raise, and calls the same fill light "usescreenlight". They are the same
/// small set of ideas under different names, so both are read into one set of
/// values here and the viewport has one model to draw rather than two.
/// <para>
/// Everything in here comes from the material. Where the two vocabularies do
/// not agree on a value the older one's is used if the material speaks it, and
/// each such choice is written down beside the property it affects.
/// </para>
/// </remarks>
public sealed record SurfaceShading
{
    /// <summary>The base material this was read from, by name.</summary>
    public required string BaseName { get; init; }

    // ---------------------------------------------------------------- diffuse

    /// <summary>
    /// Whether the light wraps around the surface rather than stopping at the
    /// terminator. The older vocabulary names this outright; the newer one has
    /// no such switch and shapes the same falloff with a curve instead.
    /// </summary>
    public bool HalfLambert { get; init; }

    /// <summary>The exponent the lit amount is raised to.</summary>
    public float DiffusePower { get; init; } = 1f;

    /// <summary>The light that reaches a surface facing away from the key.</summary>
    /// <remarks>
    /// The material's own ambient colour, added to the lit amount before the
    /// key light's colour is applied. Its ambientmult is a separate thing and
    /// lands elsewhere - see <see cref="AmbientMult"/>.
    /// </remarks>
    public MaterialColour Ambient { get; init; } = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// How much of the scene's own ambient this surface takes, beside its fill
    /// light.
    /// </summary>
    /// <remarks>
    /// The two travel together. The game's base pass adds the fill light to
    /// this number, multiplies the sum by the surface colour, and scales the
    /// result by the scene's sky light - which is where a fill amount of 30
    /// stops being thirty times anything.
    /// </remarks>
    public float AmbientMult { get; init; } = 1f;

    // --------------------------------------------------------------- specular

    public bool Specular { get; init; }

    /// <summary>
    /// Whether two highlights are added together - a broad one and a tight one
    /// - rather than one. Only the older vocabulary offers this.
    /// </summary>
    public bool DualSpecular { get; init; }

    /// <summary>
    /// The tightness of the first highlight, at the two ends of whatever the
    /// mask's sharpness channel says. Equal when nothing varies it.
    /// </summary>
    public float SpecularPowerLow { get; init; } = 5f;
    public float SpecularPowerHigh { get; init; } = 6f;

    /// <summary>The tightness of the second highlight, when there are two.</summary>
    public float SecondPowerLow { get; init; } = 70f;
    public float SecondPowerHigh { get; init; } = 80f;

    public float SpecularStrength { get; init; } = 1f;
    public float SecondStrength { get; init; } = 8f;

    /// <summary>Scales both highlights together.</summary>
    public float SpecularTotal { get; init; } = 1f;

    /// <summary>
    /// Whether the highlight is scaled by the surface's own colour, and by how
    /// much. Only the newer vocabulary asks for this.
    /// </summary>
    /// <summary>
    /// How far the surface's own colour is mixed toward its plain brightness
    /// before it scales the highlight.
    /// </summary>
    public float SpecularDesaturate { get; init; } = 0.375f;

    /// <summary>
    /// Whether the highlight takes the surface's own colour instead of the
    /// colour the material states for it.
    /// </summary>
    /// <remarks>
    /// The material's own usediffusemultspec, which is what chooses between two
    /// compiled shapes of the same base: one is handed SpecColorValue and the
    /// other is handed SpecColorDesat and DiffuseSpecMult and no SpecColorValue
    /// at all.
    /// <para>
    /// One costume states a highlight colour of 0.012, 0.302, 1 - a strong blue
    /// - at a strength of 60, and sets this switch, so its shader never reads
    /// that colour. Painting with it anyway turned a black costume vivid cyan
    /// from head to foot.
    /// </para>
    /// </remarks>
    public bool SpecularFromDiffuse { get; init; }

    /// <summary>How much of the surface's colour the highlight then takes.</summary>
    /// <remarks>
    /// 551 of the 579 materials that state it use 2.55; the costume above uses
    /// 55.
    /// </remarks>
    public float SpecularFromDiffuseAmount { get; init; } = 2.55f;

    /// <summary>
    /// Whether this material's compiled form is handed rim numbers of its own.
    /// </summary>
    /// <remarks>
    /// A v2 material lists rimfalloff and rimcolormult among the values its
    /// shader reads, so its rim is the one those two describe. A v1 material
    /// lists neither - its rim is built from constants in the base pass - and
    /// reading its rim and rimstrength as though the shader used them made
    /// every rim four times too strong and the wrong shape.
    /// </remarks>
    public bool RimStated { get; init; }

    /// <summary>The highlight's colour where no colour map is bound.</summary>
    public MaterialColour SpecularColour { get; init; } = new(1f, 1f, 1f, 1f);

    // -------------------------------------------------------------------- rim

    public bool Rim { get; init; }

    /// <summary>Whether the mask says where the rim shows.</summary>
    public bool RimMasked { get; init; }

    /// <summary>Whether the rim takes the surface's own colour.</summary>
    public bool RimFromDiffuse { get; init; }

    public MaterialColour RimColour { get; init; } = new(0f, 0f, 0f, 1f);

    /// <summary>How quickly the rim falls away from the silhouette.</summary>
    public float RimFalloff { get; init; } = 2f;

    /// <summary>How bright the rim is.</summary>
    public float RimStrength { get; init; } = 1f;

    // ------------------------------------------------------------- reflection

    public bool Reflects { get; init; }

    public float ReflectionStrength { get; init; } = 1f;

    /// <summary>
    /// Whether what is reflected is scaled by the surface's own colour. The
    /// newer vocabulary asks for this and the older one does not.
    /// </summary>
    public bool ReflectionFromDiffuse { get; init; }

    // ------------------------------------------------------------------- fill

    /// <summary>
    /// A second light, aimed from a fixed direction the material states, which
    /// most materials leave switched off.
    /// </summary>
    public bool Fill { get; init; }

    public MaterialColour FillColour { get; init; } = new(0f, 0f, 0f, 1f);

    /// <summary>Which way the fill light points, in the material's own axes.</summary>
    public MaterialColour FillDirection { get; init; } = new(0f, 0f, -1f, 1f);

    public float FillStrength { get; init; } = 1f;
    public float FillPower { get; init; } = 1f;

    // ----------------------------------------------------------------- others

    public bool NormalMap { get; init; } = true;
    public float NormalStrength { get; init; } = 1f;

    public bool Emissive { get; init; }
    public float EmissiveStrength { get; init; } = 1f;

    /// <summary>Whether both sides of a surface are drawn.</summary>
    public bool TwoSided { get; init; }

    /// <summary>Whether the material is lit at all.</summary>
    public bool Unlit { get; init; }

    /// <summary>Whether the surface is cut away where its picture is clear.</summary>
    public bool Cutout { get; init; }

    /// <summary>How opaque a point has to be to survive the cut.</summary>
    public float CutoutThreshold { get; init; } = 0.333f;

    /// <summary>What a material with nothing to say would shade as.</summary>
    public static SurfaceShading Plain { get; } = new() { BaseName = string.Empty };
}

/// <summary>Reads a material's settings into one set of shading values.</summary>
public static class SurfaceShadingReader
{
    public static SurfaceShading Read(MaterialSettings settings) => Read(settings, true, null);

    /// <param name="usesSpecularColour">
    /// Whether the material's compiled shader is actually handed the highlight
    /// colour it states. A material states many values and its shader is given
    /// only the ones it uses.
    /// </param>
    /// <param name="given">
    /// The parameters this material's compiled form hands its shaders, where
    /// the shader cache has been read. Kept for callers that have it; the
    /// shading no longer turns any term off on the strength of it.
    /// </param>
    /// <remarks>
    /// Switching terms off where the compiled form named no parameter for them
    /// was wrong, and measurably so. The cache files a plain instance under the
    /// resource its base wrote, so one identity answers for many compiled
    /// permutations - 183 of them under the commonest, spanning materials as
    /// unrelated as ChBaseMaterial and EnvBaseShaderV3 - and the permutation a
    /// material actually uses is chosen by its own static parameter set, not by
    /// that identity.
    /// <para>
    /// The costume the rule was built on states use_filllight = True in its own
    /// static set, so it really is compiled with a fill light, and the rule was
    /// switching off a term the material has. It looked right for the wrong
    /// reason: what makes the fill small is that the game scales it, and the
    /// ambient beside it, by the scene's own sky light rather than applying the
    /// material's amount at full strength.
    /// </para>
    /// </remarks>
    public static SurfaceShading Read(
        MaterialSettings settings, bool usesSpecularColour, IReadOnlySet<string>? given)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Numbers.Count == 0 && settings.Choices.Count == 0) return SurfaceShading.Plain;

        // The newer vocabulary is recognised by a parameter only it declares.
        bool newer = settings.Numbers.ContainsKey("lambertdiffusepower")
                  || settings.Numbers.ContainsKey("rimfalloff");

        float diffusePower = newer
            ? settings.Number("lambertdiffusepower", 1.25f)
            : settings.Number("diffusepower", 1f);

        // Both vocabularies name an ambient colour; only the older one names a
        // separate amount to scale it by, so the newer one's colour is taken as
        // it stands.
        // The colour alone. Folding its ambientmult in here was wrong: the
        // base pass adds this colour to the lit amount inside the block the key
        // light multiplies, and puts ambientmult in the separate block the
        // scene's sky light scales, beside the fill.
        MaterialColour ambient = newer
            ? settings.Colour("lambertambient", new MaterialColour(0f, 0f, 0f, 1f))
            : settings.Colour("ambient", new MaterialColour(0f, 0f, 0f, 1f));

        bool dual = settings.On("usedualspec");

        float power = settings.Number("specularpower", 5f);
        float powerMasked = settings.Number("specularpowermask", power);

        return new SurfaceShading
        {
            BaseName = settings.BaseName,

            HalfLambert = settings.On("usehalflambert"),
            AmbientMult = newer
                ? settings.Number("lightingambient", 1f)
                : settings.Number("ambientmult", 1f),
            DiffusePower = diffusePower,
            Ambient = ambient,

            Specular = settings.On("usespecular") || settings.On("usespec"),
            DualSpecular = dual,

            // The older vocabulary states a range for each lobe and picks
            // along it with the mask; the newer states one tightness and, when
            // its mask switch is on, a second to reach towards.
            // The two ends of the range, put in order rather than taken from
            // their names.
            //
            // The names say minimum and maximum, and across the roster 338 of
            // the 1,413 materials that state both have them the other way
            // round - not one artist's slip but a habit, the same shape over
            // and over: a maximum of 1 beside a minimum of 5, 10, 20 or 25.
            // Read as named, those costumes take an exponent of 1, which is no
            // falloff worth the name: the highlight covers the whole surface
            // and the costume comes out washed white. One agent in a dark suit
            // states 30 and 1 and was coming out pale blue all over.
            //
            // A material with nothing to say at one end - 166 state a maximum
            // of nothing and 27 a minimum of nothing - has the other end stand
            // for both, which is what it already did.
            SpecularPowerLow = newer ? power : Nearer(
                settings.Number("specularpower1min", 5f),
                settings.Number("specularpower1max", 6f)),

            SpecularPowerHigh = newer
                ? (settings.On("usespecpowermask") ? powerMasked : power)
                : Further(
                    settings.Number("specularpower1min", 5f),
                    settings.Number("specularpower1max", 6f)),

            SecondPowerLow = Nearer(
                settings.Number("specularpower2min", 70f),
                settings.Number("specularpower2max", 80f)),

            SecondPowerHigh = Further(
                settings.Number("specularpower2min", 70f),
                settings.Number("specularpower2max", 80f)),

            SpecularStrength = newer
                ? settings.Number("specmult", 1f)
                : settings.Number("specmult1", 1f),
            SecondStrength = settings.Number("specmult2", 8f),
            SpecularTotal = settings.Number("totalspecmult", 1f),

            SpecularDesaturate = settings.Number("speccolordesat", 0.375f),
            SpecularFromDiffuse = settings.On("usediffusemultspec"),
            SpecularFromDiffuseAmount = settings.Number("diffusespecmult", 2.55f),
            // Only where the shader asks for it. One costume states a
            // highlight colour of 0.012, 0.302, 1 - a strong blue - and its
            // compiled shader never reads it, which painted a black costume
            // blue from head to foot.
            SpecularColour = usesSpecularColour
                ? settings.Colour("speccolorvalue", new MaterialColour(1f, 1f, 1f, 1f))
                : new MaterialColour(1f, 1f, 1f, 1f),

            Rim = settings.On("userimlight"),
            RimMasked = settings.On("userimmask") || settings.On("userimtexturemask"),
            RimFromDiffuse = settings.On("usediffuseinrim"),
            RimColour = settings.Colour("rimcolor", new MaterialColour(0f, 0f, 0f, 1f)),

            // The newer vocabulary names its two rim numbers plainly. The
            // older one has five, and which is which is not recorded - so the
            // question is settled by what they do across the roster rather
            // than by what they are called.
            //
            // rimpower is 1 on all 1,160 materials that have it and rimmult is
            // 3 on all 1,160: neither is a control anybody turns, so neither
            // can be what separates one costume's rim from another's. The two
            // that vary are rim, from 1.5 to 50 with 5 in the middle, and
            // rimstrength, from 0 to 5 with 1.75 in the middle. A number
            // ranging to 50 is an exponent and one ranging to 5 is a strength.
            //
            // Taking rimpower as the exponent instead - which reads as the
            // obvious choice from the name alone - makes every rim linear in
            // the viewing angle, so it covers the whole of a curved body
            // rather than its edge. Multiplied by a strength of 1.75 times a
            // rimmult of 3, that washed one costume's armour to white and its cape to
            // pale pink.
            RimFalloff = newer
                ? settings.Number("rimfalloff", 2.15f)
                : settings.Number("rim", 5f),
            RimStrength = newer
                ? settings.Number("rimcolormult", 2.55f)
                : settings.Number("rimstrength", 1.75f),

            // Which vocabulary the material speaks, not which values its
            // compiled form was recorded with. Several compiled permutations
            // share one identity, so that recording cannot separate a v1
            // material's rim from a v2 material's - but the vocabulary can, and
            // the two build their rim differently.
            RimStated = newer,

            Reflects = settings.On("usereflection") || settings.On("alwaysusereflection"),
            ReflectionStrength = settings.Number("reflectionmult", 1f),
            ReflectionFromDiffuse = settings.On("multiplyreflectionbydiffuse"),

            Fill = settings.On("use_filllight") || settings.On("usescreenlight"),
            FillColour = settings.Colour("filllightcolor", new MaterialColour(0f, 0f, 0f, 1f)),
            FillDirection = newer
                ? settings.Colour("screenlight_direction", new MaterialColour(0f, 0f, -1f, 1f))
                : settings.Colour("filllightdirection", new MaterialColour(0f, 0f, -1f, 1f)),
            FillStrength = newer
                ? settings.Number("screenlight_amount", 1f) * settings.Number("screenlight_mult", 1f)
                : settings.Number("filllightamount", 1f),
            FillPower = newer
                ? settings.Number("screenlight_power", 1.25f)
                : settings.Number("filllight_power", 2.5f),

            NormalMap = settings.On("usenormalmap", fallback: true),
            NormalStrength = settings.Number("normalstrength", 1f),

            Emissive = settings.On("useemissive") || settings.On("useemissivespecpow"),
            EmissiveStrength = settings.Number("emissivemultiplier", 1f),

            TwoSided = settings.TwoSided,
            Unlit = settings.Unlit,
            Cutout = settings.Cutout,
            CutoutThreshold = settings.CutoutThreshold,
        };
    }

    /// <summary>
    /// The broader end of a stated range, ignoring an end that says nothing.
    /// </summary>
    private static float Nearer(float one, float other)
    {
        if (one <= 0f) return other;
        if (other <= 0f) return one;

        return MathF.Min(one, other);
    }

    /// <summary>
    /// The tighter end of a stated range, ignoring an end that says nothing.
    /// This is what a surface with nothing to vary it settles at, because an
    /// exponent low enough to spread a highlight over a whole costume is not
    /// something any of these materials asks for on its own.
    /// </summary>
    private static float Further(float one, float other)
    {
        if (one <= 0f) return other;
        if (other <= 0f) return one;

        return MathF.Max(one, other);
    }

    private static MaterialColour Scale(MaterialColour colour, float by) =>
        new(colour.R * by, colour.G * by, colour.B * by, colour.A);
}
