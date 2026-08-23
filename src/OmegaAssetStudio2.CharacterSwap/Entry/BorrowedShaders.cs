using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio2.CharacterSwap;

/// <summary>
/// Some costumes are a body and nothing else. They carry no shader and no
/// texture of their own and borrow both from another costume of the same
/// character - a helmetless costume wears the helmeted one's shader, and a
/// variant wears the shader of the costume it varies. Brought over alone, such a costume arrives
/// with nothing to draw itself with and the game draws it flat.
/// </summary>
/// <remarks>
/// This finds the costume it borrows from, so the shaders can be brought over
/// first and the body then transplanted onto a chassis that already holds them.
/// <para>
/// The tool has a pass like this already, but pointed the other way: it follows
/// what the OLDER costume borrows. This follows what the NEWER one borrows.
/// </para>
/// </remarks>
public static class BorrowedShaders
{
    /// <summary>What a costume needs before it can be worn.</summary>
    public sealed record Lender
    {
        /// <summary>The costume package holding the shaders, beside the newer one.</summary>
        public required string Package { get; init; }

        /// <summary>The shaders it is being asked for, by name.</summary>
        public required IReadOnlyList<string> Shaders { get; init; }
    }

    /// <summary>
    /// The costume this one borrows its shaders from, or null when it has its
    /// own and needs nobody.
    /// </summary>
    public static async Task<Lender?> LenderForAsync(string costumePackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(costumePackage);

        if (!File.Exists(costumePackage)) return null;

        var repo = new UpkFileRepository();
        UnrealHeader costume = await repo.LoadUpkFile(costumePackage).ConfigureAwait(false);

        await costume.ReadHeaderAsync(null).ConfigureAwait(false);

        // A costume with shaders of its own is not borrowing anything.
        bool hasItsOwn = costume.ExportTable.Any(e =>
            string.Equals(e.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase));

        if (hasItsOwn) return null;

        // What it asks for: shaders, but not the base materials every costume
        // names, which come from the older game's own packages.
        var wanted = new List<string>();

        foreach (UnrealImportTableEntry import in costume.ImportTable)
        {
            string cls = import.ClassNameIndex?.Name ?? string.Empty;

            if (!cls.Equals("materialinstanceconstant", StringComparison.OrdinalIgnoreCase)
                && !cls.Equals("material", StringComparison.OrdinalIgnoreCase))
                continue;

            string name = import.ObjectNameIndex?.Name ?? string.Empty;

            if (name.Length == 0) continue;
            if (name.StartsWith("chbasematerial", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("hairshader", StringComparison.OrdinalIgnoreCase)) continue;

            if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) wanted.Add(name);
        }

        if (wanted.Count == 0) return null;

        // Who has them. The lender sits beside the costume, being another
        // costume for the same character out of the same game.
        string folder = Path.GetDirectoryName(costumePackage) ?? string.Empty;

        if (folder.Length == 0) return null;

        string mine = Path.GetFileNameWithoutExtension(costumePackage);
        string? best = null;
        int bestHas = 0;

        foreach (string candidate in Directory.EnumerateFiles(folder, "UC__MarvelPlayer_*.upk"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(candidate), mine, StringComparison.OrdinalIgnoreCase))
                continue;

            int has;

            try
            {
                has = await HowManyOfAsync(repo, candidate, wanted).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (has <= bestHas) continue;

            best = candidate;
            bestHas = has;

            if (bestHas == wanted.Count) break; // nobody can do better
        }

        return best is null ? null : new Lender { Package = best, Shaders = wanted };
    }

    private static async Task<int> HowManyOfAsync(
        UpkFileRepository repo,
        string package,
        IReadOnlyList<string> wanted)
    {
        UnrealHeader header = await repo.LoadUpkFile(package).ConfigureAwait(false);

        await header.ReadHeaderAsync(null).ConfigureAwait(false);

        int found = 0;

        foreach (string name in wanted)
        {
            bool exports = header.ExportTable.Any(e =>
                string.Equals(e.ObjectNameIndex?.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.ClassReferenceNameIndex?.Name, "materialinstanceconstant", StringComparison.OrdinalIgnoreCase));

            if (exports) found++;
        }

        return found;
    }
}
