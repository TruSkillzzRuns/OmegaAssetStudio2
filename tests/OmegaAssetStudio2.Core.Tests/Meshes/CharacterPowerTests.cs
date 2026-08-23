using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Checks the skill list against real installs.
/// </summary>
/// <remarks>
/// The list is built from package names, so the risk is not that it comes back
/// empty — it is that it comes back full of the wrong things: another
/// character's skills, or packages that hold nothing worth editing. The checks
/// here are aimed at that.
/// </remarks>
public sealed class CharacterPowerTests
{
    private readonly ITestOutputHelper _output;

    public CharacterPowerTests(ITestOutputHelper output) => _output = output;

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
    public void MostHeroesHaveSkillsAndEverySkillBelongsToThem()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            // Distinct characters, not costumes: costumes of one hero share a
            // single set of skills, so counting them would flatter the result.
            List<string> tokens = CharacterRoster.Build(client, RosterCategory.Hero)
                .Select(h => h.Token)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            int withSkills = 0, totalSkills = 0, withRelated = 0;

            foreach (string token in tokens)
            {
                IReadOnlyList<PowerEntry> powers = CharacterPowers.Build(client, token);

                if (powers.Count > 0) withSkills++;
                totalSkills += powers.Count;
                withRelated += powers.Count(p => p.RelatedPackages.Count > 0);

                foreach (PowerEntry power in powers)
                {
                    // A skill listed under one character must not be another's.
                    Assert.Equal(token, power.CharacterToken);
                    Assert.Contains(token, Path.GetFileName(power.PackagePath), StringComparison.OrdinalIgnoreCase);

                    Assert.True(File.Exists(power.PackagePath),
                        $"{power.DisplayName} points at a file that is not there.");

                    Assert.False(string.IsNullOrWhiteSpace(power.DisplayName));

                    // The name must read as words, not as a file name.
                    Assert.DoesNotContain("UC__", power.DisplayName, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("_SF", power.DisplayName, StringComparison.OrdinalIgnoreCase);

                    foreach (string related in power.RelatedPackages)
                        Assert.True(File.Exists(related), $"{related} is listed but not there.");

                    // Every package is searched, so a duplicate would be read twice.
                    Assert.Equal(power.AllPackages.Count, power.AllPackages.Distinct().Count());
                }
            }

            double rate = tokens.Count == 0 ? 0 : withSkills / (double)tokens.Count;

            _output.WriteLine(
                $"{client.DisplayName}: {withSkills} of {tokens.Count} heroes have skills " +
                $"({rate:P0}); {totalSkills:N0} skills, {withRelated:N0} with related effect packages.");

            Assert.True(rate > 0.8, $"{client.DisplayName}: only {rate:P0} of heroes had any skill listed.");
        }
    }

    [Fact]
    public void SkillsLeadToColoursThatCanBeEdited()
    {
        // The point of the panel is that picking a skill shows something to
        // change. This walks the whole way: character, skill, packages, colours.
        foreach (GameClient client in InstalledClients())
        {
            string? token = CharacterRoster.Build(client, RosterCategory.Hero)
                .Select(h => h.Token)
                .FirstOrDefault(t => CharacterPowers.Build(client, t).Count > 0);

            if (token is null) continue;

            IReadOnlyList<PowerEntry> powers = CharacterPowers.Build(client, token);
            var catalog = new ColourCatalog();

            int withColour = 0, coloursFound = 0;

            foreach (PowerEntry power in powers.Take(12))
            {
                IReadOnlyList<ColourTarget> targets =
                    catalog.ScanFilesAsync(power.AllPackages).GetAwaiter().GetResult();

                if (targets.Count == 0) continue;

                withColour++;
                coloursFound += targets.Sum(t => t.Colours.Count);

                foreach (ColourTarget target in targets)
                {
                    Assert.NotEmpty(target.Colours);
                    Assert.Contains(target.PackagePath, power.AllPackages);
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {token} — {withColour} of {Math.Min(12, powers.Count)} skills " +
                $"had editable colour ({coloursFound:N0} colours).");

            Assert.True(withColour > 0,
                $"{client.DisplayName}: no skill of {token} led to a colour that could be edited.");
        }
    }
}
