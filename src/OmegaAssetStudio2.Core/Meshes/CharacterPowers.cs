using System.Text;
using OmegaAssetStudio2.Core.Calligraphy;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Meshes;

/// <summary>One skill a character has, and the files that make it look the way it does.</summary>
public sealed record PowerEntry
{
    /// <summary>The character this belongs to, spelled as package names spell it.</summary>
    public required string CharacterToken { get; init; }

    /// <summary>The skill's name as package names spell it.</summary>
    public required string Token { get; init; }

    /// <summary>The skill's name, spaced out to read as words.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The package named for this skill.</summary>
    public required string PackagePath { get; init; }

    /// <summary>
    /// Other packages that carry parts of this skill's appearance — the effects
    /// it applies, the things it throws, and what it leaves behind.
    /// </summary>
    /// <remarks>
    /// A skill's colour is very often not in the package named after it. Missing
    /// these is the difference between finding a handful of colours and finding
    /// all of them.
    /// </remarks>
    public required IReadOnlyList<string> RelatedPackages { get; init; }

    /// <summary>
    /// The costume this skill belongs to, empty when every costume shares it.
    /// </summary>
    /// <remarks>
    /// A few skills are shipped once per costume - one melee skill has a
    /// SuperSoldier and a TheCaptain of its own beside the
    /// plain one - and those are the only ones that can be recoloured for a
    /// single costume. In 1.52.0.1700 that is 77 packages across 37 of the
    /// game's 514 costumes; everything else is one file that every costume of
    /// that character reads, so a colour changed in it changes all of them.
    /// </remarks>
    public string Costume { get; init; } = string.Empty;

    /// <summary>Every package worth searching for this skill's colours.</summary>
    public IReadOnlyList<string> AllPackages => [PackagePath, .. RelatedPackages];

    /// <summary>
    /// Whether the package is named after this character, rather than after
    /// what it does with the character added to it.
    /// </summary>
    /// <remarks>
    /// UC__PowerThor_StormHammerThrow_SF is that character's own;
    /// UC__PowerBoomerangThrow_Thor_SF is the game's boomerang-throw power with a
    /// version made for them. Both hold their effects and both are worth
    /// recolouring, but only the first kind lines up with what the power tree
    /// shows, and listing them together makes it look as though they have skills
    /// they have never had.
    /// </remarks>
    public bool Own { get; init; } = true;

    /// <summary>
    /// Whether the game's own data says this character has this power: true or
    /// false where the character's definition could be read, and null where it
    /// could not.
    /// </summary>
    /// <remarks>
    /// Null is not the same as false and must not be shown as though it were.
    /// Only the playable characters have a definition of this kind; every
    /// enemy, boss and team-up has none, and saying their skills are not in
    /// their power tree would be inventing a fact about them.
    /// </remarks>
    public bool? InTree { get; init; }

    /// <summary>
    /// The picture the game shows for this power, as package and texture
    /// together, or empty where there is none to show.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    public string Subtitle
    {
        get
        {
            string packages = RelatedPackages.Count == 0
                ? "1 package"
                : $"{RelatedPackages.Count + 1} packages";

            if (InTree == false) return packages + " — not in their power tree";

            // A power the game says they have is described by what it is, not
            // by how its file happens to be named. One ground-slam power is in
            // its owner's tree and its package is the shared one; that it is
            // shared is beside the point once the game has said whose it is.
            if (InTree != true && !Own) return packages + " — a shared power, their version of it";

            return Costume.Length == 0
                ? packages + " — shared by every costume"
                : packages + $" — {DisplayNames.Humanise(Costume)} only";
        }
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Lists the skills a character has, by the packages the game ships for them.
/// </summary>
/// <remarks>
/// Nothing in the cooked data enumerates a character's skills, but the game
/// names their packages after them, so the list is recoverable from file names
/// alone — and recoverable instantly, which matters for a panel that fills as
/// soon as somebody is picked.
/// </remarks>
public static class CharacterPowers
{
    // Cooked package names. Data, matched literally against shipped files.
    private const string PowerPrefix = "UC__Power";
    private const string Suffix = "_SF";

    /// <summary>Below this a name matches too much to mean anything.</summary>
    private const int ShortestToken = 4;

    /// <summary>
    /// Prefixes of the packages that carry the rest of a skill's appearance.
    /// </summary>
    private static readonly string[] EffectPrefixes =
    [
        "UC__MarvelConditionEffect_",
        "UC__MarvelProjectile_",
        "UC__MarvelEntity_",
        "UC__ItemPower",
    ];

    /// <summary>
    /// Words the game groups by rather than names anybody by.
    /// </summary>
    /// <remarks>
    /// TeamUp is the game's own category word — it is the middle of
    /// UC__MarvelTeamUp_, which is how every team-up's model package is named.
    /// It also turns up as though it were a character, in three agent packages
    /// for the demons one team-up summons, and left standing it took all 592 of
    /// 1.53.0.203's UC__PowerTeamUp_* packages for itself while every one of the
    /// 52 real team-ups showed nothing at all.
    /// </remarks>
    private static readonly string[] GroupWords = ["TeamUp"];

    /// <summary>Who owns each power package, one entry per cooked folder.</summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, List<PowerEntry>>> Owned =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Lock = new();

    /// <summary>Lists a character's skills.</summary>
    public static IReadOnlyList<PowerEntry> Build(GameClient client, string characterToken)
    {
        if (characterToken.Length == 0 || !Directory.Exists(client.CookedPath)) return [];

        IReadOnlyDictionary<string, List<PowerEntry>> owned = ForFolder(client);

        return owned.TryGetValue(characterToken, out List<PowerEntry>? mine) ? mine : [];
    }

    /// <summary>Forgets what was read, for a folder whose files have changed.</summary>
    public static void Forget()
    {
        lock (Lock) Owned.Clear();
    }

    private static IReadOnlyDictionary<string, List<PowerEntry>> ForFolder(GameClient client)
    {
        lock (Lock)
        {
            if (Owned.TryGetValue(client.CookedPath, out IReadOnlyDictionary<string, List<PowerEntry>>? already))
                return already;
        }

        IReadOnlyDictionary<string, List<PowerEntry>> found = Attribute(client);

        lock (Lock) Owned[client.CookedPath] = found;

        return found;
    }

    /// <summary>
    /// Gives every power package to the character it names.
    /// </summary>
    /// <remarks>
    /// To the character it names, not the one it starts with. The game writes a
    /// skill's package name with the character wherever it reads best:
    /// UC__PowerCaptainAmerica_BroadStrike_SF has it first,
    /// UC__PowerDefaultAttack_BlackPanther_SF has it second, and
    /// UC__PowerTeamUp_Drax_Whirlwind_SF puts a group word in front of it.
    /// Reading the first position alone found 3,610 of 1.53.0.203's 5,072 power
    /// packages and left 412 of its 600 characters showing nothing at all.
    /// <para>
    /// Where several characters are named, the longest wins: one character's
    /// token can sit wholly inside another's, and only one of them is in the
    /// package name on purpose. Ties go to whichever is named first, so a skill that names
    /// its owner and then somebody it hits stays with its owner.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, List<PowerEntry>> Attribute(GameClient client)
    {
        var owned = new Dictionary<string, List<PowerEntry>>(StringComparer.OrdinalIgnoreCase);

        string[] tokens = CharacterRoster.Build(client)
            .Select(entry => entry.Token)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0) return owned;

        // Read once and shared, because every skill asks the same question of
        // the same list.
        List<string> effects = EffectPackages(client.CookedPath);

        IReadOnlyDictionary<string, string[]> costumes = Costumes(client);

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, PowerPrefix + "*" + Suffix + ".upk"))
        {
            string middle = Middle(Path.GetFileNameWithoutExtension(path));
            if (middle.Length == 0) continue;

            // The group words come off before anybody is looked for, so that
            // a team-up's package reads as its character rather than as nothing.
            string bare = middle;
            foreach (string word in GroupWords) bare = Without(bare, word);

            string[] pieces = bare.Split('_', StringSplitOptions.RemoveEmptyEntries);

            string? owner = null;
            int longest = 0;
            int earliest = int.MaxValue;

            foreach (string token in tokens)
            {
                int at = Named(pieces, token);
                if (at < 0) continue;

                if (token.Length > longest || (token.Length == longest && at < earliest))
                {
                    owner = token;
                    longest = token.Length;
                    earliest = at;
                }
            }

            if (owner is null) continue;

            string skill = SkillToken(middle, owner);
            if (skill.Length == 0) skill = middle;

            string costume = CostumeOf(skill, costumes.GetValueOrDefault(owner, []));

            // Both answers come from the package itself: a power says which
            // cooked package holds its art, so the file on disk is matched to
            // the definition rather than to a spelling of its name.
            string stem = Path.GetFileNameWithoutExtension(path);

            IReadOnlySet<string> tree = PowerTree.TreePackagesFor(client, owner);
            bool? inTree = tree.Count == 0 ? null : tree.Contains(stem);

            if (!owned.TryGetValue(owner, out List<PowerEntry>? mine)) owned[owner] = mine = [];

            mine.Add(new PowerEntry
            {
                CharacterToken = owner,
                Token = skill,
                DisplayName = DisplayNames.Humanise(skill),
                Costume = costume,
                Own = Path.GetFileNameWithoutExtension(path)
                          .StartsWith(PowerPrefix + owner + "_", StringComparison.OrdinalIgnoreCase),
                InTree = inTree,
                Icon = PowerTree.IconForPowerPackage(client, path),
                PackagePath = path,
                RelatedPackages = Related(effects, skill, owner),
            });
        }

        foreach (List<PowerEntry> mine in owned.Values)
            mine.Sort((a, b) =>
            {
                int mineFirst = Rank(a).CompareTo(Rank(b));

                return mineFirst != 0
                    ? mineFirst
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

        Inherit(tokens, owned);

        return owned;
    }

    /// <summary>
    /// Gives a variant the skills of the character it is a variant of.
    /// </summary>
    /// <remarks>
    /// The game ships a second edition of some characters - DraxVol2 beside
    /// Drax, FirestarYellow beside Firestar - as a model package of its own with
    /// no skills of its own, because it uses the first one's. Nothing names the
    /// pairing, but the name says it: a variant's token starts with the token it
    /// is a variant of. Matched against the longest such token, so
    /// IronmanMark2Override lands on IronmanMark2 rather than on Ironman.
    /// </remarks>
    private static void Inherit(string[] tokens, Dictionary<string, List<PowerEntry>> owned)
    {
        foreach (string token in tokens)
        {
            if (owned.ContainsKey(token)) continue;

            string? based = null;

            foreach (string other in tokens)
            {
                if (other.Length >= token.Length) continue;
                if (other.Length < ShortestToken) continue;
                if (!owned.ContainsKey(other)) continue;
                if (!token.StartsWith(other, StringComparison.OrdinalIgnoreCase)) continue;

                if (based is null || other.Length > based.Length) based = other;
            }

            if (based is not null) owned[token] = owned[based];
        }
    }

    /// <summary>
    /// Where a character is named in a package's name, or -1 if they are not.
    /// </summary>
    /// <remarks>
    /// A name has to begin a piece of the package's name. Anywhere else it is
    /// part of somebody else's: one character's token sits inside two longer
    /// tokens that name two other characters entirely, and matching it there
    /// handed that character three skills that are not theirs and that nobody
    /// could find in their power tree.
    /// <para>
    /// Beginning a piece, rather than being the whole of one, because the game
    /// glues a name onto what it does as readily as it separates them -
    /// ThorMedallionLightningStrike is one piece and it belongs to that character.
    /// </para>
    /// </remarks>
    private static int Named(string[] pieces, string token)
    {
        int at = 0;

        foreach (string piece in pieces)
        {
            if (piece.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return at;

            at += piece.Length;
        }

        return -1;
    }

    /// <summary>What order a skill is listed in: in the tree, then their own, then the rest.</summary>
    private static int Rank(PowerEntry power) => power.InTree switch
    {
        true => 0,
        false => 2,
        _ => power.Own ? 0 : 1,
    };

    /// <summary>Which costumes each character has, as package names spell them.</summary>
    private static IReadOnlyDictionary<string, string[]> Costumes(GameClient client)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (RosterEntry entry in CharacterRoster.Build(client))
        {
            if (entry.VariantToken.Length == 0) continue;

            if (!found.TryGetValue(entry.Token, out List<string>? mine)) found[entry.Token] = mine = [];

            if (!mine.Contains(entry.VariantToken, StringComparer.OrdinalIgnoreCase))
                mine.Add(entry.VariantToken);
        }

        // Longest first, so a costume whose name contains another's is tested
        // before the shorter one can claim the skill.
        return found.ToDictionary(
            p => p.Key,
            p => p.Value.OrderByDescending(v => v.Length).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The costume a skill belongs to, or nothing where every costume shares it.
    /// </summary>
    /// <remarks>
    /// Named at the end and nowhere else: BroadStrike_SuperSoldier is the
    /// SuperSoldier costume's, and a skill that merely mentions a costume
    /// somewhere in the middle of its name is not.
    /// </remarks>
    private static string CostumeOf(string skill, string[] costumes)
    {
        foreach (string costume in costumes)
        {
            if (skill.Length <= costume.Length + 1) continue;

            if (skill.EndsWith("_" + costume, StringComparison.OrdinalIgnoreCase)) return costume;
        }

        return string.Empty;
    }

    /// <summary>Every effect package in the folder, read once for all of them.</summary>
    private static List<string> EffectPackages(string cookedPath)
    {
        var effects = new List<string>();

        foreach (string prefix in EffectPrefixes)
            effects.AddRange(Directory.EnumerateFiles(cookedPath, prefix + "*" + Suffix + ".upk"));

        return effects;
    }

    /// <summary>
    /// Picks the effect packages that belong to one skill.
    /// </summary>
    /// <remarks>
    /// Matched on the skill's name appearing in the package name, with the
    /// underscores taken out first: the same skill is written
    /// <c>BlackWidow_RapidShot</c> in one place and <c>BlackWidowRapidShot</c>
    /// in another, and a plain comparison misses half of them. The character has
    /// to be named as well, so that two people who each have a skill called
    /// Flight do not collect one another's effects.
    /// </remarks>
    private static IReadOnlyList<string> Related(List<string> effects, string skillToken, string characterToken)
    {
        string wanted = Squashed(skillToken);
        if (wanted.Length < ShortestToken) return [];   // too short to match on without dragging in the unrelated

        string who = Squashed(characterToken);
        var related = new List<string>();

        foreach (string path in effects)
        {
            string stem = Squashed(Path.GetFileNameWithoutExtension(path));

            if (stem.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                && stem.Contains(who, StringComparison.OrdinalIgnoreCase))
            {
                related.Add(path);
            }
        }

        return related;
    }

    private static string Squashed(string value) => value.Replace("_", string.Empty);

    /// <summary>The name with the prefix and the suffix taken off it.</summary>
    private static string Middle(string stem)
    {
        if (!stem.StartsWith(PowerPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string middle = stem[PowerPrefix.Length..];

        if (middle.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            middle = middle[..^Suffix.Length];

        return middle.Trim('_');
    }

    /// <summary>
    /// What is left of the name once the character and the group words are taken
    /// out of it, which is the skill.
    /// </summary>
    /// <remarks>
    /// Taken out wherever it sits rather than off the front, because it does not
    /// always sit at the front. The underscores are kept, so that
    /// DefaultAttack_BlackPanther reads as Default Attack afterwards and not as
    /// one run-on word.
    /// </remarks>
    private static string SkillToken(string middle, string characterToken)
    {
        string left = Without(middle, characterToken);

        foreach (string word in GroupWords) left = Without(left, word);

        return left.Trim('_');
    }

    /// <summary>Takes one word out of a name, matching it across the underscores.</summary>
    private static string Without(string name, string word)
    {
        string wanted = Squashed(word);
        if (wanted.Length == 0) return name;

        // Where each letter of the squashed name came from, so a match found
        // without the underscores can be cut out of the name that has them.
        var from = new List<int>(name.Length);
        var flat = new StringBuilder(name.Length);

        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '_') continue;

            flat.Append(name[i]);
            from.Add(i);
        }

        int at = flat.ToString().IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return name;

        int first = from[at];
        int last = from[at + wanted.Length - 1];

        return (name[..first] + "_" + name[(last + 1)..]).Replace("__", "_");
    }
}
