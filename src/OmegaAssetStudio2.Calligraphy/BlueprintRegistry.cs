namespace OmegaAssetStudio.Calligraphy;

// In-memory index over every BPT/v11 blueprint in a KAPG archive.
// Built once, then queried by:
//   - Path           ("Calligraphy/Entity/Items/Armor/Blueprints/<Hero>/<Asset>.blueprint")
//   - Member ID hash (the 8-byte field id stored inside a prototype's field group)
//
// The registry resolves a prototype's declaring blueprint ID to the full schema
// (member id -> name + base type + structure type), enabling schema-driven
// prototype decoding.
public sealed class BlueprintRegistry
{
    private readonly Dictionary<ulong, BlueprintEntry> _byBlueprintId = new();
    private readonly Dictionary<string, BlueprintEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, BlueprintMember> _memberById = new();
    private readonly Dictionary<ulong, ulong> _entryHashToBlueprintId = new();

    public int BlueprintCount => _byBlueprintId.Count;
    public int FailureCount { get; private set; }
    public IReadOnlyDictionary<ulong, BlueprintEntry> ByBlueprintId => _byBlueprintId;

    public static BlueprintRegistry Load(KapgArchiveReader archive)
    {
        var registry = new BlueprintRegistry();

        foreach (KapgEntry entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase))
                continue;

            byte[] data;
            try { data = archive.ExtractEntry(entry); }
            catch { registry.FailureCount++; continue; }

            if (!CalligraphyFileReader.TryReadHeader(data, out var header) ||
                !header.IsBlueprint ||
                header.Version != CalligraphyMagic.CurrentVersion)
            {
                registry.FailureCount++;
                continue;
            }

            var parser = new BlueprintParser(data);
            if (!parser.TryParse(out _))
            {
                registry.FailureCount++;
                continue;
            }

            // The blueprint's "id" is the KAPG entry's FileHash — that is what
            // prototype field groups reference as "declaring blueprint id".
            var blueprintEntry = new BlueprintEntry
            {
                BlueprintId = entry.FileHash,
                Path = entry.Name,
                File = parser.Result
            };

            registry._byBlueprintId[entry.FileHash] = blueprintEntry;
            registry._byPath[entry.Name] = blueprintEntry;
            registry._entryHashToBlueprintId[entry.FileHash] = entry.FileHash;

            foreach (BlueprintMember member in parser.Result.NewMembers)
                registry._memberById[member.MemberId] = member;
        }

        return registry;
    }

    public bool TryGetByBlueprintId(ulong id, out BlueprintEntry entry) =>
        _byBlueprintId.TryGetValue(id, out entry!);

    public bool TryGetByPath(string path, out BlueprintEntry entry) =>
        _byPath.TryGetValue(path, out entry!);

    public bool TryGetMemberById(ulong memberId, out BlueprintMember member) =>
        _memberById.TryGetValue(memberId, out member!);

    // Returns the full inheritance chain of members for a blueprint: its own + contributing + recursive parents.
    // Useful for resolving a prototype's overridden field ids when those are defined in an ancestor.
    public IEnumerable<BlueprintMember> EnumerateAllMembers(ulong blueprintId)
    {
        var seen = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        queue.Enqueue(blueprintId);

        while (queue.Count > 0)
        {
            ulong current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            if (!_byBlueprintId.TryGetValue(current, out var entry)) continue;

            foreach (var m in entry.File.NewMembers)
                yield return m;

            if (entry.File.ParentBlueprintId != 0)
                queue.Enqueue(entry.File.ParentBlueprintId);
            foreach (var contrib in entry.File.ContributingEntries)
                queue.Enqueue(contrib.BlueprintId);
        }
    }
}

public sealed class BlueprintEntry
{
    public ulong BlueprintId { get; init; }
    public string Path { get; init; } = string.Empty;
    public required BlueprintFile File { get; init; }
}
