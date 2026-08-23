using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Audio;

/// <summary>
/// Measures how many sounds can be given back their names.
/// </summary>
/// <remarks>
/// The container records a number per event, being the hash of the event's
/// name; the name itself is only in the packages that ask for the event. These
/// tests establish what that recovers on the real installs rather than assuming
/// it works.
/// </remarks>
public sealed class SoundNameTests
{
    /// <summary>The install container these checks read, named as the game names it.</summary>
    private const string Subject = "Angela";

    private readonly ITestOutputHelper _output;

    public SoundNameTests(ITestOutputHelper output) => _output = output;

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
    /// The hash must be the one the middleware uses, or nothing else can work.
    /// </summary>
    /// <remarks>
    /// Checked against the game's own data rather than a published constant: a
    /// name taken from a package must hash to an event that a container really
    /// declares. If the function were the 1a variant, or hashed the name as it
    /// is written rather than in lower case, no name would match anything.
    /// </remarks>
    [Fact]
    public void NamesFromThePackagesHashToEventsTheContainersDeclare()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string container = Path.Combine(client.CookedPath, $"SFX_{Subject}_INT.pck");
            if (!File.Exists(container)) continue;

            AudioPackage package = AudioPackage.Open(container);

            var events = new HashSet<uint>();

            using (var stream = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                foreach (AudioEntry bank in package.Banks)
                {
                    byte[] bytes = new byte[bank.Size];
                    stream.Seek(bank.Offset, SeekOrigin.Begin);
                    stream.ReadExactly(bytes);

                    foreach (uint id in SoundBank.Read(bytes).Events) events.Add(id);
                }
            }

            int matched = 0;
            var examples = new List<string>();

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, $"*{Subject}*.upk"))
            {
                Package upk;
                try { upk = Package.Open(path); } catch (InvalidPackageException) { continue; }

                for (int i = 0; i < upk.Names.Count; i++)
                {
                    string name = upk.Names.GetName(i);

                    if (!events.Contains(SoundNameHash.Of(name))) continue;

                    matched++;
                    if (examples.Count < 4) examples.Add(name);
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {events.Count} events in {Subject}'s container, {matched} names from " +
                $"its packages hash to one of them. For example: {string.Join(", ", examples)}");

            Assert.True(matched > 0,
                $"{client.DisplayName}: no name hashed to any event, so the hash function is wrong.");

            return;
        }
    }

    /// <summary>
    /// Where the events that reach a character's sounds actually live.
    /// </summary>
    /// <remarks>
    /// Naming from a container's own banks leaves most of a talkative
    /// character's sounds unnamed, although every event in those banks is
    /// named. This asks whether the missing sounds are reached by events kept
    /// in some other container, and what it costs to read them all.
    /// </remarks>
    [Fact]
    public void SoundsNotReachedByAContainersOwnEventsAreReachedByOthers()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        GameClient client = clients[0];

        // Every event in the whole install, and the sounds each one reaches.
        var clock = Stopwatch.StartNew();
        var soundToEvent = new Dictionary<uint, uint>();
        int events = 0, banks = 0;

        foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.pck"))
        {
            AudioPackage container;
            try { container = AudioPackage.Open(path); } catch (InvalidPackageException) { continue; }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            foreach (AudioEntry bank in container.Banks)
            {
                if (bank.Size <= 0) continue;

                byte[] bytes = new byte[bank.Size];
                stream.Seek(bank.Offset, SeekOrigin.Begin);
                if (stream.ReadAtLeast(bytes, bank.Size, throwOnEndOfStream: false) < bank.Size) continue;

                banks++;
                SoundBank read = SoundBank.Read(bytes);

                foreach (uint id in read.Events)
                {
                    events++;
                    foreach (uint sound in read.SoundsOf(id)) soundToEvent.TryAdd(sound, id);
                }
            }
        }

        clock.Stop();

        _output.WriteLine(
            $"{client.DisplayName}: {events:N0} events across {banks:N0} banks reach " +
            $"{soundToEvent.Count:N0} distinct sounds, read in {clock.ElapsedMilliseconds:N0} ms.");

        foreach (string subject in new[] { "Angela", "Cable", "Beast" })
        {
            string path = Path.Combine(client.CookedPath, $"SFX_{subject}_INT.pck");
            if (!File.Exists(path)) continue;

            AudioPackage container = AudioPackage.Open(path);

            // Split by how the sound is stored, and count what its own banks
            // declare as a sound at all — reached by an event or not.
            var declared = new HashSet<uint>();
            var containers = (Total: 0, WithChildren: 0);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                foreach (AudioEntry bank in container.Banks)
                {
                    byte[] bytes = new byte[bank.Size];
                    stream.Seek(bank.Offset, SeekOrigin.Begin);
                    if (stream.ReadAtLeast(bytes, bank.Size, throwOnEndOfStream: false) < bank.Size) continue;

                    SoundBank read = SoundBank.Read(bytes);
                    foreach (uint source in read.Sources) declared.Add(source);

                    (int total, int withChildren) = read.Containers;
                    containers = (containers.Total + total, containers.WithChildren + withChildren);
                }
            }

            int streams = container.Streams.Count();
            int embedded = container.Embedded.Count();

            _output.WriteLine(
                $"   {subject}: {container.Sounds.Count(s => soundToEvent.ContainsKey(s.Id)):N0} of " +
                $"{container.Sounds.Count():N0} reached by an event; streamed {streams:N0} " +
                $"({container.Streams.Count(s => soundToEvent.ContainsKey(s.Id)):N0} reached), " +
                $"in banks {embedded:N0} ({container.Embedded.Count(s => soundToEvent.ContainsKey(s.Id)):N0} " +
                $"reached). Its banks name {declared.Count:N0} sounds outright; " +
                $"{containers.WithChildren:N0} of {containers.Total:N0} containers gave up their children.");
        }
    }

    /// <summary>
    /// What proportion of a container's sounds get a name back, across every
    /// install.
    /// </summary>
    /// <remarks>
    /// InitialDownloadChunk is in the list deliberately. It is where the spoken
    /// lines of the launch characters live — one character's own container holds
    /// 71 sounds, all of them effects — and its name matches no package, so naming it at
    /// all depends on the catalogue covering the whole install rather than the
    /// packages named after the container.
    /// </remarks>
    [Fact]
    public void NamesAreRecoveredForMostSoundsInACharactersContainer()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        string[] subjects = ["Angela", "Carnage", "Thor", "Cable", "InitialDownloadChunk"];

        foreach (GameClient client in clients)
        {
            var clock = Stopwatch.StartNew();
            SoundNameCatalog catalog = SoundNameCatalog.LoadOrBuild(client);
            clock.Stop();

            _output.WriteLine(
                $"{client.DisplayName}: {catalog.Count:N0} names, ready in {clock.ElapsedMilliseconds:N0} ms.");

            Assert.True(catalog.Count > 0, $"{client.DisplayName}: no names were found at all.");

            foreach (string subject in subjects)
            {
                string container = Path.Combine(client.CookedPath, $"SFX_{subject}_INT.pck");
                if (!File.Exists(container)) continue;

                AudioPackage package = AudioPackage.Open(container);
                SoundNameIndex names = SoundNames.Recover(package, catalog);

                int sounds = package.Sounds.Count();
                int withNames = package.Sounds.Count(s => names.Of(s.Id) is not null);

                _output.WriteLine(
                    $"   {subject,-22} {withNames,6:N0} of {sounds,6:N0} named " +
                    $"({(sounds == 0 ? 0 : withNames * 100.0 / sounds):F0}%), " +
                    $"{names.NamedEventCount:N0} of {names.EventCount:N0} events named.");

                Assert.True(sounds == 0 || withNames * 100.0 / sounds > 80,
                    $"{client.DisplayName} — {subject}: only {withNames} of {sounds} sounds were named.");
            }
        }
    }

    /// <summary>
    /// How the recovered names spread across the groups, and how much lands in
    /// the general one.
    /// </summary>
    /// <remarks>
    /// A classifier that files everything under something looks tidy and lies.
    /// This reports the share left as "other" so it stays visible.
    /// </remarks>
    [Fact]
    public void SoundsSortIntoGroupsWithoutMostLandingInTheGeneralOne()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        GameClient client = clients[0];
        SoundNameCatalog catalog = SoundNameCatalog.LoadOrBuild(client);
        var counts = new Dictionary<SoundKind, int>();
        int total = 0;

        foreach (string subject in new[] { "Angela", "Cable", "Beast", "Carnage", "Thor" })
        {
            string path = Path.Combine(client.CookedPath, $"SFX_{subject}_INT.pck");
            if (!File.Exists(path)) continue;

            AudioPackage package = AudioPackage.Open(path);
            SoundNameIndex names = SoundNames.Recover(package, catalog);

            foreach (AudioEntry sound in package.Sounds)
            {
                SoundKind kind = SoundKinds.Of(names.Of(sound.Id));

                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                total++;
            }
        }

        Assert.True(total > 0, "no sounds were examined.");

        foreach ((SoundKind kind, int count) in counts.OrderByDescending(k => k.Value))
            _output.WriteLine($"   {SoundKinds.NameOf(kind),-26} {count,6:N0}  ({count * 100.0 / total:F0}%)");

        int vague = counts.GetValueOrDefault(SoundKind.OtherVoice) + counts.GetValueOrDefault(SoundKind.Unnamed);

        _output.WriteLine($"{client.DisplayName}: {total:N0} sounds, {vague * 100.0 / total:F0}% left general.");

        Assert.True(vague < total / 2,
            $"more than half the sounds ({vague} of {total}) could not be told apart.");
    }
}