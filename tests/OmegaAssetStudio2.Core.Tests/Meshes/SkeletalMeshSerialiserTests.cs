using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks that a whole model can be written into a package and read back.
/// </summary>
/// <remarks>
/// The decisive check is a round trip through the game's own format: whatever
/// is written must come back as the same model when read by the ordinary
/// reader, because that reader is standing in for the game. Everything here
/// works in memory; nothing is saved.
/// </remarks>
public sealed class RealSkeletalMeshSerialiserTests
{
    private readonly ITestOutputHelper _output;

    public RealSkeletalMeshSerialiserTests(ITestOutputHelper output) => _output = output;

    private static (Package Package, int Index, SkeletalMesh Mesh)? FindBody(GameClient client)
    {
        foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(20))
        {
            Package package;
            try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

            int bestIndex = -1;
            SkeletalMesh? best = null;

            foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                if (mesh?.HighestDetail is not { HasGeometry: true }) continue;

                if (best is null || mesh.Bones.Count > best.Bones.Count)
                {
                    best = mesh;
                    bestIndex = index;
                }
            }

            if (best is not null) return (package, bestIndex, best);
        }

        return null;
    }

    private static MeshGeometry From(SkeletalMeshLod lod) => new()
    {
        Positions = lod.Positions,
        Normals = lod.Normals,
        TexCoords = lod.TexCoords,
        Influences = lod.Influences,
        Indices = lod.Indices,
        Sections = lod.Sections,
        TangentFrames = lod.TangentFrames,
    };

    /// <summary>Writes a model into a package and reads it back as the game would.</summary>
    private static SkeletalMesh RoundTrip(Package package, int index, SkeletalMesh mesh, MeshGeometry geometry)
    {
        byte[] written = SkeletalMeshSerialiser.Replace(package, index, mesh, geometry);

        byte[] rebuilt = PackageRebuilder.Build(package, [new ExportPatch(index, written)]);

        Package reopened = Package.Read(rebuilt, package.Path);

        SkeletalMesh? read = SkeletalMeshReader.TryRead(reopened, index, why =>
            throw new Xunit.Sdk.XunitException($"the written model could not be read back: {why}"));

        Assert.NotNull(read);
        return read!;
    }

    [Fact]
    public void AModelWrittenBackUnchangedReadsAsTheSameModel()
    {
        // The check everything rests on. A model taken apart and put back
        // together must describe the same thing, or nothing written this way
        // can be trusted.
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;
            SkeletalMesh after = RoundTrip(package, index, mesh, From(lod));

            SkeletalMeshLod result = after.HighestDetail!;

            Assert.Equal(lod.Indices.Count, result.Indices.Count);
            Assert.Equal(lod.Sections.Count, result.Sections.Count);
            Assert.Equal(lod.Chunks.Count, result.Chunks.Count);
            Assert.Equal(mesh.Bones.Count, after.Bones.Count);
            Assert.Equal(mesh.Materials.Count, after.Materials.Count);

            // Vertices are renumbered on the way out, since each run owns its
            // own, so what must match is what the triangles draw rather than
            // what sits at any given index. Positions are written in full, so
            // corner for corner they come back exactly.
            float worst = 0f;

            for (int c = 0; c < lod.Indices.Count; c++)
            {
                worst = Math.Max(worst, Vector3.Distance(
                    lod.Positions[lod.Indices[c]], result.Positions[result.Indices[c]]));
            }

            Assert.True(worst < 0.001f, $"a corner came back {worst:0.####} from where it was written.");

            // And the runs must be arranged as the game's own are.
            for (int s = 0; s < result.Sections.Count; s++)
                Assert.Equal(s, result.Sections[s].ChunkIndex);

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} — {result.Positions.Count:N0} vertices and " +
                $"{result.TriangleCount:N0} triangles survived a round trip through the game's format.");

            return;
        }

        _output.WriteLine("No installs present; nothing probed.");
    }

    [Fact]
    public void TheSkinningSurvivesTheRoundTrip()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;
            SkeletalMeshLod result = RoundTrip(package, index, mesh, From(lod)).HighestDetail!;

            int sameBones = 0;

            // Compared corner by corner, because vertices are renumbered.
            for (int c = 0; c < lod.Indices.Count; c++)
            {
                VertexInfluence before = lod.Influences[lod.Indices[c]];
                VertexInfluence after = result.Influences[result.Indices[c]];

                // Every bone must exist, and the weights must still add to one.
                Assert.NotEmpty(after.Bones);
                Assert.Equal(1f, after.Weights.Sum(), 2);

                foreach (int bone in after.Bones)
                    Assert.InRange(bone, 0, mesh.Bones.Count - 1);

                if (before.Bones.OrderBy(b => b).SequenceEqual(after.Bones.OrderBy(b => b))) sameBones++;
            }

            double rate = sameBones / (double)lod.Indices.Count;

            _output.WriteLine(
                $"{client.DisplayName}: {rate:P1} of vertices follow exactly the same bones afterwards.");

            // A vertex weighted so weakly to a bone that a byte rounds it to
            // nothing legitimately loses it, so this is not quite everything.
            Assert.True(rate > 0.98, $"only {rate:P1} of vertices kept their bones.");

            return;
        }
    }

    [Fact]
    public void ADifferentModelCanBePutIn()
    {
        // The point of the whole thing: the geometry that goes in need not be
        // the geometry that came out.
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            // Half the triangles, and every vertex moved.
            int triangles = lod.TriangleCount / 2;

            var geometry = new MeshGeometry
            {
                Positions = lod.Positions.Select(p => p * 0.5f).ToList(),
                Normals = lod.Normals,
                TexCoords = lod.TexCoords,
                Influences = lod.Influences,
                Indices = lod.Indices.Take(triangles * 3).ToList(),
                Sections = [],
            };

            SkeletalMesh after = RoundTrip(package, index, mesh, geometry);
            SkeletalMeshLod result = after.HighestDetail!;

            Assert.Equal(triangles, result.TriangleCount);
            Assert.Single(result.Sections);

            // Only the vertices the kept triangles draw are written, so the
            // count legitimately falls; what must hold is that every corner is
            // where it was asked to be.
            Assert.True(result.Positions.Count <= lod.Positions.Count);

            float worst = 0f;

            for (int c = 0; c < geometry.Indices.Count; c++)
            {
                worst = Math.Max(worst, Vector3.Distance(
                    geometry.Positions[geometry.Indices[c]], result.Positions[result.Indices[c]]));
            }

            Assert.True(worst < 0.001f, $"a corner came back {worst:0.####} from where it was written.");

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} replaced with {result.TriangleCount:N0} triangles " +
                "and read back correctly.");

            return;
        }
    }

    /// <summary>
    /// A model written back unchanged must have the shape it had.
    /// </summary>
    /// <remarks>
    /// Every one of the game's own models maps its sections to its runs of
    /// vertices one for one and holds each run's singly-bound vertices before
    /// its multiply-bound ones. Writing one run holding everything instead
    /// produced a file the game loaded and then drew as splinters on the floor,
    /// so the shape is checked here against the model that was replaced.
    /// </remarks>
    [Fact]
    public void AModelWrittenBackUnchangedKeepsTheShapeItHad()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;
            SkeletalMeshLod result = RoundTrip(package, index, mesh, From(lod)).HighestDetail!;

            Assert.Equal(lod.Sections.Count, result.Sections.Count);
            Assert.Equal(lod.Chunks.Count, result.Chunks.Count);

            for (int c = 0; c < lod.Chunks.Count; c++)
            {
                MeshChunk before = lod.Chunks[c];
                MeshChunk after = result.Chunks[c];

                Assert.Equal(before.BaseVertexIndex, after.BaseVertexIndex);
                Assert.Equal(before.RigidVertexCount, after.RigidVertexCount);
                Assert.Equal(before.SoftVertexCount, after.SoftVertexCount);
                Assert.Equal(before.BoneMap.Count, after.BoneMap.Count);

                Assert.True(after.BoneMap.Count <= MeshLayoutPlanner.MaxBonesPerChunk);
            }

            for (int s = 0; s < lod.Sections.Count; s++)
            {
                Assert.Equal(lod.Sections[s].ChunkIndex, result.Sections[s].ChunkIndex);
                Assert.Equal(lod.Sections[s].BaseIndex, result.Sections[s].BaseIndex);
                Assert.Equal(lod.Sections[s].TriangleCount, result.Sections[s].TriangleCount);
                Assert.Equal(lod.Sections[s].MaterialIndex, result.Sections[s].MaterialIndex);
            }

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} kept its {result.Sections.Count} section(s) and " +
                $"{result.Chunks.Count} run(s), " +
                string.Join(", ", result.Chunks.Select(c =>
                    $"{c.RigidVertexCount:N0} rigid + {c.SoftVertexCount:N0} soft over {c.BoneMap.Count} bones")) +
                ".");

            return;
        }
    }

    /// <summary>
    /// A model keeps as many levels of detail as it had.
    /// </summary>
    /// <remarks>
    /// Its own settings, copied across untouched, list one per level. Writing
    /// fewer levels than the settings describe leaves the game reading a level
    /// that is not there.
    /// </remarks>
    [Fact]
    public void AModelKeepsAsManyLevelsOfDetailAsItHad()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMesh after = RoundTrip(package, index, mesh, From(mesh.HighestDetail!));

            Assert.Equal(mesh.Lods.Count, after.Lods.Count);

            // Every level draws the same model, since none of them are
            // simplified — so each must hold the geometry that was put in.
            foreach (SkeletalMeshLod level in after.Lods)
                Assert.Equal(mesh.HighestDetail!.TriangleCount, level.TriangleCount);

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} kept all {after.Lods.Count} level(s) of detail, " +
                $"each with {after.HighestDetail!.TriangleCount:N0} triangles.");

            return;
        }
    }

    /// <summary>
    /// A model needing more bones than one run may address is split into
    /// several, rather than refused.
    /// </summary>
    /// <remarks>
    /// A run addresses its bones with a single byte through its own list, and
    /// the game's own models never put more than 75 in one. A costume drawing
    /// on more than that across a single section — one real model needed 79 —
    /// has to be divided, and each piece drawn by its own section.
    /// </remarks>
    [Fact]
    public void AModelNeedingTooManyBonesForOneRunIsSplit()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            // One section covering everything, so every bone the model uses has
            // to fit in a single run — which for a real character it cannot.
            var geometry = From(lod) with { Sections = [] };

            int bones = lod.Influences.SelectMany(i => i.Bones).Distinct().Count();

            MeshLayoutPlan plan = MeshLayoutPlanner.Build(geometry);

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} draws on {bones} bones across one section, " +
                $"laid out as {plan.Chunks.Count} run(s) of at most " +
                $"{plan.Chunks.Max(c => c.BoneMap.Count)} bones.");

            Assert.All(plan.Chunks, c => Assert.True(
                c.BoneMap.Count <= MeshLayoutPlanner.MaxBonesPerChunk,
                $"a run draws on {c.BoneMap.Count} bones."));

            // Every triangle has to survive the division, and each run must be
            // drawn by its own section.
            Assert.Equal(geometry.TriangleCount, plan.Indices.Count / 3);
            Assert.Equal(plan.Chunks.Count, plan.Sections.Count);

            for (int s = 0; s < plan.Sections.Count; s++)
                Assert.Equal(s, plan.Sections[s].ChunkIndex);

            // And it must still read back as the same model.
            SkeletalMesh after = RoundTrip(package, index, mesh, geometry);
            SkeletalMeshLod result = after.HighestDetail!;

            Assert.Equal(geometry.TriangleCount, result.TriangleCount);

            float worst = 0f;

            for (int c = 0; c < geometry.Indices.Count; c++)
            {
                worst = Math.Max(worst, Vector3.Distance(
                    geometry.Positions[geometry.Indices[c]], result.Positions[result.Indices[c]]));
            }

            Assert.True(worst < 0.001f, $"a corner came back {worst:0.####} from where it was written.");

            return;
        }
    }

    [Fact]
    public void EverythingAroundTheGeometryIsUntouched()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMesh after = RoundTrip(package, index, mesh, From(mesh.HighestDetail!));

            Assert.Equal(mesh.Name, after.Name);
            Assert.Equal(mesh.Bones.Select(b => b.Name), after.Bones.Select(b => b.Name));
            Assert.Equal(mesh.Bones.Select(b => b.ParentIndex), after.Bones.Select(b => b.ParentIndex));
            Assert.Equal(mesh.Bounds.Radius, after.Bounds.Radius, 3);
            Assert.Equal(mesh.Materials.Count, after.Materials.Count);

            return;
        }
    }

    [Fact]
    public void AModelDrawingOnTooManyBonesAtOnceIsRefused()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;
            if (mesh.Bones.Count <= 256) { _output.WriteLine("This skeleton is too small to test the limit."); return; }

            SkeletalMeshLod lod = mesh.HighestDetail!;

            // One vertex per bone, which drags every bone into one run.
            var influences = Enumerable.Range(0, lod.Positions.Count)
                .Select(i => new VertexInfluence { Bones = [i % mesh.Bones.Count], Weights = [1f] })
                .ToList();

            MeshWriteException failure = Assert.Throws<MeshWriteException>(
                () => SkeletalMeshSerialiser.Replace(package, index, mesh, From(lod) with { Influences = influences }));

            Assert.Contains("at most", failure.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
    }
}
