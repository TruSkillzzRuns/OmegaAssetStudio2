using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>
/// What it takes to bring a sound's name into a package that does not have it.
/// </summary>
/// <remarks>
/// A package names sounds; it holds none. So giving a costume a line it has
/// never had is not a matter of adding audio - it is adding the three small
/// entries that name one, and then wiring a moment to them.
/// <para>
/// Those three go together, and the shape is the same in every package looked
/// at. A group entry stands for one bank of the middleware's; the bank entry
/// hangs under it; and every event of that bank hangs under it too, each
/// naming the bank it needs. A group is a few bytes, a bank twelve, an event
/// forty.
/// </para>
/// <para>
/// Nothing here writes. It works out what is missing and what it would be
/// copied from, and hands that back to be looked at before anything is done.
/// </para>
/// </remarks>
public static class SoundImport
{
    /// <summary>One entry that would be brought across.</summary>
    /// <param name="Name">What it is called.</param>
    /// <param name="Kind">What sort of thing it is.</param>
    /// <param name="SourceAt">Where it sits in the package it comes from.</param>
    /// <param name="Under">
    /// The group it hangs under, or empty for a group itself.
    /// </param>
    public sealed record Coming(string Name, string Kind, int SourceAt, string Under);

    /// <summary>What bringing some sounds across would come to.</summary>
    /// <param name="Groups">Groups the target has not got.</param>
    /// <param name="Banks">Banks the target has not got.</param>
    /// <param name="Events">The sounds themselves.</param>
    /// <param name="AlreadyThere">Sounds the target already names.</param>
    /// <param name="Trouble">Why it cannot be done, where it cannot.</param>
    public sealed record Plan(
        IReadOnlyList<Coming> Groups,
        IReadOnlyList<Coming> Banks,
        IReadOnlyList<Coming> Events,
        IReadOnlyList<string> AlreadyThere,
        string Trouble)
    {
        /// <summary>Whether there is anything to do and nothing stopping it.</summary>
        public bool Worthwhile => Trouble.Length == 0 && Events.Count > 0;

        /// <summary>Everything to be added, groups first, then banks, then sounds.</summary>
        public IEnumerable<Coming> All => Groups.Concat(Banks).Concat(Events);
    }

    /// <summary>
    /// Works out what bringing named sounds from one package to another needs.
    /// </summary>
    /// <remarks>
    /// The target must already name sounds of its own. That is not a
    /// convenience: an entry has to say what sort of thing it is, and it says
    /// so by pointing at the class in the engine's own package. A package that
    /// has never held a sound does not point at those classes, and making it do
    /// so is a different and larger piece of work than this.
    /// </remarks>
    public static Plan Work(Package target, Package source, IReadOnlyCollection<string> eventNames)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(eventNames);

        var groups = new List<Coming>();
        var banks = new List<Coming>();
        var events = new List<Coming>();
        var already = new List<string>();

        if (eventNames.Count == 0)
            return new(groups, banks, events, already, "no sounds were named");

        // The target has to know what a sound entry is before it can hold one.
        if (!Names(target, "akevent") || !Names(target, "akbank"))
        {
            return new(groups, banks, events, already,
                "this package has never held a sound, so it does not name the engine's sound "
                + "classes. Bringing the first one in is a larger piece of work than adding to "
                + "a package that already has some.");
        }

        var wanted = new HashSet<string>(eventNames, StringComparer.OrdinalIgnoreCase);

        var hasEvent = Where(target, "akevent");
        var hasBank = Where(target, "akbank");
        var hasGroup = Where(target, "package");

        var addingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addingBanks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < source.Exports.Count; i++)
        {
            string kind, named;

            try { kind = source.GetExportClassName(i); named = source.GetExportName(i); }
            catch (Exception) { continue; }

            if (!kind.Equals("akevent", StringComparison.OrdinalIgnoreCase)) continue;
            if (!wanted.Contains(named)) continue;

            if (hasEvent.ContainsKey(named)) { already.Add(named); continue; }

            // The group it hangs under, and the bank it asks for. Both have to
            // be there before it can be.
            string group = OuterName(source, i);

            if (group.Length > 0 && !hasGroup.ContainsKey(group) && addingGroups.Add(group))
            {
                int groupAt = Find(source, group, "package");

                if (groupAt >= 0) groups.Add(new Coming(group, "package", groupAt, string.Empty));
            }

            string bank = BankOf(source, i);

            if (bank.Length > 0 && !hasBank.ContainsKey(bank) && addingBanks.Add(bank))
            {
                int bankAt = Find(source, bank, "akbank");

                if (bankAt >= 0) banks.Add(new Coming(bank, "akbank", bankAt, OuterName(source, bankAt)));
            }

            events.Add(new Coming(named, "akevent", i, group));
        }

        string trouble = events.Count == 0 && already.Count == 0
            ? "none of those sounds are in that package"
            : string.Empty;

        return new(groups, banks, events, already, trouble);
    }

    /// <summary>What an export hangs under, by name.</summary>
    public static string OuterName(Package package, int exportIndex)
    {
        ObjectReference outer = package.Exports[exportIndex].Outer;

        if (!outer.IsExport) return string.Empty;

        try { return package.GetExportName(outer.ExportIndex); }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>The bank an event asks for, by name.</summary>
    public static string BankOf(Package package, int exportIndex)
    {
        PropertyBag? bag;
        try { bag = package.TryReadProperties(exportIndex); }
        catch (Exception) { return string.Empty; }

        PropertyTag? asked = bag?.Find("RequiredBank");

        if (asked is null || asked.Value.Length < 4) return string.Empty;

        int which = BitConverter.ToInt32(asked.Value.ToArray());

        if (which <= 0 || which - 1 >= package.Exports.Count) return string.Empty;

        try { return package.GetExportName(which - 1); }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>Where a package keeps each export of one class, by name.</summary>
    public static Dictionary<string, int> Where(Package package, string className)
    {
        ArgumentNullException.ThrowIfNull(package);

        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            string kind;
            try { kind = package.GetExportClassName(i); }
            catch (Exception) { continue; }

            if (!kind.Equals(className, StringComparison.OrdinalIgnoreCase)) continue;

            try { found.TryAdd(package.GetExportName(i), i); }
            catch (Exception) { }
        }

        return found;
    }

    private static int Find(Package package, string name, string className)
    {
        for (int i = 0; i < package.Exports.Count; i++)
        {
            try
            {
                if (!package.GetExportName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!package.GetExportClassName(i).Equals(className, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch (Exception) { continue; }

            return i;
        }

        return -1;
    }

    private static bool Names(Package package, string what)
    {
        for (int i = 0; i < package.Names.Count; i++)
            if (package.Names.GetName(i).Equals(what, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
