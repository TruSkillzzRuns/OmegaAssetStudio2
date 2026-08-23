namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// Puts a chosen hue onto a colour without losing the brightness that was
/// there.
/// </summary>
/// <remarks>
/// Effect colours are authored far brighter than white, and that number is not
/// decoration — it is what makes an effect glow. One chained-lightning effect
/// holds its blue at 100, and one emitter of another holds 20, 12, 0.5. A
/// colour picker can only say 0 to 1, so a hue chosen there and written
/// literally takes a channel of 100 down to 1 and switches the emitter off
/// rather than recolouring it: the file changes and the skill looks exactly as
/// it did, because the emitters that were left alone are all anybody can still
/// see.
/// <para>
/// So the hue is taken from the choice and the brightness from what was already
/// there. Applied across a curve this keeps its shape as well as its scale — a
/// key that was dim stays dim and a key that blazed still blazes, which is what
/// makes an effect fade over its life rather than switch off at the end.
/// </para>
/// </remarks>
public static class ColourBrightness
{
    /// <summary>
    /// The chosen colour, scaled so it is as bright as the one it replaces.
    /// </summary>
    /// <remarks>
    /// Only where the original was brighter than white. A colour already inside
    /// the range a picker can express is taken exactly as it was picked, so
    /// choosing a dark colour for a dark thing does what it says.
    /// </remarks>
    public static MaterialColour Keeping(MaterialColour original, MaterialColour chosen)
    {
        float was = Brightest(original);
        if (was <= 1f) return chosen;

        float now = Brightest(chosen);
        if (now <= 0f) return chosen;   // black has no hue to scale

        float by = was / now;

        return new MaterialColour(chosen.R * by, chosen.G * by, chosen.B * by, chosen.A);
    }

    /// <summary>Whether any of these were authored brighter than white.</summary>
    public static bool AnyOverbright(IEnumerable<MaterialColour> colours)
    {
        foreach (MaterialColour colour in colours)
        {
            if (colour.IsOverbright) return true;
        }

        return false;
    }

    private static float Brightest(MaterialColour colour)
        => MathF.Max(colour.R, MathF.Max(colour.G, colour.B));
}
