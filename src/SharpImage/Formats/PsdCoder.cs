// PSD (Adobe Photoshop) format coder — read and write.
// Reads the composite (flattened) image and individual layers from PSD files.
// Writes single-layer PSD files with RLE (PackBits) or raw compression.
// Supports 8-bit and 16-bit RGB/RGBA color modes.
// All multi-byte values are big-endian (MSB-first).

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Metadata for a single PSD layer.
/// </summary>
public sealed class PsdLayerInfo
{
    public string Name { get; init; } = "";
    public int Top { get; init; }
    public int Left { get; init; }
    public int Bottom { get; init; }
    public int Right { get; init; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public byte Opacity { get; init; } = 255;
    public bool Visible { get; init; } = true;
    public string BlendMode { get; init; } = "norm";
    public ImageFrame? Image { get; init; }
}

/// <summary>
/// Result of decoding a PSD file with layers.
/// </summary>
public sealed class PsdDocument
{
    public ImageFrame Composite { get; init; } = null!;
    public List<PsdLayerInfo> Layers { get; init; } = [];
}

public static class PsdCoder
{
    private static ReadOnlySpan<byte> Signature => "8BPS"u8;

    private const int PsdVersion = 1;
    private const int HeaderSize = 26;

    // Compression types
    private const ushort CompressionRaw = 0;
    private const ushort CompressionRle = 1;

    // Color modes
    private const ushort ModeGrayscale = 1;
    private const ushort ModeRgb = 3;

    /// <summary>
    /// Detect PSD format by "8BPS" magic.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        return data.Length >= HeaderSize &&
               data[0] == '8' && data[1] == 'B' && data[2] == 'P' && data[3] == 'S';
    }

    /// <summary>
    /// Decode the composite/flattened image from a PSD file.
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid PSD file");
        }

        int pos = 0;

        // Header
        pos += 4; // skip signature
        ushort version = ReadU16BE(data, ref pos);
        if (version != 1 && version != 2)
        {
            throw new InvalidDataException($"Unsupported PSD version: {version}");
        }

        pos += 6; // reserved
        ushort channelCount = ReadU16BE(data, ref pos);
        uint height = ReadU32BE(data, ref pos);
        uint width = ReadU32BE(data, ref pos);
        ushort depth = ReadU16BE(data, ref pos);
        ushort colorMode = ReadU16BE(data, ref pos);

        if (depth != 8 && depth != 16)
        {
            throw new InvalidDataException($"Unsupported PSD depth: {depth}");
        }

        if (colorMode != ModeRgb && colorMode != ModeGrayscale)
        {
            throw new InvalidDataException($"Unsupported PSD color mode: {colorMode}");
        }

        // Skip color mode data section
        uint colorModeLength = ReadU32BE(data, ref pos);
        pos += (int)colorModeLength;

        // Skip image resources section
        uint resourcesLength = ReadU32BE(data, ref pos);
        pos += (int)resourcesLength;

        // Skip layer and mask information section
        uint layerInfoLength = ReadU32BE(data, ref pos);
        pos += (int)layerInfoLength;

        // Read composite image data
        if (pos + 2 > data.Length)
        {
            throw new InvalidDataException("PSD file has no composite image data");
        }

        ushort compression = ReadU16BE(data, ref pos);

        bool hasAlpha = channelCount >= 4 && colorMode == ModeRgb;
        int imageChannels = colorMode == ModeGrayscale ? 1 : 3;
        int totalChannels = hasAlpha ? imageChannels + 1 : imageChannels;
        // Read at most what we can handle
        int readChannels = Math.Min(channelCount, totalChannels);

        int w = (int)width, h = (int)height;
        int bytesPerChannel = depth / 8;

        // Validate buffer size won't overflow
        long channelBufSizeLong = (long)w * h * bytesPerChannel;
        if (channelBufSizeLong > int.MaxValue)
            throw new InvalidDataException($"PSD image dimensions {w}x{h} with {depth}-bit depth exceed maximum buffer size.");

        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, hasAlpha);
        int frameChannels = frame.NumberOfChannels;

        // Allocate planar buffers for each channel
        int channelBufSize = (int)channelBufSizeLong;
        var channelData = new byte[readChannels][];
        for (int c = 0;c < readChannels;c++)
        {
            channelData[c] = ArrayPool<byte>.Shared.Rent(channelBufSize);
        }

        try
        {

        if (compression == CompressionRaw)
        {
            // Raw: channels stored sequentially in planar order
            for (int c = 0;c < readChannels;c++)
            {
                int bytesToRead = w * h * bytesPerChannel;
                if (pos + bytesToRead > data.Length)
                {
                    throw new InvalidDataException("Unexpected end of PSD data");
                }

                data.Slice(pos, bytesToRead).CopyTo(channelData[c]);
                pos += bytesToRead;
            }
            // Skip any extra channels we don't handle
            for (int c = readChannels;c < channelCount;c++)
            {
                pos += w * h * bytesPerChannel;
            }
        }
        else if (compression == CompressionRle)
        {
            // RLE: first come row byte counts for ALL channels, then the compressed data
            int totalRows = channelCount * h;
            int rowCountSize = version == 1 ? 2 : 4;
            var rowByteCounts = new int[totalRows];

            for (int r = 0;r < totalRows;r++)
            {
                if (version == 1)
                {
                    rowByteCounts[r] = ReadU16BE(data, ref pos);
                }
                else
                {
                    rowByteCounts[r] = (int)ReadU32BE(data, ref pos);
                }
            }

            // Decompress each channel
            for (int c = 0;c < channelCount;c++)
            {
                for (int y = 0;y < h;y++)
                {
                    int rowIdx = c * h + y;
                    int compressedSize = rowByteCounts[rowIdx];

                    if (c < readChannels)
                    {
                        int destOffset = y * w * bytesPerChannel;
                        DecompressPackBits(data.Slice(pos, compressedSize),
                            channelData[c].AsSpan(destOffset, w * bytesPerChannel));
                    }
                    pos += compressedSize;
                }
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported PSD compression: {compression}");
        }

        // Convert planar channel data to interleaved pixels
        for (int y = 0;y < h;y++)
        {
            var row = frame.GetPixelRowForWrite(y);

            for (int x = 0;x < w;x++)
            {
                int srcIdx = (y * w + x) * bytesPerChannel;
                int dstIdx = x * frameChannels;

                if (colorMode == ModeGrayscale)
                {
                    ushort gray = ReadChannelValue(channelData[0], srcIdx, depth);
                    row[dstIdx] = gray;
                    if (frameChannels > 1)
                    {
                        row[dstIdx + 1] = gray;
                    }

                    if (frameChannels > 2)
                    {
                        row[dstIdx + 2] = gray;
                    }
                }
                else // RGB
                {
                    row[dstIdx] = ReadChannelValue(channelData[0], srcIdx, depth);
                    if (frameChannels > 1 && readChannels > 1)
                    {
                        row[dstIdx + 1] = ReadChannelValue(channelData[1], srcIdx, depth);
                    }

                    if (frameChannels > 2 && readChannels > 2)
                    {
                        row[dstIdx + 2] = ReadChannelValue(channelData[2], srcIdx, depth);
                    }
                }

                // Alpha
                if (hasAlpha && readChannels > imageChannels)
                {
                    row[dstIdx + frameChannels - 1] = ReadChannelValue(
                                        channelData[imageChannels], srcIdx, depth);
                }
                else if (hasAlpha)
                {
                    row[dstIdx + frameChannels - 1] = Quantum.MaxValue;
                }
            }
        }

        return frame;
        }
        finally
        {
            for (int c = 0; c < readChannels; c++)
                ArrayPool<byte>.Shared.Return(channelData[c]);
        }
    }

    /// <summary>
    /// Decode a PSD file returning both the composite image and individual layers.
    /// </summary>
    public static PsdDocument DecodeWithLayers(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
            throw new InvalidDataException("Not a valid PSD file");

        int pos = 0;

        // Header
        pos += 4; // skip signature
        ushort version = ReadU16BE(data, ref pos);
        if (version != 1 && version != 2)
            throw new InvalidDataException($"Unsupported PSD version: {version}");

        pos += 6; // reserved
        ushort channelCount = ReadU16BE(data, ref pos);
        uint height = ReadU32BE(data, ref pos);
        uint width = ReadU32BE(data, ref pos);
        ushort depth = ReadU16BE(data, ref pos);
        ushort colorMode = ReadU16BE(data, ref pos);

        if (depth != 8 && depth != 16)
            throw new InvalidDataException($"Unsupported PSD depth: {depth}");
        if (colorMode != ModeRgb && colorMode != ModeGrayscale)
            throw new InvalidDataException($"Unsupported PSD color mode: {colorMode}");

        // Skip color mode data
        uint colorModeLength = ReadU32BE(data, ref pos);
        pos += (int)colorModeLength;

        // Skip image resources
        uint resourcesLength = ReadU32BE(data, ref pos);
        pos += (int)resourcesLength;

        // Parse layer and mask information section
        int layerMaskStart = pos;
        uint layerMaskLength = ReadU32BE(data, ref pos);
        int layerMaskEnd = layerMaskStart + 4 + (int)layerMaskLength;

        var layers = new List<PsdLayerInfo>();

        if (layerMaskLength > 0)
        {
            layers = ParseLayerSection(data, ref pos, version, depth, colorMode);
        }

        // Skip to composite image data
        pos = layerMaskEnd;

        // Decode composite (reuse existing logic)
        ImageFrame composite;
        if (pos + 2 <= data.Length)
        {
            composite = DecodeCompositeImage(data, ref pos, version, channelCount,
                (int)width, (int)height, depth, colorMode);
        }
        else
        {
            // No composite data — create from layers if possible
            composite = new ImageFrame();
            composite.Initialize((int)width, (int)height, ColorspaceType.SRGB, false);
        }

        return new PsdDocument { Composite = composite, Layers = layers };
    }

    private static List<PsdLayerInfo> ParseLayerSection(ReadOnlySpan<byte> data, ref int pos,
        ushort version, ushort depth, ushort colorMode)
    {
        var layers = new List<PsdLayerInfo>();

        // Layer info section length
        uint layerInfoLength = ReadU32BE(data, ref pos);
        if (layerInfoLength == 0)
            return layers;

        int layerInfoEnd = pos + (int)layerInfoLength;

        // Layer count (signed — negative means first alpha is transparency)
        short layerCount = (short)ReadU16BE(data, ref pos);
        bool hasTransparencyAlpha = layerCount < 0;
        layerCount = Math.Abs(layerCount);

        if (layerCount == 0)
        {
            pos = layerInfoEnd;
            return layers;
        }

        int bytesPerChannel = depth / 8;

        // Read layer records
        var layerRecords = new LayerRecord[layerCount];
        for (int i = 0; i < layerCount; i++)
        {
            layerRecords[i] = ReadLayerRecord(data, ref pos, version);
        }

        // Read layer channel image data
        for (int i = 0; i < layerCount; i++)
        {
            ref var record = ref layerRecords[i];
            int layerW = record.Right - record.Left;
            int layerH = record.Bottom - record.Top;

            ImageFrame? layerImage = null;

            if (layerW > 0 && layerH > 0)
            {
                bool layerHasAlpha = false;
                int rgbChannels = colorMode == ModeGrayscale ? 1 : 3;

                // Check if layer has an alpha channel (channel ID = -1)
                for (int c = 0; c < record.ChannelCount; c++)
                {
                    if (record.ChannelIds[c] == -1)
                    {
                        layerHasAlpha = true;
                        break;
                    }
                }

                layerImage = new ImageFrame();
                layerImage.Initialize(layerW, layerH, ColorspaceType.SRGB, layerHasAlpha);
                int frameChannels = layerImage.NumberOfChannels;

                // Decode each channel's data
                var channelBuffers = new byte[record.ChannelCount][];
                for (int c = 0; c < record.ChannelCount; c++)
                {
                    int channelDataSize = record.ChannelDataSizes[c];
                    if (channelDataSize < 2)
                    {
                        channelBuffers[c] = new byte[layerW * layerH * bytesPerChannel];
                        pos += channelDataSize;
                        continue;
                    }

                    // Read compression type for this channel
                    ushort channelCompression = ReadU16BE(data, ref pos);
                    int compressedDataSize = channelDataSize - 2;

                    channelBuffers[c] = DecodeChannelData(data, ref pos, channelCompression,
                        compressedDataSize, layerW, layerH, bytesPerChannel, version);
                }

                // Assemble channel data into pixels
                AssembleLayerPixels(layerImage, channelBuffers, record.ChannelIds,
                    record.ChannelCount, layerW, layerH, bytesPerChannel, depth,
                    colorMode, frameChannels);
            }
            else
            {
                // Empty layer — skip channel data
                for (int c = 0; c < record.ChannelCount; c++)
                {
                    pos += record.ChannelDataSizes[c];
                }
            }

            layers.Add(new PsdLayerInfo
            {
                Name = record.Name,
                Top = record.Top,
                Left = record.Left,
                Bottom = record.Bottom,
                Right = record.Right,
                Opacity = record.Opacity,
                Visible = (record.Flags & 0x02) == 0, // bit 1 = hidden
                BlendMode = record.BlendMode,
                Image = layerImage
            });
        }

        pos = Math.Max(pos, layerInfoEnd);
        return layers;
    }

    private struct LayerRecord
    {
        public int Top, Left, Bottom, Right;
        public int ChannelCount;
        public short[] ChannelIds;
        public int[] ChannelDataSizes;
        public string BlendMode;
        public byte Opacity;
        public byte Flags;
        public string Name;
    }

    private static LayerRecord ReadLayerRecord(ReadOnlySpan<byte> data, ref int pos, ushort version)
    {
        var record = new LayerRecord();

        // Bounds: top, left, bottom, right (signed 32-bit)
        record.Top = (int)ReadU32BE(data, ref pos);
        record.Left = (int)ReadU32BE(data, ref pos);
        record.Bottom = (int)ReadU32BE(data, ref pos);
        record.Right = (int)ReadU32BE(data, ref pos);

        // Channel count
        record.ChannelCount = ReadU16BE(data, ref pos);
        record.ChannelIds = new short[record.ChannelCount];
        record.ChannelDataSizes = new int[record.ChannelCount];

        // Channel info: ID (signed 16-bit) + data size
        for (int c = 0; c < record.ChannelCount; c++)
        {
            record.ChannelIds[c] = (short)ReadU16BE(data, ref pos);
            record.ChannelDataSizes[c] = version == 1
                ? (int)ReadU32BE(data, ref pos)
                : (int)ReadU32BE(data, ref pos); // PSB uses 8 bytes but we handle v1
        }

        // Blend mode signature "8BIM"
        pos += 4;

        // Blend mode key (4 bytes)
        record.BlendMode = Encoding.ASCII.GetString(data.Slice(pos, 4));
        pos += 4;

        // Opacity
        record.Opacity = data[pos++];

        // Clipping
        pos++; // skip

        // Flags
        record.Flags = data[pos++];

        // Filler
        pos++;

        // Extra data length
        uint extraDataLength = ReadU32BE(data, ref pos);
        int extraEnd = pos + (int)extraDataLength;

        // Parse extra data: mask info, blending ranges, layer name
        // Layer mask data
        uint maskSize = ReadU32BE(data, ref pos);
        pos += (int)maskSize;

        // Blending ranges
        uint blendingRangesSize = ReadU32BE(data, ref pos);
        pos += (int)blendingRangesSize;

        // Layer name (Pascal string, padded to 4-byte boundary)
        if (pos < extraEnd)
        {
            byte nameLen = data[pos++];
            int paddedNameLen = ((nameLen + 1 + 3) & ~3) - 1; // pad to 4-byte boundary
            if (nameLen > 0 && pos + nameLen <= data.Length)
            {
                record.Name = Encoding.ASCII.GetString(data.Slice(pos, Math.Min((int)nameLen, 255)));
            }
            else
            {
                record.Name = "";
            }
            pos += paddedNameLen;
        }
        else
        {
            record.Name = "";
        }

        // Skip remaining extra data (additional layer info: luni, etc.)
        pos = extraEnd;

        return record;
    }

    private static byte[] DecodeChannelData(ReadOnlySpan<byte> data, ref int pos,
        ushort compression, int compressedDataSize, int width, int height,
        int bytesPerChannel, ushort version)
    {
        int channelBytes = width * height * bytesPerChannel;
        var buffer = new byte[channelBytes];

        if (compression == CompressionRaw)
        {
            int toCopy = Math.Min(compressedDataSize, channelBytes);
            if (pos + toCopy <= data.Length)
                data.Slice(pos, toCopy).CopyTo(buffer);
            pos += compressedDataSize;
        }
        else if (compression == CompressionRle)
        {
            // RLE: row byte counts first, then compressed data
            int rowCountSize = version == 1 ? 2 : 4;
            var rowByteCounts = new int[height];

            for (int y = 0; y < height; y++)
            {
                if (version == 1)
                    rowByteCounts[y] = ReadU16BE(data, ref pos);
                else
                    rowByteCounts[y] = (int)ReadU32BE(data, ref pos);
            }

            int rowBytes = width * bytesPerChannel;
            for (int y = 0; y < height; y++)
            {
                int compSize = rowByteCounts[y];
                if (pos + compSize <= data.Length)
                {
                    int destOffset = y * rowBytes;
                    DecompressPackBits(data.Slice(pos, compSize),
                        buffer.AsSpan(destOffset, Math.Min(rowBytes, buffer.Length - destOffset)));
                }
                pos += compSize;
            }
        }
        else
        {
            // Unknown compression — skip
            pos += compressedDataSize;
        }

        return buffer;
    }

    private static void AssembleLayerPixels(ImageFrame image, byte[][] channelBuffers,
        short[] channelIds, int channelCount, int width, int height,
        int bytesPerChannel, ushort depth, ushort colorMode, int frameChannels)
    {
        // Map channel IDs to buffer indices:
        // -1 = alpha, 0 = R (or Gray), 1 = G, 2 = B
        int? redIdx = null, greenIdx = null, blueIdx = null, alphaIdx = null;

        for (int c = 0; c < channelCount; c++)
        {
            switch (channelIds[c])
            {
                case -1: alphaIdx = c; break;
                case 0: redIdx = c; break;
                case 1: greenIdx = c; break;
                case 2: blueIdx = c; break;
            }
        }

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                int srcIdx = (y * width + x) * bytesPerChannel;
                int dstIdx = x * frameChannels;

                if (colorMode == ModeGrayscale)
                {
                    ushort gray = redIdx.HasValue
                        ? ReadChannelValue(channelBuffers[redIdx.Value], srcIdx, depth)
                        : (ushort)0;
                    row[dstIdx] = gray;
                    if (frameChannels > 1) row[dstIdx + 1] = gray;
                    if (frameChannels > 2) row[dstIdx + 2] = gray;
                }
                else
                {
                    row[dstIdx] = redIdx.HasValue
                        ? ReadChannelValue(channelBuffers[redIdx.Value], srcIdx, depth)
                        : (ushort)0;
                    if (frameChannels > 1)
                        row[dstIdx + 1] = greenIdx.HasValue
                            ? ReadChannelValue(channelBuffers[greenIdx.Value], srcIdx, depth)
                            : (ushort)0;
                    if (frameChannels > 2)
                        row[dstIdx + 2] = blueIdx.HasValue
                            ? ReadChannelValue(channelBuffers[blueIdx.Value], srcIdx, depth)
                            : (ushort)0;
                }

                // Alpha
                if (image.HasAlpha)
                {
                    row[dstIdx + frameChannels - 1] = alphaIdx.HasValue
                        ? ReadChannelValue(channelBuffers[alphaIdx.Value], srcIdx, depth)
                        : Quantum.MaxValue;
                }
            }
        }
    }

    /// <summary>
    /// Helper: decode the composite image data section (shared by Decode and DecodeWithLayers).
    /// </summary>
    private static ImageFrame DecodeCompositeImage(ReadOnlySpan<byte> data, ref int pos,
        ushort version, ushort channelCount, int w, int h, ushort depth, ushort colorMode)
    {
        ushort compression = ReadU16BE(data, ref pos);

        bool hasAlpha = channelCount >= 4 && colorMode == ModeRgb;
        int imageChannels = colorMode == ModeGrayscale ? 1 : 3;
        int totalChannels = hasAlpha ? imageChannels + 1 : imageChannels;
        int readChannels = Math.Min(channelCount, totalChannels);
        int bytesPerChannel = depth / 8;

        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, hasAlpha);
        int frameChannels = frame.NumberOfChannels;

        int channelBufSize = w * h * bytesPerChannel;
        var channelData = new byte[readChannels][];
        for (int c = 0; c < readChannels; c++)
            channelData[c] = ArrayPool<byte>.Shared.Rent(channelBufSize);

        try
        {
        if (compression == CompressionRaw)
        {
            for (int c = 0; c < readChannels; c++)
            {
                int bytesToRead = w * h * bytesPerChannel;
                if (pos + bytesToRead > data.Length)
                    throw new InvalidDataException("Unexpected end of PSD data");
                data.Slice(pos, bytesToRead).CopyTo(channelData[c]);
                pos += bytesToRead;
            }
            for (int c = readChannels; c < channelCount; c++)
                pos += w * h * bytesPerChannel;
        }
        else if (compression == CompressionRle)
        {
            int totalRows = channelCount * h;
            var rowByteCounts = new int[totalRows];
            for (int r = 0; r < totalRows; r++)
            {
                rowByteCounts[r] = version == 1
                    ? ReadU16BE(data, ref pos)
                    : (int)ReadU32BE(data, ref pos);
            }

            for (int c = 0; c < channelCount; c++)
            {
                for (int y = 0; y < h; y++)
                {
                    int rowIdx = c * h + y;
                    int compressedSize = rowByteCounts[rowIdx];
                    if (c < readChannels)
                    {
                        int destOffset = y * w * bytesPerChannel;
                        DecompressPackBits(data.Slice(pos, compressedSize),
                            channelData[c].AsSpan(destOffset, w * bytesPerChannel));
                    }
                    pos += compressedSize;
                }
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported PSD compression: {compression}");
        }

        // Convert planar to interleaved
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * bytesPerChannel;
                int dstIdx = x * frameChannels;

                if (colorMode == ModeGrayscale)
                {
                    ushort gray = ReadChannelValue(channelData[0], srcIdx, depth);
                    row[dstIdx] = gray;
                    if (frameChannels > 1) row[dstIdx + 1] = gray;
                    if (frameChannels > 2) row[dstIdx + 2] = gray;
                }
                else
                {
                    row[dstIdx] = ReadChannelValue(channelData[0], srcIdx, depth);
                    if (frameChannels > 1 && readChannels > 1)
                        row[dstIdx + 1] = ReadChannelValue(channelData[1], srcIdx, depth);
                    if (frameChannels > 2 && readChannels > 2)
                        row[dstIdx + 2] = ReadChannelValue(channelData[2], srcIdx, depth);
                }

                if (hasAlpha && readChannels > imageChannels)
                    row[dstIdx + frameChannels - 1] = ReadChannelValue(
                        channelData[imageChannels], srcIdx, depth);
                else if (hasAlpha)
                    row[dstIdx + frameChannels - 1] = Quantum.MaxValue;
            }
        }

        return frame;
        }
        finally
        {
            for (int c = 0; c < readChannels; c++)
                ArrayPool<byte>.Shared.Return(channelData[c]);
        }
    }
    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int frameChannels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        // Determine PSD channel count and color mode
        int psdChannels = hasAlpha ? 4 : 3;
        ushort colorMode = ModeRgb;

        using var ms = new MemoryStream();

        // Header (26 bytes)
        ms.Write(Signature);
        WriteU16BE(ms, PsdVersion);
        ms.Write(new byte[6]); // reserved
        WriteU16BE(ms, (ushort)psdChannels);
        WriteU32BE(ms, (uint)h);
        WriteU32BE(ms, (uint)w);
        WriteU16BE(ms, 16); // 16-bit depth (our quantum is 16-bit)
        WriteU16BE(ms, colorMode);

        // Color mode data (empty for RGB)
        WriteU32BE(ms, 0);

        // Image resources (empty)
        WriteU32BE(ms, 0);

        // Layer and mask info (empty for single-layer composite)
        WriteU32BE(ms, 0);

        // Composite image data
        WriteU16BE(ms, CompressionRle);

        // Extract planar channel data
        int bytesPerSample = 2; // 16-bit
        int channelBufSizeEnc = w * h * bytesPerSample;
        var channels = new byte[psdChannels][];
        for (int c = 0;c < psdChannels;c++)
        {
            channels[c] = ArrayPool<byte>.Shared.Rent(channelBufSizeEnc);
        }

        try
        {

        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < w;x++)
            {
                int srcIdx = x * frameChannels;
                int dstIdx = (y * w + x) * bytesPerSample;

                // R, G, B channels
                for (int c = 0;c < 3 && c < psdChannels;c++)
                {
                    ushort val = c < frameChannels ? row[srcIdx + c] : (ushort)0;
                    BinaryPrimitives.WriteUInt16BigEndian(
                        channels[c].AsSpan(dstIdx, 2), val);
                }

                // Alpha channel
                if (hasAlpha && psdChannels > 3)
                {
                    ushort alpha = row[srcIdx + frameChannels - 1];
                    BinaryPrimitives.WriteUInt16BigEndian(
                        channels[3].AsSpan(dstIdx, 2), alpha);
                }
            }
        }

        // RLE compress each row of each channel
        int rowByteSize = w * bytesPerSample;
        var compressedRows = new byte[psdChannels * h][];
        var rowByteCounts = new ushort[psdChannels * h];

        var compBuffer = ArrayPool<byte>.Shared.Rent(rowByteSize * 2);
        try
        {
            for (int c = 0;c < psdChannels;c++)
            {
                for (int y = 0;y < h;y++)
                {
                    int rowStart = y * rowByteSize;
                    int compLen = CompressPackBits(
                        channels[c].AsSpan(rowStart, rowByteSize), compBuffer);

                    int idx = c * h + y;
                    compressedRows[idx] = new byte[compLen];
                    compBuffer.AsSpan(0, compLen).CopyTo(compressedRows[idx]);
                    rowByteCounts[idx] = (ushort)compLen;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compBuffer);
        }

        // Write row byte counts
        for (int i = 0;i < rowByteCounts.Length;i++)
        {
            WriteU16BE(ms, rowByteCounts[i]);
        }

        // Write compressed data
        for (int i = 0;i < compressedRows.Length;i++)
        {
            ms.Write(compressedRows[i]);
        }

        return ms.ToArray();
        }
        finally
        {
            for (int c = 0; c < psdChannels; c++)
                ArrayPool<byte>.Shared.Return(channels[c]);
        }
    }

    #region PackBits compression/decompression

    /// <summary>
    /// Decompress PackBits RLE data.
    /// </summary>
    private static void DecompressPackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int srcPos = 0, dstPos = 0;

        while (srcPos < src.Length && dstPos < dst.Length)
        {
            sbyte header = (sbyte)src[srcPos++];

            if (header >= 0)
            {
                // Literal run: copy (header + 1) bytes
                int count = header + 1;
                int toCopy = Math.Min(count, dst.Length - dstPos);
                toCopy = Math.Min(toCopy, src.Length - srcPos);
                src.Slice(srcPos, toCopy).CopyTo(dst.Slice(dstPos, toCopy));
                srcPos += toCopy;
                dstPos += toCopy;
            }
            else if (header != -128)
            {
                // Packed run: repeat next byte (1 - header) times
                int count = 1 - header;
                if (srcPos >= src.Length)
                {
                    break;
                }

                byte val = src[srcPos++];
                int toFill = Math.Min(count, dst.Length - dstPos);
                dst.Slice(dstPos, toFill).Fill(val);
                dstPos += toFill;
            }
            // header == -128 (0x80): no-op
        }
    }

    /// <summary>
    /// Compress data using PackBits RLE. Returns compressed length.
    /// </summary>
    private static int CompressPackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int srcPos = 0, dstPos = 0;
        int srcLen = src.Length;

        while (srcPos < srcLen)
        {
            // Look for a run of identical bytes
            int runStart = srcPos;
            byte val = src[srcPos];
            while (srcPos < srcLen && srcPos - runStart < 128 && src[srcPos] == val)
            {
                srcPos++;
            }

            int runLen = srcPos - runStart;

            if (runLen >= 3)
            {
                // Packed run
                dst[dstPos++] = (byte)(1 - runLen); // -(runLen - 1) as sbyte
                dst[dstPos++] = val;
            }
            else
            {
                // Literal — collect non-run bytes
                srcPos = runStart;
                int litStart = srcPos;

                while (srcPos < srcLen && srcPos - litStart < 128)
                {
                    // Check if a run of 3+ starts here
                    if (srcPos + 2 < srcLen &&
                        src[srcPos] == src[srcPos + 1] &&
                        src[srcPos] == src[srcPos + 2])
                    {
                        break;
                    }

                    srcPos++;
                }

                int litLen = srcPos - litStart;
                if (litLen == 0)
                {
                    litLen = 1;
                    srcPos++;
                }
                dst[dstPos++] = (byte)(litLen - 1);
                src.Slice(litStart, litLen).CopyTo(dst.Slice(dstPos, litLen));
                dstPos += litLen;
            }
        }

        return dstPos;
    }

    #endregion

    #region Binary helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadU16BE(ReadOnlySpan<byte> data, ref int pos)
    {
        ushort val = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 2;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32BE(ReadOnlySpan<byte> data, ref int pos)
    {
        uint val = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
        pos += 4;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadChannelValue(byte[] data, int offset, int depth)
    {
        if (depth == 16)
        {
            ushort val = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            return val;
        }
        else // 8-bit
        {
            return Quantum.ScaleFromByte(data[offset]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU16BE(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        s.Write(buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32BE(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    #endregion
}
