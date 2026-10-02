using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static partial class AomInterPred
{
    // ---- aom_dsp/blend_a64_mask.c, blend_a64_vmask.c, blend_a64_hmask.c ----------------------------------------------

    private static int Blend(int m, int v0, int v1) => (m * v0 + (64 - m) * v1 + 32) >> 6;   // AOM_BLEND_A64

    private static int MaskAt(byte[] mask, int maskOff, int maskStride, int i, int j, int subw, int subh)
    {
        if (subw == 0 && subh == 0) return mask[maskOff + i * maskStride + j];
        if (subw == 1 && subh == 1)
            return (mask[maskOff + 2 * i * maskStride + 2 * j] + mask[maskOff + (2 * i + 1) * maskStride + 2 * j] +
                    mask[maskOff + 2 * i * maskStride + 2 * j + 1] + mask[maskOff + (2 * i + 1) * maskStride + 2 * j + 1] + 2) >> 2;
        if (subw == 1) return (mask[maskOff + i * maskStride + 2 * j] + mask[maskOff + i * maskStride + 2 * j + 1] + 1) >> 1;
        return (mask[maskOff + 2 * i * maskStride + j] + mask[maskOff + (2 * i + 1) * maskStride + j] + 1) >> 1;
    }

    /// <summary>aom_blend_a64_mask (dst may alias src0 / src1 element-wise).</summary>
    public static void BlendA64Mask(byte[] dst, int dstOff, int dstStride, byte[] src0, int s0Off, int s0Stride, byte[] src1, int s1Off, int s1Stride,
        byte[] mask, int maskOff, int maskStride, int w, int h, int subw, int subh)
    {
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
                dst[dstOff + i * dstStride + j] = (byte)Blend(MaskAt(mask, maskOff, maskStride, i, j, subw, subh), src0[s0Off + i * s0Stride + j],
                    src1[s1Off + i * s1Stride + j]);
    }

    /// <summary>aom_highbd_blend_a64_mask.</summary>
    public static void BlendA64MaskHbd(ushort[] dst, int dstOff, int dstStride, ushort[] src0, int s0Off, int s0Stride, ushort[] src1, int s1Off,
        int s1Stride, byte[] mask, int maskOff, int maskStride, int w, int h, int subw, int subh)
    {
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
                dst[dstOff + i * dstStride + j] = (ushort)Blend(MaskAt(mask, maskOff, maskStride, i, j, subw, subh), src0[s0Off + i * s0Stride + j],
                    src1[s1Off + i * s1Stride + j]);
    }

    /// <summary>aom_blend_a64_vmask (a mask value per row).</summary>
    private static void BlendA64Vmask(byte[] dst, int dstOff, int dstStride, byte[] src1, int s1Off, int s1Stride, byte[] mask, int w, int h)
    {
        for (int i = 0; i < h; ++i)
        {
            int m = mask[i];
            for (int j = 0; j < w; ++j) dst[dstOff + i * dstStride + j] = (byte)Blend(m, dst[dstOff + i * dstStride + j], src1[s1Off + i * s1Stride + j]);
        }
    }

    private static void BlendA64VmaskHbd(ushort[] dst, int dstOff, int dstStride, ushort[] src1, int s1Off, int s1Stride, byte[] mask, int w, int h)
    {
        for (int i = 0; i < h; ++i)
        {
            int m = mask[i];
            for (int j = 0; j < w; ++j) dst[dstOff + i * dstStride + j] = (ushort)Blend(m, dst[dstOff + i * dstStride + j], src1[s1Off + i * s1Stride + j]);
        }
    }

    /// <summary>aom_blend_a64_hmask (a mask value per column).</summary>
    private static void BlendA64Hmask(byte[] dst, int dstOff, int dstStride, byte[] src1, int s1Off, int s1Stride, byte[] mask, int w, int h)
    {
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
                dst[dstOff + i * dstStride + j] = (byte)Blend(mask[j], dst[dstOff + i * dstStride + j], src1[s1Off + i * s1Stride + j]);
    }

    private static void BlendA64HmaskHbd(ushort[] dst, int dstOff, int dstStride, ushort[] src1, int s1Off, int s1Stride, byte[] mask, int w, int h)
    {
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
                dst[dstOff + i * dstStride + j] = (ushort)Blend(mask[j], dst[dstOff + i * dstStride + j], src1[s1Off + i * s1Stride + j]);
    }

    /// <summary>aom_lowbd_blend_a64_d16_mask / aom_highbd_blend_a64_d16_mask.</summary>
    private static void BlendA64D16Mask(byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride, ushort[] src0, int s0Off, int s0Stride,
        ushort[] src1, int s1Off, int s1Stride, byte[] mask, int maskStride, int w, int h, int subw, int subh, ref AomConvParams cp, int bd)
    {
        int offsetBits = bd + 2 * 7 - cp.Round0;
        int roundOffset = (1 << (offsetBits - cp.Round1)) + (1 << (offsetBits - cp.Round1 - 1));
        int roundBits = 2 * 7 - cp.Round0 - cp.Round1;
        int sat = (1 << bd) - 1;
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int m = MaskAt(mask, 0, maskStride, i, j, subw, subh);
                int res = (m * src0[s0Off + i * s0Stride + j] + (64 - m) * src1[s1Off + i * s1Stride + j]) >> 6;
                res -= roundOffset;
                int v = (res + ((1 << roundBits) >> 1)) >> roundBits;
                v = v < 0 ? 0 : v > sat ? sat : v;
                if (dst16 != null) dst16[dstOff + i * dstStride + j] = (ushort)v;
                else dst8![dstOff + i * dstStride + j] = (byte)v;
            }
    }

    // ---- wedge / smooth inter-intra masks (reconinter.c init_all_wedge_masks) ---------------------------------------

    private const int DIFF_FACTOR = 16;
    private const int MASK_MASTER_SIZE = 64, MASK_MASTER_STRIDE = 64, WEDGE_WEIGHT_BITS = 6, MAX_WEDGE_SIZE = 32;
    private const int WEDGE_HORIZONTAL = 0, WEDGE_VERTICAL = 1, WEDGE_OBLIQUE27 = 2, WEDGE_OBLIQUE63 = 3, WEDGE_OBLIQUE117 = 4, WEDGE_OBLIQUE153 = 5;

    private static readonly byte[] WedgeMasterObliqueOdd =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 6, 18,
        37, 53, 60, 63, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
    };
    private static readonly byte[] WedgeMasterObliqueEven =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 4, 11, 27,
        46, 58, 62, 63, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
    };
    private static readonly byte[] WedgeMasterVertical =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 7, 21,
        43, 57, 62, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
    };

    // wedge_signflip_lookup [BLOCK_SIZES_ALL][MAX_WEDGE_TYPES] (the rows of the wedge sizes)
    private static readonly byte[][] WedgeSignflip = BuildSignflip();
    private static byte[][] BuildSignflip()
    {
        var a = new byte[BLOCK_SIZES_ALL][];
        for (int i = 0; i < a.Length; i++) a[i] = new byte[16];
        a[BLOCK_8X8] = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_8X16] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_16X8] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_16X16] = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_16X32] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_32X16] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_32X32] = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_8X32] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1 };
        a[BLOCK_32X8] = new byte[] { 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 0, 1 };
        return a;
    }

    // wedge codebooks {direction, x_offset, y_offset}
    private static readonly int[,] CodebookHgtw =
    {
        { WEDGE_OBLIQUE27, 4, 4 }, { WEDGE_OBLIQUE63, 4, 4 }, { WEDGE_OBLIQUE117, 4, 4 }, { WEDGE_OBLIQUE153, 4, 4 },
        { WEDGE_HORIZONTAL, 4, 2 }, { WEDGE_HORIZONTAL, 4, 4 }, { WEDGE_HORIZONTAL, 4, 6 }, { WEDGE_VERTICAL, 4, 4 },
        { WEDGE_OBLIQUE27, 4, 2 }, { WEDGE_OBLIQUE27, 4, 6 }, { WEDGE_OBLIQUE153, 4, 2 }, { WEDGE_OBLIQUE153, 4, 6 },
        { WEDGE_OBLIQUE63, 2, 4 }, { WEDGE_OBLIQUE63, 6, 4 }, { WEDGE_OBLIQUE117, 2, 4 }, { WEDGE_OBLIQUE117, 6, 4 },
    };
    private static readonly int[,] CodebookHltw =
    {
        { WEDGE_OBLIQUE27, 4, 4 }, { WEDGE_OBLIQUE63, 4, 4 }, { WEDGE_OBLIQUE117, 4, 4 }, { WEDGE_OBLIQUE153, 4, 4 },
        { WEDGE_VERTICAL, 2, 4 }, { WEDGE_VERTICAL, 4, 4 }, { WEDGE_VERTICAL, 6, 4 }, { WEDGE_HORIZONTAL, 4, 4 },
        { WEDGE_OBLIQUE27, 4, 2 }, { WEDGE_OBLIQUE27, 4, 6 }, { WEDGE_OBLIQUE153, 4, 2 }, { WEDGE_OBLIQUE153, 4, 6 },
        { WEDGE_OBLIQUE63, 2, 4 }, { WEDGE_OBLIQUE63, 6, 4 }, { WEDGE_OBLIQUE117, 2, 4 }, { WEDGE_OBLIQUE117, 6, 4 },
    };
    private static readonly int[,] CodebookHeqw =
    {
        { WEDGE_OBLIQUE27, 4, 4 }, { WEDGE_OBLIQUE63, 4, 4 }, { WEDGE_OBLIQUE117, 4, 4 }, { WEDGE_OBLIQUE153, 4, 4 },
        { WEDGE_HORIZONTAL, 4, 2 }, { WEDGE_HORIZONTAL, 4, 6 }, { WEDGE_VERTICAL, 2, 4 }, { WEDGE_VERTICAL, 6, 4 },
        { WEDGE_OBLIQUE27, 4, 2 }, { WEDGE_OBLIQUE27, 4, 6 }, { WEDGE_OBLIQUE153, 4, 2 }, { WEDGE_OBLIQUE153, 4, 6 },
        { WEDGE_OBLIQUE63, 2, 4 }, { WEDGE_OBLIQUE63, 6, 4 }, { WEDGE_OBLIQUE117, 2, 4 }, { WEDGE_OBLIQUE117, 6, 4 },
    };

    /// <summary>av1_wedge_params_lookup[bsize].codebook (null: no wedges).</summary>
    private static int[,]? Codebook(int bsize) => bsize switch
    {
        BLOCK_8X8 or BLOCK_16X16 or BLOCK_32X32 => CodebookHeqw,
        BLOCK_8X16 or BLOCK_16X32 or BLOCK_8X32 => CodebookHgtw,
        BLOCK_16X8 or BLOCK_32X16 or BLOCK_32X8 => CodebookHltw,
        _ => null,
    };

    /// <summary>get_wedge_types_lookup / av1_is_wedge_used.</summary>
    public static int WedgeTypes(int bsize) => Codebook(bsize) != null ? MAX_WEDGE_TYPES : 0;
    public static bool IsWedgeUsed(int bsize) => Codebook(bsize) != null;

    // wedge_mask_obl[2][WEDGE_DIRECTIONS][MASK_MASTER_SIZE * MASK_MASTER_SIZE], the per-size masks [bsize][sign][wedge]
    // (bw x bh, stride bw) and smooth_interintra_mask_buf[INTERINTRA_MODES][BLOCK_SIZES_ALL]
    private static readonly byte[,][] WedgeMaskObl = new byte[2, 6][];
    private static readonly byte[][][][] WedgeMasks = new byte[BLOCK_SIZES_ALL][][][];
    private static readonly byte[][][] SmoothInterintraMasks = new byte[INTERINTRA_MODES][][];

    static AomInterPred()
    {
        for (int s = 0; s < 2; s++)
            for (int d = 0; d < 6; d++) WedgeMaskObl[s, d] = new byte[MASK_MASTER_SIZE * MASK_MASTER_SIZE];
        InitWedgeMasterMasks();
        InitWedgeMasks();
        InitSmoothInterintraMasks();
    }

    private static void ShiftCopy(byte[] src, byte[] dst, int dstOff, int shift, int width)
    {
        if (shift >= 0)
        {
            Array.Copy(src, 0, dst, dstOff + shift, width - shift);
            Array.Fill(dst, src[0], dstOff, shift);
        }
        else
        {
            shift = -shift;
            Array.Copy(src, shift, dst, dstOff, width - shift);
            Array.Fill(dst, src[width - 1], dstOff + width - shift, shift);
        }
    }

    private static void InitWedgeMasterMasks()
    {
        const int w = MASK_MASTER_SIZE, h = MASK_MASTER_SIZE, stride = MASK_MASTER_STRIDE;
        int shift = h / 4;
        for (int i = 0; i < h; i += 2)
        {
            ShiftCopy(WedgeMasterObliqueEven, WedgeMaskObl[0, WEDGE_OBLIQUE63], i * stride, shift, MASK_MASTER_SIZE);
            shift--;
            ShiftCopy(WedgeMasterObliqueOdd, WedgeMaskObl[0, WEDGE_OBLIQUE63], (i + 1) * stride, shift, MASK_MASTER_SIZE);
            Array.Copy(WedgeMasterVertical, 0, WedgeMaskObl[0, WEDGE_VERTICAL], i * stride, MASK_MASTER_SIZE);
            Array.Copy(WedgeMasterVertical, 0, WedgeMaskObl[0, WEDGE_VERTICAL], (i + 1) * stride, MASK_MASTER_SIZE);
        }
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int msk = WedgeMaskObl[0, WEDGE_OBLIQUE63][i * stride + j];
                WedgeMaskObl[0, WEDGE_OBLIQUE27][j * stride + i] = (byte)msk;
                WedgeMaskObl[0, WEDGE_OBLIQUE117][i * stride + w - 1 - j] = WedgeMaskObl[0, WEDGE_OBLIQUE153][(w - 1 - j) * stride + i] =
                    (byte)((1 << WEDGE_WEIGHT_BITS) - msk);
                WedgeMaskObl[1, WEDGE_OBLIQUE63][i * stride + j] = WedgeMaskObl[1, WEDGE_OBLIQUE27][j * stride + i] =
                    (byte)((1 << WEDGE_WEIGHT_BITS) - msk);
                WedgeMaskObl[1, WEDGE_OBLIQUE117][i * stride + w - 1 - j] = WedgeMaskObl[1, WEDGE_OBLIQUE153][(w - 1 - j) * stride + i] = (byte)msk;
                int mskx = WedgeMaskObl[0, WEDGE_VERTICAL][i * stride + j];
                WedgeMaskObl[0, WEDGE_HORIZONTAL][j * stride + i] = (byte)mskx;
                WedgeMaskObl[1, WEDGE_VERTICAL][i * stride + j] = WedgeMaskObl[1, WEDGE_HORIZONTAL][j * stride + i] =
                    (byte)((1 << WEDGE_WEIGHT_BITS) - mskx);
            }
    }

    /// <summary>get_wedge_mask_inplace: (master array, offset).</summary>
    private static (byte[] Master, int Offset) GetWedgeMaskInplace(int wedgeIndex, int neg, int bsize)
    {
        int bh = BlockSizeHigh[bsize], bw = BlockSizeWide[bsize];
        var cb = Codebook(bsize)!;
        int wsignflip = WedgeSignflip[bsize][wedgeIndex];
        int woff = (cb[wedgeIndex, 1] * bw) >> 3, hoff = (cb[wedgeIndex, 2] * bh) >> 3;
        return (WedgeMaskObl[neg ^ wsignflip, cb[wedgeIndex, 0]], MASK_MASTER_STRIDE * (MASK_MASTER_SIZE / 2 - hoff) + MASK_MASTER_SIZE / 2 - woff);
    }

    private static void InitWedgeMasks()
    {
        for (int bsize = BLOCK_4X4; bsize < BLOCK_SIZES_ALL; ++bsize)
        {
            if (Codebook(bsize) == null) continue;
            int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
            WedgeMasks[bsize] = new byte[2][][];
            for (int s = 0; s < 2; s++) WedgeMasks[bsize][s] = new byte[MAX_WEDGE_TYPES][];
            for (int w = 0; w < MAX_WEDGE_TYPES; ++w)
                for (int s = 0; s < 2; s++)
                {
                    var (master, off) = GetWedgeMaskInplace(w, s, bsize);
                    var m = new byte[bw * bh];
                    for (int r = 0; r < bh; r++) Array.Copy(master, off + r * MASK_MASTER_STRIDE, m, r * bw, bw);
                    WedgeMasks[bsize][s][w] = m;
                }
        }
    }

    /// <summary>av1_get_contiguous_soft_mask.</summary>
    public static byte[] GetContiguousSoftMask(int wedgeIndex, int wedgeSign, int bsize) => WedgeMasks[bsize][wedgeSign][wedgeIndex];

    private static readonly byte[] IiWeights1d =
    {
        60, 58, 56, 54, 52, 50, 48, 47, 45, 44, 42, 41, 39, 38, 37, 35, 34, 33, 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 22, 21, 20, 19, 19, 18,
        18, 17, 16, 16, 15, 15, 14, 14, 13, 13, 12, 12, 12, 11, 11, 10, 10, 10, 9, 9, 9, 8, 8, 8, 8, 7, 7, 7, 7, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 4, 4,
        4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1,
    };
    private static readonly byte[] IiSizeScales = { 32, 16, 16, 16, 8, 8, 8, 4, 4, 4, 2, 2, 2, 1, 1, 1, 8, 8, 4, 4, 2, 2 };

    /// <summary>build_smooth_interintra_mask.</summary>
    private static void BuildSmoothInterintraMask(byte[] mask, int stride, int planeBsize, int mode)
    {
        int bw = BlockSizeWide[planeBsize], bh = BlockSizeHigh[planeBsize];
        int sizeScale = IiSizeScales[planeBsize];
        for (int i = 0; i < bh; ++i)
            for (int j = 0; j < bw; ++j)
                mask[i * stride + j] = mode switch
                {
                    II_V_PRED => IiWeights1d[i * sizeScale],
                    II_H_PRED => IiWeights1d[j * sizeScale],
                    II_SMOOTH_PRED => IiWeights1d[(i < j ? i : j) * sizeScale],
                    _ => 32,
                };
    }

    private static void InitSmoothInterintraMasks()
    {
        for (int m = 0; m < INTERINTRA_MODES; ++m)
        {
            SmoothInterintraMasks[m] = new byte[BLOCK_SIZES_ALL][];
            for (int bs = 0; bs < BLOCK_SIZES_ALL; ++bs)
            {
                int bw = BlockSizeWide[bs], bh = BlockSizeHigh[bs];
                if (bw > MAX_WEDGE_SIZE || bh > MAX_WEDGE_SIZE) continue;
                SmoothInterintraMasks[m][bs] = new byte[MAX_WEDGE_SIZE * MAX_WEDGE_SIZE];
                BuildSmoothInterintraMask(SmoothInterintraMasks[m][bs], bw, bs, m);
            }
        }
    }

    // ---- masked compound -----------------------------------------------------------------------------------------

    /// <summary>av1_get_compound_type_mask.</summary>
    public static byte[] GetCompoundTypeMask(in AomInterinterCompound comp, int sbType)
        => comp.Type == COMPOUND_WEDGE ? GetContiguousSoftMask(comp.WedgeIndex, comp.WedgeSign, sbType) : comp.SegMask!;

    /// <summary>av1_build_compound_diffwtd_mask_d16.</summary>
    public static void BuildCompoundDiffwtdMaskD16(byte[] mask, int maskType, ushort[] src0, int s0Off, int s0Stride, ushort[] src1, int s1Off,
        int s1Stride, int h, int w, ref AomConvParams cp, int bd)
    {
        int which = maskType == DIFFWTD_38_INV ? 1 : 0;
        int round = 2 * 7 - cp.Round0 - cp.Round1 + (bd - 8);
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int diff = Math.Abs(src0[s0Off + i * s0Stride + j] - src1[s1Off + i * s1Stride + j]);
                diff = (diff + ((1 << round) >> 1)) >> round;
                int m = Math.Clamp(38 + diff / DIFF_FACTOR, 0, 64);
                mask[i * w + j] = (byte)(which != 0 ? 64 - m : m);
            }
    }

    /// <summary>av1_build_compound_diffwtd_mask (8-bit predictions).</summary>
    public static void BuildCompoundDiffwtdMask(byte[] mask, int maskType, byte[] src0, int s0Off, int s0Stride, byte[] src1, int s1Off, int s1Stride,
        int h, int w)
    {
        int which = maskType == DIFFWTD_38_INV ? 1 : 0;
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int diff = Math.Abs(src0[s0Off + i * s0Stride + j] - src1[s1Off + i * s1Stride + j]);
                int m = Math.Clamp(38 + diff / DIFF_FACTOR, 0, 64);
                mask[i * w + j] = (byte)(which != 0 ? 64 - m : m);
            }
    }

    /// <summary>av1_build_compound_diffwtd_mask_highbd.</summary>
    public static void BuildCompoundDiffwtdMaskHbd(byte[] mask, int maskType, ushort[] src0, int s0Off, int s0Stride, ushort[] src1, int s1Off,
        int s1Stride, int h, int w, int bd)
    {
        int which = maskType == DIFFWTD_38_INV ? 1 : 0;
        int bdShift = bd - 8;
        for (int i = 0; i < h; ++i)
            for (int j = 0; j < w; ++j)
            {
                int diff = (Math.Abs(src0[s0Off + i * s0Stride + j] - src1[s1Off + i * s1Stride + j]) >> bdShift) / DIFF_FACTOR;
                int m = Math.Min(Math.Max(38 + diff, 0), 64);
                mask[i * w + j] = (byte)(which != 0 ? 64 - m : m);
            }
    }

    [ThreadStatic] private static ushort[]? t_maskTmp16;
    [ThreadStatic] private static byte[]? t_maskTmp8;

    /// <summary>av1_make_masked_inter_predictor.</summary>
    private static void MakeMaskedInterPredictor(AomInterPredParams p, in AomBuf2d pre, int srcOff, byte[]? dst8, ushort[]? dst16, int dstOff,
        int dstStride, in AomSubpelParams sp)
    {
        var comp = p.MaskComp;
        int sbType = p.SbType;
        var tmpBuf16 = t_maskTmp16 ??= new ushort[2 * 128 * 128];
        var tmp8 = t_maskTmp8 ??= new byte[2 * 128 * 128];
        var orgDst = p.ConvParams.Dst!;
        int orgDstOff = p.ConvParams.DstOffset, orgDstStride = p.ConvParams.DstStride;
        p.ConvParams.Dst = tmpBuf16;
        p.ConvParams.DstOffset = 0;
        p.ConvParams.DstStride = MAX_SB_SIZE;
        // the convolution writes the second prediction into tmp_buf16 (do_average 0); tmp_dst is unused
        MakeInterPredictor(p, pre, srcOff, tmp8, p.UseHbdBuf ? new ushort[0] : null, 0, MAX_SB_SIZE, sp);
        if (p.ConvParams.Plane == 0 && comp.Type == COMPOUND_DIFFWTD)
            BuildCompoundDiffwtdMaskD16(comp.SegMask!, comp.MaskType, orgDst, orgDstOff, orgDstStride, tmpBuf16, 0, MAX_SB_SIZE, p.BlockHeight,
                p.BlockWidth, ref p.ConvParams, p.BitDepth);
        var mask = GetCompoundTypeMask(comp, sbType);
        BlendA64D16Mask(dst8, p.UseHbdBuf ? dst16 : null, dstOff, dstStride, orgDst, orgDstOff, orgDstStride, tmpBuf16, 0, MAX_SB_SIZE, mask,
            BlockSizeWide[sbType], p.BlockWidth, p.BlockHeight, p.SubsamplingX, p.SubsamplingY, ref p.ConvParams, p.UseHbdBuf ? p.BitDepth : 8);
        p.ConvParams.Dst = orgDst;
        p.ConvParams.DstOffset = orgDstOff;
        p.ConvParams.DstStride = orgDstStride;
    }

    // ---- inter-intra -------------------------------------------------------------------------------------------------

    private static readonly int[] InterintraToIntraMode = { DC_PRED, V_PRED, H_PRED, SMOOTH_PRED };

    [ThreadStatic] private static byte[]? t_intraPred;
    [ThreadStatic] private static ushort[]? t_intraPred16;

    /// <summary>av1_build_intra_predictors_for_interintra.</summary>
    public static void BuildIntraPredictorsForInterintra(AomMacroblockD xd, int sbSize, bool enableIntraEdgeFilter, int bsize, int plane,
        AomBufferSet ctx, byte[]? dst8, ushort[]? dst16, int dstOff, int dstStride)
    {
        var pd = xd.Plane[plane];
        int planeBsize = AomCfl.GetPlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        int mode = InterintraToIntraMode[xd.Mi0.InterintraMode];
        ref var c = ref ctx.Plane[plane];
        if (xd.IsHbd)
            AomReconIntra.PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, pd.Width, pd.Height, MaxTxsizeRectLookup[planeBsize], mode, 0, false,
                FILTER_INTRA_MODES, c.Buf16, c.Offset, c.Stride, dst16!, dstOff, dstStride, 0, 0, plane);
        else
            AomReconIntra.PredictIntraBlock(xd, sbSize, enableIntraEdgeFilter, pd.Width, pd.Height, MaxTxsizeRectLookup[planeBsize], mode, 0, false,
                FILTER_INTRA_MODES, c.Buf, c.Offset, c.Stride, dst8!, dstOff, dstStride, 0, 0, plane);
    }

    /// <summary>av1_combine_interintra (combine_interintra / combine_interintra_highbd) into pd->dst.</summary>
    public static void CombineInterintra(AomMacroblockD xd, int bsize, int plane, in AomBuf2d inter, byte[]? intra8, ushort[]? intra16,
        int intraOff, int intraStride)
    {
        var pd = xd.Plane[plane];
        var mi = xd.Mi0;
        int planeBsize = AomCfl.GetPlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        int bw = BlockSizeWide[planeBsize], bh = BlockSizeHigh[planeBsize];
        ref var d = ref pd.Dst;
        if (mi.UseWedgeInterintra != 0)
        {
            if (IsWedgeUsed(bsize))
            {
                var mask = GetContiguousSoftMask(mi.InterintraWedgeIndex, 0, bsize);   // INTERINTRA_WEDGE_SIGN 0
                int subw = 2 * MiSizeWide[bsize] == bw ? 1 : 0, subh = 2 * MiSizeHigh[bsize] == bh ? 1 : 0;
                if (xd.IsHbd)
                    BlendA64MaskHbd(d.Buf16, d.Offset, d.Stride, intra16!, intraOff, intraStride, inter.Buf16, inter.Offset, inter.Stride, mask, 0,
                        BlockSizeWide[bsize], bw, bh, subw, subh);
                else
                    BlendA64Mask(d.Buf, d.Offset, d.Stride, intra8!, intraOff, intraStride, inter.Buf, inter.Offset, inter.Stride, mask, 0,
                        BlockSizeWide[bsize], bw, bh, subw, subh);
            }
            return;
        }
        byte[] smask;
        if (xd.IsHbd)
        {
            smask = new byte[MAX_SB_SQUARE];
            BuildSmoothInterintraMask(smask, bw, planeBsize, mi.InterintraMode);
            BlendA64MaskHbd(d.Buf16, d.Offset, d.Stride, intra16!, intraOff, intraStride, inter.Buf16, inter.Offset, inter.Stride, smask, 0, bw, bw,
                bh, 0, 0);
        }
        else
        {
            smask = SmoothInterintraMasks[mi.InterintraMode][planeBsize];
            BlendA64Mask(d.Buf, d.Offset, d.Stride, intra8!, intraOff, intraStride, inter.Buf, inter.Offset, inter.Stride, smask, 0, bw, bw, bh, 0, 0);
        }
    }

    /// <summary>av1_build_interintra_predictor (pred = the inter prediction, combined into pd->dst).</summary>
    public static void BuildInterintraPredictor(AomCommon cm, AomMacroblockD xd, in AomBuf2d pred, AomBufferSet ctx, int plane, int bsize,
        bool enableIntraEdgeFilter)
    {
        if (xd.IsHbd)
        {
            var ip = t_intraPred16 ??= new ushort[MAX_SB_SQUARE];
            BuildIntraPredictorsForInterintra(xd, cm.SbSize, enableIntraEdgeFilter, bsize, plane, ctx, null, ip, 0, MAX_SB_SIZE);
            CombineInterintra(xd, bsize, plane, pred, null, ip, 0, MAX_SB_SIZE);
        }
        else
        {
            var ip = t_intraPred ??= new byte[MAX_SB_SQUARE];
            BuildIntraPredictorsForInterintra(xd, cm.SbSize, enableIntraEdgeFilter, bsize, plane, ctx, ip, null, 0, MAX_SB_SIZE);
            CombineInterintra(xd, bsize, plane, pred, ip, null, 0, MAX_SB_SIZE);
        }
    }

    // ---- OBMC ----------------------------------------------------------------------------------------------------------

    private static readonly byte[] ObmcMask1 = { 64 }, ObmcMask2 = { 45, 64 }, ObmcMask4 = { 39, 50, 59, 64 };
    private static readonly byte[] ObmcMask8 = { 36, 42, 48, 53, 57, 61, 64, 64 };
    private static readonly byte[] ObmcMask16 = { 34, 37, 40, 43, 46, 49, 52, 54, 56, 58, 60, 61, 64, 64, 64, 64 };
    private static readonly byte[] ObmcMask32 =
    {
        33, 35, 36, 38, 40, 41, 43, 44, 45, 47, 48, 50, 51, 52, 53, 55, 56, 57, 58, 59, 60, 60, 61, 62, 64, 64, 64, 64, 64, 64, 64, 64,
    };
    private static readonly byte[] ObmcMask64 =
    {
        33, 34, 35, 35, 36, 37, 38, 39, 40, 40, 41, 42, 43, 44, 44, 44, 45, 46, 47, 47, 48, 49, 50, 51, 51, 51, 52, 52, 53, 54, 55, 56,
        56, 56, 57, 57, 58, 58, 59, 60, 60, 60, 60, 60, 61, 62, 62, 62, 62, 62, 63, 63, 63, 63, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
    };

    /// <summary>av1_get_obmc_mask.</summary>
    public static byte[] GetObmcMask(int length) => length switch
    {
        1 => ObmcMask1, 2 => ObmcMask2, 4 => ObmcMask4, 8 => ObmcMask8, 16 => ObmcMask16, 32 => ObmcMask32, 64 => ObmcMask64,
        _ => throw new ArgumentOutOfRangeException(nameof(length)),
    };

    /// <summary>av1_skip_u4x4_pred_in_obmc.</summary>
    public static bool SkipU4x4PredInObmc(int bsize, AomMbdPlane pd, int dir)
    {
        int bsizePlane = AomCfl.GetPlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        return (bsizePlane == BLOCK_4X4 || bsizePlane == BLOCK_8X4 || bsizePlane == BLOCK_4X8) && dir == 0;
    }

    /// <summary>The visitor of foreach_overlappable_nb_above / left: (rel mi row, rel mi col, op mi size, dir, neighbour).</summary>
    public delegate void OverlappableNbVisitor(int relMiRow, int relMiCol, int opMiSize, int dir, AomMbModeInfo nb);

    /// <summary>foreach_overlappable_nb_above.</summary>
    public static void ForeachOverlappableNbAbove(AomCommon cm, AomMacroblockD xd, int nbMax, OverlappableNbVisitor fun)
    {
        if (!xd.UpAvailable) return;
        int nbCount = 0;
        int miCol = xd.MiCol;
        int prevRow = xd.MiOffset - miCol - xd.MiStride;
        int endCol = Math.Min(miCol + xd.Width, cm.MiCols);
        int miStep;
        for (int aboveMiCol = miCol; aboveMiCol < endCol && nbCount < nbMax; aboveMiCol += miStep)
        {
            var aboveMi = xd.MiGrid[prevRow + aboveMiCol]!;
            miStep = Math.Min(MiSizeWide[aboveMi.Bsize], MiSizeWide[BLOCK_64X64]);
            if (miStep == 1)
            {
                aboveMiCol &= ~1;
                aboveMi = xd.MiGrid[prevRow + aboveMiCol + 1]!;
                miStep = 2;
            }
            if (aboveMi.IsInterBlock)
            {
                ++nbCount;
                fun(0, aboveMiCol - miCol, Math.Min(xd.Width, miStep), 0, aboveMi);
            }
        }
    }

    /// <summary>foreach_overlappable_nb_left.</summary>
    public static void ForeachOverlappableNbLeft(AomCommon cm, AomMacroblockD xd, int nbMax, OverlappableNbVisitor fun)
    {
        if (!xd.LeftAvailable) return;
        int nbCount = 0;
        int miRow = xd.MiRow;
        int prevCol = xd.MiOffset - 1 - miRow * xd.MiStride;
        int endRow = Math.Min(miRow + xd.Height, cm.MiRows);
        int miStep;
        for (int leftMiRow = miRow; leftMiRow < endRow && nbCount < nbMax; leftMiRow += miStep)
        {
            var leftMi = xd.MiGrid[prevCol + leftMiRow * xd.MiStride]!;
            miStep = Math.Min(MiSizeHigh[leftMi.Bsize], MiSizeHigh[BLOCK_64X64]);
            if (miStep == 1)
            {
                leftMiRow &= ~1;
                leftMi = xd.MiGrid[prevCol + (leftMiRow + 1) * xd.MiStride]!;
                miStep = 2;
            }
            if (leftMi.IsInterBlock)
            {
                ++nbCount;
                fun(leftMiRow - miRow, 0, Math.Min(xd.Height, miStep), 1, leftMi);
            }
        }
    }

    /// <summary>av1_count_overlappable_neighbors.</summary>
    public static void CountOverlappableNeighbors(AomCommon cm, AomMacroblockD xd)
    {
        var mbmi = xd.Mi0;
        mbmi.OverlappableNeighbors = 0;
        if (!AomInter.IsMotionVariationAllowedBsize(mbmi.Bsize)) return;
        int count = 0;
        ForeachOverlappableNbAbove(cm, xd, int.MaxValue, (_, _, _, _, _) => count++);
        mbmi.OverlappableNeighbors = (byte)count;
        if (count != 0) return;
        ForeachOverlappableNbLeft(cm, xd, int.MaxValue, (_, _, _, _, _) => count++);
        mbmi.OverlappableNeighbors = (byte)count;
    }

    /// <summary>av1_build_obmc_inter_prediction: blends the above / left neighbour predictions into pd->dst.</summary>
    public static void BuildObmcInterPrediction(AomCommon cm, AomMacroblockD xd, AomBuf2d[] above, AomBuf2d[] left)
    {
        int bsize = xd.Mi0.Bsize;
        int numPlanes = cm.NumPlanes;
        ForeachOverlappableNbAbove(cm, xd, AomInter.MaxNeighborObmc[MiSizeWideLog2[bsize]], (relMiRow, relMiCol, opMiSize, dir, nb) =>
        {
            int overlap = Math.Min(BlockSizeHigh[bsize], BlockSizeHigh[BLOCK_64X64]) >> 1;
            for (int plane = 0; plane < numPlanes; ++plane)
            {
                var pd = xd.Plane[plane];
                int bw = (opMiSize * 4) >> pd.SubsamplingX, bh = overlap >> pd.SubsamplingY;
                int planeCol = (relMiCol * 4) >> pd.SubsamplingX;
                if (SkipU4x4PredInObmc(bsize, pd, 0)) continue;
                ref var d = ref pd.Dst;
                var tmp = above[plane];
                var mask = GetObmcMask(bh);
                if (xd.IsHbd) BlendA64VmaskHbd(d.Buf16, d.Offset + planeCol, d.Stride, tmp.Buf16, tmp.Offset + planeCol, tmp.Stride, mask, bw, bh);
                else BlendA64Vmask(d.Buf, d.Offset + planeCol, d.Stride, tmp.Buf, tmp.Offset + planeCol, tmp.Stride, mask, bw, bh);
            }
        });
        ForeachOverlappableNbLeft(cm, xd, AomInter.MaxNeighborObmc[MiSizeHighLog2[bsize]], (relMiRow, relMiCol, opMiSize, dir, nb) =>
        {
            int overlap = Math.Min(BlockSizeWide[bsize], BlockSizeWide[BLOCK_64X64]) >> 1;
            for (int plane = 0; plane < numPlanes; ++plane)
            {
                var pd = xd.Plane[plane];
                int bw = overlap >> pd.SubsamplingX, bh = (opMiSize * 4) >> pd.SubsamplingY;
                int planeRow = (relMiRow * 4) >> pd.SubsamplingY;
                if (SkipU4x4PredInObmc(bsize, pd, 1)) continue;
                ref var d = ref pd.Dst;
                var tmp = left[plane];
                var mask = GetObmcMask(bw);
                if (xd.IsHbd)
                    BlendA64HmaskHbd(d.Buf16, d.Offset + planeRow * d.Stride, d.Stride, tmp.Buf16, tmp.Offset + planeRow * tmp.Stride, tmp.Stride, mask, bw, bh);
                else BlendA64Hmask(d.Buf, d.Offset + planeRow * d.Stride, d.Stride, tmp.Buf, tmp.Offset + planeRow * tmp.Stride, tmp.Stride, mask, bw, bh);
            }
        });
    }

    /// <summary>av1_setup_obmc_dst_bufs: the two neighbour prediction buffers (three planes of MAX_SB_SQUARE, stride MAX_SB_SIZE).</summary>
    public static (AomBuf2d[] Buf1, AomBuf2d[] Buf2) SetupObmcDstBufs(AomMacroblockD xd)
    {
        var b1 = new AomBuf2d[3];
        var b2 = new AomBuf2d[3];
        for (int p = 0; p < 3; p++)
        {
            b1[p] = new AomBuf2d { Buf = xd.TmpObmcBufs[0], Buf16 = xd.TmpObmcBufs16[0], Offset = p * MAX_SB_SQUARE, Offset0 = p * MAX_SB_SQUARE,
                Stride = MAX_SB_SIZE, Width = MAX_SB_SIZE, Height = MAX_SB_SIZE };
            b2[p] = new AomBuf2d { Buf = xd.TmpObmcBufs[1], Buf16 = xd.TmpObmcBufs16[1], Offset = p * MAX_SB_SQUARE, Offset0 = p * MAX_SB_SQUARE,
                Stride = MAX_SB_SIZE, Width = MAX_SB_SIZE, Height = MAX_SB_SIZE };
        }
        return (b1, b2);
    }

    /// <summary>reconinter_enc.c build_obmc_prediction for one neighbour: its single-reference prediction of the overlap
    /// into the temporary planes (setup_address_for_obmc + av1_enc_build_one_inter_predictor).</summary>
    private static void BuildObmcPrediction(AomCommon cm, AomMacroblockD xd, int relMiRow, int relMiCol, int opMiSize, int dir, AomMbModeInfo nb,
        AomBuf2d[] tmp)
    {
        int numPlanes = cm.NumPlanes;
        // setup_address_for_obmc
        int refBsize = Math.Max(BLOCK_8X8, nb.Bsize);
        int refMiRow = xd.MiRow + relMiRow, refMiCol = xd.MiCol + relMiCol;
        var dsts = new AomBuf2d[3];
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            var pd = xd.Plane[plane];
            // setup_pred_plane(&pd->dst, ref_bsize, tmp_buf, ..., mi_row_offset, mi_col_offset, NULL, ss)
            int r = relMiRow, c = relMiCol;
            if (pd.SubsamplingY != 0 && (r & 1) != 0 && MiSizeHigh[refBsize] == 1) r -= 1;
            if (pd.SubsamplingX != 0 && (c & 1) != 0 && MiSizeWide[refBsize] == 1) c -= 1;
            int x = (4 * c) >> pd.SubsamplingX, y = (4 * r) >> pd.SubsamplingY;
            dsts[plane] = tmp[plane];
            dsts[plane].Offset = tmp[plane].Offset0 + y * tmp[plane].Stride + x;
        }
        int frame = nb.RefFrame0;
        var refBuf = cm.RefBufs[frame]!;
        var sf = cm.RefScaleFactors[frame]!;
        xd.BlockRefScaleFactors[0] = sf;
        // av1_setup_pre_planes(xd, 0, &ref_buf->buf, ref_mi_row, ref_mi_col, sf, num_planes) (bsize: the current block's)
        for (int i = 0; i < numPlanes; ++i)
        {
            var pd = xd.Plane[i];
            SetupPredPlane(ref pd.Pre0, xd.Mi0.Bsize, refBuf.Buf, i, refMiRow, refMiCol, sf, pd.SubsamplingX, pd.SubsamplingY);
        }
        int miX = (xd.MiCol + relMiCol) << 2, miY = (xd.MiRow + relMiRow) << 2;
        int bsize = xd.Mi0.Bsize;
        var p = new AomInterPredParams();
        for (int j = 0; j < numPlanes; ++j)
        {
            var pd = xd.Plane[j];
            int bw, bh;
            if (dir != 0)
            {
                bw = Math.Clamp(BlockSizeWide[bsize] >> (pd.SubsamplingX + 1), 4, BlockSizeWide[BLOCK_64X64] >> (pd.SubsamplingX + 1));
                bh = (opMiSize << 2) >> pd.SubsamplingY;
            }
            else
            {
                bw = (opMiSize * 4) >> pd.SubsamplingX;
                bh = Math.Clamp(BlockSizeHigh[bsize] >> (pd.SubsamplingY + 1), 4, BlockSizeHigh[BLOCK_64X64] >> (pd.SubsamplingY + 1));
            }
            if (SkipU4x4PredInObmc(bsize, pd, dir)) continue;
            InitInterParams(p, bw, bh, miY >> pd.SubsamplingY, miX >> pd.SubsamplingX, pd.SubsamplingX, pd.SubsamplingY, xd.Bd, xd.IsHbd, false, sf,
                pd.Pre0, nb.InterpFilters);
            p.ConvParams = AomConvParams.Get(0, j, xd.Bd);
            BuildOneInterPredictor(dsts[j].Buf, dsts[j].Buf16, dsts[j].Offset, dsts[j].Stride, nb.Mv0, p);
        }
    }

    /// <summary>av1_build_prediction_by_above_preds.</summary>
    public static void BuildPredictionByAbovePreds(AomCommon cm, AomMacroblockD xd, AomBuf2d[] tmp)
    {
        if (!xd.UpAvailable) return;
        int bsize = xd.Mi0.Bsize;
        ForeachOverlappableNbAbove(cm, xd, AomInter.MaxNeighborObmc[MiSizeWideLog2[bsize]],
            (r, c, op, dir, nb) => BuildObmcPrediction(cm, xd, r, c, op, dir, nb, tmp));
    }

    /// <summary>av1_build_prediction_by_left_preds.</summary>
    public static void BuildPredictionByLeftPreds(AomCommon cm, AomMacroblockD xd, AomBuf2d[] tmp)
    {
        if (!xd.LeftAvailable) return;
        int bsize = xd.Mi0.Bsize;
        ForeachOverlappableNbLeft(cm, xd, AomInter.MaxNeighborObmc[MiSizeHighLog2[bsize]],
            (r, c, op, dir, nb) => BuildObmcPrediction(cm, xd, r, c, op, dir, nb, tmp));
    }

    /// <summary>av1_build_obmc_inter_predictors_sb.</summary>
    public static void BuildObmcInterPredictorsSb(AomCommon cm, AomMacroblockD xd, Action resetDstPlanes)
    {
        var (b1, b2) = SetupObmcDstBufs(xd);
        BuildPredictionByAbovePreds(cm, xd, b1);
        BuildPredictionByLeftPreds(cm, xd, b2);
        resetDstPlanes();   // av1_setup_dst_planes(xd->plane, bsize, &cm->cur_frame->buf, mi_row, mi_col, 0, num_planes)
        BuildObmcInterPrediction(cm, xd, b1, b2);
    }
}
