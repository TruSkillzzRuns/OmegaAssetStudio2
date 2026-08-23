using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>What happened when a model's skinning was moved to another skeleton.</summary>
public sealed record TransferReport
{
    /// <summary>Vertices whose bones all had a counterpart.</summary>
    public required int VerticesKept { get; init; }

    /// <summary>Vertices where some weight had to be moved to a parent.</summary>
    public required int VerticesRerouted { get; init; }

    /// <summary>Vertices that lost weight because nothing could hold it.</summary>
    public required int VerticesDropped { get; init; }

    /// <summary>
    /// Names of source bones whose weight was moved elsewhere, and how much of
    /// the model's total weight went with them.
    /// </summary>
    public required IReadOnlyDictionary<string, float> ReroutedFrom { get; init; }

    public int VertexCount => VerticesKept + VerticesRerouted + VerticesDropped;

    /// <summary>What proportion of vertices came through untouched.</summary>
    public double CleanRate => VertexCount == 0 ? 0 : VerticesKept / (double)VertexCount;

    public override string ToString() =>
        $"{VerticesKept:N0} kept, {VerticesRerouted:N0} rerouted, {VerticesDropped:N0} dropped";
}

/// <summary>A model's skinning, moved onto another skeleton.</summary>
public sealed record TransferResult
{
    public required IReadOnlyList<VertexInfluence> Influences { get; init; }
    public required TransferReport Report { get; init; }
}

/// <summary>
/// Moves a model's skinning from the skeleton it was made for onto another.
/// </summary>
/// <remarks>
/// Every vertex names the bones it follows. Those numbers mean nothing on a
/// different skeleton, so each is translated through the bone map.
/// <para>
/// The case that decides whether the result is usable is a bone with no
/// counterpart — a cape, a piece of hair, a helper the other character does not
/// have. Dropping its weight outright leaves that part of the surface following
/// nothing, and it collapses toward the origin. Instead the weight is handed up
/// the skeleton to the nearest ancestor that does have a counterpart, which is
/// where that part of the body is anchored anyway. What was moved is reported,
/// because a cape rigidly following a spine is a thing the user should be told
/// about rather than left to notice.
/// </para>
/// </remarks>
public static class WeightTransfer
{
    /// <summary>
    /// Moves the skinning of one level of detail onto the target skeleton.
    /// </summary>
    /// <param name="lod">The geometry to move.</param>
    /// <param name="sourceBones">The skeleton it was made for.</param>
    /// <param name="map">Which target bone stands for which source bone.</param>
    public static TransferResult Apply(
        SkeletalMeshLod lod,
        IReadOnlyList<MeshBone> sourceBones,
        BoneMap map)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(sourceBones);
        ArgumentNullException.ThrowIfNull(map);

        var moved = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var result = new VertexInfluence[lod.Influences.Count];

        int kept = 0, rerouted = 0, dropped = 0;

        for (int v = 0; v < lod.Influences.Count; v++)
        {
            VertexInfluence influence = lod.Influences[v];

            // Weight is gathered per target bone rather than per slot: two
            // source bones can lead to the same target one, and left as two
            // slots they would compete instead of adding up.
            var gathered = new Dictionary<int, float>(influence.Count);

            bool anyMoved = false;
            float lost = 0f;

            for (int i = 0; i < influence.Count; i++)
            {
                int sourceBone = influence.Bones[i];
                float weight = influence.Weights[i];

                int target = Resolve(sourceBone, sourceBones, map, out bool viaParent);

                if (target < 0)
                {
                    lost += weight;
                    Record(moved, Name(sourceBones, sourceBone), weight);
                    continue;
                }

                if (viaParent)
                {
                    anyMoved = true;
                    Record(moved, Name(sourceBones, sourceBone), weight);
                }

                gathered[target] = gathered.GetValueOrDefault(target) + weight;
            }

            if (gathered.Count == 0)
            {
                // Nothing at all could hold this vertex. It keeps no influence
                // rather than being pinned to the root, which would stretch it
                // across the model.
                result[v] = new VertexInfluence { Bones = [], Weights = [] };
                dropped++;
                continue;
            }

            // Whatever was lost is made up by the bones that remain, so the
            // vertex still follows exactly one bone's worth of movement.
            var bones = new List<int>(gathered.Count);
            var weights = new List<float>(gathered.Count);

            float total = 0f;
            foreach (float weight in gathered.Values) total += weight;

            foreach ((int bone, float weight) in gathered)
            {
                bones.Add(bone);
                weights.Add(total > 0.0001f ? weight / total : 1f / gathered.Count);
            }

            result[v] = new VertexInfluence { Bones = bones, Weights = weights };

            if (lost > 0.0001f || anyMoved) rerouted++;
            else kept++;
        }

        return new TransferResult
        {
            Influences = result,
            Report = new TransferReport
            {
                VerticesKept = kept,
                VerticesRerouted = rerouted,
                VerticesDropped = dropped,
                ReroutedFrom = moved,
            },
        };
    }

    /// <summary>
    /// Finds the target bone a source bone's weight should go to, walking up
    /// the skeleton when the bone itself has no counterpart.
    /// </summary>
    private static int Resolve(
        int sourceBone, IReadOnlyList<MeshBone> sourceBones, BoneMap map, out bool viaParent)
    {
        viaParent = false;

        if (map.For(sourceBone) is { } direct) return direct.TargetIndex;

        // Up the chain. Guarded against a skeleton whose parents form a loop,
        // which would otherwise hang here rather than fail.
        var seen = new HashSet<int>();
        int at = sourceBone;

        while (at >= 0 && at < sourceBones.Count && seen.Add(at))
        {
            int parent = sourceBones[at].ParentIndex;

            // The root names itself as its own parent, which would spin.
            if (parent == at) break;
            if (parent < 0 || parent >= sourceBones.Count) break;

            if (map.For(parent) is { } found)
            {
                viaParent = true;
                return found.TargetIndex;
            }

            at = parent;
        }

        return -1;
    }

    private static string Name(IReadOnlyList<MeshBone> bones, int index) =>
        index >= 0 && index < bones.Count ? bones[index].Name : $"bone {index}";

    private static void Record(Dictionary<string, float> into, string name, float weight) =>
        into[name] = into.GetValueOrDefault(name) + weight;
}
