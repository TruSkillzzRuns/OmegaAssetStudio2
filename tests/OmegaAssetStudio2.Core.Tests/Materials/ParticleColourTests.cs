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

public sealed class ParticleColourClassTests
{
    [Theory]
    [InlineData("ParticleModuleColor", true)]
    [InlineData("particlemodulecoloroverlife", true)]
    [InlineData("PARTICLEMODULECOLORSCALEOVERLIFE", true)]
    [InlineData("particlemodulerequired", false)]
    [InlineData("staticmesh", false)]
    public void RecognisesColourModuleClasses(string className, bool expected)
        => Assert.Equal(expected, ParticleColourReader.IsColourModule(className));
}

/// <summary>Reads and edits particle colours in real packages.</summary>
public sealed class RealParticleColourTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public RealParticleColourTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Scratch.NewFolder("oas2-particle");
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

    /// <summary>
    /// Packages worth looking in, smallest first.
    /// </summary>
    /// <remarks>
    /// Character packages, because they hold the effects that carry colour and
    /// are a few hundred kilobytes each. These searches used to take the
    /// largest packages in the folder instead, which are 100 MB regions — so a
    /// test that only needed one colour module copied a region to work on, and
    /// left a hundred megabytes behind every run.
    /// </remarks>
    private static IEnumerable<string> Candidates(GameClient client, int take) =>
        Directory.EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*.upk")
                 .OrderBy(p => new FileInfo(p).Length)
                 .Take(take);

    private static (string Path, ParticleColourModule Module)? FindColourModule(GameClient client, int limit = 400)
    {
        foreach (string path in Candidates(client, limit))
        {
            Package package;
            try { package = Package.Open(path); } catch (InvalidPackageException) { continue; }

            for (int i = 0; i < package.Exports.Count; i++)
            {
                ParticleColourModule? module = ParticleColourReader.TryRead(package, i);
                if (module is { HasColours: true }) return (path, module);
            }
        }
        return null;
    }

    [Fact]
    public void ReadsColoursWithPlausibleValues()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int modules = 0, keys = 0, overbright = 0;
            var byClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Candidates(client, 40))
            {
                Package package = Package.Open(path);

                for (int i = 0; i < package.Exports.Count; i++)
                {
                    ParticleColourModule? module = ParticleColourReader.TryRead(package, i);
                    if (module is not { HasColours: true }) continue;

                    modules++;
                    byClass[module.ClassName] = byClass.GetValueOrDefault(module.ClassName) + 1;

                    foreach (ParticleColourKey key in module.Keys)
                    {
                        keys++;
                        if (key.Colour.IsOverbright) overbright++;

                        // Authored colours are finite and within a sane range even
                        // when deliberately brighter than white.
                        foreach (float channel in new[] { key.Colour.R, key.Colour.G, key.Colour.B })
                        {
                            Assert.False(float.IsNaN(channel), $"{module.Name}: NaN channel.");
                            Assert.InRange(channel, -10f, 10_000f);
                        }

                        Assert.True(key.ValueOffset > 0);
                    }
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {modules:N0} colour modules, {keys:N0} colours " +
                $"({overbright:N0} overbright) — " +
                string.Join(", ", byClass.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));

            Assert.True(modules > 0, $"{client.DisplayName}: no particle colour modules were readable.");
        }
    }

    [Fact]
    public async Task EditingAParticleColourWritesBackAndReadsTheNewValue()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            (string Path, ParticleColourModule Module)? hit = FindColourModule(client);
            if (hit is null)
            {
                _output.WriteLine($"{client.DisplayName}: no colour module found.");
                continue;
            }

            // Work on a copy. No test may modify a game install.
            string copy = Path.Combine(_scratch, $"{client.Id:N}-{Path.GetFileName(hit.Value.Path)}");
            File.Copy(hit.Value.Path, copy, overwrite: true);

            Package package = Package.Open(copy);
            ParticleColourModule module = ParticleColourReader.TryRead(package, hit.Value.Module.ExportIndex)!;

            ParticleColourKey original = module.Keys[0];
            var replacement = new MaterialColour(0.125f, 0.25f, 0.5f, 1f);

            byte[] patched = ParticleColourReader.BuildPatchedExport(
                package, module.ExportIndex, [new ColourEdit(original.ValueOffset, replacement)]);

            await PackageWriter.SaveAsync(package, [new ExportPatch(module.ExportIndex, patched)]);

            Assert.True(OmegaAssetStudio2.Core.Workspace.Backup.BackupFileHelper.HasBackup(copy), "No pristine backup was taken.");

            // Re-open from disk: the value must survive the round trip.
            Package reloaded = Package.Open(copy);
            ParticleColourModule after = ParticleColourReader.TryRead(reloaded, module.ExportIndex)!;

            Assert.Equal(module.Keys.Count, after.Keys.Count);
            Assert.Equal(replacement.R, after.Keys[0].Colour.R, 5);
            Assert.Equal(replacement.G, after.Keys[0].Colour.G, 5);
            Assert.Equal(replacement.B, after.Keys[0].Colour.B, 5);

            // Every other key must be untouched.
            for (int i = 1; i < module.Keys.Count; i++)
            {
                Assert.Equal(module.Keys[i].Colour.R, after.Keys[i].Colour.R, 5);
                Assert.Equal(module.Keys[i].Colour.G, after.Keys[i].Colour.G, 5);
                Assert.Equal(module.Keys[i].Colour.B, after.Keys[i].Colour.B, 5);
            }

            _output.WriteLine(
                $"{client.DisplayName}: set '{module.Name}' key 0 from {original.Colour} to " +
                $"{after.Keys[0].Colour}; {module.Keys.Count - 1} other key(s) preserved.");
        }
    }

    [Fact]
    public void OverbrightColoursSurviveARoundTrip()
    {
        // Effect colours are routinely authored far above full brightness. A
        // reader or writer that clamped them would visibly dim every glow.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            (string Path, ParticleColourModule Module)? hit = FindColourModule(client);
            if (hit is null) continue;

            string copy = Path.Combine(_scratch, $"ob-{client.Id:N}-{Path.GetFileName(hit.Value.Path)}");
            File.Copy(hit.Value.Path, copy, overwrite: true);

            Package package = Package.Open(copy);
            ParticleColourModule module = ParticleColourReader.TryRead(package, hit.Value.Module.ExportIndex)!;

            var bright = new MaterialColour(12.5f, 3.25f, 0.75f, 1f);

            byte[] patched = ParticleColourReader.BuildPatchedExport(
                package, module.ExportIndex, [new ColourEdit(module.Keys[0].ValueOffset, bright)]);

            byte[] written = PackageWriter.Build(
                package, [new ExportPatch(module.ExportIndex, patched)]);

            ParticleColourModule after = ParticleColourReader.TryRead(
                Package.Read(written, copy), module.ExportIndex)!;

            Assert.Equal(12.5f, after.Keys[0].Colour.R, 4);
            Assert.Equal(3.25f, after.Keys[0].Colour.G, 4);
            Assert.True(after.Keys[0].Colour.IsOverbright);

            _output.WriteLine($"{client.DisplayName}: overbright {after.Keys[0].Colour} preserved.");
            return;
        }
    }
}
