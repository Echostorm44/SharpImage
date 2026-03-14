using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

using SharpImage.Core;
using SharpImage.Image;

/// <summary>
/// Reads and writes Truevision TGA (Targa) files. Supports uncompressed and RLE true-color, grayscale, and color-mapped
/// images. Bit depths: 8, 16, 24, 32. Ported from ImageMagick MagickCore/coders/tga.c.
/// </summary>
public static class TgaCoder
{
    /// <summary>
    /// Detect TGA format by checking valid image type and bit depth in header.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            return false;
        }

        byte imageType = data[2];
        byte bpp = data[16];
        bool validType = imageType is 1 or 2 or 3 or 9 or 10 or 11;
        bool validBpp = bpp is 8 or 15 or 16 or 24 or 32;
        return validType && validBpp;
    }

    private const int HeaderSize = 18;

    // Image types
    private const byte TypeColorMapped = 1;
    private const byte TypeTrueColor = 2;
    private const byte TypeGrayscale = 3;
    private const byte TypeRleColorMapped = 9;
    private const byte TypeRleTrueColor = 10;
    private const byte TypeRleGrayscale = 11;

    // --- Reading ---

    public static ImageFrame Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        byte idLength = header[0];
        byte colormapType = header[1];
        byte imageType = header[2];
        ushort colormapIndex = BinaryPrimitives.ReadUInt16LittleEndian(header[3..]);
        ushort colormapLength = BinaryPrimitives.ReadUInt16LittleEndian(header[5..]);
        byte colormapEntrySize = header[7];
        // ushort xOrigin = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        // ushort yOrigin = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        byte bitsPerPixel = header[16];
        byte descriptor = header[17];

        if (width == 0 || height == 0)
        {
            throw new InvalidDataException($"Invalid TGA dimensions: {width}x{height}");
        }

        bool topDown = (descriptor & 0x20) != 0;
        int alphaBits = descriptor & 0x0F;

        // Skip image ID
        if (idLength > 0)
        {
            byte[] skip = ArrayPool<byte>.Shared.Rent(idLength);
            try
            {
                stream.ReadExactly(skip.AsSpan(0, idLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(skip);
            }
        }

        // Read colormap if present
        ushort[][]? colormap = null;
        if (colormapType == 1 && colormapLength > 0)
        {
            colormap = ReadColormap(stream, colormapLength, colormapEntrySize);
        }

        bool isGrayscale = imageType == TypeGrayscale || imageType == TypeRleGrayscale;
        bool isRle = imageType >= 9;
        bool hasAlpha = (bitsPerPixel == 32) || (alphaBits > 0 && bitsPerPixel == 16);

        var colorspace = isGrayscale ? ColorspaceType.Gray : ColorspaceType.SRGB;
        var frame = new ImageFrame();
        frame.Initialize(width, height, colorspace, hasAlpha && !isGrayscale);
        frame.FormatName = "TGA";

        if (isRle)
        {
            ReadRlePixels(stream, frame, bitsPerPixel, topDown, colormap, isGrayscale);
        }
        else
        {
            ReadUncompressedPixels(stream, frame, bitsPerPixel, topDown, colormap, isGrayscale);
        }

        return frame;
    }

    public static ImageFrame Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    // --- Writing ---

    /// <summary>
    /// Writes an ImageFrame as an uncompressed 24-bit or 32-bit TGA.
    /// </summary>
    public static void Write(ImageFrame image, Stream stream, bool includeAlpha = false)
    {
        bool writeAlpha = includeAlpha && image.HasAlpha;
        byte bitsPerPixel = writeAlpha ? (byte)32 : (byte)24;
        bool isGrayscale = image.Colorspace == ColorspaceType.Gray && image.NumberOfChannels == 1;
        byte imageType = isGrayscale ? TypeGrayscale : TypeTrueColor;

        if (isGrayscale)
        {
            bitsPerPixel = 8;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        header[2] = imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)image.Columns);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)image.Rows);
        header[16] = bitsPerPixel;
        header[17] = (byte)(0x20 | (writeAlpha ? 8 : 0)); // top-down + alpha bits
        stream.Write(header);

        int bytesPerPixel = bitsPerPixel / 8;
        int rowBytes = (int)image.Columns * bytesPerPixel;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(rowBytes);

        try
        {
            for (int y = 0;y < image.Rows;y++)
            {
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                if (isGrayscale)
                {
                    for (int x = 0;x < image.Columns;x++)
                    {
                        rowBuffer[x] = Quantum.ScaleToByte(row[x * channels]);
                    }
                }
                else
                {
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

                        if (writeAlpha)
                        {
                            ushort a = channels >= 4 ? row[srcOffset + 3] : Quantum.MaxValue;
                            rowBuffer[dstOffset + 3] = Quantum.ScaleToByte(a);
                        }
                    }
                }

                stream.Write(rowBuffer, 0, rowBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        // Write TGA 2.0 footer
        Span<byte> footer = stackalloc byte[26];
        footer.Clear();
        "TRUEVISION-XFILE."u8.CopyTo(footer[8..]);
        stream.Write(footer);
    }

    public static void Write(ImageFrame image, string path, bool includeAlpha = false)
    {
        using var stream = File.Create(path);
        Write(image, stream, includeAlpha);
    }

    // --- Uncompressed pixel reading ---

    private static void ReadUncompressedPixels(Stream stream, ImageFrame frame,
        byte bitsPerPixel, bool topDown, ushort[][]? colormap, bool isGrayscale)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int bytesPerPixel = (bitsPerPixel + 7) / 8;
        int rowBytes = width * bytesPerPixel;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(rowBytes);

        try
        {
            for (int row = 0;row < height;row++)
            {
                int y = topDown ? row : height - 1 - row;
                stream.ReadExactly(rowBuffer.AsSpan(0, rowBytes));
                var pixels = frame.GetPixelRowForWrite(y);
                DecodeRow(rowBuffer, pixels, width, frame.NumberOfChannels,
                    bitsPerPixel, colormap, isGrayscale);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    // --- RLE pixel reading ---

    private static void ReadRlePixels(Stream stream, ImageFrame frame,
        byte bitsPerPixel, bool topDown, ushort[][]? colormap, bool isGrayscale)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int channels = frame.NumberOfChannels;
        int bytesPerPixel = (bitsPerPixel + 7) / 8;

        byte[] pixelBuffer = new byte[bytesPerPixel];
        int x = 0;
        int y = topDown ? 0 : height - 1;
        int yStep = topDown ? 1 : -1;
        int totalPixels = width * height;
        int pixelCount = 0;

        while (pixelCount < totalPixels)
        {
            int packetHeader = stream.ReadByte();
            if (packetHeader < 0)
            {
                break;
            }

            int count = (packetHeader & 0x7F) + 1;
            bool isRunLength = (packetHeader & 0x80) != 0;

            if (isRunLength)
            {
                stream.ReadExactly(pixelBuffer.AsSpan(0, bytesPerPixel));
                for (int i = 0;i < count && pixelCount < totalPixels;i++)
                {
                    var row = frame.GetPixelRowForWrite(y);
                    WriteDecodedPixel(pixelBuffer, row, x, channels, bitsPerPixel, colormap, isGrayscale);
                    x++;
                    pixelCount++;
                    if (x >= width)
                    {
                        x = 0;
                        y += yStep;
                    }
                }
            }
            else
            {
                for (int i = 0;i < count && pixelCount < totalPixels;i++)
                {
                    stream.ReadExactly(pixelBuffer.AsSpan(0, bytesPerPixel));
                    var row = frame.GetPixelRowForWrite(y);
                    WriteDecodedPixel(pixelBuffer, row, x, channels, bitsPerPixel, colormap, isGrayscale);
                    x++;
                    pixelCount++;
                    if (x >= width)
                    {
                        x = 0;
                        y += yStep;
                    }
                }
            }
        }
    }

    private static void DecodeRow(byte[] src, Span<ushort> dst, int width, int channels,
        byte bitsPerPixel, ushort[][]? colormap, bool isGrayscale)
    {
        int bytesPerPixel = (bitsPerPixel + 7) / 8;
        for (int x = 0;x < width;x++)
        {
            int srcOffset = x * bytesPerPixel;
            WriteDecodedPixel(src.AsSpan(srcOffset, bytesPerPixel), dst, x, channels,
                bitsPerPixel, colormap, isGrayscale);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteDecodedPixel(ReadOnlySpan<byte> src, Span<ushort> dst, int x,
        int channels, byte bitsPerPixel, ushort[][]? colormap, bool isGrayscale)
    {
        int dstOffset = x * channels;

        if (isGrayscale)
        {
            dst[dstOffset] = Quantum.ScaleFromByte(src[0]);
            return;
        }

        if (colormap != null)
        {
            int index = src[0];
            if (index < colormap.Length)
            {
                var entry = colormap[index];
                dst[dstOffset] = entry[0];
                dst[dstOffset + 1] = entry[1];
                dst[dstOffset + 2] = entry[2];
                if (channels >= 4 && entry.Length >= 4)
                {
                    dst[dstOffset + 3] = entry[3];
                }
            }
            return;
        }

        switch (bitsPerPixel)
        {
            case 16:
            {
                ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(src);
                dst[dstOffset] = ScaleBits((pixel >> 10) & 0x1F, 5);     // R
                dst[dstOffset + 1] = ScaleBits((pixel >> 5) & 0x1F, 5);  // G
                dst[dstOffset + 2] = ScaleBits(pixel & 0x1F, 5);         // B
                if (channels >= 4)
                {
                    dst[dstOffset + 3] = (pixel & 0x8000) != 0 ? Quantum.MaxValue : (ushort)0;
                }

                break;
            }
            case 24:
                dst[dstOffset + 2] = Quantum.ScaleFromByte(src[0]); // B
                dst[dstOffset + 1] = Quantum.ScaleFromByte(src[1]); // G
                dst[dstOffset] = Quantum.ScaleFromByte(src[2]);     // R
                break;
            case 32:
                dst[dstOffset + 2] = Quantum.ScaleFromByte(src[0]); // B
                dst[dstOffset + 1] = Quantum.ScaleFromByte(src[1]); // G
                dst[dstOffset] = Quantum.ScaleFromByte(src[2]);     // R
                if (channels >= 4)
                {
                    dst[dstOffset + 3] = Quantum.ScaleFromByte(src[3]); // A
                }

                break;
        }
    }

    // --- Colormap reading ---

    private static ushort[][] ReadColormap(Stream stream, int count, byte entryBits)
    {
        int bytesPerEntry = (entryBits + 7) / 8;
        int totalBytes = count * bytesPerEntry;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);

        try
        {
            stream.ReadExactly(buffer.AsSpan(0, totalBytes));
            var colormap = new ushort[count][];

            for (int i = 0;i < count;i++)
            {
                int offset = i * bytesPerEntry;
                switch (entryBits)
                {
                    case 8:
                        colormap[i] = [ Quantum.ScaleFromByte(buffer[offset]), Quantum.ScaleFromByte(buffer[offset]), Quantum.ScaleFromByte(buffer[offset]) ];
                        break;
                    case 15:
                    case 16:
                    {
                        ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
                        colormap[i] = [ ScaleBits((pixel >> 10) & 0x1F, 5), ScaleBits((pixel >> 5) & 0x1F, 5), ScaleBits(pixel & 0x1F, 5) ];
                        break;
                    }
                    case 24:
                        colormap[i] = [ Quantum.ScaleFromByte(buffer[offset + 2]), Quantum.ScaleFromByte(buffer[offset + 1]), Quantum.ScaleFromByte(buffer[offset]) ];
                        break;
                    case 32:
                        colormap[i] = [ Quantum.ScaleFromByte(buffer[offset + 2]), Quantum.ScaleFromByte(buffer[offset + 1]), Quantum.ScaleFromByte(buffer[offset]), Quantum.ScaleFromByte(buffer[offset + 3]) ];
                        break;
                    default:
                        colormap[i] = [ 0, 0, 0 ];
                        break;
                }
            }
            return colormap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleBits(int value, int bits)
    {
        int max = (1 << bits) - 1;
        return (ushort)(value * Quantum.MaxValue / max);
    }
}
