using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Reads real packages from installed clients when they are present.
/// </summary>
/// <remarks>
/// A synthetic fixture only proves the reader agrees with the fixture. These
/// tests prove it agrees with the game. They no-op when no install is present,
/// so the suite still passes elsewhere.
/// </remarks>
public sealed class RealPackageHeaderTests
{
    private readonly ITestOutputHelper _output;

    public RealPackageHeaderTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<string> CandidateRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [


        ];
    }

    private static List<GameClient> InstalledClients() =>
        CandidateRoots()
            .Where(Directory.Exists)
            .Select(r => GameClientLocator.FromRoot(r, new DirectoryInfo(r).Name))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    [Fact]
    public void ReadsEveryPackageInASampleFromEveryInstall()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string[] packages = Directory.EnumerateFiles(client.CookedPath, "*.upk").Take(250).ToArray();
            Assert.NotEmpty(packages);

            int compressed = 0;
            foreach (string path in packages)
            {
                PackageHeader header = PackageHeader.ReadFromFile(path);

                // Every field that shifts later reads must be sane, or the parse
                // silently drifted somewhere earlier in the header.
                Assert.True(header.NameCount >= 0, $"{path}: negative name count");
                Assert.True(header.ExportCount >= 0, $"{path}: negative export count");
                Assert.True(header.ImportCount >= 0, $"{path}: negative import count");
                Assert.True(header.NameOffset > 0, $"{path}: name offset {header.NameOffset}");
                Assert.True(header.TotalHeaderSize > 0, $"{path}: header size {header.TotalHeaderSize}");
                Assert.Equal(client.Format, header.Format);

                if (header.IsCompressed) compressed++;
            }

            _output.WriteLine(
                $"{client.DisplayName}: read {packages.Length} packages, format {client.Format}, " +
                $"{compressed} compressed.");
        }
    }

    [Fact]
    public void CompressedChunksCoverTheBodyContiguously()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk").Take(50))
            {
                PackageHeader header = PackageHeader.ReadFromFile(path);
                if (!header.IsCompressed) continue;

                // The first chunk must start where the header says the tables
                // begin, and each chunk must continue from the previous one. A
                // gap means the chunk table was misread and decompression would
                // produce a body with holes in it.
                Assert.Equal(header.NameOffset, header.Chunks[0].UncompressedOffset);

                for (int i = 1; i < header.Chunks.Count; i++)
                {
                    PackageChunk previous = header.Chunks[i - 1];
                    PackageChunk current = header.Chunks[i];

                    Assert.True(
                        current.UncompressedOffset == previous.UncompressedOffset + previous.UncompressedSize,
                        $"{path}: chunk {i} starts at {current.UncompressedOffset} but chunk {i - 1} " +
                        $"ends at {previous.UncompressedOffset + previous.UncompressedSize}.");

                    Assert.True(current.CompressedOffset > previous.CompressedOffset,
                        $"{path}: chunk {i} compressed offset moves backwards.");
                }

                long fileLength = new FileInfo(path).Length;
                PackageChunk last = header.Chunks[^1];
                Assert.True(last.CompressedOffset + last.CompressedSize <= fileLength,
                    $"{path}: last chunk runs past the end of the file.");
            }
        }
    }

    [Fact]
    public void EveryPackageInAnInstallReportsTheSameFormat()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            var formats = new HashSet<PackageFormat>();
            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk").Take(400))
                formats.Add(PackageHeader.ReadFromFile(path).Format);

            Assert.True(formats.Count == 1,
                $"{client.DisplayName} contains mixed package formats: " +
                string.Join(", ", formats.Select(f => f.ToString())));

            _output.WriteLine($"{client.DisplayName}: all sampled packages are {formats.Single()}.");
        }
    }
}
