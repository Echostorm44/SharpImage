// RAW channel format coders — read and write.
// Headerless raw pixel data in various channel layouts.
// Supported: RGB, RGBA, BGR, BGRA, Gray, Mono (1-bit), CMYK.
// Dimensions must be provided externally since there's no header.
// Reference: ImageMagick coders/rgb.c, gray.c, mono.c, cmyk.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

/// <summary>
/// Specifies the channel layout of raw pixel data.
/// </summary>
public enum RawChannelLayout
{
    Gray8,
    Gray16,
    Mono,       // 1-bit, MSB-first, 8-pixel packed bytes
    Rgb8,
    Rgb16,
    Rgba8,
    Rgba16,
    Bgr8,
    Bgr16,
    Bgra8,
    Bgra16,
    Cmyk8,
    Cmyk16
}

/// <summary>
/// Reads and writes headerless raw pixel data in configurable channel layouts. Since raw files have no header,
/// dimensions and layout must be specified by the caller.
/// </summary>
public static class RawCoder
{
    /// <summary>
    /// Decode raw pixel data with specified dimensions and layout.
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data, int width, int height, RawChannelLayout layout)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Width and height must be positive");
        }

        bool hasAlpha = layout is RawChannelLayout.Rgba8 or RawChannelLayout.Rgba16
            or RawChannelLayout.Bgra8 or RawChannelLayout.Bgra16;

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;

        int pos = 0;
        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int offset = x * channels;
                switch (layout)
                {
                    case RawChannelLayout.Gray8:
                        if (pos < data.Length)
                        {
                            ushort val = Quantum.ScaleFromByte(data[pos++]);
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
                        break;

                    case RawChannelLayout.Gray16:
                        if (pos + 1 < data.Length)
                        {
                            ushort val = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
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
                        break;

                    case RawChannelLayout.Rgb8:
                        if (pos + 2 < data.Length)
                        {
                            row[offset] = Quantum.ScaleFromByte(data[pos++]);
                            if (channels > 1)
                            {
                                row[offset + 1] = Quantum.ScaleFromByte(data[pos++]);
                            }
                            else
                            {
                                pos++;
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = Quantum.ScaleFromByte(data[pos++]);
                            }
                            else
                            {
                                pos++;
                            }
                        }
                        break;

                    case RawChannelLayout.Rgb16:
                        if (pos + 5 < data.Length)
                        {
                            row[offset] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            if (channels > 1)
                            {
                                row[offset + 1] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            }

                            pos += 2;
                            if (channels > 2)
                            {
                                row[offset + 2] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            }

                            pos += 2;
                        }
                        break;

                    case RawChannelLayout.Rgba8:
                        if (pos + 3 < data.Length)
                        {
                            row[offset] = Quantum.ScaleFromByte(data[pos++]);
                            if (channels > 1)
                            {
                                row[offset + 1] = Quantum.ScaleFromByte(data[pos++]);
                            }
                            else
                            {
                                pos++;
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = Quantum.ScaleFromByte(data[pos++]);
                            }
                            else
                            {
                                pos++;
                            }

                            row[offset + channels - 1] = Quantum.ScaleFromByte(data[pos++]);
                        }
                        break;

                    case RawChannelLayout.Rgba16:
                        if (pos + 7 < data.Length)
                        {
                            row[offset] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            if (channels > 1)
                            {
                                row[offset + 1] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            }

                            pos += 2;
                            if (channels > 2)
                            {
                                row[offset + 2] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            }

                            pos += 2;
                            row[offset + channels - 1] = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                        }
                        break;

                    case RawChannelLayout.Bgr8:
                        if (pos + 2 < data.Length)
                        {
                            byte b = data[pos++], g = data[pos++], r = data[pos++];
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
                        break;

                    case RawChannelLayout.Bgr16:
                        if (pos + 5 < data.Length)
                        {
                            ushort bv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort gv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort rv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            row[offset] = rv;
                            if (channels > 1)
                            {
                                row[offset + 1] = gv;
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = bv;
                            }
                        }
                        break;

                    case RawChannelLayout.Bgra8:
                        if (pos + 3 < data.Length)
                        {
                            byte b = data[pos++], g = data[pos++], r = data[pos++], a = data[pos++];
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
                        break;

                    case RawChannelLayout.Bgra16:
                        if (pos + 7 < data.Length)
                        {
                            ushort bv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort gv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort rv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort av = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            row[offset] = rv;
                            if (channels > 1)
                            {
                                row[offset + 1] = gv;
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = bv;
                            }

                            row[offset + channels - 1] = av;
                        }
                        break;

                    case RawChannelLayout.Cmyk8:
                        if (pos + 3 < data.Length)
                        {
                            byte c = data[pos++], m = data[pos++], yy = data[pos++], k = data[pos++];
                            CmykToRgb(c, m, yy, k, out byte rr, out byte gg, out byte bb);
                            row[offset] = Quantum.ScaleFromByte(rr);
                            if (channels > 1)
                            {
                                row[offset + 1] = Quantum.ScaleFromByte(gg);
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = Quantum.ScaleFromByte(bb);
                            }
                        }
                        break;

                    case RawChannelLayout.Cmyk16:
                        if (pos + 7 < data.Length)
                        {
                            ushort cv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort mv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort yv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            ushort kv = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                            pos += 2;
                            double cd = cv / 65535.0, md = mv / 65535.0, yd = yv / 65535.0, kd = kv / 65535.0;
                            row[offset] = (ushort)((1.0 - Math.Min(1.0, cd * (1.0 - kd) + kd)) * 65535);
                            if (channels > 1)
                            {
                                row[offset + 1] = (ushort)((1.0 - Math.Min(1.0, md * (1.0 - kd) + kd)) * 65535);
                            }

                            if (channels > 2)
                            {
                                row[offset + 2] = (ushort)((1.0 - Math.Min(1.0, yd * (1.0 - kd) + kd)) * 65535);
                            }
                        }
                        break;

                    case RawChannelLayout.Mono:
                        // Handled at row level below
                        break;
                }
            }

            // Mono is handled per-row since it's bit-packed
            if (layout == RawChannelLayout.Mono)
            {
                int bytesPerRow = (width + 7) / 8;
                for (int bx = 0;bx < bytesPerRow && pos < data.Length;bx++, pos++)
                {
                    byte packed = data[pos];
                    for (int bit = 7;bit >= 0;bit--)
                    {
                        int x = bx * 8 + (7 - bit);
                        if (x >= width)
                        {
                            break;
                        }

                        bool isWhite = ((packed >> bit) & 1) == 1;
                        ushort val = isWhite ? Quantum.MaxValue : (ushort)0;
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
            }
        }

        return frame;
    }

    /// <summary>
    /// Encode an image frame as raw pixel data in the specified layout.
    /// </summary>
    public static byte[] Encode(ImageFrame image, RawChannelLayout layout)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        int bytesPerPixel = GetBytesPerPixel(layout, w);
        int totalBytes = layout == RawChannelLayout.Mono
            ? ((w + 7) / 8) * h
            : bytesPerPixel * w * h;

        byte[] output = new byte[totalBytes];
        int pos = 0;

        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);

            if (layout == RawChannelLayout.Mono)
            {
                int bytesPerRow = (w + 7) / 8;
                for (int bx = 0;bx < bytesPerRow;bx++)
                {
                    byte packed = 0;
                    for (int bit = 7;bit >= 0;bit--)
                    {
                        int x = bx * 8 + (7 - bit);
                        if (x >= w)
                        {
                            break;
                        }

                        double luma = row[x * imgChannels] * Quantum.Scale;
                        if (luma >= 0.5)
                        {
                            packed |= (byte)(1 << bit);
                        }
                    }
                    output[pos++] = packed;
                }
                continue;
            }

            for (int x = 0;x < w;x++)
            {
                int srcOffset = x * imgChannels;
                ushort r = row[srcOffset];
                ushort g = imgChannels > 1 ? row[srcOffset + 1] : r;
                ushort b = imgChannels > 2 ? row[srcOffset + 2] : r;
                ushort a = hasAlpha ? row[srcOffset + imgChannels - 1] : Quantum.MaxValue;

                switch (layout)
                {
                    case RawChannelLayout.Gray8:
                        output[pos++] = Quantum.ScaleToByte((ushort)(0.299 * r + 0.587 * g + 0.114 * b));
                        break;
                    case RawChannelLayout.Gray16:
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos),
                            (ushort)(0.299 * r + 0.587 * g + 0.114 * b));
                        pos += 2;
                        break;
                    case RawChannelLayout.Rgb8:
                        output[pos++] = Quantum.ScaleToByte(r);
                        output[pos++] = Quantum.ScaleToByte(g);
                        output[pos++] = Quantum.ScaleToByte(b);
                        break;
                    case RawChannelLayout.Rgb16:
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), r);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), g);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), b);
                        pos += 2;
                        break;
                    case RawChannelLayout.Rgba8:
                        output[pos++] = Quantum.ScaleToByte(r);
                        output[pos++] = Quantum.ScaleToByte(g);
                        output[pos++] = Quantum.ScaleToByte(b);
                        output[pos++] = Quantum.ScaleToByte(a);
                        break;
                    case RawChannelLayout.Rgba16:
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), r);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), g);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), b);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), a);
                        pos += 2;
                        break;
                    case RawChannelLayout.Bgr8:
                        output[pos++] = Quantum.ScaleToByte(b);
                        output[pos++] = Quantum.ScaleToByte(g);
                        output[pos++] = Quantum.ScaleToByte(r);
                        break;
                    case RawChannelLayout.Bgr16:
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), b);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), g);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), r);
                        pos += 2;
                        break;
                    case RawChannelLayout.Bgra8:
                        output[pos++] = Quantum.ScaleToByte(b);
                        output[pos++] = Quantum.ScaleToByte(g);
                        output[pos++] = Quantum.ScaleToByte(r);
                        output[pos++] = Quantum.ScaleToByte(a);
                        break;
                    case RawChannelLayout.Bgra16:
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), b);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), g);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), r);
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos), a);
                        pos += 2;
                        break;
                    case RawChannelLayout.Cmyk8:
                        RgbToCmyk(Quantum.ScaleToByte(r), Quantum.ScaleToByte(g), Quantum.ScaleToByte(b),
                            out byte c8, out byte m8, out byte y8, out byte k8);
                        output[pos++] = c8;
                        output[pos++] = m8;
                        output[pos++] = y8;
                        output[pos++] = k8;
                        break;
                    case RawChannelLayout.Cmyk16:
                        double rd = r / 65535.0, gd = g / 65535.0, bd = b / 65535.0;
                        double kd = 1.0 - Math.Max(rd, Math.Max(gd, bd));
                        double invK = kd < 1.0 ? 1.0 / (1.0 - kd) : 0;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos),
                            (ushort)((1.0 - rd - kd) * invK * 65535));
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos),
                            (ushort)((1.0 - gd - kd) * invK * 65535));
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos),
                            (ushort)((1.0 - bd - kd) * invK * 65535));
                        pos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(pos),
                            (ushort)(kd * 65535));
                        pos += 2;
                        break;
                }
            }
        }

        return output;
    }

    private static int GetBytesPerPixel(RawChannelLayout layout, int width) => layout switch
    {
        RawChannelLayout.Gray8 => 1,
        RawChannelLayout.Gray16 => 2,
        RawChannelLayout.Mono => 1, // not per-pixel but handled specially
        RawChannelLayout.Rgb8 or RawChannelLayout.Bgr8 => 3,
        RawChannelLayout.Rgb16 or RawChannelLayout.Bgr16 => 6,
        RawChannelLayout.Rgba8 or RawChannelLayout.Bgra8 or RawChannelLayout.Cmyk8 => 4,
        RawChannelLayout.Rgba16 or RawChannelLayout.Bgra16 or RawChannelLayout.Cmyk16 => 8,
        _ => throw new ArgumentException($"Unknown layout: {layout}")
    };

    private static void CmykToRgb(byte c, byte m, byte y, byte k, out byte r, out byte g, out byte b)
    {
        double cd = c / 255.0, md = m / 255.0, yd = y / 255.0, kd = k / 255.0;
        r = (byte)(255 * (1 - Math.Min(1.0, cd * (1 - kd) + kd)));
        g = (byte)(255 * (1 - Math.Min(1.0, md * (1 - kd) + kd)));
        b = (byte)(255 * (1 - Math.Min(1.0, yd * (1 - kd) + kd)));
    }

    private static void RgbToCmyk(byte r, byte g, byte b, out byte c, out byte m, out byte y, out byte k)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double kd = 1.0 - Math.Max(rd, Math.Max(gd, bd));
        if (kd >= 1.0)
        {
            c = 0;
            m = 0;
            y = 0;
            k = 255;
            return;
        }
        double invK = 1.0 / (1.0 - kd);
        c = (byte)((1.0 - rd - kd) * invK * 255);
        m = (byte)((1.0 - gd - kd) * invK * 255);
        y = (byte)((1.0 - bd - kd) * invK * 255);
        k = (byte)(kd * 255);
    }
}
