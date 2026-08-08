// HEVC/H.265 NAL types and enumerations
// Ported from VLC's hevc_nal.h with improvements for C# idioms

namespace SharpImage.Formats.Hevc;

/// <summary>
/// HEVC NAL unit types as defined in ITU-T H.265.
/// HEVC uses 64 NAL types (6 bits) compared to H.264's 24 types (5 bits).
/// </summary>
public enum HevcNalUnitType : byte
{
    // VCL NAL units (Video Coding Layer) - types 0-31
    
    /// <summary>Trailing picture, non-reference.</summary>
    TrailingNonReference = 0,
    /// <summary>Trailing picture, reference.</summary>
    TrailingReference = 1,
    
    /// <summary>Temporal sublayer access, non-reference.</summary>
    TemporalSublayerAccessNonReference = 2,
    /// <summary>Temporal sublayer access, reference.</summary>
    TemporalSublayerAccessReference = 3,
    
    /// <summary>Stepwise temporal sublayer access, non-reference.</summary>
    StepwiseTemporalAccessNonReference = 4,
    /// <summary>Stepwise temporal sublayer access, reference.</summary>
    StepwiseTemporalAccessReference = 5,
    
    /// <summary>Random access decodable leading picture, non-reference.</summary>
    RandomAccessDecodableLeadingNonReference = 6,
    /// <summary>Random access decodable leading picture, reference.</summary>
    RandomAccessDecodableLeadingReference = 7,
    
    /// <summary>Random access skipped leading picture, non-reference.</summary>
    RandomAccessSkippedLeadingNonReference = 8,
    /// <summary>Random access skipped leading picture, reference.</summary>
    RandomAccessSkippedLeadingReference = 9,
    
    // Reserved VCL types 10-15
    ReservedVclNonReference10 = 10,
    ReservedVclReference11 = 11,
    ReservedVclNonReference12 = 12,
    ReservedVclReference13 = 13,
    ReservedVclNonReference14 = 14,
    ReservedVclReference15 = 15,
    
    // IRAP (Intra Random Access Point) NAL units - Key frames (16-23)
    
    /// <summary>Broken link access with leading pictures.</summary>
    BrokenLinkAccessWithLeadingPictures = 16,
    /// <summary>Broken link access with RADL.</summary>
    BrokenLinkAccessWithRadl = 17,
    /// <summary>Broken link access, no leading pictures.</summary>
    BrokenLinkAccessNoLeadingPictures = 18,
    
    /// <summary>IDR with RADL pictures (key frame).</summary>
    IdrWithRadl = 19,
    /// <summary>IDR with no leading pictures (key frame).</summary>
    IdrNoLeadingPictures = 20,
    
    /// <summary>Clean random access (key frame).</summary>
    CleanRandomAccess = 21,
    
    /// <summary>Reserved IRAP VCL type 22.</summary>
    ReservedIrapVcl22 = 22,
    /// <summary>Reserved IRAP VCL type 23.</summary>
    ReservedIrapVcl23 = 23,
    
    // Reserved VCL types 24-31
    ReservedVcl24 = 24,
    ReservedVcl31 = 31,
    
    // Non-VCL NAL units (types 32-63)
    
    /// <summary>Video Parameter Set - new in HEVC.</summary>
    VideoParameterSet = 32,
    /// <summary>Sequence Parameter Set.</summary>
    SequenceParameterSet = 33,
    /// <summary>Picture Parameter Set.</summary>
    PictureParameterSet = 34,
    /// <summary>Access Unit Delimiter.</summary>
    AccessUnitDelimiter = 35,
    /// <summary>End of Sequence.</summary>
    EndOfSequence = 36,
    /// <summary>End of Bitstream.</summary>
    EndOfBitstream = 37,
    /// <summary>Filler Data.</summary>
    FillerData = 38,
    /// <summary>Prefix SEI message.</summary>
    PrefixSei = 39,
    /// <summary>Suffix SEI message.</summary>
    SuffixSei = 40,
    
    // Reserved non-VCL types 41-47
    ReservedNonVcl41 = 41,
    ReservedNonVcl44 = 44,
    ReservedNonVcl45 = 45,
    ReservedNonVcl47 = 47,
    
    // Unspecified types 48-63 (for custom use)
    Unspecified48 = 48,
    Unspecified55 = 55,
    Unspecified56 = 56,
    Unspecified63 = 63,
    
    /// <summary>Unknown or invalid NAL type.</summary>
    Unknown = 64
}

/// <summary>
/// HEVC slice types. Note: order differs from H.264 (B=0, P=1, I=2 vs H.264's P=0, B=1, I=2).
/// </summary>
public enum HevcSliceType : byte
{
    /// <summary>Bidirectional prediction (uses two reference lists).</summary>
    BSlice = 0,
    /// <summary>Predicted (uses one reference list).</summary>
    PSlice = 1,
    /// <summary>Intra-coded (no inter prediction).</summary>
    ISlice = 2
}

/// <summary>
/// HEVC profile identification.
/// </summary>
public enum HevcProfile : byte
{
    None = 0,
    Main = 1,
    Main10 = 2,
    MainStillPicture = 3,
    RangeExtensions = 4,
    HighThroughput = 5,
    MultiviewMain = 6,
    ScalableMain = 7,
    ThreeDMain = 8,
    ScreenExtended = 9,
    ScalableRangeExtensions = 10
}

/// <summary>
/// HEVC level (stored as 30 * level_number for fractional levels).
/// </summary>
public enum HevcLevel : byte
{
    Level1 = 30,    // 1.0
    Level2 = 60,    // 2.0
    Level21 = 63,   // 2.1
    Level3 = 90,    // 3.0
    Level31 = 93,   // 3.1
    Level4 = 120,   // 4.0
    Level41 = 123,  // 4.1
    Level5 = 150,   // 5.0
    Level51 = 153,  // 5.1
    Level52 = 156,  // 5.2
    Level6 = 180,   // 6.0
    Level61 = 183,  // 6.1
    Level62 = 186,  // 6.2
    Level85 = 255   // 8.5 (max)
}

/// <summary>
/// HEVC chroma format (same values as H.264).
/// </summary>
public enum HevcChromaFormat : byte
{
    Monochrome = 0,
    Chroma420 = 1,
    Chroma422 = 2,
    Chroma444 = 3
}

/// <summary>
/// Maximum parameter set ID limits in HEVC.
/// </summary>
public static class HevcParameterSetLimits
{
    public const int MaxVpsId = 15;   // 4-bit ID
    public const int MaxSpsId = 15;   // 4-bit ID  
    public const int MaxPpsId = 63;   // 6-bit ID
}

/// <summary>
/// Represents a parsed HEVC NAL unit.
/// HEVC NAL header is 2 bytes (vs H.264's 1 byte).
/// </summary>
public readonly struct HevcNalUnit
{
    /// <summary>NAL unit type (6 bits from byte 0).</summary>
    public HevcNalUnitType Type { get; init; }
    
    /// <summary>Layer ID for multi-layer streams (6 bits, spanning bytes 0-1).</summary>
    public byte LayerId { get; init; }
    
    /// <summary>Temporal ID plus 1 (3 bits from byte 1). Actual temporal ID = this - 1.</summary>
    public byte TemporalIdPlus1 { get; init; }
    
    /// <summary>The raw NAL payload (after 2-byte header, before emulation prevention removal).</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
    
    /// <summary>Temporal ID (0-6). Derived from TemporalIdPlus1 - 1.</summary>
    public int TemporalId => TemporalIdPlus1 > 0 ? TemporalIdPlus1 - 1 : 0;
    
    /// <summary>Returns true if this is a VCL (video coding layer) NAL unit.</summary>
    public bool IsVcl => (byte)Type <= 31;
    
    /// <summary>Returns true if this is a parameter set (VPS, SPS, or PPS).</summary>
    public bool IsParameterSet => Type is HevcNalUnitType.VideoParameterSet 
        or HevcNalUnitType.SequenceParameterSet 
        or HevcNalUnitType.PictureParameterSet;
    
    /// <summary>Returns true if this is an IRAP (Intra Random Access Point) - a key frame.</summary>
    public bool IsIrap => (byte)Type >= 16 && (byte)Type <= 23;
    
    /// <summary>Returns true if this is an IDR frame specifically.</summary>
    public bool IsIdr => Type is HevcNalUnitType.IdrWithRadl or HevcNalUnitType.IdrNoLeadingPictures;
    
    /// <summary>Returns true if this is a BLA (Broken Link Access) frame.</summary>
    public bool IsBla => Type is HevcNalUnitType.BrokenLinkAccessWithLeadingPictures
        or HevcNalUnitType.BrokenLinkAccessWithRadl
        or HevcNalUnitType.BrokenLinkAccessNoLeadingPictures;
    
    /// <summary>Returns true if this is a CRA (Clean Random Access) frame.</summary>
    public bool IsCra => Type is HevcNalUnitType.CleanRandomAccess;
    
    /// <summary>Returns true if this NAL unit is a reference picture.</summary>
    public bool IsReference => ((byte)Type & 1) == 1 && (byte)Type <= 15;
}

/// <summary>
/// Utility methods for HEVC NAL processing.
/// </summary>
public static class HevcNalUtilities
{
    /// <summary>
    /// Extracts the NAL unit type from the first byte of an HEVC NAL header.
    /// In HEVC: nal_unit_type = (byte[0] & 0x7E) >> 1 (6 bits at positions 1-6).
    /// </summary>
    public static HevcNalUnitType GetNalType(byte firstByte)
    {
        byte typeValue = (byte)((firstByte & 0x7E) >> 1);
        return typeValue <= 63 ? (HevcNalUnitType)typeValue : HevcNalUnitType.Unknown;
    }
    
    /// <summary>
    /// Extracts the layer ID from the 2-byte HEVC NAL header.
    /// nuh_layer_id = ((byte[0] & 0x01) << 5) | ((byte[1] & 0xF8) >> 3)
    /// </summary>
    public static byte GetLayerId(byte firstByte, byte secondByte)
    {
        return (byte)(((firstByte & 0x01) << 5) | ((secondByte & 0xF8) >> 3));
    }
    
    /// <summary>
    /// Extracts the temporal ID plus 1 from the second byte of HEVC NAL header.
    /// nuh_temporal_id_plus1 = byte[1] & 0x07 (3 bits)
    /// </summary>
    public static byte GetTemporalIdPlus1(byte secondByte)
    {
        return (byte)(secondByte & 0x07);
    }
    
    /// <summary>
    /// Checks if an HEVCDecoderConfigurationRecord (hvcC box) is present.
    /// </summary>
    public static bool IsHvcC(ReadOnlySpan<byte> data)
    {
        // Minimum hvcC size is 23 bytes, and first byte cannot be 0x00
        // (which would indicate Annex B start code)
        return data.Length >= 23 && data[0] != 0x00;
    }
    
    /// <summary>
    /// Gets the NAL length size from an hvcC record (1, 2, 3, or 4 bytes).
    /// </summary>
    public static int GetNalLengthSize(ReadOnlySpan<byte> hvcC)
    {
        if (hvcC.Length < 22)
            return 4; // Default
        
        return (hvcC[21] & 0x03) + 1;
    }
    
    /// <summary>
    /// Returns true if the NAL type represents an IRAP (key frame).
    /// </summary>
    public static bool IsIrapNalType(HevcNalUnitType type)
    {
        byte t = (byte)type;
        return t >= 16 && t <= 23;
    }
    
    /// <summary>
    /// Returns the user-friendly name for an HEVC profile.
    /// </summary>
    public static string GetProfileName(HevcProfile profile) => profile switch
    {
        HevcProfile.Main => "Main",
        HevcProfile.Main10 => "Main 10",
        HevcProfile.MainStillPicture => "Main Still Picture",
        HevcProfile.RangeExtensions => "Range Extensions",
        HevcProfile.HighThroughput => "High Throughput",
        HevcProfile.MultiviewMain => "Multiview Main",
        HevcProfile.ScalableMain => "Scalable Main",
        HevcProfile.ThreeDMain => "3D Main",
        HevcProfile.ScreenExtended => "Screen Extended",
        HevcProfile.ScalableRangeExtensions => "Scalable Range Extensions",
        _ => "Unknown"
    };
    
    /// <summary>
    /// Converts an HEVC level value to a display string (e.g., Level51 = "5.1").
    /// </summary>
    public static string GetLevelString(HevcLevel level)
    {
        int value = (int)level;
        int major = value / 30;
        int minor = (value % 30) / 3;
        return minor > 0 ? $"{major}.{minor}" : $"{major}.0";
    }
}
