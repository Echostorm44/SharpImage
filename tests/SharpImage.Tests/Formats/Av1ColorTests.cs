using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Verifies AVIF colour encoding (64x64 I420): a colourful test image encodes and round-trips back through
// HeifCoder with reasonable fidelity (subsampled chroma ⇒ some loss, but hues preserved).
public sealed class Av1ColorTests
{
    private static ImageFrame ColorImage(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        for (long y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < w; x++)
            {
                // Smooth colour field: R ramps with x, G with y, B constant (no byte overflow at any size).
                byte r = (byte)(20 + x * 200 / w);
                byte g = (byte)(20 + y * 200 / h);
                byte b = (byte)(128);
                int o = (int)x * ch;
                row[o] = Quantum.ScaleFromByte(r);
                if (ch > 1) row[o + 1] = Quantum.ScaleFromByte(g);
                if (ch > 2) row[o + 2] = Quantum.ScaleFromByte(b);
            }
        }

        return f;
    }

    [Test]
    [Arguments(64, 64)]    // 64 block, TX_32X32 chroma
    [Arguments(32, 32)]    // 32 block, TX_16X16 chroma, CfL-allowed
    [Arguments(16, 16)]    // 16 block, TX_8X8 chroma
    [Arguments(24, 24)]    // 32 block covering 24x24, TX_16X16 chroma
    [Arguments(128, 64)]   // multi-SB colour, 2x1
    [Arguments(128, 128)]  // multi-SB colour, 2x2
    [Arguments(256, 128)]  // multi-SB colour, 4x2
    [Arguments(192, 192)]  // multi-SB colour, 3x3
    [Arguments(100, 100)]  // non-multiple colour (even), edges padded/clipped
    [Arguments(168, 104)]  // non-multiple colour
    public async Task Color_RoundTrips(int w, int h)
    {
        ImageFrame src = ColorImage(w, h);
        byte[] avif = HeifCoder.Encode(src, HeifContainerType.Avif, qp: 8);

        await Assert.That(HeifCoder.IsAvif(avif)).IsTrue();
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(w);

        double sse = 0;
        int n = 0;
        for (long y = 0; y < h; y++)
        {
            for (long x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int a = (src.GetPixelChannel(x, y, c) * 255 + 32767) / 65535;
                    int b = (dec.GetPixelChannel(x, y, c) * 255 + 32767) / 65535;
                    int d = a - b;
                    sse += d * d;
                    n++;
                }
            }
        }

        double rmse = System.Math.Sqrt(sse / n);
        System.Console.WriteLine($"[COLOR] rgb rmse={rmse:F3}");
        // Colour with I420 subsampling + matrix rounding: expect small but non-zero error.
        await Assert.That(rmse).IsLessThan(6.0);
    }

    // Sharp colour steps aligned to the 32-column boundaries — exercises the colour recursive partitioner
    // (luma splits, chroma follows to 4x4). Round-trips near-losslessly (only I420 subsampling error remains).
    [Test]
    [Arguments(128, 128)]
    [Arguments(192, 128)]
    public async Task Color_Partition_RoundTrips(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        for (long y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < w; x++)
            {
                bool a = ((x / 32) & 1) == 0;
                int o = (int)x * ch;
                row[o] = Quantum.ScaleFromByte((byte)(a ? 40 : 200));
                if (ch > 1) row[o + 1] = Quantum.ScaleFromByte((byte)(a ? 200 : 40));
                if (ch > 2) row[o + 2] = Quantum.ScaleFromByte((byte)(a ? 90 : 150));
            }
        }

        byte[] avif = HeifCoder.Encode(f, HeifContainerType.Avif, qp: 8);
        ImageFrame dec = HeifCoder.Decode(avif);
        double sse = 0; int n = 0;
        for (long y = 0; y < h; y++)
            for (long x = 0; x < w; x++)
            {
                // HeifCoder upsamples 4:2:0 chroma bilinearly (libavif parity), which blends the two luma columns that
                // straddle each hard colour boundary (x % 32 == 31 / 0). That is upsampling, not coding error, so
                // those columns are excluded; everywhere else the partitioned coding must stay near-exact.
                if (x % 32 is 31 or 0) continue;
                for (int cc = 0; cc < 3; cc++)
                {
                    int a = (f.GetPixelChannel(x, y, cc) * 255 + 32767) / 65535;
                    int b = (dec.GetPixelChannel(x, y, cc) * 255 + 32767) / 65535;
                    int d = a - b; sse += d * d; n++;
                }
            }

        await Assert.That(System.Math.Sqrt(sse / n)).IsLessThan(3.0);
    }

    // Angular colour wedges ("pie chart") — the content archetype that makes the RD search pick DIRECTIONAL/Smooth/
    // Paeth UV chroma modes (not just DC/CfL), whose coeffs are coded under the UV-mode-derived transform type
    // (TxTypeFromUvMode: AdstDct/DctAdst/AdstAdst). This is a round-trip SMOKE test of that UV-mode path through our
    // own decoder (guards against crashes / gross desync): decoded rmse stays near the 4:2:0 subsampling floor. It
    // does NOT tightly prove chroma tx-type conformance — at near-lossless qp the residual is too small for a wrong
    // kernel to show, and at coarse qp quant loss confounds the floor comparison. TIGHT conformance for this path is
    // verified by the corpus ffmpeg/libdav1d A/B (a wrong chroma tx-type there collapses piechart PSNR / +170% BD-rate).
    [Test]
    [Arguments(128, 128)]
    [Arguments(64, 64)]
    public async Task Color_DiagonalEdges_UvModes_RoundTrip(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        var src = new byte[w * h * 3];
        for (long y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < w; x++)
            {
                // Angular colour wedges around the centre (a "pie chart") — the archetype that makes the RD search
                // pick directional UV chroma prediction (straight-line prediction of a radial edge leaves residual).
                double ang = System.Math.Atan2(y - h / 2.0, x - w / 2.0);   // -pi..pi
                int seg = (int)(((ang + System.Math.PI) / (2 * System.Math.PI)) * 6) % 6;
                (byte r, byte g, byte b) = seg switch
                {
                    0 => ((byte)220, (byte)40, (byte)60),
                    1 => ((byte)40, (byte)200, (byte)90),
                    2 => ((byte)60, (byte)80, (byte)210),
                    3 => ((byte)230, (byte)200, (byte)40),
                    4 => ((byte)200, (byte)60, (byte)200),
                    _ => ((byte)40, (byte)190, (byte)200),
                };
                int o = (int)x * ch, so = (int)(y * w + x) * 3;
                row[o] = Quantum.ScaleFromByte(r); src[so] = r;
                if (ch > 1) { row[o + 1] = Quantum.ScaleFromByte(g); src[so + 1] = g; }
                if (ch > 2) { row[o + 2] = Quantum.ScaleFromByte(b); src[so + 2] = b; }
            }
        }

        byte[] avif = HeifCoder.Encode(f, HeifContainerType.Avif, qp: 8);
        ImageFrame dec = HeifCoder.Decode(avif);
        double sse = 0; int n = 0;
        for (long y = 0; y < h; y++)
            for (long x = 0; x < w; x++)
                for (int cc = 0; cc < 3; cc++)
                {
                    int a = (f.GetPixelChannel(x, y, cc) * 255 + 32767) / 65535;
                    int b = (dec.GetPixelChannel(x, y, cc) * 255 + 32767) / 65535;
                    int d = a - b; sse += d * d; n++;
                }
        double decRmse = System.Math.Sqrt(sse / n);

        // 4:2:0 subsampling-only reference: RGB->YUV(BT601 full) -> 2x2 box down -> nearest up -> RGB, vs source.
        double ssRef = SubsampleRefRmse(src, w, h);
        System.Console.WriteLine($"[DIAG-UV] {w}x{h} decRmse={decRmse:F3} subsampleRef={ssRef:F3}");
        // Correct codec (qp8, near-lossless) tracks the subsampling floor; a wrong chroma tx-type adds gross error.
        await Assert.That(decRmse).IsLessThan(ssRef + 4.0);
    }

    // RGB rmse of a pure 4:2:0 round-trip of the source (no codec): BT.601 full-range RGB->YUV, 2x2 box-average
    // chroma down, nearest-neighbour up, YUV->RGB. Isolates the subsampling floor from codec reconstruction error.
    private static double SubsampleRefRmse(byte[] src, int w, int h)
    {
        int cw = w / 2, chh = h / 2;
        var uf = new double[cw * chh]; var vf = new double[cw * chh];
        double Y(int i) => 0.299 * src[i] + 0.587 * src[i + 1] + 0.114 * src[i + 2];
        double U(int i) => -0.168736 * src[i] - 0.331264 * src[i + 1] + 0.5 * src[i + 2] + 128;
        double V(int i) => 0.5 * src[i] - 0.418688 * src[i + 1] - 0.081312 * src[i + 2] + 128;
        for (int cy = 0; cy < chh; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                double su = 0, sv = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    { int i = ((cy * 2 + dy) * w + (cx * 2 + dx)) * 3; su += U(i); sv += V(i); }
                uf[cy * cw + cx] = su / 4; vf[cy * cw + cx] = sv / 4;
            }
        double sse = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                double yy = Y(i), uu = uf[(y / 2) * cw + (x / 2)], vv = vf[(y / 2) * cw + (x / 2)];
                double r = yy + 1.402 * (vv - 128), g = yy - 0.344136 * (uu - 128) - 0.714136 * (vv - 128), b = yy + 1.772 * (uu - 128);
                double[] rec = { r, g, b };
                for (int c = 0; c < 3; c++) { int d = (int)System.Math.Clamp(System.Math.Round(rec[c]), 0, 255) - src[i + c]; sse += d * d; n++; }
            }
        return System.Math.Sqrt(sse / n);
    }
}
