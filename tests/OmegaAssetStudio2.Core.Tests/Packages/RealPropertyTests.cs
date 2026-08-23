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
/// Reads tagged properties out of real objects.
/// </summary>
/// <remarks>
/// The property block ends with a name of "none". If any type's layout is wrong
/// the stream desynchronises and that terminator is never found, so "it parsed
/// at all" is itself a strong correctness signal — and reaching it across tens of
/// thousands of objects is a stronger one.
/// </remarks>
public sealed class RealPropertyTests
{
    private readonly ITestOutputHelper _output;

    public RealPropertyTests(ITestOutputHelper output) => _output = output;

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
    public void TexturesExposeTheirDimensions()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int textures = 0;
            var formats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(6))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    PropertyBag? properties = package.TryReadProperties(index);
                    Assert.NotNull(properties);

                    int width = properties!.GetInt("SizeX");
                    int height = properties.GetInt("SizeY");

                    Assert.True(width > 0 && width <= 8192,
                        $"{Path.GetFileName(path)}: texture {package.GetExportName(index)} has width {width}.");
                    Assert.True(height > 0 && height <= 8192,
                        $"{Path.GetFileName(path)}: texture {package.GetExportName(index)} has height {height}.");

                    // Deliberately NOT asserting power-of-two dimensions. An
                    // earlier version of this test did, and real UI icons in the
                    // game fail it — the assumption was wrong, not the data.

                    string format = properties.GetName("Format", "(none)");
                    formats[format] = formats.GetValueOrDefault(format) + 1;

                    textures++;
                }
            }

            if (textures == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no icon packages found.");
                continue;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {textures} textures — " +
                string.Join(", ", formats.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
        }
    }

    [Fact]
    public void PropertyBlocksParseAcrossEveryObjectClass()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int parsed = 0, unparsed = 0, withProperties = 0;
            var failedClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                             .OrderByDescending(p => new FileInfo(p).Length)
                                             .Take(3))
            {
                Package package = Package.Open(path);

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    if (package.Exports[i].SerialSize < sizeof(int) * 3) continue;

                    PropertyBag? properties = package.TryReadProperties(i);
                    if (properties is null)
                    {
                        unparsed++;
                        string className = package.GetExportClassName(i);
                        failedClasses[className] = failedClasses.GetValueOrDefault(className) + 1;
                        continue;
                    }

                    parsed++;
                    if (properties.Tags.Count > 0) withProperties++;

                    // The payload must sit inside the object, or the block was
                    // misread and any binary data after it would be read wrong.
                    Assert.InRange(properties.PayloadOffset, 0, package.Exports[i].SerialSize);
                }
            }

            double rate = parsed / (double)Math.Max(1, parsed + unparsed);
            _output.WriteLine(
                $"{client.DisplayName}: {parsed:N0} parsed ({withProperties:N0} with properties), " +
                $"{unparsed:N0} not, {rate:P1} success.");

            if (failedClasses.Count > 0)
            {
                _output.WriteLine("    did not parse: " + string.Join(", ",
                    failedClasses.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}")));
            }

            // The vast majority of engine objects begin with a property block.
            Assert.True(rate > 0.90, $"{client.DisplayName}: only {rate:P1} of objects parsed.");
        }
    }

    [Fact]
    public void MaterialParametersExposeNamesAndValues()
    {
        // These are the properties a recolouring tool edits.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int found = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                             .OrderByDescending(p => new FileInfo(p).Length)
                                             .Take(3))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("materialexpressionscalarparameter"))
                {
                    PropertyBag? properties = package.TryReadProperties(index);
                    if (properties is null) continue;

                    if (properties.Contains("ParameterName") || properties.Contains("DefaultValue"))
                        found++;
                }
            }

            _output.WriteLine($"{client.DisplayName}: {found} scalar parameters with readable values.");
            Assert.True(found > 0, $"{client.DisplayName}: no material parameters were readable.");
        }
    }
}
