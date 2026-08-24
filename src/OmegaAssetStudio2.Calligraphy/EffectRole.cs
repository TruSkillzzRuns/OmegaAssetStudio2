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
    public static string FromPackageClass(string? className, string? powerClassName = null)
    {
        string name = className ?? string.Empty;

        if (Starts(name, "MarvelConditionEffect")) return "on whoever it hits";
        if (Starts(name, "MarvelProjectile")) return "the projectile";
        if (Starts(name, "MarvelEntity_Hotspot")) return "the area it leaves";
        if (Starts(name, "MarvelEntity")) return "what it summons";
        if (Starts(name, "MarvelAgent")) return "what it summons";
        if (Starts(name, "MarvelAttachment")) return "what it attaches";

        return FromPowerPackage(name, powerClassName ?? string.Empty);
    }

    /// <summary>
    /// What a power's own package holds, from what its name adds to the power's.
    /// </summary>
    /// <remarks>
    /// Most of a power's packages are its own kind: 126 of 173 across five
    /// characters, and 71 of those are the power itself with nothing added. The
    /// rest add a word saying which piece they are — MissileEffect, Combo, Beam,
    /// Hit, Knockback, PBAoE — and those words are the label.
    /// <para>
    /// A suffix with no known meaning is called another version rather than
    /// guessed at. OF and NoOF are two such: they clearly separate a power's
    /// empowered form from its plain one, but nothing in the name says which
    /// way round, so neither is claimed.
    /// </para>
    /// </remarks>
    private static string FromPowerPackage(string className, string powerClassName)
    {
        if (!Starts(className, "Power")) return string.Empty;
        if (powerClassName.Length == 0) return "when you cast it";
        if (!Starts(className, powerClassName)) return string.Empty;

        string extra = className[powerClassName.Length..].Trim('_');

        if (extra.Length == 0) return "when you cast it";

        if (Has(extra, "MissileEffect")) return "the projectile";
        if (Has(extra, "Combo")) return "the combo that follows";
        if (Has(extra, "Beam")) return "the beam";
        if (Has(extra, "Knockback")) return "the knockback";
        if (Has(extra, "PBAoE")) return "the burst around you";
        if (Has(extra, "Hit")) return "the hit";

        // A suffix nothing here recognises is left unsaid. "Another version of
        // it" reads like information and is not: it fits every case and
        // distinguishes none, which is worse than a blank line.
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

    /// <summary>
    /// What an effect is, from the name of the particle system itself.
    /// </summary>
    /// <remarks>
    /// The last resort and often the most specific one. A power that binds
    /// nothing has no component to ask and a package name that says only which
    /// power it belongs to, but the systems inside it are named for what they
    /// draw. Counted over 4,166 of them: 1,119 say hit, 428 critical, 224
    /// impact, 188 trail, 167 dust, 113 the burst of landing, 101 projectile,
    /// 86 cast, 84 beam, 62 ground, 53 area.
    /// <para>
    /// Read most specific first, since the words nest — a critical hit is also
    /// a hit, and an impact on snow is also an impact.
    /// </para>
    /// </remarks>
    public static string FromSystemName(string? systemName)
    {
        string name = systemName ?? string.Empty;
        if (name.Length == 0) return string.Empty;

        foreach ((string token, string meaning) in SystemWords)
        {
            if (Has(name, token)) return meaning;
        }

        return string.Empty;
    }

    /// <summary>
    /// The words a particle system is named with, and what each one draws.
    /// </summary>
    /// <remarks>
    /// Ordered, not alphabetical: the first match wins, so the narrow words
    /// come before the broad ones they contain.
    /// </remarks>
    private static readonly (string Token, string Meaning)[] SystemWords =
    [
        ("agmwater", "the splash when it hits water"),
        ("agmsnow", "the spray when it hits snow"),
        ("agmmagma", "the burst when it hits magma"),
        ("agmdirt", "the dirt it kicks up"),
        ("agmdust", "the dust it kicks up"),
        ("airburstdown", "the burst as it lands"),
        ("thrownimpact", "the impact when thrown"),
        ("uberhit", "a heavy hit"),
        ("ubertell", "the wind-up before it lands"),
        ("uberglint", "the glint before it lands"),
        ("startdust", "the dust as it starts"),
        ("crit", "a critical hit"),
        ("miss", "a miss"),
        ("hitfx", "the hit"),
        ("impact", "the impact"),
        ("projhit", "the projectile hitting"),
        ("proj", "the projectile"),
        ("projectile", "the projectile"),
        ("trail", "the trail"),
        ("beam", "the beam"),
        ("cast", "as you cast it"),
        ("launch", "as it launches"),
        ("explo", "the explosion"),
        ("burst", "the burst"),
        ("flash", "the flash"),
        ("shockwave", "the shockwave"),
        ("ground", "what it draws on the ground"),
        ("scorch", "the scorch it leaves"),
        ("aoe", "the area it covers"),
        ("dust", "the dust"),
        ("smoke", "the smoke"),
        ("debris", "the debris"),
        ("glow", "the glow"),
        ("loop", "while it lasts"),
        ("hit", "the hit"),
    ];

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
