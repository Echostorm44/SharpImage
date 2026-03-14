using System.Buffers.Binary;
using System.Text;

namespace SharpImage.Metadata;

/// <summary>
/// EXIF/TIFF IFD parser. Reads and writes EXIF data from JPEG APP1 segments and PNG eXIf chunks.
/// Follows the TIFF 6.0 / EXIF 2.32 specification.
/// </summary>
public static class ExifParser
{
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();

    /// <summary>
    /// Parses an EXIF profile from a JPEG APP1 segment payload (after the marker and length).
    /// Expected format: "Exif\0\0" + TIFF data.
    /// </summary>
    public static ExifProfile? ParseFromApp1(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return null;

        // Check "Exif\0\0" header
        if (!data[..6].SequenceEqual(ExifHeader))
            return null;

        return ParseTiffData(data[6..]);
    }

    /// <summary>
    /// Parses an EXIF profile from raw TIFF IFD data (PNG eXIf chunk format).
    /// </summary>
    public static ExifProfile? ParseFromTiff(ReadOnlySpan<byte> data)
    {
        return ParseTiffData(data);
    }

    /// <summary>
    /// Serializes an EXIF profile to a JPEG APP1 segment payload (including "Exif\0\0" header).
    /// If the profile has RawData, returns that for exact round-trip; otherwise rebuilds from tags.
    /// </summary>
    public static byte[] SerializeForApp1(ExifProfile profile)
    {
        // Use raw data for round-trip if available
        if (profile.RawData is not null)
        {
            var result = new byte[6 + profile.RawData.Length];
            ExifHeader.CopyTo(result, 0);
            profile.RawData.CopyTo(result, 6);
            return result;
        }

        // Build from tags
        byte[] tiffData = SerializeTiff(profile);
        var output = new byte[6 + tiffData.Length];
        ExifHeader.CopyTo(output, 0);
        tiffData.CopyTo(output, 6);
        return output;
    }

    /// <summary>
    /// Serializes an EXIF profile as raw TIFF data (for PNG eXIf chunks).
    /// </summary>
    public static byte[] SerializeForPngExif(ExifProfile profile)
    {
        if (profile.RawData is not null)
            return (byte[])profile.RawData.Clone();
        return SerializeTiff(profile);
    }

    private static ExifProfile? ParseTiffData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return null;

        // Determine byte order
        bool littleEndian;
        if (data[0] == 0x49 && data[1] == 0x49)
            littleEndian = true;
        else if (data[0] == 0x4D && data[1] == 0x4D)
            littleEndian = false;
        else
            return null;

        // Verify magic number 42
        ushort magic = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (magic != 42) return null;

        // Get IFD0 offset
        uint ifd0Offset = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

        var profile = new ExifProfile
        {
            IsLittleEndian = littleEndian,
            RawData = data.ToArray()
        };

        // Parse IFD0
        ReadIfd(data, ifd0Offset, littleEndian, profile.Ifd0Tags);

        // Look for EXIF sub-IFD pointer
        var exifPointer = profile.Ifd0Tags.FirstOrDefault(e => e.Tag == ExifTag.ExifIfdPointer);
        if (exifPointer.Value is not null)
        {
            uint exifOffset = exifPointer.GetUInt32(littleEndian);
            if (exifOffset > 0 && exifOffset < data.Length)
            {
                ReadIfd(data, exifOffset, littleEndian, profile.ExifTags);
            }
        }

        // Look for GPS sub-IFD pointer
        var gpsPointer = profile.Ifd0Tags.FirstOrDefault(e => e.Tag == ExifTag.GpsIfdPointer);
        if (gpsPointer.Value is not null)
        {
            uint gpsOffset = gpsPointer.GetUInt32(littleEndian);
            if (gpsOffset > 0 && gpsOffset < data.Length)
            {
                ReadIfd(data, gpsOffset, littleEndian, profile.GpsTags);
            }
        }

        return profile;
    }

    private static void ReadIfd(ReadOnlySpan<byte> data, uint offset, bool le, List<ExifEntry> entries)
    {
        if (offset + 2 > data.Length) return;

        ushort entryCount = le
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[(int)offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[(int)offset..]);

        uint pos = offset + 2;
        for (int i = 0; i < entryCount; i++)
        {
            if (pos + 12 > data.Length) break;

            ushort tag = le
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[(int)pos..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[(int)pos..]);

            ushort type = le
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(pos + 2)..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[(int)(pos + 2)..]);

            uint count = le
                ? BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(pos + 4)..])
                : BinaryPrimitives.ReadUInt32BigEndian(data[(int)(pos + 4)..]);

            int typeSize = GetTypeSize((ExifDataType)type);
            uint totalSize = count * (uint)typeSize;

            byte[] value;
            if (totalSize <= 4)
            {
                // Value fits in the 4-byte value/offset field
                value = data.Slice((int)(pos + 8), (int)totalSize).ToArray();
            }
            else
            {
                // Value is stored at an offset
                uint valueOffset = le
                    ? BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(pos + 8)..])
                    : BinaryPrimitives.ReadUInt32BigEndian(data[(int)(pos + 8)..]);

                if (valueOffset + totalSize <= data.Length)
                {
                    value = data.Slice((int)valueOffset, (int)totalSize).ToArray();
                }
                else
                {
                    value = [];
                }
            }

            entries.Add(new ExifEntry
            {
                Tag = tag,
                DataType = (ExifDataType)type,
                Count = count,
                Value = value
            });

            pos += 12;
        }
    }

    private static int GetTypeSize(ExifDataType type) => type switch
    {
        ExifDataType.Byte => 1,
        ExifDataType.Ascii => 1,
        ExifDataType.Short => 2,
        ExifDataType.Long => 4,
        ExifDataType.Rational => 8,
        ExifDataType.SignedByte => 1,
        ExifDataType.Undefined => 1,
        ExifDataType.SignedShort => 2,
        ExifDataType.SignedLong => 4,
        ExifDataType.SignedRational => 8,
        ExifDataType.Float => 4,
        ExifDataType.Double => 8,
        _ => 1
    };

    private static byte[] SerializeTiff(ExifProfile profile)
    {
        // Calculate total size needed
        bool le = profile.IsLittleEndian;
        var allIfd0 = profile.Ifd0Tags.Where(e => e.Tag != ExifTag.ExifIfdPointer && e.Tag != ExifTag.GpsIfdPointer).ToList();
        bool hasExif = profile.ExifTags.Count > 0;
        bool hasGps = profile.GpsTags.Count > 0;

        if (hasExif)
            allIfd0.Add(CreatePointerEntry(ExifTag.ExifIfdPointer, 0, le)); // placeholder
        if (hasGps)
            allIfd0.Add(CreatePointerEntry(ExifTag.GpsIfdPointer, 0, le)); // placeholder

        // TIFF header: 8 bytes
        // IFD0: 2 + 12*count + 4 (next IFD offset)
        // Then overflow data for IFD0
        // Then EXIF IFD (if any)
        // Then GPS IFD (if any)

        using var ms = new MemoryStream();

        // Write TIFF header
        if (le)
        {
            ms.Write("II"u8);
            WriteUInt16(ms, 42, true);
            WriteUInt32(ms, 8, true); // IFD0 starts at offset 8
        }
        else
        {
            ms.Write("MM"u8);
            WriteUInt16(ms, 42, false);
            WriteUInt32(ms, 8, false);
        }

        // Write IFD0
        int ifd0Count = allIfd0.Count;
        WriteUInt16(ms, (ushort)ifd0Count, le);

        // Reserve space for IFD entries
        long ifd0EntriesStart = ms.Position;
        for (int i = 0; i < ifd0Count; i++)
        {
            ms.Write(new byte[12]); // placeholder
        }
        WriteUInt32(ms, 0, le); // no next IFD

        // Write IFD0 overflow data and fix up entries
        var ifd0Entries = new (long EntryPos, ExifEntry Entry, uint ValueOffset)[ifd0Count];
        for (int i = 0; i < ifd0Count; i++)
        {
            var entry = allIfd0[i];
            int typeSize = GetTypeSize(entry.DataType);
            uint totalSize = entry.Count * (uint)typeSize;

            uint valueOffset = 0;
            if (totalSize > 4)
            {
                valueOffset = (uint)ms.Position;
                ms.Write(entry.Value);
                // Pad to word boundary
                if (ms.Position % 2 != 0) ms.WriteByte(0);
            }

            ifd0Entries[i] = (ifd0EntriesStart + i * 12, entry, valueOffset);
        }

        // Write EXIF sub-IFD
        uint exifIfdOffset = 0;
        if (hasExif)
        {
            exifIfdOffset = (uint)ms.Position;
            WriteIfd(ms, profile.ExifTags, le);
        }

        // Write GPS sub-IFD
        uint gpsIfdOffset = 0;
        if (hasGps)
        {
            gpsIfdOffset = (uint)ms.Position;
            WriteIfd(ms, profile.GpsTags, le);
        }

        // Now go back and write the actual IFD0 entries
        byte[] result = ms.ToArray();
        for (int i = 0; i < ifd0Count; i++)
        {
            var (entryPos, entry, valueOffset) = ifd0Entries[i];
            int pos = (int)entryPos;

            // Fix up EXIF/GPS pointer entries
            var actualEntry = entry;
            byte[]? actualValue = entry.Value;

            if (entry.Tag == ExifTag.ExifIfdPointer && hasExif)
            {
                actualValue = new byte[4];
                if (le) BinaryPrimitives.WriteUInt32LittleEndian(actualValue, exifIfdOffset);
                else BinaryPrimitives.WriteUInt32BigEndian(actualValue, exifIfdOffset);
            }
            else if (entry.Tag == ExifTag.GpsIfdPointer && hasGps)
            {
                actualValue = new byte[4];
                if (le) BinaryPrimitives.WriteUInt32LittleEndian(actualValue, gpsIfdOffset);
                else BinaryPrimitives.WriteUInt32BigEndian(actualValue, gpsIfdOffset);
            }

            if (le)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos), actualEntry.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos + 2), (ushort)actualEntry.DataType);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 4), actualEntry.Count);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), actualEntry.Tag);
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos + 2), (ushort)actualEntry.DataType);
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 4), actualEntry.Count);
            }

            int typeSize = GetTypeSize(actualEntry.DataType);
            uint totalSize = actualEntry.Count * (uint)typeSize;
            if (totalSize <= 4)
            {
                // Value inline
                var valueSpan = result.AsSpan(pos + 8, 4);
                valueSpan.Clear();
                if (actualValue is not null)
                    actualValue.AsSpan(0, Math.Min(actualValue.Length, 4)).CopyTo(valueSpan);
            }
            else
            {
                // Value at offset
                if (le) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 8), valueOffset);
                else BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos + 8), valueOffset);
            }
        }

        return result;
    }

    private static void WriteIfd(MemoryStream ms, List<ExifEntry> entries, bool le)
    {
        WriteUInt16(ms, (ushort)entries.Count, le);

        long entriesStart = ms.Position;
        for (int i = 0; i < entries.Count; i++)
            ms.Write(new byte[12]); // placeholder
        WriteUInt32(ms, 0, le); // no next IFD

        // Write overflow data
        var entryInfos = new (long Pos, uint ValueOffset)[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int typeSize = GetTypeSize(entry.DataType);
            uint totalSize = entry.Count * (uint)typeSize;

            uint valueOffset = 0;
            if (totalSize > 4)
            {
                valueOffset = (uint)ms.Position;
                ms.Write(entry.Value);
                if (ms.Position % 2 != 0) ms.WriteByte(0);
            }
            entryInfos[i] = (entriesStart + i * 12, valueOffset);
        }

        // Fix up entries in-place
        byte[] data = ms.ToArray();
        // We need to write entries into the stream — but ms is still open.
        // Instead, seek back and overwrite.
        long endPos = ms.Position;
        Span<byte> buf = stackalloc byte[12];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ms.Position = entryInfos[i].Pos;
            if (le)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buf, entry.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(buf[2..], (ushort)entry.DataType);
                BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], entry.Count);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(buf, entry.Tag);
                BinaryPrimitives.WriteUInt16BigEndian(buf[2..], (ushort)entry.DataType);
                BinaryPrimitives.WriteUInt32BigEndian(buf[4..], entry.Count);
            }

            int typeSize = GetTypeSize(entry.DataType);
            uint totalSize = entry.Count * (uint)typeSize;
            if (totalSize <= 4)
            {
                buf[8..12].Clear();
                if (entry.Value is not null)
                    entry.Value.AsSpan(0, Math.Min(entry.Value.Length, 4)).CopyTo(buf[8..]);
            }
            else
            {
                if (le) BinaryPrimitives.WriteUInt32LittleEndian(buf[8..], entryInfos[i].ValueOffset);
                else BinaryPrimitives.WriteUInt32BigEndian(buf[8..], entryInfos[i].ValueOffset);
            }

            ms.Write(buf);
        }
        ms.Position = endPos;
    }

    private static ExifEntry CreatePointerEntry(ushort tag, uint offset, bool le)
    {
        var value = new byte[4];
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(value, offset);
        else BinaryPrimitives.WriteUInt32BigEndian(value, offset);
        return new ExifEntry { Tag = tag, DataType = ExifDataType.Long, Count = 1, Value = value };
    }

    private static void WriteUInt16(MemoryStream ms, ushort value, bool le)
    {
        Span<byte> buf = stackalloc byte[2];
        if (le) BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        else BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteUInt32(MemoryStream ms, uint value, bool le)
    {
        Span<byte> buf = stackalloc byte[4];
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        else BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
    }
}

/// <summary>
/// IPTC-IIM data parser. Reads 8BIM resources from JPEG APP13 segments.
/// </summary>
public static class IptcParser
{
    private static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();
    private static readonly byte[] Bim8 = "8BIM"u8.ToArray();

    /// <summary>
    /// Parses IPTC records from a JPEG APP13 segment payload.
    /// </summary>
    public static IptcProfile? ParseFromApp13(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return null;

        // Check for "Photoshop 3.0\0" header
        if (!data[..14].SequenceEqual(PhotoshopHeader))
            return null;

        var profile = new IptcProfile { RawData = data.ToArray() };
        int pos = 14;

        // Parse 8BIM resources looking for IPTC (resource ID 0x0404)
        while (pos + 12 <= data.Length)
        {
            if (!data[pos..(pos + 4)].SequenceEqual(Bim8))
                break;
            pos += 4;

            ushort resourceId = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;

            // Pascal string (name)
            byte nameLen = data[pos++];
            pos += nameLen;
            if ((nameLen + 1) % 2 != 0) pos++; // pad to even

            uint resSize = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            pos += 4;

            if (resourceId == 0x0404 && pos + resSize <= data.Length)
            {
                // This is the IPTC-NAA resource
                ParseIptcRecords(data.Slice(pos, (int)resSize), profile.Records);
            }

            pos += (int)resSize;
            if (pos % 2 != 0) pos++; // pad to even
        }

        return profile.Records.Count > 0 ? profile : null;
    }

    /// <summary>
    /// Parses raw IPTC-IIM records (without 8BIM wrapper).
    /// </summary>
    public static void ParseIptcRecords(ReadOnlySpan<byte> data, List<IptcRecord> records)
    {
        int pos = 0;
        while (pos + 5 <= data.Length)
        {
            if (data[pos] != 0x1C) break; // IPTC tag marker
            byte recordNumber = data[pos + 1];
            byte dataSet = data[pos + 2];
            ushort dataLen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
            pos += 5;

            if (pos + dataLen > data.Length) break;

            string value = Encoding.UTF8.GetString(data.Slice(pos, dataLen));
            records.Add(new IptcRecord
            {
                RecordNumber = recordNumber,
                DataSet = dataSet,
                Value = value
            });

            pos += dataLen;
        }
    }

    /// <summary>
    /// Serializes IPTC records into a JPEG APP13 segment payload.
    /// If the profile has RawData, returns that for round-trip.
    /// </summary>
    public static byte[] SerializeForApp13(IptcProfile profile)
    {
        if (profile.RawData is not null)
            return (byte[])profile.RawData.Clone();

        using var ms = new MemoryStream();

        // Photoshop 3.0 header
        ms.Write(PhotoshopHeader);

        // 8BIM resource for IPTC-NAA (0x0404)
        ms.Write(Bim8);
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0x0404);
        ms.Write(buf);
        ms.WriteByte(0); // empty pascal string
        ms.WriteByte(0); // pad

        // Build IPTC records
        using var iptcMs = new MemoryStream();
        foreach (var rec in profile.Records)
        {
            byte[] valBytes = Encoding.UTF8.GetBytes(rec.Value);
            iptcMs.WriteByte(0x1C);
            iptcMs.WriteByte(rec.RecordNumber);
            iptcMs.WriteByte(rec.DataSet);
            BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)valBytes.Length);
            iptcMs.Write(buf);
            iptcMs.Write(valBytes);
        }

        byte[] iptcData = iptcMs.ToArray();
        Span<byte> sizeBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBuf, (uint)iptcData.Length);
        ms.Write(sizeBuf);
        ms.Write(iptcData);

        if (ms.Position % 2 != 0) ms.WriteByte(0);

        return ms.ToArray();
    }
}
