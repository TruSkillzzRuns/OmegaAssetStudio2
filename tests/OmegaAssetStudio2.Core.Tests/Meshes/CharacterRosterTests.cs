using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks the roster against real installs.
/// </summary>
/// <remarks>
/// A roster built from names is only worth showing if the entries lead
/// somewhere, so the checks here are about usefulness rather than shape: the
/// files exist, the categories do not overlap, and an entry picked at random
/// actually opens and holds a model.
/// </remarks>
public sealed class CharacterRosterTests
{
    private readonly ITestOutputHelper _output;

    public CharacterRosterTests(ITestOutputHelper output) => _output = output;

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
    public void EveryCategoryFillsAndPointsAtFilesThatExist()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            var counts = new List<string>();

            foreach (RosterCategory category in Enum.GetValues<RosterCategory>())
            {
                IReadOnlyList<RosterEntry> entries = CharacterRoster.Build(client, category);
                counts.Add($"{category} {entries.Count:N0}");

                Assert.True(entries.Count > 0, $"{client.DisplayName}: {category} came back empty.");

                foreach (RosterEntry entry in entries.Take(50))
                {
                    Assert.True(File.Exists(entry.PackagePath),
                        $"{entry.DisplayName} points at a file that is not there.");
                    Assert.False(string.IsNullOrWhiteSpace(entry.Character),
                        $"{entry.PackagePath} produced an entry with no name.");
                    Assert.Equal(category, entry.Category);
                }
            }

            _output.WriteLine($"{client.DisplayName}: {string.Join(", ", counts)}");
        }
    }

    [Fact]
    public void BossesAndEnemiesDoNotOverlap()
    {
        // Both come from the same family of packages, separated only by name.
        // An entry appearing in both would show the same character twice under
        // two headings, which is the failure a name-based split invites.
        foreach (GameClient client in InstalledClients())
        {
            HashSet<string> bosses = CharacterRoster.Build(client, RosterCategory.Boss)
                .Select(e => e.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            HashSet<string> enemies = CharacterRoster.Build(client, RosterCategory.Enemy)
                .Select(e => e.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            bosses.IntersectWith(enemies);

            Assert.True(bosses.Count == 0,
                $"{client.DisplayName}: {bosses.Count} package(s) are listed as both a boss and an enemy.");
        }
    }

    [Fact]
    public void HeroesAreNamedAsWordsNotFileNames()
    {
        foreach (GameClient client in InstalledClients())
        {
            IReadOnlyList<RosterEntry> heroes = CharacterRoster.Build(client, RosterCategory.Hero);

            foreach (RosterEntry hero in heroes)
            {
                Assert.DoesNotContain("UC__", hero.DisplayName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("_SF", hero.DisplayName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain('_', hero.DisplayName);
            }

            _output.WriteLine(
                $"{client.DisplayName}: {string.Join(", ", heroes.Take(6).Select(h => h.DisplayName))}…");
        }
    }

    [Fact]
    public void MostHeroesLeadToAModelThatCanBeDrawn()
    {
        // The point of the panel is that clicking an entry shows something.
        // Not every package holds a model — a few heroes are assembled from
        // parts elsewhere — so this asserts the common case, not perfection.
        foreach (GameClient client in InstalledClients())
        {
            IReadOnlyList<RosterEntry> heroes = CharacterRoster.Build(client, RosterCategory.Hero);
            if (heroes.Count == 0) continue;

            int checkedEntries = 0, drawable = 0;

            foreach (RosterEntry hero in heroes.Take(25))
            {
                checkedEntries++;

                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                    if (mesh?.HighestDetail is not { HasGeometry: true }) continue;

                    drawable++;
                    break;
                }
            }

            double rate = drawable / (double)checkedEntries;
            _output.WriteLine(
                $"{client.DisplayName}: {drawable} of {checkedEntries} heroes opened to a drawable model ({rate:P0}).");

            Assert.True(rate > 0.8,
                $"{client.DisplayName}: only {rate:P0} of heroes led to a model that could be drawn.");
        }
    }
}
