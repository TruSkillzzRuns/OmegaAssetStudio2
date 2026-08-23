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

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks that a model taken out of the game and brought straight back is the
/// same model.
/// </summary>
/// <remarks>
/// This is the check that matters for an export: whatever it writes has to be
/// something the reader turns back into what it started with. Reading and
/// writing exchange the same two axes, turn the same texture coordinate round,
/// and wind the triangles the same way — so a round trip that does not come
/// back means one of those pairs does not cancel.
/// <para>
/// Everything is written to a temporary file and deleted; the game folder is
/// never touched.
/// </para>
/// </remarks>
public sealed class RealFbxExporterTests
{
    private readonly ITestOutputHelper _output;

    public RealFbxExporterTests(ITestOutputHelper output) => _output = output;

    /// <summary>The first character model the install offers.</summary>
    private static (SkeletalMesh Mesh, SkeletalMeshLod Lod)? SomeModel()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(10))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);

                    if (mesh?.HighestDetail is { HasGeometry: true } lod) return (mesh, lod);
                }
            }
        }

        return null;
    }

    [Fact]
    public void AModelTakenOutAndBroughtBackIsTheSameModel()
    {
        if (SomeModel() is not var (mesh, lod)) { _output.WriteLine("No model to work from."); return; }

        string path = Path.Combine(Path.GetTempPath(), $"oas2_export_{Guid.NewGuid():N}.fbx");

        try
        {
            FbxExporter.Write(path, mesh, lod);

            Assert.True(File.Exists(path), "nothing was written.");

            ImportedMesh back = MeshFile.Read(path);
            SourceModel rebuilt = SourceModelBuilder.Build(back);

            SkeletalMeshLod result = rebuilt.Geometry;

            _output.WriteLine(
                $"{mesh.Name}: {lod.Positions.Count:N0} vertices and {lod.TriangleCount:N0} triangles " +
                $"went out as {new FileInfo(path).Length:N0} bytes and came back as " +
                $"{result.Positions.Count:N0} vertices and {result.TriangleCount:N0} triangles, " +
                $"{rebuilt.Bones.Count:N0} bones.");

            Assert.Equal(lod.TriangleCount, result.TriangleCount);

            // Every bone that actually holds part of the model has to survive,
            // or it comes back unusable for anything that binds by bone name.
            // Bones nothing is weighted to do not: a file records a skeleton
            // through the skin, so a bone with no skin on it leaves no trace.
            var carrying = lod.Influences
                .SelectMany(i => i.Bones)
                .Distinct()
                .Select(b => mesh.Bones[b].Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var came = rebuilt.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            string[] lost = carrying.Where(name => !came.Contains(name)).ToArray();

            _output.WriteLine(
                $"    {carrying.Count:N0} bones carry part of the model; {lost.Length:N0} did not come back.");

            Assert.Empty(lost);

            // Corner for corner, the model must occupy the same space. Compared
            // by where the triangles are drawn, because a file is free to
            // number its vertices differently.
            float worst = 0f;

            for (int c = 0; c < lod.Indices.Count && c < result.Indices.Count; c++)
            {
                worst = Math.Max(worst, Vector3.Distance(
                    lod.Positions[lod.Indices[c]], result.Positions[result.Indices[c]]));
            }

            _output.WriteLine($"    the furthest corner moved {worst:0.####}.");

            for (int c = 0; c < 3 && c < lod.Indices.Count && c < result.Indices.Count; c++)
            {
                Vector3 was = lod.Positions[lod.Indices[c]];
                Vector3 now = result.Positions[result.Indices[c]];

                _output.WriteLine(
                    $"    corner {c}: {was.X:0.##},{was.Y:0.##},{was.Z:0.##} became " +
                    $"{now.X:0.##},{now.Y:0.##},{now.Z:0.##}");
            }

            Assert.True(worst < 0.05f, $"a corner came back {worst:0.###} from where it went out.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WhatComesBackIsWoundTheWayTheGameWindsIt()
    {
        if (SomeModel() is not var (mesh, lod)) { _output.WriteLine("No model to work from."); return; }

        string path = Path.Combine(Path.GetTempPath(), $"oas2_export_{Guid.NewGuid():N}.fbx");

        try
        {
            FbxExporter.Write(path, mesh, lod);

            SkeletalMeshLod result = SourceModelBuilder.Build(MeshFile.Read(path)).Geometry;

            int agree = 0, disagree = 0;

            for (int t = 0; t + 2 < result.Indices.Count; t += 3)
            {
                int a = result.Indices[t], b = result.Indices[t + 1], c = result.Indices[t + 2];

                Vector3 wound = Vector3.Cross(
                    result.Positions[b] - result.Positions[a],
                    result.Positions[c] - result.Positions[a]);

                Vector3 claimed = result.Normals[a] + result.Normals[b] + result.Normals[c];

                if (wound.LengthSquared() < 1e-12f || claimed.LengthSquared() < 1e-12f) continue;

                if (Vector3.Dot(Vector3.Normalize(wound), Vector3.Normalize(claimed)) > 0f) agree++;
                else disagree++;
            }

            _output.WriteLine(
                $"came back with {agree:N0} triangles wound with the surface and {disagree:N0} against it.");

            // The game winds its triangles against the way the surface faces, so
            // a model that has been out and back must still do the same.
            Assert.True(disagree > agree, "the round trip turned the surface inside out.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
