// WBMP (Wireless Bitmap) format coder — read and write.
// Level 0 WBMP: 2-byte header (0x00 0x00) + variable-length width/height + 1-bit MSB-first pixels.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

public static class WbmpCoder
{
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }
        // Type must be 0, fixed header byte must be 0
        if (data[0] != 0 || data[1] != 0)
        {
            return false;
        }
        // Try to parse width/height — they must be reasonable
        int pos = 2;
        int w = ReadVariableInt(data, ref pos);
        int h = ReadVariableInt(data, ref pos);
        return w > 0 && w <= 65535 && h > 0 && h <= 65535 && pos <= data.Length;
    }

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("Not a valid WBMP file");
        }

        int pos = 2; // skip type (0x00) + fixed header (0x00)
        int width = ReadVariableInt(data, ref pos);
        int height = ReadVariableInt(data, ref pos);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid WBMP dimensions: {width}x{height}");
        }

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int byteIdx = pos + (y * ((width + 7) / 8)) + (x / 8);
                bool white = false;
                if (byteIdx < data.Length)
                {
                    white = ((data[byteIdx] >> (7 - (x % 8))) & 1) == 1;
                }

                // WBMP: 1 = white, 0 = black
                ushort val = white ? Quantum.MaxValue : (ushort)0;
                int offset = x * channels;
                row[offset] = val;
                if (channels > 1)
                {
                    row[offset + 1] = val;
                }

                if (channels > 2)
                {
                    row[offset + 2] = val;
                }
            }
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int channels = image.NumberOfChannels;
        int bytesPerRow = (w + 7) / 8;

        using var ms = new MemoryStream();
        ms.WriteByte(0); // type
        ms.WriteByte(0); // fixed header
        WriteVariableInt(ms, w);
        WriteVariableInt(ms, h);

        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int byteIdx = 0;byteIdx < bytesPerRow;byteIdx++)
            {
                byte packed = 0;
                for (int bit = 0;bit < 8;bit++)
                {
                    int x = byteIdx * 8 + bit;
                    if (x >= w)
                    {
                        break;
                    }

                    // Convert to grayscale luminance, threshold at 50%
                    int offset = x * channels;
                    ushort r = row[offset];
                    ushort g = channels > 1 ? row[offset + 1] : r;
                    ushort b = channels > 2 ? row[offset + 2] : r;
                    double luma = 0.299 * r + 0.587 * g + 0.114 * b;

                    if (luma > Quantum.MaxValue / 2.0)
                    {
                        packed |= (byte)(1 << (7 - bit)); // white
                    }
                }
                ms.WriteByte(packed);
            }
        }

        return ms.ToArray();
    }

    private static int ReadVariableInt(ReadOnlySpan<byte> data, ref int pos)
    {
        int value = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }
        return value;
    }

    private static void WriteVariableInt(Stream stream, int value)
    {
        if (value == 0)
        {
            stream.WriteByte(0);
            return;
        }

        // Count bytes needed
        Span<byte> bytes = stackalloc byte[5];
        int count = 0;
        int v = value;
        while (v > 0)
        {
            bytes[count++] = (byte)(v & 0x7F);
            v >>= 7;
        }

        // Write in reverse (MSB first), set continuation bit on all but last
        for (int i = count - 1;i >= 0;i--)
        {
            byte b = bytes[i];
            if (i > 0)
            {
                b |= 0x80;
            }

            stream.WriteByte(b);
        }
    }
}
