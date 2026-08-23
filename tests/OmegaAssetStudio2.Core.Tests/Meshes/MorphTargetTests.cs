using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks the per-vertex displacements a power uses can be read, written back
/// unchanged, and renumbered onto a rewritten model.
/// </summary>
/// <remarks>
/// Everything here reads the game's own files and writes only in memory.
/// </remarks>
public sealed class RealMorphTargetTests
{
    private readonly ITestOutputHelper _output;

    public RealMorphTargetTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The first package in the install that carries displacements at all.
    /// </summary>
    /// <remarks>
    /// Found rather than named. Only some characters reshape themselves, and
    /// which ones is the game's business, not this test's.
    /// </remarks>
    private static Package? WithDisplacements()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (string path in Directory
                         .EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Package package;
                try { package = Package.Open(path); } catch (InvalidPackageException) { continue; }

                if (MorphTargetReader.ReadAll(package).Any(t => t.DeltaCount > 0)) return package;
            }
        }

        return null;
    }

    [Fact]
    public void TheyCanBeReadAndWrittenBackUnchanged()
    {
        if (WithDisplacements() is not { } package) { _output.WriteLine("No model here reshapes itself."); return; }

        IReadOnlyList<MorphTarget> targets = MorphTargetReader.ReadAll(package);

        Assert.NotEmpty(targets);

        foreach (MorphTarget target in targets)
        {
            byte[] before = package.GetExportData(target.ExportIndex).ToArray();
            byte[] after = MorphTargetReader.Replace(package, target, target.Levels);

            _output.WriteLine(
                $"    {target.Name,-24} {target.Levels.Count} level(s), " +
                $"{target.DeltaCount:N0} displacements, {before.Length:N0} bytes");

            // Read and written back with nothing changed must be the same bytes,
            // or the layout is not understood well enough to renumber safely.
            Assert.Equal(before.Length, after.Length);
            Assert.True(before.AsSpan().SequenceEqual(after),
                $"{target.Name}: writing it back unchanged altered it.");
        }

        _output.WriteLine($"{targets.Count} set(s) of displacements read and written back exactly.");
    }

    [Fact]
    public void TheyFollowTheModelWhenItIsRewritten()
    {
        if (WithDisplacements() is not { } package) { _output.WriteLine("No model here reshapes itself."); return; }

        int index = package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass).First();
        SkeletalMesh mesh = SkeletalMeshReader.TryRead(package, index)!;
        SkeletalMeshLod lod = mesh.HighestDetail!;

        var geometry = new MeshGeometry
        {
            Positions = lod.Positions,
            Normals = lod.Normals,
            TexCoords = lod.TexCoords,
            Influences = lod.Influences,
            Indices = lod.Indices,
            Sections = lod.Sections,
            TangentFrames = lod.TangentFrames,
        };

        MeshInstallPlan plan = MeshInstaller.Plan(package, index, mesh, geometry);

        _output.WriteLine(
            $"{mesh.Name}: {lod.Positions.Count:N0} vertices became {plan.VerticesAfter:N0}; " +
            $"{plan.Morphs.Count} set(s) of displacements renumbered.");

        foreach (MorphRemapReport report in plan.Morphs)
        {
            _output.WriteLine(
                $"    {report.Name,-24} {report.Before:N0} -> {report.After:N0}, " +
                $"{report.Lost:N0} lost, furthest reach {report.FurthestReach:0.###}");
        }

        Assert.NotEmpty(plan.Morphs);

        // Nothing may be lost when the model has not actually changed: every
        // vertex is still exactly where it was.
        Assert.All(plan.Morphs, r => Assert.Equal(0, r.Lost));

        // And every displacement must still name a vertex that exists.
        Package written = Package.Read(plan.Content, package.Path);

        SkeletalMeshLod after = SkeletalMeshReader.TryRead(written, index)!.HighestDetail!;

        foreach (MorphTarget target in MorphTargetReader.ReadAll(written))
        {
            foreach (MorphLevel level in target.Levels)
            {
                Assert.All(level.Deltas, d => Assert.InRange(d.Vertex, 0, after.Positions.Count - 1));
                Assert.Equal(after.Positions.Count, level.BaseVertexCount);

                // No vertex may be displaced twice. The game adds every
                // displacement in the list, so a vertex named twice moves twice
                // as far — which bursts a hand into spikes and turns a power
                // that inflates the model into one that explodes it.
                var seen = new HashSet<int>();

                foreach (MorphDelta delta in level.Deltas)
                {
                    Assert.True(seen.Add(delta.Vertex),
                        $"{target.Name}: vertex {delta.Vertex} is displaced more than once.");
                }
            }
        }
    }
}
