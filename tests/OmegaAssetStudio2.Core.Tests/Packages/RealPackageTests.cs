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
/// Opens real packages end to end: header, decompression, all three tables, and
/// object-path resolution.
/// </summary>
public sealed class RealPackageTests
{
    private readonly ITestOutputHelper _output;

    public RealPackageTests(ITestOutputHelper output) => _output = output;

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
    public void OpensRealPackagesAndResolvesEveryReference()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string[] bySize = Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                       .OrderBy(p => new FileInfo(p).Length)
                                       .ToArray();

            var sample = bySize.Take(15)
                               .Concat(bySize.Skip(bySize.Length / 2).Take(15))
                               .Concat(bySize.TakeLast(5))
                               .Distinct()
                               .ToArray();

            long exports = 0, imports = 0;

            foreach (string path in sample)
            {
                Package package = Package.Open(path);

                Assert.Equal(package.Header.ExportCount, package.Exports.Count);
                Assert.Equal(package.Header.ImportCount, package.Imports.Count);
                Assert.Equal(package.Header.NameCount, package.Names.Count);

                // Every reference in both tables must land inside a table. An
                // off-by-one in the sign convention shows up here immediately.
                foreach (ImportEntry import in package.Imports.Entries)
                {
                    AssertResolvable(package, import.Outer, path);
                    Assert.InRange(import.ObjectName.Index, 0, package.Names.Count - 1);
                    Assert.InRange(import.ClassName.Index, 0, package.Names.Count - 1);
                    Assert.InRange(import.ClassPackage.Index, 0, package.Names.Count - 1);
                }

                foreach (ExportEntry export in package.Exports.Entries)
                {
                    AssertResolvable(package, export.Class, path);
                    AssertResolvable(package, export.Super, path);
                    AssertResolvable(package, export.Outer, path);
                    AssertResolvable(package, export.Archetype, path);
                    Assert.InRange(export.ObjectName.Index, 0, package.Names.Count - 1);
                }

                exports += package.Exports.Count;
                imports += package.Imports.Count;
            }

            _output.WriteLine(
                $"{client.DisplayName}: opened {sample.Length} packages, " +
                $"{exports:N0} exports and {imports:N0} imports all resolvable.");
        }
    }

    private static void AssertResolvable(Package package, ObjectReference reference, string path)
    {
        if (reference.IsNull) return;

        if (reference.IsExport)
            Assert.InRange(reference.ExportIndex, 0, package.Exports.Count - 1);
        else
            Assert.InRange(reference.ImportIndex, 0, package.Imports.Count - 1);
    }

    [Fact]
    public void ExportDataIsContiguousAndInBounds()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int checkedExports = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                             .OrderBy(p => new FileInfo(p).Length)
                                             .Take(40))
            {
                Package package = Package.Open(path);

                ExportEntry[] ordered = package.Exports.Entries
                                               .Where(e => e.SerialSize > 0)
                                               .OrderBy(e => e.SerialOffset)
                                               .ToArray();

                for (int i = 0; i < ordered.Length; i++)
                {
                    // Objects must not overlap. Overlap would mean the export
                    // table was misread, and any write based on it would corrupt
                    // a neighbouring object.
                    if (i > 0)
                    {
                        ExportEntry previous = ordered[i - 1];
                        Assert.True(
                            ordered[i].SerialOffset >= previous.SerialOffset + previous.SerialSize,
                            $"{Path.GetFileName(path)}: export data overlaps at offset {ordered[i].SerialOffset}.");
                    }
                }

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    ReadOnlySpan<byte> data = package.GetExportData(i);
                    Assert.Equal(package.Exports[i].SerialSize, data.Length);
                    checkedExports++;
                }
            }

            _output.WriteLine($"{client.DisplayName}: {checkedExports:N0} export payloads in bounds.");
        }
    }

    [Fact]
    public void ResolvesReadableClassNamesAndPaths()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            var classCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                             .OrderByDescending(p => new FileInfo(p).Length)
                                             .Take(3))
            {
                Package package = Package.Open(path);

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    string className = package.GetExportClassName(i);
                    string fullPath = package.GetExportPath(i);

                    Assert.False(string.IsNullOrWhiteSpace(fullPath),
                        $"{Path.GetFileName(path)}: export {i} resolved to an empty path.");

                    classCounts[className] = classCounts.GetValueOrDefault(className) + 1;
                }
            }

            string top = string.Join(", ", classCounts.OrderByDescending(kv => kv.Value)
                                                      .Take(8)
                                                      .Select(kv => $"{kv.Key} x{kv.Value:N0}"));
            _output.WriteLine($"{client.DisplayName}: {top}");
        }
    }
}
