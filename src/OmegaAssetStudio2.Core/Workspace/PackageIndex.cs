using System.Collections.Concurrent;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>Where an object referenced from elsewhere actually lives.</summary>
public readonly record struct ObjectLocation(string PackagePath, int ExportIndex)
{
    public override string ToString() => $"{Path.GetFileName(PackagePath)}#{ExportIndex}";
}

/// <summary>
/// Knows which file holds each material and texture in a game folder.
/// </summary>
/// <remarks>
/// A character package references materials it does not contain, and the file
/// holding them is not named after them: the package name written into the
/// reference is an internal name that matches no file on disk. The only
/// reliable way to follow such a reference is to know what every package
/// exports, so that is what this builds.
/// <para>
/// Objects are keyed by their full dotted path, never by name alone. The same
/// material name appears in several packages under different paths — a costume's
/// own material and a loading screen's copy of it — and picking by name would
/// silently paint a model with the wrong one.
/// </para>
/// <para>
/// Only materials and textures are indexed. Indexing every export of every
/// package would hold millions of entries in memory to answer a question nobody
/// asks.
/// </para>
/// </remarks>
public sealed class PackageIndex
{
    /// <summary>Classes worth remembering the location of.</summary>
    private static readonly HashSet<string> IndexedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "material",
        "materialinstanceconstant",
        "materialinstancetimevarying",
        "texture2d",
        "texturecube",
        "texturemovie",
    };

    private readonly Dictionary<string, ObjectLocation> _byPath;

    private PackageIndex(Dictionary<string, ObjectLocation> byPath) => _byPath = byPath;

    /// <summary>How many objects are known.</summary>
    public int Count => _byPath.Count;

    /// <summary>How many packages were read to build this.</summary>
    public int PackagesRead { get; private init; }

    /// <summary>How many packages could not be read, and so are not covered.</summary>
    public int PackagesSkipped { get; private init; }

    /// <summary>Every object whose path mentions a word.</summary>
    public IEnumerable<KeyValuePair<string, ObjectLocation>> Mentioning(string word)
    {
        foreach (var pair in _byPath)
        {
            if (pair.Key.Contains(word, StringComparison.OrdinalIgnoreCase)) yield return pair;
        }
    }

    /// <summary>Finds where an object lives, by its full dotted path.</summary>
    public ObjectLocation? Find(string objectPath) =>
        _byPath.TryGetValue(objectPath, out ObjectLocation location) ? location : null;

    /// <summary>
    /// Reads every package in a game folder and records what it exports.
    /// </summary>
    /// <remarks>
    /// Runs across all processors because it is thousands of independent reads;
    /// on a real install this takes a few seconds, which is why callers build it
    /// once in the background rather than on demand.
    /// </remarks>
    public static PackageIndex Build(
        GameClient client,
        IProgress<int>? progress = null,
        CancellationToken cancellation = default)
    {
        var found = new ConcurrentDictionary<string, ObjectLocation>(StringComparer.OrdinalIgnoreCase);

        string[] paths = Directory.Exists(client.CookedPath)
            ? Directory.GetFiles(client.CookedPath, "*.upk")
            : [];

        int read = 0, skipped = 0, done = 0;

        Parallel.ForEach(
            paths,
            new ParallelOptions
            {
                CancellationToken = cancellation,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            },
            path =>
            {
                Package package;

                try
                {
                    package = Package.Open(path);
                    Interlocked.Increment(ref read);
                }
                catch (Exception)
                {
                    // A package that will not open costs its own contents, not
                    // the index. The count is reported so a caller can say how
                    // complete the answer is.
                    Interlocked.Increment(ref skipped);
                    return;
                }

                Record(package, path, found);

                int count = Interlocked.Increment(ref done);
                if (count % 250 == 0) progress?.Report(count);
            });

        progress?.Report(paths.Length);

        return new PackageIndex(new Dictionary<string, ObjectLocation>(found, StringComparer.OrdinalIgnoreCase))
        {
            PackagesRead = read,
            PackagesSkipped = skipped,
        };
    }

    private static void Record(
        Package package, string path, ConcurrentDictionary<string, ObjectLocation> found)
    {
        for (int i = 0; i < package.Exports.Count; i++)
        {
            string className;
            string objectPath;

            try
            {
                className = package.GetExportClassName(i);
                if (!IndexedClasses.Contains(className)) continue;

                objectPath = package.GetExportPath(i);
            }
            catch (InvalidPackageException)
            {
                continue;
            }

            if (objectPath.Length == 0) continue;

            var location = new ObjectLocation(path, i);

            // Several packages can export the same path — a costume's own copy
            // and a test or loading-screen copy of it. The game's own content
            // packages are the ones a model means, so they win; otherwise the
            // first name alphabetically, so the answer never depends on the
            // order threads happened to finish in.
            found.AddOrUpdate(objectPath, location, (_, existing) => Prefer(existing, location));
        }
    }

    private static ObjectLocation Prefer(ObjectLocation a, ObjectLocation b)
    {
        string nameA = Path.GetFileName(a.PackagePath);
        string nameB = Path.GetFileName(b.PackagePath);

        bool contentA = nameA.StartsWith("UC__", StringComparison.OrdinalIgnoreCase);
        bool contentB = nameB.StartsWith("UC__", StringComparison.OrdinalIgnoreCase);

        if (contentA != contentB) return contentA ? a : b;

        return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase) <= 0 ? a : b;
    }
}
