using System.Numerics;
using System.Runtime.InteropServices;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>What every draw in a frame shares.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FrameConstants
{
    public Matrix4x4 WorldViewProjection;
    public Matrix4x4 World;
    public Vector3 CameraDirection;
    public float Pad0;
    public Vector3 BaseColour;
    public float HasTexture;
    public float HasSpecular;
    public float HasEnvironment;
    public float HasNormalMap;

    /// <summary>Whether the material binds a colour for its highlight.</summary>
    public float HasSpecularColour;

    /// <summary>Whether the material binds a curve to be lit along.</summary>
    public float HasRamp;

    // Three of these, not two. A shader packs its constants in fours, so a
    // group of three leaves a four-byte hole and everything after it lands one
    // slot early - which is exactly what happened: the channel selectors were
    // read shifted, so gloss and reflectivity came back as whatever sat beside
    // them, and a red cape washed out to pink.
    /// <summary>How many levels of blur the reflected picture was built with.</summary>
    public float EnvironmentLevels;

    public float Pad4;
    public float Pad5;

    /// <summary>Picks the mask channel that is the specular amount.</summary>
    public Vector4 GlossSelect;

    /// <summary>Picks the mask channel that is the reflectivity.</summary>
    public Vector4 ReflectSelect;

    /// <summary>Picks the mask channel that says how sharp the shine is.</summary>
    public Vector4 SharpSelect;

    /// <summary>Picks the mask channel that says where the rim light shows.</summary>
    public Vector4 RimSelect;

    // Everything below is the material's own account of how it shades, read
    // from its parameters and the choices it was compiled with. None of it is
    // chosen here.

    /// <summary>The light reaching a surface that faces away from the key.</summary>
    public Vector3 AmbientColour;

    /// <summary>The exponent the lit amount is raised to.</summary>
    public float DiffusePower;

    public Vector3 RimColour;
    public float RimFalloff;

    public Vector3 FillColour;
    public float RimStrength;

    /// <summary>Which way the material's own second light points.</summary>
    public Vector3 FillDirection;
    public float FillStrength;

    /// <summary>The highlight's colour where the material binds no map for it.</summary>
    public Vector3 SpecularColour;
    public float FillPower;

    public float UseHalfLambert;
    public float UseSpecular;
    public float UseDualSpecular;
    public float UseRim;

    public float RimFromDiffuse;
    public float UseReflection;
    public float ReflectionStrength;
    public float ReflectionFromDiffuse;

    public float SpecularPowerLow;
    public float SpecularPowerHigh;
    public float SecondPowerLow;
    public float SecondPowerHigh;

    public float SpecularStrength;
    public float SecondStrength;
    public float SpecularTotal;

    /// <summary>
    /// How far the surface's own colour is mixed toward its plain brightness
    /// before it scales the highlight.
    /// </summary>
    public float SpecularDesaturate;

    public float Pad9;
    public float UseFill;
    public float NormalStrength;

    /// <summary>Whether to cut the surface away where its picture is clear.</summary>
    public float Cutout;

    public float CutoutThreshold;

    /// <summary>
    /// Whether the light is read across the whole of the falloff curve rather
    /// than only the half of it a surface facing the light reaches.
    /// </summary>
    public float WrapLight;

    /// <summary>
    /// Forces the falloff curve to be read the old way, over half its width.
    /// Only the sweep sets this, so the two readings can be compared.
    /// </summary>
    public float ClampCurve;

    /// <summary>Whether the surface shows its own colour, unlit.</summary>
    public float Unlit;

    /// <summary>
    /// Whether this material's compiled form is handed rim numbers of its own.
    /// </summary>
    /// <remarks>
    /// The two vocabularies do not shade their rim the same way and only one of
    /// them is asked. A v2 material's compiled form lists rimfalloff and
    /// rimcolormult as parameters; a v1 material's lists neither, and its base
    /// pass builds the rim from constants instead.
    /// </remarks>
    public float RimStated;

    /// <summary>
    /// How much of the scene's ambient this surface takes, beside its fill.
    /// </summary>
    public float AmbientMult;

    /// <summary>
    /// Whether to draw the frame the way the game's own base pass builds it.
    /// </summary>
    /// <remarks>
    /// The reading behind it was made against 1.53.0.203, and every other
    /// build draws exactly as it drew before that reading was done.
    /// </remarks>
    public float Traced;

    /// <summary>Whether the highlight takes the surface's own colour.</summary>
    public float SpecularFromDiffuse;

    /// <summary>How much of it the highlight then takes.</summary>
    public float SpecularFromDiffuseAmount;

    public float Pad12;
    public float Pad14;
    public float Pad15;

    /// <summary>
    /// The scene's own ambient, which the fill light and the ambient amount are
    /// both scaled by.
    /// </summary>
    /// <remarks>
    /// Read from the game's own levels rather than chosen: the sky light in one
    /// indoor level is 194, 196, 255 at a brightness of 0.06, and another's
    /// are 0.05 and 0.15. The material states a fill amount of 30 and it is
    /// this that keeps thirty from meaning thirty.
    /// </remarks>
    public Vector3 SkyColour;

    public float Pad13;

    /// <summary>
    /// Where the model stands and how far the stand's light reaches around it:
    /// the point in the world, and the reach in its w.
    /// </summary>
    public Vector4 HologramAt;

    /// <summary>
    /// Whether the surface being drawn is the stand and carries that light, in
    /// x, and how long the viewport has been open, in y.
    /// </summary>
    public Vector4 Hologram;

    public const int Size = (16 * 4) + (16 * 4) + (96 * 4);
}

/// <summary>Fills the block a draw reads from a material's own account of itself.</summary>
/// <remarks>
/// Kept beside the block rather than in the viewport so that anything drawing
/// these models - the viewport, and the sweep that grades every model in the
/// game - shades them the same way. A second copy of this would let the two
/// drift, and then the sweep would be grading something the user never sees.
/// </remarks>
public static class ShadingConstants
{
    /// <summary>
    /// Checks the hand-written size against the block it describes, before
    /// anything is drawn with it.
    /// </summary>
    /// <remarks>
    /// FrameConstants.Size sizes the buffer, and the block is then written into
    /// it by an unchecked pointer store of the whole struct. Nothing made the
    /// two agree: the count of floats is written out by hand and has to be
    /// raised every time a field is added. Get it wrong low and the store runs
    /// past the buffer; get it wrong high and the shader reads a tail that was
    /// never written. A copy of this block once fell six fields behind and did
    /// exactly the second of those, and the tail was whatever had been left in
    /// the buffer - so it is asked here, once, and it says which way it is
    /// wrong.
    /// </remarks>
    static ShadingConstants()
    {
        int actual = Marshal.SizeOf<FrameConstants>();

        if (actual != FrameConstants.Size)
        {
            throw new InvalidOperationException(
                $"The shading block is {actual} bytes and its stated size is {FrameConstants.Size}. "
                + "FrameConstants.Size has to be raised to match the fields it describes.");
        }
    }

    /// <summary>
    /// Copies a material's own account of its shading into what the shader
    /// reads.
    /// </summary>
    /// <remarks>
    /// Nothing is decided here. Whether a surface reflects, whether it has a
    /// rim light, how tight its highlight is and what colour its ambient is are
    /// all the material's statements; this only carries them across.
    /// <para>
    /// An untextured model is shown as plain geometry, so the terms that only
    /// mean anything against a texture are left off for it.
    /// </para>
    /// </remarks>
    public static void Fill(
        ref FrameConstants constants, SurfaceShading shading, bool textured, bool hasNormalMap)
        => Fill(ref constants, shading, textured, hasNormalMap, traced: false);

    /// <param name="traced">
    /// Whether this model's build is the one the game's own base pass was read
    /// against. Everything else is left drawing as it drew before.
    /// </param>
    public static void Fill(
        ref FrameConstants constants, SurfaceShading shading, bool textured, bool hasNormalMap, bool traced)
    {
        constants.Traced = traced ? 1f : 0f;

        // The colour alone where the base pass has been read, because there the
        // ambient amount travels with the fill light and is scaled by the
        // scene's sky. Everywhere else the two stay folded together, which is
        // how the ambient was read before.
        constants.AmbientColour = textured
            ? new Vector3(shading.Ambient.R, shading.Ambient.G, shading.Ambient.B)
              * (traced ? 1f : shading.AmbientMult)
            : Vector3.Zero;

        constants.DiffusePower = shading.DiffusePower;
        constants.UseHalfLambert = shading.HalfLambert ? 1f : 0f;

        constants.HasNormalMap = textured && hasNormalMap && shading.NormalMap ? 1f : 0f;
        constants.NormalStrength = shading.NormalStrength;

        constants.UseSpecular = textured && shading.Specular ? 1f : 0f;
        constants.UseDualSpecular = shading.DualSpecular ? 1f : 0f;
        constants.SpecularPowerLow = shading.SpecularPowerLow;
        constants.SpecularPowerHigh = shading.SpecularPowerHigh;
        constants.SecondPowerLow = shading.SecondPowerLow;
        constants.SecondPowerHigh = shading.SecondPowerHigh;
        constants.SpecularStrength = shading.SpecularStrength;
        constants.SecondStrength = shading.SecondStrength;
        constants.SpecularTotal = shading.SpecularTotal;
        constants.SpecularDesaturate = shading.SpecularDesaturate;
        constants.SpecularFromDiffuse = shading.SpecularFromDiffuse ? 1f : 0f;
        constants.SpecularFromDiffuseAmount = shading.SpecularFromDiffuseAmount;

        constants.SpecularColour = new Vector3(
            shading.SpecularColour.R, shading.SpecularColour.G, shading.SpecularColour.B);

        constants.UseReflection = textured && shading.Reflects ? 1f : 0f;

        // What the material asks for, at last.
        //
        // This stood at 1 while it was not known what reflectionmult - which
        // runs from 0 to 50,000 across the roster, with 3 in the middle -
        // multiplied, on the grounds that whatever it was had to be very small.
        // It is the surface colour: the game's base pass builds its reflection
        // as the panorama times the mask's reflectivity times reflectionmult,
        // and multiplies the whole of it by the diffuse.
        //
        // A metal's diffuse in this game is its unlit colour, and that is the
        // small number - one armoured costume is 70, 15, 6 and a chrome one a
        // flat 51. Held at 1 and added on top of the surface instead,
        // the panorama covered that red armour completely: it measured
        // 116, 104, 105 with the reflection and 75, 44, 36 without.
        constants.ReflectionStrength = shading.ReflectionStrength;
        constants.ReflectionFromDiffuse = shading.ReflectionFromDiffuse ? 1f : 0f;

        constants.UseRim = textured && shading.Rim ? 1f : 0f;
        constants.RimFalloff = shading.RimFalloff;
        constants.RimStrength = shading.RimStrength;
        constants.RimStated = shading.RimStated ? 1f : 0f;
        constants.AmbientMult = shading.AmbientMult;

        // The sky light the game's own hub is built with: 194, 196, 255 at a
        // brightness of 0.06. Every scene has its own and the model is not
        // standing in any of them, so the one the game's own hub uses stands
        // for all of them.
        constants.SkyColour = new Vector3(0.760f, 0.769f, 1f) * 0.06f;
        constants.RimFromDiffuse = shading.RimFromDiffuse ? 1f : 0f;
        constants.RimColour = new Vector3(
            shading.RimColour.R, shading.RimColour.G, shading.RimColour.B);

        // A mask channel only applies where the material asked for one.
        if (!shading.RimMasked) constants.RimSelect = Vector4.Zero;

        // Only where a picture reached the surface: the cut is made from that
        // picture's own alpha, and without one there is nothing to cut by.
        constants.Unlit = textured && shading.Unlit ? 1f : 0f;
        constants.Cutout = textured && shading.Cutout ? 1f : 0f;
        constants.CutoutThreshold = shading.CutoutThreshold;

        constants.UseFill = textured && shading.Fill ? 1f : 0f;
        constants.FillStrength = shading.FillStrength;
        constants.FillPower = shading.FillPower;
        constants.FillColour = new Vector3(
            shading.FillColour.R, shading.FillColour.G, shading.FillColour.B);
        constants.FillDirection = new Vector3(
            shading.FillDirection.R, shading.FillDirection.G, shading.FillDirection.B);
    }
}
