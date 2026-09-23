using System;

namespace SharpImage.Formats;

/// <summary>
/// libyuv ScalePlane / ScalePlane_12 with kFilterBox, as libavif applies it (avifImageScaleWithLimit) when a decoded
/// AV1 image is not the item's 'ispe' size — e.g. the reduced-resolution base layer of a progressive AVIF. Every
/// upscaling path is ported exactly (C row functions; libyuv's x86 SIMD rows are bit-identical to them: Intel builds use
/// the 7-bit column blender in C too): Up2 linear / bilinear (9/3/3/1), general bilinear up (16.16 stepping) and the
/// vertical-only path. Downscaling (never needed for valid layered AVIFs, whose layers are at most 'ispe' size) falls
/// back to libyuv's point sampling (ScalePlaneSimple). Planes hold 8-bit or up-to-12-bit samples.
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
        bool down = dw <= sw && dh <= sh;
        if (!down)
        {
            if ((dw + 1) / 2 == sw && f == Filter.Linear) { Up2Linear(src, ss, sw, sh, dst, dw, dh); return dst; }
            if ((dh + 1) / 2 == sh && (dw + 1) / 2 == sw && f is Filter.Bilinear or Filter.Box) { Up2Bilinear(src, ss, sh, dst, dw, dh); return dst; }
            if (f != Filter.None && dh > sh) { BilinearUp(src, ss, sw, sh, dst, dw, dh, f, highBitDepth); return dst; }
        }
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
