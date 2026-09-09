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
}
