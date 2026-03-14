using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

using SharpImage.Core;
using SharpImage.Image;

/// <summary>
/// Reads and writes Windows BMP (Bitmap) files. Supports BITMAPINFOHEADER (v3), BITMAPV4HEADER, BITMAPV5HEADER. Bit
/// depths: 1, 4, 8, 16, 24, 32. Compression: BI_RGB, BI_RLE8, BI_RLE4, BI_BITFIELDS. Ported from ImageMagick
/// MagickCore/coders/bmp.c.
/// </summary>
public static class BmpCoder
{
    private const ushort BmpMagic = 0x4D42; // "BM" little-endian

    /// <summary>
    /// Detect BMP format by "BM" signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 'B' && data[1] == 'M';

    private const int FileHeaderSize = 14;

    // Compression types
    private const uint BiRgb = 0;
    private const uint BiRle8 = 1;
    private const uint BiRle4 = 2;
    private const uint BiBitfields = 3;
    private const uint BiAlphaBitfields = 6;

    // --- Reading ---

    public static ImageFrame Read(Stream stream)
    {
        Span<byte> fileHeader = stackalloc byte[FileHeaderSize];
        stream.ReadExactly(fileHeader);

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader);
        if (magic != BmpMagic)
        {
            throw new InvalidDataException("Not a valid BMP file: missing 'BM' signature.");
        }

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader[2..]);
        uint pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader[10..]);

        // Read DIB header size to determine variant
        Span<byte> sizeBytes = stackalloc byte[4];
        stream.ReadExactly(sizeBytes);
        uint dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);

        if (dibHeaderSize < 12)
        {
            throw new InvalidDataException($"Invalid DIB header size: {dibHeaderSize}");
        }

        // Read rest of DIB header
        byte[] dibHeader = ArrayPool<byte>.Shared.Rent((int)dibHeaderSize);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dibHeader, dibHeaderSize);
            stream.ReadExactly(dibHeader.AsSpan(4, (int)dibHeaderSize - 4));

            int width, height;
            bool topDown;
            ushort bitsPerPixel;
            uint compression = BiRgb;
            uint colorsUsed = 0;
            uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;

            if (dibHeaderSize == 12)
            {
                // BITMAPCOREHEADER
                width = BinaryPrimitives.ReadInt16LittleEndian(dibHeader.AsSpan(4));
                height = BinaryPrimitives.ReadInt16LittleEndian(dibHeader.AsSpan(6));
                topDown = false;
                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dibHeader.AsSpan(10));
            }
            else
            {
                // BITMAPINFOHEADER or later
                width = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(4));
                int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(8));
                topDown = rawHeight < 0;
                height = Math.Abs(rawHeight);
                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dibHeader.AsSpan(14));
                compression = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(16));
                colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(32));

                // Read bit field masks
                if (dibHeaderSize >= 52 && (compression == BiBitfields || compression == BiAlphaBitfields))
                {
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(40));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(44));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(48));
                }
                if (dibHeaderSize >= 56 && compression == BiAlphaBitfields)
                {
                    alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(52));
                }
                // V4+ headers have masks at fixed positions
                if (dibHeaderSize >= 108)
                {
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(40));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(44));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(48));
                    alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(52));
                }
            }

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"Invalid BMP dimensions: {width}x{height}");
            }

            // Read color palette
            ushort[][]? palette = null;
            if (bitsPerPixel <= 8)
            {
                int paletteCount = colorsUsed > 0 ? (int)colorsUsed : (1 << bitsPerPixel);
                int bytesPerEntry = dibHeaderSize == 12 ? 3 : 4;
                palette = ReadPalette(stream, paletteCount, bytesPerEntry,
                    (int)(pixelDataOffset - FileHeaderSize - dibHeaderSize));
            }

            // Seek to pixel data
            long currentPos = FileHeaderSize + dibHeaderSize +
                (palette != null ? palette.Length * (dibHeaderSize == 12 ? 3 : 4) : 0);
            if (compression == BiBitfields && dibHeaderSize == 40)
            {
                currentPos += 12; // 3 mask uint32s after v3 header
            }

            if (stream.CanSeek && stream.Position != pixelDataOffset)
            {
                stream.Seek(pixelDataOffset, SeekOrigin.Begin);
            }

            // Set default masks for common bit depths
            if (compression == BiRgb && bitsPerPixel == 16)
            {
                redMask = 0x7C00;
                greenMask = 0x03E0;
                blueMask = 0x001F;
            }

            bool hasAlpha = alphaMask != 0 || bitsPerPixel == 32;
            var frame = new ImageFrame();
            frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha && bitsPerPixel == 32);
            frame.FormatName = "BMP";

            // Read pixel data based on compression and bit depth
            switch (compression)
            {
                case BiRgb:
                case BiBitfields:
                case BiAlphaBitfields:
                    ReadUncompressed(stream, frame, bitsPerPixel, topDown, palette,
                        redMask, greenMask, blueMask, alphaMask);
                    break;
                case BiRle8:
                    ReadRle8(stream, frame, topDown, palette!);
                    break;
                case BiRle4:
                    ReadRle4(stream, frame, topDown, palette!);
                    break;
                default:
                    throw new NotSupportedException($"BMP compression type {compression} is not supported.");
            }

            return frame;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dibHeader);
        }
    }

    public static ImageFrame Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    // --- Writing ---

    /// <summary>
    /// Writes an ImageFrame as a 24-bit or 32-bit uncompressed BMP.
    /// </summary>
    public static void Write(ImageFrame image, Stream stream, bool includeAlpha = false)
    {
        int bitsPerPixel = includeAlpha && image.HasAlpha ? 32 : 24;
        int bytesPerPixel = bitsPerPixel / 8;
        int scanlineBytes = ((int)image.Columns * bitsPerPixel + 31) / 32 * 4;
        int imageDataSize = scanlineBytes * (int)image.Rows;
        uint dibHeaderSize = includeAlpha ? 108u : 40u; // V4 for alpha, V3 otherwise
        uint pixelDataOffset = (uint)(FileHeaderSize + dibHeaderSize);
        uint fileSize = pixelDataOffset + (uint)imageDataSize;

        // File header
        Span<byte> header = stackalloc byte[FileHeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header, BmpMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[6..], 0);       // reserved
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], pixelDataOffset);
        stream.Write(header);

        // DIB header
        byte[] dib = new byte[dibHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, dibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), (int)image.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), (int)image.Rows); // bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);             // planes
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), (ushort)bitsPerPixel);
        uint compression = includeAlpha ? BiBitfields : BiRgb;
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), (uint)imageDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(24), 2835); // ~72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(28), 2835);

        if (dibHeaderSize >= 108)
        {
            // V4 masks: BGRA
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00FF0000); // red
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000FF00); // green
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000FF); // blue
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xFF000000); // alpha
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), 0x73524742); // "sRGB" LCS_sRGB
        }

        stream.Write(dib);

        // Pixel data: bottom-up, BGR(A) order
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(scanlineBytes);
        try
        {
            for (long y = image.Rows - 1;y >= 0;y--)
            {
                Array.Clear(rowBuffer, 0, scanlineBytes);
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                for (int x = 0;x < image.Columns;x++)
                {
                    int srcOffset = x * channels;
                    int dstOffset = x * bytesPerPixel;
                    ushort r, g, b;

                    if (channels >= 3)
                    {
                        r = row[srcOffset];
                        g = row[srcOffset + 1];
                        b = row[srcOffset + 2];
                    }
                    else
                    {
                        r = g = b = row[srcOffset];
                    }

                    rowBuffer[dstOffset] = Quantum.ScaleToByte(b);
                    rowBuffer[dstOffset + 1] = Quantum.ScaleToByte(g);
                    rowBuffer[dstOffset + 2] = Quantum.ScaleToByte(r);

                    if (bytesPerPixel == 4)
                    {
                        ushort a = (channels >= 4) ? row[srcOffset + 3] : Quantum.MaxValue;
                        rowBuffer[dstOffset + 3] = Quantum.ScaleToByte(a);
                    }
                }
                stream.Write(rowBuffer, 0, scanlineBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    public static void Write(ImageFrame image, string path, bool includeAlpha = false)
    {
        using var stream = File.Create(path);
        Write(image, stream, includeAlpha);
    }

    // --- Uncompressed pixel reading ---

    private static void ReadUncompressed(Stream stream, ImageFrame frame,
        ushort bitsPerPixel, bool topDown, ushort[][]? palette,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int scanlineBytes = (width * bitsPerPixel + 31) / 32 * 4;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(scanlineBytes);

        try
        {
            for (int row = 0;row < height;row++)
            {
                int y = topDown ? row : height - 1 - row;
                stream.ReadExactly(rowBuffer.AsSpan(0, scanlineBytes));
                var pixels = frame.GetPixelRowForWrite(y);
                int channels = frame.NumberOfChannels;

                switch (bitsPerPixel)
                {
                    case 1:
                        DecodePalettized1(rowBuffer, pixels, width, channels, palette!);
                        break;
                    case 4:
                        DecodePalettized4(rowBuffer, pixels, width, channels, palette!);
                        break;
                    case 8:
                        DecodePalettized8(rowBuffer, pixels, width, channels, palette!);
                        break;
                    case 16:
                        Decode16Bit(rowBuffer, pixels, width, channels, redMask, greenMask, blueMask, alphaMask);
                        break;
                    case 24:
                        Decode24Bit(rowBuffer, pixels, width, channels);
                        break;
                    case 32:
                        Decode32Bit(rowBuffer, pixels, width, channels, alphaMask);
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodePalettized1(byte[] src, Span<ushort> dst, int width, int channels, ushort[][] palette)
    {
        for (int x = 0;x < width;x++)
        {
            int byteIndex = x >> 3;
            int bitIndex = 7 - (x & 7);
            int index = (src[byteIndex] >> bitIndex) & 1;
            WritePalettePixel(dst, x, channels, palette, index);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodePalettized4(byte[] src, Span<ushort> dst, int width, int channels, ushort[][] palette)
    {
        for (int x = 0;x < width;x++)
        {
            int byteIndex = x >> 1;
            int index = (x & 1) == 0 ? (src[byteIndex] >> 4) & 0x0F : src[byteIndex] & 0x0F;
            WritePalettePixel(dst, x, channels, palette, index);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodePalettized8(byte[] src, Span<ushort> dst, int width, int channels, ushort[][] palette)
    {
        for (int x = 0;x < width;x++)
        {
            WritePalettePixel(dst, x, channels, palette, src[x]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePalettePixel(Span<ushort> dst, int x, int channels, ushort[][] palette, int index)
    {
        int offset = x * channels;
        if (index < palette.Length)
        {
            var entry = palette[index];
            if (channels >= 3)
            {
                dst[offset] = entry[0];
                dst[offset + 1] = entry[1];
                dst[offset + 2] = entry[2];
            }
            else
            {
                dst[offset] = entry[0];
            }
        }
    }

    private static void Decode16Bit(byte[] src, Span<ushort> dst, int width, int channels,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        int redShift = BitShift(redMask);
        int greenShift = BitShift(greenMask);
        int blueShift = BitShift(blueMask);
        int redBits = BitCount(redMask);
        int greenBits = BitCount(greenMask);
        int blueBits = BitCount(blueMask);

        for (int x = 0;x < width;x++)
        {
            uint pixel = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(x * 2));
            int offset = x * channels;

            dst[offset] = ScaleBitsToQuantum((int)((pixel & redMask) >> redShift), redBits);
            dst[offset + 1] = ScaleBitsToQuantum((int)((pixel & greenMask) >> greenShift), greenBits);
            dst[offset + 2] = ScaleBitsToQuantum((int)((pixel & blueMask) >> blueShift), blueBits);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Decode24Bit(byte[] src, Span<ushort> dst, int width, int channels)
    {
        for (int x = 0;x < width;x++)
        {
            int srcOffset = x * 3;
            int dstOffset = x * channels;
            dst[dstOffset + 2] = Quantum.ScaleFromByte(src[srcOffset]);     // B → B
            dst[dstOffset + 1] = Quantum.ScaleFromByte(src[srcOffset + 1]); // G → G
            dst[dstOffset] = Quantum.ScaleFromByte(src[srcOffset + 2]);     // R → R
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Decode32Bit(byte[] src, Span<ushort> dst, int width, int channels, uint alphaMask)
    {
        for (int x = 0;x < width;x++)
        {
            int srcOffset = x * 4;
            int dstOffset = x * channels;
            dst[dstOffset + 2] = Quantum.ScaleFromByte(src[srcOffset]);     // B
            dst[dstOffset + 1] = Quantum.ScaleFromByte(src[srcOffset + 1]); // G
            dst[dstOffset] = Quantum.ScaleFromByte(src[srcOffset + 2]);     // R

            if (channels >= 4)
            {
                dst[dstOffset + 3] = Quantum.ScaleFromByte(src[srcOffset + 3]); // A
            }
        }
    }

    // --- RLE8 decoding ---

    private static void ReadRle8(Stream stream, ImageFrame frame, bool topDown, ushort[][] palette)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        int x = 0, y = topDown ? 0 : height - 1;
        int yStep = topDown ? 1 : -1;

        while (true)
        {
            int count = stream.ReadByte();
            int value = stream.ReadByte();
            if (count < 0 || value < 0)
            {
                break;
            }

            if (count > 0)
            {
                // Encoded run
                var row = frame.GetPixelRowForWrite(y);
                for (int i = 0;i < count && x < width;i++, x++)
                {
                    WritePalettePixel(row, x, channels, palette, value);
                }
            }
            else
            {
                // Escape
                switch (value)
                {
                    case 0: // End of line
                        x = 0;
                        y += yStep;
                        break;
                    case 1: // End of bitmap
                        return;
                    case 2: // Delta
                        int dx = stream.ReadByte();
                        int dy = stream.ReadByte();
                        if (dx < 0 || dy < 0)
                        {
                            return;
                        }

                        x += dx;
                        y += dy * yStep;
                        break;
                    default: // Absolute mode
                        var absRow = frame.GetPixelRowForWrite(y);
                        for (int i = 0;i < value && x < width;i++, x++)
                        {
                            int idx = stream.ReadByte();
                            if (idx < 0)
                            {
                                return;
                            }

                            WritePalettePixel(absRow, x, channels, palette, idx);
                        }
                        if ((value & 1) != 0)
                        {
                            stream.ReadByte(); // pad to even
                        }

                        break;
                }
            }

            if (y < 0 || y >= height)
            {
                break;
            }
        }
    }

    // --- RLE4 decoding ---

    private static void ReadRle4(Stream stream, ImageFrame frame, bool topDown, ushort[][] palette)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        int x = 0, y = topDown ? 0 : height - 1;
        int yStep = topDown ? 1 : -1;

        while (true)
        {
            int count = stream.ReadByte();
            int value = stream.ReadByte();
            if (count < 0 || value < 0)
            {
                break;
            }

            if (count > 0)
            {
                var row = frame.GetPixelRowForWrite(y);
                int hiNibble = (value >> 4) & 0x0F;
                int loNibble = value & 0x0F;
                for (int i = 0;i < count && x < width;i++, x++)
                {
                    WritePalettePixel(row, x, channels, palette, (i & 1) == 0 ? hiNibble : loNibble);
                }
            }
            else
            {
                switch (value)
                {
                    case 0:
                        x = 0;
                        y += yStep;
                        break;
                    case 1:
                        return;
                    case 2:
                        int dx = stream.ReadByte();
                        int dy = stream.ReadByte();
                        if (dx < 0 || dy < 0)
                        {
                            return;
                        }

                        x += dx;
                        y += dy * yStep;
                        break;
                    default:
                        var absRow = frame.GetPixelRowForWrite(y);
                        int bytesRead = 0;
                        for (int i = 0;i < value && x < width;i++, x++)
                        {
                            int idx;
                            if ((i & 1) == 0)
                            {
                                int b = stream.ReadByte();
                                if (b < 0)
                                {
                                    return;
                                }

                                bytesRead++;
                                idx = (b >> 4) & 0x0F;
                                // Save low nibble for next pixel
                                if (i + 1 < value && x + 1 < width)
                                {
                                    WritePalettePixel(absRow, x, channels, palette, idx);
                                    x++;
                                    i++;
                                    WritePalettePixel(absRow, x, channels, palette, b & 0x0F);
                                    continue;
                                }
                            }
                            else
                            {
                                continue; // Already handled
                            }
                            WritePalettePixel(absRow, x, channels, palette, idx);
                        }
                        if ((bytesRead & 1) != 0)
                        {
                            stream.ReadByte();
                        }

                        break;
                }
            }

            if (y < 0 || y >= height)
            {
                break;
            }
        }
    }

    // --- Palette reading ---

    private static ushort[][] ReadPalette(Stream stream, int count, int bytesPerEntry, int totalPaletteBytes)
    {
        int actualBytes = count * bytesPerEntry;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(actualBytes, totalPaletteBytes));
        try
        {
            int toRead = totalPaletteBytes > 0 ? totalPaletteBytes : actualBytes;
            stream.ReadExactly(buffer.AsSpan(0, toRead));

            var palette = new ushort[count][];
            for (int i = 0;i < count;i++)
            {
                int offset = i * bytesPerEntry;
                ushort b = Quantum.ScaleFromByte(buffer[offset]);
                ushort g = Quantum.ScaleFromByte(buffer[offset + 1]);
                ushort r = Quantum.ScaleFromByte(buffer[offset + 2]);
                palette[i] = [ r, g, b ];
            }
            return palette;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // --- Bit manipulation helpers ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitShift(uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }
        return shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitCount(uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        mask >>= BitShift(mask);
        int count = 0;
        while ((mask & 1) == 1)
        {
            mask >>= 1;
            count++;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleBitsToQuantum(int value, int bits)
    {
        if (bits <= 0 || bits >= 16)
        {
            return (ushort)value;
        }

        int max = (1 << bits) - 1;
        return (ushort)(value * Quantum.MaxValue / max);
    }
}
