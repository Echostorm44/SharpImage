// AV1 motion compensation for the decoder
// Ported from dav1d: src/mc_tmpl.c (VideoLAN dav1d, BSD-2-Clause)
// Implements 8-tap subpel filtering, bilinear, warp affine, OBMC blending,
// weighted prediction, edge emulation, and super-resolution resize.

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 motion compensation DSP routines. Handles subpel interpolation
/// (8-tap and bilinear), warp-affine, compound blending (avg, w_avg, mask),
/// OBMC blending (blend_v, blend_h), edge emulation, and super-res resize.
/// All operations work on 8-bit pixels. High bit depth (10/12) uses separate 16-bit paths.
/// </summary>
public static class Av1MotionComp
{
    // dav1d get_intermediate_bits / PREP_BIAS: 4 and 0 for 8-bit; 14 - bitdepth and 8192 for 10/12-bit (keeps the
    // compound intermediates inside int16).
    private static int IntermediateBits8 => McBitDepth == 8 ? 4 : 14 - McBitDepth;
    private static int PrepBias8 => McBitDepth == 8 ? 0 : 8192;

    // ========================================================================
    // Simple copy / prepare
    // ========================================================================

    /// <summary>
    /// Simple pixel copy (no subpel filtering).
    /// </summary>
    public static void Put(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h)
    {
        for (int y = 0; y < h; y++)
            src.Slice(y * srcStride, w).CopyTo(dst.Slice(y * dstStride, w));
    }

    /// <summary>
    /// Prepare intermediate values for compound prediction (no subpel filtering).
    /// Output is <c>(src[x] &lt;&lt; intermediate_bits) - PREP_BIAS</c>.
    /// </summary>
    public static void Prep(
        Span<short> tmp,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h)
    {
        int tmpIdx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                tmp[tmpIdx + x] = (short)((src[y * srcStride + x] << IntermediateBits8) - PrepBias8);
            tmpIdx += w;
        }
    }

    // ========================================================================
    // 8-tap subpel filter helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Filter8Tap(ReadOnlySpan<ushort> src, int x, int f0, int f1, int f2, int f3, int f4, int f5, int f6, int f7, int stride)
    {
        return f0 * src[x - 3 * stride] + f1 * src[x - 2 * stride] +
               f2 * src[x - 1 * stride] + f3 * src[x] +
               f4 * src[x + 1 * stride] + f5 * src[x + 2 * stride] +
               f6 * src[x + 3 * stride] + f7 * src[x + 4 * stride];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Filter8TapMid(Span<short> mid, int x, int f0, int f1, int f2, int f3, int f4, int f5, int f6, int f7, int stride)
    {
        return f0 * mid[x - 3 * stride] + f1 * mid[x - 2 * stride] +
               f2 * mid[x - 1 * stride] + f3 * mid[x] +
               f4 * mid[x + 1 * stride] + f5 * mid[x + 2 * stride] +
               f6 * mid[x + 3 * stride] + f7 * mid[x + 4 * stride];
    }

    /// <summary>
    /// Gets the horizontal 8-tap filter for the given motion vector fractional position.
    /// Returns null (length 0) if mx == 0 (integer position).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetFilters(int mx, int my, int w, int h, int filterType,
        out bool hasFh, out int fhType, out int fhPos,
        out bool hasFv, out int fvType, out int fvPos)
    {
        hasFh = mx != 0;
        fhType = hasFh ? (w > 4 ? (filterType & 3) : (3 + (filterType & 1))) : 0;
        fhPos = mx - 1;

        hasFv = my != 0;
        fvType = hasFv ? (h > 4 ? (filterType >> 2) : (3 + ((filterType >> 2) & 1))) : 0;
        fvPos = my - 1;
    }

    // ========================================================================
    // 8-tap put (pixel output)
    // ========================================================================

    /// <summary>
    /// 8-tap subpel interpolation: write to pixel output.
    /// Ref src pointer should already point to the top-left of the padded area
    /// (3 rows above + 3 cols left of the block origin). Uses non-negative indices.
    /// </summary>
    /// <param name="filterType">Packed filter type: (hor_filter | ver_filter &lt;&lt; 2).</param>
    public static void Put8Tap(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h, int mx, int my, int filterType)
    {
        int intermediateBits = IntermediateBits8;
        int intermediateRnd = 32 + ((1 << (6 - intermediateBits)) >> 1);

        int srcPad = 3 * srcStride + 3; // top-left padding offset within src

        GetFilters(mx, my, w, h, filterType,
            out bool hasFh, out int fhType, out int fhPos,
            out bool hasFv, out int fvType, out int fvPos);

        // Debug for first non-zero MV block
        if (mx == 4 && my == 0 && w == 8)
        {
            int dbgIdx = srcPad;
            AvDbg.W($"[MC-8TAP-DBG] mx={mx} my={my} srcPad={srcPad} stride={srcStride} taps:");
            for (int t = 0; t < 8; t++)
                AvDbg.W($" {src[dbgIdx - 3 + t]:X2}({src[dbgIdx - 3 + t]})");
            AvDbg.W();
        }

        if (hasFh)
        {
            if (hasFv)
            {
                // H+V: horizontal filter into intermediate buffer, then vertical filter
                int tmpH = h + 7;
                Span<short> mid = stackalloc short[128 * 135];
                int midIdx = 0;
                int srcIdx = 0; // start at top-left of padded area

                for (int y = 0; y < tmpH; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // dav1d put_8tap: mid row y = horizontal filter of source row (dy-3+y).
                        // Only +3 cols here; the +3 rows are handled by the vertical pass (midIdx = 128*3).
                        int val = Filter8Tap(src, srcIdx + 3 + x,
                            Av1Tables.McSubpelFilters[fhType, fhPos, 0],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 1],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 2],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 3],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 4],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 5],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 6],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 7],
                            1);
                        mid[midIdx + x] = (short)((val + ((1 << (6 - intermediateBits)) >> 1)) >> (6 - intermediateBits));
                    }
                    midIdx += 128;
                    srcIdx += srcStride;
                }

                midIdx = 128 * 3;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int val = Filter8TapMid(mid, midIdx + x,
                            Av1Tables.McSubpelFilters[fvType, fvPos, 0],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 1],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 2],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 3],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 4],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 5],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 6],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 7],
                            128);
                        dst[y * dstStride + x] = ClipPixel((val + ((1 << (6 + intermediateBits)) >> 1)) >> (6 + intermediateBits));
                    }
                    midIdx += 128;
                }
            }
            else
            {
                // H only
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int val = Filter8Tap(src, srcPad + y * srcStride + x,
                            Av1Tables.McSubpelFilters[fhType, fhPos, 0],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 1],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 2],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 3],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 4],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 5],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 6],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 7],
                            1);
                        dst[y * dstStride + x] = ClipPixel((val + intermediateRnd) >> 6);
                    }
                }
            }
        }
        else if (hasFv)
        {
            // V only
            if (my == 4 && mx == 0 && w == 8 && srcStride > 100)
            {
                int dbgVal = Filter8Tap(src, srcPad,
                    Av1Tables.McSubpelFilters[fvType, fvPos, 0],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 1],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 2],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 3],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 4],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 5],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 6],
                    Av1Tables.McSubpelFilters[fvType, fvPos, 7],
                    srcStride);
                int dbgResult = (dbgVal + ((1 << 6) >> 1)) >> 6;
                int dbgPos3 = srcPad - 3 * srcStride;
                int dbgPos2 = srcPad - 2 * srcStride;
                int dbgPos1 = srcPad - 1 * srcStride;
                int dbgPos0 = srcPad;
                int dbgPos4 = srcPad + 1 * srcStride;
                int dbgPos5 = srcPad + 2 * srcStride;
                int dbgPos6 = srcPad + 3 * srcStride;
                int dbgPos7 = srcPad + 4 * srcStride;
                AvDbg.W($"[MC-VDETAIL] pos=({dbgPos3},{dbgPos2},{dbgPos1},{dbgPos0},{dbgPos4},{dbgPos5},{dbgPos6},{dbgPos7}) vals=({src[dbgPos3]},{src[dbgPos2]},{src[dbgPos1]},{src[dbgPos0]},{src[dbgPos4]},{src[dbgPos5]},{src[dbgPos6]},{src[dbgPos7]}) f*val={0*src[dbgPos3] + 1*src[dbgPos2] + (-7)*src[dbgPos1] + 55*src[dbgPos0] + 19*src[dbgPos4] + (-5)*src[dbgPos5] + 1*src[dbgPos6] + 0*src[dbgPos7]}");
                AvDbg.W($"[MC-VRESULT] filterVal={dbgVal} result={dbgResult} fvPos={fvPos} fvType={fvType}");
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int val = Filter8Tap(src, srcPad + y * srcStride + x,
                        Av1Tables.McSubpelFilters[fvType, fvPos, 0],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 1],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 2],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 3],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 4],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 5],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 6],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 7],
                        srcStride);
                    dst[y * dstStride + x] = ClipPixel((val + ((1 << 6) >> 1)) >> 6);
                }
            }
        }
        else
        {
            Put(dst, dstStride, src.Slice(srcPad), srcStride, w, h);
        }
    }

    // ========================================================================
    // 8-tap prep (intermediate output for compound)
    // ========================================================================

    /// <summary>
    /// 8-tap subpel interpolation: write to intermediate int16 buffer for compound prediction.
    /// Ref src pointer should already point to the top-left of the padded area.
    /// </summary>
    public static void Prep8Tap(
        Span<short> tmp,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h, int mx, int my, int filterType)
    {
        int intermediateBits = IntermediateBits8;
        int srcPad = 3 * srcStride + 3;

        GetFilters(mx, my, w, h, filterType,
            out bool hasFh, out int fhType, out int fhPos,
            out bool hasFv, out int fvType, out int fvPos);

        if (hasFh)
        {
            if (hasFv)
            {
                int tmpH = h + 7;
                Span<short> mid = stackalloc short[128 * 135];
                int midIdx = 0;
                int srcIdx = 0;

                for (int y = 0; y < tmpH; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // dav1d prep_8tap: mid row y = horizontal filter of source row (dy-3+y).
                        // Only +3 cols here; the +3 rows are handled by the vertical pass (midIdx = 128*3).
                        int val = Filter8Tap(src, srcIdx + 3 + x,
                            Av1Tables.McSubpelFilters[fhType, fhPos, 0],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 1],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 2],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 3],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 4],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 5],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 6],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 7],
                            1);
                        mid[midIdx + x] = (short)((val + ((1 << (6 - intermediateBits)) >> 1)) >> (6 - intermediateBits));
                    }
                    midIdx += 128;
                    srcIdx += srcStride;
                }

                midIdx = 128 * 3;
                int tmpIdx = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int val = Filter8TapMid(mid, midIdx + x,
                            Av1Tables.McSubpelFilters[fvType, fvPos, 0],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 1],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 2],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 3],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 4],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 5],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 6],
                            Av1Tables.McSubpelFilters[fvType, fvPos, 7],
                            128);
                        tmp[tmpIdx + x] = (short)(((val + ((1 << 6) >> 1)) >> 6) - PrepBias8);
                    }
                    midIdx += 128;
                    tmpIdx += w;
                }
            }
            else
            {
                int tmpIdx = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int val = Filter8Tap(src, srcPad + y * srcStride + x,
                            Av1Tables.McSubpelFilters[fhType, fhPos, 0],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 1],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 2],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 3],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 4],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 5],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 6],
                            Av1Tables.McSubpelFilters[fhType, fhPos, 7],
                            1);
                        tmp[tmpIdx + x] = (short)(((val + ((1 << (6 - intermediateBits)) >> 1)) >> (6 - intermediateBits)) - PrepBias8);
                    }
                    tmpIdx += w;
                }
            }
        }
        else if (hasFv)
        {
            int tmpIdx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int val = Filter8Tap(src, srcPad + y * srcStride + x,
                        Av1Tables.McSubpelFilters[fvType, fvPos, 0],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 1],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 2],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 3],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 4],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 5],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 6],
                        Av1Tables.McSubpelFilters[fvType, fvPos, 7],
                        srcStride);
                    tmp[tmpIdx + x] = (short)(((val + ((1 << (6 - intermediateBits)) >> 1)) >> (6 - intermediateBits)) - PrepBias8);
                }
                tmpIdx += w;
            }
        }
        else
        {
            Prep(tmp, src.Slice(srcPad), srcStride, w, h);
        }
    }

    // ========================================================================
    // Bilinear filter
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterBilin(ReadOnlySpan<ushort> src, int x, int mxy, int stride)
    {
        return 16 * src[x] + mxy * (src[x + stride] - src[x]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterBilinMid(Span<short> src, int x, int mxy, int stride)
    {
        return 16 * src[x] + mxy * (src[x + stride] - src[x]);
    }

    /// <summary>
    /// Bilinear interpolation: write to pixel output.
    /// </summary>
    public static void PutBilin(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h, int mx, int my)
    {
        int intermediateBits = IntermediateBits8;
        int intermediateRnd = (1 << intermediateBits) >> 1;

        if (mx != 0)
        {
            if (my != 0)
            {
                // H+V bilinear
                int tmpH = h + 1;
                Span<short> mid = stackalloc short[128 * 129];
                int midIdx = 0;
                int srcIdx = 0;

                for (int y = 0; y < tmpH; y++)
                {
                    for (int x = 0; x < w; x++)
                        mid[midIdx + x] = (short)((FilterBilin(src, srcIdx + x, mx, 1) +
                            ((1 << (4 - intermediateBits)) >> 1)) >> (4 - intermediateBits));
                    midIdx += 128;
                    srcIdx += srcStride;
                }

                midIdx = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        dst[y * dstStride + x] = ClipPixel(
                            (FilterBilinMid(mid, midIdx + x, my, 128) +
                            ((1 << (4 + intermediateBits)) >> 1)) >> (4 + intermediateBits));
                    midIdx += 128;
                }
            }
            else
            {
                // H only bilinear
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int px = (FilterBilin(src, y * srcStride + x, mx, 1) +
                            ((1 << (4 - intermediateBits)) >> 1)) >> (4 - intermediateBits);
                        dst[y * dstStride + x] = ClipPixel((px + intermediateRnd) >> intermediateBits);
                    }
            }
        }
        else if (my != 0)
        {
            // V only bilinear
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    dst[y * dstStride + x] = ClipPixel(
                        (FilterBilin(src, y * srcStride + x, my, srcStride) +
                        ((1 << 4) >> 1)) >> 4);
        }
        else
        {
            Put(dst, dstStride, src, srcStride, w, h);
        }
    }

    /// <summary>
    /// Bilinear interpolation: write to intermediate buffer for compound.
    /// </summary>
    public static void PrepBilin(
        Span<short> tmp,
        ReadOnlySpan<ushort> src, int srcStride,
        int w, int h, int mx, int my)
    {
        int intermediateBits = IntermediateBits8;

        if (mx != 0)
        {
            if (my != 0)
            {
                int tmpH = h + 1;
                Span<short> mid = stackalloc short[128 * 129];
                int midIdx = 0;
                int srcIdx = 0;

                for (int y = 0; y < tmpH; y++)
                {
                    for (int x = 0; x < w; x++)
                        mid[midIdx + x] = (short)((FilterBilin(src, srcIdx + x, mx, 1) +
                            ((1 << (4 - intermediateBits)) >> 1)) >> (4 - intermediateBits));
                    midIdx += 128;
                    srcIdx += srcStride;
                }

                midIdx = 0;
                int tmpIdx = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        tmp[tmpIdx + x] = (short)(((FilterBilinMid(mid, midIdx + x, my, 128) +
                            ((1 << 4) >> 1)) >> 4) - PrepBias8);
                    midIdx += 128;
                    tmpIdx += w;
                }
            }
            else
            {
                int tmpIdx = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                        tmp[tmpIdx + x] = (short)(((FilterBilin(src, y * srcStride + x, mx, 1) +
                            ((1 << (4 - intermediateBits)) >> 1)) >> (4 - intermediateBits)) - PrepBias8);
                    tmpIdx += w;
                }
            }
        }
        else if (my != 0)
        {
            int tmpIdx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    tmp[tmpIdx + x] = (short)(((FilterBilin(src, y * srcStride + x, my, srcStride) +
                        ((1 << (4 - intermediateBits)) >> 1)) >> (4 - intermediateBits)) - PrepBias8);
                tmpIdx += w;
            }
        }
        else
        {
            Prep(tmp, src, srcStride, w, h);
        }
    }

    // ========================================================================
    // Compound blending (avg, w_avg, mask)
    // ========================================================================

    /// <summary>
    /// Simple average of two compound (intermediate) buffers.
    /// </summary>
    public static void Avg(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<short> tmp1, ReadOnlySpan<short> tmp2,
        int w, int h)
    {
        int sh = IntermediateBits8 + 1;
        int rnd = (1 << IntermediateBits8) + PrepBias8 * 2;
        int idx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y * dstStride + x] = ClipPixel((tmp1[idx + x] + tmp2[idx + x] + rnd) >> sh);
            idx += w;
        }
    }

    /// <summary>
    /// Weighted average of two compound buffers.
    /// Weight ranges from 0 to 16 (applied to tmp1, 16-weight to tmp2).
    /// </summary>
    public static void WeightedAvg(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<short> tmp1, ReadOnlySpan<short> tmp2,
        int w, int h, int weight)
    {
        int sh = IntermediateBits8 + 4;
        int rnd = (8 << IntermediateBits8) + PrepBias8 * 16;
        int idx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y * dstStride + x] = ClipPixel(
                    (tmp1[idx + x] * weight + tmp2[idx + x] * (16 - weight) + rnd) >> sh);
            idx += w;
        }
    }

    /// <summary>
    /// Mask-based blending of two compound buffers.
    /// Mask values range 0..64.
    /// </summary>
    public static void Mask(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<short> tmp1, ReadOnlySpan<short> tmp2,
        int w, int h, ReadOnlySpan<byte> mask)
    {
        int sh = IntermediateBits8 + 6;
        int rnd = (32 << IntermediateBits8) + PrepBias8 * 64;
        int idx = 0;
        int maskIdx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y * dstStride + x] = ClipPixel(
                    (tmp1[idx + x] * mask[maskIdx + x] +
                     tmp2[idx + x] * (64 - mask[maskIdx + x]) + rnd) >> sh);
            idx += w;
            maskIdx += w;
        }
    }

    // ========================================================================
    // OBMC blending
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort BlendPx(int a, int b, int m)
    {
        return (ushort)((a * (64 - m) + b * m + 32) >> 6);
    }

    /// <summary>
    /// Blend with mask (generic, per-pixel mask).
    /// </summary>
    public static void Blend(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> tmp, int w, int h,
        ReadOnlySpan<byte> mask)
    {
        int tmpIdx = 0;
        int maskIdx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[y * dstStride + x] = BlendPx(dst[y * dstStride + x], tmp[tmpIdx + x], mask[maskIdx + x]);
            tmpIdx += w;
            maskIdx += w;
        }
    }

    /// <summary>
    /// OBMC vertical blending: blend left ¾ of the block width using OBMC masks.
    /// </summary>
    public static void BlendV(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> tmp, int w, int h)
    {
        var mask = Av1Tables.ObmcMasks.AsSpan(w);
        int tmpIdx = 0;
        int blendW = (w * 3) >> 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < blendW; x++)
                dst[y * dstStride + x] = BlendPx(dst[y * dstStride + x], tmp[tmpIdx + x], mask[x]);
            tmpIdx += w;
        }
    }

    /// <summary>
    /// OBMC horizontal blending: blend top ¾ of the block height using OBMC masks.
    /// </summary>
    public static void BlendH(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> tmp, int w, int h)
    {
        var mask = Av1Tables.ObmcMasks.AsSpan(h);
        int blendH = (h * 3) >> 2;
        int tmpIdx = 0;
        int maskIdx = 0;
        for (int y = 0; y < blendH; y++)
        {
            int m = mask[maskIdx++];
            for (int x = 0; x < w; x++)
                dst[y * dstStride + x] = BlendPx(dst[y * dstStride + x], tmp[tmpIdx + x], m);
            tmpIdx += w;
        }
    }

    // ========================================================================
    // Weighted mask (wedge/compound)
    // ========================================================================

    /// <summary>
    /// Weighted mask compound prediction. Generates a mask from the difference
    /// of two intermediate buffers and blends them.
    /// </summary>
    public static void WeightedMask(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<short> tmp1, ReadOnlySpan<short> tmp2,
        int w, int h, Span<byte> mask, int sign,
        int ssHor, int ssVer)
    {
        int intermediateBits = IntermediateBits8;
        const int bitDepth = 8;
        int sh = intermediateBits + 6;
        int rnd = (32 << intermediateBits) + PrepBias8 * 64;
        int maskSh = bitDepth + intermediateBits - 4;
        int maskRnd = 1 << (maskSh - 5);
        int idx = 0;
        int maskIdx = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int diff = tmp1[idx + x] - tmp2[idx + x];
                int m = Math.Min(38 + ((Math.Abs(diff) + maskRnd) >> maskSh), 64);
                dst[y * dstStride + x] = ClipPixel((diff * m + tmp2[idx + x] * 64 + rnd) >> sh);

                if (ssHor != 0)
                {
                    x++;
                    int diff2 = tmp1[idx + x] - tmp2[idx + x];
                    int n = Math.Min(38 + ((Math.Abs(diff2) + maskRnd) >> maskSh), 64);
                    dst[y * dstStride + x] = ClipPixel((diff2 * n + tmp2[idx + x] * 64 + rnd) >> sh);

                    if ((y & ssVer) != 0)
                        mask[maskIdx + (x >> 1)] = (byte)((m + n + mask[maskIdx + (x >> 1)] + 2 - sign) >> 2);
                    else if (ssVer != 0)
                        mask[maskIdx + (x >> 1)] = (byte)(m + n);
                    else
                        mask[maskIdx + (x >> 1)] = (byte)((m + n + 1 - sign) >> 1);
                }
                else
                {
                    mask[maskIdx + x] = (byte)m;
                }
            }
            idx += w;
            if (ssVer == 0 || (y & 1) != 0) maskIdx += w >> ssHor;
        }
    }

    // ========================================================================
    // Warp affine 8×8
    // ========================================================================

    /// <summary>
    /// Warp affine motion compensation for an 8×8 block (pixel output).
    /// </summary>
    /// <param name="abcd">Warp parameters: [alpha, beta, gamma, delta].</param>
    /// <param name="src">Reference pixels starting 3 rows above and 3 columns left of the block (the tap corner).</param>
    public static void WarpAffine8x8(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> src, int srcStride,
        ReadOnlySpan<short> abcd, int mx, int my)
    {
        int intermediateBits = IntermediateBits8;
        Span<short> mid = stackalloc short[15 * 8];
        int midIdx = 0;

        int srcIdx = 0;   // src starts at the (-3, -3) tap corner (spans cannot index backwards)
        for (int y = 0; y < 15; y++, mx += abcd[1])
        {
            for (int x = 0, tmx = mx; x < 8; x++, tmx += abcd[0])
            {
                int filterIdx = 64 + ((tmx + 512) >> 10);
                filterIdx = Math.Clamp(filterIdx, 0, 192);
                int val = 0;
                for (int t = 0; t < 8; t++)
                    val += Av1Tables.McWarpFilter[filterIdx, t] * src[srcIdx + x + t];
                mid[midIdx + x] = (short)((val + ((1 << (7 - intermediateBits)) >> 1)) >> (7 - intermediateBits));
            }
            srcIdx += srcStride;
            midIdx += 8;
        }

        midIdx = 3 * 8;
        for (int y = 0; y < 8; y++, my += abcd[3])
        {
            for (int x = 0, tmy = my; x < 8; x++, tmy += abcd[2])
            {
                int filterIdx = 64 + ((tmy + 512) >> 10);
                filterIdx = Math.Clamp(filterIdx, 0, 192);
                int val = 0;
                for (int t = 0; t < 8; t++)
                    val += Av1Tables.McWarpFilter[filterIdx, t] * mid[midIdx + x + (t - 3) * 8];
                dst[y * dstStride + x] = ClipPixel((val + ((1 << (7 + intermediateBits)) >> 1)) >> (7 + intermediateBits));
            }
            midIdx += 8;
        }
    }

    /// <summary>
    /// Warp affine 8×8 for compound prediction (intermediate output).
    /// </summary>
    public static void WarpAffine8x8t(
        Span<short> tmp, int tmpStride,
        ReadOnlySpan<ushort> src, int srcStride,
        ReadOnlySpan<short> abcd, int mx, int my)
    {
        int intermediateBits = IntermediateBits8;
        Span<short> mid = stackalloc short[15 * 8];
        int midIdx = 0;

        int srcIdx = 0;   // src starts at the (-3, -3) tap corner (spans cannot index backwards)
        for (int y = 0; y < 15; y++, mx += abcd[1])
        {
            for (int x = 0, tmx = mx; x < 8; x++, tmx += abcd[0])
            {
                int filterIdx = 64 + ((tmx + 512) >> 10);
                filterIdx = Math.Clamp(filterIdx, 0, 192);
                int val = 0;
                for (int t = 0; t < 8; t++)
                    val += Av1Tables.McWarpFilter[filterIdx, t] * src[srcIdx + x + t];
                mid[midIdx + x] = (short)((val + ((1 << (7 - intermediateBits)) >> 1)) >> (7 - intermediateBits));
            }
            srcIdx += srcStride;
            midIdx += 8;
        }

        midIdx = 3 * 8;
        int tmpIdx = 0;
        for (int y = 0; y < 8; y++, my += abcd[3])
        {
            for (int x = 0, tmy = my; x < 8; x++, tmy += abcd[2])
            {
                int filterIdx = 64 + ((tmy + 512) >> 10);
                filterIdx = Math.Clamp(filterIdx, 0, 192);
                int val = 0;
                for (int t = 0; t < 8; t++)
                    val += Av1Tables.McWarpFilter[filterIdx, t] * mid[midIdx + x + (t - 3) * 8];
                tmp[tmpIdx + x] = (short)(((val + ((1 << 7) >> 1)) >> 7) - PrepBias8);
            }
            midIdx += 8;
            tmpIdx += tmpStride;
        }
    }

    // ========================================================================
    // Edge emulation
    // ========================================================================

    // ========================================================================
    // Scaled motion compensation (reference frame of a different size)
    // ========================================================================

    /// <summary>Converts dav1d's FILTER_2D index (0..8 eight-tap combinations) to the packed h | v &lt;&lt; 2 filter type.</summary>
    internal static int PackedFilterType(int filter2d) => filter2d switch
    {
        0 => 0,             // REGULAR
        1 => 0 | (1 << 2),  // REGULAR_SMOOTH (h regular, v smooth)
        2 => 0 | (2 << 2),  // REGULAR_SHARP
        3 => 2 | (0 << 2),  // SHARP_REGULAR
        4 => 2 | (1 << 2),  // SHARP_SMOOTH
        5 => 2 | (2 << 2),  // SHARP
        6 => 1 | (0 << 2),  // SMOOTH_REGULAR
        7 => 1 | (1 << 2),  // SMOOTH
        8 => 1 | (2 << 2),  // SMOOTH_SHARP
        _ => 0,
    };

    // put_8tap_scaled_c / prep_8tap_scaled_c (dav1d mc_tmpl.c): 16-row... rolling 8-row window of horizontally filtered
    // rows, 10-bit (1/1024) source positions stepping by dx / dy. src[org] is the block origin in the reference; the
    // span must include 3 rows above / 3 columns left of it (and the right / bottom margin).
    private static void Scaled8Tap(Span<ushort> dst, int dstStride, Span<short> tmp, ReadOnlySpan<ushort> src, int org,
        int srcStride, int w, int h, int mx, int my, int dx, int dy, int filterType, bool put)
    {
        int ib = IntermediateBits8, irnd = (1 << ib) >> 1, bias = PrepBias8;
        var mid = new short[128 * 8];
        var rows = new int[8];
        for (int i = 0; i < 8; i++) rows[i] = 128 * i;
        int inY = -8;
        int s = org - 3 * srcStride;
        for (int y = 0; y < h; y++)
        {
            int srcY = my >> 10;
            int fvPos = (my & 0x3ff) >> 6;
            int fvType = h > 4 ? filterType >> 2 : 3 + ((filterType >> 2) & 1);
            while (inY < srcY)
            {
                int imx = mx, ioff = 0;
                int first = rows[0];
                for (int i = 0; i < 7; i++) rows[i] = rows[i + 1];
                rows[7] = first;
                for (int x = 0; x < w; x++)
                {
                    int fhPos = imx >> 6;
                    if (fhPos != 0)
                    {
                        int fhType = w > 4 ? filterType & 3 : 3 + (filterType & 1);
                        int sum = 0;
                        for (int k = 0; k < 8; k++) sum += Av1Tables.McSubpelFilters[fhType, fhPos - 1, k] * src[s + ioff + k - 3];
                        mid[first + x] = (short)((sum + ((1 << (6 - ib)) >> 1)) >> (6 - ib));
                    }
                    else mid[first + x] = (short)(src[s + ioff] << ib);
                    imx += dx;
                    ioff += imx >> 10;
                    imx &= 0x3ff;
                }
                s += srcStride;
                inY++;
            }
            for (int x = 0; x < w; x++)
            {
                int v;
                if (fvPos != 0)
                {
                    int sum = 0;
                    for (int k = 0; k < 8; k++) sum += Av1Tables.McSubpelFilters[fvType, fvPos - 1, k] * mid[rows[k] + x];
                    v = put ? (sum + ((1 << (6 + ib)) >> 1)) >> (6 + ib) : (sum + 32) >> 6;
                }
                else v = put ? (mid[rows[3] + x] + irnd) >> ib : mid[rows[3] + x];
                if (put) dst[y * dstStride + x] = ClipPixel(v);
                else tmp[y * w + x] = (short)(v - bias);
            }
            my += dy;
        }
    }

    // put_bilin_scaled_c / prep_bilin_scaled_c.
    private static void ScaledBilin(Span<ushort> dst, int dstStride, Span<short> tmp, ReadOnlySpan<ushort> src, int org,
        int srcStride, int w, int h, int mx, int my, int dx, int dy, bool put)
    {
        int ib = IntermediateBits8, bias = PrepBias8;
        var mid = new short[128 * 2];
        int inY = -2;
        int s = org;
        for (int y0 = 0; y0 < h; y0++)
        {
            int y = my >> 10;
            int m1 = (y & 1) * 128, m2 = ((y + 1) & 1) * 128;
            int dmy = my & 0x3ff;
            while (inY < y)
            {
                int imx = mx, ioff = 0;
                int mp = (inY & 1) * 128;
                for (int x = 0; x < w; x++)
                {
                    int f = imx >> 6;
                    int a = src[s + ioff], b = src[s + ioff + 1];
                    mid[mp + x] = (short)((16 * a + f * (b - a) + ((1 << (4 - ib)) >> 1)) >> (4 - ib));
                    imx += dx;
                    ioff += imx >> 10;
                    imx &= 0x3ff;
                }
                s += srcStride;
                inY++;
            }
            int fy = dmy >> 6;
            for (int x = 0; x < w; x++)
            {
                int a = mid[m1 + x], b = mid[m2 + x];
                int sum = 16 * a + fy * (b - a);
                if (put) dst[y0 * dstStride + x] = ClipPixel((sum + ((1 << (4 + ib)) >> 1)) >> (4 + ib));
                else tmp[y0 * w + x] = (short)(((sum + 8) >> 4) - bias);
            }
            my += dy;
        }
    }

    /// <summary>Scaled MC (dav1d mc_scaled / mct_scaled): <paramref name="filter2d"/> is the FILTER_2D index (9 = bilinear).
    /// Exactly one of <paramref name="dst"/> (put) / <paramref name="tmp"/> (prep) is non-empty.</summary>
    internal static void McScaled(Span<ushort> dst, int dstStride, Span<short> tmp, ReadOnlySpan<ushort> src, int org,
        int srcStride, int w, int h, int mx, int my, int dx, int dy, int filter2d)
    {
        bool put = !dst.IsEmpty;
        if (filter2d == 9) ScaledBilin(dst, dstStride, tmp, src, org, srcStride, w, h, mx, my, dx, dy, put);
        else Scaled8Tap(dst, dstStride, tmp, src, org, srcStride, w, h, mx, my, dx, dy, PackedFilterType(filter2d), put);
    }

    /// <summary>
    /// Emulate edges for blocks that extend beyond the frame boundary.
    /// Pads by replicating the nearest boundary pixel.
    /// </summary>
    public static void EmuEdge(
        int bw, int bh, int iw, int ih, int x, int y,
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> refBuf, int refStride)
    {
        int refOff = Math.Clamp(y, 0, ih - 1) * refStride + Math.Clamp(x, 0, iw - 1);

        int leftExt = Math.Clamp(-x, 0, bw - 1);
        int rightExt = Math.Clamp(x + bw - iw, 0, bw - 1);
        int topExt = Math.Clamp(-y, 0, bh - 1);
        int bottomExt = Math.Clamp(y + bh - ih, 0, bh - 1);
        int centerW = bw - leftExt - rightExt;
        int centerH = bh - topExt - bottomExt;

        // Copy visible portion
        int blkOff = topExt * dstStride;
        int srcOff = refOff;
        for (int row = 0; row < centerH; row++)
        {
            refBuf.Slice(srcOff, centerW).CopyTo(dst.Slice(blkOff + leftExt, centerW));
            if (leftExt > 0)
                dst.Slice(blkOff, leftExt).Fill(dst[blkOff + leftExt]);
            if (rightExt > 0)
                dst.Slice(blkOff + leftExt + centerW, rightExt).Fill(dst[blkOff + leftExt + centerW - 1]);
            srcOff += refStride;
            blkOff += dstStride;
        }

        // Extend top
        int firstRow = topExt * dstStride;
        for (int row = 0; row < topExt; row++)
            dst.Slice(firstRow, bw).CopyTo(dst.Slice(row * dstStride, bw));

        // Extend bottom
        int lastRow = (topExt + centerH - 1) * dstStride;
        for (int row = 0; row < bottomExt; row++)
            dst.Slice(lastRow, bw).CopyTo(dst.Slice((topExt + centerH + row) * dstStride, bw));
    }

    // ========================================================================
    // Super-resolution resize
    // ========================================================================

    /// <summary>
    /// Super-resolution horizontal resize using 8-tap filter.
    /// </summary>
    public static void Resize(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> src, int srcStride,
        int dstW, int h, int srcW, int dx, int mx0)
    {
        for (int y = 0; y < h; y++)
        {
            int mx = mx0;
            int srcX = -1;
            for (int x = 0; x < dstW; x++)
            {
                int filterPhase = mx >> 8;
                int val = 0;
                for (int t = 0; t < 8; t++)
                    val += Av1Tables.ResizeFilter[filterPhase, t] *
                           src[y * srcStride + Math.Clamp(srcX + t - 3, 0, srcW - 1)];
                dst[y * dstStride + x] = ClipPixel((-val + 64) >> 7);
                mx += dx;
                srcX += mx >> 14;
                mx &= 0x3fff;
            }
        }
    }

    // ========================================================================
    // Pixel clipping
    // ========================================================================

    /// <summary>Pixel bit depth for motion compensation output clamping. The decoder sets this
    /// per frame; the 8-bit encoder leaves it at 8. Used by intra block copy on 10/12-bit frames
    /// (chroma displacement vectors are half-pel → bilinear interpolation → needs the real clamp).
    /// The bilinear intermediate_bits are 4 for both 8- and 10-bit (dav1d get_intermediate_bits),
    /// so only the clamp differs; 12-bit additionally needs intermediate_bits=2 (follow-up).</summary>
    // Per thread: set by the decoding thread before motion compensation (0 on a fresh thread means 8-bit).
    [ThreadStatic] private static int t_mcBitDepth;
    internal static int McBitDepth { get => t_mcBitDepth == 0 ? 8 : t_mcBitDepth; set => t_mcBitDepth = value; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClipPixel(int v) => (ushort)Math.Clamp(v, 0, (1 << McBitDepth) - 1);
}
