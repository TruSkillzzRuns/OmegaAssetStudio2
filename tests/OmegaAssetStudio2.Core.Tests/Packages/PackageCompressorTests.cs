using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

/// <summary>
/// Checks that a package written back compressed holds exactly what it held.
/// </summary>
/// <remarks>
/// The game reads only compressed packages, so this is the form that has to be
/// right. The decisive check is that the body expands back to the same bytes:
/// everything in the file — the tables, each object's position, and the texture
/// mips that record their own position — is measured against the expanded body,
/// so if that is identical, nothing addressed against it can have moved.
/// </remarks>
public sealed class RealPackageCompressorTests
{
    private readonly ITestOutputHelper _output;

    public RealPackageCompressorTests(ITestOutputHelper output) => _output = output;

    private static IEnumerable<string> SomePackages(GameClient client, int count) => Directory
        .EnumerateFiles(client.CookedPath, "UC__MarvelPlayer_*.upk", SearchOption.TopDirectoryOnly)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .Take(count);

    /// <summary>
    /// Every block written must be an LZO stream, never the plain bytes.
    /// </summary>
    /// <remarks>
    /// The game decompresses every block without asking whether it got smaller,
    /// so a block left plain is read as an LZO stream and becomes rubbish. This
    /// counts the blocks that do not compress, which are exactly the ones that
    /// used to be left plain, and checks each is nonetheless a stream that
    /// decodes back to what went in.
    /// </remarks>
    [Fact]
    public void EveryBlockIsCompressedEvenWhenThatMakesItBigger()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            string path = SomePackages(client, 1).FirstOrDefault(string.Empty);
            if (path.Length == 0) continue;

            string fileName = Path.GetFileName(path);

            Package package = Package.Open(path);
            byte[] body = package.CopyBody(out _);

            const int blockSize = 128 * 1024;
            int blocks = 0, wouldNotShrink = 0;

            for (int at = 0; at < body.Length; at += blockSize)
            {
                int length = Math.Min(blockSize, body.Length - at);
                ReadOnlySpan<byte> plain = body.AsSpan(at, length);

                byte[] packed = OmegaAssetStudio2.Core.Packages.Compression.Lzo1xCompressor.Compress(plain);

                blocks++;
                if (packed.Length >= length) wouldNotShrink++;

                // Whatever its size, it has to decode back exactly.
                byte[] back = new byte[length];
                OmegaAssetStudio2.Core.Packages.Compression.Lzo1x.Decompress(packed, back);

                Assert.True(plain.SequenceEqual(back), $"block at {at:N0} did not decode back to itself.");
            }

            _output.WriteLine(
                $"{client.DisplayName} — {fileName}: {blocks} block(s), {wouldNotShrink} of which do not " +
                "get smaller and used to be written plain.");

            return;
        }
    }

    [Fact]
    public void ACompressedPackageWrittenBackExpandsToTheSameBody()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            int checkedCount = 0;

            foreach (string path in SomePackages(client, 12))
            {
                Package package;
                try { package = Package.Open(path); } catch (InvalidPackageException) { continue; }
                if (!package.Header.IsCompressed) continue;

                byte[] body = package.CopyBody(out int bodyStart);

                _output.WriteLine(
                    $"  {Path.GetFileName(path)}: body starts at {bodyStart:N0}, " +
                    $"{package.Header.Chunks.Count} chunk(s), first at {package.Header.Chunks[0].CompressedOffset:N0}, " +
                    $"folder name \"{package.Header.FolderName}\" ({package.Header.FolderName.Length} chars).");

                byte[] file = PackageCompressor.Build(package, body);

                // Re-read it exactly as the loader would, from the bytes alone.
                Package reopened = Package.Read(file, path);
                byte[] again = reopened.CopyBody(out int startAgain);

                Assert.Equal(bodyStart, startAgain);
                Assert.True(body.AsSpan().SequenceEqual(again),
                    $"{Path.GetFileName(path)}: the body did not survive being packed and unpacked.");

                Assert.True(reopened.Header.IsCompressed,
                    $"{Path.GetFileName(path)} was written without compression.");

                // And every object must still be where the table says.
                for (int i = 0; i < package.Exports.Count; i++)
                {
                    Assert.Equal(package.Exports[i].SerialOffset, reopened.Exports[i].SerialOffset);
                    Assert.Equal(package.Exports[i].SerialSize, reopened.Exports[i].SerialSize);
                }

                checkedCount++;

                if (checkedCount == 1)
                {
                    _output.WriteLine(
                        $"{client.DisplayName} — {Path.GetFileName(path)}: " +
                        $"{new FileInfo(path).Length:N0} bytes on disk, {body.Length:N0} expanded, " +
                        $"{file.Length:N0} written back ({file.Length / (double)new FileInfo(path).Length:P0} " +
                        "of the original).");
                }
            }

            _output.WriteLine($"{client.DisplayName}: {checkedCount} packages packed and unpacked intact.");

            Assert.True(checkedCount > 0, "no compressed package was checked.");
            return;
        }

        _output.WriteLine("No installs present; nothing checked.");
    }

    [Fact]
    public void ReplacingAModelStillProducesACompressedPackage()
    {
        foreach (GameClient client in TestGames.Installed)
        {
            foreach (RosterEntry hero in CharacterRoster.Build(client, RosterCategory.Hero).Take(6))
            {
                Package package;
                try { package = Package.Open(hero.PackagePath); } catch (InvalidPackageException) { continue; }
                if (!package.Header.IsCompressed) continue;

                int index = -1;
                SkeletalMesh? mesh = null;

                foreach (int candidate in package.FindExportsOfClass(SkeletalMeshReader.SkeletalMeshClass))
                {
                    SkeletalMesh? read = SkeletalMeshReader.TryRead(package, candidate);
                    if (read?.HighestDetail is not { HasGeometry: true }) continue;

                    mesh = read;
                    index = candidate;
                    break;
                }

                if (mesh is null) continue;

                SkeletalMeshLod lod = mesh.HighestDetail!;

                byte[] written = SkeletalMeshSerialiser.Replace(package, index, mesh, new MeshGeometry
                {
                    Positions = lod.Positions,
                    Normals = lod.Normals,
                    TexCoords = lod.TexCoords,
                    Influences = lod.Influences,
                    Indices = lod.Indices,
                    Sections = lod.Sections,
                });

                byte[] file = PackageRebuilder.Build(package, [new ExportPatch(index, written)]);

                Package reopened = Package.Read(file, package.Path);

                Assert.True(reopened.Header.IsCompressed, "the rebuilt package was not compressed.");

                SkeletalMesh? back = SkeletalMeshReader.TryRead(reopened, index, why =>
                    throw new Xunit.Sdk.XunitException($"could not read the model back: {why}"));

                Assert.NotNull(back);
                Assert.Equal(lod.Positions.Count, back!.HighestDetail!.Positions.Count);

                // Nothing else may have moved, exactly as when written plainly.
                for (int i = 0; i < package.Exports.Count; i++)
                {
                    if (i == index) continue;

                    Assert.Equal(package.Exports[i].SerialOffset, reopened.Exports[i].SerialOffset);
                    Assert.True(package.GetExportData(i).SequenceEqual(reopened.GetExportData(i)),
                        $"object {i} changed although it was not touched.");
                }

                _output.WriteLine(
                    $"{client.DisplayName} — {hero.DisplayName}: {mesh.Name} replaced, package written " +
                    $"back compressed at {file.Length:N0} bytes " +
                    $"(was {new FileInfo(package.Path).Length:N0}).");

                return;
            }
        }
    }
}
