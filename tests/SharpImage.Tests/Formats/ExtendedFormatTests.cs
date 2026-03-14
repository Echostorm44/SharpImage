using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>Tests for Phase 11 extended format coders: Farbfeld, WBMP, PCX, XBM, XPM, DPX, FITS.</summary>
public class ExtendedFormatTests
{
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

    /// <summary>Creates a black & white checkerboard pattern (no alpha).</summary>
    private static ImageFrame CreateCheckerboardFrame(int width, int height, int squareSize)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < width; x++)
            {
                bool isWhite = ((x / squareSize) + (y / squareSize)) % 2 == 0;
                ushort val = isWhite ? Quantum.MaxValue : (ushort)0;
                int offset = x * ch;
                row[offset] = val;
                if (ch > 1) row[offset + 1] = val;
                if (ch > 2) row[offset + 2] = val;
            }
        }
        return frame;
    }

    #endregion

    #region Farbfeld Tests

    [Test]
    public async Task Farbfeld_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(8, 8, 255, 0, 0, 255);
        byte[] encoded = FarbfeldCoder.Encode(frame);
        var decoded = FarbfeldCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Farbfeld_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = FarbfeldCoder.Encode(frame);
        var decoded = FarbfeldCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // Farbfeld is 16-bit lossless — verify pixel values match
        bool allMatch = true;
        for (int y = 0; y < 32 && allMatch; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                var origPx = frame.GetPixel(x, y);
                var decPx = decoded.GetPixel(x, y);
                if ((ushort)origPx.Red != (ushort)decPx.Red ||
                    (ushort)origPx.Green != (ushort)decPx.Green ||
                    (ushort)origPx.Blue != (ushort)decPx.Blue)
                { allMatch = false; break; }
            }
        }
        await Assert.That(allMatch).IsTrue();
    }

    [Test]
    public async Task Farbfeld_CanDecode_ValidSignature()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = FarbfeldCoder.Encode(frame);

        await Assert.That(FarbfeldCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(FarbfeldCoder.CanDecode(new byte[] { 0, 0, 0, 0 })).IsFalse();
    }

    [Test]
    public async Task Farbfeld_HeaderStructure()
    {
        var frame = CreateSolidFrame(10, 20, 0, 255, 0, 255);
        byte[] encoded = FarbfeldCoder.Encode(frame);

        // Header: "farbfeld" (8 bytes) + width BE uint32 + height BE uint32 = 16 bytes
        await Assert.That(encoded.Length).IsGreaterThan(16);
        string magic = System.Text.Encoding.ASCII.GetString(encoded, 0, 8);
        await Assert.That(magic).IsEqualTo("farbfeld");
    }

    [Test]
    public async Task Farbfeld_FormatRegistry_Detection()
    {
        var frame = CreateSolidFrame(4, 4, 100, 200, 50, 255);
        byte[] encoded = FarbfeldCoder.Encode(frame);

        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Farbfeld);
    }

    #endregion

    #region WBMP Tests

    [Test]
    public async Task Wbmp_RoundTrip_Checkerboard()
    {
        var frame = CreateCheckerboardFrame(16, 16, 4);
        byte[] encoded = WbmpCoder.Encode(frame);
        var decoded = WbmpCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        // Check a white square pixel
        var pxWhite = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxWhite.Red)).IsEqualTo(255);

        // Check a black square pixel
        var pxBlack = decoded.GetPixel(4, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxBlack.Red)).IsEqualTo(0);
    }

    [Test]
    public async Task Wbmp_RoundTrip_AllWhite()
    {
        var frame = CreateSolidFrame(8, 8, 255, 255, 255, 255);
        byte[] encoded = WbmpCoder.Encode(frame);
        var decoded = WbmpCoder.Decode(encoded);

        bool allWhite = true;
        for (int y = 0; y < 8 && allWhite; y++)
        {
            var row = decoded.GetPixelRow(y);
            int ch = decoded.NumberOfChannels;
            for (int x = 0; x < 8; x++)
            {
                if (Quantum.ScaleToByte(row[x * ch]) != 255) { allWhite = false; break; }
            }
        }
        await Assert.That(allWhite).IsTrue();
    }

    [Test]
    public async Task Wbmp_RoundTrip_AllBlack()
    {
        var frame = CreateSolidFrame(8, 8, 0, 0, 0, 255);
        byte[] encoded = WbmpCoder.Encode(frame);
        var decoded = WbmpCoder.Decode(encoded);

        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(0);
    }

    [Test]
    public async Task Wbmp_SmallSize()
    {
        // 1-bit packed, so 8×8 = 8 bytes data + header
        var frame = CreateSolidFrame(8, 8, 255, 255, 255, 255);
        byte[] encoded = WbmpCoder.Encode(frame);

        // Header: type(1) + fix(1) + width(1) + height(1) + 8 bytes data = 12
        await Assert.That(encoded.Length).IsLessThanOrEqualTo(20);
    }

    [Test]
    public async Task Wbmp_NonMultipleOf8Width()
    {
        // Width not multiple of 8, test padding
        var frame = CreateCheckerboardFrame(13, 7, 2);
        byte[] encoded = WbmpCoder.Encode(frame);
        var decoded = WbmpCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(13u);
        await Assert.That(decoded.Rows).IsEqualTo(7u);
    }

    #endregion

    #region PCX Tests

    [Test]
    public async Task Pcx_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(16, 16, 255, 0, 0, 255);
        byte[] encoded = PcxCoder.Encode(frame);
        var decoded = PcxCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var px = decoded.GetPixel(8, 8);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Pcx_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(64, 64);
        byte[] encoded = PcxCoder.Encode(frame);
        var decoded = PcxCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(64u);
        await Assert.That(decoded.Rows).IsEqualTo(64u);

        // Verify pixel at center
        var origPx = frame.GetPixel(32, 32);
        var decPx = decoded.GetPixel(32, 32);
        int origR = (int)Quantum.ScaleToByte((ushort)origPx.Red);
        int decR = (int)Quantum.ScaleToByte((ushort)decPx.Red);
        await Assert.That(Math.Abs(origR - decR)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Pcx_HeaderMagicByte()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = PcxCoder.Encode(frame);

        // PCX magic: 0x0A
        await Assert.That((int)encoded[0]).IsEqualTo(0x0A);
    }

    [Test]
    public async Task Pcx_CanDecode()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = PcxCoder.Encode(frame);

        await Assert.That(PcxCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(PcxCoder.CanDecode(new byte[] { 0xFF, 0x00 })).IsFalse();
    }

    [Test]
    public async Task Pcx_LargeImage()
    {
        var frame = CreateGradientFrame(256, 256);
        byte[] encoded = PcxCoder.Encode(frame);
        var decoded = PcxCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);
    }

    #endregion

    #region XBM Tests

    [Test]
    public async Task Xbm_RoundTrip_Checkerboard()
    {
        var frame = CreateCheckerboardFrame(16, 16, 4);
        byte[] encoded = XbmCoder.Encode(frame);
        var decoded = XbmCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        // White square should stay white, black should stay black
        var pxWhite = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxWhite.Red)).IsEqualTo(255);

        var pxBlack = decoded.GetPixel(4, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxBlack.Red)).IsEqualTo(0);
    }

    [Test]
    public async Task Xbm_RoundTrip_AllWhite()
    {
        var frame = CreateSolidFrame(8, 8, 255, 255, 255, 255);
        byte[] encoded = XbmCoder.Encode(frame);
        var decoded = XbmCoder.Decode(encoded);

        // All white -> all background (0 bits) -> decoded as white
        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
    }

    [Test]
    public async Task Xbm_RoundTrip_AllBlack()
    {
        var frame = CreateSolidFrame(8, 8, 0, 0, 0, 255);
        byte[] encoded = XbmCoder.Encode(frame);
        var decoded = XbmCoder.Decode(encoded);

        // All black -> all foreground (1 bits) -> decoded as black
        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(0);
    }

    [Test]
    public async Task Xbm_TextFormat()
    {
        var frame = CreateCheckerboardFrame(8, 8, 4);
        byte[] encoded = XbmCoder.Encode(frame);
        string text = System.Text.Encoding.ASCII.GetString(encoded);

        await Assert.That(text).Contains("#define image_width 8");
        await Assert.That(text).Contains("#define image_height 8");
        await Assert.That(text).Contains("static unsigned char image_bits[]");
        await Assert.That(text).Contains("0x");
    }

    [Test]
    public async Task Xbm_CanDecode()
    {
        var frame = CreateCheckerboardFrame(4, 4, 2);
        byte[] encoded = XbmCoder.Encode(frame);

        await Assert.That(XbmCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(XbmCoder.CanDecode(new byte[] { 0, 0, 0, 0 })).IsFalse();
    }

    [Test]
    public async Task Xbm_NonMultipleOf8Width()
    {
        var frame = CreateCheckerboardFrame(13, 7, 2);
        byte[] encoded = XbmCoder.Encode(frame);
        var decoded = XbmCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(13u);
        await Assert.That(decoded.Rows).IsEqualTo(7u);
    }

    #endregion

    #region XPM Tests

    [Test]
    public async Task Xpm_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(8, 8, 255, 0, 0, 255);
        byte[] encoded = XpmCoder.Encode(frame);
        var decoded = XpmCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Xpm_RoundTrip_TwoColors()
    {
        var frame = CreateCheckerboardFrame(8, 8, 4);
        byte[] encoded = XpmCoder.Encode(frame);
        var decoded = XpmCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);

        var pxWhite = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxWhite.Red)).IsEqualTo(255);

        var pxBlack = decoded.GetPixel(4, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)pxBlack.Red)).IsEqualTo(0);
    }

    [Test]
    public async Task Xpm_TextFormat()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = XpmCoder.Encode(frame);
        string text = System.Text.Encoding.ASCII.GetString(encoded);

        await Assert.That(text).Contains("/* XPM */");
        await Assert.That(text).Contains("static char");
    }

    [Test]
    public async Task Xpm_CanDecode()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = XpmCoder.Encode(frame);

        await Assert.That(XpmCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(XpmCoder.CanDecode(new byte[] { 0, 0, 0, 0 })).IsFalse();
    }

    [Test]
    public async Task Xpm_MultipleColors()
    {
        // Create a frame with 4 distinct colors in quadrants
        var frame = new ImageFrame();
        frame.Initialize(8, 8, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < 8; x++)
            {
                int offset = x * ch;
                bool isRight = x >= 4;
                bool isBottom = y >= 4;
                if (!isRight && !isBottom) { row[offset] = Quantum.ScaleFromByte(255); row[offset + 1] = 0; row[offset + 2] = 0; }
                else if (isRight && !isBottom) { row[offset] = 0; row[offset + 1] = Quantum.ScaleFromByte(255); row[offset + 2] = 0; }
                else if (!isRight && isBottom) { row[offset] = 0; row[offset + 1] = 0; row[offset + 2] = Quantum.ScaleFromByte(255); }
                else { row[offset] = Quantum.ScaleFromByte(255); row[offset + 1] = Quantum.ScaleFromByte(255); row[offset + 2] = 0; }
            }
        }

        byte[] encoded = XpmCoder.Encode(frame);
        var decoded = XpmCoder.Decode(encoded);

        // Verify top-left is red
        var px = decoded.GetPixel(0, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);

        // Verify top-right is green
        var px2 = decoded.GetPixel(4, 0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px2.Red)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px2.Green)).IsEqualTo(255);
    }

    #endregion

    #region DPX Tests

    [Test]
    public async Task Dpx_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(8, 8, 255, 0, 0, 255);
        byte[] encoded = DpxCoder.Encode(frame);
        var decoded = DpxCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(255);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(0);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Blue)).IsEqualTo(0);
    }

    [Test]
    public async Task Dpx_RoundTrip_Gradient_Lossless()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = DpxCoder.Encode(frame);
        var decoded = DpxCoder.Decode(encoded);

        // 16-bit DPX should be lossless for our 16-bit quantum
        bool dpxMatch = true;
        for (int y = 0; y < 32 && dpxMatch; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                var origPx = frame.GetPixel(x, y);
                var decPx = decoded.GetPixel(x, y);
                if ((ushort)origPx.Red != (ushort)decPx.Red ||
                    (ushort)origPx.Green != (ushort)decPx.Green ||
                    (ushort)origPx.Blue != (ushort)decPx.Blue)
                { dpxMatch = false; break; }
            }
        }
        await Assert.That(dpxMatch).IsTrue();
    }

    [Test]
    public async Task Dpx_CanDecode()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = DpxCoder.Encode(frame);

        await Assert.That(DpxCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(DpxCoder.CanDecode(new byte[] { 0, 0, 0, 0 })).IsFalse();
    }

    [Test]
    public async Task Dpx_Magic_IsSdpx()
    {
        var frame = CreateSolidFrame(4, 4, 0, 0, 0, 255);
        byte[] encoded = DpxCoder.Encode(frame);

        // "SDPX" = 0x53, 0x44, 0x50, 0x58
        await Assert.That((int)encoded[0]).IsEqualTo(0x53);
        await Assert.That((int)encoded[1]).IsEqualTo(0x44);
        await Assert.That((int)encoded[2]).IsEqualTo(0x50);
        await Assert.That((int)encoded[3]).IsEqualTo(0x58);
    }

    [Test]
    public async Task Dpx_FormatRegistry_Detection()
    {
        var frame = CreateSolidFrame(4, 4, 100, 200, 50, 255);
        byte[] encoded = DpxCoder.Encode(frame);

        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Dpx);
    }

    [Test]
    public async Task Dpx_LargeImage()
    {
        var frame = CreateGradientFrame(256, 256);
        byte[] encoded = DpxCoder.Encode(frame);
        var decoded = DpxCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);
    }

    #endregion

    #region FITS Tests

    [Test]
    public async Task Fits_RoundTrip_SolidColor()
    {
        var frame = CreateSolidFrame(8, 8, 128, 128, 128, 255);
        byte[] encoded = FitsCoder.Encode(frame);
        var decoded = FitsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        // FITS uses BZERO transform, so values should be close
        var px = decoded.GetPixel(4, 4);
        int r = (int)Quantum.ScaleToByte((ushort)px.Red);
        await Assert.That(Math.Abs(r - 128)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Fits_RoundTrip_Gradient()
    {
        var frame = CreateGradientFrame(32, 32);
        byte[] encoded = FitsCoder.Encode(frame);
        var decoded = FitsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // 16-bit roundtrip should be exact
        bool fitsMatch = true;
        for (int y = 0; y < 32 && fitsMatch; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                var origPx = frame.GetPixel(x, y);
                var decPx = decoded.GetPixel(x, y);
                if ((ushort)origPx.Red != (ushort)decPx.Red ||
                    (ushort)origPx.Green != (ushort)decPx.Green ||
                    (ushort)origPx.Blue != (ushort)decPx.Blue)
                { fitsMatch = false; break; }
            }
        }
        await Assert.That(fitsMatch).IsTrue();
    }

    [Test]
    public async Task Fits_CanDecode()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = FitsCoder.Encode(frame);

        await Assert.That(FitsCoder.CanDecode(encoded)).IsTrue();
        await Assert.That(FitsCoder.CanDecode(new byte[] { 0, 0, 0, 0 })).IsFalse();
    }

    [Test]
    public async Task Fits_HeaderKeywords()
    {
        var frame = CreateSolidFrame(10, 20, 0, 255, 0, 255);
        byte[] encoded = FitsCoder.Encode(frame);
        string header = System.Text.Encoding.ASCII.GetString(encoded, 0, Math.Min(encoded.Length, 2880));

        await Assert.That(header).Contains("SIMPLE");
        await Assert.That(header).Contains("BITPIX");
        await Assert.That(header).Contains("NAXIS");
        await Assert.That(header).Contains("END");
    }

    [Test]
    public async Task Fits_BlockAlignment()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = FitsCoder.Encode(frame);

        // FITS requires 2880-byte block alignment
        await Assert.That(encoded.Length % 2880).IsEqualTo(0);
    }

    [Test]
    public async Task Fits_FormatRegistry_Detection()
    {
        var frame = CreateSolidFrame(4, 4, 100, 200, 50, 255);
        byte[] encoded = FitsCoder.Encode(frame);

        var format = FormatRegistry.DetectFormat(encoded);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Fits);
    }

    #endregion

    #region FormatRegistry Extended Tests

    [Test]
    public async Task FormatRegistry_DetectFromExtension_AllPhase11()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.ff")).IsEqualTo(ImageFileFormat.Farbfeld);
        await Assert.That(FormatRegistry.DetectFromExtension("test.wbmp")).IsEqualTo(ImageFileFormat.Wbmp);
        await Assert.That(FormatRegistry.DetectFromExtension("test.pcx")).IsEqualTo(ImageFileFormat.Pcx);
        await Assert.That(FormatRegistry.DetectFromExtension("test.xbm")).IsEqualTo(ImageFileFormat.Xbm);
        await Assert.That(FormatRegistry.DetectFromExtension("test.xpm")).IsEqualTo(ImageFileFormat.Xpm);
        await Assert.That(FormatRegistry.DetectFromExtension("test.dpx")).IsEqualTo(ImageFileFormat.Dpx);
        await Assert.That(FormatRegistry.DetectFromExtension("test.fits")).IsEqualTo(ImageFileFormat.Fits);
    }

    [Test]
    public async Task FormatRegistry_RoundTrip_Farbfeld()
    {
        var frame = CreateSolidFrame(8, 8, 200, 100, 50, 255);
        byte[] encoded = FormatRegistry.Encode(frame, ImageFileFormat.Farbfeld);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Farbfeld);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Red)).IsEqualTo(200);
    }

    [Test]
    public async Task FormatRegistry_RoundTrip_Dpx()
    {
        var frame = CreateSolidFrame(8, 8, 0, 255, 128, 255);
        byte[] encoded = FormatRegistry.Encode(frame, ImageFileFormat.Dpx);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Dpx);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        var px = decoded.GetPixel(4, 4);
        await Assert.That((int)Quantum.ScaleToByte((ushort)px.Green)).IsEqualTo(255);
    }

    [Test]
    public async Task FormatRegistry_RoundTrip_Fits()
    {
        var frame = CreateSolidFrame(8, 8, 128, 0, 255, 255);
        byte[] encoded = FormatRegistry.Encode(frame, ImageFileFormat.Fits);
        var decoded = FormatRegistry.Decode(encoded, ImageFileFormat.Fits);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
    }

    #endregion

    #region CIN (Cineon) Tests

    [Test]
    public async Task CinCoder_Roundtrip_SolidRed()
    {
        var original = CreateSolidFrame(16, 16, 255, 0, 0, 255);
        byte[] encoded = CinCoder.Encode(original);
        var decoded = CinCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        var pixel = decoded.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Red)).IsGreaterThanOrEqualTo((byte)240);
    }

    [Test]
    public async Task CinCoder_Roundtrip_Gradient()
    {
        var original = CreateGradientFrame(32, 24);
        byte[] encoded = CinCoder.Encode(original);
        var decoded = CinCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(24u);
    }

    [Test]
    public async Task CinCoder_CanDecode_ValidMagic()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] data = CinCoder.Encode(frame);
        await Assert.That(CinCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task CinCoder_CanDecode_InvalidData()
    {
        byte[] bad = [0x00, 0x00, 0x00, 0x00];
        await Assert.That(CinCoder.CanDecode(bad)).IsFalse();
    }

    [Test]
    public async Task CinCoder_MagicBytes()
    {
        var frame = CreateSolidFrame(8, 8, 0, 255, 0, 255);
        byte[] data = CinCoder.Encode(frame);
        // Cineon magic: 0x80 0x2A 0x5F 0xD7
        await Assert.That(data[0]).IsEqualTo((byte)0x80);
        await Assert.That(data[1]).IsEqualTo((byte)0x2A);
        await Assert.That(data[2]).IsEqualTo((byte)0x5F);
        await Assert.That(data[3]).IsEqualTo((byte)0xD7);
    }

    [Test]
    public async Task CinCoder_Registry_Detection()
    {
        var frame = CreateSolidFrame(8, 8, 100, 200, 50, 255);
        byte[] data = CinCoder.Encode(frame);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Cin);
    }

    #endregion

    #region RAW Channel Tests

    [Test]
    public async Task RawCoder_Rgb8_Roundtrip()
    {
        var original = CreateSolidFrame(8, 8, 100, 150, 200, 255);
        byte[] encoded = RawCoder.Encode(original, RawChannelLayout.Rgb8);
        var decoded = RawCoder.Decode(encoded, 8, 8, RawChannelLayout.Rgb8);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        var pixel = decoded.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Red)).IsEqualTo((byte)100);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Green)).IsEqualTo((byte)150);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Blue)).IsEqualTo((byte)200);
    }

    [Test]
    public async Task RawCoder_Rgba8_Roundtrip()
    {
        var original = CreateSolidFrame(8, 8, 255, 128, 0, 200);
        byte[] encoded = RawCoder.Encode(original, RawChannelLayout.Rgba8);
        var decoded = RawCoder.Decode(encoded, 8, 8, RawChannelLayout.Rgba8);

        var pixel = decoded.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Red)).IsEqualTo((byte)255);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Alpha)).IsEqualTo((byte)200);
    }

    [Test]
    public async Task RawCoder_Bgr8_Roundtrip()
    {
        var original = CreateSolidFrame(4, 4, 10, 20, 30, 255);
        byte[] encoded = RawCoder.Encode(original, RawChannelLayout.Bgr8);
        var decoded = RawCoder.Decode(encoded, 4, 4, RawChannelLayout.Bgr8);

        var pixel = decoded.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Red)).IsEqualTo((byte)10);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Green)).IsEqualTo((byte)20);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Blue)).IsEqualTo((byte)30);
    }

    [Test]
    public async Task RawCoder_Gray8_Roundtrip()
    {
        var frame = new ImageFrame();
        frame.Initialize(8, 8, ColorspaceType.SRGB, false);
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 8; x++)
            {
                byte val = (byte)(y * 32);
                int off = x * ch;
                row[off] = Quantum.ScaleFromByte(val);
                if (ch > 1) row[off + 1] = Quantum.ScaleFromByte(val);
                if (ch > 2) row[off + 2] = Quantum.ScaleFromByte(val);
            }
        }

        byte[] encoded = RawCoder.Encode(frame, RawChannelLayout.Gray8);
        var decoded = RawCoder.Decode(encoded, 8, 8, RawChannelLayout.Gray8);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
    }

    [Test]
    public async Task RawCoder_Rgb16_Roundtrip()
    {
        var original = CreateSolidFrame(4, 4, 200, 100, 50, 255);
        byte[] encoded = RawCoder.Encode(original, RawChannelLayout.Rgb16);
        var decoded = RawCoder.Decode(encoded, 4, 4, RawChannelLayout.Rgb16);

        await Assert.That(decoded.Columns).IsEqualTo(4u);
        await Assert.That(decoded.Rows).IsEqualTo(4u);
    }

    [Test]
    public async Task RawCoder_Mono_Roundtrip()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 8, ColorspaceType.SRGB, false);
        for (int y = 0; y < 8; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                byte val = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                int off = x * ch;
                row[off] = Quantum.ScaleFromByte(val);
                if (ch > 1) row[off + 1] = Quantum.ScaleFromByte(val);
                if (ch > 2) row[off + 2] = Quantum.ScaleFromByte(val);
            }
        }

        byte[] encoded = RawCoder.Encode(frame, RawChannelLayout.Mono);
        var decoded = RawCoder.Decode(encoded, 16, 8, RawChannelLayout.Mono);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
    }

    [Test]
    public async Task RawCoder_DataSize_Rgb8()
    {
        var frame = CreateSolidFrame(10, 5, 0, 0, 0, 255);
        byte[] encoded = RawCoder.Encode(frame, RawChannelLayout.Rgb8);
        // RGB8: 3 bytes per pixel, 10*5=50 pixels
        await Assert.That(encoded.Length).IsEqualTo(150);
    }

    #endregion

    #region DICOM Tests

    [Test]
    public async Task DicomCoder_Roundtrip_Grayscale()
    {
        var frame = new ImageFrame();
        frame.Initialize(16, 16, ColorspaceType.SRGB, false);
        for (int y = 0; y < 16; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                byte val = (byte)(y * 16);
                int off = x * ch;
                row[off] = Quantum.ScaleFromByte(val);
                if (ch > 1) row[off + 1] = Quantum.ScaleFromByte(val);
                if (ch > 2) row[off + 2] = Quantum.ScaleFromByte(val);
            }
        }

        byte[] encoded = DicomCoder.Encode(frame);
        var decoded = DicomCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
    }

    [Test]
    public async Task DicomCoder_CanDecode_ValidMagic()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] data = DicomCoder.Encode(frame);
        await Assert.That(DicomCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task DicomCoder_CanDecode_InvalidData()
    {
        byte[] bad = new byte[140];
        await Assert.That(DicomCoder.CanDecode(bad)).IsFalse();
    }

    [Test]
    public async Task DicomCoder_MagicAtOffset128()
    {
        var frame = CreateSolidFrame(8, 8, 64, 64, 64, 255);
        byte[] data = DicomCoder.Encode(frame);
        // "DICM" at offset 128
        await Assert.That(data[128]).IsEqualTo((byte)'D');
        await Assert.That(data[129]).IsEqualTo((byte)'I');
        await Assert.That(data[130]).IsEqualTo((byte)'C');
        await Assert.That(data[131]).IsEqualTo((byte)'M');
    }

    [Test]
    public async Task DicomCoder_Registry_Detection()
    {
        var frame = CreateSolidFrame(8, 8, 100, 100, 100, 255);
        byte[] data = DicomCoder.Encode(frame);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Dicom);
    }

    [Test]
    public async Task DicomCoder_Roundtrip_Solid()
    {
        var original = CreateSolidFrame(8, 8, 200, 200, 200, 255);
        byte[] encoded = DicomCoder.Encode(original);
        var decoded = DicomCoder.Decode(encoded);

        // DICOM encodes as grayscale, so R≈G≈B after decode
        var pixel = decoded.GetPixel(0, 0);
        byte lum = Quantum.ScaleToByte((ushort)pixel.Red);
        await Assert.That(lum).IsGreaterThanOrEqualTo((byte)180);
    }

    #endregion

    #region JPEG 2000 Tests

    [Test]
    public async Task Jpeg2000Coder_Roundtrip_Solid()
    {
        var original = CreateSolidFrame(16, 16, 128, 64, 200, 255);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        var decoded = Jpeg2000Coder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
    }

    [Test]
    public async Task Jpeg2000Coder_Roundtrip_Gradient()
    {
        var original = CreateGradientFrame(32, 24);
        byte[] encoded = Jpeg2000Coder.Encode(original);
        var decoded = Jpeg2000Coder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(24u);
    }

    [Test]
    public async Task Jpeg2000Coder_CanDecode_JP2Container()
    {
        var frame = CreateSolidFrame(4, 4, 100, 100, 100, 255);
        byte[] data = Jpeg2000Coder.Encode(frame);
        await Assert.That(Jpeg2000Coder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task Jpeg2000Coder_CanDecode_InvalidData()
    {
        byte[] bad = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        await Assert.That(Jpeg2000Coder.CanDecode(bad)).IsFalse();
    }

    [Test]
    public async Task Jpeg2000Coder_Registry_Detection()
    {
        var frame = CreateSolidFrame(8, 8, 50, 100, 150, 255);
        byte[] data = Jpeg2000Coder.Encode(frame);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Jpeg2000);
    }

    [Test]
    public async Task Jpeg2000Coder_Extension_Detection()
    {
        var format = FormatRegistry.DetectFromExtension("test.jp2");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Jpeg2000);
        format = FormatRegistry.DetectFromExtension("test.j2k");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Jpeg2000);
    }

    #endregion

    #region JPEG XL Tests

    [Test]
    public async Task JxlCoder_Roundtrip_Solid()
    {
        var original = CreateSolidFrame(16, 16, 200, 100, 50, 255);
        byte[] encoded = JxlCoder.Encode(original);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
    }

    [Test]
    public async Task JxlCoder_Roundtrip_Gradient()
    {
        var original = CreateGradientFrame(32, 24);
        byte[] encoded = JxlCoder.Encode(original);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(24u);
    }

    [Test]
    public async Task JxlCoder_CanDecode_Valid()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] data = JxlCoder.Encode(frame);
        await Assert.That(JxlCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task JxlCoder_CanDecode_InvalidData()
    {
        byte[] bad = [0x00, 0x00, 0x00, 0x00];
        await Assert.That(JxlCoder.CanDecode(bad)).IsFalse();
    }

    [Test]
    public async Task JxlCoder_Registry_Detection()
    {
        var frame = CreateSolidFrame(8, 8, 50, 100, 150, 255);
        byte[] data = JxlCoder.Encode(frame);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.JpegXl);
    }

    [Test]
    public async Task JxlCoder_Extension_Detection()
    {
        var format = FormatRegistry.DetectFromExtension("test.jxl");
        await Assert.That(format).IsEqualTo(ImageFileFormat.JpegXl);
    }

    #endregion

    #region JPEG XL VarDCT Lossy Tests

    [Test]
    public async Task JxlVarDct_Roundtrip_SolidColor()
    {
        var original = CreateSolidFrame(16, 16, 200, 100, 50, 255);
        byte[] encoded = JxlCoder.EncodeLossy(original, 95);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);

        // Solid color should survive high-quality lossy within XYB transform precision
        var row = decoded.GetPixelRow(8);
        int channels = decoded.NumberOfChannels;
        int rVal = row[channels];
        int gVal = row[channels + 1];
        int bVal = row[channels + 2];
        int expectedR = Quantum.ScaleFromByte(200);
        int expectedG = Quantum.ScaleFromByte(100);
        int expectedB = Quantum.ScaleFromByte(50);
        // Allow ~5% error for lossy XYB + DCT quantization
        int tolerance = Quantum.MaxValue / 20;
        await Assert.That(Math.Abs(rVal - expectedR)).IsLessThanOrEqualTo(tolerance);
        await Assert.That(Math.Abs(gVal - expectedG)).IsLessThanOrEqualTo(tolerance);
        await Assert.That(Math.Abs(bVal - expectedB)).IsLessThanOrEqualTo(tolerance);
    }

    [Test]
    public async Task JxlVarDct_Roundtrip_Gradient()
    {
        var original = CreateGradientFrame(32, 24);
        byte[] encoded = JxlCoder.EncodeLossy(original, 90);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(24u);
    }

    [Test]
    public async Task JxlVarDct_Roundtrip_WithAlpha()
    {
        var original = CreateSolidFrame(16, 16, 180, 90, 45, 128);
        byte[] encoded = JxlCoder.EncodeLossy(original, 95);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.HasAlpha).IsTrue();

        // Alpha is encoded losslessly via squeeze
        var row = decoded.GetPixelRow(8);
        int channels = decoded.NumberOfChannels;
        ushort alphaVal = row[channels + 3];
        ushort expectedAlpha = Quantum.ScaleFromByte(128);
        await Assert.That(alphaVal).IsEqualTo(expectedAlpha);
    }

    [Test]
    public async Task JxlVarDct_HigherQuality_HigherPsnr()
    {
        var original = CreateGradientFrame(32, 32);
        byte[] lowQ = JxlCoder.EncodeLossy(original, 50);
        byte[] highQ = JxlCoder.EncodeLossy(original, 95);

        var decodedLow = JxlCoder.Decode(lowQ);
        var decodedHigh = JxlCoder.Decode(highQ);

        double psnrLow = ComputePsnr(original, decodedLow);
        double psnrHigh = ComputePsnr(original, decodedHigh);

        // Higher quality should yield higher PSNR
        await Assert.That(psnrHigh).IsGreaterThan(psnrLow);
    }

    [Test]
    public async Task JxlVarDct_LowerQuality_SmallerFile()
    {
        var original = CreateGradientFrame(32, 32);
        byte[] lowQ = JxlCoder.EncodeLossy(original, 25);
        byte[] highQ = JxlCoder.EncodeLossy(original, 95);

        // Lower quality should produce smaller files
        await Assert.That(lowQ.Length).IsLessThan(highQ.Length);
    }

    [Test]
    public async Task JxlVarDct_Quality95_HighPsnr()
    {
        var original = CreateGradientFrame(32, 32);
        byte[] encoded = JxlCoder.EncodeLossy(original, 95);
        var decoded = JxlCoder.Decode(encoded);

        double psnr = ComputePsnr(original, decoded);

        // Q95 should produce PSNR > 30 dB
        await Assert.That(psnr).IsGreaterThan(30.0);
    }

    [Test]
    public async Task JxlVarDct_LargeImage()
    {
        var original = CreateGradientFrame(100, 80);
        byte[] encoded = JxlCoder.EncodeLossy(original, 75);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(100u);
        await Assert.That(decoded.Rows).IsEqualTo(80u);
    }

    [Test]
    public async Task JxlVarDct_NonBlockAligned_Dimensions()
    {
        // 13x11 is not divisible by 8 — tests edge block padding
        var original = CreateGradientFrame(13, 11);
        byte[] encoded = JxlCoder.EncodeLossy(original, 90);
        var decoded = JxlCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(13u);
        await Assert.That(decoded.Rows).IsEqualTo(11u);
    }

    [Test]
    public async Task JxlVarDct_CanDecode_LossyFile()
    {
        var original = CreateSolidFrame(8, 8, 128, 128, 128, 255);
        byte[] data = JxlCoder.EncodeLossy(original, 80);
        await Assert.That(JxlCoder.CanDecode(data)).IsTrue();
    }

    private static double ComputePsnr(ImageFrame a, ImageFrame b)
    {
        int w = (int)a.Columns;
        int h = (int)a.Rows;
        int channels = Math.Min(a.NumberOfChannels, b.NumberOfChannels);
        double mse = 0;
        int count = 0;
        for (int y = 0; y < h; y++)
        {
            var rowA = a.GetPixelRow(y);
            var rowB = b.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < Math.Min(channels, 3); c++)
                {
                    double diff = rowA[x * a.NumberOfChannels + c] - rowB[x * b.NumberOfChannels + c];
                    mse += diff * diff;
                    count++;
                }
            }
        }
        mse /= count;
        if (mse == 0) return 100;
        return 10 * Math.Log10((double)Quantum.MaxValue * Quantum.MaxValue / mse);
    }

    #endregion

    #region AVIF/HEIC Tests

    [Test]
    public async Task AvifCoder_Roundtrip_Solid()
    {
        var original = CreateSolidFrame(16, 16, 200, 100, 50, 255);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Avif);
        var decoded = HeifCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
    }

    [Test]
    public async Task AvifCoder_Roundtrip_Gradient()
    {
        var original = CreateGradientFrame(32, 24);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Avif);
        var decoded = HeifCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(24u);
    }

    [Test]
    public async Task AvifCoder_CanDecode_Valid()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Avif);
        await Assert.That(HeifCoder.CanDecode(data)).IsTrue();
    }

    [Test]
    public async Task AvifCoder_CanDecode_Invalid()
    {
        byte[] bad = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        await Assert.That(HeifCoder.CanDecode(bad)).IsFalse();
    }

    [Test]
    public async Task AvifCoder_FtypBrand()
    {
        var frame = CreateSolidFrame(8, 8, 100, 100, 100, 255);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Avif);
        // ftyp box: size(4) + "ftyp"(4) + "avif"(4)
        string ftyp = System.Text.Encoding.ASCII.GetString(data, 4, 4);
        string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
        await Assert.That(ftyp).IsEqualTo("ftyp");
        await Assert.That(brand).IsEqualTo("avif");
    }

    [Test]
    public async Task HeicCoder_Roundtrip_Solid()
    {
        var original = CreateSolidFrame(16, 16, 50, 150, 250, 255);
        byte[] encoded = HeifCoder.Encode(original, HeifContainerType.Heic);
        var decoded = HeifCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(16u);
        await Assert.That(decoded.Rows).IsEqualTo(16u);
    }

    [Test]
    public async Task HeicCoder_FtypBrand()
    {
        var frame = CreateSolidFrame(8, 8, 100, 100, 100, 255);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Heic);
        string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
        await Assert.That(brand).IsEqualTo("heic");
    }

    [Test]
    public async Task AvifCoder_Registry_FormatDetection()
    {
        var frame = CreateSolidFrame(8, 8, 100, 100, 100, 255);
        byte[] data = HeifCoder.Encode(frame, HeifContainerType.Avif);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Avif);
    }

    [Test]
    public async Task AvifCoder_Extension_Detection()
    {
        await Assert.That(FormatRegistry.DetectFromExtension("test.avif")).IsEqualTo(ImageFileFormat.Avif);
        await Assert.That(FormatRegistry.DetectFromExtension("test.heic")).IsEqualTo(ImageFileFormat.Heic);
        await Assert.That(FormatRegistry.DetectFromExtension("test.heif")).IsEqualTo(ImageFileFormat.Heic);
    }

    #endregion

    #region DDS BC7 Tests

    [Test]
    public async Task DdsBc7_RoundTrip_SolidRed()
    {
        var frame = CreateSolidFrame(8, 8, 200, 0, 0, 255);
        byte[] encoded = DdsCoder.EncodeBc7(frame);
        var decoded = DdsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(8u);
        await Assert.That(decoded.Rows).IsEqualTo(8u);

        var row = decoded.GetPixelRow(0);
        int ch = decoded.NumberOfChannels;
        ushort r = row[0], g = row[1], b = row[2];
        // BC7 mode 6 with 7+1 bit endpoints — allow small quantization error
        await Assert.That(Math.Abs(r - Quantum.ScaleFromByte(200))).IsLessThanOrEqualTo((ushort)514);
        await Assert.That(g).IsLessThanOrEqualTo((ushort)514);
        await Assert.That(b).IsLessThanOrEqualTo((ushort)514);
    }

    [Test]
    public async Task DdsBc7_RoundTrip_SolidGreen()
    {
        var frame = CreateSolidFrame(8, 8, 0, 180, 0, 255);
        byte[] encoded = DdsCoder.EncodeBc7(frame);
        var decoded = DdsCoder.Decode(encoded);

        var row = decoded.GetPixelRow(4);
        int ch = decoded.NumberOfChannels;
        ushort g = row[1];
        await Assert.That(Math.Abs(g - Quantum.ScaleFromByte(180))).IsLessThanOrEqualTo((ushort)514);
    }

    [Test]
    public async Task DdsBc7_RoundTrip_WithAlpha()
    {
        var frame = CreateSolidFrame(4, 4, 100, 150, 200, 128);
        byte[] encoded = DdsCoder.EncodeBc7(frame);
        var decoded = DdsCoder.Decode(encoded);

        var row = decoded.GetPixelRow(0);
        int ch = decoded.NumberOfChannels;
        ushort a = row[ch - 1];
        await Assert.That(Math.Abs(a - Quantum.ScaleFromByte(128))).IsLessThanOrEqualTo((ushort)514);
    }

    [Test]
    public async Task DdsBc7_RoundTrip_NonMultipleOf4()
    {
        // BC7 blocks are 4×4; test with non-aligned dimensions
        var frame = CreateSolidFrame(5, 7, 100, 100, 100, 255);
        byte[] encoded = DdsCoder.EncodeBc7(frame);
        var decoded = DdsCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(5u);
        await Assert.That(decoded.Rows).IsEqualTo(7u);

        var row = decoded.GetPixelRow(3);
        int ch = decoded.NumberOfChannels;
        ushort r = row[ch * 2]; // pixel 2
        await Assert.That(Math.Abs(r - Quantum.ScaleFromByte(100))).IsLessThanOrEqualTo((ushort)514);
    }

    [Test]
    public async Task DdsBc7_RoundTrip_Gradient()
    {
        // Create a gradient to test endpoint interpolation quality
        var frame = new ImageFrame();
        frame.Initialize(16, 4, ColorspaceType.SRGB, true);
        for (int y = 0; y < 4; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 16; x++)
            {
                byte val = (byte)(x * 17); // 0, 17, 34, ... 255
                int di = x * ch;
                row[di] = Quantum.ScaleFromByte(val);
                row[di + 1] = Quantum.ScaleFromByte(val);
                row[di + 2] = Quantum.ScaleFromByte(val);
                row[di + ch - 1] = Quantum.ScaleFromByte(255);
            }
        }

        byte[] encoded = DdsCoder.EncodeBc7(frame);
        var decoded = DdsCoder.Decode(encoded);

        // Verify gradient is approximately preserved (within BC7 quantization tolerance)
        var srcRow = frame.GetPixelRow(0);
        var dstRow = decoded.GetPixelRow(0);
        int channels = decoded.NumberOfChannels;
        double totalError = 0;
        for (int x = 0; x < 16; x++)
        {
            int diff = Math.Abs(Quantum.ScaleToByte(srcRow[x * channels]) - Quantum.ScaleToByte(dstRow[x * channels]));
            totalError += diff;
        }
        double avgError = totalError / 16;
        await Assert.That(avgError).IsLessThan(4.0); // BC7 mode 6 should be very accurate
    }

    [Test]
    public async Task DdsBc7_Encode_HasDx10Header()
    {
        var frame = CreateSolidFrame(4, 4, 128, 128, 128, 255);
        byte[] encoded = DdsCoder.EncodeBc7(frame);

        // Verify DDS magic
        uint magic = BitConverter.ToUInt32(encoded, 0);
        await Assert.That(magic).IsEqualTo(0x20534444u);

        // Verify FourCC = DX10 at offset 84 (pixel format FourCC)
        uint fourcc = BitConverter.ToUInt32(encoded, 84);
        await Assert.That(fourcc).IsEqualTo(0x30315844u); // "DX10"

        // Verify DXGI format = BC7_UNORM_SRGB (99) at offset 128 (DX10 header)
        uint dxgiFormat = BitConverter.ToUInt32(encoded, 128);
        await Assert.That(dxgiFormat).IsEqualTo(99u);
    }

    [Test]
    public async Task DdsBc7_RoseImage_RoundTrip()
    {
        string rosePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "photo_small.png");
        if (!File.Exists(rosePath)) return;

        byte[] pngData = await File.ReadAllBytesAsync(rosePath);
        var original = FormatRegistry.Read(pngData);

        byte[] bc7Data = DdsCoder.EncodeBc7(original);
        var decoded = DdsCoder.Decode(bc7Data);

        await Assert.That(decoded.Columns).IsEqualTo(original.Columns);
        await Assert.That(decoded.Rows).IsEqualTo(original.Rows);

        // Check first pixel for sanity
        var oRow = original.GetPixelRow(0);
        var dRow = decoded.GetPixelRow(0);
        int oCh = original.NumberOfChannels;
        int dCh = decoded.NumberOfChannels;
        Console.WriteLine($"Original: {original.Columns}x{original.Rows} ch={oCh} alpha={original.HasAlpha}");
        Console.WriteLine($"Decoded:  {decoded.Columns}x{decoded.Rows} ch={dCh} alpha={decoded.HasAlpha}");
        Console.WriteLine($"Pixel[0,0] orig=({Quantum.ScaleToByte(oRow[0])},{Quantum.ScaleToByte(oRow[1])},{Quantum.ScaleToByte(oRow[2])}) dec=({Quantum.ScaleToByte(dRow[0])},{Quantum.ScaleToByte(dRow[1])},{Quantum.ScaleToByte(dRow[2])})");

        // PSNR should be reasonable for BC7 mode 6
        double psnr = ComputePsnr(original, decoded);
        Console.WriteLine($"BC7 PSNR = {psnr:F2} dB");
        await Assert.That(psnr).IsGreaterThan(25.0);
    }

    #endregion

    #region TIFF Improvement Tests

    [Test]
    public async Task Tiff_TiledWrite_RoundTrip()
    {
        var frame = CreateSolidFrame(100, 80, 200, 100, 50, 255);
        using var ms = new MemoryStream();
        TiffCoder.WriteTiled(frame, ms, TiffCompression.Deflate, 32);
        ms.Position = 0;
        var decoded = TiffCoder.Read(ms);

        await Assert.That(decoded.Columns).IsEqualTo(100u);
        await Assert.That(decoded.Rows).IsEqualTo(80u);

        var row = decoded.GetPixelRow(40);
        int ch = decoded.NumberOfChannels;
        ushort r = row[50 * ch];
        await Assert.That(r).IsEqualTo(Quantum.ScaleFromByte(200));
    }

    [Test]
    public async Task Tiff_TiledWrite_NonAlignedDimensions()
    {
        var frame = CreateSolidFrame(37, 23, 128, 64, 192, 255);
        using var ms = new MemoryStream();
        TiffCoder.WriteTiled(frame, ms, TiffCompression.Lzw, 16);
        ms.Position = 0;
        var decoded = TiffCoder.Read(ms);

        await Assert.That(decoded.Columns).IsEqualTo(37u);
        await Assert.That(decoded.Rows).IsEqualTo(23u);

        var row = decoded.GetPixelRow(22);
        int ch = decoded.NumberOfChannels;
        ushort g = row[36 * ch + 1];
        await Assert.That(g).IsEqualTo(Quantum.ScaleFromByte(64));
    }

    [Test]
    public async Task Tiff_MultiPage_RoundTrip()
    {
        var page1 = CreateSolidFrame(20, 20, 255, 0, 0, 255);
        var page2 = CreateSolidFrame(30, 25, 0, 255, 0, 255);
        var page3 = CreateSolidFrame(15, 10, 0, 0, 255, 255);

        using var ms = new MemoryStream();
        TiffCoder.WriteMultiPage([page1, page2, page3], ms);
        ms.Position = 0;
        var pages = TiffCoder.ReadMultiPage(ms);

        await Assert.That(pages.Count).IsEqualTo(3);
        await Assert.That(pages[0].Columns).IsEqualTo(20u);
        await Assert.That(pages[1].Columns).IsEqualTo(30u);
        await Assert.That(pages[2].Columns).IsEqualTo(15u);

        ushort r1 = pages[0].GetPixelRow(0)[0];
        await Assert.That(r1).IsEqualTo(Quantum.ScaleFromByte(255));

        ushort g2 = pages[1].GetPixelRow(0)[1];
        await Assert.That(g2).IsEqualTo(Quantum.ScaleFromByte(255));

        ushort b3 = pages[2].GetPixelRow(0)[2];
        await Assert.That(b3).IsEqualTo(Quantum.ScaleFromByte(255));
    }

    [Test]
    public async Task Tiff_MultiPage_ReadFirst_IsPage1()
    {
        var page1 = CreateSolidFrame(20, 20, 200, 0, 0, 255);
        var page2 = CreateSolidFrame(30, 25, 0, 200, 0, 255);

        using var ms = new MemoryStream();
        TiffCoder.WriteMultiPage([page1, page2], ms);
        ms.Position = 0;

        var first = TiffCoder.Read(ms);
        await Assert.That(first.Columns).IsEqualTo(20u);
        await Assert.That(first.Rows).IsEqualTo(20u);
    }

    [Test]
    public async Task Tiff_Tiled_Gradient_Lossless()
    {
        var frame = new ImageFrame();
        frame.Initialize(64, 64, ColorspaceType.SRGB, false);
        for (int y = 0; y < 64; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0; x < 64; x++)
            {
                row[x * ch] = Quantum.ScaleFromByte((byte)(x * 4));
                row[x * ch + 1] = Quantum.ScaleFromByte((byte)(y * 4));
                row[x * ch + 2] = Quantum.ScaleFromByte(128);
            }
        }

        using var ms = new MemoryStream();
        TiffCoder.WriteTiled(frame, ms, TiffCompression.None, 16);
        ms.Position = 0;
        var decoded = TiffCoder.Read(ms);

        // Verify exact lossless match (read values into locals before await)
        int channels = frame.NumberOfChannels;
        bool allMatch = true;
        for (int x = 0; x < 64; x++)
        {
            ushort srcR = frame.GetPixelRow(32)[x * channels];
            ushort dstR = decoded.GetPixelRow(32)[x * channels];
            if (srcR != dstR) { allMatch = false; break; }
        }
        await Assert.That(allMatch).IsTrue();
    }

    [Test]
    public async Task Tiff_BigTiff_CanDetect()
    {
        byte[] bigTiffHeader = [
            0x49, 0x49, 43, 0, 8, 0, 0, 0,
            16, 0, 0, 0, 0, 0, 0, 0,
        ];
        bool canDecode = TiffCoder.CanDecode(bigTiffHeader);
        await Assert.That(canDecode).IsTrue();
    }

    #endregion
}
