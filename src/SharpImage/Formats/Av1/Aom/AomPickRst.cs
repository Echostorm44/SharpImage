using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static SharpImage.Formats.Av1.AomRestoration;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>RestUnitSearchInfo: the per-unit search results (best Wiener / SGR parameters and the unit type chosen for
/// each frame type WIENER, SGRPROJ, SWITCHABLE).</summary>
internal struct AomRestUnitSearchInfo
{
    public AomSgrprojInfo Sgrproj;
    public AomWienerInfo Wiener;
    public int BestRtype0, BestRtype1, BestRtype2;

    public int GetBestRtype(int frameRtype) => frameRtype switch { 1 => BestRtype0, 2 => BestRtype1, _ => BestRtype2 };

    public void SetBestRtype(int frameRtype, int v)
    {
        switch (frameRtype)
        {
            case 1: BestRtype0 = v; break;
            case 2: BestRtype1 = v; break;
            default: BestRtype2 = v; break;
        }
    }
}

/// <summary>What av1_pick_filter_restoration reads besides the frames: cpi->sf.lpf_sf's restoration fields, the rd
/// multiplier, the restoration rate costs (av1_fill_lr_rates of the tile's CDFs), the tile layout.</summary>
internal sealed class AomRstPickConfig
{
    public int MinLrUnitSize = RESTORATION_PROC_UNIT_SIZE, MaxLrUnitSize = RESTORATION_UNITSIZE_MAX;
    public int DisableLoopRestorationLuma, DisableLoopRestorationChroma;
    public bool DisableWienerFilter, DisableSgrFilter, DisableWienerCoeffRefineSearch;
    public int PruneWienerBasedOnSrcVar, PruneSgrBasedOnWiener, ReduceWienerWindowSize, EnableSgrEpPruning;
    public int DualSgrPenaltyLevel, SwitchableLrWithBiasLevel, UseDownsampledWienerStats;
    public int Rdmult;                     // cpi->rd.RDMULT
    public int BaseQindex;
    public int BitDepth = 8;
    public int SbSize = BLOCK_64X64;       // seq_params->sb_size
    public readonly int[] SwitchableRestoreCost = new int[3];
    public readonly int[] WienerRestoreCost = new int[2];
    public readonly int[] SgrprojRestoreCost = new int[2];
    /// <summary>tiles->row_start_sb / col_start_sb (rows + 1 / cols + 1 entries); null = one tile.</summary>
    public int[]? TileRowStartSb, TileColStartSb;

    public void SetSpeedFeatures(AomLoopFilterSpeedFeatures s)
    {
        MinLrUnitSize = s.min_lr_unit_size;
        MaxLrUnitSize = s.max_lr_unit_size;
        DisableLoopRestorationLuma = s.disable_loop_restoration_luma;
        DisableLoopRestorationChroma = s.disable_loop_restoration_chroma;
        DisableWienerFilter = s.disable_wiener_filter;
        DisableSgrFilter = s.disable_sgr_filter;
        DisableWienerCoeffRefineSearch = s.disable_wiener_coeff_refine_search;
        PruneWienerBasedOnSrcVar = s.prune_wiener_based_on_src_var;
        PruneSgrBasedOnWiener = s.prune_sgr_based_on_wiener;
        ReduceWienerWindowSize = s.reduce_wiener_window_size;
        EnableSgrEpPruning = s.enable_sgr_ep_pruning;
        DualSgrPenaltyLevel = s.dual_sgr_penalty_level;
        SwitchableLrWithBiasLevel = s.switchable_lr_with_bias_level;
        UseDownsampledWienerStats = s.use_downsampled_wiener_stats;
    }
}

/// <summary>Port of libaom av1/encoder/pickrst.c (8-bit): av1_pick_filter_restoration and everything under it (unit
/// size search, Wiener statistics / separable symmetric decomposition / coefficient refinement, the self-guided
/// projection search, switchable, the rd costs with the delta-coded reference parameters per tile). The AVX2 kernels
/// libaom dispatches (av1_compute_stats, av1_calc_proj_params, av1_lowbd_pixel_proj_error, av1_selfguided_restoration)
/// are exact integer computations equal to the C ones ported here (twin-verified).</summary>
internal sealed class AomPickRst
{
    private const int NumWienerIters = 5;
    private const double DualSgrPenaltyMult = 0.01, WienerSgrPenaltyMult = 0.005;
    private const long WienerTapScaleFactor = 1L << 16;
    private const int AvProbCostShift = 9, RdDivBits = 7;

    private static readonly int[] SgprojEpGrp1Seed = { 0, 3, 6, 9 };
    private static readonly int[,] SgprojEpGrp23 =
    {
        { 10, 10, 11, 11, 12, 12, 13, 13, 13, 13, -1, -1, -1, -1 },
        { 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15 },
    };

    private readonly AomYv12 _src, _dgd, _trial;
    private readonly AomRestorationInfo[] _rst;
    private readonly AomRstPickConfig _cfg;
    private readonly AomRestUnitSearchInfo[][] _rusi;
    private readonly UnitScratch _sc = new();
    private readonly int[] _rstBuf0 = new int[RestorationUnitPelsMax], _rstBuf1 = new int[RestorationUnitPelsMax];
    private readonly long[] _m = new long[WienerWin2], _h = new long[WienerWin2 * WienerWin2];
    private readonly int _miRows, _miCols, _mibSizeLog2;

    // RestSearchCtxt
    private int _plane, _planeW, _planeH;
    private AomYv12Plane _srcPlane = null!, _dgdPlane = null!, _dstPlane = null!;
    private readonly long[] _sse = new long[RestoreSwitchableTypes];
    private bool _skipSgrEval;
    public readonly long[] TotalSse = new long[RestoreTypes], TotalBits = new long[RestoreTypes];
    private AomWienerInfo _refWiener, _switchableRefWiener;
    private AomSgrprojInfo _refSgrproj, _switchableRefSgrproj;

    /// <summary>src: the source frame; dgd: the deblocked (CDEF'd) frame the search reads (its borders get extended);
    /// rst: per-plane restoration info, whose Boundaries must hold the saved stripe lines.</summary>
    public AomPickRst(AomYv12 src, AomYv12 dgd, AomRestorationInfo[] rst, AomRstPickConfig cfg)
    {
        _src = src;
        _dgd = dgd;
        _rst = rst;
        _cfg = cfg;
        _trial = dgd.CloneGeometry();
        _miCols = ((dgd.Width + 7) & ~7) >> 2;
        _miRows = ((dgd.Height + 7) & ~7) >> 2;
        _mibSizeLog2 = MiSizeWideLog2[cfg.SbSize];
        // allocate_search_structs: sized for the minimum unit size, zeroed once per frame and reused by every size
        int minLrUnitSize = Math.Max(cfg.MinLrUnitSize, BlockSizeWide[cfg.SbSize]);
        _rusi = new AomRestUnitSearchInfo[dgd.NumPlanes][];
        for (int plane = 0; plane < dgd.NumPlanes; plane++)
        {
            GetPlaneSize(dgd, plane > 0, out int pw, out int ph);
            _rusi[plane] = new AomRestUnitSearchInfo[LrCountUnits(minLrUnitSize, pw) * LrCountUnits(minLrUnitSize, ph)];
        }
    }

    public AomRestUnitSearchInfo[] UnitSearchInfo(int plane) => _rusi[plane];

    // RDCOST_DBL_WITH_NATIVE_BD_DIST
    private double RdcostDbl(long r, long d)
        => r * (double)_cfg.Rdmult / (1 << AvProbCostShift) + (double)(d >> (2 * (_cfg.BitDepth - 8))) * (1 << RdDivBits);

    // ---- bit counts (aom_dsp/binary_codes_writer.c) -------------------------------------------------------------------

    private static int CountPrimitiveQuniform(int n, int v)
    {
        if (n <= 1) return 0;
        int l = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)n) + 1;
        int m = (1 << l) - n;
        return v < m ? l - 1 : l;
    }

    private static int CountPrimitiveSubexpfin(int n, int k, int v)
    {
        int count = 0, i = 0, mk = 0;
        while (true)
        {
            int b = i != 0 ? k + i - 1 : k;
            int a = 1 << b;
            if (n <= mk + 3 * a)
            {
                count += CountPrimitiveQuniform(n - mk, v - mk);
                break;
            }
            bool t = v >= mk + a;
            count++;
            if (t)
            {
                i++;
                mk += a;
            }
            else
            {
                count += b;
                break;
            }
        }
        return count;
    }

    private static int RecenterNonneg(int r, int v) => v > (r << 1) ? v : v >= r ? (v - r) << 1 : ((r - v) << 1) - 1;

    private static int RecenterFiniteNonneg(int n, int r, int v)
        => (r << 1) <= n ? RecenterNonneg(r, v) : RecenterNonneg(n - 1 - r, n - 1 - v);

    /// <summary>aom_count_primitive_refsubexpfin (arguments as the uint16_t libaom takes).</summary>
    public static int CountRefSubexpfin(int n, int k, int r, int v)
        => CountPrimitiveSubexpfin((ushort)n, (ushort)k, (ushort)RecenterFiniteNonneg((ushort)n, (ushort)r, (ushort)v));

    public static int CountSgrprojBits(in AomSgrprojInfo s, in AomSgrprojInfo r)
    {
        int bits = SgrprojParamsBits;
        if (SgrR0[s.Ep] > 0)
            bits += CountRefSubexpfin(SgrprojPrjMax0 - SgrprojPrjMin0 + 1, SgrprojPrjSubexpK, r.Xqd0 - SgrprojPrjMin0,
                s.Xqd0 - SgrprojPrjMin0);
        if (SgrR1[s.Ep] > 0)
            bits += CountRefSubexpfin(SgrprojPrjMax1 - SgrprojPrjMin1 + 1, SgrprojPrjSubexpK, r.Xqd1 - SgrprojPrjMin1,
                s.Xqd1 - SgrprojPrjMin1);
        return bits;
    }

    public static int CountWienerBits(int wienerWin, in AomWienerInfo w, in AomWienerInfo r)
    {
        int bits = 0;
        if (wienerWin == WienerWin)
            bits += CountRefSubexpfin(WienerFiltTap0Maxv - WienerFiltTap0Minv + 1, WienerFiltTap0SubexpK,
                r.V[0] - WienerFiltTap0Minv, w.V[0] - WienerFiltTap0Minv);
        bits += CountRefSubexpfin(WienerFiltTap1Maxv - WienerFiltTap1Minv + 1, WienerFiltTap1SubexpK,
            r.V[1] - WienerFiltTap1Minv, w.V[1] - WienerFiltTap1Minv);
        bits += CountRefSubexpfin(WienerFiltTap2Maxv - WienerFiltTap2Minv + 1, WienerFiltTap2SubexpK,
            r.V[2] - WienerFiltTap2Minv, w.V[2] - WienerFiltTap2Minv);
        if (wienerWin == WienerWin)
            bits += CountRefSubexpfin(WienerFiltTap0Maxv - WienerFiltTap0Minv + 1, WienerFiltTap0SubexpK,
                r.H[0] - WienerFiltTap0Minv, w.H[0] - WienerFiltTap0Minv);
        bits += CountRefSubexpfin(WienerFiltTap1Maxv - WienerFiltTap1Minv + 1, WienerFiltTap1SubexpK,
            r.H[1] - WienerFiltTap1Minv, w.H[1] - WienerFiltTap1Minv);
        bits += CountRefSubexpfin(WienerFiltTap2Maxv - WienerFiltTap2Minv + 1, WienerFiltTap2SubexpK,
            r.H[2] - WienerFiltTap2Minv, w.H[2] - WienerFiltTap2Minv);
        return bits;
    }

    // ---- self-guided search ---------------------------------------------------------------------------------------------

    /// <summary>av1_lowbd_pixel_proj_error_c.</summary>
    public static long LowbdPixelProjError(byte[] src8, int s0, int width, int height, int srcStride, byte[] dat8, int d0,
        int datStride, int[] flt0, int f0, int flt0Stride, int[] flt1, int f1, int flt1Stride, int xq0, int xq1, int ep)
    {
        long err = 0;
        const int sh = SgrprojRstBits + SgrprojPrjBits;
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        bool simd = Avx2.IsSupported && width >= 8 && d0 >= 0 && s0 >= 0 && f0 >= 0 && f1 >= 0
            && d0 + (long)(height - 1) * datStride + width <= dat8.Length && s0 + (long)(height - 1) * srcStride + width <= src8.Length
            && f0 + (long)(height - 1) * flt0Stride + width <= flt0.Length && f1 + (long)(height - 1) * flt1Stride + width <= flt1.Length;
        var rnd = Vector256.Create(1 << (sh - 1));
        var vxq0 = Vector256.Create(r0 ? xq0 : 0);
        var vxq1 = Vector256.Create(r1 ? xq1 : 0);
        ref byte dat0 = ref MemoryMarshal.GetArrayDataReference(dat8);
        ref byte src0 = ref MemoryMarshal.GetArrayDataReference(src8);
        ref int fl0 = ref MemoryMarshal.GetArrayDataReference(flt0);
        ref int fl1 = ref MemoryMarshal.GetArrayDataReference(flt1);
        for (int i = 0; i < height; ++i)
        {
            int dr = d0 + i * datStride, sr = s0 + i * srcStride, a = f0 + i * flt0Stride, b = f1 + i * flt1Stride;
            int j0 = 0;
            if (simd)
            {
                // 8 pixels a lane: the same int32 v and e; e^2 summed in int64 lanes (exact)
                var acc = Vector256<long>.Zero;
                for (; j0 + 8 <= width; j0 += 8)
                {
                    var d = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dat0, dr + j0))).AsByte());
                    var sv = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src0, sr + j0))).AsByte());
                    Vector256<int> e;
                    if (r0 || r1)
                    {
                        var u = Vector256.ShiftLeft(d, SgrprojRstBits);
                        var v = Vector256.ShiftLeft(u, SgrprojPrjBits);
                        if (r0) v += vxq0 * (Vector256.LoadUnsafe(ref fl0, (nuint)(a + j0)) - u);
                        if (r1) v += vxq1 * (Vector256.LoadUnsafe(ref fl1, (nuint)(b + j0)) - u);
                        e = Vector256.ShiftRightArithmetic(v + rnd, sh) - sv;
                    }
                    else e = d - sv;
                    var e2 = e * e;
                    acc += Avx2.ConvertToVector256Int64(e2.GetLower()) + Avx2.ConvertToVector256Int64(e2.GetUpper());
                }
                err += Vector256.Sum(acc);
            }
            for (int j = j0; j < width; ++j)
            {
                int e;
                if (r0 || r1)
                {
                    int u = dat8[dr + j] << SgrprojRstBits;
                    int v = u << SgrprojPrjBits;
                    if (r0 && r1) v += xq0 * (flt0[a + j] - u) + xq1 * (flt1[b + j] - u);
                    else if (r0) v += xq0 * (flt0[a + j] - u);
                    else v += xq1 * (flt1[b + j] - u);
                    e = ((v + (1 << (sh - 1))) >> sh) - src8[sr + j];
                }
                else
                {
                    e = dat8[dr + j] - src8[sr + j];
                }
                err += (long)e * e;
            }
        }
        return err;
    }

    /// <summary>av1_calc_proj_params_c: H (2x2, row-major) and C.</summary>
    public static void CalcProjParams(byte[] src8, int s0, int width, int height, int srcStride, byte[] dat8, int d0,
        int datStride, int[] flt0, int f0, int flt0Stride, int[] flt1, int f1, int flt1Stride, Span<long> H,
        Span<long> C, int ep)
    {
        int size = width * height;
        bool r0 = SgrR0[ep] > 0, r1 = SgrR1[ep] > 0;
        if (!r0 && !r1) return;
        long h00 = 0, h01 = 0, h11 = 0, c0 = 0, c1 = 0;
        bool simd = Avx2.IsSupported && width >= 8 && d0 >= 0 && s0 >= 0 && f0 >= 0 && f1 >= 0
            && d0 + (long)(height - 1) * datStride + width <= dat8.Length && s0 + (long)(height - 1) * srcStride + width <= src8.Length
            && (!r0 || f0 + (long)(height - 1) * flt0Stride + width <= flt0.Length)
            && (!r1 || f1 + (long)(height - 1) * flt1Stride + width <= flt1.Length);
        ref byte dat0 = ref MemoryMarshal.GetArrayDataReference(dat8);
        ref byte src0 = ref MemoryMarshal.GetArrayDataReference(src8);
        ref int fl0 = ref MemoryMarshal.GetArrayDataReference(flt0);
        ref int fl1 = ref MemoryMarshal.GetArrayDataReference(flt1);
        for (int i = 0; i < height; ++i)
        {
            int j0 = 0;
            if (simd)
            {
                // the same int32 differences (|.| < 2^12, so every product fits 2^24) summed in int64 lanes
                Vector256<long> a00 = default, a01 = default, a11 = default, ac0 = default, ac1 = default;
                static Vector256<long> W(Vector256<int> p) => Avx2.ConvertToVector256Int64(p.GetLower()) + Avx2.ConvertToVector256Int64(p.GetUpper());
                int dr = d0 + i * datStride, sr = s0 + i * srcStride, fa0 = f0 + i * flt0Stride, fb0 = f1 + i * flt1Stride;
                for (; j0 + 8 <= width; j0 += 8)
                {
                    var u = Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dat0, dr + j0))).AsByte()), SgrprojRstBits);
                    var sv = Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src0, sr + j0))).AsByte()), SgrprojRstBits) - u;
                    if (r0)
                    {
                        var fa = Vector256.LoadUnsafe(ref fl0, (nuint)(fa0 + j0)) - u;
                        a00 += W(fa * fa); ac0 += W(fa * sv);
                        if (r1)
                        {
                            var fb = Vector256.LoadUnsafe(ref fl1, (nuint)(fb0 + j0)) - u;
                            a11 += W(fb * fb); a01 += W(fa * fb); ac1 += W(fb * sv);
                        }
                    }
                    else
                    {
                        var fb = Vector256.LoadUnsafe(ref fl1, (nuint)(fb0 + j0)) - u;
                        a11 += W(fb * fb); ac1 += W(fb * sv);
                    }
                }
                h00 += Vector256.Sum(a00); h01 += Vector256.Sum(a01); h11 += Vector256.Sum(a11);
                c0 += Vector256.Sum(ac0); c1 += Vector256.Sum(ac1);
            }
            for (int j = j0; j < width; ++j)
            {
                int u = dat8[d0 + i * datStride + j] << SgrprojRstBits;
                int s = (src8[s0 + i * srcStride + j] << SgrprojRstBits) - u;
                if (r0)
                {
                    int fa = flt0[f0 + i * flt0Stride + j] - u;
                    h00 += (long)fa * fa;
                    c0 += (long)fa * s;
                    if (r1)
                    {
                        int fb = flt1[f1 + i * flt1Stride + j] - u;
                        h11 += (long)fb * fb;
                        h01 += (long)fa * fb;
                        c1 += (long)fb * s;
                    }
                }
                else
                {
                    int fb = flt1[f1 + i * flt1Stride + j] - u;
                    h11 += (long)fb * fb;
                    c1 += (long)fb * s;
                }
            }
        }
        if (r0 && r1)
        {
            H[0] = h00 / size; H[1] = h01 / size; H[3] = h11 / size; H[2] = H[1];
            C[0] = c0 / size; C[1] = c1 / size;
        }
        else if (r0)
        {
            H[0] = h00 / size;
            C[0] = c0 / size;
        }
        else
        {
            H[3] = h11 / size;
            C[1] = c1 / size;
        }
    }

    private static long SignedRoundedDivide(long dividend, long divisor)
        => dividend < 0 ? (dividend - divisor / 2) / divisor : (dividend + divisor / 2) / divisor;

    private static void GetProjSubspace(byte[] src8, int s0, int width, int height, int srcStride, byte[] dat8, int d0,
        int datStride, int[] flt0, int f0, int flt0Stride, int[] flt1, int f1, int flt1Stride, out int xq0, out int xq1,
        int ep)
    {
        Span<long> H = stackalloc long[4];
        Span<long> C = stackalloc long[2];
        H.Clear();
        C.Clear();
        xq0 = 0;
        xq1 = 0;
        CalcProjParams(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0, f0, flt0Stride, flt1, f1,
            flt1Stride, H, C, ep);
        if (SgrR0[ep] == 0)
        {
            long det = H[3];
            if (det == 0) return;
            xq0 = 0;
            xq1 = (int)SignedRoundedDivide(C[1] * (1 << SgrprojPrjBits), det);
        }
        else if (SgrR1[ep] == 0)
        {
            long det = H[0];
            if (det == 0) return;
            xq0 = (int)SignedRoundedDivide(C[0] * (1 << SgrprojPrjBits), det);
            xq1 = 0;
        }
        else
        {
            long det = unchecked(H[0] * H[3] - H[1] * H[2]);
            if (det == 0) return;
            long div1 = unchecked(H[3] * C[0] - H[1] * C[1]);
            if ((div1 > 0 && long.MaxValue / (1 << SgrprojPrjBits) < div1) ||
                (div1 < 0 && long.MinValue / (1 << SgrprojPrjBits) > div1))
                xq0 = (int)SignedRoundedDivide(div1, det / (1 << SgrprojPrjBits));
            else
                xq0 = (int)SignedRoundedDivide(div1 * (1 << SgrprojPrjBits), det);
            long div2 = unchecked(H[0] * C[1] - H[2] * C[0]);
            if ((div2 > 0 && long.MaxValue / (1 << SgrprojPrjBits) < div2) ||
                (div2 < 0 && long.MinValue / (1 << SgrprojPrjBits) > div2))
                xq1 = (int)SignedRoundedDivide(div2, det / (1 << SgrprojPrjBits));
            else
                xq1 = (int)SignedRoundedDivide(div2 * (1 << SgrprojPrjBits), det);
        }
    }

    private static void EncodeXq(int xq0, int xq1, out int xqd0, out int xqd1, int ep)
    {
        if (SgrR0[ep] == 0)
        {
            xqd0 = 0;
            xqd1 = Math.Clamp((1 << SgrprojPrjBits) - xq1, SgrprojPrjMin1, SgrprojPrjMax1);
        }
        else if (SgrR1[ep] == 0)
        {
            xqd0 = Math.Clamp(xq0, SgrprojPrjMin0, SgrprojPrjMax0);
            xqd1 = Math.Clamp((1 << SgrprojPrjBits) - xqd0, SgrprojPrjMin1, SgrprojPrjMax1);
        }
        else
        {
            xqd0 = Math.Clamp(xq0, SgrprojPrjMin0, SgrprojPrjMax0);
            xqd1 = Math.Clamp((1 << SgrprojPrjBits) - xqd0 - xq1, SgrprojPrjMin1, SgrprojPrjMax1);
        }
    }

    private static long GetPixelProjError(byte[] src8, int s0, int width, int height, int srcStride, byte[] dat8, int d0,
        int datStride, int[] flt0, int[] flt1, int fltStride, int xqd0, int xqd1, int ep)
    {
        DecodeXq(xqd0, xqd1, ep, out int xq0, out int xq1);
        return LowbdPixelProjError(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0, 0, fltStride, flt1, 0,
            fltStride, xq0, xq1, ep);
    }

    private static long FinerSearchPixelProjError(byte[] src8, int s0, int width, int height, int srcStride, byte[] dat8,
        int d0, int datStride, int[] flt0, int[] flt1, int fltStride, int startStep, Span<int> xqd, int ep)
    {
        long err = GetPixelProjError(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0, flt1, fltStride,
            xqd[0], xqd[1], ep);
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
                        long err2 = GetPixelProjError(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0,
                            flt1, fltStride, xqd[0], xqd[1], ep);
                        if (err2 > err)
                        {
                            xqd[p] += s;
                        }
                        else
                        {
                            err = err2;
                            skip = true;
                            // At the highest step size continue moving in the same direction
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
                        long err2 = GetPixelProjError(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0,
                            flt1, fltStride, xqd[0], xqd[1], ep);
                        if (err2 > err)
                        {
                            xqd[p] -= s;
                        }
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

    private static void ApplySgr(int ep, byte[] dat8, int d0, int width, int height, int datStride, int puWidth,
        int puHeight, int[] flt0, int[] flt1, int fltStride, SgrScratch sc)
    {
        for (int i = 0; i < height; i += puHeight)
        {
            int h = Math.Min(puHeight, height - i);
            for (int j = 0; j < width; j += puWidth)
            {
                int w = Math.Min(puWidth, width - j);
                SelfguidedRestoration(dat8, d0 + i * datStride + j, w, h, datStride, flt0, i * fltStride + j, flt1,
                    i * fltStride + j, fltStride, ep, sc);
            }
        }
    }

    private static void ComputeSgrprojErr(byte[] dat8, int d0, int width, int height, int datStride, byte[] src8, int s0,
        int srcStride, int puWidth, int puHeight, int ep, int[] flt0, int[] flt1, int fltStride, Span<int> exqd,
        out long err, SgrScratch sc)
    {
        ApplySgr(ep, dat8, d0, width, height, datStride, puWidth, puHeight, flt0, flt1, fltStride, sc);
        GetProjSubspace(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0, 0, fltStride, flt1, 0, fltStride,
            out int xq0, out int xq1, ep);
        EncodeXq(xq0, xq1, out exqd[0], out exqd[1], ep);
        err = FinerSearchPixelProjError(src8, s0, width, height, srcStride, dat8, d0, datStride, flt0, flt1, fltStride, 2,
            exqd, ep);
    }

    /// <summary>search_selfguided_restoration.</summary>
    public static AomSgrprojInfo SearchSelfguided(byte[] dat8, int d0, int width, int height, int datStride,
        byte[] src8, int s0, int srcStride, int puWidth, int puHeight, int[] flt0, int[] flt1, int enableSgrEpPruning,
        SgrScratch sc)
    {
        int bestep = 0;
        long besterr = -1;
        Span<int> exqd = stackalloc int[2];
        Span<int> bestxqd = stackalloc int[2];
        bestxqd.Clear();
        int fltStride = ((width + 7) & ~7) + 8;

        void Eval(int ep, Span<int> exqd, Span<int> bestxqd, ref long besterr, ref int bestep)
        {
            ComputeSgrprojErr(dat8, d0, width, height, datStride, src8, s0, srcStride, puWidth, puHeight, ep, flt0,
                flt1, fltStride, exqd, out long err, sc);
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
            for (int idx = 0; idx < SgprojEpGrp1Seed.Length; idx++)
                Eval(SgprojEpGrp1Seed[idx], exqd, bestxqd, ref besterr, ref bestep);
            if (enableSgrEpPruning < 2)
            {
                int bestepRef = bestep;
                for (int ep = bestepRef - 1; ep < bestepRef + 2; ep += 2)
                {
                    if (ep < 0 || ep > 9) continue;
                    Eval(ep, exqd, bestxqd, ref besterr, ref bestep);
                }
                for (int idx = 0; idx < 2; idx++)
                    Eval(SgprojEpGrp23[idx, bestep], exqd, bestxqd, ref besterr, ref bestep);
            }
        }
        return new AomSgrprojInfo { Ep = bestep, Xqd0 = bestxqd[0], Xqd1 = bestxqd[1] };
    }

    // ---- Wiener search --------------------------------------------------------------------------------------------------

    /// <summary>find_average.</summary>
    private static byte FindAverage(byte[] src, AomYv12Plane p, int hStart, int hEnd, int vStart, int vEnd)
    {
        ulong sum = 0;
        for (int i = vStart; i < vEnd; i++)
        {
            int r = p.At(0, i);
            for (int j = hStart; j < hEnd; j++) sum += src[r + j];
        }
        return (byte)(sum / (ulong)((vEnd - vStart) * (hEnd - hStart)));
    }

    /// <summary>av1_compute_stats_c (8-bit): M (win2) and the symmetric H (win2 x win2).</summary>
    public static void ComputeStats(int wienerWin, AomYv12Plane dgd, AomYv12Plane src, int hStart, int hEnd,
        int vStart, int vEnd, long[] M, long[] H, int useDownsampledWienerStats)
    {
        int wienerWin2 = wienerWin * wienerWin, halfwin = wienerWin >> 1;
        byte avg = FindAverage(dgd.Buf, dgd, hStart, hEnd, vStart, vEnd);
        Array.Clear(M, 0, wienerWin2);
        Array.Clear(H, 0, wienerWin2 * wienerWin2);
        int downsampleFactor = useDownsampledWienerStats != 0 ? 4 : 1;
        // the window samples padded to whole 8-lane vectors; per-row int32 sums (|Y| <= 255 over at most 384 columns
        // cannot overflow), whole rows accumulated (the entries below the diagonal are dropped: the symmetrisation below
        // overwrites them)
        int padded = (wienerWin2 + 7) & ~7;
        Span<int> y = stackalloc int[padded];
        y.Clear();
        var mRow = new int[wienerWin2];
        var hRow = new int[wienerWin2 * padded];
        byte[] d = dgd.Buf, s = src.Buf;
        Span<int> rowBase = stackalloc int[WienerWin];
        ref int y0 = ref MemoryMarshal.GetReference(y);
        ref int h0 = ref MemoryMarshal.GetArrayDataReference(hRow);
        // the int32 row sums of up to 32 rows with one downsample factor are summed before scaling into the int64 totals
        // (exact: the products distribute; 32 rows x 384 columns x 255^2 stays below 2^31)
        int pendingRows = 0, pendingFactor = downsampleFactor;
        void Flush()
        {
            for (int k = 0; k < wienerWin2; ++k)
            {
                M[k] += (long)mRow[k] * pendingFactor;
                for (int l = k; l < wienerWin2; ++l) H[k * wienerWin2 + l] += (long)hRow[k * padded + l] * pendingFactor;
            }
            Array.Clear(mRow);
            Array.Clear(hRow);
            pendingRows = 0;
        }
        for (int i = vStart; i < vEnd; i += downsampleFactor)
        {
            if (useDownsampledWienerStats != 0 && vEnd - i < 4) downsampleFactor = vEnd - i;
            if (pendingRows > 0 && (pendingRows == 32 || downsampleFactor != pendingFactor)) Flush();
            pendingFactor = downsampleFactor;
            pendingRows++;
            // acc_stat_one_line
            int srow = src.At(0, i);
            for (int l = -halfwin; l <= halfwin; l++) rowBase[l + halfwin] = dgd.At(0, i + l);
            for (int j = hStart; j < hEnd; j++)
            {
                int x = s[srow + j] - avg;
                int idx = 0;
                for (int k = -halfwin; k <= halfwin; k++)
                    for (int l = 0; l < wienerWin; l++)
                        y[idx++] = d[rowBase[l] + j + k] - avg;
                for (int k = 0; k < wienerWin2; ++k)
                {
                    int yk = y[k];
                    mRow[k] += yk * x;
                    var vk = Vector256.Create(yk);
                    ref int hr = ref Unsafe.Add(ref h0, k * padded);
                    for (int l = k & ~7; l < padded; l += 8)
                        (Vector256.LoadUnsafe(ref hr, (nuint)l) + vk * Vector256.LoadUnsafe(ref y0, (nuint)l)).StoreUnsafe(ref hr, (nuint)l);
                }
            }
        }
        if (pendingRows > 0) Flush();
        for (int k = 0; k < wienerWin2; ++k)
            for (int l = k + 1; l < wienerWin2; ++l) H[l * wienerWin2 + k] = H[k * wienerWin2 + l];
    }

    private static int WrapIndex(int i, int wienerWin)
    {
        int halfwin1 = (wienerWin >> 1) + 1;
        return i >= halfwin1 ? wienerWin - 1 - i : i;
    }

    private static long Llabs(long v) => v < 0 ? unchecked(-v) : v;

    /// <summary>linsolve_wiener: Gaussian elimination with partial pivoting in int64 (x scaled by 2^16).</summary>
    public static bool LinsolveWiener(int n, long[] A, int stride, long[] b, long[] x)
    {
        unchecked
        {
            for (int k = 0; k < n - 1; k++)
            {
                // Partial pivoting: bring the row with the largest pivot to the top
                for (int i = n - 1; i > k; i--)
                {
                    if (Llabs(A[(i - 1) * stride + k]) < Llabs(A[i * stride + k]))
                    {
                        for (int j = 0; j < n; j++)
                        {
                            long c = A[i * stride + j];
                            A[i * stride + j] = A[(i - 1) * stride + j];
                            A[(i - 1) * stride + j] = c;
                        }
                        long cb = b[i];
                        b[i] = b[i - 1];
                        b[i - 1] = cb;
                    }
                }
                long maxAbsAkj = 0;
                for (int j = 0; j < n; j++)
                {
                    long absAkj = Llabs(A[k * stride + j]);
                    if (absAkj > maxAbsAkj) maxAbsAkj = absAkj;
                }
                const int scaleThreshold = 1 << 22;
                int scalerA = maxAbsAkj < scaleThreshold ? 1 : 1 << 6;
                int scalerC = maxAbsAkj < scaleThreshold ? 1 : 1 << 7;
                int scaler = scalerC * scalerA;
                // Forward elimination (convert A to row-echelon form)
                for (int i = k; i < n - 1; i++)
                {
                    if (A[k * stride + k] == 0) return false;
                    long c = A[(i + 1) * stride + k] / scalerC;
                    long cd = A[k * stride + k];
                    for (int j = 0; j < n; j++) A[(i + 1) * stride + j] -= A[k * stride + j] / scalerA * c / cd * scaler;
                    b[i + 1] -= c * b[k] / cd * scalerC;
                }
            }
            // Back-substitution
            for (int i = n - 1; i >= 0; i--)
            {
                if (A[i * stride + i] == 0) return false;
                long c = 0;
                for (int j = i + 1; j <= n - 1; j++) c += A[i * stride + j] * x[j] / WienerTapScaleFactor;
                // Store filter taps x in scaled form.
                x[i] = WienerTapScaleFactor * (b[i] - c) / A[i * stride + i];
            }
            return true;
        }
    }

    private static void SplitWienerFilterCoefficients(int wienerWin, int[] w, Span<int> w1, Span<int> w2)
    {
        for (int i = 0; i < wienerWin; i++)
        {
            w1[i] = (int)(w[i] / WienerTapScaleFactor);
            w2[i] = (int)(w[i] - w1[i] * WienerTapScaleFactor);
        }
    }

    private static long MultiplyAndScale(long x, int w1, int w2) => unchecked(x * w1 + x * w2 / WienerTapScaleFactor);

    // Hc[idx][off] = H + (idx / win) * win * win2 + (idx % win) * win + off
    private static long HcAt(long[] H, int wienerWin, int idx, int off)
        => H[(idx / wienerWin) * wienerWin * wienerWin * wienerWin + (idx % wienerWin) * wienerWin + off];

    private static void SolveAndSet(int wienerWin, long[] A, long[] B, int[] outp)
    {
        int halfwin1 = (wienerWin >> 1) + 1;
        unchecked
        {
            // Normalization enforcement in the system of equations itself
            for (int i = 0; i < halfwin1 - 1; ++i)
                A[i] -= A[halfwin1 - 1] * 2 + B[i * halfwin1 + halfwin1 - 1]
                        - 2 * B[(halfwin1 - 1) * halfwin1 + (halfwin1 - 1)];
            for (int i = 0; i < halfwin1 - 1; ++i)
                for (int j = 0; j < halfwin1 - 1; ++j)
                    B[i * halfwin1 + j] -= 2 * (B[i * halfwin1 + (halfwin1 - 1)] + B[(halfwin1 - 1) * halfwin1 + j]
                                                - 2 * B[(halfwin1 - 1) * halfwin1 + (halfwin1 - 1)]);
        }
        var S = new long[WienerWin];
        if (LinsolveWiener(halfwin1 - 1, B, halfwin1, A, S))
        {
            S[halfwin1 - 1] = WienerTapScaleFactor;
            for (int i = halfwin1; i < wienerWin; ++i)
            {
                S[i] = S[wienerWin - 1 - i];
                S[halfwin1 - 1] -= 2 * S[i];
            }
            const long lo = -(1L << (WienerFiltBits - 1)), hi = (1L << (WienerFiltBits - 1)) - 1;
            for (int i = 0; i < wienerWin; ++i) outp[i] = (int)(S[i] < lo ? lo : S[i] > hi ? hi : S[i]);
        }
    }

    // Fix vector b, update vector a
    private static void UpdateASepSym(int wienerWin, long[] M, long[] H, int[] a, int[] b)
    {
        int wienerWin2 = wienerWin * wienerWin, halfwin1 = (wienerWin >> 1) + 1;
        var A = new long[WienerHalfwin1];
        var B = new long[WienerHalfwin1 * WienerHalfwin1];
        Span<int> b1 = stackalloc int[WienerWin], b2 = stackalloc int[WienerWin];
        unchecked
        {
            for (int i = 0; i < wienerWin; i++)
                for (int j = 0; j < wienerWin; ++j)
                    A[WrapIndex(j, wienerWin)] += M[i * wienerWin + j] * b[i] / WienerTapScaleFactor;
            SplitWienerFilterCoefficients(wienerWin, b, b1, b2);
            for (int i = 0; i < wienerWin; i++)
                for (int j = 0; j < wienerWin; j++)
                    for (int k = 0; k < wienerWin; ++k)
                    {
                        int kk = WrapIndex(k, wienerWin);
                        for (int l = 0; l < wienerWin; ++l)
                        {
                            int ll = WrapIndex(l, wienerWin);
                            long x = HcAt(H, wienerWin, j * wienerWin + i, k * wienerWin2 + l) * b[i] / WienerTapScaleFactor;
                            B[ll * halfwin1 + kk] += MultiplyAndScale(x, b1[j], b2[j]);
                        }
                    }
        }
        SolveAndSet(wienerWin, A, B, a);
    }

    // Fix vector a, update vector b
    private static void UpdateBSepSym(int wienerWin, long[] M, long[] H, int[] a, int[] b)
    {
        int wienerWin2 = wienerWin * wienerWin, halfwin1 = (wienerWin >> 1) + 1;
        var A = new long[WienerHalfwin1];
        var B = new long[WienerHalfwin1 * WienerHalfwin1];
        Span<int> a1 = stackalloc int[WienerWin], a2 = stackalloc int[WienerWin];
        unchecked
        {
            for (int i = 0; i < wienerWin; i++)
            {
                int ii = WrapIndex(i, wienerWin);
                for (int j = 0; j < wienerWin; j++) A[ii] += M[i * wienerWin + j] * a[j] / WienerTapScaleFactor;
            }
            SplitWienerFilterCoefficients(wienerWin, a, a1, a2);
            for (int i = 0; i < wienerWin; i++)
            {
                int ii = WrapIndex(i, wienerWin);
                for (int j = 0; j < wienerWin; j++)
                {
                    int jj = WrapIndex(j, wienerWin);
                    for (int k = 0; k < wienerWin; ++k)
                        for (int l = 0; l < wienerWin; ++l)
                        {
                            long x = HcAt(H, wienerWin, i * wienerWin + j, k * wienerWin2 + l) * a[k] / WienerTapScaleFactor;
                            B[jj * halfwin1 + ii] += MultiplyAndScale(x, a1[l], a2[l]);
                        }
                }
            }
        }
        SolveAndSet(wienerWin, A, B, b);
    }

    /// <summary>wiener_decompose_sep_sym: a (vertical) and b (horizontal) taps scaled by 2^16.</summary>
    public static void WienerDecomposeSepSym(int wienerWin, long[] M, long[] H, int[] a, int[] b)
    {
        ReadOnlySpan<int> initFilt = stackalloc int[]
        {
            WienerFiltTap0Midv, WienerFiltTap1Midv, WienerFiltTap2Midv,
            WienerFiltStep - 2 * (WienerFiltTap0Midv + WienerFiltTap1Midv + WienerFiltTap2Midv),
            WienerFiltTap2Midv, WienerFiltTap1Midv, WienerFiltTap0Midv,
        };
        int planeOff = (WienerWin - wienerWin) >> 1;
        for (int i = 0; i < wienerWin; i++)
            a[i] = b[i] = (int)(WienerTapScaleFactor / WienerFiltStep * initFilt[i + planeOff]);
        for (int iter = 1; iter < NumWienerIters; iter++)
        {
            UpdateASepSym(wienerWin, M, H, a, b);
            UpdateBSepSym(wienerWin, M, H, a, b);
        }
    }

    /// <summary>compute_score: x'Hx - 2x'M of the separable filter minus that of the identity.</summary>
    public static long ComputeScore(int wienerWin, long[] M, long[] H, in AomTaps8 vfilt, in AomTaps8 hfilt)
    {
        Span<int> ab = stackalloc int[WienerWin * WienerWin];
        Span<short> a = stackalloc short[WienerWin], b = stackalloc short[WienerWin];
        long P = 0, Q = 0;
        int planeOff = (WienerWin - wienerWin) >> 1, wienerWin2 = wienerWin * wienerWin;
        a[WienerHalfwin] = b[WienerHalfwin] = WienerFiltStep;
        for (int i = 0; i < WienerHalfwin; ++i)
        {
            a[i] = a[WienerWin - i - 1] = vfilt[i];
            b[i] = b[WienerWin - i - 1] = hfilt[i];
            a[WienerHalfwin] -= (short)(2 * a[i]);
            b[WienerHalfwin] -= (short)(2 * b[i]);
        }
        ab.Clear();
        for (int k = 0; k < wienerWin; ++k)
            for (int l = 0; l < wienerWin; ++l) ab[k * wienerWin + l] = a[l + planeOff] * b[k + planeOff];
        unchecked
        {
            for (int k = 0; k < wienerWin2; ++k)
            {
                P += ab[k] * M[k] / WienerFiltStep / WienerFiltStep;
                for (int l = 0; l < wienerWin2; ++l)
                    Q += ab[k] * H[k * wienerWin2 + l] * ab[l] / WienerFiltStep / WienerFiltStep / WienerFiltStep / WienerFiltStep;
            }
            long score = Q - 2 * P;
            long iP = M[wienerWin2 >> 1];
            long iQ = H[(wienerWin2 >> 1) * wienerWin2 + (wienerWin2 >> 1)];
            long iScore = iQ - 2 * iP;
            return score - iScore;
        }
    }

    /// <summary>finalize_sym_filter: taps rounded to WIENER_FILT_STEP units, clipped, mirrored, centre implied.</summary>
    public static void FinalizeSymFilter(int wienerWin, int[] f, ref AomTaps8 fi)
    {
        int halfwin = wienerWin >> 1;
        for (int i = 0; i < halfwin; ++i)
        {
            long dividend = (long)f[i] * WienerFiltStep;
            const long divisor = WienerTapScaleFactor;
            fi[i] = (short)(dividend < 0 ? (dividend - divisor / 2) / divisor : (dividend + divisor / 2) / divisor);
        }
        if (wienerWin == WienerWin)
        {
            fi[0] = (short)Math.Clamp((int)fi[0], WienerFiltTap0Minv, WienerFiltTap0Maxv);
            fi[1] = (short)Math.Clamp((int)fi[1], WienerFiltTap1Minv, WienerFiltTap1Maxv);
            fi[2] = (short)Math.Clamp((int)fi[2], WienerFiltTap2Minv, WienerFiltTap2Maxv);
        }
        else
        {
            fi[2] = (short)Math.Clamp((int)fi[1], WienerFiltTap2Minv, WienerFiltTap2Maxv);
            fi[1] = (short)Math.Clamp((int)fi[0], WienerFiltTap1Minv, WienerFiltTap1Maxv);
            fi[0] = 0;
        }
        fi[WienerWin - 1] = fi[0];
        fi[WienerWin - 2] = fi[1];
        fi[WienerWin - 3] = fi[2];
        // The central element has an implicit +WIENER_FILT_STEP
        fi[3] = (short)(-2 * (fi[0] + fi[1] + fi[2]));
    }

    // ---- the per-unit visitors --------------------------------------------------------------------------------------

    private long TryRestorationUnit(in AomRestorationTileLimits limits, in AomRestorationUnitInfo rui)
    {
        bool isUv = _plane > 0;
        FilterUnit(limits, rui, _rst[_plane].Boundaries, _planeW, _planeH, isUv ? _dgd.SsX : 0, isUv ? _dgd.SsY : 0,
            _dgdPlane, _dstPlane, _sc);
        return AomSse.SsePart(_srcPlane, _dstPlane, limits.HStart, limits.HEnd - limits.HStart, limits.VStart,
            limits.VEnd - limits.VStart);
    }

    private void SearchNorestore(in AomRestorationTileLimits limits)
    {
        _sse[RestoreNone] = AomSse.SsePart(_srcPlane, _dgdPlane, limits.HStart, limits.HEnd - limits.HStart,
            limits.VStart, limits.VEnd - limits.VStart);
        TotalSse[RestoreNone] += _sse[RestoreNone];
    }

    private long FinerSearchWiener(in AomRestorationTileLimits limits, ref AomRestorationUnitInfo rui, int wienerWin)
    {
        int planeOff = (WienerWin - wienerWin) >> 1;
        long err = TryRestorationUnit(limits, rui);
        if (_cfg.DisableWienerCoeffRefineSearch) return err;
        ReadOnlySpan<int> tapMin = stackalloc int[] { WienerFiltTap0Minv, WienerFiltTap1Minv, WienerFiltTap2Minv };
        ReadOnlySpan<int> tapMax = stackalloc int[] { WienerFiltTap0Maxv, WienerFiltTap1Maxv, WienerFiltTap2Maxv };
        const int startStep = 4;
        for (int s = startStep; s >= 1; s >>= 1)
        {
            for (int dir = 0; dir < 2; dir++)   // hfilter, then vfilter
                for (int p = planeOff; p < WienerHalfwin; ++p)
                {
                    bool skip = false;
                    while (true)
                    {
                        ref AomTaps8 f = ref dir == 0 ? ref rui.Wiener.H : ref rui.Wiener.V;
                        if (f[p] - s >= tapMin[p])
                        {
                            f[p] -= (short)s;
                            f[WienerWin - p - 1] -= (short)s;
                            f[WienerHalfwin] += (short)(2 * s);
                            long err2 = TryRestorationUnit(limits, rui);
                            f = ref dir == 0 ? ref rui.Wiener.H : ref rui.Wiener.V;
                            if (err2 > err)
                            {
                                f[p] += (short)s;
                                f[WienerWin - p - 1] += (short)s;
                                f[WienerHalfwin] -= (short)(2 * s);
                            }
                            else
                            {
                                err = err2;
                                skip = true;
                                // At the highest step size continue moving in the same direction
                                if (s == startStep) continue;
                            }
                        }
                        break;
                    }
                    if (skip) break;
                    while (true)
                    {
                        ref AomTaps8 f = ref dir == 0 ? ref rui.Wiener.H : ref rui.Wiener.V;
                        if (f[p] + s <= tapMax[p])
                        {
                            f[p] += (short)s;
                            f[WienerWin - p - 1] += (short)s;
                            f[WienerHalfwin] -= (short)(2 * s);
                            long err2 = TryRestorationUnit(limits, rui);
                            f = ref dir == 0 ? ref rui.Wiener.H : ref rui.Wiener.V;
                            if (err2 > err)
                            {
                                f[p] -= (short)s;
                                f[WienerWin - p - 1] -= (short)s;
                                f[WienerHalfwin] += (short)(2 * s);
                            }
                            else
                            {
                                err = err2;
                                if (s == startStep) continue;
                            }
                        }
                        break;
                    }
                }
        }
        return err;
    }

    private void SearchWiener(in AomRestorationTileLimits limits, int unitIdx)
    {
        ref AomRestUnitSearchInfo rusi = ref _rusi[_plane][unitIdx];
        long bitsNone = _cfg.WienerRestoreCost[0];
        // Skip Wiener search for low variance contents
        if (_cfg.PruneWienerBasedOnSrcVar != 0)
        {
            ReadOnlySpan<int> scale = stackalloc int[] { 0, 1, 2 };
            int qs = AomComp.DcQuantQtx(_cfg.BaseQindex, 0, _cfg.BitDepth) >> 3;
            ulong thresh = (ulong)((qs * qs * scale[_cfg.PruneWienerBasedOnSrcVar]) >> 4);
            ulong srcVar = AomSse.VarPart(_srcPlane, limits.HStart, limits.HEnd - limits.HStart, limits.VStart,
                limits.VEnd - limits.VStart);
            bool pruneWiener = srcVar < thresh || _sse[RestoreNone] == 0;
            if (pruneWiener)
            {
                TotalBits[RestoreWiener] += bitsNone;
                TotalSse[RestoreWiener] += _sse[RestoreNone];
                rusi.SetBestRtype(RestoreWiener, RestoreNone);
                _sse[RestoreWiener] = long.MaxValue;
                if (_cfg.PruneSgrBasedOnWiener == 2) _skipSgrEval = true;
                return;
            }
        }

        int wienerWin = _plane == 0 ? WienerWin : WienerWinChroma;
        int reducedWienerWin = wienerWin;
        if (_cfg.ReduceWienerWindowSize != 0) reducedWienerWin = _plane == 0 ? WienerWinReduced : WienerWinChroma;

        ComputeStats(reducedWienerWin, _dgdPlane, _srcPlane, limits.HStart, limits.HEnd, limits.VStart, limits.VEnd, _m,
            _h, _cfg.UseDownsampledWienerStats);
        var vfilter = new int[WienerWin];
        var hfilter = new int[WienerWin];
        WienerDecomposeSepSym(reducedWienerWin, _m, _h, vfilter, hfilter);

        var rui = new AomRestorationUnitInfo { Type = RestoreWiener };
        FinalizeSymFilter(reducedWienerWin, vfilter, ref rui.Wiener.V);
        FinalizeSymFilter(reducedWienerWin, hfilter, ref rui.Wiener.H);

        // If there is no reduction in the function, the filter is reverted back to identity
        if (ComputeScore(reducedWienerWin, _m, _h, rui.Wiener.V, rui.Wiener.H) > 0)
        {
            TotalBits[RestoreWiener] += bitsNone;
            TotalSse[RestoreWiener] += _sse[RestoreNone];
            rusi.SetBestRtype(RestoreWiener, RestoreNone);
            _sse[RestoreWiener] = long.MaxValue;
            if (_cfg.PruneSgrBasedOnWiener == 2) _skipSgrEval = true;
            return;
        }

        _sse[RestoreWiener] = FinerSearchWiener(limits, ref rui, reducedWienerWin);
        rusi.Wiener = rui.Wiener;

        long bitsWiener = _cfg.WienerRestoreCost[1]
                          + ((long)CountWienerBits(wienerWin, rusi.Wiener, _refWiener) << AvProbCostShift);
        double costNone = RdcostDbl(bitsNone >> 4, _sse[RestoreNone]);
        double costWiener = RdcostDbl(bitsWiener >> 4, _sse[RestoreWiener]);
        int rtype = costWiener < costNone ? RestoreWiener : RestoreNone;
        rusi.SetBestRtype(RestoreWiener, rtype);

        // Set 'skip_sgr_eval' based on rdcost ratio of RESTORE_WIENER and RESTORE_NONE or based on best_rtype
        if (_cfg.PruneSgrBasedOnWiener == 1) _skipSgrEval = costWiener > 1.01 * costNone;
        else if (_cfg.PruneSgrBasedOnWiener == 2) _skipSgrEval = rtype == RestoreNone;

        TotalSse[RestoreWiener] += _sse[rtype];
        TotalBits[RestoreWiener] += costWiener < costNone ? bitsWiener : bitsNone;
        if (costWiener < costNone) _refWiener = rusi.Wiener;
    }

    private void SearchSgrproj(in AomRestorationTileLimits limits, int unitIdx)
    {
        ref AomRestUnitSearchInfo rusi = ref _rusi[_plane][unitIdx];
        long bitsNone = _cfg.SgrprojRestoreCost[0];
        if (_skipSgrEval)
        {
            TotalBits[RestoreSgrproj] += bitsNone;
            TotalSse[RestoreSgrproj] += _sse[RestoreNone];
            rusi.SetBestRtype(RestoreSgrproj, RestoreNone);
            _sse[RestoreSgrproj] = long.MaxValue;
            return;
        }
        bool isUv = _plane > 0;
        int ssX = isUv ? _dgd.SsX : 0, ssY = isUv ? _dgd.SsY : 0;
        rusi.Sgrproj = SearchSelfguided(_dgdPlane.Buf, _dgdPlane.At(limits.HStart, limits.VStart),
            limits.HEnd - limits.HStart, limits.VEnd - limits.VStart, _dgdPlane.Stride, _srcPlane.Buf,
            _srcPlane.At(limits.HStart, limits.VStart), _srcPlane.Stride, RestorationProcUnitSize >> ssX,
            RestorationProcUnitSize >> ssY, _rstBuf0, _rstBuf1, _cfg.EnableSgrEpPruning, _sc.Sgr);

        var rui = new AomRestorationUnitInfo { Type = RestoreSgrproj, Sgrproj = rusi.Sgrproj };
        _sse[RestoreSgrproj] = TryRestorationUnit(limits, rui);

        long bitsSgr = _cfg.SgrprojRestoreCost[1] + ((long)CountSgrprojBits(rusi.Sgrproj, _refSgrproj) << AvProbCostShift);
        double costNone = RdcostDbl(bitsNone >> 4, _sse[RestoreNone]);
        double costSgr = RdcostDbl(bitsSgr >> 4, _sse[RestoreSgrproj]);
        if (rusi.Sgrproj.Ep < 10) costSgr *= 1 + DualSgrPenaltyMult * _cfg.DualSgrPenaltyLevel;

        int rtype = costSgr < costNone ? RestoreSgrproj : RestoreNone;
        rusi.SetBestRtype(RestoreSgrproj, rtype);
        TotalSse[RestoreSgrproj] += _sse[rtype];
        TotalBits[RestoreSgrproj] += costSgr < costNone ? bitsSgr : bitsNone;
        if (costSgr < costNone) _refSgrproj = rusi.Sgrproj;
    }

    private void SearchSwitchable(int unitIdx)
    {
        ref AomRestUnitSearchInfo rusi = ref _rusi[_plane][unitIdx];
        int wienerWin = _plane == 0 ? WienerWin : WienerWinChroma;
        double bestCost = 0;
        long bestBits = 0;
        int bestRtype = RestoreNone;
        for (int r = 0; r < RestoreSwitchableTypes; ++r)
        {
            // prune based on SSE, rather than on whether or not the previous search function selected this mode
            if (r > RestoreNone && _sse[r] > _sse[RestoreNone]) continue;
            long sse = _sse[r];
            long coeffPcost = r switch
            {
                RestoreWiener => CountWienerBits(wienerWin, rusi.Wiener, _switchableRefWiener),
                RestoreSgrproj => CountSgrprojBits(rusi.Sgrproj, _switchableRefSgrproj),
                _ => 0,
            };
            long coeffBits = coeffPcost << AvProbCostShift;
            long bits = _cfg.SwitchableRestoreCost[r] + coeffBits;
            double cost = RdcostDbl(bits >> 4, sse);
            if (r == RestoreSgrproj && rusi.Sgrproj.Ep < 10) cost *= 1 + DualSgrPenaltyMult * _cfg.DualSgrPenaltyLevel;
            if (r == RestoreWiener || r == RestoreSgrproj) cost *= 1 + WienerSgrPenaltyMult * _cfg.SwitchableLrWithBiasLevel;
            if (r == 0 || cost < bestCost)
            {
                bestCost = cost;
                bestBits = bits;
                bestRtype = r;
            }
        }
        rusi.SetBestRtype(RestoreSwitchable, bestRtype);
        TotalSse[RestoreSwitchable] += _sse[bestRtype];
        TotalBits[RestoreSwitchable] += bestBits;
        if (bestRtype == RestoreWiener) _switchableRefWiener = rusi.Wiener;
        if (bestRtype == RestoreSgrproj) _switchableRefSgrproj = rusi.Sgrproj;
    }

    private void RscOnTile()
    {
        SetDefaultWiener(ref _refWiener);
        SetDefaultSgrproj(ref _refSgrproj);
        SetDefaultWiener(ref _switchableRefWiener);
        SetDefaultSgrproj(ref _switchableRefSgrproj);
    }

    private static void DeriveFlags(AomRstPickConfig cfg, Span<bool> disable)
    {
        disable[RestoreNone] = cfg.DisableWienerFilter && cfg.DisableSgrFilter;
        disable[RestoreWiener] = cfg.DisableWienerFilter;
        disable[RestoreSgrproj] = cfg.DisableSgrFilter;
        disable[RestoreSwitchable] = cfg.DisableWienerFilter || cfg.DisableSgrFilter;
    }

    private static void SetRestorationUnitSize(AomYv12 frame, AomRestorationInfo rsi, bool isUv, int lumaUnitSize)
    {
        GetPlaneSize(frame, isUv, out int pw, out int ph);
        rsi.RestorationUnitSize = lumaUnitSize;
        rsi.HorzUnits = LrCountUnits(lumaUnitSize, pw);
        rsi.VertUnits = LrCountUnits(lumaUnitSize, ph);
        rsi.NumRestUnits = rsi.HorzUnits * rsi.VertUnits;
    }

    /// <summary>Extends the searched planes of dgd by RESTORATION_BORDER (av1_pick_filter_restoration does this before
    /// any search).</summary>
    public void PrepareFrame()
    {
        GetPlaneRange(out int ps, out int pe);
        for (int plane = ps; plane <= pe; plane++)
        {
            GetPlaneSize(_dgd, plane > 0, out int pw, out int ph);
            ExtendFrame(_dgd.Planes[plane], pw, ph, RestorationBorder, RestorationBorder);
        }
    }

    private void GetPlaneRange(out int planeStart, out int planeEnd)
    {
        planeStart = _cfg.DisableLoopRestorationLuma != 0 ? 1 : 0;
        planeEnd = _dgd.NumPlanes == 1 || _cfg.DisableLoopRestorationChroma != 0 ? 0 : 2;
    }

    /// <summary>set_restoration_unit_size + init_rsc + restoration_search for one plane at one luma unit size: fills
    /// TotalSse / TotalBits and the plane's unit search infos.</summary>
    public void SearchPlane(int lumaUnitSize, int plane)
    {
        var rsi = _rst[plane];
        SetRestorationUnitSize(_dgd, rsi, plane > 0, lumaUnitSize);
        _plane = plane;
        bool isUv = plane > 0;
        GetPlaneSize(_dgd, isUv, out _planeW, out _planeH);
        _srcPlane = _src.Planes[plane];
        _dgdPlane = _dgd.Planes[plane];
        _dstPlane = _trial.Planes[plane];
        Span<bool> disable = stackalloc bool[RestoreTypes];
        DeriveFlags(_cfg, disable);

        int ssX = isUv ? _dgd.SsX : 0, ssY = isUv ? _dgd.SsY : 0;
        int ruSize = rsi.RestorationUnitSize, extSize = ruSize * 3 / 2;
        int numRtypes = rsi.NumRestUnits > 1 ? RestoreTypes : RestoreSwitchableTypes;
        Array.Clear(TotalSse);
        Array.Clear(TotalBits);

        int sbRows = (_miRows + (1 << _mibSizeLog2) - 1) >> _mibSizeLog2;
        int sbCols = (_miCols + (1 << _mibSizeLog2) - 1) >> _mibSizeLog2;
        int[] rowStart = _cfg.TileRowStartSb ?? new[] { 0, sbRows };
        int[] colStart = _cfg.TileColStartSb ?? new[] { 0, sbCols };
        int sbMi = MiSizeWide[_cfg.SbSize];
        for (int tileRow = 0; tileRow < rowStart.Length - 1; tileRow++)
            for (int tileCol = 0; tileCol < colStart.Length - 1; tileCol++)
            {
                // Reset reference parameters for delta-coding at the start of each tile
                RscOnTile();
                for (int sbRow = rowStart[tileRow]; sbRow < rowStart[tileRow + 1]; sbRow++)
                {
                    int miRow = sbRow << _mibSizeLog2;
                    for (int sbCol = colStart[tileCol]; sbCol < colStart[tileCol + 1]; sbCol++)
                    {
                        int miCol = sbCol << _mibSizeLog2;
                        if (!CornersInSb(rsi, isUv, _dgd.SsX, _dgd.SsY, miRow, miCol, sbMi, out int rcol0, out int rcol1,
                                out int rrow0, out int rrow1))
                            continue;
                        for (int rrow = rrow0; rrow < rrow1; rrow++)
                        {
                            int y0 = rrow * ruSize;
                            int remainingH = _planeH - y0;
                            int h = remainingH < extSize ? remainingH : ruSize;
                            var limits = new AomRestorationTileLimits { VStart = y0, VEnd = y0 + h };
                            int voffset = RestorationUnitOffset >> ssY;
                            limits.VStart = Math.Max(0, limits.VStart - voffset);
                            if (limits.VEnd < _planeH) limits.VEnd -= voffset;
                            for (int rcol = rcol0; rcol < rcol1; rcol++)
                            {
                                int x0 = rcol * ruSize;
                                int remainingW = _planeW - x0;
                                int w = remainingW < extSize ? remainingW : ruSize;
                                limits.HStart = x0;
                                limits.HEnd = x0 + w;
                                int unitIdx = rrow * rsi.HorzUnits + rcol;
                                _skipSgrEval = false;
                                for (int r = RestoreNone; r < numRtypes; r++)
                                {
                                    if (disable[r]) continue;
                                    switch (r)
                                    {
                                        case RestoreNone: SearchNorestore(limits); break;
                                        case RestoreWiener: SearchWiener(limits, unitIdx); break;
                                        case RestoreSgrproj: SearchSgrproj(limits, unitIdx); break;
                                        default: SearchSwitchable(unitIdx); break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        _ = ssX;
    }

    private static void CopyUnitInfo(int frameRtype, in AomRestUnitSearchInfo rusi, ref AomRestorationUnitInfo rui)
    {
        rui.Type = rusi.GetBestRtype(frameRtype);
        if (rui.Type == RestoreWiener) rui.Wiener = rusi.Wiener;
        else rui.Sgrproj = rusi.Sgrproj;
    }

    /// <summary>av1_pick_filter_restoration: fills rst[plane] (frame type, unit size, unit parameters). The search reads
    /// dgd (its border is extended) and never changes its visible samples.</summary>
    public static void PickFilterRestoration(AomYv12 src, AomYv12 dgd, AomRestorationInfo[] rst, AomRstPickConfig cfg)
    {
        var pick = new AomPickRst(src, dgd, rst, cfg);
        pick.Pick();
    }

    public void Pick()
    {
        int numPlanes = _dgd.NumPlanes;
        int minLrUnitSize = Math.Max(_cfg.MinLrUnitSize, BlockSizeWide[_cfg.SbSize]);
        int maxLrUnitSize = Math.Max(minLrUnitSize, _cfg.MaxLrUnitSize);
        for (int plane = 0; plane < numPlanes; plane++)
        {
            GetPlaneSize(_dgd, plane > 0, out int pw, out int ph);
            int maxUnits = LrCountUnits(minLrUnitSize, pw) * LrCountUnits(minLrUnitSize, ph);
            if (_rst[plane].UnitInfo.Length < maxUnits) _rst[plane].UnitInfo = new AomRestorationUnitInfo[maxUnits];
            _rst[plane].FrameRestorationType = RestoreNone;
        }
        GetPlaneRange(out int planeStart, out int planeEnd);
        Span<bool> disable = stackalloc bool[RestoreTypes];
        DeriveFlags(_cfg, disable);
        PrepareFrame();

        double bestCost = double.MaxValue;
        int bestLumaUnitSize = maxLrUnitSize;
        Span<int> bestRtype = stackalloc int[3];
        for (int lumaUnitSize = maxLrUnitSize; lumaUnitSize >= minLrUnitSize; lumaUnitSize >>= 1)
        {
            long bitsThisSize = 0, sseThisSize = 0;
            bestRtype.Clear();
            for (int plane = planeStart; plane <= planeEnd; ++plane)
            {
                SearchPlane(lumaUnitSize, plane);
                int numRtypes = _rst[plane].NumRestUnits > 1 ? RestoreTypes : RestoreSwitchableTypes;
                double bestCostThisPlane = double.MaxValue;
                for (int r = 0; r < numRtypes; ++r)
                {
                    if (disable[r]) continue;
                    // Restrict loop restoration search to RESTORE_SWITCHABLE by skipping WIENER and SGRPROJ.
                    if (_cfg.SwitchableLrWithBiasLevel > 0 && (r == RestoreWiener || r == RestoreSgrproj)) continue;
                    double costThisPlane = RdcostDbl(TotalBits[r] >> 4, TotalSse[r]);
                    if (costThisPlane < bestCostThisPlane)
                    {
                        bestCostThisPlane = costThisPlane;
                        bestRtype[plane] = r;
                    }
                }
                bitsThisSize += TotalBits[bestRtype[plane]];
                sseThisSize += TotalSse[bestRtype[plane]];
            }
            double costThisSize = RdcostDbl(bitsThisSize >> 4, sseThisSize);
            if (costThisSize < bestCost)
            {
                bestCost = costThisSize;
                bestLumaUnitSize = lumaUnitSize;
                bool allNone = true;
                for (int plane = planeStart; plane <= planeEnd; ++plane)
                {
                    _rst[plane].FrameRestorationType = bestRtype[plane];
                    if (bestRtype[plane] != RestoreNone)
                    {
                        allNone = false;
                        int n = _rst[plane].NumRestUnits;
                        for (int u = 0; u < n; ++u)
                            CopyUnitInfo(bestRtype[plane], _rusi[plane][u], ref _rst[plane].UnitInfo[u]);
                    }
                }
                // If all best_rtype entries are RESTORE_NONE, smaller sizes are unlikely to find good filters either
                if (allNone) break;
            }
            else
            {
                // If this size is worse than the previous (larger) size, the next size down will likely be even worse
                break;
            }
        }
        for (int plane = 0; plane < numPlanes; ++plane) SetRestorationUnitSize(_dgd, _rst[plane], plane > 0, bestLumaUnitSize);
    }
}
