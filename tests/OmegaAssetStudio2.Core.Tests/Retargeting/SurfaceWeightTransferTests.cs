using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Retargeting;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

public sealed class SurfaceWeightTransferTests
{
    private static VertexInfluence Skin(params (int Bone, float Weight)[] parts) => new()
    {
        Bones = parts.Select(p => p.Bone).ToList(),
        Weights = parts.Select(p => p.Weight).ToList(),
    };

    /// <summary>A square in the ground plane, its two halves on different bones.</summary>
    private static SkeletalMeshLod Target()
    {
        Vector3[] positions =
        [
            new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0),
        ];

        return new SkeletalMeshLod
        {
            Sections = [],
            Indices = [0, 1, 2, 0, 2, 3],
            VertexCount = 4,
            Layout = default,
            Positions = positions,
            Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
            TexCoords = new Vector2[4],
            Influences =
            [
                Skin((0, 1f)),   // the near corner
                Skin((1, 1f)),
                Skin((1, 1f)),
                Skin((0, 1f)),
            ],
            Chunks = [],
        };
    }

    [Fact]
    public void AVertexTakesTheSkinningOfWhatIsUnderIt()
    {
        // Sitting just above the far corner, which follows bone one.
        SurfaceTransferResult result = SurfaceWeightTransfer.Apply([new Vector3(10, 10, 1)], Target());

        Assert.Equal(1, result.Report.VerticesBound);
        Assert.Equal(1, result.Influences[0].Bones[0]);
        Assert.Equal(1f, result.Influences[0].Weights.Sum(), 3);
    }

    [Fact]
    public void AVertexBetweenTwoBonesTakesSomethingOfBoth()
    {
        // Halfway along an edge whose ends follow different bones.
        SurfaceTransferResult result = SurfaceWeightTransfer.Apply([new Vector3(5, 0, 0)], Target());

        VertexInfluence influence = result.Influences[0];

        Assert.Equal(2, influence.Bones.Count);
        Assert.Equal(1f, influence.Weights.Sum(), 3);
        Assert.All(influence.Weights, w => Assert.InRange(w, 0.2f, 0.8f));
    }

    [Fact]
    public void WeightsAlwaysAddToOne()
    {
        Vector3[] points =
        [
            new(0, 0, 0), new(5, 5, 2), new(9, 1, -3), new(2, 8, 0.5f),
        ];

        SurfaceTransferResult result = SurfaceWeightTransfer.Apply(points, Target());

        Assert.All(result.Influences, i => Assert.Equal(1f, i.Weights.Sum(), 3));
    }

    [Fact]
    public void HowFarEachVertexHadToReachIsReported()
    {
        // The distance is the warning: a model sitting on the character binds
        // within a whisker, one placed elsewhere binds to whatever is nearest.
        SurfaceTransferResult near = SurfaceWeightTransfer.Apply([new Vector3(5, 5, 0.1f)], Target());
        SurfaceTransferResult far = SurfaceWeightTransfer.Apply([new Vector3(5, 5, 400f)], Target());

        Assert.True(near.Report.LargestDistance < 1f);
        Assert.True(far.Report.LargestDistance > 100f);
    }

    [Fact]
    public void ATargetWithNoGeometryLeavesEverythingUnbound()
    {
        var empty = new SkeletalMeshLod
        {
            Sections = [],
            Indices = [],
            VertexCount = 0,
            Layout = default,
            Positions = [],
            Normals = [],
            TexCoords = [],
            Influences = [],
            Chunks = [],
        };

        SurfaceTransferResult result = SurfaceWeightTransfer.Apply([Vector3.Zero], empty);

        Assert.Equal(1, result.Report.VerticesUnbound);
        Assert.Empty(result.Influences[0].Bones);
    }

    [Fact]
    public void NoVertexEndsUpFollowingMoreBonesThanTheFormatHolds()
    {
        // Four is all the game's own format stores; a blend of three corners
        // each on different bones could otherwise produce more.
        Vector3[] positions = [new(0, 0, 0), new(10, 0, 0), new(5, 10, 0)];

        var crowded = new SkeletalMeshLod
        {
            Sections = [],
            Indices = [0, 1, 2],
            VertexCount = 3,
            Layout = default,
            Positions = positions,
            Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
            TexCoords = new Vector2[3],
            Influences =
            [
                Skin((0, 0.5f), (1, 0.5f)),
                Skin((2, 0.5f), (3, 0.5f)),
                Skin((4, 0.5f), (5, 0.5f)),
            ],
            Chunks = [],
        };

        SurfaceTransferResult result = SurfaceWeightTransfer.Apply([new Vector3(5, 3, 0)], crowded);

        Assert.InRange(result.Influences[0].Bones.Count, 1, 4);
        Assert.Equal(1f, result.Influences[0].Weights.Sum(), 3);
    }

    [Fact]
    public void TheNearestPlaceOnATriangleIsFoundEvenFromOutsideIt()
    {
        Vector3 a = new(0, 0, 0), b = new(10, 0, 0), c = new(0, 10, 0);

        // Straight above the middle: lands on the face.
        Assert.True(Vector3.Distance(
            new Vector3(2, 2, 0),
            SurfaceWeightTransfer.ClosestOnTriangle(a, b, c, new Vector3(2, 2, 5))) < 0.001f);

        // Well beyond one corner: lands on that corner, not on the face's plane.
        Assert.True(Vector3.Distance(
            a, SurfaceWeightTransfer.ClosestOnTriangle(a, b, c, new Vector3(-5, -5, 0))) < 0.001f);
    }
}
