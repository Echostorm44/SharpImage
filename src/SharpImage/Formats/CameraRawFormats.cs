// Camera raw format parsers — per-format TIFF IFD and proprietary header readers.
// Each parser extracts RawSensorData and CameraRawMetadata from its format.
// All parsers share TiffIfdReader utilities for TIFF-based formats.
// Reference: dcraw.c, libraw, Adobe DNG spec 1.7, camera manufacturer documentation.

using SharpImage.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using static SharpImage.Formats.CameraRawParserUtils;

namespace SharpImage.Formats;

// ═══════════════════════════════════════════════════════════════════════════════
// DNG (Adobe Digital Negative) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Adobe DNG files — the most standardized camera raw format.
/// Standard TIFF structure with DNG-specific tags (50706+).
/// </summary>
internal static class DngParser
{
    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid DNG: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid DNG: no IFDs found");

        // DNG stores the main image in the first IFD or a SubIFD
        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.DNG);

        // Find the raw data IFD — DNG typically stores thumbnails in IFD0 and raw data in SubIFDs.
        // Select the SubIFD with NewSubFileType=0 (full-resolution) or the largest dimensions.
        var rawIfd = ifd0;
        var allSubIfds = TiffIfdReader.ParseAllSubIfds(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian, isBigTiff);
        int bestWidth = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageWidth);
        int ifd0SubFileType = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagNewSubFileType, -1);
        foreach (var subIfd in allSubIfds)
        {
            int subWidth = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            int subFileType = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagNewSubFileType, -1);
            // Prefer full-resolution (NewSubFileType=0) with largest dimensions
            if (subFileType == 0 && subWidth > 0)
            {
                rawIfd = subIfd;
                bestWidth = subWidth;
                break; // NewSubFileType=0 is definitive
            }
            if (subWidth > bestWidth)
            {
                rawIfd = subIfd;
                bestWidth = subWidth;
            }
        }

        // Read DNG-specific tags from both IFD0 and the raw IFD (some tags are only in one)
        var tagSource = rawIfd; // Prefer raw IFD for image-specific tags
        metadata.BitsPerSample = TiffIfdReader.GetTagInt(tagSource, TiffIfdReader.TagBitsPerSample, 14);

        // Update dimensions from the selected raw IFD
        int rawW = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth);
        int rawH = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength);
        if (rawW > 0 && rawH > 0)
        {
            metadata.SensorWidth = rawW;
            metadata.SensorHeight = rawH;
            if (metadata.ActiveAreaWidth == 0 || metadata.ActiveAreaWidth < rawW)
            {
                metadata.ActiveAreaWidth = rawW;
                metadata.ActiveAreaHeight = rawH;
            }
        }

        // DNG version (from IFD0)
        var dngVersion = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagDNGVersion);

        // CFA Pattern — check raw IFD first, then IFD0, then EXIF IFD
        var cfaPattern = TiffIfdReader.GetTagArray(tagSource, TiffIfdReader.TagCFAPattern);
        if (cfaPattern.Length < 4)
            cfaPattern = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagCFAPattern);
        if (cfaPattern.Length >= 4)
            metadata.BayerPattern = DecodeCfaPattern(cfaPattern);

        // Try EXIF CFA pattern (tag 0xA302) if not found in TIFF tags
        if (metadata.BayerPattern == BayerPattern.Unknown)
        {
            int exifOffset = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagExifIFD);
            if (exifOffset > 0 && exifOffset < data.Length)
            {
                try
                {
                    var (exifIfd, _) = TiffIfdReader.ParseIfd(data, exifOffset, bigEndian);
                    const ushort tagExifCfaPattern = 0xA302;
                    var exifCfa = TiffIfdReader.GetTagArray(exifIfd, tagExifCfaPattern);
                    // EXIF CFA format: [dimH_hi, dimH_lo, dimV_hi, dimV_lo, pattern...]
                    // Pattern bytes start at index 4
                    if (exifCfa.Length >= 8)
                        metadata.BayerPattern = DecodeCfaPattern(exifCfa[4..]);
                }
                catch { /* EXIF CFA parsing is best-effort */ }
            }
        }

        // Color matrices — try raw IFD first, then IFD0
        metadata.ColorMatrix1 = ReadRationalMatrix(data, tagSource, TiffIfdReader.TagColorMatrix1, bigEndian, 9);
        if (metadata.ColorMatrix1 is null || metadata.ColorMatrix1.Length < 9)
            metadata.ColorMatrix1 = ReadRationalMatrix(data, ifd0, TiffIfdReader.TagColorMatrix1, bigEndian, 9);
        metadata.ColorMatrix2 = ReadRationalMatrix(data, tagSource, TiffIfdReader.TagColorMatrix2, bigEndian, 9);
        if (metadata.ColorMatrix2 is null || metadata.ColorMatrix2.Length < 9)
            metadata.ColorMatrix2 = ReadRationalMatrix(data, ifd0, TiffIfdReader.TagColorMatrix2, bigEndian, 9);
        metadata.ForwardMatrix1 = ReadRationalMatrix(data, tagSource, TiffIfdReader.TagForwardMatrix1, bigEndian, 9);
        if (metadata.ForwardMatrix1 is null || metadata.ForwardMatrix1.Length < 9)
            metadata.ForwardMatrix1 = ReadRationalMatrix(data, ifd0, TiffIfdReader.TagForwardMatrix1, bigEndian, 9);

        // White balance — try raw IFD first, then IFD0
        var asShotNeutral = ReadRationalArray(data, tagSource, TiffIfdReader.TagAsShotNeutral, bigEndian, 3);
        if (asShotNeutral is null || asShotNeutral.Length < 3)
            asShotNeutral = ReadRationalArray(data, ifd0, TiffIfdReader.TagAsShotNeutral, bigEndian, 3);
        if (asShotNeutral is not null && asShotNeutral.Length >= 3)
        {
            metadata.AsShotWhiteBalance = new double[3];
            for (int i = 0; i < 3; i++)
                metadata.AsShotWhiteBalance[i] = asShotNeutral[i] > 0 ? 1.0 / asShotNeutral[i] : 1.0;
        }

        // Black/White levels — try raw IFD first, then IFD0
        var blackLevel = TiffIfdReader.GetTagArray(tagSource, TiffIfdReader.TagBlackLevel);
        if (blackLevel.Length == 0) blackLevel = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagBlackLevel);
        if (blackLevel.Length > 0) metadata.BlackLevel = blackLevel[0];
        if (blackLevel.Length >= 4) metadata.BlackLevelPerChannel = blackLevel[..4];

        var whiteLevel = TiffIfdReader.GetTagArray(tagSource, TiffIfdReader.TagWhiteLevel);
        if (whiteLevel.Length == 0) whiteLevel = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagWhiteLevel);
        if (whiteLevel.Length > 0) metadata.WhiteLevel = whiteLevel[0];
        else metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        // Active area — try raw IFD first, then IFD0
        var activeArea = TiffIfdReader.GetTagArray(tagSource, TiffIfdReader.TagActiveArea);
        if (activeArea.Length < 4) activeArea = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagActiveArea);
        if (activeArea.Length >= 4)
        {
            metadata.ActiveAreaTop = activeArea[0];
            metadata.ActiveAreaLeft = activeArea[1];
            metadata.ActiveAreaHeight = activeArea[2] - activeArea[0];
            metadata.ActiveAreaWidth = activeArea[3] - activeArea[1];
        }

        // Linearization table
        var linTable = TiffIfdReader.GetTagArray(tagSource, TiffIfdReader.TagLinearizationTable);
        if (linTable.Length == 0) linTable = TiffIfdReader.GetTagArray(ifd0, TiffIfdReader.TagLinearizationTable);
        if (linTable.Length > 0)
        {
            metadata.LinearizationTable = new ushort[linTable.Length];
            for (int i = 0; i < linTable.Length; i++)
                metadata.LinearizationTable[i] = (ushort)Math.Clamp(linTable[i], 0, 65535);
        }

        // Read compression from the raw IFD (not IFD0, which may be thumbnail)
        int compression = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = compression switch
        {
            7 => RawCompression.LosslessJpeg,
            34892 => RawCompression.LosslessJpeg, // DNG lossy JPEG (treat as lossless for now)
            _ => RawCompression.Uncompressed
        };

        // Extract raw sensor data from the selected IFD
        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }

    internal static byte[] Encode(SharpImage.Image.ImageFrame image, in CameraRawMetadata metadata)
    {
        // DNG encoding: write as a valid TIFF/DNG with required DNG tags
        // This is a simplified encoder that creates a minimal valid DNG
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // TIFF header (little-endian)
        writer.Write((byte)'I'); writer.Write((byte)'I'); // Little-endian
        writer.Write((ushort)42); // TIFF magic
        writer.Write((uint)8); // First IFD offset

        // For now, write uncompressed CFA data as a simple DNG
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        // Build tag list
        var tags = new List<(ushort tag, ushort type, int count, byte[] value)>();

        AddTag(tags, 256, 3, 1, BitConverter.GetBytes((ushort)width)); // ImageWidth
        AddTag(tags, 257, 3, 1, BitConverter.GetBytes((ushort)height)); // ImageLength
        AddTag(tags, 258, 3, 1, BitConverter.GetBytes((ushort)16)); // BitsPerSample
        AddTag(tags, 259, 3, 1, BitConverter.GetBytes((ushort)1)); // Compression: None
        AddTag(tags, 262, 3, 1, BitConverter.GetBytes((ushort)32803)); // PhotometricInterpretation: CFA
        AddTag(tags, 274, 3, 1, BitConverter.GetBytes((ushort)1)); // Orientation: Normal

        // DNG Version 1.4
        AddTag(tags, 50706, 1, 4, [1, 4, 0, 0]); // DNGVersion
        AddTag(tags, 50707, 1, 4, [1, 1, 0, 0]); // DNGBackwardVersion

        // CFA pattern (RGGB default)
        AddTag(tags, 33421, 3, 2, [2, 0, 2, 0]); // CFARepeatPatternDim
        AddTag(tags, 33422, 1, 4, [0, 1, 1, 2]); // CFAPattern: RGGB

        // Write IFD
        long ifdPos = ms.Position;
        int tagCount = tags.Count;
        writer.Write((ushort)tagCount);

        // Calculate data area start (after IFD + next IFD pointer)
        long dataAreaStart = ifdPos + 2 + tagCount * 12 + 4;

        // Pixel data follows tags
        long stripOffset = dataAreaStart;
        int stripByteCount = width * height * 2; // 16-bit per sample

        // Add strip offset/count tags
        AddTag(tags, 273, 4, 1, BitConverter.GetBytes((uint)stripOffset)); // StripOffsets
        AddTag(tags, 277, 3, 1, BitConverter.GetBytes((ushort)1)); // SamplesPerPixel
        AddTag(tags, 278, 4, 1, BitConverter.GetBytes((uint)height)); // RowsPerStrip
        AddTag(tags, 279, 4, 1, BitConverter.GetBytes((uint)stripByteCount)); // StripByteCounts

        // Rewrite IFD with all tags
        ms.Position = ifdPos;
        writer.Write((ushort)tags.Count);
        foreach (var (tag, type, count, value) in tags.OrderBy(t => t.tag))
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            // Inline value if 4 bytes or less
            if (value.Length <= 4)
            {
                writer.Write(value);
                for (int i = value.Length; i < 4; i++) writer.Write((byte)0);
            }
            else
            {
                writer.Write((uint)0); // placeholder - would need offset for larger values
            }
        }
        writer.Write((uint)0); // No next IFD

        // Write pixel data (raw CFA values from green channel as approximation)
        ms.Position = stripOffset;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                // Write the primary channel value as a CFA sample
                ushort value = row[x * channels]; // R channel
                writer.Write(value);
            }
        }

        return ms.ToArray();
    }

    private static void AddTag(List<(ushort, ushort, int, byte[])> tags, ushort tag, ushort type, int count, byte[] value)
    {
        tags.Add((tag, type, count, value));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CR2 (Canon) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Canon CR2 files — TIFF-based with 4 IFDs. Raw data in IFD3 as lossless JPEG.
/// Signature: II\x2A\x00 + offset to IFD0 + "CR\x02" at bytes 8-10.
/// </summary>
internal static class Cr2Parser
{
    private const ushort TagCr2SliceInfo = 0xC640; // Canon CR2 slice information
    private const ushort TagMakerNote = 37500; // 0x927C — EXIF MakerNote tag
    private const ushort TagCanonColorData = 0x4001; // Canon color/WB data in MakerNote

    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out _, out long firstIfdOffset))
            throw new InvalidDataException("Invalid CR2: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset);
        if (ifds.Count < 4)
            throw new InvalidDataException($"Invalid CR2: expected 4 IFDs, found {ifds.Count}");

        var ifd0 = ifds[0];
        var ifd3 = ifds[3]; // Raw data IFD

        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.CR2);

        // Extract Canon MakerNote white balance multipliers
        ExtractCanonWhiteBalance(data, ifd0, bigEndian, ref metadata);

        // CR2 raw data dimensions from IFD3
        int rawWidth = TiffIfdReader.GetTagInt(ifd3, TiffIfdReader.TagImageWidth, 0);
        int rawHeight = TiffIfdReader.GetTagInt(ifd3, TiffIfdReader.TagImageLength, 0);
        if (rawWidth == 0) rawWidth = metadata.SensorWidth;
        if (rawHeight == 0) rawHeight = metadata.SensorHeight;
        metadata.SensorWidth = rawWidth;
        metadata.SensorHeight = rawHeight;

        // CR2 is typically RGGB
        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(ifd3, TiffIfdReader.TagBitsPerSample, 14);

        // Canon 14-bit sensors have optical black level ~2048. Without subtracting this,
        // dark areas have near-identical R/G/B values (e.g., 2150-2310 for a dark red motorcycle),
        // destroying color separation. With subtraction: R=261, G=211, B=104 → clear separation.
        if (metadata.BlackLevel == 0)
            metadata.BlackLevel = metadata.BitsPerSample >= 14 ? 2048 : 512;
        metadata.CfaType = CfaType.Bayer;
        metadata.Compression = RawCompression.LosslessJpeg;

        // Read CR2 slice info from IFD3 (tag 0xC640)
        int[] sliceInfo = TiffIfdReader.GetTagArray(ifd3, TagCr2SliceInfo);

        // Extract lossless JPEG strip data
        long stripOffset = TiffIfdReader.GetTagLong(ifd3, TiffIfdReader.TagStripOffsets, 0);
        long stripCount = TiffIfdReader.GetTagLong(ifd3, TiffIfdReader.TagStripByteCounts, 0);
        if (stripOffset <= 0 || stripCount <= 0)
            throw new InvalidDataException("CR2: no strip data found in IFD3");

        var decoded = LosslessJpegDecoder.Decode(data, (int)stripOffset, (int)stripCount,
            out int ljWidth, out int ljHeight, out int components, out _);
        int jwide = ljWidth * components;

        // De-slice: CR2 JPEG stores sensor data in vertical slice order.
        // Each slice contains all rows for a column range, stored sequentially.
        // dcraw algorithm: jidx → (slice_index, position_in_slice) → (row, col)
        var pixels = new ushort[rawWidth * rawHeight];

        if (sliceInfo.Length >= 3 && sliceInfo[0] > 0)
        {
            int sliceCount = sliceInfo[0];  // number of initial slices
            int sliceWidth = sliceInfo[1];  // width of initial slices
            int lastSliceWidth = sliceInfo[2]; // width of the final slice

            int jidx = 0;
            for (int jrow = 0; jrow < ljHeight; jrow++)
            {
                int rowBase = jrow * jwide;
                for (int jcol = 0; jcol < jwide; jcol++, jidx++)
                {
                    // Determine which slice this value belongs to
                    int sliceIdx = jidx / (sliceWidth * rawHeight);
                    int sliceW;
                    if (sliceIdx < sliceCount)
                        sliceW = sliceWidth;
                    else
                    {
                        sliceIdx = sliceCount;
                        sliceW = lastSliceWidth;
                    }

                    int posInSlice = jidx - sliceIdx * sliceWidth * rawHeight;
                    if (sliceIdx == sliceCount)
                        posInSlice = jidx - sliceCount * sliceWidth * rawHeight;

                    int row = posInSlice / sliceW;
                    int col = posInSlice % sliceW + Math.Min(sliceIdx, sliceCount) * sliceWidth;

                    if (row >= 0 && row < rawHeight && col >= 0 && col < rawWidth)
                        pixels[row * rawWidth + col] = decoded[rowBase + jcol];
                }
            }
        }
        else
        {
            // No slice info — treat as linear stream mapping to sensor raster order
            int total = Math.Min(decoded.Length, rawWidth * rawHeight);
            Array.Copy(decoded, pixels, total);
        }

        // Canon sensors saturate below 2^bits-1 (e.g., 15510 vs 16383 for 14-bit).
        // Using the theoretical max as white level causes under-scaling by ~5-6%.
        // Scan the raw data to find the actual saturation point (99.9th percentile).
        int theoreticalMax = (1 << metadata.BitsPerSample) - 1;
        metadata.WhiteLevel = DetectRawWhiteLevel(pixels, metadata.BlackLevel, theoreticalMax);

        return new RawSensorData
        {
            RawPixels = pixels,
            Width = rawWidth,
            Height = rawHeight,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Extracts white balance multipliers from Canon MakerNote (EXIF tag 37500).
    /// Canon stores WB data in MakerNote tag 0x4001 (ColorData) as RGGB SHORT values.
    /// The offset within the tag depends on the total element count (varies by camera model).
    /// </summary>
    private static void ExtractCanonWhiteBalance(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        try
        {
            ExtractCanonWhiteBalanceCore(data, ifd0, bigEndian, ref metadata);
        }
        catch
        {
            // MakerNote parsing is best-effort; auto WB fallback will handle it
        }
    }

    private static void ExtractCanonWhiteBalanceCore(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        // Parse EXIF IFD to find MakerNote
        var exifIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagExifIFD, bigEndian);
        if (exifIfd is null || !exifIfd.TryGetValue(TagMakerNote, out var mnTag))
            return;

        // Canon MakerNote is a standard TIFF IFD structure (no prefix header).
        // ValueOffset holds the file offset where the MakerNote data begins.
        long mnOffset = mnTag.ValueOffset;
        if (mnOffset <= 0 || mnOffset + 2 >= data.Length) return;

        // Parse Canon MakerNote IFD
        var (mnIfd, _) = TiffIfdReader.ParseIfd(data, mnOffset, bigEndian);
        if (mnIfd.Count == 0 || !mnIfd.TryGetValue(TagCanonColorData, out var colorDataTag))
            return;

        // Tag 0x4001 is a SHORT array — WB offset depends on array length
        int wbOffset = colorDataTag.Count switch
        {
            582 => 50,   // Canon EOS 20D
            653 => 68,   // Canon EOS 1D Mark II
            796 => 63,   // Canon EOS 30D, 400D
            674 or 692 or 702 or 1227 or 1250 or 1251 or 1273 or 1275 => 63,
            5120 => 71,  // Canon EOS 5D Mark III, 6D, etc.
            _ => 63      // Default offset works for most Canon models (5D4, 80D, etc.)
        };

        if (wbOffset + 3 >= colorDataTag.Count) return;

        int rggbR = colorDataTag.Values[wbOffset];
        int rggbGr = colorDataTag.Values[wbOffset + 1];
        int rggbGb = colorDataTag.Values[wbOffset + 2];
        int rggbB = colorDataTag.Values[wbOffset + 3];

        // Normalize to Green=1.0 (average of Gr and Gb)
        double greenAvg = (rggbGr + rggbGb) / 2.0;
        if (greenAvg > 0)
        {
            metadata.AsShotWhiteBalance = [rggbR / greenAvg, 1.0, rggbB / greenAvg];
        }
    }

    /// <summary>
    /// Detects the actual sensor saturation level from raw pixel data.
    /// Canon sensors clip below the theoretical maximum (e.g., 15383 for 14-bit = 16383).
    /// Finds the saturation spike — the value where a large number of pixels cluster,
    /// indicating sensor clipping. This is typically well below 2^bits - 1.
    /// </summary>
    private static int DetectRawWhiteLevel(ushort[] pixels, int blackLevel, int theoreticalMax)
    {
        // Build a histogram of the upper portion of the dynamic range.
        // Canon sensors typically saturate at 90-95% of theoretical max.
        int rangeStart = (int)(theoreticalMax * 0.80);
        int binCount = theoreticalMax - rangeStart + 1;
        int[] histogram = new int[binCount];

        for (int i = 0; i < pixels.Length; i++)
        {
            int v = pixels[i];
            if (v >= rangeStart && v <= theoreticalMax)
                histogram[v - rangeStart]++;
        }

        // Find the saturation spike — the single bin with an anomalously high count.
        // Sensor clipping creates a spike that's 10-100× larger than neighboring bins.
        // Search from the top down looking for a bin much larger than its neighbors.
        int spikeBin = -1;
        int spikeCount = 0;

        for (int i = binCount - 1; i >= 0; i--)
        {
            if (histogram[i] > spikeCount)
            {
                spikeCount = histogram[i];
                spikeBin = i;
            }
        }

        // The spike must be significant (>0.05% of total pixels) and must be
        // much larger than the local average to be a true saturation point
        if (spikeBin >= 0 && spikeCount > pixels.Length * 0.0005)
        {
            // Verify it's a true spike by checking it's at least 10× the local median
            int windowStart = Math.Max(0, spikeBin - 20);
            int windowEnd = Math.Min(binCount - 1, spikeBin + 20);
            int neighborSum = 0;
            int neighborCount = 0;
            for (int i = windowStart; i <= windowEnd; i++)
            {
                if (i != spikeBin)
                {
                    neighborSum += histogram[i];
                    neighborCount++;
                }
            }
            double neighborAvg = neighborCount > 0 ? (double)neighborSum / neighborCount : 0;
            if (spikeCount > neighborAvg * 10)
                return rangeStart + spikeBin;
        }

        return theoreticalMax;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CR3 (Canon) Parser — ISO BMFF container
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Canon CR3 files — ISO Base Media File Format (like HEIF/AVIF).
/// Signature: "ftypcrx " at offset 4.
/// </summary>
internal static class Cr3Parser
{
    internal static RawSensorData Parse(byte[] data)
    {
        // CR3 uses ISO BMFF container format with CRAW codec
        // Parse ftyp box to confirm, then find moov/mdat boxes
        int pos = 0;

        // Parse boxes
        long mdatOffset = 0;
        long mdatSize = 0;
        int width = 0, height = 0;

        while (pos + 8 <= data.Length)
        {
            uint boxSize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            string boxType = Encoding.ASCII.GetString(data, pos + 4, 4);

            if (boxSize == 0) break;
            if (boxSize == 1 && pos + 16 <= data.Length)
            {
                // Extended size
                boxSize = (uint)TiffIfdReader.ReadUInt32(data, pos + 8, true);
            }

            switch (boxType)
            {
                case "moov":
                    // Parse moov for track info
                    ParseMoov(data, pos + 8, (int)(pos + boxSize), out width, out height);
                    break;
                case "mdat":
                    mdatOffset = pos + 8;
                    mdatSize = boxSize - 8;
                    break;
            }

            pos += (int)boxSize;
            if (boxSize < 8) break;
        }

        if (width == 0 || height == 0)
        {
            // Fallback: try to parse as TIFF-like structure within mdat
            width = 6000; height = 4000; // Common Canon resolution
        }

        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.CR3,
            Make = "Canon",
            SensorWidth = width,
            SensorHeight = height,
            ActiveAreaWidth = width,
            ActiveAreaHeight = height,
            BayerPattern = BayerPattern.RGGB,
            CfaType = CfaType.Bayer,
            BitsPerSample = 14,
            Compression = RawCompression.LosslessJpeg,
            WhiteLevel = (1 << 14) - 1,
            Orientation = 1
        };

        // Extract raw data from mdat
        return ExtractCr3SensorData(data, mdatOffset, mdatSize, metadata);
    }

    private static void ParseMoov(byte[] data, int start, int end, out int width, out int height)
    {
        width = 0; height = 0;
        int pos = start;
        while (pos + 8 <= end)
        {
            uint boxSize = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            if (boxSize < 8 || pos + boxSize > end) break;
            string boxType = Encoding.ASCII.GetString(data, pos + 4, 4);

            if (boxType == "trak" || boxType == "tkhd" || boxType == "mdia" || boxType == "minf" || boxType == "stbl")
            {
                ParseMoov(data, pos + 8, (int)(pos + boxSize), out int w, out int h);
                if (w > width) { width = w; height = h; }
            }
            else if (boxType == "stsd" && pos + 24 <= end)
            {
                // Sample description might contain dimensions
                // Check for CRAW sample entry
                int entryPos = pos + 16; // skip stsd header
                if (entryPos + 8 <= end)
                {
                    string entryType = Encoding.ASCII.GetString(data, entryPos + 4, 4);
                    if (entryType == "CRAW" && entryPos + 32 <= end)
                    {
                        width = (data[entryPos + 24] << 8) | data[entryPos + 25];
                        height = (data[entryPos + 26] << 8) | data[entryPos + 27];
                    }
                }
            }

            pos += (int)boxSize;
        }
    }

    private static RawSensorData ExtractCr3SensorData(byte[] data, long offset, long size, CameraRawMetadata metadata)
    {
        int w = metadata.SensorWidth;
        int h = metadata.SensorHeight;

        if (offset > 0 && size > 0 && offset + size <= data.Length)
        {
            try
            {
                // Try to decode as lossless JPEG
                var decoded = LosslessJpegDecoder.Decode(data, (int)offset, (int)size,
                    out int ljWidth, out int ljHeight, out _, out int ljPrecision);

                if (ljWidth > 0 && ljHeight > 0)
                {
                    metadata.SensorWidth = ljWidth;
                    metadata.SensorHeight = ljHeight;
                    metadata.BitsPerSample = ljPrecision;
                    return new RawSensorData
                    {
                        RawPixels = decoded,
                        Width = ljWidth,
                        Height = ljHeight,
                        Metadata = metadata
                    };
                }
            }
            catch
            {
                // Fall through to uncompressed extraction
            }
        }

        // Fallback: extract raw bytes as uncompressed
        return CreateUncompressedSensorData(data, offset, w, h, metadata);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// NEF (Nikon) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Nikon NEF files — TIFF-based with Nikon MakerNote.
/// Also handles NRW (Nikon 1 / Coolpix) variant.
/// </summary>
internal static class NefParser
{
    private const ushort TagMakerNote = 37500;

    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid NEF: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid NEF: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.NEF);
        metadata.BigEndian = bigEndian;

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;

        metadata.CfaType = CfaType.Bayer;

        // NEF stores raw data in SubIFDs — parse ALL and select the full-resolution one.
        var rawIfd = ifd0;
        int bestWidth = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageWidth);
        var allSubIfds = TiffIfdReader.ParseAllSubIfds(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian, isBigTiff);
        foreach (var subIfd in allSubIfds)
        {
            int subWidth = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            int subFileType = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagNewSubFileType, -1);
            if (subFileType == 0 && subWidth > 0)
            {
                rawIfd = subIfd;
                bestWidth = subWidth;
                break;
            }
            if (subWidth > bestWidth)
            {
                rawIfd = subIfd;
                bestWidth = subWidth;
            }
        }

        int w = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int h = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        metadata.ActiveAreaWidth = w;
        metadata.ActiveAreaHeight = h;

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg
            : comp == 34713 ? RawCompression.NikonCompressed
            : RawCompression.Uncompressed;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 14);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        // Extract Nikon MakerNote meta_offset for Huffman tree/curve/vpred
        ExtractNikonMakerNoteMetaOffset(data, ifd0, bigEndian, ref metadata);

        // Nikon cameras store a pre-rotated JPEG thumbnail in IFD0 but set orientation=1.
        // When the thumbnail aspect ratio is inverted vs the raw sensor data, infer the
        // correct orientation from the thumbnail dimensions (dcraw reads flip from IFD0 but
        // some Nikon models need this inference for correct portrait-mode handling).
        if (metadata.Orientation == 1)
        {
            int thumbW = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageWidth);
            int thumbH = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageLength);
            if (thumbW > 0 && thumbH > 0 && thumbW != w)
            {
                bool thumbIsPortrait = thumbH > thumbW;
                bool rawIsLandscape = w > h;
                if (thumbIsPortrait && rawIsLandscape)
                    metadata.Orientation = 8; // Rotate 90° CCW to match portrait thumbnail
                else if (!thumbIsPortrait && !rawIsLandscape && w != h)
                    metadata.Orientation = 8; // Thumbnail landscape but raw portrait
            }
        }

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }

    /// <summary>
    /// Finds the Nikon MakerNote and extracts meta_offset and camera white balance.
    /// meta_offset: file position of NikonLinearizationTable (0x96) or NikonContrastCurve (0x8C).
    /// WB: extracted from encrypted ColorBalance tag (0x0097) using serial number and shutter count,
    /// with fallback to unencrypted ColorBalance1 tag (0x000C) if decryption fails.
    /// </summary>
    private static void ExtractNikonMakerNoteMetaOffset(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        try
        {
            var exifIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagExifIFD, bigEndian);
            if (exifIfd is null || !exifIfd.TryGetValue(TagMakerNote, out var mnTag))
                return;

            // Get MakerNote absolute offset
            long mnOffset = TiffIfdReader.GetTagLargeDataOffset(mnTag);
            if (mnOffset <= 0 || mnOffset + 18 >= data.Length) return;

            // Nikon type-2 MakerNote: "Nikon\x00\x02\x10\x00\x00" (10 bytes) then embedded TIFF
            if (data[mnOffset] == (byte)'N' && data[mnOffset + 1] == (byte)'i' &&
                data[mnOffset + 2] == (byte)'k' && data[mnOffset + 3] == (byte)'o' &&
                data[mnOffset + 4] == (byte)'n' && data[mnOffset + 5] == 0x00)
            {
                long tiffStart = mnOffset + 10; // Embedded TIFF header
                bool mnBigEndian = data[tiffStart] == (byte)'M' && data[tiffStart + 1] == (byte)'M';

                // Parse the embedded TIFF's first IFD (the MakerNote IFD)
                int mnIfdOffset = mnBigEndian
                    ? (data[tiffStart + 4] << 24) | (data[tiffStart + 5] << 16) | (data[tiffStart + 6] << 8) | data[tiffStart + 7]
                    : data[tiffStart + 4] | (data[tiffStart + 5] << 8) | (data[tiffStart + 6] << 16) | (data[tiffStart + 7] << 24);

                long ifdAbsOffset = tiffStart + mnIfdOffset;
                if (ifdAbsOffset + 2 >= data.Length) return;

                // Parse the MakerNote IFD — offsets within are relative to tiffStart
                var (mnIfd, _) = TiffIfdReader.ParseIfd(data, ifdAbsOffset, mnBigEndian);

                // Tags 0x8C and 0x96 both set meta_offset; 0x96 comes last and wins
                const ushort tagNikonCurve8C = 0x8C;
                const ushort tagNikonCurve96 = 0x96;

                // The tag data offsets are relative to the MN TIFF base
                if (mnIfd.TryGetValue(tagNikonCurve8C, out var tag8C))
                {
                    long dataOff = GetNikonMnTagDataOffset(data, tag8C, tiffStart, mnBigEndian);
                    if (dataOff > 0) metadata.MetaOffset = dataOff;
                }
                if (mnIfd.TryGetValue(tagNikonCurve96, out var tag96))
                {
                    long dataOff = GetNikonMnTagDataOffset(data, tag96, tiffStart, mnBigEndian);
                    if (dataOff > 0) metadata.MetaOffset = dataOff;
                }

                // Extract camera white balance: try encrypted 0x0097, fallback to 0x000C
                ExtractNikonWhiteBalance(data, mnIfd, tiffStart, mnBigEndian, ref metadata);
            }
        }
        catch
        {
            // MakerNote parsing is best-effort
        }
    }

    /// <summary>
    /// Extracts camera white balance from Nikon MakerNote.
    /// Primary: tag 0x0097 (ColorBalance) — encrypted for version "0205"+.
    /// Fallback: tag 0x000C (ColorBalance1) — 4 RATIONAL values (RGGB), unencrypted.
    /// </summary>
    private static void ExtractNikonWhiteBalance(byte[] data,
        Dictionary<ushort, TiffTag> mnIfd, long mnTiffBase, bool mnBigEndian,
        ref CameraRawMetadata metadata)
    {
        // Try encrypted ColorBalance (0x0097) first
        if (TryExtractNikonEncryptedWb(data, mnIfd, mnTiffBase, mnBigEndian, ref metadata))
            return;

        // Fallback: tag 0x000C (ColorBalance1) — 4 RATIONAL values in RGGB order
        TryExtractNikonColorBalance1(data, mnIfd, mnTiffBase, mnBigEndian, ref metadata);
    }

    /// <summary>
    /// Attempts to extract WB from Nikon encrypted ColorBalance tag 0x0097.
    /// Returns true if WB was successfully set.
    /// </summary>
    private static bool TryExtractNikonEncryptedWb(byte[] data,
        Dictionary<ushort, TiffTag> mnIfd, long mnTiffBase, bool mnBigEndian,
        ref CameraRawMetadata metadata)
    {
        const ushort tagColorBalance = 0x0097;
        const ushort tagSerialNumber = 0x001D;
        const ushort tagShutterCount = 0x00A7;

        if (!mnIfd.TryGetValue(tagColorBalance, out var cbTag)) return false;

        long cbOffset = GetNikonMnTagDataOffset(data, cbTag, mnTiffBase, mnBigEndian);
        int cbSize = TiffIfdReader.GetTypeSize(cbTag.Type) * cbTag.Count;
        if (cbOffset <= 0 || cbOffset + cbSize > data.Length || cbSize < 14) return false;

        string version = System.Text.Encoding.ASCII.GetString(data, (int)cbOffset, 4);

        int wbOffset;
        bool encrypted;
        if (version == "0100" || version == "0102")
        {
            wbOffset = 68;
            encrypted = false;
        }
        else if (version == "0103")
        {
            wbOffset = 80;
            encrypted = false;
        }
        else if (version == "0205" || version == "0209" || version == "0210" ||
                 version == "0211" || version == "0212" || version == "0213" ||
                 version == "0214" || version == "0215" || version == "0216" ||
                 version == "0217" || version == "0218" || version == "0219" ||
                 version == "0220" || version == "0221" || version == "0222" ||
                 version == "0223" || version == "0224" || version == "0225" ||
                 version == "0226" || version == "0227" || version == "0228" ||
                 version == "0229" || version == "0230" || version == "0800" ||
                 version == "0801")
        {
            wbOffset = 284;
            encrypted = true;
        }
        else
            return false;

        if (cbOffset + wbOffset + 8 > data.Length) return false;

        if (encrypted)
        {
            uint serial = 0;
            if (mnIfd.TryGetValue(tagSerialNumber, out var snTag))
            {
                long snOffset = GetNikonMnTagDataOffset(data, snTag, mnTiffBase, mnBigEndian);
                int snSize = TiffIfdReader.GetTypeSize(snTag.Type) * snTag.Count;
                if (snOffset > 0 && snOffset + snSize <= data.Length)
                {
                    for (int i = 0; i < snSize && snOffset + i < data.Length; i++)
                    {
                        byte c = data[snOffset + i];
                        if (c == 0) break;
                        serial = serial * 10 + (uint)(c >= (byte)'0' && c <= (byte)'9' ? c - '0' : c % 10);
                    }
                }
            }

            // Shutter count key: XOR of all 4 bytes of tag 0x00A7 (matches dcraw behavior)
            uint shutterCountKey = 0;
            if (mnIfd.TryGetValue(tagShutterCount, out var scTag))
            {
                long scOffset = GetNikonMnTagDataOffset(data, scTag, mnTiffBase, mnBigEndian);
                if (scOffset > 0 && scOffset + 4 <= data.Length)
                    shutterCountKey = (uint)(data[scOffset] ^ data[scOffset + 1] ^ data[scOffset + 2] ^ data[scOffset + 3]);
            }

            int decryptStart = (int)cbOffset + 4;
            int decryptLen = cbSize - 4;
            if (decryptLen <= 0) return false;

            byte[] decrypted = new byte[decryptLen];
            Array.Copy(data, decryptStart, decrypted, 0, decryptLen);
            NikonDecrypt(serial, shutterCountKey, decrypted, decryptLen);

            int wbInDecrypted = wbOffset - 4;
            if (wbInDecrypted + 8 > decrypted.Length) return false;

            ushort wbR = (ushort)(decrypted[wbInDecrypted] | (decrypted[wbInDecrypted + 1] << 8));
            ushort wbGr = (ushort)(decrypted[wbInDecrypted + 2] | (decrypted[wbInDecrypted + 3] << 8));
            ushort wbGb = (ushort)(decrypted[wbInDecrypted + 4] | (decrypted[wbInDecrypted + 5] << 8));
            ushort wbB = (ushort)(decrypted[wbInDecrypted + 6] | (decrypted[wbInDecrypted + 7] << 8));

            if (wbR > 0 && wbGr > 0 && wbB > 0)
            {
                double green = (wbGr + wbGb) / 2.0;
                double rNorm = wbR / green;
                double bNorm = wbB / green;
                // Sanity check: valid WB multipliers are typically in 0.4–8.0 range
                if (rNorm > 0.4 && rNorm < 8.0 && bNorm > 0.4 && bNorm < 8.0)
                {
                    metadata.AsShotWhiteBalance = [rNorm, 1.0, bNorm];
                    return true;
                }
            }
        }
        else
        {
            int off = (int)cbOffset + wbOffset;
            if (off + 8 > data.Length) return false;

            ushort wbR, wbGr, wbGb, wbB;
            if (mnBigEndian)
            {
                wbR = (ushort)((data[off] << 8) | data[off + 1]);
                wbGr = (ushort)((data[off + 2] << 8) | data[off + 3]);
                wbGb = (ushort)((data[off + 4] << 8) | data[off + 5]);
                wbB = (ushort)((data[off + 6] << 8) | data[off + 7]);
            }
            else
            {
                wbR = (ushort)(data[off] | (data[off + 1] << 8));
                wbGr = (ushort)(data[off + 2] | (data[off + 3] << 8));
                wbGb = (ushort)(data[off + 4] | (data[off + 5] << 8));
                wbB = (ushort)(data[off + 6] | (data[off + 7] << 8));
            }

            if (wbR > 0 && wbGr > 0 && wbB > 0)
            {
                double green = (wbGr + wbGb) / 2.0;
                metadata.AsShotWhiteBalance = [wbR / green, 1.0, wbB / green];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fallback WB extraction from Nikon MakerNote tag 0x000C (ColorBalance1).
    /// Contains 4 RATIONAL values. Nikon stores these as [R, B, Gr, Gb] gain factors.
    /// Not as accurate as the encrypted 0x0097 but better than daylight WB from the color matrix.
    /// </summary>
    private static void TryExtractNikonColorBalance1(byte[] data,
        Dictionary<ushort, TiffTag> mnIfd, long mnTiffBase, bool mnBigEndian,
        ref CameraRawMetadata metadata)
    {
        const ushort tagColorBalance1 = 0x000C;
        if (!mnIfd.TryGetValue(tagColorBalance1, out var cbTag)) return;

        // Must be RATIONAL type (5) with at least 4 values
        if (cbTag.Type != 5 || cbTag.Count < 4) return;

        long dataOffset = GetNikonMnTagDataOffset(data, cbTag, mnTiffBase, mnBigEndian);
        if (dataOffset <= 0 || dataOffset + 32 > data.Length) return;

        // Read 4 RATIONAL values (8 bytes each: 4-byte numerator + 4-byte denominator)
        double[] vals = new double[4];
        for (int i = 0; i < 4; i++)
        {
            long pos = dataOffset + i * 8;
            uint numerator = mnBigEndian
                ? (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3])
                : (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
            uint denominator = mnBigEndian
                ? (uint)((data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7])
                : (uint)(data[pos + 4] | (data[pos + 5] << 8) | (data[pos + 6] << 16) | (data[pos + 7] << 24));
            vals[i] = denominator > 0 ? (double)numerator / denominator : 0;
        }

        // Nikon order: [R_gain, B_gain, Gr_gain, Gb_gain]
        double rGain = vals[0], bGain = vals[1], grGain = vals[2], gbGain = vals[3];
        if (rGain > 0 && grGain > 0 && gbGain > 0)
            metadata.AsShotWhiteBalance = [rGain / grGain, 1.0, bGain / gbGain];
    }

    /// <summary>
    /// Nikon MakerNote decryption cipher (from dcraw). Uses two 256-byte substitution tables
    /// seeded by the camera serial number and shutter count to decrypt ColorBalance data.
    /// </summary>
    private static void NikonDecrypt(uint serial, uint count, byte[] buf, int len)
    {
        byte ci = NikonXlat0[(int)(serial & 0xFF)];
        byte cj = NikonXlat1[(int)(count & 0xFF)];
        byte ck = 0x60;
        for (int i = 0; i < len; i++)
        {
            cj = (byte)(cj + ci * ck);
            ck++;
            buf[i] ^= cj;
        }
    }

    // Nikon decryption lookup tables (from dcraw parse.c xlat[2][256])
    private static ReadOnlySpan<byte> NikonXlat0 =>
    [
        0xc1,0xbf,0x6d,0x0d,0x59,0xc5,0x13,0x9d,0x83,0x61,0x6b,0x4f,0xc7,0x7f,0x3d,0x3d,
        0x53,0x59,0xe3,0xc7,0xe9,0x2f,0x95,0xa7,0x95,0x1f,0xdf,0x7f,0x2b,0x29,0xc7,0x0d,
        0xdf,0x07,0xef,0x71,0x89,0x3d,0x13,0x3d,0x3b,0x13,0xfb,0x0d,0x89,0xc1,0x65,0x1f,
        0xb3,0x0d,0x6b,0x29,0xe3,0xfb,0xef,0xa3,0x6b,0x47,0x7f,0x95,0x35,0xa7,0x47,0x4f,
        0xc7,0xf1,0x59,0x95,0x35,0x11,0x29,0x61,0xf1,0x3d,0xb3,0x2b,0x0d,0x43,0x89,0xc1,
        0x9d,0x9d,0x89,0x65,0xf1,0xe9,0xdf,0xbf,0x3d,0x7f,0x53,0x97,0xe5,0xe9,0x95,0x17,
        0x1d,0x3d,0x8b,0xfb,0xc7,0xe3,0x67,0xa7,0x07,0xf1,0x71,0xa7,0x53,0xb5,0x29,0x89,
        0xe5,0x2b,0xa7,0x17,0x29,0xe9,0x4f,0xc5,0x65,0x6d,0x6b,0xef,0x0d,0x89,0x49,0x2f,
        0xb3,0x43,0x53,0x65,0x1d,0x49,0xa3,0x13,0x89,0x59,0xef,0x6b,0xef,0x65,0x1d,0x0b,
        0x59,0x13,0xe3,0x4f,0x9d,0xb3,0x29,0x43,0x2b,0x07,0x1d,0x95,0x59,0x59,0x47,0xfb,
        0xe5,0xe9,0x61,0x47,0x2f,0x35,0x7f,0x17,0x7f,0xef,0x7f,0x95,0x95,0x71,0xd3,0xa3,
        0x0b,0x71,0xa3,0xad,0x0b,0x3b,0xb5,0xfb,0xa3,0xbf,0x4f,0x83,0x1d,0xad,0xe9,0x2f,
        0x71,0x65,0xa3,0xe5,0x07,0x35,0x3d,0x0d,0xb5,0xe9,0xe5,0x47,0x3b,0x9d,0xef,0x35,
        0xa3,0xbf,0xb3,0xdf,0x53,0xd3,0x97,0x53,0x49,0x71,0x07,0x35,0x61,0x71,0x2f,0x43,
        0x2f,0x11,0xdf,0x17,0x97,0xfb,0x95,0x3b,0x7f,0x6b,0xd3,0x25,0xbf,0xad,0xc7,0xc5,
        0xc5,0xb5,0x8b,0xef,0x2f,0xd3,0x07,0x6b,0x25,0x49,0x95,0x25,0x49,0x6d,0x71,0xc7
    ];

    private static ReadOnlySpan<byte> NikonXlat1 =>
    [
        0xa7,0xbc,0xc9,0xad,0x91,0xdf,0x85,0xe5,0xd4,0x78,0xd5,0x17,0x46,0x7c,0x29,0x4c,
        0x4d,0x03,0xe9,0x25,0x68,0x11,0x86,0xb3,0xbd,0xf7,0x6f,0x61,0x22,0xa2,0x26,0x34,
        0x2a,0xbe,0x1e,0x46,0x14,0x68,0x9d,0x44,0x18,0xc2,0x40,0xf4,0x7e,0x5f,0x1b,0xad,
        0x0b,0x94,0xb6,0x67,0xb4,0x0b,0xe1,0xea,0x95,0x9c,0x66,0xdc,0xe7,0x5d,0x6c,0x05,
        0xda,0xd5,0xdf,0x7a,0xef,0xf6,0xdb,0x1f,0x82,0x4c,0xc0,0x68,0x47,0xa1,0xbd,0xee,
        0x39,0x50,0x56,0x4a,0xdd,0xdf,0xa5,0xf8,0xc6,0xda,0xca,0x90,0xca,0x01,0x42,0x9d,
        0x8b,0x0c,0x73,0x43,0x75,0x05,0x94,0xde,0x24,0xb3,0x80,0x34,0xe5,0x2c,0xdc,0x9b,
        0x3f,0xca,0x33,0x45,0xd0,0xdb,0x5f,0xf5,0x52,0xc3,0x21,0xda,0xe2,0x22,0x72,0x6b,
        0x3e,0xd0,0x5b,0xa8,0x87,0x8c,0x06,0x5d,0x0f,0xdd,0x09,0x19,0x93,0xd0,0xb9,0xfc,
        0x8b,0x0f,0x84,0x60,0x33,0x1c,0x9b,0x45,0xf1,0xf0,0xa3,0x94,0x3a,0x12,0x77,0x33,
        0x4d,0x44,0x78,0x28,0x3c,0x9e,0xfd,0x65,0x57,0x16,0x94,0x6b,0xfb,0x59,0xd0,0xc8,
        0x22,0x36,0xdb,0xd2,0x63,0x98,0x43,0xa1,0x04,0x87,0x86,0xf7,0xa6,0x26,0xbb,0xd6,
        0x59,0x4d,0xbf,0x6a,0x2e,0xaa,0x2b,0xef,0xe6,0x78,0xb6,0x4e,0xe0,0x2f,0xdc,0x7c,
        0xbe,0x57,0x19,0x32,0x7e,0x2a,0xd0,0xb8,0xba,0x29,0x00,0x3c,0x52,0x7d,0xa8,0x49,
        0x3b,0x2d,0xeb,0x25,0x49,0xfa,0xa3,0xaa,0x39,0xa7,0xc5,0xa7,0x50,0x11,0x36,0xfb,
        0xc6,0x67,0x4a,0xf5,0xa5,0x12,0x65,0x7e,0xb0,0xdf,0xaf,0x4e,0xb3,0x61,0x7f,0x2f
    ];

    /// <summary>
    /// Gets the absolute file offset of a Nikon MakerNote tag's data.
    /// For small tags (≤4 bytes), data is inline at the IFD entry. For large tags, the stored
    /// offset in the IFD entry is relative to the MakerNote's embedded TIFF header start.
    /// ParseIfd stores the raw offset value in ValueOffset — we add the MN TIFF base to get absolute.
    /// </summary>
    private static long GetNikonMnTagDataOffset(byte[] data, TiffTag tag, long mnTiffBase, bool mnBigEndian)
    {
        int typeSize = TiffIfdReader.GetTypeSize(tag.Type);
        int totalSize = typeSize * tag.Count;
        if (totalSize <= 4)
            return tag.ValueOffset; // Inline data at the IFD entry position
        // Large data: ValueOffset holds the MN-relative offset (raw from IFD entry)
        return mnTiffBase + tag.ValueOffset;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ARW (Sony) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Sony ARW files. Also handles SR2 and SRF variants.
/// TIFF-based with Sony-specific tags and compression.
/// </summary>
internal static class ArwParser
{
    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid ARW: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid ARW: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.ARW);

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;

        metadata.CfaType = CfaType.Bayer;

        // Sony stores raw data in SubIFD or second IFD
        var rawIfd = ifds.Count > 1 ? ifds[1] : ifd0;
        var subIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian, isBigTiff);
        if (subIfd is not null)
        {
            int subWidth = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            if (subWidth > 0) rawIfd = subIfd;
        }

        int w = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int h = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        if (metadata.ActiveAreaWidth == 0) { metadata.ActiveAreaWidth = w; metadata.ActiveAreaHeight = h; }

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg
            : comp == 32767 ? RawCompression.SonyCompressed
            : RawCompression.Uncompressed;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 14);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ORF (Olympus) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Olympus ORF files. TIFF variant with "IIRO" magic (0x4F52 instead of 0x002A).
/// </summary>
internal static class OrfParser
{
    private const ushort TagMakerNote = 37500;
    private const ushort TagOlympusBlackLevel = 0x1012; // Per-channel black levels (4 SHORTs)
    private const ushort TagOlympusColorMatrix = 0x1011; // 3×3 signed SHORTs (÷256)

    // Image Processing sub-IFD (0x2040) tags — most reliable source for WB
    private const ushort TagImgProcSubIfd = 0x2040;
    private const ushort TagImgProcWBRBLevels = 0x0100; // 2 SHORTs: [R, B] levels
    private const ushort TagImgProcWBGLevel = 0x011F;   // 1 SHORT: G level (typically 256)

    internal static RawSensorData Parse(byte[] data)
    {
        // ORF uses II byte order with magic 0x4F52 ("RO" in little-endian) instead of 42
        bool bigEndian = data[0] == 'M' && data[1] == 'M';
        int magic = bigEndian
            ? (data[2] << 8) | data[3]
            : data[2] | (data[3] << 8);

        if (magic != 0x4F52 && magic != 42) // 0x4F52 = ORF magic, 42 = standard TIFF
            throw new InvalidDataException($"Invalid ORF: unexpected magic {magic:X4}");

        long firstIfdOffset = bigEndian
            ? (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7])
            : (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid ORF: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.ORF);

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;

        metadata.CfaType = CfaType.Bayer;

        // Extract Olympus MakerNote for black levels, WB, and color matrix
        ExtractOlympusMakerNote(data, ifd0, bigEndian, ref metadata);

        // Find the raw data IFD — pick the IFD with the largest width among main IFDs
        var rawIfd = ifd0;
        int bestWidth = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageWidth);
        for (int i = 1; i < ifds.Count; i++)
        {
            int ifdWidth = TiffIfdReader.GetTagInt(ifds[i], TiffIfdReader.TagImageWidth);
            if (ifdWidth > bestWidth)
            {
                bestWidth = ifdWidth;
                rawIfd = ifds[i];
            }
        }

        // Also check SubIFDs
        var allSubIfds = TiffIfdReader.ParseAllSubIfds(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian);
        foreach (var subIfd in allSubIfds)
        {
            int subWidth = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            if (subWidth > bestWidth)
            {
                bestWidth = subWidth;
                rawIfd = subIfd;
            }
        }

        int w = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int h = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg
            : comp == 65535 ? RawCompression.OlympusCompressed
            : RawCompression.Uncompressed;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 12);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        // ORF uncompressed: Olympus often stores 12-bit packed data even though BitsPerSample tag says 16.
        // Detect this by checking if strip bytes match the Olympus 12-bit packed layout:
        // 16-byte blocks containing 10 pixels (15 data bytes + 1 padding byte).
        bool olympus12BitPacked = false;
        if (metadata.Compression == RawCompression.Uncompressed && h > 0 && w > 0)
        {
            var stripCounts = TiffIfdReader.GetTagArray(rawIfd, TiffIfdReader.TagStripByteCounts);
            long totalStripBytes = 0;
            for (int i = 0; i < stripCounts.Length; i++)
                totalStripBytes += (uint)stripCounts[i];

            if (totalStripBytes > 0)
            {
                long expectedOlympus12 = (long)(w / 10) * 16 * h;
                if (w % 10 == 0 && totalStripBytes == expectedOlympus12)
                {
                    // Olympus 12-bit packed format confirmed
                    olympus12BitPacked = true;
                    metadata.BitsPerSample = 12;
                    metadata.WhiteLevel = 4095;
                }
                else
                {
                    // Standard calculation for non-Olympus-packed data
                    int bytesPerPixel = metadata.BitsPerSample <= 8 ? 1 : 2;
                    int computedWidth = (int)(totalStripBytes / h / bytesPerPixel);
                    if (computedWidth > 0 && computedWidth != w)
                        w = computedWidth;
                }
            }
        }

        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        if (metadata.ActiveAreaWidth == 0) { metadata.ActiveAreaWidth = w; metadata.ActiveAreaHeight = h; }

        if (olympus12BitPacked)
        {
            return ExtractOlympus12BitPackedData(data, rawIfd, metadata);
        }

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }

    /// <summary>
    /// Extracts Olympus MakerNote data: per-channel black levels, WB multipliers, and color matrix.
    /// Olympus MakerNote starts with "OLYMP\x00" (6 bytes) then a standard IFD at offset +8.
    /// </summary>
    private static void ExtractOlympusMakerNote(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        try
        {
            ExtractOlympusMakerNoteCore(data, ifd0, bigEndian, ref metadata);
        }
        catch (Exception ex)
        {
            // MakerNote parsing is best-effort; auto WB fallback will handle it
            System.Diagnostics.Debug.WriteLine($"ORF MakerNote error: {ex.Message} at {ex.StackTrace}");
        }
    }

    private static void ExtractOlympusMakerNoteCore(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        var exifIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagExifIFD, bigEndian);
        if (exifIfd is null || !exifIfd.TryGetValue(TagMakerNote, out var mnTag))
            return;

        long mnOffset = mnTag.ValueOffset;
        if (mnOffset <= 0 || mnOffset + 10 >= data.Length) return;

        // Verify Olympus MakerNote header: "OLYMP\x00" or "OLYMPUS\x00"
        if (data[mnOffset] != (byte)'O' || data[mnOffset + 1] != (byte)'L') return;

        // Determine IFD start offset based on header variant:
        // "OLYMP\x00\xNN\xNN" = 8-byte header, "OLYMPUS\x00II\x03\x00" = 12-byte header
        long ifdStart;
        if (mnOffset + 8 < data.Length && data[mnOffset + 5] == 0 && data[mnOffset + 6] != 'U')
            ifdStart = mnOffset + 8;
        else if (mnOffset + 12 < data.Length)
            ifdStart = mnOffset + 12;
        else return;

        var (mnIfd, _) = TiffIfdReader.ParseIfd(data, ifdStart, bigEndian);
        if (mnIfd.Count == 0) return;

        // Tag 0x1012: Per-channel black levels (4 unsigned SHORTs: R, Gr, Gb, B)
        if (mnIfd.TryGetValue(TagOlympusBlackLevel, out var blTag) && blTag.Values.Length >= 4)
        {
            int avgBlack = (blTag.Values[0] + blTag.Values[1] + blTag.Values[2] + blTag.Values[3]) / 4;
            if (avgBlack > 0)
                metadata.BlackLevel = avgBlack;
        }

        // White balance: prefer Image Processing sub-IFD (0x2040) which has R, B, and G levels.
        // The main MakerNote tags 0x1017/0x1018 only have R and G — no blue, requiring a bad heuristic.
        if (mnIfd.TryGetValue(TagImgProcSubIfd, out var ipTag))
        {
            // Tag 0x2040 may be LONG (standard sub-IFD pointer where Values[0] = offset) or
            // UNDEFINED (Olympus stores sub-IFD data directly; Values[0] = first raw byte, wrong!).
            // For UNDEFINED type, ValueOffset is the file position where data starts = sub-IFD offset.
            long ipOffset = ipTag.Type == 7 ? (long)ipTag.ValueOffset
                : (ipTag.Values.Length > 0 ? ipTag.Values[0] : (long)ipTag.ValueOffset);
            if (ipOffset > 0 && ipOffset + 2 < data.Length)
            {
                try
                {
                    var (ipIfd, _) = TiffIfdReader.ParseIfd(data, ipOffset, bigEndian);
                    if (ipIfd.Count > 0)
                    {
                        // Tag 0x0100: WB_RBLevels [R, B] relative to G level
                        // Tag 0x011F: WB_GLevel (typically 256)
                        if (ipIfd.TryGetValue(TagImgProcWBRBLevels, out var wbRBTag) && wbRBTag.Values.Length >= 2)
                        {
                            double wbR = wbRBTag.Values[0];
                            double wbB = wbRBTag.Values[1];
                            double wbG = 256.0; // default
                            if (ipIfd.TryGetValue(TagImgProcWBGLevel, out var wbGTag) && wbGTag.Values.Length >= 1 && wbGTag.Values[0] > 0)
                                wbG = wbGTag.Values[0];

                            if (wbR > 0 && wbB > 0 && wbG > 0)
                                metadata.AsShotWhiteBalance = [wbR / wbG, 1.0, wbB / wbG];
                        }

                        // Tag 0x0600: Per-channel black levels from Image Processing IFD
                        if (ipIfd.TryGetValue(0x0600, out var ipBlTag) && ipBlTag.Values.Length >= 4)
                        {
                            int ipAvgBlack = (ipBlTag.Values[0] + ipBlTag.Values[1] + ipBlTag.Values[2] + ipBlTag.Values[3]) / 4;
                            if (ipAvgBlack > metadata.BlackLevel)
                                metadata.BlackLevel = ipAvgBlack;
                        }
                    }
                }
                catch { /* Image Processing sub-IFD parsing is best-effort */ }
            }
        }

        // Don't use Olympus MakerNote color matrix (tag 0x1011) — its format (signed shorts/256)
        // differs from dcraw's adobe_coeff matrices and produces inconsistent results.
        // Let the dcraw matrix lookup provide the color matrix instead.
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// RW2 (Panasonic) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Panasonic RW2 files. TIFF variant with "IIU\x00" magic and 0x55 identifier.
/// </summary>
internal static class Rw2Parser
{
    internal static RawSensorData Parse(byte[] data)
    {
        // RW2 header: II + 0x55 0x00 (little-endian)
        bool bigEndian = false;
        long firstIfdOffset = TiffIfdReader.ReadUInt32(data, 4, bigEndian);

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid RW2: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.RW2);

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;

        metadata.CfaType = CfaType.Bayer;

        // Panasonic stores dimensions in proprietary tags, NOT standard TIFF 0x0100/0x0101
        const ushort tagPanasonicSensorWidth = 0x0002;
        const ushort tagPanasonicSensorHeight = 0x0003;
        const ushort tagPanasonicActiveWidth = 0x0007;
        const ushort tagPanasonicActiveHeight = 0x0006;
        const ushort tagPanasonicRawOffset = 0x0118;

        int w = TiffIfdReader.GetTagInt(ifd0, tagPanasonicSensorWidth, 0);
        if (w == 0) w = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageWidth, 0);
        int h = TiffIfdReader.GetTagInt(ifd0, tagPanasonicSensorHeight, 0);
        if (h == 0) h = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagImageLength, 0);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;

        int aw = TiffIfdReader.GetTagInt(ifd0, tagPanasonicActiveWidth, 0);
        int ah = TiffIfdReader.GetTagInt(ifd0, tagPanasonicActiveHeight, 0);
        metadata.ActiveAreaWidth = aw > 0 ? aw : w;
        metadata.ActiveAreaHeight = ah > 0 ? ah : h;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(ifd0, TiffIfdReader.TagBitsPerSample, 12);
        metadata.Compression = RawCompression.PanasonicCompressed;
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        // Note: ActiveAreaLeft/Top are NOT set here because the Panasonic decompressor
        // already produces only the active area (aw × ah pixels), not the full sensor
        // (w × h). Setting margins from the difference would cause ProcessRawData to
        // incorrectly sample real image data as "border" masked pixels.

        // Panasonic standard optical black level: ~160 for 12-bit, ~640 for 14-bit.
        // Measured from real samples (rawpy reports per-channel: [163, 158, 157, 158] for 12-bit).
        if (metadata.BlackLevel == 0)
            metadata.BlackLevel = metadata.BitsPerSample >= 14 ? 640 : 160;

        int rawOffset = TiffIfdReader.GetTagInt(ifd0, tagPanasonicRawOffset, 0);
        if (rawOffset > 0)
        {
            metadata.RawDataOffset = rawOffset;
            metadata.RawDataLength = data.Length - rawOffset;
        }

        // Panasonic stores WB as separate R/G/B level tags in the main IFD
        const ushort tagPanasonicWBRed = 0x0024;
        const ushort tagPanasonicWBGreen = 0x0025;
        const ushort tagPanasonicWBBlue = 0x0026;
        int wbRed = TiffIfdReader.GetTagInt(ifd0, tagPanasonicWBRed, 0);
        int wbGreen = TiffIfdReader.GetTagInt(ifd0, tagPanasonicWBGreen, 0);
        int wbBlue = TiffIfdReader.GetTagInt(ifd0, tagPanasonicWBBlue, 0);
        if (wbGreen > 0 && wbRed > 0 && wbBlue > 0)
        {
            metadata.AsShotWhiteBalance = [(double)wbRed / wbGreen, 1.0, (double)wbBlue / wbGreen];
        }

        return ExtractSensorData(data, ifd0, bigEndian, metadata);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// RAF (Fuji) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Fuji RAF files. Proprietary format with "FUJIFILMCCD-RAW" header.
/// Uses X-Trans 6×6 CFA pattern (not standard Bayer) on newer cameras.
/// </summary>
internal static class RafParser
{
    internal static RawSensorData Parse(byte[] data)
    {
        // RAF header: "FUJIFILMCCD-RAW " (16 bytes)
        if (data.Length < 148)
            throw new InvalidDataException("Invalid RAF: file too short");

        string magic = Encoding.ASCII.GetString(data, 0, 16);
        if (!magic.StartsWith("FUJIFILMCCD-RAW"))
            throw new InvalidDataException("Invalid RAF: missing FUJIFILMCCD-RAW header");

        // Camera model at offset 28 (32 bytes)
        string model = Encoding.ASCII.GetString(data, 28, 32).TrimEnd('\0').Trim();

        // RAF directory: offset to JPEG preview, raw data at specific offsets
        // Byte 84-87: JPEG offset, 88-91: JPEG length
        // Byte 92-95: CFA header offset, 96-99: CFA header length
        // Byte 100-103: CFA data offset, 104-107: CFA data length
        uint jpegOffset = ReadBE32(data, 84);
        uint jpegLength = ReadBE32(data, 88);
        uint cfaHeaderOffset = ReadBE32(data, 92);
        uint cfaHeaderLength = ReadBE32(data, 96);
        uint cfaDataOffset = ReadBE32(data, 100);
        uint cfaDataLength = ReadBE32(data, 104);

        // Parse CFA header entries for dimensions and metadata
        // CFA header format: 4-byte entry count (BE), then entries: 2-byte tag, 2-byte size, N-byte data
        int width = 0, height = 0;
        int activeWidth = 0, activeHeight = 0;
        int bitsPerSample = 14;
        double[]? rafWbMultipliers = null;
        byte[,]? fileCfaPattern = null; // Raw 6×6 X-Trans CFA from tag 0x0131
        if (cfaHeaderOffset > 0 && cfaHeaderOffset + 8 <= data.Length)
        {
            uint entryCount = ReadBE32(data, (int)cfaHeaderOffset);
            int pos = (int)cfaHeaderOffset + 4;
            for (uint i = 0; i < entryCount && pos + 4 <= data.Length; i++)
            {
                ushort tag = (ushort)((data[pos] << 8) | data[pos + 1]);
                ushort sz = (ushort)((data[pos + 2] << 8) | data[pos + 3]);
                if (pos + 4 + sz > data.Length) break;

                if (tag == 0x0100 && sz >= 4) // Full sensor dimensions (height, width as BE16 pairs)
                {
                    height = (data[pos + 4] << 8) | data[pos + 5];
                    width = (data[pos + 6] << 8) | data[pos + 7];
                }
                else if (tag == 0x0111 && sz >= 4) // Active area dimensions (height, width)
                {
                    activeHeight = (data[pos + 4] << 8) | data[pos + 5];
                    activeWidth = (data[pos + 6] << 8) | data[pos + 7];
                }
                else if (tag == 0x0130 && sz >= 1) // Bits per sample
                {
                    bitsPerSample = data[pos + 4];
                }
                else if (tag == 0x2FF0 && sz >= 8) // WB multipliers (dcraw: cam_mul[c^1] = get2())
                {
                    // 4 big-endian shorts: stored as G1, R, G2, B with c^1 swizzle
                    int v0 = (data[pos + 4] << 8) | data[pos + 5]; // → cam_mul[1] = G1
                    int v1 = (data[pos + 6] << 8) | data[pos + 7]; // → cam_mul[0] = R
                    int v2 = (data[pos + 8] << 8) | data[pos + 9]; // → cam_mul[3] = G2
                    int v3 = (data[pos + 10] << 8) | data[pos + 11]; // → cam_mul[2] = B
                    double wbR = v1, wbG = (v0 + v2) / 2.0, wbB = v3;
                    if (wbG > 0 && wbR > 0 && wbB > 0)
                        rafWbMultipliers = [wbR / wbG, 1.0, wbB / wbG];
                }
                else if (tag == 0xC000 && sz >= 10) // Extended data block — search for WB pattern
                {
                    var wb = SearchFuji0xC000ForWb(data, pos + 4, sz);
                    if (wb is not null)
                        rafWbMultipliers = wb;
                }
                else if (tag == 0x0131 && sz >= 36) // X-Trans CFA pattern: 6×6 grid of color indices
                {
                    fileCfaPattern = new byte[6, 6];
                    for (int r = 0; r < 6; r++)
                        for (int c = 0; c < 6; c++)
                            fileCfaPattern[r, c] = data[pos + 4 + r * 6 + c];
                }

                pos += 4 + sz;
            }
        }

        // Prefer active area if available, fall back to full sensor
        if (activeWidth <= 0 || activeHeight <= 0)
        {
            activeWidth = width;
            activeHeight = height;
        }

        // Compute active area offsets from full sensor vs active area dimensions.
        // Fuji sensors have borders on all sides; margins are symmetric.
        int activeAreaLeft = 0;
        int activeAreaTop = 0;
        if (activeHeight > 0 && activeHeight < height)
            activeAreaTop = (height - activeHeight) / 2;
        if (activeWidth > 0 && activeWidth < width)
            activeAreaLeft = (width - activeWidth) / 2;

        // Compute effective CFA pattern for the active area.
        // CRITICAL: Use the standard X-Trans CFA pattern, NOT the file's tag 0x0131.
        // Tag 0x0131 stores a representation of the CFA that uses a different color
        // numbering or orientation than the raw pixel data layout. libraw/rawpy always
        // use the hardcoded standard X-Trans pattern (matching XTransCfa.Pattern) which
        // aligns correctly with the actual sensor mosaic.
        byte[,]? effectiveCfa = null;
        {
            effectiveCfa = new byte[6, 6];
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 6; c++)
                    effectiveCfa[r, c] = XTransCfa.Pattern[(r + activeAreaTop) % 6, (c + activeAreaLeft) % 6];
        }

        // Determine if X-Trans or Bayer
        bool isXTrans = model.Contains("X-T", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("X-Pro", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("X-H", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("X-E", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("X-S", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("X100", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("GFX", StringComparison.OrdinalIgnoreCase);

        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.RAF,
            Make = "FUJIFILM",
            Model = model,
            SensorWidth = width,
            SensorHeight = height,
            ActiveAreaLeft = activeAreaLeft,
            ActiveAreaTop = activeAreaTop,
            ActiveAreaWidth = activeWidth,
            ActiveAreaHeight = activeHeight,
            CfaType = isXTrans ? CfaType.XTrans : CfaType.Bayer,
            BayerPattern = isXTrans ? BayerPattern.Unknown : BayerPattern.RGGB,
            BitsPerSample = bitsPerSample,
            Compression = RawCompression.FujiCompressed,
            WhiteLevel = (1 << bitsPerSample) - 1,
            Orientation = 1,
            ThumbnailOffset = jpegOffset,
            ThumbnailLength = jpegLength,
            RawDataOffset = cfaDataOffset,
            RawDataLength = cfaDataLength
        };

        // Try to parse embedded TIFF for EXIF metadata
        if (jpegOffset > 0 && jpegOffset + 12 <= data.Length)
        {
            TryExtractExifFromJpeg(data, (int)jpegOffset, (int)jpegLength, ref metadata);
        }

        if (rafWbMultipliers is not null)
        {
            metadata.AsShotWhiteBalance = rafWbMultipliers;
        }

        // Use active area for extraction if available
        int extractWidth = activeWidth > 0 ? activeWidth : width;
        int extractHeight = activeHeight > 0 ? activeHeight : height;
        return ExtractRafSensorData(data, cfaDataOffset, cfaDataLength, extractWidth, extractHeight, metadata, effectiveCfa);
    }

    private static void TryExtractExifFromJpeg(byte[] data, int offset, int length, ref CameraRawMetadata metadata)
    {
        // Parse EXIF from JPEG APP1 segment, including Fuji MakerNote for WB.
        if (offset + 4 >= data.Length) return;
        if (data[offset] != 0xFF || data[offset + 1] != 0xD8) return;

        int pos = offset + 2;
        int end = offset + Math.Min(length, data.Length - offset);
        while (pos + 4 < end)
        {
            if (data[pos] != 0xFF) { pos++; continue; }
            byte marker = data[pos + 1];
            if (marker == 0xE1) // APP1 = EXIF
            {
                int segLen = (data[pos + 2] << 8) | data[pos + 3];
                int exifStart = pos + 4;
                if (exifStart + 6 < end && Encoding.ASCII.GetString(data, exifStart, 4) == "Exif")
                {
                    int tiffStart = exifStart + 6; // Skip "Exif\0\0"
                    // Use full remaining file data — Fuji MakerNote 0xC000 block offsets
                    // reference data beyond the JPEG segment boundary
                    byte[] tiffData = data[tiffStart..];
                    if (TiffIfdReader.ParseHeader(tiffData, out bool be, out _, out long ifdOff))
                    {
                        var tags = TiffIfdReader.ParseIfd(tiffData, ifdOff, be).tags;
                        string? make = TiffIfdReader.GetTagString(tiffData, tags, TiffIfdReader.TagMake, be);
                        if (make is not null) metadata.Make = make;

                        // Navigate IFD0 → ExifIFD → MakerNote → Fuji 0xC000 for WB
                        TryExtractFujiMakerNoteWb(tiffData, tags, be, ref metadata);
                    }
                }
                break;
            }
            int mLen = (data[pos + 2] << 8) | data[pos + 3];
            pos += 2 + mLen;
        }
    }

    /// <summary>
    /// Navigates EXIF IFD0 → ExifIFD → MakerNote → Fuji 0xC000 extended block to extract camera WB.
    /// Fuji cameras store WB as 3 LE shorts [G, R, B] in the 0xC000 block, followed by zeros.
    /// </summary>
    private static void TryExtractFujiMakerNoteWb(byte[] tiffData, Dictionary<ushort, TiffTag> ifd0,
        bool bigEndian, ref CameraRawMetadata metadata)
    {
        try
        {
            // IFD0 → ExifIFD (tag 0x8769)
            const ushort tagExifIfd = 0x8769;
            if (!ifd0.TryGetValue(tagExifIfd, out var exifIfdTag) || exifIfdTag.Values.Length == 0)
                return;

            long exifIfdOffset = exifIfdTag.Values[0];
            if (exifIfdOffset <= 0 || exifIfdOffset + 2 >= tiffData.Length) return;

            var exifIfd = TiffIfdReader.ParseIfd(tiffData, exifIfdOffset, bigEndian).tags;

            // ExifIFD → MakerNote (tag 0x927C)
            const ushort tagMakerNote = 0x927C;
            if (!exifIfd.TryGetValue(tagMakerNote, out var mnTag))
                return;

            long mnOffset = mnTag.ValueOffset;
            if (mnOffset <= 0 || mnOffset + 12 >= tiffData.Length)
                return;
            if (tiffData[mnOffset] != (byte)'F' || tiffData[mnOffset + 1] != (byte)'U' ||
                tiffData[mnOffset + 2] != (byte)'J' || tiffData[mnOffset + 3] != (byte)'I')
                return;

            long mnBase = mnOffset; // All offsets in Fuji MakerNote are relative to "FUJIFILM"
            long fujiIfdOffset = mnBase + (tiffData[mnOffset + 8] | (tiffData[mnOffset + 9] << 8) |
                                           (tiffData[mnOffset + 10] << 16) | (tiffData[mnOffset + 11] << 24));

            if (fujiIfdOffset + 2 >= tiffData.Length) return;

            // Parse Fuji MakerNote IFD (always little-endian)
            int entryCount = tiffData[fujiIfdOffset] | (tiffData[fujiIfdOffset + 1] << 8);
            long entryPos = fujiIfdOffset + 2;
            for (int i = 0; i < entryCount && entryPos + 12 <= tiffData.Length; i++, entryPos += 12)
            {
                ushort tag = (ushort)(tiffData[entryPos] | (tiffData[entryPos + 1] << 8));

                if (tag == 0xC000) // Extended data block
                {
                    ushort type = (ushort)(tiffData[entryPos + 2] | (tiffData[entryPos + 3] << 8));
                    int count = tiffData[entryPos + 4] | (tiffData[entryPos + 5] << 8) |
                                (tiffData[entryPos + 6] << 16) | (tiffData[entryPos + 7] << 24);
                    int typeSize = type switch { 1 => 1, 3 => 2, 4 => 4, 7 => 1, _ => 1 };
                    int totalSize = count * typeSize;

                    long blockOffset;
                    if (totalSize <= 4)
                        blockOffset = entryPos + 8;
                    else
                        blockOffset = mnBase + (tiffData[entryPos + 8] | (tiffData[entryPos + 9] << 8) |
                                                (tiffData[entryPos + 10] << 16) | (tiffData[entryPos + 11] << 24));

                    if (blockOffset > 0 && blockOffset + totalSize <= tiffData.Length)
                    {
                        var wb = SearchFuji0xC000ForWb(tiffData, (int)blockOffset, totalSize);
                        if (wb is not null)
                            metadata.AsShotWhiteBalance = wb;
                    }
                    break;
                }
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Searches a Fuji 0xC000 extended block for WB values.
    /// Pattern: 3 consecutive LE unsigned shorts [G, R, B] where all are 100-2000,
    /// followed by at least 2 shorts that are 0 or near-zero.
    /// </summary>
    private static double[]? SearchFuji0xC000ForWb(byte[] data, int blockOffset, int blockLength)
    {
        if (blockLength < 10) return null;

        int searchEnd = blockOffset + blockLength - 10;
        for (int i = blockOffset; i <= searchEnd; i += 2)
        {
            ushort v0 = (ushort)(data[i] | (data[i + 1] << 8));
            ushort v1 = (ushort)(data[i + 2] | (data[i + 3] << 8));
            ushort v2 = (ushort)(data[i + 4] | (data[i + 5] << 8));
            ushort v3 = (ushort)(data[i + 6] | (data[i + 7] << 8));
            ushort v4 = (ushort)(data[i + 8] | (data[i + 9] << 8));

            // Pattern: [G, R, B, 0, 0] — all WB values between 100-2000, trailing zeros
            if (v0 >= 100 && v0 <= 2000 &&
                v1 >= 100 && v1 <= 2000 &&
                v2 >= 100 && v2 <= 2000 &&
                v3 <= 10 && v4 <= 10)
            {
                double wbG = v0, wbR = v1, wbB = v2;
                double r = wbR / wbG;
                double b = wbB / wbG;

                if (r >= 0.5 && r <= 4.0 && b >= 0.5 && b <= 4.0)
                    return [r, 1.0, b];
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadBE32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static RawSensorData ExtractRafSensorData(byte[] data, uint offset, uint length,
        int width, int height, CameraRawMetadata metadata, byte[,]? effectiveCfa = null)
    {
        // Modern Fuji cameras (X-T3, X-T4, GFX, etc.) embed a TIFF inside the CFA data area.
        // Detect by checking for TIFF header at the CFA data offset.
        if (offset + 8 <= data.Length &&
            ((data[offset] == 0x49 && data[offset + 1] == 0x49 && data[offset + 2] == 0x2A && data[offset + 3] == 0x00) ||
             (data[offset] == 0x4D && data[offset + 1] == 0x4D && data[offset + 2] == 0x00 && data[offset + 3] == 0x2A)))
        {
            return ExtractRafEmbeddedTiff(data, offset, length, width, height, metadata, effectiveCfa);
        }

        // Legacy Fuji cameras: raw pixel data packed directly
        if (width <= 0 || height <= 0 || offset + length > data.Length)
        {
            long totalPixels = length * 8 / metadata.BitsPerSample;
            if (width <= 0)
            {
                width = (int)Math.Sqrt(totalPixels * 3.0 / 2.0);
                width = (width + 15) & ~15;
                height = (int)(totalPixels / width);
            }
            metadata.SensorWidth = width;
            metadata.SensorHeight = height;
            metadata.ActiveAreaWidth = width;
            metadata.ActiveAreaHeight = height;
        }

        var pixels = new ushort[width * height];
        UnpackBits(data, (int)offset, (int)length, pixels, metadata.BitsPerSample);

        // Set black level for legacy Fuji sensors if not already set
        if (metadata.BlackLevel == 0)
            metadata.BlackLevel = metadata.BitsPerSample >= 14 ? 1022 : 64;

        return new RawSensorData
        {
            RawPixels = pixels,
            Width = width,
            Height = height,
            Metadata = metadata,
            XTransCfaEffective = effectiveCfa
        };
    }

    /// <summary>
    /// Extracts raw sensor data from an embedded TIFF inside a Fuji RAF CFA data block.
    /// Modern Fuji cameras (X-T3+) wrap the raw CFA data in a TIFF container with
    /// Fuji-proprietary tags (0xF000-0xF012) instead of standard TIFF image data tags.
    /// </summary>
    private static RawSensorData ExtractRafEmbeddedTiff(byte[] data, uint cfaOffset, uint cfaLength,
        int width, int height, CameraRawMetadata metadata, byte[,]? effectiveCfa = null)
    {
        byte[] tiffData = new byte[cfaLength];
        Buffer.BlockCopy(data, (int)cfaOffset, tiffData, 0, (int)cfaLength);

        if (!TiffIfdReader.ParseHeader(tiffData, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid RAF embedded TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(tiffData, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("RAF embedded TIFF has no IFDs");

        // Fuji embedded TIFFs use tag 0xF000 (type=IFD) pointing to a sub-IFD
        // with proprietary tags for dimensions, bit depth, and data location:
        //   0xF001 = sensor width
        //   0xF002 = sensor height
        //   0xF003 = bits per sample (14 for most modern Fuji)
        //   0xF007 = pixel data start offset within this TIFF block
        //   0xF008 = pixel data byte count
        const ushort tagFujiIfd = 0xF000;
        const ushort tagFujiWidth = 0xF001;
        const ushort tagFujiHeight = 0xF002;
        const ushort tagFujiBps = 0xF003;
        const ushort tagFujiDataOffset = 0xF007;
        const ushort tagFujiDataLength = 0xF008;

        // Try to parse Fuji sub-IFD
        var fujiSubIfds = TiffIfdReader.ParseAllSubIfds(tiffData, ifds[0], tagFujiIfd, bigEndian, isBigTiff);
        Dictionary<ushort, TiffTag>? fujiIfd = fujiSubIfds.Count > 0 ? fujiSubIfds[0] : null;

        if (fujiIfd is not null)
        {
            int fujiW = TiffIfdReader.GetTagInt(fujiIfd, tagFujiWidth, width);
            int fujiH = TiffIfdReader.GetTagInt(fujiIfd, tagFujiHeight, height);
            int fujiBps = TiffIfdReader.GetTagInt(fujiIfd, tagFujiBps, 14);
            int fujiDataOff = TiffIfdReader.GetTagInt(fujiIfd, tagFujiDataOffset, 0);
            int fujiDataLen = TiffIfdReader.GetTagInt(fujiIfd, tagFujiDataLength, 0);

            if (fujiW > 0 && fujiH > 0)
            {
                metadata.SensorWidth = fujiW;
                metadata.SensorHeight = fujiH;
                metadata.BitsPerSample = fujiBps;
                metadata.WhiteLevel = (1 << fujiBps) - 1;
                metadata.Compression = RawCompression.Uncompressed;

                // Fuji stores pixel data as 16-bit LE values (2 bytes per pixel)
                // starting at the offset specified by tag 0xF007
                int pixelCount = fujiW * fujiH;
                var pixels = new ushort[pixelCount];

                if (fujiDataOff > 0 && fujiDataOff + pixelCount * 2 <= tiffData.Length)
                {
                    // Data is stored as 16-bit LE values with only fujiBps meaningful bits
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int bytePos = fujiDataOff + i * 2;
                        pixels[i] = (ushort)(tiffData[bytePos] | (tiffData[bytePos + 1] << 8));
                    }
                }
                else
                {
                    // Fallback: try reading from right after the TIFF header metadata
                    int headerSize = fujiDataOff > 0 ? fujiDataOff : 2048;
                    int available = (int)cfaLength - headerSize;
                    if (available >= pixelCount * 2)
                    {
                        for (int i = 0; i < pixelCount; i++)
                        {
                            int bytePos = headerSize + i * 2;
                            pixels[i] = (ushort)(tiffData[bytePos] | (tiffData[bytePos + 1] << 8));
                        }
                    }
                    else
                    {
                        UnpackBits(tiffData, headerSize, available, pixels, fujiBps);
                    }
                }

                // Standard Fuji black level (1022 for 14-bit, ~64 for 12-bit).
                // The embedded TIFF pixel data contains only the active sensor area — there
                // are no optically masked border rows available, so we must use the known
                // nominal black level rather than trying to compute from non-existent dark pixels.
                if (metadata.BlackLevel == 0)
                    metadata.BlackLevel = fujiBps >= 14 ? 1022 : 64;

                // Update active area from RAF header if available
                if (metadata.ActiveAreaWidth == 0 || metadata.ActiveAreaWidth > fujiW)
                    metadata.ActiveAreaWidth = fujiW;
                if (metadata.ActiveAreaHeight == 0 || metadata.ActiveAreaHeight > fujiH)
                    metadata.ActiveAreaHeight = fujiH;

                return new RawSensorData
                {
                    RawPixels = pixels,
                    Width = fujiW,
                    Height = fujiH,
                    Metadata = metadata,
                    XTransCfaEffective = effectiveCfa
                };
            }
        }

        // Fallback: try standard TIFF image data tags (rare for modern Fuji)
        var rawIfd = ifds[0];
        int bestPixels = 0;
        foreach (var ifd in ifds)
        {
            int iw = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageWidth);
            int ih = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageLength);
            if ((long)iw * ih > bestPixels)
            {
                bestPixels = iw * ih;
                rawIfd = ifd;
            }
        }

        int tiffWidth = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth);
        int tiffHeight = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength);
        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        int bps = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, metadata.BitsPerSample);

        if (tiffWidth > 0 && tiffHeight > 0)
        {
            metadata.SensorWidth = tiffWidth;
            metadata.SensorHeight = tiffHeight;
            metadata.ActiveAreaWidth = tiffWidth;
            metadata.ActiveAreaHeight = tiffHeight;
            metadata.BitsPerSample = bps;
        }

        if (comp == 7)
            metadata.Compression = RawCompression.LosslessJpeg;
        else if (comp == 1)
            metadata.Compression = RawCompression.Uncompressed;

        return ExtractSensorData(tiffData, rawIfd, bigEndian, metadata);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PEF (Pentax) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Pentax PEF files. Standard TIFF (big or little-endian) with Pentax MakerNote.
/// </summary>
internal static class PefParser
{
    private const ushort TagMakerNote = 37500;

    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid PEF: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid PEF: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.PEF);
        metadata.BigEndian = bigEndian;

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.BGGR;

        metadata.CfaType = CfaType.Bayer;

        var rawIfd = ifd0;
        foreach (var ifd in ifds)
        {
            int ifdW = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageWidth);
            int currW = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth);
            if (ifdW > currW) rawIfd = ifd;
        }

        int w = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int h = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        if (metadata.ActiveAreaWidth == 0) { metadata.ActiveAreaWidth = w; metadata.ActiveAreaHeight = h; }

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg
            : comp == 65535 ? RawCompression.PentaxCompressed
            : RawCompression.Uncompressed;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 12);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        // Pentax standard optical black level: ~64 for both 12-bit and 14-bit sensors.
        if (metadata.BlackLevel == 0)
            metadata.BlackLevel = 64;

        // Extract Pentax MakerNote meta_offset for Huffman decode table (MN tag 0x220)
        ExtractPentaxMakerNoteMetaOffset(data, ifd0, bigEndian, ref metadata);

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }

    /// <summary>
    /// Finds the Pentax MakerNote and extracts meta_offset — the file position of
    /// the Huffman decode table (MN tag 0x220). The Pentax decoder reads the variable-length
    /// Huffman table from this offset during decompression.
    /// </summary>
    private static void ExtractPentaxMakerNoteMetaOffset(byte[] data,
        Dictionary<ushort, TiffTag> ifd0, bool bigEndian, ref CameraRawMetadata metadata)
    {
        try
        {
            var exifIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagExifIFD, bigEndian);
            if (exifIfd is null || !exifIfd.TryGetValue(TagMakerNote, out var mnTag))
                return;

            long mnOffset = TiffIfdReader.GetTagLargeDataOffset(mnTag);
            if (mnOffset <= 0 || mnOffset + 10 >= data.Length) return;

            // Pentax MakerNote: "AOC\x00" (4 bytes) + byte order (2 bytes: "MM" or "II")
            // Then IFD entries follow. Offsets in IFD tags are absolute file offsets (base=0).
            bool isPentaxMn = data[mnOffset] == (byte)'A' && data[mnOffset + 1] == (byte)'O' &&
                              data[mnOffset + 2] == (byte)'C' && data[mnOffset + 3] == 0x00;
            if (!isPentaxMn) return;

            bool mnBigEndian = data[mnOffset + 4] == (byte)'M' && data[mnOffset + 5] == (byte)'M';

            // IFD starts at mnOffset + 6 (right after "AOC\x00MM")
            long ifdStart = mnOffset + 6;
            if (ifdStart + 2 >= data.Length) return;

            var (mnIfd, _) = TiffIfdReader.ParseIfd(data, ifdStart, mnBigEndian);

            // Tag 0x0201: WB_RGGBLevels — 4 unsigned SHORTs [R, Gr, Gb, B]
            const ushort tagPentaxWBLevels = 0x0201;
            if (mnIfd.TryGetValue(tagPentaxWBLevels, out var wbTag) && wbTag.Count >= 4)
            {
                // Read 4 SHORT values from the tag data
                int typeSize = TiffIfdReader.GetTypeSize(wbTag.Type);
                long wbDataOff;
                if (typeSize * wbTag.Count <= 4)
                    wbDataOff = -1; // inline (unlikely for 4 shorts = 8 bytes)
                else
                    wbDataOff = wbTag.ValueOffset; // Pentax MN uses absolute offsets

                if (wbDataOff > 0 && wbDataOff + 8 <= data.Length)
                {
                    int wbR = mnBigEndian
                        ? (data[wbDataOff] << 8) | data[wbDataOff + 1]
                        : data[wbDataOff] | (data[wbDataOff + 1] << 8);
                    int wbGr = mnBigEndian
                        ? (data[wbDataOff + 2] << 8) | data[wbDataOff + 3]
                        : data[wbDataOff + 2] | (data[wbDataOff + 3] << 8);
                    int wbGb = mnBigEndian
                        ? (data[wbDataOff + 4] << 8) | data[wbDataOff + 5]
                        : data[wbDataOff + 4] | (data[wbDataOff + 5] << 8);
                    int wbB = mnBigEndian
                        ? (data[wbDataOff + 6] << 8) | data[wbDataOff + 7]
                        : data[wbDataOff + 6] | (data[wbDataOff + 7] << 8);

                    double greenAvg = (wbGr + wbGb) / 2.0;
                    if (greenAvg > 0 && wbR > 0 && wbB > 0)
                    {
                        metadata.AsShotWhiteBalance = [wbR / greenAvg, 1.0, wbB / greenAvg];
                    }
                }
            }

            const ushort tagPentaxHuffman = 0x220;
            if (mnIfd.TryGetValue(tagPentaxHuffman, out var huffTag))
            {
                // Tag 0x220 is type 7 (UNDEFINED), count=59 for typical Pentax cameras
                // Pentax MN offsets are absolute file offsets (base=0)
                // ValueOffset holds the raw offset from the IFD entry — already absolute
                int typeSize = TiffIfdReader.GetTypeSize(huffTag.Type);
                int totalSize = typeSize * huffTag.Count;
                if (totalSize > 4)
                    metadata.MetaOffset = huffTag.ValueOffset; // Already absolute
                else
                    metadata.MetaOffset = huffTag.ValueOffset; // Inline at IFD entry
            }
        }
        catch
        {
            // MakerNote parsing is best-effort
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// SRW (Samsung) Parser
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Parses Samsung SRW files. TIFF-based with Samsung-specific tags.
/// </summary>
internal static class SrwParser
{
    internal static RawSensorData Parse(byte[] data)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException("Invalid SRW: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException("Invalid SRW: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, CameraRawFormat.SRW);

        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.GRBG; // Samsung NX commonly uses GRBG

        metadata.CfaType = CfaType.Bayer;

        var rawIfd = ifd0;
        var subIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian, isBigTiff);
        if (subIfd is not null)
        {
            int subWidth = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            if (subWidth > 0) rawIfd = subIfd;
        }

        int w = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int h = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        if (metadata.ActiveAreaWidth == 0) { metadata.ActiveAreaWidth = w; metadata.ActiveAreaHeight = h; }

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg
            : comp == 32769 || comp == 32770 ? RawCompression.SamsungCompressed
            : RawCompression.Uncompressed;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 14);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Remaining Format Parsers (grouped by similarity)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Generic TIFF-based camera raw parser for formats that follow standard TIFF structure
/// with minimal proprietary extensions. Covers: CRW, DCR, K25, KDC, MDC, MRW, MEF,
/// MOS, RWL, ERF, FFF, STI, RMF, 3FR, IIQ, DCRAW, NRW.
/// </summary>
internal static class GenericRawParser
{
    internal static RawSensorData Parse(byte[] data, CameraRawFormat format)
    {
        // Most of these are TIFF-based
        if (format == CameraRawFormat.CRW)
            return ParseCrw(data);
        if (format == CameraRawFormat.X3F)
            return ParseX3f(data);
        if (format == CameraRawFormat.MRW)
            return ParseMrw(data);

        // All other formats: standard TIFF parsing
        return ParseTiffBased(data, format);
    }

    private static RawSensorData ParseTiffBased(byte[] data, CameraRawFormat format)
    {
        if (!TiffIfdReader.ParseHeader(data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset))
            throw new InvalidDataException($"Invalid {format}: not a valid TIFF header");

        var ifds = TiffIfdReader.ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        if (ifds.Count == 0)
            throw new InvalidDataException($"Invalid {format}: no IFDs found");

        var ifd0 = ifds[0];
        var metadata = ExtractCommonTiffMetadata(data, ifd0, bigEndian, format);
        if (metadata.BayerPattern == BayerPattern.Unknown)
            metadata.BayerPattern = BayerPattern.RGGB;
        metadata.CfaType = CfaType.Bayer;

        // Find the largest IFD for raw data
        var rawIfd = ifd0;
        int maxWidth = 0;
        foreach (var ifd in ifds)
        {
            int w = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageWidth);
            if (w > maxWidth) { maxWidth = w; rawIfd = ifd; }
        }

        // Also check SubIFDs
        var subIfd = TiffIfdReader.ParseSubIfd(data, ifd0, TiffIfdReader.TagSubIFDs, bigEndian, isBigTiff);
        if (subIfd is not null)
        {
            int subW = TiffIfdReader.GetTagInt(subIfd, TiffIfdReader.TagImageWidth);
            if (subW > maxWidth) rawIfd = subIfd;
        }

        int width = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageWidth, metadata.SensorWidth);
        int height = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagImageLength, metadata.SensorHeight);
        metadata.SensorWidth = width;
        metadata.SensorHeight = height;
        if (metadata.ActiveAreaWidth == 0) { metadata.ActiveAreaWidth = width; metadata.ActiveAreaHeight = height; }

        int comp = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagCompression, 1);
        metadata.Compression = comp == 7 ? RawCompression.LosslessJpeg : RawCompression.Uncompressed;
        metadata.BitsPerSample = TiffIfdReader.GetTagInt(rawIfd, TiffIfdReader.TagBitsPerSample, 14);
        if (metadata.WhiteLevel == 0) metadata.WhiteLevel = (1 << metadata.BitsPerSample) - 1;

        return ExtractSensorData(data, rawIfd, bigEndian, metadata);
    }

    private static RawSensorData ParseCrw(byte[] data)
    {
        // Canon CRW uses CIFF (Camera Image File Format) — not TIFF
        // Parse the CIFF heap structure
        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.CRW,
            Make = "Canon",
            BayerPattern = BayerPattern.RGGB,
            CfaType = CfaType.Bayer,
            BitsPerSample = 10,
            Compression = RawCompression.Uncompressed,
            Orientation = 1
        };

        // CIFF header: byte order (II/MM) at offset 0, header length at 2, "HEAP" at offset 6
        if (data.Length < 26 || Encoding.ASCII.GetString(data, 6, 4) != "HEAP")
            throw new InvalidDataException("Invalid CRW: missing HEAP signature");

        bool be = data[0] == 'M';
        uint heapLength = be ? TiffIfdReader.ReadUInt32(data, 2, true) : TiffIfdReader.ReadUInt32(data, 2, false);

        // For CRW, the raw data is typically in the root heap
        // Simplified: assume raw data starts after the header
        int rawStart = (int)heapLength;
        int rawLen = data.Length - rawStart;

        // Estimate dimensions from data size (10-bit packed)
        long totalPixels = (long)rawLen * 8 / 10;
        int width = 2592; // Common CRW resolution
        int height = (int)(totalPixels / width);
        if (height <= 0) { width = 1728; height = (int)(totalPixels / width); }

        metadata.SensorWidth = width;
        metadata.SensorHeight = height;
        metadata.ActiveAreaWidth = width;
        metadata.ActiveAreaHeight = height;
        metadata.WhiteLevel = (1 << 10) - 1;
        metadata.RawDataOffset = rawStart;
        metadata.RawDataLength = rawLen;

        var pixels = new ushort[width * height];
        UnpackBits(data, rawStart, rawLen, pixels, 10);

        return new RawSensorData { RawPixels = pixels, Width = width, Height = height, Metadata = metadata };
    }

    private static RawSensorData ParseMrw(byte[] data)
    {
        // Minolta MRW: "\x00MRM" header
        if (data.Length < 8 || data[0] != 0 || data[1] != 'M' || data[2] != 'R' || data[3] != 'M')
            throw new InvalidDataException("Invalid MRW: missing MRM header");

        uint dataOffset = TiffIfdReader.ReadUInt32(data, 4, true); // Big-endian offset to raw data

        // Parse MRW blocks for dimensions
        int width = 0, height = 0, bitsPerSample = 12;
        int pos = 8;
        while (pos + 8 < data.Length && pos < (int)dataOffset)
        {
            string blockName = Encoding.ASCII.GetString(data, pos, 4);
            uint blockLen = TiffIfdReader.ReadUInt32(data, pos + 4, true);
            int blockData = pos + 8;

            if (blockName == "\x00PRD" && blockData + 8 <= data.Length) // Pixel Raw Dimensions
            {
                height = (data[blockData + 2] << 8) | data[blockData + 3];
                width = (data[blockData + 4] << 8) | data[blockData + 5];
                bitsPerSample = data[blockData + 8];
            }

            pos += 8 + (int)blockLen;
        }

        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.MRW,
            Make = "Minolta",
            SensorWidth = width,
            SensorHeight = height,
            ActiveAreaWidth = width,
            ActiveAreaHeight = height,
            BayerPattern = BayerPattern.RGGB,
            CfaType = CfaType.Bayer,
            BitsPerSample = bitsPerSample,
            Compression = RawCompression.Uncompressed,
            WhiteLevel = (1 << bitsPerSample) - 1,
            Orientation = 1,
            RawDataOffset = dataOffset,
            RawDataLength = data.Length - dataOffset
        };

        var pixels = new ushort[width * height];
        UnpackBits(data, (int)dataOffset, data.Length - (int)dataOffset, pixels, bitsPerSample);

        return new RawSensorData { RawPixels = pixels, Width = width, Height = height, Metadata = metadata };
    }

    private static RawSensorData ParseX3f(byte[] data)
    {
        // Sigma X3F: Foveon 3-layer sensor — no CFA demosaicing needed
        if (data.Length < 28)
            throw new InvalidDataException("Invalid X3F: file too short");

        // X3F header: "FOVb" at offset 0
        string sig = Encoding.ASCII.GetString(data, 0, 4);
        if (sig != "FOVb")
            throw new InvalidDataException("Invalid X3F: missing FOVb signature");

        // Simplified X3F parsing — extract dimensions from header
        uint version = TiffIfdReader.ReadUInt32(data, 4, false);
        // Dimensions are typically in the directory entries
        int width = 2640; // Common Sigma resolution
        int height = 1760;

        // Scan for image dimensions in the directory
        if (data.Length > 100)
        {
            // Try to read directory offset from end of file
            uint dirOffset = TiffIfdReader.ReadUInt32(data, data.Length - 4, false);
            if (dirOffset > 0 && dirOffset + 28 < data.Length)
            {
                int entryPos = (int)dirOffset + 12; // skip directory header
                if (entryPos + 8 < data.Length)
                {
                    width = (int)TiffIfdReader.ReadUInt32(data, entryPos + 16, false);
                    height = (int)TiffIfdReader.ReadUInt32(data, entryPos + 20, false);
                    if (width <= 0 || width > 20000) { width = 2640; height = 1760; }
                }
            }
        }

        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.X3F,
            Make = "Sigma",
            SensorWidth = width,
            SensorHeight = height,
            ActiveAreaWidth = width,
            ActiveAreaHeight = height,
            CfaType = CfaType.Foveon,
            BayerPattern = BayerPattern.Unknown,
            BitsPerSample = 14,
            Compression = RawCompression.FoveonCompressed,
            WhiteLevel = (1 << 14) - 1,
            Orientation = 1
        };

        // For Foveon, each pixel has 3 layers — extract as 3-channel data
        var pixels = new ushort[width * height];
        // Simplified: fill with zeros until proper X3F decompression is implemented
        // X3F decompression is complex and requires dedicated Huffman/Rice decoding

        return new RawSensorData { RawPixels = pixels, Width = width, Height = height, Metadata = metadata };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Shared Utilities
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared utilities used by all camera raw format parsers.
/// </summary>
internal static class CameraRawParserUtils
{
    /// <summary>
    /// Extracts common TIFF metadata from IFD0 (Make, Model, Orientation, EXIF data).
    /// </summary>
    internal static CameraRawMetadata ExtractCommonTiffMetadata(byte[] data,
        Dictionary<ushort, TiffTag> ifd, bool bigEndian, CameraRawFormat format)
    {
        var metadata = new CameraRawMetadata { Format = format };

        metadata.Make = TiffIfdReader.GetTagString(data, ifd, TiffIfdReader.TagMake, bigEndian)?.Trim('\0').Trim() ?? "";
        metadata.Model = TiffIfdReader.GetTagString(data, ifd, TiffIfdReader.TagModel, bigEndian)?.Trim('\0').Trim() ?? "";
        metadata.Orientation = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagOrientation, 1);

        int w = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageWidth);
        int h = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagImageLength);
        metadata.SensorWidth = w;
        metadata.SensorHeight = h;
        metadata.ActiveAreaWidth = w;
        metadata.ActiveAreaHeight = h;

        metadata.BitsPerSample = TiffIfdReader.GetTagInt(ifd, TiffIfdReader.TagBitsPerSample, 14);

        // Try to read EXIF IFD
        var exifIfd = TiffIfdReader.ParseSubIfd(data, ifd, TiffIfdReader.TagExifIFD, bigEndian);
        if (exifIfd is not null)
        {
            metadata.ExposureTime = TiffIfdReader.GetTagRational(data, exifIfd, TiffIfdReader.TagExposureTime, bigEndian);
            metadata.FNumber = TiffIfdReader.GetTagRational(data, exifIfd, TiffIfdReader.TagFNumber, bigEndian);
            metadata.IsoSpeed = TiffIfdReader.GetTagInt(exifIfd, TiffIfdReader.TagIsoSpeed);
            metadata.FocalLength = TiffIfdReader.GetTagRational(data, exifIfd, TiffIfdReader.TagFocalLength, bigEndian);

            string? dateTime = TiffIfdReader.GetTagString(data, exifIfd, TiffIfdReader.TagDateTimeOriginal, bigEndian);
            if (dateTime is not null && dateTime.Length >= 10
                && DateTime.TryParse(string.Concat(dateTime.AsSpan(0, 10).ToString().Replace(':', '-'), dateTime.AsSpan(10)), out var dt))
                metadata.CaptureTime = dt;
        }

        // ICC profile
        const ushort tagIccProfile = 34675;
        metadata.IccProfile = TiffIfdReader.GetTagRawBytes(data, ifd, tagIccProfile, bigEndian);

        // XMP
        const ushort tagXmp = 700;
        metadata.XmpData = TiffIfdReader.GetTagRawBytes(data, ifd, tagXmp, bigEndian);

        return metadata;
    }

    /// <summary>
    /// Extracts Olympus 12-bit packed raw data. Olympus ORF stores 12-bit pixels in 16-byte blocks:
    /// each block contains 5 pairs of 12-bit pixels (15 data bytes) followed by 1 padding byte.
    /// Within each pair, 3 bytes encode 2 pixels in little-endian order:
    ///   pixel0 = byte0 | ((byte1 &amp; 0x0F) &lt;&lt; 8)
    ///   pixel1 = (byte1 &gt;&gt; 4) | (byte2 &lt;&lt; 4)
    /// </summary>
    internal static RawSensorData ExtractOlympus12BitPackedData(byte[] data,
        Dictionary<ushort, TiffTag> ifd, CameraRawMetadata metadata)
    {
        int w = metadata.SensorWidth;
        int h = metadata.SensorHeight;
        int blocksPerRow = w / 10;
        int bytesPerRow = blocksPerRow * 16;

        var stripOffsets = TiffIfdReader.GetTagArray(ifd, TiffIfdReader.TagStripOffsets);
        if (stripOffsets.Length == 0)
            throw new InvalidDataException("ORF: no strip offsets found");

        int rawOffset = stripOffsets[0];
        var pixels = new ushort[w * h];

        for (int row = 0; row < h; row++)
        {
            int rowBase = rawOffset + row * bytesPerRow;
            int pixelIdx = row * w;

            for (int block = 0; block < blocksPerRow; block++)
            {
                int blockStart = rowBase + block * 16;

                for (int pair = 0; pair < 5; pair++)
                {
                    int pairStart = blockStart + pair * 3;
                    byte b0 = data[pairStart];
                    byte b1 = data[pairStart + 1];
                    byte b2 = data[pairStart + 2];

                    ushort v0 = (ushort)(b0 | ((b1 & 0x0F) << 8));
                    ushort v1 = (ushort)((b1 >> 4) | (b2 << 4));

                    // Store in native 12-bit range (0-4095) — ScaleToFullRange handles 16-bit scaling
                    pixels[pixelIdx++] = v0;
                    pixels[pixelIdx++] = v1;
                }
                // byte at blockStart + 15 is padding, skip it
            }
        }

        return new RawSensorData { RawPixels = pixels, Width = w, Height = h, Metadata = metadata };
    }

    /// <summary>
    /// Extracts raw sensor data from TIFF strip/tile offsets, handling lossless JPEG and uncompressed formats.
    /// For tile-based layouts (common in DNG), each tile is decoded independently and assembled.
    /// </summary>
    internal static RawSensorData ExtractSensorData(byte[] data,
        Dictionary<ushort, TiffTag> ifd, bool bigEndian, CameraRawMetadata metadata)
    {
        int w = metadata.SensorWidth;
        int h = metadata.SensorHeight;
        if (w <= 0 || h <= 0)
            throw new InvalidDataException($"Invalid sensor dimensions: {w}×{h}");

        // Check for tile-based layout first (DNG commonly uses tiles)
        const ushort tagTileWidth = 322;
        const ushort tagTileLength = 323;
        const ushort tagTileOffsets = 324;
        const ushort tagTileByteCounts = 325;

        int tileWidth = TiffIfdReader.GetTagInt(ifd, tagTileWidth);
        int tileHeight = TiffIfdReader.GetTagInt(ifd, tagTileLength);
        var tileOffsets = TiffIfdReader.GetTagArray(ifd, tagTileOffsets);
        var tileCounts = TiffIfdReader.GetTagArray(ifd, tagTileByteCounts);

        if (tileWidth > 0 && tileHeight > 0 && tileOffsets.Length > 0)
        {
            return ExtractTiledSensorData(data, w, h, tileWidth, tileHeight, tileOffsets, tileCounts, metadata);
        }

        // Strip-based layout
        var stripOffsets = TiffIfdReader.GetTagArray(ifd, TiffIfdReader.TagStripOffsets);
        var stripCounts = TiffIfdReader.GetTagArray(ifd, TiffIfdReader.TagStripByteCounts);

        // Validate strip offsets — some formats (e.g., RW2) store 0xFFFFFFFF as sentinel
        bool stripOffsetsValid = stripOffsets.Length > 0 &&
            stripOffsets[0] > 0 && stripOffsets[0] < data.Length;

        // Fallback to metadata-specified offset if strips are absent or invalid
        if (!stripOffsetsValid && metadata.RawDataOffset > 0)
        {
            stripOffsets = [(int)metadata.RawDataOffset];
            stripCounts = [(int)metadata.RawDataLength];
        }

        if (stripOffsets.Length == 0)
            throw new InvalidDataException("No strip/tile offsets found in raw IFD");

        // Concatenate all strips into one buffer
        int totalBytes = 0;
        for (int i = 0; i < stripCounts.Length; i++)
            totalBytes += stripCounts[i];

        byte[] rawBytes;
        if (stripOffsets.Length == 1)
        {
            rawBytes = data;
            int singleOffset = stripOffsets[0];
            int singleLength = stripCounts.Length > 0 ? stripCounts[0] : data.Length - singleOffset;

            if (metadata.Compression == RawCompression.LosslessJpeg)
            {
                return DecodeLosslessJpegSensorData(data, singleOffset, singleLength, w, h, metadata);
            }

            // Proprietary compression dispatch
            if (metadata.Compression == RawCompression.NikonCompressed)
            {
                var pixels = DecodeNikonCompressed(data, singleOffset, singleLength, w, h, metadata);
                return new RawSensorData { RawPixels = pixels, Width = w, Height = h, Metadata = metadata };
            }
            if (metadata.Compression == RawCompression.PentaxCompressed)
            {
                var pixels = DecodePentaxCompressed(data, singleOffset, singleLength, w, h, metadata);
                return new RawSensorData { RawPixels = pixels, Width = w, Height = h, Metadata = metadata };
            }
            if (metadata.Compression == RawCompression.PanasonicCompressed)
            {
                var pixels = DecodePanasonicRaw(data, singleOffset, singleLength, w, h, metadata.BitsPerSample);
                return new RawSensorData { RawPixels = pixels, Width = w, Height = h, Metadata = metadata };
            }

            var defaultPixels = new ushort[w * h];
            UnpackBits(data, singleOffset, singleLength, defaultPixels, metadata.BitsPerSample);
            return new RawSensorData { RawPixels = defaultPixels, Width = w, Height = h, Metadata = metadata };
        }

        // Multiple strips — concatenate
        rawBytes = new byte[totalBytes];
        int destPos = 0;
        for (int i = 0; i < stripOffsets.Length; i++)
        {
            int srcOffset = stripOffsets[i];
            int count = i < stripCounts.Length ? stripCounts[i] : 0;
            if (srcOffset + count <= data.Length && count > 0)
            {
                Buffer.BlockCopy(data, srcOffset, rawBytes, destPos, count);
                destPos += count;
            }
        }

        if (metadata.Compression == RawCompression.LosslessJpeg)
        {
            return DecodeLosslessJpegSensorData(rawBytes, 0, destPos, w, h, metadata);
        }

        var rawPixels = new ushort[w * h];
        UnpackBits(rawBytes, 0, destPos, rawPixels, metadata.BitsPerSample);
        return new RawSensorData { RawPixels = rawPixels, Width = w, Height = h, Metadata = metadata };
    }

    /// <summary>
    /// Decodes tile-based raw data. Each tile is a separate lossless JPEG or uncompressed block
    /// that must be decoded independently and placed at its grid position in the output image.
    /// </summary>
    private static RawSensorData ExtractTiledSensorData(byte[] data, int imageWidth, int imageHeight,
        int tileWidth, int tileHeight, int[] tileOffsets, int[] tileCounts, CameraRawMetadata metadata)
    {
        var pixels = new ushort[imageWidth * imageHeight];
        int tilesAcross = (imageWidth + tileWidth - 1) / tileWidth;
        int tilesDown = (imageHeight + tileHeight - 1) / tileHeight;

        for (int tileIdx = 0; tileIdx < tileOffsets.Length; tileIdx++)
        {
            int tileX = (tileIdx % tilesAcross) * tileWidth;
            int tileY = (tileIdx / tilesAcross) * tileHeight;
            int tileOffset = tileOffsets[tileIdx];
            int tileByteCount = tileIdx < tileCounts.Length ? tileCounts[tileIdx] : 0;

            if (tileOffset <= 0 || tileByteCount <= 0 || tileOffset + tileByteCount > data.Length)
                continue;

            ushort[]? tilePixels = null;
            int tw = tileWidth, th = tileHeight;

            if (metadata.Compression == RawCompression.LosslessJpeg)
            {
                try
                {
                    tilePixels = LosslessJpegDecoder.Decode(data, tileOffset, tileByteCount,
                        out int ljWidth, out int ljHeight, out int components, out _);
                    tw = ljWidth * components;
                    th = ljHeight;
                }
                catch
                {
                    continue;
                }
            }
            else
            {
                // Uncompressed tile
                int pixelsInTile = tileWidth * tileHeight;
                tilePixels = new ushort[pixelsInTile];
                UnpackBits(data, tileOffset, tileByteCount, tilePixels, metadata.BitsPerSample);
            }

            if (tilePixels is null) continue;

            // Copy tile pixels to the correct position in the output image
            for (int row = 0; row < th && (tileY + row) < imageHeight; row++)
            {
                int srcStart = row * tw;
                int dstStart = (tileY + row) * imageWidth + tileX;
                int copyWidth = Math.Min(tw, imageWidth - tileX);
                if (srcStart + copyWidth <= tilePixels.Length && dstStart + copyWidth <= pixels.Length)
                {
                    Array.Copy(tilePixels, srcStart, pixels, dstStart, copyWidth);
                }
            }
        }

        return new RawSensorData { RawPixels = pixels, Width = imageWidth, Height = imageHeight, Metadata = metadata };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Nikon Compressed Decoder (compression code 34713)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Nikon Huffman trees from dcraw (16 bytes bit-counts followed by symbol values).
    /// Six trees: [0]=12-bit lossy, [1]=12-bit lossy after split, [2]=12-bit lossless,
    /// [3]=14-bit lossy, [4]=14-bit lossy after split, [5]=14-bit lossless.
    /// </summary>
    private static readonly byte[][] NikonTrees =
    [
        [0,1,5,1,1,1,1,1,1,2,0,0,0,0,0,0, 5,4,3,6,2,7,1,0,8,9,11,10,12],             // 12-bit lossy
        [0,1,5,1,1,1,1,1,1,2,0,0,0,0,0,0, 0x39,0x5A,0x38,0x27,0x16,5,4,3,2,1,0,11,12,12], // 12-bit lossy after split
        [0,1,4,2,3,1,2,0,0,0,0,0,0,0,0,0, 5,4,6,3,7,2,8,1,9,0,10,11,12],             // 12-bit lossless
        [0,1,4,3,1,1,1,1,1,2,0,0,0,0,0,0, 5,6,4,7,8,3,9,2,1,0,10,11,12,13,14],       // 14-bit lossy
        [0,1,5,1,1,1,1,1,1,1,2,0,0,0,0,0, 8,0x5C,0x4B,0x3A,0x29,7,6,5,4,3,2,1,0,13,14], // 14-bit lossy after split
        [0,1,4,2,2,3,1,2,0,0,0,0,0,0,0,0, 7,6,8,5,9,4,10,3,11,12,2,0,1,13,14],       // 14-bit lossless
    ];

    /// <summary>
    /// Builds a dcraw-style lookup Huffman table from a nikon_tree byte array.
    /// Returns huff[] where huff[0] = maxBits and huff[1..1&lt;&lt;maxBits] = (codelen &lt;&lt; 8 | symbol).
    /// </summary>
    private static ushort[] BuildNikonHuffTable(byte[] treeData)
    {
        // First 16 bytes are bit counts per code length (1-16)
        int max = 16;
        while (max > 0 && treeData[max - 1] == 0) max--;

        var huff = new ushort[1 + (1 << max)];
        huff[0] = (ushort)max;

        int h = 1;
        int srcIdx = 16; // symbols start after the 16 bit-count bytes
        for (int len = 1; len <= max; len++)
        {
            int count = treeData[len - 1];
            for (int i = 0; i < count; i++)
            {
                byte symbol = srcIdx < treeData.Length ? treeData[srcIdx++] : (byte)0;
                int fillCount = 1 << (max - len);
                for (int j = 0; j < fillCount && h <= (1 << max); j++)
                    huff[h++] = (ushort)((len << 8) | symbol);
            }
        }
        return huff;
    }

    /// <summary>
    /// Decodes Nikon compressed raw data (TIFF compression 34713).
    /// Full dcraw nikon_load_raw() algorithm: reads version bytes and initial predictors
    /// from meta_offset, selects appropriate Huffman tree, builds linearization curve
    /// (lossy mode), handles split-point tree switching, and applies curve to output.
    /// </summary>
    private static ushort[] DecodeNikonCompressed(byte[] data, int offset, int length,
        int width, int height, CameraRawMetadata metadata)
    {
        var pixels = new ushort[width * height];
        int bitsPerSample = metadata.BitsPerSample;
        bool bigEndian = metadata.BigEndian;
        long metaOffset = metadata.MetaOffset;

        // Read version bytes and initial predictors from meta_offset
        int tree = 0;
        int split = 0;
        int[,] vpredInit = new int[2, 2]; // initial vertical predictors
        ushort[] curve = new ushort[0x8000];
        int curveMax = 1 << bitsPerSample;

        if (metaOffset > 0 && metaOffset + 10 < data.Length)
        {
            byte ver0 = data[metaOffset];
            byte ver1 = data[metaOffset + 1];

            long pos = metaOffset + 2;
            // Skip encryption header if present
            if (ver0 == 0x49 || ver1 == 0x58)
                pos += 2110;

            // Select base tree: ver0=0x46 means lossless (tree=2), else lossy (tree=0)
            if (ver0 == 0x46) tree = 2;
            if (bitsPerSample == 14) tree += 3;

            // Read initial vertical predictors (4 SHORT values in file endian)
            if (pos + 8 <= data.Length)
            {
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                    {
                        vpredInit[i, j] = bigEndian
                            ? (data[pos] << 8 | data[pos + 1])
                            : (data[pos + 1] << 8 | data[pos]);
                        // Sign extend from 16-bit
                        if (vpredInit[i, j] >= 32768) vpredInit[i, j] -= 65536;
                        pos += 2;
                    }
            }

            // Read curve size
            int max = (1 << bitsPerSample) & 0x7FFF;
            int csize = 0;
            if (pos + 2 <= data.Length)
            {
                csize = bigEndian
                    ? (data[pos] << 8 | data[pos + 1])
                    : (data[pos + 1] << 8 | data[pos]);
                pos += 2;
            }

            int step = csize > 1 ? max / (csize - 1) : 0;

            if (ver0 == 0x44 && ver1 == 0x20 && step > 0)
            {
                // Lossy mode: read csize sample points, interpolate full curve
                for (int i = 0; i < csize && pos + 2 <= data.Length; i++)
                {
                    int idx = i * step;
                    if (idx < curve.Length)
                    {
                        curve[idx] = (ushort)(bigEndian
                            ? (data[pos] << 8 | data[pos + 1])
                            : (data[pos + 1] << 8 | data[pos]));
                    }
                    pos += 2;
                }
                // Interpolate between sample points
                for (int i = 0; i < max; i++)
                {
                    int lo = i - i % step;
                    int hi = lo + step;
                    if (hi < curve.Length && lo < curve.Length)
                        curve[i] = (ushort)((curve[lo] * (step - i % step) + curve[hi] * (i % step)) / step);
                }
                curveMax = max;

                // Read split point from meta_offset + 562
                if (metaOffset + 563 < data.Length)
                {
                    split = bigEndian
                        ? (data[metaOffset + 562] << 8 | data[metaOffset + 563])
                        : (data[metaOffset + 563] << 8 | data[metaOffset + 562]);
                }
            }
            else if (ver0 != 0x46 && csize > 0 && csize <= 0x4001)
            {
                // Lossless with full curve table
                for (int i = 0; i < csize && pos + 2 <= data.Length; i++)
                {
                    curve[i] = (ushort)(bigEndian
                        ? (data[pos] << 8 | data[pos + 1])
                        : (data[pos + 1] << 8 | data[pos]));
                    pos += 2;
                }
                curveMax = csize;
            }
            else
            {
                // Lossless with identity curve
                for (int i = 0; i < curveMax; i++)
                    curve[i] = (ushort)i;
            }
        }
        else
        {
            // No meta_offset — use identity curve and lossless tree
            tree = bitsPerSample == 14 ? 5 : 2;
            for (int i = 0; i < curveMax; i++)
                curve[i] = (ushort)i;
        }

        // Trim duplicate trailing curve entries
        while (curveMax > 2 && curve[curveMax - 2] == curve[curveMax - 1]) curveMax--;

        // Build Huffman table from selected tree
        var huff = BuildNikonHuffTable(NikonTrees[tree]);

        // Bitstream state
        long bitBuffer = 0;
        int bitsInBuffer = 0;
        int srcPos = offset;
        int srcEnd = offset + length;

        int[,] vpred = { { vpredInit[0, 0], vpredInit[0, 1] }, { vpredInit[1, 0], vpredInit[1, 1] } };
        int[] hpred = new int[2];
        int min = 0;

        for (int row = 0; row < height; row++)
        {
            // Handle split point — switch to second Huffman tree
            if (split > 0 && row == split)
            {
                huff = BuildNikonHuffTable(NikonTrees[tree + 1]);
                min = 16;
                curveMax += min << 1;
            }

            for (int col = 0; col < width; col++)
            {
                // Decode Huffman symbol using lookup table
                int huffMax = huff[0];
                EnsureBits(data, ref srcPos, srcEnd, ref bitBuffer, ref bitsInBuffer, huffMax);
                int peekBits = (int)((bitBuffer >> (bitsInBuffer - huffMax)) & ((1 << huffMax) - 1));
                int entry = huff[1 + peekBits];
                int codeLen = entry >> 8;
                int symbol = entry & 0xFF;
                bitsInBuffer -= codeLen;

                // Decode difference value using dcraw's shl-aware algorithm
                int len = symbol & 15;
                int shl = symbol >> 4;
                int diff = 0;
                if (len > 0)
                {
                    int readBits = len - shl;
                    if (readBits > 0)
                    {
                        int raw = ReadBitsFromBuffer(data, ref srcPos, srcEnd, ref bitBuffer, ref bitsInBuffer, readBits);
                        diff = ((raw << 1) + 1) << shl >> 1;
                    }
                    else
                    {
                        diff = 1 << (shl - 1); // readBits == 0
                    }
                    if ((diff & (1 << (len - 1))) == 0)
                        diff -= (1 << len) - (shl == 0 ? 1 : 0);
                }

                // Apply predictor
                if (col < 2)
                {
                    vpred[row & 1, col] += diff;
                    hpred[col] = vpred[row & 1, col];
                }
                else
                {
                    hpred[col & 1] += diff;
                }

                // Apply curve and clamp
                int predVal = (short)hpred[col & 1]; // sign-aware cast
                int curveIdx = Math.Clamp(predVal, 0, 0x3FFF);
                if (curveIdx < curveMax)
                    pixels[row * width + col] = curve[curveIdx];
                else
                    pixels[row * width + col] = curve[Math.Min(curveIdx, curveMax - 1)];
            }
        }

        return pixels;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureBits(byte[] data, ref int srcPos, int srcEnd,
        ref long bitBuffer, ref int bitsInBuffer, int needed)
    {
        while (bitsInBuffer < needed && srcPos < srcEnd)
        {
            bitBuffer = (bitBuffer << 8) | data[srcPos++];
            bitsInBuffer += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBitsFromBuffer(byte[] data, ref int srcPos, int srcEnd,
        ref long bitBuffer, ref int bitsInBuffer, int count)
    {
        EnsureBits(data, ref srcPos, srcEnd, ref bitBuffer, ref bitsInBuffer, count);
        bitsInBuffer -= count;
        return (int)((bitBuffer >> bitsInBuffer) & ((1 << count) - 1));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Panasonic RW2 Decompressor
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decodes Panasonic RW2 compressed raw data using dcraw's panasonic_load_raw() algorithm.
    /// Data is organized in 0x4000-byte blocks with LSB-first bit reading and XOR byte addressing.
    /// Pixels are encoded in groups of 14 using differential prediction on even/odd columns.
    /// </summary>
    private static ushort[] DecodePanasonicRaw(byte[] data, int offset, int length,
        int width, int height, int bitsPerSample)
    {
        var pixels = new ushort[width * height];

        // Block-based state: each block is 0x4000 bytes, read via pana_bits()
        const int blockSize = 0x4000;
        byte[] buf = new byte[blockSize + 2]; // +2 for safe 16-bit reads at end
        int vbits = 0;
        int filePos = offset;
        int fileEnd = offset + length;
        int loadFlags = 0x2008; // dcraw sets 0x2008 when Panasonic RW2 offset tag (0x0118) is present

        int[] pred = new int[2];
        int[] nonz = new int[2];
        int sh = 0;

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = col % 14;
                if (i == 0)
                {
                    pred[0] = pred[1] = 0;
                    nonz[0] = nonz[1] = 0;
                }

                if (i % 3 == 2)
                    sh = 4 >> (3 - PanaBits(data, buf, ref vbits, ref filePos, fileEnd, blockSize, loadFlags, 2));

                if (nonz[i & 1] != 0)
                {
                    int j = PanaBits(data, buf, ref vbits, ref filePos, fileEnd, blockSize, loadFlags, 8);
                    if (j != 0)
                    {
                        if ((pred[i & 1] -= 0x80 << sh) < 0 || sh == 4)
                            pred[i & 1] &= ~((-1) << sh);
                        pred[i & 1] += j << sh;
                    }
                }
                else
                {
                    nonz[i & 1] = PanaBits(data, buf, ref vbits, ref filePos, fileEnd, blockSize, loadFlags, 8);
                    if (nonz[i & 1] != 0 || i > 11)
                        pred[i & 1] = nonz[i & 1] << 4 | PanaBits(data, buf, ref vbits, ref filePos, fileEnd, blockSize, loadFlags, 4);
                }

                pixels[row * width + col] = (ushort)Math.Clamp(pred[col & 1], 0, 0xFFFF);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Reads N bits from a Panasonic block-structured bitstream (dcraw pana_bits algorithm).
    /// Bytes are read in 0x4000-byte blocks with LSB-first bit order and XOR byte addressing.
    /// The byte access pattern XORs the index with 0x3FF0, reading 16-byte groups in reverse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PanaBits(byte[] data, byte[] buf, ref int vbits, ref int filePos,
        int fileEnd, int blockSize, int loadFlags, int nbits)
    {
        if (nbits == 0) { vbits = 0; return 0; }

        // When vbits is 0, load next block
        if (vbits == 0)
        {
            int bytesAvail = Math.Min(blockSize, fileEnd - filePos);
            if (bytesAvail <= 0) return 0;

            // Buffer loading with load_flags rotation (dcraw convention)
            int partA = Math.Min(blockSize - loadFlags, bytesAvail);
            int partB = Math.Min(loadFlags, bytesAvail - partA);
            if (partA > 0) Buffer.BlockCopy(data, filePos, buf, loadFlags, partA);
            if (partB > 0) Buffer.BlockCopy(data, filePos + partA, buf, 0, partB);
            filePos += bytesAvail;

            // Zero out extra bytes for safe reads
            if (bytesAvail < blockSize)
                Array.Clear(buf, bytesAvail, blockSize - bytesAvail);

            vbits = blockSize * 8; // Total bits available in this block
        }

        vbits = (vbits - nbits) & 0x1FFFF;
        int byteIdx = (vbits >> 3) ^ 0x3FF0;
        return (buf[byteIdx] | buf[byteIdx + 1] << 8) >> (vbits & 7) & ~((-1) << nbits);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Pentax Compressed Decoder (compression code 65535)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decodes Pentax compressed raw data (TIFF compression 65535).
    /// Full dcraw pentax_load_raw() algorithm: reads Huffman table from meta_offset
    /// (MakerNote tag 0x220), builds 12-bit direct lookup table, then decodes using
    /// ljpeg_diff with vertical/horizontal prediction.
    /// </summary>
    private static ushort[] DecodePentaxCompressed(byte[] data, int offset, int length,
        int width, int height, CameraRawMetadata metadata)
    {
        var pixels = new ushort[width * height];
        int bitsPerSample = metadata.BitsPerSample;
        bool bigEndian = metadata.BigEndian;
        long metaOffset = metadata.MetaOffset;

        // Build Huffman lookup table from MakerNote tag 0x220 (dcraw pentax_load_raw approach)
        // huff[0] = maxBits (always 12), huff[1..4096] = (codelen << 8 | symbol)
        var huff = new ushort[4097];
        huff[0] = 12; // Always 12-bit codes for Pentax

        if (metaOffset > 0 && metaOffset + 2 < data.Length)
        {
            // Read dep (depth = number of table entries)
            int dep = bigEndian
                ? (data[metaOffset] << 8 | data[metaOffset + 1])
                : (data[metaOffset + 1] << 8 | data[metaOffset]);
            dep = (dep + 12) & 15;

            long pos = metaOffset + 2 + 12; // skip 2 for dep + 12 bytes of padding

            // Read dep start-code values (2 bytes each)
            int[] starts = new int[dep];
            for (int c = 0; c < dep && pos + 2 <= data.Length; c++)
            {
                starts[c] = bigEndian
                    ? (data[pos] << 8 | data[pos + 1])
                    : (data[pos + 1] << 8 | data[pos]);
                pos += 2;
            }

            // Read dep bit-length values (1 byte each)
            int[] lengths = new int[dep];
            for (int c = 0; c < dep && pos + 1 <= data.Length; c++)
            {
                lengths[c] = data[pos++];
            }

            // Fill the lookup table: for each entry, fill from start to start+(4096>>len)-1
            for (int c = 0; c < dep; c++)
            {
                int start = starts[c];
                int len = lengths[c];
                int rangeSize = 4096 >> len;
                for (int i = start; i <= ((start + rangeSize - 1) & 4095); )
                {
                    if (i + 1 < huff.Length)
                        huff[i + 1] = (ushort)((len << 8) | c);
                    i++;
                }
            }
        }

        // Bitstream state
        long bitBuffer = 0;
        int bitsInBuffer = 0;
        int srcPos = offset;
        int srcEnd = offset + length;

        int[,] vpred = new int[2, 2];
        int[] hpred = new int[2];

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                // Decode using dcraw ljpeg_diff algorithm with the Pentax huff table
                int huffMax = huff[0]; // 12
                EnsureBits(data, ref srcPos, srcEnd, ref bitBuffer, ref bitsInBuffer, huffMax);
                int peekBits = (int)((bitBuffer >> (bitsInBuffer - huffMax)) & ((1 << huffMax) - 1));
                int entry = huff[1 + peekBits];
                int codeLen = entry >> 8;
                int diffLen = entry & 0xFF; // symbol = difference magnitude
                bitsInBuffer -= codeLen > 0 ? codeLen : huffMax;

                // ljpeg_diff: read diffLen bits and sign-extend
                int diff = 0;
                if (diffLen == 16)
                {
                    diff = -32768;
                }
                else if (diffLen > 0)
                {
                    diff = ReadBitsFromBuffer(data, ref srcPos, srcEnd, ref bitBuffer, ref bitsInBuffer, diffLen);
                    if ((diff & (1 << (diffLen - 1))) == 0)
                        diff -= (1 << diffLen) - 1;
                }

                // Apply predictor
                if (col < 2)
                {
                    vpred[row & 1, col] += diff;
                    hpred[col] = vpred[row & 1, col];
                }
                else
                {
                    hpred[col & 1] += diff;
                }

                pixels[row * width + col] = (ushort)Math.Clamp(hpred[col & 1], 0, (1 << bitsPerSample) - 1);
            }
        }

        return pixels;
    }

    private static RawSensorData DecodeLosslessJpegSensorData(byte[] data, int offset, int length,
        int expectedWidth, int expectedHeight, CameraRawMetadata metadata)
    {
        try
        {
            var decoded = LosslessJpegDecoder.Decode(data, offset, length,
                out int ljWidth, out int ljHeight, out int components, out int precision);

            // The lossless JPEG dimensions may differ from the metadata dimensions
            int actualWidth = ljWidth * components; // multi-component interleaved
            int actualHeight = ljHeight;
            int totalDecoded = decoded.Length;

            // If decoded size matches expected, use as-is
            if (actualWidth == expectedWidth && actualHeight == expectedHeight)
            {
                return new RawSensorData
                {
                    RawPixels = decoded, Width = actualWidth, Height = actualHeight, Metadata = metadata
                };
            }

            // CR2 and similar: multi-component JPEG encodes the full sensor as a flat pixel stream.
            // The total decoded pixels may match expectedWidth * expectedHeight when reshaped.
            // Example: CR2 SOF3 has 4448×2960 w/4 components = 52,664,320 values = 8896×5920.
            int expectedTotal = expectedWidth * expectedHeight;
            if (totalDecoded == expectedTotal && expectedWidth > 0 && expectedHeight > 0)
            {
                metadata.SensorWidth = expectedWidth;
                metadata.SensorHeight = expectedHeight;
                return new RawSensorData
                {
                    RawPixels = decoded, Width = expectedWidth, Height = expectedHeight, Metadata = metadata
                };
            }

            // Also check if the total pixels can be reshaped to expected width
            if (expectedWidth > 0 && totalDecoded >= expectedWidth)
            {
                int reshapedHeight = totalDecoded / expectedWidth;
                if (reshapedHeight * expectedWidth == totalDecoded && reshapedHeight > 0)
                {
                    metadata.SensorWidth = expectedWidth;
                    metadata.SensorHeight = reshapedHeight;
                    if (metadata.ActiveAreaWidth == 0 || metadata.ActiveAreaWidth > expectedWidth)
                    {
                        metadata.ActiveAreaWidth = expectedWidth;
                        metadata.ActiveAreaHeight = reshapedHeight;
                    }
                    return new RawSensorData
                    {
                        RawPixels = decoded, Width = expectedWidth, Height = reshapedHeight, Metadata = metadata
                    };
                }
            }

            // Fallback: use the lossless JPEG dimensions as-is
            metadata.SensorWidth = actualWidth;
            metadata.SensorHeight = actualHeight;
            if (metadata.ActiveAreaWidth == 0 || metadata.ActiveAreaWidth > actualWidth)
            {
                metadata.ActiveAreaWidth = actualWidth;
                metadata.ActiveAreaHeight = actualHeight;
            }

            return new RawSensorData
            {
                RawPixels = decoded, Width = actualWidth, Height = actualHeight, Metadata = metadata
            };
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to decode lossless JPEG in {metadata.Format}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates sensor data from uncompressed raw bytes at a given offset.
    /// </summary>
    internal static RawSensorData CreateUncompressedSensorData(byte[] data, long offset,
        int width, int height, CameraRawMetadata metadata)
    {
        var pixels = new ushort[width * height];
        int available = (int)Math.Min(data.Length - offset, (long)width * height * 2);
        UnpackBits(data, (int)offset, available, pixels, metadata.BitsPerSample);

        return new RawSensorData { RawPixels = pixels, Width = width, Height = height, Metadata = metadata };
    }

    /// <summary>
    /// Unpacks packed bit data into 16-bit pixel values. Handles 10, 12, 14, and 16-bit packing.
    /// </summary>
    internal static void UnpackBits(byte[] src, int offset, int length, ushort[] dest, int bitsPerSample)
    {
        int maxDest = dest.Length;

        switch (bitsPerSample)
        {
            case 16:
                // Simple 16-bit little-endian
                for (int i = 0, srcPos = offset; i < maxDest && srcPos + 1 < offset + length; i++, srcPos += 2)
                    dest[i] = (ushort)(src[srcPos] | (src[srcPos + 1] << 8));
                break;

            case 8:
                for (int i = 0, srcPos = offset; i < maxDest && srcPos < offset + length; i++, srcPos++)
                    dest[i] = src[srcPos];
                break;

            default:
                // Bit-packed (10, 12, 14-bit) — values stored in native bit depth
                UnpackBitsPacked(src, offset, length, dest, bitsPerSample);
                break;
        }
    }

    private static void UnpackBitsPacked(byte[] src, int offset, int length,
        ushort[] dest, int bitsPerSample)
    {
        int maxDest = dest.Length;
        long bitBuffer = 0;
        int bitsInBuffer = 0;
        int srcPos = offset;
        int srcEnd = offset + length;
        int destIdx = 0;
        int mask = (1 << bitsPerSample) - 1;

        while (destIdx < maxDest && srcPos < srcEnd)
        {
            // Fill the bit buffer
            while (bitsInBuffer < bitsPerSample && srcPos < srcEnd)
            {
                bitBuffer = (bitBuffer << 8) | src[srcPos++];
                bitsInBuffer += 8;
            }

            if (bitsInBuffer >= bitsPerSample)
            {
                bitsInBuffer -= bitsPerSample;
                int value = (int)((bitBuffer >> bitsInBuffer) & mask);
                dest[destIdx++] = (ushort)value;
            }
        }
    }

    /// <summary>
    /// Decodes CFA pattern from TIFF tag values to BayerPattern enum.
    /// CFA pattern tag stores color indices: 0=Red, 1=Green, 2=Blue.
    /// </summary>
    internal static BayerPattern DecodeCfaPattern(int[] cfaValues)
    {
        if (cfaValues.Length < 4) return BayerPattern.Unknown;

        int tl = cfaValues[0], tr = cfaValues[1], bl = cfaValues[2], br = cfaValues[3];

        // RGGB: R G / G B = 0 1 / 1 2
        if (tl == 0 && tr == 1 && bl == 1 && br == 2) return BayerPattern.RGGB;
        // BGGR: B G / G R = 2 1 / 1 0
        if (tl == 2 && tr == 1 && bl == 1 && br == 0) return BayerPattern.BGGR;
        // GRBG: G R / B G = 1 0 / 2 1
        if (tl == 1 && tr == 0 && bl == 2 && br == 1) return BayerPattern.GRBG;
        // GBRG: G B / R G = 1 2 / 0 1
        if (tl == 1 && tr == 2 && bl == 0 && br == 1) return BayerPattern.GBRG;

        return BayerPattern.Unknown;
    }

    /// <summary>
    /// Reads a RATIONAL tag array and converts to double array.
    /// </summary>
    internal static double[]? ReadRationalArray(byte[] data, Dictionary<ushort, TiffTag> tags,
        ushort tagId, bool bigEndian, int expectedCount)
    {
        if (!tags.TryGetValue(tagId, out var tag) || tag.Count < expectedCount)
            return null;

        return TiffIfdReader.GetTagRationalArray(data, tags, tagId, bigEndian, expectedCount);
    }

    /// <summary>
    /// Reads a SRATIONAL matrix (signed rationals) — used for color matrices.
    /// </summary>
    internal static double[]? ReadRationalMatrix(byte[] data, Dictionary<ushort, TiffTag> tags,
        ushort tagId, bool bigEndian, int expectedCount)
    {
        if (!tags.TryGetValue(tagId, out var tag) || tag.Count < expectedCount)
            return null;

        return TiffIfdReader.GetTagRationalArray(data, tags, tagId, bigEndian, expectedCount);
    }

    /// <summary>
    /// Looks up a camera-specific color matrix (camera RGB → XYZ D50) from dcraw's adobe_coeff table.
    /// Returns a 9-element double array (3×3 row-major) or null if the model is not found.
    /// Used for non-DNG formats that don't embed ColorMatrix1 in their metadata.
    /// </summary>
    internal static double[]? LookupCameraColorMatrix(string make, string model)
    {
        // Many cameras include the brand in both Make and Model (e.g., Make="Canon",
        // Model="Canon EOS 5DS"). Deduplicate to match dcraw's table format.
        string trimmedMake = make.Trim();
        string trimmedModel = model.Trim();
        string key = trimmedModel.StartsWith(trimmedMake, StringComparison.OrdinalIgnoreCase)
            ? trimmedModel.ToUpperInvariant()
            : (trimmedMake + " " + trimmedModel).ToUpperInvariant();

        foreach (var (prefix, matrix) in CameraColorMatrices)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return matrix;
        }
        return null;
    }

    // dcraw adobe_coeff entries: { Make+Model prefix, 3×3 XYZ_D50→Camera matrix as doubles }
    // Same convention as DNG ColorMatrix1. Inverted at runtime to get Camera→XYZ.
    // Ordered longest-prefix-first within each brand for correct matching.
    private static readonly (string Prefix, double[] Matrix)[] CameraColorMatrices =
    [
        // Canon
        ("CANON EOS 5DS", [0.6250, -0.0711, -0.0808, -0.5153, 1.2794, 0.2636, -0.1249, 0.2198, 0.5610]),
        ("CANON EOS 5D MARK IV", [0.6446, -0.0366, -0.0864, -0.4436, 1.2204, 0.2513, -0.0952, 0.2496, 0.6348]),
        ("CANON EOS 5D MARK III", [0.6722, -0.0635, -0.0963, -0.4287, 1.2460, 0.2028, -0.0908, 0.2162, 0.5668]),
        ("CANON EOS 5D MARK II", [0.4716, 0.0603, -0.0830, -0.7798, 1.5474, 0.2480, -0.1496, 0.1937, 0.6651]),
        ("CANON EOS 6D MARK II", [0.6875, -0.0970, -0.0932, -0.4691, 1.2459, 0.2501, -0.0874, 0.1953, 0.5809]),
        ("CANON EOS 6D", [0.7034, -0.0804, -0.1014, -0.4420, 1.2564, 0.2058, -0.0851, 0.1994, 0.5758]),
        ("CANON EOS 80D", [0.7457, -0.0671, -0.0937, -0.4849, 1.2495, 0.2643, -0.1213, 0.2354, 0.5492]),
        ("CANON EOS 70D", [0.7034, -0.0804, -0.1014, -0.4420, 1.2564, 0.2058, -0.0851, 0.1994, 0.5758]),
        ("CANON EOS 7D MARK II", [0.7268, -0.1082, -0.0969, -0.4457, 1.2467, 0.2243, -0.0891, 0.2277, 0.5492]),
        ("CANON EOS 7D", [0.6844, -0.0996, -0.0856, -0.3876, 1.1761, 0.2396, -0.0593, 0.1772, 0.6198]),
        ("CANON EOS R5", [0.8424, -0.2249, -0.0934, -0.4243, 1.2208, 0.2292, -0.0595, 0.1700, 0.6170]),
        ("CANON EOS R6", [0.8424, -0.2249, -0.0934, -0.4243, 1.2208, 0.2292, -0.0595, 0.1700, 0.6170]),
        ("CANON EOS R", [0.6446, -0.0366, -0.0864, -0.4436, 1.2204, 0.2513, -0.0952, 0.2496, 0.6348]),
        ("CANON EOS 90D", [0.8424, -0.2249, -0.0934, -0.4243, 1.2208, 0.2292, -0.0595, 0.1700, 0.6170]),
        ("CANON EOS", [0.6461, -0.0907, -0.0882, -0.4300, 1.2184, 0.2378, -0.0819, 0.1944, 0.5931]), // Generic Canon fallback

        // Nikon
        ("NIKON D850", [0.7437, -0.2110, -0.0855, -0.4349, 1.2304, 0.2278, -0.0467, 0.1475, 0.6480]),
        ("NIKON D810", [0.9369, -0.3195, -0.0791, -0.4488, 1.2430, 0.2301, -0.0547, 0.1528, 0.5765]),
        ("NIKON D800", [0.7866, -0.2108, -0.0555, -0.4869, 1.2483, 0.2681, -0.1176, 0.2069, 0.5947]),
        ("NIKON D750", [0.9020, -0.2890, -0.0715, -0.4535, 1.2436, 0.2348, -0.0934, 0.1919, 0.5936]),
        ("NIKON D500", [0.8813, -0.3210, -0.0612, -0.3513, 1.1305, 0.2368, -0.0587, 0.1577, 0.6321]),
        ("NIKON D7500", [0.8813, -0.3210, -0.0612, -0.3513, 1.1305, 0.2368, -0.0587, 0.1577, 0.6321]),
        ("NIKON D7200", [0.8322, -0.3112, -0.0686, -0.3015, 1.1193, 0.2006, -0.0471, 0.1536, 0.6139]),
        ("NIKON D5600", [0.8322, -0.3112, -0.0686, -0.3015, 1.1193, 0.2006, -0.0471, 0.1536, 0.6139]),
        ("NIKON D5", [0.9200, -0.3522, -0.0417, -0.4690, 1.2480, 0.2500, -0.0937, 0.1940, 0.5960]),
        ("NIKON D4S", [0.8598, -0.2848, -0.0857, -0.4027, 1.2111, 0.2158, -0.0621, 0.1621, 0.6209]),
        ("NIKON D4", [0.8598, -0.2848, -0.0857, -0.4027, 1.2111, 0.2158, -0.0621, 0.1621, 0.6209]),
        ("NIKON D3X", [0.7171, -0.1986, -0.0648, -0.8085, 1.5555, 0.2718, -0.2170, 0.2512, 0.7457]),
        ("NIKON D3S", [0.8828, -0.2406, -0.0694, -0.4874, 1.2603, 0.2541, -0.0529, 0.1560, 0.5765]),
        ("NIKON D3", [0.8139, -0.2171, -0.0663, -0.8747, 1.6541, 0.2295, -0.1925, 0.2008, 0.8093]),
        ("NIKON Z", [0.7866, -0.2108, -0.0555, -0.4869, 1.2483, 0.2681, -0.1176, 0.2069, 0.5947]), // Generic Nikon Z
        ("NIKON", [0.7866, -0.2108, -0.0555, -0.4869, 1.2483, 0.2681, -0.1176, 0.2069, 0.5947]), // Generic Nikon fallback

        // Panasonic / Lumix
        ("PANASONIC DMC-FZ7", [1.1532, -0.4324, -0.1066, -0.2375, 1.0847, 0.1749, -0.0564, 0.1699, 0.4351]), // FZ70/FZ72
        ("PANASONIC DMC-FZ100", [0.7830, -0.2696, -0.0763, -0.3325, 1.1667, 0.1866, -0.0641, 0.1712, 0.4824]),
        ("PANASONIC DMC-GH5", [0.7641, -0.2336, -0.0586, -0.2610, 1.0605, 0.2178, -0.0306, 0.1541, 0.5799]),
        ("PANASONIC DMC-GH4", [0.7122, -0.1590, -0.0764, -0.3785, 1.1289, 0.2820, -0.0820, 0.1773, 0.6349]),
        ("PANASONIC DMC-G", [0.7122, -0.1590, -0.0764, -0.3785, 1.1289, 0.2820, -0.0820, 0.1773, 0.6349]), // Generic G-series
        ("PANASONIC", [0.7122, -0.1590, -0.0764, -0.3785, 1.1289, 0.2820, -0.0820, 0.1773, 0.6349]), // Generic Panasonic

        // Olympus
        ("OLYMPUS E-M1 MARK III", [0.9234, -0.3860, -0.0582, -0.3646, 1.1569, 0.2340, -0.0390, 0.1387, 0.5765]),
        ("OLYMPUS E-M1 MARK II", [0.9383, -0.4009, -0.0514, -0.3602, 1.1485, 0.2390, -0.0331, 0.1384, 0.5875]),
        ("OLYMPUS E-M1", [0.7687, -0.1984, -0.0630, -0.4541, 1.2316, 0.2519, -0.0792, 0.1745, 0.6455]),
        ("OLYMPUS E-M5", [0.8380, -0.2630, -0.0639, -0.2887, 1.0725, 0.2496, -0.0627, 0.1427, 0.5438]),
        ("OLYMPUS E-P", [0.8380, -0.2630, -0.0639, -0.2887, 1.0725, 0.2496, -0.0627, 0.1427, 0.5438]), // Generic PEN
        ("OLYMPUS", [0.8380, -0.2630, -0.0639, -0.2887, 1.0725, 0.2496, -0.0627, 0.1427, 0.5438]), // Generic Olympus

        // Pentax
        ("PENTAX K-1 MARK II", [0.8596, -0.2981, -0.0377, -0.4241, 1.2164, 0.2358, -0.0791, 0.1799, 0.5765]),
        ("PENTAX K-1", [0.8596, -0.2981, -0.0377, -0.4241, 1.2164, 0.2358, -0.0791, 0.1799, 0.5765]),
        ("PENTAX K-5 II", [0.8170, -0.2725, -0.0639, -0.4440, 1.2017, 0.2744, -0.0771, 0.1465, 0.6599]),
        ("PENTAX K-5", [0.8713, -0.2833, -0.0743, -0.4342, 1.2304, 0.2316, -0.0778, 0.1805, 0.5765]),
        ("PENTAX K-3", [0.7415, -0.2052, -0.0627, -0.5360, 1.3340, 0.2199, -0.1238, 0.1981, 0.6338]),
        ("PENTAX K-70", [0.8766, -0.3149, -0.0622, -0.3640, 1.1426, 0.2508, -0.0476, 0.1532, 0.6170]),
        ("PENTAX", [0.8170, -0.2725, -0.0639, -0.4440, 1.2017, 0.2744, -0.0771, 0.1465, 0.6599]), // Generic Pentax

        // Fujifilm — dcraw adobe_coeff values divided by 10000
        // X-Trans IV sensor (2018+): X-T3, X-T4, X-T30, X-Pro3, X-H2, X-S10, etc.
        ("FUJIFILM X-T4", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-T30", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-T3", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        // X-Trans III sensor (2016-2017): X-T2, X-T20, X-Pro2, X-H1, X-E3
        ("FUJIFILM X-T2", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]),
        ("FUJIFILM X-T1", [0.8458, -0.2451, -0.0855, -0.4597, 1.2447, 0.2407, -0.1475, 0.2482, 0.6526]),
        ("FUJIFILM X-E4", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-E3", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]),
        ("FUJIFILM X-E", [0.8458, -0.2451, -0.0855, -0.4597, 1.2447, 0.2407, -0.1475, 0.2482, 0.6526]),
        ("FUJIFILM X-PRO3", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-PRO2", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]),
        ("FUJIFILM X-PRO1", [1.0413, -0.3996, -0.0993, -0.3721, 1.1640, 0.2361, -0.0733, 0.1540, 0.6011]),
        ("FUJIFILM X-PRO", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]),
        ("FUJIFILM X-H2", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-H1", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]),
        ("FUJIFILM X-H", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-S", [1.3426, -0.6334, -0.1177, -0.4244, 1.2136, 0.2371, -0.0580, 0.1303, 0.5980]),
        ("FUJIFILM X-", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]), // Generic X-series (X-Trans III)
        ("FUJIFILM GFX", [1.1434, -0.5154, -0.0898, -0.3312, 1.1456, 0.2086, -0.0330, 0.1400, 0.5516]), // GFX medium format
        ("FUJIFILM", [1.0593, -0.4856, -0.0646, -0.2840, 1.1002, 0.2053, -0.0414, 0.1439, 0.5542]), // Generic Fujifilm

        // Sony
        ("SONY ILCE-7RM4", [0.7828, -0.2578, -0.0535, -0.4518, 1.2374, 0.2414, -0.0472, 0.1537, 0.6399]),
        ("SONY ILCE-7RM3", [0.7828, -0.2578, -0.0535, -0.4518, 1.2374, 0.2414, -0.0472, 0.1537, 0.6399]),
        ("SONY ILCE-7M3", [0.7374, -0.2389, -0.0551, -0.5765, 1.3303, 0.2715, -0.1014, 0.2072, 0.6276]),
        ("SONY ILCE-7", [0.5271, -0.0712, -0.0550, -0.4758, 1.2339, 0.2672, -0.0922, 0.1827, 0.6119]),
        ("SONY ILCE-6", [0.5991, -0.1456, -0.0455, -0.4764, 1.2135, 0.2930, -0.1024, 0.2066, 0.5849]),
        ("SONY", [0.5271, -0.0712, -0.0550, -0.4758, 1.2339, 0.2672, -0.0922, 0.1827, 0.6119]), // Generic Sony
    ];
}


