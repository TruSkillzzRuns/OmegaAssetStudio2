namespace OmegaAssetStudio2.Core.Packages.Compression;

/// <summary>
/// LZO1X decompression, in managed code.
/// </summary>
/// <remarks>
/// Every cooked package in the supported game is LZO-compressed, so this is
/// required to read anything at all.
/// <para>
/// This is a clean-room implementation of the LZO1X decompression format. It
/// deliberately does not bundle or link the reference LZO library, which is
/// GPL-licensed and would impose a source-availability obligation on every
/// distribution of this application. The compressed format is not covered by
/// that licence; only the reference implementation is.
/// </para>
/// <para>
/// The control flow below is flat, with every label at method scope, because the
/// encoding is a state machine whose literal and match paths share exit points.
/// Restructuring it into nested loops would break the correspondence to the
/// format description and make it unreviewable.
/// </para>
/// </remarks>
public static class Lzo1x
{
    /// <summary>Offset bound for the short-match encoding.</summary>
    private const int M2MaxOffset = 0x0800;

    /// <summary>
    /// Decompresses <paramref name="source"/> into a new buffer of exactly
    /// <paramref name="uncompressedSize"/> bytes.
    /// </summary>
    /// <exception cref="InvalidPackageException">
    /// The stream is malformed, or decoding would read or write out of bounds.
    /// </exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new InvalidPackageException($"Negative uncompressed size {uncompressedSize}.");

        byte[] output = new byte[uncompressedSize];
        int written = Decompress(source, output);

        if (written != uncompressedSize)
            throw new InvalidPackageException(
                $"Decompressed {written} bytes but the block header declared {uncompressedSize}.");

        return output;
    }

    /// <summary>
    /// Decompresses into a caller-supplied buffer. Returns the bytes written.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            throw new InvalidPackageException("Cannot decompress an empty block.");

        int ip = 0;         // read cursor
        int op = 0;         // write cursor
        int matchPos;       // back-reference cursor into destination
        int t;              // run or match length, reused throughout

        // A first byte above 17 means the stream opens with a literal run.
        if (source[0] > 17)
        {
            t = ReadByte(source, ref ip) - 17;
            if (t < 4) goto MatchNext;

            CopyLiterals(source, ref ip, destination, ref op, t);
            goto FirstLiteralRun;
        }

    NextOperation:
        t = ReadByte(source, ref ip);
        if (t >= 16) goto Match;

        if (t == 0) t = ReadExtendedLength(source, ref ip, seed: 0, bias: 15, limit: destination.Length);
        CopyLiterals(source, ref ip, destination, ref op, t + 3);

    FirstLiteralRun:
        t = ReadByte(source, ref ip);
        if (t >= 16) goto Match;

        matchPos = op - (1 + M2MaxOffset) - (t >> 2) - (ReadByte(source, ref ip) << 2);
        CopyMatch(destination, ref op, matchPos, 3);
        goto MatchDone;

    Match:
        if (t >= 64)
        {
            matchPos = op - 1 - ((t >> 2) & 7) - (ReadByte(source, ref ip) << 3);
            t = (t >> 5) - 1;
        }
        else if (t >= 32)
        {
            t &= 31;
            if (t == 0) t = ReadExtendedLength(source, ref ip, seed: 0, bias: 31, limit: destination.Length);

            matchPos = op - 1 - ReadOffset(source, ref ip);
        }
        else if (t >= 16)
        {
            matchPos = op - ((t & 8) << 11);

            t &= 7;
            if (t == 0) t = ReadExtendedLength(source, ref ip, seed: 0, bias: 7, limit: destination.Length);

            matchPos -= ReadOffset(source, ref ip);

            // A back-reference resolving to the current position is the
            // end-of-stream marker, not a real match.
            if (matchPos == op) goto Done;

            matchPos -= 0x4000;
        }
        else
        {
            matchPos = op - 1 - (t >> 2) - (ReadByte(source, ref ip) << 2);
            CopyMatch(destination, ref op, matchPos, 2);
            goto MatchDone;
        }

        CopyMatch(destination, ref op, matchPos, t + 2);

    MatchDone:
        // The low two bits of the second-to-last consumed byte carry a short
        // literal run that follows the match.
        if (ip < 2)
            throw new InvalidPackageException("Malformed stream: match state reached before any operand bytes.");

        t = source[ip - 2] & 3;
        if (t == 0) goto NextOperation;

    MatchNext:
        CopyLiterals(source, ref ip, destination, ref op, t);
        t = ReadByte(source, ref ip);
        goto Match;

    Done:
        return op;
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int ip)
    {
        if (ip >= source.Length)
            throw new InvalidPackageException($"Compressed stream ended early at input offset {ip}.");
        return source[ip++];
    }

    /// <summary>Reads the little-endian 14-bit back-reference distance.</summary>
    private static int ReadOffset(ReadOnlySpan<byte> source, ref int ip)
    {
        byte low = ReadByte(source, ref ip);
        byte high = ReadByte(source, ref ip);
        return (low >> 2) + (high << 6);
    }

    private static void CopyLiterals(
        ReadOnlySpan<byte> source, ref int ip, Span<byte> destination, ref int op, int count)
    {
        if (count < 0)
            throw new InvalidPackageException($"Negative literal run {count}.");
        if (ip + count > source.Length)
            throw new InvalidPackageException(
                $"Literal run of {count} at input offset {ip} runs past the end of the block.");
        if (op + count > destination.Length)
            throw new InvalidPackageException(
                $"Literal run of {count} at output offset {op} would overflow the {destination.Length}-byte output.");

        source.Slice(ip, count).CopyTo(destination.Slice(op, count));
        ip += count;
        op += count;
    }

    /// <summary>
    /// Copies from earlier in the output. Source and destination ranges may
    /// overlap — that is how runs are encoded — so this copies byte by byte and
    /// must not be replaced with a bulk move.
    /// </summary>
    private static void CopyMatch(Span<byte> destination, ref int op, int from, int count)
    {
        if (from < 0)
            throw new InvalidPackageException(
                $"Back-reference to {from} points before the start of the output.");
        if (count < 0)
            throw new InvalidPackageException($"Negative match length {count}.");
        if (from + count > destination.Length || op + count > destination.Length)
            throw new InvalidPackageException(
                $"Match of {count} bytes at output offset {op} would overflow the " +
                $"{destination.Length}-byte output.");

        for (int i = 0; i < count; i++)
            destination[op++] = destination[from++];
    }

    /// <summary>
    /// Decodes a length encoded as a run of zero bytes followed by a remainder.
    /// </summary>
    private static int ReadExtendedLength(
        ReadOnlySpan<byte> source, ref int ip, int seed, int bias, int limit)
    {
        int length = seed;
        while (true)
        {
            byte b = ReadByte(source, ref ip);
            if (b != 0) return length + bias + b;

            length += 255;
            if (length > limit)
                throw new InvalidPackageException(
                    $"Extended length exceeded the {limit}-byte output; the block is corrupt.");
        }
    }
}
