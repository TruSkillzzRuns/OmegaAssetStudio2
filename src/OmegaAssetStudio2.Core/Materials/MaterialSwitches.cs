using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;

namespace OmegaAssetStudio2.Core.Materials;

/// <summary>One on-or-off choice a material instance makes.</summary>
public sealed record StaticSwitch
{
    public required string Name { get; init; }

    /// <summary>What the instance sets it to.</summary>
    public required bool Value { get; init; }

    /// <summary>
    /// Whether the instance is speaking for itself. An instance lists every
    /// switch its base declares, so one it has not touched appears here too,
    /// carrying the inherited value and saying so.
    /// </summary>
    public required bool Overrides { get; init; }

    public override string ToString() =>
        $"{Name} = {Value}{(Overrides ? "" : " (inherited)")}";
}

/// <summary>What an instance's compiled resource says about how it blends.</summary>
public sealed record BlendOverride
{
    /// <summary>Whether the instance replaces the base material's blend mode.</summary>
    public required bool Overrides { get; init; }

    /// <summary>Whether the replacement cuts holes rather than being solid.</summary>
    public required bool Masked { get; init; }

    /// <summary>The mode it names, as the engine numbers them.</summary>
    public required int Mode { get; init; }

    public static BlendOverride None { get; } = new() { Overrides = false, Masked = false, Mode = 0 };
}

/// <summary>
/// Reads the on-or-off choices a material instance makes: whether it uses a
/// normal map, whether it reflects, whether it has a rim light.
/// </summary>
/// <remarks>
/// These are not tagged properties and so are not in the property bag. A
/// material instance that compiles its own shader writes, after its properties,
/// a compiled resource for each quality level and then the set of choices that
/// resource was compiled with. The layout is the one this game's own package
/// format defines, taken from the reader that has been parsing these files
/// correctly for years rather than worked out afresh here.
/// <para>
/// They matter because they decide whole terms of the shading. The v2 base
/// material a large part of the roster inherits from has <c>usereflection</c>
/// off by default, so a viewport that reflects whenever a material happens to
/// name a panorama is reflecting on surfaces the game leaves matte.
/// </para>
/// </remarks>
public static class MaterialStaticParameters
{
    /// <summary>
    /// The choices this instance was compiled with, or nothing when it has no
    /// compiled resource of its own - in which case it inherits every one of
    /// them from its parent.
    /// </summary>
    public static IReadOnlyList<StaticSwitch> Read(Package package, int exportIndex) =>
        ReadAll(package, exportIndex).Switches;

    /// <summary>
    /// How this instance blends, when it says. A base material states its blend
    /// mode as a plain property, but an instance can replace it, and that
    /// replacement is written into the compiled resource rather than alongside
    /// the properties - which is why a costume whose base is solid can still
    /// have hair cut out of it.
    /// </summary>
    public static BlendOverride ReadBlend(Package package, int exportIndex) =>
        ReadAll(package, exportIndex).Blend;

    /// <summary>Everything the compiled resource carries, read in one pass.</summary>
    public static (IReadOnlyList<StaticSwitch> Switches, BlendOverride Blend) ReadAll(
        Package package, int exportIndex)
    {
        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return ([], BlendOverride.None);

        if (!properties.GetBool("bHasStaticPermutationResource")) return ([], BlendOverride.None);

        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
        int at = properties.PayloadOffset;

        try
        {
            if (!TryReadInt(data, ref at, out int qualityMask)) return ([], BlendOverride.None);

            // One compiled resource and one set of choices per quality level
            // that was compiled. Both levels are compiled from the same choices,
            // so the first that reads cleanly is the answer.
            for (int quality = 0; quality < 2; quality++)
            {
                if ((qualityMask & (1 << quality)) == 0) continue;

                if (!TryReadResource(data, ref at, out BlendOverride blend)) return ([], BlendOverride.None);

                IReadOnlyList<StaticSwitch>? switches = TryReadSet(package, data, ref at);

                return (switches ?? [], blend);
            }
        }
        catch (Exception)
        {
            // A layout that does not read is reported as "nothing said", which
            // leaves the caller on the base material's own defaults. That is the
            // same answer it had before this could be read at all.
            return ([], BlendOverride.None);
        }

        return ([], BlendOverride.None);
    }

    /// <summary>
    /// Walks past one compiled resource without interpreting it. Everything in
    /// it describes the compiled shader, not the material's appearance; only its
    /// length matters here.
    /// </summary>
    private static bool TryReadResource(ReadOnlySpan<byte> data, ref int at, out BlendOverride blend)
    {
        blend = BlendOverride.None;

        // Compile errors, each a string.
        if (!TryReadInt(data, ref at, out int errors)) return false;
        for (int i = 0; i < errors; i++)
        {
            if (!TrySkipString(data, ref at)) return false;
        }

        // How long each texture's dependency chain is: an object and a number.
        if (!TryReadInt(data, ref at, out int dependencies)) return false;
        if (!Advance(data, ref at, dependencies * 8)) return false;

        // The longest of those, then the resource's identity.
        if (!Advance(data, ref at, 4 + 16 + 4)) return false;

        // The textures its uniform expressions read.
        if (!TryReadInt(data, ref at, out int textures)) return false;
        if (!Advance(data, ref at, textures * 4)) return false;

        // Five flags, each a whole number, and the transforms it uses.
        if (!Advance(data, ref at, (5 * 4) + 4)) return false;

        // Texture lookups: four numbers each.
        if (!TryReadInt(data, ref at, out int lookups)) return false;
        if (!Advance(data, ref at, lookups * 16)) return false;

        // A field the engine no longer uses but still writes.
        if (!Advance(data, ref at, 4)) return false;

        // Then the three that say whether this resource replaces how the
        // material blends, and with what.
        if (!TryReadInt(data, ref at, out int mode)) return false;
        if (!TryReadInt(data, ref at, out int overrides)) return false;
        if (!TryReadInt(data, ref at, out int masked)) return false;

        blend = new BlendOverride
        {
            Overrides = overrides != 0,
            Masked = masked != 0,
            Mode = mode,
        };

        return true;
    }

    /// <summary>
    /// One set of choices: the base it was compiled against, then the switches,
    /// then three further kinds this game's characters do not use but which are
    /// still written and so still have to be walked past.
    /// </summary>
    private static IReadOnlyList<StaticSwitch>? TryReadSet(
        Package package, ReadOnlySpan<byte> data, ref int at)
    {
        if (!Advance(data, ref at, 16)) return null;                 // which base
        if (!TryReadInt(data, ref at, out int count)) return null;

        if (count < 0 || count > 4096) return null;

        var switches = new List<StaticSwitch>(count);

        for (int i = 0; i < count; i++)
        {
            if (!TryReadName(package, data, ref at, out string name)) return null;
            if (!TryReadInt(data, ref at, out int value)) return null;
            if (!TryReadInt(data, ref at, out int overrides)) return null;
            if (!Advance(data, ref at, 16)) return null;

            switches.Add(new StaticSwitch
            {
                Name = name,
                Value = value != 0,
                Overrides = overrides != 0,
            });
        }

        return switches;
    }

    private static bool TryReadName(
        Package package, ReadOnlySpan<byte> data, ref int at, out string name)
    {
        name = string.Empty;

        if (!TryReadInt(data, ref at, out int index)) return false;
        if (!TryReadInt(data, ref at, out int number)) return false;

        if ((uint)index >= (uint)package.Names.Count) return false;

        name = package.Names.Resolve(index, number);
        return true;
    }

    private static bool TrySkipString(ReadOnlySpan<byte> data, ref int at)
    {
        if (!TryReadInt(data, ref at, out int count)) return false;

        // A negative count means the text is stored two bytes to the character.
        return count >= 0
            ? Advance(data, ref at, count)
            : Advance(data, ref at, -count * 2);
    }

    private static bool TryReadInt(ReadOnlySpan<byte> data, ref int at, out int value)
    {
        value = 0;
        if (at < 0 || at + 4 > data.Length) return false;

        value = BitConverter.ToInt32(data.Slice(at, 4));
        at += 4;
        return true;
    }

    private static bool Advance(ReadOnlySpan<byte> data, ref int at, int by)
    {
        if (by < 0 || at + by > data.Length) return false;
        at += by;
        return true;
    }
}
