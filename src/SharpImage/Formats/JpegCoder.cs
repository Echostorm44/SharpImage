using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Metadata;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Chroma subsampling mode for JPEG encoding.
/// </summary>
public enum JpegSubsampling
{
    /// <summary>No chroma subsampling. Full resolution for all channels. Best quality, largest files.</summary>
    Yuv444 = 0,
    /// <summary>Horizontal 2:1 chroma subsampling. Good balance of quality and size.</summary>
    Yuv422 = 1,
    /// <summary>Horizontal and vertical 2:1 chroma subsampling. Smallest files, standard for photos.</summary>
    Yuv420 = 2,
}

/// <summary>
/// Pure C# JPEG reader/writer (ITU-T T.81 / ISO 10918-1). Supports: SOF0 baseline DCT, SOF2 progressive DCT,
/// Huffman coding, YCbCr/grayscale, 4:4:4/4:2:2/4:2:0 subsampling, optimized Huffman tables.
/// Does not support: arithmetic coding, lossless, JPEG2000.
/// </summary>
public static class JpegCoder
{
    // JPEG markers
    private const byte MarkerPrefix = 0xFF;
    private const byte SOI = 0xD8; // Start of Image

    /// <summary>
    /// Detect JPEG format by SOI marker.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;

    private const byte EOI = 0xD9; // End of Image
    private const byte SOF0 = 0xC0; // Baseline DCT
    private const byte SOF2 = 0xC2; // Progressive DCT
    private const byte DHT = 0xC4; // Define Huffman Table
    private const byte DQT = 0xDB; // Define Quantization Table
    private const byte DRI = 0xDD; // Define Restart Interval
    private const byte SOS = 0xDA; // Start of Scan
    private const byte APP0 = 0xE0; // JFIF
    private const byte APP1 = 0xE1; // EXIF / XMP
    private const byte APP2 = 0xE2; // ICC Profile
    private const byte APP13 = 0xED; // IPTC / Photoshop
    private const byte COM = 0xFE; // Comment

    private const int MaxComponents = 4;
    private const int BlockSize = 8;

    #region Read

    public static ImageFrame Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        return Read(stream);
    }

    public static ImageFrame Read(Stream stream)
    {
        // Verify SOI marker
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != SOI)
        {
            throw new InvalidDataException("Not a valid JPEG file (missing SOI marker).");
        }

        // Image parameters (filled from markers)
        int width = 0, height = 0;
        int componentCount = 0;
        int restartInterval = 0;
        bool isProgressive = false;

        // Component info
        var components = new JpegComponent[MaxComponents];
        int maxHSample = 1, maxVSample = 1;

        // Quantization tables (up to 4)
        var quantTables = new int[4][];

        // Huffman tables (DC and AC, up to 4 each)
        var dcTables = new HuffmanTable[4];
        var acTables = new HuffmanTable[4];

        // For progressive: blocks are allocated once and filled by multiple scans
        bool blocksAllocated = false;
        int mcuCols = 0, mcuRows = 0;

        // Metadata segments collected during marker parsing
        byte[]? exifData = null;
        List<byte[]>? iccChunks = null;
        byte[]? iptcData = null;
        string? xmpData = null;

        // Parse all markers
        while (true)
        {
            int marker = ReadMarker(stream);
            if (marker < 0 || marker == EOI)
            {
                break;
            }

            switch (marker)
            {
                case SOF0: // Baseline DCT
                    ReadSof(stream, ref width, ref height, ref componentCount, components,
                        ref maxHSample, ref maxVSample);
                    break;

                case SOF2: // Progressive DCT
                    isProgressive = true;
                    ReadSof(stream, ref width, ref height, ref componentCount, components,
                        ref maxHSample, ref maxVSample);
                    break;

                case DHT:
                    ReadDht(stream, dcTables, acTables);
                    break;

                case DQT:
                    ReadDqt(stream, quantTables);
                    break;

                case DRI:
                    ReadDri(stream, ref restartInterval);
                    break;

                case SOS:
                    if (!isProgressive)
                    {
                        // Baseline: single scan, return immediately
                        ReadSosHeader(stream, components, componentCount);
                        var frame = DecodeScanData(stream, width, height, componentCount, components,
                            quantTables, dcTables, acTables, maxHSample, maxVSample, restartInterval);
                        AttachMetadata(frame, exifData, iccChunks, iptcData, xmpData);
                        return frame;
                    }

                    // Progressive: allocate blocks once, then decode each scan
                    if (!blocksAllocated)
                    {
                        int mcuWidth = maxHSample * BlockSize;
                        int mcuHeight = maxVSample * BlockSize;
                        mcuCols = (width + mcuWidth - 1) / mcuWidth;
                        mcuRows = (height + mcuHeight - 1) / mcuHeight;

                        for (int c = 0; c < componentCount; c++)
                        {
                            int blocksH = mcuCols * components[c].HSample;
                            int blocksV = mcuRows * components[c].VSample;
                            components[c].Blocks = new int[blocksV * blocksH][];
                            for (int i = 0; i < components[c].Blocks.Length; i++)
                            {
                                components[c].Blocks[i] = new int[64];
                            }
                        }
                        blocksAllocated = true;
                    }

                    var scanInfo = ReadSosHeaderProgressive(stream, components, componentCount);
                    DecodeProgressiveScan(stream, componentCount, components,
                        dcTables, acTables, mcuCols, mcuRows,
                        maxHSample, maxVSample, restartInterval, scanInfo);
                    break;

                default:
                    // Capture metadata from APP markers before skipping
                    ReadOrSkipAppMarker(stream, marker, ref exifData, ref iccChunks, ref iptcData, ref xmpData);
                    break;
            }
        }

        if (isProgressive && blocksAllocated)
        {
            // All scans decoded — dequantize, IDCT, and convert to pixels
            for (int c = 0; c < componentCount; c++)
            {
                var qt = quantTables[components[c].QuantTableIndex];
                foreach (var block in components[c].Blocks)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] *= qt[i];
                    }
                    Dct.InverseDct(block);
                }
            }
            var progFrame = BlocksToImage(width, height, componentCount, components, maxHSample, maxVSample, mcuCols);
            AttachMetadata(progFrame, exifData, iccChunks, iptcData, xmpData);
            return progFrame;
        }

        throw new InvalidDataException("JPEG missing SOS marker.");
    }

    private static void ReadSof(Stream stream, ref int width, ref int height,
        ref int componentCount, JpegComponent[] components, ref int maxH, ref int maxV)
    {
        int length = ReadUInt16(stream);
        int precision = stream.ReadByte(); // Usually 8
        if (precision != 8)
        {
            throw new NotSupportedException($"JPEG bit depth {precision} not supported (only 8-bit baseline).");
        }

        height = ReadUInt16(stream);
        width = ReadUInt16(stream);
        componentCount = stream.ReadByte();

        if (componentCount < 1 || componentCount > MaxComponents)
        {
            throw new InvalidDataException($"Invalid JPEG component count: {componentCount}");
        }

        for (int i = 0;i < componentCount;i++)
        {
            components[i].Id = (byte)stream.ReadByte();
            int sampling = stream.ReadByte();
            components[i].HSample = (byte)(sampling >> 4);
            components[i].VSample = (byte)(sampling & 0xF);
            components[i].QuantTableIndex = (byte)stream.ReadByte();

            if (components[i].HSample > maxH)
            {
                maxH = components[i].HSample;
            }

            if (components[i].VSample > maxV)
            {
                maxV = components[i].VSample;
            }
        }
    }

    private static void ReadDht(Stream stream, HuffmanTable[] dcTables, HuffmanTable[] acTables)
    {
        int length = ReadUInt16(stream) - 2;
        Span<byte> bits = stackalloc byte[16];
        while (length > 0)
        {
            int info = stream.ReadByte();
            length--;
            int tableClass = info >> 4;   // 0 = DC, 1 = AC
            int tableIndex = info & 0x0F; // 0-3
            stream.ReadExactly(bits);
            length -= 16;

            int totalValues = 0;
            for (int i = 0;i < 16;i++)
            {
                totalValues += bits[i];
            }

            byte[] values = new byte[totalValues];
            stream.ReadExactly(values);
            length -= totalValues;

            var table = new HuffmanTable(bits, values);
            if (tableClass == 0)
            {
                dcTables[tableIndex] = table;
            }
            else
            {
                acTables[tableIndex] = table;
            }
        }
    }

    private static void ReadDqt(Stream stream, int[][] quantTables)
    {
        int length = ReadUInt16(stream) - 2;
        while (length > 0)
        {
            int info = stream.ReadByte();
            length--;
            int precision = info >> 4;    // 0 = 8-bit, 1 = 16-bit
            int tableIndex = info & 0x0F;

            int[] table = new int[64];
            if (precision == 0)
            {
                // DQT data is in zigzag order — convert to natural (row-major) order
                for (int i = 0;i < 64;i++)
                {
                    table[JpegTables.NaturalOrder[i]] = stream.ReadByte();
                }

                length -= 64;
            }
            else
            {
                for (int i = 0;i < 64;i++)
                {
                    table[JpegTables.NaturalOrder[i]] = ReadUInt16(stream);
                }

                length -= 128;
            }
            quantTables[tableIndex] = table;
        }
    }

    private static void ReadDri(Stream stream, ref int restartInterval)
    {
        ReadUInt16(stream); // length (always 4)
        restartInterval = ReadUInt16(stream);
    }

    private static void ReadSosHeader(Stream stream, JpegComponent[] components, int componentCount)
    {
        int length = ReadUInt16(stream);
        int scanComponents = stream.ReadByte();

        for (int i = 0;i < scanComponents;i++)
        {
            int id = stream.ReadByte();
            int tableSelector = stream.ReadByte();

            // Find matching component
            for (int c = 0;c < componentCount;c++)
            {
                if (components[c].Id == id)
                {
                    components[c].DcTableIndex = (byte)(tableSelector >> 4);
                    components[c].AcTableIndex = (byte)(tableSelector & 0xF);
                    break;
                }
            }
        }

        // Skip spectral selection and successive approximation (baseline ignores these)
        stream.ReadByte(); // Ss (start of spectral selection)
        stream.ReadByte(); // Se (end of spectral selection)
        stream.ReadByte(); // Ah/Al (successive approximation)
    }

    /// <summary>
    /// Progressive scan parameters parsed from SOS header.
    /// </summary>
    private struct ScanInfo
    {
        public int[] ComponentIndices; // Which components are in this scan
        public int ComponentCount;
        public int Ss; // Spectral selection start (0 = DC)
        public int Se; // Spectral selection end (63 = all AC)
        public int Ah; // Successive approximation high bit (previous)
        public int Al; // Successive approximation low bit (current)
    }

    private static ScanInfo ReadSosHeaderProgressive(Stream stream, JpegComponent[] components, int componentCount)
    {
        int length = ReadUInt16(stream);
        int scanComponents = stream.ReadByte();

        var scanInfo = new ScanInfo
        {
            ComponentIndices = new int[scanComponents],
            ComponentCount = scanComponents,
        };

        for (int i = 0; i < scanComponents; i++)
        {
            int id = stream.ReadByte();
            int tableSelector = stream.ReadByte();

            for (int c = 0; c < componentCount; c++)
            {
                if (components[c].Id == id)
                {
                    components[c].DcTableIndex = (byte)(tableSelector >> 4);
                    components[c].AcTableIndex = (byte)(tableSelector & 0xF);
                    scanInfo.ComponentIndices[i] = c;
                    break;
                }
            }
        }

        scanInfo.Ss = stream.ReadByte();
        scanInfo.Se = stream.ReadByte();
        int approx = stream.ReadByte();
        scanInfo.Ah = approx >> 4;
        scanInfo.Al = approx & 0xF;

        return scanInfo;
    }

    private static void DecodeProgressiveScan(Stream stream, int componentCount,
        JpegComponent[] components, HuffmanTable[] dcTables, HuffmanTable[] acTables,
        int mcuCols, int mcuRows, int maxHSample, int maxVSample,
        int restartInterval, ScanInfo scan)
    {
        var bitReader = new JpegBitReader(stream);
        int[] dcPredictors = new int[componentCount];
        int mcuCount = 0;
        int eobRun = 0; // End-of-band run counter for progressive AC

        bool isDcScan = scan.Ss == 0;
        bool isFirstScan = scan.Ah == 0;

        if (isDcScan)
        {
            // DC scan (Ss=0, Se=0): interleaved or single-component
            for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
            {
                for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
                {
                    if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                    {
                        bitReader.Reset();
                        Array.Clear(dcPredictors);
                    }

                    for (int si = 0; si < scan.ComponentCount; si++)
                    {
                        int c = scan.ComponentIndices[si];
                        int blocksH = components[c].HSample;
                        int blocksV = components[c].VSample;

                        for (int bv = 0; bv < blocksV; bv++)
                        {
                            for (int bh = 0; bh < blocksH; bh++)
                            {
                                int blockCol = mcuCol * blocksH + bh;
                                int blockRow = mcuRow * blocksV + bv;
                                int blockIndex = blockRow * (mcuCols * blocksH) + blockCol;
                                var block = components[c].Blocks[blockIndex];

                                if (isFirstScan)
                                {
                                    // First DC scan: decode DC coefficient
                                    byte dcCategory = dcTables[components[c].DcTableIndex].Decode(bitReader);
                                    int dcDiff = dcCategory > 0
                                        ? JpegBitReader.Extend(bitReader.ReadBits(dcCategory), dcCategory)
                                        : 0;
                                    dcPredictors[c] += dcDiff;
                                    block[0] = dcPredictors[c] << scan.Al;
                                }
                                else
                                {
                                    // Refining DC scan: add one bit of precision
                                    block[0] |= bitReader.ReadBits(1) << scan.Al;
                                }
                            }
                        }
                    }
                    mcuCount++;
                }
            }
        }
        else
        {
            // AC scan (Ss>0): always single-component
            int c = scan.ComponentIndices[0];
            int blocksH = components[c].HSample;
            int blocksV = components[c].VSample;
            int totalBlocksH = mcuCols * blocksH;
            int totalBlocksV = mcuRows * blocksV;

            for (int blockRow = 0; blockRow < totalBlocksV; blockRow++)
            {
                for (int blockCol = 0; blockCol < totalBlocksH; blockCol++)
                {
                    if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                    {
                        bitReader.Reset();
                        eobRun = 0;
                    }

                    int blockIndex = blockRow * totalBlocksH + blockCol;
                    var block = components[c].Blocks[blockIndex];

                    if (isFirstScan)
                    {
                        DecodeProgressiveAcFirst(bitReader, block, scan.Ss, scan.Se, scan.Al,
                            acTables[components[c].AcTableIndex], ref eobRun);
                    }
                    else
                    {
                        DecodeProgressiveAcRefine(bitReader, block, scan.Ss, scan.Se, scan.Al,
                            acTables[components[c].AcTableIndex], ref eobRun);
                    }
                    mcuCount++;
                }
            }
        }
    }

    private static void DecodeProgressiveAcFirst(JpegBitReader reader, int[] block,
        int ss, int se, int al, HuffmanTable acTable, ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        for (int k = ss; k <= se; k++)
        {
            byte symbol = acTable.Decode(reader);
            int runLength = symbol >> 4;
            int category = symbol & 0xF;

            if (category == 0)
            {
                if (runLength == 15)
                {
                    k += 15; // ZRL: skip 16 zeros
                }
                else
                {
                    // EOBn: end of band for 2^runLength blocks
                    eobRun = (1 << runLength) - 1;
                    if (runLength > 0)
                    {
                        eobRun += reader.ReadBits(runLength);
                    }
                    return;
                }
            }
            else
            {
                k += runLength;
                if (k > se) break;

                int value = JpegBitReader.Extend(reader.ReadBits(category), category);
                block[JpegTables.NaturalOrder[k]] = value << al;
            }
        }
    }

    private static void DecodeProgressiveAcRefine(JpegBitReader reader, int[] block,
        int ss, int se, int al, HuffmanTable acTable, ref int eobRun)
    {
        int bit = 1 << al;

        if (eobRun > 0)
        {
            // Refine existing nonzero coefficients
            for (int k = ss; k <= se; k++)
            {
                int pos = JpegTables.NaturalOrder[k];
                if (block[pos] != 0)
                {
                    if (reader.ReadBits(1) != 0)
                    {
                        if (block[pos] > 0)
                            block[pos] += bit;
                        else
                            block[pos] -= bit;
                    }
                }
            }
            eobRun--;
            return;
        }

        for (int k = ss; k <= se; k++)
        {
            byte symbol = acTable.Decode(reader);
            int runLength = symbol >> 4;
            int category = symbol & 0xF;

            if (category == 0)
            {
                if (runLength == 15)
                {
                    // ZRL: skip 16 zero positions, refining any nonzero along the way
                    int zerosToSkip = 16;
                    for (; k <= se; k++)
                    {
                        int pos = JpegTables.NaturalOrder[k];
                        if (block[pos] != 0)
                        {
                            if (reader.ReadBits(1) != 0)
                            {
                                if (block[pos] > 0)
                                    block[pos] += bit;
                                else
                                    block[pos] -= bit;
                            }
                        }
                        else
                        {
                            zerosToSkip--;
                            if (zerosToSkip == 0) break;
                        }
                    }
                }
                else
                {
                    // EOBn
                    eobRun = (1 << runLength) - 1;
                    if (runLength > 0)
                    {
                        eobRun += reader.ReadBits(runLength);
                    }
                    // Refine remaining nonzero coefficients in this band
                    for (; k <= se; k++)
                    {
                        int pos = JpegTables.NaturalOrder[k];
                        if (block[pos] != 0)
                        {
                            if (reader.ReadBits(1) != 0)
                            {
                                if (block[pos] > 0)
                                    block[pos] += bit;
                                else
                                    block[pos] -= bit;
                            }
                        }
                    }
                    eobRun--;
                    return;
                }
            }
            else
            {
                // New nonzero coefficient: skip `runLength` zero positions, refining nonzeros
                int newValue = JpegBitReader.Extend(reader.ReadBits(1), 1);
                int zerosToSkip = runLength;
                for (; k <= se; k++)
                {
                    int pos = JpegTables.NaturalOrder[k];
                    if (block[pos] != 0)
                    {
                        if (reader.ReadBits(1) != 0)
                        {
                            if (block[pos] > 0)
                                block[pos] += bit;
                            else
                                block[pos] -= bit;
                        }
                    }
                    else
                    {
                        zerosToSkip--;
                        if (zerosToSkip < 0)
                        {
                            block[pos] = newValue << al;
                            break;
                        }
                    }
                }
            }
        }
    }

    private static ImageFrame DecodeScanData(Stream stream, int width, int height,
        int componentCount, JpegComponent[] components, int[][] quantTables,
        HuffmanTable[] dcTables, HuffmanTable[] acTables,
        int maxHSample, int maxVSample, int restartInterval)
    {
        // Calculate MCU dimensions
        int mcuWidth = maxHSample * BlockSize;
        int mcuHeight = maxVSample * BlockSize;
        int mcuCols = (width + mcuWidth - 1) / mcuWidth;
        int mcuRows = (height + mcuHeight - 1) / mcuHeight;

        // Allocate block storage for each component
        for (int c = 0;c < componentCount;c++)
        {
            int blocksH = mcuCols * components[c].HSample;
            int blocksV = mcuRows * components[c].VSample;
            components[c].Blocks = new int[blocksV * blocksH][];
            for (int i = 0;i < components[c].Blocks.Length;i++)
            {
                components[c].Blocks[i] = new int[64];
            }
        }

        var bitReader = new JpegBitReader(stream);
        int[] dcPredictors = new int[componentCount];
        int mcuCount = 0;
        int restartCounter = 0;

        // Decode all MCUs
        for (int mcuRow = 0;mcuRow < mcuRows;mcuRow++)
        {
            for (int mcuCol = 0;mcuCol < mcuCols;mcuCol++)
            {
                // Handle restart interval
                if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                {
                    bitReader.Reset();
                    Array.Clear(dcPredictors);
                    restartCounter++;
                    // Skip restart marker bytes (already consumed by bit reader or need to find them)
                }

                // Decode each component's blocks in this MCU
                for (int c = 0;c < componentCount;c++)
                {
                    int blocksH = components[c].HSample;
                    int blocksV = components[c].VSample;

                    for (int bv = 0;bv < blocksV;bv++)
                    {
                        for (int bh = 0;bh < blocksH;bh++)
                        {
                            int blockCol = mcuCol * blocksH + bh;
                            int blockRow = mcuRow * blocksV + bv;
                            int blockIndex = blockRow * (mcuCols * blocksH) + blockCol;

                            var block = components[c].Blocks[blockIndex];
                            DecodeBlock(bitReader, block, ref dcPredictors[c],
                                dcTables[components[c].DcTableIndex],
                                acTables[components[c].AcTableIndex],
                                quantTables[components[c].QuantTableIndex]);
                        }
                    }
                }
                mcuCount++;
            }
        }

        // Convert decoded blocks to image pixels
        return BlocksToImage(width, height, componentCount, components, maxHSample, maxVSample, mcuCols);
    }

    private static void DecodeBlock(JpegBitReader reader, int[] block, ref int dcPredictor,
        HuffmanTable dcTable, HuffmanTable acTable, int[] quantTable)
    {
        Array.Clear(block);

        // Decode DC coefficient
        byte dcCategory = dcTable.Decode(reader);
        int dcDiff = dcCategory > 0
            ? JpegBitReader.Extend(reader.ReadBits(dcCategory), dcCategory)
            : 0;
        dcPredictor += dcDiff;
        block[0] = dcPredictor;

        // Decode AC coefficients (zigzag positions 1-63)
        int position = 1;
        while (position < 64)
        {
            byte acSymbol = acTable.Decode(reader);
            if (acSymbol == 0)
            {
                break; // EOB (End of Block)
            }

            int runLength = acSymbol >> 4;   // Number of zero coefficients to skip
            int acCategory = acSymbol & 0xF; // Category of the non-zero coefficient

            if (acCategory == 0 && runLength == 15)
            {
                position += 16; // ZRL (Zero Run Length of 16)
                continue;
            }

            position += runLength;
            if (position >= 64)
            {
                break;
            }

            int acValue = JpegBitReader.Extend(reader.ReadBits(acCategory), acCategory);
            block[JpegTables.NaturalOrder[position]] = acValue;
            position++;
        }

        // Dequantize
        for (int i = 0;i < 64;i++)
        {
            block[i] *= quantTable[i];
        }

        // Inverse DCT
        Dct.InverseDct(block);
    }

    private static ImageFrame BlocksToImage(int width, int height, int componentCount,
        JpegComponent[] components, int maxHSample, int maxVSample, int mcuCols)
    {
        bool isGrayscale = componentCount == 1;
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);

        for (int y = 0;y < height;y++)
        {
            var pixelRow = image.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int offset = x * image.NumberOfChannels;

                if (isGrayscale)
                {
                    int gray = GetComponentSample(components[0], x, y, maxHSample, maxVSample, mcuCols);
                    gray = Math.Clamp(gray + 128, 0, 255);
                    ushort q = Quantum.ScaleFromByte((byte)gray);
                    pixelRow[offset] = q;
                    pixelRow[offset + 1] = q;
                    pixelRow[offset + 2] = q;
                }
                else
                {
                    // YCbCr to RGB conversion
                    int yVal = GetComponentSample(components[0], x, y, maxHSample, maxVSample, mcuCols) + 128;
                    int cb = GetComponentSample(components[1], x, y, maxHSample, maxVSample, mcuCols);
                    int cr = GetComponentSample(components[2], x, y, maxHSample, maxVSample, mcuCols);

                    int r = Math.Clamp(yVal + ((cr * 91881 + 32768) >> 16), 0, 255);
                    int g = Math.Clamp(yVal - ((cb * 22554 + cr * 46802 + 32768) >> 16), 0, 255);
                    int b = Math.Clamp(yVal + ((cb * 116130 + 32768) >> 16), 0, 255);

                    pixelRow[offset] = Quantum.ScaleFromByte((byte)r);
                    pixelRow[offset + 1] = Quantum.ScaleFromByte((byte)g);
                    pixelRow[offset + 2] = Quantum.ScaleFromByte((byte)b);
                }
            }
        }

        return image;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetComponentSample(in JpegComponent comp, int x, int y,
        int maxHSample, int maxVSample, int mcuCols)
    {
        // Map pixel coordinates to the component's block grid (handles subsampling)
        int scaledX = x * comp.HSample / maxHSample;
        int scaledY = y * comp.VSample / maxVSample;

        int blockCol = scaledX >> 3; // / 8
        int blockRow = scaledY >> 3;
        int pixelX = scaledX & 7;    // % 8
        int pixelY = scaledY & 7;

        int blocksPerRow = mcuCols * comp.HSample;
        int blockIndex = blockRow * blocksPerRow + blockCol;

        if (blockIndex >= comp.Blocks.Length)
        {
            return 0;
        }

        return comp.Blocks[blockIndex][pixelY * 8 + pixelX];
    }

    #endregion

    #region Write

    public static void Write(ImageFrame image, string path, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        Write(image, stream, quality, subsampling, optimizeHuffman);
    }

    /// <summary>
    /// Writes a progressive JPEG (SOF2). Multiple spectral scans for better web streaming.
    /// DC coefficients are written first, then AC coefficients in bands.
    /// </summary>
    public static void WriteProgressive(ImageFrame image, string path, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        WriteProgressive(image, stream, quality, subsampling, optimizeHuffman);
    }

    /// <summary>
    /// Writes a progressive JPEG to a stream.
    /// </summary>
    public static void WriteProgressive(ImageFrame image, Stream stream, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        quality = Math.Clamp(quality, 1, 100);
        bool isGrayscale = image.NumberOfChannels == 1;

        int[] lumQuant = JpegTables.ScaleQuantTable(JpegTables.LuminanceQuantTable, quality);
        int[] chrQuant = JpegTables.ScaleQuantTable(JpegTables.ChrominanceQuantTable, quality);

        // Determine sampling factors
        int hSampleY, vSampleY;
        GetSamplingFactors(subsampling, isGrayscale, out hSampleY, out vSampleY);
        int mcuPixelW = hSampleY * 8;
        int mcuPixelH = vSampleY * 8;
        int mcuCols = (width + mcuPixelW - 1) / mcuPixelW;
        int mcuRows = (height + mcuPixelH - 1) / mcuPixelH;

        // Pre-compute all DCT blocks
        int yBlocksPerMcu = hSampleY * vSampleY;
        int totalMcus = mcuCols * mcuRows;
        int totalYBlocks = totalMcus * yBlocksPerMcu;
        int totalChrBlocks = isGrayscale ? 0 : totalMcus;

        int[][] yBlocks = new int[totalYBlocks][];
        int[][] cbBlocks = isGrayscale ? [] : new int[totalChrBlocks][];
        int[][] crBlocks = isGrayscale ? [] : new int[totalChrBlocks][];

        int[] block = new int[64];
        int yIdx = 0, chrIdx = 0;

        for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
        {
            for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
            {
                int mcuX = mcuCol * mcuPixelW;
                int mcuY = mcuRow * mcuPixelH;

                // Extract Y blocks (hSampleY × vSampleY per MCU)
                for (int bv = 0; bv < vSampleY; bv++)
                {
                    for (int bh = 0; bh < hSampleY; bh++)
                    {
                        ExtractYBlock(image, block, mcuX + bh * 8, mcuY + bv * 8, width, height);
                        yBlocks[yIdx++] = QuantizeBlock(block, lumQuant);
                    }
                }

                if (!isGrayscale)
                {
                    ExtractSubsampledCbBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    cbBlocks[chrIdx] = QuantizeBlock(block, chrQuant);

                    ExtractSubsampledCrBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    crBlocks[chrIdx] = QuantizeBlock(block, chrQuant);
                    chrIdx++;
                }
            }
        }

        // Build Huffman tables (optimized or standard)
        (int code, int length)[] dcLumCodes, acLumCodes;
        (int code, int length)[] dcChrCodes, acChrCodes;
        byte[] dcLumBits, dcLumValues, acLumBits, acLumValues;
        byte[] dcChrBits, dcChrValues, acChrBits, acChrValues;

        if (optimizeHuffman)
        {
            BuildOptimizedHuffmanTables(yBlocks, cbBlocks, crBlocks, isGrayscale,
                out dcLumBits, out dcLumValues, out acLumBits, out acLumValues,
                out dcChrBits, out dcChrValues, out acChrBits, out acChrValues);
        }
        else
        {
            dcLumBits = JpegTables.DcLuminanceBits; dcLumValues = JpegTables.DcLuminanceValues;
            acLumBits = JpegTables.AcLuminanceBits; acLumValues = JpegTables.AcLuminanceValues;
            dcChrBits = JpegTables.DcChrominanceBits; dcChrValues = JpegTables.DcChrominanceValues;
            acChrBits = JpegTables.AcChrominanceBits; acChrValues = JpegTables.AcChrominanceValues;
        }

        dcLumCodes = BuildEncoderTable(dcLumBits, dcLumValues);
        acLumCodes = BuildEncoderTable(acLumBits, acLumValues);
        dcChrCodes = BuildEncoderTable(dcChrBits, dcChrValues);
        acChrCodes = BuildEncoderTable(acChrBits, acChrValues);

        // Write SOI
        stream.WriteByte(0xFF);
        stream.WriteByte(SOI);

        WriteApp0(stream);
        WriteMetadataMarkers(stream, image);

        WriteDqt(stream, 0, lumQuant);
        if (!isGrayscale)
            WriteDqt(stream, 1, chrQuant);

        // SOF2 (progressive)
        WriteSof(stream, SOF2, width, height, isGrayscale, hSampleY, vSampleY);

        WriteDht(stream, 0, 0, dcLumBits, dcLumValues);
        WriteDht(stream, 1, 0, acLumBits, acLumValues);
        if (!isGrayscale)
        {
            WriteDht(stream, 0, 1, dcChrBits, dcChrValues);
            WriteDht(stream, 1, 1, acChrBits, acChrValues);
        }

        if (isGrayscale)
        {
            // Scan 1: DC for Y
            WriteProgressiveSosHeader(stream, [1], 0, 0, 0, 0);
            EncodeProgressiveDcScanSingle(stream, totalYBlocks, yBlocks, dcLumCodes);

            // Scan 2: AC for Y
            WriteProgressiveSosHeader(stream, [1], 1, 63, 0, 0);
            EncodeProgressiveAcScan(stream, totalYBlocks, yBlocks, acLumCodes);
        }
        else
        {
            // Scan 1: DC coefficients for all components
            WriteProgressiveSosHeader(stream, [1, 2, 3], 0, 0, 0, 0);
            EncodeProgressiveDcScanSubsampled(stream, mcuCols, mcuRows, yBlocks, cbBlocks, crBlocks,
                dcLumCodes, dcChrCodes, hSampleY, vSampleY);

            // Scan 2: AC for Y
            WriteProgressiveSosHeader(stream, [1], 1, 63, 0, 0);
            EncodeProgressiveAcScan(stream, totalYBlocks, yBlocks, acLumCodes);

            // Scan 3: AC for Cb
            WriteProgressiveSosHeader(stream, [2], 1, 63, 0, 0);
            EncodeProgressiveAcScan(stream, totalChrBlocks, cbBlocks, acChrCodes);

            // Scan 4: AC for Cr
            WriteProgressiveSosHeader(stream, [3], 1, 63, 0, 0);
            EncodeProgressiveAcScan(stream, totalChrBlocks, crBlocks, acChrCodes);
        }

        // Write EOI
        stream.WriteByte(0xFF);
        stream.WriteByte(EOI);
    }

    /// <summary>
    /// Applies forward DCT and quantization, returning the 64-coefficient block in spatial order.
    /// Index 0 = DC. AC coefficients accessed via JpegTables.NaturalOrder for zigzag.
    /// </summary>
    private static int[] QuantizeBlock(int[] spatialBlock, int[] quantTable)
    {
        int[] result = new int[64];
        Array.Copy(spatialBlock, result, 64);
        Dct.ForwardDct(result);
        for (int i = 0; i < 64; i++)
        {
            int q = quantTable[i];
            result[i] = (result[i] + (result[i] >= 0 ? q / 2 : -q / 2)) / q;
        }
        return result;
    }

    private static void WriteSof(Stream stream, byte sofMarker, int width, int height,
        bool isGrayscale, int hSampleY, int vSampleY)
    {
        int componentCount = isGrayscale ? 1 : 3;
        stream.WriteByte(0xFF);
        stream.WriteByte(sofMarker);
        WriteUInt16(stream, 8 + 3 * componentCount);
        stream.WriteByte(8);     // Precision
        WriteUInt16(stream, height);
        WriteUInt16(stream, width);
        stream.WriteByte((byte)componentCount);

        // Y: ID=1, sampling hSampleY × vSampleY, quant table 0
        stream.WriteByte(1);
        stream.WriteByte((byte)((hSampleY << 4) | vSampleY));
        stream.WriteByte(0);

        if (!isGrayscale)
        {
            // Cb: ID=2, sampling 1x1, quant table 1
            stream.WriteByte(2);
            stream.WriteByte(0x11);
            stream.WriteByte(1);

            // Cr: ID=3, sampling 1x1, quant table 1
            stream.WriteByte(3);
            stream.WriteByte(0x11);
            stream.WriteByte(1);
        }
    }

    private static void WriteProgressiveSosHeader(Stream stream, byte[] componentIds,
        int ss, int se, int ah, int al)
    {
        int numComponents = componentIds.Length;
        stream.WriteByte(0xFF);
        stream.WriteByte(SOS);
        WriteUInt16(stream, 6 + 2 * numComponents);
        stream.WriteByte((byte)numComponents);

        foreach (byte compId in componentIds)
        {
            stream.WriteByte(compId);
            // DC/AC table selection: comp 1 → tables 0, comp 2,3 → tables 1
            stream.WriteByte(compId == 1 ? (byte)0x00 : (byte)0x11);
        }

        stream.WriteByte((byte)ss);
        stream.WriteByte((byte)se);
        stream.WriteByte((byte)((ah << 4) | al));
    }

    private static void EncodeProgressiveDcScanSubsampled(Stream stream, int mcuCols, int mcuRows,
        int[][] yBlocks, int[][] cbBlocks, int[][] crBlocks,
        (int code, int length)[] dcLumCodes, (int code, int length)[] dcChrCodes,
        int hSampleY, int vSampleY)
    {
        var bitWriter = new JpegBitWriter(stream);
        int dcPredY = 0, dcPredCb = 0, dcPredCr = 0;
        int yBlocksPerMcu = hSampleY * vSampleY;
        int yIdx = 0, chrIdx = 0;

        int totalMcus = mcuCols * mcuRows;
        for (int i = 0; i < totalMcus; i++)
        {
            // Y DC blocks (hSampleY × vSampleY per MCU)
            for (int b = 0; b < yBlocksPerMcu; b++)
            {
                int yDc = yBlocks[yIdx][0];
                EncodeDcCoefficient(bitWriter, yDc - dcPredY, dcLumCodes);
                dcPredY = yDc;
                yIdx++;
            }

            // Cb DC
            int cbDc = cbBlocks[chrIdx][0];
            EncodeDcCoefficient(bitWriter, cbDc - dcPredCb, dcChrCodes);
            dcPredCb = cbDc;

            // Cr DC
            int crDc = crBlocks[chrIdx][0];
            EncodeDcCoefficient(bitWriter, crDc - dcPredCr, dcChrCodes);
            dcPredCr = crDc;
            chrIdx++;
        }

        bitWriter.Flush();
    }

    private static void EncodeProgressiveDcScanSingle(Stream stream, int totalBlocks,
        int[][] blocks, (int code, int length)[] dcCodes)
    {
        var bitWriter = new JpegBitWriter(stream);
        int dcPred = 0;

        for (int i = 0; i < totalBlocks; i++)
        {
            int dc = blocks[i][0];
            EncodeDcCoefficient(bitWriter, dc - dcPred, dcCodes);
            dcPred = dc;
        }

        bitWriter.Flush();
    }

    private static void EncodeProgressiveAcScan(Stream stream, int totalBlocks,
        int[][] blocks, (int code, int length)[] acCodes)
    {
        var bitWriter = new JpegBitWriter(stream);

        for (int i = 0; i < totalBlocks; i++)
        {
            int[] block = blocks[i];
            EncodeAcCoefficients(bitWriter, block, acCodes);
        }

        bitWriter.Flush();
    }

    private static void EncodeDcCoefficient(JpegBitWriter writer, int diff,
        (int code, int length)[] dcCodes)
    {
        var (category, extraBits) = JpegBitWriter.EncodeValue(diff);
        var (code, length) = dcCodes[category];
        writer.WriteBits(code, length);
        if (category > 0)
        {
            writer.WriteBits(extraBits, category);
        }
    }

    private static void EncodeAcCoefficients(JpegBitWriter writer, int[] block,
        (int code, int length)[] acCodes)
    {
        // Walk AC coefficients in zigzag order (same as baseline EncodeBlock)
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int zigzagIndex = JpegTables.NaturalOrder[i];
            int value = block[zigzagIndex];

            if (value == 0)
            {
                zeroRun++;
                continue;
            }

            while (zeroRun >= 16)
            {
                var (zrlCode, zrlLen) = acCodes[0xF0];
                writer.WriteBits(zrlCode, zrlLen);
                zeroRun -= 16;
            }

            var (category, extraBits) = JpegBitWriter.EncodeValue(value);
            int symbol = (zeroRun << 4) | category;
            var (symCode, symLen) = acCodes[symbol];
            writer.WriteBits(symCode, symLen);
            if (category > 0)
            {
                writer.WriteBits(extraBits, category);
            }

            zeroRun = 0;
        }

        // EOB if trailing zeros
        if (zeroRun > 0)
        {
            var (eobCode, eobLen) = acCodes[0x00];
            writer.WriteBits(eobCode, eobLen);
        }
    }

    public static void Write(ImageFrame image, Stream stream, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        quality = Math.Clamp(quality, 1, 100);
        bool isGrayscale = image.NumberOfChannels == 1;

        // Scale quantization tables
        int[] lumQuant = JpegTables.ScaleQuantTable(JpegTables.LuminanceQuantTable, quality);
        int[] chrQuant = JpegTables.ScaleQuantTable(JpegTables.ChrominanceQuantTable, quality);

        // Determine sampling factors
        int hSampleY, vSampleY;
        GetSamplingFactors(subsampling, isGrayscale, out hSampleY, out vSampleY);
        int mcuPixelW = hSampleY * 8;
        int mcuPixelH = vSampleY * 8;
        int mcuCols = (width + mcuPixelW - 1) / mcuPixelW;
        int mcuRows = (height + mcuPixelH - 1) / mcuPixelH;

        // For optimized Huffman: two-pass encoding. First collect all blocks, then build tables.
        int[][] allYBlocks = null!;
        int[][] allCbBlocks = null!;
        int[][] allCrBlocks = null!;

        if (optimizeHuffman)
        {
            PrecomputeAllBlocks(image, width, height, isGrayscale,
                mcuCols, mcuRows, mcuPixelW, mcuPixelH, hSampleY, vSampleY,
                lumQuant, chrQuant,
                out allYBlocks, out allCbBlocks, out allCrBlocks);
        }

        // Build Huffman tables
        (int code, int length)[] dcLumCodes, acLumCodes;
        (int code, int length)[] dcChrCodes, acChrCodes;
        byte[] dcLumBits, dcLumValues, acLumBits, acLumValues;
        byte[] dcChrBits, dcChrValues, acChrBits, acChrValues;

        if (optimizeHuffman)
        {
            BuildOptimizedHuffmanTables(allYBlocks, allCbBlocks, allCrBlocks, isGrayscale,
                out dcLumBits, out dcLumValues, out acLumBits, out acLumValues,
                out dcChrBits, out dcChrValues, out acChrBits, out acChrValues);
        }
        else
        {
            dcLumBits = JpegTables.DcLuminanceBits; dcLumValues = JpegTables.DcLuminanceValues;
            acLumBits = JpegTables.AcLuminanceBits; acLumValues = JpegTables.AcLuminanceValues;
            dcChrBits = JpegTables.DcChrominanceBits; dcChrValues = JpegTables.DcChrominanceValues;
            acChrBits = JpegTables.AcChrominanceBits; acChrValues = JpegTables.AcChrominanceValues;
        }

        dcLumCodes = BuildEncoderTable(dcLumBits, dcLumValues);
        acLumCodes = BuildEncoderTable(acLumBits, acLumValues);
        dcChrCodes = BuildEncoderTable(dcChrBits, dcChrValues);
        acChrCodes = BuildEncoderTable(acChrBits, acChrValues);

        // Write SOI
        stream.WriteByte(0xFF);
        stream.WriteByte(SOI);

        // Write JFIF APP0
        WriteApp0(stream);

        // Write metadata APP markers (EXIF, ICC, IPTC, XMP) if present
        WriteMetadataMarkers(stream, image);

        // Write DQT (quantization tables)
        WriteDqt(stream, 0, lumQuant);
        if (!isGrayscale)
            WriteDqt(stream, 1, chrQuant);

        // Write SOF0 (baseline DCT)
        WriteSof(stream, SOF0, width, height, isGrayscale, hSampleY, vSampleY);

        // Write DHT (Huffman tables)
        WriteDht(stream, 0, 0, dcLumBits, dcLumValues);
        WriteDht(stream, 1, 0, acLumBits, acLumValues);
        if (!isGrayscale)
        {
            WriteDht(stream, 0, 1, dcChrBits, dcChrValues);
            WriteDht(stream, 1, 1, acChrBits, acChrValues);
        }

        // Write SOS header
        WriteSosHeaderGeneric(stream, isGrayscale);

        // Encode scan data
        if (optimizeHuffman)
        {
            // Use pre-computed blocks
            EncodePrecomputedScanData(stream, allYBlocks, allCbBlocks, allCrBlocks,
                isGrayscale, mcuCols, mcuRows, hSampleY, vSampleY,
                dcLumCodes, acLumCodes, dcChrCodes, acChrCodes);
        }
        else
        {
            EncodeSubsampledScanData(stream, image, width, height, isGrayscale,
                mcuCols, mcuRows, mcuPixelW, mcuPixelH, hSampleY, vSampleY,
                lumQuant, chrQuant,
                dcLumCodes, acLumCodes, dcChrCodes, acChrCodes);
        }

        // Write EOI
        stream.WriteByte(0xFF);
        stream.WriteByte(EOI);
    }

    private static void WriteApp0(Stream stream)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(APP0);
        WriteUInt16(stream, 16);        // Length
        stream.Write("JFIF\0"u8);      // Identifier
        stream.WriteByte(1);             // Version major
        stream.WriteByte(1);             // Version minor
        stream.WriteByte(0);             // Units (0 = no units)
        WriteUInt16(stream, 1);          // X density
        WriteUInt16(stream, 1);          // Y density
        stream.WriteByte(0);             // Thumbnail width
        stream.WriteByte(0);             // Thumbnail height
    }

    /// <summary>
    /// Writes EXIF, ICC, IPTC, and XMP metadata as APP markers.
    /// </summary>
    private static void WriteMetadataMarkers(Stream stream, ImageFrame image)
    {
        // EXIF → APP1
        if (image.Metadata.ExifProfile is not null)
        {
            byte[] exifPayload = ExifParser.SerializeForApp1(image.Metadata.ExifProfile);
            if (exifPayload.Length > 0 && exifPayload.Length <= 65533)
            {
                stream.WriteByte(0xFF);
                stream.WriteByte(APP1);
                WriteUInt16(stream, exifPayload.Length + 2);
                stream.Write(exifPayload);
            }
        }

        // XMP → APP1 (separate from EXIF)
        if (image.Metadata.Xmp is not null)
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            byte[] xmpBytes = System.Text.Encoding.UTF8.GetBytes(image.Metadata.Xmp);
            int totalLen = header.Length + xmpBytes.Length;
            if (totalLen <= 65533)
            {
                stream.WriteByte(0xFF);
                stream.WriteByte(APP1);
                WriteUInt16(stream, totalLen + 2);
                stream.Write(header);
                stream.Write(xmpBytes);
            }
        }

        // ICC Profile → APP2 (chunked if needed)
        if (image.Metadata.IccProfile is not null)
        {
            byte[] iccData = image.Metadata.IccProfile.Data;
            const int maxChunkData = 65533 - 14; // max payload minus header
            int chunkCount = (iccData.Length + maxChunkData - 1) / maxChunkData;
            if (chunkCount > 255) chunkCount = 255;

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * maxChunkData;
                int len = Math.Min(maxChunkData, iccData.Length - offset);
                int segLen = 14 + len + 2; // header(12) + chunk#(1) + total(1) + data + length field(2)

                stream.WriteByte(0xFF);
                stream.WriteByte(APP2);
                WriteUInt16(stream, 14 + len + 2);
                stream.Write("ICC_PROFILE\0"u8);
                stream.WriteByte((byte)(i + 1));
                stream.WriteByte((byte)chunkCount);
                stream.Write(iccData, offset, len);
            }
        }

        // IPTC → APP13
        if (image.Metadata.IptcProfile is not null)
        {
            byte[] iptcPayload = IptcParser.SerializeForApp13(image.Metadata.IptcProfile);
            if (iptcPayload.Length > 0 && iptcPayload.Length <= 65533)
            {
                stream.WriteByte(0xFF);
                stream.WriteByte(APP13);
                WriteUInt16(stream, iptcPayload.Length + 2);
                stream.Write(iptcPayload);
            }
        }
    }

    private static void WriteSosHeaderGeneric(Stream stream, bool isGrayscale)
    {
        int componentCount = isGrayscale ? 1 : 3;
        stream.WriteByte(0xFF);
        stream.WriteByte(SOS);
        WriteUInt16(stream, 6 + 2 * componentCount);
        stream.WriteByte((byte)componentCount);

        // Y: DC table 0, AC table 0
        stream.WriteByte(1);
        stream.WriteByte(0x00);

        if (!isGrayscale)
        {
            // Cb: DC table 1, AC table 1
            stream.WriteByte(2);
            stream.WriteByte(0x11);
            // Cr: DC table 1, AC table 1
            stream.WriteByte(3);
            stream.WriteByte(0x11);
        }

        stream.WriteByte(0);   // Ss
        stream.WriteByte(63);  // Se
        stream.WriteByte(0);   // Ah/Al
    }

    private static void WriteDqt(Stream stream, int tableIndex, int[] table)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(DQT);
        WriteUInt16(stream, 67);         // Length: 2 + 1 + 64
        stream.WriteByte((byte)tableIndex);
        for (int i = 0;i < 64;i++)
        {
            stream.WriteByte((byte)table[JpegTables.NaturalOrder[i]]);
        }
    }

    private static void WriteDht(Stream stream, int tableClass, int tableIndex,
        ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        int totalValues = 0;
        for (int i = 0;i < 16;i++)
        {
            totalValues += bits[i];
        }

        stream.WriteByte(0xFF);
        stream.WriteByte(DHT);
        WriteUInt16(stream, 3 + 16 + totalValues);
        stream.WriteByte((byte)((tableClass << 4) | tableIndex));
        stream.Write(bits);
        stream.Write(values[..totalValues]);
    }

    private static void EncodeSubsampledScanData(Stream stream, ImageFrame image,
        int width, int height, bool isGrayscale,
        int mcuCols, int mcuRows, int mcuPixelW, int mcuPixelH,
        int hSampleY, int vSampleY,
        int[] lumQuant, int[] chrQuant,
        (int code, int length)[] dcLumCodes, (int code, int length)[] acLumCodes,
        (int code, int length)[] dcChrCodes, (int code, int length)[] acChrCodes)
    {
        var bitWriter = new JpegBitWriter(stream);
        int[] block = new int[64];
        int dcPredY = 0, dcPredCb = 0, dcPredCr = 0;

        for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
        {
            for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
            {
                int mcuX = mcuCol * mcuPixelW;
                int mcuY = mcuRow * mcuPixelH;

                // Encode Y blocks (hSampleY × vSampleY per MCU)
                for (int bv = 0; bv < vSampleY; bv++)
                {
                    for (int bh = 0; bh < hSampleY; bh++)
                    {
                        ExtractYBlock(image, block, mcuX + bh * 8, mcuY + bv * 8, width, height);
                        EncodeBlock(bitWriter, block, ref dcPredY, lumQuant, dcLumCodes, acLumCodes);
                    }
                }

                if (!isGrayscale)
                {
                    // Encode Cb block (averaged over MCU area)
                    ExtractSubsampledCbBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    EncodeBlock(bitWriter, block, ref dcPredCb, chrQuant, dcChrCodes, acChrCodes);

                    // Encode Cr block (averaged over MCU area)
                    ExtractSubsampledCrBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    EncodeBlock(bitWriter, block, ref dcPredCr, chrQuant, dcChrCodes, acChrCodes);
                }
            }
        }

        bitWriter.Flush();
    }

    private static void EncodePrecomputedScanData(Stream stream,
        int[][] yBlocks, int[][] cbBlocks, int[][] crBlocks,
        bool isGrayscale, int mcuCols, int mcuRows,
        int hSampleY, int vSampleY,
        (int code, int length)[] dcLumCodes, (int code, int length)[] acLumCodes,
        (int code, int length)[] dcChrCodes, (int code, int length)[] acChrCodes)
    {
        var bitWriter = new JpegBitWriter(stream);
        int dcPredY = 0, dcPredCb = 0, dcPredCr = 0;
        int yBlocksPerMcu = hSampleY * vSampleY;
        int yIdx = 0, chrIdx = 0;

        int totalMcus = mcuCols * mcuRows;
        for (int i = 0; i < totalMcus; i++)
        {
            for (int b = 0; b < yBlocksPerMcu; b++)
            {
                EncodePrecomputedBlock(bitWriter, yBlocks[yIdx], ref dcPredY, dcLumCodes, acLumCodes);
                yIdx++;
            }

            if (!isGrayscale)
            {
                EncodePrecomputedBlock(bitWriter, cbBlocks[chrIdx], ref dcPredCb, dcChrCodes, acChrCodes);
                EncodePrecomputedBlock(bitWriter, crBlocks[chrIdx], ref dcPredCr, dcChrCodes, acChrCodes);
                chrIdx++;
            }
        }

        bitWriter.Flush();
    }

    private static void EncodePrecomputedBlock(JpegBitWriter writer, int[] quantizedBlock,
        ref int dcPredictor, (int code, int length)[] dcCodes, (int code, int length)[] acCodes)
    {
        // DC coefficient
        int dcDiff = quantizedBlock[0] - dcPredictor;
        dcPredictor = quantizedBlock[0];

        var (dcCategory, dcBits) = JpegBitWriter.EncodeValue(dcDiff);
        var dcEntry = dcCodes[dcCategory];
        writer.WriteBits(dcEntry.code, dcEntry.length);
        if (dcCategory > 0)
            writer.WriteBits(dcBits, dcCategory);

        // AC coefficients in zigzag order
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int zigzagIndex = JpegTables.NaturalOrder[i];
            int acValue = quantizedBlock[zigzagIndex];

            if (acValue == 0) { zeroRun++; continue; }

            while (zeroRun >= 16)
            {
                var zrlEntry = acCodes[0xF0];
                writer.WriteBits(zrlEntry.code, zrlEntry.length);
                zeroRun -= 16;
            }

            var (acCategory, acBits) = JpegBitWriter.EncodeValue(acValue);
            int acSymbol = (zeroRun << 4) | acCategory;
            var acEntry = acCodes[acSymbol];
            writer.WriteBits(acEntry.code, acEntry.length);
            if (acCategory > 0)
                writer.WriteBits(acBits, acCategory);

            zeroRun = 0;
        }

        if (zeroRun > 0)
        {
            var eobEntry = acCodes[0x00];
            writer.WriteBits(eobEntry.code, eobEntry.length);
        }
    }

    private static void ExtractYBlock(ImageFrame image, int[] block, int blockX, int blockY,
        int width, int height)
    {
        for (int by = 0;by < 8;by++)
        {
            int y = Math.Min(blockY + by, height - 1);
            var row = image.GetPixelRow(y);
            int channels = image.NumberOfChannels;

            for (int bx = 0;bx < 8;bx++)
            {
                int x = Math.Min(blockX + bx, width - 1);
                int offset = x * channels;

                byte r = Quantum.ScaleToByte(row[offset]);
                byte g = Quantum.ScaleToByte(row[offset + 1]);
                byte b = Quantum.ScaleToByte(row[offset + 2]);

                // RGB to Y (level-shifted by -128)
                block[by * 8 + bx] = ((r * 19595 + g * 38470 + b * 7471 + 32768) >> 16) - 128;
            }
        }
    }

    private static void ExtractCbBlock(ImageFrame image, int[] block, int blockX, int blockY,
        int width, int height)
    {
        ExtractSubsampledCbBlock(image, block, blockX, blockY, width, height, 8, 8);
    }

    private static void ExtractSubsampledCbBlock(ImageFrame image, int[] block,
        int areaX, int areaY, int width, int height, int areaW, int areaH)
    {
        // Average chroma values from the specified area into an 8×8 block
        int channels = image.NumberOfChannels;
        for (int by = 0; by < 8; by++)
        {
            // Map block row to source rows
            int srcY0 = areaY + by * areaH / 8;
            int srcY1 = areaY + (by + 1) * areaH / 8;
            srcY0 = Math.Min(srcY0, height - 1);
            srcY1 = Math.Min(srcY1, height);
            if (srcY1 <= srcY0) srcY1 = srcY0 + 1;

            for (int bx = 0; bx < 8; bx++)
            {
                int srcX0 = areaX + bx * areaW / 8;
                int srcX1 = areaX + (bx + 1) * areaW / 8;
                srcX0 = Math.Min(srcX0, width - 1);
                srcX1 = Math.Min(srcX1, width);
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;

                int sum = 0;
                int count = 0;
                for (int sy = srcY0; sy < srcY1; sy++)
                {
                    int clampY = Math.Min(sy, height - 1);
                    var row = image.GetPixelRow(clampY);
                    for (int sx = srcX0; sx < srcX1; sx++)
                    {
                        int clampX = Math.Min(sx, width - 1);
                        int offset = clampX * channels;
                        byte r = Quantum.ScaleToByte(row[offset]);
                        byte g = Quantum.ScaleToByte(row[offset + 1]);
                        byte b = Quantum.ScaleToByte(row[offset + 2]);
                        sum += (-r * 11056 - g * 21712 + b * 32768 + 32768) >> 16;
                        count++;
                    }
                }
                block[by * 8 + bx] = count > 0 ? sum / count : 0;
            }
        }
    }

    private static void ExtractCrBlock(ImageFrame image, int[] block, int blockX, int blockY,
        int width, int height)
    {
        ExtractSubsampledCrBlock(image, block, blockX, blockY, width, height, 8, 8);
    }

    private static void ExtractSubsampledCrBlock(ImageFrame image, int[] block,
        int areaX, int areaY, int width, int height, int areaW, int areaH)
    {
        int channels = image.NumberOfChannels;
        for (int by = 0; by < 8; by++)
        {
            int srcY0 = areaY + by * areaH / 8;
            int srcY1 = areaY + (by + 1) * areaH / 8;
            srcY0 = Math.Min(srcY0, height - 1);
            srcY1 = Math.Min(srcY1, height);
            if (srcY1 <= srcY0) srcY1 = srcY0 + 1;

            for (int bx = 0; bx < 8; bx++)
            {
                int srcX0 = areaX + bx * areaW / 8;
                int srcX1 = areaX + (bx + 1) * areaW / 8;
                srcX0 = Math.Min(srcX0, width - 1);
                srcX1 = Math.Min(srcX1, width);
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;

                int sum = 0;
                int count = 0;
                for (int sy = srcY0; sy < srcY1; sy++)
                {
                    int clampY = Math.Min(sy, height - 1);
                    var row = image.GetPixelRow(clampY);
                    for (int sx = srcX0; sx < srcX1; sx++)
                    {
                        int clampX = Math.Min(sx, width - 1);
                        int offset = clampX * channels;
                        byte r = Quantum.ScaleToByte(row[offset]);
                        byte g = Quantum.ScaleToByte(row[offset + 1]);
                        byte b = Quantum.ScaleToByte(row[offset + 2]);
                        sum += (r * 32768 - g * 27440 - b * 5328 + 32768) >> 16;
                        count++;
                    }
                }
                block[by * 8 + bx] = count > 0 ? sum / count : 0;
            }
        }
    }

    private static void EncodeBlock(JpegBitWriter writer, int[] block, ref int dcPredictor,
        int[] quantTable, (int code, int length)[] dcCodes, (int code, int length)[] acCodes)
    {
        // Forward DCT
        Dct.ForwardDct(block);

        // Quantize
        for (int i = 0;i < 64;i++)
        {
            int q = quantTable[i];
            block[i] = (block[i] + (block[i] >= 0 ? q / 2 : -q / 2)) / q;
        }

        // Encode DC coefficient
        int dcDiff = block[0] - dcPredictor;
        dcPredictor = block[0];

        var (dcCategory, dcBits) = JpegBitWriter.EncodeValue(dcDiff);
        var dcEntry = dcCodes[dcCategory];
        writer.WriteBits(dcEntry.code, dcEntry.length);
        if (dcCategory > 0)
        {
            writer.WriteBits(dcBits, dcCategory);
        }

        // Encode AC coefficients in zigzag order
        int zeroRun = 0;
        for (int i = 1;i < 64;i++)
        {
            int zigzagIndex = JpegTables.NaturalOrder[i];
            int acValue = block[zigzagIndex];

            if (acValue == 0)
            {
                zeroRun++;
                continue;
            }

            // Emit ZRL (zero run length = 16) symbols for runs > 15
            while (zeroRun >= 16)
            {
                var zrlEntry = acCodes[0xF0];
                writer.WriteBits(zrlEntry.code, zrlEntry.length);
                zeroRun -= 16;
            }

            // Emit run/category symbol + extra bits
            var (acCategory, acBits) = JpegBitWriter.EncodeValue(acValue);
            int acSymbol = (zeroRun << 4) | acCategory;
            var acEntry = acCodes[acSymbol];
            writer.WriteBits(acEntry.code, acEntry.length);
            if (acCategory > 0)
            {
                writer.WriteBits(acBits, acCategory);
            }

            zeroRun = 0;
        }

        // Emit EOB if there are trailing zeros
        if (zeroRun > 0)
        {
            var eobEntry = acCodes[0x00];
            writer.WriteBits(eobEntry.code, eobEntry.length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetSamplingFactors(JpegSubsampling subsampling, bool isGrayscale,
        out int hSampleY, out int vSampleY)
    {
        if (isGrayscale)
        {
            hSampleY = 1; vSampleY = 1;
            return;
        }

        switch (subsampling)
        {
            case JpegSubsampling.Yuv422:
                hSampleY = 2; vSampleY = 1; break;
            case JpegSubsampling.Yuv420:
                hSampleY = 2; vSampleY = 2; break;
            default:
                hSampleY = 1; vSampleY = 1; break;
        }
    }

    private static void PrecomputeAllBlocks(ImageFrame image, int width, int height,
        bool isGrayscale, int mcuCols, int mcuRows, int mcuPixelW, int mcuPixelH,
        int hSampleY, int vSampleY, int[] lumQuant, int[] chrQuant,
        out int[][] yBlocks, out int[][] cbBlocks, out int[][] crBlocks)
    {
        int yBlocksPerMcu = hSampleY * vSampleY;
        int totalMcus = mcuCols * mcuRows;
        yBlocks = new int[totalMcus * yBlocksPerMcu][];
        cbBlocks = isGrayscale ? [] : new int[totalMcus][];
        crBlocks = isGrayscale ? [] : new int[totalMcus][];

        int[] block = new int[64];
        int yIdx = 0, chrIdx = 0;

        for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
        {
            for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
            {
                int mcuX = mcuCol * mcuPixelW;
                int mcuY = mcuRow * mcuPixelH;

                for (int bv = 0; bv < vSampleY; bv++)
                {
                    for (int bh = 0; bh < hSampleY; bh++)
                    {
                        ExtractYBlock(image, block, mcuX + bh * 8, mcuY + bv * 8, width, height);
                        yBlocks[yIdx++] = QuantizeBlock(block, lumQuant);
                    }
                }

                if (!isGrayscale)
                {
                    ExtractSubsampledCbBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    cbBlocks[chrIdx] = QuantizeBlock(block, chrQuant);

                    ExtractSubsampledCrBlock(image, block, mcuX, mcuY, width, height, mcuPixelW, mcuPixelH);
                    crBlocks[chrIdx] = QuantizeBlock(block, chrQuant);
                    chrIdx++;
                }
            }
        }
    }

    /// <summary>
    /// Builds optimized Huffman tables from actual symbol frequencies in the image data.
    /// Per ITU-T T.81 Annex K: count frequencies, generate optimal code lengths,
    /// adjust to max 16-bit codes, then generate bits/values arrays.
    /// </summary>
    private static void BuildOptimizedHuffmanTables(int[][] yBlocks, int[][] cbBlocks, int[][] crBlocks,
        bool isGrayscale,
        out byte[] dcLumBits, out byte[] dcLumValues,
        out byte[] acLumBits, out byte[] acLumValues,
        out byte[] dcChrBits, out byte[] dcChrValues,
        out byte[] acChrBits, out byte[] acChrValues)
    {
        // Count DC and AC symbol frequencies for luminance
        int[] dcLumFreq = new int[17]; // categories 0-16
        int[] acLumFreq = new int[257]; // symbols 0x00-0xFF + EOB

        CountBlockFrequencies(yBlocks, dcLumFreq, acLumFreq);

        GenerateHuffmanSpec(dcLumFreq, 17, out dcLumBits, out dcLumValues);
        GenerateHuffmanSpec(acLumFreq, 257, out acLumBits, out acLumValues);

        if (isGrayscale)
        {
            // Use standard tables as fallback for unused chrominance
            dcChrBits = JpegTables.DcChrominanceBits;
            dcChrValues = JpegTables.DcChrominanceValues;
            acChrBits = JpegTables.AcChrominanceBits;
            acChrValues = JpegTables.AcChrominanceValues;
            return;
        }

        // Count chrominance frequencies (Cb + Cr combined)
        int[] dcChrFreq = new int[17];
        int[] acChrFreq = new int[257];

        CountBlockFrequencies(cbBlocks, dcChrFreq, acChrFreq);
        CountBlockFrequencies(crBlocks, dcChrFreq, acChrFreq);

        GenerateHuffmanSpec(dcChrFreq, 17, out dcChrBits, out dcChrValues);
        GenerateHuffmanSpec(acChrFreq, 257, out acChrBits, out acChrValues);
    }

    private static void CountBlockFrequencies(int[][] blocks, int[] dcFreq, int[] acFreq)
    {
        int dcPred = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            int[] block = blocks[i];

            // DC
            int dcDiff = block[0] - dcPred;
            dcPred = block[0];
            var (dcCat, _) = JpegBitWriter.EncodeValue(dcDiff);
            if (dcCat < dcFreq.Length)
                dcFreq[dcCat]++;

            // AC in zigzag order
            int zeroRun = 0;
            for (int j = 1; j < 64; j++)
            {
                int zigzagIndex = JpegTables.NaturalOrder[j];
                int value = block[zigzagIndex];

                if (value == 0)
                {
                    zeroRun++;
                    continue;
                }

                while (zeroRun >= 16)
                {
                    acFreq[0xF0]++;
                    zeroRun -= 16;
                }

                var (acCat, _) = JpegBitWriter.EncodeValue(value);
                int symbol = (zeroRun << 4) | acCat;
                if (symbol < acFreq.Length)
                    acFreq[symbol]++;
                zeroRun = 0;
            }

            if (zeroRun > 0)
                acFreq[0x00]++; // EOB
        }
    }

    /// <summary>
    /// Generates JPEG Huffman bits/values specification from symbol frequencies.
    /// Uses the algorithm from ITU-T T.81 Annex K.2 (Figure K.1-K.4).
    /// </summary>
    private static void GenerateHuffmanSpec(int[] freq, int symbolCount,
        out byte[] bits, out byte[] values)
    {
        // Ensure at least 2 symbols have non-zero frequency
        int nonZero = 0;
        for (int i = 0; i < symbolCount; i++)
            if (freq[i] > 0) nonZero++;

        if (nonZero < 2)
        {
            // Need at least 2 symbols for a valid Huffman tree
            // Add dummy frequencies for symbols 0 and 1 if needed
            if (freq[0] == 0) { freq[0] = 1; nonZero++; }
            if (nonZero < 2 && freq[1] == 0) { freq[1] = 1; }
        }

        // Build a Huffman tree by repeated merging of lowest-frequency nodes
        // Result: codeLength[i] = bit length assigned to symbol i
        int maxSymbols = Math.Min(symbolCount, 256);
        int[] codeLength = new int[maxSymbols];

        // Collect symbols with non-zero frequency
        var symbols = new List<(int symbol, int frequency)>();
        for (int i = 0; i < maxSymbols; i++)
        {
            if (freq[i] > 0)
                symbols.Add((i, freq[i]));
        }

        if (symbols.Count == 0)
        {
            // Fallback: single-symbol tree
            bits = new byte[16];
            bits[0] = 1;
            values = [0];
            return;
        }

        if (symbols.Count == 1)
        {
            bits = new byte[16];
            bits[0] = 1;
            values = [(byte)symbols[0].symbol];
            return;
        }

        // Package-merge algorithm for length-limited Huffman codes (max 16 bits)
        // Simplified: use standard Huffman then limit to 16 bits

        // Build code lengths using a priority queue approach
        int n = symbols.Count;
        var nodes = new List<HuffNode>(n * 2);
        for (int i = 0; i < n; i++)
            nodes.Add(new HuffNode { Symbol = symbols[i].symbol, Frequency = symbols[i].frequency, Depth = 0 });

        // Sort by frequency (ascending)
        nodes.Sort((a, b) => a.Frequency.CompareTo(b.Frequency));

        // Build tree by merging two lowest-frequency nodes
        var queue1 = new Queue<HuffNode>(nodes);
        var queue2 = new Queue<HuffNode>();

        while (queue1.Count + queue2.Count > 1)
        {
            var left = Dequeue(queue1, queue2);
            var right = Dequeue(queue1, queue2);

            var merged = new HuffNode
            {
                Symbol = -1,
                Frequency = left.Frequency + right.Frequency,
                Depth = Math.Max(left.Depth, right.Depth) + 1,
                Left = left,
                Right = right
            };
            queue2.Enqueue(merged);
        }

        var root = queue1.Count > 0 ? queue1.Dequeue() : queue2.Dequeue();

        // Extract code lengths from tree
        AssignCodeLengths(root, 0, codeLength);

        // Limit code lengths to 16 bits (JPEG maximum)
        LimitCodeLengths(codeLength, maxSymbols, 16);

        // Generate bits[] and values[] arrays from code lengths
        bits = new byte[16];
        var sortedSymbols = new List<(int symbol, int length)>();

        for (int i = 0; i < maxSymbols; i++)
        {
            if (codeLength[i] > 0)
            {
                bits[codeLength[i] - 1]++;
                sortedSymbols.Add((i, codeLength[i]));
            }
        }

        // Sort by (length ascending, symbol ascending) per JPEG spec
        sortedSymbols.Sort((a, b) =>
        {
            int cmp = a.length.CompareTo(b.length);
            return cmp != 0 ? cmp : a.symbol.CompareTo(b.symbol);
        });

        values = new byte[sortedSymbols.Count];
        for (int i = 0; i < sortedSymbols.Count; i++)
            values[i] = (byte)sortedSymbols[i].symbol;
    }

    private static HuffNode Dequeue(Queue<HuffNode> q1, Queue<HuffNode> q2)
    {
        if (q1.Count == 0) return q2.Dequeue();
        if (q2.Count == 0) return q1.Dequeue();
        return q1.Peek().Frequency <= q2.Peek().Frequency ? q1.Dequeue() : q2.Dequeue();
    }

    private static void AssignCodeLengths(HuffNode node, int depth, int[] codeLengths)
    {
        if (node.Left is null && node.Right is null)
        {
            if (node.Symbol >= 0 && node.Symbol < codeLengths.Length)
                codeLengths[node.Symbol] = depth;
            return;
        }
        if (node.Left is not null) AssignCodeLengths(node.Left, depth + 1, codeLengths);
        if (node.Right is not null) AssignCodeLengths(node.Right, depth + 1, codeLengths);
    }

    /// <summary>
    /// Limits Huffman code lengths to maxLen bits using the algorithm from ITU-T T.81 Annex K.3.
    /// </summary>
    private static void LimitCodeLengths(int[] codeLengths, int symbolCount, int maxLen)
    {
        bool needsAdjustment = false;
        for (int i = 0; i < symbolCount; i++)
        {
            if (codeLengths[i] > maxLen)
            {
                needsAdjustment = true;
                break;
            }
        }
        if (!needsAdjustment) return;

        // Count symbols at each depth
        int[] depthCount = new int[33];
        for (int i = 0; i < symbolCount; i++)
        {
            if (codeLengths[i] > 0)
                depthCount[Math.Min(codeLengths[i], 32)]++;
        }

        // Move symbols from depths > maxLen up to maxLen per Annex K.3
        while (true)
        {
            bool changed = false;
            for (int depth = 32; depth > maxLen; depth--)
            {
                while (depthCount[depth] > 0)
                {
                    // Find a symbol at this depth and shorten it
                    int j = depth - 2;
                    while (j > 0 && depthCount[j] == 0) j--;

                    depthCount[depth] -= 2;
                    depthCount[depth - 1]++;
                    depthCount[j + 1] += 2;
                    depthCount[j]--;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // Reassign code lengths based on adjusted depth counts
        // Sort symbols by original code length (longest first)
        var symbolsByLength = new List<int>();
        for (int i = 0; i < symbolCount; i++)
        {
            if (codeLengths[i] > 0)
                symbolsByLength.Add(i);
        }
        symbolsByLength.Sort((a, b) => codeLengths[b].CompareTo(codeLengths[a]));

        int idx = 0;
        for (int depth = maxLen; depth >= 1; depth--)
        {
            for (int c = 0; c < depthCount[depth] && idx < symbolsByLength.Count; c++)
            {
                codeLengths[symbolsByLength[idx++]] = depth;
            }
        }
    }

    private class HuffNode
    {
        public int Symbol;
        public int Frequency;
        public int Depth;
        public HuffNode? Left;
        public HuffNode? Right;
    }

    /// <summary>
    /// Builds an encoder lookup table from Huffman bits/values specification. Returns array indexed by symbol value →
    /// (code, length).
    /// </summary>
    private static (int code, int length)[] BuildEncoderTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        var table = new (int code, int length)[256];
        int code = 0;
        int valueIndex = 0;

        for (int len = 1;len <= 16;len++)
        {
            for (int i = 0;i < bits[len - 1];i++)
            {
                table[values[valueIndex]] = (code, len);
                code++;
                valueIndex++;
            }
            code <<= 1;
        }

        return table;
    }

    #endregion

    #region Helpers

    private static int ReadMarker(Stream stream)
    {
        int b;
        // Find marker prefix 0xFF
        do
        {
            b = stream.ReadByte();
            if (b < 0)
            {
                return -1;
            }
        }
        while (b != 0xFF);

        // Skip padding 0xFF bytes
        do
        {
            b = stream.ReadByte();
            if (b < 0)
            {
                return -1;
            }
        }
        while (b == 0xFF);

        return b;
    }

    private static void SkipMarkerSegment(Stream stream)
    {
        int length = ReadUInt16(stream);
        if (length > 2)
        {
            if (stream.CanSeek)
            {
                stream.Seek(length - 2, SeekOrigin.Current);
            }
            else
            {
                byte[] skip = new byte[Math.Min(length - 2, 8192)];
                int remaining = length - 2;
                while (remaining > 0)
                {
                    int toRead = Math.Min(remaining, skip.Length);
                    int read = stream.Read(skip, 0, toRead);
                    if (read == 0)
                    {
                        break;
                    }

                    remaining -= read;
                }
            }
        }
    }

    /// <summary>
    /// Reads an APP marker segment's data, capturing EXIF/ICC/IPTC/XMP if recognized.
    /// Falls back to skipping for unrecognized markers.
    /// </summary>
    private static void ReadOrSkipAppMarker(Stream stream, int marker,
        ref byte[]? exifData, ref List<byte[]>? iccChunks, ref byte[]? iptcData, ref string? xmpData)
    {
        int length = ReadUInt16(stream);
        int dataLen = length - 2;
        if (dataLen <= 0) return;

        byte[] data = new byte[dataLen];
        int totalRead = 0;
        while (totalRead < dataLen)
        {
            int read = stream.Read(data, totalRead, dataLen - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        ReadOnlySpan<byte> span = data.AsSpan(0, totalRead);

        switch (marker)
        {
            case APP1:
                // EXIF: starts with "Exif\0\0"
                if (span.Length >= 6 && span[..6].SequenceEqual("Exif\0\0"u8))
                {
                    exifData = data[..totalRead];
                }
                // XMP: starts with "http://ns.adobe.com/xap/1.0/\0"
                else if (span.Length > 29 && System.Text.Encoding.ASCII.GetString(data, 0, 29) == "http://ns.adobe.com/xap/1.0/\0")
                {
                    xmpData = System.Text.Encoding.UTF8.GetString(data, 29, totalRead - 29);
                }
                break;

            case APP2:
                // ICC Profile: starts with "ICC_PROFILE\0"
                if (span.Length >= 14 && span[..12].SequenceEqual("ICC_PROFILE\0"u8))
                {
                    // Bytes 12-13: chunk number and total chunks
                    iccChunks ??= [];
                    iccChunks.Add(data[14..totalRead]); // skip header + chunk info
                }
                break;

            case APP13:
                // IPTC / Photoshop: starts with "Photoshop 3.0\0"
                if (span.Length >= 14 && span[..14].SequenceEqual("Photoshop 3.0\0"u8))
                {
                    iptcData = data[..totalRead];
                }
                break;
        }
    }

    /// <summary>
    /// Attaches parsed metadata to an ImageFrame after decoding.
    /// </summary>
    private static void AttachMetadata(ImageFrame frame, byte[]? exifData, List<byte[]>? iccChunks,
        byte[]? iptcData, string? xmpData)
    {
        if (exifData is not null)
        {
            frame.Metadata.ExifProfile = ExifParser.ParseFromApp1(exifData);

            // Apply EXIF orientation to ImageFrame
            if (frame.Metadata.ExifProfile is not null)
            {
                var orientTag = frame.Metadata.ExifProfile.GetTag(ExifTag.Orientation);
                if (orientTag.HasValue)
                {
                    ushort orient = orientTag.Value.GetUInt16(frame.Metadata.ExifProfile.IsLittleEndian);
                    if (orient >= 1 && orient <= 8)
                        frame.Orientation = (OrientationType)orient;
                }
            }
        }

        if (iccChunks is { Count: > 0 })
        {
            // Reassemble ICC profile from chunks
            int totalLen = 0;
            foreach (var chunk in iccChunks) totalLen += chunk.Length;
            var iccData = new byte[totalLen];
            int offset = 0;
            foreach (var chunk in iccChunks)
            {
                chunk.CopyTo(iccData, offset);
                offset += chunk.Length;
            }
            frame.Metadata.IccProfile = new IccProfile(iccData);
        }

        if (iptcData is not null)
        {
            frame.Metadata.IptcProfile = IptcParser.ParseFromApp13(iptcData);
        }

        if (xmpData is not null)
        {
            frame.Metadata.Xmp = xmpData;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt16(Stream stream)
    {
        int high = stream.ReadByte();
        int low = stream.ReadByte();
        return (high << 8) | low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    #endregion

    private struct JpegComponent
    {
        public byte Id;
        public byte HSample;
        public byte VSample;
        public byte QuantTableIndex;
        public byte DcTableIndex;
        public byte AcTableIndex;
        public int[][] Blocks;
    }
}
