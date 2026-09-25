using System.Threading.Tasks;
// AVIF/HEIC format coder — read and write.
// Pure C# implementation of AVIF (AV1 Still Image) and HEIC (HEVC Still Image).
// Uses ISOBMFF (ISO Base Media File Format) container with intra-frame encoding.
// AVIF: ftyp=avif/avis, coding=av01 (AV1 intra)
// HEIC: ftyp=heic/heix, coding=hvc1 (HEVC intra)
// Reference: ISO/IEC 14496-12 (ISOBMFF), AOM AV1 spec, ImageMagick coders/heic.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Distinguishes AVIF from HEIC container type.
/// </summary>
public enum HeifContainerType
{
    Avif,
    Heic
}

/// <summary>Chroma subsampling of an encoded AVIF colour image.</summary>
public enum AvifChromaSubsampling
{
    /// <summary>Automatic: 4:2:0 (the most compact and most widely decoded).</summary>
    Auto,
    /// <summary>4:2:0 — chroma at half width and half height (AV1 Main profile).</summary>
    Yuv420,
    /// <summary>4:2:2 — chroma at half width, full height (AV1 Professional profile).</summary>
    Yuv422,
    /// <summary>4:4:4 — full-resolution chroma (AV1 High profile; Professional at 12-bit).</summary>
    Yuv444,
    /// <summary>4:0:0 — monochrome: luma computed from RGB with the matrix coefficients, chroma dropped (avifenc
    /// -y 400). Grayscale sources are always coded this way.</summary>
    Yuv400,
}

/// <summary>libavif bit-depth extension recipes (avifenc -d D,Dextension): a 16-bit image stored as a backward-compatible
/// base item plus a hidden item with the remaining bits, combined by a 'sato' Sample Transform derived item.</summary>
public enum AvifBitDepthExtension
{
    /// <summary>No extension (a single coded item).</summary>
    None,
    /// <summary>avifenc -d 8,8: 8 most significant bits (lossless) + 8 least significant bits.</summary>
    Bits8Plus8,
    /// <summary>avifenc -d 12,4: 12 most significant bits (lossless) + 4 least significant bits.</summary>
    Bits12Plus4,
    /// <summary>avifenc -d 12,8: 12 most significant bits (lossy allowed) + an 8-bit residual overlapping 4 bits.</summary>
    Bits12Plus8Overlap4,
}

/// <summary>One layer of a layered AVIF (avifenc --layered): coded at a scaling fraction of the image size with its own
/// quality, optionally from its own image (same size as the main image, as avifenc requires).</summary>
public sealed class AvifLayer
{
    /// <summary>The layer's source; null = the image being encoded.</summary>
    public ImageFrame? Image { get; set; }

    /// <summary>Quality 0..99 (libavif scale). Null: the options' <see cref="AvifEncodeOptions.Quality"/>, else its Qp.</summary>
    public int? Quality { get; set; }

    /// <summary>Alpha quality 0..99. Null: <see cref="Quality"/>, else the options' alpha quality.</summary>
    public int? QualityAlpha { get; set; }

    /// <summary>avifenc --scaling-mode N/D: the layer is coded at ceil(size * N / D) (as libaom sizes a scaled frame);
    /// one of 1/1, 1/2, 1/4, 1/8, 3/4, 3/5, 4/5 (libaom's modes).</summary>
    public int ScaleNumerator { get; set; } = 1;

    /// <inheritdoc cref="ScaleNumerator"/>
    public int ScaleDenominator { get; set; } = 1;
}

/// <summary>
/// Options that control AVIF encoding.
/// </summary>
public sealed class AvifEncodeOptions
{
    /// <summary>Quantization parameter 0..51 (0 = highest quality / largest file). Default 20.</summary>
    public int Qp { get; set; } = 20;

    /// <summary>Deblocking filter sharpness 0..7 (loop_filter_sharpness; libaom / avifenc -a sharpness=S): higher values
    /// weaken the filter across strong edges. The deblocking level search runs with it.</summary>
    public int Sharpness { get; set; }

    /// <summary>Creation / modification time written into an image sequence's movie, track and media headers (avifenc
    /// --creation-time / --modification-time); null takes <see cref="ImageSequence.CreationTime"/> /
    /// <see cref="ImageSequence.ModificationTime"/>, which are also what decoding reports.</summary>
    public DateTimeOffset? CreationTime { get; set; }

    /// <inheritdoc cref="CreationTime"/>
    public DateTimeOffset? ModificationTime { get; set; }

    /// <summary>Encoder speed 0 (slowest, smallest files) .. 10 (fastest), as avifenc -s. Each step prunes the
    /// rate-distortion search further (mode candidates, partition shapes, transform sets, filter searches, angle
    /// deltas, RDOQ); measured on the encoder corpus, every speed gives smaller files than libaom at the same speed
    /// setting (7..10: +20 / +24 / +31 / +34 % BD-rate vs libaom speed 0, libaom +22 / +26 / +55 / +55 %).</summary>
    public int Speed { get; set; } = DefaultSpeed;

    /// <summary>Default <see cref="Speed"/>.</summary>
    public const int DefaultSpeed = 6;

    /// <summary>Worker threads (avifenc -j): 0 (default) uses every core, 1 encodes on the calling thread. The output
    /// is identical for any thread count.</summary>
    public int MaxThreads { get; set; }

    /// <summary>Coded bit depth: 8, 10 or 12 — or 0 (default) to choose automatically: 8 when every source sample
    /// is exactly representable at 8 bits (e.g. images decoded from 8-bit formats), otherwise 10. 12-bit is coded
    /// with AV1 Professional profile (seq_profile 2).</summary>
    public int BitDepth { get; set; }

    /// <summary>Chroma subsampling for colour images (grayscale images are always coded monochrome).</summary>
    public AvifChromaSubsampling ChromaSubsampling { get; set; } = AvifChromaSubsampling.Auto;

    /// <summary>CICP colour primaries (ITU-T H.273). Null: the image's <c>Metadata.Cicp</c>, else 1 (BT.709/sRGB)
    /// — or 2 (unspecified) when an ICC profile is attached, as avifenc does.</summary>
    public int? ColorPrimaries { get; set; }

    /// <summary>CICP transfer characteristics. Null: the image's <c>Metadata.Cicp</c>, else 13 (sRGB) — or 2 when an
    /// ICC profile is attached.</summary>
    public int? TransferCharacteristics { get; set; }

    /// <summary>CICP matrix coefficients used to code RGB as YUV: 6/5 (BT.601, default), 1 (BT.709), 9 (BT.2020 NCL),
    /// 4 (FCC), 7 (SMPTE 240M), 12 (chromaticity-derived NCL), 0 (identity: RGB coded as GBR, 4:4:4 only),
    /// 8 (YCgCo, full range only), 16/17 (YCgCo-Re/Ro: lossless-reversible, coded 2/1 bits deeper than the RGB).
    /// Null: the image's <c>Metadata.Cicp</c> matrix when it is one of the linear kr/kb matrices, else 6.</summary>
    public int? MatrixCoefficients { get; set; }

    /// <summary>Full (true, default) or limited/studio (false) YUV range.</summary>
    public bool FullRange { get; set; } = true;

    /// <summary>'irot' rotation to signal, in anti-clockwise quarter turns (0..3). Null: derived (with
    /// <see cref="Mirror"/>) from the image's EXIF-style <c>Orientation</c>, as avifenc does. Pixels are stored as
    /// given; readers apply the rotation for display.</summary>
    public int? Rotation { get; set; }

    /// <summary>'imir' mirror to signal: 0 = top/bottom exchanged, 1 = left/right exchanged (ISO/IEC 23008-12:2022).
    /// Applied after <see cref="Rotation"/>. Null: derived from <c>Orientation</c>.</summary>
    public int? Mirror { get; set; }

    /// <summary>Clean aperture ('clap'): the display crop (x, y, width, height) within the coded image. Applied by
    /// readers before rotation/mirroring.</summary>
    public (int X, int Y, int Width, int Height)? CropRect { get; set; }

    /// <summary>Pixel aspect ratio ('pasp') to signal. Null: the image's <c>Metadata.PixelAspectRatio</c>.</summary>
    public SharpImage.Metadata.PixelAspectRatio? PixelAspectRatio { get; set; }

    /// <summary>Lossless coding (AV1 base_q_idx 0: 4x4 Walsh-Hadamard, no in-loop filters): the decoded samples equal
    /// the coded ones exactly. Unless <see cref="MatrixCoefficients"/> is set, RGB is coded with the identity matrix at
    /// 4:4:4, so an 8/10/12-bit source round-trips bit-exactly (avifenc --lossless); YCgCo-Re (16) is also exact.
    /// <see cref="Qp"/> is ignored. Alpha is coded losslessly too.</summary>
    public bool Lossless { get; set; }

    /// <summary>Premultiply colour by alpha before coding and signal it ('prem' item reference), as avifenc
    /// --premultiply. Readers (including this one) un-premultiply on decode. Only meaningful for images with alpha.</summary>
    public bool PremultiplyAlpha { get; set; }

    /// <summary>HDR content light level ('clli'). Null: the image's <c>Metadata.ContentLightLevel</c>.</summary>
    public SharpImage.Metadata.ContentLightLevel? ContentLightLevel { get; set; }

    /// <summary>HDR mastering display colour volume ('mdcv'). Null: the image's <c>Metadata.MasteringDisplay</c>.</summary>
    public SharpImage.Metadata.MasteringDisplayColourVolume? MasteringDisplay { get; set; }

    /// <summary>AV1 film grain parameters signalled on the colour item (decoders synthesize the grain on display), e.g.
    /// <see cref="AvifFilmGrain.TestVector"/> (aomenc --film-grain-test) or <see cref="AvifFilmGrain.ParseTable"/>
    /// (--film-grain-table). Not allowed with <see cref="Lossless"/>.</summary>
    public AvifFilmGrain? FilmGrain { get; set; }

    /// <summary>Denoise the colour planes and signal the removed noise as film grain (aomenc / avifenc
    /// --denoise-noise-level; 0 = off). As in libaom's all-intra mode, which libavif uses for still images, any value
    /// above 0 enables it and the Wiener filter strength is estimated from the image itself (capped at 5); set
    /// <see cref="DenoiseUseRequestedLevel"/> to use this value / 10 instead (libaom's good-quality mode). The grain
    /// model (libaom noise_model) fits AR coefficients and piecewise scaling functions on flat blocks. Not available
    /// for 4:2:2 (as in libaom) or with <see cref="Lossless"/> / <see cref="FilmGrain"/>.</summary>
    public int DenoiseNoiseLevel { get; set; }

    /// <summary>Use <see cref="DenoiseNoiseLevel"/> / 10 as the Wiener noise level instead of the all-intra estimate.</summary>
    public bool DenoiseUseRequestedLevel { get; set; }

    /// <summary>Denoising / noise-model block size (--denoise-block-size): 8, 16 or 32 (default).</summary>
    public int DenoiseBlockSize { get; set; } = 32;

    /// <summary>Encode the denoised planes (--enable-dnl-denoising, default on). Off keeps the source planes but still
    /// signals the estimated grain.</summary>
    public bool DenoiseApply { get; set; } = true;

    /// <summary>Progressive (layered) AVIF, as avifenc --progressive: a half-size, low-quality base layer (quality 10)
    /// followed by the full image at <see cref="Qp"/>, in one item with an 'a1lx' layer index. Progressive readers
    /// (<see cref="HeifCoder.DecodeProgressive"/>, libavif allowProgressive) can show the preview first; every other
    /// reader decodes the full image. Not available with <see cref="Lossless"/>.</summary>
    public bool Progressive { get; set; }

    /// <summary>Image sequences (avifenc --keyframe): the maximum distance between key frames — any
    /// <c>KeyframeInterval</c> consecutive frames include at least one. 0 (default): only the first frame, plus scene
    /// cuts; 1: every frame is a key frame (all-intra). The other frames are inter frames predicted from the previous
    /// frame.</summary>
    public int KeyframeInterval { get; set; }

    /// <summary>An ISO 21496-1 gain map to store with the image (e.g. from <see cref="HeifCoder.ComputeGainMap"/>): a
    /// 'tmap' tone-mapped derived item preferred over the image, and the gain map as a hidden image item, laid out as
    /// libavif writes them. Its <see cref="AvifGainMap.Image"/> is coded at its <c>Depth</c> (8, 10 or 12).</summary>
    public AvifGainMap? GainMap { get; set; }

    /// <summary>Quantization parameter for the gain map image. Null: <see cref="Qp"/>.</summary>
    public int? GainMapQp { get; set; }

    /// <summary>Code the gain map losslessly (avifgainmaputil --qgain-map 100): its YUV samples are exact.</summary>
    public bool GainMapLossless { get; set; }

    /// <summary>Downscaling factor of the gain map image (avifgainmaputil combine --downscaling): the gain map is
    /// converted to YUV at full size, then scaled to (size + factor / 2) / factor with libyuv's box filter.</summary>
    public int GainMapDownscaling { get; set; } = 1;

    /// <summary>Chroma subsampling of the gain map image (single-channel gain maps are coded 4:0:0). Default 4:4:4, as
    /// avifgainmaputil combine.</summary>
    public AvifChromaSubsampling GainMapChromaSubsampling { get; set; } = AvifChromaSubsampling.Yuv444;

    /// <summary>Encode as a grid of Columns x Rows independently coded cells (avifenc --grid MxN): a 'grid' derived
    /// item over hidden cell items (and an alpha grid). Cell sizes follow avifenc: ceil(size / count), at least 64 px
    /// (MIAF), even along subsampled axes; the last row / column may be smaller. Null or 1x1: a single coded item.</summary>
    public (int Columns, int Rows)? Grid { get; set; }

    /// <summary>log2 of the number of AV1 tile columns (avifenc --tilecolslog2, 0..6): more tiles decode in parallel at
    /// some compression cost. Clamped to what the frame size allows (at least the tiles AV1 requires).</summary>
    public int TileColumnsLog2 { get; set; }

    /// <summary>log2 of the number of AV1 tile rows (avifenc --tilerowslog2, 0..6).</summary>
    public int TileRowsLog2 { get; set; }

    /// <summary>Choose the tiling automatically (avifenc --autotiling; overrides the log2 values): up to 8 tiles, about
    /// one per 512x512 pixels, more along the longer dimension. Null (default): automatic at <see cref="Speed"/> 6 and
    /// faster when no tile counts are set (tiles encode and decode in parallel for ~0.3-0.5% size), one tile at slower
    /// speeds; false: always the explicit <see cref="TileColumnsLog2"/> / <see cref="TileRowsLog2"/>.</summary>
    public bool? AutoTiling { get; set; }

    /// <summary>Quality 0..100 on libavif's scale (avifenc -q; overrides <see cref="Qp"/>): mapped to a quantizer as
    /// libavif does (its default libaom tune=iq table; the linear formula for the identity matrix) and to a base
    /// q-index through libaom's quantizer table. 100 = lossless AV1 coding of the YUV samples (the matrix is kept; set
    /// <see cref="Lossless"/> for avifenc --lossless, which also codes RGB with the identity matrix).</summary>
    public int? Quality { get; set; }

    /// <summary>Alpha quality 0..100 (avifenc --qalpha; 100 = lossless alpha). Null: <see cref="Quality"/> when set
    /// (as avifenc), otherwise alpha is coded at half the colour q-index.</summary>
    public int? QualityAlpha { get; set; }

    /// <summary>Gain map quality 0..100 (avifenc / avifgainmaputil --qgain-map). Null: <see cref="GainMapLossless"/> /
    /// <see cref="GainMapQp"/>, else <see cref="Quality"/> when set.</summary>
    public int? QualityGainMap { get; set; }

    /// <summary>Target file size in bytes (avifenc --target-size): binary search over quality 0..100 (10..100 when
    /// progressive) for the encode closest to it; <see cref="Quality"/> / <see cref="QualityAlpha"/> /
    /// <see cref="QualityGainMap"/> that are set stay fixed. Up to ~7 encodes.</summary>
    public int? TargetSize { get; set; }

    /// <summary>A layered AVIF (avifenc --layered): 2..4 layers coded in one item as spatial layers (the first a key
    /// frame, the rest intra-only), with an 'a1lx' index so progressive readers can show each layer as it arrives; other
    /// readers decode the last layer. Not combined with <see cref="Progressive"/>, <see cref="Lossless"/> or
    /// <see cref="Grid"/>.</summary>
    public IList<AvifLayer>? Layers { get; set; }

    /// <summary>Sharp RGB -> YUV 4:2:0 (avifenc --sharpyuv, libsharpyuv): chroma and luma refined together in linear light
    /// so upsampled colour edges stay crisp. Applies to 4:2:0 with the kr/kb matrices; ignored otherwise.</summary>
    public bool SharpYuv { get; set; }

    /// <summary>libavif avifRGBImage.avoidLibYUV: convert RGB to YUV with libavif's built-in float path everywhere
    /// instead of libyuv's 8-bit fixed-point converters (which libavif, and so the default here, uses for 8-bit RGB to
    /// 8-bit BT.601 YUV). More precise, slower, and no longer identical to avifenc's output.</summary>
    public bool AvoidLibyuv { get; set; }

    /// <summary>Store 16-bit samples through a Sample Transform (avifenc -d 8,8 / 12,4 / 12,8): readers without Sample
    /// Transform support see the base image, this decoder (and avifdec --sato) the full 16 bits.</summary>
    public AvifBitDepthExtension BitDepthExtension { get; set; }

    internal AvifEncodeOptions Clone() => (AvifEncodeOptions)MemberwiseClone();
}

/// <summary>Film grain for one AVIF encode: explicit parameters, or denoise-and-estimate (Level &lt; 0 = all-intra estimate).</summary>
internal sealed record AvifGrainRequest(AvifFilmGrain? Explicit, bool Denoise, float Level, int BlockSize, bool Apply);

public static partial class HeifCoder
{
    // AVIF ftypes
    private static readonly string[] AvifBrands = [ "avif", "avis", "avio" ];
    // HEIC ftypes
    private static readonly string[] HeicBrands = [ "heic", "heix", "hevc", "hevx", "heim", "heis" ];

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data[4..8]) != "ftyp")
        {
            return false;
        }

        // The major brand of a real HEIC/AVIF is often the generic HEIF brand 'mif1'/'msf1',
        // with 'heic'/'avif' listed only among the compatible brands — so check every brand
        // in the ftyp box, and accept the generic HEIF brands too.
        foreach (string brand in FtypBrands(data))
        {
            if (IsAvifBrand(brand) || IsHeicBrand(brand) || brand is "mif1" or "msf1" or "mif1")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAvif(ReadOnlySpan<byte> data)
    {
        // An AVIF carries AV1 configuration ('av1C'); a HEIC carries 'hvcC'. Prefer that over
        // the brand, since the major brand is frequently the generic 'mif1'.
        if (ContainsFourCc(data, "av1C"))
        {
            return true;
        }

        if (ContainsFourCc(data, "hvcC"))
        {
            return false;
        }

        foreach (string brand in FtypBrands(data))
        {
            if (IsAvifBrand(brand))
            {
                return true;
            }
        }

        return false;
    }

    // Enumerates the major + compatible brands in the ftyp box.
    private static System.Collections.Generic.IEnumerable<string> FtypBrands(ReadOnlySpan<byte> data)
    {
        var brands = new System.Collections.Generic.List<string>();
        if (data.Length < 16 || Encoding.ASCII.GetString(data[4..8]) != "ftyp")
        {
            return brands;
        }

        int boxSize = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        int end = Math.Min(boxSize, data.Length);
        brands.Add(Encoding.ASCII.GetString(data[8..12]).TrimEnd('\0')); // major brand
        for (int p = 16; p + 4 <= end; p += 4)                            // compatible brands
        {
            brands.Add(Encoding.ASCII.GetString(data[p..(p + 4)]).TrimEnd('\0'));
        }

        return brands;
    }

    private static bool ContainsFourCc(ReadOnlySpan<byte> data, string fourcc)
    {
        byte a = (byte)fourcc[0], b = (byte)fourcc[1], c = (byte)fourcc[2], d = (byte)fourcc[3];
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Decodes an AVIF / HEIC image under the given resource limits (null: <see cref="AvifDecodeOptions.Default"/>).</summary>
    public static ImageFrame Decode(byte[] data, AvifDecodeOptions? options) => WithLimits(options, () => Decode(data));

    /// <summary><see cref="DecodeSequence(byte[])"/> under the given resource limits.</summary>
    public static ImageSequence DecodeSequence(byte[] data, AvifDecodeOptions? options) => WithLimits(options, () => DecodeSequence(data));

    /// <summary><see cref="DecodeProgressive(byte[])"/> under the given resource limits.</summary>
    public static IReadOnlyList<ImageFrame> DecodeProgressive(byte[] data, AvifDecodeOptions? options) => WithLimits(options, () => DecodeProgressive(data));

    // The limits of the decode running on this thread (libavif avifDecoder imageSizeLimit / imageDimensionLimit /
    // imageCountLimit); every decode is limited, by default with libavif's defaults.
    [ThreadStatic] private static AvifDecodeOptions? t_limits;
    private static AvifDecodeOptions Limits => t_limits ?? AvifDecodeOptions.Default;

    private static T WithLimits<T>(AvifDecodeOptions? options, Func<T> decode)
    {
        var previous = t_limits;
        t_limits = options ?? previous;
        try { return decode(); }
        finally { t_limits = previous; }
    }

    // An AV1 decoder for untrusted input: frames larger than the image size limit fail before allocation (libavif sets
    // dav1d's frame_size_limit / checks aom's stream info against imageSizeLimit).
    private static Av1.Av1Decoder NewAv1Decoder() => new() { FrameSizeLimit = Limits.ImageSizeLimit, MaxThreads = Limits.ThreadCount };

    // libavif avifDecoderParse: every image item (coded or grid, with data, not a thumbnail) with an 'ispe' must have a
    // non-zero size within the limits.
    private static void CheckItemLimits(HeifContainer c)
    {
        foreach (var item in c.Items.Values)
        {
            if (item.Type is not ("av01" or "grid" or "hvc1") || c.ReferencesFrom(item.Id, "thmb").Count > 0) continue;
            if (item.Type != "grid" && item.Extents.Count == 0) continue;
            if (Limits.Strict && item.Type == "av01" && c.Property(item.Id, "pixi") == null)
                throw new InvalidDataException($"[Strict] Item ID [{item.Id}] of type 'av01' is missing mandatory pixi property.");
            if (c.Property(item.Id, "ispe") is not { Len: >= 12 } p)
            {
                // Alpha may lack 'ispe' (libavif without AVIF_STRICT_ALPHA_ISPE_REQUIRED: the decoded size is used).
                if (IsAlphaAux(c, item.Id) && !Limits.Strict) continue;
                throw new InvalidDataException(IsAlphaAux(c, item.Id)
                    ? $"[Strict] Alpha auxiliary image item ID [{item.Id}] is missing a mandatory ispe property."
                    : $"Item ID [{item.Id}] is missing a mandatory ispe property.");
            }
            long w = BinaryPrimitives.ReadUInt32BigEndian(c.Data.AsSpan(p.Off + 4)), h = BinaryPrimitives.ReadUInt32BigEndian(c.Data.AsSpan(p.Off + 8));
            if (w == 0 || h == 0) throw new InvalidDataException($"Item ID [{item.Id}] has an invalid size [{w}x{h}].");
            if (Limits.TooLarge(w, h)) throw new InvalidDataException($"Item ID [{item.Id}] dimensions are too large [{w}x{h}].");
        }
    }

    public static ImageFrame Decode(byte[] data)
    {
        // libavif AVIF_DECODER_SOURCE_AUTO: an 'avis' major brand (or no 'avif' major brand with tracks present) decodes
        // the image sequence track; the single-image result is its first frame.
        if (IsAvifSequence(data, out var tracks))
        {
            using var seq = DecodeTracks(data, tracks!.Value, maxFrames: 1);
            var first = seq.Frames[0];
            seq.RemoveFrameWithoutDispose(0);
            return first;
        }
        return DecodeCore(data);
    }

    private static bool IsAvifSequence(byte[] data, out (string Major, List<AvifTrack> Tracks)? tracks)
    {
        tracks = null;
        if (data.Length < 12) return false;
        var parsed = AvifTracks.Parse(data, Limits.ImageCountLimit);
        bool anyTrack = parsed.Tracks.Exists(t => t.Samples.Count > 0 && t.Codec == "av01");
        // Honour the major brand ('avis' tracks, 'avif' primary item), otherwise prefer tracks when present.
        if (!anyTrack || parsed.MajorBrand == "avif") return false;
        tracks = parsed;
        return true;
    }

    /// <summary>
    /// Decodes an AVIF image sequence (animated AVIF, 'avis' tracks) to frames with their exact timing
    /// (<see cref="ImageSequence.Timescale"/> / <see cref="ImageFrame.DurationTicks"/>, plus centisecond
    /// <see cref="ImageFrame.Delay"/>) and loop count, as libavif does: the colour track, its 'auxl' alpha track and
    /// 'prem' premultiplication, inter frames decoded in order. A still AVIF yields a one-frame sequence.
    /// </summary>
    public static ImageSequence DecodeSequence(byte[] data)
    {
        if (!CanDecode(data)) throw new InvalidDataException("Not a valid AVIF/HEIC file");
        if (IsAvifSequence(data, out var tracks)) return DecodeTracks(data, tracks!.Value, int.MaxValue);
        var seq = new ImageSequence { FormatName = "AVIF" };
        seq.AddFrame(DecodeCore(data));
        return seq;
    }

    private static (int Cp, int Tc, int Mc, bool Full)? SampleEntryNclx(byte[] d, AvifTrack t)
    {
        foreach (var p in t.Properties)
            if (p.Type == "colr" && p.Len >= 11 && Encoding.ASCII.GetString(d, p.Off, 4) == "nclx")
                return (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 4)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 6)),
                    BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 8)), (d[p.Off + 10] & 0x80) != 0);
        return null;
    }

    private static ImageSequence DecodeTracks(byte[] data, (string Major, List<AvifTrack> Tracks) parsed, int maxFrames)
    {
        var tracks = parsed.Tracks;
        var color = tracks.Find(t => t.Id != 0 && t.Samples.Count > 0 && t.Codec == "av01" && t.AuxForId == 0)
            ?? throw new InvalidDataException("AVIF sequence has no AV1 colour track.");
        AvifTrack? alpha = tracks.Find(t => t.Id != 0 && t.Samples.Count > 0 && t.Codec == "av01" && t.AuxForId == color.Id
            && (t.Property("auxi") is not { } ax || Encoding.ASCII.GetString(data, ax.Off + 4, Math.Max(0, ax.Len - 4)).TrimEnd('\0')
                is "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"));
        bool prem = alpha != null && color.PremById == alpha.Id;
        var nclx = SampleEntryNclx(data, color);
        byte[]? icc = null;
        foreach (var p in color.Properties)
            if (p.Type == "colr" && p.Len > 4 && Encoding.ASCII.GetString(data, p.Off, 4) is "prof" or "rICC")
                icc = data.AsSpan(p.Off + 4, p.Len - 4).ToArray();

        var seq = new ImageSequence
        {
            FormatName = "AVIF", Timescale = color.MediaTimescale,
            CreationTime = FromMp4Time(color.CreationTime), ModificationTime = FromMp4Time(color.ModificationTime),
        };
        int rep = color.RepetitionCount;
        seq.LoopCount = rep < 0 ? 0 : rep + 1;   // plays in total; infinite / unknown -> 0 (loop forever)

        // libavif avifParseMovieBox / avifCodecDecodeInputFillFromSampleTable: track sizes and frame counts are limited.
        foreach (var tr in alpha != null ? new[] { color, alpha } : [color])
        {
            if (Limits.TooLarge(tr.Width, tr.Height)) throw new InvalidDataException($"Track ID [{tr.Id}] dimensions are too large [{tr.Width}x{tr.Height}].");
            if (Limits.ImageCountLimit != 0 && tr.Samples.Count > Limits.ImageCountLimit) throw new InvalidDataException("Exceeded the image count limit.");
        }
        var cDec = NewAv1Decoder();
        var aDec = alpha != null ? NewAv1Decoder() : null;
        int n = Math.Min(maxFrames, color.Samples.Count);
        for (int i = 0; i < n; i++)
        {
            var (off, size) = color.Samples[i];
            using var yuv = cDec.Decode(data.AsSpan((int)off, size), i, isKeyframe: color.Sync[i])
                ?? throw new InvalidDataException($"AVIF sequence frame {i} did not decode. " + FirstLine(Av1.Av1Decoder.LastDecodeError));
            int w = color.Width > 0 ? color.Width : yuv.Width, h = color.Height > 0 ? color.Height : yuv.Height;
            var frame = new ImageFrame();
            frame.Initialize(w, h, ColorspaceType.SRGB, false);
            Av1.DecodedVideoFrame? scaled = null;
            var src = yuv;
            if (yuv.Width != w || yuv.Height != h) src = scaled = ScaleDecodedFrame(yuv, w, h);
            ushort[]? premAlpha = null;
            Av1.DecodedVideoFrame? ayuv = null;
            try
            {
                if (aDec != null && i < alpha!.Samples.Count)
                {
                    var (ao, asz) = alpha.Samples[i];
                    ayuv = aDec.Decode(data.AsSpan((int)ao, asz), i, isKeyframe: alpha.Sync[i]);
                    if (ayuv != null && (ayuv.Width != w || ayuv.Height != h))
                    {
                        var s2 = ScaleDecodedFrame(ayuv, w, h);
                        ayuv.Dispose();
                        ayuv = s2;
                    }
                    if (ayuv != null && prem) premAlpha = NativeAlpha(ayuv, aDec);
                }
                ConvertDecodedAv1(src, cDec, frame, nclx, premAlpha);
                if (ayuv != null) ApplyDecodedAlpha(ayuv, aDec!.FullColorRange, frame, w, h);
            }
            finally
            {
                scaled?.Dispose();
                ayuv?.Dispose();
            }
            if (icc != null)
            {
                frame.IccProfile = icc;
                frame.Metadata.IccProfile = new SharpImage.Metadata.IccProfile(icc);
            }
            long ticks = color.Durations[i];
            frame.DurationTicks = ticks;
            frame.Delay = color.MediaTimescale > 0 ? (int)Math.Round(ticks * 100.0 / color.MediaTimescale) : 0;
            frame.Iterations = seq.LoopCount;
            seq.AddFrame(frame);
        }
        return seq;
    }

    /// <summary>
    /// Progressive / layered AVIF (libavif allowProgressive): one image per layer of the primary item, from the coarsest
    /// preview to the final image, each scaled to the full output size as libavif does. Items without layers ('a1lx'
    /// with no specific 'lsel' layer) give a single image, identical to <see cref="Decode"/>.
    /// </summary>
    public static IReadOnlyList<ImageFrame> DecodeProgressive(byte[] data)
    {
        if (!CanDecode(data)) throw new InvalidDataException("Not a valid AVIF/HEIC file");
        var c = HeifContainer.Parse(data);
        int layers = c.Primary is { } p ? ProgressiveLayerCount(c, p.Type == "grid" ? c.ReferencesFrom(p.Id, "dimg").FirstOrDefault() : p.Id) : 0;
        if (layers <= 1) return [DecodeCore(data)];
        if (Limits.ImageCountLimit != 0 && layers > Limits.ImageCountLimit) throw new InvalidDataException("Exceeded the image count limit (progressive).");
        var result = new List<ImageFrame>(layers);
        for (int i = 0; i < layers; i++)
        {
            t_progressiveLayer = i;
            try { result.Add(DecodeCore(data)); }
            finally { t_progressiveLayer = -1; }
        }
        return result;
    }

    // The layer being produced by DecodeProgressive (-1: the default, final image).
    [ThreadStatic] private static int t_progressiveLayer = -1;

    // When set, high-bit-depth YUV->RGB quantises to the coded depth first ((uint16)(0.5 + v * (2^d - 1)), as libavif's
    // avifImageYUVToRGB does for an RGB image of that depth) and stores that value scaled to 16 bits — so code working
    // on libavif's native-depth RGB (gain map tone mapping) sees exactly its samples.
    [ThreadStatic] private static bool t_nativeDepthRgb;
    // AvifEncodeOptions.AvoidLibyuv for the encode running on this thread.
    [ThreadStatic] private static bool t_avoidLibyuv;

    // libavif avifCodecDecodeInputFillFromDecoderItem: an item is progressive when it has 'a1lx' and no 'lsel' selecting
    // a specific layer; its layer count follows from the a1lx sizes (a zero size ends the list; a remainder is the last).
    private static int ProgressiveLayerCount(HeifContainer c, int id)
    {
        if (c.Property(id, "a1lx") is not { } ax) return 0;
        if (c.Property(id, "lsel") is { } ls && BinaryPrimitives.ReadUInt16BigEndian(c.Data.AsSpan(ls.Off + 0)) != 0xFFFF) return 0;
        var sizes = A1lxSizes(c, id, ax);
        return sizes.Count;
    }

    private static List<long> A1lxSizes(HeifContainer c, int id, (int Off, int Len, bool Essential) ax)
    {
        var d = c.Data.AsSpan(ax.Off, ax.Len);
        bool large = (d[0] & 1) != 0;
        long remaining = c.ItemData(id)?.Length ?? 0;
        var sizes = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            long s = large ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(1 + 4 * i)) : BinaryPrimitives.ReadUInt16BigEndian(d.Slice(1 + 2 * i));
            if (s == 0) { sizes.Add(remaining); remaining = 0; break; }
            if (s >= remaining) throw new InvalidDataException("a1lx layer size does not fit in the item.");
            sizes.Add(s);
            remaining -= s;
        }
        if (remaining > 0) sizes.Add(remaining);
        return sizes;
    }

    private static ImageFrame DecodeCore(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid AVIF/HEIC file");
        }

        var c = HeifContainer.Parse(data);
        CheckItemLimits(c);
        var primary = c.Primary ?? throw new InvalidDataException("AVIF/HEIC has no primary item.");
        int pid = primary.Id;
        // libavif validates the gain map of every file carrying the 'tmap' brand; invalid gain map metadata fails the decode.
        FindGainMap(c, decodeImage: false);

        // ---- a Sample Transform ('sato', e.g. 16-bit depth extension) preferred over the primary: decoded as avifdec does
        ImageFrame frame;
        if (TryDecodeSampleTransform(c, pid) is { } satoFrame)
        {
            frame = satoFrame;
            goto primaryMetadata;
        }

        // ---- colour image: a coded item, or a 'grid' of coded tiles (ISO/IEC 23008-12 6.6.2.3) ----------------------
        var (tiles, rows, cols, outW, outH, codec) = ResolveImageTiles(c, pid);
        var nclx = Nclx(c, pid);

        // Premultiplied alpha ('prem' colour -> alpha): libavif un-premultiplies during/after YUV->RGB. For a single
        // coded colour item the alpha is decoded first so the division happens exactly where libavif does it.
        int premAlphaId = -1;
        foreach (int a in c.ReferencesTo(pid, "auxl"))
            if (IsAlphaAux(c, a) && c.ReferencesFrom(pid, "prem").Contains(a)) { premAlphaId = a; break; }
        ushort[]? premAlpha = null;
        if (premAlphaId >= 0 && tiles.Count == 1 && codec == "av01" && c.Items.TryGetValue(premAlphaId, out var pai) && pai.Type == "av01")
            premAlpha = DecodeAlphaNative(c, premAlphaId);
        else if (premAlphaId >= 0 && tiles.Count > 1 && codec == "av01" && c.Items.TryGetValue(premAlphaId, out var pag) && pag.Type == "grid"
                 && c.ItemData(premAlphaId) is { } agp)
            premAlpha = DecodeAlphaGridNative(c, c.ReferencesFrom(premAlphaId, "dimg"), ParseImageGrid(agp), outW, outH);

        frame = DecodeImageTiles(c, tiles, rows, cols, outW, outH, codec, nclx, premAlpha);
        frame.Depth = ItemBitDepth(c, tiles[0]);

        // ---- alpha: an auxl alpha item (or alpha grid) of the primary; else per-tile alpha items -------------------
        int alphaId = -1;
        foreach (int a in c.ReferencesTo(pid, "auxl")) if (IsAlphaAux(c, a)) { alphaId = a; break; }
        if (alphaId >= 0)
        {
            List<int> aTiles = c.Items.TryGetValue(alphaId, out var ai) && ai.Type == "grid" ? c.ReferencesFrom(alphaId, "dimg") : [alphaId];
            int aCols = cols;
            if (ai?.Type == "grid" && c.ItemData(alphaId) is { } ag) aCols = ParseImageGrid(ag).Cols;
            AddAlphaTiles(c, aTiles, aCols, frame);
        }
        else if (tiles.Count > 1)
        {
            // libavif color_grid_alpha_nogrid: each colour tile carries its own auxl alpha item.
            var perTile = new List<int>();
            foreach (int tile in tiles)
            {
                int found = -1;
                foreach (int a in c.ReferencesTo(tile, "auxl")) if (IsAlphaAux(c, a)) { found = a; break; }
                perTile.Add(found);
            }
            if (!perTile.Contains(-1)) AddAlphaTiles(c, perTile, cols, frame);
        }

        // Premultiplied grids (alpha not available during conversion): un-premultiply the 16-bit result.
        if (premAlphaId >= 0 && premAlpha == null && frame.HasAlpha) UnpremultiplyFrame(frame);

    primaryMetadata:
        // ---- metadata of the primary: ICC, Exif, XMP, pasp; then transforms -----------------------------------------
        if (c.Property(pid, "colr", (o, l) => l > 4 && Encoding.ASCII.GetString(data, o, 4) is "prof" or "rICC") is { } prof)
        {
            byte[] icc = data.AsSpan(prof.Off + 4, prof.Len - 4).ToArray();
            frame.IccProfile = icc;
            frame.Metadata.IccProfile = new SharpImage.Metadata.IccProfile(icc);
        }
        foreach (int m in c.ReferencesTo(pid, "cdsc"))
        {
            if (!c.Items.TryGetValue(m, out var mi) || c.ItemData(m) is not { } payload) continue;
            if (mi.Type == "Exif" && frame.Metadata.ExifProfile == null && payload.Length > 4)
            {
                // unsigned int(32) exif_tiff_header_offset, then the Exif block.
                long tiffOff = 4L + BinaryPrimitives.ReadUInt32BigEndian(payload);
                if (tiffOff < payload.Length) frame.Metadata.ExifProfile = SharpImage.Metadata.ExifParser.ParseFromTiff(payload.AsSpan((int)tiffOff));
            }
            else if (mi.Type == "mime" && mi.ContentType == "application/rdf+xml" && frame.Metadata.Xmp == null)
            {
                frame.Metadata.Xmp = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            }
        }
        if (c.Property(pid, "clli") is { Len: >= 4 } clliP)
            frame.Metadata.ContentLightLevel = new SharpImage.Metadata.ContentLightLevel(
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(clliP.Off)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(clliP.Off + 2)));
        if (c.Property(pid, "mdcv") is { Len: >= 24 } mdcvP)
        {
            ushort U(int k) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(mdcvP.Off + 2 * k));
            frame.Metadata.MasteringDisplay = new SharpImage.Metadata.MasteringDisplayColourVolume(U(0), U(1), U(2), U(3), U(4), U(5), U(6), U(7),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mdcvP.Off + 16)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mdcvP.Off + 20)));
        }
        if (c.Property(pid, "pasp") is { Len: >= 8 } pasp)
        {
            uint h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pasp.Off)), v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pasp.Off + 4));
            if (h > 0 && v > 0) frame.Metadata.PixelAspectRatio = new SharpImage.Metadata.PixelAspectRatio(h, v);
        }
        uint[]? clap = null;
        if (c.Property(pid, "clap") is { Len: >= 32 } cp)
        {
            clap = new uint[8];
            for (int k = 0; k < 8; k++) clap[k] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cp.Off + 4 * k));
            if (Limits.Strict && CropRectFromCleanAperture(clap, (int)frame.Columns, (int)frame.Rows) == null)
                throw new InvalidDataException("[Strict] clap property is invalid.");
        }
        int? irot = c.Property(pid, "irot") is { Len: >= 1 } ir ? data[ir.Off] & 3 : null;
        int? imir = c.Property(pid, "imir") is { Len: >= 1 } im ? data[im.Off] & 1 : null;
        return ApplyHeifTransforms(frame, clap, irot, imir);
    }

    // The coded tiles of an image item: the item itself, or a 'grid' item's dimg tiles (validated like libavif).
    private static (List<int> Tiles, int Rows, int Cols, int OutW, int OutH, string Codec) ResolveImageTiles(HeifContainer c, int id)
    {
        var item = c.Items.GetValueOrDefault(id) ?? throw new InvalidDataException($"Item {id} does not exist.");
        List<int> tiles;
        int rows = 1, cols = 1, outW, outH;
        if (item.Type == "grid")
        {
            (rows, cols, outW, outH) = ParseImageGrid(c.ItemData(id) ?? throw new InvalidDataException("Grid item has no data."));
            tiles = c.ReferencesFrom(id, "dimg");
            if (tiles.Count != rows * cols || new HashSet<int>(tiles).Count != tiles.Count)
                throw new InvalidDataException($"Invalid image grid: {tiles.Count} distinct dimg tiles for a {rows}x{cols} grid.");
        }
        else
        {
            tiles = [id];
            (outW, outH) = c.Ispe(id) ?? (0, 0);
        }

        string codec = c.Items.TryGetValue(tiles[0], out var t0) ? t0.Type : "";
        if (codec is not ("av01" or "hvc1"))
            throw new NotSupportedException($"AVIF/HEIC coded item type '{codec}' is not supported.");
        return (tiles, rows, cols, outW, outH, codec);
    }

    // Decodes the colour of resolved tiles to one RGB frame (AV1 grids stitched in YUV, as libavif does). scaleTo: the
    // YUV image is first rescaled to that size with libyuv's box filter (libavif avifImageScale), e.g. a gain map
    // applied to a larger base image.
    private static ImageFrame DecodeImageTiles(HeifContainer c, List<int> tiles, int rows, int cols, int outW, int outH, string codec,
        (int Cp, int Tc, int Mc, bool Full)? nclx, ushort[]? premAlpha, (int W, int H)? scaleTo = null)
    {
        ImageFrame frame;
        if (codec == "av01" && tiles.Count > 1)
        {
            // AV1 grid: stitch the tiles' YUV planes, then convert once — chroma upsampling crosses tile seams exactly
            // as in libavif (which reassembles the YUV image before avifImageYUVToRGB).
            frame = new ImageFrame();
            var (fw, fh) = scaleTo ?? (outW, outH);
            frame.Initialize(fw, fh, ColorspaceType.SRGB, false);
            DecodeAv1GridInto(c, tiles, cols, outW, outH, nclx, frame, scaleTo, premAlpha);
            return frame;
        }
        if (scaleTo is { } st)
        {
            if (codec != "av01") throw new NotSupportedException("Rescaling is only supported for AV1 items.");
            byte[] coded = c.ItemData(tiles[0]) ?? throw new InvalidDataException($"Item {tiles[0]} has no data.");
            using var yuv = DecodeAv1Item(c, tiles[0], coded, out var dec, "AV1");
            var src = yuv.Width == st.W && yuv.Height == st.H ? yuv : ScaleDecodedFrame(yuv, st.W, st.H);
            frame = new ImageFrame();
            frame.Initialize(st.W, st.H, ColorspaceType.SRGB, false);
            ConvertDecodedAv1(src, dec, frame, Nclx(c, tiles[0]) ?? nclx, premAlpha: null);
            if (!ReferenceEquals(src, yuv)) src.Dispose();
            return frame;
        }
        var tileFrames = new ImageFrame[tiles.Count];
        for (int i = 0; i < tiles.Count; i++)
            tileFrames[i] = DecodeCodedItem(c, tiles[i], codec, nclx, premAlpha);
        int tw = (int)tileFrames[0].Columns, th = (int)tileFrames[0].Rows;
        if (outW <= 0 || outH <= 0) (outW, outH) = (tw * cols, th * rows);
        if (tiles.Count > 1 && (tw * cols < outW || th * rows < outH))
            throw new InvalidDataException("Image grid tiles do not cover the output size.");

        if (tiles.Count == 1 && tw == outW && th == outH) return tileFrames[0];
        frame = new ImageFrame();
        frame.Initialize(outW, outH, ColorspaceType.SRGB, false);
        for (int i = 0; i < tiles.Count; i++) Blit(tileFrames[i], frame, (i % cols) * tw, (i / cols) * th, alphaOnly: false);
        frame.Metadata.Cicp = tileFrames[0].Metadata.Cicp;
        return frame;
    }

    // Decodes an AV1 image grid: every tile to YUV, the planes stitched at the tile offsets (clipped to the grid's
    // output size), then one YUV->RGB conversion of the whole image. Tiles must share size, layout and depth.
    private static void DecodeAv1GridInto(HeifContainer c, List<int> tiles, int cols, int outW, int outH,
        (int Cp, int Tc, int Mc, bool Full)? nclx, ImageFrame frame, (int W, int H)? scaleTo = null, ushort[]? premAlpha = null)
    {
        var yuvs = new List<Av1.DecodedVideoFrame>(tiles.Count);
        try
        {
            Av1.Av1Decoder? first = null;
            foreach (int id in tiles)
            {
                byte[] coded = c.ItemData(id) ?? throw new InvalidDataException($"Grid tile {id} has no data.");
                yuvs.Add(DecodeAv1Item(c, id, coded, out var dec, $"Grid tile {id}"));
                first ??= dec;
            }
            var f0 = yuvs[0];
            int bd = f0.BitDepth, tw = f0.Width, th = f0.Height;
            foreach (var f in yuvs)
                if (f.Width != tw || f.Height != th || f.Format != f0.Format || f.BitDepth != bd)
                    throw new InvalidDataException("Image grid tiles differ in size, layout or depth.");
            if (tw * cols < outW || th * ((tiles.Count + cols - 1) / cols) < outH)
                throw new InvalidDataException("Image grid tiles do not cover the output size.");
            bool mono = first!.Monochrome;
            int ssX = f0.Format is Av1.PixelFormat.Yuv444P or Av1.PixelFormat.Yuv444P10 or Av1.PixelFormat.Yuv444P12 ? 0 : 1;
            int ssY = f0.Format is Av1.PixelFormat.Yuv420P or Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12 ? 1 : 0;
            int cw = (outW + ssX) >> ssX, chh = (outH + ssY) >> ssY;
            int ySize = outW * outH, cSize = cw * chh;
            byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(ySize + 2 * cSize);
            ushort[]? y16 = null, u16 = null, v16 = null;
            if (bd > 8) { y16 = new ushort[ySize]; u16 = new ushort[cSize]; v16 = new ushort[cSize]; }
            for (int i = 0; i < yuvs.Count; i++)
            {
                var f = yuvs[i];
                int x0 = (i % cols) * tw, y0 = (i / cols) * th;
                for (int pl = 0; pl < 3; pl++)
                {
                    if (mono && pl > 0) break;
                    int sx = pl == 0 ? 0 : ssX, sy = pl == 0 ? 0 : ssY;
                    int dW = pl == 0 ? outW : cw, dH = pl == 0 ? outH : chh, dOff = pl == 0 ? 0 : pl == 1 ? ySize : ySize + cSize;
                    int px0 = x0 >> sx, py0 = y0 >> sy, pw = Math.Min((tw + sx) >> sx, dW - px0), ph = Math.Min((th + sy) >> sy, dH - py0);
                    int stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
                    for (int yy = 0; yy < ph; yy++)
                    {
                        if (bd > 8)
                        {
                            var src = (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span.Slice(yy * stride, pw);
                            src.CopyTo((pl == 0 ? y16! : pl == 1 ? u16! : v16!).AsSpan((py0 + yy) * dW + px0, pw));
                        }
                        else
                        {
                            var src = (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span.Slice(yy * stride, pw);
                            src.CopyTo(buf.AsSpan(dOff + (py0 + yy) * dW + px0, pw));
                        }
                    }
                }
            }
            using var stitched = new Av1.DecodedVideoFrame(outW, outH, f0.Format, 0, buf, 0, outW, ySize, cw, ySize + cSize, cw)
            {
                BitDepth = bd,
                YPlane16 = y16 ?? ReadOnlyMemory<ushort>.Empty,
                UPlane16 = u16 ?? ReadOnlyMemory<ushort>.Empty,
                VPlane16 = v16 ?? ReadOnlyMemory<ushort>.Empty,
            };
            var cicp = nclx ?? (first.ColorPrimaries, first.TransferCharacteristics, first.MatrixCoefficients, first.FullColorRange);
            frame.Metadata.Cicp = new SharpImage.Metadata.CicpInfo(cicp.Cp, cicp.Tc, cicp.Mc, cicp.Full);
            if (scaleTo is { } st && (st.W != outW || st.H != outH))
            {
                using var scaled = ScaleDecodedFrame(stitched, st.W, st.H);
                ConvertYuvToRgbLibavif(scaled, frame, st.W, st.H, frame.NumberOfChannels, mono, cicp.Mc, cicp.Full, cicp.Cp, ssX, ssY);
            }
            else ConvertYuvToRgbLibavif(stitched, frame, outW, outH, frame.NumberOfChannels, mono, cicp.Mc, cicp.Full, cicp.Cp, ssX, ssY, premAlpha);
        }
        finally
        {
            foreach (var f in yuvs) f.Dispose();
        }
    }

    private static string FirstLine(string? s) => s == null ? "" : s.Split((char)10)[0];

    // ImageGrid payload: version(8)=0, flags(8) (bit 0: 32-bit output size), rows_minus_one(8), columns_minus_one(8),
    // output_width, output_height.
    private static (int Rows, int Cols, int W, int H) ParseImageGrid(byte[] g)
    {
        if (g.Length < 8 || g[0] != 0) throw new InvalidDataException("Unsupported image grid payload.");
        bool large = (g[1] & 1) != 0;
        if (large && g.Length < 12) throw new InvalidDataException("Truncated image grid payload.");
        long lw = large ? BinaryPrimitives.ReadUInt32BigEndian(g.AsSpan(4)) : BinaryPrimitives.ReadUInt16BigEndian(g.AsSpan(4));
        long lh = large ? BinaryPrimitives.ReadUInt32BigEndian(g.AsSpan(8)) : BinaryPrimitives.ReadUInt16BigEndian(g.AsSpan(6));
        // libavif avifParseImageGridBox: a non-empty output within the limits.
        if (lw == 0 || lh == 0) throw new InvalidDataException("Image grid has an invalid output size.");
        if (Limits.TooLarge(lw, lh)) throw new InvalidDataException($"Grid image dimensions are too large [{lw}x{lh}].");
        int w = (int)lw, h = (int)lh;
        return (g[2] + 1, g[3] + 1, w, h);
    }

    // colr nclx of an item (CICP + range), if any.
    private static (int Cp, int Tc, int Mc, bool Full)? Nclx(HeifContainer c, int id)
    {
        byte[] d = c.Data;
        return c.Property(id, "colr", (o, l) => l >= 11 && Encoding.ASCII.GetString(d, o, 4) == "nclx") is { } p
            ? (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 4)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 6)),
               BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 8)), (d[p.Off + 10] & 0x80) != 0)
            : null;
    }

    // An auxiliary item is alpha when its auxC names the MPEG alpha URN (AVIF) or the HEVC alpha auxid; an auxl item
    // without auxC is treated as alpha too (as old writers produced).
    private static bool IsAlphaAux(HeifContainer c, int id)
    {
        if (c.Property(id, "auxC") is not { } p) return true;
        string urn = Encoding.ASCII.GetString(c.Data, p.Off + 4, Math.Max(0, p.Len - 4)).TrimEnd('\0');
        return urn is "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha" or "urn:mpeg:hevc:2015:auxid:1";
    }

    // Decodes one coded colour item (AV1 or HEVC) to an RGB frame of its coded size. CICP: the item's own colr nclx,
    // else the one inherited from the primary/grid, else (AV1) the sequence header.
    private static ImageFrame DecodeCodedItem(HeifContainer c, int id, string codec, (int Cp, int Tc, int Mc, bool Full)? inherited,
        ushort[]? premAlpha = null)
    {
        byte[] coded = c.ItemData(id) ?? throw new InvalidDataException($"Item {id} has no data.");
        var nclx = Nclx(c, id) ?? inherited;
        int w, h;
        if (c.Ispe(id) is { } ispe) (w, h) = ispe;
        else throw new InvalidDataException($"Item ID [{id}] is missing a mandatory ispe property.");
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        if (codec == "av01")
        {
            DecodeAv1IntraFrame(coded, f, nclx, premAlpha, c, id);
        }
        else
        {
            int matrixCoeffs; bool fullRange;
            if (nclx is { } hn) { matrixCoeffs = hn.Mc; fullRange = hn.Full; }
            else ParseNclxColour(c.Data, out matrixCoeffs, out fullRange);
            byte[] hvcC = c.Property(id, "hvcC") is { } hp ? c.Data.AsSpan(hp.Off, hp.Len).ToArray() : FindConfigBox(c.Data, "hvcC");
            DecodeHevcIntraFrame(coded, f, hvcC, matrixCoeffs, fullRange);
        }
        return f;
    }

    // Decodes alpha tiles (AV1 monochrome items) into the frame's alpha channel, laid out like the colour grid.
    private static void AddAlphaTiles(HeifContainer c, List<int> aTiles, int cols, ImageFrame frame)
    {
        for (int i = 0; i < aTiles.Count; i++)
        {
            byte[]? coded = c.ItemData(aTiles[i]);
            if (coded == null) continue;
            // An alpha item without 'ispe' takes its decoded size, which must be the colour tile's.
            int w = (int)frame.Columns / cols, h = (int)frame.Rows / Math.Max(1, (aTiles.Count + cols - 1) / cols);
            if (c.Ispe(aTiles[i]) is { } ispe) (w, h) = ispe;
            var af = new ImageFrame();
            af.Initialize(w, h, ColorspaceType.SRGB, false);
            ApplyAv1Alpha(coded, af, w, h, c, aTiles[i]);
            if (!frame.HasAlpha) frame.SetAlpha(true);
            Blit(af, frame, (i % cols) * w, (i / cols) * h, alphaOnly: true);
        }
    }

    // Copies a tile into the destination at (dx, dy), clipped to the destination (grid output cropping).
    private static void Blit(ImageFrame src, ImageFrame dst, int dx, int dy, bool alphaOnly)
    {
        int sw = (int)src.Columns, sh = (int)src.Rows, dw = (int)dst.Columns, dh = (int)dst.Rows;
        int w = Math.Min(sw, dw - dx), h = Math.Min(sh, dh - dy);
        if (w <= 0 || h <= 0) return;
        int sc = src.NumberOfChannels, dc = dst.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var s = src.GetPixelRow(y);
            var d = dst.GetPixelRowForWrite(dy + y);
            for (int x = 0; x < w; x++)
            {
                int so = x * sc, dofs = (dx + x) * dc;
                if (alphaOnly) d[dofs + dc - 1] = s[so + sc - 1];
                else for (int k = 0; k < Math.Min(3, sc); k++) d[dofs + k] = s[so + k];
            }
        }
    }

    // HEIF transformative properties are essential: the displayed image is clap-cropped, then rotated (irot, anti-
    // clockwise quarter turns), then mirrored (imir: 0 = top/bottom, 1 = left/right) — MIAF 7.3.6.7 order. The result
    // is display-oriented, so Orientation (and any EXIF orientation tag, which AVIF readers ignore) become top-left.
    private static ImageFrame ApplyHeifTransforms(ImageFrame frame, uint[]? clap, int? irot, int? imir)
    {
        var src = frame;
        if (clap != null && CropRectFromCleanAperture(clap, (int)frame.Columns, (int)frame.Rows) is { } r
            && (r.X != 0 || r.Y != 0 || r.W != frame.Columns || r.H != frame.Rows))
            frame = SharpImage.Transform.Geometry.Crop(frame, r.X, r.Y, r.W, r.H);
        if (irot is 1 or 2 or 3)
            frame = SharpImage.Transform.Geometry.Rotate(frame, irot switch
            {
                1 => SharpImage.Transform.RotationAngle.Rotate270,   // 90° anti-clockwise
                2 => SharpImage.Transform.RotationAngle.Rotate180,
                _ => SharpImage.Transform.RotationAngle.Rotate90,    // 270° anti-clockwise
            });
        if (imir == 0) frame = SharpImage.Transform.Geometry.Flip(frame);
        else if (imir == 1) frame = SharpImage.Transform.Geometry.Flop(frame);

        if (!ReferenceEquals(frame, src))
        {
            frame.Metadata = src.Metadata;
            frame.IccProfile = src.IccProfile;
            frame.Colorspace = src.Colorspace;
        }
        frame.Orientation = OrientationType.TopLeft;
        if (frame.Metadata.ExifProfile is { } exif && exif.GetTag(SharpImage.Metadata.ExifTag.Orientation) is { } ot)
        {
            byte[] one = exif.IsLittleEndian ? [1, 0] : [0, 1];
            exif.SetTag(new SharpImage.Metadata.ExifEntry { Tag = ot.Tag, DataType = SharpImage.Metadata.ExifDataType.Short, Count = 1, Value = one });
        }
        return frame;
    }

    // Finds a codec configuration box (e.g. 'hvcC') in the ISOBMFF stream and returns its
    // payload (the decoder configuration record). Returns an empty array if not present.
    private static byte[] FindConfigBox(byte[] data, string fourcc)
    {
        byte a = (byte)fourcc[0], b = (byte)fourcc[1], c = (byte)fourcc[2], d = (byte)fourcc[3];
        for (int i = 4; i + 4 <= data.Length; i++)
        {
            if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d)
            {
                int boxStart = i - 4;
                int boxSize = (data[boxStart] << 24) | (data[boxStart + 1] << 16) | (data[boxStart + 2] << 8) | data[boxStart + 3];
                int payloadStart = i + 4;
                int payloadLen = boxSize - 8;
                if (payloadLen > 0 && payloadStart + payloadLen <= data.Length)
                {
                    return data[payloadStart..(payloadStart + payloadLen)];
                }
            }
        }

        return [];
    }

    // Reads the colour matrix coefficients + full-range flag from the ISOBMFF 'colr'/'nclx'
    // box so YUV→RGB uses the right matrix. Defaults to BT.601 full range when absent.
    private static void ParseNclxColour(byte[] data, out int matrixCoeffs, out bool fullRange)
    {
        // Defaults when no colour box is present: BT.709, limited (video) range — the
        // convention decoders assume for HEVC/HEIC with unspecified colour.
        matrixCoeffs = 1;
        fullRange = false;
        for (int i = 0; i + 19 < data.Length; i++)
        {
            if (data[i] == 'c' && data[i + 1] == 'o' && data[i + 2] == 'l' && data[i + 3] == 'r'
                && data[i + 4] == 'n' && data[i + 5] == 'c' && data[i + 6] == 'l' && data[i + 7] == 'x')
            {
                // colour_primaries(2) transfer(2) matrix(2) full_range_flag(1 bit, high)
                matrixCoeffs = (data[i + 12] << 8) | data[i + 13];
                fullRange = (data[i + 14] & 0x80) != 0;
                return;
            }
        }
    }

    public static byte[] Encode(ImageFrame image, HeifContainerType containerType = HeifContainerType.Avif)
        => Encode(image, containerType, 20);

    /// <summary>
    /// Encodes an image to HEIC (HEVC) or AVIF (AV1) still image at the given quantization parameter (0 = highest
    /// quality/largest, ~51 = lowest). AVIF uses SharpImage's from-scratch AV1 intra encoder with automatic bit
    /// depth (see <see cref="AvifEncodeOptions.BitDepth"/>); use <see cref="EncodeAvif(ImageFrame, AvifEncodeOptions?)"/>
    /// for full control.
    /// </summary>
    public static byte[] Encode(ImageFrame image, HeifContainerType containerType, int qp)
    {
        if (containerType == HeifContainerType.Avif)
        {
            return EncodeAvif(image, new AvifEncodeOptions { Qp = qp });
        }

        int w = (int)image.Columns;
        int h = (int)image.Rows;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            int ch = image.NumberOfChannels;
            for (int x = 0; x < w; x++)
            {
                int o = x * ch;
                int d = ((y * w) + x) * 3;
                rgb[d] = Quantum.ScaleToByte(row[o]);
                rgb[d + 1] = Quantum.ScaleToByte(ch > 1 ? row[o + 1] : row[o]);
                rgb[d + 2] = Quantum.ScaleToByte(ch > 2 ? row[o + 2] : row[o]);
            }
        }

        return Hevc.HeicEncoder.Encode(rgb, w, h, 3, Math.Clamp(qp, 0, 51), signDataHiding: true);
    }

    // AVIF encode via the from-scratch AV1 intra encoder. Current scope: grayscale, up to one 64x64 superblock.
    /// <summary>Encodes an image as AVIF (AV1 intra) with the given options.</summary>
    public static byte[] EncodeAvif(ImageFrame image, AvifEncodeOptions? options = null)
    {
        options ??= new AvifEncodeOptions();
        using var scope = new EncoderScope(options);
        return EncodeAvifEntry(image, options);
    }

    // The per-call encoder settings of an AvifEncodeOptions, held thread-static for the encode's duration (the
    // encoder's worker threads inherit them): libyuv avoidance, speed preset and thread count.
    private readonly struct EncoderScope : IDisposable
    {
        private readonly bool prevAvoid;
        private readonly Av1.Av1EncodeSpeed? prevSpeed;
        private readonly int prevThreads, prevSharpness;
        public EncoderScope(AvifEncodeOptions options)
        {
            if (options.Sharpness is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(options), "Sharpness must be 0..7.");
            prevSharpness = Av1.Av1ObuWriter.t_sharpness;
            Av1.Av1ObuWriter.t_sharpness = options.Sharpness;
            prevAvoid = t_avoidLibyuv; prevSpeed = Av1.Av1StillImageEncoder.t_speed; prevThreads = Av1.Av1StillImageEncoder.t_threads;
            t_avoidLibyuv = options.AvoidLibyuv;
            Av1.Av1StillImageEncoder.t_speed = Av1.Av1EncodeSpeed.ForSpeed(options.Speed);
            Av1.Av1StillImageEncoder.t_threads = Math.Max(0, options.MaxThreads);
        }
        public void Dispose()
        {
            t_avoidLibyuv = prevAvoid; Av1.Av1StillImageEncoder.t_speed = prevSpeed; Av1.Av1StillImageEncoder.t_threads = prevThreads;
            Av1.Av1ObuWriter.t_sharpness = prevSharpness;
        }
    }

    private static byte[] EncodeAvifEntry(ImageFrame image, AvifEncodeOptions options)
    {
        options = AdoptSourceFormat(image, options);
        using var siting = new Av1.Av1ObuWriter.ChromaPositionScope(SourceChromaPosition(image, options));
        if (options.TargetSize is { } target)
            return SearchTargetSize(options, target, o => EncodeAvif(image, o));
        if (options.BitDepthExtension != AvifBitDepthExtension.None)
        {
            using var satoTiling = Av1.Av1ObuWriter.UseTiling(ResolveTiling(options, (int)image.Columns, (int)image.Rows));
            return EncodeAvifBitDepthExtension(image, options);
        }
        if (options.Grid is { } grid && (grid.Columns != 1 || grid.Rows != 1))
            return EncodeAvifGrid(image, options, grid.Columns, grid.Rows);
        using var tiling = Av1.Av1ObuWriter.UseTiling(ResolveTiling(options, (int)image.Columns, (int)image.Rows));
        if (options.Layers is { Count: > 0 } layers) return EncodeAvifLayers(image, options, layers);
        return EncodeAvifCore(image, options, forceColor: false, forceAlpha: false);
    }

    // avifenc --layered: every layer converted to YUV at full size, area-scaled to its libaom-scaled size, coded with its
    // own q-index (libavif codes layered images with tune=iq), alpha layered alongside.
    private static byte[] EncodeAvifLayers(ImageFrame image, AvifEncodeOptions options, IList<AvifLayer> layers,
        bool forceColor = false, bool forceAlpha = false)
    {
        if (layers.Count is < 2 or > 4) throw new ArgumentException("A layered AVIF has 2 to 4 layers.", nameof(options));
        if (options.Progressive || options.Lossless)
            throw new ArgumentException("Layers cannot be combined with Progressive or Lossless.", nameof(options));
        int w = (int)image.Columns, h = (int)image.Rows;
        var fractions = new (int N, int D)[] { (1, 1), (1, 2), (1, 4), (1, 8), (3, 4), (3, 5), (4, 5) };
        foreach (var l in layers)
        {
            if (l.Image is { } li && (li.Columns != w || li.Rows != h))
                throw new ArgumentException("Every layer image must have the size of the main image.", nameof(options));
            int g = Gcd(l.ScaleNumerator, l.ScaleDenominator);
            if (l.ScaleDenominator <= 0 || g == 0 || Array.IndexOf(fractions, (l.ScaleNumerator / g, l.ScaleDenominator / g)) < 0)
                throw new ArgumentException($"Unsupported scaling mode {l.ScaleNumerator}/{l.ScaleDenominator} (libaom: 1/1, 1/2, 1/4, 1/8, 3/4, 3/5, 4/5).", nameof(options));
        }
        if (layers[^1].ScaleNumerator != layers[^1].ScaleDenominator)
            throw new ArgumentException("The last layer must be coded at full size.", nameof(options));

        int bd = options.BitDepth == 0 ? (HasSubByteDetail(image) ? 10 : 8) : options.BitDepth;
        if (bd is not (8 or 10 or 12)) throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        var color = ResolveAvifColor(image, options, bd);
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0) color = color with { Matrix = 6 };
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1.Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1.Av1PixelLayout.I400,
            _ => color.Matrix == 0 ? Av1.Av1PixelLayout.I444 : Av1.Av1PixelLayout.I420,
        };
        if (color.Matrix == 0 && layout != Av1.Av1PixelLayout.I444)
            throw new ArgumentException("The identity matrix (MatrixCoefficients 0) requires 4:4:4 chroma.", nameof(options));

        var sources = layers.Select(l => l.Image ?? image).ToArray();
        var planes = new List<(double[] R, double[] G, double[] B, ushort[]? A)>();
        bool anyColour = false, anyTranslucent = false;
        foreach (var src in sources)
        {
            ReadRgbPlanes(src, bd, out var r, out var g, out var b, out var a, out bool colour, out bool nonOpaque);
            anyColour |= colour;
            anyTranslucent |= (nonOpaque || forceAlpha) && a != null;
            planes.Add((r, g, b, a));
        }
        anyColour |= forceColor;
        bool mono = layout == Av1.Av1PixelLayout.I400 || (!anyColour && color.Matrix is not (0 or 16 or 17));
        var coded = mono ? Av1.Av1PixelLayout.I400 : layout;
        int ssX = coded is Av1.Av1PixelLayout.I420 or Av1.Av1PixelLayout.I422 ? 1 : 0, ssY = coded == Av1.Av1PixelLayout.I420 ? 1 : 0;
        int defQ = Math.Clamp((int)Math.Round(Math.Clamp(options.Qp, 0, 51) * (255.0 / 51.0)), 4, 255);
        int max = (1 << bd) - 1;
        var inputs = new List<Av1.Av1StillImageEncoder.LayerInput>();
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            var (r, g, b, a) = planes[i];
            ushort[] y;
            ushort[]? u = null, v = null;
            if (mono) y = anyColour ? MonoLumaLibavif(r, g, b, w, h, bd, color, sources[i].Depth is >= 1 and <= 16 ? sources[i].Depth : 16)
                : GreyLumaLibavif(sources[i], bd, color.FullRange, SourceRgbDepth(sources[i]), false);
            else { RgbToYuvLibavif(sources[i], bd, layout, color, SourceRgbDepth(sources[i]), false, r, g, b, out y, out var u0, out var v0); u = u0; v = v0; }
            ushort[]? alpha = anyTranslucent ? a ?? Enumerable.Repeat((ushort)max, w * h).ToArray() : null;
            int lw = (int)(((long)w * l.ScaleNumerator + l.ScaleDenominator - 1) / l.ScaleDenominator);
            int lh = (int)(((long)h * l.ScaleNumerator + l.ScaleDenominator - 1) / l.ScaleDenominator);
            int cw = (w + ssX) >> ssX, chh = (h + ssY) >> ssY, lcw = (lw + ssX) >> ssX, lch = (lh + ssY) >> ssY;
            if (lw != w || lh != h)
            {
                y = AreaScale(y, w, h, lw, lh);
                if (u != null) { u = AreaScale(u, cw, chh, lcw, lch); v = AreaScale(v!, cw, chh, lcw, lch); }
                if (alpha != null) alpha = AreaScale(alpha, w, h, lw, lh);
            }
            int? lq = l.Quality ?? options.Quality;
            int? lqa = l.QualityAlpha ?? l.Quality ?? options.QualityAlpha ?? options.Quality;
            if (lq >= 100 || lqa >= 100) throw new NotSupportedException("Lossless layers are not supported.");
            int qIdx = lq is { } q ? QualityToQIndex(q, color.Matrix == 0) : defQ;
            int aQIdx = lqa is { } qa ? QualityToQIndex(qa, identityMatrix: true) : Math.Clamp(qIdx / 2, 4, 255);
            inputs.Add(new Av1.Av1StillImageEncoder.LayerInput(y, u, v, alpha, lw, lh, qIdx, aQIdx));
        }
        var extras = AvifExtras(image, options);
        extras.Premultiplied = false;
        return Av1.Av1StillImageEncoder.EncodeAvifLayered(inputs, mono, bd, coded, color, extras);
    }

    private static int Gcd(int a, int b) { a = Math.Abs(a); b = Math.Abs(b); while (b != 0) (a, b) = (b, a % b); return a; }

    // Quality / QualityAlpha -> q-index overrides (null = the Qp path); alpha 0 = lossless. libavif picks libaom's tune=iq
    // (and its quantizer table) only for colour in all-intra still images and layered images, not for identity-matrix
    // colour, alpha (tune=psnr) or image sequences (good-quality mode, tune=ssim): those use the linear formula.
    private static (int? QIdx, int? AlphaQIdx, bool Lossless) QualityQIndices(AvifEncodeOptions o, Av1.Av1ObuWriter.Av1ColorDesc color,
        bool sequence = false)
    {
        bool linear = color.Matrix == 0 || sequence;
        int? q = o.Quality is { } c && c < 100 ? QualityToQIndex(c, linear) : null;
        int? qa = (o.QualityAlpha ?? o.Quality) is { } a ? QualityToQIndex(a, identityMatrix: true) : null;
        return (q, qa, o.Quality >= 100);
    }

    // avifenc --target-size: binary search on quality (colour, alpha and gain map unless fixed) for the encode whose
    // size is closest to the target; an exact hit ends the search.
    private static byte[] SearchTargetSize(AvifEncodeOptions options, int target, Func<AvifEncodeOptions, byte[]> encode)
    {
        bool fixColor = options.Quality != null, fixAlpha = options.QualityAlpha != null, fixGainMap = options.QualityGainMap != null || options.GainMap == null;
        if (fixColor && fixAlpha && fixGainMap)
            throw new ArgumentException("TargetSize needs at least one of Quality / QualityAlpha / QualityGainMap left unset.", nameof(options));
        int lo = options.Progressive ? 10 : 0, hi = 100;
        byte[]? closest = null;
        long closestDiff = 0;
        while (lo <= hi)
        {
            int q = (lo + hi) / 2;
            var o = options.Clone();
            o.TargetSize = null;
            if (!fixColor) o.Quality = q;
            if (!fixAlpha) o.QualityAlpha = q;
            if (!fixGainMap) o.QualityGainMap = q;
            byte[] data = encode(o);
            if (data.Length == target) return data;
            long diff;
            if (data.Length > target) { diff = data.Length - (long)target; hi = q - 1; }
            else { diff = target - (long)data.Length; lo = q + 1; }
            if (closest == null || diff < closestDiff) { closest = data; closestDiff = diff; }
        }
        return closest!;
    }

    // libaom av1_quantize.c quantizer_to_qindex.
    private static readonly int[] QuantizerToQIndex =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100,
        104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 148, 152, 156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204,
        208, 212, 216, 220, 224, 228, 232, 236, 240, 244, 249, 255,
    ];

    // libavif codec_aom.c tuneIqQualityToQuantizer (its default tuning with libaom >= 3.13 for non-identity matrices).
    private static readonly int[] TuneIqQualityToQuantizer =
    [
        63, 63, 63, 62, 62, 62, 61, 61, 60, 60, 59, 59, 58, 58, 57, 57, 56, 56, 55, 55, 54, 54, 53, 53, 52, 52, 51, 51, 50, 50,
        49, 49, 48, 48, 47, 46, 46, 45, 45, 44, 43, 43, 42, 42, 41, 40, 40, 39, 39, 38, 37, 37, 36, 36, 35, 34, 33, 33, 32, 31,
        30, 30, 29, 28, 27, 27, 26, 25, 24, 24, 23, 22, 21, 21, 20, 19, 18, 18, 17, 16, 15, 15, 14, 13, 12, 12, 11, 10, 9, 9,
        8, 7, 6, 6, 5, 4, 3, 3, 2, 1, 0,
    ];

    // Quality 0..99 -> base q-index (libavif aomQualityToQuantizer, then libaom's quantizer table); 100 = lossless (0).
    internal static int QualityToQIndex(int quality, bool identityMatrix)
    {
        quality = Math.Clamp(quality, 0, 100);
        if (quality == 100) return 0;
        int quantizer = identityMatrix ? ((100 - quality) * 63 + 50) / 100 : TuneIqQualityToQuantizer[quality];
        return Math.Max(QuantizerToQIndex[quantizer], 1);
    }

    // libavif: tile log2 values clamped to 0..6; autoTiling = avifSetTileConfiguration(8 threads, first cell size).
    private static (int Cols, int Rows) ResolveTiling(AvifEncodeOptions o, int w, int h)
    {
        // Null: the fast presets (speed 6+) tile automatically unless tile counts were given; slower speeds keep one
        // tile (tiles cost ~0.3-0.5% but encode and decode in parallel).
        bool auto = o.AutoTiling ?? (o.Speed >= 6 && o.TileColumnsLog2 == 0 && o.TileRowsLog2 == 0);
        if (!auto) return (Math.Clamp(o.TileColumnsLog2, 0, 6), Math.Clamp(o.TileRowsLog2, 0, 6));
        const int threads = 8;
        const long minTileArea = 512 * 512, maxTiles = 32;
        long tiles = Math.Min(((long)w * h + minTileArea - 1) / minTileArea, maxTiles);
        tiles = Math.Min(tiles, threads);
        int tilesLog2 = 0;
        while ((2L << tilesLog2) <= tiles) tilesLog2++;   // floorLog2
        static (int D1, int D2) Split(long dim1, long dim2, int tilesLog2)
        {
            long ratio = dim1 / dim2;
            int diffLog2 = 0;
            while ((2L << diffLog2) <= ratio) diffLog2++;
            int subtract = Math.Max(tilesLog2 - diffLog2, 0);
            int d2 = subtract / 2;
            return (tilesLog2 - d2, d2);
        }
        if (w >= h) { var (c, r) = Split(w, h, tilesLog2); return (c, r); }
        var (rr, cc) = Split(h, w, tilesLog2);
        return (cc, rr);
    }

    // forceColor / forceAlpha: grid cells must share one chroma format and all carry alpha (MIAF), so a grey or opaque
    // cell of a colour / translucent image is still coded in colour / with an alpha item.
    private static byte[] EncodeAvifCore(ImageFrame image, AvifEncodeOptions options, bool forceColor, bool forceAlpha)
    {
        int bd = options.BitDepth == 0 ? (HasSubByteDetail(image) ? 10 : 8) : options.BitDepth;
        if (bd is not (8 or 10 or 12))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        }

        var color = ResolveAvifColor(image, options, bd);
        if (options.Lossless && options.MatrixCoefficients == null)
            color = color with { Matrix = 0 };   // identity (GBR) at 4:4:4 — exact RGB
        // 4:0:0 cannot use the identity matrix: avifenc resets it to BT.601.
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0)
            color = color with { Matrix = 6 };
        // The identity matrix (RGB coded as GBR) is only defined for 4:4:4 — Auto picks it, explicit subsampling fails.
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1.Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1.Av1PixelLayout.I400,
            // Auto: 4:4:4 for identity and for lossless (subsampled chroma cannot be lossless; avifenc --lossless), else 4:2:0.
            _ => color.Matrix == 0 || options.Lossless ? Av1.Av1PixelLayout.I444 : Av1.Av1PixelLayout.I420,
        };
        if (color.Matrix == 0 && layout != Av1.Av1PixelLayout.I444)
            throw new ArgumentException("The identity matrix (MatrixCoefficients 0) requires 4:4:4 chroma.", nameof(options));

        // 8-bit 4:2:0 BT.601 full range keeps the original byte pipeline (EncodeAvif8); everything else goes through
        // the general matrix/range/layout path.
        bool bt601Full = color.Matrix is 5 or 6 && color.FullRange;
        var extras = AvifExtras(image, options);
        extras.Premultiplied = options.PremultiplyAlpha && image.HasAlpha;
        if (options.GainMap != null) extras.GainMap = BuildGainMapItem(options.GainMap, options, extras);
        bool denoise = options.DenoiseNoiseLevel > 0;
        if ((options.FilmGrain != null || denoise) && options.Lossless)
            throw new ArgumentException("Film grain / denoising cannot be combined with lossless coding.", nameof(options));
        if (options.FilmGrain != null && denoise)
            throw new ArgumentException("Set either FilmGrain or DenoiseNoiseLevel, not both.", nameof(options));
        if (options.Progressive && options.Lossless)
            throw new ArgumentException("Progressive (layered) encoding cannot be combined with lossless coding.", nameof(options));
        if (denoise && options.DenoiseBlockSize is not (8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(options), "DenoiseBlockSize must be 8, 16 or 32.");
        var grain = new AvifGrainRequest(options.FilmGrain, denoise, options.DenoiseUseRequestedLevel ? options.DenoiseNoiseLevel / 10.0f : -1,
            options.DenoiseBlockSize, options.DenoiseApply);
        // Quality (libavif scale) overrides Qp; quality 100 = lossless AV1 coding (matrix unchanged).
        var (qIdx, aQIdx, qualityLossless) = QualityQIndices(options, color);
        return bd == 8 && layout == Av1.Av1PixelLayout.I420 && bt601Full && !options.Lossless && !extras.Premultiplied
               && options.FilmGrain == null && !denoise && !options.Progressive && !forceColor && !forceAlpha && qIdx == null && aQIdx == null
               && !qualityLossless && !options.SharpYuv && !SourcePlanes.TryGetValue(image, out _)
            ? EncodeAvif8(image, options.Qp, color, extras)
            : EncodeAvifGeneral(image, options.Qp, bd, layout, color, extras, options.Lossless || qualityLossless, grain,
                options.Progressive && image.Columns >= 16 && image.Rows >= 16,   // a sub-8px base layer is pointless
                forceColor: forceColor, forceAlpha: forceAlpha, qIdxOverride: qIdx, alphaQIdxOverride: aQIdx, sharpYuv: options.SharpYuv);
    }

    /// <summary>
    /// Encodes an image sequence as an animated AVIF ('avis', libavif layout): an AV1 colour track (and an alpha track
    /// linked by 'auxl' when any frame is not opaque), with the first frame also stored as the primary image items so
    /// still-image readers show it. Every frame is coded as a key frame with the still-image encoder at the options'
    /// quality, depth, subsampling, CICP and lossless / film-grain settings. Timing comes from
    /// <see cref="ImageSequence.Timescale"/> and <see cref="ImageFrame.DurationTicks"/> when both are set, else from
    /// <see cref="ImageFrame.Delay"/> (centiseconds; 0 plays as 10); <see cref="ImageSequence.LoopCount"/> 0 loops
    /// forever, n plays n times. All frames must have the same size; colour properties, ICC and metadata come from
    /// the first frame.
    /// </summary>
    public static byte[] EncodeAvifSequence(ImageSequence sequence, AvifEncodeOptions? options = null)
    {
        options ??= new AvifEncodeOptions();
        using var scope = new EncoderScope(options);
        return EncodeAvifSequenceEntry(sequence, options);
    }

    // ISO BMFF times: seconds since 1904-01-01 UTC (0 = unset).
    private const long Mp4EpochOffset = 2082844800;   // 1904-01-01 -> 1970-01-01
    private static ulong ToMp4Time(DateTimeOffset? t) =>
        t is { } v ? (ulong)Math.Max(0, v.ToUnixTimeSeconds() + Mp4EpochOffset) : 0;
    private static DateTimeOffset? FromMp4Time(ulong t) =>
        t == 0 || t > long.MaxValue / 2 ? null : DateTimeOffset.FromUnixTimeSeconds((long)t - Mp4EpochOffset);

    private static byte[] EncodeAvifSequenceEntry(ImageSequence sequence, AvifEncodeOptions options)
    {
        if (sequence.Count > 0) options = AdoptSourceFormat(sequence[0], options);
        using var siting = new Av1.Av1ObuWriter.ChromaPositionScope(sequence.Count > 0 ? SourceChromaPosition(sequence[0], options) : 0);
        if (options.TargetSize is { } target)
            return SearchTargetSize(options, target, o => EncodeAvifSequence(sequence, o));
        if (sequence.Count == 0) throw new ArgumentException("The sequence has no frames.", nameof(sequence));
        if (options.Progressive) throw new NotSupportedException("Progressive (layered) coding applies to still AVIF images.");
        var first = sequence[0];
        int w = (int)first.Columns, h = (int)first.Rows;
        foreach (var f in sequence.Frames)
            if ((int)f.Columns != w || (int)f.Rows != h)
                throw new ArgumentException("Every frame of an AVIF sequence must have the same size.", nameof(sequence));
        if (w > 65536 || h > 65536 || w < 1 || h < 1)
            throw new NotSupportedException($"AVIF encoding supports 1..65536 per dimension (got {w}x{h}).");

        int bd = options.BitDepth == 0 ? (sequence.Frames.Any(HasSubByteDetail) ? 10 : 8) : options.BitDepth;
        if (bd is not (8 or 10 or 12))
            throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        var color = ResolveAvifColor(first, options, bd);
        if (options.Lossless && options.MatrixCoefficients == null) color = color with { Matrix = 0 };
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0) color = color with { Matrix = 6 };
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1.Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1.Av1PixelLayout.I400,
            _ => color.Matrix == 0 || options.Lossless ? Av1.Av1PixelLayout.I444 : Av1.Av1PixelLayout.I420,
        };
        using var tiling = Av1.Av1ObuWriter.UseTiling(ResolveTiling(options, w, h));
        if (color.Matrix == 0 && layout != Av1.Av1PixelLayout.I444)
            throw new ArgumentException("The identity matrix (MatrixCoefficients 0) requires 4:4:4 chroma.", nameof(options));
        bool denoise = options.DenoiseNoiseLevel > 0;
        if ((options.FilmGrain != null || denoise) && options.Lossless)
            throw new ArgumentException("Film grain / denoising cannot be combined with lossless coding.", nameof(options));
        if (options.FilmGrain != null && denoise)
            throw new ArgumentException("Set either FilmGrain or DenoiseNoiseLevel, not both.", nameof(options));
        if (denoise && options.DenoiseBlockSize is not (8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(options), "DenoiseBlockSize must be 8, 16 or 32.");

        var extras = AvifExtras(first, options);
        var frames = new List<(double[] R, double[] G, double[] B, ushort[]? A)>();
        bool anyColour = false, anyTranslucent = false;
        foreach (var f in sequence.Frames)
        {
            ReadRgbPlanes(f, bd, out var r, out var g, out var b, out var a, out bool colour, out bool nonOpaque);
            anyColour |= colour;
            anyTranslucent |= nonOpaque;
            frames.Add((r, g, b, a));
        }
        bool hasAlpha = anyTranslucent;
        extras.Premultiplied = options.PremultiplyAlpha && hasAlpha;
        // Grey sequences are coded 4:0:0 like stills; identity / YCgCo-R keep the colour path (they carry exact RGB).
        bool forceMono = layout == Av1.Av1PixelLayout.I400;   // avifenc -y 400: luma from RGB, chroma dropped
        bool mono = forceMono || (!anyColour && !(!options.Lossless && color.Matrix is 0 or 16 or 17));
        var codedLayout = mono ? Av1.Av1PixelLayout.I400 : layout;
        var (qIdxQ, aQIdxQ, qualityLossless) = QualityQIndices(options, color, sequence: true);
        bool lossless = options.Lossless || qualityLossless;
        int baseQIdx = qIdxQ ?? Math.Clamp((int)Math.Round(Math.Clamp(options.Qp, 0, 51) * (255.0 / 51.0)), 4, 255);
        int alphaQIdx = aQIdxQ ?? (lossless ? 0 : Math.Clamp(baseQIdx / 2, 4, 255));
        int gssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, gssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        int max = (1 << bd) - 1;

        var sq = new Av1.AvifSequenceData
        {
            ColorSamples = [], AlphaSamples = hasAlpha ? [] : null, ColorSync = [], AlphaSync = hasAlpha ? [] : null,
        };
        if (options.KeyframeInterval < 0) throw new ArgumentOutOfRangeException(nameof(options), "KeyframeInterval must be >= 0.");

        // Every frame's coded planes first: the group coder looks ahead (a hidden ALTREF codes a later frame early).
        var coded = new List<(ushort[] Y, ushort[]? U, ushort[]? V, ushort[]? A, AvifFilmGrain? Grain)>();
        int frameIndex = 0;
        foreach (var (r, g, b, a0) in frames)
        {
            var frameImage = sequence.Frames[frameIndex++];
            ushort[]? alpha = hasAlpha ? a0 ?? Enumerable.Repeat((ushort)max, w * h).ToArray() : null;
            if (extras.Premultiplied && alpha != null)
                for (int i = 0; i < r.Length; i++)
                {
                    double k = alpha[i] / (double)max;
                    if (k < 1) { r[i] *= k; g[i] *= k; b[i] *= k; }
                }
            ushort[] yP;
            ushort[]? uP = null, vP = null;
            if (mono && forceMono && anyColour)
                yP = MonoLumaLibavif(r, g, b, w, h, bd, color, first.Depth is >= 1 and <= 16 ? first.Depth : 16);
            else if (mono)
                yP = GreyLumaLibavif(frameImage, bd, color.FullRange, SourceRgbDepth(frameImage), extras.Premultiplied && alpha != null);
            else if (SourcePlanesFor(frameImage, bd, color, w, h) is { } sp && sp.Layout == layout && !(extras.Premultiplied && alpha != null))
            {
                yP = sp.Planes.Value[0].ToArray(); uP = sp.Planes.Value[1].ToArray(); vP = sp.Planes.Value[2].ToArray();   // kept Y4M planes
            }
            else
            {
                RgbToYuvLibavif(frameImage, bd, layout, color, SourceRgbDepth(frameImage), extras.Premultiplied && alpha != null, r, g, b,
                    out yP, out var u0, out var v0);
                uP = u0;
                vP = v0;
            }

            AvifFilmGrain? grain = options.FilmGrain;
            if (denoise)
            {
                float level = options.DenoiseUseRequestedLevel ? options.DenoiseNoiseLevel / 10.0f
                    : Av1.Av1NoiseModel.AllIntraNoiseLevel(yP, w, h, bd);
                grain = Av1.Av1NoiseModel.DenoiseAndModel(yP, uP, vP, w, h, mono ? 1 : gssX, mono ? 1 : gssY, bd, level,
                    options.DenoiseBlockSize, out var den);
                if (grain != null && options.DenoiseApply)
                {
                    yP = den[0]!;
                    uP = den[1];
                    vP = den[2];
                }
            }
            coded.Add((yP, uP, vP, alpha, grain));
        }
        frames.Clear();

        int n = coded.Count;
        var cSamples = new byte[n][];
        var cSync = new bool[n];
        // The decoder model: inter frames predict from exactly what a decoder holds in its reference slots (the
        // reconstruction before film grain synthesis).
        var colorDec = new Av1.Av1Decoder { ApplyFilmGrain = false };
        var noCdef = new Av1.Av1ObuWriter.CdefParams(3, 0, [0], [0]);
        byte[] td = [0x12, 0x00];   // temporal delimiter: every sample is one complete temporal unit (as libavif writes)
        var ls = new Av1.Av1ObuWriter.LayeredStream
        {
            Layers = 1, Sequence = true, MaxWidth = w, MaxHeight = h, Widths = [w], Heights = [h],
        };
        int lf = InterLoopFilterLevel(baseQIdx, bd);
        int arfQIdx = BoostedQIdx(baseQIdx, bd, SequenceArfQuantizerScale);
        int midQIdx = BoostedQIdx(baseQIdx, bd, SequenceMidArfQuantizerScale);
        using (Av1.Av1ObuWriter.UseLayers(ls))
        {
            int lastKeyBytes = 0, sinceKey = 0, lastSlot = 0, goldenSlot = 0;

            Av1.Av1ObuWriter.FilmGrainScope Grain(int i)
                => Av1.Av1ObuWriter.UseFilmGrain(coded[i].Grain?.ToAv1(mono, mono ? 1 : gssX, mono ? 1 : gssY), !mono && gssX == 1 && gssY == 1);

            (byte[] cSeq, byte[] cFrame) KeyObus(int i)
            {
                var (yP, uP, vP, _, _) = coded[i];
                using (Grain(i))
                    return lossless
                        ? Av1.Av1StillImageEncoder.BuildLosslessObus(yP, uP, vP, mono, w, h, bd, codedLayout,
                            mono && color.Matrix is 16 or 17 ? color with { Matrix = 0 } : color)
                        : mono ? Av1.Av1StillImageEncoder.BuildMonochromeObus(yP, w, h, KeyFrameQIdx(baseQIdx, bd), bd, color)
                        : Av1.Av1StillImageEncoder.BuildColorObus(yP, uP!, vP!, w, h, KeyFrameQIdx(baseQIdx, bd), bd, layout, color);
            }

            void Key(int i)
            {
                var cObus = KeyObus(i);
                cSamples[i] = [.. td, .. cObus.cSeq, .. cObus.cFrame];
                cSync[i] = true;
                lastKeyBytes = cObus.cFrame.Length;
                sinceKey = 1;
                lastSlot = goldenSlot = 0;   // a key frame refreshes every slot
                Decode(cSamples[i], true);
            }

            void Decode(byte[] tu, bool key)
            {
                Av1.Av1Decoder.LastDecodeError = null;
                using var f = colorDec.Decode(tu, 0, key);
                if (Av1.Av1Decoder.LastDecodeError is { } err) throw new InvalidOperationException("Reference decode failed: " + err);
            }

            // One inter frame of source frame i reading LAST / GOLDEN / ALTREF from the given slots (ALTREF -1: none),
            // replacing the slots in refresh; hidden frames stay showable.
            byte[] Inter(int i, int last, int golden, int arf, int refresh, bool show, int qIdx, int bwd = -1)
            {
                var pics = new Dictionary<int, Av1.Av1InterEncoder.Picture>();
                Av1.Av1InterEncoder.Picture Slot(int sl)
                {
                    if (!pics.TryGetValue(sl, out var pic)) pics[sl] = pic = colorDec.ReferencePicture(sl, mono, gssX, gssY);
                    return pic;
                }
                var refs = new Av1.Av1InterEncoder.Picture?[7];
                refs[0] = Slot(last);
                if (golden != last) refs[3] = Slot(golden);
                if (arf >= 0 && arf != last && arf != golden) refs[6] = Slot(arf);
                if (bwd >= 0 && bwd != last && bwd != golden && bwd != arf) refs[4] = Slot(bwd);
                int alt = arf >= 0 ? arf : golden;
                ls.RefFrameIdx = [last, last, last, golden, bwd >= 0 ? bwd : alt, alt, alt];
                ls.RefreshFlags = refresh;
                ls.ShowFrame = show;
                ls.ReferenceSelect = SequenceCompound && (refs[6] != null || refs[4] != null);   // compound (past + future)
                ls.InterFrame = true;
                try
                {
                    var (yP, uP, vP, _, _) = coded[i];
                    using (Grain(i))
                    {
                        var build = Av1.Av1InterEncoder.EncodeFrameVariants(new Av1.Av1InterEncoder.Picture { Y = yP, U = uP, V = vP, Width = w, Height = h },
                            refs, bd, codedLayout, qIdx, colorDec.SequenceIntraTools);
                        if (!SequenceFilterSearch || qIdx == 0) return build(lf, noCdef);   // lossless: no filters
                        // Deblocking level, then CDEF strength, by decoding each variant into the refreshed slot and
                        // measuring it against the source (libaom's full-image loop filter / CDEF search); the slot
                        // is restored after every trial.
                        int slot = System.Numerics.BitOperations.TrailingZeroCount(refresh);
                        long Trial(int lfv, Av1.Av1ObuWriter.CdefParams cd)
                        {
                            var snap = colorDec.SnapshotSlot(slot);
                            Decode([.. td, .. build(lfv, cd)], false);
                            var r = colorDec.ReferencePicture(slot, mono, gssX, gssY);
                            long sse = PlaneSse(r.Y, yP) + (uP != null ? PlaneSse(r.U!, uP) + PlaneSse(r.V!, vP!) : 0);
                            colorDec.RestoreSlot(slot, snap);
                            return sse;
                        }
                        int g = InterLoopFilterLevel(qIdx, bd), bestLf = 0;
                        long best = long.MaxValue;
                        foreach (int cand in new[] { 0, g / 2, g, Math.Min(63, g * 3 / 2) }.Distinct())
                        {
                            long e = Trial(cand, noCdef);
                            if (e < best) { best = e; bestLf = cand; }
                        }
                        var bestCdef = noCdef;
                        foreach (byte st in new byte[] { (1 << 2) | 0, (2 << 2) | 1, (4 << 2) | 1, (8 << 2) | 2 })
                        {
                            var cd = new Av1.Av1ObuWriter.CdefParams(3, 0, [st], [st]);
                            long e = Trial(bestLf, cd);
                            if (e < best) { best = e; bestCdef = cd; }
                        }
                        return build(bestLf, bestCdef);
                    }
                }
                finally
                {
                    ls.InterFrame = false;
                    ls.ShowFrame = true;
                    ls.ReferenceSelect = false;
                    ls.RefreshFlags = 1;
                    ls.RefFrameIdx = new int[7];
                }
            }

            // A slot other than the ones given (8 slots; at most four are live).
            static int FreeSlot(int a, int b, int c = -1)
            {
                for (int sl = 0; ; sl++) if (sl != a && sl != b && sl != c) return sl;
            }

            // A shown inter frame from LAST / GOLDEN (/ ALTREF); false when it is a scene cut (about as large as a key
            // frame) — the caller codes a key frame instead.
            bool Shown(int i, int arf, byte[]? prefix, int bwd = -1)
            {
                int ws = FreeSlot(goldenSlot, arf >= 0 ? arf : goldenSlot, bwd);
                byte[] obu = Inter(i, lastSlot, goldenSlot, arf, 1 << ws, true, lossless ? 0 : baseQIdx, bwd);
                // A scene cut (key frame, as libaom's scene-change detection): among frames well above a normal inter size
                // (60% of the last key frame), either the inter frame costs over 3/4 of this frame coded as a key frame, or
                // the source jumps (a source-SAD test after the best global shift: this frame matches the previous one over
                // 3x worse than that one matched its predecessor, by a clear margin). The source test keeps the rule
                // independent of how the key encoder trades bits for quality.
                if (obu.Length > lastKeyBytes * 6 / 10 && (SourceJump(i) || obu.Length > KeyObus(i).cFrame.Length * 3 / 4)) return false;
                Decode([.. td, .. obu], false);
                lastSlot = ws;
                cSamples[i] = prefix == null ? [.. td, .. obu] : [.. td, .. prefix, .. obu];
                sinceKey++;
                return true;
            }

            // Mean absolute luma difference of source frame a from frame b after the best global shift (+-16 pixels), on
            // a sparse grid of about 4096 interior samples, in 8-bit units: ~0 for a pan, large across a cut.
            double SourceDiff(int a, int b)
            {
                ushort[] ya = coded[a].Y, yb = coded[b].Y;
                const int r = 16;
                int x0 = Math.Min(r, w / 4), y0 = Math.Min(r, h / 4), x1 = w - x0, y1 = h - y0;
                if (x1 <= x0 || y1 <= y0) return 0;
                int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 4096));
                long best = long.MaxValue; int cnt = 0;
                for (int dy = -y0; dy <= y0; dy++)
                    for (int dx = -x0; dx <= x0; dx++)
                    {
                        long sum = 0; int c = 0;
                        for (int y = y0; y < y1 && sum < best; y += step)
                            for (int x = x0; x < x1; x += step) { sum += Math.Abs(ya[y * w + x] - yb[(y + dy) * w + x + dx]); c++; }
                        if (sum < best) { best = sum; cnt = c; }
                    }
                return (double)best / Math.Max(1, cnt) / (1 << (bd - 8));
            }
            bool SourceJump(int i) => i >= 2 && SourceDiff(i, i - 1) is var d && d > 8 && d > 3 * SourceDiff(i - 1, i - 2);

            int fi = 0;
            while (fi < n)
            {
                if (fi == 0 || (options.KeyframeInterval > 0 && sinceKey >= options.KeyframeInterval))
                {
                    Key(fi++);
                    continue;
                }
                // Frames until the next forced key frame, grouped like libaom's GF groups: the group's last frame is
                // coded first as a hidden ALTREF at a lower q-index (predicted from the past), the frames before it
                // predict from LAST, GOLDEN and that ALTREF, and the ALTREF is then shown as is (show_existing_frame).
                int limit = n - fi;
                if (options.KeyframeInterval > 0) limit = Math.Min(limit, options.KeyframeInterval - sinceKey);
                int gf = Math.Min(limit, SequenceMaxGfInterval);
                // Lossless sequences are plain P-frame chains (a hidden ALTREF would only add bits).
                if (gf >= SequenceMinArfGroup && !lossless)
                {
                    int arfIdx = fi + gf - 1;
                    int arfSlot = FreeSlot(lastSlot, goldenSlot);
                    byte[] arf = Inter(arfIdx, lastSlot, goldenSlot, -1, 1 << arfSlot, false, arfQIdx);
                    if (arf.Length <= lastKeyBytes * 9 / 10)
                    {
                        Decode([.. td, .. arf], false);
                        // Pyramid (libaom's multi-layer GF group): a second hidden frame in the middle, predicted from
                        // the past and the ALTREF, serves as BWDREF for the first half and is shown in place.
                        int midIdx = -1, midSlot = -1;
                        byte[] prefix = arf;
                        if (gf >= SequencePyramidMinGroup)
                        {
                            midIdx = fi + (gf - 1) / 2;
                            midSlot = FreeSlot(lastSlot, goldenSlot, arfSlot);
                            byte[] mid = Inter(midIdx, lastSlot, goldenSlot, arfSlot, 1 << midSlot, false, midQIdx);
                            Decode([.. td, .. mid], false);
                            prefix = [.. arf, .. mid];
                        }
                        bool cut = false;
                        for (int k = fi; k < arfIdx && !cut; k++)
                        {
                            if (k == midIdx)
                            {
                                cSamples[k] = [.. td, .. Av1.Av1ObuWriter.ShowExistingFrameObu(midSlot)];
                                lastSlot = midSlot;
                                sinceKey++;
                                continue;
                            }
                            if (!Shown(k, arfSlot, k == fi ? prefix : null, k < midIdx ? midSlot : -1))
                            {
                                Key(k);
                                fi = k + 1;
                                cut = true;
                            }
                        }
                        if (cut) continue;
                        cSamples[arfIdx] = [.. td, .. Av1.Av1ObuWriter.ShowExistingFrameObu(arfSlot)];
                        lastSlot = goldenSlot = arfSlot;
                        sinceKey++;
                        fi = arfIdx + 1;
                        continue;
                    }
                    // The group's last frame is a new scene: code frame by frame (a cut becomes a key frame).
                }
                if (!Shown(fi, -1, null)) Key(fi);
                fi++;
            }

            sq.ColorSamples.AddRange(cSamples);
            sq.ColorSync!.AddRange(cSync);

            if (hasAlpha)
            {
                // The alpha track keeps the colour track's key frames; the others are inter frames from the previous one.
                var alphaDec = new Av1.Av1Decoder { ApplyFilmGrain = false };
                for (int i = 0; i < n; i++)
                {
                    var alpha = coded[i].A!;
                    byte[] aSample;
                    if (cSync[i])
                    {
                        var aObus = Av1.Av1StillImageEncoder.BuildAlphaObus(alpha, w, h, lossless ? alphaQIdx : KeyFrameQIdx(alphaQIdx, bd), bd);
                        aSample = [.. td, .. aObus.SeqObu, .. aObus.FrameObu];
                    }
                    else
                    {
                        ls.InterFrame = true;
                        try
                        {
                            aSample = [.. td, .. Av1.Av1InterEncoder.EncodeFrameObu(new Av1.Av1InterEncoder.Picture { Y = alpha, Width = w, Height = h },
                                alphaDec.ReferencePicture(0, true, 1, 1), bd, Av1.Av1PixelLayout.I400, alphaQIdx, noCdef,
                                InterLoopFilterLevel(alphaQIdx, bd), alphaDec.SequenceIntraTools)];
                        }
                        finally { ls.InterFrame = false; }
                    }
                    sq.AlphaSamples!.Add(aSample);
                    sq.AlphaSync!.Add(cSync[i]);
                    {
                        using var f = alphaDec.Decode(aSample, 0, cSync[i]);
                        if (f == null) throw new InvalidOperationException("Reference decode failed: " + Av1.Av1Decoder.LastDecodeError);
                    }
                }
            }
        }

        // Timing (libavif: mdhd/mvhd timescale, per-sample stts durations, elst repetition).
        bool exact = sequence.Timescale > 0 && sequence.Timescale <= uint.MaxValue
                     && sequence.Frames.All(f => f.DurationTicks > 0 && f.DurationTicks <= uint.MaxValue);
        sq.Timescale = exact ? (uint)sequence.Timescale : 100;
        sq.Durations = sequence.Frames.Select(f => exact ? (uint)f.DurationTicks : (uint)(f.Delay > 0 ? f.Delay : 10)).ToArray();
        sq.RepetitionCount = sequence.LoopCount <= 0 ? -1 : sequence.LoopCount - 1;
        sq.CreationTime = ToMp4Time(options.CreationTime ?? sequence.CreationTime);
        sq.ModificationTime = ToMp4Time(options.ModificationTime ?? sequence.ModificationTime);
        extras.Sequence = sq;

        var c0 = sq.ColorSamples[0];
        return hasAlpha
            ? Av1.Av1AvifWriter.BuildAvifWithAlpha(c0, [], sq.AlphaSamples![0], [], w, h, mono, bd, layout, color, extras)
            : Av1.Av1AvifWriter.BuildAvif(c0, [], w, h, mono, bd, layout, color, extras);
    }

    // Key frames of a sequence get a lower q-index than the cq level the inter frames use, as libaom's good-quality
    // rate control gives them (its lookahead key-frame boost): measured on libavif sequences at qualities 30-80 its key
    // frames sit at the q-index whose quantizer is ~0.43x the cq level's (av1_compute_qdelta: the smallest q-index
    // reaching each q, q = ac_quant / 4, 16, 64 for 8, 10, 12 bits) — cq 52/76/100/128/152 -> 19/29/42/69/101
    // against libaom's 19/30/43/69/100.
    internal static int KeyFrameQIdx(int cqIdx, int bd) => BoostedQIdx(cqIdx, bd, 0.43);

    // Sequence GF groups (libaom good-quality defaults: min / max GF interval 4 / 16) and the hidden ALTREF's
    // quantizer relative to the cq level (lower than the inter frames, higher than a key frame).
    internal static int SequenceMaxGfInterval = 16, SequenceMinArfGroup = 4;
    internal static double SequenceArfQuantizerScale = 0.6;
    internal static bool SequenceCompound = true;
    internal static bool SequenceFilterSearch = true;
    // Pyramid: groups of at least this many frames also code a hidden middle frame (BWDREF) at this quantizer scale.
    internal static int SequencePyramidMinGroup = 6;
    internal static double SequenceMidArfQuantizerScale = 0.75;

    // The q-index whose quantizer is `scale` x the cq level's (av1_compute_qdelta).
    internal static int BoostedQIdx(int cqIdx, int bd, double scale0)
    {
        if (cqIdx == 0) return 0;
        int bdIdx = bd == 8 ? 0 : bd == 10 ? 1 : 2;
        double scale = bd == 8 ? 4.0 : bd == 10 ? 16.0 : 64.0;
        int FindQIdx(double q)
        {
            for (int i = 0; i < 255; i++) if (Av1.Av1Tables.DequantTable[bdIdx, i, 1] / scale >= q) return i;
            return 255;
        }
        double qv = Av1.Av1Tables.DequantTable[bdIdx, cqIdx, 1] / scale;
        return Math.Clamp(cqIdx + FindQIdx(qv * scale0) - FindQIdx(qv), 1, 255);
    }

    private static long PlaneSse(ushort[] a, ushort[] b)
    {
        long s = 0;
        for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; s += (long)d * d; }
        return s;
    }

    // libaom picklpf.c LPF_PICK_FROM_Q for inter frames: the deblocking level guessed from the AC quantizer.
    private static int InterLoopFilterLevel(int qIdx, int bd)
    {
        int q = Av1.Av1Tables.DequantTable[bd == 8 ? 0 : bd == 10 ? 1 : 2, Math.Clamp(qIdx, 0, 255), 1];
        long guess = bd switch
        {
            8 => ((long)q * 6017 + 650707 + (1 << 17)) >> 18,
            10 => ((long)q * 20723 + 4060632 + (1 << 19)) >> 20,
            _ => ((long)q * 20723 + 16242526 + (1 << 21)) >> 22,
        };
        return (int)Math.Clamp(guess, 0, 63);
    }

    // The frame's samples as RGB in coded-depth units [0, 2^bd - 1] plus the alpha plane (null without an alpha
    // channel), whether any pixel has colour, and whether any pixel is not fully opaque.
    private static void ReadRgbPlanes(ImageFrame image, int bd, out double[] r, out double[] g, out double[] b, out ushort[]? alpha,
        out bool colour, out bool nonOpaque)
    {
        int w = (int)image.Columns, h = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int alphaOff = channels - 1;
        double scale = ((1 << bd) - 1) / 65535.0;
        r = new double[w * h];
        g = new double[w * h];
        b = new double[w * h];
        alpha = image.HasAlpha ? new ushort[w * h] : null;
        colour = false;
        nonOpaque = false;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * channels;
                ushort r16 = row[o];
                ushort g16 = channels >= 3 ? row[o + 1] : r16;
                ushort b16 = channels >= 3 ? row[o + 2] : r16;
                if (r16 != g16 || g16 != b16) colour = true;
                int i = y * w + x;
                r[i] = r16 * scale;
                g[i] = g16 * scale;
                b[i] = b16 * scale;
                if (alpha != null)
                {
                    ushort a16 = row[o + alphaOff];
                    alpha[i] = (ushort)Math.Round(a16 * scale);
                    if (a16 != ushort.MaxValue) nonOpaque = true;
                }
            }
        }
    }

    // Container content carried over from the image: the ICC profile (colr 'prof'), Exif (as raw TIFF, the
    // form libavif stores after its 4-byte header offset) and XMP (mime item, application/rdf+xml).
    // Transforms: irot/imir from the options, else from the EXIF-style Orientation (libavif
    // avifImageExtractExifOrientationToIrotImir); clap from CropRect; pasp from the options or metadata.
    private static Av1.AvifContainerExtras AvifExtras(ImageFrame image, AvifEncodeOptions o)
    {
        var x = new Av1.AvifContainerExtras
        {
            Icc = image.Metadata.IccProfile?.Data ?? image.IccProfile,
            Exif = image.Metadata.ExifProfile is { } exif ? SharpImage.Metadata.ExifParser.SerializeForPngExif(exif) : null,
            Xmp = image.Metadata.Xmp is { Length: > 0 } xmp ? Encoding.UTF8.GetBytes(xmp) : null,
        };
        (int? irot, int? imir) = image.Orientation switch
        {
            OrientationType.TopRight => ((int?)null, (int?)1),
            OrientationType.BottomRight => (2, null),
            OrientationType.BottomLeft => (null, 0),
            OrientationType.LeftTop => (1, 0),
            OrientationType.RightTop => (3, null),
            OrientationType.RightBottom => (3, 0),
            OrientationType.LeftBottom => (1, null),
            _ => (null, null),
        };
        if (o.Rotation != null || o.Mirror != null) (irot, imir) = (o.Rotation, o.Mirror);
        if (irot is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(o), "Rotation must be 0..3 quarter turns.");
        if (imir is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(o), "Mirror axis must be 0 or 1.");
        x.IrotAngle = irot is > 0 ? irot : null;
        x.ImirAxis = imir;
        if (o.CropRect is { } crop)
            x.Clap = CleanApertureFromCropRect(crop.X, crop.Y, crop.Width, crop.Height, (int)image.Columns, (int)image.Rows);
        if ((o.PixelAspectRatio ?? image.Metadata.PixelAspectRatio) is { } pa)
            x.Pasp = (pa.HorizontalSpacing, pa.VerticalSpacing);
        if ((o.ContentLightLevel ?? image.Metadata.ContentLightLevel) is { } cll)
            x.Clli = (cll.MaxContentLightLevel, cll.MaxFrameAverageLightLevel);
        if ((o.MasteringDisplay ?? image.Metadata.MasteringDisplay) is { } md)
        {
            var b = new byte[24];
            ushort[] p16 = [md.GreenX, md.GreenY, md.BlueX, md.BlueY, md.RedX, md.RedY, md.WhitePointX, md.WhitePointY];
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2 * i), p16[i]);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), md.MaxLuminance);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), md.MinLuminance);
            x.Mdcv = b;
        }
        return x;
    }

    // libavif avifCleanApertureBoxFromCropRect: clap size = crop size (/1); offsets = crop centre - image centre as
    // reduced fractions (centres are dim/2, so offsets are multiples of 1/2).
    private static uint[] CleanApertureFromCropRect(int x, int y, int w, int h, int imageW, int imageH)
    {
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > imageW || y + h > imageH)
            throw new ArgumentOutOfRangeException(nameof(x), "CropRect must be non-empty and inside the image.");
        (long hn, long hd) = Reduce(2L * x + w - imageW, 2);
        (long vn, long vd) = Reduce(2L * y + h - imageH, 2);
        return [(uint)w, 1, (uint)h, 1, (uint)(int)hn, (uint)hd, (uint)(int)vn, (uint)vd];
    }

    private static (long N, long D) Reduce(long n, long d)
    {
        long a = Math.Abs(n), b = d;
        while (b != 0) (a, b) = (b, a % b);
        return a > 1 ? (n / a, d / a) : (n, d);
    }

    // libavif avifCropRectFromCleanApertureBox: null when the clap is invalid or not integral (then ignored).
    private static (int X, int Y, int W, int H)? CropRectFromCleanAperture(uint[] c, int imageW, int imageH)
    {
        long wN = (int)c[0], wD = (int)c[1], hN = (int)c[2], hD = (int)c[3], xN = (int)c[4], xD = (int)c[5], yN = (int)c[6], yD = (int)c[7];
        if (wD <= 0 || hD <= 0 || xD <= 0 || yD <= 0 || wN < 0 || hN < 0 || wN % wD != 0 || hN % hD != 0) return null;
        long cw = wN / wD, ch = hN / hD;
        // cropX = imageW/2 + horizOff - cw/2, computed over the common denominator 2 * xD.
        long xNum = (long)imageW * xD + 2 * xN - cw * xD, xDen = 2 * xD;
        long yNum = (long)imageH * yD + 2 * yN - ch * yD, yDen = 2 * yD;
        if (xNum % xDen != 0 || yNum % yDen != 0) return null;
        long cx = xNum / xDen, cy = yNum / yDen;
        if (cx < 0 || cy < 0 || cw == 0 || ch == 0 || cx + cw > imageW || cy + ch > imageH) return null;
        return ((int)cx, (int)cy, (int)cw, (int)ch);
    }

    // CICP for an AVIF encode, validated against what libavif can represent (reformat.c avifGetYUVColorSpaceInfo).
    private static Av1.Av1ObuWriter.Av1ColorDesc ResolveAvifColor(ImageFrame image, AvifEncodeOptions o, int bd)
    {
        var meta = image.Metadata.Cicp;
        bool hasIcc = image.IccProfile != null || image.Metadata.IccProfile != null;
        int cp = o.ColorPrimaries ?? meta?.ColorPrimaries ?? (hasIcc ? 2 : 1);
        int tc = o.TransferCharacteristics ?? meta?.TransferCharacteristics ?? (hasIcc ? 2 : 13);
        int mc = o.MatrixCoefficients ?? (meta?.MatrixCoefficients is 1 or 4 or 5 or 6 or 7 or 9 or 12 ? meta.MatrixCoefficients : 6);
        if (cp is < 0 or > 255 || tc is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(o), "CICP primaries/transfer must be 0..255.");
        if (mc is not (0 or 1 or 2 or 4 or 5 or 6 or 7 or 8 or 9 or 12 or 16 or 17))
            throw new NotSupportedException($"AVIF matrix coefficients {mc} are not supported (as in libavif: reserved, BT.2020 CL, SMPTE 2085, chroma-derived CL and ICtCp have no RGB<->YUV mapping here).");
        if (mc is 8 or 16 or 17 && !o.FullRange)
            throw new NotSupportedException("YCgCo matrices require full range (as in libavif).");
        if (mc is 16 or 17 && bd - (mc == 16 ? 2 : 1) < 8)
            throw new ArgumentException($"YCgCo-{(mc == 16 ? "Re" : "Ro")} codes {(mc == 16 ? 2 : 1)} bit(s) above the RGB depth — use BitDepth 10 or 12.");
        return new Av1.Av1ObuWriter.Av1ColorDesc(cp, tc, mc, o.FullRange);
    }

    // True when any sample carries more than 8 bits of precision (an 8-bit-origin sample is exactly v8 * 257).
    private static bool HasSubByteDetail(ImageFrame image)
    {
        int w = (int)image.Columns, h = (int)image.Rows, n = w * image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int i = 0; i < n; i++)
            {
                if (row[i] % 257 != 0) return true;
            }
        }

        return false;
    }

    // General AVIF encode (any bit depth, any chroma layout): samples are taken straight from the 16-bit quantum at
    // full precision and coded through the multi-superblock encoder (which handles every size 8..4096). The 8-bit
    // 4:2:0 case keeps its original byte path (EncodeAvif8) so its output is unchanged.
    private static byte[] EncodeAvifGeneral(ImageFrame image, int qp, int bd, Av1.Av1PixelLayout layout,
        Av1.Av1ObuWriter.Av1ColorDesc color, Av1.AvifContainerExtras extras, bool lossless = false, AvifGrainRequest? request = null,
        bool progressive = false, (int W, int H)? scaleYuvTo = null, bool libavifFloatYuv = false, bool forceColor = false,
        bool forceAlpha = false, int? qIdxOverride = null, int? alphaQIdxOverride = null, bool sharpYuv = false)
    {
        // Film grain rides on the colour stream only: the ambient scope is read by the colour builders' headers and
        // suppressed around the alpha builds. Denoising (libaom aom_denoise_and_model_run) replaces the colour planes
        // and supplies the estimated grain.
        int gssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, gssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        AvifFilmGrain? grain = request?.Explicit;
        Av1.Av1ObuWriter.FilmGrainScope Grain(bool mono) =>
            Av1.Av1ObuWriter.UseFilmGrain(grain?.ToAv1(mono, mono ? 1 : gssX, mono ? 1 : gssY), !mono && gssX == 1 && gssY == 1);
        void Denoise(ref ushort[] yPl, ref ushort[]? uPl, ref ushort[]? vPl, int ssX, int ssY)
        {
            if (request is not { Denoise: true } r) return;
            float level = r.Level >= 0 ? r.Level : Av1.Av1NoiseModel.AllIntraNoiseLevel(yPl, (int)image.Columns, (int)image.Rows, bd);
            grain = Av1.Av1NoiseModel.DenoiseAndModel(yPl, uPl, vPl, (int)image.Columns, (int)image.Rows, ssX, ssY, bd, level,
                r.BlockSize, out var den);
            if (grain != null && r.Apply)
            {
                yPl = den[0]!;
                uPl = den[1];
                vPl = den[2];
            }
        }
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        // libavif avifImageScale after RGB->YUV (a gain map's --downscaling): planes rescaled with libyuv's box filter.
        void ScaleYuv(ref ushort[] yPl, ref ushort[]? uPl, ref ushort[]? vPl, int ssX, int ssY)
        {
            if (scaleYuvTo is not { } st || (st.W == w && st.H == h)) return;
            int scw = (w + ssX) >> ssX, sch = (h + ssY) >> ssY, dcw = (st.W + ssX) >> ssX, dch = (st.H + ssY) >> ssY;
            yPl = LibyuvScale.ScalePlane(yPl, w, w, h, st.W, st.H, bd > 8);
            if (uPl != null) uPl = LibyuvScale.ScalePlane(uPl, scw, scw, sch, dcw, dch, bd > 8);
            if (vPl != null) vPl = LibyuvScale.ScalePlane(vPl, scw, scw, sch, dcw, dch, bd > 8);
            (w, h) = st;
        }
        if (w > 65536 || h > 65536 || w < 1 || h < 1)
        {
            throw new NotSupportedException($"AVIF encoding supports 1..65536 per dimension (got {w}x{h}).");
        }

        bool hasAlpha = image.HasAlpha;
        ReadRgbPlanes(image, bd, out var r, out var g, out var b, out var alpha, out bool colour, out bool nonOpaque);
        colour |= forceColor;
        nonOpaque |= forceAlpha && alpha != null;
        // Source YUV planes (JPEG input, a decoded gain map): coded as they are when the layout matches (a grey-looking
        // colour JPEG stays YCbCr, as in libavif); 4:0:0 takes the luma of any source.
        var srcYuv = SourcePlanesFor(image, bd, color, w, h);
        if (hasAlpha && nonOpaque && srcYuv?.Alpha == null) srcYuv = null;   // RGBA sources go through RGB -> YUV
        if (srcYuv != null && srcYuv.Layout != Av1.Av1PixelLayout.I400 && layout != Av1.Av1PixelLayout.I400)
        {
            if (srcYuv.Layout == layout) colour = true;
            else srcYuv = null;
        }

        int baseQIdx = qIdxOverride ?? Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);
        if (progressive && alphaQIdxOverride == 0 && hasAlpha)
            throw new NotSupportedException("Progressive (layered) encoding with lossless alpha is not supported.");
        if (extras.Premultiplied && hasAlpha && nonOpaque && alpha != null)
        {
            double amax = (1 << bd) - 1;
            for (int i = 0; i < r.Length; i++)
            {
                double a = alpha[i] / amax;
                if (a < 1) { r[i] *= a; g[i] *= a; b[i] *= a; }
            }
        }
        else extras.Premultiplied = false;   // nothing to signal without a coded alpha item
        // 4:0:0 requested for a colour source (avifenc -y 400): luma from RGB with the matrix, chroma dropped.
        bool forceMono = layout == Av1.Av1PixelLayout.I400;
        ushort[]? monoLuma = null;
        if (forceMono && srcYuv != null)
        {
            monoLuma = srcYuv.Planes.Value[0].ToArray();
            colour = false;
        }
        else if (forceMono && colour)
        {
            monoLuma = MonoLumaLibavif(r, g, b, w, h, bd, color, image.Depth is >= 1 and <= 16 ? image.Depth : 16);
            colour = false;
        }
        if (hasAlpha && nonOpaque && alpha != null && forceMono)
        {
            int alphaQIdx = alphaQIdxOverride ?? Math.Clamp(baseQIdx / 2, 4, 255);
            ushort[] yM = monoLuma ?? GreyLumaLibavif(image, bd, color.FullRange, SourceRgbDepth(image), extras.Premultiplied);
            if (lossless)
                return Av1.Av1StillImageEncoder.EncodeAvifLossless(yM, default, default, true, alpha, true, w, h, bd, Av1.Av1PixelLayout.I400,
                    color.Matrix is 16 or 17 ? color with { Matrix = 0 } : color, extras, alphaQIdxOverride ?? 0);
            ushort[]? mU = null, mV = null;
            Denoise(ref yM, ref mU, ref mV, 1, 1);
            using (Grain(true))
                return progressive
                    ? Av1.Av1StillImageEncoder.EncodeAvifLayered(ProgressiveLayers(yM, null, null, alpha, w, h, 1, 1, baseQIdx, alphaQIdx),
                        true, bd, Av1.Av1PixelLayout.I400, color, extras)
                    : Av1.Av1StillImageEncoder.EncodeAvifMonochromeWithAlpha(yM, alpha, w, h, baseQIdx, alphaQIdx, bd, color, extras);
        }
        if (hasAlpha && nonOpaque && alpha != null)
        {
            int alphaQIdx = alphaQIdxOverride ?? Math.Clamp(baseQIdx / 2, 4, 255);
            ushort[] yA, uA0, vA0;
            if (srcYuv?.Alpha != null && srcYuv.Layout == layout && !extras.Premultiplied)
            {
                yA = srcYuv.Planes.Value[0].ToArray(); uA0 = srcYuv.Planes.Value[1].ToArray(); vA0 = srcYuv.Planes.Value[2].ToArray();
                alpha = srcYuv.Alpha;
            }
            else if (!(sharpYuv && TrySharpYuv(image, r, g, b, w, h, bd, layout, color, out yA, out uA0, out vA0)))
                RgbToYuvLibavif(image, bd, layout, color, SourceRgbDepth(image), extras.Premultiplied, r, g, b, out yA, out uA0, out vA0);
            ushort[]? uA = uA0, vA = vA0;
            Denoise(ref yA, ref uA, ref vA, gssX, gssY);
            if (lossless)
                return Av1.Av1StillImageEncoder.EncodeAvifLossless(yA, uA!, vA!, false, alpha, true, w, h, bd, layout, color, extras,
                    alphaQIdxOverride ?? 0);
            using (Grain(false))
                return progressive
                    ? Av1.Av1StillImageEncoder.EncodeAvifLayered(ProgressiveLayers(yA, uA, vA, alpha, w, h, gssX, gssY, baseQIdx, alphaQIdx),
                        false, bd, layout, color, extras)
                    : Av1.Av1StillImageEncoder.EncodeAvifColorWithAlpha(yA, uA!, vA!, alpha, w, h, baseQIdx, alphaQIdx, bd, layout, color, extras);
        }

        // Identity / YCgCo-R carry exact RGB, so lossy grey content keeps the colour (4:4:4) path; lossless grey is
        // coded as 4:0:0 like libavif (the single plane is exact already).
        if (!forceMono && (colour || (!lossless && color.Matrix is 0 or 16 or 17)))
        {
            ushort[] yP, uP0, vP0;
            if (srcYuv != null) { yP = srcYuv.Planes.Value[0].ToArray(); uP0 = srcYuv.Planes.Value[1].ToArray(); vP0 = srcYuv.Planes.Value[2].ToArray(); }
            else if (sharpYuv && TrySharpYuv(image, r, g, b, w, h, bd, layout, color, out yP, out uP0, out vP0)) { }
            else if (libavifFloatYuv && color.Matrix is not (8 or 16 or 17))
                RgbToYuvAvifFloat(r, g, b, w, h, bd, layout, color, out yP, out uP0, out vP0, image.Depth is >= 1 and <= 16 ? image.Depth : 16);
            else
                RgbToYuvLibavif(image, bd, layout, color, SourceRgbDepth(image), extras.Premultiplied, r, g, b, out yP, out uP0, out vP0);
            ushort[]? uP = uP0, vP = vP0;
            ScaleYuv(ref yP, ref uP, ref vP, gssX, gssY);
            Denoise(ref yP, ref uP, ref vP, gssX, gssY);
            if (lossless)
                return Av1.Av1StillImageEncoder.EncodeAvifLossless(yP, uP!, vP!, false, default, false, w, h, bd, layout, color, extras);
            using (Grain(false))
                return progressive
                    ? Av1.Av1StillImageEncoder.EncodeAvifLayered(ProgressiveLayers(yP, uP, vP, null, w, h, gssX, gssY, baseQIdx, 0),
                        false, bd, layout, color, extras)
                    : Av1.Av1StillImageEncoder.EncodeAvifColorMultiSb(yP, uP!, vP!, w, h, baseQIdx, bd, layout, color, extras);
        }

        // Grey: 4:0:0 luma (Y = the grey value; limited range maps it into [16, 235] << (bd - 8)).
        var luma = monoLuma ?? (srcYuv != null ? srcYuv.Planes.Value[0].ToArray()
            : GreyLumaLibavif(image, bd, color.FullRange, SourceRgbDepth(image), extras.Premultiplied));
        {
            ushort[]? su = null, sv = null;
            ScaleYuv(ref luma, ref su, ref sv, 1, 1);
        }
        if (lossless)
            return Av1.Av1StillImageEncoder.EncodeAvifLossless(luma, default, default, true, default, false, w, h, bd, Av1.Av1PixelLayout.I400,
                color.Matrix is 16 or 17 ? color with { Matrix = 0 } : color, extras);
        ushort[]? noU = null, noV = null;
        Denoise(ref luma, ref noU, ref noV, 1, 1);
        using (Grain(true))
            return progressive
                ? Av1.Av1StillImageEncoder.EncodeAvifLayered(ProgressiveLayers(luma, null, null, null, w, h, 1, 1, baseQIdx, 0),
                    true, bd, Av1.Av1PixelLayout.I400, color, extras)
                : Av1.Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(luma, w, h, baseQIdx, bd, color, extras);
    }

    // avifenc --progressive: layer 0 = the image scaled by 1/2 (ceil, like libaom's AOME_ONETWO scale mode) at quality
    // 10 (libavif quantizer 57 -> aom qindex 228), alpha at its own quality; layer 1 = the full image. The base layer is
    // area-averaged per plane (encoder-side, non-normative, as libaom's resizer is).
    private static List<Av1.Av1StillImageEncoder.LayerInput> ProgressiveLayers(ushort[] y, ushort[]? u, ushort[]? v,
        ushort[]? alpha, int w, int h, int ssX, int ssY, int qIdx, int alphaQIdx)
    {
        const int baseQIdx = 228;
        int w0 = (w + 1) / 2, h0 = (h + 1) / 2;
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY, cw0 = (w0 + ssX) >> ssX, ch0 = (h0 + ssY) >> ssY;
        var layer0 = new Av1.Av1StillImageEncoder.LayerInput(
            AreaScale(y, w, h, w0, h0),
            u == null ? null : AreaScale(u, cw, ch, cw0, ch0),
            v == null ? null : AreaScale(v, cw, ch, cw0, ch0),
            alpha == null ? null : AreaScale(alpha, w, h, w0, h0),
            w0, h0, Math.Max(qIdx, baseQIdx), alphaQIdx);
        var layer1 = new Av1.Av1StillImageEncoder.LayerInput(y, u, v, alpha, w, h, qIdx, alphaQIdx);
        return [layer0, layer1];
    }

    // Area-average resampling of one plane (each output sample is the coverage-weighted mean of the source samples
    // under it), rounded to the nearest code.
    private static ushort[] AreaScale(ushort[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new ushort[dw * dh];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int j = 0; j < dh; j++)
        {
            double y0 = j * fy, y1 = y0 + fy;
            for (int i = 0; i < dw; i++)
            {
                double x0 = i * fx, x1 = x0 + fx, sum = 0, wsum = 0;
                for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                    {
                        double wgt = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                        sum += src[sy * sw + sx] * wgt;
                        wsum += wgt;
                    }
                }
                dst[j * dw + i] = (ushort)Math.Round(sum / wsum);
            }
        }
        return dst;
    }

    // RGB -> Y'CbCr for AVIF at any depth / layout / CICP matrix / range, following libavif's avifImageRGBToYUV:
    // normalised RGB, per-pixel Y (and U/V for 4:4:4), chroma = mean of each (possibly partial) subsampling group in
    // the normalised domain, then unorm = round(v * range + bias). Identity codes G/B/R with the luma range; YCgCo
    // (8) uses H.273 eqs 44-46; YCgCo-Re/Ro (16/17) are the integer lifting transforms on RGB quantised to
    // bd-2 / bd-1 bits. r/g/b arrive in coded-depth units [0, 2^bd - 1].
    // Colour source -> 4:0:0 luma exactly as libavif's avifImageRGBToYUV: libyuv's J400 ((77r + 150g + 29b + 128) >> 8,
    // verified against the libyuv binary) for 8-bit full-range BT.601 / BT.470BG, else the float path's Y.
    private static ushort[] MonoLumaLibavif(double[] r, double[] g, double[] b, int w, int h, int bd, Av1.Av1ObuWriter.Av1ColorDesc color,
        int srcDepth)
    {
        // libavif's libyuv path needs 8-bit RGB: an 8-bit source (every 16-bit sample a multiple of 257).
        bool eightBitSource = srcDepth == 8;
        if (!eightBitSource && bd == 8)
        {
            eightBitSource = true;
            for (int i = 0; i < r.Length && eightBitSource; i++)
                eightBitSource = Math.Abs(r[i] - Math.Round(r[i])) < 1e-6 && Math.Abs(g[i] - Math.Round(g[i])) < 1e-6 && Math.Abs(b[i] - Math.Round(b[i])) < 1e-6;
        }
        if (bd == 8 && eightBitSource && color.Matrix is 5 or 6 && color.FullRange)
        {
            var y = new ushort[w * h];
            for (int i = 0; i < y.Length; i++)
                y[i] = (ushort)((77 * (int)Math.Round(r[i]) + 150 * (int)Math.Round(g[i]) + 29 * (int)Math.Round(b[i]) + 128) >> 8);
            return y;
        }
        RgbToYuvAvifFloat(r, g, b, w, h, bd, Av1.Av1PixelLayout.I444, color, out var yf, out _, out _, srcDepth);
        return yf;
    }

    // avifenc --sharpyuv: libsharpyuv for 4:2:0 with kr/kb coefficients, fed the RGB at the source's depth (8-bit when
    // every sample is 8-bit representable, as avifenc reads such a PNG; else the frame's depth, 16 when unknown).
    private static bool TrySharpYuv(ImageFrame image, double[] r, double[] g, double[] b, int w, int h, int bd, Av1.Av1PixelLayout layout,
        Av1.Av1ObuWriter.Av1ColorDesc color, out ushort[] y, out ushort[] u, out ushort[] v)
    {
        y = u = v = [];
        if (layout != Av1.Av1PixelLayout.I420 || color.Matrix is 0 or 8 or 16 or 17) return false;
        int max = (1 << bd) - 1;
        var r16 = new ushort[r.Length];
        var g16 = new ushort[r.Length];
        var b16 = new ushort[r.Length];
        bool eightBit = true;
        for (int i = 0; i < r.Length; i++)
        {
            r16[i] = (ushort)Math.Round(r[i] * 65535.0 / max);
            g16[i] = (ushort)Math.Round(g[i] * 65535.0 / max);
            b16[i] = (ushort)Math.Round(b[i] * 65535.0 / max);
            eightBit &= r16[i] % 257 == 0 && g16[i] % 257 == 0 && b16[i] % 257 == 0;
        }
        int srcDepth = image.Depth is 8 or 10 or 12 ? image.Depth : eightBit ? 8 : 16;
        if (srcDepth < 16)
        {
            uint smax = (1u << srcDepth) - 1;
            for (int i = 0; i < r16.Length; i++)
            {
                r16[i] = (ushort)((r16[i] * smax + 32767) / 65535);
                g16[i] = (ushort)((g16[i] * smax + 32767) / 65535);
                b16[i] = (ushort)((b16[i] * smax + 32767) / 65535);
            }
        }
        (float kr, float kb) = color.Matrix == 12 ? ChromaDerivedKrKb(color.Primaries) : MatrixKrKb(color.Matrix);
        SharpImage.Formats.SharpYuv.Convert(r16, g16, b16, w, h, srcDepth, bd, kr, kb, color.FullRange, color.Transfer, out y, out u, out v);
        return true;
    }

    // Grey source -> 4:0:0 luma (Y = the grey value; limited range maps it into [16, 235] << (bd - 8)).
    private static ushort[] GreyLuma(double[] r, int w, int h, int bd, Av1.Av1ObuWriter.Av1ColorDesc color)
    {
        int max = (1 << bd) - 1;
        var luma = new ushort[w * h];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = (ushort)Math.Clamp((int)Math.Round(color.FullRange ? r[i] : r[i] / max * (219 << (bd - 8)) + (16 << (bd - 8))), 0, max);
        return luma;
    }

    // libavif avifImageRGBToYUV's built-in path operation for operation (float, 2x2 blocks, chroma averaged in block
    // order, avifRoundf), for the kr/kb matrices and identity. The RGB samples are taken at the source's depth
    // (srcDepth: ImageFrame.Depth, 16 when unknown — exact for 8-bit sources too), as libavif's avifRGBImage holds them,
    // and normalised by that depth's maximum. Used where the coded YUV must equal libavif's exactly (lossless gain maps,
    // 4:0:0 from colour).
    private static void RgbToYuvAvifFloat(double[] r, double[] g, double[] b, int w, int h, int bd, Av1.Av1PixelLayout layout,
        Av1.Av1ObuWriter.Av1ColorDesc color, out ushort[] y, out ushort[] u, out ushort[] v, int srcDepth = 16)
    {
        int max = (1 << bd) - 1;
        int srcMax = (1 << Math.Clamp(srcDepth, 1, 16)) - 1;
        float srcMaxF = srcMax;
        float Src(double x)
        {
            int u16 = (int)Math.Round(x * 65535.0 / max);
            int q = srcMax == 65535 ? u16 : (int)((u16 * (long)srcMax + 32767) / 65535);
            return q / srcMaxF;
        }
        bool full = color.FullRange;
        float biasY = full ? 0.0f : 16 << (bd - 8), rangeY = full ? max : 219 << (bd - 8);
        float biasUV = 1 << (bd - 1), rangeUV = full ? max : 224 << (bd - 8);
        bool identity = color.Matrix == 0;
        (float kr, float kb) = color.Matrix == 12 ? ChromaDerivedKrKb(color.Primaries) : MatrixKrKb(color.Matrix);
        float kg = 1.0f - kr - kb;
        int ssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        int cw = (w + ssX) >> ssX, chh = (h + ssY) >> ssY;
        y = new ushort[w * h];
        u = new ushort[cw * chh];
        v = new ushort[cw * chh];
        int ToY(float x) => Math.Clamp((int)MathF.Floor(x * rangeY + biasY + 0.5f), 0, max);
        int ToUV(float x) => Math.Clamp((int)MathF.Floor(identity ? x * rangeY + biasY + 0.5f : x * rangeUV + biasUV + 0.5f), 0, max);
        Span<float> bu = stackalloc float[4], bv = stackalloc float[4];
        for (int oj = 0; oj < h; oj += 2)
            for (int oi = 0; oi < w; oi += 2)
            {
                int bw = oi + 1 >= w ? 1 : 2, bh = oj + 1 >= h ? 1 : 2;
                for (int bj = 0; bj < bh; bj++)
                    for (int bi = 0; bi < bw; bi++)
                    {
                        int i = (oj + bj) * w + oi + bi;
                        float R = Src(r[i]), G = Src(g[i]), B = Src(b[i]);
                        float Y, U, V;
                        if (identity) { Y = G; U = B; V = R; }
                        else
                        {
                            Y = (kr * R) + (kg * G) + (kb * B);
                            U = (B - Y) / (2 * (1 - kb));
                            V = (R - Y) / (2 * (1 - kr));
                        }
                        y[i] = (ushort)ToY(Y);
                        bu[bi * 2 + bj] = U;
                        bv[bi * 2 + bj] = V;
                        if (layout == Av1.Av1PixelLayout.I444) { u[i] = (ushort)ToUV(U); v[i] = (ushort)ToUV(V); }
                    }
                if (layout == Av1.Av1PixelLayout.I420)
                {
                    float su = 0.0f, sv = 0.0f;
                    for (int bj = 0; bj < bh; bj++)
                        for (int bi = 0; bi < bw; bi++) { su += bu[bi * 2 + bj]; sv += bv[bi * 2 + bj]; }
                    float n = bw * bh;
                    int ci = (oj >> 1) * cw + (oi >> 1);
                    u[ci] = (ushort)ToUV(su / n);
                    v[ci] = (ushort)ToUV(sv / n);
                }
                else if (layout == Av1.Av1PixelLayout.I422)
                {
                    for (int bj = 0; bj < bh; bj++)
                    {
                        float su = 0.0f, sv = 0.0f;
                        for (int bi = 0; bi < bw; bi++) { su += bu[bi * 2 + bj]; sv += bv[bi * 2 + bj]; }
                        float n = bw;
                        int ci = (oj + bj) * cw + (oi >> 1);
                        u[ci] = (ushort)ToUV(su / n);
                        v[ci] = (ushort)ToUV(sv / n);
                    }
                }
            }
    }

    private static void RgbToYuvAvif(double[] r, double[] g, double[] b, int w, int h, int bd, Av1.Av1PixelLayout layout,
        Av1.Av1ObuWriter.Av1ColorDesc color, out ushort[] y, out ushort[] u, out ushort[] v)
    {
        int max = (1 << bd) - 1;
        bool full = color.FullRange;
        double biasY = full ? 0 : 16 << (bd - 8), rangeY = full ? max : 219 << (bd - 8);
        double biasUV = 1 << (bd - 1), rangeUV = full ? max : 224 << (bd - 8);
        int mc = color.Matrix;
        (double kr, double kb) = mc == 12 ? ChromaDerivedKrKb(color.Primaries) : MatrixKrKb(mc);
        double kg = 1 - kr - kb;
        int ssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        int cw = (w + ssX) >> ssX, chh = (h + ssY) >> ssY;
        y = new ushort[w * h];
        u = new ushort[cw * chh];
        v = new ushort[cw * chh];
        var uf = new double[cw * chh];
        var vf = new double[cw * chh];
        var cnt = new int[cw * chh];
        int rgbMax = mc == 16 ? (1 << (bd - 2)) - 1 : mc == 17 ? (1 << (bd - 1)) - 1 : max;
        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int i = yy * w + xx;
                double R = r[i] / max, G = g[i] / max, B = b[i] / max, Y, U, V;
                if (mc == 0) { Y = G; U = B; V = R; }
                else if (mc == 8) { Y = 0.5 * G + 0.25 * (R + B); U = 0.5 * G - 0.25 * (R + B); V = 0.5 * (R - B); }
                else if (mc is 16 or 17)
                {
                    int Ri = (int)Math.Round(Math.Clamp(R * rgbMax, 0, rgbMax)), Gi = (int)Math.Round(Math.Clamp(G * rgbMax, 0, rgbMax));
                    int Bi = (int)Math.Round(Math.Clamp(B * rgbMax, 0, rgbMax));
                    int co = Ri - Bi, tt = Bi + (co >> 1), cg = Gi - tt;
                    Y = (tt + (cg >> 1)) / rangeY; U = cg / rangeUV; V = co / rangeUV;
                }
                else { Y = kr * R + kg * G + kb * B; U = (B - Y) / (2 * (1 - kb)); V = (R - Y) / (2 * (1 - kr)); }
                y[i] = (ushort)Math.Clamp((int)Math.Round(Y * rangeY + biasY), 0, max);
                int ci = (yy >> ssY) * cw + (xx >> ssX);
                uf[ci] += U;
                vf[ci] += V;
                cnt[ci]++;
            }
        }

        // Identity codes chroma planes with the luma range (H.273: G, B, R are all "luma-like").
        double cRange = mc == 0 ? rangeY : rangeUV, cBias = mc == 0 ? biasY : biasUV;
        for (int i = 0; i < cw * chh; i++)
        {
            int n = cnt[i] > 0 ? cnt[i] : 1;
            u[i] = (ushort)Math.Clamp((int)Math.Round(uf[i] / n * cRange + cBias), 0, max);
            v[i] = (ushort)Math.Clamp((int)Math.Round(vf[i] / n * cRange + cBias), 0, max);
        }
    }

    // libavif avifColorPrimariesComputeYCoeffs (H.273 eqs 32-37): kr/kb of the chromaticity-derived NCL matrix.
    private static (float Kr, float Kb) ChromaDerivedKrKb(int primaries)
    {
        float[] p = primaries switch
        {
            4 => [0.67f, 0.33f, 0.21f, 0.71f, 0.14f, 0.08f, 0.310f, 0.316f],
            5 => [0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f],
            6 or 7 => [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f],
            8 => [0.681f, 0.319f, 0.243f, 0.692f, 0.145f, 0.049f, 0.310f, 0.316f],
            9 => [0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.3290f],
            10 => [1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.3333f, 0.3333f],
            11 => [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.314f, 0.351f],
            12 => [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.3127f, 0.3290f],
            22 => [0.630f, 0.340f, 0.295f, 0.605f, 0.155f, 0.077f, 0.3127f, 0.3290f],
            _ => [0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f],   // BT.709 (and libavif's unknown default)
        };
        float rX = p[0], rY = p[1], gX = p[2], gY = p[3], bX = p[4], bY = p[5], wX = p[6], wY = p[7];
        float rZ = 1.0f - (rX + rY), gZ = 1.0f - (gX + gY), bZ = 1.0f - (bX + bY), wZ = 1.0f - (wX + wY);
        float den = wY * (rX * (gY * bZ - bY * gZ) + gX * (bY * rZ - rY * bZ) + bX * (rY * gZ - gY * rZ));
        float kr = rY * (wX * (gY * bZ - bY * gZ) + wY * (bX * gZ - gX * bZ) + wZ * (gX * bY - bX * gY)) / den;
        float kb = bY * (wX * (rY * gZ - gY * rZ) + wY * (gX * rZ - rX * gZ) + wZ * (rX * gY - gX * rY)) / den;
        return (kr, kb);
    }

    private static byte[] EncodeAvif8(ImageFrame image, int qp, Av1.Av1ObuWriter.Av1ColorDesc color, Av1.AvifContainerExtras extras)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w > 65536 || h > 65536 || w < 1 || h < 1)
        {
            throw new NotSupportedException($"AVIF encoding supports 1..65536 per dimension (got {w}x{h}).");
        }

        // The single-block path is a fast path for an even, square frame of 6..64px; the multi-superblock path (a grid
        // of 64x64 superblocks with edge force-split partitioning) handles everything else — larger, non-square,
        // odd, tiny (down to 1x1, as libavif), or mixed small/large dimensions.
        bool multiSb = w > 64 || h > 64 || w != h || (w & 1) != 0 || (h & 1) != 0 || w < 5;

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int alphaOff = channels - 1; // alpha is the last channel (idx 1 for gray+A, 3 for RGBA)

        // Extract luma; detect whether the image has real colour and non-opaque alpha.
        var luma = new byte[w * h];
        var alpha = hasAlpha ? new byte[w * h] : null;
        bool colour = false;
        bool nonOpaque = false;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<ushort> row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * channels;
                int r = Quantum.ScaleToByte(row[o]);
                int g = channels >= 3 ? Quantum.ScaleToByte(row[o + 1]) : r;
                int b = channels >= 3 ? Quantum.ScaleToByte(row[o + 2]) : r;
                if (r != g || g != b)
                {
                    colour = true;
                }

                luma[y * w + x] = (byte)r;
                if (alpha != null)
                {
                    byte a = Quantum.ScaleToByte(row[o + alphaOff]);
                    alpha[y * w + x] = a;
                    if (a != 255) nonOpaque = true;
                }
            }
        }

        // Map the HEVC-style qp (0..51, lower = better) to an AV1 base_q_idx (1..255, lower = better).
        int baseQIdx = Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);

        // Non-opaque alpha ⇒ 2-item AVIF (colour primary + monochrome alpha aux). Alpha is coded at higher
        // quality than colour (half the base_q_idx) since matte edges are visually unforgiving.
        if (hasAlpha && nonOpaque && alpha != null)
        {
            int alphaQIdx = Math.Clamp(baseQIdx / 2, 4, 255);
            I420Libavif(image, color, out byte[] yA, out byte[] uA, out byte[] vA);
            return Av1.Av1StillImageEncoder.EncodeAvifColorWithAlpha(yA, uA, vA, alpha, w, h, baseQIdx, alphaQIdx, color, extras);
        }

        if (colour)
        {
            // Colour: single-block I420 for <=64px, multi-superblock I420 for larger frames. I420 chroma is
            // ceil(w/2) x ceil(h/2) — odd luma dimensions are supported (the last chroma sample averages the
            // partial 2x2 group at the edge).
            I420Libavif(image, color, out byte[] yP, out byte[] uP, out byte[] vP);
            if (multiSb)
                return Av1.Av1StillImageEncoder.EncodeAvifColorMultiSb(yP, uP, vP, w, h, baseQIdx, color, extras);
            if ((w & 1) != 0 || (h & 1) != 0)
                throw new NotSupportedException($"AVIF single-block colour needs even dimensions (got {w}x{h}); larger frames support odd.");
            return Av1.Av1StillImageEncoder.EncodeAvifColor(yP, uP, vP, w, h, baseQIdx, color, extras);
        }

        return multiSb
            ? Av1.Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(luma, w, h, baseQIdx, color, extras)
            : Av1.Av1StillImageEncoder.EncodeAvifMonochrome(luma, w, h, baseQIdx, color, extras);
    }

    // avifenc --grid: the image split into Columns x Rows cells (avifImageSplitGrid / avifGetBestCellSize), every cell
    // coded with the same settings, laid out as libavif does (Av1AvifWriter.BuildGridAvif).
    private static byte[] EncodeAvifGrid(ImageFrame image, AvifEncodeOptions options, int cols, int rows)
    {
        if (cols is < 1 or > 256 || rows is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options), "Grid columns and rows must be in 1..256.");
        int w = (int)image.Columns, h = (int)image.Rows;
        // Layered grids (libavif avifEncoderAddImageGrid with extraLayerCount): every cell is itself layered.
        var layerSpecs = options.Layers is { Count: > 0 } ls ? ls : null;
        int bd = options.BitDepth == 0 ? (HasSubByteDetail(image) ? 10 : 8) : options.BitDepth;
        if (bd is not (8 or 10 or 12))
            throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        var color = ResolveAvifColor(image, options, bd);
        if (options.Lossless && options.MatrixCoefficients == null) color = color with { Matrix = 0 };
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0) color = color with { Matrix = 6 };
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1.Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1.Av1PixelLayout.I400,
            _ => color.Matrix == 0 || options.Lossless ? Av1.Av1PixelLayout.I444 : Av1.Av1PixelLayout.I420,
        };

        // The coded chroma format of every cell: grey sources are coded 4:0:0 (except lossy identity / YCgCo-R).
        ReadRgbPlanes(image, bd, out _, out _, out _, out _, out bool colour, out bool nonOpaque);
        bool mono = layout == Av1.Av1PixelLayout.I400 || (!colour && !(!options.Lossless && color.Matrix is 0 or 16 or 17));
        var coded = mono ? Av1.Av1PixelLayout.I400 : layout;
        bool ssX = coded is Av1.Av1PixelLayout.I420 or Av1.Av1PixelLayout.I422, ssY = coded == Av1.Av1PixelLayout.I420;
        int cellW = GridCellSize("horizontally", w, cols, ssX), cellH = GridCellSize("vertically", h, rows, ssY);
        // MIAF 7.3.11.4.2 (libavif avifAreGridDimensionsValid): the output size must be even along subsampled axes.
        if ((ssX && (w & 1) != 0) || (ssY && (h & 1) != 0))
            throw new ArgumentException($"A {coded} grid image must have an even {(ssX && (w & 1) != 0 ? "width" : "height")} (MIAF 7.3.11.4.2).", nameof(options));
        bool alpha = image.HasAlpha && nonOpaque;

        var cellOpt = new AvifEncodeOptions
        {
            Qp = options.Qp, BitDepth = bd, ChromaSubsampling = options.ChromaSubsampling == AvifChromaSubsampling.Auto
                ? (layout switch { Av1.Av1PixelLayout.I444 => AvifChromaSubsampling.Yuv444, _ => AvifChromaSubsampling.Yuv420 })
                : options.ChromaSubsampling,
            ColorPrimaries = color.Primaries, TransferCharacteristics = color.Transfer, MatrixCoefficients = color.Matrix,
            FullRange = color.FullRange, Rotation = 0, Mirror = null, Lossless = options.Lossless, PremultiplyAlpha = options.PremultiplyAlpha,
            FilmGrain = options.FilmGrain, DenoiseNoiseLevel = options.DenoiseNoiseLevel, DenoiseUseRequestedLevel = options.DenoiseUseRequestedLevel,
            DenoiseBlockSize = options.DenoiseBlockSize, DenoiseApply = options.DenoiseApply,
            Quality = options.Quality, QualityAlpha = options.QualityAlpha, Progressive = options.Progressive,
        };
        bool layered = options.Progressive || layerSpecs != null;
        var cells = new Av1.AvifGridCells
        {
            Columns = cols, Rows = rows, CellWidth = cellW, CellHeight = cellH, Alpha = alpha ? [] : null,
            ColorLayerSizes = layered ? [] : null, AlphaLayerSizes = layered && alpha ? [] : null,
        };
        using var tiling = Av1.Av1ObuWriter.UseTiling(ResolveTiling(options, cellW, cellH));   // libavif: from the first cell
        using var sharedHeader = new Av1.Av1ObuWriter.SharedHeaderScope();   // every cell uses the first cell's av1C
        for (int gy = 0; gy < rows; gy++)
            for (int gx = 0; gx < cols; gx++)
            {
                int x0 = gx * cellW, y0 = gy * cellH;
                int cw = Math.Min(cellW, w - x0), chh = Math.Min(cellH, h - y0);
                ImageFrame Crop(ImageFrame src)
                {
                    var cell = new ImageFrame();
                    cell.Initialize(cw, chh, ColorspaceType.SRGB, src.HasAlpha);
                    cell.Depth = src.Depth;
                    int n = src.NumberOfChannels;
                    for (int y = 0; y < chh; y++)
                        src.GetPixelRow(y0 + y).Slice(x0 * n, cw * n).CopyTo(cell.GetPixelRowForWrite(y));
                    return cell;
                }
                var cellImage = Crop(image);
                byte[] file = layerSpecs != null
                    ? EncodeAvifLayers(cellImage, cellOpt, layerSpecs.Select(l => new AvifLayer
                    {
                        Image = l.Image == null ? null : Crop(l.Image), Quality = l.Quality, QualityAlpha = l.QualityAlpha,
                        ScaleNumerator = l.ScaleNumerator, ScaleDenominator = l.ScaleDenominator,
                    }).ToList(), forceColor: !mono, forceAlpha: alpha)
                    : EncodeAvifCore(cellImage, cellOpt, forceColor: !mono, forceAlpha: alpha);
                var c = HeifContainer.Parse(file);
                int pid = c.PrimaryId;
                cells.Color.Add(c.ItemData(pid)!);
                if (cells.ColorAv1CBox.Length == 0) cells.ColorAv1CBox = RawProperty(c, pid, "av1C");
                if (layered) cells.ColorLayerSizes!.Add(LayerSizes(c, pid));
                if (alpha)
                {
                    int aid = c.ReferencesTo(pid, "auxl").First();
                    cells.Alpha!.Add(c.ItemData(aid)!);
                    if (cells.AlphaAv1CBox.Length == 0) cells.AlphaAv1CBox = RawProperty(c, aid, "av1C");
                    if (layered) cells.AlphaLayerSizes!.Add(LayerSizes(c, aid));
                }
            }

        var extras = AvifExtras(image, options);
        extras.Premultiplied = options.PremultiplyAlpha && alpha;
        if (options.GainMap != null) extras.GainMap = BuildGainMapItem(options.GainMap, options, extras);
        return Av1.Av1AvifWriter.BuildGridAvif(cells, w, h, bd, coded, color, extras);
    }

    // avifGetBestCellSize (avifenc): ceil(pixels / cells), raised to MIAF's 64 minimum and to even along subsampled axes,
    // rejected when a cell would fall entirely off the canvas or exceed AV1's 65536.
    private static int GridCellSize(string dimension, int pixels, int cells, bool subsampled)
    {
        long size = ((long)pixels + cells - 1) / cells;
        if (cells > 1 && size < 64)
        {
            size = 64;
            if ((long)(cells - 1) * size >= pixels)
                throw new ArgumentException($"There are too many cells {dimension} ({cells}) to have at least 64 pixels per cell.");
        }
        if (size > 65536) throw new ArgumentException($"Cell size {size} is bigger {dimension} than the maximum frame size 65536.");
        if (subsampled && (size & 1) != 0)
        {
            size++;
            if ((long)(cells - 1) * size >= pixels)
                throw new ArgumentException($"Odd cell size {size - 1} is forbidden on a {dimension} subsampled image.");
        }
        return (int)size;
    }

    // The layer sizes of a layered item from its 'a1lx' (every layer but the last; the last is the remainder).
    private static long[] LayerSizes(HeifContainer c, int id)
    {
        int total = c.ItemData(id)!.Length;
        if (c.Property(id, "a1lx") is not { } p) return [total];
        byte[] d = c.Data;
        bool large = (d[p.Off] & 1) != 0;
        var sizes = new List<long>();
        long sum = 0;
        for (int i = 0; i < 3; i++)
        {
            long v = large ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p.Off + 1 + i * 4)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p.Off + 1 + i * 2));
            if (v == 0) break;
            sizes.Add(v);
            sum += v;
        }
        sizes.Add(total - sum);
        return sizes.ToArray();
    }

    private static byte[] RawProperty(HeifContainer c, int id, string type)
    {
        var p = c.Property(id, type) ?? throw new InvalidOperationException($"Encoded cell lacks '{type}'.");
        return c.Data.AsSpan(p.Off - 8, p.Len + 8).ToArray();
    }

    // BT.601 full-range RGB→YUV (the inverse of ConvertYuvToRgb's full-range BT.601 path) with I420 chroma
    // subsampling: w x h luma, (w/2) x (h/2) U and V (2x2 box average). Requires even dimensions.
    // 8-bit 4:2:0 planes as libavif converts them (libyuv J420 / I420 for 8-bit sources, else its float path).
    private static void I420Libavif(ImageFrame image, Av1.Av1ObuWriter.Av1ColorDesc color, out byte[] y, out byte[] u, out byte[] v)
    {
        RgbToYuvLibavif(image, 8, Av1.Av1PixelLayout.I420, color, SourceRgbDepth(image), false, [], [], [], out var y16, out var u16, out var v16);
        static byte[] Narrow(ushort[] p)
        {
            var r = new byte[p.Length];
            for (int i = 0; i < p.Length; i++) r[i] = (byte)p[i];
            return r;
        }
        y = Narrow(y16);
        u = Narrow(u16);
        v = Narrow(v16);
    }

    #region AV1 Intra Frame Codec

    // Decodes a monochrome AV1 alpha auxiliary item and writes its luma samples into the frame's alpha channel
    // (enabling alpha if needed). Full-range 8-bit is the standard AVIF alpha representation.
    private static void ApplyAv1Alpha(ReadOnlySpan<byte> codedData, ImageFrame frame, int w, int h, HeifContainer? c = null, int id = 0)
    {
        using var yuv = DecodeAv1Item(c, id, codedData, out var decoder, "AV1 alpha");
        ApplyDecodedAlpha(yuv, decoder.FullColorRange, frame, w, h);
    }

    private static void ApplyDecodedAlpha(Av1.DecodedVideoFrame yuv, bool fullRange, ImageFrame frame, int w, int h)
    {
        if (yuv.Width != w || yuv.Height != h || frame.Columns < w || frame.Rows < h)
            throw new InvalidDataException($"The alpha plane ({yuv.Width}x{yuv.Height}) does not match the image ({w}x{h}).");
        if (!fullRange)
        {
            ApplyAv1AlphaLimited(yuv, frame, w, h);
            return;
        }
        if (yuv.BitDepth > 8)
        {
            // Native-precision alpha onto the 16-bit quantum, as libavif's avifReformatAlpha rescales depth (float):
            // (int)(0.5f + (a / srcMax) * 65535f).
            if (!frame.HasAlpha) frame.SetAlpha(true);
            ReadOnlySpan<ushort> a16 = yuv.YPlane16.Span;
            float srcMax = (1 << yuv.BitDepth) - 1;
            int aOff = frame.NumberOfChannels - 1, nch = frame.NumberOfChannels;
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                    row[x * nch + aOff] = (ushort)Math.Clamp((int)(0.5f + (a16[y * yuv.YStride + x] / srcMax) * 65535.0f), 0, 65535);
            }
            return;
        }

        bool tenBit = yuv.Format is Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12;
        int shift = tenBit ? (yuv.Format == Av1.PixelFormat.Yuv420P12 ? 4 : 2) : 0;
        ReadOnlySpan<byte> y0 = yuv.YPlane.Span;
        int stride = yuv.YStride;

        if (!frame.HasAlpha)
        {
            frame.SetAlpha(true);
        }

        int alphaOff = frame.NumberOfChannels - 1;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < w; x++)
            {
                int a = tenBit ? ((y0[(y * stride + x) * 2] | (y0[(y * stride + x) * 2 + 1] << 8)) >> shift) : y0[y * stride + x];
                row[x * ch + alphaOff] = Quantum.ScaleFromByte((byte)Math.Clamp(a, 0, 255));
            }
        }
    }

    // Limited-range alpha (allowed by AVIF 1.0.0, since forbidden): libavif converts it to full range per sample with
    // LIMITED_TO_FULL (v = ((v - lo) * max + (hi - lo) / 2) / (hi - lo), clamped), lo/hi = 16/235 << (bd - 8).
    private static void ApplyAv1AlphaLimited(Av1.DecodedVideoFrame yuv, ImageFrame frame, int w, int h)
    {
        int bd = yuv.BitDepth, max = (1 << bd) - 1, lo = 16 << (bd - 8), hi = 235 << (bd - 8);
        if (!frame.HasAlpha) frame.SetAlpha(true);
        int nch = frame.NumberOfChannels, aOff = nch - 1;
        ReadOnlySpan<byte> a8 = yuv.YPlane.Span;
        ReadOnlySpan<ushort> a16 = yuv.YPlane16.Span;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int v = bd > 8 ? a16[y * yuv.YStride + x] : a8[y * yuv.YStride + x];
                v = Math.Clamp(((v - lo) * max + (hi - lo) / 2) / (hi - lo), 0, max);
                row[x * nch + aOff] = bd > 8 ? (ushort)Math.Clamp((int)(0.5f + (v / (float)max) * 65535.0f), 0, 65535) : Quantum.ScaleFromByte((byte)v);
            }
        }
    }

    private static void DecodeAv1IntraFrame(ReadOnlySpan<byte> codedData, ImageFrame frame, (int Cp, int Tc, int Mc, bool Full)? nclx,
        ushort[]? premAlpha = null, HeifContainer? c = null, int id = 0)
    {
        // Decode the AV1 item with the vendored decoder (pixel-exact vs dav1d), then convert its YUV planes to RGB. AVIF
        // stores the whole temporal unit (sequence header + frame OBUs, possibly several layers) in the item's data.
        using var yuv = DecodeAv1Item(c, id, codedData, out var decoder, "AV1");
        ConvertDecodedAv1(yuv, decoder, frame, nclx, premAlpha);
    }

    // YUV -> RGB of one decoded AV1 frame into `frame` (sized like it), with the container's nclx overriding the
    // sequence header's CICP (MIAF 7.3.6.4).
    private static void ConvertDecodedAv1(Av1.DecodedVideoFrame yuv, Av1.Av1Decoder decoder, ImageFrame frame,
        (int Cp, int Tc, int Mc, bool Full)? nclx, ushort[]? premAlpha)
    {
        var cicp = nclx ?? (decoder.ColorPrimaries, decoder.TransferCharacteristics, decoder.MatrixCoefficients, decoder.FullColorRange);
        frame.Metadata.Cicp = new SharpImage.Metadata.CicpInfo(cicp.Cp, cicp.Tc, cicp.Mc, cicp.Full);

        // YUV -> RGB exactly as libavif does it (avifImageYUVToRGB, AUTOMATIC upsampling): libyuv for 8-bit where it
        // has the matrix, else the reference float path (unorm tables, bilinear 9/3/3/1 chroma, matrix modes).
        bool is444 = yuv.Format == Av1.PixelFormat.Yuv444P, is422 = yuv.Format == Av1.PixelFormat.Yuv422P;
        ConvertYuvToRgbLibavif(yuv, frame, (int)frame.Columns, (int)frame.Rows, frame.NumberOfChannels,
            decoder.Monochrome, cicp.Mc, cicp.Full, cicp.Cp, is444 ? 0 : 1, (is444 || is422) ? 0 : 1, premAlpha);
    }

    // Converts a decoded 8/10/12-bit planar YUV 4:2:0 frame to RGB, honouring the colour
    // matrix (BT.709 vs BT.601) and range (full vs limited) signalled by the container.
    // Shared by the AVIF (AV1) and HEIC (HEVC) paths.
    private static void ConvertYuvToRgb(ReadOnlySpan<byte> y0, ReadOnlySpan<byte> u0, ReadOnlySpan<byte> v0, int yStride, int uStride, int vStride, ImageFrame frame, int w, int h, int channels, bool tenBit, int shift, bool bt709, bool fullRange, int ssHor = 1, int ssVer = 1)
    {
        int Sample(ReadOnlySpan<byte> plane, int stride, int x, int y)
        {
            if (!tenBit)
            {
                return plane[y * stride + x];
            }

            int idx = (y * stride + x) * 2;
            int v = plane[idx] | (plane[idx + 1] << 8);
            return v >> shift;
        }

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int cy = y >> ssVer;
            for (int x = 0; x < w; x++)
            {
                int cx = x >> ssHor;
                int yv = Sample(y0, yStride, x, y);
                int d = Sample(u0, uStride, cx, cy) - 128;
                int e = Sample(v0, vStride, cx, cy) - 128;
                byte r, g, b;
                if (fullRange)
                {
                    // Full range: no Y offset, 16.16 fixed-point coefficients.
                    (int cr, int cgU, int cgV, int cb) = bt709
                        ? (103206, 12276, 30679, 121609)   // BT.709
                        : (91881, 22554, 46802, 116130);   // BT.601 (JPEG)
                    r = ClampByte(yv + ((cr * e + 32768) >> 16));
                    g = ClampByte(yv - ((cgU * d + cgV * e + 32768) >> 16));
                    b = ClampByte(yv + ((cb * d + 32768) >> 16));
                }
                else
                {
                    // Limited (video) range: Y scaled by 1.164 from 16, 8.8 fixed-point.
                    int c = 298 * (yv - 16);
                    (int cr, int cgU, int cgV, int cb) = bt709
                        ? (459, 55, 136, 541)              // BT.709
                        : (409, 100, 208, 516);            // BT.601
                    r = ClampByte((c + cr * e + 128) >> 8);
                    g = ClampByte((c - cgU * d - cgV * e + 128) >> 8);
                    b = ClampByte((c + cb * d + 128) >> 8);
                }

                int off = x * channels;
                row[off] = Quantum.ScaleFromByte(r);
                if (channels > 1)
                {
                    row[off + 1] = Quantum.ScaleFromByte(g);
                }

                if (channels > 2)
                {
                    row[off + 2] = Quantum.ScaleFromByte(b);
                }
            }
        }
    }

    // Monochrome (I400): the luma plane IS the image. Replicate it into each output channel. Luma is treated as
    // full-range gray (no BT.601/709 chroma matrix applies).
    private static void ConvertGrayToRgb(ReadOnlySpan<byte> y0, int yStride, ImageFrame frame, int w, int h, int channels, bool tenBit, int shift)
    {
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int yv;
                if (!tenBit)
                {
                    yv = y0[y * yStride + x];
                }
                else
                {
                    int idx = (y * yStride + x) * 2;
                    yv = (y0[idx] | (y0[idx + 1] << 8)) >> shift;
                }

                ushort q = Quantum.ScaleFromByte(ClampByte(yv));
                int off = x * channels;
                row[off] = q;
                if (channels > 1)
                {
                    row[off + 1] = q;
                }

                if (channels > 2)
                {
                    row[off + 2] = q;
                }
            }
        }
    }

    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // libavif's matrix table (colr.c matrixCoefficientsTables): (kr, kb) per CICP matrix_coefficients. Anything else
    // (incl. unspecified) falls back to the MIAF default BT.601, as libavif does.
    private static (float Kr, float Kb) MatrixKrKb(int mc) => mc switch
    {
        1 => (0.2126f, 0.0722f),   // BT.709
        4 => (0.30f, 0.11f),       // FCC
        5 or 6 => (0.299f, 0.114f), // BT.470BG / BT.601
        7 => (0.212f, 0.087f),     // SMPTE 240
        9 => (0.2627f, 0.0593f),   // BT.2020 NCL
        _ => (0.299f, 0.114f),
    };

    // A port of libavif's avifImageYUVAnyToRGBAnySlow for the YUV->RGB step (8/10/12-bit, 4:0:0/4:2:0/4:2:2/4:4:4):
    // unorm float tables (limited range: Y (v-16s)/219s, UV (v-2^(bd-1))/224s; full: v/max, (v-2^(bd-1))/max),
    // bilinear chroma with weights 9/16, 3/16, 3/16, 1/16 where the second tap is the neighbouring chroma sample
    // toward the luma sample's side (none at the picture edge; 4:2:2 is horizontal only), clamp to [0,1], and
    // (uint16)(0.5f + v * 65535) into the 16-bit quantum. 32-bit float throughout, like libavif.
    private static void ConvertYuvToRgbLibavif(Av1.DecodedVideoFrame yuv, ImageFrame frame, int w, int h, int channels,
        bool monochrome, int matrixCoeffs, bool fullRange, int primaries, int ssHor, int ssVer, ushort[]? premAlpha = null)
    {
        // 8-bit colour goes through libyuv in libavif (its default build), whose fixed-point math differs from the
        // float path by up to 2 levels — use the exact port so we decode what libavif decodes.
        var ups = Limits.ChromaUpsampling;
        bool nearest = ups is AvifChromaUpsampling.Fastest or AvifChromaUpsampling.Nearest;
        if (yuv.BitDepth == 8 && !monochrome && LibyuvConstants(matrixCoeffs, fullRange, primaries) is { } k)
        {
            ConvertYuvToRgbLibyuv8(yuv, frame, w, h, channels, k, ssHor, ssVer, nearest);
            if (premAlpha != null) UnattenuateLibyuv(frame, premAlpha, w, h, channels);
            return;
        }

        int bd = yuv.BitDepth, maxCh = (1 << bd) - 1;
        bool hbd = bd > 8;
        // libavif reformat modes: identity (GBR, chroma on the luma table), YCgCo, YCgCo-Re/Ro (integer), else kr/kb.
        int mode = monochrome ? 0 : matrixCoeffs switch { 0 => 1, 8 => 2, 16 or 17 => 3, _ => 0 };
        // libavif converts high-bit-depth 4:4:4 colour with kr/kb coefficients through its fast path and un-premultiplies
        // the quantised result afterwards; every other case goes through the slow path, which un-premultiplies in float
        // before quantisation (monochrome included: avifdec converts grey images to a grey RGB format, which the fast
        // paths do not take).
        bool postUnmultiply = premAlpha != null && hbd && mode == 0 && !monochrome && ssHor == 0 && ssVer == 0;
        float rangeY = fullRange ? maxCh : 219 << (bd - 8), biasY = fullRange ? 0 : 16 << (bd - 8);
        float rangeUV = fullRange ? maxCh : 224 << (bd - 8), biasUV = 1 << (bd - 1);
        var tabY = new float[maxCh + 1];
        var tabUV = new float[maxCh + 1];
        for (int cp = 0; cp <= maxCh; cp++) { tabY[cp] = (cp - biasY) / rangeY; tabUV[cp] = mode == 1 ? tabY[cp] : (cp - biasUV) / rangeUV; }
        (float kr, float kb) = matrixCoeffs == 12 ? ChromaDerivedKrKb(primaries) : MatrixKrKb(matrixCoeffs);
        float kg = 1.0f - kr - kb;
        int rgbMaxR = matrixCoeffs == 16 ? (1 << (bd - 2)) - 1 : (1 << (bd - 1)) - 1;   // YCgCo-Re/Ro RGB depth

        ReadOnlySpan<byte> y8 = yuv.YPlane.Span, u8 = yuv.UPlane.Span, v8 = yuv.VPlane.Span;
        ReadOnlySpan<ushort> y16 = yuv.YPlane16.Span, u16 = yuv.UPlane16.Span, v16 = yuv.VPlane16.Span;
        int ys = yuv.YStride, us = yuv.UStride, vs = yuv.VStride;
        bool is420 = ssVer == 1, is444 = ssHor == 0;

        for (int j = 0; j < h; j++)
        {
            var row = frame.GetPixelRowForWrite(j);
            int uvJ = j >> ssVer;
            int adjRow = (j == 0 || (j == h - 1 && (j & 1) != 0) || !is420) ? 0 : ((j & 1) != 0 ? 1 : -1);
            for (int i = 0; i < w; i++)
            {
                float Y = tabY[Math.Min(hbd ? y16[j * ys + i] : y8[j * ys + i], maxCh)];
                float R, G, B;
                if (monochrome)
                {
                    R = G = B = Y;
                }
                else
                {
                    float Cb, Cr;
                    int uvI = i >> ssHor;
                    if (is444 || nearest)
                    {
                        // 4:4:4, or nearest-neighbour chroma (avifChromaUpsampling FASTEST / NEAREST)
                        Cb = tabUV[Math.Min(hbd ? u16[uvJ * us + uvI] : u8[uvJ * us + uvI], maxCh)];
                        Cr = tabUV[Math.Min(hbd ? v16[uvJ * vs + uvI] : v8[uvJ * vs + uvI], maxCh)];
                    }
                    else
                    {
                        int adjCol = (i == 0 || (i == w - 1 && (i & 1) != 0)) ? 0 : ((i & 1) != 0 ? 1 : -1);
                        int c0 = uvJ * us + uvI, c1 = c0 + adjCol, c2 = c0 + adjRow * us, c3 = c2 + adjCol;
                        int d0 = uvJ * vs + uvI, d1 = d0 + adjCol, d2 = d0 + adjRow * vs, d3 = d2 + adjCol;
                        Cb = S(u8, u16, hbd, c0, maxCh, tabUV) * (9.0f / 16.0f) + S(u8, u16, hbd, c1, maxCh, tabUV) * (3.0f / 16.0f)
                           + S(u8, u16, hbd, c2, maxCh, tabUV) * (3.0f / 16.0f) + S(u8, u16, hbd, c3, maxCh, tabUV) * (1.0f / 16.0f);
                        Cr = S(v8, v16, hbd, d0, maxCh, tabUV) * (9.0f / 16.0f) + S(v8, v16, hbd, d1, maxCh, tabUV) * (3.0f / 16.0f)
                           + S(v8, v16, hbd, d2, maxCh, tabUV) * (3.0f / 16.0f) + S(v8, v16, hbd, d3, maxCh, tabUV) * (1.0f / 16.0f);
                    }

                    if (mode == 1) { G = Y; B = Cb; R = Cr; }                               // identity (H.273 41-43)
                    else if (mode == 2) { float tt = Y - Cb; G = Y + Cb; B = tt - Cr; R = tt + Cr; }  // YCgCo (47-50)
                    else if (mode == 3)
                    {
                        // YCgCo-Re/Ro (H.273-2024 62-65): integer lifting on the unorm Y and rounded Cg/Co.
                        int yy = Math.Min(hbd ? y16[j * ys + i] : y8[j * ys + i], maxCh);
                        int cg = (int)MathF.Floor(Cb * maxCh + 0.5f), co = (int)MathF.Floor(Cr * maxCh + 0.5f);   // avifRoundf
                        int t2 = yy - (cg >> 1);
                        int gi = Math.Clamp(t2 + cg, 0, rgbMaxR), bi = Math.Clamp(t2 - (co >> 1), 0, rgbMaxR), ri = Math.Clamp(bi + co, 0, rgbMaxR);
                        G = gi / (float)rgbMaxR; B = bi / (float)rgbMaxR; R = ri / (float)rgbMaxR;
                    }
                    else
                    {
                        R = Y + (2 * (1 - kr)) * Cr;
                        B = Y + (2 * (1 - kb)) * Cb;
                        G = Y - ((2 * ((kr * (1 - kr) * Cr) + (kb * (1 - kb) * Cb))) / kg);
                    }
                }

                if (premAlpha != null && !postUnmultiply)
                {
                    // libavif slow path UNMULTIPLY: on the clamped float colour, before quantisation.
                    float ac = Math.Clamp(premAlpha[j * w + i] / (float)maxCh, 0.0f, 1.0f);
                    R = Math.Clamp(R, 0.0f, 1.0f); G = Math.Clamp(G, 0.0f, 1.0f); B = Math.Clamp(B, 0.0f, 1.0f);
                    if (ac == 0.0f) { R = G = B = 0.0f; }
                    else if (ac < 1.0f) { R = Math.Min(R / ac, 1.0f); G = Math.Min(G / ac, 1.0f); B = Math.Min(B / ac, 1.0f); }
                }

                int off = i * channels;
                if (t_nativeDepthRgb && hbd)
                {
                    row[off] = QNative(R, maxCh);
                    if (channels >= 3)
                    {
                        row[off + 1] = QNative(G, maxCh);
                        row[off + 2] = QNative(B, maxCh);
                    }
                }
                else
                {
                    row[off] = Q16f(R);
                    if (channels >= 3)
                    {
                        row[off + 1] = Q16f(G);
                        row[off + 2] = Q16f(B);
                    }
                }
                if (postUnmultiply) UnmultiplyQuantized(row, off, channels >= 3 ? 3 : 1, premAlpha![j * w + i], maxCh, t_nativeDepthRgb);
            }
        }
    }

    // libavif avifRGBImageUnpremultiplyAlpha (high-bit-depth RGB): after its 4:4:4 fast YUV->RGB path the
    // colour is un-premultiplied on the quantised samples, c = min(roundf(c * max / a), max), with alpha rescaled to the
    // RGB depth by avifReformatAlpha. Stored values are 16-bit (or native-depth scaled, see t_nativeDepthRgb).
    private static void UnmultiplyQuantized(Span<ushort> row, int off, int n, ushort alphaNative, int alphaMax, bool nativeDepth)
    {
        int max = nativeDepth ? alphaMax : 65535;
        float maxF = max;
        int a = nativeDepth ? alphaNative : Math.Clamp((int)(0.5f + (alphaNative / (float)alphaMax) * 65535.0f), 0, 65535);
        if (a >= max) return;
        for (int k = 0; k < n; k++)
        {
            if (a == 0) { row[off + k] = 0; continue; }
            int p = nativeDepth ? (int)((row[off + k] * (uint)max + 32767u) / 65535u) : row[off + k];
            float c = MathF.Min(MathF.Floor(p * maxF / a + 0.5f), maxF);
            row[off + k] = nativeDepth ? (ushort)(((uint)c * 65535u + (uint)max / 2) / (uint)max) : (ushort)c;
        }
    }

    // libyuv YuvConstants (row_common.cc, default build: UB clamped to 128 for limited range) as libavif's
    // getLibYUVConstants selects them: (YG, YB, UB, UG, VG, VR). Null where libavif falls back to its float path.
    private static (int Yg, int Yb, int Ub, int Ug, int Vg, int Vr)? LibyuvConstants(int mc, bool fullRange, int primaries)
        => mc == 12
            ? primaries switch { 1 or 2 => LibyuvConstants(1, fullRange), 5 or 6 => LibyuvConstants(6, fullRange), 9 => LibyuvConstants(9, fullRange), _ => null }
            : LibyuvConstants(mc, fullRange);

    private static (int Yg, int Yb, int Ub, int Ug, int Vg, int Vr)? LibyuvConstants(int mc, bool fullRange) => (mc, fullRange) switch
    {
        (5 or 6 or 2, true) => (16320, 32, 113, 22, 46, 90),        // JPEG
        (1, true) => (16320, 32, 119, 12, 30, 101),                 // F709
        (9, true) => (16320, 32, 120, 11, 37, 94),                  // V2020
        (5 or 6 or 2, false) => (18997, -1160, 128, 25, 52, 102),   // I601
        (1, false) => (18997, -1160, 128, 14, 34, 115),             // H709
        (9, false) => (19003, -1160, 128, 12, 42, 107),             // 2020
        _ => null,
    };

    // libyuv 8-bit YUV->RGB as libavif calls it (AUTOMATIC upsampling => the *MatrixFilter bilinear variants):
    // chroma upsampled per row by ScaleRowUp2_Linear_Any (4:2:2) / ScaleRowUp2_Bilinear_Any (4:2:0, first/last
    // row linear), then the x86 CALC_RGB16 fixed-point pixel, >> 6, clamp. Result is 8-bit, widened by 257.
    // libavif's 8-bit un-premultiply (avifRGBImageUnpremultiplyAlpha -> libyuv ARGBUnattenuate): 8.8 fixed-point
    // reciprocal table fixed_invtbl8 and clamp255(((f | f << 8) * ia) >> 16), exactly as libyuv's C/SIMD rows.
    private static void UnattenuateLibyuv(ImageFrame frame, ushort[] alpha8, int w, int h, int channels)
    {
        for (int j = 0; j < h; j++)
        {
            var row = frame.GetPixelRowForWrite(j);
            for (int i = 0; i < w; i++)
            {
                int a = alpha8[j * w + i];
                int ia = a == 0 ? 0 : a == 1 ? 0xffff : a == 255 ? 0x100 : 0x10000 / a;
                for (int ch = 0; ch < Math.Min(3, channels); ch++)
                {
                    int f = row[i * channels + ch] / 257;
                    row[i * channels + ch] = (ushort)(Math.Min(255, ((f | (f << 8)) * ia) >> 16) * 257);
                }
            }
        }
    }

    // Grid fallback: un-premultiply the decoded 16-bit colour by the frame's alpha (float, as libavif's slow path).
    private static void UnpremultiplyFrame(ImageFrame frame)
    {
        int w = (int)frame.Columns, h = (int)frame.Rows, ch = frame.NumberOfChannels;
        for (int j = 0; j < h; j++)
        {
            var row = frame.GetPixelRowForWrite(j);
            for (int i = 0; i < w; i++)
            {
                float a = row[i * ch + ch - 1] / 65535.0f;
                for (int k = 0; k < Math.Min(3, ch - 1); k++)
                {
                    float v = row[i * ch + k] / 65535.0f;
                    v = a == 0 ? 0 : a < 1 ? Math.Min(v / a, 1.0f) : v;
                    row[i * ch + k] = Q16f(v);
                }
            }
        }
    }

    /// <summary>
    /// Decodes an AV1 item the way libavif does: the 'a1op' operating point; a layer chosen by 'lsel' (or by
    /// DecodeProgressive for a progressive item), else the temporal unit's top spatial layer; and finally the image is
    /// scaled to the item's 'ispe' size when the coded frame differs (avifImageScaleWithLimit / libyuv kFilterBox).
    /// </summary>
    private static Av1.DecodedVideoFrame DecodeAv1Item(HeifContainer? c, int id, ReadOnlySpan<byte> coded, out Av1.Av1Decoder decoder, string what)
    {
        decoder = NewAv1Decoder();
        int layer = -1;
        bool byIndex = false;
        if (c != null)
        {
            if (c.Property(id, "a1op") is { } op) decoder.OperatingPoint = c.Data[op.Off + 0];
            if (c.Property(id, "lsel") is { } ls && BinaryPrimitives.ReadUInt16BigEndian(c.Data.AsSpan(ls.Off)) is var lid && lid != 0xFFFF)
                layer = lid;
            else if (t_progressiveLayer >= 0 && ProgressiveLayerCount(c, id) > 1) { layer = t_progressiveLayer; byIndex = true; }
        }
        Av1.DecodedVideoFrame? yuv;
        if (layer < 0) yuv = decoder.Decode(coded, 0, isKeyframe: true);
        else
        {
            // All layers are decoded; the requested one is kept (libavif: all_layers + spatial id / sample index).
            var frames = decoder.DecodeTemporalUnit(coded, 0);
            int pick = -1;
            for (int k = 0; k < frames.Count && pick < 0; k++)
                if (byIndex ? k == layer : frames[k].SpatialId == layer) pick = k;
            if (byIndex && pick < 0 && frames.Count > 0) pick = frames.Count - 1;
            for (int k = 0; k < frames.Count; k++) if (k != pick) frames[k].Frame.Dispose();
            yuv = pick >= 0 ? frames[pick].Frame : null;
        }
        if (yuv == null) throw new InvalidDataException($"{what} decode produced no frame. " + FirstLine(Av1.Av1Decoder.LastDecodeError));
        if (c?.Ispe(id) is { } ispe && (ispe.W != yuv.Width || ispe.H != yuv.Height))
        {
            var scaled = ScaleDecodedFrame(yuv, ispe.W, ispe.H);
            yuv.Dispose();
            yuv = scaled;
        }
        return yuv;
    }

    // Per-plane libyuv scaling of a decoded frame (8-bit ScalePlane or ScalePlane_12, kFilterBox), as libavif scales an
    // image whose coded size is not its item's output size.
    private static Av1.DecodedVideoFrame ScaleDecodedFrame(Av1.DecodedVideoFrame f, int w, int h)
    {
        // libavif avifImageScaleWithLimit: a valid target within the limits, and (its guard against integer overflow
        // in libyuv's scalers) a source of at most 16384 x 16384.
        if (w <= 0 || h <= 0) throw new InvalidDataException($"Invalid scaling target {w}x{h}.");
        if (Limits.TooLarge(w, h)) throw new InvalidDataException($"Scaling target {w}x{h} is too large.");
        if (f.Width > 16384 || f.Height > 16384)
            throw new NotSupportedException($"Scaling a {f.Width}x{f.Height} frame to {w}x{h} is not supported (libavif's 16384 limit).");
        int ssx = f.Format is Av1.PixelFormat.Yuv444P or Av1.PixelFormat.Yuv444P10 or Av1.PixelFormat.Yuv444P12 ? 0 : 1;
        int ssy = f.Format is Av1.PixelFormat.Yuv420P or Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12 ? 1 : 0;
        bool hbd = f.BitDepth > 8;
        int scw = (f.Width + ssx) >> ssx, sch = (f.Height + ssy) >> ssy;
        int dcw = (w + ssx) >> ssx, dch = (h + ssy) >> ssy;
        ushort[] Src(ReadOnlyMemory<byte> p8, ReadOnlyMemory<ushort> p16, int stride, int pw, int ph)
        {
            if (hbd) return p16.Span.ToArray();
            var a = new ushort[stride * ph];
            var s = p8.Span;
            for (int i = 0; i < a.Length && i < s.Length; i++) a[i] = s[i];
            return a;
        }
        var y = LibyuvScale.ScalePlane(Src(f.YPlane, f.YPlane16, f.YStride, f.Width, f.Height), f.YStride, f.Width, f.Height, w, h, hbd);
        // Monochrome (e.g. alpha) frames carry no native chroma: keep them neutral at the new size.
        bool mono = hbd && f.UPlane16.IsEmpty;
        ushort[] Neutral() { var n = new ushort[dcw * dch]; Array.Fill(n, (ushort)(1 << (f.BitDepth - 1))); return n; }
        var u = mono ? Neutral() : LibyuvScale.ScalePlane(Src(f.UPlane, f.UPlane16, f.UStride, scw, sch), f.UStride, scw, sch, dcw, dch, hbd);
        var v = mono ? Neutral() : LibyuvScale.ScalePlane(Src(f.VPlane, f.VPlane16, f.VStride, scw, sch), f.VStride, scw, sch, dcw, dch, hbd);
        int ySize = w * h, cSize = dcw * dch;
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(ySize + 2 * cSize);
        int bdShift = f.BitDepth - 8, bdRound = bdShift > 0 ? 1 << (bdShift - 1) : 0;
        void Put8(ushort[] p, int off) { for (int i = 0; i < p.Length; i++) buf[off + i] = (byte)Math.Min(255, (p[i] + bdRound) >> bdShift); }
        Put8(y, 0); Put8(u, ySize); Put8(v, ySize + cSize);
        ReadOnlyMemory<ushort> y16 = default, u16 = default, v16 = default;
        if (hbd)
        {
            var native = new ushort[ySize + 2 * cSize];
            y.CopyTo(native, 0); u.CopyTo(native, ySize); v.CopyTo(native, ySize + cSize);
            y16 = new ReadOnlyMemory<ushort>(native, 0, ySize);
            if (!mono)
            {
                u16 = new ReadOnlyMemory<ushort>(native, ySize, cSize);
                v16 = new ReadOnlyMemory<ushort>(native, ySize + cSize, cSize);
            }
        }
        return new Av1.DecodedVideoFrame(w, h, f.Format, f.PresentationTimeTicks, buf, 0, w, ySize, dcw, ySize + cSize, dcw)
        {
            BitDepth = f.BitDepth, YPlane16 = y16, UPlane16 = u16, VPlane16 = v16,
        };
    }

    // The alpha item's samples at their native depth (studio range expanded to full, as ApplyAv1Alpha does).
    private static ushort[] DecodeAlphaNative(HeifContainer c, int id)
    {
        byte[] coded = c.ItemData(id) ?? throw new InvalidDataException($"Alpha item {id} has no data.");
        using var yuv = DecodeAv1Item(c, id, coded, out var decoder, "AV1 alpha");
        return NativeAlpha(yuv, decoder);
    }

    // A premultiplied grid's alpha at native precision, its tiles stitched (and cropped) like the colour grid, so the
    // colour can be un-premultiplied during YUV->RGB exactly where libavif does it (on the reassembled image).
    private static ushort[] DecodeAlphaGridNative(HeifContainer c, List<int> aTiles, (int Rows, int Cols, int W, int H) grid, int outW, int outH)
    {
        var full = new ushort[outW * outH];
        int tw = 0, th = 0;
        for (int i = 0; i < aTiles.Count; i++)
        {
            byte[] coded = c.ItemData(aTiles[i]) ?? throw new InvalidDataException($"Alpha tile {aTiles[i]} has no data.");
            using var yuv = DecodeAv1Item(c, aTiles[i], coded, out var decoder, "AV1 alpha tile");
            var a = NativeAlpha(yuv, decoder);
            if (i == 0) { tw = yuv.Width; th = yuv.Height; }
            int x0 = (i % grid.Cols) * tw, y0 = (i / grid.Cols) * th;
            int cw = Math.Min(yuv.Width, outW - x0), chh = Math.Min(yuv.Height, outH - y0);
            for (int y = 0; y < chh; y++)
                a.AsSpan(y * yuv.Width, Math.Max(0, cw)).CopyTo(full.AsSpan((y0 + y) * outW + x0));
        }
        return full;
    }

    private static ushort[] NativeAlpha(Av1.DecodedVideoFrame yuv, Av1.Av1Decoder decoder)
    {
        int w = yuv.Width, h = yuv.Height, bd = yuv.BitDepth, max = (1 << bd) - 1, lo = 16 << (bd - 8), hi = 235 << (bd - 8);
        var a = new ushort[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = bd > 8 ? yuv.YPlane16.Span[y * yuv.YStride + x] : yuv.YPlane.Span[y * yuv.YStride + x];
                if (!decoder.FullColorRange) v = Math.Clamp(((v - lo) * max + (hi - lo) / 2) / (hi - lo), 0, max);
                a[y * w + x] = (ushort)v;
            }
        return a;
    }

    private static void ConvertYuvToRgbLibyuv8(Av1.DecodedVideoFrame yuv, ImageFrame frame, int w, int h, int channels,
        (int Yg, int Yb, int Ub, int Ug, int Vg, int Vr) k, int ssHor, int ssVer, bool nearest = false)
    {
        var ym = yuv.YPlane; var um = yuv.UPlane; var vm = yuv.VPlane;
        int ys = yuv.YStride, us = yuv.UStride, vs = yuv.VStride;
        // Rows (4:2:0: row pairs sharing two chroma rows) are independent: converted in parallel with per-thread
        // chroma scratch rows. Identical output for any thread count.
        var po = new ParallelOptions { MaxDegreeOfParallelism = Limits.ThreadCount };
        if (ssHor == 0)
        {
            Parallel.For(0, h, po, j => LibyuvRow(frame, ym.Span.Slice(j * ys, w), j, um.Span.Slice(j * us, w), vm.Span.Slice(j * vs, w), w, channels, k));
            return;
        }
        if (nearest)
        {
            // libyuv without a filter (I420/I422ToARGBMatrix): each chroma sample serves two columns (and two rows at 4:2:0).
            Parallel.For(0, h, po, () => (new byte[w], new byte[w]), (j, _, b) =>
            {
                var up = um.Span.Slice((j >> ssVer) * us); var vp = vm.Span.Slice((j >> ssVer) * vs);
                for (int x = 0; x < w; x++) { b.Item1[x] = up[x >> 1]; b.Item2[x] = vp[x >> 1]; }
                LibyuvRow(frame, ym.Span.Slice(j * ys, w), j, b.Item1, b.Item2, w, channels, k);
                return b;
            }, _ => { });
            return;
        }
        if (ssVer == 0)
        {
            Parallel.For(0, h, po, () => (new byte[w], new byte[w]), (j, _, b) =>
            {
                UpLinear(um.Span.Slice(j * us), b.Item1, w); UpLinear(vm.Span.Slice(j * vs), b.Item2, w);
                LibyuvRow(frame, ym.Span.Slice(j * ys, w), j, b.Item1, b.Item2, w, channels, k);
                return b;
            }, _ => { });
            return;
        }
        {
            var ur = new byte[w]; var vr = new byte[w];
            UpLinear(um.Span, ur, w); UpLinear(vm.Span, vr, w); LibyuvRow(frame, ym.Span.Slice(0, w), 0, ur, vr, w, channels, k);
        }
        int pairs = (h - 1) / 2;   // output rows 1+2i and 2+2i from chroma rows i and i+1
        Parallel.For(0, pairs, po, () => (new byte[w], new byte[w], new byte[w], new byte[w]), (cj, _, b) =>
        {
            int jj = 1 + 2 * cj;
            ReadOnlySpan<byte> up = um.Span, vp = vm.Span, yp = ym.Span;
            UpBilinear(up.Slice(cj * us), up.Slice((cj + 1) * us), b.Item1, b.Item3, w);
            UpBilinear(vp.Slice(cj * vs), vp.Slice((cj + 1) * vs), b.Item2, b.Item4, w);
            LibyuvRow(frame, yp.Slice(jj * ys, w), jj, b.Item1, b.Item2, w, channels, k);
            LibyuvRow(frame, yp.Slice((jj + 1) * ys, w), jj + 1, b.Item3, b.Item4, w, channels, k);
            return b;
        }, _ => { });
        if ((h & 1) == 0)
        {
            int cj = pairs;
            var ur = new byte[w]; var vr = new byte[w];
            UpLinear(um.Span.Slice(cj * us), ur, w); UpLinear(vm.Span.Slice(cj * vs), vr, w);
            LibyuvRow(frame, ym.Span.Slice((h - 1) * ys, w), h - 1, ur, vr, w, channels, k);
        }
    }

    private static void LibyuvRow(ImageFrame frame, ReadOnlySpan<byte> yRow, int j, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int w, int channels,
        (int Yg, int Yb, int Ub, int Ug, int Vg, int Vr) k)
    {
        var row = frame.GetPixelRowForWrite(j);
        for (int i = 0; i < w; i++)
        {
            int y1 = (int)((uint)(yRow[i] * 0x0101 * k.Yg) >> 16) + k.Yb;   // x86 CALC_RGB16
            int ui = u[i] - 128, vi = v[i] - 128;
            int off = i * channels;
            row[off] = (ushort)(Math.Clamp((y1 + vi * k.Vr) >> 6, 0, 255) * 257);
            if (channels >= 3)
            {
                row[off + 1] = (ushort)(Math.Clamp((y1 - (ui * k.Ug + vi * k.Vg)) >> 6, 0, 255) * 257);
                row[off + 2] = (ushort)(Math.Clamp((y1 + ui * k.Ub) >> 6, 0, 255) * 257);
            }
        }
    }

    // libyuv ScaleRowUp2_Linear_Any_C: edge samples copied, interior (3a+b+2)>>2 / (a+3b+2)>>2.
    private static void UpLinear(ReadOnlySpan<byte> s, byte[] d, int dw)
    {
        d[0] = s[0];
        int ww = (dw - 1) & ~1;
        for (int x = 0; x < ww / 2; x++)
        {
            d[1 + 2 * x] = (byte)((s[x] * 3 + s[x + 1] + 2) >> 2);
            d[2 + 2 * x] = (byte)((s[x] + s[x + 1] * 3 + 2) >> 2);
        }
        d[dw - 1] = s[(dw - 1) / 2];
    }

    // libyuv ScaleRowUp2_Bilinear_Any_C: two output rows from chroma rows sa/sb; 9/3/3/1 interior, 3/1 edges.
    private static void UpBilinear(ReadOnlySpan<byte> sa, ReadOnlySpan<byte> sb, byte[] da, byte[] db, int dw)
    {
        da[0] = (byte)((3 * sa[0] + sb[0] + 2) >> 2);
        db[0] = (byte)((sa[0] + 3 * sb[0] + 2) >> 2);
        int ww = (dw - 1) & ~1;
        for (int x = 0; x < ww / 2; x++)
        {
            int s0 = sa[x], s1 = sa[x + 1], t0 = sb[x], t1 = sb[x + 1];
            da[1 + 2 * x] = (byte)((s0 * 9 + s1 * 3 + t0 * 3 + t1 + 8) >> 4);
            da[2 + 2 * x] = (byte)((s0 * 3 + s1 * 9 + t0 + t1 * 3 + 8) >> 4);
            db[1 + 2 * x] = (byte)((s0 * 3 + s1 + t0 * 9 + t1 * 3 + 8) >> 4);
            db[2 + 2 * x] = (byte)((s0 + s1 * 3 + t0 * 3 + t1 * 9 + 8) >> 4);
        }
        int kk = (dw - 1) / 2;
        da[dw - 1] = (byte)((3 * sa[kk] + sb[kk] + 2) >> 2);
        db[dw - 1] = (byte)((sa[kk] + 3 * sb[kk] + 2) >> 2);
    }

    private static float S(ReadOnlySpan<byte> p8, ReadOnlySpan<ushort> p16, bool hbd, int k, int maxCh, float[] tab)
        => tab[Math.Min(hbd ? p16[k] : p8[k], maxCh)];

    private static ushort Q16f(float v) => (ushort)(0.5f + Math.Clamp(v, 0.0f, 1.0f) * 65535.0f);

    private static ushort QNative(float v, int max)
    {
        uint q = (uint)(0.5f + Math.Clamp(v, 0.0f, 1.0f) * max);
        return (ushort)((q * 65535u + (uint)max / 2) / (uint)max);
    }

    private static ushort Q16(double n) => (ushort)Math.Clamp((int)Math.Round(n * 65535.0), 0, 65535);

    private static byte[] EncodeAv1IntraFrame(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;

        var obus = new List<byte>();

        // OBU: Sequence Header
        var seqHeader = new List<byte>();
        var shBits = new SimpleBitWriter();
        shBits.Write(3, 0); // seq_profile = 0 (main)
        shBits.Write(1, 1); // still_picture = true
        shBits.Write(1, 1); // reduced_still_picture_header = true
        shBits.Write(5, 0); // seq_level_idx = 0

        int wBits = BitsNeeded(w - 1);
        int hBits = BitsNeeded(h - 1);
        shBits.Write(4, (uint)(wBits - 1));
        shBits.Write(4, (uint)(hBits - 1));
        shBits.Write(wBits, (uint)(w - 1));
        shBits.Write(hBits, (uint)(h - 1));

        shBits.Write(1, 0); // use_128_intra_default = false
        shBits.Write(1, 0); // enable_filter_intra = false
        shBits.Write(1, 0); // enable_intra_edge_filter = false
        shBits.Write(1, 0); // enable_superres = false
        shBits.Write(1, 0); // enable_cdef = false
        shBits.Write(1, 0); // enable_restoration = false
        // Color config
        shBits.Write(1, 0); // high_bitdepth = false (8-bit)
        shBits.Write(1, 0); // mono_chrome = false
        shBits.Write(1, 0); // color_description_present = false
        shBits.Write(1, 0); // color_range = studio
        shBits.Write(2, 0); // subsampling_x, subsampling_y = 0,0 (4:4:4)
        shBits.Write(1, 0); // film_grain_params_present = false
        shBits.Flush();

        byte[] seqData = shBits.GetBytes();
        WriteObu(obus, 1, seqData); // OBU_SEQUENCE_HEADER

        // OBU: Frame (simplified intra-only with DC prediction)
        int blockW = (w + 7) / 8;
        int blockH = (h + 7) / 8;

        // Convert to YUV and encode DC values per 8x8 block
        var frameBytes = new List<byte>();
        byte[][] blockDc = new byte[3][];
        for (int plane = 0;plane < 3;plane++)
        {
            blockDc[plane] = new byte[blockW * blockH];
        }

        for (int by = 0;by < blockH;by++)
        {
            for (int bx = 0;bx < blockW;bx++)
            {
                double sumY = 0, sumU = 0, sumV = 0;
                int count = 0;
                for (int dy = 0;dy < 8 && by * 8 + dy < h;dy++)
                {
                    var row = image.GetPixelRow(by * 8 + dy);
                    for (int dx = 0;dx < 8 && bx * 8 + dx < w;dx++)
                    {
                        int x = bx * 8 + dx;
                        int off = x * imgChannels;
                        byte r = Quantum.ScaleToByte(row[off]);
                        byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[off + 1]) : r;
                        byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[off + 2]) : r;

                        sumY += 0.299 * r + 0.587 * g + 0.114 * b;
                        sumU += -0.169 * r - 0.331 * g + 0.500 * b + 128;
                        sumV += 0.500 * r - 0.419 * g - 0.081 * b + 128;
                        count++;
                    }
                }
                int idx = by * blockW + bx;
                blockDc[0][idx] = (byte)Math.Clamp(sumY / count, 0, 255);
                blockDc[1][idx] = (byte)Math.Clamp(sumU / count, 0, 255);
                blockDc[2][idx] = (byte)Math.Clamp(sumV / count, 0, 255);
            }
        }

        for (int plane = 0;plane < 3;plane++)
        {
            frameBytes.AddRange(blockDc[plane]);
        }

        WriteObu(obus, 6, frameBytes.ToArray()); // OBU_FRAME

        return obus.ToArray();
    }

    #endregion

    #region HEVC Intra Frame Codec

    private static void DecodeHevcIntraFrame(ReadOnlySpan<byte> codedData, ImageFrame frame, byte[] hvcC, int matrixCoeffs, bool fullRange)
    {
        // Decode the HEVC keyframe with the vendored HEVC decoder. HEIC keeps the parameter
        // sets (VPS/SPS/PPS) in the hvcC configuration box and the coded slice NALs in the
        // item's data, so configure from hvcC first, then decode the slice.
        var decoder = new Hevc.HevcDecoder();
        decoder.Initialize(hvcC);
        using var yuv = decoder.Decode(codedData, 0, isKeyframe: true)
            ?? throw new InvalidDataException("HEVC decode produced no frame.");

        int w = (int)frame.Columns;
        int h = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        bool tenBit = yuv.Format is Hevc.PixelFormat.Yuv420P10 or Hevc.PixelFormat.Yuv420P12;
        int shift = tenBit ? (yuv.Format == Hevc.PixelFormat.Yuv420P12 ? 4 : 2) : 0;
        ConvertYuvToRgb(yuv.YPlane.Span, yuv.UPlane.Span, yuv.VPlane.Span, yuv.YStride, yuv.UStride, yuv.VStride,
            frame, w, h, channels, tenBit, shift, matrixCoeffs == 1, fullRange);
    }

    private static byte[] EncodeHevcIntraFrame(ImageFrame image)
    {
        // Simplified HEVC intra encoding: DC-only prediction per 8x8 CTU
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int blockW = (w + 7) / 8;
        int blockH = (h + 7) / 8;

        var output = new List<byte>();

        // VPS NAL unit (minimal)
        byte[] vps = [ 0x40, 0x01, 0x0C, 0x01, 0xFF, 0xFF, 0x01, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 ];
        WriteNalUnit(output, vps);

        // SPS NAL unit (minimal with dimensions)
        var sps = new List<byte>();
        sps.AddRange(new byte[] { 0x42, 0x01, 0x01 }); // NAL header + profile
        sps.Add(0x01); // general_profile_space
        // Encode width/height in SPS (simplified)
        sps.Add((byte)(w >> 8));
        sps.Add((byte)(w & 0xFF));
        sps.Add((byte)(h >> 8));
        sps.Add((byte)(h & 0xFF));
        WriteNalUnit(output, sps.ToArray());

        // PPS NAL unit (minimal)
        byte[] pps = [ 0x44, 0x01, 0xC0 ];
        WriteNalUnit(output, pps);

        // IDR slice with DC-coded blocks
        var slice = new List<byte>();
        slice.AddRange(new byte[] { 0x26, 0x01 }); // NAL header (IDR_W_RADL)

        // Encode Y, U, V DC blocks
        for (int plane = 0;plane < 3;plane++)
        {
            for (int by = 0;by < blockH;by++)
            {
                for (int bx = 0;bx < blockW;bx++)
                {
                    double sum = 0;
                    int count = 0;
                    for (int dy = 0;dy < 8 && by * 8 + dy < h;dy++)
                    {
                        var row = image.GetPixelRow(by * 8 + dy);
                        for (int dx = 0;dx < 8 && bx * 8 + dx < w;dx++)
                        {
                            int x = bx * 8 + dx;
                            int off = x * imgChannels;
                            byte r = Quantum.ScaleToByte(row[off]);
                            byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[off + 1]) : r;
                            byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[off + 2]) : r;

                            sum += plane switch
                            {
                                0 => 0.299 * r + 0.587 * g + 0.114 * b,
                                1 => -0.169 * r - 0.331 * g + 0.500 * b + 128,
                                _ => 0.500 * r - 0.419 * g - 0.081 * b + 128
                            };
                            count++;
                        }
                    }
                    slice.Add((byte)Math.Clamp(sum / count, 0, 255));
                }
            }
        }

        WriteNalUnit(output, slice.ToArray());

        return output.ToArray();
    }

    #endregion

    #region ISOBMFF Helpers

    private static void WriteFtypBox(List<byte> output, string brand)
    {
        byte[] data = new byte[8];
        Encoding.ASCII.GetBytes(brand, data.AsSpan(0, 4)); // major_brand
        // minor_version = 0
        Encoding.ASCII.GetBytes(brand, data.AsSpan(4, 4)); // compatible_brand
        WriteBox(output, "ftyp", data);
    }

    private static void WriteMetaBox(List<byte> output, int w, int h, int dataLength,
        HeifContainerType containerType)
    {
        var meta = new List<byte>();
        meta.AddRange(new byte[4]); // version + flags

        // hdlr (handler) box
        var hdlr = new List<byte>();
        hdlr.AddRange(new byte[4]); // version + flags
        hdlr.AddRange(new byte[4]); // pre_defined
        hdlr.AddRange(Encoding.ASCII.GetBytes("pict")); // handler_type
        hdlr.AddRange(new byte[12]); // reserved
        hdlr.Add(0); // name (null terminated)
        WriteBoxTo(meta, "hdlr", hdlr.ToArray());

        // pitm (primary item) box
        var pitm = new List<byte>();
        pitm.AddRange(new byte[4]); // version + flags
        pitm.Add(0);
        pitm.Add(1); // item_ID = 1
        WriteBoxTo(meta, "pitm", pitm.ToArray());

        // iprp (item properties) box
        var iprp = new List<byte>();
        var ipco = new List<byte>();

        // ispe (image spatial extents)
        byte[] ispe = new byte[12];
        // version + flags = 0
        BinaryPrimitives.WriteUInt32BigEndian(ispe.AsSpan(4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ispe.AsSpan(8), (uint)h);
        WriteBoxTo(ipco, "ispe", ispe);

        WriteBoxTo(iprp, "ipco", ipco.ToArray());

        // ipma (item property association)
        byte[] ipma = [ 0, 0, 0, 0, 0, 1, 0, 1, 1, 0x81 ]; // item 1, 1 association, property 1
        WriteBoxTo(iprp, "ipma", ipma);

        WriteBoxTo(meta, "iprp", iprp.ToArray());

        // iloc (item location) box
        var iloc = new List<byte>();
        iloc.AddRange(new byte[] { 0, 0, 0, 0 }); // version + flags
        iloc.Add(0x44); // offset_size=4, length_size=4
        iloc.Add(0x00); // base_offset_size=0, index_size=0
        iloc.Add(0);
        iloc.Add(1); // item_count = 1
        iloc.Add(0);
        iloc.Add(1); // item_ID = 1
        iloc.Add(0);
        iloc.Add(0); // data_reference_index = 0
        iloc.Add(0);
        iloc.Add(1); // extent_count = 1
        // extent_offset (4 bytes) — offset within mdat data
        byte[] offBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(offBytes, 0);
        iloc.AddRange(offBytes);
        // extent_length
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)dataLength);
        iloc.AddRange(lenBytes);
        WriteBoxTo(meta, "iloc", iloc.ToArray());

        WriteBox(output, "meta", meta.ToArray());
    }

    private static void WriteBox(List<byte> output, string type, byte[] data)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + data.Length));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        output.AddRange(header);
        output.AddRange(data);
    }

    private static void WriteBoxTo(List<byte> target, string type, byte[] data)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + data.Length));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        target.AddRange(header);
        target.AddRange(data);
    }

    private static void WriteNalUnit(List<byte> output, byte[] nal)
    {
        byte[] len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)nal.Length);
        output.AddRange(len);
        output.AddRange(nal);
    }

    private static void WriteObu(List<byte> output, int obuType, byte[] data)
    {
        // OBU header: type(4 bits) | has_extension(1) | has_size(1) | reserved(1)
        byte header = (byte)((obuType << 3) | 0x02); // has_size = true
        output.Add(header);
        // LEB128 size
        WriteLeb128(output, data.Length);
        output.AddRange(data);
    }

    private static void WriteLeb128(List<byte> output, int value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                b |= 0x80;
            }

            output.Add(b);
        }
        while (value > 0);
    }

    private static long ReadVarInt(byte[] data, int offset, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        if (size == 2 && offset + 2 <= data.Length)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        }

        if (size == 4 && offset + 4 <= data.Length)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        }

        if (size == 8 && offset + 8 <= data.Length)
        {
            return (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset));
        }

        return 0;
    }

    private static bool IsAvifBrand(string brand) => AvifBrands.Any(b => brand.StartsWith(b));

    private static bool IsHeicBrand(string brand) => HeicBrands.Any(b => brand.StartsWith(b));

    private static int BitsNeeded(int value)
    {
        int bits = 1;
        while ((1 << bits) <= value)
        {
            bits++;
        }

        return bits;
    }

    #endregion

    #region Simple Bit I/O

    private sealed class SimpleBitWriter
    {
        private readonly List<byte> buffer = new();
        private byte current;
        private int bitPos = 7;

        public void Write(int numBits, uint value)
        {
            for (int i = numBits - 1;i >= 0;i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    current |= (byte)(1 << bitPos);
                }

                bitPos--;
                if (bitPos < 0)
                {
                    buffer.Add(current);
                    current = 0;
                    bitPos = 7;
                }
            }
        }

        public void Flush()
        {
            if (bitPos < 7)
            {
                buffer.Add(current);
            }
        }

        public byte[] GetBytes() => buffer.ToArray();
    }

    #endregion
}

