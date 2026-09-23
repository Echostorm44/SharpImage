using System;

namespace SharpImage.Formats;

/// <summary>
/// libyuv ScalePlane / ScalePlane_12 with kFilterBox, as libavif applies it (avifImageScaleWithLimit) when a decoded
/// AV1 image is not the item's 'ispe' size (e.g. the reduced-resolution base layer of a progressive AVIF) and when a gain
/// map of another size is applied. Every dispatch path is ported: Up2 linear / bilinear (9/3/3/1), general bilinear up
/// and down (16.16 stepping, 7-bit column blender as in Intel builds), vertical-only, Down2 / Down4 / Down34 / Down38
/// and the general box filter. 8-bit rows follow the x86 SIMD kernels libavif's build runs (their rounding differs from
/// C for Down34 and Down38_2); verified bit-exact against libyuv on 50 size pairs at 8 and 12 bits. Planes hold 8-bit or
/// up-to-12-bit samples.
/// </summary>
internal static class LibyuvScale
{
    private enum Filter { None, Linear, Bilinear, Box }

    private static Filter Reduce(int sw, int sh, int dw, int dh, Filter f)
    {
        if (f == Filter.Box && ((long)dw * 2 >= sw || (long)dh * 2 >= sh)) f = Filter.Bilinear;
        if (f == Filter.Bilinear)
        {
            if (sh == 1) f = Filter.Linear;
            if (dh == sh || (long)dh * 3 == sh) f = Filter.Linear;
            if (sw == 1) f = Filter.None;
        }
        if (f == Filter.Linear)
        {
            if (sw == 1) f = Filter.None;
            if (dw == sw || (long)dw * 3 == sw) f = Filter.None;
        }
        return f;
    }

    private static int FixedDiv(int num, int div) => (int)(((long)num << 16) / div);
    private static int FixedDiv1(int num, int div) => (int)((((long)num << 16) - 0x00010001) / (div - 1));
    private static int CenterStart(int dx, int s) => dx < 0 ? -((-dx >> 1) + s) : (dx >> 1) + s;

    /// <summary>Scales <paramref name="src"/> (sw x sh, row stride <paramref name="ss"/>) to dw x dh. <paramref name="highBitDepth"/>
    /// selects ScalePlane_12 (libavif depth &gt; 8) over the 8-bit ScalePlane.</summary>
    public static ushort[] ScalePlane(ReadOnlySpan<ushort> src, int ss, int sw, int sh, int dw, int dh, bool highBitDepth)
    {
        var dst = new ushort[dw * dh];
        var f = Reduce(sw, sh, dw, dh, Filter.Box);
        if (highBitDepth)
        {
            // ScalePlane_12 tries its Up2 paths before handing over to ScalePlane_16 (same results as the 16 variants).
            if ((dw + 1) / 2 == sw && f == Filter.Linear) { Up2Linear(src, ss, sw, sh, dst, dw, dh); return dst; }
            if ((dh + 1) / 2 == sh && (dw + 1) / 2 == sw && f is Filter.Bilinear or Filter.Box) { Up2Bilinear(src, ss, sh, dst, dw, dh); return dst; }
        }
        if (dw == sw && dh == sh)
        {
            for (int y = 0; y < dh; y++) src.Slice(y * ss, dw).CopyTo(dst.AsSpan(y * dw));
            return dst;
        }
        if (dw == sw && f != Filter.Box)
        {
            int dy = 0, y = 0;
            if (dh <= sh) { dy = FixedDiv(sh, dh); y = CenterStart(dy, -32768); }
            else if (sh > 1 && dh > 1)
            {
                if (f == Filter.None) { dy = FixedDiv(sh, dh); y = CenterStart(dy, 0); }
                else dy = FixedDiv1(sh, dh);
            }
            Vertical(src, ss, sh, dst, dw, dh, y, dy, f);
            return dst;
        }
        if (dw <= sw && dh <= sh)
        {
            if (4 * dw == 3 * sw && 4 * dh == 3 * sh) { Down34(src, ss, dst, dw, dh, f, highBitDepth); return dst; }
            if (2 * dw == sw && 2 * dh == sh) { Down2(src, ss, dst, dw, dh, f); return dst; }
            if (8 * dw == 3 * sw && 8 * dh == 3 * sh) { Down38(src, ss, dst, dw, dh, f, highBitDepth); return dst; }
            if (4 * dw == sw && 4 * dh == sh && f is Filter.Box or Filter.None) { Down4(src, ss, dst, dw, dh, f); return dst; }
        }
        if (f == Filter.Box && dh * 2 < sh) { Box(src, ss, sw, sh, dst, dw, dh, highBitDepth); return dst; }
        if ((dw + 1) / 2 == sw && f == Filter.Linear) { Up2Linear(src, ss, sw, sh, dst, dw, dh); return dst; }
        if ((dh + 1) / 2 == sh && (dw + 1) / 2 == sw && f is Filter.Bilinear or Filter.Box) { Up2Bilinear(src, ss, sh, dst, dw, dh); return dst; }
        if (f != Filter.None && dh > sh) { BilinearUp(src, ss, sw, sh, dst, dw, dh, f, highBitDepth); return dst; }
        if (f != Filter.None) { BilinearDown(src, ss, sw, sh, dst, dw, dh, f, highBitDepth); return dst; }
        Simple(src, ss, sw, sh, dst, dw, dh);
        return dst;
    }

    // ScaleRowUp2_Linear(_16)_Any_C: edges copied, interior 3:1 / 1:3.
    private static void RowUp2Linear(ReadOnlySpan<ushort> s, Span<ushort> d, int dw)
    {
        d[0] = s[0];
        int ww = (dw - 1) & ~1;
        for (int x = 0; x < ww / 2; x++)
        {
            d[1 + 2 * x] = (ushort)((s[x] * 3 + s[x + 1] + 2) >> 2);
            d[2 + 2 * x] = (ushort)((s[x] + s[x + 1] * 3 + 2) >> 2);
        }
        d[dw - 1] = s[(dw - 1) / 2];
    }

    // ScaleRowUp2_Bilinear(_16)_Any_C: two output rows (da may equal db when the source stride is 0).
    private static void RowUp2Bilinear(ReadOnlySpan<ushort> sa, ReadOnlySpan<ushort> sb, Span<ushort> da, Span<ushort> db, int dw)
    {
        int a0 = (3 * sa[0] + sb[0] + 2) >> 2, b0 = (sa[0] + 3 * sb[0] + 2) >> 2;
        da[0] = (ushort)a0;
        db[0] = (ushort)b0;
        int ww = (dw - 1) & ~1;
        for (int x = 0; x < ww / 2; x++)
        {
            int s0 = sa[x], s1 = sa[x + 1], t0 = sb[x], t1 = sb[x + 1];
            da[1 + 2 * x] = (ushort)((s0 * 9 + s1 * 3 + t0 * 3 + t1 + 8) >> 4);
            da[2 + 2 * x] = (ushort)((s0 * 3 + s1 * 9 + t0 + t1 * 3 + 8) >> 4);
            db[1 + 2 * x] = (ushort)((s0 * 3 + s1 + t0 * 9 + t1 * 3 + 8) >> 4);
            db[2 + 2 * x] = (ushort)((s0 + s1 * 3 + t0 * 3 + t1 * 9 + 8) >> 4);
        }
        int k = (dw - 1) / 2;
        da[dw - 1] = (ushort)((3 * sa[k] + sb[k] + 2) >> 2);
        db[dw - 1] = (ushort)((sa[k] + 3 * sb[k] + 2) >> 2);
    }

    // ScalePlaneUp2_Linear: horizontal 2x, rows picked with a 16.16 step.
    private static void Up2Linear(ReadOnlySpan<ushort> src, int ss, int sw, int sh, ushort[] dst, int dw, int dh)
    {
        if (dh == 1)
        {
            RowUp2Linear(src.Slice((sh - 1) / 2 * ss), dst, dw);
            return;
        }
        int dy = FixedDiv(sh - 1, dh - 1), y = (1 << 15) - 1;
        for (int i = 0; i < dh; i++, y += dy)
            RowUp2Linear(src.Slice((y >> 16) * ss), dst.AsSpan(i * dw, dw), dw);
    }

    // ScalePlaneUp2_Bilinear: first / last output rows from one source row, pairs in between.
    private static void Up2Bilinear(ReadOnlySpan<ushort> src, int ss, int sh, ushort[] dst, int dw, int dh)
    {
        var tmp = new ushort[dw];
        RowUp2Bilinear(src, src, dst.AsSpan(0, dw), tmp, dw);
        tmp.CopyTo(dst.AsSpan(0, dw));   // da/db alias the same row (stride 0): db's values win, as in C
        int d = dw;
        int sp = 0;
        for (int x = 0; x < sh - 1; x++)
        {
            RowUp2Bilinear(src.Slice(sp), src.Slice(sp + ss), dst.AsSpan(d, dw), dst.AsSpan(d + dw, dw), dw);
            sp += ss;
            d += 2 * dw;
        }
        if ((dh & 1) == 0)
        {
            RowUp2Bilinear(src.Slice(sp), src.Slice(sp), dst.AsSpan(d, dw), tmp, dw);
            tmp.CopyTo(dst.AsSpan(d, dw));
        }
    }

    // InterpolateRow(_16)_C.
    private static void InterpolateRow(Span<ushort> d, ReadOnlySpan<ushort> s0, ReadOnlySpan<ushort> s1, int w, int frac)
    {
        if (frac == 0) { s0[..w].CopyTo(d); return; }
        if (frac == 128) { for (int x = 0; x < w; x++) d[x] = (ushort)((s0[x] + s1[x] + 1) >> 1); return; }
        int f0 = 256 - frac;
        for (int x = 0; x < w; x++) d[x] = (ushort)((s0[x] * f0 + s1[x] * frac + 128) >> 8);
    }

    // ScalePlaneVertical(_16).
    private static void Vertical(ReadOnlySpan<ushort> src, int ss, int sh, ushort[] dst, int dw, int dh, int y, int dy, Filter f)
    {
        bool interpolate = f == Filter.Bilinear;
        long maxY = 0;
        if (sh > 1)
        {
            maxY = ((long)sh - 1) << 16;
            if (interpolate) --maxY;
        }
        long y64 = y;
        for (int j = 0; j < dh; j++, y64 += dy)
        {
            if (y64 > maxY) y64 = maxY;
            int yi = (int)(y64 >> 16), yf = interpolate ? (int)((y64 >> 8) & 255) : 0;
            var r0 = src.Slice(yi * ss);
            InterpolateRow(dst.AsSpan(j * dw, dw), r0, interpolate ? src.Slice(yi * ss + ss) : r0, dw, yf);
        }
    }

    // ScaleFilterCols_C (Intel: 7-bit blend with rounding) / ScaleFilterCols_16_C (16-bit blend).
    private static void FilterCols(Span<ushort> d, ReadOnlySpan<ushort> s, int dw, int x, int dx, bool hbd)
    {
        for (int j = 0; j < dw; j++, x += dx)
        {
            int xi = x >> 16, a = s[xi], b = s[xi + 1], fr = x & 0xffff;
            d[j] = hbd ? (ushort)(a + (int)(((long)fr * (b - a) + 0x8000) >> 16))
                       : (ushort)(byte)(a + (((fr >> 9) * (b - a) + 0x40) >> 7));
        }
    }

    // ScalePlaneBilinearUp(_16): 16.16 source stepping (ScaleSlope), two filtered rows rolled down the source.
    private static void BilinearUp(ReadOnlySpan<ushort> src, int ss, int sw, int sh, ushort[] dst, int dw, int dh, Filter f, bool hbd)
    {
        int x = 0, y = 0, dx = 0, dy = 0;
        if (dw <= sw) { dx = FixedDiv(sw, dw); x = CenterStart(dx, -32768); }
        else if (sw > 1 && dw > 1) { dx = FixedDiv1(sw, dw); x = 0; }
        if (f == Filter.Linear) { dy = FixedDiv(sh, dh); y = dy >> 1; }
        else if (dh <= sh) { dy = FixedDiv(sh, dh); y = CenterStart(dy, -32768); }
        else if (sh > 1 && dh > 1) { dy = FixedDiv1(sh, dh); y = 0; }

        int maxY = (sh - 1) << 16;
        if (y > maxY) y = maxY;
        int yi = y >> 16;
        int srcRow = yi;
        var rows = new ushort[2][] { new ushort[dw], new ushort[dw] };
        int cur = 0;
        int lastY = yi;
        FilterCols(rows[cur], src.Slice(srcRow * ss), dw, x, dx, hbd);
        if (sh > 1) srcRow++;
        FilterCols(rows[cur ^ 1], src.Slice(srcRow * ss), dw, x, dx, hbd);
        if (sh > 2) srcRow++;
        for (int j = 0; j < dh; j++, y += dy)
        {
            if (y > maxY) y = maxY;
            yi = y >> 16;
            if (yi != lastY)
            {
                FilterCols(rows[cur], src.Slice(srcRow * ss), dw, x, dx, hbd);
                cur ^= 1;
                lastY = yi;
                if (y + 65536 < maxY) srcRow++;
            }
            if (f == Filter.Linear) InterpolateRow(dst.AsSpan(j * dw, dw), rows[cur], rows[cur], dw, 0);
            else InterpolateRow(dst.AsSpan(j * dw, dw), rows[cur], rows[cur ^ 1], dw, (y >> 8) & 255);
        }
    }

    // ---- Downscaling. 16-bit planes run libyuv's C rows (x86 has no 16-bit SIMD for these); 8-bit planes run the
    // SSSE3/AVX2 rows libavif's x86 build uses, whose rounding differs from C for Down34 and Down38_2 (the "Any"
    // wrappers finish the last dst_width % 24 (Down34) / % 6 (Down38) pixels in C). ------------------------------------

    // ScalePlaneDown2(_16): 2x2 box (Bilinear/Box; the SIMD rows equal C), 2x1 average (Linear) or odd pixel (None).
    private static void Down2(ReadOnlySpan<ushort> src, int ss, ushort[] dst, int dw, int dh, Filter f)
    {
        for (int y = 0; y < dh; y++)
        {
            int r = 2 * y * ss;
            var d = dst.AsSpan(y * dw, dw);
            if (f == Filter.None)
            {
                var s = src.Slice(r + ss);
                for (int x = 0; x < dw; x++) d[x] = s[2 * x + 1];
            }
            else if (f == Filter.Linear)
            {
                var s = src.Slice(r);
                for (int x = 0; x < dw; x++) d[x] = (ushort)((s[2 * x] + s[2 * x + 1] + 1) >> 1);
            }
            else
            {
                var s = src.Slice(r);
                var t = src.Slice(r + ss);
                for (int x = 0; x < dw; x++) d[x] = (ushort)((s[2 * x] + s[2 * x + 1] + t[2 * x] + t[2 * x + 1] + 2) >> 2);
            }
        }
    }

    // ScalePlaneDown4(_16): 4x4 box or point sample of row 2 / column 2.
    private static void Down4(ReadOnlySpan<ushort> src, int ss, ushort[] dst, int dw, int dh, Filter f)
    {
        for (int y = 0; y < dh; y++)
        {
            int r = 4 * y * ss;
            var d = dst.AsSpan(y * dw, dw);
            if (f == Filter.None)
            {
                var s = src.Slice(r + 2 * ss);
                for (int x = 0; x < dw; x++) d[x] = s[4 * x + 2];
                continue;
            }
            for (int x = 0; x < dw; x++)
            {
                int sum = 8;
                for (int k = 0; k < 4; k++)
                    for (int i = 0; i < 4; i++) sum += src[r + k * ss + 4 * x + i];
                d[x] = (ushort)(sum >> 4);
            }
        }
    }

    // ScaleRowDown34_{0,1}_Box: 4 source columns -> 3, rows blended 3:1 (w0 = 3) or 1:1 (w0 = 1). t may be s (stride 0).
    private static void Row34(ReadOnlySpan<ushort> s, ReadOnlySpan<ushort> t, Span<ushort> d, int dw, int w0, bool hbd)
    {
        int simd = hbd ? 0 : dw - dw % 24;
        for (int x = 0; x < dw; x += 3)
        {
            int o = x / 3 * 4;
            if (x < simd)
            {
                // SSSE3: rows first (pavgb; 3:1 as pavg(s, pavg(t, s))), then (3a + b + 2) >> 2 / (a + b + 1) >> 1.
                Span<int> v = stackalloc int[4];
                for (int i = 0; i < 4; i++)
                {
                    int a = s[o + i], b = t[o + i];
                    int avg = (a + b + 1) >> 1;
                    v[i] = w0 == 3 ? (a + avg + 1) >> 1 : avg;
                }
                d[x] = (ushort)((v[0] * 3 + v[1] + 2) >> 2);
                d[x + 1] = (ushort)((v[1] * 2 + v[2] * 2 + 2) >> 2);
                d[x + 2] = (ushort)((v[2] + v[3] * 3 + 2) >> 2);
            }
            else
            {
                int a0 = (s[o] * 3 + s[o + 1] + 2) >> 2, a1 = (s[o + 1] + s[o + 2] + 1) >> 1, a2 = (s[o + 2] + s[o + 3] * 3 + 2) >> 2;
                int b0 = (t[o] * 3 + t[o + 1] + 2) >> 2, b1 = (t[o + 1] + t[o + 2] + 1) >> 1, b2 = (t[o + 2] + t[o + 3] * 3 + 2) >> 2;
                if (w0 == 3)
                {
                    d[x] = (ushort)((a0 * 3 + b0 + 2) >> 2);
                    d[x + 1] = (ushort)((a1 * 3 + b1 + 2) >> 2);
                    d[x + 2] = (ushort)((a2 * 3 + b2 + 2) >> 2);
                }
                else
                {
                    d[x] = (ushort)((a0 + b0 + 1) >> 1);
                    d[x + 1] = (ushort)((a1 + b1 + 1) >> 1);
                    d[x + 2] = (ushort)((a2 + b2 + 1) >> 1);
                }
            }
        }
    }

    // ScaleRowDown34 (point): columns 0, 1, 3 of every 4.
    private static void Row34Point(ReadOnlySpan<ushort> s, Span<ushort> d, int dw)
    {
        for (int x = 0; x < dw; x += 3)
        {
            int o = x / 3 * 4;
            d[x] = s[o]; d[x + 1] = s[o + 1]; d[x + 2] = s[o + 3];
        }
    }

    // ScalePlaneDown34(_16): 4 rows -> 3 (row pairs 0/1 at 3:1, 1/2 at 1:1, 3/2 at 3:1); remainder rows unfiltered
    // vertically.
    private static void Down34(ReadOnlySpan<ushort> src, int ss, ushort[] dst, int dw, int dh, Filter f, bool hbd)
    {
        int fs = f == Filter.Linear ? 0 : ss;
        int sp = 0, dp = 0;
        int y;
        for (y = 0; y < dh - 2; y += 3)
        {
            Row34Any(src, sp, fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += ss; dp += dw;
            Row34Any(src, sp, fs, 1, dst.AsSpan(dp, dw), dw, f, hbd); sp += ss; dp += dw;
            Row34Any(src, sp + ss, -fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += 2 * ss; dp += dw;
        }
        if (dh % 3 == 2)
        {
            Row34Any(src, sp, fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += ss; dp += dw;
            Row34Any(src, sp, 0, 1, dst.AsSpan(dp, dw), dw, f, hbd);
        }
        else if (dh % 3 == 1) Row34Any(src, sp, 0, 3, dst.AsSpan(dp, dw), dw, f, hbd);
    }

    private static void Row34Any(ReadOnlySpan<ushort> src, int at, int stride, int w0, Span<ushort> d, int dw, Filter f, bool hbd)
    {
        if (f == Filter.None) Row34Point(src.Slice(at), d, dw);
        else Row34(src.Slice(at), src.Slice(at + stride), d, dw, w0, hbd);
    }

    // ScaleRowDown38_3_Box (3 rows; SIMD equals C) / ScaleRowDown38_2_Box (2 rows; SSSE3 averages the rows with pavgb
    // first, then divides by 3 / 2 instead of 6 / 4) / ScaleRowDown38 (point: columns 0, 3, 6 of every 8).
    private static void Row38(ReadOnlySpan<ushort> src, int at, int stride, int rows, Span<ushort> d, int dw, Filter f, bool hbd)
    {
        int simd = hbd ? 0 : dw - dw % 6;
        for (int x = 0; x < dw; x += 3)
        {
            int o = at + x / 3 * 8;
            if (f == Filter.None)
            {
                d[x] = src[o]; d[x + 1] = src[o + 3]; d[x + 2] = src[o + 6];
                continue;
            }
            if (rows == 2 && x < simd)
            {
                Span<uint> v = stackalloc uint[8];
                for (int i = 0; i < 8; i++) v[i] = (uint)(src[o + i] + src[o + stride + i] + 1) >> 1;
                d[x] = (ushort)((v[0] + v[1] + v[2]) * (65536u / 3u) >> 16);
                d[x + 1] = (ushort)((v[3] + v[4] + v[5]) * (65536u / 3u) >> 16);
                d[x + 2] = (ushort)((v[6] + v[7]) * (65536u / 2u) >> 16);
                continue;
            }
            Span<uint> col = stackalloc uint[8];
            for (int i = 0; i < 8; i++)
            {
                uint c = 0;
                for (int k = 0; k < rows; k++) c += src[o + k * stride + i];
                col[i] = c;
            }
            uint big = rows == 3 ? 65536u / 9u : 65536u / 6u, small = rows == 3 ? 65536u / 6u : 65536u / 4u;
            d[x] = (ushort)((col[0] + col[1] + col[2]) * big >> 16);
            d[x + 1] = (ushort)((col[3] + col[4] + col[5]) * big >> 16);
            d[x + 2] = (ushort)((col[6] + col[7]) * small >> 16);
        }
    }

    // ScalePlaneDown38(_16): 8 rows -> 3 (3 + 3 + 2 row boxes); remainder rows unfiltered vertically.
    private static void Down38(ReadOnlySpan<ushort> src, int ss, ushort[] dst, int dw, int dh, Filter f, bool hbd)
    {
        int fs = f == Filter.Linear ? 0 : ss;
        int sp = 0, dp = 0, y;
        for (y = 0; y < dh - 2; y += 3)
        {
            Row38(src, sp, fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += 3 * ss; dp += dw;
            Row38(src, sp, fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += 3 * ss; dp += dw;
            Row38(src, sp, fs, 2, dst.AsSpan(dp, dw), dw, f, hbd); sp += 2 * ss; dp += dw;
        }
        if (dh % 3 == 2)
        {
            Row38(src, sp, fs, 3, dst.AsSpan(dp, dw), dw, f, hbd); sp += 3 * ss; dp += dw;
            Row38(src, sp, 0, 3, dst.AsSpan(dp, dw), dw, f, hbd);
        }
        else if (dh % 3 == 1) Row38(src, sp, 0, 3, dst.AsSpan(dp, dw), dw, f, hbd);
    }

    // ScalePlaneBox(_16): rows summed over each box height (ScaleAddRow), then ScaleAddCols{0,1,2}.
    private static void Box(ReadOnlySpan<ushort> src, int ss, int sw, int sh, ushort[] dst, int dw, int dh, bool hbd)
    {
        int dx = FixedDiv(sw, dw), dy = FixedDiv(sh, dh), x0 = 0, y = 0;
        int maxY = sh << 16;
        var row = new uint[sw];
        for (int j = 0; j < dh; j++)
        {
            int iy = y >> 16;
            y += dy;
            if (y > maxY) y = maxY;
            int boxheight = Math.Max(1, (y >> 16) - iy);
            Array.Clear(row);
            for (int k = 0; k < boxheight; k++)
            {
                var s = src.Slice((iy + k) * ss, sw);
                for (int i = 0; i < sw; i++) row[i] = hbd ? row[i] + s[i] : (ushort)(row[i] + s[i]);
            }
            var d = dst.AsSpan(j * dw, dw);
            if ((dx & 0xffff) != 0)
            {
                // ScaleAddCols2: boxes of minboxwidth or minboxwidth + 1 columns.
                int minbox = dx >> 16;
                int s0 = 65536 / (Math.Max(1, minbox) * boxheight), s1 = 65536 / (Math.Max(1, minbox + 1) * boxheight);
                int x = x0;
                for (int i = 0; i < dw; i++)
                {
                    int ix = x >> 16;
                    x += dx;
                    int bw = Math.Max(1, (x >> 16) - ix);
                    uint sum = 0;
                    for (int k = 0; k < bw; k++) sum += row[ix + k];
                    uint v = sum * (uint)(bw - minbox == 0 ? s0 : s1) >> 16;
                    d[i] = hbd ? (ushort)v : (byte)v;
                }
            }
            else if (!hbd && dx == 0x10000)
            {
                // ScaleAddCols0: one column per output.
                int scale = 65536 / boxheight;
                for (int i = 0; i < dw; i++) d[i] = (byte)((uint)row[(x0 >> 16) + i] * (uint)scale >> 16);
            }
            else
            {
                // ScaleAddCols1: integer box width.
                int bw = Math.Max(1, dx >> 16), scale = 65536 / (bw * boxheight);
                int x = hbd ? x0 : x0 >> 16;
                for (int i = 0; i < dw; i++, x += bw)
                {
                    uint sum = 0;
                    for (int k = 0; k < bw; k++) sum += row[x + k];
                    uint v = sum * (uint)scale >> 16;
                    d[i] = hbd ? (ushort)v : (byte)v;
                }
            }
        }
    }

    // ScalePlaneBilinearDown(_16): ScaleSlope stepping; each output row interpolated from two source rows
    // (InterpolateRow at the full source width), then filtered across columns.
    private static void BilinearDown(ReadOnlySpan<ushort> src, int ss, int sw, int sh, ushort[] dst, int dw, int dh, Filter f, bool hbd)
    {
        int x = 0, y = 0, dx = 0, dy = 0;
        if (dw <= sw) { dx = FixedDiv(sw, dw); x = CenterStart(dx, -32768); }
        else if (sw > 1 && dw > 1) { dx = FixedDiv1(sw, dw); x = 0; }
        if (f == Filter.Linear) { dy = FixedDiv(sh, dh); y = dy >> 1; }
        else if (dh <= sh) { dy = FixedDiv(sh, dh); y = CenterStart(dy, -32768); }
        else if (sh > 1 && dh > 1) { dy = FixedDiv1(sh, dh); y = 0; }

        int maxY = (sh - 1) << 16;
        if (y > maxY) y = maxY;
        var row = new ushort[sw + 1];
        for (int j = 0; j < dh; j++)
        {
            int yi = y >> 16;
            var s0 = src.Slice(yi * ss);
            if (f == Filter.Linear) FilterCols(dst.AsSpan(j * dw, dw), s0, dw, x, dx, hbd);
            else
            {
                int yf = (y >> 8) & 255;
                InterpolateRow(row, s0, yf != 0 ? src.Slice((yi + 1) * ss) : s0, sw, yf);
                row[sw] = row[sw - 1];
                FilterCols(dst.AsSpan(j * dw, dw), row, dw, x, dx, hbd);
            }
            y += dy;
            if (y > maxY) y = maxY;
        }
    }

    // ScalePlaneSimple: point sampling (ScaleSlope, kFilterNone).
    private static void Simple(ReadOnlySpan<ushort> src, int ss, int sw, int sh, ushort[] dst, int dw, int dh)
    {
        int dx = FixedDiv(sw, dw), dy = FixedDiv(sh, dh);
        int x0 = CenterStart(dx, 0), y = CenterStart(dy, 0);
        for (int j = 0; j < dh; j++, y += dy)
        {
            var row = src.Slice((y >> 16) * ss);
            int x = x0;
            for (int i = 0; i < dw; i++, x += dx) dst[j * dw + i] = row[x >> 16];
        }
    }
}
