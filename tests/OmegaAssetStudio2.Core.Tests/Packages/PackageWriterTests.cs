using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Round-trips real packages through the writer.
/// </summary>
/// <remarks>
/// The decisive test is that a package written back out re-reads identically:
/// same tables, same names, same export bytes. If any offset were disturbed by
/// dropping compression, the re-read would fail or produce different data.
/// </remarks>
public sealed class PackageWriterTests
{
    private readonly ITestOutputHelper _output;

    public PackageWriterTests(ITestOutputHelper output) => _output = output;

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
    public void RewrittenPackagesReadBackIdentically()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedPackages = 0;
            long bytesWritten = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk")
                                             .OrderBy(p => new FileInfo(p).Length)
                                             .Take(10))
            {
                Package original = Package.Open(path);

                byte[] rewritten = PackageWriter.Build(original, []);
                Package reloaded = Package.Read(rewritten, path);

                Assert.Equal(original.Names.Count, reloaded.Names.Count);
                Assert.Equal(original.Imports.Count, reloaded.Imports.Count);
                Assert.Equal(original.Exports.Count, reloaded.Exports.Count);
                Assert.True(reloaded.Header.IsCompressed, "The rewritten package was not packed the way it arrived.");

                for (int i = 0; i < original.Names.Count; i++)
                    Assert.Equal(original.Names.GetName(i), reloaded.Names.GetName(i));

                for (int i = 0; i < original.Exports.Count; i++)
                {
                    Assert.Equal(original.GetExportPath(i), reloaded.GetExportPath(i));
                    Assert.Equal(original.GetExportClassName(i), reloaded.GetExportClassName(i));

                    // The bytes of every object must survive untouched.
                    Assert.True(
                        original.GetExportData(i).SequenceEqual(reloaded.GetExportData(i)),
                        $"{Path.GetFileName(path)}: export {i} changed when rewritten.");
                }

                checkedPackages++;
                bytesWritten += rewritten.Length;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {checkedPackages} packages rewritten and re-read identically " +
                $"({bytesWritten:N0} bytes).");

            Assert.True(checkedPackages > 0);
        }
    }

    [Fact]
    public void APatchedExportComesBackWithTheNewBytes()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        GameClient client = clients[0];
        string path = Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk")
                               .OrderBy(p => new FileInfo(p).Length)
                               .First();

        Package original = Package.Open(path);

        // Choose an export with a property block and flip one byte deep inside
        // its payload, past anything structural.
        int target = -1;
        for (int i = 0; i < original.Exports.Count; i++)
        {
            if (original.Exports[i].SerialSize > 256) { target = i; break; }
        }
        Assert.True(target >= 0, "No export large enough to patch.");

        byte[] patched = original.GetExportData(target).ToArray();
        int flipAt = patched.Length - 8;
        patched[flipAt] ^= 0xFF;

        byte[] rewritten = PackageWriter.Build(
            original, [new ExportPatch(target, patched)]);

        Package reloaded = Package.Read(rewritten, path);

        Assert.Equal(patched[flipAt], reloaded.GetExportData(target)[flipAt]);

        // Everything else must be untouched.
        for (int i = 0; i < original.Exports.Count; i++)
        {
            if (i == target) continue;
            Assert.True(original.GetExportData(i).SequenceEqual(reloaded.GetExportData(i)),
                $"Export {i} changed when a different export was patched.");
        }

        _output.WriteLine(
            $"Patched export {target} of {Path.GetFileName(path)} and every other export was preserved.");
    }

    [Fact]
    public void ASizeChangingPatchIsRefused()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        string path = Directory.EnumerateFiles(clients[0].CookedPath, "ICO__*.upk")
                               .OrderBy(p => new FileInfo(p).Length)
                               .First();

        Package package = Package.Open(path);
        int target = Enumerable.Range(0, package.Exports.Count)
                               .First(i => package.Exports[i].SerialSize > 16);

        byte[] tooShort = new byte[package.Exports[target].SerialSize - 1];

        // Silently accepting this would shift every later object in the file.
        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageWriter.Build(package, [new ExportPatch(target, tooShort)]));

        Assert.Contains("Size-changing", ex.Message);
    }

    [Fact]
    public void PropertiesStillParseAfterARewrite()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string path = Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk")
                                   .OrderBy(p => new FileInfo(p).Length)
                                   .First();

            Package original = Package.Open(path);
            Package reloaded = Package.Read(PackageWriter.Build(original, []), path);

            int compared = 0;
            for (int i = 0; i < original.Exports.Count; i++)
            {
                PropertyBag? before = original.TryReadProperties(i);
                PropertyBag? after = reloaded.TryReadProperties(i);

                Assert.Equal(before is null, after is null);
                if (before is null || after is null) continue;

                Assert.Equal(before.Tags.Count, after.Tags.Count);
                for (int t = 0; t < before.Tags.Count; t++)
                {
                    Assert.Equal(before.Tags[t].Name, after.Tags[t].Name);
                    Assert.Equal(before.Tags[t].TypeName, after.Tags[t].TypeName);
                }
                compared++;
            }

            _output.WriteLine($"{client.DisplayName}: {compared} objects' properties identical after a rewrite.");
            Assert.True(compared > 0);
        }
    }
}
