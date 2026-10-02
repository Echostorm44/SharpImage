using System;

namespace SharpImage.Formats.Av1;

/// <summary>ConvolveParams.</summary>
internal struct AomConvParams
{
    public int DoAverage;
    public ushort[]? Dst;   // CONV_BUF_TYPE *dst (compound intermediate)
    public int DstOffset, DstStride;
    public int Round0, Round1, Plane, IsCompound, UseDistWtdCompAvg, FwdOffset, BckOffset;

    /// <summary>get_conv_params_no_round.</summary>
    public static AomConvParams NoRound(int cmpIndex, int plane, ushort[]? dst, int dstStride, bool isCompound, int bd)
    {
        var c = new AomConvParams
        {
            IsCompound = isCompound ? 1 : 0, UseDistWtdCompAvg = 0, Round0 = 3,   // ROUND0_BITS
            Round1 = isCompound ? 7 : 2 * 7 - 3,                                     // COMPOUND_ROUND1_BITS
        };
        int intbufrange = bd + 7 - c.Round0 + 2;
        if (intbufrange > 16)
        {
            c.Round0 += intbufrange - 16;
            if (!isCompound) c.Round1 -= intbufrange - 16;
        }
        c.Dst = dst;
        c.DstStride = dstStride;
        c.Plane = plane;
        c.DoAverage = cmpIndex;
        return c;
    }

    /// <summary>get_conv_params.</summary>
    public static AomConvParams Get(int doAverage, int plane, int bd) => NoRound(doAverage, plane, null, 0, false, bd);
}

// Port of libaom 3.14.1 av1/common/convolve.c (8-bit and high bit depth C kernels; libaom's SIMD versions are
// bit-exact with them): av1_convolve_{2d,x,y}_sr, av1_dist_wtd_convolve_{2d,x,y,2d_copy}, av1_convolve_2d_scale,
// aom_convolve_copy and av1_convolve_2d_facade.
internal static class AomConvolve
{
    private const int FILTER_BITS = 7, SUBPEL_MASK = 15, SCALE_SUBPEL_BITS = 10, SCALE_SUBPEL_MASK = (1 << SCALE_SUBPEL_BITS) - 1,
        SCALE_EXTRA_BITS = 6, DIST_PRECISION_BITS = 4, MAX_SB_SIZE = 128, MAX_FILTER_TAP = 12;

    private static int Rpt(int v, int n) => (v + ((1 << n) >> 1)) >> n;   // ROUND_POWER_OF_TWO (n may be 0)
    private static byte ClipPixel(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    private static ushort ClipHbd(int v, int bd) { int max = (1 << bd) - 1; return (ushort)(v < 0 ? 0 : v > max ? max : v); }

    [ThreadStatic] private static short[]? t_im;
    private static short[] Im => t_im ??= new short[(2 * MAX_SB_SIZE + MAX_FILTER_TAP) * MAX_SB_SIZE];

    /// <summary>av1_convolve_2d_facade (8-bit).</summary>
    public static void Facade(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fx, AomFilter.Params fy, int subpelXQn, int xStepQ4, int subpelYQn, int yStepQ4, bool scaled, ref AomConvParams cp)
    {
        if (scaled) Convolve2dScale(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, xStepQ4, subpelYQn, yStepQ4, ref cp);
        else if (cp.IsCompound != 0)
        {
            bool needX = subpelXQn != 0, needY = subpelYQn != 0;
            if (!needX && !needY) DistWtd2dCopy(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, ref cp);
            else if (needX && !needY) DistWtdX(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, subpelXQn, ref cp);
            else if (!needX && needY) DistWtdY(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fy, subpelYQn, ref cp);
            else DistWtd2d(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, subpelYQn, ref cp);
        }
        else
        {
            bool needX = subpelXQn != 0, needY = subpelYQn != 0;
            if (!needX && !needY)
                for (int r = 0; r < h; r++) Buffer.BlockCopy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
            else if (needX && !needY) ConvolveXSr(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, subpelXQn, ref cp);
            else if (!needX && needY) ConvolveYSr(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fy, subpelYQn);
            else Convolve2dSr(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, subpelYQn, ref cp);
        }
    }

    public static void Convolve2dSr(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int subpelYQn, ref AomConvParams cp)
    {
        var im = Im;
        int imH = h + fpy.Taps - 1, imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        const int bd = 8;
        int bits = FILTER_BITS * 2 - cp.Round0 - cp.Round1;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < imH; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << (bd + FILTER_BITS - 1);
                int s = srcHoriz + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[s + k];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
        int srcVert = foVert * imStride;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcVert + (y - foVert + k) * imStride + x];
                short res = (short)(Rpt(sum, cp.Round1) - sub);
                dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(res, bits));
            }
    }

    public static void ConvolveYSr(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpy, int subpelYQn)
    {
        int foVert = fpy.Taps / 2 - 1;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                for (int k = 0; k < fpy.Taps; ++k) res += yf[yk + k] * src[srcOff + (y - foVert + k) * srcStride + x];
                dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(res, FILTER_BITS));
            }
    }

    public static void ConvolveXSr(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, int subpelXQn, ref AomConvParams cp)
    {
        int foHoriz = fpx.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round0;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                int s = srcOff + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) res += xf[xk + k] * src[s + k];
                res = Rpt(res, cp.Round0);
                dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(res, bits));
            }
    }

    private static int AvgDist(int tmp, int res, ref AomConvParams cp)
    {
        if (cp.UseDistWtdCompAvg != 0) return (tmp * cp.FwdOffset + res * cp.BckOffset) >> DIST_PRECISION_BITS;
        return (tmp + res) >> 1;
    }

    public static void DistWtd2d(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int subpelYQn, ref AomConvParams cp)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        var im = Im;
        int imH = h + fpy.Taps - 1, imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        const int bd = 8;
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < imH; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << (bd + FILTER_BITS - 1);
                int s = srcHoriz + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[s + k];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
        int srcVert = foVert * imStride;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcVert + (y - foVert + k) * imStride + x];
                ushort res = (ushort)Rpt(sum, cp.Round1);
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - sub;
                    dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, roundBits));
                }
                else dst16[d16 + y * d16s + x] = res;
            }
    }

    public static void DistWtdY(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpy, int subpelYQn, ref AomConvParams cp)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int foVert = fpy.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round0;
        const int bd = 8;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                for (int k = 0; k < fpy.Taps; ++k) res += yf[yk + k] * src[srcOff + (y - foVert + k) * srcStride + x];
                res *= 1 << bits;
                res = Rpt(res, cp.Round1) + roundOffset;
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, roundBits));
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void DistWtdX(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, int subpelXQn, ref AomConvParams cp)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int foHoriz = fpx.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round1;
        const int bd = 8;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                int s = srcOff + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) res += xf[xk + k] * src[s + k];
                res = (1 << bits) * Rpt(res, cp.Round0);
                res += roundOffset;
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, roundBits));
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void DistWtd2dCopy(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        ref AomConvParams cp)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int bits = FILTER_BITS * 2 - cp.Round1 - cp.Round0;
        const int bd = 8;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = (src[srcOff + y * srcStride + x] << bits) + roundOffset;
                res &= 0xffff;   // CONV_BUF_TYPE
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, bits));
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void Convolve2dScale(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int xStepQn, int subpelYQn, int yStepQn, ref AomConvParams cp)
    {
        var im = Im;
        int imH = (((h - 1) * yStepQn + subpelYQn) >> SCALE_SUBPEL_BITS) + fpy.Taps;
        var dst16 = cp.Dst; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int bits = FILTER_BITS * 2 - cp.Round0 - cp.Round1;
        int imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        const int bd = 8;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter;
        for (int y = 0; y < imH; ++y)
        {
            int xQn = subpelXQn;
            for (int x = 0; x < w; ++x, xQn += xStepQn)
            {
                int srcX = srcHoriz + (xQn >> SCALE_SUBPEL_BITS);
                int xFilterIdx = (xQn & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS;
                int xk = fpx.Kernel(xFilterIdx);
                int sum = 1 << (bd + FILTER_BITS - 1);
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[srcX + k - foHoriz];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
            srcHoriz += srcStride;
        }
        int srcVert = foVert * imStride;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        var yf = fpy.Filter;
        for (int x = 0; x < w; ++x)
        {
            int yQn = subpelYQn;
            for (int y = 0; y < h; ++y, yQn += yStepQn)
            {
                int srcY = srcVert + (yQn >> SCALE_SUBPEL_BITS) * imStride;
                int yFilterIdx = (yQn & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS;
                int yk = fpy.Kernel(yFilterIdx);
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcY + (k - foVert) * imStride];
                ushort res = (ushort)Rpt(sum, cp.Round1);
                if (cp.IsCompound != 0)
                {
                    if (cp.DoAverage != 0)
                    {
                        int tmp = AvgDist(dst16![d16 + y * d16s + x], res, ref cp) - sub;
                        dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, bits));
                    }
                    else dst16![d16 + y * d16s + x] = res;
                }
                else
                {
                    int tmp = res - sub;
                    dst[dstOff + y * dstStride + x] = ClipPixel(Rpt(tmp, bits));
                }
            }
            srcVert++;
        }
    }

    // ---- high bit depth ---------------------------------------------------------------------------------------------

    /// <summary>av1_highbd_convolve_2d_facade.</summary>
    public static void FacadeHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fx, AomFilter.Params fy, int subpelXQn, int xStepQ4, int subpelYQn, int yStepQ4, bool scaled, ref AomConvParams cp, int bd)
    {
        if (scaled) Convolve2dScaleHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, xStepQ4, subpelYQn, yStepQ4, ref cp, bd);
        else if (cp.IsCompound != 0)
        {
            bool needX = subpelXQn != 0, needY = subpelYQn != 0;
            if (!needX && !needY) DistWtd2dCopyHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, ref cp, bd);
            else if (needX && !needY) DistWtdXHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, subpelXQn, ref cp, bd);
            else if (!needX && needY) DistWtdYHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fy, subpelYQn, ref cp, bd);
            else DistWtd2dHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, subpelYQn, ref cp, bd);
        }
        else
        {
            bool needX = subpelXQn != 0, needY = subpelYQn != 0;
            if (!needX && !needY)
                for (int r = 0; r < h; r++) Array.Copy(src, srcOff + r * srcStride, dst, dstOff + r * dstStride, w);
            else if (needX && !needY) ConvolveXSrHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, subpelXQn, ref cp, bd);
            else if (!needX && needY) ConvolveYSrHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fy, subpelYQn, bd);
            else Convolve2dSrHbd(src, srcOff, srcStride, dst, dstOff, dstStride, w, h, fx, fy, subpelXQn, subpelYQn, ref cp, bd);
        }
    }

    public static void Convolve2dSrHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int subpelYQn, ref AomConvParams cp, int bd)
    {
        var im = Im;
        int imH = h + fpy.Taps - 1, imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        int bits = FILTER_BITS * 2 - cp.Round0 - cp.Round1;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < imH; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << (bd + FILTER_BITS - 1);
                int s = srcHoriz + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[s + k];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
        int srcVert = foVert * imStride;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcVert + (y - foVert + k) * imStride + x];
                int res = Rpt(sum, cp.Round1) - sub;
                dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(res, bits), bd);
            }
    }

    public static void ConvolveYSrHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpy, int subpelYQn, int bd)
    {
        int foVert = fpy.Taps / 2 - 1;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                for (int k = 0; k < fpy.Taps; ++k) res += yf[yk + k] * src[srcOff + (y - foVert + k) * srcStride + x];
                dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(res, FILTER_BITS), bd);
            }
    }

    public static void ConvolveXSrHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, int subpelXQn, ref AomConvParams cp, int bd)
    {
        int foHoriz = fpx.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round0;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                int s = srcOff + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) res += xf[xk + k] * src[s + k];
                res = Rpt(res, cp.Round0);
                dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(res, bits), bd);
            }
    }

    public static void DistWtd2dHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int subpelYQn, ref AomConvParams cp, int bd)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        var im = Im;
        int imH = h + fpy.Taps - 1, imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < imH; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << (bd + FILTER_BITS - 1);
                int s = srcHoriz + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[s + k];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
        int srcVert = foVert * imStride;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcVert + (y - foVert + k) * imStride + x];
                ushort res = (ushort)Rpt(sum, cp.Round1);
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - sub;
                    dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, roundBits), bd);
                }
                else dst16[d16 + y * d16s + x] = res;
            }
    }

    public static void DistWtdXHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, int subpelXQn, ref AomConvParams cp, int bd)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int foHoriz = fpx.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round1;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        var xf = fpx.Filter; int xk = fpx.Kernel(subpelXQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                int s = srcOff + y * srcStride + x - foHoriz;
                for (int k = 0; k < fpx.Taps; ++k) res += xf[xk + k] * src[s + k];
                res = (1 << bits) * Rpt(res, cp.Round0);
                res += roundOffset;
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, roundBits), bd);
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void DistWtdYHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpy, int subpelYQn, ref AomConvParams cp, int bd)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int foVert = fpy.Taps / 2 - 1;
        int bits = FILTER_BITS - cp.Round0;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        int roundBits = 2 * FILTER_BITS - cp.Round0 - cp.Round1;
        var yf = fpy.Filter; int yk = fpy.Kernel(subpelYQn & SUBPEL_MASK);
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = 0;
                for (int k = 0; k < fpy.Taps; ++k) res += yf[yk + k] * src[srcOff + (y - foVert + k) * srcStride + x];
                res *= 1 << bits;
                res = Rpt(res, cp.Round1) + roundOffset;
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, roundBits), bd);
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void DistWtd2dCopyHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        ref AomConvParams cp, int bd)
    {
        var dst16 = cp.Dst!; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int bits = FILTER_BITS * 2 - cp.Round1 - cp.Round0;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                int res = ((src[srcOff + y * srcStride + x] << bits) + roundOffset) & 0xffff;
                if (cp.DoAverage != 0)
                {
                    int tmp = AvgDist(dst16[d16 + y * d16s + x], res, ref cp) - roundOffset;
                    dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, bits), bd);
                }
                else dst16[d16 + y * d16s + x] = (ushort)res;
            }
    }

    public static void Convolve2dScaleHbd(ushort[] src, int srcOff, int srcStride, ushort[] dst, int dstOff, int dstStride, int w, int h,
        AomFilter.Params fpx, AomFilter.Params fpy, int subpelXQn, int xStepQn, int subpelYQn, int yStepQn, ref AomConvParams cp, int bd)
    {
        var im = Im;
        int imH = (((h - 1) * yStepQn + subpelYQn) >> SCALE_SUBPEL_BITS) + fpy.Taps;
        var dst16 = cp.Dst; int d16 = cp.DstOffset, d16s = cp.DstStride;
        int bits = FILTER_BITS * 2 - cp.Round0 - cp.Round1;
        int imStride = w;
        int foVert = fpy.Taps / 2 - 1, foHoriz = fpx.Taps / 2 - 1;
        int srcHoriz = srcOff - foVert * srcStride;
        var xf = fpx.Filter;
        for (int y = 0; y < imH; ++y)
        {
            int xQn = subpelXQn;
            for (int x = 0; x < w; ++x, xQn += xStepQn)
            {
                int srcX = srcHoriz + (xQn >> SCALE_SUBPEL_BITS);
                int xk = fpx.Kernel((xQn & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS);
                int sum = 1 << (bd + FILTER_BITS - 1);
                for (int k = 0; k < fpx.Taps; ++k) sum += xf[xk + k] * src[srcX + k - foHoriz];
                im[y * imStride + x] = (short)Rpt(sum, cp.Round0);
            }
            srcHoriz += srcStride;
        }
        int srcVert = foVert * imStride;
        int offsetBits = bd + 2 * FILTER_BITS - cp.Round0;
        int sub = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        var yf = fpy.Filter;
        for (int x = 0; x < w; ++x)
        {
            int yQn = subpelYQn;
            for (int y = 0; y < h; ++y, yQn += yStepQn)
            {
                int srcY = srcVert + (yQn >> SCALE_SUBPEL_BITS) * imStride;
                int yk = fpy.Kernel((yQn & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS);
                int sum = 1 << offsetBits;
                for (int k = 0; k < fpy.Taps; ++k) sum += yf[yk + k] * im[srcY + (k - foVert) * imStride];
                ushort res = (ushort)Rpt(sum, cp.Round1);
                if (cp.IsCompound != 0)
                {
                    if (cp.DoAverage != 0)
                    {
                        int tmp = AvgDist(dst16![d16 + y * d16s + x], res, ref cp) - sub;
                        dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, bits), bd);
                    }
                    else dst16![d16 + y * d16s + x] = res;
                }
                else
                {
                    int tmp = res - sub;
                    dst[dstOff + y * dstStride + x] = ClipHbd(Rpt(tmp, bits), bd);
                }
            }
            srcVert++;
        }
    }
}
