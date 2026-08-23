using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks what putting a model into the game plans to do, and that it refuses
/// rather than writing when the result would not read back.
/// </summary>
/// <remarks>
/// Planning is deliberately separate from committing, so everything here can
/// run against real packages without a single byte being written. Nothing in
/// this file commits, so no game file is ever touched.
/// </remarks>
public sealed class RealMeshInstallerTests
{
    private readonly ITestOutputHelper _output;

    public RealMeshInstallerTests(ITestOutputHelper output) => _output = output;

    private static (Package Package, int Index, SkeletalMesh Mesh)? FindBody(GameClient client)
    {
        foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(20))
        {
            Package package;
            try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

            foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                if (mesh?.HighestDetail is { HasGeometry: true }) return (package, index, mesh);
            }
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
    };

    [Fact]
    public void APlanDescribesExactlyWhatWouldChange()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            long sizeOnDisk = new FileInfo(package.Path).Length;

            MeshInstallPlan plan = MeshInstaller.Plan(package, index, mesh, From(lod));

            Assert.Equal(package.Path, plan.PackagePath);
            Assert.Equal(mesh.Name, plan.ObjectName);
            Assert.Equal(lod.Positions.Count, plan.VerticesAfter);
            Assert.Equal(lod.TriangleCount, plan.TrianglesAfter);
            Assert.Equal(lod.Positions.Count, plan.VerticesBefore);
            Assert.Equal(mesh.Lods.Count, plan.DetailLevels);

            // The size reported has to be the size of what would actually land,
            // because it is shown to the user as a fact about their file.
            Assert.Equal(sizeOnDisk, plan.FileSizeBefore);
            Assert.True(plan.FileSizeAfter > 0);

            // And the file itself must be exactly as it was: planning writes
            // nothing.
            Assert.Equal(sizeOnDisk, new FileInfo(package.Path).Length);

            _output.WriteLine(
                $"{client.DisplayName}: {plan.ObjectName} in {plan.FileName} — " +
                $"{plan.VerticesAfter:N0} vertices, {plan.TrianglesAfter:N0} triangles, " +
                $"{plan.FileSizeBefore:N0} to {plan.FileSizeAfter:N0} bytes, " +
                $"{plan.DetailLevels} level(s) of detail.");

            return;
        }

        _output.WriteLine("No installs present; nothing probed.");
    }

    [Fact]
    public void PlanningADifferentModelReportsTheNewCounts()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;
            int triangles = lod.TriangleCount / 2;

            MeshInstallPlan plan = MeshInstaller.Plan(package, index, mesh, From(lod) with
            {
                Indices = lod.Indices.Take(triangles * 3).ToList(),
                Sections = [],
            });

            Assert.Equal(lod.TriangleCount, plan.TrianglesBefore);
            Assert.Equal(triangles, plan.TrianglesAfter);

            return;
        }
    }

    [Fact]
    public void AModelThatCannotBeWrittenIsRefusedBeforeAnyPlanExists()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            // A triangle naming a vertex that does not exist. Nothing may be
            // written from this, and the file must be left alone.
            long sizeOnDisk = new FileInfo(package.Path).Length;

            var broken = From(lod) with
            {
                Indices = lod.Indices.Select(i => i == 0 ? lod.Positions.Count + 5 : i).ToList(),
            };

            Assert.ThrowsAny<Exception>(() => MeshInstaller.Plan(package, index, mesh, broken));
            Assert.Equal(sizeOnDisk, new FileInfo(package.Path).Length);

            return;
        }
    }

    [Fact]
    public void APackageThatIsNotAFileIsRefused()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            // Read from memory, so it has no path to write to. This stands for
            // every case where the target did not come from the user's install.
            Package detached = Package.Read(File.ReadAllBytes(package.Path));

            MeshWriteException failure = Assert.Throws<MeshWriteException>(
                () => MeshInstaller.Plan(detached, index, mesh, From(mesh.HighestDetail!)));

            Assert.Contains("not a file on disk", failure.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
    }
}
