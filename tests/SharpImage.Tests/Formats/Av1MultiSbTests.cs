using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Verifies the multi-superblock encoder (cross-block DC prediction + reconstruct-as-you-go) for 128px frames:
// each output round-trips through our HeifCoder decoder near-losslessly at low qp.
public sealed class Av1MultiSbTests
{
    private static byte[] Ramp(int w, int h)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = (byte)(30 + (x + y) * 190 / (w + h));
            }
        }

        return px;
    }

    private static (double rmse, int maxErr) Roundtrip(int w, int h, int baseQ)
    {
        byte[] src = Ramp(w, h);
        byte[] avif = Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(src, w, h, baseQ);
        ImageFrame frame = HeifCoder.Decode(avif);
        double sse = 0; int maxErr = 0;
        for (long y = 0; y < h; y++)
        {
            for (long x = 0; x < w; x++)
            {
                int v = (frame.GetPixelChannel(x, y, 0) * 255 + 32767) / 65535;
                int d = src[y * w + x] - v;
                sse += d * d;
                maxErr = System.Math.Max(maxErr, System.Math.Abs(d));
            }
        }

        return (System.Math.Sqrt(sse / (w * (double)h)), maxErr);
    }

    [Test]
    [Arguments(128, 64)]   // 2x1 SBs
    [Arguments(64, 128)]   // 1x2 SBs
    [Arguments(128, 128)]  // 2x2 SBs
    public async Task MultiSb_RoundTrips(int w, int h)
    {
        (double rmse, int _) = Roundtrip(w, h, baseQ: 32);
        await Assert.That(rmse).IsLessThan(4.0);
    }
}
