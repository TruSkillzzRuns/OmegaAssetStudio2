using System.Buffers.Binary;

namespace OmegaAssetStudio2.Core.Calligraphy;

/// <summary>
/// The number the game refers to each definition by, and the name that goes
/// with it.
/// </summary>
/// <remarks>
/// A definition refers to another one by a number, and that number is not the
/// one the archive files it under — the archive's own key and the game's
/// reference are two different hashes of the same name, and matching one
/// against the other resolves nothing at all. The archive carries the map
/// itself, in Calligraphy/Prototype.directory.
/// <para>
/// Its layout, read off the bytes: "PDR" and a version, then how many records
/// follow, then each record as the number, a guid, what kind of definition it
/// is, one flag, the length of the name, and the name. Names are written with
/// backslashes, without the Calligraphy folder in front and without the
/// extension the archive gives them.
/// </para>
/// </remarks>
public static class PrototypeDirectory
{
    private const string Path = "Calligraphy/Prototype.directory";

    /// <summary>Bytes before the name in one record: the number, a guid, a kind, a flag.</summary>
    private const int FixedPart = 8 + 8 + 8 + 1;

    /// <summary>Reads the map. Empty when the archive has no such file.</summary>
    public static IReadOnlyDictionary<ulong, string> Read(PrototypeArchive archive)
    {
        var byId = new Dictionary<ulong, string>();

        byte[]? bytes;
        try { bytes = archive.Read(Path); }
        catch (Exception) { return byId; }

        if (bytes is null || bytes.Length < 8) return byId;
        if (bytes[0] != 'P' || bytes[1] != 'D' || bytes[2] != 'R') return byId;

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (count < 0) return byId;

        int at = 8;

        for (int i = 0; i < count; i++)
        {
            if (at + FixedPart + 2 > bytes.Length) break;

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
            at += FixedPart;

            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
            at += 2;

            if (at + length > bytes.Length) break;

            byId[id] = System.Text.Encoding.UTF8.GetString(bytes, at, length);
            at += length;
        }

        return byId;
    }
}
