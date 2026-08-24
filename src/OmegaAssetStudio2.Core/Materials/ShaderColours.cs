using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>
/// The colour a material was compiled with, for materials that carry none of
/// their own.
/// </summary>
/// <remarks>
/// Cooking strips the node graph out of a material. A material whose colour was
/// a constant in that graph is left with nothing: no texture, no parameter, no
/// colour on its vertices. One costume's string of lights is four such
/// materials, and they came out as bare grey geometry.
/// <para>
/// The colour survives in the compiled shader, which the game keeps in a
/// separate cache and files under the same identity the material writes in its
/// own resource. A constant the shader defines before it runs is written as an
/// instruction naming a register and four numbers, and the ones a material does
/// not share with every other material are its own.
/// </para>
/// </remarks>
public static class ShaderColours
{
    /// <summary>What the cache is called, beside the rest of the game.</summary>
    private const string CacheName = "RefShaderCache-PC-D3D-SM3.upk";

    /// <summary>
    /// Reading eighty megabytes of shaders takes a while and never changes, so
    /// each game folder is read once.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, MaterialColour>> Read =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The values each material's shaders are actually given, by name.
    /// </summary>
    /// <remarks>
    /// A material states many values and its shader is handed only the ones it
    /// uses. One costume states a highlight colour of 0.012, 0.302, 1 - a strong
    /// blue - and its shader never asks for it; painting with it anyway turned a
    /// black costume blue all over.
    /// </remarks>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, HashSet<string>>> Used =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Lock = new();

    /// <summary>
    /// The colour this material was compiled with, if the cache has one for it.
    /// </summary>
    public static bool TryFind(
        string cookedPath, Package package, int materialExport, out MaterialColour colour)
    {
        colour = default;

        string? identity = Identity(package, materialExport);
        if (identity is null) return false;

        IReadOnlyDictionary<string, MaterialColour> known = ForFolder(cookedPath);

        return known.TryGetValue(identity, out colour);
    }

    /// <summary>
    /// Whether this material's shaders are given the named value at all.
    /// </summary>
    /// <remarks>
    /// Answers yes when nothing is known about the material, so a cache that
    /// cannot be read leaves every material exactly as it was.
    /// </remarks>
    public static bool Uses(string cookedPath, Package package, int materialExport, string parameter)
    {
        string? identity = Identity(package, materialExport);
        if (identity is null) return true;

        ForFolder(cookedPath);

        IReadOnlyDictionary<string, HashSet<string>> known;
        lock (Lock)
        {
            if (!Used.TryGetValue(cookedPath, out known!)) return true;
        }

        if (!known.TryGetValue(identity, out HashSet<string>? given)) return true;

        return given.Contains(parameter);
    }

    /// <summary>
    /// Every value this material's compiled form hands its shaders, or nothing
    /// when the material has no compiled form the cache knows.
    /// </summary>
    /// <remarks>
    /// A material's property chain answers for every switch and number the
    /// vocabulary has, whether or not this material's shader was built with
    /// them. Its compiled form lists only the parameters the shader was
    /// actually built to read, so a term whose parameters are missing from that
    /// list is a term this material does not run.
    /// <para>
    /// One costume's chain says it lights itself with a fill light of strength
    /// 30 - the same 30 that 1,292 of 1,413 materials carry, so not a dial
    /// anyone turned - and its compiled form is handed no fill light at all. It
    /// is a black costume and the fill was painting it grey-blue from head to
    /// foot.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string>? Given(string cookedPath, Package package, int materialExport)
    {
        string? identity = Identity(package, materialExport);
        if (identity is null) return null;

        ForFolder(cookedPath);

        IReadOnlyDictionary<string, HashSet<string>> known;
        lock (Lock)
        {
            if (!Used.TryGetValue(cookedPath, out known!)) return null;
        }

        return known.TryGetValue(identity, out HashSet<string>? given) ? given : null;
    }

    /// <summary>Whether a folder's cache has been read yet.</summary>
    public static bool AlreadyRead(string cookedPath)
    {
        lock (Lock) return Read.ContainsKey(cookedPath);
    }

    private static IReadOnlyDictionary<string, MaterialColour> ForFolder(string cookedPath)
    {
        lock (Lock)
        {
            if (Read.TryGetValue(cookedPath, out IReadOnlyDictionary<string, MaterialColour>? already))
                return already;
        }

        var given = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, MaterialColour> found = Walk(cookedPath, given);

        lock (Lock)
        {
            Read[cookedPath] = found;
            Used[cookedPath] = given;
        }

        return found;
    }

    /// <summary>The identity a material writes in its own compiled resource.</summary>
    private static string? Identity(Package package, int exportIndex)
    {
        PropertyBag? bag = package.TryReadProperties(exportIndex);
        if (bag is null) return null;

        byte[] data = package.GetExportData(exportIndex).ToArray();
        int at = bag.PayloadOffset;

        // An instance writes which quality levels it compiled before its
        // resource; a base material writes the resource straight away.
        if (bag.GetBool("bHasStaticPermutationResource")) at += 4;

        try
        {
            int errors = Int(data, ref at);
            for (int e = 0; e < errors; e++)
            {
                int count = Int(data, ref at);
                at += count >= 0 ? count : -count * 2;
            }

            int dependencies = Int(data, ref at);
            at += dependencies * 8;
            at += 4;                                     // the longest chain

            if (at + 16 > data.Length) return null;

            return Show(data, at);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every material in the cache, and the colour its shaders define that no
    /// other material's do.
    /// </summary>
    private static IReadOnlyDictionary<string, MaterialColour> Walk(
        string cookedPath, Dictionary<string, HashSet<string>> given)
    {
        var colours = new Dictionary<string, MaterialColour>(StringComparer.OrdinalIgnoreCase);

        string path = Path.Combine(cookedPath, CacheName);
        if (!File.Exists(path)) return colours;

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
                       cache.Exports[i].SerialOffset, colours, given);

                break;
            }
        }
        catch (Exception)
        {
            // A cache that will not read leaves every material as it was.
        }

        return colours;
    }

    private static void Gather(
        Package cache, byte[] data, int at, int fileStart,
        Dictionary<string, MaterialColour> colours, Dictionary<string, HashSet<string>> given)
    {
        at += 4 + 1;                                     // how preferred, and for which platform

        int kinds = Int(data, ref at);
        if (kinds != 0) return;                          // a cache keeping compressed code is not read

        int shaders = Int(data, ref at);
        if (shaders < 0 || shaders > 4_000_000) return;

        // Where each shader's code sits, by the identity a map refers to it by.
        var code = new Dictionary<string, (bool Pixel, int At, int Length)>(shaders, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < shaders; i++)
        {
            string kind = Name(cache, data, ref at);

            string id = Show(data, at);
            at += 16;
            at += 20;                                    // the hash of what it was built from

            int resume = Int(data, ref at) - fileStart;

            int table = Int(data, ref at);
            at += table * 2;
            at += 2;                                     // which platform, and which stage

            int length = Int(data, ref at);
            int began = at;

            if (length < 0 || began + length > data.Length) return;

            code[id] = (kind.Contains("Pixel", StringComparison.OrdinalIgnoreCase), began, length);

            if (resume <= at || resume > data.Length) return;

            at = resume;
        }

        int maps = Int(data, ref at);
        if (maps < 0 || maps > 1_000_000) return;

        var perMaterial = new List<(string Identity, List<(int At, int Length)> Shaders)>();

        for (int i = 0; i < maps; i++)
        {
            if (!GatherMap(cache, data, ref at, fileStart, code, perMaterial, given)) break;
        }

        foreach ((string identity, List<(int At, int Length)> compiled) in perMaterial)
        {
            foreach ((int began, int length) in compiled)
            {
                if (!TryTrace(data, began, length, out MaterialColour made)) continue;

                colours[identity] = made;
                break;
            }
        }
    }

    private static bool GatherMap(
        Package cache, byte[] data, ref int at, int fileStart,
        Dictionary<string, (bool Pixel, int At, int Length)> code,
        List<(string Identity, List<(int At, int Length)> Shaders)> perMaterial,
        Dictionary<string, HashSet<string>> given)
    {
        try
        {
            SkipChoices(data, ref at);

            at += 4 + 4;                                 // two version numbers
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
            for (int m = 0; m < meshes; m++)
            {
                int inside = Int(data, ref at);
                for (int i = 0; i < inside; i++)
                {
                    Name(cache, data, ref at);
                    used.Add(Show(data, at));
                    at += 16;
                    Name(cache, data, ref at);
                }

                Name(cache, data, ref at);               // which vertex layout
            }

            string identity = Show(data, at);
            at += 16;

            Text(data, ref at);                          // what it was compiled as

            // The simplest shaders first: one lit by nothing has the fewest
            // instructions between the colour and the output.
            var shaders = new List<(int At, int Length)>();

            foreach (string id in used)
            {
                if (!code.TryGetValue(id, out (bool Pixel, int At, int Length) found)) continue;
                if (!found.Pixel) continue;

                shaders.Add((found.At, found.Length));
            }

            shaders.Sort((one, other) => one.Length.CompareTo(other.Length));

            if (shaders.Count > 0) perMaterial.Add((identity, shaders));

            // Which of the values this material states its shaders are handed.
            //
            // The material lists its values in order; a shader is given each as
            // a register, and its own table names those registers. A value
            // whose register is not in the table is one the shader never reads,
            // whatever the material says about it.
            try
            {
                List<(string Name, string Register)> stated = Stated(cache, data, ref at);

                if (stated.Count > 0 && shaders.Count > 0)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Registers(data, shaders[^1].At, shaders[^1].Length, names);

                    var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach ((string parameter, string register) in stated)
                    {
                        if (parameter.Length == 0) continue;
                        if (!names.Contains(register)) continue;

                        reads.Add(parameter);
                    }

                    // Several compiled permutations answer to one identity -
                    // 183 of them under the commonest, and 466 identities have
                    // more than one - because a plain instance is filed under
                    // the resource its base wrote. Which of them a given
                    // material uses is not recoverable from the identity, so a
                    // value any of them reads counts as read.
                    //
                    // That is the conservative side of the question: a term is
                    // switched off only where no permutation of the material
                    // binds it at all. Taking whichever came last instead left
                    // the answer depending on the order of the file.
                    if (given.TryGetValue(identity, out HashSet<string>? already))
                    {
                        already.UnionWith(reads);
                    }
                    else
                    {
                        given[identity] = reads;
                    }
                }
            }
            catch (Exception)
            {
                // A material whose values will not read is left unrecorded, and
                // every one of them then counts as used.
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

    /// <summary>
    /// The colour a compiled pixel shader multiplies into what it writes out.
    /// </summary>
    /// <remarks>
    /// Which numbers in a defined constant are the colour cannot be told from
    /// the numbers. Three of this game's light materials define the very same
    /// constant - 1, 0.3, 0, 0 - and come out white, yellow and red, because
    /// each takes different components of it: one reads y,z,z, another y,y,z,
    /// and the third never multiplies by it at all.
    /// <para>
    /// So the program is followed instead. Every register component is tracked
    /// as either a number the shader defined or something worked out while it
    /// runs; where a multiply has a known number on one side and an unknown on
    /// the other, that number is what is being tinted by, and it is carried
    /// forward. Whatever is carried into the three channels of the colour it
    /// writes out is the colour.
    /// </para>
    /// </remarks>
    private static bool TryTrace(byte[] data, int at, int length, out MaterialColour colour)
    {
        colour = default;

        // What each register component is: a number the shader defined, and the
        // number being multiplied in on the way to the output.
        var known = new Dictionary<int, float>();
        var tint = new Dictionary<int, float>();

        int end = at + length;
        int position = at + 4;

        while (position + 4 <= end)
        {
            uint word = BitConverter.ToUInt32(data, position);
            if (word == 0x0000FFFF) break;

            int opcode = (int)(word & 0xFFFF);

            if (opcode == 0xFFFE)
            {
                position += 4 + ((int)((word >> 16) & 0x7FFF) * 4);
                continue;
            }

            int words = (int)((word >> 24) & 0x0F);
            if (words <= 0) { position += 4; continue; }

            if (position + 4 + (words * 4) > end) break;

            uint destination = BitConverter.ToUInt32(data, position + 4);

            if (opcode == 0x51 && words >= 5)
            {
                for (int i = 0; i < 4; i++)
                    known[Slot(destination, i)] = BitConverter.ToSingle(data, position + 8 + (i * 4));

                position += 4 + (words * 4);
                continue;
            }

            int mask = (int)((destination >> 16) & 0xF);
            if (mask == 0) mask = 0xF;

            var sources = new uint[words - 1];
            for (int i = 0; i < words - 1; i++)
                sources[i] = BitConverter.ToUInt32(data, position + 8 + (i * 4));

            // The colour it writes out.
            if (Kind(destination) == ColourOut)
            {
                var channels = new float[3];
                int carried = 0;

                for (int channel = 0; channel < 3; channel++)
                {
                    if ((mask & (1 << channel)) == 0) continue;

                    foreach (uint source in sources)
                    {
                        if (!tint.TryGetValue(Slot(source, Component(source, channel)), out float value)) continue;

                        channels[channel] = value;
                        carried++;
                        break;
                    }
                }

                if (carried == 0) return false;

                colour = new MaterialColour(channels[0], channels[1], channels[2], 1f);
                return true;
            }

            for (int channel = 0; channel < 4; channel++)
            {
                if ((mask & (1 << channel)) == 0) continue;

                int slot = Slot(destination, channel);

                known.Remove(slot);
                tint.Remove(slot);

                if (opcode == Move && sources.Length >= 1)
                {
                    int from = Slot(sources[0], Component(sources[0], channel));

                    if (known.TryGetValue(from, out float copied)) known[slot] = copied;
                    if (tint.TryGetValue(from, out float carried)) tint[slot] = carried;

                    continue;
                }

                // A multiply, on its own or followed by an add. One side a
                // number the shader defined and the other not is a tint.
                if (opcode is not (Multiply or MultiplyAdd) || sources.Length < 2) continue;

                int left = Slot(sources[0], Component(sources[0], channel));
                int right = Slot(sources[1], Component(sources[1], channel));

                bool leftKnown = known.TryGetValue(left, out float leftValue);
                bool rightKnown = known.TryGetValue(right, out float rightValue);

                if (leftKnown && !rightKnown) tint[slot] = leftValue;
                else if (rightKnown && !leftKnown) tint[slot] = rightValue;
                else if (tint.TryGetValue(left, out float already)) tint[slot] = already;
                else if (tint.TryGetValue(right, out float other)) tint[slot] = other;
            }

            position += 4 + (words * 4);
        }

        return false;
    }

    private const int Move = 0x01;
    private const int Multiply = 0x05;
    private const int MultiplyAdd = 0x04;

    /// <summary>The register kind the shader writes its colour to.</summary>
    private const int ColourOut = 8;

    /// <summary>Which kind of register a token names.</summary>
    private static int Kind(uint token) =>
        (int)(((token >> 28) & 0x7) | ((token >> 8) & 0x18));

    /// <summary>One component of one register, as a single number to key by.</summary>
    private static int Slot(uint token, int component) =>
        (Kind(token) * 4096) + ((int)(token & 0x7FF) * 4) + component;

    /// <summary>
    /// Which component of a source a given channel reads, after its swizzle.
    /// This is the whole of the difference between the yellow bulb and the red
    /// one.
    /// </summary>
    private static int Component(uint token, int channel) =>
        (int)((token >> (16 + (channel * 2))) & 3);

    /// <summary>
    /// The colours a material states, in the order it states them.
    /// </summary>
    private static List<(string Name, string Register)> Stated(Package cache, byte[] data, ref int at)
    {
        var stated = new List<(string Name, string Register)>();

        SkipChoices(data, ref at);                       // the choices it was compiled with

        int colours = Int(data, ref at);
        if (colours < 0 || colours > 4096) return stated;

        for (int i = 0; i < colours; i++)
            stated.Add((Named(cache, data, ref at, 0), "UniformPixelVector_" + i));

        // The numbers follow the colours, and four of them share a register.
        int numbers = Int(data, ref at);
        if (numbers < 0 || numbers > 4096) return stated;

        for (int i = 0; i < numbers; i++)
            stated.Add((Named(cache, data, ref at, 0), "UniformPixelScalars_" + (i / 4)));

        return stated;
    }

    /// <summary>
    /// One stated value, keeping the name where it has one. Its parts are read
    /// through so the next value begins in the right place.
    /// </summary>
    private static string Named(Package cache, byte[] data, ref int at, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("values nested too deeply");

        string kind = Name(cache, data, ref at)
            .Replace("FMaterialUniformExpression", string.Empty, StringComparison.OrdinalIgnoreCase);

        switch (kind.ToLowerInvariant())
        {
            case "constant": at += (4 * 4) + 1; return string.Empty;

            case "vectorparameter":
                {
                    string named = Name(cache, data, ref at);
                    at += 4 * 4;
                    return named;
                }

            case "scalarparameter":
                {
                    string named = Name(cache, data, ref at);
                    at += 4;
                    return named;
                }

            case "textureparameter": { Name(cache, data, ref at); at += 4; return string.Empty; }

            case "texture":
            case "flipbooktextureparameter": at += 4; return string.Empty;

            case "time":
            case "realtime": return string.Empty;

            case "appendvector":
                {
                    string one = Named(cache, data, ref at, depth + 1);
                    string other = Named(cache, data, ref at, depth + 1);
                    at += 4;
                    return one.Length > 0 ? one : other;
                }

            case "foldedmath":
                {
                    string one = Named(cache, data, ref at, depth + 1);
                    string other = Named(cache, data, ref at, depth + 1);
                    at += 1;
                    return one.Length > 0 ? one : other;
                }

            case "min":
            case "max":
            case "fmod":
                {
                    string one = Named(cache, data, ref at, depth + 1);
                    string other = Named(cache, data, ref at, depth + 1);
                    return one.Length > 0 ? one : other;
                }

            case "clamp":
                {
                    string one = Named(cache, data, ref at, depth + 1);
                    Named(cache, data, ref at, depth + 1);
                    Named(cache, data, ref at, depth + 1);
                    return one;
                }

            case "sine":
                {
                    string one = Named(cache, data, ref at, depth + 1);
                    at += 4;
                    return one;
                }

            case "periodic":
            case "length":
            case "squareroot":
            case "floor":
            case "ceil":
            case "frac":
            case "abs":
                return Named(cache, data, ref at, depth + 1);

            default:
                throw new InvalidOperationException("a value of a kind not read here: " + kind);
        }
    }

    /// <summary>The registers a compiled shader's own table names.</summary>
    private static void Registers(byte[] data, int at, int length, HashSet<string> into)
    {
        int end = at + length;
        int position = at + 4;

        while (position + 4 <= end)
        {
            uint word = BitConverter.ToUInt32(data, position);
            if (word == 0x0000FFFF) return;

            if ((word & 0xFFFF) != 0xFFFE)
            {
                int words = (int)((word >> 24) & 0x0F);
                position += words <= 0 ? 4 : 4 + (words * 4);
                continue;
            }

            int inside = (int)((word >> 16) & 0x7FFF);
            int table = position + 4;

            if (table + 8 <= end
                && data[table] == (byte)'C' && data[table + 1] == (byte)'T'
                && data[table + 2] == (byte)'A' && data[table + 3] == (byte)'B')
            {
                int start = table + 4;

                int count = BitConverter.ToInt32(data, start + 12);
                int info = BitConverter.ToInt32(data, start + 16);

                if (count < 0 || count > 4096) return;

                for (int i = 0; i < count; i++)
                {
                    int entry = start + info + (i * 20);
                    if (entry + 20 > end) return;

                    int name = BitConverter.ToInt32(data, entry);

                    var text = new System.Text.StringBuilder();
                    for (int k = start + name; k < end && data[k] != 0; k++) text.Append((char)data[k]);

                    if (text.Length > 0) into.Add(text.ToString());
                }

                return;
            }

            position += 4 + (inside * 4);
        }
    }

    /// <summary>The constants a compiled shader defines before it runs.</summary>
    private static IEnumerable<string> Literals(byte[] data, int at, int length)
    {
        int end = at + length;
        int position = at + 4;                           // past which shader model it is

        while (position + 4 <= end)
        {
            uint word = BitConverter.ToUInt32(data, position);

            if (word == 0x0000FFFF) yield break;

            int opcode = (int)(word & 0xFFFF);

            // A comment says how long it is in a different place from an
            // instruction; read as an instruction it walks into the middle of
            // the constant table.
            if (opcode == 0xFFFE)
            {
                position += 4 + ((int)((word >> 16) & 0x7FFF) * 4);
                continue;
            }

            int words = (int)((word >> 24) & 0x0F);

            if (opcode == 0x51 && words >= 5 && position + 4 + (words * 4) <= end)
            {
                yield return
                    BitConverter.ToSingle(data, position + 8).ToString("0.###") + ", " +
                    BitConverter.ToSingle(data, position + 12).ToString("0.###") + ", " +
                    BitConverter.ToSingle(data, position + 16).ToString("0.###") + ", " +
                    BitConverter.ToSingle(data, position + 20).ToString("0.###");
            }

            if (words <= 0) { position += 4; continue; }

            position += 4 + (words * 4);
        }
    }

    /// <summary>
    /// The choices a material was compiled with, read field by field as the
    /// format defines each one.
    /// </summary>
    private static void SkipChoices(byte[] data, ref int at)
    {
        at += 16;                                        // which base it was compiled against

        int switches = Count(data, ref at);
        for (int i = 0; i < switches; i++) at += 8 + 4 + 4 + 16;

        int masks = Count(data, ref at);
        for (int i = 0; i < masks; i++) at += 8 + (4 * 4) + 4 + 16;

        // A normal's compression setting is one byte, not four.
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

    private static int Int(byte[] data, ref int at)
    {
        if (at < 0 || at + 4 > data.Length) throw new InvalidOperationException("past the end");

        int value = BitConverter.ToInt32(data, at);
        at += 4;
        return value;
    }

    private static string Show(byte[] data, int at)
    {
        var text = new System.Text.StringBuilder(32);
        for (int i = 0; i < 16; i++) text.Append(data[at + i].ToString("x2"));
        return text.ToString();
    }
}
