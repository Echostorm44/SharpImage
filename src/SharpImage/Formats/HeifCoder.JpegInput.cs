using System.Runtime.CompilerServices;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    // The YUV planes a frame was decoded from (a JPEG's raw components, a decoded AVIF gain map item): when the frame is
    // coded at their depth, layout, matrix and range, they are coded as they are instead of going YUV -> RGB -> YUV
    // (libavif avifJPEGReadCopy; avifImageCopy of a gain map's planes in avifgainmaputil swapbase).
    private sealed class SourceYuv
    {
        public required Lazy<ushort[][]> Planes;
        public required int Width, Height, Depth;
        public required Av1.Av1PixelLayout Layout;
        public required int[] Matrices;
        public required bool FullRange;
    }

    private static readonly ConditionalWeakTable<ImageFrame, SourceYuv> SourcePlanes = new();

    private static void AttachJpegPlanes(ImageFrame image, JpegRawYuv raw) => SourcePlanes.AddOrUpdate(image, new SourceYuv
    {
        Planes = new Lazy<ushort[][]>(() => raw.Planes.Select(Widen).ToArray()),
        Width = raw.Width, Height = raw.Height, Depth = 8, Layout = raw.Layout, Matrices = [5, 6], FullRange = true,
    });

    /// <summary>
    /// Encodes a JPEG file as AVIF the way <c>avifenc</c> reads JPEG input: the decoded YCbCr planes are coded as they
    /// are (libjpeg-turbo accurate IDCT, no colour conversion) whenever the options allow it — 8-bit, full range,
    /// BT.601 matrix, and the JPEG's own 4:4:4 / 4:2:2 / 4:2:0 layout, which <see cref="AvifChromaSubsampling.Auto"/>
    /// adopts — otherwise libjpeg-turbo's RGB decode goes through the normal RGB -> YUV path. ICC, Exif and XMP (with
    /// extended XMP merged) are kept. Unless <paramref name="ignoreGainMap"/> is set (avifenc --ignore-gain-map) or
    /// <see cref="AvifEncodeOptions.GainMap"/> is already set, an Ultra HDR / Adobe hdrgm or Apple HDR gain map
    /// (<see cref="JpegCoder.ReadGainMap(byte[])"/>) is carried over as an ISO 21496-1 gain map, its planes copied the
    /// same way.
    /// <para><paramref name="swapBase"/> is <c>avifgainmaputil convert --swap-base</c>: the HDR rendition (the base with
    /// the gain map applied) becomes the base image — <see cref="AvifEncodeOptions.BitDepth"/> 0 picks 10 bits (8 when
    /// the alternate is SDR), <see cref="AvifChromaSubsampling.Auto"/> 4:4:4 — and the SDR JPEG the alternate. As in
    /// libavif, a JPEG with an ICC profile needs <paramref name="ignoreIccProfile"/>.</para>
    /// </summary>
    public static byte[] EncodeAvifFromJpeg(byte[] jpeg, AvifEncodeOptions? options = null, bool ignoreGainMap = false,
        bool swapBase = false, bool ignoreIccProfile = false)
    {
        var o = (options ?? new AvifEncodeOptions()).Clone();
        var image = JpegCoder.Read(new MemoryStream(jpeg));
        if (ignoreIccProfile)
        {
            image.IccProfile = null;
            image.Metadata.IccProfile = null;
        }
        var raw = JpegCoder.ReadRawYuv(jpeg);
        if (raw != null)
        {
            CopyPixels(JpegCoder.LibjpegRgb(raw), image);   // libjpeg-turbo's RGB, for whatever cannot copy the planes
            if (!swapBase)
            {
                AttachJpegPlanes(image, raw);
                if (o.ChromaSubsampling == AvifChromaSubsampling.Auto && !o.Lossless) o.ChromaSubsampling = ToSubsampling(raw.Layout);
            }
        }

        AvifGainMap? gm = null;
        JpegRawYuv? gmRaw = null;
        if ((!ignoreGainMap || swapBase) && o.GainMap == null)
            gm = JpegCoder.ReadGainMap(jpeg, (_, r) => gmRaw = r);
        if (gm != null && gmRaw != null)
        {
            // libavif keeps the gain map as the JPEG's YCbCr (BT.601, full range); the gain map math converts it with
            // avifImageYUVToRGB, rescaling the planes first when the sizes differ.
            var gmCicp = new CicpInfo(2, 2, 6, true);
            var captured = gmRaw;
            gm.Image = RgbFromPlanes8(captured, gmCicp);
            gm.ScaledImage = (w, h) => RgbFromPlanes8(captured, gmCicp, (w, h));
            AttachJpegPlanes(gm.Image, captured);
            o.GainMapChromaSubsampling = ToSubsampling(captured.Layout);
        }

        byte[]? icc = image.IccProfile ?? image.Metadata.IccProfile?.Data;
        if (swapBase)
        {
            if (gm == null) throw new ArgumentException("The JPEG does not contain a gain map.", nameof(jpeg));
            if (icc != null) throw new NotSupportedException("Tone mapping for images with ICC profiles is not supported (set ignoreIccProfile).");
            // avifgainmaputil reads the JPEG with the matrix unspecified: no plane copy (except greyscale), RGB -> YUV
            // (BT.601 coefficients) in the requested layout, sRGB assumed.
            var cicp = new CicpInfo(o.ColorPrimaries ?? 1, o.TransferCharacteristics ?? 13, 6, true);
            o.ColorPrimaries = o.TransferCharacteristics = o.MatrixCoefficients = null;
            bool mono = raw?.Layout == Av1.Av1PixelLayout.I400;
            var layout = mono ? Av1.Av1PixelLayout.I400 : o.ChromaSubsampling switch
            {
                AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
                AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
                _ => Av1.Av1PixelLayout.I444,
            };
            var baseRgb = mono ? RgbFromPlanes8(raw!, cicp) : RoundTripYuv(image, cicp, layout);
            baseRgb.Metadata = image.Metadata;
            baseRgb.Orientation = image.Orientation;
            baseRgb.Metadata.Cicp = cicp;
            baseRgb.Depth = 8;
            gm.AlternateCicp = new CicpInfo(cicp.ColorPrimaries, 16, 2, true);
            gm.AlternateIccProfile = null;
            if (o.ChromaSubsampling == AvifChromaSubsampling.Auto) o.ChromaSubsampling = ToSubsampling(layout);
            int depth = o.BitDepth != 0 ? o.BitDepth : gm.AlternateHdrHeadroom.Numerator == 0 ? 8 : 10;
            return EncodeSwapped(baseRgb, gm, depth, mono, o);
        }

        if (gm != null)
        {
            // avifenc: the alternate rendition is assumed to be PQ HDR with the base image's primaries and matrix — or,
            // when the base carries an ICC profile, that profile (primaries left unspecified).
            var baseColor = ResolveAvifColor(image, o, 8);
            gm.AlternateCicp = new CicpInfo(icc != null ? 2 : baseColor.Primaries, 16, baseColor.Matrix, true);
            gm.AlternateIccProfile = icc;
            o.GainMap = gm;
        }
        return EncodeAvif(image, o);
    }

    private static AvifChromaSubsampling ToSubsampling(Av1.Av1PixelLayout l) => l switch
    {
        Av1.Av1PixelLayout.I420 => AvifChromaSubsampling.Yuv420,
        Av1.Av1PixelLayout.I422 => AvifChromaSubsampling.Yuv422,
        Av1.Av1PixelLayout.I400 => AvifChromaSubsampling.Yuv400,
        _ => AvifChromaSubsampling.Yuv444,
    };

    private static void CopyPixels(ImageFrame src, ImageFrame dst)
    {
        int h = (int)dst.Rows, n = (int)dst.Columns * dst.NumberOfChannels;
        for (int y = 0; y < h; y++) src.GetPixelRow(y)[..n].CopyTo(dst.GetPixelRowForWrite(y));
        dst.Depth = 8;
    }

    // The source planes to code for this frame, or null when the coded format differs from what they hold.
    private static SourceYuv? SourcePlanesFor(ImageFrame image, int bd, Av1.Av1ObuWriter.Av1ColorDesc color, int w, int h) =>
        SourcePlanes.TryGetValue(image, out var src) && src.Depth == bd && src.FullRange == color.FullRange
        && src.Matrices.Contains(color.Matrix) && src.Width == w && src.Height == h ? src : null;

    private static ushort[] Widen(byte[] p)
    {
        var r = new ushort[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = p[i];
        return r;
    }
}
