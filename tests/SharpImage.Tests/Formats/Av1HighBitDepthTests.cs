using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// AVIF high-bit-depth (10/12-bit) encode + full-precision decode, automatic depth selection, and the odd-width
// 4:2:0 decoder regression (chroma stride under-allocation for widths just past a multiple of 128).
public sealed class Av1HighBitDepthTests
{
    // Smooth 16-bit colour/gray field (+ optional smooth alpha) carrying full 16-bit precision.
    private static ImageFrame Smooth16(int w, int h, bool gray = false, bool alpha = false)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                double t = (x + 0.5 * y) / (w + 0.5 * h);
                double r = 0.1 + 0.8 * t, g = 0.5 + 0.3 * System.Math.Sin(5 * t), b = 0.9 - 0.7 * t;
                if (gray) g = b = r;
                row[x * ch] = (ushort)System.Math.Round(r * 65535);
                row[x * ch + 1] = (ushort)System.Math.Round(g * 65535);
                row[x * ch + 2] = (ushort)System.Math.Round(b * 65535);
                if (alpha) row[x * ch + 3] = (ushort)System.Math.Round((0.25 + 0.7 * y / (h - 1.0)) * 65535);
            }
        }
        return f;
    }

    // RMSE in 16-bit sample units over the first `chans` channels.
    private static double Rmse16(ImageFrame a, ImageFrame b, int chans)
    {
        double sse = 0; long n = 0;
        for (long y = 0; y < a.Rows; y++)
            for (long x = 0; x < a.Columns; x++)
                for (int c = 0; c < chans; c++)
                {
                    double d = a.GetPixelChannel(x, y, c) - b.GetPixelChannel(x, y, c);
                    sse += d * d; n++;
                }
        return System.Math.Sqrt(sse / n);
    }

    // Fraction of samples in channel c that are not exact 8-bit values (v8 * 257) — i.e. carry >8-bit precision.
    private static double SubByteFraction(ImageFrame f, int c)
    {
        long sub = 0, n = 0;
        for (long y = 0; y < f.Rows; y++)
            for (long x = 0; x < f.Columns; x++) { if (f.GetPixelChannel(x, y, c) % 257 != 0) sub++; n++; }
        return sub / (double)n;
    }

    // Reads (high_bitdepth, twelve_bit, seq_profile) from the file's av1C box.
    private static (bool Hbd, bool Twelve, int Profile) Av1C(byte[] avif)
    {
        for (int i = 0; i + 8 < avif.Length; i++)
            if (avif[i] == 'a' && avif[i + 1] == 'v' && avif[i + 2] == '1' && avif[i + 3] == 'C')
                return ((avif[i + 6] & 0x40) != 0, (avif[i + 6] & 0x20) != 0, avif[i + 5] >> 5);
        throw new System.InvalidOperationException("no av1C");
    }

    private static bool HasBrand(byte[] avif, string brand)
    {
        int ftypLen = (avif[0] << 24) | (avif[1] << 16) | (avif[2] << 8) | avif[3];
        for (int i = 8; i + 4 <= ftypLen; i += 4)
            if (System.Text.Encoding.ASCII.GetString(avif, i, 4) == brand) return true;
        return false;
    }

    [Test]
    [Arguments(129, 40)]
    [Arguments(257, 33)]
    [Arguments(385, 24)]
    public async Task OddWidth420_DecodesAndRoundTrips(int w, int h)
    {
        // Widths whose floor(w/2) sits on a multiple of 64 used to under-allocate the decoder's chroma stride
        // (chroma reconstructs to the MI-grid edge), overlapping rows and failing the reference copy.
        var src = Smooth16(w, h);
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 8, BitDepth = 8 });
        ImageFrame dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(w);
        await Assert.That(Rmse16(src, dec, 3) / 257.0).IsLessThan(4.0);
    }

    [Test]
    [Arguments(10)]
    [Arguments(12)]
    public async Task HighBitDepth_Color_KeepsPrecision(int bd)
    {
        var src = Smooth16(99, 61);   // odd dims
        byte[] a8 = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 2, BitDepth = 8, ChromaSubsampling = AvifChromaSubsampling.Yuv420 });
        byte[] ah = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 2, BitDepth = bd, ChromaSubsampling = AvifChromaSubsampling.Yuv420 });
        var c = Av1C(ah);
        await Assert.That(c.Hbd).IsTrue();
        await Assert.That(c.Twelve).IsEqualTo(bd == 12);
        await Assert.That(c.Profile).IsEqualTo(bd == 12 ? 2 : 0);
        await Assert.That(HasBrand(ah, "MA1B")).IsEqualTo(bd != 12);   // 12-bit (Professional) fits no AVIF profile brand

        ImageFrame d8 = HeifCoder.Decode(a8), dh = HeifCoder.Decode(ah);
        // The decode must carry the extra precision (not squeezed to 8-bit)...
        await Assert.That(SubByteFraction(dh, 0)).IsGreaterThan(0.5);
        // ...and near-lossless it must beat 8-bit coding of the same 16-bit source (no 8-bit rounding floor).
        double e8 = Rmse16(src, d8, 3), eh = Rmse16(src, dh, 3);
        System.Console.WriteLine($"[HBD] bd={bd} rmse16 8-bit={e8:F1} {bd}-bit={eh:F1}");
        await Assert.That(eh).IsLessThan(e8);
    }

    // 4:4:4 (High profile; Professional at 12-bit) and 4:2:2 (Professional): av1C profile + subsampling bits,
    // the AVIF profile brand (MA1A only for High), and a round trip that keeps full-resolution chroma.
    [Test]
    [Arguments(AvifChromaSubsampling.Yuv444, 8)]
    [Arguments(AvifChromaSubsampling.Yuv444, 10)]
    [Arguments(AvifChromaSubsampling.Yuv444, 12)]
    [Arguments(AvifChromaSubsampling.Yuv422, 8)]
    [Arguments(AvifChromaSubsampling.Yuv422, 10)]
    [Arguments(AvifChromaSubsampling.Yuv422, 12)]
    public async Task ChromaLayouts_SignalledAndRoundTrip(AvifChromaSubsampling layout, int bd)
    {
        var src = ChromaStripes(131, 70, alpha: bd == 10);   // odd dims; alpha exercises the 2-item path
        byte[] avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 4, BitDepth = bd, ChromaSubsampling = layout });
        bool i444 = layout == AvifChromaSubsampling.Yuv444;
        int b = Av1CByte2(avif);
        await Assert.That((b & 0x08) != 0).IsEqualTo(!i444);   // chroma_subsampling_x
        await Assert.That((b & 0x04) != 0).IsFalse();          // chroma_subsampling_y (4:2:0 only)
        await Assert.That(Av1C(avif).Profile).IsEqualTo(i444 && bd < 12 ? 1 : 2);
        await Assert.That(HasBrand(avif, "MA1A")).IsEqualTo(i444 && bd < 12);
        await Assert.That(HasBrand(avif, "MA1B")).IsFalse();

        var dec = HeifCoder.Decode(avif);
        await Assert.That((int)dec.Columns).IsEqualTo(131);
        await Assert.That(dec.HasAlpha).IsEqualTo(bd == 10);
        double e = Rmse16(src, dec, 3) / 257.0;
        System.Console.WriteLine($"[Layout] {layout} bd={bd} rmse8={e:F2}");
        // 1-px colour rows: 4:2:0 halves chroma vertically and loses them; 4:2:2 and 4:4:4 keep full chroma rows.
        var d420 = HeifCoder.Decode(HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 4, BitDepth = bd, ChromaSubsampling = AvifChromaSubsampling.Yuv420 }));
        await Assert.That(e).IsLessThan(Rmse16(src, d420, 3) / 257.0 * 0.5);
    }

    // Alternating red/blue rows (plus a slow horizontal ramp) — chroma detail at 1-px vertical pitch.
    private static ImageFrame ChromaStripes(int w, int h, bool alpha)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                bool cy = (y & 1) != 0;
                row[x * ch] = (ushort)(cy ? 52000 : 14000);
                row[x * ch + 1] = (ushort)(30000 + x * 100);
                row[x * ch + 2] = (ushort)(cy ? 14000 : 52000);
                if (alpha) row[x * ch + 3] = (ushort)(65535 - x * 200);
            }
        }
        return f;
    }

    private static int Av1CByte2(byte[] avif)
    {
        for (int i = 0; i + 8 < avif.Length; i++)
            if (avif[i] == 'a' && avif[i + 1] == 'v' && avif[i + 2] == '1' && avif[i + 3] == 'C') return avif[i + 6];
        throw new System.InvalidOperationException("no av1C");
    }

    [Test]
    public async Task HighBitDepth_Gray_And_Alpha()
    {
        var gray = Smooth16(80, 72, gray: true);
        ImageFrame dg = HeifCoder.Decode(HeifCoder.EncodeAvif(gray, new AvifEncodeOptions { Qp = 4, BitDepth = 10 }));
        await Assert.That(SubByteFraction(dg, 0)).IsGreaterThan(0.5);
        await Assert.That(Rmse16(gray, dg, 3) / 257.0).IsLessThan(1.5);

        var rgba = Smooth16(72, 56, alpha: true);
        ImageFrame da = HeifCoder.Decode(HeifCoder.EncodeAvif(rgba, new AvifEncodeOptions { Qp = 4, BitDepth = 10 }));
        await Assert.That(da.HasAlpha).IsTrue();
        await Assert.That(SubByteFraction(da, 3)).IsGreaterThan(0.5);
        double aErr = 0; long n = 0;
        for (long y = 0; y < rgba.Rows; y++)
            for (long x = 0; x < rgba.Columns; x++) { double d = rgba.GetPixelChannel(x, y, 3) - da.GetPixelChannel(x, y, 3); aErr += d * d; n++; }
        await Assert.That(System.Math.Sqrt(aErr / n) / 257.0).IsLessThan(1.0);
    }

    [Test]
    public async Task AutoDepth_PicksEightForByteSources_TenForDeepSources()
    {
        var deep = Smooth16(64, 48);
        var byteOrigin = new ImageFrame();
        byteOrigin.Initialize(64, 48, ColorspaceType.SRGB, false);
        for (int y = 0; y < 48; y++)
        {
            var row = byteOrigin.GetPixelRowForWrite(y);
            for (int x = 0; x < 64 * 3; x++) row[x] = Quantum.ScaleFromByte((byte)((x * 3 + y * 5) & 255));
        }
        await Assert.That(Av1C(HeifCoder.EncodeAvif(byteOrigin)).Hbd).IsFalse();
        await Assert.That(Av1C(HeifCoder.EncodeAvif(deep)).Hbd).IsTrue();
        await Assert.That(Av1C(HeifCoder.Encode(deep, HeifContainerType.Avif, 20)).Hbd).IsTrue();
    }
}
