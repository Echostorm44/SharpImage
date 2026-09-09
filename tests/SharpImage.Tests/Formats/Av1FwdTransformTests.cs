using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// End-to-end forward-path verification: encode a real 64x64 monochrome image (DC predict → forward DCT →
// quantize → coefficient code) and decode it back with our Av1Decoder. Asserts the reconstruction is faithful
// (low error), which confirms the forward transform + quant scaling inverts the decoder's normative transform.
public sealed class Av1FwdTransformTests
{
    private static byte[] Gradient()
    {
        var px = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                px[y * 64 + x] = (byte)(40 + (x + y) * 130 / 126); // smooth diagonal ramp 40..170
            }
        }

        return px;
    }

    private static byte[] Circles()
    {
        var px = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                double dx = x - 32, dy = y - 32;
                double r = System.Math.Sqrt(dx * dx + dy * dy);
                px[y * 64 + x] = (byte)(128 + 100 * System.Math.Cos(r * 0.5));
            }
        }

        return px;
    }

    private static (double rmse, int maxErr) Roundtrip(byte[] src, int baseQ)
    {
        byte[] stream = Av1StillImageEncoder.EncodeMonochromeImage64(src, baseQ);
        var decoder = new Av1Decoder();
        decoder.Initialize(default);
        DecodedVideoFrame? frame = decoder.Decode(stream, 0, isKeyframe: true);
        var y = frame!.YPlane.Span;
        int stride = frame.YStride;

        double sse = 0;
        int maxErr = 0;
        for (int r = 0; r < 64; r++)
        {
            for (int c = 0; c < 64; c++)
            {
                int diff = src[r * 64 + c] - y[r * stride + c];
                sse += diff * diff;
                maxErr = System.Math.Max(maxErr, System.Math.Abs(diff));
            }
        }

        return (System.Math.Sqrt(sse / (64.0 * 64.0)), maxErr);
    }

    [Test]
    public async Task Gradient_LowQ_ReconstructsFaithfully()
    {
        // Low qindex ⇒ fine quantization ⇒ small error. A smooth ramp is nearly lossless.
        (double rmse, int maxErr) = Roundtrip(Gradient(), baseQ: 16);
        System.Console.WriteLine($"[FWD] gradient q16 rmse={rmse:F3} maxErr={maxErr}");
        await Assert.That(rmse).IsLessThan(3.0);
    }

    [Test]
    public async Task Circles_LowQ_ReconstructsReasonably()
    {
        (double rmse, int maxErr) = Roundtrip(Circles(), baseQ: 16);
        System.Console.WriteLine($"[FWD] circles q16 rmse={rmse:F3} maxErr={maxErr}");
        await Assert.That(rmse).IsLessThan(8.0);
    }

    [Test]
    public async Task Gradient_HigherQ_StillTracksMean()
    {
        // Coarser quantization ⇒ larger but bounded error.
        (double rmse, int maxErr) = Roundtrip(Gradient(), baseQ: 96);
        System.Console.WriteLine($"[FWD] gradient q96 rmse={rmse:F3} maxErr={maxErr}");
        await Assert.That(rmse).IsLessThan(12.0);
    }
}
