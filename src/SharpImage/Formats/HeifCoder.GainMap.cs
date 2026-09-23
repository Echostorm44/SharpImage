// AVIF gain maps (ISO 21496-1, carried as in ISO/IEC 23008-12:2024/AMD 1): a 'tmap' derived image item whose dimg
// references are [base image, gain map image], preferred over the base in an 'altr' entity group, with the metadata as
// the tmap item's payload and the alternate image's colr/clli/pixi as tmap properties. Parsing and validation follow
// libavif's read.c (avifDecoderFindGainMapItem, avifParseToneMappedImageBox) so the same files are accepted, ignored or
// rejected.
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Metadata;
using System.Buffers.Binary;
using System.Text;

namespace SharpImage.Formats;

/// <summary>A signed fraction as stored in gain map metadata.</summary>
public readonly record struct GainMapFraction(int Numerator, uint Denominator)
{
    /// <summary>The value as float (0 when the denominator is 0), computed exactly as libavif does.</summary>
    public float ToSingle() => Denominator == 0 ? 0f : (float)Numerator / Denominator;
}

/// <summary>An unsigned fraction as stored in gain map metadata.</summary>
public readonly record struct GainMapUFraction(uint Numerator, uint Denominator)
{
    /// <summary>The value as float (0 when the denominator is 0), computed exactly as libavif does.</summary>
    public float ToSingle() => Denominator == 0 ? 0f : (float)Numerator / Denominator;
}

/// <summary>
/// An ISO 21496-1 gain map: per-channel metadata, the alternate image's colour description, and the gain map image.
/// </summary>
public sealed class AvifGainMap
{
    /// <summary>log2 of the minimum gain, per channel (R, G, B).</summary>
    public GainMapFraction[] Min { get; } = [new(1, 1), new(1, 1), new(1, 1)];
    /// <summary>log2 of the maximum gain, per channel.</summary>
    public GainMapFraction[] Max { get; } = [new(1, 1), new(1, 1), new(1, 1)];
    /// <summary>Encoding gamma of the gain map values, per channel.</summary>
    public GainMapUFraction[] Gamma { get; } = [new(1, 1), new(1, 1), new(1, 1)];
    /// <summary>Offset added to the base image's linear values, per channel.</summary>
    public GainMapFraction[] BaseOffset { get; } = [new(1, 64), new(1, 64), new(1, 64)];
    /// <summary>Offset added to the alternate image's linear values, per channel.</summary>
    public GainMapFraction[] AlternateOffset { get; } = [new(1, 64), new(1, 64), new(1, 64)];
    /// <summary>log2 HDR headroom the base image is meant for (0 = SDR).</summary>
    public GainMapUFraction BaseHdrHeadroom { get; set; } = new(0, 1);
    /// <summary>log2 HDR headroom of the alternate image.</summary>
    public GainMapUFraction AlternateHdrHeadroom { get; set; } = new(1, 1);
    /// <summary>True when the gain map math happens in the base image's colour space, else the alternate's.</summary>
    public bool UseBaseColorSpace { get; set; } = true;

    /// <summary>The alternate image's CICP (colr nclx on the tmap item), if any.</summary>
    public CicpInfo? AlternateCicp { get; set; }
    /// <summary>The alternate image's ICC profile (colr prof/rICC on the tmap item), if any.</summary>
    public byte[]? AlternateIccProfile { get; set; }
    /// <summary>The alternate image's content light level (clli on the tmap item), if any.</summary>
    public ContentLightLevel? AlternateContentLightLevel { get; set; }
    /// <summary>The alternate image's plane count (pixi on the tmap item), 0 if unknown.</summary>
    public int AlternatePlaneCount { get; set; }
    /// <summary>The alternate image's bit depth (pixi on the tmap item), 0 if unknown.</summary>
    public int AlternateDepth { get; set; }

    /// <summary>The gain map image (RGB; grey when single-channel). Null when only metadata was read.</summary>
    public ImageFrame? Image { get; set; }
    /// <summary>Bit depth the gain map image is coded at.</summary>
    public int ImageDepth { get; set; } = 8;

    /// <summary>True when the three channels have different metadata (written with is_multichannel = 1).</summary>
    public bool IsMultichannel =>
        Min[0] != Min[1] || Min[0] != Min[2] || Max[0] != Max[1] || Max[0] != Max[2] ||
        Gamma[0] != Gamma[1] || Gamma[0] != Gamma[2] || BaseOffset[0] != BaseOffset[1] || BaseOffset[0] != BaseOffset[2] ||
        AlternateOffset[0] != AlternateOffset[1] || AlternateOffset[0] != AlternateOffset[2];

    /// <summary>libavif avifGainMapValidateMetadata: null when valid, else the reason.</summary>
    public string? Validate()
    {
        for (int i = 0; i < 3; i++)
        {
            if (Min[i].Denominator == 0 || Max[i].Denominator == 0 || Gamma[i].Denominator == 0 ||
                BaseOffset[i].Denominator == 0 || AlternateOffset[i].Denominator == 0)
                return "Per-channel denominator is 0 in gain map metadata";
            if ((long)Max[i].Numerator * Min[i].Denominator < (long)Min[i].Numerator * Max[i].Denominator)
                return "Per-channel max is less than per-channel min in gain map metadata";
            if (Gamma[i].Numerator == 0) return "Per-channel gamma is 0 in gain map metadata";
        }
        if (BaseHdrHeadroom.Denominator == 0 || AlternateHdrHeadroom.Denominator == 0)
            return "Headroom denominator is 0 in gain map metadata";
        return null;
    }
}

public static partial class HeifCoder
{
    /// <summary>
    /// Reads the gain map of an AVIF still (metadata, alternate colour properties and the decoded gain map image), or
    /// null when the file has none — or has one libavif ignores (no 'tmap' brand, tmap not the preferred 'altr'
    /// alternative, unsupported metadata version). Throws for gain maps libavif rejects as invalid.
    /// </summary>
    public static AvifGainMap? DecodeGainMap(byte[] data) => FindGainMap(HeifContainer.Parse(data), decodeImage: true);

    // libavif avifDecoderFindGainMapItem. decodeImage=false reads and validates only (the check libavif makes on every
    // decode of a file with the 'tmap' brand).
    private static AvifGainMap? FindGainMap(HeifContainer c, bool decodeImage)
    {
        if (!c.Brands.Contains("tmap") || c.Primary is not { } primary) return null;
        byte[] d = c.Data;

        // avifDecoderDataFindToneMappedImageItem: the first tmap item whose dimg[0] is the colour item.
        int tmapId = -1, gainMapId = 0;
        foreach (var item in c.Items.Values.OrderBy(i => i.Id))
        {
            if (item.Type != "tmap" || c.ItemData(item.Id) is not { Length: > 0 }) continue;
            var dimg = c.ReferencesFrom(item.Id, "dimg");
            if (dimg.Count != 2 || dimg[0] == 0 || dimg[1] == 0 || dimg[0] == dimg[1])
                throw new InvalidDataException($"box[dimg] for 'tmap' item {item.Id} must have exactly 2 entries with distinct ids");
            if (dimg[0] != primary.Id) continue;
            tmapId = item.Id; gainMapId = dimg[1];
            break;
        }
        if (tmapId < 0 || !IsPreferredAlternative(c, (uint)tmapId, (uint)primary.Id)) return null;

        var gm = new AvifGainMap();
        if (!ParseToneMappedImageBox(c.ItemData(tmapId)!, gm)) return null;   // unsupported version: ignored

        if (!c.Items.ContainsKey(gainMapId)) throw new InvalidDataException($"Gain map item {gainMapId} does not exist.");

        // Alternate image colour properties from the tmap item.
        if (c.Property(tmapId, "colr", (o, l) => l > 4 && Encoding.ASCII.GetString(d, o, 4) is "prof" or "rICC") is { } prof)
            gm.AlternateIccProfile = d.AsSpan(prof.Off + 4, prof.Len - 4).ToArray();
        if (Nclx(c, tmapId) is { } alt) gm.AlternateCicp = new CicpInfo(alt.Cp, alt.Tc, alt.Mc, alt.Full);
        if (c.Property(tmapId, "clli") is { Len: >= 4 } clli)
            gm.AlternateContentLightLevel = new ContentLightLevel(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(clli.Off)),
                BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(clli.Off + 2)));
        if (c.Property(tmapId, "pixi") is { Len: >= 6 } pixi && d[pixi.Off + 4] > 0)
        {
            gm.AlternatePlaneCount = d[pixi.Off + 4];
            gm.AlternateDepth = d[pixi.Off + 5];
        }
        var ispe = c.Ispe(tmapId) ?? throw new InvalidDataException("Box[tmap] missing mandatory ispe property");
        if (ispe != (c.Ispe(primary.Id) ?? (-1, -1)))
            throw new InvalidDataException("Box[tmap] ispe property width/height does not match base image");
        foreach (var t in new[] { "pasp", "clap", "irot", "imir" })
            if (c.Property(tmapId, t) != null)
                throw new InvalidDataException("Box[tmap] 'pasp', 'clap', 'irot' and 'imir' properties must be associated with base and gain map items instead of 'tmap'");

        if (decodeImage)
        {
            var (tiles, rows, cols, outW, outH, codec) = ResolveImageTiles(c, gainMapId);
            gm.Image = DecodeImageTiles(c, tiles, rows, cols, outW, outH, codec, Nclx(c, gainMapId), premAlpha: null);
            gm.ImageDepth = ItemBitDepth(c, tiles[0]);
        }
        return gm;
    }

    // libavif avifIsPreferredAlternativeTo: id1 precedes id2 in an 'altr' group.
    private static bool IsPreferredAlternative(HeifContainer c, uint id1, uint id2)
    {
        foreach (var (type, _, ids) in c.Groups)
        {
            if (type != "altr") continue;
            bool id1Found = false;
            foreach (uint e in ids)
            {
                if (e == id1) id1Found = true;
                else if (e == id2) return id1Found;
            }
        }
        return false;
    }

    // libavif avifParseToneMappedImageBox + avifParseGainMapMetadata. False: unsupported version (gain map ignored).
    private static bool ParseToneMappedImageBox(byte[] p, AvifGainMap gm)
    {
        const string Invalid = "Invalid tone mapped image ('tmap') metadata.";
        if (p.Length < 1) throw new InvalidDataException(Invalid);
        if (p[0] != 0) return false;                                                   // version
        if (p.Length < 5) throw new InvalidDataException(Invalid);
        int minimumVersion = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1));
        if (minimumVersion > 0) return false;
        int writerVersion = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3));
        if (writerVersion < minimumVersion) throw new InvalidDataException(Invalid);

        int pos = 5;
        if (pos + 1 > p.Length) throw new InvalidDataException(Invalid);
        bool multichannel = (p[pos] & 0x80) != 0;
        gm.UseBaseColorSpace = (p[pos] & 0x40) != 0;
        pos++;
        uint U32()
        {
            if (pos + 4 > p.Length) throw new InvalidDataException(Invalid);
            uint v = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos));
            pos += 4;
            return v;
        }
        gm.BaseHdrHeadroom = new(U32(), U32());
        gm.AlternateHdrHeadroom = new(U32(), U32());
        int channels = multichannel ? 3 : 1;
        for (int ch = 0; ch < channels; ch++)
        {
            gm.Min[ch] = new((int)U32(), U32());
            gm.Max[ch] = new((int)U32(), U32());
            gm.Gamma[ch] = new(U32(), U32());
            gm.BaseOffset[ch] = new((int)U32(), U32());
            gm.AlternateOffset[ch] = new((int)U32(), U32());
        }
        for (int ch = channels; ch < 3; ch++)
        {
            gm.Min[ch] = gm.Min[0]; gm.Max[ch] = gm.Max[0]; gm.Gamma[ch] = gm.Gamma[0];
            gm.BaseOffset[ch] = gm.BaseOffset[0]; gm.AlternateOffset[ch] = gm.AlternateOffset[0];
        }
        if (writerVersion <= 0 && pos != p.Length) throw new InvalidDataException(Invalid);
        if (gm.Validate() != null) throw new InvalidDataException(Invalid);
        return true;
    }

    // Coded bit depth of an AV1 (av1C) or HEVC item; 8 when unknown.
    private static int ItemBitDepth(HeifContainer c, int id)
    {
        if (c.Property(id, "av1C") is { Len: >= 3 } a)
        {
            byte b = c.Data[a.Off + 2];
            return (b & 0x40) == 0 ? 8 : (b & 0x20) != 0 ? 12 : 10;
        }
        if (c.Property(id, "pixi") is { Len: >= 6 } px && c.Data[px.Off + 4] > 0) return c.Data[px.Off + 5];
        return 8;
    }
}
