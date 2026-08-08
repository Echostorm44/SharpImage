// AV1 CDEF (Constrained Directional Enhancement Filter)
// Ported from dav1d cdef_tmpl.c (filter kernel + direction finding) and
// cdef_apply_tmpl.c (per-SB-row application). All 8-bit.

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 CDEF filter — direction finding, filter kernel, and per-SB-row application.
/// </summary>
public static class Av1Cdef
{
    // ========================================================================
    // Edge flags (cdef.h: CdefEdgeFlags)
    // ========================================================================

    [Flags]
    public enum EdgeFlags
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Top = 1 << 2,
        Bottom = 1 << 3,
        All = Left | Right | Top | Bottom
    }

    // ========================================================================
    // Direction offsets (tables.c: dav1d_cdef_directions)
    // Indexed as [dir + 2][pass], 12 entries (dir -2 to dir+9 wrapped).
    // Offsets are relative to tmp buffer with stride 12.
    // ========================================================================

    private static readonly sbyte[,] Directions = new sbyte[12, 2]
    {
        {  1 * 12 + 0,  2 * 12 + 0 }, // dir 6 (wrap-around for dir-2)
        {  1 * 12 + 0,  2 * 12 - 1 }, // dir 7
        { -1 * 12 + 1, -2 * 12 + 2 }, // dir 0
        {  0 * 12 + 1, -1 * 12 + 2 }, // dir 1
        {  0 * 12 + 1,  0 * 12 + 2 }, // dir 2
        {  0 * 12 + 1,  1 * 12 + 2 }, // dir 3
        {  1 * 12 + 1,  2 * 12 + 2 }, // dir 4
        {  1 * 12 + 0,  2 * 12 + 1 }, // dir 5
        {  1 * 12 + 0,  2 * 12 + 0 }, // dir 6
        {  1 * 12 + 0,  2 * 12 - 1 }, // dir 7
        { -1 * 12 + 1, -2 * 12 + 2 }, // dir 0 (wrap-around for dir+2)
        {  0 * 12 + 1, -1 * 12 + 2 }, // dir 1
    };

    /// <summary>
    /// UV direction remapping for 4:2:2 chroma subsampling.
    /// </summary>
    private static readonly byte[,] UvDirs = new byte[2, 8]
    {
        { 0, 1, 2, 3, 4, 5, 6, 7 }, // 4:4:4 / 4:2:0
        { 7, 0, 2, 4, 5, 6, 6, 6 }, // 4:2:2
    };

    private static readonly ushort[] DivTable = [840, 420, 280, 210, 168, 140, 120];

    // ========================================================================
    // Constrain function (cdef_tmpl.c: constrain)
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Constrain(int diff, int threshold, int shift)
    {
        int adiff = Math.Abs(diff);
        int val = Math.Min(adiff, Math.Max(0, threshold - (adiff >> shift)));
        return diff < 0 ? -val : val;
    }

    // ========================================================================
    // Direction Finding (cdef_tmpl.c: cdef_find_dir_c)
    // ========================================================================

    /// <summary>
    /// Find the dominant edge direction for an 8×8 block.
    /// Returns direction index (0-7) and sets variance.
    /// </summary>
    public static int FindDirection(ReadOnlySpan<byte> img, int imgOffset, int stride, out uint variance)
    {
        Span<int> partialSumHv0 = stackalloc int[8];
        Span<int> partialSumHv1 = stackalloc int[8];
        Span<int> partialSumDiag0 = stackalloc int[15];
        Span<int> partialSumDiag1 = stackalloc int[15];
        Span<int> partialSumAlt0 = stackalloc int[11];
        Span<int> partialSumAlt1 = stackalloc int[11];
        Span<int> partialSumAlt2 = stackalloc int[11];
        Span<int> partialSumAlt3 = stackalloc int[11];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int px = img[imgOffset + x] - 128;
                partialSumDiag0[y + x] += px;
                partialSumAlt0[y + (x >> 1)] += px;
                partialSumHv0[y] += px;
                partialSumAlt1[3 + y - (x >> 1)] += px;
                partialSumDiag1[7 + y - x] += px;
                partialSumAlt2[3 - (y >> 1) + x] += px;
                partialSumHv1[x] += px;
                partialSumAlt3[(y >> 1) + x] += px;
            }
            imgOffset += stride;
        }

        Span<uint> cost = stackalloc uint[8];
        for (int n = 0; n < 8; n++)
        {
            cost[2] += (uint)(partialSumHv0[n] * partialSumHv0[n]);
            cost[6] += (uint)(partialSumHv1[n] * partialSumHv1[n]);
        }
        cost[2] *= 105;
        cost[6] *= 105;

        for (int n = 0; n < 7; n++)
        {
            uint d = DivTable[n];
            cost[0] += (uint)(partialSumDiag0[n] * partialSumDiag0[n] +
                              partialSumDiag0[14 - n] * partialSumDiag0[14 - n]) * d;
            cost[4] += (uint)(partialSumDiag1[n] * partialSumDiag1[n] +
                              partialSumDiag1[14 - n] * partialSumDiag1[14 - n]) * d;
        }
        cost[0] += (uint)(partialSumDiag0[7] * partialSumDiag0[7]) * 105;
        cost[4] += (uint)(partialSumDiag1[7] * partialSumDiag1[7]) * 105;

        cost[1] = ComputeAltCost(partialSumAlt0);
        cost[3] = ComputeAltCost(partialSumAlt1);
        cost[5] = ComputeAltCost(partialSumAlt2);
        cost[7] = ComputeAltCost(partialSumAlt3);

        int bestDir = 0;
        uint bestCost = cost[0];
        for (int n = 1; n < 8; n++)
        {
            if (cost[n] > bestCost)
            {
                bestCost = cost[n];
                bestDir = n;
            }
        }

        variance = (bestCost - cost[bestDir ^ 4]) >> 10;
        return bestDir;
    }

    // ========================================================================
    // Padding (cdef_tmpl.c: padding)
    // ========================================================================

    /// <summary>
    /// Fill extended input buffer (int16) for the filter kernel.
    /// tmp points to offset [2*stride+2] in a (h+4)×12 buffer.
    /// </summary>
    private static void Padding(Span<short> tmp, int tmpOffset, int tmpStride,
        ReadOnlySpan<byte> src, int srcOffset, int srcStride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> top, int topOffset,
        ReadOnlySpan<byte> bottom, int bottomOffset,
        int w, int h, EdgeFlags edges)
    {
        int xStart = -2, xEnd = w + 2, yStart = -2, yEnd = h + 2;

        if ((edges & EdgeFlags.Top) == 0)
        {
            FillShort(tmp, tmpOffset - 2 - 2 * tmpStride, tmpStride, w + 4, 2);
            yStart = 0;
        }
        if ((edges & EdgeFlags.Bottom) == 0)
        {
            FillShort(tmp, tmpOffset + h * tmpStride - 2, tmpStride, w + 4, 2);
            yEnd -= 2;
        }
        if ((edges & EdgeFlags.Left) == 0)
        {
            FillShort(tmp, tmpOffset + yStart * tmpStride - 2, tmpStride, 2, yEnd - yStart);
            xStart = 0;
        }
        if ((edges & EdgeFlags.Right) == 0)
        {
            FillShort(tmp, tmpOffset + yStart * tmpStride + w, tmpStride, 2, yEnd - yStart);
            xEnd -= 2;
        }

        int topOff = topOffset;
        for (int y = yStart; y < 0; y++)
        {
            for (int x = xStart; x < xEnd; x++)
                tmp[tmpOffset + x + y * tmpStride] = top[topOff + x];
            topOff += srcStride;
        }

        for (int y = 0; y < h; y++)
            for (int x = xStart; x < 0; x++)
                tmp[tmpOffset + x + y * tmpStride] = left[leftOffset + y * leftStride + (2 + x)];

        int sOff = srcOffset;
        int tOff = tmpOffset;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < xEnd; x++)
                tmp[tOff + x] = src[sOff + x];
            sOff += srcStride;
            tOff += tmpStride;
        }

        int bOff = bottomOffset;
        for (int y = h; y < yEnd; y++)
        {
            for (int x = xStart; x < xEnd; x++)
                tmp[tOff + x] = bottom[bOff + x];
            bOff += srcStride;
            tOff += tmpStride;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillShort(Span<short> buf, int offset, int stride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                buf[offset + x] = short.MinValue;
            offset += stride;
        }
    }

    // ========================================================================
    // Filter Kernel (cdef_tmpl.c: cdef_filter_block_c)
    // ========================================================================

    /// <summary>
    /// Apply CDEF filter to a single block (w×h, where w,h ∈ {4,8}).
    /// dst is filtered in-place. left[y][2] provides the 2 left context pixels per row.
    /// top/bottom provide the 2 rows above/below for padding.
    /// </summary>
    public static void FilterBlock(Span<byte> dst, int dstOffset, int dstStride,
        ReadOnlySpan<byte> left, int leftOffset, int leftStride,
        ReadOnlySpan<byte> top, int topOffset,
        ReadOnlySpan<byte> bottom, int bottomOffset,
        int priStrength, int secStrength, int dir, int damping,
        int w, int h, EdgeFlags edges)
    {
        const int tmpStride = 12;
        Span<short> tmpBuf = stackalloc short[144]; // 12*12
        int tmpCenter = 2 * tmpStride + 2;

        Padding(tmpBuf, tmpCenter, tmpStride,
            dst, dstOffset, dstStride,
            left, leftOffset, leftStride,
            top, topOffset, bottom, bottomOffset,
            w, h, edges);

        int dOff = dstOffset;
        int tOff = tmpCenter;

        if (priStrength != 0)
        {
            int priTap = 4 - ((priStrength) & 1); // for 8-bit, bitdepth_min_8=0
            int priShift = Math.Max(0, damping - Log2(priStrength));

            if (secStrength != 0)
            {
                int secShift = damping - Log2(secStrength);
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int px = dst[dOff + x];
                        int sum = 0;
                        int max = px, min = px;
                        int priTapK = priTap;
                        for (int k = 0; k < 2; k++)
                        {
                            int off1 = Directions[dir + 2, k];
                            int p0 = tmpBuf[tOff + x + off1];
                            int p1 = tmpBuf[tOff + x - off1];
                            sum += priTapK * Constrain(p0 - px, priStrength, priShift);
                            sum += priTapK * Constrain(p1 - px, priStrength, priShift);
                            priTapK = (priTapK & 3) | 2;
                            min = MinU16(p0, min); max = Math.Max(p0, max);
                            min = MinU16(p1, min); max = Math.Max(p1, max);

                            int off2 = Directions[dir + 4, k];
                            int off3 = Directions[dir + 0, k];
                            int s0 = tmpBuf[tOff + x + off2];
                            int s1 = tmpBuf[tOff + x - off2];
                            int s2 = tmpBuf[tOff + x + off3];
                            int s3 = tmpBuf[tOff + x - off3];
                            int secTap = 2 - k;
                            sum += secTap * Constrain(s0 - px, secStrength, secShift);
                            sum += secTap * Constrain(s1 - px, secStrength, secShift);
                            sum += secTap * Constrain(s2 - px, secStrength, secShift);
                            sum += secTap * Constrain(s3 - px, secStrength, secShift);
                            min = MinU16(s0, min); max = Math.Max(s0, max);
                            min = MinU16(s1, min); max = Math.Max(s1, max);
                            min = MinU16(s2, min); max = Math.Max(s2, max);
                            min = MinU16(s3, min); max = Math.Max(s3, max);
                        }
                        dst[dOff + x] = (byte)Math.Clamp(
                            px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4), min, max);
                    }
                    dOff += dstStride;
                    tOff += tmpStride;
                }
            }
            else // pri_strength only
            {
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int px = dst[dOff + x];
                        int sum = 0;
                        int priTapK = priTap;
                        for (int k = 0; k < 2; k++)
                        {
                            int off = Directions[dir + 2, k];
                            int p0 = tmpBuf[tOff + x + off];
                            int p1 = tmpBuf[tOff + x - off];
                            sum += priTapK * Constrain(p0 - px, priStrength, priShift);
                            sum += priTapK * Constrain(p1 - px, priStrength, priShift);
                            priTapK = (priTapK & 3) | 2;
                        }
                        dst[dOff + x] = (byte)(px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4));
                    }
                    dOff += dstStride;
                    tOff += tmpStride;
                }
            }
        }
        else // sec_strength only
        {
            int secShift = damping - Log2(secStrength);
            for (int row = 0; row < h; row++)
            {
                for (int x = 0; x < w; x++)
                {
                    int px = dst[dOff + x];
                    int sum = 0;
                    for (int k = 0; k < 2; k++)
                    {
                        int off1 = Directions[dir + 4, k];
                        int off2 = Directions[dir + 0, k];
                        int s0 = tmpBuf[tOff + x + off1];
                        int s1 = tmpBuf[tOff + x - off1];
                        int s2 = tmpBuf[tOff + x + off2];
                        int s3 = tmpBuf[tOff + x - off2];
                        int secTap = 2 - k;
                        sum += secTap * Constrain(s0 - px, secStrength, secShift);
                        sum += secTap * Constrain(s1 - px, secStrength, secShift);
                        sum += secTap * Constrain(s2 - px, secStrength, secShift);
                        sum += secTap * Constrain(s3 - px, secStrength, secShift);
                    }
                    dst[dOff + x] = (byte)(px + ((sum - (sum < 0 ? 1 : 0) + 8) >> 4));
                }
                dOff += dstStride;
                tOff += tmpStride;
            }
        }
    }

    // ========================================================================
    // Strength Adjustment (cdef_apply_tmpl.c: adjust_strength)
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AdjustStrength(int strength, uint variance)
    {
        if (variance == 0) return 0;
        int i = variance >> 6 != 0 ? Math.Min(Log2(variance >> 6), 12) : 0;
        return (strength * (4 + i) + 8) >> 4;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeAltCost(Span<int> alt)
    {
        uint c = 0;
        for (int m = 0; m < 5; m++)
            c += (uint)(alt[3 + m] * alt[3 + m]);
        c *= 105;
        for (int m = 0; m < 3; m++)
        {
            uint d = DivTable[2 * m + 1];
            c += (uint)(alt[m] * alt[m] + alt[10 - m] * alt[10 - m]) * d;
        }
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(int v)
    {
        int r = 0;
        while (v > 1) { v >>= 1; r++; }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(uint v) => Log2((int)v);

    /// <summary>Min treating short.MinValue as "large" (unavailable pixel sentinel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinU16(int a, int b)
    {
        // In dav1d, INT16_MIN (0x8000) is used as sentinel for unavailable pixels.
        // When interpreted as unsigned 16-bit, it's 32768 (very large), so umin skips it.
        if (a == short.MinValue) return b;
        if (b == short.MinValue) return a;
        return a < b ? a : b;
    }
}
