using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using OmegaAssetStudio2.Core.Retargeting;
using Xunit;
using Xunit.Abstractions;
using AssimpMesh = Assimp.Mesh;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

/// <summary>
/// Checks the interchange-format reader by writing files and reading them back.
/// </summary>
/// <remarks>
/// A model is built here, saved as a real file, and read back through the
/// application's own reader. That exercises the whole path a user's file takes,
/// including the library that parses it, rather than a hand-made stand-in.
/// </remarks>
public sealed class FbxReaderTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oas2-fbx-" + Guid.NewGuid().ToString("N"));

    public FbxReaderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A triangle skinned to two bones, saved as a real file.</summary>
    private string WriteScene(string name, string format = "fbx")
    {
        var mesh = new AssimpMesh("body", PrimitiveType.Triangle) { MaterialIndex = 0 };

        mesh.Vertices.Add(new Vector3D(0, 0, 0));
        mesh.Vertices.Add(new Vector3D(1, 0, 0));
        mesh.Vertices.Add(new Vector3D(0, 1, 0));

        mesh.TextureCoordinateChannels[0].Add(new Vector3D(0, 0, 0));
        mesh.TextureCoordinateChannels[0].Add(new Vector3D(1, 0, 0));
        mesh.TextureCoordinateChannels[0].Add(new Vector3D(0, 1, 0));
        mesh.UVComponentCount[0] = 2;

        mesh.Faces.Add(new Face([0, 1, 2]));

        var pelvis = new Bone { Name = "g_pelvis", OffsetMatrix = Assimp.Matrix4x4.Identity };
        pelvis.VertexWeights.Add(new VertexWeight(0, 1f));
        pelvis.VertexWeights.Add(new VertexWeight(2, 0.25f));

        var hip = new Bone { Name = "g_l_hip", OffsetMatrix = Assimp.Matrix4x4.Identity };
        hip.VertexWeights.Add(new VertexWeight(1, 1f));
        hip.VertexWeights.Add(new VertexWeight(2, 0.75f));

        mesh.Bones.Add(pelvis);
        mesh.Bones.Add(hip);

        var scene = new Scene { RootNode = new Node("root") };
        scene.Meshes.Add(mesh);
        scene.Materials.Add(new Material { Name = "skin" });

        var pelvisNode = new Node("g_pelvis", scene.RootNode);
        var hipNode = new Node("g_l_hip", pelvisNode)
        {
            Transform = Assimp.Matrix4x4.FromTranslation(new Vector3D(0, 0, 10)),
        };

        pelvisNode.Children.Add(hipNode);
        scene.RootNode.Children.Add(pelvisNode);
        scene.RootNode.MeshIndices.Add(0);

        string path = Path.Combine(_folder, $"{name}.{format}");

        using var exporter = new AssimpContext();
        exporter.ExportFile(scene, path, format == "fbx" ? "fbx" : format);

        return path;
    }

    [Fact]
    public void AModelSavedByAModellingToolIsReadBack()
    {
        ImportedMesh mesh = MeshFile.Read(WriteScene("triangle"));

        Assert.Equal(3, mesh.Positions.Count);
        Assert.Equal(3, mesh.WedgePoints.Count);
        Assert.Equal(1, mesh.TriangleCount);

        _output.WriteLine($"read back: {mesh}");
    }

    [Fact]
    public void TheSkeletonComesBackWithItsNamesAndArrangement()
    {
        ImportedMesh mesh = MeshFile.Read(WriteScene("skeleton"));

        Assert.True(mesh.HasSkeleton);
        Assert.Contains(mesh.Bones, b => b.Name == "g_pelvis");
        Assert.Contains(mesh.Bones, b => b.Name == "g_l_hip");

        // A parent must come before its child, or the chain cannot be walked.
        for (int i = 1; i < mesh.Bones.Count; i++)
            Assert.True(mesh.Bones[i].ParentIndex < i, $"{mesh.Bones[i].Name} names a parent after it.");
    }

    [Fact]
    public void WeightsComeBackOnTheRightBonesAndAddToOne()
    {
        ImportedMesh mesh = MeshFile.Read(WriteScene("weights"));

        int hip = mesh.Bones.ToList().FindIndex(b => b.Name == "g_l_hip");
        Assert.True(hip >= 0);

        // The third corner was three-quarters on the hip.
        IReadOnlyList<(int Bone, float Weight)> shared =
            mesh.Weights.First(w => w.Count == 2);

        Assert.Equal(1f, shared.Sum(w => w.Weight), 3);
        Assert.Equal(hip, shared[0].Bone);
        Assert.Equal(0.75f, shared[0].Weight, 2);
    }

    [Fact]
    public void AModelReadThisWayGoesThroughARetargetUnchanged()
    {
        // The point of the reader: what comes out has to be usable by the rest
        // of the tool without special handling.
        ImportedMesh imported = MeshFile.Read(WriteScene("pipeline"));

        SourceModel model = SourceModelBuilder.Build(imported);

        Assert.True(model.HasSkeleton);
        Assert.Equal(3, model.Geometry.Positions.Count);
        Assert.All(model.Geometry.Normals, n => Assert.True(n.Length() > 0.9f));
        Assert.InRange(model.MostInfluences, 1, 4);
    }

    [Fact]
    public void AFileThatIsNotThereSaysSo()
        => Assert.Throws<FileNotFoundException>(
            () => MeshFile.Read(Path.Combine(_folder, "nothing-here.fbx")));

    [Fact]
    public void AFileThatIsNotAModelIsRefusedWithAReason()
    {
        string path = Path.Combine(_folder, "rubbish.fbx");
        File.WriteAllText(path, "this is not a model");

        Assert.Throws<InvalidMeshFileException>(() => MeshFile.Read(path));
    }

    [Fact]
    public void TheFileTypesOfferedIncludeWhatModellingToolsSave()
    {
        Assert.Contains(".fbx", MeshFile.Extensions);
        Assert.Contains(".psk", MeshFile.Extensions);
    }
}
