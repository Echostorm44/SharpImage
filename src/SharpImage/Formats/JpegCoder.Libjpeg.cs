using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>A JPEG's component planes after the IDCT, each at its downsampled size (libjpeg raw_data_out).</summary>
internal sealed class JpegComponents
{
    public required byte[][] Planes;
    public required int[] Widths, Heights, HSamp, VSamp;
    public required int MaxH, MaxV, Width, Height;
    /// <summary>1 component: grey; 3: YCbCr, or RGB (Adobe transform 0 / component ids 'R','G','B').</summary>
    public required bool IsRgb;
}

public static partial class JpegCoder
{
    /// <summary>
    /// Decodes the JPEG starting at <paramref name="start"/> into its component planes with libjpeg-turbo's accurate
    /// integer IDCT (jpeg_idct_islow). Null for what this path does not cover (4-component CMYK / YCCK, precision other
    /// than 8 bits, lossless or arithmetic-coded files, or anything the coefficient reader rejects).
    /// </summary>
    internal static JpegComponents? ReadComponents(byte[] jpeg, int start = 0)
    {
        JpegDctData d;
        try { d = ReadDctData(new MemoryStream(jpeg, start, jpeg.Length - start)); }
        catch (Exception) { return null; }
        if (d.ComponentCount is not (1 or 3)) return null;
        int n = d.ComponentCount;
        var planes = new byte[n][];
        int[] widths = new int[n], heights = new int[n], hs = new int[n], vs = new int[n];
        Span<byte> block = stackalloc byte[64];
        for (int ci = 0; ci < n; ci++)
        {
            var comp = d.Components[ci];
            hs[ci] = comp.HSample;
            vs[ci] = comp.VSample;
            int pw = widths[ci] = (d.Width * comp.HSample + d.MaxHSample - 1) / d.MaxHSample;
            int ph = heights[ci] = (d.Height * comp.VSample + d.MaxVSample - 1) / d.MaxVSample;
            var plane = new byte[pw * ph];
            var qt = d.QuantTables[comp.QuantTableIndex];
            if (qt == null) return null;
            int bw = (pw + 7) / 8, bh = (ph + 7) / 8;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    IdctIslow(comp.Blocks[by * comp.BlocksPerRow + bx], qt, block);
                    for (int y = 0; y < 8 && by * 8 + y < ph; y++)
                        for (int x = 0; x < 8 && bx * 8 + x < pw; x++)
                            plane[(by * 8 + y) * pw + bx * 8 + x] = block[y * 8 + x];
                }
            planes[ci] = plane;
        }
        return new JpegComponents
        {
            Planes = planes, Widths = widths, Heights = heights, HSamp = hs, VSamp = vs,
            MaxH = d.MaxHSample, MaxV = d.MaxVSample, Width = d.Width, Height = d.Height,
            IsRgb = n == 3 && !IsYCbCr(jpeg, start, d),
        };
    }

    /// <summary>The JPEG starting at <paramref name="start"/> decoded to RGB exactly as libjpeg-turbo's default
    /// decompression does (islow IDCT, its upsampler choice with fancy upsampling, fixed-point YCbCr -> RGB), stored
    /// 16-bit; null when <see cref="ReadComponents"/> does not cover the file.</summary>
    internal static ImageFrame? ReadLibjpegRgb(byte[] jpeg, int start = 0)
    {
        var c = ReadComponents(jpeg, start);
        return c == null ? null : LibjpegRgb(c);
    }

    internal static ImageFrame? LibjpegRgb(JpegComponents c)
    {
        int w = c.Width, h = c.Height, n = c.Planes.Length;
        var full = new byte[n][];
        for (int ci = 0; ci < n; ci++)
            if ((full[ci] = Upsample(c, ci)) is null) return null;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        int nch = frame.NumberOfChannels;
        if (n == 1)
        {
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    ushort v = (ushort)(full[0][y * w + x] * 257);
                    row[x * nch] = row[x * nch + 1] = row[x * nch + 2] = v;
                }
            }
            return frame;
        }
        BuildYccTables();
        byte[] p0 = full[0], p1 = full[1], p2 = full[2];
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (c.IsRgb)
                {
                    row[x * nch] = (ushort)(p0[i] * 257);
                    row[x * nch + 1] = (ushort)(p1[i] * 257);
                    row[x * nch + 2] = (ushort)(p2[i] * 257);
                    continue;
                }
                int yy = p0[i], cb = p1[i], cr = p2[i];
                row[x * nch] = (ushort)(Clamp255(yy + s_crR![cr]) * 257);
                row[x * nch + 1] = (ushort)(Clamp255(yy + ((s_cbG![cb] + s_crG![cr]) >> 16)) * 257);
                row[x * nch + 2] = (ushort)(Clamp255(yy + s_cbB![cb]) * 257);
            }
        }
        return frame;

        static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }

    private static int[]? s_crR, s_cbB, s_crG, s_cbG;

    // jdcolor.c build_ycc_rgb_table (SCALEBITS 16).
    private static void BuildYccTables()
    {
        if (s_cbG != null) return;
        const int scale = 16, half = 1 << (scale - 1);
        static int Fix(double v) => (int)(v * (1 << scale) + 0.5);
        int[] crR = new int[256], cbB = new int[256], crG = new int[256], cbG = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int x = i - 128;
            crR[i] = (Fix(1.40200) * x + half) >> scale;
            cbB[i] = (Fix(1.77200) * x + half) >> scale;
            crG[i] = -Fix(0.71414) * x;
            cbG[i] = -Fix(0.34414) * x + half;
        }
        (s_crR, s_cbB, s_crG) = (crR, cbB, crG);
        s_cbG = cbG;
    }

    // jdsample.c jinit_upsampler's per-component choice (do_fancy_upsampling on): full size; 2:1 horizontal -> h2v1
    // fancy (triangle filter) when the component is more than 2 samples wide, else replication; 1:2 vertical -> h1v2
    // fancy; 2:2 -> h2v2 fancy when more than 2 samples wide, else replication; other integral ratios -> replication
    // (int_upsample). The rows above / below the image repeat its first / last row (jdmainct's context pointers).
    private static byte[]? Upsample(JpegComponents c, int ci)
    {
        int w = c.Width, h = c.Height, sw = c.Widths[ci], sh = c.Heights[ci];
        byte[] src = c.Planes[ci];
        if (c.MaxH % c.HSamp[ci] != 0 || c.MaxV % c.VSamp[ci] != 0) return null;
        int rx = c.MaxH / c.HSamp[ci], ry = c.MaxV / c.VSamp[ci];
        if (rx == 1 && ry == 1) return src;
        var dst = new byte[w * h];
        bool fancyH = rx == 2 && sw > 2;
        if (rx == 2 && ry == 1 && fancyH)
        {
            var line = new byte[sw * 2];
            for (int y = 0; y < h; y++)
            {
                int o = y * sw;
                int v = src[o];
                line[0] = (byte)v;
                line[1] = (byte)((v * 3 + src[o + 1] + 2) >> 2);
                for (int x = 1; x < sw - 1; x++)
                {
                    v = src[o + x] * 3;
                    line[2 * x] = (byte)((v + src[o + x - 1] + 1) >> 2);
                    line[2 * x + 1] = (byte)((v + src[o + x + 1] + 2) >> 2);
                }
                v = src[o + sw - 1];
                line[2 * sw - 2] = (byte)((v * 3 + src[o + sw - 2] + 1) >> 2);
                line[2 * sw - 1] = (byte)v;
                Buffer.BlockCopy(line, 0, dst, y * w, w);
            }
            return dst;
        }
        if (rx == 1 && ry == 2)
        {
            for (int y = 0; y < h; y++)
            {
                int r0 = y >> 1, r1 = Math.Clamp((y & 1) == 0 ? r0 - 1 : r0 + 1, 0, sh - 1), bias = (y & 1) == 0 ? 1 : 2;
                for (int x = 0; x < w; x++) dst[y * w + x] = (byte)((src[r0 * sw + x] * 3 + src[r1 * sw + x] + bias) >> 2);
            }
            return dst;
        }
        if (rx == 2 && ry == 2 && fancyH)
        {
            var line = new byte[sw * 2];
            for (int y = 0; y < h; y++)
            {
                int r0 = y >> 1, r1 = Math.Clamp((y & 1) == 0 ? r0 - 1 : r0 + 1, 0, sh - 1);
                int o0 = r0 * sw, o1 = r1 * sw;
                int thisSum = src[o0] * 3 + src[o1], nextSum = src[o0 + 1] * 3 + src[o1 + 1];
                line[0] = (byte)((thisSum * 4 + 8) >> 4);
                line[1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
                int lastSum = thisSum;
                thisSum = nextSum;
                for (int x = 1; x < sw - 1; x++)
                {
                    nextSum = src[o0 + x + 1] * 3 + src[o1 + x + 1];
                    line[2 * x] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
                    line[2 * x + 1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
                    lastSum = thisSum;
                    thisSum = nextSum;
                }
                line[2 * sw - 2] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
                line[2 * sw - 1] = (byte)((thisSum * 4 + 7) >> 4);
                Buffer.BlockCopy(line, 0, dst, y * w, w);
            }
            return dst;
        }
        // Replication (h2v1_upsample, h2v2_upsample, int_upsample).
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(y / ry, sh - 1);
            for (int x = 0; x < w; x++) dst[y * w + x] = src[sy * sw + Math.Min(x / rx, sw - 1)];
        }
        return dst;
    }
}
