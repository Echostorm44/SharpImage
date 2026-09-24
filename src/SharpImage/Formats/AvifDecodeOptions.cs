namespace SharpImage.Formats;

/// <summary>
/// Resource limits for AVIF / HEIF decoding, with libavif's avifDecoder defaults (avifdec --size-limit /
/// --dimension-limit): a file whose image items, grid, sequence track or AV1 frames exceed them is rejected with an
/// <see cref="System.IO.InvalidDataException"/> before any large allocation, so untrusted input cannot exhaust memory.
/// </summary>
public sealed class AvifDecodeOptions
{
    /// <summary>Chroma upsampling for 4:2:0 / 4:2:2 -> RGB (avifdec -u), libavif's avifChromaUpsampling: Automatic,
    /// BestQuality and Bilinear are bilinear (libyuv's filter for 8-bit, as libavif 1.4 picks it for all three), Fastest
    /// and Nearest nearest-neighbour chroma. Matches avifdec -u for every mode.</summary>
    public AvifChromaUpsampling ChromaUpsampling { get; init; }

    /// <summary>libavif's strict validation (avifdec without --no-strict: AVIF_STRICT_PIXI_REQUIRED,
    /// AVIF_STRICT_CLAP_VALID, AVIF_STRICT_ALPHA_ISPE_REQUIRED): every AV1 item needs a 'pixi' property, an alpha item
    /// its 'ispe', and a 'clap' must describe a valid crop — else <see cref="System.IO.InvalidDataException"/>.
    /// Off by default (lenient, as avifdec --no-strict: a missing pixi / alpha ispe is tolerated, an invalid clap
    /// ignored).</summary>
    public bool Strict { get; init; }

    /// <summary>libavif AVIF_DEFAULT_IMAGE_SIZE_LIMIT: 16384 x 16384 pixels.</summary>
    public const long DefaultImageSizeLimit = 16384L * 16384;

    /// <summary>libavif AVIF_DEFAULT_IMAGE_DIMENSION_LIMIT: 32768 pixels per side.</summary>
    public const int DefaultImageDimensionLimit = 32768;

    /// <summary>libavif AVIF_DEFAULT_IMAGE_COUNT_LIMIT: 12 hours of 60 fps frames.</summary>
    public const int DefaultImageCountLimit = 12 * 3600 * 60;

    /// <summary>The defaults every decode uses unless other options are passed.</summary>
    public static AvifDecodeOptions Default { get; } = new();

    private readonly long imageSizeLimit = DefaultImageSizeLimit;
    private readonly int imageDimensionLimit = DefaultImageDimensionLimit;
    private readonly int imageCountLimit = DefaultImageCountLimit;

    /// <summary>Maximum width x height of any image, grid or frame, 1 to <see cref="DefaultImageSizeLimit"/> (as in
    /// libavif, larger values and 0 are not supported).</summary>
    public long ImageSizeLimit
    {
        get => imageSizeLimit;
        init => imageSizeLimit = value is >= 1 and <= DefaultImageSizeLimit ? value
            : throw new System.ArgumentOutOfRangeException(nameof(ImageSizeLimit), value, $"Must be between 1 and {DefaultImageSizeLimit}.");
    }

    /// <summary>Maximum width or height, or 0 for no per-side limit.</summary>
    public int ImageDimensionLimit
    {
        get => imageDimensionLimit;
        init => imageDimensionLimit = value >= 0 ? value : throw new System.ArgumentOutOfRangeException(nameof(ImageDimensionLimit));
    }

    /// <summary>Maximum number of frames in a sequence (or layers of a progressive image), or 0 for no limit.</summary>
    public int ImageCountLimit
    {
        get => imageCountLimit;
        init => imageCountLimit = value >= 0 ? value : throw new System.ArgumentOutOfRangeException(nameof(ImageCountLimit));
    }

    /// <summary>Worker threads a decode may use (avifdec -j): 0 (default) uses every core, 1 decodes on the calling
    /// thread only. The output is identical whatever the count.</summary>
    public int MaxThreads
    {
        get => maxThreads;
        init => maxThreads = value >= 0 ? value : throw new System.ArgumentOutOfRangeException(nameof(MaxThreads));
    }
    private readonly int maxThreads;

    internal int ThreadCount => maxThreads > 0 ? maxThreads : System.Environment.ProcessorCount;

    // libavif avifDimensionsTooLarge.
    internal bool TooLarge(long width, long height) =>
        width > imageSizeLimit / System.Math.Max(height, 1) || (imageDimensionLimit != 0 && (width > imageDimensionLimit || height > imageDimensionLimit));
}

/// <summary>avifdec -u / libavif avifChromaUpsampling.</summary>
public enum AvifChromaUpsampling
{
    /// <summary>libyuv bilinear where available (libavif's default).</summary>
    Automatic,
    /// <summary>Nearest-neighbour through libyuv where available.</summary>
    Fastest,
    /// <summary>Bilinear (libavif 1.4 still uses libyuv's bilinear filter for 8-bit).</summary>
    BestQuality,
    /// <summary>Nearest-neighbour chroma.</summary>
    Nearest,
    /// <summary>Bilinear chroma (libyuv's filter where available).</summary>
    Bilinear,
}
