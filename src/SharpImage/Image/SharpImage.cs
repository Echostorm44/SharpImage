using global::SharpImage.Core;
using global::SharpImage.Metadata;
using System.Runtime.CompilerServices;

namespace SharpImage.Image;

/// <summary>
/// The core image container. Holds pixel data, metadata, dimensions, colorspace, and channel configuration. This is the
/// central data structure of SharpImage.
/// </summary>
public class ImageFrame : IDisposable
{
    private PixelCache? pixelCache;
    private PixelChannelMap[] channelMap;
    private bool disposed;

    /// <summary>
    /// Image width in pixels.
    /// </summary>
    public long Columns { get; private set; }

    /// <summary>
    /// Image height in pixels.
    /// </summary>
    public long Rows { get; private set; }

    /// <summary>
    /// Bit depth per channel.
    /// </summary>
    public int Depth { get; set; } = Quantum.Depth;

    /// <summary>
    /// Color space of the pixel data.
    /// </summary>
    public ColorspaceType Colorspace { get; set; } = ColorspaceType.SRGB;

    /// <summary>
    /// Direct (true color) or Pseudo (palette-indexed).
    /// </summary>
    public StorageClass StorageClass { get; set; } = StorageClass.Direct;

    /// <summary>
    /// Number of active channels in the pixel data.
    /// </summary>
    public int NumberOfChannels { get; private set; }

    /// <summary>
    /// Whether the image has an alpha channel.
    /// </summary>
    public bool HasAlpha { get; private set; }

    /// <summary>
    /// Compression used by the source file.
    /// </summary>
    public CompressionType Compression { get; set; } = CompressionType.Undefined;

    /// <summary>
    /// Interlace scheme from the source file.
    /// </summary>
    public InterlaceType Interlace { get; set; } = InterlaceType.None;

    /// <summary>
    /// EXIF orientation tag.
    /// </summary>
    public OrientationType Orientation { get; set; } = OrientationType.Undefined;

    /// <summary>
    /// Image type classification.
    /// </summary>
    public ImageType Type { get; set; } = ImageType.Undefined;

    /// <summary>
    /// Resize filter preference.
    /// </summary>
    public FilterType Filter { get; set; } = FilterType.Undefined;

    /// <summary>
    /// Pixel interpolation method.
    /// </summary>
    public PixelInterpolateMethod Interpolate { get; set; } = PixelInterpolateMethod.Undefined;

    /// <summary>
    /// Default composite operator.
    /// </summary>
    public CompositeOperator Compose { get; set; } = CompositeOperator.Over;

    /// <summary>
    /// Positioning gravity.
    /// </summary>
    public GravityType Gravity { get; set; } = GravityType.Undefined;

    /// <summary>
    /// Gamma value of the image.
    /// </summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>
    /// Color comparison tolerance.
    /// </summary>
    public double Fuzz { get; set; }

    /// <summary>
    /// Horizontal resolution (DPI).
    /// </summary>
    public double ResolutionX { get; set; } = ImageConstants.DefaultResolution;

    /// <summary>
    /// Vertical resolution (DPI).
    /// </summary>
    public double ResolutionY { get; set; } = ImageConstants.DefaultResolution;

    /// <summary>
    /// Resolution unit.
    /// </summary>
    public ResolutionType ResolutionUnits { get; set; } = ResolutionType.PixelsPerInch;

    /// <summary>
    /// Background color for canvas operations.
    /// </summary>
    public PixelInfo BackgroundColor { get; set; }

    /// <summary>
    /// Border color for frame/border operations.
    /// </summary>
    public PixelInfo BorderColor { get; set; }

    /// <summary>
    /// Transparent color for transparency operations.
    /// </summary>
    public PixelInfo TransparentColor { get; set; }

    /// <summary>
    /// Palette for indexed-color images.
    /// </summary>
    public PixelInfo[]? Colormap { get; set; }

    /// <summary>
    /// Number of colors in the palette.
    /// </summary>
    public int ColormapSize { get; set; }

    /// <summary>
    /// Animation: frame sequence number.
    /// </summary>
    public int Scene { get; set; }

    /// <summary>
    /// Animation: frame delay in centiseconds.
    /// </summary>
    public int Delay { get; set; }

    /// <summary>
    /// Animation: total duration in centiseconds.
    /// </summary>
    public int Duration { get; set; }

    /// <summary>
    /// Animation: loop count (0 = infinite).
    /// </summary>
    public int Iterations { get; set; }

    /// <summary>
    /// Animation: disposal method between frames.
    /// </summary>
    public DisposeType DisposeMethod { get; set; } = DisposeType.Undefined;

    /// <summary>
    /// Animation: frame offset position within the canvas (for sub-frame GIFs/APNGs).
    /// </summary>
    public FrameOffset Page { get; set; }

    /// <summary>
    /// Source format name (e.g., "PNG", "JPEG").
    /// </summary>
    public string FormatName { get; set; } = string.Empty;

    /// <summary>
    /// Source filename.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Rendering intent for color management.
    /// </summary>
    public PixelIntensityMethod IntensityMethod { get; set; } = PixelIntensityMethod.Undefined;

    /// <summary>
    /// Encoding quality from source file.
    /// </summary>
    public int Quality { get; set; } = ImageConstants.DefaultQuality;

    /// <summary>
    /// Image metadata: EXIF, ICC profile, XMP, IPTC. Lazily created on first access.
    /// </summary>
    public ImageMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Creates an empty image. Use Initialize() to allocate pixel storage.
    /// </summary>
    public ImageFrame()
    {
        channelMap = [];
        BackgroundColor = PixelInfo.FromRgba(
            Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue, Quantum.Opaque);
    }

    /// <summary>
    /// Initializes the image with the specified dimensions and allocates pixel storage.
    /// </summary>
    public void Initialize(long columns, long rows, ColorspaceType colorspace = ColorspaceType.SRGB, bool hasAlpha = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        Columns = columns;
        Rows = rows;
        Colorspace = colorspace;
        HasAlpha = hasAlpha;

        SetupChannelMap();

        pixelCache?.Dispose();
        pixelCache = new PixelCache(columns, rows, NumberOfChannels);
    }

    /// <summary>
    /// Enables or disables the alpha channel and reconfigures the pixel cache.
    /// </summary>
    public void SetAlpha(bool enabled)
    {
        if (HasAlpha == enabled)
        {
            return;
        }

        HasAlpha = enabled;

        if (pixelCache is null)
        {
            return;
        }

        // Rebuild channel map and reallocate cache
        var oldCache = pixelCache;
        int oldChannelCount = NumberOfChannels;
        SetupChannelMap();
        pixelCache = new PixelCache(Columns, Rows, NumberOfChannels);

        // Copy existing pixel data
        int copyChannels = Math.Min(oldChannelCount, NumberOfChannels);
        for (long y = 0;y < Rows;y++)
        {
            var oldRow = oldCache.GetRow(y);
            var newRow = pixelCache.GetRowForWrite(y);
            for (long x = 0;x < Columns;x++)
            {
                int oldOffset = (int)(x * oldChannelCount);
                int newOffset = (int)(x * NumberOfChannels);
                for (int c = 0;c < copyChannels;c++)
                {
                    newRow[newOffset + c] = oldRow[oldOffset + c];
                }

                // Initialize alpha to opaque if we just added it
                if (enabled && NumberOfChannels > oldChannelCount)
                {
                    newRow[newOffset + NumberOfChannels - 1] = Quantum.Opaque;
                }
            }
        }

        oldCache.Dispose();
    }

    /// <summary>
    /// Gets a read-only view of a scanline's pixel data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ushort> GetPixelRow(long y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pixelCache is null)
        {
            throw new InvalidOperationException("Image not initialized");
        }

        return pixelCache.GetRow(y);
    }

    /// <summary>
    /// Gets a writable view of a scanline's pixel data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<ushort> GetPixelRowForWrite(long y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pixelCache is null)
        {
            throw new InvalidOperationException("Image not initialized");
        }

        return pixelCache.GetRowForWrite(y);
    }

    /// <summary>
    /// Gets the pixel value for a specific channel at (x, y).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetPixelChannel(long x, long y, int channelIndex)
    {
        var row = GetPixelRow(y);
        return row[(int)(x * NumberOfChannels + channelIndex)];
    }

    /// <summary>
    /// Sets the pixel value for a specific channel at (x, y).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixelChannel(long x, long y, int channelIndex, ushort value)
    {
        var row = GetPixelRowForWrite(y);
        row[(int)(x * NumberOfChannels + channelIndex)] = value;
    }

    /// <summary>
    /// Reads a full PixelInfo at the specified coordinates.
    /// </summary>
    public PixelInfo GetPixel(long x, long y)
    {
        var row = GetPixelRow(y);
        int offset = (int)(x * NumberOfChannels);

        var pixel = new PixelInfo
        {
            StorageClass = StorageClass,
            Colorspace = Colorspace,
            Depth = Depth
        };

        pixel.Red = row[offset];
        if (NumberOfChannels > 1)
        {
            pixel.Green = row[offset + 1];
        }

        if (NumberOfChannels > 2)
        {
            pixel.Blue = row[offset + 2];
        }

        if (Colorspace == ColorspaceType.CMYK && NumberOfChannels > 3)
        {
            pixel.Black = row[offset + 3];
        }

        if (HasAlpha)
        {
            pixel.AlphaTrait = PixelTrait.Blend;
            pixel.Alpha = row[offset + NumberOfChannels - 1];
        }
        else
        {
            pixel.Alpha = Quantum.Opaque;
        }

        return pixel;
    }

    /// <summary>
    /// Writes a PixelInfo at the specified coordinates.
    /// </summary>
    public void SetPixel(long x, long y, in PixelInfo pixel)
    {
        var row = GetPixelRowForWrite(y);
        int offset = (int)(x * NumberOfChannels);

        row[offset] = Quantum.Clamp(pixel.Red);
        if (NumberOfChannels > 1)
        {
            row[offset + 1] = Quantum.Clamp(pixel.Green);
        }

        if (NumberOfChannels > 2)
        {
            row[offset + 2] = Quantum.Clamp(pixel.Blue);
        }

        if (Colorspace == ColorspaceType.CMYK && NumberOfChannels > 3)
        {
            row[offset + 3] = Quantum.Clamp(pixel.Black);
        }

        if (HasAlpha)
        {
            row[offset + NumberOfChannels - 1] = Quantum.Clamp(pixel.Alpha);
        }
    }

    /// <summary>
    /// The channel map defining how logical channels map to physical offsets.
    /// </summary>
    public ReadOnlySpan<PixelChannelMap> ChannelMap => channelMap;

    /// <summary>
    /// The underlying pixel cache (for advanced direct access).
    /// </summary>
    public PixelCache? Cache => pixelCache;

    /// <summary>
    /// Total pixel count.
    /// </summary>
    public long TotalPixels => Columns * Rows;

    private void SetupChannelMap()
    {
        int channelCount;
        switch (Colorspace)
        {
            case ColorspaceType.Gray:
            case ColorspaceType.LinearGray:
                channelCount = HasAlpha ? 2 : 1;
                channelMap = new PixelChannelMap[channelCount];
                channelMap[0] = new PixelChannelMap { Channel = PixelChannel.Gray, Traits = PixelTrait.Update, Offset = 0 };
                if (HasAlpha)
                {
                    channelMap[1] = new PixelChannelMap { Channel = PixelChannel.Alpha, Traits = PixelTrait.Update | PixelTrait.Blend, Offset = 1 };
                }

                break;

            case ColorspaceType.CMYK:
                channelCount = HasAlpha ? 5 : 4;
                channelMap = new PixelChannelMap[channelCount];
                channelMap[0] = new PixelChannelMap { Channel = PixelChannel.Cyan, Traits = PixelTrait.Update, Offset = 0 };
                channelMap[1] = new PixelChannelMap { Channel = PixelChannel.Magenta, Traits = PixelTrait.Update, Offset = 1 };
                channelMap[2] = new PixelChannelMap { Channel = PixelChannel.Yellow, Traits = PixelTrait.Update, Offset = 2 };
                channelMap[3] = new PixelChannelMap { Channel = PixelChannel.Black, Traits = PixelTrait.Update, Offset = 3 };
                if (HasAlpha)
                {
                    channelMap[4] = new PixelChannelMap { Channel = PixelChannel.Alpha, Traits = PixelTrait.Update | PixelTrait.Blend, Offset = 4 };
                }

                break;

            default: // RGB-like colorspaces
                channelCount = HasAlpha ? 4 : 3;
                channelMap = new PixelChannelMap[channelCount];
                channelMap[0] = new PixelChannelMap { Channel = PixelChannel.Red, Traits = PixelTrait.Update, Offset = 0 };
                channelMap[1] = new PixelChannelMap { Channel = PixelChannel.Green, Traits = PixelTrait.Update, Offset = 1 };
                channelMap[2] = new PixelChannelMap { Channel = PixelChannel.Blue, Traits = PixelTrait.Update, Offset = 2 };
                if (HasAlpha)
                {
                    channelMap[3] = new PixelChannelMap { Channel = PixelChannel.Alpha, Traits = PixelTrait.Update | PixelTrait.Blend, Offset = 3 };
                }

                break;
        }
        NumberOfChannels = channelCount;
    }

    /// <summary>
    /// Creates a deep copy of this image frame, including all pixel data and metadata.
    /// </summary>
    public ImageFrame Clone()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var clone = new ImageFrame();
        clone.Initialize(Columns, Rows, Colorspace, HasAlpha);
        clone.Depth = Depth;
        clone.Compression = Compression;
        clone.Interlace = Interlace;
        clone.Orientation = Orientation;
        clone.Type = Type;
        clone.Filter = Filter;
        clone.Interpolate = Interpolate;
        clone.StorageClass = StorageClass;
        clone.Quality = Quality;
        clone.BackgroundColor = BackgroundColor;
        clone.Metadata = Metadata.Clone();

        for (long y = 0; y < Rows; y++)
        {
            var srcRow = GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }

        return clone;
    }

    /// <summary>
    /// Removes all metadata (EXIF, ICC profile, XMP, IPTC) from this image.
    /// </summary>
    public void StripMetadata()
    {
        Metadata = new ImageMetadata();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            pixelCache?.Dispose();
            pixelCache = null;
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~ImageFrame()
    {
        Dispose();
    }
}
