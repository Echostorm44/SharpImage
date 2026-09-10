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
    [Arguments(256, 128)]  // 4x2 SBs
    [Arguments(192, 192)]  // 3x3 SBs
    [Arguments(320, 64)]   // 5x1 SBs (odd SB128 count)
    [Arguments(100, 100)]  // non-multiple: 2x2 SBs, edges padded/clipped
    [Arguments(168, 104)]  // non-multiple, remainder > 32 both axes
    public async Task MultiSb_RoundTrips(int w, int h)
    {
        (double rmse, int _) = Roundtrip(w, h, baseQ: 32);
        await Assert.That(rmse).IsLessThan(4.0);
    }

    // Structured content whose regions favour different intra modes (diagonal, vertical, horizontal) — this
    // exercises directional intra-mode selection with angle_delta AND produces adjacent skipped/coded blocks,
    // the combination that first exposed the block-skip-context bug. Verifies round-trip through our decoder;
    // the same streams are byte-exact in ffmpeg/libdav1d.
    [Test]
    [Arguments(192, 128)]
    [Arguments(128, 192)]
    [Arguments(256, 128)]
    public async Task MultiSb_Directional_RoundTrips(int w, int h)
    {
        var src = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = x < w / 3 ? 20 + (x + y) * 180 / (w + h)   // diagonal
                      : x < 2 * w / 3 ? 20 + x * 180 / w            // vertical edges
                      : 20 + y * 180 / h;                            // horizontal edges
                src[y * w + x] = (byte)v;
            }

        byte[] avif = Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(src, w, h, 48);
        ImageFrame frame = HeifCoder.Decode(avif);
        double sse = 0;
        for (long y = 0; y < h; y++)
            for (long x = 0; x < w; x++)
            {
                int val = (frame.GetPixelChannel(x, y, 0) * 255 + 32767) / 65535;
                int d = src[y * w + x] - val;
                sse += d * d;
            }

        double rmse = System.Math.Sqrt(sse / (w * (double)h));
        await Assert.That(rmse).IsLessThan(3.0);
    }

    // Sharp step edges aligned to the 32-column boundaries: a 64x64 transform rings across the step, but the
    // recursive partitioner splits into flat 32-wide (or smaller) blocks that code losslessly. Verifies the
    // partition tree round-trips (byte-exact in ffmpeg/libdav1d too) and that splitting engages where it helps.
    [Test]
    [Arguments(128, 128)]
    [Arguments(192, 128)]
    public async Task MultiSb_Partition_RoundTrips(int w, int h)
    {
        var src = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src[y * w + x] = (byte)(((x / 32) & 1) == 0 ? 40 : 200);

        byte[] avif = Av1StillImageEncoder.EncodeAvifMonochromeMultiSb(src, w, h, 32);
        ImageFrame frame = HeifCoder.Decode(avif);
        double sse = 0;
        for (long y = 0; y < h; y++)
            for (long x = 0; x < w; x++)
            {
                int val = (frame.GetPixelChannel(x, y, 0) * 255 + 32767) / 65535;
                int d = src[y * w + x] - val;
                sse += d * d;
            }

        double rmse = System.Math.Sqrt(sse / (w * (double)h));
        await Assert.That(rmse).IsLessThan(1.0); // flat blocks after split ⇒ near-lossless
    }
}
