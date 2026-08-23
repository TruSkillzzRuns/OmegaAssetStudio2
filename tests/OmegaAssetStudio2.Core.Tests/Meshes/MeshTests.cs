using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

public sealed class MeshBoundsTests
{
    [Fact]
    public void ASphereReachingTheBoxCornerIsPlausible()
    {
        var bounds = new MeshBounds(0, 0, 0, 3, 4, 12, 13.1f);

        Assert.True(bounds.IsPlausible);
        Assert.Equal(6, bounds.Width);
        Assert.Equal(24, bounds.Height);
    }

    [Fact]
    public void ASphereTighterThanTheBoxCornerIsStillPlausible()
    {
        // Taken from a real mesh. The sphere and the box are fitted to the same
        // geometry independently, so the sphere sits inside the box's corner
        // distance whenever those corners are empty space — which is usual.
        // Requiring it to reach the corner rejected nearly half of all real
        // meshes.
        var bounds = new MeshBounds(0, 0, 0, 445.86f, 427.99f, 137.06f, 619.92f);

        Assert.True(bounds.IsPlausible);
    }

    [Fact]
    public void ASphereTooSmallToCoverTheLongestAxisIsNot()
    {
        // Below this the numbers cannot describe the same object, so the payload
        // was read from the wrong place.
        Assert.False(new MeshBounds(0, 0, 0, 100, 100, 100, 5).IsPlausible);
    }

    [Fact]
    public void ASphereLargerThanTheBoxCornerIsNot()
        => Assert.False(new MeshBounds(0, 0, 0, 10, 10, 10, 500).IsPlausible);

    [Fact]
    public void AFlatOrEmptyMeshIsAllowed()
    {
        // A plane has no thickness and a point has no size; both are legitimate.
        Assert.True(new MeshBounds(0, 0, 0, 50, 50, 0, 70.8f).IsPlausible);
        Assert.True(new MeshBounds(0, 0, 0, 0, 0, 0, 0).IsPlausible);
    }

    [Fact]
    public void NonFiniteValuesAreNotPlausible()
    {
        Assert.False(new MeshBounds(float.NaN, 0, 0, 1, 1, 1, 2).IsPlausible);
        Assert.False(new MeshBounds(0, 0, 0, float.PositiveInfinity, 1, 1, 2).IsPlausible);
    }

    [Fact]
    public void NegativeExtentsAreNotPlausible()
        => Assert.False(new MeshBounds(0, 0, 0, -1, 1, 1, 5).IsPlausible);

    [Fact]
    public void ReadsSevenFloatsFromTheGivenOffset()
    {
        byte[] data = new byte[64];
        float[] values = [1f, 2f, 3f, 4f, 5f, 6f, 11f];
        for (int i = 0; i < values.Length; i++)
            BitConverter.GetBytes(values[i]).CopyTo(data, 8 + (i * 4));

        MeshBounds bounds = MeshBounds.Read(data, 8);

        Assert.Equal(1f, bounds.OriginX);
        Assert.Equal(6f, bounds.ExtentZ);
        Assert.Equal(11f, bounds.Radius);
    }

    [Fact]
    public void ReadingPastTheEndThrows()
        => Assert.Throws<OmegaAssetStudio2.Core.Packages.InvalidPackageException>(
            () => MeshBounds.Read(new byte[8], 0));
}

/// <summary>Reads real models from every installed client.</summary>
public sealed class RealMeshTests
{
    private readonly ITestOutputHelper _output;

    public RealMeshTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public async Task ReadsModelsWithSelfConsistentSizes()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        var catalog = new MeshCatalog();

        foreach (GameClient client in clients)
        {
            IReadOnlyList<MeshInfo> meshes = await catalog.ScanAsync(client, "SCS__*.upk");
            if (meshes.Count == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no models in the sampled packages.");
                continue;
            }

            int withBounds = meshes.Count(m => m.HasBounds);

            foreach (MeshInfo mesh in meshes)
            {
                Assert.False(string.IsNullOrWhiteSpace(mesh.Name));
                Assert.False(string.IsNullOrWhiteSpace(mesh.ObjectPath));
                Assert.True(mesh.DataSize > 0);

                // Bounds are only reported when they are self-consistent, so any
                // that are present must pass the check.
                if (mesh.Bounds is not null) Assert.True(mesh.Bounds.Value.IsPlausible);

                // A collision shape is made of whole triangles referencing real
                // material slots; anything else means the walk went wrong.
                if (mesh.HasCollision)
                {
                    Assert.True(mesh.CollisionMaterialCount > 0,
                        $"{mesh.Name}: has triangles but references no material slot.");
                    Assert.True(mesh.CollisionTriangleCount < 5_000_000,
                        $"{mesh.Name}: implausible triangle count {mesh.CollisionTriangleCount}.");
                }
            }

            double rate = withBounds / (double)meshes.Count;
            _output.WriteLine(
                $"{client.DisplayName}: {meshes.Count:N0} models, {withBounds:N0} with a readable size ({rate:P0}). " +
                $"Largest: {meshes.Where(m => m.HasBounds).OrderByDescending(m => m.Bounds!.Value.Radius).FirstOrDefault()?.Name ?? "none"}");

            int withCollision = meshes.Count(m => m.HasCollision);
            _output.WriteLine($"    {withCollision:N0} reported a collision shape.");

            // Every mesh in every installed client reads. This was 53% until the
            // plausibility check was corrected — it had demanded the bounding
            // sphere reach the bounding box's corners, which real geometry does
            // not do. A regression here means either the payload offset moved or
            // that mistake has been reintroduced.
            Assert.True(rate > 0.99, $"{client.DisplayName}: only {rate:P0} of models had readable bounds.");
        }
    }
}
