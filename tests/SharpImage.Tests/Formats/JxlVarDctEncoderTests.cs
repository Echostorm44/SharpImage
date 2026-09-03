using System;
using SharpImage.Formats.Jxl;

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
}
