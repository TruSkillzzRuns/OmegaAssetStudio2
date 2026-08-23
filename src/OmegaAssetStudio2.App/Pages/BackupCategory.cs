namespace OmegaAssetStudio2.App.Pages;

/// <summary>
/// What kind of thing a backed-up file holds.
/// </summary>
/// <remarks>
/// A list of fifty-odd files called UC__PowerThor_RagnarokForward_SF.upk tells
/// somebody nothing about what they changed. The game names every package after
/// what is inside it, and those names are strict enough to sort by: a costume,
/// a power, a team-up, an enemy, an effect, an icon. So the list can be put in
/// order without reading a single file.
/// </remarks>
public enum BackupCategory
{
    /// <summary>Anything whose name says nothing this understands.</summary>
    Other,

    /// <summary>A hero's model and costumes.</summary>
    Heroes,

    /// <summary>A power's art — what a skill looks like when it goes off.</summary>
    Powers,

    /// <summary>The effects a power applies, throws, or leaves behind.</summary>
    Effects,

    /// <summary>A team-up companion.</summary>
    TeamUps,

    /// <summary>Enemies, bosses and everything else the game spawns.</summary>
    Enemies,

    /// <summary>The pictures the interface draws.</summary>
    Icons,

    /// <summary>The game's own data, rather than its art.</summary>
    GameData,
}

/// <summary>Sorts a backed-up file by what its name says it holds.</summary>
public static class BackupCategories
{
    /// <summary>
    /// The order they are shown in: what somebody is most likely to have
    /// changed, first.
    /// </summary>
    public static readonly BackupCategory[] Order =
    [
        BackupCategory.Heroes,
        BackupCategory.Powers,
        BackupCategory.Effects,
        BackupCategory.TeamUps,
        BackupCategory.Enemies,
        BackupCategory.Icons,
        BackupCategory.GameData,
        BackupCategory.Other,
    ];

    /// <summary>What the tab for a category is called.</summary>
    public static string Name(BackupCategory category) => category switch
    {
        BackupCategory.Heroes => "Heroes",
        BackupCategory.Powers => "Powers",
        BackupCategory.Effects => "Effects",
        BackupCategory.TeamUps => "Team-Ups",
        BackupCategory.Enemies => "Enemies & Bosses",
        BackupCategory.Icons => "Icons",
        BackupCategory.GameData => "Game data",
        _ => "Other",
    };

    /// <summary>
    /// Which category a file belongs to, by the name the game gave it.
    /// </summary>
    /// <remarks>
    /// Read longest-first, because the prefixes nest: every team-up package
    /// begins UC__MarvelTeamUp_ and would otherwise be caught by a shorter
    /// rule. A backup of a backup — the .bak the tool leaves — is named after
    /// the file it protects, so the extension is taken off before the name is
    /// read.
    /// </remarks>
    public static BackupCategory Of(string fileName)
    {
        string name = fileName;

        // A .bak protects a file and is named after it.
        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        if (name.StartsWith("ICO__", StringComparison.OrdinalIgnoreCase)) return BackupCategory.Icons;

        if (name.EndsWith(".sip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".directory", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
        {
            return BackupCategory.GameData;
        }

        if (name.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__PowerTeamUp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__PowerTeamup", StringComparison.OrdinalIgnoreCase))
        {
            return BackupCategory.TeamUps;
        }

        if (name.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase))
            return BackupCategory.Heroes;

        if (name.StartsWith("UC__MarvelConditionEffect", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__MarvelProjectile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__MarvelEntity", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__MarvelAttachment", StringComparison.OrdinalIgnoreCase))
        {
            return BackupCategory.Effects;
        }

        if (name.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__Itempower", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__MarvelPower", StringComparison.OrdinalIgnoreCase))
        {
            return BackupCategory.Powers;
        }

        if (name.StartsWith("UC__MarvelAgent_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__MarvelDestructible", StringComparison.OrdinalIgnoreCase))
        {
            return BackupCategory.Enemies;
        }

        return BackupCategory.Other;
    }

    /// <summary>
    /// Who or what the file belongs to, for grouping inside a category.
    /// </summary>
    /// <remarks>
    /// The segment after the prefix: the character token in
    /// UC__Power&lt;name&gt;_&lt;power&gt;_SF, or in UC__MarvelPlayer_&lt;name&gt;_&lt;costume&gt;_SF.
    /// Empty where the name does not say.
    /// </remarks>
    public static string Owner(string fileName)
    {
        string name = fileName;

        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];

        int lead = name.IndexOf("__", StringComparison.Ordinal);
        if (lead < 0) return string.Empty;

        string middle = name[(lead + 2)..];

        if (middle.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) middle = middle[..^3];

        // The kind, then who it is for.
        int cut = middle.IndexOf('_');

        return cut < 0 ? middle : middle[(cut + 1)..];
    }
}
