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
/// Checks that moved vertices can be written back into a real package.
/// </summary>
/// <remarks>
/// Every check here reads a real package and writes only in memory. Nothing is
/// saved anywhere, least of all into a game folder.
/// </remarks>
public sealed class RealSkeletalMeshWriterTests
{
    private readonly ITestOutputHelper _output;

    public RealSkeletalMeshWriterTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public void WritingVerticesBackUnchangedLeavesPositionsExact()
    {
        // The check everything else rests on. If writing what was already there
        // alters a single byte, the writer is putting something in the wrong
        // place and would quietly damage a model.
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            byte[] before = package.GetExportData(index).ToArray();

            // Positions alone must come back byte for byte. They are stored as
            // full numbers, so there is no excuse for any difference, and one
            // would mean the writer is putting them in the wrong place.
            byte[] positionsOnly = SkeletalMeshWriter.MoveVertices(package, index, lod, lod.Positions);

            Assert.Equal(before.Length, positionsOnly.Length);
            Assert.True(before.AsSpan().SequenceEqual(positionsOnly),
                $"{mesh.Name}: writing the same positions back changed the object.");

            // Directions cannot come back exactly: each is a single byte, so
            // reading one and writing it again rounds. What must hold is that
            // nothing moves by more than that rounding, and that nothing
            // outside the direction bytes is touched at all.
            byte[] withDirections =
                SkeletalMeshWriter.MoveVertices(package, index, lod, lod.Positions, lod.Normals);

            int stride = lod.Layout.Stride;
            int differing = 0, worst = 0;

            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == withDirections[i]) continue;

                differing++;
                worst = Math.Max(worst, Math.Abs(before[i] - withDirections[i]));

                int within = (i - lod.VertexDataOffset) % stride;

                Assert.True(within is >= 4 and <= 6,
                    $"{mesh.Name}: byte {within} of a vertex changed, which is not a direction.");
            }

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} — positions exact; " +
                $"{differing:N0} direction bytes differ by at most {worst}.");

            Assert.True(worst <= 1, $"a direction changed by {worst}, which is more than rounding.");
            return;
        }

        _output.WriteLine("No installs present; nothing probed.");
    }

    [Fact]
    public void MovedVerticesComeBackWhereTheyWerePut()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            // Moved by a fixed amount, which is easy to check for and cannot be
            // confused with the original positions.
            var shift = new Vector3(3f, -5f, 7f);
            var moved = lod.Positions.Select(p => p + shift).ToList();

            byte[] written = SkeletalMeshWriter.MoveVertices(package, index, lod, moved, lod.Normals);

            // Read the object back through the ordinary reader, so this is
            // checking what the game would see rather than what was intended.
            Package rebuilt = Package.Read(
                PackageWriter.Build(package, [new ExportPatch(index, written)]), package.Path);

            SkeletalMesh? reread = SkeletalMeshReader.TryRead(rebuilt, index);

            Assert.NotNull(reread);

            SkeletalMeshLod after = reread!.HighestDetail!;

            Assert.Equal(lod.Positions.Count, after.Positions.Count);

            float worst = 0f;
            for (int i = 0; i < moved.Count; i++)
                worst = Math.Max(worst, Vector3.Distance(moved[i], after.Positions[i]));

            _output.WriteLine(
                $"{client.DisplayName}: {mesh.Name} moved and re-read — largest error {worst:0.#####} " +
                $"({lod.Layout})");

            // Full-precision positions come back exactly; quantised ones come
            // back within the step of their own encoding.
            float allowed = lod.Layout.PackedPosition ? mesh.Bounds.Radius * 0.01f : 0.001f;

            Assert.True(worst < allowed,
                $"vertices came back {worst:0.####} from where they were put, allowed {allowed:0.####}.");

            return;
        }
    }

    [Fact]
    public void EverythingAroundTheVerticesIsLeftAlone()
    {
        // Only the vertices may change. The skeleton, sections, index buffer
        // and skinning must all read back exactly as before.
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            var moved = lod.Positions.Select(p => p * 1.05f).ToList();
            byte[] written = SkeletalMeshWriter.MoveVertices(package, index, lod, moved);

            Package rebuilt = Package.Read(
                PackageWriter.Build(package, [new ExportPatch(index, written)]), package.Path);

            SkeletalMesh after = SkeletalMeshReader.TryRead(rebuilt, index)!;

            Assert.Equal(mesh.Bones.Count, after.Bones.Count);
            Assert.Equal(mesh.Bones.Select(b => b.Name), after.Bones.Select(b => b.Name));
            Assert.Equal(mesh.Materials.Count, after.Materials.Count);
            Assert.Equal(mesh.Lods.Count, after.Lods.Count);

            SkeletalMeshLod afterLod = after.HighestDetail!;

            Assert.Equal(lod.Indices, afterLod.Indices);
            Assert.Equal(lod.Sections.Count, afterLod.Sections.Count);
            Assert.Equal(lod.TexCoords.Count, afterLod.TexCoords.Count);

            for (int i = 0; i < lod.Influences.Count; i++)
            {
                Assert.Equal(lod.Influences[i].Bones, afterLod.Influences[i].Bones);
            }

            return;
        }
    }

    [Fact]
    public void GivingTheWrongNumberOfVerticesIsRefused()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            if (FindBody(client) is not var (package, index, mesh)) continue;

            SkeletalMeshLod lod = mesh.HighestDetail!;

            MeshWriteException failure = Assert.Throws<MeshWriteException>(
                () => SkeletalMeshWriter.MoveVertices(package, index, lod, [Vector3.Zero]));

            Assert.Contains("cannot add or remove", failure.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
    }
}
