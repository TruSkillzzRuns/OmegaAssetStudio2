namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>How much notice something needs.</summary>
public enum FindingKind
{
    /// <summary>Something was wrong and has been put right.</summary>
    Mended,

    /// <summary>Worth knowing, but nothing was wrong.</summary>
    Noted,

    /// <summary>
    /// Something is wrong that this cannot put right, or has only patched over.
    /// </summary>
    Warned,
}

/// <summary>
/// One thing found in a model being brought in.
/// </summary>
/// <remarks>
/// Kept separate from the running commentary so it can be shown as a short list
/// the user actually reads. A model that arrives sideways, half-weighted and a
/// hundred times too large has three distinct faults, and a paragraph of prose
/// makes that harder to see, not easier.
/// </remarks>
public sealed record ModelFinding
{
    public required FindingKind Kind { get; init; }

    /// <summary>What was found, in a few words.</summary>
    public required string What { get; init; }

    /// <summary>What was done about it, or what the user should do.</summary>
    public required string Detail { get; init; }

    public override string ToString() => $"{Kind}: {What} — {Detail}";
}
