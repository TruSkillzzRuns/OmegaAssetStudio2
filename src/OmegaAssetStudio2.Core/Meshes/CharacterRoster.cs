using System.Text;
using System.Text.RegularExpressions;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>Which part of the cast an entry belongs to.</summary>
public enum RosterCategory
{
    Hero,
    TeamUp,
    Boss,
    Enemy,
}

/// <summary>One selectable character, and the package its models live in.</summary>
public sealed record RosterEntry
{
    public required RosterCategory Category { get; init; }
    public required string PackagePath { get; init; }

    /// <summary>The character, without the costume.</summary>
    public required string Character { get; init; }

    /// <summary>
    /// The character's name exactly as package names spell it, with no spaces
    /// added. Everything else the game ships for this character is named after
    /// this, so it is what other lookups match on.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>The costume or variant, empty for the default one.</summary>
    public required string Variant { get; init; }

    /// <summary>
    /// The costume exactly as package names spell it, with no spaces added.
    /// </summary>
    /// <remarks>
    /// Kept beside the readable one because everything else the game ships for
    /// a costume is named after this. A few costumes have skills of their own -
    /// UC__PowerCaptainAmerica_BroadStrike_SuperSoldier_SF beside
    /// UC__PowerCaptainAmerica_BroadStrike_SF - and this is what says which.
    /// </remarks>
    public string VariantToken { get; init; } = string.Empty;

    /// <summary>What to show in the list.</summary>
    public string DisplayName => Variant.Length == 0
        ? Character
        : $"{Character} — {Variant}";

    public string Subtitle => Variant.Length == 0 ? "Default" : Variant;

    public bool Matches(string query) =>
        Character.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Variant.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Builds the list of characters a client ships, grouped so they can be browsed.
/// </summary>
/// <remarks>
/// The cast is not catalogued anywhere in the cooked data — it is inferred from
/// package names, which follow a strict convention. Reading names is also what
/// makes the panel appear instantly: opening twelve thousand packages to ask
/// each one what it holds would take minutes, and the name already says.
/// </remarks>
public static class CharacterRoster
{
    // Cooked package names. These are data, matched literally against files the
    // game shipped, and cannot be renamed.
    private const string HeroPattern = "UC__MarvelPlayer_*_SF.upk";
    private const string TeamUpPattern = "UC__MarvelTeamUp_*_SF.upk";
    private const string AgentPattern = "UC__MarvelAgent_*_SF.upk";

    private const string HeroPrefix = "UC__MarvelPlayer_";
    private const string TeamUpPrefix = "UC__MarvelTeamUp_";
    private const string AgentPrefix = "UC__MarvelAgent_";
    private const string Suffix = "_SF";

    /// <summary>
    /// Packages named for something that has no character in it: effects,
    /// spawn points, triggers, and other machinery that would otherwise pad the
    /// list with entries that load nothing.
    /// </summary>
    private static readonly string[] EmptyOfCharacters =
    [
        "Orb", "SpawnIn", "Marker", "invisMortar", "VFX", "NULL", "Empty",
        "Trigger", "Cyclone", "Hotspot", "Trap", "LootJackpot", "DamageEntity",
        "OneShot", "Spawner", "EnvironmentalDamage",
    ];

    /// <summary>
    /// Support actors that are not enemies people fight: helpers, summons,
    /// civilians, scenery, and the one-off actors a single mission spawns.
    /// </summary>
    private static readonly string[] NotAnOpponent =
    [
        "MarkerAgent", "Affix_", "Spawn_Teleport", "DropIn", "_FX_",
        "Civilian", "Door", "Marker", "Hostage", "Pet_", "Summon", "Decoy",
    ];

    private static readonly Regex MissionOneOff =
        new(@"Unique\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads every category. Safe to call off the interface thread.</summary>
    public static IReadOnlyList<RosterEntry> Build(GameClient client)
    {
        var entries = new List<RosterEntry>();

        foreach (RosterCategory category in Enum.GetValues<RosterCategory>())
            entries.AddRange(Build(client, category));

        return entries;
    }

    /// <summary>Reads one category, so a panel can fill a section at a time.</summary>
    public static IReadOnlyList<RosterEntry> Build(GameClient client, RosterCategory category)
    {
        if (!Directory.Exists(client.CookedPath)) return [];

        (string pattern, string prefix) = category switch
        {
            RosterCategory.Hero => (HeroPattern, HeroPrefix),
            RosterCategory.TeamUp => (TeamUpPattern, TeamUpPrefix),
            _ => (AgentPattern, AgentPrefix),
        };

        var entries = new List<RosterEntry>();

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, pattern))
        {
            string stem = Path.GetFileNameWithoutExtension(path);

            if (!Wanted(category, stem)) continue;

            (string character, string variant, string token, string variantToken) = SplitName(stem, prefix);
            if (character.Length == 0) continue;

            entries.Add(new RosterEntry
            {
                Category = category,
                PackagePath = path,
                Character = character,
                Variant = variant,
                VariantToken = variantToken,
                Token = token,
            });
        }

        return entries
            .OrderBy(e => e.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Variant, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Wanted(RosterCategory category, string stem)
    {
        if (Contains(stem, EmptyOfCharacters)) return false;

        // Both bosses and ordinary enemies are agents; the name is the only
        // thing that separates them, so each category takes its half.
        bool boss = stem.Contains("Boss", StringComparison.OrdinalIgnoreCase);

        return category switch
        {
            RosterCategory.Boss => boss,
            RosterCategory.Enemy => !boss && !Contains(stem, NotAnOpponent) && !MissionOneOff.IsMatch(stem),
            _ => true,
        };
    }

    /// <summary>
    /// Words the game groups by rather than names anybody by.
    /// </summary>
    /// <remarks>
    /// TeamUp is the game's own category word: it is the middle of
    /// UC__MarvelTeamUp_, which is how every team-up's model package is named.
    /// </remarks>
    private static readonly string[] GroupWords = ["TeamUp"];

    private static bool IsGroupWord(string token)
    {
        foreach (string word in GroupWords)
        {
            if (token.Equals(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool Contains(string stem, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (stem.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Splits a package name into character and costume. The convention is a
    /// prefix, the character, an optional variant, and a suffix; the first
    /// underscore after the prefix separates the two.
    /// </summary>
    private static (string Character, string Variant, string Token, string VariantToken) SplitName(
        string stem, string prefix)
    {
        string middle = stem;

        if (middle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            middle = middle[prefix.Length..];

        if (middle.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            middle = middle[..^Suffix.Length];

        int split = middle.IndexOf('_');

        string token = split < 0 ? middle : middle[..split];

        // A word the game groups by is not somebody's name. Three agent
        // packages are named UC__MarvelAgent_Teamup_<demon>_SF, for the demons
        // one team-up summons, and read as a character called Teamup that row
        // collected every team-up's skills - all 592 of them - while the 52
        // real team-ups showed none.
        if (split >= 0 && IsGroupWord(token))
        {
            int next = middle.IndexOf('_', split + 1);

            token = next < 0
                ? middle.Replace("_", string.Empty)
                : middle[..next].Replace("_", string.Empty);

            split = next;
        }

        return split < 0
            ? (DisplayNames.Humanise(middle), string.Empty, token, string.Empty)
            : (DisplayNames.Humanise(token), DisplayNames.Humanise(middle[(split + 1)..]), token,
               middle[(split + 1)..]);
    }
}
