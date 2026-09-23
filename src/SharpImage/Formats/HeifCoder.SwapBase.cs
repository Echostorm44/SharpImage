using System.Buffers;
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// <c>avifgainmaputil swapbase</c>: re-encodes an AVIF with a gain map so that its alternate rendition (the base
    /// with the gain map fully applied) becomes the base image and the old base the alternate — e.g. an SDR-base file
    /// becomes HDR-base. The new base takes the alternate's CICP (unspecified primaries / matrix: the base's; unspecified
    /// transfer: PQ, or sRGB when tone mapping to SDR), a content light level computed from its pixels when the file gives
    /// none, and <see cref="AvifEncodeOptions.BitDepth"/> (0: the alternate's depth, else the larger of base and gain map)
    /// and <see cref="AvifEncodeOptions.ChromaSubsampling"/> (Auto: 4:2:0 when the alternate is single-plane, else
    /// 4:4:4). The gain map's coded planes are kept; its metadata is swapped (headrooms, offsets, base colour space
    /// flag). As in libavif, files with ICC profiles are refused unless <paramref name="ignoreIccProfile"/> is set.
    /// </summary>
    public static byte[] SwapGainMapBase(byte[] avif, AvifEncodeOptions? options = null, bool ignoreIccProfile = false)
    {
        var c = HeifContainer.Parse(avif);
        var gm = DecodeGainMap(avif) ?? throw new ArgumentException("The image does not contain a gain map.", nameof(avif));
        var baseImage = DecodeNativeDepth(avif);
        if (ignoreIccProfile)
        {
            baseImage.IccProfile = null;
            baseImage.Metadata.IccProfile = null;
            gm.AlternateIccProfile = null;
        }
        var o = (options ?? new AvifEncodeOptions()).Clone();
        int depth = o.BitDepth != 0 ? o.BitDepth : gm.AlternateDepth != 0 ? gm.AlternateDepth : Math.Max(baseImage.Depth, gm.Image!.Depth);
        if (o.ChromaSubsampling == AvifChromaSubsampling.Auto)
            o.ChromaSubsampling = gm.AlternatePlaneCount == 1 ? AvifChromaSubsampling.Yuv420 : AvifChromaSubsampling.Yuv444;
        bool baseMono = c.Primary is { } p && c.Property(p.Id, "av1C") is { Len: >= 4 } av1c && (c.Data[av1c.Off + 2] & 0x10) != 0;
        return EncodeSwapped(baseImage, gm, depth, baseMono, o);
    }

    private static byte[] EncodeSwapped(ImageFrame baseImage, AvifGainMap gm, int depth, bool baseMono, AvifEncodeOptions o)
    {
        var (newBase, newGm) = ChangeGainMapBase(baseImage, gm, depth, baseMono);
        o.BitDepth = depth;
        o.GainMap = newGm;
        if (SourcePlanes.TryGetValue(newGm.Image!, out var gmSrc))
            o.GainMapChromaSubsampling = gmSrc.Layout switch
            {
                Av1.Av1PixelLayout.I420 => AvifChromaSubsampling.Yuv420,
                Av1.Av1PixelLayout.I422 => AvifChromaSubsampling.Yuv422,
                Av1.Av1PixelLayout.I400 => AvifChromaSubsampling.Yuv400,
                _ => AvifChromaSubsampling.Yuv444,
            };
        bool prev = t_rgbaSource;
        t_rgbaSource = true;   // ChangeBase converts an RGBA avifRGBImage
        try { return EncodeAvif(newBase, o); }
        finally { t_rgbaSource = prev; }
    }

    // avifgainmaputil ChangeBase: the alternate rendition as the new base, the old base described as the alternate.
    private static (ImageFrame Base, AvifGainMap GainMap) ChangeGainMapBase(ImageFrame baseImage, AvifGainMap gm, int depth, bool baseMono)
    {
        if (gm.Image == null) throw new ArgumentException("The gain map has no image.", nameof(gm));
        if (gm.AlternateHdrHeadroom.Denominator == 0) throw new ArgumentException("Invalid alternate headroom.", nameof(gm));
        float headroom = (float)gm.AlternateHdrHeadroom.Numerator / gm.AlternateHdrHeadroom.Denominator;
        bool toSdr = headroom == 0.0f;
        var baseCicp = baseImage.Metadata.Cicp ?? CicpInfo.Srgb;
        var alt = gm.AlternateCicp;
        int cp = alt?.ColorPrimaries is { } acp && acp != 2 ? acp : baseCicp.ColorPrimaries;
        int tc = alt?.TransferCharacteristics is { } atc && atc != 2 ? atc : toSdr ? 13 : 16;
        int mc = alt?.MatrixCoefficients is { } amc && amc != 2 ? amc : baseCicp.MatrixCoefficients;

        var clli = gm.AlternateContentLightLevel;
        bool computeClli = !toSdr && (clli == null || (clli.MaxContentLightLevel == 0 && clli.MaxFrameAverageLightLevel == 0));
        var rgb = ApplyGainMap(baseImage, gm, headroom, cp, tc, depth, out var computed);
        rgb.Metadata = baseImage.Metadata.Clone();
        rgb.IccProfile = baseImage.IccProfile;
        rgb.Orientation = baseImage.Orientation;
        rgb.Metadata.Cicp = new CicpInfo(cp, tc, mc, baseCicp.FullRange);
        var newClli = computeClli ? computed : clli;
        rgb.Metadata.ContentLightLevel = newClli is { MaxContentLightLevel: 0, MaxFrameAverageLightLevel: 0 } ? null : newClli;
        rgb.Depth = depth;

        var swapped = new AvifGainMap
        {
            Image = gm.Image,
            ScaledImage = gm.ScaledImage,
            UseBaseColorSpace = !gm.UseBaseColorSpace,
            BaseHdrHeadroom = gm.AlternateHdrHeadroom,
            AlternateHdrHeadroom = gm.BaseHdrHeadroom,
            AlternateIccProfile = baseImage.IccProfile ?? baseImage.Metadata.IccProfile?.Data,
            AlternateCicp = baseCicp,
            AlternateDepth = baseImage.Depth,
            AlternatePlaneCount = baseMono ? 1 : 3,
            AlternateContentLightLevel = baseImage.Metadata.ContentLightLevel,
        };
        for (int ch = 0; ch < 3; ch++)
        {
            swapped.Min[ch] = gm.Min[ch];
            swapped.Max[ch] = gm.Max[ch];
            swapped.Gamma[ch] = gm.Gamma[ch];
            swapped.BaseOffset[ch] = gm.AlternateOffset[ch];
            swapped.AlternateOffset[ch] = gm.BaseOffset[ch];
        }
        return (rgb, swapped);
    }

    // libavif avifImageYUVToRGB of 8-bit planes (e.g. a JPEG's raw components) into RGB at 8 bits, optionally after
    // rescaling the planes with libyuv (avifImageScale), as the gain map math sees a JPEG's gain map or base.
    private static ImageFrame RgbFromPlanes8(JpegRawYuv raw, CicpInfo cicp, (int W, int H)? scaleTo = null)
    {
        int w = raw.Width, h = raw.Height;
        bool mono = raw.Layout == Av1.Av1PixelLayout.I400;
        int ssX = raw.Layout == Av1.Av1PixelLayout.I444 ? 0 : 1, ssY = raw.Layout is Av1.Av1PixelLayout.I420 or Av1.Av1PixelLayout.I400 ? 1 : 0;
        var format = raw.Layout switch
        {
            Av1.Av1PixelLayout.I444 => Av1.PixelFormat.Yuv444P,
            Av1.Av1PixelLayout.I422 => Av1.PixelFormat.Yuv422P,
            _ => Av1.PixelFormat.Yuv420P,
        };
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        var buf = ArrayPool<byte>.Shared.Rent(w * h + 2 * cw * ch);   // returned by the frame's Dispose
        raw.Planes[0].CopyTo(buf, 0);
        if (mono) buf.AsSpan(w * h).Fill(128);
        else
        {
            raw.Planes[1].CopyTo(buf, w * h);
            raw.Planes[2].CopyTo(buf, w * h + cw * ch);
        }
        using var yuv = new Av1.DecodedVideoFrame(w, h, format, 0, buf, 0, w, w * h, cw, w * h + cw * ch, cw) { BitDepth = 8 };
        var frame = new ImageFrame();
        int ow = scaleTo?.W ?? w, oh = scaleTo?.H ?? h;
        frame.Initialize(ow, oh, ColorspaceType.SRGB, false);
        frame.Depth = 8;
        frame.Metadata.Cicp = cicp;
        if (ow != w || oh != h)
        {
            using var scaled = ScaleDecodedFrame(yuv, ow, oh);
            ConvertYuvToRgbLibavif(scaled, frame, ow, oh, frame.NumberOfChannels, mono, cicp.MatrixCoefficients, cicp.FullRange,
                cicp.ColorPrimaries, mono ? 1 : ssX, mono ? 1 : ssY);
        }
        else
            ConvertYuvToRgbLibavif(yuv, frame, w, h, frame.NumberOfChannels, mono, cicp.MatrixCoefficients, cicp.FullRange,
                cicp.ColorPrimaries, mono ? 1 : ssX, mono ? 1 : ssY);
        return frame;
    }

    // avifImageRGBToYUV then avifImageYUVToRGB at 8 bits: the round trip avifgainmaputil convert puts a JPEG's
    // libjpeg RGB through before applying the gain map.
    private static ImageFrame RoundTripYuv(ImageFrame rgb, CicpInfo cicp, Av1.Av1PixelLayout layout)
    {
        int w = (int)rgb.Columns, h = (int)rgb.Rows;
        // The JPEG is read with the matrix still unspecified: no libyuv route, libavif's float path with its default
        // (BT.601) coefficients; the planes are then labelled with the final matrix for the conversion back.
        var color = new Av1.Av1ObuWriter.Av1ColorDesc(cicp.ColorPrimaries, cicp.TransferCharacteristics, 2, cicp.FullRange);
        ReadRgbPlanes(rgb, 8, out var r, out var g, out var b, out _, out _, out _);
        RgbToYuvLibavif(rgb, 8, layout, color, 8, false, r, g, b, out var y, out var u, out var v);
        static byte[] Narrow(ushort[] p) => p.Select(s => (byte)s).ToArray();
        var raw = new JpegRawYuv { Planes = [Narrow(y), Narrow(u), Narrow(v)], Width = w, Height = h, Layout = layout };
        return RgbFromPlanes8(raw, cicp);
    }
}
