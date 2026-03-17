// Shared TIFF IFD reading utilities for camera raw format parsers.
// Provides endian-aware binary reading, IFD parsing, and tag value extraction.
// Used by DNG, CR2, NEF, ARW, ORF, RW2, PEF, SRW and other TIFF-based raw formats.
// Reference: TIFF 6.0 specification, BigTIFF extension, Adobe DNG specification 1.7

using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Shared TIFF IFD reading utilities for camera raw format parsers.
/// Provides endian-aware binary reading, IFD parsing, and tag value extraction.
/// Used by DNG, CR2, NEF, ARW, ORF, RW2, PEF, SRW and other TIFF-based raw formats.
/// </summary>
internal static class TiffIfdReader
{
    // ── TIFF Data Type Constants ──────────────────────────────────────────────
    internal const ushort TypeByte = 1;
    internal const ushort TypeAscii = 2;
    internal const ushort TypeShort = 3;
    internal const ushort TypeLong = 4;
    internal const ushort TypeRational = 5;
    internal const ushort TypeSbyte = 6;
    internal const ushort TypeUndefined = 7;
    internal const ushort TypeSshort = 8;
    internal const ushort TypeSlong = 9;
    internal const ushort TypeSrational = 10;
    internal const ushort TypeFloat = 11;
    internal const ushort TypeDouble = 12;
    internal const ushort TypeLong8 = 16;

    // ── Standard TIFF Tags ───────────────────────────────────────────────────
    internal const ushort TagImageWidth = 256;
    internal const ushort TagImageLength = 257;
    internal const ushort TagBitsPerSample = 258;
    internal const ushort TagCompression = 259;
    internal const ushort TagPhotometric = 262;
    internal const ushort TagMake = 271;
    internal const ushort TagModel = 272;
    internal const ushort TagStripOffsets = 273;
    internal const ushort TagOrientation = 274;
    internal const ushort TagSamplesPerPixel = 277;
    internal const ushort TagRowsPerStrip = 278;
    internal const ushort TagStripByteCounts = 279;
    internal const ushort TagXResolution = 282;
    internal const ushort TagYResolution = 283;
    internal const ushort TagSubIFDs = 330;
    internal const ushort TagExtraSamples = 338;

    // ── EXIF / Camera Tags ───────────────────────────────────────────────────
    internal const ushort TagExifIFD = 34665;
    internal const ushort TagGpsIFD = 34853;
    internal const ushort TagExposureTime = 33434;
    internal const ushort TagFNumber = 33437;
    internal const ushort TagIsoSpeed = 34855;
    internal const ushort TagDateTimeOriginal = 36867;
    internal const ushort TagFocalLength = 37386;

    // ── DNG-Specific Tags ────────────────────────────────────────────────────
    internal const ushort TagDNGVersion = 50706;
    internal const ushort TagUniqueCameraModel = 50708;
    internal const ushort TagCFARepeatPatternDim = 33421;
    internal const ushort TagCFAPattern = 33422;
    internal const ushort TagLinearizationTable = 50712;
    internal const ushort TagBlackLevelRepeatDim = 50713;
    internal const ushort TagBlackLevel = 50714;
    internal const ushort TagWhiteLevel = 50717;
    internal const ushort TagDefaultScale = 50718;
    internal const ushort TagDefaultCropOrigin = 50719;
    internal const ushort TagDefaultCropSize = 50720;
    internal const ushort TagColorMatrix1 = 50721;
    internal const ushort TagColorMatrix2 = 50722;
    internal const ushort TagCameraCalibration1 = 50723;
    internal const ushort TagAsShotNeutral = 50728;
    internal const ushort TagAsShotWhiteXY = 50729;
    internal const ushort TagActiveArea = 50829;
    internal const ushort TagForwardMatrix1 = 50964;

    // ── CR2-Specific Tags ────────────────────────────────────────────────────
    internal const ushort TagCR2Slices = 50752;

    // ── Endian-Aware Binary Readers ──────────────────────────────────────────

    /// <summary>Reads an unsigned 16-bit integer. Returns int to match TiffCoder convention.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadUInt16(byte[] data, int offset, bool bigEndian) => bigEndian
        ? (data[offset] << 8) | data[offset + 1]
        : data[offset] | (data[offset + 1] << 8);

    /// <summary>Reads an unsigned 32-bit integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ReadUInt32(byte[] data, int offset, bool bigEndian) => bigEndian
        ? (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3])
        : (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    /// <summary>Reads a signed 64-bit integer (BigTIFF offsets and counts).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ReadInt64(byte[] data, long offset, bool bigEndian)
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

    /// <summary>Reads a signed 16-bit integer. Useful for signed TIFF tag types (SSHORT).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short ReadInt16(byte[] data, int offset, bool bigEndian) =>
        (short)ReadUInt16(data, offset, bigEndian);

    /// <summary>Reads a signed 32-bit integer. Useful for signed TIFF tag types (SLONG).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadInt32(byte[] data, int offset, bool bigEndian) =>
        (int)ReadUInt32(data, offset, bigEndian);

    // ── TIFF Header Parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Parses the TIFF header to determine byte order, TIFF variant, and first IFD offset.
    /// </summary>
    /// <param name="data">Raw file bytes.</param>
    /// <param name="bigEndian">True if the file uses big-endian (Motorola) byte order.</param>
    /// <param name="isBigTiff">True if the file is BigTIFF (magic 43) rather than classic TIFF (magic 42).</param>
    /// <param name="firstIfdOffset">Byte offset to the first IFD.</param>
    /// <returns>True if the header is a valid TIFF/BigTIFF header; false otherwise.</returns>
    internal static bool ParseHeader(byte[] data, out bool bigEndian, out bool isBigTiff, out long firstIfdOffset)
    {
        bigEndian = false;
        isBigTiff = false;
        firstIfdOffset = 0;

        if (data.Length < 8) return false;

        bool littleEndian = data[0] == 'I' && data[1] == 'I';
        bool big = data[0] == 'M' && data[1] == 'M';
        if (!littleEndian && !big) return false;

        bigEndian = big;
        int magic = ReadUInt16(data, 2, bigEndian);

        if (magic == 43)
        {
            // BigTIFF: bytes 4-5 = offset size (must be 8), bytes 8-15 = first IFD offset
            if (data.Length < 16) return false;
            int offsetSize = ReadUInt16(data, 4, bigEndian);
            if (offsetSize != 8) return false;
            isBigTiff = true;
            firstIfdOffset = ReadInt64(data, 8, bigEndian);
        }
        else if (magic == 42)
        {
            // Classic TIFF: bytes 4-7 = first IFD offset
            firstIfdOffset = ReadUInt32(data, 4, bigEndian);
        }
        else
        {
            return false;
        }

        return firstIfdOffset > 0 && firstIfdOffset < data.Length;
    }

    // ── Type Size ────────────────────────────────────────────────────────────

    /// <summary>Returns the byte size of a TIFF data type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetTypeSize(ushort type) => type switch
    {
        TypeByte or TypeAscii or TypeSbyte or TypeUndefined => 1,
        TypeShort or TypeSshort => 2,
        TypeLong or TypeSlong or TypeFloat => 4,
        TypeRational or TypeSrational or TypeDouble or TypeLong8 => 8,
        _ => 1
    };

    // ── Tag Value Reading ────────────────────────────────────────────────────

    /// <summary>
    /// Reads an array of tag values from raw TIFF data. Handles all standard TIFF types.
    /// For RATIONAL types, only the numerator is stored in the int array — use
    /// <see cref="GetTagRational"/> or <see cref="GetTagRationalArray"/> for full rational reading.
    /// </summary>
    internal static int[] ReadTagValues(byte[] data, int offset, ushort type, int count, bool bigEndian)
    {
        if (count <= 0 || count > data.Length) return [];
        int[] values = new int[count];
        int typeSize = GetTypeSize(type);

        for (int i = 0; i < count; i++)
        {
            int pos = offset + i * typeSize;
            if (pos >= data.Length) break;

            values[i] = type switch
            {
                TypeByte or TypeAscii or TypeUndefined => data[pos],
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

    // ── IFD Parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single IFD (Image File Directory) at the given byte offset.
    /// Returns the parsed tags and the offset to the next IFD (0 if none).
    /// </summary>
    internal static (Dictionary<ushort, TiffTag> tags, long nextIfdOffset) ParseIfd(
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
            tags[tagId] = new TiffTag(tagId, type, (int)count, values, valueOffset);
        }

        long nextOffset;
        long afterEntries = entriesStart + (long)entryCount * entrySize;
        if (isBigTiff)
            nextOffset = afterEntries + 8 <= data.Length ? ReadInt64(data, afterEntries, bigEndian) : 0;
        else
            nextOffset = afterEntries + 4 <= data.Length ? ReadUInt32(data, (int)afterEntries, bigEndian) : 0;

        return (tags, nextOffset);
    }

    // ── Multi-IFD Traversal ──────────────────────────────────────────────────

    /// <summary>
    /// Parses all IFDs in a TIFF file by following the next-IFD chain.
    /// Stops when the chain ends or <paramref name="maxIfds"/> is reached (safety limit).
    /// </summary>
    internal static List<Dictionary<ushort, TiffTag>> ParseAllIfds(
        byte[] data, bool bigEndian, long firstIfdOffset, bool isBigTiff = false, int maxIfds = 16)
    {
        var ifds = new List<Dictionary<ushort, TiffTag>>();
        long offset = firstIfdOffset;

        while (offset > 0 && offset < data.Length && ifds.Count < maxIfds)
        {
            var (tags, nextIfdOffset) = ParseIfd(data, offset, bigEndian, isBigTiff);
            if (tags.Count == 0) break;
            ifds.Add(tags);
            offset = nextIfdOffset;
        }

        return ifds;
    }

    // ── Sub-IFD Reading ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses a sub-IFD referenced by a tag in the parent IFD (e.g. EXIF IFD, GPS IFD, SubIFDs).
    /// Returns null if the tag is not present or the offset is invalid.
    /// </summary>
    internal static Dictionary<ushort, TiffTag>? ParseSubIfd(
        byte[] data, Dictionary<ushort, TiffTag> parentTags, ushort subIfdTag,
        bool bigEndian, bool isBigTiff = false)
    {
        if (!parentTags.TryGetValue(subIfdTag, out var tag) || tag.Values.Length == 0)
            return null;

        long subIfdOffset = tag.Values[0] & 0xFFFFFFFFL; // Treat as unsigned offset
        if (subIfdOffset <= 0 || subIfdOffset >= data.Length)
            return null;

        var (subTags, _) = ParseIfd(data, subIfdOffset, bigEndian, isBigTiff);
        return subTags.Count > 0 ? subTags : null;
    }

    /// <summary>
    /// Parses ALL sub-IFDs referenced by a tag (e.g. SubIFDs tag 330 can have multiple offsets).
    /// Returns an empty list if the tag is not present. Camera raw formats like DNG and NEF store
    /// thumbnails and full-resolution raw data in separate SubIFDs — callers must select the right one.
    /// </summary>
    internal static List<Dictionary<ushort, TiffTag>> ParseAllSubIfds(
        byte[] data, Dictionary<ushort, TiffTag> parentTags, ushort subIfdTag,
        bool bigEndian, bool isBigTiff = false)
    {
        var result = new List<Dictionary<ushort, TiffTag>>();
        if (!parentTags.TryGetValue(subIfdTag, out var tag) || tag.Values.Length == 0)
            return result;

        for (int i = 0; i < tag.Values.Length; i++)
        {
            long subIfdOffset = tag.Values[i] & 0xFFFFFFFFL;
            if (subIfdOffset <= 0 || subIfdOffset >= data.Length)
                continue;

            var (subTags, _) = ParseIfd(data, subIfdOffset, bigEndian, isBigTiff);
            if (subTags.Count > 0)
                result.Add(subTags);
        }

        return result;
    }

    /// <summary>
    /// NewSubFileType tag: 0 = full-resolution image, 1 = reduced-resolution/thumbnail.
    /// </summary>
    internal const ushort TagNewSubFileType = 254;

    // ── Tag Helper Methods ───────────────────────────────────────────────────

    /// <summary>
    /// <summary>
    /// Gets the absolute file offset where a tag's data is stored. For small tags (≤4 bytes),
    /// this is the inline value position within the IFD entry. For large tags, this is the
    /// offset pointer that was stored in the IFD entry. Used for MakerNote sub-parsing where
    /// the raw file offset of tag data is needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetTagLargeDataOffset(TiffTag tag)
    {
        return tag.ValueOffset;
    }

    /// <summary>
    /// Gets the first integer value from a tag, or <paramref name="defaultValue"/> if not present.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetTagInt(Dictionary<ushort, TiffTag> tags, ushort tagId, int defaultValue = 0)
    {
        if (tags.TryGetValue(tagId, out var tag) && tag.Values.Length > 0)
            return tag.Values[0];
        return defaultValue;
    }

    /// <summary>
    /// Gets the full value array from a tag, or an empty array if not present.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int[] GetTagArray(Dictionary<ushort, TiffTag> tags, ushort tagId)
    {
        if (tags.TryGetValue(tagId, out var tag))
            return tag.Values;
        return [];
    }

    /// <summary>
    /// Gets the first value from a tag as a long. Handles unsigned 32-bit values correctly
    /// for large offsets that would overflow int (e.g. strip offsets in large files).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetTagLong(Dictionary<ushort, TiffTag> tags, ushort tagId, long defaultValue = 0)
    {
        if (tags.TryGetValue(tagId, out var tag) && tag.Values.Length > 0)
            return tag.Values[0] & 0xFFFFFFFFL; // Treat stored int as unsigned
        return defaultValue;
    }

    /// <summary>
    /// Reads an ASCII string tag value from the raw file data.
    /// ASCII tags store their data at the value offset; this reads <c>count</c> bytes
    /// and trims the null terminator.
    /// </summary>
    /// <returns>The string value, or null if the tag is not present.</returns>
    internal static string? GetTagString(byte[] data, Dictionary<ushort, TiffTag> tags, ushort tagId, bool bigEndian)
    {
        if (!tags.TryGetValue(tagId, out var tag))
            return null;

        int count = tag.Count;
        if (count <= 0) return null;

        if (count <= 4)
        {
            // Value stored inline in the IFD entry — reconstruct from parsed int values
            // For short ASCII strings (1-4 bytes), the values are stored as individual bytes
            var sb = new StringBuilder(count);
            for (int i = 0; i < count && i < tag.Values.Length; i++)
            {
                int ch = tag.Values[i];
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // Value stored at an external offset — the first parsed value IS the offset
        // (for ASCII type with count > 4, ReadTagValues reads byte values at the offset)
        // We need to reconstruct the offset. For tags with count > 4, the IFD entry's
        // value field contains the offset pointer. We can read the string directly from
        // the Values array since ReadTagValues reads each byte individually for TypeAscii.
        if (tag.Type == TypeAscii)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < tag.Values.Length; i++)
            {
                int ch = tag.Values[i];
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    /// <summary>
    /// Reads a RATIONAL tag value (two consecutive uint32: numerator/denominator) and returns
    /// the result as a double. Returns 0.0 if the tag is not present or denominator is zero.
    /// </summary>
    internal static double GetTagRational(byte[] data, Dictionary<ushort, TiffTag> tags, ushort tagId, bool bigEndian)
    {
        if (!tags.TryGetValue(tagId, out var tag) || tag.Values.Length == 0)
            return 0.0;

        // For RATIONAL/SRATIONAL with count=1, the data is 8 bytes (numerator + denominator).
        // If count=1 and type size is 8, the value is stored at an external offset.
        // The tag's Values[0] only has the numerator. We need the raw data offset.
        // Since ParseIfd stored values from ReadTagValues which reads at the value offset,
        // and RATIONAL reads only numerator into Values[0], we need to find the actual
        // data location. The offset was resolved during parsing, so we reconstruct it:
        // For RATIONAL, ReadTagValues reads at the resolved offset. The first value is
        // numerator = (int)ReadUInt32(data, pos, bigEndian). The denominator is at pos+4.
        // We can find pos by searching for where the numerator matches, but that's fragile.
        // Instead, re-derive the offset from the tag entry. However, we don't store the
        // raw offset in TiffTag. So we use the Values array: Values[0] is numerator as int.
        // We need the denominator too. For count >= 2 rational values, ReadTagValues only
        // stored numerators at stride=8. So we lost denominator info in the int[] array.
        //
        // The practical solution: the tag's value offset is where data was read from.
        // For count=1 RATIONAL (8 bytes > 4), the offset is stored in the IFD entry's
        // value field. But we already resolved it. We don't have the raw offset anymore.
        //
        // Workaround: scan the data for the matching numerator at a plausible offset.
        // Better approach: re-read from the tag. Since Values[0] was read as
        // (int)ReadUInt32(data, pos, bigEndian) for RATIONAL, and RATIONAL entries with
        // count=1 always point to an external 8-byte block, the offset is in the IFD.
        // But we don't store offsets in TiffTag.
        //
        // Best practical approach for the current TiffTag structure: use a helper that
        // finds the value offset by re-scanning. However, this is expensive.
        // For now, use the simpler approach that works for most camera raw scenarios:
        // If the tag has at least 2 values (count >= 2 rationals are stored as
        // alternating num/denom in the Values array due to typeSize=8 stride),
        // this doesn't help either since ReadTagValues skips denominators.
        //
        // Cleanest solution with current architecture: GetTagRawBytes gives us the raw
        // bytes, then we read numerator and denominator from those.
        byte[]? raw = GetTagRawBytes(data, tags, tagId, bigEndian);
        if (raw == null || raw.Length < 8) return 0.0;

        uint numerator = ReadUInt32(raw, 0, bigEndian);
        uint denominator = ReadUInt32(raw, 4, bigEndian);

        if (tag.Type == TypeSrational)
        {
            int signedNum = (int)numerator;
            int signedDenom = (int)denominator;
            return signedDenom == 0 ? 0.0 : (double)signedNum / signedDenom;
        }

        return denominator == 0 ? 0.0 : (double)numerator / denominator;
    }

    /// <summary>
    /// Reads multiple RATIONAL values from a tag and returns them as a double array.
    /// Each RATIONAL is a pair of uint32 values (numerator/denominator).
    /// </summary>
    /// <param name="data">Raw file bytes.</param>
    /// <param name="tags">Parsed IFD tags.</param>
    /// <param name="tagId">Tag ID to read.</param>
    /// <param name="bigEndian">Byte order.</param>
    /// <param name="count">Number of RATIONAL values to read.</param>
    /// <returns>Array of doubles, or empty array if tag not found.</returns>
    internal static double[] GetTagRationalArray(
        byte[] data, Dictionary<ushort, TiffTag> tags, ushort tagId, bool bigEndian, int count)
    {
        if (!tags.TryGetValue(tagId, out var tag))
            return [];

        byte[]? raw = GetTagRawBytes(data, tags, tagId, bigEndian);
        if (raw == null) return [];

        bool signed = tag.Type == TypeSrational;
        int available = Math.Min(count, raw.Length / 8);
        double[] result = new double[available];

        for (int i = 0; i < available; i++)
        {
            int pos = i * 8;
            if (signed)
            {
                int num = (int)ReadUInt32(raw, pos, bigEndian);
                int denom = (int)ReadUInt32(raw, pos + 4, bigEndian);
                result[i] = denom == 0 ? 0.0 : (double)num / denom;
            }
            else
            {
                uint num = ReadUInt32(raw, pos, bigEndian);
                uint denom = ReadUInt32(raw, pos + 4, bigEndian);
                result[i] = denom == 0 ? 0.0 : (double)num / denom;
            }
        }

        return result;
    }

    // ── Raw Byte Extraction ──────────────────────────────────────────────────

    /// <summary>
    /// Reads the raw bytes for a tag's value data directly from the file.
    /// Essential for reading binary blobs like ICC profiles (tag 34675), MakerNotes,
    /// XMP data (tag 700), and RATIONAL values that need numerator+denominator.
    /// </summary>
    /// <returns>Raw byte array, or null if the tag is not present or data is out of range.</returns>
    internal static byte[]? GetTagRawBytes(byte[] data, Dictionary<ushort, TiffTag> tags, ushort tagId, bool bigEndian)
    {
        if (!tags.TryGetValue(tagId, out var tag))
            return null;

        int typeSize = GetTypeSize(tag.Type);
        int totalBytes = typeSize * tag.Count;
        if (totalBytes <= 0) return null;

        // We need to find the actual byte offset where this tag's data lives.
        // For inline values (totalBytes <= 4 for classic TIFF), the data is in the IFD entry.
        // For external values, we need the offset pointer from the IFD entry.
        //
        // Since TiffTag doesn't store the raw offset, we reconstruct from the Values array:
        // For types where ReadTagValues reads byte-by-byte (Byte, ASCII, Undefined, Sbyte),
        // we can reconstruct the raw bytes directly from Values[].
        // For all other types, we need to find the offset in the file data.
        //
        // Strategy: for small inline values, reconstruct from Values[]. For external values,
        // scan the IFD to find this tag's entry and extract the offset.
        if (totalBytes <= 4 && tag.Type is TypeByte or TypeAscii or TypeUndefined or TypeSbyte)
        {
            byte[] result = new byte[totalBytes];
            for (int i = 0; i < totalBytes && i < tag.Values.Length; i++)
                result[i] = (byte)tag.Values[i];
            return result;
        }

        // For external data or non-byte types, we need to locate the data in the file.
        // Use a reverse lookup: find where the Values were read from by matching the first value.
        // This is the pragmatic approach given the current TiffTag structure.
        int offset = FindTagDataOffset(data, tag, bigEndian);
        if (offset < 0 || offset + totalBytes > data.Length) return null;

        byte[] bytes = new byte[totalBytes];
        Array.Copy(data, offset, bytes, 0, totalBytes);
        return bytes;
    }

    /// <summary>
    /// Locates the byte offset in the file where a tag's value data resides.
    /// Scans the file's IFD entries to find the matching tag and extract its value/offset field.
    /// </summary>
    private static int FindTagDataOffset(byte[] data, TiffTag tag, bool bigEndian)
    {
        // Parse header to find IFDs, then scan entries for this tag
        if (!ParseHeader(data, out bool parsedBigEndian, out bool isBigTiff, out long firstIfdOffset))
            return -1;

        long ifdOffset = firstIfdOffset;
        int maxIfds = 32;

        while (ifdOffset > 0 && ifdOffset < data.Length && maxIfds-- > 0)
        {
            int entryCount;
            int entrySize;
            long entriesStart;

            if (isBigTiff)
            {
                entryCount = (int)ReadInt64(data, ifdOffset, parsedBigEndian);
                entrySize = 20;
                entriesStart = ifdOffset + 8;
            }
            else
            {
                entryCount = ReadUInt16(data, (int)ifdOffset, parsedBigEndian);
                entrySize = 12;
                entriesStart = ifdOffset + 2;
            }

            for (int i = 0; i < entryCount; i++)
            {
                long entryOffset = entriesStart + i * entrySize;
                if (entryOffset + entrySize > data.Length) break;

                ushort entryTagId = (ushort)ReadUInt16(data, (int)entryOffset, parsedBigEndian);
                if (entryTagId != tag.Id) continue;

                ushort type = (ushort)ReadUInt16(data, (int)entryOffset + 2, parsedBigEndian);
                int typeSize = GetTypeSize(type);

                if (isBigTiff)
                {
                    long count = ReadInt64(data, entryOffset + 4, parsedBigEndian);
                    long valueSize = typeSize * count;
                    return valueSize <= 8
                        ? (int)(entryOffset + 12)
                        : (int)ReadInt64(data, entryOffset + 12, parsedBigEndian);
                }
                else
                {
                    long count = ReadUInt32(data, (int)entryOffset + 4, parsedBigEndian);
                    long valueSize = typeSize * count;
                    return valueSize <= 4
                        ? (int)(entryOffset + 8)
                        : (int)ReadUInt32(data, (int)entryOffset + 8, parsedBigEndian);
                }
            }

            // Move to next IFD
            long afterEntries = entriesStart + (long)entryCount * entrySize;
            if (isBigTiff)
                ifdOffset = afterEntries + 8 <= data.Length ? ReadInt64(data, afterEntries, parsedBigEndian) : 0;
            else
                ifdOffset = afterEntries + 4 <= data.Length ? ReadUInt32(data, (int)afterEntries, parsedBigEndian) : 0;
        }

        // Tag might be in a sub-IFD — scan all IFDs for sub-IFD pointers
        return FindTagInSubIfds(data, tag, parsedBigEndian, isBigTiff, firstIfdOffset);
    }

    private static int FindTagInSubIfds(byte[] data, TiffTag tag, bool bigEndian, bool isBigTiff, long firstIfdOffset)
    {
        // Common sub-IFD tags that contain nested IFDs
        ushort[] subIfdTags = [TagSubIFDs, TagExifIFD, TagGpsIFD];

        var allIfds = ParseAllIfds(data, bigEndian, firstIfdOffset, isBigTiff);
        foreach (var ifd in allIfds)
        {
            foreach (ushort subTag in subIfdTags)
            {
                if (!ifd.TryGetValue(subTag, out var subIfdPointer) || subIfdPointer.Values.Length == 0)
                    continue;

                for (int s = 0; s < subIfdPointer.Values.Length; s++)
                {
                    long subOffset = subIfdPointer.Values[s] & 0xFFFFFFFFL;
                    if (subOffset <= 0 || subOffset >= data.Length) continue;

                    int result = FindTagInIfd(data, tag, bigEndian, isBigTiff, subOffset);
                    if (result >= 0) return result;
                }
            }
        }

        return -1;
    }

    private static int FindTagInIfd(byte[] data, TiffTag tag, bool bigEndian, bool isBigTiff, long ifdOffset)
    {
        int entryCount;
        int entrySize;
        long entriesStart;

        if (isBigTiff)
        {
            entryCount = (int)ReadInt64(data, ifdOffset, bigEndian);
            entrySize = 20;
            entriesStart = ifdOffset + 8;
        }
        else
        {
            if ((int)ifdOffset + 2 > data.Length) return -1;
            entryCount = ReadUInt16(data, (int)ifdOffset, bigEndian);
            entrySize = 12;
            entriesStart = ifdOffset + 2;
        }

        for (int i = 0; i < entryCount; i++)
        {
            long entryOffset = entriesStart + i * entrySize;
            if (entryOffset + entrySize > data.Length) break;

            ushort entryTagId = (ushort)ReadUInt16(data, (int)entryOffset, bigEndian);
            if (entryTagId != tag.Id) continue;

            ushort type = (ushort)ReadUInt16(data, (int)entryOffset + 2, bigEndian);
            int typeSize = GetTypeSize(type);

            if (isBigTiff)
            {
                long count = ReadInt64(data, entryOffset + 4, bigEndian);
                long valueSize = typeSize * count;
                return valueSize <= 8
                    ? (int)(entryOffset + 12)
                    : (int)ReadInt64(data, entryOffset + 12, bigEndian);
            }
            else
            {
                long count = ReadUInt32(data, (int)entryOffset + 4, bigEndian);
                long valueSize = typeSize * count;
                return valueSize <= 4
                    ? (int)(entryOffset + 8)
                    : (int)ReadUInt32(data, (int)entryOffset + 8, bigEndian);
            }
        }

        return -1;
    }
}
