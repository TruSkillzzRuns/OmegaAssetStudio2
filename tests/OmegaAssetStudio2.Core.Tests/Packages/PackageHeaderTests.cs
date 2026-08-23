using System;
using System.IO;
using OmegaAssetStudio2.Core.Packages;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Packages;

public sealed class PackageHeaderTests
{
    private static byte[] BuildHeader(
        short fileVersion = 868,
        short licenseeVersion = 3,
        int generationCount = 1,
        int chunkCount = 0,
        int compression = 2,
        string folderName = "None")
        => TestPackageBuilder.Header(fileVersion, licenseeVersion, generationCount, chunkCount, compression, folderName);

    [Fact]
    public void ReadsEveryFieldAtTheRightOffset()
    {
        PackageHeader header = PackageHeader.Read(BuildHeader());

        Assert.Equal(868, header.FileVersion);
        Assert.Equal(3, header.LicenseeVersion);
        Assert.Equal(167732, header.TotalHeaderSize);
        Assert.Equal("None", header.FolderName);
        Assert.Equal(0xA28A0009u, header.PackageFlags);
        Assert.Equal(1503, header.NameCount);
        Assert.Equal(833, header.NameOffset);
        Assert.Equal(1666, header.ExportCount);
        Assert.Equal(47628, header.ExportOffset);
        Assert.Equal(125, header.ImportCount);
        Assert.Equal(44128, header.ImportOffset);
        Assert.Equal(160556, header.DependsOffset);
        Assert.Equal(10897, header.EngineVersion);
        Assert.Equal(136, header.CookerVersion);
        Assert.Equal(PackageCompression.Lzo, header.Compression);
    }

    [Fact]
    public void GenerationCountShiftsEveryFieldAfterIt()
    {
        // Generations are variable-length. Getting this wrong reads the engine
        // version out of the middle of the generation table and everything after
        // it is garbage — which is exactly the kind of silent misparse that ends
        // up written back into a package.
        PackageHeader one = PackageHeader.Read(BuildHeader(generationCount: 1));
        PackageHeader three = PackageHeader.Read(BuildHeader(generationCount: 3));

        Assert.Equal(10897, one.EngineVersion);
        Assert.Equal(10897, three.EngineVersion);
        Assert.Equal(136, three.CookerVersion);
    }

    [Fact]
    public void FolderNameLengthShiftsEveryFieldAfterIt()
    {
        PackageHeader header = PackageHeader.Read(BuildHeader(folderName: "SomethingLonger"));

        Assert.Equal("SomethingLonger", header.FolderName);
        Assert.Equal(1503, header.NameCount);
        Assert.Equal(10897, header.EngineVersion);
    }

    [Fact]
    public void ReadsCompressedChunks()
    {
        PackageHeader header = PackageHeader.Read(BuildHeader(chunkCount: 3));

        Assert.Equal(3, header.Chunks.Count);
        Assert.True(header.IsCompressed);
        Assert.Equal(new PackageChunk(1000, 2000, 3000, 4000), header.Chunks[0]);
        Assert.Equal(new PackageChunk(1002, 2002, 3002, 4002), header.Chunks[2]);
    }

    [Fact]
    public void UncompressedPackageIsNotReportedAsCompressed()
    {
        PackageHeader header = PackageHeader.Read(BuildHeader(compression: 0, chunkCount: 0));

        Assert.Equal(PackageCompression.None, header.Compression);
        Assert.False(header.IsCompressed);
    }

    [Fact]
    public void RejectsSomethingThatIsNotAPackage()
    {
        byte[] notAPackage = System.Text.Encoding.ASCII.GetBytes("this is not a package at all");

        var ex = Assert.Throws<InvalidPackageException>(() => PackageHeader.Read(notAPackage));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsATruncatedHeaderWithTheOffsetNamed()
    {
        byte[] full = BuildHeader();
        byte[] truncated = full[..40];

        var ex = Assert.Throws<InvalidPackageException>(() => PackageHeader.Read(truncated));
        Assert.Contains("offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnAbsurdChunkCountRatherThanAllocatingForIt()
    {
        byte[] header = BuildHeader(chunkCount: 0);
        // Overwrite the chunk count with a hostile value.
        BitConverter.GetBytes(int.MaxValue).CopyTo(header, header.Length - 4);

        Assert.Throws<InvalidPackageException>(() => PackageHeader.Read(header));
    }

    [Fact]
    public void FormatMatchesTheVersionFields()
    {
        PackageHeader header = PackageHeader.Read(BuildHeader(fileVersion: 894, licenseeVersion: 3));

        Assert.Equal(894, header.Format.FileVersion);
        Assert.Equal(3, header.Format.LicenseeVersion);
    }
}
