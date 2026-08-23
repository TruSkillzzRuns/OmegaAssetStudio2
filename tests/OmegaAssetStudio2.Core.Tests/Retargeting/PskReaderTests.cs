using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using OmegaAssetStudio2.Core.Retargeting;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Retargeting;

/// <summary>
/// Checks the mesh-file reader by writing files and reading them back.
/// </summary>
/// <remarks>
/// The format states the size of every entry and how many there are, so a file
/// can be built here exactly and the reader held to it. That is stronger than
/// checking a sample file, because the expected answer is known rather than
/// inferred.
/// </remarks>
public sealed class PskReaderTests
{
    private static void Chunk(BinaryWriter writer, string name, int entrySize, int entryCount)
    {
        var padded = new byte[20];
        Encoding.ASCII.GetBytes(name).CopyTo(padded, 0);

        writer.Write(padded);
        writer.Write(0);            // flags
        writer.Write(entrySize);
        writer.Write(entryCount);
    }

    private static void WriteName(BinaryWriter writer, string name, int width)
    {
        var padded = new byte[width];
        Encoding.ASCII.GetBytes(name).CopyTo(padded, 0);
        writer.Write(padded);
    }

    /// <summary>A small but complete model: two triangles on two bones.</summary>
    private static byte[] BuildFile()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        Chunk(writer, "ACTRHEAD", 0, 0);

        Vector3[] points =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
        ];

        Chunk(writer, "PNTS0000", 12, points.Length);
        foreach (Vector3 point in points)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
        }

        Chunk(writer, "VTXW0000", 16, points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            writer.Write((ushort)i);      // which point
            writer.Write((ushort)0);      // padding
            writer.Write(i * 0.25f);      // u
            writer.Write(1f - (i * 0.25f)); // v
            writer.Write(0);              // material and padding
        }

        Chunk(writer, "FACE0000", 12, 2);
        foreach ((ushort a, ushort b, ushort c) in new[] { ((ushort)0, (ushort)1, (ushort)2), ((ushort)0, (ushort)2, (ushort)3) })
        {
            writer.Write(a);
            writer.Write(b);
            writer.Write(c);
            writer.Write(0);              // material, aux, smoothing
            writer.Write((ushort)0);
        }

        Chunk(writer, "MATT0000", 88, 1);
        WriteName(writer, "skin", 64);
        writer.Write(new byte[24]);

        Chunk(writer, "REFSKELT", 120, 2);
        foreach ((string name, int parent, Vector3 position) in
                 new[] { ("root", 0, Vector3.Zero), ("g_l_hip", 0, new Vector3(0, 0, 10)) })
        {
            WriteName(writer, name, 64);
            writer.Write(0);              // flags
            writer.Write(0);              // children
            writer.Write(parent);
            writer.Write(0f);             // rotation
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(1f);
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
            writer.Write(new byte[16]);   // length and size
        }

        // Deliberately out of order and not adding to one, which is how these
        // arrive: the reader has to gather and normalise them.
        Chunk(writer, "RAWWEIGHTS", 12, 3);
        foreach ((float weight, int point, int bone) in new[] { (1f, 0, 0), (1f, 1, 1), (3f, 1, 0) })
        {
            writer.Write(weight);
            writer.Write(point);
            writer.Write(bone);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    [Fact]
    public void AWholeModelIsReadBack()
    {
        ImportedMesh mesh = PskReader.Read(BuildFile());

        Assert.Equal(4, mesh.Positions.Count);
        Assert.Equal(4, mesh.TexCoords.Count);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(6, mesh.Indices.Count);
        Assert.Equal(["skin"], mesh.Materials);
        Assert.Equal(2, mesh.Bones.Count);
        Assert.True(mesh.HasSkeleton);
    }

    [Fact]
    public void PositionsAreReadExactlyAsStored()
    {
        // Not turned or flipped. Some tools write a model on its side, but
        // correcting that here would move one that was already right.
        ImportedMesh mesh = PskReader.Read(BuildFile());

        Assert.Equal(new Vector3(0, 0, 0), mesh.Positions[0]);
        Assert.Equal(new Vector3(1, 1, 0), mesh.Positions[2]);
    }

    [Fact]
    public void TheSkeletonComesBackWithItsNamesAndParents()
    {
        ImportedMesh mesh = PskReader.Read(BuildFile());

        Assert.Equal("root", mesh.Bones[0].Name);
        Assert.Equal("g_l_hip", mesh.Bones[1].Name);
        Assert.Equal(0, mesh.Bones[1].ParentIndex);
        Assert.Equal(new Vector3(0, 0, 10), mesh.Bones[1].Position);
    }

    [Fact]
    public void WeightsAreGatheredStrongestFirstAndBroughtToOne()
    {
        ImportedMesh mesh = PskReader.Read(BuildFile());

        IReadOnlyList<(int Bone, float Weight)> second = mesh.Weights[1];

        Assert.Equal(2, second.Count);
        Assert.Equal(0, second[0].Bone);                 // the stronger of the two
        Assert.Equal(0.75f, second[0].Weight, 4);
        Assert.Equal(0.25f, second[1].Weight, 4);
        Assert.Equal(1f, second.Sum(w => w.Weight), 4);
    }

    [Fact]
    public void APointNothingWeightsIsEmptyRatherThanMissing()
    {
        ImportedMesh mesh = PskReader.Read(BuildFile());

        Assert.Equal(4, mesh.Weights.Count);
        Assert.Empty(mesh.Weights[3]);
    }

    [Fact]
    public void AChunkTheReaderDoesNotKnowIsSteppedOverExactly()
    {
        // The point of the stated sizes: a file carrying extra chunks must
        // still read, and everything after them must land correctly.
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        Chunk(writer, "ACTRHEAD", 0, 0);
        Chunk(writer, "SOMETHING", 7, 3);
        writer.Write(new byte[21]);

        Chunk(writer, "PNTS0000", 12, 1);
        writer.Write(5f);
        writer.Write(6f);
        writer.Write(7f);

        writer.Flush();

        ImportedMesh mesh = PskReader.Read(buffer.ToArray());

        Assert.Single(mesh.Positions);
        Assert.Equal(new Vector3(5, 6, 7), mesh.Positions[0]);
    }

    [Fact]
    public void AnEntryWiderThanExpectedIsStillWalkedCorrectly()
    {
        // Writers pad entries differently. The size comes from the file, so a
        // wider entry must not shift everything after it.
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        Chunk(writer, "ACTRHEAD", 0, 0);
        Chunk(writer, "PNTS0000", 16, 2);   // four bytes of padding per point

        writer.Write(1f); writer.Write(2f); writer.Write(3f); writer.Write(0);
        writer.Write(4f); writer.Write(5f); writer.Write(6f); writer.Write(0);

        writer.Flush();

        ImportedMesh mesh = PskReader.Read(buffer.ToArray());

        Assert.Equal(new Vector3(1, 2, 3), mesh.Positions[0]);
        Assert.Equal(new Vector3(4, 5, 6), mesh.Positions[1]);
    }

    [Fact]
    public void AChunkClaimingMoreThanTheFileHoldsIsRefused()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        Chunk(writer, "ACTRHEAD", 0, 0);
        Chunk(writer, "PNTS0000", 12, 1000);   // nothing follows
        writer.Flush();

        Assert.Throws<InvalidMeshFileException>(() => PskReader.Read(buffer.ToArray()));
    }

    [Fact]
    public void SomethingThatIsNotAMeshFileIsRefused()
    {
        var rubbish = new byte[64];
        Array.Fill(rubbish, (byte)0x41);

        Assert.Throws<InvalidMeshFileException>(() => PskReader.Read(rubbish));
    }
}
