using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    // avifenc --progressive: the base layer at quality 10, which libaom codes at half size
    private const int ProgressiveBaseQuality = 10;

    /// <summary>One layer as libavif hands it to libaom: the full-size source, its quality / alpha quality and the
    /// AOME_SET_SCALEMODE fraction.</summary>
    private readonly record struct AomLayerSpec(ImageFrame Source, int? Quality, int? QualityAlpha, int ScaleN, int ScaleD);

    // avifFindAOMScalingMode: libavif's fractions to AOM_SCALING_MODE (AOME_NORMAL 0 = no scaling)
    private static int AomScalingMode(int n, int d)
    {
        int g = Gcd(n, d);
        return (n / g, d / g) switch
        {
            (1, 1) => 0, (4, 5) => 1, (3, 5) => 2, (3, 4) => 3, (1, 4) => 4, (1, 8) => 5, (1, 2) => 6,
            _ => throw new ArgumentException($"Unsupported scaling mode {n}/{d}."),
        };
    }

    /// <summary>
    /// A layered (progressive) still through the libaom port's good-quality encoder the way libavif 1.4.2 encodes it:
    /// one aom_codec_encode per layer with AOME_SET_SPATIAL_LAYER_ID and AOME_SET_SCALEMODE (lag 0, g_limit = layers,
    /// AOME_SET_NUMBER_SPATIAL_LAYERS, use_fixed_qp_offsets 2, the upper layers referencing only LAST), every packet one
    /// layer of the item ('a1lx' sizes), the alpha item encoded the same way. Returns null for what this path lacks
    /// (lossless, film grain / denoising, gain maps, premultiplied alpha, real-time speeds 7..10).
    /// </summary>
    private static byte[]? TryEncodeLayersAom(ImageFrame image, AvifEncodeOptions options, IReadOnlyList<AomLayerSpec> layers,
        bool reapplyCodecOptions)
    {
        // a libaom path not ported yet falls back to SharpImage's own layered encoder
        try { return TryEncodeLayersAomCore(image, options, layers, reapplyCodecOptions); }
        catch (NotImplementedException) { return null; }
    }

    private static byte[]? TryEncodeLayersAomCore(ImageFrame image, AvifEncodeOptions options, IReadOnlyList<AomLayerSpec> layers,
        bool reapplyCodecOptions)
    {
        int speed = Math.Clamp(options.Speed, 0, 9);
        if (speed >= 7 || options.Lossless || options.FilmGrain != null || options.DenoiseNoiseLevel > 0 || options.GainMap != null ||
            options.PremultiplyAlpha || options.SharpYuv)
            return null;
        if (layers.Any(l => l.Quality >= 100 || l.QualityAlpha >= 100)) return null;
        int w = (int)image.Columns, h = (int)image.Rows;

        int bd = options.BitDepth == 0 ? AutoBitDepth(image) : options.BitDepth;
        if (bd is not (8 or 10 or 12)) throw new ArgumentOutOfRangeException(nameof(options), "AVIF bit depth must be 0 (auto), 8, 10 or 12.");
        var color = ResolveAvifColor(image, options, bd);
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0) color = color with { Matrix = 6 };
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1PixelLayout.I400,
            _ => color.Matrix == 0 ? Av1PixelLayout.I444 : Av1PixelLayout.I420,
        };
        if (color.Matrix == 0 && layout != Av1PixelLayout.I444)
            throw new ArgumentException("The identity matrix (MatrixCoefficients 0) requires 4:4:4 chroma.", nameof(options));

        var planes = new List<(double[] R, double[] G, double[] B, ushort[]? A)>();
        bool anyColour = false, anyTranslucent = false;
        foreach (var l in layers)
        {
            ReadRgbPlanes(l.Source, bd, out var r, out var g, out var b, out var a, out bool colour, out bool nonOpaque);
            anyColour |= colour;
            anyTranslucent |= a != null;   // libavif: layered images code alpha whenever there is an alpha plane
            planes.Add((r, g, b, a));
        }
        bool mono = layout == Av1PixelLayout.I400 || (!anyColour && color.Matrix is not (0 or 16 or 17));
        var coded = mono ? Av1PixelLayout.I400 : layout;
        int max = (1 << bd) - 1;
        int cw = coded == Av1PixelLayout.I444 ? w : (w + 1) >> 1, ch = coded == Av1PixelLayout.I420 ? (h + 1) >> 1 : h;

        // libavif's default tune: iq for layered colour (ssim for the identity matrix), psnr for alpha
        AomTune Tune(bool alpha) => options.Tune switch
        {
            AvifTune.Iq => AomTune.Iq,
            AvifTune.Psnr => AomTune.Psnr,
            AvifTune.Ssim => AomTune.Ssim,
            _ => alpha ? AomTune.Psnr : color.Matrix == 0 ? AomTune.Ssim : AomTune.Iq,
        };
        int Quantizer(int? quality, bool tuneIq)
        {
            int q = quality ?? (options.Qp is { } qp ? -1 : AvifEncodeOptions.DefaultQuality);
            if (q < 0) return Math.Clamp((int)Math.Round(options.Qp!.Value * 63 / 51.0), 1, 63);
            q = Math.Clamp(q, 0, 100);
            return tuneIq ? TuneIqQualityToQuantizer[q] : ((100 - q) * 63 + 50) / 100;
        }
        var (tileCols, tileRows) = ResolveTiling(options, w, h);
        int threads = options.MaxThreads == 0 ? Environment.ProcessorCount : options.MaxThreads;
        AomGqConfig Config(bool alpha) => new()
        {
            Width = w, Height = h,
            SsX = alpha || mono || coded != Av1PixelLayout.I444 ? 1 : 0, SsY = alpha || mono || coded == Av1PixelLayout.I420 ? 1 : 0,
            Monochrome = alpha || mono, BitDepth = bd, Speed = speed, Tune = Tune(alpha), Threads = Math.Min(threads, 64),
            TileColumnsLog2 = tileCols, TileRowsLog2 = tileRows, LagInFrames = 0, Limit = layers.Count, NumSpatialLayers = layers.Count,
            UseFixedQpOffsets = 2, EnableRestoration = bd != 12, Sharpness = options.Sharpness, EnableCdef = options.EnableCdef,
            ReapplyCodecOptions = reapplyCodecOptions,
            Color = alpha
                ? new AomSequenceConfig { ColorRange = 1 }
                : new AomSequenceConfig
                {
                    ColorPrimaries = color.Primaries, TransferCharacteristics = color.Transfer, MatrixCoefficients = color.Matrix,
                    ColorRange = color.FullRange ? 1 : 0, ChromaSamplePosition = SourceChromaPosition(image, options),
                },
        };
        static byte[] B(ushort[] p) { var r = new byte[p.Length]; for (int i = 0; i < p.Length; i++) r[i] = (byte)p[i]; return r; }
        AomGqFrameInput Input(ushort[] y, ushort[]? u, ushort[]? v, bool monochrome, int quantizer, int layer, int scaleMode)
        {
            var fi = new AomGqFrameInput
            {
                Strides = monochrome ? [w] : [w, cw, cw], Quantizer = quantizer, SpatialLayerId = layer, ScaleModeH = scaleMode, ScaleModeV = scaleMode,
                Flags = layer > 0 ? AomGqEncoder.FlagsLayerUpper : 0,
            };
            if (bd > 8) fi.Planes16 = monochrome ? [y] : [y, u!, v!];
            else fi.Planes = monochrome ? [B(y)] : [B(y), B(u!), B(v!)];
            return fi;
        }
        (byte[] Data, long[] Sizes) Run(bool alpha)
        {
            var enc = new AomGqEncoder(Config(alpha));
            var packets = new List<byte[]>();
            bool tuneIq = Tune(alpha) == AomTune.Iq;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                var (r, g, b, a) = planes[i];
                ushort[] y;
                ushort[]? u = null, v = null;
                if (alpha) y = a ?? Enumerable.Repeat((ushort)max, w * h).ToArray();
                else if (mono)
                    y = anyColour ? MonoLumaLibavif(r, g, b, w, h, bd, color, l.Source.Depth is >= 1 and <= 16 ? l.Source.Depth : 16)
                        : GreyLumaLibavif(l.Source, bd, color.FullRange, SourceRgbDepth(l.Source), false);
                else if (SourcePlanesFor(l.Source, bd, color, w, h) is { } sp && sp.Layout == coded)
                {
                    y = sp.Planes.Value[0].ToArray(); u = sp.Planes.Value[1].ToArray(); v = sp.Planes.Value[2].ToArray();   // kept JPEG / Y4M planes
                }
                else
                {
                    RgbToYuvLibavif(l.Source, bd, coded, color, SourceRgbDepth(l.Source), false, r, g, b, out y, out var u0, out var v0);
                    u = u0;
                    v = v0;
                }
                int quantizer = Quantizer(alpha ? l.QualityAlpha : l.Quality, tuneIq);
                foreach (var p in enc.Encode(Input(y, u, v, alpha || mono, quantizer, i, AomScalingMode(l.ScaleN, l.ScaleD)))) packets.Add(p.Data);
            }
            for (;;)
            {
                var ps = enc.Encode(null);
                if (ps.Count == 0) break;
                foreach (var p in ps) packets.Add(p.Data);
            }
            if (packets.Count != layers.Count) throw new InvalidOperationException("The encoder output a different number of layers.");
            var data = new byte[packets.Sum(p => p.Length)];
            int o = 0;
            foreach (var p in packets) { p.CopyTo(data, o); o += p.Length; }
            return (data, packets.Select(p => (long)p.Length).ToArray());
        }

        var extras = AvifExtras(image, options);
        extras.Premultiplied = false;
        var (cData, cSizes) = Run(false);
        extras.ColorLayerSizes = cSizes;
        if (!anyTranslucent) return Av1AvifWriter.BuildAvif(cData, [], w, h, mono, bd, coded, color, extras);
        var (aData, aSizes) = Run(true);
        extras.AlphaLayerSizes = aSizes;
        return Av1AvifWriter.BuildAvifWithAlpha(cData, [], aData, [], w, h, mono, bd, coded, color, extras);
    }

    /// <summary>avifenc --progressive: two layers, the base at quality 10 scaled to half size, then the full image at
    /// the target quality (alpha keeps its quality on both).</summary>
    private static byte[]? TryEncodeProgressiveAom(ImageFrame image, AvifEncodeOptions options)
    {
        int target = options.Quality ?? AvifEncodeOptions.DefaultQuality;
        int? alphaQuality = options.QualityAlpha ?? options.Quality;
        var layers = new[]
        {
            new AomLayerSpec(image, ProgressiveBaseQuality, alphaQuality, 1, 2),
            new AomLayerSpec(image, target, alphaQuality, 1, 1),
        };
        return TryEncodeLayersAom(image, options, layers, reapplyCodecOptions: false);
    }
}
