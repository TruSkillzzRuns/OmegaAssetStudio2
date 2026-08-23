using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Retargeting;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

public sealed class SourceModelTests
{
    private static ImportedMesh Mesh(Vector3[] points, int[] wedgePoints, int[] indices) => new()
    {
        Positions = points,
        TexCoords = wedgePoints.Select(_ => Vector2.Zero).ToArray(),
        WedgePoints = wedgePoints,
        Indices = indices,
        Materials = [],
        Bones = [],
        Weights = points.Select(_ => (IReadOnlyList<(int, float)>)[]).ToArray(),
    };

    [Fact]
    public void EveryDrawnCornerGetsItsOwnEntry()
    {
        // Two corners share a position in the file; both must come through, or
        // they cannot carry different texture coordinates.
        SourceModel model = SourceModelBuilder.Build(
            Mesh([new Vector3(1, 2, 3)], [0, 0, 0], [0, 1, 2]));

        Assert.Equal(3, model.Geometry.Positions.Count);
        Assert.All(model.Geometry.Positions, p => Assert.Equal(new Vector3(1, 2, 3), p));
    }

    [Fact]
    public void ASurfaceIsGivenADirectionToFace()
    {
        // A flat triangle in the ground plane faces straight up.
        SourceModel model = SourceModelBuilder.Build(
            Mesh([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], [0, 1, 2], [0, 1, 2]));

        Assert.All(model.Geometry.Normals, n => Assert.Equal(1f, n.Z, 3));
    }

    [Fact]
    public void TurningTheTrianglesAroundTurnsTheSurfaceOver()
    {
        ImportedMesh mesh = Mesh([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], [0, 1, 2], [0, 1, 2]);

        SourceModel asIs = SourceModelBuilder.Build(mesh);
        SourceModel flipped = SourceModelBuilder.Build(mesh, flipWinding: true);

        Assert.Equal(1f, asIs.Geometry.Normals[0].Z, 3);
        Assert.Equal(-1f, flipped.Geometry.Normals[0].Z, 3);
    }

    [Fact]
    public void ACornerNoTriangleUsesStillFacesSomewhere()
    {
        // A zero direction lights that part of the surface black.
        SourceModel model = SourceModelBuilder.Build(
            Mesh([Vector3.Zero, Vector3.One], [0, 1], []));

        Assert.All(model.Geometry.Normals, n => Assert.True(n.Length() > 0.9f));
    }
}

public sealed class RetargetRunTests
{
    private static MeshBone Bone(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = position,
    };

    private static ImportedBone Source(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        Orientation = Quaternion.Identity,
        Position = position,
    };

    private static SkeletalMesh Target(params MeshBone[] bones) => new()
    {
        Name = "target",
        ObjectPath = "target",
        Bounds = default,
        Bones = bones,
        Lods = [],
        Materials = [],
    };

    /// <summary>A target carrying a surface, for the paths that copy from one.</summary>
    private static SkeletalMesh TargetWithGeometry()
    {
        Vector3[] positions = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)];

        var lod = new SkeletalMeshLod
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
                new VertexInfluence { Bones = [0], Weights = [1f] },
                new VertexInfluence { Bones = [0], Weights = [1f] },
                new VertexInfluence { Bones = [0], Weights = [1f] },
            ],
            Chunks = [],
        };

        return new SkeletalMesh
        {
            Name = "target",
            ObjectPath = "target",
            Bounds = default,
            Bones = [Bone("root", 0, Vector3.Zero)],
            Lods = [lod],
            Materials = [],
        };
    }

    private static SourceModel Model(ImportedBone[] bones, Vector3 point, int bone)
    {
        var mesh = new ImportedMesh
        {
            Positions = [point],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            WedgePoints = [0, 0, 0],
            Indices = [0, 1, 2],
            Materials = ["skin"],
            Bones = bones,
            Weights = [new[] { (bone, 1f) }],
        };

        return SourceModelBuilder.Build(mesh);
    }

    [Fact]
    public void AModelIsFittedToTheTargetSkeleton()
    {
        // The matching bone sits four further along, so the model follows it.
        SourceModel source = Model(
            [Source("root", 0, Vector3.Zero), Source("g_l_hip", 0, new Vector3(0, 0, 10))],
            new Vector3(1, 0, 10), bone: 1);

        SkeletalMesh target = Target(
            Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 14)));

        RetargetOutcome outcome = RetargetRun.Run(source, target, new RetargetOptions { Shape = ShapeHandling.FitToRestPose });

        Assert.Equal(2, outcome.Map.Pairs.Count);
        Assert.Equal(new Vector3(1, 0, 14), outcome.After.Positions[0].Round());
        Assert.NotNull(outcome.Conform);
    }

    [Fact]
    public void AModelSaidToBeAlignedKeepsItsShape()
    {
        // Only the skinning moves. Fitting a model that was already built for
        // this skeleton would shift something that was right.
        SourceModel source = Model(
            [Source("root", 0, Vector3.Zero), Source("g_l_hip", 0, new Vector3(0, 0, 10))],
            new Vector3(1, 0, 10), bone: 1);

        SkeletalMesh target = Target(
            Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 14)));

        RetargetOutcome outcome = RetargetRun.Run(
            source, target, new RetargetOptions { Shape = ShapeHandling.LeaveAlone });

        Assert.Equal(new Vector3(1, 0, 10), outcome.After.Positions[0].Round());
        Assert.Null(outcome.Conform);
        Assert.Contains(outcome.Log, line => line.Contains("left alone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AModelWithNoSkeletonIsBoundFromTheTargetSurfaceInstead()
    {
        // Asking to keep its own weights cannot be honoured when it has none,
        // so it takes the only path that can work — and says so.
        SourceModel source = Model([], Vector3.Zero, bone: 0);

        RetargetOutcome outcome = RetargetRun.Run(source, TargetWithGeometry(), new RetargetOptions());

        Assert.Contains(outcome.Log, line => line.Contains("no skeleton", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outcome.Log, line => line.Contains("surface", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoUnrelatedRigsAreRefusedRatherThanMangled()
    {
        SourceModel source = Model([Source("zzz_thing", 0, Vector3.Zero)], Vector3.Zero, bone: 0);

        RetargetException failure = Assert.Throws<RetargetException>(
            () => RetargetRun.Run(source, Target(Bone("g_pelvis", 0, Vector3.Zero)), new RetargetOptions()));

        Assert.Contains("unrelated", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningOffNameBindingBindsFromTheSurfaceInstead()
    {
        SourceModel source = Model([Source("root", 0, Vector3.Zero)], Vector3.Zero, bone: 0);

        RetargetOutcome outcome = RetargetRun.Run(
            source, TargetWithGeometry(), new RetargetOptions { KeepSourceWeights = false });

        Assert.Contains(outcome.Log, line => line.Contains("surface", StringComparison.OrdinalIgnoreCase));

        // The shape is left alone: nothing here knows how the two rigs relate,
        // so there is no basis on which to move a vertex.
        Assert.Null(outcome.Conform);
    }

    [Fact]
    public void ATargetWithNoGeometryCannotBeBoundFromAndSaysSo()
    {
        SourceModel source = Model([Source("root", 0, Vector3.Zero)], Vector3.Zero, bone: 0);

        RetargetException failure = Assert.Throws<RetargetException>(
            () => RetargetRun.Run(
                source, Target(Bone("root", 0, Vector3.Zero)),
                new RetargetOptions { KeepSourceWeights = false }));

        Assert.Contains("no geometry", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class VectorRounding
{
    /// <summary>Rounds a point, so arithmetic that is never exact can be compared.</summary>
    public static Vector3 Round(this Vector3 value) => new(
        MathF.Round(value.X, 3), MathF.Round(value.Y, 3), MathF.Round(value.Z, 3));
}
