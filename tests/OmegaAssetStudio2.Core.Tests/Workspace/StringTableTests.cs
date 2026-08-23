using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

/// <summary>
/// Checks the game's display text against real installs.
/// </summary>
public sealed class StringTableTests
{
    /// <summary>
    /// A key whose text is known. It is the anchor for the whole layout: if the
    /// keys are assembled wrongly this lands on nothing, or on the wrong words.
    /// </summary>
    private const ulong KnownKey = 0x3210183D24E6050B;

    private const string KnownText = "God Blast";

    private readonly ITestOutputHelper _output;

    public StringTableTests(ITestOutputHelper output) => _output = output;

    private static List<string> InstalledRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("OAS2_CLIENT_ROOTS");
        string[] roots = !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return roots.Where(Directory.Exists).ToList();
    }

    [Fact]
    public void TheKnownKeyResolvesToItsKnownText()
    {
        List<string> roots = InstalledRoots();
        if (roots.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (string root in roots)
        {
            StringTable table = StringTable.Load(root);

            _output.WriteLine($"{Path.GetFileName(root)}: {table.Count:N0} names.");

            Assert.True(table.Count > 10_000, $"{root}: only {table.Count} names were read.");
            Assert.Equal(KnownText, table.Find(KnownKey));
        }
    }

    [Fact]
    public void AMissingKeyIsSaidToBeMissing()
    {
        foreach (string root in InstalledRoots())
        {
            StringTable table = StringTable.Load(root);

            Assert.Null(table.Find(0));
            Assert.False(table.Contains(0));
            return;   // one install is enough
        }
    }

    [Fact]
    public void AFolderWithNoTextReadsAsEmptyRatherThanFailing()
    {
        StringTable table = StringTable.Load(Path.Combine(Path.GetTempPath(), "oas2-no-such-game"));

        Assert.Equal(0, table.Count);
        Assert.Null(table.Find(KnownKey));
    }
}
