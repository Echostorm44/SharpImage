using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// selfguided_avx2.c without AVX2 (the default Native AOT instruction set): every 8-lane AVX2 group as two 4-lane SSE
// halves with the same per-lane operations, the 8-lane prefix scan carried from the low half to the high one, the
// table gather done per lane, and the same 8-group masks and stores, so A, B, flt and the output are the AVX2 kernels'.
internal static partial class AomRestoration
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector128<int> L4(int* p) => Sse2.LoadVector128(p);

    /// <summary><see cref="SelfguidedRestorationAvx2"/> in SSE4.1.</summary>
    public static unsafe void SelfguidedRestorationSse(byte[] dgd8, int d0, int width, int height, int dgdStride,
        int[] flt0, int f0, int[] flt1, int f1, int fltStride, int sgrParamsIdx, SgrScratch sc)
    {
        int widthExt = width + 2 * SgrprojBorderHorz;
        int heightExt = height + 2 * SgrprojBorderVert;
        int bufStride = (widthExt + 16 + 7) & ~7;
        fixed (int* buf = sc.Ii)
        fixed (byte* dgdBase = dgd8)
        fixed (int* flt0Base = flt0)
        fixed (int* flt1Base = flt1)
        {
            int* atl = buf + 0 * SgrBufElts + 7;
            int* btl = buf + 1 * SgrBufElts + 7;
            int* ctl = buf + 2 * SgrBufElts + 7;
            int* dtl = buf + 3 * SgrBufElts + 7;
            int bufDiagBorder = SgrprojBorderHorz + bufStride * SgrprojBorderVert;
            int* A = atl + 1 + bufStride + bufDiagBorder;
            int* B = btl + 1 + bufStride + bufDiagBorder;
            int* C = ctl + 1 + bufStride + bufDiagBorder;
            int* D = dtl + 1 + bufStride + bufDiagBorder;
            byte* dgd = dgdBase + d0;
            byte* dgd0 = dgd - (SgrprojBorderHorz + dgdStride * SgrprojBorderVert);
            IntegralImagesSse(dgd0, dgdStride, widthExt, heightExt, ctl, dtl, bufStride);
            if (SgrR0[sgrParamsIdx] > 0)
            {
                CalcAbSse(A, B, C, D, width, height, bufStride, sgrParamsIdx, 0, 2);
                FinalFilterFastSse(flt0Base + f0, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
            if (SgrR1[sgrParamsIdx] > 0)
            {
                CalcAbSse(A, B, C, D, width, height, bufStride, sgrParamsIdx, 1, 1);
                FinalFilterSse(flt1Base + f1, fltStride, A, B, bufStride, dgd, dgdStride, width, height);
            }
        }
    }

    /// <summary><see cref="ApplySelfguidedAvx2"/> in SSE4.1 (8 samples a step, the same per-sample values).</summary>
    public static unsafe void ApplySelfguidedSse(byte[] dat, int d0, int width, int height, int stride, int ep, int xqd0,
        int xqd1, byte[] dst, int dst0, int dstStride, int[] flt0, int[] flt1, SgrScratch sc)
    {
        SelfguidedRestorationSse(dat, d0, width, height, stride, flt0, 0, flt1, 0, width, ep, sc);
        DecodeXq(xqd0, xqd1, ep, out int xq0i, out int xq1i);
        var xq0 = Vector128.Create(xq0i);
        var xq1 = Vector128.Create(xq1i);
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        const int sh = SgrprojPrjBits + SgrprojRstBits;
        var rounding = Vector128.Create((1 << sh) >> 1);
        fixed (byte* datBase = dat)
        fixed (byte* dstBase = dst)
        fixed (int* f0p = flt0)
        fixed (int* f1p = flt1)
        {
            for (int i = 0; i < height; ++i)
            {
                for (int j = 0; j < width; j += 8)
                {
                    int k = i * width + j;
                    byte* dat8ij = datBase + d0 + i * stride + j;
                    var u0 = Sse2.ShiftLeftLogical(Sse41.ConvertToVector128Int32(dat8ij), SgrprojRstBits);
                    var u1 = Sse2.ShiftLeftLogical(Sse41.ConvertToVector128Int32(dat8ij + 4), SgrprojRstBits);
                    var v0 = Sse2.ShiftLeftLogical(u0, SgrprojPrjBits);
                    var v1 = Sse2.ShiftLeftLogical(u1, SgrprojPrjBits);
                    if (r0)
                    {
                        v0 = Sse2.Add(v0, Sse41.MultiplyLow(xq0, Sse2.Subtract(L4(f0p + k), u0)));
                        v1 = Sse2.Add(v1, Sse41.MultiplyLow(xq0, Sse2.Subtract(L4(f0p + k + 4), u1)));
                    }
                    if (r1)
                    {
                        v0 = Sse2.Add(v0, Sse41.MultiplyLow(xq1, Sse2.Subtract(L4(f1p + k), u0)));
                        v1 = Sse2.Add(v1, Sse41.MultiplyLow(xq1, Sse2.Subtract(L4(f1p + k + 4), u1)));
                    }
                    var w0 = Sse2.ShiftRightArithmetic(Sse2.Add(v0, rounding), sh);
                    var w1 = Sse2.ShiftRightArithmetic(Sse2.Add(v1, rounding), sh);
                    var t = Sse2.PackSignedSaturate(w0, w1);
                    var res = Sse2.PackUnsignedSaturate(t, t);
                    byte* d = dstBase + dst0 + i * dstStride + j;
                    if (j + 8 <= width) *(ulong*)d = res.AsUInt64().ToScalar();
                    else
                    {
                        ulong q = res.AsUInt64().ToScalar();
                        for (int c = 0; c < width - j; c++) d[c] = (byte)(q >> (8 * c));
                    }
                }
            }
        }
    }

    /// <summary>The inclusive prefix sum of 4 int32 lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Scan4(Vector128<int> x)
    {
        var x2 = Sse2.Add(x, Sse2.ShiftLeftLogical128BitLane(x, 4));
        return Sse2.Add(x2, Sse2.ShiftLeftLogical128BitLane(x2, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lane3(Vector128<int> x) => Sse2.Shuffle(x, 0xff);

    private static unsafe void IntegralImagesSse(byte* src, int srcStride, int width, int height, int* A, int* B, int bufStride)
    {
        new Span<int>(A, width + 8).Clear();
        new Span<int>(B, width + 8).Clear();
        int* aAbove = A + 1, bAbove = B + 1;
        for (int i = 0; i < height; ++i, src += srcStride)
        {
            int* aRow = aAbove + bufStride, bRow = bAbove + bufStride;
            aRow[-1] = bRow[-1] = 0;
            Vector128<int> ldiff1 = Vector128<int>.Zero, ldiff2 = Vector128<int>.Zero;
            for (int j = 0; j < width; j += 8)
            {
                var x1lo = Sse41.ConvertToVector128Int32(src + j);
                var x1hi = Sse41.ConvertToVector128Int32(src + j + 4);
                var x2lo = Sse2.MultiplyAddAdjacent(x1lo.AsInt16(), x1lo.AsInt16());
                var x2hi = Sse2.MultiplyAddAdjacent(x1hi.AsInt16(), x1hi.AsInt16());
                var sc1lo = Scan4(x1lo);
                var sc1hi = Sse2.Add(Scan4(x1hi), Lane3(sc1lo));
                var sc2lo = Scan4(x2lo);
                var sc2hi = Sse2.Add(Scan4(x2hi), Lane3(sc2lo));
                var ab1lo = L4(bAbove + j); var ab1hi = L4(bAbove + j + 4);
                var ab2lo = L4(aAbove + j); var ab2hi = L4(aAbove + j + 4);
                var row1lo = Sse2.Add(Sse2.Add(sc1lo, ab1lo), ldiff1);
                var row1hi = Sse2.Add(Sse2.Add(sc1hi, ab1hi), ldiff1);
                var row2lo = Sse2.Add(Sse2.Add(sc2lo, ab2lo), ldiff2);
                var row2hi = Sse2.Add(Sse2.Add(sc2hi, ab2hi), ldiff2);
                Sse2.Store(bRow + j, row1lo); Sse2.Store(bRow + j + 4, row1hi);
                Sse2.Store(aRow + j, row2lo); Sse2.Store(aRow + j + 4, row2hi);
                ldiff1 = Lane3(Sse2.Subtract(row1hi, ab1hi));
                ldiff2 = Lane3(Sse2.Subtract(row2hi, ab2hi));
            }
            aAbove = aRow;
            bAbove = bRow;
        }
    }

    private static unsafe void CalcAbSse(int* A, int* B, int* C, int* D, int width, int height, int bufStride,
        int sgrParamsIdx, int radiusIdx, int step)
    {
        int r = radiusIdx == 0 ? SgrR0[sgrParamsIdx] : SgrR1[sgrParamsIdx];
        int n = (2 * r + 1) * (2 * r + 1);
        var s = Vector128.Create(radiusIdx == 0 ? SgrS0[sgrParamsIdx] : SgrS1[sgrParamsIdx]);
        var nV = Vector128.Create(n);
        var oneOverN = Vector128.Create(OneByX[n - 1]).AsInt16();
        var rndZ = Vector128.Create((1 << SgrprojMtableBits) >> 1);
        var rndRes = Vector128.Create((1 << SgrprojRecipBits) >> 1);
        var c255 = Vector128.Create(255);
        var sgr = Vector128.Create(SgrprojSgr);
        nint oTl = -(r + 1) - (nint)(r + 1) * bufStride, oTr = r - (nint)(r + 1) * bufStride;
        nint oBl = -(r + 1) + (nint)r * bufStride, oBr = r + (nint)r * bufStride;
        int jLast = -1 + ((width + 1) / 8) * 8;
        int idxLast = Math.Min(width + 1 - jLast, 8);
        var iota = Vector128.Create(0, 1, 2, 3);
        var maskLo = Sse2.CompareGreaterThan(Vector128.Create(idxLast), iota);
        var maskHi = Sse2.CompareGreaterThan(Vector128.Create(idxLast - 4), iota);
        int[] tab = AomRestoration.XByXplus1;
        for (int i = -1; i < height + 1; i += step)
        {
            nint rowOff = (nint)i * bufStride;
            int* cRow = C + rowOff, dRow = D + rowOff, aRow = A + rowOff, bRow = B + rowOff;
            for (int j = -1; j < width + 1; j += 8)
                for (int h = 0; h < 8; h += 4)
                {
                    int* cij = cRow + j + h, dij = dRow + j + h;
                    var sum1 = Sse2.Subtract(Sse2.Subtract(L4(dij + oBr), L4(dij + oBl)), Sse2.Subtract(L4(dij + oTr), L4(dij + oTl)));
                    var sum2 = Sse2.Subtract(Sse2.Subtract(L4(cij + oBr), L4(cij + oBl)), Sse2.Subtract(L4(cij + oTr), L4(cij + oTl)));
                    if (j == jLast)
                    {
                        var m = h == 0 ? maskLo : maskHi;
                        sum1 = Sse2.And(m, sum1);
                        sum2 = Sse2.And(m, sum2);
                    }
                    var bb = Sse2.MultiplyAddAdjacent(sum1.AsInt16(), sum1.AsInt16());
                    var an = Sse41.MultiplyLow(sum2, nV);
                    var p = Sse2.Subtract(an, bb);
                    var z = Sse41.Min(Sse2.ShiftRightLogical(Sse2.Add(Sse41.MultiplyLow(p, s), rndZ), SgrprojMtableBits), c255);
                    var aRes = Vector128.Create(tab[z.GetElement(0)], tab[z.GetElement(1)], tab[z.GetElement(2)], tab[z.GetElement(3)]);
                    Sse2.Store(aRow + j + h, aRes);
                    var aCompOverN = Sse2.MultiplyAddAdjacent(Sse2.Subtract(sgr, aRes).AsInt16(), oneOverN);
                    var bInt = Sse41.MultiplyLow(aCompOverN, sum1);
                    Sse2.Store(bRow + j + h, Sse2.ShiftRightLogical(Sse2.Add(bInt, rndRes), SgrprojRecipBits));
                }
        }
    }

    private static unsafe void FinalFilterSse(int* dst, int dstStride, int* A, int* B, int bufStride, byte* dgd8, int dgdStride,
        int width, int height)
    {
        const int nb = 5, sh = SgrprojSgrBits + nb - SgrprojRstBits;
        var rounding = Vector128.Create((1 << sh) >> 1);
        nint up = -bufStride, dn = bufStride;
        int wEnd = (width + 7) & ~7;   // whole 8-sample groups, as the AVX2 kernel stores
        for (int i = 0; i < height; ++i)
        {
            int* aRow = A + (nint)i * bufStride, bRow = B + (nint)i * bufStride, dRow = dst + (nint)i * dstStride;
            byte* sRow = dgd8 + (nint)i * dgdStride;
            for (int j = 0; j < wEnd; j += 4)
            {
                int* a0 = aRow + j, b0 = bRow + j;
                var aFours = Sse2.Add(L4(a0 - 1), Sse2.Add(L4(a0 + up), Sse2.Add(L4(a0 + 1), Sse2.Add(L4(a0 + dn), L4(a0)))));
                var aThrees = Sse2.Add(L4(a0 - 1 + up), Sse2.Add(L4(a0 + 1 + up), Sse2.Add(L4(a0 + 1 + dn), L4(a0 - 1 + dn))));
                var a = Sse2.Subtract(Sse2.ShiftLeftLogical(Sse2.Add(aFours, aThrees), 2), aThrees);
                var bFours = Sse2.Add(L4(b0 - 1), Sse2.Add(L4(b0 + up), Sse2.Add(L4(b0 + 1), Sse2.Add(L4(b0 + dn), L4(b0)))));
                var bThrees = Sse2.Add(L4(b0 - 1 + up), Sse2.Add(L4(b0 + 1 + up), Sse2.Add(L4(b0 + 1 + dn), L4(b0 - 1 + dn))));
                var b = Sse2.Subtract(Sse2.ShiftLeftLogical(Sse2.Add(bFours, bThrees), 2), bThrees);
                var src = Sse41.ConvertToVector128Int32(sRow + j);
                var v = Sse2.Add(Sse2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                Sse2.Store(dRow + j, Sse2.ShiftRightArithmetic(Sse2.Add(v, rounding), sh));
            }
        }
    }

    private static unsafe void FinalFilterFastSse(int* dst, int dstStride, int* A, int* B, int bufStride, byte* dgd8,
        int dgdStride, int width, int height)
    {
        const int nb0 = 5, nb1 = 4, sh0 = SgrprojSgrBits + nb0 - SgrprojRstBits, sh1 = SgrprojSgrBits + nb1 - SgrprojRstBits;
        var rounding0 = Vector128.Create((1 << sh0) >> 1);
        var rounding1 = Vector128.Create((1 << sh1) >> 1);
        nint up = -bufStride, dn = bufStride;
        int wEnd = (width + 7) & ~7;   // whole 8-sample groups, as the AVX2 kernel stores
        for (int i = 0; i < height; ++i)
        {
            int* aRow = A + (nint)i * bufStride, bRow = B + (nint)i * bufStride, dRow = dst + (nint)i * dstStride;
            byte* sRow = dgd8 + (nint)i * dgdStride;
            if ((i & 1) == 0)
            {
                for (int j = 0; j < wEnd; j += 4)
                {
                    int* a0 = aRow + j, b0 = bRow + j;
                    var aFives = Sse2.Add(L4(a0 - 1 + up), Sse2.Add(L4(a0 + 1 + up), Sse2.Add(L4(a0 + 1 + dn), L4(a0 - 1 + dn))));
                    var aSixes = Sse2.Add(L4(a0 + up), L4(a0 + dn));
                    var aFs = Sse2.Add(aFives, aSixes);
                    var a = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(aFs, 2), aFs), aSixes);
                    var bFives = Sse2.Add(L4(b0 - 1 + up), Sse2.Add(L4(b0 + 1 + up), Sse2.Add(L4(b0 + 1 + dn), L4(b0 - 1 + dn))));
                    var bSixes = Sse2.Add(L4(b0 + up), L4(b0 + dn));
                    var bFs = Sse2.Add(bFives, bSixes);
                    var b = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(bFs, 2), bFs), bSixes);
                    var src = Sse41.ConvertToVector128Int32(sRow + j);
                    var v = Sse2.Add(Sse2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                    Sse2.Store(dRow + j, Sse2.ShiftRightArithmetic(Sse2.Add(v, rounding0), sh0));
                }
            }
            else
            {
                for (int j = 0; j < wEnd; j += 4)
                {
                    int* a0 = aRow + j, b0 = bRow + j;
                    var aSixes = L4(a0);
                    var aFs = Sse2.Add(Sse2.Add(L4(a0 - 1), L4(a0 + 1)), aSixes);
                    var a = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(aFs, 2), aFs), aSixes);
                    var bSixes = L4(b0);
                    var bFs = Sse2.Add(Sse2.Add(L4(b0 - 1), L4(b0 + 1)), bSixes);
                    var b = Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(bFs, 2), bFs), bSixes);
                    var src = Sse41.ConvertToVector128Int32(sRow + j);
                    var v = Sse2.Add(Sse2.MultiplyAddAdjacent(a.AsInt16(), src.AsInt16()), b);
                    Sse2.Store(dRow + j, Sse2.ShiftRightArithmetic(Sse2.Add(v, rounding1), sh1));
                }
            }
        }
    }
}
