// DICOM (Digital Imaging and Communications in Medicine) format coder — read and write.
// Medical imaging standard. 128-byte preamble + "DICM" magic.
// Tag-Length-Value structure with implicit/explicit VR transfer syntaxes.
// Supports 8/12/16-bit grayscale and RGB, palette color LUT, planar configuration,
// uncompressed and RLE compressed pixel data, window/level, rescale, MONOCHROME1/2.
// Reference: ImageMagick coders/dcm.c, DICOM PS3.5 spec.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

public static class DicomCoder
{
    // Transfer syntax compression types
    private const int TransferUncompressed = 0;
    private const int TransferRle = 5;

    private struct DicomInfo
    {
        public int Rows, Columns;
        public int BitsAllocated, BitsStored, HighBit;
        public int SamplesPerPixel;
        public bool IsSigned;
        public string Photometric;
        public double WindowCenter, WindowWidth;
        public double RescaleSlope, RescaleIntercept;
        public int PixelDataOffset, PixelDataLength;
        public int NumberOfFrames;
        public bool ExplicitVr, BigEndian;
        public int PlanarConfiguration; // 0=interleaved, 1=separate planes
        public int TransferType; // 0=uncompressed, 5=RLE
        public ushort[]? RedLut, GreenLut, BlueLut;
        public int LutCount;
        public bool HasPalette;
    }

    // RLE decompression state machine per DICOM PS3.5 Annex G
    private struct RleState
    {
        public int Position;        // current read position in data
        public int Remaining;       // bytes remaining in segment
        public int RunCount;        // remaining bytes in current run
        public int RunByte;         // byte to repeat (-1 = literal mode)

        public static RleState Create(int startPos, int segmentLength)
        {
            return new RleState
            {
                Position = startPos,
                Remaining = segmentLength,
                RunCount = 0,
                RunByte = -1
            };
        }
    }

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132)
            return false;
        return data[128] == (byte)'D' && data[129] == (byte)'I'
            && data[130] == (byte)'C' && data[131] == (byte)'M';
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
            throw new InvalidDataException("Not a valid DICOM file");

        var info = ParseTags(data);

        if (info.Rows <= 0 || info.Columns <= 0)
            throw new InvalidDataException($"Invalid DICOM dimensions: {info.Columns}x{info.Rows}");
        if (info.PixelDataOffset < 0)
            throw new InvalidDataException("No pixel data found in DICOM file");

        bool isRgb = info.SamplesPerPixel >= 3 || info.Photometric.Contains("RGB");
        bool isPalette = info.HasPalette && info.Photometric.Contains("PALETTE");
        var frame = new ImageFrame();
        frame.Initialize(info.Columns, info.Rows, ColorspaceType.SRGB, false);

        if (info.TransferType == TransferRle)
            ReadPixelsRle(data, ref info, frame, isRgb, isPalette);
        else if (info.BitsAllocated == 12 && info.BitsStored == 12)
            ReadPixels12Bit(data, ref info, frame);
        else if (isRgb && info.PlanarConfiguration == 1)
            ReadPixelsPlanar(data, ref info, frame);
        else
            ReadPixelsUncompressed(data, ref info, frame, isRgb, isPalette);

        return frame;
    }

    #region Tag Parsing

    private static DicomInfo ParseTags(byte[] data)
    {
        var info = new DicomInfo
        {
            BitsAllocated = 16, BitsStored = 16, HighBit = 15,
            SamplesPerPixel = 1, Photometric = "MONOCHROME2",
            WindowCenter = double.NaN, WindowWidth = double.NaN,
            RescaleSlope = 1.0, RescaleIntercept = 0.0,
            PixelDataOffset = -1, NumberOfFrames = 1,
            ExplicitVr = true, BigEndian = false,
            TransferType = TransferUncompressed
        };

        int pos = 132;
        int sequenceDepth = 0;
        bool explicitFileDetected = false;

        while (pos + 4 <= data.Length)
        {
            ushort group = ReadU16(data, pos, info.BigEndian);
            ushort element = ReadU16(data, pos + 2, info.BigEndian);
            pos += 4;

            // Sequence delimiter — exit sequence level
            if (group == 0xFFFE)
            {
                if (element == 0xE0DD && sequenceDepth > 0)
                    sequenceDepth--;
                // Item or item delimiters: read 4-byte length and skip
                if (pos + 4 <= data.Length)
                {
                    int itemLen = (int)ReadU32(data, pos, false);
                    pos += 4;
                    if (element == 0xE000 && itemLen > 0 && itemLen != unchecked((int)0xFFFFFFFF))
                        pos += itemLen;
                }
                continue;
            }

            // End-of-file markers
            if ((group == 0xFFFC && element == 0xFFFC) || (group == 0x0000 && element == 0x0000 && pos > 140))
                break;

            // Auto-detect implicit VR for non-meta groups
            if (group > 0x0002 && !explicitFileDetected)
            {
                explicitFileDetected = true;
                if (pos + 2 <= data.Length)
                {
                    byte v0 = data[pos], v1 = data[pos + 1];
                    bool looksExplicit = v0 >= (byte)'A' && v0 <= (byte)'Z'
                                      && v1 >= (byte)'A' && v1 <= (byte)'Z';
                    if (!looksExplicit)
                        info.ExplicitVr = false;
                }
            }

            int valueLength;
            string vr = "";

            // Group 0x0002 (file meta) is always explicit VR LE
            bool useExplicit = (group == 0x0002) || info.ExplicitVr;

            if (useExplicit)
            {
                if (pos + 2 > data.Length) break;
                vr = Encoding.ASCII.GetString(data, pos, 2);
                pos += 2;

                if (vr is "OB" or "OD" or "OF" or "OL" or "OW" or "SQ" or "UC" or "UN" or "UR" or "UT")
                {
                    pos += 2; // reserved bytes
                    if (pos + 4 > data.Length) break;
                    valueLength = (int)ReadU32(data, pos, info.BigEndian);
                    pos += 4;
                }
                else
                {
                    if (pos + 2 > data.Length) break;
                    valueLength = ReadU16(data, pos, info.BigEndian);
                    pos += 2;
                }
            }
            else
            {
                if (pos + 4 > data.Length) break;
                valueLength = (int)ReadU32(data, pos, info.BigEndian);
                pos += 4;
            }

            // Undefined length
            if (valueLength < 0 || valueLength == unchecked((int)0xFFFFFFFF))
            {
                if (vr == "SQ")
                {
                    sequenceDepth++;
                    continue;
                }
                if (group == 0x7FE0 && element == 0x0010)
                {
                    info.PixelDataOffset = pos;
                    info.PixelDataLength = data.Length - pos;
                    break;
                }
                continue;
            }

            if (pos + valueLength > data.Length) break;

            // Skip tags inside sequences
            if (sequenceDepth > 0)
            {
                if (vr == "SQ") sequenceDepth++;
                pos += valueLength;
                continue;
            }

            uint tag = ((uint)group << 16) | element;
            switch (tag)
            {
                case 0x00020010: // Transfer Syntax UID
                    string ts = GetString(data, pos, valueLength);
                    info.ExplicitVr = ts != "1.2.840.10008.1.2";
                    info.BigEndian = ts.Contains("1.2.840.10008.1.2.2");
                    if (ts.Contains("1.2.840.10008.1.2.5"))
                        info.TransferType = TransferRle;
                    break;
                case 0x00280002: info.SamplesPerPixel = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280004: info.Photometric = GetString(data, pos, valueLength); break;
                case 0x00280006: info.PlanarConfiguration = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280008:
                    if (int.TryParse(GetString(data, pos, valueLength), out int nf))
                        info.NumberOfFrames = nf;
                    break;
                case 0x00280010: info.Rows = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280011: info.Columns = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280100: info.BitsAllocated = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280101: info.BitsStored = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280102: info.HighBit = ReadU16(data, pos, info.BigEndian); break;
                case 0x00280103: info.IsSigned = ReadU16(data, pos, info.BigEndian) == 1; break;
                case 0x00281050: TryParseDouble(data, pos, valueLength, ref info.WindowCenter); break;
                case 0x00281051: TryParseDouble(data, pos, valueLength, ref info.WindowWidth); break;
                case 0x00281052: TryParseDouble(data, pos, valueLength, ref info.RescaleIntercept); break;
                case 0x00281053: TryParseDouble(data, pos, valueLength, ref info.RescaleSlope); break;
                case 0x00281201: // Red Palette Color LUT Data
                    info.RedLut = LoadLut(data, pos, valueLength, info.BigEndian);
                    info.HasPalette = true;
                    break;
                case 0x00281202: // Green Palette Color LUT Data
                    info.GreenLut = LoadLut(data, pos, valueLength, info.BigEndian);
                    break;
                case 0x00281203: // Blue Palette Color LUT Data
                    info.BlueLut = LoadLut(data, pos, valueLength, info.BigEndian);
                    break;
                case 0x7FE00010: // Pixel Data
                    info.PixelDataOffset = pos;
                    info.PixelDataLength = valueLength;
                    break;
            }

            pos += valueLength;
            if (info.PixelDataOffset >= 0 && tag == 0x7FE00010)
                break;
        }

        if (info.HasPalette)
        {
            int lutLen = info.RedLut?.Length ?? 0;
            info.LutCount = Math.Max(lutLen, Math.Max(info.GreenLut?.Length ?? 0, info.BlueLut?.Length ?? 0));
        }

        return info;
    }

    private static void TryParseDouble(byte[] data, int pos, int length, ref double target)
    {
        if (double.TryParse(GetString(data, pos, length),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v))
            target = v;
    }

    private static ushort[] LoadLut(byte[] data, int offset, int length, bool bigEndian)
    {
        int count = length / 2;
        var lut = new ushort[count];
        for (int i = 0; i < count; i++)
            lut[i] = ReadU16(data, offset + i * 2, bigEndian);
        return lut;
    }

    #endregion

    #region Pixel Reading — Uncompressed

    private static void ReadPixelsUncompressed(byte[] data, ref DicomInfo info,
        ImageFrame frame, bool isRgb, bool isPalette)
    {
        int channels = frame.NumberOfChannels;
        int bytesPerSample = Math.Max(info.BitsAllocated / 8, 1);
        int mask = (1 << info.BitsStored) - 1;
        bool hasWindow = !double.IsNaN(info.WindowCenter) && !double.IsNaN(info.WindowWidth) && info.WindowWidth > 0;
        int pixelPos = info.PixelDataOffset;

        for (int y = 0; y < info.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < info.Columns; x++)
            {
                int offset = x * channels;

                if (isRgb)
                {
                    for (int c = 0; c < Math.Min(info.SamplesPerPixel, channels); c++)
                    {
                        int rawVal = ReadSample(data, pixelPos, bytesPerSample, info.BigEndian, info.IsSigned);
                        pixelPos += bytesPerSample;
                        row[offset + c] = ScaleToQuantum(rawVal & mask, info.BitsStored);
                    }
                    if (info.SamplesPerPixel > channels)
                        pixelPos += (info.SamplesPerPixel - channels) * bytesPerSample;
                }
                else if (isPalette)
                {
                    int rawVal = ReadSample(data, pixelPos, bytesPerSample, info.BigEndian, info.IsSigned) & mask;
                    pixelPos += bytesPerSample;
                    ApplyPalette(ref info, rawVal, row, offset, channels);
                }
                else
                {
                    int rawVal = ReadSample(data, pixelPos, bytesPerSample, info.BigEndian, info.IsSigned);
                    pixelPos += bytesPerSample;
                    ApplyGrayscale(ref info, rawVal & mask, row, offset, channels, hasWindow);
                }
            }
        }
    }

    #endregion

    #region Pixel Reading — Planar Configuration

    private static void ReadPixelsPlanar(byte[] data, ref DicomInfo info, ImageFrame frame)
    {
        int channels = frame.NumberOfChannels;
        int bytesPerSample = Math.Max(info.BitsAllocated / 8, 1);
        int mask = (1 << info.BitsStored) - 1;
        int pixelPos = info.PixelDataOffset;

        // Each color plane stored separately: all R, then all G, then all B
        for (int plane = 0; plane < Math.Min(info.SamplesPerPixel, channels); plane++)
        {
            for (int y = 0; y < info.Rows; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < info.Columns; x++)
                {
                    int rawVal = ReadSample(data, pixelPos, bytesPerSample, info.BigEndian, info.IsSigned);
                    pixelPos += bytesPerSample;
                    row[x * channels + plane] = ScaleToQuantum(rawVal & mask, info.BitsStored);
                }
            }
        }
    }

    #endregion

    #region Pixel Reading — 12-Bit Packed

    private static void ReadPixels12Bit(byte[] data, ref DicomInfo info, ImageFrame frame)
    {
        int channels = frame.NumberOfChannels;
        int mask = (1 << info.BitsStored) - 1;
        bool hasWindow = !double.IsNaN(info.WindowCenter) && !double.IsNaN(info.WindowWidth) && info.WindowWidth > 0;
        int pixelPos = info.PixelDataOffset;
        int nibbleCarry = 0;
        int pixelIndex = 0;

        for (int y = 0; y < info.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < info.Columns; x++)
            {
                int rawVal;
                if ((pixelIndex & 1) == 0)
                {
                    // Even pixel: read 2 bytes, extract upper 12 bits, save lower 4
                    if (pixelPos + 2 > data.Length) break;
                    int word = info.BigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pixelPos))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pixelPos));
                    pixelPos += 2;
                    nibbleCarry = word & 0x0F;
                    rawVal = (word >> 4) & 0xFFF;
                }
                else
                {
                    // Odd pixel: combine saved nibble with next byte
                    if (pixelPos >= data.Length) break;
                    int nextByte = data[pixelPos++];
                    rawVal = nibbleCarry | (nextByte << 4);
                    rawVal &= 0xFFF;
                }
                pixelIndex++;

                int offset = x * channels;
                ApplyGrayscale(ref info, rawVal & mask, row, offset, channels, hasWindow);
            }
        }
    }

    #endregion

    #region Pixel Reading — RLE Compressed

    private static void ReadPixelsRle(byte[] data, ref DicomInfo info,
        ImageFrame frame, bool isRgb, bool isPalette)
    {
        int channels = frame.NumberOfChannels;
        int mask = (1 << info.BitsStored) - 1;
        bool hasWindow = !double.IsNaN(info.WindowCenter) && !double.IsNaN(info.WindowWidth) && info.WindowWidth > 0;
        int bytesPerSample = Math.Max(info.BitsAllocated / 8, 1);

        // Encapsulated pixel data: skip offset table item
        int pos = info.PixelDataOffset;
        if (pos + 8 <= data.Length)
        {
            ushort itemGroup = ReadU16(data, pos, false);
            ushort itemElement = ReadU16(data, pos + 2, false);
            if (itemGroup == 0xFFFE && itemElement == 0xE000)
            {
                int offsetTableLen = (int)ReadU32(data, pos + 4, false);
                pos += 8 + offsetTableLen;
            }
        }

        // Read RLE frame item header
        if (pos + 8 > data.Length)
            throw new InvalidDataException("Missing RLE frame item");

        ushort fGroup = ReadU16(data, pos, false);
        ushort fElement = ReadU16(data, pos + 2, false);
        int frameItemLen = (int)ReadU32(data, pos + 4, false);
        pos += 8;

        if (fGroup != 0xFFFE || fElement != 0xE000)
            throw new InvalidDataException("Expected RLE frame item tag");

        // RLE segment header: 16 uint32 values (segment count + 15 segment offsets)
        if (pos + 64 > data.Length)
            throw new InvalidDataException("RLE segment header too short");

        int segmentCount = (int)ReadU32(data, pos, false);
        var segmentOffsets = new int[15];
        for (int i = 0; i < 15; i++)
            segmentOffsets[i] = (int)ReadU32(data, pos + 4 + i * 4, false);
        int rleDataBase = pos; // base for segment offsets

        // Determine how many segments we need
        int expectedSegments = info.SamplesPerPixel * bytesPerSample;
        segmentCount = Math.Min(segmentCount, expectedSegments);

        // Decode each segment into a byte plane
        int totalPixels = info.Rows * info.Columns;
        var segmentData = new byte[segmentCount][];

        for (int s = 0; s < segmentCount; s++)
        {
            int segStart = rleDataBase + segmentOffsets[s];
            int segEnd = (s + 1 < segmentCount && segmentOffsets[s + 1] > 0)
                ? rleDataBase + segmentOffsets[s + 1]
                : rleDataBase + (frameItemLen > 0 ? frameItemLen : data.Length - rleDataBase);

            segmentData[s] = DecodeRleSegment(data, segStart, segEnd - segStart, totalPixels);
        }

        // Assemble pixels from segments
        for (int y = 0; y < info.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < info.Columns; x++)
            {
                int pixelIdx = y * info.Columns + x;
                int offset = x * channels;

                if (isRgb)
                {
                    for (int c = 0; c < Math.Min(info.SamplesPerPixel, channels); c++)
                    {
                        int rawVal;
                        if (bytesPerSample == 2)
                        {
                            int hiSeg = c * 2, loSeg = c * 2 + 1;
                            int hi = hiSeg < segmentCount ? segmentData[hiSeg][pixelIdx] : 0;
                            int lo = loSeg < segmentCount ? segmentData[loSeg][pixelIdx] : 0;
                            rawVal = (hi << 8) | lo;
                        }
                        else
                        {
                            rawVal = c < segmentCount ? segmentData[c][pixelIdx] : 0;
                        }
                        row[offset + c] = ScaleToQuantum(rawVal & mask, info.BitsStored);
                    }
                }
                else if (isPalette)
                {
                    int rawVal;
                    if (bytesPerSample == 2 && segmentCount >= 2)
                        rawVal = (segmentData[0][pixelIdx] << 8) | segmentData[1][pixelIdx];
                    else
                        rawVal = segmentData[0][pixelIdx];
                    ApplyPalette(ref info, rawVal & mask, row, offset, channels);
                }
                else
                {
                    int rawVal;
                    if (bytesPerSample == 2 && segmentCount >= 2)
                        rawVal = (segmentData[0][pixelIdx] << 8) | segmentData[1][pixelIdx];
                    else
                        rawVal = segmentData[0][pixelIdx];
                    ApplyGrayscale(ref info, rawVal & mask, row, offset, channels, hasWindow);
                }
            }
        }
    }

    private static byte[] DecodeRleSegment(byte[] data, int start, int segmentLength, int outputSize)
    {
        var output = new byte[outputSize];
        int readPos = start;
        int writePos = 0;
        int end = start + segmentLength;

        while (readPos < end && writePos < outputSize)
        {
            if (readPos >= data.Length) break;
            sbyte header = (sbyte)data[readPos++];

            if (header >= 0)
            {
                // Literal run: copy header+1 bytes
                int count = header + 1;
                for (int i = 0; i < count && writePos < outputSize && readPos < end; i++)
                    output[writePos++] = data[readPos++];
            }
            else if (header > -128)
            {
                // Repeat run: repeat next byte (1-header) times
                int count = 1 - header;
                byte value = readPos < data.Length ? data[readPos++] : (byte)0;
                for (int i = 0; i < count && writePos < outputSize; i++)
                    output[writePos++] = value;
            }
            // header == -128 (0x80): no-op
        }

        return output;
    }

    #endregion

    #region Pixel Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyPalette(ref DicomInfo info, int index, Span<ushort> row, int offset, int channels)
    {
        if (info.RedLut != null && info.GreenLut != null && info.BlueLut != null)
        {
            int safeIdx = Math.Clamp(index, 0, info.LutCount - 1);
            row[offset] = info.RedLut[safeIdx];
            if (channels > 1) row[offset + 1] = info.GreenLut[safeIdx];
            if (channels > 2) row[offset + 2] = info.BlueLut[safeIdx];
        }
        else
        {
            ushort q = ScaleToQuantum(index, info.BitsStored);
            row[offset] = q;
            if (channels > 1) row[offset + 1] = q;
            if (channels > 2) row[offset + 2] = q;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGrayscale(ref DicomInfo info, int rawVal, Span<ushort> row,
        int offset, int channels, bool hasWindow)
    {
        double physVal = info.RescaleSlope * rawVal + info.RescaleIntercept;
        ushort quantum;

        if (hasWindow)
        {
            double lo = info.WindowCenter - info.WindowWidth / 2.0;
            double hi = info.WindowCenter + info.WindowWidth / 2.0;
            double normalized = Math.Clamp((physVal - lo) / (hi - lo), 0, 1);
            quantum = (ushort)(normalized * Quantum.MaxValue);
        }
        else
        {
            quantum = ScaleToQuantum((int)physVal, info.BitsStored);
        }

        if (info.Photometric.Contains("MONOCHROME1"))
            quantum = (ushort)(Quantum.MaxValue - quantum);

        row[offset] = quantum;
        if (channels > 1) row[offset + 1] = quantum;
        if (channels > 2) row[offset + 2] = quantum;
    }

    #endregion

    #region Encoder

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;

        // Write as 16-bit grayscale DICOM (MONOCHROME2), Explicit VR LE
        int pixelDataSize = w * h * 2;
        var output = new List<byte>(132 + 512 + pixelDataSize);

        // 128-byte preamble (zeros) + "DICM" magic
        output.AddRange(new byte[128]);
        output.AddRange("DICM"u8.ToArray());

        // Meta information group (0002) — always explicit VR little-endian
        string transferSyntax = "1.2.840.10008.1.2.1";
        WriteTag(output, 0x0002, 0x0010, "UI", Encoding.ASCII.GetBytes(PadUid(transferSyntax)));

        // Image tags
        WriteTagU16(output, 0x0028, 0x0002, "US", 1);          // Samples Per Pixel
        WriteTag(output, 0x0028, 0x0004, "CS",
            Encoding.ASCII.GetBytes(PadString("MONOCHROME2")));  // Photometric
        WriteTagU16(output, 0x0028, 0x0010, "US", (ushort)h);  // Rows
        WriteTagU16(output, 0x0028, 0x0011, "US", (ushort)w);  // Columns
        WriteTagU16(output, 0x0028, 0x0100, "US", 16);         // Bits Allocated
        WriteTagU16(output, 0x0028, 0x0101, "US", 16);         // Bits Stored
        WriteTagU16(output, 0x0028, 0x0102, "US", 15);         // High Bit
        WriteTagU16(output, 0x0028, 0x0103, "US", 0);          // Pixel Representation (unsigned)

        // Pixel data
        WritePixelDataTag(output, 0x7FE0, 0x0010, "OW", pixelDataSize);

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int srcOffset = x * imgChannels;
                ushort r = row[srcOffset];
                ushort g = imgChannels > 1 ? row[srcOffset + 1] : r;
                ushort b = imgChannels > 2 ? row[srcOffset + 2] : r;
                ushort gray = (ushort)(0.299 * r + 0.587 * g + 0.114 * b);
                output.Add((byte)(gray & 0xFF));
                output.Add((byte)(gray >> 8));
            }
        }

        return output.ToArray();
    }

    #endregion

    #region Tag Writing Helpers

    private static void WriteTag(List<byte> output, ushort group, ushort element, string vr, byte[] value)
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

    private static void WriteTagU16(List<byte> output, ushort group, ushort element, string vr, ushort value)
    {
        byte[] val = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(val, value);
        WriteTag(output, group, element, vr, val);
    }

    private static void WritePixelDataTag(List<byte> output, ushort group, ushort element, string vr, int dataLength)
    {
        WriteU16LE(output, group);
        WriteU16LE(output, element);
        output.AddRange(Encoding.ASCII.GetBytes(vr));
        WriteU16LE(output, 0);
        WriteU32LE(output, (uint)dataLength);
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

    #region Read Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadU16(byte[] data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(byte[] data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static string GetString(byte[] data, int offset, int length)
    {
        if (offset + length > data.Length) return "";
        return Encoding.ASCII.GetString(data, offset, length).Trim('\0', ' ');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSample(byte[] data, int pos, int bytesPerSample, bool bigEndian, bool isSigned)
    {
        if (pos + bytesPerSample > data.Length) return 0;

        if (bytesPerSample == 1)
            return isSigned ? (sbyte)data[pos] : data[pos];

        if (bytesPerSample == 2)
        {
            return isSigned
                ? bigEndian ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos))
                            : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos))
                : bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
                            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        }

        if (bytesPerSample == 4)
        {
            return bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleToQuantum(int value, int bitsStored)
    {
        if (value < 0) value = 0;
        int maxVal = (1 << bitsStored) - 1;
        if (value > maxVal) value = maxVal;
        return (ushort)((long)value * Quantum.MaxValue / maxVal);
    }

    #endregion
}
