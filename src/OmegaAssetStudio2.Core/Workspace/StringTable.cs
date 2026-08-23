using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// The game's own display text, looked up by the number that names it.
/// </summary>
/// <remarks>
/// Every name a player sees lives in four files under the game's data folder,
/// each holding one quarter of the range of a sixty-four bit key. The file name
/// states that quarter's inclusive upper bound, so the file to search is the
/// first whose bound the key does not exceed.
/// <para>
/// A key is not stored whole in any one row. Each row carries the top sixteen
/// bits of its own key and the bottom forty-eight bits of the row after it, so
/// a key is assembled from a row and the one before it. This was verified
/// against all three installed clients: the one known pair resolves to its
/// expected text, and of the roughly two hundred thousand keys rebuilt this
/// way, not one falls outside the quarter its own file covers.
/// </para>
/// <para>
/// These files are read and never written. Nothing here opens them for writing.
/// </para>
/// </remarks>
public sealed class StringTable
{
    /// <summary>Anchors each row in the index region.</summary>
    private const uint RowMarker = 0xFFFF0001;

    private readonly Dictionary<ulong, string> _byKey;

    private StringTable(Dictionary<ulong, string> byKey) => _byKey = byKey;

    /// <summary>How many names were read.</summary>
    public int Count => _byKey.Count;

    /// <summary>Finds the text a key names, or null.</summary>
    public string? Find(ulong key) => _byKey.GetValueOrDefault(key);

    /// <summary>True when a key names something.</summary>
    public bool Contains(ulong key) => _byKey.ContainsKey(key);

    /// <summary>
    /// Reads a game's display text.
    /// </summary>
    /// <param name="installRoot">The game folder.</param>
    /// <param name="language">
    /// Which language to read. The shipped folders are named for it.
    /// </param>
    /// <returns>An empty table when the game ships no text for that language.</returns>
    public static StringTable Load(string installRoot, string language = "eng")
    {
        string folder = Path.Combine(installRoot, "Data", "Game", "Loco", $"{language}.all");

        var byKey = new Dictionary<ulong, string>();
        if (!Directory.Exists(folder)) return new StringTable(byKey);

        foreach (string path in Directory.EnumerateFiles(folder, $"{language}.all_*.string"))
        {
            try { ReadFile(path, byKey); }
            catch (Exception)
            {
                // One unreadable file costs its own quarter of the text, not
                // the rest. Callers see a smaller table, never an exception.
            }
        }

        return new StringTable(byKey);
    }

    private static void ReadFile(string path, Dictionary<ulong, string> byKey)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length < 8 || data[0] != 'S' || data[1] != 'T' || data[2] != 'R') return;

        // The count in the header is only truthful in the first quarter; the
        // others declare tens of millions of rows for a file of about a
        // megabyte. Rows are therefore found by their marker, and the count is
        // used for nothing but reserving space.
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        int reserve = declared is > 0 and < 200_000 ? (int)declared : 4096;

        var markers = new List<int>(reserve);

        for (int i = 8; i <= data.Length - 4; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == RowMarker)
                markers.Add(i);
        }

        if (markers.Count == 0) return;

        // The first row's low bits sit just before the first marker.
        ulong carried = markers[0] >= 8 ? ReadUInt48(data, markers[0] - 8) : 0;

        foreach (int marker in markers)
        {
            if (marker < 2 || marker + 14 > data.Length) continue;

            ushort high = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(marker - 2, 2));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(marker + 4, 4));

            ulong key = ((ulong)high << 48) | carried;
            carried = ReadUInt48(data, marker + 8);

            if (offset == 0 || offset >= data.Length) continue;

            string text = ReadText(data, (int)offset);
            if (text.Length == 0) continue;

            // First writer wins. A repeated key is a stray marker matched inside
            // the text rather than a second name for the same thing.
            byKey.TryAdd(key, text);
        }
    }

    private static ulong ReadUInt48(byte[] data, int at)
    {
        ulong value = 0;
        for (int i = 5; i >= 0; i--) value = (value << 8) | data[at + i];
        return value;
    }

    private static string ReadText(byte[] data, int at)
    {
        int end = at;
        while (end < data.Length && data[end] != 0) end++;

        return end > at ? Encoding.UTF8.GetString(data, at, end - at) : string.Empty;
    }
}
