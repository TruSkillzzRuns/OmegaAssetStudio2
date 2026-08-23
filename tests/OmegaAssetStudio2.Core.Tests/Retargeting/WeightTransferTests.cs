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

public sealed class WeightTransferTests
{
    private static MeshBone Bone(string name, int parent) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = Vector3.Zero,
    };

    private static SkeletalMeshLod Lod(params VertexInfluence[] influences) => new()
    {
        Sections = [],
        Indices = [],
        VertexCount = influences.Length,
        Layout = default,
        Positions = new Vector3[influences.Length],
        Normals = new Vector3[influences.Length],
        TexCoords = new Vector2[influences.Length],
        Influences = influences,
        Chunks = [],
    };

    private static VertexInfluence Skin(params (int Bone, float Weight)[] parts) => new()
    {
        Bones = parts.Select(p => p.Bone).ToList(),
        Weights = parts.Select(p => p.Weight).ToList(),
    };

    [Fact]
    public void MatchingSkeletonsMoveTheSkinningUntouched()
    {
        List<MeshBone> bones = [Bone("root", 0), Bone("g_pelvis", 0), Bone("g_l_hip", 1)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((1, 0.75f), (2, 0.25f))), bones, BoneMap.Build(bones, bones));

        Assert.Equal(1, result.Report.VerticesKept);
        Assert.Equal(0, result.Report.VerticesRerouted);
        VertexInfluence skin = result.Influences[0];
        int slot = skin.Bones.ToList().IndexOf(1);

        Assert.True(slot >= 0, "the pelvis is not among the bones this vertex follows.");
        Assert.Equal(0.75f, skin.Weights[slot], 4);
    }

    [Fact]
    public void WeightOnABoneTheTargetLacksGoesToItsNearestAncestor()
    {
        // A cape hangs off the spine. The target has no cape, so its weight
        // belongs on the spine — not dropped, which would leave that part of
        // the surface following nothing and collapse it toward the origin.
        List<MeshBone> source = [Bone("root", 0), Bone("g_spine01", 0), Bone("g_cape1", 1)];
        List<MeshBone> target = [Bone("root", 0), Bone("g_spine01", 0)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((2, 1.0f))), source, BoneMap.Build(source, target));

        VertexInfluence moved = result.Influences[0];

        Assert.Single(moved.Bones);
        Assert.Equal(1, moved.Bones[0]);              // the spine on the target
        Assert.Equal(1.0f, moved.Weights[0], 4);
        Assert.Equal(1, result.Report.VerticesRerouted);
        Assert.Contains("g_cape1", result.Report.ReroutedFrom.Keys);
    }

    [Fact]
    public void TwoBonesLeadingToOneAddUpRatherThanCompete()
    {
        // Both a cape bone and the spine resolve to the spine. Kept as two
        // slots they would each hold half the weight of what they should.
        List<MeshBone> source = [Bone("root", 0), Bone("g_spine01", 0), Bone("g_cape1", 1)];
        List<MeshBone> target = [Bone("root", 0), Bone("g_spine01", 0)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((1, 0.5f), (2, 0.5f))), source, BoneMap.Build(source, target));

        Assert.Single(result.Influences[0].Bones);
        Assert.Equal(1.0f, result.Influences[0].Weights[0], 4);
    }

    [Fact]
    public void WeightAlwaysAddsToOneAfterwards()
    {
        List<MeshBone> source = [Bone("root", 0), Bone("g_spine01", 0), Bone("g_cape1", 1), Bone("g_hair1", 1)];
        List<MeshBone> target = [Bone("root", 0), Bone("g_spine01", 0)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((1, 0.2f), (2, 0.3f), (3, 0.5f))), source, BoneMap.Build(source, target));

        Assert.Equal(1.0f, result.Influences[0].Weights.Sum(), 4);
    }

    [Fact]
    public void AVertexWithNowhereToGoIsLeftLooseRatherThanPinned()
    {
        // Pinning it to the root would stretch the vertex across the whole
        // model. Left empty, the caller can see it and say so.
        List<MeshBone> source = [Bone("g_orphan", 0)];
        List<MeshBone> target = [Bone("g_pelvis", 0)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((0, 1.0f))), source, BoneMap.Build(source, target));

        Assert.Empty(result.Influences[0].Bones);
        Assert.Equal(1, result.Report.VerticesDropped);
    }

    [Fact]
    public void ASkeletonThatLoopsDoesNotHang()
    {
        // A parent chain pointing back at itself must fail, not spin.
        List<MeshBone> source = [Bone("a", 1), Bone("b", 0)];
        List<MeshBone> target = [Bone("z", 0)];

        TransferResult result = WeightTransfer.Apply(
            Lod(Skin((0, 1.0f))), source, BoneMap.Build(source, target));

        Assert.Equal(1, result.Report.VerticesDropped);
    }
}

/// <summary>
/// Moves real characters onto one another's skeletons.
/// </summary>
public sealed class RealWeightTransferTests
{
    private readonly ITestOutputHelper _output;

    public RealWeightTransferTests(ITestOutputHelper output) => _output = output;

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
    public void EveryVertexStillFollowsExactlyOneBonesWorthOfMovement()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            List<SkeletalMesh> bodies = Bodies(client, 6);
            if (bodies.Count < 2) continue;

            long checkedVertices = 0;
            double worstClean = 1.0;
            string worstPair = string.Empty;
            int totalDropped = 0;

            for (int i = 1; i < bodies.Count; i++)
            {
                SkeletalMesh source = bodies[0];
                SkeletalMesh target = bodies[i];

                SkeletalMeshLod lod = source.HighestDetail!;
                BoneMap map = BoneMap.Build(source.Bones, target.Bones);

                TransferResult result = WeightTransfer.Apply(lod, source.Bones, map);

                Assert.Equal(lod.Influences.Count, result.Influences.Count);

                foreach (VertexInfluence influence in result.Influences)
                {
                    checkedVertices++;

                    if (influence.Count == 0) continue;   // reported as dropped

                    // Bones must exist on the skeleton being moved onto. A
                    // number left over from the old skeleton would read some
                    // other bone entirely.
                    foreach (int bone in influence.Bones)
                        Assert.InRange(bone, 0, target.Bones.Count - 1);

                    // No bone may appear twice, or the two entries fight.
                    Assert.Equal(influence.Bones.Count, influence.Bones.Distinct().Count());

                    Assert.Equal(1.0f, influence.Weights.Sum(), 3);
                }

                totalDropped += result.Report.VerticesDropped;

                if (result.Report.CleanRate < worstClean)
                {
                    worstClean = result.Report.CleanRate;
                    worstPair = $"{source.Name} → {target.Name}: {result.Report}";
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {checkedVertices:N0} vertices moved. Worst — {worstPair}");

            // Nothing may be left following nothing: every character shares the
            // same root, so there is always somewhere for weight to go.
            Assert.Equal(0, totalDropped);
        }
    }

    [Fact]
    public void MovingAModelOntoItsOwnSkeletonChangesNothing()
    {
        // The strongest check available without a renderer: the same skeleton
        // must be an exact identity. Any drift here is the transfer inventing
        // something rather than translating it.
        foreach (GameClient client in InstalledClients())
        {
            foreach (SkeletalMesh mesh in Bodies(client, 3))
            {
                SkeletalMeshLod lod = mesh.HighestDetail!;

                TransferResult result = WeightTransfer.Apply(
                    lod, mesh.Bones, BoneMap.Build(mesh.Bones, mesh.Bones));

                Assert.Equal(lod.Influences.Count, result.Report.VerticesKept);
                Assert.Equal(0, result.Report.VerticesRerouted);
                Assert.Equal(0, result.Report.VerticesDropped);

                for (int v = 0; v < lod.Influences.Count; v++)
                {
                    VertexInfluence before = lod.Influences[v];
                    VertexInfluence after = result.Influences[v];

                    Assert.Equal(before.Bones.OrderBy(b => b), after.Bones.OrderBy(b => b));
                }
            }

            return;   // one client is enough for an identity check
        }
    }
}
