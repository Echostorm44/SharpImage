using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>The one-pass real-time frame state the block-level non-RD search reads (cpi->rc / cpi->src_sad_blk_64x64 /
/// cpi->last_source / cpi->vbp_info / cpi->zeromv_skip_thresh_exit_part ...), handed over by the frame-level encoder.</summary>
internal sealed class AomRtFrameState
{
    public bool HighSourceSad;
    public ulong FrameSourceSad = ulong.MaxValue, AvgSourceSad;
    public int PercentBlocksWithMotion, FramesSinceKey, FramesSinceGolden, AvgFrameLowMotion;
    public int AvgFrameQindexInter;
    /// <summary>cpi->src_sad_blk_64x64 (null when not computed for this frame).</summary>
    public ulong[]? SrcSadBlk64;
    /// <summary>cpi->last_source (the previous frame's source, possibly filtered in place by the RTC temporal filter).</summary>
    public AomFrameBuffer? LastSource;
    /// <summary>cpi->vbp_info.thresholds (persist across frames: thresholds[4] is only set on key frames).</summary>
    public long[] VbpThresholds = new long[5];
    public readonly uint[] ZeromvSkipThreshExitPart = new uint[BLOCK_SIZES_ALL];
    /// <summary>cpi->noise_estimate.enabled (and its level when enabled).</summary>
    public bool NoiseEstimateEnabled;
    public int NoiseLevel;
    public int FrameNumber;
    /// <summary>cpi->rc.cnt_zeromv and the scroll counters accumulated from the tiles.</summary>
    public int CntZeromv, NumColBlscroll, NumRowBlscroll;
}

internal sealed partial class AomComp
{
    /// <summary>The real-time frame state (null outside one-pass real-time encoding).</summary>
    public AomRtFrameState? Rt;
}

internal sealed partial class AomEncodeInput
{
    public AomRtFrameState? Rt;
    /// <summary>td->mb.rdmult at the frame start (the previous frame's cpi->rd.RDMULT, set by its loopfilter_frame).</summary>
    public int InitialRdmult;
}

internal sealed partial class AomMacroblock
{
    // x->content_state_sb
    public int SourceSadNonrd = AomRtSb.kMedSad, SourceSadRd = AomRtSb.kMedSad;
    public bool LightingChange, LowSumdiff;
    public readonly byte[] ColorSensitivitySb = new byte[2], ColorSensitivitySbG = new byte[2], ColorSensitivitySbAlt = new byte[2];
    public int ForceZeromvSkipForSb, ForceZeromvSkipForBlk;
    public bool SbMeBlock, SbMePartition;
    public AomMv SbMeMv;
    public bool SbForceFixedPart = true, ForceColorCheckBlockLevel;
    public int NonrdPruneRefFrameSearch;
    /// <summary>x->part_search_info.variance_low.</summary>
    public readonly byte[] VarianceLow = new byte[105];
    public bool BlockIsZeroSad;
    public int SbColScroll, SbRowScroll;
}

/// <summary>Port of libaom 3.14.1's per-superblock real-time source analysis (encodeframe.c grade_source_content_sb,
/// get_sb_source_sad, is_calc_src_content_needed; encodeframe_utils.c av1_source_content_sb with the RTC temporal
/// filter of static blocks).</summary>
internal static class AomRtSb
{
    public const int kZeroSad = 0, kVeryLowSad = 1, kLowSad = 2, kMedSad = 3, kHighSad = 4;

    /// <summary>The superblock-level initialisation of encode_sb_row (the real-time fields).</summary>
    public static void InitSb(AomComp cpi, AomMacroblock x)
    {
        Array.Clear(x.ColorSensitivitySb);
        Array.Clear(x.ColorSensitivitySbG);
        Array.Clear(x.ColorSensitivitySbAlt);
        Array.Clear(x.ColorSensitivity);
        x.SourceSadNonrd = kMedSad;
        x.SourceSadRd = kMedSad;
        x.LightingChange = false;
        x.LowSumdiff = false;
        x.ForceZeromvSkipForSb = 0;
        x.SbMeBlock = false;
        x.SbMePartition = false;
        x.SbMeMv = default;
        x.SbForceFixedPart = true;
        x.ForceColorCheckBlockLevel = false;
        x.NonrdPruneRefFrameSearch = cpi.Sf.rt_sf.nonrd_prune_ref_frame_search;
    }

    /// <summary>get_sb_source_sad.</summary>
    private static ulong GetSbSourceSad(AomComp cpi, int miRow, int miCol)
    {
        var rt = cpi.Rt!;
        if (rt.SrcSadBlk64 == null) return ulong.MaxValue;
        var cm = cpi.Cm;
        int blk64InMis = cm.SbSize == BLOCK_128X128 ? cm.MibSize >> 1 : cm.MibSize;
        int numCols = (cm.MiCols + blk64InMis - 1) / blk64InMis, numRows = (cm.MiRows + blk64InMis - 1) / blk64InMis;
        int colIdx = miCol / blk64InMis, rowIdx = miRow / blk64InMis;
        if (rowIdx >= numRows - 1 || colIdx >= numCols - 1) return ulong.MaxValue;
        int o = colIdx + rowIdx * numCols;
        if (cm.SbSize == BLOCK_128X128) return rt.SrcSadBlk64[o] + rt.SrcSadBlk64[o + 1] + rt.SrcSadBlk64[o + numCols] + rt.SrcSadBlk64[o + numCols + 1];
        return rt.SrcSadBlk64[o];
    }

    /// <summary>is_calc_src_content_needed (one spatial layer).</summary>
    private static bool IsCalcSrcContentNeeded(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        ulong currSbSad = GetSbSourceSad(cpi, miRow, miCol);
        if (currSbSad == ulong.MaxValue) return true;
        if (currSbSad == 0)
        {
            x.SourceSadNonrd = kZeroSad;
            return false;
        }
        var cm = cpi.Cm;
        if (cpi.Speed < 9) return true;
        if (Math.Min(cm.Width, cm.Height) < 360)
        {
            ulong avg64 = cm.SbSize == BLOCK_128X128 ? (currSbSad + 2) >> 2 : currSbSad;
            ulong threshLow = 15000, threshHigh = 40000;
            if (cpi.Sf.rt_sf.increase_source_sad_thresh != 0) { threshLow <<= 1; threshHigh <<= 1; }
            if (avg64 > threshLow && avg64 < threshHigh)
            {
                x.SourceSadNonrd = kMedSad;
                return false;
            }
        }
        return true;
    }

    /// <summary>grade_source_content_sb.</summary>
    public static void GradeSourceContentSb(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var cm = cpi.Cm;
        if (cm.FrameType == KEY_FRAME) return;
        var rt = cpi.Rt!;
        bool calcSrcContent = false;
        var rtSf = cpi.Sf.rt_sf;
        if (rtSf.source_metrics_sb_nonrd != 0)
        {
            if (rtSf.check_scene_detection == 0 || rt.FrameSourceSad > 0) calcSrcContent = IsCalcSrcContentNeeded(cpi, x, miRow, miCol);
            else x.SourceSadNonrd = kZeroSad;
        }
        else if (rtSf.var_part_based_on_qidx >= 1 && cm.Width * cm.Height <= 352 * 288)
        {
            if (rt.FrameSourceSad > 0) calcSrcContent = true;
            else x.SourceSadRd = kZeroSad;
        }
        if (calcSrcContent) SourceContentSb(cpi, x, miRow, miCol);
    }

    /// <summary>av1_source_content_sb.</summary>
    private static void SourceContentSb(AomComp cpi, AomMacroblock x, int miRow, int miCol)
    {
        var rt = cpi.Rt!;
        var src = cpi.Source!;
        var last = rt.LastSource!;
        if (last.CropWidths[0] != src.CropWidths[0] || last.CropHeights[0] != src.CropHeights[0]) return;   // y_width / y_height
        if (src.Hbd) return;
        var cm = cpi.Cm;
        int bsize = cm.SbSize;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        int srcStride = src.Strides[0], lastStride = last.Strides[0];
        int srcOff = src.Offsets[0] + srcStride * (miRow << 2) + (miCol << 2);
        int lastOff = last.Offsets[0] + lastStride * (miRow << 2) + (miCol << 2);
        ulong thrVerylow = 10000;
        ulong[] thrLow = { 100000, 36000 };
        ulong thrHigh = 1000000;
        if (cpi.Sf.rt_sf.increase_source_sad_thresh != 0)
        {
            thrHigh <<= 1;
            thrLow[0] <<= 1;
            thrVerylow <<= 1;
        }
        const ulong sumSqThresh = 10000;
        uint tmpVariance = AomSad.Variance(src.Buffers[0], srcOff, srcStride, last.Buffers[0], lastOff, lastStride, bw, bh, out uint tmpSse);
        if (tmpSse < thrLow[1]) x.SourceSadRd = kLowSad;
        if (tmpSse == 0)
        {
            x.SourceSadNonrd = kZeroSad;
            return;
        }
        if (tmpSse < thrVerylow) x.SourceSadNonrd = kVeryLowSad;
        else if (tmpSse < thrLow[0]) x.SourceSadNonrd = kLowSad;
        else if (tmpSse > thrHigh) x.SourceSadNonrd = kHighSad;
        if (tmpVariance < (tmpSse >> 1) && tmpSse - tmpVariance > sumSqThresh) x.LightingChange = true;
        if (tmpSse - tmpVariance < (sumSqThresh >> 1)) x.LowSumdiff = true;
        if (tmpSse > ((thrHigh * 7) >> 3) && !x.LightingChange && !x.LowSumdiff) x.SbForceFixedPart = false;

        int useRtcTf = cpi.Sf.rt_sf.use_rtc_tf;
        if (useRtcTf == 0 || rt.HighSourceSad || rt.FrameSourceSad > 20000) return;
        uint nmean2 = tmpSse - tmpVariance;
        int bdIdx = cm.BitDepth == 8 ? 0 : cm.BitDepth == 10 ? 1 : 2;
        int acQStep = Av1Tables.DequantTable[bdIdx, cm.BaseQindex, 1];
        int avgQStep = Av1Tables.DequantTable[bdIdx, rt.AvgFrameQindexInter, 1];
        uint threshold = useRtcTf == 1 ? (uint)(Math.Clamp(avgQStep, 250, 1000) * acQStep) : (uint)(250 * acQStep);
        if (tmpVariance <= threshold && nmean2 <= 15)
        {
            if (!CheckNeighborBlocks(cm, x.E, miRow, miCol)) return;
            if (!FastDetectNonZeroMotion(cpi, src.Buffers[0], srcOff, srcStride, last.Buffers[0], lastOff, lastStride, miRow, miCol)) return;
            for (int plane = 0; plane < cm.NumPlanes; ++plane)
            {
                int ssx = plane != 0 ? src.SsX : 0, ssy = plane != 0 ? src.SsY : 0;
                int ss = src.Strides[plane], ls = last.Strides[plane];
                int so = src.Offsets[plane] + ss * (miRow << (2 - ssy)) + (miCol << (2 - ssx));
                int lo = last.Offsets[plane] + ls * (miRow << (2 - ssy)) + (miCol << (2 - ssx));
                byte[] s = src.Buffers[plane], l = last.Buffers[plane];
                for (int i = 0; i < (bh >> ssy); ++i)
                {
                    for (int j = 0; j < (bw >> ssx); ++j) s[so + j] = (byte)((l[lo + j] + s[so + j]) >> 1);
                    so += ss;
                    lo += ls;
                }
            }
        }
    }

    /// <summary>check_neighbor_blocks.</summary>
    private static bool CheckNeighborBlocks(AomCommon cm, AomMacroblockD xd, int miRow, int miCol)
    {
        const int thr = 24;
        bool aboveLow = true, leftLow = true;
        int idx = miRow * cm.MiStride + miCol;
        if (miRow > xd.TileMiRowStart)
        {
            var a = cm.MiGridBase[idx - cm.MiStride]!;
            if (a.Mode >= INTRA_MODE_END && (Math.Abs((int)a.Mv0.Row) > thr || Math.Abs((int)a.Mv0.Col) > thr)) aboveLow = false;
        }
        if (miCol > xd.TileMiColStart)
        {
            var l = cm.MiGridBase[idx - 1]!;
            if (l.Mode >= INTRA_MODE_END && (Math.Abs((int)l.Mv0.Row) > thr || Math.Abs((int)l.Mv0.Col) > thr)) leftLow = false;
        }
        return aboveLow && leftLow;
    }

    /// <summary>fast_detect_non_zero_motion.</summary>
    private static bool FastDetectNonZeroMotion(AomComp cpi, byte[] src, int srcOff, int srcStride, byte[] last, int lastOff, int lastStride,
        int miRow, int miCol)
    {
        var cm = cpi.Cm;
        int bsize = cm.SbSize;
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        uint blkSad;
        var sad64 = cpi.Rt!.SrcSadBlk64;
        if (sad64 != null)
        {
            int sbSizeByMb = bsize == BLOCK_128X128 ? cm.MibSize >> 1 : cm.MibSize;
            int sbCols = (cm.MiCols + sbSizeByMb - 1) / sbSizeByMb;
            blkSad = (uint)sad64[miCol / sbSizeByMb + miRow / sbSizeByMb * sbCols];
        }
        else blkSad = AomSad.Sad(src, srcOff, srcStride, last, lastOff, lastStride, bw, bh);
        uint s0 = AomSad.Sad(src, srcOff, srcStride, last, lastOff - lastStride, lastStride, bw, bh);
        uint s1 = AomSad.Sad(src, srcOff, srcStride, last, lastOff - 1, lastStride, bw, bh);
        uint s2 = AomSad.Sad(src, srcOff, srcStride, last, lastOff + 1, lastStride, bw, bh);
        uint s3 = AomSad.Sad(src, srcOff, srcStride, last, lastOff + lastStride, lastStride, bw, bh);
        blkSad = (blkSad * 5) >> 3;
        return blkSad < s0 && blkSad < s1 && blkSad < s2 && blkSad < s3;
    }

    /// <summary>populate_thresh_to_force_zeromv_skip.</summary>
    public static void PopulateThreshToForceZeromvSkip(AomComp cpi)
    {
        if (cpi.Sf.rt_sf.part_early_exit_zeromv == 0) return;
        const uint threshExit128 = 10000;   // FORCE_ZMV_SKIP_128X128_BLK_DIFF
        const int num128Pix = 128 * 128;
        for (int bsize = BLOCK_4X4; bsize < BLOCK_SIZES_ALL; bsize++)
        {
            int numBlockPix = BlockSizeWide[bsize] * BlockSizeHigh[bsize];
            uint t = (uint)(threshExit128 * Math.Sqrt((double)numBlockPix / num128Pix) + 0.5);
            t = Math.Min(t, (uint)(4 * numBlockPix));   // FORCE_ZMV_SKIP_MAX_PER_PIXEL_DIFF
            cpi.Rt!.ZeromvSkipThreshExitPart[bsize] = t;
        }
    }
}
