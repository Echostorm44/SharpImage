namespace SharpImage.Core;

/// <summary>
/// General-purpose types used throughout SharpImage.
/// These replace the Magick-prefixed types from the original C codebase.
/// </summary>
public static class ImageConstants
{
    /// <summary>Maximum number of pixel channels per image.</summary>
    public const int MaxPixelChannels = 64;

    /// <summary>Default fuzz tolerance for color comparison (0 = exact match).</summary>
    public const double DefaultFuzz = 0.0;

    /// <summary>Default image quality for lossy format encoding (1-100).</summary>
    public const int DefaultQuality = 75;

    /// <summary>Default DPI for images without resolution metadata.</summary>
    public const double DefaultResolution = 72.0;
}

/// <summary>
/// Quantum format for pixel import/export operations.
/// </summary>
public enum QuantumFormat
{
    FloatingPoint,
    Signed,
    Unsigned
}

/// <summary>
/// Quantum alpha premultiplication type.
/// </summary>
public enum QuantumAlphaType
{
    Undefined,
    Associated,       // Premultiplied alpha
    Disassociated     // Straight (unassociated) alpha
}

/// <summary>
/// Specifies how pixel data channels are laid out for import/export.
/// </summary>
public enum QuantumType
{
    Undefined,
    Alpha,
    Black,
    Blue,
    CMYKA,
    CMYK,
    Cyan,
    Gray,
    GrayAlpha,
    Green,
    Index,
    IndexAlpha,
    Magenta,
    Red,
    RGB,
    RGBA,
    RGBO,
    Yellow,
    BGR,
    BGRA,
    BGRO,
    CbYCr,
    CbYCrA,
    Multispectral,
    RGBPad
}

/// <summary>
/// Storage type for pixel data during import/export.
/// </summary>
public enum StorageType
{
    Undefined,
    Char,
    Double,
    Float,
    Short,
    Int,
    Long
}
