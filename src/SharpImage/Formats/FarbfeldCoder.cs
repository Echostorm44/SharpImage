// Farbfeld image format coder — read and write.
// Farbfeld is a simple, uncompressed format: 8-byte magic + dimensions + raw 16-bit RGBA.
// All multi-byte values are big-endian. Matches our 16-bit quantum perfectly.
// Spec: https://tools.suckless.org/farbfeld/

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class FarbfeldCoder
{
    private static ReadOnlySpan<byte> Magic => "farbfeld"u8;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[..8].SequenceEqual(Magic);

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid Farbfeld file");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);

        if (width == 0 || height == 0 || width > 65535 || height > 65535)
        {
            throw new InvalidDataException($"Invalid Farbfeld dimensions: {width}x{height}");
        }

        int w = (int)width, h = (int)height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, true);
        int channels = frame.NumberOfChannels;

        int pos = 16; // after header
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                if (pos + 8 > data.Length)
                {
                    break;
                }

                ushort r = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                ushort g = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
                ushort b = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]);
                ushort a = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 6)..]);
                pos += 8;

                int offset = x * channels;
                row[offset] = r;
                if (channels > 1)
                {
                    row[offset + 1] = g;
                }

                if (channels > 2)
                {
                    row[offset + 2] = b;
                }

                row[offset + channels - 1] = a;
            }
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        // Header (16) + pixels (w * h * 8)
        byte[] output = new byte[16 + w * h * 8];
        Magic.CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), (uint)h);

        int pos = 16;
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < w;x++)
            {
                int srcIdx = x * channels;
                ushort r = row[srcIdx];
                ushort g = channels > 1 ? row[srcIdx + 1] : r;
                ushort b = channels > 2 ? row[srcIdx + 2] : r;
                ushort a = hasAlpha ? row[srcIdx + channels - 1] : Quantum.MaxValue;

                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos), r);
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos + 2), g);
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos + 4), b);
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos + 6), a);
                pos += 8;
            }
        }

        return output;
    }
}
