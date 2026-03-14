using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// Pure C# TIFF reader/writer (baseline TIFF 6.0 + BigTIFF).
/// Supports: strip-based and tile-based layout, big/little endian, multi-page,
/// RGB/Grayscale/RGBA/Palette, 8/16-bit, uncompressed + Deflate + LZW + PackBits + JPEG compression.
/// </summary>
public static class TiffCoder
{
    /// <summary>
    /// Detect TIFF format by byte order mark + magic 42.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        bool littleEndian = data[0] == 'I' && data[1] == 'I';
        bool bigEndian = data[0] == 'M' && data[1] == 'M';
        if (!littleEndian && !bigEndian)
        {
            return false;
        }

        ushort magic = littleEndian
            ? (ushort)(data[2] | (data[3] << 8))
            : (ushort)((data[2] << 8) | data[3]);
        return magic == 42 || magic == 43; // 42 = TIFF, 43 = BigTIFF
    }

    // TIFF Tag IDs
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagXResolution = 282;
    private const ushort TagYResolution = 283;
    private const ushort TagPlanarConfig = 284;
    private const ushort TagResolutionUnit = 296;
    private const ushort TagColorMap = 320;
    private const ushort TagExtraSamples = 338;
    private const ushort TagSampleFormat = 339;

    // Tile tags
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;

    // JPEG-in-TIFF
    private const int CompressionJpeg = 7;

    // TIFF Data Types
    private const ushort TypeByte = 1;
    private const ushort TypeAscii = 2;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;
    private const ushort TypeRational = 5;
    private const ushort TypeSbyte = 6;
    private const ushort TypeUndefined = 7;
    private const ushort TypeSshort = 8;
    private const ushort TypeSlong = 9;
    private const ushort TypeSrational = 10;
    private const ushort TypeFloat = 11;
    private const ushort TypeDouble = 12;
    private const ushort TypeLong8 = 16;

    // Compression values
    private const int CompressionNone = 1;
    private const int CompressionCcittRle = 2;
    private const int CompressionCcittGroup3 = 3;
    private const int CompressionCcittGroup4 = 4;
    private const int CompressionLzw = 5;
    private const int CompressionDeflate = 8;
    private const int CompressionPackBits = 32773;
    private const int CompressionAdobeDeflate = 32946;

    // CCITT-related tags
    private const ushort TagT4Options = 292;
    private const ushort TagT6Options = 293;

    // Photometric values
    private const int PhotometricMinIsWhite = 0;
    private const int PhotometricMinIsBlack = 1;
    private const int PhotometricRgb = 2;
    private const int PhotometricPalette = 3;

    #region Read

    public static ImageFrame Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return Read(stream);
    }

    /// <summary>
    /// Reads the first IFD (page) of a TIFF file.
    /// </summary>
    public static ImageFrame Read(Stream stream)
    {
        byte[] fileData = ReadAllBytes(stream);
        var pages = ReadAllPages(fileData);
        return pages[0];
    }

    /// <summary>
    /// Read all pages (IFDs) from a multi-page TIFF file.
    /// </summary>
    public static List<ImageFrame> ReadMultiPage(Stream stream)
    {
        byte[] fileData = ReadAllBytes(stream);
        return ReadAllPages(fileData);
    }

    /// <summary>
    /// Read all pages from a multi-page TIFF file.
    /// </summary>
    public static List<ImageFrame> ReadMultiPage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return ReadMultiPage(stream);
    }

    private static List<ImageFrame> ReadAllPages(byte[] fileData, int maxPages = int.MaxValue)
    {
        if (fileData.Length < 8)
            throw new InvalidDataException("TIFF file too small.");

        bool bigEndian;
        if (fileData[0] == 0x49 && fileData[1] == 0x49)
            bigEndian = false;
        else if (fileData[0] == 0x4D && fileData[1] == 0x4D)
            bigEndian = true;
        else
            throw new InvalidDataException("Not a valid TIFF file.");

        int magic = ReadUInt16(fileData, 2, bigEndian);
        bool isBigTiff = magic == 43;
        if (magic != 42 && !isBigTiff)
            throw new InvalidDataException($"Invalid TIFF magic number: {magic}");

        long ifdOffset;
        if (isBigTiff)
        {
            // BigTIFF: bytes 4-5 = offset size (must be 8), bytes 6-7 = reserved (0)
            int offsetSize = ReadUInt16(fileData, 4, bigEndian);
            if (offsetSize != 8)
                throw new InvalidDataException($"Invalid BigTIFF offset size: {offsetSize}");
            ifdOffset = ReadInt64(fileData, 8, bigEndian);
        }
        else
        {
            ifdOffset = ReadUInt32(fileData, 4, bigEndian);
        }

        var pages = new List<ImageFrame>();
        while (ifdOffset > 0 && ifdOffset < fileData.Length && pages.Count < maxPages)
        {
            var (tags, nextIfdOffset) = ParseIfd(fileData, ifdOffset, bigEndian, isBigTiff);
            pages.Add(DecodeIfdToFrame(fileData, tags, bigEndian));
            ifdOffset = nextIfdOffset;
        }

        if (pages.Count == 0)
            throw new InvalidDataException("TIFF file contains no valid IFDs.");

        return pages;
    }

    private static ImageFrame DecodeIfdToFrame(byte[] fileData, Dictionary<ushort, TiffTag> tags, bool bigEndian)
    {
        int width = GetTagValueInt(tags, TagImageWidth);
        int height = GetTagValueInt(tags, TagImageLength);
        int bitsPerSample = GetTagFirstValueInt(tags, TagBitsPerSample, 8);
        int samplesPerPixel = GetTagValueInt(tags, TagSamplesPerPixel, 1);
        int compression = GetTagValueInt(tags, TagCompression, CompressionNone);
        int photometric = GetTagValueInt(tags, TagPhotometric, PhotometricRgb);
        int planarConfig = GetTagValueInt(tags, TagPlanarConfig, 1);
        int t4Options = GetTagValueInt(tags, TagT4Options, 0);

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Invalid TIFF dimensions.");
        if (planarConfig != 1)
            throw new NotSupportedException("Planar TIFF configuration not supported (only chunky/interleaved).");

        // Color map for palette images
        int[]? colorMap = null;
        if (photometric == PhotometricPalette && tags.TryGetValue(TagColorMap, out var cmTag))
            colorMap = cmTag.Values;

        bool hasAlpha = samplesPerPixel > 3 || (samplesPerPixel > 1 && photometric <= PhotometricMinIsBlack);
        bool isGrayscale = photometric == PhotometricMinIsBlack || photometric == PhotometricMinIsWhite;
        bool invertGray = photometric == PhotometricMinIsWhite;
        bool isPalette = photometric == PhotometricPalette;
        if (isGrayscale && samplesPerPixel >= 2) hasAlpha = true;

        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB,
            hasAlpha && !isGrayscale && samplesPerPixel > 3);
        int channels = image.NumberOfChannels;

        // Check for tile-based layout
        int tileWidth = GetTagValueInt(tags, TagTileWidth, 0);
        int tileLength = GetTagValueInt(tags, TagTileLength, 0);

        if (tileWidth > 0 && tileLength > 0)
        {
            // Tile-based TIFF
            int[] tileOffsets = GetTagValueArray(tags, TagTileOffsets);
            int[] tileByteCounts = GetTagValueArray(tags, TagTileByteCounts);
            if (tileOffsets.Length == 0)
                throw new InvalidDataException("TIFF has tile dimensions but no tile offsets.");

            int tilesAcross = (width + tileWidth - 1) / tileWidth;
            int tilesDown = (height + tileLength - 1) / tileLength;
            int tileIndex = 0;

            for (int ty = 0; ty < tilesDown; ty++)
            {
                for (int tx = 0; tx < tilesAcross; tx++)
                {
                    if (tileIndex >= tileOffsets.Length) break;
                    int offset = tileOffsets[tileIndex];
                    int byteCount = tileIndex < tileByteCounts.Length ? tileByteCounts[tileIndex] : 0;

                    // Decompress tile (tile always covers full tileWidth × tileLength, even at edges)
                    byte[] rawTile = DecompressStrip(fileData, offset, byteCount, compression,
                        tileWidth, tileLength, samplesPerPixel, bitsPerSample, t4Options);

                    // Copy tile data into the image, clipping at edges
                    int startX = tx * tileWidth;
                    int startY = ty * tileLength;
                    int effectiveW = Math.Min(tileWidth, width - startX);
                    int effectiveH = Math.Min(tileLength, height - startY);

                    ConvertTileToPixels(image, rawTile, startX, startY, effectiveW, effectiveH,
                        tileWidth, bitsPerSample, samplesPerPixel, photometric,
                        isGrayscale, invertGray, isPalette, colorMap, channels);

                    tileIndex++;
                }
            }
        }
        else
        {
            // Strip-based TIFF
            int rowsPerStrip = GetTagValueInt(tags, TagRowsPerStrip, height);
            int[] stripOffsets = GetTagValueArray(tags, TagStripOffsets);
            int[] stripByteCounts = GetTagValueArray(tags, TagStripByteCounts);
            if (stripOffsets.Length == 0)
                throw new InvalidDataException("TIFF has no strip offsets.");

            int stripIndex = 0;
            int currentRow = 0;
            while (currentRow < height && stripIndex < stripOffsets.Length)
            {
                int stripRows = Math.Min(rowsPerStrip, height - currentRow);
                int offset = stripOffsets[stripIndex];
                int byteCount = stripIndex < stripByteCounts.Length ? stripByteCounts[stripIndex] : 0;

                byte[] rawStrip = DecompressStrip(fileData, offset, byteCount, compression,
                    width, stripRows, samplesPerPixel, bitsPerSample, t4Options);

                ConvertStripToPixels(image, rawStrip, currentRow, stripRows, width,
                    bitsPerSample, samplesPerPixel, photometric, isGrayscale,
                    invertGray, isPalette, colorMap, channels);

                currentRow += stripRows;
                stripIndex++;
            }
        }

        return image;
    }

    #endregion

    #region Write

    public static void Write(ImageFrame image, string path,
        TiffCompression compression = TiffCompression.Deflate)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        Write(image, stream, compression);
    }

    /// <summary>
    /// Writes a TIFF file (little-endian, strip-based).
    /// </summary>
    public static void Write(ImageFrame image, Stream stream,
        TiffCompression compression = TiffCompression.Deflate)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int bitsPerSample = 16;
        int bytesPerSample = 2;
        int rowsPerStrip = Math.Max(1, 8192 / (width * channels * bytesPerSample));
        rowsPerStrip = Math.Min(rowsPerStrip, height);

        int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;
        byte[][] compressedStrips = new byte[stripCount][];
        int stripIdx = 0;

        for (int startRow = 0; startRow < height; startRow += rowsPerStrip)
        {
            int rows = Math.Min(rowsPerStrip, height - startRow);
            int rawSize = rows * width * channels * bytesPerSample;
            byte[] rawData = new byte[rawSize];
            int pos = 0;
            for (int y = startRow; y < startRow + rows; y++)
            {
                var row = image.GetPixelRow(y);
                for (int x = 0; x < width * channels; x++)
                {
                    ushort val = row[x];
                    rawData[pos++] = (byte)(val & 0xFF);
                    rawData[pos++] = (byte)(val >> 8);
                }
            }
            compressedStrips[stripIdx++] = CompressStrip(rawData, compression);
        }

        int dataOffset = 8;
        int[] stripOffsets = new int[stripCount];
        int[] stripByteCounts = new int[stripCount];
        for (int i = 0; i < stripCount; i++)
        {
            stripOffsets[i] = dataOffset;
            stripByteCounts[i] = compressedStrips[i].Length;
            dataOffset += compressedStrips[i].Length;
        }
        if (dataOffset % 2 != 0) dataOffset++;
        int ifdOffset = dataOffset;

        var tagList = BuildCommonTags(width, height, channels, hasAlpha, bitsPerSample, compression);
        tagList.Add(MakeTagArray(TagStripOffsets, TypeLong, stripOffsets));
        tagList.Add(MakeTag(TagRowsPerStrip, TypeLong, rowsPerStrip));
        tagList.Add(MakeTagArray(TagStripByteCounts, TypeLong, stripByteCounts));
        tagList.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0x49);
        writer.Write((byte)0x49);
        writer.Write((ushort)42);
        writer.Write((uint)ifdOffset);

        for (int i = 0; i < stripCount; i++)
            writer.Write(compressedStrips[i]);
        if ((int)stream.Position % 2 != 0)
            writer.Write((byte)0);

        WriteIfd(writer, tagList, (int)stream.Position);
    }

    /// <summary>
    /// Writes a tiled TIFF file (little-endian). Tile size defaults to 256×256.
    /// </summary>
    public static void WriteTiled(ImageFrame image, Stream stream,
        TiffCompression compression = TiffCompression.Deflate, int tileSize = 256)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        int bitsPerSample = 16;
        int bytesPerSample = 2;
        int tileWidth = tileSize;
        int tileLength = tileSize;
        int tilesAcross = (width + tileWidth - 1) / tileWidth;
        int tilesDown = (height + tileLength - 1) / tileLength;
        int tileCount = tilesAcross * tilesDown;

        byte[][] compressedTiles = new byte[tileCount][];
        int tileIdx = 0;

        for (int ty = 0; ty < tilesDown; ty++)
        {
            for (int tx = 0; tx < tilesAcross; tx++)
            {
                // Always write full tile (pad with zeros at edges)
                int rawSize = tileWidth * tileLength * channels * bytesPerSample;
                byte[] rawData = new byte[rawSize];
                int pos = 0;

                for (int py = 0; py < tileLength; py++)
                {
                    int sy = Math.Min(ty * tileLength + py, height - 1);
                    var row = image.GetPixelRow(sy);
                    for (int px = 0; px < tileWidth; px++)
                    {
                        int sx = Math.Min(tx * tileWidth + px, width - 1);
                        for (int c = 0; c < channels; c++)
                        {
                            ushort val = row[sx * channels + c];
                            rawData[pos++] = (byte)(val & 0xFF);
                            rawData[pos++] = (byte)(val >> 8);
                        }
                    }
                }

                compressedTiles[tileIdx++] = CompressStrip(rawData, compression);
            }
        }

        // Layout: header(8) + tile data + IFD
        int dataOffset = 8;
        int[] tileOffsets = new int[tileCount];
        int[] tileByteCounts = new int[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            tileOffsets[i] = dataOffset;
            tileByteCounts[i] = compressedTiles[i].Length;
            dataOffset += compressedTiles[i].Length;
        }
        if (dataOffset % 2 != 0) dataOffset++;
        int ifdOffset = dataOffset;

        var tagList = BuildCommonTags(width, height, channels, hasAlpha, bitsPerSample, compression);
        tagList.Add(MakeTag(TagTileWidth, TypeLong, tileWidth));
        tagList.Add(MakeTag(TagTileLength, TypeLong, tileLength));
        tagList.Add(MakeTagArray(TagTileOffsets, TypeLong, tileOffsets));
        tagList.Add(MakeTagArray(TagTileByteCounts, TypeLong, tileByteCounts));
        tagList.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0x49);
        writer.Write((byte)0x49);
        writer.Write((ushort)42);
        writer.Write((uint)ifdOffset);

        for (int i = 0; i < tileCount; i++)
            writer.Write(compressedTiles[i]);
        if ((int)stream.Position % 2 != 0)
            writer.Write((byte)0);

        WriteIfd(writer, tagList, (int)stream.Position);
    }

    /// <summary>
    /// Writes a multi-page TIFF file. Each ImageFrame is a separate page.
    /// </summary>
    public static void WriteMultiPage(IReadOnlyList<ImageFrame> pages, Stream stream,
        TiffCompression compression = TiffCompression.Deflate)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages to write.");

        // Pre-build all page data
        var pageDataList = new List<(byte[][] strips, int[] offsets, int[] byteCounts,
            List<TiffTagEntry> tags, int rowsPerStrip)>();

        foreach (var image in pages)
        {
            int width = (int)image.Columns;
            int height = (int)image.Rows;
            int channels = image.NumberOfChannels;
            bool hasAlpha = image.HasAlpha;
            int bitsPerSample = 16;
            int bytesPerSample = 2;
            int rowsPerStrip = Math.Max(1, 8192 / (width * channels * bytesPerSample));
            rowsPerStrip = Math.Min(rowsPerStrip, height);
            int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;

            byte[][] compressedStrips = new byte[stripCount][];
            int stripIdx = 0;
            for (int startRow = 0; startRow < height; startRow += rowsPerStrip)
            {
                int rows = Math.Min(rowsPerStrip, height - startRow);
                int rawSize = rows * width * channels * bytesPerSample;
                byte[] rawData = new byte[rawSize];
                int pos = 0;
                for (int y = startRow; y < startRow + rows; y++)
                {
                    var row = image.GetPixelRow(y);
                    for (int x = 0; x < width * channels; x++)
                    {
                        ushort val = row[x];
                        rawData[pos++] = (byte)(val & 0xFF);
                        rawData[pos++] = (byte)(val >> 8);
                    }
                }
                compressedStrips[stripIdx++] = CompressStrip(rawData, compression);
            }

            var tags = BuildCommonTags(width, height, channels, hasAlpha, bitsPerSample, compression);
            tags.Add(MakeTag(TagRowsPerStrip, TypeLong, rowsPerStrip));
            pageDataList.Add((compressedStrips, new int[stripCount], new int[stripCount], tags, rowsPerStrip));
        }

        // Calculate layout: header(8) + all strip data + IFDs
        int currentOffset = 8;
        for (int p = 0; p < pageDataList.Count; p++)
        {
            var pd = pageDataList[p];
            for (int i = 0; i < pd.strips.Length; i++)
            {
                pd.offsets[i] = currentOffset;
                pd.byteCounts[i] = pd.strips[i].Length;
                currentOffset += pd.strips[i].Length;
            }
        }

        // Write file
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0x49);
        writer.Write((byte)0x49);
        writer.Write((ushort)42);

        // Placeholder for first IFD offset
        long firstIfdOffsetPos = stream.Position;
        writer.Write((uint)0);

        // Write all strip data
        for (int p = 0; p < pageDataList.Count; p++)
        {
            var pd = pageDataList[p];
            for (int i = 0; i < pd.strips.Length; i++)
                writer.Write(pd.strips[i]);
        }

        // Write IFDs, chaining them together
        if ((int)stream.Position % 2 != 0) writer.Write((byte)0);

        int firstIfdOffset = (int)stream.Position;
        // Go back and write the first IFD offset
        long savedPos = stream.Position;
        stream.Position = firstIfdOffsetPos;
        writer.Write((uint)firstIfdOffset);
        stream.Position = savedPos;

        long prevNextIfdPos = -1;
        for (int p = 0; p < pageDataList.Count; p++)
        {
            var pd = pageDataList[p];
            pd.tags.Add(MakeTagArray(TagStripOffsets, TypeLong, pd.offsets));
            pd.tags.Add(MakeTagArray(TagStripByteCounts, TypeLong, pd.byteCounts));
            pd.tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            if ((int)stream.Position % 2 != 0) writer.Write((byte)0);

            // Patch previous IFD's "next" pointer to this IFD's position
            if (prevNextIfdPos >= 0)
            {
                int thisIfdStart = (int)stream.Position;
                long curPos = stream.Position;
                stream.Position = prevNextIfdPos;
                writer.Write((uint)thisIfdStart);
                stream.Position = curPos;
            }

            int ifdStart = (int)stream.Position;
            prevNextIfdPos = WriteIfd(writer, pd.tags, ifdStart, isLast: p == pageDataList.Count - 1);
        }
    }

    /// <summary>
    /// Writes a multi-page TIFF file to disk.
    /// </summary>
    public static void WriteMultiPage(IReadOnlyList<ImageFrame> pages, string path,
        TiffCompression compression = TiffCompression.Deflate)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        WriteMultiPage(pages, stream, compression);
    }

    private static List<TiffTagEntry> BuildCommonTags(int width, int height, int channels,
        bool hasAlpha, int bitsPerSample, TiffCompression compression)
    {
        int compValue = compression switch
        {
            TiffCompression.None => CompressionNone,
            TiffCompression.Deflate => CompressionDeflate,
            TiffCompression.Lzw => CompressionLzw,
            TiffCompression.PackBits => CompressionPackBits,
            _ => CompressionNone
        };

        var tagList = new List<TiffTagEntry>
        {
            MakeTag(TagImageWidth, TypeLong, width),
            MakeTag(TagImageLength, TypeLong, height),
            MakeTagArray(TagBitsPerSample, TypeShort,
                Enumerable.Repeat(bitsPerSample, channels).ToArray()),
            MakeTag(TagCompression, TypeShort, compValue),
            MakeTag(TagPhotometric, TypeShort, PhotometricRgb),
            MakeTag(TagSamplesPerPixel, TypeShort, channels),
            MakeTag(TagXResolution, TypeRational, 72, 1),
            MakeTag(TagYResolution, TypeRational, 72, 1),
            MakeTag(TagResolutionUnit, TypeShort, 2),
            MakeTag(TagPlanarConfig, TypeShort, 1),
            MakeTag(TagSampleFormat, TypeShort, 1),
        };

        if (hasAlpha)
            tagList.Add(MakeTag(TagExtraSamples, TypeShort, 2));

        return tagList;
    }

    #endregion

    #region IFD Parsing

    private static (Dictionary<ushort, TiffTag> tags, long nextIfdOffset) ParseIfd(
        byte[] data, long offset, bool bigEndian, bool isBigTiff = false)
    {
        var tags = new Dictionary<ushort, TiffTag>();
        if (offset + 2 > data.Length) return (tags, 0);

        int entryCount;
        int entrySize;
        long entriesStart;

        if (isBigTiff)
        {
            entryCount = (int)ReadInt64(data, offset, bigEndian);
            entrySize = 20; // BigTIFF IFD entry: tag(2) + type(2) + count(8) + value(8)
            entriesStart = offset + 8;
        }
        else
        {
            entryCount = ReadUInt16(data, (int)offset, bigEndian);
            entrySize = 12;
            entriesStart = offset + 2;
        }

        for (int i = 0; i < entryCount; i++)
        {
            long entryOffset = entriesStart + i * entrySize;
            if (entryOffset + entrySize > data.Length) break;

            ushort tagId = (ushort)ReadUInt16(data, (int)entryOffset, bigEndian);
            ushort type = (ushort)ReadUInt16(data, (int)entryOffset + 2, bigEndian);
            long count;
            long valueOffset;

            if (isBigTiff)
            {
                count = ReadInt64(data, entryOffset + 4, bigEndian);
                long valueSize = GetTypeSize(type) * count;
                valueOffset = valueSize <= 8
                    ? entryOffset + 12
                    : ReadInt64(data, entryOffset + 12, bigEndian);
            }
            else
            {
                count = (int)ReadUInt32(data, (int)entryOffset + 4, bigEndian);
                int valueSize = GetTypeSize(type) * (int)count;
                valueOffset = valueSize <= 4
                    ? entryOffset + 8
                    : (int)ReadUInt32(data, (int)entryOffset + 8, bigEndian);
            }

            int[] values = ReadTagValues(data, (int)valueOffset, type, (int)count, bigEndian);
            tags[tagId] = new TiffTag(tagId, type, (int)count, values);
        }

        // Next IFD offset
        long nextOffset;
        long afterEntries = entriesStart + (long)entryCount * entrySize;
        if (isBigTiff)
            nextOffset = afterEntries + 8 <= data.Length ? ReadInt64(data, afterEntries, bigEndian) : 0;
        else
            nextOffset = afterEntries + 4 <= data.Length ? ReadUInt32(data, (int)afterEntries, bigEndian) : 0;

        return (tags, nextOffset);
    }

    private static int[] ReadTagValues(byte[] data, int offset, ushort type, int count, bool bigEndian)
    {
        int[] values = new int[count];
        int typeSize = GetTypeSize(type);

        for (int i = 0;i < count;i++)
        {
            int pos = offset + i * typeSize;
            if (pos >= data.Length)
            {
                break;
            }

            values[i] = type switch
            {
                TypeByte or TypeUndefined => data[pos],
                TypeShort => ReadUInt16(data, pos, bigEndian),
                TypeLong => (int)ReadUInt32(data, pos, bigEndian),
                TypeSbyte => (sbyte)data[pos],
                TypeSshort => (short)ReadUInt16(data, pos, bigEndian),
                TypeSlong => (int)ReadUInt32(data, pos, bigEndian),
                TypeRational => (int)ReadUInt32(data, pos, bigEndian), // Numerator only
                _ => (int)ReadUInt32(data, pos, bigEndian)
            };
        }

        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetTypeSize(ushort type) => type switch
    {
        TypeByte or TypeAscii or TypeSbyte or TypeUndefined => 1,
        TypeShort or TypeSshort => 2,
        TypeLong or TypeSlong or TypeFloat => 4,
        TypeRational or TypeSrational or TypeDouble or TypeLong8 => 8,
        _ => 1
    };

    private static int GetTagValueInt(Dictionary<ushort, TiffTag> tags, ushort tagId, int defaultValue = 0)
    {
        if (tags.TryGetValue(tagId, out var tag) && tag.Values.Length > 0)
        {
            return tag.Values[0];
        }

        return defaultValue;
    }

    private static int GetTagFirstValueInt(Dictionary<ushort, TiffTag> tags, ushort tagId, int defaultValue = 0)
        => GetTagValueInt(tags, tagId, defaultValue);

    private static int[] GetTagValueArray(Dictionary<ushort, TiffTag> tags, ushort tagId)
    {
        if (tags.TryGetValue(tagId, out var tag))
        {
            return tag.Values;
        }

        return [];
    }

    #endregion

    #region Strip Decompression

    private static byte[] DecompressStrip(byte[] fileData, int offset, int byteCount,
        int compression, int width, int rows, int samplesPerPixel, int bitsPerSample,
        int t4Options = 0)
    {
        if (offset + byteCount > fileData.Length)
        {
            byteCount = Math.Max(0, fileData.Length - offset);
        }

        ReadOnlySpan<byte> compressed = fileData.AsSpan(offset, byteCount);

        return compression switch
        {
            CompressionNone => compressed.ToArray(),
            CompressionCcittRle => CcittFaxDecoder.DecodeModifiedHuffman(compressed, width, rows),
            CompressionCcittGroup3 => CcittFaxDecoder.DecodeGroup3(compressed, width, rows, t4Options),
            CompressionCcittGroup4 => CcittFaxDecoder.DecodeGroup4(compressed, width, rows),
            CompressionDeflate or CompressionAdobeDeflate => DeflateDecompress(compressed),
            CompressionLzw => TiffLzwDecompress(compressed),
            CompressionJpeg => JpegInTiffDecompress(compressed, width, rows, samplesPerPixel, bitsPerSample),
            CompressionPackBits => PackBitsDecompress(compressed,
                rows * width * samplesPerPixel * ((bitsPerSample + 7) / 8)),
            _ => throw new NotSupportedException($"TIFF compression type {compression} not supported.")
        };
    }

    private static byte[] DeflateDecompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var deflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// JPEG-in-TIFF: each strip/tile is a complete JPEG stream.
    /// Decode with JpegCoder, then extract raw pixel bytes.
    /// </summary>
    private static byte[] JpegInTiffDecompress(ReadOnlySpan<byte> jpegData,
        int width, int rows, int samplesPerPixel, int bitsPerSample)
    {
        using var stream = new MemoryStream(jpegData.ToArray());
        var frame = JpegCoder.Read(stream);

        int bytesPerSample = (bitsPerSample + 7) / 8;
        int rowBytes = width * samplesPerPixel * bytesPerSample;
        byte[] result = new byte[rows * rowBytes];
        int frameChannels = frame.NumberOfChannels;

        for (int y = 0; y < Math.Min(rows, (int)frame.Rows); y++)
        {
            var pixelRow = frame.GetPixelRow(y);
            int pos = y * rowBytes;
            for (int x = 0; x < Math.Min(width, (int)frame.Columns); x++)
            {
                for (int c = 0; c < samplesPerPixel && c < frameChannels; c++)
                {
                    ushort val = pixelRow[x * frameChannels + c];
                    if (bytesPerSample == 1)
                        result[pos++] = Quantum.ScaleToByte(val);
                    else
                    {
                        result[pos++] = (byte)(val & 0xFF);
                        result[pos++] = (byte)(val >> 8);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// TIFF LZW decompression (MSB-first bit packing, unlike GIF's LSB-first).
    /// </summary>
    private static byte[] TiffLzwDecompress(ReadOnlySpan<byte> compressedSpan)
    {
        byte[] data = compressedSpan.ToArray();
        const int ClearCode = 256;
        const int EndCode = 257;
        const int MaxTableSize = 4096;
        const int MaxCodeSize = 12;

        int codeSize = 9;
        int nextCode = 258;

        // String table
        int[] prefix = new int[MaxTableSize];
        byte[] suffix = new byte[MaxTableSize];

        // Initialize single-byte entries
        for (int i = 0;i < 256;i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
        }

        var output = new List<byte>(data.Length * 2);
        byte[] decodeStack = new byte[MaxTableSize];

        // MSB-first bit reader for TIFF
        int bitPos = 0;
        int totalBits = data.Length * 8;

        int ReadBitsMsb(int count)
        {
            if (bitPos + count > totalBits)
            {
                return EndCode;
            }

            int result = 0;
            for (int i = 0;i < count;i++)
            {
                int byteIdx = (bitPos + i) / 8;
                int bitIdx = 7 - ((bitPos + i) % 8); // MSB first
                if ((data[byteIdx] & (1 << bitIdx)) != 0)
                {
                    result |= 1 << (count - 1 - i);
                }
            }
            bitPos += count;
            return result;
        }

        int prevCode = -1;
        byte firstByte = 0;

        while (bitPos < totalBits)
        {
            int code = ReadBitsMsb(codeSize);
            if (code == EndCode)
            {
                break;
            }

            if (code == ClearCode)
            {
                codeSize = 9;
                nextCode = 258;
                prevCode = -1;
                continue;
            }

            int currentCode = code;
            int stackIndex = 0;

            if (code > nextCode || code >= MaxTableSize)
            {
                break; // Invalid code
            }

            if (code == nextCode)
            {
                // Special case: code not yet in table
                decodeStack[stackIndex++] = firstByte;
                currentCode = prevCode;
            }

            // Unwind table
            while (currentCode >= 258)
            {
                if (stackIndex >= MaxTableSize)
                {
                    break;
                }

                decodeStack[stackIndex++] = suffix[currentCode];
                currentCode = prefix[currentCode];
            }
            if (currentCode < 0 || currentCode >= MaxTableSize)
            {
                break;
            }

            decodeStack[stackIndex++] = suffix[currentCode];
            firstByte = decodeStack[stackIndex - 1];

            // Output in reverse
            for (int i = stackIndex - 1;i >= 0;i--)
            {
                output.Add(decodeStack[i]);
            }

            // Add new entry
            if (prevCode >= 0 && nextCode < MaxTableSize)
            {
                prefix[nextCode] = prevCode;
                suffix[nextCode] = firstByte;
                nextCode++;

                // Decoder is 1 entry behind encoder — bump one earlier to stay in sync
                if (nextCode >= (1 << codeSize) - 1 && codeSize < MaxCodeSize)
                {
                    codeSize++;
                }
            }

            prevCode = code;
        }

        return output.ToArray();
    }

    /// <summary>
    /// PackBits (run-length) decompression.
    /// </summary>
    private static byte[] PackBitsDecompress(ReadOnlySpan<byte> data, int expectedSize)
    {
        var output = new List<byte>(expectedSize);
        int pos = 0;

        while (pos < data.Length && output.Count < expectedSize)
        {
            sbyte n = (sbyte)data[pos++];
            if (n >= 0)
            {
                // Copy n+1 literal bytes
                int count = n + 1;
                for (int i = 0;i < count && pos < data.Length;i++)
                {
                    output.Add(data[pos++]);
                }
            }
            else if (n != -128)
            {
                // Repeat next byte (1-n) times
                int count = 1 - n;
                if (pos < data.Length)
                {
                    byte val = data[pos++];
                    for (int i = 0;i < count;i++)
                    {
                        output.Add(val);
                    }
                }
            }
            // n == -128: no-op
        }

        return output.ToArray();
    }

    #endregion

    #region Strip Compression

    private static byte[] CompressStrip(byte[] data, TiffCompression compression)
    {
        return compression switch
        {
            TiffCompression.None => data,
            TiffCompression.Deflate => DeflateCompress(data),
            TiffCompression.Lzw => TiffLzwCompress(data),
            TiffCompression.PackBits => PackBitsCompress(data),
            _ => data
        };
    }

    private static byte[] DeflateCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>
    /// TIFF LZW compression (MSB-first bit packing).
    /// </summary>
    private static byte[] TiffLzwCompress(byte[] data)
    {
        const int ClearCode = 256;
        const int EndCode = 257;
        const int MaxTableSize = 4096;
        const int MaxCodeSize = 12;

        using var output = new MemoryStream();
        int codeSize = 9;
        int nextCode = 258;

        // MSB-first bit writer
        int bitBuffer = 0;
        int bitsInBuffer = 0;

        void WriteBitsMsb(int value, int count)
        {
            // Pack bits MSB-first
            bitBuffer = (bitBuffer << count) | (value & ((1 << count) - 1));
            bitsInBuffer += count;
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.WriteByte((byte)((bitBuffer >> bitsInBuffer) & 0xFF));
            }
        }

        void FlushBits()
        {
            if (bitsInBuffer > 0)
            {
                output.WriteByte((byte)((bitBuffer << (8 - bitsInBuffer)) & 0xFF));
                bitsInBuffer = 0;
                bitBuffer = 0;
            }
        }

        // Hash table
        int tableCapacity = MaxTableSize * 2;
        int[] hashPrefix = new int[tableCapacity];
        byte[] hashSuffix = new byte[tableCapacity];
        int[] hashCodeTable = new int[tableCapacity];

        void ResetTable()
        {
            Array.Fill(hashPrefix, -1);
            codeSize = 9;
            nextCode = 258;
        }

        ResetTable();
        WriteBitsMsb(ClearCode, codeSize);

        if (data.Length == 0)
        {
            WriteBitsMsb(EndCode, codeSize);
            FlushBits();
            return output.ToArray();
        }

        int currentPrefix = data[0];
        for (int i = 1;i < data.Length;i++)
        {
            byte currentSuffix = data[i];

            int hash = ((currentPrefix << 8) ^ currentSuffix) % tableCapacity;
            if (hash < 0)
            {
                hash += tableCapacity;
            }

            bool found = false;
            while (hashPrefix[hash] != -1)
            {
                if (hashPrefix[hash] == currentPrefix && hashSuffix[hash] == currentSuffix)
                {
                    currentPrefix = hashCodeTable[hash];
                    found = true;
                    break;
                }
                hash = (hash + 1) % tableCapacity;
            }

            if (!found)
            {
                WriteBitsMsb(currentPrefix, codeSize);

                if (nextCode < MaxTableSize)
                {
                    hashPrefix[hash] = currentPrefix;
                    hashSuffix[hash] = currentSuffix;
                    hashCodeTable[hash] = nextCode;
                    nextCode++;

                    if (nextCode >= (1 << codeSize) && codeSize < MaxCodeSize)
                    {
                        codeSize++;
                    }
                }
                else
                {
                    WriteBitsMsb(ClearCode, codeSize);
                    ResetTable();
                }

                currentPrefix = currentSuffix;
            }
        }

        WriteBitsMsb(currentPrefix, codeSize);
        WriteBitsMsb(EndCode, codeSize);
        FlushBits();

        return output.ToArray();
    }

    /// <summary>
    /// PackBits (run-length) compression.
    /// </summary>
    private static byte[] PackBitsCompress(byte[] data)
    {
        using var output = new MemoryStream();
        int pos = 0;

        while (pos < data.Length)
        {
            // Look for runs
            int runStart = pos;
            if (pos + 1 < data.Length && data[pos] == data[pos + 1])
            {
                // Run of identical bytes
                byte val = data[pos];
                int runLen = 1;
                while (pos + runLen < data.Length && data[pos + runLen] == val && runLen < 128)
                {
                    runLen++;
                }

                output.WriteByte((byte)(1 - runLen)); // -(runLen-1)
                output.WriteByte(val);
                pos += runLen;
            }
            else
            {
                // Literal run
                int litStart = pos;
                int litLen = 1;
                pos++;
                while (pos < data.Length && litLen < 128)
                {
                    if (pos + 1 < data.Length && data[pos] == data[pos + 1])
                    {
                        break;
                    }

                    litLen++;
                    pos++;
                }
                output.WriteByte((byte)(litLen - 1));
                output.Write(data, litStart, litLen);
            }
        }

        return output.ToArray();
    }

    #endregion

    #region Pixel Conversion

    private static void ConvertStripToPixels(ImageFrame image, byte[] rawStrip,
        int startRow, int stripRows, int width,
        int bitsPerSample, int samplesPerPixel, int photometric,
        bool isGrayscale, bool invertGray, bool isPalette,
        int[]? colorMap, int outputChannels)
    {
        int bytesPerSample = (bitsPerSample + 7) / 8;
        int pos = 0;

        for (int y = 0;y < stripRows;y++)
        {
            int outputY = startRow + y;
            if (outputY >= image.Rows)
            {
                break;
            }

            var row = image.GetPixelRowForWrite(outputY);

            for (int x = 0;x < width;x++)
            {
                int dstOffset = x * outputChannels;

                if (isPalette && colorMap != null)
                {
                    // Palette: index → RGB
                    int idx = ReadSampleValue(rawStrip, pos, bitsPerSample, bytesPerSample);
                    pos += bytesPerSample;

                    int paletteSize = colorMap.Length / 3;
                    if (idx < paletteSize)
                    {
                        // TIFF palette has 16-bit entries: R[0..n], G[0..n], B[0..n]
                        row[dstOffset] = (ushort)colorMap[idx];
                        row[dstOffset + 1] = (ushort)colorMap[paletteSize + idx];
                        row[dstOffset + 2] = (ushort)colorMap[2 * paletteSize + idx];
                    }
                }
                else if (isGrayscale)
                {
                    int gray = ReadSampleValue(rawStrip, pos, bitsPerSample, bytesPerSample);
                    pos += bytesPerSample;

                    ushort val = ScaleToQuantum(gray, bitsPerSample);
                    if (invertGray)
                    {
                        val = (ushort)(Quantum.MaxValue - val);
                    }

                    row[dstOffset] = val;
                    row[dstOffset + 1] = val;
                    row[dstOffset + 2] = val;

                    if (outputChannels > 3 && samplesPerPixel >= 2)
                    {
                        int alpha = ReadSampleValue(rawStrip, pos, bitsPerSample, bytesPerSample);
                        pos += bytesPerSample;
                        row[dstOffset + 3] = ScaleToQuantum(alpha, bitsPerSample);
                    }
                    else if (outputChannels > 3)
                    {
                        row[dstOffset + 3] = Quantum.MaxValue;
                    }

                    // Skip extra samples
                    for (int s = (samplesPerPixel >= 2 && outputChannels > 3 ? 2 : 1);s < samplesPerPixel;s++)
                    {
                        pos += bytesPerSample;
                    }
                }
                else
                {
                    // RGB
                    for (int c = 0;c < Math.Min(samplesPerPixel, outputChannels);c++)
                    {
                        int val = ReadSampleValue(rawStrip, pos, bitsPerSample, bytesPerSample);
                        pos += bytesPerSample;
                        row[dstOffset + c] = ScaleToQuantum(val, bitsPerSample);
                    }

                    // Fill missing alpha
                    if (outputChannels > samplesPerPixel)
                    {
                        for (int c = samplesPerPixel;c < outputChannels;c++)
                        {
                            row[dstOffset + c] = Quantum.MaxValue;
                        }
                    }

                    // Skip extra samples beyond what we use
                    for (int s = outputChannels;s < samplesPerPixel;s++)
                    {
                        pos += bytesPerSample;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Convert tile pixel data to ImageFrame, placing it at the correct position.
    /// Handles clipping at image edges (tiles may extend past the image boundary).
    /// </summary>
    private static void ConvertTileToPixels(ImageFrame image, byte[] rawTile,
        int startX, int startY, int effectiveW, int effectiveH, int tileWidth,
        int bitsPerSample, int samplesPerPixel, int photometric,
        bool isGrayscale, bool invertGray, bool isPalette,
        int[]? colorMap, int outputChannels)
    {
        int bytesPerSample = (bitsPerSample + 7) / 8;
        int tileRowBytes = tileWidth * samplesPerPixel * bytesPerSample;

        for (int y = 0; y < effectiveH; y++)
        {
            int outputY = startY + y;
            if (outputY >= (int)image.Rows) break;

            var row = image.GetPixelRowForWrite(outputY);
            int pos = y * tileRowBytes;

            for (int x = 0; x < effectiveW; x++)
            {
                int dstOffset = (startX + x) * outputChannels;

                if (isPalette && colorMap != null)
                {
                    int idx = ReadSampleValue(rawTile, pos, bitsPerSample, bytesPerSample);
                    pos += bytesPerSample;
                    int paletteSize = colorMap.Length / 3;
                    if (idx < paletteSize)
                    {
                        row[dstOffset] = (ushort)colorMap[idx];
                        row[dstOffset + 1] = (ushort)colorMap[paletteSize + idx];
                        row[dstOffset + 2] = (ushort)colorMap[2 * paletteSize + idx];
                    }
                }
                else if (isGrayscale)
                {
                    int gray = ReadSampleValue(rawTile, pos, bitsPerSample, bytesPerSample);
                    pos += bytesPerSample;
                    ushort val = ScaleToQuantum(gray, bitsPerSample);
                    if (invertGray) val = (ushort)(Quantum.MaxValue - val);
                    row[dstOffset] = val;
                    row[dstOffset + 1] = val;
                    row[dstOffset + 2] = val;
                    if (outputChannels > 3 && samplesPerPixel >= 2)
                    {
                        int alpha = ReadSampleValue(rawTile, pos, bitsPerSample, bytesPerSample);
                        pos += bytesPerSample;
                        row[dstOffset + 3] = ScaleToQuantum(alpha, bitsPerSample);
                    }
                    else if (outputChannels > 3)
                        row[dstOffset + 3] = Quantum.MaxValue;
                    for (int s = (samplesPerPixel >= 2 && outputChannels > 3 ? 2 : 1); s < samplesPerPixel; s++)
                        pos += bytesPerSample;
                }
                else
                {
                    for (int c = 0; c < Math.Min(samplesPerPixel, outputChannels); c++)
                    {
                        int val = ReadSampleValue(rawTile, pos, bitsPerSample, bytesPerSample);
                        pos += bytesPerSample;
                        row[dstOffset + c] = ScaleToQuantum(val, bitsPerSample);
                    }
                    if (outputChannels > samplesPerPixel)
                        for (int c = samplesPerPixel; c < outputChannels; c++)
                            row[dstOffset + c] = Quantum.MaxValue;
                    for (int s = outputChannels; s < samplesPerPixel; s++)
                        pos += bytesPerSample;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSampleValue(byte[] data, int pos, int bitsPerSample, int bytesPerSample)
    {
        if (pos >= data.Length)
        {
            return 0;
        }

        return bytesPerSample switch
        {
            1 => data[pos],
            2 when pos + 1 < data.Length => data[pos] | (data[pos + 1] << 8),
            _ => data[pos]
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleToQuantum(int value, int bitsPerSample) => bitsPerSample switch
    {
        1 => value != 0 ? Quantum.MaxValue : (ushort)0,
        8 => Quantum.ScaleFromByte((byte)value),
        16 => (ushort)value,
        _ => (ushort)(value * Quantum.MaxValue / ((1 << bitsPerSample) - 1))
    };

    #endregion

    #region IFD Writing

    /// <summary>
    /// Write an IFD. Returns the stream position of the "next IFD offset" field for chaining.
    /// </summary>
    private static long WriteIfd(BinaryWriter writer, List<TiffTagEntry> tags, int ifdStart,
        bool isLast = true)
    {
        int extraDataOffset = ifdStart + 2 + tags.Count * 12 + 4;
        var extraData = new MemoryStream();

        writer.Write((ushort)tags.Count);

        foreach (var tag in tags)
        {
            writer.Write(tag.Tag);
            writer.Write(tag.Type);
            writer.Write((uint)tag.Count);

            int valueSize = GetTypeSize(tag.Type) * tag.Count;
            if (valueSize <= 4)
                WriteTagValueInline(writer, tag);
            else
            {
                writer.Write((uint)(extraDataOffset + extraData.Position));
                WriteTagExtraData(extraData, tag);
            }
        }

        // Next IFD offset — save position for chaining
        long nextIfdOffsetPos = writer.BaseStream.Position;
        writer.Write((uint)0);

        writer.Write(extraData.ToArray());
        return nextIfdOffsetPos;
    }

    private static void WriteTagValueInline(BinaryWriter writer, TiffTagEntry tag)
    {
        using var ms = new MemoryStream(4);
        using var tw = new BinaryWriter(ms);

        if (tag.Type == TypeShort)
        {
            for (int i = 0;i < tag.Count && i < 2;i++)
            {
                tw.Write((ushort)tag.Values[i]);
            }
        }
        else if (tag.Type == TypeLong)
        {
            if (tag.Count >= 1)
            {
                tw.Write((uint)tag.Values[0]);
            }
        }
        else if (tag.Type == TypeRational)
        {
            // Won't fit inline for rational (8 bytes)
            tw.Write((uint)tag.Values[0]);
        }

        byte[] buf = ms.ToArray();
        writer.Write(buf);
        // Pad to 4 bytes
        for (int i = buf.Length;i < 4;i++)
        {
            writer.Write((byte)0);
        }
    }

    private static void WriteTagExtraData(Stream output, TiffTagEntry tag)
    {
        using var tw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        if (tag.Type == TypeShort)
        {
            foreach (int v in tag.Values)
            {
                tw.Write((ushort)v);
            }
        }
        else if (tag.Type == TypeLong)
        {
            foreach (int v in tag.Values)
            {
                tw.Write((uint)v);
            }
        }
        else if (tag.Type == TypeRational)
        {
            // Values stored as num, denom pairs
            for (int i = 0;i < tag.Values.Length;i += 2)
            {
                tw.Write((uint)tag.Values[i]);
                tw.Write((uint)(i + 1 < tag.Values.Length ? tag.Values[i + 1] : 1));
            }
        }
    }

    private static TiffTagEntry MakeTag(ushort tag, ushort type, int value)
        => new(tag, type, 1, [ value ]);

    private static TiffTagEntry MakeTag(ushort tag, ushort type, int num, int denom)
        => new(tag, type, 1, [ num, denom ]);

    private static TiffTagEntry MakeTagArray(ushort tag, ushort type, int[] values)
        => new(tag, type, values.Length, values);

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt16(byte[] data, int offset, bool bigEndian) => bigEndian
        ? (data[offset] << 8) | data[offset + 1]
        : data[offset] | (data[offset + 1] << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian) => bigEndian
        ? (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3])
        : (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadInt64(byte[] data, long offset, bool bigEndian)
    {
        int o = (int)offset;
        if (o + 8 > data.Length) return 0;
        if (bigEndian)
            return ((long)data[o] << 56) | ((long)data[o + 1] << 48) | ((long)data[o + 2] << 40) |
                   ((long)data[o + 3] << 32) | ((long)data[o + 4] << 24) | ((long)data[o + 5] << 16) |
                   ((long)data[o + 6] << 8) | data[o + 7];
        return data[o] | ((long)data[o + 1] << 8) | ((long)data[o + 2] << 16) | ((long)data[o + 3] << 24) |
               ((long)data[o + 4] << 32) | ((long)data[o + 5] << 40) | ((long)data[o + 6] << 48) |
               ((long)data[o + 7] << 56);
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    #endregion
}

/// <summary>
/// TIFF compression options for writing.
/// </summary>
public enum TiffCompression
{
    None = 1,
    Lzw = 5,
    Deflate = 8,
    PackBits = 32773
}

/// <summary>
/// Internal TIFF tag representation.
/// </summary>
internal readonly record struct TiffTag(ushort Id, ushort Type, int Count, int[] Values);

/// <summary>
/// Internal TIFF tag entry for writing.
/// </summary>
internal readonly record struct TiffTagEntry(ushort Tag, ushort Type, int Count, int[] Values);
