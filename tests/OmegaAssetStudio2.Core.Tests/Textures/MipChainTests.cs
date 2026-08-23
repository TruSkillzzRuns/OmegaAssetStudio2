using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Packages.Properties;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Textures;

/// <summary>
/// Validates the mip layout against real textures.
/// </summary>
/// <remarks>
/// The decisive check is that each mip's <em>declared</em> byte size equals the
/// size computed independently from its format and dimensions. Those two numbers
/// come from different places in the file, so agreeing across thousands of
/// textures is strong evidence the layout is right rather than coincidental.
/// </remarks>
public sealed class MipChainTests
{
    private readonly ITestOutputHelper _output;

    public MipChainTests(ITestOutputHelper output) => _output = output;

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
    public void DeclaredMipSizesMatchTheFormatArithmetic()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int textures = 0, chains = 0, inlineMips = 0, externalMips = 0, sizeMatches = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(8))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    PropertyBag? properties = package.TryReadProperties(index);
                    if (properties is null) continue;

                    TextureInfo? info = TextureInfo.TryRead(package, index);
                    if (info is null) continue;
                    textures++;

                    TextureMipChain? chain = TextureMipChain.TryRead(package, index, properties);
                    if (chain is null) continue;
                    chains++;

                    Assert.NotEmpty(chain.Mips);

                    // The top mip must match the texture's declared dimensions.
                    TextureMipMap top = chain.Mips[0];
                    Assert.Equal(info.Width, top.Width);
                    Assert.Equal(info.Height, top.Height);

                    foreach (TextureMipMap mip in chain.Mips)
                    {
                        if (mip.IsInline) inlineMips++; else externalMips++;

                        // The size the file declares must equal the size the
                        // format arithmetic demands. These come from different
                        // places in the package, so agreement is real evidence.
                        // This holds for external mips too, where the count is
                        // carried in a different field.
                        int expected = mip.ExpectedByteSize(info.Format);
                        if (expected > 0)
                        {
                            Assert.True(mip.ByteCount == expected,
                                $"{info.Name}: mip {mip.Width}x{mip.Height} {info.FormatName} " +
                                $"declares {mip.ByteCount} bytes but the format needs {expected}.");
                            sizeMatches++;
                        }
                    }
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {chains}/{textures} chains read, " +
                $"{inlineMips} inline mips ({sizeMatches} size-verified), {externalMips} external.");

            Assert.True(chains > 0, $"{client.DisplayName}: no mip chain could be read.");
            Assert.True(sizeMatches > 0, $"{client.DisplayName}: no inline mip size could be verified.");
            Assert.True(chains == textures,
                $"{client.DisplayName}: {textures - chains} textures had an unreadable mip chain.");
        }
    }

    [Fact]
    public void CacheBackedTexturesReportExternalMips()
    {
        // A texture whose properties name a texture cache should not claim its
        // pixels are inline. Getting this backwards is what makes a write land in
        // the wrong file.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int cacheBacked = 0, withExternalTopMip = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "ICO__*.upk").Take(8))
            {
                Package package = Package.Open(path);

                foreach (int index in package.FindExportsOfClass("texture2d"))
                {
                    PropertyBag? properties = package.TryReadProperties(index);
                    TextureInfo? info = properties is null ? null : TextureInfo.TryRead(package, index);
                    if (info is null || !info.IsCacheBacked) continue;

                    cacheBacked++;

                    TextureMipChain? chain = TextureMipChain.TryRead(package, index, properties!);
                    if (chain is not null && !chain.Mips[0].IsInline) withExternalTopMip++;
                }
            }

            if (cacheBacked == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no cache-backed icons in the sample.");
                continue;
            }

            _output.WriteLine(
                $"{client.DisplayName}: {cacheBacked} cache-backed, {withExternalTopMip} with an external top mip.");

            Assert.True(withExternalTopMip > 0,
                $"{client.DisplayName}: cache-backed textures all claimed inline pixels.");
        }
    }
}
