using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Metadata;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Pure C# PNG reader/writer supporting all color types, bit depths 1-16, all five filter types, tRNS transparency,
/// gAMA, and pHYs chunks. Uses System.IO.Compression.ZLibStream for deflate (part of .NET BCL).
/// </summary>
public static class PngCoder
{
    // PNG file signature (8 bytes)
    private static readonly byte[] PngSignature = [ 137, 80, 78, 71, 13, 10, 26, 10 ];

    /// <summary>
    /// Detect PNG format by signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    // Chunk type codes (big-endian ASCII)
    private const uint ChunkIHDR = 0x49484452;
    private const uint ChunkPLTE = 0x504C5445;
    private const uint ChunkIDAT = 0x49444154;
    private const uint ChunkIEND = 0x49454E44;
    private const uint ChunktRNS = 0x74524E53;
    private const uint ChunkgAMA = 0x67414D41;
    private const uint ChunkpHYs = 0x70485973;
    private const uint ChunksRGB = 0x73524742;
    private const uint ChunkiCCP = 0x69434350; // ICC profile
    private const uint ChunktEXt = 0x74455874; // Text metadata
    private const uint ChunkiTXt = 0x69545874; // International text
    private const uint ChunkeXIf = 0x65584966; // EXIF data

    // APNG chunk types
    private const uint ChunkacTL = 0x6163544C; // Animation Control
    private const uint ChunkfcTL = 0x6663544C; // Frame Control
    private const uint ChunkfdAT = 0x66644154; // Frame Data

    // PNG color type flags
    private const byte ColorTypeGrayscale = 0;
    private const byte ColorTypeRgb = 2;
    private const byte ColorTypeIndexed = 3;
    private const byte ColorTypeGrayscaleAlpha = 4;
    private const byte ColorTypeRgba = 6;

    // PNG filter types
    private const byte FilterNone = 0;
    private const byte FilterSub = 1;
    private const byte FilterUp = 2;
    private const byte FilterAverage = 3;
    private const byte FilterPaeth = 4;

    #region Read

    public static ImageFrame Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        return Read(stream);
    }

    public static ImageFrame Read(Stream stream)
    {
        // Verify PNG signature
        Span<byte> signature = stackalloc byte[8];
        ReadExact(stream, signature);
        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("Not a valid PNG file.");
        }

        // IHDR data
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlaceMethod = 0;

        // Optional chunk data
        byte[]? palette = null;
        byte[]? transparencyData = null;
        double gamma = 0.0;
        int dpiX = 0, dpiY = 0;

        // Metadata collected from chunks
        byte[]? iccProfileData = null;
        byte[]? exifChunkData = null;
        Dictionary<string, string>? textEntries = null;

        // Collect all IDAT chunk data
        using var idatStream = new MemoryStream();

        // Read chunks
        Span<byte> chunkHeader = stackalloc byte[8]; // 4 length + 4 type
        Span<byte> crcBuf = stackalloc byte[4];

        while (true)
        {
            ReadExact(stream, chunkHeader);
            uint chunkLength = ReadUInt32BigEndian(chunkHeader);
            uint chunkType = ReadUInt32BigEndian(chunkHeader[4..]);

            if (chunkLength > 0x7FFFFFFF)
            {
                throw new InvalidDataException("PNG chunk length exceeds maximum.");
            }

            int dataLength = (int)chunkLength;

            if (chunkType == ChunkIHDR)
            {
                byte[] ihdrData = ArrayPool<byte>.Shared.Rent(dataLength);
                try
                {
                    ReadExact(stream, ihdrData.AsSpan(0, dataLength));
                    VerifyChunkCrc(stream, chunkHeader[4..], ihdrData.AsSpan(0, dataLength), crcBuf);

                    width = (int)ReadUInt32BigEndian(ihdrData);
                    height = (int)ReadUInt32BigEndian(ihdrData.AsSpan(4));
                    if (width <= 0 || height <= 0 || width > 0x7FFFFFFF || height > 0x7FFFFFFF)
                        throw new InvalidDataException($"Invalid PNG dimensions: {width}x{height}");
                    bitDepth = ihdrData[8];
                    colorType = ihdrData[9];
                    // ihdrData[10] = compression method (always 0 = deflate)
                    // ihdrData[11] = filter method (always 0 = adaptive)
                    interlaceMethod = ihdrData[12];
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(ihdrData);
                }
            }
            else if (chunkType == ChunkPLTE)
            {
                palette = new byte[dataLength];
                ReadExact(stream, palette);
                VerifyChunkCrc(stream, chunkHeader[4..], palette, crcBuf);
            }
            else if (chunkType == ChunktRNS)
            {
                transparencyData = new byte[dataLength];
                ReadExact(stream, transparencyData);
                VerifyChunkCrc(stream, chunkHeader[4..], transparencyData, crcBuf);
            }
            else if (chunkType == ChunkgAMA)
            {
                byte[] gamaData = ArrayPool<byte>.Shared.Rent(dataLength);
                try
                {
                    ReadExact(stream, gamaData.AsSpan(0, dataLength));
                    VerifyChunkCrc(stream, chunkHeader[4..], gamaData.AsSpan(0, dataLength), crcBuf);
                    uint gamaValue = ReadUInt32BigEndian(gamaData);
                    gamma = gamaValue / 100000.0;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(gamaData);
                }
            }
            else if (chunkType == ChunkpHYs)
            {
                byte[] physData = ArrayPool<byte>.Shared.Rent(dataLength);
                try
                {
                    ReadExact(stream, physData.AsSpan(0, dataLength));
                    VerifyChunkCrc(stream, chunkHeader[4..], physData.AsSpan(0, dataLength), crcBuf);
                    uint ppuX = ReadUInt32BigEndian(physData);
                    uint ppuY = ReadUInt32BigEndian(physData.AsSpan(4));
                    byte unit = physData[8];
                    if (unit == 1) // Meter
                    {
                        dpiX = (int)Math.Round(ppuX / 39.3701);
                        dpiY = (int)Math.Round(ppuY / 39.3701);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(physData);
                }
            }
            else if (chunkType == ChunkIDAT)
            {
                // Stream IDAT data into our collector
                byte[] idatData = ArrayPool<byte>.Shared.Rent(dataLength);
                try
                {
                    ReadExact(stream, idatData.AsSpan(0, dataLength));
                    VerifyChunkCrc(stream, chunkHeader[4..], idatData.AsSpan(0, dataLength), crcBuf);
                    idatStream.Write(idatData, 0, dataLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(idatData);
                }
            }
            else if (chunkType == ChunkIEND)
            {
                // Skip CRC for IEND (no data)
                if (dataLength > 0)
                {
                    byte[] skip = ArrayPool<byte>.Shared.Rent(dataLength);
                    try
                    {
                        ReadExact(stream, skip.AsSpan(0, dataLength));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(skip);
                    }
                }
                // Read CRC
                ReadExact(stream, crcBuf);
                break;
            }
            else if (chunkType == ChunkiCCP)
            {
                // ICC profile chunk
                byte[] iccpData = new byte[dataLength];
                ReadExact(stream, iccpData);
                VerifyChunkCrc(stream, chunkHeader[4..], iccpData, crcBuf);

                // Format: keyword\0, compression method (1 byte), compressed data
                int nullPos = Array.IndexOf(iccpData, (byte)0);
                if (nullPos >= 0 && nullPos + 2 < dataLength)
                {
                    using var compressedStream = new MemoryStream(iccpData, nullPos + 2, dataLength - nullPos - 2);
                    using var iccZlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
                    using var iccDecompressed = new MemoryStream();
                    iccZlibStream.CopyTo(iccDecompressed);
                    iccProfileData = iccDecompressed.ToArray();
                }
            }
            else if (chunkType == ChunktEXt)
            {
                byte[] textData = new byte[dataLength];
                ReadExact(stream, textData);
                VerifyChunkCrc(stream, chunkHeader[4..], textData, crcBuf);

                int nullPos = Array.IndexOf(textData, (byte)0);
                if (nullPos >= 0)
                {
                    string keyword = System.Text.Encoding.Latin1.GetString(textData, 0, nullPos);
                    string value = System.Text.Encoding.Latin1.GetString(textData, nullPos + 1, dataLength - nullPos - 1);
                    textEntries ??= [];
                    textEntries[keyword] = value;
                }
            }
            else if (chunkType == ChunkiTXt)
            {
                byte[] itxtData = new byte[dataLength];
                ReadExact(stream, itxtData);
                VerifyChunkCrc(stream, chunkHeader[4..], itxtData, crcBuf);

                int nullPos = Array.IndexOf(itxtData, (byte)0);
                if (nullPos >= 0 && nullPos + 3 < dataLength)
                {
                    string keyword = System.Text.Encoding.Latin1.GetString(itxtData, 0, nullPos);
                    byte compressionFlag = itxtData[nullPos + 1];

                    int langStart = nullPos + 3;
                    int langEnd = Array.IndexOf(itxtData, (byte)0, langStart);
                    if (langEnd < 0) langEnd = langStart;
                    int transStart = langEnd + 1;
                    int transEnd = Array.IndexOf(itxtData, (byte)0, transStart);
                    if (transEnd < 0) transEnd = transStart;
                    int textStart = transEnd + 1;

                    string value;
                    if (compressionFlag == 1 && textStart < dataLength)
                    {
                        using var compMs = new MemoryStream(itxtData, textStart, dataLength - textStart);
                        using var deflate = new DeflateStream(compMs, CompressionMode.Decompress);
                        using var decompMs = new MemoryStream();
                        deflate.CopyTo(decompMs);
                        value = System.Text.Encoding.UTF8.GetString(decompMs.ToArray());
                    }
                    else
                    {
                        value = System.Text.Encoding.UTF8.GetString(itxtData, textStart, dataLength - textStart);
                    }

                    textEntries ??= [];
                    textEntries[keyword] = value;
                }
            }
            else if (chunkType == ChunkeXIf)
            {
                exifChunkData = new byte[dataLength];
                ReadExact(stream, exifChunkData);
                VerifyChunkCrc(stream, chunkHeader[4..], exifChunkData, crcBuf);
            }
            else
            {
                // Skip unknown chunk data + CRC
                SkipBytes(stream, dataLength + 4);
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG missing IHDR chunk.");
        }

        if (interlaceMethod != 0)
        {
            throw new NotSupportedException("Adam7 interlaced PNGs are not yet supported.");
        }

        // Decompress all IDAT data
        idatStream.Position = 0;
        using var zlibStream = new ZLibStream(idatStream, CompressionMode.Decompress);
        byte[] decompressed = ReadAllBytes(zlibStream, width, height, bitDepth, colorType);

        // Create the image frame
        bool hasAlpha = colorType == ColorTypeGrayscaleAlpha || colorType == ColorTypeRgba ||
                        transparencyData != null;

        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        // Decode filtered scanlines into pixels
        DecodePixelData(image, decompressed, width, height, bitDepth, colorType, palette, transparencyData);

        // Attach collected metadata
        if (iccProfileData is not null)
            image.Metadata.IccProfile = new IccProfile(iccProfileData);

        if (exifChunkData is not null)
            image.Metadata.ExifProfile = ExifParser.ParseFromTiff(exifChunkData);

        if (textEntries is not null)
        {
            // Check for XMP stored as tEXt/iTXt with "XML:com.adobe.xmp" keyword
            if (textEntries.TryGetValue("XML:com.adobe.xmp", out string? xmpText))
                image.Metadata.Xmp = xmpText;
        }

        // Apply gamma and DPI
        if (gamma > 0) image.Gamma = gamma;
        if (dpiX > 0)
        {
            image.ResolutionX = dpiX;
            image.ResolutionY = dpiY;
        }

        return image;
    }

    /// <summary>
    /// Reads an animated PNG (APNG) file and returns all frames as individual ImageFrames.
    /// If the file is not animated, returns a single-element array with the static image.
    /// </summary>
    public static (ImageFrame[] Frames, int LoopCount, int[] DelaysMs) ReadSequence(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        return ReadSequence(stream);
    }

    /// <summary>
    /// Reads an animated PNG (APNG) from a stream.
    /// </summary>
    public static (ImageFrame[] Frames, int LoopCount, int[] DelaysMs) ReadSequence(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        ReadExact(stream, signature);
        if (!signature.SequenceEqual(PngSignature))
            throw new InvalidDataException("Not a valid PNG file.");

        // IHDR values
        int canvasWidth = 0, canvasHeight = 0;
        byte bitDepth = 0, colorType = 0;

        // APNG values
        int frameCount = 0, loopCount = 0;
        var frameControls = new List<ApngFrameControl>();
        var frameDataStreams = new List<MemoryStream>();
        using var defaultIdatStream = new MemoryStream();
        bool hasApng = false;
        int currentFcTlIndex = -1;
        bool defaultImageIsFirstFrame = false;
        // Optional data
        byte[]? palette = null;
        byte[]? transparencyData = null;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> crcBuf = stackalloc byte[4];

        while (true)
        {
            ReadExact(stream, chunkHeader);
            uint chunkLength = ReadUInt32BigEndian(chunkHeader);
            uint chunkType = ReadUInt32BigEndian(chunkHeader[4..]);
            int dataLength = (int)chunkLength;

            if (chunkType == ChunkIHDR)
            {
                byte[] ihdrData = new byte[dataLength];
                ReadExact(stream, ihdrData);
                VerifyChunkCrc(stream, chunkHeader[4..], ihdrData, crcBuf);
                canvasWidth = (int)ReadUInt32BigEndian(ihdrData);
                canvasHeight = (int)ReadUInt32BigEndian(ihdrData.AsSpan(4));
                bitDepth = ihdrData[8];
                colorType = ihdrData[9];
            }
            else if (chunkType == ChunkacTL)
            {
                byte[] actlData = new byte[dataLength];
                ReadExact(stream, actlData);
                VerifyChunkCrc(stream, chunkHeader[4..], actlData, crcBuf);
                frameCount = (int)ReadUInt32BigEndian(actlData);
                loopCount = (int)ReadUInt32BigEndian(actlData.AsSpan(4));
                hasApng = true;
            }
            else if (chunkType == ChunkfcTL)
            {
                byte[] fctlData = new byte[dataLength];
                ReadExact(stream, fctlData);
                VerifyChunkCrc(stream, chunkHeader[4..], fctlData, crcBuf);

                var fc = new ApngFrameControl
                {
                    SequenceNumber = ReadUInt32BigEndian(fctlData),
                    Width = (int)ReadUInt32BigEndian(fctlData.AsSpan(4)),
                    Height = (int)ReadUInt32BigEndian(fctlData.AsSpan(8)),
                    XOffset = (int)ReadUInt32BigEndian(fctlData.AsSpan(12)),
                    YOffset = (int)ReadUInt32BigEndian(fctlData.AsSpan(16)),
                    DelayNumerator = (ushort)((fctlData[20] << 8) | fctlData[21]),
                    DelayDenominator = (ushort)((fctlData[22] << 8) | fctlData[23]),
                    DisposeOp = fctlData[24],
                    BlendOp = fctlData[25]
                };
                frameControls.Add(fc);
                currentFcTlIndex = frameControls.Count - 1;

                if (currentFcTlIndex == 0 && frameDataStreams.Count == 0)
                    defaultImageIsFirstFrame = true;

                frameDataStreams.Add(new MemoryStream());
            }
            else if (chunkType == ChunkIDAT)
            {
                byte[] idatData = new byte[dataLength];
                ReadExact(stream, idatData);
                VerifyChunkCrc(stream, chunkHeader[4..], idatData, crcBuf);

                defaultIdatStream.Write(idatData);

                // If an fcTL appeared before IDAT, this IDAT is also frame 0's data
                if (defaultImageIsFirstFrame && frameDataStreams.Count > 0)
                    frameDataStreams[0].Write(idatData);
            }
            else if (chunkType == ChunkfdAT)
            {
                byte[] fdatData = new byte[dataLength];
                ReadExact(stream, fdatData);
                VerifyChunkCrc(stream, chunkHeader[4..], fdatData, crcBuf);

                // fdAT: 4-byte sequence number + compressed data (same as IDAT)
                if (currentFcTlIndex >= 0 && currentFcTlIndex < frameDataStreams.Count)
                    frameDataStreams[currentFcTlIndex].Write(fdatData, 4, dataLength - 4);
            }
            else if (chunkType == ChunkPLTE)
            {
                palette = new byte[dataLength];
                ReadExact(stream, palette);
                VerifyChunkCrc(stream, chunkHeader[4..], palette, crcBuf);
            }
            else if (chunkType == ChunktRNS)
            {
                transparencyData = new byte[dataLength];
                ReadExact(stream, transparencyData);
                VerifyChunkCrc(stream, chunkHeader[4..], transparencyData, crcBuf);
            }
            else if (chunkType == ChunkIEND)
            {
                if (dataLength > 0)
                    SkipBytes(stream, dataLength);
                ReadExact(stream, crcBuf);
                break;
            }
            else
            {
                SkipBytes(stream, dataLength + 4);
            }
        }

        if (!hasApng || frameCount <= 0)
        {
            // Not animated — decode as single frame
            defaultIdatStream.Position = 0;
            using var zlib = new ZLibStream(defaultIdatStream, CompressionMode.Decompress);
            byte[] decompressed = ReadAllBytes(zlib, canvasWidth, canvasHeight, bitDepth, colorType);
            bool hasAlpha = colorType == ColorTypeGrayscaleAlpha || colorType == ColorTypeRgba || transparencyData != null;
            var single = new ImageFrame();
            single.Initialize(canvasWidth, canvasHeight, ColorspaceType.SRGB, hasAlpha);
            DecodePixelData(single, decompressed, canvasWidth, canvasHeight, bitDepth, colorType, palette, transparencyData);
            return ([single], 0, [0]);
        }

        // Decode APNG frames
        var frames = new ImageFrame[frameControls.Count];
        var delaysMs = new int[frameControls.Count];

        // Canvas buffer for disposal operations
        var canvas = new ImageFrame();
        canvas.Initialize(canvasWidth, canvasHeight, ColorspaceType.SRGB, true);
        ClearFrame(canvas);

        for (int i = 0; i < frameControls.Count; i++)
        {
            var fc = frameControls[i];
            int delayMs = fc.DelayDenominator == 0
                ? (fc.DelayNumerator * 1000 / 100) // Default to 100 Hz
                : (fc.DelayNumerator * 1000 / fc.DelayDenominator);
            delaysMs[i] = delayMs;

            // Decode this frame's image data
            var frameStream = frameDataStreams[i];
            frameStream.Position = 0;
            using var frameZlib = new ZLibStream(frameStream, CompressionMode.Decompress);
            byte[] frameBytes = ReadAllBytes(frameZlib, fc.Width, fc.Height, bitDepth, colorType);

            bool hasAlpha = colorType == ColorTypeGrayscaleAlpha || colorType == ColorTypeRgba || transparencyData != null;
            var frameImage = new ImageFrame();
            frameImage.Initialize(fc.Width, fc.Height, ColorspaceType.SRGB, hasAlpha);
            DecodePixelData(frameImage, frameBytes, fc.Width, fc.Height, bitDepth, colorType, palette, transparencyData);

            // Save canvas state for APNG_DISPOSE_OP_PREVIOUS
            ImageFrame? previousCanvas = null;
            if (fc.DisposeOp == 2) // APNG_DISPOSE_OP_PREVIOUS
            {
                previousCanvas = new ImageFrame();
                previousCanvas.Initialize(canvasWidth, canvasHeight, ColorspaceType.SRGB, true);
                CopyFrame(canvas, previousCanvas);
            }

            // Blend frame onto canvas
            if (fc.BlendOp == 0) // APNG_BLEND_OP_SOURCE
                BlitFrame(frameImage, canvas, fc.XOffset, fc.YOffset);
            else // APNG_BLEND_OP_OVER
                BlitFrameOver(frameImage, canvas, fc.XOffset, fc.YOffset);

            // Output frame is a full-canvas snapshot
            frames[i] = new ImageFrame();
            frames[i].Initialize(canvasWidth, canvasHeight, ColorspaceType.SRGB, true);
            CopyFrame(canvas, frames[i]);

            // Apply disposal
            if (fc.DisposeOp == 1) // APNG_DISPOSE_OP_BACKGROUND
                ClearRegion(canvas, fc.XOffset, fc.YOffset, fc.Width, fc.Height);
            else if (fc.DisposeOp == 2 && previousCanvas != null) // APNG_DISPOSE_OP_PREVIOUS
            {
                CopyFrame(previousCanvas, canvas);
                previousCanvas.Dispose();
            }

            frameImage.Dispose();
        }

        canvas.Dispose();
        foreach (var fds in frameDataStreams) fds.Dispose();

        return (frames, loopCount, delaysMs);
    }

    /// <summary>
    /// Writes an animated PNG (APNG) file from a sequence of frames.
    /// </summary>
    public static void WriteSequence(ImageFrame[] frames, string path, int[] delaysMs, int loopCount = 0)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        WriteSequence(frames, stream, delaysMs, loopCount);
    }

    /// <summary>
    /// Writes an animated PNG (APNG) to a stream.
    /// </summary>
    public static void WriteSequence(ImageFrame[] frames, Stream stream, int[] delaysMs, int loopCount = 0)
    {
        if (frames.Length == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));

        int width = (int)frames[0].Columns;
        int height = (int)frames[0].Rows;
        byte colorType = ColorTypeRgba;
        byte bitDepth = 8;
        int outputChannels = 4;
        int rawBytesPerRow = width * outputChannels;
        int bytesPerPixel = outputChannels;

        // Write PNG signature
        stream.Write(PngSignature);

        // Write IHDR
        byte[] ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, (uint)width);
        WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        WriteChunk(stream, "IHDR"u8, ihdr);

        // Write acTL (Animation Control) — must come before IDAT
        byte[] actl = new byte[8];
        WriteUInt32BigEndian(actl, (uint)frames.Length);
        WriteUInt32BigEndian(actl.AsSpan(4), (uint)loopCount);
        WriteChunk(stream, "acTL"u8, actl);

        uint sequenceNumber = 0;

        for (int i = 0; i < frames.Length; i++)
        {
            int delayMs = i < delaysMs.Length ? delaysMs[i] : 100;

            // Write fcTL (Frame Control)
            byte[] fctl = new byte[26];
            WriteUInt32BigEndian(fctl, sequenceNumber++);
            WriteUInt32BigEndian(fctl.AsSpan(4), (uint)width);
            WriteUInt32BigEndian(fctl.AsSpan(8), (uint)height);
            WriteUInt32BigEndian(fctl.AsSpan(12), 0); // x_offset
            WriteUInt32BigEndian(fctl.AsSpan(16), 0); // y_offset
            fctl[20] = (byte)(delayMs >> 8); // delay_num high
            fctl[21] = (byte)(delayMs & 0xFF); // delay_num low
            fctl[22] = 0x03; // delay_den high = 1000
            fctl[23] = 0xE8; // delay_den low = 1000
            fctl[24] = 0; // dispose_op: NONE
            fctl[25] = 0; // blend_op: SOURCE
            WriteChunk(stream, "fcTL"u8, fctl);

            // Compress frame data
            byte[] compressedFrame = CompressFrameData(frames[i], width, height, outputChannels, rawBytesPerRow, bytesPerPixel);

            if (i == 0)
            {
                // First frame uses IDAT chunks
                WriteChunkedData(stream, "IDAT"u8, compressedFrame);
            }
            else
            {
                // Subsequent frames use fdAT chunks (4-byte sequence number prefix)
                WriteChunkedFdatData(stream, compressedFrame, ref sequenceNumber);
            }
        }

        // Write IEND
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static byte[] CompressFrameData(ImageFrame frame, int width, int height,
        int outputChannels, int rawBytesPerRow, int bytesPerPixel)
    {
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] currentRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            byte[] previousRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            byte[] filteredRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            try
            {
                Array.Clear(previousRow, 0, rawBytesPerRow);
                for (int y = 0; y < height; y++)
                {
                    var pixelRow = frame.GetPixelRow(y);
                    ConvertPixelRowToBytes(pixelRow, currentRow.AsSpan(0, rawBytesPerRow), width, outputChannels);

                    byte bestFilter = ChooseBestFilter(
                        currentRow.AsSpan(0, rawBytesPerRow),
                        previousRow.AsSpan(0, rawBytesPerRow),
                        bytesPerPixel);

                    ApplyFilter(bestFilter,
                        currentRow.AsSpan(0, rawBytesPerRow),
                        previousRow.AsSpan(0, rawBytesPerRow),
                        filteredRow.AsSpan(0, rawBytesPerRow),
                        bytesPerPixel);

                    zlibStream.WriteByte(bestFilter);
                    zlibStream.Write(filteredRow, 0, rawBytesPerRow);

                    var temp = previousRow;
                    previousRow = currentRow;
                    currentRow = temp;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(currentRow);
                ArrayPool<byte>.Shared.Return(previousRow);
                ArrayPool<byte>.Shared.Return(filteredRow);
            }
        }
        return compressedStream.ToArray();
    }

    private static void WriteChunkedData(Stream output, ReadOnlySpan<byte> chunkType, byte[] data)
    {
        const int maxChunkSize = 32768;
        int offset = 0;
        while (offset < data.Length)
        {
            int chunkSize = Math.Min(maxChunkSize, data.Length - offset);
            WriteChunk(output, chunkType, data.AsSpan(offset, chunkSize));
            offset += chunkSize;
        }
    }

    private static void WriteChunkedFdatData(Stream output, byte[] data, ref uint sequenceNumber)
    {
        const int maxChunkSize = 32768;
        int offset = 0;
        while (offset < data.Length)
        {
            int dataSize = Math.Min(maxChunkSize, data.Length - offset);
            byte[] fdatChunk = new byte[4 + dataSize];
            WriteUInt32BigEndian(fdatChunk, sequenceNumber++);
            Buffer.BlockCopy(data, offset, fdatChunk, 4, dataSize);
            WriteChunk(output, "fdAT"u8, fdatChunk);
            offset += dataSize;
        }
    }

    // APNG helper structs and methods
    private struct ApngFrameControl
    {
        public uint SequenceNumber;
        public int Width, Height;
        public int XOffset, YOffset;
        public ushort DelayNumerator, DelayDenominator;
        public byte DisposeOp, BlendOp;
    }

    private static void ClearFrame(ImageFrame frame)
    {
        for (int y = 0; y < frame.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            row.Clear();
        }
    }

    private static void ClearRegion(ImageFrame frame, int xOff, int yOff, int w, int h)
    {
        int channels = frame.NumberOfChannels;
        for (int y = yOff; y < yOff + h && y < frame.Rows; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int start = xOff * channels;
            int end = Math.Min((xOff + w) * channels, row.Length);
            row[start..end].Clear();
        }
    }

    private static void CopyFrame(ImageFrame src, ImageFrame dst)
    {
        for (int y = 0; y < src.Rows; y++)
        {
            var srcRow = src.GetPixelRow(y);
            var dstRow = dst.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
    }

    private static void BlitFrame(ImageFrame src, ImageFrame dst, int xOff, int yOff)
    {
        int srcChannels = src.NumberOfChannels;
        int dstChannels = dst.NumberOfChannels;
        for (int y = 0; y < src.Rows; y++)
        {
            int dy = y + yOff;
            if (dy < 0 || dy >= dst.Rows) continue;
            var srcRow = src.GetPixelRow(y);
            var dstRow = dst.GetPixelRowForWrite(dy);
            for (int x = 0; x < src.Columns; x++)
            {
                int dx = x + xOff;
                if (dx < 0 || dx >= dst.Columns) continue;
                int si = x * srcChannels;
                int di = dx * dstChannels;
                dstRow[di] = srcRow[si];
                dstRow[di + 1] = srcRow[si + 1];
                dstRow[di + 2] = srcRow[si + 2];
                dstRow[di + 3] = srcChannels > 3 ? srcRow[si + 3] : Quantum.MaxValue;
            }
        }
    }

    private static void BlitFrameOver(ImageFrame src, ImageFrame dst, int xOff, int yOff)
    {
        int srcChannels = src.NumberOfChannels;
        int dstChannels = dst.NumberOfChannels;
        for (int y = 0; y < src.Rows; y++)
        {
            int dy = y + yOff;
            if (dy < 0 || dy >= dst.Rows) continue;
            var srcRow = src.GetPixelRow(y);
            var dstRow = dst.GetPixelRowForWrite(dy);
            for (int x = 0; x < src.Columns; x++)
            {
                int dx = x + xOff;
                if (dx < 0 || dx >= dst.Columns) continue;
                int si = x * srcChannels;
                int di = dx * dstChannels;
                ushort srcA = srcChannels > 3 ? srcRow[si + 3] : Quantum.MaxValue;
                if (srcA == Quantum.MaxValue)
                {
                    dstRow[di] = srcRow[si];
                    dstRow[di + 1] = srcRow[si + 1];
                    dstRow[di + 2] = srcRow[si + 2];
                    dstRow[di + 3] = Quantum.MaxValue;
                }
                else if (srcA > 0)
                {
                    ushort dstA = dstChannels > 3 ? dstRow[di + 3] : Quantum.MaxValue;
                    float sa = srcA / (float)Quantum.MaxValue;
                    float da = dstA / (float)Quantum.MaxValue;
                    float outA = sa + da * (1 - sa);
                    if (outA > 0)
                    {
                        dstRow[di] = (ushort)((srcRow[si] * sa + dstRow[di] * da * (1 - sa)) / outA);
                        dstRow[di + 1] = (ushort)((srcRow[si + 1] * sa + dstRow[di + 1] * da * (1 - sa)) / outA);
                        dstRow[di + 2] = (ushort)((srcRow[si + 2] * sa + dstRow[di + 2] * da * (1 - sa)) / outA);
                        dstRow[di + 3] = (ushort)(outA * Quantum.MaxValue);
                    }
                }
            }
        }
    }

    private static byte[] ReadAllBytes(Stream zlibStream, int width, int height, byte bitDepth, byte colorType)
    {
        int channels = GetChannelCount(colorType);
        int bitsPerPixel = channels * bitDepth;
        // Raw bytes per row (without filter byte): ceil(width * bitsPerPixel / 8)
        int rawBytesPerRow = (width * bitsPerPixel + 7) / 8;
        int totalBytes = height * (1 + rawBytesPerRow); // +1 for filter byte per row

        byte[] buffer = new byte[totalBytes];
        int offset = 0;
        while (offset < totalBytes)
        {
            int read = zlibStream.Read(buffer, offset, totalBytes - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }
        return buffer;
    }

    private static void DecodePixelData(ImageFrame image, byte[] data, int width, int height,
        byte bitDepth, byte colorType, byte[]? palette, byte[]? transparencyData)
    {
        int channels = GetChannelCount(colorType);
        int bitsPerPixel = channels * bitDepth;
        int bytesPerPixel = Math.Max(1, (bitsPerPixel + 7) / 8);
        int rawBytesPerRow = (width * bitsPerPixel + 7) / 8;
        int stride = 1 + rawBytesPerRow; // filter byte + raw data

        // Allocate current and previous row buffers for unfiltering
        byte[] currentRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
        byte[] previousRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
        try
        {
            Array.Clear(previousRow, 0, rawBytesPerRow);

            for (int y = 0;y < height;y++)
            {
                int rowOffset = y * stride;
                byte filterType = data[rowOffset];

                // Copy raw row data
                Buffer.BlockCopy(data, rowOffset + 1, currentRow, 0, rawBytesPerRow);

                // Reverse filter
                UnfilterRow(filterType, currentRow.AsSpan(0, rawBytesPerRow),
                    previousRow.AsSpan(0, rawBytesPerRow), bytesPerPixel);

                // Convert to pixels
                ConvertRowToPixels(image, currentRow.AsSpan(0, rawBytesPerRow), y, width,
                    bitDepth, colorType, palette, transparencyData);

                // Swap current → previous
                var temp = previousRow;
                previousRow = currentRow;
                currentRow = temp;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(currentRow);
            ArrayPool<byte>.Shared.Return(previousRow);
        }
    }

    private static void ConvertRowToPixels(ImageFrame image, ReadOnlySpan<byte> rowData, int y, int width,
        byte bitDepth, byte colorType, byte[]? palette, byte[]? transparencyData)
    {
        var pixelRow = image.GetPixelRowForWrite(y);
        int channelsPerPixel = image.NumberOfChannels;

        switch (colorType)
        {
            case ColorTypeGrayscale:
                DecodeGrayscaleRow(pixelRow, rowData, width, bitDepth, channelsPerPixel, transparencyData);
                break;

            case ColorTypeRgb:
                DecodeRgbRow(pixelRow, rowData, width, bitDepth, channelsPerPixel, transparencyData);
                break;

            case ColorTypeIndexed:
                DecodeIndexedRow(pixelRow, rowData, width, bitDepth, channelsPerPixel, palette!, transparencyData);
                break;

            case ColorTypeGrayscaleAlpha:
                DecodeGrayscaleAlphaRow(pixelRow, rowData, width, bitDepth, channelsPerPixel);
                break;

            case ColorTypeRgba:
                DecodeRgbaRow(pixelRow, rowData, width, bitDepth, channelsPerPixel);
                break;

            default:
                throw new InvalidDataException($"Unknown PNG color type: {colorType}");
        }
    }

    private static void DecodeGrayscaleRow(Span<ushort> pixels, ReadOnlySpan<byte> rowData,
        int width, byte bitDepth, int channelsPerPixel, byte[]? transparencyData)
    {
        ushort transparentValue = ushort.MaxValue;
        bool hasTransparency = false;
        if (transparencyData != null && transparencyData.Length >= 2)
        {
            hasTransparency = true;
            transparentValue = (ushort)((transparencyData[0] << 8) | transparencyData[1]);
        }

        for (int x = 0;x < width;x++)
        {
            ushort gray = ReadSample(rowData, x, bitDepth);
            ushort scaled = ScaleToQuantum(gray, bitDepth);

            int offset = x * channelsPerPixel;
            pixels[offset] = scaled;     // R
            pixels[offset + 1] = scaled; // G
            pixels[offset + 2] = scaled; // B
            if (channelsPerPixel > 3)
            {
                pixels[offset + 3] = (hasTransparency && gray == transparentValue) ? (ushort)0 : Quantum.MaxValue;
            }
        }
    }

    private static void DecodeRgbRow(Span<ushort> pixels, ReadOnlySpan<byte> rowData,
        int width, byte bitDepth, int channelsPerPixel, byte[]? transparencyData)
    {
        bool hasTransparency = transparencyData != null && transparencyData.Length >= 6;
        ushort tR = 0, tG = 0, tB = 0;
        if (hasTransparency)
        {
            tR = (ushort)((transparencyData![0] << 8) | transparencyData[1]);
            tG = (ushort)((transparencyData[2] << 8) | transparencyData[3]);
            tB = (ushort)((transparencyData[4] << 8) | transparencyData[5]);
        }

        if (bitDepth == 8)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 3;
                int dstOffset = x * channelsPerPixel;
                byte r = rowData[srcOffset];
                byte g = rowData[srcOffset + 1];
                byte b = rowData[srcOffset + 2];
                pixels[dstOffset] = Quantum.ScaleFromByte(r);
                pixels[dstOffset + 1] = Quantum.ScaleFromByte(g);
                pixels[dstOffset + 2] = Quantum.ScaleFromByte(b);
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = (hasTransparency && r == tR && g == tG && b == tB)
                                        ? (ushort)0 : Quantum.MaxValue;
                }
            }
        }
        else if (bitDepth == 16)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 6;
                int dstOffset = x * channelsPerPixel;
                ushort r = (ushort)((rowData[srcOffset] << 8) | rowData[srcOffset + 1]);
                ushort g = (ushort)((rowData[srcOffset + 2] << 8) | rowData[srcOffset + 3]);
                ushort b = (ushort)((rowData[srcOffset + 4] << 8) | rowData[srcOffset + 5]);
                pixels[dstOffset] = r;
                pixels[dstOffset + 1] = g;
                pixels[dstOffset + 2] = b;
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = (hasTransparency && r == tR && g == tG && b == tB)
                                        ? (ushort)0 : Quantum.MaxValue;
                }
            }
        }
    }

    private static void DecodeIndexedRow(Span<ushort> pixels, ReadOnlySpan<byte> rowData,
        int width, byte bitDepth, int channelsPerPixel, byte[] palette, byte[]? transparencyData)
    {
        for (int x = 0;x < width;x++)
        {
            int index = ReadSample(rowData, x, bitDepth);
            int palOffset = index * 3;

            int dstOffset = x * channelsPerPixel;
            if (palOffset + 2 < palette.Length)
            {
                pixels[dstOffset] = Quantum.ScaleFromByte(palette[palOffset]);
                pixels[dstOffset + 1] = Quantum.ScaleFromByte(palette[palOffset + 1]);
                pixels[dstOffset + 2] = Quantum.ScaleFromByte(palette[palOffset + 2]);
            }

            if (channelsPerPixel > 3)
            {
                pixels[dstOffset + 3] = (transparencyData != null && index < transparencyData.Length)
                    ? Quantum.ScaleFromByte(transparencyData[index])
                    : Quantum.MaxValue;
            }
        }
    }

    private static void DecodeGrayscaleAlphaRow(Span<ushort> pixels, ReadOnlySpan<byte> rowData,
        int width, byte bitDepth, int channelsPerPixel)
    {
        if (bitDepth == 8)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 2;
                int dstOffset = x * channelsPerPixel;
                ushort gray = Quantum.ScaleFromByte(rowData[srcOffset]);
                ushort alpha = Quantum.ScaleFromByte(rowData[srcOffset + 1]);
                pixels[dstOffset] = gray;
                pixels[dstOffset + 1] = gray;
                pixels[dstOffset + 2] = gray;
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = alpha;
                }
            }
        }
        else if (bitDepth == 16)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 4;
                int dstOffset = x * channelsPerPixel;
                ushort gray = (ushort)((rowData[srcOffset] << 8) | rowData[srcOffset + 1]);
                ushort alpha = (ushort)((rowData[srcOffset + 2] << 8) | rowData[srcOffset + 3]);
                pixels[dstOffset] = gray;
                pixels[dstOffset + 1] = gray;
                pixels[dstOffset + 2] = gray;
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = alpha;
                }
            }
        }
    }

    private static void DecodeRgbaRow(Span<ushort> pixels, ReadOnlySpan<byte> rowData,
        int width, byte bitDepth, int channelsPerPixel)
    {
        if (bitDepth == 8)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 4;
                int dstOffset = x * channelsPerPixel;
                pixels[dstOffset] = Quantum.ScaleFromByte(rowData[srcOffset]);
                pixels[dstOffset + 1] = Quantum.ScaleFromByte(rowData[srcOffset + 1]);
                pixels[dstOffset + 2] = Quantum.ScaleFromByte(rowData[srcOffset + 2]);
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = Quantum.ScaleFromByte(rowData[srcOffset + 3]);
                }
            }
        }
        else if (bitDepth == 16)
        {
            for (int x = 0;x < width;x++)
            {
                int srcOffset = x * 8;
                int dstOffset = x * channelsPerPixel;
                pixels[dstOffset] = (ushort)((rowData[srcOffset] << 8) | rowData[srcOffset + 1]);
                pixels[dstOffset + 1] = (ushort)((rowData[srcOffset + 2] << 8) | rowData[srcOffset + 3]);
                pixels[dstOffset + 2] = (ushort)((rowData[srcOffset + 4] << 8) | rowData[srcOffset + 5]);
                if (channelsPerPixel > 3)
                {
                    pixels[dstOffset + 3] = (ushort)((rowData[srcOffset + 6] << 8) | rowData[srcOffset + 7]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadSample(ReadOnlySpan<byte> data, int sampleIndex, byte bitDepth)
    {
        switch (bitDepth)
        {
            case 1:
            {
                int byteIndex = sampleIndex >> 3;
                int bitIndex = 7 - (sampleIndex & 7);
                return (ushort)((data[byteIndex] >> bitIndex) & 1);
            }
            case 2:
            {
                int byteIndex = sampleIndex >> 2;
                int bitIndex = 6 - ((sampleIndex & 3) << 1);
                return (ushort)((data[byteIndex] >> bitIndex) & 3);
            }
            case 4:
            {
                int byteIndex = sampleIndex >> 1;
                int bitIndex = (sampleIndex & 1) == 0 ? 4 : 0;
                return (ushort)((data[byteIndex] >> bitIndex) & 0xF);
            }
            case 8:
                return data[sampleIndex];
            case 16:
                return (ushort)((data[sampleIndex * 2] << 8) | data[sampleIndex * 2 + 1]);
            default:
                throw new InvalidDataException($"Unsupported PNG bit depth: {bitDepth}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleToQuantum(ushort value, byte bitDepth)
    {
        return bitDepth switch
        {
            1 => value != 0 ? Quantum.MaxValue : (ushort)0,
            2 => (ushort)(value * 21845), // 65535 / 3
            4 => (ushort)(value * 4369),  // 65535 / 15
            8 => Quantum.ScaleFromByte((byte)value),
            16 => value,
            _ => value
        };
    }

    #endregion

    #region Write

    public static void Write(ImageFrame image, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        Write(image, stream);
    }

    public static void Write(ImageFrame image, Stream stream)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        bool hasAlpha = image.HasAlpha;

        // Determine output format
        byte colorType = hasAlpha ? ColorTypeRgba : ColorTypeRgb;
        byte bitDepth = 8; // Write as 8-bit for maximum compatibility
        int outputChannels = hasAlpha ? 4 : 3;
        int rawBytesPerRow = width * outputChannels;
        int bytesPerPixel = outputChannels;

        // Write PNG signature
        stream.Write(PngSignature);

        // Write IHDR
        byte[] ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, (uint)width);
        WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0; // Compression method (deflate)
        ihdr[11] = 0; // Filter method (adaptive)
        ihdr[12] = 0; // Interlace method (none)
        WriteChunk(stream, "IHDR"u8, ihdr);

        // Write sRGB chunk (rendering intent = perceptual)
        WriteChunk(stream, "sRGB"u8, [ 0 ]);

        // Write gAMA chunk (1/2.2 = 45455 in PNG units)
        byte[] gamaData = new byte[4];
        WriteUInt32BigEndian(gamaData, 45455);
        WriteChunk(stream, "gAMA"u8, gamaData);

        // Write metadata chunks (before IDAT per PNG spec)
        WriteMetadataChunks(stream, image);

        // Compress and write IDAT
        WriteIdatChunks(stream, image, width, height, outputChannels, rawBytesPerRow, bytesPerPixel);

        // Write IEND
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteIdatChunks(Stream output, ImageFrame image, int width, int height,
        int outputChannels, int rawBytesPerRow, int bytesPerPixel)
    {
        // Compress filtered image data into memory, then write as IDAT chunks
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] currentRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            byte[] previousRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            byte[] filteredRow = ArrayPool<byte>.Shared.Rent(rawBytesPerRow);
            try
            {
                Array.Clear(previousRow, 0, rawBytesPerRow);

                for (int y = 0;y < height;y++)
                {
                    // Convert pixel row to bytes
                    var pixelRow = image.GetPixelRow(y);
                    ConvertPixelRowToBytes(pixelRow, currentRow.AsSpan(0, rawBytesPerRow),
                        width, outputChannels);

                    // Choose best filter and apply
                    byte bestFilter = ChooseBestFilter(
                        currentRow.AsSpan(0, rawBytesPerRow),
                        previousRow.AsSpan(0, rawBytesPerRow),
                        bytesPerPixel);

                    ApplyFilter(bestFilter,
                        currentRow.AsSpan(0, rawBytesPerRow),
                        previousRow.AsSpan(0, rawBytesPerRow),
                        filteredRow.AsSpan(0, rawBytesPerRow),
                        bytesPerPixel);

                    // Write filter byte + filtered data
                    zlibStream.WriteByte(bestFilter);
                    zlibStream.Write(filteredRow, 0, rawBytesPerRow);

                    // Swap for next row
                    var temp = previousRow;
                    previousRow = currentRow;
                    currentRow = temp;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(currentRow);
                ArrayPool<byte>.Shared.Return(previousRow);
                ArrayPool<byte>.Shared.Return(filteredRow);
            }
        }

        // Write compressed data as IDAT chunks (max 32KB each)
        const int maxChunkSize = 32768;
        byte[] compressed = compressedStream.ToArray();
        int offset = 0;
        while (offset < compressed.Length)
        {
            int chunkSize = Math.Min(maxChunkSize, compressed.Length - offset);
            WriteChunk(output, "IDAT"u8, compressed.AsSpan(offset, chunkSize));
            offset += chunkSize;
        }
    }

    private static void ConvertPixelRowToBytes(ReadOnlySpan<ushort> pixelRow, Span<byte> output,
        int width, int outputChannels)
    {
        int srcChannels = pixelRow.Length / width;
        for (int x = 0;x < width;x++)
        {
            int srcOffset = x * srcChannels;
            int dstOffset = x * outputChannels;

            if (srcChannels >= 3)
            {
                output[dstOffset] = Quantum.ScaleToByte(pixelRow[srcOffset]);         // R
                output[dstOffset + 1] = Quantum.ScaleToByte(pixelRow[srcOffset + 1]); // G
                output[dstOffset + 2] = Quantum.ScaleToByte(pixelRow[srcOffset + 2]); // B
            }
            else
            {
                // Grayscale: replicate single channel to R, G, B
                byte gray = Quantum.ScaleToByte(pixelRow[srcOffset]);
                output[dstOffset] = gray;
                output[dstOffset + 1] = gray;
                output[dstOffset + 2] = gray;
            }

            if (outputChannels > 3)
            {
                output[dstOffset + 3] = srcChannels > 3
                    ? Quantum.ScaleToByte(pixelRow[srcOffset + 3])
                    : srcChannels == 2
                        ? Quantum.ScaleToByte(pixelRow[srcOffset + 1])
                        : (byte)255;
            }
        }
    }

    /// <summary>
    /// Selects the filter that produces the smallest sum of absolute values. This heuristic (minimum sum of absolute
    /// differences) matches libpng's adaptive approach.
    /// </summary>
    private static byte ChooseBestFilter(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previous, int bpp)
    {
        long bestSum = long.MaxValue;
        byte bestFilter = FilterNone;

        // Test each filter type
        for (byte f = FilterNone;f <= FilterPaeth;f++)
        {
            long sum = ComputeFilterCost(f, raw, previous, bpp);
            if (sum < bestSum)
            {
                bestSum = sum;
                bestFilter = f;
            }
        }

        return bestFilter;
    }

    private static long ComputeFilterCost(byte filterType, ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> previous, int bpp)
    {
        long sum = 0;
        for (int i = 0;i < raw.Length;i++)
        {
            byte a = i >= bpp ? raw[i - bpp] : (byte)0;
            byte b = previous[i];
            byte c = i >= bpp ? previous[i - bpp] : (byte)0;

            int filtered = filterType switch
            {
                FilterNone => raw[i],
                FilterSub => (raw[i] - a) & 0xFF,
                FilterUp => (raw[i] - b) & 0xFF,
                FilterAverage => (raw[i] - ((a + b) >> 1)) & 0xFF,
                FilterPaeth => (raw[i] - PaethPredictor(a, b, c)) & 0xFF,
                _ => raw[i]
            };

            // Sum of absolute values (treating as signed byte for cost)
            sum += filtered < 128 ? filtered : 256 - filtered;
        }
        return sum;
    }

    private static void ApplyFilter(byte filterType, ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> previous, Span<byte> output, int bpp)
    {
        for (int i = 0;i < raw.Length;i++)
        {
            byte a = i >= bpp ? raw[i - bpp] : (byte)0;
            byte b = previous[i];
            byte c = i >= bpp ? previous[i - bpp] : (byte)0;

            output[i] = filterType switch
            {
                FilterNone => raw[i],
                FilterSub => (byte)((raw[i] - a) & 0xFF),
                FilterUp => (byte)((raw[i] - b) & 0xFF),
                FilterAverage => (byte)((raw[i] - ((a + b) >> 1)) & 0xFF),
                FilterPaeth => (byte)((raw[i] - PaethPredictor(a, b, c)) & 0xFF),
                _ => raw[i]
            };
        }
    }

    #endregion

    #region Filter

    private static void UnfilterRow(byte filterType, Span<byte> currentRow,
        ReadOnlySpan<byte> previousRow, int bytesPerPixel)
    {
        switch (filterType)
        {
            case FilterNone:
                break;

            case FilterSub:
                for (int i = bytesPerPixel;i < currentRow.Length;i++)
                {
                    currentRow[i] = (byte)(currentRow[i] + currentRow[i - bytesPerPixel]);
                }

                break;

            case FilterUp:
                for (int i = 0;i < currentRow.Length;i++)
                {
                    currentRow[i] = (byte)(currentRow[i] + previousRow[i]);
                }

                break;

            case FilterAverage:
                for (int i = 0;i < currentRow.Length;i++)
                {
                    byte a = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                    byte b = previousRow[i];
                    currentRow[i] = (byte)(currentRow[i] + ((a + b) >> 1));
                }
                break;

            case FilterPaeth:
                for (int i = 0;i < currentRow.Length;i++)
                {
                    byte a = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                    byte b = previousRow[i];
                    byte c = i >= bytesPerPixel ? previousRow[i - bytesPerPixel] : (byte)0;
                    currentRow[i] = (byte)(currentRow[i] + PaethPredictor(a, b, c));
                }
                break;

            default:
                throw new InvalidDataException($"Unknown PNG filter type: {filterType}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    #endregion

    #region Chunk I/O

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        WriteUInt32BigEndian(lengthBuf, (uint)data.Length);
        stream.Write(lengthBuf);
        stream.Write(chunkType);

        // CRC covers type + data
        uint crc = Crc32.Compute(chunkType);
        if (data.Length > 0)
        {
            crc = Crc32.Update(crc, data);
            stream.Write(data);
        }

        Span<byte> crcBuf = stackalloc byte[4];
        WriteUInt32BigEndian(crcBuf, crc);
        stream.Write(crcBuf);
    }

    /// <summary>
    /// Writes metadata chunks (iCCP, eXIf, tEXt) for an image.
    /// </summary>
    private static void WriteMetadataChunks(Stream stream, ImageFrame image)
    {
        // ICC Profile → iCCP chunk (compressed)
        if (image.Metadata.IccProfile is not null)
        {
            using var ms = new MemoryStream();
            // Keyword: "ICC Profile\0" + compression method (0 = zlib)
            ms.Write(System.Text.Encoding.ASCII.GetBytes("ICC Profile"));
            ms.WriteByte(0); // null terminator
            ms.WriteByte(0); // compression method = deflate

            using var compMs = new MemoryStream();
            using (var deflate = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(image.Metadata.IccProfile.Data);
            }
            ms.Write(compMs.ToArray());

            WriteChunk(stream, "iCCP"u8, ms.ToArray());
        }

        // EXIF → eXIf chunk (raw TIFF data)
        if (image.Metadata.ExifProfile is not null)
        {
            byte[] exifData = ExifParser.SerializeForPngExif(image.Metadata.ExifProfile);
            WriteChunk(stream, "eXIf"u8, exifData);
        }

        // XMP → iTXt chunk
        if (image.Metadata.Xmp is not null)
        {
            using var ms = new MemoryStream();
            byte[] keyword = System.Text.Encoding.Latin1.GetBytes("XML:com.adobe.xmp");
            ms.Write(keyword);
            ms.WriteByte(0); // null terminator
            ms.WriteByte(0); // compression flag = 0 (not compressed)
            ms.WriteByte(0); // compression method
            ms.WriteByte(0); // language tag (empty)
            ms.WriteByte(0); // translated keyword (empty)
            ms.Write(System.Text.Encoding.UTF8.GetBytes(image.Metadata.Xmp));
            WriteChunk(stream, "iTXt"u8, ms.ToArray());
        }
    }

    private static void VerifyChunkCrc(Stream stream, ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> data, Span<byte> crcBuf)
    {
        ReadExact(stream, crcBuf);
        uint expectedCrc = ReadUInt32BigEndian(crcBuf);
        uint computedCrc = Crc32.Compute(chunkType);
        if (data.Length > 0)
        {
            computedCrc = Crc32.Update(computedCrc, data);
        }

        if (expectedCrc != computedCrc)
        {
            throw new InvalidDataException($"PNG chunk CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{computedCrc:X8}");
        }
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetChannelCount(byte colorType) => colorType switch
    {
        ColorTypeGrayscale => 1,
        ColorTypeRgb => 3,
        ColorTypeIndexed => 1,
        ColorTypeGrayscaleAlpha => 2,
        ColorTypeRgba => 4,
        _ => throw new InvalidDataException($"Unknown PNG color type: {colorType}")
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
    {
        return ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32BigEndian(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of PNG stream.");
            }

            totalRead += read;
        }
    }

    private static void SkipBytes(Stream stream, int count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
        }
        else
        {
            byte[] skip = ArrayPool<byte>.Shared.Rent(Math.Min(count, 8192));
            try
            {
                int remaining = count;
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
            finally
            {
                ArrayPool<byte>.Shared.Return(skip);
            }
        }
    }

    #endregion
}
