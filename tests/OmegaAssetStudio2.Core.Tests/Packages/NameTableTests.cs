using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Packages;

public sealed class NameTableTests
{
    /// <summary>Builds a body containing just a name table, plus a header describing it.</summary>
    private static (byte[] Body, PackageHeader Header) BuildTable(params string[] names)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        foreach (string name in names)
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            writer.Write(text.Length);
            writer.Write(text);
            writer.Write(0x0007001000000000UL);
        }
        writer.Flush();

        PackageHeader header = PackageHeader.Read(
            TestPackageBuilder.Header(nameCount: names.Length, nameOffset: 0));

        return (buffer.ToArray(), header);
    }

    [Fact]
    public void ReadsEveryEntryWithItsFlags()
    {
        (byte[] body, PackageHeader header) = BuildTable("class", "core", "engine");

        NameTable table = NameTable.Read(body, header, bodyStart: 0);

        Assert.Equal(3, table.Count);
        Assert.Equal("class", table.GetName(0));
        Assert.Equal("core", table.GetName(1));
        Assert.Equal("engine", table.GetName(2));
        Assert.Equal(0x0007001000000000UL, table[0].Flags);
    }

    [Fact]
    public void LookupFoldsCase()
    {
        // Real packages store names lower-cased. A case-sensitive lookup silently
        // fails to find names that are present, which reads as "asset missing".
        (byte[] body, PackageHeader header) = BuildTable("none", "objectreferencer");

        NameTable table = NameTable.Read(body, header, bodyStart: 0);

        Assert.True(table.Contains("None"));
        Assert.True(table.Contains("NONE"));
        Assert.Equal(0, table.IndexOf("None"));
        Assert.Equal(1, table.IndexOf("ObjectReferencer"));
    }

    [Fact]
    public void MissingNameReportsMinusOneRatherThanThrowing()
    {
        (byte[] body, PackageHeader header) = BuildTable("class");
        NameTable table = NameTable.Read(body, header, bodyStart: 0);

        Assert.Equal(-1, table.IndexOf("NotPresent"));
        Assert.False(table.Contains("NotPresent"));
    }

    [Fact]
    public void ResolveAppendsTheNumericSuffix()
    {
        (byte[] body, PackageHeader header) = BuildTable("light");
        NameTable table = NameTable.Read(body, header, bodyStart: 0);

        // Zero means "no suffix"; the stored number is one greater than the
        // suffix that is displayed.
        Assert.Equal("light", table.Resolve(0, 0));
        Assert.Equal("light_0", table.Resolve(0, 1));
        Assert.Equal("light_4", table.Resolve(0, 5));
    }

    [Fact]
    public void OutOfRangeIndexNamesTheProblem()
    {
        (byte[] body, PackageHeader header) = BuildTable("class");
        NameTable table = NameTable.Read(body, header, bodyStart: 0);

        var ex = Assert.Throws<InvalidPackageException>(() => table.GetName(99));
        Assert.Contains("99", ex.Message);

        Assert.Throws<InvalidPackageException>(() => table.GetName(-1));
    }

    [Fact]
    public void RejectsANameCountThatCannotFit()
    {
        (byte[] body, _) = BuildTable("class");
        PackageHeader header = PackageHeader.Read(
            TestPackageBuilder.Header(nameCount: 10_000_000, nameOffset: 0));

        // Must fail on the arithmetic, not by allocating ten million entries.
        Assert.Throws<InvalidPackageException>(() => NameTable.Read(body, header, bodyStart: 0));
    }

    [Fact]
    public void RejectsAnOffsetOutsideTheBody()
    {
        (byte[] body, _) = BuildTable("class");
        PackageHeader header = PackageHeader.Read(
            TestPackageBuilder.Header(nameCount: 1, nameOffset: 500_000));

        Assert.Throws<InvalidPackageException>(() => NameTable.Read(body, header, bodyStart: 0));
    }
}

/// <summary>Reads name tables out of real packages.</summary>
public sealed class RealNameTableTests
{
    private readonly ITestOutputHelper _output;

    public RealNameTableTests(ITestOutputHelper output) => _output = output;

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
    public void ReadsEveryNameOfManyRealPackages()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int packages = 0;
            long totalNames = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.upk")
                                             .OrderBy(p => new FileInfo(p).Length)
                                             .Take(60))
            {
                byte[] bytes = File.ReadAllBytes(path);
                PackageHeader header = PackageHeader.Read(bytes);
                byte[] body = ChunkExpander.ExpandBody(header, bytes, out int bodyStart);

                NameTable table = NameTable.Read(body, header, bodyStart);

                Assert.Equal(header.NameCount, table.Count);
                Assert.True(table.Contains("none"),
                    $"{Path.GetFileName(path)} has no null name, so the table was misread.");

                // Every name must be printable. Garbage here means the body was
                // expanded wrongly, which nothing downstream would notice.
                foreach (NameEntry entry in table.Entries)
                {
                    Assert.NotEqual(string.Empty, entry.Name);
                    Assert.All(entry.Name, c => Assert.InRange(c, (char)32, (char)126));
                }

                packages++;
                totalNames += table.Count;
            }

            _output.WriteLine($"{client.DisplayName}: {totalNames:N0} names across {packages} packages.");
        }
    }
}
