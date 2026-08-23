using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

/// <summary>
/// Checks the index of what every package exports.
/// </summary>
/// <remarks>
/// The index exists to answer one question — which file holds this object — and
/// the way it can quietly be wrong is by answering with a different object of
/// the same name. So the checks here are about the answer being the right one,
/// not merely an answer.
/// </remarks>
public sealed class PackageIndexTests
{
    private readonly ITestOutputHelper _output;

    public PackageIndexTests(ITestOutputHelper output) => _output = output;

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
    public void EveryAnswerPointsAtAnObjectOfThatExactPath()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            PackageIndex index = TestGames.IndexFor(client);

            _output.WriteLine(
                $"{client.DisplayName}: {index.Count:N0} materials and textures across " +
                $"{index.PackagesRead:N0} packages ({index.PackagesSkipped} unreadable).");

            Assert.True(index.Count > 0, $"{client.DisplayName}: the index came back empty.");

            // Take a spread of real reference paths from character packages and
            // check each answer opens to an object with that same path. A wrong
            // answer here is a model painted with somebody else's texture.
            int checkedPaths = 0;

            foreach (string upk in Directory.EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk").Take(12))
            {
                Package package;
                try { package = Package.Open(upk); } catch (InvalidPackageException) { continue; }

                for (int i = 0; i < package.Imports.Count && checkedPaths < 60; i++)
                {
                    string importPath;
                    try { importPath = package.GetImportPath(i); }
                    catch (InvalidPackageException) { continue; }

                    ObjectLocation? location = index.Find(importPath);
                    if (location is null) continue;

                    Package owner;
                    try { owner = Package.Open(location.Value.PackagePath); }
                    catch (InvalidPackageException) { continue; }

                    Assert.InRange(location.Value.ExportIndex, 0, owner.Exports.Count - 1);

                    // Compared without case: names resolved through an import
                    // come back lower-cased, while an export keeps the casing it
                    // was stored with. The index is keyed the same way, so this
                    // difference never affects a lookup.
                    Assert.Equal(
                        importPath,
                        owner.GetExportPath(location.Value.ExportIndex),
                        StringComparer.OrdinalIgnoreCase);

                    checkedPaths++;
                }
            }

            _output.WriteLine($"    {checkedPaths} references followed to an object with the same path.");
            Assert.True(checkedPaths > 0, $"{client.DisplayName}: no reference could be followed.");
        }
    }

    [Fact]
    public void AReferenceStaysHomeWhenItsOwnPackageHasTheObject()
    {
        // Cooked packages import objects they also contain. Reaching across the
        // folder for one of those would open a file for nothing, and could pick
        // a different package's copy.
        foreach (GameClient client in InstalledClients())
        {
            var locator = new ObjectLocator(TestGames.IndexFor(client));

            string? upk = Directory.EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*_SF.upk").FirstOrDefault();
            if (upk is null) continue;

            Package package;
            try { package = Package.Open(upk); } catch (InvalidPackageException) { continue; }

            int local = 0;

            for (int i = 0; i < package.Exports.Count && local < 20; i++)
            {
                LocatedObject? found = locator.TryLocate(package, new ObjectReference(i + 1));
                if (found is null) continue;

                Assert.False(found.Value.CameFromElsewhere);
                Assert.Same(package, found.Value.Package);
                local++;
            }

            Assert.True(local > 0, $"{client.DisplayName}: nothing local resolved.");
            return;   // one client is enough for this
        }
    }
}
