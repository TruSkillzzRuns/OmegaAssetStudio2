using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>
/// Turns an install folder the user picked into a usable <see cref="GameClient"/>:
/// finds the cooked package folder and reads the package format off disk.
/// </summary>
public static class GameClientLocator
{
    /// <summary>
    /// Where the cooked folder sits relative to the install root, most specific
    /// first. The engine folder holds a game-named subfolder whose name varies by
    /// title, so the search walks for the cooked folder rather than assuming it.
    /// </summary>
    private const string CookedFolderName = "CookedPCConsole";

    /// <summary>
    /// Builds a client descriptor from an install root. Accepts either the install
    /// root or the cooked folder itself, so the user cannot reasonably pick wrong.
    /// Returns null when no cooked folder can be found underneath.
    /// </summary>
    public static GameClient? FromRoot(string rootPath, string displayName, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return null;

        string? cooked = FindCookedFolder(rootPath);
        if (cooked is null)
            return null;

        return new GameClient
        {
            Id = id ?? Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? new DirectoryInfo(rootPath).Name
                : displayName,
            RootPath = Path.GetFullPath(rootPath),
            CookedPath = cooked,
            Format = ReadPackageFormat(cooked),
            Build = ReadBuild(rootPath),
        };
    }

    /// <summary>
    /// The build an install is, taken from its own executable.
    /// </summary>
    /// <remarks>
    /// Nothing in the cooked folder tells one build from another: they share a
    /// folder name, a package format, and even the same MaxAnisotropy buckets
    /// in their configuration. The executable's version resource does.
    /// </remarks>
    public static string ReadBuild(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return string.Empty;

        string[] places =
        [
            Path.Combine("UnrealEngine3", "Binaries", "Win64"),
            Path.Combine("UnrealEngine3", "Binaries", "Win32"),
        ];

        foreach (string place in places)
        {
            string folder = Path.Combine(rootPath, place);
            if (!Directory.Exists(folder)) continue;

            // The game's own executable first, then whatever else is there, so
            // an install carrying a launcher or a helper beside it still
            // answers with the build the game is.
            IEnumerable<string> files =
            [
                .. Directory.EnumerateFiles(folder, "*.exe")
                            .Where(f => Path.GetFileName(f).StartsWith("MarvelHeroes", StringComparison.OrdinalIgnoreCase)),
                .. Directory.EnumerateFiles(folder, "*.exe"),
            ];

            foreach (string file in files)
            {
                string? said;
                try { said = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).ProductVersion; }
                catch (Exception) { continue; }

                if (string.IsNullOrWhiteSpace(said)) continue;

                // The resource writes its parts with commas.
                return said.Replace(',', '.').Replace(" ", string.Empty);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The build an install is, found from its cooked folder rather than from
    /// its root, for the readers that only ever see the cooked path.
    /// </summary>
    /// <remarks>
    /// Read once per folder. Walking up for the executable and reading its
    /// version resource on every material would cost more than the reading it
    /// decides.
    /// </remarks>
    public static string BuildBesideCooked(string cookedPath)
    {
        if (string.IsNullOrWhiteSpace(cookedPath)) return string.Empty;

        lock (Builds)
        {
            if (Builds.TryGetValue(cookedPath, out string? already)) return already;
        }

        string found = string.Empty;
        DirectoryInfo? walking = Directory.Exists(cookedPath) ? new DirectoryInfo(cookedPath) : null;

        // CookedPCConsole sits three deep in an install: MarvelGame, then the
        // engine folder, then the root. One more than that leaves room for a
        // layout that nests differently.
        for (int up = 0; up < 4 && walking is not null; up++)
        {
            found = ReadBuild(walking.FullName);
            if (found.Length > 0) break;

            walking = walking.Parent;
        }

        lock (Builds) Builds[cookedPath] = found;

        return found;
    }

    private static readonly Dictionary<string, string> Builds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the cooked package folder under <paramref name="rootPath"/>. Checks
    /// the obvious places first, then falls back to a bounded search so an
    /// unusual install layout still resolves.
    /// </summary>
    public static string? FindCookedFolder(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return null;

        // The user may have pointed straight at the cooked folder.
        if (string.Equals(new DirectoryInfo(rootPath).Name, CookedFolderName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(rootPath);

        string direct = Path.Combine(rootPath, CookedFolderName);
        if (Directory.Exists(direct)) return Path.GetFullPath(direct);

        // Depth-limited walk. The cooked folder normally sits two levels down,
        // under an engine folder and a title-named folder. Unbounded recursion
        // over a game install is slow and can wander into content directories.
        try
        {
            foreach (string level1 in Directory.EnumerateDirectories(rootPath))
            {
                string candidate = Path.Combine(level1, CookedFolderName);
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

                foreach (string level2 in Directory.EnumerateDirectories(level1))
                {
                    string deeper = Path.Combine(level2, CookedFolderName);
                    if (Directory.Exists(deeper)) return Path.GetFullPath(deeper);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return null;
    }

    /// <summary>
    /// Reads the package format from packages in <paramref name="cookedPath"/>.
    /// Samples several rather than trusting one, and returns the format only if
    /// the sample agrees — a split result means something is wrong with the
    /// install and callers should not silently assume either answer.
    /// </summary>
    public static PackageFormat ReadPackageFormat(string cookedPath, int sampleSize = 5)
    {
        if (string.IsNullOrWhiteSpace(cookedPath) || !Directory.Exists(cookedPath))
            return PackageFormat.Unknown;

        PackageFormat? agreed = null;
        int read = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(cookedPath, "*.upk"))
            {
                PackageFormat format = ReadPackageFormatFromFile(file);
                if (!format.IsKnown) continue;

                if (agreed is null) agreed = format;
                else if (!agreed.Value.Equals(format)) return PackageFormat.Unknown;

                if (++read >= sampleSize) break;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return agreed ?? PackageFormat.Unknown;
    }

    /// <summary>
    /// Reads the format from a single package. Returns Unknown for anything that
    /// is not a cooked package rather than throwing.
    /// </summary>
    public static PackageFormat ReadPackageFormatFromFile(string packagePath)
    {
        try
        {
            // Goes through the real header reader rather than peeking at bytes,
            // so there is exactly one definition of what a package looks like.
            return PackageHeader.ReadFromFile(packagePath, probeBytes: 4096).Format;
        }
        catch (InvalidPackageException) { return PackageFormat.Unknown; }
        catch (IOException) { return PackageFormat.Unknown; }
        catch (UnauthorizedAccessException) { return PackageFormat.Unknown; }
    }
}
