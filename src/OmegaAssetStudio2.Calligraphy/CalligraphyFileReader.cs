namespace OmegaAssetStudio.Calligraphy;

// Reads the 4-byte header common to every Calligraphy file (.prototype/.blueprint/.curve/.type/.directory).
// Spec: 3-byte ASCII magic + 1-byte version number.
public static class CalligraphyFileReader
{
    public static CalligraphyHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            throw new InvalidDataException($"Calligraphy file shorter than 4-byte header ({data.Length} bytes).");

        string magic = System.Text.Encoding.ASCII.GetString(data.Slice(0, 3));
        byte version = data[3];
        return new CalligraphyHeader(magic, version);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out CalligraphyHeader header)
    {
        if (data.Length < 4)
        {
            header = default;
            return false;
        }

        try
        {
            header = ReadHeader(data);
            return true;
        }
        catch
        {
            header = default;
            return false;
        }
    }
}
