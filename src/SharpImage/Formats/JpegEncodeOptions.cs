namespace SharpImage.Formats;

/// <summary>The colour space a JPEG is coded in (libjpeg's jpeg_color_space).</summary>
public enum JpegColorSpace
{
    /// <summary>libjpeg's jpeg_default_colorspace: greyscale images as greyscale, colour as YCbCr, CMYK as CMYK.</summary>
    Auto,
    /// <summary>One component (cjpeg -grayscale); colour input is converted to luma.</summary>
    Grayscale,
    /// <summary>Y, Cb, Cr with a JFIF marker.</summary>
    YCbCr,
    /// <summary>R, G, B coded directly, with an Adobe marker (cjpeg -rgb).</summary>
    Rgb,
    /// <summary>C, M, Y, K with an Adobe marker (samples inverted, as Adobe applications write them).</summary>
    Cmyk,
    /// <summary>CMYK converted to Y, Cb, Cr, K with an Adobe marker.</summary>
    Ycck,
}

/// <summary>The forward DCT (cjpeg -dct).</summary>
public enum JpegDctMethod
{
    /// <summary>Accurate integer DCT (jfdctint.c, cjpeg -dct int). libjpeg's default.</summary>
    IntegerSlow,
    /// <summary>Fast, less accurate integer DCT (jfdctfst.c, cjpeg -dct fast).</summary>
    IntegerFast,
    /// <summary>Floating-point DCT (jfdctflt.c, cjpeg -dct float).</summary>
    Float,
}

/// <summary>One scan of a scan script (cjpeg -scans): component indexes, spectral selection Ss..Se and successive
/// approximation Ah / Al (for lossless files Ss is the predictor and Al the point transform).</summary>
public sealed record JpegScanInfo(int[] Components, int Ss, int Se, int Ah, int Al);

/// <summary>
/// Options for <see cref="JpegCoder.Encode(SharpImage.Image.ImageFrame, JpegEncodeOptions)"/>, libjpeg-turbo's cjpeg
/// switches. The defaults are cjpeg's: quality 75, 4:2:0 YCbCr for colour, accurate integer DCT, standard Huffman
/// tables, sequential (baseline when the tables allow), JFIF 1.01 with density 0 / 1:1.
/// </summary>
public sealed class JpegEncodeOptions
{
    /// <summary>cjpeg -quality N (1..100) for every quantization table; null keeps libjpeg's default (75, limited
    /// to baseline values).</summary>
    public int? Quality { get; init; }

    /// <summary>cjpeg -quality N,N,...: a quality per quantization table slot (the last value repeats). Takes
    /// precedence over <see cref="Quality"/>.</summary>
    public int[]? TableQualities { get; init; }

    /// <summary>cjpeg -baseline: clamp quantization values to 8 bits.</summary>
    public bool ForceBaseline { get; init; }

    /// <summary>cjpeg -qtables: basic quantization tables (up to 4, 64 values each in natural row-major order),
    /// scaled by their slot's quality (100% when no quality is given).</summary>
    public ushort[][]? QuantTables { get; init; }

    /// <summary>cjpeg -qslots N,N,...: the quantization table each component uses (the last value repeats).</summary>
    public int[]? QuantTableSlots { get; init; }

    /// <summary>cjpeg -sample HxV,...: sampling factors per component (components not listed get 1x1).</summary>
    public (int H, int V)[]? SamplingFactors { get; init; }

    /// <summary>The coded colour space (cjpeg -grayscale / -rgb).</summary>
    public JpegColorSpace ColorSpace { get; init; }

    /// <summary>cjpeg -dct.</summary>
    public JpegDctMethod Dct { get; init; }

    /// <summary>cjpeg -optimize: Huffman tables computed from the image (always on for progressive, lossless and
    /// 12-bit files).</summary>
    public bool OptimizeCoding { get; init; }

    /// <summary>cjpeg -progressive: libjpeg's default progression (jpeg_simple_progression).</summary>
    public bool Progressive { get; init; }

    /// <summary>cjpeg -scans: an explicit scan script (sequential, progressive or lossless).</summary>
    public JpegScanInfo[]? Scans { get; init; }

    /// <summary>cjpeg -arithmetic: arithmetic entropy coding.</summary>
    public bool Arithmetic { get; init; }

    /// <summary>cjpeg -restart NB: a restart marker every N MCUs (0 = none).</summary>
    public int RestartInterval { get; init; }

    /// <summary>cjpeg -restart N: a restart marker every N MCU rows (takes precedence over
    /// <see cref="RestartInterval"/>).</summary>
    public int RestartRows { get; init; }

    /// <summary>cjpeg -smooth N (0..100): smooth the input before downsampling.</summary>
    public int Smoothing { get; init; }

    /// <summary>cjpeg -precision: 8 or 12 bits for DCT files; 2..16 for lossless.</summary>
    public int Precision { get; init; } = 8;

    /// <summary>cjpeg -lossless psv[,Pt]: the lossless predictor 1..7 (0 = a DCT file).</summary>
    public int LosslessPredictor { get; init; }

    /// <summary>cjpeg -lossless psv,Pt: the lossless point transform.</summary>
    public int LosslessPointTransform { get; init; }

    /// <summary>cjpeg -icc: an ICC profile written as APP2 ICC_PROFILE markers.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Also write the image's metadata after the JFIF / Adobe marker: EXIF (APP1), XMP (APP1), its ICC profile
    /// (APP2, unless <see cref="IccProfile"/> is given) and IPTC (APP13). Off by default, as cjpeg copies none.</summary>
    public bool WriteMetadata { get; init; }

    /// <summary>JFIF density unit (0 = aspect ratio only, 1 = dots per inch, 2 = dots per cm) and densities.</summary>
    public int DensityUnit { get; init; }
    public int XDensity { get; init; } = 1;
    public int YDensity { get; init; } = 1;
}
