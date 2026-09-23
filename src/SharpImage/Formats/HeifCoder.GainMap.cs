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

    /// <summary>The gain map image (RGB; grey when single-channel; <see cref="ImageFrame.Depth"/> = coded bit depth).
    /// Null when only metadata was read.</summary>
    public ImageFrame? Image { get; set; }

    // Decodes the coded gain map rescaled (in YUV, libyuv box filter) to a base image size, as libavif does before
    // applying it. Null for gain maps not read from a file (then the RGB image is rescaled instead).
    internal Func<int, int, ImageFrame>? ScaledImage { get; set; }

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
            // Gain map samples are used as values: keep them at libavif's native-depth RGB precision.
            var gmNclx = Nclx(c, gainMapId);
            ImageFrame Decode((int W, int H)? scaleTo)
            {
                bool prev = t_nativeDepthRgb;
                t_nativeDepthRgb = true;
                try { return DecodeImageTiles(c, tiles, rows, cols, outW, outH, codec, gmNclx, premAlpha: null, scaleTo); }
                finally { t_nativeDepthRgb = prev; }
            }
            gm.Image = Decode(null);
            gm.Image.Depth = ItemBitDepth(c, tiles[0]);
            gm.ScaledImage = (w, h) => Decode((w, h));
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

    /// <summary>
    /// Tone maps an AVIF with a gain map for a display with the given HDR headroom (log2 of peak / SDR white; 0 = SDR),
    /// with the defaults of libavif's <c>avifgainmaputil tonemap</c>: output primaries and transfer from the image being
    /// reproduced (base or alternate), else the gain map math colour space and PQ (headroom &gt; 0) or sRGB; depth from
    /// the image reproduced, else the largest involved. The result carries its CICP, depth and content light level.
    /// </summary>
    public static ImageFrame DecodeToneMapped(byte[] data, float hdrHeadroom, CicpInfo? output = null, int depth = 0)
    {
        // The base as libavif converts it for tone mapping: RGB at its coded depth.
        ImageFrame baseImage;
        bool prev = t_nativeDepthRgb;
        t_nativeDepthRgb = true;
        try { baseImage = Decode(data); }
        finally { t_nativeDepthRgb = prev; }
        var gm = DecodeGainMap(data) ?? throw new InvalidDataException("The image has no gain map.");
        var baseCicp = baseImage.Metadata.Cicp ?? CicpInfo.Srgb;
        float baseH = gm.BaseHdrHeadroom.ToSingle(), altH = gm.AlternateHdrHeadroom.ToSingle();
        bool toHdr = hdrHeadroom > 0.0f;
        bool toBase = (hdrHeadroom <= baseH && baseH <= altH) || (hdrHeadroom >= baseH && baseH >= altH);
        bool toAlt = (hdrHeadroom <= altH && altH <= baseH) || (hdrHeadroom >= altH && altH >= baseH);
        bool baseIsHdr = baseH != 0.0f;

        int cp, tc, mc;
        if (output != null) (cp, tc, mc) = (output.ColorPrimaries, output.TransferCharacteristics, output.MatrixCoefficients);
        else if (toBase || (toHdr && baseIsHdr)) (cp, tc, mc) = (baseCicp.ColorPrimaries, baseCicp.TransferCharacteristics, baseCicp.MatrixCoefficients);
        else (cp, tc, mc) = gm.AlternateCicp is { } a ? (a.ColorPrimaries, a.TransferCharacteristics, a.MatrixCoefficients) : (2, 2, 2);
        if (cp == 2) cp = gm.UseBaseColorSpace ? baseCicp.ColorPrimaries : gm.AlternateCicp?.ColorPrimaries ?? 2;
        if (tc == 2) tc = toHdr ? 16 : 13;

        if (depth == 0)
        {
            if (toBase) depth = baseImage.Depth;
            else if (toAlt) depth = gm.AlternateDepth;
            if (depth == 0) depth = Math.Max(Math.Max(baseImage.Depth, gm.Image!.Depth), gm.AlternateDepth);
        }

        ContentLightLevel? clli = toBase ? baseImage.Metadata.ContentLightLevel : toAlt ? gm.Image!.Metadata.ContentLightLevel : null;
        if (clli is { MaxContentLightLevel: 0, MaxFrameAverageLightLevel: 0 }) clli = null;
        var result = ApplyGainMap(baseImage, gm, hdrHeadroom, cp, tc, depth, out var computed);
        result.Metadata.Cicp = new CicpInfo(cp, tc, mc, true);
        result.Metadata.ContentLightLevel = clli ?? computed;
        return result;
    }

    /// <summary>
    /// libavif avifRGBImageApplyGainMap: applies <paramref name="gainMap"/> to <paramref name="baseImage"/> (its CICP from
    /// Metadata.Cicp, sRGB if absent; its sample precision from <see cref="ImageFrame.Depth"/>) for the given HDR headroom,
    /// producing RGB in the output primaries/transfer at <paramref name="outputDepth"/> bits (stored 16-bit). A gain map
    /// of another size is rescaled first. <paramref name="contentLightLevel"/> receives the result's max / average
    /// light level in nits (SDR white = 203), as libavif computes it.
    /// </summary>
    public static ImageFrame ApplyGainMap(ImageFrame baseImage, AvifGainMap gainMap, float hdrHeadroom, int outputColorPrimaries,
        int outputTransferCharacteristics, int outputDepth, out ContentLightLevel contentLightLevel)
    {
        if (hdrHeadroom < 0.0f) throw new ArgumentOutOfRangeException(nameof(hdrHeadroom), "hdrHeadroom should be >= 0");
        if (gainMap.Validate() is { } why) throw new ArgumentException(why, nameof(gainMap));
        if (baseImage.IccProfile is { Length: > 0 } || gainMap.AlternateIccProfile is { Length: > 0 })
            throw new NotSupportedException("Tone mapping for images with ICC profiles is not supported");
        if (outputDepth is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(outputDepth));
        if (gainMap.Image == null) throw new ArgumentException("The gain map has no image.", nameof(gainMap));

        int width = (int)baseImage.Columns, height = (int)baseImage.Rows;
        var baseCicp = baseImage.Metadata.Cicp ?? CicpInfo.Srgb;
        int baseCp = baseCicp.ColorPrimaries, baseTc = baseCicp.TransferCharacteristics;
        int altCp = gainMap.AlternateCicp?.ColorPrimaries ?? 2;
        int mathCp = gainMap.UseBaseColorSpace || altCp == 2 ? baseCp : altCp;
        bool needsInput = baseCp != mathCp, needsOutput = mathCp != outputColorPrimaries;

        var result = new ImageFrame();
        result.Initialize(width, height, ColorspaceType.SRGB, baseImage.HasAlpha);
        result.Depth = outputDepth;
        contentLightLevel = new ContentLightLevel(0, 0);
        int baseDepth = Math.Clamp(baseImage.Depth, 1, 16);
        float baseMax = (1 << baseDepth) - 1, outMax = (1 << outputDepth) - 1;
        int bch = baseImage.NumberOfChannels, och = result.NumberOfChannels;
        bool baseGray = bch - (baseImage.HasAlpha ? 1 : 0) < 3;

        float weight = GainMapWeight(hdrHeadroom, gainMap);
        if (weight == 0.0f && outputTransferCharacteristics == baseTc && outputColorPrimaries == baseCp && baseDepth == outputDepth)
        {
            for (int y = 0; y < height; y++)
            {
                var src = baseImage.GetPixelRow(y);
                var dst = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                    for (int k = 0; k < och; k++)
                        dst[x * och + k] = src[x * bch + (k < 3 ? (baseGray ? 0 : k) : bch - 1)];
            }
            return result;
        }

        var toLinear = AvifColorMath.ToLinear(baseTc);
        var toGamma = AvifColorMath.ToGamma(outputTransferCharacteristics);
        Span<float> px = stackalloc float[4];
        Span<float> tm = stackalloc float[4];

        if (weight == 0.0f)
        {
            bool primariesDiffer = baseCp != outputColorPrimaries;
            double[,]? m = primariesDiffer ? AvifColorMath.RgbToRgbMatrix(baseCp, outputColorPrimaries)
                ?? throw new NotSupportedException("Unsupported RGB color space conversion") : null;
            bool convert = outputTransferCharacteristics != baseTc || primariesDiffer;
            for (int y = 0; y < height; y++)
            {
                var src = baseImage.GetPixelRow(y);
                var dst = result.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    ReadPixel(src, x, bch, baseGray, baseImage.HasAlpha, baseDepth, baseMax, px);
                    if (convert)
                    {
                        for (int k = 0; k < 3; k++) px[k] = toLinear(px[k]);
                        if (m != null) AvifColorMath.Convert(px, m);
                        for (int k = 0; k < 3; k++) px[k] = NanSafeClamp(toGamma(px[k]));
                    }
                    WritePixel(dst, x, och, result.HasAlpha, outputDepth, outMax, px);
                }
            }
            return result;
        }

        double[,]? inM = needsInput ? AvifColorMath.RgbToRgbMatrix(baseCp, mathCp)
            ?? throw new NotSupportedException("Unsupported RGB color space conversion") : null;
        double[,]? outM = needsOutput ? AvifColorMath.RgbToRgbMatrix(mathCp, outputColorPrimaries)
            ?? throw new NotSupportedException("Unsupported RGB color space conversion") : null;

        var gmImage = gainMap.Image;
        int gmDepth = Math.Clamp(gmImage.Depth, 1, 16);
        bool scaled = gmImage.Columns != width || gmImage.Rows != height;
        if (scaled) gmImage = gainMap.ScaledImage?.Invoke(width, height) ?? ScaleRgb(gmImage, width, height, gmDepth);
        float gmMax = (1 << gmDepth) - 1;
        int gch = gmImage.NumberOfChannels;
        bool gmGray = gch - (gmImage.HasAlpha ? 1 : 0) < 3;

        Span<float> gammaInv = stackalloc float[3], gMin = stackalloc float[3], gMax = stackalloc float[3],
            bOff = stackalloc float[3], aOff = stackalloc float[3], gpx = stackalloc float[4];
        for (int k = 0; k < 3; k++)
        {
            gammaInv[k] = 1.0f / gainMap.Gamma[k].ToSingle();
            gMin[k] = gainMap.Min[k].ToSingle();
            gMax[k] = gainMap.Max[k].ToSingle();
            bOff[k] = gainMap.BaseOffset[k].ToSingle();
            aOff[k] = gainMap.AlternateOffset[k].ToSingle();
        }

        float rgbMaxLinear = 0, rgbSumLinear = 0;
        for (int y = 0; y < height; y++)
        {
            var src = baseImage.GetPixelRow(y);
            var gsrc = gmImage.GetPixelRow(y);
            var dst = result.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ReadPixel(src, x, bch, baseGray, baseImage.HasAlpha, baseDepth, baseMax, px);
                ReadPixel(gsrc, x, gch, gmGray, gmImage.HasAlpha, gmDepth, gmMax, gpx);
                float pixelRgbMaxLinear = 0.0f;
                for (int k = 0; k < 3; k++) px[k] = toLinear(px[k]);
                if (inM != null) AvifColorMath.Convert(px, inM);
                for (int k = 0; k < 3; k++)
                {
                    float gainMapLog2 = Lerp(gMin[k], gMax[k], MathF.Pow(gpx[k], gammaInv[k]));
                    float toneMappedLinear = (px[k] + bOff[k]) * MathF.Pow(2.0f, gainMapLog2 * weight) - aOff[k];
                    if (toneMappedLinear > rgbMaxLinear) rgbMaxLinear = toneMappedLinear;
                    if (toneMappedLinear > pixelRgbMaxLinear) pixelRgbMaxLinear = toneMappedLinear;
                    tm[k] = toneMappedLinear;
                }
                if (outM != null) AvifColorMath.Convert(tm, outM);
                for (int k = 0; k < 3; k++)
                {
                    if (float.IsNaN(tm[k]))
                        throw new InvalidDataException($"Degenerate gain map parameters produce NaN at pixel ({x}, {y})");
                    tm[k] = NanSafeClamp(toGamma(tm[k]));
                }
                tm[3] = px[3];
                rgbSumLinear += pixelRgbMaxLinear;
                WritePixel(dst, x, och, result.HasAlpha, outputDepth, outMax, tm);
            }
        }
        if (scaled) gmImage.Dispose();

        static ushort Nits(float v) => (ushort)Math.Clamp(MathF.Floor(v * AvifColorMath.SdrWhiteNits + 0.5f), 0.0f, 65535.0f);
        float rgbAverageLinear = rgbSumLinear / ((long)width * height);
        contentLightLevel = new ContentLightLevel(Nits(rgbMaxLinear), Nits(rgbAverageLinear));
        return result;
    }

    // A weight in [-1, 1]: how much of the gain map to apply for a display headroom (libavif avifGetGainMapWeight).
    private static float GainMapWeight(float hdrHeadroom, AvifGainMap gm)
    {
        float baseH = gm.BaseHdrHeadroom.ToSingle(), altH = gm.AlternateHdrHeadroom.ToSingle();
        if (baseH == altH) return 0.0f;
        float w = (hdrHeadroom - baseH) / (altH - baseH);
        w = w < 0.0f ? 0.0f : (1.0f < w ? 1.0f : w);
        return altH < baseH ? -w : w;
    }

    private static float Lerp(float a, float b, float w) => (1.0f - w) * a + w * b;

    // fminf(1, fmaxf(0, v)): NaN -> 0.
    private static float NanSafeClamp(float v) => float.IsNaN(v) ? 0.0f : MathF.Min(1.0f, MathF.Max(0.0f, v));

    // A 16-bit stored sample back to its native-depth value, normalised like libavif (value / (2^depth - 1)).
    private static float Sample(ushort u, int depth, float max) =>
        depth >= 16 ? u / 65535.0f : (int)(((uint)u * (uint)max + 32767u) / 65535u) / max;

    private static void ReadPixel(ReadOnlySpan<ushort> row, int x, int ch, bool gray, bool alpha, int depth, float max, Span<float> px)
    {
        int o = x * ch;
        for (int k = 0; k < 3; k++) px[k] = Sample(row[o + (gray ? 0 : k)], depth, max);
        px[3] = alpha ? Sample(row[o + ch - 1], depth, max) : 1.0f;
    }

    private static void WritePixel(Span<ushort> row, int x, int ch, bool alpha, int depth, float max, ReadOnlySpan<float> px)
    {
        int o = x * ch;
        for (int k = 0; k < (alpha ? 4 : 3); k++)
        {
            uint q = (uint)(0.5f + px[k] * max);
            row[o + (k < 3 ? k : ch - 1)] = depth >= 16 ? (ushort)q : (ushort)((q * 65535u + (uint)max / 2) / (uint)max);
        }
    }

    // A gain map not read from a file: rescale its RGB planes with the same libyuv box filter (at 8 or 12 bits).
    private static ImageFrame ScaleRgb(ImageFrame src, int w, int h, int depth)
    {
        int sw = (int)src.Columns, sh = (int)src.Rows, ch = src.NumberOfChannels;
        bool gray = ch - (src.HasAlpha ? 1 : 0) < 3;
        bool hbd = depth > 8;
        uint max = hbd ? 4095u : 255u;
        var dst = new ImageFrame();
        dst.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int k = 0; k < 3; k++)
        {
            var plane = new ushort[sw * sh];
            for (int y = 0; y < sh; y++)
            {
                var row = src.GetPixelRow(y);
                for (int x = 0; x < sw; x++) plane[y * sw + x] = (ushort)((row[x * ch + (gray ? 0 : k)] * max + 32767u) / 65535u);
            }
            var s = LibyuvScale.ScalePlane(plane, sw, sw, sh, w, h, hbd);
            for (int y = 0; y < h; y++)
            {
                var row = dst.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++) row[x * 3 + k] = (ushort)((s[y * w + x] * 65535u + max / 2) / max);
            }
        }
        return dst;
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
