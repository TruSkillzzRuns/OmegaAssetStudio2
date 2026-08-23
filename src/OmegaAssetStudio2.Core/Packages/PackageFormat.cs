namespace OmegaAssetStudio2.Core.Packages;

/// <summary>
/// The on-disk format of a cooked package, as recorded in its header.
/// </summary>
/// <remarks>
/// Read from the bytes, never guessed from a folder name or a product version.
/// Two installs that ship different game builds can still share a package
/// format — this describes the <em>byte layout</em> and nothing else.
/// <para>
/// Observed across the installed clients: two read 868/3 and one reads 894/3.
/// Format alone therefore cannot identify which install a package came from.
/// </para>
/// </remarks>
public readonly record struct PackageFormat(int FileVersion, int LicenseeVersion)
{
    /// <summary>The format could not be read. Never treat this as a match.</summary>
    public static readonly PackageFormat Unknown = new(0, 0);

    public bool IsKnown => FileVersion > 0;

    /// <summary>
    /// True when two packages share a byte layout, so content can move between
    /// them without conversion. An unknown format is never compatible with
    /// anything, including another unknown — a failed probe must not be allowed
    /// to authorise a cross-format write.
    /// </summary>
    public bool IsCompatibleWith(PackageFormat other) =>
        IsKnown && other.IsKnown &&
        FileVersion == other.FileVersion &&
        LicenseeVersion == other.LicenseeVersion;

    public override string ToString() => IsKnown ? $"{FileVersion}/{LicenseeVersion}" : "unknown";
}
