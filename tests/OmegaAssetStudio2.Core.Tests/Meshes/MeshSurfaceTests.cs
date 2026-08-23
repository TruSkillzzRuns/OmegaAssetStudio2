using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

public sealed class SurfaceSlotChoiceTests
{
    private static MaterialTextureSlot Slot(string parameter, string texture) =>
        new() { ParameterName = parameter, Texture = new ObjectReference(1), TextureName = texture };

    [Fact]
    public void ANamedColourSlotWinsOverEverythingElse()
    {
        var slots = new[]
        {
            Slot("NormalMap", "hero_body_N"),
            Slot("DiffuseMap", "hero_body_D"),
            Slot("SpecularMap", "hero_body_S"),
        };

        Assert.Equal("DiffuseMap", MaterialTextureReader.PickSurfaceSlot(slots)!.ParameterName);
    }

    [Fact]
    public void AnUnnamedSlotIsTakenWhenNothingIsNamedForColour()
    {
        // Real materials use slot names this code has never seen. Falling back
        // to one not identified as another channel is what covers them.
        var slots = new[]
        {
            Slot("Texture_02", "hero_normal_map"),
            Slot("Texture_01", "hero_body"),
        };

        Assert.Equal("Texture_01", MaterialTextureReader.PickSurfaceSlot(slots)!.ParameterName);
    }

    [Fact]
    public void SomethingIsChosenEvenWhenEverySlotLooksWrong()
    {
        // Better a wrong picture than a grey model with no explanation.
        var slots = new[] { Slot("NormalMap", "hero_N"), Slot("SpecMap", "hero_S") };

        Assert.NotNull(MaterialTextureReader.PickSurfaceSlot(slots));
    }

    [Fact]
    public void NoSlotsMeansNoChoice() =>
        Assert.Null(MaterialTextureReader.PickSurfaceSlot([]));
}

/// <summary>
/// Checks that real models resolve to real pictures.
/// </summary>
/// <remarks>
/// This is the check that proves the chain end to end: a model's material slot,
/// through the material's texture bindings, to decoded pixels. Every link is
/// read from a different part of the file, so a break anywhere shows up here.
/// </remarks>
public sealed class RealMeshSurfaceTests
{
    private readonly ITestOutputHelper _output;

    public RealMeshSurfaceTests(ITestOutputHelper output) => _output = output;

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
    public void HeroModelsResolveToPicturesThatCanBeDrawn()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            var reader = new TextureReader(client.CookedPath);
            var reasons = new Dictionary<string, int>();

            // Built once for the whole folder, then shared: it is what lets a
            // material living in another package be followed at all.
            var locator = new ObjectLocator(TestGames.IndexFor(client));

            int models = 0, textured = 0, slots = 0, covered = 0, borrowed = 0;

            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(25))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                    if (mesh?.HighestDetail is not { HasGeometry: true }) continue;

                    models++;
                    slots += mesh.Materials.Count;

                    IReadOnlyList<MeshSurface> surfaces = MeshSurfaceResolver.Resolve(
                        package, mesh, reader,
                        onSkipped: (_, why) => reasons[why] = reasons.GetValueOrDefault(why) + 1,
                        locator: locator);

                    covered += surfaces.Count;
                    borrowed += surfaces.Count(s => s.FromAnotherPackage);
                    if (surfaces.Count > 0) textured++;

                    foreach (MeshSurface surface in surfaces)
                    {
                        Assert.InRange(surface.Image.Width, 1, 8192);
                        Assert.InRange(surface.Image.Height, 1, 8192);

                        // Four bytes a pixel, no more and no less. A mismatch
                        // means the wrong mip was measured, and the viewport
                        // would upload past the end of the buffer.
                        Assert.Equal(
                            surface.Image.Width * surface.Image.Height * 4,
                            surface.Image.Rgba.Length);
                    }

                    break;   // one model per package is enough for this check
                }
            }

            if (models == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no models to check.");
                continue;
            }

            double modelRate = textured / (double)models;
            double slotRate = slots == 0 ? 0 : covered / (double)slots;

            _output.WriteLine(
                $"{client.DisplayName}: {textured} of {models} models textured ({modelRate:P0}); " +
                $"{covered} of {slots} material slots covered ({slotRate:P0}); " +
                $"{borrowed} came from another package.");

            foreach (var reason in reasons.OrderByDescending(r => r.Value).Take(4))
                _output.WriteLine($"    skipped x{reason.Value}: {reason.Key}");

            Assert.True(modelRate > 0.8,
                $"{client.DisplayName}: only {modelRate:P0} of models resolved to a picture.");

            // Not every slot can have a picture: some materials are a glow or a
            // placeholder and genuinely bind no texture, and no index changes
            // that. What the index does guarantee is that nothing is missed
            // because it lives in another file — so that is what is asserted,
            // rather than a percentage that would drift with the content.
            string[] lookupFailures = reasons.Keys
                .Where(r => r.Contains("not in this game folder", StringComparison.Ordinal) ||
                            r.Contains("is in another package", StringComparison.Ordinal) ||
                            r.Contains("could not be found", StringComparison.Ordinal))
                .ToArray();

            Assert.True(lookupFailures.Length == 0,
                $"{client.DisplayName}: {lookupFailures.Length} slot(s) failed to find their material or " +
                $"texture even with the index: {string.Join("; ", lookupFailures)}");
        }
    }
}
