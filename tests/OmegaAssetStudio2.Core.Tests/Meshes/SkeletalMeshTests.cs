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

public sealed class VertexLayoutTests
{
    [Theory]
    // tangent frame 8 + skinning 8 + position + UVs
    [InlineData(false, false, 1, 32)]   // full position 12, half UVs 4
    [InlineData(true, false, 1, 24)]    // packed position 4, half UVs 4
    [InlineData(false, true, 1, 36)]    // full position 12, full UVs 8
    [InlineData(true, true, 2, 36)]     // packed position 4, two full UV sets
    public void StrideFollowsTheLayout(bool packed, bool fullUvs, int uvSets, int expected)
        => Assert.Equal(expected, new VertexLayout(packed, fullUvs, uvSets).Stride);

    [Fact]
    public void PositionSitsAfterTheTangentFrameAndSkinning()
        => Assert.Equal(16, new VertexLayout(true, false, 1).PositionOffset);
}

/// <summary>
/// Reads skinned models from real packages.
/// </summary>
/// <remarks>
/// The decisive check is that reconstructed vertex positions fall inside the
/// bounds the mesh declares. Those bounds are read from a completely different
/// part of the file, so agreement cannot happen by accident — it is what proves
/// the vertex layout and the position unpacking are both right.
/// </remarks>
public sealed class RealSkeletalMeshTests
{
    private readonly ITestOutputHelper _output;

    public RealSkeletalMeshTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// Yields skinned models from character packages, up to <paramref name="meshLimit"/>.
    /// </summary>
    /// <remarks>
    /// Counted in models rather than packages on purpose: most character packages
    /// hold animation or effect data and no mesh at all, so a fixed package count
    /// samples nothing on one install and plenty on another. The package scan is
    /// capped as well so a client with no meshes cannot run forever.
    /// </remarks>
    private static IEnumerable<(Package Package, int Index)> CharacterMeshes(GameClient client, int meshLimit)
    {
        const int packageCeiling = 3000;

        int found = 0;

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, "UC__*.upk").Take(packageCeiling))
        {
            Package package;
            try { package = Package.Open(path); } catch (InvalidPackageException) { continue; }

            foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
            {
                yield return (package, index);

                if (++found >= meshLimit) yield break;
            }
        }
    }

    [Fact]
    public void ReadsSkinnedModelsFromCharacterPackages()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int found = 0, read = 0;
            var layouts = new Dictionary<string, int>();
            var failures = new Dictionary<string, int>();

            foreach ((Package package, int index) in CharacterMeshes(client, 60))
            {
                found++;

                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(
                    package, index, why => failures[why] = failures.GetValueOrDefault(why) + 1);
                if (mesh is null) continue;

                read++;

                SkeletalMeshLod? lod = mesh.HighestDetail;
                if (lod is null) continue;

                string key = lod.Layout.ToString();
                layouts[key] = layouts.GetValueOrDefault(key) + 1;
            }

            if (found == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no skinned models in the sample.");
                continue;
            }

            _output.WriteLine(
                $"{client.DisplayName}: read {read} of {found} skinned models. Layouts seen: " +
                string.Join("; ", layouts.Select(kv => $"{kv.Key} x{kv.Value}")));

            foreach (var failure in failures.OrderByDescending(f => f.Value).Take(6))
                _output.WriteLine($"    failed x{failure.Value}: {failure.Key}");

            Assert.True(read == found, $"{client.DisplayName}: only {read} of {found} skinned models could be read.");
        }
    }

    [Fact]
    public void EverythingAModelDeclaresAgreesWithItself()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedModels = 0;
            long checkedVertices = 0;

            foreach ((Package package, int index) in CharacterMeshes(client, 40))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                if (mesh?.HighestDetail is not { } lod) continue;

                // A skeleton is a tree: every bone's parent must come before it,
                // or the hierarchy cannot be walked.
                for (int b = 1; b < mesh.Bones.Count; b++)
                {
                    Assert.True(mesh.Bones[b].ParentIndex < b,
                        $"{mesh.Name}: bone {b} names a parent that comes after it.");
                }

                Assert.NotEmpty(mesh.Bones);

                // The count the LOD declares and the number of vertices actually
                // decoded are stored in different places and must agree.
                Assert.Equal(lod.VertexCount, lod.Positions.Count);

                // Every index must address a real vertex.
                foreach (int vertexIndex in lod.Indices)
                    Assert.InRange(vertexIndex, 0, lod.Positions.Count - 1);

                // Every section must lie inside the index buffer.
                foreach (MeshSection section in lod.Sections)
                {
                    Assert.InRange(section.BaseIndex, 0, Math.Max(0, lod.Indices.Count));
                    Assert.True(section.BaseIndex + section.IndexCount <= lod.Indices.Count,
                        $"{mesh.Name}: a section runs past the end of the index buffer.");

                    Assert.InRange(section.MaterialIndex, 0, Math.Max(0, mesh.Materials.Count));
                }

                // Triangles come in whole corners.
                Assert.Equal(0, lod.Indices.Count % 3);

                // Every vertex must carry a direction and a texture coordinate,
                // because the viewport reads all three arrays in step.
                Assert.Equal(lod.Positions.Count, lod.Normals.Count);
                Assert.Equal(lod.Positions.Count, lod.TexCoords.Count);

                // A direction that is not unit length would light the surface
                // wrongly; one that is not a number would put a hole in it.
                foreach (Vector3 normal in lod.Normals)
                    Assert.InRange(normal.Length(), 0.99f, 1.01f);

                foreach (Vector2 uv in lod.TexCoords)
                {
                    Assert.False(float.IsNaN(uv.X) || float.IsNaN(uv.Y),
                        $"{mesh.Name}: a texture coordinate is not a number.");
                }

                // Skinning: every vertex must follow at least one bone, name
                // only bones the skeleton has, and be pulled by exactly one
                // bone's worth of strength in total. Any of those being wrong
                // tears the model apart the moment it is posed.
                Assert.Equal(lod.Positions.Count, lod.Influences.Count);

                foreach (VertexInfluence influence in lod.Influences)
                {
                    Assert.NotEmpty(influence.Bones);
                    Assert.Equal(influence.Bones.Count, influence.Weights.Count);

                    foreach (int bone in influence.Bones)
                        Assert.InRange(bone, 0, mesh.Bones.Count - 1);

                    float total = 0f;
                    foreach (float weight in influence.Weights)
                    {
                        Assert.InRange(weight, 0.0001f, 1.0001f);
                        total += weight;
                    }

                    Assert.InRange(total, 0.999f, 1.001f);
                }

                // Every vertex must belong to a run, or its bone numbers were
                // never translated into the skeleton's.
                Assert.NotEmpty(lod.Chunks);
                Assert.Equal(lod.VertexCount, lod.Chunks.Sum(c => c.VertexCount));

                checkedModels++;
                checkedVertices += lod.Positions.Count;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {checkedModels} models internally consistent " +
                $"({checkedVertices:N0} vertices).");

            Assert.True(checkedModels > 0, $"{client.DisplayName}: no skinned model was available to check.");
        }
    }

    [Fact]
    public void ReconstructedPositionsFallInsideTheDeclaredBounds()
    {
        // This is the test that matters. The bounds come from the start of the
        // payload; the positions come from a buffer far later whose layout had to
        // be worked out. If the layout or the unpacking were wrong the positions
        // would be nonsense, and nonsense does not land inside an unrelated box.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedModels = 0;
            long inside = 0, total = 0;

            foreach ((Package package, int index) in CharacterMeshes(client, 40))
            {
                SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                if (mesh?.HighestDetail is not { } lod) continue;
                if (!mesh.Bounds.IsPlausible || !lod.HasGeometry) continue;

                MeshBounds bounds = mesh.Bounds;

                // A little slack: the bounds are authored, not derived to the last
                // decimal, and quantised positions carry rounding error.
                float slackX = (bounds.ExtentX * 0.05f) + 1f;
                float slackY = (bounds.ExtentY * 0.05f) + 1f;
                float slackZ = (bounds.ExtentZ * 0.05f) + 1f;

                foreach (Vector3 position in lod.Positions)
                {
                    total++;

                    if (Math.Abs(position.X - bounds.OriginX) <= bounds.ExtentX + slackX &&
                        Math.Abs(position.Y - bounds.OriginY) <= bounds.ExtentY + slackY &&
                        Math.Abs(position.Z - bounds.OriginZ) <= bounds.ExtentZ + slackZ)
                    {
                        inside++;
                    }
                }

                checkedModels++;
            }

            if (total == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no positions to check.");
                continue;
            }

            double rate = inside / (double)total;
            _output.WriteLine(
                $"{client.DisplayName}: {inside:N0} of {total:N0} vertices lie inside their model's " +
                $"own bounds ({rate:P2}) across {checkedModels} models.");

            Assert.True(rate > 0.99,
                $"{client.DisplayName}: only {rate:P2} of vertices landed inside the declared bounds, " +
                "so the vertex layout or the position unpacking is wrong.");
        }
    }
}
