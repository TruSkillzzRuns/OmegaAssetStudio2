using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Materials;

public sealed class MaterialColourTests
{
    [Fact]
    public void ConvertsBetweenFloatsAndBytes()
    {
        MaterialColour colour = MaterialColour.FromBytes(255, 128, 0, 255);

        (byte r, byte g, byte b, byte a) = colour.ToBytes();
        Assert.Equal(255, r);
        Assert.InRange(g, 127, 129);
        Assert.Equal(0, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void OverbrightColoursAreNotClamped()
    {
        // Effect colours are routinely authored above full brightness so they
        // bloom. Clamping on read would silently destroy that.
        var colour = new MaterialColour(4.5f, 2f, 1f, 1f);

        Assert.True(colour.IsOverbright);
        Assert.Equal(4.5f, colour.R);
        Assert.Equal(255, colour.ToBytes().R);   // display clamps, storage does not
    }

    [Fact]
    public void FormatsAsHexForDisplay()
        => Assert.Equal("#FF8000FF", MaterialColour.FromBytes(255, 128, 0, 255).ToHex());
}

/// <summary>Reads and edits material parameters in real packages.</summary>
public sealed class RealMaterialTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public RealMaterialTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Scratch.NewFolder("oas2-mat");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

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

    /// <summary>Finds a package containing a material with a colour parameter.</summary>
    private static (string Path, MaterialInstance Material)? FindColourMaterial(GameClient client, int limit = 400)
    {
        // Character packages, smallest first. This used to take the largest
        // package in the folder, on the reasoning that a big one is the
        // likeliest to hold a material — true, but the largest here are 100 MB
        // regions, so every run copied one of those to work on. A colour
        // parameter is a colour parameter whatever holds it, and a character
        // package is a few hundred kilobytes and full of them.
        foreach (string path in Directory.EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*.upk")
                                         .OrderBy(p => new FileInfo(p).Length)
                                         .Take(limit))
        {
            Package package;
            try { package = Package.Open(path); } catch (InvalidPackageException) { continue; }

            foreach (int index in package.FindExportsOfClass(MaterialParameterReader.MaterialInstanceClass))
            {
                MaterialInstance? material = MaterialParameterReader.TryRead(package, index);
                if (material is not null && material.Colours.Count > 0) return (path, material);
            }
        }
        return null;
    }

    [Fact]
    public void ReadsColourParametersWithPlausibleValues()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            (string Path, MaterialInstance Material)? hit = FindColourMaterial(client);
            Assert.True(hit is not null, $"{client.DisplayName}: no material with a colour parameter found.");

            MaterialInstance material = hit!.Value.Material;

            foreach (ColourParameter parameter in material.Colours)
            {
                Assert.False(string.IsNullOrWhiteSpace(parameter.Name));
                Assert.True(parameter.ValueOffset > 0);

                // Colours are authored values, not arbitrary bytes: they must be
                // finite and within a sane range even when overbright.
                foreach (float channel in new[] { parameter.Colour.R, parameter.Colour.G, parameter.Colour.B, parameter.Colour.A })
                {
                    Assert.False(float.IsNaN(channel), $"{parameter.Name} has a NaN channel.");
                    Assert.InRange(channel, -100f, 1000f);
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {material.Name} — " +
                string.Join(", ", material.Colours.Take(4).Select(c => $"{c.Name}={c.Colour.ToHex()}")));
        }
    }

    [Fact]
    public async Task EditingAColourWritesBackAndReadsTheNewValue()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            (string Path, MaterialInstance Material)? hit = FindColourMaterial(client);
            if (hit is null) continue;

            // Work on a copy: no test may modify a game install.
            string copy = Path.Combine(_scratch, $"{client.Id:N}-{Path.GetFileName(hit.Value.Path)}");
            File.Copy(hit.Value.Path, copy, overwrite: true);

            Package package = Package.Open(copy);
            MaterialInstance material = MaterialParameterReader.TryRead(package, hit.Value.Material.ExportIndex)!;
            ColourParameter original = material.Colours[0];

            var replacement = new MaterialColour(0.25f, 0.5f, 0.75f, 1f);

            await MaterialParameterWriter.SaveAsync(package, new Dictionary<int, (IReadOnlyList<ColourEdit>, IReadOnlyList<ScalarEdit>)>
            {
                [material.ExportIndex] = ([new ColourEdit(original.ValueOffset, replacement)], []),
            });

            Assert.True(OmegaAssetStudio2.Core.Workspace.Backup.BackupFileHelper.HasBackup(copy), "No pristine backup was taken.");

            // Re-open from disk and confirm the value survived the round trip.
            Package reloaded = Package.Open(copy);
            MaterialInstance after = MaterialParameterReader.TryRead(reloaded, material.ExportIndex)!;
            ColourParameter edited = after.Colours[0];

            Assert.Equal(original.Name, edited.Name);
            Assert.Equal(replacement.R, edited.Colour.R, 5);
            Assert.Equal(replacement.G, edited.Colour.G, 5);
            Assert.Equal(replacement.B, edited.Colour.B, 5);

            // Every other parameter must be untouched.
            for (int i = 1; i < material.Colours.Count; i++)
            {
                Assert.Equal(material.Colours[i].Colour, after.Colours[i].Colour);
                Assert.Equal(material.Colours[i].Name, after.Colours[i].Name);
            }

            _output.WriteLine(
                $"{client.DisplayName}: set '{edited.Name}' from {original.Colour.ToHex()} to " +
                $"{edited.Colour.ToHex()}; {material.Colours.Count - 1} other colours preserved.");
        }
    }

    [Fact]
    public async Task ScanFindsMaterialsWithParametersAcrossAClient()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        var catalog = new MaterialCatalog();

        foreach (GameClient client in clients)
        {
            // A narrow filter keeps the test quick; the tool scans everything.
            IReadOnlyList<MaterialInstance> materials = await catalog.ScanAsync(client, "SCS__*.upk");

            int colours = materials.Sum(m => m.Colours.Count);
            int scalars = materials.Sum(m => m.Scalars.Count);

            _output.WriteLine(
                $"{client.DisplayName}: {materials.Count:N0} materials with parameters " +
                $"({colours:N0} colours, {scalars:N0} values).");

            Assert.All(materials, m => Assert.True(m.HasEditableParameters));
        }
    }
}
