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
            // Keep the coded planes reachable so re-encoding the gain map (swapbase) copies them instead of
            // converting the RGB decode back to YUV.
            if (codec == "av01" && c.Property(tiles[0], "av1C") is { Len: >= 4 } av1c)
            {
                byte f = d[av1c.Off + 2];
                var layout = (f & 0x10) != 0 ? Av1.Av1PixelLayout.I400
                    : (f & 0x08) == 0 ? Av1.Av1PixelLayout.I444 : (f & 0x04) == 0 ? Av1.Av1PixelLayout.I422 : Av1.Av1PixelLayout.I420;
                var cicp = gm.Image.Metadata.Cicp;
                SourcePlanes.AddOrUpdate(gm.Image, new SourceYuv
                {
                    Planes = new Lazy<ushort[][]>(() => DecodeItemPlanes(c, gainMapId).Planes),
                    Width = (int)gm.Image.Columns, Height = (int)gm.Image.Rows, Depth = gm.Image.Depth, Layout = layout,
                    Matrices = [cicp?.MatrixCoefficients ?? 2], FullRange = cicp?.FullRange ?? true,
                });
            }
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
        var baseImage = DecodeNativeDepth(data);
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

    // Decode with high-bit-depth RGB quantised to the coded depth first, as libavif's avifImageYUVToRGB produces it for
    // an RGB image of the image's depth (what its gain map computation and tone mapping work on).
    internal static ImageFrame DecodeNativeDepth(byte[] data)
    {
        bool prev = t_nativeDepthRgb;
        t_nativeDepthRgb = true;
        try { return Decode(data); }
        finally { t_nativeDepthRgb = prev; }
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

    /// <summary>
    /// Computes the gain map that turns <paramref name="baseImage"/> into <paramref name="alternateImage"/> (libavif
    /// avifImageComputeGainMap, as <c>avifgainmaputil combine</c> runs it): per-pixel log2 ratios in the gain map math
    /// colour space (the wider of the two primaries), outlier-trimmed min/max per channel, headrooms from the images'
    /// peaks (capped at <paramref name="maxHeadroom"/>; 0 = no cap), the image quantised to <paramref name="depth"/>
    /// bits; <paramref name="singleChannel"/> computes one luma-weighted channel. Both images carry their CICP in
    /// Metadata.Cicp (sRGB if absent) and their sample precision in <see cref="ImageFrame.Depth"/>. The alternate
    /// image's CICP, depth, plane count and content light level are recorded. Encode it with
    /// <see cref="AvifEncodeOptions.GainMap"/>.
    /// </summary>
    public static AvifGainMap ComputeGainMap(ImageFrame baseImage, ImageFrame alternateImage, int depth = 8, bool singleChannel = false,
        double maxHeadroom = 4.0)
    {
        if (baseImage.IccProfile is { Length: > 0 } || alternateImage.IccProfile is { Length: > 0 })
            throw new NotSupportedException("Computing gain maps for images with ICC profiles is not supported");
        if (baseImage.Columns != alternateImage.Columns || baseImage.Rows != alternateImage.Rows)
            throw new ArgumentException($"Image dimensions don't match, got {baseImage.Columns}x{baseImage.Rows} and {alternateImage.Columns}x{alternateImage.Rows}");
        if (depth is not (8 or 10 or 12)) throw new ArgumentOutOfRangeException(nameof(depth), "Gain map depth must be 8, 10 or 12.");

        int width = (int)baseImage.Columns, height = (int)baseImage.Rows;
        var baseCicp = baseImage.Metadata.Cicp ?? CicpInfo.Srgb;
        var altCicp = alternateImage.Metadata.Cicp ?? CicpInfo.Srgb;
        int baseCp = baseCicp.ColorPrimaries, altCp = altCicp.ColorPrimaries;
        bool colorSpacesDiffer = baseCp != altCp;
        int mathCp = ChooseGainMapMathPrimaries(baseCp, altCp);

        var gm = new AvifGainMap { UseBaseColorSpace = mathCp == baseCp };   // encoding defaults (gamma 1, offsets 1/64)
        var baseToLinear = AvifColorMath.ToLinear(baseCicp.TransferCharacteristics);
        var altToLinear = AvifColorMath.ToLinear(altCicp.TransferCharacteristics);
        float[] yCoeffs = AvifColorMath.YCoeffs(mathCp);
        double[,]? conv = null;
        if (colorSpacesDiffer)
            conv = (gm.UseBaseColorSpace ? AvifColorMath.RgbToRgbMatrix(altCp, baseCp) : AvifColorMath.RgbToRgbMatrix(baseCp, altCp))
                ?? throw new NotSupportedException("Unsupported RGB color space conversion");

        int bDepth = Math.Clamp(baseImage.Depth, 1, 16), aDepth = Math.Clamp(alternateImage.Depth, 1, 16);
        float bMax = (1 << bDepth) - 1, aMax = (1 << aDepth) - 1;
        int bch = baseImage.NumberOfChannels, ach = alternateImage.NumberOfChannels;
        bool bGray = bch - (baseImage.HasAlpha ? 1 : 0) < 3, aGray = ach - (alternateImage.HasAlpha ? 1 : 0) < 3;
        Span<float> bpx = stackalloc float[4], apx = stackalloc float[4];

        Span<float> baseOffset = stackalloc float[3], altOffset = stackalloc float[3];
        for (int c = 0; c < 3; c++) { baseOffset[c] = gm.BaseOffset[c].ToSingle(); altOffset[c] = gm.AlternateOffset[c].ToSingle(); }

        // Converting between colour spaces can give negative values: raise the offset of the converted image to avoid
        // clamping (capped at 0.1, larger offsets cause artefacts when partially applying the gain map).
        const float kEpsilon = 1e-10f;
        if (colorSpacesDiffer)
        {
            Span<float> channelMin = stackalloc float[3];
            var src = gm.UseBaseColorSpace ? alternateImage : baseImage;
            var toLin = gm.UseBaseColorSpace ? altToLinear : baseToLinear;
            int sch = gm.UseBaseColorSpace ? ach : bch, sd = gm.UseBaseColorSpace ? aDepth : bDepth;
            bool sg = gm.UseBaseColorSpace ? aGray : bGray;
            float sm = gm.UseBaseColorSpace ? aMax : bMax;
            for (int j = 0; j < height; j++)
            {
                var row = src.GetPixelRow(j);
                for (int i = 0; i < width; i++)
                {
                    ReadPixel(row, i, sch, sg, src.HasAlpha, sd, sm, apx);
                    for (int c = 0; c < 3; c++) apx[c] = toLin(apx[c]);
                    AvifColorMath.Convert(apx, conv!);
                    for (int c = 0; c < 3; c++) channelMin[c] = MathF.Min(channelMin[c], apx[c]);
                }
            }
            for (int c = 0; c < 3; c++)
            {
                const float maxOffset = 0.1f;
                if (channelMin[c] < -kEpsilon)
                {
                    if (gm.UseBaseColorSpace) altOffset[c] = MathF.Min(altOffset[c] - channelMin[c], maxOffset);
                    else baseOffset[c] = MathF.Min(baseOffset[c] - channelMin[c], maxOffset);
                }
            }
        }

        int channels = singleChannel ? 1 : 3;
        int numPixels = width * height;
        var gmf = new float[channels][];
        for (int c = 0; c < channels; c++) gmf[c] = new float[numPixels];
        float baseMaxV = 1.0f, altMaxV = 1.0f;
        for (int j = 0; j < height; j++)
        {
            var brow = baseImage.GetPixelRow(j);
            var arow = alternateImage.GetPixelRow(j);
            for (int i = 0; i < width; i++)
            {
                ReadPixel(brow, i, bch, bGray, baseImage.HasAlpha, bDepth, bMax, bpx);
                ReadPixel(arow, i, ach, aGray, alternateImage.HasAlpha, aDepth, aMax, apx);
                for (int c = 0; c < 3; c++) { bpx[c] = baseToLinear(bpx[c]); apx[c] = altToLinear(apx[c]); }
                if (colorSpacesDiffer)
                {
                    if (gm.UseBaseColorSpace) AvifColorMath.Convert(apx, conv!);
                    else AvifColorMath.Convert(bpx, conv!);
                }
                for (int c = 0; c < channels; c++)
                {
                    float b = bpx[c], a = apx[c];
                    if (singleChannel)
                    {
                        b = yCoeffs[0] * bpx[0] + yCoeffs[1] * bpx[1] + yCoeffs[2] * bpx[2];
                        a = yCoeffs[0] * apx[0] + yCoeffs[1] * apx[1] + yCoeffs[2] * apx[2];
                    }
                    if (b > baseMaxV) baseMaxV = b;
                    if (a > altMaxV) altMaxV = a;
                    float ratio = (a + altOffset[c]) / (b + baseOffset[c]);
                    gmf[c][j * width + i] = MathF.Log2(MathF.Max(ratio, kEpsilon));
                }
            }
        }

        double baseHeadroom = MathF.Log2(MathF.Max(baseMaxV, kEpsilon));
        double altHeadroom = MathF.Log2(MathF.Max(altMaxV, kEpsilon));
        gm.BaseHdrHeadroom = ToUFraction(baseHeadroom);
        gm.AlternateHdrHeadroom = ToUFraction(altHeadroom);
        // Store the log-ratio of the HDR representation to the SDR one.
        if (altHeadroom < baseHeadroom)
            foreach (var plane in gmf)
                for (int k = 0; k < plane.Length; k++) plane[k] *= -1.0f;

        Span<float> minLog2 = stackalloc float[3], maxLog2 = stackalloc float[3];
        for (int c = 0; c < channels; c++) FindMinMaxWithoutOutliers(gmf[c], out minLog2[c], out maxLog2[c]);
        for (int c = 0; c < 3; c++)
        {
            gm.Min[c] = ToSFraction(minLog2[singleChannel ? 0 : c]);
            gm.Max[c] = ToSFraction(maxLog2[singleChannel ? 0 : c]);
            gm.AlternateOffset[c] = ToSFraction(altOffset[c]);
            gm.BaseOffset[c] = ToSFraction(baseOffset[c]);
        }

        // Remap [min, max] to [0, 1] (with the encoding gamma).
        for (int c = 0; c < channels; c++)
        {
            float range = MathF.Max(maxLog2[c] - minLog2[c], 0.0f);
            var plane = gmf[c];
            if (range == 0.0f) { Array.Clear(plane); continue; }
            float gamma = gm.Gamma[c].ToSingle();
            for (int k = 0; k < plane.Length; k++)
            {
                float v = plane[k];
                v = v < minLog2[c] ? minLog2[c] : (maxLog2[c] < v ? maxLog2[c] : v);
                v = MathF.Pow((v - minLog2[c]) / range, gamma);
                plane[k] = NanSafeClamp(v);
            }
        }

        // The gain map image at the requested depth (avifSetRGBAPixel quantisation), grey for a single channel.
        var img = new ImageFrame();
        img.Initialize(width, height, ColorspaceType.SRGB, false);
        img.Depth = depth;
        float gMaxQ = (1 << depth) - 1;
        Span<float> q = stackalloc float[4];
        for (int j = 0; j < height; j++)
        {
            var row = img.GetPixelRowForWrite(j);
            for (int i = 0; i < width; i++)
            {
                int o = j * width + i;
                q[0] = gmf[0][o];
                q[1] = singleChannel ? q[0] : gmf[1][o];
                q[2] = singleChannel ? q[0] : gmf[2][o];
                WritePixel(row, i, 3, false, depth, gMaxQ, q);
            }
        }
        img.Metadata.Cicp = new CicpInfo(2, 2, 2, true);
        gm.Image = img;

        gm.AlternateCicp = altCicp with { FullRange = true };   // libavif leaves altYUVRange at its default (full)
        gm.AlternateDepth = aDepth;
        gm.AlternatePlaneCount = aGray ? 1 : 3;
        gm.AlternateContentLightLevel = alternateImage.Metadata.ContentLightLevel;

        // avifgainmaputil combine --max-headroom (default 4): cap headrooms computed from the content.
        if (maxHeadroom > 0)
        {
            if (maxHeadroom * gm.BaseHdrHeadroom.Denominator < gm.BaseHdrHeadroom.Numerator) gm.BaseHdrHeadroom = ToUFraction(maxHeadroom);
            if (maxHeadroom * gm.AlternateHdrHeadroom.Denominator < gm.AlternateHdrHeadroom.Numerator) gm.AlternateHdrHeadroom = ToUFraction(maxHeadroom);
        }
        return gm;
    }

    // Codes the gain map image through the regular AV1 path (RGB -> YUV at full size with the gain map's own CICP —
    // libavif leaves it unspecified, 2/2/2, i.e. BT.601 coefficients — then the optional YUV downscale) and collects
    // the item data and properties the container needs, plus the tmap payload and the alternate image's properties.
    private static Av1.AvifGainMapItem BuildGainMapItem(AvifGainMap gm, AvifEncodeOptions o, Av1.AvifContainerExtras extras)
    {
        if (gm.Validate() is { } why) throw new ArgumentException(why, nameof(o));
        var img = gm.Image ?? throw new ArgumentException("The gain map has no image.", nameof(o));
        if (o.GainMapDownscaling < 1) throw new ArgumentOutOfRangeException(nameof(o), "GainMapDownscaling must be at least 1.");
        int bd = img.Depth is 8 or 10 or 12 ? img.Depth : 8;
        var cicp = img.Metadata.Cicp ?? new CicpInfo(2, 2, 2, true);
        var color = new Av1.Av1ObuWriter.Av1ColorDesc(cicp.ColorPrimaries, cicp.TransferCharacteristics, cicp.MatrixCoefficients, cicp.FullRange);
        var layout = o.GainMapChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            _ => Av1.Av1PixelLayout.I444,
        };
        int ds = o.GainMapDownscaling, rounding = ds / 2;
        int gw = Math.Max(((int)img.Columns + rounding) / ds, 1), gh = Math.Max(((int)img.Rows + rounding) / ds, 1);
        // Gain map quality: QualityGainMap, else GainMapLossless / GainMapQp, else Quality (100 = lossless).
        int? gq = o.QualityGainMap ?? (o.GainMapLossless || o.GainMapQp != null ? null : o.Quality);
        bool gmLossless = gq is { } g ? g >= 100 : o.GainMapLossless;
        int? gmQIdx = gq is { } g2 && g2 < 100 ? QualityToQIndex(g2, color.Matrix == 0) : null;
        byte[] file = EncodeAvifGeneral(img, o.GainMapQp ?? o.Qp ?? 20, bd, layout, color, new Av1.AvifContainerExtras(), gmLossless,
            scaleYuvTo: (gw, gh), libavifFloatYuv: true, qIdxOverride: gmQIdx);

        // Lift the coded item and its properties out of the single-item file.
        var c = HeifContainer.Parse(file);
        byte[] d = c.Data;
        byte[] RawBox(int id, string type, Func<int, int, bool>? match = null)
        {
            var p = c.Property(id, type, match) ?? throw new InvalidOperationException($"Encoded gain map lacks '{type}'.");
            return d.AsSpan(p.Off - 8, p.Len + 8).ToArray();
        }
        int pid = c.PrimaryId;
        var item = new Av1.AvifGainMapItem
        {
            Data = c.ItemData(pid)!,
            Width = gw,
            Height = gh,
            PixiBox = RawBox(pid, "pixi"),
            Av1CBox = RawBox(pid, "av1C"),
            ColrBox = RawBox(pid, "colr", (off, len) => len >= 11 && Encoding.ASCII.GetString(d, off, 4) == "nclx"),
            Tmap = ToneMapImagePayload(gm),
        };

        // Alternate image properties on the tmap item (libavif: pixi only when known, colr prof + nclx, clli).
        if (gm.AlternateDepth != 0 || gm.AlternatePlaneCount != 0)
        {
            int ch = gm.AlternatePlaneCount == 1 ? 1 : 3;
            var pixi = new byte[1 + ch];
            pixi[0] = (byte)ch;
            for (int i = 1; i <= ch; i++) pixi[i] = (byte)gm.AlternateDepth;
            item.AltPixiBox = Av1.Av1AvifWriter.FullBox("pixi", 0, 0, pixi);
        }
        if (gm.AlternateIccProfile is { Length: > 0 } icc)
            item.AltIccBox = Av1.Av1AvifWriter.Box("colr", [.. "prof"u8, .. icc]);
        var alt = gm.AlternateCicp ?? new CicpInfo(2, 2, 2, true);
        var nclx = new byte[11];
        "nclx"u8.CopyTo(nclx);
        BinaryPrimitives.WriteUInt16BigEndian(nclx.AsSpan(4), (ushort)alt.ColorPrimaries);
        BinaryPrimitives.WriteUInt16BigEndian(nclx.AsSpan(6), (ushort)alt.TransferCharacteristics);
        BinaryPrimitives.WriteUInt16BigEndian(nclx.AsSpan(8), (ushort)alt.MatrixCoefficients);
        nclx[10] = (byte)(alt.FullRange ? 0x80 : 0);
        item.AltNclxBox = Av1.Av1AvifWriter.Box("colr", nclx);
        if (gm.AlternateContentLightLevel is { } cl && (cl.MaxContentLightLevel != 0 || cl.MaxFrameAverageLightLevel != 0))
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(b, cl.MaxContentLightLevel);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), cl.MaxFrameAverageLightLevel);
            item.AltClliBox = Av1.Av1AvifWriter.Box("clli", b);
        }
        return item;
    }

    // The tmap item payload (libavif avifWriteToneMappedImagePayload): version 0, then ISO 21496-1 GainMapMetadata with
    // minimum_version = writer_version = 0; one channel when all three carry the same metadata.
    private static byte[] ToneMapImagePayload(AvifGainMap gm)
    {
        bool multi = gm.IsMultichannel;
        int channels = multi ? 3 : 1;
        var p = new byte[1 + 4 + 1 + 16 + channels * 40];
        int pos = 5;   // version (0), minimum_version (0), writer_version (0)
        p[pos++] = (byte)((multi ? 0x80 : 0) | (gm.UseBaseColorSpace ? 0x40 : 0));
        void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(pos), v); pos += 4; }
        U32(gm.BaseHdrHeadroom.Numerator); U32(gm.BaseHdrHeadroom.Denominator);
        U32(gm.AlternateHdrHeadroom.Numerator); U32(gm.AlternateHdrHeadroom.Denominator);
        for (int c = 0; c < channels; c++)
        {
            U32((uint)gm.Min[c].Numerator); U32(gm.Min[c].Denominator);
            U32((uint)gm.Max[c].Numerator); U32(gm.Max[c].Denominator);
            U32(gm.Gamma[c].Numerator); U32(gm.Gamma[c].Denominator);
            U32((uint)gm.BaseOffset[c].Numerator); U32(gm.BaseOffset[c].Denominator);
            U32((uint)gm.AlternateOffset[c].Numerator); U32(gm.AlternateOffset[c].Denominator);
        }
        return p;
    }

    // avifChooseColorSpaceForGainMapMath: the primaries whose gamut holds the other's pure R, G and B (largest min).
    private static int ChooseGainMapMathPrimaries(int baseCp, int altCp)
    {
        if (baseCp == altCp) return baseCp;
        var baseToAlt = AvifColorMath.RgbToRgbMatrix(baseCp, altCp);
        var altToBase = AvifColorMath.RgbToRgbMatrix(altCp, baseCp);
        if (baseToAlt == null || altToBase == null) throw new NotSupportedException("Unsupported RGB color space conversion");
        Span<float> rgba = stackalloc float[4];
        float baseMin = 0, altMin = 0;
        for (int c = 0; c < 3; c++)
        {
            rgba.Clear(); rgba[c] = 1.0f;
            AvifColorMath.Convert(rgba, altToBase);
            for (int i = 0; i < 3; i++) baseMin = MathF.Min(baseMin, rgba[i]);
            rgba.Clear(); rgba[c] = 1.0f;
            AvifColorMath.Convert(rgba, baseToAlt);
            for (int i = 0; i < 3; i++) altMin = MathF.Min(altMin, rgba[i]);
        }
        return altMin <= baseMin ? baseCp : altCp;
    }

    // avifFindMinMaxWithoutOutliers: min / max ignoring up to 0.1% outliers (0.01-wide histogram buckets).
    private static void FindMinMaxWithoutOutliers(float[] v, out float rangeMin, out float rangeMax)
    {
        const float bucketSize = 0.01f, maxOutliersRatio = 0.001f;
        int maxOutliersOnEachSide = (int)MathF.Floor(v.Length * maxOutliersRatio / 2.0f + 0.5f);
        float min = v[0], max = v[0];
        for (int i = 1; i < v.Length; i++) { min = MathF.Min(min, v[i]); max = MathF.Max(max, v[i]); }
        rangeMin = min;
        rangeMax = max;
        if ((max - min) <= (bucketSize * 2) || maxOutliersOnEachSide == 0) return;

        int numBuckets = Math.Min((int)MathF.Ceiling((max - min) / bucketSize), 10000);
        var histogram = new int[numBuckets];
        foreach (float x in v)
        {
            float c = x < min ? min : (max < x ? max : x);
            histogram[Math.Min((int)MathF.Floor((c - min) / (max - min) * numBuckets + 0.5f), numBuckets - 1)]++;
        }
        float BucketValue(int idx) => idx * (max - min) / numBuckets + min;
        int left = 0;
        for (int i = 0; i < numBuckets; i++)
        {
            left += histogram[i];
            if (left > maxOutliersOnEachSide) break;
            if (histogram[i] == 0) rangeMin = BucketValue(i + 1);
        }
        int right = 0;
        for (int i = numBuckets - 1; i >= 0; i--)
        {
            right += histogram[i];
            if (right > maxOutliersOnEachSide) break;
            if (histogram[i] == 0) rangeMax = BucketValue(i);
        }
    }

    // avifDoubleToUnsignedFractionImpl: best continued-fraction approximation with numerator <= maxNumerator.
    private static (uint N, uint D) DoubleToFraction(double v, uint maxNumerator)
    {
        if (double.IsNaN(v) || v < 0 || v > maxNumerator) throw new ArgumentException($"{v} cannot be expressed as a fraction.");
        uint maxD = v <= 1 ? uint.MaxValue : (uint)Math.Floor(maxNumerator / v);
        uint d = 1, previousD = 0, n;
        double currentV = v - Math.Floor(v);
        for (int iter = 0; iter < 39; iter++)
        {
            double numeratorDouble = (double)d * v;
            n = (uint)Math.Round(numeratorDouble, MidpointRounding.AwayFromZero);
            if (Math.Abs(numeratorDouble - n) == 0.0) return (n, d);
            currentV = 1.0 / currentV;
            double newD = previousD + Math.Floor(currentV) * d;
            if (newD > maxD) return (n, d);
            previousD = d;
            d = (uint)newD;
            currentV -= Math.Floor(currentV);
        }
        return ((uint)Math.Round((double)d * v, MidpointRounding.AwayFromZero), d);
    }

    internal static GainMapUFraction ToUFraction(double v)
    {
        var (n, d) = DoubleToFraction(v, uint.MaxValue);
        return new GainMapUFraction(n, d);
    }

    internal static GainMapFraction ToSFraction(double v)
    {
        var (n, d) = DoubleToFraction(Math.Abs(v), int.MaxValue);
        return new GainMapFraction(v < 0 ? -(int)n : (int)n, d);
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
