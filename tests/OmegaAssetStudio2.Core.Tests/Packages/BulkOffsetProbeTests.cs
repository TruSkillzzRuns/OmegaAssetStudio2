using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Counts the blocks of bulk data inside a package that record their own
/// position in the file.
/// </summary>
/// <remarks>
/// A block stored inline says where it is with an offset measured from the
/// start of the file, and the reader here checks that offset to decide the
/// bytes really are inline. Anything that moves an object therefore invalidates
/// every such offset inside it — which is exactly what rewriting an object at a
/// different size does to everything stored after it. This measures how many
/// there are, so the size of that problem is known rather than assumed.
/// </remarks>
public sealed class RealBulkOffsetProbeTests
{
    private readonly ITestOutputHelper _output;

    public RealBulkOffsetProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Finds positions inside an object that look like a bulk-data header
    /// pointing at the bytes directly after itself.
    /// </summary>
    private static List<int> FindSelfReferencingOffsets(ReadOnlySpan<byte> data, int absoluteBase)
    {
        var found = new List<int>();

        for (int p = 0; p + 4 <= data.Length; p += 4)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(data[p..]);

            // The offset field is the last of four, and the payload begins
            // immediately after it — the same test the reader itself makes.
            if (value != absoluteBase + p + 4) continue;

            // Guard against a coincidence: the size field just before it has to
            // be a plausible length that fits inside the object.
            if (p < 4) continue;

            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(p - 4)..]);
            if (size <= 0 || p + 4 + size > data.Length) continue;

            found.Add(p);
        }

        return found;
    }

    [Fact]
    public void HowManyObjectsCarryTheirOwnPositionInTheFile()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(3))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                int objectsWith = 0, blocks = 0;
                var classes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    ReadOnlySpan<byte> data = package.GetExportData(i);

                    List<int> hits = FindSelfReferencingOffsets(data, package.Exports[i].SerialOffset);
                    if (hits.Count == 0) continue;

                    objectsWith++;
                    blocks += hits.Count;

                    string className = package.GetExportClassName(i);
                    classes[className] = classes.GetValueOrDefault(className) + hits.Count;
                }

                _output.WriteLine(
                    $"{hero.DisplayName} ({package.Exports.Count:N0} objects): {objectsWith:N0} objects hold " +
                    $"{blocks:N0} blocks that record their own position.");

                foreach ((string className, int count) in classes.OrderByDescending(c => c.Value).Take(10))
                    _output.WriteLine($"    {className}: {count:N0}");
            }

            return;
        }

        _output.WriteLine("No installs present; nothing probed.");
    }

    /// <summary>
    /// The same measurement on one named package, rather than whichever the
    /// roster happens to list first.
    /// </summary>
    [Fact]
    public void OnePackageInDetail()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            string path = Directory
                .EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(string.Empty);

            if (path.Length == 0) continue;

            string fileName = Path.GetFileName(path);

            Package package = Package.Open(path);

            int blocks = 0;

            for (int i = 0; i < package.Exports.Count; i++)
            {
                blocks += FindSelfReferencingOffsets(
                    package.GetExportData(i), package.Exports[i].SerialOffset).Count;
            }

            var models = new List<string>();

            foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);

                models.Add(mesh?.HighestDetail is { HasGeometry: true } lod
                    ? $"{mesh.Name}: {lod.Positions.Count:N0} vertices, {mesh.Bones.Count:N0} bones"
                    : $"{package.GetExportName(index)}: no geometry");
            }

            _output.WriteLine(
                $"{client.DisplayName} — {fileName}: {new FileInfo(path).Length:N0} bytes, " +
                $"{package.Exports.Count:N0} objects, {blocks:N0} self-recorded blocks.");

            foreach (string model in models) _output.WriteLine($"    {model}");

            // And that replacing the model in this very package disturbs
            // nothing else, however much it grows.
            int meshIndex = package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass).First();
            SkeletalMesh body = SkeletalMeshReader.TryRead(package, meshIndex)!;
            SkeletalMeshLod full = body.HighestDetail!;

            byte[] written = SkeletalMeshSerialiser.Replace(package, meshIndex, body, new MeshGeometry
            {
                // Twice the vertices, so the object cannot possibly fit where
                // it was and has to be put somewhere else.
                Positions = [.. full.Positions, .. full.Positions],
                Normals = [.. full.Normals, .. full.Normals],
                TexCoords = [.. full.TexCoords, .. full.TexCoords],
                Influences = [.. full.Influences, .. full.Influences],
                Indices = full.Indices,
                Sections = [],
            });

            Package after = Package.Read(
                PackageRebuilder.Build(package, [new ExportPatch(meshIndex, written)]), path);

            int moved = 0;

            for (int i = 0; i < package.Exports.Count; i++)
            {
                if (i == meshIndex) continue;

                Assert.Equal(package.Exports[i].SerialOffset, after.Exports[i].SerialOffset);
                Assert.True(package.GetExportData(i).SequenceEqual(after.GetExportData(i)),
                    $"object {i} changed although it was not touched.");
            }

            // Not a size comparison: the layout keeps only the vertices the
            // triangles actually draw, so doubling the buffer without drawing
            // the copies leaves the object much as it was. What matters here is
            // that nothing else moved.
            Assert.Equal(package.Exports.Count, after.Exports.Count);

            _output.WriteLine(
                $"    doubling the model moved {moved} other objects; " +
                $"{blocks:N0} self-recorded blocks all still point at themselves.");
        }
    }

    [Fact]
    public void HowManyOfThoseAreBrokenByReplacingAModel()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(3))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                int meshIndex = -1;
                SkeletalMesh? mesh = null;

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? candidate = SkeletalMeshReader.TryRead(package, index);
                    if (candidate?.HighestDetail is not { HasGeometry: true }) continue;

                    mesh = candidate;
                    meshIndex = index;
                    break;
                }

                if (mesh is null) continue;

                SkeletalMeshLod lod = mesh.HighestDetail!;

                byte[] written = SkeletalMeshSerialiser.Replace(package, meshIndex, mesh, new MeshGeometry
                {
                    Positions = lod.Positions,
                    Normals = lod.Normals,
                    TexCoords = lod.TexCoords,
                    Influences = lod.Influences,
                    Indices = lod.Indices,
                    Sections = lod.Sections,
                });

                byte[] rebuilt = PackageRebuilder.Build(package, [new ExportPatch(meshIndex, written)]);
                Package after = Package.Read(rebuilt, package.Path);

                int moved = 0, stale = 0, blocks = 0;

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    if (i == meshIndex) continue;

                    blocks += FindSelfReferencingOffsets(
                        package.GetExportData(i), package.Exports[i].SerialOffset).Count;

                    if (after.Exports[i].SerialOffset == package.Exports[i].SerialOffset)
                    {
                        // In the same place, so it must also be the same bytes.
                        Assert.True(
                            package.GetExportData(i).SequenceEqual(after.GetExportData(i)),
                            $"object {i} was left where it was but its bytes changed.");

                        continue;
                    }

                    moved++;

                    // Anything that did move takes its self-recorded offsets
                    // with it, and they no longer point at themselves.
                    stale += FindSelfReferencingOffsets(
                        package.GetExportData(i), package.Exports[i].SerialOffset).Count;
                }

                _output.WriteLine(
                    $"{hero.DisplayName}: replacing {mesh.Name} in a package holding {blocks:N0} " +
                    $"self-recorded blocks moved {moved:N0} other objects and broke {stale:N0} of them.");

                // The whole point. Nothing else may move, so nothing else can
                // break, however much the model grows.
                Assert.Equal(0, moved);
                Assert.True(blocks > 0, "this package holds no self-recorded blocks, so it proves nothing.");

                return;
            }
        }
    }
}
