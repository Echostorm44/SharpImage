using SharpImage.Formats.Av1;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// An image sequence through the libaom port's good-quality encoder (AomGqEncoder) the way libavif 1.4.2's
    /// codec_aom.c drives libaom: one encoder per track, aom_codec_encode(img, pts 0, duration 1) for each frame, every
    /// AOM_CODEC_CX_FRAME_PKT a sample (sync = AOM_FRAME_IS_KEY), then flushed. The colour track uses libavif's default
    /// tune=ssim (alpha tune=psnr), quantizer from the quality, lag 35 (0 with alpha: disableLaggedOutput), kf_max_dist
    /// from the keyframe interval; an alpha keyframe is forced whenever the colour frame just output was one.
    /// Returns false when the sequence needs a path this one lacks (lossless, film grain / denoising, real-time
    /// speeds 7..10).
    /// </summary>
    private static bool TryEncodeSequenceAom(AvifEncodeOptions options,
        List<(ushort[] Y, ushort[]? U, ushort[]? V, ushort[]? A, AvifFilmGrain? Grain)> coded, int w, int h, int bd, bool mono,
        Av1PixelLayout layout, Av1ObuWriter.Av1ColorDesc color, bool hasAlpha, bool lossless, int chromaPosition, AvifSequenceData sq)
    {
        int speed = Math.Clamp(options.Speed, 0, 9);
        if (lossless || speed >= 7 || coded.Count == 0 || coded.Exists(c => c.Grain != null)) return false;

        var (tileCols, tileRows) = ResolveTiling(options, w, h);
        int threads = options.MaxThreads == 0 ? Environment.ProcessorCount : options.MaxThreads;

        AomTune Tune(bool alpha) => options.Tune switch
        {
            AvifTune.Iq => AomTune.Iq,
            AvifTune.Psnr => AomTune.Psnr,
            AvifTune.Ssim => AomTune.Ssim,
            _ => alpha ? AomTune.Psnr : AomTune.Ssim,
        };
        int Quantizer(int? quality, bool tuneIq)
        {
            if (quality is { } q) return tuneIq ? TuneIqQualityToQuantizer[Math.Clamp(q, 0, 100)] : ((100 - Math.Clamp(q, 0, 100)) * 63 + 50) / 100;
            if (options.Qp is { } qp) return Math.Clamp((int)Math.Round(qp * 63 / 51.0), 1, 63);   // SharpImage's 0..51 QP scale
            return tuneIq ? TuneIqQualityToQuantizer[AvifEncodeOptions.DefaultQuality] : ((100 - AvifEncodeOptions.DefaultQuality) * 63 + 50) / 100;
        }
        var colorTune = Tune(false);
        int colorQuantizer = Quantizer(options.Quality, colorTune == AomTune.Iq);
        var alphaTune = Tune(true);
        int alphaQuantizer = Quantizer(options.QualityAlpha ?? options.Quality, alphaTune == AomTune.Iq);
        if (colorQuantizer == 0 || (hasAlpha && alphaQuantizer == 0)) return false;   // lossless: not on this path

        AomGqConfig Config(bool alpha) => new()
        {
            Width = w, Height = h,
            // libavif's aom_image: chroma shift 1 / 1 for alpha and monochrome (profile 0)
            SsX = alpha || mono || layout != Av1PixelLayout.I444 ? 1 : 0, SsY = alpha || mono || layout == Av1PixelLayout.I420 ? 1 : 0,
            Monochrome = alpha || mono, BitDepth = bd, Speed = speed, Tune = alpha ? alphaTune : colorTune, Threads = Math.Min(threads, 64),
            TileColumnsLog2 = tileCols, TileRowsLog2 = tileRows, LagInFrames = hasAlpha ? 0 : 35,
            KfMaxDist = options.KeyframeInterval > 0 ? options.KeyframeInterval : 9999, EnableRestoration = bd != 12,
            Sharpness = options.Sharpness, EnableCdef = options.EnableCdef,
            ReapplyCodecOptions = options.Tune != null || options.Sharpness != null || options.EnableCdef != null,
            Color = alpha
                ? new AomSequenceConfig { ColorRange = 1 }
                : new AomSequenceConfig
                {
                    ColorPrimaries = color.Primaries, TransferCharacteristics = color.Transfer, MatrixCoefficients = color.Matrix,
                    ColorRange = color.FullRange ? 1 : 0, ChromaSamplePosition = chromaPosition,
                },
        };

        int cw = layout == Av1PixelLayout.I444 ? w : (w + 1) >> 1, ch = layout == Av1PixelLayout.I420 ? (h + 1) >> 1 : h;
        AomGqFrameInput Input(ushort[] y, ushort[]? u, ushort[]? v, bool monochrome, int quantizer, long flags)
        {
            var fi = new AomGqFrameInput { Strides = monochrome ? [w] : [w, cw, cw], Quantizer = quantizer, Flags = flags };
            if (bd > 8) fi.Planes16 = monochrome ? [y] : [y, u!, v!];
            else
            {
                static byte[] B(ushort[] p) { var r = new byte[p.Length]; for (int i = 0; i < p.Length; i++) r[i] = (byte)p[i]; return r; }
                fi.Planes = monochrome ? [B(y)] : [B(y), B(u!), B(v!)];
            }
            return fi;
        }

        var colorEnc = new AomGqEncoder(Config(false));
        var alphaEnc = hasAlpha ? new AomGqEncoder(Config(true)) : null;
        var cSamples = new List<byte[]>();
        var cSync = new List<bool>();
        var aSamples = new List<byte[]>();
        var aSync = new List<bool>();
        for (int i = 0; i < coded.Count; i++)
        {
            var (y, u, v, a, _) = coded[i];
            foreach (var p in colorEnc.Encode(Input(y, u, v, mono, colorQuantizer, 0)))
            {
                cSamples.Add(p.Data);
                cSync.Add(p.IsKey);
            }
            if (alphaEnc == null) continue;
            // avifEncoderDataShouldForceKeyframeForAlpha (no lag with alpha: the colour frame is already out)
            bool forceKey = i > 0 && cSamples.Count == i + 1 && cSync[^1];
            foreach (var p in alphaEnc.Encode(Input(a!, null, null, true, alphaQuantizer, forceKey ? AomGqEncoder.FlagForceKf : 0)))
            {
                aSamples.Add(p.Data);
                aSync.Add(p.IsKey);
            }
        }
        // aomCodecEncodeFinish
        for (;;)
        {
            var ps = colorEnc.Encode(null);
            if (ps.Count == 0) break;
            foreach (var p in ps) { cSamples.Add(p.Data); cSync.Add(p.IsKey); }
        }
        if (alphaEnc != null)
            for (;;)
            {
                var ps = alphaEnc.Encode(null);
                if (ps.Count == 0) break;
                foreach (var p in ps) { aSamples.Add(p.Data); aSync.Add(p.IsKey); }
            }
        if (cSamples.Count != coded.Count || (hasAlpha && aSamples.Count != coded.Count))
            throw new InvalidOperationException("The encoder output a different number of samples than frames.");

        sq.ColorSamples.AddRange(cSamples);
        sq.ColorSync!.AddRange(cSync);
        if (hasAlpha)
        {
            sq.AlphaSamples!.AddRange(aSamples);
            sq.AlphaSync!.AddRange(aSync);
        }
        return true;
    }
}
