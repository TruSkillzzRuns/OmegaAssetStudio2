using System.Buffers.Binary;
using System.Text;

namespace OmegaAssetStudio.Calligraphy;

// Parses Target Game Client's `<lang>.all.locale` index file (â‰ˆ72 bytes).
//
// Format (reverse-engineered from `eng.all.locale`):
//   [4 bytes]  "LOC\x02"                              -- magic + version
//   length-prefixed UTF-8 strings (uint16 LE length + bytes):
//     [str]    language code        ("English")
//     [str]    language display     ("English")
//     [str]    region               ("Everywhere")
//     [str]    file-prefix          ("eng.all")
//   [1 byte]   bucket count                            (== 4 in shipped data)
//   [4 bytes]  0xFF 0xFF 0xFF 0xFF                     -- sentinel
//   then per bucket (count times):
//     [uint16 LE]   index               (1, 1, 2, 4 â€” observed)
//     [uint16 LE]   ??? (0x000F)        (omitted for the first bucket)
//     [uint16 LE]   ??? (0x0001)        (omitted for the first bucket)
//     [1 byte]      ASCII letter        ('W','A','S','P' â€” observed)
//
// The four ASCII letters spell "WASP" and label the four `.string` files whose
// names end in 3FFFFFFFFFFFFFFF / 7F.../ BF.../ FF... â€” i.e. the four equal
// quartiles of the 64-bit hash space. They are NOT correlated with prototype
// field type codes (Asset/Prototype/String); the bucket for a given hash H is
// determined purely by `H` value vs the four upper-bound names.
public sealed class LocoLocaleReader
{
    public string LanguageCode { get; }
    public string LanguageDisplay { get; }
    public string Region { get; }
    public string FilePrefix { get; }
    public IReadOnlyList<char> BucketLetters { get; }
    // Upper-bound hash for each bucket index (parallel to BucketLetters), parsed from
    // the sibling directory file names (e.g. eng.all_3FFFFFFFFFFFFFFF.string).
    // Default values match the four shipped buckets.
    public IReadOnlyList<ulong> BucketUpperBounds { get; }

    private LocoLocaleReader(
        string code, string display, string region, string prefix,
        IReadOnlyList<char> letters, IReadOnlyList<ulong> bounds)
    {
        LanguageCode = code;
        LanguageDisplay = display;
        Region = region;
        FilePrefix = prefix;
        BucketLetters = letters;
        BucketUpperBounds = bounds;
    }

    public static LocoLocaleReader Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return ReadBytes(data);
    }

    public static LocoLocaleReader ReadBytes(byte[] data)
    {
        if (data.Length < 8 || data[0] != 'L' || data[1] != 'O' || data[2] != 'C')
            throw new InvalidDataException("Not a LOC file.");

        int pos = 4;
        string code = ReadStr(data, ref pos);
        string display = ReadStr(data, ref pos);
        string region = ReadStr(data, ref pos);
        string prefix = ReadStr(data, ref pos);

        byte bucketCount = data[pos++];
        // Skip 4-byte sentinel (0xFFFFFFFF).
        pos += 4;

        var letters = new List<char>();
        for (int b = 0; b < bucketCount; b++)
        {
            // First bucket lacks the leading 4-byte field cluster; all others have
            // `01 00 0F 00 01 00` (3 little-endian shorts) before the letter.
            ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2)); pos += 2;
            if (b > 0)
            {
                pos += 2; // 0x000F
                pos += 2; // 0x0001
            }
            char letter = (char)data[pos++];
            letters.Add(letter);
            _ = idx;
        }

        // Default bucket bounds for TargetClient (4 quartiles of uint64 space). If we ever
        // see a different bucket count the caller can override via filename parsing.
        var bounds = bucketCount switch
        {
            4 => new ulong[] {
                0x3FFFFFFFFFFFFFFF,
                0x7FFFFFFFFFFFFFFF,
                0xBFFFFFFFFFFFFFFF,
                0xFFFFFFFFFFFFFFFF
            },
            _ => Enumerable.Range(1, bucketCount).Select(i => (ulong)i * (ulong.MaxValue / (ulong)bucketCount)).ToArray()
        };

        return new LocoLocaleReader(code, display, region, prefix, letters, bounds);
    }

    private static string ReadStr(byte[] data, ref int pos)
    {
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
        pos += 2;
        string s = Encoding.UTF8.GetString(data, pos, len);
        pos += len;
        return s;
    }

    // Return the bucket-file suffix (e.g. "3FFFFFFFFFFFFFFF") for a given hash.
    public string BucketFileSuffix(ulong hash)
    {
        for (int i = 0; i < BucketUpperBounds.Count; i++)
            if (hash <= BucketUpperBounds[i])
                return BucketUpperBounds[i].ToString("X16");
        return BucketUpperBounds[^1].ToString("X16");
    }
}

