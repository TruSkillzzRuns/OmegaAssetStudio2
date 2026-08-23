using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// Whether a base material's compiled form actually cuts holes in what it
/// draws.
/// </summary>
/// <remarks>
/// A base material's BlendMode is what it was authored as, and it is not what
/// its shaders were built to do. The cache files the two apart -
/// ChBaseMaterial_v2-1 sits beside ChBaseMaterial_v2-1_masked - and a costume
/// resolves to one or the other through its own static parameters.
/// <para>
/// Every base pass carries one texkill for the screen-door fade the engine
/// applies to everything. A material that cuts holes carries a second, fed by
/// its opacity mask, and that second one is the whole of the difference. One
/// armoured costume resolves to ChBaseMaterial_v2-1, whose base pass carries
/// one - and reading its BlendMode instead cut its cape away entirely, because
/// 38 per cent of that costume's colour map has an alpha of nothing.
/// </para>
/// </remarks>
public static class CompiledCuts
{
    private const string CacheName = "RefShaderCache-PC-D3D-SM3.upk";

    /// <summary>The instruction that throws a pixel away.</summary>
    private const int TexKill = 0x41;

    /// <summary>
    /// The one base pass every material compiles, read for all of them so the
    /// count of what each throws away is comparable.
    /// </summary>
    private const string BasePass = "TBasePassPixelShaderFDirectionalLightLightMapPolicyNoSkyLight";

    /// <summary>
    /// The one every base pass carries, for the fade the engine applies to
    /// everything. A material has to carry more than this to be cutting holes
    /// of its own.
    /// </summary>
    private const int ScreenDoorFade = 1;

    private static readonly Dictionary<string, IReadOnlyDictionary<string, bool>> Read =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Lock = new();

    /// <summary>
    /// Whether the base material of this name cuts holes, as its compiled base
    /// pass does or does not.
    /// </summary>
    /// <remarks>
    /// Answers yes for anything the cache has nothing to say about, so a game
    /// folder whose cache cannot be read cuts exactly what it always cut.
    /// </remarks>
    public static bool Cuts(string cookedPath, string baseName)
    {
        if (string.IsNullOrEmpty(cookedPath) || string.IsNullOrEmpty(baseName)) return true;

        IReadOnlyDictionary<string, bool> known = ForFolder(cookedPath);

        // The chain names a base by its whole path; the cache names it by
        // itself.
        int cut = baseName.LastIndexOf('.');
        string leaf = cut < 0 ? baseName : baseName[(cut + 1)..];

        return known.TryGetValue(leaf, out bool cuts) ? cuts : true;
    }

    private static IReadOnlyDictionary<string, bool> ForFolder(string cookedPath)
    {
        lock (Lock)
        {
            if (Read.TryGetValue(cookedPath, out IReadOnlyDictionary<string, bool>? already)) return already;
        }

        IReadOnlyDictionary<string, bool> found = Walk(cookedPath);

        lock (Lock) Read[cookedPath] = found;

        return found;
    }

    private static IReadOnlyDictionary<string, bool> Walk(string cookedPath)
    {
        var cuts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        string path = Path.Combine(cookedPath, CacheName);
        if (!File.Exists(path)) return cuts;

        try
        {
            Package cache = Package.Open(path);

            for (int i = 0; i < cache.Exports.Count; i++)
            {
                if (!cache.GetExportClassName(i).Contains("ShaderCache", StringComparison.OrdinalIgnoreCase))
                    continue;

                PropertyBag? bag = cache.TryReadProperties(i);
                if (bag is null) continue;

                Gather(cache, cache.GetExportData(i).ToArray(), bag.PayloadOffset,
                       cache.Exports[i].SerialOffset, cuts);
                break;
            }
        }
        catch (Exception)
        {
            cuts.Clear();
        }

        return cuts;
    }

    private static void Gather(
        Package cache, byte[] data, int at, int fileStart, Dictionary<string, bool> cuts)
    {
        at += 4 + 1;
        if (Int(data, ref at) != 0) return;

        int shaders = Int(data, ref at);

        // Every compiled shader, filed by the identity its map will name.
        var code = new Dictionary<string, (bool BasePass, int At, int Length)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < shaders; i++)
        {
            string kind = Name(cache, data, ref at);
            string id = Show(data, at);
            at += 16 + 20;

            int resume = Int(data, ref at) - fileStart;
            int table = Int(data, ref at);
            at += (table * 2) + 2;

            int length = Int(data, ref at);
            if (length < 0 || at + length > data.Length) return;

            // One kind of shader, the same one for every material, so the
            // count means the same thing each time it is read. A material
            // compiles its base pass several ways - lit by a directional, by
            // spherical harmonics, by nothing, with a sky and without - and
            // they do not all carry the same instructions.
            code[id] = (kind.Equals(BasePass, StringComparison.OrdinalIgnoreCase), at, length);

            if (resume <= at || resume > data.Length) return;
            at = resume;
        }

        int maps = Int(data, ref at);

        var tallies = new Dictionary<string, (int Cuts, int Whole)>(StringComparer.OrdinalIgnoreCase);

        for (int m = 0; m < maps; m++)
        {
            if (!GatherMap(cache, data, ref at, fileStart, code, tallies)) break;
        }

        // What most of a name's compiled forms do is what that material does.
        foreach ((string friendly, (int cut, int whole)) in tallies) cuts[friendly] = cut > whole;
    }

    private static bool GatherMap(
        Package cache, byte[] data, ref int at, int fileStart,
        Dictionary<string, (bool BasePass, int At, int Length)> code,
        Dictionary<string, (int Cuts, int Whole)> tallies)
    {
        try
        {
            SkipChoices(data, ref at);
            at += 8;

            int resume = Int(data, ref at) - fileStart;

            var used = new List<string>();

            int named = Int(data, ref at);
            for (int i = 0; i < named; i++)
            {
                Name(cache, data, ref at);
                used.Add(Show(data, at));
                at += 16;
                Name(cache, data, ref at);
            }

            int meshes = Int(data, ref at);
            for (int k = 0; k < meshes; k++)
            {
                int inside = Int(data, ref at);
                for (int i = 0; i < inside; i++)
                {
                    Name(cache, data, ref at);
                    used.Add(Show(data, at));
                    at += 16;
                    Name(cache, data, ref at);
                }

                Name(cache, data, ref at);
            }

            at += 16;                                    // what the cache files it under
            string friendly = Text(data, ref at);

            if (friendly.Length > 0)
            {
                int most = 0;

                foreach (string id in used)
                {
                    if (!code.TryGetValue(id, out (bool BasePass, int At, int Length) found)) continue;
                    if (!found.BasePass) continue;

                    int thrown = Thrown(data, found.At, found.Length);
                    if (thrown > most) most = thrown;
                }

                // A name that appears more than once in the cache is counted,
                // not merged: taking any one of them as speaking for the rest
                // is how the value ends up depending on the order of the file.
                if (most > 0)
                {
                    (int Cuts, int Whole) tally = tallies.GetValueOrDefault(friendly);

                    tallies[friendly] = most > ScreenDoorFade
                        ? (tally.Cuts + 1, tally.Whole)
                        : (tally.Cuts, tally.Whole + 1);
                }
            }

            if (resume <= at || resume > data.Length) return false;
            at = resume;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>How many times a compiled shader throws a pixel away.</summary>
    private static int Thrown(byte[] data, int at, int length)
    {
        int end = at + length;
        int position = at + 4;                           // past the version token
        int found = 0;

        while (position + 4 <= end)
        {
            uint word = BitConverter.ToUInt32(data, position);
            if (word == 0x0000FFFF) break;               // the end token

            // A comment carries its length in words, in bits 16 to 30.
            if ((word & 0xFFFF) == 0xFFFE)
            {
                position += 4 + ((int)((word >> 16) & 0x7FFF) * 4);
                continue;
            }

            if ((word & 0xFFFF) == TexKill) found++;

            int words = (int)((word >> 24) & 0x0F);
            position += words <= 0 ? 4 : 4 + (words * 4);
        }

        return found;
    }

    private static void SkipChoices(byte[] data, ref int at)
    {
        at += 16;
        int switches = Count(data, ref at);
        for (int i = 0; i < switches; i++) at += 8 + 4 + 4 + 16;
        int masks = Count(data, ref at);
        for (int i = 0; i < masks; i++) at += 8 + (4 * 4) + 4 + 16;
        int normals = Count(data, ref at);
        for (int i = 0; i < normals; i++) at += 8 + 1 + 4 + 16;
        int layers = Count(data, ref at);
        for (int i = 0; i < layers; i++) at += 8 + 4 + 4 + 16;
    }

    private static int Count(byte[] data, ref int at)
    {
        int count = Int(data, ref at);
        if (count < 0 || count > 65536) throw new InvalidOperationException("a run of choices is too long");
        return count;
    }

    private static string Name(Package cache, byte[] data, ref int at)
    {
        int index = Int(data, ref at);
        int number = Int(data, ref at);

        if ((uint)index >= (uint)cache.Names.Count) throw new InvalidOperationException("not a name");

        return cache.Names.Resolve(index, number);
    }

    private static string Text(byte[] data, ref int at)
    {
        int count = Int(data, ref at);
        if (count == 0) return string.Empty;

        if (count > 0)
        {
            if (at + count > data.Length) throw new InvalidOperationException("past the end");

            string made = System.Text.Encoding.ASCII.GetString(data, at, Math.Max(0, count - 1));
            at += count;
            return made;
        }

        int letters = -count;
        if (at + (letters * 2) > data.Length) throw new InvalidOperationException("past the end");

        at += letters * 2;
        return string.Empty;
    }

    private static string Show(byte[] data, int at) => Convert.ToHexString(data, at, 16).ToLowerInvariant();

    private static int Int(byte[] data, ref int at)
    {
        if (at < 0 || at + 4 > data.Length) throw new InvalidOperationException("past the end");

        int value = BitConverter.ToInt32(data, at);
        at += 4;
        return value;
    }
}
