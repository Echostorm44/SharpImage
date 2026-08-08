// AV1 reference motion vector prediction.
// Port of dav1d refmvs.c — spatial/temporal MV candidate collection and stack construction.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Av1;

// ============================================================================
// RefMvs types
// ============================================================================

/// <summary>
/// Temporal MV block stored at 8×8 resolution for MV prediction.
/// Maps to dav1d refmvs_temporal_block (refmvs.h).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Av1RefMvsTemporalBlock
{
    public Av1MotionVector Mv;
    public byte Ref;
}

/// <summary>
/// Reference pair for a block: ref[0]=0 means intra, ref[1]=-1 means no compound.
/// Maps to dav1d refmvs_refpair union (refmvs.h).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 2)]
public struct Av1RefMvsRefPair
{
    [FieldOffset(0)] public sbyte Ref0;
    [FieldOffset(1)] public sbyte Ref1;
    [FieldOffset(0)] public ushort Pair;
}

/// <summary>
/// MV pair for compound prediction.
/// Maps to dav1d refmvs_mvpair (refmvs.h).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct Av1RefMvsMvPair
{
    [FieldOffset(0)] public Av1MotionVector Mv0;
    [FieldOffset(4)] public Av1MotionVector Mv1;
    [FieldOffset(0)] public ulong Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Av1MotionVector GetMv(int idx) => idx == 0 ? Mv0 : Mv1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMv(int idx, Av1MotionVector mv)
    {
        if (idx == 0) Mv0 = mv; else Mv1 = mv;
    }
}

/// <summary>
/// Per-4×4 spatial MV reference block.
/// Maps to dav1d refmvs_block (refmvs.h, 12 bytes packed).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Av1RefMvsBlock
{
    public Av1RefMvsMvPair Mv;
    public Av1RefMvsRefPair Ref;
    public byte Bs;  // Av1BlockSize
    public byte Mf;  // 1 = globalmv+affine, 2 = newmv
}

/// <summary>
/// MV candidate with weight for sorting.
/// Maps to dav1d refmvs_candidate (refmvs.h).
/// </summary>
public struct Av1RefMvsCandidate
{
    public Av1RefMvsMvPair Mv;
    public int Weight;
}

/// <summary>
/// Frame-level reference MV state.
/// Maps to dav1d refmvs_frame (refmvs.h).
/// </summary>
public class Av1RefMvsFrame
{
    public Av1DecoderFrameHeader? FrameHeader;
    public int Iw4, Ih4, Iw8, Ih8;
    public int SbSize;
    public bool UseRefFrameMvs;
    public byte[] SignBias = new byte[7];
    public byte[] MfmvSign = new byte[7];
    public sbyte[] PocDiff = new sbyte[7];
    public byte[] MfmvRef = new byte[3];
    public int[] MfmvRef2Cur = new int[3];
    public byte[,] MfmvRef2Ref = new byte[3, 7];
    public int MfmvCount;

    // Spatial MV storage: 35 × 2 × 2 rows (for frame threading support)
    public Av1RefMvsBlock[]?[] R = new Av1RefMvsBlock[140][];
    public int RStride;

    // Temporal MV storage
    public Av1RefMvsTemporalBlock[]? Rp;
    public Av1RefMvsTemporalBlock[]?[]? RpRef;
    public Av1RefMvsTemporalBlock[]? RpProj;
    public int RpStride;
}

/// <summary>
/// Tile-level reference MV state.
/// Maps to dav1d refmvs_tile (refmvs.h).
/// </summary>
public class Av1RefMvsTile
{
    public Av1RefMvsFrame? Rf;
    public Av1RefMvsBlock[]?[] R = new Av1RefMvsBlock[37][];
    public Av1RefMvsTemporalBlock[]? RpProj;
    public int TileColStart, TileColEnd;
    public int TileRowStart, TileRowEnd;
}

// ============================================================================
// RefMvs algorithm
// ============================================================================

/// <summary>
/// AV1 reference MV prediction.
/// Port of dav1d refmvs.c — builds the MV candidate stack used by inter prediction.
/// </summary>
public static class Av1RefMvs
{
    private const uint InvalidMv = 0x80008000;
    private const int InvalidRef2Cur = -32;

    /// <summary>Number of inter mode symbols per candidate count (dav1d n_mvs).</summary>
    public static ReadOnlySpan<byte> NCandidateMvs => new byte[] { 1, 2, 3, 4, 4, 4, 4, 4, 4 };

    // ========================================================================
    // MV projection
    // ========================================================================

    private static readonly ushort[] DivMult =
    [
           0, 16384, 8192, 5461, 4096, 3276, 2730, 2340,
        2048,  1820, 1638, 1489, 1365, 1260, 1170, 1092,
        1024,   963,  910,  862,  819,  780,  744,  712,
         682,   655,  630,  606,  585,  564,  546,  528
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Av1MotionVector MvProjection(Av1MotionVector mv, int num, int den)
    {
        int frac = num * DivMult[den];
        int y = mv.Y * frac, x = mv.X * frac;
        return new Av1MotionVector
        {
            Y = (short)Math.Clamp((y + 8192 + (y >> 31)) >> 14, -0x3fff, 0x3fff),
            X = (short)Math.Clamp((x + 8192 + (x >> 31)) >> 14, -0x3fff, 0x3fff)
        };
    }

    // ========================================================================
    // MV precision helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FixIntMvPrecision(ref Av1MotionVector mv)
    {
        mv.X = (short)((mv.X - (mv.X >> 15) + 3) & ~7);
        mv.Y = (short)((mv.Y - (mv.Y >> 15) + 3) & ~7);
    }

    /// <summary>
    /// Compute DRL context from the MV stack.
    /// Port of dav1d get_drl_context (env.h:430-437).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDrlContext(ReadOnlySpan<Av1RefMvsCandidate> mvstack, int refIdx)
    {
        if (mvstack[refIdx].Weight >= 640)
            return mvstack[refIdx + 1].Weight < 640 ? 1 : 0;
        return mvstack[refIdx + 1].Weight < 640 ? 2 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FixMvPrecision(Av1DecoderFrameHeader fh, ref Av1MotionVector mv)
    {
        if (fh.ForceIntegerMv)
        {
            FixIntMvPrecision(ref mv);
        }
        else if (!fh.Hp)
        {
            mv.X = (short)((mv.X - (mv.X >> 15)) & ~1);
            mv.Y = (short)((mv.Y - (mv.Y >> 15)) & ~1);
        }
    }

    /// <summary>
    /// Compute 2D global motion vector for a block.
    /// Port of dav1d get_gmv_2d (env.h).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Av1MotionVector GetGmv2d(in Av1WarpedMotionParams gmv,
        int bx4, int by4, int bw4, int bh4, Av1DecoderFrameHeader fh)
    {
        switch (gmv.Type)
        {
            case Av1WarpedMotionType.RotZoom:
            case Av1WarpedMotionType.Affine:
            {
                int x = bx4 * 4 + bw4 * 2 - 1;
                int y = by4 * 4 + bh4 * 2 - 1;
                int xc = (gmv.Matrix2 - (1 << 16)) * x + gmv.Matrix3 * y + gmv.Matrix0;
                int yc = (gmv.Matrix5 - (1 << 16)) * y + gmv.Matrix4 * x + gmv.Matrix1;
                int shift = 16 - (3 - (fh.Hp ? 0 : 1));
                int round = (1 << shift) >> 1;
                var res = new Av1MotionVector
                {
                    Y = (short)(ApplySign((Math.Abs(yc) + round) >> shift, yc) << (fh.Hp ? 0 : 1)),
                    X = (short)(ApplySign((Math.Abs(xc) + round) >> shift, xc) << (fh.Hp ? 0 : 1))
                };
                if (fh.ForceIntegerMv)
                    FixIntMvPrecision(ref res);
                return res;
            }
            case Av1WarpedMotionType.Translation:
            {
                var res = new Av1MotionVector
                {
                    Y = (short)(gmv.Matrix0 >> 13),
                    X = (short)(gmv.Matrix1 >> 13)
                };
                if (fh.ForceIntegerMv)
                    FixIntMvPrecision(ref res);
                return res;
            }
            default: // Identity
                return default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplySign(int v, int s) => s < 0 ? -v : v;

    // ========================================================================
    // POC difference
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPocDiff(int orderHintNBits, int poc0, int poc1)
    {
        if (orderHintNBits == 0) return 0;
        int mask = (1 << orderHintNBits) - 1;
        int diff = poc0 - poc1;
        // Sign-extend
        if ((diff & (mask >> 1)) != diff)
            diff = ((diff & mask) ^ (1 << (orderHintNBits - 1))) - (1 << (orderHintNBits - 1));
        return diff;
    }

    // ========================================================================
    // Add spatial candidate
    // ========================================================================

    private static void AddSpatialCandidate(
        Span<Av1RefMvsCandidate> mvstack, ref int cnt,
        int weight, in Av1RefMvsBlock b,
        Av1RefMvsRefPair refPair, ReadOnlySpan<Av1MotionVector> gmv,
        ref int haveNewmvMatch, ref int haveRefmvMatch)
    {
        if (b.Mv.Mv0.Raw == InvalidMv) return; // intra block, no intrabc

        if (refPair.Ref1 == -1)
        {
            // Single reference
            for (int n = 0; n < 2; n++)
            {
                sbyte candRef = n == 0 ? b.Ref.Ref0 : b.Ref.Ref1;
                if (candRef == refPair.Ref0)
                {
                    var candMv = ((b.Mf & 1) != 0 && gmv[0].Raw != InvalidMv)
                        ? gmv[0] : b.Mv.GetMv(n);

                    haveRefmvMatch = 1;
                    haveNewmvMatch |= b.Mf >> 1;

                    int last = cnt;
                    for (int m = 0; m < last; m++)
                    {
                        if (mvstack[m].Mv.Mv0.Raw == candMv.Raw)
                        {
                            mvstack[m].Weight += weight;
                            return;
                        }
                    }

                    if (last < 8)
                    {
                        mvstack[last].Mv.Mv0 = candMv;
                        mvstack[last].Weight = weight;
                        cnt = last + 1;
                    }
                    return;
                }
            }
        }
        else if (b.Ref.Pair == refPair.Pair)
        {
            // Compound reference
            var candMv0 = ((b.Mf & 1) != 0 && gmv[0].Raw != InvalidMv) ? gmv[0] : b.Mv.Mv0;
            var candMv1 = ((b.Mf & 1) != 0 && gmv[1].Raw != InvalidMv) ? gmv[1] : b.Mv.Mv1;
            ulong candMvPairRaw = (ulong)candMv0.Raw | ((ulong)candMv1.Raw << 32);

            haveRefmvMatch = 1;
            haveNewmvMatch |= b.Mf >> 1;

            int last = cnt;
            for (int n = 0; n < last; n++)
            {
                if (mvstack[n].Mv.Raw == candMvPairRaw)
                {
                    mvstack[n].Weight += weight;
                    return;
                }
            }

            if (last < 8)
            {
                mvstack[last].Mv.Mv0 = candMv0;
                mvstack[last].Mv.Mv1 = candMv1;
                mvstack[last].Weight = weight;
                cnt = last + 1;
            }
        }
    }

    // ========================================================================
    // Scan row / scan col
    // ========================================================================

    private static int ScanRow(
        Span<Av1RefMvsCandidate> mvstack, ref int cnt,
        Av1RefMvsRefPair refPair, ReadOnlySpan<Av1MotionVector> gmv,
        ReadOnlySpan<Av1RefMvsBlock> b, int bw4, int w4,
        int maxRows, int step,
        ref int haveNewmvMatch, ref int haveRefmvMatch)
    {
        var candB = b[0];
        int candBw4 = Av1Tables.BlockDimensions[candB.Bs, 0];
        int len = Math.Max(step, Math.Min(bw4, candBw4));

        if (bw4 <= candBw4)
        {
            int weight = bw4 == 1 ? 2 :
                Math.Max(2, Math.Min(2 * maxRows, Av1Tables.BlockDimensions[candB.Bs, 1]));
            AddSpatialCandidate(mvstack, ref cnt, len * weight, in candB, refPair, gmv,
                ref haveNewmvMatch, ref haveRefmvMatch);
            return weight >> 1;
        }

        for (int x = 0; ;)
        {
            AddSpatialCandidate(mvstack, ref cnt, len * 2, in b[x], refPair, gmv,
                ref haveNewmvMatch, ref haveRefmvMatch);
            x += len;
            if (x >= w4) return 1;
            candBw4 = Av1Tables.BlockDimensions[b[x].Bs, 0];
            len = Math.Max(step, candBw4);
        }
    }

    private static int ScanCol(
        Span<Av1RefMvsCandidate> mvstack, ref int cnt,
        Av1RefMvsRefPair refPair, ReadOnlySpan<Av1MotionVector> gmv,
        Av1RefMvsBlock[]?[] rows, int bh4, int h4,
        int bx4, int maxCols, int step,
        ref int haveNewmvMatch, ref int haveRefmvMatch)
    {
        if (rows[0] == null) return 0;
        var candB = rows[0]![bx4];
        int candBh4 = Av1Tables.BlockDimensions[candB.Bs, 1];
        int len = Math.Max(step, Math.Min(bh4, candBh4));

        if (bh4 <= candBh4)
        {
            int weight = bh4 == 1 ? 2 :
                Math.Max(2, Math.Min(2 * maxCols, Av1Tables.BlockDimensions[candB.Bs, 0]));
            AddSpatialCandidate(mvstack, ref cnt, len * weight, in candB, refPair, gmv,
                ref haveNewmvMatch, ref haveRefmvMatch);
            return weight >> 1;
        }

        for (int y = 0; ;)
        {
            AddSpatialCandidate(mvstack, ref cnt, len * 2, in rows[y]![bx4], refPair, gmv,
                ref haveNewmvMatch, ref haveRefmvMatch);
            y += len;
            if (y >= h4) return 1;
            candBh4 = Av1Tables.BlockDimensions[rows[y]![bx4].Bs, 1];
            len = Math.Max(step, candBh4);
        }
    }

    // ========================================================================
    // Temporal candidate
    // ========================================================================

    private static void AddTemporalCandidate(
        Av1RefMvsFrame rf,
        Span<Av1RefMvsCandidate> mvstack, ref int cnt,
        in Av1RefMvsTemporalBlock rb,
        Av1RefMvsRefPair refPair, ref int globalmvCtx, bool hasGlobalmvCtx,
        ReadOnlySpan<Av1MotionVector> tgmv)
    {
        if (rb.Mv.Raw == InvalidMv) return;

        var mv = MvProjection(rb.Mv, rf.PocDiff[refPair.Ref0 - 1], rb.Ref);
        FixMvPrecision(rf.FrameHeader!, ref mv);

        int last = cnt;
        if (refPair.Ref1 == -1)
        {
            if (hasGlobalmvCtx)
                globalmvCtx = (Math.Abs(mv.X - tgmv[0].X) | Math.Abs(mv.Y - tgmv[0].Y)) >= 16 ? 1 : 0;

            for (int n = 0; n < last; n++)
            {
                if (mvstack[n].Mv.Mv0.Raw == mv.Raw)
                {
                    mvstack[n].Weight += 2;
                    return;
                }
            }
            if (last < 8)
            {
                mvstack[last].Mv.Mv0 = mv;
                mvstack[last].Weight = 2;
                cnt = last + 1;
            }
        }
        else
        {
            var mv1 = MvProjection(rb.Mv, rf.PocDiff[refPair.Ref1 - 1], rb.Ref);
            FixMvPrecision(rf.FrameHeader!, ref mv1);
            var mvp = new Av1RefMvsMvPair { Mv0 = mv, Mv1 = mv1 };

            for (int n = 0; n < last; n++)
            {
                if (mvstack[n].Mv.Raw == mvp.Raw)
                {
                    mvstack[n].Weight += 2;
                    return;
                }
            }
            if (last < 8)
            {
                mvstack[last].Mv = mvp;
                mvstack[last].Weight = 2;
                cnt = last + 1;
            }
        }
    }

    // ========================================================================
    // Extended candidates
    // ========================================================================

    private static void AddCompoundExtendedCandidate(
        Span<Av1RefMvsCandidate> same, Span<int> sameCount,
        in Av1RefMvsBlock candB, int sign0, int sign1,
        Av1RefMvsRefPair refPair, ReadOnlySpan<byte> signBias)
    {
        var diff = same.Slice(2);
        var diffCount = sameCount.Slice(2);

        for (int n = 0; n < 2; n++)
        {
            sbyte candRef = n == 0 ? candB.Ref.Ref0 : candB.Ref.Ref1;
            if (candRef <= 0) break;

            var candMv = candB.Mv.GetMv(n);
            if (candRef == refPair.Ref0)
            {
                if (sameCount[0] < 2)
                    same[sameCount[0]++].Mv.Mv0 = candMv;
                if (diffCount[1] < 2)
                {
                    if ((sign1 ^ signBias[candRef - 1]) != 0)
                        candMv = new Av1MotionVector { Y = (short)-candMv.Y, X = (short)-candMv.X };
                    diff[diffCount[1]++].Mv.Mv1 = candMv;
                }
            }
            else if (candRef == refPair.Ref1)
            {
                if (sameCount[1] < 2)
                    same[sameCount[1]++].Mv.Mv1 = candMv;
                if (diffCount[0] < 2)
                {
                    if ((sign0 ^ signBias[candRef - 1]) != 0)
                        candMv = new Av1MotionVector { Y = (short)-candMv.Y, X = (short)-candMv.X };
                    diff[diffCount[0]++].Mv.Mv0 = candMv;
                }
            }
            else
            {
                var iCandMv = new Av1MotionVector { X = (short)-candMv.X, Y = (short)-candMv.Y };
                if (diffCount[0] < 2)
                    diff[diffCount[0]++].Mv.Mv0 = (sign0 ^ signBias[candRef - 1]) != 0 ? iCandMv : candMv;
                if (diffCount[1] < 2)
                    diff[diffCount[1]++].Mv.Mv1 = (sign1 ^ signBias[candRef - 1]) != 0 ? iCandMv : candMv;
            }
        }
    }

    private static void AddSingleExtendedCandidate(
        Span<Av1RefMvsCandidate> mvstack, ref int cnt,
        in Av1RefMvsBlock candB, int sign, ReadOnlySpan<byte> signBias)
    {
        for (int n = 0; n < 2; n++)
        {
            sbyte candRef = n == 0 ? candB.Ref.Ref0 : candB.Ref.Ref1;
            if (candRef <= 0) break;

            var candMv = candB.Mv.GetMv(n);
            if ((sign ^ signBias[candRef - 1]) != 0)
                candMv = new Av1MotionVector { Y = (short)-candMv.Y, X = (short)-candMv.X };

            int last = cnt;
            int m;
            for (m = 0; m < last; m++)
            {
                if (candMv.Raw == mvstack[m].Mv.Mv0.Raw)
                    break;
            }
            if (m == last)
            {
                mvstack[m].Mv.Mv0 = candMv;
                mvstack[m].Weight = 2;
                cnt = last + 1;
            }
        }
    }

    // ========================================================================
    // Main: FindRefMvs
    // ========================================================================

    /// <summary>
    /// Build the MV candidate stack for a block.
    /// Port of dav1d dav1d_refmvs_find (refmvs.c lines 348-651).
    /// </summary>
    /// <param name="rt">Tile-level MV state.</param>
    /// <param name="mvstack">Output: up to 8 MV candidates.</param>
    /// <param name="cnt">Output: number of candidates.</param>
    /// <param name="ctx">Output: combined context (refmv|globalmv|newmv).</param>
    /// <param name="refPair">Reference pair being searched for.</param>
    /// <param name="bs">Block size.</param>
    /// <param name="edgeFlags">Edge availability flags.</param>
    /// <param name="by4">Block Y position in 4×4 units.</param>
    /// <param name="bx4">Block X position in 4×4 units.</param>
    public static void FindRefMvs(
        Av1RefMvsTile rt,
        Span<Av1RefMvsCandidate> mvstack, out int cnt, out int ctx,
        out int refmvsCtx,
        Av1RefMvsRefPair refPair, int bs, Av1EdgeFlags edgeFlags,
        int by4, int bx4)
    {
        var rf = rt.Rf!;
        int bw4 = Av1Tables.BlockDimensions[bs, 0];
        int w4 = Math.Min(Math.Min(bw4, 16), rt.TileColEnd - bx4);
        int bh4 = Av1Tables.BlockDimensions[bs, 1];
        int h4 = Math.Min(Math.Min(bh4, 16), rt.TileRowEnd - by4);

        Span<Av1MotionVector> gmv = stackalloc Av1MotionVector[2];
        Span<Av1MotionVector> tgmv = stackalloc Av1MotionVector[2];

        cnt = 0;
        var fh = rf.FrameHeader!;

        if (refPair.Ref0 > 0)
        {
            tgmv[0] = GetGmv2d(in fh.Gmv[refPair.Ref0 - 1], bx4, by4, bw4, bh4, fh);
            gmv[0] = fh.Gmv[refPair.Ref0 - 1].Type > Av1WarpedMotionType.Translation
                ? tgmv[0] : new Av1MotionVector { Raw = InvalidMv };
            if (bx4 == 0 && by4 == 0)
                AvDbg.W($"[GMV-DBG] bx={bx4} by={by4} bw4={bw4} bh4={bh4} ref0={refPair.Ref0-1} gmvType={fh.Gmv[refPair.Ref0-1].Type} tgmv0=({tgmv[0].Y},{tgmv[0].X}) gmv0=({gmv[0].Y},{gmv[0].X})");
        }
        else
        {
            tgmv[0] = default;
            gmv[0] = new Av1MotionVector { Raw = InvalidMv };
        }

        if (refPair.Ref1 > 0)
        {
            tgmv[1] = GetGmv2d(in fh.Gmv[refPair.Ref1 - 1], bx4, by4, bw4, bh4, fh);
            gmv[1] = fh.Gmv[refPair.Ref1 - 1].Type > Av1WarpedMotionType.Translation
                ? tgmv[1] : new Av1MotionVector { Raw = InvalidMv };
        }

        // === Top row scan ===
        int haveNewmv = 0, haveColMvs = 0, haveRowMvs = 0;
        int maxRows = 0;
        int nRows = -1; // ~0U sentinel → -1 for "not scanned"
        ReadOnlySpan<Av1RefMvsBlock> bTop = default;

        if (by4 > rt.TileRowStart)
        {
            maxRows = Math.Min((by4 - rt.TileRowStart + 1) >> 1, 2 + (bh4 > 1 ? 1 : 0));
            var topRow = rt.R[(by4 & 31) - 1 + 5];
            if (topRow != null)
            {
                bTop = topRow.AsSpan(bx4);
                if (bx4 == 8 && by4 == 5)
                    AvDbg.W($"[OUR-TOPSCAN] b[0]: bs={bTop[0].Bs} ref=({bTop[0].Ref.Ref0},{bTop[0].Ref.Ref1}) mv0=({bTop[0].Mv.Mv0.Y},{bTop[0].Mv.Mv0.X}) raw={bTop[0].Mv.Mv0.Raw:X8} b[1]: bs={bTop[1].Bs} mv0=({bTop[1].Mv.Mv0.Y},{bTop[1].Mv.Mv0.X})");
                nRows = ScanRow(mvstack, ref cnt, refPair, gmv, bTop,
                    bw4, w4, maxRows, bw4 >= 16 ? 4 : 1,
                    ref haveNewmv, ref haveRowMvs);
            }
        }

        // === Left column scan ===
        int maxCols = 0;
        int nCols = -1;
        // For ScanCol, we need an array of row pointers starting at (by4 & 31) + 5
        Av1RefMvsBlock[]?[]? bLeft = null;

        if (bx4 > rt.TileColStart)
        {
            maxCols = Math.Min((bx4 - rt.TileColStart + 1) >> 1, 2 + (bw4 > 1 ? 1 : 0));
            int leftRowBase = (by4 & 31) + 5;
            bLeft = new Av1RefMvsBlock[h4][];
            for (int i = 0; i < h4; i++)
                bLeft[i] = rt.R[leftRowBase + i];
            if (bx4 == 8 && by4 == 8)
            {
                AvDbg.W($"[OUR-LSCAN] h4={h4} maxCols={maxCols} step={(bh4 >= 16 ? 4 : 1)} row13null={bLeft[0] == null} row14null={(h4 > 1 && bLeft[1] == null)}");
                for (int i = 0; i < h4; i++)
                    if (bLeft[i] != null)
                    {
                        var bb = bLeft[i]![7];
                        AvDbg.W($"  bLeft[{i}][7]: bs={bb.Bs} ref=({bb.Ref.Ref0},{bb.Ref.Ref1}) mv=({bb.Mv.Mv0.Y},{bb.Mv.Mv0.X}) raw={bb.Mv.Mv0.Raw:X8}");
                    }
            }
            nCols = ScanCol(mvstack, ref cnt, refPair, gmv, bLeft,
                bh4, h4, bx4 - 1, maxCols, bh4 >= 16 ? 4 : 1,
                ref haveNewmv, ref haveColMvs);
        }

        // === Top-right ===
        if (nRows >= 0 && (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 &&
            Math.Max(bw4, bh4) <= 16 && bw4 + bx4 < rt.TileColEnd)
        {
            var topRow = rt.R[(by4 & 31) - 1 + 5];
            if (topRow != null)
            {
                AddSpatialCandidate(mvstack, ref cnt, 4, in topRow[bx4 + bw4], refPair, gmv,
                    ref haveNewmv, ref haveRowMvs);
            }
        }

        int nearestMatch = haveColMvs + haveRowMvs;
        int nearestCnt = cnt;
        for (int n = 0; n < nearestCnt; n++)
            mvstack[n].Weight += 640;

        // === Temporal candidates ===
        int globalmvCtx = fh.UseRefFrameMvs ? 1 : 0;
        if (rf.UseRefFrameMvs && rt.RpProj != null)
        {
            int stride = rf.RpStride;
            int by8 = by4 >> 1, bx8 = bx4 >> 1;
            int rpBase = (by8 & 15) * stride + bx8;
            int stepH = bw4 >= 16 ? 2 : 1, stepV = bh4 >= 16 ? 2 : 1;
            int w8 = Math.Min((w4 + 1) >> 1, 8), h8 = Math.Min((h4 + 1) >> 1, 8);

            for (int y = 0; y < h8; y += stepV)
            {
                for (int x = 0; x < w8; x += stepH)
                {
                    bool isFirst = (x | y) == 0;
                    AddTemporalCandidate(rf, mvstack, ref cnt,
                        in rt.RpProj[rpBase + y * stride + x],
                        refPair, ref globalmvCtx, isFirst, tgmv);
                }
            }

            // Additional temporal candidates for medium blocks
            if (Math.Min(bw4, bh4) >= 2 && Math.Max(bw4, bh4) < 16)
            {
                int bh8 = bh4 >> 1, bw8 = bw4 >> 1;
                int bottomBase = rpBase + bh8 * stride;
                bool hasBottom = by8 + bh8 < Math.Min(rt.TileRowEnd >> 1, (by8 & ~7) + 8);

                if (hasBottom && bx8 - 1 >= Math.Max(rt.TileColStart >> 1, bx8 & ~7))
                {
                    AddTemporalCandidate(rf, mvstack, ref cnt,
                        in rt.RpProj[bottomBase - 1],
                        refPair, ref globalmvCtx, false, tgmv);
                }
                if (bx8 + bw8 < Math.Min(rt.TileColEnd >> 1, (bx8 & ~7) + 8))
                {
                    if (hasBottom)
                    {
                        AddTemporalCandidate(rf, mvstack, ref cnt,
                            in rt.RpProj[bottomBase + bw8],
                            refPair, ref globalmvCtx, false, tgmv);
                    }
                    if (by8 + bh8 - 1 < Math.Min(rt.TileRowEnd >> 1, (by8 & ~7) + 8))
                    {
                        AddTemporalCandidate(rf, mvstack, ref cnt,
                            in rt.RpProj[bottomBase + bw8 - stride],
                            refPair, ref globalmvCtx, false, tgmv);
                    }
                }
            }
        }

        // === Top-left ===
        int haveDummyNewmv = 0;
        if (nRows >= 0 || nCols >= 0)
        {
            var topRow = rt.R[(by4 & 31) - 1 + 5];
            if (topRow != null && bx4 - 1 >= 0)
            {
                AddSpatialCandidate(mvstack, ref cnt, 4, in topRow[bx4 - 1], refPair, gmv,
                    ref haveDummyNewmv, ref haveRowMvs);
            }
        }

        // === Secondary top & left edges (8×8 resolution) ===
        for (int n = 2; n <= 3; n++)
        {
            if (n > (nRows < 0 ? 0 : nRows) && n <= maxRows)
            {
                int rowIdx = (((by4 & 31) - 2 * n + 1) | 1) + 5;
                var secRow = rt.R[rowIdx];
                if (secRow != null)
                {
                    nRows = (nRows < 0 ? 0 : nRows) + ScanRow(mvstack, ref cnt, refPair, gmv,
                        secRow.AsSpan(bx4 | 1),
                        bw4, w4, 1 + maxRows - n, bw4 >= 16 ? 4 : 2,
                        ref haveDummyNewmv, ref haveRowMvs);
                }
            }

            if (n > (nCols < 0 ? 0 : nCols) && n <= maxCols)
            {
                int leftRowBase = ((by4 & 31) | 1) + 5;
                var secLeft = new Av1RefMvsBlock[h4][];
                for (int i = 0; i < h4; i++)
                    secLeft[i] = rt.R[leftRowBase + i];
                nCols = (nCols < 0 ? 0 : nCols) + ScanCol(mvstack, ref cnt, refPair, gmv,
                    secLeft, bh4, h4, (bx4 - n * 2 + 1) | 1,
                    1 + maxCols - n, bh4 >= 16 ? 4 : 2,
                    ref haveDummyNewmv, ref haveColMvs);
            }
        }

        int refMatchCount = haveColMvs + haveRowMvs;

        // === Context computation ===
        int refmvCtx, newmvCtx;
        switch (nearestMatch)
        {
            case 0:
                refmvCtx = Math.Min(2, refMatchCount);
                newmvCtx = refMatchCount > 0 ? 1 : 0;
                break;
            case 1:
                refmvCtx = Math.Min(refMatchCount * 3, 4);
                newmvCtx = 3 - haveNewmv;
                break;
            default: // 2
                refmvCtx = 5;
                newmvCtx = 5 - haveNewmv;
                break;
        }

        // === Sorting: nearest, then secondary ===
        BubbleSort(mvstack, 0, nearestCnt);
        BubbleSort(mvstack, nearestCnt, cnt);

        if (by4 >= 8 && by4 <= 14)
        {
            AvDbg.W($"[OUR-REFMVS] bx={bx4} by={by4} cnt={cnt} cands:");
            for (int z = 0; z < cnt; z++)
                AvDbg.W($" ({mvstack[z].Mv.Mv0.Y},{mvstack[z].Mv.Mv0.X})w{mvstack[z].Weight}");
            AvDbg.W();
        }

        // Compute refmvs context for inter mode decode (dav1d: ctx from mvstack)
        // ctx = (cnt > 1 ? 8 : 0) | (have_newmv ? 2 : 0) | (nearestMatch > 0 ? 1 : 0) | (refMatchCount > 0 ? 4 : 0)
        refmvsCtx = (cnt > 1 ? 1 : 0) << 3;
        if (haveNewmv > 0) refmvsCtx |= 1 << 1;
        if (nearestMatch > 0) refmvsCtx |= 1 << 0;
        if (refMatchCount > 0) refmvsCtx |= 1 << 2;

        // === Compound extended candidates ===
        if (refPair.Ref1 > 0)
        {
            if (cnt < 2)
            {
                int sign0 = rf.SignBias[refPair.Ref0 - 1];
                int sign1 = rf.SignBias[refPair.Ref1 - 1];
                int sz4 = Math.Min(w4, h4);
                var same = mvstack.Slice(cnt);
                Span<int> sameCount = stackalloc int[4];

                if (nRows >= 0)
                {
                    var topRow = rt.R[(by4 & 31) - 1 + 5];
                    if (topRow != null)
                    {
                        for (int x = 0; x < sz4;)
                        {
                            AddCompoundExtendedCandidate(same, sameCount, in topRow[bx4 + x],
                                sign0, sign1, refPair, rf.SignBias);
                            x += Av1Tables.BlockDimensions[topRow[bx4 + x].Bs, 0];
                        }
                    }
                }

                if (nCols >= 0 && bLeft != null)
                {
                    for (int y = 0; y < sz4;)
                    {
                        AddCompoundExtendedCandidate(same, sameCount, in bLeft[y]![bx4 - 1],
                            sign0, sign1, refPair, rf.SignBias);
                        y += Av1Tables.BlockDimensions[bLeft[y]![bx4 - 1].Bs, 1];
                    }
                }

                var diff = same.Slice(2);
                var diffCount = sameCount.Slice(2);

                for (int n = 0; n < 2; n++)
                {
                    int m = sameCount[n];
                    if (m >= 2) continue;

                    int l = diffCount[n];
                    if (l > 0)
                    {
                        same[m].Mv.SetMv(n, diff[0].Mv.GetMv(n));
                        if (++m == 2) continue;
                        if (l == 2)
                        {
                            same[1].Mv.SetMv(n, diff[1].Mv.GetMv(n));
                            continue;
                        }
                    }
                    do
                    {
                        same[m].Mv.SetMv(n, tgmv[n]);
                    } while (++m < 2);
                }

                int nc = cnt;
                if (nc == 1 && mvstack[0].Mv.Raw == same[0].Mv.Raw)
                    mvstack[1].Mv = mvstack[2].Mv;
                do
                {
                    mvstack[nc].Weight = 2;
                } while (++nc < 2);
                cnt = 2;
            }

            // Clamping
            int left = -(bx4 + bw4 + 4) * 4 * 8;
            int right = (rf.Iw4 - bx4 + 4) * 4 * 8;
            int top = -(by4 + bh4 + 4) * 4 * 8;
            int bottom = (rf.Ih4 - by4 + 4) * 4 * 8;

            for (int n = 0; n < cnt; n++)
            {
                mvstack[n].Mv.Mv0.X = (short)Math.Clamp(mvstack[n].Mv.Mv0.X, left, right);
                mvstack[n].Mv.Mv0.Y = (short)Math.Clamp(mvstack[n].Mv.Mv0.Y, top, bottom);
                mvstack[n].Mv.Mv1.X = (short)Math.Clamp(mvstack[n].Mv.Mv1.X, left, right);
                mvstack[n].Mv.Mv1.Y = (short)Math.Clamp(mvstack[n].Mv.Mv1.Y, top, bottom);
            }

            ctx = (refmvCtx >> 1) switch
            {
                0 => Math.Min(newmvCtx, 1),
                1 => 1 + Math.Min(newmvCtx, 3),
                _ => Math.Clamp(3 + newmvCtx, 4, 7)
            };
            return;
        }

        // === Single reference extended candidates ===
        if (cnt < 2 && refPair.Ref0 > 0)
        {
            int sign = rf.SignBias[refPair.Ref0 - 1];
            int sz4 = Math.Min(w4, h4);

            if (nRows >= 0)
            {
                var topRow = rt.R[(by4 & 31) - 1 + 5];
                if (topRow != null)
                {
                    for (int x = 0; x < sz4 && cnt < 2;)
                    {
                        AddSingleExtendedCandidate(mvstack, ref cnt, in topRow[bx4 + x], sign, rf.SignBias);
                        x += Av1Tables.BlockDimensions[topRow[bx4 + x].Bs, 0];
                    }
                }
            }

            if (nCols >= 0 && bLeft != null)
            {
                for (int y = 0; y < sz4 && cnt < 2;)
                {
                    if (bLeft[y] == null) break;
                    AddSingleExtendedCandidate(mvstack, ref cnt, in bLeft[y]![bx4 - 1], sign, rf.SignBias);
                    y += Av1Tables.BlockDimensions[bLeft[y]![bx4 - 1].Bs, 1];
                }
            }
        }

        // Clamping
        if (cnt > 0)
        {
            int left = -(bx4 + bw4 + 4) * 4 * 8;
            int right = (rf.Iw4 - bx4 + 4) * 4 * 8;
            int top = -(by4 + bh4 + 4) * 4 * 8;
            int bottom = (rf.Ih4 - by4 + 4) * 4 * 8;

            for (int n = 0; n < cnt; n++)
            {
                mvstack[n].Mv.Mv0.X = (short)Math.Clamp(mvstack[n].Mv.Mv0.X, left, right);
                mvstack[n].Mv.Mv0.Y = (short)Math.Clamp(mvstack[n].Mv.Mv0.Y, top, bottom);
            }
        }

        for (int n = cnt; n < 2; n++)
            mvstack[n].Mv.Mv0 = tgmv[0];

        ctx = (refmvCtx << 4) | (globalmvCtx << 3) | newmvCtx;
    }

    // ========================================================================
    // Frame initialization
    // ========================================================================

    /// <summary>
    /// Initialize frame-level refmvs state.
    /// Port of dav1d dav1d_refmvs_init_frame (refmvs.c lines 799-897).
    /// </summary>
    public static void InitFrame(
        Av1RefMvsFrame rf,
        Av1DecoderSequenceHeader seqHdr,
        Av1DecoderFrameHeader fh,
        ReadOnlySpan<byte> refPoc,
        Av1RefMvsTemporalBlock[]? rp,
        byte[,]? refRefPoc,
        Av1RefMvsTemporalBlock[]?[]? rpRef)
    {
        int rpStride = ((fh.CodedWidth + 127) & ~127) >> 3;

        rf.SbSize = 16 << (seqHdr.Sb128 ? 1 : 0);
        rf.FrameHeader = fh;
        rf.Iw8 = (fh.CodedWidth + 7) >> 3;
        rf.Ih8 = (fh.Height + 7) >> 3;
        rf.Iw4 = rf.Iw8 << 1;
        rf.Ih4 = rf.Ih8 << 1;

        // Allocate temporal MV storage if not provided
        int rpSize = rpStride * fh.TileRows;
        if (rp == null || rp.Length < rpSize)
            rp = new Av1RefMvsTemporalBlock[rpSize];
        rf.Rp = rp;
        rf.RpStride = rpStride;

        // Allocate spatial MV rows if needed
        int rpStride2 = rpStride * 2;
        rf.RStride = rpStride2;
        int rRowCount = 35 * 2;
        if (rf.R[0] == null || rf.R[0]!.Length < rpStride2)
        {
            for (int i = 0; i < rRowCount; i++)
                rf.R[i] = new Av1RefMvsBlock[rpStride2];
        }

        // Allocate temporal projection buffer
        int rpProjSize = 16 * rpStride;
        if (rf.RpProj == null || rf.RpProj.Length < rpProjSize)
            rf.RpProj = new Av1RefMvsTemporalBlock[rpProjSize];

        int poc = fh.FrameOffset;
        for (int i = 0; i < 7; i++)
        {
            int pocDiff = GetPocDiff(seqHdr.OrderHintNBits, refPoc[i], poc);
            rf.SignBias[i] = (byte)(pocDiff > 0 ? 1 : 0);
            rf.MfmvSign[i] = (byte)(pocDiff < 0 ? 1 : 0);
            rf.PocDiff[i] = (sbyte)Math.Clamp(
                GetPocDiff(seqHdr.OrderHintNBits, poc, refPoc[i]), -31, 31);
        }

        // Temporal MV setup
        rf.MfmvCount = 0;
        rf.RpRef = rpRef;
        if (fh.UseRefFrameMvs && seqHdr.OrderHintNBits > 0 && rpRef != null && refRefPoc != null)
        {
            int total = 2;
            if (rpRef[0] != null && refRefPoc[0, 6] != refPoc[3])
            {
                rf.MfmvRef[rf.MfmvCount++] = 0; // last
                total = 3;
            }
            if (rpRef[4] != null && GetPocDiff(seqHdr.OrderHintNBits, refPoc[4], fh.FrameOffset) > 0)
                rf.MfmvRef[rf.MfmvCount++] = 4; // bwd
            if (rpRef[5] != null && GetPocDiff(seqHdr.OrderHintNBits, refPoc[5], fh.FrameOffset) > 0)
                rf.MfmvRef[rf.MfmvCount++] = 5; // altref2
            if (rf.MfmvCount < total && rpRef[6] != null &&
                GetPocDiff(seqHdr.OrderHintNBits, refPoc[6], fh.FrameOffset) > 0)
                rf.MfmvRef[rf.MfmvCount++] = 6; // altref
            if (rf.MfmvCount < total && rpRef[1] != null)
                rf.MfmvRef[rf.MfmvCount++] = 1; // last2

            for (int n = 0; n < rf.MfmvCount; n++)
            {
                int rpoc = refPoc[rf.MfmvRef[n]];
                int diff1 = GetPocDiff(seqHdr.OrderHintNBits, rpoc, fh.FrameOffset);
                if (Math.Abs(diff1) > 31)
                {
                    rf.MfmvRef2Cur[n] = InvalidRef2Cur;
                }
                else
                {
                    rf.MfmvRef2Cur[n] = rf.MfmvRef[n] < 4 ? -diff1 : diff1;
                    for (int m = 0; m < 7; m++)
                    {
                        int rrpoc = refRefPoc[rf.MfmvRef[n], m];
                        int diff2 = GetPocDiff(seqHdr.OrderHintNBits, rpoc, rrpoc);
                        rf.MfmvRef2Ref[n, m] = (byte)((uint)diff2 > 31 ? 0 : diff2);
                    }
                }
            }
        }
        rf.UseRefFrameMvs = rf.MfmvCount > 0;
    }

    // ========================================================================
    // Tile sbrow init
    // ========================================================================

    /// <summary>
    /// Initialize tile-level refmvs pointers for a superblock row.
    /// Port of dav1d dav1d_refmvs_tile_sbrow_init (refmvs.c lines 653-688).
    /// </summary>
    public static void TileSbRowInit(
        Av1RefMvsTile rt, Av1RefMvsFrame rf,
        int tileColStart4, int tileColEnd4,
        int tileRowStart4, int tileRowEnd4,
        int sby)
    {
        int rStride = rf.RpStride * 2;
        int sbsz = rf.SbSize;
        int off = (sbsz * sby) & 16;

        // Set up row pointers
        int rIdx = 0;
        for (int i = 0; i < sbsz; i++)
        {
            rt.R[off + 5 + i] = rf.R[rIdx];
            rIdx++;
        }
        rt.R[off + 0] = rf.R[rIdx]; rIdx++;
        rt.R[off + 1] = null;
        rt.R[off + 2] = rf.R[rIdx]; rIdx++;
        rt.R[off + 3] = null;
        rt.R[off + 4] = rf.R[rIdx];

        if ((sby & 1) != 0)
        {
            (rt.R[off + 0], rt.R[off + sbsz + 0]) = (rt.R[off + sbsz + 0], rt.R[off + 0]);
            (rt.R[off + 2], rt.R[off + sbsz + 2]) = (rt.R[off + sbsz + 2], rt.R[off + 2]);
            (rt.R[off + 4], rt.R[off + sbsz + 4]) = (rt.R[off + sbsz + 4], rt.R[off + 4]);
        }

        rt.Rf = rf;
        rt.RpProj = rf.RpProj;
        rt.TileRowStart = tileRowStart4;
        rt.TileRowEnd = Math.Min(tileRowEnd4, rf.Ih4);
        rt.TileColStart = tileColStart4;
        rt.TileColEnd = Math.Min(tileColEnd4, rf.Iw4);
    }

    // ========================================================================
    // Load / save temporal MVs
    // ========================================================================

    /// <summary>
    /// Load temporal MVs for a superblock row (project from reference frames).
    /// Port of dav1d load_tmvs_c (refmvs.c lines 690-761).
    /// </summary>
    public static void LoadTemporalMvs(
        Av1RefMvsFrame rf,
        int colStart8, int colEnd8,
        int rowStart8, int rowEnd8)
    {
        rowEnd8 = Math.Min(rowEnd8, rf.Ih8);
        int colStart8i = Math.Max(colStart8 - 8, 0);
        int colEnd8i = Math.Min(colEnd8 + 8, rf.Iw8);

        int stride = rf.RpStride;
        var rpProj = rf.RpProj!;

        // Clear projection area
        for (int y = rowStart8; y < rowEnd8; y++)
        {
            int rowBase = (y & 15) * stride;
            for (int x = colStart8; x < colEnd8; x++)
                rpProj[rowBase + x].Mv.Raw = InvalidMv;
        }

        for (int n = 0; n < rf.MfmvCount; n++)
        {
            int ref2cur = rf.MfmvRef2Cur[n];
            if (ref2cur == InvalidRef2Cur) continue;

            int refIdx = rf.MfmvRef[n];
            int refSign = refIdx - 4;
            var rpRefN = rf.RpRef![refIdx]!;

            for (int y = rowStart8; y < rowEnd8; y++)
            {
                int ySbAlign = y & ~7;
                int yProjStart = Math.Max(ySbAlign, rowStart8);
                int yProjEnd = Math.Min(ySbAlign + 8, rowEnd8);

                for (int x = colStart8i; x < colEnd8i; x++)
                {
                    var rb = rpRefN[y * stride + x];
                    int bRef = rb.Ref;
                    if (bRef == 0) continue;
                    int ref2ref = rf.MfmvRef2Ref[n, bRef - 1];
                    if (ref2ref == 0) continue;

                    var bMv = rb.Mv;
                    var offset = MvProjection(bMv, ref2cur, ref2ref);
                    int posX = x + ApplySign(Math.Abs(offset.X) >> 6, offset.X ^ refSign);
                    int posY = y + ApplySign(Math.Abs(offset.Y) >> 6, offset.Y ^ refSign);

                    if (posY >= yProjStart && posY < yProjEnd)
                    {
                        int pos = (posY & 15) * stride;
                        for (; ;)
                        {
                            int xSbAlign = x & ~7;
                            if (posX >= Math.Max(xSbAlign - 8, colStart8) &&
                                posX < Math.Min(xSbAlign + 16, colEnd8))
                            {
                                rpProj[pos + posX].Mv = rb.Mv;
                                rpProj[pos + posX].Ref = (byte)ref2ref;
                            }
                            if (++x >= colEnd8i) break;
                            var nextRb = rpRefN[y * stride + x];
                            if (nextRb.Ref != bRef || nextRb.Mv.Raw != bMv.Raw) break;
                            posX++;
                        }
                    }
                    else
                    {
                        for (; ;)
                        {
                            if (++x >= colEnd8i) break;
                            var nextRb = rpRefN[y * stride + x];
                            if (nextRb.Ref != bRef || nextRb.Mv.Raw != bMv.Raw) break;
                        }
                    }
                    x--; // counteract the loop increment
                }
            }
        }
    }

    /// <summary>
    /// Save temporal MVs from spatial blocks (downsample 4×4 → 8×8).
    /// Port of dav1d save_tmvs_c (refmvs.c lines 763-797).
    /// </summary>
    public static void SaveTemporalMvs(
        Av1RefMvsTemporalBlock[] rp, int rpOffset, int stride,
        Av1RefMvsBlock[]?[] rr,
        ReadOnlySpan<byte> refSign,
        int colEnd8, int rowEnd8, int colStart8, int rowStart8)
    {
        int rpIdx = rpOffset;
        for (int y = rowStart8; y < rowEnd8; y++)
        {
            var b = rr[6 + (y & 15) * 2]!;

            for (int x = colStart8; x < colEnd8;)
            {
                var candB = b[x * 2 + 1];
                int bw8 = (Av1Tables.BlockDimensions[candB.Bs, 0] + 1) >> 1;

                if (candB.Ref.Ref1 > 0 && refSign[candB.Ref.Ref1 - 1] != 0 &&
                    (Math.Abs(candB.Mv.Mv1.Y) | Math.Abs(candB.Mv.Mv1.X)) < 4096)
                {
                    for (int i = 0; i < bw8; i++, x++)
                    {
                        rp[rpIdx + x].Mv = candB.Mv.Mv1;
                        rp[rpIdx + x].Ref = (byte)candB.Ref.Ref1;
                    }
                }
                else if (candB.Ref.Ref0 > 0 && refSign[candB.Ref.Ref0 - 1] != 0 &&
                         (Math.Abs(candB.Mv.Mv0.Y) | Math.Abs(candB.Mv.Mv0.X)) < 4096)
                {
                    for (int i = 0; i < bw8; i++, x++)
                    {
                        rp[rpIdx + x].Mv = candB.Mv.Mv0;
                        rp[rpIdx + x].Ref = (byte)candB.Ref.Ref0;
                    }
                }
                else
                {
                    for (int i = 0; i < bw8; i++, x++)
                    {
                        rp[rpIdx + x].Mv.Raw = 0;
                        rp[rpIdx + x].Ref = 0;
                    }
                }
            }
            rpIdx += stride;
        }
    }

    // ========================================================================
    // Splat MV
    // ========================================================================

    /// <summary>
    /// Fill a block region in the spatial MV grid.
    /// Port of dav1d splat_mv_c (refmvs.c lines 900-908).
    /// </summary>
    public static void SplatMv(
        Av1RefMvsBlock[]?[] rr, int rrStartIdx,
        in Av1RefMvsBlock rmv, int bx4, int bw4, int bh4)
    {
        for (int y = 0; y < bh4; y++)
        {
            var r = rr[rrStartIdx + y];
            if (r == null) continue;
            for (int x = 0; x < bw4; x++)
                r[bx4 + x] = rmv;
        }
    }

    // ========================================================================
    // Sorting helper
    // ========================================================================

    private static void BubbleSort(Span<Av1RefMvsCandidate> stack, int start, int end)
    {
        int len = end;
        while (len > start)
        {
            int last = start;
            for (int n = start + 1; n < len; n++)
            {
                if (stack[n - 1].Weight < stack[n].Weight)
                {
                    (stack[n - 1], stack[n]) = (stack[n], stack[n - 1]);
                    last = n;
                }
            }
            len = last;
        }
    }
}
