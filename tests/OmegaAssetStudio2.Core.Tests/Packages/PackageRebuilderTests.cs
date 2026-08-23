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
/// Checks that a package can be written back out with objects that changed
/// size.
/// </summary>
/// <remarks>
/// This is the operation that can ruin an installed game, so it is held to the
/// strongest check available: the rebuilt package is read back and every object
/// compared with what it held before. Nothing is written anywhere near a game
/// folder — the tests read real packages and write only to a temporary folder.
/// </remarks>
public sealed class RealPackageRebuilderTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oas2-rebuild-" + Guid.NewGuid().ToString("N"));

    public RealPackageRebuilderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static IEnumerable<string> SomePackages(GameClient client, int count) =>
        Directory.EnumerateFiles(client.CookedPath, "UC__*.upk").Take(count);

    /// <summary>Reads every object of a package, so two can be compared.</summary>
    private static List<byte[]> AllObjects(Package package)
    {
        var objects = new List<byte[]>(package.Exports.Count);

        for (int i = 0; i < package.Exports.Count; i++)
            objects.Add(package.GetExportData(i).ToArray());

        return objects;
    }

    [Fact]
    public void APackageRebuiltWithNoChangesHoldsExactlyWhatItHeld()
    {
        // Before anything is allowed to change size, rebuilding must be able to
        // change nothing. If this is wrong, everything built on it is wrong.
        List<GameClient> clients = TestGames.Installed.ToList();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedPackages = 0, refused = 0;

            foreach (string path in SomePackages(client, 25))
            {
                Package original;
                try { original = Package.Open(path); } catch (InvalidPackageException) { continue; }

                byte[] rebuilt;
                try { rebuilt = PackageRebuilder.Build(original, []); }
                catch (PackageRebuildException) { refused++; continue; }

                Package reopened = Package.Read(rebuilt, path);

                Assert.Equal(original.Exports.Count, reopened.Exports.Count);
                Assert.Equal(original.Names.Count, reopened.Names.Count);
                Assert.Equal(original.Imports.Count, reopened.Imports.Count);

                List<byte[]> before = AllObjects(original);
                List<byte[]> after = AllObjects(reopened);

                for (int i = 0; i < before.Count; i++)
                {
                    Assert.True(before[i].AsSpan().SequenceEqual(after[i]),
                        $"{Path.GetFileName(path)}: object {i} ({original.GetExportName(i)}) came back different.");
                }

                checkedPackages++;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {checkedPackages} packages rebuilt unchanged and re-read identically" +
                (refused > 0 ? $"; {refused} refused as unsafe to rebuild." : "."));

            Assert.True(checkedPackages > 0, $"{client.DisplayName}: nothing could be rebuilt.");
        }
    }

    [Fact]
    public void AnObjectCanGrowAndEverythingElseStillReadsCorrectly()
    {
        // The point of the whole exercise: making one object larger moves every
        // object after it, and all of them must still be found.
        foreach (GameClient client in TestGames.Installed)
        {
            int checkedPackages = 0;

            foreach (string path in SomePackages(client, 12))
            {
                Package original;
                try { original = Package.Open(path); } catch (InvalidPackageException) { continue; }

                // Something in the middle, so objects move both before and after.
                int index = original.Exports.Count / 2;
                if (original.Exports.Count < 3 || original.Exports[index].SerialSize == 0) continue;

                byte[] grown = [.. original.GetExportData(index).ToArray(), .. new byte[777]];

                byte[] rebuilt;
                try { rebuilt = PackageRebuilder.Build(original, [new ExportPatch(index, grown)]); }
                catch (PackageRebuildException) { continue; }

                Package reopened = Package.Read(rebuilt, path);

                Assert.Equal(grown.Length, reopened.Exports[index].SerialSize);
                Assert.True(reopened.GetExportData(index).SequenceEqual(grown),
                    $"{Path.GetFileName(path)}: the grown object did not come back as written.");

                // Every other object must be untouched, which is the part that
                // fails when offsets are rewritten wrongly.
                for (int i = 0; i < original.Exports.Count; i++)
                {
                    if (i == index) continue;

                    Assert.True(
                        original.GetExportData(i).SequenceEqual(reopened.GetExportData(i)),
                        $"{Path.GetFileName(path)}: object {i} ({original.GetExportName(i)}) moved wrongly.");
                }

                checkedPackages++;
                if (checkedPackages >= 5) break;
            }

            _output.WriteLine($"{client.DisplayName}: {checkedPackages} packages survived an object growing.");

            Assert.True(checkedPackages > 0, $"{client.DisplayName}: no package could be grown.");
            return;   // one client is enough for this
        }
    }

    [Fact]
    public void AnObjectCanShrinkAndEverythingElseStillReadsCorrectly()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            int checkedPackages = 0;

            foreach (string path in SomePackages(client, 12))
            {
                Package original;
                try { original = Package.Open(path); } catch (InvalidPackageException) { continue; }

                int index = original.Exports.Count / 2;
                if (original.Exports.Count < 3 || original.Exports[index].SerialSize < 64) continue;

                byte[] shrunk = original.GetExportData(index)[..32].ToArray();

                byte[] rebuilt;
                try { rebuilt = PackageRebuilder.Build(original, [new ExportPatch(index, shrunk)]); }
                catch (PackageRebuildException) { continue; }

                Package reopened = Package.Read(rebuilt, path);

                Assert.Equal(32, reopened.Exports[index].SerialSize);

                for (int i = 0; i < original.Exports.Count; i++)
                {
                    if (i == index) continue;

                    Assert.True(
                        original.GetExportData(i).SequenceEqual(reopened.GetExportData(i)),
                        $"{Path.GetFileName(path)}: object {i} moved wrongly after a shrink.");
                }

                checkedPackages++;
                if (checkedPackages >= 5) break;
            }

            _output.WriteLine($"{client.DisplayName}: {checkedPackages} packages survived an object shrinking.");

            Assert.True(checkedPackages > 0, $"{client.DisplayName}: no package could be shrunk.");
            return;
        }
    }

    [Fact]
    public void TheNamesAndReferencesOfEveryObjectSurvive()
    {
        // A rebuilt package must still describe the same things: an object
        // whose name or class moved would be a different object to the game.
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (string path in SomePackages(client, 8))
            {
                Package original;
                try { original = Package.Open(path); } catch (InvalidPackageException) { continue; }

                byte[] rebuilt;
                try { rebuilt = PackageRebuilder.Build(original, []); }
                catch (PackageRebuildException) { continue; }

                Package reopened = Package.Read(rebuilt, path);

                for (int i = 0; i < original.Exports.Count; i++)
                {
                    Assert.Equal(original.GetExportName(i), reopened.GetExportName(i));
                    Assert.Equal(original.GetExportClassName(i), reopened.GetExportClassName(i));
                    Assert.Equal(original.GetExportPath(i), reopened.GetExportPath(i));
                }
            }

            return;
        }
    }
}
