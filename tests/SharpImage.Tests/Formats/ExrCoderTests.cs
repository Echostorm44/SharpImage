using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>Tests for OpenEXR format coder.</summary>
public class ExrCoderTests
{
    [Test]
    public async Task Exr_CanDecode_ValidMagic()
    {
        byte[] magic = [0x76, 0x2F, 0x31, 0x01, 0x02, 0x00, 0x00, 0x00];
        await Assert.That(ExrCoder.CanDecode(magic)).IsTrue();
    }

    [Test]
    public async Task Exr_CanDecode_InvalidMagic()
    {
        byte[] notExr = [0x89, 0x50, 0x4E, 0x47]; // PNG magic
        await Assert.That(ExrCoder.CanDecode(notExr)).IsFalse();
    }

    [Test]
    public async Task Exr_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(32, 32, 200, 100, 50);
        byte[] encoded = ExrCoder.Encode(frame);
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // EXR uses HALF precision, so allow some tolerance
        var px = decoded.GetPixel(16, 16);
        int r = Quantum.ScaleToByte((ushort)px.Red);
        int g = Quantum.ScaleToByte((ushort)px.Green);
        int b = Quantum.ScaleToByte((ushort)px.Blue);

        await Assert.That(r).IsGreaterThanOrEqualTo(195).And.IsLessThanOrEqualTo(205);
        await Assert.That(g).IsGreaterThanOrEqualTo(95).And.IsLessThanOrEqualTo(105);
        await Assert.That(b).IsGreaterThanOrEqualTo(45).And.IsLessThanOrEqualTo(55);
    }

    [Test]
    public async Task Exr_RoundTrip_WithAlpha()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 16, ColorspaceType.SRGB, true);
        for (int y = 0; y < 16; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 16; x++)
            {
                int off = x * 4;
                row[off] = Quantum.ScaleFromByte(255);     // R
                row[off + 1] = Quantum.ScaleFromByte(128); // G
                row[off + 2] = Quantum.ScaleFromByte(64);  // B
                row[off + 3] = Quantum.ScaleFromByte(192); // A
            }
        }

        byte[] encoded = ExrCoder.Encode(frame);
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.HasAlpha).IsTrue();
        await Assert.That(decoded.Columns).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        int a = Quantum.ScaleToByte((ushort)px.Alpha);
        await Assert.That(a).IsGreaterThanOrEqualTo(188).And.IsLessThanOrEqualTo(196);
    }

    [Test]
    public async Task Exr_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(64, 64);
        byte[] encoded = ExrCoder.Encode(frame);
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(64u);
        await Assert.That(decoded.Rows).IsEqualTo(64u);

        // Top-left should be dark
        var topLeft = decoded.GetPixel(0, 0);
        int tlr = Quantum.ScaleToByte((ushort)topLeft.Red);
        await Assert.That(tlr).IsLessThan(10);

        // Bottom-right should be bright
        var bottomRight = decoded.GetPixel(63, 63);
        int brr = Quantum.ScaleToByte((ushort)bottomRight.Red);
        await Assert.That(brr).IsGreaterThan(200);
    }

    [Test]
    public async Task Exr_RoundTrip_ZipsCompression()
    {
        var frame = CreateSolidFrame(32, 32, 100, 150, 200);
        byte[] encoded = ExrCoder.Encode(frame, compressionType: 2); // ZIPS
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        var px = decoded.GetPixel(16, 16);
        int g = Quantum.ScaleToByte((ushort)px.Green);
        await Assert.That(g).IsGreaterThanOrEqualTo(145).And.IsLessThanOrEqualTo(155);
    }

    [Test]
    public async Task Exr_RoundTrip_NoCompression()
    {
        var frame = CreateSolidFrame(16, 16, 128, 128, 128);
        byte[] encoded = ExrCoder.Encode(frame, compressionType: 0); // NONE
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        var px = decoded.GetPixel(8, 8);
        int r = Quantum.ScaleToByte((ushort)px.Red);
        await Assert.That(r).IsGreaterThanOrEqualTo(124).And.IsLessThanOrEqualTo(132);
    }

    [Test]
    public async Task Exr_RoundTrip_RleCompression()
    {
        var frame = CreateSolidFrame(32, 32, 255, 0, 0);
        byte[] encoded = ExrCoder.Encode(frame, compressionType: 1); // RLE
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        var px = decoded.GetPixel(16, 16);
        int r = Quantum.ScaleToByte((ushort)px.Red);
        await Assert.That(r).IsGreaterThanOrEqualTo(250);
    }

    [Test]
    public async Task Exr_FormatRegistry_DetectsExr()
    {
        var frame = CreateSolidFrame(8, 8, 200, 100, 50);
        byte[] encoded = ExrCoder.Encode(frame);
        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Exr);
    }

    [Test]
    public async Task Exr_FormatRegistry_ExtensionDetection()
    {
        var format = FormatRegistry.DetectFromExtension("test.exr");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Exr);
    }

    [Test]
    public async Task Exr_LargeImage_RoundTrip()
    {
        // Test with a larger image to exercise multi-block compression
        var frame = CreateGradientFrame(256, 256);
        byte[] encoded = ExrCoder.Encode(frame);
        var decoded = ExrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);

        // Spot check center pixel
        var center = decoded.GetPixel(128, 128);
        int r = Quantum.ScaleToByte((ushort)center.Red);
        // Center of gradient should be around 128
        await Assert.That(r).IsGreaterThan(100).And.IsLessThan(160);
    }

    #region Helpers

    private static ImageFrame CreateSolidFrame(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        bool hasAlpha = a < 255;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, hasAlpha);
        int ch = hasAlpha ? 4 : 3;
        ushort sr = Quantum.ScaleFromByte(r);
        ushort sg = Quantum.ScaleFromByte(g);
        ushort sb = Quantum.ScaleFromByte(b);
        ushort sa = Quantum.ScaleFromByte(a);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                row[off] = sr;
                row[off + 1] = sg;
                row[off + 2] = sb;
                if (hasAlpha) row[off + 3] = sa;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientFrame(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * 3;
                byte val = (byte)((x + y) * 255 / (w + h - 2));
                row[off] = Quantum.ScaleFromByte(val);
                row[off + 1] = Quantum.ScaleFromByte((byte)(val / 2));
                row[off + 2] = Quantum.ScaleFromByte((byte)(255 - val));
            }
        }
        return frame;
    }

    #endregion
}
