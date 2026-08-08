// AV1 video header parsing support
// Reference: AV1 Bitstream & Decoding Process Specification v1.0.0

using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 profile codes.
/// </summary>
public enum Av1Profile : byte
{
    /// <summary>Main profile: 8/10-bit YUV 4:2:0.</summary>
    Main = 0,
    
    /// <summary>High profile: 8/10-bit YUV 4:4:4.</summary>
    High = 1,
    
    /// <summary>Professional profile: 8/10/12-bit all subsampling modes.</summary>
    Professional = 2
}

/// <summary>
/// AV1 OBU (Open Bitstream Unit) types.
/// </summary>
public enum Av1ObuType : byte
{
    /// <summary>Reserved OBU type.</summary>
    Reserved = 0,
    
    /// <summary>Sequence header OBU.</summary>
    SequenceHeader = 1,
    
    /// <summary>Temporal delimiter OBU.</summary>
    TemporalDelimiter = 2,
    
    /// <summary>Frame header OBU.</summary>
    FrameHeader = 3,
    
    /// <summary>Tile group OBU.</summary>
    TileGroup = 4,
    
    /// <summary>Metadata OBU.</summary>
    Metadata = 5,
    
    /// <summary>Frame OBU (combined header + tiles).</summary>
    Frame = 6,
    
    /// <summary>Redundant frame header OBU.</summary>
    RedundantFrameHeader = 7,
    
    /// <summary>Tile list OBU.</summary>
    TileList = 8,
    
    /// <summary>Padding OBU.</summary>
    Padding = 15
}

/// <summary>
/// AV1 frame types.
/// </summary>
public enum Av1FrameType : byte
{
    /// <summary>Keyframe (intra-only, random access point).</summary>
    Key = 0,
    
    /// <summary>Inter-frame (uses reference frames).</summary>
    Inter = 1,
    
    /// <summary>Intra-only non-keyframe.</summary>
    IntraOnly = 2,
    
    /// <summary>Switch frame (seamless resolution switching).</summary>
    Switch = 3
}

/// <summary>
/// AV1 color primaries values.
/// </summary>
public enum Av1ColorPrimaries : byte
{
    /// <summary>BT.709 (sRGB/Rec. 709).</summary>
    Bt709 = 1,
    Unspecified = 2,
    Bt470M = 4,
    Bt470Bg = 5,
    /// <summary>SMPTE 170M (NTSC).</summary>
    Smpte170M = 6,
    Smpte240M = 7,
    Film = 8,
    /// <summary>BT.2020 (Rec. 2020).</summary>
    Bt2020 = 9,
    Xyz = 10,
    Smpte431 = 11,
    Smpte432 = 12,
    Ebu3213 = 22
}

/// <summary>
/// AV1 transfer characteristics.
/// </summary>
public enum Av1TransferCharacteristics : byte
{
    Bt709 = 1,
    Unspecified = 2,
    Bt470M = 4,
    Bt470Bg = 5,
    Smpte170M = 6,
    Smpte240M = 7,
    Linear = 8,
    Log100 = 9,
    Log100Sqrt10 = 10,
    Iec61966 = 11,
    Bt1361 = 12,
    /// <summary>sRGB transfer function.</summary>
    Srgb = 13,
    Bt2020Ten = 14,
    Bt2020Twelve = 15,
    /// <summary>PQ (HDR10).</summary>
    Smpte2084 = 16,
    Smpte428 = 17,
    /// <summary>HLG (Hybrid Log-Gamma, HDR).</summary>
    Hlg = 18
}

/// <summary>
/// AV1 matrix coefficients for YUV conversion.
/// </summary>
public enum Av1MatrixCoefficients : byte
{
    Identity = 0,
    Bt709 = 1,
    Unspecified = 2,
    Fcc = 4,
    Bt470Bg = 5,
    Smpte170M = 6,
    Smpte240M = 7,
    SmpteYcgco = 8,
    Bt2020Ncl = 9,
    Bt2020Cl = 10,
    Smpte2085 = 11,
    ChromaDerivedNcl = 12,
    ChromaDerivedCl = 13,
    Ictcp = 14
}

/// <summary>
/// AV1 chroma sample position.
/// </summary>
public enum Av1ChromaSamplePosition : byte
{
    Unknown = 0,
    Vertical = 1,       // Commonly used in progressive content
    Colocated = 2,      // Commonly used in interlaced content
    Reserved = 3
}

/// <summary>
/// AV1 OBU header information.
/// </summary>
public readonly struct Av1ObuHeader
{
    /// <summary>OBU type.</summary>
    public Av1ObuType Type { get; init; }
    
    /// <summary>Whether the extension header is present.</summary>
    public bool HasExtension { get; init; }
    
    /// <summary>Whether the OBU has a size field.</summary>
    public bool HasSize { get; init; }
    
    /// <summary>Temporal ID (0-7) from extension header.</summary>
    public int TemporalId { get; init; }
    
    /// <summary>Spatial ID (0-3) from extension header.</summary>
    public int SpatialId { get; init; }
    
    /// <summary>Size of the OBU payload in bytes (0 if not present).</summary>
    public int PayloadSize { get; init; }
    
    /// <summary>Total header size in bytes (including size field).</summary>
    public int HeaderSize { get; init; }
}

/// <summary>
/// AV1 sequence header information.
/// </summary>
public readonly struct Av1SequenceHeader
{
    /// <summary>Codec profile (Main, High, Professional).</summary>
    public Av1Profile Profile { get; init; }
    
    /// <summary>Whether this is a still picture sequence.</summary>
    public bool StillPicture { get; init; }
    
    /// <summary>Maximum frame width in pixels.</summary>
    public int MaxFrameWidth { get; init; }
    
    /// <summary>Maximum frame height in pixels.</summary>
    public int MaxFrameHeight { get; init; }
    
    /// <summary>Bit depth (8, 10, or 12).</summary>
    public int BitDepth { get; init; }
    
    /// <summary>Whether the sequence is monochrome.</summary>
    public bool Monochrome { get; init; }
    
    /// <summary>Color primaries.</summary>
    public Av1ColorPrimaries ColorPrimaries { get; init; }
    
    /// <summary>Transfer characteristics.</summary>
    public Av1TransferCharacteristics TransferCharacteristics { get; init; }
    
    /// <summary>Matrix coefficients.</summary>
    public Av1MatrixCoefficients MatrixCoefficients { get; init; }
    
    /// <summary>Whether full color range (0-255) is used.</summary>
    public bool FullColorRange { get; init; }
    
    /// <summary>Chroma subsampling X (1 = subsampled, 0 = not).</summary>
    public int SubsamplingX { get; init; }
    
    /// <summary>Chroma subsampling Y (1 = subsampled, 0 = not).</summary>
    public int SubsamplingY { get; init; }
    
    /// <summary>Chroma sample position.</summary>
    public Av1ChromaSamplePosition ChromaSamplePosition { get; init; }
    
    /// <summary>Whether film grain is used.</summary>
    public bool FilmGrainPresent { get; init; }
    
    /// <summary>Number of bytes consumed.</summary>
    public int BytesConsumed { get; init; }
    
    /// <summary>Gets the chroma format string.</summary>
    public string ChromaFormat => (SubsamplingX, SubsamplingY, Monochrome) switch
    {
        (_, _, true) => "Mono",
        (0, 0, _) => "4:4:4",
        (1, 0, _) => "4:2:2",
        (1, 1, _) => "4:2:0",
        _ => "Unknown"
    };
}

/// <summary>
/// AV1 frame header information.
/// </summary>
public readonly struct Av1FrameHeader
{
    /// <summary>Frame type (Key, Inter, IntraOnly, Switch).</summary>
    public Av1FrameType FrameType { get; init; }
    
    /// <summary>Whether this frame should be shown.</summary>
    public bool ShowFrame { get; init; }
    
    /// <summary>Whether to show an existing reference frame.</summary>
    public bool ShowExistingFrame { get; init; }
    
    /// <summary>Reference frame index to show (if ShowExistingFrame).</summary>
    public int FrameToShowMapIndex { get; init; }
    
    /// <summary>Whether this frame can be used as a reference.</summary>
    public bool Showable { get; init; }
    
    /// <summary>Whether error resilient mode is enabled.</summary>
    public bool ErrorResilient { get; init; }
    
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Render width (may differ for display aspect ratio).</summary>
    public int RenderWidth { get; init; }
    
    /// <summary>Render height.</summary>
    public int RenderHeight { get; init; }
    
    /// <summary>Reference frame refresh flags (8 bits, one per slot).</summary>
    public byte RefreshFrameFlags { get; init; }
    
    /// <summary>Number of bytes consumed.</summary>
    public int BytesConsumed { get; init; }
    
    /// <summary>True if this is a keyframe or intra-only frame.</summary>
    public bool IsKeyframe => FrameType == Av1FrameType.Key || FrameType == Av1FrameType.IntraOnly;
}

/// <summary>
/// AV1 header parser for OBU parsing.
/// </summary>
/// <remarks>
/// Parses AV1 OBU headers, sequence headers, and frame headers for demuxer and seeking support.
/// Actual decoding requires entropy decoding, transforms, and prediction.
/// </remarks>
public static class Av1HeaderParser
{
    /// <summary>
    /// Parses an AV1 OBU header.
    /// </summary>
    /// <param name="data">Data starting at the OBU.</param>
    /// <param name="header">Parsed OBU header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseObuHeader(ReadOnlySpan<byte> data, out Av1ObuHeader header)
    {
        header = default;
        
        if (data.Length < 1)
            return false;
        
        byte obuByte = data[0];
        
        // Check forbidden bit (bit 7, must be 0)
        if ((obuByte & 0x80) != 0)
            return false;
        
        // OBU type (bits 4-7 after forbidden bit, so bits 3-6 of the byte = bits 1-4 in spec)
        // Actually: bit 0 is forbidden_obu_flag (must be 0)
        //           bits 1-4 are obu_type
        //           bit 5 is obu_extension_flag
        //           bit 6 is obu_has_size_field
        //           bit 7 is reserved (must be 0)
        // Wait, reading MSB first:
        // Bit 7: forbidden = 0
        // Bits 6-3: obu_type (4 bits)
        // Bit 2: obu_extension_flag
        // Bit 1: obu_has_size_field
        // Bit 0: reserved = 0
        
        var obuType = (Av1ObuType)((obuByte >> 3) & 0x0F);
        bool hasExtension = ((obuByte >> 2) & 1) == 1;
        bool hasSize = ((obuByte >> 1) & 1) == 1;
        
        // Check reserved bit (bit 0)
        if ((obuByte & 1) != 0)
            return false;
        
        int headerSize = 1;
        int temporalId = 0;
        int spatialId = 0;
        
        // Extension header
        if (hasExtension)
        {
            if (data.Length < 2)
                return false;
            
            byte extByte = data[1];
            temporalId = (extByte >> 5) & 0x07;
            spatialId = (extByte >> 3) & 0x03;
            headerSize = 2;
        }
        
        // Size field (LEB128)
        int payloadSize = 0;
        if (hasSize)
        {
            if (!TryReadLeb128(data[headerSize..], out payloadSize, out int sizeBytes))
                return false;
            headerSize += sizeBytes;
        }
        
        header = new Av1ObuHeader
        {
            Type = obuType,
            HasExtension = hasExtension,
            HasSize = hasSize,
            TemporalId = temporalId,
            SpatialId = spatialId,
            PayloadSize = payloadSize,
            HeaderSize = headerSize
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses an AV1 sequence header OBU.
    /// </summary>
    /// <param name="data">OBU payload data (after OBU header).</param>
    /// <param name="seqHeader">Parsed sequence header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseSequenceHeader(ReadOnlySpan<byte> data, out Av1SequenceHeader seqHeader)
    {
        seqHeader = default;
        
        if (data.Length < 3)
            return false;
        
        var reader = new Av1BitReader(data);
        
        // seq_profile (3 bits)
        var profile = (Av1Profile)reader.ReadBits(3);
        
        // still_picture (1 bit)
        bool stillPicture = reader.ReadBit();
        
        // reduced_still_picture_header (1 bit)
        bool reducedHeader = reader.ReadBit();
        
        int bitDepth = 8;
        bool monochrome = false;
        var colorPrimaries = Av1ColorPrimaries.Unspecified;
        var transferCharacteristics = Av1TransferCharacteristics.Unspecified;
        var matrixCoefficients = Av1MatrixCoefficients.Unspecified;
        bool fullColorRange = false;
        int subsamplingX = 1;
        int subsamplingY = 1;
        var chromaSamplePosition = Av1ChromaSamplePosition.Unknown;
        bool filmGrainPresent = false;
        
        if (reducedHeader)
        {
            // Minimal header for still pictures
            // timing_info not present
            // decoder_model_info not present
            // operating_points_cnt_minus_1 = 0
            // operating_point_idc[0] = 0
            // seq_level_idx[0] (5 bits)
            _ = reader.ReadBits(5);
        }
        else
        {
            // timing_info_present_flag (1 bit)
            bool timingInfoPresent = reader.ReadBit();
            
            if (timingInfoPresent)
            {
                // Skip timing_info()
                _ = reader.ReadBits(32); // num_units_in_display_tick
                _ = reader.ReadBits(32); // time_scale
                bool equalPictureInterval = reader.ReadBit();
                if (equalPictureInterval)
                {
                    // Skip uvlc(num_ticks_per_picture_minus_1)
                    SkipUvlc(ref reader);
                }
                
                // decoder_model_info_present_flag (1 bit)
                bool decoderModelInfoPresent = reader.ReadBit();
                if (decoderModelInfoPresent)
                {
                    // Skip decoder_model_info()
                    _ = reader.ReadBits(5);  // buffer_delay_length_minus_1
                    _ = reader.ReadBits(32); // num_units_in_decoding_tick
                    _ = reader.ReadBits(5);  // buffer_removal_time_length_minus_1
                    _ = reader.ReadBits(5);  // frame_presentation_time_length_minus_1
                }
            }
            
            // initial_display_delay_present_flag (1 bit)
            bool initialDisplayDelayPresent = reader.ReadBit();
            
            // operating_points_cnt_minus_1 (5 bits)
            int operatingPointsCnt = reader.ReadBits(5) + 1;
            
            for (int i = 0; i < operatingPointsCnt; i++)
            {
                _ = reader.ReadBits(12); // operating_point_idc
                _ = reader.ReadBits(5);  // seq_level_idx
                
                if (reader.PeekBits(5) > 7)
                {
                    _ = reader.ReadBits(5);
                    _ = reader.ReadBit(); // seq_tier
                }
                else
                {
                    _ = reader.ReadBits(5);
                }
                
                // Skip decoder model and display delay if present
                if (initialDisplayDelayPresent)
                {
                    bool displayDelayPresent = reader.ReadBit();
                    if (displayDelayPresent)
                    {
                        _ = reader.ReadBits(4); // initial_display_delay_minus_1
                    }
                }
            }
        }
        
        // frame_width_bits_minus_1 (4 bits)
        int frameWidthBits = reader.ReadBits(4) + 1;
        
        // frame_height_bits_minus_1 (4 bits)
        int frameHeightBits = reader.ReadBits(4) + 1;
        
        // max_frame_width_minus_1 (n bits)
        int maxFrameWidth = reader.ReadBits(frameWidthBits) + 1;
        
        // max_frame_height_minus_1 (n bits)
        int maxFrameHeight = reader.ReadBits(frameHeightBits) + 1;
        
        // frame_id_numbers_present_flag (unless reduced header)
        if (!reducedHeader)
        {
            bool frameIdNumbersPresent = reader.ReadBit();
            if (frameIdNumbersPresent)
            {
                _ = reader.ReadBits(4); // delta_frame_id_length_minus_2
                _ = reader.ReadBits(3); // additional_frame_id_length_minus_1
            }
        }
        
        // use_128x128_superblock (1 bit)
        _ = reader.ReadBit();
        
        // enable_filter_intra (1 bit)
        _ = reader.ReadBit();
        
        // enable_intra_edge_filter (1 bit)
        _ = reader.ReadBit();
        
        if (!reducedHeader)
        {
            // enable_interintra_compound (1 bit)
            _ = reader.ReadBit();
            
            // enable_masked_compound (1 bit)
            _ = reader.ReadBit();
            
            // enable_warped_motion (1 bit)
            _ = reader.ReadBit();
            
            // enable_dual_filter (1 bit)
            _ = reader.ReadBit();
            
            // enable_order_hint (1 bit)
            bool enableOrderHint = reader.ReadBit();
            
            if (enableOrderHint)
            {
                // enable_jnt_comp (1 bit)
                _ = reader.ReadBit();
                
                // enable_ref_frame_mvs (1 bit)
                _ = reader.ReadBit();
            }
            
            // seq_choose_screen_content_tools (1 bit)
            bool seqChooseScreenContentTools = reader.ReadBit();
            
            int seqForceScreenContentTools = 2; // SELECT_SCREEN_CONTENT_TOOLS
            if (!seqChooseScreenContentTools)
            {
                seqForceScreenContentTools = reader.ReadBits(1);
            }
            
            if (seqForceScreenContentTools > 0)
            {
                bool seqChooseIntegerMv = reader.ReadBit();
                if (!seqChooseIntegerMv)
                {
                    _ = reader.ReadBit(); // seq_force_integer_mv
                }
            }
            
            if (enableOrderHint)
            {
                _ = reader.ReadBits(3); // order_hint_bits_minus_1
            }
        }
        
        // enable_superres (1 bit)
        _ = reader.ReadBit();
        
        // enable_cdef (1 bit)
        _ = reader.ReadBit();
        
        // enable_restoration (1 bit)
        _ = reader.ReadBit();
        
        // color_config()
        bool highBitDepth = reader.ReadBit();
        
        if (profile == Av1Profile.Professional && highBitDepth)
        {
            bool twelveBit = reader.ReadBit();
            bitDepth = twelveBit ? 12 : 10;
        }
        else if (profile <= Av1Profile.Professional)
        {
            bitDepth = highBitDepth ? 10 : 8;
        }
        
        if (profile == Av1Profile.High)
        {
            monochrome = false;
        }
        else
        {
            monochrome = reader.ReadBit();
        }
        
        bool colorDescriptionPresent = reader.ReadBit();
        
        if (colorDescriptionPresent)
        {
            colorPrimaries = (Av1ColorPrimaries)reader.ReadBits(8);
            transferCharacteristics = (Av1TransferCharacteristics)reader.ReadBits(8);
            matrixCoefficients = (Av1MatrixCoefficients)reader.ReadBits(8);
        }
        
        if (monochrome)
        {
            fullColorRange = reader.ReadBit();
            subsamplingX = 1;
            subsamplingY = 1;
        }
        else if (colorPrimaries == Av1ColorPrimaries.Bt709 &&
                 transferCharacteristics == Av1TransferCharacteristics.Srgb &&
                 matrixCoefficients == Av1MatrixCoefficients.Identity)
        {
            // sRGB
            fullColorRange = true;
            subsamplingX = 0;
            subsamplingY = 0;
        }
        else
        {
            fullColorRange = reader.ReadBit();
            
            if (profile == Av1Profile.Main)
            {
                subsamplingX = 1;
                subsamplingY = 1;
            }
            else if (profile == Av1Profile.High)
            {
                subsamplingX = 0;
                subsamplingY = 0;
            }
            else
            {
                if (bitDepth == 12)
                {
                    subsamplingX = reader.ReadBits(1);
                    if (subsamplingX == 1)
                    {
                        subsamplingY = reader.ReadBits(1);
                    }
                    else
                    {
                        subsamplingY = 0;
                    }
                }
                else
                {
                    subsamplingX = 1;
                    subsamplingY = 0;
                }
            }
            
            if (subsamplingX == 1 && subsamplingY == 1)
            {
                chromaSamplePosition = (Av1ChromaSamplePosition)reader.ReadBits(2);
            }
        }
        
        // separate_uv_delta_q (1 bit, if not monochrome)
        if (!monochrome)
        {
            _ = reader.ReadBit();
        }
        
        // film_grain_params_present (1 bit)
        filmGrainPresent = reader.ReadBit();
        
        seqHeader = new Av1SequenceHeader
        {
            Profile = profile,
            StillPicture = stillPicture,
            MaxFrameWidth = maxFrameWidth,
            MaxFrameHeight = maxFrameHeight,
            BitDepth = bitDepth,
            Monochrome = monochrome,
            ColorPrimaries = colorPrimaries,
            TransferCharacteristics = transferCharacteristics,
            MatrixCoefficients = matrixCoefficients,
            FullColorRange = fullColorRange,
            SubsamplingX = subsamplingX,
            SubsamplingY = subsamplingY,
            ChromaSamplePosition = chromaSamplePosition,
            FilmGrainPresent = filmGrainPresent,
            BytesConsumed = reader.BytePosition + (reader.BitPosition > 0 ? 1 : 0)
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses a simple AV1 frame header (show_existing_frame and frame_type only).
    /// </summary>
    /// <param name="data">OBU payload data (after OBU header).</param>
    /// <param name="seqHeader">Sequence header for context.</param>
    /// <param name="frameHeader">Parsed frame header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseFrameHeader(
        ReadOnlySpan<byte> data,
        in Av1SequenceHeader seqHeader,
        out Av1FrameHeader frameHeader)
    {
        frameHeader = default;
        
        if (data.Length < 1)
            return false;
        
        var reader = new Av1BitReader(data);
        
        bool showExistingFrame = false;
        int frameToShowMapIndex = 0;
        Av1FrameType frameType;
        bool showFrame = true;
        bool showable = false;
        bool errorResilient = false;
        byte refreshFrameFlags = 0;
        
        // show_existing_frame (1 bit if not reduced still picture)
        showExistingFrame = reader.ReadBit();
        
        if (showExistingFrame)
        {
            frameToShowMapIndex = reader.ReadBits(3);
            
            frameHeader = new Av1FrameHeader
            {
                FrameType = Av1FrameType.Inter, // Existing frame reference
                ShowFrame = true,
                ShowExistingFrame = true,
                FrameToShowMapIndex = frameToShowMapIndex,
                Width = seqHeader.MaxFrameWidth,
                Height = seqHeader.MaxFrameHeight,
                RenderWidth = seqHeader.MaxFrameWidth,
                RenderHeight = seqHeader.MaxFrameHeight,
                BytesConsumed = reader.BytePosition + (reader.BitPosition > 0 ? 1 : 0)
            };
            return true;
        }
        
        // frame_type (2 bits)
        frameType = (Av1FrameType)reader.ReadBits(2);
        
        // show_frame (1 bit)
        showFrame = reader.ReadBit();
        
        if (!showFrame)
        {
            // showable_frame (1 bit)
            showable = reader.ReadBit();
        }
        
        // error_resilient_mode (1 bit) - depends on frame type
        if (frameType == Av1FrameType.Switch)
        {
            errorResilient = true;
        }
        else if (frameType != Av1FrameType.Key && !showFrame)
        {
            errorResilient = reader.ReadBit();
        }
        
        // refresh_frame_flags
        if (frameType == Av1FrameType.Key || frameType == Av1FrameType.IntraOnly)
        {
            refreshFrameFlags = 0xFF; // Refresh all slots
        }
        
        // Simplified: use sequence header dimensions
        int width = seqHeader.MaxFrameWidth;
        int height = seqHeader.MaxFrameHeight;
        
        frameHeader = new Av1FrameHeader
        {
            FrameType = frameType,
            ShowFrame = showFrame,
            ShowExistingFrame = false,
            FrameToShowMapIndex = 0,
            Showable = showable,
            ErrorResilient = errorResilient,
            Width = width,
            Height = height,
            RenderWidth = width,
            RenderHeight = height,
            RefreshFrameFlags = refreshFrameFlags,
            BytesConsumed = reader.BytePosition + (reader.BitPosition > 0 ? 1 : 0)
        };
        
        return true;
    }
    
    /// <summary>
    /// Finds the next OBU of the specified type in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="obuType">OBU type to find.</param>
    /// <param name="offset">Offset to the OBU on success.</param>
    /// <returns>True if the OBU type was found.</returns>
    public static bool FindObu(ReadOnlySpan<byte> data, Av1ObuType obuType, out int offset)
    {
        offset = 0;
        
        while (offset < data.Length)
        {
            if (!TryParseObuHeader(data[offset..], out var header))
                return false;
            
            if (header.Type == obuType)
                return true;
            
            // Move to next OBU
            if (header.HasSize)
            {
                offset += header.HeaderSize + header.PayloadSize;
            }
            else
            {
                // Without size field, we can't continue
                return false;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets a human-readable description of the sequence header.
    /// </summary>
    public static string GetDescription(in Av1SequenceHeader header)
    {
        string hdr = header.TransferCharacteristics switch
        {
            Av1TransferCharacteristics.Smpte2084 => " HDR10",
            Av1TransferCharacteristics.Hlg => " HLG",
            _ => ""
        };
        
        return $"AV1 {header.Profile} {header.MaxFrameWidth}x{header.MaxFrameHeight} " +
               $"{header.BitDepth}bit {header.ChromaFormat}{hdr}";
    }
    
    #region Private Helpers
    
    /// <summary>
    /// Reads a LEB128 (variable-length) unsigned integer.
    /// </summary>
    private static bool TryReadLeb128(ReadOnlySpan<byte> data, out int value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        
        for (int i = 0; i < Math.Min(8, data.Length); i++)
        {
            byte b = data[i];
            value |= (b & 0x7F) << (i * 7);
            bytesRead++;
            
            if ((b & 0x80) == 0)
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Skips a UVLC (unsigned variable-length code) value.
    /// </summary>
    private static void SkipUvlc(ref Av1BitReader reader)
    {
        int leadingZeros = 0;
        while (reader.ReadBit() == false)
        {
            leadingZeros++;
            if (leadingZeros > 32)
                break;
        }
        
        if (leadingZeros > 0)
        {
            reader.SkipBits(leadingZeros);
        }
    }
    
    #endregion
}

/// <summary>
/// Simple bit reader for AV1 parsing.
/// </summary>
internal ref struct Av1BitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;
    
    public int BytePosition => bytePosition;
    public int BitPosition => bitPosition;
    
    public Av1BitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit()
    {
        if (bytePosition >= data.Length)
            return false;
        
        bool bit = ((data[bytePosition] >> (7 - bitPosition)) & 1) == 1;
        
        bitPosition++;
        if (bitPosition >= 8)
        {
            bitPosition = 0;
            bytePosition++;
        }
        
        return bit;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        int result = 0;
        
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (ReadBit() ? 1 : 0);
        }
        
        return result;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekBits(int count)
    {
        int savedByte = bytePosition;
        int savedBit = bitPosition;
        
        int result = ReadBits(count);
        
        bytePosition = savedByte;
        bitPosition = savedBit;
        
        return result;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        int totalBits = bytePosition * 8 + bitPosition + count;
        bytePosition = totalBits / 8;
        bitPosition = totalBits % 8;
    }
}
