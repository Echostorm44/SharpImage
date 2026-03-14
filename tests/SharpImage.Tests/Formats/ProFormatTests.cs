using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>Tests for QOI, ICO, and HDR format coders.</summary>
public class ProFormatTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    #region QOI Tests

    [Test]
    public async Task Qoi_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(16, 16, 255, 0, 0, 255);
        byte[] encoded = QoiCoder.Encode(frame);
        var decoded = QoiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Qoi_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(64, 64);
        byte[] encoded = QoiCoder.Encode(frame);
        var decoded = QoiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(64u);
        await Assert.That(decoded.Rows).IsEqualTo(64u);

        // Check corners
        var topLeft = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)topLeft.Red)).IsEqualTo(0);

        var bottomRight = decoded.GetPixel(63, 63);
        // Bottom-right should have higher red value
        await Assert.That((int)Quantum.ScaleToByte((ushort)bottomRight.Red)).IsGreaterThan(200);
    }

    [Test]
    public async Task Qoi_RoundTrip_WithAlpha()
    {
        var frame = new ImageFrame();
        frame.Initialize(8, 8, ColorspaceType.SRGB, true);
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 8; x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.ScaleFromByte(200);
                row[offset + 1] = Quantum.ScaleFromByte(100);
                row[offset + 2] = Quantum.ScaleFromByte(50);
                row[offset + ch - 1] = Quantum.ScaleFromByte((byte)(y * 32)); // varying alpha
            }
        }

        byte[] encoded = QoiCoder.Encode(frame);
        var decoded = QoiCoder.Decode(encoded);

        // Row 4 should have alpha ~128
        var px = decoded.GetPixel(0, 4);
        int alpha = Quantum.ScaleToByte((ushort)px.Alpha);
        await Assert.That(alpha).IsEqualTo(128);
    }

    [Test]
    public async Task Qoi_CanDecode_ValidSignature()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = QoiCoder.Encode(frame);
        await Assert.That(QoiCoder.CanDecode(encoded)).IsTrue();
    }

    [Test]
    public async Task Qoi_CanDecode_InvalidData()
    {
        await Assert.That(QoiCoder.CanDecode(new byte[] { 0, 1, 2, 3 })).IsFalse();
    }

    [Test]
    public async Task Qoi_Header_ContainsMagicAndDimensions()
    {
        var frame = CreateSolidFrame(100, 50, 0, 255, 0, 255);
        byte[] encoded = QoiCoder.Encode(frame);

        // "qoif" magic
        await Assert.That(encoded[0]).IsEqualTo((byte)'q');
        await Assert.That(encoded[1]).IsEqualTo((byte)'o');
        await Assert.That(encoded[2]).IsEqualTo((byte)'i');
        await Assert.That(encoded[3]).IsEqualTo((byte)'f');

        // Width = 100 (big-endian)
        int width = (encoded[4] << 24) | (encoded[5] << 16) | (encoded[6] << 8) | encoded[7];
        await Assert.That(width).IsEqualTo(100);

        // Height = 50 (big-endian)
        int height = (encoded[8] << 24) | (encoded[9] << 16) | (encoded[10] << 8) | encoded[11];
        await Assert.That(height).IsEqualTo(50);
    }

    [Test]
    public async Task Qoi_EndMarker_Present()
    {
        var frame = CreateSolidFrame(4, 4, 0, 0, 255, 255);
        byte[] encoded = QoiCoder.Encode(frame);

        // End marker: 7×0x00 + 0x01
        int len = encoded.Length;
        for (int i = 0; i < 7; i++)
            await Assert.That(encoded[len - 8 + i]).IsEqualTo((byte)0);
        await Assert.That(encoded[len - 1]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Qoi_RoundTrip_LargerImage()
    {
        var frame = CreateGradientFrame(256, 256);
        byte[] encoded = QoiCoder.Encode(frame);
        var decoded = QoiCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);

        // Sample several pixels for correctness
        for (int y = 0; y < 256; y += 64)
        {
            for (int x = 0; x < 256; x += 64)
            {
                var orig = frame.GetPixel(x, y);
                var dec = decoded.GetPixel(x, y);
                await Assert.That(Math.Abs((int)Quantum.ScaleToByte((ushort)orig.Red) - (int)Quantum.ScaleToByte((ushort)dec.Red)))
                    .IsLessThanOrEqualTo(1);
                await Assert.That(Math.Abs((int)Quantum.ScaleToByte((ushort)orig.Green) - (int)Quantum.ScaleToByte((ushort)dec.Green)))
                    .IsLessThanOrEqualTo(1);
            }
        }
    }

    [Test]
    public async Task Qoi_Compression_SolidIsSmallerThanRaw()
    {
        var frame = CreateSolidFrame(64, 64, 128, 128, 128, 255);
        byte[] encoded = QoiCoder.Encode(frame);
        int rawSize = 64 * 64 * 4;

        // Solid color should compress very well via RUN opcodes
        await Assert.That(encoded.Length).IsLessThan(rawSize);
    }

    #endregion

    #region ICO Tests

    [Test]
    public async Task Ico_RoundTrip_SmallIcon()
    {
        var frame = CreateSolidFrame(16, 16, 0, 128, 255, 255);
        byte[] encoded = IcoCoder.Encode(frame);
        var decoded = IcoCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(128);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(255);
    }

    [Test]
    public async Task Ico_RoundTrip_32x32()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = IcoCoder.Encode(frame);
        var decoded = IcoCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // Verify gradient pixels
        var px0 = decoded.GetPixel(0, 0);
        var px31 = decoded.GetPixel(31, 31);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px31.Red)).IsGreaterThan((int)Quantum.ScaleToByte((ushort)px0.Red));
    }

    [Test]
    public async Task Ico_RoundTrip_WithAlpha()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 16, ColorspaceType.SRGB, true);
        for (int y = 0; y < 16; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.MaxValue;
                row[offset + 1] = 0;
                row[offset + 2] = 0;
                // Left half transparent, right half opaque
                row[offset + ch - 1] = x < 8 ? (ushort)0 : Quantum.MaxValue;
            }
        }

        byte[] encoded = IcoCoder.Encode(frame);
        var decoded = IcoCoder.Decode(encoded);

        var transparent = decoded.GetPixel(4, 8);
        var opaque = decoded.GetPixel(12, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)transparent.Alpha)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)opaque.Alpha)).IsEqualTo(255);
    }

    [Test]
    public async Task Ico_CanDecode_ValidHeader()
    {
        var frame = CreateSolidFrame(16, 16, 100, 100, 100, 255);
        byte[] encoded = IcoCoder.Encode(frame);
        await Assert.That(IcoCoder.CanDecode(encoded)).IsTrue();
    }

    [Test]
    public async Task Ico_CanDecode_InvalidData()
    {
        await Assert.That(IcoCoder.CanDecode(new byte[] { 1, 2, 3, 4, 5, 6 })).IsFalse();
    }

    [Test]
    public async Task Ico_Header_CorrectStructure()
    {
        var frame = CreateSolidFrame(32, 32, 255, 0, 0, 255);
        byte[] encoded = IcoCoder.Encode(frame);

        // Reserved = 0
        await Assert.That(encoded[0]).IsEqualTo((byte)0);
        await Assert.That(encoded[1]).IsEqualTo((byte)0);
        // Type = 1 (ICO)
        await Assert.That(encoded[2]).IsEqualTo((byte)1);
        await Assert.That(encoded[3]).IsEqualTo((byte)0);
        // Count = 1
        await Assert.That(encoded[4]).IsEqualTo((byte)1);
        await Assert.That(encoded[5]).IsEqualTo((byte)0);
        // Width = 32, Height = 32
        await Assert.That(encoded[6]).IsEqualTo((byte)32);
        await Assert.That(encoded[7]).IsEqualTo((byte)32);
    }

    [Test]
    public async Task Ico_MaxSize_256x256()
    {
        var frame = CreateSolidFrame(256, 256, 0, 0, 0, 255);
        byte[] encoded = IcoCoder.Encode(frame);
        var decoded = IcoCoder.Decode(encoded);
        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);
    }

    [Test]
    public async Task Ico_TooLarge_ThrowsException()
    {
        var frame = CreateSolidFrame(300, 300, 0, 0, 0, 255);
        await Assert.That(() => IcoCoder.Encode(frame)).Throws<ArgumentException>();
    }

    #endregion

    #region HDR Tests

    [Test]
    public async Task Hdr_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(16, 16, 200, 100, 50, 255);
        byte[] encoded = HdrCoder.Encode(frame);
        var decoded = HdrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        // HDR has lossy tone-mapping, so allow tolerance
        var px = decoded.GetPixel(8, 8);
        int r = Quantum.ScaleToByte((ushort)px.Red);
        int g = Quantum.ScaleToByte((ushort)px.Green);
        int b = Quantum.ScaleToByte((ushort)px.Blue);
        await Assert.That(Math.Abs(r - 200)).IsLessThan(30);
        await Assert.That(Math.Abs(g - 100)).IsLessThan(30);
        await Assert.That(Math.Abs(b - 50)).IsLessThan(30);
    }

    [Test]
    public async Task Hdr_RoundTrip_BlackPixels()
    {
        var frame = CreateSolidFrame(8, 8, 0, 0, 0, 255);
        byte[] encoded = HdrCoder.Encode(frame);
        var decoded = HdrCoder.Decode(encoded);

        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Hdr_CanDecode_ValidHeader()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = HdrCoder.Encode(frame);
        await Assert.That(HdrCoder.CanDecode(encoded)).IsTrue();
    }

    [Test]
    public async Task Hdr_CanDecode_InvalidData()
    {
        await Assert.That(HdrCoder.CanDecode(new byte[] { 0, 1, 2, 3 })).IsFalse();
        await Assert.That(HdrCoder.CanDecode("NOT HDR"u8)).IsFalse();
    }

    [Test]
    public async Task Hdr_Header_ContainsRadianceSignature()
    {
        var frame = CreateSolidFrame(8, 8, 128, 128, 128, 255);
        byte[] encoded = HdrCoder.Encode(frame);

        string header = System.Text.Encoding.ASCII.GetString(encoded, 0, 10);
        await Assert.That(header).IsEqualTo("#?RADIANCE");
    }

    [Test]
    public async Task Hdr_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = HdrCoder.Encode(frame);
        var decoded = HdrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // Dark corner should still be dark, bright corner brighter
        var dark = decoded.GetPixel(0, 0);
        var bright = decoded.GetPixel(31, 31);
        await Assert.That((int)Quantum.ScaleToByte((ushort)bright.Red)).IsGreaterThan((int)Quantum.ScaleToByte((ushort)dark.Red));
    }

    [Test]
    public async Task Hdr_RoundTrip_WhitePixels()
    {
        var frame = CreateSolidFrame(8, 8, 255, 255, 255, 255);
        byte[] encoded = HdrCoder.Encode(frame);
        var decoded = HdrCoder.Decode(encoded);

        var px = decoded.GetPixel(4, 4);
        // White through Reinhard is high but not 255 (lossy)
        int r = Quantum.ScaleToByte((ushort)px.Red);
        await Assert.That(r).IsGreaterThan(200);
    }

    [Test]
    public async Task Hdr_LargerImage_RoundTrip()
    {
        var frame = CreateGradientFrame(128, 128);
        byte[] encoded = HdrCoder.Encode(frame);
        var decoded = HdrCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(128u);
        await Assert.That(decoded.Rows).IsEqualTo(128u);
    }

    #endregion

    #region PSD Tests

    [Test]
    public async Task Psd_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(16, 16, 255, 0, 0, 255);
        byte[] encoded = PsdCoder.Encode(frame);
        var decoded = PsdCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Psd_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(64, 64);
        byte[] encoded = PsdCoder.Encode(frame);
        var decoded = PsdCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(64u);
        await Assert.That(decoded.Rows).IsEqualTo(64u);

        for (int y = 0; y < 64; y += 16)
        {
            for (int x = 0; x < 64; x += 16)
            {
                var orig = frame.GetPixel(x, y);
                var dec = decoded.GetPixel(x, y);
                await Assert.That(Math.Abs((int)Quantum.ScaleToByte((ushort)orig.Red) - (int)Quantum.ScaleToByte((ushort)dec.Red)))
                    .IsLessThanOrEqualTo(1);
            }
        }
    }

    [Test]
    public async Task Psd_RoundTrip_WithAlpha()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 16, ColorspaceType.SRGB, true);
        for (int y = 0; y < 16; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.MaxValue;
                row[offset + 1] = 0;
                row[offset + 2] = 0;
                row[offset + ch - 1] = Quantum.ScaleFromByte((byte)(y * 16));
            }
        }

        byte[] encoded = PsdCoder.Encode(frame);
        var decoded = PsdCoder.Decode(encoded);

        var px0 = decoded.GetPixel(0, 0);
        var px8 = decoded.GetPixel(0, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px0.Alpha)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px8.Alpha)).IsEqualTo(128);
    }

    [Test]
    public async Task Psd_CanDecode_ValidHeader()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = PsdCoder.Encode(frame);
        await Assert.That(PsdCoder.CanDecode(encoded)).IsTrue();
    }

    [Test]
    public async Task Psd_CanDecode_InvalidData()
    {
        await Assert.That(PsdCoder.CanDecode(new byte[] { 0, 1, 2, 3 })).IsFalse();
    }

    [Test]
    public async Task Psd_Header_Contains8BPS()
    {
        var frame = CreateSolidFrame(8, 8, 128, 128, 128, 255);
        byte[] encoded = PsdCoder.Encode(frame);
        await Assert.That(encoded[0]).IsEqualTo((byte)'8');
        await Assert.That(encoded[1]).IsEqualTo((byte)'B');
        await Assert.That(encoded[2]).IsEqualTo((byte)'P');
        await Assert.That(encoded[3]).IsEqualTo((byte)'S');
    }

    [Test]
    public async Task Psd_LargerImage_RoundTrip()
    {
        var frame = CreateGradientFrame(128, 128);
        byte[] encoded = PsdCoder.Encode(frame);
        var decoded = PsdCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(128u);
        await Assert.That(decoded.Rows).IsEqualTo(128u);
    }

    #endregion

    #region DDS Tests

    [Test]
    public async Task Dds_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(16, 16, 0, 255, 0, 255);
        byte[] encoded = DdsCoder.Encode(frame);
        var decoded = DdsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Dds_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = DdsCoder.Encode(frame);
        var decoded = DdsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        for (int y = 0; y < 32; y += 8)
        {
            for (int x = 0; x < 32; x += 8)
            {
                var orig = frame.GetPixel(x, y);
                var dec = decoded.GetPixel(x, y);
                await Assert.That(Math.Abs((int)Quantum.ScaleToByte((ushort)orig.Red) - (int)Quantum.ScaleToByte((ushort)dec.Red)))
                    .IsLessThanOrEqualTo(1);
            }
        }
    }

    [Test]
    public async Task Dds_RoundTrip_WithAlpha()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 16, ColorspaceType.SRGB, true);
        for (int y = 0; y < 16; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.ScaleFromByte(200);
                row[offset + 1] = Quantum.ScaleFromByte(100);
                row[offset + 2] = Quantum.ScaleFromByte(50);
                row[offset + ch - 1] = Quantum.ScaleFromByte((byte)(x * 16));
            }
        }

        byte[] encoded = DdsCoder.Encode(frame);
        var decoded = DdsCoder.Decode(encoded);

        var px = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Alpha)).IsEqualTo(0);
        var pxM = decoded.GetPixel(8, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxM.Alpha)).IsEqualTo(128);
    }

    [Test]
    public async Task Dds_CanDecode_ValidHeader()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = DdsCoder.Encode(frame);
        await Assert.That(DdsCoder.CanDecode(encoded)).IsTrue();
    }

    [Test]
    public async Task Dds_CanDecode_InvalidData()
    {
        await Assert.That(DdsCoder.CanDecode(new byte[] { 0, 1, 2, 3 })).IsFalse();
    }

    [Test]
    public async Task Dds_Header_ContainsMagic()
    {
        var frame = CreateSolidFrame(8, 8, 0, 0, 255, 255);
        byte[] encoded = DdsCoder.Encode(frame);
        // "DDS " = 0x44 0x44 0x53 0x20
        await Assert.That(encoded[0]).IsEqualTo((byte)'D');
        await Assert.That(encoded[1]).IsEqualTo((byte)'D');
        await Assert.That(encoded[2]).IsEqualTo((byte)'S');
        await Assert.That(encoded[3]).IsEqualTo((byte)' ');
    }

    [Test]
    public async Task Dds_LargerImage_RoundTrip()
    {
        var frame = CreateGradientFrame(128, 64);
        byte[] encoded = DdsCoder.Encode(frame);
        var decoded = DdsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(128u);
        await Assert.That(decoded.Rows).IsEqualTo(64u);
    }

    #endregion

    #region SVG Tests

    [Test]
    public async Task Svg_CanDecode_ValidSvg()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"red\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        await Assert.That(SvgCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task Svg_CanDecode_InvalidData()
    {
        await Assert.That(SvgCoder.CanDecode(System.Text.Encoding.UTF8.GetBytes("hello world"))).IsFalse();
    }

    [Test]
    public async Task Svg_Decode_RedRectangle()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\">" +
                      "<rect x=\"0\" y=\"0\" width=\"32\" height=\"32\" fill=\"red\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        var frame = SvgCoder.Decode(data);

        await Assert.That(frame.Columns).IsEqualTo(32u);
        await Assert.That(frame.Rows).IsEqualTo(32u);

        // Center should be red
        var px = frame.GetPixel(16, 16);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsGreaterThan(200);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsLessThan(50);
    }

    [Test]
    public async Task Svg_Decode_BlueCircle()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\">" +
                      "<circle cx=\"32\" cy=\"32\" r=\"20\" fill=\"blue\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        var frame = SvgCoder.Decode(data);

        // Center should be blue
        var center = frame.GetPixel(32, 32);
        await Assert.That((int)Quantum.ScaleToByte((ushort)center.Blue)).IsGreaterThan(200);
        await Assert.That((int)Quantum.ScaleToByte((ushort)center.Red)).IsLessThan(50);

        // Corner should be white (background)
        var corner = frame.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)corner.Red)).IsGreaterThan(200);
        await Assert.That((int)Quantum.ScaleToByte((ushort)corner.Green)).IsGreaterThan(200);
    }

    [Test]
    public async Task Svg_Decode_PathElement()
    {
        // Simple triangle path
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\">" +
                      "<path d=\"M 32 5 L 59 59 L 5 59 Z\" fill=\"green\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        var frame = SvgCoder.Decode(data);

        // Center of the triangle should be green-ish
        var center = frame.GetPixel(32, 40);
        await Assert.That((int)Quantum.ScaleToByte((ushort)center.Green)).IsGreaterThan(80);
    }

    [Test]
    public async Task Svg_Decode_WithViewBox()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\" viewBox=\"0 0 100 100\">" +
                      "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"#FF8800\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        var frame = SvgCoder.Decode(data);

        await Assert.That(frame.Columns).IsEqualTo(64u);
        await Assert.That(frame.Rows).IsEqualTo(64u);
    }

    [Test]
    public async Task Svg_RoundTrip_EncodeProducesSvg()
    {
        var frame = CreateSolidFrame(32, 32, 128, 64, 200, 255);
        byte[] svgData = SvgCoder.Encode(frame);
        string svgText = System.Text.Encoding.UTF8.GetString(svgData);

        await Assert.That(svgText).Contains("<svg");
        await Assert.That(svgText).Contains("width=\"32\"");
        await Assert.That(svgText).Contains("data:image/png;base64");
    }

    [Test]
    public async Task Svg_Decode_HexColor()
    {
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\">" +
                      "<rect width=\"16\" height=\"16\" fill=\"#00FF00\"/></svg>";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(svg);
        var frame = SvgCoder.Decode(data);

        var px = frame.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsGreaterThan(200);
    }

    #endregion

    #region FormatRegistry Tests

    [Test]
    public async Task FormatRegistry_DetectFormat_Png()
    {
        byte[] pngHeader = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
        await Assert.That(FormatRegistry.DetectFormat(pngHeader)).IsEqualTo(ImageFileFormat.Png);
    }

    [Test]
    public async Task FormatRegistry_DetectFormat_Jpeg()
    {
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
        await Assert.That(FormatRegistry.DetectFormat(jpegHeader)).IsEqualTo(ImageFileFormat.Jpeg);
    }

    [Test]
    public async Task FormatRegistry_DetectFormat_Gif()
    {
        byte[] gifHeader = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]; // GIF89a
        await Assert.That(FormatRegistry.DetectFormat(gifHeader)).IsEqualTo(ImageFileFormat.Gif);
    }

    [Test]
    public async Task FormatRegistry_DetectFormat_Bmp()
    {
        byte[] bmpHeader = [(byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        await Assert.That(FormatRegistry.DetectFormat(bmpHeader)).IsEqualTo(ImageFileFormat.Bmp);
    }

    [Test]
    public async Task FormatRegistry_DetectFromExtension()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.png")).IsEqualTo(ImageFileFormat.Png);
        await Assert.That(FormatRegistry.DetectFromExtension("test.jpg")).IsEqualTo(ImageFileFormat.Jpeg);
        await Assert.That(FormatRegistry.DetectFromExtension("test.bmp")).IsEqualTo(ImageFileFormat.Bmp);
        await Assert.That(FormatRegistry.DetectFromExtension("test.webp")).IsEqualTo(ImageFileFormat.WebP);
        await Assert.That(FormatRegistry.DetectFromExtension("test.psd")).IsEqualTo(ImageFileFormat.Psd);
        await Assert.That(FormatRegistry.DetectFromExtension("test.svg")).IsEqualTo(ImageFileFormat.Svg);
        await Assert.That(FormatRegistry.DetectFromExtension("test.xyz")).IsEqualTo(ImageFileFormat.Unknown);
    }

    [Test]
    public async Task FormatRegistry_RoundTrip_PsdViaRegistry()
    {
        var frame = CreateSolidFrame(8, 8, 255, 128, 64, 255);
        byte[] encoded = FormatRegistry.Encode(frame, ImageFileFormat.Psd);
        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Psd);

        var decoded = FormatRegistry.Decode(encoded, format);
        await Assert.That(decoded.Columns).IsEqualTo(8u);
    }

    [Test]
    public async Task FormatRegistry_RoundTrip_QoiViaRegistry()
    {
        var frame = CreateSolidFrame(8, 8, 100, 200, 50, 255);
        byte[] encoded = FormatRegistry.Encode(frame, ImageFileFormat.Qoi);
        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Qoi);
    }

    #endregion

    #region Helpers

    private static ImageFrame CreateSolidFrame(int width, int height, byte r, byte g, byte b, byte a)
    {
        bool hasAlpha = a < 255;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha || true);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.ScaleFromByte(r);
                if (ch > 1) row[offset + 1] = Quantum.ScaleFromByte(g);
                if (ch > 2) row[offset + 2] = Quantum.ScaleFromByte(b);
                row[offset + ch - 1] = Quantum.ScaleFromByte(a);
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientFrame(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x + y) * 255 / (width + height - 2));
                byte g = (byte)(y * 255 / Math.Max(height - 1, 1));
                byte b = (byte)(x * 255 / Math.Max(width - 1, 1));
                int offset = x * ch;
                row[offset] = Quantum.ScaleFromByte(r);
                if (ch > 1) row[offset + 1] = Quantum.ScaleFromByte(g);
                if (ch > 2) row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return frame;
    }

    #endregion
}

