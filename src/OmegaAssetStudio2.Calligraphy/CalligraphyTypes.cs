namespace OmegaAssetStudio.Calligraphy;

// Calligraphy file magic codes (ASCII), confirmed via raw byte inspection of decompressed data.
// All Calligraphy files start with a 3-byte magic + 1-byte version (currently 11 = 0x0B).
public static class CalligraphyMagic
{
    // "PTP"
    public const uint Prototype = 0x0050_5450; // bytes P,T,P -> 0x50,0x54,0x50 -> uint32 LE = 0x00505450
    // "BPT"
    public const uint Blueprint = 0x0054_5042;
    // "CRV"
    public const uint Curve = 0x0056_5243;
    // "TYP"
    public const uint Type = 0x0050_5954;
    // "DIR"
    public const uint Directory = 0x0052_4944;

    public const byte CurrentVersion = 11;
}

// Single-character ASCII base type codes that appear inside prototype field data,
// per the public format documentation.
public enum CalligraphyBaseType : byte
{
    Asset = (byte)'A',     // 0x41
    Boolean = (byte)'B',   // 0x42
    Curve = (byte)'C',     // 0x43
    Double = (byte)'D',    // 0x44
    Long = (byte)'L',      // 0x4C
    Prototype = (byte)'P', // 0x50
    RHStruct = (byte)'R',  // 0x52 - "fully-featured prototypes without an id", nestable
    String = (byte)'S',    // 0x53
    Type = (byte)'T'       // 0x54
}

public readonly record struct CalligraphyHeader(string Magic, byte Version)
{
    public bool IsPrototype => Magic == "PTP";
    public bool IsBlueprint => Magic == "BPT";
    public bool IsCurve => Magic == "CRV";
    public bool IsType => Magic == "TYP";
    public bool IsDirectory => Magic == "DIR";
}
