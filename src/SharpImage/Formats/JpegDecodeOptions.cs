namespace SharpImage.Formats;

/// <summary>
/// Options for <see cref="JpegCoder.Read(System.IO.Stream, JpegDecodeOptions)"/>, libjpeg-turbo's decompression
/// parameters as djpeg sets them. The defaults are libjpeg's: full size, accurate integer IDCT, fancy upsampling,
/// block smoothing, colour output. djpeg -fast is <see cref="Dct"/> = IntegerFast, <see cref="FancyUpsampling"/> off and,
/// when quantizing, <see cref="TwoPassQuantize"/> off with <see cref="Dither"/> = Ordered.
/// </summary>
public sealed class JpegDecodeOptions
{
    /// <summary>Largest accepted width x height (checked on the frame header, before scaling).</summary>
    public long MaxPixels { get; init; } = JpegCoder.DefaultMaxPixels;

    /// <summary>djpeg -scale M/N: the output is scaled by the smallest n/8 (n = 1..16) at or above M/N, using libjpeg's
    /// scaled inverse DCTs. Lossless files are always decoded at full size.</summary>
    public int ScaleNumerator { get; init; } = 1;

    /// <summary>The N of djpeg -scale M/N.</summary>
    public int ScaleDenominator { get; init; } = 1;

    /// <summary>djpeg -dct: the 8x8 inverse DCT (scaled sizes always use the accurate integer transforms).</summary>
    public JpegDctMethod Dct { get; init; }

    /// <summary>Triangle-filtered chroma upsampling (libjpeg's do_fancy_upsampling); djpeg -nosmooth turns it off
    /// (sample replication, as libjpeg's merged upsampler).</summary>
    public bool FancyUpsampling { get; init; } = true;

    /// <summary>libjpeg's do_block_smoothing: interblock smoothing of progressive files whose later scans are missing.</summary>
    public bool BlockSmoothing { get; init; } = true;

    /// <summary>djpeg -grayscale: greyscale output (luma of YCbCr, the weighted sum of RGB; not for CMYK / YCCK).
    /// Returned as an sRGB frame with equal channels, like greyscale files.</summary>
    public bool Grayscale { get; init; }

    /// <summary>djpeg -strict: libjpeg's warnings (corrupt entropy data, premature end of file, restart resynchronisation,
    /// extraneous bytes, inconsistent progression, unknown JFIF / Adobe codes) fail with
    /// <see cref="System.IO.InvalidDataException"/> instead of being recovered from.</summary>
    public bool Strict { get; init; }

    /// <summary>djpeg -maxscans N: fail on a file with more than N scans (0 = no limit).</summary>
    public int MaxScans { get; init; }

    /// <summary>djpeg -crop WxH+X+Y (jpeg_crop_scanline): decode only this region of the (scaled) output. As in libjpeg,
    /// X moves down to an iMCU column boundary and the width grows by as much (the returned frame's
    /// <see cref="SharpImage.Image.ImageFrame.Page"/> holds the region's actual origin), and the upsampler treats the
    /// region's left and right ends as image edges. Not for lossless files.</summary>
    public (int X, int Y, int Width, int Height)? Crop { get; init; }

    /// <summary>djpeg -skip Y0,Y1 (jpeg_skip_scanlines): decode every row except Y0..Y1 (inclusive). Not for lossless
    /// files. (libjpeg-turbo 3.1.4's merged upsampler, used with <see cref="FancyUpsampling"/> off for 4:2:0 colour
    /// output, returns rows shifted by one after a skip that starts on an odd row; this returns the intended rows.) Skipping every row is an error (djpeg writes an empty image).</summary>
    public (int Start, int End)? SkipRows { get; init; }

    /// <summary>djpeg -rgb565: RGB565 output (not for CMYK, lossless or 12-bit files), returned as 8-bit-per-channel
    /// samples R &amp; 0xF8, G &amp; 0xFC, B &amp; 0xF8 as djpeg's BMP writer expands them; ordered-dithered unless
    /// <see cref="Dither"/> is None.</summary>
    public bool Rgb565 { get; init; }

    /// <summary>djpeg -colors N: quantize to at most N colors (0 = no quantization). The frame's pixels are then colors
    /// of <see cref="SharpImage.Image.ImageFrame.Colormap"/>. Two-pass median cut for colour output unless
    /// <see cref="TwoPassQuantize"/> is off; greyscale and CMYK output always use the one-pass quantizer.</summary>
    public int QuantizeColors { get; init; }

    /// <summary>djpeg -onepass turns this off: the one-pass quantizer's equally spaced colormap instead of median cut.</summary>
    public bool TwoPassQuantize { get; init; } = true;

    /// <summary>djpeg -map: quantize colour output to these colors (samples of the file's precision).</summary>
    public (int R, int G, int B)[]? QuantizeColormap { get; init; }

    /// <summary>djpeg -dither: the quantizers' dithering (the two-pass quantizer does Floyd-Steinberg or none), and
    /// whether RGB565 output is dithered.</summary>
    public JpegDitherMode Dither { get; init; }
}

/// <summary>djpeg -dither.</summary>
public enum JpegDitherMode
{
    /// <summary>Floyd-Steinberg error diffusion (libjpeg's default).</summary>
    FloydSteinberg,
    /// <summary>Ordered (16x16) dithering; the one-pass quantizer only.</summary>
    Ordered,
    /// <summary>No dithering.</summary>
    None,
}
