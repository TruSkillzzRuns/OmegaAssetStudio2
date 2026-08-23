namespace OmegaAssetStudio2.Core.Packages.Compression;

/// <summary>
/// LZO1X compression, in managed code.
/// </summary>
/// <remarks>
/// Needed to write texture data back into the shared cache, where every slot was
/// measured to hold compressed data — there is no stored-uncompressed path to
/// take instead.
/// <para>
/// This emits a stream in the same format <see cref="Lzo1x"/> reads. It does not
/// attempt to match the original compressor's ratio; it aims to be correct and
/// to produce output that round-trips exactly. Callers must check the result
/// fits the slot they are writing into, because a worse ratio than the original
/// is entirely possible.
/// </para>
/// <para>
/// Correctness here is not a matter of judgement: compressing and then
/// decompressing must reproduce the input byte for byte, and the tests assert
/// that over real game data.
/// </para>
/// </remarks>
public static class Lzo1xCompressor
{
    /// <summary>Bits of hash used to index the match table.</summary>
    private const int HashBits = 16;
    private const int HashSize = 1 << HashBits;

    /// <summary>
    /// How many earlier positions to consider per hash before settling.
    /// </summary>
    /// <remarks>
    /// Keeping only the most recent position per hash costs real ratio: a
    /// slightly older position often yields a much longer match. Walking a short
    /// chain and taking the best recovers most of that, which matters because a
    /// replacement texture has to compress into the slot the original occupies —
    /// a worse ratio is the difference between a texture being editable and being
    /// refused.
    /// </remarks>
    private const int MatchCandidates = 8;

    /// <summary>A match long enough to stop searching for a better one.</summary>
    private const int GoodEnoughMatch = 64;

    /// <summary>
    /// Longest back-reference the format can encode: one bit lives in the tag and
    /// fourteen in the two offset bytes, on top of the 0x4000 base.
    /// </summary>
    private const int MaxMatchOffset = 0xBFFF;

    /// <summary>Shortest run worth encoding as a match rather than as literals.</summary>
    private const int MinMatchLength = 3;

    /// <summary>
    /// Compresses <paramref name="source"/>.
    /// </summary>
    /// <returns>The compressed stream, which always decompresses back to the input.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        // Worst case for this format is a little larger than the input, since a
        // pathological stream is emitted as literals plus run headers.
        byte[] output = new byte[source.Length + (source.Length / 16) + 64 + 3];
        int written = Compress(source, output);

        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Compresses into a caller-supplied buffer. Returns the bytes written.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int op = 0;

        if (source.Length <= MinMatchLength + 1)
        {
            // Too short for any match; emit as a single literal run.
            op = EmitFirstLiterals(source, destination, 0, source.Length, op);
            return EmitEndMarker(destination, op);
        }

        int[] hashHead = new int[HashSize];
        Array.Fill(hashHead, -1);

        // Previous position sharing each position's hash, forming a chain.
        int[] previous = new int[source.Length];
        Array.Fill(previous, -1);

        int ip = 0;              // read cursor
        int literalStart = 0;    // start of the pending literal run
        bool anyMatchEmitted = false;

        while (ip + MinMatchLength + 1 < source.Length)
        {
            int hash = Hash(source, ip);

            // Link this position into the chain before searching, so the chain
            // stays complete even when no match is found here.
            previous[ip] = hashHead[hash];
            hashHead[hash] = ip;

            int bestCandidate = -1;
            int matchLength = 0;

            int candidate = previous[ip];
            for (int tried = 0; tried < MatchCandidates && candidate >= 0; tried++)
            {
                int candidateOffset = ip - candidate;
                if (candidateOffset <= 0 || candidateOffset > MaxMatchOffset) break;

                if (MatchesAt(source, candidate, ip, MinMatchLength))
                {
                    int length = MeasureMatch(source, candidate, ip);
                    if (length > matchLength)
                    {
                        matchLength = length;
                        bestCandidate = candidate;

                        // Long enough that hunting further is not worth the time.
                        if (length >= GoodEnoughMatch) break;
                    }
                }

                candidate = previous[candidate];
            }

            if (bestCandidate < 0)
            {
                ip++;
                continue;
            }

            candidate = bestCandidate;
            int offset = ip - candidate;

            // Emit whatever literals preceded this match.
            int literalCount = ip - literalStart;
            op = anyMatchEmitted
                ? EmitLiterals(source, destination, literalStart, literalCount, op)
                : EmitFirstLiterals(source, destination, literalStart, literalCount, op);

            op = EmitMatch(destination, op, matchLength, offset);
            anyMatchEmitted = true;

            // Index the bytes the match covered so later matches can find them.
            for (int i = ip + 1; i < ip + matchLength && i + MinMatchLength + 1 < source.Length; i++)
            {
                int coveredHash = Hash(source, i);
                previous[i] = hashHead[coveredHash];
                hashHead[coveredHash] = i;
            }

            ip += matchLength;
            literalStart = ip;
        }

        // Trailing literals.
        int remaining = source.Length - literalStart;
        op = anyMatchEmitted
            ? EmitLiterals(source, destination, literalStart, remaining, op)
            : EmitFirstLiterals(source, destination, literalStart, remaining, op);

        return EmitEndMarker(destination, op);
    }

    private static int Hash(ReadOnlySpan<byte> data, int at)
    {
        // Four bytes folded down to the table width. Any stable mixing works; the
        // decompressor never sees this.
        uint value = (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));
        return (int)((value * 2654435761u) >> (32 - HashBits)) & (HashSize - 1);
    }

    private static bool MatchesAt(ReadOnlySpan<byte> data, int a, int b, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (data[a + i] != data[b + i]) return false;
        }
        return true;
    }

    private static int MeasureMatch(ReadOnlySpan<byte> data, int candidate, int ip)
    {
        int length = MinMatchLength;

        // Matches may overlap the current position; that is how runs are encoded.
        while (ip + length < data.Length && data[candidate + length] == data[ip + length])
            length++;

        return length;
    }

    /// <summary>
    /// Emits the literal run that opens the stream, which has its own encoding.
    /// </summary>
    private static int EmitFirstLiterals(
        ReadOnlySpan<byte> source, Span<byte> destination, int start, int count, int op)
    {
        if (count == 0) return op;

        if (count <= 238)
        {
            destination[op++] = (byte)(17 + count);
        }
        else
        {
            destination[op++] = 0;
            op = EmitLongLength(destination, op, count - 3, 15);
        }

        source.Slice(start, count).CopyTo(destination[op..]);
        return op + count;
    }

    /// <summary>Emits a literal run that follows a match.</summary>
    private static int EmitLiterals(
        ReadOnlySpan<byte> source, Span<byte> destination, int start, int count, int op)
    {
        if (count == 0) return op;

        if (count <= 3)
        {
            // Short runs ride in the low bits of the previous match's last byte.
            destination[op - 2] |= (byte)count;
        }
        else if (count <= 18)
        {
            destination[op++] = (byte)(count - 3);
        }
        else
        {
            destination[op++] = 0;
            op = EmitLongLength(destination, op, count - 3, 15);
        }

        source.Slice(start, count).CopyTo(destination[op..]);
        return op + count;
    }

    /// <summary>
    /// Emits a length too large for its tag, as zero bytes followed by a
    /// remainder. This mirrors how the decompressor reads extended lengths.
    /// </summary>
    private static int EmitLongLength(Span<byte> destination, int op, int length, int bias)
    {
        int remaining = length - bias;

        while (remaining > 255)
        {
            destination[op++] = 0;
            remaining -= 255;
        }

        destination[op++] = (byte)remaining;
        return op;
    }

    private static int EmitMatch(Span<byte> destination, int op, int length, int offset)
    {
        if (length <= 8 && offset <= 0x0800)
        {
            int encoded = offset - 1;
            destination[op++] = (byte)(((length - 1) << 5) | ((encoded & 7) << 2));
            destination[op++] = (byte)(encoded >> 3);
            return op;
        }

        if (offset <= 0x4000)
        {
            int encoded = offset - 1;

            if (length <= 33)
            {
                destination[op++] = (byte)(32 | (length - 2));
            }
            else
            {
                destination[op++] = 32;
                op = EmitLongLength(destination, op, length - 2, 31);
            }

            destination[op++] = (byte)((encoded << 2) & 0xFF);
            destination[op++] = (byte)(encoded >> 6);
            return op;
        }

        // Long-distance match. The distance is split: its top bit rides in the
        // tag and the remaining fourteen go in the two offset bytes.
        //
        // Writing the distance unmasked here is a silent corruption: for values
        // at or above 0x4000 the two bytes overflow to zero, and a zero distance
        // is exactly how the format signals end of stream. Decompression then
        // stops early with a plausible-looking truncated result.
        int far = offset - 0x4000;
        int lowBits = far & 0x3FFF;
        int tagHighBit = (far >> 11) & 8;

        if (length <= 9)
        {
            destination[op++] = (byte)(16 | tagHighBit | (length - 2));
        }
        else
        {
            destination[op++] = (byte)(16 | tagHighBit);
            op = EmitLongLength(destination, op, length - 2, 7);
        }

        destination[op++] = (byte)((lowBits << 2) & 0xFF);
        destination[op++] = (byte)(lowBits >> 6);
        return op;
    }

    /// <summary>
    /// Writes the sequence that tells the decompressor the stream has ended.
    /// </summary>
    private static int EmitEndMarker(Span<byte> destination, int op)
    {
        destination[op++] = 16 | 1;   // long-distance match tag with length 3
        destination[op++] = 0;
        destination[op++] = 0;
        return op;
    }
}
