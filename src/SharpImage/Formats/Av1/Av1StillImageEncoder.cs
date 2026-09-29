// Minimal AV1 still-image (AVIF codestream) encoder — step 2 of the AV1 encoder marathon. Produces a single
// key frame of one 64x64 monochrome superblock: PARTITION_NONE, DC intra prediction, skip=1 (zero residual).
// The decoded result is a flat DC-predicted plane (128 for 8-bit with no neighbors). This proves the full
// pipeline — OBU framing, uncompressed headers (Av1ObuWriter), and MSAC tile coding (Av1MsacWriter) driven by
// the real default CDFs and context derivation — round-trips through our Av1Decoder. Larger frames, real
// residual, and mode decisions come in later steps.
using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

/// <summary>Search-effort settings of the still-image encoder (one set per encode; see Av1StillImageEncoder.t_speed).</summary>
internal sealed class Av1EncodeSpeed
{
    public bool UseRd = true;
    public bool UseDeblockSearch = true;
    public bool UseCdefSearch = true;
    public bool UseFilterIntra = true;
    public bool UseChromaRdoq = true;
    public bool UseRectPartition = true;
    public bool UseSub8Partition = true;
    public bool UseTrueRd = true;
    public bool UseCfl = true;
    public bool UseColorTxDepth = true;
    /// <summary>Deepest luma tx split searched with UseColorTxDepth (libaom allintra: 1, i.e. full and half size).</summary>
    public int ColorTxMaxDepth = 2;
    // Largest frame the trial-encode partition search runs on (the fast estimate above it). Unlimited since row-parallel
    // encoding: hato 3082x2048 at speed 3/4/5 takes 4.5 / 4.0 / 3.5 s vs avifenc 10.9 / 6.8 / 3.3 s.
    public long TrueRdPixelBudget = long.MaxValue;
    public double EarlyTermBits = 8.0;
    // Off: the sequence-level intra edge filter measured -0.60% (speed 0) / -0.62% (speed 6) BD when disabled on the
    // encoder corpus (piechart -3%), and it made wider mode searches lose (a SMOOTH choice re-filters its neighbours'
    // edges, which the per-block RD cannot see). libaom gains ~0 from it.
    public bool UseIntraEdgeFilter = false;
    public int RdModeCandidates = 16;
    public bool UseUvModeSearch = true;
    public int RdUvCandidates = 13;
    public bool UseFullIntraTxSet = true;
    public bool UseExtPartition = true;
    // HORZ_4 / VERT_4 at 16x16 (16x4 / 4x16 strips; libaom codes them heavily on photos at its slowest speed)
    public bool Part4At16;
    // tx_depth 1 trial in the rect leaf (4:2:0 rect / strips, every 4:2:2 / 4:4:4 leaf); needs UseColorTxDepth
    public bool RectTxDepth;
    // RectTxDepth: how many further prescreened modes are also tried on the split transform (0 = the winner only)
    public int RectTxDepthAlt;
    // 4:4:4 / 4:2:2 encodes switch on UseColorTxDepth + RectTxDepth (LayoutSpeedScope)
    public bool TxSplit444422;
    // libaom's slow-speed luma search in the rect leaf (Av1StillImageEncoder.LumaSearch.cs) and its speed features:
    // top_intra_model_count_allowed, intra_tx_size_search_init_depth_sqr / _rect, the coeff_opt_thresholds MSE
    // threshold for RDOQ in the tx-type loop, use_reduced_intra_txset, disable_smooth_intra (SMOOTH_H / V).
    public bool LibaomLuma;
    public int AomTopIntraModelCount = 4, AomTxInitDepthSqr = 1, AomTxInitDepthRect;
    public double AomTrellisMseThr = 3200;
    public bool AomReducedIntraTxSet = true, AomDisableSmoothHV;
    // libaom's single-pass trellis (Av1CoeffEncode.TrellisOptimize) for the libaom luma search's RDOQ
    public bool AomTrellis;
    // trellis before the rate / distortion of each tx type (libaom search_tx_type); winner trellised even above the MSE gate
    public bool AomTrellisFirst, AomTrellisAll = true;
    // tx-type loop distortion in the transform domain, the winner's from its reconstruction (tx_domain_dist_level)
    public bool AomTxDomainDist;
    // reuse_best_prediction_for_part_ab (libaom s1+): AB-partition leaves search only the mode that block size won
    // at that position in the NONE / HORZ / VERT / SPLIT evaluations
    public bool AomPartAbReuse;
    // rd_try_subblock: a partition candidate stops once its sub-blocks' J reaches the best candidate's; each luma
    // search starts from the remaining budget
    public bool AomPartAbort;
    // less_rectangular_check_level 1 (needs AomPartAbort) and prune_ext_partition_types_search_level 1 (AB shapes)
    public bool AomLessRectCheck, AomPruneAb;
    /// <summary>libaom prune_ext_part_using_split_info 1 (prune_4_partition_using_split_info): HORZ_4 / VERT_4 only when
    /// enough of the SPLIT sub-blocks' own HORZ / VERT searches won (or were not searched).</summary>
    public bool AomPrune4Split;
    /// <summary>T-shape / 4-way partitions for 4:4:4 / 4:2:2 only (LayoutSpeedScope) when UseExtPartition is off.</summary>
    public bool ExtNon420;
    // the libaom luma search also for 64-wide / 64-tall leaves (64-point transforms: pixel-domain distortion)
    public bool AomLuma64;
    // dev: lambda from libaom's rdmult(qindex) times this factor (0 = RdLambdaK * acDq^2)
    public double LambdaLibaom;
    // the lambda constant LambdaLibaom derived for the frame being coded (set per encode on a copy; 0 = RdLambdaK)
    public double LambdaKFrame;
    // libaom intra_sb_rdmult_modifier: superblocks mixing flat and busy 4x4s code at up to 37.5% lower lambda
    public bool AomSbLambda;
    // libaom's chroma search in the rect leaf: every chroma candidate trellised and priced exactly; CfL alphas RD-searched
    // per plane within AomCflRange of the SSE estimate (cfl_search_range 3 = +-2)
    public bool AomChroma;
    public int AomCflRange = 2;
    // dev: luma trellis lambda = factor x libaom's trellis lambda for this qindex (0 = RdoqLambdaScale x ours)
    public double AomTrellisLam;
    // round-to-nearest quantisation (libaom's FP quantiser before its trellis) and the chroma trellis scale
    public bool AomRoundNearest;
    public double AomChromaLam;
    // RDOQ scale for the non-libaom-search paths (0 = RdoqLambdaScale)
    public double AomRdoqScale;
    // winner-mode tx-size search (libaom s4+): mode decision at the largest tx, splits only for the winner
    public bool AomWinnerTxSize;
    // speed 4 in 4:4:4 / 4:2:2 (LayoutSpeedScope): the libaom luma search in its speed-4 form (winner-mode tx size,
    // 2 model candidates) with the lambda balance and chroma search of speeds 0-3
    public bool AomS4Non420;
    // speeds 5 / 6 in 4:4:4 (LayoutSpeedScope): libaom's lambda balance; 5 adds the chroma search and drops loop
    // restoration (libaom has none from speed 5), 6 keeps LR with coarser statistics
    public int Aom444Tier;
    // 4:2:0 / mono sub-8x8 leaves (8x4 / 4x8 / 4x4) through the rect leaf, i.e. the libaom luma / chroma searches
    public bool AomSub8Rect;
    // search_tx_type early exits: adaptive_txb_search_level (0 off, 1 s0, 2 s1+) and skip_tx_search (s1+)
    public int AomAdaptiveTxb;
    public bool AomSkipTxSearch;
    /// <summary>Angle deltas searched for the directional intra modes: 1 = all of -3..3, 0 = none (delta 0 only),
    /// 2 = {-2, 0, 2}, 3 = {-3, 0, 3}, 4 = {-3, -1, 0, 1, 3}.</summary>
    public int AngleDeltaSet = 1;
    // The candidate (mode, delta) list, rebuilt whenever AngleDeltaSet changes (however it is set).
    private (Av1IntraPredMode Mode, int Delta)[]? candidates; private int candidatesSet = -1;
    public (Av1IntraPredMode Mode, int Delta)[] Candidates
        => candidatesSet == AngleDeltaSet && candidates != null ? candidates
            : (candidates = Av1StillImageEncoder.BuildCandidates(AngleDeltas(candidatesSet = AngleDeltaSet)));
    internal static int[] AngleDeltas(int set) => set switch
    {
        0 => [0], 2 => [-2, 0, 2], 3 => [-3, 0, 3], 4 => [-3, -1, 0, 1, 3], _ => [-3, -2, -1, 0, 1, 2, 3],
    };
    public void SetAngleDeltas(int set) => AngleDeltaSet = set;
    public bool UseTxTypeSearch = true;
    public bool UseRdoq = true;
    /// <summary>RDOQ every candidate inside the leaf mode / tx-type search (as libaom optimizes coefficients in its RD
    /// loop), so the choice is made on the coefficients that will be coded; otherwise only the winner is optimized.</summary>
    public bool RdoqInSearch = true;
    /// <summary>With RdoqInSearch: optimize only candidates whose plain J is within this factor of the best optimized J
    /// so far (the others cannot win once RDOQ lowers the winner's cost).</summary>
    public double RdoqSearchMargin = 1.25;
    /// <summary>Price chroma candidates (UV modes, CfL) with the CDF-based coefficient estimate and the exact CfL
    /// alpha symbols, like luma, instead of the flat per-coefficient proxy.</summary>
    public bool ChromaRateExact = true;
    /// <summary>RDOQ each chroma candidate (DC, UV modes, CfL) inside the UV decision, so it is priced on the levels
    /// that will be coded (square leaves).</summary>
    public bool ChromaRdoqInSearch;
    /// <summary>PARTITION_SPLIT at 8x8: four 4x4 luma blocks (4:2:0 chroma on the last), for fine detail.</summary>
    public bool UseSplit4x4 = true;
    /// <summary>Per-superblock CDEF: a strength set (1..8 luma/chroma pairs) and each 64x64's index, searched on the
    /// deblocked picture (Av1CdefSearch), instead of one frame-wide strength. 4:2:0 colour.</summary>
    public bool UseCdefPerSb = true;
    /// <summary>Sub-8x8 leaves' chroma searches the UV modes (else DC / CfL only).</summary>
    public bool UseSub8UvSearch = true;
    /// <summary>Trial-encode partition search pruned by the fast estimate (EstCost): when > 0, SPLIT is not tried if its
    /// estimate exceeds NONE's by this factor, and NONE (with the rect shapes) not if it exceeds SPLIT's by it.</summary>
    public double TrueRdPruneMargin;
    /// <summary>Row-parallel tiles (libaom row-mt): superblock rows are decided in a wavefront (two superblocks behind
    /// the row above), each on its own CDF copy seeded from the row above after its second superblock; the tile is then
    /// coded by replaying every row's symbols in order against the real adaptive CDFs. The output does not depend on
    /// the thread count.</summary>
    public bool RowMt = true;
    /// <summary>Per-superblock CDEF search breadth (Av1CdefSearch.Codes: 0 = all 64 strengths .. 4 = 4).</summary>
    public int CdefSearchLevel;
    /// <summary>Luma modes RD-evaluated per sub-8x8 leaf, the best by prediction SAD (13 = every base mode).</summary>
    public int Sub8ModeCandidates = 13;
    /// <summary>libaom fast_intra_tx_type_search: rank the luma mode candidates with DCT_DCT only, then search the
    /// other transform types for the winning mode alone.</summary>
    public bool FastIntraTxType;
    /// <summary>Partition levels (1 = 64x64 .. 4 = 8x8) decided by trial encodes; the others use the fast estimate.</summary>
    public int TrueRdFromBl = 1, TrueRdToBl = 4;
    /// <summary>Coarse-to-fine luma mode prescreen (libaom prunes delta angles around the best base modes): 0 = every
    /// (mode, delta) candidate; N = the delta-0 modes first, then the other deltas of the N best directional modes.</summary>
    public int AngleRefineTop;
    /// <summary>Monochrome images and alpha planes go through the colour encoder with its chroma switched off (every
    /// partition / mode / filter tool, row-parallel), instead of the older grey-only encoder.</summary>
    public bool MonoColorPath = true;
    /// <summary>libaom's per-frame screen-content detection: when it fires, palette mode is searched and signalled.</summary>
    public bool UseScreenContentDetection = true;
    /// <summary>Screen detection on every other 16x16 block (libaom all-intra speed >= 3).</summary>
    public bool FastScreenDetection;
    /// <summary>libaom default_max_partition_size = BLOCK_32X32 (allintra speed 6): 64x64 superblocks are always split.</summary>
    public bool MaxPartition32;
    /// <summary>libaom intra_pruning_with_hog / chroma_intra_pruning_with_hog levels (0 = off, 1..4): directional modes
    /// the HOG model scores low are not searched.</summary>
    public int HogLevel = 1, HogChromaLevel;
    /// <summary>libaom intra_cnn_based_part_prune_level for camera content / screen content (0 off; 1 = prune SPLIT
    /// only; 2 = also force SPLIT, pruning NONE and the rectangular shapes).</summary>
    public int CnnPruneLevel, CnnPruneLevelScreen;
    /// <summary>CfL alpha search: 0 = all 33 alphas; n > 0 = only the least-squares alpha +- n (libaom cfl_search_range).</summary>
    public int CflSearchRange;
    /// <summary>libaom allintra (every speed): a 16x16+ block holding a near-flat 4x4 (log variance &lt; 0.272) and a much
    /// busier one (spread &gt; 3) is not coded whole (NONE pruned), so ringing does not spread into the flat part.</summary>
    public bool ForceSplitVar;
    /// <summary>RDOQ level-down trials that would need more than this many bits of saving are skipped (0 = all).</summary>
    public double RdoqSkipBits;
    /// <summary>The luma winner's mode is always a chroma RD candidate (libaom prune_chroma_modes_using_luma_winner keeps
    /// it); RectScreenContent: rectangular partitions stay searched for screen content when UseRectPartition is off
    /// (libaom prunes rect by qindex only for camera content).</summary>
    public bool UvLumaWinner = true, RectScreenContent;
    /// <summary>libaom VAR_BASED_PARTITION for key frames (allintra speed 7+) in the estimate path: the variance of 4x4
    /// source averages decides NONE / SPLIT per 32x32 / 16x16 block against q-scaled thresholds (64x64 always split,
    /// 8x8 the minimum) — no transforms.</summary>
    public bool VarPartition;
    /// <summary>Joint tx-size search: at the split depths the next-best prescreened luma modes (up to this many) are
    /// also tried, each paying its own mode symbol, so a mode that only wins with smaller transforms can be chosen
    /// (libaom searches tx size inside the mode decision).</summary>
    public int TxDepthAltModes;
    /// <summary>libaom nonrd intra mode masks (allintra speed 8+, intra_y_mode_bsize_mask_nrd): DC / H / V below 32x32,
    /// DC only from 32x32 — for the partition estimate (EstimateNrdModes) and the leaf mode prescreen (LeafNrdModes).</summary>
    public bool EstimateNrdModes, LeafNrdModes;
    /// <summary>Partition estimate from the best mode's SATD (+ the header at the SATD rate weight), without the
    /// forward / inverse transform of the estimate block.</summary>
    public bool EstimateSatdOnly;
    /// <summary>Partition estimate distortion from the quantisation error of the kept coefficients ((qf - L) * dq, the
    /// pixel-domain error as RDOQ counts it) instead of an inverse transform + reconstruction; up to 32x32.</summary>
    public bool EstimateCoefDist;
    // (qf - L) * dq is in dequantised-coefficient units, 64x the pixel-domain SSE (scoreboard-calibrated; 1/32 loses 7%).
    public double EstimateCoefDistScale = 1.0 / 64;
    /// <summary>Luma palette sizes are searched largest first; stop at the first size that does not improve on the
    /// previous (libaom prune_palette_search_level).</summary>
    public bool PaletteEarlyStop;
    /// <summary>2 = libaom prune_palette_search_level 2 luma palette size order (ascending until no gain, then
    /// descending), with its header-rd gating.</summary>
    public int PaletteSearchLevel;
    /// <summary>libaom prune_filter_intra_level 1: only FILTER_DC and the filter mode matching the best regular mode so
    /// far (V, H, D157, Paeth) are tried.</summary>
    public bool FilterIntraPrune;
    /// <summary>Deblocking guess from libaom's LPF_PICK_FROM_Q fit (else qindex / 8).</summary>
    public bool LfGuessLibaom;
    /// <summary>Deblock-only level search picks luma / U / V levels separately (deblocking is per plane, so one decode
    /// at a level measures every plane at it): a ladder around the guess, then +-DeblockRefine steps per plane.</summary>
    public bool DeblockPerPlane;
    public int DeblockRefine;
    /// <summary>libaom allintra speed-6 8x8 partition prunes (camera content only, like libaom): RectPruneQidx =
    /// prune_rectangular_split_based_on_qidx 2 (no 8x4/4x8 below qindex 171); Sub8PruneNeighbour =
    /// prune_sub_8x8_partition_level 1 (no sub-8x8 partition when the left or above block is larger than 8x8);
    /// RectPruneVarDev = prune_rect_part_using_4x4_var_deviation; RectPruneNoneMode = prune_rect_part_using_none_pred_mode.</summary>
    public bool RectPruneQidx, Sub8PruneNeighbour, RectPruneVarDev, RectPruneNoneMode;
    /// <summary>Deblocking level from the quantiser (libaom LPF_PICK_FROM_Q) while CDEF is still searched.</summary>
    public bool DeblockPickFromQ;
    /// <summary>Loop restoration (Wiener / self-guided) search; LrSgrSets = self-guided parameter sets tried per unit.</summary>
    public bool UseLoopRestoration = true;
    /// <summary>No loop restoration for 4:2:0 / monochrome (it gains ~0 there at speed 6; 4:4:4 keeps ~1.2%).</summary>
    public bool LrSkip420;
    /// <summary>No loop restoration for 4:2:2 either (speed 6: 0.2% for 6% of the time; 4:4:4 keeps it).</summary>
    public bool LrSkip422;
    public int LrSgrSets = 16;
    /// <summary>libaom's loop-restoration search prunes (Av1LrEncoder.LrPrune): enable_sgr_ep_pruning,
    /// prune_sgr_based_on_wiener (…Screen with screen content tools), prune_wiener_based_on_src_var,
    /// reduce_wiener_window_size, dual_sgr_penalty_level.</summary>
    public int LrSgrEp, LrSgrOnWiener, LrSgrOnWienerScreen, LrWienerSrcVar, LrDualSgrPenalty;
    public bool LrReduceWiener;
    /// <summary>Luma restoration unit sizes searched, as a mask of lr_unit_shift (bit 0 = 64, 1 = 128, 2 = 256).</summary>
    public int LrUnitShiftMask = 7;
    public int LrWienerRounds = 3;
    /// <summary>Decode the restored frame and keep it only if it beats the unrestored one (else trust the search).</summary>
    public bool LrVerify = true;
    /// <summary>Wiener statistics on every n-th row and column (1 = every pixel).</summary>
    public int LrStatsStep = 1;
    /// <summary>Largest frame (pixels) the decode-based deblocking / CDEF searches run on.</summary>
    public long FilterSearchMaxPixels = long.MaxValue;
    /// <summary>Deblocking level / CDEF strength taken straight from the quantiser guess, without decode-based search
    /// (libaom LPF_PICK_FROM_Q).</summary>
    public bool FilterPickFromQ;
    /// <summary>One joint round of deblocking x CDEF candidates (no/guess level x no/heuristic strength) instead of
    /// the level search followed by the CDEF search.</summary>
    public bool FilterSearchFast;
    /// <summary>Rect / T-shape / 4-way partitions also at 64x64 (64x32, 64x16, ...; 4:2:0).</summary>
    public bool UsePartition64 = true;

    public Av1EncodeSpeed Clone() => (Av1EncodeSpeed)MemberwiseClone();

    /// <summary>Dev harness hook: adjusts every preset ForSpeed returns (timing experiments).</summary>
    internal static Action<Av1EncodeSpeed>? TestOverride;

    /// <summary>The preset for an avifenc-style speed 0 (slowest, smallest files) .. 10 (fastest). Chosen from measured
    /// time / BD-rate trade-offs on the encoder corpus against libaom's own ladder (see AvifEncodeOptions.Speed).</summary>
    public static Av1EncodeSpeed ForSpeed(int speed)
    {
        var p = new Av1EncodeSpeed();
        speed = Math.Clamp(speed, 0, 10);
        if (speed >= 1) p.CnnPruneLevel = 2;
        if (speed >= 5) p.CnnPruneLevelScreen = 1;
        if (speed >= 2) p.HogLevel = 2;
        if (speed >= 3) { p.HogLevel = 3; p.HogChromaLevel = 2; }
        if (speed >= 5) p.HogChromaLevel = 3;
        if (speed >= 6) { p.HogLevel = 4; p.HogChromaLevel = 4; }
        // 1+: filter intra with libaom's prune_filter_intra_level 1 (FILTER_DC + the best mode's filter mode), 8 RD mode
        // candidates, no 64x64 rectangular shapes (scoreboard s1 -0.37% x1.47 -> -0.20% x0.89).
        // 0: tx-depth search and in-search RDOQ measured neutral-to-negative on the avifenc scoreboard (tx depth +0.2%,
        // RDOQ-in-search -0.05% for 2.2x time): off (s0 +1.03% x1.70 -> +0.29% x0.57).
        p.UseColorTxDepth = false; p.RdoqInSearch = false;
        // 0-2: 16x4 / 4x16 strips (HORZ_4 / VERT_4 at 16x16): s0 +0.49% x0.55 -> -0.01% x0.58,
        // s1 -0.12% x0.85 -> -0.54% x0.96, s2 -0.17% x0.78 -> -0.57% x0.89.
        p.Part4At16 = speed <= 2;
        // 0-2, 4:4:4 / 4:2:2 only: tx_depth 1 in the rect leaf, jointly with the next 2 prescreened modes
        // (s2 444 +2.39% x0.74 -> +1.84% x1.01 with 3 / +1.93% x0.88 with 1; 422 +1.79% x0.59 -> +1.07% x0.83 with 3;
        // 4:4:4 keeps 1 alternative mode: 3 cost x1.11 at s1).
        p.TxSplit444422 = speed <= 2; p.RectTxDepthAlt = 3;
        if (speed >= 1) { p.UseColorTxDepth = false; p.EarlyTermBits = 16; p.RdoqInSearch = false; p.RdUvCandidates = 6; }
        if (speed >= 1) { p.RdModeCandidates = 8; p.UsePartition64 = false; p.FilterIntraPrune = true; }
        // 0-3: libaom's allintra luma search (LumaSearch.cs; its speed features per speed): modes x tx sizes x tx types
        // jointly with the single-pass trellis, the intra edge filter, AB-partition prunes, rd_try_subblock budgets.
        if (speed <= 3)
        {
            p.LibaomLuma = true; p.UseColorTxDepth = true; p.UseIntraEdgeFilter = true; p.TxSplit444422 = false;
            p.AomTrellis = true; p.AomTrellisFirst = true; p.AomTxDomainDist = true; p.AomLuma64 = true;
            p.AomPartAbort = true; p.AomPruneAb = true; p.AomLessRectCheck = true;
            p.AomTopIntraModelCount = speed == 0 ? 4 : 3;              // top_intra_model_count_allowed
            p.AomTxInitDepthRect = speed == 0 ? 0 : 1;                 // intra_tx_size_search_init_depth_rect
            p.AomTrellisMseThr = speed switch { 0 => 3200, 1 => 1728, _ => 864 };   // coeff_opt_thresholds
            p.AomDisableSmoothHV = speed >= 2;                         // disable_smooth_intra
            p.AomAdaptiveTxb = speed == 0 ? 1 : 2;                      // adaptive_txb_search_level
            p.AomPartAbReuse = speed >= 1;                             // reuse_best_prediction_for_part_ab
            // libaom's lambda balance: its key-frame rdmult for the mode decisions, its trellis lambda (x0.9) for the
            // luma trellis, the chroma trellis at plane_rd_mult_chroma's ratio, round-to-nearest before the trellis,
            // and its chroma search (every candidate trellised, CfL alphas RD-searched): scoreboard s2 444 +1.15 ->
            // +0.3..0.6, 420 -0.64 -> -0.8..-1.0
            p.LambdaLibaom = 1; p.AomTrellisLam = 0.9; p.AomChroma = true; p.AomChromaLam = 36; p.AomRoundNearest = true;
            // 4:2:0 / mono sub-8x8 leaves through the same libaom luma / chroma search, the other RDOQ at 61: s2 420
            // -1.34% -> -1.57% x0.98, 400 0.00% x1.04 -> -0.17% x1.01
            p.AomSub8Rect = true; p.AomRdoqScale = 61;
        }
        // 2 keeps the T-shape and 4-way partitions (s2 +0.27% x0.53 -> -0.17% x0.85).
        if (speed >= 2) { p.RdModeCandidates = 4; p.EarlyTermBits = 32; }
        if (speed >= 3) { p.EarlyTermBits = 64; p.FastScreenDetection = true; }
        // 3: T-shape and 4-way partitions as libaom searches them (4-way pruned by the split sub-blocks' rect wins) for
        // 4:4:4 / 4:2:2 only: 444 +0.23% x0.77 -> -0.07% x0.96; in 4:2:0 they gain 0.4% for 27% more time
        if (speed >= 3) { p.UseExtPartition = false; p.AomPrune4Split = true; p.ExtNon420 = speed == 3; }
        // 0-5: deblocking levels per plane around libaom's q fit (scoreboard s4 -0.1%).
        if (speed <= 5) { p.DeblockPerPlane = true; p.LfGuessLibaom = true; }
        if (speed >= 4) p.CflSearchRange = 2;
        // 4+: sub-8x8 leaves stay (worth ~4.5% here) with their luma modes prescreened to 4.
        if (speed >= 4) { p.RdModeCandidates = 2; p.Sub8ModeCandidates = 4; p.UseSplit4x4 = false; p.EarlyTermBits = 32; }
        if (speed >= 4) p.LrSgrSets = 8;
        // libaom allintra loop-restoration search prunes (speed 1: sgr ep pruning + dual-sgr penalty; 2: Wiener skipped
        // on flat units, self-guided skipped when Wiener did not pay; 3: harsher, 5-tap luma Wiener)
        if (speed >= 1) { p.LrSgrEp = 1; p.LrDualSgrPenalty = 1; }
        if (speed >= 2) { p.LrWienerSrcVar = 1; p.LrSgrOnWiener = p.LrSgrOnWienerScreen = 1; }
        if (speed >= 3) { p.LrWienerSrcVar = 2; p.LrSgrOnWiener = 2; p.LrReduceWiener = true; }
        p.AomS4Non420 = speed == 4;
        p.Aom444Tier = speed is 5 or 6 ? speed : 0;
        // 5-6 keep the full intra tx set (V_DCT / H_DCT: s6 4:4:4 +0.17 -> -0.27%, 4:2:0 -3.14 -> -3.45%, no time cost)
        if (speed >= 5) p.RdUvCandidates = 1;
        if (speed >= 7) p.UseFullIntraTxSet = false;
        // 5: no 4:2:0 loop restoration (libaom allintra 5+), libaom's 4x4-variance and NONE-mode 8x8 prunes, CDEF fast
        // level 3: scoreboard -1.64% x1.49 -> -1.26% x0.93.
        if (speed >= 5) { p.LrSkip420 = true; p.RectPruneVarDev = true; p.RectPruneNoneMode = true; p.CdefSearchLevel = 3; }
        // 5: no 4:2:2 loop restoration either (libaom allintra 5+ disables Wiener and self-guided: 422 x1.07 -> x0.81,
        // -1.54 -> -1.44%) and libaom's level-2 luma palette size order (at 3 it lost 0.16% on 4:4:4; here mono
        // x1.05 -> x1.01 on the screen content for 0.06%)
        if (speed >= 5) { p.LrSkip422 = true; p.PaletteSearchLevel = 2; }
        // 6 keeps the trial-encode partition search (libaom's speed 6 is RD with pruning): corpus BD vs libaom
        // cpu-used 6 -0.3% (the estimate-driven partitions it used before: +9.3%), fox 1204x800 0.62 s all threads.
        // 6: no rectangular partitions above 8x8 and no filter intra (libaom prune_filter_intra_level 2).
        // 6: CDEF fast level 4 as libaom (4:2:0 -3.29% x1.05 -> -3.19% x1.01), no 4:2:2 loop restoration.
        if (speed >= 6) { p.UseRd = false; p.EarlyTermBits = 64; p.CdefSearchLevel = 4; p.UseRectPartition = false; p.UseFilterIntra = false; p.LrSkip422 = true; }
        // 6: DCT-only mode decision with the tx type searched for the winner (libaom fast_intra_tx_type_search), one RD
        // mode candidate (top_intra_model_count), early termination at 128 bits, CNN split forcing for screen content:
        // scoreboard -2.99% x1.42 -> -1.44% x1.05.
        if (speed >= 6) { p.FastIntraTxType = true; p.RdModeCandidates = 1; p.EarlyTermBits = 128; p.CnnPruneLevelScreen = 2; }
        if (speed >= 6) { p.LrSgrSets = 0; p.LrUnitShiftMask = 4; p.LrWienerRounds = 1; p.LrVerify = false; p.LrStatsStep = 2; p.FilterSearchFast = true; }
        // 6: deblocking level from q (libaom LPF_PICK_FROM_Q) and no 4:2:0 loop restoration (libaom allintra 5+ has none): both measured
        // within 0.1% BD on the avifenc scoreboard, ~6% less time.
        if (speed >= 6) { p.DeblockPickFromQ = true; p.LrSkip420 = true; }
        // 6: libaom's 8x8 partition prunes (qindex, neighbour size, 4x4 variance spread, NONE mode): 4:2:0 scoreboard
        // -3.71% x3.78 -> -3.11% x2.43 vs avifenc.
        if (speed >= 6) { p.RectPruneQidx = true; p.Sub8PruneNeighbour = true; p.RectPruneVarDev = true; p.RectPruneNoneMode = true; }
        // 6+: RDOQ skips level-down trials that would need > 4 bits of saving (4:2:0 s6 x0.96 -> x0.89, 4:4:4 x1.07 -> x1.01).
        if (speed >= 6) p.RdoqSkipBits = 4;
        // 7+: partitions from the fast estimate.
        if (speed >= 7) { p.UseTrueRd = false; p.EarlyTermBits = 8; }
        // 7-10 (measured on the speed corpus vs libaom's ladder, BD vs libaom speed 0 / fox 1204x800 1-thread time):
        // 7 +20.1% 0.30 s (aom s7 +22.3%), 8 +24.1% 0.22 s (aom s8 +26.5% 0.25 s), 9 +30.7% 0.20 s,
        // 10 +33.6% 0.18 s (aom s9/s10 +54.6%, 0.11 s).
        // 7+: deblocking level and CDEF strength from the quantiser (no decode-based search; -2.9% vs no filtering).
        if (speed >= 7) { p.FilterPickFromQ = true; p.SetAngleDeltas(0); p.RdModeCandidates = 1; p.UseLoopRestoration = false; }
        if (speed >= 8) p.UseRdoq = false;
        if (speed >= 9) p.UseTxTypeSearch = false;
        // 9+: libaom's nonrd intra mode masks (DC/H/V below 32x32, DC above) for the partition estimate and the leaf
        // prescreen: scoreboard s10 -9.31% x1.49 -> -9.50% x1.27.
        if (speed >= 9) { p.EstimateNrdModes = true; p.LeafNrdModes = true; }
        // 9+: no HOG pruning (the nonrd masks leave only V / H directional; s9 x1.30 -> x1.15); 10: DC / CfL chroma only.
        if (speed >= 9) { p.HogLevel = 0; p.HogChromaLevel = 0; }
        // 9+: partition estimates from the coefficient quantisation error (no inverse transform) and DC / CfL chroma:
        // s9 -12.4% x1.15 -> -11.3% x0.96, s10 -8.9% x1.12 -> -8.6% x0.99.
        if (speed >= 9) { p.EstimateCoefDist = true; p.UseUvModeSearch = false; }
        if (speed >= 10) p.UseCfl = false;
        TestOverride?.Invoke(p);
        return p;
    }
}

internal static partial class Av1StillImageEncoder
{
    // Coding bit depth (8/10/12) for the current encode, set by BitDepthScope at every multi-superblock entry.
    // Thread-static: the encoder runs single-threaded per call but callers may encode different images on
    // different threads. The whole pixel pipeline runs in ushort through the decoder's own 16-bit twins
    // (Predict16 / InvTxfmAdd16 / PrepareIntraEdges<ushort>), which Av1HbdTwinTests pin to the 8-bit results at
    // bd=8 — so 8-bit streams are unchanged — and which the decoder itself uses for every bit depth.
    // Search-effort settings of the encode running on this thread (AvifEncodeOptions.Speed): the knobs below read
    // through Sp, so concurrent encodes at different speeds never interfere; worker threads inherit their caller's.
    [ThreadStatic] internal static Av1EncodeSpeed? t_speed;
    internal static readonly Av1EncodeSpeed DefaultSpeed = new();
    private static Av1EncodeSpeed Sp => t_speed ?? DefaultSpeed;

    [ThreadStatic] private static int t_bd;
    private static int Bd => t_bd == 0 ? 8 : t_bd;
    // Worker threads for the encode running on this thread (AvifEncodeOptions.MaxThreads; 0 = every core).
    [ThreadStatic] internal static int t_threads;
    private static int ThreadCount => t_threads > 0 ? t_threads : Environment.ProcessorCount;

    // Evaluates independent candidates (trial decodes) on worker threads that inherit this encode's bit depth and
    // speed settings; results come back in candidate order, so the choice never depends on the thread count.
    private static long[] EvaluateAll(int count, Func<int, long> eval)
    {
        var results = new long[count];
        int threads = Math.Min(ThreadCount, count);
        if (threads <= 1) { for (int i = 0; i < count; i++) results[i] = eval(i); return results; }
        int bd = Bd; var speed = t_speed; var writer = Av1ObuWriter.CaptureThreadState();
        System.Threading.Tasks.Parallel.For(0, count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            using var scope = new BitDepthScope(bd);
            var prev = t_speed; t_speed = speed;
            var prevWriter = Av1ObuWriter.ExchangeThreadState(writer);
            try { results[i] = eval(i); } finally { t_speed = prev; Av1ObuWriter.ExchangeThreadState(prevWriter); }
        });
        return results;
    }
    private static int PixMax => (1 << Bd) - 1;
    private static int PixMid => 1 << (Bd - 1);
    private static int BdIdx => Bd == 8 ? 0 : Bd == 10 ? 1 : 2;
    // Dequantized-coefficient saturation, exactly the decoder's cf_max = ~(~127 << bpc) (32767 at 8-bit).
    private static int CfMax => ~(~127 << Bd);

    private readonly struct BitDepthScope : IDisposable
    {
        private readonly int prev;
        public BitDepthScope(int bd)
        {
            if (bd is not (8 or 10 or 12)) throw new ArgumentOutOfRangeException(nameof(bd), "AV1 bit depth must be 8, 10 or 12.");
            prev = t_bd;
            t_bd = bd;
        }
        public void Dispose() => t_bd = prev;
    }

    // LambdaLibaom (dev): the RD lambda follows libaom's key-frame rdmult for this qindex (av1_compute_rd_mult:
    // dcq^2 * (3.3 + 0.0015 dcq), >> 2(bd-8) beyond 8 bits; RDCOST -> pixel-SSE lambda = rdmult / 2048, with the SSE in
    // coded-bit-depth units) times the factor, as RdLambdaK * acDq^2. Restored on dispose.
    // Per encode (never the shared static): the frame's lambda constant goes into a copy of this encode's speed
    // settings, which worker threads inherit with t_speed.
    private readonly struct LambdaScope : IDisposable
    {
        private readonly Av1EncodeSpeed? prev;
        private readonly bool set;
        public LambdaScope(int qIdx)
        {
            prev = t_speed; set = false;
            double f = Sp.LambdaLibaom;
            if (f <= 0) return;
            double dc = Av1Tables.DequantTable[BdIdx, qIdx, 0], ac = Av1Tables.DequantTable[BdIdx, qIdx, 1];
            double rdmult = dc * dc * (3.3 + 0.0015 * dc) / (1 << (2 * (Bd - 8)));
            double lam = rdmult / 2048 * (1 << (2 * (Bd - 8)));
            var sp = Sp.Clone(); sp.LambdaKFrame = f * lam / (ac * ac);
            t_speed = sp; set = true;
        }
        public void Dispose() { if (set) t_speed = prev; }
    }

    // Layout-dependent preset adjustments for one colour encode: 4:4:4 / 4:2:2 turn on the rect-leaf tx split
    // (TxSplit444422 — it pays there, not in 4:2:0 / mono). Restores the caller's settings on dispose.
    private readonly struct LayoutSpeedScope : IDisposable
    {
        private readonly Av1EncodeSpeed? prev;
        private readonly bool swapped;
        public LayoutSpeedScope(Av1PixelLayout layout)
        {
            prev = t_speed; swapped = false;
            var sp = Sp;
            if (sp.ExtNon420 && !sp.UseExtPartition && layout is Av1PixelLayout.I444 or Av1PixelLayout.I422)
            {
                sp = sp.Clone(); sp.UseExtPartition = true;
                t_speed = sp; swapped = true;
            }
            if (sp.TxSplit444422 && layout is Av1PixelLayout.I444 or Av1PixelLayout.I422 && !(sp.UseColorTxDepth && sp.RectTxDepth))
            {
                var s = sp.Clone(); s.UseColorTxDepth = true; s.RectTxDepth = true;
                if (layout == Av1PixelLayout.I444) s.RectTxDepthAlt = Math.Min(s.RectTxDepthAlt, 1);   // 3 is x1.11 at s1
                t_speed = s; swapped = true;
            }
            else if (sp.AomS4Non420 && layout is Av1PixelLayout.I444 or Av1PixelLayout.I422)
            {
                // scoreboard s4: 444 +1.33% x0.81 -> -0.60% x0.96 (on 4:2:0 it measured -2.71 -> -2.39: not there)
                var s = sp.Clone();
                s.LibaomLuma = true; s.UseColorTxDepth = true; s.UseIntraEdgeFilter = true;
                s.AomTrellis = true; s.AomTrellisFirst = true; s.AomTxDomainDist = true; s.AomLuma64 = true;
                s.AomPartAbort = true; s.AomPruneAb = true; s.AomLessRectCheck = true; s.AomPartAbReuse = true;
                s.AomDisableSmoothHV = true; s.AomTrellisMseThr = 864; s.AomTopIntraModelCount = 2;
                s.AomTxInitDepthSqr = 1; s.AomTxInitDepthRect = 1; s.AomAdaptiveTxb = 2; s.AomWinnerTxSize = true;
                s.LambdaLibaom = 1; s.AomTrellisLam = 0.9; s.AomChroma = true; s.AomChromaLam = 36; s.AomRoundNearest = true; s.AomRdoqScale = 61;
                t_speed = s; swapped = true;
            }
            else if (sp.Aom444Tier > 0 && layout == Av1PixelLayout.I444)
            {
                // scoreboard 444: s5 -0.64% x1.03 -> -0.46% x0.93, s6 -0.84% x1.03 -> -0.85% x1.00
                var s = sp.Clone();
                s.LambdaLibaom = 1; s.AomRoundNearest = true; s.AomRdoqScale = 61;
                if (sp.Aom444Tier == 5) { s.AomChroma = true; s.AomChromaLam = 36; s.UseLoopRestoration = false; }
                else s.LrStatsStep = 4;
                t_speed = s; swapped = true;
            }
        }
        public void Dispose() { if (swapped) t_speed = prev; }
    }

    // A/B toggle: rate-distortion leaf decision (mode + tx-type via EstimateCoefBits) vs the SATD-only baseline.
    internal static bool UseRd { get => Sp.UseRd; set => Sp.UseRd = value; }

    /// <summary>Encodes a flat DC-only monochrome key frame at <paramref name="width"/>x<paramref name="height"/>
    /// (must fit in a single 64x64 superblock). Returns the AV1 temporal unit (temporal delimiter + sequence
    /// header + OBU_FRAME). <paramref name="dcLevel"/> is the quantized DC coefficient level: 0 codes skip=1
    /// (flat 128 plane); 1 or 2 codes a single DC coefficient (uniform non-128 plane). Larger levels need the
    /// base-range (HiTok) path, not yet implemented.</summary>
    internal static byte[] EncodeFlatMonochrome(int width, int height, int baseQIdx = 40, int dcLevel = 0, bool dcNegative = false)
    {
        if (width < 1 || width > 64 || height < 1 || height > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Step-2/3 encoder supports a single 64x64 superblock (1..64).");
        }

        if (dcLevel < 0 || dcLevel > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(dcLevel), "dcLevel must be 0 (skip), 1, or 2 (HiTok path not implemented).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true);
        byte[] seqPayload = Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, seqPayload);

        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Frame {width}x{height} is not a single square block.");
        }

        // Build the coefficient array (rc-indexed for the block's transform) for a single DC coefficient.
        int[]? coeffs = null;
        if (dcLevel > 0)
        {
            coeffs = new int[Av1Tables.Scans[plan.Tx].Length];
            coeffs[0] = dcNegative ? -dcLevel : dcLevel;
        }

        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleBlockTile(baseQIdx, coeffs, plan);

        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);

        byte[] tdObu = Av1ObuWriter.WrapObu(Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);

        var outBytes = new byte[tdObu.Length + seqObu.Length + frameObu.Length];
        int o = 0;
        tdObu.CopyTo(outBytes, o); o += tdObu.Length;
        seqObu.CopyTo(outBytes, o); o += seqObu.Length;
        frameObu.CopyTo(outBytes, o);
        return outBytes;
    }

    /// <summary>Encodes a tightly-packed 64x64 monochrome luma image as a raw AV1 temporal unit (TD + seq +
    /// OBU_FRAME) — DC prediction, forward transform, quant, coefficient coding. Lossy.</summary>
    internal static byte[] EncodeMonochromeImage64(ReadOnlySpan<byte> pixels, int baseQIdx)
    {
        TryResolveSingleBlock(64, 64, out BlockPlan plan);
        return EncodeMonochromeWithCoeffs(64, 64, baseQIdx, QuantizeBlock(pixels, 64, 64, plan, baseQIdx));
    }

    // A single square intra block covering the frame, reached from the 64x64 superblock via forced partition
    // splits (which emit no symbols). Only PARTITION_NONE at the target level is coded.
    private readonly struct BlockPlan
    {
        public readonly Av1BlockLevel Bl;   // partition level of the coded block
        public readonly int Bs;             // block size ordinal (Av1BlockSize)
        public readonly int Tx;             // luma transform size ordinal
        public readonly int NPart;          // partition symbol count at this level (PartitionTypeCount[bl])
        public readonly int BlockPx;        // block dimension in pixels (8/16/32/64)

        public BlockPlan(Av1BlockLevel bl, int bs, int tx, int nPart, int blockPx)
        {
            Bl = bl;
            Bs = bs;
            Tx = tx;
            NPart = nPart;
            BlockPx = blockPx;
        }

        // Chroma transform ordinal for I420 (subsampled) — the largest chroma tx for this block size.
        public int ChromaTxI420 => Av1Tables.MaxTxfmSizeForBlockSize[Bs, (int)Av1PixelLayout.I420];

        // CfL is allowed for blocks <= 32x32.
        public bool CflAllowed => ((Av1Tables.CflAllowedMask >> Bs) & 1) != 0;
    }

    // Resolves the single square block that covers a width x height frame, or false if the frame needs a
    // rectangular / multi-block partition we don't yet emit. Mirrors the decoder's forced-split rule at the
    // superblock root: while neither dimension exceeds hsz the block is force-split to the next level.
    private static bool TryResolveSingleBlock(int width, int height, out BlockPlan plan)
    {
        plan = default;
        int width4 = (width + 3) >> 2;
        int height4 = (height + 3) >> 2;

        for (int bl = 1; bl <= 4; bl++)
        {
            int hsz = 16 >> bl;
            bool haveH = width4 > hsz;
            bool haveV = height4 > hsz;
            if (!haveH && !haveV)
            {
                continue; // forced split to bl+1 (no symbol)
            }

            // First level with a real partition decision. We can code PARTITION_NONE only when the full range is
            // available (both splits) and the block covers the whole frame.
            if (!haveH || !haveV)
            {
                return false;
            }

            int blockPx = 64 >> (bl - 1);
            if (width > blockPx || height > blockPx)
            {
                return false;
            }

            (int bs, int tx) = bl switch
            {
                1 => ((int)Av1BlockSize.Bs64x64, 4),
                2 => (7 /*Bs32x32*/, 3),
                3 => (12 /*Bs16x16*/, 2),
                _ => (17 /*Bs8x8*/, 1),
            };
            plan = new BlockPlan((Av1BlockLevel)bl, bs, tx, Av1Tables.PartitionTypeCount[bl], blockPx);
            return true;
        }

        return false;
    }

    private static byte[] EncodeSingleBlockTile(int baseQIdx, int[]? coeffs, in BlockPlan plan)
    {
        // qcat selects the coefficient CDF set; must match the decoder's derivation from the segment q index.
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);

        var w = new Av1MsacWriter();

        // Forced splits from the 64x64 root down to plan.Bl emit NO symbols. At plan.Bl, ctx=0 (no neighbours;
        // reset_context fills Partition=0). PARTITION_NONE = 0. Decoder: DecodeSymbolAdapt(partCdf, NPart).
        w.EncodeSymbolAdapt(cdf.GetPartitionCdf(plan.Bl, 0), 0, plan.NPart);

        // Skip flag, ctx=0 (above/left skip = 0). skip=0 ⇒ residual coded. CDEF disabled ⇒ no CDEF bits.
        int skip = coeffs == null ? 1 : 0;
        w.EncodeBoolAdapt(cdf.GetSkipCdf(0), (uint)skip);

        // Keyframe Y mode, contexts 0/0 (neighbour modes DC). DC_PRED = 0.
        w.EncodeSymbolAdapt(cdf.GetKfYModeCdf(0, 0), 0, 12);

        if (skip == 0)
        {
            // Single square luma transform (DctDct), DC intra mode. First block ⇒ skip/dc-sign contexts are 0.
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, plan.Tx, chroma: 0, yMode: 0, coeffs!);
        }

        return w.Finish();
    }

    private const int Tx64x64 = 4;
    private const int Tx32x32 = 3;

    /// <summary>Encodes a single-block I420 colour image (near-square even size, mapping to one square luma block
    /// of 8/16/32/64) as a complete .avif. <paramref name="luma"/> is w x h; <paramref name="u"/>/<paramref
    /// name="v"/> are (w/2) x (h/2) subsampled chroma. DC intra for luma and chroma.</summary>
    internal static byte[] EncodeAvifColor(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (width % 2 != 0 || height % 2 != 0 || !TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Colour AVIF encode requires an even near-square size mapping to one square block (got {width}x{height}).");
        }

        byte[] tile = EncodeColorTile(luma, u, v, width, height, plan, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: false, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, 1, 1, monochrome: false);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false, color: color, extras: extras);
    }

    private static byte[] EncodeColorTile(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, in BlockPlan plan, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int dcDq = Av1Tables.DequantTable[0, baseQIdx, 0];
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1]; // U/V share Y's dq (no separate_uv_delta_q)

        int n = plan.BlockPx;
        int cn = n / 2;
        int lumaTx = plan.Tx;
        int chromaTx = plan.ChromaTxI420;
        int cw = width / 2;
        int ch = height / 2;

        int[] yCoeffs = QuantizeResidualBlock(luma, width, height, n, lumaTx, dcDq, acDq);
        int[] uCoeffs = QuantizeResidualBlock(u, cw, ch, cn, chromaTx, dcDq, acDq);
        int[] vCoeffs = QuantizeResidualBlock(v, cw, ch, cn, chromaTx, dcDq, acDq);
        bool anyNz = HasNonZero(yCoeffs) || HasNonZero(uCoeffs) || HasNonZero(vCoeffs);
        int skip = anyNz ? 0 : 1;

        int uvMaxSym = Av1Constants.NumUvIntraPredModes - 1 - (plan.CflAllowed ? 0 : 1);

        var w = new Av1MsacWriter();
        w.EncodeSymbolAdapt(cdf.GetPartitionCdf(plan.Bl, 0), 0, plan.NPart);        // PARTITION_NONE
        w.EncodeBoolAdapt(cdf.GetSkipCdf(0), (uint)skip);                        // skip, ctx 0
        w.EncodeSymbolAdapt(cdf.GetKfYModeCdf(0, 0), 0, 12);                        // Y mode = DC
        w.EncodeSymbolAdapt(cdf.GetUvModeCdf(plan.CflAllowed, 0), 0, uvMaxSym);     // UV mode = DC

        if (skip == 0)
        {
            var neutral = new byte[32];
            Array.Fill(neutral, (byte)0x40);
            ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[chromaTx];
            int chromaSkipCtx = Av1CoeffDecode.GetSkipCtx(in uvtDim, plan.Bs,
                neutral, neutral, chroma: 1, layout: (int)Av1PixelLayout.I420);

            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, lumaTx, chroma: 0, yMode: 0, yCoeffs);
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, chromaTx, chroma: 1, yMode: 0, uCoeffs, skipCtx: chromaSkipCtx);
            Av1CoeffEncode.EncodeCoefs(w, cdf.Coef, cdf.Mode, chromaTx, chroma: 1, yMode: 0, vCoeffs, skipCtx: chromaSkipCtx);
        }

        return w.Finish();
    }

    // Forward-transforms and quantizes an n x n block (DC prediction 128) built from a srcW x srcH plane with
    // edge replication beyond the frame.
    private static int[] QuantizeResidualBlock(ReadOnlySpan<byte> src, int srcW, int srcH, int n, int tx, int dcDq, int acDq)
    {
        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(y, srcH - 1);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(x, srcW - 1);
                residual[y * n + x] = src[sy * srcW + sx] - 128;
            }
        }

        return Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, Av1Tables.Scans[tx].Length);
    }

    private static bool HasNonZero(int[] a)
    {
        foreach (int c in a)
        {
            if (c != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Encodes a monochrome image whose frame is a 1..2 by 1..2 grid of full 64x64 superblocks (width
    /// and height each 64 or 128) as a complete .avif. Unlike the single-block path this codes each superblock as
    /// PARTITION_NONE with real DC prediction from reconstructed neighbours and neighbour DC-sign contexts,
    /// reconstructing as it goes. Capped at 2x2 SBs because the decoder's above context holds only two SBs.</summary>
    internal static byte[] EncodeAvifMonochromeMultiSb(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
        => EncodeAvifMonochromeMultiSb(Widen(luma), width, height, baseQIdx, 8, color, extras);

    /// <summary>High-bit-depth monochrome entry: <paramref name="luma"/> holds samples in [0, 2^bitDepth).</summary>
    internal static byte[] EncodeAvifMonochromeMultiSb(ReadOnlySpan<ushort> luma, int width, int height, int baseQIdx, int bitDepth,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] seqObu, byte[] frameObu) = BuildMonochromeObus(luma, width, height, baseQIdx, bitDepth, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true, bitDepth, color: color, extras: extras);
    }

    // Widens 8-bit samples to the encoder's ushort pixel type (identity values).
    private static ushort[] Widen(ReadOnlySpan<byte> src)
    {
        var d = new ushort[src.Length];
        for (int i = 0; i < src.Length; i++) d[i] = src[i];
        return d;
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for a monochrome (I400) multi-superblock key frame — the
    /// shared core used both for a standalone grayscale AVIF and for an AVIF alpha auxiliary item.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildMonochromeObus(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx)
        => BuildMonochromeObus(Widen(luma), width, height, baseQIdx, 8);

    internal static (byte[] SeqObu, byte[] FrameObu) BuildMonochromeObus(ReadOnlySpan<ushort> luma, int width, int height, int baseQIdx, int bitDepth,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        if (Sp.MonoColorPath) return BuildColorObus(luma, default, default, width, height, baseQIdx, bitDepth, Av1PixelLayout.I400, color);
        using var bdScope = new BitDepthScope(bitDepth);
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        ushort[] padded = PadPlane(luma, width, height, pw, ph);
        byte[] tile = EncodeMultiSbTile(padded, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true, bitDepth: bitDepth, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));

        // CDEF strength search: the tile is CDEF-independent (cdef_bits=0), so try candidate strengths by decoding
        // each and keeping the one with the lowest reconstruction SSE vs source (always incl. the no-op, so it can
        // never hurt). The decoder applies CDEF as an output filter; intra prediction used pre-CDEF recon.
        ushort[] srcCopy = luma.ToArray();
        int lfLevel = SearchDeblock(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: true, srcCopy, null, null, 0, 0);
        Av1ObuWriter.CdefParams best = SearchCdef(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            monochrome: true, srcCopy, null, null, 0, 0, lfLevel);
        byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome: true, tile, best, lfLevel, legacyGray: true);
        return (seqObu, frameObu);
    }

    // Assembles the OBU_FRAME (frame header with the given CDEF params + tile) for a multi-SB key frame.
    // legacyGray: the grey-only encoder (EncodeMultiSbTile) always selects tx sizes and uses the reduced tx set without
    // screen-content tools; the colour encoder (also for monochrome) signals what its speed preset uses.
    private static byte[] BuildFrameObu(int baseQIdx, int sbCols, int sbRows, bool monochrome, byte[] tile, Av1ObuWriter.CdefParams cdef, Av1ObuWriter.LfLevels lfLevel = default,
        bool legacyGray = false, bool screenContent = false)
    {
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, monochrome, txModeSelect: legacyGray || UseColorTxDepth, cdef, lfLevel, screenContentTools: (UsePalette || screenContent) && !legacyGray, reducedTxSet: legacyGray || !UseFullIntraTxSet);
        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        return Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
    }

    /// <summary>Dev hook: colour-encode phase durations (ms) as they finish.</summary>
    internal static Action<string, double>? PhaseHook;

    private static bool NrdModeAllowed(Av1IntraPredMode mode, int n)
        => mode == Av1IntraPredMode.Dc || (n < 32 && mode is Av1IntraPredMode.Vertical or Av1IntraPredMode.Horizontal);

    // libaom choose_var_based_partitioning for a key frame (64x64 superblock): leaves are 4x4 source averages minus 128
    // (fill_variance_4x4avg), a node's variance = 256 * (sse - sum^2 / n) / n over its leaves (get_variance); a 16x16
    // over thresholds[3] forces the split of itself and its parents, a 32x32 over thresholds[2] likewise, 64x64 always
    // splits, and a block below its threshold is kept whole (set_vt_partitioning).
    private static int VarPartitionChoice(ColorPartCtx c, int bl, int bx4, int by4)
    {
        if (bl <= 1) return 3;
        int sbKey = ((by4 >> 4) << 16) | (bx4 >> 4);
        int sx4 = bx4 & ~15, sy4 = by4 & ~15, sh = Bd - 8;
        if (c.VbpSb != sbKey)
        {
            for (int j = 0; j < 16; j++)
                for (int i = 0; i < 16; i++)
                {
                    int x0 = (sx4 + i) * 4, y0 = (sy4 + j) * 4, s = 0;
                    if (sx4 + i < c.Bw4 && sy4 + j < c.Bh4)
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++) s += c.Luma[(y0 + y) * c.W + x0 + x] >> sh;
                    c.VbpAvg[j * 16 + i] = sx4 + i < c.Bw4 && sy4 + j < c.Bh4 ? ((s + 8) >> 4) - 128 : 0;
                }
            c.VbpSb = sbKey;
        }
        long Var(int ox, int oy, int n)   // n x n leaves at (ox, oy) of the superblock
        {
            long sum = 0, sse = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++) { long v = c.VbpAvg[(oy + j) * 16 + ox + i]; sum += v; sse += v * v; }
            int log2 = 2 * System.Numerics.BitOperations.Log2((uint)n);
            return (256 * (sse - ((sum * sum) >> log2))) >> log2;
        }
        long baseT = 120L * (c.AcDq >> sh);
        bool below720p = (long)c.Bw4 * c.Bh4 * 16 < 1280 * 720;
        long th2 = below720p ? baseT / 3 : baseT >> 2, th3 = below720p ? baseT >> 1 : baseT >> 2;
        int ox = bx4 & 15, oy = by4 & 15;
        if (bl == 2)
        {
            for (int q = 0; q < 4; q++) if (Var(ox + (q & 1) * 4, oy + (q >> 1) * 4, 4) > th3) return 3;
            long v = Var(ox, oy, 8);
            return v < th2 ? 0 : 3;
        }
        if (bl == 3) return Var(ox, oy, 4) < th3 ? 0 : 3;
        return 0;
    }

    // UvLumaWinner: appends the luma winner's (mode, delta) to the chroma RD list when the prescreen dropped it; returns the
    // list length (the entries before the first unused slot, plus the appended one).
    private static int AddUvLumaWinner(Span<int> idx, Span<long> cost, Av1IntraPredMode yMode, int yDelta, bool uvAngleOk)
    {
        int n = 0;
        while (n < idx.Length - 1 && cost[n] != long.MaxValue) n++;
        if (!Sp.UvLumaWinner || yMode == Av1IntraPredMode.Dc || yMode > Av1IntraPredMode.Paeth) return n;
        int d = uvAngleOk && IsDirectional(yMode) ? yDelta : 0, wi = -1;
        var cm = CandidateModes;
        for (int i = 0; i < cm.Length && wi < 0; i++) if (cm[i].Mode == yMode && cm[i].Delta == d) wi = i;
        for (int i = 0; i < cm.Length && wi < 0; i++) if (cm[i].Mode == yMode && cm[i].Delta == 0) wi = i;
        if (wi < 0) return n;
        for (int i = 0; i < n; i++) if (idx[i] == wi) return n;
        idx[n] = wi; cost[n] = 0;
        return n + 1;
    }

    // libaom av1_derived_filter_intra_mode_used_flag: FILTER_DC plus the filter mode of the best regular mode so far
    // (FILTER_V / H / D157 / PAETH for V / H / D157 / Paeth).
    private static int FilterIntraModesFor(Av1IntraPredMode best) => best switch
    {
        Av1IntraPredMode.Vertical => 0x03,
        Av1IntraPredMode.Horizontal => 0x05,
        Av1IntraPredMode.HorizontalDown => 0x09,
        Av1IntraPredMode.Paeth => 0x11,
        _ => 0x01,
    };

    // libaom LPF_PICK_FROM_Q (picklpf.c) for a key frame: a linear fit of the searched level on the AC quantiser.
    private static int LibaomLfFromQ(int baseQIdx)
    {
        int q = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];
        int g = Bd switch
        {
            8 => (q * 17563 - 421574 + (1 << 17)) >> 18,
            10 => (q * 20723 + 4060632 + (1 << 19)) >> 20,
            _ => (int)(((long)q * 20723 + 16242526 + (1 << 21)) >> 22),
        };
        if (Bd != 8) g -= 4;
        return Math.Clamp(g, 0, 63);
    }

    // Searches a single global CDEF strength set (cdef_bits=0) that minimises reconstruction SSE. Decodes each
    // candidate through our own decoder (which matches libdav1d's CDEF), comparing the decoded planes to the
    // source. srcY is width*height; srcU/srcV (cw*ch) are only used when !monochrome.
    private static Av1ObuWriter.CdefParams SearchCdef(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, ushort[] srcY, ushort[]? srcU, ushort[]? srcV, int cw, int ch, int lfLevel = 0)
    {
        int damping = Math.Clamp(3 + (baseQIdx >> 6), 3, 6);
        long BestSse = long.MaxValue;
        Av1ObuWriter.CdefParams bestParams = Av1ObuWriter.CdefParams.None;

        long Evaluate(int yLvl, int uvLvl)
        {
            var cdef = new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)uvLvl });
            byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, tileRef, cdef, lfLevel, legacyGray: true);
            var tu = new byte[seqObu.Length + frameObu.Length];
            seqObu.CopyTo(tu, 0);
            frameObu.CopyTo(tu, seqObu.Length);
            var dec = new Av1Decoder { ApplyFilmGrain = false };   // the filter search measures the grain-free recon
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome);

            return sse;
        }

        // CDEF's still-image payoff is modest (~1% RMSE) and its verification costs a full re-decode, so it is
        // applied only where that trade is worth it: lossy quality (baseQIdx >= 64, below which CDEF risks
        // blurring fine detail) and images small enough that 1-2 decodes are cheap. Large frames — where the
        // re-decode is expensive and CDEF's gain on detailed content is near zero — skip it. Within that gate we
        // evaluate the no-op plus one q-scaled heuristic strength and keep whichever decodes closer to the
        // source, so it can never regress vs no CDEF.
        if (!UseCdefSearch || baseQIdx < 64 || (long)width * height > FilterSearchMaxPixels) return Av1ObuWriter.CdefParams.None;

        int yPri = Math.Clamp(baseQIdx / 16, 1, 12);   // stronger deringing as quantisation coarsens
        int ySec = baseQIdx >= 128 ? 2 : 1;
        int yLvl = (yPri << 2) | ySec;
        int uvLvl = monochrome ? 0 : ((Math.Clamp(baseQIdx / 24, 1, 8) << 2) | (baseQIdx >= 160 ? 1 : 0));
        if (FilterPickFromQ) return new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)uvLvl });
        var both = EvaluateAll(2, i => i == 0 ? Evaluate(0, 0) : Evaluate(yLvl, uvLvl));
        long noopSse = both[0];
        BestSse = noopSse;
        long sse = both[1];
        if (sse < BestSse) { BestSse = sse; bestParams = new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)uvLvl }); }

        return bestParams;
    }

    // Searches a single global deblocking loop_filter_level that minimises reconstruction SSE, the same
    // decode-based way as SearchCdef. Deblocking is post-reconstruction (it does not feed intra prediction), so
    // the coded tile is unchanged across candidates — only the frame-header level differs. Searched with CDEF off
    // (SearchCdef then runs with the chosen level), mirroring libaom's deblock-before-CDEF ordering. Returns the
    // level (0 = off) that decoded closest to the source, so it can never regress vs no deblocking.
    internal static bool UseDeblockSearch { get => Sp.UseDeblockSearch; set => Sp.UseDeblockSearch = value; }   // toggle the deblock loop_filter_level RD search (A/B)
    internal static bool UseCdefSearch { get => Sp.UseCdefSearch; set => Sp.UseCdefSearch = value; }      // toggle the CDEF strength search (A/B, conformance isolation)

    // Palette mode for colour (screen-content). When on, the frame enables screen_content_tools and eligible
    // DC luma blocks may be coded as palette; rect partitions are disabled to keep palette to the square leaf.
    internal static bool UsePalette = false;
    internal static int PaletteMaxColors = 8;   // AV1 caps luma palette at 8
    // Filter-intra: the recursive 4x2 filter predictor (5 modes) for DC-eligible luma blocks <=32x32. Colour-only.
    // A filter block codes y_mode=DC + use_filter_intra + filter_mode. THREE distinct "mode" values result (all
    // verified against dav1d): the coded y_mode SYMBOL = DC; the tx-type coefficient context = FilterModeToYMode[fm]
    // (dav1d recon_tmpl.c: filter_mode_to_y_mode); the NEIGHBOUR mode context = DC (dav1d decode.c: FILTER_PRED->DC).
    // -0.33% BD-rate (clean on 5/6 corpus images), byte-exact vs ffmpeg/libdav1d.
    internal static bool UseFilterIntra { get => Sp.UseFilterIntra; set => Sp.UseFilterIntra = value; }

    private static int SearchDeblock(byte[] seqObu, byte[] tileRef, int baseQIdx, int sbCols, int sbRows,
        int width, int height, bool monochrome, ushort[] srcY, ushort[]? srcU, ushort[]? srcV, int cw, int ch)
    {
        // Deblocking's still-image payoff is largest at coarse quantisation; skip the extra decodes when tiny.
        if (!UseDeblockSearch || (long)width * height > FilterSearchMaxPixels) return 0;

        long Evaluate(int lvl)
        {
            byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, tileRef, Av1ObuWriter.CdefParams.None, lvl, legacyGray: true);
            var tu = new byte[seqObu.Length + frameObu.Length];
            seqObu.CopyTo(tu, 0);
            frameObu.CopyTo(tu, seqObu.Length);
            var dec = new Av1Decoder { ApplyFilmGrain = false };   // the filter search measures the grain-free recon
            using var yuv = dec.Decode(tu, 0, isKeyframe: true);
            if (yuv == null) return long.MaxValue;
            long sse = DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome);
            return sse;
        }

        // Candidate levels around a q-scaled guess (AV1 levels are 0..63; deblock strength grows with q), after the
        // no-deblocking baseline; evaluated concurrently, chosen in this order (first strictly lower SSE wins).
        int guess = Math.Clamp(baseQIdx / 8, 1, 40);
        if (FilterPickFromQ) return guess;
        var levels = new List<int> { 0 };
        foreach (int lvl in new[] { guess / 2, guess, Math.Min(guess * 3 / 2, 63) }) if (lvl > 0) levels.Add(lvl);
        var sses = EvaluateAll(levels.Count, i => Evaluate(levels[i]));
        int bestLvl = 0;
        long bestSse = sses[0];
        for (int i = 1; i < levels.Count; i++)
            if (sses[i] < bestSse) { bestSse = sses[i]; bestLvl = levels[i]; }
        return bestLvl;
    }

    // Reconstruction SSE of a decoded frame vs the source planes, at the coded precision (native 10/12-bit
    // samples for high-bit-depth streams, the 8-bit planes otherwise).
    private static long DecodedSse(DecodedVideoFrame yuv, ushort[] srcY, ushort[]? srcU, ushort[]? srcV,
        int width, int height, int cw, int ch, bool monochrome)
        => DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome, out _, out _, out _);

    private static long DecodedSse(DecodedVideoFrame yuv, ushort[] srcY, ushort[]? srcU, ushort[]? srcV,
        int width, int height, int cw, int ch, bool monochrome, out long sseY, out long sseU, out long sseV)
    {
        bool hbd = yuv.BitDepth > 8;
        sseY = hbd ? PlaneSse(yuv.YPlane16.Span, yuv.YStride, srcY, width, height)
                   : PlaneSse(yuv.YPlane.Span, yuv.YStride, srcY, width, height);
        sseU = sseV = 0;
        if (!monochrome && srcU != null && srcV != null)
        {
            sseU = hbd ? PlaneSse(yuv.UPlane16.Span, yuv.UStride, srcU, cw, ch) : PlaneSse(yuv.UPlane.Span, yuv.UStride, srcU, cw, ch);
            sseV = hbd ? PlaneSse(yuv.VPlane16.Span, yuv.VStride, srcV, cw, ch) : PlaneSse(yuv.VPlane.Span, yuv.VStride, srcV, cw, ch);
        }
        return sseY + sseU + sseV;
    }

    private static long PlaneSse(ReadOnlySpan<byte> dec, int stride, ushort[] src, int w, int h)
    {
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = dec[y * stride + x] - src[y * w + x];
                sse += (long)d * d;
            }

        return sse;
    }

    private static long PlaneSse(ReadOnlySpan<ushort> dec, int stride, ushort[] src, int w, int h)
    {
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = dec[y * stride + x] - src[y * w + x];
                sse += (long)d * d;
            }

        return sse;
    }

    /// <summary>Multi-superblock I420 COLOUR: a 1..2 x 1..2 grid of full 64x64 superblocks (64 or 128 each side),
    /// coding luma + subsampled chroma with cross-block DC prediction and reconstruct-as-you-go on all three
    /// planes.</summary>
    internal static byte[] EncodeAvifColorMultiSb(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
        => EncodeAvifColorMultiSb(Widen(luma), Widen(u), Widen(v), width, height, baseQIdx, 8, color: color, extras: extras);

    /// <summary>High-bit-depth I420 colour entry: planes hold samples in [0, 2^bitDepth).</summary>
    internal static byte[] EncodeAvifColorMultiSb(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, int baseQIdx, int bitDepth, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] seqObu, byte[] frameObu) = BuildColorObus(luma, u, v, width, height, baseQIdx, bitDepth, layout, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: false, bitDepth, layout, color, extras);
    }

    /// <summary>Builds the sequence-header + OBU_FRAME for an I420 colour multi-superblock key frame.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildColorObus(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        int width, int height, int baseQIdx)
        => BuildColorObus(Widen(luma), Widen(u), Widen(v), width, height, baseQIdx, 8);

    /// <summary>Builds the sequence header + OBU_FRAME for a colour key frame in the given chroma layout. Chroma planes
    /// are ceil(width >> ssX) x ceil(height >> ssY) (ssX = 0 for 4:4:4, ssY = 1 only for 4:2:0).</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildColorObus(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, int baseQIdx, int bitDepth, Av1PixelLayout layout = Av1PixelLayout.I420,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        if (layout is not (Av1PixelLayout.I420 or Av1PixelLayout.I422 or Av1PixelLayout.I444 or Av1PixelLayout.I400))
            throw new ArgumentOutOfRangeException(nameof(layout));
        using var bdScope = new BitDepthScope(bitDepth);
        using var spScope = new LayoutSpeedScope(layout);
        using var lamScope = new LambdaScope(baseQIdx);
        long phaseT = System.Diagnostics.Stopwatch.GetTimestamp();
        void Phase(string name)
        {
            if (PhaseHook == null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            PhaseHook(name, (now - phaseT) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            phaseT = now;
        }
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        // Monochrome runs the 4:2:0 machinery with its chroma switched off (all-zero chroma planes, never coded).
        bool mono = layout == Av1PixelLayout.I400;
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout is Av1PixelLayout.I420 or Av1PixelLayout.I400 ? 1 : 0;
        int cwIn = mono ? 0 : (width + ssX) >> ssX, chIn = mono ? 0 : (height + ssY) >> ssY;   // ceil — odd dims keep a partial edge sample
        ushort[] padY = PadPlane(luma, width, height, pw, ph);
        SourceHook?.Invoke(padY, pw, ph);
        ushort[] padU = mono ? new ushort[(pw >> 1) * (ph >> 1)] : PadPlane(u, cwIn, chIn, pw >> ssX, ph >> ssY);
        ushort[] padV = mono ? new ushort[(pw >> 1) * (ph >> 1)] : PadPlane(v, cwIn, chIn, pw >> ssX, ph >> ssY);
        // Loop restoration (a standalone still enables it in its sequence header only when used; shared-header streams
        // and grids always enable it — Av1ObuWriter.RestorationHeaderShared). Tiny frames gain nothing.
        bool lrOn = UseLoopRestoration && !(Sp.LrSkip420 && layout is Av1PixelLayout.I420 or Av1PixelLayout.I400)
            && !(Sp.LrSkip422 && layout == Av1PixelLayout.I422) && baseQIdx > 0 && width >= 16 && height >= 16;
        bool cdefSb = Sp.UseCdefPerSb && UseCdefSearch && baseQIdx > 0 && width >= 16 && height >= 16
            && (long)width * height <= FilterSearchMaxPixels && !FilterPickFromQ;
        var (_, _, lrCols, lrRows) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var logs = lrOn || cdefSb ? new List<Av1MsacWriter.LogOp>[(lrCols.Length - 1) * (lrRows.Length - 1)] : null;
        // Screen content (libaom's per-frame detection): palette mode on, signalled in the frame header.
        bool sct = UsePalette || (Sp.UseScreenContentDetection && DetectScreenContent(padY, pw, width, height, bitDepth, fast: Sp.FastScreenDetection));
        Phase("setup");
        byte[] tile = EncodeMultiSbColorTile(padY, padU, padV, pw, ph, sbCols, sbRows, bw4, bh4, baseQIdx, layout, logs, sct);
        Phase("tiles");
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: mono, enableFilterIntra: UseFilterIntra, bitDepth: bitDepth, layout: layout, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        if (t_verifyRecon && t_lastRecon is { } lr)
        {
            var dp = DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, Av1ObuWriter.CdefParams.None, 0, screenContent: sct),
                luma.ToArray(), u.ToArray(), v.ToArray(), width, height, cwIn, chIn, mono, keep: true, 1);
            for (int pl = 0; pl < (mono ? 1 : 3); pl++)
            {
                int pw2 = pl == 0 ? width : cwIn, ph2 = pl == 0 ? height : chIn, rs = pl == 0 ? lr.W : lr.Cw, ds = dp!.Strides![pl];
                for (int yy = 0; yy < ph2; yy++)
                    for (int xx = 0; xx < pw2; xx++)
                        if (lr.Planes[pl][yy * rs + xx] != dp.Planes![pl][yy * ds + xx])
                            throw new InvalidOperationException($"encoder reconstruction differs from the decoder: plane {pl} ({xx},{yy}) " +
                                $"encoder {lr.Planes[pl][yy * rs + xx]} decoder {dp.Planes[pl][yy * ds + xx]} ({width}x{height} {layout} q{baseQIdx})");
            }
        }

        // In-loop filter search (deblocking + CDEF); its winning decode feeds the loop-restoration search.
        ushort[] srcY = luma.ToArray(), srcU = u.ToArray(), srcV = v.ToArray();
        // With per-superblock CDEF the filter search only picks the deblocking level, and its winning (CDEF-free) decode
        // is the CDEF search's input.
        var (lfLevel, best, pic) = SearchColorFilters(seqObu, tile, baseQIdx, sbCols, sbRows, width, height,
            srcY, srcU, srcV, cwIn, chIn, keepPicture: logs != null, deblockOnly: cdefSb, mono: mono, sct: sct);
        Phase("filters");
        sbyte[]? cdefIdx = null;
        if (cdefSb)
        {
            // Per-superblock CDEF on the deblocked picture; replay the tiles with each superblock's cdef_idx.
            var dpic = pic?.Noskip != null ? pic : DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, Av1ObuWriter.CdefParams.None, lfLevel, screenContent: sct),
                srcY, srcU, srcV, width, height, cwIn, chIn, mono, keep: true, ThreadCount, noskip: true);
            int acDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];
            ushort[]? cU = mono ? null : dpic?.Planes?[1], cV = mono ? null : dpic?.Planes?[2];
            int cStride = mono ? 0 : dpic?.Strides?[1] ?? 0;
            if (dpic?.Planes != null && Av1CdefSearch.Search(dpic.Planes[0], dpic.Strides![0], cU, cV, cStride,
                    dpic.RowsY, dpic.RowsC, dpic.Noskip!, dpic.W8, dpic.H8, srcY, srcU, srcV, width, height, cwIn, chIn,
                    sbCols, sbRows, baseQIdx, Bd, LamK * acDq * acDq, ThreadCount, Sp.CdefSearchLevel, ssX, ssY) is { } r)
            {
                var tiles = new byte[logs!.Length][];
                var cw2 = CdefIndexWriter(r.SbIdx, sbCols, r.Params.Bits);
                for (int ti = 0; ti < logs.Length; ti++)
                    tiles[ti] = Av1MsacWriter.Replay(logs[ti], (id, w) => { if (id < 0) cw2(id, w); }, FreshCdfArrays(baseQIdx));
                tile = AssembleTileGroup(tiles);
                best = r.Params;
                cdefIdx = r.SbIdx;
                // The loop-restoration input is the CDEF output: filter the deblocked planes here instead of decoding.
                var planes = Av1CdefSearch.Apply(r, dpic.Planes[0], dpic.Strides[0], cU, cV, cStride,
                    dpic.RowsY, dpic.RowsC, dpic.Noskip!, dpic.W8, dpic.H8, width, height, cwIn, chIn, sbCols, sbRows, Bd, ThreadCount, ssX, ssY);
                pic = new DecodedPicture
                {
                    Planes = planes, Strides = dpic.Strides,
                    Sse = PlaneSse(planes[0], dpic.Strides[0], srcY, width, height)
                        + (mono ? 0 : PlaneSse(planes[1], dpic.Strides[1], srcU, cwIn, chIn) + PlaneSse(planes[2], dpic.Strides[2], srcV, cwIn, chIn)),
                };
            }
        }
        Phase("cdef");
        byte[] frameObu = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome: mono, tile, best, lfLevel, screenContent: sct);
        if (lrOn && logs != null && TryLoopRestoration(seqCfg, seqObu, frameObu, logs, sbCols, sbRows, width, height, layout, monochrome: mono,
                srcY, srcU, srcV, cwIn, chIn, baseQIdx, best, lfLevel, pic, cdefIdx, sct) is { } withLr)
            return withLr;
        return (seqObu, frameObu);
    }

    /// <summary>A decoded (grain-free) frame: its SSE vs the source and, when kept, the native-depth planes.</summary>
    private sealed class DecodedPicture
    {
        public long Sse, SseY, SseU, SseV;
        public ushort[][]? Planes;
        public int[]? Strides;
        public byte[]? Noskip;          // 8x8 CDEF map (with padded planes, for the per-superblock CDEF search)
        public int W8, H8, RowsY, RowsC;
    }

    private static DecodedPicture? DecodePicture(byte[] seqObu, byte[] frameObu, ushort[] srcY, ushort[]? srcU, ushort[]? srcV,
        int width, int height, int cw, int ch, bool monochrome, bool keep, int threads = 1, bool noskip = false)
    {
        var dec = new Av1Decoder { ApplyFilmGrain = false, MaxThreads = threads };
        using var yuv = dec.Decode([.. seqObu, .. frameObu], 0, isKeyframe: true);
        if (yuv == null) return null;
        var d = new DecodedPicture();
        d.Sse = DecodedSse(yuv, srcY, srcU, srcV, width, height, cw, ch, monochrome, out d.SseY, out d.SseU, out d.SseV);
        if (!keep) return d;
        if (noskip)
        {
            // The CDEF search filters the MI grid (8-aligned) as the decoder does, reading the reconstruction past the
            // visible edges and two rows below: take the decoder's internal planes, not its cropped output.
            d.Noskip = dec.NoskipMap8x8(out d.W8, out d.H8);
            d.RowsY = d.H8 * 8 + 2;
            d.RowsC = ((d.H8 * 8) >> (ch < height ? 1 : 0)) + 2;   // vertically subsampled chroma (4:2:0) or not
            int[] st;
            d.Planes = dec.InternalPlanes(d.RowsY, d.RowsC, out st);
            d.Strides = st;
            return d;
        }
        ushort[] Plane(int p, int stride, int ph)
        {
            var a = new ushort[stride * ph];
            int avail;
            if (yuv.BitDepth > 8)
            {
                var b = (p == 0 ? yuv.YPlane16 : p == 1 ? yuv.UPlane16 : yuv.VPlane16).Span;
                avail = Math.Min(a.Length, b.Length) / stride * stride;
                b[..avail].CopyTo(a);
            }
            else
            {
                var b = (p == 0 ? yuv.YPlane : p == 1 ? yuv.UPlane : yuv.VPlane).Span;
                avail = Math.Min(a.Length, b.Length) / stride * stride;
                for (int i = 0; i < avail; i++) a[i] = b[i];
            }
            for (int o = avail; o + stride <= a.Length; o += stride) Array.Copy(a, avail - stride, a, o, stride);
            return a;
        }
        d.Strides = monochrome ? [yuv.YStride] : [yuv.YStride, yuv.UStride, yuv.VStride];
        d.Planes = monochrome ? [Plane(0, yuv.YStride, height)] : [Plane(0, yuv.YStride, height), Plane(1, yuv.UStride, ch), Plane(2, yuv.VStride, ch)];
        return d;
    }

    /// <summary>Colour-path deblocking level + CDEF strength: the full search (levels, then CDEF at the chosen level),
    /// or with FilterSearchFast one concurrent round of {no, q-guess level} x {no, heuristic CDEF}; FilterPickFromQ takes
    /// the guesses without decoding. Every search keeps the no-filter option, so it never loses to it. Returns the
    /// winner's decode (planes kept when asked) for the loop-restoration stage, or null when nothing was decoded.</summary>
    private static (Av1ObuWriter.LfLevels Lf, Av1ObuWriter.CdefParams Cdef, DecodedPicture? Pic) SearchColorFilters(byte[] seqObu, byte[] tile,
        int baseQIdx, int sbCols, int sbRows, int width, int height, ushort[] srcY, ushort[] srcU, ushort[] srcV, int cw, int ch,
        bool keepPicture, bool deblockOnly = false, bool mono = false, bool sct = false)
    {
        bool sizeOk = (long)width * height <= FilterSearchMaxPixels;
        bool dbOn = UseDeblockSearch && sizeOk, cdOn = UseCdefSearch && baseQIdx >= 64 && sizeOk && !deblockOnly;
        // deblockOnly: CDEF is searched per superblock afterwards; keep the winner's planes + noskip map for it.
        bool keepNoskip = deblockOnly;
        if (deblockOnly) keepPicture = true;
        int guess = Sp.LfGuessLibaom ? LibaomLfFromQ(baseQIdx) : Math.Clamp(baseQIdx / 8, 1, 40);
        int damping = Math.Clamp(3 + (baseQIdx >> 6), 3, 6);
        int yLvl = (Math.Clamp(baseQIdx / 16, 1, 12) << 2) | (baseQIdx >= 128 ? 2 : 1);
        int uvLvl = (Math.Clamp(baseQIdx / 24, 1, 8) << 2) | (baseQIdx >= 160 ? 1 : 0);
        var heur = new Av1ObuWriter.CdefParams(damping, 0, new[] { (byte)yLvl }, new[] { (byte)(mono ? 0 : uvLvl) });
        var none = Av1ObuWriter.CdefParams.None;
        if (FilterPickFromQ) return (dbOn ? guess : 0, cdOn ? heur : none, null);
        if (!dbOn && !cdOn) return (0, none, null);
        if (Sp.DeblockPickFromQ && dbOn)
        {
            // Deblocking level from q (libaom LPF_PICK_FROM_Q); CDEF still chosen (here, or per superblock afterwards).
            var fromQ = new List<(int, Av1ObuWriter.CdefParams)> { (guess, none) };
            if (cdOn) fromQ.Add((guess, heur));
            return Best(fromQ, keepPicture);
        }
        if (!cdOn && FilterSearchFast) return Best(dbOn ? [(0, none), (guess, none)] : [(0, none)], keepPicture);
        if (Sp.DeblockPerPlane && dbOn && !cdOn && !mono) return PerPlane();

        // Luma / U / V deblocking levels chosen independently (no CDEF in these decodes, so the planes do not interact).
        (Av1ObuWriter.LfLevels, Av1ObuWriter.CdefParams, DecodedPicture?) PerPlane()
        {
            var ladder = new SortedSet<int> { 0 };
            foreach (double f in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 }) ladder.Add(Math.Clamp((int)Math.Round(guess * f), 1, 63));
            var lv = ladder.ToList();
            var pics = new DecodedPicture?[lv.Count];
            EvaluateAll(lv.Count, i =>
            {
                pics[i] = DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, none, lv[i], screenContent: sct),
                    srcY, srcU, srcV, width, height, cw, ch, mono, false, Math.Max(1, ThreadCount / lv.Count));
                return pics[i]?.Sse ?? long.MaxValue;
            });
            var sse = new Dictionary<int, (long Y, long U, long V)>();
            for (int i = 0; i < lv.Count; i++) if (pics[i] != null) sse[lv[i]] = (pics[i]!.SseY, pics[i]!.SseU, pics[i]!.SseV);
            int by = 0, bu = 0, bv = 0;
            void Pick()
            {
                foreach (var (l, s) in sse)
                {
                    if (s.Y < sse[by].Y) by = l;
                    if (s.U < sse[bu].U) bu = l;
                    if (s.V < sse[bv].V) bv = l;
                }
            }
            Pick();
            for (int step = Sp.DeblockRefine; step >= 1; step /= 2)
            {
                // one decode per offset tests all three planes at their own neighbouring level
                var offs = new[] { -step, step };
                var tri = offs.Select(o => (Y: Math.Clamp(by + o, 0, 63), U: Math.Clamp(bu + o, 0, 63), V: Math.Clamp(bv + o, 0, 63))).ToArray();
                var rp = new DecodedPicture?[tri.Length];
                EvaluateAll(tri.Length, i =>
                {
                    var t = tri[i];
                    if (t.Y == 0) return long.MaxValue;   // chroma levels are not coded with luma off
                    rp[i] = DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, none,
                            new Av1ObuWriter.LfLevels(t.Y, t.Y, t.U, t.V), screenContent: sct),
                        srcY, srcU, srcV, width, height, cw, ch, mono, false, Math.Max(1, ThreadCount / tri.Length));
                    return rp[i]?.Sse ?? long.MaxValue;
                });
                for (int i = 0; i < tri.Length; i++)
                {
                    if (rp[i] == null) continue;
                    var t = tri[i];
                    if (rp[i]!.SseY < sse[by].Y) { sse.TryAdd(t.Y, (long.MaxValue, long.MaxValue, long.MaxValue)); sse[t.Y] = (rp[i]!.SseY, sse[t.Y].U, sse[t.Y].V); }
                    if (rp[i]!.SseU < sse[bu].U) { sse.TryAdd(t.U, (long.MaxValue, long.MaxValue, long.MaxValue)); sse[t.U] = (sse[t.U].Y, rp[i]!.SseU, sse[t.U].V); }
                    if (rp[i]!.SseV < sse[bv].V) { sse.TryAdd(t.V, (long.MaxValue, long.MaxValue, long.MaxValue)); sse[t.V] = (sse[t.V].Y, sse[t.V].U, rp[i]!.SseV); }
                }
                Pick();
            }
            var best = by == 0 ? new Av1ObuWriter.LfLevels(0, 0, 0, 0) : new Av1ObuWriter.LfLevels(by, by, bu, bv);
            // the winner's decode (planes and noskip map when the caller needs them)
            var wp = keepPicture ? DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, none, best, screenContent: sct),
                srcY, srcU, srcV, width, height, cw, ch, mono, true, ThreadCount, keepNoskip) : null;
            return (best, none, wp);
        }

        (int, Av1ObuWriter.CdefParams, DecodedPicture?) Best(List<(int Lf, Av1ObuWriter.CdefParams Cdef)> cands, bool keep)
        {
            var pics = new DecodedPicture?[cands.Count];
            EvaluateAll(cands.Count, i =>
            {
                pics[i] = DecodePicture(seqObu, BuildFrameObu(baseQIdx, sbCols, sbRows, mono, tile, cands[i].Cdef, cands[i].Lf, screenContent: sct),
                    srcY, srcU, srcV, width, height, cw, ch, mono, keep, Math.Max(1, ThreadCount / cands.Count), keepNoskip);
                return pics[i]?.Sse ?? long.MaxValue;
            });
            int bi = 0;
            for (int i = 1; i < cands.Count; i++) if ((pics[i]?.Sse ?? long.MaxValue) < (pics[bi]?.Sse ?? long.MaxValue)) bi = i;
            return (cands[bi].Lf, cands[bi].Cdef, pics[bi]);
        }

        if (FilterSearchFast)
        {
            var c = new List<(int, Av1ObuWriter.CdefParams)> { (0, none) };
            if (dbOn) c.Add((guess, none));
            if (cdOn) { c.Add((0, heur)); if (dbOn) c.Add((guess, heur)); }
            return Best(c, keepPicture);
        }
        int lf = 0;
        if (dbOn)
        {
            var levels = new List<(int, Av1ObuWriter.CdefParams)> { (0, none) };
            foreach (int lvl in new[] { guess / 2, guess, Math.Min(guess * 3 / 2, 63) }) if (lvl > 0) levels.Add((lvl, none));
            var (l, _, p0) = Best(levels, keepPicture && !cdOn);
            lf = l;
            if (!cdOn) return (lf, none, p0);
        }
        return Best([(lf, none), (lf, heur)], keepPicture);
    }

    /// <summary>Loop-restoration stage: decodes the filtered (deblock + CDEF) frame, searches Wiener / self-guided
    /// units against the source, replays the recorded tiles with the unit syntax at each superblock and rebuilds the
    /// headers with restoration enabled. Returns null (keep the frame as is) unless the decoded result is better in
    /// SSE + λ·bits.</summary>
    private static (byte[] SeqObu, byte[] FrameObu)? TryLoopRestoration(in Av1ObuWriter.SeqConfig seqCfg, byte[] seqObu, byte[] frameObu,
        List<Av1MsacWriter.LogOp>[] logs, int sbCols, int sbRows, int width, int height, Av1PixelLayout layout, bool monochrome,
        ushort[] srcY, ushort[]? srcU, ushort[]? srcV, int cw, int ch, int baseQIdx, Av1ObuWriter.CdefParams cdef, Av1ObuWriter.LfLevels lfLevel,
        DecodedPicture? pic = null, sbyte[]? cdefIdx = null, bool sct = false)
    {
        int acDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];
        double lambda = LamK * acDq * acDq;
        bool i420 = layout == Av1PixelLayout.I420;
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = i420 ? 1 : 0;

        if (pic?.Planes == null)
            pic = DecodePicture(seqObu, frameObu, srcY, srcU, srcV, width, height, cw, ch, monochrome, keep: true, ThreadCount);
        if (pic?.Planes == null) return null;
        ushort[][] rec = pic.Planes; int[] recStrides = pic.Strides!; long sseNoLr = pic.Sse;
        ushort[][] src = monochrome ? [srcY] : [srcY, srcU!, srcV!];
        var plan = Av1LrEncoder.Search(src, rec, [width, cw, cw], [height, ch, ch], [width, cw, cw], recStrides,
            monochrome, i420, Bd, lambda, LrSgrSets, LrWienerRounds, LrStatsStep, ThreadCount,
            Enumerable.Range(0, 3).Where(sh => (LrUnitShiftMask >> sh & 1) != 0).ToArray(),
            new Av1LrEncoder.LrPrune(Sp.LrSgrEp, sct ? Sp.LrSgrOnWienerScreen : Sp.LrSgrOnWiener, Sp.LrWienerSrcVar, Sp.LrReduceWiener,
                Sp.LrDualSgrPenalty, Av1Tables.DequantTable[BdIdx, baseQIdx, 0] >> 3));
        if (plan == null) return null;

        // Replay each tile with the restoration syntax at its superblocks (fresh restoration CDFs / references per tile).
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var tiles = new byte[logs.Length][];
        for (int ti = 0; ti < logs.Length; ti++)
        {
            var cdf = new Av1CdfContext();
            Av1CdfDefaults.InitializeDefault(cdf, qcat);
            var ts = new Av1LrEncoder.TileState(cdf);
            var cdefW = cdefIdx != null ? CdefIndexWriter(cdefIdx, sbCols, cdef.Bits) : null;
            tiles[ti] = Av1MsacWriter.Replay(logs[ti], (id, w) =>
            {
                if (id < 0) { cdefW?.Invoke(id, w); return; }
                Av1LrEncoder.WriteSb(w, ts, plan, (id & 0xFFFF) * 16, (id >> 16) * 16, width, height, ssX, ssY, monochrome);
            }, Av1CdfIndex.Arrays(cdf));
        }
        byte[] seq2, frame2;
        using (Av1ObuWriter.UseRestoration(plan, i420))
        {
            seq2 = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
            frame2 = BuildFrameObu(baseQIdx, sbCols, sbRows, monochrome, AssembleTileGroup(tiles), cdef, lfLevel, screenContent: sct);
        }
        if (!LrVerify) return (seq2, frame2);
        using var yuv2 = new Av1Decoder { ApplyFilmGrain = false, MaxThreads = ThreadCount }.Decode([.. seq2, .. frame2], 0, isKeyframe: true);
        if (yuv2 == null) return null;
        long sseLr = DecodedSse(yuv2, srcY, srcU, srcV, width, height, cw, ch, monochrome);
        double jNo = sseNoLr + lambda * 8 * (seqObu.Length + frameObu.Length);
        double jLr = sseLr + lambda * 8 * (seq2.Length + frame2.Length);
        return jLr < jNo ? (seq2, frame2) : null;
    }

    /// <summary>Lossless key frame (base_q_idx 0, 4x4 WHT, no in-loop filters) for a colour image in any chroma layout
    /// (<paramref name="u"/>/<paramref name="v"/> null = monochrome, e.g. an alpha plane). The decoded samples equal the
    /// input exactly.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildLosslessObus(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u,
        ReadOnlySpan<ushort> v, bool monochrome, int width, int height, int bitDepth, Av1PixelLayout layout,
        Av1ObuWriter.Av1ColorDesc? color)
    {
        using var bdScope = new BitDepthScope(bitDepth);
        ValidateMultiSb(width, height, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph);
        ushort[] padY = PadPlane(luma, width, height, pw, ph);
        ushort[]? padU = null, padV = null;
        if (!monochrome)
        {
            int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
            int cwIn = (width + ssX) >> ssX, chIn = (height + ssY) >> ssY;
            padU = PadPlane(u, cwIn, chIn, pw >> ssX, ph >> ssY);
            padV = PadPlane(v, cwIn, chIn, pw >> ssX, ph >> ssY);
        }
        byte[] tile = Av1LosslessEncoder.EncodeTiles(padY, padU, padV, pw, bw4, bh4, sbCols, sbRows,
            monochrome ? Av1PixelLayout.I400 : layout, bitDepth, UseIntraEdgeFilter);
        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome, enableFilterIntra: false, bitDepth: bitDepth,
            layout: layout, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(0, isObuFrame: true, sbCols, sbRows, monochrome,
            txModeSelect: false, Av1ObuWriter.CdefParams.None, 0, screenContentTools: false, reducedTxSet: true);
        var payload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(payload, 0);
        tile.CopyTo(payload.AsSpan(frameHdr.Length));
        return (seqObu, Av1ObuWriter.WrapObu(Av1ObuType.Frame, payload));
    }

    /// <summary>Lossless AVIF: colour (any layout) or monochrome, plus an optional lossless alpha item.</summary>
    internal static byte[] EncodeAvifLossless(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        bool monochrome, ReadOnlySpan<ushort> alpha, bool hasAlpha, int width, int height, int bitDepth, Av1PixelLayout layout,
        Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? extras, int alphaQIdx = 0)
    {
        (byte[] cSeq, byte[] cFrame) = BuildLosslessObus(luma, u, v, monochrome, width, height, bitDepth, layout, color);
        if (!hasAlpha)
            return Av1AvifWriter.BuildAvif(cSeq, cFrame, width, height, monochrome, bitDepth, layout, color, extras);
        (byte[] aSeq, byte[] aFrame) = BuildAlphaObus(alpha, width, height, alphaQIdx, bitDepth);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, monochrome, bitDepth, layout, color, extras);
    }

    /// <summary>Encodes an I420 colour image plus an 8-bit alpha plane into a 2-item AVIF: a primary colour
    /// `av01` item and a monochrome alpha auxiliary item, linked by an `auxl` item reference. Alpha is coded as a
    /// full-range monochrome AV1 image (the standard AVIF alpha representation).</summary>
    internal static byte[] EncodeAvifColorWithAlpha(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha, int width, int height, int baseQIdx, int alphaQIdx, Av1ObuWriter.Av1ColorDesc? color = null,
        AvifContainerExtras? extras = null)
        => EncodeAvifColorWithAlpha(Widen(luma), Widen(u), Widen(v), Widen(alpha), width, height, baseQIdx, alphaQIdx, 8, color: color, extras: extras);

    /// <summary>High-bit-depth colour + alpha (alpha coded at the same depth, as libavif does).</summary>
    internal static byte[] EncodeAvifColorWithAlpha(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        ReadOnlySpan<ushort> alpha, int width, int height, int baseQIdx, int alphaQIdx, int bitDepth,
        Av1PixelLayout layout = Av1PixelLayout.I420, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] cSeq, byte[] cFrame) = BuildColorObus(luma, u, v, width, height, baseQIdx, bitDepth, layout, color);
        // Alpha: no colour description, always full range (AVIF forbids limited-range alpha).
        (byte[] aSeq, byte[] aFrame) = BuildAlphaObus(alpha, width, height, alphaQIdx, bitDepth);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, colorMonochrome: false, bitDepth, layout, color, extras);
    }

    /// <summary>The alpha item's OBUs: lossless (4x4 WHT, base_q_idx 0) when <paramref name="alphaQIdx"/> is 0 (libavif
    /// quality 100, avifenc's default --qalpha), else a lossy monochrome frame. Film grain never applies to alpha.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildAlphaObus(ReadOnlySpan<ushort> alpha, int width, int height, int alphaQIdx, int bitDepth)
    {
        using (new Av1ObuWriter.SuppressFilmGrain(true))
            return alphaQIdx <= 0
                ? BuildLosslessObus(alpha, default, default, true, width, height, bitDepth, Av1PixelLayout.I400, null)
                : BuildMonochromeObus(alpha, width, height, alphaQIdx, bitDepth);
    }

    /// <summary>Monochrome (4:0:0) colour + alpha: both items coded as monochrome AV1 at the same depth; the colour item
    /// carries the colour description, the alpha item none (always full range).</summary>
    internal static byte[] EncodeAvifMonochromeWithAlpha(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> alpha, int width, int height,
        int baseQIdx, int alphaQIdx, int bitDepth, Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        (byte[] cSeq, byte[] cFrame) = BuildMonochromeObus(luma, width, height, baseQIdx, bitDepth, color);
        (byte[] aSeq, byte[] aFrame) = BuildAlphaObus(alpha, width, height, alphaQIdx, bitDepth);
        return Av1AvifWriter.BuildAvifWithAlpha(cSeq, cFrame, aSeq, aFrame, width, height, colorMonochrome: true, bitDepth,
            Av1PixelLayout.I400, color, extras);
    }

    /// <summary>One layer of a layered (progressive) still image: planes at the layer's size and its quantizers.</summary>
    internal readonly record struct LayerInput(ushort[] Y, ushort[]? U, ushort[]? V, ushort[]? Alpha, int Width, int Height,
        int QIdx, int AlphaQIdx);

    /// <summary>
    /// Layered AVIF (avifenc --progressive / --layered): each layer is coded as its own frame (a key frame for the base,
    /// intra-only frames above it) in one temporal unit with spatial-id extensions; the item's payload concatenates them
    /// and 'a1lx' records the per-layer sizes. Alpha, when present, is layered the same way.
    /// </summary>
    internal static byte[] EncodeAvifLayered(IReadOnlyList<LayerInput> layers, bool monochrome, int bitDepth,
        Av1PixelLayout layout, Av1ObuWriter.Av1ColorDesc? color, AvifContainerExtras? extras)
    {
        var ls = new Av1ObuWriter.LayeredStream
        {
            Layers = layers.Count, MaxWidth = layers[^1].Width, MaxHeight = layers[^1].Height,
            Widths = layers.Select(l => l.Width).ToArray(), Heights = layers.Select(l => l.Height).ToArray(),
        };
        (byte[] Data, long[] Sizes) Build(bool alpha)
        {
            byte[]? seq = null;
            var frames = new List<byte[]>();
            using (Av1ObuWriter.UseLayers(ls))
            using (new Av1ObuWriter.SuppressFilmGrain(alpha))
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    ls.Current = i;
                    var l = layers[i];
                    var (s, f) = alpha ? BuildMonochromeObus(l.Alpha!, l.Width, l.Height, l.AlphaQIdx, bitDepth)
                        : monochrome ? BuildMonochromeObus(l.Y, l.Width, l.Height, l.QIdx, bitDepth, color)
                        : BuildColorObus(l.Y, l.U!, l.V!, l.Width, l.Height, l.QIdx, bitDepth, layout, color);
                    seq ??= s;
                    frames.Add(f);
                }
            }
            var sizes = new long[layers.Count];
            sizes[0] = seq!.Length + frames[0].Length;
            for (int i = 1; i < frames.Count; i++) sizes[i] = frames[i].Length;
            var data = new byte[sizes.Sum()];
            int o = 0;
            foreach (var part in new[] { seq }.Concat(frames)) { part.CopyTo(data, o); o += part.Length; }
            return (data, sizes);
        }

        extras ??= new AvifContainerExtras();
        var (cData, cSizes) = Build(false);
        extras.ColorLayerSizes = cSizes;
        if (layers[0].Alpha == null)
            return Av1AvifWriter.BuildAvif(cData, [], ls.MaxWidth, ls.MaxHeight, monochrome, bitDepth, layout, color, extras);
        var (aData, aSizes) = Build(true);
        extras.AlphaLayerSizes = aSizes;
        return Av1AvifWriter.BuildAvifWithAlpha(cData, [], aData, [], ls.MaxWidth, ls.MaxHeight, monochrome, bitDepth, layout, color, extras);
    }

    // Per-superblock recursive-partition state for I420 colour. Extends the grayscale scheme with two chroma
    // planes (half resolution): chroma follows the luma partition tree, each leaf coding U/V at half the luma
    // block size (down to 4x4 chroma for an 8x8 luma leaf).
    private sealed class ColorPartCtx
    {
        public Av1MsacWriter Msac = null!;
        public Av1CdfContext Cdf = null!;
        public ushort[] Luma = null!, U = null!, V = null!;
        public ushort[] ReconY = null!, ReconU = null!, ReconV = null!;
        public int W, Cw, Chh;         // luma stride, chroma stride, chroma height
        // Chroma layout: I420 (SsX=SsY=1, the tuned path), I422 (1,0) or I444 (0,0). Non-4:2:0 leaves go through
        // the layout-generic rect leaf (EncodeRectLeafColor), which sizes chroma as (w>>SsX) x (h>>SsY).
        public Av1PixelLayout Layout = Av1PixelLayout.I420;
        public int SsX = 1, SsY = 1;
        public bool Mono;              // monochrome: 4:2:0 geometry, but no chroma decided, coded or reconstructed
        public int Bw4, Bh4;           // REAL luma frame dims in 4-units
        public int DcDq, AcDq, QIdx;
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        // Neighbour UV-mode context (chroma 4-unit indexed, like ACU): the intra-edge smooth-neighbour filter for a
        // directional chroma prediction reads the adjacent blocks' UV modes (dav1d SmUvFlag). Stores the uv_mode
        // symbol (DC=0 .. Paeth=12, CfL=13); only Smooth/SmoothV/SmoothH trip the filter bit, so CfL/DC read as 0.
        public byte[] AModeUv = null!, LModeUv = null!;
        public sbyte[] ATxY = null!, LTxY = null!;   // neighbour luma tx log-size, for the tx-depth context (UseColorTxDepth)
        // Palette neighbour state (SB128-relative, mirrors the decoder's Above/Left.PalSz + PalPrevY): per 4-unit
        // position the covering block's luma palette size and (when >0) its colours, used for the has_palette
        // context and the colour-prediction cache. APal* are per-SB128-column (like AModeY); LPal* reset per SB row.
        public byte[] APalSz = null!, LPalSz = null!;         // [32] palette size at each position (0 = none)
        public ushort[] APalCol = null!, LPalCol = null!;     // [32*8] palette colours at each position
        public byte[] APalSzUv = null!, LPalSzUv = null!;     // chroma palette size / U colours (dav1d pal_sz_uv, pal[1])
        public ushort[] APalColU = null!, LPalColU = null!;
        public bool ScreenContent;     // allow_screen_content_tools: palette flags coded, palette searched
        public ushort[] Pred = new ushort[64 * 64];
        public ushort[] EstScratch = new ushort[64 * 64];
        // libaom reuse_best_prediction_for_part_ab: the last luma winner per (block size, 4x4 position in the 64x64
        // superblock) with the superblock it belongs to; AB-partition leaves search only that mode while set.
        public readonly int[] AomModeCache = new int[22 * 256], AomModeCacheSb = new int[22 * 256];
        public bool UseAomModeCache;
        // libaom rd_try_subblock (AomPartAbort): the running candidate's budget (the best partition J so far), its
        // start bits, coded-region SSE and lambda; set when it ran over (the candidate is then discarded).
        public double PartBudget = double.PositiveInfinity, PartBits0, PartSse, PartLambda;
        public bool PartAborted;
        // Per partition level: the J of each sub-block of the SPLIT / HORZ / VERT candidates (MaxValue = not coded),
        // SPLIT's sub-block count when it aborted and its J then (libaom rect_part_rd / split_rd, AB and rect prunes).
        public readonly double[] SubJ = new double[6 * 3 * 4];
        public readonly int[] SplitAbortN = new int[6];
        public readonly double[] SplitAbortJ = new double[6];
        // Per partition level and quadrant: whether that block's own HORZ (bit 0) / VERT (bit 1) search won when it
        // ran (set = won or not searched; libaom split_part_rect_win), read by the parent's 4-way prune.
        public readonly byte[] RectWin = new byte[7 * 4];
        // EstimateBlockCost is a pure function of the source block (source prediction) and the quantizers, and the
        // partition decision evaluates each block twice (as a parent's SPLIT child, then as its own NONE): cached per
        // SB128 position (1 + 4 + 16 + 64 + 256 slots for levels 0..4), reset when the superblock changes.
        public readonly long[] EstCache = new long[341];
        public readonly RdSnapshot?[] SnapPool = new RdSnapshot?[18];
        public readonly Av1MsacWriter.LogOp[][] LogTailPool = new Av1MsacWriter.LogOp[6][];   // per partition level
        public readonly List<Av1MsacWriter.LogOp>?[] NoneLogPool = new List<Av1MsacWriter.LogOp>?[6];
        public readonly double[] LogVar4 = new double[256];   // log1p(var / 16) of each 4x4 of the superblock LogVarSb
        public int LogVarSb = -1;
        public readonly int[] VbpAvg = new int[256];   // 4x4 source averages - 128 (8-bit scale) of the superblock VbpSb
        public int VbpSb = -1;
        public Av1PartitionCnn.SbOutput? Cnn;   // intra CNN partition features of the superblock CnnSb
        public int CnnSb = -1;
        public int EstCacheSb = -1, EstCacheDc, EstCacheAc;

        public long EstCost(int bl, int bx4, int by4)
        {
            int sb = ((by4 >> 5) << 16) | (bx4 >> 5);
            if (sb != EstCacheSb || DcDq != EstCacheDc || AcDq != EstCacheAc)
            { Array.Fill(EstCache, -1); EstCacheSb = sb; EstCacheDc = DcDq; EstCacheAc = AcDq; }
            int idx = ((1 << (2 * bl)) - 1) / 3 + ((((by4 & 31) >> (5 - bl)) << bl) | ((bx4 & 31) >> (5 - bl)));
            long v = EstCache[idx];
            if (v < 0) EstCache[idx] = v = EstimateBlockCost(Luma, W, Bw4, Bh4, DcDq, AcDq, EstScratch, bl, bx4, by4);
            return v;
        }
    }

    // One tile's row-parallel state: above contexts, per-row CDF seeds (copied from the row above after its second
    // superblock), per-row progress and op logs.
    private sealed class TileMt
    {
        public int R0, NRows, C0, NCols, SyncAfter;
        public byte[][] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!, AModeUv = null!, APalSz = null!, APalSzUv = null!;
        public sbyte[][] ATxY = null!;
        public ushort[][] APalCol = null!, APalColU = null!;
        public Av1CdfContext?[] RowInit = null!;
        public int[] Progress = null!;
        public List<Av1MsacWriter.LogOp>[] RowLogs = null!;
    }

    private static byte[] EncodeMultiSbColorTile(ushort[] luma, ushort[] uPlane, ushort[] vPlane,
        int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx, Av1PixelLayout layout = Av1PixelLayout.I420)
        => EncodeMultiSbColorTile(luma, uPlane, vPlane, w, h, sbCols, sbRows, bw4, bh4, baseQIdx, layout, null);

    /// <summary>As above; with <paramref name="tileLogs"/> (one slot per tile) each tile's coded operations are also
    /// recorded, with a marker (sby &lt;&lt; 16 | sbx) before every superblock, for a later Replay that inserts syntax
    /// decided after the tile (loop restoration).</summary>
    private static byte[] EncodeMultiSbColorTile(ushort[] luma, ushort[] uPlane, ushort[] vPlane,
        int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx, Av1PixelLayout layout,
        List<Av1MsacWriter.LogOp>[]? tileLogs, bool screenContent = false)
    {
        screenContent |= UsePalette;
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        bool mono = layout == Av1PixelLayout.I400;
        if (mono) layout = Av1PixelLayout.I420;   // the 4:2:0 machinery with Mono set: chroma is neither decided nor coded
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
        int cw = w >> ssX, chh = h >> ssY;

        int sb128Cols = (sbCols + 1) >> 1;
        // the padded source planes are only read: used as they are (no per-encode copies of whole planes)
        ushort[] lumaArr = luma.Length == w * h ? luma : luma.AsSpan(0, w * h).ToArray();
        ushort[] uArr = uPlane.Length == cw * chh ? uPlane : uPlane.AsSpan(0, cw * chh).ToArray();
        ushort[] vArr = vPlane.Length == cw * chh ? vPlane : vPlane.AsSpan(0, cw * chh).ToArray();
        ushort[] reconY = new ushort[w * h], reconU = new ushort[cw * chh], reconV = new ushort[cw * chh];
        int dcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 0], acDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];
        int bd = Bd;
        var speed = t_speed;
        var writerState = Av1ObuWriter.CaptureThreadState();

        // One entropy-coded tile per TileLayout tile: fresh default CDFs, writer and above contexts, and intra edges
        // confined to the tile (SetTileWindow). Tiles never predict from each other, so they are encoded in parallel
        // (each on its own context; the recon planes are written in disjoint regions) and the output does not depend
        // on the thread count.
        var (_, _, colStart, rowStart) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        int tileCols = colStart.Length - 1, tileRows = rowStart.Length - 1;
        var tiles = new byte[tileCols * tileRows][];
        void EncodeTile(int ti)
        {
            int tr = ti / tileCols, tc = ti % tileCols;
            using var bdScope = new BitDepthScope(bd);
            var prevSpeed = t_speed;
            t_speed = speed;
            var prevWriter = Av1ObuWriter.ExchangeThreadState(writerState);
            var tcdf = new Av1CdfContext();
            Av1CdfDefaults.InitializeDefault(tcdf, qcat);
            var c = new ColorPartCtx
            {
                Msac = new Av1MsacWriter { Log = tileLogs != null ? new() : null }, Cdf = tcdf, Luma = lumaArr, U = uArr, V = vArr,
                ReconY = reconY, ReconU = reconU, ReconV = reconV,
                W = w, Cw = cw, Chh = chh, Bw4 = bw4, Bh4 = bh4, Layout = layout, SsX = ssX, SsY = ssY,
                DcDq = dcDq, AcDq = acDq, Mono = mono, ScreenContent = screenContent, QIdx = baseQIdx,
            };
            var abovePart = new byte[sb128Cols][];
            var aLY = FilledArray(sb128Cols); var aCU = FilledArray(sb128Cols); var aCV = FilledArray(sb128Cols);
            var aModeY = new byte[sb128Cols][]; var aSkip = new byte[sb128Cols][]; var aTxY = new sbyte[sb128Cols][];
            var aModeUv = new byte[sb128Cols][];
            var aPalSz = new byte[sb128Cols][]; var aPalCol = new ushort[sb128Cols][];
            var aPalSzUv = new byte[sb128Cols][]; var aPalColU = new ushort[sb128Cols][];
            for (int i = 0; i < sb128Cols; i++) { abovePart[i] = new byte[16]; aModeY[i] = new byte[32]; aSkip[i] = new byte[32]; aTxY[i] = FilledSbyte(32, -1); aModeUv[i] = new byte[32]; aPalSz[i] = new byte[32]; aPalCol[i] = new ushort[32 * 8]; aPalSzUv[i] = new byte[32]; aPalColU[i] = new ushort[32 * 8]; }
            SetTileWindow(colStart[tc] * 16, rowStart[tr] * 16, Math.Min(colStart[tc + 1] * 16, bw4),
                Math.Min(rowStart[tr + 1] * 16, bh4), w, ssX, ssY);
            try
            {
                for (int sby = rowStart[tr]; sby < rowStart[tr + 1]; sby++)
                {
                    c.LeftPart = new byte[16]; c.LLY = Filled(32); c.LCU = Filled(32); c.LCV = Filled(32);
                    c.LModeY = new byte[32]; c.LSkip = new byte[32]; c.LTxY = FilledSbyte(32, -1);
                    c.LModeUv = new byte[32];
                    c.LPalSz = new byte[32]; c.LPalCol = new ushort[32 * 8]; c.LPalSzUv = new byte[32]; c.LPalColU = new ushort[32 * 8];
                    for (int sbx = colStart[tc]; sbx < colStart[tc + 1]; sbx++)
                    {
                        int col = sbx >> 1;
                        c.AbovePart = abovePart[col]; c.ALY = aLY[col]; c.ACU = aCU[col]; c.ACV = aCV[col];
                        c.AModeY = aModeY[col]; c.ASkip = aSkip[col]; c.ATxY = aTxY[col];
                        c.AModeUv = aModeUv[col];
                        c.APalSz = aPalSz[col]; c.APalCol = aPalCol[col]; c.APalSzUv = aPalSzUv[col]; c.APalColU = aPalColU[col];
                        c.Msac.Mark((sby << 16) | sbx);
                        t_sbLamMul = Sp.AomSbLambda ? SbLambdaMul(c, sbx, sby) : 0;
                        EncodePartitionColor(c, 1, sbx * 16, sby * 16);
                        t_sbLamMul = 0;
                    }
                }
                if (tileLogs != null) tileLogs[ti] = c.Msac.Log!;
                tiles[ti] = c.Msac.Finish();
            }
            finally
            {
                ClearTileWindow();
                t_speed = prevSpeed;
                Av1ObuWriter.ExchangeThreadState(prevWriter);
            }
        }
        // Row-parallel (see Av1EncodeSpeed.RowMt): every tile's superblock rows run as a wavefront, and all tiles at once
        // from one queue of rows interleaved across tiles (row 0 of every tile, then row 1, ...). A row only waits for the
        // row above it in its own tile, which was always claimed earlier, so a worker never waits on unclaimed work.
        var mts = new TileMt[tiles.Length];
        for (int ti = 0; ti < tiles.Length; ti++)
        {
            int tr = ti / tileCols, tc = ti % tileCols;
            var st = mts[ti] = new TileMt
            {
                R0 = rowStart[tr], NRows = rowStart[tr + 1] - rowStart[tr], C0 = colStart[tc], NCols = colStart[tc + 1] - colStart[tc],
                AbovePart = new byte[sb128Cols][], ALY = FilledArray(sb128Cols), ACU = FilledArray(sb128Cols), ACV = FilledArray(sb128Cols),
                AModeY = new byte[sb128Cols][], ASkip = new byte[sb128Cols][], ATxY = new sbyte[sb128Cols][], AModeUv = new byte[sb128Cols][],
                APalSz = new byte[sb128Cols][], APalCol = new ushort[sb128Cols][], APalSzUv = new byte[sb128Cols][], APalColU = new ushort[sb128Cols][],
            };
            for (int i = 0; i < sb128Cols; i++) { st.AbovePart[i] = new byte[16]; st.AModeY[i] = new byte[32]; st.ASkip[i] = new byte[32]; st.ATxY[i] = FilledSbyte(32, -1); st.AModeUv[i] = new byte[32]; st.APalSz[i] = new byte[32]; st.APalCol[i] = new ushort[32 * 8]; st.APalSzUv[i] = new byte[32]; st.APalColU[i] = new ushort[32 * 8]; }
            st.RowInit = new Av1CdfContext?[st.NRows];
            st.RowInit[0] = new Av1CdfContext();
            Av1CdfDefaults.InitializeDefault(st.RowInit[0]!, qcat);
            st.Progress = new int[st.NRows];
            st.RowLogs = new List<Av1MsacWriter.LogOp>[st.NRows];
            st.SyncAfter = Math.Min(2, st.NCols);   // the row below starts once this many superblocks are done
        }
        var work = new List<(int Ti, int R)>();
        for (int r = 0, maxRows = mts.Max(m => m.NRows); r < maxRows; r++)
            for (int ti = 0; ti < mts.Length; ti++) if (r < mts[ti].NRows) work.Add((ti, r));
        int nextItem = 0;
        var gate = new object();
        bool failed = false;

        // Progress waits: a short spin, then the shared monitor (Publish pulses it) — never Thread.Sleep(1), whose
        // timer-tick granularity would stall the wavefront.
        void WaitFor(TileMt st, int row, int count)
        {
            for (int i = 0; i < 64; i++)
            {
                if (System.Threading.Volatile.Read(ref st.Progress[row]) >= count) return;
                System.Threading.Thread.SpinWait(20);
            }
            lock (gate)
                while (System.Threading.Volatile.Read(ref st.Progress[row]) < count)
                {
                    if (failed) throw new OperationCanceledException();
                    System.Threading.Monitor.Wait(gate);
                }
        }
        void Publish(TileMt st, int row, int value)
        {
            lock (gate) { st.Progress[row] = value; System.Threading.Monitor.PulseAll(gate); }
        }

        void EncodeRow(int ti, int r)
        {
            var st = mts[ti];
            SetTileWindow(st.C0 * 16, st.R0 * 16, Math.Min((st.C0 + st.NCols) * 16, bw4), Math.Min((st.R0 + st.NRows) * 16, bh4), w, ssX, ssY);
            if (r > 0) WaitFor(st, r - 1, st.SyncAfter);
            var (rcdf, index) = Av1CdfIndex.CreatePinned(st.RowInit[r]!);
            st.RowInit[r] = null;
            var c = new ColorPartCtx
            {
                Msac = new Av1MsacWriter { Log = new(st.NCols * 1024), SymIndex = index, DiscardOutput = true }, Cdf = rcdf, Luma = lumaArr, U = uArr, V = vArr,
                ReconY = reconY, ReconU = reconU, ReconV = reconV,
                W = w, Cw = cw, Chh = chh, Bw4 = bw4, Bh4 = bh4, Layout = layout, SsX = ssX, SsY = ssY,
                DcDq = dcDq, AcDq = acDq, Mono = mono, ScreenContent = screenContent, QIdx = baseQIdx,
            };
            int sby = st.R0 + r;
            c.LeftPart = new byte[16]; c.LLY = Filled(32); c.LCU = Filled(32); c.LCV = Filled(32);
            c.LModeY = new byte[32]; c.LSkip = new byte[32]; c.LTxY = FilledSbyte(32, -1);
            c.LModeUv = new byte[32];
            c.LPalSz = new byte[32]; c.LPalCol = new ushort[32 * 8]; c.LPalSzUv = new byte[32]; c.LPalColU = new ushort[32 * 8];
            for (int k = 0; k < st.NCols; k++)
            {
                // the superblock above-right must be reconstructed (top-right edge, above contexts)
                if (r > 0) WaitFor(st, r - 1, Math.Min(k + 2, st.NCols));
                int sbx = st.C0 + k, col = sbx >> 1;
                c.AbovePart = st.AbovePart[col]; c.ALY = st.ALY[col]; c.ACU = st.ACU[col]; c.ACV = st.ACV[col];
                c.AModeY = st.AModeY[col]; c.ASkip = st.ASkip[col]; c.ATxY = st.ATxY[col];
                c.AModeUv = st.AModeUv[col];
                c.APalSz = st.APalSz[col]; c.APalCol = st.APalCol[col]; c.APalSzUv = st.APalSzUv[col]; c.APalColU = st.APalColU[col];
                c.Msac.Mark((sby << 16) | sbx);
                t_sbLamMul = Sp.AomSbLambda ? SbLambdaMul(c, sbx, sby) : 0;
                EncodePartitionColor(c, 1, sbx * 16, sby * 16);
                t_sbLamMul = 0;
                if (k + 1 == st.SyncAfter && r + 1 < st.NRows)
                {
                    var snap = new Av1CdfContext();
                    snap.CopyFrom(rcdf);
                    st.RowInit[r + 1] = snap;
                }
                Publish(st, r, k + 1);
            }
            st.RowLogs[r] = c.Msac.Log!;
        }

        void Worker(int _)
        {
            using var bdScope = new BitDepthScope(bd);
            var prevSpeed = t_speed;
            t_speed = speed;
            var prevWriter = Av1ObuWriter.ExchangeThreadState(writerState);
            try
            {
                int i;
                while ((i = System.Threading.Interlocked.Increment(ref nextItem) - 1) < work.Count) EncodeRow(work[i].Ti, work[i].R);
            }
            catch (OperationCanceledException) when (failed) { }
            catch { lock (gate) { failed = true; System.Threading.Monitor.PulseAll(gate); } throw; }
            finally
            {
                ClearTileWindow();
                t_speed = prevSpeed;
                Av1ObuWriter.ExchangeThreadState(prevWriter);
            }
        }

        // The tile's coded order is its rows in order; replay them against the real adaptive CDFs.
        void FinishTile(int ti)
        {
            var st = mts[ti];
            var all = new List<Av1MsacWriter.LogOp>(st.RowLogs.Sum(l => l.Count));
            foreach (var l in st.RowLogs) all.AddRange(l);
            if (tileLogs != null) tileLogs[ti] = all;
            tiles[ti] = Av1MsacWriter.Replay(all, null, FreshCdfArrays(baseQIdx));
        }

        if (t_verifyRecon) t_lastRecon = ([reconY, reconU, reconV], w, cw);
        if (Sp.RowMt)
        {
            int workers = Math.Max(1, Math.Min(ThreadCount, work.Count));
            if (workers == 1) Worker(0);
            else System.Threading.Tasks.Parallel.For(0, workers, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = workers }, Worker);
            if (tiles.Length > 1 && ThreadCount > 1)
                System.Threading.Tasks.Parallel.For(0, tiles.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = ThreadCount }, FinishTile);
            else
                for (int ti = 0; ti < tiles.Length; ti++) FinishTile(ti);
            return AssembleTileGroup(tiles);
        }

        int threads = Math.Min(ThreadCount, tiles.Length);
        if (threads <= 1) for (int ti = 0; ti < tiles.Length; ti++) EncodeTile(ti);
        else System.Threading.Tasks.Parallel.For(0, tiles.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, EncodeTile);

        return AssembleTileGroup(tiles);
    }

    /// <summary>Test hook (this thread's encodes): after coding, decode the tile without in-loop filters and require the
    /// encoder's own reconstruction to match it sample for sample — the encoder predicts from exactly what a decoder
    /// will have. A mismatch throws.</summary>
    [ThreadStatic] internal static bool t_verifyRecon;

    // Dev oracle (partition + qindex of another encoder's stream, see the encq probe's ORACLE_DIR): per 4x4 unit the
    // origin (x | y << 16, 4-units) and dav1d block size of the block covering it; ForceQIdx replaces the quality map.
    internal static int[]? OracleOrigin;
    internal static byte[]? OracleBs;
    internal static int OracleW4;
    internal static int ForceQIdx = -1;
    // Dev: the padded luma plane (and its dims) each colour encode codes.
    internal static Action<ushort[], int, int>? SourceHook;
    // Dev decision oracle (ORACLE_MODES): per block origin (x | y << 16) the other encoder's block size, luma mode,
    // angle delta (filter mode for filter intra) and tx size; per luma tx block origin its tx type.
    internal static Dictionary<int, (byte Bs, byte YMode, sbyte YAngle, byte Tx)>? OracleModes;
    internal static Dictionary<int, byte>? OracleTxtp;
    // Dev level oracle (ORACLE_LEVELS): per luma tx block origin the other encoder's tx size and signed levels.
    internal static Dictionary<int, (int Tx, int[] Lv)>? OracleLevels;

    private static int OracleChoice(int bx4, int by4, int n)
    {
        (int X, int Y, int W, int H) B(int x4, int y4)
        {
            int i = y4 * OracleW4 + x4;
            if (i < 0 || i >= OracleBs!.Length) return (-1, -1, 0, 0);
            int o = OracleOrigin![i], bs = OracleBs[i];
            return (o & 0xFFFF, o >> 16, Av1Tables.BlockDimensions[bs, 0], Av1Tables.BlockDimensions[bs, 1]);
        }
        var b = B(bx4, by4);
        if (b.X != bx4 || b.Y != by4) return -1;
        int h = n >> 1, q = n >> 2;
        if (b.W == n && b.H == n) return 0;
        if (b.W == n && b.H == h) return B(bx4, by4 + h).W == n ? 1 : 5;
        if (b.W == h && b.H == n) return B(bx4 + h, by4).H == n ? 2 : 7;
        if (n >= 4 && b.W == n && b.H == q) return 8;
        if (n >= 4 && b.W == q && b.H == n) return 9;
        if (n >= 4 && b.W == h && b.H == h)
        {
            var b2 = B(bx4, by4 + h); if (b2.W == n && b2.H == h) return 4;
            var b3 = B(bx4 + h, by4); if (b3.W == h && b3.H == n) return 6;
        }
        return 3;
    }
    [ThreadStatic] private static (ushort[][] Planes, int W, int Cw)? t_lastRecon;
    // The CDF arrays (Av1CdfIndex order) of a fresh default tile context, to replay symbolic tile logs against.
    private static ushort[][] FreshCdfArrays(int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        return Av1CdfIndex.Arrays(cdf);
    }

    // tile_group_obu payload after the frame header: a single tile is its bare data; with several tiles, one byte
    // for tile_start_and_end_present_flag = 0 (+ byte alignment), then every tile but the last prefixed by
    // tile_size_minus_1 as a 4-byte little-endian value (tile_size_bytes_minus_1 = 3 in tile_info).
    internal static byte[] AssembleTileGroup(IReadOnlyList<byte[]> tiles)
    {
        if (tiles.Count == 1) return tiles[0];
        var o = new System.IO.MemoryStream();
        o.WriteByte(0);
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i < tiles.Count - 1)
            {
                uint sz = (uint)(tiles[i].Length - 1);
                o.WriteByte((byte)sz); o.WriteByte((byte)(sz >> 8)); o.WriteByte((byte)(sz >> 16)); o.WriteByte((byte)(sz >> 24));
            }
            o.Write(tiles[i]);
        }
        return o.ToArray();
    }

    private static void EncodePartitionColor(ColorPartCtx c, int bl, int bx4, int by4, int edgeIdx = 0)
    {
        int hsz = 16 >> bl, blk4 = 32 >> bl;
        bool haveH = c.Bw4 > bx4 + hsz;
        bool haveV = c.Bh4 > by4 + hsz;

        int bx8 = (bx4 & 31) >> 1, by8 = (by4 & 31) >> 1;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];

        // Edge handling mirrors the gray path (force SPLIT at edges; the tree is luma-driven, chroma follows). The
        // forced-split children take the same intra-edge tree children as a coded SPLIT (0=TL,1=TR,2=BL,3=BR).
        if (!haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            return;
        }
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBoolGathered(partCdf, (Av1BlockLevel)bl, top: true, 1);
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 1));
            return;
        }
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBoolGathered(partCdf, (Av1BlockLevel)bl, top: false, 1);
            EncodePartitionColor(c, bl + 1, bx4, by4, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 0));
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz, Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], 2));
            return;
        }

        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;

        // True trial-encode RD: encode each candidate partition for real, measure its actual coded bits + SSE,
        // and commit the one with the lowest J = SSE + λ·bits. Unlike the estimate path this uses the live CDFs
        // and real reconstruction (incl. adaptation and recursive sub-decisions), so it picks partitions optimally.
        // Also runs for PARTIAL blocks (extend past the frame but their centre is in-frame, i.e. haveH && haveV):
        // those were previously forced to a single large NONE, wasting bits on the padded region — RD now splits
        // them. The SPLIT recursion terminates cleanly at 8x8 (bw4/bh4 are always even, so 8x8 tiles edges exactly;
        // the 4x4 forced-split throw is unreachable). Rect HORZ/VERT candidates are only offered when fully inside.
        if (UseTrueRd && (long)c.Bw4 * c.Bh4 * 16 <= TrueRdPixelBudget && bl >= Math.Max(1, Sp.TrueRdFromBl) && bl <= Sp.TrueRdToBl
            && (bl < 4 || (bl == 4 && UseSub8Partition && fullyInside)))
        {
            EncodePartitionColorTrueRd(c, bl, bx4, by4, hsz, blk4, partCdf, nPart, bx8, by8, edgeIdx, fullyInside);
            return;
        }

        // Partition choice: 0=NONE, 1=HORZ, 2=VERT, 3=SPLIT (rectangular HORZ/VERT only at 16x16, which yields
        // 16x8/8x16 luma + 8x4/4x8 chroma — both ≤16 per axis, so the matched rect transform applies and there is
        // no sub-8x8 chroma corner case).
        int choice = 0;
        if (Sp.VarPartition && bl < 4 && fullyInside) choice = VarPartitionChoice(c, bl, bx4, by4);
        else if (bl < 4 && fullyInside)
        {
            long costNone = c.EstCost(bl, bx4, by4);
            long costSplit = c.EstCost(bl + 1, bx4, by4) + c.EstCost(bl + 1, bx4 + hsz, by4)
                           + c.EstCost(bl + 1, bx4, by4 + hsz) + c.EstCost(bl + 1, bx4 + hsz, by4 + hsz);
            long costHorz = long.MaxValue, costVert = long.MaxValue;
            if ((bl == 2 || bl == 3) && UseRectPartition && !UsePalette && c.Layout == Av1PixelLayout.I420)
            {
                // Rectangular HORZ/VERT at 32x32 (→32x16/16x32) and 16x16 (→16x8/8x16). Chroma-aware: a partition
                // codes a chroma block per luma leaf, so include chroma coeff cost (λ·bits) in every candidate —
                // otherwise HORZ/VERT (2 chroma blocks) look artificially cheap vs NONE (1).
                double lambda = LamK * c.AcDq * c.AcDq;
                int cbx = bx4 * 2, cby = by4 * 2;
                int cBlk = blk4 * 2, cH = cBlk >> 1;    // chroma NONE size (px) and half (px)
                // Per-level tx / block-size ordinals for the rect leaves and the chroma cost blocks.
                (int lumaTxH, int lumaTxV, int chTxH, int chTxV, int lumaBsH, int lumaBsV, int noneChTx, int splitChTx) = bl == 2
                    ? (TxIdx32x16, TxIdx16x32, TxIdx16x8, TxIdx8x16, (int)Av1BlockSize.Bs32x16, (int)Av1BlockSize.Bs16x32, 2, 1)
                    : (TxIdx16x8, TxIdx8x16, TxIdx8x4, TxIdx4x8, (int)Av1BlockSize.Bs16x8, (int)Av1BlockSize.Bs8x16, 1, 0);

                costNone += (long)(lambda * ChromaCostDc(c, cbx, cby, cBlk, cBlk, noneChTx));
                costSplit += (long)(lambda * (ChromaCostDc(c, cbx, cby, cH, cH, splitChTx)
                          + ChromaCostDc(c, cbx + cH, cby, cH, cH, splitChTx) + ChromaCostDc(c, cbx, cby + cH, cH, cH, splitChTx)
                          + ChromaCostDc(c, cbx + cH, cby + cH, cH, cH, splitChTx)));
                costHorz = EstimateRectCostColor(c, lumaTxH, bx4, by4, blk4, hsz)
                         + EstimateRectCostColor(c, lumaTxH, bx4, by4 + hsz, blk4, hsz)
                         + (long)(lambda * (ChromaCostDc(c, cbx, cby, cBlk, cH, chTxH) + ChromaCostDc(c, cbx, cby + cH, cBlk, cH, chTxH)));
                costVert = EstimateRectCostColor(c, lumaTxV, bx4, by4, hsz, blk4)
                         + EstimateRectCostColor(c, lumaTxV, bx4 + hsz, by4, hsz, blk4)
                         + (long)(lambda * (ChromaCostDc(c, cbx, cby, cH, cBlk, chTxV) + ChromaCostDc(c, cbx + cH, cby, cH, cBlk, chTxV)));
            }

            // Square NONE/SPLIT decide normally; rect is only taken when it beats the best square option by a
            // margin — the estimate (coeff-cost proxy, source prediction) is optimistic about rect, so a plain
            // argmin over-picks it and loses at matched quality. The margin keeps rect to its clear wins.
            long sqBest = Math.Min(costNone, costSplit);
            int sqChoice = costNone <= costSplit ? 0 : 3;
            long rectBest = Math.Min(costHorz, costVert);
            int rectChoice = costHorz <= costVert ? 1 : 2;
            choice = (rectBest < (long)(sqBest * RectCostMargin)) ? rectChoice : sqChoice;
        }

        EncodeChoiceColor(c, choice, bl, bx4, by4, hsz, blk4, partCdf, nPart, bx8, by8, edgeIdx);
    }

    // Encodes one specific partition choice (0=NONE,1=HORZ,2=VERT,3=SPLIT): the partition symbol, the leaf(s) or
    // recursive children, and the partition-context fill. SPLIT recurses into EncodePartitionColor (which itself
    // applies whatever decision mode is active).
    private static void EncodeChoiceColor(ColorPartCtx c, int choice, int bl, int bx4, int by4, int hsz, int blk4,
        Span<ushort> partCdf, int nPart, int bx8, int by8, int edgeIdx)
    {
        ref readonly var node = ref Av1IntraEdgeTree.Tree64[edgeIdx];
        if (choice == 3 && bl == 4) { EncodeSub8Quad(c, bx4, by4, partCdf, nPart, bx8, by8, node); return; }
        if (choice == 3)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
            int c0 = Av1IntraEdgeTree.GetSplitChild(node, 0), c1 = Av1IntraEdgeTree.GetSplitChild(node, 1);
            int c2 = Av1IntraEdgeTree.GetSplitChild(node, 2), c3 = Av1IntraEdgeTree.GetSplitChild(node, 3);
            double sb0 = c.Msac.MeasuredBits;
            EncodePartitionColor(c, bl + 1, bx4, by4, c0);
            if (SubDone(c, bl, 0, 0, bx4, by4, hsz, hsz, ref sb0)) return;
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4, c1);
            if (SubDone(c, bl, 0, 1, bx4 + hsz, by4, hsz, hsz, ref sb0)) return;
            EncodePartitionColor(c, bl + 1, bx4, by4 + hsz, c2);
            if (SubDone(c, bl, 0, 2, bx4, by4 + hsz, hsz, hsz, ref sb0)) return;
            EncodePartitionColor(c, bl + 1, bx4 + hsz, by4 + hsz, c3);
            SubDone(c, bl, 0, 3, bx4 + hsz, by4 + hsz, hsz, hsz, ref sb0);
            return;
        }

        if (choice == 1) // PARTITION_HORZ: two stacked leaves
        {
            if (bl == 4) { EncodeSub8Pair(c, horz: true, bx4, by4, partCdf, nPart, bx8, by8, node); return; }
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Horizontal, nPart);
            double hb0 = c.Msac.MeasuredBits;
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4, blk4, hsz, node.H0);
            if (SubDone(c, bl, 1, 0, bx4, by4, blk4, hsz, ref hb0)) return;
            EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4 + hsz, blk4, hsz, node.H1);
            SubDone(c, bl, 1, 1, bx4, by4 + hsz, blk4, hsz, ref hb0);
            if (c.PartAborted) return;
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Horizontal);
            return;
        }

        if (choice == 2) // PARTITION_VERT: two side-by-side leaves
        {
            if (bl == 4) { EncodeSub8Pair(c, horz: false, bx4, by4, partCdf, nPart, bx8, by8, node); return; }
            var rp = RectLeafParams(bl);
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Vertical, nPart);
            double vb0 = c.Msac.MeasuredBits;
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4, by4, hsz, blk4, node.V0);
            if (SubDone(c, bl, 2, 0, bx4, by4, hsz, blk4, ref vb0)) return;
            EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4 + hsz, by4, hsz, blk4, node.V1);
            SubDone(c, bl, 2, 1, bx4 + hsz, by4, hsz, blk4, ref vb0);
            if (c.PartAborted) return;
            FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Vertical);
            return;
        }

        // Extended partitions (T-shapes): two quarter-square leaves (bl+1) + one half-rect leaf, in the EXACT
        // sub-block order + edge availability our decoder uses (Av1Decode TopSplit/BottomSplit/LeftSplit/RightSplit).
        if (choice >= 4)
        {
            c.UseAomModeCache = Sp.AomPartAbReuse && choice <= 7;
            var rp = RectLeafParams(bl);
            if (choice == 4) // PARTITION_HORZ_A: two top quarters, then bottom half
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.TopSplit, nPart);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4, hsz, Av1EdgeFlags.AllTrAndBl);
                if (PartOver(c, bx4, by4, hsz, hsz)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4, hsz, node.V1);
                if (PartOver(c, bx4 + hsz, by4, hsz, hsz)) return;
                EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4 + hsz, blk4, hsz, node.H1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.TopSplit);
            }
            else if (choice == 5) // PARTITION_HORZ_B: top half, then two bottom quarters
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.BottomSplit, nPart);
                EncodeRectLeafColor(c, rp.BsH, rp.LumaTxH, rp.ChTxH, bx4, by4, blk4, hsz, node.H0);
                if (PartOver(c, bx4, by4, blk4, hsz)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4, by4 + hsz, hsz, node.V0);
                if (PartOver(c, bx4, by4 + hsz, hsz, hsz)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4 + hsz, hsz, Av1EdgeFlags.None);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.BottomSplit);
            }
            else if (choice == 6) // PARTITION_VERT_A: two left quarters, then right half
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.LeftSplit, nPart);
                EncodeLeafBlockColor(c, bl + 1, bx4, by4, hsz, Av1EdgeFlags.AllTrAndBl);
                if (PartOver(c, bx4, by4, hsz, hsz)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4, by4 + hsz, hsz, node.H1);
                if (PartOver(c, bx4, by4 + hsz, hsz, hsz)) return;
                EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4 + hsz, by4, hsz, blk4, node.V1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.LeftSplit);
            }
            else if (choice == 7) // PARTITION_VERT_B: left half, then two right quarters
            {
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.RightSplit, nPart);
                EncodeRectLeafColor(c, rp.BsV, rp.LumaTxV, rp.ChTxV, bx4, by4, hsz, blk4, node.V0);
                if (PartOver(c, bx4, by4, hsz, blk4)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4, hsz, node.H0);
                if (PartOver(c, bx4 + hsz, by4, hsz, hsz)) return;
                EncodeLeafBlockColor(c, bl + 1, bx4 + hsz, by4 + hsz, hsz, Av1EdgeFlags.None);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.RightSplit);
            }
            else if (bl == 3) // HORZ_4 / VERT_4 at 16x16: 16x4 / 4x16 strips; in 4:2:0 the odd strips carry the shared chroma
            {
                bool h4 = choice == 8;
                int bs = h4 ? (int)Av1BlockSize.Bs16x4 : (int)Av1BlockSize.Bs4x16;
                int tx = h4 ? (int)Av1RectTxSize.Rtx16x4 : (int)Av1RectTxSize.Rtx4x16;
                int ctx = h4 ? (int)Av1RectTxSize.Rtx8x4 : (int)Av1RectTxSize.Rtx4x8;
                int r0 = c.Layout == Av1PixelLayout.I420 ? -1 : 0, r1 = c.Layout == Av1PixelLayout.I420 ? 1 : 0;
                c.Msac.EncodeSymbolAdapt(partCdf, (int)(h4 ? Av1BlockPartition.Horizontal4 : Av1BlockPartition.Vertical4), nPart);
                if (h4)
                {
                    // (checked per strip pair: in 4:2:0 the odd strip codes the pair's chroma)
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4, by4, 4, 1, node.H0, r0);
                    if (c.PartAborted) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4, by4 + 1, 4, 1, node.H4, r1);
                    if (PartOver(c, bx4, by4, 4, 2)) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4, by4 + 2, 4, 1, Av1EdgeFlags.AllLeftHasBottom, r0);
                    if (c.PartAborted) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4, by4 + 3, 4, 1, node.H1, r1);
                }
                else
                {
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4, by4, 1, 4, node.V0, r0);
                    if (c.PartAborted) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4 + 1, by4, 1, 4, node.V4, r1);
                    if (PartOver(c, bx4, by4, 2, 4)) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4 + 2, by4, 1, 4, Av1EdgeFlags.AllTopHasRight, r0);
                    if (c.PartAborted) return;
                    EncodeRectLeafColor(c, bs, tx, ctx, bx4 + 3, by4, 1, 4, node.V1, r1);
                }
                FillPartCtx(c, bl, bx8, by8, hsz, h4 ? Av1BlockPartition.Horizontal4 : Av1BlockPartition.Vertical4);
            }
            else if (choice == 8) // PARTITION_HORZ_4: four strips, 32x8 at 32x32 / 64x16 at 64x64 (edge flags per decoder)
            {
                int q = hsz >> 1;   // quarter-height step in 4-units
                var (hBs, hTx, hCh) = bl == 1 ? ((int)Av1BlockSize.Bs64x16, (int)Av1RectTxSize.Rtx64x16, (int)Av1RectTxSize.Rtx32x8)
                                              : ((int)Av1BlockSize.Bs32x8, (int)Av1RectTxSize.Rtx32x8, (int)Av1RectTxSize.Rtx16x4);
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Horizontal4, nPart);
                EncodeRectLeafColor(c, hBs, hTx, hCh, bx4, by4, blk4, q, node.H0);
                if (PartOver(c, bx4, by4, blk4, q)) return;
                EncodeRectLeafColor(c, hBs, hTx, hCh, bx4, by4 + q, blk4, q, node.H4);
                if (PartOver(c, bx4, by4 + q, blk4, q)) return;
                EncodeRectLeafColor(c, hBs, hTx, hCh, bx4, by4 + 2 * q, blk4, q, Av1EdgeFlags.AllLeftHasBottom);
                if (PartOver(c, bx4, by4 + 2 * q, blk4, q)) return;
                EncodeRectLeafColor(c, hBs, hTx, hCh, bx4, by4 + 3 * q, blk4, q, node.H1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Horizontal4);
            }
            else // choice == 9, PARTITION_VERT_4: four strips, 8x32 at 32x32 / 16x64 at 64x64
            {
                int q = hsz >> 1;
                var (vBs, vTx, vCh) = bl == 1 ? ((int)Av1BlockSize.Bs16x64, (int)Av1RectTxSize.Rtx16x64, (int)Av1RectTxSize.Rtx8x32)
                                              : ((int)Av1BlockSize.Bs8x32, (int)Av1RectTxSize.Rtx8x32, (int)Av1RectTxSize.Rtx4x16);
                c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Vertical4, nPart);
                EncodeRectLeafColor(c, vBs, vTx, vCh, bx4, by4, q, blk4, node.V0);
                if (PartOver(c, bx4, by4, q, blk4)) return;
                EncodeRectLeafColor(c, vBs, vTx, vCh, bx4 + q, by4, q, blk4, node.V4);
                if (PartOver(c, bx4 + q, by4, q, blk4)) return;
                EncodeRectLeafColor(c, vBs, vTx, vCh, bx4 + 2 * q, by4, q, blk4, Av1EdgeFlags.AllTopHasRight);
                if (PartOver(c, bx4 + 2 * q, by4, q, blk4)) return;
                EncodeRectLeafColor(c, vBs, vTx, vCh, bx4 + 3 * q, by4, q, blk4, node.V1);
                FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.Vertical4);
            }
            c.UseAomModeCache = false;
            return;
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        EncodeLeafBlockColor(c, bl, bx4, by4, blk4, node.O);
        FillPartCtx(c, bl, bx8, by8, hsz, Av1BlockPartition.None);
    }

    // Snapshot of all mutable encoder state a partition subtree touches, so trial encodes can be rolled back.
    private sealed class RdSnapshot
    {
        public Av1MsacWriter.State Msac;
        public Av1CdfContext Cdf = new();
        public ushort[] ReconY = null!, ReconU = null!, ReconV = null!;   // block regions
        public byte[] AbovePart = null!, ALY = null!, ACU = null!, ACV = null!, AModeY = null!, ASkip = null!;
        public byte[] LeftPart = null!, LLY = null!, LCU = null!, LCV = null!, LModeY = null!, LSkip = null!;
        public sbyte[] ATxY = null!, LTxY = null!;
        public byte[] APalSz = null!, LPalSz = null!; public ushort[] APalCol = null!, LPalCol = null!;
        public byte[] AModeUv = null!, LModeUv = null!;
        public byte[] APalSzUv = null!, LPalSzUv = null!; public ushort[] APalColU = null!, LPalColU = null!;
    }

    // Snapshots reused per partition level (slot = level * 3 + k: the candidate loop's base and best, the leaf's
    // tx-depth trial), so the RD search does not allocate a CDF context and block copies at every node.
    private static RdSnapshot Pooled(ColorPartCtx c, int slot) => c.SnapPool[slot] ??= new RdSnapshot();

    private static ushort[] CopyRegion(ushort[]? into, ushort[] plane, int stride, int px, int py, int w, int h)
    {
        var r = into != null && into.Length == w * h ? into : new ushort[w * h];
        for (int y = 0; y < h; y++) Array.Copy(plane, (py + y) * stride + px, r, y * w, w);
        return r;
    }

    private static T[] CopyInto<T>(T[]? into, T[] src)
    {
        var r = into != null && into.Length == src.Length ? into : new T[src.Length];
        Array.Copy(src, r, src.Length);
        return r;
    }

    private static void PasteRegion(ushort[] region, ushort[] plane, int stride, int px, int py, int w, int h)
    {
        for (int y = 0; y < h; y++) Array.Copy(region, y * w, plane, (py + y) * stride + px, w);
    }

    private static RdSnapshot SnapshotRd(ColorPartCtx c, int bx4, int by4, int blk4, RdSnapshot? into = null)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY, cnw = (blk4 * 4) >> c.SsX, cnh = (blk4 * 4) >> c.SsY;
        var s = into ?? new RdSnapshot();
        s.Msac = c.Msac.Save();
        s.Cdf.CopyIntraFrom(c.Cdf);
        s.ReconY = CopyRegion(s.ReconY, c.ReconY, c.W, lpx, lpy, ln, ln);
        s.ReconU = CopyRegion(s.ReconU, c.ReconU, c.Cw, cpx, cpy, cnw, cnh);
        s.ReconV = CopyRegion(s.ReconV, c.ReconV, c.Cw, cpx, cpy, cnw, cnh);
        s.AbovePart = CopyInto(s.AbovePart, c.AbovePart); s.LeftPart = CopyInto(s.LeftPart, c.LeftPart);
        s.ALY = CopyInto(s.ALY, c.ALY); s.LLY = CopyInto(s.LLY, c.LLY);
        s.ACU = CopyInto(s.ACU, c.ACU); s.LCU = CopyInto(s.LCU, c.LCU);
        s.ACV = CopyInto(s.ACV, c.ACV); s.LCV = CopyInto(s.LCV, c.LCV);
        s.AModeY = CopyInto(s.AModeY, c.AModeY); s.LModeY = CopyInto(s.LModeY, c.LModeY);
        s.ASkip = CopyInto(s.ASkip, c.ASkip); s.LSkip = CopyInto(s.LSkip, c.LSkip);
        s.ATxY = CopyInto(s.ATxY, c.ATxY); s.LTxY = CopyInto(s.LTxY, c.LTxY);
        s.APalSz = CopyInto(s.APalSz, c.APalSz); s.LPalSz = CopyInto(s.LPalSz, c.LPalSz);
        s.APalCol = CopyInto(s.APalCol, c.APalCol); s.LPalCol = CopyInto(s.LPalCol, c.LPalCol);
        s.AModeUv = CopyInto(s.AModeUv, c.AModeUv); s.LModeUv = CopyInto(s.LModeUv, c.LModeUv);
        s.APalSzUv = CopyInto(s.APalSzUv, c.APalSzUv); s.LPalSzUv = CopyInto(s.LPalSzUv, c.LPalSzUv);
        s.APalColU = CopyInto(s.APalColU, c.APalColU); s.LPalColU = CopyInto(s.LPalColU, c.LPalColU);
        return s;
    }

    private static void RestoreRd(ColorPartCtx c, RdSnapshot s, int bx4, int by4, int blk4)
    {
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY, cnw = (blk4 * 4) >> c.SsX, cnh = (blk4 * 4) >> c.SsY;
        c.Msac.Restore(s.Msac);
        c.Cdf.CopyIntraFrom(s.Cdf);
        PasteRegion(s.ReconY, c.ReconY, c.W, lpx, lpy, ln, ln);
        PasteRegion(s.ReconU, c.ReconU, c.Cw, cpx, cpy, cnw, cnh);
        PasteRegion(s.ReconV, c.ReconV, c.Cw, cpx, cpy, cnw, cnh);
        Array.Copy(s.AbovePart, c.AbovePart, s.AbovePart.Length); Array.Copy(s.LeftPart, c.LeftPart, s.LeftPart.Length);
        Array.Copy(s.ALY, c.ALY, s.ALY.Length); Array.Copy(s.LLY, c.LLY, s.LLY.Length);
        Array.Copy(s.ACU, c.ACU, s.ACU.Length); Array.Copy(s.LCU, c.LCU, s.LCU.Length);
        Array.Copy(s.ACV, c.ACV, s.ACV.Length); Array.Copy(s.LCV, c.LCV, s.LCV.Length);
        Array.Copy(s.AModeY, c.AModeY, s.AModeY.Length); Array.Copy(s.LModeY, c.LModeY, s.LModeY.Length);
        Array.Copy(s.ASkip, c.ASkip, s.ASkip.Length); Array.Copy(s.LSkip, c.LSkip, s.LSkip.Length);
        Array.Copy(s.ATxY, c.ATxY, s.ATxY.Length); Array.Copy(s.LTxY, c.LTxY, s.LTxY.Length);
        Array.Copy(s.APalSz, c.APalSz, s.APalSz.Length); Array.Copy(s.LPalSz, c.LPalSz, s.LPalSz.Length);
        Array.Copy(s.APalCol, c.APalCol, s.APalCol.Length); Array.Copy(s.LPalCol, c.LPalCol, s.LPalCol.Length);
        Array.Copy(s.AModeUv, c.AModeUv, s.AModeUv.Length); Array.Copy(s.LModeUv, c.LModeUv, s.LModeUv.Length);
        Array.Copy(s.APalSzUv, c.APalSzUv, s.APalSzUv.Length); Array.Copy(s.LPalSzUv, c.LPalSzUv, s.LPalSzUv.Length);
        Array.Copy(s.APalColU, c.APalColU, s.APalColU.Length); Array.Copy(s.LPalColU, c.LPalColU, s.LPalColU.Length);
    }

    // SSE of the reconstructed block (luma + chroma) vs the source planes — the distortion term for true-RD.
    // rd_try_subblock's check after one coded sub-block of the running candidate: adds its region's SSE and aborts
    // the candidate once SSE + lambda * bits so far reaches the budget.
    private static bool PartOver(ColorPartCtx c, int x4, int y4, int w4, int h4)
    {
        if (c.PartAborted) return true;
        if (double.IsPositiveInfinity(c.PartBudget)) return false;
        c.PartSse += RegionSseColor(c, x4, y4, w4, h4);
        if (c.PartSse + c.PartLambda * (c.Msac.MeasuredBits - c.PartBits0) >= c.PartBudget) { c.PartAborted = true; return true; }
        return false;
    }

    // A SPLIT / HORZ / VERT sub-block is coded: record its J (region SSE + lambda * its bits) for the AB prune, then
    // the budget check. kind 0 SPLIT, 1 HORZ, 2 VERT; bits0 = the measured bits before it (updated).
    private static bool SubDone(ColorPartCtx c, int bl, int kind, int k, int x4, int y4, int w4, int h4, ref double bits0)
    {
        if (c.PartAborted) return true;
        long sse = RegionSseColor(c, x4, y4, w4, h4);
        double now = c.Msac.MeasuredBits;
        c.SubJ[(bl * 3 + kind) * 4 + k] = sse + c.PartLambda * (now - bits0);
        bits0 = now;
        if (double.IsPositiveInfinity(c.PartBudget)) return false;
        c.PartSse += sse;
        if (c.PartSse + c.PartLambda * (now - c.PartBits0) >= c.PartBudget)
        {
            c.PartAborted = true;
            if (kind == 0) { c.SplitAbortN[bl] = k + 1; c.SplitAbortJ[bl] = c.PartSse + c.PartLambda * (now - c.PartBits0); }
            return true;
        }
        return false;
    }

    // Remaining budget for the next sub-block's luma search (+inf when none applies).
    private static double PartRemaining(ColorPartCtx c) => double.IsPositiveInfinity(c.PartBudget) ? double.MaxValue
        : c.PartBudget - (c.PartSse + c.PartLambda * (c.Msac.MeasuredBits - c.PartBits0));

    private static long RegionSseColor(ColorPartCtx c, int x4, int y4, int w4, int h4)
    {
        int lpx = x4 * 4, lpy = y4 * 4;
        int lw = Math.Min(w4 * 4, c.Bw4 * 4 - lpx), lh = Math.Min(h4 * 4, c.Bh4 * 4 - lpy);
        long sse = 0;
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            { int d = c.ReconY[(lpy + y) * c.W + lpx + x] - c.Luma[(lpy + y) * c.W + lpx + x]; sse += (long)d * d; }
        if (c.Mono) return sse;
        int cpx = lpx >> c.SsX, cpy = lpy >> c.SsY;
        int cnw = Math.Min(Math.Max(lw >> c.SsX, 1), ((c.Bw4 * 4) >> c.SsX) - cpx), cnh = Math.Min(Math.Max(lh >> c.SsY, 1), ((c.Bh4 * 4) >> c.SsY) - cpy);
        for (int y = 0; y < cnh; y++)
            for (int x = 0; x < cnw; x++)
            {
                int du = c.ReconU[(cpy + y) * c.Cw + cpx + x] - c.U[(cpy + y) * c.Cw + cpx + x];
                int dv = c.ReconV[(cpy + y) * c.Cw + cpx + x] - c.V[(cpy + y) * c.Cw + cpx + x];
                sse += (long)du * du + (long)dv * dv;
            }
        return sse;
    }

    // Per-pixel variance of the source luma block (libaom pb_source_variance, 8-bit scale).
    private static double BlockSrcVar(ColorPartCtx c, int bx4, int by4, int blk4)
    {
        int x0 = bx4 * 4, y0 = by4 * 4, n = blk4 * 4;
        int w = Math.Min(n, c.Bw4 * 4 - x0), h = Math.Min(n, c.Bh4 * 4 - y0);
        long sum = 0, sq = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { int v = c.Luma[(y0 + y) * c.W + x0 + x]; sum += v; sq += (long)v * v; }
        double np = Math.Max(1, w * h);
        return (sq - sum * (double)sum / np) / np / (1 << (2 * (Bd - 8)));
    }

    private static long BlockSseColor(ColorPartCtx c, int bx4, int by4, int blk4)
    {
        // Only in-frame samples count: a partial block's forced-split children never reconstruct the part past the
        // frame edge, so including it would make every edge SPLIT look hopeless (and force a 64x64 NONE there).
        int lpx = bx4 * 4, lpy = by4 * 4, ln = blk4 * 4;
        int lw = Math.Min(ln, c.Bw4 * 4 - lpx), lh = Math.Min(ln, c.Bh4 * 4 - lpy);
        long sse = 0;
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            { int d = c.ReconY[(lpy + y) * c.W + lpx + x] - c.Luma[(lpy + y) * c.W + lpx + x]; sse += (long)d * d; }
        if (c.Mono) return sse;
        int cpx = (bx4 * 4) >> c.SsX, cpy = (by4 * 4) >> c.SsY;
        int cnw = Math.Min((blk4 * 4) >> c.SsX, ((c.Bw4 * 4) >> c.SsX) - cpx), cnh = Math.Min((blk4 * 4) >> c.SsY, ((c.Bh4 * 4) >> c.SsY) - cpy);
        for (int y = 0; y < cnh; y++)
            for (int x = 0; x < cnw; x++)
            {
                int du = c.ReconU[(cpy + y) * c.Cw + cpx + x] - c.U[(cpy + y) * c.Cw + cpx + x];
                int dv = c.ReconV[(cpy + y) * c.Cw + cpx + x] - c.V[(cpy + y) * c.Cw + cpx + x];
                sse += (long)du * du + (long)dv * dv;
            }

        return sse;
    }

    private static void EncodePartitionColorTrueRd(ColorPartCtx c, int bl, int bx4, int by4, int hsz, int blk4,
        Span<ushort> partCdf, int nPart, int bx8, int by8, int edgeIdx, bool fullyInside)
    {
        // Candidates: NONE and SPLIT always; HORZ/VERT at 32x32/16x16 when rect is enabled AND the block is fully
        // inside the frame (the rect leaves assume in-frame dimensions). For partial blocks only NONE vs SPLIT.
        Span<int> cands = stackalloc int[10];
        int pruneRect = 0;
        int nc = 0;
        // Outside 4:2:0 a 64x64 NONE leaf would need several chroma tx blocks (not yet coded) — always split there.
        bool i420 = c.Layout == Av1PixelLayout.I420;
        if (bl == 1 && Sp.MaxPartition32 && fullyInside)
        {
            // 64x64 blocks are not searched (libaom default_max_partition_size): SPLIT only.
            cands[nc++] = 3;
            goto Searched;
        }
        cands[nc++] = 0;
        if (bl < 4) cands[nc++] = 3;   // SPLIT (at 8x8 only with UseSplit4x4, below)
        // Sub-8x8 HORZ/VERT use 4:2:0 shared chroma (EncodeSub8Pair), so they stay 4:2:0-only for now.
        bool rectHere = fullyInside && (((bl == 2 || bl == 3 || (bl == 1 && i420 && UsePartition64)) && (UseRectPartition || (Sp.RectScreenContent && c.ScreenContent)))
            || (bl == 4 && UseSub8Partition && (i420 || c.Layout == Av1PixelLayout.I444)));
        // 4:2:2 forbids every tall (h = 2w) leaf: its chroma would be 1:4 (get_plane_residual_size == BLOCK_INVALID),
        // so VERT, VERT_A/B and VERT_4 are never emitted there (spec conformance requirement; dav1d table has 0).
        bool vertOk = c.Layout != Av1PixelLayout.I422;
        // libaom allintra 8x8 prunes (camera content). The partition contexts give the neighbours' extent: bit 1 set
        // = the above block is at most 8 wide / the left one at most 8 high (exact while no rect shapes above 8x8).
        // pruneRect: 4 / 8 = decide HORZ / VERT from NONE's mode (neighbour larger / not)
        if (bl == 4 && rectHere && !c.ScreenContent)
        {
            if (Sp.RectPruneQidx && c.QIdx < 171) rectHere = false;
            var (tx0, ty0, _, _) = TileBounds4(c.W, c.Bw4, c.Bh4);
            bool leftAv = bx4 > tx0, upAv = by4 > ty0;
            bool leftBig = leftAv && (c.LeftPart[by8] & 2) == 0, upBig = upAv && (c.AbovePart[bx8] & 2) == 0;
            if (Sp.Sub8PruneNeighbour && leftAv && upAv && (leftBig || upBig)) { rectHere = false; nc = 1; }
            if (rectHere && Sp.RectPruneVarDev)
            {
                double vmin = double.MaxValue, vmax = 0;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                    {
                        double v = Math.Log(1 + Var4x4Norm(c.Luma, c.W, bx4 * 4 + sx * 4, by4 * 4 + sy * 4) / 16.0);
                        vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v);
                    }
                if (vmax - vmin < 3.0) rectHere = false;
            }
            if (rectHere && Sp.RectPruneNoneMode) pruneRect = (leftBig || upBig) ? 4 : 8;
        }
        if (rectHere) { cands[nc++] = 1; if (vertOk) cands[nc++] = 2; }
        // 8x8 SPLIT -> four 4x4 blocks with shared 4:2:0 chroma (EncodeSub8Quad); same gate as the 8x4 / 4x8 pairs.
        if (bl == 4 && rectHere && Sp.UseSplit4x4) cands[nc++] = 3;
        // Extended T-shape partitions (HORZ_A/B, VERT_A/B) at 32x32/16x16 — quarter squares + half rects, all
        // block sizes we already code. Same in-frame + rect gate; 8x8 has no extended types.
        if (UseExtPartition && (bl == 1 || bl == 2 || bl == 3) && rectHere)
        { cands[nc++] = 4; cands[nc++] = 5; if (vertOk) { cands[nc++] = 6; cands[nc++] = 7; } }
        // HORZ_4/VERT_4 at 32x32 → 32x8/8x32 strips (normal 16x4 chroma). Only bl==2: the 16x16→16x4 case needs
        // sub-8x8-style shared chroma. The decoder still reads the symbol wherever we choose not to emit it.
        if (UseExtPartition && (bl == 1 || bl == 2 || (bl == 3 && Sp.Part4At16)) && rectHere)
        { cands[nc++] = 8; if (vertOk) cands[nc++] = 9; }

        Searched:
        // Dev oracle: the partition another encoder chose here (OracleOrigin / OracleBs from its decoded stream).
        if (OracleBs != null && fullyInside)
        {
            int oc = OracleChoice(bx4, by4, blk4);
            if (oc >= 0) { cands[0] = oc; nc = 1; }
        }
        // libaom's intra CNN partition pruning (whole-in-frame 64x64 .. 8x8 blocks; the CNN runs once per superblock).
        int cnnLevel = c.ScreenContent ? Sp.CnnPruneLevelScreen : Sp.CnnPruneLevel;
        if (cnnLevel > 0 && fullyInside && bl >= 1 && bl <= 4 && nc > 1)
        {
            int sbKey = ((by4 >> 4) << 16) | (bx4 >> 4);
            if (bl == 1)
            {
                c.Cnn = Av1PartitionCnn.Predict(c.Luma, c.W, c.W, c.Luma.Length / c.W, bx4 * 4, by4 * 4, Bd, c.DcDq >> (Bd - 8));
                c.CnnSb = sbKey;
            }
            if (c.CnnSb == sbKey && c.Cnn != null)
            {
                int d = Av1PartitionCnn.Decide(c.Cnn, bl, bx4 & 15, by4 & 15, Math.Min(c.Bw4, c.Bh4) * 4);
                bool hasSplit = false;
                for (int i = 0; i < nc; i++) hasSplit |= cands[i] == 3;
                if (d > 0 && cnnLevel != 1 && hasSplit) { cands[0] = 3; nc = 1; }
                else if (d < 0)
                {
                    int k = 0;
                    for (int i = 0; i < nc; i++) if (cands[i] != 3) cands[k++] = cands[i];
                    nc = k;
                }
            }
        }
        // libaom allintra: a mix of a near-flat and a busy 4x4 forces the split (NONE pruned); 16x16 and larger.
        if (Sp.ForceSplitVar && bl >= 1 && bl <= 3 && nc > 1 && cands[0] == 0)
        {
            bool hasSplit = false;
            for (int i = 1; i < nc; i++) hasSplit |= cands[i] == 3;
            if (hasSplit)
            {
                int sbKey = ((by4 >> 4) << 16) | (bx4 >> 4);
                if (c.LogVarSb != sbKey)
                {
                    int sx4 = bx4 & ~15, sy4 = by4 & ~15;
                    for (int j = 0; j < 16; j++)
                        for (int i = 0; i < 16; i++)
                            c.LogVar4[j * 16 + i] = sx4 + i < c.Bw4 && sy4 + j < c.Bh4
                                ? Math.Log(1 + Var4x4Norm(c.Luma, c.W, (sx4 + i) * 4, (sy4 + j) * 4) / 16.0) : double.NaN;
                    c.LogVarSb = sbKey;
                }
                double vmin = double.MaxValue, vmax = 0;
                int ox = bx4 & 15, oy = by4 & 15;
                for (int j = 0; j < blk4 && oy + j < 16; j++)
                    for (int i = 0; i < blk4 && ox + i < 16; i++)
                    {
                        double v = c.LogVar4[(oy + j) * 16 + ox + i];
                        if (double.IsNaN(v)) continue;
                        vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v);
                    }
                if (vmin < 0.272 && vmax - vmin > 3.0)
                {
                    for (int i = 1; i < nc; i++) cands[i - 1] = cands[i];
                    nc--;
                }
            }
        }
        // Estimate-based pruning (faster presets): drop the side the source-predicted estimate clearly rejects.
        if (Sp.TrueRdPruneMargin > 0 && bl < 4 && fullyInside && nc > 1)
        {
            long eN = c.EstCost(bl, bx4, by4);
            long eS = c.EstCost(bl + 1, bx4, by4) + c.EstCost(bl + 1, bx4 + hsz, by4)
                    + c.EstCost(bl + 1, bx4, by4 + hsz) + c.EstCost(bl + 1, bx4 + hsz, by4 + hsz);
            double m = Sp.TrueRdPruneMargin;
            bool dropSplit = eS > eN * m, dropNone = !dropSplit && eN > eS * m;
            if (dropSplit || dropNone)
            {
                int k = 0;
                for (int i = 0; i < nc; i++)
                    if (dropSplit ? cands[i] != 3 : cands[i] == 3) cands[k++] = cands[i];
                nc = k;
            }
        }

        double lambda = LamK * c.AcDq * c.AcDq;
        // A single candidate needs no snapshot: it is simply coded in place.
        var snap0 = nc > 1 ? SnapshotRd(c, bx4, by4, blk4, Pooled(c, bl * 3)) : null;
        int baseCount = snap0?.Msac.PrecarryCount ?? 0;
        bool measureWas = c.Msac.Measure;
        c.Msac.Measure = true;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);

        // Encode each candidate once, capture its full post-state (recon/contexts/cdf + MSAC scalars + the coded
        // byte tail), and commit the lowest-J one by restoring that post-state — no re-encode, so a SPLIT winner's
        // subtree is not redone (that is where the exponential blow-up would otherwise come from).
        double bestJ = double.MaxValue;
        RdSnapshot? bestSnap = null;
        int[] bestTail = System.Array.Empty<int>();
        var logBuf = c.LogTailPool[bl] ??= new Av1MsacWriter.LogOp[256];
        int bestLogCount = 0;
        int bestI = -1, lastI = 0;
        // NONE (a leaf, coded first) records its ops in a side list: if a later candidate wins they are simply dropped,
        // if NONE wins they are appended once — instead of copying NONE's tail out of the log and back.
        List<Av1MsacWriter.LogOp>? noneLog = null, mainLog = null;
        bool noneBest = false;
        for (int k = 0; k < 12; k++) c.SubJ[bl * 12 + k] = double.MaxValue;
        c.SplitAbortN[bl] = 0;
        int quad = ((bx4 / blk4) & 1) | (((by4 / blk4) & 1) << 1);
        c.RectWin[bl * 4 + quad] = 3;
        for (int k = 0; k < 4; k++) c.RectWin[(bl + 1) * 4 + k] = 3;   // SPLIT sub-blocks not searched count as wins
        double noneJ = double.MaxValue;
        bool noRect = false;
        for (int i = 0; i < nc; i++)
        {
            int cand = cands[i];
            // libaom less_rectangular_check_level 1: SPLIT stopped within its first two sub-blocks while NONE was
            // better than its partial sum -> no rectangular / AB / 4-way shapes here
            if (noRect && (cand is 1 or 2 || cand >= 4)) continue;
            // libaom prune_ext_partition_types_search_level 1 (av1_prune_ab_partitions): AB shapes only when the best so
            // far is the matching rect, SPLIT, or NONE on a flat block, and when their sub-blocks' known J (14/16) could
            // still beat it
            if (Sp.AomPrune4Split && cand >= 8)
            {
                // prune_4_partition_using_split_info: conservative at high quantizers
                int bit = cand == 8 ? 1 : 2, wins = 0, thr = Math.Min(3 * (255 - c.QIdx) / 255 + 1, 3);
                for (int k = 0; k < 4; k++) if ((c.RectWin[(bl + 1) * 4 + k] & bit) != 0) wins++;
                if (wins < thr) continue;
            }
            if (Sp.AomPruneAb && cand >= 4 && cand <= 7 && bestI >= 0)
            {
                int bp = cands[bestI];
                bool horz = cand <= 5;
                bool okType = bp == 3 || bp == (horz ? 1 : 2) || (bp == 0 && BlockSrcVar(c, bx4, by4, blk4) < 32);
                double J(int kind, int k) { double v = c.SubJ[(bl * 3 + kind) * 4 + k]; return v == double.MaxValue ? 0 : v; }
                double est = cand switch
                {
                    4 => J(1, 1) + J(0, 0) + J(0, 1),   // HORZ_A: bottom half + top quarters
                    5 => J(1, 0) + J(0, 2) + J(0, 3),   // HORZ_B: top half + bottom quarters
                    6 => J(2, 1) + J(0, 0) + J(0, 2),   // VERT_A: right half + left quarters
                    _ => J(2, 0) + J(0, 1) + J(0, 3),   // VERT_B: left half + right quarters
                };
                if (!okType || est / 16 * 14 >= bestJ) continue;
            }
            if (i > 0) RestoreRd(c, snap0!, bx4, by4, blk4);
            double b0 = c.Msac.MeasuredBits;
            if (i == 0 && nc > 1 && cands[0] == 0 && c.Msac.Log != null)
            {
                mainLog = c.Msac.Log;
                noneLog = c.NoneLogPool[bl] ??= new List<Av1MsacWriter.LogOp>(1024);
                noneLog.Clear();
                c.Msac.Log = noneLog;
            }
            var svB = (c.PartBudget, c.PartBits0, c.PartSse, c.PartLambda, c.PartAborted);
            c.PartBudget = Sp.AomPartAbort && bestJ != double.MaxValue ? bestJ : double.PositiveInfinity;
            c.PartBits0 = b0; c.PartSse = 0; c.PartLambda = lambda; c.PartAborted = false;
            EncodeChoiceColor(c, cands[i], bl, bx4, by4, hsz, blk4, c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx), nPart, bx8, by8, edgeIdx);
            bool abortedCand = c.PartAborted;
            (c.PartBudget, c.PartBits0, c.PartSse, c.PartLambda, c.PartAborted) = svB;
            if (i == 0 && noneLog != null) c.Msac.Log = mainLog;
            double bits = c.Msac.MeasuredBits - b0;
            double j = abortedCand ? double.MaxValue : BlockSseColor(c, bx4, by4, blk4) + lambda * bits;
            if (cand == 0) noneJ = j;
            if (cand is 1 or 2 && !(j < bestJ)) c.RectWin[bl * 4 + quad] &= (byte)~cand;   // rect_part_win = false
            if (cand == 3 && Sp.AomLessRectCheck && abortedCand && c.SplitAbortN[bl] > 0 && c.SplitAbortN[bl] <= 2
                && noneJ < c.SplitAbortJ[bl]) noRect = true;
            if (i == 0 && pruneRect != 0 && cands[0] == 0)
            {
                // prune_rect_part_using_none_pred_mode: NONE's luma mode steers the rect shapes (bit 0 HORZ, 1 VERT).
                var m = (Av1IntraPredMode)c.AModeY[bx4 & 31];
                int pr = m is Av1IntraPredMode.Dc or Av1IntraPredMode.Smooth ? (pruneRect == 4 ? 3 : 0)
                    : m is Av1IntraPredMode.VerticalLeft or Av1IntraPredMode.Vertical or Av1IntraPredMode.VerticalRight ? 1
                    : m is Av1IntraPredMode.HorizontalDown or Av1IntraPredMode.Horizontal or Av1IntraPredMode.HorizontalUp ? 2 : 0;
                int k = 1;
                for (int t = 1; t < nc; t++)
                    if (!((cands[t] == 1 && (pr & 1) != 0) || (cands[t] == 2 && (pr & 2) != 0))) cands[k++] = cands[t];
                nc = k;
            }
            // Early termination: NONE (cand 0) coded in very few bits ⇒ a flat/skip block, which SPLIT/rect can
            // only make more expensive (more partition + header bits) for no distortion gain. Commit NONE and
            // skip the costly subtree recursion — the dominant speedup on the smooth regions that fill photos.
            bool last = i == nc - 1 || (i == 0 && cands[0] == 0 && bits <= EarlyTermBits);
            lastI = i;
            if (j < bestJ)
            {
                bestJ = j;
                bestI = i;
                // The last candidate coded stays in place if it wins: no copy of its state is needed.
                noneBest = i == 0 && noneLog != null;
                if (!last)
                {
                    bestTail = c.Msac.PrecarryFrom(baseCount);
                    if (!noneBest)
                    {
                        bestLogCount = c.Msac.CopyLogTail(snap0!.Msac.LogCount, ref logBuf);
                        c.LogTailPool[bl] = logBuf;
                    }
                    bestSnap = SnapshotRd(c, bx4, by4, blk4, Pooled(c, bl * 3 + 1));
                }
            }
            if (last) break;
        }

        if (bestI == lastI)
        {
            if (noneBest) c.Msac.AppendLog(noneLog!);   // NONE won in place (early termination): its ops join the log
            c.Msac.Measure = measureWas;
            return;
        }
        // Commit the winner: restore its recon/contexts/CDF, then re-apply its coded bytes onto the base stream.
        RestoreRd(c, snap0!, bx4, by4, blk4);
        RestoreRd(c, bestSnap!, bx4, by4, blk4);
        c.Msac.AppendPrecarry(bestTail);
        if (noneBest) c.Msac.AppendLog(noneLog!);
        else c.Msac.AppendLog(logBuf, bestLogCount);
        c.Msac.Measure = measureWas;
    }

    // Rectangular tx-size ordinals (Av1TxSize / RectTxfmSize).
    private const int TxIdx8x16 = 7, TxIdx16x8 = 8, TxIdx8x4 = 6, TxIdx4x8 = 5, TxIdx32x16 = 10, TxIdx16x32 = 9;

    // Rect leaf tx/block-size ordinals per partition level (bl==2: 32x32→32x16/16x32; bl==3: 16x16→16x8/8x16).
    private static (int LumaTxH, int LumaTxV, int ChTxH, int ChTxV, int BsH, int BsV) RectLeafParams(int bl) => bl switch
    {
        1 => ((int)Av1RectTxSize.Rtx64x32, (int)Av1RectTxSize.Rtx32x64, TxIdx32x16, TxIdx16x32, (int)Av1BlockSize.Bs64x32, (int)Av1BlockSize.Bs32x64),
        2 => (TxIdx32x16, TxIdx16x32, TxIdx16x8, TxIdx8x16, (int)Av1BlockSize.Bs32x16, (int)Av1BlockSize.Bs16x32),
        _ => (TxIdx16x8, TxIdx8x16, TxIdx8x4, TxIdx4x8, (int)Av1BlockSize.Bs16x8, (int)Av1BlockSize.Bs8x16),
    };

    private static void FillPartCtx(ColorPartCtx c, int bl, int bx8, int by8, int hsz, Av1BlockPartition part)
    {
        byte aboveVal = Av1Tables.AboveLeftPartCtx[0, bl, (int)part];
        byte leftVal = Av1Tables.AboveLeftPartCtx[1, bl, (int)part];
        for (int i = 0; i < hsz && bx8 + i < 16; i++) c.AbovePart[bx8 + i] = aboveVal;
        for (int j = 0; j < hsz && by8 + j < 16; j++) c.LeftPart[by8 + j] = leftVal;
    }

    // === Chroma-from-luma (CfL) — mirrors Av1Reconstruction.ComputeCflAc / ApplyCflAlpha exactly (I420). ===
    private static void ComputeCflAcEnc(ushort[] reconY, int yStride, int bx, int by, int cn, short[] ac)
    {
        int idx = 0;
        for (int y = 0; y < cn; y++)
        {
            int yOff = (by + 2 * y) * yStride + bx;
            for (int x = 0; x < cn; x++)
            { int p = yOff + 2 * x; ac[idx + x] = (short)((reconY[p] + reconY[p + 1] + reconY[p + yStride] + reconY[p + 1 + yStride]) << 1); }
            idx += cn;
        }

        int log2Sz = System.Numerics.BitOperations.TrailingZeroCount(cn) * 2;
        int dc = (1 << log2Sz) >> 1;
        for (int i = 0; i < cn * cn; i++) dc += ac[i];
        dc >>= log2Sz;
        for (int i = 0; i < cn * cn; i++) ac[i] = (short)(ac[i] - dc);
    }

    private static ushort[] BuildCflPred(int dcPred, short[] ac, int cn, int alpha)
    {
        var pred = new ushort[cn * cn];
        for (int i = 0; i < cn * cn; i++)
        {
            int diff = ac[i] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
            pred[i] = (ushort)Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
        }

        return pred;
    }

    private static int BestCflAlpha(ushort[] plane, int planeW, int cbx, int cby, int cn, int dcPred, short[] ac)
    {
        int bestAlpha = 0; long bestSse = long.MaxValue;
        int lo = -16, hi = 16;
        if (Sp.CflSearchRange > 0)
        {
            // Least-squares alpha (pred = dc + ac * alpha / 64), then only a window around it.
            long num = 0, den = 0;
            for (int y = 0; y < cn; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cn; x++) { int a = ac[y * cn + x]; num += (long)a * (plane[row + x] - dcPred); den += (long)a * a; }
            }
            int est = den == 0 ? 0 : (int)Math.Clamp(Math.Round(64.0 * num / den), -16, 16);
            lo = Math.Max(-16, est - Sp.CflSearchRange); hi = Math.Min(16, est + Sp.CflSearchRange);
        }
        for (int alpha = lo; alpha <= hi; alpha++)
        {
            long sse = 0;
            for (int y = 0; y < cn; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cn; x++)
                {
                    int diff = ac[y * cn + x] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
                    int e = plane[row + x] - Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
                    sse += (long)e * e;
                }
            }

            if (sse < bestSse) { bestSse = sse; bestAlpha = alpha; }
        }

        return bestAlpha;
    }

    // Rect (cw x ch) versions of the CfL helpers — mirror the decoder's ComputeCflAc (I420: sum the 2x2 luma,
    // <<1, subtract DC with log2Sz = log2(cw)+log2(ch)). Used to add CfL chroma to the rect and sub-8x8 leaves,
    // which previously coded DC-only chroma (the profiled +722B chroma-coef gap vs libaom on piechart).
    private static void ComputeCflAcEncRect(ushort[] reconY, int yStride, int bx, int by, int cw, int ch, short[] ac)
    {
        int idx = 0;
        for (int y = 0; y < ch; y++)
        {
            int yOff = (by + 2 * y) * yStride + bx;
            for (int x = 0; x < cw; x++)
            { int p = yOff + 2 * x; ac[idx + x] = (short)((reconY[p] + reconY[p + 1] + reconY[p + yStride] + reconY[p + 1 + yStride]) << 1); }
            idx += cw;
        }
        int log2Sz = System.Numerics.BitOperations.TrailingZeroCount(cw) + System.Numerics.BitOperations.TrailingZeroCount(ch);
        int dc = (1 << log2Sz) >> 1;
        for (int i = 0; i < cw * ch; i++) dc += ac[i];
        dc >>= log2Sz;
        for (int i = 0; i < cw * ch; i++) ac[i] = (short)(ac[i] - dc);
    }

    private static ushort[] BuildCflPredRect(int dcPred, short[] ac, int cw, int ch, int alpha)
    {
        var pred = new ushort[cw * ch];
        for (int i = 0; i < cw * ch; i++)
        { int diff = ac[i] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6; pred[i] = (ushort)Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax); }
        return pred;
    }

    private static int BestCflAlphaRect(ushort[] plane, int planeW, int cbx, int cby, int cw, int ch, int dcPred, short[] ac)
    {
        int bestAlpha = 0; long bestSse = long.MaxValue;
        int lo = -16, hi = 16;
        if (Sp.CflSearchRange > 0)
        {
            long num = 0, den = 0;
            for (int y = 0; y < ch; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cw; x++) { int a = ac[y * cw + x]; num += (long)a * (plane[row + x] - dcPred); den += (long)a * a; }
            }
            int est = den == 0 ? 0 : (int)Math.Clamp(Math.Round(64.0 * num / den), -16, 16);
            lo = Math.Max(-16, est - Sp.CflSearchRange); hi = Math.Min(16, est + Sp.CflSearchRange);
        }
        for (int alpha = lo; alpha <= hi; alpha++)
        {
            long sse = 0;
            for (int y = 0; y < ch; y++)
            {
                int row = (cby + y) * planeW + cbx;
                for (int x = 0; x < cw; x++)
                {
                    int diff = ac[y * cw + x] * alpha, sign = diff >> 31, rounded = (Math.Abs(diff) + 32) >> 6;
                    int e = plane[row + x] - Math.Clamp(dcPred + ((rounded ^ sign) - sign), 0, PixMax);
                    sse += (long)e * e;
                }
            }
            if (sse < bestSse) { bestSse = sse; bestAlpha = alpha; }
        }
        return bestAlpha;
    }

    // Op-log marker ids for cdef_idx (read after the skip flag of a 64x64's first non-skip block): negative, so the
    // loop-restoration superblock markers ((sby << 16) | sbx) stay distinct.
    private static int CdefMarker(int bx4, int by4) => int.MinValue | ((by4 >> 4) << 15) | (bx4 >> 4);

    /// <summary>Writes cdef_idx at the first CDEF marker of each superblock (later markers of the same superblock are
    /// blocks after the first non-skip one). One instance per replay.</summary>
    private static Action<int, Av1MsacWriter> CdefIndexWriter(sbyte[] sbIdx, int sbCols, int bits)
    {
        var seen = new bool[sbIdx.Length];
        return (id, w) =>
        {
            int k = ((id >> 15) & 0xFFFF) * sbCols + (id & 0x7FFF);
            if (k >= seen.Length || seen[k]) return;
            seen[k] = true;
            if (bits > 0) w.EncodeLiteral((uint)Math.Max(0, (int)sbIdx[k]), bits);
        };
    }

    // Bits of the CfL sign / alpha symbols EncodeCflAlphas codes.
    private static double CflAlphaBits(Av1CdfContext cdf, int alphaU, int alphaV)
    {
        int signU = alphaU == 0 ? 0 : (alphaU < 0 ? 1 : 2);
        int signV = alphaV == 0 ? 0 : (alphaV < 0 ? 1 : 2);
        double bits = Av1CoeffEncode.SymBits(cdf.GetCflSignCdf(), signU * 3 + signV - 1);
        if (signU != 0) bits += Av1CoeffEncode.SymBits(cdf.GetCflAlphaCdf((signU == 2 ? 3 : 0) + signV), Math.Abs(alphaU) - 1);
        if (signV != 0) bits += Av1CoeffEncode.SymBits(cdf.GetCflAlphaCdf((signV == 2 ? 3 : 0) + signU), Math.Abs(alphaV) - 1);
        return bits;
    }

    private static void EncodeCflAlphas(Av1MsacWriter w, Av1CdfContext cdf, int alphaU, int alphaV)
    {
        int signU = alphaU == 0 ? 0 : (alphaU < 0 ? 1 : 2);
        int signV = alphaV == 0 ? 0 : (alphaV < 0 ? 1 : 2);
        w.EncodeSymbolAdapt(cdf.GetCflSignCdf(), signU * 3 + signV - 1, 7);
        if (signU != 0) w.EncodeSymbolAdapt(cdf.GetCflAlphaCdf((signU == 2 ? 3 : 0) + signV), Math.Abs(alphaU) - 1, 15);
        if (signV != 0) w.EncodeSymbolAdapt(cdf.GetCflAlphaCdf((signV == 2 ? 3 : 0) + signU), Math.Abs(alphaV) - 1, 15);
    }

    private static void CopyPlaneBlock(ushort[] src, int cn, ushort[] recon, int reconW, int cbx, int cby)
    {
        for (int y = 0; y < cn; y++) Array.Copy(src, y * cn, recon, (cby + y) * reconW + cbx, cn);
    }

    private static ushort[] FlatPlane(int value, int cn)
    {
        var p = new ushort[cn * cn]; Array.Fill(p, (ushort)Math.Clamp(value, 0, PixMax)); return p;
    }

    // Reconstruction SSE of a chroma block coded with `coeffs` on prediction `predPlane` (cn x cn), vs the source.
    private static long ChromaReconSse(int[] coeffs, int tx, int cn, int dcDq, int acDq, ushort[] predPlane,
        ushort[] src, int srcW, int cbx, int cby)
    {
        var tmp = new ushort[cn * cn];
        DequantAndReconstructPredRect(coeffs, tx, cn, cn, dcDq, acDq, predPlane, tmp, cn, 0, 0);
        long sse = 0;
        for (int y = 0; y < cn; y++)
            for (int x = 0; x < cn; x++) { int d = tmp[y * cn + x] - src[(cby + y) * srcW + cbx + x]; sse += (long)d * d; }
        return sse;
    }

    private static void EncodeLeafBlockColor(ColorPartCtx c, int bl, int bx4, int by4, int blk4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        if (c.Layout != Av1PixelLayout.I420)
        {
            // 4:4:4 / 4:2:2: the rect leaf is generic in its chroma dimensions, so square leaves use it too (w4 == h4).
            // (It codes luma at the max transform with no tx-depth / filter-intra selection — a later efficiency pass.)
            int sqBs = BlToBs(bl);
            EncodeRectLeafColor(c, sqBs, BlToTx(bl), -1, bx4, by4, blk4, blk4, edgeFlags);
            return;
        }

        int n = blk4 * 4, cn = n / 2;
        int tx = BlToTx(bl), ctx0 = tx - 1; // chroma tx = one size smaller (I420)
        int bs = BlToBs(bl);
        int bxR = bx4 & 31, byR = by4 & 31;
        int cxR = bxR >> 1, cyR = byR >> 1;      // chroma context index (4-unit)
        int cblk4 = Math.Max(1, blk4 >> 1);
        int bx = bx4 * 4, by = by4 * 4, cbx = bx4 * 2, cby = by4 * 2; // pixel positions
        bool cflAllowed = bl >= 2;               // blocks <= 32x32
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
        ref readonly var maxTDim = ref Av1Tables.TxfmDimensions[tx];

        // Filter-intra: signalled for DC-eligible luma blocks <= 32x32 (max block dim <= 3 in log2-of-4units). The
        // decoder reads use_filter_intra for EVERY such DC block, so the flag is emitted for all of them (0 when
        // filter is not chosen). This is the emission gate; selection uses the same set here.
        bool filterEligible = UseFilterIntra && Math.Max(Av1Tables.BlockDimensions[bs, 2], Av1Tables.BlockDimensions[bs, 3]) <= 3;

        // Luma: rate-distortion mode + tx-type decision (from reconstruction). Writes prediction into c.Pred.
        // libaom's slow-speed luma search (LumaSearch.cs): joint mode x tx size x tx type; it replaces ChooseLeafRdCore
        // and the tx-depth trials below, and leaves the winner reconstructed in ReconY.
        bool aomSq = Sp.LibaomLuma && (n <= 32 || Sp.AomLuma64) && bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        LumaPick aomPickSq = default;
        (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx) rd;
        if (aomSq)
        {
            var saveA = c.ALY.AsSpan(bxR, Math.Min(blk4, 32 - bxR)).ToArray();
            var saveL = c.LLY.AsSpan(byR, Math.Min(blk4, 32 - byR)).ToArray();
            aomPickSq = AomLumaSearch(c, bs, tx, bx4, by4, blk4, blk4, edgeFlags, IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]), true, filterEligible);
            if (aomPickSq.Txb == null) { c.PartAborted = true; return; }   // over the partition budget (rd_try_subblock)
            // depth 0 (and a palette winner) read their contexts from ALY / LLY: put the pre-block values back; a kept
            // split re-applies its per-tx contexts below
            saveA.CopyTo(c.ALY.AsSpan(bxR)); saveL.CopyTo(c.LLY.AsSpan(byR));
            var t0 = aomPickSq.Txb[0];
            rd = (aomPickSq.Mode, aomPickSq.Delta, t0.Cf, t0.Inv, t0.Idx);
            t_leafJ = aomPickSq.J;
        }
        else rd = ChooseLeafRdCore(c.ReconY, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AModeY[bxR], c.LModeY[byR], c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR), c.Pred, edgeFlags, filterEligible, bs, fullSet: UseFullIntraTxSet);
        Av1IntraPredMode yMode = rd.Mode; int yDelta = rd.Delta;
        int[] yC = rd.Coeffs; Av1TxType yInv = rd.Inv; int yTxIdx = rd.Idx;
        // A filter winner is coded as y_mode=DC; the Filter predictor uses (yMode==Filter, yDelta==filter mode).
        // THREE mode values (all verified vs dav1d): coded y_mode SYMBOL + uv context + NEIGHBOUR mode ctx = DC
        // (yModeSym); tx-type coefficient context = FilterModeToYMode (yModeNoFilt).
        bool isFilter = yMode == Av1IntraPredMode.Filter;
        int yModeSym = isFilter ? (int)Av1IntraPredMode.Dc : (int)yMode;
        int yModeNoFilt = isFilter ? Av1Tables.FilterModeToYMode[yDelta] : (int)yMode;

        // Transform-size (tx_depth) TRUE-RD search for luma. Only when residual is coded (depth-0 not all-zero) and
        // the block is fully inside the frame. Each depth is scored by its ACTUAL reconstruction (ReconstructLumaAtDepth
        // — depth 0 = the RDOQ'd single transform, depth>0 = the raster recon cascade) under SnapshotRd/RestoreRd:
        // J = real recon SSE + λ·(real coef bits + tx_size symbol bits). The winner is then reconstructed for keeps.
        // (Replaces the old source-predicted estimate, which mismatched the recon-predicted coding path.)
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;

        // Luma palette (screen content): the colour map as predictor, residual coded as for DC; kept when its RD beats
        // the regular choice (which then also pays has_palette_y = 0 when it is a DC block).
        LumaPal? yPal = null;
        bool palOk = c.ScreenContent && blk4 <= 16 && fullyInside;
        if (palOk)
        {
            var ymCdfP = c.Cdf.GetKfYModeCdf(Av1Tables.IntraModeContext[c.AModeY[bxR]], Av1Tables.IntraModeContext[c.LModeY[byR]]);
            int pSzC = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
            int pCtxC = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
            double nonPalJ = t_leafJ + (yModeSym == (int)Av1IntraPredMode.Dc
                ? LamK * c.AcDq * c.AcDq * Av1CoeffEncode.BoolBits(c.Cdf.GetPalYCdf(pSzC, pCtxC)[0], 0) : 0);
            var pal = SearchLumaPalette(c, bs, tx, bx4, by4, n, n, ymCdfP, nonPalJ);
            if (pal != null && pal.J < nonPalJ)
            {
                yPal = pal;
                yMode = Av1IntraPredMode.Dc; yDelta = 0; isFilter = false;
                yModeSym = (int)Av1IntraPredMode.Dc; yModeNoFilt = (int)Av1IntraPredMode.Dc;
                yC = pal.Coeffs; yInv = pal.Inv; yTxIdx = pal.Idx;
                Array.Copy(pal.Pred, c.Pred, n * n);
            }
        }
        int maxDepth = (!aomSq && UseColorTxDepth && HasNonZero(yC) && fullyInside && yPal == null) ? Math.Min((int)maxTDim.Max, Sp.ColorTxMaxDepth) : 0;
        int depth = 0;
        if (maxDepth > 0)
        {
            double lambda = LamK * c.AcDq * c.AcDq;
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            var txSzCdf = c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx);
            int txNsym = Math.Min((int)maxTDim.Max, 2);
            var snap = SnapshotRd(c, bx4, by4, blk4, Pooled(c, bl * 3 + 2));
            long txBestJ = long.MaxValue;
            int ymA = Av1Tables.IntraModeContext[c.AModeY[bxR]], ymL = Av1Tables.IntraModeContext[c.LModeY[byR]];
            double ModeBits(Av1IntraPredMode m, int dl) => Av1CoeffEncode.SymBits(c.Cdf.GetKfYModeCdf(ymA, ymL), (int)m)
                + (IsDirectional(m) ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)m - (int)Av1IntraPredMode.Vertical), dl + 3) : 0)
                + (filterEligible && m == Av1IntraPredMode.Dc ? Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraCdf((Av1BlockSize)bs), 0) : 0);
            // the winner's own mode bits (only compared across modes, so a filter winner keeps its flag cost out)
            double winModeBits = isFilter ? 0 : ModeBits(yMode, yDelta);
            (Av1IntraPredMode M, int D) alt = (yMode, yDelta);
            for (int d = 0; d <= maxDepth; d++)
            {
                var (_, _, bitsT) = ReconstructLumaAtDepth(c, bx4, by4, blk4, n, tx, ReduceTx(tx, d), d,
                    yMode, yDelta, yModeNoFilt, yC, yInv, yTxIdx, edgeFlags, bs);
                double txSizeBits = Av1CoeffEncode.SymBits(txSzCdf, Math.Min(d, txNsym));
                long j = LumaBlockSse(c, bx, by, n) + (long)(lambda * (bitsT + txSizeBits + winModeBits));
                if (j < txBestJ) { txBestJ = j; depth = d; alt = (yMode, yDelta); }
                RestoreRd(c, snap, bx4, by4, blk4);
                if (d == 0 || isFilter) continue;
                // joint: the other prescreened modes at this split depth
                for (int k = 0, tried = 0; k < t_topModeCount && tried < Sp.TxDepthAltModes; k++)
                {
                    var (m, dl) = CandidateModes[t_topModes[k]];
                    if (m == yMode && dl == yDelta) continue;
                    tried++;
                    var (_, _, bitsA) = ReconstructLumaAtDepth(c, bx4, by4, blk4, n, tx, ReduceTx(tx, d), d,
                        m, dl, (int)m, yC, yInv, yTxIdx, edgeFlags, bs);
                    long ja = LumaBlockSse(c, bx, by, n) + (long)(lambda * (bitsA + txSizeBits + ModeBits(m, dl)));
                    if (ja < txBestJ) { txBestJ = ja; depth = d; alt = (m, dl); }
                    RestoreRd(c, snap, bx4, by4, blk4);
                }
            }
            if (alt.M != yMode || alt.D != yDelta)
            {
                // a split depth won with another mode: it becomes the leaf's mode
                yMode = alt.M; yDelta = alt.D; isFilter = false;
                yModeSym = (int)yMode; yModeNoFilt = (int)yMode;
            }
        }
        bool aomUsed = aomSq && yPal == null;
        if (aomUsed)
        {
            depth = aomPickSq.Depth;
            if (depth > 0)
            {
                ref readonly var sTd = ref Av1Tables.TxfmDimensions[aomPickSq.Tx];
                foreach (var t in aomPickSq.Txb)
                {
                    byte cc = TxCoefCtx(t.Cf, aomPickSq.Tx);
                    int tR = (t.Px >> 2) & 31, lR = (t.Py >> 2) & 31;
                    for (int i = 0; i < sTd.W && tR + i < 32; i++) c.ALY[tR + i] = cc;
                    for (int j = 0; j < sTd.H && lR + j < 32; j++) c.LLY[lR + j] = cc;
                }
            }
        }
        else if (aomSq) foreach (var t in aomPickSq.Txb) Av1FwdTransform.ReturnLevels(t.Cf);
        int lumaTx = ReduceTx(tx, depth);
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];

        // Reconstruct luma at the chosen depth into ReconY for keeps (CfL needs the reconstructed luma AC; depth>0
        // records the per-tx-block coeffs for emission after the tx_size symbol). The libaom search already did.
        var (cfY, lumaTxb, _) = aomUsed
            ? (depth == 0 ? TxCoefCtx(yC, tx) : (byte)0x40, depth == 0 ? null : aomPickSq.Txb, 0.0)
            : ReconstructLumaAtDepth(c, bx4, by4, blk4, n, tx, lumaTx, depth,
                yMode, yDelta, yModeNoFilt, yC, yInv, yTxIdx, edgeFlags, bs);
        bool lumaAllZero = depth == 0 ? !HasNonZero(yC) : lumaTxb!.TrueForAll(t => !HasNonZero(t.Cf));

        if (c.Mono)
        {
            // Monochrome: the colour syntax minus uv_mode / CfL / has_palette_uv / chroma coefficients.
            int mSkip = lumaAllZero ? 1 : 0;
            c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(c.ASkip[bxR] + c.LSkip[byR]), (uint)mSkip);
            if (mSkip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(Av1Tables.IntraModeContext[c.AModeY[bxR]], Av1Tables.IntraModeContext[c.LModeY[byR]]), yModeSym, 12);
            if (IsDirectional(yMode))
                c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
            if (c.ScreenContent && blk4 <= 16 && yModeSym == (int)Av1IntraPredMode.Dc)
            {
                int pSzCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
                int pCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
                c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(pSzCtx, pCtx), yPal != null ? 1u : 0u);
                if (yPal != null) EmitLumaPaletteColors(c, yPal, bx4, by4, pSzCtx);
            }
            if (filterEligible && yPal == null && (yMode == Av1IntraPredMode.Dc || isFilter))
            {
                c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)bs), (uint)(isFilter ? 1 : 0));
                if (isFilter) c.Msac.EncodeSymbolAdapt(c.Cdf.GetFilterIntraModeCdf(), yDelta, 4);
            }
            if (yPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, yPal.Map, yPal.Size, n, n, blk4, blk4, isLuma: true);
            if (UseColorTxDepth && maxTDim.Max > (byte)Av1TxSize.Tx4x4)
            {
                int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
                c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), depth, Math.Min((int)maxTDim.Max, 2));
            }
            if (mSkip == 0)
            {
                if (depth == 0)
                {
                    int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
                    if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                        Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, yModeNoFilt, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
                    else
                        Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, 0, yModeNoFilt, yC, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
                }
                else
                    EmitSplitLuma(c, lumaTx, yModeNoFilt, lumaTxb!);   // (V_DCT / H_DCT tx blocks code their 1D scan)
            }
            int mW = Math.Min(blk4, c.Bw4 - bx4), mH = Math.Min(blk4, c.Bh4 - by4);
            sbyte mLw = (sbyte)lTDim.Lw, mLh = (sbyte)lTDim.Lh;
            for (int i = 0; i < mW && bxR + i < 32; i++) { if (depth == 0) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yModeSym; c.ASkip[bxR + i] = (byte)mSkip; c.ATxY[bxR + i] = mLw; c.APalSz[bxR + i] = 0; }
            for (int j = 0; j < mH && byR + j < 32; j++) { if (depth == 0) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yModeSym; c.LSkip[byR + j] = (byte)mSkip; c.LTxY[byR + j] = mLh; c.LPalSz[byR + j] = 0; }
            FillPaletteCtx(c, bx4, by4, mW, mH, yPal, null);
            return;
        }

        int dcU = ChromaDc(c, c.ReconU, cbx, cby, cn, cn);
        int dcV = ChromaDc(c, c.ReconV, cbx, cby, cn, cn);
        int scanLenC = Av1Tables.Scans[ctx0].Length;
        var qfU = new double[scanLenC]; var qfV = new double[scanLenC];
        int[] uC = ForwardResidual(c.U, c.Cw, cbx, cby, cn, dcU, c.DcDq, c.AcDq, scanLenC, qfU);
        int[] vC = ForwardResidual(c.V, c.Cw, cbx, cby, cn, dcV, c.DcDq, c.AcDq, scanLenC, qfV);
        double clam0 = LamK * c.AcDq * c.AcDq;
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, yModeSym);
        var dcuP = FlatPlane(dcU, cn); var dcvP = FlatPlane(dcV, cn);

        // Chroma rate for the UV decision: the CDF-based estimate with the contexts the coefficients are coded with
        // (ChromaRateExact), or the flat per-coefficient proxy.
        ref readonly var eUvTd = ref Av1Tables.TxfmDimensions[ctx0];
        int eUSkip = Av1CoeffDecode.GetSkipCtx(in eUvTd, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
        int eVSkip = Av1CoeffDecode.GetSkipCtx(in eUvTd, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
        int eUSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
        int eVSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
        bool exactC = Sp.ChromaRateExact;
        var eCoef = c.Cdf.Coef; var eMode = c.Cdf.Mode; var eCdf = c.Cdf; int eTx = ctx0;
        double CRate(int[] cu, int[] cv) => exactC
            ? Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, eTx, 1, 0, cu, eUSkip, eUSign, 0) + Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, eTx, 1, 0, cv, eVSkip, eVSign, 0)
            : CoeffCost(cu) + CoeffCost(cv);
        double CflRate(int aU, int aV) => exactC ? CflAlphaBits(eCdf, aU, aV) : (aU != 0 ? 5 : 0) + (aV != 0 ? 5 : 0);
        bool rdoqC = Sp.ChromaRdoqInSearch && UseChromaRdoq;
        double eClam = ChromaLamScale * LamK * c.AcDq * c.AcDq;
        int eDcDq = c.DcDq, eAcDq = c.AcDq;
        // AomChroma: every candidate trellised (libaom av1_txfm_uvrd), the CfL alphas RD-searched per plane below
        bool aomSqC = Sp.AomChroma && UseRdoq;
        void CRdoq(int[] cu, double[] qu, int[] cv, double[] qv)
        {
            if (aomSqC)
            {
                if (HasNonZero(cu)) Av1CoeffEncode.TrellisOptimize(eCoef, eTx, 1, cu, qu, eDcDq, eAcDq, eUSkip, eUSign, eClam);
                if (HasNonZero(cv)) Av1CoeffEncode.TrellisOptimize(eCoef, eTx, 1, cv, qv, eDcDq, eAcDq, eVSkip, eVSign, eClam);
                return;
            }
            if (!rdoqC) return;
            Av1CoeffEncode.RdoqOptimize(eCoef, eMode, eTx, 1, 0, cu, qu, eDcDq, eAcDq, eUSkip, eUSign, 0, eClam);
            Av1CoeffEncode.RdoqOptimize(eCoef, eMode, eTx, 1, 0, cv, qv, eDcDq, eAcDq, eVSkip, eVSign, 0, eClam);
        }
        CRdoq(uC, qfU, vC, qfV);

        // Winner state across DC / directional-UV / CfL. predU/predV are the reconstruction prediction planes.
        int uvMode = 0, uvDelta = 0; bool useCfl = false;
        ushort[] predU = dcuP, predV = dcvP; int alphaU = 0, alphaV = 0;
        double bestJ = ChromaReconSse(uC, ctx0, cn, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby)
                     + ChromaReconSse(vC, ctx0, cn, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby)
                     + clam0 * (CRate(uC, vC) + Av1CoeffEncode.SymBits(uvModeCdf, 0));

        // Directional / Smooth / Paeth UV modes. AV1 lets chroma pick any of the 13 intra modes; we only coded
        // DC/CfL before, so sharp colour boundaries (e.g. piechart slices) paid full chroma residual. Byte-exact:
        // the decoder reconstructs every UV mode, and we mirror its chroma edge prep exactly — single chroma tx
        // block (square leaf), so uvSbHasTr/Bl reduce to the block's I420 top-right/bottom-left availability, and
        // the smooth-neighbour edge filter reads the stored UV-mode context.
        if (UseUvModeSearch)
        {
            var chromaEdge =
                ((edgeFlags & Av1EdgeFlags.I420TopHasRight) != 0 ? Av1EdgeFlags.I444TopHasRight : 0) |
                ((edgeFlags & Av1EdgeFlags.I420LeftHasBottom) != 0 ? Av1EdgeFlags.I444LeftHasBottom : 0);
            int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
            int cbw4 = c.Bw4 >> 1, cbh4 = c.Bh4 >> 1, cbx4 = bx4 >> 1, cby4 = by4 >> 1;
            int bDimW = Av1Tables.BlockDimensions[bs, 2], bDimH = Av1Tables.BlockDimensions[bs, 3];
            bool uvAngleOk = bDimW + bDimH >= 2;
            var pu = new ushort[cn * cn]; var pv = new ushort[cn * cn];

            // SAD prescreen on U (SATD's 8x8 tiling overflows the 4x4 chroma case), then full RD on the best few.
            Span<int> topIdx = stackalloc int[RdUvCandidates + 1];
            Span<long> topCost = stackalloc long[RdUvCandidates + 1];
            topCost.Fill(long.MaxValue);
            uint hogUv = uvAngleOk ? HogSkipMask(c.U, c.Cw, cbx, cby, cn, cn, (c.Bw4 * 4) >> 1, (c.Bh4 * 4) >> 1, Sp.HogChromaLevel, 4) : 0;
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc) continue;      // DC is the baseline above
                if (delta != 0 && !uvAngleOk) continue;         // no uv angle_delta at tiny sizes
                if ((hogUv >> (int)mode & 1) != 0) continue;
                PredictIntra(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pu, chromaEdge, cIntraFlags);
                long sad = SadBlock(c.U, c.Cw, cbx, cby, pu, cn);
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < topCost[k]) { for (int j = RdUvCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = sad; topIdx[k] = ci; break; }
            }
            int uvN = AddUvLumaWinner(topIdx, topCost, yMode, yDelta, uvAngleOk);
            for (int t = 0; t < uvN; t++)
            {
                if (topCost[t] == long.MaxValue) break;
                (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
                PredictIntra(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pu, chromaEdge, cIntraFlags);
                PredictIntra(c.ReconV, c.Cw, cbw4, cbh4, cbx4, cby4, cn, mode, delta, pv, chromaEdge, cIntraFlags);
                // Chroma tx-type is DERIVED from the UV mode (TxTypeFromUvMode) — no symbol coded. All map to
                // TX_CLASS_2D so the scan/coeff-coding is unchanged, but the transform KERNEL differs, so the
                // forward transform and the reconstruction SSE MUST use it (coding DctDct here desyncs vs libdav1d).
                var uvTx = (Av1TxType)Av1Tables.TxTypeFromUvMode[(int)mode];
                var uvFwd = FwdTypeForTxType(uvTx);
                var qfu2 = new double[scanLenC]; var qfv2 = new double[scanLenC];
                int[] uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pu, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfu2, uvFwd);
                int[] vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pv, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfv2, uvFwd);
                CRdoq(uu, qfu2, vv, qfv2);
                double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                    + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
                double j = ReconSseCandRect(uu, ctx0, cn, cn, c.DcDq, c.AcDq, pu, c.U, c.Cw, cbx, cby, uvTx)
                         + ReconSseCandRect(vv, ctx0, cn, cn, c.DcDq, c.AcDq, pv, c.V, c.Cw, cbx, cby, uvTx)
                         + clam0 * (CRate(uu, vv) + modeBits);
                if (j < bestJ)
                {
                    bestJ = j; uvMode = (int)mode; uvDelta = delta; useCfl = false;
                    uC = uu; vC = vv; qfU = qfu2; qfV = qfv2;
                    // the candidate buffers become the winner's; the previous winner's (unless DC) take the next candidates
                    (predU, pu) = (pu, predU != dcuP ? predU : new ushort[pu.Length]);
                    (predV, pv) = (pv, predV != dcvP ? predV : new ushort[pv.Length]);
                }
            }
        }

        // Chroma-from-luma: predict chroma AC from reconstructed luma AC scaled by a signed per-plane alpha; keep
        // CfL when it codes cheaper (incl. the alpha signalling). CfL-allowed sizes only.
        if (cflAllowed && UseCfl)
        {
            var ac = new short[cn * cn];
            ComputeCflAcEnc(c.ReconY, c.W, bx, by, cn, ac);
            int aU = BestCflAlpha(c.U, c.Cw, cbx, cby, cn, dcU, ac);
            int aV = BestCflAlpha(c.V, c.Cw, cbx, cby, cn, dcV, ac);
            if (aomSqC)
            {
                // cfl_rd_pick_alpha: each plane priced (trellised) at the alphas around its estimate; best pair, not both 0
                int R = Math.Max(0, Sp.AomCflRange);
                double PJ(ushort[] src, ushort[] pr, int skc, int sgc, out int[] lv, out double[] q)
                {
                    q = new double[scanLenC];
                    lv = ForwardResidualPredRect(src, c.Cw, cbx, cby, pr, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, q);
                    if (HasNonZero(lv)) Av1CoeffEncode.TrellisOptimize(eCoef, eTx, 1, lv, q, eDcDq, eAcDq, skc, sgc, eClam);
                    return ChromaReconSse(lv, ctx0, cn, c.DcDq, c.AcDq, pr, src, c.Cw, cbx, cby)
                         + clam0 * Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, eTx, 1, 0, lv, skc, sgc, 0);
                }
                var ju = new double[33]; var jv = new double[33];
                var lu = new int[33][]; var lvv = new int[33][]; var qu = new double[33][]; var qv = new double[33][];
                var pu2 = new ushort[33][]; var pv2 = new ushort[33][];
                for (int k = 0; k < 33; k++) { ju[k] = jv[k] = double.MaxValue; }
                for (int a = Math.Max(-16, aU - R); a <= Math.Min(16, aU + R); a++)
                { pu2[a + 16] = BuildCflPred(dcU, ac, cn, a); ju[a + 16] = PJ(c.U, pu2[a + 16], eUSkip, eUSign, out lu[a + 16], out qu[a + 16]); }
                for (int a = Math.Max(-16, aV - R); a <= Math.Min(16, aV + R); a++)
                { pv2[a + 16] = BuildCflPred(dcV, ac, cn, a); jv[a + 16] = PJ(c.V, pv2[a + 16], eVSkip, eVSign, out lvv[a + 16], out qv[a + 16]); }
                double cflBest = double.MaxValue; int bu = 16, bv = 16;
                double cflMode = Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma);
                for (int x1 = 0; x1 < 33; x1++)
                {
                    if (ju[x1] == double.MaxValue) continue;
                    for (int x2 = 0; x2 < 33; x2++)
                    {
                        if (jv[x2] == double.MaxValue || (x1 == 16 && x2 == 16)) continue;
                        double jj = ju[x1] + jv[x2] + clam0 * (cflMode + CflAlphaBits(eCdf, x1 - 16, x2 - 16));
                        if (jj < cflBest) { cflBest = jj; bu = x1; bv = x2; }
                    }
                }
                if (cflBest < bestJ)
                {
                    bestJ = cflBest; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = bu - 16; alphaV = bv - 16;
                    uC = lu[bu]; vC = lvv[bv]; qfU = qu[bu]; qfV = qv[bv]; predU = pu2[bu]; predV = pv2[bv];
                }
            }
            else if (aU != 0 || aV != 0)
            {
                var pcflU = BuildCflPred(dcU, ac, cn, aU);
                var pcflV = BuildCflPred(dcV, ac, cn, aV);
                var qfUcfl = new double[scanLenC]; var qfVcfl = new double[scanLenC];
                int[] uCcfl = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pcflU, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfUcfl);
                int[] vCcfl = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pcflV, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qfVcfl);
                CRdoq(uCcfl, qfUcfl, vCcfl, qfVcfl);
                double cflJ = ChromaReconSse(uCcfl, ctx0, cn, c.DcDq, c.AcDq, pcflU, c.U, c.Cw, cbx, cby)
                            + ChromaReconSse(vCcfl, ctx0, cn, c.DcDq, c.AcDq, pcflV, c.V, c.Cw, cbx, cby)
                            + clam0 * (CRate(uCcfl, vCcfl) + Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma) + CflRate(aU, aV));
                if (cflJ < bestJ)
                {
                    bestJ = cflJ; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = aU; alphaV = aV;
                    uC = uCcfl; vC = vCcfl; qfU = qfUcfl; qfV = qfVcfl; predU = pcflU; predV = pcflV;
                }
            }
        }

        // Chroma palette (screen content; uv_mode DC): one map over (u, v) pairs, coded residual on top.
        UvPal? uvPal = null;
        if (palOk)
        {
            foreach (var cp in UvPaletteCandidates(c, cbx, cby, cn, cn))
            {
                var qu = new double[scanLenC]; var qv = new double[scanLenC];
                int[] uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, cp.PredU, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qu);
                int[] vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, cp.PredV, cn, cn, ctx0, c.DcDq, c.AcDq, scanLenC, qv);
                if (aomSqC) CRdoq(uu, qu, vv, qv);
                double j = ChromaReconSse(uu, ctx0, cn, c.DcDq, c.AcDq, cp.PredU, c.U, c.Cw, cbx, cby)
                         + ChromaReconSse(vv, ctx0, cn, c.DcDq, c.AcDq, cp.PredV, c.V, c.Cw, cbx, cby)
                         + clam0 * (CRate(uu, vv) + Av1CoeffEncode.SymBits(uvModeCdf, 0) + UvPaletteBits(c, cp, bx4, by4, bs, yPal != null, cn, cn));
                if (j < bestJ)
                {
                    bestJ = j; uvPal = cp; uvMode = 0; uvDelta = 0; useCfl = false;
                    uC = uu; vC = vv; qfU = qu; qfV = qv; predU = cp.PredU; predV = cp.PredV;
                }
            }
        }

        // Chroma RDOQ: trim coefficients whose coding rate outweighs their (dq-scaled) distortion, the same
        // rate-distortion coefficient optimisation luma gets in the rect leaf. Chroma was previously left at
        // round-to-nearest quantisation, which over-codes it (measured 2-6 dB above luma at matched rate). Must
        // run before the skip/txb_skip decision below so an all-zeroed plane is coded as skipped.
        if (UseChromaRdoq && !rdoqC && !aomSqC)
        {
            double clam = ChromaLamScale * LamK * c.AcDq * c.AcDq;
            ref readonly var uvtd0 = ref Av1Tables.TxfmDimensions[ctx0];
            int ruSkip = Av1CoeffDecode.GetSkipCtx(in uvtd0, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int rvSkip = Av1CoeffDecode.GetSkipCtx(in uvtd0, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int ruSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int rvSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, qfU, c.DcDq, c.AcDq, ruSkip, ruSign, 0, clam);
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, qfV, c.DcDq, c.AcDq, rvSkip, rvSign, 0, clam);
        }

        int skip = (!lumaAllZero || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        if (skip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
        int yAboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int yLeftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        // Filter blocks code the Y-mode SYMBOL as DC (real mode Filter is signalled by use_filter_intra below); the
        // uv-mode context also sees DC. DC/Filter carry no angle_delta.
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAboveCtx, yLeftCtx), yModeSym, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        int uvSym = useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : uvMode;
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvSym, uvNsym);
        if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        else if (IsDirectional((Av1IntraPredMode)uvMode) &&
                 Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] >= 2)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);

        // has_palette flags (this non-palette block emits 0). When screen-content tools are on the decoder reads
        // has_palette_y for every size-eligible block whose Y mode is DC, and — INDEPENDENTLY — has_palette_uv for
        // every such block whose UV mode is DC (not CfL). The size gate max(bw4,bh4)<=16 && bw4+bh4>=4 is always
        // true for the square leaf's 8x8..64x64, so only the per-plane DC condition matters.
        if (c.ScreenContent && blk4 <= 16)
        {
            int pSzCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
            if (yModeSym == (int)Av1IntraPredMode.Dc)
            {
                int pCtx = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
                c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(pSzCtx, pCtx), yPal != null ? 1u : 0u);
                if (yPal != null) EmitLumaPaletteColors(c, yPal, bx4, by4, pSzCtx);
            }
            if (!useCfl && uvMode == 0)   // has_palette_uv only when UvMode==DC
            {
                c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(yPal != null ? 1 : 0), uvPal != null ? 1u : 0u);
                if (uvPal != null) EmitUvPaletteColors(c, uvPal, bx4, by4, pSzCtx);
            }
        }

        // filter_intra: for DC-coded eligible blocks (no palette — always true on this non-palette leaf), emit
        // use_filter_intra + the filter mode, in the exact decode_b position (after palette flags, before tx_size).
        if (filterEligible && yPal == null && (yMode == Av1IntraPredMode.Dc || isFilter))
        {
            c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)bs), (uint)(isFilter ? 1 : 0));
            if (isFilter) c.Msac.EncodeSymbolAdapt(c.Cdf.GetFilterIntraModeCdf(), yDelta, 4);
        }
        // palette colour-index maps (after filter_intra, before tx_size: dav1d decode_b order)
        if (yPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, yPal.Map, yPal.Size, n, n, blk4, blk4, isLuma: true);
        if (uvPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, uvPal.Map, uvPal.Size, cn, cn, cblk4, cblk4, isLuma: false);

        // tx_size (read_tx_size): coded for every intra block > 4x4 when tx_mode=SELECT — including skip blocks
        // (depth 0). Comes after all mode info, before residual, mirroring the decoder + grayscale path.
        if (UseColorTxDepth && maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), depth, Math.Min((int)maxTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[ctx0];
            int uSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in uvtDim, bs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int uSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(ctx0, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));

            if (depth == 0)
            {
                int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
                if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                    Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, yModeNoFilt, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
                else
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, 0, yModeNoFilt, yC, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            }
            else
            {
                EmitSplitLuma(c, lumaTx, yModeNoFilt, lumaTxb!);   // (V_DCT / H_DCT tx blocks code their 1D scan)
            }
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, ctx0, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);

            {
                var uvTxR = (Av1TxType)Av1Tables.TxTypeFromUvMode[uvSym];
                cfU = DequantAndReconstructPredRect(uC, ctx0, cn, cn, c.DcDq, c.AcDq, predU, c.ReconU, c.Cw, cbx, cby, uvTxR);
                cfV = DequantAndReconstructPredRect(vC, ctx0, cn, cn, c.DcDq, c.AcDq, predV, c.ReconV, c.Cw, cbx, cby, uvTxR);
            }
        }
        else
        {
            CopyPlaneBlock(predU, cn, c.ReconU, c.Cw, cbx, cby);
            CopyPlaneBlock(predV, cn, c.ReconV, c.Cw, cbx, cby);
        }

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        // Luma LCoef context: for depth>0 it was already filled per-tx-block during reconstruction, so only fill it
        // here (with the single-transform cfY) at depth 0. Mode/skip/tx-size context is filled over the whole block.
        // Neighbour mode context stores DC for filter blocks (yModeSym), NOT FilterModeToYMode — matches the decoder
        // (dav1d decode.c: FILTER_PRED -> DC_PRED). This is distinct from the tx-type context (yModeNoFilt) above.
        for (int i = 0; i < yW && bxR + i < 32; i++) { if (depth == 0) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yModeSym; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = 0; }
        for (int j = 0; j < yH && byR + j < 32; j++) { if (depth == 0) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yModeSym; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = 0; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; c.AModeUv[cxR + i] = (byte)uvSym; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; c.LModeUv[cyR + j] = (byte)uvSym; }
        FillPaletteCtx(c, bx4, by4, yW, yH, yPal, uvPal);
    }

    // Emits a fully-inside DC luma block as a palette block, in the decoder's exact order: skip=1, y_mode=DC,
    // uv_mode=DC, has_palette_y=1 + size + colours, has_palette_uv=0, palette indices, tx_size(depth 0), no
    // coeffs. Then reconstructs luma from the palette (chroma = DC pred) and updates neighbour state.
    private static void EmitPaletteBlock(ColorPartCtx c, int bx4, int by4, int blk4, int bs, int tx, int n,
        ushort[] palColors, int palSz, byte[] palIdx, int szCtx, int palCtx, in Av1TxfmInfo maxTDim, bool cflAllowed, int uvNsym)
    {
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> 1, cyR = byR >> 1;
        int bx = bx4 * 4, by = by4 * 4, cbx = bx4 * 2, cby = by4 * 2;
        int cn = n / 2, cblk4 = Math.Max(1, blk4 >> 1), stride = blk4 * 4;
        int bitDepth = Bd;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), 1);
        int yAbove = Av1Tables.IntraModeContext[c.AModeY[bxR]], yLeft = Av1Tables.IntraModeContext[c.LModeY[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(yAbove, yLeft), 0, 12);         // DC
        if (!c.Mono) c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, 0), 0, uvNsym);      // UV DC
        c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(szCtx, palCtx), 1);
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetPalSzCdf(0, szCtx), palSz - 2, 6);

        int leftPalSz = c.LPalSz[byR]; if (leftPalSz > 8) leftPalSz = 0;
        int abovePalSz = (by4 & 15) != 0 ? c.APalSz[bxR] : 0; if (abovePalSz > 8) abovePalSz = 0;
        Span<ushort> lCol = stackalloc ushort[8], aCol = stackalloc ushort[8];
        for (int ci = 0; ci < leftPalSz; ci++) lCol[ci] = c.LPalCol[byR * 8 + ci];
        for (int ci = 0; ci < abovePalSz; ci++) aCol[ci] = c.APalCol[bxR * 8 + ci];
        Av1CoeffEncode.EncodeLumaPaletteColorsCore(c.Msac, palColors, palSz, lCol, leftPalSz, aCol, abovePalSz, bitDepth);

        if (!c.Mono) c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(1), 0);                // has_palette_uv = 0
        Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, palIdx, palSz, n, n, blk4, blk4, isLuma: true);

        if (UseColorTxDepth && maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= maxTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= maxTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), 0, Math.Min((int)maxTDim.Max, 2));
        }

        // Reconstruct luma from palette; chroma from DC prediction (skip ⇒ no residual on either).
        for (int yy = 0; yy < n; yy++)
            for (int xx = 0; xx < n; xx++)
                c.ReconY[(by + yy) * c.W + bx + xx] = (ushort)palColors[palIdx[yy * stride + xx]];
        if (!c.Mono)
        {
            int dcU = ChromaDc(c, c.ReconU, cbx, cby, cn, cn);
            int dcV = ChromaDc(c, c.ReconV, cbx, cby, cn, cn);
            FillFlat(c.ReconU, c.Cw, cbx, cby, cn, dcU);
            FillFlat(c.ReconV, c.Cw, cbx, cby, cn, dcV);
        }

        int yW = Math.Min(blk4, c.Bw4 - bx4), yH = Math.Min(blk4, c.Bh4 - by4);
        int cW = Math.Min(cblk4, (c.Bw4 - bx4 + 1) >> 1), cH = Math.Min(cblk4, (c.Bh4 - by4 + 1) >> 1);
        sbyte txLw = (sbyte)maxTDim.Lw, txLh = (sbyte)maxTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++)
        {
            c.ALY[bxR + i] = 0x40; c.AModeY[bxR + i] = 0; c.ASkip[bxR + i] = 1; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = (byte)palSz;
            for (int ci = 0; ci < palSz; ci++) c.APalCol[(bxR + i) * 8 + ci] = palColors[ci];
        }
        for (int j = 0; j < yH && byR + j < 32; j++)
        {
            c.LLY[byR + j] = 0x40; c.LModeY[byR + j] = 0; c.LSkip[byR + j] = 1; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = (byte)palSz;
            for (int ci = 0; ci < palSz; ci++) c.LPalCol[(byR + j) * 8 + ci] = palColors[ci];
        }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = 0x40; c.ACV[cxR + i] = 0x40; c.AModeUv[cxR + i] = 0; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = 0x40; c.LCV[cyR + j] = 0x40; c.LModeUv[cyR + j] = 0; }
    }

    // Estimates palette colour-index-map bits (uniform top-left + wavefront ranks under the current ColorMap CDF,
    // no adaptation) for the palette-vs-transform RD decision. Mirrors EncodePaletteIndices with SymBits.
    private static double EstimatePaletteIndexBits(Av1CdfModeContext modeCdf, byte[] idxMap, int palSize, int width, int height, int bw4, int bh4)
    {
        int stride = bw4 * 4;
        double bits = Math.Log2(palSize);   // top-left uniform
        Span<byte> order = stackalloc byte[8];
        int maxDiag = 4 * (bw4 + bh4) - 1;
        for (int diag = 1; diag < maxDiag; diag++)
        {
            int first = Math.Min(diag, width - 1), last = Math.Max(0, diag - height + 1);
            for (int x = first; x >= last; x--)
            {
                int y = diag - x;
                int l = x > 0 ? idxMap[y * stride + x - 1] : 0xFF;
                int tt = y > 0 ? idxMap[(y - 1) * stride + x] : 0xFF;
                int tl = (x > 0 && y > 0) ? idxMap[(y - 1) * stride + x - 1] : 0xFF;
                int ctx = Av1CoeffDecode.BuildColorOrder(order, palSize, l, tt, tl);
                int target = idxMap[y * stride + x], colorIdx = 0; while (order[colorIdx] != target) colorIdx++;
                bits += Av1CoeffEncode.SymBits(modeCdf.ColorMap[(palSize - 2) * 5 + ctx], colorIdx);
            }
        }
        return bits;
    }

    // Rate-DISTORTION cost J = SSE + λ·bits of a colour leaf's luma coded at a given (uniform) tx size. Each tx
    // block is predicted from the SOURCE plane (≈ reconstruction) and reconstructed through the decoder's inverse,
    // so the distortion term reflects what the transform can actually represent. Used for the colour tx-depth
    // decision: a pure rate estimate over-splits smooth content (splitting barely changes SSE but the estimate
    // undercounts the per-tx-block overhead), so the SSE term is essential to keep large blocks whole.
    // Reconstructs luma at a given tx_depth EXACTLY as the emit path does (depth 0 = the single block-size transform
    // from the RDOQ'd yC; depth>0 = the quadtree cascade, each sub-block predicted from reconstruction in raster
    // order, tx-type chosen + RDOQ'd), writing pixels into c.ReconY and, for depth>0, the per-tx neighbour coef
    // context into c.ALY/c.LLY. Returns the coef-context byte (depth 0), the per-tx records (depth>0), and the total
    // coefficient-bit estimate. Used both to TRIAL each depth (under SnapshotRd/RestoreRd) and to commit the winner,
    // so the tx_depth decision is true-RD: it compares the real reconstruction + real coded cost of each depth.
    private static (byte CfY, List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? LumaTxb, double CoefBits)
        ReconstructLumaAtDepth(ColorPartCtx c, int bx4, int by4, int blk4, int n, int tx, int lumaTx, int depth,
            Av1IntraPredMode yMode, int yDelta, int yModeNoFilt, int[] yC, Av1TxType yInv, int yTxIdx,
            Av1EdgeFlags edgeFlags, int bs)
    {
        int bxR = bx4 & 31, byR = by4 & 31, bx = bx4 * 4, by = by4 * 4;
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (depth == 0)
        {
            int ySign = Av1CoeffDecode.GetDcSignCtx(tx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
            byte cfY = DequantAndReconstructPred(yC, tx, n, c.DcDq, c.AcDq, c.Pred, c.ReconY, c.W, bx, by, yInv);
            double bits = (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, tx, yModeNoFilt, yInv, yC, 0, ySign)
                : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, tx, 0, yModeNoFilt, yC, 0, ySign, yTxIdx, fullSet: UseFullIntraTxSet);
            return (cfY, null, bits);
        }
        var lumaTxb = new List<(int[], Av1TxType, int, int, int, int, int)>();
        double coefBits = 0;
        int txN = lTDim.W * 4, txW4 = lTDim.W;
        var predBuf = new ushort[txN * txN];
        int sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0;
        int sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0;
        for (int iy = 0; iy < blk4; iy += txW4)
            for (int ix = 0; ix < blk4; ix += txW4)
            {
                int cbx4 = bx4 + ix, cby4 = by4 + iy, cbxR = cbx4 & 31, cbyR = cby4 & 31;
                var localEdge =
                    (((iy > 0 || sbHasTr == 0) && (ix + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                    ((ix > 0 || (sbHasBl == 0 && iy + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                PredictIntra(c.ReconY, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf, localEdge, IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]));
                int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, predBuf, txN);
                (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, lumaTx, c.DcDq, c.AcDq,
                    predBuf, c.Luma, c.W, cbx4 * 4, cby4 * 4, LamK * c.AcDq * c.AcDq);
                int skc = Av1CoeffDecode.GetSkipCtx(in lTDim, bs, c.ALY.AsSpan(cbxR), c.LLY.AsSpan(cbyR), 0, 0);
                int snc = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(cbxR), c.LLY.AsSpan(cbyR));
                if (HasNonZero(cf))
                {
                    var qfTx = new double[Av1Tables.Scans[lumaTx].Length];
                    Av1FwdTransform.ForwardQuantTyped(res, txN, c.DcDq, c.AcDq, qfTx.Length, FwdTypeForIdx(idx), qfTx);
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, cf, qfTx,
                        c.DcDq, c.AcDq, skc, snc, idx, RdoqScale * LamK * c.AcDq * c.AcDq);
                }
                coefBits += Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, cf, skc, snc, idx, fullSet: UseFullIntraTxSet);
                lumaTxb.Add((cf, inv, idx, skc, snc, cbx4 * 4, cby4 * 4));
                byte txCfCtx = DequantAndReconstructPred(cf, lumaTx, txN, c.DcDq, c.AcDq, predBuf, c.ReconY, c.W, cbx4 * 4, cby4 * 4, inv);
                int tcw = Math.Min(txW4, c.Bw4 - cbx4), tch = Math.Min(txW4, c.Bh4 - cby4);
                for (int i = 0; i < tcw && cbxR + i < 32; i++) c.ALY[cbxR + i] = txCfCtx;
                for (int j = 0; j < tch && cbyR + j < 32; j++) c.LLY[cbyR + j] = txCfCtx;
            }
        return (0x40, lumaTxb, coefBits);
    }

    // RectTxDepth trial: the w4 x h4 luma leaf on its depth-1 transform (TxfmDimensions.Sub), tx blocks in raster order,
    // each predicted from the reconstruction with the decoder's per-tx edge flags (dav1d recon_b_intra), its tx type
    // by RD over the intra set for that size, RDOQ'd. Writes ReconY and the per-tx ALY/LLY coefficient contexts.
    // Returns J = SSE + lambda * coefficient bits (tx_size symbol excluded) and the per-tx records.
    private static (double J, List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)> Txb)
        RectLumaSplitTrial(ColorPartCtx c, int lumaBs, int lumaTx, int bx4, int by4, int w4, int h4,
            Av1IntraPredMode yMode, int yDelta, int yModeNoFilt, Av1EdgeFlags edgeFlags, int intraFlags)
    {
        int stx = Av1Tables.TxfmDimensions[lumaTx].Sub;
        ref readonly var sTD = ref Av1Tables.TxfmDimensions[stx];
        int tw4 = sTD.W, th4 = sTD.H, tw = tw4 * 4, th = th4 * 4, sScan = Av1Tables.Scans[stx].Length;
        bool symbolCoded = sTD.Max <= (byte)Av1TxSize.Tx16x16;
        bool full = UseFullIntraTxSet && sTD.Min < (byte)Av1TxSize.Tx16x16;
        var txSet = (full && symbolCoded) ? IntraTxTypesFull : (symbolCoded ? IntraTxTypes : DctOnly);
        double lambda = LamK * c.AcDq * c.AcDq;
        var scr = t_scSplit ??= new LeafScratch();
        var pred = scr.P1; var res = scr.R; var qf = scr.Q1; var qfBest = scr.Q2;
        bool sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0, sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0;
        var list = new List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>();
        double bitsSum = 0;
        long sseSum = 0;
        for (int iy = 0; iy < h4; iy += th4)
            for (int ix = 0; ix < w4; ix += tw4)
            {
                int tx4 = bx4 + ix, ty4 = by4 + iy, txR = tx4 & 31, tyR = ty4 & 31, px = tx4 * 4, py = ty4 * 4;
                var localEdge = (((iy > 0 || !sbHasTr) && ix + tw4 >= w4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                                ((ix > 0 || (!sbHasBl && iy + th4 >= h4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, tx4, ty4, tw, th, yMode, yDelta, pred, localEdge, intraFlags);
                for (int yy = 0; yy < th; yy++)
                    for (int xx = 0; xx < tw; xx++) res[yy * tw + xx] = c.Luma[(py + yy) * c.W + px + xx] - pred[yy * tw + xx];
                int skc = Av1CoeffDecode.GetSkipCtx(in sTD, lumaBs, c.ALY.AsSpan(txR), c.LLY.AsSpan(tyR), 0, 0);
                int snc = Av1CoeffDecode.GetDcSignCtx(stx, c.ALY.AsSpan(txR), c.LLY.AsSpan(tyR));
                int[] bestCf = null!; Av1TxType bestInv = Av1TxType.DctDct; int bestIdx = 1; double bestJ = double.MaxValue, bestBits = 0;
                foreach (var (fwd, inv, idx) in txSet)
                {
                    int[] cf = Av1FwdTransform.ForwardQuantRect(res, tw, th, stx, c.DcDq, c.AcDq, sScan, fwd, qf);
                    bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                    long sse = ReconSseCandRect(cf, stx, tw, th, c.DcDq, c.AcDq, pred, c.Luma, c.W, px, py, inv);
                    if (sse >= bestJ) { Av1FwdTransform.ReturnLevels(cf); continue; }
                    double bits = oneD
                        ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, stx, yModeNoFilt, inv, cf, skc, snc)
                        : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, cf, skc, snc, idx, fullSet: UseFullIntraTxSet);
                    double j = sse + lambda * bits;
                    if (j < bestJ) { Av1FwdTransform.ReturnLevels(bestCf); bestJ = j; bestBits = bits; bestCf = cf; bestInv = inv; bestIdx = idx; Array.Copy(qf, qfBest, sScan); }
                    else Av1FwdTransform.ReturnLevels(cf);
                }
                if (bestInv != Av1TxType.VDct && bestInv != Av1TxType.HDct && HasNonZero(bestCf))
                {
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, bestCf, qfBest, c.DcDq, c.AcDq, skc, snc, bestIdx,
                        RdoqScale * lambda);
                    bestBits = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, bestCf, skc, snc, bestIdx, fullSet: UseFullIntraTxSet);
                }
                byte cfc = DequantAndReconstructPredRect(bestCf, stx, tw, th, c.DcDq, c.AcDq, pred, c.ReconY, c.W, px, py, bestInv);
                for (int yy = 0; yy < th; yy++)
                    for (int xx = 0; xx < tw; xx++) { int d = c.ReconY[(py + yy) * c.W + px + xx] - c.Luma[(py + yy) * c.W + px + xx]; sseSum += (long)d * d; }
                bitsSum += bestBits;
                list.Add((bestCf, bestInv, bestIdx, skc, snc, px, py));
                for (int i = 0; i < tw4 && txR + i < 32; i++) c.ALY[txR + i] = cfc;
                for (int j = 0; j < th4 && tyR + j < 32; j++) c.LLY[tyR + j] = cfc;
            }
        return (sseSum + lambda * bitsSum, list);
    }

    // Emits the luma coefficients of a split-depth leaf (RectLumaSplitTrial records) in tx-block order.
    private static void EmitSplitLuma(ColorPartCtx c, int stx, int yModeNoFilt,
        List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)> txb)
    {
        foreach (var t in txb)
        {
            if (t.Inv == Av1TxType.VDct || t.Inv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, stx, yModeNoFilt, t.Inv, t.Cf, skipCtx: t.SkipCtx, dcSignCtx: t.SignCtx);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, stx, 0, yModeNoFilt, t.Cf, skipCtx: t.SkipCtx, dcSignCtx: t.SignCtx,
                    txTypeIdx: t.Idx, fullSet: UseFullIntraTxSet);
        }
    }

    // Luma block SSE (reconstruction vs source) over an n x n region at pixel (bx,by).
    private static long LumaBlockSse(ColorPartCtx c, int bx, int by, int n)
    {
        long sse = 0;
        int nw = Math.Min(n, c.Bw4 * 4 - bx), nh = Math.Min(n, c.Bh4 * 4 - by);   // in-frame samples only
        for (int yy = 0; yy < nh; yy++)
            for (int xx = 0; xx < nw; xx++) { int d = c.ReconY[(by + yy) * c.W + bx + xx] - c.Luma[(by + yy) * c.W + bx + xx]; sse += (long)d * d; }
        return sse;
    }

    // Estimates the luma coding cost J = SSE + λ·bits of a single rectangular leaf, predicting from the SOURCE
    // plane and reconstructing through the decoder's inverse — the same methodology as EstimateBlockCost, so the
    // PARTITION_HORZ / PARTITION_VERT costs compare fairly against NONE / SPLIT.
    private static long EstimateRectCostColor(ColorPartCtx c, int lumaTx, int bx4, int by4, int w4, int h4)
    {
        int w = w4 * 4, h = h4 * 4;
        int lScan = Av1Tables.Scans[lumaTx].Length;
        var pred = new ushort[h * w];
        var bestPred = new ushort[h * w];
        int[] bestCf = null!;
        long bestBits = long.MaxValue;
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            PredictIntraRect(c.Luma, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred);
            int[] cf = ForwardResidualPredRect(c.Luma, c.W, bx4 * 4, by4 * 4, pred, w, h, lumaTx, c.DcDq, c.AcDq, lScan);
            long bits = CoeffCost(cf);
            if (bits < bestBits) { bestBits = bits; bestCf = cf; Array.Copy(pred, bestPred, h * w); }
        }

        var reconTmp = new ushort[h * w];
        DequantAndReconstructPredRect(bestCf, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, reconTmp, w, 0, 0);
        long sse = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = reconTmp[y * w + x] - c.Luma[(by4 * 4 + y) * c.W + (bx4 * 4 + x)];
                sse += (long)d * d;
            }

        double lambda = LamK * c.AcDq * c.AcDq;
        return sse + (long)(lambda * (bestBits + HeaderCostBits));
    }

    // DC intra prediction for a w x h block from reconstructed neighbours — matches the decoder's DcGenBoth/Top/
    // Left (Av1IntraPred), INCLUDING the non-square reciprocal-multiplier correction (0x5556 for 1:2, 0x3334 for
    // 1:4). Square DcPredict cannot be used for rect blocks (w+h isn't a power of two).
    // Per-thread tile window (luma 4-units) for multi-tile encodes: intra edges never cross a tile boundary and
    // top-right / bottom-left availability ends at the tile's right / bottom edge (dav1d prepare_intra_edges uses
    // ts->tiling.col_start/col_end/row_start/row_end). Inactive => the whole frame is one tile.
    [ThreadStatic] private static bool t_tileOn;
    [ThreadStatic] private static int t_tileX4, t_tileY4, t_tileEndX4, t_tileEndY4, t_tileLumaW, t_tileSsX, t_tileSsY;

    // The active tile's bounds in the 4-unit grid of the plane whose stride is reconW (chroma bounds are the luma
    // bounds shifted by the subsampling, as dav1d's col_start >> ss_hor).
    private static (int X0, int Y0, int X1, int Y1) TileBounds4(int reconW, int bw4, int bh4)
    {
        if (!t_tileOn) return (0, 0, bw4, bh4);
        bool luma = reconW == t_tileLumaW;
        int sx = luma ? 0 : t_tileSsX, sy = luma ? 0 : t_tileSsY;
        return (t_tileX4 >> sx, t_tileY4 >> sy, Math.Min(bw4, t_tileEndX4 >> sx), Math.Min(bh4, t_tileEndY4 >> sy));
    }

    private static void SetTileWindow(int x4, int y4, int endX4, int endY4, int lumaW, int ssX, int ssY)
    {
        t_tileOn = true; t_tileX4 = x4; t_tileY4 = y4; t_tileEndX4 = endX4; t_tileEndY4 = endY4;
        t_tileLumaW = lumaW; t_tileSsX = ssX; t_tileSsY = ssY;
    }

    private static void ClearTileWindow() => t_tileOn = false;

    // Chroma DC prediction exactly as the decoder forms it: the intra edges clipped to the plane's 4-unit extent (tile
    // end >> ss) and padded by replication — the encoder's reconstruction past the frame edge never feeds it.
    [ThreadStatic] private static ushort[]? t_dcScratch;
    private static int ChromaDc(ColorPartCtx c, ushort[] plane, int cbx, int cby, int w, int h)
    {
        var buf = t_dcScratch ??= new ushort[64 * 64];
        PredictIntraRect(plane, c.Cw, c.Bw4 >> c.SsX, c.Bh4 >> c.SsY, cbx >> 2, cby >> 2, w, h, Av1IntraPredMode.Dc, 0, buf);
        return buf[0];
    }

    private static int DcPredictRect(ushort[] recon, int reconW, int bx, int by, int w, int h)
    {
        var tb = TileBounds4(reconW, int.MaxValue, int.MaxValue);
        bool haveTop = by > tb.Y0 * 4, haveLeft = bx > tb.X0 * 4;
        if (haveTop && haveLeft)
        {
            int dc = (w + h) >> 1;
            for (int x = 0; x < w; x++) dc += recon[(by - 1) * reconW + bx + x];
            for (int y = 0; y < h; y++) dc += recon[(by + y) * reconW + bx - 1];
            dc >>= System.Numerics.BitOperations.TrailingZeroCount((uint)(w + h));
            if (w != h)
            {
                int mult = (w > h * 2 || h > w * 2) ? 0x3334 : 0x5556;
                dc = (int)(((uint)dc * (uint)mult) >> 16);
            }

            return dc;
        }

        if (haveTop)
        {
            int dc = w >> 1;
            for (int x = 0; x < w; x++) dc += recon[(by - 1) * reconW + bx + x];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)w);
        }

        if (haveLeft)
        {
            int dc = h >> 1;
            for (int y = 0; y < h; y++) dc += recon[(by + y) * reconW + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)h);
        }

        return PixMid;
    }

    private static void FillFlatRect(ushort[] recon, int reconW, int bx, int by, int w, int h, int value)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = (ushort)value;
    }

    // Estimated chroma coding cost (both planes, DC-predicted from the block mean) of an I420 chroma block of
    // cw x ch pixels at chroma pixel position (cbx,cby), coded with transform chromaTx. Used to make the 16x16
    // partition decision chroma-aware (a HORZ/VERT split codes two chroma blocks vs NONE's one).
    private static long ChromaCostDc(ColorPartCtx c, int cbx, int cby, int cw, int ch, int chromaTx)
    {
        int scan = Av1Tables.Scans[chromaTx].Length;
        long sU = 0, sV = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++) { sU += c.U[(cby + y) * c.Cw + cbx + x]; sV += c.V[(cby + y) * c.Cw + cbx + x]; }
        int n = cw * ch, dcU = (int)((sU + n / 2) / n), dcV = (int)((sV + n / 2) / n);
        return CoeffCost(ForwardResidualRectDc(c.U, c.Cw, cbx, cby, cw, ch, dcU, chromaTx, c.DcDq, c.AcDq, scan))
             + CoeffCost(ForwardResidualRectDc(c.V, c.Cw, cbx, cby, cw, ch, dcV, chromaTx, c.DcDq, c.AcDq, scan));
    }

    private static int[] ForwardResidualRectDc(ReadOnlySpan<ushort> plane, int planeW, int bx, int by, int w, int h,
        int dc, int txIdx, int dcDq, int acDq, int rcCount)
    {
        var residual = Av1FwdTransform.RentLevels(h * w);   // fully written below
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) residual[y * w + x] = plane[(by + y) * planeW + (bx + x)] - dc;
        var lv = Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, Av1FwdTransform.FwdTxType.DctDct);
        Av1FwdTransform.ReturnLevels(residual);
        return lv;
    }

    // Reconstructs a rect w x h block on top of a flat DC prediction, via the decoder's InvTxfmAdd.
    private static byte DequantAndReconstructRectDc(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        int dc, ushort[] recon, int reconW, int bx, int by)
    {
        var pred = new ushort[h * w];
        Array.Fill(pred, (ushort)Math.Clamp(dc, 0, PixMax));
        return DequantAndReconstructPredRect(levels, txIdx, w, h, dcDq, acDq, pred, recon, reconW, bx, by);
    }

    // Codes one rectangular luma leaf (PARTITION_HORZ/VERT half) plus its I420 chroma: skip, Y mode (+angle),
    // UV mode (DC), then Y/U/V coefficients (rect transforms), and reconstructs all three planes. Chroma is DC-
    // predicted (no CfL for rect yet). Mirrors EncodeLeafBlockColor for a w4 x h4 (in 4-units) rectangle.
    private static void EncodeRectLeafColor(ColorPartCtx c, int lumaBs, int lumaTx, int chromaTx,
        int bx4, int by4, int w4, int h4, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int chromaRef = 0)
    {
        // Layout-generic chroma geometry: (w >> SsX) x (h >> SsY); for 4:2:0 these are exactly the old w/2, h/2.
        // chromaRef (4:2:0 16x4 / 4x16 strips, one 4-unit thin): -1 = no chroma (coded like a mono leaf), +1 = the
        // odd strip that codes the chroma shared with the previous strip (8x4 / 4x8 over the 16x8 / 8x16 pair).
        int ssX = c.SsX, ssY = c.SsY;
        if (c.Layout != Av1PixelLayout.I420) chromaTx = Av1Tables.MaxTxfmSizeForBlockSize[lumaBs, (int)c.Layout];
        if (c.Mono) chromaTx = lumaTx;   // unused (no chroma), keeps the geometry lookups valid
        int w = w4 * 4, h = h4 * 4, cw = w >> ssX, ch = h >> ssY;
        int bx = bx4 * 4, by = by4 * 4, cbx = bx >> ssX, cby = by >> ssY;
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> ssX, cyR = byR >> ssY;
        int cw4 = Math.Max(1, w4 >> ssX), ch4 = Math.Max(1, h4 >> ssY);
        if (chromaRef > 0)
        {
            int ox4 = bx4 & ~ssX, oy4 = by4 & ~ssY;
            cw = (Math.Max(w4, 1 << ssX) * 4) >> ssX; ch = (Math.Max(h4, 1 << ssY) * 4) >> ssY;
            cbx = (ox4 * 4) >> ssX; cby = (oy4 * 4) >> ssY; cxR = (ox4 & 31) >> ssX; cyR = (oy4 & 31) >> ssY;
            cw4 = cw >> 2; ch4 = ch >> 2;
        }
        ref readonly var cTDim = ref Av1Tables.TxfmDimensions[chromaTx];
        int lScan = Av1Tables.Scans[lumaTx].Length, cScan = Av1Tables.Scans[chromaTx].Length;
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);

        // Luma: rate-based mode search (safe modes, DCT_DCT rect transform), writing the best prediction.
        int aboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]];
        int leftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var ymCdf = c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        // per-thread scratch (the leaf is not re-entrant; only the first h * w / lScan entries are used)
        var scr = t_scRect ??= new LeafScratch();
        var pred = scr.P1; var bestPred = scr.P2; var resBuf = scr.R;
        var qfCand = scr.Q1; var qfWin = scr.Q2;
        int[] yC = null!;
        Av1IntraPredMode yMode = Av1IntraPredMode.Dc; int yDelta = 0;
        Av1TxType yInv = Av1TxType.DctDct; int yTxIdx = 1;
        double best = double.MaxValue;
        double rectLambda = LamK * c.AcDq * c.AcDq;   // true RD: D + λ·rate (rect edges want IDTX; DctDct-only misranks)
        // The tx-type symbol is coded for rect luma only when max tx dim <= 16 (16x8/8x16); 32x16/16x32 force DctDct.
        bool rectSymbolCoded = Av1Tables.TxfmDimensions[lumaTx].Max <= (byte)Av1TxSize.Tx16x16;
        // The full intra set (with V_DCT/H_DCT) exists only while the min tx dim < 16 (square 16x16 uses the reduced set).
        bool fullHere = UseFullIntraTxSet && Av1Tables.TxfmDimensions[lumaTx].Min < (byte)Av1TxSize.Tx16x16;
        var txSet = (fullHere && rectSymbolCoded) ? IntraTxTypesFull
                  : (rectSymbolCoded ? IntraTxTypes : DctOnly);
        int intraFlags = IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]);
        // angle_delta is coded only from 8x8 up (bw4 + bh4 log2 >= 2); filter intra for DC blocks up to 32x32.
        bool angleOk = Av1Tables.BlockDimensions[lumaBs, 2] + Av1Tables.BlockDimensions[lumaBs, 3] >= 2;
        bool fiOk = UseFilterIntra && Math.Max(Av1Tables.BlockDimensions[lumaBs, 2], Av1Tables.BlockDimensions[lumaBs, 3]) <= 3;
        var fiCdf = c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs);

        // libaom's slow-speed luma search (Av1StillImageEncoder.LumaSearch.cs): modes x tx sizes x tx types jointly;
        // it replaces the prescreen / RD / filter-intra / RDOQ / depth steps below.
        bool aom = Sp.LibaomLuma && (w <= 32 && h <= 32 || Sp.AomLuma64) && bx4 + w4 <= c.Bw4 && by4 + h4 <= c.Bh4;
        LumaPick aomPick = default;
        if (aom)
        {
            aomPick = AomLumaSearch(c, lumaBs, lumaTx, bx4, by4, w4, h4, edgeFlags, intraFlags, angleOk, fiOk);
            if (aomPick.Txb == null) { c.PartAborted = true; return; }   // over the partition budget (rd_try_subblock)
            yMode = aomPick.Mode; yDelta = aomPick.Delta; best = aomPick.J;
            yC = aomPick.Txb![0].Cf; yInv = aomPick.Txb[0].Inv; yTxIdx = aomPick.Txb[0].Idx;
        }

        // Prescreen modes by cheap SATD (as the square leaf does) and RD-evaluate only the best few — the full
        // tx-type search is the hot loop; SATD tracks coded cost closely enough that the top handful holds the winner.
        Span<int> topIdx = stackalloc int[RdModeCandidates];
        Span<long> topCost = stackalloc long[RdModeCandidates];
        topCost.Fill(long.MaxValue);
        double satdLambda = Math.Sqrt(LamK) * c.AcDq;
        uint hogMask = HogSkipMask(c.Luma, c.W, bx, by, w, h, c.Bw4 * 4, c.Bh4 * 4, Sp.HogLevel);
        int refine = Sp.AngleRefineTop;
        Span<long> baseCost = stackalloc long[16];
        baseCost.Fill(long.MaxValue);
        for (int pass = 0; pass < (aom ? 0 : refine > 0 ? 2 : 1); pass++)
        {
            uint dirMask = pass == 1 ? TopDirectional(baseCost, refine) : 0;
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
                if (refine > 0 && (pass == 0 ? delta != 0 : delta == 0 || (dirMask >> (int)mode & 1) == 0)) continue;
                if (delta != 0 && !angleOk) continue;
                if ((hogMask >> (int)mode & 1) != 0) continue;
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred, edgeFlags, intraFlags);
                long satd = 0;
                if (w >= 8 && h >= 8) satd = Satd8x8Rect(c.Luma, c.W, bx, by, pred, w, h);
                else   // sub-8x8 (4:4:4 sub-block leaves): SAD
                    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) satd += Math.Abs(c.Luma[(by + yy) * c.W + bx + xx] - pred[yy * w + xx]);
                long mb = (long)(satdLambda * (Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                    + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0)));
                long cost = satd + mb;
                if (delta == 0) baseCost[(int)mode] = cost;
                for (int k = 0; k < RdModeCandidates; k++)
                    if (cost < topCost[k]) { for (int j = RdModeCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = cost; topIdx[k] = ci; break; }
            }
        }

        bool fastTx = Sp.FastIntraTxType;
        for (int pass = 0; pass < (aom ? 0 : fastTx ? 2 : 1); pass++)
        for (int t = 0; t < RdModeCandidates; t++)
        {
            if (topCost[t] == long.MaxValue) break;
            (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
            if (pass == 1 && (mode != yMode || delta != yDelta || yC == null)) continue;
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, delta, pred, edgeFlags, intraFlags);
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++) resBuf[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx];
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) && angleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0)
                + (fiOk && mode == Av1IntraPredMode.Dc ? Av1CoeffEncode.SymBits(fiCdf, 0) : 0);
            foreach (var (fwd, inv, idx) in txSet)
            {
                if (fastTx && (pass == 0) != (inv == Av1TxType.DctDct)) continue;
                int[] cf = Av1FwdTransform.ForwardQuantRect(resBuf, w, h, lumaTx, c.DcDq, c.AcDq, lScan, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                long sse = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv);
                // Distortion alone already loses (rate >= 0): skip the rate estimate (exact).
                if (!Sp.RdoqInSearch && sse + rectLambda * modeBits >= best) { Av1FwdTransform.ReturnLevels(cf); continue; }
                double rate = (oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)mode, inv, cf, 0, ySign)
                    : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet)) + modeBits;
                double j = sse + rectLambda * rate;
                if (Sp.RdoqInSearch && !oneD && j < best * Sp.RdoqSearchMargin)
                {
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, qfCand, c.DcDq, c.AcDq, 0, ySign, idx,
                        RdoqScale * LamK * c.AcDq * c.AcDq);
                    rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet) + modeBits;
                    j = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv) + rectLambda * rate;
                }
                if (j < best) { Av1FwdTransform.ReturnLevels(yC); best = j; yC = cf; yMode = mode; yDelta = delta; yInv = inv; yTxIdx = idx; Array.Copy(pred, bestPred, h * w); Array.Copy(qfCand, qfWin, lScan); }
                else Av1FwdTransform.ReturnLevels(cf);
            }
        }

        // Filter intra: the 5 recursive-filter predictors, coded as y_mode=DC + use_filter_intra + filter_mode (the
        // tx-type context is FilterModeToYMode; the neighbour mode context stays DC).
        if (fiOk && !aom)
        {
            double flagBits = Av1CoeffEncode.SymBits(fiCdf, 1) + Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc);
            int fiMask = Sp.FilterIntraPrune ? FilterIntraModesFor(yMode) : 0x1F;
            for (int fm = 0; fm < 5; fm++)
            {
                if ((fiMask >> fm & 1) == 0) continue;
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, Av1IntraPredMode.Filter, fm, pred, edgeFlags, intraFlags);
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++) resBuf[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx];
                int ymnf = Av1Tables.FilterModeToYMode[fm];
                double modeBits = flagBits + Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraModeCdf(), fm);
                foreach (var (fwd, inv, idx) in txSet)
                {
                    int[] cf = Av1FwdTransform.ForwardQuantRect(resBuf, w, h, lumaTx, c.DcDq, c.AcDq, lScan, fwd, qfCand);
                    bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                    long sse = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv);
                    if (!Sp.RdoqInSearch && sse + rectLambda * modeBits >= best) { Av1FwdTransform.ReturnLevels(cf); continue; }
                    double rate = (oneD
                        ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, ymnf, inv, cf, 0, ySign)
                        : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, ymnf, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet)) + modeBits;
                    double j = sse + rectLambda * rate;
                    if (Sp.RdoqInSearch && !oneD && j < best * Sp.RdoqSearchMargin)
                    {
                        Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, ymnf, cf, qfCand, c.DcDq, c.AcDq, 0, ySign, idx,
                            RdoqScale * LamK * c.AcDq * c.AcDq);
                        rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, ymnf, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet) + modeBits;
                        j = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv) + rectLambda * rate;
                    }
                    if (j < best) { Av1FwdTransform.ReturnLevels(yC); best = j; yC = cf; yMode = Av1IntraPredMode.Filter; yDelta = fm; yInv = inv; yTxIdx = idx; Array.Copy(pred, bestPred, h * w); Array.Copy(qfCand, qfWin, lScan); }
                    else Av1FwdTransform.ReturnLevels(cf);
                }
            }
        }

        // Luma palette (screen content), as in the square leaf.
        LumaPal? yPal = null;
        bool palOk = c.ScreenContent && chromaRef == 0 && Math.Max(w4, h4) <= 16 && w4 + h4 >= 4 && bx4 + w4 <= c.Bw4 && by4 + h4 <= c.Bh4;
        if (palOk)
        {
            int pSzC = Av1Tables.BlockDimensions[lumaBs, 2] + Av1Tables.BlockDimensions[lumaBs, 3] - 2;
            int pCtxC = (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0);
            double nonPalJ = best + (yMode is Av1IntraPredMode.Dc or Av1IntraPredMode.Filter ? rectLambda * Av1CoeffEncode.BoolBits(c.Cdf.GetPalYCdf(pSzC, pCtxC)[0], 0) : 0);
            var pal = SearchLumaPalette(c, lumaBs, lumaTx, bx4, by4, w, h, ymCdf, nonPalJ);
            if (pal != null && pal.J < nonPalJ)
            {
                yPal = pal; yMode = Av1IntraPredMode.Dc; yDelta = 0;
                yC = pal.Coeffs; yInv = pal.Inv; yTxIdx = pal.Idx;
                Array.Copy(pal.Pred, bestPred, w * h);
            }
        }

        // The coded y_mode symbol and neighbour context are DC for a filter block; its tx-type context is the
        // filter's FilterModeToYMode.
        bool isFilter = yMode == Av1IntraPredMode.Filter;
        int yModeSym = isFilter ? (int)Av1IntraPredMode.Dc : (int)yMode;
        int yModeNoFilt = isFilter ? Av1Tables.FilterModeToYMode[yDelta] : (int)yMode;

        // RDOQ-refine the winning luma coefficients (skipped for V_DCT/H_DCT: RdoqOptimize assumes the 2D scan).
        if (!aom && yPal == null && yInv != Av1TxType.VDct && yInv != Av1TxType.HDct && !Sp.RdoqInSearch)
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, yC, qfWin, c.DcDq, c.AcDq, 0, ySign, yTxIdx,
                RdoqScale * LamK * c.AcDq * c.AcDq);

        // tx_depth 1 (RectTxDepth): the winning mode re-coded on the split transform, each tx block predicted from the
        // reconstruction in the decoder's raster order with its own tx type; kept when its true RD beats the whole tx.
        ref readonly var fullTD = ref Av1Tables.TxfmDimensions[lumaTx];
        int depth = 0;
        List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? txb = null;
        if (!aom && Sp.RectTxDepth && UseColorTxDepth && yPal == null && fullTD.Max > (byte)Av1TxSize.Tx4x4 && w <= 32 && h <= 32
            && bx4 + w4 <= c.Bw4 && by4 + h4 <= c.Bh4 && HasNonZero(yC))
        {
            int txCtxD = (c.LTxY[byR] >= fullTD.Lh ? 1 : 0) + (c.ATxY[bxR] >= fullTD.Lw ? 1 : 0);
            var txSzCdfD = c.Cdf.GetTxSzCdf(fullTD.Max - 1, txCtxD);
            double j0 = ReconSseCandRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.Luma, c.W, bx, by, yInv)
                + rectLambda * ((yInv == Av1TxType.VDct || yInv == Av1TxType.HDct
                    ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, yModeNoFilt, yInv, yC, 0, ySign)
                    : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, yC, 0, ySign, yTxIdx, fullSet: UseFullIntraTxSet))
                  + Av1CoeffEncode.SymBits(txSzCdfD, 0));
            var saveA = c.ALY.AsSpan(bxR, Math.Min(w4, 32 - bxR)).ToArray();
            var saveL = c.LLY.AsSpan(byR, Math.Min(h4, 32 - byR)).ToArray();
            // Mode bits relative to the depth-0 winner's (joint trials of the next prescreened modes, RectTxDepthAlt).
            int ymA = Av1Tables.IntraModeContext[c.AModeY[bxR]], ymL = Av1Tables.IntraModeContext[c.LModeY[byR]];
            double ModeBitsOf(Av1IntraPredMode m, int dl) => Av1CoeffEncode.SymBits(c.Cdf.GetKfYModeCdf(ymA, ymL), (int)m)
                + (IsDirectional(m) && angleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)m - (int)Av1IntraPredMode.Vertical), dl + 3) : 0)
                + (fiOk && m == Av1IntraPredMode.Dc ? Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs), 0) : 0);
            double winBits = isFilter
                ? Av1CoeffEncode.SymBits(fiCdf, 1) + Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc) + Av1CoeffEncode.SymBits(c.Cdf.GetFilterIntraModeCdf(), yDelta)
                : ModeBitsOf(yMode, yDelta);
            double tsz1 = rectLambda * Av1CoeffEncode.SymBits(txSzCdfD, 1);
            double bestD = j0; (Av1IntraPredMode M, int D, int Nf)? win = null;
            List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? winList = null;
            bool lastIsWin = false;
            for (int k = -1; k < RdModeCandidates && k < Sp.RectTxDepthAlt; k++)
            {
                Av1IntraPredMode m = yMode; int dl = yDelta, nf = yModeNoFilt; double extra = 0;
                if (k >= 0)
                {
                    if (topCost[k] == long.MaxValue) break;
                    (m, dl) = CandidateModes[topIdx[k]];
                    if (m == yMode && dl == yDelta && !isFilter) continue;
                    nf = (int)m; extra = rectLambda * (ModeBitsOf(m, dl) - winBits);
                }
                var (j1, list) = RectLumaSplitTrial(c, lumaBs, lumaTx, bx4, by4, w4, h4, m, dl, nf, edgeFlags, intraFlags);
                j1 += tsz1 + extra;
                saveA.CopyTo(c.ALY.AsSpan(bxR)); saveL.CopyTo(c.LLY.AsSpan(byR));
                if (j1 < bestD)
                {
                    if (winList != null) foreach (var t in winList) Av1FwdTransform.ReturnLevels(t.Cf);
                    bestD = j1; win = (m, dl, nf); winList = list; lastIsWin = true;
                }
                else { foreach (var t in list) Av1FwdTransform.ReturnLevels(t.Cf); lastIsWin = false; }
            }
            if (win is var (wm, wd, wnf))
            {
                depth = 1;
                if (lastIsWin)
                {
                    // ReconY holds the winner's reconstruction: only its tx coefficient contexts need re-applying
                    foreach (var t in winList!)
                    {
                        int tR = (t.Px >> 2) & 31, lR = (t.Py >> 2) & 31;
                        ref readonly var sTD = ref Av1Tables.TxfmDimensions[fullTD.Sub];
                        byte cfc = TxCoefCtx(t.Cf, fullTD.Sub);
                        for (int i = 0; i < sTD.W && tR + i < 32; i++) c.ALY[tR + i] = cfc;
                        for (int j = 0; j < sTD.H && lR + j < 32; j++) c.LLY[lR + j] = cfc;
                    }
                }
                else
                {
                    // re-run the winner so ReconY / ALY / LLY hold its reconstruction (deterministic: same records)
                    foreach (var t in winList!) Av1FwdTransform.ReturnLevels(t.Cf);
                    winList = RectLumaSplitTrial(c, lumaBs, lumaTx, bx4, by4, w4, h4, wm, wd, wnf, edgeFlags, intraFlags).Txb;
                }
                txb = winList;
                if (wnf != yModeNoFilt || wd != yDelta || wm != yMode)
                {
                    // an alternative (non-filter) mode won on the split transform: it becomes the leaf's mode
                    yMode = wm; yDelta = wd; isFilter = false; yModeSym = (int)wm; yModeNoFilt = (int)wm;
                }
            }
        }
        if (aom && yPal == null) { depth = aomPick.Depth; txb = aomPick.Txb; }
        else if (aom && aomPick.Txb != null) foreach (var t in aomPick.Txb) Av1FwdTransform.ReturnLevels(t.Cf);
        int emitTx = txb == null ? lumaTx : aom ? aomPick.Tx : fullTD.Sub;
        bool lumaAllZero = txb == null ? !HasNonZero(yC) : txb.TrueForAll(t => !HasNonZero(t.Cf));

        // Reconstruct luma into ReconY now — CfL chroma prediction reads it (encoder-internal; independent of the
        // symbol emission order below, which the decoder does luma-then-chroma too). A split depth already did.
        byte cfY = txb == null ? DequantAndReconstructPredRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.ReconY, c.W, bx, by, yInv) : (byte)0x40;

        if (c.Mono || chromaRef < 0)
        {
            // Monochrome (or a 4:2:0 strip without chroma): the colour syntax minus uv_mode / CfL / chroma coefficients.
            int mSkip = lumaAllZero ? 1 : 0;
            c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(c.ASkip[bxR] + c.LSkip[byR]), (uint)mSkip);
            if (mSkip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
            c.Msac.EncodeSymbolAdapt(ymCdf, yModeSym, 12);
            if (IsDirectional(yMode) && angleOk)
                c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
            EmitPaletteFlags(c, lumaBs, bx4, by4, w4, h4, yModeSym == (int)Av1IntraPredMode.Dc, uvDc: false, yPal);
            if (fiOk && yPal == null && yModeSym == (int)Av1IntraPredMode.Dc)
            {
                c.Msac.EncodeBoolAdapt(fiCdf, isFilter ? 1u : 0u);
                if (isFilter) c.Msac.EncodeSymbolAdapt(c.Cdf.GetFilterIntraModeCdf(), yDelta, 4);
            }
            if (yPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, yPal.Map, yPal.Size, w, h, w4, h4, isLuma: true);
            ref readonly var mTDim = ref Av1Tables.TxfmDimensions[lumaTx];
            if (UseColorTxDepth && mTDim.Max > (byte)Av1TxSize.Tx4x4)
            {
                int txCtx = (c.LTxY[byR] >= mTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= mTDim.Lw ? 1 : 0);
                c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(mTDim.Max - 1, txCtx), depth, Math.Min((int)mTDim.Max, 2));
            }
            if (mSkip == 0 && txb != null) EmitSplitLuma(c, emitTx, yModeNoFilt, txb);
            else if (mSkip == 0)
            {
                if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                    Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, yModeNoFilt, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
                else
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            }
            int mW = Math.Min(w4, c.Bw4 - bx4), mH = Math.Min(h4, c.Bh4 - by4);
            sbyte mLw = (sbyte)Av1Tables.TxfmDimensions[emitTx].Lw, mLh = (sbyte)Av1Tables.TxfmDimensions[emitTx].Lh;
            for (int i = 0; i < mW && bxR + i < 32; i++) { if (txb == null) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yModeSym; c.ASkip[bxR + i] = (byte)mSkip; c.ATxY[bxR + i] = mLw; }
            for (int j = 0; j < mH && byR + j < 32; j++) { if (txb == null) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yModeSym; c.LSkip[byR + j] = (byte)mSkip; c.LTxY[byR + j] = mLh; }
            FillPaletteCtx(c, bx4, by4, mW, mH, yPal, null);
            return;
        }

        // Chroma larger than its max transform (64x64 leaves outside 4:2:0): several chroma tx blocks, each predicted
        // from its own edges in the decoder's order (see RectLeafChromaMultiTx).
        if (cw > cTDim.W * 4 || ch > cTDim.H * 4)
        {
            RectLeafChromaMultiTx(c, lumaBs, lumaTx, chromaTx, bx4, by4, w4, h4, edgeFlags, ymCdf,
                yC, yMode, yDelta, yInv, yTxIdx, ySign, cfY, txb, emitTx, depth);
            return;
        }

        int dcU = ChromaDc(c, c.ReconU, cbx, cby, cw, ch);
        int dcV = ChromaDc(c, c.ReconV, cbx, cby, cw, ch);
        int[] uC = ForwardResidualRectDc(c.U, c.Cw, cbx, cby, cw, ch, dcU, chromaTx, c.DcDq, c.AcDq, cScan);
        int[] vC = ForwardResidualRectDc(c.V, c.Cw, cbx, cby, cw, ch, dcV, chromaTx, c.DcDq, c.AcDq, cScan);
        double clam0 = LamK * c.AcDq * c.AcDq;
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, yModeSym);
        var dcuP = new ushort[cw * ch]; Array.Fill(dcuP, (ushort)Math.Clamp(dcU, 0, PixMax));
        var dcvP = new ushort[cw * ch]; Array.Fill(dcvP, (ushort)Math.Clamp(dcV, 0, PixMax));

        int eUSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)c.Layout);
        int eVSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)c.Layout);
        int eUSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
        int eVSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
        bool exactC = Sp.ChromaRateExact;
        var eCoef = c.Cdf.Coef; var eMode = c.Cdf.Mode; var eCdf = c.Cdf; int eTx = chromaTx;
        double CRate(int[] cu, int[] cv) => exactC
            ? Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, eTx, 1, 0, cu, eUSkip, eUSign, 0) + Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, eTx, 1, 0, cv, eVSkip, eVSign, 0)
            : CoeffCost(cu) + CoeffCost(cv);
        double CflRate(int aU, int aV) => exactC ? CflAlphaBits(eCdf, aU, aV) : (aU != 0 ? 5 : 0) + (aV != 0 ? 5 : 0);
        // libaom av1_txfm_uvrd (AomChroma): a chroma candidate is priced on its trellised levels (the chroma trellis
        // lambda) with their exact rate and the reconstruction SSE, per plane.
        bool aomC = Sp.AomChroma && UseRdoq;
        double clamT = ChromaLamScale * LamK * c.AcDq * c.AcDq;
        double PlaneJ(ushort[] src, ushort[] pred, Av1TxType t, int skc, int sgc, out int[] lv)
        {
            var qf = new double[cScan];
            lv = ForwardResidualPredRect(src, c.Cw, cbx, cby, pred, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, qf, FwdTypeForTxType(t));
            double bits = Av1CoeffEncode.TrellisOptimize(eCoef, chromaTx, 1, lv, qf, c.DcDq, c.AcDq, skc, sgc, clamT);
            return ReconSseCandRect(lv, chromaTx, cw, ch, c.DcDq, c.AcDq, pred, src, c.Cw, cbx, cby, t) + clam0 * bits;
        }

        int uvMode = 0, uvDelta = 0; bool useCfl = false;
        ushort[] predU = dcuP, predV = dcvP; int alphaU = 0, alphaV = 0;
        double bestJ;
        if (aomC)
        {
            Av1FwdTransform.ReturnLevels(uC); Av1FwdTransform.ReturnLevels(vC);
            bestJ = PlaneJ(c.U, dcuP, Av1TxType.DctDct, eUSkip, eUSign, out uC) + PlaneJ(c.V, dcvP, Av1TxType.DctDct, eVSkip, eVSign, out vC)
                  + clam0 * Av1CoeffEncode.SymBits(uvModeCdf, 0);
        }
        else bestJ = ReconSseCandRect(uC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcuP, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                     + ReconSseCandRect(vC, chromaTx, cw, ch, c.DcDq, c.AcDq, dcvP, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                     + clam0 * (CRate(uC, vC) + Av1CoeffEncode.SymBits(uvModeCdf, 0));

        // Directional/Smooth/Paeth UV chroma modes (see EncodeLeafBlockColor): rect chroma is a single tx block too,
        // so the same edge derivation + UV-mode-derived tx-type (TxTypeFromUvMode) applies.
        if (UseUvModeSearch)
        {
            // The layout's own availability bits (dav1d: I420TopHasRight >> (layout - 1)), passed as I444 bits.
            int lsh = (int)c.Layout - 1;
            var chromaEdge =
                ((edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> lsh)) != 0 ? Av1EdgeFlags.I444TopHasRight : 0) |
                ((edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> lsh)) != 0 ? Av1EdgeFlags.I444LeftHasBottom : 0);
            int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
            int cbw4 = c.Bw4 >> ssX, cbh4 = c.Bh4 >> ssY, cbx4 = bx4 >> ssX, cby4 = by4 >> ssY;
            int bDimW = Av1Tables.BlockDimensions[lumaBs, 2], bDimH = Av1Tables.BlockDimensions[lumaBs, 3];
            bool uvAngleOk = bDimW + bDimH >= 2;
            var pu = new ushort[cw * ch]; var pv = new ushort[cw * ch];
            Span<int> uvTopIdx = stackalloc int[RdUvCandidates + 1];
            Span<long> uvTopCost = stackalloc long[RdUvCandidates + 1];
            uvTopCost.Fill(long.MaxValue);
            uint hogUv = uvAngleOk ? HogSkipMask(c.U, c.Cw, cbx, cby, cw, ch, (c.Bw4 * 4) >> ssX, (c.Bh4 * 4) >> ssY, Sp.HogChromaLevel, (1 + ssX) * (1 + ssY)) : 0;
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc) continue;
                if (delta != 0 && !uvAngleOk) continue;
                if ((hogUv >> (int)mode & 1) != 0) continue;
                PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pu, chromaEdge, cIntraFlags);
                long sad = 0;
                for (int yy = 0; yy < ch; yy++) { int r = (cby + yy) * c.Cw + cbx; for (int xx = 0; xx < cw; xx++) sad += Math.Abs(c.U[r + xx] - pu[yy * cw + xx]); }
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < uvTopCost[k]) { for (int j = RdUvCandidates - 1; j > k; j--) { uvTopCost[j] = uvTopCost[j - 1]; uvTopIdx[j] = uvTopIdx[j - 1]; } uvTopCost[k] = sad; uvTopIdx[k] = ci; break; }
            }
            int uvN = AddUvLumaWinner(uvTopIdx, uvTopCost, yMode, yDelta, uvAngleOk);
            for (int t = 0; t < uvN; t++)
            {
                if (uvTopCost[t] == long.MaxValue) break;
                (Av1IntraPredMode mode, int delta) = CandidateModes[uvTopIdx[t]];
                PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pu, chromaEdge, cIntraFlags);
                PredictIntraRect(c.ReconV, c.Cw, cbw4, cbh4, cbx4, cby4, cw, ch, mode, delta, pv, chromaEdge, cIntraFlags);
                var uvTx = UvIntraTxType(chromaTx, (int)mode);
                var uvFwd = FwdTypeForTxType(uvTx);
                double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                    + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
                int[] uu, vv; double j;
                if (aomC)
                    j = PlaneJ(c.U, pu, uvTx, eUSkip, eUSign, out uu) + PlaneJ(c.V, pv, uvTx, eVSkip, eVSign, out vv) + clam0 * modeBits;
                else
                {
                    uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pu, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                    vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pv, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                    j = ReconSseCandRect(uu, chromaTx, cw, ch, c.DcDq, c.AcDq, pu, c.U, c.Cw, cbx, cby, uvTx)
                      + ReconSseCandRect(vv, chromaTx, cw, ch, c.DcDq, c.AcDq, pv, c.V, c.Cw, cbx, cby, uvTx)
                      + clam0 * (CRate(uu, vv) + modeBits);
                }
                if (j < bestJ)
                {
                    bestJ = j; uvMode = (int)mode; uvDelta = delta; useCfl = false;
                    uC = uu; vC = vv;
                    (predU, pu) = (pu, predU != dcuP ? predU : new ushort[pu.Length]);
                    (predV, pv) = (pv, predV != dcvP ? predV : new ushort[pv.Length]);
                }
            }
        }

        // Chroma-from-luma vs the running best.
        if (cflAllowed && UseCfl)
        {
            var acc = new short[cw * ch];
            if (c.Layout == Av1PixelLayout.I420) ComputeCflAcEncRect(c.ReconY, c.W, cbx * 2, cby * 2, cw, ch, acc);
            else CflAcAnyLayout(c, lumaBs, lumaTx, bx4, by4, acc);
            int aU = BestCflAlphaRect(c.U, c.Cw, cbx, cby, cw, ch, dcU, acc);
            int aV = BestCflAlphaRect(c.V, c.Cw, cbx, cby, cw, ch, dcV, acc);
            if (aomC)
            {
                // cfl_rd_pick_alpha: each plane RD-priced at the alphas around its estimate, the best pair (not both
                // zero) with the joint alpha signalling
                int R = Math.Max(0, Sp.AomCflRange);
                double[] ju = new double[33], jv = new double[33];
                int[][] lu = new int[33][], lvv = new int[33][];
                ushort[][] pu2 = new ushort[33][], pv2 = new ushort[33][];
                for (int k = 0; k < 33; k++) { ju[k] = jv[k] = double.MaxValue; }
                for (int a = Math.Max(-16, aU - R); a <= Math.Min(16, aU + R); a++)
                { pu2[a + 16] = BuildCflPredRect(dcU, acc, cw, ch, a); ju[a + 16] = PlaneJ(c.U, pu2[a + 16], Av1TxType.DctDct, eUSkip, eUSign, out lu[a + 16]); }
                for (int a = Math.Max(-16, aV - R); a <= Math.Min(16, aV + R); a++)
                { pv2[a + 16] = BuildCflPredRect(dcV, acc, cw, ch, a); jv[a + 16] = PlaneJ(c.V, pv2[a + 16], Av1TxType.DctDct, eVSkip, eVSign, out lvv[a + 16]); }
                double cflBest = double.MaxValue; int bu = 0, bv = 0;
                double cflMode = Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma);
                for (int x1 = 0; x1 < 33; x1++)
                {
                    if (ju[x1] == double.MaxValue) continue;
                    for (int x2 = 0; x2 < 33; x2++)
                    {
                        if (jv[x2] == double.MaxValue || (x1 == 16 && x2 == 16)) continue;
                        double jj = ju[x1] + jv[x2] + clam0 * (cflMode + CflAlphaBits(eCdf, x1 - 16, x2 - 16));
                        if (jj < cflBest) { cflBest = jj; bu = x1; bv = x2; }
                    }
                }
                if (cflBest < bestJ)
                {
                    bestJ = cflBest; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = bu - 16; alphaV = bv - 16; uC = lu[bu]; vC = lvv[bv]; predU = pu2[bu]; predV = pv2[bv];
                }
            }
            else if (aU != 0 || aV != 0)
            {
                var pcflU = BuildCflPredRect(dcU, acc, cw, ch, aU);
                var pcflV = BuildCflPredRect(dcV, acc, cw, ch, aV);
                int[] uCc = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pcflU, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                int[] vCc = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pcflV, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                double cflJ = ReconSseCandRect(uCc, chromaTx, cw, ch, c.DcDq, c.AcDq, pcflU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + ReconSseCandRect(vCc, chromaTx, cw, ch, c.DcDq, c.AcDq, pcflV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                            + clam0 * (CRate(uCc, vCc) + Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma) + CflRate(aU, aV));
                if (cflJ < bestJ)
                {
                    bestJ = cflJ; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                    alphaU = aU; alphaV = aV; uC = uCc; vC = vCc; predU = pcflU; predV = pcflV;
                }
            }
        }

        // Chroma palette (screen content; uv_mode DC).
        UvPal? uvPal = null;
        if (palOk)
            foreach (var cp in UvPaletteCandidates(c, cbx, cby, cw, ch))
            {
                int[] uu, vv; double j;
                double palHdr = Av1CoeffEncode.SymBits(uvModeCdf, 0) + UvPaletteBits(c, cp, bx4, by4, lumaBs, yPal != null, cw, ch);
                if (aomC)
                    j = PlaneJ(c.U, cp.PredU, Av1TxType.DctDct, eUSkip, eUSign, out uu) + PlaneJ(c.V, cp.PredV, Av1TxType.DctDct, eVSkip, eVSign, out vv)
                      + clam0 * palHdr;
                else
                {
                    uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, cp.PredU, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                    vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, cp.PredV, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan);
                    j = ReconSseCandRect(uu, chromaTx, cw, ch, c.DcDq, c.AcDq, cp.PredU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                      + ReconSseCandRect(vv, chromaTx, cw, ch, c.DcDq, c.AcDq, cp.PredV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                      + clam0 * (CRate(uu, vv) + palHdr);
                }
                if (j < bestJ)
                {
                    bestJ = j; uvPal = cp; uvMode = 0; uvDelta = 0; useCfl = false;
                    uC = uu; vC = vv; predU = cp.PredU; predV = cp.PredV;
                }
            }
        // RDOQ of the chroma winner (as the square 4:2:0 leaf): the pre-quant floats re-derived with its tx kernel.
        // Not for 4:2:0 rectangles: their chroma blocks are small, and there it cost time for a slight loss.
        if (!aomC && UseChromaRdoq && UseRdoq && uvPal == null && c.Layout != Av1PixelLayout.I420)
        {
            var uvT = useCfl ? Av1TxType.DctDct : UvIntraTxType(chromaTx, uvMode);
            if (uvT is Av1TxType.DctDct or Av1TxType.AdstDct or Av1TxType.DctAdst or Av1TxType.AdstAdst)
            {
                var fwdT = FwdTypeForTxType(uvT);
                double clamR = ChromaLamScale * LamK * c.AcDq * c.AcDq;
                var qfC = new double[cScan];
                if (HasNonZero(uC))
                {
                    Av1FwdTransform.ReturnLevels(ForwardResidualPredRect(c.U, c.Cw, cbx, cby, predU, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, qfC, fwdT));
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, qfC, c.DcDq, c.AcDq,
                        Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)c.Layout),
                        Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR)), 0, clamR);
                }
                if (HasNonZero(vC))
                {
                    Av1FwdTransform.ReturnLevels(ForwardResidualPredRect(c.V, c.Cw, cbx, cby, predV, cw, ch, chromaTx, c.DcDq, c.AcDq, cScan, qfC, fwdT));
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, qfC, c.DcDq, c.AcDq,
                        Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)c.Layout),
                        Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR)), 0, clamR);
                }
            }
        }
        int skip = (!lumaAllZero || HasNonZero(uC) || HasNonZero(vC)) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        if (skip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
        c.Msac.EncodeSymbolAdapt(ymCdf, yModeSym, 12);
        if (IsDirectional(yMode) && angleOk)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        int uvSym = useCfl ? (int)Av1IntraPredMode.ChromaFromLuma : uvMode;
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvSym, uvNsym);
        if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        else if (IsDirectional((Av1IntraPredMode)uvMode) &&
                 Av1Tables.BlockDimensions[lumaBs, 2] + Av1Tables.BlockDimensions[lumaBs, 3] >= 2)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);
        EmitPaletteFlags(c, lumaBs, bx4, by4, w4, h4, yModeSym == (int)Av1IntraPredMode.Dc, uvDc: !useCfl && uvMode == 0, yPal, uvPal);

        // filter_intra (DC-coded blocks up to 32x32 without a palette)
        if (fiOk && yPal == null && yModeSym == (int)Av1IntraPredMode.Dc)
        {
            c.Msac.EncodeBoolAdapt(fiCdf, isFilter ? 1u : 0u);
            if (isFilter) c.Msac.EncodeSymbolAdapt(c.Cdf.GetFilterIntraModeCdf(), yDelta, 4);
        }
        if (yPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, yPal.Map, yPal.Size, w, h, w4, h4, isLuma: true);
        if (uvPal != null) Av1CoeffEncode.EncodePaletteIndices(c.Msac, c.Cdf.Mode, uvPal.Map, uvPal.Size, cw, ch, cw >> 2, ch >> 2, isLuma: false);

        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), depth, Math.Min((int)lTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        if (skip == 0)
        {
            int uSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)c.Layout);
            int vSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)c.Layout);
            int uSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int vSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            if (txb != null) EmitSplitLuma(c, emitTx, yModeNoFilt, txb);
            else if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, yModeNoFilt, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, yModeNoFilt, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);
            var uvTxR = UvIntraTxType(chromaTx, uvSym);
            cfU = DequantAndReconstructPredRect(uC, chromaTx, cw, ch, c.DcDq, c.AcDq, predU, c.ReconU, c.Cw, cbx, cby, uvTxR);
            cfV = DequantAndReconstructPredRect(vC, chromaTx, cw, ch, c.DcDq, c.AcDq, predV, c.ReconV, c.Cw, cbx, cby, uvTxR);
        }
        else
        {
            for (int yy = 0; yy < ch; yy++) Array.Copy(predU, yy * cw, c.ReconU, (cby + yy) * c.Cw + cbx, cw);
            for (int yy = 0; yy < ch; yy++) Array.Copy(predV, yy * cw, c.ReconV, (cby + yy) * c.Cw + cbx, cw);
        }

        int yW = Math.Min(w4, c.Bw4 - bx4), yH = Math.Min(h4, c.Bh4 - by4);
        int cW = Math.Min(cw4, (c.Bw4 - bx4 + ssX) >> ssX), cH = Math.Min(ch4, (c.Bh4 - by4 + ssY) >> ssY);
        sbyte txLw = (sbyte)Av1Tables.TxfmDimensions[emitTx].Lw, txLh = (sbyte)Av1Tables.TxfmDimensions[emitTx].Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++) { if (txb == null) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yModeSym; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; }
        for (int j = 0; j < yH && byR + j < 32; j++) { if (txb == null) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yModeSym; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { c.ACU[cxR + i] = cfU; c.ACV[cxR + i] = cfV; c.AModeUv[cxR + i] = (byte)uvSym; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { c.LCU[cyR + j] = cfU; c.LCV[cyR + j] = cfV; c.LModeUv[cyR + j] = (byte)uvSym; }
        FillPaletteCtx(c, bx4, by4, yW, yH, yPal, uvPal);
    }

    // The has_palette flags the decoder reads (screen content, 8x8..64x64 extent; Y when y_mode is DC, UV when uv_mode
    // is DC), with the palettes' sizes and colours when chosen.
    private static void EmitPaletteFlags(ColorPartCtx c, int bs, int bx4, int by4, int w4, int h4, bool yDc, bool uvDc,
        LumaPal? yp = null, UvPal? uvp = null)
    {
        if (!c.ScreenContent || Math.Max(w4, h4) > 16 || w4 + h4 < 4) return;
        int szCtx = Av1Tables.BlockDimensions[bs, 2] + Av1Tables.BlockDimensions[bs, 3] - 2;
        int bxR = bx4 & 31, byR = by4 & 31;
        if (yDc)
        {
            c.Msac.EncodeBoolAdapt(c.Cdf.GetPalYCdf(szCtx, (c.APalSz[bxR] > 0 ? 1 : 0) + (c.LPalSz[byR] > 0 ? 1 : 0)), yp != null ? 1u : 0u);
            if (yp != null) EmitLumaPaletteColors(c, yp, bx4, by4, szCtx);
        }
        if (uvDc && !c.Mono)
        {
            c.Msac.EncodeBoolAdapt(c.Cdf.GetPalUvCdf(yp != null ? 1 : 0), uvp != null ? 1u : 0u);
            if (uvp != null) EmitUvPaletteColors(c, uvp, bx4, by4, szCtx);
        }
    }

    // Bit mask (by mode value) of the `top` directional modes (Vertical..VerticalLeft) with the lowest delta-0 prescreen cost.
    private static uint TopDirectional(ReadOnlySpan<long> baseCost, int top)
    {
        uint mask = 0;
        for (int k = 0; k < top; k++)
        {
            int bi = -1;
            for (int m = (int)Av1IntraPredMode.Vertical; m <= (int)Av1IntraPredMode.VerticalLeft; m++)
                if ((mask >> m & 1) == 0 && baseCost[m] != long.MaxValue && (bi < 0 || baseCost[m] < baseCost[bi])) bi = m;
            if (bi < 0) break;
            mask |= 1u << bi;
        }
        return mask;
    }

    // Intra chroma transform type: derived from the UV mode, except DCT_DCT once the chroma tx reaches 32 in either
    // dimension (dav1d decode_coefs: t_dim->max + intra >= TX_64X64).
    private static Av1TxType UvIntraTxType(int chromaTx, int uvMode)
        => Av1Tables.TxfmDimensions[chromaTx].Max >= (byte)Av1TxSize.Tx32x32
            ? Av1TxType.DctDct : (Av1TxType)Av1Tables.TxTypeFromUvMode[uvMode];

    // Chroma for a leaf whose chroma block exceeds its max transform (4:4:4 / 4:2:2 64x64: 64x64 / 32x64 chroma on
    // TX_32X32). Mirrors dav1d recon_b_intra: tx blocks in raster order (one 64x64 luma chunk), each predicted from
    // its own edges — earlier tx blocks of this leaf included — with the per-tx top-right/bottom-left rules, tx
    // blocks past the visible frame edge not coded, and per-tx coefficient contexts. Luma is already decided and
    // reconstructed by the caller; this picks the UV mode by exact per-tx RD, then emits the whole block.
    private static void RectLeafChromaMultiTx(ColorPartCtx c, int lumaBs, int lumaTx, int chromaTx, int bx4, int by4,
        int w4, int h4, Av1EdgeFlags edgeFlags, Span<ushort> ymCdf, int[] yC, Av1IntraPredMode yMode, int yDelta,
        Av1TxType yInv, int yTxIdx, int ySign, byte cfY,
        List<(int[] Cf, Av1TxType Inv, int Idx, int SkipCtx, int SignCtx, int Px, int Py)>? txb = null, int emitTx = -1, int depth = 0)
    {
        int ssX = c.SsX, ssY = c.SsY;
        ref readonly var ct = ref Av1Tables.TxfmDimensions[chromaTx];
        int ctW = ct.W, ctH = ct.H;
        int tw = ctW * 4, th = ctH * 4, cScan = Av1Tables.Scans[chromaTx].Length;
        int bxR = bx4 & 31, byR = by4 & 31, cxR = bxR >> ssX, cyR = byR >> ssY;
        int cbx4 = bx4 >> ssX, cby4 = by4 >> ssY, pbw4 = c.Bw4 >> ssX, pbh4 = c.Bh4 >> ssY;
        int vw4 = Math.Min(w4, c.Bw4 - bx4), vh4 = Math.Min(h4, c.Bh4 - by4);
        int cw4v = (vw4 + ssX) >> ssX, ch4v = (vh4 + ssY) >> ssY;
        int subCw4 = Math.Min(cw4v, 16 >> ssX), subCh4 = Math.Min(ch4v, 16 >> ssY);
        int lsh = (int)c.Layout - 1;
        bool sbHasTr = (16 >> ssX) < cw4v || (edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> lsh)) != 0;
        bool sbHasBl = (16 >> ssY) < ch4v || (edgeFlags & (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> lsh)) != 0;
        var txs = new List<(int X, int Y, Av1EdgeFlags Edge)>();
        for (int y = 0; y < subCh4; y += ctH)
            for (int x = 0; x < subCw4; x += ctW)
            {
                var e = (((y > 0 || !sbHasTr) && x + ctW >= subCw4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                        ((x > 0 || (!sbHasBl && y + ctH >= subCh4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                txs.Add((x, y, e));
            }
        int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
        int cpx = cbx4 * 4, cpy = cby4 * 4;
        int rw = Math.Min((w4 * 4) >> ssX, c.Cw - cpx), rh = Math.Min((h4 * 4) >> ssY, c.Chh - cpy);
        ushort[] saveU = CopyRegion(null, c.ReconU, c.Cw, cpx, cpy, rw, rh), saveV = CopyRegion(null, c.ReconV, c.Cw, cpx, cpy, rw, rh);
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;   // never at 64 — CfL is <=32x32
        var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode);
        int bDimW = Av1Tables.BlockDimensions[lumaBs, 2], bDimH = Av1Tables.BlockDimensions[lumaBs, 3];
        bool uvAngleOk = bDimW + bDimH >= 2;
        double lambda = LamK * c.AcDq * c.AcDq;
        var pred = new ushort[tw * th];

        // Codes one plane with (mode, delta) through every tx block, reconstructing in place; returns SSE + λ·bits.
        double RunPlane(ushort[] src, ushort[] recon, Av1IntraPredMode mode, int delta, List<(int[] Lv, byte Cf)> outLv)
        {
            var uvTx = UvIntraTxType(chromaTx, (int)mode);
            var fwd = FwdTypeForTxType(uvTx);
            double j = 0;
            foreach (var (x, y, e) in txs)
            {
                PredictIntraRect(recon, c.Cw, pbw4, pbh4, cbx4 + x, cby4 + y, tw, th, mode, delta, pred, e, cIntraFlags);
                int px = cpx + x * 4, py = cpy + y * 4;
                int[] lv = ForwardResidualPredRect(src, c.Cw, px, py, pred, tw, th, chromaTx, c.DcDq, c.AcDq, cScan, null, fwd);
                byte cf = DequantAndReconstructPredRect(lv, chromaTx, tw, th, c.DcDq, c.AcDq, pred, recon, c.Cw, px, py, uvTx);
                long sse = 0;
                for (int yy = 0; yy < th; yy++)
                    for (int xx = 0; xx < tw; xx++)
                    { int d = recon[(py + yy) * c.Cw + px + xx] - src[(py + yy) * c.Cw + px + xx]; sse += (long)d * d; }
                j += sse + lambda * CoeffCost(lv);
                outLv.Add((lv, cf));
            }
            return j;
        }

        // Candidates: DC plus the best few non-DC modes by SAD of their per-tx prediction off the pre-leaf recon.
        var cands = new List<(Av1IntraPredMode Mode, int Delta)> { (Av1IntraPredMode.Dc, 0) };
        if (UseUvModeSearch)
        {
            var topIdx = new int[RdUvCandidates];
            var topCost = new long[RdUvCandidates];
            Array.Fill(topCost, long.MaxValue);
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (mode == Av1IntraPredMode.Dc || (delta != 0 && !uvAngleOk)) continue;
                long sad = 0;
                foreach (var (x, y, e) in txs)
                {
                    PredictIntraRect(c.ReconU, c.Cw, pbw4, pbh4, cbx4 + x, cby4 + y, tw, th, mode, delta, pred, e, cIntraFlags);
                    int px = cpx + x * 4, py = cpy + y * 4;
                    for (int yy = 0; yy < th; yy++)
                        for (int xx = 0; xx < tw; xx++) sad += Math.Abs(c.U[(py + yy) * c.Cw + px + xx] - pred[yy * tw + xx]);
                }
                for (int k = 0; k < RdUvCandidates; k++)
                    if (sad < topCost[k]) { for (int q = RdUvCandidates - 1; q > k; q--) { topCost[q] = topCost[q - 1]; topIdx[q] = topIdx[q - 1]; } topCost[k] = sad; topIdx[k] = ci; break; }
            }
            for (int k = 0; k < RdUvCandidates && topCost[k] != long.MaxValue; k++) cands.Add(CandidateModes[topIdx[k]]);
        }

        double bestJ = double.MaxValue;
        (Av1IntraPredMode Mode, int Delta) best = cands[0];
        List<(int[] Lv, byte Cf)> bestU = null!, bestV = null!;
        ushort[] bestRU = null!, bestRV = null!;
        foreach (var (mode, delta) in cands)
        {
            PasteRegion(saveU, c.ReconU, c.Cw, cpx, cpy, rw, rh);
            PasteRegion(saveV, c.ReconV, c.Cw, cpx, cpy, rw, rh);
            var lu = new List<(int[] Lv, byte Cf)>(); var lvv = new List<(int[] Lv, byte Cf)>();
            double modeBits = Av1CoeffEncode.SymBits(uvModeCdf, (int)mode)
                + (IsDirectional(mode) && uvAngleOk ? Av1CoeffEncode.SymBits(c.Cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            double j = RunPlane(c.U, c.ReconU, mode, delta, lu) + RunPlane(c.V, c.ReconV, mode, delta, lvv) + lambda * modeBits;
            if (j < bestJ)
            {
                bestJ = j; best = (mode, delta); bestU = lu; bestV = lvv;
                bestRU = CopyRegion(null, c.ReconU, c.Cw, cpx, cpy, rw, rh); bestRV = CopyRegion(null, c.ReconV, c.Cw, cpx, cpy, rw, rh);
            }
        }
        PasteRegion(bestRU, c.ReconU, c.Cw, cpx, cpy, rw, rh);
        PasteRegion(bestRV, c.ReconV, c.Cw, cpx, cpy, rw, rh);
        int uvMode = (int)best.Mode, uvDelta = best.Delta;

        bool anyC = false;
        foreach (var l in bestU) anyC |= HasNonZero(l.Lv);
        foreach (var l in bestV) anyC |= HasNonZero(l.Lv);
        bool lumaNz = txb == null ? HasNonZero(yC) : !txb.TrueForAll(t => !HasNonZero(t.Cf));
        int skip = (lumaNz || anyC) ? 0 : 1;

        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        if (skip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);
        c.Msac.EncodeSymbolAdapt(uvModeCdf, uvMode, uvNsym);
        if (IsDirectional((Av1IntraPredMode)uvMode) && uvAngleOk)
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf(uvMode - (int)Av1IntraPredMode.Vertical), uvDelta + 3, 6);
        EmitPaletteFlags(c, lumaBs, bx4, by4, w4, h4, yMode == Av1IntraPredMode.Dc, uvDc: uvMode == 0);
        // (no filter_intra symbol: these leaves are 64 wide, above the 32x32 filter-intra limit)

        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), depth, Math.Min((int)lTDim.Max, 2));
        }

        if (skip == 0)
        {
            if (txb != null) EmitSplitLuma(c, emitTx, (int)yMode, txb);
            else if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)yMode, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            for (int pl = 0; pl < 2; pl++)
            {
                byte[] ac = pl == 0 ? c.ACU : c.ACV, lc = pl == 0 ? c.LCU : c.LCV;
                var lvs = pl == 0 ? bestU : bestV;
                for (int k = 0; k < txs.Count; k++)
                {
                    var (x, y, _) = txs[k];
                    int sk = Av1CoeffDecode.GetSkipCtx(in ct, lumaBs, ac.AsSpan(cxR + x), lc.AsSpan(cyR + y), 1, (int)c.Layout);
                    int sg = Av1CoeffDecode.GetDcSignCtx(chromaTx, ac.AsSpan(cxR + x), lc.AsSpan(cyR + y));
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, lvs[k].Lv, skipCtx: sk, dcSignCtx: sg);
                    // dav1d: per-tx ctx fill, clipped to the visible frame
                    int ctw = Math.Min(ctW, (c.Bw4 - (bx4 + (x << ssX)) + ssX) >> ssX);
                    int cth = Math.Min(ctH, (c.Bh4 - (by4 + (y << ssY)) + ssY) >> ssY);
                    for (int i = 0; i < ctw && cxR + x + i < 32; i++) ac[cxR + x + i] = lvs[k].Cf;
                    for (int i = 0; i < cth && cyR + y + i < 32; i++) lc[cyR + y + i] = lvs[k].Cf;
                }
            }
        }

        int yW = Math.Min(w4, c.Bw4 - bx4), yH = Math.Min(h4, c.Bh4 - by4);
        int cW = Math.Min(Math.Max(1, w4 >> ssX), (c.Bw4 - bx4 + ssX) >> ssX), cH = Math.Min(Math.Max(1, h4 >> ssY), (c.Bh4 - by4 + ssY) >> ssY);
        ref readonly var eTDim = ref Av1Tables.TxfmDimensions[txb == null ? lumaTx : emitTx];
        sbyte txLw = (sbyte)eTDim.Lw, txLh = (sbyte)eTDim.Lh;
        for (int i = 0; i < yW && bxR + i < 32; i++) { if (txb == null) c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; }
        for (int j = 0; j < yH && byR + j < 32; j++) { if (txb == null) c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; }
        for (int i = 0; i < cW && cxR + i < 32; i++) { if (skip != 0) { c.ACU[cxR + i] = 0x40; c.ACV[cxR + i] = 0x40; } c.AModeUv[cxR + i] = (byte)uvMode; }
        for (int j = 0; j < cH && cyR + j < 32; j++) { if (skip != 0) { c.LCU[cyR + j] = 0x40; c.LCV[cyR + j] = 0x40; } c.LModeUv[cyR + j] = (byte)uvMode; }
        FillPaletteCtx(c, bx4, by4, yW, yH, null, null);
    }

    // CfL luma AC for 4:2:2 / 4:4:4 via the decoder's own Av1Reconstruction.ComputeCflAc, with the decoder's exact
    // edge padding (recon_tmpl cfl: furthest_r/b from the visible luma extent rounded to the luma tx size).
    private static void CflAcAnyLayout(ColorPartCtx c, int lumaBs, int lumaTx, int bx4, int by4, short[] ac)
    {
        int ssX = c.SsX, ssY = c.SsY;
        int bw4 = Av1Tables.BlockDimensions[lumaBs, 0], bh4 = Av1Tables.BlockDimensions[lumaBs, 1];
        int w4 = Math.Min(bw4, c.Bw4 - bx4), h4 = Math.Min(bh4, c.Bh4 - by4);
        int cw4 = (w4 + ssX) >> ssX, ch4 = (h4 + ssY) >> ssY;
        int cbw4 = (bw4 + ssX) >> ssX, cbh4 = (bh4 + ssY) >> ssY;
        ref readonly var td = ref Av1Tables.TxfmDimensions[lumaTx];
        int furthestR = ((cw4 << ssX) + td.W - 1) & ~(td.W - 1);
        int furthestB = ((ch4 << ssY) + td.H - 1) & ~(td.H - 1);
        int wPad = cbw4 - (furthestR >> ssX), hPad = cbh4 - (furthestB >> ssY);
        int yOff = 4 * ((bx4 & ~ssX) + (by4 & ~ssY) * c.W);
        Av1Reconstruction.ComputeCflAc(ac, c.ReconY.AsSpan(yOff), c.W, cbw4 * 4, cbh4 * 4, ssX, ssY, wPad, hPad);
    }

    // Codes PARTITION_HORZ/VERT at the 8x8 level -> two 8x4 (horz) or 4x8 (vert) luma sub-blocks. AV1 shared-chroma:
    // the chroma (4x4, covering the 8x8) is coded once on the SECOND (odd-position) sub-block. partCdf/nPart are the
    // 8x8 partition CDF (PartitionTypeCount[4]=3). Mirrors the decoder's HORZ/VERT recursion at Bl8x8.
    private static void EncodeSub8Pair(ColorPartCtx c, bool horz, int bx4, int by4, Span<ushort> partCdf, int nPart, int bx8, int by8, Av1EdgeNode node)
    {
        c.Msac.EncodeSymbolAdapt(partCdf, (int)(horz ? Av1BlockPartition.Horizontal : Av1BlockPartition.Vertical), nPart);
        if (Sp.AomSub8Rect && (c.Layout == Av1PixelLayout.I420 || c.Mono))
        {
            // through the rect leaf (the libaom luma search): the second sub-block carries the 4x4 chroma of the 8x8
            if (horz)
            {
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, (int)Av1TxSize.Tx4x4, bx4, by4, 2, 1, node.H0, -1);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, (int)Av1TxSize.Tx4x4, bx4, by4 + 1, 2, 1, node.H1, 1);
            }
            else
            {
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, (int)Av1TxSize.Tx4x4, bx4, by4, 1, 2, node.V0, -1);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, (int)Av1TxSize.Tx4x4, bx4 + 1, by4, 1, 2, node.V1, 1);
            }
        }
        else if (c.Layout == Av1PixelLayout.I444)
        {
            // 4:4:4: every sub-block codes its own chroma, so each is a regular (layout-generic) rect leaf.
            if (horz)
            {
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, -1, bx4, by4, 2, 1, node.H0);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, -1, bx4, by4 + 1, 2, 1, node.H1);
            }
            else
            {
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, -1, bx4, by4, 1, 2, node.V0);
                EncodeRectLeafColor(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, -1, bx4 + 1, by4, 1, 2, node.V1);
            }
        }
        else if (horz)
        {
            // two 8x4: top (by4, no chroma), bottom (by4+1, chroma over the 8x8)
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, bx4, by4, 2, 1, false, node.H0);
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs8x4, TxIdx8x4, bx4, by4 + 1, 2, 1, true, node.H1);
        }
        else
        {
            // two 4x8: left (bx4, no chroma), right (bx4+1, chroma over the 8x8)
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, bx4, by4, 1, 2, false, node.V0);
            EncodeSub8Leaf(c, (int)Av1BlockSize.Bs4x8, TxIdx4x8, bx4 + 1, by4, 1, 2, true, node.V1);
        }
        FillPartCtx(c, 4, bx8, by8, 1, horz ? Av1BlockPartition.Horizontal : Av1BlockPartition.Vertical);
    }

    // Codes PARTITION_SPLIT at the 8x8 level -> four 4x4 luma blocks in the decoder's order (top-left, top-right,
    // bottom-left, bottom-right) with the edge-tree tip's availability flags; the 4x4 chroma covering the 8x8 is coded
    // on the last (odd x, odd y) block.
    private static void EncodeSub8Quad(ColorPartCtx c, int bx4, int by4, Span<ushort> partCdf, int nPart, int bx8, int by8, Av1EdgeNode node)
    {
        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
        const int bs = (int)Av1BlockSize.Bs4x4, tx = (int)Av1TxSize.Tx4x4;
        if (Sp.AomSub8Rect && (c.Layout == Av1PixelLayout.I420 || c.Mono))
        {
            // through the rect leaf; the last (odd x, odd y) block carries the 4x4 chroma of the 8x8
            EncodeRectLeafColor(c, bs, tx, tx, bx4, by4, 1, 1, Av1EdgeFlags.AllTrAndBl, -1);
            EncodeRectLeafColor(c, bs, tx, tx, bx4 + 1, by4, 1, 1, node.Split0, -1);
            EncodeRectLeafColor(c, bs, tx, tx, bx4, by4 + 1, 1, 1, node.Split1, -1);
            EncodeRectLeafColor(c, bs, tx, tx, bx4 + 1, by4 + 1, 1, 1, node.Split2, 1);
            FillPartCtx(c, 4, bx8, by8, 1, Av1BlockPartition.Split);
            return;
        }
        if (c.Layout == Av1PixelLayout.I444)
        {
            EncodeRectLeafColor(c, bs, tx, -1, bx4, by4, 1, 1, Av1EdgeFlags.AllTrAndBl);
            EncodeRectLeafColor(c, bs, tx, -1, bx4 + 1, by4, 1, 1, node.Split0);
            EncodeRectLeafColor(c, bs, tx, -1, bx4, by4 + 1, 1, 1, node.Split1);
            EncodeRectLeafColor(c, bs, tx, -1, bx4 + 1, by4 + 1, 1, 1, node.Split2);
            FillPartCtx(c, 4, bx8, by8, 1, Av1BlockPartition.Split);
            return;
        }
        EncodeSub8Leaf(c, bs, tx, bx4, by4, 1, 1, false, Av1EdgeFlags.AllTrAndBl);
        EncodeSub8Leaf(c, bs, tx, bx4 + 1, by4, 1, 1, false, node.Split0);
        EncodeSub8Leaf(c, bs, tx, bx4, by4 + 1, 1, 1, false, node.Split1);
        EncodeSub8Leaf(c, bs, tx, bx4 + 1, by4 + 1, 1, 1, true, node.Split2);
        FillPartCtx(c, 4, bx8, by8, 1, Av1BlockPartition.Split);
    }

    // One sub-8x8 luma leaf (8x4, 4x8 or 4x4): skip, y_mode (NO angle_delta for these sizes), use_filter_intra
    // (eligible, always 0 here), tx_size, luma coeffs. When hasChroma (the last sub-block of the 8x8) it additionally
    // codes uv_mode (RD-searched like the rect leaf; no angle_delta either) + a 4x4 chroma over the 8x8-aligned region.
    private static void EncodeSub8Leaf(ColorPartCtx c, int lumaBs, int lumaTx, int bx4, int by4, int w4, int h4, bool hasChroma, Av1EdgeFlags edge)
    {
        if (c.Mono) hasChroma = false;
        int w = w4 * 4, h = h4 * 4, bx = bx4 * 4, by = by4 * 4;
        int bxR = bx4 & 31, byR = by4 & 31;
        int lScan = Av1Tables.Scans[lumaTx].Length;
        ref readonly var lTDim = ref Av1Tables.TxfmDimensions[lumaTx];
        int aboveCtx = Av1Tables.IntraModeContext[c.AModeY[bxR]], leftCtx = Av1Tables.IntraModeContext[c.LModeY[byR]];
        var ymCdf = c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int ySign = Av1CoeffDecode.GetDcSignCtx(lumaTx, c.ALY.AsSpan(bxR), c.LLY.AsSpan(byR));
        int intraFlags = IntraEdgeFlags(c.AModeY[bxR], c.LModeY[byR]);

        // Luma mode search (no angle_delta at these sizes, so only the ~13 base modes — all RD-evaluated directly,
        // no SATD prescreen). Full reduced tx-type set (IDTX+DCT/ADST): the symbol is coded for 8x4/4x8 (max tx
        // dim <= 16), and IDTX helps these tiny edge sub-blocks — same win as the square/rect leaves.
        var scr = t_scSub8 ??= new LeafScratch();
        var pred = scr.P1; var bestPred = scr.P2; var resBuf = scr.R;
        var qfCand = scr.Q1; var qfWin = scr.Q2;
        double rectLambda = LamK * c.AcDq * c.AcDq;
        int[] yC = null!; Av1IntraPredMode yMode = Av1IntraPredMode.Dc; Av1TxType yInv = Av1TxType.DctDct; int yTxIdx = 1;
        double best = double.MaxValue;
        // Faster presets: only the Sub8ModeCandidates base modes with the lowest prediction SAD reach the RD loop.
        ulong keep = ulong.MaxValue;
        uint hogMask8 = HogSkipMask(c.Luma, c.W, bx, by, w, h, c.Bw4 * 4, c.Bh4 * 4, Sp.HogLevel);
        for (int ci = 0; ci < CandidateModes.Length && ci < 64; ci++)
            if ((hogMask8 >> (int)CandidateModes[ci].Mode & 1) != 0) keep &= ~(1UL << ci);
        if (Sp.Sub8ModeCandidates < 13)
        {
            Span<long> sads = stackalloc long[CandidateModes.Length];
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                sads[ci] = long.MaxValue;
                if (CandidateModes[ci].Delta != 0 || (keep >> ci & 1) == 0) continue;
                PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, CandidateModes[ci].Mode, 0, pred, edge, intraFlags);
                long sad = 0;
                for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) sad += Math.Abs(c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx]);
                sads[ci] = sad;
            }
            ulong allowed = keep;
            keep = 0;
            for (int k = 0; k < Sp.Sub8ModeCandidates; k++)
            {
                int bi = -1;
                for (int ci = 0; ci < sads.Length; ci++) if (sads[ci] != long.MaxValue && (bi < 0 || sads[ci] < sads[bi])) bi = ci;
                if (bi < 0) break;
                keep |= 1UL << bi; sads[bi] = long.MaxValue;
            }
        }
        bool fastTx = Sp.FastIntraTxType;
        for (int pass = 0; pass < (fastTx ? 2 : 1); pass++)
        for (int ci = 0; ci < CandidateModes.Length; ci++)
        {
            (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
            if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
            if (delta != 0) continue;   // no angle_delta at 8x4/4x8: only the base (delta 0) mode
            if (ci < 64 && (keep >> ci & 1) == 0) continue;
            if (pass == 1 && (mode != yMode || yC == null)) continue;
            PredictIntraRect(c.ReconY, c.W, c.Bw4, c.Bh4, bx4, by4, w, h, mode, 0, pred, edge, intraFlags);
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) resBuf[yy * w + xx] = c.Luma[(by + yy) * c.W + (bx + xx)] - pred[yy * w + xx];
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode);
            foreach (var (fwd, inv, idx) in (UseFullIntraTxSet ? IntraTxTypesFull : IntraTxTypes))
            {
                if (fastTx && (pass == 0) != (inv == Av1TxType.DctDct)) continue;
                int[] cf = Av1FwdTransform.ForwardQuantRect(resBuf, w, h, lumaTx, c.DcDq, c.AcDq, lScan, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                long sse = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv);
                if (!Sp.RdoqInSearch && sse + rectLambda * modeBits >= best) { Av1FwdTransform.ReturnLevels(cf); continue; }
                double rate = oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)mode, inv, cf, 0, ySign)
                    : Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet);
                double j = sse + rectLambda * (rate + modeBits);
                if (Sp.RdoqInSearch && !oneD && j < best * Sp.RdoqSearchMargin)
                {
                    Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, qfCand, c.DcDq, c.AcDq, 0, ySign, idx,
                        RdoqScale * LamK * c.AcDq * c.AcDq);
                    rate = Av1CoeffEncode.EstimateCoefBits(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)mode, cf, 0, ySign, idx, fullSet: UseFullIntraTxSet);
                    j = ReconSseCandRect(cf, lumaTx, w, h, c.DcDq, c.AcDq, pred, c.Luma, c.W, bx, by, inv) + rectLambda * (rate + modeBits);
                }
                if (j < best) { Av1FwdTransform.ReturnLevels(yC); best = j; yC = cf; yMode = mode; yInv = inv; yTxIdx = idx; Array.Copy(pred, bestPred, h * w); Array.Copy(qfCand, qfWin, lScan); }
                else Av1FwdTransform.ReturnLevels(cf);
            }
        }
        if (yInv != Av1TxType.VDct && yInv != Av1TxType.HDct && !Sp.RdoqInSearch)
            Av1CoeffEncode.RdoqOptimize(c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, qfWin, c.DcDq, c.AcDq, 0, ySign, yTxIdx,
                RdoqScale * LamK * c.AcDq * c.AcDq);

        // Reconstruct this sub-block's luma into ReconY now — the has_chroma sub-block's CfL reads the full 8x8 luma
        // (both sub-blocks are reconstructed by the time we reach it). Encoder-internal; emission order is unaffected.
        byte cfY = DequantAndReconstructPredRect(yC, lumaTx, w, h, c.DcDq, c.AcDq, bestPred, c.ReconY, c.W, bx, by, yInv);

        // Chroma (deferred to the has_chroma sub-block): 4x4 over the 8x8-aligned region. UV mode by RD, as the rect leaf.
        int c8x4 = bx4 & ~1, c8y4 = by4 & ~1, cbx = c8x4 * 2, cby = c8y4 * 2, cxR = (c8x4 & 31) >> 1, cyR = (c8y4 & 31) >> 1;
        const int chromaTx = (int)Av1TxSize.Tx4x4; int cScan = Av1Tables.Scans[chromaTx].Length;
        int[] uC = System.Array.Empty<int>(), vC = System.Array.Empty<int>();
        bool cflAllowed = ((Av1Tables.CflAllowedMask >> lumaBs) & 1) != 0;
        int uvNsym = Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1);
        bool useCfl = false; int alphaU = 0, alphaV = 0, uvMode = 0;
        ushort[] predU = null!, predV = null!;
        if (hasChroma)
        {
            int dcU = ChromaDc(c, c.ReconU, cbx, cby, 4, 4);
            int dcV = ChromaDc(c, c.ReconV, cbx, cby, 4, 4);
            uC = ForwardResidualRectDc(c.U, c.Cw, cbx, cby, 4, 4, dcU, chromaTx, c.DcDq, c.AcDq, cScan);
            vC = ForwardResidualRectDc(c.V, c.Cw, cbx, cby, 4, 4, dcV, chromaTx, c.DcDq, c.AcDq, cScan);
            predU = new ushort[16]; Array.Fill(predU, (ushort)Math.Clamp(dcU, 0, PixMax));
            predV = new ushort[16]; Array.Fill(predV, (ushort)Math.Clamp(dcV, 0, PixMax));
            ref readonly var cTD = ref Av1Tables.TxfmDimensions[chromaTx];
            int eUSkip = Av1CoeffDecode.GetSkipCtx(in cTD, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int eVSkip = Av1CoeffDecode.GetSkipCtx(in cTD, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
            int eUSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
            int eVSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
            bool exactC = Sp.ChromaRateExact;
            var eCoef = c.Cdf.Coef; var eMode = c.Cdf.Mode; var eCdf = c.Cdf;
            double CRate(int[] cu, int[] cv) => exactC
                ? Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, chromaTx, 1, 0, cu, eUSkip, eUSign, 0) + Av1CoeffEncode.EstimateCoefBits(eCoef, eMode, chromaTx, 1, 0, cv, eVSkip, eVSign, 0)
                : CoeffCost(cu) + CoeffCost(cv);
            var uvModeCdf = c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode);
            double lam = LamK * c.AcDq * c.AcDq;
            double bestJ = ReconSseCandRect(uC, chromaTx, 4, 4, c.DcDq, c.AcDq, predU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                         + ReconSseCandRect(vC, chromaTx, 4, 4, c.DcDq, c.AcDq, predV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                         + lam * (CRate(uC, vC) + Av1CoeffEncode.SymBits(uvModeCdf, 0));

            // Other UV modes (base angles only: no uv angle_delta below 8x8): SAD prescreen, then RD.
            if (UseUvModeSearch && Sp.UseSub8UvSearch)
            {
                var chromaEdge = ((edge & Av1EdgeFlags.I420TopHasRight) != 0 ? Av1EdgeFlags.I444TopHasRight : 0) |
                                 ((edge & Av1EdgeFlags.I420LeftHasBottom) != 0 ? Av1EdgeFlags.I444LeftHasBottom : 0);
                int cIntraFlags = IntraEdgeFlags(c.AModeUv[cxR], c.LModeUv[cyR]);
                int cbw4 = c.Bw4 >> 1, cbh4 = c.Bh4 >> 1, cbx4 = c8x4 >> 1, cby4 = c8y4 >> 1;
                var pu = new ushort[16]; var pv = new ushort[16];
                Span<int> topIdx = stackalloc int[RdUvCandidates];
                Span<long> topCost = stackalloc long[RdUvCandidates];
                topCost.Fill(long.MaxValue);
                for (int ci = 0; ci < CandidateModes.Length; ci++)
                {
                    (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                    if (mode == Av1IntraPredMode.Dc || delta != 0) continue;
                    PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, 4, 4, mode, 0, pu, chromaEdge, cIntraFlags);
                    long sad = 0;
                    for (int yy = 0; yy < 4; yy++) { int r = (cby + yy) * c.Cw + cbx; for (int xx = 0; xx < 4; xx++) sad += Math.Abs(c.U[r + xx] - pu[yy * 4 + xx]); }
                    for (int k = 0; k < RdUvCandidates; k++)
                        if (sad < topCost[k]) { for (int j = RdUvCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = sad; topIdx[k] = ci; break; }
                }
                for (int t = 0; t < RdUvCandidates; t++)
                {
                    if (topCost[t] == long.MaxValue) break;
                    var (mode, _) = CandidateModes[topIdx[t]];
                    PredictIntraRect(c.ReconU, c.Cw, cbw4, cbh4, cbx4, cby4, 4, 4, mode, 0, pu, chromaEdge, cIntraFlags);
                    PredictIntraRect(c.ReconV, c.Cw, cbw4, cbh4, cbx4, cby4, 4, 4, mode, 0, pv, chromaEdge, cIntraFlags);
                    var uvTx = UvIntraTxType(chromaTx, (int)mode);
                    var uvFwd = FwdTypeForTxType(uvTx);
                    int[] uu = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, pu, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                    int[] vv = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, pv, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan, null, uvFwd);
                    double j = ReconSseCandRect(uu, chromaTx, 4, 4, c.DcDq, c.AcDq, pu, c.U, c.Cw, cbx, cby, uvTx)
                             + ReconSseCandRect(vv, chromaTx, 4, 4, c.DcDq, c.AcDq, pv, c.V, c.Cw, cbx, cby, uvTx)
                             + lam * (CRate(uu, vv) + Av1CoeffEncode.SymBits(uvModeCdf, (int)mode));
                    if (j < bestJ) { bestJ = j; uvMode = (int)mode; uC = uu; vC = vv; (predU, pu) = (pu, predU); (predV, pv) = (pv, predV); }
                }
            }

            if (cflAllowed && UseCfl)
            {
                var acc = new short[16];
                ComputeCflAcEncRect(c.ReconY, c.W, c8x4 * 4, c8y4 * 4, 4, 4, acc);
                int aU = BestCflAlphaRect(c.U, c.Cw, cbx, cby, 4, 4, dcU, acc);
                int aV = BestCflAlphaRect(c.V, c.Cw, cbx, cby, 4, 4, dcV, acc);
                if (aU != 0 || aV != 0)
                {
                    var cflU = BuildCflPredRect(dcU, acc, 4, 4, aU);
                    var cflV = BuildCflPredRect(dcV, acc, 4, 4, aV);
                    int[] uCc = ForwardResidualPredRect(c.U, c.Cw, cbx, cby, cflU, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan);
                    int[] vCc = ForwardResidualPredRect(c.V, c.Cw, cbx, cby, cflV, 4, 4, chromaTx, c.DcDq, c.AcDq, cScan);
                    double cflJ = ReconSseCandRect(uCc, chromaTx, 4, 4, c.DcDq, c.AcDq, cflU, c.U, c.Cw, cbx, cby, Av1TxType.DctDct)
                                + ReconSseCandRect(vCc, chromaTx, 4, 4, c.DcDq, c.AcDq, cflV, c.V, c.Cw, cbx, cby, Av1TxType.DctDct)
                                + lam * (CRate(uCc, vCc) + Av1CoeffEncode.SymBits(uvModeCdf, (int)Av1IntraPredMode.ChromaFromLuma)
                                         + (exactC ? CflAlphaBits(eCdf, aU, aV) : (aU != 0 ? 5 : 0) + (aV != 0 ? 5 : 0)));
                    if (cflJ < bestJ)
                    {
                        bestJ = cflJ; useCfl = true; uvMode = (int)Av1IntraPredMode.ChromaFromLuma;
                        alphaU = aU; alphaV = aV; uC = uCc; vC = vCc; predU = cflU; predV = cflV;
                    }
                }
            }
        }
        int skip = (HasNonZero(yC) || (hasChroma && (HasNonZero(uC) || HasNonZero(vC)))) ? 0 : 1;

        int skipCtx = c.ASkip[bxR] + c.LSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);
        if (skip == 0) c.Msac.Mark(CdefMarker(bx4, by4));
        c.Msac.EncodeSymbolAdapt(ymCdf, (int)yMode, 12);   // no angle_delta for 8x4/4x8
        if (hasChroma)
        {
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), uvMode, uvNsym);
            if (useCfl) EncodeCflAlphas(c.Msac, c.Cdf, alphaU, alphaV);
        }
        if (UseFilterIntra && yMode == Av1IntraPredMode.Dc &&
            Math.Max(Av1Tables.BlockDimensions[lumaBs, 2], Av1Tables.BlockDimensions[lumaBs, 3]) <= 3)
            c.Msac.EncodeBoolAdapt(c.Cdf.GetFilterIntraCdf((Av1BlockSize)lumaBs), 0);
        if (UseColorTxDepth && lTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LTxY[byR] >= lTDim.Lh ? 1 : 0) + (c.ATxY[bxR] >= lTDim.Lw ? 1 : 0);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(lTDim.Max - 1, txCtx), 0, Math.Min((int)lTDim.Max, 2));
        }

        byte cfU = 0x40, cfV = 0x40;
        ref readonly var cTDim = ref Av1Tables.TxfmDimensions[chromaTx];
        if (skip == 0)
        {
            if (yInv == Av1TxType.VDct || yInv == Av1TxType.HDct)
                Av1CoeffEncode.EncodeCoefs1D(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, (int)yMode, yInv, yC, skipCtx: 0, dcSignCtx: ySign);
            else
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, lumaTx, 0, (int)yMode, yC, skipCtx: 0, dcSignCtx: ySign, txTypeIdx: yTxIdx, fullSet: UseFullIntraTxSet);
            if (hasChroma)
            {
                int uSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
                int vSkip = Av1CoeffDecode.GetSkipCtx(in cTDim, lumaBs, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR), 1, (int)Av1PixelLayout.I420);
                int uSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACU.AsSpan(cxR), c.LCU.AsSpan(cyR));
                int vSign = Av1CoeffDecode.GetDcSignCtx(chromaTx, c.ACV.AsSpan(cxR), c.LCV.AsSpan(cyR));
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, uC, skipCtx: uSkip, dcSignCtx: uSign);
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, chromaTx, 1, 0, vC, skipCtx: vSkip, dcSignCtx: vSign);
            }
            if (hasChroma)
            {
                var uvTxR = UvIntraTxType(chromaTx, uvMode);
                cfU = DequantAndReconstructPredRect(uC, chromaTx, 4, 4, c.DcDq, c.AcDq, predU, c.ReconU, c.Cw, cbx, cby, uvTxR);
                cfV = DequantAndReconstructPredRect(vC, chromaTx, 4, 4, c.DcDq, c.AcDq, predV, c.ReconV, c.Cw, cbx, cby, uvTxR);
            }
        }
        else if (hasChroma)
        {
            for (int yy = 0; yy < 4; yy++) Array.Copy(predU, yy * 4, c.ReconU, (cby + yy) * c.Cw + cbx, 4);
            for (int yy = 0; yy < 4; yy++) Array.Copy(predV, yy * 4, c.ReconV, (cby + yy) * c.Cw + cbx, 4);
        }

        sbyte txLw = (sbyte)lTDim.Lw, txLh = (sbyte)lTDim.Lh;
        for (int i = 0; i < w4 && bxR + i < 32; i++) { c.ALY[bxR + i] = cfY; c.AModeY[bxR + i] = (byte)yMode; c.ASkip[bxR + i] = (byte)skip; c.ATxY[bxR + i] = txLw; c.APalSz[bxR + i] = 0; }
        for (int j = 0; j < h4 && byR + j < 32; j++) { c.LLY[byR + j] = cfY; c.LModeY[byR + j] = (byte)yMode; c.LSkip[byR + j] = (byte)skip; c.LTxY[byR + j] = txLh; c.LPalSz[byR + j] = 0; }
        FillPaletteCtx(c, bx4, by4, Math.Min(w4, c.Bw4 - bx4), Math.Min(h4, c.Bh4 - by4), null, null);
        if (hasChroma) { byte s8u = (byte)uvMode; c.ACU[cxR] = cfU; c.ACV[cxR] = cfV; c.LCU[cyR] = cfU; c.LCV[cyR] = cfV; c.AModeUv[cxR] = s8u; c.LModeUv[cyR] = s8u; }
    }

    private static byte[] Filled(int n)
    {
        var a = new byte[n];
        Array.Fill(a, (byte)0x40);
        return a;
    }

    private static byte[][] FilledArray(int count)
    {
        var a = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            a[i] = Filled(32);
        }

        return a;
    }

    private static int[] ForwardResidual(ReadOnlySpan<ushort> plane, int planeW, int bx, int by, int n, int dcPred,
        int dcDq, int acDq, int scanLen, double[]? qfOut = null)
    {
        var residual = Av1FwdTransform.RentLevels(n * n);   // fully written below
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[y * n + x] = plane[(by + y) * planeW + (bx + x)] - dcPred;
            }
        }

        var lv = qfOut == null
            ? Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen)
            : Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, Av1FwdTransform.FwdTxType.DctDct, qfOut);
        Av1FwdTransform.ReturnLevels(residual);
        return lv;
    }

    // Validates a multi-SB frame and returns the SB grid, real 4-unit dims (bw4/bh4, for context clipping) and
    // padded pixel dims (pw/ph = SB-aligned). Every superblock must permit PARTITION_NONE (the edge SB's
    // in-frame remainder must exceed 32px), i.e. a dimension's remainder mod 64 is 0 or >32.
    private static void ValidateMultiSb(int w, int h, out int sbCols, out int sbRows, out int bw4, out int bh4, out int pw, out int ph)
    {
        if (w < 1 || h < 1 || w > 65536 || h > 65536)
        {
            throw new NotSupportedException($"AVIF encode supports 1..65536 per dimension (got {w}x{h}).");
        }

        // Frame dims in 4-unit MI units: MiCols = 2*ceil(w/8) (always even), matching dav1d's f->bw. Using ceil(w/4)
        // instead would be odd for non-multiple-of-8 sizes and disagree with dav1d — the even MI grid lets 8x8
        // blocks tile the edges (no 4x4 needed) and is exactly what a conformant decoder derives from the header.
        bw4 = ((w + 7) >> 3) << 1;
        bh4 = ((h + 7) >> 3) << 1;
        sbCols = (bw4 + 15) >> 4;
        sbRows = (bh4 + 15) >> 4;
        pw = sbCols * 64;
        ph = sbRows * 64;
    }

    // Pads a plane to pw x ph by replicating the right/bottom edge.
    private static ushort[] PadPlane(ReadOnlySpan<ushort> src, int w, int h, int pw, int ph)
    {
        var padded = new ushort[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < pw; x++)
            {
                padded[y * pw + x] = src[sy * w + Math.Min(x, w - 1)];
            }
        }

        return padded;
    }

    private static void FillFlat(ushort[] recon, int reconW, int bx, int by, int n, int value)
    {
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = (ushort)value;
            }
        }
    }

    // Per-superblock recursive-partition encoder state. All context arrays mirror the decoder's Above/Left
    // block-context arrays: Above arrays are per-SB128 column (persist across SB rows), Left arrays reset per
    // SB row. 4-unit arrays are indexed by Bx4&31 / By4&31; the 8-unit partition array by (Bx4&31)>>1.
    private sealed class GrayPartCtx
    {
        public Av1MsacWriter Msac = null!;
        public Av1CdfContext Cdf = null!;
        public ushort[] Luma = null!;   // padded plane
        public ushort[] Recon = null!;  // padded plane (SB-aligned), reconstructed as-we-go
        public int W;                 // padded stride
        public int Bw4, Bh4;          // REAL frame dims in 4-units (partition decisions use these)
        public int DcDq, AcDq;
        public long SplitLambda;      // rate bias for the split decision, in SATD units

        // Above (per SB128 column), Left (per SB row).
        public byte[] AbovePart = null!, AboveLCoef = null!, AboveMode = null!, AboveSkip = null!;
        public byte[] LeftPart = null!, LeftLCoef = null!, LeftMode = null!, LeftSkip = null!;
        public sbyte[] AboveTxIntra = null!, LeftTxIntra = null!; // neighbour tx log-size, for the tx-depth context
        public ushort[] Pred = new ushort[64 * 64];
        public ushort[] EstScratch = new ushort[64 * 64];
    }

    private static sbyte[] FilledSbyte(int n, sbyte v) { var a = new sbyte[n]; Array.Fill(a, v); return a; }

    private static int BlToTx(int bl) => bl switch { 1 => 4, 2 => 3, 3 => 2, _ => 1 };            // TX size ordinal
    private static int BlToBs(int bl) => bl switch { 1 => 3, 2 => 7, 3 => 12, _ => 17 };          // Av1BlockSize ordinal

    // Codes the whole tile with a recursive partition tree (PARTITION_NONE / PARTITION_SPLIT down to 8x8),
    // reconstructing each leaf so later blocks predict from the pixels the decoder will produce. Only fully-inside
    // blocks are split; edge superblocks (non-multiple-of-64 frames) stay a single PARTITION_NONE 64x64 block, so
    // every partition decision is full-range and never needs the partial-edge bool path.
    private static byte[] EncodeMultiSbTile(ReadOnlySpan<ushort> luma, int w, int h, int sbCols, int sbRows, int bw4, int bh4, int baseQIdx)
    {
        int qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
        var cdf = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cdf, qcat);
        int acDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];

        int sb128Cols = (sbCols + 1) >> 1;
        var abovePart = new byte[sb128Cols][];
        var aboveLCoef = new byte[sb128Cols][];
        var aboveMode = new byte[sb128Cols][];
        var aboveSkip = new byte[sb128Cols][];
        var aboveTxIntra = new sbyte[sb128Cols][];
        for (int i = 0; i < sb128Cols; i++)
        {
            abovePart[i] = new byte[16];
            aboveLCoef[i] = Filled(32);
            aboveMode[i] = new byte[32];
            aboveSkip[i] = new byte[32];
            aboveTxIntra[i] = FilledSbyte(32, -1);
        }

        var ctx = new GrayPartCtx
        {
            Msac = new Av1MsacWriter(), Cdf = cdf, Luma = new ushort[0], Recon = new ushort[w * h], W = w,
            Bw4 = bw4, Bh4 = bh4, DcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 0], AcDq = acDq,
            // Extra split bias beyond the per-block header cost already in EstimateCost. Zero works well because
            // that header term already penalises the four sub-block headers a split introduces.
            SplitLambda = 0L,
        };
        // `Luma` is a ReadOnlySpan param; copy to a field-friendly array once.
        var lumaArr = new ushort[w * h];
        luma.CopyTo(lumaArr);
        ctx.Luma = lumaArr;

        var (_, _, colStart, rowStart) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        try
        {
            for (int tr = 0; tr + 1 < rowStart.Length; tr++)
                for (int tc = 0; tc + 1 < colStart.Length; tc++)
                {
                    if (tiles.Count > 0)
                    {
                        ctx.Cdf = new Av1CdfContext();
                        Av1CdfDefaults.InitializeDefault(ctx.Cdf, qcat);
                        ctx.Msac = new Av1MsacWriter();
                        for (int i = 0; i < sb128Cols; i++)
                        {
                            abovePart[i] = new byte[16]; aboveLCoef[i] = Filled(32); aboveMode[i] = new byte[32];
                            aboveSkip[i] = new byte[32]; aboveTxIntra[i] = FilledSbyte(32, -1);
                        }
                    }
                    SetTileWindow(colStart[tc] * 16, rowStart[tr] * 16, Math.Min(colStart[tc + 1] * 16, bw4),
                        Math.Min(rowStart[tr + 1] * 16, bh4), w, 0, 0);
                    for (int sby = rowStart[tr]; sby < rowStart[tr + 1]; sby++)
                    {
                        ctx.LeftPart = new byte[16];
                        ctx.LeftLCoef = Filled(32);
                        ctx.LeftMode = new byte[32];
                        ctx.LeftSkip = new byte[32];
                        ctx.LeftTxIntra = FilledSbyte(32, -1);
                        for (int sbx = colStart[tc]; sbx < colStart[tc + 1]; sbx++)
                        {
                            int col = sbx >> 1;
                            ctx.AbovePart = abovePart[col];
                            ctx.AboveLCoef = aboveLCoef[col];
                            ctx.AboveMode = aboveMode[col];
                            ctx.AboveSkip = aboveSkip[col];
                            ctx.AboveTxIntra = aboveTxIntra[col];
                            EncodePartition(ctx, 1 /*Bl64x64*/, sbx * 16, sby * 16);
                        }
                    }
                    tiles.Add(ctx.Msac.Finish());
                }
        }
        finally
        {
            ClearTileWindow();
        }

        return AssembleTileGroup(tiles);
    }

    // Recursively encodes the partition tree for one block. bl is the Av1BlockLevel (1=64x64..4=8x8); bx4/by4 are
    // the block's absolute 4-unit position. Chooses PARTITION_NONE vs PARTITION_SPLIT by comparing the whole-block
    // residual SATD against the sum of the four quadrants' SATD (plus a rate bias).
    private static void EncodePartition(GrayPartCtx c, int bl, int bx4, int by4, int edgeIdx = 0)
    {
        int hsz = 16 >> bl;          // half block in 4-units
        int blk4 = 32 >> bl;         // full block in 4-units
        int n = blk4 * 4;            // block pixels
        int SplitCh(int q) => Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], q);
        // Edge logic mirrors the decoder: haveH/haveV = there is room for the right/bottom half inside the frame.
        bool haveH = c.Bw4 > bx4 + hsz;
        bool haveV = c.Bh4 > by4 + hsz;

        int bx8 = (bx4 & 31) >> 1, by8 = (by4 & 31) >> 1;
        int partCtx = ((c.AbovePart[bx8] >> (4 - bl)) & 1) + (((c.LeftPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];

        // Both halves off-frame: forced SPLIT (no symbol), only the top-left child has in-frame content.
        if (!haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size in 4-units) — not yet implemented.");
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            return;
        }

        // Bottom edge (room across, none below): split_or_horz — we always force SPLIT (avoids rectangular blocks).
        if (haveH && !haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBoolGathered(partCdf, (Av1BlockLevel)bl, top: true, 1);
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4 + hsz, by4, SplitCh(1));
            return;
        }
        // Right edge (room below, none across): split_or_vert — force SPLIT.
        if (!haveH && haveV)
        {
            if (bl >= 4) throw new NotSupportedException("Edge block needs 4x4 split (odd frame size) — not yet implemented.");
            c.Msac.EncodeBoolGathered(partCdf, (Av1BlockLevel)bl, top: false, 1);
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4, by4 + hsz, SplitCh(2));
            return;
        }

        // Interior: full partition symbol, RD NONE-vs-SPLIT — also for a block that merely extends past the frame
        // (midpoint inside): all four children start in-frame, and the estimate sees the replicated padding on both
        // sides. Keeping such blocks a single NONE put a large transform on every bottom/right edge block.
        bool doSplit = false;
        if (bl < 4)
        {
            long costNone = EstimateCost(c, bl, bx4, by4);
            long costSplit = c.SplitLambda;
            foreach ((int dx, int dy) in new[] { (0, 0), (hsz, 0), (0, hsz), (hsz, hsz) })
                costSplit += EstimateCost(c, bl + 1, bx4 + dx, by4 + dy);
            doSplit = costSplit < costNone;
        }

        if (doSplit)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
            EncodePartition(c, bl + 1, bx4, by4, SplitCh(0));
            EncodePartition(c, bl + 1, bx4 + hsz, by4, SplitCh(1));
            EncodePartition(c, bl + 1, bx4, by4 + hsz, SplitCh(2));
            EncodePartition(c, bl + 1, bx4 + hsz, by4 + hsz, SplitCh(3));
            return; // SPLIT nodes (bl<8x8) do not update partition context
        }

        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
        // Actual leaf: pick the best mode predicting from the real reconstruction, then code + reconstruct.
        Av1EdgeFlags leafEdge = Av1IntraEdgeTree.Tree64[edgeIdx].O;
        (Av1IntraPredMode yMode, int yDelta, _) =
            ChooseIntraMode(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred, leafEdge);
        EncodeLeafBlock(c, bl, bx4, by4, blk4, n, yMode, yDelta, leafEdge);

        // Partition context fill for the NONE leaf (mirrors DecodeSuperblock's AboveLeftPartCtx update).
        byte aboveVal = Av1Tables.AboveLeftPartCtx[0, bl, (int)Av1BlockPartition.None];
        byte leftVal = Av1Tables.AboveLeftPartCtx[1, bl, (int)Av1BlockPartition.None];
        int pcount = hsz; // = 1<<Ulog2(hsz), block width in 8-units
        for (int i = 0; i < pcount && bx8 + i < 16; i++) c.AbovePart[bx8 + i] = aboveVal;
        for (int j = 0; j < pcount && by8 + j < 16; j++) c.LeftPart[by8 + j] = leftVal;
    }

    // Reduces a square tx size `depth` times (each step to the next-smaller square, via TxfmDimensions.Sub).
    private static int ReduceTx(int tx, int depth) { for (int i = 0; i < depth; i++) tx = Av1Tables.TxfmDimensions[tx].Sub; return tx; }

    // Encodes one PARTITION_NONE leaf: skip flag, Y mode (+ angle_delta), tx size (TX_MODE_SELECT), then the
    // coefficients of each sub-transform block (with per-tx-block intra prediction + reconstruction), and the
    // above/left context fills. Chooses a tx depth (0/1/2) that minimizes estimated coding cost.
    private static void EncodeLeafBlock(GrayPartCtx c, int bl, int bx4, int by4, int blk4, int n,
        Av1IntraPredMode yMode, int yDelta, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int maxTx = BlToTx(bl);
        int bxR = bx4 & 31, byR = by4 & 31;
        ref readonly var maxTDim = ref Av1Tables.TxfmDimensions[maxTx];

        // Rate-distortion mode + tx-type decision at the block-size transform (also fixes the skip flag and the
        // prediction in c.Pred). Overrides the SATD mode passed from the partition search.
        int[] coeffs0; Av1TxType invTx0; int txIdx0;
        if (UseRd)
        {
            var rd = ChooseLeafRd(c, bx4, by4, n, maxTx, edgeFlags);
            yMode = rd.Mode; yDelta = rd.Delta;
            coeffs0 = rd.Coeffs; invTx0 = rd.Inv; txIdx0 = rd.Idx;
        }
        else
        {
            PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, bx4, by4, n, yMode, yDelta, c.Pred, edgeFlags, IntraEdgeFlags(c.AboveMode[bxR], c.LeftMode[byR]));
            int[] res = ComputeResidualPred(c.Luma, c.W, bx4 * 4, by4 * 4, c.Pred, n);
            (coeffs0, invTx0, txIdx0) = ChooseTxType(res, n, maxTx, c.DcDq, c.AcDq, c.Pred, c.Luma, c.W, bx4 * 4, by4 * 4, LamK * c.AcDq * c.AcDq);
        }
        int skip = HasNonZero(coeffs0) ? 0 : 1;

        // Choose tx depth (only when coding residual, block > 4x4, and fully inside the frame so every sub-tx block
        // is in-bounds — edge blocks in non-multiple-of-64 frames keep the single block-size transform).
        bool fullyInside = bx4 + blk4 <= c.Bw4 && by4 + blk4 <= c.Bh4;
        int maxDepth = (skip == 0 && fullyInside) ? Math.Min((int)maxTDim.Max, Sp.ColorTxMaxDepth) : 0;
        int depth = 0;
        if (maxDepth > 0)
        {
            long bestCost = EstimateTxDepthCost(c, bx4, by4, blk4, maxTx, yMode, yDelta);
            for (int d = 1; d <= maxDepth; d++)
            {
                long cost = EstimateTxDepthCost(c, bx4, by4, blk4, ReduceTx(maxTx, d), yMode, yDelta);
                if (cost < bestCost) { bestCost = cost; depth = d; }
            }
        }
        int tx = ReduceTx(maxTx, depth);
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];

        int skipCtx = c.AboveSkip[bxR] + c.LeftSkip[byR];
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(skipCtx), (uint)skip);

        int aboveCtx = Av1Tables.IntraModeContext[c.AboveMode[bxR]];
        int leftCtx = Av1Tables.IntraModeContext[c.LeftMode[byR]];
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(aboveCtx, leftCtx), (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), yDelta + 3, 6);

        // tx_depth is signalled for every intra block > 4x4 (read_tx_size allowSelect = !skip || !is_inter,
        // and is_inter is false), so it is coded for skip blocks too (depth 0). Gating on skip==0 desynced dav1d.
        if (maxTDim.Max > (byte)Av1TxSize.Tx4x4)
        {
            int txCtx = (c.LeftTxIntra[byR] >= maxTDim.Lh ? 1 : 0) + (c.AboveTxIntra[bxR] >= maxTDim.Lw ? 1 : 0);
            int nSym = Math.Min((int)maxTDim.Max, 2);
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetTxSzCdf(maxTDim.Max - 1, txCtx), depth, nSym);
        }

        if (skip == 0 && depth == 0)
        {
            // Single transform (block size): use the RD-chosen coefficients/tx-type directly.
            int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(maxTx, c.AboveLCoef.AsSpan(bxR), c.LeftLCoef.AsSpan(byR));
            Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, maxTx, chroma: 0, yMode: (int)yMode, coeffs0,
                skipCtx: 0, dcSignCtx: dcSignCtx, txTypeIdx: txIdx0);
            byte cfCtx0 = DequantAndReconstructPred(coeffs0, maxTx, n, c.DcDq, c.AcDq, c.Pred, c.Recon, c.W, bx4 * 4, by4 * 4, invTx0);
            int cwl = Math.Min(blk4, c.Bw4 - bx4), chl = Math.Min(blk4, c.Bh4 - by4);
            for (int i = 0; i < cwl && bxR + i < 32; i++) c.AboveLCoef[bxR + i] = cfCtx0;
            for (int j = 0; j < chl && byR + j < 32; j++) c.LeftLCoef[byR + j] = cfCtx0;
        }
        else if (skip == 0)
        {
            int txN = tDim.W * 4;          // tx pixel size
            int txW4 = tDim.W;             // tx 4-unit size
            var predBuf = new ushort[txN * txN];
            int sbHasTr = (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0;
            int sbHasBl = (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0;
            // Per-tx-block: predict from reconstruction, code coeffs, reconstruct — in raster order.
            for (int iy = 0; iy < blk4; iy += txW4)
                for (int ix = 0; ix < blk4; ix += txW4)
                {
                    int cbx4 = bx4 + ix, cby4 = by4 + iy;
                    int cbxR = cbx4 & 31, cbyR = cby4 & 31;
                    var localEdge =
                        (((iy > 0 || sbHasTr == 0) && (ix + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                        ((ix > 0 || (sbHasBl == 0 && iy + txW4 >= blk4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                    PredictIntra(c.Recon, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, predBuf, localEdge, IntraEdgeFlags(c.AboveMode[bxR], c.LeftMode[byR]));
                    int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, predBuf, txN);
                    (int[] cf, Av1TxType inv, int idx) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq, predBuf, c.Luma, c.W, cbx4 * 4, cby4 * 4, LamK * c.AcDq * c.AcDq);
                    // Coeff-skip context is neighbour-based for sub-block transforms (0 only when tx == block size).
                    int coefSkipCtx = Av1CoeffDecode.GetSkipCtx(in tDim, BlToBs(bl), c.AboveLCoef.AsSpan(cbxR), c.LeftLCoef.AsSpan(cbyR), 0, 0);
                    int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, c.AboveLCoef.AsSpan(cbxR), c.LeftLCoef.AsSpan(cbyR));
                    Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, tx, chroma: 0, yMode: (int)yMode, cf,
                        skipCtx: coefSkipCtx, dcSignCtx: dcSignCtx, txTypeIdx: idx);
                    byte txCfCtx = DequantAndReconstructPred(cf, tx, txN, c.DcDq, c.AcDq, predBuf, c.Recon, c.W, cbx4 * 4, cby4 * 4, inv);
                    // LCoef context fill per tx block (clip to frame + 32-wide array).
                    int tcw = Math.Min(txW4, c.Bw4 - cbx4), tch = Math.Min(txW4, c.Bh4 - cby4);
                    for (int i = 0; i < tcw && cbxR + i < 32; i++) c.AboveLCoef[cbxR + i] = txCfCtx;
                    for (int j = 0; j < tch && cbyR + j < 32; j++) c.LeftLCoef[cbyR + j] = txCfCtx;
                }
        }
        else
        {
            for (int y = 0; y < n; y++)
                Array.Copy(c.Pred, y * n, c.Recon, (by4 * 4 + y) * c.W + bx4 * 4, n);
            int cwz = Math.Min(blk4, c.Bw4 - bx4), chz = Math.Min(blk4, c.Bh4 - by4);
            for (int i = 0; i < cwz && bxR + i < 32; i++) c.AboveLCoef[bxR + i] = 0x40;
            for (int j = 0; j < chz && byR + j < 32; j++) c.LeftLCoef[byR + j] = 0x40;
        }

        // Mode/skip/tx-size context fills over the whole coding block.
        int cw = Math.Min(blk4, c.Bw4 - bx4);
        int ch = Math.Min(blk4, c.Bh4 - by4);
        sbyte txLw = (sbyte)tDim.Lw, txLh = (sbyte)tDim.Lh;
        for (int i = 0; i < cw && bxR + i < 32; i++) { c.AboveMode[bxR + i] = (byte)yMode; c.AboveSkip[bxR + i] = (byte)skip; c.AboveTxIntra[bxR + i] = txLw; }
        for (int j = 0; j < ch && byR + j < 32; j++) { c.LeftMode[byR + j] = (byte)yMode; c.LeftSkip[byR + j] = (byte)skip; c.LeftTxIntra[byR + j] = txLh; }
    }

    // Estimates the coding cost of a leaf at a given (uniform) tx size, predicting each tx block from the SOURCE
    // plane (≈ what reconstruction will be). Used only for the tx-depth decision.
    private static long EstimateTxDepthCost(GrayPartCtx c, int bx4, int by4, int blk4, int tx, Av1IntraPredMode yMode, int yDelta)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
        int txN = tDim.W * 4, txW4 = tDim.W;
        long cost = 0;
        for (int iy = 0; iy < blk4; iy += txW4)
            for (int ix = 0; ix < blk4; ix += txW4)
            {
                int cbx4 = bx4 + ix, cby4 = by4 + iy;
                PredictIntra(c.Luma, c.W, c.Bw4, c.Bh4, cbx4, cby4, txN, yMode, yDelta, c.EstScratch);
                int[] res = ComputeResidualPred(c.Luma, c.W, cbx4 * 4, cby4 * 4, c.EstScratch, txN);
                (int[] cf, _, _) = ChooseTxType(res, txN, tx, c.DcDq, c.AcDq, c.EstScratch, c.Luma, c.W, cbx4 * 4, cby4 * 4, LamK * c.AcDq * c.AcDq);
                cost += CoeffCost(cf) + 6; // per-tx-block overhead (all_zero + tx-type + eob)
            }

        return cost;
    }

    // Estimates a block's best-mode residual SATD for the split decision, predicting from the SOURCE plane so a
    // quadrant is scored the way it would predict after splitting (its reconstructed neighbours ≈ source). Decision
    // only — never affects the coded bitstream.
    // Estimates the coding cost (in bit-like units) of a block coded PARTITION_NONE with its best mode, predicting
    // from the SOURCE plane. Cost = a fixed per-block header + a coefficient term, so splitting is only chosen when
    // the sum of quadrant costs (each carrying its own header) beats coding the parent whole. Unlike raw SATD this
    // reflects that a smooth block, though it has residual energy, quantizes to very few coefficients and is cheap.
    private const int HeaderCostBits = 22;   // partition + skip + y-mode (+ angle) symbols, amortized
    private static long EstimateCost(GrayPartCtx c, int bl, int bx4, int by4)
        => EstimateBlockCost(c.Luma, c.W, c.Bw4, c.Bh4, c.DcDq, c.AcDq, c.EstScratch, bl, bx4, by4);

    // RD lagrangian weight: J = SSE + λ·bits, with λ ∝ quant-step². Drives the NONE/SPLIT/mode decisions AND (via
    // RdoqLambdaScale·RdLambdaK·Q²) the RDOQ coefficient trimming. Retuned 2026-09 on a 6-image BD-rate corpus
    // (photo/graphics/text/texture) jointly with RdoqLambdaScale=20 and DeadzoneBias=0.04 after chroma RDOQ was
    // added: 0.004 wins over the prior 0.008 (which had been tuned before chroma RDOQ existed) — the combined
    // retune is -6.2% BD-rate, improving on all six images. Re-swept AFTER tx_depth was enabled (which shifts the
    // joint optimum): 0.004->0.003 with RdoqLambdaScale 20->25 adds a further -1.15% BD-rate (clean across the
    // corpus). Note DeadzoneBias then wants to STAY at 0.04 — with tx_depth on, dropping it to 0.02 regresses all
    // six images (+3.7%; many small transforms over-keep coefficients at a low deadzone).
    // Re-swept AGAIN 2026-09-16 after tx/mode selection became full RD (D+λR, which added a λ=RdLambdaK·Q² term to
    // the candidate decision, so RdLambdaK now serves triple duty: SATD prescreen √λ, candidate selection λ, and
    // RDOQ scale·λ): 0.003->0.002 adds -0.39% BD-rate (piechart/mountains/logo win; granite +1.6% but we already
    // beat libaom there by 2.6%). RdoqLambdaScale stays 25 (dropping to 20 regressed +0.64%).
    // Re-swept AGAIN 2026-09-21 after this session's prediction gains (CfL, directional UV modes, full intra tx set):
    // the residual statistics shifted the base-λ optimum DOWN, 0.002->0.00125 = -1.02% BD-rate (clean minimum:
    // 0.0011->+7.84, 0.00125->+7.59, 0.00135->+8.01; RdoqLambdaScale 50 re-confirmed, 65/80 worse). Better
    // prediction ⇒ keeping more residual detail (lower λ) wins.
    // Re-swept 2026-09-26 against avifenc (-a tune=psnr, scoreboard, joint with RdoqLambdaScale): 0.0008 / 125
    // (effective RDOQ λ 0.1, was 0.0625): speed 2 +1.58 -> +0.27%, speed 4 -1.00 -> -2.11%, speed 6 -1.44 -> -2.98%.
    internal static double RdLambdaK = 0.0008;
    internal static bool RoundNearest => Sp.AomRoundNearest;
    // RDOQ lambda scale (coefficient-domain) for the older search paths: the preset's, else the dev static
    private static double RdoqScale => Sp.AomRdoqScale > 0 ? Sp.AomRdoqScale : RdoqLambdaScale;
    // chroma trellis / RDOQ scale for the libaom chroma search (the preset's, else the dev static)
    private static double ChromaLamScale => Sp.AomChroma && Sp.AomChromaLam > 0 ? Sp.AomChromaLam : ChromaRdoqLambdaScale;
    // libaom allintra intra_sb_rdmult_modifier (AomSbLambda): this thread's superblock lambda factor (0 = 1).
    [ThreadStatic] private static double t_sbLamMul;
    private static double LamK
    {
        get { double k = Sp.LambdaKFrame > 0 ? Sp.LambdaKFrame : RdLambdaK; return t_sbLamMul == 0 ? k : k * t_sbLamMul; }
    }

    // libaom setup_block_rdmult (ALLINTRA): a superblock mixing near-flat (log 4x4 variance < 2) and busy (> 4) areas
    // codes at a lower lambda: 128 - min(48, 6 * (max - min)) over 128.
    private static double SbLambdaMul(ColorPartCtx c, int sbx, int sby)
    {
        int x0 = sbx * 64, y0 = sby * 64, w = Math.Min(64, c.Bw4 * 4 - x0), h = Math.Min(64, c.Bh4 * 4 - y0);
        int vmin = int.MaxValue, vmax = 0;
        for (int y = 0; y < h; y += 4)
            for (int x = 0; x < w; x += 4)
            {
                int v = Var4x4Norm(c.Luma, c.W, x0 + x, y0 + y);
                vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v);
            }
        double lmin = Math.Log(1 + vmin / 16.0), lmax = Math.Log(1 + vmax / 16.0);
        int m = 128;
        if (lmin < 2.0 && lmax > 4.0) m -= lmax - lmin > 8.0 ? 48 : (int)((lmax - lmin) * 6);
        return m / 128.0;
    }

    // Extra multiplier on the RDOQ lambda relative to the partition lambda. The partition lambda is tuned for
    // whole-block decisions; coefficient RDOQ needs a larger effective lambda to trade a marginal coefficient's
    // small distortion against its (EOB-inclusive) coding rate. Retuned to 20 (from 30) in the 2026-09 corpus
    // sweep jointly with RdLambdaK=0.004 / DeadzoneBias=0.04; then raised 20->25 in the tx_depth-on re-sweep
    // (jointly with RdLambdaK=0.003). Chroma uses the same scale (ChromaRdoqLambdaScale). Raised 25->40 in the
    // filter-intra re-sweep (2026-09-16): filter improves prediction, so residuals are smaller and more aggressive
    // coefficient trimming wins (-0.46% BD-rate, clean 5/6; the knee — 36=-0.44, 40=-0.46, 45=-0.41 vs 25).
    // RdLambdaK stayed 0.002 and DeadzoneBias stayed 0.04 (both re-confirmed optimal in the same sweep).
    // 125 with RdLambdaK 0.0008 (2026-09-26, see RdLambdaK); chroma stays at 50 (higher chroma scales lost).
    internal static double RdoqLambdaScale = 125.0;

    // Chroma coefficient RDOQ in the square colour leaf (EncodeLeafBlockColor). Chroma was previously coded at
    // round-to-nearest with no rate-distortion trimming, running measurably richer than luma at matched rate;
    // this applies the same RDOQ to U/V. Scale kept equal to luma initially, tuned against the RD benchmark.
    internal static bool UseChromaRdoq { get => Sp.UseChromaRdoq; set => Sp.UseChromaRdoq = value; }
    internal static double RdoqSkipBits => Sp.RdoqSkipBits;
    internal static double ChromaRdoqLambdaScale = 50.0;   // 25->40 (filter-intra re-sweep) ->50 (re-swept after UV modes + full tx set: gap -0.85%)

    // Enables PARTITION_HORZ / PARTITION_VERT rectangular leaves at 16x16 (colour path). Toggle for A/B testing.
    internal static bool UseRectPartition { get => Sp.UseRectPartition; set => Sp.UseRectPartition = value; }

    // Sub-8x8 rectangular partitions: PARTITION_HORZ/VERT at the 8x8 level -> two 8x4 or 4x8 luma sub-blocks with
    // AV1 shared-chroma (chroma coded once per 8x8, on the odd-position sub-block, over 4x4). -0.93% BD-rate (clean
    // on all 6 corpus images), byte-exact vs ffmpeg/libdav1d. Directly attacks piechart over-splitting (the profile
    // showed libaom fits wedge edges with 8x4/4x8 where we full-SPLIT to 8x8). Adds encode cost (every 8x8 RD-trials
    // NONE/HORZ/VERT with sub-block coding).
    internal static bool UseSub8Partition { get => Sp.UseSub8Partition; set => Sp.UseSub8Partition = value; }

    // Rect is chosen only when its estimated cost is below this fraction of the best square (NONE/SPLIT) cost.
    internal static double RectCostMargin = 0.95;

    // Enables true trial-encode RD for the colour partition decision (measure actual coded bits + SSE per
    // candidate). Much more accurate than the cost estimate (e.g. peppers qp15 RMSE 7.6→6.2 at −2.7% size), but
    // several times slower. Gated to TrueRdPixelBudget so very large frames keep the fast estimate path. The
    // budget covers typical photos (~2.5 MP): true-RD is a measured −1 to −2.8% on the 1.5 MP landscape test at
    // the cost of ~15 s, and the payoff (partitions the estimate over-splits) grows with frame detail. The path
    // is byte-identical to the small-frame one (ffmpeg/libdav1d-verified), just applied to more blocks.
    internal static bool UseTrueRd { get => Sp.UseTrueRd; set => Sp.UseTrueRd = value; }
    internal static bool UseCfl { get => Sp.UseCfl; set => Sp.UseCfl = value; }

    // Transform-size selection (tx_depth) in the COLOUR luma path — the grayscale path already does this. When on,
    // the colour frame header sets tx_mode=SELECT and every colour luma block codes a tx_size symbol (square leaves
    // search depth 0..2 by SSE+λ·bits; rect leaves keep their single rect transform = depth 0). Verified byte-exact
    // in dav1d/ffmpeg (peppers qp10, 1300+ depth>0 blocks). DEFAULT OFF: measured net-neutral-to-slightly-negative
    // overhead. ENABLED 2026-09 after two fixes made it a net win: (1) the depth>0 tx-split blocks are now RDOQ'd
    // (was round-to-nearest), and (2) the tx-depth decision (EstimateTxDepthCostColor) RDOQs its per-depth trial
    // coefficients so it compares fairly against the RDOQ'd large transform instead of over-splitting. On the
    // 6-image corpus (with the retuned lambdas): -3.4% BD-rate average — big on graphics/screen content
    // (logo/piechart ~-8.8%, wizard -3%), neutral on natural photos (mountains -0.2%, bluebells/granite ~+0.1%
    // noise). tx_size bitstream verified byte-exact in dav1d/ffmpeg.
    internal static bool UseColorTxDepth { get => Sp.UseColorTxDepth; set => Sp.UseColorTxDepth = value; }
    internal static long TrueRdPixelBudget { get => Sp.TrueRdPixelBudget; set => Sp.TrueRdPixelBudget = value; }
    internal static double EarlyTermBits { get => Sp.EarlyTermBits; set => Sp.EarlyTermBits = value; }

    // Intra edge filtering + upsampling for directional prediction (AV1 enable_intra_edge_filter). The decoder
    // already implements it fully (Av1IntraPred.PredZ1/Z2/Z3 do the filter/upsample, gated on bit 10 of `angle`;
    // the Z2 corner filter on PrepareIntraEdges' filterEdge). Enabling it: set the seq-header flag and have the
    // encoder OR the intra flags (bit 10 = enable, bit 9 = smooth-neighbour, mirroring the decoder's SmFlag) into
    // the resolved angle + pass filterEdge:true. Improves the directional modes' prediction quality.
    internal static bool UseIntraEdgeFilter { get => Sp.UseIntraEdgeFilter; set => Sp.UseIntraEdgeFilter = value; }
    private const int EdgeFilterEnableBit = 1 << 10;
    private const int SmoothNeighbourBit = 1 << 9;

    // Per-block intra flags OR'd into the prediction angle: the edge-filter-enable bit (when the seq flag is on)
    // plus the smooth-neighbour bit if either the above or left neighbour used a SMOOTH mode (mirrors the decoder's
    // SmFlag, which drives the filter-strength / upsample decision). Neighbours are intra on a key frame.
    private static int IntraEdgeFlags(int aboveMode, int leftMode)
    {
        if (!UseIntraEdgeFilter) return 0;
        int f = EdgeFilterEnableBit;
        if (IsSmoothMode(aboveMode) || IsSmoothMode(leftMode)) f |= SmoothNeighbourBit;
        return f;
    }
    private static bool IsSmoothMode(int m) =>
        m == (int)Av1IntraPredMode.Smooth || m == (int)Av1IntraPredMode.SmoothV || m == (int)Av1IntraPredMode.SmoothH;

    // Rate-DISTORTION coding-cost estimate for a luma block (see EncodePartition). Reconstructs the block through
    // the decoder's own inverse and returns J = SSE + λ·rate — so a 64x64 (or 32x32) transform that drops the
    // high-frequency detail of a sharp block is penalised by its reconstruction error, not just its (small) rate.
    // Used for the NONE/SPLIT decision in both the grayscale and colour encoders (the tree is luma-driven).
    private static long EstimateBlockCost(ushort[] luma, int w, int bw4, int bh4, int dcDq, int acDq, ushort[] scratch,
        int bl, int bx4, int by4)
    {
        int n = (32 >> bl) * 4;
        int tx = BlToTx(bl);
        var (_, _, satd) = ChooseIntraMode(luma, w, bw4, bh4, bx4, by4, n, luma, w, bx4 * 4, by4 * 4, scratch);
        if (Sp.EstimateSatdOnly) return satd + (long)(Math.Sqrt(LamK) * acDq * HeaderCostBits);
        if (Sp.EstimateCoefDist && n <= 32)
        {
            int scanLen = Av1Tables.Scans[tx].Length;
            var residual = Av1FwdTransform.RentLevels(n * n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) residual[y * n + x] = luma[(by4 * 4 + y) * w + bx4 * 4 + x] - scratch[y * n + x];
            var qf = t_estQf ??= new double[1024];
            int[] cq = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, Av1FwdTransform.FwdTxType.DctDct, qf);
            Av1FwdTransform.ReturnLevels(residual);
            long cbits = HeaderCostBits;
            double dist = 0;
            for (int i = 0; i < scanLen; i++)
            {
                int v = cq[i];
                if (v != 0) { int a = Math.Abs(v); cbits += 5 + (a >= 15 ? 8 : a >> 1); }
                double e = (qf[i] - v) * (i == 0 ? dcDq : acDq);
                dist += e * e;
            }
            Av1FwdTransform.ReturnLevels(cq);
            return (long)(dist * Sp.EstimateCoefDistScale + LamK * acDq * acDq * cbits);
        }
        int[] coeffs = ForwardResidualPred(luma, w, bx4 * 4, by4 * 4, scratch, n, dcDq, acDq, Av1Tables.Scans[tx].Length);
        long bits = HeaderCostBits;
        foreach (int v in coeffs)
            if (v != 0) { int a = Math.Abs(v); bits += 5 + (a >= 15 ? 8 : a >> 1); } // base+sign ~5b, magnitude tail

        // Reconstruct through the decoder's inverse (from the same source-plane prediction) and measure SSE.
        var reconTmp = new ushort[n * n];
        DequantAndReconstructPred(coeffs, tx, n, dcDq, acDq, scratch, reconTmp, n, 0, 0);
        long sse = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int d = reconTmp[y * n + x] - luma[(by4 * 4 + y) * w + (bx4 * 4 + x)];
                sse += (long)d * d;
            }

        double lambda = LamK * acDq * acDq;
        return sse + (long)(lambda * bits);
    }

    // Intra modes tried per block, each as (mode, angle_delta). All are verified against libdav1d/ffmpeg.
    // The kf-y-mode symbol is coded with nsym = NumIntraPredModes - 1 = 12 (dav1d convention for a 13-symbol
    // alphabet, values 0..12), so Paeth (mode 12) round-trips like any other mode.
    //
    // Directional candidates are restricted to a "safe" subset whose prediction angle stays in [90,180], so the
    // implementation mode is ImplVert / ImplHor / ImplZ2 — all of which need only top/left/top-left edges, never
    // top-right or bottom-left. That makes the block's edge-availability flags (sbHasTr / sbHasBl, which depend on
    // frame-level superblock ordering) irrelevant to the prediction, so the encoder reproduces the decoder's
    // output bit-for-bit without replicating that logic. Directional prediction reuses the decoder's own
    // PrepareIntraEdges (which folds angle_delta into the base angle) + Av1IntraPred.Predict. angle_delta ∈ [-3,3]
    // is coded via AngleDeltaCdf[mode-Vertical], symbol = delta + 3, nsym 6 (7 symbols), for blocks ≥ 8x8.
    private static (Av1IntraPredMode Mode, int Delta)[] CandidateModes => Sp.Candidates;


    // How many SATD-best modes the RD leaf search fully rate-evaluates (of ~61 candidates: DC/Smooth/SmoothV/
    // SmoothH/Paeth + 8 directional × 7 angle_deltas). The SATD prescreen is an imperfect proxy, so it discards
    // true-RD winners at small counts: a corpus sweep (2026-09-16) found 4→8 = -0.31%, 8→16 = -0.74%, 16→32 =
    // -0.79% (saturated) BD-rate. 16 is the knee — essentially all the quality for ~1/4 the RD cost of an
    // exhaustive search. Higher trades encode time for <0.1%.
    internal static int RdModeCandidates { get => Sp.RdModeCandidates; set => Sp.RdModeCandidates = value; }
    internal static bool UseRdoq { get => Sp.UseRdoq; set => Sp.UseRdoq = value; }
    internal static bool UseTxTypeSearch { get => Sp.UseTxTypeSearch; set => Sp.UseTxTypeSearch = value; }
    internal static bool UseLoopRestoration { get => Sp.UseLoopRestoration; set => Sp.UseLoopRestoration = value; }
    internal static int LrSgrSets { get => Sp.LrSgrSets; set => Sp.LrSgrSets = value; }
    internal static int LrUnitShiftMask { get => Sp.LrUnitShiftMask; set => Sp.LrUnitShiftMask = value; }
    internal static int LrWienerRounds { get => Sp.LrWienerRounds; set => Sp.LrWienerRounds = value; }
    internal static bool LrVerify { get => Sp.LrVerify; set => Sp.LrVerify = value; }

    internal static int LrStatsStep { get => Sp.LrStatsStep; set => Sp.LrStatsStep = value; }
    internal static long FilterSearchMaxPixels { get => Sp.FilterSearchMaxPixels; set => Sp.FilterSearchMaxPixels = value; }
    internal static bool FilterPickFromQ { get => Sp.FilterPickFromQ; set => Sp.FilterPickFromQ = value; }
    internal static bool FilterSearchFast { get => Sp.FilterSearchFast; set => Sp.FilterSearchFast = value; }
    internal static bool UsePartition64 { get => Sp.UsePartition64; set => Sp.UsePartition64 = value; }
    internal static int AngleDeltaSet { get => Sp.AngleDeltaSet; set => Sp.SetAngleDeltas(value); }

    // Dev/conformance isolation: when set, only luma intra candidates passing the filter are considered (square,
    // rect and sub-8x8 leaves). Null in production.
    internal static Func<Av1IntraPredMode, int, bool>? DbgLumaModeFilter;

    // Chroma UV-mode search: try directional/Smooth/Paeth UV predictions (not just DC/CfL) so sharp colour
    // boundaries stop paying full chroma residual. SAD-prescreen to this many candidates for the full chroma RD.
    internal static bool UseUvModeSearch { get => Sp.UseUvModeSearch; set => Sp.UseUvModeSearch = value; }
    internal static int RdUvCandidates { get => Sp.RdUvCandidates; set => Sp.RdUvCandidates = value; }

    // Full intra transform set (reduced_tx_set=0): adds V_DCT/H_DCT (1D DCT) for sub-16x16 luma, fitting sharp
    // horizontal/vertical edges (piechart wedges, logo edges) with less residual. When on, the colour path's seq
    // header codes reduced_tx_set=0 and every sub-16x16 luma tx codes the 7-type Intra1 symbol.
    internal static bool UseFullIntraTxSet { get => Sp.UseFullIntraTxSet; set => Sp.UseFullIntraTxSet = value; }

    // Extended T-shape partitions (HORZ_A/B, VERT_A/B) at 32x32/16x16 — libaom uses the full 10-type partition set;
    // we default to the 4 basic ones. These add candidates to the true-RD partition search (quarter squares + half
    // rects). Byte-exact: the sub-block order + edge availability mirror our decoder's Av1Decode recursion.
    internal static bool UseExtPartition { get => Sp.UseExtPartition; set => Sp.UseExtPartition = value; }

    // Full-set (Intra1) tx candidates for the square depth-0 luma leaf: the 5 reduced types + V_DCT/H_DCT.
    // Idx is the Intra2 index (mapped to Intra1 at emit time); V/H_DCT carry -1 and route through EncodeCoefs1D.
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] IntraTxTypesFull =
    {
        (Av1FwdTransform.FwdTxType.Identity, Av1TxType.Identity, 0),
        (Av1FwdTransform.FwdTxType.DctDct,   Av1TxType.DctDct,   1),
        (Av1FwdTransform.FwdTxType.AdstAdst, Av1TxType.AdstAdst, 2),
        (Av1FwdTransform.FwdTxType.AdstDct,  Av1TxType.AdstDct,  3),
        (Av1FwdTransform.FwdTxType.DctAdst,  Av1TxType.DctAdst,  4),
        (Av1FwdTransform.FwdTxType.VDct,     Av1TxType.VDct,    -1),
        (Av1FwdTransform.FwdTxType.HDct,     Av1TxType.HDct,    -1),
    };

    internal static (Av1IntraPredMode, int)[] BuildCandidates(int[] deltas)
    {
        var list = new List<(Av1IntraPredMode, int)>
        {
            (Av1IntraPredMode.Dc, 0), (Av1IntraPredMode.Smooth, 0),
            (Av1IntraPredMode.SmoothV, 0), (Av1IntraPredMode.SmoothH, 0), (Av1IntraPredMode.Paeth, 0),
        };
        // Full directional set: all 8 directional modes with the complete angle_delta range. Modes whose angle
        // needs the top-right / bottom-left edge are now correct because the leaf threads the real intra-edge
        // availability flags (from Av1IntraEdgeTree, mirroring the decoder) into PrepareIntraEdges.
        for (int m = (int)Av1IntraPredMode.Vertical; m <= (int)Av1IntraPredMode.VerticalLeft; m++)
            foreach (int d in deltas) list.Add(((Av1IntraPredMode)m, d));
        return list.ToArray();
    }

    // True for the 8 directional intra modes (Vertical..VerticalLeft) that carry an angle_delta symbol.
    private static bool IsDirectional(Av1IntraPredMode m) =>
        m >= Av1IntraPredMode.Vertical && m <= Av1IntraPredMode.VerticalLeft;

    // Predicts an n x n luma block with the given intra mode into dst (stride n), reusing the decoder's own edge
    // preparation + prediction so encoder and decoder agree bit-for-bit. recon is the reconstruction plane
    // (stride reconW), bx4/by4 the block position in 4-unit units, bw4/bh4 the frame size in 4-unit units.
    private static void PredictIntra(ushort[] recon, int reconW, int bw4, int bh4, int bx4, int by4, int n,
        Av1IntraPredMode mode, int delta, ushort[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<ushort> edge = stackalloc ushort[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = n >> 2;
        var tb = TileBounds4(reconW, bw4, bh4);
        bool haveTop = by4 > tb.Y0;
        bool haveLeft = bx4 > tb.X0;
        int angle = delta; // PrepareIntraEdges folds this into the base angle for directional modes
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, tb.X1, tb.Y1, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, tw4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, Bd);
        Av1IntraPred.Predict16(m, dst, n, edge, edgeCenter, n, n, angle | intraFlags,
            4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4, Bd);
    }

    // Chooses the intra mode with the lowest residual SATD (sum of absolute Hadamard-transformed differences) —
    // a frequency-domain cost proxy that tracks DCT coding cost far better than raw SAD, so smooth ramps and
    // directional edges are scored the way the transform will actually code them. Returns the winning (mode,
    // angle_delta) and writes its prediction into predOut (n x n). Purely an encoder decision: any candidate is
    // a valid mode, so this can never desync the decoder.
    private static (Av1IntraPredMode Mode, int Delta, long Cost) ChooseIntraMode(ushort[] recon, int reconW, int bw4, int bh4,
        int bx4, int by4, int n, ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] predOut,
        Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        long best = long.MaxValue;
        (Av1IntraPredMode Mode, int Delta) bestCand = (Av1IntraPredMode.Dc, 0);
        var tmp = new ushort[n * n];
        foreach ((Av1IntraPredMode mode, int delta) in CandidateModes)
        {
            if (Sp.EstimateNrdModes && !NrdModeAllowed(mode, n)) continue;
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, tmp, edgeFlags);
            long cost = Satd8x8(src, srcW, srcBx, srcBy, tmp, n);
            if (cost < best)
            {
                best = cost;
                bestCand = (mode, delta);
                Array.Copy(tmp, predOut, n * n);
            }
        }

        return (bestCand.Mode, bestCand.Delta, best);
    }

    // Sum of 8x8 Hadamard-transformed absolute residuals (src - pred) tiled over an n x n block. SATD is the
    // standard cheap frequency-domain proxy for transform coding cost.
    private static long Satd8x8(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int n)
    {
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            long sum = 0;
            for (int by = 0; by < n; by += 8)
                for (int bx = 0; bx < n; bx += 8)
                    sum += Hadamard8x8AbsAvx2(src, (srcBy + by) * srcW + srcBx + bx, srcW, pred, by * n + bx, n);
            return sum;
        }
        long total = 0;
        var d = new int[64];
        for (int by = 0; by < n; by += 8)
        {
            for (int bx = 0; bx < n; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        d[y * 8 + x] = src[(srcBy + by + y) * srcW + (srcBx + bx + x)] - pred[(by + y) * n + (bx + x)];
                total += Hadamard8x8Abs(d);
            }
        }

        return total;
    }

    // Sum of absolute differences over a cn x cn block (pred stride = cn). Works at any size (unlike the 8x8-tiled
    // SATD), so it prescreens chroma UV modes down to 4x4. A coarse proxy — good enough to pick the RD shortlist.
    private static long SadBlock(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int cn)
    {
        long total = 0;
        for (int y = 0; y < cn; y++)
        {
            int row = (srcBy + y) * srcW + srcBx;
            for (int x = 0; x < cn; x++) total += Math.Abs(src[row + x] - pred[y * cn + x]);
        }
        return total;
    }

    // SATD over a w x h rectangular block (pred stride = w). Rect leaf sizes are all multiples of 8, so the 8x8
    // Hadamard tiling is exact. Used to prescreen rect intra modes cheaply before the full RD tx-type search.
    private static long Satd8x8Rect(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int w, int h)
    {
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            long sum = 0;
            for (int by = 0; by < h; by += 8)
                for (int bx = 0; bx < w; bx += 8)
                    sum += Hadamard8x8AbsAvx2(src, (srcBy + by) * srcW + srcBx + bx, srcW, pred, by * w + bx, w);
            return sum;
        }
        long total = 0;
        var d = new int[64];
        for (int by = 0; by < h; by += 8)
            for (int bx = 0; bx < w; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        d[y * 8 + x] = src[(srcBy + by + y) * srcW + (srcBx + bx + x)] - pred[(by + y) * w + (bx + x)];
                total += Hadamard8x8Abs(d);
            }
        return total;
    }

    // In-place 8x8 Walsh–Hadamard transform (rows then columns) of d, returning the sum of absolute outputs.
    // Hadamard8x8Abs of (src - pred) with AVX2: the column butterflies combine the 8 row vectors, the row butterflies
    // run inside each vector (lane swaps at distance 4 / 2 / 1). Exact integer arithmetic, and the sum of absolute
    // values does not depend on the output order, so the result equals the scalar version's.
    private static long Hadamard8x8AbsAvx2(ReadOnlySpan<ushort> src, int srcOff, int srcW, ReadOnlySpan<ushort> pred, int predOff, int predW)
    {
        static Vector256<int> Row(ReadOnlySpan<ushort> s, int o, ReadOnlySpan<ushort> p, int po)
            => Avx2.Subtract(Avx2.ConvertToVector256Int32(Vector128.Create(s.Slice(o, 8))), Avx2.ConvertToVector256Int32(Vector128.Create(p.Slice(po, 8))));
        var a0 = Row(src, srcOff, pred, predOff);
        var a1 = Row(src, srcOff + srcW, pred, predOff + predW);
        var a2 = Row(src, srcOff + 2 * srcW, pred, predOff + 2 * predW);
        var a3 = Row(src, srcOff + 3 * srcW, pred, predOff + 3 * predW);
        var a4 = Row(src, srcOff + 4 * srcW, pred, predOff + 4 * predW);
        var a5 = Row(src, srcOff + 5 * srcW, pred, predOff + 5 * predW);
        var a6 = Row(src, srcOff + 6 * srcW, pred, predOff + 6 * predW);
        var a7 = Row(src, srcOff + 7 * srcW, pred, predOff + 7 * predW);
        Vector256<int> b0 = a0 + a4, b1 = a1 + a5, b2 = a2 + a6, b3 = a3 + a7, b4 = a0 - a4, b5 = a1 - a5, b6 = a2 - a6, b7 = a3 - a7;
        Vector256<int> c0 = b0 + b2, c1 = b1 + b3, c2 = b0 - b2, c3 = b1 - b3, c4 = b4 + b6, c5 = b5 + b7, c6 = b4 - b6, c7 = b5 - b7;
        static Vector256<int> H(Vector256<int> x)
        {
            var y = Avx2.Permute2x128(x, x, 1);
            x = Avx2.Blend(x + y, y - x, 0b11110000);
            y = Avx2.Shuffle(x, 0b01_00_11_10);
            x = Avx2.Blend(x + y, y - x, 0b11001100);
            y = Avx2.Shuffle(x, 0b10_11_00_01);
            return Avx2.Blend(x + y, y - x, 0b10101010);
        }
        var acc = Avx2.Abs(H(c0 + c1)) + Avx2.Abs(H(c0 - c1)) + Avx2.Abs(H(c2 + c3)) + Avx2.Abs(H(c2 - c3))
                + Avx2.Abs(H(c4 + c5)) + Avx2.Abs(H(c4 - c5)) + Avx2.Abs(H(c6 + c7)) + Avx2.Abs(H(c6 - c7));
        return Vector256.Sum(acc);
    }

    private static long Hadamard8x8Abs(int[] d)
    {
        Span<int> t = stackalloc int[64];
        for (int i = 0; i < 8; i++) Hadamard8(d, i * 8, 1, t, i * 8, 1);
        for (int i = 0; i < 8; i++) Hadamard8(t, i, 8, d, i, 8);
        long s = 0;
        for (int i = 0; i < 64; i++) s += Math.Abs(d[i]);
        return s;
    }

    // One 8-point Walsh–Hadamard butterfly from in[inOff + k*inStride] to out[outOff + k*outStride].
    private static void Hadamard8(Span<int> input, int inOff, int inStride, Span<int> output, int outOff, int outStride)
    {
        int a0 = input[inOff], a1 = input[inOff + inStride], a2 = input[inOff + 2 * inStride], a3 = input[inOff + 3 * inStride];
        int a4 = input[inOff + 4 * inStride], a5 = input[inOff + 5 * inStride], a6 = input[inOff + 6 * inStride], a7 = input[inOff + 7 * inStride];
        int b0 = a0 + a4, b1 = a1 + a5, b2 = a2 + a6, b3 = a3 + a7;
        int b4 = a0 - a4, b5 = a1 - a5, b6 = a2 - a6, b7 = a3 - a7;
        int c0 = b0 + b2, c1 = b1 + b3, c2 = b0 - b2, c3 = b1 - b3;
        int c4 = b4 + b6, c5 = b5 + b7, c6 = b4 - b6, c7 = b5 - b7;
        output[outOff] = c0 + c1;
        output[outOff + outStride] = c0 - c1;
        output[outOff + 2 * outStride] = c2 + c3;
        output[outOff + 3 * outStride] = c2 - c3;
        output[outOff + 4 * outStride] = c4 + c5;
        output[outOff + 5 * outStride] = c4 - c5;
        output[outOff + 6 * outStride] = c6 + c7;
        output[outOff + 7 * outStride] = c6 - c7;
    }

    // DC prediction for a 64x64 block from reconstructed neighbours, mirroring Av1IntraPred DC modes.
    private static int DcPredict(ushort[] recon, int w, int h, int bx, int by, int bw, int bh)
    {
        var tb = TileBounds4(w, int.MaxValue, int.MaxValue);
        bool haveTop = by > tb.Y0 * 4;
        bool haveLeft = bx > tb.X0 * 4;
        if (haveTop && haveLeft)
        {
            int dc = (bw + bh) >> 1;
            for (int x = 0; x < bw; x++) dc += recon[(by - 1) * w + bx + x];
            for (int y = 0; y < bh; y++) dc += recon[(by + y) * w + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)(bw + bh));
        }

        if (haveTop)
        {
            int dc = bw >> 1;
            for (int x = 0; x < bw; x++) dc += recon[(by - 1) * w + bx + x];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)bw);
        }

        if (haveLeft)
        {
            int dc = bh >> 1;
            for (int y = 0; y < bh; y++) dc += recon[(by + y) * w + bx - 1];
            return dc >> System.Numerics.BitOperations.TrailingZeroCount((uint)bh);
        }

        return PixMid;
    }

    // Dequantizes the quantized levels the way the decoder does, inverse-transforms onto the DC prediction (via
    // the decoder's own InvTxfmAdd) to reconstruct the 64x64 block into `recon`, and returns the coefficient
    // context byte (cul_level | dc-sign) that neighbours read.
    private static byte DequantAndReconstruct(int[] levels, int dcDq, int acDq, int dcPred, ushort[] recon, int w, int bx, int by)
        => DequantAndReconstruct(levels, Tx64x64, 64, dcDq, acDq, dcPred, recon, w, bx, by);

    // Dequantizes quantized levels (decoder-exact), inverse-transforms onto the DC prediction (via the decoder's
    // own InvTxfmAdd) to reconstruct an n x n block of transform <paramref name="tx"/> into <paramref
    // name="recon"/> (stride <paramref name="reconW"/>), and returns the block's coefficient context byte.
    private static byte DequantAndReconstruct(int[] levels, int tx, int n, int dcDq, int acDq, int dcPred,
        ushort[] recon, int reconW, int bx, int by)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        int cfMax = CfMax;
        var scan = Av1Tables.Scans[tx];

        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
        {
            if (levels[scan[i]] != 0) { eob = i; break; }
        }

        var cf = t_reconCf ??= new int[64 * 64];   // left all-zero by InvTxfmAdd16
        int culLevel = 0;
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl);
            int sign = lvl < 0 ? 1 : 0;
            int dq = ((rc == 0 ? dcDq : acDq) * mag) >> dqShift;
            dq = Math.Min(dq, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
            culLevel += mag;
        }

        int dcSignLevel = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        byte cfCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);

        var block = new ushort[n * n];
        Array.Fill(block, (ushort)dcPred);
        Av1InvTransform.InvTxfmAdd16(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], Av1TxType.DctDct, Bd);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = block[y * n + x];
            }
        }

        return cfCtx;
    }

    // Dequantizes and reconstructs an n x n block on top of an arbitrary intra prediction (predBlock, n x n),
    // via the decoder's InvTxfmAdd, into recon. Returns the coefficient-context byte.
    private static byte DequantAndReconstructPred(int[] levels, int tx, int n, int dcDq, int acDq,
        ushort[] predBlock, ushort[] recon, int reconW, int bx, int by, Av1TxType txType = Av1TxType.DctDct)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[tx].Ctx - 2);
        int cfMax = CfMax;
        var scan = Av1Tables.Scans[tx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
        {
            if (levels[scan[i]] != 0) { eob = i; break; }
        }

        var cf = t_reconCf ??= new int[64 * 64];   // left all-zero by InvTxfmAdd16
        int culLevel = 0;
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl);
            int sign = lvl < 0 ? 1 : 0;
            int dq = ((rc == 0 ? dcDq : acDq) * mag) >> dqShift;
            dq = Math.Min(dq, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
            culLevel += mag;
        }

        int dcSignLevel = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        byte cfCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);

        var block = (t_reconBlock ??= new ushort[64 * 64]).AsSpan(0, n * n);
        predBlock.AsSpan(0, n * n).CopyTo(block);
        Av1InvTransform.InvTxfmAdd16(block, n, cf, eob, tx, Av1InvTransform.TxShift[tx], txType, Bd);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                recon[(by + y) * reconW + (bx + x)] = block[y * n + x];
            }
        }

        return cfCtx;
    }

    // Reconstruction SSE of an n x n luma candidate: dequant `levels` (mirrors the decoder), inverse-transform onto
    // `predBlock`, and sum squared error vs the source. Used to score tx-type / mode candidates by true RD (D + λR)
    // rather than rate alone — necessary because IDTX changes the reconstruction distortion at a matched quantizer
    // (unlike the DCT/ADST family), so a rate-only comparison over-selects it on smooth content.
    private static long ReconSseCand(int[] levels, int tx, int n, int dcDq, int acDq, ushort[] predBlock,
        ushort[] src, int srcW, int srcBx, int srcBy, Av1TxType txType)
        => ReconSseCandRect(levels, tx, n, n, dcDq, acDq, predBlock, src, srcW, srcBx, srcBy, txType);

    // J (pre-RDOQ) of the last ChooseLeafRdCore winner, for the palette comparison.
    [ThreadStatic] private static double t_leafJ;

    // Per-thread working buffers of a leaf search (ChooseLeafRdCore / the rect leaf / the sub-8 leaf, one set each: none
    // of them re-enters itself), sized for the largest block.
    private sealed class LeafScratch
    {
        public readonly ushort[] P1 = new ushort[64 * 64], P2 = new ushort[64 * 64];
        public readonly int[] R = new int[64 * 64];
        public readonly double[] Q1 = new double[1024], Q2 = new double[1024];
    }
    [ThreadStatic] private static LeafScratch? t_scCore, t_scRect, t_scSub8, t_scSplit;
    [ThreadStatic] private static double[]? t_estQf;
    // The last ChooseLeafRdCore prescreen's surviving candidates (CandidateModes indices, SATD order).
    [ThreadStatic] private static int[]? t_topModesArr;
    private static int[] t_topModes => t_topModesArr ??= new int[64];
    [ThreadStatic] private static int t_topModeCount;

    // Per-thread scratch for the candidate reconstructions (the encoder's hottest loop): the coefficient buffer is
    // always all zero between calls — the inverse transform clears the region it consumed, as dav1d's does — so no
    // allocation or clearing is needed per candidate.
    [ThreadStatic] private static int[]? t_reconCf;
    [ThreadStatic] private static ushort[]? t_reconBlock;

    // Forward-transforms and quantizes (src block - prediction) for an n x n block.
    // The reduced intra tx set (Intra2) types searched for luma tx ≤ 16x16 (where the type is signalled), as
    // (forward type, inverse type, symbol index in TxTypesPerSet Intra2). DctDct is idx 1; ADST combos 2/3/4.
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] IntraTxTypes =
    {
        (Av1FwdTransform.FwdTxType.Identity, Av1TxType.Identity, 0),
        (Av1FwdTransform.FwdTxType.DctDct,   Av1TxType.DctDct,   1),
        (Av1FwdTransform.FwdTxType.AdstAdst, Av1TxType.AdstAdst, 2),
        (Av1FwdTransform.FwdTxType.AdstDct,  Av1TxType.AdstDct,  3),
        (Av1FwdTransform.FwdTxType.DctAdst,  Av1TxType.DctAdst,  4),
    };
    private static readonly (Av1FwdTransform.FwdTxType Fwd, Av1TxType Inv, int Idx)[] DctOnly =
        { (Av1FwdTransform.FwdTxType.DctDct, Av1TxType.DctDct, 1) };

    // Forward transform type for a chosen intra tx-type index (1=Dct_Dct, 2=Adst_Adst, 3=Adst_Dct, 4=Dct_Adst),
    // used to re-derive the pre-quant floats (qf) for RDOQ of a tx-block whose type ChooseTxType already picked.
    private static Av1FwdTransform.FwdTxType FwdTypeForIdx(int idx) => idx switch
    {
        0 => Av1FwdTransform.FwdTxType.Identity,
        2 => Av1FwdTransform.FwdTxType.AdstAdst,
        3 => Av1FwdTransform.FwdTxType.AdstDct,
        4 => Av1FwdTransform.FwdTxType.DctAdst,
        _ => Av1FwdTransform.FwdTxType.DctDct,
    };

    // Forward-transform kernel matching a derived Av1TxType (used for chroma, whose tx-type comes from the UV mode).
    private static Av1FwdTransform.FwdTxType FwdTypeForTxType(Av1TxType t) => t switch
    {
        Av1TxType.Identity => Av1FwdTransform.FwdTxType.Identity,
        Av1TxType.AdstAdst => Av1FwdTransform.FwdTxType.AdstAdst,
        Av1TxType.AdstDct => Av1FwdTransform.FwdTxType.AdstDct,
        Av1TxType.DctAdst => Av1FwdTransform.FwdTxType.DctAdst,
        _ => Av1FwdTransform.FwdTxType.DctDct,
    };

    // Gray wrapper for the primitive-arg RD leaf decision.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRd(GrayPartCtx c, int bx4, int by4, int n, int tx, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None)
    {
        int bxR = bx4 & 31, byR = by4 & 31;
        return ChooseLeafRdCore(c.Recon, c.W, c.Bw4, c.Bh4, c.Luma, c.W, bx4, by4, n, tx, c.DcDq, c.AcDq,
            c.Cdf, c.AboveMode[bxR], c.LeftMode[byR], c.AboveLCoef.AsSpan(bxR), c.LeftLCoef.AsSpan(byR), c.Pred, edgeFlags);
    }

    // Rate-distortion leaf decision: over all candidate (intra mode, tx type) pairs, pick the one with the lowest
    // actual coded rate — coefficient bits (EstimateCoefBits, from the live CDFs) plus the mode/angle signalling
    // bits. This replaces the SATD proxy: it directly minimises what the bitstream costs and lets the tx-type
    // (ADST/DCT) choice compound with the mode choice. Writes the winning prediction into predOut.
    private static (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx)
        ChooseLeafRdCore(ushort[] recon, int reconW, int bw4, int bh4, ushort[] luma, int lumaW, int bx4, int by4,
            int n, int tx, int dcDq, int acDq, Av1CdfContext cdf, byte aboveMode, byte leftMode,
            ReadOnlySpan<byte> aboveLCoef, ReadOnlySpan<byte> leftLCoef, ushort[] predOut,
            Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, bool filterEligible = false, int bs = 0, bool fullSet = false)
    {
        // Full intra set adds V_DCT/H_DCT for sub-16x16 luma (n in {4,8}); 16x16 and up stay on the reduced set.
        var txSet = !Sp.UseTxTypeSearch ? DctOnly : (fullSet && n <= 8) ? IntraTxTypesFull : (n <= 16 ? IntraTxTypes : DctOnly);
        int aboveCtx = Av1Tables.IntraModeContext[aboveMode];
        int leftCtx = Av1Tables.IntraModeContext[leftMode];
        Span<ushort> ymCdf = cdf.GetKfYModeCdf(aboveCtx, leftCtx);
        int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, aboveLCoef, leftLCoef);
        int intraFlags = IntraEdgeFlags(aboveMode, leftMode);
        int scanLen = Av1Tables.Scans[tx].Length;
        var scr = t_scCore ??= new LeafScratch();
        var predBuf = scr.P1;
        var qfCand = scr.Q1;
        var qfWin = scr.Q2;
        // The winner's prediction stays in predBuf until the next mode would overwrite it (then the buffers swap), and
        // the pre-quant floats swap buffers too: no copy per improvement, one to predOut at the end.
        var predBest = scr.P2;
        bool bestInPredBuf = false;

        // Pre-screen all candidate modes by cheap SATD and RD-evaluate only the best few — the full rate search
        // (forward transform + EstimateCoefBits over every tx-type) is the encoder's hot loop, and SATD tracks the
        // eventual coded cost closely enough that the top handful almost always contains the RD winner.
        Span<int> topIdx = stackalloc int[RdModeCandidates];
        Span<long> topCost = stackalloc long[RdModeCandidates];
        topCost.Fill(long.MaxValue);
        double satdLambda = Math.Sqrt(LamK) * acDq; // ~rate weight in SATD units
        uint hogMask = HogSkipMask(luma, lumaW, bx4 * 4, by4 * 4, n, n, bw4 * 4, bh4 * 4, Sp.HogLevel);
        int refine = Sp.AngleRefineTop;
        Span<long> baseCost = stackalloc long[16];
        baseCost.Fill(long.MaxValue);
        for (int pass = 0; pass < (refine > 0 ? 2 : 1); pass++)
        {
            uint dirMask = pass == 1 ? TopDirectional(baseCost, refine) : 0;
            for (int ci = 0; ci < CandidateModes.Length; ci++)
            {
                (Av1IntraPredMode mode, int delta) = CandidateModes[ci];
                if (DbgLumaModeFilter != null && !DbgLumaModeFilter(mode, delta)) continue;
                if (refine > 0 && (pass == 0 ? delta != 0 : delta == 0 || (dirMask >> (int)mode & 1) == 0)) continue;
                if ((hogMask >> (int)mode & 1) != 0) continue;
                if (Sp.LeafNrdModes && !NrdModeAllowed(mode, n)) continue;
                PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, predBuf, edgeFlags, intraFlags);
                long satd = Satd8x8(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
                long mb = (long)(satdLambda * (Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                    + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0)));
                long cost = satd + mb;
                if (delta == 0) baseCost[(int)mode] = cost;
                for (int k = 0; k < RdModeCandidates; k++)
                    if (cost < topCost[k]) { for (int j = RdModeCandidates - 1; j > k; j--) { topCost[j] = topCost[j - 1]; topIdx[j] = topIdx[j - 1]; } topCost[k] = cost; topIdx[k] = ci; break; }
            }
        }

        t_topModeCount = 0;
        for (int t = 0; t < RdModeCandidates && t < t_topModes.Length; t++)
            if (topCost[t] != long.MaxValue) t_topModes[t_topModeCount++] = topIdx[t];
        double best = double.MaxValue;
        double rdLambda = LamK * acDq * acDq;   // pixel-SSE units per bit (same λ as the palette RD gate)
        double rdoqLambda = RdoqScale * LamK * acDq * acDq;
        (Av1IntraPredMode Mode, int Delta, int[] Coeffs, Av1TxType Inv, int Idx) bestCand = default;
        bool fastTx = Sp.FastIntraTxType;
        for (int pass = 0; pass < (fastTx ? 2 : 1); pass++)
        for (int t = 0; t < RdModeCandidates; t++)
        {
            if (topCost[t] == long.MaxValue) break;
            (Av1IntraPredMode mode, int delta) = CandidateModes[topIdx[t]];
            if (pass == 1 && (mode != bestCand.Mode || delta != bestCand.Delta || bestCand.Coeffs == null)) continue;
            if (bestInPredBuf) { (predBuf, predBest) = (predBest, predBuf); bestInPredBuf = false; }
            PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, mode, delta, predBuf, edgeFlags, intraFlags);
            int[] residual = ComputeResidualPred(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
            double modeBits = Av1CoeffEncode.SymBits(ymCdf, (int)mode)
                + (IsDirectional(mode) ? Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), delta + 3) : 0);
            // A DC-coded block also emits use_filter_intra=0 when filter is enabled — charge that bit for fairness.
            if (filterEligible && mode == Av1IntraPredMode.Dc)
                modeBits += Av1CoeffEncode.SymBits(cdf.GetFilterIntraCdf((Av1BlockSize)bs), 0);
            foreach (var (fwd, inv, idx) in txSet)
            {
                if (fastTx && (pass == 0) != (inv == Av1TxType.DctDct)) continue;
                int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                long sse = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv);
                // Distortion alone already loses (rate >= 0): skip the rate estimate (exact).
                if (!Sp.RdoqInSearch && sse + rdLambda * modeBits >= best) { Av1FwdTransform.ReturnLevels(cf); continue; }
                double rate = (oneD
                    ? Av1CoeffEncode.EstimateCoefBits1D(cdf.Coef, cdf.Mode, tx, (int)mode, inv, cf, 0, dcSignCtx)
                    : Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, 0, dcSignCtx, idx, fullSet: fullSet)) + modeBits;
                double j = sse + rdLambda * rate;   // true RD: distortion + λ·rate (IDTX changes distortion, so rate alone misranks it)
                if (Sp.RdoqInSearch && !oneD && j < best * Sp.RdoqSearchMargin)
                {
                    // price the candidate on the levels RDOQ would code
                    Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, qfCand, dcDq, acDq, 0, dcSignCtx, idx, rdoqLambda);
                    rate = Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, (int)mode, cf, 0, dcSignCtx, idx, fullSet: fullSet) + modeBits;
                    j = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv) + rdLambda * rate;
                }
                if (j < best) { Av1FwdTransform.ReturnLevels(bestCand.Coeffs); best = j; bestCand = (mode, delta, cf, inv, idx); bestInPredBuf = true; (qfCand, qfWin) = (qfWin, qfCand); }
                else Av1FwdTransform.ReturnLevels(cf);
            }
            Av1FwdTransform.ReturnLevels(residual);
        }

        // Filter-intra candidates: the 5 recursive-filter predictors, coded as y_mode=DC + use_filter_intra=1 +
        // filter_mode. The tx-type coefficient context uses FilterModeToYMode (dav1d recon_tmpl); a filter winner
        // sets Mode=Filter and the caller stores DC for the neighbour mode context (dav1d decode.c).
        if (filterEligible)
        {
            Span<ushort> fiCdf = cdf.GetFilterIntraCdf((Av1BlockSize)bs);
            double flagBits = Av1CoeffEncode.SymBits(fiCdf, 1) + Av1CoeffEncode.SymBits(ymCdf, (int)Av1IntraPredMode.Dc);
            int fiMask = Sp.FilterIntraPrune ? FilterIntraModesFor(bestCand.Mode) : 0x1F;
            for (int fm = 0; fm < 5; fm++)
            {
                if ((fiMask >> fm & 1) == 0) continue;
                if (bestInPredBuf) { (predBuf, predBest) = (predBest, predBuf); bestInPredBuf = false; }
                PredictIntra(recon, reconW, bw4, bh4, bx4, by4, n, Av1IntraPredMode.Filter, fm, predBuf, edgeFlags, intraFlags);
                int[] residual = ComputeResidualPred(luma, lumaW, bx4 * 4, by4 * 4, predBuf, n);
                int ymnf = Av1Tables.FilterModeToYMode[fm];
                double modeBits = flagBits + Av1CoeffEncode.SymBits(cdf.GetFilterIntraModeCdf(), fm);
                foreach (var (fwd, inv, idx) in txSet)
                {
                    int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd, qfCand);
                    bool oneD = inv == Av1TxType.VDct || inv == Av1TxType.HDct;
                    long sse = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv);
                    // Distortion alone already loses (rate >= 0): skip the rate estimate (exact).
                    if (!Sp.RdoqInSearch && sse + rdLambda * modeBits >= best) { Av1FwdTransform.ReturnLevels(cf); continue; }
                    double rate = (oneD
                        ? Av1CoeffEncode.EstimateCoefBits1D(cdf.Coef, cdf.Mode, tx, ymnf, inv, cf, 0, dcSignCtx)
                        : Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, ymnf, cf, 0, dcSignCtx, idx, fullSet: fullSet)) + modeBits;
                    double j = sse + rdLambda * rate;
                    if (Sp.RdoqInSearch && !oneD && j < best * Sp.RdoqSearchMargin)
                    {
                        Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, 0, ymnf, cf, qfCand, dcDq, acDq, 0, dcSignCtx, idx, rdoqLambda);
                        rate = Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, 0, ymnf, cf, 0, dcSignCtx, idx, fullSet: fullSet) + modeBits;
                        j = ReconSseCand(cf, tx, n, dcDq, acDq, predBuf, luma, lumaW, bx4 * 4, by4 * 4, inv) + rdLambda * rate;
                    }
                    if (j < best) { Av1FwdTransform.ReturnLevels(bestCand.Coeffs); best = j; bestCand = (Av1IntraPredMode.Filter, fm, cf, inv, idx); bestInPredBuf = true; (qfCand, qfWin) = (qfWin, qfCand); }
                    else Av1FwdTransform.ReturnLevels(cf);
                }
                Av1FwdTransform.ReturnLevels(residual);
            }
        }

        if (bestCand.Coeffs != null) Array.Copy(bestInPredBuf ? predBuf : predBest, predOut, n * n);
        t_leafJ = best;
        // RDOQ-refine the winning coefficients (encoder-only; decoder reconstructs from these same levels).
        // Skipped for V_DCT/H_DCT winners: RdoqOptimize assumes the 2D scan/contexts (EncodeCoefs1D codes the
        // 1D scan), so its rate model doesn't apply — the deadzone-quantized 1D levels are coded as-is.
        if (bestCand.Coeffs != null && bestCand.Inv != Av1TxType.VDct && bestCand.Inv != Av1TxType.HDct && !Sp.RdoqInSearch)
        {
            double lambda = RdoqScale * LamK * acDq * acDq;
            int rdoqMode = bestCand.Mode == Av1IntraPredMode.Filter ? Av1Tables.FilterModeToYMode[bestCand.Delta] : (int)bestCand.Mode;
            Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, 0, rdoqMode, bestCand.Coeffs, qfWin,
                dcDq, acDq, 0, dcSignCtx, bestCand.Idx, lambda);
        }

        return bestCand;
    }


    // === Rectangular block helpers (w x h, w != h) — mirror the square versions but keep width/height separate.
    // Used by PARTITION_HORZ / PARTITION_VERT leaves. Prediction/reconstruction reuse the decoder's own
    // PrepareIntraEdges + Av1IntraPred.Predict + InvTxfmAdd, so they are conformant by construction. ===

    // Intra prediction for a w x h block into dst (h rows x w cols, stride w).
    private static void PredictIntraRect(ushort[] recon, int reconW, int bw4, int bh4, int bx4, int by4,
        int w, int h, Av1IntraPredMode mode, int delta, ushort[] dst, Av1EdgeFlags edgeFlags = Av1EdgeFlags.None, int intraFlags = 0)
    {
        Span<ushort> edge = stackalloc ushort[257];
        const int edgeCenter = 128;
        int dstOff = (by4 * 4) * reconW + (bx4 * 4);
        int tw4 = w >> 2, th4 = h >> 2;
        var tb = TileBounds4(reconW, bw4, bh4);
        bool haveTop = by4 > tb.Y0, haveLeft = bx4 > tb.X0;
        int angle = delta;
        int m = Av1Reconstruction.PrepareIntraEdges(
            bx4, haveLeft, by4, haveTop, tb.X1, tb.Y1, edgeFlags,
            recon, dstOff, reconW, default, mode, ref angle, tw4, th4,
            filterEdge: (intraFlags & EdgeFilterEnableBit) != 0, edge, edgeCenter, Bd);
        Av1IntraPred.Predict16(m, dst, w, edge, edgeCenter, w, h, angle | intraFlags, 4 * bw4 - 4 * bx4, 4 * bh4 - 4 * by4, Bd);
    }

    // Forward+quant of (src - pred) for a w x h block. pred is h x w row-major.
    private static int[] ForwardResidualPredRect(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy,
        ushort[] pred, int w, int h, int txIdx, int dcDq, int acDq, int rcCount, double[]? qfOut = null,
        Av1FwdTransform.FwdTxType fwd = Av1FwdTransform.FwdTxType.DctDct)
    {
        var residual = Av1FwdTransform.RentLevels(h * w);   // fully written below
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                residual[y * w + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * w + x];
        var lv = Av1FwdTransform.ForwardQuantRect(residual, w, h, txIdx, dcDq, acDq, rcCount, fwd, qfOut);
        Av1FwdTransform.ReturnLevels(residual);
        return lv;
    }

    // Dequantizes rect levels and reconstructs a w x h block onto predBlock (h x w) via the decoder's InvTxfmAdd,
    // into recon. Returns the coefficient-context byte.
    // The above/left coefficient-context byte of a coded tx block (as DequantAndReconstructPredRect returns it).
    private static byte TxCoefCtx(int[] levels, int txIdx)
    {
        int n = Av1Tables.Scans[txIdx].Length, cul = 0;
        for (int i = 0; i < n; i++) cul += Math.Abs(levels[i]);
        return (byte)(Math.Min(cul, 63) | (levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80)));
    }

    private static byte DequantAndReconstructPredRect(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        ushort[] predBlock, ushort[] recon, int reconW, int bx, int by, Av1TxType txType = Av1TxType.DctDct)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[txIdx].Ctx - 2);
        int cfMax = CfMax;
        var scan = Av1Tables.Scans[txIdx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
            if (levels[scan[i]] != 0) { eob = i; break; }

        var cf = t_reconCf ??= new int[64 * 64];   // left all-zero by InvTxfmAdd16
        int culLevel = 0;
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl);
            int sign = lvl < 0 ? 1 : 0;
            int dq = ((rc == 0 ? dcDq : acDq) * mag) >> dqShift;
            dq = Math.Min(dq, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
            culLevel += mag;
        }

        int dcSignLevel = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        byte cfCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);

        var block = (t_reconBlock ??= new ushort[64 * 64]).AsSpan(0, w * h);
        predBlock.AsSpan(0, w * h).CopyTo(block);
        Av1InvTransform.InvTxfmAdd16(block, w, cf, eob, txIdx, Av1InvTransform.TxShift[txIdx], txType, Bd);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                recon[(by + y) * reconW + (bx + x)] = block[y * w + x];

        return cfCtx;
    }

    // Reconstruction SSE of a w x h rectangular candidate (dequant + inverse onto predBlock) vs the source — the
    // distortion term for true-RD rect leaf selection (mirrors ReconSseCand for the square path).
    private static long ReconSseCandRect(int[] levels, int txIdx, int w, int h, int dcDq, int acDq,
        ushort[] predBlock, ushort[] src, int srcW, int srcBx, int srcBy, Av1TxType txType)
    {
        int dqShift = Math.Max(0, Av1Tables.TxfmDimensions[txIdx].Ctx - 2);
        int cfMax = CfMax;
        var scan = Av1Tables.Scans[txIdx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--) if (levels[scan[i]] != 0) { eob = i; break; }
        var cf = t_reconCf ??= new int[64 * 64];
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i], lvl = levels[rc];
            if (lvl == 0) continue;
            int mag = Math.Abs(lvl), sign = lvl < 0 ? 1 : 0;
            int dq = Math.Min(((rc == 0 ? dcDq : acDq) * mag) >> dqShift, cfMax + sign);
            cf[rc] = sign != 0 ? -dq : dq;
        }
        var blockBuf = t_reconBlock ??= new ushort[64 * 64];
        var block = blockBuf.AsSpan(0, w * h);
        predBlock.AsSpan(0, w * h).CopyTo(block);
        Av1InvTransform.InvTxfmAdd16(block, w, cf, eob, txIdx, Av1InvTransform.TxShift[txIdx], txType, Bd);
        long sse = 0;
        for (int y = 0; y < h; y++)
        {
            var b = block.Slice(y * w, w);
            var sr = src.AsSpan((srcBy + y) * srcW + srcBx, w);
            for (int x = 0; x < w; x++) { int d = b[x] - sr[x]; sse += d * d; }
        }
        return sse;
    }

    // Residual (src - prediction) for an n x n block.
    private static int[] ComputeResidualPred(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy, ushort[] pred, int n)
    {
        var r = Av1FwdTransform.RentLevels(n * n);   // fully written below
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                r[y * n + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * n + x];
        return r;
    }

    // Coefficient coding-cost proxy (bit-ish): per nonzero base+sign + magnitude tail.
    private static long CoeffCost(int[] coeffs)
    {
        long bits = 0;
        foreach (int v in coeffs) if (v != 0) { int a = Math.Abs(v); bits += 5 + (a >= 15 ? 8 : a >> 1); }
        return bits;
    }

    // Chooses the intra transform type minimizing coefficient cost for a residual. For luma tx ≤ 16x16 the type is
    // signalled (Intra2 set: DctDct + ADST combos); for tx ≥ 32x32 DctDct is forced. Returns the quantized coeffs,
    // the inverse type for reconstruction, and the Intra2 symbol index to code.
    private static (int[] Coeffs, Av1TxType Inv, int Idx) ChooseTxType(int[] residual, int n, int tx, int dcDq, int acDq,
        ushort[] predBlock, ushort[] src, int srcW, int srcBx, int srcBy, double rdLambda)
    {
        int scanLen = Av1Tables.Scans[tx].Length;
        if (n > 16)
            return (Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen), Av1TxType.DctDct, 1);

        double best = double.MaxValue;
        (int[], Av1TxType, int) bestCand = default;
        foreach (var (fwd, inv, idx) in IntraTxTypes)
        {
            int[] cf = Av1FwdTransform.ForwardQuantTyped(residual, n, dcDq, acDq, scanLen, fwd);
            // True RD: distortion + λ·rate. Rate alone over-selects IDTX on smooth content (see ChooseLeafRdCore).
            double j = ReconSseCand(cf, tx, n, dcDq, acDq, predBlock, src, srcW, srcBx, srcBy, inv) + rdLambda * CoeffCost(cf);
            if (j < best) { best = j; bestCand = (cf, inv, idx); }
        }

        return bestCand;
    }

    private static int[] ForwardResidualPred(ReadOnlySpan<ushort> src, int srcW, int srcBx, int srcBy,
        ushort[] pred, int n, int dcDq, int acDq, int scanLen)
    {
        var residual = Av1FwdTransform.RentLevels(n * n);   // fully written below
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                residual[y * n + x] = src[(srcBy + y) * srcW + (srcBx + x)] - pred[y * n + x];
            }
        }

        var lv = Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, scanLen);
        Av1FwdTransform.ReturnLevels(residual);
        return lv;
    }

    /// <summary>Builds the sequence-header OBU and the OBU_FRAME (frame header + tile) for a monochrome key frame
    /// carrying the given coefficients (null ⇒ skip). The two OBUs are the building blocks for both a raw
    /// temporal unit and an AVIF container (seq OBU → av1C configOBUs, frame OBU → mdat).</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) BuildObus(int width, int height, int baseQIdx, int[]? coeffs,
        Av1ObuWriter.Av1ColorDesc? color = null)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                $"Frame {width}x{height} does not map to a single square block (rectangular/multi-block not yet supported).");
        }

        var seqCfg = new Av1ObuWriter.SeqConfig(width, height, monochrome: true, color: color);
        byte[] seqObu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));
        byte[] frameHdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true);
        byte[] tile = EncodeSingleBlockTile(baseQIdx, coeffs, plan);

        var framePayload = new byte[frameHdr.Length + tile.Length];
        frameHdr.CopyTo(framePayload, 0);
        tile.CopyTo(framePayload.AsSpan(frameHdr.Length));
        byte[] frameObu = Av1ObuWriter.WrapObu(Av1ObuType.Frame, framePayload);
        return (seqObu, frameObu);
    }

    /// <summary>Encodes a monochrome key frame whose single 64x64 luma block carries the given quantized
    /// coefficients (rc-indexed, TX_64X64 layout). Returns a raw AV1 temporal unit (TD + seq + OBU_FRAME).</summary>
    internal static byte[] EncodeMonochromeWithCoeffs(int width, int height, int baseQIdx, int[]? coeffs)
    {
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs);
        byte[] tdObu = Av1ObuWriter.WrapObu(Av1ObuType.TemporalDelimiter, ReadOnlySpan<byte>.Empty);

        var outBytes = new byte[tdObu.Length + seqObu.Length + frameObu.Length];
        int o = 0;
        tdObu.CopyTo(outBytes, o); o += tdObu.Length;
        seqObu.CopyTo(outBytes, o); o += seqObu.Length;
        frameObu.CopyTo(outBytes, o);
        return outBytes;
    }

    /// <summary>Encodes a tightly-packed 64x64 monochrome luma image as a complete .avif. Lossy.</summary>
    internal static byte[] EncodeAvifMonochrome64(ReadOnlySpan<byte> pixels, int baseQIdx)
        => EncodeAvifMonochrome(pixels, 64, 64, baseQIdx);

    /// <summary>Encodes a monochrome image (<paramref name="luma"/> tightly packed, <paramref name="width"/> x
    /// <paramref name="height"/>) as a complete .avif. The frame must map to a single square block (both
    /// dimensions within one of 5..8, 9..16, 17..32, 33..64 — coded at block sizes 8/16/32/64). The residual
    /// block is the frame content in its top-left, edge-replicated to the block size.</summary>
    internal static byte[] EncodeAvifMonochrome(ReadOnlySpan<byte> luma, int width, int height, int baseQIdx,
        Av1ObuWriter.Av1ColorDesc? color = null, AvifContainerExtras? extras = null)
    {
        if (!TryResolveSingleBlock(width, height, out BlockPlan plan))
        {
            throw new NotSupportedException(
                $"Frame {width}x{height} does not map to a single square block (only near-square sizes with both " +
                "dimensions in 5..8, 9..16, 17..32 or 33..64 are supported).");
        }

        int[]? coeffs = QuantizeBlock(luma, width, height, plan, baseQIdx);
        (byte[] seqObu, byte[] frameObu) = BuildObus(width, height, baseQIdx, coeffs, color);
        return Av1AvifWriter.BuildAvif(seqObu, frameObu, width, height, monochrome: true, color: color, extras: extras);
    }

    /// <summary>Forward-transforms and quantizes a frame into its single block's coefficients (DC prediction =
    /// 128), or null when quantization zeroes everything (⇒ skip / flat plane). The n x n residual block is built
    /// from the frame luma with edge replication beyond the frame.</summary>
    private static int[]? QuantizeBlock(ReadOnlySpan<byte> luma, int width, int height, in BlockPlan plan, int baseQIdx)
    {
        int n = plan.BlockPx;
        int dcDq = Av1Tables.DequantTable[0, baseQIdx, 0];
        int acDq = Av1Tables.DequantTable[0, baseQIdx, 1];

        var residual = new int[n * n];
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(x, width - 1);
                residual[y * n + x] = luma[sy * width + sx] - 128; // DC prediction (first block, no neighbours)
            }
        }

        int[] coeffs = Av1FwdTransform.ForwardQuantSquare(residual, n, dcDq, acDq, Av1Tables.Scans[plan.Tx].Length);
        foreach (int c in coeffs)
        {
            if (c != 0)
            {
                return coeffs;
            }
        }

        return null;
    }
}
