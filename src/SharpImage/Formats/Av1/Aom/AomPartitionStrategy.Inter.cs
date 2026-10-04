using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>SIMPLE_MOTION_DATA_TREE (context_tree.h).</summary>
internal sealed class AomSmsTree
{
    public int BlockSize;
    public int Partitioning = PARTITION_NONE;
    public readonly AomSmsTree?[] Split = new AomSmsTree?[4];
    /// <summary>start_mvs[REF_FRAMES] (full-pel).</summary>
    public readonly AomMv[] StartMvs = new AomMv[REF_FRAMES];
    public readonly uint[] SmsNoneFeat = new uint[2];
    public readonly uint[] SmsRectFeat = new uint[8];
    public bool SmsNoneValid, SmsRectValid;

    /// <summary>setup_sms_tree: the tree from the superblock size down to BLOCK_4X4 leaves.</summary>
    public static AomSmsTree Build(int bsize)
    {
        var t = new AomSmsTree { BlockSize = bsize };
        if (bsize > BLOCK_4X4)
        {
            int sub = AomMl.GetPartitionSubsize(bsize, PARTITION_SPLIT);
            for (int i = 0; i < 4; i++) t.Split[i] = Build(sub);
        }
        return t;
    }

    /// <summary>av1_reset_simple_motion_tree_partition.</summary>
    public static void ResetPartition(AomSmsTree? t, int bsize)
    {
        if (t == null) return;
        t.Partitioning = PARTITION_NONE;
        if (bsize >= BLOCK_8X8)
        {
            int sub = AomMl.GetPartitionSubsize(bsize, PARTITION_SPLIT);
            for (int i = 0; i < 4; i++) ResetPartition(t.Split[i], sub);
        }
    }
}

internal sealed partial class AomMacroblock
{
    /// <summary>td->sms_root.</summary>
    public AomSmsTree? SmsRoot;
}

// Port of libaom 3.14.1 av1/encoder/partition_strategy.c's simple-motion-search based partition pruning (inter
// frames), partition_strategy.h's set_offsets_for_motion_search / set_max_min_partition_size and
// motion_search_facade.c's av1_simple_motion_search_sse_var.
internal static partial class AomEncodeFrame
{
    private const int FEATURE_SMS_NONE_FLAG = 1, FEATURE_SMS_SPLIT_FLAG = 2, FEATURE_SMS_RECT_FLAG = 4;
    private const int FEATURE_SMS_PRUNE_PART_FLAG = FEATURE_SMS_NONE_FLAG | FEATURE_SMS_SPLIT_FLAG | FEATURE_SMS_RECT_FLAG;
    private const int FEATURE_SMS_SPLIT_MODEL_FLAG = FEATURE_SMS_NONE_FLAG | FEATURE_SMS_SPLIT_FLAG;
    private const int FEATURE_SIZE_SMS_SPLIT = 17, FEATURE_SIZE_SMS_PRUNE_PART = 25, FEATURE_SIZE_SMS_TERM_NONE = 28;
    private const int FEATURE_SIZE_MAX_MIN_PART_PRED = 13, MAX_NUM_CLASSES_MAX_MIN_PART_PRED = 4;
    private const int TOTAL_SIMPLE_AGG_LVLS = 6;

    private static int RefFrameFlag(int refFrame) => 1 << (refFrame - LAST_FRAME);

    /// <summary>set_offsets_for_motion_search.</summary>
    internal static void SetOffsetsForMotionSearch(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize)
    {
        var cm = cpi.Cm;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        int miWidth = MiSizeWide[bsize], miHeight = MiSizeHigh[bsize];
        // set_mode_info_offsets
        int gridIdx = miRow * cm.MiStride + miCol;
        cm.MiGridBase[gridIdx] = cm.MiAlloc(gridIdx);
        xd.MiGrid = cm.MiGridBase;
        xd.MiStride = cm.MiStride;
        xd.MiOffset = gridIdx;
        xd.Mi0 = cm.MiAlloc(gridIdx);
        xd.TxTypeMap = cm.TxTypeMap;
        xd.TxTypeMapOffset = gridIdx;
        xd.TxTypeMapStride = cm.MiStride;
        SetupDstPlanes(cpi, xd, bsize, miRow, miCol);
        AomMotionSearch.SetMvLimits(cm, ref x.MvLimits, miRow, miCol, miHeight, miWidth, cpi.BorderInPixels);
        for (int i = 0; i < numPlanes; i++)
        {
            var pd = xd.Plane[i];
            pd.Width = Math.Max((miWidth * 4) >> pd.SubsamplingX, 4);
            pd.Height = Math.Max((miHeight * 4) >> pd.SubsamplingY, 4);
        }
        xd.MiRow = miRow;
        xd.MiCol = miCol;
        xd.MbToTopEdge = -((miRow * 4) * 8);
        xd.MbToBottomEdge = ((cm.MiRows - miHeight - miRow) * 4) * 8;
        xd.MbToLeftEdge = -((miCol * 4) * 8);
        xd.MbToRightEdge = ((cm.MiCols - miWidth - miCol) * 4) * 8;
        SetupSrcPlanes(cpi, x, miRow, miCol, numPlanes, bsize);
    }

    /// <summary>The block-size variance fn_ptr[bsize].vf of the source against the prediction (8-bit or high bit depth).</summary>
    private static uint BlockVariance(AomMacroblock x, in AomBuf2d src, in AomBuf2d dst, int bsize, out uint sse)
    {
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        if (src.Buf16 != null)
            return AomHbd.Variance(src.Buf16, src.Offset, src.Stride, dst.Buf16!, dst.Offset, dst.Stride, 0, w, h, x.E.Bd, out sse);
        return AomSad.Variance(src.Buf, src.Offset, src.Stride, dst.Buf, dst.Offset, dst.Stride, w, h, out sse);
    }

    /// <summary>av1_simple_motion_search_sse_var (one plane).</summary>
    internal static AomMv SimpleMotionSearchSseVar(AomComp cpi, AomMacroblock x, int miRow, int miCol, int bsize, int refFrame,
        AomMv startMv, bool useSubpixel, out uint sse, out uint var)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        SetOffsetsForMotionSearch(cpi, x, miRow, miCol, bsize);
        var mbmi = xd.Mi0;
        mbmi.Bsize = bsize;
        mbmi.RefFrame0 = refFrame;
        mbmi.RefFrame1 = NONE_FRAME;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.InterpFilters = AomInterpSearch.Broadcast(EIGHTTAP_REGULAR);

        var yv12 = cm.RefBufs[refFrame]!.Buf;
        var scaledRef = cpi.GetScaledRefFrame(refFrame);
        var refMv = new AomMv(0, 0);
        int stepParam = Math.Min(cpi.MvStepParam + cpi.Sf.part_sf.simple_motion_search_reduce_search_steps, AomMcomp.MAX_MVSEARCH_STEPS - 2);
        var costList = new int[5];
        const int refIdx = 0;
        AomInterPred.SetupPrePlanes(xd, refIdx, yv12, miRow, miCol, cm.RefScaleFactors[refFrame]!, 1);
        AomRdoptInter.SetRefPtrs(cm, xd, mbmi.RefFrame0, mbmi.RefFrame1);
        AomBuf2d backupY = default;
        if (scaledRef != null)
        {
            backupY = xd.Plane[0].Pre(refIdx);
            AomInterPred.SetupPrePlanes(xd, refIdx, scaledRef, miRow, miCol, null!, 1);
        }
        bool fineSearchInterval = cpi.IsScreenContentType && cpi.UpdateType == ARF_UPDATE && cpi.Speed <= 2;
        var mvSf = cpi.Sf.mv_sf;
        int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, mvSf, bsize);
        var p = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv, cpi.SearchSites, searchMethod, fineSearchInterval, x.MvLimits, startMv);
        AomMv dummySecond = default;
        int bestsme = AomMcomp.FullPixelSearch(startMv, p, stepParam, AomSubpel.CondCostList(cpi, costList), out var bestFull,
            out var bestMvStats, ref dummySecond, false);
        bool useSubpelSearch = bestsme < int.MaxValue && !cm.CurFrameForceIntegerMv && useSubpixel &&
            mvSf.simple_motion_subpel_force_stop != AomSubpel.FULL_PEL;
        if (scaledRef != null) xd.Plane[0].Pre(refIdx) = backupY;
        AomMv bestMv;
        if (useSubpelSearch)
        {
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, refMv, costList, cpi.SubpelParamsScratch(x));
            ms.ForcedStop = mvSf.simple_motion_subpel_force_stop;
            var subpelStartMv = bestFull.ToMv();
            AomSubpel.FindFractionalMvStep(cpi, ms, subpelStartMv, bestMvStats, out bestMv, out _, out uint psse, null);
            x.PredSse[refFrame] = psse;
            mbmi.Mv0 = bestMv;
            AomInterPred.EncBuildInterPredictor(cm, xd, miRow, miCol, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
            var = BlockVariance(x, x.Plane[0].Src, xd.Plane[0].Dst, bsize, out sse);
        }
        else
        {
            bestMv = bestFull.ToMv();   // convert_fullmv_to_mv
            var = (uint)bestMvStats.Distortion;
            sse = (uint)bestMvStats.Sse;
        }
        return bestMv;
    }

    /// <summary>av1_init_simple_motion_search_mvs_for_sb (tile given: the offsets are set here).</summary>
    internal static void InitSimpleMotionSearchMvsForSb(AomComp cpi, AomMacroblock x, AomSmsTree smsRoot, int miRow, int miCol, bool setOffsets)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        Span<AomMv> refMvs = stackalloc AomMv[REF_FRAMES];
        refMvs.Clear();
        if (setOffsets) SetOffsetsWithoutSegmentId(cpi, x, miRow, miCol, cm.SbSize);
        int refFrame = cpi.IsSrcFrameAltRef ? ALTREF_FRAME : LAST_FRAME;
        var ext = new AomMbmiExtInter();
        AomMvPred.FindMvRefs(cm, xd, xd.Mi0, refFrame, ext);
        refMvs[refFrame] = ext.RefMvCount[refFrame] > 0 ? xd.RefMvStacks[refFrame][0].ThisMv.ToFullMv() : ext.GlobalMvs[refFrame].ToFullMv();
        InitSimpleMotionSearchMvs(smsRoot, refMvs);
    }

    private static void InitSimpleMotionSearchMvs(AomSmsTree t, ReadOnlySpan<AomMv> startMvs)
    {
        startMvs.CopyTo(t.StartMvs);
        Array.Clear(t.SmsNoneFeat);
        Array.Clear(t.SmsRectFeat);
        t.SmsNoneValid = false;
        t.SmsRectValid = false;
        if (t.BlockSize >= BLOCK_8X8)
            for (int i = 0; i < 4; i++) InitSimpleMotionSearchMvs(t.Split[i]!, startMvs);
    }

    /// <summary>simple_motion_search_get_best_ref.</summary>
    private static int SimpleMotionSearchGetBestRef(AomComp cpi, AomMacroblock x, AomSmsTree t, int miRow, int miCol, int bsize,
        ReadOnlySpan<int> refs, bool useSubpixel, bool saveMv, out uint bestSse, out uint bestVar)
    {
        var cm = cpi.Cm;
        int bestRef = -1;
        bestVar = 0;
        if (miCol >= cm.MiCols || miRow >= cm.MiRows)
        {
            bestSse = 0;
            return bestRef;
        }
        bestSse = int.MaxValue;
        foreach (int r in refs)
        {
            if ((cpi.RefFrameFlags & RefFrameFlag(r)) == 0) continue;
            var bestMv = SimpleMotionSearchSseVar(cpi, x, miRow, miCol, bsize, r, t.StartMvs[r], useSubpixel, out uint currSse, out uint currVar);
            AomTrace.Out?.Write($"sgb {miRow} {miCol} bs {bsize} st {t.StartMvs[r].Row} {t.StartMvs[r].Col} -> {bestMv.Row} {bestMv.Col} sse {currSse} var {currVar} save {(saveMv ? 1 : 0)}" + (char)10);
            if (currSse < bestSse)
            {
                bestSse = currSse;
                bestVar = currVar;
                bestRef = r;
            }
            if (saveMv)
            {
                t.StartMvs[r] = new AomMv(bestMv.Row / 8, bestMv.Col / 8);
                if (bsize >= BLOCK_8X8)
                    for (int i = 0; i < 4; i++) t.Split[i]!.StartMvs[r] = t.StartMvs[r];
            }
        }
        return bestRef;
    }

    /// <summary>simple_motion_search_prune_part_features.</summary>
    private static void SimpleMotionSearchPrunePartFeatures(AomComp cpi, AomMacroblock x, AomSmsTree t, int miRow, int miCol, int bsize,
        Span<float> features, int featuresToGet)
    {
        int wMi = MiSizeWide[bsize], hMi = MiSizeHigh[bsize];
        ReadOnlySpan<int> refList = stackalloc int[] { cpi.IsSrcFrameAltRef ? ALTREF_FRAME : LAST_FRAME };
        if (!t.SmsNoneValid && (featuresToGet & FEATURE_SMS_NONE_FLAG) != 0)
        {
            SimpleMotionSearchGetBestRef(cpi, x, t, miRow, miCol, bsize, refList, true, true, out t.SmsNoneFeat[0], out t.SmsNoneFeat[1]);
            t.SmsNoneValid = true;
        }
        if ((featuresToGet & FEATURE_SMS_SPLIT_FLAG) != 0)
        {
            int subsize = AomMl.GetPartitionSubsize(bsize, PARTITION_SPLIT);
            for (int r = 0; r < 4; r++)
            {
                int subMiCol = miCol + (r & 1) * wMi / 2, subMiRow = miRow + (r >> 1) * hMi / 2;
                var sub = t.Split[r]!;
                if (!sub.SmsNoneValid)
                {
                    SimpleMotionSearchGetBestRef(cpi, x, sub, subMiRow, subMiCol, subsize, refList, true, true, out sub.SmsNoneFeat[0], out sub.SmsNoneFeat[1]);
                    sub.SmsNoneValid = true;
                }
            }
        }
        if (!t.SmsRectValid && (featuresToGet & FEATURE_SMS_RECT_FLAG) != 0)
        {
            int subsize = AomMl.GetPartitionSubsize(bsize, PARTITION_HORZ);
            for (int r = 0; r < 2; r++)
                SimpleMotionSearchGetBestRef(cpi, x, t, miRow + r * hMi / 2, miCol, subsize, refList, true, false,
                    out t.SmsRectFeat[2 * r], out t.SmsRectFeat[2 * r + 1]);
            subsize = AomMl.GetPartitionSubsize(bsize, PARTITION_VERT);
            for (int r = 0; r < 2; r++)
                SimpleMotionSearchGetBestRef(cpi, x, t, miRow, miCol + r * wMi / 2, subsize, refList, true, false,
                    out t.SmsRectFeat[4 + 2 * r], out t.SmsRectFeat[4 + 2 * r + 1]);
            t.SmsRectValid = true;
        }
        if (features.IsEmpty) return;

        int f = 0;
        if ((featuresToGet & FEATURE_SMS_NONE_FLAG) != 0)
            for (int i = 0; i < 2; i++) features[f++] = AomMl.Log1pf((float)t.SmsNoneFeat[i]);
        if ((featuresToGet & FEATURE_SMS_SPLIT_FLAG) != 0)
            for (int i = 0; i < 4; i++)
            {
                var sub = t.Split[i]!;
                features[f++] = AomMl.Log1pf((float)sub.SmsNoneFeat[0]);
                features[f++] = AomMl.Log1pf((float)sub.SmsNoneFeat[1]);
            }
        if ((featuresToGet & FEATURE_SMS_RECT_FLAG) != 0)
            for (int i = 0; i < 8; i++) features[f++] = AomMl.Log1pf((float)t.SmsRectFeat[i]);

        var xd = x.E;
        SetOffsetsForMotionSearch(cpi, x, miRow, miCol, bsize);
        int dcQ = AomMl.DcQuantQtx(x.Qindex, xd.Bd) >> (xd.Bd - 8);
        features[f++] = AomMl.Log1pf((float)(dcQ * dcQ) / 256.0f);
        bool hasAbove = xd.AboveMbmi != null, hasLeft = xd.LeftMbmi != null;
        int aboveBsize = hasAbove ? xd.AboveMbmi!.Bsize : bsize;
        int leftBsize = hasLeft ? xd.LeftMbmi!.Bsize : bsize;
        features[f++] = hasAbove ? 1f : 0f;
        features[f++] = MiSizeWideLog2[aboveBsize];
        features[f++] = MiSizeHighLog2[aboveBsize];
        features[f++] = hasLeft ? 1f : 0f;
        features[f++] = MiSizeWideLog2[leftBsize];
        features[f++] = MiSizeHighLog2[leftBsize];
    }

    /// <summary>get_simple_motion_search_prune_agg.</summary>
    private static int GetSimpleMotionSearchPruneAgg(int qindex, int pruneLevel, bool isRectPart)
    {
        if (pruneLevel == NO_PRUNING) return -1;
        if (pruneLevel < TOTAL_SIMPLE_AGG_LVLS) return pruneLevel;
        int qband = isRectPart ? (qindex <= 90 ? 1 : 0) : 0;
        return qband == 1 ? 4 : 3;
    }

    /// <summary>convert_bsize_to_idx.</summary>
    private static int SmsBsizeIdx(int bsize) => bsize switch
    {
        BLOCK_128X128 => 0, BLOCK_64X64 => 1, BLOCK_32X32 => 2, BLOCK_16X16 => 3, BLOCK_8X8 => 4, _ => -1,
    };

    /// <summary>simple_motion_search_based_split.</summary>
    private static void SimpleMotionSearchBasedSplit(AomComp cpi, AomMacroblock x, AomSmsTree t, AomPartitionSearchState s)
    {
        var cm = cpi.Cm;
        ref var bp = ref s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        int bsizeIdx = SmsBsizeIdx(bsize);
        bool is720p = Math.Min(cm.Width, cm.Height) >= 720, is480p = Math.Min(cm.Width, cm.Height) >= 480;
        int resIdx = (is480p ? 1 : 0) + (is720p ? 1 : 0);
        int agg = GetSimpleMotionSearchPruneAgg(x.Qindex, cpi.Sf.part_sf.simple_motion_search_prune_agg, false);
        if (agg < 0) return;
        int mlModelIndex = agg == SIMPLE_AGG_LVL1 || agg == SIMPLE_AGG_LVL2 ? 1 : 0;
        var mlMean = AomMlModels.SimpleMotionSearchSplitMean[mlModelIndex * 5 + bsizeIdx]!;
        var mlStd = AomMlModels.SimpleMotionSearchSplitStd[mlModelIndex * 5 + bsizeIdx]!;
        var nnConfig = AomMlModels.SimpleMotionSearchSplitNnConfig[mlModelIndex * 5 + bsizeIdx]!;
        float splitOnlyThresh = AomMlModels.SimpleMotionSearchSplitThresh[(agg * 3 + resIdx) * 5 + bsizeIdx];
        float noSplitThresh = AomMlModels.SimpleMotionSearchNoSplitThresh[(agg * 3 + resIdx) * 5 + bsizeIdx];
        Span<float> features = stackalloc float[FEATURE_SIZE_SMS_SPLIT];
        features.Clear();
        SimpleMotionSearchPrunePartFeatures(cpi, x, t, miRow, miCol, bsize, features, FEATURE_SMS_SPLIT_MODEL_FLAG);
        for (int i = 0; i < FEATURE_SIZE_SMS_SPLIT; i++) features[i] = (features[i] - mlMean[i]) / mlStd[i];
        Span<float> score = stackalloc float[1];
        score[0] = 0f;
        AomMl.NnPredict(features, nnConfig, true, score);
        if (score[0] > splitOnlyThresh) s.SetSquareSplitOnly();
        if (cpi.Sf.part_sf.simple_motion_search_split >= 2 && score[0] < noSplitThresh) s.DisableSquareSplitPartition();
        if (cpi.Sf.part_sf.simple_motion_search_rect_split != 0)
        {
            float scale = resIdx >= 2 ? 3.0f : 2.0f;
            float rectSplitThresh = scale * AomMlModels.SimpleMotionSearchNoSplitThresh[(SIMPLE_AGG_LVL3 * 3 + resIdx) * 5 + bsizeIdx];
            if (score[0] < rectSplitThresh) s.DoRectangularSplit = false;
        }
    }

    /// <summary>simple_motion_search_prune_rect.</summary>
    private static void SimpleMotionSearchPruneRect(AomComp cpi, AomMacroblock x, AomSmsTree t, AomPartitionSearchState s)
    {
        var cm = cpi.Cm;
        ref var bp = ref s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        int bsizeIdx = SmsBsizeIdx(bsize);
        bool is720p = Math.Min(cm.Width, cm.Height) >= 720, is480p = Math.Min(cm.Width, cm.Height) >= 480;
        int resIdx = (is480p ? 1 : 0) + (is720p ? 1 : 0);
        var nnConfig = AomMlModels.SimpleMotionSearchPruneRectNnConfig[bsizeIdx];
        var mlMean = AomMlModels.SimpleMotionSearchPruneRectMean[bsizeIdx];
        var mlStd = AomMlModels.SimpleMotionSearchPruneRectStd[bsizeIdx];
        int agg = GetSimpleMotionSearchPruneAgg(x.Qindex, cpi.Sf.part_sf.simple_motion_search_prune_agg, true);
        if (agg < 0) return;
        float pruneThresh = AomMlModels.SimpleMotionSearchPruneRectThresh[(agg * 3 + resIdx) * 5 + bsizeIdx];
        if (nnConfig == null || pruneThresh == 0.0f) return;
        Span<float> features = stackalloc float[FEATURE_SIZE_SMS_PRUNE_PART];
        features.Clear();
        SimpleMotionSearchPrunePartFeatures(cpi, x, t, miRow, miCol, bsize, features, FEATURE_SMS_PRUNE_PART_FLAG);
        if (AomTrace.Out != null)
        {
            var sb = new System.Text.StringBuilder($"spf {miRow} {miCol}");
            for (int i = 0; i < FEATURE_SIZE_SMS_PRUNE_PART; i++) sb.Append(' ').Append(((double)features[i]).ToString("G9"));
            AomTrace.Out.Write(sb.ToString() + (char)10);
        }
        for (int i = 0; i < FEATURE_SIZE_SMS_PRUNE_PART; i++) features[i] = (features[i] - mlMean![i]) / mlStd![i];
        Span<float> scores = stackalloc float[10];
        Span<float> probs = stackalloc float[10];
        scores.Clear();
        probs.Clear();
        int numClasses = bsize == BLOCK_128X128 || bsize == BLOCK_8X8 ? 4 : 10;   // PARTITION_TYPES : EXT_PARTITION_TYPES
        AomMl.NnPredict(features, nnConfig, true, scores);
        AomMl.NnSoftmax(scores, probs, numClasses);
        AomTrace.Out?.Write($"spr {miRow} {miCol} bs {bsize} h {((double)probs[PARTITION_HORZ]).ToString("G9")} v {((double)probs[PARTITION_VERT]).ToString("G9")} th {((double)pruneThresh).ToString("G9")} f0 {((double)features[0]).ToString("G9")}" + (char)10);
        if (probs[PARTITION_HORZ] <= pruneThresh) s.PruneRectPart[HORZ] = true;
        if (probs[PARTITION_VERT] <= pruneThresh) s.PruneRectPart[VERT] = true;
    }

    /// <summary>av1_simple_motion_search_early_term_none.</summary>
    private static void SimpleMotionSearchEarlyTermNone(AomComp cpi, AomMacroblock x, AomSmsTree t, in AomRdStats noneRdc, AomPartitionSearchState s)
    {
        ref var bp = ref s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        Span<float> features = stackalloc float[FEATURE_SIZE_SMS_TERM_NONE];
        features.Clear();
        SimpleMotionSearchPrunePartFeatures(cpi, x, t, miRow, miCol, bsize, features, FEATURE_SMS_PRUNE_PART_FLAG);
        int f = FEATURE_SIZE_SMS_PRUNE_PART;
        features[f++] = AomMl.Log1pf((float)noneRdc.Rate);
        features[f++] = AomMl.Log1pf((float)noneRdc.Dist);
        features[f++] = AomMl.Log1pf((float)noneRdc.Rdcost);
        float[] mlMean, mlStd, mlModel;
        switch (bsize)
        {
            case BLOCK_128X128: mlMean = AomMlModels.SimpleMotionSearchTermNoneMean128; mlStd = AomMlModels.SimpleMotionSearchTermNoneStd128; mlModel = AomMlModels.SimpleMotionSearchTermNoneModel128; break;
            case BLOCK_64X64: mlMean = AomMlModels.SimpleMotionSearchTermNoneMean64; mlStd = AomMlModels.SimpleMotionSearchTermNoneStd64; mlModel = AomMlModels.SimpleMotionSearchTermNoneModel64; break;
            case BLOCK_32X32: mlMean = AomMlModels.SimpleMotionSearchTermNoneMean32; mlStd = AomMlModels.SimpleMotionSearchTermNoneStd32; mlModel = AomMlModels.SimpleMotionSearchTermNoneModel32; break;
            case BLOCK_16X16: mlMean = AomMlModels.SimpleMotionSearchTermNoneMean16; mlStd = AomMlModels.SimpleMotionSearchTermNoneStd16; mlModel = AomMlModels.SimpleMotionSearchTermNoneModel16; break;
            default: throw new InvalidOperationException("Unexpected block size in simple_motion_term_none");
        }
        float score = 0.0f;
        for (f = 0; f < FEATURE_SIZE_SMS_TERM_NONE; f++) score += mlModel[f] * (features[f] - mlMean[f]) / mlStd[f];
        score += mlModel[FEATURE_SIZE_SMS_TERM_NONE];
        if (score >= 0.0f) s.TerminatePartitionSearch = true;
    }

    /// <summary>av1_prune_partitions_before_search's inter-frame part (simple motion search split / rect pruning).</summary>
    private static void PrunePartitionsBeforeSearchInter(AomComp cpi, AomMacroblock x, AomSmsTree? t, AomPartitionSearchState s)
    {
        var cm = cpi.Cm;
        ref var bp = ref s.BlkParams;
        bool trySplitOnly = cpi.Sf.part_sf.simple_motion_search_split != 0 && s.DoSquareSplit && bp.BsizeAtLeast8x8 &&
            IsWholeBlkInFrame(bp, cm) && !cm.FrameIsIntraOnly;
        if (trySplitOnly) SimpleMotionSearchBasedSplit(cpi, x, t!, s);
        bool nonRectPartAllowed = s.DoSquareSplit || s.PartitionNoneAllowed;
        bool rectPartAllowed = s.DoRectangularSplit &&
            ((s.PartitionRectAllowed[HORZ] && !s.PruneRectPart[HORZ]) || (s.PartitionRectAllowed[VERT] && !s.PruneRectPart[VERT]));
        bool tryPruneRect = cpi.Sf.part_sf.simple_motion_search_prune_rect != 0 && !cm.FrameIsIntraOnly && nonRectPartAllowed && rectPartAllowed;
        if (tryPruneRect) SimpleMotionSearchPruneRect(cpi, x, t!, s);
    }

    /// <summary>prune_partitions_after_none (inter frames: the skippable breakout and the SMS early termination).</summary>
    private static void PrunePartitionsAfterNone(AomComp cpi, AomMacroblock x, AomSmsTree? t, AomPickModeContext ctxNone, AomPartitionSearchState s,
        in AomRdStats bestRdc, uint pbSourceVariance)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var sf = cpi.Sf;
        ref var bp = ref s.BlkParams;
        int bsize = bp.Bsize;
        if (!cm.FrameIsIntraOnly && (s.DoSquareSplit || s.DoRectangularSplit) && xd.Lossless[xd.Mi0.SegmentId] == 0 && ctxNone.Skippable != 0)
        {
            bool useMlBasedBreakout = bsize <= sf.part_sf.use_square_partition_only_threshold && bsize > BLOCK_4X4 &&
                sf.part_sf.ml_predict_breakout_level >= 1;
            if (useMlBasedBreakout)
            {
                int doSq = s.DoSquareSplit ? 1 : 0, doRect = s.DoRectangularSplit ? 1 : 0;
                AomMl.MlPredictBreakout(bsize, s.ThisRdc.Rate, s.ThisRdc.Dist, pbSourceVariance, x.Rdmult, x.Qindex, xd.Bd,
                    sf.part_sf.ml_partition_search_breakout_thresh, sf.part_sf.ml_partition_search_breakout_model_index,
                    sf.part_sf.ml_predict_breakout_level, ref doSq, ref doRect);
                s.DoSquareSplit = doSq != 0;
                s.DoRectangularSplit = doRect != 0;
            }
            long distBreakoutThr = sf.part_sf.partition_search_breakout_dist_thr >> ((2 * (MAX_SB_SIZE_LOG2 - 2)) - (MiSizeWideLog2[bsize] + MiSizeHighLog2[bsize]));
            int rateBreakoutThr = sf.part_sf.partition_search_breakout_rate_thr * NumPelsLog2Lookup[bsize];
            if (bestRdc.Dist < distBreakoutThr && bestRdc.Rate < rateBreakoutThr)
            {
                s.DoSquareSplit = false;
                s.DoRectangularSplit = false;
            }
        }
        if (sf.part_sf.simple_motion_search_early_term_none != 0 && cm.ShowFrame && !cm.FrameIsIntraOnly && bsize >= BLOCK_16X16 &&
            s.HasRowsAndCols && s.ThisRdc.Rdcost < long.MaxValue && s.ThisRdc.Rdcost >= 0 && s.ThisRdc.Rate < int.MaxValue &&
            s.ThisRdc.Rate >= 0 && (s.DoSquareSplit || s.DoRectangularSplit))
            SimpleMotionSearchEarlyTermNone(cpi, x, t!, s.ThisRdc, s);
        AomTrace.Out?.Write($"pan {bp.MiRow} {bp.MiCol} bs {bsize} pr {(s.PruneRectPart[HORZ] ? 1 : 0)} {(s.PruneRectPart[VERT] ? 1 : 0)} sq {(s.DoSquareSplit ? 1 : 0)} rect {(s.DoRectangularSplit ? 1 : 0)} term {(s.TerminatePartitionSearch ? 1 : 0)} rd {s.ThisRdc.Rdcost} rate {s.ThisRdc.Rate} skippable {ctxNone.Skippable}" + (char)10);
    }

    /// <summary>get_min_bsize.</summary>
    private static void GetMinBsize(AomSmsTree? t, ref int minBw, ref int minBh)
    {
        if (t == null) return;
        int bsize = t.BlockSize;
        if (bsize == BLOCK_4X4) { minBw = 0; minBh = 0; return; }
        int partType = t.Partitioning;
        if (partType == PARTITION_INVALID) return;
        if (partType == PARTITION_SPLIT)
        {
            for (int i = 0; i < 4; ++i) GetMinBsize(t.Split[i], ref minBw, ref minBh);
        }
        else
        {
            if (partType == PARTITION_HORZ_A || partType == PARTITION_HORZ_B || partType == PARTITION_VERT_A || partType == PARTITION_VERT_B)
                partType = PARTITION_SPLIT;
            int subsize = AomMl.GetPartitionSubsize(bsize, partType);
            if (subsize != BLOCK_INVALID)
            {
                minBw = Math.Min(minBw, MiSizeWideLog2[subsize]);
                minBh = Math.Min(minBh, MiSizeHighLog2[subsize]);
            }
        }
    }

    /// <summary>prune_partitions_after_split (inter frames).</summary>
    private static void PrunePartitionsAfterSplit(AomComp cpi, AomMacroblock x, AomSmsTree? t, AomPartitionSearchState s, in AomRdStats bestRdc,
        long partNoneRd, long partSplitRd)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        ref var bp = ref s.BlkParams;
        int miRow = bp.MiRow, miCol = bp.MiCol, bsize = bp.Bsize;
        if (sf.part_sf.ml_early_term_after_part_split_level != 0 && !cm.FrameIsIntraOnly && !s.TerminatePartitionSearch &&
            s.DoRectangularSplit && (s.PartitionRectAllowed[HORZ] || s.PartitionRectAllowed[VERT]))
        {
            // av1_ml_early_term_after_split (the simple motion search features are computed after its early returns)
            if (bestRdc.Rdcost > 0 && bestRdc.Rdcost != long.MaxValue && bsize != BLOCK_4X4)
            {
                Span<int> minBwBh = stackalloc int[8];
                Span<uint> splitFeat = stackalloc uint[4];
                for (int i = 0; i < 4; i++)
                {
                    int minBw = MAX_SB_SIZE_LOG2, minBh = MAX_SB_SIZE_LOG2;
                    GetMinBsize(t!.Split[i], ref minBw, ref minBh);
                    minBwBh[2 * i] = minBw;
                    minBwBh[2 * i + 1] = minBh;
                }
                SimpleMotionSearchPrunePartFeatures(cpi, x, t!, miRow, miCol, bsize, Span<float>.Empty, FEATURE_SMS_PRUNE_PART_FLAG);
                for (int i = 0; i < 4; i++) splitFeat[i] = t!.Split[i]!.SmsNoneFeat[1];
                int dcQ = AomMl.DcQuantQtx(x.Qindex, x.E.Bd) >> (x.E.Bd - 8);
                s.TerminatePartitionSearch = AomMl.EarlyTermAfterSplit(bsize, cm.Width, cm.Height, sf.part_sf.ml_early_term_after_part_split_level,
                    dcQ, bestRdc.Rdcost, partNoneRd, partSplitRd, s.SplitRd, minBwBh, t!.SmsNoneFeat[1], splitFeat, t.SmsRectFeat,
                    s.TerminatePartitionSearch);
            }
        }
        if (sf.part_sf.ml_early_term_after_part_split_level == 0 && sf.part_sf.ml_prune_partition != 0 && !cm.FrameIsIntraOnly &&
            (s.PartitionRectAllowed[HORZ] || s.PartitionRectAllowed[VERT]) && !(s.PruneRectPart[HORZ] || s.PruneRectPart[VERT]) &&
            !s.TerminatePartitionSearch)
        {
            SetupSrcPlanes(cpi, x, miRow, miCol, cm.NumPlanes, bsize);
            var src = x.Plane[0].Src;
            Span<int> prune = stackalloc int[] { s.PruneRectPart[HORZ] ? 1 : 0, s.PruneRectPart[VERT] ? 1 : 0 };
            if (src.Buf16 != null)
                AomMl.PruneRectPartition(src.Buf16.AsSpan(src.Offset), src.Stride, x.E.Bd, bsize, bestRdc.Rdcost, s.NoneRd, s.SplitRd, prune);
            else
                AomMl.PruneRectPartition(src.Buf.AsSpan(src.Offset), src.Stride, bsize, bestRdc.Rdcost, s.NoneRd, s.SplitRd, prune);
            s.PruneRectPart[HORZ] = prune[0] != 0;
            s.PruneRectPart[VERT] = prune[1] != 0;
        }
    }

    /// <summary>prune_part4_using_sms.</summary>
    private static void PrunePart4UsingSms(AomComp cpi, AomMacroblock x, AomPartitionSearchState s, AomSmsTree t, int miRow, int miCol, int bsize,
        Span<bool> part4Allowed)
    {
        if (!part4Allowed[HORZ4] || !part4Allowed[VERT4]) return;
        int subsizeH4 = AomMl.GetPartitionSubsize(bsize, PARTITION_HORZ_4), subsizeV4 = AomMl.GetPartitionSubsize(bsize, PARTITION_VERT_4);
        int hMi = MiSizeHigh[bsize], wMi = MiSizeWide[bsize];
        int r = cpi.IsSrcFrameAltRef ? ALTREF_FRAME : LAST_FRAME;
        if ((cpi.RefFrameFlags & RefFrameFlag(r)) == 0) return;
        long hSum = 0, vSum = 0;
        for (int i = 0; i < 4; i++)
        {
            SimpleMotionSearchSseVar(cpi, x, miRow + i * hMi / 4, miCol, subsizeH4, r, t.StartMvs[r], true, out uint sse, out _);
            hSum += sse;
        }
        for (int i = 0; i < 4; i++)
        {
            SimpleMotionSearchSseVar(cpi, x, miRow, miCol + i * wMi / 4, subsizeV4, r, t.StartMvs[r], true, out uint sse, out _);
            vSum += sse;
        }
        long hRd = AomRd.RdCost(x.Rdmult, s.Cost(PARTITION_HORZ_4), hSum);
        long vRd = AomRd.RdCost(x.Rdmult, s.Cost(PARTITION_VERT_4), vSum);
        if (hRd > vRd) part4Allowed[HORZ4] = false;
        if (vRd > hRd) part4Allowed[VERT4] = false;
    }

    /// <summary>av1_update_picked_ref_frames_mask.</summary>
    private static void UpdatePickedRefFramesMask(AomMacroblock x, int refType, int bsize, int mibSize, int miRow, int miCol)
    {
        int m = mibSize - 1;
        int r0 = miRow & m, c0 = miCol & m, n = MiSizeWide[bsize];
        for (int i = r0; i < r0 + n; ++i)
            for (int j = c0; j < c0 + n; ++j) x.PickedRefFramesMask[i * 32 + j] |= 1 << refType;
    }

    /// <summary>use_auto_max_partition.</summary>
    private static bool UseAutoMaxPartition(AomComp cpi, int sbSize, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        return !cm.FrameIsIntraOnly && !cpi.UseScreenContentTools && cpi.Sf.part_sf.auto_max_partition_based_on_simple_motion != NOT_IN_USE &&
               sbSize == BLOCK_128X128 && miRow + MiSizeHigh[sbSize] <= cm.MiRows && miCol + MiSizeWide[sbSize] <= cm.MiCols &&
               cpi.UpdateType != OVERLAY_UPDATE && cpi.UpdateType != INTNL_OVERLAY_UPDATE;
    }

    /// <summary>set_max_min_partition_size.</summary>
    internal static void SetMaxMinPartitionSize(AomComp cpi, AomMacroblock x, int sbSize, int miRow, int miCol)
    {
        var sf = cpi.Sf;
        x.MaxPartitionSize = Math.Min(sf.part_sf.default_max_partition_size, DimToSizeP(cpi.MaxPartitionSizeCfg));
        x.MinPartitionSize = Math.Max(sf.part_sf.default_min_partition_size, DimToSizeP(cpi.MinPartitionSizeCfg));
        x.MaxPartitionSize = Math.Min(x.MaxPartitionSize, cpi.Cm.SbSize);
        x.MinPartitionSize = Math.Min(x.MinPartitionSize, cpi.Cm.SbSize);
        if (UseAutoMaxPartition(cpi, sbSize, miRow, miCol))
        {
            Span<float> features = stackalloc float[FEATURE_SIZE_MAX_MIN_PART_PRED];
            features.Clear();
            GetMaxMinPartitionFeatures(cpi, x, miRow, miCol, features);
            x.MaxPartitionSize = Math.Max(Math.Min(PredictMaxPartition(cpi, x, features), x.MaxPartitionSize), x.MinPartitionSize);
        }
    }

    private static int DimToSizeP(int dim) => dim switch
    {
        4 => BLOCK_4X4, 8 => BLOCK_8X8, 16 => BLOCK_16X16, 32 => BLOCK_32X32, 64 => BLOCK_64X64, 128 => BLOCK_128X128,
        _ => throw new ArgumentOutOfRangeException(nameof(dim)),
    };

    /// <summary>av1_get_max_min_partition_features.</summary>
    private static void GetMaxMinPartitionFeatures(AomComp cpi, AomMacroblock x, int miRow, int miCol, Span<float> features)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int sbSize = cm.SbSize;
        int f = 0;
        int dcQ = AomMl.DcQuantQtx(x.Qindex, xd.Bd) >> (xd.Bd - 8);
        float logQSq = AomMl.Log1pf((float)(dcQ * dcQ) / 256.0f);
        float sumMvRowSq = 0, sumMvRow = 0, minAbsMvRow = float.MaxValue, maxAbsMvRow = 0;
        float sumMvColSq = 0, sumMvCol = 0, minAbsMvCol = float.MaxValue, maxAbsMvCol = 0;
        float sumLogSseSq = 0, sumLogSse = 0, minLogSse = float.MaxValue, maxLogSse = 0;
        const int mbSize = BLOCK_16X16;
        int mbRows = BlockSizeHigh[sbSize] / BlockSizeHigh[mbSize], mbCols = BlockSizeWide[sbSize] / BlockSizeWide[mbSize];
        int hLog2 = MiSizeHighLog2[mbSize], wLog2 = MiSizeWideLog2[mbSize];
        for (int mbRow = 0; mbRow < mbRows; mbRow++)
            for (int mbCol = 0; mbCol < mbCols; mbCol++)
            {
                int r = cpi.IsSrcFrameAltRef ? ALTREF_FRAME : LAST_FRAME;
                var bestMv = SimpleMotionSearchSseVar(cpi, x, miRow + (mbRow << hLog2), miCol + (mbCol << wLog2), mbSize, r, new AomMv(0, 0), false,
                    out uint sse, out _);
                float mvRow = bestMv.Row / 8, mvCol = bestMv.Col / 8;
                float logSse = AomMl.Log1pf((float)sse);
                float absMvRow = MathF.Abs(mvRow), absMvCol = MathF.Abs(mvCol);
                sumMvRowSq += mvRow * mvRow;
                sumMvRow += mvRow;
                sumMvColSq += mvCol * mvCol;
                sumMvCol += mvCol;
                if (absMvRow < minAbsMvRow) minAbsMvRow = absMvRow;
                if (absMvRow > maxAbsMvRow) maxAbsMvRow = absMvRow;
                if (absMvCol < minAbsMvCol) minAbsMvCol = absMvCol;
                if (absMvCol > maxAbsMvCol) maxAbsMvCol = absMvCol;
                sumLogSseSq += logSse * logSse;
                sumLogSse += logSse;
                if (logSse < minLogSse) minLogSse = logSse;
                if (logSse > maxLogSse) maxLogSse = logSse;
            }
        int blks = mbRows * mbCols;
        float avgMvRow = sumMvRow / blks, varMvRow = sumMvRowSq / blks - avgMvRow * avgMvRow;
        float avgMvCol = sumMvCol / blks, varMvCol = sumMvColSq / blks - avgMvCol * avgMvCol;
        float avgLogSse = sumLogSse / blks, varLogSse = sumLogSseSq / blks - avgLogSse * avgLogSse;
        features[f++] = avgLogSse;
        features[f++] = avgMvCol;
        features[f++] = avgMvRow;
        features[f++] = logQSq;
        features[f++] = maxAbsMvCol;
        features[f++] = maxAbsMvRow;
        features[f++] = maxLogSse;
        features[f++] = minAbsMvCol;
        features[f++] = minAbsMvRow;
        features[f++] = minLogSse;
        features[f++] = varLogSse;
        features[f++] = varMvCol;
        features[f++] = varMvRow;
    }

    /// <summary>av1_predict_max_partition.</summary>
    private static int PredictMaxPartition(AomComp cpi, AomMacroblock x, ReadOnlySpan<float> features)
    {
        Span<float> scores = stackalloc float[MAX_NUM_CLASSES_MAX_MIN_PART_PRED];
        scores.Clear();
        AomMl.NnPredict(features, AomMlModels.MaxPartPredNnConfig, true, scores);
        int mode = cpi.Sf.part_sf.auto_max_partition_based_on_simple_motion;
        int result = MAX_NUM_CLASSES_MAX_MIN_PART_PRED - 1;
        if (mode == DIRECT_PRED)
        {
            result = 0;
            float maxScore = scores[0];
            for (int i = 1; i < MAX_NUM_CLASSES_MAX_MIN_PART_PRED; ++i)
                if (scores[i] > maxScore) { maxScore = scores[i]; result = i; }
            return (result + 2) * 3;
        }
        Span<float> probs = stackalloc float[MAX_NUM_CLASSES_MAX_MIN_PART_PRED];
        probs.Clear();
        AomMl.NnSoftmax(scores, probs, MAX_NUM_CLASSES_MAX_MIN_PART_PRED);
        if (mode == RELAXED_PRED)
        {
            for (result = MAX_NUM_CLASSES_MAX_MIN_PART_PRED - 1; result >= 0; --result)
            {
                if (result < MAX_NUM_CLASSES_MAX_MIN_PART_PRED - 1) probs[result] += probs[result + 1];
                if (probs[result] > 0.2) break;
            }
        }
        else if (mode == ADAPT_PRED)
        {
            uint sourceVariance = PerpixelVariance(x, cpi.Cm.SbSize, 0);
            if (sourceVariance > 16)
            {
                double thresh = sourceVariance < 128 ? 0.05 : 0.1;
                for (result = MAX_NUM_CLASSES_MAX_MIN_PART_PRED - 1; result >= 0; --result)
                {
                    if (result < MAX_NUM_CLASSES_MAX_MIN_PART_PRED - 1) probs[result] += probs[result + 1];
                    if (probs[result] > thresh) break;
                }
            }
        }
        return (result + 2) * 3;
    }
}
