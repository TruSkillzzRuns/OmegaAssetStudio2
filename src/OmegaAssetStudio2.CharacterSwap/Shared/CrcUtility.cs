namespace OmegaAssetStudio;

public static class CrcUtility
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            uint index = (crc ^ value) & 0xFFu;
            crc = (crc >> 8) ^ Table[index];
        }

        return ~crc;
    }

    public static uint Compute(byte[] data) => Compute(data.AsSpan());

    // Streaming variant: keep the running pre-XOR'd state across chunks.
    // Seed with 0xFFFFFFFF, final value is ~state. Used by streaming file CRC.
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        uint crc = state;
        foreach (byte value in data)
        {
            uint index = (crc ^ value) & 0xFFu;
            crc = (crc >> 8) ^ Table[index];
        }
        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1u) != 0
                    ? (crc >> 1) ^ Polynomial
                    : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}

