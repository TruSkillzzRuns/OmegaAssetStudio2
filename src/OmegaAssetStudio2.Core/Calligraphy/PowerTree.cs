using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>One power a character has, as the game's own data describes it.</summary>
public sealed record TreePower
{
    /// <summary>The power's own name, without the folders that lead to it.</summary>
    public required string Name { get; init; }

    /// <summary>Where the whole definition lives, for anything that needs the rest of it.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The picture the game shows for it, as package and texture together, or
    /// empty where it names none.
    /// </summary>
    public string Icon { get; init; } = string.Empty;
}

/// <summary>
/// The powers a character actually has, as the game's own data says.
/// </summary>
/// <remarks>
/// Package names are not the answer to this. UC__PowerBoomerangThrow_Thor_SF is
/// a real file that really does name one character, and that character has never
/// had a boomerang-throw power — it belongs to somebody else, and the file is
/// that power with a version built for the first character's prop. No rule about how names are spelled
/// separates those two cases, because the difference is not in the name.
/// <para>
/// The game keeps the real list in Calligraphy. A character's definition lives
/// at Entity/Characters/Avatars/Shipping/&lt;name&gt;, and every power they have
/// — the tree, the talents, the traits, the travel power, the ultimate — is
/// referred to from it by number. Reading those numbers back through
/// Prototype.directory gives the names, and that character's list has no
/// boomerang throw in it.
/// </para>
/// </remarks>
public static class PowerTree
{
    private const string AvatarFolder = "Entity/Characters/Avatars/Shipping/";
    private const string Extension = ".prototype";

    /// <summary>Definitions under this are powers; the rest of what a character names is not.</summary>
    private const string PowersFolder = "Powers";

    /// <summary>What the game's icon packages are called before their own name.</summary>
    private const string IconPackagePrefix = "ICO__";

    private const string IconPackageSuffix = "_SF.upk";

    /// <summary>Where the game keeps a costume's own description of itself.</summary>
    private const string CostumeFolder = "Entity/Items/Costumes/Prototypes/";

    /// <summary>What a model package is called before and after its own name.</summary>
    private const string ModelPrefix = "UC__";

    /// <summary>The field naming the model package a costume dresses.</summary>
    private const string CostumeClass = "CostumeUnrealClass";

    /// <summary>
    /// Where a costume's picture is looked for, best first.
    /// </summary>
    /// <remarks>
    /// The party portrait leads because it is the one drawn small and square,
    /// which is what a row of a list is. PortraitIconPath is next; for some
    /// costumes it holds the wide banner, which reads poorly at this size.
    /// </remarks>
    private static readonly string[] CostumePortrait =
        ["PartyPortraitIconPath", "PortraitIconPath", "PortraitIconPathHiRes",
         "FullBodyIconPath", "StoreIconPath"];

    /// <summary>
    /// Where a character's own picture is looked for, best first.
    /// </summary>
    /// <remarks>
    /// The small portrait leads for the same reason the costume's party
    /// portrait does: a list row is small and square. PortraitPath holds the
    /// wide banner for some characters, in the shape HeroHor_&lt;name&gt;_&lt;costume&gt;,
    /// which is nearly unreadable at this size.
    /// </remarks>
    private static readonly string[] AvatarPortrait =
        ["CharacterSelectIconPortraitSmall", "PortraitPath", "CharacterSelectIconPath"];

    /// <summary>Where a power's picture is looked for, best first.</summary>
    private static readonly string[] PowerIcon = ["IconPath", "IconPathHiRes"];

    /// <summary>The field naming the cooked package a power's art lives in.</summary>
    private const string PowerClass = "PowerUnrealClass";

    /// <summary>Where the game keeps its powers.</summary>
    private const string PowersRoot = "Calligraphy/Powers/";

    private sealed record Cast(
        IReadOnlyDictionary<string, IReadOnlyList<TreePower>> Powers,
        IReadOnlyDictionary<string, string> Icons,
        IReadOnlyDictionary<string, (string Icon, int Named)> Costumes,
        IReadOnlyDictionary<string, string> PowerIcons,
        IReadOnlyDictionary<string, IReadOnlySet<string>> TreePackages);

    private static readonly Dictionary<string, Cast> Read = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Lock = new();

    /// <summary>
    /// The powers this character has.
    /// </summary>
    /// <remarks>
    /// Empty for anybody the game has no character definition for — every
    /// enemy, boss and team-up, and any install whose archive cannot be read.
    /// A caller has to be able to tell that apart from a character who truly
    /// has none, so it is empty rather than a guess.
    /// </remarks>
    public static IReadOnlyList<TreePower> For(GameClient client, string characterToken)
    {
        if (characterToken.Length == 0) return [];

        return ForInstall(client).Powers.GetValueOrDefault(characterToken, []);
    }

    /// <summary>The character's own portrait, as package and texture together.</summary>
    public static string IconFor(GameClient client, string characterToken)
    {
        if (characterToken.Length == 0) return string.Empty;

        return ForInstall(client).Icons.GetValueOrDefault(characterToken, string.Empty);
    }

    /// <summary>
    /// The picture for one costume, found by the model package it is worn on.
    /// </summary>
    /// <remarks>
    /// Every costume has a portrait of its own and the character's own portrait
    /// is not it — a list of ten Cyclops costumes showing the same face ten
    /// times says nothing about which is which. A costume's definition names
    /// both its picture and the model package it dresses, so the row's own file
    /// is what finds it, with no guessing at how the two names line up: the
    /// game writes the costume folder as Astonishing and the package as
    /// AstonishingXmenVU, and nothing but the definition joins them.
    /// </remarks>
    public static string IconForPackage(GameClient client, string packagePath)
    {
        if (packagePath.Length == 0) return string.Empty;

        string stem = System.IO.Path.GetFileNameWithoutExtension(packagePath);

        return ForInstall(client).Costumes.GetValueOrDefault(stem).Icon ?? string.Empty;
    }

    /// <summary>
    /// The picture for one power, found by the package its art lives in.
    /// </summary>
    /// <remarks>
    /// Every power says which cooked package holds its art, so the row's own
    /// file finds its picture with nothing guessed. This answers for powers a
    /// character has and for the effects that merely ship with them alike — the
    /// second kind are still powers, just nobody's tree entry, and they have
    /// pictures of their own.
    /// </remarks>
    public static string IconForPowerPackage(GameClient client, string packagePath)
    {
        if (packagePath.Length == 0) return string.Empty;

        string stem = System.IO.Path.GetFileNameWithoutExtension(packagePath);

        return ForInstall(client).PowerIcons.GetValueOrDefault(stem, string.Empty);
    }

    /// <summary>
    /// The packages holding the art of the powers this character actually has.
    /// </summary>
    /// <remarks>
    /// Empty where the game has no character definition to read, which a caller
    /// has to tell apart from a character who has none.
    /// </remarks>
    public static IReadOnlySet<string> TreePackagesFor(GameClient client, string characterToken)
    {
        if (characterToken.Length == 0) return new HashSet<string>();

        return ForInstall(client).TreePackages.GetValueOrDefault(
            characterToken, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether the game's own list of the cast could be read for this install.</summary>
    public static bool Known(GameClient client) => ForInstall(client).Powers.Count > 0;

    /// <summary>Forgets what was read, for an install whose files have changed.</summary>
    public static void Forget()
    {
        lock (Lock) Read.Clear();
    }

    private static Cast ForInstall(GameClient client)
    {
        string key = client.RootPath ?? string.Empty;

        lock (Lock)
        {
            if (Read.TryGetValue(key, out Cast? already)) return already;
        }

        Cast found = Walk(key, client.CookedPath ?? string.Empty);

        lock (Lock) Read[key] = found;

        return found;
    }

    private static Cast Walk(string installRoot, string cookedPath)
    {
        var powers = new Dictionary<string, IReadOnlyList<TreePower>>(StringComparer.OrdinalIgnoreCase);
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var costumes = new Dictionary<string, (string Icon, int Named)>(StringComparer.OrdinalIgnoreCase);
        var powerIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var treePackages = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        var empty = new Cast(powers, icons, costumes, powerIcons, treePackages);

        if (installRoot.Length == 0) return empty;

        PrototypeArchive? archive;
        try { archive = PrototypeArchive.Open(installRoot); }
        catch (Exception) { return empty; }

        if (archive is null) return empty;

        using (archive)
        {
            IReadOnlyDictionary<ulong, string> byId = PrototypeDirectory.Read(archive);
            if (byId.Count == 0) return empty;

            IReadOnlyDictionary<ulong, string> assets = AssetDirectory.Read(archive);
            IReadOnlyDictionary<ulong, string> fields = BlueprintFields.Read(archive);

            // Where every definition lives, so a power named by a character can
            // be opened in turn and asked for its picture.
            var entries = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ArchiveEntry entry in archive.Entries) entries[entry.Name] = entry;

            // Every power, and the package its art lives in. Read before the
            // characters are, because a character names its powers and the
            // powers name their packages, and the packages are what a row is.
            var packageOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var setOff = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (!entry.Name.StartsWith(PowersRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                Prototype? power = Open(archive, entry);
                if (power is null) continue;

                string stem = Package(power, fields, assets, cookedPath);
                if (stem.Length == 0) continue;

                packageOf[entry.Name] = stem;

                // What this power sets off, so a piece of a power that names no
                // picture can be given the picture of the power it belongs to.
                var uses = new List<string>();

                foreach (PrototypeField field in power.Fields)
                {
                    if (field.Kind != 'P') continue;
                    if (!byId.TryGetValue(field.Value, out string? name)) continue;
                    if (!name.StartsWith(PowersFolder, StringComparison.OrdinalIgnoreCase)) continue;

                    uses.Add(name);
                }

                if (uses.Count > 0) setOff[entry.Name] = uses;

                if (powerIcons.ContainsKey(stem)) continue;

                // A power that names no picture of its own takes the one it was
                // built from. Definitions inherit here, and a second activation
                // or a variant is built from the power it belongs to.
                string icon = Inherited(archive, entries, byId, power, fields, assets, cookedPath);

                if (icon.Length > 0) powerIcons[stem] = icon;
            }

            Borrowed(setOff, packageOf, powerIcons);

            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (!entry.Name.Contains(AvatarFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                string who = entry.Name[(entry.Name.LastIndexOf('/') + 1)..^Extension.Length];
                if (who.Length == 0) continue;

                Prototype? avatar = Open(archive, entry);
                if (avatar is null) continue;

                var mine = new List<TreePower>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string portrait = Named(avatar, fields, assets, cookedPath, AvatarPortrait);
                if (portrait.Length > 0) icons[who] = portrait;

                foreach (PrototypeField field in avatar.Fields)
                {
                    if (field.Kind == 'A') continue;

                    if (!byId.TryGetValue(field.Value, out string? name)) continue;
                    if (!name.StartsWith(PowersFolder, StringComparison.OrdinalIgnoreCase)) continue;

                    string leaf = Leaf(name);
                    if (leaf.Length == 0 || !seen.Add(leaf)) continue;

                    mine.Add(new TreePower
                    {
                        Name = leaf,
                        Path = name,
                        Icon = IconOf(archive, entries, name, assets, fields, cookedPath),
                    });
                }

                if (mine.Count > 0) powers[who] = mine;

                var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (TreePower power in mine)
                {
                    string wanted = "Calligraphy/" + power.Path.Replace('\\', '/');

                    if (!wanted.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) wanted += Extension;

                    if (packageOf.TryGetValue(wanted, out string? stem)) held.Add(stem);
                }

                if (held.Count > 0) treePackages[who] = held;
            }

            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (!entry.Name.Contains(CostumeFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                ReadCostume(archive, entry, assets, fields, cookedPath, costumes);
            }
        }

        return new Cast(powers, icons, costumes, powerIcons, treePackages);
    }

    /// <summary>
    /// Files one costume's picture under the model package it dresses.
    /// </summary>
    /// <remarks>
    /// A costume names several pictures — a portrait, one for the store, a
    /// banner — and the portrait is the one it names twice, once at each of the
    /// two sizes it is shown at. So the picture named most often is the
    /// portrait, which is read off what the definition says rather than off
    /// what the pictures happen to be called.
    /// </remarks>
    private static void ReadCostume(
        PrototypeArchive archive,
        ArchiveEntry entry,
        IReadOnlyDictionary<ulong, string> assets,
        IReadOnlyDictionary<ulong, string> fields,
        string cookedPath,
        Dictionary<string, (string Icon, int Named)> costumes)
    {
        Prototype? costume = Open(archive, entry);
        if (costume is null) return;

        string icon = Named(costume, fields, assets, cookedPath, CostumePortrait);
        if (icon.Length == 0) return;

        // The costume says which model package it dresses, so the row's own
        // file is what finds it. Nothing else joins the two: the game writes
        // the costume folder as Astonishing and the package as
        // AstonishingXmenVU.
        string model = string.Empty;

        foreach (PrototypeField field in costume.Fields)
        {
            if (field.Kind != 'A') continue;
            if (!fields.TryGetValue(field.Id, out string? named)) continue;
            if (!named.Equals(CostumeClass, StringComparison.Ordinal)) continue;
            if (!assets.TryGetValue(field.Value, out string? name)) continue;

            model = ModelPrefix + name + "_SF";
            break;
        }

        if (model.Length == 0) return;

        costumes[model] = (icon, 1);
    }

    /// <summary>
    /// Hands a power's picture to the pieces of it that have none.
    /// </summary>
    /// <remarks>
    /// A combo, a missile effect, a hotspot and a second activation are powers
    /// in their own right and the game never shows them, so most name no
    /// picture at all — a combo and its follow-up arc are both blank. But the power that sets them off does have one, and
    /// says which pieces it sets off, so the picture is taken from there rather
    /// than left empty or made up.
    /// <para>
    /// Only where a piece has none of its own: a power that names its picture
    /// keeps it.
    /// </para>
    /// </remarks>
    private static void Borrowed(
        Dictionary<string, List<string>> setOff,
        Dictionary<string, string> packageOf,
        Dictionary<string, string> powerIcons)
    {
        foreach ((string from, List<string> uses) in setOff)
        {
            if (!packageOf.TryGetValue(from, out string? mine)) continue;
            if (!powerIcons.TryGetValue(mine, out string? icon)) continue;

            foreach (string used in uses)
            {
                string wanted = "Calligraphy/" + used.Replace('\\', '/');

                if (!wanted.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) wanted += Extension;

                if (!packageOf.TryGetValue(wanted, out string? stem)) continue;
                if (powerIcons.ContainsKey(stem)) continue;

                powerIcons[stem] = icon;
            }
        }
    }

    /// <summary>How far up the chain a picture is looked for.</summary>
    private const int MostParents = 8;

    /// <summary>
    /// The picture a definition names, or the nearest one it was built from
    /// names.
    /// </summary>
    /// <remarks>
    /// Bounded, because a chain that pointed back at itself would otherwise be
    /// walked for ever.
    /// </remarks>
    private static string Inherited(
        PrototypeArchive archive,
        IReadOnlyDictionary<string, ArchiveEntry> entries,
        IReadOnlyDictionary<ulong, string> byId,
        Prototype power,
        IReadOnlyDictionary<ulong, string> fields,
        IReadOnlyDictionary<ulong, string> assets,
        string cookedPath)
    {
        Prototype? at = power;

        for (int step = 0; step < MostParents && at is not null; step++)
        {
            string icon = Named(at, fields, assets, cookedPath, PowerIcon);
            if (icon.Length > 0) return icon;

            if (at.ParentId == 0) return string.Empty;
            if (!byId.TryGetValue(at.ParentId, out string? name)) return string.Empty;

            string wanted = "Calligraphy/" + name.Replace('\\', '/');

            if (!wanted.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) wanted += Extension;

            at = entries.TryGetValue(wanted, out ArchiveEntry? parent) ? Open(archive, parent) : null;
        }

        return string.Empty;
    }

    /// <summary>The cooked package a power's art lives in, as it names it.</summary>
    private static string Package(
        Prototype power,
        IReadOnlyDictionary<ulong, string> fields,
        IReadOnlyDictionary<ulong, string> assets,
        string cookedPath)
    {
        foreach (PrototypeField field in power.Fields)
        {
            if (field.Kind != 'A') continue;
            if (!fields.TryGetValue(field.Id, out string? named)) continue;
            if (!named.Equals(PowerClass, StringComparison.Ordinal)) continue;
            if (!assets.TryGetValue(field.Value, out string? name)) continue;

            string stem = ModelPrefix + name + "_SF";

            return cookedPath.Length > 0
                   && File.Exists(System.IO.Path.Combine(cookedPath, stem + ".upk"))
                ? stem
                : string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// The first picture the definition names in one of these fields.
    /// </summary>
    /// <remarks>
    /// Asked for by name, because a definition names several pictures and only
    /// the field says which is which. A costume names its portrait, its party
    /// portrait, a full-body picture, a store tile and a banner, and two of
    /// them are named twice over — so picking the one named most often gave
    /// Cyclops's Noir costume its store tile.
    /// </remarks>
    private static string Named(
        Prototype proto,
        IReadOnlyDictionary<ulong, string> fields,
        IReadOnlyDictionary<ulong, string> assets,
        string cookedPath,
        string[] wanted)
    {
        foreach (string want in wanted)
        {
            foreach (PrototypeField field in proto.Fields)
            {
                if (field.Kind != 'A') continue;
                if (!fields.TryGetValue(field.Id, out string? named)) continue;
                if (!named.Equals(want, StringComparison.Ordinal)) continue;

                string picture = Picture(field.Value, assets, cookedPath);
                if (picture.Length > 0) return picture;
            }
        }

        return string.Empty;
    }

    /// <summary>The picture a power names, or nothing where it names none.</summary>
    private static string IconOf(
        PrototypeArchive archive,
        IReadOnlyDictionary<string, ArchiveEntry> entries,
        string path,
        IReadOnlyDictionary<ulong, string> assets,
        IReadOnlyDictionary<ulong, string> fields,
        string cookedPath)
    {
        // The directory writes a name the way the game says it; the archive
        // files the same thing under a folder and an extension.
        string wanted = "Calligraphy/" + path.Replace('\\', '/');

        if (!wanted.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) wanted += Extension;

        if (!entries.TryGetValue(wanted, out ArchiveEntry? entry)) return string.Empty;

        Prototype? power = Open(archive, entry);

        return power is null ? string.Empty : Named(power, fields, assets, cookedPath, PowerIcon);
    }

    /// <summary>
    /// An asset number read as a picture, or nothing when it is not one.
    /// </summary>
    /// <remarks>
    /// A definition refers to all sorts of things by number — which state it is
    /// in, how it is aimed, what class the engine draws it with — and they all
    /// arrive here as the same kind of field. A picture is told from the rest
    /// by what it names: a package and a texture, and a package the game
    /// actually ships an icon file for.
    /// </remarks>
    private static string Picture(
        ulong id, IReadOnlyDictionary<ulong, string> assets, string cookedPath)
    {
        if (!assets.TryGetValue(id, out string? name)) return string.Empty;

        int cut = name.IndexOf('.');
        if (cut <= 0 || cut == name.Length - 1) return string.Empty;

        if (cookedPath.Length == 0) return string.Empty;

        string file = System.IO.Path.Combine(
            cookedPath, IconPackagePrefix + name[..cut] + IconPackageSuffix);

        return File.Exists(file) ? name : string.Empty;
    }

    private static Prototype? Open(PrototypeArchive archive, ArchiveEntry entry)
    {
        try { return Prototype.TryRead(archive.Read(entry)); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The power's own name, without the folders that lead to it.
    /// </summary>
    /// <remarks>
    /// Powers\Player\&lt;name&gt;\Rework\BoltSpray is Bolt Spray. The folders say which
    /// character and which revision of their kit, and neither is part of what
    /// the power is called.
    /// </remarks>
    private static string Leaf(string path)
    {
        string name = path;

        if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^Extension.Length];

        int cut = name.LastIndexOfAny(['\\', '/']);

        return cut < 0 ? name : name[(cut + 1)..];
    }
}
