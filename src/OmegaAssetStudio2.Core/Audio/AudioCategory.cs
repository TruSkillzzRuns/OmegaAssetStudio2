using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>What a sound container covers.</summary>
public enum AudioCategory
{
    Hero,
    TeamUp,
    Pet,
    Zone,
    Shared,
    Other,
}

/// <summary>
/// Sorts sound containers into the groups a person would look for them in.
/// </summary>
/// <remarks>
/// A container's file name gives only its subject — a character, a download
/// chunk, "Knockables" — with nothing saying what kind of thing that is. Characters are
/// settled by asking the roster, which is built from the model packages actually
/// present, so it is the game's own answer rather than a list kept here that
/// would rot as heroes are added. Measured against the Steam install: of 89
/// subjects, 63 are heroes by that test.
/// <para>
/// The remainder are named by convention and matched on it: everything holding
/// "DownloadChunk" or "DLChunk" is a chapter or hub, and a short list of known
/// names covers the shared effects. Anything unrecognised is grouped as Other
/// rather than guessed at, so a name this does not know still appears.
/// </para>
/// </remarks>
public static class AudioCategories
{
    /// <summary>Containers holding sounds used all over the game.</summary>
    private static readonly string[] SharedNames =
        ["SFX", "Shared", "SharedEnviro", "Knockables"];

    /// <summary>
    /// Every character name in a client, lowercased, for matching subjects.
    /// </summary>
    public static IReadOnlyDictionary<string, AudioCategory> NamesIn(GameClient client)
    {
        var names = new Dictionary<string, AudioCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (RosterEntry entry in CharacterRoster.Build(client, RosterCategory.TeamUp))
            names[entry.Character] = AudioCategory.TeamUp;

        // Heroes last: a name that is both is a hero, which is how the game
        // presents it and where a person would look first.
        foreach (RosterEntry entry in CharacterRoster.Build(client, RosterCategory.Hero))
            names[entry.Character] = AudioCategory.Hero;

        return names;
    }

    /// <summary>Which group a container's subject belongs to.</summary>
    public static AudioCategory Of(string subject, IReadOnlyDictionary<string, AudioCategory>? characters)
    {
        if (string.IsNullOrWhiteSpace(subject)) return AudioCategory.Other;

        if (characters is not null && characters.TryGetValue(subject, out AudioCategory known))
            return known;

        if (subject.Contains("DownloadChunk", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("DLChunk", StringComparison.OrdinalIgnoreCase))
        {
            return AudioCategory.Zone;
        }

        if (subject.Equals("Teamups", StringComparison.OrdinalIgnoreCase)) return AudioCategory.TeamUp;
        if (subject.Equals("Pets", StringComparison.OrdinalIgnoreCase)) return AudioCategory.Pet;

        foreach (string shared in SharedNames)
            if (subject.Equals(shared, StringComparison.OrdinalIgnoreCase)) return AudioCategory.Shared;

        return AudioCategory.Other;
    }

    /// <summary>The heading a group is shown under.</summary>
    public static string NameOf(AudioCategory category) => category switch
    {
        AudioCategory.Hero => "Heroes",
        AudioCategory.TeamUp => "Team-Ups",
        AudioCategory.Pet => "Pets",
        AudioCategory.Zone => "Zones and chapters",
        AudioCategory.Shared => "Shared sounds",
        _ => "Everything else",
    };

    /// <summary>The order the groups are shown in.</summary>
    public static int OrderOf(AudioCategory category) => category switch
    {
        AudioCategory.Hero => 0,
        AudioCategory.TeamUp => 1,
        AudioCategory.Pet => 2,
        AudioCategory.Zone => 3,
        AudioCategory.Shared => 4,
        _ => 5,
    };
}
