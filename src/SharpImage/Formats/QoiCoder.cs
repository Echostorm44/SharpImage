// QOI (Quite OK Image) format coder — read and write.
// Reference: https://qoiformat.org/qoi-specification.pdf
// Pure C# implementation, no dependencies.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Reads and writes images in the QOI (Quite OK Image) format. QOI is a simple lossless format with fast encode/decode.
/// </summary>
public static class QoiCoder
{
    private static ReadOnlySpan<byte> Magic => "qoif"u8;

    private const byte OpIndex = 0x00; // 00xxxxxx
    private const byte OpDiff = 0x40; // 01xxxxxx
    private const byte OpLuma = 0x80; // 10xxxxxx
    private const byte OpRun = 0xC0; // 11xxxxxx
    private const byte OpRgb = 0xFE;
    private const byte OpRgba = 0xFF;
    private const byte Mask2 = 0xC0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorHash(byte r, byte g, byte b, byte a)
        => (r * 3 + g * 5 + b * 7 + a * 11) & 63;

    /// <summary>
    /// Detect QOI format by magic bytes.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
        => data.Length >= 14 && data[0] == 'q' && data[1] == 'o' && data[2] == 'i' && data[3] == 'f';

    /// <summary>
    /// Decode a QOI file from a byte span into an ImageFrame.
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid QOI file");
        }

        // Parse header
        uint width = ReadU32BE(data, 4);
        uint height = ReadU32BE(data, 8);
        byte channels = data[12];
        // byte colorspace = data[13]; // 0=sRGB, 1=linear — informational only

        if (width == 0 || height == 0 || width > 65535 || height > 65535)
        {
            throw new InvalidDataException($"Invalid QOI dimensions: {width}x{height}");
        }

        bool hasAlpha = channels == 4;
        var frame = new ImageFrame();
        frame.Initialize((int)width, (int)height, ColorspaceType.SRGB, hasAlpha);

        // Decode pixels
        Span<byte> index = stackalloc byte[64 * 4]; // 64 RGBA entries
        index.Clear();

        byte prevR = 0, prevG = 0, prevB = 0, prevA = 255;
        int pos = 14;
        int run = 0;
        long totalPixels = (long)width * height;
        long pixelIndex = 0;
        int imgChannels = frame.NumberOfChannels;

        for (long y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);

            for (long x = 0;x < width;x++)
            {
                if (run > 0)
                {
                    run--;
                }
                else if (pos < data.Length)
                {
                    byte b1 = data[pos++];

                    if (b1 == OpRgb)
                    {
                        prevR = data[pos++];
                        prevG = data[pos++];
                        prevB = data[pos++];
                    }
                    else if (b1 == OpRgba)
                    {
                        prevR = data[pos++];
                        prevG = data[pos++];
                        prevB = data[pos++];
                        prevA = data[pos++];
                    }
                    else
                    {
                        byte tag = (byte)(b1 & Mask2);
                        if (tag == OpIndex)
                        {
                            int idx = (b1 & 0x3F) * 4;
                            prevR = index[idx];
                            prevG = index[idx + 1];
                            prevB = index[idx + 2];
                            prevA = index[idx + 3];
                        }
                        else if (tag == OpDiff)
                        {
                            prevR += (byte)(((b1 >> 4) & 0x03) - 2);
                            prevG += (byte)(((b1 >> 2) & 0x03) - 2);
                            prevB += (byte)((b1 & 0x03) - 2);
                        }
                        else if (tag == OpLuma)
                        {
                            byte b2 = data[pos++];
                            int dg = (b1 & 0x3F) - 32;
                            prevR += (byte)(dg + ((b2 >> 4) & 0x0F) - 8);
                            prevG += (byte)dg;
                            prevB += (byte)(dg + (b2 & 0x0F) - 8);
                        }
                        else // OpRun
                        {
                            run = (b1 & 0x3F); // additional pixels beyond this one
                        }
                    }

                    // Store in index
                    int hash = ColorHash(prevR, prevG, prevB, prevA) * 4;
                    index[hash] = prevR;
                    index[hash + 1] = prevG;
                    index[hash + 2] = prevB;
                    index[hash + 3] = prevA;
                }

                // Write pixel
                int offset = (int)(x * imgChannels);
                row[offset] = Quantum.ScaleFromByte(prevR);
                if (imgChannels > 1)
                {
                    row[offset + 1] = Quantum.ScaleFromByte(prevG);
                }

                if (imgChannels > 2)
                {
                    row[offset + 2] = Quantum.ScaleFromByte(prevB);
                }

                if (hasAlpha)
                {
                    row[offset + imgChannels - 1] = Quantum.ScaleFromByte(prevA);
                }

                pixelIndex++;
            }
        }

        return frame;
    }

    /// <summary>
    /// Encode an ImageFrame to QOI format bytes.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        bool hasAlpha = image.HasAlpha;
        byte channels = (byte)(hasAlpha ? 4 : 3);
        int imgChannels = image.NumberOfChannels;

        // Max output size: header(14) + pixels * 5 (worst case RGBA) + end(8)
        int maxSize = 14 + width * height * 5 + 8;
        byte[] output = ArrayPool<byte>.Shared.Rent(maxSize);
        int pos = 0;

        try
        {
            // Header
            output[pos++] = (byte)'q';
            output[pos++] = (byte)'o';
            output[pos++] = (byte)'i';
            output[pos++] = (byte)'f';
            WriteU32BE(output, ref pos, (uint)width);
            WriteU32BE(output, ref pos, (uint)height);
            output[pos++] = channels;
            output[pos++] = 0; // sRGB colorspace

            // Encode pixels
            Span<byte> index = stackalloc byte[64 * 4];
            index.Clear();

            byte prevR = 0, prevG = 0, prevB = 0, prevA = 255;
            int run = 0;

            for (int y = 0;y < height;y++)
            {
                var row = image.GetPixelRow(y);

                for (int x = 0;x < width;x++)
                {
                    int offset = x * imgChannels;
                    byte r = Quantum.ScaleToByte(row[offset]);
                    byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[offset + 1]) : r;
                    byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[offset + 2]) : r;
                    byte a = hasAlpha ? Quantum.ScaleToByte(row[offset + imgChannels - 1]) : (byte)255;

                    if (r == prevR && g == prevG && b == prevB && a == prevA)
                    {
                        run++;
                        if (run == 62 || (y == height - 1 && x == width - 1))
                        {
                            output[pos++] = (byte)(OpRun | (run - 1));
                            run = 0;
                        }
                        continue;
                    }

                    if (run > 0)
                    {
                        output[pos++] = (byte)(OpRun | (run - 1));
                        run = 0;
                    }

                    int hashIdx = ColorHash(r, g, b, a);
                    int idxOffset = hashIdx * 4;

                    if (index[idxOffset] == r && index[idxOffset + 1] == g &&
                        index[idxOffset + 2] == b && index[idxOffset + 3] == a)
                    {
                        output[pos++] = (byte)(OpIndex | hashIdx);
                    }
                    else
                    {
                        index[idxOffset] = r;
                        index[idxOffset + 1] = g;
                        index[idxOffset + 2] = b;
                        index[idxOffset + 3] = a;

                        if (a == prevA)
                        {
                            int dr = (int)r - prevR;
                            int dg = (int)g - prevG;
                            int db = (int)b - prevB;

                            int drDg = dr - dg;
                            int dbDg = db - dg;

                            if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1)
                            {
                                output[pos++] = (byte)(OpDiff | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2));
                            }
                            else if (dg >= -32 && dg <= 31 && drDg >= -8 && drDg <= 7 && dbDg >= -8 && dbDg <= 7)
                            {
                                output[pos++] = (byte)(OpLuma | (dg + 32));
                                output[pos++] = (byte)(((drDg + 8) << 4) | (dbDg + 8));
                            }
                            else
                            {
                                output[pos++] = OpRgb;
                                output[pos++] = r;
                                output[pos++] = g;
                                output[pos++] = b;
                            }
                        }
                        else
                        {
                            output[pos++] = OpRgba;
                            output[pos++] = r;
                            output[pos++] = g;
                            output[pos++] = b;
                            output[pos++] = a;
                        }
                    }

                    prevR = r;
                    prevG = g;
                    prevB = b;
                    prevA = a;
                }
            }

            // Flush remaining run
            if (run > 0)
            {
                output[pos++] = (byte)(OpRun | (run - 1));
            }

            // End marker: 7 × 0x00 + 0x01
            for (int i = 0;i < 7;i++)
            {
                output[pos++] = 0x00;
            }

            output[pos++] = 0x01;

            byte[] result = new byte[pos];
            Buffer.BlockCopy(output, 0, result, 0, pos);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32BE(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) | data[offset + 3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32BE(byte[] data, ref int pos, uint value)
    {
        data[pos++] = (byte)(value >> 24);
        data[pos++] = (byte)(value >> 16);
        data[pos++] = (byte)(value >> 8);
        data[pos++] = (byte)value;
    }
}
