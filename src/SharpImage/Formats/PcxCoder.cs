// PCX (ZSoft Paintbrush) format coder — read and write.
// Supports 8-bit indexed (256-color) and 24-bit RGB (3-plane) images.
// Uses RLE compression. 128-byte header.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class PcxCoder
{
    private const byte PcxMagic = 0x0A;
    private const int HeaderSize = 128;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[0] == PcxMagic &&
        (data[2] == 0 || data[2] == 1); // encoding: 0=raw, 1=RLE

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid PCX file");
        }

        byte version = data[1];
        byte encoding = data[2];
        byte bitsPerPixel = data[3];
        int xMin = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int yMin = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int xMax = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int yMax = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        byte planes = data[65];
        int bytesPerLine = BinaryPrimitives.ReadUInt16LittleEndian(data[66..]);

        int width = xMax - xMin + 1;
        int height = yMax - yMin + 1;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid PCX dimensions: {width}x{height}");
        }

        var frame = new ImageFrame();
        bool is24Bit = planes == 3 && bitsPerPixel == 8;
        bool is8BitIndexed = planes == 1 && bitsPerPixel == 8;
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        int pos = HeaderSize;
        int totalBytesPerScanline = bytesPerLine * planes;
        var scanlineBuffer = new byte[totalBytesPerScanline];

        for (int y = 0;y < height;y++)
        {
            // Decode RLE scanline
            if (encoding == 1)
            {
                pos = DecompressRleScanline(data, pos, scanlineBuffer);
            }
            else
            {
                int toCopy = Math.Min(totalBytesPerScanline, data.Length - pos);
                data.Slice(pos, toCopy).CopyTo(scanlineBuffer);
                pos += toCopy;
            }

            var row = frame.GetPixelRowForWrite(y);

            if (is24Bit)
            {
                // 3-plane RGB: R plane, G plane, B plane each bytesPerLine long
                for (int x = 0;x < width;x++)
                {
                    byte r = scanlineBuffer[x];
                    byte g = scanlineBuffer[bytesPerLine + x];
                    byte b = scanlineBuffer[bytesPerLine * 2 + x];
                    int offset = x * channels;
                    row[offset] = Quantum.ScaleFromByte(r);
                    if (channels > 1)
                    {
                        row[offset + 1] = Quantum.ScaleFromByte(g);
                    }

                    if (channels > 2)
                    {
                        row[offset + 2] = Quantum.ScaleFromByte(b);
                    }
                }
            }
            else if (is8BitIndexed)
            {
                // Store indices temporarily — palette applied after all scanlines
                for (int x = 0;x < width;x++)
                {
                    byte idx = scanlineBuffer[x];
                    int offset = x * channels;
                    // Temporarily store index in red channel
                    row[offset] = idx;
                    if (channels > 1)
                    {
                        row[offset + 1] = idx;
                    }

                    if (channels > 2)
                    {
                        row[offset + 2] = idx;
                    }
                }
            }
        }

        // Apply 256-color palette for indexed images
        if (is8BitIndexed)
        {
            // Palette is at end of file: 0x0C marker + 768 bytes (256 × RGB)
            int paletteOffset = data.Length - 769;
            if (paletteOffset > 0 && data[paletteOffset] == 0x0C)
            {
                paletteOffset++; // skip marker
                for (int y = 0;y < height;y++)
                {
                    var row = frame.GetPixelRowForWrite(y);
                    for (int x = 0;x < width;x++)
                    {
                        int offset = x * channels;
                        int idx = row[offset]; // stored index
                        int palOff = paletteOffset + idx * 3;
                        if (palOff + 2 < data.Length)
                        {
                            row[offset] = Quantum.ScaleFromByte(data[palOff]);
                            if (channels > 1)
                            {
                                row[offset + 1] = Quantum.ScaleFromByte(data[palOff + 1]);
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = Quantum.ScaleFromByte(data[palOff + 2]);
                            }
                        }
                    }
                }
            }
            else
            {
                // Use header palette (16 colors) or grayscale fallback
                for (int y = 0;y < height;y++)
                {
                    var row = frame.GetPixelRowForWrite(y);
                    for (int x = 0;x < width;x++)
                    {
                        int offset = x * channels;
                        byte idx = (byte)row[offset];
                        if (idx < 16)
                        {
                            int palOff = 16 + idx * 3; // header palette at offset 16
                            row[offset] = Quantum.ScaleFromByte(data[palOff]);
                            if (channels > 1)
                            {
                                row[offset + 1] = Quantum.ScaleFromByte(data[palOff + 1]);
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = Quantum.ScaleFromByte(data[palOff + 2]);
                            }
                        }
                        else
                        {
                            ushort gray = Quantum.ScaleFromByte(idx);
                            row[offset] = gray;
                            if (channels > 1)
                            {
                                row[offset + 1] = gray;
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = gray;
                            }
                        }
                    }
                }
            }
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;

        // Write 24-bit 3-plane PCX
        int bytesPerLine = (w + 1) & ~1; // padded to even
        using var ms = new MemoryStream();

        // Header
        var header = new byte[HeaderSize];
        header[0] = PcxMagic;          // magic
        header[1] = 5;                 // version 3.0
        header[2] = 1;                 // RLE encoding
        header[3] = 8;                 // bits per pixel
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);       // xMin
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);       // yMin
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(w - 1));  // xMax
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(h - 1)); // yMax
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 72);     // hDpi
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 72);     // vDpi
        header[65] = 3;                // 3 planes (RGB)
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)bytesPerLine);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68), 1);      // palette info: color
        ms.Write(header);

        // Pixel data (RLE encoded, 3 planes per scanline)
        var scanline = new byte[bytesPerLine * 3];
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);

            // Fill R plane
            for (int x = 0;x < w;x++)
            {
                scanline[x] = Quantum.ScaleToByte(row[x * imgChannels]);
            }

            for (int x = w;x < bytesPerLine;x++)
            {
                scanline[x] = 0;
            }

            // Fill G plane
            for (int x = 0;x < w;x++)
            {
                scanline[bytesPerLine + x] = imgChannels > 1
                                ? Quantum.ScaleToByte(row[x * imgChannels + 1])
                                : scanline[x];
            }

            for (int x = w;x < bytesPerLine;x++)
            {
                scanline[bytesPerLine + x] = 0;
            }

            // Fill B plane
            for (int x = 0;x < w;x++)
            {
                scanline[bytesPerLine * 2 + x] = imgChannels > 2
                                ? Quantum.ScaleToByte(row[x * imgChannels + 2])
                                : scanline[x];
            }

            for (int x = w;x < bytesPerLine;x++)
            {
                scanline[bytesPerLine * 2 + x] = 0;
            }

            CompressRleScanline(ms, scanline);
        }

        return ms.ToArray();
    }

    private static int DecompressRleScanline(ReadOnlySpan<byte> data, int pos, byte[] buffer)
    {
        int filled = 0;
        while (filled < buffer.Length && pos < data.Length)
        {
            byte b = data[pos++];
            if ((b & 0xC0) == 0xC0)
            {
                int count = b & 0x3F;
                byte val = pos < data.Length ? data[pos++] : (byte)0;
                for (int i = 0;i < count && filled < buffer.Length;i++)
                {
                    buffer[filled++] = val;
                }
            }
            else
            {
                buffer[filled++] = b;
            }
        }
        return pos;
    }

    private static void CompressRleScanline(Stream stream, byte[] scanline)
    {
        int i = 0;
        while (i < scanline.Length)
        {
            byte val = scanline[i];
            int count = 1;
            while (i + count < scanline.Length && count < 63 && scanline[i + count] == val)
            {
                count++;
            }

            if (count > 1 || (val & 0xC0) == 0xC0)
            {
                stream.WriteByte((byte)(0xC0 | count));
                stream.WriteByte(val);
            }
            else
            {
                stream.WriteByte(val);
            }
            i += count;
        }
    }
}
