using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>
/// The sounds a package hangs on a character, and how they got there.
/// </summary>
/// <remarks>
/// A package does not hold the sounds themselves. It holds their names and the
/// wiring: an entry naming an event, an entry naming the bank that event lives
/// in, and a table saying which moment plays which event. Measured on a costume
/// as it ships, all of that comes to eighteen kilobytes of a seven-megabyte
/// package, a bank entry being twelve bytes and an event forty. The sounds
/// themselves are in the containers beside it.
/// <para>
/// That is what makes taking a sound out again cheap. Nothing needs deleting:
/// the wiring is what makes a sound play, and on a costume the whole of it sits
/// in one table. Put that table back the way it shipped and the sounds stop,
/// whatever else has been done to the package. What is left behind is named by
/// nothing and does nothing.
/// </para>
/// <para>
/// Deleting the entries properly would mean renumbering, because everything in
/// a package refers to everything else by its place in the table, and those
/// references are not all in property tables - a model keeps its list of
/// materials inside its own body. Getting that wrong quietly ruins models. It
/// buys back eighteen kilobytes.
/// </para>
/// </remarks>
public static class PackageSounds
{
    /// <summary>The classes that name a sound rather than play one.</summary>
    private static readonly string[] Naming = ["akevent", "akbank"];

    /// <summary>
    /// One moment of a character's life, and the sound it plays.
    /// </summary>
    /// <param name="Holder">Where the table this sits in is kept.</param>
    /// <param name="HolderName">What that table is called.</param>
    /// <param name="HolderKind">What sort of thing holds it.</param>
    /// <param name="Moment">
    /// What the table calls this slot, which is the moment it plays at: the
    /// death cry, the line on being revived, the grunt for a power that cannot
    /// be afforded.
    /// </param>
    /// <param name="Sound">The event it names.</param>
    public sealed record Hook(
        int Holder, string HolderName, string HolderKind, string Moment, string Sound);

    /// <summary>
    /// How one export of a changed package stands against the one it shipped as.
    /// </summary>
    public sealed record Difference(
        string Name,
        string Kind,
        int ChangedAt,
        int ShippedAt,
        int ChangedSize,
        int ShippedSize,
        int Hooks)
    {
        /// <summary>Whether the shipped package has no such export at all.</summary>
        public bool IsNew => ShippedAt < 0;

        /// <summary>Whether it is in both, and no longer the same.</summary>
        public bool IsAltered => ShippedAt >= 0 && ChangedSize != ShippedSize;
    }

    /// <summary>
    /// Every moment-to-sound wiring a package holds.
    /// </summary>
    /// <remarks>
    /// Found rather than expected. Every export is read, and any property
    /// pointing at an entry that names a sound is a wiring - whoever put it
    /// there, and with whatever tool. That is what lets this answer for a
    /// package it has never seen, which matters because the packages this is
    /// for were changed by something else.
    /// </remarks>
    public static IReadOnlyList<Hook> Read(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Which exports merely name a sound. A property pointing at one of
        // these is a wiring; a property pointing anywhere else is not.
        var names = new Dictionary<int, string>();

        for (int i = 0; i < package.Exports.Count; i++)
        {
            string kind;
            try { kind = package.GetExportClassName(i); }
            catch (Exception) { continue; }

            if (!Naming.Contains(kind, StringComparer.OrdinalIgnoreCase)) continue;

            try { names[i] = package.GetExportName(i); }
            catch (Exception) { }
        }

        if (names.Count == 0) return [];

        var found = new List<Hook>();

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (names.ContainsKey(i)) continue;

            PropertyBag? bag;
            try { bag = package.TryReadProperties(i); }
            catch (Exception) { continue; }
            if (bag is null) continue;

            string holderName, holderKind;

            try
            {
                holderName = package.GetExportName(i);
                holderKind = package.GetExportClassName(i);
            }
            catch (Exception) { continue; }

            foreach (PropertyTag tag in bag.Tags)
            {
                foreach (int at in PointsAt(tag, where => names.ContainsKey(where)))
                {
                    if (!names.TryGetValue(at - 1, out string? sound)) continue;

                    found.Add(new Hook(i, holderName, holderKind, tag.Name, sound));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// What a changed package holds for sound that the shipped one does not.
    /// </summary>
    /// <remarks>
    /// Matched by name and class rather than by where they sit, because putting
    /// anything into a package moves along everything after it: one costume's
    /// sound table shipped as the nineteenth export and sat at the eighteenth
    /// once a mod had been through it.
    /// </remarks>
    public static IReadOnlyList<Difference> Compare(Package changed, Package shipped)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(shipped);

        Dictionary<string, int> before = Places(shipped);

        IReadOnlyList<Hook> hooks = Read(changed);

        var differences = new List<Difference>();

        for (int i = 0; i < changed.Exports.Count; i++)
        {
            string name, kind;

            try { name = changed.GetExportName(i); kind = changed.GetExportClassName(i); }
            catch (Exception) { continue; }

            int carries = hooks.Count(h => h.Holder == i);

            // Anything that names a sound, and anything that wires one up.
            bool worthSaying = carries > 0
                               || Naming.Contains(kind, StringComparer.OrdinalIgnoreCase);

            if (!worthSaying) continue;

            int was = before.GetValueOrDefault(Key(changed, i), -1);

            int now = Size(changed, i);
            int then = was >= 0 ? Size(shipped, was) : -1;

            // In both, the same size, and wiring nothing up: nothing to say.
            if (was >= 0 && now == then && carries == 0) continue;

            differences.Add(new Difference(name, kind, i, was, now, then, carries));
        }

        return differences;
    }

    /// <summary>One export to put back, and the bytes it shipped as.</summary>
    public sealed record PutBack(int ChangedAt, string Name, byte[] Shipped);

    /// <summary>
    /// Which exports can be put back the way the shipped package has them, and
    /// the bytes to put there.
    /// </summary>
    /// <remarks>
    /// The bytes are carried across whole rather than worked out, so what goes
    /// back is what shipped, to the byte. Only what is named here is touched;
    /// everything else in the package stays as it stands, which is the whole
    /// point - a sound put right should not cost the pictures, the models and
    /// the animations that were imported alongside it.
    /// <para>
    /// An export the shipped package never had is left out, because there is
    /// nothing to put back. That covers the sounds an imported animation
    /// carries in its own notifies: the animation is not in the shipped
    /// package at all, so quieting one means editing the animation rather than
    /// restoring it.
    /// </para>
    /// <para>
    /// Nothing is written to disk here. The caller is handed the bytes and
    /// decides what becomes of them.
    /// </para>
    /// </remarks>
    /// <param name="bytesOf">How to get one export's bytes out of a package.</param>
    public static IReadOnlyList<PutBack> WhatToPutBack(
        Package changed,
        Package shipped,
        IReadOnlyCollection<string> exportNames,
        Func<Package, int, byte[]> bytesOf)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(exportNames);
        ArgumentNullException.ThrowIfNull(bytesOf);

        if (exportNames.Count == 0) return [];

        Dictionary<string, int> before = Places(shipped);

        var wanted = new HashSet<string>(exportNames, StringComparer.OrdinalIgnoreCase);

        var found = new List<PutBack>();

        for (int i = 0; i < changed.Exports.Count; i++)
        {
            string name;
            try { name = changed.GetExportName(i); }
            catch (Exception) { continue; }

            if (!wanted.Contains(name)) continue;

            int was = before.GetValueOrDefault(Key(changed, i), -1);
            if (was < 0) continue;

            found.Add(new PutBack(i, name, bytesOf(shipped, was)));
        }

        return found;
    }

    /// <summary>
    /// One export's bytes with named moments taken out of its table.
    /// </summary>
    /// <remarks>
    /// A moment is one tagged property, and the reader records where each tag
    /// begins and how long it runs. So a moment is quieted by cutting its run
    /// out of the bytes and leaving everything either side alone - no table is
    /// built again from what was read of it, which is what would risk writing a
    /// name or a length back differently from how it was found.
    /// <para>
    /// Taking a property out is the right way to quiet one rather than a
    /// shortcut. A cooked package writes down only what differs from the class
    /// it is built on, so a property that is not there is not silence - it is
    /// whatever the class says, which is what the character would have done had
    /// nobody been at it. On the costume this was measured against, the table
    /// shipped holding nothing at all, so every moment in it is an addition and
    /// cutting one out leaves exactly what shipped.
    /// </para>
    /// </remarks>
    /// <returns>The new bytes, or nothing where no such moment is there.</returns>
    public static byte[]? WithoutMoments(
        Package package, int exportIndex, IReadOnlyCollection<string> moments)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(moments);

        if (moments.Count == 0) return null;

        PropertyBag? bag;
        try { bag = package.TryReadProperties(exportIndex); }
        catch (Exception) { return null; }
        if (bag is null) return null;

        var wanted = new HashSet<string>(moments, StringComparer.OrdinalIgnoreCase);

        // Where each one runs, in the order they sit.
        var cuts = bag.Tags
            .Where(t => wanted.Contains(t.Name))
            .Select(t => (Start: t.TagOffset, End: t.TagOffset + t.TotalSize))
            .OrderBy(c => c.Start)
            .ToList();

        if (cuts.Count == 0) return null;

        byte[] was;
        try { was = package.GetExportData(exportIndex).ToArray(); }
        catch (Exception) { return null; }

        var kept = new List<byte>(was.Length);

        int at = 0;

        foreach ((int start, int end) in cuts)
        {
            if (start < at || end > was.Length) return null;

            kept.AddRange(was.AsSpan(at, start - at).ToArray());

            at = end;
        }

        kept.AddRange(was.AsSpan(at).ToArray());

        return kept.ToArray();
    }

    /// <summary>Where a package keeps each of its exports, by name and class.</summary>
    private static Dictionary<string, int> Places(Package package)
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < package.Exports.Count; i++)
        {
            try { found[Key(package, i)] = i; }
            catch (Exception) { }
        }

        return found;
    }

    /// <summary>An export's name and class together, for matching across packages.</summary>
    private static string Key(Package package, int at) =>
        package.GetExportName(at) + " " + package.GetExportClassName(at);

    private static int Size(Package package, int at)
    {
        try { return package.GetExportData(at).Length; }
        catch (Exception) { return -1; }
    }

    /// <summary>
    /// Every export a property points at, where it really points at anything.
    /// </summary>
    /// <remarks>
    /// A property that holds one thing says so, and is taken at its word. A
    /// list is harder, because a cooked package does not write down what a list
    /// holds: a list of references and a list of plain numbers look exactly
    /// alike, four bytes to each of them, and only the class the package was
    /// built from says which it is.
    /// <para>
    /// Reading every four-byte list as references is wrong, and quietly. One
    /// animation keeps 356 numbers of packed movement in such a list; read as
    /// places in the table, 52 of this one costume's animations appeared to
    /// name sounds they have nothing to do with, because a small number lands
    /// on some export or other and 161 of that package's 404 exports name a
    /// sound. It reported 206 sounds that were not there.
    /// </para>
    /// <para>
    /// So a list counts only where every one of its entries lands on something
    /// naming a sound. Packed movement does not: its numbers land all over the
    /// table. Two real lists in that package pass, 52 false ones do not. A list
    /// of larger things is left alone altogether, since what sits where inside
    /// one of them is not something to be guessed at.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> PointsAt(PropertyTag tag, Func<int, bool> namesASound)
    {
        byte[] raw = tag.Value.ToArray();

        if (tag.TypeName.Equals("objectproperty", StringComparison.OrdinalIgnoreCase))
        {
            if (raw.Length >= 4) yield return BitConverter.ToInt32(raw);

            yield break;
        }

        if (!tag.TypeName.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase)) yield break;
        if (raw.Length < 8) yield break;

        int many = BitConverter.ToInt32(raw);

        if (many <= 0) yield break;

        // Four bytes to each, and nothing over: anything else is not a list of
        // references at all.
        if (raw.Length - 4 != many * 4) yield break;

        var held = new List<int>(many);

        for (int i = 0; i < many; i++)
        {
            int one = BitConverter.ToInt32(raw, 4 + (i * 4));

            if (one == 0) continue;

            if (!namesASound(one - 1)) yield break;

            held.Add(one);
        }

        foreach (int one in held) yield return one;
    }
}
