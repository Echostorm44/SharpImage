using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Pure C# GIF reader/writer (GIF87a/GIF89a). Supports: single-frame, animated (read), global/local color tables,
/// transparency, interlacing, LZW compression. Writer produces single-frame GIF89a with optional transparency.
/// </summary>
public static class GifCoder
{
    /// <summary>
    /// Detect GIF format by "GIF87a" or "GIF89a" signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' &&
        data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

    // GIF block types
    private const byte ImageSeparator = 0x2C;
    private const byte ExtensionIntroducer = 0x21;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationExtensionLabel = 0xFF;
    private const byte CommentExtensionLabel = 0xFE;

    // Interlace pass starting rows and row increments
    private static readonly int[] InterlaceStart = [ 0, 4, 2, 1 ];
    private static readonly int[] InterlaceStep = [ 8, 8, 4, 2 ];

    #region Read

    public static ImageFrame Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        return Read(stream);
    }

    /// <summary>
    /// Reads the first frame of a GIF image. For animated GIFs, returns just the first frame.
    /// </summary>
    public static ImageFrame Read(Stream stream)
    {
        // Read header
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        bool isGif87a = header.SequenceEqual("GIF87a"u8);
        bool isGif89a = header.SequenceEqual("GIF89a"u8);
        if (!isGif87a && !isGif89a)
        {
            throw new InvalidDataException("Not a valid GIF file.");
        }

        // Logical Screen Descriptor
        int canvasWidth = ReadUInt16Le(stream);
        int canvasHeight = ReadUInt16Le(stream);
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new InvalidDataException($"Invalid GIF canvas dimensions: {canvasWidth}x{canvasHeight}");
        int packed = stream.ReadByte();
        bool hasGlobalColorTable = (packed & 0x80) != 0;
        int colorResolution = ((packed >> 4) & 7) + 1;
        int globalTableSize = 1 << ((packed & 7) + 1);
        int backgroundColorIndex = stream.ReadByte();
        int aspectRatio = stream.ReadByte();

        // Global Color Table
        byte[]? globalColorTable = null;
        if (hasGlobalColorTable)
        {
            globalColorTable = new byte[globalTableSize * 3];
            stream.ReadExactly(globalColorTable);
        }

        // Parse blocks until first image
        int transparentIndex = -1;
        int disposalMethod = 0;
        int delay = 0;

        while (true)
        {
            int blockType = stream.ReadByte();
            if (blockType < 0 || blockType == Trailer)
            {
                break;
            }

            if (blockType == ExtensionIntroducer)
            {
                int label = stream.ReadByte();
                if (label == GraphicControlLabel)
                {
                    int blockSize = stream.ReadByte(); // Always 4
                    int gcPacked = stream.ReadByte();
                    disposalMethod = (gcPacked >> 2) & 7;
                    bool hasTransparency = (gcPacked & 1) != 0;
                    delay = ReadUInt16Le(stream);
                    int transIdx = stream.ReadByte();
                    if (hasTransparency)
                    {
                        transparentIndex = transIdx;
                    }

                    stream.ReadByte(); // Block terminator (0x00)
                }
                else
                {
                    // Skip other extensions
                    SkipSubBlocks(stream);
                }
            }
            else if (blockType == ImageSeparator)
            {
                return ReadImageData(stream, canvasWidth, canvasHeight,
                    globalColorTable, transparentIndex);
            }
        }

        throw new InvalidDataException("GIF contains no image data.");
    }

    private static ImageFrame ReadImageData(Stream stream, int canvasWidth, int canvasHeight,
        byte[]? globalColorTable, int transparentIndex)
    {
        // Image Descriptor
        int left = ReadUInt16Le(stream);
        int top = ReadUInt16Le(stream);
        int width = ReadUInt16Le(stream);
        int height = ReadUInt16Le(stream);
        int packed = stream.ReadByte();
        bool hasLocalColorTable = (packed & 0x80) != 0;
        bool isInterlaced = (packed & 0x40) != 0;
        int localTableSize = hasLocalColorTable ? 1 << ((packed & 7) + 1) : 0;

        // Local Color Table (overrides global)
        byte[]? colorTable = globalColorTable;
        if (hasLocalColorTable)
        {
            colorTable = new byte[localTableSize * 3];
            stream.ReadExactly(colorTable);
        }

        if (colorTable == null)
        {
            throw new InvalidDataException("GIF has no color table.");
        }

        // LZW minimum code size
        int minCodeSize = stream.ReadByte();
        if (minCodeSize < 2 || minCodeSize > 11)
        {
            throw new InvalidDataException($"Invalid GIF LZW minimum code size: {minCodeSize}");
        }

        // Decompress LZW data
        byte[] pixelIndices = Lzw.Decompress(stream, minCodeSize);

        // Create image frame
        bool hasAlpha = transparentIndex >= 0;
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        // Convert palette indices to RGB pixels
        int pixelIndex = 0;
        for (int y = 0;y < height;y++)
        {
            int outputY = isInterlaced ? DeinterlaceRow(y, height) : y;
            var row = image.GetPixelRowForWrite(outputY);
            int channels = image.NumberOfChannels;

            for (int x = 0;x < width;x++)
            {
                byte colorIndex = pixelIndex < pixelIndices.Length ? pixelIndices[pixelIndex] : (byte)0;
                pixelIndex++;

                int palOffset = colorIndex * 3;
                int dstOffset = x * channels;

                if (palOffset + 2 < colorTable.Length)
                {
                    row[dstOffset] = Quantum.ScaleFromByte(colorTable[palOffset]);
                    row[dstOffset + 1] = Quantum.ScaleFromByte(colorTable[palOffset + 1]);
                    row[dstOffset + 2] = Quantum.ScaleFromByte(colorTable[palOffset + 2]);
                }

                if (channels > 3)
                {
                    row[dstOffset + 3] = (colorIndex == transparentIndex)
                                        ? (ushort)0 : Quantum.MaxValue;
                }
            }
        }

        return image;
    }

    /// <summary>
    /// Maps sequential row index to deinterlaced output row for GIF interlacing.
    /// </summary>
    private static int DeinterlaceRow(int sequentialRow, int height)
    {
        int row = 0;
        for (int pass = 0;pass < 4;pass++)
        {
            int passRows = 0;
            for (int y = InterlaceStart[pass];y < height;y += InterlaceStep[pass])
            {
                passRows++;
            }

            if (sequentialRow < row + passRows)
            {
                int indexInPass = sequentialRow - row;
                return InterlaceStart[pass] + indexInPass * InterlaceStep[pass];
            }
            row += passRows;
        }
        return sequentialRow; // Fallback
    }

    #endregion

    #region Write

    public static void Write(ImageFrame image, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        Write(image, stream);
    }

    /// <summary>
    /// Writes a single-frame GIF89a. Quantizes to 256 colors using a simple median-cut approach.
    /// </summary>
    public static void Write(ImageFrame image, Stream stream)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;

        // Build palette and index map
        byte[] palette = new byte[256 * 3];
        var indices = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
        var (colorCount, transparentIdx) = BuildPaletteWithAlpha(image, width, height, palette, indices);

        // Round up to power of 2
        int bitsPerPixel = 1;
        while ((1 << bitsPerPixel) < colorCount)
        {
            bitsPerPixel++;
        }

        int paletteEntries = 1 << bitsPerPixel;

        // Header
        stream.Write("GIF89a"u8);

        // Logical Screen Descriptor
        WriteUInt16Le(stream, width);
        WriteUInt16Le(stream, height);
        int packed = 0x80 | ((bitsPerPixel - 1) << 4) | (bitsPerPixel - 1);
        stream.WriteByte((byte)packed);
        stream.WriteByte(0); // Background color index
        stream.WriteByte(0); // Pixel aspect ratio

        // Global Color Table
        stream.Write(palette.AsSpan(0, paletteEntries * 3));

        // Graphic Control Extension (for transparency)
        if (transparentIdx >= 0)
        {
            stream.WriteByte(ExtensionIntroducer);
            stream.WriteByte(GraphicControlLabel);
            stream.WriteByte(4);
            stream.WriteByte(1); // has transparency
            WriteUInt16Le(stream, 0); // delay
            stream.WriteByte((byte)transparentIdx);
            stream.WriteByte(0); // block terminator
        }

        // Image Descriptor
        stream.WriteByte(ImageSeparator);
        WriteUInt16Le(stream, 0); // Left
        WriteUInt16Le(stream, 0); // Top
        WriteUInt16Le(stream, width);
        WriteUInt16Le(stream, height);
        stream.WriteByte(0); // No local color table, not interlaced

        // LZW minimum code size
        int minCodeSize = Math.Max(2, bitsPerPixel);
        stream.WriteByte((byte)minCodeSize);

        // LZW compressed data
        Lzw.Compress(stream, indices, minCodeSize);

        // Trailer
        stream.WriteByte(Trailer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indices);
        }
    }

    /// <summary>
    /// Simple color quantization: builds a 256-color palette using frequency-based selection. Maps each pixel to its
    /// nearest palette color. If the frame has alpha, index 0 is reserved for transparent pixels.
    /// Returns the number of palette entries used and the transparent index (-1 if none).
    /// </summary>
    private static (int colorCount, int transparentIndex) BuildPaletteWithAlpha(
        ImageFrame image, int width, int height, byte[] palette, byte[] indices)
    {
        bool hasAlpha = image.HasAlpha;
        int channels = image.NumberOfChannels;
        int nextIndex = hasAlpha ? 1 : 0; // Reserve index 0 for transparency
        int transparentIdx = -1;

        var colorMap = new Dictionary<int, int>(256);

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);

            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;

                // Check transparency
                if (hasAlpha && channels > 3 && row[offset + 3] == 0)
                {
                    indices[y * width + x] = 0;
                    transparentIdx = 0;
                    continue;
                }

                byte r = Quantum.ScaleToByte(row[offset]);
                byte g = Quantum.ScaleToByte(row[offset + 1]);
                byte b = Quantum.ScaleToByte(row[offset + 2]);
                int rgb = (r << 16) | (g << 8) | b;

                if (!colorMap.TryGetValue(rgb, out int palIndex))
                {
                    if (nextIndex >= 256)
                    {
                        palIndex = FindNearestColor(palette, nextIndex, r, g, b);
                    }
                    else
                    {
                        palIndex = nextIndex;
                        palette[palIndex * 3] = r;
                        palette[palIndex * 3 + 1] = g;
                        palette[palIndex * 3 + 2] = b;
                        colorMap[rgb] = palIndex;
                        nextIndex++;
                    }
                }

                indices[y * width + x] = (byte)palIndex;
            }
        }

        return (Math.Max(nextIndex, 2), transparentIdx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNearestColor(byte[] palette, int count, byte r, byte g, byte b)
    {
        int bestIndex = 0;
        int bestDist = int.MaxValue;
        for (int i = 0;i < count;i++)
        {
            int dr = r - palette[i * 3];
            int dg = g - palette[i * 3 + 1];
            int db = b - palette[i * 3 + 2];
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    #endregion

    #region Multi-Frame Read

    /// <summary>
    /// Reads all frames from an animated GIF, returning an ImageSequence.
    /// </summary>
    public static ImageSequence ReadSequence(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192);
        return ReadSequence(stream);
    }

    /// <summary>
    /// Reads all frames from an animated GIF stream, returning an ImageSequence.
    /// </summary>
    public static ImageSequence ReadSequence(Stream stream)
    {
        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        if (!header.SequenceEqual("GIF87a"u8) && !header.SequenceEqual("GIF89a"u8))
            throw new InvalidDataException("Not a valid GIF file.");

        int canvasWidth = ReadUInt16Le(stream);
        int canvasHeight = ReadUInt16Le(stream);
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new InvalidDataException($"Invalid GIF canvas dimensions: {canvasWidth}x{canvasHeight}");
        int packed = stream.ReadByte();
        bool hasGlobalColorTable = (packed & 0x80) != 0;
        int globalTableSize = 1 << ((packed & 7) + 1);
        int backgroundColorIndex = stream.ReadByte();
        stream.ReadByte(); // aspect ratio

        byte[]? globalColorTable = null;
        if (hasGlobalColorTable)
        {
            globalColorTable = new byte[globalTableSize * 3];
            stream.ReadExactly(globalColorTable);
        }

        var sequence = new ImageSequence
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            BackgroundColorIndex = backgroundColorIndex,
            FormatName = "GIF"
        };

        int transparentIndex = -1;
        int disposalMethod = 0;
        int delay = 0;

        Span<byte> appId = stackalloc byte[11];
        while (true)
        {
            int blockType = stream.ReadByte();
            if (blockType < 0 || blockType == Trailer)
                break;

            if (blockType == ExtensionIntroducer)
            {
                int label = stream.ReadByte();
                if (label == GraphicControlLabel)
                {
                    stream.ReadByte(); // block size (always 4)
                    int gcPacked = stream.ReadByte();
                    disposalMethod = (gcPacked >> 2) & 7;
                    bool hasTransparency = (gcPacked & 1) != 0;
                    delay = ReadUInt16Le(stream);
                    int transIdx = stream.ReadByte();
                    transparentIndex = hasTransparency ? transIdx : -1;
                    stream.ReadByte(); // block terminator
                }
                else if (label == ApplicationExtensionLabel)
                {
                    // Check for NETSCAPE2.0 loop extension
                    int blockSize = stream.ReadByte();
                    if (blockSize == 11)
                    {
                        stream.ReadExactly(appId);
                        if (appId.SequenceEqual("NETSCAPE2.0"u8))
                        {
                            int subBlockSize = stream.ReadByte();
                            if (subBlockSize == 3)
                            {
                                stream.ReadByte(); // sub-block index (1)
                                sequence.LoopCount = ReadUInt16Le(stream);
                                stream.ReadByte(); // block terminator
                            }
                            else
                            {
                                // Skip remaining sub-blocks
                                if (subBlockSize > 0)
                                {
                                    byte[] skip = new byte[subBlockSize];
                                    stream.ReadExactly(skip);
                                }
                                SkipSubBlocks(stream);
                            }
                        }
                        else
                        {
                            SkipSubBlocks(stream);
                        }
                    }
                    else
                    {
                        if (blockSize > 0)
                        {
                            byte[] skip = new byte[blockSize];
                            stream.ReadExactly(skip);
                        }
                        SkipSubBlocks(stream);
                    }
                }
                else
                {
                    SkipSubBlocks(stream);
                }
            }
            else if (blockType == ImageSeparator)
            {
                var frame = ReadFrameData(stream, canvasWidth, canvasHeight,
                    globalColorTable, transparentIndex);
                frame.Delay = delay;
                frame.DisposeMethod = disposalMethod switch
                {
                    0 => DisposeType.Undefined,
                    1 => DisposeType.None,
                    2 => DisposeType.Background,
                    3 => DisposeType.Previous,
                    _ => DisposeType.Undefined
                };
                frame.Iterations = sequence.LoopCount;
                sequence.AddFrame(frame);

                // Reset per-frame state
                transparentIndex = -1;
                disposalMethod = 0;
                delay = 0;
            }
        }

        if (sequence.Count == 0)
            throw new InvalidDataException("GIF contains no image data.");

        return sequence;
    }

    /// <summary>
    /// Reads a single frame's image data from the stream. Like ReadImageData but also reads
    /// the frame's position offset for sub-frame GIFs.
    /// </summary>
    private static ImageFrame ReadFrameData(Stream stream, int canvasWidth, int canvasHeight,
        byte[]? globalColorTable, int transparentIndex)
    {
        int left = ReadUInt16Le(stream);
        int top = ReadUInt16Le(stream);
        int width = ReadUInt16Le(stream);
        int height = ReadUInt16Le(stream);
        int packed = stream.ReadByte();
        bool hasLocalColorTable = (packed & 0x80) != 0;
        bool isInterlaced = (packed & 0x40) != 0;
        int localTableSize = hasLocalColorTable ? 1 << ((packed & 7) + 1) : 0;

        byte[]? colorTable = globalColorTable;
        if (hasLocalColorTable)
        {
            colorTable = new byte[localTableSize * 3];
            stream.ReadExactly(colorTable);
        }

        if (colorTable == null)
            throw new InvalidDataException("GIF has no color table.");

        int minCodeSize = stream.ReadByte();
        if (minCodeSize < 2 || minCodeSize > 11)
            throw new InvalidDataException($"Invalid GIF LZW minimum code size: {minCodeSize}");

        byte[] pixelIndices = Lzw.Decompress(stream, minCodeSize);

        bool hasAlpha = transparentIndex >= 0;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);

        // Store frame position for compositing
        frame.Page = new FrameOffset { X = left, Y = top };

        int pixelIndex = 0;
        for (int y = 0; y < height; y++)
        {
            int outputY = isInterlaced ? DeinterlaceRow(y, height) : y;
            var row = frame.GetPixelRowForWrite(outputY);
            int channels = frame.NumberOfChannels;

            for (int x = 0; x < width; x++)
            {
                byte colorIndex = pixelIndex < pixelIndices.Length ? pixelIndices[pixelIndex] : (byte)0;
                pixelIndex++;

                int palOffset = colorIndex * 3;
                int dstOffset = x * channels;

                if (palOffset + 2 < colorTable.Length)
                {
                    row[dstOffset] = Quantum.ScaleFromByte(colorTable[palOffset]);
                    row[dstOffset + 1] = Quantum.ScaleFromByte(colorTable[palOffset + 1]);
                    row[dstOffset + 2] = Quantum.ScaleFromByte(colorTable[palOffset + 2]);
                }

                if (channels > 3)
                {
                    row[dstOffset + 3] = (colorIndex == transparentIndex)
                                        ? (ushort)0 : Quantum.MaxValue;
                }
            }
        }

        return frame;
    }

    #endregion

    #region Multi-Frame Write

    /// <summary>
    /// Writes an animated GIF with all frames from the sequence.
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
        WriteSequence(sequence, stream);
    }

    /// <summary>
    /// Writes an animated GIF89a with multiple frames, loop count, delays, and disposal methods.
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, Stream stream)
    {
        if (sequence.Count == 0)
            throw new ArgumentException("Sequence has no frames.", nameof(sequence));

        int canvasWidth = sequence.CanvasWidth;
        int canvasHeight = sequence.CanvasHeight;

        // Build global palette from first frame
        var firstFrame = sequence[0];
        byte[] globalPalette = new byte[256 * 3];
        var firstIndices = ArrayPool<byte>.Shared.Rent((int)firstFrame.Columns * (int)firstFrame.Rows);
        try
        {
        var (globalColorCount, firstTransparentIdx) = BuildPaletteWithAlpha(
            firstFrame, (int)firstFrame.Columns, (int)firstFrame.Rows, globalPalette, firstIndices);

        int bitsPerPixel = 1;
        while ((1 << bitsPerPixel) < globalColorCount)
            bitsPerPixel++;
        int paletteEntries = 1 << bitsPerPixel;

        // GIF Header
        stream.Write("GIF89a"u8);

        // Logical Screen Descriptor
        WriteUInt16Le(stream, canvasWidth);
        WriteUInt16Le(stream, canvasHeight);
        int packed = 0x80 | ((bitsPerPixel - 1) << 4) | (bitsPerPixel - 1);
        stream.WriteByte((byte)packed);
        stream.WriteByte(0); // Background color index
        stream.WriteByte(0); // Pixel aspect ratio

        // Global Color Table
        stream.Write(globalPalette.AsSpan(0, paletteEntries * 3));

        // NETSCAPE2.0 Application Extension (loop count)
        stream.WriteByte(ExtensionIntroducer);
        stream.WriteByte(ApplicationExtensionLabel);
        stream.WriteByte(11); // block size
        stream.Write("NETSCAPE2.0"u8);
        stream.WriteByte(3); // sub-block size
        stream.WriteByte(1); // sub-block index
        WriteUInt16Le(stream, sequence.LoopCount);
        stream.WriteByte(0); // block terminator

        // Write each frame
        for (int i = 0; i < sequence.Count; i++)
        {
            var frame = sequence[i];
            int frameWidth = (int)frame.Columns;
            int frameHeight = (int)frame.Rows;

            // Quantize this frame
            byte[] framePalette = new byte[256 * 3];
            byte[] frameIndices = new byte[frameWidth * frameHeight];
            int frameColorCount;
            int transparentIdx;

            if (i == 0)
            {
                // Reuse first frame's already-computed indices
                frameIndices = firstIndices;
                framePalette = globalPalette;
                frameColorCount = globalColorCount;
                transparentIdx = firstTransparentIdx;
            }
            else
            {
                (frameColorCount, transparentIdx) = BuildPaletteWithAlpha(
                    frame, frameWidth, frameHeight, framePalette, frameIndices);
            }

            int frameBpp = 1;
            while ((1 << frameBpp) < frameColorCount)
                frameBpp++;

            // Graphic Control Extension
            stream.WriteByte(ExtensionIntroducer);
            stream.WriteByte(GraphicControlLabel);
            stream.WriteByte(4); // block size

            int disposal = frame.DisposeMethod switch
            {
                DisposeType.None => 1,
                DisposeType.Background => 2,
                DisposeType.Previous => 3,
                _ => 0
            };

            int gcPacked = (disposal << 2) | (transparentIdx >= 0 ? 1 : 0);
            stream.WriteByte((byte)gcPacked);
            WriteUInt16Le(stream, Math.Max(frame.Delay, 0));
            stream.WriteByte(transparentIdx >= 0 ? (byte)transparentIdx : (byte)0);
            stream.WriteByte(0); // block terminator

            // Image Descriptor
            stream.WriteByte(ImageSeparator);
            int left = frame.Page.X;
            int top = frame.Page.Y;
            WriteUInt16Le(stream, left);
            WriteUInt16Le(stream, top);
            WriteUInt16Le(stream, frameWidth);
            WriteUInt16Le(stream, frameHeight);

            // Use local color table for non-first frames
            if (i == 0)
            {
                stream.WriteByte(0); // No local color table
            }
            else
            {
                int localBpp = Math.Max(frameBpp, 1);
                int localPacked = 0x80 | (localBpp - 1);
                stream.WriteByte((byte)localPacked);
                int localEntries = 1 << localBpp;
                stream.Write(framePalette.AsSpan(0, localEntries * 3));
            }

            // LZW data
            int minCodeSize = i == 0 ? Math.Max(2, bitsPerPixel) : Math.Max(2, frameBpp);
            stream.WriteByte((byte)minCodeSize);
            Lzw.Compress(stream, frameIndices, minCodeSize);
        }

        // Trailer
        stream.WriteByte(Trailer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstIndices);
        }
    }

    #endregion

    #region Helpers

    private static void SkipSubBlocks(Stream stream)
    {
        while (true)
        {
            int size = stream.ReadByte();
            if (size <= 0)
            {
                break;
            }

            if (stream.CanSeek)
            {
                stream.Seek(size, SeekOrigin.Current);
            }
            else
            {
                byte[] skip = new byte[size];
                stream.ReadExactly(skip);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt16Le(Stream stream)
    {
        int low = stream.ReadByte();
        int high = stream.ReadByte();
        return (high << 8) | low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt16Le(Stream stream, int value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    #endregion
}
