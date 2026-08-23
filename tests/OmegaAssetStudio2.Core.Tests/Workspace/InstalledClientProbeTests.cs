using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

/// <summary>
/// Probes real game installs when they are present on this machine.
/// </summary>
/// <remarks>
/// Every test here no-ops when the folder is absent, so the suite still passes
/// on a machine without the game. The point is to catch the class of bug that
/// unit tests cannot: a layout assumption that is simply wrong about how the
/// game is actually installed.
/// <para>
/// Set OAS2_CLIENT_ROOTS to a semicolon-separated list of install folders to
/// probe your own; otherwise the defaults below are used.
/// </para>
/// </remarks>
public sealed class InstalledClientProbeTests
{
    private readonly ITestOutputHelper _output;

    public InstalledClientProbeTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<string> CandidateRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [


        ];
    }

    private List<GameClient> ResolveInstalled()
    {
        var found = new List<GameClient>();
        foreach (string root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;

            GameClient? client = GameClientLocator.FromRoot(root, new DirectoryInfo(root).Name);
            if (client is not null) found.Add(client);
        }
        return found;
    }

    [Fact]
    public void EveryPresentInstallResolvesToAReadableClient()
    {
        string[] present = CandidateRoots().Where(Directory.Exists).ToArray();
        if (present.Length == 0)
        {
            _output.WriteLine("No game installs on this machine; nothing probed.");
            return;
        }

        foreach (string root in present)
        {
            GameClient? client = GameClientLocator.FromRoot(root, new DirectoryInfo(root).Name);

            Assert.True(client is not null,
                $"An install exists at '{root}' but no cooked content folder was found under it.");
            Assert.True(client!.Exists, $"Resolved cooked path does not exist: {client.CookedPath}");
            Assert.True(client.Format.IsKnown,
                $"Could not read a package format from {client.CookedPath}.");

            _output.WriteLine(
                $"{client.DisplayName}\n" +
                $"    cooked   : {client.CookedPath}\n" +
                $"    format   : {client.Format}\n" +
                $"    manifest : {client.HasTextureCacheManifest}");
        }
    }

    [Fact]
    public void InstallsWithMatchingFormatsAreReportedCompatible()
    {
        List<GameClient> clients = ResolveInstalled();
        if (clients.Count < 2)
        {
            _output.WriteLine("Fewer than two installs present; nothing to compare.");
            return;
        }

        foreach (GameClient a in clients)
        {
            foreach (GameClient b in clients)
            {
                if (ReferenceEquals(a, b)) continue;

                bool expected = a.Format.FileVersion == b.Format.FileVersion
                             && a.Format.LicenseeVersion == b.Format.LicenseeVersion;

                Assert.Equal(expected, a.Format.IsCompatibleWith(b.Format));

                _output.WriteLine(
                    $"{a.DisplayName} ({a.Format})  vs  {b.DisplayName} ({b.Format})  ->  " +
                    (expected ? "same format" : "different format"));
            }
        }
    }

    [Fact]
    public void EachInstallHasADistinctCookedFolder()
    {
        List<GameClient> clients = ResolveInstalled();
        if (clients.Count < 2)
        {
            _output.WriteLine("Fewer than two installs present; nothing to compare.");
            return;
        }

        // Two configured installs resolving to one folder would mean edits made
        // "to one client" silently land in the other.
        string[] cookedPaths = clients.Select(c => c.CookedPath).ToArray();
        Assert.Equal(cookedPaths.Length, cookedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
