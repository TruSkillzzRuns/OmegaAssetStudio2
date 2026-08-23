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

public sealed class SkeletonPoseTests
{
    private static MeshBone Bone(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = position,
    };

    [Fact]
    public void ABoneSitsWhereItsChainPutsIt()
    {
        // Each bone is stored relative to its parent, so the third sits at the
        // sum of the three offsets, not at its own.
        List<MeshBone> bones =
        [
            Bone("root", 0, Vector3.Zero),
            Bone("a", 0, new Vector3(0, 0, 10)),
            Bone("b", 1, new Vector3(0, 0, 5)),
        ];

        SkeletonPose pose = SkeletonPose.Rest(bones);

        Assert.Equal(new Vector3(0, 0, 15), pose.PositionOf(2));
    }

    [Fact]
    public void TheWayBackUndoesTheWayThere()
    {
        List<MeshBone> bones = [Bone("root", 0, Vector3.Zero), Bone("a", 0, new Vector3(3, 4, 5))];

        SkeletonPose pose = SkeletonPose.Rest(bones);

        Matrix4x4 both = pose.BoneToModel[1] * pose.ModelToBone[1];

        Assert.True(Vector3.Distance(Vector3.Zero, Vector3.Transform(Vector3.Zero, both)) < 0.001f,
            "going into a bone's space and back did not return the same point.");
    }
}

public sealed class MeshConformTests
{
    /// <summary>Compares two points, allowing for arithmetic that is never exact.</summary>
    private static void AssertAt(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    private static MeshBone Bone(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = position,
    };

    private static SkeletalMeshLod Lod(Vector3[] positions, VertexInfluence[] influences) => new()
    {
        Sections = [],
        Indices = [],
        VertexCount = positions.Length,
        Layout = default,
        Positions = positions,
        Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
        TexCoords = new Vector2[positions.Length],
        Influences = influences,
        Chunks = [],
    };

    private static VertexInfluence Skin(int bone) =>
        new() { Bones = [bone], Weights = [1f] };

    [Fact]
    public void AVertexFollowsItsBoneToWhereTheNewSkeletonPutsIt()
    {
        // The matching bone sits four units further along, so a vertex bound to
        // it must move exactly four units.
        List<MeshBone> source = [Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 10))];
        List<MeshBone> target = [Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 14))];

        var lod = Lod([new Vector3(1, 0, 10)], [Skin(1)]);

        ConformResult result = MeshConform.Apply(
            lod, lod.Influences,
            SkeletonPose.Rest(source), SkeletonPose.Rest(target),
            BoneMap.Build(source, target));

        AssertAt(new Vector3(1, 0, 14), result.Positions[0]);
        Assert.Equal(4f, result.LargestMove, 3);
    }

    [Fact]
    public void TheSameSkeletonLeavesTheModelExactlyWhereItWas()
    {
        List<MeshBone> bones = [Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 10))];

        var lod = Lod([new Vector3(1, 2, 3), new Vector3(-4, 5, 6)], [Skin(1), Skin(0)]);

        ConformResult result = MeshConform.Apply(
            lod, lod.Influences,
            SkeletonPose.Rest(bones), SkeletonPose.Rest(bones),
            BoneMap.Build(bones, bones));

        AssertAt(lod.Positions[0], result.Positions[0]);
        AssertAt(lod.Positions[1], result.Positions[1]);
        Assert.Equal(0f, result.LargestMove, 4);
    }

    [Fact]
    public void AVertexNothingHoldsStaysWhereItIs()
    {
        // Dragging it to the origin would streak the surface across the model.
        List<MeshBone> bones = [Bone("root", 0, Vector3.Zero)];

        var lod = Lod([new Vector3(7, 8, 9)], [new VertexInfluence { Bones = [], Weights = [] }]);

        ConformResult result = MeshConform.Apply(
            lod, lod.Influences,
            SkeletonPose.Rest(bones), SkeletonPose.Rest(bones),
            BoneMap.Build(bones, bones));

        AssertAt(new Vector3(7, 8, 9), result.Positions[0]);
    }
}

/// <summary>
/// Conforms real characters onto one another's skeletons.
/// </summary>
public sealed class RealMeshConformTests
{
    private readonly ITestOutputHelper _output;

    public RealMeshConformTests(ITestOutputHelper output) => _output = output;

    private static List<GameClient> InstalledClients()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        string[] roots = !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return roots.Where(Directory.Exists)
                    .Select(r => GameClientLocator.FromRoot(r, new DirectoryInfo(r).Name))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();
    }

    private static List<SkeletalMesh> Bodies(GameClient client, int count)
    {
        var meshes = new List<SkeletalMesh>();

        foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero))
        {
            if (meshes.Count >= count) break;

            Package package;
            try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

            SkeletalMesh? body = null;

            foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                if (mesh?.HighestDetail is not { HasGeometry: true }) continue;

                if (body is null || mesh.Bones.Count > body.Bones.Count) body = mesh;
            }

            if (body is not null) meshes.Add(body);
        }

        return meshes;
    }

    [Fact]
    public void AModelOnItsOwnSkeletonDoesNotMoveAtAll()
    {
        // The check that proves the whole chain is a translation rather than an
        // invention: rebound and conformed onto the skeleton it already has,
        // every vertex must land exactly where it started.
        foreach (GameClient client in InstalledClients())
        {
            foreach (SkeletalMesh mesh in Bodies(client, 4))
            {
                SkeletalMeshLod lod = mesh.HighestDetail!;
                BoneMap map = BoneMap.Build(mesh.Bones, mesh.Bones);
                SkeletonPose pose = SkeletonPose.Rest(mesh.Bones);

                TransferResult rebound = WeightTransfer.Apply(lod, mesh.Bones, map);
                ConformResult conformed = MeshConform.Apply(lod, rebound.Influences, pose, pose, map);

                // The model's own size sets what counts as "did not move": a
                // fixed tolerance would be strict on a small model and loose on
                // a large one.
                float tolerance = Math.Max(0.01f, mesh.Bounds.Radius * 0.0005f);

                Assert.True(conformed.LargestMove < tolerance,
                    $"{mesh.Name}: a vertex moved {conformed.LargestMove:0.####} " +
                    $"conforming onto its own skeleton (allowed {tolerance:0.####}).");
            }

            _output.WriteLine($"{client.DisplayName}: models conform onto themselves without moving.");
            return;
        }
    }

    [Fact]
    public void AConformedModelTakesTheShapeOfTheSkeletonItMovedTo()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            List<SkeletalMesh> bodies = Bodies(client, 5);
            if (bodies.Count < 2) continue;

            for (int i = 1; i < bodies.Count; i++)
            {
                SkeletalMesh source = bodies[0];
                SkeletalMesh target = bodies[i];

                SkeletalMeshLod lod = source.HighestDetail!;
                BoneMap map = BoneMap.Build(source.Bones, target.Bones);

                TransferResult rebound = WeightTransfer.Apply(lod, source.Bones, map);

                ConformResult conformed = MeshConform.Apply(
                    lod, rebound.Influences,
                    SkeletonPose.Rest(source.Bones), SkeletonPose.Rest(target.Bones), map);

                Assert.Equal(lod.Positions.Count, conformed.Positions.Count);

                // Nothing may come out as nonsense: one bad matrix produces
                // coordinates that are not numbers, and they spread.
                foreach (Vector3 position in conformed.Positions)
                {
                    Assert.False(float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z),
                        $"{source.Name} → {target.Name}: a vertex is not a number.");
                    Assert.False(float.IsInfinity(position.X) || float.IsInfinity(position.Y) ||
                                 float.IsInfinity(position.Z),
                        $"{source.Name} → {target.Name}: a vertex ran off to infinity.");
                }

                // The result must be about the size of the character it moved
                // onto. A retarget that lands far outside those bounds has
                // stretched the model rather than fitted it.
                float radius = 0f;
                var centre = new Vector3(target.Bounds.OriginX, target.Bounds.OriginY, target.Bounds.OriginZ);

                foreach (Vector3 position in conformed.Positions)
                    radius = Math.Max(radius, Vector3.Distance(centre, position));

                _output.WriteLine(
                    $"{client.DisplayName}: {source.Name} → {target.Name} — " +
                    $"moved {conformed.AverageMove:0.##} on average, {conformed.LargestMove:0.##} at most; " +
                    $"reaches {radius:0.#} against the target's {target.Bounds.Radius:0.#}");

                Assert.True(radius < target.Bounds.Radius * 3f,
                    $"{source.Name} → {target.Name}: the result reaches {radius:0.#}, " +
                    $"far outside the target's own {target.Bounds.Radius:0.#}.");
            }
        }
    }
}
