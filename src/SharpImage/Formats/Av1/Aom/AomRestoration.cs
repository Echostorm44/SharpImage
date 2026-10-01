using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

[InlineArray(8)]
internal struct AomTaps8
{
    private short _e;
}

/// <summary>WienerInfo: the 7 (+1 zero) taps of the vertical and horizontal filters.</summary>
internal struct AomWienerInfo
{
    public AomTaps8 V, H;

    public static bool Equal(in AomWienerInfo a, in AomWienerInfo b)
    {
        for (int i = 0; i < 8; i++)
            if (a.V[i] != b.V[i] || a.H[i] != b.H[i]) return false;
        return true;
    }
}

/// <summary>SgrprojInfo.</summary>
internal struct AomSgrprojInfo
{
    public int Ep, Xqd0, Xqd1;
}

/// <summary>RestorationUnitInfo.</summary>
internal struct AomRestorationUnitInfo
{
    public int Type;
    public AomWienerInfo Wiener;
    public AomSgrprojInfo Sgrproj;
}

/// <summary>RestorationTileLimits.</summary>
internal struct AomRestorationTileLimits
{
    public int HStart, HEnd, VStart, VEnd;
}

/// <summary>RestorationStripeBoundaries: two deblocked (or CDEF'd at the frame edges) lines above and below every
/// 64-row processing stripe, each extended RESTORATION_EXTRA_HORZ samples left and right.</summary>
internal sealed class AomStripeBoundaries
{
    public byte[] Above = Array.Empty<byte>(), Below = Array.Empty<byte>();
    public int Stride;
}

/// <summary>RestorationInfo for one plane: the frame restoration type, unit size and per-unit parameters.</summary>
internal sealed class AomRestorationInfo
{
    public int FrameRestorationType;
    public int RestorationUnitSize = AomRestoration.RestorationUnitSizeMax;
    public int NumRestUnits, VertUnits, HorzUnits;
    public AomRestorationUnitInfo[] UnitInfo = Array.Empty<AomRestorationUnitInfo>();
    public readonly AomStripeBoundaries Boundaries = new();
}

/// <summary>Port of libaom av1/common/restoration.c (8-bit), the Wiener convolution of convolve.c
/// (av1_wiener_convolve_add_src_c) and the self-guided filter: filtering one restoration unit stripe by stripe with the
/// saved boundary lines, applying the whole frame, and saving the boundary lines. The C kernels (the *C methods) are
/// the reference and non-AVX2 fallback; the dispatched entry points run the AVX2 ports in AomRestoration.Simd.cs.</summary>
internal static partial class AomRestoration
{
    public const int RestoreNone = 0, RestoreWiener = 1, RestoreSgrproj = 2, RestoreSwitchable = 3;
    public const int RestoreSwitchableTypes = 3, RestoreTypes = 4;
    public const int RestorationProcUnitSize = 64, RestorationUnitOffset = 8, RestorationUnitSizeMax = 256;
    public const int RestorationBorder = 3, RestorationCtxVert = 2, RestorationExtraHorz = 4;
    public const int SgrprojBorderVert = 3, SgrprojBorderHorz = 3;
    public const int SgrprojParamsBits = 4, SgrprojParams = 16, SgrprojPrjBits = 7, SgrprojRstBits = 4, SgrprojSgrBits = 8;
    public const int SgrprojSgr = 1 << SgrprojSgrBits;
    public const int SgrprojPrjMin0 = -(1 << SgrprojPrjBits) * 3 / 4, SgrprojPrjMax0 = SgrprojPrjMin0 + (1 << SgrprojPrjBits) - 1;
    public const int SgrprojPrjMin1 = -(1 << SgrprojPrjBits) / 4, SgrprojPrjMax1 = SgrprojPrjMin1 + (1 << SgrprojPrjBits) - 1;
    public const int SgrprojPrjSubexpK = 4, SgrprojMtableBits = 20, SgrprojRecipBits = 12;
    public const int WienerHalfwin = 3, WienerWin = 7, WienerWinChroma = 5, WienerWinReduced = 5, WienerHalfwin1 = 4;
    public const int WienerWin2 = WienerWin * WienerWin;
    public const int WienerFiltPrecBits = 7, WienerFiltStep = 1 << WienerFiltPrecBits;
    public const int WienerFiltTap0Midv = 3, WienerFiltTap1Midv = -7, WienerFiltTap2Midv = 15;
    public const int WienerFiltTap0Bits = 4, WienerFiltTap1Bits = 5, WienerFiltTap2Bits = 6;
    public const int WienerFiltBits = (WienerFiltTap0Bits + WienerFiltTap1Bits + WienerFiltTap2Bits) * 2;
    public const int WienerFiltTap0Minv = WienerFiltTap0Midv - (1 << WienerFiltTap0Bits) / 2;
    public const int WienerFiltTap1Minv = WienerFiltTap1Midv - (1 << WienerFiltTap1Bits) / 2;
    public const int WienerFiltTap2Minv = WienerFiltTap2Midv - (1 << WienerFiltTap2Bits) / 2;
    public const int WienerFiltTap0Maxv = WienerFiltTap0Midv - 1 + (1 << WienerFiltTap0Bits) / 2;
    public const int WienerFiltTap1Maxv = WienerFiltTap1Midv - 1 + (1 << WienerFiltTap1Bits) / 2;
    public const int WienerFiltTap2Maxv = WienerFiltTap2Midv - 1 + (1 << WienerFiltTap2Bits) / 2;
    public const int WienerFiltTap0SubexpK = 1, WienerFiltTap1SubexpK = 2, WienerFiltTap2SubexpK = 3;
    public const int RestorationUnitPelsHorzMax = RestorationUnitSizeMax * 3 / 2 + 2 * SgrprojBorderHorz + 16;
    public const int RestorationUnitPelsVertMax = RestorationUnitSizeMax * 3 / 2 + 2 * SgrprojBorderVert + RestorationUnitOffset;
    public const int RestorationUnitPelsMax = RestorationUnitPelsHorzMax * RestorationUnitPelsVertMax;
    private const int RestorationPadding = 20;
    public const int RestorationProcUnitPels = (RestorationProcUnitSize + SgrprojBorderHorz * 2 + RestorationPadding)
                                               * (RestorationProcUnitSize + SgrprojBorderVert * 2 + RestorationPadding);

    /// <summary>av1_sgr_params: radii and s values per set.</summary>
    public static readonly int[] SgrR0 = { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 2, 2 };
    public static readonly int[] SgrR1 = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0 };
    public static readonly int[] SgrS0 = { 140, 112, 93, 80, 70, 58, 47, 37, 30, 25, -1, -1, -1, -1, 56, 22 };
    public static readonly int[] SgrS1 = { 3236, 2158, 1618, 1438, 1295, 1177, 1079, 996, 925, 863, 2589, 1618, 1177, 925, -1, -1 };

    private static readonly int[] XByXplus1 =
    {
        1,   128, 171, 192, 205, 213, 219, 224, 228, 230, 233, 235, 236, 238, 239,
        240, 241, 242, 243, 243, 244, 244, 245, 245, 246, 246, 247, 247, 247, 247,
        248, 248, 248, 248, 249, 249, 249, 249, 249, 250, 250, 250, 250, 250, 250,
        250, 251, 251, 251, 251, 251, 251, 251, 251, 251, 251, 252, 252, 252, 252,
        252, 252, 252, 252, 252, 252, 252, 252, 252, 252, 252, 252, 252, 253, 253,
        253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253,
        253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 254, 254, 254,
        254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        256,
    };

    private static readonly int[] OneByX =
    {
        4096, 2048, 1365, 1024, 819, 683, 585, 512, 455, 410, 372, 341, 315,
        293,  273,  256,  241,  228, 216, 205, 195, 186, 178, 171, 164,
    };

    /// <summary>set_default_wiener.</summary>
    public static void SetDefaultWiener(ref AomWienerInfo w)
    {
        w.V[0] = w.H[0] = WienerFiltTap0Midv;
        w.V[1] = w.H[1] = WienerFiltTap1Midv;
        w.V[2] = w.H[2] = WienerFiltTap2Midv;
        w.V[WienerHalfwin] = w.H[WienerHalfwin] = -2 * (WienerFiltTap2Midv + WienerFiltTap1Midv + WienerFiltTap0Midv);
        w.V[4] = w.H[4] = WienerFiltTap2Midv;
        w.V[5] = w.H[5] = WienerFiltTap1Midv;
        w.V[6] = w.H[6] = WienerFiltTap0Midv;
    }

    /// <summary>set_default_sgrproj.</summary>
    public static void SetDefaultSgrproj(ref AomSgrprojInfo s)
    {
        s.Xqd0 = (SgrprojPrjMin0 + SgrprojPrjMax0) / 2;
        s.Xqd1 = (SgrprojPrjMin1 + SgrprojPrjMax1) / 2;
    }

    /// <summary>av1_get_upsampled_plane_size (no superres).</summary>
    public static void GetPlaneSize(AomYv12 frame, bool isUv, out int planeW, out int planeH)
    {
        int ssX = isUv ? frame.SsX : 0, ssY = isUv ? frame.SsY : 0;
        planeW = (frame.Width + ((1 << ssX) >> 1)) >> ssX;
        planeH = (frame.Height + ((1 << ssY) >> 1)) >> ssY;
    }

    /// <summary>av1_lr_count_units.</summary>
    public static int LrCountUnits(int unitSize, int planeSize) => Math.Max((planeSize + (unitSize >> 1)) / unitSize, 1);

    /// <summary>av1_extend_frame (8-bit): replicate the edge samples of the width x height area border samples out.</summary>
    public static void ExtendFrame(AomYv12Plane p, int width, int height, int borderHorz, int borderVert)
    {
        byte[] b = p.Buf;
        for (int i = 0; i < height; ++i)
        {
            int row = p.At(0, i);
            b.AsSpan(row - borderHorz, borderHorz).Fill(b[row]);
            b.AsSpan(row + width, borderHorz).Fill(b[row + width - 1]);
        }
        int w = width + 2 * borderHorz;
        for (int i = -borderVert; i < 0; ++i) Buffer.BlockCopy(b, p.At(-borderHorz, 0), b, p.At(-borderHorz, i), w);
        for (int i = height; i < height + borderVert; ++i)
            Buffer.BlockCopy(b, p.At(-borderHorz, height - 1), b, p.At(-borderHorz, i), w);
    }

    // ---- stripe boundaries --------------------------------------------------------------------------------------------

    /// <summary>Allocates a plane's boundary line buffers (av1_alloc_restoration_buffers' stripe part).</summary>
    public static void AllocBoundaries(AomRestorationInfo rsi, AomYv12 frame, int plane)
    {
        bool isUv = plane > 0;
        int ssY = isUv ? frame.SsY : 0;
        GetPlaneSize(frame, isUv, out int planeW, out int planeH);
        int stripeHeight = RestorationProcUnitSize >> ssY, stripeOff = RestorationUnitOffset >> ssY;
        int numStripes = (planeH + stripeOff + stripeHeight - 1) / stripeHeight + 1;
        var b = rsi.Boundaries;
        b.Stride = planeW + 2 * RestorationExtraHorz + 32;
        int size = numStripes * RestorationCtxVert * b.Stride;
        if (b.Above.Length < size)
        {
            b.Above = new byte[size];
            b.Below = new byte[size];
        }
    }

    private static void ExtendLines(byte[] buf, int start, int width, int height, int stride, int extend)
    {
        for (int i = 0; i < height; ++i, start += stride)
        {
            buf.AsSpan(start - extend, extend).Fill(buf[start]);
            buf.AsSpan(start + width, extend).Fill(buf[start + width - 1]);
        }
    }

    private static void SaveDeblockBoundaryLines(AomYv12Plane p, AomStripeBoundaries bnd, int row, int stripe, bool isAbove)
    {
        byte[] dstBuf = isAbove ? bnd.Above : bnd.Below;
        int bdryRows = RestorationExtraHorz + RestorationCtxVert * stripe * bnd.Stride;
        int linesToSave = Math.Min(RestorationCtxVert, p.CropHeight - row);
        int width = p.CropWidth;
        for (int i = 0; i < linesToSave; i++)
            Buffer.BlockCopy(p.Buf, p.At(0, row + i), dstBuf, bdryRows + i * bnd.Stride, width);
        if (linesToSave == 1) Buffer.BlockCopy(dstBuf, bdryRows, dstBuf, bdryRows + bnd.Stride, width);
        ExtendLines(dstBuf, bdryRows, width, RestorationCtxVert, bnd.Stride, RestorationExtraHorz);
    }

    private static void SaveCdefBoundaryLines(AomYv12Plane p, AomStripeBoundaries bnd, int row, int stripe, bool isAbove)
    {
        byte[] dstBuf = isAbove ? bnd.Above : bnd.Below;
        int bdryRows = RestorationExtraHorz + RestorationCtxVert * stripe * bnd.Stride;
        int width = p.CropWidth;
        for (int i = 0; i < RestorationCtxVert; i++)
            Buffer.BlockCopy(p.Buf, p.At(0, row), dstBuf, bdryRows + i * bnd.Stride, width);
        ExtendLines(dstBuf, bdryRows, width, RestorationCtxVert, bnd.Stride, RestorationExtraHorz);
    }

    /// <summary>av1_loop_restoration_save_boundary_lines (no superres): afterCdef = false saves the deblocked lines at
    /// internal stripe boundaries, true the (CDEF'd) lines at the frame's top and bottom.</summary>
    public static void SaveBoundaryLines(AomYv12 frame, AomRestorationInfo[] rst, bool afterCdef)
    {
        for (int plane = 0; plane < frame.NumPlanes; plane++)
        {
            bool isUv = plane > 0;
            int ssY = isUv ? frame.SsY : 0;
            int stripeHeight = RestorationProcUnitSize >> ssY, stripeOff = RestorationUnitOffset >> ssY;
            GetPlaneSize(frame, isUv, out _, out int planeH);
            var p = frame.Planes[plane];
            AllocBoundaries(rst[plane], frame, plane);
            var bnd = rst[plane].Boundaries;
            int planeHeight = planeH;
            for (int stripeIdx = 0; ; ++stripeIdx)
            {
                int y0 = Math.Max(0, stripeIdx * stripeHeight - stripeOff);
                if (y0 >= planeH) break;
                int y1 = Math.Min((stripeIdx + 1) * stripeHeight - stripeOff, planeH);
                bool useDeblockAbove = stripeIdx > 0;
                bool useDeblockBelow = y1 < planeHeight;
                if (!afterCdef)
                {
                    if (useDeblockAbove) SaveDeblockBoundaryLines(p, bnd, y0 - RestorationCtxVert, stripeIdx, true);
                    if (useDeblockBelow) SaveDeblockBoundaryLines(p, bnd, y1, stripeIdx, false);
                }
                else
                {
                    if (!useDeblockAbove) SaveCdefBoundaryLines(p, bnd, y0, stripeIdx, true);
                    if (!useDeblockBelow) SaveCdefBoundaryLines(p, bnd, y1 - 1, stripeIdx, false);
                }
            }
        }
    }

    // ---- Wiener -----------------------------------------------------------------------------------------------------

    /// <summary>av1_wiener_convolve_add_src_c (8-bit, x/y step 16): horizontal pass to a 13-bit intermediate
    /// (round0 = 3, clamped), vertical pass back to pixels (round1 = 11); the centre tap gets an implicit +128.</summary>
    public static void WienerConvolveAddSrcC(byte[] src, int s0, int srcStride, byte[] dst, int d0, int dstStride,
        in AomTaps8 hf, in AomTaps8 vf, int w, int h, ushort[] temp)
    {
        const int round0 = 3, round1 = 11, maxSb = 128, bd = 8;
        const int clampLimit = 1 << (bd + 1 + 7 - round0);
        int intermediateHeight = h + 7;
        int h0 = hf[0], h1 = hf[1], h2 = hf[2], h3 = hf[3], h4 = hf[4], h5 = hf[5], h6 = hf[6], h7 = hf[7];
        // horizontal: rows -3 .. h + 3 of src, taps at x - 3 .. x + 4
        int srow = s0 - srcStride * 3 - 3;
        for (int y = 0; y < intermediateHeight; ++y, srow += srcStride)
        {
            int t = y * maxSb;
            for (int x = 0; x < w; ++x)
            {
                int sx = srow + x;
                int rounding = (src[sx + 3] << 7) + (1 << (bd + 7 - 1));
                int sum = src[sx] * h0 + src[sx + 1] * h1 + src[sx + 2] * h2 + src[sx + 3] * h3 + src[sx + 4] * h4
                          + src[sx + 5] * h5 + src[sx + 6] * h6 + src[sx + 7] * h7 + rounding;
                int v = (sum + (1 << (round0 - 1))) >> round0;
                temp[t + x] = (ushort)(v < 0 ? 0 : v > clampLimit - 1 ? clampLimit - 1 : v);
            }
        }
        int v0 = vf[0], v1 = vf[1], v2 = vf[2], v3 = vf[3], v4 = vf[4], v5 = vf[5], v6 = vf[6], v7 = vf[7];
        for (int x = 0; x < w; ++x)
            for (int y = 0; y < h; ++y)
            {
                int t = y * maxSb + x;
                int rounding = (temp[t + 3 * maxSb] << 7) - (1 << (bd + round1 - 1));
                int sum = temp[t] * v0 + temp[t + maxSb] * v1 + temp[t + 2 * maxSb] * v2 + temp[t + 3 * maxSb] * v3
                          + temp[t + 4 * maxSb] * v4 + temp[t + 5 * maxSb] * v5 + temp[t + 6 * maxSb] * v6
                          + temp[t + 7 * maxSb] * v7 + rounding;
                int r = (sum + (1 << (round1 - 1))) >> round1;
                dst[d0 + y * dstStride + x] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
            }
    }

    // ---- self-guided --------------------------------------------------------------------------------------------------

    private static void Boxsum1(int[] src, int s0, int width, int height, int srcStride, bool sqr, int[] dst, int d0,
        int dstStride)
    {
        int i, j, a, b, c;
        // Vertical sum over 3-pixel regions, from src into dst.
        for (j = 0; j < width; ++j)
        {
            int Sq(int v) => sqr ? v * v : v;
            a = Sq(src[s0 + j]);
            b = Sq(src[s0 + srcStride + j]);
            c = Sq(src[s0 + 2 * srcStride + j]);
            dst[d0 + j] = a + b;
            for (i = 1; i < height - 2; ++i)
            {
                dst[d0 + i * dstStride + j] = a + b + c;
                a = b;
                b = c;
                c = Sq(src[s0 + (i + 2) * srcStride + j]);
            }
            dst[d0 + i * dstStride + j] = a + b + c;
            dst[d0 + (i + 1) * dstStride + j] = b + c;
        }
        // Horizontal sum over 3-pixel regions of dst
        for (i = 0; i < height; ++i)
        {
            int r = d0 + i * dstStride;
            a = dst[r];
            b = dst[r + 1];
            c = dst[r + 2];
            dst[r] = a + b;
            for (j = 1; j < width - 2; ++j)
            {
                dst[r + j] = a + b + c;
                a = b;
                b = c;
                c = dst[r + j + 2];
            }
            dst[r + j] = a + b + c;
            dst[r + j + 1] = b + c;
        }
    }

    private static void Boxsum2(int[] src, int s0, int width, int height, int srcStride, bool sqr, int[] dst, int d0,
        int dstStride)
    {
        int i, j, a, b, c, d, e;
        for (j = 0; j < width; ++j)
        {
            int Sq(int v) => sqr ? v * v : v;
            a = Sq(src[s0 + j]);
            b = Sq(src[s0 + srcStride + j]);
            c = Sq(src[s0 + 2 * srcStride + j]);
            d = Sq(src[s0 + 3 * srcStride + j]);
            e = Sq(src[s0 + 4 * srcStride + j]);
            dst[d0 + j] = a + b + c;
            dst[d0 + dstStride + j] = a + b + c + d;
            for (i = 2; i < height - 3; ++i)
            {
                dst[d0 + i * dstStride + j] = a + b + c + d + e;
                a = b;
                b = c;
                c = d;
                d = e;
                e = Sq(src[s0 + (i + 3) * srcStride + j]);
            }
            dst[d0 + i * dstStride + j] = a + b + c + d + e;
            dst[d0 + (i + 1) * dstStride + j] = b + c + d + e;
            dst[d0 + (i + 2) * dstStride + j] = c + d + e;
        }
        for (i = 0; i < height; ++i)
        {
            int r = d0 + i * dstStride;
            a = dst[r];
            b = dst[r + 1];
            c = dst[r + 2];
            d = dst[r + 3];
            e = dst[r + 4];
            dst[r] = a + b + c;
            dst[r + 1] = a + b + c + d;
            for (j = 2; j < width - 3; ++j)
            {
                dst[r + j] = a + b + c + d + e;
                a = b;
                b = c;
                c = d;
                d = e;
                e = dst[r + j + 3];
            }
            dst[r + j] = a + b + c + d + e;
            dst[r + j + 1] = b + c + d + e;
            dst[r + j + 2] = c + d + e;
        }
    }

    /// <summary>Scratch for the self-guided filter (the C version's stack arrays).</summary>
    internal sealed class SgrScratch
    {
        public readonly int[] Dgd32 = new int[RestorationProcUnitPels];
        public readonly int[] A = new int[RestorationProcUnitPels];
        public readonly int[] B = new int[RestorationProcUnitPels];
        /// <summary>av1_selfguided_restoration_avx2's buffer: the A, B and the two integral images.</summary>
        public readonly int[] Ii = new int[4 * SgrBufElts + 64];
    }

    private static void CalculateIntermediateResult(int[] dgd, int dgdIdx, int width, int height, int dgdStride,
        int sgrParamsIdx, int radiusIdx, int pass, int[] A, int[] B)
    {
        int r = radiusIdx == 0 ? SgrR0[sgrParamsIdx] : SgrR1[sgrParamsIdx];
        uint s = (uint)(radiusIdx == 0 ? SgrS0[sgrParamsIdx] : SgrS1[sgrParamsIdx]);
        int widthExt = width + 2 * SgrprojBorderHorz, heightExt = height + 2 * SgrprojBorderVert;
        int bufStride = ((widthExt + 3) & ~3) + 16;
        int step = pass == 0 ? 1 : 2;
        int src0 = dgdIdx - dgdStride * SgrprojBorderVert - SgrprojBorderHorz;
        if (r == 1)
        {
            Boxsum1(dgd, src0, widthExt, heightExt, dgdStride, false, B, 0, bufStride);
            Boxsum1(dgd, src0, widthExt, heightExt, dgdStride, true, A, 0, bufStride);
        }
        else
        {
            Boxsum2(dgd, src0, widthExt, heightExt, dgdStride, false, B, 0, bufStride);
            Boxsum2(dgd, src0, widthExt, heightExt, dgdStride, true, A, 0, bufStride);
        }
        int o = SgrprojBorderVert * bufStride + SgrprojBorderHorz;
        uint n = (uint)((2 * r + 1) * (2 * r + 1));
        uint oneByX = (uint)OneByX[n - 1];
        for (int i = -1; i < height + 1; i += step)
            for (int j = -1; j < width + 1; ++j)
            {
                int k = o + i * bufStride + j;
                uint a = (uint)A[k];   // 8-bit: ROUND_POWER_OF_TWO(A[k], 0)
                uint b = (uint)B[k];
                uint p = a * n < b * b ? 0 : a * n - b * b;
                uint z = (p * s + (1u << (SgrprojMtableBits - 1))) >> SgrprojMtableBits;
                A[k] = XByXplus1[Math.Min(z, 255u)];
                B[k] = (int)(((uint)(SgrprojSgr - A[k]) * (uint)B[k] * oneByX + (1u << (SgrprojRecipBits - 1)))
                             >> SgrprojRecipBits);
            }
    }

    private static void SelfguidedFastInternal(int[] dgd, int dgdIdx, int width, int height, int dgdStride, int[] dst,
        int dst0, int dstStride, int sgrParamsIdx, SgrScratch sc)
    {
        int widthExt = width + 2 * SgrprojBorderHorz;
        int bufStride = ((widthExt + 3) & ~3) + 16;
        int[] A = sc.A, B = sc.B;
        CalculateIntermediateResult(dgd, dgdIdx, width, height, dgdStride, sgrParamsIdx, 0, 1, A, B);
        int o = SgrprojBorderVert * bufStride + SgrprojBorderHorz;
        for (int i = 0; i < height; ++i)
        {
            if ((i & 1) == 0)
            {
                // even row
                for (int j = 0; j < width; ++j)
                {
                    int k = o + i * bufStride + j, l = dgdIdx + i * dgdStride + j, m = dst0 + i * dstStride + j;
                    const int nb = 5;
                    int a = (A[k - bufStride] + A[k + bufStride]) * 6
                            + (A[k - 1 - bufStride] + A[k - 1 + bufStride] + A[k + 1 - bufStride] + A[k + 1 + bufStride]) * 5;
                    int b = (B[k - bufStride] + B[k + bufStride]) * 6
                            + (B[k - 1 - bufStride] + B[k - 1 + bufStride] + B[k + 1 - bufStride] + B[k + 1 + bufStride]) * 5;
                    int v = a * dgd[l] + b;
                    const int sh = SgrprojSgrBits + nb - SgrprojRstBits;
                    dst[m] = (v + (1 << (sh - 1))) >> sh;
                }
            }
            else
            {
                // odd row
                for (int j = 0; j < width; ++j)
                {
                    int k = o + i * bufStride + j, l = dgdIdx + i * dgdStride + j, m = dst0 + i * dstStride + j;
                    const int nb = 4;
                    int a = A[k] * 6 + (A[k - 1] + A[k + 1]) * 5;
                    int b = B[k] * 6 + (B[k - 1] + B[k + 1]) * 5;
                    int v = a * dgd[l] + b;
                    const int sh = SgrprojSgrBits + nb - SgrprojRstBits;
                    dst[m] = (v + (1 << (sh - 1))) >> sh;
                }
            }
        }
    }

    private static void SelfguidedInternal(int[] dgd, int dgdIdx, int width, int height, int dgdStride, int[] dst,
        int dst0, int dstStride, int sgrParamsIdx, SgrScratch sc)
    {
        int widthExt = width + 2 * SgrprojBorderHorz;
        int bufStride = ((widthExt + 3) & ~3) + 16;
        int[] A = sc.A, B = sc.B;
        CalculateIntermediateResult(dgd, dgdIdx, width, height, dgdStride, sgrParamsIdx, 1, 0, A, B);
        int o = SgrprojBorderVert * bufStride + SgrprojBorderHorz;
        for (int i = 0; i < height; ++i)
            for (int j = 0; j < width; ++j)
            {
                int k = o + i * bufStride + j, l = dgdIdx + i * dgdStride + j, m = dst0 + i * dstStride + j;
                const int nb = 5;
                int a = (A[k] + A[k - 1] + A[k + 1] + A[k - bufStride] + A[k + bufStride]) * 4
                        + (A[k - 1 - bufStride] + A[k - 1 + bufStride] + A[k + 1 - bufStride] + A[k + 1 + bufStride]) * 3;
                int b = (B[k] + B[k - 1] + B[k + 1] + B[k - bufStride] + B[k + bufStride]) * 4
                        + (B[k - 1 - bufStride] + B[k - 1 + bufStride] + B[k + 1 - bufStride] + B[k + 1 + bufStride]) * 3;
                int v = a * dgd[l] + b;
                const int sh = SgrprojSgrBits + nb - SgrprojRstBits;
                dst[m] = (v + (1 << (sh - 1))) >> sh;
            }
    }

    /// <summary>av1_selfguided_restoration_c (8-bit): the two filtered versions flt0 (r0) and flt1 (r1) of the width x
    /// height block at dgd[d0] (reads 3 samples around it), scaled by 2^SGRPROJ_RST_BITS.</summary>
    public static void SelfguidedRestorationC(byte[] dgd8, int d0, int width, int height, int dgdStride, int[] flt0,
        int f0, int[] flt1, int f1, int fltStride, int sgrParamsIdx, SgrScratch sc)
    {
        int dgd32Stride = width + 2 * SgrprojBorderHorz;
        int dgd32 = dgd32Stride * SgrprojBorderVert + SgrprojBorderHorz;
        int[] buf = sc.Dgd32;
        for (int i = -SgrprojBorderVert; i < height + SgrprojBorderVert; ++i)
        {
            int srow = d0 + i * dgdStride, drow = dgd32 + i * dgd32Stride;
            for (int j = -SgrprojBorderHorz; j < width + SgrprojBorderHorz; ++j) buf[drow + j] = dgd8[srow + j];
        }
        if (SgrR0[sgrParamsIdx] > 0)
            SelfguidedFastInternal(buf, dgd32, width, height, dgd32Stride, flt0, f0, fltStride, sgrParamsIdx, sc);
        if (SgrR1[sgrParamsIdx] > 0)
            SelfguidedInternal(buf, dgd32, width, height, dgd32Stride, flt1, f1, fltStride, sgrParamsIdx, sc);
    }

    /// <summary>av1_decode_xq.</summary>
    public static void DecodeXq(int xqd0, int xqd1, int ep, out int xq0, out int xq1)
    {
        if (SgrR0[ep] == 0)
        {
            xq0 = 0;
            xq1 = (1 << SgrprojPrjBits) - xqd1;
        }
        else if (SgrR1[ep] == 0)
        {
            xq0 = xqd0;
            xq1 = 0;
        }
        else
        {
            xq0 = xqd0;
            xq1 = (1 << SgrprojPrjBits) - xq0 - xqd1;
        }
    }

    /// <summary>av1_apply_selfguided_restoration_c (8-bit).</summary>
    public static void ApplySelfguidedC(byte[] dat, int d0, int width, int height, int stride, int ep, int xqd0, int xqd1,
        byte[] dst, int dst0, int dstStride, int[] flt0, int[] flt1, SgrScratch sc)
    {
        SelfguidedRestorationC(dat, d0, width, height, stride, flt0, 0, flt1, 0, width, ep, sc);
        DecodeXq(xqd0, xqd1, ep, out int xq0, out int xq1);
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        const int sh = SgrprojPrjBits + SgrprojRstBits;
        for (int i = 0; i < height; ++i)
            for (int j = 0; j < width; ++j)
            {
                int k = i * width + j;
                int u = dat[d0 + i * stride + j] << SgrprojRstBits;
                int v = u << SgrprojPrjBits;
                if (r0) v += xq0 * (flt0[k] - u);
                if (r1) v += xq1 * (flt1[k] - u);
                short w = (short)((v + (1 << (sh - 1))) >> sh);
                dst[dst0 + i * dstStride + j] = (byte)(w < 0 ? 0 : w > 255 ? 255 : w);
            }
    }

    // ---- filtering a unit ---------------------------------------------------------------------------------------------

    /// <summary>The line buffers and filter scratch one restoration pass needs (RestorationLineBuffers, rst_tmpbuf).</summary>
    internal sealed class UnitScratch
    {
        public const int LineBufferWidth = RestorationUnitSizeMax * 3 / 2 + 2 * RestorationExtraHorz;
        public readonly byte[][] SaveAbove = { new byte[LineBufferWidth], new byte[LineBufferWidth], new byte[LineBufferWidth] };
        public readonly byte[][] SaveBelow = { new byte[LineBufferWidth], new byte[LineBufferWidth], new byte[LineBufferWidth] };
        public readonly ushort[] WienerTemp = new ushort[(128 + 7) * 128];
        public readonly int[] Flt0 = new int[RestorationUnitPelsMax], Flt1 = new int[RestorationUnitPelsMax];
        public readonly SgrScratch Sgr = new();
    }

    private static void GetStripeBoundaryInfo(in AomRestorationTileLimits limits, int planeH, int ssY,
        out bool copyAbove, out bool copyBelow)
    {
        int fullStripeHeight = RestorationProcUnitSize >> ssY, runitOffset = RestorationUnitOffset >> ssY;
        bool firstStripeInPlane = limits.VStart == 0;
        int thisStripeHeight = fullStripeHeight - (firstStripeInPlane ? runitOffset : 0);
        bool lastStripeInPlane = limits.VStart + thisStripeHeight >= planeH;
        copyAbove = !firstStripeInPlane;
        copyBelow = !lastStripeInPlane;
    }

    private static void SetupProcessingStripeBoundary(in AomRestorationTileLimits limits, AomStripeBoundaries rsb,
        int rsbRow, int h, AomYv12Plane data, UnitScratch rlbs, bool copyAbove, bool copyBelow)
    {
        int bufStride = rsb.Stride;
        int bufX0Off = limits.HStart;
        int lineSize = limits.HEnd - limits.HStart + 2 * RestorationExtraHorz;
        int dataX0 = limits.HStart - RestorationExtraHorz;
        if (copyAbove)
            for (int i = -RestorationBorder; i < 0; ++i)
            {
                int bufRow = rsbRow + Math.Max(i + RestorationCtxVert, 0);
                int bufOff = bufX0Off + bufRow * bufStride;
                int dst8 = data.At(dataX0, limits.VStart + i);
                Buffer.BlockCopy(data.Buf, dst8, rlbs.SaveAbove[i + RestorationBorder], 0, lineSize);
                Buffer.BlockCopy(rsb.Above, bufOff, data.Buf, dst8, lineSize);
            }
        if (copyBelow)
        {
            int stripeEnd = limits.VStart + h;
            for (int i = 0; i < RestorationBorder; ++i)
            {
                int bufRow = rsbRow + Math.Min(i, RestorationCtxVert - 1);
                int bufOff = bufX0Off + bufRow * bufStride;
                int dst8 = data.At(dataX0, stripeEnd + i);
                Buffer.BlockCopy(data.Buf, dst8, rlbs.SaveBelow[i], 0, lineSize);
                Buffer.BlockCopy(rsb.Below, bufOff, data.Buf, dst8, lineSize);
            }
        }
    }

    private static void RestoreProcessingStripeBoundary(in AomRestorationTileLimits limits, UnitScratch rlbs, int h,
        AomYv12Plane data, bool copyAbove, bool copyBelow)
    {
        int lineSize = limits.HEnd - limits.HStart + 2 * RestorationExtraHorz;
        int dataX0 = limits.HStart - RestorationExtraHorz;
        if (copyAbove)
            for (int i = -RestorationBorder; i < 0; ++i)
                Buffer.BlockCopy(rlbs.SaveAbove[i + RestorationBorder], 0, data.Buf, data.At(dataX0, limits.VStart + i),
                    lineSize);
        if (copyBelow)
        {
            int stripeBottom = limits.VStart + h;
            for (int i = 0; i < RestorationBorder; ++i)
            {
                if (stripeBottom + i >= limits.VEnd + RestorationBorder) break;
                Buffer.BlockCopy(rlbs.SaveBelow[i], 0, data.Buf, data.At(dataX0, stripeBottom + i), lineSize);
            }
        }
    }

    /// <summary>av1_loop_restoration_filter_unit (8-bit, optimized_lr = 0): filters the unit at limits of data into dst
    /// (both planes of the same geometry), stripe by stripe, with the stripe boundary lines swapped in around each.</summary>
    public static void FilterUnit(in AomRestorationTileLimits limits, in AomRestorationUnitInfo rui,
        AomStripeBoundaries rsb, int planeW, int planeH, int ssX, int ssY, AomYv12Plane data, AomYv12Plane dst,
        UnitScratch sc)
    {
        _ = planeW;
        int unitH = limits.VEnd - limits.VStart, unitW = limits.HEnd - limits.HStart;
        int dataTl = data.At(limits.HStart, limits.VStart), dstTl = dst.At(limits.HStart, limits.VStart);
        if (rui.Type == RestoreNone)
        {
            for (int i = 0; i < unitH; ++i)
                Buffer.BlockCopy(data.Buf, dataTl + i * data.Stride, dst.Buf, dstTl + i * dst.Stride, unitW);
            return;
        }
        int procunitWidth = RestorationProcUnitSize >> ssX;
        AomRestorationTileLimits remaining = limits;
        int iRow = 0;
        while (iRow < unitH)
        {
            remaining.VStart = limits.VStart + iRow;
            GetStripeBoundaryInfo(remaining, planeH, ssY, out bool copyAbove, out bool copyBelow);
            int fullStripeHeight = RestorationProcUnitSize >> ssY, runitOffset = RestorationUnitOffset >> ssY;
            int frameStripe = (remaining.VStart + runitOffset) / fullStripeHeight;
            int rsbRow = RestorationCtxVert * frameStripe;
            int nominalStripeHeight = fullStripeHeight - (frameStripe == 0 ? runitOffset : 0);
            int h = Math.Min(nominalStripeHeight, remaining.VEnd - remaining.VStart);

            SetupProcessingStripeBoundary(remaining, rsb, rsbRow, h, data, sc, copyAbove, copyBelow);
            int src = dataTl + iRow * data.Stride, dstRow = dstTl + iRow * dst.Stride;
            if (rui.Type == RestoreWiener)
            {
                for (int j = 0; j < unitW; j += procunitWidth)
                {
                    int w = Math.Min(procunitWidth, (unitW - j + 15) & ~15);
                    WienerConvolveAddSrc(data.Buf, src + j, data.Stride, dst.Buf, dstRow + j, dst.Stride, rui.Wiener.H,
                        rui.Wiener.V, w, h, sc.WienerTemp);
                }
            }
            else
            {
                for (int j = 0; j < unitW; j += procunitWidth)
                {
                    int w = Math.Min(procunitWidth, unitW - j);
                    ApplySelfguided(data.Buf, src + j, w, h, data.Stride, rui.Sgrproj.Ep, rui.Sgrproj.Xqd0,
                        rui.Sgrproj.Xqd1, dst.Buf, dstRow + j, dst.Stride, sc.Flt0, sc.Flt1, sc.Sgr);
                }
            }
            RestoreProcessingStripeBoundary(remaining, sc, h, data, copyAbove, copyBelow);
            iRow += h;
        }
    }

    /// <summary>av1_loop_restoration_filter_frame (one thread, optimized_lr = 0): filters every plane whose frame
    /// restoration type is not NONE, unit by unit, and copies the result back into frame.</summary>
    public static void FilterFrame(AomYv12 frame, AomRestorationInfo[] rst)
    {
        var dstFrame = frame.CloneGeometry();
        var sc = new UnitScratch();
        for (int plane = 0; plane < frame.NumPlanes; ++plane)
        {
            var rsi = rst[plane];
            if (rsi.FrameRestorationType == RestoreNone) continue;
            bool isUv = plane > 0;
            int ssX = isUv ? frame.SsX : 0, ssY = isUv ? frame.SsY : 0;
            GetPlaneSize(frame, isUv, out int planeW, out int planeH);
            var data = frame.Planes[plane];
            ExtendFrame(data, planeW, planeH, RestorationBorder, RestorationBorder);
            var dst = dstFrame.Planes[plane];
            // foreach_rest_unit_in_plane
            int unitSize = rsi.RestorationUnitSize, extSize = unitSize * 3 / 2;
            int y0 = 0, row = 0;
            while (y0 < planeH)
            {
                int remainingH = planeH - y0;
                int h = remainingH < extSize ? remainingH : unitSize;
                var limits = new AomRestorationTileLimits { VStart = y0, VEnd = y0 + h };
                int voffset = RestorationUnitOffset >> ssY;
                limits.VStart = Math.Max(0, limits.VStart - voffset);
                if (limits.VEnd < planeH) limits.VEnd -= voffset;
                int x0 = 0, col = 0;
                while (x0 < planeW)
                {
                    int remainingW = planeW - x0;
                    int w = remainingW < extSize ? remainingW : unitSize;
                    limits.HStart = x0;
                    limits.HEnd = x0 + w;
                    FilterUnit(limits, rsi.UnitInfo[row * rsi.HorzUnits + col], rsi.Boundaries, planeW, planeH, ssX,
                        ssY, data, dst, sc);
                    x0 += w;
                    ++col;
                }
                y0 += h;
                ++row;
            }
            // loop_restoration_copy_planes
            for (int r = 0; r < planeH; r++) Buffer.BlockCopy(dst.Buf, dst.At(0, r), data.Buf, data.At(0, r), planeW);
        }
    }

    /// <summary>av1_loop_restoration_corners_in_sb (no superres): the restoration units coded in the superblock at
    /// (miRow, miCol).</summary>
    public static bool CornersInSb(AomRestorationInfo rsi, bool isUv, int ssX, int ssY, int miRow, int miCol,
        int sbMiSize, out int rcol0, out int rcol1, out int rrow0, out int rrow1)
    {
        int size = rsi.RestorationUnitSize;
        int miSizeX = 4 >> (isUv ? ssX : 0), miSizeY = 4 >> (isUv ? ssY : 0);
        int rndX = size - 1, rndY = size - 1;
        rcol0 = (miCol * miSizeX + rndX) / size;
        rrow0 = (miRow * miSizeY + rndY) / size;
        rcol1 = Math.Min(((miCol + sbMiSize) * miSizeX + rndX) / size, rsi.HorzUnits);
        rrow1 = Math.Min(((miRow + sbMiSize) * miSizeY + rndY) / size, rsi.VertUnits);
        return rcol0 < rcol1 && rrow0 < rrow1;
    }
}
