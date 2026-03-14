using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for Phase 25 Format Enhancements:
/// - TIFF CCITT G3/G4 fax compression decode
/// - PSD layer reading
/// </summary>
public class FormatEnhancementTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_fmt25_{name}");

    // ═══════════════════════════════════════════════════════════════
    // CCITT FAX DECODER UNIT TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task CcittDecoder_ModifiedHuffman_AllWhiteLine()
    {
        // A single white line of width 8: white run of 8 pixels
        // White terminating code for 8 = 10011 (5 bits)
        // Pad to byte boundary
        byte[] encoded = EncodeWhiteRun(8);
        var result = CcittFaxDecoder.DecodeModifiedHuffman(encoded, 8, 1);

        await Assert.That(result.Length).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task CcittDecoder_ModifiedHuffman_AllBlackLine()
    {
        // White run of 0 + Black run of 8
        // White terminating code for 0 = 00110101 (8 bits)
        // Black terminating code for 8 = 000101 (6 bits)
        // Total = 14 bits, pad to 2 bytes
        var bits = new BitWriter();
        bits.WriteBits(0b00110101, 8); // white 0
        bits.WriteBits(0b000101, 6);   // black 8
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeModifiedHuffman(bits.ToArray(), 8, 1);

        await Assert.That(result.Length).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task CcittDecoder_ModifiedHuffman_AlternatingBlocks()
    {
        // 4 white + 4 black on 8-pixel line
        var bits = new BitWriter();
        bits.WriteBits(0b1011, 4);  // white 4
        bits.WriteBits(0b011, 3);   // black 4
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeModifiedHuffman(bits.ToArray(), 8, 1);

        await Assert.That(result.Length).IsEqualTo(8);
        // First 4 should be white (0)
        for (int i = 0; i < 4; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
        // Last 4 should be black (1)
        for (int i = 4; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task CcittDecoder_ModifiedHuffman_MultipleRows()
    {
        // 2 rows, each 8 pixels: first row all white, second row all black
        var bits = new BitWriter();
        // Row 1: white 8
        bits.WriteBits(0b10011, 5); // white 8
        bits.PadToByte();
        // Row 2: white 0 + black 8
        bits.WriteBits(0b00110101, 8); // white 0
        bits.WriteBits(0b000101, 6);   // black 8
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeModifiedHuffman(bits.ToArray(), 8, 2);

        await Assert.That(result.Length).IsEqualTo(16);
        // Row 1 all white
        for (int i = 0; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
        // Row 2 all black
        for (int i = 8; i < 16; i++)
            await Assert.That(result[i]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task CcittDecoder_ModifiedHuffman_LongWhiteRun()
    {
        // 128 white pixels using makeup code 128 + terminating 0
        // White makeup 128 = 10010 (5 bits)
        // White terminating 0 = 00110101 (8 bits)
        var bits = new BitWriter();
        bits.WriteBits(0b10010, 5);    // white makeup 128
        bits.WriteBits(0b00110101, 8); // white terminating 0
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeModifiedHuffman(bits.ToArray(), 128, 1);

        await Assert.That(result.Length).IsEqualTo(128);
        for (int i = 0; i < 128; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task CcittDecoder_Group4_AllWhiteLine()
    {
        // G4: V(0) means change at same position as reference (all white ref → all white current)
        // For an all-white line with all-white reference, we need horizontal mode
        // with white run = width, or simply no changes at all.
        // Actually for G4, if reference is all-white and current is all-white,
        // there are no changing elements, so we just need to pass through.
        // The encoder would use horizontal mode: H + white(width) + black(0)
        var bits = new BitWriter();
        // Horizontal mode: 001
        bits.WriteBits(0b001, 3);
        // White run 8: 10011 (5 bits)
        bits.WriteBits(0b10011, 5);
        // Black run 0: 0000110111 (10 bits)
        bits.WriteBits(0b0000110111, 10);
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeGroup4(bits.ToArray(), 8, 1);

        await Assert.That(result.Length).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task CcittDecoder_Group4_VerticalMode()
    {
        // Reference line: all white
        // Current line: 4 white, 4 black
        // b1 (first changing element on ref that differs from a0 color) would be at position 8 (end)
        // Using horizontal mode for simplicity: H + white(4) + black(4)
        var bits = new BitWriter();
        bits.WriteBits(0b001, 3);   // Horizontal mode
        bits.WriteBits(0b1011, 4);  // white 4
        bits.WriteBits(0b011, 3);   // black 4
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeGroup4(bits.ToArray(), 8, 1);

        await Assert.That(result.Length).IsEqualTo(8);
        for (int i = 0; i < 4; i++)
            await Assert.That(result[i]).IsEqualTo((byte)0);
        for (int i = 4; i < 8; i++)
            await Assert.That(result[i]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task CcittDecoder_Group4_MultiRow()
    {
        // Row 1: 4 white + 4 black (ref = all white)
        // Row 2: same pattern (ref = row 1)
        var bits = new BitWriter();
        // Row 1: H mode
        bits.WriteBits(0b001, 3);   // Horizontal mode
        bits.WriteBits(0b1011, 4);  // white 4
        bits.WriteBits(0b011, 3);   // black 4

        // Row 2: reference has change at pos 4 (w→b)
        // Current also changes at pos 4 → V(0) for first change
        bits.WriteBits(0b1, 1);     // V(0) — change at same pos as ref (pos 4)
        // After this, a0=4, color flipped to black, b1 on ref changes back at pos 8
        // Current also changes at pos 8 → V(0)
        bits.WriteBits(0b1, 1);     // V(0) — change at same pos as ref (pos 8)
        bits.PadToByte();

        var result = CcittFaxDecoder.DecodeGroup4(bits.ToArray(), 8, 2);

        await Assert.That(result.Length).IsEqualTo(16);
        // Both rows: 4 white + 4 black
        for (int row = 0; row < 2; row++)
        {
            int offset = row * 8;
            for (int i = 0; i < 4; i++)
                await Assert.That(result[offset + i]).IsEqualTo((byte)0);
            for (int i = 4; i < 8; i++)
                await Assert.That(result[offset + i]).IsEqualTo((byte)1);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TIFF CCITT INTEGRATION TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Tiff_CcittGroup4_RoundTrip_ViaRawTiff()
    {
        // Create a TIFF with CCITT Group 4 compression manually,
        // then read it with TiffCoder
        int width = 16;
        int height = 4;

        // Create bilevel pattern: checkerboard
        var pattern = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pattern[y * width + x] = (byte)(((x + y) % 2 == 0) ? 0 : 1);

        // Encode with G4
        byte[] g4Data = EncodeWithGroup4Horizontal(pattern, width, height);

        // Build a minimal TIFF file with G4 compression
        byte[] tiffData = BuildCcittTiff(g4Data, width, height, compression: 4);

        string tempFile = TempPath("ccitt_g4.tiff");
        try
        {
            File.WriteAllBytes(tempFile, tiffData);
            var image = TiffCoder.Read(tempFile);

            await Assert.That(image.Columns).IsEqualTo(width);
            await Assert.That(image.Rows).IsEqualTo(height);

            // Verify checkerboard pattern
            for (int y = 0; y < height; y++)
            {
                var row = image.GetPixelRow(y).ToArray();
                for (int x = 0; x < width; x++)
                {
                    bool shouldBeBlack = (x + y) % 2 != 0;
                    // MinIsWhite: decoder output 0 (white) → invertGray → MaxValue
                    //             decoder output 1 (black) → invertGray → 0
                    ushort expected = shouldBeBlack ? (ushort)0 : Quantum.MaxValue;
                    await Assert.That(row[x * 3]).IsEqualTo(expected);
                }
            }

            image.Dispose();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_CcittModifiedHuffman_AllWhite()
    {
        int width = 32;
        int height = 2;

        // All white
        var pattern = new byte[width * height]; // zeros = white

        // Encode each row with Modified Huffman
        var mhData = EncodeModifiedHuffmanRows(pattern, width, height);

        byte[] tiffData = BuildCcittTiff(mhData, width, height, compression: 2);

        string tempFile = TempPath("ccitt_mh_white.tiff");
        try
        {
            File.WriteAllBytes(tempFile, tiffData);
            var image = TiffCoder.Read(tempFile);

            await Assert.That(image.Columns).IsEqualTo(width);
            await Assert.That(image.Rows).IsEqualTo(height);

            // All pixels should be white (MinIsBlack → 0 maps to quantum 0 → black...
            // Wait, we use PhotometricMinIsBlack=1, so 0=black, 1=white is WRONG.
            // CCITT typically uses MinIsWhite (0). Let me use that.
            // Actually our BuildCcittTiff uses MinIsBlack so 0=black, which means
            // output of decoder value 0 → ScaleToQuantum(0,1) = 0 → and MinIsBlack means 0=black.
            // So all-white pattern (decoder output 0) = all black pixels? No...
            // Let me think: decoder output 0 = white pixel. With MinIsBlack, value 0 = black.
            // That's inverted! CCITT convention: 0=white, 1=black. But with MinIsBlack,
            // 0 = minimum intensity = black. So we need MinIsWhite for CCITT.
            // Let me fix BuildCcittTiff to use MinIsWhite.

            // With MinIsWhite: value 0 → white → ScaleToQuantum(0,1)=0, then invertGray
            // gives MaxValue. So white pixel = MaxValue.
            for (int y = 0; y < height; y++)
            {
                var row = image.GetPixelRow(y).ToArray();
                for (int x = 0; x < width; x++)
                {
                    // White pixel should have high intensity
                    await Assert.That(row[x * 3]).IsEqualTo(Quantum.MaxValue);
                }
            }

            image.Dispose();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Tiff_CcittModifiedHuffman_AllBlack()
    {
        int width = 16;
        int height = 2;

        // All black
        var pattern = new byte[width * height];
        Array.Fill(pattern, (byte)1);

        var mhData = EncodeModifiedHuffmanRows(pattern, width, height);
        byte[] tiffData = BuildCcittTiff(mhData, width, height, compression: 2);

        string tempFile = TempPath("ccitt_mh_black.tiff");
        try
        {
            File.WriteAllBytes(tempFile, tiffData);
            var image = TiffCoder.Read(tempFile);

            await Assert.That(image.Columns).IsEqualTo(width);
            await Assert.That(image.Rows).IsEqualTo(height);

            // With MinIsWhite: value 1 → black → ScaleToQuantum(1,1)=MaxValue, invertGray → 0
            for (int y = 0; y < height; y++)
            {
                var row = image.GetPixelRow(y).ToArray();
                for (int x = 0; x < width; x++)
                    await Assert.That(row[x * 3]).IsEqualTo((ushort)0);
            }

            image.Dispose();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // PSD LAYER TESTS
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Psd_DecodeWithLayers_CompositeIsPreserved()
    {
        // Create a simple PSD with Encode, then DecodeWithLayers should give us the composite
        var original = CreateTestImage(10, 10, 255, 0, 0);
        byte[] psdBytes = PsdCoder.Encode(original);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);

        await Assert.That(doc.Composite.Columns).IsEqualTo(10);
        await Assert.That(doc.Composite.Rows).IsEqualTo(10);
        // No layers in a flat PSD
        await Assert.That(doc.Layers.Count).IsEqualTo(0);

        original.Dispose();
        doc.Composite.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_TwoLayers()
    {
        // Build a PSD with 2 layers manually
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: 16, canvasHeight: 16, depth: 8,
            layers:
            [
                new TestLayer("Background", 0, 0, 16, 16, 255, 0, true, "norm",
                    CreateSolidChannelData(16, 16, 1, 200, 100, 50)),
                new TestLayer("Overlay", 2, 2, 10, 10, 128, 0, true, "norm",
                    CreateSolidChannelData(10, 10, 1, 0, 255, 0))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);

        await Assert.That(doc.Layers.Count).IsEqualTo(2);
        await Assert.That(doc.Layers[0].Name).IsEqualTo("Background");
        await Assert.That(doc.Layers[1].Name).IsEqualTo("Overlay");
        await Assert.That(doc.Layers[0].Width).IsEqualTo(16);
        await Assert.That(doc.Layers[0].Height).IsEqualTo(16);
        await Assert.That(doc.Layers[1].Width).IsEqualTo(10);
        await Assert.That(doc.Layers[1].Height).IsEqualTo(10);
        await Assert.That(doc.Layers[1].Left).IsEqualTo(2);
        await Assert.That(doc.Layers[1].Top).IsEqualTo(2);

        doc.Composite.Dispose();
        foreach (var l in doc.Layers)
            l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_LayerOpacity()
    {
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: 8, canvasHeight: 8, depth: 8,
            layers:
            [
                new TestLayer("HalfOpaque", 0, 0, 8, 8, 128, 0, true, "norm",
                    CreateSolidChannelData(8, 8, 1, 255, 0, 0))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);

        await Assert.That(doc.Layers.Count).IsEqualTo(1);
        await Assert.That(doc.Layers[0].Opacity).IsEqualTo((byte)128);
        await Assert.That(doc.Layers[0].Visible).IsTrue();

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_HiddenLayer()
    {
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: 8, canvasHeight: 8, depth: 8,
            layers:
            [
                new TestLayer("Hidden", 0, 0, 8, 8, 255, 0x02, false, "norm",
                    CreateSolidChannelData(8, 8, 1, 0, 0, 255))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);

        await Assert.That(doc.Layers.Count).IsEqualTo(1);
        await Assert.That(doc.Layers[0].Visible).IsFalse();

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_BlendMode()
    {
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: 8, canvasHeight: 8, depth: 8,
            layers:
            [
                new TestLayer("Multiply", 0, 0, 8, 8, 255, 0, true, "mul ",
                    CreateSolidChannelData(8, 8, 1, 128, 128, 128))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);

        await Assert.That(doc.Layers[0].BlendMode).IsEqualTo("mul ");

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_LayerPixelData_8bit()
    {
        // Create a layer with known pixel data and verify it's correctly decoded
        int w = 4, h = 4;
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: w, canvasHeight: h, depth: 8,
            layers:
            [
                new TestLayer("Red", 0, 0, w, h, 255, 0, true, "norm",
                    CreateSolidChannelData(w, h, 1, 255, 0, 0))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);
        var layer = doc.Layers[0];

        await Assert.That(layer.Image).IsNotNull();
        await Assert.That(layer.Image!.Columns).IsEqualTo(w);
        await Assert.That(layer.Image!.Rows).IsEqualTo(h);

        // Verify red pixels
        var row = layer.Image!.GetPixelRow(0).ToArray();
        int ch = layer.Image!.NumberOfChannels;
        // Red channel should be max
        await Assert.That(row[0]).IsEqualTo(Quantum.ScaleFromByte(255));
        // Green should be 0
        await Assert.That(row[1]).IsEqualTo((ushort)0);
        // Blue should be 0
        await Assert.That(row[2]).IsEqualTo((ushort)0);

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_16bit()
    {
        int w = 4, h = 4;
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: w, canvasHeight: h, depth: 16,
            layers:
            [
                new TestLayer("Green16", 0, 0, w, h, 255, 0, true, "norm",
                    CreateSolidChannelData(w, h, 2, 0, 255, 0))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);
        var layer = doc.Layers[0];

        await Assert.That(layer.Image).IsNotNull();
        await Assert.That(layer.Image!.Columns).IsEqualTo(w);

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_LayerWithAlpha()
    {
        int w = 4, h = 4;
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: w, canvasHeight: h, depth: 8,
            layers:
            [
                new TestLayer("WithAlpha", 0, 0, w, h, 255, 0, true, "norm",
                    CreateChannelDataWithAlpha(w, h, 1, 128, 64, 32, 200))
            ],
            hasAlpha: true);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);
        var layer = doc.Layers[0];

        await Assert.That(layer.Image).IsNotNull();
        await Assert.That(layer.Image!.HasAlpha).IsTrue();

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    [Test]
    public async Task Psd_DecodeWithLayers_MultipleBlendModes()
    {
        string[] blendModes = ["norm", "mul ", "scrn", "over", "dark", "lite"];

        foreach (string mode in blendModes)
        {
            byte[] psdBytes = BuildLayeredPsd(
                canvasWidth: 4, canvasHeight: 4, depth: 8,
                layers:
                [
                    new TestLayer($"Layer_{mode}", 0, 0, 4, 4, 255, 0, true, mode,
                        CreateSolidChannelData(4, 4, 1, 100, 100, 100))
                ]);

            var doc = PsdCoder.DecodeWithLayers(psdBytes);
            await Assert.That(doc.Layers[0].BlendMode).IsEqualTo(mode);

            doc.Composite.Dispose();
            foreach (var l in doc.Layers) l.Image?.Dispose();
        }
    }

    [Test]
    public async Task Psd_DecodeWithLayers_OffsetLayer()
    {
        // Layer positioned at (3,5) within 16x16 canvas
        byte[] psdBytes = BuildLayeredPsd(
            canvasWidth: 16, canvasHeight: 16, depth: 8,
            layers:
            [
                new TestLayer("Offset", 5, 3, 6, 4, 255, 0, true, "norm",
                    CreateSolidChannelData(6, 4, 1, 255, 128, 0))
            ]);

        var doc = PsdCoder.DecodeWithLayers(psdBytes);
        var layer = doc.Layers[0];

        await Assert.That(layer.Top).IsEqualTo(5);
        await Assert.That(layer.Left).IsEqualTo(3);
        await Assert.That(layer.Width).IsEqualTo(6);
        await Assert.That(layer.Height).IsEqualTo(4);

        doc.Composite.Dispose();
        foreach (var l in doc.Layers) l.Image?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS — CCITT Encoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple G4 encoder using only horizontal mode for testing.
    /// </summary>
    private static byte[] EncodeWithGroup4Horizontal(byte[] pattern, int width, int height)
    {
        var bits = new BitWriter();
        for (int y = 0; y < height; y++)
        {
            int col = 0;
            bool isWhite = true;
            int offset = y * width;

            while (col < width)
            {
                // Count run of current color
                int run = 0;
                byte target = isWhite ? (byte)0 : (byte)1;
                while (col + run < width && pattern[offset + col + run] == target)
                    run++;

                // Count next run of opposite color
                int nextRun = 0;
                byte nextTarget = isWhite ? (byte)1 : (byte)0;
                int nextStart = col + run;
                while (nextStart + nextRun < width && pattern[offset + nextStart + nextRun] == nextTarget)
                    nextRun++;

                // Horizontal mode: 001
                bits.WriteBits(0b001, 3);
                WriteRunCode(bits, run, isWhite);
                WriteRunCode(bits, nextRun, !isWhite);

                col += run + nextRun;
                // Color stays the same after horizontal mode
            }
        }
        bits.PadToByte();
        return bits.ToArray();
    }

    private static byte[] EncodeModifiedHuffmanRows(byte[] pattern, int width, int height)
    {
        var bits = new BitWriter();
        for (int y = 0; y < height; y++)
        {
            int col = 0;
            bool isWhite = true;
            int offset = y * width;

            while (col < width)
            {
                int run = 0;
                byte target = isWhite ? (byte)0 : (byte)1;
                while (col + run < width && pattern[offset + col + run] == target)
                    run++;

                WriteRunCode(bits, run, isWhite);
                col += run;
                isWhite = !isWhite;
            }
            bits.PadToByte();
        }
        return bits.ToArray();
    }

    private static byte[] EncodeWhiteRun(int length)
    {
        var bits = new BitWriter();
        WriteRunCode(bits, length, isWhite: true);
        bits.PadToByte();
        return bits.ToArray();
    }

    // Standard CCITT Huffman code tables for encoding
    private static readonly (int Code, int Bits)[] WhiteTermCodes =
    [
        (0b00110101, 8), (0b000111, 6), (0b0111, 4), (0b1000, 4),
        (0b1011, 4), (0b1100, 4), (0b1110, 4), (0b1111, 4),
        (0b10011, 5), (0b10100, 5), (0b00111, 5), (0b01000, 5),
        (0b001000, 6), (0b000011, 6), (0b110100, 6), (0b110101, 6),
        (0b101010, 6), (0b101011, 6), (0b0100111, 7), (0b0001100, 7),
        (0b0001000, 7), (0b0010111, 7), (0b0000011, 7), (0b0000100, 7),
        (0b0101000, 7), (0b0101011, 7), (0b0010011, 7), (0b0100100, 7),
        (0b0011000, 7), (0b00000010, 8), (0b00000011, 8), (0b00011010, 8),
        (0b00011011, 8), (0b00010010, 8), (0b00010011, 8), (0b00010100, 8),
        (0b00010101, 8), (0b00010110, 8), (0b00010111, 8), (0b00101000, 8),
        (0b00101001, 8), (0b00101010, 8), (0b00101011, 8), (0b00101100, 8),
        (0b00101101, 8), (0b00000100, 8), (0b00000101, 8), (0b00001010, 8),
        (0b00001011, 8), (0b01010010, 8), (0b01010011, 8), (0b01010100, 8),
        (0b01010101, 8), (0b00100100, 8), (0b00100101, 8), (0b01011000, 8),
        (0b01011001, 8), (0b01011010, 8), (0b01011011, 8), (0b01001010, 8),
        (0b01001011, 8), (0b00110010, 8), (0b00110011, 8), (0b00110100, 8),
    ];

    private static readonly (int Code, int Bits)[] BlackTermCodes =
    [
        (0b0000110111, 10), (0b010, 3), (0b11, 2), (0b10, 2),
        (0b011, 3), (0b0011, 4), (0b0010, 4), (0b00011, 5),
        (0b000101, 6), (0b000100, 6), (0b0000100, 7), (0b0000101, 7),
        (0b0000111, 7), (0b00000100, 8), (0b00000111, 8), (0b000011000, 9),
        (0b0000010111, 10), (0b0000011000, 10), (0b0000001000, 10),
        (0b00001100111, 11), (0b00001101000, 11), (0b00001101100, 11),
        (0b00000110111, 11), (0b00000101000, 11), (0b00000010111, 11),
        (0b00000011000, 11), (0b000011001010, 12), (0b000011001011, 12),
        (0b000011001100, 12), (0b000011001101, 12), (0b000001101000, 12),
        (0b000001101001, 12), (0b000001101010, 12), (0b000001101011, 12),
        (0b000011010010, 12), (0b000011010011, 12), (0b000011010100, 12),
        (0b000011010101, 12), (0b000011010110, 12), (0b000011010111, 12),
        (0b000001101100, 12), (0b000001101101, 12), (0b000011011010, 12),
        (0b000011011011, 12), (0b000001010100, 12), (0b000001010101, 12),
        (0b000001010110, 12), (0b000001010111, 12), (0b000001100100, 12),
        (0b000001100101, 12), (0b000001010010, 12), (0b000001010011, 12),
        (0b000000100100, 12), (0b000000110111, 12), (0b000000111000, 12),
        (0b000000100111, 12), (0b000000101000, 12), (0b000001011000, 12),
        (0b000001011001, 12), (0b000000101011, 12), (0b000000101100, 12),
        (0b000001011010, 12), (0b000001100110, 12), (0b000001100111, 12),
    ];

    private static readonly (int Code, int Bits, int RunLength)[] WhiteMakeupCodes =
    [
        (0b11011, 5, 64), (0b10010, 5, 128), (0b010111, 6, 192),
        (0b0110111, 7, 256), (0b00110110, 8, 320), (0b00110111, 8, 384),
        (0b01100100, 8, 448), (0b01100101, 8, 512), (0b01101000, 8, 576),
        (0b01100111, 8, 640),
    ];

    private static readonly (int Code, int Bits, int RunLength)[] BlackMakeupCodes =
    [
        (0b0000001111, 10, 64), (0b000011001000, 12, 128),
        (0b000011001001, 12, 192), (0b000001011011, 12, 256),
        (0b000000110011, 12, 320), (0b000000110100, 12, 384),
        (0b000000110101, 12, 448),
    ];

    private static void WriteRunCode(BitWriter bits, int runLength, bool isWhite)
    {
        // Write makeup codes for runs >= 64
        while (runLength >= 64)
        {
            var makeupTable = isWhite ? WhiteMakeupCodes : BlackMakeupCodes;
            // Find largest makeup code <= runLength
            int bestIdx = -1;
            for (int i = makeupTable.Length - 1; i >= 0; i--)
            {
                if (makeupTable[i].RunLength <= runLength)
                {
                    bestIdx = i;
                    break;
                }
            }

            if (bestIdx >= 0)
            {
                bits.WriteBits(makeupTable[bestIdx].Code, makeupTable[bestIdx].Bits);
                runLength -= makeupTable[bestIdx].RunLength;
            }
            else
            {
                break;
            }
        }

        // Write terminating code (0-63)
        var termTable = isWhite ? WhiteTermCodes : BlackTermCodes;
        if (runLength < termTable.Length)
            bits.WriteBits(termTable[runLength].Code, termTable[runLength].Bits);
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS — TIFF Building
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a minimal TIFF file with CCITT-compressed 1-bit data.
    /// Uses MinIsWhite photometric interpretation (standard for fax).
    /// </summary>
    private static byte[] BuildCcittTiff(byte[] compressedData, int width, int height, int compression)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: little-endian
        writer.Write((byte)0x49); // I
        writer.Write((byte)0x49); // I
        writer.Write((ushort)42); // magic

        // Data starts at offset 8
        int dataOffset = 8;
        writer.Write((uint)0); // IFD offset placeholder

        // Write compressed data
        writer.Write(compressedData);

        // Pad to word boundary
        if (ms.Position % 2 != 0)
            writer.Write((byte)0);

        int ifdOffset = (int)ms.Position;

        // Go back and write IFD offset
        ms.Seek(4, SeekOrigin.Begin);
        writer.Write((uint)ifdOffset);
        ms.Seek(ifdOffset, SeekOrigin.Begin);

        // IFD entries (sorted by tag number)
        int numTags = 8;
        writer.Write((ushort)numTags);

        // Tag 256: ImageWidth
        WriteIfdEntry(writer, 256, 3, 1, (uint)width);
        // Tag 257: ImageLength
        WriteIfdEntry(writer, 257, 3, 1, (uint)height);
        // Tag 258: BitsPerSample = 1
        WriteIfdEntry(writer, 258, 3, 1, 1);
        // Tag 259: Compression
        WriteIfdEntry(writer, 259, 3, 1, (uint)compression);
        // Tag 262: PhotometricInterpretation = MinIsWhite (0)
        WriteIfdEntry(writer, 262, 3, 1, 0);
        // Tag 273: StripOffsets
        WriteIfdEntry(writer, 273, 4, 1, (uint)dataOffset);
        // Tag 277: SamplesPerPixel = 1
        WriteIfdEntry(writer, 277, 3, 1, 1);
        // Tag 279: StripByteCounts
        WriteIfdEntry(writer, 279, 4, 1, (uint)compressedData.Length);

        // Next IFD offset = 0 (no more IFDs)
        writer.Write((uint)0);

        return ms.ToArray();
    }

    private static void WriteIfdEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS — PSD Building
    // ═══════════════════════════════════════════════════════════════

    private record TestLayer(
        string Name, int Top, int Left, int Width, int Height,
        byte Opacity, byte Flags, bool Visible, string BlendMode,
        byte[][] ChannelData);

    private static byte[] BuildLayeredPsd(int canvasWidth, int canvasHeight, int depth,
        TestLayer[] layers, bool hasAlpha = false)
    {
        using var ms = new MemoryStream();
        int bytesPerChannel = depth / 8;
        int psdChannels = hasAlpha ? 4 : 3;

        // Header
        ms.Write("8BPS"u8);
        WriteU16BE(ms, 1); // version
        ms.Write(new byte[6]); // reserved
        WriteU16BE(ms, (ushort)psdChannels); // channels
        WriteU32BE(ms, (uint)canvasHeight);
        WriteU32BE(ms, (uint)canvasWidth);
        WriteU16BE(ms, (ushort)depth);
        WriteU16BE(ms, 3); // RGB

        // Color mode data (empty)
        WriteU32BE(ms, 0);

        // Image resources (empty)
        WriteU32BE(ms, 0);

        // Layer and mask information section
        var layerSection = new MemoryStream();
        BuildLayerInfoSection(layerSection, layers, depth, hasAlpha);

        // Write layer section length
        byte[] layerSectionBytes = layerSection.ToArray();
        // Layer and mask info length includes the layer info sub-section
        WriteU32BE(ms, (uint)layerSectionBytes.Length);
        ms.Write(layerSectionBytes);

        // Composite image data (raw, all zeros)
        WriteU16BE(ms, 0); // compression = raw
        int pixelCount = canvasWidth * canvasHeight;
        for (int c = 0; c < psdChannels; c++)
        {
            ms.Write(new byte[pixelCount * bytesPerChannel]);
        }

        return ms.ToArray();
    }

    private static void BuildLayerInfoSection(MemoryStream ms, TestLayer[] layers,
        int depth, bool hasAlpha)
    {
        var layerInfo = new MemoryStream();
        int bytesPerChannel = depth / 8;

        // Layer count
        WriteU16BE(layerInfo, (ushort)layers.Length);

        // Layer records
        foreach (var layer in layers)
        {
            int numChannels = layer.ChannelData.Length;

            // Bounds: top, left, bottom, right
            WriteU32BE(layerInfo, (uint)layer.Top);
            WriteU32BE(layerInfo, (uint)layer.Left);
            WriteU32BE(layerInfo, (uint)(layer.Top + layer.Height));
            WriteU32BE(layerInfo, (uint)(layer.Left + layer.Width));

            // Channel count
            WriteU16BE(layerInfo, (ushort)numChannels);

            // Channel info
            short[] channelIds = numChannels == 4
                ? [-1, 0, 1, 2]   // Alpha, R, G, B
                : [0, 1, 2];      // R, G, B

            for (int c = 0; c < numChannels; c++)
            {
                WriteU16BE(layerInfo, (ushort)(short)channelIds[c]);
                // Channel data size: compression type (2 bytes) + raw data
                uint channelDataSize = (uint)(2 + layer.ChannelData[c].Length);
                WriteU32BE(layerInfo, channelDataSize);
            }

            // Blend mode signature
            layerInfo.Write("8BIM"u8);

            // Blend mode key
            byte[] blendKey = System.Text.Encoding.ASCII.GetBytes(layer.BlendMode.PadRight(4));
            layerInfo.Write(blendKey);

            // Opacity
            layerInfo.WriteByte(layer.Opacity);

            // Clipping
            layerInfo.WriteByte(0);

            // Flags
            layerInfo.WriteByte(layer.Visible ? (byte)0 : (byte)0x02);

            // Filler
            layerInfo.WriteByte(0);

            // Extra data
            var extraData = new MemoryStream();
            // Mask data: 0 length
            WriteU32BE(extraData, 0);
            // Blending ranges: 0 length
            WriteU32BE(extraData, 0);
            // Layer name (Pascal string, padded to 4-byte boundary)
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(layer.Name);
            extraData.WriteByte((byte)nameBytes.Length);
            extraData.Write(nameBytes);
            // Pad to 4 bytes
            int totalNameLen = 1 + nameBytes.Length;
            int padNeeded = ((totalNameLen + 3) & ~3) - totalNameLen;
            for (int i = 0; i < padNeeded; i++)
                extraData.WriteByte(0);

            byte[] extraBytes = extraData.ToArray();
            WriteU32BE(layerInfo, (uint)extraBytes.Length);
            layerInfo.Write(extraBytes);
        }

        // Channel image data for each layer
        foreach (var layer in layers)
        {
            for (int c = 0; c < layer.ChannelData.Length; c++)
            {
                WriteU16BE(layerInfo, 0); // compression = raw
                layerInfo.Write(layer.ChannelData[c]);
            }
        }

        // Write layer info sub-section with length
        byte[] layerInfoBytes = layerInfo.ToArray();
        WriteU32BE(ms, (uint)layerInfoBytes.Length);
        ms.Write(layerInfoBytes);
    }

    private static byte[][] CreateSolidChannelData(int width, int height, int bytesPerChannel,
        byte r, byte g, byte b)
    {
        int pixelCount = width * height;
        var channels = new byte[3][];

        byte[] values = [r, g, b];
        for (int c = 0; c < 3; c++)
        {
            channels[c] = new byte[pixelCount * bytesPerChannel];
            if (bytesPerChannel == 1)
            {
                Array.Fill(channels[c], values[c]);
            }
            else // 16-bit
            {
                ushort val = (ushort)(values[c] << 8 | values[c]);
                for (int i = 0; i < pixelCount; i++)
                {
                    channels[c][i * 2] = (byte)(val >> 8);
                    channels[c][i * 2 + 1] = (byte)(val & 0xFF);
                }
            }
        }

        return channels;
    }

    private static byte[][] CreateChannelDataWithAlpha(int width, int height, int bytesPerChannel,
        byte r, byte g, byte b, byte a)
    {
        int pixelCount = width * height;
        var channels = new byte[4][];

        // Alpha channel is first (channel ID -1)
        byte[] values = [a, r, g, b];
        for (int c = 0; c < 4; c++)
        {
            channels[c] = new byte[pixelCount * bytesPerChannel];
            Array.Fill(channels[c], values[c]);
        }

        return channels;
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS — Common
    // ═══════════════════════════════════════════════════════════════

    private static ImageFrame CreateTestImage(int width, int height, byte r, byte g, byte b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(r);
                row[offset + 1] = Quantum.ScaleFromByte(g);
                row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return image;
    }

    private static void WriteU16BE(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteU32BE(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    /// <summary>
    /// MSB-first bit writer for constructing CCITT test data.
    /// </summary>
    private class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int currentByte;
        private int bitPos; // 0-7, 0 = MSB

        public void WriteBits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                int bit = (value >> i) & 1;
                currentByte |= bit << (7 - bitPos);
                bitPos++;
                if (bitPos >= 8)
                {
                    bytes.Add((byte)currentByte);
                    currentByte = 0;
                    bitPos = 0;
                }
            }
        }

        public void PadToByte()
        {
            if (bitPos > 0)
            {
                bytes.Add((byte)currentByte);
                currentByte = 0;
                bitPos = 0;
            }
        }

        public byte[] ToArray()
        {
            if (bitPos > 0)
                PadToByte();
            return [.. bytes];
        }
    }
}
