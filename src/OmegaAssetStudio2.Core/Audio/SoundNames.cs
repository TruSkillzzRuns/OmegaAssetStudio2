using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>How far the name catalogue has got.</summary>
public readonly record struct SoundNameProgress(int Read, int Total, int Found);

/// <summary>Names recovered for the sounds in one container.</summary>
public sealed class SoundNameIndex
{
    private readonly Dictionary<uint, string> _bySound;

    internal SoundNameIndex(Dictionary<uint, string> bySound, int events, int named)
    {
        _bySound = bySound;
        EventCount = events;
        NamedEventCount = named;
    }

    /// <summary>Events found in the container's banks.</summary>
    public int EventCount { get; }

    /// <summary>How many of those events a name was recovered for.</summary>
    public int NamedEventCount { get; }

    /// <summary>Sounds a name was recovered for.</summary>
    public int Count => _bySound.Count;

    public static SoundNameIndex Empty { get; } = new([], 0, 0);

    /// <summary>The name of a sound, or null when it could not be recovered.</summary>
    public string? Of(uint soundId) => _bySound.GetValueOrDefault(soundId);
}

/// <summary>
/// Every sound name in one install, ready to be matched against the numbers a
/// container records.
/// </summary>
/// <remarks>
/// A shipped container records numbers, never names: an event is stored as the
/// hash of its name and the name itself is discarded. The names survive in the
/// packages that fire those events — <c>play_vox_iga_ply_thor_defeatmob</c> and
/// its like sit in package name tables — so hashing those puts the two halves
/// back together.
/// <para>
/// It has to be the whole install, not the packages named after the container.
/// One character's container holds 71 sounds, all of them effects; the spoken lines
/// are in <c>SFX_InitialDownloadChunk</c> along with everyone else's, and that
/// name matches no package at all. Reading every package raises what can be
/// named there from 68% to 99%.
/// </para>
/// <para>
/// That pass takes about thirty seconds for 15,250 packages, so it is done once
/// and kept. Only names that look like sound events are stored, which is 28,424
/// of 254,772 — a megabyte rather than eight, and no loss: the same 99%.
/// </para>
/// </remarks>
public sealed class SoundNameCatalog
{
    private readonly Dictionary<uint, string> _byHash;

    private SoundNameCatalog(Dictionary<uint, string> byHash) => _byHash = byHash;

    public int Count => _byHash.Count;

    public static SoundNameCatalog Empty { get; } = new([]);

    /// <summary>The name behind a number, or null.</summary>
    public string? Of(uint hash) => _byHash.GetValueOrDefault(hash);

    /// <summary>Where a built catalogue is kept between runs.</summary>
    public static string CacheFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmegaAssetStudio2", "sound-names");

    /// <summary>
    /// Loads the catalogue for an install, building it if there is nothing kept
    /// or if the install has changed since.
    /// </summary>
    public static SoundNameCatalog LoadOrBuild(
        GameClient client,
        IProgress<SoundNameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Directory.Exists(client.CookedPath)) return Empty;

        string[] packages = Directory.GetFiles(client.CookedPath, "*.upk");
        string stamp = Stamp(packages);
        string cache = Path.Combine(CacheFolder, $"{Key(client)}.txt");

        if (Read(cache, stamp) is { } kept) return kept;

        var names = new Dictionary<uint, string>();

        for (int i = 0; i < packages.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Package upk;
            try { upk = Package.Open(packages[i]); }
            catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (int n = 0; n < upk.Names.Count; n++)
            {
                string name = upk.Names.GetName(n);
                if (!LooksLikeSound(name)) continue;

                names.TryAdd(SoundNameHash.Of(name), name);
            }

            // Reported every so often rather than per package: fifteen thousand
            // updates would cost more than the reading.
            if (i % 250 == 0 || i == packages.Length - 1)
                progress?.Report(new SoundNameProgress(i + 1, packages.Length, names.Count));
        }

        Write(cache, stamp, names.Values);

        return new SoundNameCatalog(names);
    }

    /// <summary>
    /// Whether a name is worth keeping: the events are all asked for by name,
    /// and every one of those names says what it does to the sound.
    /// </summary>
    private static bool LooksLikeSound(string name) =>
        name.StartsWith("play_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("stop_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the install looked like when the catalogue was built, so a patched
    /// game is noticed and read again.
    /// </summary>
    private static string Stamp(string[] packages)
    {
        long newest = 0;

        foreach (string path in packages)
        {
            long written = File.GetLastWriteTimeUtc(path).Ticks;
            if (written > newest) newest = written;
        }

        return $"{packages.Length}:{newest}";
    }

    private static string Key(GameClient client)
    {
        string full = Path.GetFullPath(client.CookedPath).ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(full)));
    }

    private static SoundNameCatalog? Read(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var reader = new StreamReader(path);

            if (reader.ReadLine() != stamp) return null;

            var names = new Dictionary<uint, string>();

            while (reader.ReadLine() is { } line)
                if (line.Length > 0) names.TryAdd(SoundNameHash.Of(line), line);

            return new SoundNameCatalog(names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(string path, string stamp, IEnumerable<string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var writer = new StreamWriter(path, append: false);

            writer.WriteLine(stamp);
            foreach (string name in names) writer.WriteLine(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keeping it is an optimisation. Failing to is not worth an error.
        }
    }
}

/// <summary>Puts recovered names to the sounds in a container.</summary>
public static class SoundNames
{
    /// <summary>
    /// Names what it can in one container, using an install's catalogue.
    /// </summary>
    /// <remarks>
    /// An event names a sound only indirectly: it fires actions, which act on
    /// sounds or on containers of them. A line recorded in several takes is one
    /// event over a container of takes, so the takes share the event's name and
    /// are told apart by number.
    /// </remarks>
    public static SoundNameIndex Recover(
        AudioPackage package,
        SoundNameCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.Count == 0) return SoundNameIndex.Empty;

        var bySound = new Dictionary<uint, string>();
        int events = 0, named = 0;

        using var stream = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        foreach (AudioEntry bank in package.Banks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bank.Size <= 0) continue;

            byte[] bytes = new byte[bank.Size];
            stream.Seek(bank.Offset, SeekOrigin.Begin);

            if (stream.ReadAtLeast(bytes, bank.Size, throwOnEndOfStream: false) < bank.Size) continue;

            SoundBank read = SoundBank.Read(bytes);

            foreach (uint id in read.Events)
            {
                events++;

                string? name = catalog.Of(id);
                if (name is null) continue;

                List<uint> sounds = read.SoundsOf(id).ToList();
                if (sounds.Count == 0) continue;

                named++;

                for (int i = 0; i < sounds.Count; i++)
                {
                    string label = sounds.Count > 1 ? $"{name} ({i + 1})" : name;
                    bySound.TryAdd(sounds[i], label);
                }
            }
        }

        return new SoundNameIndex(bySound, events, named);
    }
}
