using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Audio;

/// <summary>
/// Costumes that change a character's voice, and where those lines are kept.
/// </summary>
public sealed class CostumeVoiceTests
{
    /// <summary>The install container these checks read, named as the game names it.</summary>
    private const string Subject = "Thor";

    private readonly ITestOutputHelper _output;

    public CostumeVoiceTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// A costume's lines are found, and they are that costume's own.
    /// </summary>
    /// <remarks>
    /// The check that matters is the second one. Finding sounds proves only
    /// that something was found; two costumes returning the same sounds would
    /// mean the choice does nothing. One character's sets are named after the
    /// costume being worn rather than the character, so they must not overlap.
    /// </remarks>
    [Fact]
    public void EachCostumeHasItsOwnLines()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            SoundNameCatalog catalog = SoundNameCatalog.LoadOrBuild(client);

            IReadOnlyList<CostumeVoice> costumes = CostumeVoices.For(client, Subject);
            if (costumes.Count == 0) continue;

            _output.WriteLine($"{client.DisplayName}: {Subject} has {costumes.Count} voice sets.");

            var byCostume = new Dictionary<string, HashSet<uint>>();

            foreach (CostumeVoice costume in costumes)
            {
                var clock = Stopwatch.StartNew();
                IReadOnlyList<PlacedSound> sounds = CostumeVoices.Sounds(client, costume, "INT", catalog);
                clock.Stop();

                byCostume[costume.Costume] = sounds.Select(s => s.Entry.Id).ToHashSet();

                _output.WriteLine(
                    $"   {costume.Costume,-14} {sounds.Count,5:N0} sounds in " +
                    $"{string.Join(", ", sounds.Select(s => s.ContainerName).Distinct().Take(3))}, " +
                    $"{clock.ElapsedMilliseconds:N0} ms. e.g. {sounds.FirstOrDefault()?.Name}");

                Assert.NotEmpty(sounds);
            }

            // No two sets may be the same, or choosing between them is theatre.
            foreach (string one in byCostume.Keys)
            {
                foreach (string other in byCostume.Keys)
                {
                    if (one == other) continue;

                    int shared = byCostume[one].Intersect(byCostume[other]).Count();

                    Assert.True(shared * 2 < byCostume[one].Count,
                        $"{one} and {other} share {shared} of {byCostume[one].Count} sounds.");
                }
            }

            return;
        }
    }

    /// <summary>Characters without a separate voice set report none.</summary>
    [Fact]
    public void CharactersWithoutCostumeVoicesReportNone()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0) return;

        foreach (GameClient client in clients)
        {
            foreach (string hero in new[] { "Angela", "Storm" })
            {
                IReadOnlyList<CostumeVoice> costumes = CostumeVoices.For(client, hero);

                _output.WriteLine($"{client.DisplayName}: {hero} has {costumes.Count} voice sets.");
                Assert.Empty(costumes);
            }

            return;
        }
    }
}
