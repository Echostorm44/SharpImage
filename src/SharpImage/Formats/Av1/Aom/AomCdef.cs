using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1's CDEF for the 8-bit encoder: av1/common/cdef_block.c (cdef_find_dir, the block filter,
// av1_cdef_filter_fb), av1/common/cdef.c (av1_cdef_compute_sb_list, av1_cdef_frame: the frame filtered from its
// pre-CDEF copy, CDEF_VERY_LARGE outside the frame) and av1/encoder/pickcdef.c (av1_cdef_search: the per 64x64 SSE of
// every candidate strength against the source, the greedy joint luma / chroma strength search, the signalling-bit RD
// choice, CDEF_ADAPTIVE's strength reduction / zeroing, av1_pick_cdef_from_qp). CDEF is normative, so libaom's SIMD
// kernels give exactly the C results ported here.
internal static partial class AomCdef
{
    private const int VBorder = 2, HBorder = 8;
    internal const int BStride = 144;
    internal const int InOff = VBorder * BStride + HBorder;   // the filter block's origin in the 16-bit input buffer   // ALIGN_POWER_OF_TWO(128 + 2 * CDEF_HBORDER, 3)
    internal const int VeryLarge = 0x4000;
    internal const int InbufSize = BStride * (128 + 2 * VBorder);
    private const int NBlocks = 16;     // CDEF_NBLOCKS
    internal const int InbufSizeForScratch = InbufSize, NBlocksForScratch = NBlocks;
    private const int SecStrengths = 4, PriStrengths = 16, TotalStrengths = PriStrengths * SecStrengths;
    private const int MiSize64 = 16, MiSize128 = 32;

    // cdef_directions with two padding entries on each side (index dir + 2)
    private static readonly int[] DirectionsPadded =
    {
        1 * BStride + 0, 2 * BStride + 0,
        1 * BStride + 0, 2 * BStride - 1,
        -1 * BStride + 1, -2 * BStride + 2,
        0 * BStride + 1, -1 * BStride + 2,
        0 * BStride + 1, 0 * BStride + 2,
        0 * BStride + 1, 1 * BStride + 2,
        1 * BStride + 1, 2 * BStride + 2,
        1 * BStride + 0, 2 * BStride + 1,
        1 * BStride + 0, 2 * BStride + 0,
        1 * BStride + 0, 2 * BStride - 1,
        -1 * BStride + 1, -2 * BStride + 2,
        0 * BStride + 1, -1 * BStride + 2,
    };
    private static int Direction(int dir, int k) => DirectionsPadded[(dir + 2) * 2 + k];
    private static readonly int[] PriTaps = { 4, 2, 3, 3 };
    private static readonly int[] SecTaps = { 2, 1 };
    private static readonly int[] DivTable = { 0, 840, 420, 280, 210, 168, 140, 120, 105 };

    // pickcdef.h reduced strength sets
    private static readonly int[] PriconvLvl1 = { 0, 1, 2, 3, 5, 7, 10, 13 };
    private static readonly int[] PriconvLvl2 = { 0, 2, 4, 8, 14 };
    private static readonly int[] PriconvLvl4 = { 0, 11 };
    private static readonly int[] PriconvLvl5 = { 0, 5 };
    private static readonly int[] SecconvLvl3 = { 0, 2 };
    private static readonly int[] SecconvLvl5 = { 0 };
    private static readonly int[] NbCdefStrengths = { 64, 8 * 4, 5 * 4, 5 * 2, 2 * 2, 2 * 1, 64 };

    /// <summary>cdef_find_dir_c: the 8x8 block's dominant direction and its directional variance.</summary>
    internal static int FindDir(ushort[] img, int off, int stride, out int var, int coeffShift)
    {
        Span<int> cost = stackalloc int[8];
        Span<int> partial = stackalloc int[8 * 15];
        cost.Clear(); partial.Clear();
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
            {
                int x = (img[off + i * stride + j] >> coeffShift) - 128;
                partial[0 * 15 + i + j] += x;
                partial[1 * 15 + i + j / 2] += x;
                partial[2 * 15 + i] += x;
                partial[3 * 15 + 3 + i - j / 2] += x;
                partial[4 * 15 + 7 + i - j] += x;
                partial[5 * 15 + 3 - i / 2 + j] += x;
                partial[6 * 15 + j] += x;
                partial[7 * 15 + i / 2 + j] += x;
            }
        for (int i = 0; i < 8; i++)
        {
            cost[2] += partial[2 * 15 + i] * partial[2 * 15 + i];
            cost[6] += partial[6 * 15 + i] * partial[6 * 15 + i];
        }
        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];
        for (int i = 0; i < 7; i++)
        {
            cost[0] += (partial[0 * 15 + i] * partial[0 * 15 + i] + partial[0 * 15 + 14 - i] * partial[0 * 15 + 14 - i]) * DivTable[i + 1];
            cost[4] += (partial[4 * 15 + i] * partial[4 * 15 + i] + partial[4 * 15 + 14 - i] * partial[4 * 15 + 14 - i]) * DivTable[i + 1];
        }
        cost[0] += partial[0 * 15 + 7] * partial[0 * 15 + 7] * DivTable[8];
        cost[4] += partial[4 * 15 + 7] * partial[4 * 15 + 7] * DivTable[8];
        for (int i = 1; i < 8; i += 2)
        {
            for (int j = 0; j < 4 + 1; j++) cost[i] += partial[i * 15 + 3 + j] * partial[i * 15 + 3 + j];
            cost[i] *= DivTable[8];
            for (int j = 0; j < 4 - 1; j++)
                cost[i] += (partial[i * 15 + j] * partial[i * 15 + j] + partial[i * 15 + 10 - j] * partial[i * 15 + 10 - j]) * DivTable[2 * j + 2];
        }
        int bestCost = 0, bestDir = 0;
        for (int i = 0; i < 8; i++)
            if (cost[i] > bestCost) { bestCost = cost[i]; bestDir = i; }
        var = (bestCost - cost[(bestDir + 4) & 7]) >> 10;
        return bestDir;
    }

    private static int Constrain(int diff, int threshold, int damping)
    {
        if (threshold == 0) return 0;
        int shift = Math.Max(0, damping - (31 - BitOperations.LeadingZeroCount((uint)threshold)));
        int ad = Math.Abs(diff);
        int v = Math.Min(ad, Math.Max(0, threshold - (ad >> shift)));
        return diff < 0 ? -v : v;
    }

    /// <summary>cdef_filter_block_internal (8-bit output).</summary>
    internal static void FilterBlock(byte[] dst, int dstOff, int dstride, ushort[] inb, int inOff, int priStrength, int secStrength, int dir,
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
                dst[dstOff + i * dstride + j] = (byte)y;
            }
    }

    /// <summary>adjust_strength.</summary>
    private static int AdjustStrength(int strength, int var)
    {
        int i = (var >> 6) != 0 ? Math.Min(BitOperations.Log2((uint)(var >> 6)), 12) : 0;
        return var != 0 ? (strength * (4 + i) + 8) >> 4 : 0;
    }

    /// <summary>aom_cdef_find_dir: the luma directions / variances of the listed blocks (pairs through cdef_find_dir_dual,
    /// then the odd one).</summary>
    internal static unsafe void FindDirs(ushort[] inb, int inOff, (byte by, byte bx)[] dlist, int cdefCount, int[] dir, int[] var, int coeffShift)
    {
        if (!Avx2.IsSupported)
        {
            for (int bi = 0; bi < cdefCount; bi++)
            {
                int by = dlist[bi].by, bx = dlist[bi].bx;
                dir[by * NBlocks + bx] = FindDir(inb, inOff + 8 * by * BStride + 8 * bx, BStride, out var[by * NBlocks + bx], coeffShift);
            }
            return;
        }
        fixed (ushort* ib = inb)
        {
            ushort* inp = ib + inOff;
            int bi;
            for (bi = 0; bi < cdefCount - 1; bi += 2)
            {
                int by = dlist[bi].by, bx = dlist[bi].bx, by2 = dlist[bi + 1].by, bx2 = dlist[bi + 1].bx;
                FindDirDualAvx2(inp + 8 * by * BStride + 8 * bx, inp + 8 * by2 * BStride + 8 * bx2, BStride, out var[by * NBlocks + bx],
                    out var[by2 * NBlocks + bx2], coeffShift, out dir[by * NBlocks + bx], out dir[by2 * NBlocks + bx2]);
            }
            if ((cdefCount & 1) != 0)
            {
                // cdef_find_dir_avx2 is the same per-lane arithmetic as the dual kernel's first lane
                int by = dlist[bi].by, bx = dlist[bi].bx;
                ushort* p = inp + 8 * by * BStride + 8 * bx;
                FindDirDualAvx2(p, p, BStride, out var[by * NBlocks + bx], out _, coeffShift, out dir[by * NBlocks + bx], out _);
            }
        }
    }

    /// <summary>av1_cdef_filter_fb with 8-bit output (dirinit: the search caches the luma directions per block).</summary>
    internal static unsafe void FilterFb(byte[] dst8, int dstOff, int dstride, ushort[] inb, int inOff, int xdec, int ydec, int[] dir, ref bool dirinit,
        bool useDirinit, int[] var, int pli, (byte by, byte bx)[] dlist, int cdefCount, int level, int secStrength, int damping, int coeffShift)
    {
        int priStrength = level << coeffShift;
        secStrength <<= coeffShift;
        damping += coeffShift - (pli != 0 ? 1 : 0);
        int bwLog2 = 3 - xdec, bhLog2 = 3 - ydec;
        if (useDirinit && priStrength == 0 && secStrength == 0)
            throw new InvalidOperationException("the 8-bit search never filters with zero strengths");
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
        if (Avx2.IsSupported)
        {
            fixed (byte* d0 = dst8)
            fixed (ushort* i0 = inb)
            {
                byte* d = d0 + dstOff;
                short* inp = (short*)i0 + inOff;
                for (int bi = 0; bi < cdefCount; bi++)
                {
                    int by = dlist[bi].by, bx = dlist[bi].bx;
                    int t = pli != 0 ? priStrength : AdjustStrength(priStrength, var[by * NBlocks + bx]);
                    int strengthIndex = (secStrength == 0 ? 1 : 0) | (t == 0 ? 2 : 0);
                    FilterBlock8Avx2(strengthIndex, d + (by << bhLog2) * dstride + (bx << bwLog2), dstride,
                        inp + (by * BStride << bhLog2) + (bx << bwLog2), t, secStrength, priStrength != 0 ? dir[by * NBlocks + bx] : 0,
                        damping, damping, coeffShift, bw, bh);
                }
            }
            return;
        }
        for (int bi = 0; bi < cdefCount; bi++)
        {
            int by = dlist[bi].by, bx = dlist[bi].bx;
            int t = pli != 0 ? priStrength : AdjustStrength(priStrength, var[by * NBlocks + bx]);
            bool enablePrimary = t != 0, enableSecondary = secStrength != 0;
            FilterBlockV(dst8, dstOff + (by << bhLog2) * dstride + (bx << bwLog2), dstride, inb, inOff + (by * BStride << bhLog2) + (bx << bwLog2),
                t, secStrength, priStrength != 0 ? dir[by * NBlocks + bx] : 0, damping, damping, coeffShift, bw, bh, enablePrimary, enableSecondary);
        }
    }

    private static bool Is8x8BlockSkip(AomCommon cm, int miRow, int miCol)
    {
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                if (cm.MiGridBase[(miRow + r) * cm.MiStride + miCol + c]!.SkipTxfm == 0) return false;
        return true;
    }

    /// <summary>av1_cdef_compute_sb_list: the non-skip 8x8 blocks of the filter block.</summary>
    private static int ComputeSbList(AomCommon cm, int miRow, int miCol, (byte by, byte bx)[] dlist, int bs)
    {
        int maxc = cm.MiCols - miCol, maxr = cm.MiRows - miRow;
        maxc = Math.Min(maxc, bs == BLOCK_128X128 || bs == BLOCK_128X64 ? MiSize128 : MiSize64);
        maxr = Math.Min(maxr, bs == BLOCK_128X128 || bs == BLOCK_64X128 ? MiSize128 : MiSize64);
        int count = 0;
        for (int r = 0; r < maxr; r += 2)
            for (int c = 0; c < maxc; c += 2)
                if (!Is8x8BlockSkip(cm, miRow + r, miCol + c)) dlist[count++] = ((byte)(r >> 1), (byte)(c >> 1));
        return count;
    }

    /// <summary>sb_all_skip.</summary>
    private static bool SbAllSkip(AomCommon cm, int miRow, int miCol)
    {
        int maxr = Math.Min(cm.MiRows - miRow, MiSize64), maxc = Math.Min(cm.MiCols - miCol, MiSize64);
        for (int r = 0; r < maxr; ++r)
            for (int c = 0; c < maxc; ++c)
                if (cm.MiGridBase[(miRow + r) * cm.MiStride + miCol + c]!.SkipTxfm == 0) return false;
        return true;
    }

    /// <summary>cdef_sb_skip.</summary>
    private static bool CdefSbSkip(AomCommon cm, int fbr, int fbc)
    {
        var mbmi = cm.MiGridBase[MiSize64 * fbr * cm.MiStride + MiSize64 * fbc]!;
        if (SbAllSkip(cm, fbr * MiSize64, fbc * MiSize64)) return true;
        if (((fbc & 1) != 0 && (mbmi.Bsize == BLOCK_128X128 || mbmi.Bsize == BLOCK_128X64)) ||
            ((fbr & 1) != 0 && (mbmi.Bsize == BLOCK_128X128 || mbmi.Bsize == BLOCK_64X128)))
            return true;
        return false;
    }

    private static void FillRect(ushort[] dst, int off, int dstride, int v, int h, ushort x)
    {
        for (int i = 0; i < v; i++) dst.AsSpan(off + i * dstride, h).Fill(x);
    }

    /// <summary>aom_sse (the vectorized 8-bit kernel; integer sums, so exactly libaom's).</summary>
    private static long Sse(byte[] a, int aOff, int aStride, byte[] b, int bOff, int bStride, int w, int h) =>
        AomEncodeMb.Sse(a, aOff, aStride, b, bOff, bStride, w, h);

    /// <summary>get_error_calc_width_in_filt_units: how many horizontally adjacent listed blocks one SSE call covers.</summary>
    private static int ErrorCalcWidthInFiltUnits((byte by, byte bx)[] dlist, int cdefCount, int bi, int ssX, int ssY)
    {
        if (ssX != ssY) return 1;
        if (bi + 3 < cdefCount && dlist[bi].by == dlist[bi + 3].by && dlist[bi].bx + 3 == dlist[bi + 3].bx) return 4;
        if (bi + 1 < cdefCount && dlist[bi].by == dlist[bi + 1].by && dlist[bi].bx + 1 == dlist[bi + 1].bx) return 2;
        return 1;
    }

    /// <summary>get_cdef_filter_strengths.</summary>
    private static void GetFilterStrengths(int pickMethod, out int pri, out int sec, int strengthIdx)
    {
        int totSec = pickMethod == CDEF_FAST_SEARCH_LVL5 ? 1 : pickMethod >= CDEF_FAST_SEARCH_LVL3 ? 2 : SecStrengths;
        int priIdx = strengthIdx / totSec, secIdx = strengthIdx % totSec;
        pri = priIdx; sec = secIdx;
        switch (pickMethod)
        {
            case CDEF_FULL_SEARCH: return;
            case CDEF_FAST_SEARCH_LVL1: pri = PriconvLvl1[priIdx]; break;
            case CDEF_FAST_SEARCH_LVL2: pri = PriconvLvl2[priIdx]; break;
            case CDEF_FAST_SEARCH_LVL3: pri = PriconvLvl2[priIdx]; sec = SecconvLvl3[secIdx]; break;
            case CDEF_FAST_SEARCH_LVL4: pri = PriconvLvl4[priIdx]; sec = SecconvLvl3[secIdx]; break;
            case CDEF_FAST_SEARCH_LVL5: pri = PriconvLvl5[priIdx]; sec = SecconvLvl5[secIdx]; break;
            default: throw new ArgumentOutOfRangeException(nameof(pickMethod));
        }
    }

    /// <summary>av1_cdef_search: the frame's CDEF parameters and every 64x64 filter block's strength index.</summary>
    /// <param name="cur">the deblocked reconstruction (cm->cur_frame->buf)</param>
    /// <param name="src">the source (cpi->source)</param>
    internal static AomCdefInfo Search(AomComp cpi, AomYv12 cur, AomYv12 src, int rdmult)
    {
        var cm = cpi.Cm;
        var ci = new AomCdefInfo();
        bool applyAdaptiveCdef = cpi.CdefControl == 3 && cpi.Mode != REALTIME;   // CDEF_ADAPTIVE with AOM_Q (not CBR)
        int cqLevel = cm.BaseQindex;                      // rc_cfg.cq_level (the qindex of AOME_SET_CQ_LEVEL)
        if (applyAdaptiveCdef && cqLevel <= 32)
        {
            // cdef_damping is left as initialised (calloc: 0)
            ci.NbCdefStrengths = 1; ci.CdefBits = 0; ci.CdefStrengths[0] = 0; ci.CdefUvStrengths[0] = 0;
            return ci;
        }
        int pickMethod = cpi.Sf.lpf_sf.cdef_pick_method;
        if (pickMethod == CDEF_PICK_FROM_Q)
        {
            // use_screen_content_model needs tune content AOM_CONTENT_SCREEN (libavif leaves the default)
            PickCdefFromQp(cpi, ci, cpi.Sf.rt_sf.skip_cdef_sb != 0, false, applyAdaptiveCdef);
            return ci;
        }
        int damping = 3 + (cm.BaseQindex >> 6);
        bool fast = pickMethod >= CDEF_FAST_SEARCH_LVL1 && pickMethod <= CDEF_FAST_SEARCH_LVL5;
        int adaptiveCdefMode = cpi.Sf.lpf_sf.adaptive_cdef_mode;
        int numPlanes = cm.NumPlanes;
        int nvfb = (cm.MiRows + MiSize64 - 1) / MiSize64, nhfb = (cm.MiCols + MiSize64 - 1) / MiSize64;
        int totalStrengths = NbCdefStrengths[pickMethod];

        // cdef_mse_calc_frame
        var mse0 = new ulong[nvfb * nhfb][];
        var mse1 = new ulong[nvfb * nhfb][];
        var sbIndex = new int[nvfb * nhfb];
        // (av1_cdef_mse_calc_frame_mt with multi-threading: the same blocks in the same sb_count order, any thread)
        var jobs = new List<(int fbr, int fbc)>();
        for (int fbr = 0; fbr < nvfb; ++fbr)
            for (int fbc = 0; fbc < nhfb; ++fbc)
                if (!CdefSbSkip(cm, fbr, fbc)) jobs.Add((fbr, fbc));
        int sbCount = jobs.Count;
        int inOff = VBorder * BStride + HBorder;
        void CalcBlock(int k, CdefSearchScratch sc)
        {
                var (fbr, fbc) = jobs[k];
                // av1_cdef_mse_calc_block
                mse0[k] = new ulong[TotalStrengths];
                mse1[k] = new ulong[TotalStrengths];
                // av1_cdef_mse_calc_block
                var inbuf = sc.Inbuf; var dlist = sc.Dlist; var dir = sc.Dir; var var = sc.Var; var tmpDst8 = sc.TmpDst8;
                Array.Clear(dir); Array.Clear(var);
                int nhb = Math.Min(MiSize64, cm.MiCols - MiSize64 * fbc), nvb = Math.Min(MiSize64, cm.MiRows - MiSize64 * fbr);
                int hbStep = 1, vbStep = 1;
                var mbmi = cm.MiGridBase[MiSize64 * fbr * cm.MiStride + MiSize64 * fbc]!;
                int bs;
                if (mbmi.Bsize == BLOCK_128X128 || mbmi.Bsize == BLOCK_128X64 || mbmi.Bsize == BLOCK_64X128)
                {
                    bs = mbmi.Bsize;
                    if (bs == BLOCK_128X128 || bs == BLOCK_128X64) { nhb = Math.Min(MiSize128, cm.MiCols - MiSize64 * fbc); hbStep = 2; }
                    if (bs == BLOCK_128X128 || bs == BLOCK_64X128) { nvb = Math.Min(MiSize128, cm.MiRows - MiSize64 * fbr); vbStep = 2; }
                }
                else bs = BLOCK_64X64;
                int cdefCount = ComputeSbList(cm, fbr * MiSize64, fbc * MiSize64, dlist, bs);
                bool left = fbc == 0, right = fbc + hbStep == nhfb, top = fbr == 0, bottom = fbr + vbStep == nvfb;
                int yoff = top ? 0 : VBorder, xoff = left ? 0 : HBorder;
                bool dirinit = false;
                for (int pli = 0; pli < numPlanes; pli++)
                {
                    if (adaptiveCdefMode > 0 && pli > 0)
                    {
                        mse1[k][0] = 0;
                        for (int gi = 1; gi < totalStrengths; gi++) mse1[k][gi] = 1;
                        break;
                    }
                    int xdec = pli == 0 ? 0 : cm.SsX, ydec = pli == 0 ? 0 : cm.SsY;
                    int miWideL2 = 2 - xdec, miHighL2 = 2 - ydec;
                    int hfiltSize = nhb << miWideL2, vfiltSize = nvb << miHighL2;
                    int ysize = vfiltSize + (bottom ? 0 : VBorder) + yoff, xsize = hfiltSize + (right ? 0 : HBorder) + xoff;
                    int row = fbr * MiSize64 << miHighL2, col = fbc * MiSize64 << miWideL2;
                    var dp = cur.Planes[pli];
                    if (dp.Buf16 != null)
                    {
                        // av1_cdef_copy_sb8_16_highbd
                        for (int r = 0; r < ysize; r++)
                            Array.Copy(dp.Buf16, dp.At(col - xoff, row - yoff + r), inbuf, inOff - yoff * BStride - xoff + r * BStride, xsize);
                    }
                    else
                    // av1_cdef_copy_sb8_16_lowbd
                    unsafe
                    {
                        fixed (ushort* ib = inbuf)
                        fixed (byte* sb = dp.Buf)
                            CopyRect8To16(ib + inOff - yoff * BStride - xoff, BStride, sb + dp.At(col - xoff, row - yoff), dp.Stride, xsize, ysize);
                    }
                    FillBordersOnFrameBoundary(inbuf, hfiltSize, vfiltSize, left, right, top, bottom);
                    var rp = src.Planes[pli];
                    int planeBsize = AomEncodeMb.PlaneBlockSize(bs, xdec, ydec);
                    int bwLog2 = 3 - xdec, bhLog2 = 3 - ydec;
                    int pw = BlockSizeWide[planeBsize], ph = BlockSizeHigh[planeBsize];
                    int totBlkCount = (pw * ph) >> (bwLog2 + bhLog2);
                    for (int gi = 0; gi < totalStrengths; gi++)
                    {
                        GetFilterStrengths(pickMethod, out int pri, out int sec, gi);
                        if (dp.Buf16 != null)
                        {
                            ulong hsse = FiltErrorHbd(rp, row, col, sc.TmpDst16, inbuf, inOff, xdec, ydec, dir, ref dirinit, var, pli, dlist, cdefCount,
                                pri, sec, damping, cm.BitDepth - 8);
                            if (pli < 2) (pli == 0 ? mse0 : mse1)[k][gi] = hsse;
                            else mse1[k][gi] += hsse;
                            continue;
                        }
                        // get_filt_error (8-bit)
                        ulong currSse = 0;
                        bool zero = pri == 0 && sec == 0;
                        if (!zero)
                            FilterFb(tmpDst8, 0, 128, inbuf, inOff, xdec, ydec, dir, ref dirinit, true, var, pli, dlist, cdefCount,
                                pri, sec + (sec == 3 ? 1 : 0), damping, 0);
                        if (cdefCount == totBlkCount)
                        {
                            currSse = zero ? (ulong)Sse(rp.Buf, rp.At(col, row), rp.Stride, dp.Buf, dp.At(col, row), dp.Stride, pw, ph)
                                : (ulong)Sse(rp.Buf, rp.At(col, row), rp.Stride, tmpDst8, 0, 128, pw, ph);
                        }
                        else
                        {
                            int units;
                            for (int bi = 0; bi < cdefCount; bi += units)
                            {
                                int byPos = dlist[bi].by << bhLog2, bxPos = dlist[bi].bx << bwLog2;
                                units = ErrorCalcWidthInFiltUnits(dlist, cdefCount, bi, xdec, ydec);
                                currSse += zero
                                    ? (ulong)Sse(rp.Buf, rp.At(col + bxPos, row + byPos), rp.Stride, dp.Buf, dp.At(col + bxPos, row + byPos), dp.Stride, units << bwLog2, 1 << bhLog2)
                                    : (ulong)Sse(rp.Buf, rp.At(col + bxPos, row + byPos), rp.Stride, tmpDst8, byPos * 128 + bxPos, 128, units << bwLog2, 1 << bhLog2);
                            }
                        }
                        if (pli < 2) (pli == 0 ? mse0 : mse1)[k][gi] = currSse;
                        else mse1[k][gi] += currSse;
                    }
                }
                sbIndex[k] = MiSize64 * fbr * cm.MiStride + MiSize64 * fbc;
        }
        if (cpi.NumWorkers > 1 && sbCount > 1)
            System.Threading.Tasks.Parallel.For(0, sbCount, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = cpi.NumWorkers },
                () => new CdefSearchScratch(), (k, _, sc) => { CalcBlock(k, sc); return sc; }, _ => { });
        else
        {
            var sc = new CdefSearchScratch();
            for (int k = 0; k < sbCount; k++) CalcBlock(k, sc);
        }

        // search for the number of signalling bits
        int nbStrengthBits = 0;
        ulong bestRd = ulong.MaxValue;
        int jointStrengths = numPlanes > 1 ? totalStrengths * totalStrengths : totalStrengths;
        int maxSignalingBits = jointStrengths == 1 ? 0 : BitOperations.Log2((uint)(jointStrengths - 1)) + 1;
        bool shouldReduceCdefStrengths = applyAdaptiveCdef && cqLevel <= 220;
        bool shouldZeroCdefStrengths = shouldReduceCdefStrengths && cpi.Sf.lpf_sf.zero_low_cdef_strengths;
        int minSignalingBits = shouldZeroCdefStrengths && maxSignalingBits > 0 ? 1 : 0;
        for (int i = minSignalingBits; i <= 3; i++)
        {
            if (i > maxSignalingBits) break;
            var bestLev0 = new int[8];
            var bestLev1 = new int[8];
            int nbStrengths = 1 << i;
            ulong totMse = numPlanes > 1
                ? JointStrengthSearchDual(bestLev0, bestLev1, nbStrengths, mse0, mse1, sbCount, totalStrengths)
                : JointStrengthSearch(bestLev0, nbStrengths, mse0, sbCount, totalStrengths, fast);
            int totalBits = sbCount * i + nbStrengths * 6 * (numPlanes > 1 ? 2 : 1);
            int rateCost = totalBits << AomCost.ProbCostShift;   // av1_cost_literal
            ulong dist = totMse * 16;
            ulong rd = (ulong)AomRd.RdCost(rdmult, rateCost, 0) + (dist << AomRd.RdDivBits);   // RDCOST in uint64
            if (rd < bestRd)
            {
                bestRd = rd;
                nbStrengthBits = i;
                Array.Copy(bestLev0, ci.CdefStrengths, nbStrengths);
                if (numPlanes > 1) Array.Copy(bestLev1, ci.CdefUvStrengths, nbStrengths);
            }
        }
        ci.CdefBits = nbStrengthBits;
        ci.NbCdefStrengths = 1 << nbStrengthBits;
        for (int i = 0; i < sbCount; i++)
        {
            ulong bestMse = ulong.MaxValue;
            int bestGi = 0;
            for (int gi = 0; gi < ci.NbCdefStrengths; gi++)
            {
                ulong curr = mse0[i][ci.CdefStrengths[gi]];
                if (numPlanes > 1) curr += mse1[i][ci.CdefUvStrengths[gi]];
                if (curr < bestMse) { bestGi = gi; bestMse = curr; }
            }
            cm.MiGridBase[sbIndex[i]]!.CdefStrength = (sbyte)bestGi;
        }
        // (the pyramid_level > 1 luma MSE gain check needs adaptive_cdef_mode, which the all-intra speeds leave at 0)
        if (fast)
            for (int j = 0; j < ci.NbCdefStrengths; j++)
            {
                GetFilterStrengths(pickMethod, out int pri, out int sec, ci.CdefStrengths[j]);
                ci.CdefStrengths[j] = pri * SecStrengths + sec;
                if (numPlanes > 1)
                {
                    GetFilterStrengths(pickMethod, out pri, out sec, ci.CdefUvStrengths[j]);
                    ci.CdefUvStrengths[j] = pri * SecStrengths + sec;
                }
            }
        if (shouldReduceCdefStrengths)
            for (int j = 0; j < ci.NbCdefStrengths; j++)
            {
                int luma = ci.CdefStrengths[j];
                int newPriLuma = (luma / SecStrengths) >> 1, newSecLuma = (luma % SecStrengths) >> 1;
                ci.CdefStrengths[j] = newPriLuma * SecStrengths + newSecLuma;
                int newPriChroma = 0, newSecChroma = 0;
                if (numPlanes > 1)
                {
                    int chroma = ci.CdefUvStrengths[j];
                    newPriChroma = (chroma / SecStrengths) >> 1;
                    newSecChroma = (chroma % SecStrengths) >> 1;
                    ci.CdefUvStrengths[j] = newPriChroma * SecStrengths + newSecChroma;
                }
                if (shouldZeroCdefStrengths)
                {
                    bool lowLuma = newPriLuma <= 4 && newSecLuma <= 1;
                    if (lowLuma) ci.CdefStrengths[j] = 0;
                    if (numPlanes > 1)
                    {
                        bool lowChroma = newPriChroma <= 4 && newSecChroma <= 1;
                        if (lowLuma || lowChroma) ci.CdefUvStrengths[j] = 0;
                    }
                }
            }
        ci.CdefDamping = damping;
        return ci;
    }

    /// <summary>fill_borders_for_fbs_on_frame_boundary.</summary>
    private static void FillBordersOnFrameBoundary(ushort[] inbuf, int hfiltSize, int vfiltSize, bool left, bool right, bool top, bool bottom)
    {
        if (!left && !right && !top && !bottom) return;
        if (bottom) FillRect(inbuf, (vfiltSize + VBorder) * BStride + HBorder, BStride, VBorder, hfiltSize, VeryLarge);
        if (bottom || left) FillRect(inbuf, (vfiltSize + VBorder) * BStride, BStride, VBorder, HBorder, VeryLarge);
        if (bottom || right) FillRect(inbuf, (vfiltSize + VBorder) * BStride + hfiltSize + HBorder, BStride, VBorder, HBorder, VeryLarge);
        if (top) FillRect(inbuf, HBorder, BStride, VBorder, hfiltSize, VeryLarge);
        if (top || left) FillRect(inbuf, 0, BStride, VBorder, HBorder, VeryLarge);
        if (top || right) FillRect(inbuf, hfiltSize + HBorder, BStride, VBorder, HBorder, VeryLarge);
        if (left) FillRect(inbuf, VBorder * BStride, BStride, vfiltSize, HBorder, VeryLarge);
        if (right) FillRect(inbuf, VBorder * BStride + hfiltSize + HBorder, BStride, vfiltSize, HBorder, VeryLarge);
    }

    /// <summary>search_one.</summary>
    private static ulong SearchOne(int[] lev, int nbStrengths, ulong[][] mse, int sbCount, int totalStrengths)
    {
        var totMse = new ulong[TotalStrengths];
        ulong bestTotMse = 1UL << 63;
        int bestId = 0;
        for (int i = 0; i < sbCount; i++)
        {
            ulong bestMse = 1UL << 63;
            for (int gi = 0; gi < nbStrengths; gi++)
                if (mse[i][lev[gi]] < bestMse) bestMse = mse[i][lev[gi]];
            for (int j = 0; j < totalStrengths; j++)
            {
                ulong best = bestMse;
                if (mse[i][j] < best) best = mse[i][j];
                totMse[j] += best;
            }
        }
        for (int j = 0; j < totalStrengths; j++)
            if (totMse[j] < bestTotMse) { bestTotMse = totMse[j]; bestId = j; }
        lev[nbStrengths] = bestId;
        return bestTotMse;
    }

    /// <summary>search_one_dual.</summary>
    private static ulong SearchOneDual(int[] lev0, int[] lev1, int nbStrengths, ulong[][] mse0, ulong[][] mse1, int sbCount, int totalStrengths)
    {
        var totMse = new ulong[totalStrengths * totalStrengths];
        ulong bestTotMse = 1UL << 63;
        int bestId0 = 0, bestId1 = 0;
        for (int i = 0; i < sbCount; i++)
        {
            ulong bestMse = 1UL << 63;
            var m0 = mse0[i]; var m1 = mse1[i];
            for (int gi = 0; gi < nbStrengths; gi++)
            {
                ulong curr = m0[lev0[gi]] + m1[lev1[gi]];
                if (curr < bestMse) bestMse = curr;
            }
            for (int j = 0; j < totalStrengths; j++)
                for (int k = 0; k < totalStrengths; k++)
                {
                    ulong best = bestMse;
                    ulong curr = m0[j] + m1[k];
                    if (curr < best) best = curr;
                    totMse[j * totalStrengths + k] += best;
                }
        }
        for (int j = 0; j < totalStrengths; j++)
            for (int k = 0; k < totalStrengths; k++)
                if (totMse[j * totalStrengths + k] < bestTotMse)
                {
                    bestTotMse = totMse[j * totalStrengths + k];
                    bestId0 = j; bestId1 = k;
                }
        lev0[nbStrengths] = bestId0;
        lev1[nbStrengths] = bestId1;
        return bestTotMse;
    }

    /// <summary>joint_strength_search.</summary>
    private static ulong JointStrengthSearch(int[] bestLev, int nbStrengths, ulong[][] mse, int sbCount, int totalStrengths, bool fast)
    {
        ulong bestTotMse = 1UL << 63;
        for (int i = 0; i < nbStrengths; i++) bestTotMse = SearchOne(bestLev, i, mse, sbCount, totalStrengths);
        if (!fast)
            for (int i = 0; i < 4 * nbStrengths; i++)
            {
                for (int j = 0; j < nbStrengths - 1; j++) bestLev[j] = bestLev[j + 1];
                bestTotMse = SearchOne(bestLev, nbStrengths - 1, mse, sbCount, totalStrengths);
            }
        return bestTotMse;
    }

    /// <summary>joint_strength_search_dual.</summary>
    private static ulong JointStrengthSearchDual(int[] bestLev0, int[] bestLev1, int nbStrengths, ulong[][] mse0, ulong[][] mse1, int sbCount,
        int totalStrengths)
    {
        ulong bestTotMse = 1UL << 63;
        for (int i = 0; i < nbStrengths; i++) bestTotMse = SearchOneDual(bestLev0, bestLev1, i, mse0, mse1, sbCount, totalStrengths);
        for (int i = 0; i < 4 * nbStrengths; i++)
        {
            for (int j = 0; j < nbStrengths - 1; j++) { bestLev0[j] = bestLev0[j + 1]; bestLev1[j] = bestLev1[j + 1]; }
            bestTotMse = SearchOneDual(bestLev0, bestLev1, nbStrengths - 1, mse0, mse1, sbCount, totalStrengths);
        }
        return bestTotMse;
    }

    /// <summary>av1_pick_cdef_from_qp.</summary>
    private static void PickCdefFromQp(AomComp cpi, AomCdefInfo ci, bool skipCdef, bool isScreenContent, bool avoidUvCdef)
    {
        var cm = cpi.Cm;
        int bdq = cm.BitDepth;
        int q = Av1Tables.DequantTable[bdq == 8 ? 0 : bdq == 10 ? 1 : 2, cm.BaseQindex, 1] >> (bdq - 8);
        if (skipCdef) { ci.CdefBits = 1; ci.NbCdefStrengths = 2; }
        else { ci.CdefBits = 0; ci.NbCdefStrengths = 1; }
        ci.CdefDamping = 3 + (cm.BaseQindex >> 6);
        int yF1, yF2, uvF1, uvF2;
        if (isScreenContent)
        {
            yF1 = Math.Clamp((int)(5.88217781e-06 * q * q + 6.10391455e-03 * q + 9.95043102e-02), 0, 15);
            yF2 = Math.Clamp((int)(-7.79934857e-06 * q * q + 6.58957830e-03 * q + 8.81045025e-01), 0, 3);
            uvF1 = Math.Clamp((int)(-6.79500136e-06 * q * q + 1.02695586e-02 * q + 1.36126802e-01), 0, 15);
            uvF2 = Math.Clamp((int)(-9.99613695e-08 * q * q - 1.79361339e-05 * q + 1.17022324e+0), 0, 3);
        }
        else if (!cm.FrameIsIntraOnly)
        {
            yF1 = Math.Clamp((int)MathF.Round(q * q * -0.0000023593946f + q * 0.0068615186f + 0.02709886f, MidpointRounding.AwayFromZero), 0, 15);
            yF2 = Math.Clamp((int)MathF.Round(q * q * -0.00000057629734f + q * 0.0013993345f + 0.03831067f, MidpointRounding.AwayFromZero), 0, 3);
            uvF1 = Math.Clamp((int)MathF.Round(q * q * -0.0000007095069f + q * 0.0034628846f + 0.00887099f, MidpointRounding.AwayFromZero), 0, 15);
            uvF2 = Math.Clamp((int)MathF.Round(q * q * 0.00000023874085f + q * 0.00028223585f + 0.05576307f, MidpointRounding.AwayFromZero), 0, 3);
        }
        else
        {
            // frame_is_intra_only
            yF1 = Math.Clamp((int)MathF.Round(q * q * 0.0000033731974f + q * 0.008070594f + 0.0187634f, MidpointRounding.AwayFromZero), 0, 15);
            yF2 = Math.Clamp((int)MathF.Round(q * q * 0.0000029167343f + q * 0.0027798624f + 0.0079405f, MidpointRounding.AwayFromZero), 0, 3);
            uvF1 = Math.Clamp((int)MathF.Round(q * q * -0.0000130790995f + q * 0.012892405f - 0.00748388f, MidpointRounding.AwayFromZero), 0, 15);
            uvF2 = Math.Clamp((int)MathF.Round(q * q * 0.0000032651783f + q * 0.00035520183f + 0.00228092f, MidpointRounding.AwayFromZero), 0, 3);
        }
        ci.CdefStrengths[0] = yF1 * SecStrengths + yF2;
        ci.CdefUvStrengths[0] = avoidUvCdef ? 0 : uvF1 * SecStrengths + uvF2;
        if (skipCdef)
        {
            ci.CdefStrengths[1] = 0;
            ci.CdefUvStrengths[1] = 0;
            return;
        }
        int nvfb = (cm.MiRows + MiSize64 - 1) / MiSize64, nhfb = (cm.MiCols + MiSize64 - 1) / MiSize64;
        for (int r = 0; r < nvfb; ++r)
            for (int c = 0; c < nhfb; ++c)
                cm.MiGridBase[r * MiSize64 * cm.MiStride + MiSize64 * c]!.CdefStrength = 0;
    }

    /// <summary>av1_cdef_frame: filters the frame in place from its pre-CDEF copy (the line / column buffers of
    /// libaom's in-place implementation give the same result).</summary>
    internal static void Frame(AomComp cpi, AomYv12 frame, AomCdefInfo ci)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        int nvfb = (cm.MiRows + MiSize64 - 1) / MiSize64, nhfb = (cm.MiCols + MiSize64 - 1) / MiSize64;
        AomYv12? pre = null;   // the pre-CDEF copy, taken once something is filtered
        var inbuf = new ushort[InbufSize];
        var dlist = new (byte by, byte bx)[MiSize128 * MiSize128];
        var dir = new int[NBlocks * NBlocks];
        var var = new int[NBlocks * NBlocks];
        int inOff = VBorder * BStride + HBorder;
        for (int fbr = 0; fbr < nvfb; fbr++)
        {
            Array.Clear(dir); Array.Clear(var);
            for (int fbc = 0; fbc < nhfb; fbc++)
            {
                var mbmi = cm.MiGridBase[MiSize64 * fbr * cm.MiStride + MiSize64 * fbc];
                if (mbmi == null || mbmi.CdefStrength == -1) continue;
                int idx = mbmi.CdefStrength;
                int levelY = ci.CdefStrengths[idx] / SecStrengths, secY = ci.CdefStrengths[idx] % SecStrengths;
                secY += secY == 3 ? 1 : 0;
                bool zeroY = levelY == 0 && secY == 0, zeroUv = true;
                int levelUv = 0, secUv = 0;
                if (numPlanes > 1)
                {
                    levelUv = ci.CdefUvStrengths[idx] / SecStrengths; secUv = ci.CdefUvStrengths[idx] % SecStrengths;
                    secUv += secUv == 3 ? 1 : 0;
                    zeroUv = levelUv == 0 && secUv == 0;
                }
                if (zeroY && zeroUv) continue;
                int cdefCount = ComputeSbList(cm, fbr * MiSize64, fbc * MiSize64, dlist, BLOCK_64X64);
                if (cdefCount == 0) continue;
                pre ??= frame.Clone();
                for (int plane = 0; plane < numPlanes; plane++)
                {
                    if (plane != 0 && zeroUv) continue;
                    int xdec = plane == 0 ? 0 : cm.SsX, ydec = plane == 0 ? 0 : cm.SsY;
                    int miWideL2 = 2 - xdec, miHighL2 = 2 - ydec;
                    int nhb = Math.Min(MiSize64, cm.MiCols - MiSize64 * fbc), nvb = Math.Min(MiSize64, cm.MiRows - MiSize64 * fbr);
                    int hsize = nhb << miWideL2, vsize = nvb << miHighL2;
                    int roffset = MiSize64 * fbr << miHighL2, coffset = MiSize64 * fbc << miWideL2;
                    var pp = pre.Planes[plane];
                    int planeW = cm.MiCols << miWideL2, planeH = cm.MiRows << miHighL2;
                    // the 16-bit input block: pre-CDEF pixels, CDEF_VERY_LARGE outside the frame (mi-aligned)
                    int i0 = Math.Max(-VBorder, -roffset), i1 = Math.Min(vsize + VBorder, planeH - roffset);
                    int j0 = Math.Max(-HBorder, -coffset), j1 = Math.Min(hsize + HBorder, planeW - coffset);
                    if (i0 > -VBorder) FillRect(inbuf, inOff - VBorder * BStride - HBorder, BStride, i0 + VBorder, hsize + 2 * HBorder, VeryLarge);
                    if (i1 < vsize + VBorder) FillRect(inbuf, inOff + i1 * BStride - HBorder, BStride, vsize + VBorder - i1, hsize + 2 * HBorder, VeryLarge);
                    if (j0 > -HBorder) FillRect(inbuf, inOff + i0 * BStride - HBorder, BStride, i1 - i0, j0 + HBorder, VeryLarge);
                    if (j1 < hsize + HBorder) FillRect(inbuf, inOff + i0 * BStride + j1, BStride, i1 - i0, hsize + HBorder - j1, VeryLarge);
                    var dp = frame.Planes[plane];
                    bool dirinit = false;
                    if (pp.Buf16 != null)
                    {
                        for (int r = 0; r < i1 - i0; r++)
                            Array.Copy(pp.Buf16, pp.At(coffset + j0, roffset + i0 + r), inbuf, inOff + (i0 + r) * BStride + j0, j1 - j0);
                        FilterFb16(dp.Buf16, dp.At(coffset, roffset), dp.Stride, inbuf, inOff, xdec, ydec, dir, ref dirinit, false, var, plane, dlist,
                            cdefCount, plane == 0 ? levelY : levelUv, plane == 0 ? secY : secUv, ci.CdefDamping, cm.BitDepth - 8);
                        continue;
                    }
                    unsafe
                    {
                        fixed (ushort* ib = inbuf)
                        fixed (byte* sb = pp.Buf)
                            CopyRect8To16(ib + inOff + i0 * BStride + j0, BStride, sb + pp.At(coffset + j0, roffset + i0), pp.Stride, j1 - j0, i1 - i0);
                    }
                    FilterFb(dp.Buf, dp.At(coffset, roffset), dp.Stride, inbuf, inOff, xdec, ydec, dir, ref dirinit, false, var, plane, dlist,
                        cdefCount, plane == 0 ? levelY : levelUv, plane == 0 ? secY : secUv, ci.CdefDamping, 0);
                }
            }
        }
    }
}

/// <summary>The per-thread buffers of av1_cdef_mse_calc_block.</summary>
internal sealed class CdefSearchScratch
{
    public readonly ushort[] Inbuf = new ushort[AomCdef.InbufSizeForScratch];
    public readonly (byte by, byte bx)[] Dlist = new (byte by, byte bx)[32 * 32];
    public readonly int[] Dir = new int[AomCdef.NBlocksForScratch * AomCdef.NBlocksForScratch];
    public readonly int[] Var = new int[AomCdef.NBlocksForScratch * AomCdef.NBlocksForScratch];
    public readonly byte[] TmpDst8 = new byte[128 * 128];
    public readonly ushort[] TmpDst16 = new ushort[128 * 128];
}
