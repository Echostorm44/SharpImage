// AV1 Loop Restoration Filter
// Ported from dav1d looprestoration_tmpl.c (Wiener + SGR filter kernels) and
// lr_apply_tmpl.c (per-SB-row application). All 8-bit.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 Loop Restoration — Wiener filter and Self-Guided Restoration (SGR).
/// </summary>
public static class Av1LoopRestoration
{
    // ========================================================================
    // Edge Flags (looprestoration.h: LrEdgeFlags)
    // ========================================================================

    [Flags]
    public enum LrEdgeFlags
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Top = 1 << 2,
        Bottom = 1 << 3,
    }

    // ========================================================================
    // Constants
    // ========================================================================

    private const int RestUnitStride = 390; // 256 * 1.5 + 3 + 3
    private const int BufStride = 400; // 384 + 16
    private const int FilterOutStride = 384;

    // ========================================================================
    // SGR Params (tables.c: dav1d_sgr_params)
    // ========================================================================

    public static readonly ushort[,] SgrParams = new ushort[16, 2]
    {
        { 140, 3236 }, { 112, 2158 }, {  93, 1618 }, {  80, 1438 },
        {  70, 1295 }, {  58, 1177 }, {  47, 1079 }, {  37,  996 },
        {  30,  925 }, {  25,  863 }, {   0, 2589 }, {   0, 1618 },
        {   0, 1177 }, {   0,  925 }, {  56,    0 }, {  22,    0 },
    };

    // SGR x_by_x lookup table (tables.c: dav1d_sgr_x_by_x)
    private static readonly byte[] SgrXByX = new byte[256]
    {
        255, 128,  85,  64,  51,  43,  37,  32,  28,  26,  23,  21,  20,  18,  17,
         16,  15,  14,  13,  13,  12,  12,  11,  11,  10,  10,   9,   9,   9,   9,
          8,   8,   8,   8,   7,   7,   7,   7,   7,   6,   6,   6,   6,   6,   6,
          6,   5,   5,   5,   5,   5,   5,   5,   5,   5,   5,   4,   4,   4,   4,
          4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   3,   3,
          3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,
          3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          0
    };

    // ========================================================================
    // Wiener Filter — Horizontal pass
    // ========================================================================

    /// <summary>
    /// Wiener horizontal filter: produces 16-bit intermediates from 8-bit pixels.
    /// For 8-bit: sum starts with (1 &lt;&lt; 14), adds src[x]*128, then 7-tap filter.
    /// Round with 3 bits, clip to [0, 2048).
    /// </summary>
    private static void WienerFilterH(Span<ushort> dst, ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> src, int srcOffset, ReadOnlySpan<short> fh,
        int w, LrEdgeFlags edges)
    {
        const int bitdepth = 8;
        const int roundBitsH = 3;
        const int roundingOffH = 1 << (roundBitsH - 1);
        const int clipLimit = 1 << (bitdepth + 1 + 7 - roundBitsH);

        int start = 3;
        if ((edges & LrEdgeFlags.Left) == 0)
        {
            for (int x = 0; x < 3; x++)
            {
                int sum = (1 << (bitdepth + 6)) + src[srcOffset + x] * 128;
                for (int i = 0; i < 7; i++)
                {
                    int idx = x + i - 3;
                    sum += (idx < 0 ? src[srcOffset] : src[srcOffset + idx]) * fh[i];
                }
                dst[x] = (ushort)Math.Clamp((sum + roundingOffH) >> roundBitsH, 0, clipLimit - 1);
            }
        }
        else if (left.Length > 0)
        {
            for (int x = 0; x < 3; x++)
            {
                int sum = (1 << (bitdepth + 6)) + src[srcOffset + x] * 128;
                for (int i = 0; i < 7; i++)
                {
                    int idx = x + i - 3;
                    sum += (idx < 0 ? left[4 + idx] : src[srcOffset + idx]) * fh[i];
                }
                dst[x] = (ushort)Math.Clamp((sum + roundingOffH) >> roundBitsH, 0, clipLimit - 1);
            }
        }
        else
        {
            start = 0;
        }

        int end = (edges & LrEdgeFlags.Right) != 0 ? w : w - 3;

        for (int x = start; x < end; x++)
        {
            int sum = (1 << (bitdepth + 6)) + src[srcOffset + x] * 128;
            for (int i = 0; i < 7; i++)
                sum += src[srcOffset + x + i - 3] * fh[i];
            dst[x] = (ushort)Math.Clamp((sum + roundingOffH) >> roundBitsH, 0, clipLimit - 1);
        }

        for (int x = end; x < w; x++)
        {
            int sum = (1 << (bitdepth + 6)) + src[srcOffset + x] * 128;
            for (int i = 0; i < 7; i++)
            {
                int idx = x + i - 3;
                sum += (idx >= w ? src[srcOffset + w - 1] : src[srcOffset + idx]) * fh[i];
            }
            dst[x] = (ushort)Math.Clamp((sum + roundingOffH) >> roundBitsH, 0, clipLimit - 1);
        }
    }

    // ========================================================================
    // Wiener Filter — Combined H+V pass
    // ========================================================================

    private static void WienerFilterHV(Span<byte> p, int pOffset, int stride,
        ushort[][] ptrs, ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> src, int srcOffset,
        ReadOnlySpan<short> fh, ReadOnlySpan<short> fv,
        int w, LrEdgeFlags edges)
    {
        const int bitdepth = 8;
        const int roundBitsV = 11;
        const int roundingOffV = 1 << (roundBitsV - 1);
        const int roundOffset = 1 << (bitdepth + (roundBitsV - 1));

        Span<ushort> tmp = stackalloc ushort[RestUnitStride];
        WienerFilterH(tmp, left, src, srcOffset, fh, w, edges);

        for (int i = 0; i < w; i++)
        {
            int sum = -roundOffset;
            for (int k = 0; k < 6; k++)
                sum += ptrs[k][i] * fv[k];
            sum += tmp[i] * fv[6];
            p[pOffset + i] = (byte)Math.Clamp((sum + roundingOffV) >> roundBitsV, 0, 255);
        }

        // Copy new row into ptrs[6], rotate down
        Array.Copy(tmp.ToArray(), ptrs[6], RestUnitStride);
        for (int i = 0; i < 6; i++)
            ptrs[i] = ptrs[i + 1];
        ptrs[6] = ptrs[0];
    }

    private static void WienerFilterV(Span<byte> p, int pOffset, ushort[][] ptrs,
        ReadOnlySpan<short> fv, int w)
    {
        const int bitdepth = 8;
        const int roundBitsV = 11;
        const int roundingOffV = 1 << (roundBitsV - 1);
        const int roundOffset = 1 << (bitdepth + (roundBitsV - 1));

        for (int i = 0; i < w; i++)
        {
            int sum = -roundOffset;
            for (int k = 0; k < 6; k++)
                sum += ptrs[k][i] * fv[k];
            sum += ptrs[5][i] * fv[6]; // 7th row = last row duplicated
            p[pOffset + i] = (byte)Math.Clamp((sum + roundingOffV) >> roundBitsV, 0, 255);
        }

        for (int i = 0; i < 5; i++)
            ptrs[i] = ptrs[i + 1];
    }

    // ========================================================================
    // Wiener Entry Point (looprestoration_tmpl.c: wiener_c)
    // ========================================================================

    /// <summary>
    /// Apply Wiener separable filter to a restoration unit.
    /// </summary>
    public static void Wiener(Span<byte> p, int pOffset, int stride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> lpf, int lpfOffset,
        int w, int h, ReadOnlySpan<short> filterH, ReadOnlySpan<short> filterV,
        LrEdgeFlags edges)
    {
        var rows = new ushort[6][];
        for (int i = 0; i < 6; i++)
            rows[i] = new ushort[RestUnitStride];
        var ptrs = new ushort[8][];

        int srcOff = pOffset;
        int lpfOff = lpfOffset;
        int lpfBottomOff = lpfOffset + 6 * stride;

        if ((edges & LrEdgeFlags.Top) != 0)
        {
            ptrs[0] = rows[0]; ptrs[1] = rows[0]; ptrs[2] = rows[1];
            ptrs[3] = rows[2]; ptrs[4] = rows[2]; ptrs[5] = rows[2];

            WienerFilterH(rows[0], ReadOnlySpan<byte>.Empty, lpf, lpfOff, filterH, w, edges);
            lpfOff += stride;
            WienerFilterH(rows[1], ReadOnlySpan<byte>.Empty, lpf, lpfOff, filterH, w, edges);

            WienerFilterH(rows[2], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 1); return; }

            ptrs[4] = ptrs[5] = rows[3];
            WienerFilterH(rows[3], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 2); return; }

            ptrs[5] = rows[4];
            WienerFilterH(rows[4], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 3); return; }
        }
        else
        {
            ptrs[0] = rows[0]; ptrs[1] = rows[0]; ptrs[2] = rows[0];
            ptrs[3] = rows[0]; ptrs[4] = rows[0]; ptrs[5] = rows[0];

            WienerFilterH(rows[0], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 1); return; }

            ptrs[4] = ptrs[5] = rows[1];
            WienerFilterH(rows[1], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 2); return; }

            ptrs[5] = rows[2];
            WienerFilterH(rows[2], left.Slice(leftOffset, 4), p, srcOff, filterH, w, edges);
            leftOffset += leftStride;
            srcOff += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 3); return; }

            ptrs[6] = rows[3];
            WienerFilterHV(p, pOffset, stride, ptrs,
                left.Slice(leftOffset, 4), p, srcOff, filterH, filterV, w, edges);
            leftOffset += leftStride;
            srcOff += stride;
            pOffset += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 3); return; }

            ptrs[6] = rows[4];
            WienerFilterHV(p, pOffset, stride, ptrs,
                left.Slice(leftOffset, 4), p, srcOff, filterH, filterV, w, edges);
            leftOffset += leftStride;
            srcOff += stride;
            pOffset += stride;

            if (--h <= 0) { WienerVTail(p, pOffset, stride, ptrs, filterV, w, 3); return; }
        }

        ptrs[6] = ptrs[5]; // Will be overwritten per row
        // Allocate a rotating row buffer
        var extraRow = new ushort[RestUnitStride];
        ptrs[6] = extraRow;

        do
        {
            WienerFilterHV(p, pOffset, stride, ptrs,
                left.Slice(leftOffset, 4), p, srcOff, filterH, filterV, w, edges);
            leftOffset += leftStride;
            srcOff += stride;
            pOffset += stride;
        } while (--h > 0);

        if ((edges & LrEdgeFlags.Bottom) == 0)
        {
            WienerVTail(p, pOffset, stride, ptrs, filterV, w, 3);
            return;
        }

        WienerFilterHV(p, pOffset, stride, ptrs,
            ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, filterH, filterV, w, edges);
        lpfBottomOff += stride;
        pOffset += stride;

        WienerFilterHV(p, pOffset, stride, ptrs,
            ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, filterH, filterV, w, edges);
        pOffset += stride;

        // v1: final single-row V filter
        WienerFilterV(p, pOffset, ptrs, filterV, w);
    }

    private static void WienerVTail(Span<byte> p, int pOffset, int stride,
        ushort[][] ptrs, ReadOnlySpan<short> fv, int w, int pendingRows)
    {
        // pendingRows: 3 → v3 → v2 → v1
        //              2 → v2 → v1
        //              1 → v1
        if (pendingRows >= 3)
        {
            WienerFilterV(p, pOffset, ptrs, fv, w);
            pOffset += stride;
        }
        if (pendingRows >= 2)
        {
            WienerFilterV(p, pOffset, ptrs, fv, w);
            pOffset += stride;
        }
        WienerFilterV(p, pOffset, ptrs, fv, w);
    }

    // ========================================================================
    // SGR Box Filters
    // ========================================================================

    private static void SgrBox3RowH(int[] sumsq, int[] sum, int offset,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> src, int srcOffset,
        int w, LrEdgeFlags edges)
    {
        int off = offset + 1; // sumsq++; sum++;
        bool haveLeft = (edges & LrEdgeFlags.Left) != 0;
        bool haveRight = (edges & LrEdgeFlags.Right) != 0;

        int a = haveLeft ? (left.Length > 0 ? left[2] : src[srcOffset - 2]) : src[srcOffset];
        int b = haveLeft ? (left.Length > 0 ? left[3] : src[srcOffset - 1]) : src[srcOffset];

        for (int x = -1; x < w + 1; x++)
        {
            int c = (x + 1 < w || haveRight) ? src[srcOffset + x + 1] : src[srcOffset + w - 1];
            sum[off + x] = a + b + c;
            sumsq[off + x] = a * a + b * b + c * c;
            a = b;
            b = c;
        }
    }

    private static void SgrBox5RowH(int[] sumsq, int[] sum, int offset,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> src, int srcOffset,
        int w, LrEdgeFlags edges)
    {
        int off = offset + 1;
        bool haveLeft = (edges & LrEdgeFlags.Left) != 0;
        bool haveRight = (edges & LrEdgeFlags.Right) != 0;

        int a = haveLeft ? (left.Length > 0 ? left[1] : src[srcOffset - 3]) : src[srcOffset];
        int b = haveLeft ? (left.Length > 0 ? left[2] : src[srcOffset - 2]) : src[srcOffset];
        int c = haveLeft ? (left.Length > 0 ? left[3] : src[srcOffset - 1]) : src[srcOffset];
        int d = src[srcOffset];

        for (int x = -1; x < w + 1; x++)
        {
            int e = (x + 2 < w || haveRight) ? src[srcOffset + x + 2] : src[srcOffset + w - 1];
            sum[off + x] = a + b + c + d + e;
            sumsq[off + x] = a * a + b * b + c * c + d * d + e * e;
            a = b; b = c; c = d; d = e;
        }
    }

    private static void SgrBox3RowV(int[][] sumsq, int[][] sumPtrs,
        int[] sumsqOut, int[] sumOut, int offset, int w)
    {
        for (int x = 0; x < w + 2; x++)
        {
            sumsqOut[offset + x] = sumsq[0][offset + x] + sumsq[1][offset + x] + sumsq[2][offset + x];
            sumOut[offset + x] = sumPtrs[0][offset + x] + sumPtrs[1][offset + x] + sumPtrs[2][offset + x];
        }
    }

    private static void SgrBox5RowV(int[][] sumsq, int[][] sumPtrs,
        int[] sumsqOut, int[] sumOut, int offset, int w)
    {
        for (int x = 0; x < w + 2; x++)
        {
            sumsqOut[offset + x] = sumsq[0][offset + x] + sumsq[1][offset + x] +
                sumsq[2][offset + x] + sumsq[3][offset + x] + sumsq[4][offset + x];
            sumOut[offset + x] = sumPtrs[0][offset + x] + sumPtrs[1][offset + x] +
                sumPtrs[2][offset + x] + sumPtrs[3][offset + x] + sumPtrs[4][offset + x];
        }
    }

    private static void SgrCalcRowAB(int[] AA, int[] BB, int offset, int w,
        int s, int n, int sgrOneByX)
    {
        // For 8-bit: bitdepth_min_8 = 0, so rounding terms simplify
        for (int i = 0; i < w + 2; i++)
        {
            int a = AA[offset + i];
            int b = BB[offset + i];
            int p = Math.Max(a * n - b * b, 0);
            int z = (int)(((uint)p * (uint)s + (1 << 19)) >> 20);
            int x = SgrXByX[Math.Min(z, 255)];

            BB[offset + i] = x;
            AA[offset + i] = (x * b * sgrOneByX + (1 << 11)) >> 12;
        }
    }

    // ========================================================================
    // SGR Box Vert + CalcAB combined
    // ========================================================================

    private static void SgrBox3Vert(int[][] sumsq, int[][] sumPtrs,
        int[] sumsqOut, int[] sumOut, int offset, int w, int s)
    {
        SgrBox3RowV(sumsq, sumPtrs, sumsqOut, sumOut, offset, w);
        SgrCalcRowAB(sumsqOut, sumOut, offset, w, s, 9, 455);
        Rotate(sumsq, sumPtrs, 3);
    }

    private static void SgrBox5Vert(int[][] sumsq, int[][] sumPtrs,
        int[] sumsqOut, int[] sumOut, int offset, int w, int s)
    {
        SgrBox5RowV(sumsq, sumPtrs, sumsqOut, sumOut, offset, w);
        SgrCalcRowAB(sumsqOut, sumOut, offset, w, s, 25, 164);
        Rotate5x2(sumsq, sumPtrs);
    }

    private static void SgrBox3HV(int[][] sumsq, int[][] sumPtrs,
        int[] AA, int[] BB, int offset,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> src, int srcOffset,
        int w, int s, LrEdgeFlags edges)
    {
        SgrBox3RowH(sumsq[2], sumPtrs[2], offset, left, src, srcOffset, w, edges);
        SgrBox3Vert(sumsq, sumPtrs, AA, BB, offset, w, s);
    }

    // ========================================================================
    // SGR Finish Filter
    // ========================================================================

    private static void SgrFinishFilterRow1(int[] tmp, int tmpOffset,
        ReadOnlySpan<byte> src, int srcOffset, int[][] aPtrs, int[][] bPtrs,
        int offset, int w)
    {
        for (int i = 0; i < w; i++)
        {
            int idx = offset + i + 1;
            int a = (bPtrs[1][idx] + bPtrs[1][idx - 1] + bPtrs[1][idx + 1] +
                     bPtrs[0][idx] + bPtrs[2][idx]) * 4 +
                    (bPtrs[0][idx - 1] + bPtrs[2][idx - 1] +
                     bPtrs[0][idx + 1] + bPtrs[2][idx + 1]) * 3;
            int b = (aPtrs[1][idx] + aPtrs[1][idx - 1] + aPtrs[1][idx + 1] +
                     aPtrs[0][idx] + aPtrs[2][idx]) * 4 +
                    (aPtrs[0][idx - 1] + aPtrs[2][idx - 1] +
                     aPtrs[0][idx + 1] + aPtrs[2][idx + 1]) * 3;
            tmp[tmpOffset + i] = (b - a * src[srcOffset + i] + (1 << 8)) >> 9;
        }
    }

    private static void SgrFinishFilter2(int[] tmp, int tmpOffset,
        ReadOnlySpan<byte> src, int srcOffset, int srcStride,
        int[][] aPtrs, int[][] bPtrs, int offset, int w, int h)
    {
        for (int i = 0; i < w; i++)
        {
            int idx = offset + i + 1;
            int a = (bPtrs[0][idx] + bPtrs[1][idx]) * 6 +
                    (bPtrs[0][idx - 1] + bPtrs[1][idx - 1] +
                     bPtrs[0][idx + 1] + bPtrs[1][idx + 1]) * 5;
            int b = (aPtrs[0][idx] + aPtrs[1][idx]) * 6 +
                    (aPtrs[0][idx - 1] + aPtrs[1][idx - 1] +
                     aPtrs[0][idx + 1] + aPtrs[1][idx + 1]) * 5;
            tmp[tmpOffset + i] = (b - a * src[srcOffset + i] + (1 << 8)) >> 9;
        }
        if (h <= 1) return;

        int tmpOff2 = tmpOffset + FilterOutStride;
        int srcOff2 = srcOffset + srcStride;
        for (int i = 0; i < w; i++)
        {
            int idx = offset + i + 1;
            int a = bPtrs[1][idx] * 6 + (bPtrs[1][idx - 1] + bPtrs[1][idx + 1]) * 5;
            int b = aPtrs[1][idx] * 6 + (aPtrs[1][idx - 1] + aPtrs[1][idx + 1]) * 5;
            tmp[tmpOff2 + i] = (b - a * src[srcOff2 + i] + (1 << 7)) >> 8;
        }
    }

    private static void SgrWeightedRow1(Span<byte> dst, int dstOffset,
        int[] t, int tOffset, int w, int w1)
    {
        for (int i = 0; i < w; i++)
        {
            int v = w1 * t[tOffset + i];
            dst[dstOffset + i] = (byte)Math.Clamp(
                dst[dstOffset + i] + ((v + (1 << 10)) >> 11), 0, 255);
        }
    }

    private static void SgrWeighted2(Span<byte> dst, int dstOffset, int dstStride,
        int[] t1, int t1Offset, int[] t2, int t2Offset,
        int w, int h, int w0, int w1)
    {
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int v = w0 * t1[t1Offset + i] + w1 * t2[t2Offset + i];
                dst[dstOffset + i] = (byte)Math.Clamp(
                    dst[dstOffset + i] + ((v + (1 << 10)) >> 11), 0, 255);
            }
            dstOffset += dstStride;
            t1Offset += FilterOutStride;
            t2Offset += FilterOutStride;
        }
    }

    // ========================================================================
    // SGR 3×3 Entry Point (looprestoration_tmpl.c: sgr_3x3_c)
    // ========================================================================

    public static void Sgr3x3(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> lpf, int lpfOffset,
        int w, int h, int s1, int w1, LrEdgeFlags edges)
    {
        var sumsqRows = new int[3][];
        var sumRows = new int[3][];
        for (int i = 0; i < 3; i++)
        {
            sumsqRows[i] = new int[BufStride];
            sumRows[i] = new int[BufStride];
        }
        var sumsqPtrs = new int[3][];
        var sumPtrs = new int[3][];

        var aBuf = new int[3][];
        var bBuf = new int[3][];
        for (int i = 0; i < 3; i++)
        {
            aBuf[i] = new int[BufStride];
            bBuf[i] = new int[BufStride];
        }
        var aPtrs = new int[3][];
        var bPtrs = new int[3][];
        for (int i = 0; i < 3; i++) { aPtrs[i] = aBuf[i]; bPtrs[i] = bBuf[i]; }

        int srcOff = dstOffset;
        int pOff = dstOffset;
        int lpfOff = lpfOffset;
        int lpfBottomOff = lpfOffset + 6 * stride;
        int lOff = leftOffset;

        if ((edges & LrEdgeFlags.Top) != 0)
        {
            sumsqPtrs[0] = sumsqRows[0]; sumsqPtrs[1] = sumsqRows[1]; sumsqPtrs[2] = sumsqRows[2];
            sumPtrs[0] = sumRows[0]; sumPtrs[1] = sumRows[1]; sumPtrs[2] = sumRows[2];

            SgrBox3RowH(sumsqRows[0], sumRows[0], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);
            lpfOff += stride;
            SgrBox3RowH(sumsqRows[1], sumRows[1], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);

            SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, s1, edges);
            lOff += leftStride; srcOff += stride;
            Rotate(aPtrs, bPtrs, 3);

            if (--h <= 0) goto vert_1;

            SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, s1, edges);
            lOff += leftStride; srcOff += stride;
            Rotate(aPtrs, bPtrs, 3);

            if (--h <= 0) goto vert_2;
        }
        else
        {
            sumsqPtrs[0] = sumsqRows[0]; sumsqPtrs[1] = sumsqRows[0]; sumsqPtrs[2] = sumsqRows[0];
            sumPtrs[0] = sumRows[0]; sumPtrs[1] = sumRows[0]; sumPtrs[2] = sumRows[0];

            SgrBox3RowH(sumsqRows[0], sumRows[0], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox3Vert(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0, w, s1);
            Rotate(aPtrs, bPtrs, 3);

            if (--h <= 0) goto vert_1;

            sumsqPtrs[2] = sumsqRows[1]; sumPtrs[2] = sumRows[1];

            SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, s1, edges);
            lOff += leftStride; srcOff += stride;
            Rotate(aPtrs, bPtrs, 3);

            if (--h <= 0) goto vert_2;

            sumsqPtrs[2] = sumsqRows[2]; sumPtrs[2] = sumRows[2];
        }

        // Main loop
        do
        {
            SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, s1, edges);
            lOff += leftStride; srcOff += stride;

            // sgr_finish1
            Span<int> tmpRow = stackalloc int[384];
            int[] tmpArr = new int[384];
            SgrFinishFilterRow1(tmpArr, 0, dst, pOff, aPtrs, bPtrs, 0, w);
            SgrWeightedRow1(dst, pOff, tmpArr, 0, w, w1);
            pOff += stride;
            Rotate(aPtrs, bPtrs, 3);
        } while (--h > 0);

        if ((edges & LrEdgeFlags.Bottom) == 0) goto vert_2;

        SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
            ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, w, s1, edges);
        lpfBottomOff += stride;
        SgrFinish1(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, w1);

        SgrBox3HV(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0,
            ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, w, s1, edges);
        SgrFinish1(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, w1);
        return;

    vert_2:
        sumsqPtrs[2] = sumsqPtrs[1]; sumPtrs[2] = sumPtrs[1];
        SgrBox3Vert(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0, w, s1);
        SgrFinish1(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, w1);

        // output_1:
        sumsqPtrs[2] = sumsqPtrs[1]; sumPtrs[2] = sumPtrs[1];
        SgrBox3Vert(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0, w, s1);
        SgrFinish1(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, w1);
        return;

    vert_1:
        sumsqPtrs[2] = sumsqPtrs[1]; sumPtrs[2] = sumPtrs[1];
        SgrBox3Vert(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0, w, s1);
        Rotate(aPtrs, bPtrs, 3);
        // goto output_1:
        sumsqPtrs[2] = sumsqPtrs[1]; sumPtrs[2] = sumPtrs[1];
        SgrBox3Vert(sumsqPtrs, sumPtrs, aPtrs[2], bPtrs[2], 0, w, s1);
        SgrFinish1(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, w1);
    }

    private static void SgrFinish1(Span<byte> dst, ref int pOff, int stride,
        int[][] aPtrs, int[][] bPtrs, int offset, int w, int w1)
    {
        var tmp = new int[384];
        SgrFinishFilterRow1(tmp, 0, dst, pOff, aPtrs, bPtrs, offset, w);
        SgrWeightedRow1(dst, pOff, tmp, 0, w, w1);
        pOff += stride;
        Rotate(aPtrs, bPtrs, 3);
    }

    // ========================================================================
    // SGR 5×5 Entry Point (looprestoration_tmpl.c: sgr_5x5_c)
    // ========================================================================

    public static void Sgr5x5(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> lpf, int lpfOffset,
        int w, int h, int s0, int w0, LrEdgeFlags edges)
    {
        var sumsqRows = new int[5][];
        var sumRows = new int[5][];
        for (int i = 0; i < 5; i++)
        {
            sumsqRows[i] = new int[BufStride];
            sumRows[i] = new int[BufStride];
        }
        var sumsqPtrs = new int[5][];
        var sumPtrs = new int[5][];

        var aBuf = new int[2][];
        var bBuf = new int[2][];
        for (int i = 0; i < 2; i++)
        {
            aBuf[i] = new int[BufStride];
            bBuf[i] = new int[BufStride];
        }
        var aPtrs = new int[2][];
        var bPtrs = new int[2][];
        for (int i = 0; i < 2; i++) { aPtrs[i] = aBuf[i]; bPtrs[i] = bBuf[i]; }

        int srcOff = dstOffset;
        int pOff = dstOffset;
        int lpfOff = lpfOffset;
        int lpfBottomOff = lpfOffset + 6 * stride;
        int lOff = leftOffset;

        if ((edges & LrEdgeFlags.Top) != 0)
        {
            sumsqPtrs[0] = sumsqRows[0]; sumsqPtrs[1] = sumsqRows[0];
            sumsqPtrs[2] = sumsqRows[1]; sumsqPtrs[3] = sumsqRows[2];
            sumsqPtrs[4] = sumsqRows[3];
            sumPtrs[0] = sumRows[0]; sumPtrs[1] = sumRows[0];
            sumPtrs[2] = sumRows[1]; sumPtrs[3] = sumRows[2];
            sumPtrs[4] = sumRows[3];

            SgrBox5RowH(sumsqRows[0], sumRows[0], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);
            lpfOff += stride;
            SgrBox5RowH(sumsqRows[1], sumRows[1], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);
            SgrBox5RowH(sumsqRows[2], sumRows[2], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            if (--h <= 0) goto vert_1;

            SgrBox5RowH(sumsqRows[3], sumRows[3], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;
            SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
            Rotate(aPtrs, bPtrs, 2);

            if (--h <= 0) goto vert_2;

            sumsqPtrs[3] = sumsqRows[4]; sumPtrs[3] = sumRows[4];
        }
        else
        {
            for (int i = 0; i < 5; i++) { sumsqPtrs[i] = sumsqRows[0]; sumPtrs[i] = sumRows[0]; }

            SgrBox5RowH(sumsqRows[0], sumRows[0], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            if (--h <= 0) goto vert_1;

            sumsqPtrs[4] = sumsqRows[1]; sumPtrs[4] = sumRows[1];
            SgrBox5RowH(sumsqRows[1], sumRows[1], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;
            SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
            Rotate(aPtrs, bPtrs, 2);

            if (--h <= 0) goto vert_2;

            sumsqPtrs[3] = sumsqRows[2]; sumsqPtrs[4] = sumsqRows[3];
            sumPtrs[3] = sumRows[2]; sumPtrs[4] = sumRows[3];

            SgrBox5RowH(sumsqRows[2], sumRows[2], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            if (--h <= 0) goto odd;

            SgrBox5RowH(sumsqRows[3], sumRows[3], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;
            SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
            SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 2, w0);

            if (--h <= 0) goto vert_2;

            sumsqPtrs[3] = sumsqRows[4]; sumPtrs[3] = sumRows[4];
        }

        // Main loop
        do
        {
            SgrBox5RowH(sumsqPtrs[3], sumPtrs[3], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            if (--h <= 0) goto odd;

            SgrBox5RowH(sumsqPtrs[4], sumPtrs[4], 0, left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
            SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 2, w0);
        } while (--h > 0);

        if ((edges & LrEdgeFlags.Bottom) == 0) goto vert_2;

        SgrBox5RowH(sumsqPtrs[3], sumPtrs[3], 0, ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, w, edges);
        lpfBottomOff += stride;
        SgrBox5RowH(sumsqPtrs[4], sumPtrs[4], 0, ReadOnlySpan<byte>.Empty, lpf, lpfBottomOff, w, edges);
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 2, w0);
        return;

    vert_2:
        sumsqPtrs[3] = sumsqPtrs[2]; sumsqPtrs[4] = sumsqPtrs[2];
        sumPtrs[3] = sumPtrs[2]; sumPtrs[4] = sumPtrs[2];
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 2, w0);
        return;

    odd:
        sumsqPtrs[4] = sumsqPtrs[3]; sumPtrs[4] = sumPtrs[3];
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 2, w0);

        // output_1:
        sumsqPtrs[3] = sumsqPtrs[2]; sumsqPtrs[4] = sumsqPtrs[2];
        sumPtrs[3] = sumPtrs[2]; sumPtrs[4] = sumPtrs[2];
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 1, w0);
        return;

    vert_1:
        sumsqPtrs[4] = sumsqPtrs[3]; sumPtrs[4] = sumPtrs[3];
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        Rotate(aPtrs, bPtrs, 2);
        // goto output_1:
        sumsqPtrs[3] = sumsqPtrs[2]; sumsqPtrs[4] = sumsqPtrs[2];
        sumPtrs[3] = sumPtrs[2]; sumPtrs[4] = sumPtrs[2];
        SgrBox5Vert(sumsqPtrs, sumPtrs, aPtrs[1], bPtrs[1], 0, w, s0);
        SgrFinish2(dst, ref pOff, stride, aPtrs, bPtrs, 0, w, 1, w0);
    }

    private static void SgrFinish2(Span<byte> dst, ref int pOff, int stride,
        int[][] aPtrs, int[][] bPtrs, int offset, int w, int h, int w0)
    {
        var tmp = new int[2 * FilterOutStride];
        SgrFinishFilter2(tmp, 0, dst, pOff, stride, aPtrs, bPtrs, offset, w, h);
        SgrWeightedRow1(dst, pOff, tmp, 0, w, w0);
        pOff += stride;
        if (h > 1)
        {
            SgrWeightedRow1(dst, pOff, tmp, FilterOutStride, w, w0);
            pOff += stride;
        }
        Rotate(aPtrs, bPtrs, 2);
    }

    // ========================================================================
    // Array Rotation Helpers
    // ========================================================================

    private static void Rotate(int[][] a, int[][] b, int n)
    {
        var tmpA = a[0];
        var tmpB = b[0];
        for (int i = 0; i < n - 1; i++)
        {
            a[i] = a[i + 1];
            b[i] = b[i + 1];
        }
        a[n - 1] = tmpA;
        b[n - 1] = tmpB;
    }

    private static void Rotate5x2(int[][] a, int[][] b)
    {
        var tmpA0 = a[0]; var tmpA1 = a[1];
        var tmpB0 = b[0]; var tmpB1 = b[1];
        for (int i = 0; i < 3; i++)
        {
            a[i] = a[i + 2];
            b[i] = b[i + 2];
        }
        a[3] = tmpA0; a[4] = tmpA1;
        b[3] = tmpB0; b[4] = tmpB1;
    }

    // ========================================================================
    // SgrMix helper — combine box-fill for 3x3+5x5 (dav1d: sgr_box35_row_h)
    // ========================================================================
    private static void SgrBox35RowH(int[] sumsq3, int[] sum3,
        int[] sumsq5, int[] sum5, int offset,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> src, int srcOffset,
        int w, LrEdgeFlags edges)
    {
        SgrBox3RowH(sumsq3, sum3, offset, left, src, srcOffset, w, edges);
        SgrBox5RowH(sumsq5, sum5, offset, left, src, srcOffset, w, edges);
    }

    // ========================================================================
    // SgrFinishMix — finish both guides + weighted mix (dav1d: sgr_finish_mix)
    // ========================================================================
    private static void SgrFinishMix(Span<byte> dst, ref int pOff, int stride,
        int[][] A5Ptrs, int[][] B5Ptrs, int[][] A3Ptrs, int[][] B3Ptrs,
        int offset, int w, int h, int w0, int w1)
    {
        var tmp5 = new int[2 * FilterOutStride];
        var tmp3 = new int[2 * FilterOutStride];

        SgrFinishFilter2(tmp5, 0, dst, pOff, stride, A5Ptrs, B5Ptrs, offset, w, h);
        SgrFinishFilterRow1(tmp3, 0, dst, pOff, A3Ptrs, B3Ptrs, offset, w);
        if (h > 1)
            SgrFinishFilterRow1(tmp3, FilterOutStride, dst, pOff + stride, A3Ptrs, B3Ptrs, offset, w);
        SgrWeighted2(dst, pOff, stride, tmp5, 0, tmp3, 0, w, h, w0, w1);
        pOff += h * stride;
        Rotate(A5Ptrs, B5Ptrs, 2);
        Rotate(A3Ptrs, B3Ptrs, 4);
    }

    // ========================================================================
    // SGR Mix Entry Point (dav1d: sgr_mix_c)
    // Processes both 5x5 (s0) and 3x3 (s1) guides on the same source pixels,
    // then mixes the results with weights w0 (5x5 guide weight) and w1 (3x3).
    // ========================================================================
    public static void SgrMix(Span<byte> dst, int dstOffset, int stride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> lpf, int lpfOffset,
        int w, int h, int s0, int s1, int w0, int w1, LrEdgeFlags edges)
    {
        // 5x5 buffers: 5 rows of sumsq/sum + 2 rows of AB
        var sumsq5Buf = new int[5][];
        var sum5Buf = new int[5][];
        for (int i = 0; i < 5; i++) { sumsq5Buf[i] = new int[BufStride]; sum5Buf[i] = new int[BufStride]; }
        var sumsq5Rows = sumsq5Buf;
        var sum5Rows = sum5Buf;
        var sumsq5Ptrs = new int[5][]; var sum5Ptrs = new int[5][];

        // 3x3 buffers: 3 rows of sumsq/sum + 4 rows of AB
        var sumsq3Buf = new int[3][];
        var sum3Buf = new int[3][];
        for (int i = 0; i < 3; i++) { sumsq3Buf[i] = new int[BufStride]; sum3Buf[i] = new int[BufStride]; }
        var sumsq3Rows = sumsq3Buf;
        var sum3Rows = sum3Buf;
        var sumsq3Ptrs = new int[3][]; var sum3Ptrs = new int[3][];

        // 5x5 AB: 2 rows
        var A5Buf = new int[2][];
        var B5Buf = new int[2][];
        for (int i = 0; i < 2; i++) { A5Buf[i] = new int[BufStride]; B5Buf[i] = new int[BufStride]; }
        var A5Ptrs = new int[2][];
        var B5Ptrs = new int[2][];
        for (int i = 0; i < 2; i++) { A5Ptrs[i] = A5Buf[i]; B5Ptrs[i] = B5Buf[i]; }

        // 3x3 AB: 4 rows
        var A3Buf = new int[4][];
        var B3Buf = new int[4][];
        for (int i = 0; i < 4; i++) { A3Buf[i] = new int[BufStride]; B3Buf[i] = new int[BufStride]; }
        var A3Ptrs = new int[4][];
        var B3Ptrs = new int[4][];
        for (int i = 0; i < 4; i++) { A3Ptrs[i] = A3Buf[i]; B3Ptrs[i] = B3Buf[i]; }

        int srcOff = dstOffset;
        int pOff = dstOffset;
        int lpfOff = lpfOffset;
        ReadOnlySpan<byte> lpfBottom = lpf.Slice(lpfOffset + 6 * stride);
        int lOff = leftOffset;

        if ((edges & LrEdgeFlags.Top) != 0)
        {
            // === Top edge prologue ===
            sumsq5Ptrs[0] = sumsq5Rows[0]; sum5Ptrs[0] = sum5Rows[0];
            sumsq5Ptrs[1] = sumsq5Rows[0]; sum5Ptrs[1] = sum5Rows[0];
            sumsq5Ptrs[2] = sumsq5Rows[1]; sum5Ptrs[2] = sum5Rows[1];
            sumsq5Ptrs[3] = sumsq5Rows[2]; sum5Ptrs[3] = sum5Rows[2];
            sumsq5Ptrs[4] = sumsq5Rows[3]; sum5Ptrs[4] = sum5Rows[3];

            sumsq3Ptrs[0] = sumsq3Rows[0]; sum3Ptrs[0] = sum3Rows[0];
            sumsq3Ptrs[1] = sumsq3Rows[1]; sum3Ptrs[1] = sum3Rows[1];
            sumsq3Ptrs[2] = sumsq3Rows[2]; sum3Ptrs[2] = sum3Rows[2];

            SgrBox35RowH(sumsq3Rows[0], sum3Rows[0],
                sumsq5Rows[0], sum5Rows[0], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);
            lpfOff += stride;
            SgrBox35RowH(sumsq3Rows[1], sum3Rows[1],
                sumsq5Rows[1], sum5Rows[1], 0, ReadOnlySpan<byte>.Empty, lpf, lpfOff, w, edges);

            SgrBox35RowH(sumsq3Rows[2], sum3Rows[2],
                sumsq5Rows[2], sum5Rows[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto vert_1;

            SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
                sumsq5Rows[3], sum5Rows[3], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;
            SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
            Rotate(A5Ptrs, B5Ptrs, 2);
            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto vert_2;

            // After rotate by 2, both [3] and [4] point at rows[0]; fix [3] → rows[4]
            sumsq5Ptrs[3] = sumsq5Rows[4];
            sum5Ptrs[3] = sum5Rows[4];
        }
        else
        {
            // === No top edge prologue ===
            sumsq5Ptrs[0] = sumsq5Rows[0]; sum5Ptrs[0] = sum5Rows[0];
            sumsq5Ptrs[1] = sumsq5Rows[0]; sum5Ptrs[1] = sum5Rows[0];
            sumsq5Ptrs[2] = sumsq5Rows[0]; sum5Ptrs[2] = sum5Rows[0];
            sumsq5Ptrs[3] = sumsq5Rows[0]; sum5Ptrs[3] = sum5Rows[0];
            sumsq5Ptrs[4] = sumsq5Rows[0]; sum5Ptrs[4] = sum5Rows[0];

            sumsq3Ptrs[0] = sumsq3Rows[0]; sum3Ptrs[0] = sum3Rows[0];
            sumsq3Ptrs[1] = sumsq3Rows[0]; sum3Ptrs[1] = sum3Rows[0];
            sumsq3Ptrs[2] = sumsq3Rows[0]; sum3Ptrs[2] = sum3Rows[0];

            SgrBox35RowH(sumsq3Rows[0], sum3Rows[0],
                sumsq5Rows[0], sum5Rows[0], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto vert_1;

            sumsq5Ptrs[4] = sumsq5Rows[1];
            sum5Ptrs[4] = sum5Rows[1];
            sumsq3Ptrs[2] = sumsq3Rows[1];
            sum3Ptrs[2] = sum3Rows[1];

            SgrBox35RowH(sumsq3Rows[1], sum3Rows[1],
                sumsq5Rows[1], sum5Rows[1], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
            Rotate(A5Ptrs, B5Ptrs, 2);
            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto vert_2;

            sumsq5Ptrs[3] = sumsq5Rows[2];
            sumsq5Ptrs[4] = sumsq5Rows[3];
            sum5Ptrs[3] = sum5Rows[2];
            sum5Ptrs[4] = sum5Rows[3];
            sumsq3Ptrs[2] = sumsq3Rows[2];
            sum3Ptrs[2] = sum3Rows[2];

            SgrBox35RowH(sumsq3Rows[2], sum3Rows[2],
                sumsq5Rows[2], sum5Rows[2], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto odd;

            SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
                sumsq5Rows[3], sum5Rows[3], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            SgrFinishMix(dst, ref pOff, stride, A5Ptrs, B5Ptrs, A3Ptrs, B3Ptrs, 0, w, 2, w0, w1);

            if (--h <= 0) goto vert_2;

            // ptrs rotated by 2; [3] and [4] both point at rows[0]; fix [3] → rows[4]
            sumsq5Ptrs[3] = sumsq5Rows[4];
            sum5Ptrs[3] = sum5Rows[4];
        }

        // === Main loop ===
        do
        {
            SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
                sumsq5Ptrs[3], sum5Ptrs[3], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            Rotate(A3Ptrs, B3Ptrs, 4);

            if (--h <= 0) goto odd;

            SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
                sumsq5Ptrs[4], sum5Ptrs[4], 0,
                left.Slice(lOff, 4), dst, srcOff, w, edges);
            lOff += leftStride; srcOff += stride;

            SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
            SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
            SgrFinishMix(dst, ref pOff, stride, A5Ptrs, B5Ptrs, A3Ptrs, B3Ptrs, 0, w, 2, w0, w1);
        } while (--h > 0);

        if ((edges & LrEdgeFlags.Bottom) == 0) goto vert_2;

        // Bottom border: mask out Left/Right since lpf_bottom has no left/right context
        var botEdges = edges & ~(LrEdgeFlags.Left | LrEdgeFlags.Right);

        SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
            sumsq5Ptrs[3], sum5Ptrs[3], 0,
            ReadOnlySpan<byte>.Empty, lpfBottom, 0, w, botEdges);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        Rotate(A3Ptrs, B3Ptrs, 4);

        SgrBox35RowH(sumsq3Ptrs[2], sum3Ptrs[2],
            sumsq5Ptrs[4], sum5Ptrs[4], 0,
            ReadOnlySpan<byte>.Empty, lpfBottom.Slice(stride), 0, w, botEdges);

    // output_2:
        SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        SgrFinishMix(dst, ref pOff, stride, A5Ptrs, B5Ptrs, A3Ptrs, B3Ptrs, 0, w, 2, w0, w1);
        return;

    vert_2:
        // Duplicate last row twice more for 5x5, once more for 3x3
        sumsq5Ptrs[3] = sumsq5Ptrs[2]; sumsq5Ptrs[4] = sumsq5Ptrs[2];
        sum5Ptrs[3] = sum5Ptrs[2]; sum5Ptrs[4] = sum5Ptrs[2];
        sumsq3Ptrs[2] = sumsq3Ptrs[1];
        sum3Ptrs[2] = sum3Ptrs[1];
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        Rotate(A3Ptrs, B3Ptrs, 4);
        sumsq3Ptrs[2] = sumsq3Ptrs[1];
        sum3Ptrs[2] = sum3Ptrs[1];
        goto output_2;

    output_2:
        SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        SgrFinishMix(dst, ref pOff, stride, A5Ptrs, B5Ptrs, A3Ptrs, B3Ptrs, 0, w, 2, w0, w1);
        return;

    odd:
        // Copy last row as padding once
        sumsq5Ptrs[4] = sumsq5Ptrs[3];
        sum5Ptrs[4] = sum5Ptrs[3];
        sumsq3Ptrs[2] = sumsq3Ptrs[1];
        sum3Ptrs[2] = sum3Ptrs[1];

        SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        SgrFinishMix(dst, ref pOff, stride, A5Ptrs, B5Ptrs, A3Ptrs, B3Ptrs, 0, w, 2, w0, w1);

    output_1:
        sumsq5Ptrs[3] = sumsq5Ptrs[2]; sumsq5Ptrs[4] = sumsq5Ptrs[2];
        sum5Ptrs[3] = sum5Ptrs[2]; sum5Ptrs[4] = sum5Ptrs[2];
        sumsq3Ptrs[2] = sumsq3Ptrs[1];
        sum3Ptrs[2] = sum3Ptrs[1];

        SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        Rotate(A3Ptrs, B3Ptrs, 4);
        // output only one row
        {
            var tmp5_2 = new int[2 * FilterOutStride];
            var tmp3_2 = new int[2 * FilterOutStride];
            SgrFinishFilter2(tmp5_2, 0, dst, pOff, stride, A5Ptrs, B5Ptrs, 0, w, 1);
            SgrFinishFilterRow1(tmp3_2, 0, dst, pOff, A3Ptrs, B3Ptrs, 0, w);
            SgrWeighted2(dst, pOff, stride, tmp5_2, 0, tmp3_2, 0, w, 1, w0, w1);
        }
        return;

    vert_1:
        sumsq5Ptrs[4] = sumsq5Ptrs[3];
        sum5Ptrs[4] = sum5Ptrs[3];
        sumsq3Ptrs[2] = sumsq3Ptrs[1];
        sum3Ptrs[2] = sum3Ptrs[1];

        SgrBox5Vert(sumsq5Ptrs, sum5Ptrs, A5Ptrs[1], B5Ptrs[1], 0, w, s0);
        Rotate(A5Ptrs, B5Ptrs, 2);
        SgrBox3Vert(sumsq3Ptrs, sum3Ptrs, A3Ptrs[3], B3Ptrs[3], 0, w, s1);
        Rotate(A3Ptrs, B3Ptrs, 4);

        goto output_1;
    }
}
