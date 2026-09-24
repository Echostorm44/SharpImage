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

/// <summary>Quantized DCT data of a baseline JPEG (for lossless DCT-domain transcode). Coefficients and
/// quantization tables are in natural (row-major) 8x8 order.</summary>
public sealed class JpegDctData
{
    public int Width;
    public int Height;
    public int ComponentCount;
    public int MaxHSample;
    public int MaxVSample;
    public int RestartInterval;
    public int[][] QuantTables = new int[4][];      // [tableIndex][64], natural order
    public JpegDctComponent[] Components = Array.Empty<JpegDctComponent>();

    // Byte-exact reconstruction (jbrd): everything before the entropy-coded scan data (SOI..SOS header) and
    // everything from the EOI marker onward, captured verbatim. RebuildJpeg re-encodes only the entropy stream
    // (from Components' coefficients + the Huffman tables parsed out of HeaderBytes) between them.
    public byte[] HeaderBytes = Array.Empty<byte>();
    public byte[] TrailingBytes = Array.Empty<byte>();

    // Progressive (SOF2): the coefficients are the FINAL values after all scans. Each scan is re-emitted from
    // them in order (its verbatim prefix — inter-scan markers + SOS header — then its re-encoded entropy).
    public bool Progressive;
    public System.Collections.Generic.List<JpegScan> Scans = new();
}

/// <summary>One progressive scan: its spectral/approximation band, participating components, and the verbatim
/// bytes (marker segments + SOS header) that precede its entropy-coded data.</summary>
public sealed class JpegScan
{
    public int Ss;
    public int Se;
    public int Ah;
    public int Al;
    public int RestartInterval;                          // DRI in effect for this scan (0 = none)
    public int[] ComponentIndices = Array.Empty<int>(); // indices into JpegDctData.Components
    public int[] CompDcTable = Array.Empty<int>();       // DC Huffman selector per scan component
    public int[] CompAcTable = Array.Empty<int>();       // AC Huffman selector per scan component
    public byte[] Prefix = Array.Empty<byte>();          // markers + SOS header emitted before the entropy data
}

/// <summary>One component's quantized DCT blocks (natural order), sampling factors and quant-table index.</summary>
public sealed class JpegDctComponent
{
    public int Id;
    public int HSample;
    public int VSample;
    public int QuantTableIndex;
    public int DcTableIndex;                          // DC Huffman table selector (from SOS)
    public int AcTableIndex;                          // AC Huffman table selector (from SOS)
    public int BlocksPerRow;                          // mcuCols * HSample
    public int BlocksPerCol;                          // mcuRows * VSample
    public int[][] Blocks = Array.Empty<int[]>();     // [BlocksPerRow*BlocksPerCol][64], quantized, natural order
}

/// <summary>
/// Pure C# JPEG reader/writer (ITU-T T.81 / ISO 10918-1). Reading decodes everything libjpeg-turbo 3.1 does,
/// pixel-identically: baseline / extended / progressive DCT at 8 or 12 bits with Huffman or arithmetic coding,
/// lossless (SOF3, 2..16 bits), greyscale / YCbCr / RGB / CMYK / YCCK, any integral sampling. Writing is libjpeg-turbo's
/// compressor, byte-identical to cjpeg, for the same range of files (see <see cref="JpegEncodeOptions"/>).
/// Not supported (as in libjpeg-turbo): hierarchical and arithmetic-lossless files, JPEG 2000 / XL.
/// </summary>
public static partial class JpegCoder
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
    private const byte SOF1 = 0xC1; // Extended sequential DCT (Huffman; decoded like baseline at 8-bit precision)
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

    /// <summary>
    /// Decodes a JPEG as libjpeg-turbo 3.1 does by default (accurate integer IDCT, fancy chroma upsampling, fixed-point
    /// colour conversion), so the pixels are identical to djpeg / Pillow / browsers built on it: baseline, extended and
    /// progressive files with Huffman or arithmetic coding at 8 or 12 bits, lossless files (2..16 bits), grey / YCbCr /
    /// RGB and CMYK / YCCK (returned as CMYK). Malformed files throw <see cref="InvalidDataException"/> where
    /// libjpeg-turbo fails; files libjpeg-turbo cannot decode either (hierarchical, arithmetic lossless, fractional
    /// sampling) throw <see cref="NotSupportedException"/>. Images over <see cref="DefaultMaxPixels"/> are rejected.
    /// </summary>
    public static ImageFrame Read(Stream stream) => Read(stream, DefaultMaxPixels);

    /// <summary><see cref="Read(Stream)"/> with a custom pixel limit (width x height).</summary>
    public static ImageFrame Read(Stream stream, long maxPixels)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }
        var frame = ReadLibjpegExact(data, maxPixels);

        byte[]? exifData = null, iptcData = null;
        List<byte[]>? iccChunks = null, extendedXmp = null;
        string? xmpData = null;
        var ms2 = new MemoryStream(data);
        foreach (var (marker, off, _) in ScanSegments(data, 0))
        {
            if (marker is < 0xE0 or > 0xEF) continue;
            ms2.Position = off - 2;
            ReadOrSkipAppMarker(ms2, marker, ref exifData, ref iccChunks, ref iptcData, ref xmpData, ref extendedXmp);
        }
        AttachMetadata(frame, exifData, iccChunks, iptcData, xmpData, extendedXmp);
        return frame;
    }

    // The general decoder (float IDCT, simple upsampling): CMYK / YCCK and layouts the libjpeg-exact path skips.
    private static ImageFrame ReadGeneral(Stream stream)
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
        List<byte[]>? extendedXmp = null;

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
                case SOF1: // Extended sequential DCT (e.g. 16-bit quantisation tables)
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
                        AttachMetadata(frame, exifData, iccChunks, iptcData, xmpData, extendedXmp);
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
                        maxHSample, maxVSample, restartInterval, scanInfo, width, height);
                    break;

                default:
                    // Capture metadata from APP markers before skipping
                    ReadOrSkipAppMarker(stream, marker, ref exifData, ref iccChunks, ref iptcData, ref xmpData, ref extendedXmp);
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
            AttachMetadata(progFrame, exifData, iccChunks, iptcData, xmpData, extendedXmp);
            return progFrame;
        }

        throw new InvalidDataException("JPEG missing SOS marker.");
    }

    /// <summary>
    /// Parses a baseline (SOF0) JPEG into its quantized DCT coefficients, quantization tables and sampling
    /// factors — the raw data needed for a lossless DCT-domain transcode (e.g. JPEG->JXL recompression),
    /// without dequantising or running the IDCT. Progressive JPEGs are not yet supported here.
    /// </summary>
    public static JpegDctData ReadDctData(Stream input)
    {
        using var buf = new MemoryStream();
        input.CopyTo(buf);
        byte[] data = buf.ToArray();
        var stream = new MemoryStream(data);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != SOI)
        {
            throw new InvalidDataException("Not a valid JPEG file (missing SOI marker).");
        }

        int width = 0, height = 0, componentCount = 0, restartInterval = 0;
        bool isProgressive = false;
        var components = new JpegComponent[MaxComponents];
        int maxHSample = 1, maxVSample = 1;
        var quantTables = new int[4][];
        var dcTables = new HuffmanTable[4];
        var acTables = new HuffmanTable[4];

        while (true)
        {
            int marker = ReadMarker(stream);
            if (marker < 0 || marker == EOI)
            {
                break;
            }

            switch (marker)
            {
                case SOF0:
                case SOF1:
                    ReadSof(stream, ref width, ref height, ref componentCount, components, ref maxHSample, ref maxVSample);
                    break;
                case SOF2:
                    isProgressive = true;
                    ReadSof(stream, ref width, ref height, ref componentCount, components, ref maxHSample, ref maxVSample);
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
                case SOS when isProgressive:
                    return ReadProgressiveDctData(stream, data, marker, width, height, componentCount, components,
                        quantTables, dcTables, acTables, maxHSample, maxVSample, restartInterval);
                case SOS:
                    ReadSosHeader(stream, components, componentCount);
                    int scanStart = (int)stream.Position; // entropy-coded data begins here (SOS header captured verbatim)
                    int mcuCols = DecodeScanBlocks(stream, width, height, componentCount, components, quantTables,
                        dcTables, acTables, maxHSample, maxVSample, restartInterval, coefficientsOnly: true);
                    int mcuWidth = maxHSample * BlockSize, mcuHeight = maxVSample * BlockSize;
                    int mcuRows = (height + mcuHeight - 1) / mcuHeight;
                    int eoiPos = FindScanEnd(data, scanStart);
                    var dct = new JpegDctData
                    {
                        Width = width,
                        Height = height,
                        ComponentCount = componentCount,
                        MaxHSample = maxHSample,
                        MaxVSample = maxVSample,
                        RestartInterval = restartInterval,
                        QuantTables = quantTables,
                        Components = new JpegDctComponent[componentCount],
                        HeaderBytes = data[..scanStart],
                        TrailingBytes = data[eoiPos..],
                    };
                    for (int c = 0; c < componentCount; c++)
                    {
                        dct.Components[c] = new JpegDctComponent
                        {
                            Id = components[c].Id,
                            HSample = components[c].HSample,
                            VSample = components[c].VSample,
                            QuantTableIndex = components[c].QuantTableIndex,
                            DcTableIndex = components[c].DcTableIndex,
                            AcTableIndex = components[c].AcTableIndex,
                            BlocksPerRow = mcuCols * components[c].HSample,
                            BlocksPerCol = mcuRows * components[c].VSample,
                            Blocks = components[c].Blocks,
                        };
                    }

                    return dct;
                default:
                    SkipMarkerSegment(stream);
                    break;
            }
        }

        throw new InvalidDataException("JPEG missing SOS marker.");
    }

    // Progressive (SOF2) DCT extraction: iterates every scan, decoding into the shared final coefficient
    // blocks, and captures each scan's spectral/approximation band + the verbatim bytes (inter-scan markers +
    // SOS header) preceding its entropy data, so RebuildJpeg can re-emit the whole multi-scan file byte-exact.
    // Entered with `stream` positioned just after the FIRST SOS marker's two bytes.
    private static JpegDctData ReadProgressiveDctData(MemoryStream stream, byte[] data, int marker,
        int width, int height, int componentCount, JpegComponent[] components,
        int[][] quantTables, HuffmanTable[] dcTables, HuffmanTable[] acTables,
        int maxHSample, int maxVSample, int restartInterval)
    {
        int mcuWidth = maxHSample * BlockSize, mcuHeight = maxVSample * BlockSize;
        int mcuCols = (width + mcuWidth - 1) / mcuWidth;
        int mcuRows = (height + mcuHeight - 1) / mcuHeight;
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

        var scans = new List<JpegScan>();
        int prevEnd = 0; // start of file: scan 0's prefix carries SOI..first-SOS-header verbatim
        while (true)
        {
            if (marker == SOS)
            {
                ScanInfo scanInfo = ReadSosHeaderProgressive(stream, components, componentCount);
                int entropyStart = (int)stream.Position;
                var scan = new JpegScan
                {
                    Ss = scanInfo.Ss,
                    Se = scanInfo.Se,
                    Ah = scanInfo.Ah,
                    Al = scanInfo.Al,
                    RestartInterval = restartInterval,
                    ComponentIndices = (int[])scanInfo.ComponentIndices.Clone(),
                    CompDcTable = new int[scanInfo.ComponentCount],
                    CompAcTable = new int[scanInfo.ComponentCount],
                    Prefix = data[prevEnd..entropyStart],
                };
                for (int si = 0; si < scanInfo.ComponentCount; si++)
                {
                    int c = scanInfo.ComponentIndices[si];
                    scan.CompDcTable[si] = components[c].DcTableIndex;
                    scan.CompAcTable[si] = components[c].AcTableIndex;
                }

                scans.Add(scan);
                DecodeProgressiveScan(stream, componentCount, components, dcTables, acTables,
                    mcuCols, mcuRows, maxHSample, maxVSample, restartInterval, scanInfo, width, height);
                int entropyEnd = FindScanEnd(data, entropyStart);
                prevEnd = entropyEnd;
                stream.Position = entropyEnd;
            }
            else if (marker == DHT)
            {
                ReadDht(stream, dcTables, acTables);
            }
            else if (marker == DQT)
            {
                ReadDqt(stream, quantTables);
            }
            else if (marker == DRI)
            {
                ReadDri(stream, ref restartInterval);
            }
            else if (marker < 0 || marker == EOI)
            {
                break;
            }
            else
            {
                SkipMarkerSegment(stream);
            }

            marker = ReadMarker(stream);
        }

        var dct = new JpegDctData
        {
            Width = width,
            Height = height,
            ComponentCount = componentCount,
            MaxHSample = maxHSample,
            MaxVSample = maxVSample,
            RestartInterval = restartInterval,
            QuantTables = quantTables,
            Progressive = true,
            Scans = scans,
            Components = new JpegDctComponent[componentCount],
            HeaderBytes = scans.Count > 0 ? scans[0].Prefix : Array.Empty<byte>(),
            TrailingBytes = data[prevEnd..],
        };
        for (int c = 0; c < componentCount; c++)
        {
            dct.Components[c] = new JpegDctComponent
            {
                Id = components[c].Id,
                HSample = components[c].HSample,
                VSample = components[c].VSample,
                QuantTableIndex = components[c].QuantTableIndex,
                DcTableIndex = components[c].DcTableIndex,
                AcTableIndex = components[c].AcTableIndex,
                BlocksPerRow = mcuCols * components[c].HSample,
                BlocksPerCol = mcuRows * components[c].VSample,
                Blocks = components[c].Blocks,
            };
        }

        return dct;
    }

    /// <summary>
    /// Reconstructs the derivable metadata — dimensions, sampling factors, per-component sampling / quant-table
    /// / Huffman-table indices, block grid and restart interval — from a captured JPEG header (SOI..SOS). A
    /// recompression container need not store any of this alongside the verbatim header; it is re-parsed here.
    /// </summary>
    internal static void PopulateDctMetadata(JpegDctData d)
    {
        var stream = new MemoryStream(d.HeaderBytes);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != SOI)
        {
            throw new InvalidDataException("SharpImage lossless-JPEG header missing SOI marker.");
        }

        int width = 0, height = 0, componentCount = 0, restartInterval = 0, maxH = 1, maxV = 1;
        var comps = new JpegComponent[MaxComponents];
        bool sawSos = false;
        while (!sawSos)
        {
            int marker = ReadMarker(stream);
            if (marker < 0)
            {
                break;
            }

            switch (marker)
            {
                case SOF0:
                case SOF1:
                case SOF2:
                    ReadSof(stream, ref width, ref height, ref componentCount, comps, ref maxH, ref maxV);
                    break;
                case DRI:
                    ReadDri(stream, ref restartInterval);
                    break;
                case SOS:
                    ReadSosHeader(stream, comps, componentCount); // sets each scan component's Dc/AcTableIndex
                    sawSos = true;
                    break;
                default:
                    SkipMarkerSegment(stream);
                    break;
            }
        }

        int mcuCols = (width + (maxH * BlockSize) - 1) / (maxH * BlockSize);
        int mcuRows = (height + (maxV * BlockSize) - 1) / (maxV * BlockSize);
        d.Width = width;
        d.Height = height;
        d.MaxHSample = maxH;
        d.MaxVSample = maxV;
        d.RestartInterval = restartInterval;
        d.ComponentCount = componentCount;
        d.Components = new JpegDctComponent[componentCount];
        for (int i = 0; i < componentCount; i++)
        {
            d.Components[i] = new JpegDctComponent
            {
                Id = comps[i].Id,
                HSample = comps[i].HSample,
                VSample = comps[i].VSample,
                QuantTableIndex = comps[i].QuantTableIndex,
                DcTableIndex = comps[i].DcTableIndex,
                AcTableIndex = comps[i].AcTableIndex,
                BlocksPerRow = mcuCols * comps[i].HSample,
                BlocksPerCol = mcuRows * comps[i].VSample,
            };
        }
    }

    // Finds the byte offset of the marker that terminates the entropy-coded scan (EOI for baseline), skipping
    // byte-stuffed 0xFF00 and RSTn restart markers embedded in the data.
    private static int FindScanEnd(byte[] data, int start)
    {
        for (int i = start; i < data.Length - 1; i++)
        {
            if (data[i] != 0xFF)
            {
                continue;
            }

            byte m = data[i + 1];
            if (m == 0x00 || (m >= 0xD0 && m <= 0xD7))
            {
                i++; // stuffing or RSTn — part of the scan
                continue;
            }

            return i; // real marker (EOI for a single-scan baseline JPEG)
        }

        return data.Length;
    }

    /// <summary>
    /// Byte-exact inverse of <see cref="ReadDctData(Stream)"/>: re-emits the verbatim header, re-encodes the
    /// entropy-coded scan from the quantized coefficients using the JPEG's own Huffman tables (parsed from the
    /// header), then re-emits the verbatim trailer — reproducing the original baseline JPEG byte-for-byte.
    /// </summary>
    public static byte[] RebuildJpeg(JpegDctData d)
    {
        if (d.Progressive)
        {
            return RebuildProgressiveJpeg(d);
        }

        var dcEnc = new (int Code, int Len)[4][];
        var acEnc = new (int Code, int Len)[4][];
        ParseHuffmanEncodeTables(d.HeaderBytes, dcEnc, acEnc);

        using var ms = new MemoryStream();
        ms.Write(d.HeaderBytes, 0, d.HeaderBytes.Length);
        EncodeEntropyScan(ms, d, dcEnc, acEnc);
        ms.Write(d.TrailingBytes, 0, d.TrailingBytes.Length);
        return ms.ToArray();
    }

    // Re-encodes the interleaved baseline scan (same MCU/block order as DecodeScanBlocks), inserting restart
    // markers + DC-predictor resets at the restart interval, with 1-bit padding before each marker.
    private static void EncodeEntropyScan(Stream stream, JpegDctData d, (int Code, int Len)[][] dcEnc, (int Code, int Len)[][] acEnc)
    {
        int mcuWidth = d.MaxHSample * BlockSize, mcuHeight = d.MaxVSample * BlockSize;
        int mcuCols = (d.Width + mcuWidth - 1) / mcuWidth;
        int mcuRows = (d.Height + mcuHeight - 1) / mcuHeight;
        var dcPred = new int[d.ComponentCount];
        var writer = new JpegBitWriter(stream);
        int mcuCount = 0, restartCounter = 0;

        for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
        {
            for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
            {
                if (d.RestartInterval > 0 && mcuCount > 0 && mcuCount % d.RestartInterval == 0)
                {
                    writer.Flush();
                    stream.WriteByte(0xFF);
                    stream.WriteByte((byte)(0xD0 + (restartCounter & 7)));
                    Array.Clear(dcPred);
                    restartCounter++;
                }

                for (int c = 0; c < d.ComponentCount; c++)
                {
                    JpegDctComponent comp = d.Components[c];
                    int blocksPerRow = mcuCols * comp.HSample;
                    for (int bv = 0; bv < comp.VSample; bv++)
                    {
                        for (int bh = 0; bh < comp.HSample; bh++)
                        {
                            int blockRow = (mcuRow * comp.VSample) + bv;
                            int blockCol = (mcuCol * comp.HSample) + bh;
                            int[] block = comp.Blocks[(blockRow * blocksPerRow) + blockCol];
                            EncodeDcCoefficient(writer, block[0] - dcPred[c], dcEnc[comp.DcTableIndex]);
                            dcPred[c] = block[0];
                            EncodeAcCoefficients(writer, block, acEnc[comp.AcTableIndex]);
                        }
                    }
                }

                mcuCount++;
            }
        }

        writer.Flush();
    }

    // Parses every DHT segment out of the verbatim header into canonical (code,length)-per-symbol encode
    // tables, indexed [tableIndex][symbol]. dcEnc/acEnc are 4-slot arrays (JPEG allows table indices 0..3).
    private static void ParseHuffmanEncodeTables(byte[] header, (int Code, int Len)[][] dcEnc, (int Code, int Len)[][] acEnc) =>
        ParseHuffmanEncodeTables(header, 2, dcEnc, acEnc);

    // Scans `header` from `start` for DHT segments, filling/overwriting the encode tables (progressive files may
    // (re)define Huffman tables between scans; call this cumulatively over each scan prefix, later defs winning).
    private static void ParseHuffmanEncodeTables(byte[] header, int start, (int Code, int Len)[][] dcEnc, (int Code, int Len)[][] acEnc)
    {
        int p = start;
        while (p + 4 <= header.Length)
        {
            if (header[p] != 0xFF)
            {
                p++;
                continue;
            }

            byte marker = header[p + 1];
            if (marker == DHT)
            {
                int len = (header[p + 2] << 8) | header[p + 3];
                int seg = p + 4, end = p + 2 + len;
                while (seg < end)
                {
                    int tcth = header[seg++];
                    int tclass = tcth >> 4, tindex = tcth & 0x0F;
                    var counts = new byte[16];
                    int total = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        counts[i] = header[seg + i];
                        total += counts[i];
                    }

                    seg += 16;
                    var values = new byte[total];
                    Array.Copy(header, seg, values, 0, total);
                    seg += total;

                    var enc = BuildEncodeTable(counts, values);
                    if (tclass == 0)
                    {
                        dcEnc[tindex] = enc;
                    }
                    else
                    {
                        acEnc[tindex] = enc;
                    }
                }

                p = end;
            }
            else if (marker == SOS || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7))
            {
                p += 2;
            }
            else
            {
                int len = (header[p + 2] << 8) | header[p + 3];
                p += 2 + len;
            }
        }
    }

    // Canonical JPEG Huffman codes (T.81 Annex C): assign increasing codes by length, in `values` order.
    private static (int Code, int Len)[] BuildEncodeTable(byte[] counts, byte[] values)
    {
        var enc = new (int Code, int Len)[256];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < counts[len - 1]; i++)
            {
                enc[values[k++]] = (code, len);
                code++;
            }

            code <<= 1;
        }

        return enc;
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
        int restartInterval, ScanInfo scan, int width, int height)
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
                        bitReader.ConsumeRestartMarker();
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
            // AC scan (Ss>0): always single-component (non-interleaved). A non-interleaved scan
            // iterates the component's OWN block grid — ceil(x_i/8) × ceil(y_i/8) — NOT the
            // MCU-padded grid. (Interleaved scans pad to MCU and code dummy edge blocks; a
            // non-interleaved scan does not, so using the padded width reads phantom blocks that
            // aren't in the bitstream and desyncs everything after the first line.)
            int c = scan.ComponentIndices[0];
            // Storage grid stride (blocks were allocated MCU-padded: mcuCols*HSample wide).
            int strideH = mcuCols * components[c].HSample;
            // Component sample dims x_i = ceil(width*Hi/Hmax), y_i = ceil(height*Vi/Vmax), then /8.
            int compSamplesW = (width * components[c].HSample + maxHSample - 1) / maxHSample;
            int compSamplesH = (height * components[c].VSample + maxVSample - 1) / maxVSample;
            int totalBlocksH = (compSamplesW + BlockSize - 1) / BlockSize;
            int totalBlocksV = (compSamplesH + BlockSize - 1) / BlockSize;

            for (int blockRow = 0; blockRow < totalBlocksV; blockRow++)
            {
                for (int blockCol = 0; blockCol < totalBlocksH; blockCol++)
                {
                    if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                    {
                        bitReader.ConsumeRestartMarker();
                        eobRun = 0;
                    }

                    int blockIndex = blockRow * strideH + blockCol;
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

                    // eobRun now holds the count of FOLLOWING blocks covered by this EOB run (the current
                    // block was just refined above). Matches the AC-first convention — do NOT decrement again.
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
        int mcols = DecodeScanBlocks(stream, width, height, componentCount, components, quantTables,
            dcTables, acTables, maxHSample, maxVSample, restartInterval);
        return BlocksToImage(width, height, componentCount, components, maxHSample, maxVSample, mcols);
    }

    // Decodes a baseline scan into per-component quantized-coefficient blocks (natural order) on
    // components[c].Blocks, without dequantising or running the IDCT. Returns mcuCols. Shared by the pixel
    // decoder (DecodeScanData) and the DCT extractor (ReadDctData).
    private static int DecodeScanBlocks(Stream stream, int width, int height,
        int componentCount, JpegComponent[] components, int[][] quantTables,
        HuffmanTable[] dcTables, HuffmanTable[] acTables,
        int maxHSample, int maxVSample, int restartInterval, bool coefficientsOnly = false)
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
                    bitReader.ConsumeRestartMarker();     // byte-align + consume the RSTn marker
                    Array.Clear(dcPredictors);            // DC prediction resets across each interval
                    restartCounter++;
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
                                quantTables[components[c].QuantTableIndex], coefficientsOnly);
                        }
                    }
                }
                mcuCount++;
            }
        }

        return mcuCols;
    }

    private static void DecodeBlock(JpegBitReader reader, int[] block, ref int dcPredictor,
        HuffmanTable dcTable, HuffmanTable acTable, int[] quantTable, bool coefficientsOnly = false)
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

        if (coefficientsOnly)
        {
            return; // keep the raw quantized coefficients (for DCT-domain transcode)
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

    /// <summary>Writes a baseline JPEG with libjpeg-turbo's compressor (<see cref="Encode(ImageFrame, JpegEncodeOptions)"/>),
    /// keeping the image's EXIF, XMP, ICC and IPTC metadata.</summary>
    public static void Write(ImageFrame image, string path, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        Write(image, stream, quality, subsampling, optimizeHuffman);
    }

    /// <summary>Writes a progressive JPEG (libjpeg's default progression, optimized Huffman tables), keeping the image's
    /// metadata.</summary>
    public static void WriteProgressive(ImageFrame image, string path, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        WriteProgressive(image, stream, quality, subsampling, optimizeHuffman);
    }

    /// <summary>Writes a progressive JPEG to a stream.</summary>
    public static void WriteProgressive(ImageFrame image, Stream stream, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false) =>
        Write(image, stream, SimpleOptions(image, quality, subsampling, optimizeHuffman, progressive: true));

    /// <summary>Writes a baseline JPEG to a stream.</summary>
    public static void Write(ImageFrame image, Stream stream, int quality = 85,
        JpegSubsampling subsampling = JpegSubsampling.Yuv444, bool optimizeHuffman = false) =>
        Write(image, stream, SimpleOptions(image, quality, subsampling, optimizeHuffman, progressive: false));

    private static JpegEncodeOptions SimpleOptions(ImageFrame image, int quality, JpegSubsampling subsampling, bool optimize, bool progressive)
    {
        bool grey = image.NumberOfChannels - (image.HasAlpha ? 1 : 0) == 1;
        return new JpegEncodeOptions
        {
            Quality = Math.Clamp(quality, 1, 100),
            ForceBaseline = true,
            SamplingFactors = grey ? null : subsampling switch
            {
                JpegSubsampling.Yuv420 => [(2, 2)],
                JpegSubsampling.Yuv422 => [(2, 1)],
                _ => [(1, 1)],
            },
            OptimizeCoding = optimize,
            Progressive = progressive,
            WriteMetadata = true,
        };
    }

    // Huffman coding of one block's coefficients (used by RebuildJpeg).

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
        ref byte[]? exifData, ref List<byte[]>? iccChunks, ref byte[]? iptcData, ref string? xmpData,
        ref List<byte[]>? extendedXmp)
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
                // Extended XMP (Adobe XMP Part 3): merged into the standard packet after all markers are read.
                else if (span.Length > 35 && span[..35].SequenceEqual("http://ns.adobe.com/xmp/extension/ "u8))
                {
                    (extendedXmp ??= []).Add(data[..totalRead]);
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
        byte[]? iptcData, string? xmpData, List<byte[]>? extendedXmp = null)
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
            frame.Metadata.Xmp = extendedXmp != null ? MergeExtendedXmp(xmpData.TrimEnd(' '), extendedXmp) ?? xmpData : xmpData;
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
