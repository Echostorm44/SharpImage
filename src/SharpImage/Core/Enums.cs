namespace SharpImage.Core;

/// <summary>
/// Specifies which color channels to operate on. Flags-based for combining.
/// </summary>
[Flags]
public enum ChannelType : uint
{
    Undefined = 0x0000,
    Red = 0x0001,
    Gray = 0x0001,
    Cyan = 0x0001,
    L = 0x0001,
    Green = 0x0002,
    Magenta = 0x0002,
    A = 0x0002,
    Blue = 0x0004,
    B = 0x0004,
    Yellow = 0x0004,
    Black = 0x0008,
    Alpha = 0x0010,
    Opacity = 0x0010,
    Index = 0x0020,
    ReadMask = 0x0040,
    WriteMask = 0x0080,
    Meta = 0x0100,
    CompositeMask = 0x0200,
    Composite = 0x001F,
    All = 0x7FFFFFF,
    TrueAlpha = 0x0100,
    RGB = 0x0200,
    GrayChannels = 0x0400,
    Sync = 0x20000,
    Default = All
}

/// <summary>
/// Identifies a specific pixel channel by its index in the channel array.
/// </summary>
public enum PixelChannel
{
    Undefined = 0,
    Red = 0,
    Cyan = 0,
    Gray = 0,
    Y = 0,
    Green = 1,
    Magenta = 1,
    Cb = 1,
    Blue = 2,
    Yellow = 2,
    Cr = 2,
    Black = 3,
    Alpha = 4,
    Index = 5,
    ReadMask = 6,
    WriteMask = 7,
    Meta = 8,
    CompositeMask = 9,
    MaxMetaChannels = 10,
    Intensity = 64,      // MaxPixelChannels
    Composite = 64,
    Sync = 65
}

/// <summary>
/// Pixel channel behavior traits for compositing and blending.
/// </summary>
[Flags]
public enum PixelTrait
{
    Undefined = 0x000000,
    Copy = 0x000001,
    Update = 0x000002,
    Blend = 0x000004
}

/// <summary>
/// Types of pixel masks used during operations.
/// </summary>
[Flags]
public enum PixelMask
{
    Undefined = 0x000000,
    Read = 0x000001,
    Write = 0x000002,
    Composite = 0x000004
}

/// <summary>
/// Image storage class — direct color or palette-indexed.
/// </summary>
public enum StorageClass
{
    Undefined,
    Direct,     // True color, no colormap
    Pseudo      // Palette-indexed with colormap
}

/// <summary>
/// Method for computing pixel intensity (grayscale conversion).
/// </summary>
public enum PixelIntensityMethod
{
    Undefined,
    Average,
    Brightness,
    Lightness,
    MeanSquared,
    Rec601Luma,
    Rec601Luminance,
    Rec709Luma,
    Rec709Luminance,
    RootMeanSquared
}

/// <summary>
/// Interpolation method for sub-pixel sampling.
/// </summary>
public enum PixelInterpolateMethod
{
    Undefined,
    Average,
    Average9,
    Average16,
    Background,
    Bilinear,
    Blend,
    CatmullRom,
    Integer,
    Mesh,
    Nearest,
    Spline
}

/// <summary>
/// Supported color spaces for image data.
/// </summary>
public enum ColorspaceType
{
    Undefined,
    CMY,
    CMYK,
    Gray,
    HCL,
    HCLp,
    HSB,
    HSI,
    HSL,
    HSV,
    HWB,
    Lab,
    LCH,
    LCHab,
    LCHuv,
    Log,
    LMS,
    Luv,
    OHTA,
    Rec601YCbCr,
    Rec709YCbCr,
    RGB,
    ScRGB,
    SRGB,
    Transparent,
    XyY,
    XYZ,
    YCbCr,
    YCC,
    YDbDr,
    YIQ,
    YPbPr,
    YUV,
    LinearGray,
    Jzazbz,
    DisplayP3,
    Adobe98,
    ProPhoto,
    Oklab,
    Oklch,
    CAT02LMS
}

/// <summary>
/// Image compression method.
/// </summary>
public enum CompressionType
{
    Undefined,
    B44A,
    B44,
    BZip,
    DXT1,
    DXT3,
    DXT5,
    Fax,
    Group4,
    JBIG1,
    JBIG2,
    JPEG2000,
    JPEG,
    LosslessJPEG,
    LZMA,
    LZW,
    None,
    Piz,
    Pxr24,
    RLE,
    Zip,
    ZipS,
    Zstd,
    WebP,
    DWAA,
    DWAB,
    BC7,
    BC5,
    LERC
}

/// <summary>
/// Image interlace scheme.
/// </summary>
public enum InterlaceType
{
    Undefined,
    None,
    Line,
    Plane,
    Partition,
    GIF,
    JPEG,
    PNG
}

/// <summary>
/// Byte order for multi-byte pixel values.
/// </summary>
public enum EndianType
{
    Undefined,
    LSB,    // Little-endian
    MSB     // Big-endian
}

/// <summary>
/// Image gravity for positioning operations (compose, crop, etc.).
/// </summary>
public enum GravityType
{
    Undefined,
    Forget = 0,
    NorthWest = 1,
    North = 2,
    NorthEast = 3,
    West = 4,
    Center = 5,
    East = 6,
    SouthWest = 7,
    South = 8,
    SouthEast = 9
}

/// <summary>
/// EXIF orientation tag values.
/// </summary>
public enum OrientationType
{
    Undefined,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
    LeftTop,
    RightTop,
    RightBottom,
    LeftBottom
}

/// <summary>
/// Resolution unit for DPI metadata.
/// </summary>
public enum ResolutionType
{
    Undefined,
    PixelsPerInch,
    PixelsPerCentimeter
}

/// <summary>
/// Image type classification.
/// </summary>
public enum ImageType
{
    Undefined,
    Bilevel,
    Grayscale,
    GrayscaleAlpha,
    Palette,
    PaletteAlpha,
    TrueColor,
    TrueColorAlpha,
    ColorSeparation,
    ColorSeparationAlpha,
    Optimize,
    PaletteBilevelAlpha
}

/// <summary>
/// Filter types used for image resizing.
/// </summary>
public enum FilterType
{
    Undefined,
    Point,
    Box,
    Triangle,
    Hermite,
    Hann,
    Hamming,
    Blackman,
    Gaussian,
    Quadratic,
    Cubic,
    Catrom,
    Mitchell,
    Jinc,
    Sinc,
    SincFast,
    Kaiser,
    Welch,
    Parzen,
    Bohman,
    Bartlett,
    Lagrange,
    Lanczos,
    LanczosSharp,
    Lanczos2,
    Lanczos2Sharp,
    Robidoux,
    RobidouxSharp,
    Cosine,
    Spline,
    LanczosRadius,
    CubicSpline
}

/// <summary>
/// Composite (blend) operators for layer composition.
/// </summary>
public enum CompositeOperator
{
    Undefined,
    Alpha,
    Atop,
    Blend,
    Blur,
    Bumpmap,
    ChangeMask,
    Clear,
    ColorBurn,
    ColorDodge,
    Colorize,
    CopyBlack,
    CopyBlue,
    Copy,
    CopyCyan,
    CopyGreen,
    CopyMagenta,
    CopyAlpha,
    CopyRed,
    CopyYellow,
    Darken,
    DarkenIntensity,
    Difference,
    Displace,
    Dissolve,
    Distort,
    DivideDst,
    DivideSrc,
    DstAtop,
    Dst,
    DstIn,
    DstOut,
    DstOver,
    Exclusion,
    HardLight,
    HardMix,
    Hue,
    In,
    Intensity,
    Lighten,
    LightenIntensity,
    LinearBurn,
    LinearDodge,
    LinearLight,
    Luminize,
    Mathematics,
    MinusDst,
    MinusSrc,
    Modulate,
    ModulusAdd,
    ModulusSubtract,
    Multiply,
    No,
    Out,
    Over,
    Overlay,
    PegtopLight,
    PinLight,
    Plus,
    Replace,
    RMSE,
    Saturate,
    Screen,
    SoftBurn,
    SoftDodge,
    SoftLight,
    SrcAtop,
    Src,
    SrcIn,
    SrcOut,
    SrcOver,
    Stamp,
    Stereo,
    VividLight,
    Xor
}

/// <summary>
/// Disposal method for animation frames.
/// </summary>
public enum DisposeType
{
    Undefined,
    None,
    Background,
    Previous
}

/// <summary>
/// Maximum number of pixel channels supported.
/// </summary>
public static class PixelChannelConstants
{
    public const int MaxPixelChannels = 64;
}

/// <summary>
/// How to handle pixels outside image boundaries during filter/transform operations.
/// </summary>
public enum VirtualPixelMethod
{
    /// <summary>Pixels outside the image are transparent black (0,0,0,0).</summary>
    Transparent,
    /// <summary>Pixels outside the image are the background color (default: black).</summary>
    Background,
    /// <summary>Pixels outside the image are white.</summary>
    White,
    /// <summary>Pixels outside the image are black.</summary>
    Black,
    /// <summary>Pixels outside the image mirror the edge pixels.</summary>
    Mirror,
    /// <summary>Pixels outside the image tile (wrap) the image.</summary>
    Tile,
    /// <summary>Pixels outside the image use the nearest edge pixel.</summary>
    Edge,
    /// <summary>Random pixel from the image.</summary>
    Random,
    /// <summary>A solid gray (50%) for out-of-bounds pixels.</summary>
    Gray,
    /// <summary>Pixels outside the image use a checkerboard pattern.</summary>
    CheckerTile,
    /// <summary>Horizontal mirror tiling.</summary>
    HorizontalTile,
    /// <summary>Vertical mirror tiling.</summary>
    VerticalTile
}
