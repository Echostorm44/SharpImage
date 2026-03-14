// PIX (Alias/Wavefront) format coder — read and write.
// Simple RLE format with big-endian 10-byte header.
// 24-bit uses BGR channel order on read, RGB on write.
// No magic bytes — detection by extension only.
// Reference: ImageMagick coders/pix.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class PixCoder
{
    private const int HeaderSize = 10;

    // No magic bytes — PIX is detected by file extension only
    public static bool CanDecode(ReadOnlySpan<byte> data) => false;

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("Not a valid PIX file — too short");

        int width = BinaryPrimitives.ReadUInt16BigEndian(data);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        // bytes 4-5: x_offset (ignored)
        // bytes 6-7: y_offset (ignored)
        int bitsPerPixel = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
            throw new InvalidDataException($"Invalid PIX dimensions: {width}x{height}");
        if (bitsPerPixel != 8 && bitsPerPixel != 24)
            throw new InvalidDataException($"Unsupported PIX bit depth: {bitsPerPixel}");

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        int pos = HeaderSize;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int x = 0;

            while (x < width && pos < data.Length)
            {
                byte count = data[pos++];
                if (count == 0) break; // end of line

                if (bitsPerPixel == 24)
                {
                    if (pos + 2 >= data.Length) break;
                    byte blue = data[pos++];
                    byte green = data[pos++];
                    byte red = data[pos++];

                    for (int i = 0; i < count && x < width; i++, x++)
                    {
                        int offset = x * channels;
                        row[offset] = Quantum.ScaleFromByte(red);
                        if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte(green);
                        if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte(blue);
                    }
                }
                else // 8-bit grayscale
                {
                    if (pos >= data.Length) break;
                    byte value = data[pos++];
                    ushort gray = Quantum.ScaleFromByte(value);

                    for (int i = 0; i < count && x < width; i++, x++)
                    {
                        int offset = x * channels;
                        row[offset] = gray;
                        if (channels > 1) row[offset + 1] = gray;
                        if (channels > 2) row[offset + 2] = gray;
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

        using var ms = new MemoryStream();

        // Header: width, height, x_offset, y_offset, bits_per_pixel (all big-endian 16-bit)
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 0); // x_offset
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 0); // y_offset
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], 24); // 24-bit
        ms.Write(header);

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            int x = 0;

            while (x < w)
            {
                // Get current pixel RGB
                int srcIdx = x * imgChannels;
                byte r = Quantum.ScaleToByte(row[srcIdx]);
                byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[srcIdx + 1]) : r;
                byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[srcIdx + 2]) : r;

                // Count matching pixels (max 255, cannot be 0)
                int count = 1;
                while (x + count < w && count < 255)
                {
                    int nextIdx = (x + count) * imgChannels;
                    byte nr = Quantum.ScaleToByte(row[nextIdx]);
                    byte ng = imgChannels > 1 ? Quantum.ScaleToByte(row[nextIdx + 1]) : nr;
                    byte nb = imgChannels > 2 ? Quantum.ScaleToByte(row[nextIdx + 2]) : nr;
                    if (nr != r || ng != g || nb != b) break;
                    count++;
                }

                ms.WriteByte((byte)count);
                ms.WriteByte(b); // BGR order
                ms.WriteByte(g);
                ms.WriteByte(r);
                x += count;
            }
        }

        return ms.ToArray();
    }
}
