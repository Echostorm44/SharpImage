// SUN (Sun Rasterfile) format coder — read and write.
// Supports 1-bit mono, 8-bit indexed, 24-bit RGB, 32-bit RGBA.
// RLE compression uses escape byte 128 protocol.
// All header values are big-endian 32-bit unsigned.
// Reference: ImageMagick coders/sun.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class SunCoder
{
    private const uint SunMagic = 0x59a66a95;
    private const int HeaderSize = 32;

    // Sun raster types
    private const int RtStandard = 1;  // BGR order
    private const int RtEncoded = 2;   // RLE + BGR
    private const int RtFormatRgb = 3; // RGB order

    // Colormap types
    private const int RmtNone = 0;
    private const int RmtEqualRgb = 1;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize &&
        BinaryPrimitives.ReadUInt32BigEndian(data) == SunMagic;

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
            throw new InvalidDataException("Not a valid SUN rasterfile");

        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int depth = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        int dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        int type = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        int mapType = (int)BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
        int mapLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data[28..]);

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
            throw new InvalidDataException($"Invalid SUN dimensions: {width}x{height}");
        if (depth != 1 && depth != 8 && depth != 24 && depth != 32)
            throw new InvalidDataException($"Unsupported SUN bit depth: {depth}");

        // Read colormap if present
        byte[]? colormapR = null, colormapG = null, colormapB = null;
        int pos = HeaderSize;

        if (mapType == RmtEqualRgb && mapLength > 0)
        {
            int colorsPerChannel = mapLength / 3;
            colormapR = new byte[colorsPerChannel];
            colormapG = new byte[colorsPerChannel];
            colormapB = new byte[colorsPerChannel];

            data.Slice(pos, colorsPerChannel).CopyTo(colormapR);
            data.Slice(pos + colorsPerChannel, colorsPerChannel).CopyTo(colormapG);
            data.Slice(pos + colorsPerChannel * 2, colorsPerChannel).CopyTo(colormapB);
        }
        pos += mapLength;

        // Decompress RLE if needed
        byte[] pixelData;
        if (type == RtEncoded)
        {
            pixelData = DecompressRle(data[pos..], dataLength, width, height, depth);
        }
        else
        {
            int remaining = Math.Min(dataLength, data.Length - pos);
            if (remaining <= 0) remaining = data.Length - pos;
            pixelData = data.Slice(pos, remaining).ToArray();
        }

        bool hasAlpha = depth == 32;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;

        bool isRgbOrder = type == RtFormatRgb;

        if (depth == 1)
            Decode1Bit(pixelData, frame, width, height, channels);
        else if (depth == 8)
            Decode8Bit(pixelData, frame, width, height, channels, colormapR, colormapG, colormapB);
        else
            DecodeDirectColor(pixelData, frame, width, height, depth, channels, isRgbOrder,
                colormapR, colormapG, colormapB);

        return frame;
    }

    private static void Decode1Bit(byte[] pixelData, ImageFrame frame,
        int width, int height, int channels)
    {
        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int bytesInRow = (width + 7) / 8;

            for (int byteIdx = 0; byteIdx < bytesInRow && pos < pixelData.Length; byteIdx++)
            {
                byte b = pixelData[pos++];
                for (int bit = 7; bit >= 0; bit--)
                {
                    int x = byteIdx * 8 + (7 - bit);
                    if (x >= width) break;

                    // Bit set = black (0), bit clear = white (max)
                    bool isSet = (b & (1 << bit)) != 0;
                    ushort value = isSet ? (ushort)0 : Quantum.MaxValue;

                    int offset = x * channels;
                    row[offset] = value;
                    if (channels > 1) row[offset + 1] = value;
                    if (channels > 2) row[offset + 2] = value;
                }
            }

            // Pad to even byte boundary
            if (bytesInRow % 2 != 0 && pos < pixelData.Length) pos++;
        }
    }

    private static void Decode8Bit(byte[] pixelData, ImageFrame frame,
        int width, int height, int channels,
        byte[]? colormapR, byte[]? colormapG, byte[]? colormapB)
    {
        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width && pos < pixelData.Length; x++)
            {
                byte idx = pixelData[pos++];
                int offset = x * channels;

                if (colormapR != null && colormapG != null && colormapB != null &&
                    idx < colormapR.Length)
                {
                    row[offset] = Quantum.ScaleFromByte(colormapR[idx]);
                    if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte(colormapG[idx]);
                    if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte(colormapB[idx]);
                }
                else
                {
                    ushort gray = Quantum.ScaleFromByte(idx);
                    row[offset] = gray;
                    if (channels > 1) row[offset + 1] = gray;
                    if (channels > 2) row[offset + 2] = gray;
                }
            }

            // Pad to even byte boundary
            if (width % 2 != 0 && pos < pixelData.Length) pos++;
        }
    }

    private static void DecodeDirectColor(byte[] pixelData, ImageFrame frame,
        int width, int height, int depth, int channels, bool isRgbOrder,
        byte[]? colormapR, byte[]? colormapG, byte[]? colormapB)
    {
        int bytesPerPixel = depth == 32 ? 4 : 3;
        bool hasAlpha = depth == 32;
        int pos = 0;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width && pos + bytesPerPixel <= pixelData.Length; x++)
            {
                byte a = 255;
                byte r, g, b;

                if (hasAlpha)
                    a = pixelData[pos++];

                if (isRgbOrder)
                {
                    r = pixelData[pos++];
                    g = pixelData[pos++];
                    b = pixelData[pos++];
                }
                else
                {
                    b = pixelData[pos++];
                    g = pixelData[pos++];
                    r = pixelData[pos++];
                }

                // Apply colormap lookup if present
                if (colormapR != null && colormapG != null && colormapB != null)
                {
                    if (r < colormapR.Length) r = colormapR[r];
                    if (g < colormapG.Length) g = colormapG[g];
                    if (b < colormapB.Length) b = colormapB[b];
                }

                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(r);
                if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte(g);
                if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte(b);
                if (hasAlpha && channels > 3)
                    row[offset + channels - 1] = Quantum.ScaleFromByte(a);
            }

            // Pad to even byte boundary
            if ((bytesPerPixel * width) % 2 != 0 && pos < pixelData.Length) pos++;
        }
    }

    private static byte[] DecompressRle(ReadOnlySpan<byte> data, int expectedLength,
        int width, int height, int depth)
    {
        // Estimate output size generously
        int bytesPerPixel = depth <= 8 ? 1 : (depth == 24 ? 3 : 4);
        int bytesPerRow = bytesPerPixel * width;
        if (bytesPerRow % 2 != 0) bytesPerRow++;
        int estimatedSize = bytesPerRow * height;
        if (depth == 1)
        {
            int rowBytes = (width + 7) / 8;
            if (rowBytes % 2 != 0) rowBytes++;
            estimatedSize = rowBytes * height;
        }

        var output = new byte[Math.Max(estimatedSize, expectedLength > 0 ? expectedLength : estimatedSize)];
        int pos = 0;
        int outPos = 0;

        while (pos < data.Length && outPos < output.Length)
        {
            byte b = data[pos++];
            if (b != 128)
            {
                output[outPos++] = b;
            }
            else
            {
                if (pos >= data.Length) break;
                byte count = data[pos++];
                if (count == 0)
                {
                    // Literal 128
                    output[outPos++] = 128;
                }
                else
                {
                    // Repeat value (count+1) times
                    if (pos >= data.Length) break;
                    byte value = data[pos++];
                    for (int i = 0; i <= count && outPos < output.Length; i++)
                        output[outPos++] = value;
                }
            }
        }

        return output;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int depth = hasAlpha ? 32 : 24;
        int bytesPerPixel = hasAlpha ? 4 : 3;
        int scanlineBytes = bytesPerPixel * w;
        bool padScanline = scanlineBytes % 2 != 0;
        int totalPixelBytes = (scanlineBytes + (padScanline ? 1 : 0)) * h;

        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];

        BinaryPrimitives.WriteUInt32BigEndian(header, SunMagic);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)h);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)depth);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)totalPixelBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)RtFormatRgb);
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], (uint)RmtNone);
        BinaryPrimitives.WriteUInt32BigEndian(header[28..], 0); // no colormap
        ms.Write(header);

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int srcIdx = x * imgChannels;

                if (hasAlpha)
                    ms.WriteByte(Quantum.ScaleToByte(row[srcIdx + imgChannels - 1]));

                byte r = Quantum.ScaleToByte(row[srcIdx]);
                byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[srcIdx + 1]) : r;
                byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[srcIdx + 2]) : r;

                ms.WriteByte(r);
                ms.WriteByte(g);
                ms.WriteByte(b);
            }

            if (padScanline)
                ms.WriteByte(0);
        }

        return ms.ToArray();
    }
}
