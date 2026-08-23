namespace OmegaAssetStudio.Calligraphy;

// Reads just the leading portion of a PTP prototype body, AFTER the 4-byte header.
// Working hypothesis from the public format spec + first-byte inspection of a known
// prototype (BasicMelee):
//   header[0..4]      "PTP" + version byte
//   header[4]         flags byte
//   header[5..13]     parent prototype ID (uint64 LE) -- present when flag bit indicates
//
// We don't yet commit to a specific flag bit layout; this reader records the raw flags
// byte so we can bucket values empirically across the whole archive.
public sealed class PrototypeBodyReader
{
    public byte Flags { get; private set; }
    public ulong? ParentPrototypeId { get; private set; }
    public int BodyStartOffset { get; private set; }

    // Parses just enough to extract flags and (optionally) the parent prototype ID.
    // Returns true if the bytes are long enough to read the leading section.
    public static bool TryReadLeading(ReadOnlySpan<byte> data, out PrototypeBodyReader result)
    {
        result = new PrototypeBodyReader();
        if (data.Length < 5)
            return false;

        // First 4 bytes are the magic+version (caller has already validated).
        byte flags = data[4];
        result.Flags = flags;

        // Speculative: lowest bit indicates "has parent prototype reference".
        // Most prototypes observed have flag bit 0 set and 8 bytes of plausible parent ID following.
        // We record the parent ID conditionally so the empirical histogram can tell us
        // whether the bit interpretation holds.
        int offset = 5;
        if ((flags & 0x01) != 0)
        {
            if (data.Length < offset + 8)
                return true; // header readable, but no room for parent id

            ulong parentId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            result.ParentPrototypeId = parentId;
            offset += 8;
        }

        result.BodyStartOffset = offset;
        return true;
    }
}
