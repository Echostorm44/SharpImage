// ICO (Windows Icon) format coder — read and write.
// Supports 32-bit RGBA icons (most common modern format).
// Also reads 24-bit, 8-bit, 4-bit, and 1-bit icons.
// PNG-compressed icons (Vista+) detected but require PngCoder for decoding.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Reads and writes ICO (Windows Icon) format files. Reads the largest icon entry; writes 32-bit RGBA icons.
/// </summary>
public static class IcoCoder
{
    /// <summary>
    /// Detect ICO format by header signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return false;
        }

        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        return reserved == 0 && (type == 1 || type == 2) && count > 0 && count < 1024;
    }

    /// <summary>
    /// Decode the largest icon entry from an ICO file.
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid ICO file");
        }

        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);

        // Parse directory entries, pick largest
        int bestIdx = 0, bestArea = 0;
        for (int i = 0;i < count;i++)
        {
            int entryOffset = 6 + i * 16;
            if (entryOffset + 16 > data.Length)
            {
                break;
            }

            int w = data[entryOffset] == 0 ? 256 : data[entryOffset];
            int h = data[entryOffset + 1] == 0 ? 256 : data[entryOffset + 1];
            int area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                bestIdx = i;
            }
        }

        int bestEntryOffset = 6 + bestIdx * 16;
        int width = data[bestEntryOffset] == 0 ? 256 : data[bestEntryOffset];
        int height = data[bestEntryOffset + 1] == 0 ? 256 : data[bestEntryOffset + 1];
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(bestEntryOffset + 8)..]);
        uint imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(bestEntryOffset + 12)..]);

        if (imageOffset + 4 > data.Length)
        {
            throw new InvalidDataException("ICO image data offset out of bounds");
        }

        // Check if PNG-compressed
        var imgData = data[(int)imageOffset..];
        if (imgData.Length >= 8 && imgData[0] == 0x89 && imgData[1] == 'P' &&
            imgData[2] == 'N' && imgData[3] == 'G')
        {
            using var ms = new MemoryStream(imgData.ToArray());
            return PngCoder.Read(ms);
        }

        // DIB format
        return DecodeDIB(imgData, width, height);
    }

    private static ImageFrame DecodeDIB(ReadOnlySpan<byte> data, int width, int height)
    {
        if (data.Length < 40)
        {
            throw new InvalidDataException("ICO DIB header too short");
        }

        // BITMAPINFOHEADER
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        int dibWidth = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int dibHeight = BinaryPrimitives.ReadInt32LittleEndian(data[8..]); // doubled (XOR + AND)
        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);

        // Use directory dimensions (more reliable)
        int actualHeight = height;

        var frame = new ImageFrame();
        frame.Initialize(width, actualHeight, ColorspaceType.SRGB, true);

        int paletteOffset = headerSize;
        int paletteColors = 0;

        if (bitsPerPixel <= 8)
        {
            paletteColors = 1 << bitsPerPixel;
        }

        int pixelDataOffset = paletteOffset + paletteColors * 4;
        int scanlineBytes;

        switch (bitsPerPixel)
        {
            case 32:
                scanlineBytes = width * 4;
                break;
            case 24:
                scanlineBytes = ((width * 3 + 3) / 4) * 4;
                break;
            case 8:
                scanlineBytes = ((width + 3) / 4) * 4;
                break;
            case 4:
                scanlineBytes = (((width + 1) / 2 + 3) / 4) * 4;
                break;
            case 1:
                scanlineBytes = (((width + 7) / 8 + 3) / 4) * 4;
                break;
            default:
                throw new InvalidDataException($"Unsupported ICO bit depth: {bitsPerPixel}");
        }

        // AND mask offset (after pixel data)
        int andMaskOffset = pixelDataOffset + scanlineBytes * actualHeight;
        int andScanlineBytes = (((width + 7) / 8 + 3) / 4) * 4;
        bool hasAndMask = bitsPerPixel < 32 && andMaskOffset + andScanlineBytes <= data.Length;

        // Read pixels (bottom-to-top in DIB)
        for (int y = 0;y < actualHeight;y++)
        {
            int dibY = actualHeight - 1 - y; // flip
            int srcOffset = pixelDataOffset + dibY * scanlineBytes;
            var row = frame.GetPixelRowForWrite(y);
            int channels = frame.NumberOfChannels;

            for (int x = 0;x < width;x++)
            {
                byte r, g, b, a;

                switch (bitsPerPixel)
                {
                    case 32:
                    {
                        int px = srcOffset + x * 4;
                        if (px + 3 >= data.Length)
                        {
                            r = g = b = a = 0;
                            break;
                        }
                        b = data[px];
                        g = data[px + 1];
                        r = data[px + 2];
                        a = data[px + 3];
                        break;
                    }
                    case 24:
                    {
                        int px = srcOffset + x * 3;
                        if (px + 2 >= data.Length)
                        {
                            r = g = b = 0;
                            a = 255;
                            break;
                        }
                        b = data[px];
                        g = data[px + 1];
                        r = data[px + 2];
                        a = 255;
                        break;
                    }
                    case 8:
                    {
                        int px = srcOffset + x;
                        if (px >= data.Length)
                        {
                            r = g = b = 0;
                            a = 255;
                            break;
                        }
                        int palIdx = data[px];
                        int palOff = paletteOffset + palIdx * 4;
                        b = data[palOff];
                        g = data[palOff + 1];
                        r = data[palOff + 2];
                        a = 255;
                        break;
                    }
                    case 4:
                    {
                        int byteIdx = srcOffset + x / 2;
                        if (byteIdx >= data.Length)
                        {
                            r = g = b = 0;
                            a = 255;
                            break;
                        }
                        int palIdx = (x % 2 == 0) ? (data[byteIdx] >> 4) : (data[byteIdx] & 0x0F);
                        int palOff = paletteOffset + palIdx * 4;
                        b = data[palOff];
                        g = data[palOff + 1];
                        r = data[palOff + 2];
                        a = 255;
                        break;
                    }
                    case 1:
                    {
                        int byteIdx = srcOffset + x / 8;
                        if (byteIdx >= data.Length)
                        {
                            r = g = b = 0;
                            a = 255;
                            break;
                        }
                        int bit = (data[byteIdx] >> (7 - (x % 8))) & 1;
                        int palOff = paletteOffset + bit * 4;
                        b = data[palOff];
                        g = data[palOff + 1];
                        r = data[palOff + 2];
                        a = 255;
                        break;
                    }
                    default:
                        r = g = b = 0;
                        a = 255;
                        break;
                }

                // Apply AND mask for non-32-bit formats
                if (hasAndMask && bitsPerPixel < 32)
                {
                    int andByteIdx = andMaskOffset + dibY * andScanlineBytes + x / 8;
                    if (andByteIdx < data.Length)
                    {
                        int andBit = (data[andByteIdx] >> (7 - (x % 8))) & 1;
                        if (andBit == 1)
                        {
                            a = 0; // transparent
                        }
                    }
                }

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

                row[offset + channels - 1] = Quantum.ScaleFromByte(a);
            }
        }

        return frame;
    }

    /// <summary>
    /// Encode an ImageFrame as a single 32-bit RGBA ICO file.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;

        if (width > 256 || height > 256)
        {
            throw new ArgumentException("ICO format supports images up to 256x256");
        }

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        // DIB data: 40-byte header + BGRA pixel data + AND mask
        int dibHeaderSize = 40;
        int pixelDataSize = width * height * 4;
        int andScanlineBytes = (((width + 7) / 8 + 3) / 4) * 4;
        int andMaskSize = andScanlineBytes * height;
        int dibSize = dibHeaderSize + pixelDataSize + andMaskSize;

        // ICO file: 6 (ICONDIR) + 16 (entry) + DIB
        int totalSize = 6 + 16 + dibSize;
        byte[] output = new byte[totalSize];
        int pos = 0;

        // ICONDIR
        WriteU16LE(output, ref pos, 0);      // reserved
        WriteU16LE(output, ref pos, 1);      // type = ICO
        WriteU16LE(output, ref pos, 1);      // count = 1

        // ICONDIRENTRY
        output[pos++] = (byte)(width == 256 ? 0 : width);
        output[pos++] = (byte)(height == 256 ? 0 : height);
        output[pos++] = 0;   // no palette
        output[pos++] = 0;   // reserved
        WriteU16LE(output, ref pos, 1);      // planes
        WriteU16LE(output, ref pos, 32);     // bits per pixel
        WriteU32LE(output, ref pos, (uint)dibSize);
        WriteU32LE(output, ref pos, 22);     // offset to image data (6 + 16)

        // BITMAPINFOHEADER
        WriteU32LE(output, ref pos, 40);     // header size
        WriteU32LE(output, ref pos, (uint)width);
        WriteU32LE(output, ref pos, (uint)(height * 2)); // doubled for XOR + AND
        WriteU16LE(output, ref pos, 1);      // planes
        WriteU16LE(output, ref pos, 32);     // bpp
        WriteU32LE(output, ref pos, 0);      // compression (none)
        WriteU32LE(output, ref pos, (uint)(pixelDataSize + andMaskSize));
        WriteU32LE(output, ref pos, 0);      // x pixels/meter
        WriteU32LE(output, ref pos, 0);      // y pixels/meter
        WriteU32LE(output, ref pos, 0);      // colors used
        WriteU32LE(output, ref pos, 0);      // colors important

        // Pixel data (BGRA, bottom-to-top)
        for (int y = height - 1;y >= 0;y--)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int offset = x * channels;
                byte r = Quantum.ScaleToByte(row[offset]);
                byte g = channels > 1 ? Quantum.ScaleToByte(row[offset + 1]) : r;
                byte b = channels > 2 ? Quantum.ScaleToByte(row[offset + 2]) : r;
                byte a = hasAlpha ? Quantum.ScaleToByte(row[offset + channels - 1]) : (byte)255;

                output[pos++] = b;
                output[pos++] = g;
                output[pos++] = r;
                output[pos++] = a;
            }
        }

        // AND mask (all opaque for 32-bit — alpha handles transparency)
        for (int i = 0;i < andMaskSize;i++)
        {
            output[pos++] = 0;
        }

        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU16LE(byte[] data, ref int pos, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), value);
        pos += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32LE(byte[] data, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), value);
        pos += 4;
    }
}
