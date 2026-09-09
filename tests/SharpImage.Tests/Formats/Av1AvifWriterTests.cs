using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// Verifies the AVIF container: encode a real 64x64 monochrome image to a .avif file and dump it for external
// decoders. Basic structural asserts here (ftyp brand, boxes present); the strong cross-check (ffmpeg opens the
// .avif and decodes matching pixels) runs from the dump test.
public sealed class Av1AvifWriterTests
{
    private static byte[] Ramp()
    {
        var px = new byte[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                px[y * 64 + x] = (byte)(40 + (x + y) * 130 / 126);
            }
        }

        return px;
    }

    private static bool Contains(byte[] hay, string needle, int start = 0)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        for (int i = start; i <= hay.Length - n.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < n.Length; j++)
            {
                if (hay[i + j] != n[j]) { ok = false; break; }
            }

            if (ok) return true;
        }

        return false;
    }

    [Test]
    public async Task Avif_HasExpectedStructure()
    {
        byte[] avif = Av1StillImageEncoder.EncodeAvifMonochrome64(Ramp(), baseQIdx: 20);

        // ftyp ... avif brand, and the key boxes for an AV1 image item.
        await Assert.That(Contains(avif, "ftyp")).IsTrue();
        await Assert.That(Contains(avif, "avif")).IsTrue();
        await Assert.That(Contains(avif, "meta")).IsTrue();
        await Assert.That(Contains(avif, "av1C")).IsTrue();
        await Assert.That(Contains(avif, "av01")).IsTrue();
        await Assert.That(Contains(avif, "ispe")).IsTrue();
        await Assert.That(Contains(avif, "mdat")).IsTrue();
        // Reasonable size for a 64x64 still.
        await Assert.That(avif.Length).IsGreaterThan(120);
        await Assert.That(avif.Length).IsLessThan(4096);
    }

    [Test]
    public async Task Avif_RoundTripsThroughHeifCoder()
    {
        byte[] src = Ramp();
        byte[] avif = Av1StillImageEncoder.EncodeAvifMonochrome64(src, baseQIdx: 16);

        await Assert.That(HeifCoder.CanDecode(avif)).IsTrue();
        await Assert.That(HeifCoder.IsAvif(avif)).IsTrue();

        ImageFrame frame = HeifCoder.Decode(avif);
        await Assert.That((int)frame.Columns).IsEqualTo(64);
        await Assert.That((int)frame.Rows).IsEqualTo(64);

        // Monochrome ⇒ gray: each channel equals luma. Reconstruction is near-lossless at q16.
        double sse = 0;
        int maxErr = 0;
        for (long y = 0; y < 64; y++)
        {
            for (long x = 0; x < 64; x++)
            {
                int v16 = frame.GetPixelChannel(x, y, 0);
                int v8 = (v16 * 255 + 32767) / 65535;
                int d = src[y * 64 + x] - v8;
                sse += d * d;
                maxErr = System.Math.Max(maxErr, System.Math.Abs(d));
            }
        }

        await Assert.That(System.Math.Sqrt(sse / 4096.0)).IsLessThan(2.0);
    }
}
