using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Verifies the public AVIF encode entry point (HeifCoder.Encode → the from-scratch AV1 encoder) and that its
// output round-trips back through HeifCoder.Decode. Covers a 64x64 and a non-64 (48x48) grayscale image, plus
// the honest limitations (colour and out-of-range sizes throw).
public sealed class HeifAvifEncodeTests
{
    private static ImageFrame GrayRamp(int w, int h)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, false);
        for (long y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < w; x++)
            {
                byte v = (byte)(40 + (x + y) * 150 / (w + h));
                ushort q = Quantum.ScaleFromByte(v);
                int o = (int)x * ch;
                for (int c = 0; c < ch; c++)
                {
                    row[o + c] = q;
                }
            }
        }

        return f;
    }

    private static (double rmse, int maxErr) RoundTrip(ImageFrame src, int qp)
    {
        byte[] avif = HeifCoder.Encode(src, HeifContainerType.Avif, qp);
        int w = (int)src.Columns, h = (int)src.Rows;

        // Must be a valid AVIF that our own decoder reads back.
        if (!HeifCoder.IsAvif(avif))
        {
            return (999, 999);
        }

        ImageFrame dec = HeifCoder.Decode(avif);
        double sse = 0; int maxErr = 0;
        for (long y = 0; y < h; y++)
        {
            for (long x = 0; x < w; x++)
            {
                int a = (src.GetPixelChannel(x, y, 0) * 255 + 32767) / 65535;
                int b = (dec.GetPixelChannel(x, y, 0) * 255 + 32767) / 65535;
                int d = a - b;
                sse += d * d;
                maxErr = System.Math.Max(maxErr, System.Math.Abs(d));
            }
        }

        return (System.Math.Sqrt(sse / (w * (double)h)), maxErr);
    }

    [Test]
    public async Task Avif_64x64_Grayscale_RoundTrips()
    {
        (double rmse, int _) = RoundTrip(GrayRamp(64, 64), qp: 10);
        await Assert.That(rmse).IsLessThan(3.0);
    }

    [Test]
    [Arguments(48, 48)]  // 64-block
    [Arguments(24, 24)]  // 32-block (one forced split)
    [Arguments(16, 16)]  // 16-block (two forced splits)
    [Arguments(30, 28)]  // near-square, 32-block
    [Arguments(8, 8)]    // 8-block (three forced splits)
    public async Task Avif_SmallSquare_RoundTrips(int w, int h)
    {
        (double rmse, int _) = RoundTrip(GrayRamp(w, h), qp: 10);
        await Assert.That(rmse).IsLessThan(4.0);
    }

    [Test]
    public async Task Avif_Colour_NonMultipleOf64_RoundTrips()
    {
        // 96x96 colour (a non-64-multiple >64) now encodes via edge-split partitioning; verify it decodes.
        var f = new ImageFrame();
        f.Initialize(96, 96, ColorspaceType.SRGB, false);
        for (long y = 0; y < 96; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < 96; x++)
            {
                int o = (int)x * ch;
                row[o] = Quantum.ScaleFromByte(200);            // R
                if (ch > 1) row[o + 1] = Quantum.ScaleFromByte(50); // G ≠ R ⇒ colour
                if (ch > 2) row[o + 2] = Quantum.ScaleFromByte(50);
            }
        }

        byte[] avif = HeifCoder.Encode(f, HeifContainerType.Avif, 10);
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(96);
        await Assert.That((int)dec.Rows).IsEqualTo(96);
    }

    [Test]
    public async Task Avif_Rectangular_RoundTrips()
    {
        // 40x20 (non-square, one dim < 64) now routes through the multi-SB path with edge force-split.
        byte[] avif = HeifCoder.Encode(GrayRamp(40, 20), HeifContainerType.Avif, 10);
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(40);
        await Assert.That((int)dec.Rows).IsEqualTo(20);
    }

    [Test]
    public async Task Avif_Alpha_RoundTrips()
    {
        // RGBA with a diagonal alpha gradient → 2-item AVIF (colour primary + monochrome alpha aux linked by
        // auxl). Verify our decoder reads back the alpha channel faithfully.
        const int w = 130, h = 97;
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, hasAlpha: true);
        for (long y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            int ch = f.NumberOfChannels;
            for (long x = 0; x < w; x++)
            {
                int o = (int)x * ch;
                row[o] = Quantum.ScaleFromByte((byte)(x * 255 / w));
                row[o + 1] = Quantum.ScaleFromByte((byte)(y * 255 / h));
                row[o + 2] = Quantum.ScaleFromByte(128);
                row[o + 3] = Quantum.ScaleFromByte((byte)((x + y) * 255 / (w + h)));
            }
        }

        byte[] avif = HeifCoder.Encode(f, HeifContainerType.Avif, 15);
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That(dec.HasAlpha).IsTrue();

        int aOff = dec.NumberOfChannels - 1;
        double sse = 0;
        for (long y = 0; y < h; y++)
        {
            for (long x = 0; x < w; x++)
            {
                int sa = (int)((x + y) * 255 / (w + h));
                int da = (dec.GetPixelChannel(x, y, aOff) * 255 + 32767) / 65535;
                sse += (sa - da) * (double)(sa - da);
            }
        }

        await Assert.That(System.Math.Sqrt(sse / (w * (double)h))).IsLessThan(3.0);
    }
}
