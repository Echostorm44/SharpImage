// CIN (Cineon) format coder — read and write.
// Film industry predecessor to DPX. 10-bit log film density values.
// Magic: 0x802A5FD7 (big-endian). Header is 712+ bytes. Always big-endian.
// Reference: ImageMagick coders/cin.c, SMPTE 268M spec.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

public static class CinCoder
{
    private const uint Magic = 0x802A5FD7;

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        return magic == Magic;
    }

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid Cineon file");
        }

        // File header: magic(4) + imageOffset(4) + genericLength(4) + industryLength(4) + userLength(4) + fileSize(4)
        uint imageOffset = ReadU32(data, 4);

        // Image channel info starts at offset 200 in standard layout
        // Designation at offset 200: 0=universal, 1=red, 2=green, 3=blue
        // Bits per pixel at offset 202
        byte channelCount = data[196]; // number_channels at offset 196
        if (channelCount == 0 || channelCount > 8)
        {
            channelCount = 3; // default RGB
        }

        // For standard Cineon, primary channel info starts at 200
        byte bitsPerPixel = data[202];
        if (bitsPerPixel == 0)
        {
            bitsPerPixel = 10; // default 10-bit
        }

        // Pixels per line at offset 204 (4 bytes)
        int width = (int)ReadU32(data, 204);
        // Lines per image at offset 208 (4 bytes)  
        int height = (int)ReadU32(data, 208);

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
        {
            throw new InvalidDataException($"Invalid Cineon dimensions: {width}x{height}");
        }

        // Packing at offset 218 (data format section)
        // 0 = tight (packed), 5 = 32-bit word aligned
        byte packing = data.Length > 218 ? data[218] : (byte)5;

        int samplesPerPixel = Math.Min((int)channelCount, 3);
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        int pos = (int)imageOffset;

        if (bitsPerPixel == 10)
        {
            DecodePacked10Bit(data, pos, frame, width, height, samplesPerPixel, packing);
        }
        else if (bitsPerPixel == 8)
        {
            Decode8Bit(data, pos, frame, width, height, samplesPerPixel);
        }
        else if (bitsPerPixel == 16)
        {
            Decode16Bit(data, pos, frame, width, height, samplesPerPixel);
        }
        else if (bitsPerPixel == 12)
        {
            Decode12Bit(data, pos, frame, width, height, samplesPerPixel);
        }
        else
        {
            throw new NotSupportedException($"Cineon {bitsPerPixel}-bit not supported");
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int samplesPerPixel = Math.Min(imgChannels, 3);

        // 10-bit packed: 3 samples per 32-bit word, padded to row boundary
        int wordsPerRow = (w * samplesPerPixel + 2) / 3;
        int bytesPerRow = wordsPerRow * 4;
        int dataSize = bytesPerRow * h;

        int headerSize = 712; // standard Cineon header
        int totalSize = headerSize + dataSize;
        byte[] output = new byte[totalSize];

        // File header
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)headerSize); // image offset
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), (uint)headerSize); // generic header length
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), 0u); // industry header length
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), 0u); // user data length
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), (uint)totalSize); // file size

        // Version "V4.5" at offset 24
        Encoding.ASCII.GetBytes("V4.5", output.AsSpan(24));

        // Image orientation at 192: 0 = left-to-right, top-to-bottom
        output[192] = 0;
        // Number of channels at 196
        output[196] = (byte)samplesPerPixel;

        // Channel 0 info starting at 200
        // Designator[0-1]: 0 for universal metric
        output[200] = 0;
        output[201] = 0;
        // Bits per pixel
        output[202] = 10;
        // Pixels per line
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(204), (uint)w);
        // Lines per image
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(208), (uint)h);

        // Write pixel data (10-bit packed, 3 samples per 32-bit word)
        int pos = headerSize;
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            int sampleIdx = 0;
            int totalSamples = w * samplesPerPixel;

            while (sampleIdx < totalSamples)
            {
                uint word = 0;
                for (int s = 0;s < 3;s++)
                {
                    int val10 = 0;
                    if (sampleIdx < totalSamples)
                    {
                        int x = sampleIdx / samplesPerPixel;
                        int c = sampleIdx % samplesPerPixel;
                        int srcOffset = x * imgChannels + c;
                        // Scale 16-bit to 10-bit
                        val10 = (row[srcOffset] * 1023 + 32767) / 65535;
                        sampleIdx++;
                    }
                    // Pack MSB-first: sample 0 at bits 22-31, sample 1 at 12-21, sample 2 at 2-11
                    word |= (uint)(val10 & 0x3FF) << (22 - s * 10);
                }
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(pos), word);
                pos += 4;
            }
        }

        return output;
    }

    #region Decoders

    private static void DecodePacked10Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp, byte packing)
    {
        int channels = frame.NumberOfChannels;
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int sampleIdx = 0;
            int totalSamples = w * spp;

            while (sampleIdx < totalSamples)
            {
                if (pos + 3 >= data.Length)
                {
                    break;
                }

                uint word = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                pos += 4;

                int s0 = (int)((word >> 22) & 0x3FF);
                int s1 = (int)((word >> 12) & 0x3FF);
                int s2 = (int)((word >> 2) & 0x3FF);

                int[] samples = [ s0, s1, s2 ];
                for (int si = 0;si < 3 && sampleIdx < totalSamples;si++, sampleIdx++)
                {
                    int x = sampleIdx / spp;
                    int c = sampleIdx % spp;
                    if (x < w && c < channels)
                    {
                        ushort val = (ushort)((samples[si] * 65535) / 1023);
                        row[x * channels + c] = val;
                    }
                    // For grayscale CIN, replicate to all channels
                    if (spp == 1 && channels >= 3)
                    {
                        ushort val = (ushort)((samples[si] * 65535) / 1023);
                        row[x * channels + 1] = val;
                        row[x * channels + 2] = val;
                    }
                }
            }
        }
    }

    private static void Decode8Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp)
    {
        int channels = frame.NumberOfChannels;
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * channels;
                for (int c = 0;c < Math.Min(spp, channels);c++)
                {
                    row[offset + c] = pos < data.Length ? Quantum.ScaleFromByte(data[pos++]) : (ushort)0;
                }
                if (spp > channels)
                {
                    pos += spp - channels;
                }

                if (spp == 1 && channels >= 3)
                {
                    row[offset + 1] = row[offset];
                    row[offset + 2] = row[offset];
                }
            }
            // Pad to 4-byte boundary
            int rowBytes = w * spp;
            pos += (4 - (rowBytes % 4)) % 4;
        }
    }

    private static void Decode16Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp)
    {
        int channels = frame.NumberOfChannels;
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * channels;
                for (int c = 0;c < Math.Min(spp, channels);c++)
                {
                    if (pos + 1 < data.Length)
                    {
                        row[offset + c] = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                    }
                    pos += 2;
                }
                if (spp > channels)
                {
                    pos += (spp - channels) * 2;
                }

                if (spp == 1 && channels >= 3)
                {
                    row[offset + 1] = row[offset];
                    row[offset + 2] = row[offset];
                }
            }
            int rowBytes = w * spp * 2;
            pos += (4 - (rowBytes % 4)) % 4;
        }
    }

    private static void Decode12Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp)
    {
        int channels = frame.NumberOfChannels;
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * channels;
                for (int c = 0;c < Math.Min(spp, channels);c++)
                {
                    if (pos + 1 < data.Length)
                    {
                        ushort raw = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                        int val12 = raw >> 4;
                        row[offset + c] = (ushort)((val12 * 65535) / 4095);
                    }
                    pos += 2;
                }
                if (spp > channels)
                {
                    pos += (spp - channels) * 2;
                }

                if (spp == 1 && channels >= 3)
                {
                    row[offset + 1] = row[offset];
                    row[offset + 2] = row[offset];
                }
            }
        }
    }

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
