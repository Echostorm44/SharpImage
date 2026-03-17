// Camera raw format types — enums, structs, and options for camera raw decoding.
// Shared across all camera raw format parsers (CR2, NEF, ARW, DNG, ORF, RW2, RAF, PEF, SRW, etc.).
// Reference: libraw data structures, Adobe DNG specification 1.7, TIFF/EP ISO 12234-2

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats;

/// <summary>
/// Bayer color filter array pattern describing the 2×2 mosaic arrangement on the sensor.
/// Named by the top-left 2×2 corner reading left-to-right, top-to-bottom.
/// </summary>
public enum BayerPattern
{
    /// <summary>Unknown or unspecified pattern.</summary>
    Unknown,
    /// <summary>Red-Green / Green-Blue (most Canon, some Sony).</summary>
    RGGB,
    /// <summary>Blue-Green / Green-Red (some older sensors).</summary>
    BGGR,
    /// <summary>Green-Red / Blue-Green (some Nikon, Sony).</summary>
    GRBG,
    /// <summary>Green-Blue / Red-Green (some sensors).</summary>
    GBRG
}

/// <summary>
/// Fuji X-Trans 6×6 color filter array pattern. Not a standard Bayer pattern —
/// uses a repeating 6×6 arrangement with more green samples for improved detail.
/// </summary>
public enum XTransPattern
{
    /// <summary>Standard Fuji X-Trans 6×6 pattern used in X-T, X-Pro, X-H series.</summary>
    Standard,
    /// <summary>Unknown or custom X-Trans variant.</summary>
    Unknown
}

/// <summary>
/// Color filter array type — distinguishes standard 2×2 Bayer from non-Bayer sensors.
/// </summary>
public enum CfaType
{
    /// <summary>Standard 2×2 Bayer mosaic (CR2, NEF, ARW, DNG, ORF, RW2, PEF, SRW, etc.).</summary>
    Bayer,
    /// <summary>Fuji X-Trans 6×6 mosaic (RAF files from X-series cameras).</summary>
    XTrans,
    /// <summary>Sigma Foveon three-layer sensor (X3F files) — no demosaicing needed.</summary>
    Foveon,
    /// <summary>Linear/monochrome sensor — no CFA, raw values are luminance.</summary>
    Linear
}

/// <summary>
/// Demosaicing algorithm for converting raw Bayer/X-Trans CFA data to full RGB.
/// Ordered from fastest (lowest quality) to slowest (highest quality).
/// </summary>
public enum DemosaicAlgorithm
{
    /// <summary>Bilinear interpolation — fast, simple averaging of neighbors. Good for previews.</summary>
    Bilinear,
    /// <summary>Variable Number of Gradients — edge-aware, balances speed and quality.</summary>
    VNG,
    /// <summary>Adaptive Homogeneity-Directed — highest quality, best color accuracy. Default.</summary>
    AHD
}

/// <summary>
/// Identifies which camera raw format a file uses. Used for parser routing.
/// </summary>
public enum CameraRawFormat
{
    Unknown,
    // Canon
    CR2,
    CR3,
    CRW,
    // Nikon
    NEF,
    NRW,
    // Sony
    ARW,
    SR2,
    SRF,
    // Adobe
    DNG,
    // Olympus
    ORF,
    // Panasonic
    RW2,
    // Fuji
    RAF,
    // Pentax
    PEF,
    // Samsung
    SRW,
    // Hasselblad
    ThreeFR,
    FFF,
    // Phase One
    IIQ,
    // Sigma
    X3F,
    // Kodak
    DCR,
    K25,
    KDC,
    // Minolta / Sony legacy
    MDC,
    MRW,
    // Mamiya
    MEF,
    // Leaf
    MOS,
    // Leica
    RWL,
    // Epson
    ERF,
    // Sinar
    STI,
    // Raw Media Format
    RMF,
    // Generic dcraw-compatible
    DCRAW
}

/// <summary>
/// Compression method used for the raw sensor data within the camera file.
/// </summary>
public enum RawCompression
{
    /// <summary>Uncompressed sensor data — direct 12/14/16-bit values.</summary>
    Uncompressed,
    /// <summary>ITU-T T.81 lossless JPEG (predictive coding) — used by CR2, DNG, some NEF.</summary>
    LosslessJpeg,
    /// <summary>Nikon proprietary compressed format — lossy or lossless variants.</summary>
    NikonCompressed,
    /// <summary>Sony ARW compressed format.</summary>
    SonyCompressed,
    /// <summary>Panasonic proprietary compression.</summary>
    PanasonicCompressed,
    /// <summary>Olympus proprietary compression.</summary>
    OlympusCompressed,
    /// <summary>Pentax proprietary compression (Huffman-based).</summary>
    PentaxCompressed,
    /// <summary>Samsung proprietary compression.</summary>
    SamsungCompressed,
    /// <summary>Fuji proprietary compression.</summary>
    FujiCompressed,
    /// <summary>Phase One proprietary compression.</summary>
    PhaseOneCompressed,
    /// <summary>Sigma X3F Foveon compression.</summary>
    FoveonCompressed,
    /// <summary>Deflate/zlib compression (some DNG files).</summary>
    Deflate,
    /// <summary>Lossy JPEG DCT compression (some DNG, lossy NEF).</summary>
    LossyJpeg
}

/// <summary>
/// White balance source — where to get white balance multipliers from.
/// </summary>
public enum WhiteBalanceMode
{
    /// <summary>Use the camera's as-shot white balance from metadata.</summary>
    AsShot,
    /// <summary>Auto white balance computed from image statistics.</summary>
    Auto,
    /// <summary>D65 daylight (6500K) — standard reference illuminant.</summary>
    Daylight,
    /// <summary>Use explicitly provided multipliers from CameraRawDecodeOptions.</summary>
    Custom
}

/// <summary>
/// Options controlling how camera raw files are decoded. All fields have sensible defaults.
/// </summary>
public struct CameraRawDecodeOptions
{
    /// <summary>Demosaicing algorithm to use. Default: AHD.</summary>
    public DemosaicAlgorithm Algorithm;

    /// <summary>White balance mode. Default: AsShot.</summary>
    public WhiteBalanceMode WhiteBalance;

    /// <summary>Custom white balance multipliers (R, G, B). Only used when WhiteBalance is Custom.</summary>
    public double CustomWbRed;
    public double CustomWbGreen;
    public double CustomWbBlue;

    /// <summary>Output bit depth (8 or 16). Default: 16.</summary>
    public int OutputBitDepth;

    /// <summary>Apply camera-embedded color matrix. Default: true.</summary>
    public bool ApplyColorMatrix;

    /// <summary>Apply sRGB gamma curve. If false, output is linear. Default: true.</summary>
    public bool ApplyGamma;

    /// <summary>Highlight recovery mode: 0 = clip, 1 = unclip, 2 = blend. Default: 0.</summary>
    public int HighlightRecovery;

    /// <summary>
    /// Creates default decode options: AHD demosaic, as-shot WB, 16-bit output, color matrix and gamma applied.
    /// </summary>
    public static CameraRawDecodeOptions Default => new()
    {
        Algorithm = DemosaicAlgorithm.AHD,
        WhiteBalance = WhiteBalanceMode.AsShot,
        CustomWbRed = 1.0,
        CustomWbGreen = 1.0,
        CustomWbBlue = 1.0,
        OutputBitDepth = 16,
        ApplyColorMatrix = true,
        ApplyGamma = true,
        HighlightRecovery = 0
    };
}

/// <summary>
/// Metadata extracted from a camera raw file during parsing. Contains camera info,
/// sensor characteristics, and color processing parameters needed by the decode pipeline.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraRawMetadata
{
    /// <summary>Camera manufacturer (e.g., "Canon", "Nikon", "Sony").</summary>
    public string Make;
    /// <summary>Camera model (e.g., "EOS 5D Mark IV", "D850", "ILCE-7RM4").</summary>
    public string Model;

    /// <summary>Sensor width in pixels (full sensor, including masked/dark pixels).</summary>
    public int SensorWidth;
    /// <summary>Sensor height in pixels (full sensor).</summary>
    public int SensorHeight;

    /// <summary>Active image area — left offset in pixels.</summary>
    public int ActiveAreaLeft;
    /// <summary>Active image area — top offset in pixels.</summary>
    public int ActiveAreaTop;
    /// <summary>Active image area — width in pixels (the actual image).</summary>
    public int ActiveAreaWidth;
    /// <summary>Active image area — height in pixels (the actual image).</summary>
    public int ActiveAreaHeight;

    /// <summary>CFA type — Bayer, X-Trans, Foveon, or Linear.</summary>
    public CfaType CfaType;
    /// <summary>Bayer filter pattern (only valid when CfaType is Bayer).</summary>
    public BayerPattern BayerPattern;
    /// <summary>X-Trans pattern variant (only valid when CfaType is XTrans).</summary>
    public XTransPattern XTransPattern;

    /// <summary>Bits per sample in the raw sensor data (typically 12, 14, or 16).</summary>
    public int BitsPerSample;
    /// <summary>Compression method used for raw data.</summary>
    public RawCompression Compression;

    /// <summary>Black level (optical black) — subtracted before processing. Per-channel average.</summary>
    public int BlackLevel;
    /// <summary>Per-channel black levels [R, G1, G2, B] when available. Null if uniform.</summary>
    public int[]? BlackLevelPerChannel;
    /// <summary>White level (saturation point). Values above this are clipped.</summary>
    public int WhiteLevel;

    /// <summary>As-shot white balance multipliers [R, G, B] from camera.</summary>
    public double[]? AsShotWhiteBalance;
    /// <summary>Daylight white balance multipliers [R, G, B].</summary>
    public double[]? DaylightWhiteBalance;

    /// <summary>
    /// Camera-to-XYZ D65 color matrix (3×3 stored row-major as 9 doubles).
    /// Maps camera-native RGB to CIE XYZ under D65 illuminant.
    /// </summary>
    public double[]? ColorMatrix1;
    /// <summary>
    /// Second color matrix for illuminant A/TL84 (3×3 row-major).
    /// Used for dual-illuminant interpolation in DNG.
    /// </summary>
    public double[]? ColorMatrix2;
    /// <summary>
    /// Forward matrix (XYZ to camera RGB, 3×3 row-major). Used in DNG for profile connection.
    /// </summary>
    public double[]? ForwardMatrix1;

    /// <summary>Linearization table for non-linear sensor response. Null if linear.</summary>
    public ushort[]? LinearizationTable;

    /// <summary>Camera orientation from EXIF (1-8, where 1 = normal).</summary>
    public int Orientation;
    /// <summary>ISO speed rating.</summary>
    public int IsoSpeed;
    /// <summary>Exposure time in seconds.</summary>
    public double ExposureTime;
    /// <summary>F-number (aperture).</summary>
    public double FNumber;
    /// <summary>Focal length in mm.</summary>
    public double FocalLength;
    /// <summary>Capture timestamp (UTC if available).</summary>
    public DateTime CaptureTime;

    /// <summary>Embedded ICC color profile bytes. Null if not present.</summary>
    public byte[]? IccProfile;
    /// <summary>Embedded XMP metadata bytes. Null if not present.</summary>
    public byte[]? XmpData;

    /// <summary>Raw format type detected.</summary>
    public CameraRawFormat Format;

    /// <summary>Offset in bytes to the start of raw sensor data within the file.</summary>
    public long RawDataOffset;
    /// <summary>Length in bytes of the raw sensor data.</summary>
    public long RawDataLength;

    /// <summary>
    /// Format-specific metadata offset — points to decoder configuration data within the file.
    /// For Nikon: offset to NikonLinearizationTable (MN tag 0x96) for Huffman tree/curve/vpred.
    /// For Pentax: offset to PentaxHuffmanTable (MN tag 0x220) for Huffman decode table.
    /// Zero if not applicable.
    /// </summary>
    public long MetaOffset;

    /// <summary>Whether the file uses big-endian byte order (needed by decoders reading metadata).</summary>
    public bool BigEndian;

    /// <summary>
    /// True when ColorMatrix1 came from dcraw's adobe_coeff lookup (D65-calibrated).
    /// False for DNG-embedded ColorMatrix1 (D50-calibrated per DNG spec).
    /// Controls which XYZ→sRGB conversion matrix is used in the color pipeline.
    /// </summary>
    public bool ColorMatrixIsD65;

    /// <summary>
    /// True when white balance was applied to raw Bayer data before demosaicing
    /// (matching dcraw's scale_colors behavior). When set, the post-demosaic
    /// color matrix is applied WITHOUT absorbing WB multipliers.
    /// </summary>
    public bool WbAppliedAtBayerLevel;

    /// <summary>Offset to embedded JPEG thumbnail. 0 if not present.</summary>
    public long ThumbnailOffset;
    /// <summary>Length of embedded JPEG thumbnail.</summary>
    public long ThumbnailLength;
}

/// <summary>
/// Raw sensor data extracted from a camera file, ready for demosaicing.
/// Contains the unpacked/decompressed CFA values and associated metadata.
/// </summary>
public struct RawSensorData
{
    /// <summary>Unpacked sensor values — one value per sensel (pixel site).
    /// Dimensions: [SensorHeight × SensorWidth] in row-major order.</summary>
    public ushort[] RawPixels;

    /// <summary>Sensor width in pixels.</summary>
    public int Width;
    /// <summary>Sensor height in pixels.</summary>
    public int Height;

    /// <summary>Metadata from the camera raw file.</summary>
    public CameraRawMetadata Metadata;

    /// <summary>
    /// Pre-computed 6×6 X-Trans CFA pattern adjusted for active area offset.
    /// When non-null, DemosaicXTrans uses this instead of the default hardcoded pattern.
    /// Index: [row % 6, col % 6] → 0=R, 1=G, 2=B.
    /// </summary>
    public byte[,]? XTransCfaEffective;

    /// <summary>
    /// Gets the raw value at the specified sensor position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort GetValue(int x, int y) => RawPixels[y * Width + x];

    /// <summary>
    /// Sets the raw value at the specified sensor position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetValue(int x, int y, ushort value) => RawPixels[y * Width + x] = value;

    /// <summary>
    /// Gets the CFA color at the given position for a standard 2×2 Bayer pattern.
    /// Returns 0=Red, 1=Green (on red row), 2=Green (on blue row), 3=Blue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetBayerColor(int x, int y)
    {
        return Metadata.BayerPattern switch
        {
            BayerPattern.RGGB => (y & 1) * 2 + (x & 1),        // 0=R, 1=G, 2=G, 3=B
            BayerPattern.BGGR => (1 - (y & 1)) * 2 + (1 - (x & 1)),
            BayerPattern.GRBG => (y & 1) * 2 + (1 - (x & 1)),
            BayerPattern.GBRG => (1 - (y & 1)) * 2 + (x & 1),
            _ => 0
        };
    }

    /// <summary>
    /// Returns true if the sensel at (x, y) is a red site in the Bayer pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsRed(int x, int y) => GetBayerColor(x, y) == 0;

    /// <summary>
    /// Returns true if the sensel at (x, y) is a green site in the Bayer pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsGreen(int x, int y)
    {
        int c = GetBayerColor(x, y);
        return c == 1 || c == 2;
    }

    /// <summary>
    /// Returns true if the sensel at (x, y) is a blue site in the Bayer pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsBlue(int x, int y) => GetBayerColor(x, y) == 3;
}

/// <summary>
/// The standard 6×6 Fuji X-Trans CFA color layout. Each value: 0=Red, 1=Green, 2=Blue.
/// Used by RAF parser to identify colors at each sensor site.
/// </summary>
public static class XTransCfa
{
    /// <summary>
    /// Standard X-Trans 6×6 color filter array pattern (X-Trans II/III/IV sensors).
    /// Index with [row % 6, col % 6] to get color: 0=R, 1=G, 2=B.
    /// 20 green, 8 red, 8 blue per 6×6 tile — same ratio as Bayer but with
    /// irregular placement that reduces moiré.
    /// </summary>
    public static readonly byte[,] Pattern = new byte[6, 6]
    {
        { 1, 1, 0, 1, 1, 2 },  // G G R G G B
        { 1, 1, 2, 1, 1, 0 },  // G G B G G R
        { 2, 0, 1, 0, 2, 1 },  // B R G R B G
        { 1, 1, 2, 1, 1, 0 },  // G G B G G R
        { 1, 1, 0, 1, 1, 2 },  // G G R G G B
        { 0, 2, 1, 2, 0, 1 },  // R B G B R G
    };

    /// <summary>
    /// Gets the color channel (0=R, 1=G, 2=B) at the given sensor position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetColor(int x, int y) => Pattern[y % 6, x % 6];
}
