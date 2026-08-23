namespace OmegaAssetStudio2.App.Rendering;

/// <summary>
/// The shaders the viewport draws with, compiled at run time.
/// </summary>
/// <remarks>
/// Kept as source rather than compiled bytecode so there is no build step that
/// can silently go stale, and so the lighting can be read alongside the code
/// that sets it up.
/// <para>
/// The model is drawn in its own space with a single light that follows the
/// camera, which is what makes a model readable from any angle without needing
/// a scene set up around it. A second, dimmer light from below keeps the
/// underside from going flat black.
/// </para>
/// </remarks>
internal static class ModelShaders
{
    public const string VertexEntryPoint = "VertexMain";
    public const string PixelEntryPoint = "PixelMain";
    public const string VertexProfile = "vs_4_0";
    public const string PixelProfile = "ps_4_0";

    /// <summary>
    /// The room the model stands in: a backdrop behind it and a lit patch of
    /// ground under it.
    /// </summary>
    /// <remarks>
    /// Both are drawn with this one shader, told apart by <c>Mode</c>, because
    /// they want the same handful of constants and the same vertex format.
    /// <para>
    /// The first attempt drew the ground as bare lines fading to the background
    /// colour. It was technically present and visually nothing: a few grey
    /// strokes on black, which is not a floor. What makes a model look like it
    /// is standing somewhere is the ground being lighter than the void behind
    /// it and darker directly beneath the feet — so the patch is lit, the grid
    /// is drawn on top of it, and there is a soft shadow where the model meets
    /// it.
    /// </para>
    /// <para>
    /// The grid is worked out in the pixel shader from the world position, so
    /// its lines stay one pixel wide however close the camera gets — geometry
    /// lines break up into stipple at a distance and go fat close up.
    /// </para>
    /// </remarks>
    public const string SceneSource = """
        cbuffer Scene : register(b0)
        {
            float4x4 WorldViewProjection;
            float4   LineColour;
            float4   Background;
            float3   Centre;
            float    Reach;
            float    Step;
            float    Mode;
            float    Style;
            float    Pad1;

            float3   Eye;
            float    Height;

            float    Time;
            float3   Pad2;
        };

        // Smooth noise, built from a hash rather than a texture so the beam
        // carries nothing with it. What the shapes without lines are made of.
        float Speck(float3 at)
        {
            return frac(sin(dot(at, float3(12.9898, 78.233, 37.719))) * 43758.5453);
        }

        float Cloud(float3 at)
        {
            float3 whole = floor(at);
            float3 part = frac(at);

            // Eased, so the cells blend instead of showing their edges.
            part = part * part * (3.0 - (2.0 * part));

            float a = lerp(Speck(whole + float3(0, 0, 0)), Speck(whole + float3(1, 0, 0)), part.x);
            float b = lerp(Speck(whole + float3(0, 1, 0)), Speck(whole + float3(1, 1, 0)), part.x);
            float c = lerp(Speck(whole + float3(0, 0, 1)), Speck(whole + float3(1, 0, 1)), part.x);
            float d = lerp(Speck(whole + float3(0, 1, 1)), Speck(whole + float3(1, 1, 1)), part.x);

            return lerp(lerp(a, b, part.y), lerp(c, d, part.y), part.z);
        }

        struct VertexIn  { float3 Position : POSITION; };
        struct VertexOut { float4 Position : SV_POSITION; float3 World : TEXCOORD0; };

        VertexOut VertexMain(VertexIn input)
        {
            VertexOut output;

            // The backdrop arrives already in screen space and is pinned just
            // short of the far plane, so it sits behind everything without
            // needing the depth test turned off and back on.
            output.Position = Mode < 0.5
                ? float4(input.Position.xy, 0.99999, 1.0)
                : mul(float4(input.Position, 1.0), WorldViewProjection);

            output.World = input.Position;
            return output;
        }

        float4 PixelMain(VertexOut input) : SV_TARGET
        {
            if (Mode < 0.5)
            {
                // A gentle vertical gradient. A flat colour reads as no
                // background at all; a gradient gives the model something to
                // sit against and separates its top from its feet.
                float height = saturate((input.World.y * 0.5) + 0.5);
                float3 low   = Background.rgb * 0.72;
                float3 high  = (Background.rgb * 1.55) + 0.015;

                return float4(pow(saturate(lerp(low, high, height * height)), 1.0 / 2.2), 1.0);
            }

            // The beam the stand throws up around what is standing on it.
            //
            // A sleeve of light rather than a solid one: brightest where it
            // leaves the pad and gone by the top, and brightest around the
            // silhouette, because a column of haze is thickest where you are
            // looking through the most of it. Drawn added rather than mixed, so
            // it lights what is behind it instead of hiding it.
            if (Mode > 1.5)
            {
                // The dome closes to a point, and every vertex of its top ring
                // sits exactly on the centre line - so this offset is exactly
                // zero at the apex, and normalizing it there gives NaN, which
                // the adding blend then writes into the picture. Held away from
                // the axis rather than normalized blind.
                float2 across = input.World.xy - Centre.xy;
                float  span = length(across);

                float3 outward = span > 1e-5
                    ? float3(across / span, 0.0)
                    : float3(1.0, 0.0, 0.0);
                float3 toEye = normalize(Eye - input.World);

                // Edge-on where the sleeve turns away from the viewer, and
                // never quite nothing where it faces you: a beam you can only
                // see the rim of reads as two stripes rather than a volume.
                float edge = 1.0 - saturate(abs(dot(outward, toEye)));
                edge = (edge * edge * 0.9) + 0.1;

                float up = saturate((input.World.z - Centre.z) / max(Height, 0.001));

                // Thinning as it rises, and thinning again right at the pad so
                // it does not end in a hard collar. Gently, so it still has
                // body by the time it passes the model's head.
                float along = saturate(1.0 - (up * 0.92)) * saturate(up * 6.0);

                float breath = 0.86 + (0.14 * sin(Time * 0.9));

                // How far round the beam this point sits, for anything drawn
                // as uprights.
                float round = atan2(input.World.y - Centre.y, input.World.x - Centre.x);

                float climbed = (input.World.z - Centre.z) * 0.35;

                // What each of them does with all that.
                float pattern = 1.0;

                if (Style < 1.5)
                {
                    // Beam: soft bands drifting upward.
                    pattern = 0.72 + (0.28 * sin(climbed - (Time * 2.1)));
                }
                else if (Style < 2.5)
                {
                    // Rings: the same bands cut hard, so they read as separate
                    // rings climbing rather than a gradient.
                    float wave = frac((climbed - (Time * 1.6)) / 6.2831853);
                    pattern = saturate(1.0 - abs((wave - 0.5) * 7.0)) + 0.28;
                }
                else if (Style < 3.5)
                {
                    // Scan: one band running the whole height and starting
                    // again, with the rest left faint behind it.
                    float run = frac(Time * 0.35);
                    pattern = saturate(1.0 - abs((up - run) * 9.0));
                    pattern = (pattern * 1.6) + 0.22;
                }
                else if (Style < 4.5)
                {
                    // Cage: uprights around the beam and cross-pieces up it,
                    // which reads as something held rather than projected.
                    float posts = saturate(1.0 - abs(frac(round * 3.8197186) - 0.5) * 5.0);
                    float rungs = saturate(1.0 - abs(frac(climbed * 0.5 - (Time * 0.4)) - 0.5) * 6.0);
                    pattern = saturate(posts + rungs) + 0.18;
                }
                else if (Style < 5.5)
                {
                    // Column: even from foot to head, with the faintest drift
                    // through it - a pillar of light rather than a projection.
                    pattern = 0.82 + (0.18 * sin(climbed - (Time * 0.8)));
                }
                else if (Style < 6.5)
                {
                    // Spiral: one line winding up and round, turning slowly.
                    float wound = frac(((round * 1.6) + climbed - (Time * 1.1)) / 6.2831853);
                    pattern = saturate(1.0 - abs((wound - 0.5) * 6.0)) + 0.2;
                }
                else if (Style < 7.5)
                {
                    // Lattice: two sets of lines crossing the sleeve, which
                    // reads as a net drawn in light.
                    float one = abs(frac(((round * 2.4) + climbed - (Time * 0.5))) - 0.5);
                    float two = abs(frac(((round * 2.4) - climbed + (Time * 0.5))) - 0.5);
                    float net = saturate(1.0 - (min(one, two) * 8.0));
                    pattern = net + 0.16;
                }
                else if (Style < 8.5)
                {
                    // Motes: specks carried upward, each starting again at the
                    // pad when it reaches the top.
                    float lane = floor(round * 7.0);
                    float seed = frac(sin(lane * 91.37) * 43758.5453);
                    float risen = frac((Time * 0.22) + seed);
                    float mote = saturate(1.0 - abs((up - risen) * 26.0));
                    float side = saturate(1.0 - abs(frac(round * 7.0) - 0.5) * 4.0);
                    pattern = (mote * side * 2.2) + 0.12;
                }
                else if (Style < 9.5)
                {
                    // Glitch: the height cut into slices, a few of them thrown
                    // bright for a moment and the rest left low.
                    float slice = floor(up * 26.0);
                    float when = floor(Time * 7.0);
                    float roll = frac(sin((slice * 12.9898) + (when * 78.233)) * 43758.5453);
                    pattern = roll > 0.86 ? 1.9 : 0.24;
                }
                else if (Style < 10.5)
                {
                    // Plasma: cloud drifting through the beam and folding over
                    // itself. No edges anywhere in it.
                    float3 place = input.World * 0.035;

                    float rolling =
                        (Cloud(place + float3(0.0, 0.0, Time * 0.35)) * 0.6)
                      + (Cloud((place * 2.3) - float3(0.0, 0.0, Time * 0.21)) * 0.3)
                      + (Cloud(place * 4.7) * 0.1);

                    pattern = (rolling * rolling * 2.6) + 0.14;
                }
                else if (Style < 11.5)
                {
                    // Embers: sparks scattered through the beam, each carried up
                    // at its own pace and going out as it rises.
                    float3 place = input.World * 0.11;
                    place.z -= Time * 1.4;

                    float grain = Cloud(place);
                    float spark = saturate((grain - 0.74) * 7.0);

                    // Every one of them flickering on its own count.
                    float flicker = 0.55 + (0.45 * sin((grain * 90.0) + (Time * 9.0)));

                    pattern = (spark * flicker * 4.5) + 0.1;
                }
                else if (Style < 12.5)
                {
                    // Dissolve: the beam breaking apart and coming back, as
                    // though it were still working out what it is showing.
                    float turn = (sin(Time * 0.42) * 0.5) + 0.5;
                    float grain = Cloud(input.World * 0.09);

                    // Softly, so it thins rather than switching off.
                    pattern = saturate((grain - (turn * 0.85)) * 4.5) + 0.12;
                }
                else
                {
                    // Dome: no pattern of its own. What it is, is its shape -
                    // a shell over the model rather than a beam under it - and
                    // a haze that gathers where the shell turns away.
                    float3 place = input.World * 0.05;
                    pattern = 0.5 + (Cloud(place + float3(0.0, 0.0, Time * 0.15)) * 0.7);
                }

                float3 projected = float3(0.176, 0.702, 1.0);
                float strength = edge * along * pattern * breath;

                return float4(pow(saturate(projected * strength * 1.85), 1.0 / 2.2), saturate(strength * 0.95));
            }

            float2 fromCentre = input.World.xy - Centre.xy;
            float  away = length(fromCentre) / max(Reach, 0.001);

            // Rounded off rather than cut off, so the ground has no visible
            // edge to give away that it is a square.
            float on = saturate(1.0 - (away * away));

            // Lines a pixel wide whatever the distance: the fractional part of
            // the position in grid steps, divided by how fast that fraction is
            // changing across this pixel.
            float2 grid = abs(frac(fromCentre / Step) - 0.5);
            float2 width = max(fwidth(fromCentre / Step), 0.00001);

            // Not named "line" - that is a reserved word, and calling it that
            // stopped this whole shader compiling. The floor then silently did
            // not draw at all, which looked exactly like having built nothing.
            float rule = 1.0 - saturate(min(grid.x / width.x, grid.y / width.y) - 0.5);

            float3 ground = (Background.rgb * 1.9) + 0.012;
            ground = lerp(ground, LineColour.rgb, rule * 0.75);

            // Where the model meets the ground. Nothing here casts a real
            // shadow, but the eye reads a darkening under the feet as contact,
            // and without it a model appears to hover.
            float contact = saturate(1.0 - (length(fromCentre) / (Reach * 0.42)));
            ground *= lerp(1.0, 0.34, contact * contact);

            return float4(pow(saturate(lerp(Background.rgb, ground, on)), 1.0 / 2.2), 1.0);
        }
        """;

    public const string Source = """
        cbuffer Frame : register(b0)
        {
            float4x4 WorldViewProjection;
            float4x4 World;
            float3   CameraDirection;
            float    Pad0;
            float3   BaseColour;
            float    HasTexture;
            float    HasSpecular;
            float    HasEnvironment;
            float    HasNormalMap;
            float    HasSpecularColour;
            float    HasRamp;
            float    EnvironmentLevels;
            float    Pad4;
            float4   GlossSelect;
            float4   ReflectSelect;
            float4   SharpSelect;
            float4   RimSelect;

            float3   AmbientColour;
            float    DiffusePower;
            float3   RimColour;
            float    RimFalloff;
            float3   FillColour;
            float    RimStrength;
            float3   FillDirection;
            float    FillStrength;
            float3   SpecularColour;
            float    FillPower;

            float    UseHalfLambert;
            float    UseSpecular;
            float    UseDualSpecular;
            float    UseRim;

            float    RimFromDiffuse;
            float    UseReflection;
            float    ReflectionStrength;
            float    ReflectionFromDiffuse;

            float    SpecularPowerLow;
            float    SpecularPowerHigh;
            float    SecondPowerLow;
            float    SecondPowerHigh;

            float    SpecularStrength;
            float    SecondStrength;
            float    SpecularTotal;
            float    SpecularDesaturate;

            float    Pad9;
            float    UseFill;
            float    NormalStrength;
            float    Cutout;

            float    CutoutThreshold;
            float    WrapLight;
            float    ClampCurve;
            float    Unlit;

            float    RimStated;
            float    AmbientMult;
            float    Traced;
            float    SpecularFromDiffuse;

            float    SpecularFromDiffuseAmount;
            float    Pad12;
            float    Pad14;
            float    Pad15;

            float3   SkyColour;
            float    Pad13;

            float4   HologramAt;
            float4   Hologram;
        };

        Texture2D    Surface     : register(t0);
        Texture2D    SurfaceMask : register(t1);
        Texture2D    Reflected   : register(t2);
        Texture2D    NormalMap   : register(t3);
        Texture2D    SpecColour  : register(t4);
        Texture2D    Ramp        : register(t5);
        SamplerState Sampling    : register(s0);

        struct VertexIn
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
            float2 Uv       : TEXCOORD0;
            float4 Tangent  : TANGENT;
        };

        struct VertexOut
        {
            float4 Position : SV_POSITION;
            float3 Normal   : NORMAL;
            float2 Uv       : TEXCOORD0;
            float4 Tangent  : TANGENT;
            float3 Place    : TEXCOORD1;
        };

        VertexOut VertexMain(VertexIn input)
        {
            VertexOut output;
            output.Position = mul(float4(input.Position, 1.0), WorldViewProjection);
            output.Normal   = normalize(mul(float4(input.Normal, 0.0), World).xyz);
            output.Uv       = input.Uv;
            output.Tangent  = float4(normalize(mul(float4(input.Tangent.xyz, 0.0), World).xyz), input.Tangent.w);

            // Where in the world this corner ended up, for the light the stand
            // throws up around whatever is standing on it.
            output.Place    = mul(float4(input.Position, 1.0), World).xyz;
            return output;
        }

        float4 PixelMain(VertexOut input) : SV_TARGET
        {
            float3 normal = normalize(input.Normal);

            // The detail. Nearly everything a costume shows up close - panel
            // lines, stitching, the shape of muscle - is written into the
            // normal map rather than built into the mesh.
            //
            // Whether it is used at all is the material's own choice: 357 of
            // the slots on listed models have usenormalmap switched off, and
            // how strongly is its normalstrength.
            if (HasNormalMap > 0.5)
            {
                float3 tangent = normalize(input.Tangent.xyz - (normal * dot(normal, input.Tangent.xyz)));
                float3 bitangent = cross(normal, tangent) * input.Tangent.w;

                float3 sampled = NormalMap.Sample(Sampling, input.Uv).rgb;

                // Two channels carry the direction and the third is rebuilt
                // from them, which is how these maps are stored - reading the
                // third as given makes a flat surface look dented.
                float2 xy = ((sampled.rg * 2.0) - 1.0) * NormalStrength;
                float  z  = sqrt(saturate(1.0 - saturate(dot(xy, xy))));

                normal = normalize((tangent * xy.x) + (bitangent * xy.y) + (normal * z));
            }

            // The key light, read out of the game's own levels rather than
            // chosen by eye. Two built levels light along (-0.357, 0.234,
            // -0.904) and (-0.412, 0.410, -0.814) - steeply down and to one
            // side - in a warm white of 255/250/229 and 255/239/240.
            //
            // Its direction and colour are used exactly. Its brightness is not:
            // the game tone-maps its frame and this does not, so a dominant
            // directional at 2.2 would clip every lit surface to white.
            float3 forward = normalize(CameraDirection);
            float3 towardsCamera = -forward;

            float3 keyDirection = normalize(float3(0.357, -0.234, 0.904));
            float3 keyColour = float3(1.000, 0.980, 0.898);

            float3 albedo = BaseColour;
            float clear = 1.0;

            if (HasTexture > 0.5)
            {
                float4 painted = Surface.Sample(Sampling, input.Uv);
                albedo = painted.rgb;
                clear = painted.a;
            }

            // Cut away where the picture is clear.
            //
            // Hair is built as flat cards with the strands painted on and the
            // space between them left transparent; 432 of the game's character
            // base materials blend this way, and the commonest of them is the
            // hair shader. Drawn solid, every card is a slab across the face -
            // which is what was making faces look blotchy, and what was sitting
            // over one costume's eyes.
            //
            // Where the cut sits is the material's own OpacityMaskClipValue,
            // which is 0.333 on 293 of them and 0.001 on 129.
            if (Cutout > 0.5 && clear < CutoutThreshold) discard;

            // A material that says it is unlit shows its own colour and stops.
            // 52 of the game's character base materials say so, among them the
            // props whose names begin fxunlit, and lighting one of those turns
            // clean metal into something muddy.
            if (Unlit > 0.5) return float4(pow(saturate(albedo), 1.0 / 2.2), 1.0);

            // How much light reaches this point.
            //
            // Whether the light wraps past the terminator is the material's
            // choice - usehalflambert, on for 1,482 of the slots on listed
            // models and off for 1,180 - and so is the exponent it is then
            // shaped by. Where the material binds a falloff curve instead, the
            // curve is the shaping and the exponent does not apply.
            float lambert = dot(normal, keyDirection);

            // Wrapped, so that a surface turned away from the key lands at one
            // end of the range and one facing it at the other, rather than
            // everything turned away piling up at nothing.
            float wrapped = saturate((lambert * 0.5) + 0.5);

            float toLight = (UseHalfLambert > 0.5) || (WrapLight > 0.5) ? wrapped : saturate(lambert);

            float3 diffuse = pow(max(toLight, 0.0001), DiffusePower).xxx;

            // A bound falloff curve is always read across the whole of itself.
            //
            // These curves run from an authored warm shadow at one end to the
            // lit colour at the other, and a dark end painted as a shadow
            // rather than as black is one meant to be reached. Read with the
            // light clamped instead, half the curve can never be reached at
            // all: everything facing away from the key piles up on the single
            // darkest pixel, and wherever neighbouring parts of a surface fall
            // on either side of that the join shows as a hard-edged patch.
            //
            // That is what was marking faces. Measured over all 2,107 models,
            // reading the curve across its whole width takes the count of
            // hard-edged joins from 2,208,750 down to 2,088,238, and 121 models
            // lose most of theirs outright - one costume's face going from 873
            // to 624 and coming out clean. Costumes whose materials already ask
            // to be lit this way are unchanged, as they should be.
            if (HasRamp > 0.5)
            {
                diffuse = Ramp.Sample(Sampling, float2(ClampCurve > 0.5 ? saturate(lambert) : wrapped, 0.5)).rgb;
            }

            // The material's own ambient colour, added to the lit amount before
            // anything is multiplied by it. This is a fifth in red falling to a
            // tenth in blue, which is why an unlit side reads as warm shadow
            // rather than black.
            //
            // The whole of this - the lighting, the ambient and the reflection
            // - is what the surface colour multiplies, and the highlight is
            // added to the result. That order is the game's own, read out of
            // its base pass instruction by instruction.
            float3 lit = diffuse + AmbientColour;

            // The frame as it was built before the game's own base pass was
            // read: the surface lit and given its ambient, and everything else
            // added on top of that. Kept for the builds this reading was not
            // done against, which are to draw exactly as they always have.
            float3 wasShaded = albedo * ((diffuse * keyColour) + AmbientColour);

            if (Traced < 0.5 && UseFill > 0.5)
            {
                float towardsFill = saturate(dot(normal, -normalize(FillDirection)));
                wasShaded += albedo * FillColour * (pow(towardsFill, max(FillPower, 0.0001)) * FillStrength);
            }

            // The packed mask. Which channel means what was read from the
            // material's own parameter name, whose words list its channels in
            // order.
            float gloss = 0.0;
            float reflectivity = 0.0;
            float rimMask = 1.0;

            // How tight the shine is. A mask that names a sharpness channel
            // says so; one that does not leaves this at its default and the
            // shine stays at the tight end - which is right for the chrome
            // masks, since those are exactly the ones bound under a name with
            // no sharpness word in it.
            float sharpnessMask = 1.0;

            if (HasSpecular > 0.5)
            {
                float4 mask = SurfaceMask.Sample(Sampling, input.Uv);

                gloss = saturate(dot(mask, GlossSelect));
                reflectivity = saturate(dot(mask, ReflectSelect));

                if (dot(SharpSelect, float4(1.0, 1.0, 1.0, 1.0)) > 0.5)
                    sharpnessMask = saturate(dot(mask, SharpSelect));

                if (dot(RimSelect, float4(1.0, 1.0, 1.0, 1.0)) > 0.5)
                    rimMask = saturate(dot(mask, RimSelect));
            }

            // What colour the highlight and the reflection are. Where the
            // material binds a colour map, that is the answer; where it does
            // not, its own speccolorvalue is, which is white unless it says
            // otherwise.
            //
            // Falling back to the surface colour instead was wrong: a metal's
            // surface colour in this game is its unlit, in-shadow colour, which
            // for chrome armour is very nearly black, and multiplying by it
            // left one such costume pitch black in 1.53 while 1.52 - which
            // binds a plain white map - looked right. The same costume looks
            // the same in both games.
            float3 tint = SpecularColour;
            if (HasSpecularColour > 0.5)
            {
                tint = SpecColour.Sample(Sampling, input.Uv).rgb;
            }

            // The highlight. The older materials add two at once, a broad one
            // and a tight one, each with its own strength - 829 of the slots on
            // listed models ask for both. The newer ones have a single lobe
            // whose tightness a mask channel can raise.
            // What is added to the surface rather than multiplied into it: the
            // highlight always, and the reflection where the material does not
            // ask for it to take the surface colour.
            float3 onTop = 0.0;

            float3 highlight = 0.0;

            if (UseSpecular > 0.5 && gloss > 0.0)
            {
                // Where the light lands, taken two different ways by the two
                // vocabularies.
                //
                // The older one shines along the half-vector. The newer one
                // shines along the reflected light - read out of its own
                // compiled shader, where the value the exponent is applied to
                // is the dot of the view with 2(n.V)n + V, a reflection. Its
                // parameters are named for it: phongspecmult, phongdiffusepower.
                //
                // It matters most on a model lit from above and seen head on,
                // where a reflection points away from the viewer over most of
                // the body and a half-vector does not. One black costume came
                // out mid-blue on the half-vector.
                // The newer vocabulary shines along the reflected light: the
                // value its exponent is applied to is the dot of the view with
                // 2(n.V)n + V, read out of its own compiled shader, and it
                // names its parameters for it - phongspecmult,
                // phongdiffusepower.
                //
                // The older one raises the surface's own amount of light to its
                // exponent. Its pixel shader dots the normal with the register
                // it declares as texcoord4; its vertex shader declares
                // texcoord4 as the output it fills by dotting
                // LightPositionAndInvRadius against the tangent, the binormal
                // and the normal. The two are the same interpolator, so that
                // register carries the light's direction and the dot is N.L.
                //
                // Neither is a half-vector, which is what stood in for both.
                float facing = SpecularFromDiffuse > 0.5
                    ? saturate(dot(reflect(-keyDirection, normal), towardsCamera))
                    : saturate(dot(normal, keyDirection));

                float shine = pow(facing, lerp(SpecularPowerLow, SpecularPowerHigh, sharpnessMask))
                            * SpecularStrength;

                if (UseDualSpecular > 0.5)
                {
                    shine += pow(facing, lerp(SecondPowerLow, SecondPowerHigh, sharpnessMask))
                           * SecondStrength;
                }

                shine *= SpecularTotal * gloss;

                // Added on top of the surface, not scaled by it.
                //
                // Read out of the game's own base pass, instruction by
                // instruction. It builds its frame as
                //
                //     diffuse * (lit + ambient + reflection) + highlight
                //
                // so the diffuse multiplies the lighting and the reflection and
                // stops there; the highlight is added to the result. A black
                // costume with a full gloss mask therefore still shines, which
                // is what a black costume in this game looks like.
                //
                // This stood the other way round for a while, on the strength
                // of three costumes that came out grey-blue when they are
                // black. They were being washed by a fill light their compiled
                // material is handed no parameter for; with that gone they are
                // black either way, and multiplying by the surface as well
                // turned one all-black costume - whose colour map is
                // pure black over 95 per cent of itself - into a flat
                // silhouette with no form at all.
                // Where the material says so, the highlight is the surface's
                // own colour instead of the colour it states - blended toward
                // its plain brightness by speccolordesat and scaled by
                // diffusespecmult. That is what usediffusemultspec chooses, and
                // the shader it chooses is handed no highlight colour at all.
                //
                // One costume states 0.012, 0.302, 1 at a strength of 60 and
                // sets that switch. Painted with the stated colour it came out
                // vivid cyan; it is a black costume.
                if (SpecularFromDiffuse > 0.5)
                {
                    // Both, multiplied together - not one or the other.
                    //
                    // Read from the shader this material compiles, which takes
                    // the shine, multiplies it by the colour the material
                    // states, and multiplies that by the surface's own colour
                    // blended toward its plain brightness by speccolordesat and
                    // scaled by diffusespecmult.
                    //
                    // The surface term is what makes a black costume black. One
                    // states a highlight of 0.012, 0.302, 1 at a strength of 60
                    // and a diffusespecmult of 55, and is still black, because
                    // its colour map is 17, 21, 27 and that multiplies the lot.
                    float ownPlain = dot(albedo, float3(0.3, 0.59, 0.11));

                    float3 fromSurface = lerp(albedo, ownPlain.xxx, saturate(SpecularDesaturate))
                                       * SpecularFromDiffuseAmount;

                    highlight = tint * shine * fromSurface;
                }
                else
                {
                    highlight = tint * shine;
                }

                float plain = dot(albedo, float3(0.3, 0.59, 0.11));
                wasShaded += tint * shine * keyColour
                           * lerp(albedo, plain.xxx, saturate(SpecularDesaturate));
            }

            // What the surface reflects, and only where the material says it
            // reflects at all. This is the largest single thing the viewport
            // had wrong: 1,583 of the slots on listed models have their
            // reflection switched off, and every one of them was being
            // reflected anyway because a mask happened to carry a reflectivity
            // channel.
            if (UseReflection > 0.5 && HasEnvironment > 0.5 && reflectivity > 0.004)
            {
                // The game reflects a panorama, so the reflected direction is
                // turned into a longitude and a latitude and read off it.
                //
                // Up is Z here, not Y. Reading the latitude off Y instead split
                // every curved metal surface into a bright half and a black
                // half - the two halves sampling the sky and the ground of the
                // panorama - which put a gold-and-black seam down a face.
                float3 mirrored = reflect(forward, normal);

                float2 sky;
                sky.x = (atan2(mirrored.y, mirrored.x) / 6.2831853) + 0.5;
                sky.y = acos(clamp(mirrored.z, -1.0, 1.0)) / 3.1415927;

                // Read at the level whose spread matches the shine this surface
                // asks for, so it reflects as widely as it shines.
                float blur = (1.0 - sharpnessMask) * max(EnvironmentLevels - 1.0, 0.0);

                float3 panorama = Reflected.SampleLevel(Sampling, sky, blur).rgb * tint * reflectivity;

                float3 reflected = panorama * ReflectionStrength;

                // What the older reading did with it: the material's own
                // multiplier discarded, and the panorama laid over the surface
                // rather than multiplied into it.
                wasShaded += ReflectionFromDiffuse > 0.5 ? panorama * albedo : panorama;

                // The surface colour multiplies it, always. The game's base
                // pass keeps its reflection inside the block the diffuse
                // multiplies, whatever the material's own
                // multiplyreflectionbydiffuse says - that switch chooses
                // between the two reflection paths, not whether the surface
                // scales them.
                lit += reflected;
            }

            // The frame, in the order the game's base pass builds it: the
            // surface colour multiplies the lighting, the highlight is added to
            // that, and the key light's colour multiplies the sum.
            float3 shaded = ((albedo * lit) + highlight + onTop) * keyColour;

            // The scene's own ambient, and the fill light that travels with it.
            //
            // The base pass adds the material's fill to its ambient amount,
            // multiplies the sum by the surface colour and scales the result by
            // the scene's sky light - so the fill amount of 30 that 1,292 of
            // 1,413 materials carry is thirty parts of a sky light whose
            // brightness the game's own hub sets to 0.06, not thirty times the
            // surface.
            //
            // Applied at its stated strength instead, it more than doubled
            // every costume that has one: one black costume measured 30, 33, 39
            // without it and 68, 70, 81 with.
            float3 ambient = AmbientMult.xxx;

            if (UseFill > 0.5)
            {
                // The direction as the material states it, not turned around:
                // the base pass takes the surface's amount along it directly
                // and drops the light where that amount is negative.
                float towards = dot(normal, FillDirection);

                if (towards > 0.0)
                    ambient += FillColour * (pow(towards, max(FillPower, 0.0001)) * FillStrength);
            }

            shaded += albedo * ambient * SkyColour;

            // The rim light along the silhouette. The materials ask for one on
            // 1,953 of the slots on listed models, in a colour they state -
            // usually a cool blue - and 1,630 of them put it only where their
            // mask allows.
            //
            // The exponent and the strength are the material's own. Which of
            // the older vocabulary's several rim numbers is which is not
            // recorded in the cooked data, so its rimpower is taken as the
            // exponent and its rimmult, whose role it does not state, is left
            // alone.
            if (UseRim > 0.5)
            {
                float edge;

                if (RimStated > 0.5)
                {
                    // The newer materials state the shape and the strength of
                    // their rim, and their compiled form is handed both.
                    edge = pow(1.0 - saturate(dot(normal, towardsCamera)), max(RimFalloff, 0.0001))
                         * RimStrength;
                }
                else
                {
                    // The older ones state numbers their shader never reads.
                    // Their base pass builds the rim from constants: it turns
                    // the surface normal into the camera's own frame, takes its
                    // amount along a fixed direction of (0.44, -0.12, -1.17),
                    // doubles it and takes one away, and scales the result by
                    // four tenths.
                    //
                    // Read instruction by instruction out of the game's own
                    // base pass. It is not a falloff at all - there is no
                    // exponent and no view angle - and reading it as one, at a
                    // rimstrength of 1.75 where the shader uses 0.4, put a pale
                    // blue wash over every dark costume in the game.
                    float3 forward = normalize(CameraDirection);
                    float3 right = normalize(cross(float3(0.0, 0.0, 1.0), forward));
                    float3 above = cross(forward, right);

                    float3 towards = (right * 0.44) + (above * -0.12) + (forward * 1.17);

                    edge = max(((dot(normal, towards) * 2.0) - 1.0) * 0.4, 0.0);
                }

                float3 rim = RimColour * (edge * rimMask);

                // Added last and on its own, outside everything the light
                // touches. The game's base pass keeps the rim in a separate
                // register through the whole of the lighting and adds it after
                // the light colour has been applied, so it is neither scaled by
                // the surface nor tinted by the key.
                //
                // Only the materials that say so take the surface colour.
                if (RimFromDiffuse > 0.5) rim *= albedo;

                shaded += rim;

                // And what the older reading made of it: a falloff in the
                // viewing angle at the material's stated strength, scaled by
                // the surface the way the highlight was.
                float wasEdge = pow(1.0 - saturate(dot(normal, towardsCamera)), max(RimFalloff, 0.0001));
                float3 wasRim = RimColour * (wasEdge * RimStrength * rimMask);
                float plainRim = dot(albedo, float3(0.3, 0.59, 0.11));

                wasShaded += wasRim * (RimFromDiffuse > 0.5
                    ? albedo
                    : lerp(albedo, plainRim.xxx, saturate(SpecularDesaturate)));
            }

            // For every build but the one this reading was made against, the
            // frame as it was assembled before.
            if (Traced < 0.5) shaded = wasShaded;

            // The light the stand throws up around whatever is standing on it.
            //
            // Not the game's: nothing in the cooked data asks for this, and it
            // is not pretending to. It is the viewport's own furniture, drawn
            // on the stand alone so that the model beside it is still shaded by
            // the material and nothing else.
            //
            // A disc of light centred under the model, brightest at its feet
            // and gone by the edge of the pad, with rings running outward
            // through it and fine lines across the whole surface. Added rather
            // than mixed, because a projection is light arriving and not paint.
            if (Hologram.x > 0.5)
            {
                float2 outward = input.Place.xy - HologramAt.xy;
                float far = length(outward);

                // How far out the light reaches: the model's own width, with
                // room around it, so a wide model lights more of the pad than a
                // narrow one.
                float reach = max(HologramAt.w, 0.0001);
                float near = saturate(1.0 - (far / reach));

                // Squared, so it gathers at the feet instead of washing the
                // whole pad evenly.
                float pool = near * near;

                // Rings running outward, and a slow breath in the whole of it.
                float rings = 0.5 + (0.5 * sin((far * 0.09) - (Hologram.y * 1.7)));
                float breath = 0.85 + (0.15 * sin(Hologram.y * 0.9));

                // Lines across the surface, fixed to the world so they read as
                // something the pad is doing rather than a pattern painted on.
                float lines = 0.75 + (0.25 * sin(input.Place.y * 0.7));

                float3 projected = float3(0.176, 0.702, 1.0);

                shaded += projected * (pool * breath * ((rings * 0.5) + 0.5) * lines * 0.62);

                // And an edge where the light stops, which is what makes it
                // read as a beam rather than a stain.
                float edge = saturate(1.0 - abs(((far / reach) - 0.82) * 14.0));
                shaded += projected * (edge * breath * 0.25);
            }


            // The frame is compressed before it is shown, because the
            // materials are written for a brighter range than a screen has.
            // They ask for specular strengths of 6.5, reflection multipliers of
            // 500 and emissive multipliers of 150; against a range that stops
            // at one, every metal costume in the game clips to a white
            // silhouette, which is exactly what happened.
            //
            // The game ships the setting: its post-processing declares a
            // tonemapper whose range is 8, and the chain used where no level
            // overrides it leaves that alone. So 8 is where white is.
            //
            // The curve that carries values up to 8 down to 1 is not shipped -
            // it lives in the compiled post shader - so the standard curve
            // defined by a white point alone is used. Its shape is a choice;
            // the white point is not.
            float white = 8.0;

            float3 shown = (shaded * (1.0 + (shaded / (white * white)))) / (1.0 + shaded);

            // Everything above is in linear light: the surface textures declare
            // themselves gamma-encoded and are handed over already converted,
            // which is the only footing on which multiplying by a light is
            // meaningful. The screen wants gamma back.
            return float4(pow(saturate(shown), 1.0 / 2.2), 1.0);
        }
        """;
}
