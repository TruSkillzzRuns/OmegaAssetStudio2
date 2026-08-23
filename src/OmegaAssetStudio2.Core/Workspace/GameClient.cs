using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// One installed game client the tools can read from and write to.
/// </summary>
/// <remarks>
/// Every tool works against a client rather than a single global folder, because
/// more than one client is installed at a time and content is routinely compared
/// or moved between them. The display name is user-supplied: package headers
/// cannot tell two clients apart when they share a format.
/// </remarks>
// Properties are init-only with defaults rather than `required`: the XAML type
// generator needs to be able to activate any type a control exposes, and it
// cannot satisfy required members. Construct these through GameClientLocator,
// which is the only path that produces a usable instance.
public sealed record GameClient
{
    /// <summary>Stable identity, so settings survive a rename.</summary>
    public Guid Id { get; init; }

    /// <summary>What the user calls this install. Theirs to choose.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Install root — the folder containing the engine directory.</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Folder holding the cooked packages.</summary>
    public string CookedPath { get; init; } = string.Empty;

    /// <summary>Package format read from a sample package, or Unknown.</summary>
    public PackageFormat Format { get; init; } = PackageFormat.Unknown;

    /// <summary>
    /// The build this install is, as its own executable states it - "1.53.0.203"
    /// and the like - or empty where no executable was found to ask.
    /// </summary>
    /// <remarks>
    /// Every build ships the same cooked folder name and the same package
    /// format, so nothing inside the cooked data separates one from another.
    /// The executable's own version does, and it is the only thing that does.
    /// </remarks>
    public string Build { get; init; } = string.Empty;

    /// <summary>Whether a build is the one named, part by part.</summary>
    /// <remarks>
    /// The parts are compared as numbers rather than as text, and an empty or
    /// unreadable build matches nothing - so anything gated on a build stays
    /// off until the build is known.
    /// </remarks>
    public static bool Reads(string build, string wanted)
    {
        int[] mine = Parts(build);
        int[] theirs = Parts(wanted);

        if (mine.Length == 0 || mine.Length != theirs.Length) return false;

        for (int i = 0; i < mine.Length; i++)
        {
            if (mine[i] != theirs[i]) return false;
        }

        return true;
    }

    private static int[] Parts(string build)
    {
        if (string.IsNullOrWhiteSpace(build)) return [];

        string[] pieces = build.Split(['.', ','], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(pieces.Length);

        foreach (string piece in pieces)
        {
            if (!int.TryParse(piece.Trim(), out int number)) return [];
            numbers.Add(number);
        }

        return [.. numbers];
    }

    public bool Exists => !string.IsNullOrEmpty(CookedPath) && Directory.Exists(CookedPath);

    /// <summary>Texture cache manifest that sits beside the cooked packages.</summary>
    public string TextureCacheManifestPath => string.IsNullOrEmpty(CookedPath)
        ? string.Empty
        : Path.Combine(CookedPath, "TextureFileCacheManifest.bin");

    public bool HasTextureCacheManifest =>
        TextureCacheManifestPath.Length > 0 && File.Exists(TextureCacheManifestPath);

    public override string ToString() => $"{DisplayName} ({Format})";
}
