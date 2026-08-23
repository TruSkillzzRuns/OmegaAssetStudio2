namespace OmegaAssetStudio2.Core.Audio;

/// <summary>What a sound is for.</summary>
public enum SoundKind
{
    PowerEffect,
    OtherEffect,
    CombatVoice,
    PowerVoice,
    Banter,
    Emote,
    StoryVoice,
    StatusVoice,
    OtherVoice,
    Unnamed,
}

/// <summary>
/// Sorts sounds by what they are for, using the names recovered for them.
/// </summary>
/// <remarks>
/// The names are laid out in parts, and the shape was taken from the names
/// themselves rather than assumed. Across seven of this game's containers:
/// the first part is <c>play</c> or <c>stop</c>; the second is <c>vox</c> for
/// speech (1,075) or <c>sfx</c> for everything else (386); the third is
/// <c>iga</c> on every spoken line, and <c>pwr</c> on nearly every effect.
/// <para>
/// Beyond that the parts name the occasion — <c>defeatmob</c>,
/// <c>encounterbos</c>, <c>interplay</c>, <c>emote</c>, <c>levelup</c>,
/// <c>revived</c>. Only parts whose meaning is plain from the word itself are
/// matched. A line whose occasion is not one of those is left in a general
/// group rather than pushed into a category on a guess: being told a line is
/// simply "other" is honest, whereas filing it under Combat because a rule
/// somewhere said so is not.
/// </para>
/// </remarks>
public static class SoundKinds
{
    // Matched as beginnings, because the occasions come in families: defeatmob,
    // defeatmobhuman, defeatmobsentinel; receivedamagelow, -med, -high;
    // knockbacked, knockdowned, knockuped.
    private static readonly string[] Combat =
    [
        "defeat", "encounter", "receivedamage", "dealingdamage", "hitbycrit", "crit", "death",
        "knock", "stunned", "rooted", "slowed", "feared", "taunted", "destroy", "attack", "melee",
    ];

    private static readonly string[] Powers =
        ["power", "newpower", "activation", "ultimate"];

    private static readonly string[] Story =
        ["msn", "mission", "moco", "cinematic", "story"];

    private static readonly string[] Status =
    [
        "levelup", "revived", "noenergy", "lowhealth", "invfull", "powerlocked", "finditem",
        "start", "inactive", "longdowntime", "cantdo", "needtarget", "nearby", "env",
    ];

    /// <summary>Which group a sound belongs to, given its recovered name.</summary>
    public static SoundKind Of(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SoundKind.Unnamed;

        // A repeated take carries its number in brackets; the parts are what
        // matter here.
        int bracket = name.IndexOf('(');
        string bare = bracket > 0 ? name[..bracket].Trim() : name;

        string[] parts = bare.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return SoundKind.OtherVoice;

        bool spoken = parts[1].Equals("vox", StringComparison.OrdinalIgnoreCase);

        if (!spoken)
        {
            bool power = parts.Length > 2 && parts[2].Equals("pwr", StringComparison.OrdinalIgnoreCase);
            return power ? SoundKind.PowerEffect : SoundKind.OtherEffect;
        }

        foreach (string part in parts)
        {
            if (part.Equals("interplay", StringComparison.OrdinalIgnoreCase)) return SoundKind.Banter;

            if (part.Equals("emote", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("emo", StringComparison.OrdinalIgnoreCase))
            {
                return SoundKind.Emote;
            }

            if (Begins(part, Combat)) return SoundKind.CombatVoice;
            if (Begins(part, Powers)) return SoundKind.PowerVoice;
            if (Begins(part, Story)) return SoundKind.StoryVoice;
            if (Begins(part, Status)) return SoundKind.StatusVoice;
        }

        return SoundKind.OtherVoice;
    }

    private static bool Begins(string part, string[] families)
    {
        foreach (string family in families)
            if (part.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>The heading a group is shown under.</summary>
    public static string NameOf(SoundKind kind) => kind switch
    {
        SoundKind.PowerEffect => "Power sounds",
        SoundKind.OtherEffect => "Other effects",
        SoundKind.CombatVoice => "Combat lines",
        SoundKind.PowerVoice => "Power callouts",
        SoundKind.Banter => "Banter with other heroes",
        SoundKind.Emote => "Emotes",
        SoundKind.StoryVoice => "Story and missions",
        SoundKind.StatusVoice => "Level-ups and status",
        SoundKind.OtherVoice => "Other spoken lines",
        _ => "Unnamed",
    };

    /// <summary>The order the groups are shown in.</summary>
    public static int OrderOf(SoundKind kind) => kind switch
    {
        SoundKind.CombatVoice => 0,
        SoundKind.PowerVoice => 1,
        SoundKind.Banter => 2,
        SoundKind.Emote => 3,
        SoundKind.StoryVoice => 4,
        SoundKind.StatusVoice => 5,
        SoundKind.OtherVoice => 6,
        SoundKind.PowerEffect => 7,
        SoundKind.OtherEffect => 8,
        _ => 8,
    };
}
