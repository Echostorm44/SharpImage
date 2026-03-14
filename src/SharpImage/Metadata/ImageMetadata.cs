using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpImage.Metadata;

/// <summary>
/// Container for all image metadata: EXIF tags, ICC color profile, XMP, IPTC.
/// Stored as raw parsed data to allow round-trip preservation.
/// </summary>
public sealed class ImageMetadata
{
    /// <summary>
    /// EXIF tags from IFD0 and sub-IFDs (EXIF IFD, GPS IFD).
    /// </summary>
    public ExifProfile? ExifProfile { get; set; }

    /// <summary>
    /// ICC color management profile (raw bytes).
    /// </summary>
    public IccProfile? IccProfile { get; set; }

    /// <summary>
    /// XMP metadata (XML string).
    /// </summary>
    public string? Xmp { get; set; }

    /// <summary>
    /// IPTC metadata records.
    /// </summary>
    public IptcProfile? IptcProfile { get; set; }

    /// <summary>
    /// Whether any metadata is present.
    /// </summary>
    public bool HasMetadata =>
        ExifProfile is not null || IccProfile is not null || Xmp is not null || IptcProfile is not null;

    /// <summary>
    /// Creates a deep copy of all metadata.
    /// </summary>
    public ImageMetadata Clone()
    {
        return new ImageMetadata
        {
            ExifProfile = ExifProfile?.Clone(),
            IccProfile = IccProfile?.Clone(),
            Xmp = Xmp,
            IptcProfile = IptcProfile?.Clone()
        };
    }
}

/// <summary>
/// EXIF tag data types per the TIFF/EXIF specification.
/// </summary>
public enum ExifDataType : ushort
{
    Byte = 1,
    Ascii = 2,
    Short = 3,
    Long = 4,
    Rational = 5,
    SignedByte = 6,
    Undefined = 7,
    SignedShort = 8,
    SignedLong = 9,
    SignedRational = 10,
    Float = 11,
    Double = 12
}

/// <summary>
/// Well-known EXIF tag IDs (IFD0 + EXIF sub-IFD + GPS sub-IFD).
/// </summary>
public static class ExifTag
{
    // IFD0 tags
    public const ushort ImageWidth = 0x0100;
    public const ushort ImageHeight = 0x0101;
    public const ushort BitsPerSample = 0x0102;
    public const ushort Compression = 0x0103;
    public const ushort PhotometricInterpretation = 0x0106;
    public const ushort ImageDescription = 0x010E;
    public const ushort Make = 0x010F;
    public const ushort Model = 0x0110;
    public const ushort Orientation = 0x0112;
    public const ushort SamplesPerPixel = 0x0115;
    public const ushort XResolution = 0x011A;
    public const ushort YResolution = 0x011B;
    public const ushort ResolutionUnit = 0x0128;
    public const ushort Software = 0x0131;
    public const ushort DateTime = 0x0132;
    public const ushort Artist = 0x013B;
    public const ushort Copyright = 0x8298;

    // Pointers to sub-IFDs
    public const ushort ExifIfdPointer = 0x8769;
    public const ushort GpsIfdPointer = 0x8825;

    // EXIF sub-IFD tags
    public const ushort ExposureTime = 0x829A;
    public const ushort FNumber = 0x829D;
    public const ushort ExposureProgram = 0x8822;
    public const ushort ISOSpeedRatings = 0x8827;
    public const ushort ExifVersion = 0x9000;
    public const ushort DateTimeOriginal = 0x9003;
    public const ushort DateTimeDigitized = 0x9004;
    public const ushort ShutterSpeedValue = 0x9201;
    public const ushort ApertureValue = 0x9202;
    public const ushort BrightnessValue = 0x9203;
    public const ushort ExposureBiasValue = 0x9204;
    public const ushort MaxApertureValue = 0x9205;
    public const ushort MeteringMode = 0x9207;
    public const ushort LightSource = 0x9208;
    public const ushort Flash = 0x9209;
    public const ushort FocalLength = 0x920A;
    public const ushort UserComment = 0x9286;
    public const ushort ColorSpace = 0xA001;
    public const ushort PixelXDimension = 0xA002;
    public const ushort PixelYDimension = 0xA003;
    public const ushort FocalLengthIn35mmFilm = 0xA405;
    public const ushort LensModel = 0xA434;
    public const ushort LensMake = 0xA433;

    // GPS sub-IFD tags
    public const ushort GpsVersionId = 0x0000;
    public const ushort GpsLatitudeRef = 0x0001;
    public const ushort GpsLatitude = 0x0002;
    public const ushort GpsLongitudeRef = 0x0003;
    public const ushort GpsLongitude = 0x0004;
    public const ushort GpsAltitudeRef = 0x0005;
    public const ushort GpsAltitude = 0x0006;
    public const ushort GpsTimestamp = 0x0007;
    public const ushort GpsDateStamp = 0x001D;

    /// <summary>
    /// Gets a human-readable name for a tag ID.
    /// </summary>
    public static string GetName(ushort tag) => tag switch
    {
        ImageWidth => "ImageWidth",
        ImageHeight => "ImageHeight",
        BitsPerSample => "BitsPerSample",
        Compression => "Compression",
        PhotometricInterpretation => "PhotometricInterpretation",
        ImageDescription => "ImageDescription",
        Make => "Make",
        Model => "Model",
        Orientation => "Orientation",
        SamplesPerPixel => "SamplesPerPixel",
        XResolution => "XResolution",
        YResolution => "YResolution",
        ResolutionUnit => "ResolutionUnit",
        Software => "Software",
        DateTime => "DateTime",
        Artist => "Artist",
        Copyright => "Copyright",
        ExifIfdPointer => "ExifIFD",
        GpsIfdPointer => "GpsIFD",
        ExposureTime => "ExposureTime",
        FNumber => "FNumber",
        ExposureProgram => "ExposureProgram",
        ISOSpeedRatings => "ISOSpeedRatings",
        ExifVersion => "ExifVersion",
        DateTimeOriginal => "DateTimeOriginal",
        DateTimeDigitized => "DateTimeDigitized",
        ShutterSpeedValue => "ShutterSpeedValue",
        ApertureValue => "ApertureValue",
        BrightnessValue => "BrightnessValue",
        ExposureBiasValue => "ExposureBiasValue",
        MaxApertureValue => "MaxApertureValue",
        MeteringMode => "MeteringMode",
        LightSource => "LightSource",
        Flash => "Flash",
        FocalLength => "FocalLength",
        UserComment => "UserComment",
        ColorSpace => "ColorSpace",
        PixelXDimension => "PixelXDimension",
        PixelYDimension => "PixelYDimension",
        FocalLengthIn35mmFilm => "FocalLengthIn35mmFilm",
        LensModel => "LensModel",
        LensMake => "LensMake",
        GpsVersionId => "GPSVersionID",
        GpsLatitudeRef => "GPSLatitudeRef",
        GpsLatitude => "GPSLatitude",
        GpsLongitudeRef => "GPSLongitudeRef",
        GpsLongitude => "GPSLongitude",
        GpsAltitudeRef => "GPSAltitudeRef",
        GpsAltitude => "GPSAltitude",
        GpsTimestamp => "GPSTimeStamp",
        GpsDateStamp => "GPSDateStamp",
        _ => $"Tag(0x{tag:X4})"
    };
}

/// <summary>
/// A single EXIF tag entry with its raw value data.
/// </summary>
public readonly struct ExifEntry
{
    public ushort Tag { get; init; }
    public ExifDataType DataType { get; init; }
    public uint Count { get; init; }
    public byte[] Value { get; init; }

    /// <summary>
    /// Gets the tag value as a string (for ASCII type).
    /// </summary>
    public string? GetString()
    {
        if (DataType != ExifDataType.Ascii || Value is null) return null;
        int len = Value.Length;
        if (len > 0 && Value[len - 1] == 0) len--;
        return Encoding.ASCII.GetString(Value, 0, len);
    }

    /// <summary>
    /// Gets the tag value as a ushort (for SHORT type).
    /// </summary>
    public ushort GetUInt16(bool littleEndian = true)
    {
        if (Value is null || Value.Length < 2) return 0;
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(Value)
            : BinaryPrimitives.ReadUInt16BigEndian(Value);
    }

    /// <summary>
    /// Gets the tag value as a uint (for LONG type).
    /// </summary>
    public uint GetUInt32(bool littleEndian = true)
    {
        if (Value is null || Value.Length < 4) return 0;
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(Value)
            : BinaryPrimitives.ReadUInt32BigEndian(Value);
    }

    /// <summary>
    /// Gets the tag value as a rational (numerator/denominator).
    /// </summary>
    public (uint Numerator, uint Denominator) GetRational(bool littleEndian = true)
    {
        if (Value is null || Value.Length < 8) return (0, 1);
        uint num = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(Value)
            : BinaryPrimitives.ReadUInt32BigEndian(Value);
        uint den = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(Value.AsSpan(4))
            : BinaryPrimitives.ReadUInt32BigEndian(Value.AsSpan(4));
        return (num, den);
    }

    /// <summary>
    /// Gets a human-readable string representation of the value.
    /// </summary>
    public string FormatValue(bool littleEndian = true)
    {
        if (Value is null) return "(null)";
        return DataType switch
        {
            ExifDataType.Ascii => GetString() ?? "",
            ExifDataType.Byte => Count == 1 ? Value[0].ToString() : string.Join(", ", Value),
            ExifDataType.Short => Count == 1
                ? GetUInt16(littleEndian).ToString()
                : FormatMultipleShorts(littleEndian),
            ExifDataType.Long => Count == 1
                ? GetUInt32(littleEndian).ToString()
                : FormatMultipleLongs(littleEndian),
            ExifDataType.Rational => FormatRationals(littleEndian),
            ExifDataType.SignedRational => FormatSignedRationals(littleEndian),
            ExifDataType.Undefined => Count <= 8
                ? string.Join(" ", Value.Select(b => $"0x{b:X2}"))
                : $"({Count} bytes)",
            _ => $"({Count} values, type {DataType})"
        };
    }

    private string FormatMultipleShorts(bool le)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < (int)Count && i * 2 + 1 < Value.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            ushort v = le
                ? BinaryPrimitives.ReadUInt16LittleEndian(Value.AsSpan(i * 2))
                : BinaryPrimitives.ReadUInt16BigEndian(Value.AsSpan(i * 2));
            sb.Append(v);
        }
        return sb.ToString();
    }

    private string FormatMultipleLongs(bool le)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < (int)Count && i * 4 + 3 < Value.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            uint v = le
                ? BinaryPrimitives.ReadUInt32LittleEndian(Value.AsSpan(i * 4))
                : BinaryPrimitives.ReadUInt32BigEndian(Value.AsSpan(i * 4));
            sb.Append(v);
        }
        return sb.ToString();
    }

    private string FormatRationals(bool le)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < (int)Count && i * 8 + 7 < Value.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            uint num = le
                ? BinaryPrimitives.ReadUInt32LittleEndian(Value.AsSpan(i * 8))
                : BinaryPrimitives.ReadUInt32BigEndian(Value.AsSpan(i * 8));
            uint den = le
                ? BinaryPrimitives.ReadUInt32LittleEndian(Value.AsSpan(i * 8 + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(Value.AsSpan(i * 8 + 4));
            sb.Append(den != 0
                ? ((double)num / den).ToString("F4", CultureInfo.InvariantCulture)
                : $"{num}/{den}");
        }
        return sb.ToString();
    }

    private string FormatSignedRationals(bool le)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < (int)Count && i * 8 + 7 < Value.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            int num = le
                ? BinaryPrimitives.ReadInt32LittleEndian(Value.AsSpan(i * 8))
                : BinaryPrimitives.ReadInt32BigEndian(Value.AsSpan(i * 8));
            int den = le
                ? BinaryPrimitives.ReadInt32LittleEndian(Value.AsSpan(i * 8 + 4))
                : BinaryPrimitives.ReadInt32BigEndian(Value.AsSpan(i * 8 + 4));
            sb.Append(den != 0
                ? ((double)num / den).ToString("F4", CultureInfo.InvariantCulture)
                : $"{num}/{den}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Parsed EXIF profile containing IFD0, EXIF sub-IFD, and GPS sub-IFD tags.
/// Also preserves the raw EXIF data for round-trip writing.
/// </summary>
public sealed class ExifProfile
{
    /// <summary>
    /// IFD0 (main image) tags.
    /// </summary>
    public List<ExifEntry> Ifd0Tags { get; } = [];

    /// <summary>
    /// EXIF sub-IFD tags.
    /// </summary>
    public List<ExifEntry> ExifTags { get; } = [];

    /// <summary>
    /// GPS sub-IFD tags.
    /// </summary>
    public List<ExifEntry> GpsTags { get; } = [];

    /// <summary>
    /// Raw EXIF APP1 segment data (for round-trip preservation). Excludes the "Exif\0\0" header.
    /// </summary>
    public byte[]? RawData { get; set; }

    /// <summary>
    /// True if byte order is little-endian (Intel "II"), false if big-endian (Motorola "MM").
    /// </summary>
    public bool IsLittleEndian { get; set; } = true;

    /// <summary>
    /// Gets a tag value from any IFD by tag ID. Searches IFD0 first, then EXIF, then GPS.
    /// </summary>
    public ExifEntry? GetTag(ushort tag)
    {
        foreach (var entry in Ifd0Tags)
            if (entry.Tag == tag) return entry;
        foreach (var entry in ExifTags)
            if (entry.Tag == tag) return entry;
        foreach (var entry in GpsTags)
            if (entry.Tag == tag) return entry;
        return null;
    }

    /// <summary>
    /// Sets or replaces a tag in IFD0.
    /// </summary>
    public void SetTag(ExifEntry entry)
    {
        for (int i = 0; i < Ifd0Tags.Count; i++)
        {
            if (Ifd0Tags[i].Tag == entry.Tag)
            {
                Ifd0Tags[i] = entry;
                return;
            }
        }
        Ifd0Tags.Add(entry);
    }

    /// <summary>
    /// Removes a tag from all IFDs.
    /// </summary>
    public bool RemoveTag(ushort tag)
    {
        bool removed = false;
        removed |= Ifd0Tags.RemoveAll(e => e.Tag == tag) > 0;
        removed |= ExifTags.RemoveAll(e => e.Tag == tag) > 0;
        removed |= GpsTags.RemoveAll(e => e.Tag == tag) > 0;
        return removed;
    }

    /// <summary>
    /// Gets all tags from all IFDs.
    /// </summary>
    public IEnumerable<(string Ifd, ExifEntry Entry)> GetAllTags()
    {
        foreach (var e in Ifd0Tags) yield return ("IFD0", e);
        foreach (var e in ExifTags) yield return ("EXIF", e);
        foreach (var e in GpsTags) yield return ("GPS", e);
    }

    public ExifProfile Clone()
    {
        var clone = new ExifProfile
        {
            IsLittleEndian = IsLittleEndian,
            RawData = RawData is not null ? (byte[])RawData.Clone() : null
        };
        clone.Ifd0Tags.AddRange(Ifd0Tags);
        clone.ExifTags.AddRange(ExifTags);
        clone.GpsTags.AddRange(GpsTags);
        return clone;
    }
}

/// <summary>
/// ICC color management profile (raw binary data with header parsing).
/// </summary>
public sealed class IccProfile
{
    /// <summary>
    /// Raw ICC profile data.
    /// </summary>
    public byte[] Data { get; }

    public IccProfile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    /// <summary>
    /// ICC profile size from header.
    /// </summary>
    public uint ProfileSize => Data.Length >= 4
        ? BinaryPrimitives.ReadUInt32BigEndian(Data)
        : (uint)Data.Length;

    /// <summary>
    /// Color space signature (e.g., "RGB ", "CMYK", "Gray").
    /// </summary>
    public string ColorSpace => Data.Length >= 20
        ? Encoding.ASCII.GetString(Data, 16, 4).Trim()
        : "Unknown";

    /// <summary>
    /// Profile description from the 'desc' tag, if present.
    /// </summary>
    public string? Description => FindDescriptionTag();

    private string? FindDescriptionTag()
    {
        if (Data.Length < 132) return null;

        uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(128));
        int offset = 132;

        for (uint i = 0; i < tagCount && offset + 12 <= Data.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset));
            uint tagOffset = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset + 4));
            uint tagSize = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset + 8));
            offset += 12;

            // 'desc' tag signature = 0x64657363
            if (sig == 0x64657363 && tagOffset + tagSize <= Data.Length)
            {
                uint typeSig = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)tagOffset));
                if (typeSig == 0x64657363) // 'desc' type
                {
                    uint strLen = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)tagOffset + 8));
                    if (strLen > 0 && tagOffset + 12 + strLen <= Data.Length)
                    {
                        int len = (int)strLen;
                        if (len > 0 && Data[(int)tagOffset + 12 + len - 1] == 0) len--;
                        return Encoding.ASCII.GetString(Data, (int)tagOffset + 12, len);
                    }
                }
                else if (typeSig == 0x6D6C7563) // 'mluc' type (v4 profiles)
                {
                    if (tagOffset + 28 <= Data.Length)
                    {
                        uint recCount = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)tagOffset + 8));
                        if (recCount > 0 && tagOffset + 28 <= Data.Length)
                        {
                            uint strLen2 = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)tagOffset + 20));
                            uint strOff = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)tagOffset + 24));
                            if (tagOffset + strOff + strLen2 <= Data.Length && strLen2 > 0)
                            {
                                return Encoding.BigEndianUnicode.GetString(Data, (int)(tagOffset + strOff), (int)strLen2).TrimEnd('\0');
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    public IccProfile Clone() => new((byte[])Data.Clone());
}

/// <summary>
/// IPTC-IIM (Information Interchange Model) metadata profile.
/// </summary>
public sealed class IptcProfile
{
    /// <summary>
    /// Parsed IPTC records.
    /// </summary>
    public List<IptcRecord> Records { get; } = [];

    /// <summary>
    /// Raw IPTC data for round-trip preservation.
    /// </summary>
    public byte[]? RawData { get; set; }

    /// <summary>
    /// Gets the first record with the specified dataset tag.
    /// </summary>
    public string? GetValue(byte dataSet)
    {
        foreach (var rec in Records)
            if (rec.DataSet == dataSet) return rec.Value;
        return null;
    }

    /// <summary>
    /// Sets or adds an IPTC record.
    /// </summary>
    public void SetValue(byte dataSet, string value)
    {
        for (int i = 0; i < Records.Count; i++)
        {
            if (Records[i].DataSet == dataSet)
            {
                Records[i] = new IptcRecord { RecordNumber = 2, DataSet = dataSet, Value = value };
                return;
            }
        }
        Records.Add(new IptcRecord { RecordNumber = 2, DataSet = dataSet, Value = value });
    }

    public IptcProfile Clone()
    {
        var clone = new IptcProfile { RawData = RawData is not null ? (byte[])RawData.Clone() : null };
        clone.Records.AddRange(Records);
        return clone;
    }
}

/// <summary>
/// A single IPTC-IIM record.
/// </summary>
public struct IptcRecord
{
    public byte RecordNumber { get; set; }
    public byte DataSet { get; set; }
    public string Value { get; set; }
}

/// <summary>
/// Well-known IPTC dataset tags (Record 2 — Application Record).
/// </summary>
public static class IptcDataSet
{
    public const byte ObjectName = 5;      // Title
    public const byte Urgency = 10;
    public const byte Category = 15;
    public const byte Keywords = 25;
    public const byte SpecialInstructions = 40;
    public const byte DateCreated = 55;
    public const byte TimeCreated = 60;
    public const byte Byline = 80;         // Author
    public const byte BylineTitle = 85;
    public const byte City = 90;
    public const byte SubLocation = 92;
    public const byte ProvinceState = 95;
    public const byte CountryCode = 100;
    public const byte CountryName = 101;
    public const byte Headline = 105;
    public const byte Credit = 110;
    public const byte Source = 115;
    public const byte CopyrightNotice = 116;
    public const byte Caption = 120;
    public const byte CaptionWriter = 122;

    public static string GetName(byte dataSet) => dataSet switch
    {
        ObjectName => "ObjectName",
        Urgency => "Urgency",
        Category => "Category",
        Keywords => "Keywords",
        SpecialInstructions => "SpecialInstructions",
        DateCreated => "DateCreated",
        TimeCreated => "TimeCreated",
        Byline => "Byline",
        BylineTitle => "BylineTitle",
        City => "City",
        SubLocation => "SubLocation",
        ProvinceState => "ProvinceState",
        CountryCode => "CountryCode",
        CountryName => "CountryName",
        Headline => "Headline",
        Credit => "Credit",
        Source => "Source",
        CopyrightNotice => "CopyrightNotice",
        Caption => "Caption",
        CaptionWriter => "CaptionWriter",
        _ => $"DataSet({dataSet})"
    };
}
