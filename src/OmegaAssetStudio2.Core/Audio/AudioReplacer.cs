using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Workspace;

namespace OmegaAssetStudio2.Core.Audio;

/// <summary>Why a sound cannot be swapped.</summary>
public enum AudioRefusal
{
    None = 0,
    ReplacementTooLarge,
    ReplacementEmpty,
    NotAWwiseSound,
}

/// <summary>The outcome of checking or performing a swap.</summary>
public sealed record AudioReplaceResult(bool Succeeded, string Message, AudioRefusal Refusal = AudioRefusal.None)
{
    public static AudioReplaceResult Refuse(AudioRefusal refusal, string message) => new(false, message, refusal);
    public static AudioReplaceResult Ok(string message) => new(true, message);
}

/// <summary>
/// Swaps one sound for another inside an audio container.
/// </summary>
/// <remarks>
/// The replacement is written over the original's bytes in place and the entry's
/// recorded size is updated. A shorter replacement is padded with silence rather
/// than moving anything, because every later sound's offset is recorded
/// absolutely and shifting them would mean rewriting the whole container.
/// <para>
/// A longer replacement is refused. There is nowhere to put the extra bytes
/// without moving the sound that follows.
/// </para>
/// </remarks>
public static class AudioReplacer
{
    /// <summary>Every Wwise sound begins with one of these.</summary>
    private static readonly byte[] RiffMagic = "RIFF"u8.ToArray();
    private static readonly byte[] RiffxMagic = "RIFX"u8.ToArray();

    /// <summary>Offset of the size field within an entry's record.</summary>
    private const int RecordSizeFieldOffset = 8;

    /// <summary>Checks a replacement without writing anything.</summary>
    public static AudioReplaceResult CanReplace(AudioEntry entry, ReadOnlySpan<byte> replacement)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (replacement.Length == 0)
            return AudioReplaceResult.Refuse(AudioRefusal.ReplacementEmpty, "The replacement file is empty.");

        if (!LooksLikeWwiseSound(replacement))
        {
            return AudioReplaceResult.Refuse(
                AudioRefusal.NotAWwiseSound,
                "That file is not a Wwise sound. Convert it to .wem first — a plain .wav or .mp3 " +
                "will not play in game.");
        }

        if (replacement.Length > entry.Size)
        {
            return AudioReplaceResult.Refuse(
                AudioRefusal.ReplacementTooLarge,
                $"The replacement is {replacement.Length:N0} bytes but the slot holds {entry.Size:N0}. " +
                "A larger sound would overwrite the one after it. Re-encode it smaller.");
        }

        return AudioReplaceResult.Ok(
            $"Ready to replace sound {entry.Id} ({replacement.Length:N0} of {entry.Size:N0} bytes).");
    }

    /// <summary>True when the bytes carry a Wwise sound signature.</summary>
    public static bool LooksLikeWwiseSound(ReadOnlySpan<byte> data) =>
        data.Length >= 4 &&
        (data[..4].SequenceEqual(RiffMagic) || data[..4].SequenceEqual(RiffxMagic));

    /// <summary>
    /// Swaps a sound and saves the container, taking a backup and swapping the
    /// file in atomically.
    /// </summary>
    public static async Task<AudioReplaceResult> ReplaceAsync(
        AudioPackage package,
        AudioEntry entry,
        ReadOnlyMemory<byte> replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        AudioReplaceResult check = CanReplace(entry, replacement.Span);
        if (!check.Succeeded) return check;

        try
        {
            byte[] container = await File.ReadAllBytesAsync(package.Path, cancellationToken)
                                        .ConfigureAwait(false);

            if (entry.Offset + entry.Size > container.Length)
            {
                return new AudioReplaceResult(false,
                    $"Sound {entry.Id} lies past the end of the container; it may have been changed already.");
            }

            // Overwrite the slot, then clear whatever the old sound left behind so
            // no fragment of it can be read as part of the new one.
            replacement.Span.CopyTo(container.AsSpan((int)entry.Offset, replacement.Length));
            container.AsSpan((int)entry.Offset + replacement.Length, entry.Size - replacement.Length).Clear();

            // Record the real length, so the game reads only the new sound.
            BitConverter.GetBytes(replacement.Length)
                        .CopyTo(container, entry.RecordOffset + RecordSizeFieldOffset);

            string backup = await SafeFileWriter.WriteAsync(package.Path, container, cancellationToken)
                                                .ConfigureAwait(false);

            return AudioReplaceResult.Ok(
                $"Replaced sound {entry.Id}. The original container was backed up to {Path.GetFileName(backup)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidPackageException)
        {
            return new AudioReplaceResult(false, $"Could not replace sound {entry.Id}: {ex.Message}");
        }
    }
}
