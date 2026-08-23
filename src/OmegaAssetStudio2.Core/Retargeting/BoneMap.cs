using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>How confidently a pair of bones was matched.</summary>
public enum MatchQuality
{
    /// <summary>The two names are the same.</summary>
    Exact,

    /// <summary>The same name, spelled differently.</summary>
    SameName,

    /// <summary>The same joint, described differently.</summary>
    SameJoint,

    /// <summary>Set by hand.</summary>
    Chosen,
}

/// <summary>One bone of the source paired with one of the target.</summary>
public sealed record BonePair
{
    public required int SourceIndex { get; init; }
    public required int TargetIndex { get; init; }
    public required string SourceName { get; init; }
    public required string TargetName { get; init; }
    public required MatchQuality Quality { get; init; }

    public override string ToString() => $"{SourceName} → {TargetName} ({Quality})";
}

/// <summary>
/// Which bone of one skeleton stands for which bone of another.
/// </summary>
public sealed class BoneMap
{
    private readonly Dictionary<int, BonePair> _bySource;

    private BoneMap(IReadOnlyList<BonePair> pairs, IReadOnlyList<int> unmatchedSource, IReadOnlyList<int> unusedTarget)
    {
        Pairs = pairs;
        UnmatchedSource = unmatchedSource;
        UnusedTarget = unusedTarget;

        _bySource = pairs.ToDictionary(p => p.SourceIndex);
    }

    public IReadOnlyList<BonePair> Pairs { get; }

    /// <summary>Source bones with nothing to stand for them.</summary>
    public IReadOnlyList<int> UnmatchedSource { get; }

    /// <summary>Target bones nothing was matched to.</summary>
    public IReadOnlyList<int> UnusedTarget { get; }

    /// <summary>The target bone standing for a source bone, or null.</summary>
    public BonePair? For(int sourceIndex) => _bySource.GetValueOrDefault(sourceIndex);

    /// <summary>What proportion of the source skeleton was matched.</summary>
    public double Coverage
    {
        get
        {
            int total = Pairs.Count + UnmatchedSource.Count;
            return total == 0 ? 0 : Pairs.Count / (double)total;
        }
    }

    /// <summary>
    /// Pairs the bones of two skeletons.
    /// </summary>
    /// <remarks>
    /// Three passes, each less certain than the one before, and each taking only
    /// bones the earlier ones left. Doing it in that order matters: a loose
    /// match made early would take a target bone that an exact match later
    /// needed, and the exact one is the one worth keeping.
    /// </remarks>
    public static BoneMap Build(
        IReadOnlyList<MeshBone> source,
        IReadOnlyList<MeshBone> target,
        IReadOnlyDictionary<string, string>? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var pairs = new List<BonePair>(source.Count);
        var takenTarget = new HashSet<int>();
        var matchedSource = new HashSet<int>();

        // Set by hand first: a person's decision outranks every rule here.
        if (chosen is { Count: > 0 })
        {
            for (int s = 0; s < source.Count; s++)
            {
                if (!chosen.TryGetValue(source[s].Name, out string? wanted)) continue;

                int t = IndexOfName(target, wanted);
                if (t < 0 || !takenTarget.Add(t)) continue;

                pairs.Add(Pair(source, target, s, t, MatchQuality.Chosen));
                matchedSource.Add(s);
            }
        }

        Match(source, target, pairs, matchedSource, takenTarget,
            b => b.Name, StringComparer.OrdinalIgnoreCase, MatchQuality.Exact);

        Match(source, target, pairs, matchedSource, takenTarget,
            b => BoneNames.Normalise(b.Name), StringComparer.Ordinal, MatchQuality.SameName);

        Match(source, target, pairs, matchedSource, takenTarget,
            b => BoneNames.Describe(b.Name), StringComparer.Ordinal, MatchQuality.SameJoint);

        var unmatched = new List<int>();
        for (int s = 0; s < source.Count; s++)
        {
            if (!matchedSource.Contains(s)) unmatched.Add(s);
        }

        var unused = new List<int>();
        for (int t = 0; t < target.Count; t++)
        {
            if (!takenTarget.Contains(t)) unused.Add(t);
        }

        return new BoneMap(pairs, unmatched, unused);
    }

    /// <summary>
    /// Pairs whatever is still unpaired, by whichever reading of a name is
    /// being tried.
    /// </summary>
    private static void Match(
        IReadOnlyList<MeshBone> source,
        IReadOnlyList<MeshBone> target,
        List<BonePair> pairs,
        HashSet<int> matchedSource,
        HashSet<int> takenTarget,
        Func<MeshBone, string> read,
        StringComparer comparer,
        MatchQuality quality)
    {
        // Only the first target bone with a given reading is offered. A skeleton
        // with two bones that read alike would otherwise pair the second one
        // arbitrarily, and which of the two won would depend on nothing.
        var byReading = new Dictionary<string, int>(comparer);

        for (int t = 0; t < target.Count; t++)
        {
            if (takenTarget.Contains(t)) continue;

            string reading = read(target[t]);
            if (reading.Length == 0) continue;

            byReading.TryAdd(reading, t);
        }

        for (int s = 0; s < source.Count; s++)
        {
            if (matchedSource.Contains(s)) continue;

            string reading = read(source[s]);
            if (reading.Length == 0) continue;

            if (!byReading.TryGetValue(reading, out int t)) continue;
            if (!takenTarget.Add(t)) continue;

            byReading.Remove(reading);

            pairs.Add(Pair(source, target, s, t, quality));
            matchedSource.Add(s);
        }
    }

    private static BonePair Pair(
        IReadOnlyList<MeshBone> source, IReadOnlyList<MeshBone> target, int s, int t, MatchQuality quality) =>
        new()
        {
            SourceIndex = s,
            TargetIndex = t,
            SourceName = source[s].Name,
            TargetName = target[t].Name,
            Quality = quality,
        };

    private static int IndexOfName(IReadOnlyList<MeshBone> bones, string name)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
