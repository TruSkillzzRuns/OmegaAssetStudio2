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
/// Measures how the game's own packages are stored.
/// </summary>
/// <remarks>
/// This application writes packages uncompressed on the grounds that the engine
/// reads either form. That was never checked against the game's own content,
/// and a package the game will not load is exactly what a hang at a loading
/// screen looks like. So: does this game ship any uncompressed package at all,
/// and if it does, how is its header laid out compared to what is written here.
/// </remarks>
public sealed class RealCompressionSurveyTests
{
    private readonly ITestOutputHelper _output;

    public RealCompressionSurveyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HowAreTheGamesOwnPackagesStored()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            string[] files = Directory
                .EnumerateFiles(client.CookedPath, "*.upk", SearchOption.TopDirectoryOnly)
                .ToArray();

            int compressed = 0, plain = 0, unreadable = 0;
            var flags = new Dictionary<uint, int>();
            var plainExamples = new List<string>();
            var compressedExamples = new List<string>();

            foreach (string file in files)
            {
                PackageHeader header;

                try
                {
                    // Only the header is needed, and expanding every body would
                    // take far longer than this is worth.
                    byte[] start = ReadStart(file, 4096);
                    header = PackageHeader.Read(start);
                }
                catch (Exception) { unreadable++; continue; }

                flags[(uint)header.Compression] = flags.GetValueOrDefault((uint)header.Compression) + 1;

                if (header.Chunks.Count > 0)
                {
                    compressed++;
                    if (compressedExamples.Count < 3) compressedExamples.Add(Path.GetFileName(file));
                }
                else
                {
                    plain++;
                    if (plainExamples.Count < 8) plainExamples.Add(Path.GetFileName(file));
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {files.Length:N0} packages — {compressed:N0} compressed, " +
                $"{plain:N0} stored plainly, {unreadable:N0} could not be read.");

            foreach ((uint flag, int count) in flags.OrderByDescending(f => f.Value))
                _output.WriteLine($"    compression flag 0x{flag:X8}: {count:N0}");

            if (plainExamples.Count > 0)
                _output.WriteLine("    stored plainly, for example: " + string.Join(", ", plainExamples));

            if (compressedExamples.Count > 0)
                _output.WriteLine("    compressed, for example: " + string.Join(", ", compressedExamples));

            return;
        }

        _output.WriteLine("No installs present; nothing measured.");
    }

    private static byte[] ReadStart(string path, int count)
    {
        using var stream = File.OpenRead(path);

        byte[] buffer = new byte[(int)Math.Min(count, stream.Length)];
        stream.ReadExactly(buffer);

        return buffer;
    }
}
