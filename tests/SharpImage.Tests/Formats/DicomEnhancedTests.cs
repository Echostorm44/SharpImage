// Tests for enhanced DICOM codec: RLE decompression, palette color, 12-bit packed,
// planar configuration, implicit VR, sequence skipping, window/level, MONOCHROME1/2.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Text;

namespace SharpImage.Tests.Formats;

public class DicomEnhancedTests
{
    #region Helpers

    private static ImageFrame CreateGrayscaleFrame(int w, int h, byte intensity)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        ushort q = Quantum.ScaleFromByte(intensity);
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * ch;
                row[off] = q;
                if (ch > 1) row[off + 1] = q;
                if (ch > 2) row[off + 2] = q;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientFrame(int w, int h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            byte val = (byte)(y * 255 / Math.Max(h - 1, 1));
            for (int x = 0; x < w; x++)
            {
                ushort q = Quantum.ScaleFromByte(val);
                int off = x * ch;
                row[off] = q;
                if (ch > 1) row[off + 1] = q;
                if (ch > 2) row[off + 2] = q;
            }
        }
        return frame;
    }

    // Builds a minimal DICOM byte array by hand with given parameters
    private static byte[] BuildDicom(int width, int height, int bitsAllocated, int bitsStored,
        string photometric, byte[] pixelData,
        string? transferSyntaxUid = null, bool explicitVr = true,
        ushort[]? redLut = null, ushort[]? greenLut = null, ushort[]? blueLut = null,
        int samplesPerPixel = 1, int planarConfig = 0,
        double? windowCenter = null, double? windowWidth = null,
        double? rescaleSlope = null, double? rescaleIntercept = null,
        bool isRle = false, byte[]? rlePixelData = null)
    {
        var output = new List<byte>();

        // Preamble + magic
        output.AddRange(new byte[128]);
        output.AddRange("DICM"u8.ToArray());

        // Transfer Syntax (group 0002)
        string ts = transferSyntaxUid ?? "1.2.840.10008.1.2.1";
        WriteExplicitTag(output, 0x0002, 0x0010, "UI", PadUid(ts));

        // Image tags (group 0028)
        WriteExplicitTagU16(output, 0x0028, 0x0002, "US", (ushort)samplesPerPixel);
        WriteExplicitTag(output, 0x0028, 0x0004, "CS", PadString(photometric));
        if (samplesPerPixel > 1)
            WriteExplicitTagU16(output, 0x0028, 0x0006, "US", (ushort)planarConfig);
        WriteExplicitTagU16(output, 0x0028, 0x0010, "US", (ushort)height);
        WriteExplicitTagU16(output, 0x0028, 0x0011, "US", (ushort)width);
        WriteExplicitTagU16(output, 0x0028, 0x0100, "US", (ushort)bitsAllocated);
        WriteExplicitTagU16(output, 0x0028, 0x0101, "US", (ushort)bitsStored);
        WriteExplicitTagU16(output, 0x0028, 0x0102, "US", (ushort)(bitsStored - 1));
        WriteExplicitTagU16(output, 0x0028, 0x0103, "US", 0); // unsigned

        if (windowCenter.HasValue)
            WriteExplicitTag(output, 0x0028, 0x1050, "DS", PadString(windowCenter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (windowWidth.HasValue)
            WriteExplicitTag(output, 0x0028, 0x1051, "DS", PadString(windowWidth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (rescaleIntercept.HasValue)
            WriteExplicitTag(output, 0x0028, 0x1052, "DS", PadString(rescaleIntercept.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (rescaleSlope.HasValue)
            WriteExplicitTag(output, 0x0028, 0x1053, "DS", PadString(rescaleSlope.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Palette LUT
        if (redLut != null)
            WriteExplicitTagU16Array(output, 0x0028, 0x1201, "OW", redLut);
        if (greenLut != null)
            WriteExplicitTagU16Array(output, 0x0028, 0x1202, "OW", greenLut);
        if (blueLut != null)
            WriteExplicitTagU16Array(output, 0x0028, 0x1203, "OW", blueLut);

        // Pixel data
        if (isRle && rlePixelData != null)
        {
            // Encapsulated pixel data with undefined length
            WriteU16LE(output, 0x7FE0);
            WriteU16LE(output, 0x0010);
            output.AddRange("OB"u8.ToArray());
            WriteU16LE(output, 0); // reserved
            WriteU32LE(output, 0xFFFFFFFF); // undefined length

            // Empty offset table item
            WriteU16LE(output, 0xFFFE);
            WriteU16LE(output, 0xE000);
            WriteU32LE(output, 0); // empty offset table

            // RLE frame item
            WriteU16LE(output, 0xFFFE);
            WriteU16LE(output, 0xE000);
            WriteU32LE(output, (uint)rlePixelData.Length);
            output.AddRange(rlePixelData);

            // Sequence delimiter
            WriteU16LE(output, 0xFFFE);
            WriteU16LE(output, 0xE0DD);
            WriteU32LE(output, 0);
        }
        else
        {
            WriteExplicitTagBytes(output, 0x7FE0, 0x0010, "OW", pixelData);
        }

        return output.ToArray();
    }

    private static byte[] BuildRleSegmentData(int segmentCount, byte[][] segments)
    {
        // RLE header: 16 uint32 values (segment count + 15 offsets)
        var rleData = new List<byte>();
        WriteU32LE(rleData, (uint)segmentCount);

        // Calculate offsets (relative to start of header)
        int headerSize = 64;
        int currentOffset = headerSize;
        for (int i = 0; i < 15; i++)
        {
            if (i < segmentCount)
            {
                WriteU32LE(rleData, (uint)currentOffset);
                currentOffset += segments[i].Length;
            }
            else
            {
                WriteU32LE(rleData, 0);
            }
        }

        // Append segment data
        for (int i = 0; i < segmentCount; i++)
            rleData.AddRange(segments[i]);

        return rleData.ToArray();
    }

    // Encode a byte array as RLE (PackBits style)
    private static byte[] EncodeRleSegment(byte[] input)
    {
        var output = new List<byte>();
        int i = 0;
        while (i < input.Length)
        {
            // Check for run
            int runStart = i;
            while (i + 1 < input.Length && input[i] == input[i + 1] && i - runStart < 127)
                i++;
            int runLen = i - runStart + 1;

            if (runLen >= 3 || i == input.Length - 1 || (runLen >= 2 && i + 1 >= input.Length))
            {
                // Repeat run: -(runLen-1) as sbyte, then the byte value
                output.Add((byte)(sbyte)(-(runLen - 1)));
                output.Add(input[runStart]);
                i = runStart + runLen;
            }
            else
            {
                // Literal run
                i = runStart;
                int litStart = i;
                while (i < input.Length && i - litStart < 128)
                {
                    if (i + 2 < input.Length && input[i] == input[i + 1] && input[i + 1] == input[i + 2])
                        break;
                    i++;
                }
                int litLen = i - litStart;
                output.Add((byte)(litLen - 1));
                for (int j = litStart; j < litStart + litLen; j++)
                    output.Add(input[j]);
            }
        }
        return output.ToArray();
    }

    #region Low-Level Writers

    private static void WriteExplicitTag(List<byte> output, ushort group, ushort element, string vr, string value)
    {
        byte[] valBytes = Encoding.ASCII.GetBytes(value);
        WriteExplicitTagBytes(output, group, element, vr, valBytes);
    }

    private static void WriteExplicitTagBytes(List<byte> output, ushort group, ushort element, string vr, byte[] value)
    {
        WriteU16LE(output, group);
        WriteU16LE(output, element);
        output.AddRange(Encoding.ASCII.GetBytes(vr));
        if (vr is "OB" or "OD" or "OF" or "OL" or "OW" or "SQ" or "UC" or "UN" or "UR" or "UT")
        {
            WriteU16LE(output, 0);
            WriteU32LE(output, (uint)value.Length);
        }
        else
        {
            WriteU16LE(output, (ushort)value.Length);
        }
        output.AddRange(value);
    }

    private static void WriteExplicitTagU16(List<byte> output, ushort group, ushort element, string vr, ushort value)
    {
        byte[] val = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(val, value);
        WriteExplicitTagBytes(output, group, element, vr, val);
    }

    private static void WriteExplicitTagU16Array(List<byte> output, ushort group, ushort element, string vr, ushort[] values)
    {
        byte[] val = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(val.AsSpan(i * 2), values[i]);
        WriteExplicitTagBytes(output, group, element, vr, val);
    }

    private static void WriteU16LE(List<byte> output, ushort value)
    {
        output.Add((byte)(value & 0xFF));
        output.Add((byte)(value >> 8));
    }

    private static void WriteU32LE(List<byte> output, uint value)
    {
        output.Add((byte)(value & 0xFF));
        output.Add((byte)((value >> 8) & 0xFF));
        output.Add((byte)((value >> 16) & 0xFF));
        output.Add((byte)(value >> 24));
    }

    private static string PadUid(string uid) => uid.Length % 2 == 0 ? uid : $"{uid}\0";
    private static string PadString(string s) => s.Length % 2 == 0 ? s : $"{s} ";

    #endregion

    #endregion

    // --- Basic Round-Trip ---

    [Test]
    public async Task Roundtrip_16BitGrayscale_PreservesPixels()
    {
        var original = CreateGradientFrame(32, 32);
        byte[] encoded = DicomCoder.Encode(original);
        var decoded = DicomCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(32u);
        await Assert.That(decoded.Rows).IsEqualTo(32u);

        // Encoder writes grayscale, decoder reads it back — pixel values should be close
        for (int y = 0; y < 32; y++)
        {
            var origRow = original.GetPixelRow(y);
            var decRow = decoded.GetPixelRow(y);
            // R channel should be preserved (grayscale round-trip)
            int diff = Math.Abs(origRow[0] - decRow[0]);
            if (diff > 512) // ~0.8% tolerance due to luminance conversion
                throw new Exception($"Row {y} pixel mismatch: {origRow[0]} vs {decRow[0]}");
        }
    }

    [Test]
    public async Task Roundtrip_SolidBlack_AllZeros()
    {
        var original = CreateGrayscaleFrame(8, 8, 0);
        byte[] encoded = DicomCoder.Encode(original);
        var decoded = DicomCoder.Decode(encoded);

        var pixel = decoded.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)pixel.Red)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Roundtrip_SolidWhite_AllMax()
    {
        var original = CreateGrayscaleFrame(8, 8, 255);
        byte[] encoded = DicomCoder.Encode(original);
        var decoded = DicomCoder.Decode(encoded);

        var pixel = decoded.GetPixel(0, 0);
        byte lum = Quantum.ScaleToByte((ushort)pixel.Red);
        await Assert.That(lum).IsGreaterThanOrEqualTo((byte)250);
    }

    // --- 8-Bit Uncompressed ---

    [Test]
    public async Task Decode_8BitGrayscale_CorrectValues()
    {
        int w = 4, h = 4;
        byte[] pixels = new byte[w * h];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 16);

        byte[] dcm = BuildDicom(w, h, 8, 8, "MONOCHROME2", pixels);
        var frame = DicomCoder.Decode(dcm);

        await Assert.That(frame.Columns).IsEqualTo((uint)w);
        await Assert.That(frame.Rows).IsEqualTo((uint)h);

        byte firstPixel = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        await Assert.That(firstPixel).IsEqualTo((byte)0);

        byte lastPixel = Quantum.ScaleToByte((ushort)frame.GetPixel(3, 3).Red);
        await Assert.That(lastPixel).IsEqualTo((byte)240);
    }

    // --- MONOCHROME1 (Inverted) ---

    [Test]
    public async Task Decode_Monochrome1_InvertsValues()
    {
        int w = 2, h = 2;
        byte[] pixels = [0, 0, 255, 255];
        byte[] dcm = BuildDicom(w, h, 8, 8, "MONOCHROME1", pixels);
        var frame = DicomCoder.Decode(dcm);

        // MONOCHROME1: 0 maps to white, 255 maps to black
        byte topLeft = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        byte bottomRight = Quantum.ScaleToByte((ushort)frame.GetPixel(1, 1).Red);

        await Assert.That(topLeft).IsGreaterThanOrEqualTo((byte)250);   // inverted 0 → ~255
        await Assert.That(bottomRight).IsLessThanOrEqualTo((byte)5);     // inverted 255 → ~0
    }

    // --- Window/Level ---

    [Test]
    public async Task Decode_WithWindowLevel_AppliesCorrectly()
    {
        int w = 4, h = 1;
        // 16-bit pixels: 0, 100, 200, 300
        byte[] pixels = new byte[w * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(4), 200);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(6), 300);

        byte[] dcm = BuildDicom(w, h, 16, 16, "MONOCHROME2", pixels,
            windowCenter: 200, windowWidth: 200);
        var frame = DicomCoder.Decode(dcm);

        // Window [100, 300]: val 0 → black, 200 → mid, 300 → white
        byte p0 = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        byte p1 = Quantum.ScaleToByte((ushort)frame.GetPixel(1, 0).Red);
        byte p2 = Quantum.ScaleToByte((ushort)frame.GetPixel(2, 0).Red);
        byte p3 = Quantum.ScaleToByte((ushort)frame.GetPixel(3, 0).Red);

        await Assert.That(p0).IsEqualTo((byte)0);       // below window
        await Assert.That(p1).IsLessThanOrEqualTo((byte)5);  // at window min edge
        await Assert.That(p2).IsGreaterThanOrEqualTo((byte)120); // middle
        await Assert.That(p2).IsLessThanOrEqualTo((byte)135);
        await Assert.That(p3).IsEqualTo((byte)255);     // at window max
    }

    // --- Rescale Slope/Intercept ---

    [Test]
    public async Task Decode_WithRescale_AppliesCorrectly()
    {
        int w = 2, h = 1;
        byte[] pixels = new byte[w * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(0), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2), 200);

        byte[] dcm = BuildDicom(w, h, 16, 16, "MONOCHROME2", pixels,
            rescaleSlope: 1.0, rescaleIntercept: 0.0);
        var frame = DicomCoder.Decode(dcm);

        // Without windowing, raw values should map proportionally
        ushort p0 = (ushort)frame.GetPixel(0, 0).Red;
        ushort p1 = (ushort)frame.GetPixel(1, 0).Red;
        await Assert.That(p1).IsGreaterThan(p0);
    }

    // --- RGB Interleaved ---

    [Test]
    public async Task Decode_Rgb_Interleaved_CorrectColors()
    {
        int w = 2, h = 1;
        // RGB interleaved: pixel0=(255,0,0), pixel1=(0,255,0)
        byte[] pixels = [255, 0, 0, 0, 255, 0];
        byte[] dcm = BuildDicom(w, h, 8, 8, "RGB", pixels, samplesPerPixel: 3);
        var frame = DicomCoder.Decode(dcm);

        var p0 = frame.GetPixel(0, 0);
        var p1 = frame.GetPixel(1, 0);

        await Assert.That(Quantum.ScaleToByte((ushort)p0.Red)).IsEqualTo((byte)255);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Green)).IsEqualTo((byte)0);
        await Assert.That(Quantum.ScaleToByte((ushort)p1.Red)).IsEqualTo((byte)0);
        await Assert.That(Quantum.ScaleToByte((ushort)p1.Green)).IsEqualTo((byte)255);
    }

    // --- RGB Planar ---

    [Test]
    public async Task Decode_Rgb_Planar_CorrectColors()
    {
        int w = 2, h = 2;
        // Planar: all R values, then all G values, then all B values
        // 4 pixels: (255,0,0), (128,0,0), (0,255,0), (0,128,0)
        byte[] pixels =
        [
            255, 128, 0, 0,     // R plane
            0, 0, 255, 128,     // G plane
            0, 0, 0, 0          // B plane
        ];
        byte[] dcm = BuildDicom(w, h, 8, 8, "RGB", pixels,
            samplesPerPixel: 3, planarConfig: 1);
        var frame = DicomCoder.Decode(dcm);

        var p0 = frame.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Red)).IsEqualTo((byte)255);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Green)).IsEqualTo((byte)0);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Blue)).IsEqualTo((byte)0);

        var p2 = frame.GetPixel(0, 1);
        await Assert.That(Quantum.ScaleToByte((ushort)p2.Red)).IsEqualTo((byte)0);
        await Assert.That(Quantum.ScaleToByte((ushort)p2.Green)).IsEqualTo((byte)255);
    }

    // --- Palette Color ---

    [Test]
    public async Task Decode_PaletteColor_LookupCorrect()
    {
        int w = 4, h = 1;
        // 4 palette indices: 0, 1, 2, 3
        byte[] pixels = [0, 1, 2, 3];

        // Simple 4-entry palette
        ushort[] redLut = [65535, 0, 0, 32768];
        ushort[] greenLut = [0, 65535, 0, 32768];
        ushort[] blueLut = [0, 0, 65535, 32768];

        byte[] dcm = BuildDicom(w, h, 8, 8, "PALETTE COLOR", pixels,
            redLut: redLut, greenLut: greenLut, blueLut: blueLut);
        var frame = DicomCoder.Decode(dcm);

        // Index 0: red
        var p0 = frame.GetPixel(0, 0);
        await Assert.That(p0.Red).IsEqualTo(65535);
        await Assert.That(p0.Green).IsEqualTo(0);
        await Assert.That(p0.Blue).IsEqualTo(0);

        // Index 1: green
        var p1 = frame.GetPixel(1, 0);
        await Assert.That(p1.Green).IsEqualTo(65535);

        // Index 2: blue
        var p2 = frame.GetPixel(2, 0);
        await Assert.That(p2.Blue).IsEqualTo(65535);

        // Index 3: mid-gray
        var p3 = frame.GetPixel(3, 0);
        await Assert.That(p3.Red).IsEqualTo(32768);
    }

    // --- RLE Compressed ---

    [Test]
    public async Task Decode_Rle_8BitGrayscale_CorrectValues()
    {
        int w = 8, h = 2;
        // Build raw pixel data: all 128 (constant), then gradient 0-7
        byte[] rawPixels = new byte[w * h];
        for (int i = 0; i < w; i++) rawPixels[i] = 128;
        for (int i = 0; i < w; i++) rawPixels[w + i] = (byte)(i * 32);

        // Encode as RLE
        byte[] rleSegment = EncodeRleSegment(rawPixels);
        byte[] rleData = BuildRleSegmentData(1, [rleSegment]);

        byte[] dcm = BuildDicom(w, h, 8, 8, "MONOCHROME2", [],
            transferSyntaxUid: "1.2.840.10008.1.2.5",
            isRle: true, rlePixelData: rleData);
        var frame = DicomCoder.Decode(dcm);

        await Assert.That(frame.Columns).IsEqualTo((uint)w);
        await Assert.That(frame.Rows).IsEqualTo((uint)h);

        // First row should be ~128
        byte topLeft = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        await Assert.That(topLeft).IsEqualTo((byte)128);

        // Second row, first pixel should be 0
        byte bottomLeft = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 1).Red);
        await Assert.That(bottomLeft).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Decode_Rle_16BitGrayscale_TwoSegments()
    {
        int w = 4, h = 2;
        int totalPixels = w * h;

        // Build raw 16-bit values
        ushort[] rawValues = new ushort[totalPixels];
        for (int i = 0; i < totalPixels; i++)
            rawValues[i] = (ushort)(i * 1000);

        // Split into high-byte and low-byte segments
        byte[] highBytes = new byte[totalPixels];
        byte[] lowBytes = new byte[totalPixels];
        for (int i = 0; i < totalPixels; i++)
        {
            highBytes[i] = (byte)(rawValues[i] >> 8);
            lowBytes[i] = (byte)(rawValues[i] & 0xFF);
        }

        byte[] rleHigh = EncodeRleSegment(highBytes);
        byte[] rleLow = EncodeRleSegment(lowBytes);
        byte[] rleData = BuildRleSegmentData(2, [rleHigh, rleLow]);

        byte[] dcm = BuildDicom(w, h, 16, 16, "MONOCHROME2", [],
            transferSyntaxUid: "1.2.840.10008.1.2.5",
            isRle: true, rlePixelData: rleData);
        var frame = DicomCoder.Decode(dcm);

        await Assert.That(frame.Columns).IsEqualTo((uint)w);
        await Assert.That(frame.Rows).IsEqualTo((uint)h);

        // First pixel: value 0
        byte p0 = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        await Assert.That(p0).IsEqualTo((byte)0);

        // Verify progressive increase
        ushort v0 = (ushort)frame.GetPixel(0, 0).Red;
        ushort v1 = (ushort)frame.GetPixel(1, 0).Red;
        await Assert.That(v1).IsGreaterThan(v0);
    }

    [Test]
    public async Task Decode_Rle_AllSameValue_Compressed()
    {
        int w = 16, h = 16;
        byte[] rawPixels = new byte[w * h];
        Array.Fill(rawPixels, (byte)200);

        byte[] rleSegment = EncodeRleSegment(rawPixels);
        byte[] rleData = BuildRleSegmentData(1, [rleSegment]);

        // RLE of 256 identical bytes should be much smaller than raw
        await Assert.That(rleSegment.Length).IsLessThan(rawPixels.Length);

        byte[] dcm = BuildDicom(w, h, 8, 8, "MONOCHROME2", [],
            transferSyntaxUid: "1.2.840.10008.1.2.5",
            isRle: true, rlePixelData: rleData);
        var frame = DicomCoder.Decode(dcm);

        byte val = Quantum.ScaleToByte((ushort)frame.GetPixel(8, 8).Red);
        await Assert.That(val).IsEqualTo((byte)200);
    }

    // --- 12-Bit Packed Pixels ---

    [Test]
    public async Task Decode_12BitPacked_CorrectValues()
    {
        int w = 4, h = 1;
        // 12-bit values: 0, 2048, 4095, 1024
        // Packing: pair of 12-bit values into 3 bytes
        // Even pixel: read 16-bit, lower 4 bits saved, upper 12 bits = value
        // Odd pixel: saved nibble | (next byte << 4)

        // Pack manually: val0=0x000, val1=0x800, val2=0xFFF, val3=0x400
        // Pair 0,1: 16-bit word = (0x000 << 4) | (0x800 & 0xF) = 0x0000, carry=0x0
        //   Then odd: carry=0x0 | (0x80 << 4) = 0x800
        // Actually let me just write raw 16-bit values for 12-bit allocated:
        // With bitsAllocated=12 and bitsStored=12, the decoder uses 12-bit packing
        // The packing is: first pixel uses upper 12 of a 16-bit read, lower 4 saved
        //   second pixel uses saved 4 bits + next byte shifted

        // For simplicity, use 4 pixels packed into 6 bytes (1.5 bytes each):
        // Pixel 0 (even): read word=0x0000 → value=0x000>>4=0, nibble=0x0
        // Actually the exact algorithm from the code:
        //   Even: word = LE_read_u16 at pixelPos, pixelPos+=2
        //          nibbleCarry = word & 0x0F
        //          rawVal = (word >> 4) & 0xFFF
        //   Odd:  nextByte = data[pixelPos++]
        //          rawVal = nibbleCarry | (nextByte << 4) → & 0xFFF

        // Let's encode: pixel0=100, pixel1=200, pixel2=4095, pixel3=0
        // Even pixel0: word = (100 << 4) | nibble_for_pixel1_low
        // We need: rawVal = (word >> 4) & 0xFFF = 100, so word >> 4 = 100
        // word = 100 << 4 = 0x0640, nibbleCarry = word & 0xF = 0
        // For pixel1 (odd): rawVal = nibbleCarry | (nextByte << 4) = 0 | (nextByte << 4) = 200
        //   nextByte = 200 >> 4 = 12 → rawVal = 0 | (12 << 4) = 192 ≠ 200
        // Hmm, this packing is tricky. Let me work through it more carefully.

        // The nibbleCarry from even pixel becomes the low nibble of the odd pixel.
        // So we need: nibbleCarry | (nextByte << 4) = pixel1_value
        // nibbleCarry = (even_word) & 0xF
        // We have: even_word = (pixel0_value << 4) | (pixel1_value & 0xF)
        // And: nextByte = pixel1_value >> 4

        // pixel0=100=0x064: even_word = (0x064 << 4) | (pixel1 & 0xF)
        //   pixel1=200=0x0C8: even_word = 0x0640 | 0x8 = 0x0648
        //   nextByte = 0x0C8 >> 4 = 0x0C = 12
        //   Verify: nibbleCarry = 0x0648 & 0xF = 0x8
        //           rawVal_odd = 0x8 | (12 << 4) = 0x8 | 0xC0 = 0xC8 = 200 ✓
        //           rawVal_even = (0x0648 >> 4) & 0xFFF = 0x064 = 100 ✓

        // pixel2=4095=0xFFF, pixel3=0=0x000:
        //   even_word = (0xFFF << 4) | (0x000 & 0xF) = 0xFFF0
        //   nextByte = 0x000 >> 4 = 0
        //   Verify: rawVal_even = (0xFFF0 >> 4) & 0xFFF = 0xFFF ✓
        //           nibbleCarry = 0xFFF0 & 0xF = 0x0
        //           rawVal_odd = 0x0 | (0 << 4) = 0 ✓

        byte[] pixels = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(0), 0x0648);
        pixels[2] = 12;
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(3), 0xFFF0);
        pixels[5] = 0;

        byte[] dcm = BuildDicom(w, h, 12, 12, "MONOCHROME2", pixels);
        var frame = DicomCoder.Decode(dcm);

        await Assert.That(frame.Columns).IsEqualTo((uint)w);

        // Check that values scale correctly (12-bit max = 4095)
        ushort v0 = (ushort)frame.GetPixel(0, 0).Red;
        ushort v2 = (ushort)frame.GetPixel(2, 0).Red;

        // Pixel 2 (value 4095) should be max quantum
        await Assert.That(v2).IsEqualTo(Quantum.MaxValue);
        // Pixel 0 (value 100) should be much less
        await Assert.That(v0).IsLessThan(v2);
    }

    // --- Sequence Skipping ---

    [Test]
    public async Task Decode_WithSequence_SkipsCorrectly()
    {
        // Build a DICOM with a sequence (SQ) tag before the image tags
        var output = new List<byte>();
        output.AddRange(new byte[128]);
        output.AddRange("DICM"u8.ToArray());

        // Transfer Syntax
        string ts = "1.2.840.10008.1.2.1";
        WriteExplicitTag(output, 0x0002, 0x0010, "UI", PadUid(ts));

        // A sequence tag with some nested items (group 0x0008)
        WriteU16LE(output, 0x0008);
        WriteU16LE(output, 0x1115); // Referenced Series Sequence
        output.AddRange("SQ"u8.ToArray());
        WriteU16LE(output, 0); // reserved
        // Undefined length sequence
        WriteU32LE(output, 0xFFFFFFFF);

        // Nested item
        WriteU16LE(output, 0xFFFE);
        WriteU16LE(output, 0xE000); // Item
        WriteU32LE(output, 8); // 8 bytes of item data
        output.AddRange(new byte[8]); // dummy data

        // Sequence delimiter
        WriteU16LE(output, 0xFFFE);
        WriteU16LE(output, 0xE0DD);
        WriteU32LE(output, 0);

        // Now the actual image tags
        WriteExplicitTagU16(output, 0x0028, 0x0002, "US", 1);
        WriteExplicitTag(output, 0x0028, 0x0004, "CS", PadString("MONOCHROME2"));
        WriteExplicitTagU16(output, 0x0028, 0x0010, "US", 2); // Rows
        WriteExplicitTagU16(output, 0x0028, 0x0011, "US", 2); // Columns
        WriteExplicitTagU16(output, 0x0028, 0x0100, "US", 8);
        WriteExplicitTagU16(output, 0x0028, 0x0101, "US", 8);
        WriteExplicitTagU16(output, 0x0028, 0x0102, "US", 7);
        WriteExplicitTagU16(output, 0x0028, 0x0103, "US", 0);

        byte[] pixels = [100, 150, 200, 250];
        WriteExplicitTagBytes(output, 0x7FE0, 0x0010, "OW", pixels);

        var dcm = output.ToArray();
        var frame = DicomCoder.Decode(dcm);

        await Assert.That(frame.Columns).IsEqualTo(2u);
        await Assert.That(frame.Rows).IsEqualTo(2u);

        byte p0 = Quantum.ScaleToByte((ushort)frame.GetPixel(0, 0).Red);
        await Assert.That(p0).IsEqualTo((byte)100);
    }

    // --- CanDecode Tests ---

    [Test]
    public async Task CanDecode_TooShort_ReturnsFalse()
    {
        await Assert.That(DicomCoder.CanDecode(new byte[100])).IsFalse();
    }

    [Test]
    public async Task CanDecode_NoMagic_ReturnsFalse()
    {
        byte[] data = new byte[200];
        await Assert.That(DicomCoder.CanDecode(data)).IsFalse();
    }

    [Test]
    public async Task CanDecode_ValidMagic_ReturnsTrue()
    {
        byte[] data = new byte[200];
        data[128] = (byte)'D';
        data[129] = (byte)'I';
        data[130] = (byte)'C';
        data[131] = (byte)'M';
        await Assert.That(DicomCoder.CanDecode(data)).IsTrue();
    }

    // --- Error Handling ---

    [Test]
    public async Task Decode_InvalidMagic_Throws()
    {
        await Assert.That(() => DicomCoder.Decode(new byte[200]))
            .ThrowsExactly<InvalidDataException>();
    }

    [Test]
    public async Task Decode_ZeroDimensions_Throws()
    {
        // Build DICOM with rows=0
        byte[] dcm = BuildDicom(0, 0, 8, 8, "MONOCHROME2", []);
        await Assert.That(() => DicomCoder.Decode(dcm))
            .ThrowsExactly<InvalidDataException>();
    }

    // --- FormatRegistry Integration ---

    [Test]
    public async Task FormatRegistry_DetectsDicom()
    {
        var frame = CreateGrayscaleFrame(4, 4, 100);
        byte[] data = DicomCoder.Encode(frame);
        var format = FormatRegistry.DetectFormat(data);
        await Assert.That(format).IsEqualTo(ImageFileFormat.Dicom);
    }

    [Test]
    public async Task FormatRegistry_DcmExtension_Maps()
    {
        var format = FormatRegistry.DetectFromExtension("test.dcm");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Dicom);
    }

    [Test]
    public async Task FormatRegistry_DicomExtension_Maps()
    {
        var format = FormatRegistry.DetectFromExtension("test.dicom");
        await Assert.That(format).IsEqualTo(ImageFileFormat.Dicom);
    }

    // --- Encode Tests ---

    [Test]
    public async Task Encode_WritesValidDicomHeader()
    {
        var frame = CreateGrayscaleFrame(8, 8, 128);
        byte[] encoded = DicomCoder.Encode(frame);

        // Preamble: 128 zero bytes
        for (int i = 0; i < 128; i++)
            if (encoded[i] != 0) throw new Exception($"Preamble byte {i} is not zero");

        // Magic
        await Assert.That(encoded[128]).IsEqualTo((byte)'D');
        await Assert.That(encoded[129]).IsEqualTo((byte)'I');
        await Assert.That(encoded[130]).IsEqualTo((byte)'C');
        await Assert.That(encoded[131]).IsEqualTo((byte)'M');
    }

    [Test]
    public async Task Encode_OutputSizeIsCorrect()
    {
        int w = 16, h = 16;
        var frame = CreateGrayscaleFrame(w, h, 128);
        byte[] encoded = DicomCoder.Encode(frame);

        // Must contain at least: 128 preamble + 4 magic + tags + 16*16*2 pixel data
        int minSize = 132 + w * h * 2;
        await Assert.That(encoded.Length).IsGreaterThanOrEqualTo(minSize);
    }

    [Test]
    public async Task Encode_CanBeDecoded()
    {
        var frame = CreateGrayscaleFrame(10, 10, 180);
        byte[] encoded = DicomCoder.Encode(frame);
        await Assert.That(DicomCoder.CanDecode(encoded)).IsTrue();

        var decoded = DicomCoder.Decode(encoded);
        await Assert.That(decoded.Columns).IsEqualTo(10u);
        await Assert.That(decoded.Rows).IsEqualTo(10u);
    }

    // --- Large Image ---

    [Test]
    public async Task Roundtrip_LargeImage_Succeeds()
    {
        var frame = CreateGradientFrame(256, 256);
        byte[] encoded = DicomCoder.Encode(frame);
        var decoded = DicomCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(256u);
        await Assert.That(decoded.Rows).IsEqualTo(256u);
    }

    // --- Odd Dimensions ---

    [Test]
    public async Task Roundtrip_OddDimensions_Succeeds()
    {
        var frame = CreateGrayscaleFrame(13, 7, 200);
        byte[] encoded = DicomCoder.Encode(frame);
        var decoded = DicomCoder.Decode(encoded);

        await Assert.That(decoded.Columns).IsEqualTo(13u);
        await Assert.That(decoded.Rows).IsEqualTo(7u);
    }

    // --- RLE Round-Trip via Synthetic ---

    [Test]
    public async Task Rle_RandomData_DecodesCorrectly()
    {
        int w = 8, h = 8;
        var rng = new Random(42);
        byte[] rawPixels = new byte[w * h];
        rng.NextBytes(rawPixels);

        byte[] rleSegment = EncodeRleSegment(rawPixels);
        byte[] rleData = BuildRleSegmentData(1, [rleSegment]);

        byte[] dcm = BuildDicom(w, h, 8, 8, "MONOCHROME2", [],
            transferSyntaxUid: "1.2.840.10008.1.2.5",
            isRle: true, rlePixelData: rleData);
        var frame = DicomCoder.Decode(dcm);

        // Verify each pixel
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte expected = rawPixels[y * w + x];
                byte actual = Quantum.ScaleToByte((ushort)frame.GetPixel(x, y).Red);
                if (expected != actual)
                    throw new Exception($"Pixel ({x},{y}): expected {expected}, got {actual}");
            }
        }
    }

    [Test]
    public async Task Rle_Rgb_ThreeSegments_DecodesCorrectly()
    {
        int w = 4, h = 2;
        int totalPixels = w * h;

        // Build separate R, G, B planes
        byte[] rPlane = new byte[totalPixels];
        byte[] gPlane = new byte[totalPixels];
        byte[] bPlane = new byte[totalPixels];
        for (int i = 0; i < totalPixels; i++)
        {
            rPlane[i] = (byte)(i * 30);
            gPlane[i] = (byte)(255 - i * 30);
            bPlane[i] = 128;
        }

        byte[] rleR = EncodeRleSegment(rPlane);
        byte[] rleG = EncodeRleSegment(gPlane);
        byte[] rleB = EncodeRleSegment(bPlane);
        byte[] rleData = BuildRleSegmentData(3, [rleR, rleG, rleB]);

        byte[] dcm = BuildDicom(w, h, 8, 8, "RGB", [],
            transferSyntaxUid: "1.2.840.10008.1.2.5",
            samplesPerPixel: 3,
            isRle: true, rlePixelData: rleData);
        var frame = DicomCoder.Decode(dcm);

        // Check first pixel
        var p0 = frame.GetPixel(0, 0);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Red)).IsEqualTo(rPlane[0]);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Green)).IsEqualTo(gPlane[0]);
        await Assert.That(Quantum.ScaleToByte((ushort)p0.Blue)).IsEqualTo((byte)128);
    }
}
