using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Tests;

/// <summary>
/// The installed games the tests run against, and the expensive things built
/// from them.
/// </summary>
/// <remarks>
/// Tests here read real installs rather than fixtures, which is what makes them
/// worth having — nearly every format mistake this project has made was caught
/// by real bytes disagreeing. The cost is that some of what they need is slow
/// to produce, so anything that takes seconds is built once and shared.
/// </remarks>
public static class TestGames
{
    private static readonly ConcurrentDictionary<string, PackageIndex> Indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installs present on this machine, or none.</summary>
    public static IReadOnlyList<GameClient> Installed { get; } = Discover();

    private static List<GameClient> Discover()
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
    /// The index of what every package in a game holds, built once per run.
    /// </summary>
    /// <remarks>
    /// Building it reads every package in the folder — some fifteen thousand of
    /// them — and several tests need it. Built per test it was the single
    /// largest cost in the suite; built once it is paid for by the first test
    /// that asks.
    /// </remarks>
    public static PackageIndex IndexFor(GameClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return Indexes.GetOrAdd(client.CookedPath, _ => PackageIndex.Build(client));
    }
}
