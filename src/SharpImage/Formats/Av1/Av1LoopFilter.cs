// AV1 Loop Filter — ported from dav1d loopfilter_tmpl.c, lf_apply_tmpl.c, lf_mask.c
// Reference: dav1d/src/loopfilter_tmpl.c (filter kernels)
//            dav1d/src/lf_apply_tmpl.c   (per-SB-row application)
//            dav1d/src/lf_mask.c          (bitmask generation + EIH/level computation)

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 deblocking loop filter — filter kernels, mask generation, and per-SB-row application.
/// All methods are 8-bit. 10-bit support would shift E/I/H/F by bitdepth_min_8.
/// </summary>
public static class Av1LoopFilter
{
    public static bool DumpEdges;

    // ========================================================================
    // Filter Kernel (loopfilter_tmpl.c: loop_filter)
    // ========================================================================

    /// <summary>
    /// Core loop filter for one 4-pixel-wide edge.
    /// stridea = stride along the edge (perpendicular to filtering direction).
    /// strideb = stride across the edge (filtering direction: 1 for H-filter, stride for V-filter).
    /// wd = filter width: 4, 6, 8, or 16.
    /// For 8-bit: bitdepth_min_8 = 0 so F=1, E/I/H are unshifted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterEdge(Span<byte> dst, int dstOffset,
        int E, int I, int H, int strideA, int strideB, int wd)
    {
        const int F = 1; // 1 << (bitdepth - 8), for 8-bit = 1

        for (int i = 0; i < 4; i++, dstOffset += strideA)
        {
            int p1 = dst[dstOffset + strideB * -2];
            int p0 = dst[dstOffset + strideB * -1];
            int q0 = dst[dstOffset + strideB * 0];
            int q1 = dst[dstOffset + strideB * 1];
            int p2 = 0, p3 = 0, q2 = 0, q3 = 0;
            int p4, p5, p6, q4, q5, q6;

            // Filter mask (fm)
            int fm = (Math.Abs(p1 - p0) <= I && Math.Abs(q1 - q0) <= I &&
                      Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) <= E) ? 1 : 0;

            if (wd > 4)
            {
                p2 = dst[dstOffset + strideB * -3];
                q2 = dst[dstOffset + strideB * 2];
                fm &= (Math.Abs(p2 - p1) <= I && Math.Abs(q2 - q1) <= I) ? 1 : 0;

                if (wd > 6)
                {
                    p3 = dst[dstOffset + strideB * -4];
                    q3 = dst[dstOffset + strideB * 3];
                    fm &= (Math.Abs(p3 - p2) <= I && Math.Abs(q3 - q2) <= I) ? 1 : 0;
                }
            }

            if (fm == 0) continue;

            bool dbg = DumpEdges && (dstOffset % 64) <= 20 && i == 0;
            if (dbg)
                AvDbg.W($"[DB-DEBLK] off={dstOffset} E={E} I={I} H={H} wd={wd} fm=1 p1={p1:x2} p0={p0:x2} q0={q0:x2} q1={q1:x2}");

            int flat8out = 0, flat8in = 0;

            if (wd >= 16)
            {
                p6 = dst[dstOffset + strideB * -7];
                p5 = dst[dstOffset + strideB * -6];
                p4 = dst[dstOffset + strideB * -5];
                q4 = dst[dstOffset + strideB * 4];
                q5 = dst[dstOffset + strideB * 5];
                q6 = dst[dstOffset + strideB * 6];

                flat8out = (Math.Abs(p6 - p0) <= F && Math.Abs(p5 - p0) <= F &&
                            Math.Abs(p4 - p0) <= F && Math.Abs(q4 - q0) <= F &&
                            Math.Abs(q5 - q0) <= F && Math.Abs(q6 - q0) <= F) ? 1 : 0;
            }
            else
            {
                p4 = p5 = p6 = q4 = q5 = q6 = 0;
            }

            if (wd >= 6)
                flat8in = (Math.Abs(p2 - p0) <= F && Math.Abs(p1 - p0) <= F &&
                           Math.Abs(q1 - q0) <= F && Math.Abs(q2 - q0) <= F) ? 1 : 0;

            if (wd >= 8)
                flat8in &= (Math.Abs(p3 - p0) <= F && Math.Abs(q3 - q0) <= F) ? 1 : 0;

            if (dbg)
                AvDbg.W($"[DB-FLAT] off={dstOffset} flat8in={flat8in} p2={p2:x2} p3={p3:x2} q2={q2:x2} q3={q3:x2}");

            if (wd >= 16 && (flat8out & flat8in) != 0)
            {
                if (dbg) AvDbg.W($"[DB-PATH] wd=16 flat-out+in");
                // Wide flat filter (13-tap)
                dst[dstOffset + strideB * -6] = (byte)((p6 + p6 + p6 + p6 + p6 + p6 * 2 + p5 * 2 +
                    p4 * 2 + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
                dst[dstOffset + strideB * -5] = (byte)((p6 + p6 + p6 + p6 + p6 + p5 * 2 + p4 * 2 +
                    p3 * 2 + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
                dst[dstOffset + strideB * -4] = (byte)((p6 + p6 + p6 + p6 + p5 + p4 * 2 + p3 * 2 +
                    p2 * 2 + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
                dst[dstOffset + strideB * -3] = (byte)((p6 + p6 + p6 + p5 + p4 + p3 * 2 + p2 * 2 +
                    p1 * 2 + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
                dst[dstOffset + strideB * -2] = (byte)((p6 + p6 + p5 + p4 + p3 + p2 * 2 + p1 * 2 +
                    p0 * 2 + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
                dst[dstOffset + strideB * -1] = (byte)((p6 + p5 + p4 + p3 + p2 + p1 * 2 + p0 * 2 +
                    q0 * 2 + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
                dst[dstOffset + strideB * 0] = (byte)((p5 + p4 + p3 + p2 + p1 + p0 * 2 + q0 * 2 +
                    q1 * 2 + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
                dst[dstOffset + strideB * 1] = (byte)((p4 + p3 + p2 + p1 + p0 + q0 * 2 + q1 * 2 +
                    q2 * 2 + q3 + q4 + q5 + q6 + q6 + 8) >> 4);
                dst[dstOffset + strideB * 2] = (byte)((p3 + p2 + p1 + p0 + q0 + q1 * 2 + q2 * 2 +
                    q3 * 2 + q4 + q5 + q6 + q6 + q6 + 8) >> 4);
                dst[dstOffset + strideB * 3] = (byte)((p2 + p1 + p0 + q0 + q1 + q2 * 2 + q3 * 2 +
                    q4 * 2 + q5 + q6 + q6 + q6 + q6 + 8) >> 4);
                dst[dstOffset + strideB * 4] = (byte)((p1 + p0 + q0 + q1 + q2 + q3 * 2 + q4 * 2 +
                    q5 * 2 + q6 + q6 + q6 + q6 + q6 + 8) >> 4);
                dst[dstOffset + strideB * 5] = (byte)((p0 + q0 + q1 + q2 + q3 + q4 * 2 + q5 * 2 +
                    q6 * 2 + q6 + q6 + q6 + q6 + q6 + 8) >> 4);
            }
            else if (wd >= 8 && flat8in != 0)
            {
                if (dbg) AvDbg.W($"[DB-PATH] wd=8 flat-in");
                // 7-tap flat filter
                dst[dstOffset + strideB * -3] = (byte)((p3 + p3 + p3 + 2 * p2 + p1 + p0 + q0 + 4) >> 3);
                dst[dstOffset + strideB * -2] = (byte)((p3 + p3 + p2 + 2 * p1 + p0 + q0 + q1 + 4) >> 3);
                dst[dstOffset + strideB * -1] = (byte)((p3 + p2 + p1 + 2 * p0 + q0 + q1 + q2 + 4) >> 3);
                dst[dstOffset + strideB * 0] = (byte)((p2 + p1 + p0 + 2 * q0 + q1 + q2 + q3 + 4) >> 3);
                dst[dstOffset + strideB * 1] = (byte)((p1 + p0 + q0 + 2 * q1 + q2 + q3 + q3 + 4) >> 3);
                dst[dstOffset + strideB * 2] = (byte)((p0 + q0 + q1 + 2 * q2 + q3 + q3 + q3 + 4) >> 3);
            }
            else if (wd == 6 && flat8in != 0)
            {
                // 5-tap flat filter (chroma 6-wide)
                dst[dstOffset + strideB * -2] = (byte)((p2 + 2 * p2 + 2 * p1 + 2 * p0 + q0 + 4) >> 3);
                dst[dstOffset + strideB * -1] = (byte)((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3);
                dst[dstOffset + strideB * 0] = (byte)((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3);
                dst[dstOffset + strideB * 1] = (byte)((p0 + 2 * q0 + 2 * q1 + 2 * q2 + q2 + 4) >> 3);
            }
            else
            {
                if (dbg) AvDbg.W($"[DB-PATH] narrow");
                // Narrow filter (4-wide)
                int hev = (Math.Abs(p1 - p0) > H || Math.Abs(q1 - q0) > H) ? 1 : 0;

                if (hev != 0)
                {
                    int f = Clip(p1 - q1, -128, 127);
                    f = Clip(3 * (q0 - p0) + f, -128, 127);
                    int f1 = Math.Min(f + 4, 127) >> 3;
                    int f2 = Math.Min(f + 3, 127) >> 3;
                    dst[dstOffset + strideB * -1] = ClipPixel(p0 + f2);
                    dst[dstOffset + strideB * 0] = ClipPixel(q0 - f1);
                }
                else
                {
                    int f = Clip(3 * (q0 - p0), -128, 127);
                    int f1 = Math.Min(f + 4, 127) >> 3;
                    int f2 = Math.Min(f + 3, 127) >> 3;
                    dst[dstOffset + strideB * -1] = ClipPixel(p0 + f2);
                    dst[dstOffset + strideB * 0] = ClipPixel(q0 - f1);
                    f = (f1 + 1) >> 1;
                    dst[dstOffset + strideB * -2] = ClipPixel(p1 + f);
                    dst[dstOffset + strideB * 1] = ClipPixel(q1 - f);
                }
            }
            if (dbg)
            {
                int newP1 = dst[dstOffset + strideB * -2];
                int newP0 = dst[dstOffset + strideB * -1];
                int newQ0 = dst[dstOffset + strideB * 0];
                int newQ1 = dst[dstOffset + strideB * 1];
                AvDbg.W($"[DB-POST] off={dstOffset} p1:{p1:x2}->{newP1:x2} p0:{p0:x2}->{newP0:x2} q0:{q0:x2}->{newQ0:x2} q1:{q1:x2}->{newQ1:x2}");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip(int v, int min, int max) => v < min ? min : v > max ? max : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ========================================================================
    // SB128 Filter Dispatchers (loopfilter_tmpl.c)
    // 4 variants: {H,V} x {Y, UV}
    // ========================================================================

    /// <summary>
    /// Horizontal luma filter for one SB128 column. Filters column edges (block1 | block2).
    /// vmask[3] bitmask: vmask[0]=4-wide, vmask[1]=8-wide, vmask[2]=16-wide.
    /// l[y][4] = loop filter levels per 4x4 block.
    /// </summary>
    public static void LoopFilterHSb128Y(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<uint> vmask, byte[,] level, int levelOffset, int b4Stride,
        Av1FilterLut lut, int h)
    {
        uint vm = vmask[0] | vmask[1] | vmask[2];
        for (uint y = 1; (vm & ~(y - 1)) != 0; y <<= 1, dstOffset += 4 * stride, levelOffset += b4Stride)
        {
            if ((vm & y) == 0) continue;
            int L = level[levelOffset, 0] != 0 ? level[levelOffset, 0] : level[levelOffset - 1, 0];
            if (L == 0) continue;
            int H = L >> 4;
            int E2 = lut.E[L], I2 = lut.I[L];
            int idx = (vmask[2] & y) != 0 ? 2 : ((vmask[1] & y) != 0 ? 1 : 0);
            int wd = 4 << idx;
            bool dbgTarget = (dstOffset == 1060); // Pixel (36,16) = col 9, row 4
            bool dbgTarget8 = (dstOffset == 1056); // Column 8 at row 16 (p0 at col 31, q0 at col 32)
            if (dbgTarget || dbgTarget8)
            {
                int dbgP2pre = dst[dstOffset - 3], dbgP1pre = dst[dstOffset - 2];
                int dbgP0pre = dst[dstOffset - 1], dbgQ0pre = dst[dstOffset];
                int dbgQ1pre = dst[dstOffset + 1], dbgQ2pre = dst[dstOffset + 2];
                LoopFilterEdge(dst, dstOffset, E2, I2, H, stride, 1, wd);
                int dbgP2 = dst[dstOffset - 3], dbgP1 = dst[dstOffset - 2];
                int dbgP0 = dst[dstOffset - 1], dbgQ0 = dst[dstOffset];
                int dbgQ1 = dst[dstOffset + 1], dbgQ2 = dst[dstOffset + 2];
                string colTag = dbgTarget ? "9" : dbgTarget8 ? "8" : "?";
                AvDbg.W($"[DBG-EDGE] col={colTag} pre p2={dbgP2pre} p1={dbgP1pre} p0={dbgP0pre} q0={dbgQ0pre} q1={dbgQ1pre} q2={dbgQ2pre}  post p0={dbgP0} q0={dbgQ0}  L={L} E={E2} I={I2} H={H} wd={wd} y={y}");
            }
            else
            {
                LoopFilterEdge(dst, dstOffset, E2, I2, H, stride, 1, wd);
            }
        }
    }

    /// <summary>
    /// Vertical luma filter for one SB128 column. Filters row edges (top/bottom).
    /// </summary>
    public static void LoopFilterVSb128Y(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<uint> vmask, byte[,] level, int levelOffset, int b4Stride,
        Av1FilterLut lut, int w)
    {
        uint vm = vmask[0] | vmask[1] | vmask[2];
        for (uint x = 1; (vm & ~(x - 1)) != 0; x <<= 1, dstOffset += 4, levelOffset++)
        {
            if ((vm & x) == 0) continue;
            int L = level[levelOffset, 1] != 0 ? level[levelOffset, 1] : level[levelOffset - b4Stride, 1];
            if (L == 0) continue;
            int H = L >> 4;
            int E2 = lut.E[L], I2 = lut.I[L];
            int idx = (vmask[2] & x) != 0 ? 2 : ((vmask[1] & x) != 0 ? 1 : 0);
            int wd = 4 << idx;
            bool dbgTarget = (dstOffset == 1060); // Pixel (36,16)
            int dbgP2pre = 0, dbgP1pre = 0, dbgP0pre = 0, dbgQ0pre = 0, dbgQ1pre = 0, dbgQ2pre = 0;
            if (dbgTarget)
            {
                // Row filter: strideB = stride, strideA = 1
                dbgP2pre = dst[dstOffset + stride * -3]; dbgP1pre = dst[dstOffset + stride * -2];
                dbgP0pre = dst[dstOffset + stride * -1]; dbgQ0pre = dst[dstOffset + stride * 0];
                dbgQ1pre = dst[dstOffset + stride * 1]; dbgQ2pre = dst[dstOffset + stride * 2];
            }
            LoopFilterEdge(dst, dstOffset, E2, I2, H, 1, stride, wd);
            if (dbgTarget)
            {
                int dbgP2 = dst[dstOffset + stride * -3], dbgP1 = dst[dstOffset + stride * -2];
                int dbgP0 = dst[dstOffset + stride * -1], dbgQ0 = dst[dstOffset + stride * 0];
                int dbgQ1 = dst[dstOffset + stride * 1], dbgQ2 = dst[dstOffset + stride * 2];
                AvDbg.W($"[DBG-ROW-FILT] pre p2={dbgP2pre} p1={dbgP1pre} p0={dbgP0pre} q0={dbgQ0pre} q1={dbgQ1pre} q2={dbgQ2pre}  post p2={dbgP2} p1={dbgP1} p0={dbgP0} q0={dbgQ0} q1={dbgQ1} q2={dbgQ2}  L={L} E={E2} I={I2} H={H} wd={wd} x={x} vm={vm:X8}");
            }
        }
    }

    /// <summary>
    /// Horizontal chroma filter for one SB128 column.
    /// </summary>
    public static void LoopFilterHSb128Uv(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<uint> vmask, byte[,] level, int levelOffset, int levelPlane,
        int b4Stride, Av1FilterLut lut, int h)
    {
        uint vm = vmask[0] | vmask[1];
        for (uint y = 1; (vm & ~(y - 1)) != 0; y <<= 1, dstOffset += 4 * stride, levelOffset += b4Stride)
        {
            if ((vm & y) == 0) continue;
            int L = level[levelOffset, levelPlane] != 0 ? level[levelOffset, levelPlane] : level[levelOffset - 1, levelPlane];
            if (L == 0) continue;
            int H = L >> 4;
            int E2 = lut.E[L], I2 = lut.I[L];
            int idx = (vmask[1] & y) != 0 ? 1 : 0;
            LoopFilterEdge(dst, dstOffset, E2, I2, H, stride, 1, 4 + 2 * idx);
        }
    }

    /// <summary>
    /// Vertical chroma filter for one SB128 column.
    /// </summary>
    public static void LoopFilterVSb128Uv(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<uint> vmask, byte[,] level, int levelOffset, int levelPlane,
        int b4Stride, Av1FilterLut lut, int w)
    {
        uint vm = vmask[0] | vmask[1];
        for (uint x = 1; (vm & ~(x - 1)) != 0; x <<= 1, dstOffset += 4, levelOffset++)
        {
            if ((vm & x) == 0) continue;
            int L = level[levelOffset, levelPlane] != 0 ? level[levelOffset, levelPlane] : level[levelOffset - b4Stride, levelPlane];
            if (L == 0) continue;
            int H = L >> 4;
            int E2 = lut.E[L], I2 = lut.I[L];
            int idx = (vmask[1] & x) != 0 ? 1 : 0;
            LoopFilterEdge(dst, dstOffset, E2, I2, H, 1, stride, 4 + 2 * idx);
        }
    }

    // ========================================================================
    // E/I/H Calculation (lf_mask.c: dav1d_calc_eih)
    // ========================================================================

    /// <summary>
    /// Compute E/I/H lookup tables from filter sharpness level.
    /// </summary>
    public static void CalcEih(Av1FilterLut lut, int filterSharpness)
    {
        int sharp = filterSharpness;
        for (int level = 0; level < 64; level++)
        {
            int limit = level;
            if (sharp > 0)
            {
                limit >>= (sharp + 3) >> 2;
                limit = Math.Min(limit, 9 - sharp);
            }
            limit = Math.Max(limit, 1);
            lut.I[level] = (byte)limit;
            lut.E[level] = (byte)(2 * (level + 2) + limit);
        }
        lut.Sharp0 = (sharp + 3) >> 2;
        lut.Sharp1 = sharp != 0 ? 9 - sharp : 0xff;
    }

    // ========================================================================
    // Loop Filter Level Calculation (lf_mask.c: dav1d_calc_lf_values)
    // ========================================================================

    /// <summary>
    /// Compute per-segment, per-plane, per-ref/mode loop filter levels.
    /// lflvlValues[segment][plane 0-3][ref 0-7][mode 0-1].
    /// plane 0 = Y vertical, 1 = Y horizontal, 2 = U, 3 = V.
    /// </summary>
    public static void CalcLfValues(byte[,,,] lflvlValues,
        Av1DecoderFrameHeader fh, ReadOnlySpan<sbyte> lfDelta)
    {
        int nSeg = fh.SegmentationEnabled ? 8 : 1;

        if (fh.LfLevelY0 == 0 && fh.LfLevelY1 == 0)
        {
            Array.Clear(lflvlValues);
            return;
        }

        bool useMrDeltas = fh.LfModeRefDeltaEnabled;

        for (int s = 0; s < nSeg; s++)
        {
            int segDeltaYV = 0, segDeltaYH = 0, segDeltaU = 0, segDeltaV = 0;
            if (fh.SegmentationEnabled)
            {
                var seg = fh.SegmentationData.Segments[s];
                segDeltaYV = seg.DeltaLfYV;
                segDeltaYH = seg.DeltaLfYH;
                segDeltaU = seg.DeltaLfU;
                segDeltaV = seg.DeltaLfV;
            }

            CalcLfValue(lflvlValues, s, 0, fh.LfLevelY0, lfDelta[0], segDeltaYV,
                useMrDeltas ? fh.LfModeRefDeltas : default, useMrDeltas);
            CalcLfValue(lflvlValues, s, 1, fh.LfLevelY1,
                lfDelta[fh.DeltaLfMulti ? 1 : 0], segDeltaYH,
                useMrDeltas ? fh.LfModeRefDeltas : default, useMrDeltas);
            CalcLfValueChroma(lflvlValues, s, 2, fh.LfLevelU,
                lfDelta[fh.DeltaLfMulti ? 2 : 0], segDeltaU,
                useMrDeltas ? fh.LfModeRefDeltas : default, useMrDeltas);
            CalcLfValueChroma(lflvlValues, s, 3, fh.LfLevelV,
                lfDelta[fh.DeltaLfMulti ? 3 : 0], segDeltaV,
                useMrDeltas ? fh.LfModeRefDeltas : default, useMrDeltas);
        }
    }

    private static void CalcLfValue(byte[,,,] values, int seg, int plane,
        int baseLvl, int lfDelta, int segDelta,
        Av1LoopfilterModeRefDeltas mrDelta, bool useMrDelta)
    {
        int @base = Math.Clamp(Math.Clamp(baseLvl + lfDelta, 0, 63) + segDelta, 0, 63);

        if (!useMrDelta)
        {
            for (int r = 0; r < 8; r++)
                values[seg, plane, r, 0] = values[seg, plane, r, 1] = (byte)@base;
        }
        else
        {
            int sh = @base >= 32 ? 1 : 0;
            int intraLvl = Math.Clamp(@base + (mrDelta.GetRefDelta(0) << sh), 0, 63);
            values[seg, plane, 0, 0] = values[seg, plane, 0, 1] = (byte)intraLvl;
            for (int r = 1; r < 8; r++)
            {
                for (int m = 0; m < 2; m++)
                {
                    int delta = mrDelta.GetModeDelta(m) + mrDelta.GetRefDelta(r);
                    values[seg, plane, r, m] = (byte)Math.Clamp(@base + (delta << sh), 0, 63);
                }
            }
        }
    }

    private static void CalcLfValueChroma(byte[,,,] values, int seg, int plane,
        int baseLvl, int lfDelta, int segDelta,
        Av1LoopfilterModeRefDeltas mrDelta, bool useMrDelta)
    {
        if (baseLvl == 0)
        {
            for (int r = 0; r < 8; r++)
                values[seg, plane, r, 0] = values[seg, plane, r, 1] = 0;
        }
        else
        {
            CalcLfValue(values, seg, plane, baseLvl, lfDelta, segDelta, mrDelta, useMrDelta);
        }
    }

    // ========================================================================
    // Mask Generation (lf_mask.c)
    // ========================================================================

    /// <summary>
    /// Decompose transform tree into per-edge filter sizes.
    /// txa[edge 0=left,1=top][attr 0=txSz, 1=step][y][x].
    /// </summary>
    private static void DecompTx(byte[,,,] txa, int edgeBase, int yBase, int xBase,
        int from, int depth, int yOff, int xOff, ReadOnlySpan<ushort> txMasks)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[from];
        bool isSplit = from != (int)Av1TxSize.Tx4x4 && depth <= 1 &&
                       ((txMasks[depth] >> (yOff * 4 + xOff)) & 1) != 0;

        if (isSplit)
        {
            int sub = tDim.Sub;
            int htw4 = tDim.W >> 1, hth4 = tDim.H >> 1;

            DecompTx(txa, edgeBase, yBase, xBase, sub, depth + 1, yOff * 2, xOff * 2, txMasks);
            if (tDim.W >= tDim.H)
                DecompTx(txa, edgeBase, yBase, xBase + htw4, sub, depth + 1, yOff * 2, xOff * 2 + 1, txMasks);
            if (tDim.H >= tDim.W)
            {
                DecompTx(txa, edgeBase, yBase + hth4, xBase, sub, depth + 1, yOff * 2 + 1, xOff * 2, txMasks);
                if (tDim.W >= tDim.H)
                    DecompTx(txa, edgeBase, yBase + hth4, xBase + htw4, sub, depth + 1, yOff * 2 + 1, xOff * 2 + 1, txMasks);
            }
        }
        else
        {
            int lw = Math.Min(2, (int)tDim.Lw), lh = Math.Min(2, (int)tDim.Lh);
            for (int y = 0; y < tDim.H; y++)
            {
                // Set left-edge tx size
                for (int x = 0; x < tDim.W; x++)
                    txa[0, 0, yBase + y, xBase + x] = (byte)lw;
                // Set top-edge tx size
                for (int x = 0; x < tDim.W; x++)
                    txa[1, 0, yBase + y, xBase + x] = (byte)lh;
                // Step for left edge
                txa[0, 1, yBase + y, xBase] = tDim.W;
            }
            // Step for top edge
            for (int x = 0; x < tDim.W; x++)
                txa[1, 1, yBase, xBase + x] = tDim.H;
        }
    }

    /// <summary>
    /// Build loop filter bitmasks for an inter block. Maps to dav1d mask_edges_inter.
    /// masks[direction][position][strength][half].
    /// </summary>
    public static void MaskEdgesInter(Av1FilterMask lflvl, int by4, int bx4,
        int w4, int h4, bool skip, int maxTx, ReadOnlySpan<ushort> txMasks,
        Span<byte> above, Span<byte> left)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[maxTx];

        // Build txa: [2 edges][2 attrs][32][32]
        var txa = new byte[2, 2, 32, 32];
        for (int yOff = 0, y = 0; y < h4; y += tDim.H, yOff++)
            for (int xOff = 0, x = 0; x < w4; x += tDim.W, xOff++)
                DecompTx(txa, 0, y, x, maxTx, 0, yOff, xOff, txMasks);

        // Left block edge
        uint mask = 1u << by4;
        for (int y = 0; y < h4; y++, mask <<= 1)
        {
            int sidx = mask >= 0x10000u ? 1 : 0;
            uint smask = mask >> (sidx << 4);
            int str = Math.Min(txa[0, 0, y, 0], left[y]);
            lflvl.FilterY[0, bx4, str, sidx] |= (ushort)smask;
        }

        // Top block edge
        mask = 1u << bx4;
        for (int x = 0; x < w4; x++, mask <<= 1)
        {
            int sidx = mask >= 0x10000u ? 1 : 0;
            uint smask = mask >> (sidx << 4);
            int str = Math.Min(txa[1, 0, 0, x], above[x]);
            lflvl.FilterY[1, by4, str, sidx] |= (ushort)smask;
        }

        if (!skip)
        {
            // Inner (tx) left|right edges
            for (int y = 0, m2 = 1 << by4; y < h4; y++, m2 <<= 1)
            {
                int sidx = (uint)m2 >= 0x10000u ? 1 : 0;
                uint smask = (uint)m2 >> (sidx << 4);
                int ltx = txa[0, 0, y, 0];
                int step = txa[0, 1, y, 0];
                for (int x = step; x < w4; x += step)
                {
                    int rtx = txa[0, 0, y, x];
                    lflvl.FilterY[0, bx4 + x, Math.Min(rtx, ltx), sidx] |= (ushort)smask;
                    ltx = rtx;
                    step = txa[0, 1, y, x];
                    if (step == 0) break;
                }
            }

            // Inner (tx) top/bottom edges
            for (int x = 0, m2 = 1 << bx4; x < w4; x++, m2 <<= 1)
            {
                int sidx = (uint)m2 >= 0x10000u ? 1 : 0;
                uint smask = (uint)m2 >> (sidx << 4);
                int ttx = txa[1, 0, 0, x];
                int step = txa[1, 1, 0, x];
                for (int y = step; y < h4; y += step)
                {
                    int btx = txa[1, 0, y, x];
                    lflvl.FilterY[1, by4 + y, Math.Min(ttx, btx), sidx] |= (ushort)smask;
                    ttx = btx;
                    step = txa[1, 1, y, x];
                    if (step == 0) break;
                }
            }
        }

        // Update context arrays
        for (int y = 0; y < h4; y++)
            left[y] = txa[0, 0, y, w4 - 1];
        for (int x = 0; x < w4; x++)
            above[x] = txa[1, 0, h4 - 1, x];
    }

    /// <summary>
    /// Build loop filter bitmasks for an intra block. Maps to dav1d mask_edges_intra.
    /// </summary>
    public static void MaskEdgesIntra(Av1FilterMask lflvl, int by4, int bx4,
        int w4, int h4, int tx, Span<byte> above, Span<byte> left)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
        int twl4c = Math.Min(2, (int)tDim.Lw), thl4c = Math.Min(2, (int)tDim.Lh);

        // Left block edge
        uint mask = 1u << by4;
        for (int y = 0; y < h4; y++, mask <<= 1)
        {
            int sidx = mask >= 0x10000u ? 1 : 0;
            uint smask = mask >> (sidx << 4);
            lflvl.FilterY[0, bx4, Math.Min(twl4c, left[y]), sidx] |= (ushort)smask;
        }

        // Top block edge
        mask = 1u << bx4;
        for (int x = 0; x < w4; x++, mask <<= 1)
        {
            int sidx = mask >= 0x10000u ? 1 : 0;
            uint smask = mask >> (sidx << 4);
            lflvl.FilterY[1, by4, Math.Min(thl4c, above[x]), sidx] |= (ushort)smask;
        }

        // Inner (tx) left|right edges
        int hstep = tDim.W;
        uint t = 1u << by4;
        uint inner = (uint)((((ulong)t) << h4) - t);
        ushort inner1 = (ushort)(inner & 0xffff), inner2 = (ushort)(inner >> 16);
        for (int x = hstep; x < w4; x += hstep)
        {
            if (inner1 != 0) lflvl.FilterY[0, bx4 + x, twl4c, 0] |= inner1;
            if (inner2 != 0) lflvl.FilterY[0, bx4 + x, twl4c, 1] |= inner2;
        }

        // Inner (tx) top/bottom edges
        int vstep = tDim.H;
        t = 1u << bx4;
        inner = (uint)((((ulong)t) << w4) - t);
        inner1 = (ushort)(inner & 0xffff); inner2 = (ushort)(inner >> 16);
        for (int y = vstep; y < h4; y += vstep)
        {
            if (inner1 != 0) lflvl.FilterY[1, by4 + y, thl4c, 0] |= inner1;
            if (inner2 != 0) lflvl.FilterY[1, by4 + y, thl4c, 1] |= inner2;
        }

        // Update context
        for (int x = 0; x < w4; x++) above[x] = (byte)thl4c;
        for (int y = 0; y < h4; y++) left[y] = (byte)twl4c;
    }

    /// <summary>
    /// Build chroma loop filter bitmasks. Maps to dav1d mask_edges_chroma.
    /// </summary>
    public static void MaskEdgesChroma(Av1FilterMask lflvl, int cby4, int cbx4,
        int cw4, int ch4, bool skipInter, int tx, Span<byte> above, Span<byte> left,
        int ssHor, int ssVer)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
        int twl4c = tDim.Lw != 0 ? 1 : 0;
        int thl4c = tDim.Lh != 0 ? 1 : 0;
        int vbits = 4 - ssVer, hbits = 4 - ssHor;
        uint vmask2 = 1u << (16 >> ssVer), hmask2 = 1u << (16 >> ssHor);

        // Left block edge
        uint mask = 1u << cby4;
        for (int y = 0; y < ch4; y++, mask <<= 1)
        {
            int sidx = mask >= vmask2 ? 1 : 0;
            uint smask = mask >> (sidx << vbits);
            lflvl.FilterUv[0, cbx4, Math.Min(twl4c, left[y]), sidx] |= (ushort)smask;
        }

        // Top block edge
        mask = 1u << cbx4;
        for (int x = 0; x < cw4; x++, mask <<= 1)
        {
            int sidx = mask >= hmask2 ? 1 : 0;
            uint smask = mask >> (sidx << hbits);
            lflvl.FilterUv[1, cby4, Math.Min(thl4c, above[x]), sidx] |= (ushort)smask;
        }

        if (!skipInter)
        {
            // Inner (tx) left|right edges
            int hstep = tDim.W;
            uint t = 1u << cby4;
            uint inner = (uint)((((ulong)t) << ch4) - t);
            ushort inner1 = (ushort)(inner & ((1 << (16 >> ssVer)) - 1));
            ushort inner2 = (ushort)(inner >> (16 >> ssVer));
            for (int x = hstep; x < cw4; x += hstep)
            {
                if (inner1 != 0) lflvl.FilterUv[0, cbx4 + x, twl4c, 0] |= inner1;
                if (inner2 != 0) lflvl.FilterUv[0, cbx4 + x, twl4c, 1] |= inner2;
            }

            // Inner (tx) top/bottom edges
            int vstep = tDim.H;
            t = 1u << cbx4;
            inner = (uint)((((ulong)t) << cw4) - t);
            inner1 = (ushort)(inner & ((1 << (16 >> ssHor)) - 1));
            inner2 = (ushort)(inner >> (16 >> ssHor));
            for (int y = vstep; y < ch4; y += vstep)
            {
                if (inner1 != 0) lflvl.FilterUv[1, cby4 + y, thl4c, 0] |= inner1;
                if (inner2 != 0) lflvl.FilterUv[1, cby4 + y, thl4c, 1] |= inner2;
            }
        }

        // Update context
        for (int x = 0; x < cw4; x++) above[x] = (byte)thl4c;
        for (int y = 0; y < ch4; y++) left[y] = (byte)twl4c;
    }

    // ========================================================================
    // Mask Creation Entry Points (lf_mask.c: dav1d_create_lf_mask_intra/inter)
    // ========================================================================

    /// <summary>
    /// Create loop filter mask for an intra block.
    /// </summary>
    public static void CreateLfMaskIntra(Av1FilterMask lflvl, byte[,] levelCache,
        int b4Stride, byte[,,,] filterLevel, int segId,
        int bx, int by, int iw, int ih,
        Av1BlockSize bs, int ytx, int uvtx, int layout,
        byte[] ay, int ayOff, byte[] ly, int lyOff,
        byte[]? auv, int auvOff, byte[]? luv, int luvOff)
    {
        bool hasChroma = auv != null;
        int bw4 = Math.Min(iw - bx, Av1BlockSizeHelper.GetWidth4(bs));
        int bh4 = Math.Min(ih - by, Av1BlockSizeHelper.GetHeight4(bs));
        int bx4 = bx & 31;
        int by4 = by & 31;

        if (bw4 > 0 && bh4 > 0)
        {
            // Fill level cache for luma
            AvDbg.W($"[LF-MASK] bx={bx} by={by} segId={segId} bw4={bw4} bh4={bh4} ytx={ytx} lvlY0={filterLevel[segId, 0, 0, 0]} lvlY1={filterLevel[segId, 1, 0, 0]}");
            for (int y = 0; y < bh4; y++)
                for (int x = 0; x < bw4; x++)
                {
                    int idx = (by + y) * b4Stride + bx + x;
                    if (idx >= 0 && idx < levelCache.GetLength(0))
                    {
                        levelCache[idx, 0] = filterLevel[segId, 0, 0, 0]; // col dir, intra, no-mode
                        levelCache[idx, 1] = filterLevel[segId, 1, 0, 0]; // row dir, intra, no-mode
                    }
                }

            MaskEdgesIntra(lflvl, by4, bx4, bw4, bh4, ytx,
                new Span<byte>(ay, ayOff, ay.Length - ayOff),
                new Span<byte>(ly, lyOff, ly.Length - lyOff));
        }

        if (!hasChroma) return;

        int ssVer = layout == (int)Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = layout != (int)Av1PixelLayout.I444 ? 1 : 0;
        int cbw4 = Math.Min(((iw + ssHor) >> ssHor) - (bx >> ssHor),
            (Av1BlockSizeHelper.GetWidth4(bs) + ssHor) >> ssHor);
        int cbh4 = Math.Min(((ih + ssVer) >> ssVer) - (by >> ssVer),
            (Av1BlockSizeHelper.GetHeight4(bs) + ssVer) >> ssVer);

        if (cbw4 <= 0 || cbh4 <= 0) return;

        int cbx4 = bx4 >> ssHor;
        int cby4 = by4 >> ssVer;

        // Fill level cache for chroma
        for (int y = 0; y < cbh4; y++)
            for (int x = 0; x < cbw4; x++)
            {
                int idx = ((by >> ssVer) + y) * b4Stride + (bx >> ssHor) + x;
                if (idx >= 0 && idx < levelCache.GetLength(0))
                {
                    levelCache[idx, 2] = filterLevel[segId, 2, 0, 0];
                    levelCache[idx, 3] = filterLevel[segId, 3, 0, 0];
                }
            }

        MaskEdgesChroma(lflvl, cby4, cbx4, cbw4, cbh4, false, uvtx,
            new Span<byte>(auv!, auvOff, auv!.Length - auvOff),
            new Span<byte>(luv!, luvOff, luv!.Length - luvOff),
            ssHor, ssVer);
    }

    /// <summary>
    /// Create loop filter mask for an inter block.
    /// filterLevel is LfLvl[seg][plane][ref][mode]; the per-block slice is
    /// filterLevel[segId][plane][refIdx][modeIdx] where refIdx = ref0 + 1 and
    /// modeIdx = !is_globalmv (dav1d decode.c:1933-1934).
    /// </summary>
    public static void CreateLfMaskInter(Av1FilterMask lflvl, byte[,] levelCache,
        int b4Stride, byte[,,,] filterLevel, int segId, int refIdx, int modeIdx,
        int bx, int by, int iw, int ih,
        bool skip, Av1BlockSize bs, int maxYtx, ReadOnlySpan<ushort> txMasks,
        int uvtx, Av1PixelLayout layout,
        Span<byte> ay, Span<byte> ly, Span<byte> auv, Span<byte> luv, bool hasChroma)
    {
        int bw4 = Math.Min(iw - bx, Av1BlockSizeHelper.GetWidth4(bs));
        int bh4 = Math.Min(ih - by, Av1BlockSizeHelper.GetHeight4(bs));
        int bx4 = bx & 31;
        int by4 = by & 31;

        if (bw4 > 0 && bh4 > 0)
        {
            for (int y = 0; y < bh4; y++)
                for (int x = 0; x < bw4; x++)
                {
                    int idx = (by + y) * b4Stride + bx + x;
                    if (idx >= 0 && idx < levelCache.GetLength(0))
                    {
                        levelCache[idx, 0] = filterLevel[segId, 0, refIdx, modeIdx];
                        levelCache[idx, 1] = filterLevel[segId, 1, refIdx, modeIdx];
                    }
                }

            MaskEdgesInter(lflvl, by4, bx4, bw4, bh4, skip, maxYtx, txMasks, ay, ly);
        }

        if (!hasChroma) return;

        int ssVer = layout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = layout != Av1PixelLayout.I444 ? 1 : 0;
        int cbw4 = Math.Min(((iw + ssHor) >> ssHor) - (bx >> ssHor),
            (Av1BlockSizeHelper.GetWidth4(bs) + ssHor) >> ssHor);
        int cbh4 = Math.Min(((ih + ssVer) >> ssVer) - (by >> ssVer),
            (Av1BlockSizeHelper.GetHeight4(bs) + ssVer) >> ssVer);

        if (cbw4 <= 0 || cbh4 <= 0) return;

        int cbx4 = bx4 >> ssHor;
        int cby4 = by4 >> ssVer;

        for (int y = 0; y < cbh4; y++)
            for (int x = 0; x < cbw4; x++)
            {
                int idx = ((by >> ssVer) + y) * b4Stride + (bx >> ssHor) + x;
                if (idx >= 0 && idx < levelCache.GetLength(0))
                {
                    levelCache[idx, 2] = filterLevel[segId, 2, refIdx, modeIdx];
                    levelCache[idx, 3] = filterLevel[segId, 3, refIdx, modeIdx];
                }
            }

        MaskEdgesChroma(lflvl, cby4, cbx4, cbw4, cbh4, skip, uvtx, auv, luv, ssHor, ssVer);
    }

    // ========================================================================
    // Per-SB-Row Application (lf_apply_tmpl.c)
    // ========================================================================

    /// <summary>
    /// Filter luma columns for a plane within one SB128.
    /// </summary>
    public static void FilterPlaneColsY(byte[,] level, int levelOffset, int b4Stride,
        Av1FilterMask lflvl, int maskIdx, Span<byte> dst, int dstOffset, int stride,
        int w, int starty4, int endy4, Av1FilterLut lut, bool haveLeft)
    {
        // Dump mask for first SB128 column at sby=0
        if (DumpEdges && dstOffset < 128 && starty4 == 0 && !haveLeft && maskIdx == 0)
        {
            using var sw = new System.IO.StreamWriter(@"C:\Users\adamm\AppData\Local\Temp\ours_lf_mask.txt", true);
            sw.WriteLine($"cols dstOff={dstOffset} w={w} starty4={starty4} endy4={endy4}");
            for (int col = 0; col < 32; col++)
            {
                sw.Write($"  col{col:D2}:");
                for (int s = 0; s < 3; s++)
                    sw.Write($" [s{s}]={lflvl.FilterY[0, col + maskIdx, s, 0]:X4}/{lflvl.FilterY[0, col + maskIdx, s, 1]:X4}");
                sw.WriteLine();
            }
            sw.Write("  levels first 8 cols: ");
            for (int col = 0; col < 8; col++)
                sw.Write($" {level[levelOffset + col, 0]}");
            sw.WriteLine();
        }
        for (int x = 0; x < w; x++)
        {
            if (!haveLeft && x == 0) continue;
            Span<uint> hmask = stackalloc uint[4];
            if (starty4 == 0)
            {
                hmask[0] = lflvl.FilterY[0, maskIdx + x, 0, 0];
                hmask[1] = lflvl.FilterY[0, maskIdx + x, 1, 0];
                hmask[2] = lflvl.FilterY[0, maskIdx + x, 2, 0];
                if (endy4 > 16)
                {
                    hmask[0] |= (uint)lflvl.FilterY[0, maskIdx + x, 0, 1] << 16;
                    hmask[1] |= (uint)lflvl.FilterY[0, maskIdx + x, 1, 1] << 16;
                    hmask[2] |= (uint)lflvl.FilterY[0, maskIdx + x, 2, 1] << 16;
                }
            }
            else
            {
                hmask[0] = lflvl.FilterY[0, maskIdx + x, 0, 1];
                hmask[1] = lflvl.FilterY[0, maskIdx + x, 1, 1];
                hmask[2] = lflvl.FilterY[0, maskIdx + x, 2, 1];
            }
            hmask[3] = 0;

            // Debug: dump pre/post for first edge
            AvDbg.W($"[FILTER-COLS-Y] x={x} haveLeft={haveLeft} w={w} starty4={starty4} endy4={endy4} hmask=[{hmask[0]:X8},{hmask[1]:X8},{hmask[2]:X8}]");
            LoopFilterHSb128Y(dst, dstOffset + x * 4, stride, hmask,
                level, levelOffset + x, b4Stride, lut, endy4 - starty4);
        }
    }

    /// <summary>
    /// Filter luma rows for a plane within one SB128.
    /// </summary>
    public static void FilterPlaneRowsY(byte[,] level, int levelOffset, int b4Stride,
        Av1FilterMask lflvl, int maskIdx, Span<byte> dst, int dstOffset, int stride,
        int w, int starty4, int endy4, Av1FilterLut lut, bool haveTop)
    {
        int off = dstOffset;
        int lvl = levelOffset;
        for (int y = starty4; y < endy4; y++, off += 4 * stride, lvl += b4Stride)
        {
            if (!haveTop && y == 0) continue;
            Span<uint> vmask = stackalloc uint[4];
            vmask[0] = (uint)(lflvl.FilterY[1, y, 0, 0] | ((uint)lflvl.FilterY[1, y, 0, 1] << 16));
            vmask[1] = (uint)(lflvl.FilterY[1, y, 1, 0] | ((uint)lflvl.FilterY[1, y, 1, 1] << 16));
            vmask[2] = (uint)(lflvl.FilterY[1, y, 2, 0] | ((uint)lflvl.FilterY[1, y, 2, 1] << 16));
            vmask[3] = 0;

            LoopFilterVSb128Y(dst, off, stride, vmask,
                level, lvl, b4Stride, lut, w);
        }
    }

    /// <summary>
    /// Filter chroma columns for one SB128.
    /// </summary>
    public static void FilterPlaneColsUv(byte[,] level, int levelOffset, int b4Stride,
        Av1FilterMask lflvl, int maskIdx, Span<byte> dstU, int uOffset,
        Span<byte> dstV, int vOffset, int stride,
        int w, int starty4, int endy4, int ssVer, Av1FilterLut lut, bool haveLeft)
    {
        for (int x = 0; x < w; x++)
        {
            if (!haveLeft && x == 0) continue;
            Span<uint> hmask = stackalloc uint[3];
            if (starty4 == 0)
            {
                hmask[0] = lflvl.FilterUv[0, maskIdx + x, 0, 0];
                hmask[1] = lflvl.FilterUv[0, maskIdx + x, 1, 0];
                if (endy4 > (16 >> ssVer))
                {
                    hmask[0] |= (uint)lflvl.FilterUv[0, maskIdx + x, 0, 1] << (16 >> ssVer);
                    hmask[1] |= (uint)lflvl.FilterUv[0, maskIdx + x, 1, 1] << (16 >> ssVer);
                }
            }
            else
            {
                hmask[0] = lflvl.FilterUv[0, maskIdx + x, 0, 1];
                hmask[1] = lflvl.FilterUv[0, maskIdx + x, 1, 1];
            }
            hmask[2] = 0;

            LoopFilterHSb128Uv(dstU, uOffset + x * 4, stride, hmask, level, levelOffset + x, 2, b4Stride, lut, endy4 - starty4);
            LoopFilterHSb128Uv(dstV, vOffset + x * 4, stride, hmask, level, levelOffset + x, 3, b4Stride, lut, endy4 - starty4);
        }
    }

    /// <summary>
    /// Filter chroma rows for one SB128.
    /// </summary>
    public static void FilterPlaneRowsUv(byte[,] level, int levelOffset, int b4Stride,
        Av1FilterMask lflvl, int maskIdx, Span<byte> dstU, int uOffset,
        Span<byte> dstV, int vOffset, int stride,
        int w, int starty4, int endy4, int ssHor, Av1FilterLut lut, bool haveTop)
    {
        int offU = uOffset, offV = vOffset;
        int lvl = levelOffset;
        for (int y = starty4; y < endy4; y++, offU += 4 * stride, offV += 4 * stride, lvl += b4Stride)
        {
            if (!haveTop && y == 0) continue;
            Span<uint> vmask = stackalloc uint[3];
            vmask[0] = (uint)(lflvl.FilterUv[1, y, 0, 0] | ((uint)lflvl.FilterUv[1, y, 0, 1] << (16 >> ssHor)));
            vmask[1] = (uint)(lflvl.FilterUv[1, y, 1, 0] | ((uint)lflvl.FilterUv[1, y, 1, 1] << (16 >> ssHor)));
            vmask[2] = 0;

            LoopFilterVSb128Uv(dstU, offU, stride, vmask, level, lvl, 2, b4Stride, lut, w);
            LoopFilterVSb128Uv(dstV, offV, stride, vmask, level, lvl, 3, b4Stride, lut, w);
        }
    }

    /// <summary>
    /// Apply loop filter column pass for one SB row. Maps to dav1d_loopfilter_sbrow_cols.
    /// </summary>
    public static void LoopFilterSbRowCols(Av1DecoderContext ctx, Span<byte> yPlane,
        Span<byte> uPlane, Span<byte> vPlane, int yOffset, int uOffset, int vOffset,
        Av1FilterMask[] lflvl, int sby, bool startOfTileRow)
    {
        ref readonly var fh = ref ctx.FrameHeader;
        ref readonly var sh = ref ctx.SequenceHeader;
        int isSb64 = sh.Sb128 ? 0 : 1;
        int starty4 = (sby & isSb64) << 4;
        int sbsz = 32 >> isSb64;
        int endy4 = starty4 + Math.Min(ctx.H4 - sby * sbsz, sbsz);
        int ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int uvEndy4 = (endy4 + ssVer) >> ssVer;

        // TODO: fix lpf strength at tile col/row boundaries (requires tx_lpf_right_edge)

        // Filter luma columns
        int yOff = yOffset;
        int levelOff = sby * sbsz * ctx.B4Stride;
        bool haveLeft = false;
        for (int x = 0; x < ctx.Sb128W; x++, haveLeft = true, yOff += 128)
        {
            FilterPlaneColsY(ctx.LfLevel, levelOff + x * 32, ctx.B4Stride,
                lflvl[x], 0, yPlane, yOff, ctx.YStride,
                Math.Min(32, ctx.W4 - x * 32), starty4, endy4, ctx.LfLimLut, haveLeft);
        }

        if (fh.LfLevelU == 0 && fh.LfLevelV == 0) return;

        // Filter chroma columns
        int uvOff = uOffset;
        int uvLvlOff = sby * (sbsz >> ssVer) * ctx.B4Stride;
        haveLeft = false;
        for (int x = 0; x < ctx.Sb128W; x++, haveLeft = true,
             uvOff += 128 >> ssHor)
        {
            FilterPlaneColsUv(ctx.LfLevel, uvLvlOff + (x * 32 >> ssHor), ctx.B4Stride,
                lflvl[x], 0, uPlane, uvOff, vPlane, uvOff, ctx.UvStride,
                (Math.Min(32, ctx.W4 - x * 32) + ssHor) >> ssHor,
                starty4 >> ssVer, uvEndy4, ssVer, ctx.LfLimLut, haveLeft);
        }
    }

    /// <summary>
    /// Apply loop filter row pass for one SB row. Maps to dav1d_loopfilter_sbrow_rows.
    /// </summary>
    public static void LoopFilterSbRowRows(Av1DecoderContext ctx, Span<byte> yPlane,
        Span<byte> uPlane, Span<byte> vPlane, int yOffset, int uOffset, int vOffset,
        Av1FilterMask[] lflvl, int sby)
    {
        ref readonly var fh = ref ctx.FrameHeader;
        ref readonly var sh = ref ctx.SequenceHeader;
        bool haveTop = sby > 0;
        int isSb64 = sh.Sb128 ? 0 : 1;
        int starty4 = (sby & isSb64) << 4;
        int sbsz = 32 >> isSb64;
        int endy4 = starty4 + Math.Min(ctx.H4 - sby * sbsz, sbsz);
        int ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int uvEndy4 = (endy4 + ssVer) >> ssVer;

        // Filter luma rows
        int yOff = yOffset;
        int levelOff = sby * sbsz * ctx.B4Stride;
        for (int x = 0; x < ctx.Sb128W; x++, yOff += 128)
        {
            FilterPlaneRowsY(ctx.LfLevel, levelOff + x * 32, ctx.B4Stride,
                lflvl[x], 0, yPlane, yOff, ctx.YStride,
                Math.Min(32, ctx.W4 - x * 32), starty4, endy4, ctx.LfLimLut, haveTop);
        }

        if (fh.LfLevelU == 0 && fh.LfLevelV == 0) return;

        // Filter chroma rows
        int uvOff = uOffset;
        int uvLvlOff = sby * (sbsz >> ssVer) * ctx.B4Stride;
        for (int x = 0; x < ctx.Sb128W; x++,
             uvOff += 128 >> ssHor)
        {
            FilterPlaneRowsUv(ctx.LfLevel, uvLvlOff + (x * 32 >> ssHor), ctx.B4Stride,
                lflvl[x], 0, uPlane, uvOff, vPlane, uvOff, ctx.UvStride,
                (Math.Min(32, ctx.W4 - x * 32) + ssHor) >> ssHor,
                starty4 >> ssVer, uvEndy4, ssHor, ctx.LfLimLut, haveTop);
        }
    }
}
