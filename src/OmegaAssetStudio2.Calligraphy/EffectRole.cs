namespace OmegaAssetStudio.Calligraphy;

/// <summary>
/// What part of a skill an effect is, said plainly.
/// </summary>
/// <remarks>
/// The game already labels this and the labels are consistent. Every effect a
/// power binds is held by a component whose class says its role, counted across
/// 82 powers of five characters: 245 are the power's own, 81 belong to the
/// condition it applies, 38 to the projectile it throws, 44 to the hit, 22 to
/// what it leaves in the world, 24 are decals, 16 are beams.
/// <para>
/// Nothing here is invented. Where the game's own shorthand is all there is —
/// <c>palmglow_l</c>, <c>burnoutlhand</c> — that shorthand is shown as it
/// stands rather than dressed up into a guess.
/// </para>
/// </remarks>
public static class EffectRole
{
    /// <summary>What a component's class says the effect is for.</summary>
    /// <remarks>
    /// Read longest-first: every condition class begins with the word that also
    /// starts the power classes, so a shorter rule would swallow them.
    /// </remarks>
    public static string FromComponentClass(string? componentClass)
    {
        string kind = componentClass ?? string.Empty;

        if (Has(kind, "conditionfx")) return "on whoever it hits";
        if (Has(kind, "projectilefx")) return "the projectile";
        if (Has(kind, "entityfx")) return "what it leaves behind";
        if (Has(kind, "hit_crit")) return "a critical hit";
        if (Has(kind, "fxhit")) return "the hit";
        if (Has(kind, "beam")) return "the beam";
        if (Has(kind, "decal")) return "the mark on the ground";
        if (Has(kind, "powerfx")) return "when you cast it";

        return string.Empty;
    }

    /// <summary>What a package's class says it holds.</summary>
    /// <remarks>
    /// For colours reached through the power's data rather than through a bound
    /// component, where there is no component to ask.
    /// </remarks>
    public static string FromPackageClass(string? className)
    {
        string name = className ?? string.Empty;

        if (Starts(name, "MarvelConditionEffect")) return "on whoever it hits";
        if (Starts(name, "MarvelProjectile")) return "the projectile";
        if (Starts(name, "MarvelEntity_Hotspot")) return "the area it leaves";
        if (Starts(name, "MarvelEntity")) return "what it summons";
        if (Starts(name, "MarvelAgent")) return "what it summons";
        if (Starts(name, "MarvelAttachment")) return "what it attaches";

        return string.Empty;
    }

    /// <summary>
    /// What the slot a component sits in is called, made readable.
    /// </summary>
    /// <remarks>
    /// These are the artists' names and most read plainly once the joins are
    /// opened out: <c>trailfx</c>, <c>castvfx</c>, <c>ground_scorch</c>,
    /// <c>explodefx</c>. The suffixes fx and vfx say nothing a reader needs.
    /// </remarks>
    public static string FromSlotName(string? componentName)
    {
        string slot = (componentName ?? string.Empty).Trim();
        if (slot.Length == 0) return string.Empty;

        slot = slot.Replace('_', ' ');

        foreach (string noise in new[] { "vfx", "fx" })
        {
            if (slot.EndsWith(noise, StringComparison.OrdinalIgnoreCase) && slot.Length > noise.Length)
            {
                slot = slot[..^noise.Length];
                break;
            }
        }

        slot = slot.Trim();

        // A slot called "particle" on a particle component says nothing the
        // reader does not already have.
        foreach (string empty in Generic)
        {
            if (slot.Equals(empty, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        }

        // The names are typed by hand and shortened the way people shorten
        // words at a keyboard. Opened out, they read.
        var words = slot.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Shorthand.TryGetValue(w, out string? full) ? full : w);

        return string.Join(' ', words);
    }

    /// <summary>
    /// The whole description of one effect: its role, and which slot it fills.
    /// </summary>
    /// <remarks>
    /// The role comes first because it is the part that always holds. The slot
    /// name follows in brackets when it adds something the role does not
    /// already say.
    /// </remarks>
    public static string Describe(string? componentClass, string? componentName)
    {
        string role = FromComponentClass(componentClass);
        string slot = FromSlotName(componentName);

        if (role.Length == 0) return slot;
        if (slot.Length == 0) return role;

        // "the hit (basichit)" says the same thing twice.
        string flat = new string(slot.Where(char.IsLetter).ToArray());
        if (flat.Length > 0
            && role.Replace(" ", string.Empty).Contains(flat, StringComparison.OrdinalIgnoreCase))
        {
            return role;
        }

        return $"{role} — {slot}";
    }

    /// <summary>Slot names that only repeat what the component already is.</summary>
    private static readonly string[] Generic =
        ["particle", "particles", "effect", "effects", "main", "default", "base"];

    /// <summary>The shortenings the names are written in.</summary>
    private static readonly Dictionary<string, string> Shorthand =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bdy"] = "body",
            ["tgt"] = "target",
            ["proj"] = "projectile",
            ["aoe"] = "area",
            ["init"] = "start of",
            ["crit"] = "critical",
            ["l"] = "left",
            ["r"] = "right",
        };

    private static bool Has(string value, string part) =>
        value.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string value, string part) =>
        value.StartsWith(part, StringComparison.OrdinalIgnoreCase);
}
