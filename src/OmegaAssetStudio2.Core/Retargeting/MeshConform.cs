using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;

namespace OmegaAssetStudio2.Core.Retargeting;

/// <summary>A model reshaped to sit on another skeleton.</summary>
public sealed record ConformResult
{
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }

    /// <summary>How far the furthest vertex moved.</summary>
    public required float LargestMove { get; init; }

    /// <summary>How far the average vertex moved.</summary>
    public required float AverageMove { get; init; }

    public override string ToString() =>
        $"{Positions.Count:N0} vertices, moved {AverageMove:0.##} on average, {LargestMove:0.##} at most";
}

/// <summary>
/// Reshapes a model to fit the skeleton it has been rebound to.
/// </summary>
/// <remarks>
/// Rebinding alone changes which bones a vertex follows, not where it sits, so
/// a model rebound to a shorter character keeps its own proportions and its
/// bones no longer line up with its surface. Conforming moves each vertex the
/// way its bones moved.
/// <para>
/// For one bone, that is: take the vertex into the bone's own space using where
/// that bone rested on the original skeleton, then back out using where the
/// matching bone rests on the new one. A vertex following several bones is
/// moved by each and the results blended by weight, which is the same
/// arithmetic that poses a model every frame in a game.
/// </para>
/// </remarks>
public static class MeshConform
{
    /// <summary>
    /// Reshapes a level of detail onto the target skeleton.
    /// </summary>
    /// <param name="lod">The geometry, with its original positions.</param>
    /// <param name="rebound">
    /// The skinning after transfer, naming bones on the target skeleton.
    /// </param>
    /// <param name="source">Where the original skeleton rests.</param>
    /// <param name="target">Where the new skeleton rests.</param>
    /// <param name="map">Which target bone stands for which source bone.</param>
    public static ConformResult Apply(
        SkeletalMeshLod lod,
        IReadOnlyList<VertexInfluence> rebound,
        SkeletonPose source,
        SkeletonPose target,
        BoneMap map)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(rebound);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(map);

        // Built once: for each target bone, the move that carries a vertex from
        // where the original bone rested to where this one does.
        Matrix4x4[] moves = BuildMoves(source, target, map);

        var positions = new Vector3[lod.Positions.Count];
        var normals = new Vector3[lod.Positions.Count];

        float largest = 0f;
        double totalMoved = 0;

        for (int v = 0; v < lod.Positions.Count; v++)
        {
            Vector3 from = lod.Positions[v];
            Vector3 fromNormal = v < lod.Normals.Count ? lod.Normals[v] : Vector3.UnitZ;

            VertexInfluence influence = v < rebound.Count
                ? rebound[v]
                : new VertexInfluence { Bones = [], Weights = [] };

            if (influence.Count == 0)
            {
                // Nothing holds this vertex, so it stays where it is rather
                // than being dragged to the origin.
                positions[v] = from;
                normals[v] = fromNormal;
                continue;
            }

            var moved = Vector3.Zero;
            var movedNormal = Vector3.Zero;

            for (int i = 0; i < influence.Count; i++)
            {
                int bone = influence.Bones[i];
                if (bone < 0 || bone >= moves.Length) continue;

                float weight = influence.Weights[i];

                moved += Vector3.Transform(from, moves[bone]) * weight;

                // Directions are carried by the rotation only; a translation
                // would turn a direction into a point.
                movedNormal += Vector3.TransformNormal(fromNormal, moves[bone]) * weight;
            }

            positions[v] = moved;

            float length = movedNormal.Length();
            normals[v] = length > 0.0001f ? movedNormal / length : fromNormal;

            float distance = Vector3.Distance(from, moved);
            totalMoved += distance;
            if (distance > largest) largest = distance;
        }

        return new ConformResult
        {
            Positions = positions,
            Normals = normals,
            LargestMove = largest,
            AverageMove = positions.Length == 0 ? 0f : (float)(totalMoved / positions.Length),
        };
    }

    /// <summary>
    /// For each target bone, the move from where its source counterpart rested
    /// to where it rests.
    /// </summary>
    /// <remarks>
    /// A target bone nothing was matched to gets no move at all rather than a
    /// guess. Leaving it as no change keeps any surface that reaches it where
    /// it already is, which is wrong by less than an invented transform would
    /// be.
    /// </remarks>
    private static Matrix4x4[] BuildMoves(SkeletonPose source, SkeletonPose target, BoneMap map)
    {
        var moves = new Matrix4x4[target.Count];

        for (int i = 0; i < moves.Length; i++) moves[i] = Matrix4x4.Identity;

        foreach (BonePair pair in map.Pairs)
        {
            if (pair.SourceIndex >= source.Count || pair.TargetIndex >= target.Count) continue;

            // Into the original bone's space, then back out of the new one's.
            moves[pair.TargetIndex] =
                source.ModelToBone[pair.SourceIndex] * target.BoneToModel[pair.TargetIndex];
        }

        return moves;
    }
}
