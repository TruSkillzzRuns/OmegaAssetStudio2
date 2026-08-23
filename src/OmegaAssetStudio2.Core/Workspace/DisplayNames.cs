using System.Text;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// Turns the names the game uses for its files into names a person reads.
/// </summary>
/// <remarks>
/// The game's own display text lives in its localisation files, keyed by a hash
/// whose mapping into the string index is not decoded. Until it is, every name
/// shown in this application is derived from the name of the file the content
/// sits in, and this is the one place that derivation happens — so when the
/// localisation lookup is solved, there is a single place to change.
/// <para>
/// The aim is the name as it is written in the game: words separated, small
/// words left lowercase, and the abbreviations the file names use spelled out.
/// </para>
/// </remarks>
public static class DisplayNames
{
    /// <summary>
    /// Words left lowercase inside a name, the way a title is normally written.
    /// Never applied to the first word.
    /// </summary>
    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into",
        "nor", "of", "on", "onto", "or", "over", "the", "to", "up", "with",
    };

    /// <summary>
    /// Shorthand the file names use, and what it stands for. Matched on a whole
    /// word only, so a name that merely contains these letters is left alone.
    /// </summary>
    private static readonly Dictionary<string, string> Expanded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vfx"] = "Effect",
        ["Fx"] = "Effect",
        ["Anim"] = "Animation",
        ["Alt"] = "Alternate",
        ["Dmg"] = "Damage",
        ["Proj"] = "Projectile",
        ["Aoe"] = "Area",
        ["Ult"] = "Ultimate",
        ["Hp"] = "Health",
        ["Def"] = "Defence",
        ["Atk"] = "Attack",
        ["Mat"] = "Material",
        ["Tex"] = "Texture",
    };

    /// <summary>Words kept in capitals, because that is how they are read.</summary>
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "AI", "AIM", "HP", "NPC", "UI", "XP", "PVP", "PVE", "LOD", "HUD",
    };

    /// <summary>
    /// Reads a run-together file-name token as words.
    /// </summary>
    /// <example>
    /// <c>WinterPatrol</c> becomes <c>Winter Patrol</c>;
    /// <c>RapidShot_MissileEffect</c> becomes <c>Rapid Shot Missile Effect</c>.
    /// </example>
    public static string Humanise(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;

        List<string> words = Split(token);
        if (words.Count == 0) return string.Empty;

        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];

            if (Expanded.TryGetValue(word, out string? full)) word = full;

            if (Acronyms.Contains(word))
            {
                words[i] = word.ToUpperInvariant();
                continue;
            }

            // The first word always starts the name, so it is never lowered
            // however small it is: "Of Death" is a name, "of Death" is not.
            words[i] = i > 0 && SmallWords.Contains(word)
                ? word.ToLowerInvariant()
                : Capitalise(word);
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Reads a character and an optional costume as one name.
    /// </summary>
    public static string Humanise(string characterToken, string variantToken)
    {
        string character = Humanise(characterToken);
        string variant = Humanise(variantToken);

        return variant.Length == 0 ? character : $"{character} — {variant}";
    }

    /// <summary>
    /// Breaks a token into words at underscores, at case changes, and where
    /// digits meet letters.
    /// </summary>
    /// <remarks>
    /// A run of capitals is kept together up to the last one, so
    /// <c>AIMTrooper</c> reads as "AIM Trooper" rather than "A I M Trooper".
    /// </remarks>
    private static List<string> Split(string token)
    {
        var words = new List<string>();
        var word = new StringBuilder(token.Length);

        void Flush()
        {
            if (word.Length > 0) words.Add(word.ToString());
            word.Clear();
        }

        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];

            if (c is '_' or '-' or ' ')
            {
                Flush();
                continue;
            }

            if (word.Length > 0)
            {
                char previous = token[i - 1];

                // A capital after one of these is part of the same surname, not
                // the start of a new word. There is no rule that separates
                // "McCoy" from "DeathLok" — both are a capital mid-token — so
                // the prefixes that behave this way are named outright.
                bool surnamePrefix = word.ToString() is "Mc" or "Mac" or "O'";

                bool startsAWord = !surnamePrefix &&
                    (char.IsUpper(c) && !char.IsUpper(previous)) ||
                    (char.IsDigit(c) != char.IsDigit(previous)) ||
                    // The last capital of a run belongs to the word after it.
                    (char.IsUpper(c) && char.IsUpper(previous) &&
                     i + 1 < token.Length && char.IsLower(token[i + 1]));

                if (startsAWord) Flush();
            }

            word.Append(c);
        }

        Flush();
        return words;
    }

    private static string Capitalise(string word) => word.Length switch
    {
        0 => word,
        1 => word.ToUpperInvariant(),

        // Left alone when it is already mixed case: names like "McCoy" and
        // "DeathLok" are spelled that way on purpose.
        _ when word.Skip(1).Any(char.IsUpper) => word,
        _ => char.ToUpperInvariant(word[0]) + word[1..],
    };
}
