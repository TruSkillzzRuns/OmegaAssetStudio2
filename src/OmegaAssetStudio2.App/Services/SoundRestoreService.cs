using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;
using UpkManager.Repository;

namespace OmegaAssetStudio2.App.Services;

/// <summary>
/// Puts a changed package's sound wiring back the way it shipped.
/// </summary>
/// <remarks>
/// A tool that adds sound to a package can leave no way of taking it out
/// again, and then one wrong line costs the whole piece of work: the only way
/// back is to start from a clean package and import the pictures, the models
/// and the animations all over again. Nothing about that is necessary. The
/// sounds are wired up in a handful of exports, everything else in the package
/// is untouched by them, and the way it shipped is sitting in the game folder.
/// <para>
/// So each export is put back on its own, through the repacker's own
/// one-export path, and the file is written whole only once at the end. The
/// package is copied aside first.
/// </para>
/// </remarks>
public static class SoundRestoreService
{
    /// <summary>What came of putting the sound back.</summary>
    /// <param name="Ok">Whether anything was written.</param>
    /// <param name="Message">What happened, in words.</param>
    /// <param name="PutBack">How many exports were put back.</param>
    /// <param name="BackupPath">Where the package was copied to first.</param>
    public sealed record Outcome(bool Ok, string Message, int PutBack = 0, string? BackupPath = null);

    /// <summary>
    /// Finds the package as it shipped, beside the game's own cooked content.
    /// </summary>
    public static string? ShippedCounterpart(string changedPath, string cookedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath) || string.IsNullOrWhiteSpace(cookedPath)) return null;

        string beside = Path.Combine(cookedPath, Path.GetFileName(changedPath));

        return File.Exists(beside) ? beside : null;
    }

    /// <summary>
    /// The recordings behind named events, wherever they are kept.
    /// </summary>
    /// <remarks>
    /// A package names an event; a container beside it holds the recording that
    /// event sets off. Each language keeps its own recording of the same line,
    /// and a line spoken more than once keeps a take apiece, so one event
    /// commonly stands in front of several recordings. All of them are handed
    /// back rather than one being chosen here.
    /// </remarks>
    public static IReadOnlyList<PlacedSound> RecordingsBehind(
        GameClient client, IReadOnlyCollection<string> eventNames)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(eventNames);

        if (eventNames.Count == 0) return [];

        try
        {
            return CostumeVoices.SoundsBehind(client, eventNames, string.Empty);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts a different recording in place of one the game ships.
    /// </summary>
    /// <remarks>
    /// This writes to the container, not to the package: the package only names
    /// the sound. So it changes that line for everything that plays it, not for
    /// one costume alone, and the container is what gets copied aside.
    /// </remarks>
    public static async Task<Outcome> ReplaceRecordingAsync(
        PlacedSound sound, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sound);

        if (!File.Exists(filePath)) return new(false, "that file is not there");

        byte[] replacement;

        try
        {
            replacement = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(false, "that file could not be read: " + ex.Message);
        }

        AudioPackage container;

        try { container = AudioPackage.Open(sound.ContainerPath); }
        catch (Exception ex) { return new(false, "the container could not be opened: " + ex.Message); }

        AudioReplaceResult done = await AudioReplacer
            .ReplaceAsync(container, sound.Entry, replacement, ct)
            .ConfigureAwait(false);

        return new(done.Succeeded,
            done.Message,
            done.Succeeded ? 1 : 0,
            done.Succeeded ? sound.ContainerPath : null);
    }

    /// <summary>
    /// Points single moments at a different sound the package already names.
    /// </summary>
    /// <remarks>
    /// For when a line is not wanted gone but wanted different. What a moment
    /// plays is four bytes saying where that sound is kept, so this writes four
    /// others in their place - the table keeps its length and its shape, and
    /// nothing around it moves.
    /// </remarks>
    /// <param name="soundName">The sound to play, as the package names it.</param>
    public static async Task<Outcome> RepointAsync(
        string changedPath,
        string holder,
        IReadOnlyCollection<string> moments,
        string soundName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moments);

        if (!File.Exists(changedPath)) return new(false, "the package is not there");
        if (moments.Count == 0) return new(false, "nothing was chosen to point elsewhere");
        if (string.IsNullOrWhiteSpace(soundName)) return new(false, "no sound was chosen");

        int at = -1;
        PackageSounds.Repointing pointing;

        try
        {
            Package package = Package.Open(changedPath);

            for (int i = 0; i < package.Exports.Count; i++)
            {
                if (!package.GetExportName(i).Equals(holder, StringComparison.OrdinalIgnoreCase)) continue;

                at = i;
                break;
            }

            if (at < 0) return new(false, $"the package holds no table called {holder}");

            PackageSounds.Available? sound = PackageSounds.SoundsIn(package)
                .FirstOrDefault(s => s.Name.Equals(soundName, StringComparison.OrdinalIgnoreCase));

            if (sound is null)
                return new(false, $"this package does not name a sound called {soundName}");

            pointing = PackageSounds.Repointed(package, at, moments, sound.At);
        }
        catch (Exception ex)
        {
            return new(false, "the package could not be read: " + ex.Message);
        }

        // What was left alone, and why, said plainly rather than passed over.
        string lists = pointing.HoldingLists.Count == 0
            ? string.Empty
            : $"  {string.Join(", ", pointing.HoldingLists)} name several sounds rather than one, "
              + "so they were left as they are.";

        if (pointing.Bytes is null)
            return new(false, "none of those could be pointed elsewhere." + lists);

        string backup = changedPath + ".before-sound-restore";

        try
        {
            File.Copy(changedPath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            return new(false, "the package could not be copied aside first: " + ex.Message);
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(changedPath, ct).ConfigureAwait(false);

            var repository = new UpkFileRepository();

            var header = await repository.LoadUpkFile(changedPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            bytes = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressed(bytes, header, at, pointing.Bytes)
                : OmegaAssetStudio.UpkRepacker.Repack(bytes, header, at, pointing.Bytes);

            string between = changedPath + ".omtmp";

            await File.WriteAllBytesAsync(between, bytes, ct).ConfigureAwait(false);
            File.Move(between, changedPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Copy(backup, changedPath, overwrite: true); }
            catch (Exception) { }

            return new(false, "it could not be written, and the package was put back: " + ex.Message);
        }

        return new(true,
            $"{string.Join(", ", pointing.Pointed)} now play {soundName}." + lists,
            pointing.Pointed.Count,
            backup);
    }

    /// <summary>
    /// Quiets single moments, leaving the rest of the table as it is.
    /// </summary>
    /// <remarks>
    /// For when one line is wrong and the others are wanted. The moments named
    /// are cut out of the table they sit in; every other moment, and everything
    /// else in the package, is left exactly as found.
    /// </remarks>
    /// <param name="changedPath">The package to work on.</param>
    /// <param name="holder">The table the moments sit in.</param>
    /// <param name="moments">Which moments to quiet.</param>
    public static async Task<Outcome> QuietAsync(
        string changedPath,
        string holder,
        IReadOnlyCollection<string> moments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(moments);

        if (!File.Exists(changedPath)) return new(false, "the package is not there");
        if (moments.Count == 0) return new(false, "nothing was chosen to quiet");

        int at = -1;
        byte[]? shortened;

        try
        {
            Package package = Package.Open(changedPath);

            for (int i = 0; i < package.Exports.Count; i++)
            {
                if (!package.GetExportName(i).Equals(holder, StringComparison.OrdinalIgnoreCase)) continue;

                at = i;
                break;
            }

            if (at < 0) return new(false, $"the package holds no table called {holder}");

            shortened = PackageSounds.WithoutMoments(package, at, moments);
        }
        catch (Exception ex)
        {
            return new(false, "the package could not be read: " + ex.Message);
        }

        if (shortened is null)
            return new(false, "none of those moments are in that table, so there is nothing to quiet");

        string backup = changedPath + ".before-sound-restore";

        try
        {
            File.Copy(changedPath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            return new(false, "the package could not be copied aside first: " + ex.Message);
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(changedPath, ct).ConfigureAwait(false);

            var repository = new UpkFileRepository();

            var header = await repository.LoadUpkFile(changedPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            bytes = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressed(bytes, header, at, shortened)
                : OmegaAssetStudio.UpkRepacker.Repack(bytes, header, at, shortened);

            string between = changedPath + ".omtmp";

            await File.WriteAllBytesAsync(between, bytes, ct).ConfigureAwait(false);
            File.Move(between, changedPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Copy(backup, changedPath, overwrite: true); }
            catch (Exception) { }

            return new(false, "it could not be written, and the package was put back: " + ex.Message);
        }

        string named = string.Join(", ", moments);

        return new(true,
            $"quieted {moments.Count} of them ({named}). The rest of {holder}, and everything else "
            + "in the package, is as it was.",
            moments.Count,
            backup);
    }

    /// <summary>
    /// Puts the named exports back, and writes the package.
    /// </summary>
    /// <param name="changedPath">The package that was changed.</param>
    /// <param name="shippedPath">The same package as the game ships it.</param>
    /// <param name="exportNames">Which exports to put back.</param>
    public static async Task<Outcome> RestoreAsync(
        string changedPath,
        string shippedPath,
        IReadOnlyCollection<string> exportNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exportNames);

        if (!File.Exists(changedPath)) return new(false, "the changed package is not there");
        if (!File.Exists(shippedPath)) return new(false, "the package as it shipped is not there");
        if (exportNames.Count == 0) return new(false, "nothing was chosen to put back");

        IReadOnlyList<PackageSounds.PutBack> wanted;

        try
        {
            Package changed = Package.Open(changedPath);
            Package shipped = Package.Open(shippedPath);

            wanted = PackageSounds.WhatToPutBack(
                changed, shipped, exportNames, (p, at) => p.GetExportData(at).ToArray());
        }
        catch (Exception ex)
        {
            return new(false, "the packages could not be read: " + ex.Message);
        }

        if (wanted.Count == 0)
        {
            return new(false,
                "none of those are in the package as it shipped, so there is nothing to put back. "
                + "A sound an imported animation carries is in the animation itself, not in a "
                + "table that shipped with the costume.");
        }

        string backup = changedPath + ".before-sound-restore";

        try
        {
            File.Copy(changedPath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            return new(false, "the package could not be copied aside first: " + ex.Message);
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(changedPath, ct).ConfigureAwait(false);

            var repository = new UpkFileRepository();

            // One at a time, each against the file as the last one left it.
            // The repacker works out every offset in the file afresh each time,
            // so an export whose length changes is no trouble.
            foreach (PackageSounds.PutBack one in wanted)
            {
                var header = await repository.LoadUpkFile(changedPath).ConfigureAwait(false);
                await header.ReadHeaderAsync(null).ConfigureAwait(false);

                if (one.ChangedAt < 0 || one.ChangedAt >= header.ExportTable.Count)
                    return new(false, $"the package no longer holds {one.Name} where it did");

                bytes = header.CompressedChunks.Count > 0
                    ? OmegaAssetStudio.UpkRepacker.RepackCompressed(
                        bytes, header, one.ChangedAt, one.Shipped)
                    : OmegaAssetStudio.UpkRepacker.Repack(
                        bytes, header, one.ChangedAt, one.Shipped);

                // Written between each one so the next reads what the last did.
                string between = changedPath + ".omtmp";

                await File.WriteAllBytesAsync(between, bytes, ct).ConfigureAwait(false);
                File.Move(between, changedPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // Back to how it was found, so a run that stops leaves nothing worse.
            try { File.Copy(backup, changedPath, overwrite: true); }
            catch (Exception) { }

            return new(false, "it could not be written, and the package was put back: " + ex.Message);
        }

        string names = string.Join(", ", wanted.Select(w => w.Name));

        return new(true,
            $"put back {wanted.Count} of them ({names}). Everything else in the package is as it was.",
            wanted.Count,
            backup);
    }
}
