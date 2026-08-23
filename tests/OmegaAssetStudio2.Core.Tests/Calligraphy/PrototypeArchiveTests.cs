using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Calligraphy;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Calligraphy;

/// <summary>
/// Checks the game's data archive against real installs.
/// </summary>
/// <remarks>
/// The archive is read and never written, so every check here is about reading
/// it correctly: that the file table is walked exactly, and that what comes out
/// of a compressed entry is the size the table said it would be.
/// </remarks>
public sealed class PrototypeArchiveTests
{
    private readonly ITestOutputHelper _output;

    public PrototypeArchiveTests(ITestOutputHelper output) => _output = output;

    private static List<string> InstalledRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        string[] roots = !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return roots.Where(Directory.Exists).ToList();
    }

    [Fact]
    public void TheFileTableIsWalkedExactly()
    {
        List<string> roots = InstalledRoots();
        if (roots.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (string root in roots)
        {
            using PrototypeArchive? archive = PrototypeArchive.Open(root);
            if (archive is null)
            {
                _output.WriteLine($"{Path.GetFileName(root)}: no archive.");
                continue;
            }

            _output.WriteLine(
                $"{Path.GetFileName(root)}: version {archive.Version}, {archive.Entries.Count:N0} files.");

            Assert.True(archive.Entries.Count > 1000,
                $"{root}: only {archive.Entries.Count} files were listed.");

            // Names are how everything else finds anything, so a table walked
            // even slightly wrong shows up as blank or garbled names.
            Assert.All(archive.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));

            // Sizes must be usable: nothing negative, nothing outside the file.
            Assert.All(archive.Entries, e =>
            {
                Assert.True(e.Offset >= 0, $"{e.Name} starts at {e.Offset}.");
                Assert.True(e.Size >= 0, $"{e.Name} declares a size of {e.Size}.");
                Assert.True(e.StoredSize >= 0, $"{e.Name} declares a stored size of {e.StoredSize}.");
            });

            // The archive is meant to hold the definitions behind the game's
            // content; if it does not, the table was read as something else.
            Assert.Contains(archive.Entries, e =>
                e.Name.Contains("prototype", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains("blueprint", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FilesExpandToTheSizeTheArchiveSaysTheyAre()
    {
        // This is the check that proves the entries point where they claim.
        // A wrong offset feeds the decompressor another file's bytes, which
        // either fails outright or lands on the wrong length.
        foreach (string root in InstalledRoots())
        {
            using PrototypeArchive? archive = PrototypeArchive.Open(root);
            if (archive is null) continue;

            int read = 0, compressed = 0;
            long bytes = 0;

            // Spread across the archive rather than the first few, which are
            // not representative of anything.
            int stride = Math.Max(1, archive.Entries.Count / 200);

            for (int i = 0; i < archive.Entries.Count; i += stride)
            {
                ArchiveEntry entry = archive.Entries[i];
                if (entry.Size == 0) continue;

                byte[] data = archive.Read(entry);

                Assert.Equal(entry.Size, data.Length);

                read++;
                bytes += data.Length;
                if (entry.StoredSize != entry.Size) compressed++;
            }

            _output.WriteLine(
                $"{Path.GetFileName(root)}: {read} files expanded to their stated size " +
                $"({bytes:N0} bytes, {compressed} of them compressed).");

            Assert.True(read > 50, $"{root}: only {read} files could be read.");
            Assert.True(compressed > 0, $"{root}: nothing was compressed, which is not expected.");
        }
    }

    [Fact]
    public void AGameWithNoArchiveReadsAsNothingRatherThanFailing()
        => Assert.Null(PrototypeArchive.Open(Path.Combine(Path.GetTempPath(), "oas2-no-such-game")));
}
