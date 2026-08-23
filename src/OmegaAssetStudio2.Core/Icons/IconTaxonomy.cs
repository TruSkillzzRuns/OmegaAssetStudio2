namespace OmegaAssetStudio2.Core.Icons;

/// <summary>
/// Sorts a client's icons into a browsable category tree.
/// </summary>
/// <remarks>
/// Icons carry no folder structure of their own: every one sits directly under
/// its package, so the only thing available to sort by is the object name. The
/// names do follow a shape - kind, then subject, then variant - and this reads
/// that shape back out.
///
/// Nothing here is a fixed list of characters. The roster is worked out from
/// the names the scan actually returned, so a client with more (or fewer)
/// characters than another sorts correctly without anything being edited. The
/// same goes for capitalisation, which is taken from the client's own file
/// names rather than being spelled out in code.
/// </remarks>
public static class IconTaxonomy
{
    /// <summary>Kinds whose second token names the character it belongs to.</summary>
    private static readonly Dictionary<string, string> CharacterKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = "Powers",
        ["costume"] = "Costumes",
        ["herohor"] = "Portraits",
        ["portrait"] = "Portraits",
        ["travelpower"] = "Travel powers",
        ["drop"] = "Drop art",
        ["armor"] = "Gear",
        ["achievement"] = "Achievements",
    };

    /// <summary>The same kinds, spelled with the team-up suffix the names use.</summary>
    private static readonly Dictionary<string, string> TeamUpKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["teamup"] = "Powers",
        ["powerteamup"] = "Powers",
        ["costumeteamup"] = "Costumes",
        ["herohorteamup"] = "Portraits",
    };

    /// <summary>Everything that is not per-character, and where it belongs.</summary>
    private static readonly Dictionary<string, (string Group, string Section)> FixedKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["item"] = ("Items", "Items"),
            ["relic"] = ("Items", "Relics"),
            ["craft"] = ("Items", "Crafting"),
            ["inventory"] = ("Items", "Inventory"),
            ["boost"] = ("Items", "Boosts"),
            ["boosts"] = ("Items", "Boosts"),
            ["currency"] = ("Items", "Currency"),
            ["slot"] = ("Items", "Slots"),

            ["store"] = ("Store", "Store"),

            ["omega"] = ("Progression", "Omega"),
            ["infinity"] = ("Progression", "Infinity"),
            ["infinitysystem"] = ("Progression", "Infinity"),
            ["talent"] = ("Progression", "Talents"),
            ["metagame"] = ("Progression", "Metagame"),
            ["daily"] = ("Progression", "Daily"),
            ["pvp"] = ("Progression", "Versus"),

            ["waypoint"] = ("World", "Waypoints"),
            ["waypoints"] = ("World", "Waypoints"),
            ["chapter"] = ("World", "Chapters"),
            ["terminal"] = ("World", "Terminals"),
            ["locationimage"] = ("World", "Locations"),
            ["loadingscreen"] = ("World", "Loading screens"),
            ["motioncomic"] = ("World", "Motion comics"),

            ["icon"] = ("Interface", "General"),
            ["stat"] = ("Interface", "Stats"),
            ["edgepointericon"] = ("Interface", "Edge pointers"),
            ["buff"] = ("Interface", "Buffs"),
            ["debuff"] = ("Interface", "Debuffs"),
            ["ps4"] = ("Interface", "Controller"),
            ["x360"] = ("Interface", "Controller"),
            ["console"] = ("Interface", "Controller"),
            ["tutorial"] = ("Interface", "Tutorial"),
            ["rosetta"] = ("Interface", "Rosetta"),
            ["rosettaicons"] = ("Interface", "Rosetta"),
            ["icons"] = ("Interface", "General"),
            ["notif"] = ("Interface", "Notifications"),
            ["controller"] = ("Interface", "Controller"),
            ["talents"] = ("Progression", "Talents"),
            ["alternate"] = ("Progression", "Alternate advancement"),
            ["dangerroom"] = ("World", "Scenarios"),
            ["mapimage"] = ("World", "Maps"),
            ["hub"] = ("World", "Hubs"),
            ["quest"] = ("World", "Quests"),
            ["difficultymode"] = ("World", "Difficulty"),
            ["loading"] = ("World", "Loading screens"),
            ["specialization"] = ("Progression", "Specialisations"),
            ["prestige"] = ("Progression", "Prestige"),
            ["radial"] = ("Interface", "Radial menu"),
            ["ingredient"] = ("Items", "Crafting"),
            ["medallion"] = ("Items", "Items"),
            ["medalstage"] = ("Items", "Items"),
            ["minimapmarker"] = ("World", "Map markers"),
            ["gifting"] = ("Store", "Gifting"),
            ["waypointicons"] = ("World", "Waypoints"),
        };

    /// <summary>
    /// Kind tokens the data spells more than one way. Plurals and two
    /// misspellings that ship in the client, mapped to the spelling the
    /// tables above use so they sort with their own kind.
    /// </summary>
    private static readonly Dictionary<string, string> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["achievment"] = "achievement",
        ["achievemen"] = "achievement",
        ["dropicon"] = "drop",
        ["powers"] = "power",
        ["costumes"] = "costume",
        ["items"] = "item",
        ["relics"] = "relic",
    };

    /// <summary>
    /// Tokens that look like a character because they sit in the subject slot,
    /// but name a kind of content instead.
    /// </summary>
    private static readonly HashSet<string> NotCharacters =
        new(StringComparer.OrdinalIgnoreCase) { "teamup", "boss", "armor", "ph", "specialization", "generic" };

    /// <summary>
    /// Works out which subjects are characters, from the names themselves.
    /// A token counts when it appears under two different per-character kinds,
    /// or carries a spread of powers nothing else would have.
    /// </summary>
    public static HashSet<string> BuildRoster(IEnumerable<string> objectNames)
    {
        var kindsSeen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var powerCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in objectNames)
        {
            string[] parts = name.Split('_');
            if (parts.Length < 3) continue;
            if (!CharacterKinds.ContainsKey(parts[0])) continue;

            string subject = parts[1];

            if (!kindsSeen.TryGetValue(subject, out HashSet<string>? kinds))
                kindsSeen[subject] = kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            kinds.Add(parts[0]);

            if (parts[0].Equals("power", StringComparison.OrdinalIgnoreCase))
                powerCount[subject] = powerCount.GetValueOrDefault(subject) + 1;
        }

        var roster = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string subject, HashSet<string> kinds) in kindsSeen)
        {
            if (NotCharacters.Contains(subject)) continue;

            if (kinds.Count >= 2 || powerCount.GetValueOrDefault(subject) >= 5)
                roster.Add(subject);
        }

        return roster;
    }

    /// <summary>
    /// The path an icon belongs at, outermost first - Heroes, then the
    /// character, then what the icon is.
    /// </summary>
    public static string[] Classify(string objectName, IconSubjects subjects, IDisplayNames display)
    {
        string[] parts = objectName.Split('_');
        string kind = parts[0];
        string rest = parts.Length > 1 ? string.Join('_', parts.Skip(1)) : string.Empty;

        if (KindAliases.TryGetValue(kind, out string? canonical)) kind = canonical;

        // A few names repeat the kind before the subject. Step over the repeat
        // so the subject is read from where it actually is.
        if (parts.Length > 2 && parts[1].Equals(kind, StringComparison.OrdinalIgnoreCase))
        {
            parts = parts.Skip(1).ToArray();
            rest = string.Join('_', parts.Skip(1));
        }

        // A kind run together with its subject, as in the costume icons that
        // spell the character into the first token instead of separating it.
        if (!CharacterKinds.ContainsKey(kind) && !FixedKinds.ContainsKey(kind))
        {
            foreach ((string gluedKind, string gluedSection) in CharacterKinds)
            {
                if (kind.Length <= gluedKind.Length) continue;
                if (!kind.StartsWith(gluedKind, StringComparison.OrdinalIgnoreCase)) continue;

                string? gluedWho = MatchSubject(kind[gluedKind.Length..], subjects);
                if (gluedWho is not null) return Place(gluedWho, gluedSection, subjects, display);
            }
        }

        if (TeamUpKinds.TryGetValue(kind, out string? teamSection))
        {
            string? who = MatchSubject(rest, subjects);

            // The same slot carries the bonus a team-up grants - a stat name,
            // with no character behind it.
            if (who is null)
                return ["Team-Ups", "Bonuses"];

            return ["Team-Ups", display.For(subjects.Canonical(who)), teamSection];
        }

        if (CharacterKinds.TryGetValue(kind, out string? section))
        {
            // The team-up marker can sit in the subject slot, with the
            // character named after it: a team-up's costume or drop art.
            if (parts.Length > 2 && parts[1].Equals("teamup", StringComparison.OrdinalIgnoreCase))
            {
                string? teamWho = MatchSubject(string.Join('_', parts.Skip(2)), subjects);

                return teamWho is null
                    ? ["Team-Ups", "General", section]
                    : ["Team-Ups", display.For(subjects.Canonical(teamWho)), section];
            }

            string? who = MatchSubject(rest, subjects);
            if (who is not null) return Place(who, section, subjects, display);

            // No character, but the slot where one would sit names something
            // this does know - a power icon for a specialisation, say. That
            // placement beats the catch-all.
            if (parts.Length > 2
                && FixedKinds.TryGetValue(parts[1], out (string Group, string Section) named))
                return [named.Group, named.Section];

            // A character kind with no character in it: still better placed by
            // what it is than dropped into the catch-all.
            if (kind.Equals("achievement", StringComparison.OrdinalIgnoreCase))
                return ["Progression", "Achievements"];

            return ["Other", section];
        }

        // Names that open with the character instead of the kind.
        string? opener = Known(kind, subjects);
        if (opener is not null) return Place(opener, "Other", subjects, display);

        // A character glued to the front of a longer word, no separator.
        if (!FixedKinds.ContainsKey(kind))
        {
            string? leading = MatchSubject(objectName, subjects);
            if (leading is not null) return Place(leading, "Other", subjects, display);
        }

        if (FixedKinds.TryGetValue(kind, out (string Group, string Section) fixedAt))
            return [fixedAt.Group, fixedAt.Section];

        return ["Other", Capitalise(kind)];
    }

    /// <summary>Files a subject under the side of the tree it belongs to.</summary>
    private static string[] Place(string subject, string section, IconSubjects subjects, IDisplayNames display)
    {
        string canonical = subjects.Canonical(subject);
        string group = subjects.TeamUps.Contains(canonical) ? "Team-Ups" : "Heroes";

        return [group, display.For(canonical), section];
    }

    /// <summary>The subject a token names, or null.</summary>
    private static string? Known(string token, IconSubjects subjects)
    {
        if (NotCharacters.Contains(token)) return null;

        return subjects.Heroes.Contains(token) || subjects.TeamUps.Contains(token) ? token : null;
    }

    /// <summary>
    /// The character a name is about, or null. Handles both the separated form
    /// and the run-together one, preferring the longest match so a longer name
    /// is never mistaken for a shorter one it happens to start with.
    /// </summary>
    private static string? MatchSubject(string rest, IconSubjects subjects)
    {
        if (rest.Length == 0) return null;

        string head = rest.Split('_')[0];

        string? direct = Known(head, subjects);
        if (direct is not null) return direct;

        string? best = null;

        foreach (string candidate in subjects.Heroes.Concat(subjects.TeamUps))
        {
            if (NotCharacters.Contains(candidate)) continue;
            if (head.Length <= candidate.Length) continue;
            if (!head.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || candidate.Length > best.Length) best = candidate;
        }

        return best ?? subjects.KnownVariant(head) ?? subjects.NearMiss(head);
    }

    internal static string Capitalise(string token)
        => token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token[1..];
}

/// <summary>Turns a run-together lowercase subject into something readable.</summary>
public interface IDisplayNames
{
    string For(string subject);

    /// <summary>
    /// Whether the client itself spells this subject somewhere, rather than the
    /// name being a fallback. A spelling the client uses is the one to trust
    /// when two spellings of one subject are folded together.
    /// </summary>
    bool IsSpelledByClient(string subject);
}
