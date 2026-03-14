// DPX (Digital Picture Exchange) format coder — read and write.
// Film industry standard. Supports 8, 10, 12, and 16-bit RGB.
// Magic: "SDPX" (big-endian) or "XPDS" (little-endian).

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

public static class DpxCoder
{
    private const uint MagicBE = 0x53445058; // "SDPX"
    private const uint MagicLE = 0x58504453; // "XPDS"

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        return magic == MagicBE || magic == MagicLE;
    }

    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid DPX file");
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        bool bigEndian = magic == MagicBE;

        uint imageOffset = ReadU32(data, 4, bigEndian);

        // Image information header at offset 768
        // Element descriptor at offset 800
        // Pixels per line at offset 772, lines at 776
        if (data.Length < 810)
        {
            throw new InvalidDataException("DPX header too short");
        }

        // Orientation info at 768
        int width = (int)ReadU32(data, 772, bigEndian);
        int height = (int)ReadU32(data, 776, bigEndian);

        // First image element at offset 780
        // Descriptor at 800, bit size at 803
        byte descriptor = data[800]; // 50=RGB, 51=RGBA, 6=Luma
        byte bitSize = data[803];

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid DPX dimensions: {width}x{height}");
        }

        int samplesPerPixel = descriptor switch
        {
            50 => 3,  // RGB
            51 or 52 => 4,  // RGBA / ABGR
            6 => 1,   // Luminance
            _ => 3    // default RGB
        };

        bool hasAlpha = samplesPerPixel == 4;
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;

        int pos = (int)imageOffset;

        switch (bitSize)
        {
            case 10:
                DecodePacked10Bit(data, pos, frame, width, height, samplesPerPixel, bigEndian);
                break;
            case 12:
                DecodePacked12Bit(data, pos, frame, width, height, samplesPerPixel, bigEndian);
                break;
            case 16:
                Decode16Bit(data, pos, frame, width, height, samplesPerPixel, bigEndian);
                break;
            default: // 8-bit
                Decode8Bit(data, pos, frame, width, height, samplesPerPixel);
                break;
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int samplesPerPixel = hasAlpha ? 4 : 3;
        byte descriptor = hasAlpha ? (byte)51 : (byte)50;

        // Use 16-bit big-endian output (matches our quantum)
        int bytesPerRow = w * samplesPerPixel * 2;
        // Pad to 4-byte boundary
        bytesPerRow = (bytesPerRow + 3) & ~3;
        int dataSize = bytesPerRow * h;
        int imageOffset = 2048; // standard DPX offset
        int totalSize = imageOffset + dataSize;

        byte[] output = new byte[totalSize];

        // File header (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), MagicBE);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)imageOffset);
        // Version "V2.0" at offset 8
        output[8] = (byte)'V';
        output[9] = (byte)'2';
        output[10] = (byte)'.';
        output[11] = (byte)'0';
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), (uint)totalSize); // file size
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(24), 2048u); // generic header size
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(28), 0u); // industry header size

        // Image info at 768
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(768), 0); // orientation
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(770), 1); // element count
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(772), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(776), (uint)h);

        // Element 0 at 780
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(780), 0u); // data sign (unsigned)
        output[800] = descriptor;
        output[801] = 0; // transfer: user-defined
        output[802] = 0; // colorimetric
        output[803] = 16; // bit size
        output[804] = 0; // packing (filled method A)
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(808), (uint)imageOffset);

        // Write pixel data (16-bit big-endian)
        int pos = imageOffset;
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < w;x++)
            {
                int srcIdx = x * imgChannels;
                // R
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos), row[srcIdx]);
                pos += 2;
                // G
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos),
                    imgChannels > 1 ? row[srcIdx + 1] : row[srcIdx]);
                pos += 2;
                // B
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos),
                    imgChannels > 2 ? row[srcIdx + 2] : row[srcIdx]);
                pos += 2;
                // A
                if (hasAlpha)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos),
                        row[srcIdx + imgChannels - 1]);
                    pos += 2;
                }
            }
            // Pad row to 4-byte boundary
            while ((pos - imageOffset) % 4 != 0)
            {
                pos++;
            }
        }

        return output;
    }

    #region Decoders

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
                    pos += spp - channels; // skip extra channels
                }
            }
            // Align to 4-byte boundary
            int rowBytes = w * spp;
            int padding = (4 - (rowBytes % 4)) % 4;
            pos += padding;
        }
    }

    private static void Decode16Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp, bool bigEndian)
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
                        ushort val = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..])
                            : BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                        row[offset + c] = val;
                    }
                    pos += 2;
                }
                if (spp > channels)
                {
                    pos += (spp - channels) * 2;
                }
            }
            int rowBytes = w * spp * 2;
            int padding = (4 - (rowBytes % 4)) % 4;
            pos += padding;
        }
    }

    private static void DecodePacked10Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp, bool bigEndian)
    {
        int channels = frame.NumberOfChannels;
        // 10-bit packed: 3 samples per 32-bit word
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

                uint word = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data[pos..])
                    : BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                pos += 4;

                // 3 × 10-bit samples packed MSB-first in 32-bit word (2 bits unused)
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
                        // Scale 10-bit (0-1023) to 16-bit
                        ushort val = (ushort)((samples[si] * 65535) / 1023);
                        row[x * channels + c] = val;
                    }
                }
            }
        }
    }

    private static void DecodePacked12Bit(ReadOnlySpan<byte> data, int pos,
        ImageFrame frame, int w, int h, int spp, bool bigEndian)
    {
        int channels = frame.NumberOfChannels;
        // 12-bit: each sample stored in 16 bits (padded)
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
                        ushort raw = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..])
                            : BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                        int val12 = raw >> 4; // top 12 bits
                        ushort val16 = (ushort)((val12 * 65535) / 4095);
                        row[offset + c] = val16;
                    }
                    pos += 2;
                }
                if (spp > channels)
                {
                    pos += (spp - channels) * 2;
                }
            }
        }
    }

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
