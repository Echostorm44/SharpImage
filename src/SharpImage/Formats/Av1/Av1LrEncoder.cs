// Loop restoration (Wiener / self-guided) for the still-image encoder: a per-unit search on the post-CDEF
// reconstruction against the source, the frame-header lr_params, and the per-unit tile syntax (written into the
// replayed tile at each superblock's start, exactly where the decoder's ReadRestorationInfoForSb reads it).
//
// The search filters each unit with the decoder's own dav1d-ported kernels (Av1LoopRestoration) on a working copy
// that has the real left/right neighbours; unit tops/bottoms are treated as frame edges (the decoder uses the saved
// pre-CDEF stripe lines there), so the search SSE is a close estimate and the decoded result is authoritative.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal static class Av1LrEncoder
{
    // AV1 Wiener tap ranges / subexp k (taps 0..2; tap 3 = the implied centre), dav1d DecodeSubexp offsets.
    private static readonly int[] WMin = { -5, -23, -17 }, WMax = { 10, 8, 46 };
    private static readonly int[] WSubN = { 16, 32, 64 }, WSubK = { 1, 2, 3 };
    private const int XqdMin0 = -96, XqdMax0 = 31, XqdMin1 = -32, XqdMax1 = 95;

    /// <summary>One restoration unit's choice (Av1Decode.ReadRestorationInfo's fields).</summary>
    internal struct Unit
    {
        public Av1RestorationType Type;   // None, Wiener, or SelfGuided + set index
        public sbyte H0, H1, H2, V0, V1, V2, W0, W1;
    }

    /// <summary>The frame's restoration plan: coded lr_type per plane (0 none, 1 switchable, 2 Wiener, 3 self-guided),
    /// unit sizes, and every unit's parameters (raster order per plane).</summary>
    internal sealed class Plan
    {
        public readonly int[] FrameType = new int[3];
        public int UnitShift, UvShift;
        public readonly Unit[][] Units = new Unit[3][];
        public readonly int[] Cols = new int[3], Rows = new int[3], SizeLog2 = new int[3];
        public bool Any => FrameType[0] != 0 || FrameType[1] != 0 || FrameType[2] != 0;
        internal double Cost;
    }

    // ---- frame header ---------------------------------------------------------------------------------------------

    /// <summary>lr_params (sequence has enable_restoration = 1, 64x64 superblocks).</summary>
    internal static void WriteParams(Av1BitWriter w, Plan? plan, bool monochrome, bool i420)
    {
        int n = monochrome ? 1 : 3;
        for (int p = 0; p < n; p++) w.PutBits((uint)(plan?.FrameType[p] ?? 0), 2);
        if (plan == null || !plan.Any) return;
        w.PutBool(plan.UnitShift > 0);
        if (plan.UnitShift > 0) w.PutBool(plan.UnitShift > 1);
        if (i420 && (plan.FrameType[1] != 0 || plan.FrameType[2] != 0)) w.PutBool(plan.UvShift != 0);
    }

    // ---- tile syntax ----------------------------------------------------------------------------------------------

    /// <summary>Per-tile coding state: the restoration CDFs live in the tile's CDF context, the references reset per
    /// tile (dav1d InitLrRef).</summary>
    internal sealed class TileState
    {
        public readonly Av1CdfContext Cdf;
        public readonly Unit[] Ref = new Unit[3];
        public TileState(Av1CdfContext cdf)
        {
            Cdf = cdf;
            for (int p = 0; p < 3; p++) Ref[p] = new Unit { V0 = 3, V1 = -7, V2 = 15, H0 = 3, H1 = -7, H2 = 15, W0 = -32, W1 = 31 };
        }
    }

    /// <summary>Codes the units the superblock at luma 4x4 position (bx4, by4) owns — the decoder's
    /// ReadRestorationInfoForSb conditions (no superres).</summary>
    internal static void WriteSb(Av1MsacWriter w, TileState ts, Plan plan, int bx4, int by4, int width, int height,
        int ssX, int ssY, bool monochrome)
    {
        for (int p = 0; p < (monochrome ? 1 : 3); p++)
        {
            if (plan.FrameType[p] == 0) continue;
            int sx = p != 0 ? ssX : 0, sy = p != 0 ? ssY : 0;
            int log2 = plan.SizeLog2[p], size = 1 << log2, mask = size - 1, half = size >> 1;
            int y = by4 * 4 >> sy, h = (height + sy) >> sy;
            if ((y & mask) != 0 || (y != 0 && y + half > h)) continue;
            int x = bx4 * 4 >> sx, wd = (width + sx) >> sx;
            if ((x & mask) != 0 || (x != 0 && x + half > wd)) continue;
            ref var u = ref plan.Units[p][(y >> log2) * plan.Cols[p] + (x >> log2)];
            WriteUnit(w, ts, p, (Av1RestorationType)plan.FrameType[p], ref u);
        }
    }

    private static void WriteUnit(Av1MsacWriter w, TileState ts, int p, Av1RestorationType frameType, ref Unit u)
    {
        var mode = ts.Cdf.Mode;
        bool isSgr = u.Type >= Av1RestorationType.SelfGuided;
        if (frameType == Av1RestorationType.Switchable)
            w.EncodeSymbolAdapt(mode.RestoreSwitchable, u.Type == Av1RestorationType.None ? 0 : isSgr ? 2 : 1, 2);
        else
            w.EncodeBoolAdapt(frameType == Av1RestorationType.Wiener ? mode.RestoreWiener : mode.RestoreSgrproj,
                u.Type == Av1RestorationType.None ? 0u : 1u);
        ref var r = ref ts.Ref[p];
        if (u.Type == Av1RestorationType.Wiener)
        {
            if (p == 0) Subexp(w, r.V0 + 5, 16, 1, u.V0 + 5);
            Subexp(w, r.V1 + 23, 32, 2, u.V1 + 23);
            Subexp(w, r.V2 + 17, 64, 3, u.V2 + 17);
            if (p == 0) Subexp(w, r.H0 + 5, 16, 1, u.H0 + 5);
            Subexp(w, r.H1 + 23, 32, 2, u.H1 + 23);
            Subexp(w, r.H2 + 17, 64, 3, u.H2 + 17);
            u.W0 = r.W0; u.W1 = r.W1;
            if (p != 0) { u.V0 = 0; u.H0 = 0; }
            r = u;
        }
        else if (isSgr)
        {
            int set = u.Type - Av1RestorationType.SelfGuided;
            w.EncodeLiteral((uint)set, 4);
            if (Av1Tables.SgrParams[set, 0] != 0) Subexp(w, r.W0 + 96, 128, 4, u.W0 + 96); else u.W0 = 0;
            if (Av1Tables.SgrParams[set, 1] != 0) Subexp(w, r.W1 + 32, 128, 4, u.W1 + 32); else u.W1 = 95;
            u.V0 = r.V0; u.V1 = r.V1; u.V2 = r.V2; u.H0 = r.H0; u.H1 = r.H1; u.H2 = r.H2;
            r = u;
        }
    }

    // Inverse of Av1Msac.DecodeSubexp(reference, n, k) for value val in [0, n).
    private static void Subexp(Av1MsacWriter w, int reference, int n, int k, int val)
    {
        uint v = (uint)(reference * 2 <= n ? Recenter(reference, val) : Recenter(n - 1 - reference, n - 1 - val));
        if (v < (1u << k)) { w.EncodeBoolEqui(0); w.EncodeLiteral(v, k); return; }
        w.EncodeBoolEqui(1);
        if (v < (1u << (k + 1))) { w.EncodeBoolEqui(0); w.EncodeLiteral(v - (1u << k), k); return; }
        w.EncodeBoolEqui(1);
        if (v < (1u << (k + 2))) { w.EncodeBoolEqui(0); w.EncodeLiteral(v - (1u << (k + 1)), k + 1); return; }
        w.EncodeBoolEqui(1);
        w.EncodeLiteral(v - (1u << (k + 2)), k + 2);
    }

    private static int SubexpBits(int reference, int n, int k, int val)
    {
        uint v = (uint)(reference * 2 <= n ? Recenter(reference, val) : Recenter(n - 1 - reference, n - 1 - val));
        if (v < (1u << k)) return 1 + k;
        if (v < (1u << (k + 1))) return 2 + k;
        if (v < (1u << (k + 2))) return 3 + k + 1;
        return 3 + k + 2;
    }

    // Inverse of Av1Msac.InvRecenter(r, v).
    private static int Recenter(int r, int x) => x > 2 * r ? x : x >= r ? (x - r) << 1 : ((r - x) << 1) - 1;

    // ---- search ---------------------------------------------------------------------------------------------------

    /// <summary>Chooses the restoration plan for the post-CDEF reconstruction <paramref name="rec"/> of source
    /// <paramref name="src"/> (planes Y, U, V; U/V null when monochrome) minimising SSE + λ·bits. Returns null when no
    /// restoration pays.</summary>
    internal static Plan? Search(ushort[][] src, ushort[][] rec, int[] widths, int[] heights, int[] srcStrides, int[] recStrides,
        bool monochrome, bool i420, int bitDepth, double lambda, int sgrSets, int wienerRounds, int statsStep, int threads, int[]? unitShifts = null,
        LrPrune prune = default)
    {
        int[] shifts = unitShifts ?? [0, 1, 2];
        Av1LoopRestoration.WienerBitDepth = bitDepth;
        int np = monochrome ? 1 : 3;
        // Each (plane, unit size) is evaluated once; the unit-shift / uv-shift combinations then just add them up.
        var memo = new Dictionary<(int, int), Plan>();
        double PlaneCost(int p, int log2)
        {
            if (!memo.TryGetValue((p, log2), out var pp))
            {
                pp = new Plan();
                pp.SizeLog2[p] = log2;
                pp.Cost = SearchPlane(pp, p, src[p], rec[p], widths[p], heights[p], srcStrides[p], recStrides[p],
                    bitDepth, lambda, sgrSets, wienerRounds, statsStep, threads, prune);
                memo[(p, log2)] = pp;
            }
            return pp.Cost;
        }
        Plan? best = null; double bestCost = 0;   // cost relative to no restoration
        foreach (int shift in shifts)
            for (int uvShift = 0; uvShift <= (i420 && !monochrome ? 1 : 0); uvShift++)
            {
                double total = 0;
                for (int p = 0; p < np; p++) total += PlaneCost(p, 6 + shift - (p != 0 ? uvShift : 0));
                var plan = new Plan { UnitShift = shift, UvShift = uvShift };
                for (int p = 0; p < np; p++)
                {
                    var pp = memo[(p, 6 + shift - (p != 0 ? uvShift : 0))];
                    plan.SizeLog2[p] = pp.SizeLog2[p]; plan.FrameType[p] = pp.FrameType[p]; plan.Units[p] = pp.Units[p];
                    plan.Cols[p] = pp.Cols[p]; plan.Rows[p] = pp.Rows[p];
                }
                if (plan.Any) total += lambda * (3 + (i420 ? 1 : 0));   // header bits
                if (total < bestCost) { bestCost = total; best = plan; }
            }
        return best;
    }

    // Per plane: evaluates every unit's none / Wiener / self-guided options, then picks the frame type (switchable,
    // Wiener-only, self-guided-only or none) with the least total cost; returns that cost relative to none.
    private static double SearchPlane(Plan plan, int p, ushort[] src, ushort[] rec, int w, int h, int srcStride, int recStride,
        int bitDepth, double lambda, int sgrSets, int wienerRounds, int statsStep, int threads, LrPrune prune)
    {
        int log2 = plan.SizeLog2[p], size = 1 << log2;
        int cols = Math.Max(1, (w + (size >> 1)) >> log2), rows = Math.Max(1, (h + (size >> 1)) >> log2);
        plan.Cols[p] = cols; plan.Rows[p] = rows;
        var cand = new (long SseNone, long SseW, Unit W, long SseS, Unit S)[cols * rows];
        void Eval(int i)
        {
            int ux = i % cols, uy = i / cols;
            int x0 = ux << log2, y0 = uy << log2;
            int x1 = ux == cols - 1 ? w : x0 + size, y1 = uy == rows - 1 ? h : y0 + size;
            cand[i] = EvalUnit(src, rec, srcStride, recStride, w, x0, y0, x1 - x0, y1 - y0, p != 0, bitDepth, sgrSets, wienerRounds, statsStep,
                prune, lambda);
        }
        if (threads > 1)
            System.Threading.Tasks.Parallel.For(0, cand.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                i => { Av1LoopRestoration.WienerBitDepth = bitDepth; Eval(i); });
        else for (int i = 0; i < cand.Length; i++) Eval(i);

        // Frame type: run each option through the reference chain in coding order (units are read in superblock
        // raster order = unit raster order for units >= 64), counting the actual coefficient bits.
        double bestCost = 0; int bestType = 0; Unit[]? bestUnits = null;
        foreach (int ft in new[] { 1, 2, 3 })
        {
            var r = new Unit { V0 = 3, V1 = -7, V2 = 15, H0 = 3, H1 = -7, H2 = 15, W0 = -32, W1 = 31 };
            var units = new Unit[cand.Length];
            double cost = 0;
            for (int i = 0; i < cand.Length; i++)
            {
                var c = cand[i];
                double flagBits = ft == 1 ? 1.6 : 1.0;
                double jNone = 0;
                double jW = double.MaxValue, jS = double.MaxValue;
                if (ft != 3 && c.W.Type == Av1RestorationType.Wiener)
                    jW = (c.SseW - c.SseNone) + lambda * WienerBits(r, c.W, p != 0);
                if (ft != 2 && c.S.Type >= Av1RestorationType.SelfGuided)
                {
                    jS = (c.SseS - c.SseNone) + lambda * SgrBits(r, c.S);
                    // dual_sgr_penalty_level: a two-filter set's (ep < 10) RD cost x (1 + 0.01 * level)
                    if (prune.DualSgrPenalty > 0 && c.S.Type - Av1RestorationType.SelfGuided < 10)
                        jS += 0.01 * prune.DualSgrPenalty * (c.SseS + lambda * SgrBits(r, c.S));
                }
                double j = Math.Min(jNone, Math.Min(jW, jS));
                cost += j + lambda * flagBits;
                if (j == jNone) units[i] = new Unit { Type = Av1RestorationType.None };
                else if (j == jW) { units[i] = c.W; r = Chain(r, c.W, p != 0); }
                else { units[i] = c.S; r = Chain(r, c.S, p != 0); }
            }
            if (cost < bestCost) { bestCost = cost; bestType = ft; bestUnits = units; }
        }
        plan.FrameType[p] = bestType;
        plan.Units[p] = bestUnits ?? new Unit[cand.Length];
        return bestCost;
    }

    // The reference the decoder keeps after coding unit u (WriteUnit's update).
    private static Unit Chain(Unit r, Unit u, bool chroma)
    {
        if (u.Type == Av1RestorationType.Wiener)
        {
            var n = u; n.W0 = r.W0; n.W1 = r.W1; if (chroma) { n.V0 = 0; n.H0 = 0; }
            return n;
        }
        int set = u.Type - Av1RestorationType.SelfGuided;
        var s = u;
        if (Av1Tables.SgrParams[set, 0] == 0) s.W0 = 0;
        if (Av1Tables.SgrParams[set, 1] == 0) s.W1 = 95;
        s.V0 = r.V0; s.V1 = r.V1; s.V2 = r.V2; s.H0 = r.H0; s.H1 = r.H1; s.H2 = r.H2;
        return s;
    }

    private static int WienerBits(Unit r, Unit u, bool chroma)
        => (chroma ? 0 : SubexpBits(r.V0 + 5, 16, 1, u.V0 + 5) + SubexpBits(r.H0 + 5, 16, 1, u.H0 + 5))
         + SubexpBits(r.V1 + 23, 32, 2, u.V1 + 23) + SubexpBits(r.V2 + 17, 64, 3, u.V2 + 17)
         + SubexpBits(r.H1 + 23, 32, 2, u.H1 + 23) + SubexpBits(r.H2 + 17, 64, 3, u.H2 + 17);

    private static int SgrBits(Unit r, Unit u)
    {
        int set = u.Type - Av1RestorationType.SelfGuided;
        return 4 + (Av1Tables.SgrParams[set, 0] != 0 ? SubexpBits(r.W0 + 96, 128, 4, u.W0 + 96) : 0)
                 + (Av1Tables.SgrParams[set, 1] != 0 ? SubexpBits(r.W1 + 32, 128, 4, u.W1 + 32) : 0);
    }

    /// <summary>libaom's loop-restoration search prunes (pickrst.c, lpf_sf): SgrEp = enable_sgr_ep_pruning (1: 4 seed
    /// sets, the winner's neighbours, then 2 of groups 2-3), SgrOnWiener = prune_sgr_based_on_wiener (1: skip
    /// self-guided when Wiener's cost exceeds 1.01x none's; 2: when Wiener loses to none or was pruned),
    /// WienerSrcVar = prune_wiener_based_on_src_var (skip Wiener when the source unit's variance sum is below
    /// (dc_q >> 3)^2 * level / 16), ReduceWiener = reduce_wiener_window_size (5-tap luma), DualSgrPenalty =
    /// dual_sgr_penalty_level; Qs = the frame's dc_q >> 3.</summary>
    internal readonly record struct LrPrune(int SgrEp, int SgrOnWiener, int WienerSrcVar, bool ReduceWiener, int DualSgrPenalty, int Qs);

    // enable_sgr_ep_pruning's search: seeds of group 1 (sets 0-9), then per group-1 winner one set of each of groups 2, 3
    private static readonly int[] SgrEpSeeds = { 0, 3, 6, 9 };
    private static readonly int[][] SgrEpGrp23 =
    {
        new[] { 10, 10, 11, 11, 12, 12, 13, 13, 13, 13, -1, -1, -1, -1 },
        new[] { 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15 },
    };
    private static readonly Unit DefaultRef = new() { V0 = 3, V1 = -7, V2 = 15, H0 = 3, H1 = -7, H2 = 15, W0 = -32, W1 = 31 };

    // ---- per-unit evaluation ---------------------------------------------------------------------------------------

    // Per-thread scratch (units are evaluated in parallel; per-unit arrays of these sizes would be large-object-heap
    // allocations that serialise the workers on the GC).
    private sealed class Scratch
    {
        public ushort[] Base = [], Left = [], Work = [];
        public readonly ushort[][] F0 = Empty16(), F1 = Empty16();
        private static ushort[][] Empty16() { var a = new ushort[16][]; Array.Fill(a, Array.Empty<ushort>()); return a; }
        public double[] Pad = [], VBuf = [], HBuf = [];
        public int[] P = [], Sv = [];
        public readonly int[][] T0 = EmptyI16(), T1 = EmptyI16();
        private static int[][] EmptyI16() { var a = new int[16][]; Array.Fill(a, Array.Empty<int>()); return a; }
        public static T[] Get<T>(ref T[] a, int n) { if (a.Length < n) a = new T[n]; return a; }
    }
    [ThreadStatic] private static Scratch? t_scratch;
    private static readonly bool SgrSlow = Environment.GetEnvironmentVariable("AV1_LRSLOW") == "1";

    private static (long, long, Unit, long, Unit) EvalUnit(ushort[] src, ushort[] rec, int srcStride, int recStride,
        int planeW, int x0, int y0, int uw, int uh, bool chroma, int bitDepth, int sgrSets, int wienerRounds, int statsStep,
        LrPrune prune = default, double lambda = 0)
    {
        if (uw < 4 || uh < 4) return (0, long.MaxValue, default, long.MaxValue, default);   // degenerate unit: none
        // Working copy: the unit plus 3 real columns right (Right edge flag) and 4 left columns (the left[] input).
        int stride = uw + 3, n = stride * uh;
        var sc = t_scratch ??= new Scratch();
        var baseBuf = Scratch.Get(ref sc.Base, n);
        var left = Scratch.Get(ref sc.Left, uh * 4);
        bool haveLeft = x0 > 0, haveRight = x0 + uw < planeW;
        for (int y = 0; y < uh; y++)
        {
            int ro = (y0 + y) * recStride;
            Array.Copy(rec, ro + x0, baseBuf, y * stride, uw + (haveRight ? Math.Min(3, planeW - x0 - uw) : 0));
            if (haveLeft) for (int k = 0; k < 4; k++) left[y * 4 + k] = rec[ro + Math.Max(0, x0 - 4 + k)];
        }
        var edges = (haveLeft ? Av1LoopRestoration.LrEdgeFlags.Left : 0) | (haveRight ? Av1LoopRestoration.LrEdgeFlags.Right : 0);

        long Sse(ushort[] buf)
        {
            long e = 0;
            for (int y = 0; y < uh; y++)
            {
                int so = (y0 + y) * srcStride + x0, bo = y * stride;
                for (int x = 0; x < uw; x++) { int d = buf[bo + x] - src[so + x]; e += (long)d * d; }
            }
            return e;
        }
        long sseNone = Sse(baseBuf);
        var work = Scratch.Get(ref sc.Work, n);

        // --- Wiener: separable symmetric taps by alternating least squares on the float model, quantised to range.
        Unit wUnit = default; long sseW = long.MaxValue;
        bool skipSgr = false;
        bool pruneWiener = false;
        if (prune.WienerSrcVar > 0)
        {
            // prune_wiener_based_on_src_var: a flat source unit (or an exact reconstruction) gets no Wiener search
            long ss = 0, s = 0;
            for (int y = 0; y < uh; y++)
            {
                int so = (y0 + y) * srcStride + x0;
                for (int x = 0; x < uw; x++) { long v = src[so + x]; ss += v * v; s += v; }
            }
            long vsum = ss - s * s / (uw * uh), thresh = ((long)prune.Qs * prune.Qs * prune.WienerSrcVar) >> 4;
            pruneWiener = vsum < thresh || sseNone == 0;
            if (pruneWiener && prune.SgrOnWiener == 2) skipSgr = true;
        }
        if (!pruneWiener)
        {
            var (hTaps, vTaps) = FitWiener(src, srcStride, baseBuf, stride, left, haveLeft, haveRight, x0, y0, uw, uh,
                chroma || prune.ReduceWiener, wienerRounds, statsStep);
            wUnit = new Unit { Type = Av1RestorationType.Wiener, H0 = (sbyte)hTaps[0], H1 = (sbyte)hTaps[1], H2 = (sbyte)hTaps[2],
                V0 = (sbyte)vTaps[0], V1 = (sbyte)vTaps[1], V2 = (sbyte)vTaps[2] };
            Array.Copy(baseBuf, work, n);
            ApplyWiener(work, stride, left, uw, uh, wUnit, bitDepth, edges);
            sseW = Sse(work);
            if (prune.SgrOnWiener > 0)
            {
                // prune_sgr_based_on_wiener (the unit's none / Wiener RD costs, bits against the default reference)
                double costNone = sseNone + lambda, costW = sseW + lambda * (1 + WienerBits(DefaultRef, wUnit, chroma));
                skipSgr = prune.SgrOnWiener == 1 ? costW > 1.01 * costNone : !(costW < costNone);
            }
        }

        // --- Self-guided: each set's 5x5 / 3x3 outputs at full weight, least-squares projection weights.
        Unit sUnit = default; long sseS = long.MaxValue;
        ushort[] Filtered(bool five, int set, int s)
        {
            var f = Scratch.Get(ref (five ? sc.F0 : sc.F1)[set], n);
            Array.Copy(baseBuf, f, n);
            if (five) Av1LoopRestoration.Sgr5x5(f, 0, stride, left, 0, 4, Lpf(stride), 0, uw, uh, s, 128, edges);
            else Av1LoopRestoration.Sgr3x3(f, 0, stride, left, 0, 4, Lpf(stride), 0, uw, uh, s, 128, edges);
            return f;
        }
        // AVX2: the filters' exact 4-fraction-bit outputs on the padded unit, scored with the decoder's weighting
        bool fast = Av1LrSgrFast.Supported && !SgrSlow && !skipSgr && sgrSets > 0;
        int fs = 0; int[] pInt = [], sInt = [];
        if (fast)
        {
            fs = Av1LrSgrFast.Stride(uw);
            pInt = Scratch.Get(ref sc.P, (uh + 6) * (fs + 8));
            sInt = Scratch.Get(ref sc.Sv, uh * fs);
            Av1LrSgrFast.Pad(pInt, baseBuf, stride, left, haveLeft, haveRight ? Math.Min(3, planeW - x0 - uw) : 0, uw, uh);
            for (int y = 0; y < uh; y++)
            {
                int so = (y0 + y) * srcStride + x0, o = y * fs;
                for (int x = 0; x < uw; x++) sInt[o + x] = src[so + x];
            }
        }
        long TrySetFast(int set)
        {
            int s0 = Av1Tables.SgrParams[set, 0], s1 = Av1Tables.SgrParams[set, 1];
            int[]? t0 = null, t1 = null;
            if (s0 != 0) Av1LrSgrFast.Flt(pInt, uw, uh, 2, s0, bitDepth, t0 = Scratch.Get(ref sc.T0[set], uh * fs));
            if (s1 != 0) Av1LrSgrFast.Flt(pInt, uw, uh, 1, s1, bitDepth, t1 = Scratch.Get(ref sc.T1[set], uh * fs));
            // minimise Σ (e - (a·t0 + b·t1) / 2048)², e = src - u
            Span<double> m = stackalloc double[5];
            Av1LrSgrFast.Moments(pInt, sInt, (t0 ?? t1)!, t0 != null ? t1 : null, uw, uh, m);
            double a = 0, b = 0;
            if (t0 != null && t1 != null)
            {
                double det = m[0] * m[2] - m[1] * m[1];
                if (Math.Abs(det) < 1e-9) return long.MaxValue;
                a = 2048 * (m[3] * m[2] - m[4] * m[1]) / det; b = 2048 * (m[4] * m[0] - m[3] * m[1]) / det;
            }
            else if (t0 != null) { if (m[0] < 1e-9) return long.MaxValue; a = 2048 * m[3] / m[0]; }
            else { if (m[0] < 1e-9) return long.MaxValue; b = 2048 * m[3] / m[0]; }
            int xq0 = t0 != null ? Math.Clamp((int)Math.Round(a), XqdMin0, XqdMax0) : 0;
            int xq1 = t1 != null ? Math.Clamp(128 - xq0 - (int)Math.Round(b), XqdMin1, XqdMax1) : 95;
            long e2 = Av1LrSgrFast.Sse(pInt, sInt, t0, xq0, t1, 128 - xq0 - xq1, uw, uh, (1 << bitDepth) - 1);
            var cu = new Unit { Type = Av1RestorationType.SelfGuided + (byte)set, W0 = (sbyte)xq0, W1 = (sbyte)xq1 };
            if (e2 < sseS) { sseS = e2; sUnit = cu; }
            return e2;
        }
        long TrySet(int set)
        {
            if (fast) return TrySetFast(set);
            int s0 = Av1Tables.SgrParams[set, 0], s1 = Av1Tables.SgrParams[set, 1];
            ushort[]? f0 = s0 != 0 ? Filtered(true, set, s0) : null, f1 = s1 != 0 ? Filtered(false, set, s1) : null;
            // minimise Σ (src - u - a·(f0-u)/128 - b·(f1-u)/128)²
            double s00 = 0, s01 = 0, s11 = 0, t0 = 0, t1 = 0;
            for (int y = 0; y < uh; y++)
            {
                int so = (y0 + y) * srcStride + x0, bo = y * stride;
                for (int x = 0; x < uw; x++)
                {
                    double u = baseBuf[bo + x], e = src[so + x] - u;
                    double d0 = f0 != null ? f0[bo + x] - u : 0, d1 = f1 != null ? f1[bo + x] - u : 0;
                    s00 += d0 * d0; s01 += d0 * d1; s11 += d1 * d1; t0 += d0 * e; t1 += d1 * e;
                }
            }
            double a = 0, b = 0;
            if (f0 != null && f1 != null)
            {
                double det = s00 * s11 - s01 * s01;
                if (Math.Abs(det) < 1e-9) return long.MaxValue;
                a = 128 * (t0 * s11 - t1 * s01) / det; b = 128 * (t1 * s00 - t0 * s01) / det;
            }
            else if (f0 != null) { if (s00 < 1e-9) return long.MaxValue; a = 128 * t0 / s00; }
            else { if (s11 < 1e-9) return long.MaxValue; b = 128 * t1 / s11; }
            int xq0 = f0 != null ? Math.Clamp((int)Math.Round(a), XqdMin0, XqdMax0) : 0;
            int xq1 = f1 != null ? Math.Clamp(128 - xq0 - (int)Math.Round(b), XqdMin1, XqdMax1) : 95;
            var cu = new Unit { Type = Av1RestorationType.SelfGuided + (byte)set, W0 = (sbyte)xq0, W1 = (sbyte)xq1 };
            // SSE of the quantised projection on the filtered outputs (the decoder's weighting; f0/f1 carry the
            // full-weight filter already rounded to pixels, so this is a close estimate of the exact kernel).
            int wa = f0 != null ? xq0 : 0, wb = 128 - xq0 - (f1 != null ? xq1 : 95);
            if (f1 == null) wb = 0;
            int pmax = (1 << bitDepth) - 1;
            long e2 = 0;
            for (int y = 0; y < uh; y++)
            {
                int so = (y0 + y) * srcStride + x0, bo = y * stride;
                for (int x = 0; x < uw; x++)
                {
                    int u = baseBuf[bo + x];
                    int v = wa * ((f0 != null ? f0[bo + x] : u) - u) + wb * ((f1 != null ? f1[bo + x] : u) - u);
                    int o = Math.Clamp(u + ((v + 64) >> 7), 0, pmax);
                    int d = o - src[so + x]; e2 += (long)d * d;
                }
            }
            if (e2 < sseS) { sseS = e2; sUnit = cu; }
            return e2;
        }
        if (skipSgr || sgrSets <= 0) { }
        else if (prune.SgrEp > 0)
        {
            // enable_sgr_ep_pruning (search_selfguided_restoration): best of the 4 seeds, its neighbours in 0-9 (level 1),
            // then the group 2 / 3 set paired with the best so far
            int bestEp = -1; long bestErr = long.MaxValue;
            void Ep(int ep) { long e = TrySet(ep); if (bestEp < 0 || e < bestErr) { bestErr = e; bestEp = ep; } }
            foreach (int ep in SgrEpSeeds) Ep(ep);
            if (prune.SgrEp < 2)
            {
                int refEp = bestEp;
                for (int ep = refEp - 1; ep < refEp + 2; ep += 2) if (ep >= 0 && ep <= 9) Ep(ep);
                for (int g = 0; g < 2; g++) { int ep = SgrEpGrp23[g][bestEp]; if (ep >= 0) Ep(ep); }
            }
        }
        else foreach (int set in SgrSetOrder(sgrSets)) TrySet(set);
        return (sseNone, sseW, wUnit, sseS, sUnit);
    }

    // The kernels slice their stripe-line input even without Top/Bottom edges (then it is never read).
    [ThreadStatic] private static ushort[]? t_lpf;
    private static ushort[] Lpf(int stride) => t_lpf is { } a && a.Length >= stride * 8 ? a : (t_lpf = new ushort[stride * 8]);

    // Sets searched: all 16, or the first n of a spread-out order (fewer at faster speeds).
    private static readonly int[] SetOrder = { 5, 10, 14, 0, 2, 8, 12, 15, 3, 7, 11, 13, 1, 4, 6, 9 };
    private static IEnumerable<int> SgrSetOrder(int n) { for (int i = 0; i < Math.Min(n, 16); i++) yield return SetOrder[i]; }

    private static void ApplyWiener(ushort[] buf, int stride, ushort[] left, int uw, int uh, Unit u, int bitDepth,
        Av1LoopRestoration.LrEdgeFlags edges)
    {
        // Same tap construction as Av1Decoder's LR dispatch (+128 folded into the horizontal centre above 8 bits).
        Span<short> fh = stackalloc short[8], fv = stackalloc short[8];
        fh[0] = fh[6] = u.H0; fh[1] = fh[5] = u.H1; fh[2] = fh[4] = u.H2;
        fh[3] = (short)(-(u.H0 + u.H1 + u.H2) * 2 + (bitDepth == 8 ? 0 : 128));
        fv[0] = fv[6] = u.V0; fv[1] = fv[5] = u.V1; fv[2] = fv[4] = u.V2;
        fv[3] = (short)(128 - (u.V0 + u.V1 + u.V2) * 2);
        Av1LoopRestoration.Wiener(buf, 0, stride, left, 0, 4, Lpf(stride), 0, uw, uh, fh, fv, edges);
    }

    // Separable symmetric Wiener fit on the float model out = V * (H * rec) (taps /128, sum 128): alternate least
    // squares on the free taps of one axis with the other fixed (a few rounds), integer-quantised into the AV1 ranges.
    // The unit is padded 3 px (left from left[], right from the buffer's extra columns, top/bottom replicated like the
    // frame-edge filter).
    private static (int[] H, int[] V) FitWiener(ushort[] src, int srcStride, ushort[] buf, int stride, ushort[] left,
        bool haveLeft, bool haveRight, int x0, int y0, int uw, int uh, bool chroma, int rounds, int step)
    {
        int pw = uw + 6, ph = uh + 6;
        var sc = t_scratch ??= new Scratch();
        var pad = Scratch.Get(ref sc.Pad, pw * ph);
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Clamp(y - 3, 0, uh - 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = x - 3; double v;
                if (sx < 0) v = haveLeft ? left[sy * 4 + 4 + sx] : buf[sy * stride];
                else if (sx >= uw) v = haveRight ? buf[sy * stride + Math.Min(sx, uw + 2)] : buf[sy * stride + uw - 1];
                else v = buf[sy * stride + sx];
                pad[y * pw + x] = v;
            }
        }
        int m = chroma ? 2 : 3, off = chroma ? 1 : 0;   // chroma is 5-tap: tap 0 stays 0
        int[] hT = { 0, 0, 0 }, vT = { 0, 0, 0 };
        var vBuf = Scratch.Get(ref sc.VBuf, uh * pw);   // vertically filtered, unit rows x padded cols
        var hBuf = Scratch.Get(ref sc.HBuf, ph * uw);   // horizontally filtered, padded rows x unit cols
        Span<double> f = stackalloc double[3], aug = stackalloc double[12], sol = stackalloc double[3];
        for (int round = 0; round < rounds; round++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
                if (axis == 0)
                {
                    double[] kv = Kernel(vT);
                    for (int y = 0; y < uh; y += step)
                        for (int x = 0; x < pw; x++)
                        {
                            double sum = 0; for (int j = 0; j < 7; j++) sum += kv[j] * pad[(y + j) * pw + x];
                            vBuf[y * pw + x] = sum / 128;
                        }
                }
                else
                {
                    double[] kh = Kernel(hT);
                    for (int y = 0; y < ph; y++)
                        for (int x = 0; x < uw; x += step)
                        {
                            double sum = 0; for (int i = 0; i < 7; i++) sum += kh[i] * pad[y * pw + x + i];
                            hBuf[y * uw + x] = sum / 128;
                        }
                }
                for (int y = 0; y < uh; y += step)
                    for (int x = 0; x < uw; x += step)
                    {
                        double centre;
                        if (axis == 0)
                        {
                            int o = y * pw + x + 3; centre = vBuf[o];
                            for (int a = 0; a < m; a++) { int d = 3 - (a + off); f[a] = (vBuf[o - d] + vBuf[o + d] - 2 * centre) / 128; }
                        }
                        else
                        {
                            int o = (y + 3) * uw + x; centre = hBuf[o];
                            for (int a = 0; a < m; a++) { int d = 3 - (a + off); f[a] = (hBuf[o - d * uw] + hBuf[o + d * uw] - 2 * centre) / 128; }
                        }
                        double target = src[(y0 + y) * srcStride + x0 + x] - centre;
                        double f0 = f[0], f1 = f[1], f2 = m > 2 ? f[2] : 0;
                        a00 += f0 * f0; a01 += f0 * f1; a02 += f0 * f2; a11 += f1 * f1; a12 += f1 * f2; a22 += f2 * f2;
                        b0 += f0 * target; b1 += f1 * target; b2 += f2 * target;
                    }
                // the normal equations as an m x (m + 1) augmented matrix
                if (m > 2) { aug[0] = a00; aug[1] = a01; aug[2] = a02; aug[3] = b0; aug[4] = a01; aug[5] = a11; aug[6] = a12; aug[7] = b1; aug[8] = a02; aug[9] = a12; aug[10] = a22; aug[11] = b2; }
                else { aug[0] = a00; aug[1] = a01; aug[2] = b0; aug[3] = a01; aug[4] = a11; aug[5] = b1; }
                if (!Solve(aug, m, sol)) continue;
                var t = axis == 0 ? hT : vT;
                for (int a = 0; a < m; a++) t[a + off] = Math.Clamp((int)Math.Round(sol[a]), WMin[a + off], WMax[a + off]);
            }
        }
        return (hT, vT);
    }

    private static double[] Kernel(int[] t) => new double[] { t[0], t[1], t[2], 128 - 2 * (t[0] + t[1] + t[2]), t[2], t[1], t[0] };

    // Gauss-Jordan with partial pivoting on the n x (n + 1) augmented matrix m (row-major, in place); false if singular.
    private static bool Solve(Span<double> m, int n, Span<double> x)
    {
        int w = n + 1;
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r * w + c]) > Math.Abs(m[piv * w + c])) piv = r;
            if (Math.Abs(m[piv * w + c]) < 1e-9) return false;
            for (int j = 0; j <= n; j++) (m[c * w + j], m[piv * w + j]) = (m[piv * w + j], m[c * w + j]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = m[r * w + c] / m[c * w + c];
                for (int j = c; j <= n; j++) m[r * w + j] -= f * m[c * w + j];
            }
        }
        for (int i = 0; i < n; i++) x[i] = m[i * w + n] / m[i * w + i];
        return true;
    }
}
