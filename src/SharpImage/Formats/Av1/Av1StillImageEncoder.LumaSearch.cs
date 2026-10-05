using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// Port of libaom's intra luma mode search for key frames at its slow speeds (av1_rd_pick_intra_sby_mode,
// intra_mode_search.c; choose_tx_size_type_from_rd / uniform_txfm_yrd / search_tx_type, tx_search.c), used by the
// rect leaf (every 4:4:4 / 4:2:2 leaf, 4:2:0 rect and strip leaves) when Av1EncodeSpeed.LibaomLuma is set:
//  - modes in libaom's order (intra_rd_search_mode_order, then the directional modes' angle deltas -3..3),
//  - each pruned on a Hadamard model cost against the running top-k and 1.5x the best (prune_intra_y_mode),
//  - survivors RD-searched JOINTLY over tx sizes (the largest rect tx, then its splits, MAX_TX_DEPTH 2 from the
//    speed's init depth, stopping at 4x4) and, per tx block, over tx types (the reduced intra set per mode when the
//    full set applies, av1_reduced_intra_tx_used_flag) with RDOQ in the type loop when the residual MSE is below
//    the speed's coefficient-optimisation threshold (coeff_opt_thresholds),
//  - then the filter-intra modes the same way (rd_pick_filter_intra_sby).
internal static partial class Av1StillImageEncoder
{
    private static readonly Av1IntraPredMode[] AomModeOrder =
    {
        Av1IntraPredMode.Dc, Av1IntraPredMode.Horizontal, Av1IntraPredMode.Vertical, Av1IntraPredMode.Smooth,
        Av1IntraPredMode.Paeth, Av1IntraPredMode.SmoothV, Av1IntraPredMode.SmoothH, Av1IntraPredMode.DiagDownRight,
        Av1IntraPredMode.HorizontalUp, Av1IntraPredMode.HorizontalDown, Av1IntraPredMode.VerticalLeft,
        Av1IntraPredMode.VerticalRight, Av1IntraPredMode.DiagDownLeft,
    };

    // av1_reduced_intra_tx_used_flag (libaom blockd.h), indexed by the (filter-mapped) intra direction.
    private static readonly ushort[] AomReducedIntraTxMask =
    {
        0x080F, 0x040F, 0x080F, 0x020F, 0x080F, 0x040F, 0x080F, 0x080F, 0x040F, 0x080F, 0x040F, 0x080F, 0x0C0E,
    };

    // The intra tx-type candidates in libaom's evaluation order (TX_TYPE enum order, DCT_DCT first).
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] AomOrderFull =
        [.. IntraTxTypesFull.OrderBy(t => (int)t.Inv)];
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] AomOrderReduced =
        [.. IntraTxTypes.OrderBy(t => (int)t.Inv)];

    internal struct LumaPick
    {
        public Av1IntraPredMode Mode;   // Filter for a filter-intra winner (Delta = filter mode)
        public int Delta, ModeNoFilt, Depth, Tx;
        public double J;
        public List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)> Txb;
    }


    // The luma trellis lambda (coefficient-domain units): AomTrellisLam x libaom's (4.25 rdmult in RDCOST units = 68 x
    // its pixel-domain lambda rdmult / 2048), else RdoqLambdaScale x the mode lambda.
    private static double LumaTrellisLambda<TP>(ColorPartCtx<TP> c) where TP : unmanaged
    {
        if (Sp.AomTrellisLam <= 0) return RdoqScale * LamK * c.AcDq * c.AcDq;
        double dc = c.DcDq, rdm = dc * dc * (3.3 + 0.0015 * dc) / (1 << (2 * (Bd - 8)));
        return Sp.AomTrellisLam * 68 * rdm / 2048 * (1 << (2 * (Bd - 8))) * (t_sbLamMul == 0 ? 1 : t_sbLamMul);   // x->rdmult carries the SB modifier
    }
    // dev counters (AOM_STATS): searches, modes RD-evaluated, uniform-tx trials, tx blocks, RDOQ runs in the type loop
    internal static long StatSearch, StatModes, StatTrials, StatTxb, StatRdoq, StatTypeTrials, StatPal, StatPalCand;
    internal static readonly long[] StatPart = new long[6];
    internal static readonly bool StatsOn = Environment.GetEnvironmentVariable("AOM_STATS") == "1";
    internal static readonly bool NoEobHint = Environment.GetEnvironmentVariable("AV1_NOEOBHINT") == "1";
    // dev (AV1_TIMING=1): luma tx search time per stage: 0 predict, 1 residual, 2 forward+quant, 3 trellis, 4 distortion,
    // 5 rate estimate, 6 reconstruction, 7 other type-loop work
    internal static readonly bool TimingOn = Environment.GetEnvironmentVariable("AV1_TIMING") == "1";
    internal static readonly long[] TimeAcc = new long[12];
    internal static void Tick(int k, ref long t) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); System.Threading.Interlocked.Add(ref TimeAcc[k], n - t); t = n; }
    internal static readonly long[] OracleMiss = new long[19 * 16];
    internal static readonly bool CalOn = Environment.GetEnvironmentVariable("AOM_CAL") == "1";
    internal static readonly double[] CalPix = new double[19], CalCoef = new double[19];
    internal static readonly long[] CalN = new long[19];
    internal static readonly double[] CalCmp = new double[8];
    internal static bool OracleLevelsCompareOnly, OracleLevels1DOnly;
    // dev: the other encoder's luma prediction per tx block origin (CalCmp only counts blocks whose prediction is ours)
    internal static Dictionary<int, ushort[]>? OraclePred;
    internal static readonly double[] CalSame = new double[8];

    // libaom uniform_txfm_yrd for an intra block: the w4 x h4 luma block coded on tx size stx (all tx blocks alike),
    // tx blocks in raster order, each predicted from the reconstruction with the decoder's per-tx edge flags
    // (dav1d recon_b_intra). Writes ReconY and the per-tx ALY/LLY coefficient contexts (callers restore them).
    // Returns J = SSE + lambda * coefficient bits, or +inf once the running J exceeds jLimit (args->exit_early).
    private static (double J, List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? Txb)
        LumaUniformTx<TP>(ColorPartCtx<TP> c, int lumaBs, int stx, int bx4, int by4, int w4, int h4,
            Av1IntraPredMode yMode, int yDelta, int yModeNoFilt, Av1EdgeFlags edgeFlags, int intraFlags,
            double jLimit, double trellisMseThr) where TP : unmanaged
    {
        ref readonly var sTD = ref Av1Tables.TxfmDimensions[stx];
        int tw4 = sTD.W, th4 = sTD.H, tw = tw4 * 4, th = th4 * 4, sScan = Av1Tables.Scans[stx].Length;
        bool symbolCoded = sTD.Max <= (byte)Av1TxSize.Tx16x16;
        bool full = UseFullIntraTxSet && sTD.Min < (byte)Av1TxSize.Tx16x16;
        var txSet = (full && symbolCoded) ? AomOrderFull : (symbolCoded ? AomOrderReduced : DctOnly);
        // search_tx_type's mask: the reduced intra set for this direction where the 7-type set applies
        int mask = (full && symbolCoded && Sp.AomReducedIntraTxSet) ? AomReducedIntraTxMask[Math.Min(yModeNoFilt, 12)] : 0xFFFF;
        double lambda = LamK * c.AcDq * c.AcDq, rdoqLambda = RdoqScale * lambda;
        rdoqLambda = LumaTrellisLambda(c);
        // perform_block_coeff_opt: block_mse_q8 <= thr * qstep^2 (qstep = AC dequant >> 3 at 8 bits)
        double qstep = c.AcDq / (double)(1 << (Bd - 5));
        var scr = PxScratch<TP>.Aom ??= new LeafScratch<TP>();
        var pred = scr.P1; var res = scr.R; var qf = scr.Q1; var qfBest = scr.Q2;
        bool sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0, sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0;
        var list = RentTxbList((w4 / tw4) * (h4 / th4));
        var lvPool = Av1FwdTransform.ThreadLevels;
        double fwdBias = Av1FwdTransform.Bias;
        double jSum = 0;
        if (StatsOn) System.Threading.Interlocked.Increment(ref StatTrials);
        for (int iy = 0; iy < h4; iy += th4)
            for (int ix = 0; ix < w4; ix += tw4)
            {
                int tx4 = bx4 + ix, ty4 = by4 + iy, txR = tx4 & 31, tyR = ty4 & 31, px = tx4 * 4, py = ty4 * 4;
                var localEdge = (((iy > 0 || !sbHasTr) && ix + tw4 >= w4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                                ((ix > 0 || (!sbHasBl && iy + th4 >= h4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                long tq0 = TimingOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, tx4, ty4, tw, th, yMode, yDelta, pred, localEdge, intraFlags);
                if (TimingOn) Tick(0, ref tq0);
                long resSse = ResidualSse(c.Luma, c.W, px, py, pred, res, tw, th);
                if (TimingOn) Tick(1, ref tq0);
                double mseQ8 = 256.0 * resSse / (tw * th) / (1 << (2 * (Bd - 8)));
                bool trellis = UseRdoq && mseQ8 <= trellisMseThr * qstep * qstep;
                int skc = Av1CoeffDecode.GetSkipCtx(in sTD, lumaBs, c.ALY.AsSpan(txR), c.LLY.AsSpan(tyR), 0, 0);
                int snc = Av1CoeffDecode.GetDcSignCtx(stx, c.ALY.AsSpan(txR), c.LLY.AsSpan(tyR));
                int[] bestCf = null!; Av1TxType bestInv = Av1TxType.DctDct; int bestIdx = 1;
                double bestJ = double.MaxValue, bestBits = 0; bool bestRdoq = false; int bestEob = -2;
                bool txDom = Sp.AomTxDomainDist && tw <= 32 && th <= 32;   // 64-point: the zeroed half is not in qf
                // transform-domain distortion: sum ((qf - L) * dq)^2 / 64 tracks the pixel SSE (calibrated per tx size)
                double TxDist(int[] lv, double[] q) => TxDistV(lv, q, sScan, c.DcDq, c.AcDq);
                // RDOQ of a candidate: libaom's single-pass trellis (AomTrellis) or our RdoqOptimize
                // returns the coded bits when the trellis priced them (else -1)
                double Quantise(int[] lv, double[] q, int ti, int eobHint = -2)
                {
                    if (Sp.AomTrellis)
                    {
                        int[]? before = Av1CoeffEncode.RdoqCheck ? (int[])lv.Clone() : null;
                        double tb = Av1CoeffEncode.TrellisOptimize(c.Cdf.Coef, stx, 0, lv, q, c.DcDq, c.AcDq, skc, snc, rdoqLambda,
                            Av1CoeffEncode.IntraTxTypeBits(c.Cdf.Mode, stx, yModeNoFilt, ti, UseFullIntraTxSet), Av1CoeffEncode.RdoqCheck ? -2 : eobHint);
                        if (Av1CoeffEncode.RdoqCheck)
                        {
                            double full = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, lv, skc, snc, ti, fullSet: UseFullIntraTxSet);
                            if (Math.Abs(full - tb) > 1e-6) throw new InvalidOperationException($"trellis rate {tb:R} != estimate {full:R} (tx {stx} type {ti}) before [{string.Join(",", Av1Tables.Scans[stx].Select(r => before![r]))}] after [{string.Join(",", Av1Tables.Scans[stx].Select(r => lv[r]))}]");
                        }
                        return tb;
                    }
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, lv, q, c.DcDq, c.AcDq, skc, snc, ti, rdoqLambda);
                    return -1;
                }
                if (StatsOn) System.Threading.Interlocked.Increment(ref StatTxb);
                double budget = jLimit - jSum;   // ref_best_rd for this tx block
                // dev oracle: the other encoder's tx type here, when it is one this tx size searches
                int oTp = -1;
                if (OracleTxtp != null && OracleTxtp.TryGetValue(tx4 | (ty4 << 16), out byte otp))
                {
                    foreach (var tt in txSet) if ((int)tt.Inv == otp) oTp = otp;
                    if (oTp < 0) lock (OracleMiss) { OracleMiss[stx * 16 + otp]++; }
                }
                foreach (var (fwd, inv, idx) in txSet)
                {
                    // adaptive_txb_search_level: the best so far already exceeds the remaining budget by the margin
                    if (Sp.AomAdaptiveTxb > 0 && bestJ != double.MaxValue && bestJ - bestJ / (1 << Sp.AomAdaptiveTxb) > budget) break;
                    // skip_tx_search: a type quantised to all zero ends the search
                    if (Sp.AomSkipTxSearch && bestCf != null && !(bestEob != -2 ? bestEob >= 0 : HasNonZero(bestCf))) break;
                    if (oTp >= 0) { if ((int)inv != oTp) continue; }
                    else if ((mask >> (int)inv & 1) == 0) continue;
                    if (StatsOn) System.Threading.Interlocked.Increment(ref StatTypeTrials);
                    long tq = TimingOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    int[] cf = Av1FwdTransform.ForwardQuantLean(res, tw, th, stx, fwd, c.DcDq, c.AcDq, fwdBias, sScan, qf, lvPool, out int qEob);
                    int cfEob = NoEobHint ? -2 : qEob;   // valid while cf is the quantiser's output
                    if (TimingOn) Tick(2, ref tq);
                    bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                    bool pre = false;
                    bool olv = false;
                    if (oTp >= 0 && OracleLevels != null && OracleLevels.TryGetValue(tx4 | (ty4 << 16), out var ol) && ol.Tx == stx)
                    {
                        bool samePred = OraclePred != null && OraclePred.TryGetValue(tx4 | (ty4 << 16), out var opr) && SameSamples(opr, pred, tw * th);
                        if (!oneD && CalOn && samePred)
                        {
                            // compare our trellis on this residual with the oracle's levels, on our own cost function
                            var mine = (int[])cf.Clone();
                            double mb = HasNonZero(mine) ? Av1CoeffEncode.TrellisOptimize(c.Cdf.Coef, stx, 0, mine, qf, c.DcDq, c.AcDq, skc, snc, rdoqLambda,
                                Av1CoeffEncode.IntraTxTypeBits(c.Cdf.Mode, stx, yModeNoFilt, idx, UseFullIntraTxSet))
                                : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, mine, skc, snc, idx, fullSet: UseFullIntraTxSet);
                            double lb = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, ol.Lv, skc, snc, idx, fullSet: UseFullIntraTxSet);
                            double md = ReconSseCandRect(mine, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, inv);
                            double ld = ReconSseCandRect(ol.Lv, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, inv);
                            int agree = 0, n = 0;
                            for (int k = 0; k < sScan; k++) if (mine[k] != 0 || ol.Lv[k] != 0) { n++; if (mine[k] == ol.Lv[k]) agree++; }
                            lock (CalPix) { CalCmp[0] += n; CalCmp[1] += agree; CalCmp[2] += mb; CalCmp[3] += lb; CalCmp[4] += md; CalCmp[5] += ld;
                                CalCmp[6] += md + lambda * mb < ld + lambda * lb ? 1 : 0; CalCmp[7] += 1; }
                        }
                        if (!OracleLevelsCompareOnly && (oneD || !OracleLevels1DOnly)) { Array.Copy(ol.Lv, cf, sScan); olv = true; pre = true; }
                    }
                    double preBits = -1;
                    // libaom's order (search_tx_type): quantise, trellis, then one rate + one distortion
                    int candEob = cfEob;   // this candidate's eob when known (-2 not)
                    if (!olv && Sp.AomTrellisFirst && trellis && !oneD && (cfEob == -2 ? HasNonZero(cf) : cfEob >= 0))
                    {
                        if (TimingOn) Tick(7, ref tq);
                        preBits = Quantise(cf, qf, idx, cfEob);
                        candEob = Sp.AomTrellis ? Av1CoeffEncode.LastTrellisEob : -2;
                        if (TimingOn) Tick(3, ref tq);
                        if (StatsOn) System.Threading.Interlocked.Increment(ref StatRdoq);
                        pre = true;
                    }
                    if (TimingOn) Tick(7, ref tq);
                    double sse = txDom ? TxDist(cf, qf) : ReconSseCandRect(cf, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, inv);
                    if (TimingOn) Tick(4, ref tq);
                    if (CalOn && !oneD)
                    {
                        double cd = 0;
                        for (int k = 0; k < sScan; k++) { double e = (qf[k] - cf[k]) * (k == 0 ? c.DcDq : c.AcDq); cd += e * e; }
                        lock (CalPix) { CalPix[stx] += sse; CalCoef[stx] += cd; CalN[stx]++; }
                    }
                    if (sse >= bestJ) { lvPool.Return(cf); continue; }
                    if (TimingOn) Tick(7, ref tq);
                    double bits = preBits >= 0 ? preBits : oneD
                        ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, stx, yModeNoFilt, inv, cf, skc, snc)
                        : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, cf, skc, snc, idx, fullSet: UseFullIntraTxSet,
                            eobHint: pre || olv ? -2 : cfEob);
                    if (TimingOn) Tick(5, ref tq);
                    double j = sse + lambda * bits;
                    bool rdoqd = pre;
                    if (!pre && trellis && !oneD && (cfEob == -2 ? HasNonZero(cf) : cfEob >= 0) && j < bestJ * Sp.RdoqSearchMargin)
                    {
                        double qb = Quantise(cf, qf, idx, cfEob);
                        candEob = Sp.AomTrellis ? Av1CoeffEncode.LastTrellisEob : -2;
                        if (StatsOn) System.Threading.Interlocked.Increment(ref StatRdoq);
                        bits = qb >= 0 ? qb : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, cf, skc, snc, idx, fullSet: UseFullIntraTxSet);
                        j = (txDom ? TxDist(cf, qf) : ReconSseCandRect(cf, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, inv)) + lambda * bits;
                        rdoqd = true;
                    }
                    if (j < bestJ)
                    {
                        lvPool.Return(bestCf);
                        bestJ = j; bestBits = bits; bestCf = cf; bestInv = inv; bestIdx = idx; bestRdoq = rdoqd; bestEob = olv ? -2 : candEob;
                        if (!rdoqd) (qf, qfBest) = (qfBest, qf);   // keep this type's unquantised coefficients (no copy)
                    }
                    else lvPool.Return(cf);
                }
                // the coded levels are RDOQ'd (libaom's final encode trellises every block)
                if (UseRdoq && (Sp.AomTrellisAll || trellis) && !bestRdoq && (OracleLevels == null || OracleLevels1DOnly) && bestInv != Av1TxType.VDct && bestInv != Av1TxType.HDct && (bestEob != -2 ? bestEob >= 0 : HasNonZero(bestCf)))
                {
                    double qb = Quantise(bestCf, qfBest, bestIdx, bestEob);
                    bestEob = Sp.AomTrellis ? Av1CoeffEncode.LastTrellisEob : -2;
                    double bits = qb >= 0 ? qb : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, bestCf, skc, snc, bestIdx, fullSet: UseFullIntraTxSet);
                    bestBits = bits;
                    if (!txDom) bestJ = ReconSseCandRect(bestCf, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, bestInv) + lambda * bits;
                }
                if (TimingOn) tq0 = System.Diagnostics.Stopwatch.GetTimestamp();
                byte cfc = DequantAndReconstructPredRect(bestCf, stx, tw, th, c.DcDq, c.AcDq, pred, c.ReconY, c.W, px, py, bestInv, bestEob);
                if (TimingOn) Tick(6, ref tq0);
                if (txDom)
                {
                    // the winner's pixel distortion from its reconstruction (calc_pixel_domain_distortion_final)
                    long psse = SseU16(c.ReconY, py * c.W + px, c.W, c.Luma, py * c.W + px, c.W, tw, th);
                    bestJ = psse + lambda * bestBits;
                }
                list.Add((bestCf, bestInv, bestIdx, skc, snc, px, py));
                for (int i = 0; i < tw4 && txR + i < 32; i++) c.ALY[txR + i] = cfc;
                for (int j = 0; j < th4 && tyR + j < 32; j++) c.LLY[tyR + j] = cfc;
                jSum += bestJ;
                if (jSum > jLimit)
                {
                    foreach (var t in list) lvPool.Return(t.Cf);
                    ReturnTxbList(list);
                    return (double.PositiveInfinity, null);
                }
            }
        return (jSum, list);
    }

    // Per-thread pool of the tx-block lists LumaUniformTx hands out (a losing candidate's list comes back cleared).
    [ThreadStatic] private static List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>[]? t_txbPool;
    [ThreadStatic] private static int t_txbPoolN;
    private static List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)> RentTxbList(int capacity)
    {
        if (t_txbPool != null && t_txbPoolN > 0)
        {
            var l = t_txbPool[--t_txbPoolN];
            if (l.Capacity < capacity) l.Capacity = capacity;
            return l;
        }
        return new List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>(capacity);
    }
    private static void ReturnTxbList(List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)> l)
    {
        l.Clear();
        t_txbPool ??= new List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>[32];
        if (t_txbPoolN < t_txbPool.Length) t_txbPool[t_txbPoolN++] = l;
    }

    // residual = source - prediction for a tw x th block at (px, py) (res row-major, stride tw); returns its SSE.
    private static long ResidualSse<TP>(TP[] src, int stride, int px, int py, TP[] pred, int[] res, int tw, int th) where TP : unmanaged
    {
        long sse = 0;
        if (tw >= 8)
        {
            for (int yy = 0; yy < th; yy++)
            {
                ref TP s0 = ref src[(py + yy) * stride + px];
                ref TP p0 = ref pred[yy * tw];
                ref int r0 = ref res[yy * tw];
                var acc = Vector256<int>.Zero;
                for (int xx = 0; xx < tw; xx += 8)
                {
                    var d = Px.Load8x32(ref Unsafe.Add(ref s0, xx)) - Px.Load8x32(ref Unsafe.Add(ref p0, xx));
                    d.StoreUnsafe(ref r0, (nuint)xx);
                    acc += d * d;
                }
                sse += SumInt(acc);
            }
            return sse;
        }
        for (int yy = 0; yy < th; yy++)
            for (int xx = 0; xx < tw; xx++)
            {
                int d = Px.I(src[(py + yy) * stride + px + xx]) - Px.I(pred[yy * tw + xx]);
                res[yy * tw + xx] = d; sse += (long)d * d;
            }
        return sse;
    }

    // SSE of a w x h block of two sample planes (differences within 16 bits at <= 12-bit depth): 16 / 8 lanes through
    // pmaddwd, widened to 64 bits every 8 rows (each 32-bit lane then holds <= 8 * 4 * 2 * 4095^2 < 2^31).
    internal static long SseU16<TP>(TP[] a, int aOff, int aStride, TP[] b, int bOff, int bStride, int w, int h) where TP : unmanaged
    {
        long sse = 0;
        if (!Avx2.IsSupported || w < 8)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) { int d = Px.I(a[aOff + y * aStride + x]) - Px.I(b[bOff + y * bStride + x]); sse += (long)d * d; }
            return sse;
        }
        if ((long)aOff + (long)(h - 1) * aStride + w > a.Length || (long)bOff + (long)(h - 1) * bStride + w > b.Length || aOff < 0 || bOff < 0)
            throw new ArgumentOutOfRangeException(nameof(w));
        ref TP pa = ref MemoryMarshal.GetArrayDataReference(a), pb = ref MemoryMarshal.GetArrayDataReference(b);
        for (int y = 0; y < h;)
        {
            var acc = Vector256<int>.Zero;
            for (int yEnd = Math.Min(h, y + 8); y < yEnd; y++)
            {
                ref TP ra = ref Unsafe.Add(ref pa, aOff + y * aStride), rb = ref Unsafe.Add(ref pb, bOff + y * bStride);
                int x = 0;
                for (; x + 16 <= w; x += 16)
                {
                    var d = Px.Load16(ref Unsafe.Add(ref ra, x)).AsInt16() - Px.Load16(ref Unsafe.Add(ref rb, x)).AsInt16();
                    acc += Avx2.MultiplyAddAdjacent(d, d);
                }
                if (x + 8 <= w)
                {
                    var d = Px.Load8(ref Unsafe.Add(ref ra, x)).AsInt16() - Px.Load8(ref Unsafe.Add(ref rb, x)).AsInt16();
                    acc += Vector256.Create(Sse2.MultiplyAddAdjacent(d, d), Vector128<int>.Zero);
                    x += 8;
                }
                for (; x < w; x++) { int d = Px.I(Unsafe.Add(ref ra, x)) - Px.I(Unsafe.Add(ref rb, x)); sse += (long)d * d; }
            }
            sse += SumInt(acc);
        }
        return sse;
    }

    // dev oracle: the first n samples of a (ushort) and b equal
    private static bool SameSamples<TP>(ushort[] a, TP[] b, int n) where TP : unmanaged
    {
        for (int i = 0; i < n; i++) if (a[i] != Px.I(b[i])) return false;
        return true;
    }

    // sum of the 8 non-negative lanes (each < 2^31) as a long
    private static long SumInt(Vector256<int> v)
    {
        var lo = Vector256.WidenLower(v.AsUInt32()).AsInt64() + Vector256.WidenUpper(v.AsUInt32()).AsInt64();
        return Vector256.Sum(lo);
    }

    // transform-domain distortion sum ((q - L) * dq)^2 / 64 (dc dequantiser at 0), 4 lanes at a time
    private static double TxDistV(int[] lv, double[] q, int n, double dcDq, double acDq)
    {
        ref int l0 = ref MemoryMarshal.GetArrayDataReference(lv);
        ref double q0 = ref MemoryMarshal.GetArrayDataReference(q);
        var acc = Vector256<double>.Zero;
        var dq = Vector256.Create(dcDq, acDq, acDq, acDq);
        var ac = Vector256.Create(acDq);
        int k = 0;
        for (; k + 4 <= n; k += 4)
        {
            var lvd = Avx.IsSupported ? Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref l0, (nuint)k))
                : Vector256.Create((double)Unsafe.Add(ref l0, k), Unsafe.Add(ref l0, k + 1), Unsafe.Add(ref l0, k + 2), Unsafe.Add(ref l0, k + 3));
            var e = (Vector256.LoadUnsafe(ref q0, (nuint)k) - lvd) * (k == 0 ? dq : ac);
            acc += e * e;
        }
        double d = Vector256.Sum(acc);
        for (; k < n; k++) { double e = (q[k] - lv[k]) * (k == 0 ? dcDq : acDq); d += e * e; }
        return d * (1.0 / 64);
    }

    // Hadamard model cost of a prediction (intra_model_rd): SATD over 8x8 tiles, 4x4 where a side is 4.
    private static long AomModelRd<TP>(ColorPartCtx<TP> c, int bx, int by, TP[] pred, int w, int h) where TP : unmanaged
    {
        if (w >= 8 && h >= 8) return Satd8x8Rect(c.Luma, c.W, bx, by, pred, w, h);
        long satd = 0;
        Span<int> d = stackalloc int[16];
        for (int y0 = 0; y0 < h; y0 += 4)
            for (int x0 = 0; x0 < w; x0 += 4)
            {
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                        d[yy * 4 + xx] = Px.I(c.Luma[(by + y0 + yy) * c.W + bx + x0 + xx]) - Px.I(pred[(y0 + yy) * w + x0 + xx]);
                for (int r = 0; r < 4; r++)
                {
                    int a0 = d[r * 4] + d[r * 4 + 1], a1 = d[r * 4] - d[r * 4 + 1], a2 = d[r * 4 + 2] + d[r * 4 + 3], a3 = d[r * 4 + 2] - d[r * 4 + 3];
                    d[r * 4] = a0 + a2; d[r * 4 + 1] = a1 + a3; d[r * 4 + 2] = a0 - a2; d[r * 4 + 3] = a1 - a3;
                }
                for (int col = 0; col < 4; col++)
                {
                    int a0 = d[col] + d[4 + col], a1 = d[col] - d[4 + col], a2 = d[8 + col] + d[12 + col], a3 = d[8 + col] - d[12 + col];
                    satd += Math.Abs(a0 + a2) + Math.Abs(a1 + a3) + Math.Abs(a0 - a2) + Math.Abs(a1 - a3);
                }
            }
        return satd;
    }

    // av1_rd_pick_intra_sby_mode for one luma leaf. On return ReconY / ALY / LLY hold the winner's reconstruction and
    // coefficient contexts. ymCdf context and the filter-intra / angle eligibility are the caller's.
    private static LumaPick AomLumaSearch<TP>(ColorPartCtx<TP> c, int lumaBs, int lumaTx, int bx4, int by4, int w4, int h4,
        Av1EdgeFlags edgeFlags, int intraFlags, bool angleOk, bool fiOk) where TP : unmanaged
    {
        int w = w4 * 4, h = h4 * 4, bx = bx4 * 4, by = by4 * 4, bxR = bx4 & 31, byR = by4 & 31;
        double lambda = LamK * c.AcDq * c.AcDq;
        int ymA = Av1Tables.IntraModeContext[c.AModeY[bxR]], ymL = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var fullTD = Av1Tables.TxfmDimensions[lumaTx];
        int fullMax = fullTD.Max;
        int txCtx = (c.LTxY[byR] >= fullTD.Lh ? 1 : 0) + (c.ATxY[bxR] >= fullTD.Lw ? 1 : 0);
        bool txSelect = UseColorTxDepth && fullMax > (byte)Av1TxSize.Tx4x4;
        // choose_tx_size_type_from_rd: depths init..MAX_TX_DEPTH (2) from the largest rect tx, stopping at 4x4
        int initDepth = w4 == h4 ? Sp.AomTxInitDepthSqr : Sp.AomTxInitDepthRect;
        // dev oracle: this block's mode / angle / tx size as another encoder chose them
        (byte Bs, byte YMode, sbyte YAngle, byte Tx) om = default;
        bool orc = OracleModes != null && OracleModes.TryGetValue(bx4 | (by4 << 16), out om) && om.Bs == lumaBs;
        if (orc) initDepth = 0;
        if (!txSelect) initDepth = 2;
        var sizes = new int[3];
        int nSizes = 0;
        for (int d = initDepth, t = lumaTx; d <= 2; d++, t = Av1Tables.TxfmDimensions[t].Sub)
        {
            sizes[nSizes++] = t;
            if (t == (int)Av1TxSize.Tx4x4) break;
        }
        // x->source_variance (per-pixel variance of the source block) for the low-contrast depth prune
        long sum = 0, sq = 0;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++) { int v = Px.I(c.Luma[(by + yy) * c.W + bx + xx]); sum += v; sq += (long)v * v; }
        double srcVar = ((double)sq - (double)sum * sum / (w * h)) / (w * h) / (1 << (2 * (Bd - 8)));
        double thr = Sp.AomTrellisMseThr;

        var saveA = c.ALY.AsSpan(bxR, Math.Min(w4, 32 - bxR)).ToArray();
        var saveL = c.LLY.AsSpan(byR, Math.Min(h4, 32 - byR)).ToArray();
        void Restore() { saveA.CopyTo(c.ALY.AsSpan(bxR)); saveL.CopyTo(c.LLY.AsSpan(byR)); }

        // pick_sb_modes' rd budget: the partition candidate's remaining J (the luma J alone must stay below it)
        var best = new LumaPick { J = Sp.AomPartAbort ? PartRemaining(c) : double.MaxValue };
        // winner-mode processing (enable_winner_mode_for_tx_size_srch, libaom s4+): modes compared at the largest tx,
        // the full tx-size search then only for the winner
        bool winnerTx = Sp.AomWinnerTxSize && nSizes > 1;
        int kEnd = winnerTx ? 1 : nSizes, kStart = 0;
        double bestModeBits = 0;
        // one mode: the tx-size loop (uniform_txfm_yrd per size), J including mode + tx_size bits
        if (StatsOn) System.Threading.Interlocked.Increment(ref StatSearch);
        void TryMode(Av1IntraPredMode m, int dl, int nf, double modeBits)
        {
            if (StatsOn) System.Threading.Interlocked.Increment(ref StatModes);
            Span<double> rd = stackalloc double[3];
            rd.Fill(double.MaxValue);
            for (int k = kStart; k < kEnd; k++)
            {
                if (orc && txSelect && sizes[k] != om.Tx) continue;
                int codedDepth = k;   // sizes[k] is k splits below the largest tx (the coded tx_depth)
                double extra = lambda * (modeBits + (txSelect ? Av1CoeffEncode.SymBits(c.Cdf.GetTxSzCdf(fullMax - 1, txCtx), codedDepth) : 0));
                double limit = best.J - extra;
                var (j, txb) = LumaUniformTx(c, lumaBs, sizes[k], bx4, by4, w4, h4, m, dl, nf, edgeFlags, intraFlags, limit, thr);
                Restore();
                if (txb == null) { rd[Math.Min(k, 2)] = double.MaxValue; }
                else
                {
                    double jt = j + extra;
                    rd[Math.Min(k, 2)] = jt;
                    if (jt < best.J)
                    {
                        if (best.Txb != null) { foreach (var t in best.Txb) Av1FwdTransform.ReturnLevels(t.Cf); ReturnTxbList(best.Txb); }
                        best = new LumaPick { Mode = m, Delta = dl, ModeNoFilt = nf, Depth = codedDepth, Tx = sizes[k], J = jt, Txb = txb };
                        bestModeBits = modeBits;
                    }
                    else { foreach (var t in txb) Av1FwdTransform.ReturnLevels(t.Cf); ReturnTxbList(txb); }
                }
                // prune the smallest size on low-contrast blocks when splitting once already lost
                if (k > 0 && initDepth + k != 2 && srcVar < 256 && rd[k - 1] != double.MaxValue && rd[k] > rd[k - 1]) break;
            }
        }

        // reuse_best_prediction_for_part_ab: only the cached winner's mode (all its angle deltas; filter intra only if
        // it won with filter intra)
        int ck = lumaBs * 256 + (by4 & 15) * 16 + (bx4 & 15), sbId = (bx4 >> 4) | ((by4 >> 4) << 16);
        int cached = !orc && c.UseAomModeCache && c.AomModeCacheSb[ck] == sbId ? c.AomModeCache[ck] : 0;
        bool cacheFilter = (cached >> 16 & 1) != 0;
        var cacheMode = (Av1IntraPredMode)(cached >> 8 & 0xFF);
        int cacheFm = (cached & 0xFF) - 8;
        var ymCdf = c.Cdf.GetKfYModeCdf(ymA, ymL);
        double AngleBits(Av1IntraPredMode m, int dl) => IsDirectional(m) && angleOk
            ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)m - (int)Av1IntraPredMode.Vertical), dl + 3) : 0;
        double fiZero = fiOk ? Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs), 0) : 0;
        uint hogMask = HogSkipMask(c.Luma, c.W, bx, by, w, h, c.Bw4 * 4, c.Bh4 * 4, Sp.HogLevel);
        int topK = Math.Max(1, Sp.AomTopIntraModelCount);
        Span<long> top = stackalloc long[8];
        top.Fill(long.MaxValue);
        long bestModel = long.MaxValue;
        var pred = (PxScratch<TP>.Aom ??= new LeafScratch<TP>()).P2;
        int nModes = 13 + (angleOk ? 48 : 0);
        for (int mi = 0; mi < nModes; mi++)
        {
            Av1IntraPredMode m; int dl;
            if (mi < 13) { m = AomModeOrder[mi]; dl = 0; }
            else { int r = mi - 13; m = (Av1IntraPredMode)(1 + r / 6); int e = r % 6; dl = e < 3 ? e - 3 : e - 2; }
            if (orc) { if ((int)m != om.YMode || dl != om.YAngle) continue; }
            else if (Sp.AomDisableSmoothHV && (m == Av1IntraPredMode.SmoothH || m == Av1IntraPredMode.SmoothV)) continue;
            if (cached != 0 && m != (cacheFilter ? Av1IntraPredMode.Dc : cacheMode)) continue;
            if (!orc && IsDirectional(m) && cached == 0 && (hogMask >> (int)m & 1) != 0) continue;
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, m, dl, pred, edgeFlags, intraFlags);
            long model = AomModelRd(c, bx, by, pred, w, h);
            // prune_intra_y_mode: insert into the running top-k, prune if outside it or > 1.5x the best
            for (int i = 0; i < topK; i++)
                if (model < top[i]) { for (int j = topK - 1; j > i; j--) top[j] = top[j - 1]; top[i] = model; break; }
            if (!orc && top[topK - 1] != long.MaxValue && model > top[topK - 1]) continue;
            if (!orc && bestModel != long.MaxValue && model > 1.5 * bestModel) continue;
            if (model < bestModel) bestModel = model;
            double mb = Av1CoeffEncode.SymBits(ymCdf, (int)m) + AngleBits(m, dl) + (m == Av1IntraPredMode.Dc ? fiZero : 0);
            TryMode(m, dl, (int)m, mb);
        }

        // rd_pick_filter_intra_sby: the (pruned) filter modes, model-pruned at 1.25x the best model cost
        if (fiOk && (best.Txb != null || orc) && (cached == 0 || cacheFilter) && (!orc || om.YMode == (byte)Av1IntraPredMode.Filter))
        {
            var fiCdf = c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs);
            double flag = Av1CoeffEncode.SymBits(fiCdf, 1) + Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc);
            int fiMask = orc ? 0x1F : Sp.FilterIntraPrune ? FilterIntraModesFor(best.Mode) : 0x1F;
            for (int fm = 0; fm < 5; fm++)
            {
                if ((fiMask >> fm & 1) == 0) continue;
                if (cached != 0 && fm != cacheFm) continue;
                if (orc && fm != om.YAngle) continue;
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, Av1IntraPredMode.Filter, fm, pred, edgeFlags, intraFlags);
                long model = AomModelRd(c, bx, by, pred, w, h);
                if (!orc && bestModel != long.MaxValue && model > bestModel + (bestModel >> 2)) continue;
                if (model < bestModel) bestModel = model;
                TryMode(Av1IntraPredMode.Filter, fm, Av1Tables.FilterModeToYMode[fm],
                    flag + Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraModeCdf(), fm));
            }
        }

        if (winnerTx && best.Txb != null)
        {
            // the winner's split sizes (TryMode keeps whichever size beats its current J)
            kStart = 1; kEnd = nSizes;
            TryMode(best.Mode, best.Delta, best.ModeNoFilt, bestModeBits);
        }

        // leave the winner's reconstruction + coefficient contexts in place (deterministic re-run of its size)
        if (best.Txb != null)
        {
            bool fw = best.Mode == Av1IntraPredMode.Filter;
            c.AomModeCache[ck] = (1 << 20) | ((fw ? 1 : 0) << 16) | ((fw ? 0 : (int)best.Mode) << 8) | (best.Delta + 8);
            c.AomModeCacheSb[ck] = sbId;
            foreach (var t in best.Txb) Av1FwdTransform.ReturnLevels(t.Cf);
            ReturnTxbList(best.Txb);
            best.Txb = LumaUniformTx(c, lumaBs, best.Tx, bx4, by4, w4, h4, best.Mode, best.Delta, best.ModeNoFilt,
                edgeFlags, intraFlags, double.MaxValue, thr).Txb;
        }
        return best;
    }
}
