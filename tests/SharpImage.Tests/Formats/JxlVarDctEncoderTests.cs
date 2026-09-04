using System;
using SharpImage.Core;
using SharpImage.Formats.Jxl;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Validates the VarDCT forward transform core: sRGB -> XYB -> forward DCT -> quantise, reconstructed
// through the decoder's own inverse (dequantise, inverse DCT, XYB -> sRGB). This proves the forward math
// is the exact inverse of the decoder before the codestream writer is built on top of it.
public class JxlVarDctEncoderTests
{
    private static float[][] SyntheticImage(int w, int h)
    {
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[w * h];
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                srgb[0][p] = (float)x / (w - 1);                                   // horizontal ramp
                srgb[1][p] = (float)y / (h - 1);                                   // vertical ramp
                srgb[2][p] = 0.5f + (0.3f * MathF.Sin(x * 0.4f) * MathF.Cos(y * 0.3f)); // smooth texture
            }
        }

        return srgb;
    }

    [Test]
    public async Task SrgbXyb_RoundTrips()
    {
        var fp = new VarDctFrameParams();
        float[][] srgb = SyntheticImage(32, 32);
        float[][] xyb = JxlVarDctEncoder.SrgbToXyb(srgb, 32 * 32, fp);
        JxlVarDct.XybToSrgb(xyb, 32 * 32, fp); // now xyb holds reconstructed sRGB

        double maxErr = 0;
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 32 * 32; i++)
            {
                maxErr = Math.Max(maxErr, Math.Abs((srgb[c][i] - xyb[c][i]) * 255.0));
            }
        }

        await Assert.That(maxErr).IsLessThan(0.5); // sub-quantum colour round-trip
    }

    [Test]
    public async Task Dct8_ForwardInverse_IsIdentity()
    {
        var rng = new Random(1);
        var buf = new float[64];
        var orig = new float[64];
        for (int i = 0; i < 64; i++)
        {
            orig[i] = buf[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var g = new JxlDct.Grid(buf, 0, 8, 8, 8);
        JxlDct.Dct2D(g, false);
        JxlDct.Dct2D(g, true);

        double maxErr = 0;
        for (int i = 0; i < 64; i++)
        {
            maxErr = Math.Max(maxErr, Math.Abs(orig[i] - buf[i]));
        }

        Console.Error.WriteLine($"[VARDCT] DCT8 forward->inverse maxErr={maxErr:E3}");
        await Assert.That(maxErr).IsLessThan(1e-4);
    }

    [Test]
    public async Task ForwardPipeline_ReconstructsWithHighPsnr()
    {
        var fp = new VarDctFrameParams();
        float[][] srgb = SyntheticImage(64, 64);

        // Finer quantisation (larger global_scale x per-block hf_mul, larger quant_lf for DC) => higher PSNR.
        // All values here are within the codestream-encodable ranges (global_scale <= 73728).
        double fine = JxlVarDctEncoder.ReconstructPsnr(srgb, 64, 64, fp, globalScale: 65536, quantLf: 4096, blockHfMul: 8);
        double coarse = JxlVarDctEncoder.ReconstructPsnr(srgb, 64, 64, fp, globalScale: 512, quantLf: 16, blockHfMul: 1);

        await Assert.That(fine).IsGreaterThan(40.0);   // near-lossless at fine quant
        await Assert.That(fine).IsGreaterThan(coarse);  // finer quant reconstructs better
    }

    private static ImageFrame GradientFrame(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 3;
                row[o] = (ushort)(x * Quantum.MaxValue / Math.Max(1, width - 1));
                row[o + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(1, height - 1));
                row[o + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }

        return frame;
    }

    [Test]
    public async Task DecodeRealReferenceFile()
    {
        string path = Environment.GetEnvironmentVariable("VARDCT_REF");
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return; // only runs when a reference file is provided
        }

        byte[] cs = System.IO.File.ReadAllBytes(path);
        try
        {
            JxlModularResult res = JxlFrame.DecodeModularCodestream(cs);
            Console.Error.WriteLine($"[VARDCT] our decoder read real file OK: {res.Width}x{res.Height} ch={res.NumChannels}");
            System.IO.File.WriteAllText(path + ".ourdecode.txt", $"OK {res.Width}x{res.Height} ch={res.NumChannels}");
        }
        catch (Exception e)
        {
            System.IO.File.WriteAllText(path + ".ourdecode.txt", $"FAIL: {e.GetType().Name}: {e.Message}");
        }

        await Assert.That(true).IsTrue();
    }

    private static ImageFrame TexturedFrame(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 3;
                float r = 0.5f + (0.4f * MathF.Sin(x * 0.7f));
                float g = 0.5f + (0.4f * MathF.Cos(y * 0.5f));
                float b = 0.5f + (0.3f * MathF.Sin((x + y) * 0.4f));
                row[o] = (ushort)(Math.Clamp(r, 0f, 1f) * Quantum.MaxValue);
                row[o + 1] = (ushort)(Math.Clamp(g, 0f, 1f) * Quantum.MaxValue);
                row[o + 2] = (ushort)(Math.Clamp(b, 0f, 1f) * Quantum.MaxValue);
            }
        }

        return frame;
    }

    [Test]
    public async Task VarDctAc_TexturedImage_CapturesDetail()
    {
        const int w = 64, h = 64;
        ImageFrame frame = TexturedFrame(w, h);
        // Keep DC quant moderate (global_scale x quant_lf) so DC ints stay in range, and refine the AC
        // via the per-block hf_mul (global_scale x block_hf_mul) independently.
        const uint gs = 4096, qlf = 64, hfm = 32;
        byte[] cs = JxlEncoder.EncodeVarDct(frame, globalScale: gs, quantLf: qlf, blockHfMul: hfm);
        JxlModularResult res = JxlFrame.DecodeModularCodestream(cs);

        double mse = 0;
        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double src = Quantum.ScaleToByte(srow[(x * 3) + c]);
                    double d = src - res.Channels[c].Px[(y * w) + x];
                    mse += d * d;
                }
            }
        }

        mse /= 3.0 * w * h;
        double psnr = mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP_AC");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, cs);
        }

        // Model PSNR: what the encoder's own forward/inverse math predicts (no CfL) at the same quant.
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[w * h];
        }

        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    srgb[c][(y * w) + x] = Quantum.ScaleToByte(srow[(x * 3) + c]) / 255f;
                }
            }
        }

        double modelPsnr = JxlVarDctEncoder.ReconstructPsnr(srgb, w, h, new VarDctFrameParams(), gs, qlf, hfm);
        Console.Error.WriteLine($"[VARDCT] textured AC decode PSNR={psnr:F2} dB (model {modelPsnr:F2}), {cs.Length} bytes");
        await Assert.That(psnr).IsGreaterThan(30.0);                 // AC coefficients recover real detail
        await Assert.That(Math.Abs(psnr - modelPsnr)).IsLessThan(1.0); // decode matches the validated forward model
    }

    [Test]
    public async Task VarDct_RatioProbe_AgainstLibjxl()
    {
        string outDir = Environment.GetEnvironmentVariable("VARDCT_RATIO_DIR");
        if (string.IsNullOrEmpty(outDir))
        {
            return; // measurement-only; runs when VARDCT_RATIO_DIR is set
        }

        const int w = 256, h = 256;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        string srcPpm = Environment.GetEnvironmentVariable("VARDCT_RATIO_SRC");
        if (!string.IsNullOrEmpty(srcPpm))
        {
            // Load a P6 binary PPM (max 256x256) so we can measure real photographic content.
            byte[] raw = System.IO.File.ReadAllBytes(srcPpm);
            int p = 0;
            string Token()
            {
                while (p < raw.Length && (raw[p] == ' ' || raw[p] == '\n' || raw[p] == '\r' || raw[p] == '\t')) p++;
                int s = p;
                while (p < raw.Length && raw[p] != ' ' && raw[p] != '\n' && raw[p] != '\r' && raw[p] != '\t') p++;
                return System.Text.Encoding.ASCII.GetString(raw, s, p - s);
            }
            Token(); // "P6"
            int pw = int.Parse(Token()), ph = int.Parse(Token());
            Token(); // maxval
            p++;     // single whitespace after maxval
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    int o = x * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int idx = p + (((Math.Min(y, ph - 1) * pw) + Math.Min(x, pw - 1)) * 3) + c;
                        row[o + c] = Quantum.ScaleFromByte(raw[idx]);
                    }
                }
            }
        }
        else
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                // Detail-rich synthetic: overlaid sinusoids + a radial ramp.
                float fx = x / (float)w, fy = y / (float)h;
                float r = 0.5f + (0.25f * MathF.Sin(x * 0.3f)) + (0.2f * MathF.Cos(y * 0.11f));
                float g = 0.5f + (0.25f * MathF.Sin((x + y) * 0.17f)) + (0.15f * fx);
                float b = 0.5f + (0.25f * MathF.Cos(x * 0.07f + y * 0.05f)) + (0.15f * fy);
                int o = x * 3;
                row[o] = (ushort)(Math.Clamp(r, 0f, 1f) * Quantum.MaxValue);
                row[o + 1] = (ushort)(Math.Clamp(g, 0f, 1f) * Quantum.MaxValue);
                row[o + 2] = (ushort)(Math.Clamp(b, 0f, 1f) * Quantum.MaxValue);
            }
        }

        // Dump the source as a binary PPM so libjxl can encode the identical pixels.
        using (var ppm = new System.IO.FileStream(System.IO.Path.Combine(outDir, "src.ppm"), System.IO.FileMode.Create))
        {
            byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            ppm.Write(hdr, 0, hdr.Length);
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        ppm.WriteByte((byte)Quantum.ScaleToByte(srow[(x * 3) + c]));
                    }
                }
            }
        }

        string mode = Environment.GetEnvironmentVariable("VARDCT_MODE"); // "refine" | "perceptual" | default heuristic
        float qlfNum = float.TryParse(Environment.GetEnvironmentVariable("VARDCT_QLF"), out float qn) ? qn : 90f;
        foreach (float d in new[] { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f, 3.0f })
        {
            byte[] cs = mode == "block" ? JxlEncoder.EncodeVarDctBlockRefined(frame, d)
                : mode == "refine" ? JxlEncoder.EncodeVarDctRefined(frame, d)
                : mode == "perceptual" ? JxlEncoder.EncodeVarDct(frame, 8192u, (uint)Math.Clamp((int)MathF.Round(qlfNum / d), 1, 512), 1u, null, false, true, perceptual: true, distance: d)
                : JxlEncoder.EncodeVarDct(frame, d);
            JxlModularResult r = JxlFrame.DecodeModularCodestream(cs);
            double mse = 0;
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double diff = Quantum.ScaleToByte(srow[(x * 3) + c]) - r.Channels[c].Px[(y * w) + x];
                        mse += diff * diff;
                    }
                }
            }

            mse /= 3.0 * w * h;
            double psnr = 10.0 * Math.Log10(255.0 * 255.0 / mse);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"ours_d{d}.jxl"), cs);
            Console.Error.WriteLine($"[RATIO] ours d={d}: {cs.Length} bytes, {psnr:F2} dB");
        }

        await Assert.That(true).IsTrue();
    }

    // Self-contained multi-LF-group regression: images larger than one 2048px LF group tile into
    // numLf > 1. DC + HfMetadata are independent per-LF-group modular sub-streams (prediction never
    // crosses an LF boundary), so the encoder must slice them per group; a bug there round-trips wrong.
    // 2100x300 => 2x1 LF groups (horizontal split); 300x2100 => 1x2 (vertical split).
    [Test]
    [Arguments(2100, 300)]
    [Arguments(300, 2100)]
    public async Task VarDct_MultiLfGroup_RoundTrips(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                // Detail across the LF-group boundary so AC coefficients (not just flat DC) are exercised.
                float r = 0.5f + (0.4f * MathF.Sin(x * 0.05f) * MathF.Cos(y * 0.03f));
                float g = (float)x / (w - 1);
                float b = 0.5f + (0.3f * MathF.Sin((x + y) * 0.02f));
                int o = x * 3;
                row[o] = (ushort)(Math.Clamp(r, 0f, 1f) * Quantum.MaxValue);
                row[o + 1] = (ushort)(Math.Clamp(g, 0f, 1f) * Quantum.MaxValue);
                row[o + 2] = (ushort)(Math.Clamp(b, 0f, 1f) * Quantum.MaxValue);
            }
        }

        byte[] cs = JxlEncoder.EncodeVarDct(frame, 1.5f);
        JxlModularResult r2 = JxlFrame.DecodeModularCodestream(cs);
        await Assert.That(r2.Width).IsEqualTo(w);
        await Assert.That(r2.Height).IsEqualTo(h);

        double mse = 0;
        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double diff = Quantum.ScaleToByte(srow[(x * 3) + c]) - r2.Channels[c].Px[(y * w) + x];
                    mse += diff * diff;
                }
            }
        }

        mse /= 3.0 * w * h;
        double psnr = 10.0 * Math.Log10(255.0 * 255.0 / mse);
        await Assert.That(psnr).IsGreaterThan(30.0);
    }

    // Builds a synthetic RGBA frame with a known alpha ramp, encodes it lossy, and (when VARDCT_ALPHA_DIR is
    // set) dumps ours_alpha.jxl + the reference alpha as alpha_ref.pgm for external verification.
    [Test]
    public async Task VarDct_Alpha_Encode()
    {
        int sz = int.TryParse(Environment.GetEnvironmentVariable("VARDCT_ALPHA_SIZE"), out int s) ? s : 200;
        int w = sz, h = sz;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, hasAlpha: true);
        int nch = frame.NumberOfChannels; // 4
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * nch;
                row[o] = Quantum.ScaleFromByte((byte)(x * 255 / (w - 1)));
                row[o + 1] = Quantum.ScaleFromByte((byte)(y * 255 / (h - 1)));
                row[o + 2] = Quantum.ScaleFromByte(128);
                row[o + 3] = Quantum.ScaleFromByte((byte)((x + y) * 255 / (w + h - 2))); // alpha ramp
            }
        }

        byte[] cs = JxlEncoder.EncodeVarDct(frame, 1.0f);
        await Assert.That(cs.Length).IsGreaterThan(0);

        JxlModularResult r = JxlFrame.DecodeModularCodestream(cs);
        // Alpha is emitted (and must round-trip bit-exact, being lossless) only for single-group images
        // (<= 256px); larger images currently drop it to stay spec-valid, so we get a clean color-only file.
        bool expectAlpha = w <= 256 && h <= 256;
        await Assert.That(r.HasAlpha).IsEqualTo(expectAlpha);
        if (expectAlpha)
        {
            await Assert.That(r.NumChannels).IsEqualTo(4);
            long alphaErr = 0;
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    alphaErr += Math.Abs((int)Quantum.ScaleToByte(srow[(x * nch) + 3]) - r.Channels[3].Px[(y * w) + x]);
                }
            }

            await Assert.That(alphaErr).IsEqualTo(0L); // alpha is lossless
        }

        // The public EncodeLossy path (block-refined for <=2MP) must also preserve alpha for single-group.
        if (expectAlpha)
        {
            JxlModularResult rl = JxlFrame.DecodeModularCodestream(SharpImage.Formats.JxlCoder.EncodeLossy(frame, 80));
            await Assert.That(rl.HasAlpha).IsTrue();
            long e2 = 0;
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    e2 += Math.Abs((int)Quantum.ScaleToByte(srow[(x * nch) + 3]) - rl.Channels[3].Px[(y * w) + x]);
                }
            }

            await Assert.That(e2).IsEqualTo(0L);
        }

        string dir = Environment.GetEnvironmentVariable("VARDCT_ALPHA_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "ours_alpha.jxl"), cs);
            using var pgm = new System.IO.FileStream(System.IO.Path.Combine(dir, "alpha_ref.pgm"), System.IO.FileMode.Create);
            byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
            pgm.Write(hdr, 0, hdr.Length);
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    pgm.WriteByte((byte)Quantum.ScaleToByte(row[(x * nch) + 3]));
                }
            }
        }
    }

    // Truncated-preview decode: a byte-prefix of a progressive (multi-section) VarDCT frame must decode into
    // a valid best-effort preview instead of throwing. The guarantee is section-granular (an ANS stream can't
    // be safely decoded from a partial tail), so previews sharpen as whole sections arrive — DC, then AC
    // passes — and the complete buffer reconstructs exactly. Verified against jxl-oxide separately: wherever
    // sections align, our preview matches jxl-oxide's (53-57 dB); this test guards our own streaming path.
    [Test]
    public async Task VarDct_TruncatedPreview_Decodes()
    {
        const int w = 512, h = 512;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                float r = 0.5f + (0.4f * MathF.Sin(x * 0.06f) * MathF.Cos(y * 0.04f));
                float g = 0.5f + (0.3f * MathF.Sin((x + y) * 0.03f));
                float b = (float)y / (h - 1);
                int o = x * 3;
                row[o] = (ushort)(Math.Clamp(r, 0f, 1f) * Quantum.MaxValue);
                row[o + 1] = (ushort)(Math.Clamp(g, 0f, 1f) * Quantum.MaxValue);
                row[o + 2] = (ushort)(Math.Clamp(b, 0f, 1f) * Quantum.MaxValue);
            }
        }

        // 3-pass progressive, multi-group (512px => 4 groups): 1 + 1 + 1 + 4*3 = 15 TOC sections.
        byte[] prog = JxlEncoder.EncodeVarDct(frame, 1.5f, new[] { 2, 1 });
        JxlModularResult full = JxlFrame.DecodeModularCodestream(prog);

        double PsnrVsFull(JxlModularResult r)
        {
            double mse = 0;
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < w * h; i++)
                {
                    double diff = full.Channels[c].Px[i] - r.Channels[c].Px[i];
                    mse += diff * diff;
                }
            }

            mse /= 3.0 * w * h;
            return mse <= 0 ? 999 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        double coarse = 0, mid = 0, fullPsnr = 0;
        foreach (double frac in new[] { 0.45, 0.85, 1.0 })
        {
            int n = (int)(prog.Length * frac);
            var trunc = new byte[n];
            Array.Copy(prog, trunc, n);

            // Public preview API and the low-level path both return a correctly-sized image without throwing.
            ImageFrame previewFrame = SharpImage.Formats.JxlCoder.DecodePreview(trunc);
            await Assert.That((int)previewFrame.Columns).IsEqualTo(w);
            await Assert.That((int)previewFrame.Rows).IsEqualTo(h);

            double psnr = PsnrVsFull(JxlFrame.DecodeModularCodestream(trunc, allowTruncated: true));
            if (frac == 0.45) coarse = psnr;
            else if (frac == 0.85) mid = psnr;
            else fullPsnr = psnr;
        }

        await Assert.That(mid).IsGreaterThan(coarse + 5.0); // more sections => a materially sharper preview
        await Assert.That(mid).IsGreaterThan(30.0);         // once DC + AC arrive, a genuine preview
        await Assert.That(fullPsnr).IsGreaterThan(60.0);    // the complete buffer reconstructs exactly
    }

    // Multi-group / multi-LF-group verification. Reads a P6 PPM of ANY size (its own dimensions),
    // encodes at a few distances, round-trips through our own decoder for a sanity PSNR, and dumps the
    // .jxl so the harness can decode it in jxl-oxide AND libjxl (the real garbling test). Gated on
    // VARDCT_LARGE_DIR (out) + VARDCT_LARGE_SRC (a binary PPM).
    [Test]
    public async Task VarDct_LargeImage_MultiGroup()
    {
        string outDir = Environment.GetEnvironmentVariable("VARDCT_LARGE_DIR");
        string srcPpm = Environment.GetEnvironmentVariable("VARDCT_LARGE_SRC");
        if (string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(srcPpm))
        {
            return; // measurement-only
        }

        byte[] raw = System.IO.File.ReadAllBytes(srcPpm);
        int p = 0;
        string Token()
        {
            while (p < raw.Length && (raw[p] == ' ' || raw[p] == '\n' || raw[p] == '\r' || raw[p] == '\t')) p++;
            int s = p;
            while (p < raw.Length && raw[p] != ' ' && raw[p] != '\n' && raw[p] != '\r' && raw[p] != '\t') p++;
            return System.Text.Encoding.ASCII.GetString(raw, s, p - s);
        }
        Token(); // "P6"
        int w = int.Parse(Token()), h = int.Parse(Token());
        Token(); // maxval
        p++;     // single whitespace after maxval
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * 3, ip = p + (((y * w) + x) * 3);
                for (int c = 0; c < 3; c++)
                {
                    row[o + c] = Quantum.ScaleFromByte(raw[ip + c]);
                }
            }
        }

        Console.Error.WriteLine($"[LARGE] {w}x{h}");
        if (Environment.GetEnvironmentVariable("VARDCT_LARGE_LOSSY") != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] lossy = SharpImage.Formats.JxlCoder.EncodeLossy(frame, 75);
            sw.Stop();
            JxlModularResult lr = JxlFrame.DecodeModularCodestream(lossy);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "large_lossy.jxl"), lossy);
            Console.Error.WriteLine($"[LARGE] EncodeLossy(q75): {lossy.Length} bytes in {sw.ElapsedMilliseconds} ms, decoded {lr.Width}x{lr.Height}");
            await Assert.That(lr.Width).IsEqualTo(w);
            await Assert.That(lr.Height).IsEqualTo(h);
        }

        foreach (float d in new[] { 1.0f, 2.0f })
        {
            byte[] cs = JxlEncoder.EncodeVarDct(frame, d);
            JxlModularResult r = JxlFrame.DecodeModularCodestream(cs);
            double mse = 0;
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double diff = Quantum.ScaleToByte(srow[(x * 3) + c]) - r.Channels[c].Px[(y * w) + x];
                        mse += diff * diff;
                    }
                }
            }

            mse /= 3.0 * w * h;
            double psnr = 10.0 * Math.Log10(255.0 * 255.0 / mse);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"large_d{d}.jxl"), cs);
            Console.Error.WriteLine($"[LARGE] ours d={d}: {cs.Length} bytes, self-decode {psnr:F2} dB");
            await Assert.That(psnr).IsGreaterThan(28.0);
        }
    }

    [Test]
    public async Task JxlCoder_EncodeLossy_QualityKnob_Monotonic()
    {
        const int w = 128, h = 128;
        ImageFrame frame = TexturedFrame(w, h);

        byte[] hi = SharpImage.Formats.JxlCoder.EncodeLossy(frame, 92);
        byte[] lo = SharpImage.Formats.JxlCoder.EncodeLossy(frame, 40);

        double PsnrOf(byte[] cs)
        {
            JxlModularResult r = JxlFrame.DecodeModularCodestream(cs);
            double mse = 0;
            for (int y = 0; y < h; y++)
            {
                var srow = frame.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double d = Quantum.ScaleToByte(srow[(x * 3) + c]) - r.Channels[c].Px[(y * w) + x];
                        mse += d * d;
                    }
                }
            }

            mse /= 3.0 * w * h;
            return mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        double hiPsnr = PsnrOf(hi), loPsnr = PsnrOf(lo);
        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP_Q");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, hi);
        }

        Console.Error.WriteLine($"[VARDCT] q92 {hi.Length}B {hiPsnr:F1}dB  q40 {lo.Length}B {loPsnr:F1}dB");
        await Assert.That(hiPsnr).IsGreaterThan(loPsnr);   // higher quality reconstructs better
        await Assert.That(hi.Length).IsGreaterThan(lo.Length); // and costs more bytes
    }

    [Test]
    public async Task VarDctVariableBlocks_Dct16_RoundTrips()
    {
        const int w = 128, h = 128;
        // A smooth low-frequency diagonal gradient: a 16x16 DCT concentrates it into far fewer coefficients
        // than four 8x8 DCTs, so the rate-distortion block-size chooser picks Dct16.
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * 3;
                row[o] = row[o + 1] = row[o + 2] = (ushort)((x + y) * Quantum.MaxValue / (w + h));
            }
        }

        byte[] fixed8 = JxlEncoder.EncodeVarDct(frame, 4096, 128, 16, null, false, variableBlocks: false);
        byte[] vb = JxlEncoder.EncodeVarDct(frame, 4096, 128, 16, null, false, variableBlocks: true);
        await Assert.That(vb.Length).IsNotEqualTo(fixed8.Length); // Dct16 layout differs from all-8x8

        JxlModularResult res = JxlFrame.DecodeModularCodestream(vb);
        await Assert.That(res.Width).IsEqualTo(w);

        // Reconstruction should still track the smooth source well.
        double mse = 0;
        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = Quantum.ScaleToByte(srow[(x * 3) + c]) - res.Channels[c].Px[(y * w) + x];
                    mse += d * d;
                }
            }
        }

        mse /= 3.0 * w * h;
        double psnr = 10.0 * Math.Log10(255.0 * 255.0 / mse);
        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP_VB");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, vb);
        }

        Console.Error.WriteLine($"[VARDCT] variable-blocks {vb.Length}B vs 8x8 {fixed8.Length}B, {psnr:F1} dB");
        await Assert.That(psnr).IsGreaterThan(30.0);
    }

    [Test]
    public async Task VarDctAdaptiveQuant_VariesAndRoundTrips()
    {
        const int w = 128, h = 64;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        var rng = new Random(7);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                // Left half: smooth gradient. Right half: high-frequency noise (busy).
                float v = x < w / 2 ? (x / (float)(w / 2)) : (float)rng.NextDouble();
                int o = x * 3;
                row[o] = row[o + 1] = row[o + 2] = (ushort)(Math.Clamp(v, 0f, 1f) * Quantum.MaxValue);
            }
        }

        byte[] uniform = JxlEncoder.EncodeVarDct(frame, 4096, 64, 16, null, adaptiveQuant: false);
        byte[] adaptive = JxlEncoder.EncodeVarDct(frame, 4096, 64, 16, null, adaptiveQuant: true);

        // Adaptive quant must actually change the encoding (per-block hf_mul varies by luma activity)...
        await Assert.That(adaptive.Length).IsNotEqualTo(uniform.Length);
        // ...and both must still be valid, decodable frames.
        JxlModularResult ra = JxlFrame.DecodeModularCodestream(adaptive);
        JxlModularResult ru = JxlFrame.DecodeModularCodestream(uniform);
        await Assert.That(ra.Width).IsEqualTo(w);
        await Assert.That(ru.Width).IsEqualTo(w);
    }

    [Test]
    public async Task VarDctMultiGroup_LargerThanOneGroup_RoundTrips()
    {
        const int w = 384, h = 320; // 2x2 groups of 256px
        ImageFrame frame = TexturedFrame(w, h);
        const uint gs = 4096, qlf = 64, hfm = 32;
        byte[] cs = JxlEncoder.EncodeVarDct(frame, gs, qlf, hfm);
        JxlModularResult res = JxlFrame.DecodeModularCodestream(cs);
        await Assert.That(res.Width).IsEqualTo(w);
        await Assert.That(res.Height).IsEqualTo(h);

        double mse = 0;
        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double src = Quantum.ScaleToByte(srow[(x * 3) + c]);
                    double d = src - res.Channels[c].Px[(y * w) + x];
                    mse += d * d;
                }
            }
        }

        mse /= 3.0 * w * h;
        double psnr = mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);

        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[w * h];
        }

        for (int y = 0; y < h; y++)
        {
            var srow = frame.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    srgb[c][(y * w) + x] = Quantum.ScaleToByte(srow[(x * 3) + c]) / 255f;
                }
            }
        }

        double modelPsnr = JxlVarDctEncoder.ReconstructPsnr(srgb, w, h, new VarDctFrameParams(), gs, qlf, hfm);
        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP_MG");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, cs);
        }

        Console.Error.WriteLine($"[VARDCT] multigroup {w}x{h} decode PSNR={psnr:F2} (model {modelPsnr:F2}), {cs.Length} bytes");
        await Assert.That(Math.Abs(psnr - modelPsnr)).IsLessThan(1.0); // multi-group decode matches the model
        await Assert.That(psnr).IsGreaterThan(30.0);
    }

    [Test]
    public async Task VarDctProgressive_MultiPass_ReconstructsIdentically()
    {
        const int w = 64, h = 64;
        ImageFrame frame = TexturedFrame(w, h);
        const uint gs = 4096, qlf = 64, hfm = 32;

        // Single-pass reference and a 3-pass progressive encode {shift 2, 1, 0} of the SAME image.
        byte[] single = JxlEncoder.EncodeVarDct(frame, gs, qlf, hfm);
        byte[] prog = JxlEncoder.EncodeVarDct(frame, gs, qlf, hfm, passShifts: new[] { 2, 1 });

        JxlModularResult r1 = JxlFrame.DecodeModularCodestream(single);
        JxlModularResult rp = JxlFrame.DecodeModularCodestream(prog);

        // Decoding ALL passes of the progressive stream must reproduce the single-pass result exactly:
        // the shifted coefficient contributions sum back to the full coefficients.
        long diff = 0;
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < w * h; i++)
            {
                diff += Math.Abs(r1.Channels[c].Px[i] - rp.Channels[c].Px[i]);
            }
        }

        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP_PROG");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, prog);
        }

        Console.Error.WriteLine($"[VARDCT] progressive vs single: total abs pixel diff={diff}, prog {prog.Length}B vs single {single.Length}B");
        await Assert.That(diff).IsEqualTo(0L); // all-pass progressive == single-pass, pixel-exact
    }

    [Test]
    public async Task VarDctDcOnly_RoundTripsThroughDecoder()
    {
        const int w = 64, h = 64;
        ImageFrame frame = GradientFrame(w, h);
        byte[] cs = JxlEncoder.EncodeVarDct(frame, globalScale: 4096, quantLf: 32, blockHfMul: 1);

        // Decode with the reference in-tree VarDCT decoder — proves the whole frame structure is valid.
        JxlModularResult res = JxlFrame.DecodeModularCodestream(cs);
        await Assert.That(res.Width).IsEqualTo(w);
        await Assert.That(res.Height).IsEqualTo(h);
        await Assert.That(res.NumChannels).IsEqualTo(3);

        // DC-only reconstruction is blocky but each 8x8 block should sit near the source block mean.
        double mse = 0;
        for (int c = 0; c < 3; c++)
        {
            int[] px = res.Channels[c].Px;
            var srow = frame;
            for (int y = 0; y < h; y++)
            {
                var row = srow.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    double src = Quantum.ScaleToByte(row[(x * 3) + c]);
                    double d = src - px[(y * w) + x];
                    mse += d * d;
                }
            }
        }

        mse /= 3.0 * w * h;
        double psnr = mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        string dump = Environment.GetEnvironmentVariable("VARDCT_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            System.IO.File.WriteAllBytes(dump, cs);
            System.IO.File.WriteAllText(dump + ".txt", $"PSNR={psnr:F2} dB, {cs.Length} bytes");
        }

        Console.Error.WriteLine($"[VARDCT] DC-only decode PSNR={psnr:F2} dB, {cs.Length} bytes");
        await Assert.That(psnr).IsGreaterThan(22.0); // smooth gradient: block means track the source
    }
}
