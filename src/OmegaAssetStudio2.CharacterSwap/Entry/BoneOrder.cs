using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Engine.Mesh;
using UpkManager.Models.UpkFile.Objects;
using UpkManager.Models.UpkFile.Tables;
using UpkManager.Repository;

namespace OmegaAssetStudio2.CharacterSwap;

/// <summary>
/// What the two costumes' skeletons have to say to each other.
/// </summary>
/// <remarks>
/// Two costumes of the same character name their bones alike but do not
/// necessarily number them alike. One costume has g_r_palm as bone 92 and a
/// newer body of the same character has it as bone 78, where bone 92 is a
/// thigh. Everything that goes by name is fine either way - the body animates,
/// the sockets resolve - but whatever hangs the hammer on that bone goes by
/// number, and lands on the thigh.
/// <para>
/// This reads both skeletons and says whether they agree. Nothing is changed
/// here: a costume whose skeleton already agrees with the one it replaces must
/// take exactly the path it takes today.
/// </para>
/// </remarks>
public static class BoneOrder
{
    /// <summary>One skeleton, as the bones are numbered in it.</summary>
    public sealed record Skeleton
    {
        public required string MeshName { get; init; }
        public required IReadOnlyList<string> Bones { get; init; }
    }

    /// <summary>What comparing two of them found.</summary>
    public sealed record Comparison
    {
        public Skeleton? Older { get; init; }
        public Skeleton? Newer { get; init; }

        /// <summary>The two number their bones the same way.</summary>
        public bool Agree { get; init; }

        /// <summary>First bone number the two disagree about, or -1.</summary>
        public int FirstDisagreement { get; init; } = -1;

        /// <summary>Named in the older costume's skeleton and not the newer one's.</summary>
        public IReadOnlyList<string> OnlyOlder { get; init; } = [];

        /// <summary>Named in the newer costume's skeleton and not the older one's.</summary>
        public IReadOnlyList<string> OnlyNewer { get; init; } = [];

        /// <summary>How it reads to someone being told about it.</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// The body skeletons of two costume packages, compared. The body is taken
    /// to be the mesh with the most bones, props having only a few.
    /// </summary>
    public static async Task<Comparison> CompareAsync(string olderPackage, string newerPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(olderPackage);
        ArgumentException.ThrowIfNullOrWhiteSpace(newerPackage);

        Skeleton? older = await BodyOfAsync(olderPackage).ConfigureAwait(false);
        Skeleton? newer = await BodyOfAsync(newerPackage).ConfigureAwait(false);

        if (older is null || newer is null)
        {
            return new Comparison
            {
                Older = older,
                Newer = newer,
                Agree = true, // nothing to act on, so nothing is out of order
                Summary = "bone order: one of the two has no body to read, so it is left alone",
            };
        }

        int first = -1;
        int common = Math.Min(older.Bones.Count, newer.Bones.Count);

        for (int i = 0; i < common; i++)
        {
            if (string.Equals(older.Bones[i], newer.Bones[i], StringComparison.OrdinalIgnoreCase)) continue;

            first = i;
            break;
        }

        if (first < 0 && older.Bones.Count != newer.Bones.Count) first = common;

        var olderSet = new HashSet<string>(older.Bones, StringComparer.OrdinalIgnoreCase);
        var newerSet = new HashSet<string>(newer.Bones, StringComparer.OrdinalIgnoreCase);

        string[] onlyOlder = [.. older.Bones.Where(b => !newerSet.Contains(b))];
        string[] onlyNewer = [.. newer.Bones.Where(b => !olderSet.Contains(b))];

        bool agree = first < 0;

        string summary = agree
            ? $"bone order: '{newer.MeshName}' numbers its {newer.Bones.Count} bones exactly as '{older.MeshName}' does, so nothing needs reordering"
            : $"bone order: '{newer.MeshName}' ({newer.Bones.Count} bones) and '{older.MeshName}' ({older.Bones.Count}) "
              + $"part company at bone {first}"
              + (first < common ? $" - '{older.Bones[first]}' there against '{newer.Bones[first]}'" : string.Empty)
              + $"; {onlyOlder.Length} bone(s) only the older has, {onlyNewer.Length} only the newer";

        return new Comparison
        {
            Older = older,
            Newer = newer,
            Agree = agree,
            FirstDisagreement = first,
            OnlyOlder = onlyOlder,
            OnlyNewer = onlyNewer,
            Summary = summary,
        };
    }

    /// <summary>
    /// The body mesh of a package, which is the skeletal mesh with the most
    /// bones - a hammer or a pair of wings has a handful.
    /// </summary>
    public static async Task<Skeleton?> BodyOfAsync(string package)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        if (!File.Exists(package)) return null;

        var repo = new UpkFileRepository();
        UnrealHeader header = await repo.LoadUpkFile(package).ConfigureAwait(false);

        await header.ReadHeaderAsync(null).ConfigureAwait(false);

        Skeleton? best = null;

        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            if (!string.Equals(export.ClassReferenceNameIndex?.Name, "skeletalmesh", StringComparison.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<string>? bones = await BonesOfAsync(export).ConfigureAwait(false);

            if (bones is null || bones.Count == 0) continue;
            if (best is not null && bones.Count <= best.Bones.Count) continue;

            best = new Skeleton { MeshName = export.ObjectNameIndex?.Name ?? "?", Bones = bones };
        }

        return best;
    }

    private static async Task<IReadOnlyList<string>?> BonesOfAsync(UnrealExportTableEntry export)
    {
        try
        {
            if (export.UnrealObject is null)
                await export.ParseUnrealObject(false, false).ConfigureAwait(false);

            if (export.UnrealObject is not IUnrealObject holder || holder.UObject is not USkeletalMesh mesh)
                return null;

            if (mesh.RefSkeleton is null) return null;

            return [.. mesh.RefSkeleton.Select(b => b.Name?.Name ?? string.Empty)];
        }
        catch
        {
            // A mesh that cannot be read tells us nothing, and a costume is not
            // worth refusing over it.
            return null;
        }
    }
}
