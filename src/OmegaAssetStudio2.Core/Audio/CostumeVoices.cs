using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>One character's voice set, as worn with a costume.</summary>
public sealed record CostumeVoice
{
    public required string Hero { get; init; }

    /// <summary>The costume's name, or "Default" for the plain one.</summary>
    public required string Costume { get; init; }

    /// <summary>The package that asks for this set's lines.</summary>
    public required string PackagePath { get; init; }

    public override string ToString() => $"{Hero} — {Costume}";
}

/// <summary>One sound, and the container it lives in.</summary>
public sealed record PlacedSound
{
    public required AudioEntry Entry { get; init; }
    public required string ContainerPath { get; init; }
    public required string Name { get; init; }

    public string ContainerName => Path.GetFileNameWithoutExtension(ContainerPath);
}

/// <summary>
/// Finds the lines a character speaks while wearing a given costume.
/// </summary>
/// <remarks>
/// Costumes are not merely a change of appearance: several have a voice of
/// their own, recorded separately. One character is the clear case — four
/// voice packages, each asking for lines named after the costume rather than
/// the character, between 132 and 153 lines each, and
/// none of them share a name.
/// <para>
/// Those lines are not in that character's container, which holds 71 sounds and all
/// of them effects. They are in <c>SFX_InitialDownloadChunk</c> together with
/// every other launch hero's, so finding a costume's sounds means searching the
/// containers rather than assuming the one named after the character.
/// </para>
/// <para>
/// Not every character has one of these packages. Where there is none, the
/// lines sit in the character's own container under a single voice.
/// </para>
/// </remarks>
public static class CostumeVoices
{
    private const string Prefix = "UC__MarvelPlayerAudio_";
    private const string Suffix = "_SF";

    /// <summary>The voice sets a character has, one per costume that changes it.</summary>
    public static IReadOnlyList<CostumeVoice> For(GameClient client, string hero)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (string.IsNullOrWhiteSpace(hero) || !Directory.Exists(client.CookedPath)) return [];

        var found = new List<CostumeVoice>();

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, $"{Prefix}{hero}_*.upk"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);

            if (stem.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^Suffix.Length];

            string tail = stem[(Prefix.Length + hero.Length)..].TrimStart('_');
            if (tail.Length == 0) continue;

            found.Add(new CostumeVoice { Hero = hero, Costume = tail, PackagePath = path });
        }

        // Default first, then the rest by name, which is the order a person
        // looks for them in.
        return found
            .OrderByDescending(c => c.Costume.Equals("Default", StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Costume, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every sound one voice set plays, wherever it is kept.
    /// </summary>
    /// <param name="language">
    /// Only containers of this language are searched, since each language keeps
    /// its own recording of the same line. Empty searches all of them.
    /// </param>
    public static IReadOnlyList<PlacedSound> Sounds(
        GameClient client,
        CostumeVoice costume,
        string language,
        SoundNameCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(costume);
        ArgumentNullException.ThrowIfNull(catalog);

        // What this costume asks for, by name.
        var wanted = new Dictionary<uint, string>();

        try
        {
            Package upk = Package.Open(costume.PackagePath);

            for (int i = 0; i < upk.Names.Count; i++)
            {
                string name = upk.Names.GetName(i);

                if (!name.StartsWith("play_", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("stop_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                wanted.TryAdd(SoundNameHash.Of(name), name);
            }
        }
        catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (wanted.Count == 0) return [];

        var placed = new List<PlacedSound>();
        var seen = new HashSet<(string, uint)>();

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.pck"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (language.Length > 0 && !MatchesLanguage(path, language)) continue;

            AudioPackage container;
            try { container = AudioPackage.Open(path); }
            catch (Exception ex) when (ex is InvalidPackageException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Built by hand rather than by ToDictionary: a container can record
            // the same sound twice — streamed and again inside a bank — and the
            // first is the one an event means.
            var byId = new Dictionary<uint, AudioEntry>();
            foreach (AudioEntry sound in container.Sounds) byId.TryAdd(sound.Id, sound);

            if (byId.Count == 0) continue;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            foreach (AudioEntry bank in container.Banks)
            {
                if (bank.Size <= 0) continue;

                byte[] bytes = new byte[bank.Size];
                stream.Seek(bank.Offset, SeekOrigin.Begin);

                if (stream.ReadAtLeast(bytes, bank.Size, throwOnEndOfStream: false) < bank.Size) continue;

                SoundBank read = SoundBank.Read(bytes);

                foreach (uint id in read.Events)
                {
                    if (!wanted.TryGetValue(id, out string? name)) continue;

                    List<uint> sounds = read.SoundsOf(id).ToList();

                    for (int i = 0; i < sounds.Count; i++)
                    {
                        if (!byId.TryGetValue(sounds[i], out AudioEntry? entry)) continue;
                        if (!seen.Add((path, entry.Id))) continue;

                        placed.Add(new PlacedSound
                        {
                            Entry = entry,
                            ContainerPath = path,
                            Name = sounds.Count > 1 ? $"{name} ({i + 1})" : name,
                        });
                    }
                }
            }
        }

        return placed;
    }

    /// <summary>
    /// Whether a container carries a language, by the code on its file name.
    /// </summary>
    private static bool MatchesLanguage(string path, string language)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        int mark = stem.LastIndexOf('_');

        if (mark < 0) return false;

        return stem[(mark + 1)..].Equals(language, StringComparison.OrdinalIgnoreCase);
    }
}
