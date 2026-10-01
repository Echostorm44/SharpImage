using System;

namespace SharpImage.Formats.Av1;

// The high bit depth CDEF of libaom 3.14.1: av1_cdef_filter_fb's 16-bit output (cdef_filter_16_*; CDEF is normative, so
// the AVX2 kernels give the C's values), coeff_shift = bd - 8, and pickcdef.c's highbd get_filt_error (the filter's
// packed per-block output against the source, compute_cdef_dist_highbd: aom_mse_wxh_16bit_highbd sums shifted down by
// 2 * coeff_shift).
internal static partial class AomCdef
{
    /// <summary>cdef_filter_block_internal with a 16-bit output.</summary>
    private static void FilterBlock16(ushort[] dst, int dstOff, int dstride, ushort[] inb, int inOff, int priStrength, int secStrength, int dir,
        int priDamping, int secDamping, int coeffShift, int bw, int bh, bool enablePrimary, bool enableSecondary)
    {
        bool clippingRequired = enablePrimary && enableSecondary;
        int tapSet = ((priStrength >> coeffShift) & 1) * 2;
        const int s = BStride;
        for (int i = 0; i < bh; i++)
            for (int j = 0; j < bw; j++)
            {
                short sum = 0;
                int p = inOff + i * s + j;
                short x = (short)inb[p];
                int max = x, min = x;
                for (int k = 0; k < 2; k++)
                {
                    if (enablePrimary)
                    {
                        int d = Direction(dir, k);
                        short p0 = (short)inb[p + d], p1 = (short)inb[p - d];
                        sum += (short)(PriTaps[tapSet + k] * Constrain(p0 - x, priStrength, priDamping));
                        sum += (short)(PriTaps[tapSet + k] * Constrain(p1 - x, priStrength, priDamping));
                        if (clippingRequired)
                        {
                            if (p0 != VeryLarge) max = Math.Max(p0, max);
                            if (p1 != VeryLarge) max = Math.Max(p1, max);
                            min = Math.Min(p0, min);
                            min = Math.Min(p1, min);
                        }
                    }
                    if (enableSecondary)
                    {
                        int d2 = Direction(dir + 2, k), d3 = Direction(dir - 2, k);
                        short s0 = (short)inb[p + d2], s1 = (short)inb[p - d2], s2 = (short)inb[p + d3], s3 = (short)inb[p - d3];
                        if (clippingRequired)
                        {
                            if (s0 != VeryLarge) max = Math.Max(s0, max);
                            if (s1 != VeryLarge) max = Math.Max(s1, max);
                            if (s2 != VeryLarge) max = Math.Max(s2, max);
                            if (s3 != VeryLarge) max = Math.Max(s3, max);
                            min = Math.Min(s0, min); min = Math.Min(s1, min); min = Math.Min(s2, min); min = Math.Min(s3, min);
                        }
                        sum += (short)(SecTaps[k] * Constrain(s0 - x, secStrength, secDamping));
                        sum += (short)(SecTaps[k] * Constrain(s1 - x, secStrength, secDamping));
                        sum += (short)(SecTaps[k] * Constrain(s2 - x, secStrength, secDamping));
                        sum += (short)(SecTaps[k] * Constrain(s3 - x, secStrength, secDamping));
                    }
                }
                short y = (short)(x + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4));
                if (clippingRequired) y = (short)Math.Clamp((int)y, min, max);
                dst[dstOff + i * dstride + j] = (ushort)y;
            }
    }

    /// <summary>av1_cdef_filter_fb with a 16-bit output: packed (the search's dirinit call: block bi at bi &lt;&lt; (bw + bh),
    /// stride 1 &lt;&lt; bw, a zero-strength call copies the input) or into a frame plane.</summary>
    internal static void FilterFb16(ushort[] dst16, int dstOff, int dstride, ushort[] inb, int inOff, int xdec, int ydec, int[] dir, ref bool dirinit,
        bool useDirinit, int[] var, int pli, (byte by, byte bx)[] dlist, int cdefCount, int level, int secStrength, int damping, int coeffShift)
    {
        int priStrength = level << coeffShift;
        secStrength <<= coeffShift;
        damping += coeffShift - (pli != 0 ? 1 : 0);
        int bwLog2 = 3 - xdec, bhLog2 = 3 - ydec;
        if (useDirinit && priStrength == 0 && secStrength == 0)
        {
            for (int bi = 0; bi < cdefCount; bi++)
            {
                int by = dlist[bi].by, bx = dlist[bi].bx;
                for (int iy = 0; iy < 1 << bhLog2; iy++)
                    Array.Copy(inb, inOff + ((by << bhLog2) + iy) * BStride + (bx << bwLog2), dst16, dstOff + (bi << (bwLog2 + bhLog2)) + (iy << bwLog2),
                        1 << bwLog2);
            }
            return;
        }
        if (pli == 0 && (!useDirinit || !dirinit))
        {
            FindDirs(inb, inOff, dlist, cdefCount, dir, var, coeffShift);
            if (useDirinit) dirinit = true;
        }
        if (pli == 1 && xdec != ydec)
        {
            int[] conv422 = { 7, 0, 2, 4, 5, 6, 6, 6 }, conv440 = { 1, 2, 2, 2, 3, 4, 6, 0 };
            for (int bi = 0; bi < cdefCount; bi++)
            {
                int by = dlist[bi].by, bx = dlist[bi].bx;
                dir[by * NBlocks + bx] = (xdec != 0 ? conv422 : conv440)[dir[by * NBlocks + bx]];
            }
        }
        int bw = 8 >> xdec, bh = 8 >> ydec;
        for (int bi = 0; bi < cdefCount; bi++)
        {
            int by = dlist[bi].by, bx = dlist[bi].bx;
            int t = pli != 0 ? priStrength : AdjustStrength(priStrength, var[by * NBlocks + bx]);
            bool enablePrimary = t != 0, enableSecondary = secStrength != 0;
            int o = useDirinit ? dstOff + (bi << (bwLog2 + bhLog2)) : dstOff + (by << bhLog2) * dstride + (bx << bwLog2);
            int st = useDirinit ? 1 << bwLog2 : dstride;
            FilterBlock16(dst16, o, st, inb, inOff + (by * BStride << bhLog2) + (bx << bwLog2), t, secStrength,
                priStrength != 0 ? dir[by * NBlocks + bx] : 0, damping, damping, coeffShift, bw, bh, enablePrimary, enableSecondary);
        }
    }

    /// <summary>get_filt_error for a high bit depth frame: av1_cdef_filter_fb into the packed 16-bit buffer, then
    /// compute_cdef_dist_highbd against the source plane at (row, col).</summary>
    private static ulong FiltErrorHbd(AomYv12Plane rp, int row, int col, ushort[] tmpDst16, ushort[] inbuf, int inOff, int xdec, int ydec,
        int[] dir, ref bool dirinit, int[] var, int pli, (byte by, byte bx)[] dlist, int cdefCount, int pri, int sec, int damping, int coeffShift)
    {
        FilterFb16(tmpDst16, 0, BStride, inbuf, inOff, xdec, ydec, dir, ref dirinit, true, var, pli, dlist, cdefCount, pri, sec + (sec == 3 ? 1 : 0),
            damping, coeffShift);
        int wl = 3 - xdec, hl = 3 - ydec, w = 1 << wl, h = 1 << hl;
        ulong sum = 0;
        ushort[] r = rp.Buf16;
        for (int bi = 0; bi < cdefCount; bi++)
        {
            int ro = rp.At(col + (dlist[bi].bx << wl), row + (dlist[bi].by << hl));
            int so = bi << (hl + wl);
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    int e = r[ro + i * rp.Stride + j] - tmpDst16[so + i * w + j];
                    sum += (ulong)(e * e);
                }
        }
        return sum >> (2 * coeffShift);
    }
}
