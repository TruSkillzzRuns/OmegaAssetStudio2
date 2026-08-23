using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using OmegaAssetStudio2.Core.Tests;
using Xunit;
using Xunit.Abstractions;

namespace OmegaAssetStudio2.Core.Tests.Audio;

public sealed class AudioReplacerTests
{
    private static AudioEntry Entry(int size) => new()
    {
        Kind = AudioEntryKind.Stream,
        Id = 1234,
        Offset = 4096,
        Size = size,
        LanguageId = 0,
        Language = "english(us)",
        RecordOffset = 100,
    };

    private static byte[] Sound(int length)
    {
        byte[] data = new byte[length];
        "RIFF"u8.CopyTo(data);
        return data;
    }

    [Fact]
    public void AcceptsASoundThatFitsTheSlot()
        => Assert.True(AudioReplacer.CanReplace(Entry(2048), Sound(1024)).Succeeded);

    [Fact]
    public void AcceptsASoundOfExactlyTheSlotSize()
        => Assert.True(AudioReplacer.CanReplace(Entry(2048), Sound(2048)).Succeeded);

    [Fact]
    public void RefusesASoundLargerThanTheSlot()
    {
        // A larger sound would run into whatever follows it in the container.
        AudioReplaceResult result = AudioReplacer.CanReplace(Entry(1024), Sound(2048));

        Assert.False(result.Succeeded);
        Assert.Equal(AudioRefusal.ReplacementTooLarge, result.Refusal);
        Assert.Contains("2,048", result.Message);
    }

    [Fact]
    public void RefusesAFileThatIsNotAWwiseSound()
    {
        // A plain wav or mp3 will not play in game, so accepting it would produce
        // silence rather than an error the user can act on.
        AudioReplaceResult result = AudioReplacer.CanReplace(Entry(2048), new byte[] { 0x00, 0x01, 0x02, 0x03 });

        Assert.False(result.Succeeded);
        Assert.Equal(AudioRefusal.NotAWwiseSound, result.Refusal);
        Assert.Contains(".wem", result.Message);
    }

    [Fact]
    public void RefusesAnEmptyFile()
        => Assert.Equal(AudioRefusal.ReplacementEmpty, AudioReplacer.CanReplace(Entry(2048), []).Refusal);

    [Theory]
    [InlineData("RIFF")]
    [InlineData("RIFX")]
    public void RecognisesBothSoundSignatures(string magic)
        => Assert.True(AudioReplacer.LooksLikeWwiseSound(System.Text.Encoding.ASCII.GetBytes(magic + "xxxx")));
}

/// <summary>Reads the real audio containers from every installed client.</summary>
public sealed class RealAudioTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _scratch;

    public RealAudioTests(ITestOutputHelper output)
    {
        _output = output;
        _scratch = Scratch.NewFolder("oas2-audio");
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

    [Fact]
    public void ReadsEveryContainerInEveryClient()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string[] files = Directory.GetFiles(client.CookedPath, "*.pck");
            if (files.Length == 0)
            {
                _output.WriteLine($"{client.DisplayName}: no audio containers.");
                continue;
            }

            int read = 0, failed = 0;
            long sounds = 0;

            foreach (string file in files)
            {
                try
                {
                    AudioPackage package = AudioPackage.Open(file);

                    // Every sound must sit inside the file it claims to be in.
                    long length = new FileInfo(file).Length;
                    foreach (AudioEntry entry in package.Entries)
                    {
                        Assert.True(entry.Offset >= 0 && entry.Offset + entry.Size <= length,
                            $"{Path.GetFileName(file)}: sound {entry.Id} runs past the end of the file.");
                        Assert.True(entry.Size >= 0);
                    }

                    sounds += package.Entries.Count;
                    read++;
                }
                catch (InvalidPackageException)
                {
                    failed++;
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: read {read}/{files.Length} containers, {sounds:N0} sounds.");

            // The section-size check inside the reader means a wrong layout throws
            // rather than producing nonsense, so a high read rate is meaningful.
            Assert.True(read > files.Length * 0.95,
                $"{client.DisplayName}: only {read} of {files.Length} containers parsed.");
        }
    }

    [Fact]
    public void SoundsCarryTheWwiseSignature()
    {
        // Offsets and sizes could be self-consistent and still wrong. Reading the
        // bytes and finding a real sound header is what proves they point at
        // actual audio.
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string? file = Directory.EnumerateFiles(client.CookedPath, "SFX_*_INT.pck").FirstOrDefault();
            if (file is null) continue;

            AudioPackage package = AudioPackage.Open(file);
            AudioEntry[] sample = package.Streams.Take(25).ToArray();
            Assert.NotEmpty(sample);

            int valid = 0;
            foreach (AudioEntry entry in sample)
            {
                byte[] data = package.ReadEntryData(entry);
                Assert.Equal(entry.Size, data.Length);
                if (AudioReplacer.LooksLikeWwiseSound(data)) valid++;
            }

            _output.WriteLine(
                $"{client.DisplayName} — {Path.GetFileName(file)}: {valid}/{sample.Length} sounds " +
                $"begin with a valid signature.");

            Assert.Equal(sample.Length, valid);
        }
    }

    [Fact]
    public async Task ScanSummarisesEveryContainer()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        var catalog = new AudioCatalog();

        foreach (GameClient client in clients)
        {
            IReadOnlyList<AudioPackageSummary> packages = await catalog.ScanAsync(client);
            if (packages.Count == 0) continue;

            long sounds = packages.Sum(p => (long)p.StreamCount);
            int withLanguage = packages.Count(p => p.Language.Length > 0);

            _output.WriteLine(
                $"{client.DisplayName}: {packages.Count} containers, {sounds:N0} streamed sounds, " +
                $"{withLanguage} language-specific. Subjects include: " +
                string.Join(", ", packages.Select(p => p.Subject).Distinct().Take(6)));

            Assert.All(packages, p => Assert.False(string.IsNullOrWhiteSpace(p.Subject)));
        }
    }

    /// <summary>
    /// Sounds held inside banks are found, and each is really where recorded.
    /// </summary>
    /// <remarks>
    /// Some containers stream nothing and keep every sound in their banks, so
    /// reading only the stream table reported those as empty. The check that means
    /// something is not the count but the position: each recorded place must
    /// hold a sound header. A wrong offset would still count, and would still
    /// be wrong.
    /// </remarks>
    [Fact]
    public void SoundsInsideBanksAreFoundWhereTheyAreRecorded()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            int containers = 0, embedded = 0, bankOnly = 0, misplaced = 0;

            foreach (string path in Directory.EnumerateFiles(client.CookedPath, "*.pck")
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                AudioPackage package;
                try { package = AudioPackage.Open(path); } catch (InvalidPackageException) { continue; }

                int here = package.Embedded.Count();
                if (here == 0) continue;

                containers++;
                embedded += here;
                if (!package.Streams.Any()) bankOnly++;

                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                foreach (AudioEntry entry in package.Embedded)
                {
                    byte[] head = new byte[4];
                    file.Seek(entry.Offset, SeekOrigin.Begin);

                    if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 ||
                        !AudioReplacer.LooksLikeWwiseSound(head))
                    {
                        misplaced++;
                    }
                }
            }

            _output.WriteLine(
                $"{client.DisplayName}: {embedded:N0} sounds inside banks across {containers} containers, " +
                $"{bankOnly} of which stream nothing at all. {misplaced} were not where recorded.");

            Assert.Equal(0, misplaced);
            Assert.True(embedded > 0, $"{client.DisplayName}: no sounds were found inside any bank.");
        }
    }

    [Fact]
    public async Task ReplacingASoundWritesItAndUpdatesTheRecordedSize()
    {
        List<GameClient> clients = InstalledClients();
        if (clients.Count == 0)
        {
            _output.WriteLine("No installs present; nothing probed.");
            return;
        }

        foreach (GameClient client in clients)
        {
            string? source = Directory.EnumerateFiles(client.CookedPath, "SFX_*_INT.pck")
                                      .OrderBy(p => new FileInfo(p).Length)
                                      .FirstOrDefault();
            if (source is null) continue;

            // Work on a copy. No test may modify a game install.
            string copy = Path.Combine(_scratch, $"{client.Id:N}-{Path.GetFileName(source)}");
            File.Copy(source, copy, overwrite: true);

            AudioPackage package = AudioPackage.Open(copy);

            // Pick a target and the sound that physically follows it, together.
            // Choosing them separately can land on the last sound in the file,
            // which has no neighbour to check against.
            AudioEntry[] byOffset = package.Streams.OrderBy(e => e.Offset).ToArray();
            int targetIndex = Array.FindIndex(byOffset, e => e.Size > 4096);
            if (targetIndex < 0 || targetIndex + 1 >= byOffset.Length)
            {
                _output.WriteLine($"{client.DisplayName}: no suitable sound pair in this container.");
                continue;
            }

            AudioEntry target = byOffset[targetIndex];
            AudioEntry neighbour = byOffset[targetIndex + 1];
            byte[] neighbourBefore = package.ReadEntryData(neighbour);

            // A short, valid sound: enough to prove the write and the size update.
            byte[] replacement = new byte[2048];
            "RIFF"u8.CopyTo(replacement);
            replacement[64] = 0xAB;

            AudioReplaceResult result = await AudioReplacer.ReplaceAsync(package, target, replacement);
            Assert.True(result.Succeeded, result.Message);
            Assert.True(OmegaAssetStudio2.Core.Workspace.Backup.BackupFileHelper.HasBackup(copy), "No pristine backup was taken.");

            // Re-open and confirm the new sound and its new length.
            AudioPackage reloaded = AudioPackage.Open(copy);
            AudioEntry after = reloaded.Streams.First(e => e.Id == target.Id);

            Assert.Equal(replacement.Length, after.Size);

            byte[] written = reloaded.ReadEntryData(after);
            Assert.Equal(0xAB, written[64]);
            Assert.True(AudioReplacer.LooksLikeWwiseSound(written));

            // The sound after it must be untouched: padding, not shifting.
            AudioEntry neighbourAfter = reloaded.Streams.First(e => e.Id == neighbour.Id);
            Assert.Equal(neighbour.Offset, neighbourAfter.Offset);
            Assert.True(neighbourBefore.SequenceEqual(reloaded.ReadEntryData(neighbourAfter)),
                "Replacing a sound disturbed the one after it.");

            _output.WriteLine(
                $"{client.DisplayName}: replaced sound {target.Id} " +
                $"({target.Size:N0} -> {after.Size:N0} bytes) and the next sound was preserved.");
        }
    }
}
