using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class JpegCoder
{
    /// <summary>The JPEG starting at <paramref name="start"/> decoded to RGB exactly as libjpeg-turbo's default
    /// decompression does (accurate integer IDCT, fancy chroma upsampling, fixed-point YCbCr -> RGB), stored 16-bit
    /// with <see cref="ImageFrame.Depth"/> 8; null for layouts <see cref="ReadRawYuv"/> does not handle.</summary>
    internal static ImageFrame? ReadLibjpegRgb(byte[] jpeg, int start = 0)
    {
        var raw = ReadRawYuv(jpeg, start);
        return raw == null ? null : LibjpegRgb(raw);
    }

    internal static ImageFrame LibjpegRgb(JpegRawYuv raw)
    {
        int w = raw.Width, h = raw.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        frame.Depth = 8;
        int nch = frame.NumberOfChannels;
        if (raw.Layout == Av1.Av1PixelLayout.I400)
        {
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                {
                    ushort v = (ushort)(raw.Planes[0][y * w + x] * 257);
                    row[x * nch] = row[x * nch + 1] = row[x * nch + 2] = v;
                }
            }
            return frame;
        }
        byte[] cb = Upsample(raw.Planes[1], raw.Layout, w, h), cr = Upsample(raw.Planes[2], raw.Layout, w, h);
        BuildYccTables();
        var yp = raw.Planes[0];
        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, yy = yp[i], b = cb[i], r = cr[i];
                row[x * nch] = (ushort)(Clamp255(yy + s_crR![r]) * 257);
                row[x * nch + 1] = (ushort)(Clamp255(yy + ((s_cbG![b] + s_crG![r]) >> 16)) * 257);
                row[x * nch + 2] = (ushort)(Clamp255(yy + s_cbB![b]) * 257);
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

    // jdsample.c fancy upsampling of a chroma plane to full size: h2v1_fancy_upsample (4:2:2) and h2v2_fancy_upsample
    // (4:2:0, the rows above / below the image replicating its first / last row, as jdmainct's context pointers do).
    private static byte[] Upsample(byte[] src, Av1.Av1PixelLayout layout, int w, int h)
    {
        if (layout == Av1.Av1PixelLayout.I444) return src;
        int sw = (w + 1) / 2, sh = layout == Av1.Av1PixelLayout.I420 ? (h + 1) / 2 : h;
        var dst = new byte[w * h];
        var line = new byte[sw * 2];
        for (int y = 0; y < h; y++)
        {
            if (layout == Av1.Av1PixelLayout.I422)
            {
                int o = y * sw;
                if (sw == 1) { line[0] = line[1] = src[o]; }
                else
                {
                    int v = src[o];
                    line[0] = (byte)v;
                    line[1] = (byte)((v * 3 + src[o + 1] + 2) >> 2);
                    for (int c = 1; c < sw - 1; c++)
                    {
                        v = src[o + c] * 3;
                        line[2 * c] = (byte)((v + src[o + c - 1] + 1) >> 2);
                        line[2 * c + 1] = (byte)((v + src[o + c + 1] + 2) >> 2);
                    }
                    v = src[o + sw - 1];
                    line[2 * sw - 2] = (byte)((v * 3 + src[o + sw - 2] + 1) >> 2);
                    line[2 * sw - 1] = (byte)v;
                }
            }
            else
            {
                int r0 = y >> 1, r1 = Math.Clamp((y & 1) == 0 ? r0 - 1 : r0 + 1, 0, sh - 1);
                int o0 = r0 * sw, o1 = r1 * sw;
                int thisSum = src[o0] * 3 + src[o1];
                if (sw == 1) { line[0] = (byte)((thisSum * 4 + 8) >> 4); line[1] = (byte)((thisSum * 4 + 7) >> 4); }
                else
                {
                    int nextSum = src[o0 + 1] * 3 + src[o1 + 1];
                    line[0] = (byte)((thisSum * 4 + 8) >> 4);
                    line[1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
                    int lastSum = thisSum;
                    thisSum = nextSum;
                    for (int c = 1; c < sw - 1; c++)
                    {
                        nextSum = src[o0 + c + 1] * 3 + src[o1 + c + 1];
                        line[2 * c] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
                        line[2 * c + 1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
                        lastSum = thisSum;
                        thisSum = nextSum;
                    }
                    line[2 * sw - 2] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
                    line[2 * sw - 1] = (byte)((thisSum * 4 + 7) >> 4);
                }
            }
            Buffer.BlockCopy(line, 0, dst, y * w, w);
        }
        return dst;
    }
}
