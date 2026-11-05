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
    internal const byte ImageSeparator = 0x2C;
    internal const byte ExtensionIntroducer = 0x21;
    internal const byte Trailer = 0x3B;
    internal const byte GraphicControlLabel = 0xF9;
    internal const byte ApplicationExtensionLabel = 0xFF;
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
        // the rented buffer can be longer than the image: compress only its pixels
        Lzw.Compress(stream, indices.AsSpan(0, width * height), minCodeSize);

        // Trailer
        stream.WriteByte(Trailer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indices);
        }
    }

    /// <summary>
    /// Builds the frame's palette and index map. Pixels with alpha below one half are transparent (index 0, reserved
    /// when the frame has any). When the opaque colours fit in the palette they are kept exactly; otherwise the palette
    /// is a median cut of the opaque pixels and they are mapped with Floyd-Steinberg error diffusion.
    /// Returns the number of palette entries used and the transparent index (-1 if none).
    /// </summary>
    private static (int colorCount, int transparentIndex) BuildPaletteWithAlpha(
        ImageFrame image, int width, int height, byte[] palette, byte[] indices)
    {
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        bool gray = channels - (hasAlpha ? 1 : 0) == 1;
        int n = width * height;
        var rgb = new byte[n * 3];
        var transparent = new bool[n];
        bool anyTransparent = false;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * channels, p = y * width + x;
                if (hasAlpha && row[o + channels - 1] < 32768)
                {
                    transparent[p] = true;
                    anyTransparent = true;
                    continue;
                }
                rgb[p * 3] = Quantum.ScaleToByte(row[o]);
                rgb[p * 3 + 1] = Quantum.ScaleToByte(gray ? row[o] : row[o + 1]);
                rgb[p * 3 + 2] = Quantum.ScaleToByte(gray ? row[o] : row[o + 2]);
            }
        }

        int first = anyTransparent ? 1 : 0;          // index 0 is the transparent colour
        int limit = 256 - first;
        int transparentIdx = anyTransparent ? 0 : -1;
        if (anyTransparent) palette[0] = palette[1] = palette[2] = 0;

        // Exact palette when the opaque colours fit.
        var colorMap = new Dictionary<int, int>(limit);
        bool fits = true;
        for (int p = 0; p < n && fits; p++)
        {
            if (transparent[p]) continue;
            int key = (rgb[p * 3] << 16) | (rgb[p * 3 + 1] << 8) | rgb[p * 3 + 2];
            if (!colorMap.ContainsKey(key))
            {
                if (colorMap.Count == limit) fits = false;
                else colorMap[key] = first + colorMap.Count;
            }
        }
        if (fits)
        {
            foreach (var (key, idx) in colorMap)
            {
                palette[idx * 3] = (byte)(key >> 16);
                palette[idx * 3 + 1] = (byte)(key >> 8);
                palette[idx * 3 + 2] = (byte)key;
            }
            for (int p = 0; p < n; p++)
                indices[p] = transparent[p] ? (byte)0 : (byte)colorMap[(rgb[p * 3] << 16) | (rgb[p * 3 + 1] << 8) | rgb[p * 3 + 2]];
            return (Math.Max(first + colorMap.Count, 2), transparentIdx);
        }

        // Median-cut palette of the opaque pixels.
        var opaque = new byte[n * 3];
        int m = 0;
        for (int p = 0; p < n; p++)
        {
            if (transparent[p]) continue;
            opaque[m * 3] = rgb[p * 3];
            opaque[m * 3 + 1] = rgb[p * 3 + 1];
            opaque[m * 3 + 2] = rgb[p * 3 + 2];
            m++;
        }
        byte[][] cut = Quantize.ColorQuantize.MedianCutPalette(opaque, m, limit);
        int count = first + cut.Length;
        for (int k = 0; k < cut.Length; k++)
        {
            palette[(first + k) * 3] = cut[k][0];
            palette[(first + k) * 3 + 1] = cut[k][1];
            palette[(first + k) * 3 + 2] = cut[k][2];
        }

        // Floyd-Steinberg error diffusion over the opaque pixels (errors in 1/16 units, two rows).
        var errCur = new int[(width + 2) * 3];
        var errNext = new int[(width + 2) * 3];
        var nearestCache = new short[1 << 18];   // 6 bits per channel -> nearest index + 1
        byte[] cutPalette = palette[(first * 3)..(count * 3)];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(errNext);
            for (int x = 0; x < width; x++)
            {
                int p = y * width + x;
                if (transparent[p])
                {
                    indices[p] = 0;
                    continue;
                }
                int e = (x + 1) * 3;
                int r = Math.Clamp(rgb[p * 3] + (errCur[e] >> 4), 0, 255);
                int g = Math.Clamp(rgb[p * 3 + 1] + (errCur[e + 1] >> 4), 0, 255);
                int b = Math.Clamp(rgb[p * 3 + 2] + (errCur[e + 2] >> 4), 0, 255);
                int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
                int idx = nearestCache[key] - 1;
                if (idx < 0)
                {
                    idx = first + FindNearestColor(cutPalette, cut.Length, (byte)r, (byte)g, (byte)b);
                    nearestCache[key] = (short)(idx + 1);
                }
                indices[p] = (byte)idx;
                int dr = r - palette[idx * 3], dg = g - palette[idx * 3 + 1], db = b - palette[idx * 3 + 2];
                // 7/16 right, 3/16 below-left, 5/16 below, 1/16 below-right
                errCur[e + 3] += dr * 7; errCur[e + 4] += dg * 7; errCur[e + 5] += db * 7;
                errNext[e - 3] += dr * 3; errNext[e - 2] += dg * 3; errNext[e - 1] += db * 3;
                errNext[e] += dr * 5; errNext[e + 1] += dg * 5; errNext[e + 2] += db * 5;
                errNext[e + 3] += dr; errNext[e + 4] += dg; errNext[e + 5] += db;
            }
            (errCur, errNext) = (errNext, errCur);
        }
        return (Math.Max(count, 2), transparentIdx);
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
    /// Reads all frames from an animated GIF stream, returning an ImageSequence. Every frame is held in memory;
    /// <see cref="GifFrameReader"/> reads one at a time instead.
    /// </summary>
    public static ImageSequence ReadSequence(Stream stream)
    {
        var reader = new GifFrameReader(stream);
        var sequence = new ImageSequence
        {
            CanvasWidth = reader.CanvasWidth,
            CanvasHeight = reader.CanvasHeight,
            BackgroundColorIndex = reader.BackgroundColorIndex,
            FormatName = "GIF"
        };

        while (reader.ReadNextFrame() is { } frame)
        {
            sequence.AddFrame(frame);
        }
        sequence.LoopCount = reader.LoopCount;

        if (sequence.Count == 0)
            throw new InvalidDataException("GIF contains no image data.");

        return sequence;
    }

    /// <summary>
    /// Reads a single frame's image data from the stream. Like ReadImageData but also reads
    /// the frame's position offset for sub-frame GIFs.
    /// </summary>
    internal static ImageFrame ReadFrameData(Stream stream, int canvasWidth, int canvasHeight,
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
            // frame 0 uses the rented buffer, which can be longer than the frame
            Lzw.Compress(stream, frameIndices.AsSpan(0, frameWidth * frameHeight), minCodeSize);
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

    internal static void SkipSubBlocks(Stream stream)
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
    internal static int ReadUInt16Le(Stream stream)
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

/// <summary>
/// Reads an animated GIF one frame at a time, so a long or large animation can be played without holding every
/// frame in memory. Frames come back as stored (sub-rectangles at <see cref="ImageFrame.Page"/>, with
/// <see cref="ImageFrame.Delay"/> and <see cref="ImageFrame.DisposeMethod"/> set); compositing them onto the canvas
/// is the caller's. <see cref="Rewind"/> starts again from the first frame, for looping. Not thread-safe.
/// </summary>
public sealed class GifFrameReader
{
    private readonly Stream stream;
    private readonly byte[]? globalColorTable;
    private readonly long firstBlock;
    private bool ended;

    /// <summary>Reads the header and global colour table. The stream must be seekable to <see cref="Rewind"/>.</summary>
    public GifFrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;

        Span<byte> header = stackalloc byte[6];
        stream.ReadExactly(header);
        if (!header.SequenceEqual("GIF87a"u8) && !header.SequenceEqual("GIF89a"u8))
            throw new InvalidDataException("Not a valid GIF file.");

        CanvasWidth = GifCoder.ReadUInt16Le(stream);
        CanvasHeight = GifCoder.ReadUInt16Le(stream);
        if (CanvasWidth <= 0 || CanvasHeight <= 0)
            throw new InvalidDataException($"Invalid GIF canvas dimensions: {CanvasWidth}x{CanvasHeight}");
        int packed = stream.ReadByte();
        BackgroundColorIndex = stream.ReadByte();
        stream.ReadByte(); // aspect ratio

        if ((packed & 0x80) != 0)
        {
            globalColorTable = new byte[(1 << ((packed & 7) + 1)) * 3];
            stream.ReadExactly(globalColorTable);
        }

        firstBlock = stream.CanSeek ? stream.Position : -1;
    }

    /// <summary>Canvas (logical screen) width.</summary>
    public int CanvasWidth { get; }

    /// <summary>Canvas (logical screen) height.</summary>
    public int CanvasHeight { get; }

    /// <summary>Background colour index from the logical screen descriptor.</summary>
    public int BackgroundColorIndex { get; }

    /// <summary>NETSCAPE2.0 loop count (0 = forever), known once the frames before it have been read.</summary>
    public int LoopCount { get; private set; }

    /// <summary>The next frame, or null after the last one. The caller owns (and disposes) each frame.</summary>
    public ImageFrame? ReadNextFrame()
    {
        int transparentIndex = -1;
        int disposalMethod = 0;
        int delay = 0;

        Span<byte> appId = stackalloc byte[11];
        while (!ended)
        {
            int blockType = stream.ReadByte();
            if (blockType < 0 || blockType == GifCoder.Trailer)
            {
                ended = true;
                break;
            }

            if (blockType == GifCoder.ExtensionIntroducer)
            {
                int label = stream.ReadByte();
                if (label == GifCoder.GraphicControlLabel)
                {
                    stream.ReadByte(); // block size (always 4)
                    int gcPacked = stream.ReadByte();
                    disposalMethod = (gcPacked >> 2) & 7;
                    bool hasTransparency = (gcPacked & 1) != 0;
                    delay = GifCoder.ReadUInt16Le(stream);
                    int transIdx = stream.ReadByte();
                    transparentIndex = hasTransparency ? transIdx : -1;
                    stream.ReadByte(); // block terminator
                }
                else if (label == GifCoder.ApplicationExtensionLabel)
                {
                    ReadApplicationExtension(appId);
                }
                else
                {
                    GifCoder.SkipSubBlocks(stream);
                }
            }
            else if (blockType == GifCoder.ImageSeparator)
            {
                var frame = GifCoder.ReadFrameData(stream, CanvasWidth, CanvasHeight, globalColorTable, transparentIndex);
                frame.Delay = delay;
                frame.DisposeMethod = disposalMethod switch
                {
                    0 => DisposeType.Undefined,
                    1 => DisposeType.None,
                    2 => DisposeType.Background,
                    3 => DisposeType.Previous,
                    _ => DisposeType.Undefined
                };
                frame.Iterations = LoopCount;
                return frame;
            }
        }
        return null;
    }

    /// <summary>Goes back to the first frame.</summary>
    public void Rewind()
    {
        if (firstBlock < 0)
            throw new NotSupportedException("Rewinding needs a seekable stream.");
        stream.Position = firstBlock;
        ended = false;
    }

    private void ReadApplicationExtension(Span<byte> appId)
    {
        // NETSCAPE2.0 carries the loop count; anything else is skipped.
        int blockSize = stream.ReadByte();
        if (blockSize != 11)
        {
            if (blockSize > 0)
                stream.ReadExactly(new byte[blockSize]);
            GifCoder.SkipSubBlocks(stream);
            return;
        }

        stream.ReadExactly(appId);
        if (!appId.SequenceEqual("NETSCAPE2.0"u8))
        {
            GifCoder.SkipSubBlocks(stream);
            return;
        }

        int subBlockSize = stream.ReadByte();
        if (subBlockSize == 3)
        {
            stream.ReadByte(); // sub-block index (1)
            LoopCount = GifCoder.ReadUInt16Le(stream);
            stream.ReadByte(); // block terminator
            return;
        }

        if (subBlockSize > 0)
            stream.ReadExactly(new byte[subBlockSize]);
        GifCoder.SkipSubBlocks(stream);
    }
}
