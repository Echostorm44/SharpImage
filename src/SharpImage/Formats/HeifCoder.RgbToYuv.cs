using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    // True while encoding RGB that libavif holds in an RGBA avifRGBImage whatever its alpha (avifRGBImageSetDefaults' format,
    // e.g. avifgainmaputil swapbase's tone-mapped base): selects libavif's RGBA conversion routes.
    [ThreadStatic] private static bool t_rgbaSource;

    // The RGB sample depth libavif would see for this frame: its declared depth, else 8 when every sample is an 8-bit
    // value (x * 257), else 16.
    private static int SourceRgbDepth(ImageFrame image) =>
        image.Depth is >= 1 and < 16 ? image.Depth : HasSubByteDetail(image) ? 16 : 8;

    /// <summary>
    /// libavif avifImageRGBToYUV with the default (automatic) chroma downsampling: 8-bit RGB to 8-bit YUV with the BT.601
    /// matrix goes through libyuv's fixed-point converters wherever libavif has one for the RGB layout (RGB: every
    /// subsampling; RGBA: 4:2:2 / 4:2:0 full range, every subsampling limited range), everything else through libavif's
    /// float path (identity, YCgCo, premultiplication, other matrices and depths). YCgCo-Re/Ro keep their exact integer
    /// path.
    /// </summary>
    private static void RgbToYuvLibavif(ImageFrame image, int bd, Av1.Av1PixelLayout layout, Av1.Av1ObuWriter.Av1ColorDesc color,
        int srcDepth, bool premultiply, double[] r, double[] g, double[] b, out ushort[] y, out ushort[] u, out ushort[] v)
    {
        int w = (int)image.Columns, h = (int)image.Rows;
        if (color.Matrix is 16 or 17)
        {
            RgbToYuvAvif(r, g, b, w, h, bd, layout, color, out y, out u, out v);
            return;
        }
        bool rgba = image.HasAlpha || t_rgbaSource;
        if (!t_avoidLibyuv && bd == 8 && srcDepth == 8 && !premultiply && color.Matrix is 5 or 6 && layout != Av1.Av1PixelLayout.I400
            && (!rgba || !color.FullRange || layout != Av1.Av1PixelLayout.I444))
        {
            RgbToYuvLibyuv8(image, layout, color.FullRange, out y, out u, out v);
            return;
        }
        RgbToYuvLibavifFloat(image, bd, layout, color, srcDepth, premultiply, out y, out u, out v);
    }

    // libyuv (1972) ARGBTo{I,J}4xxMatrix with kI601 / kJPEG constants: Y = (cY . rgb + addY) >> 8, U/V = (32768 + cU/V . rgb)
    // >> 8 on 8-bit samples; 4:2:0 chroma from the rounded 2x2 mean ((sum + 2) >> 2), 4:2:2 from a row averaged with
    // itself, odd edges from the pair that exists ((a + b + 1) >> 1).
    private static void RgbToYuvLibyuv8(ImageFrame image, Av1.Av1PixelLayout layout, bool full, out ushort[] y, out ushort[] u, out ushort[] v)
    {
        int w = (int)image.Columns, h = (int)image.Rows, n = image.NumberOfChannels;
        int ry, gy, by, ay, ru, gu, bu, rv, gv, bv;
        if (full) (ry, gy, by, ay, ru, gu, bu, rv, gv, bv) = (77, 150, 29, 128, -43, -85, 128, 128, -107, -21);
        else (ry, gy, by, ay, ru, gu, bu, rv, gv, bv) = (66, 129, 25, 4224, -38, -74, 112, 112, -94, -18);
        var p8 = new byte[3][];
        for (int c = 0; c < 3; c++) p8[c] = new byte[w * h];
        for (int j = 0; j < h; j++)
        {
            var row = image.GetPixelRow(j);
            for (int i = 0; i < w; i++)
                for (int c = 0; c < 3; c++)
                    p8[c][j * w + i] = (byte)((row[i * n + (n >= 3 ? c : 0)] * 255 + 32767) / 65535);
        }
        byte[] R = p8[0], G = p8[1], B = p8[2];
        y = new ushort[w * h];
        for (int i = 0; i < w * h; i++) y[i] = (ushort)((ry * R[i] + gy * G[i] + by * B[i] + ay) >> 8);
        int ssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        u = new ushort[cw * ch];
        v = new ushort[cw * ch];
        for (int cj = 0; cj < ch; cj++)
        {
            int j0 = cj << ssY, j1 = ssY == 1 && j0 + 1 < h ? j0 + 1 : j0;
            for (int ci = 0; ci < cw; ci++)
            {
                int i0 = ci << ssX;
                int ar, ag, ab;
                if (ssX == 0) { int k = j0 * w + i0; ar = R[k]; ag = G[k]; ab = B[k]; }
                else if (i0 + 1 < w)
                {
                    int a = j0 * w + i0, c2 = j1 * w + i0;
                    ar = (R[a] + R[a + 1] + R[c2] + R[c2 + 1] + 2) >> 2;
                    ag = (G[a] + G[a + 1] + G[c2] + G[c2 + 1] + 2) >> 2;
                    ab = (B[a] + B[a + 1] + B[c2] + B[c2 + 1] + 2) >> 2;
                }
                else
                {
                    int a = j0 * w + i0, c2 = j1 * w + i0;
                    ar = (R[a] + R[c2] + 1) >> 1;
                    ag = (G[a] + G[c2] + 1) >> 1;
                    ab = (B[a] + B[c2] + 1) >> 1;
                }
                u[cj * cw + ci] = (ushort)((32768 + bu * ab + gu * ag + ru * ar) >> 8);
                v[cj * cw + ci] = (ushort)((32768 + rv * ar + gv * ag + bv * ab) >> 8);
            }
        }
    }

    // libavif's built-in float conversion (reformat.c): samples normalised by the RGB depth's maximum in float, optional
    // premultiplication by alpha, per-pixel YUV in float, chroma averaged over the 2x2 (4:2:0) or 2x1 (4:2:2) block in
    // float, then avifYUVColorSpaceInfo{Y,UV}ToUNorm (floor(x * range + bias + 0.5), clamped).
    private static void RgbToYuvLibavifFloat(ImageFrame image, int bd, Av1.Av1PixelLayout layout, Av1.Av1ObuWriter.Av1ColorDesc color,
        int srcDepth, bool premultiply, out ushort[] y, out ushort[] u, out ushort[] v)
    {
        int w = (int)image.Columns, h = (int)image.Rows, n = image.NumberOfChannels;
        int max = (1 << bd) - 1, srcMax = (1 << Math.Clamp(srcDepth, 1, 16)) - 1;
        float srcMaxF = srcMax;
        bool full = color.FullRange;
        float biasY = full ? 0.0f : 16 << (bd - 8), rangeY = full ? max : 219 << (bd - 8);
        float biasUV = 1 << (bd - 1), rangeUV = full ? max : 224 << (bd - 8);
        int mode = color.Matrix switch { 0 => 1, 8 => 2, _ => 0 };   // 0 YUV, 1 identity, 2 YCgCo
        (float kr, float kb) = color.Matrix == 12 ? ChromaDerivedKrKb(color.Primaries) : MatrixKrKb(color.Matrix);
        float kg = 1.0f - kr - kb;
        int ssX = layout == Av1.Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1.Av1PixelLayout.I420 ? 1 : 0;
        bool mono = layout == Av1.Av1PixelLayout.I400;
        int cw = (w + ssX) >> ssX, ch = (h + ssY) >> ssY;
        y = new ushort[w * h];
        u = mono ? [] : new ushort[cw * ch];
        v = mono ? [] : new ushort[cw * ch];
        int ToY(float x) => Math.Clamp((int)MathF.Floor(x * rangeY + biasY + 0.5f), 0, max);
        int ToUV(float x) => Math.Clamp((int)MathF.Floor(mode == 1 ? x * rangeY + biasY + 0.5f : x * rangeUV + biasUV + 0.5f), 0, max);
        float Sample(ushort s) => (srcMax == 65535 ? s : (int)((s * (long)srcMax + 32767) / 65535)) / srcMaxF;
        Span<float> bu = stackalloc float[4], bv = stackalloc float[4];
        for (int oj = 0; oj < h; oj += 2)
        {
            int bh = oj + 1 >= h ? 1 : 2;
            for (int oi = 0; oi < w; oi += 2)
            {
                int bw = oi + 1 >= w ? 1 : 2;
                for (int bj = 0; bj < bh; bj++)
                {
                    var row = image.GetPixelRow(oj + bj);
                    for (int bi = 0; bi < bw; bi++)
                    {
                        int x = oi + bi, o = x * n;
                        float R = Sample(row[o]), G = Sample(row[o + (n >= 3 ? 1 : 0)]), B = Sample(row[o + (n >= 3 ? 2 : 0)]);
                        if (premultiply && image.HasAlpha)
                        {
                            float a = Sample(row[o + n - 1]);
                            if (a == 0) { R = G = B = 0; }
                            else if (a < 1.0f) { R *= a; G *= a; B *= a; }
                        }
                        float Y, U, V;
                        if (mode == 1) { Y = G; U = B; V = R; }
                        else if (mode == 2) { Y = 0.5f * G + 0.25f * (R + B); U = 0.5f * G - 0.25f * (R + B); V = 0.5f * (R - B); }
                        else
                        {
                            Y = (kr * R) + (kg * G) + (kb * B);
                            U = (B - Y) / (2 * (1 - kb));
                            V = (R - Y) / (2 * (1 - kr));
                        }
                        y[(oj + bj) * w + x] = (ushort)ToY(Y);
                        if (layout == Av1.Av1PixelLayout.I444)
                        {
                            u[(oj + bj) * w + x] = (ushort)ToUV(U);
                            v[(oj + bj) * w + x] = (ushort)ToUV(V);
                        }
                        bu[bi * 2 + bj] = U;
                        bv[bi * 2 + bj] = V;
                    }
                }
                if (layout == Av1.Av1PixelLayout.I420)
                {
                    float su = 0.0f, sv = 0.0f;
                    for (int bj = 0; bj < bh; bj++)
                        for (int bi = 0; bi < bw; bi++) { su += bu[bi * 2 + bj]; sv += bv[bi * 2 + bj]; }
                    float total = bw * bh;
                    u[(oj >> 1) * cw + (oi >> 1)] = (ushort)ToUV(su / total);
                    v[(oj >> 1) * cw + (oi >> 1)] = (ushort)ToUV(sv / total);
                }
                else if (layout == Av1.Av1PixelLayout.I422)
                {
                    for (int bj = 0; bj < bh; bj++)
                    {
                        float su = 0.0f, sv = 0.0f;
                        for (int bi = 0; bi < bw; bi++) { su += bu[bi * 2 + bj]; sv += bv[bi * 2 + bj]; }
                        u[(oj + bj) * cw + (oi >> 1)] = (ushort)ToUV(su / bw);
                        v[(oj + bj) * cw + (oi >> 1)] = (ushort)ToUV(sv / bw);
                    }
                }
            }
        }
    }

    // libavif's grey path (avifRGBFormatIsGray sources): Y = the grey sample normalised in float (premultiplied by
    // alpha when asked), through avifYUVColorSpaceInfoYToUNorm.
    private static ushort[] GreyLumaLibavif(ImageFrame image, int bd, bool fullRange, int srcDepth, bool premultiply)
    {
        int w = (int)image.Columns, h = (int)image.Rows, n = image.NumberOfChannels;
        int max = (1 << bd) - 1, srcMax = (1 << Math.Clamp(srcDepth, 1, 16)) - 1;
        float srcMaxF = srcMax;
        float biasY = fullRange ? 0.0f : 16 << (bd - 8), rangeY = fullRange ? max : 219 << (bd - 8);
        float Sample(ushort s) => (srcMax == 65535 ? s : (int)((s * (long)srcMax + 32767) / 65535)) / srcMaxF;
        var y = new ushort[w * h];
        for (int j = 0; j < h; j++)
        {
            var row = image.GetPixelRow(j);
            for (int i = 0; i < w; i++)
            {
                float gv = Sample(row[i * n]);
                if (premultiply && image.HasAlpha)
                {
                    float a = Sample(row[i * n + n - 1]);
                    if (a == 0) gv = 0;
                    else if (a < 1.0f) gv *= a;
                }
                y[j * w + i] = (ushort)Math.Clamp((int)MathF.Floor(gv * rangeY + biasY + 0.5f), 0, max);
            }
        }
        return y;
    }
}
