// SGI (Silicon Graphics Image) format coder — read and write.
// Supports 8-bit and 16-bit per channel, 1-4 channels, uncompressed and RLE.
// Planar channel storage with bottom-to-top row order. All values big-endian.
// Reference: ImageMagick coders/sgi.c

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static class SgiCoder
{
    private const ushort SgiMagic = 0x01DA;
    private const int HeaderSize = 512;

    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize &&
        BinaryPrimitives.ReadUInt16BigEndian(data) == SgiMagic;

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
            throw new InvalidDataException("Not a valid SGI file");

        byte storage = data[2];       // 0=uncompressed, 1=RLE
        byte bytesPerPixel = data[3]; // 1 or 2
        ushort dimension = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int depth = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
            throw new InvalidDataException($"Invalid SGI dimensions: {width}x{height}");
        if (bytesPerPixel != 1 && bytesPerPixel != 2)
            throw new InvalidDataException($"Invalid SGI bytes per pixel: {bytesPerPixel}");
        if (depth < 1 || depth > 4)
            throw new InvalidDataException($"Invalid SGI channel count: {depth}");

        // dimension=1: single scanline, dimension=2: single channel image
        if (dimension == 1) { height = 1; depth = 1; }
        else if (dimension == 2) { depth = 1; }

        // Allocate per-channel planar buffers
        int rowBytes = width * bytesPerPixel;
        var channelData = new byte[depth][];
        for (int z = 0; z < depth; z++)
            channelData[z] = new byte[height * rowBytes];

        if (storage == 0)
            DecodeUncompressed(data, channelData, width, height, depth, bytesPerPixel);
        else
            DecodeRleCompressed(data, channelData, width, height, depth, bytesPerPixel);

        return ConvertToFrame(channelData, width, height, depth, bytesPerPixel);
    }

    private static void DecodeUncompressed(ReadOnlySpan<byte> data, byte[][] channelData,
        int width, int height, int depth, int bytesPerPixel)
    {
        int rowBytes = width * bytesPerPixel;
        int pos = HeaderSize;

        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                if (pos + rowBytes > data.Length) return;
                data.Slice(pos, rowBytes).CopyTo(channelData[z].AsSpan(y * rowBytes));
                pos += rowBytes;
            }
        }
    }

    private static void DecodeRleCompressed(ReadOnlySpan<byte> data, byte[][] channelData,
        int width, int height, int depth, int bytesPerPixel)
    {
        int tableEntries = height * depth;
        int tableStart = HeaderSize;
        int rowBytes = width * bytesPerPixel;

        var offsets = new int[tableEntries];
        var lengths = new int[tableEntries];

        for (int i = 0; i < tableEntries; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(tableStart + i * 4)..]);
        int lengthTableStart = tableStart + tableEntries * 4;
        for (int i = 0; i < tableEntries; i++)
            lengths[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(lengthTableStart + i * 4)..]);

        for (int y = 0; y < height; y++)
        {
            for (int z = 0; z < depth; z++)
            {
                int idx = y + z * height;
                int offset = offsets[idx];
                int length = lengths[idx];

                if (offset < 0 || offset + length > data.Length) continue;

                DecodeRleScanline(data.Slice(offset, length), bytesPerPixel, width,
                    channelData[z].AsSpan(y * rowBytes, rowBytes));
            }
        }
    }

    private static void DecodeRleScanline(ReadOnlySpan<byte> packet, int bytesPerPixel,
        int pixelCount, Span<byte> output)
    {
        int pos = 0;
        int outPos = 0;
        int outLimit = pixelCount * bytesPerPixel;

        if (bytesPerPixel == 1)
        {
            while (pos < packet.Length && outPos < outLimit)
            {
                byte control = packet[pos++];
                int count = control & 0x7F;
                if (count == 0) break;

                if ((control & 0x80) != 0)
                {
                    // Literal: copy count bytes
                    for (int i = 0; i < count && pos < packet.Length && outPos < outLimit; i++)
                        output[outPos++] = packet[pos++];
                }
                else
                {
                    // Run: repeat one byte count times
                    if (pos >= packet.Length) break;
                    byte value = packet[pos++];
                    for (int i = 0; i < count && outPos < outLimit; i++)
                        output[outPos++] = value;
                }
            }
        }
        else
        {
            while (pos + 1 < packet.Length && outPos + 1 <= outLimit)
            {
                ushort control = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
                pos += 2;
                int count = control & 0x7F;
                if (count == 0) break;

                if ((control & 0x80) != 0)
                {
                    for (int i = 0; i < count && pos + 1 < packet.Length && outPos + 1 < outLimit; i++)
                    {
                        output[outPos] = packet[pos];
                        output[outPos + 1] = packet[pos + 1];
                        pos += 2;
                        outPos += 2;
                    }
                }
                else
                {
                    if (pos + 1 >= packet.Length) break;
                    byte hi = packet[pos];
                    byte lo = packet[pos + 1];
                    pos += 2;
                    for (int i = 0; i < count && outPos + 1 < outLimit; i++)
                    {
                        output[outPos] = hi;
                        output[outPos + 1] = lo;
                        outPos += 2;
                    }
                }
            }
        }
    }

    private static ImageFrame ConvertToFrame(byte[][] channelData, int width, int height,
        int depth, int bytesPerPixel)
    {
        bool hasAlpha = depth == 4;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;
        int rowBytes = width * bytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            int srcRow = height - 1 - y; // SGI is bottom-to-top
            var row = frame.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;

                if (bytesPerPixel == 1)
                {
                    int srcIdx = srcRow * width + x;
                    row[offset] = Quantum.ScaleFromByte(channelData[0][srcIdx]);

                    if (depth >= 3)
                    {
                        row[offset + 1] = Quantum.ScaleFromByte(channelData[1][srcIdx]);
                        row[offset + 2] = Quantum.ScaleFromByte(channelData[2][srcIdx]);
                    }
                    else
                    {
                        if (channels > 1) row[offset + 1] = row[offset];
                        if (channels > 2) row[offset + 2] = row[offset];
                    }

                    if (hasAlpha)
                        row[offset + channels - 1] = Quantum.ScaleFromByte(channelData[3][srcIdx]);
                }
                else
                {
                    int srcIdx = (srcRow * width + x) * 2;
                    row[offset] = BinaryPrimitives.ReadUInt16BigEndian(channelData[0].AsSpan(srcIdx));

                    if (depth >= 3)
                    {
                        row[offset + 1] = BinaryPrimitives.ReadUInt16BigEndian(channelData[1].AsSpan(srcIdx));
                        row[offset + 2] = BinaryPrimitives.ReadUInt16BigEndian(channelData[2].AsSpan(srcIdx));
                    }
                    else
                    {
                        if (channels > 1) row[offset + 1] = row[offset];
                        if (channels > 2) row[offset + 2] = row[offset];
                    }

                    if (hasAlpha)
                        row[offset + channels - 1] = BinaryPrimitives.ReadUInt16BigEndian(channelData[3].AsSpan(srcIdx));
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
        bool hasAlpha = image.HasAlpha;
        int depth = hasAlpha ? 4 : (imgChannels >= 3 ? 3 : 1);

        // Build planar channel buffers (8-bit)
        var channelData = new byte[depth][];
        for (int z = 0; z < depth; z++)
            channelData[z] = new byte[h * w];

        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            int destRow = h - 1 - y; // flip to bottom-to-top
            for (int x = 0; x < w; x++)
            {
                int srcIdx = x * imgChannels;
                int destIdx = destRow * w + x;

                channelData[0][destIdx] = Quantum.ScaleToByte(row[srcIdx]);
                if (depth >= 3)
                {
                    channelData[1][destIdx] = Quantum.ScaleToByte(imgChannels > 1 ? row[srcIdx + 1] : row[srcIdx]);
                    channelData[2][destIdx] = Quantum.ScaleToByte(imgChannels > 2 ? row[srcIdx + 2] : row[srcIdx]);
                }
                if (depth == 4)
                    channelData[3][destIdx] = Quantum.ScaleToByte(
                        hasAlpha ? row[srcIdx + imgChannels - 1] : Quantum.MaxValue);
            }
        }

        // RLE compress each row of each channel
        int tableEntries = h * depth;
        var offsets = new int[tableEntries];
        var lengths = new int[tableEntries];
        using var packetStream = new MemoryStream();
        int dataStart = HeaderSize + tableEntries * 4 * 2;

        for (int y = 0; y < h; y++)
        {
            for (int z = 0; z < depth; z++)
            {
                int tableIdx = y + z * h;
                int packetStart = (int)packetStream.Position;
                EncodeRleRow(packetStream, channelData[z].AsSpan(y * w, w));
                int packetLength = (int)packetStream.Position - packetStart;
                offsets[tableIdx] = dataStart + packetStart;
                lengths[tableIdx] = packetLength;
            }
        }

        var packets = packetStream.ToArray();
        int totalSize = dataStart + packets.Length;
        var output = new byte[totalSize];

        // Header
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(0), SgiMagic);
        output[2] = 1; // RLE
        output[3] = 1; // 1 byte per pixel
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)(depth == 1 ? 2 : 3));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)depth);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), 0);   // min
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), 255); // max
        // bytes 20-511: zero-filled (name, pixel_format, filler)

        // Offset table
        int pos = HeaderSize;
        for (int i = 0; i < tableEntries; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(pos), (uint)offsets[i]);
            pos += 4;
        }

        // Length table
        for (int i = 0; i < tableEntries; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(pos), (uint)lengths[i]);
            pos += 4;
        }

        // Packet data
        packets.CopyTo(output.AsSpan(pos));
        return output;
    }

    private static void EncodeRleRow(MemoryStream stream, ReadOnlySpan<byte> row)
    {
        int i = 0;
        int length = row.Length;

        while (i < length)
        {
            // Look ahead for literal vs run
            int start = i;

            // Check for a run of 3+ identical values
            if (i + 2 < length && row[i] == row[i + 1] && row[i + 1] == row[i + 2])
            {
                // Emit run
                byte value = row[i];
                int runCount = 1;
                while (i + runCount < length && runCount < 126 && row[i + runCount] == value)
                    runCount++;
                stream.WriteByte((byte)runCount);
                stream.WriteByte(value);
                i += runCount;
            }
            else
            {
                // Collect literals until we hit 3+ consecutive equal values
                int litCount = 0;
                while (i < length && litCount < 126)
                {
                    if (i + 2 < length && row[i] == row[i + 1] && row[i + 1] == row[i + 2])
                        break;
                    i++;
                    litCount++;
                }

                if (litCount > 0)
                {
                    stream.WriteByte((byte)(0x80 | litCount));
                    for (int j = 0; j < litCount; j++)
                        stream.WriteByte(row[start + j]);
                }
            }
        }

        stream.WriteByte(0); // terminator
    }
}
