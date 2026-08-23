using System.Buffers.Binary;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>
/// What each field in a definition is called.
/// </summary>
/// <remarks>
/// A definition stores its fields by number, and the number is all the
/// definition itself carries. Which number means which field is written down in
/// the blueprints — one per kind of thing, naming every field that kind has.
/// <para>
/// Without them a caller is reduced to guessing from the values. A costume
/// names four pictures and two of them are its portrait, at two sizes; the
/// other two are its tile in the store and a banner, and one of those is also
/// named twice. Nothing about the values tells them apart, so Cyclops's Noir
/// costume came up wearing its store tile. The blueprint says which field is
/// PortraitIconPath and the question stops being a guess.
/// </para>
/// <para>
/// The layout, read off the bytes: "BPT" and a version, the name of the kind,
/// eight bytes, then a run of what it builds on and a run of what it can point
/// at — each eight bytes and a byte — and then the members, each as a number,
/// the length of its name, the name, what kind of value it holds, whether it
/// holds one or many, and which type it is.
/// </para>
/// </remarks>
public static class BlueprintFields
{
    private const string Extension = ".blueprint";

    /// <summary>Reads every field name in the archive, by the number it is stored under.</summary>
    public static IReadOnlyDictionary<ulong, string> Read(PrototypeArchive archive)
    {
        var byId = new Dictionary<ulong, string>();

        foreach (ArchiveEntry entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

            byte[] bytes;
            try { bytes = archive.Read(entry); }
            catch (Exception) { continue; }

            ReadOne(bytes, byId);
        }

        return byId;
    }

    /// <summary>
    /// Kinds of value that name the set they are drawn from.
    /// </summary>
    /// <remarks>
    /// A member that holds an asset, points at another definition, holds a
    /// whole definition inside it, or reads from a curve carries eight bytes
    /// saying which set of them it may hold. One that holds a plain string or a
    /// number does not, and reading those eight bytes anyway walks straight
    /// over the next member's name — Costume's own PortraitIconPath sits after
    /// two plain strings, which is why it was being missed.
    /// <para>
    /// Which kinds carry it was settled by counting: with these four, 5,627 of
    /// this game's 5,659 blueprints parse to their exact last byte, against
    /// 5,188 with assets and references alone. Adding the type kind takes it
    /// back down, so it does not carry one.
    /// </para>
    /// </remarks>
    private static bool Bounded(char kind) => kind is 'A' or 'P' or 'R' or 'C';

    private static void ReadOne(byte[] bytes, Dictionary<ulong, string> byId)
    {
        if (bytes.Length < 6 || bytes[0] != 'B' || bytes[1] != 'P' || bytes[2] != 'T') return;

        int at = 4;

        if (!Skip(bytes, ref at, out _)) return;   // the name of the kind

        at += 8;                                    // what the engine binds it to

        if (!Run(bytes, ref at)) return;            // what it builds on
        if (!Run(bytes, ref at)) return;            // what it can point at

        if (at + 2 > bytes.Length) return;

        int members = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
        at += 2;

        var read = new List<(ulong Id, string Name)>(members);

        for (int i = 0; i < members; i++)
        {
            if (at + 8 > bytes.Length) return;

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
            at += 8;

            if (!Skip(bytes, ref at, out string name)) return;

            if (at + 2 > bytes.Length) return;

            char kind = (char)bytes[at];
            at += 1 + 1;                            // what it holds, and one or many

            if (Bounded(kind)) at += 8;

            if (at > bytes.Length) return;

            if (name.Length > 0) read.Add((id, name));
        }

        // A clean read lands exactly on the end of the file. Anything else is a
        // layout this does not understand, and half of it is worse than none:
        // the names would be right and the numbers beside them would not.
        if (at != bytes.Length) return;

        foreach ((ulong id, string name) in read) byId[id] = name;
    }

    /// <summary>A run of eight-byte numbers, each with a byte after it.</summary>
    private static bool Run(byte[] bytes, ref int at)
    {
        if (at + 2 > bytes.Length) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
        at += 2 + (count * 9);

        return at <= bytes.Length;
    }

    private static bool Skip(byte[] bytes, ref int at, out string name)
    {
        name = string.Empty;

        if (at + 2 > bytes.Length) return false;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
        at += 2;

        if (at + length > bytes.Length) return false;

        name = System.Text.Encoding.UTF8.GetString(bytes, at, length);
        at += length;

        return true;
    }
}
