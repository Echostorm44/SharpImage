using System.Buffers;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// Decodes an AVIF's primary image to its coded Y'CbCr planes (avifdec -o out.y4m): native depth and layout, no RGB
    /// conversion, grids stitched, alpha from the alpha item when present. Sequences give their primary item (use
    /// <see cref="DecodeSequence(byte[])"/> for frames).
    /// </summary>
    public static YuvImage DecodeYuv(byte[] data, AvifDecodeOptions? options = null) => WithLimits(options, () =>
    {
        var c = HeifContainer.Parse(data);
        int pid = c.PrimaryId;
        if (c.Primary?.Type is not ("av01" or "grid")) throw new NotSupportedException("DecodeYuv reads AV1 (AVIF) images.");
        var p = DecodeItemPlanes(c, pid);
        var nclx = Nclx(c, pid);
        int aid = c.ReferencesTo(pid, "auxl").Where(a => IsAlphaAux(c, a)).DefaultIfEmpty(-1).First();
        ushort[]? alpha = aid >= 0 ? DecodeItemPlanes(c, aid).Planes[0] : null;
        var (tiles, _, cols, _, _, _) = ResolveImageTiles(c, pid);
        if (alpha == null && tiles.Count > 1)
        {
            // libavif color_grid_alpha_nogrid: each colour tile carries its own alpha item — stitched like the tiles.
            var perTile = tiles.Select(t => c.ReferencesTo(t, "auxl").Where(a => IsAlphaAux(c, a)).DefaultIfEmpty(-1).First()).ToList();
            if (!perTile.Contains(-1))
            {
                alpha = new ushort[p.Width * p.Height];
                for (int i = 0; i < perTile.Count; i++)
                {
                    var ap = DecodeItemPlanes(c, perTile[i]);
                    int x0 = (i % cols) * ap.Width, y0 = (i / cols) * ap.Height;
                    for (int y = 0; y < ap.Height && y0 + y < p.Height; y++)
                        for (int x = 0; x < ap.Width && x0 + x < p.Width; x++)
                            alpha[(y0 + y) * p.Width + x0 + x] = ap.Planes[0][y * ap.Width + x];
                }
            }
        }
        // Chroma sample position from the sequence header, as libavif reports it with dav1d (its aom decoder path
        // leaves it unknown).
        int csp = p.ChromaSamplePosition;
        var sub = p.Mono ? AvifChromaSubsampling.Yuv400 : p.Format switch
        {
            Av1.PixelFormat.Yuv444P or Av1.PixelFormat.Yuv444P10 or Av1.PixelFormat.Yuv444P12 => AvifChromaSubsampling.Yuv444,
            Av1.PixelFormat.Yuv422P or Av1.PixelFormat.Yuv422P10 or Av1.PixelFormat.Yuv422P12 => AvifChromaSubsampling.Yuv422,
            _ => AvifChromaSubsampling.Yuv420,
        };
        return new YuvImage
        {
            Width = p.Width, Height = p.Height, Depth = p.Depth, Subsampling = sub, FullRange = nclx?.Full ?? p.FullRange,
            ChromaSamplePosition = csp,
            Y = p.Planes[0], U = p.Mono ? null : p.Planes[1], V = p.Mono ? null : p.Planes[2], Alpha = alpha,
        };
    });

    /// <summary>RGB (BT.709 primaries, sRGB transfer, BT.601 matrix, the planes' range — what avifenc signals for Y4M) of a
    /// Y'CbCr picture, with the planes kept for AVIF encoding.</summary>
    internal static ImageFrame FrameFromYuv(YuvImage yuv)
    {
        int w = yuv.Width, h = yuv.Height, depth = yuv.Depth;
        if (depth is not (8 or 10 or 12)) throw new NotSupportedException($"{depth}-bit Y'CbCr is not supported for RGB conversion.");
        bool mono = yuv.Subsampling == AvifChromaSubsampling.Yuv400;
        int ssX = yuv.Subsampling is AvifChromaSubsampling.Yuv420 or AvifChromaSubsampling.Yuv422 or AvifChromaSubsampling.Yuv400 ? 1 : 0;
        int ssY = yuv.Subsampling is AvifChromaSubsampling.Yuv420 or AvifChromaSubsampling.Yuv400 ? 1 : 0;
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        var format = depth switch
        {
            8 => ssX == 0 ? Av1.PixelFormat.Yuv444P : ssY == 0 ? Av1.PixelFormat.Yuv422P : Av1.PixelFormat.Yuv420P,
            10 => ssX == 0 ? Av1.PixelFormat.Yuv444P10 : ssY == 0 ? Av1.PixelFormat.Yuv422P10 : Av1.PixelFormat.Yuv420P10,
            _ => ssX == 0 ? Av1.PixelFormat.Yuv444P12 : ssY == 0 ? Av1.PixelFormat.Yuv422P12 : Av1.PixelFormat.Yuv420P12,
        };
        ushort[] u = mono ? new ushort[cw * ch] : yuv.U!, v = mono ? new ushort[cw * ch] : yuv.V!;
        if (mono) { Array.Fill(u, (ushort)(1 << (depth - 1))); Array.Fill(v, (ushort)(1 << (depth - 1))); }
        byte[] buf = ArrayPool<byte>.Shared.Rent(w * h + 2 * cw * ch);
        for (int i = 0; i < w * h; i++) buf[i] = (byte)(yuv.Y[i] >> (depth - 8));
        for (int i = 0; i < cw * ch; i++) { buf[w * h + i] = (byte)(u[i] >> (depth - 8)); buf[w * h + cw * ch + i] = (byte)(v[i] >> (depth - 8)); }
        using var frameYuv = new Av1.DecodedVideoFrame(w, h, format, 0, buf, 0, w, w * h, cw, w * h + cw * ch, cw)
        {
            BitDepth = depth, YPlane16 = yuv.Y, UPlane16 = u, VPlane16 = v,
        };
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, yuv.Alpha != null);
        frame.Depth = depth;
        frame.Metadata.Cicp = new SharpImage.Metadata.CicpInfo(1, 13, 6, yuv.FullRange);
        ConvertYuvToRgbLibavif(frameYuv, frame, w, h, frame.NumberOfChannels, mono, 6, yuv.FullRange, 1, ssX, ssY);
        if (yuv.Alpha != null)
        {
            int nch = frame.NumberOfChannels;
            float max = (1 << depth) - 1;
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                    row[x * nch + nch - 1] = (ushort)Math.Clamp((int)(0.5f + yuv.Alpha[y * w + x] / max * 65535f), 0, 65535);
            }
        }
        var planes = mono ? new[] { yuv.Y } : new[] { yuv.Y, yuv.U!, yuv.V! };
        SourcePlanes.AddOrUpdate(frame, new SourceYuv
        {
            Planes = new Lazy<ushort[][]>(() => planes), Width = w, Height = h, Depth = depth,
            Layout = mono ? Av1.Av1PixelLayout.I400 : ssX == 0 ? Av1.Av1PixelLayout.I444 : ssY == 0 ? Av1.Av1PixelLayout.I422 : Av1.Av1PixelLayout.I420,
            Matrices = null, FullRange = yuv.FullRange, Alpha = yuv.Alpha,
        });
        return frame;
    }

    /// <summary>The Y'CbCr of a frame for Y4M output: its kept planes when it has them (read from Y4M, a JPEG's raw
    /// components), else RGB -> 4:4:4 BT.601 full range at 8 bits (8-bit images) or 12 bits.</summary>
    internal static YuvImage YuvFromFrame(ImageFrame image)
    {
        int w = (int)image.Columns, h = (int)image.Rows;
        if (SourcePlanes.TryGetValue(image, out var src) && src.Width == w && src.Height == h)
        {
            var pl = src.Planes.Value;
            var sub = src.Layout switch
            {
                Av1.Av1PixelLayout.I400 => AvifChromaSubsampling.Yuv400, Av1.Av1PixelLayout.I420 => AvifChromaSubsampling.Yuv420,
                Av1.Av1PixelLayout.I422 => AvifChromaSubsampling.Yuv422, _ => AvifChromaSubsampling.Yuv444,
            };
            return new YuvImage
            {
                Width = w, Height = h, Depth = src.Depth, Subsampling = sub, FullRange = src.FullRange,
                Y = pl[0], U = pl.Length > 1 ? pl[1] : null, V = pl.Length > 2 ? pl[2] : null, Alpha = src.Alpha,
            };
        }
        int bd = image.Depth > 8 ? 12 : 8;
        var color = new Av1.Av1ObuWriter.Av1ColorDesc(2, 2, 6, true);
        ReadRgbPlanes(image, bd, out var r, out var g, out var b, out var a, out _, out bool nonOpaque);
        RgbToYuvLibavif(image, bd, Av1.Av1PixelLayout.I444, color, SourceRgbDepth(image), false, r, g, b, out var y, out var u, out var v);
        return new YuvImage
        {
            Width = w, Height = h, Depth = bd, Subsampling = AvifChromaSubsampling.Yuv444, FullRange = true, Y = y, U = u, V = v,
            Alpha = image.HasAlpha && nonOpaque && bd == 8 ? a : null,
        };
    }

    // Kept Y4M planes: Auto subsampling / depth 0 adopt them and the range is retained (avifenc codes a Y4M's own
    // layout, depth and range).
    private static AvifEncodeOptions AdoptSourceFormat(ImageFrame image, AvifEncodeOptions options)
    {
        if (!SourcePlanes.TryGetValue(image, out var src) || src.Matrices != null) return options;
        bool subAuto = options.ChromaSubsampling == AvifChromaSubsampling.Auto && !options.Lossless;
        bool depthAuto = options.BitDepth == 0;
        if (!subAuto && !depthAuto && options.FullRange == src.FullRange) return options;
        var o = options.Clone();
        if (subAuto) o.ChromaSubsampling = ToSubsampling(src.Layout);
        if (depthAuto) o.BitDepth = src.Depth;
        o.FullRange = src.FullRange;   // avifenc -r: "for y4m, range is retained"
        return o;
    }
}
