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

public sealed class BoneNameTests
{
    [Theory]
    [InlineData("Bip01_L_UpperArm", BoneSide.Left)]
    [InlineData("upperarm_r", BoneSide.Right)]
    [InlineData("LeftHand", BoneSide.Left)]
    [InlineData("mixamorig:RightFoot", BoneSide.Right)]
    [InlineData("Spine1", BoneSide.Unknown)]
    [InlineData("Pelvis", BoneSide.Unknown)]
    public void SideIsReadFromTheName(string name, BoneSide expected)
        => Assert.Equal(expected, BoneNames.SideOf(name));

    // Every name here is one the game actually ships, read off 115 real
    // character skeletons.
    [Theory]
    [InlineData("g_pelvis", BoneRegion.Pelvis)]
    [InlineData("g_spine01", BoneRegion.Spine)]
    [InlineData("g_neck", BoneRegion.Neck)]
    [InlineData("g_head", BoneRegion.Head)]
    [InlineData("g_jaw", BoneRegion.Face)]
    [InlineData("g_topeyelid", BoneRegion.Face)]
    [InlineData("g_l_eye", BoneRegion.Face)]
    [InlineData("g_l_clavical", BoneRegion.Clavicle)]
    [InlineData("g_l_shoulder", BoneRegion.Shoulder)]
    [InlineData("g_l_elbow", BoneRegion.Elbow)]
    [InlineData("g_l_forarm", BoneRegion.Elbow)]
    [InlineData("g_l_wrist", BoneRegion.Wrist)]
    [InlineData("g_l_palm", BoneRegion.Hand)]
    [InlineData("g_l_birdy2", BoneRegion.Finger)]
    [InlineData("g_l_thumb1", BoneRegion.Finger)]
    [InlineData("g_l_hip", BoneRegion.Hip)]
    [InlineData("g_l_knee", BoneRegion.Knee)]
    [InlineData("g_l_ankle", BoneRegion.Ankle)]
    [InlineData("g_l_ball", BoneRegion.Ball)]
    [InlineData("g_l_biceptwist", BoneRegion.Twist)]
    [InlineData("g_l_thightwist", BoneRegion.Twist)]
    [InlineData("g_armikbase", BoneRegion.Control)]
    [InlineData("g_l_legikeffector", BoneRegion.Control)]
    [InlineData("g_throwable_attach", BoneRegion.Attachment)]
    [InlineData("root", BoneRegion.Root)]
    public void RegionIsReadFromTheName(string name, BoneRegion expected)
        => Assert.Equal(expected, BoneNames.RegionOf(BoneNames.Normalise(name)));

    [Fact]
    public void ThePelvisAndTheTopOfTheLegAreNotConfused()
    {
        // This game calls the top of the leg the hip and keeps the pelvis as a
        // separate bone above it. Reading "hip" as the pelvis, which is how the
        // word is normally used, binds a leg to the waist.
        Assert.Equal(BoneRegion.Pelvis, BoneNames.RegionOf(BoneNames.Normalise("g_pelvis")));
        Assert.Equal(BoneRegion.Hip, BoneNames.RegionOf(BoneNames.Normalise("g_l_hip")));
        Assert.NotEqual(BoneNames.Describe("g_pelvis"), BoneNames.Describe("g_l_hip"));
    }

    [Fact]
    public void EachFingerStaysItsOwnFinger()
    {
        // The game's five finger words. Reduced alike, the fingers of one hand
        // pair with one another at random.
        string[] fingers = ["g_l_thumb1", "g_l_index1", "g_l_birdy1", "g_l_ring1", "g_l_pinky1"];

        Assert.Equal(fingers.Length, fingers.Select(BoneNames.Describe).Distinct().Count());
    }

    [Fact]
    public void AJointAndItsOffsetAreNotTheSameBone()
    {
        // The game ships an offset bone beside many joints. Reduced alike, a
        // joint could be bound to its neighbour's offset.
        Assert.NotEqual(BoneNames.Describe("g_spine01"), BoneNames.Describe("g_spine01_offset"));
        Assert.NotEqual(BoneNames.Describe("g_l_hip"), BoneNames.Describe("g_l_hip_offset"));
    }

    [Fact]
    public void TheSameJointSpelledDifferentlyDescribesTheSame()
    {
        Assert.Equal(BoneNames.Describe("g_l_forarm"), BoneNames.Describe("Bip01_L_Forearm"));
        Assert.Equal(BoneNames.Describe("g_r_palm"), BoneNames.Describe("RightHand"));
        Assert.Equal(BoneNames.Describe("g_spine01"), BoneNames.Describe("Bip01_Spine1"));
    }

    [Fact]
    public void DifferentJointsDoNotDescribeTheSame()
    {
        Assert.NotEqual(BoneNames.Describe("g_l_shoulder"), BoneNames.Describe("g_r_shoulder"));
        Assert.NotEqual(BoneNames.Describe("g_l_shoulder"), BoneNames.Describe("g_l_elbow"));
        Assert.NotEqual(BoneNames.Describe("g_spine01"), BoneNames.Describe("g_spine02"));
    }
}

public sealed class BoneMapTests
{
    private static MeshBone Bone(string name, int parent = 0) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = Vector3.Zero,
    };

    [Fact]
    public void IdenticalSkeletonsMatchCompletely()
    {
        List<MeshBone> bones = [Bone("Root"), Bone("Bip01_Spine1"), Bone("Bip01_L_Hand")];

        BoneMap map = BoneMap.Build(bones, bones);

        Assert.Equal(3, map.Pairs.Count);
        Assert.Empty(map.UnmatchedSource);
        Assert.All(map.Pairs, p => Assert.Equal(MatchQuality.Exact, p.Quality));
        Assert.Equal(1.0, map.Coverage);
    }

    [Fact]
    public void DifferentSpellingsOfTheSameSkeletonStillMatch()
    {
        List<MeshBone> source = [Bone("Bip01_Pelvis"), Bone("Bip01_L_UpperArm"), Bone("Bip01_R_Hand")];
        List<MeshBone> target = [Bone("pelvis"), Bone("upperarm_l"), Bone("RightHand")];

        BoneMap map = BoneMap.Build(source, target);

        Assert.Equal(3, map.Pairs.Count);
        Assert.Empty(map.UnmatchedSource);
    }

    [Fact]
    public void ABoneWithNoCounterpartIsReportedRatherThanForced()
    {
        // The wrong answer here is a cape bone quietly bound to a finger.
        List<MeshBone> source = [Bone("Bip01_Pelvis"), Bone("Cape_03")];
        List<MeshBone> target = [Bone("pelvis"), Bone("Bip01_L_Hand")];

        BoneMap map = BoneMap.Build(source, target);

        Assert.Single(map.Pairs);
        Assert.Single(map.UnmatchedSource);
        Assert.Equal(1, map.UnmatchedSource[0]);
        Assert.Single(map.UnusedTarget);
    }

    [Fact]
    public void AnExactMatchIsNotRobbedByALooserOne()
    {
        // Both source bones describe a left hand. The one spelled exactly like
        // the target must take it, not whichever is considered first.
        List<MeshBone> source = [Bone("LeftHand"), Bone("Bip01_L_Hand")];
        List<MeshBone> target = [Bone("Bip01_L_Hand")];

        BoneMap map = BoneMap.Build(source, target);

        Assert.Single(map.Pairs);
        Assert.Equal("Bip01_L_Hand", map.Pairs[0].SourceName);
        Assert.Equal(MatchQuality.Exact, map.Pairs[0].Quality);
    }

    [Fact]
    public void AChoiceMadeByHandOutranksEveryRule()
    {
        List<MeshBone> source = [Bone("Bip01_L_Hand")];
        List<MeshBone> target = [Bone("Bip01_L_Hand"), Bone("Weapon_Attach")];

        BoneMap map = BoneMap.Build(source, target,
            new Dictionary<string, string> { ["Bip01_L_Hand"] = "Weapon_Attach" });

        Assert.Single(map.Pairs);
        Assert.Equal("Weapon_Attach", map.Pairs[0].TargetName);
        Assert.Equal(MatchQuality.Chosen, map.Pairs[0].Quality);
    }

    [Fact]
    public void NoTargetBoneIsUsedTwice()
    {
        List<MeshBone> source = [Bone("Bip01_L_Hand"), Bone("LeftHand"), Bone("l_hand")];
        List<MeshBone> target = [Bone("Bip01_L_Hand")];

        BoneMap map = BoneMap.Build(source, target);

        Assert.Single(map.Pairs);
        Assert.Equal(2, map.UnmatchedSource.Count);
    }
}

/// <summary>
/// Matches real skeletons from the game against each other.
/// </summary>
public sealed class RealBoneMapTests
{
    private readonly ITestOutputHelper _output;

    public RealBoneMapTests(ITestOutputHelper output) => _output = output;

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

    private static int IndexOf(IReadOnlyList<MeshBone> bones, string name)
    {
        for (int i = 0; i < bones.Count; i++)
            if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<SkeletalMesh> HeroMeshes(GameClient client, int count)
    {
        var meshes = new List<SkeletalMesh>();

        foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero))
        {
            if (meshes.Count >= count) break;

            Package package;
            try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

            // The character's body, not the first model in the package. A
            // package also holds their weapons and props, and those carry a
            // skeleton of their own — a whip has a handful of bones that stand
            // for nothing on a person.
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
    public void CharactersMatchOneAnotherAlmostCompletely()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            List<SkeletalMesh> meshes = HeroMeshes(client, 8);
            if (meshes.Count < 2) continue;

            // The joints every person has. Hair, capes and weapons differ from
            // character to character and have no counterpart by design, so
            // measuring against every bone measures the costume, not the rig.
            BoneRegion[] core =
            [
                BoneRegion.Pelvis, BoneRegion.Spine, BoneRegion.Neck, BoneRegion.Head,
                BoneRegion.Clavicle, BoneRegion.Shoulder, BoneRegion.Elbow,
                BoneRegion.Wrist, BoneRegion.Hand, BoneRegion.Finger,
                BoneRegion.Hip, BoneRegion.Knee, BoneRegion.Ankle, BoneRegion.Ball,
            ];

            double worst = 1.0;
            string worstPair = string.Empty;

            for (int i = 1; i < meshes.Count; i++)
            {
                BoneMap map = BoneMap.Build(meshes[0].Bones, meshes[i].Bones);

                int coreTotal = 0, coreMatched = 0;

                for (int b = 0; b < meshes[0].Bones.Count; b++)
                {
                    BoneRegion region = BoneNames.RegionOf(BoneNames.Normalise(meshes[0].Bones[b].Name));
                    if (!core.Contains(region)) continue;

                    coreTotal++;
                    if (map.For(b) is not null) coreMatched++;
                }

                double rate = coreTotal == 0 ? 1.0 : coreMatched / (double)coreTotal;

                if (rate < worst)
                {
                    worst = rate;
                    worstPair = $"{meshes[0].Name} → {meshes[i].Name} " +
                                $"({coreMatched} of {coreTotal} core joints; " +
                                $"{map.Pairs.Count} of {map.Pairs.Count + map.UnmatchedSource.Count} bones overall)";
                }

                // A bone must never be paired with one on the other side of the
                // body. That is the mistake that mirrors an arm and is not
                // obvious until the model is posed.
                foreach (BonePair pair in map.Pairs)
                {
                    BoneSide from = BoneNames.SideOf(pair.SourceName);
                    BoneSide to = BoneNames.SideOf(pair.TargetName);

                    if (from == BoneSide.Unknown || to == BoneSide.Unknown) continue;

                    Assert.True(from == to,
                        $"{client.DisplayName}: {pair.SourceName} was paired with {pair.TargetName}, " +
                        "which is on the other side of the body.");
                }

                // Nor with a different part of the body: a hand bound to a foot
                // is the failure that ruins a retarget, and it would not be
                // obvious until the model moved.
                foreach (BonePair pair in map.Pairs)
                {
                    BoneRegion from = BoneNames.RegionOf(BoneNames.Normalise(pair.SourceName));
                    BoneRegion to = BoneNames.RegionOf(BoneNames.Normalise(pair.TargetName));

                    if (from == BoneRegion.Unknown || to == BoneRegion.Unknown) continue;

                    Assert.True(from == to,
                        $"{client.DisplayName}: {pair.SourceName} ({from}) was paired with {pair.TargetName} ({to}).");
                }

                // Every joint a person cannot be without must find a home. This
                // is asserted instead of a percentage: characters genuinely
                // differ in what else they carry — one has breasts, another an
                // extra knee helper — and counting those as failures measures
                // the costume rather than the rig.
                foreach (string essential in new[]
                         {
                             "g_pelvis", "g_spine01", "g_spine02", "g_spine03", "g_neck", "g_head",
                             "g_l_clavical", "g_r_clavical", "g_l_shoulder", "g_r_shoulder",
                             "g_l_elbow", "g_r_elbow", "g_l_forarm", "g_r_forarm",
                             "g_l_wrist", "g_r_wrist", "g_l_palm", "g_r_palm",
                             "g_l_hip", "g_r_hip", "g_l_knee", "g_r_knee",
                             "g_l_ankle", "g_r_ankle", "g_l_ball", "g_r_ball",
                         })
                {
                    int b = IndexOf(meshes[0].Bones, essential);
                                        Assert.True(b >= 0,
                        $"{meshes[0].Name} has no bone called {essential}. The list is taken from " +
                        "bones present on every character read, so a miss means the list is wrong.");

                    Assert.True(map.For(b) is not null,
                        $"{client.DisplayName}: {essential} found nothing to stand for it on {meshes[i].Name}.");
                }
            }

            _output.WriteLine($"{client.DisplayName}: worst core-joint match {worst:P1} — {worstPair}");
        }
    }
}
