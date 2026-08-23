using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Retargeting;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

/// <summary>
/// Checks that a model is fitted from the pose its weights were authored
/// against, not from however its file happens to arrange the scene.
/// </summary>
/// <remarks>
/// This is the mistake that mangled a real model: a character fitted onto its
/// own skeleton, every bone matched by name, nothing rerouted — and every
/// vertex moved by roughly the height of the character. The bones were right;
/// the pose they were measured from was not.
/// </remarks>
public sealed class BindPoseTests
{
    private static ImportedBone Bone(string name, int parent, Vector3 chainPosition, Matrix4x4? bindPose) => new()
    {
        Name = name,
        ParentIndex = parent,
        Orientation = Quaternion.Identity,
        Position = chainPosition,
        BindPose = bindPose,
    };

    private static ImportedMesh Mesh(params ImportedBone[] bones) => new()
    {
        Positions = [new Vector3(1, 0, 0)],
        TexCoords = [Vector2.Zero],
        WedgePoints = [0],
        Indices = [],
        Materials = [],
        Bones = bones,
        Weights = [new[] { (1, 1f) }],
    };

    [Fact]
    public void TheRecordedBindPoseIsUsedInsteadOfTheNodeArrangement()
    {
        // The chain says the bone sits at 50; the bind pose says 10. The weights
        // mean the bind pose, and taking the chain would move the model by 40.
        ImportedMesh mesh = Mesh(
            Bone("root", 0, Vector3.Zero, Matrix4x4.Identity),
            Bone("g_l_hip", 0, new Vector3(0, 0, 50), Matrix4x4.CreateTranslation(0, 0, 10)));

        SourceModel model = SourceModelBuilder.Build(mesh);

        Assert.Equal(new Vector3(0, 0, 10), model.Pose.PositionOf(1));
    }

    [Fact]
    public void TheBoneChainIsUsedWhenTheFileRecordsNoBindPose()
    {
        // Not every format stores one. The chain is then all there is, and it
        // is the right answer for those files.
        ImportedMesh mesh = Mesh(
            Bone("root", 0, Vector3.Zero, null),
            Bone("g_l_hip", 0, new Vector3(0, 0, 50), null));

        SourceModel model = SourceModelBuilder.Build(mesh);

        Assert.Equal(new Vector3(0, 0, 50), model.Pose.PositionOf(1));
    }

    [Fact]
    public void AHalfRecordedSkeletonFallsBackRatherThanMixingTheTwo()
    {
        // Half from one source and half from the other would put those halves
        // of the model in different places.
        ImportedMesh mesh = Mesh(
            Bone("root", 0, Vector3.Zero, Matrix4x4.Identity),
            Bone("g_l_hip", 0, new Vector3(0, 0, 50), null));

        SourceModel model = SourceModelBuilder.Build(mesh);

        Assert.Equal(new Vector3(0, 0, 50), model.Pose.PositionOf(1));
    }

    [Fact]
    public void AModelFittedOntoTheSkeletonItWasBoundToDoesNotMove()
    {
        // The check that would have caught it. The bind pose and the target
        // skeleton agree, so there is nothing to move — whatever the file's own
        // node arrangement says.
        ImportedMesh mesh = Mesh(
            Bone("root", 0, Vector3.Zero, Matrix4x4.Identity),
            Bone("g_l_hip", 0, new Vector3(0, 0, 50), Matrix4x4.CreateTranslation(0, 0, 10)));

        SourceModel source = SourceModelBuilder.Build(mesh);

        var target = new SkeletalMesh
        {
            Name = "target",
            ObjectPath = "target",
            Bounds = default,
            Bones =
            [
                new MeshBone
                {
                    Name = "root", ParentIndex = 0, ChildCount = 0,
                    Orientation = Quaternion.Identity, Position = Vector3.Zero,
                },
                new MeshBone
                {
                    Name = "g_l_hip", ParentIndex = 0, ChildCount = 0,
                    Orientation = Quaternion.Identity, Position = new Vector3(0, 0, 10),
                },
            ],
            Lods = [],
            Materials = [],
        };

        RetargetOutcome outcome = RetargetRun.Run(source, target, new RetargetOptions { Shape = ShapeHandling.Decide });

        // Nothing to fit: the bind pose already agrees with the target, so the
        // shape is left exactly as it is. Fitting it anyway would turn the
        // model about every joint, because two rigs can hold a joint in the
        // same place while facing it differently.
        Assert.Null(outcome.Conform);
        Assert.Contains(outcome.Log, line => line.Contains("already sits", StringComparison.OrdinalIgnoreCase));

        for (int v = 0; v < outcome.Before.Positions.Count; v++)
            Assert.Equal(outcome.Before.Positions[v], outcome.After.Positions[v]);
    }
}
