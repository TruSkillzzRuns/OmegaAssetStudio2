using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Workspace;

/// <summary>An object found, and the package it was found in.</summary>
public readonly record struct LocatedObject(Package Package, int ExportIndex)
{
    public string Name => Package.GetExportName(ExportIndex);

    /// <summary>True when the object was not in the package that referenced it.</summary>
    public required bool CameFromElsewhere { get; init; }
}

/// <summary>
/// Follows an object reference to the object, wherever it lives.
/// </summary>
/// <remarks>
/// Most references point inside the package holding them, because character
/// content is cooked to stand alone. The ones that do not are followed through
/// an index of the whole game folder, which is optional: without it this still
/// resolves everything local, and simply reports the rest as not found.
/// <para>
/// Packages opened along the way are kept, because a model's materials and their
/// textures usually share a package and reopening it per slot would decompress
/// the same megabytes over and over.
/// </para>
/// </remarks>
public sealed class ObjectLocator
{
    /// <summary>
    /// How many packages to hold open. A model reaches into a handful at most,
    /// so this is about not growing without bound over a long session.
    /// </summary>
    private const int MaxOpenPackages = 12;

    private readonly PackageIndex? _index;
    private readonly Dictionary<string, Package> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public ObjectLocator(PackageIndex? index = null) => _index = index;

    /// <summary>True when references can be followed outside their own package.</summary>
    public bool CanFollowAcrossPackages => _index is not null;

    /// <summary>
    /// Finds what a reference points at.
    /// </summary>
    /// <param name="package">The package the reference was read from.</param>
    public LocatedObject? TryLocate(Package package, ObjectReference reference)
    {
        if (reference.IsNull) return null;

        if (reference.IsExport)
        {
            return reference.ExportIndex < package.Exports.Count
                ? new LocatedObject(package, reference.ExportIndex) { CameFromElsewhere = false }
                : null;
        }

        string importPath;
        try { importPath = package.GetImportPath(reference.ImportIndex); }
        catch (InvalidPackageException) { return null; }

        // Cooked packages often import an object they also contain, so the local
        // copy is preferred: it is the one the rest of this package agrees with,
        // and it costs nothing to reach.
        int local = FindByPath(package, importPath);
        if (local >= 0) return new LocatedObject(package, local) { CameFromElsewhere = false };

        ObjectLocation? located = _index?.Find(importPath);
        if (located is null) return null;

        Package? owner = TryOpen(located.Value.PackagePath);
        if (owner is null) return null;

        return located.Value.ExportIndex < owner.Exports.Count
            ? new LocatedObject(owner, located.Value.ExportIndex) { CameFromElsewhere = true }
            : null;
    }

    /// <summary>
    /// Finds an export by its full path. Falls back to the object's own name,
    /// which covers packages that record a shorter path for their own contents.
    /// </summary>
    private static int FindByPath(Package package, string objectPath)
    {
        if (objectPath.Length == 0) return -1;

        for (int i = 0; i < package.Exports.Count; i++)
        {
            try
            {
                if (string.Equals(package.GetExportPath(i), objectPath, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            catch (InvalidPackageException)
            {
                // A malformed outer chain on one export does not stop the search.
            }
        }

        int lastDot = objectPath.LastIndexOf('.');
        string name = lastDot >= 0 ? objectPath[(lastDot + 1)..] : objectPath;

        for (int i = 0; i < package.Exports.Count; i++)
        {
            if (string.Equals(package.GetExportName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private Package? TryOpen(string path)
    {
        if (_open.TryGetValue(path, out Package? already)) return already;

        Package package;
        try { package = Package.Open(path); }
        catch (Exception) { return null; }

        _open[path] = package;
        _order.Enqueue(path);

        while (_order.Count > MaxOpenPackages)
            _open.Remove(_order.Dequeue());

        return package;
    }
}
