using System.Buffers.Binary;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>
/// The name behind every asset number a definition can refer to.
/// </summary>
/// <remarks>
/// A definition points at a picture, a sound or a package by a number, and the
/// names those numbers stand for are kept apart from the definitions, in the
/// archive's .type files — one per kind of thing. Powers/Types/PowerIconPathType
/// holds the icon of every power; Entity/Types/EntityIconPathType holds the
/// portraits.
/// <para>
/// Their layout, read off the bytes: "TYP" and a version, how many records
/// follow, then each record as a number, a guid, one flag, the length of the
/// name, and the name. Icon names arrive as package and texture together —
/// <c>MarvelUIIcons.Power_Storm_LightningStorm</c> is the texture
/// Power_Storm_LightningStorm inside ICO__MarvelUIIcons_SF.upk.
/// </para>
/// </remarks>
public static class AssetDirectory
{
    private const int FixedPart = 8 + 8 + 1;

    /// <summary>Reads every asset name in the archive, by the number it is referred to with.</summary>
    public static IReadOnlyDictionary<ulong, string> Read(PrototypeArchive archive)
    {
        var byId = new Dictionary<ulong, string>();

        foreach (ArchiveEntry entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".type", StringComparison.OrdinalIgnoreCase)) continue;

            byte[] bytes;
            try { bytes = archive.Read(entry); }
            catch (Exception) { continue; }

            ReadOne(bytes, byId);
        }

        return byId;
    }

    /// <remarks>
    /// A file that does not read cleanly is left out whole rather than in part.
    /// Six of this game's 258 carry something this does not understand, and
    /// half-reading one would file real numbers under names taken from the
    /// wrong place.
    /// </remarks>
    private static void ReadOne(byte[] bytes, Dictionary<ulong, string> byId)
    {
        if (bytes.Length < 6 || bytes[0] != 'T' || bytes[1] != 'Y' || bytes[2] != 'P') return;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        int at = 6;

        var read = new List<(ulong Id, string Name)>(count);

        for (int i = 0; i < count; i++)
        {
            if (at + FixedPart + 2 > bytes.Length) return;

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
            at += FixedPart;

            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
            at += 2;

            if (at + length > bytes.Length) return;

            read.Add((id, System.Text.Encoding.UTF8.GetString(bytes, at, length)));
            at += length;
        }

        // A clean read lands exactly on the end of the file.
        if (at != bytes.Length) return;

        foreach ((ulong id, string name) in read) byId[id] = name;
    }
}
