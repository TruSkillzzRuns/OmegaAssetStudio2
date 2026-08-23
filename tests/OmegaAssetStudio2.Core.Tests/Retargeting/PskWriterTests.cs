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

/// <summary>
/// Checks that a model written out comes back the same.
/// </summary>
/// <remarks>
/// Written and read by two pieces of code that share no logic, so agreement
/// means the file is right rather than that one mistake cancelled another.
/// </remarks>
public sealed class PskWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oas2-psk-" + Guid.NewGuid().ToString("N"));

    public PskWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string name) => Path.Combine(_folder, name + ".psk");

    private static MeshBone Bone(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        ChildCount = 0,
        Orientation = Quaternion.Identity,
        Position = position,
    };

    private static SkeletalMeshLod Lod(
        Vector3[] positions, int[] indices, VertexInfluence[] influences, Vector2[]? uvs = null) => new()
    {
        Sections = [],
        Indices = indices,
        VertexCount = positions.Length,
        Layout = default,
        Positions = positions,
        Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
        TexCoords = uvs ?? new Vector2[positions.Length],
        Influences = influences,
        Chunks = [],
    };

    private static VertexInfluence Skin(params (int Bone, float Weight)[] parts) => new()
    {
        Bones = parts.Select(p => p.Bone).ToList(),
        Weights = parts.Select(p => p.Weight).ToList(),
    };

    [Fact]
    public void AModelWrittenOutReadsBackTheSame()
    {
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };

        List<MeshBone> bones = [Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, new Vector3(0, 0, 10))];

        SkeletalMeshLod lod = Lod(
            positions, [0, 1, 2],
            [Skin((0, 1f)), Skin((1, 1f)), Skin((0, 0.5f), (1, 0.5f))],
            uvs);

        string path = PathFor("round-trip");
        PskWriter.Write(path, lod, bones, ["skin"]);

        ImportedMesh read = PskReader.Read(path);

        Assert.Equal(3, read.Positions.Count);
        Assert.Equal(3, read.WedgePoints.Count);
        Assert.Equal(1, read.TriangleCount);
        Assert.Equal(2, read.Bones.Count);
        Assert.Equal("g_l_hip", read.Bones[1].Name);
        Assert.Equal(new Vector3(0, 0, 10), read.Bones[1].Position);
        Assert.Contains("skin", read.Materials);
    }

    [Fact]
    public void PositionsSurviveExactly()
    {
        Vector3[] positions = [new(1.5f, -2.25f, 3.125f), new(-4f, 5f, -6f), new(0, 0, 0)];

        SkeletalMeshLod lod = Lod(positions, [0, 1, 2], [Skin((0, 1f)), Skin((0, 1f)), Skin((0, 1f))]);

        string path = PathFor("positions");
        PskWriter.Write(path, lod, [Bone("root", 0, Vector3.Zero)]);

        ImportedMesh read = PskReader.Read(path);

        for (int i = 0; i < positions.Length; i++)
            Assert.Equal(positions[i], read.Positions[read.WedgePoints[i]]);
    }

    [Fact]
    public void CornersInTheSamePlaceShareOnePosition()
    {
        // Three corners, one place. Written as three the file triples in size
        // and a modelling tool can no longer tell they are joined.
        Vector3[] positions = [new(1, 1, 1), new(1, 1, 1), new(1, 1, 1)];

        SkeletalMeshLod lod = Lod(positions, [0, 1, 2], [Skin((0, 1f)), Skin((0, 1f)), Skin((0, 1f))]);

        string path = PathFor("shared");
        PskWriter.Write(path, lod, [Bone("root", 0, Vector3.Zero)]);

        ImportedMesh read = PskReader.Read(path);

        Assert.Single(read.Positions);
        Assert.Equal(3, read.WedgePoints.Count);
        Assert.All(read.WedgePoints, p => Assert.Equal(0, p));
    }

    [Fact]
    public void WeightsComeBackOnTheRightBones()
    {
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

        SkeletalMeshLod lod = Lod(
            positions, [0, 1, 2],
            [Skin((0, 1f)), Skin((1, 1f)), Skin((0, 0.25f), (1, 0.75f))]);

        string path = PathFor("weights");
        PskWriter.Write(path, lod, [Bone("root", 0, Vector3.Zero), Bone("g_l_hip", 0, Vector3.Zero)]);

        ImportedMesh read = PskReader.Read(path);

        IReadOnlyList<(int Bone, float Weight)> shared = read.Weights[read.WedgePoints[2]];

        Assert.Equal(2, shared.Count);
        Assert.Equal(1, shared[0].Bone);              // the stronger of the two
        Assert.Equal(0.75f, shared[0].Weight, 3);
    }

    [Fact]
    public void ASkeletonComesBackInTheSameOrder()
    {
        // Bone numbers in the weights refer to positions in this list, so a
        // reordered skeleton silently rebinds the whole model.
        List<MeshBone> bones =
        [
            Bone("root", 0, Vector3.Zero),
            Bone("g_pelvis", 0, new Vector3(0, 0, 5)),
            Bone("g_l_hip", 1, new Vector3(0, 0, 10)),
        ];

        SkeletalMeshLod lod = Lod([Vector3.Zero], [], [Skin((2, 1f))]);

        string path = PathFor("skeleton");
        PskWriter.Write(path, lod, bones);

        ImportedMesh read = PskReader.Read(path);

        Assert.Equal(["root", "g_pelvis", "g_l_hip"], read.Bones.Select(b => b.Name));
        Assert.Equal(1, read.Bones[2].ParentIndex);
    }
}

/// <summary>
/// Writes real characters out and reads them back.
/// </summary>
public sealed class RealPskWriterTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oas2-psk-real-" + Guid.NewGuid().ToString("N"));

    public RealPskWriterTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

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
    public void ARealCharacterSurvivesBeingWrittenAndReadBack()
    {
        foreach (GameClient client in InstalledClients())
        {
            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(6))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                SkeletalMesh? body = null;

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                    if (mesh?.HighestDetail is not { HasGeometry: true }) continue;

                    if (body is null || mesh.Bones.Count > body.Bones.Count) body = mesh;
                }

                if (body is null) continue;

                SkeletalMeshLod lod = body.HighestDetail!;
                string path = Path.Combine(_folder, body.Name + ".psk");

                PskWriter.Write(path, lod, body.Bones);
                ImportedMesh read = PskReader.Read(path);

                // Every drawn corner and every triangle must survive, or part
                // of the model is simply missing from the export.
                Assert.Equal(lod.Positions.Count, read.WedgePoints.Count);
                Assert.Equal(lod.TriangleCount, read.TriangleCount);
                Assert.Equal(body.Bones.Count, read.Bones.Count);

                // Every corner's position must come back exactly.
                for (int i = 0; i < lod.Positions.Count; i += 97)
                    Assert.Equal(lod.Positions[i], read.Positions[read.WedgePoints[i]]);

                // Nothing may point outside the skeleton it was written with.
                foreach (IReadOnlyList<(int Bone, float Weight)> weights in read.Weights)
                {
                    foreach ((int bone, _) in weights)
                        Assert.InRange(bone, 0, read.Bones.Count - 1);
                }

                _output.WriteLine(
                    $"{client.DisplayName}: {body.Name} — {read.Positions.Count:N0} points " +
                    $"for {lod.Positions.Count:N0} corners, {read.TriangleCount:N0} triangles, " +
                    $"{new FileInfo(path).Length / 1024:N0} KB");
            }

            return;   // one client is enough for a round trip
        }
    }
}
