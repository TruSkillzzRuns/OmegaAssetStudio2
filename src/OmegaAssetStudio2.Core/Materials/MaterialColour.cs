namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// A colour as the engine stores it: four floating-point channels, not bytes.
/// </summary>
/// <remarks>
/// Values are not clamped to 0-1. Effect colours are routinely authored brighter
/// than white so they bloom, and clamping on read would quietly destroy that.
/// </remarks>
public readonly record struct MaterialColour(float R, float G, float B, float A)
{
    /// <summary>Approximate 8-bit form, for display only.</summary>
    public (byte R, byte G, byte B, byte A) ToBytes() => (
        ToByte(R), ToByte(G), ToByte(B), ToByte(A));

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    public static MaterialColour FromBytes(byte r, byte g, byte b, byte a) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>True when any channel exceeds full brightness.</summary>
    public bool IsOverbright => R > 1f || G > 1f || B > 1f;

    public string ToHex()
    {
        (byte r, byte g, byte b, byte a) = ToBytes();
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    public override string ToString() =>
        $"{R:0.###}, {G:0.###}, {B:0.###}, {A:0.###}" + (IsOverbright ? " (overbright)" : "");
}
