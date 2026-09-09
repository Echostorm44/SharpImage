using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Verifies AVIF colour encoding (64x64 I420): a colourful test image encodes and round-trips back through
// HeifCoder with reasonable fidelity (subsampled chroma ⇒ some loss, but hues preserved).
public sealed class Av1ColorTests
{
    private static ImageFrame ColorImage()
    {
        var f = new ImageFrame();
        f.Initialize(64, 64, ColorspaceType.SRGB, false);
        for (long y = 0; y < 64; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < 64; x++)
            {
                // Smooth colour field: R ramps with x, G with y, B constant-ish.
                byte r = (byte)(20 + x * 3);
                byte g = (byte)(20 + y * 3);
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
    public async Task Color_64x64_RoundTrips()
    {
        ImageFrame src = ColorImage();
        byte[] avif = HeifCoder.Encode(src, HeifContainerType.Avif, qp: 8);

        await Assert.That(HeifCoder.IsAvif(avif)).IsTrue();
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(64);

        double sse = 0;
        int n = 0;
        for (long y = 0; y < 64; y++)
        {
            for (long x = 0; x < 64; x++)
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
