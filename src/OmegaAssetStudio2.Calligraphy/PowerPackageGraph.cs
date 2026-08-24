namespace OmegaAssetStudio.Calligraphy;

/// <summary>One package a power reaches, and how it was reached.</summary>
public sealed record PowerPackageRef
{
    /// <summary>The class the game names, e.g. <c>PowerThor_Shockwave</c>.</summary>
    public required string ClassName { get; init; }

    /// <summary>The cooked file that class ships in, e.g. <c>UC__PowerThor_Shockwave_SF.upk</c>.</summary>
    public required string PackageFileName { get; init; }

    /// <summary>The prototype that named it.</summary>
    public required string FoundIn { get; init; }

    /// <summary>How many prototype references away from the power it was.</summary>
    public required int Depth { get; init; }

    public override string ToString() => $"{PackageFileName} (via {FoundIn}, depth {Depth})";
}

/// <summary>
/// Every package a power draws from, as the game's own data says.
/// </summary>
/// <remarks>
/// A power's own cooked package is not the answer to this, and neither is its
/// name. Measured on one ground-slam power: its package holds one particle
/// system and names no other package — its imports reach only the engine and
/// the shared vfx libraries — while what the power actually draws lives across
/// six files. Matching those six by file name finds most of them and also finds
/// things that merely share a prefix.
/// <para>
/// The prototype says it exactly. A power prototype references the effects it
/// applies by prototype id, embeds its projectiles as bodies inside itself, and
/// names every class it needs by asset id. Following those three and resolving
/// the asset ids against the class directories gives the set the game uses,
/// distinguishing the variants of a power from one another and pulling in
/// nothing that only looks related.
/// </para>
/// </remarks>
public sealed class PowerPackageGraph
{
    /// <summary>Where the classes a prototype names are looked up.</summary>
    /// <remarks>
    /// Three directories rather than one, because the game files powers,
    /// conditions, and entities apart: 7,754 power classes, 2,012 condition
    /// classes, and 5,241 entity classes in the 1.52 archive. A hotspot is an
    /// entity, an applied effect is a condition, and both hang off powers.
    /// </remarks>
    private static readonly string[] ClassDirectories =
    [
        "Calligraphy/Powers/Types/PowerUnrealClass.type",
        "Calligraphy/Powers/Types/ConditionUnrealClass.type",
        "Calligraphy/Entity/Types/UnrealClass.type",
    ];

    private readonly KapgArchiveReader _archive;
    private readonly PrototypeDirectoryReader _prototypes;
    private readonly List<TypeDirectoryReader> _classes = new();

    public PowerPackageGraph(KapgArchiveReader archive, PrototypeDirectoryReader prototypes)
    {
        _archive = archive;
        _prototypes = prototypes;

        foreach (string path in ClassDirectories)
        {
            TypeDirectoryReader? reader = null;
            try { reader = TypeDirectoryReader.LoadFromArchive(archive, path); }
            catch (Exception) { }

            if (reader is not null) _classes.Add(reader);
        }
    }

    /// <summary>How many class names are known across all three directories.</summary>
    public int KnownClasses => _classes.Sum(c => c.EntryCount);

    /// <summary>Every package the power at <paramref name="prototypePath"/> reaches.</summary>
    /// <param name="maxDepth">
    /// How many prototype references to follow. Two is the measured reach of a
    /// power: its own package, the conditions and missiles it applies, and the
    /// entities those summon — one ground-slam power needs exactly two to find
    /// the hotspot its missile leaves behind. Three begins to arrive at other
    /// powers entirely, through the shared combo and melee references every
    /// power of a kind carries.
    /// </param>
    public IReadOnlyList<PowerPackageRef> Walk(string prototypePath, int maxDepth = 2)
    {
        var found = new Dictionary<string, PowerPackageRef>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        // A power's own pieces are named after it: Shockwave is followed by
        // ShockwaveHotspotEffect, ShockwaveOFMissile, ShockwaveMissileEffect.
        // What it references that is NOT named after it is shared — the basic
        // melee every power of a kind chains into, the buff that marks a
        // character as empowered — and belongs to the character rather than to
        // this skill. Following those is how a sky-strike power ended up
        // offering a hammer-chain impact and a death-from-above trail.
        string family = Leaf(prototypePath);

        queue.Enqueue((prototypePath, 0));

        while (queue.Count > 0)
        {
            (string path, int depth) = queue.Dequeue();

            if (depth > maxDepth || !visited.Add(path)) continue;
            if (!TryRead(path, out PrototypeBody? body)) continue;

            Collect(body!, path, depth, found, queue, family);
        }

        return found.Values
            .OrderBy(r => r.Depth)
            .ThenBy(r => r.PackageFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads one prototype body from the archive.
    /// </summary>
    /// <remarks>
    /// Parsing is what makes a body; constructing the parser alone leaves an
    /// empty one that reports no fields and no parent, which reads exactly like
    /// a prototype that says nothing.
    /// </remarks>
    private bool TryRead(string path, out PrototypeBody? body)
    {
        body = null;

        if (!_archive.TryFindByName(path, out var entry)) return false;

        byte[] data;
        try { data = _archive.ExtractEntry(entry); }
        catch (Exception) { return false; }

        try
        {
            var parser = new PrototypeParser(data);
            if (!parser.TryParse(out _)) return false;
            body = parser.Result;
        }
        catch (Exception) { return false; }

        return body is not null;
    }

    /// <summary>
    /// Takes the classes a body names, and queues the prototypes it points at.
    /// </summary>
    /// <remarks>
    /// Three kinds of value matter. An asset id names a class, which is a
    /// package. A prototype id points at another prototype — followed unless it
    /// is a shared blueprint, since those describe what every power of a kind
    /// has rather than what this one has. A nested body is a prototype written
    /// inside this one, which is where a missile power keeps its missiles, and
    /// is read in place.
    /// </remarks>
    private void Collect(
        PrototypeBody body,
        string foundIn,
        int depth,
        Dictionary<string, PowerPackageRef> found,
        Queue<(string, int)> queue,
        string family)
    {
        foreach (var group in body.Groups)
        {
            foreach (var field in group.SimpleFields.Concat(group.ListFields))
            {
                foreach (object value in field.Values)
                {
                    if (value is PrototypeBody nested)
                    {
                        Collect(nested, foundIn, depth, found, queue, family);
                        continue;
                    }

                    if (value is not ulong id || id == 0) continue;

                    if (field.TypeCode == 'A')
                    {
                        Remember(id, foundIn, depth, found);
                        continue;
                    }

                    if (field.TypeCode != 'P') continue;
                    if (!_prototypes.IdToPath.TryGetValue(id, out string? path)) continue;
                    if (IsShared(path)) continue;
                    if (!Leaf(path).StartsWith(family, StringComparison.OrdinalIgnoreCase)) continue;

                    queue.Enqueue((Archived(path), depth + 1));
                }
            }
        }
    }

    /// <summary>Records the package a class ships in, if the class is known.</summary>
    private void Remember(ulong assetId, string foundIn, int depth, Dictionary<string, PowerPackageRef> found)
    {
        foreach (var directory in _classes)
        {
            if (!directory.IdToName.TryGetValue(assetId, out string? className)) continue;
            if (string.IsNullOrWhiteSpace(className)) continue;

            string file = $"UC__{className}_SF.upk";
            if (found.ContainsKey(file)) return;

            // A buff can name the character wearing it. That package is the
            // costume, shared by every skill they have, and recolouring it
            // through one skill would recolour all of them.
            if (className.StartsWith("MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) return;

            found[file] = new PowerPackageRef
            {
                ClassName = className,
                PackageFileName = file,
                FoundIn = Leaf(foundIn),
                Depth = depth,
            };
            return;
        }
    }

    /// <summary>
    /// Whether a prototype describes a kind of thing rather than one thing.
    /// </summary>
    /// <remarks>
    /// Blueprints and their defaults are shared by every power built on them,
    /// so what they name belongs to all of them and to none in particular.
    /// Following one turns a power's package list into the game's.
    /// </remarks>
    private static bool IsShared(string path) =>
        path.Contains("/Blueprints/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Types/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase);

    /// <summary>The directory spells its paths without the archive's leading folder.</summary>
    private static string Archived(string path) =>
        path.StartsWith("Calligraphy/", StringComparison.OrdinalIgnoreCase) ? path : "Calligraphy/" + path;

    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];

        return leaf.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) ? leaf[..^10] : leaf;
    }
}
