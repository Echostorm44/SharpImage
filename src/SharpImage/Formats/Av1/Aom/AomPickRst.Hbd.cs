using System;
using static SharpImage.Formats.Av1.AomRestoration;

namespace SharpImage.Formats.Av1;

// The high bit depth paths of libaom 3.14.1 av1/encoder/pickrst.c: av1_compute_stats_highbd (the AVX2 win7 / win5 kernels
// compute exact integer sums: the C's values, M and H divided by 4 at 10 bits, 16 at 12), the self-guided search on
// 16-bit planes with av1_calc_proj_params_high_bd (exact 64-bit sums) and av1_highbd_pixel_proj_error_avx2 (16-bit
// lanes: the projected sample saturates to int16 and the error wraps in int16 before it is squared).
internal sealed partial class AomPickRst
{
    /// <summary>find_average_highbd.</summary>
    private static ushort FindAverageHbd(AomYv12Plane p, int hStart, int hEnd, int vStart, int vEnd)
    {
        ulong sum = 0;
        for (int i = vStart; i < vEnd; i++)
        {
            int r = p.At(0, i);
            for (int j = hStart; j < hEnd; j++) sum += p.Buf16[r + j];
        }
        return (ushort)(sum / (ulong)((vEnd - vStart) * (hEnd - hStart)));
    }

    /// <summary>av1_compute_stats_highbd.</summary>
    public static void ComputeStatsHbd(int wienerWin, AomYv12Plane dgd, AomYv12Plane src, int hStart, int hEnd, int vStart, int vEnd,
        long[] M, long[] H, int bd)
    {
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && wienerWin <= 7)
            ComputeStatsHbdAvx2(wienerWin, dgd, src, hStart, hEnd, vStart, vEnd, M, H, bd);
        else ComputeStatsHbdScalar(wienerWin, dgd, src, hStart, hEnd, vStart, vEnd, M, H, bd);
    }

    internal static void ComputeStatsHbdScalar(int wienerWin, AomYv12Plane dgd, AomYv12Plane src, int hStart, int hEnd, int vStart, int vEnd,
        long[] M, long[] H, int bd)
    {
        int wienerWin2 = wienerWin * wienerWin, halfwin = wienerWin >> 1;
        long avg = FindAverageHbd(dgd, hStart, hEnd, vStart, vEnd);
        int divider = bd == 12 ? 16 : bd == 10 ? 4 : 1;
        Array.Clear(M, 0, wienerWin2);
        Array.Clear(H, 0, wienerWin2 * wienerWin2);
        Span<long> y = stackalloc long[wienerWin2];
        ushort[] d = dgd.Buf16, s = src.Buf16;
        Span<int> rowBase = stackalloc int[WienerWin];
        for (int i = vStart; i < vEnd; i++)
        {
            int srow = src.At(0, i);
            for (int l = -halfwin; l <= halfwin; l++) rowBase[l + halfwin] = dgd.At(0, i + l);
            for (int j = hStart; j < hEnd; j++)
            {
                long x = s[srow + j] - avg;
                int idx = 0;
                for (int k = -halfwin; k <= halfwin; k++)
                    for (int l = 0; l < wienerWin; l++) y[idx++] = d[rowBase[l] + j + k] - avg;
                for (int k = 0; k < wienerWin2; ++k)
                {
                    long yk = y[k];
                    M[k] += yk * x;
                    int hb = k * wienerWin2;
                    for (int l = k; l < wienerWin2; ++l) H[hb + l] += yk * y[l];
                }
            }
        }
        for (int k = 0; k < wienerWin2; ++k)
        {
            M[k] /= divider;
            for (int l = k; l < wienerWin2; ++l) H[k * wienerWin2 + l] /= divider;
            for (int l = k + 1; l < wienerWin2; ++l) H[l * wienerWin2 + k] = H[k * wienerWin2 + l];
        }
    }

    /// <summary>av1_highbd_pixel_proj_error (the AVX2 kernel on whole 16-sample groups, the C after them).</summary>
    private static long HighbdPixelProjError(ushort[] src, int s0, int width, int height, int srcStride, ushort[] dat, int d0,
        int datStride, int[] flt0, int f0, int flt0Stride, int[] flt1, int f1, int flt1Stride, int xq0, int xq1, int ep)
    {
        const int shift = SgrprojRstBits + SgrprojPrjBits;
        const int rounding = 1 << (shift - 1);
        long err = 0;
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        int xqOn = r0 ? xq0 : xq1;
        int[] flt = r0 ? flt0 : flt1;
        int fo = r0 ? f0 : f1, fStride = r0 ? flt0Stride : flt1Stride;
        for (int i = 0; i < height; ++i)
        {
            int sr = s0 + i * srcStride, dr = d0 + i * datStride;
            int j = 0;
            if (r0 && r1)
            {
                int a0 = f0 + i * flt0Stride, a1 = f1 + i * flt1Stride;
                int groupEnd = width & ~15;
                for (; j < groupEnd; j++)
                {
                    int dv = dat[dr + j];
                    int u = (ushort)(dv << SgrprojRstBits);
                    int v = unchecked(xq0 * (flt0[a0 + j] - u) + xq1 * (flt1[a1 + j] - u));
                    int vr = Math.Clamp((v + rounding) >> shift, short.MinValue, short.MaxValue);
                    short e = (short)(vr + dv - src[sr + j]);
                    err += e * e;
                }
                for (int k = j; k < width; ++k)
                {
                    int dv = dat[dr + k];
                    int u = dv << SgrprojRstBits;
                    int v = xq0 * (flt0[a0 + k] - u) + xq1 * (flt1[a1 + k] - u);
                    int e = ((v + rounding) >> shift) + dv - src[sr + k];
                    err += (long)e * e;
                }
            }
            else if (r0 || r1)
            {
                int a = fo + i * fStride;
                int groupEnd = width & ~15;
                for (; j < groupEnd; j++)
                {
                    int dv = dat[dr + j];
                    int v = unchecked(flt[a + j] * xqOn + dv * (-xqOn * (1 << SgrprojRstBits)));
                    int vr = Math.Clamp((v + rounding) >> shift, short.MinValue, short.MaxValue);
                    short e = (short)(vr + dv - src[sr + j]);
                    err += e * e;
                }
                for (int k = j; k < width; ++k)
                {
                    int dv = dat[dr + k];
                    int u = dv << SgrprojRstBits;
                    int v = xqOn * (flt[a + k] - u);
                    int e = ((v + rounding) >> shift) + dv - src[sr + k];
                    err += (long)e * e;
                }
            }
            else
            {
                for (int k = 0; k < width; ++k)
                {
                    int e = dat[dr + k] - src[sr + k];
                    err += (long)e * e;
                }
            }
        }
        return err;
    }

    /// <summary>av1_calc_proj_params_high_bd (exact 64-bit sums, divided by the sample count).</summary>
    private static void CalcProjParamsHbd(ushort[] src, int s0, int width, int height, int srcStride, ushort[] dat, int d0,
        int datStride, int[] flt0, int f0, int flt0Stride, int[] flt1, int f1, int flt1Stride, Span<long> H, Span<long> C, int ep)
    {
        int size = width * height;
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        long h00 = 0, h01 = 0, h11 = 0, c0 = 0, c1 = 0;
        for (int i = 0; i < height; ++i)
            for (int j = 0; j < width; ++j)
            {
                int u = dat[d0 + i * datStride + j] << SgrprojRstBits;
                int sv = (src[s0 + i * srcStride + j] << SgrprojRstBits) - u;
                if (r0)
                {
                    long fa = flt0[f0 + i * flt0Stride + j] - u;
                    h00 += fa * fa;
                    c0 += fa * sv;
                    if (r1)
                    {
                        long fb = flt1[f1 + i * flt1Stride + j] - u;
                        h01 += fa * fb;
                        h11 += fb * fb;
                        c1 += fb * sv;
                    }
                }
                else if (r1)
                {
                    long fb = flt1[f1 + i * flt1Stride + j] - u;
                    h11 += fb * fb;
                    c1 += fb * sv;
                }
            }
        if (r0 && r1)
        {
            H[0] = h00 / size; H[1] = h01 / size; H[3] = h11 / size; H[2] = H[1];
            C[0] = c0 / size; C[1] = c1 / size;
        }
        else if (r0) { H[0] = h00 / size; C[0] = c0 / size; }
        else if (r1) { H[3] = h11 / size; C[1] = c1 / size; }
    }

    private static void GetProjSubspaceHbd(ushort[] src, int s0, int width, int height, int srcStride, ushort[] dat, int d0,
        int datStride, int[] flt0, int[] flt1, int fltStride, out int xq0, out int xq1, int ep)
    {
        Span<long> H = stackalloc long[4];
        Span<long> C = stackalloc long[2];
        H.Clear();
        C.Clear();
        xq0 = 0;
        xq1 = 0;
        CalcProjParamsHbd(src, s0, width, height, srcStride, dat, d0, datStride, flt0, 0, fltStride, flt1, 0, fltStride, H, C, ep);
        SolveProj(H, C, ep, out xq0, out xq1);
    }

    private static long FinerSearchPixelProjErrorHbd(ushort[] src, int s0, int width, int height, int srcStride, ushort[] dat,
        int d0, int datStride, int[] flt0, int[] flt1, int fltStride, int startStep, Span<int> xqd, int ep)
    {
        long Err(Span<int> q)
        {
            DecodeXq(q[0], q[1], ep, out int a, out int b);
            return HighbdPixelProjError(src, s0, width, height, srcStride, dat, d0, datStride, flt0, 0, fltStride, flt1, 0, fltStride, a, b, ep);
        }
        long err = Err(xqd);
        Span<int> tapMin = stackalloc int[] { SgrprojPrjMin0, SgrprojPrjMin1 };
        Span<int> tapMax = stackalloc int[] { SgrprojPrjMax0, SgrprojPrjMax1 };
        for (int s = startStep; s >= 1; s >>= 1)
            for (int p = 0; p < 2; ++p)
            {
                if ((SgrR0[ep] == 0 && p == 0) || (SgrR1[ep] == 0 && p == 1)) continue;
                bool skip = false;
                while (true)
                {
                    if (xqd[p] - s >= tapMin[p])
                    {
                        xqd[p] -= s;
                        long err2 = Err(xqd);
                        if (err2 > err) xqd[p] += s;
                        else
                        {
                            err = err2;
                            skip = true;
                            if (s == startStep) continue;
                        }
                    }
                    break;
                }
                if (skip) break;
                while (true)
                {
                    if (xqd[p] + s <= tapMax[p])
                    {
                        xqd[p] += s;
                        long err2 = Err(xqd);
                        if (err2 > err) xqd[p] -= s;
                        else
                        {
                            err = err2;
                            if (s == startStep) continue;
                        }
                    }
                    break;
                }
            }
        return err;
    }

    /// <summary>get_proj_subspace's solve (shared with the 8-bit search's arithmetic).</summary>
    private static void SolveProj(ReadOnlySpan<long> H, ReadOnlySpan<long> C, int ep, out int xq0, out int xq1)
    {
        xq0 = 0;
        xq1 = 0;
        if (SgrR0[ep] == 0)
        {
            long det = H[3];
            if (det == 0) return;
            xq1 = (int)SignedRoundedDivide(C[1] * (1 << SgrprojPrjBits), det);
        }
        else if (SgrR1[ep] == 0)
        {
            long det = H[0];
            if (det == 0) return;
            xq0 = (int)SignedRoundedDivide(C[0] * (1 << SgrprojPrjBits), det);
        }
        else
        {
            long det = unchecked(H[0] * H[3] - H[1] * H[2]);
            if (det == 0) return;
            long div1 = unchecked(H[3] * C[0] - H[1] * C[1]);
            if ((div1 > 0 && long.MaxValue / (1 << SgrprojPrjBits) < div1) || (div1 < 0 && long.MinValue / (1 << SgrprojPrjBits) > div1))
                xq0 = (int)SignedRoundedDivide(div1, det / (1 << SgrprojPrjBits));
            else
                xq0 = (int)SignedRoundedDivide(div1 * (1 << SgrprojPrjBits), det);
            long div2 = unchecked(H[0] * C[1] - H[2] * C[0]);
            if ((div2 > 0 && long.MaxValue / (1 << SgrprojPrjBits) < div2) || (div2 < 0 && long.MinValue / (1 << SgrprojPrjBits) > div2))
                xq1 = (int)SignedRoundedDivide(div2, det / (1 << SgrprojPrjBits));
            else
                xq1 = (int)SignedRoundedDivide(div2 * (1 << SgrprojPrjBits), det);
        }
    }

    /// <summary>search_selfguided_restoration on 16-bit planes.</summary>
    public static AomSgrprojInfo SearchSelfguidedHbd(ushort[] dat, int d0, int width, int height, int datStride, ushort[] src, int s0,
        int srcStride, int puWidth, int puHeight, int[] flt0, int[] flt1, int enableSgrEpPruning, SgrScratch sc, int bd)
    {
        int bestep = 0;
        long besterr = -1;
        Span<int> exqd = stackalloc int[2];
        Span<int> bestxqd = stackalloc int[2];
        bestxqd.Clear();
        int fltStride = ((width + 7) & ~7) + 8;

        void Eval(int ep, Span<int> exqd, Span<int> bestxqd, ref long besterr, ref int bestep)
        {
            for (int i = 0; i < height; i += puHeight)
            {
                int h = Math.Min(puHeight, height - i);
                for (int j = 0; j < width; j += puWidth)
                {
                    int w = Math.Min(puWidth, width - j);
                    SelfguidedRestorationHbd(dat, d0 + i * datStride + j, w, h, datStride, flt0, i * fltStride + j, flt1,
                        i * fltStride + j, fltStride, ep, sc, bd);
                }
            }
            GetProjSubspaceHbd(src, s0, width, height, srcStride, dat, d0, datStride, flt0, flt1, fltStride, out int xq0, out int xq1, ep);
            EncodeXq(xq0, xq1, out exqd[0], out exqd[1], ep);
            long err = FinerSearchPixelProjErrorHbd(src, s0, width, height, srcStride, dat, d0, datStride, flt0, flt1, fltStride, 2, exqd, ep);
            if (besterr == -1 || err < besterr)
            {
                bestep = ep;
                besterr = err;
                bestxqd[0] = exqd[0];
                bestxqd[1] = exqd[1];
            }
        }

        if (enableSgrEpPruning == 0)
        {
            for (int ep = 0; ep < SgrprojParams; ep++) Eval(ep, exqd, bestxqd, ref besterr, ref bestep);
        }
        else
        {
            for (int idx = 0; idx < SgprojEpGrp1Seed.Length; idx++) Eval(SgprojEpGrp1Seed[idx], exqd, bestxqd, ref besterr, ref bestep);
            if (enableSgrEpPruning < 2)
            {
                int bestepRef = bestep;
                for (int ep = bestepRef - 1; ep < bestepRef + 2; ep += 2)
                {
                    if (ep < 0 || ep > 9) continue;
                    Eval(ep, exqd, bestxqd, ref besterr, ref bestep);
                }
                for (int idx = 0; idx < 2; idx++) Eval(SgprojEpGrp23[idx, bestep], exqd, bestxqd, ref besterr, ref bestep);
            }
        }
        return new AomSgrprojInfo { Ep = bestep, Xqd0 = bestxqd[0], Xqd1 = bestxqd[1] };
    }
}
