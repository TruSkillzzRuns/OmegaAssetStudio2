namespace OmegaAssetStudio2.Core.Icons;

/// <summary>
/// Who the icons in a client are about: the playable characters, the team-up
/// characters, and the spellings that mean the same one.
/// </summary>
/// <remarks>
/// All of it is read from the scanned names. The client spells some subjects
/// more than one way - a misspelling, an abbreviation, a long and a short form
/// of the same title - and each of those would otherwise open a category of its
/// own next to the real one.
/// </remarks>
public sealed class IconSubjects
{
    private readonly Dictionary<string, string> _aliases;

    private IconSubjects(
        HashSet<string> heroes,
        HashSet<string> teamUps,
        Dictionary<string, string> aliases)
    {
        Heroes = heroes;
        TeamUps = teamUps;
        _aliases = aliases;
    }

    public HashSet<string> Heroes { get; }

    public HashSet<string> TeamUps { get; }

    /// <summary>The spelling to file a subject under.</summary>
    public string Canonical(string subject)
        => _aliases.TryGetValue(subject, out string? canonical) ? canonical : subject;

    /// <summary>
    /// A subject whose spelling is one letter off a known one. The client has a
    /// handful of these - a character's name mistyped in a few icons - and they
    /// would otherwise be filed away from the rest of that character's icons.
    /// </summary>
    /// <summary>A spelling known only as a variant of some other subject.</summary>
    public string? KnownVariant(string token)
        => _aliases.TryGetValue(token, out string? owner) ? owner : null;

    public string? NearMiss(string token)
    {
        if (token.Length < 6) return null;

        foreach (string known in Heroes.Concat(TeamUps))
            if (DiffersByOneLetter(token, known)) return known;

        // A longer name can be mistyped by more than one letter and still
        // plainly be the same name. Two is only allowed once the name is long
        // and the opening matches, which is far too much agreement for two
        // different subjects to reach by accident.
        foreach (string known in Heroes.Concat(TeamUps))
        {
            if (known.Length < 9 || token.Length < 9) continue;
            if (!token.StartsWith(known[..5], StringComparison.OrdinalIgnoreCase)) continue;
            if (EditDistance(token, known) <= 2) return known;
        }

        return null;
    }

    /// <summary>How many single-letter changes separate two spellings.</summary>
    private static int EditDistance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Marks out the subjects, then folds the duplicate spellings together.
    /// </summary>
    public static IconSubjects Build(IEnumerable<string> objectNames, IDisplayNames display)
    {
        string[] names = objectNames as string[] ?? objectNames.ToArray();

        HashSet<string> heroes = IconTaxonomy.BuildRoster(names);
        HashSet<string> teamUps = BuildTeamUpRoster(names);

        // Some characters are playable AND available as a team-up. The hero
        // side wins the category, because that is where the bulk of their
        // icons belong; the icons that name the team-up marker outright are
        // still filed under team-ups by the name itself.
        teamUps.ExceptWith(heroes);

        var counts = CountSubjects(names, heroes, teamUps);
        var aliases = BuildAliases(heroes, teamUps, counts, display);

        FoldCostumeVariants(names, heroes, teamUps, aliases);

        // The variants stay in the rosters on purpose. A name that uses the
        // losing spelling still has to be recognised as naming that subject;
        // it is only filed under the winning spelling, which Canonical does.
        // Dropping the variants here is what once sent a whole character's
        // gear and portraits into the catch-all.

        return new IconSubjects(heroes, teamUps, aliases);
    }

    /// <summary>
    /// Team-up characters: the subjects that carry powers of their own.
    /// </summary>
    /// <remarks>
    /// A power is what makes a team-up a character rather than a label. The
    /// same slot also holds the bonus icons - the stat names a team-up grants -
    /// and those never carry a power, which is how the two are told apart.
    /// </remarks>
    private static HashSet<string> BuildTeamUpRoster(IEnumerable<string> names)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var powered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            string[] parts = name.Split('_');

            // Anyone named after the team-up marker is a candidate.
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!parts[i].Equals("teamup", StringComparison.OrdinalIgnoreCase)) continue;
                if (parts[i + 1].Length >= 3) named.Add(parts[i + 1]);

                break;
            }

            if (parts.Length < 3 || parts[1].Length < 3) continue;

            // A power written either way counts as carrying one.
            if (parts[0].Equals("teamup", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("power", StringComparison.OrdinalIgnoreCase))
                powered.Add(parts[1]);
        }

        named.IntersectWith(powered);

        return named;
    }

    /// <summary>
    /// Subjects that only ever appear on a costume or a piece of drop art,
    /// never with a power. Those are a character wearing something, with the
    /// costume run onto the end of the name, so each is folded into the
    /// character it is built from.
    /// </summary>
    private static void FoldCostumeVariants(
        IEnumerable<string> names,
        HashSet<string> heroes,
        HashSet<string> teamUps,
        Dictionary<string, string> aliases)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            string[] parts = name.Split('_');

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!parts[i].Equals("teamup", StringComparison.OrdinalIgnoreCase)) continue;

                string subject = parts[i + 1];

                if (subject.Length >= 3
                    && !teamUps.Contains(subject)
                    && !heroes.Contains(subject))
                    variants.Add(subject);

                break;
            }
        }

        foreach (string variant in variants)
        {
            string? owner = null;

            foreach (string known in teamUps.Concat(heroes))
            {
                if (variant.Length - known.Length < 4) continue;

                bool built = variant.StartsWith(known, StringComparison.OrdinalIgnoreCase)
                          || variant.EndsWith(known, StringComparison.OrdinalIgnoreCase);

                if (!built) continue;

                // The longest owner wins, so a name is never credited to a
                // shorter name that merely happens to sit inside it.
                if (owner is null || known.Length > owner.Length) owner = known;
            }

            if (owner is not null && !aliases.ContainsKey(variant)) aliases[variant] = owner;
        }
    }

    private static Dictionary<string, int> CountSubjects(
        IEnumerable<string> names, HashSet<string> heroes, HashSet<string> teamUps)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            foreach (string part in name.Split('_'))
            {
                if (!heroes.Contains(part) && !teamUps.Contains(part)) continue;

                counts[part] = counts.GetValueOrDefault(part) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Pairs up spellings of one subject. Three things the client actually
    /// does: a letter dropped or doubled, a title written short in one place
    /// and long in another, and initials standing in for the whole name. The
    /// spelling used more often wins, so the smaller one folds into it.
    /// </summary>
    private static Dictionary<string, string> BuildAliases(
        HashSet<string> heroes,
        HashSet<string> teamUps,
        Dictionary<string, int> counts,
        IDisplayNames display)
    {
        var all = new List<string>(heroes.Concat(teamUps));
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Pair(string left, string right)
        {
            bool leftSpelled = display.IsSpelledByClient(left);
            bool rightSpelled = display.IsSpelledByClient(right);

            string keep, drop;

            if (leftSpelled != rightSpelled)
            {
                // One of the two is a spelling the client uses in its own file
                // names and the other is not. The client's own spelling wins,
                // even when the odd one is the more common - which is how a
                // misspelling ends up outnumbering the name it misspells.
                keep = leftSpelled ? left : right;
                drop = leftSpelled ? right : left;
            }
            else
            {
                bool leftWins = counts.GetValueOrDefault(left) >= counts.GetValueOrDefault(right);
                keep = leftWins ? left : right;
                drop = leftWins ? right : left;
            }

            if (!aliases.ContainsKey(drop)) aliases[drop] = keep;
        }

        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                string a = all[i], b = all[j];

                // A single letter added, dropped, or changed.
                if (a.Length >= 6 && b.Length >= 6 && DiffersByOneLetter(a, b)) { Pair(a, b); continue; }

                // The short form of a title against the long one.
                if (ShortAndLongForm(a, b)) Pair(a, b);
            }
        }

        // Initials against the full name, read off the capitals the client uses.
        foreach (string candidate in all)
        {
            if (candidate.Length is < 2 or > 3) continue;

            foreach (string full in all)
            {
                if (ReferenceEquals(candidate, full) || full.Length <= candidate.Length) continue;
                if (!Initials(display.For(full)).Equals(candidate, StringComparison.OrdinalIgnoreCase)) continue;

                aliases[candidate] = full;
                break;
            }
        }

        // Follow a chain to its end, so two variants of one subject never point
        // at different names.
        foreach (string variant in aliases.Keys.ToList())
        {
            string target = aliases[variant];
            int guard = 0;

            while (aliases.TryGetValue(target, out string? next) && guard++ < 8)
            {
                if (next.Equals(target, StringComparison.OrdinalIgnoreCase)) break;
                target = next;
            }

            aliases[variant] = target;
        }

        return aliases;
    }

    /// <summary>The first letter of each word of a display name.</summary>
    private static string Initials(string displayName)
        => new(displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(word => char.ToLowerInvariant(word[0]))
                          .ToArray());

    /// <summary>
    /// Whether one spelling is the other with a title written short - the same
    /// name once the opening word is spelled out, as in a two-letter title
    /// against the word it stands for.
    /// </summary>
    private static bool ShortAndLongForm(string a, string b)
    {
        (string shorter, string longer) = a.Length < b.Length ? (a, b) : (b, a);

        // Both must end the same way; only the opening differs.
        for (int split = 2; split <= 3 && split < shorter.Length; split++)
        {
            string tail = shorter[split..];
            if (tail.Length < 4) continue;
            if (!longer.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;

            string shortHead = shorter[..split];
            string longHead = longer[..^tail.Length];

            // The short opening has to be the start of the long one, which is
            // what an abbreviated title looks like.
            if (longHead.Length > shortHead.Length
                && longHead.StartsWith(shortHead[..1], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>One insertion, deletion, or substitution apart.</summary>
    internal static bool DiffersByOneLetter(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;

        if (a.Length == b.Length)
        {
            int differences = 0;

            for (int i = 0; i < a.Length; i++)
                if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]) && ++differences > 1)
                    return false;

            return differences == 1;
        }

        (string shorter, string longer) = a.Length < b.Length ? (a, b) : (b, a);

        int si = 0, li = 0;
        bool skipped = false;

        while (si < shorter.Length && li < longer.Length)
        {
            if (char.ToLowerInvariant(shorter[si]) == char.ToLowerInvariant(longer[li])) { si++; li++; continue; }
            if (skipped) return false;

            skipped = true;
            li++;
        }

        return true;
    }
}
