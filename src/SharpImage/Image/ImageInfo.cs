using global::SharpImage.Core;

namespace SharpImage.Image;

/// <summary>
/// Settings that control how images are read, written, and created. Equivalent to the original C ImageInfo struct.
/// </summary>
public class ImageSettings
{
    /// <summary>
    /// Compression algorithm to use when writing.
    /// </summary>
    public CompressionType Compression { get; set; } = CompressionType.Undefined;

    /// <summary>
    /// Encoding quality (1-100). Higher = better quality, larger file.
    /// </summary>
    public int Quality { get; set; } = ImageConstants.DefaultQuality;

    /// <summary>
    /// Target bit depth per channel when writing.
    /// </summary>
    public int Depth { get; set; } = Quantum.Depth;

    /// <summary>
    /// Interlace scheme for progressive encoding.
    /// </summary>
    public InterlaceType Interlace { get; set; } = InterlaceType.None;

    /// <summary>
    /// Byte order for multi-byte values.
    /// </summary>
    public EndianType Endian { get; set; } = EndianType.Undefined;

    /// <summary>
    /// Resolution unit (DPI or DPCM).
    /// </summary>
    public ResolutionType ResolutionUnits { get; set; } = ResolutionType.Undefined;

    /// <summary>
    /// Target colorspace for the output image.
    /// </summary>
    public ColorspaceType Colorspace { get; set; } = ColorspaceType.Undefined;

    /// <summary>
    /// Composite operator for layering.
    /// </summary>
    public CompositeOperator Compose { get; set; } = CompositeOperator.Over;

    /// <summary>
    /// Image type hint for the encoder.
    /// </summary>
    public ImageType Type { get; set; } = ImageType.Undefined;

    /// <summary>
    /// Background color for operations that extend the canvas.
    /// </summary>
    public PixelInfo BackgroundColor
    {
        get;
        set;
    } = PixelInfo.FromRgba(
        Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue, Quantum.Opaque);

    /// <summary>
    /// Color comparison tolerance.
    /// </summary>
    public double Fuzz { get; set; } = ImageConstants.DefaultFuzz;

    /// <summary>
    /// Font point size for text operations.
    /// </summary>
    public double PointSize { get; set; } = 12.0;

    /// <summary>
    /// If true, only read image metadata (no pixel data).
    /// </summary>
    public bool Ping { get; set; }

    /// <summary>
    /// If true, output verbose information during processing.
    /// </summary>
    public bool Verbose { get; set; }
}
