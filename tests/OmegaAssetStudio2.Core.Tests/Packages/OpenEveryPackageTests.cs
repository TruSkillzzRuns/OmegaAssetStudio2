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
/// Opens every package in every install.
/// </summary>
/// <remarks>
/// Sampling hid a real defect: a decompression failure showed up intermittently
/// because different runs happened to sample different packages. Reading all of
/// them makes any such failure deterministic and names the file responsible.
/// </remarks>
public sealed class OpenEveryPackageTests
{
    private readonly ITestOutputHelper _output;

    public OpenEveryPackageTests(ITestOutputHelper output) => _output = output;

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
    public void EveryPackageOpens()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        var failures = new List<string>();

        foreach (GameClient client in clients)
        {
            string[] files = Directory.GetFiles(client.CookedPath, "*.upk");
            int opened = 0;

            foreach (string path in files)
            {
                try
                {
                    Package package = Package.Open(path);
                    _ = package.Exports.Count;
                    opened++;
                }
                catch (InvalidPackageException ex)
                {
                    failures.Add($"{client.DisplayName} / {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            _output.WriteLine($"{client.DisplayName}: opened {opened:N0} of {files.Length:N0} packages.");
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"=== {failures.Count} package(s) failed ===");
            foreach (string failure in failures.Take(20)) _output.WriteLine("    " + failure);
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} package(s) could not be opened. First: {failures.FirstOrDefault()}");
    }
}
