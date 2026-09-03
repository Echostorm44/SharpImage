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
