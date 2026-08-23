using System;
using System.Collections.Generic;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Meshes;

/// <summary>
/// Measures the shape of every character model the game ships.
/// </summary>
/// <remarks>
/// Written to answer, from the game's own data rather than from anything
/// assumed about the engine: how many runs of vertices a model is split into,
/// how many bones one run ever draws on, and how runs relate to the sections
/// that are drawn. Whatever this application writes has to sit inside the same
/// bounds the shipped content sits inside.
/// </remarks>
public sealed class RealMeshShapeSurveyTests
{
    private readonly ITestOutputHelper _output;

    public RealMeshShapeSurveyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WhatShapeAreTheGamesOwnModels()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            var chunkCounts = new Dictionary<int, int>();
            var lodCounts = new Dictionary<int, int>();
            var sectionCounts = new Dictionary<int, int>();
            var bonesPerChunk = new List<int>();
            var influencesPerVertex = new List<int>();

            int models = 0, sectionsMatchChunks = 0, sectionsDoNot = 0;
            int rigidOnly = 0, softOnly = 0, mixed = 0;

            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(60))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }

                foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
                    if (mesh?.HighestDetail is not { HasGeometry: true } lod) continue;

                    models++;
                    lodCounts[mesh.Lods.Count] = lodCounts.GetValueOrDefault(mesh.Lods.Count) + 1;

                    chunkCounts[lod.Chunks.Count] = chunkCounts.GetValueOrDefault(lod.Chunks.Count) + 1;
                    sectionCounts[lod.Sections.Count] = sectionCounts.GetValueOrDefault(lod.Sections.Count) + 1;

                    foreach (MeshChunk chunk in lod.Chunks)
                    {
                        bonesPerChunk.Add(chunk.BoneMap.Count);

                        if (chunk.RigidVertexCount > 0 && chunk.SoftVertexCount > 0) mixed++;
                        else if (chunk.RigidVertexCount > 0) rigidOnly++;
                        else softOnly++;
                    }

                    // Does each section draw from its own run, one for one?
                    bool oneForOne = lod.Sections.Count == lod.Chunks.Count
                        && lod.Sections.Select((s, i) => s.ChunkIndex == i).All(x => x);

                    if (oneForOne) sectionsMatchChunks++;
                    else sectionsDoNot++;

                    foreach (VertexInfluence influence in lod.Influences)
                        influencesPerVertex.Add(influence.Bones.Count);
                }
            }

            if (models == 0) continue;

            _output.WriteLine($"{client.DisplayName}: {models:N0} models measured.");

            _output.WriteLine("  levels of detail per model: " + Spread(lodCounts));
            _output.WriteLine("  runs of vertices per model: " + Spread(chunkCounts));
            _output.WriteLine("  sections per model:         " + Spread(sectionCounts));

            _output.WriteLine(
                $"  bones in one run: smallest {bonesPerChunk.Min()}, largest {bonesPerChunk.Max()}, " +
                $"average {bonesPerChunk.Average():0.0}");

            _output.WriteLine(
                $"  runs holding only rigidly bound vertices: {rigidOnly:N0}; " +
                $"only softly bound: {softOnly:N0}; both: {mixed:N0}");

            _output.WriteLine(
                $"  sections matching runs one for one: {sectionsMatchChunks:N0} of " +
                $"{sectionsMatchChunks + sectionsDoNot:N0}");

            _output.WriteLine(
                $"  bones per vertex: largest {influencesPerVertex.Max()}, " +
                $"average {influencesPerVertex.Average():0.00}");

            // How often a run is at or near a round limit, which is what a
            // hardware limit looks like from the outside.
            foreach (int limit in new[] { 64, 68, 75, 76, 80, 128, 256 })
            {
                int over = bonesPerChunk.Count(b => b > limit);
                _output.WriteLine($"    runs drawing on more than {limit} bones: {over:N0}");
            }

            return;
        }

        _output.WriteLine("No installs present; nothing measured.");
    }

    private static string Spread(Dictionary<int, int> counts) => string.Join(", ",
        counts.OrderBy(c => c.Key).Select(c => $"{c.Key}: {c.Value:N0}"));
}
