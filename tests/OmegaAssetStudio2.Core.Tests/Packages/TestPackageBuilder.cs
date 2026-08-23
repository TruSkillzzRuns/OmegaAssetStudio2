using System;
using System.IO;
using OmegaAssetStudio2.Core.Packages;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Builds byte-accurate package headers for tests.
/// </summary>
/// <remarks>
/// Field order matches the layout decoded from real packages. Shared so that a
/// fixture cannot drift away from what the reader expects — an earlier version
/// of these tests wrote a stub header that no real reader would have accepted,
/// and it passed anyway.
/// </remarks>
public static class TestPackageBuilder
{
    public static byte[] Header(
        short fileVersion = 868,
        short licenseeVersion = 3,
        int generationCount = 1,
        int chunkCount = 0,
        int compression = 2,
        string folderName = "None",
        int nameCount = 1503,
        int nameOffset = 833,
        int exportCount = 1666,
        int importCount = 125)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(PackageHeader.Magic);
        writer.Write(fileVersion);
        writer.Write(licenseeVersion);
        writer.Write(167732);                       // total header size

        byte[] folder = System.Text.Encoding.ASCII.GetBytes(folderName + "\0");
        writer.Write(folder.Length);
        writer.Write(folder);

        writer.Write(0xA28A0009u);                  // package flags
        writer.Write(nameCount);
        writer.Write(nameOffset);
        writer.Write(exportCount);
        writer.Write(47628);                        // export offset
        writer.Write(importCount);
        writer.Write(44128);                        // import offset
        writer.Write(160556);                       // depends offset
        writer.Write(167732);                       // import/export guids offset
        writer.Write(0);                            // import guids count
        writer.Write(0);                            // export guids count
        writer.Write(0);                            // thumbnail table offset
        writer.Write(Guid.Parse("e06a6260-3244-89c0-f169-ca9ea6f840e1").ToByteArray());

        writer.Write(generationCount);
        for (int i = 0; i < generationCount; i++)
        {
            writer.Write(exportCount);
            writer.Write(nameCount);
            writer.Write(0);                        // net object count
        }

        writer.Write(10897);                        // engine version
        writer.Write(136);                          // cooker version
        writer.Write(compression);
        writer.Write(chunkCount);

        for (int i = 0; i < chunkCount; i++)
        {
            writer.Write(1000 + i);
            writer.Write(2000 + i);
            writer.Write(3000 + i);
            writer.Write(4000 + i);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>Writes a package file whose header is valid and readable.</summary>
    public static void WriteFile(string path, short fileVersion = 868, short licenseeVersion = 3)
        => File.WriteAllBytes(path, Header(fileVersion, licenseeVersion));
}
