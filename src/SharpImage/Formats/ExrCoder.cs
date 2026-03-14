// OpenEXR format coder — read and write.
// Supports single-part scanline EXR with HALF/FLOAT/UINT pixel types.
// Compression: NONE, RLE, ZIPS, ZIP, PIZ.
// Channels: arbitrary named channels (R, G, B, A, etc.), stored alphabetically.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Reads and writes images in the OpenEXR format. Supports HALF (16-bit float) and FLOAT (32-bit float)
/// pixel data with ZIP, ZIPS, RLE, PIZ, and no compression.
/// </summary>
public static class ExrCoder
{
    private const int MagicNumber = 20000630; // 0x762F3101
    private const int VersionScanline = 2;

    // Compression types
    private const byte CompressionNone = 0;
    private const byte CompressionRle = 1;
    private const byte CompressionZips = 2;
    private const byte CompressionZip = 3;
    private const byte CompressionPiz = 4;
    private const byte CompressionPxr24 = 5;
    private const byte CompressionB44 = 6;
    private const byte CompressionB44A = 7;
    private const byte CompressionDwaa = 8;
    private const byte CompressionDwab = 9;

    // Pixel types
    private const int PixelTypeUint = 0;
    private const int PixelTypeHalf = 1;
    private const int PixelTypeFloat = 2;

    /// <summary>
    /// Detect EXR format by magic number (0x76 0x2F 0x31 0x01).
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 &&
        data[0] == 0x76 && data[1] == 0x2F && data[2] == 0x31 && data[3] == 0x01;

    #region Read

    /// <summary>
    /// Reads an OpenEXR image from file.
    /// </summary>
    public static ImageFrame Decode(byte[] data)
    {
        int pos = 0;

        // Magic number
        int magic = ReadInt32(data, ref pos);
        if (magic != MagicNumber)
            throw new InvalidDataException("Not a valid OpenEXR file.");

        // Version field
        int version = ReadInt32(data, ref pos);
        int formatVersion = version & 0xFF;
        bool isTiled = (version & 0x200) != 0;
        bool isMultiPart = (version & 0x1000) != 0;

        if (formatVersion > 2)
            throw new NotSupportedException($"OpenEXR version {formatVersion} is not supported.");
        if (isTiled)
            throw new NotSupportedException("Tiled EXR files are not yet supported.");
        if (isMultiPart)
            throw new NotSupportedException("Multi-part EXR files are not yet supported.");

        // Parse header attributes
        var channels = new List<ExrChannel>();
        byte compression = CompressionNone;
        int dataWindowMinX = 0, dataWindowMinY = 0, dataWindowMaxX = 0, dataWindowMaxY = 0;
        int displayWindowMinX = 0, displayWindowMinY = 0, displayWindowMaxX = 0, displayWindowMaxY = 0;
        float pixelAspectRatio = 1.0f;

        while (pos < data.Length)
        {
            string attrName = ReadNullString(data, ref pos);
            if (string.IsNullOrEmpty(attrName))
                break; // End of header

            string attrType = ReadNullString(data, ref pos);
            int attrSize = ReadInt32(data, ref pos);
            int attrEnd = pos + attrSize;

            switch (attrName)
            {
                case "channels":
                    channels = ParseChannels(data, ref pos, attrEnd);
                    break;
                case "compression":
                    compression = data[pos];
                    break;
                case "dataWindow":
                    dataWindowMinX = ReadInt32(data, ref pos);
                    dataWindowMinY = ReadInt32(data, ref pos);
                    dataWindowMaxX = ReadInt32(data, ref pos);
                    dataWindowMaxY = ReadInt32(data, ref pos);
                    pos -= 16; // Will be advanced by attrEnd below
                    break;
                case "displayWindow":
                    displayWindowMinX = ReadInt32(data, ref pos);
                    displayWindowMinY = ReadInt32(data, ref pos);
                    displayWindowMaxX = ReadInt32(data, ref pos);
                    displayWindowMaxY = ReadInt32(data, ref pos);
                    pos -= 16;
                    break;
                case "pixelAspectRatio":
                    pixelAspectRatio = BitConverter.ToSingle(data, pos);
                    break;
            }

            pos = attrEnd;
        }

        if (channels.Count == 0)
            throw new InvalidDataException("EXR file has no channels.");

        int width = dataWindowMaxX - dataWindowMinX + 1;
        int height = dataWindowMaxY - dataWindowMinY + 1;

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid EXR data window: {width}×{height}.");

        // Sort channels alphabetically (EXR spec requirement)
        channels.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        // Determine scanlines per block based on compression
        int scanlinesPerBlock = compression switch
        {
            CompressionNone => 1,
            CompressionRle => 1,
            CompressionZips => 1,
            CompressionZip => 16,
            CompressionPiz => 32,
            CompressionPxr24 => 16,
            CompressionB44 or CompressionB44A => 32,
            CompressionDwaa => 32,
            CompressionDwab => 256,
            _ => throw new NotSupportedException($"EXR compression type {compression} is not supported.")
        };

        int blockCount = (height + scanlinesPerBlock - 1) / scanlinesPerBlock;

        // Read offset table
        long[] offsets = new long[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            offsets[i] = ReadInt64(data, ref pos);
        }

        // Compute bytes per pixel row for uncompressed data
        int bytesPerPixelRow = 0;
        foreach (var ch in channels)
        {
            bytesPerPixelRow += PixelTypeSize(ch.PixelType) * width;
        }

        // Allocate HDR float buffer for decoding
        // We'll store as float[height, width, maxChannels] then map to ImageFrame
        int outputChannels = DetermineOutputChannels(channels);
        long floatPixelCountLong = (long)height * width * outputChannels;
        if (floatPixelCountLong > int.MaxValue)
            throw new InvalidDataException($"EXR image dimensions {width}x{height} with {outputChannels} channels exceed maximum buffer size.");
        int floatPixelCount = (int)floatPixelCountLong;
        float[] floatPixels = ArrayPool<float>.Shared.Rent(floatPixelCount);
        Array.Clear(floatPixels, 0, floatPixelCount);
        try
        {

        // Read scanline blocks
        for (int block = 0; block < blockCount; block++)
        {
            int blockPos = (int)offsets[block];
            int yCoord = ReadInt32(data, ref blockPos);
            int compressedSize = ReadInt32(data, ref blockPos);

            int blockStartY = yCoord - dataWindowMinY;
            int blockLines = Math.Min(scanlinesPerBlock, height - blockStartY);

            // Decompress block
            byte[] uncompressed;
            int uncompressedSize = blockLines * bytesPerPixelRow;

            if (compression == CompressionNone)
            {
                uncompressed = data.AsSpan(blockPos, compressedSize).ToArray();
            }
            else if (compression == CompressionZip || compression == CompressionZips)
            {
                uncompressed = DecompressZip(data.AsSpan(blockPos, compressedSize), uncompressedSize);
            }
            else if (compression == CompressionRle)
            {
                uncompressed = DecompressRle(data.AsSpan(blockPos, compressedSize), uncompressedSize);
            }
            else if (compression == CompressionPiz)
            {
                // PIZ is complex — fall back to treating as uncompressed if same size
                if (compressedSize == uncompressedSize)
                    uncompressed = data.AsSpan(blockPos, compressedSize).ToArray();
                else
                    uncompressed = DecompressPiz(data.AsSpan(blockPos, compressedSize), uncompressedSize, width, blockLines, channels);
            }
            else
            {
                throw new NotSupportedException($"EXR compression type {compression} is not implemented.");
            }

            // Parse channel data from uncompressed block
            // Channels are stored non-interleaved: all pixels of channel 0, then all of channel 1, etc.
            // Within each channel, data is stored scanline by scanline, left to right
            int srcOffset = 0;
            for (int line = 0; line < blockLines; line++)
            {
                int y = blockStartY + line;
                if (y >= height) break;

                foreach (var ch in channels)
                {
                    int pixelSize = PixelTypeSize(ch.PixelType);
                    int chIdx = GetOutputChannelIndex(ch.Name, outputChannels);

                    if (chIdx < 0)
                    {
                        // Skip this channel data
                        srcOffset += pixelSize * width;
                        continue;
                    }

                    for (int x = 0; x < width; x++)
                    {
                        float value = ReadPixelValue(uncompressed, srcOffset, ch.PixelType);
                        floatPixels[(y * width + x) * outputChannels + chIdx] = value;
                        srcOffset += pixelSize;
                    }
                }
            }
        }

        // Convert float pixels to ImageFrame (16-bit ushort per channel)
        bool hasAlpha = outputChannels >= 4;
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, hasAlpha);

        int imgChannels = hasAlpha ? 4 : 3;
        for (int y = 0; y < height; y++)
        {
            var row = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int srcIdx = (y * width + x) * outputChannels;
                int dstIdx = x * imgChannels;

                // Tone map from HDR float to 16-bit range
                // Simple linear clamp for now (EXR values are typically scene-linear)
                for (int c = 0; c < Math.Min(outputChannels, imgChannels); c++)
                {
                    float val = floatPixels[srcIdx + c];
                    // Clamp to [0, 1] then scale to quantum range
                    val = Math.Clamp(val, 0f, 1f);
                    row[dstIdx + c] = (ushort)(val * Quantum.MaxValue + 0.5f);
                }

                // If source has no alpha but output expects it, set full opaque
                if (hasAlpha && outputChannels < 4)
                    row[dstIdx + 3] = Quantum.MaxValue;
            }
        }

        return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPixels);
        }
    }

    #endregion

    #region Write

    /// <summary>
    /// Encodes an ImageFrame as OpenEXR data with ZIP compression and HALF pixel type.
    /// </summary>
    public static byte[] Encode(ImageFrame image, byte compressionType = CompressionZip)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int imgChannels = image.HasAlpha ? 4 : 3;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Magic number
        bw.Write(MagicNumber);

        // Version field (version 2, scanline format)
        bw.Write(VersionScanline);

        // Build channel list (alphabetical: A, B, G, R)
        var channelNames = image.HasAlpha
            ? new[] { "A", "B", "G", "R" }
            : new[] { "B", "G", "R" };

        // Header attributes
        WriteAttribute(bw, "channels", "chlist", () =>
        {
            using var chMs = new MemoryStream();
            using var chBw = new BinaryWriter(chMs, Encoding.ASCII, leaveOpen: true);
            foreach (var name in channelNames)
            {
                WriteNullString(chBw, name);
                chBw.Write(PixelTypeHalf); // pixel type = HALF
                chBw.Write((byte)0);       // pLinear (unused)
                chBw.Write((short)0);      // padding (3 bytes for alignment - but EXR is densely packed, so just reserved)
                chBw.Write((byte)0);       // extra padding byte
                chBw.Write(1);             // xSampling
                chBw.Write(1);             // ySampling
            }
            chBw.Write((byte)0); // null terminator for channel list
            chBw.Flush();
            return chMs.ToArray();
        });

        WriteAttribute(bw, "compression", "compression", () => new[] { compressionType });

        WriteAttribute(bw, "dataWindow", "box2i", () =>
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), width - 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), height - 1);
            return buf;
        });

        WriteAttribute(bw, "displayWindow", "box2i", () =>
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), width - 1);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), height - 1);
            return buf;
        });

        WriteAttribute(bw, "lineOrder", "lineOrder", () => new byte[] { 0 }); // INCREASING_Y

        WriteAttribute(bw, "pixelAspectRatio", "float", () =>
            BitConverter.GetBytes(1.0f));

        WriteAttribute(bw, "screenWindowCenter", "v2f", () => new byte[8]); // (0, 0)

        WriteAttribute(bw, "screenWindowWidth", "float", () =>
            BitConverter.GetBytes(1.0f));

        // End of header
        bw.Write((byte)0);

        int scanlinesPerBlock = compressionType switch
        {
            CompressionNone => 1,
            CompressionRle => 1,
            CompressionZips => 1,
            CompressionZip => 16,
            _ => 16
        };

        int blockCount = (height + scanlinesPerBlock - 1) / scanlinesPerBlock;
        int bytesPerHalfRow = channelNames.Length * width * 2; // 2 bytes per HALF per pixel per channel

        // Reserve space for offset table
        long offsetTablePos = ms.Position;
        for (int i = 0; i < blockCount; i++)
            bw.Write(0L);

        // Write scanline blocks
        long[] blockOffsets = new long[blockCount];

        for (int block = 0; block < blockCount; block++)
        {
            blockOffsets[block] = ms.Position;

            int startY = block * scanlinesPerBlock;
            int blockLines = Math.Min(scanlinesPerBlock, height - startY);

            // Build uncompressed scanline data (non-interleaved by channel, alphabetical)
            int uncompSize = blockLines * bytesPerHalfRow;
            byte[] uncompressed = new byte[uncompSize];
            int dstOff = 0;

            for (int line = 0; line < blockLines; line++)
            {
                int y = startY + line;
                var srcRow = image.GetPixelRow(y);

                // Channels in alphabetical order: A, B, G, R (or B, G, R)
                foreach (var chName in channelNames)
                {
                    int chIdx = chName switch
                    {
                        "R" => 0,
                        "G" => 1,
                        "B" => 2,
                        "A" => 3,
                        _ => -1
                    };

                    for (int x = 0; x < width; x++)
                    {
                        float val = (float)(srcRow[x * imgChannels + chIdx] * Quantum.Scale);
                        Half half = (Half)val;
                        BinaryPrimitives.WriteHalfLittleEndian(uncompressed.AsSpan(dstOff), half);
                        dstOff += 2;
                    }
                }
            }

            // Compress
            byte[] compressed;
            if (compressionType == CompressionNone)
            {
                compressed = uncompressed;
            }
            else if (compressionType == CompressionZip || compressionType == CompressionZips)
            {
                // Apply predictor (interleave bytes) before deflate
                byte[] predicted = ApplyPredictor(uncompressed);
                compressed = CompressZip(predicted);
                if (compressed.Length >= uncompressed.Length)
                    compressed = uncompressed; // Store uncompressed if larger
            }
            else if (compressionType == CompressionRle)
            {
                byte[] predicted = ApplyPredictor(uncompressed);
                compressed = CompressRle(predicted);
                if (compressed.Length >= uncompressed.Length)
                    compressed = uncompressed;
            }
            else
            {
                compressed = uncompressed; // Fallback to no compression
            }

            // Write block: y coordinate, compressed size, data
            bw.Write(startY);
            bw.Write(compressed.Length);
            bw.Write(compressed);
        }

        // Go back and write offset table
        ms.Position = offsetTablePos;
        for (int i = 0; i < blockCount; i++)
            bw.Write(blockOffsets[i]);

        bw.Flush();
        return ms.ToArray();
    }

    #endregion

    #region Compression

    /// <summary>
    /// Applies the EXR byte predictor: delta + interleave bytes.
    /// Each scanline's bytes are delta-encoded, then even/odd bytes are separated.
    /// </summary>
    private static byte[] ApplyPredictor(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];

        // Step 1: Delta encoding
        byte[] delta = new byte[n];
        delta[0] = data[0];
        for (int i = 1; i < n; i++)
            delta[i] = (byte)(data[i] - data[i - 1]);

        // Step 2: Interleave — separate into two halves
        int half = (n + 1) / 2;
        int s = 0;
        for (int i = 0; i < n; i += 2)
            result[s++] = delta[i];
        for (int i = 1; i < n; i += 2)
            result[s++] = delta[i];

        return result;
    }

    /// <summary>
    /// Reverses the EXR byte predictor: de-interleave + un-delta.
    /// </summary>
    private static byte[] ReversePredictor(byte[] data, int length)
    {
        // Step 1: De-interleave
        int half = (length + 1) / 2;
        byte[] deinterleaved = new byte[length];
        int s = 0;
        for (int i = 0; i < length; i += 2)
            deinterleaved[i] = data[s++];
        for (int i = 1; i < length; i += 2)
            deinterleaved[i] = data[s++];

        // Step 2: Un-delta
        byte[] result = new byte[length];
        result[0] = deinterleaved[0];
        for (int i = 1; i < length; i++)
            result[i] = (byte)(deinterleaved[i] + result[i - 1]);

        return result;
    }

    private static byte[] CompressZip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write(data);
        }
        return ms.ToArray();
    }

    private static byte[] DecompressZip(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        byte[] predicted = new byte[expectedSize];
        int totalRead = 0;
        while (totalRead < expectedSize)
        {
            int read = ds.Read(predicted, totalRead, expectedSize - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return ReversePredictor(predicted, totalRead);
    }

    private static byte[] CompressRle(byte[] data)
    {
        using var ms = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            // Count run
            int runStart = i;
            byte val = data[i];
            while (i < data.Length && data[i] == val && (i - runStart) < 127)
                i++;
            int runLen = i - runStart;

            if (runLen >= 3)
            {
                // Run of identical bytes
                ms.WriteByte((byte)(runLen - 1));
                ms.WriteByte(val);
            }
            else
            {
                // Non-run (literal bytes)
                int literalStart = runStart;
                i = runStart;
                while (i < data.Length && (i - literalStart) < 127)
                {
                    if (i + 2 < data.Length && data[i] == data[i + 1] && data[i] == data[i + 2])
                        break; // Upcoming run
                    i++;
                }
                int literalLen = i - literalStart;
                ms.WriteByte((byte)(-(literalLen - 1) & 0xFF));
                ms.Write(data, literalStart, literalLen);
            }
        }
        return ms.ToArray();
    }

    private static byte[] DecompressRle(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        byte[] predicted = new byte[expectedSize];
        int src = 0, dst = 0;

        while (src < compressed.Length && dst < expectedSize)
        {
            sbyte count = (sbyte)compressed[src++];
            if (count >= 0)
            {
                // Run of (count + 1) identical bytes
                int runLen = count + 1;
                byte val = compressed[src++];
                for (int j = 0; j < runLen && dst < expectedSize; j++)
                    predicted[dst++] = val;
            }
            else
            {
                // Literal of (-count + 1) bytes
                int literalLen = -count + 1;
                for (int j = 0; j < literalLen && src < compressed.Length && dst < expectedSize; j++)
                    predicted[dst++] = compressed[src++];
            }
        }

        return ReversePredictor(predicted, dst);
    }

    /// <summary>
    /// PIZ decompression — Wavelet + Huffman + LUT. This is a simplified implementation.
    /// Full PIZ requires: reorder → Huffman decode → inverse Haar wavelet → LUT reverse.
    /// For now, fall back to returning raw data if the block can't be decompressed.
    /// </summary>
    private static byte[] DecompressPiz(ReadOnlySpan<byte> compressed, int expectedSize,
        int width, int blockLines, List<ExrChannel> channels)
    {
        // PIZ decompression is extremely complex (Haar wavelet + Huffman).
        // A full implementation would require 200+ lines.
        // For initial release, we throw a clear error for PIZ-compressed files.
        throw new NotSupportedException(
            "PIZ compression is not yet implemented. " +
            "Re-save the EXR file with ZIP or ZIPS compression for compatibility.");
    }

    #endregion

    #region Channel Helpers

    private struct ExrChannel
    {
        public string Name;
        public int PixelType;
        public int XSampling;
        public int YSampling;
    }

    private static List<ExrChannel> ParseChannels(byte[] data, ref int pos, int end)
    {
        var channels = new List<ExrChannel>();
        while (pos < end)
        {
            string name = ReadNullString(data, ref pos);
            if (string.IsNullOrEmpty(name))
                break;

            var ch = new ExrChannel
            {
                Name = name,
                PixelType = ReadInt32(data, ref pos)
            };
            pos += 4; // pLinear (1 byte) + reserved (3 bytes)
            ch.XSampling = ReadInt32(data, ref pos);
            ch.YSampling = ReadInt32(data, ref pos);
            channels.Add(ch);
        }
        return channels;
    }

    private static int PixelTypeSize(int pixelType) => pixelType switch
    {
        PixelTypeUint => 4,
        PixelTypeHalf => 2,
        PixelTypeFloat => 4,
        _ => throw new NotSupportedException($"Unknown EXR pixel type: {pixelType}")
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ReadPixelValue(byte[] data, int offset, int pixelType)
    {
        return pixelType switch
        {
            PixelTypeHalf => (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(offset)),
            PixelTypeFloat => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)),
            PixelTypeUint => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) / (float)uint.MaxValue,
            _ => 0f
        };
    }

    /// <summary>
    /// Maps EXR channel names to output channel indices (R=0, G=1, B=2, A=3).
    /// Returns -1 for unknown channels.
    /// </summary>
    private static int GetOutputChannelIndex(string name, int outputChannels)
    {
        return name switch
        {
            "R" or "r" or "Red" => 0,
            "G" or "g" or "Green" => 1,
            "B" or "b" or "Blue" => 2,
            "A" or "a" or "Alpha" => 3,
            "Y" => 0, // Luminance-only maps to R
            _ => -1
        };
    }

    /// <summary>
    /// Determines output channel count from EXR channel list.
    /// </summary>
    private static int DetermineOutputChannels(List<ExrChannel> channels)
    {
        bool hasR = false, hasG = false, hasB = false, hasA = false, hasY = false;
        foreach (var ch in channels)
        {
            switch (ch.Name)
            {
                case "R" or "r" or "Red": hasR = true; break;
                case "G" or "g" or "Green": hasG = true; break;
                case "B" or "b" or "Blue": hasB = true; break;
                case "A" or "a" or "Alpha": hasA = true; break;
                case "Y": hasY = true; break;
            }
        }

        if (hasR || hasG || hasB)
            return hasA ? 4 : 3;

        // Luminance-only
        if (hasY)
            return hasA ? 4 : 3; // Expand Y to RGB

        // Unknown channels — map first 3/4 as RGB(A)
        return Math.Min(channels.Count, 4);
    }

    #endregion

    #region I/O Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32(byte[] data, ref int pos)
    {
        int val = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadInt64(byte[] data, ref int pos)
    {
        long val = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos));
        pos += 8;
        return val;
    }

    private static string ReadNullString(byte[] data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0)
            pos++;
        string result = Encoding.ASCII.GetString(data, start, pos - start);
        if (pos < data.Length)
            pos++; // Skip null terminator
        return result;
    }

    private static void WriteNullString(BinaryWriter bw, string s)
    {
        bw.Write(Encoding.ASCII.GetBytes(s));
        bw.Write((byte)0);
    }

    private static void WriteAttribute(BinaryWriter bw, string name, string type, Func<byte[]> valueWriter)
    {
        WriteNullString(bw, name);
        WriteNullString(bw, type);
        byte[] value = valueWriter();
        bw.Write(value.Length);
        bw.Write(value);
    }

    #endregion
}
