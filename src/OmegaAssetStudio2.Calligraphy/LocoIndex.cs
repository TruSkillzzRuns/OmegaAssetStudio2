using System.Buffers.Binary;

namespace OmegaAssetStudio.Calligraphy;

// Top-level faÃ§ade over the Target Game Client localization system.
//
// Usage:
//   var loco = LocoIndex.Open(@"E:\SteamLibrary\steamapps\common\Target Game\Data\Game\Loco", "eng");
//   if (loco.TryResolveString(0x3210183D24E6050B, out var text)) ...
//
// IMPORTANT â€” current state of the reverse-engineering effort:
//   â€¢ The locale header (`eng.all.locale`) and the per-bucket file header
//     (`eng.all_<bound>.string`) are both fully decoded.
//   â€¢ The strings BLOB inside each .string file is fully decoded and is a
//     simple NUL-separated UTF-8 buffer. Strings can be extracted by offset.
//   â€¢ The 16-byte INDEX rows are PARTIALLY decoded. We know that bytes
//     [10..13] in each row hold the uint32 absolute byte offset of that
//     row's string within the file. The hash-to-row mapping (i.e. how a
//     prototype field hash like 0x3210183D24E6050B selects row e15050)
//     is NOT yet solved -- the hash bytes do not appear verbatim in the
//     index, and the visible 6-byte sort key does not have a clean linear
//     relationship to the full 64-bit prototype hash.
//
//   Consequently `TryResolveString` will currently fail (returns false)
//   for any input hash that is not also present in the auxiliary text-name
//   cache. The bucket selection, file loading, and string extraction
//   pipelines are all in place; only the final index lookup needs more
//   reverse-engineering input (specifically, a second known
//   (prototype-hash, expected-text) pair from a different power).
//
// Also note: 'A' (Asset) field hashes â€” e.g. a known Ultimate IconPath
// 0x1469B7BBA1A015BF â€” are NOT in the locale files at all. A linear
// byte-search across all four eng.all .string files (LE8 and BE8 forms)
// returned zero matches. Asset hashes resolve through a different system
// (likely Calligraphy `Prototype.directory`-style mapping into UPK paths)
// which is out of scope for this Loco reader.
public sealed class LocoIndex : IDisposable
{
    public LocoLocaleReader Locale { get; }
    private readonly string _bucketDir;
    private readonly LocoStringFileReader?[] _buckets;

    private LocoIndex(LocoLocaleReader locale, string bucketDir)
    {
        Locale = locale;
        _bucketDir = bucketDir;
        _buckets = new LocoStringFileReader?[locale.BucketUpperBounds.Count];
    }

    // Open the LocoIndex from `<gameDataLocoDir>` and a language token
    // (e.g. "eng"). `gameDataLocoDir` should be the directory that contains
    // both `<lang>.all.locale` and the `<lang>.all/` subdirectory.
    public static LocoIndex Open(string gameDataLocoDir, string languageToken)
    {
        string localePath = Path.Combine(gameDataLocoDir, $"{languageToken}.all.locale");
        if (!File.Exists(localePath))
            throw new FileNotFoundException($"Locale file not found: {localePath}");

        var locale = LocoLocaleReader.Read(localePath);
        string bucketDir = Path.Combine(gameDataLocoDir, $"{languageToken}.all");
        if (!Directory.Exists(bucketDir))
            throw new DirectoryNotFoundException($"Bucket directory not found: {bucketDir}");

        return new LocoIndex(locale, bucketDir);
    }

    public int BucketIndexFor(ulong hash)
    {
        for (int i = 0; i < Locale.BucketUpperBounds.Count; i++)
            if (hash <= Locale.BucketUpperBounds[i])
                return i;
        return Locale.BucketUpperBounds.Count - 1;
    }

    public LocoStringFileReader GetBucket(int index)
    {
        if (_buckets[index] is { } cached) return cached;
        string suffix = Locale.BucketUpperBounds[index].ToString("X16");
        string filePath = Path.Combine(_bucketDir, $"{Locale.FilePrefix}_{suffix}.string");
        var reader = LocoStringFileReader.Read(filePath);
        _buckets[index] = reader;
        return reader;
    }

    // Resolve a 64-bit prototype field hash to its localized text by picking
    // the bucket file the hash falls into and looking the hash up there.
    public bool TryResolveString(ulong hash, out string text)
    {
        int bucket = BucketIndexFor(hash);
        var reader = GetBucket(bucket);
        return reader.TryGetString(hash, out text);
    }

    public void Dispose() { }
}

