using System.Runtime.CompilerServices;

namespace SharpImage.Compression;

/// <summary>
/// ITU-T T.81 Section 14 lossless JPEG decoder. Uses predictive coding with Huffman
/// entropy coding — completely different from DCT-based JPEG. Required for camera raw
/// formats: Canon CR2, Nikon NEF, Adobe DNG embed raw sensor data as lossless JPEG.
/// Supports 2-16 bit precision, predictors 1-7, and multi-component interleaving.
/// </summary>
public static class LosslessJpegDecoder
{
    private const byte MarkerSOI = 0xD8;
    private const byte MarkerEOI = 0xD9;
    private const byte MarkerSOF3 = 0xC3;
    private const byte MarkerDHT = 0xC4;
    private const byte MarkerSOS = 0xDA;
    private const byte MarkerDRI = 0xDD;

    /// <summary>
    /// Per-component frame info parsed from SOF3 and updated by SOS.
    /// </summary>
    private struct FrameComponent
    {
        public byte Id;
        public byte HorizontalSampling;
        public byte VerticalSampling;
        public int HuffmanTableIndex;
    }

    /// <summary>
    /// Decodes a lossless JPEG stream into raw sample values.
    /// Returns a flat array of ushort values in row-major order, with multi-component
    /// samples interleaved (e.g., [C0, C1, C0, C1, ...] for 2-component).
    /// </summary>
    public static ushort[] Decode(byte[] jpegData, out int width, out int height,
        out int components, out int precision)
    {
        return Decode(jpegData, 0, jpegData.Length, out width, out height, out components, out precision);
    }

    /// <summary>
    /// Decodes a lossless JPEG stream from a subsection of a byte array.
    /// </summary>
    public static ushort[] Decode(byte[] data, int offset, int length,
        out int width, out int height, out int components, out int precision)
    {
        using var stream = new MemoryStream(data, offset, length, writable: false);
        return DecodeStream(stream, out width, out height, out components, out precision);
    }

    // ─── Marker Parsing ──────────────────────────────────────────────────────

    private static ushort[] DecodeStream(MemoryStream stream, out int width, out int height,
        out int components, out int precision)
    {
        width = 0;
        height = 0;
        components = 0;
        precision = 0;

        var huffmanTables = new HuffmanTable?[4];
        var frameComponents = new FrameComponent[4];
        int frameComponentCount = 0;
        int restartInterval = 0;

        // Verify SOI marker
        int soiHigh = stream.ReadByte();
        int soiLow = stream.ReadByte();
        if (soiHigh != 0xFF || soiLow != MarkerSOI)
            throw new InvalidDataException("Not a valid JPEG stream: missing SOI marker.");

        ushort[]? output = null;

        while (stream.Position < stream.Length)
        {
            int prefix = stream.ReadByte();
            if (prefix < 0)
                break;
            if (prefix != 0xFF)
                throw new InvalidDataException(
                    $"Expected marker prefix 0xFF at position {stream.Position - 1}, got 0x{prefix:X2}.");

            // Skip any padding 0xFF bytes (JPEG allows fill bytes between markers)
            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                    throw new InvalidDataException("Unexpected end of stream while reading marker.");
            } while (marker == 0xFF);

            if (marker == MarkerEOI)
                break;

            switch (marker)
            {
                case MarkerSOF3:
                    ParseSOF3(stream, out precision, out height, out width,
                        frameComponents, out frameComponentCount);
                    components = frameComponentCount;
                    break;

                case MarkerDHT:
                    ParseDHT(stream, huffmanTables);
                    break;

                case MarkerSOS:
                {
                    if (width == 0 || height == 0)
                        throw new InvalidDataException("SOS marker encountered before SOF3.");

                    ParseSOS(stream, frameComponents, frameComponentCount,
                        out int predictor, out int pointTransform, out int[] scanComponentOrder);

                    output ??= new ushort[width * height * frameComponentCount];

                    var bitReader = new JpegBitReader(stream);
                    DecodeScanData(bitReader, output, width, height, precision,
                        frameComponentCount, predictor, pointTransform, restartInterval,
                        huffmanTables, frameComponents, scanComponentOrder);

                    // Ensure stream is positioned at the next marker for continued parsing
                    if (!bitReader.EndOfData || bitReader.LastMarker < 0)
                        AdvanceToNextMarker(stream);
                    break;
                }

                case MarkerDRI:
                    restartInterval = ParseDRI(stream);
                    break;

                default:
                    // Skip APP0-APP15, COM, and any other unknown markers
                    SkipMarkerSegment(stream);
                    break;
            }
        }

        return output ?? throw new InvalidDataException("JPEG stream ended without scan data.");
    }

    private static void ParseSOF3(Stream stream, out int precision, out int height, out int width,
        FrameComponent[] frameComponents, out int componentCount)
    {
        ReadUInt16BE(stream); // segment length
        precision = ReadByteChecked(stream);
        if (precision < 2 || precision > 16)
            throw new InvalidDataException(
                $"Unsupported lossless JPEG precision: {precision}. Must be 2-16.");

        height = ReadUInt16BE(stream);
        width = ReadUInt16BE(stream);
        componentCount = ReadByteChecked(stream);

        if (componentCount < 1 || componentCount > 4)
            throw new InvalidDataException($"Unsupported component count: {componentCount}.");

        for (int i = 0; i < componentCount; i++)
        {
            frameComponents[i].Id = (byte)ReadByteChecked(stream);
            int sampling = ReadByteChecked(stream);
            frameComponents[i].HorizontalSampling = (byte)(sampling >> 4);
            frameComponents[i].VerticalSampling = (byte)(sampling & 0x0F);
            ReadByteChecked(stream); // quantization table selector (unused in lossless)
        }
    }

    private static void ParseDHT(Stream stream, HuffmanTable?[] huffmanTables)
    {
        int segmentLength = ReadUInt16BE(stream);
        int remaining = segmentLength - 2;

        // A single DHT segment can define multiple tables
        Span<byte> codeLengthCounts = stackalloc byte[16];
        while (remaining > 0)
        {
            int tableInfo = ReadByteChecked(stream);
            remaining--;

            int tableId = tableInfo & 0x0F;
            if (tableId > 3)
                throw new InvalidDataException($"Invalid Huffman table ID: {tableId}.");
            int totalCodes = 0;
            for (int i = 0; i < 16; i++)
            {
                codeLengthCounts[i] = (byte)ReadByteChecked(stream);
                totalCodes += codeLengthCounts[i];
            }
            remaining -= 16;

            byte[] symbolValues = new byte[totalCodes];
            stream.ReadExactly(symbolValues, 0, totalCodes);
            remaining -= totalCodes;

            huffmanTables[tableId] = new HuffmanTable(codeLengthCounts, symbolValues);
        }
    }

    private static void ParseSOS(Stream stream, FrameComponent[] frameComponents,
        int frameComponentCount, out int predictor, out int pointTransform,
        out int[] scanComponentOrder)
    {
        ReadUInt16BE(stream); // segment length
        int scanComponentCount = ReadByteChecked(stream);
        scanComponentOrder = new int[scanComponentCount];

        for (int i = 0; i < scanComponentCount; i++)
        {
            int componentId = ReadByteChecked(stream);
            int tableByte = ReadByteChecked(stream);
            int dcTableId = tableByte >> 4;

            // Match this scan component to a frame component by ID
            bool matched = false;
            for (int j = 0; j < frameComponentCount; j++)
            {
                if (frameComponents[j].Id == componentId)
                {
                    frameComponents[j].HuffmanTableIndex = dcTableId;
                    scanComponentOrder[i] = j;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                throw new InvalidDataException(
                    $"SOS references component ID {componentId} not defined in SOF3.");
        }

        predictor = ReadByteChecked(stream);   // Ss = predictor selection (1-7)
        ReadByteChecked(stream);               // Se (unused in lossless mode)
        int approxByte = ReadByteChecked(stream);
        pointTransform = approxByte & 0x0F;   // Al = point transform
    }

    private static int ParseDRI(Stream stream)
    {
        ReadUInt16BE(stream); // segment length (always 4)
        return ReadUInt16BE(stream);
    }

    private static void SkipMarkerSegment(Stream stream)
    {
        int segmentLength = ReadUInt16BE(stream);
        if (segmentLength > 2)
            stream.Seek(segmentLength - 2, SeekOrigin.Current);
    }

    /// <summary>
    /// Scans forward past any remaining entropy-coded bytes to the next marker.
    /// Called after a scan completes without the bit reader encountering a terminating marker.
    /// </summary>
    private static void AdvanceToNextMarker(MemoryStream stream)
    {
        while (stream.Position < stream.Length)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return;
            if (b != 0xFF)
                continue;

            // Found 0xFF — skip fill bytes, look for real marker
            int next;
            do
            {
                next = stream.ReadByte();
                if (next < 0) return;
            } while (next == 0xFF);

            if (next != 0x00) // 0xFF 0x00 is byte-stuffed 0xFF, not a marker
            {
                stream.Seek(-2, SeekOrigin.Current);
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt16BE(Stream stream)
    {
        int high = stream.ReadByte();
        int low = stream.ReadByte();
        if (high < 0 || low < 0)
            throw new InvalidDataException("Unexpected end of stream reading uint16.");
        return (high << 8) | low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadByteChecked(Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new InvalidDataException("Unexpected end of JPEG stream.");
        return b;
    }

    // ─── Core Decode Loop ────────────────────────────────────────────────────

    private static void DecodeScanData(JpegBitReader bitReader, ushort[] output,
        int width, int height, int precision, int componentCount,
        int predictor, int pointTransform, int restartInterval,
        HuffmanTable?[] huffmanTables, FrameComponent[] frameComponents,
        int[] scanComponentOrder)
    {
        int precisionMask = (1 << precision) - 1;
        int initialPrediction = 1 << (precision - pointTransform - 1);
        int scanComponents = scanComponentOrder.Length;

        // Resolve Huffman tables up front so the hot loop avoids repeated lookups
        var scanTables = new HuffmanTable[scanComponents];
        var scanToComponent = new int[scanComponents];
        for (int sc = 0; sc < scanComponents; sc++)
        {
            int c = scanComponentOrder[sc];
            scanToComponent[sc] = c;
            scanTables[sc] = huffmanTables[frameComponents[c].HuffmanTableIndex]
                ?? throw new InvalidDataException(
                    $"Huffman table {frameComponents[c].HuffmanTableIndex} not defined for component {c}.");
        }

        // Row buffers store *unshifted* reconstructed values (before point transform)
        // so predictors operate on the correct scale
        var previousRow = new int[componentCount][];
        var currentRow = new int[componentCount][];
        for (int c = 0; c < componentCount; c++)
        {
            previousRow[c] = new int[width];
            currentRow[c] = new int[width];
        }

        int mcuCount = 0;
        bool resetPrediction = true; // True at scan start and after each restart marker

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * componentCount;

            for (int x = 0; x < width; x++)
            {
                int pixelBase = rowBase + x * componentCount;

                for (int sc = 0; sc < scanComponents; sc++)
                {
                    int c = scanToComponent[sc];

                    // Decode Huffman symbol → category (number of additional bits)
                    int category = scanTables[sc].Decode(bitReader);

                    // Read additional bits and sign-extend to get the differential value
                    int diff = 0;
                    if (category > 0)
                    {
                        if (category > 16)
                            throw new InvalidDataException(
                                $"Invalid Huffman category {category} (max 16).");
                        int rawBits = bitReader.ReadBits(category);
                        diff = JpegBitReader.Extend(rawBits, category);
                    }

                    // Compute predictor from neighboring reconstructed samples
                    int px;
                    if (resetPrediction || (y == 0 && x == 0))
                    {
                        // First sample of scan or first sample after restart marker
                        px = initialPrediction;
                    }
                    else if (y == 0)
                    {
                        // First row (after first pixel): always predict from left (Ra)
                        px = currentRow[c][x - 1];
                    }
                    else if (x == 0)
                    {
                        // First column (after first row): always predict from above (Rb)
                        px = previousRow[c][0];
                    }
                    else
                    {
                        int ra = currentRow[c][x - 1];
                        int rb = previousRow[c][x];
                        int rc = previousRow[c][x - 1];
                        px = ComputePredictor(predictor, ra, rb, rc);
                    }

                    // Reconstruct: add differential to prediction, mask to precision bits
                    int reconstructed = (px + diff) & precisionMask;
                    currentRow[c][x] = reconstructed;

                    // Apply point transform (restore full-precision value) and store
                    output[pixelBase + c] = (ushort)(reconstructed << pointTransform);

                    if (bitReader.EndOfData)
                        return; // Truncated data: remaining output stays zero-filled
                }

                // The first MCU of this interval has been decoded — resume normal prediction
                if (resetPrediction)
                    resetPrediction = false;

                // Check restart interval boundary
                if (restartInterval > 0)
                {
                    mcuCount++;
                    if (mcuCount >= restartInterval)
                    {
                        mcuCount = 0;
                        resetPrediction = true;
                        bitReader.Reset();
                    }
                }
            }

            // Swap row buffers: current becomes previous for the next row
            for (int c = 0; c < componentCount; c++)
                (previousRow[c], currentRow[c]) = (currentRow[c], previousRow[c]);
        }
    }

    /// <summary>
    /// Computes the lossless JPEG predictor from neighboring reconstructed samples.
    /// Ra = left, Rb = above, Rc = above-left (ITU-T T.81 Table H.1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputePredictor(int mode, int ra, int rb, int rc)
    {
        return mode switch
        {
            1 => ra,
            2 => rb,
            3 => rc,
            4 => ra + rb - rc,
            5 => ra + ((rb - rc) >> 1),
            6 => rb + ((ra - rc) >> 1),
            7 => (ra + rb) / 2,
            _ => ra,
        };
    }
}
