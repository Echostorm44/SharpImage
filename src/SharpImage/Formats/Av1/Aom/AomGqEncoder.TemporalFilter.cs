using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/temporal_filter.c (av1_tf_info_filtering, av1_temporal_filter with
// tf_setup_filtering_buffer / tf_motion_search / tf_build_predictor / tf_normalize_filtered_frame,
// av1_estimate_noise_level, av1_check_show_filtered_frame). The weighting follows the x86 kernels libaom runs
// (av1_apply_temporal_filter_avx2 / av1_highbd_apply_temporal_filter_avx2: the per-subblock d_factor * decay product
// and mse * inv_factor are formed first); 4:2:2 high bit depth uses the C kernel.
internal sealed partial class AomGqEncoder
{
    private const int TF_BLOCK = 64, NUM_16X16 = 16, TF_WEIGHT_SCALE = 1000, TF_WINDOW_BLOCK_BALANCE_WEIGHT = 5, TF_Q_DECAY_THRESHOLD = 20,
        TF_SEARCH_ERROR_NORM_WEIGHT = 20, TF_STRENGTH_THRESHOLD = 4, TF_QINDEX_CUTOFF = 128, NOISE_ESTIMATION_EDGE_THRESHOLD = 50,
        TF_LOOKAHEAD_IDX_THR = 7, ArnrStrength = 5;
    private const double TF_SEARCH_DISTANCE_THRESHOLD = 0.1, SQRT_PI_BY_2 = 1.25331413732;

    /// <summary>ppi->tf_info.</summary>
    private readonly AomFrameBuffer?[] _tfBuf = new AomFrameBuffer?[2];
    private readonly bool[] _tfBufValid = new bool[2];
    private readonly int[] _tfBufGfIndex = new int[2], _tfBufDisplayIndexOffset = new int[2];
    private readonly long[] _tfDiffSum = new long[2], _tfDiffSse = new long[2];
    /// <summary>The compressor of the last encoded frame (cpi->sf as the temporal filter sees it).</summary>
    private AomComp? _lastCpi;

    /// <summary>av1_tf_info_filtering.</summary>
    private void TfInfoFiltering()
    {
        if (!(ArnrMaxFrames > 0 && _cfg.LagInFrames > 1)) return;
        var gf = _gfGroup;
        for (int g = 0; g < gf.Size; ++g)
        {
            int ut = gf.UpdateType[g];
            if (ut != KF_UPDATE && ut != ARF_UPDATE) continue;
            int bufIdx = gf.FrameType[g] == INTER_FRAME ? 1 : 0;
            int lookaheadIdx = gf.ArfSrcOffset[g] + gf.CurFrameIdx[g];
            if (!_tfBufValid[bufIdx] || _tfBufDisplayIndexOffset[bufIdx] != lookaheadIdx)
            {
                var outBuf = new AomFrameBuffer(_cfg.Width, _cfg.Height, _cfg.SsX, _cfg.SsY, _cfg.Monochrome, _cfg.BitDepth);
                TemporalFilter(lookaheadIdx, g, true, out _tfDiffSum[bufIdx], out _tfDiffSse[bufIdx], outBuf);
                AomResize.ExtendFrameBorders(outBuf);
                _tfBuf[bufIdx] = outBuf;
                _tfBufGfIndex[bufIdx] = g;
                _tfBufDisplayIndexOffset[bufIdx] = lookaheadIdx;
                _tfBufValid[bufIdx] = true;
            }
        }
    }

    /// <summary>av1_tf_info_get_filtered_buf.</summary>
    private AomFrameBuffer? TfInfoGetFilteredBuf(int gfIndex, out long diffSum, out long diffSse)
    {
        AomFrameBuffer? outBuf = null;
        diffSum = diffSse = 0;
        if (!(ArnrMaxFrames > 0 && _cfg.LagInFrames > 1)) return null;
        for (int i = 0; i < 2; ++i)
            if (_tfBufValid[i] && _tfBufGfIndex[i] == gfIndex)
            {
                outBuf = _tfBuf[i];
                diffSum = _tfDiffSum[i];
                diffSse = _tfDiffSse[i];
            }
        return outBuf;
    }

    /// <summary>av1_check_show_filtered_frame.</summary>
    private static bool CheckShowFilteredFrame(AomFrameBuffer frame, long diffSum, long diffSse, int qIndex, int bitDepth, bool enableOverlay,
        bool isSecondArf)
    {
        if (!enableOverlay || isSecondArf) return true;
        int mbRows = (frame.CropHeights[0] + TF_BLOCK - 1) / TF_BLOCK, mbCols = (frame.CropWidths[0] + TF_BLOCK - 1) / TF_BLOCK;
        int numMbs = Math.Max(1, mbRows * mbCols);
        float mean = (float)diffSum / numMbs;
        float std = (float)Math.Sqrt((float)diffSse / numMbs - mean * mean);
        int acQStep = Av1Tables.DequantTable[bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2, qIndex, 1];
        float threshold = 0.7f * acQStep * acQStep;
        return mean < threshold && std < mean * 1.2;
    }

    /// <summary>get_q (temporal_filter.c).</summary>
    private int TfGetQ()
    {
        if (_rcModeQ) return (int)ConvertQindexToQ(_cqLevel, _cfg.BitDepth);
        return (int)ConvertQindexToQ(_pRc.AvgFrameQindex[_gfGroup.FrameType[_gfFrameIndex]], _cfg.BitDepth);
    }

    /// <summary>av1_estimate_noise_level.</summary>
    private static void EstimateNoiseLevel(AomFrameBuffer f, double[] noiseLevel, int planeFrom, int planeTo, int bitDepth, int edgeThresh)
    {
        for (int plane = planeFrom; plane <= planeTo; plane++)
        {
            int uv = plane != 0 ? 1 : 0;
            int height = f.CropHeights[uv], width = f.CropWidths[uv], stride = f.Strides[plane];
            long accum = 0;
            int count = 0;
            int off = f.Offsets[plane];
            bool hbd = f.Hbd;
            int sh = hbd ? bitDepth - 8 : 0;
            for (int i = 1; i < height - 1; ++i)
                for (int j = 1; j < width - 1; ++j)
                {
                    int c = off + i * stride + j;
                    int m00, m01, m02, m10, m11, m12, m20, m21, m22;
                    if (hbd)
                    {
                        var s = f.Buffers16[plane];
                        m00 = s[c - stride - 1]; m01 = s[c - stride]; m02 = s[c - stride + 1];
                        m10 = s[c - 1]; m11 = s[c]; m12 = s[c + 1];
                        m20 = s[c + stride - 1]; m21 = s[c + stride]; m22 = s[c + stride + 1];
                    }
                    else
                    {
                        var s = f.Buffers[plane];
                        m00 = s[c - stride - 1]; m01 = s[c - stride]; m02 = s[c - stride + 1];
                        m10 = s[c - 1]; m11 = s[c]; m12 = s[c + 1];
                        m20 = s[c + stride - 1]; m21 = s[c + stride]; m22 = s[c + stride + 1];
                    }
                    int gx = (m00 - m02) + (m20 - m22) + 2 * (m10 - m12);
                    int gy = (m00 - m20) + (m02 - m22) + 2 * (m01 - m21);
                    int ga = sh > 0 ? (Math.Abs(gx) + Math.Abs(gy) + (1 << (sh - 1))) >> sh : Math.Abs(gx) + Math.Abs(gy);
                    if (ga < edgeThresh)
                    {
                        int v = 4 * m11 - 2 * (m01 + m21 + m10 + m12) + (m00 + m02 + m20 + m22);
                        accum += sh > 0 ? (Math.Abs(v) + (1 << (sh - 1))) >> sh : Math.Abs(v);
                        ++count;
                    }
                }
            noiseLevel[plane] = count < 16 ? -1.0 : (double)accum / (6 * count) * SQRT_PI_BY_2;
        }
    }

    /// <summary>The compressor whose speed features / frame size the temporal filter's motion search uses.</summary>
    private AomComp TfCompressor()
    {
        if (_lastCpi != null) return _lastCpi;
        // av1_check_initial_width: framesize independent + dependent speed features for the initial (key) state
        var c = _cfg;
        var cm = new AomCommon(c.Width, c.Height, c.SsX, c.SsY, c.Monochrome, SelectSbSize(c), c.BitDepth);
        var cpi = new AomComp { Cm = cm, Speed = c.Speed, SbSize = cm.SbSize, BitDepth = c.BitDepth, UseHighbitdepth = c.BitDepth > 8, Mode = GOOD };
        cpi.BorderInPixels = _resizeModeFixed ? 288 : BlockSizeWide[cm.SbSize] + 32;
        if (c.Sharpness is int sh) cpi.Sharpness = sh;
        var sfIn = new AomSpeedFeatureInputs
        {
            Width = c.Width, Height = c.Height, UseHighBitDepth = c.BitDepth > 8, BaseQindex = 0, FrameType = KEY_FRAME, UpdateType = KF_UPDATE,
            GfFrameType = KEY_FRAME, Mode = GOOD, LapEnabled = _lapEnabled, NumWorkers = 1, Sharpness = cpi.Sharpness,
            Tuning = c.Tune switch { AomTune.Iq => AOM_TUNE_IQ, AomTune.Ssim => AOM_TUNE_SSIM, _ => AOM_TUNE_PSNR },
        };
        var seq = new AomSpeedFeatureSeqFlags { enable_restoration = _seqFlags.enable_restoration, SeqParamsLocked = true };
        cpi.Sf.SetFramesizeIndependent(sfIn, seq, cpi.WinnerModeParams, c.Speed);
        cpi.Sf.SetFramesizeDependent(sfIn, seq, c.Speed);
        cpi.SearchSites = AomMcomp.InitSearchSites();
        _lastCpi = cpi;
        return cpi;
    }

    private sealed class TfCtx
    {
        public AomFrameBuffer[] Frames = null!;
        public int NumFrames, FilterFrameIdx, MbRows, MbCols, NumPels, QFactor;
        public readonly double[] NoiseLevels = new double[3];
        public AomFrameBuffer Output = null!;
        public bool ComputeDiff;
        public long DiffSum, DiffSse;
    }

    /// <summary>av1_temporal_filter.</summary>
    private void TemporalFilter(int filterFrameLookaheadIdx, int gfFrameIndex, bool computeDiff, out long diffSum, out long diffSse,
        AomFrameBuffer output)
    {
        var ctx = new TfCtx { Output = output, ComputeDiff = computeDiff };
        TfSetupFilteringBuffer(ctx, filterFrameLookaheadIdx, gfFrameIndex);
        var toFilter = ctx.Frames[ctx.FilterFrameIdx];
        ctx.MbRows = (toFilter.CropHeights[0] + TF_BLOCK - 1) / TF_BLOCK;
        ctx.MbCols = (toFilter.CropWidths[0] + TF_BLOCK - 1) / TF_BLOCK;
        int numPlanes = _cfg.Monochrome ? 1 : 3;
        int numPels = 0;
        for (int i = 0; i < numPlanes; i++) numPels += (TF_BLOCK * TF_BLOCK) >> (i == 0 ? 0 : _cfg.SsX + _cfg.SsY);
        ctx.NumPels = numPels;
        ctx.QFactor = TfGetQ();

        var cpi = TfCompressor();
        var cm = cpi.Cm;
        bool savedHp = cm.AllowHighPrecisionMv;
        int savedUpdateType = cpi.UpdateType;
        cm.AllowHighPrecisionMv = true;   // av1_set_high_precision_mv(cpi, 1, 0) at the frame start
        cpi.UpdateType = _gfGroup.UpdateType[_gfFrameIndex];
        var x = new AomMacroblock();
        var xd = x.E;
        xd.Bd = _cfg.BitDepth;
        for (int p = 0; p < 3; p++)
        {
            xd.Plane[p].SubsamplingX = p == 0 ? 0 : _cfg.SsX;
            xd.Plane[p].SubsamplingY = p == 0 ? 0 : _cfg.SsY;
        }
        xd.BlockRefScaleFactors[0] = AomInterPred.Identity;
        xd.BlockRefScaleFactors[1] = AomInterPred.Identity;
        xd.Mi0 = new AomMbModeInfo { MotionMode = SIMPLE_TRANSLATION };
        var accum = new uint[ctx.NumPels];
        var count = new ushort[ctx.NumPels];
        var pred8 = new byte[ctx.NumPels];
        var pred16 = new ushort[ctx.NumPels];
        for (int mbRow = 0; mbRow < ctx.MbRows; mbRow++)
            TfDoFilteringRow(cpi, x, ctx, mbRow, accum, count, pred8, pred16);
        cm.AllowHighPrecisionMv = savedHp;
        cpi.UpdateType = savedUpdateType;
        diffSum = ctx.DiffSum;
        diffSse = ctx.DiffSse;
        if (AomTrace.Out != null)
        {
            for (int p = 0; p < numPlanes; p++)
            {
                ulong h = 0;
                int uv = p > 0 ? 1 : 0;
                for (int r = 0; r < output.CropHeights[uv]; r++)
                    for (int c = 0; c < output.CropWidths[uv]; c++)
                        h = h * 31 + (output.Hbd ? output.Buffers16[p][output.Offsets[p] + r * output.Strides[p] + c] : (ulong)output.Buffers[p][output.Offsets[p] + r * output.Strides[p] + c]);
                AomTrace.Out.Write($"tfo {filterFrameLookaheadIdx} {gfFrameIndex} {ctx.NumFrames} {ctx.FilterFrameIdx} p{p} {h:x16}" + (char)10);
            }
            AomTrace.Out.Write(FormattableString.Invariant($"tfd {ctx.DiffSum} {ctx.DiffSse} noise {ctx.NoiseLevels[0]:F6} {ctx.NoiseLevels[1]:F6} q {ctx.QFactor}") + (char)10);
        }
    }

    /// <summary>tf_setup_filtering_buffer.</summary>
    private void TfSetupFilteringBuffer(TfCtx ctx, int filterFrameLookaheadIdx, int gfFrameIndex)
    {
        var gf = _gfGroup;
        int updateType = gf.UpdateType[gfFrameIndex];
        int frameType = gf.FrameType[gfFrameIndex];
        bool isForwardKeyframe = gf.FrameType[gfFrameIndex] == KEY_FRAME && gf.RefbufState[gfFrameIndex] == REFBUF_UPDATE;
        int numFrames = Math.Max(ArnrMaxFrames, 1);
        int numBefore = 0, numAfter = 0;
        int lookaheadDepth = LookaheadDepth(EncodeStage);
        int keyToCurframe = Math.Max(_rc.FramesSinceKey + filterFrameLookaheadIdx, 0);
        int curframeToKey = Math.Max(_rc.FramesToKey - filterFrameLookaheadIdx - 1, 0);
        int maxBefore = Math.Min(filterFrameLookaheadIdx, keyToCurframe);
        int maxAfter = Math.Min(lookaheadDepth - filterFrameLookaheadIdx - 1, curframeToKey);
        var toFilterBuf = LookaheadPeek(filterFrameLookaheadIdx, EncodeStage)!;
        var toFilterFrame = toFilterBuf.Img;
        int numPlanes = _cfg.Monochrome ? 1 : 3;
        EstimateNoiseLevel(toFilterFrame, ctx.NoiseLevels, 0, numPlanes - 1, _cfg.BitDepth, NOISE_ESTIMATION_EDGE_THRESHOLD);
        int q = TfGetQ();
        int stats = _twopass.StatsIn - (_rc.FramesSinceKey == 0 ? 1 : 0);
        double accuCoeff0 = 1.0, accuCoeff1 = 1.0;
        for (int i = 1; i <= maxAfter; i++)
        {
            if (stats + filterFrameLookaheadIdx + i >= _twopass.InEnd)
            {
                maxAfter = i - 1;
                break;
            }
            accuCoeff1 *= Math.Max(_twopass.Buf[stats + filterFrameLookaheadIdx + i].CorCoeff, 0.001);
        }
        if (maxAfter >= 1) accuCoeff1 = Math.Pow(accuCoeff1, 1.0 / maxAfter);
        for (int i = 1; i <= maxBefore; i++)
        {
            if (stats + filterFrameLookaheadIdx - i + 1 <= _twopass.InStart)
            {
                maxBefore = i - 1;
                break;
            }
            accuCoeff0 *= Math.Max(_twopass.Buf[stats + filterFrameLookaheadIdx - i + 1].CorCoeff, 0.001);
        }
        if (maxBefore >= 1) accuCoeff0 = Math.Pow(accuCoeff0, 1.0 / maxBefore);

        int adjustNum = 6;
        var sf = TfCompressor().Sf;
        int adjustLvl = sf.hl_sf.adjust_num_frames_for_arf_filtering;
        if (numFrames == 1) adjustNum = 0;
        else if (updateType == KF_UPDATE && q <= 10) adjustNum = 0;
        else if (adjustLvl > 0 && updateType != KF_UPDATE && _rc.FramesSinceKey > 0)
        {
            int[,] adj = { { 6, 4, 2 }, { 4, 2, 0 } };
            if (ctx.NoiseLevels[0] < 0.5) adjustNum = adj[adjustLvl - 1, 0];
            else if (ctx.NoiseLevels[0] < 1.0) adjustNum = adj[adjustLvl - 1, 1];
            else adjustNum = adj[adjustLvl - 1, 2];
        }
        numFrames = Math.Min(numFrames + adjustNum, lookaheadDepth);
        if (frameType == KEY_FRAME)
        {
            numBefore = Math.Min(isForwardKeyframe ? numFrames / 2 : 0, maxBefore);
            numAfter = Math.Min(numFrames - 1, maxAfter);
        }
        else
        {
            int gfuBoost = CalcArfBoost(filterFrameLookaheadIdx, maxBefore, maxAfter, true);
            numFrames = Math.Min(numFrames, gfuBoost / 150);
            numFrames += (numFrames & 1) == 0 ? 1 : 0;
            if (updateType == INTNL_ARF_UPDATE) numFrames = Math.Min(numFrames, 3);
            if (Math.Min(maxAfter, maxBefore) >= numFrames / 2)
            {
                numBefore = numFrames / 2;
                numAfter = numFrames / 2;
            }
            else
            {
                if (maxAfter < numFrames / 2)
                {
                    numAfter = maxAfter;
                    numBefore = Math.Min(numFrames - 1 - numAfter, maxBefore);
                }
                else
                {
                    numBefore = maxBefore;
                    numAfter = Math.Min(numFrames - 1 - numBefore, maxAfter);
                }
                if (maxAfter > 0 && maxBefore > 0)
                {
                    if (numAfter < numBefore)
                    {
                        int insym = (int)(0.4 / Math.Max(1 - accuCoeff1, 0.01));
                        numBefore = Math.Min(numBefore, numAfter + insym);
                    }
                    else
                    {
                        int insym = (int)(0.4 / Math.Max(1 - accuCoeff0, 0.01));
                        numAfter = Math.Min(numAfter, numBefore + insym);
                    }
                }
            }
        }
        numFrames = numBefore + 1 + numAfter;
        ctx.Frames = new AomFrameBuffer[numFrames];
        for (int frame = 0; frame < numFrames; ++frame)
            ctx.Frames[frame] = LookaheadPeek(frame - numBefore + filterFrameLookaheadIdx, EncodeStage)!.Img;
        ctx.NumFrames = numFrames;
        ctx.FilterFrameIdx = numBefore;
    }

    private static void TfSetPlane0(ref AomBuf2d b, AomFrameBuffer f, int off)
    {
        b.Buf = f.Buffers[0]; b.Buf16 = f.Buffers16[0];
        b.Offset0 = f.Offsets[0];
        b.Offset = f.Offsets[0] + off;
        b.Stride = f.Strides[0];
        b.Width = f.CropWidths[0]; b.Height = f.CropHeights[0];
    }

    private static void TfSetRowLimits(AomComp cpi, AomMacroblock x, int miRow, int miHeight)
    {
        var cm = cpi.Cm;
        int border = cpi.BorderInPixels;
        x.MvLimits.RowMin = Math.Max(-(miRow * 4 + border - 8), -((miRow + miHeight) * 4 + 8));
        x.MvLimits.RowMax = Math.Min((cm.MiRows - miRow - miHeight) * 4 + border - 8, (cm.MiRows - miRow) * 4 + 8);
    }

    private static void TfSetColLimits(AomComp cpi, AomMacroblock x, int miCol, int miWidth)
    {
        var cm = cpi.Cm;
        int border = cpi.BorderInPixels;
        x.MvLimits.ColMin = Math.Max(-(miCol * 4 + border - 8), -((miCol + miWidth) * 4 + 8));
        x.MvLimits.ColMax = Math.Min((cm.MiCols - miCol - miWidth) * 4 + border - 8, (cm.MiCols - miCol) * 4 + 8);
    }

    /// <summary>The block-size variance (fn_ptr[bsize].vf) of a against b (b null: against zeros).</summary>
    private static uint TfVariance(AomFrameBuffer? fa, byte[]? a8, ushort[]? a16, int aOff, int aStride, byte[]? b8, ushort[]? b16, int bOff, int bStride,
        int w, int h, int bd, out uint sse)
    {
        if (a16 != null) return AomHbd.Variance(a16, aOff, aStride, b16, bOff, bStride, 0, w, h, bd, out sse);
        if (b8 == null)
        {
            long sum = 0; ulong sq = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) { int d = a8![aOff + y * aStride + x]; sum += d; sq += (ulong)(d * d); }
            sse = (uint)sq;
            int bits = System.Numerics.BitOperations.Log2((uint)(w * h));
            return sse - (uint)((sum * sum) >> bits);
        }
        return AomSad.Variance(a8!, aOff, aStride, b8, bOff, bStride, w, h, out sse);
    }

    /// <summary>subblock_motion_search.</summary>
    private static void TfSubblockMotionSearch(AomComp cpi, AomMacroblock x, AomFrameBuffer frameToFilter, AomFrameBuffer refFrame, int mbRow, int mbCol,
        int subblockSize, int idx, int ofstI, int ofstJ, AomMv startMv, int mvCostType, int stepParam, AomMv[] subblockMvs, int[] subblockMses, int q)
    {
        var xd = x.E;
        int yStride = frameToFilter.Strides[0];
        int yOffset = mbRow * TF_BLOCK * yStride + mbCol * TF_BLOCK;
        int sbH = BlockSizeHigh[subblockSize], sbW = BlockSizeWide[subblockSize];
        int subblockPels = sbH * sbW;
        TfSetRowLimits(cpi, x, (mbRow << 4) + (ofstI >> 2), sbH >> 2);
        TfSetColLimits(cpi, x, (mbCol << 4) + (ofstJ >> 2), sbW >> 2);
        int boffset = ofstI * yStride + ofstJ;
        TfSetPlane0(ref x.Plane[0].Src, frameToFilter, yOffset + boffset);
        x.Plane[0].Src.Width = frameToFilter.CropWidths[0];
        TfSetPlane0(ref xd.Plane[0].Pre(0), refFrame, yOffset + boffset);
        var fp = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, subblockSize, default, cpi.SearchSites, NSTEP, false, x.MvLimits, startMv);
        fp.RunMeshSearch = true;
        fp.MvCostType = mvCostType;
        if (cpi.Sf.mv_sf.prune_mesh_search == PRUNE_MESH_SEARCH_LVL_1)
        {
            fp.PruneMeshSearch = q > 20;
            fp.MeshSearchMvDiffThreshold = 2;
        }
        var costList = new int[5];
        AomMv dummy = default;
        AomMcomp.FullPixelSearch(startMv, fp, stepParam, AomSubpel.CondCostList(cpi, costList), out var bestFull, out var stats, ref dummy, false);
        var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, subblockSize, default, costList);
        ms.ForcedStop = AomSubpel.EIGHTH_PEL;
        ms.SubpelSearchType = AomSubpel.USE_8_TAPS;
        ms.MvCostType = AomMcomp.MV_COST_NONE;
        stats.ErrCost = 0;
        int error = AomSubpel.FindFractionalMvStep(cpi, ms, bestFull.ToMv(), stats, out var bestMv, out _, out _, null);
        subblockMses[idx] = (error + (subblockPels >> 1)) / subblockPels;
        subblockMvs[idx] = bestMv;
    }

    /// <summary>tf_motion_search.</summary>
    private static void TfMotionSearch(AomComp cpi, AomMacroblock x, AomFrameBuffer frameToFilter, AomFrameBuffer refFrame, int mbRow, int mbCol,
        ref AomMv refMv, bool allowMeForSubBlks, AomMv[] subblockMvs, int[] subblockMses, out bool isDcDiffLarge, out bool isLowCntras, int q)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int minFrameSize = Math.Min(cm.Width, cm.Height);
        int mbPels = TF_BLOCK * TF_BLOCK;
        int yStride = frameToFilter.Strides[0];
        int yOffset = mbRow * TF_BLOCK * yStride + mbCol * TF_BLOCK;
        var oriSrc = x.Plane[0].Src;
        var oriPre = xd.Plane[0].Pre(0);
        int stepParam = AomMcomp.InitSearchRange(Math.Max(frameToFilter.CropWidths[0], frameToFilter.CropHeights[0]));
        bool forceIntegerMv = cm.CurFrameForceIntegerMv;
        int mvCostType = minFrameSize >= 720 ? AomMcomp.MV_COST_L1_HDRES : minFrameSize >= 480 ? AomMcomp.MV_COST_L1_MIDRES : AomMcomp.MV_COST_L1_LOWRES;
        var startMv = refMv.ToFullMv();
        TfSetPlane0(ref x.Plane[0].Src, frameToFilter, yOffset);
        TfSetPlane0(ref xd.Plane[0].Pre(0), refFrame, yOffset);
        xd.MiRow = mbRow * (TF_BLOCK / 4);
        xd.MiCol = mbCol * (TF_BLOCK / 4);
        isDcDiffLarge = false;
        isLowCntras = false;
        var src = x.Plane[0].Src;
        long srcVar = int.MaxValue;
        if (cpi.Sharpness != 0)
            srcVar = TfVariance(frameToFilter, src.Buf16 == null ? src.Buf : null, src.Buf16, src.Offset, yStride, null, src.Buf16 != null ? new ushort[TF_BLOCK * TF_BLOCK] : null,
                0, TF_BLOCK, TF_BLOCK, TF_BLOCK, xd.Bd, out _);
        int blockMse = int.MaxValue;
        var blockMv = default(AomMv);
        var midblockMses = new[] { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
        var midblockMvs = new AomMv[4];
        var fp = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, BLOCK_64X64, default, cpi.SearchSites, NSTEP, false, x.MvLimits, startMv);
        fp.RunMeshSearch = true;
        fp.MvCostType = mvCostType;
        if (cpi.Sf.mv_sf.prune_mesh_search == PRUNE_MESH_SEARCH_LVL_1)
        {
            fp.PruneMeshSearch = q > 20;
            fp.MeshSearchMvDiffThreshold = 2;
        }
        var costList = new int[5];
        AomMv dummy = default;
        AomMcomp.FullPixelSearch(startMv, fp, stepParam, AomSubpel.CondCostList(cpi, costList), out var bestFull, out var bestStats, ref dummy, false);
        if (forceIntegerMv)
        {
            var bm = new AomMv(bestFull.Row * 8, bestFull.Col * 8);
            int mvOffset = bestFull.Row * yStride + bestFull.Col;
            var refF = refFrame;
            uint error = TfVariance(refF, refF.Hbd ? null : refF.Buffers[0], refF.Hbd ? refF.Buffers16[0] : null, refF.Offsets[0] + yOffset + mvOffset, yStride,
                frameToFilter.Hbd ? null : frameToFilter.Buffers[0], frameToFilter.Hbd ? frameToFilter.Buffers16[0] : null, frameToFilter.Offsets[0] + yOffset,
                yStride, TF_BLOCK, TF_BLOCK, xd.Bd, out _);
            blockMse = (int)((error + (uint)(mbPels >> 1)) / (uint)mbPels);
            blockMv = bm;
            if (srcVar <= 2 * (long)error) isLowCntras = true;
        }
        else
        {
            var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, BLOCK_64X64, default, costList);
            ms.ForcedStop = AomSubpel.EIGHTH_PEL;
            ms.SubpelSearchType = AomSubpel.USE_8_TAPS;
            ms.MvCostType = AomMcomp.MV_COST_NONE;
            bestStats.ErrCost = 0;
            int error = AomSubpel.FindFractionalMvStep(cpi, ms, bestFull.ToMv(), bestStats, out var bestMv, out int distortion, out uint sse, null);
            blockMse = (error + (mbPels >> 1)) / mbPels;
            blockMv = bestMv;
            refMv = bestMv;
            isDcDiffLarge = 50L * (uint)error < sse;
            if (srcVar <= 2 * (long)distortion) isLowCntras = true;
            if (allowMeForSubBlks)
            {
                int midSize = BLOCK_32X32, subSize = BLOCK_16X16;
                int midH = 32, midW = 32, subH = 16, subW = 16;
                int midIdx = 0;
                for (int i = 0; i < TF_BLOCK; i += midH)
                    for (int j = 0; j < TF_BLOCK; j += midW)
                    {
                        startMv = refMv.ToFullMv();
                        TfSubblockMotionSearch(cpi, x, frameToFilter, refFrame, mbRow, mbCol, midSize, midIdx, i, j, startMv, mvCostType, stepParam,
                            midblockMvs, midblockMses, q);
                        startMv = midblockMvs[midIdx].ToFullMv();
                        int bidx = midIdx * 4;
                        for (int bi = 0; bi < midH; bi += subH)
                            for (int bj = 0; bj < midW; bj += subW)
                            {
                                TfSubblockMotionSearch(cpi, x, frameToFilter, refFrame, mbRow, mbCol, subSize, bidx, i + bi, j + bj, startMv, mvCostType,
                                    stepParam, subblockMvs, subblockMses, q);
                                ++bidx;
                            }
                        ++midIdx;
                    }
            }
        }
        x.Plane[0].Src = oriSrc;
        xd.Plane[0].Pre(0) = oriPre;
        if (allowMeForSubBlks) TfDetermineBlockPartition(blockMv, blockMse, midblockMvs, midblockMses, subblockMvs, subblockMses);
        else
            for (int i = 0; i < NUM_16X16; ++i)
            {
                subblockMvs[i] = blockMv;
                subblockMses[i] = blockMse;
            }
        int thresh = minFrameSize >= 720 ? 12 : 3;
        if (blockMse > (thresh << (xd.Bd - 8))) refMv = default;
    }

    /// <summary>tf_determine_block_partition.</summary>
    private static void TfDetermineBlockPartition(AomMv blockMv, int blockMse, AomMv[] midMvs, int[] midMses, AomMv[] subMvs, int[] subMses)
    {
        int minSub, maxSub;
        long sumSub;
        for (int idx = 0; idx < 4; ++idx)
        {
            minSub = int.MaxValue; maxSub = int.MinValue; sumSub = 0;
            int s = idx * 4;
            for (int i = s; i < s + 4; ++i)
            {
                sumSub += subMses[i];
                minSub = Math.Min(minSub, subMses[i]);
                maxSub = Math.Max(maxSub, subMses[i]);
            }
            if ((midMses[idx] * 15L <= sumSub * 4 && maxSub - minSub < 48) || (midMses[idx] * 14L <= sumSub * 4 && maxSub - minSub < 24))
                for (int i = s; i < s + 4; ++i)
                {
                    subMvs[i] = midMvs[idx];
                    subMses[i] = midMses[idx];
                }
        }
        minSub = int.MaxValue; maxSub = int.MinValue; sumSub = 0;
        for (int i = 0; i < NUM_16X16; ++i)
        {
            sumSub += subMses[i];
            minSub = Math.Min(minSub, subMses[i]);
            maxSub = Math.Max(maxSub, subMses[i]);
        }
        if ((blockMse * 15L <= sumSub && (long)(maxSub - minSub) * 16 < sumSub * 3) || (blockMse * 14L <= sumSub && (long)(maxSub - minSub) * 8 < sumSub))
            for (int i = 0; i < NUM_16X16; ++i)
            {
                subMvs[i] = blockMv;
                subMses[i] = blockMse;
            }
    }

    /// <summary>tf_build_predictor (MULTITAP_SHARP2 per 16x16 sub-block).</summary>
    private void TfBuildPredictor(AomFrameBuffer refFrame, int mbRow, int mbCol, int numPlanes, AomMv[] subblockMvs, byte[] pred8, ushort[] pred16)
    {
        int bd = _cfg.BitDepth;
        bool hbd = refFrame.Hbd;
        uint filters = ((uint)MULTITAP_SHARP2 << 16) | (uint)MULTITAP_SHARP2;
        int planeOffset = 0;
        var ip = new AomInterPredParams();
        int alignedW = (_cfg.Width + 7) & ~7, alignedH = (_cfg.Height + 7) & ~7;
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            int ssy = plane == 0 ? 0 : _cfg.SsY, ssx = plane == 0 ? 0 : _cfg.SsX;
            int planeH = TF_BLOCK >> ssy, planeW = TF_BLOCK >> ssx;
            int planeY = (TF_BLOCK * mbRow) >> ssy, planeX = (TF_BLOCK * mbCol) >> ssx;
            int h32 = planeH >> 1, w32 = planeW >> 1, h16 = planeH >> 2, w16 = planeW >> 2;
            var refBuf = new AomBuf2d
            {
                Buf = refFrame.Buffers[plane], Buf16 = refFrame.Buffers16[plane], Offset0 = refFrame.Offsets[plane], Offset = refFrame.Offsets[plane],
                Stride = refFrame.Strides[plane], Width = plane == 0 ? alignedW : (alignedW + ssx) >> ssx, Height = plane == 0 ? alignedH : (alignedH + ssy) >> ssy,
            };
            int[] subY = { 0, 0, h32, h32 }, subX = { 0, w32, 0, w32 };
            for (int idx = 0; idx < 4; ++idx)
            {
                int sbi = idx * 4;
                for (int i = 0; i < h32; i += h16)
                    for (int j = 0; j < w32; j += w16)
                    {
                        var mv = subblockMvs[sbi++];
                        int y = planeY + subY[idx] + i, xx = planeX + subX[idx] + j;
                        AomInterPred.InitInterParams(ip, w16, h16, y, xx, ssx, ssy, bd, hbd, false, AomInterPred.Identity, refBuf, filters);
                        ip.ConvParams = AomConvParams.Get(0, plane, bd);
                        AomInterPred.BuildOneInterPredictor(hbd ? null : pred8, hbd ? pred16 : null, planeOffset + (subY[idx] + i) * planeW + subX[idx] + j,
                            planeW, mv, ip);
                    }
            }
            planeOffset += planeH * planeW;
        }
    }

    /// <summary>The weighting kernels (av1_apply_temporal_filter_avx2 / av1_highbd_apply_temporal_filter_avx2 order of
    /// operations; av1_apply_temporal_filter_c for 4:2:2 high bit depth).</summary>
    private void TfApplyTemporalFilter(AomFrameBuffer frameToFilter, int mbRow, int mbCol, int numPlanes, double[] noiseLevels, AomMv[] subblockMvs,
        int[] subblockMses, int qFactor, int filterStrength, int tfWgtCalcLvl, byte[] pred8, ushort[] pred16, uint[] accum, ushort[] count)
    {
        bool hbd = frameToFilter.Hbd;
        int bd = _cfg.BitDepth;
        bool cKernel = hbd && _cfg.SsX != _cfg.SsY && numPlanes > 1;
        int minFrameSize = Math.Min(frameToFilter.CropHeights[0], frameToFilter.CropWidths[0]);
        double invFactor = 1.0 / ((TF_WINDOW_BLOCK_BALANCE_WEIGHT + 1) * TF_SEARCH_ERROR_NORM_WEIGHT);
        double weightFactor = TF_WINDOW_BLOCK_BALANCE_WEIGHT * invFactor;
        double qDecay = Math.Pow((double)qFactor / TF_Q_DECAY_THRESHOLD, 2);
        qDecay = Math.Clamp(qDecay, 1e-5, 1);
        if (qFactor >= TF_QINDEX_CUTOFF) qDecay = 0.5 * Math.Pow((double)qFactor / 64, 2);
        double sDecay = Math.Pow((double)filterStrength / TF_STRENGTH_THRESHOLD, 2);
        sDecay = Math.Clamp(sDecay, 1e-5, 1);
        var dFactor = new double[NUM_16X16];
        double distanceThreshold = Math.Max(minFrameSize * TF_SEARCH_DISTANCE_THRESHOLD, 1);
        for (int i = 0; i < NUM_16X16; i++)
        {
            var mv = subblockMvs[i];
            double distance = Math.Sqrt(Math.Pow(mv.Row, 2) + Math.Pow(mv.Col, 2));
            dFactor[i] = Math.Max(distance / distanceThreshold, 1);
        }
        var squareDiff = new uint[TF_BLOCK * TF_BLOCK];
        var lumaSseSum = new uint[TF_BLOCK * TF_BLOCK];
        int planeOffset = 0;
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            int ssy = plane == 0 ? 0 : _cfg.SsY, ssx = plane == 0 ? 0 : _cfg.SsX;
            int h = TF_BLOCK >> ssy, w = TF_BLOCK >> ssx;
            int frameStride = frameToFilter.Strides[plane];
            int frameOffset = frameToFilter.Offsets[plane] + mbRow * h * frameStride + mbCol * w;
            int numRefPixels = 25 + (plane != 0 ? 1 << (ssx + ssy) : 0);
            double invNumRefPixels = 1.0 / numRefPixels;
            double nDecay = 0.5 + Math.Log(2 * noiseLevels[plane] + 5.0);
            double decayFactor = 1 / (nDecay * qDecay * sDecay);
            // the luma sse sum for the chroma planes (computed at U from the luma square differences)
            if (plane == 1)
            {
                Array.Clear(lumaSseSum);
                int lw = w << ssx;
                for (int i = 0; i < h; ++i)
                    for (int j = 0; j < w; ++j)
                        for (int ii = 0; ii < (1 << ssy); ++ii)
                            for (int jj = 0; jj < (1 << ssx); ++jj)
                                lumaSseSum[i * w + j] += squareDiff[((i << ssy) + ii) * lw + (j << ssx) + jj];
            }
            // the square differences of the frame to filter and the prediction
            for (int i = 0; i < h; ++i)
                for (int j = 0; j < w; ++j)
                {
                    int r = hbd ? frameToFilter.Buffers16[plane][frameOffset + i * frameStride + j] : frameToFilter.Buffers[plane][frameOffset + i * frameStride + j];
                    int t = hbd ? pred16[planeOffset + i * w + j] : pred8[planeOffset + i * w + j];
                    uint d = (uint)Math.Abs(r - t);
                    squareDiff[i * w + j] = d * d;
                }
            var subMsesScaled = new double[NUM_16X16];
            var dDecayed = new double[NUM_16X16];
            for (int k = 0; k < NUM_16X16; k++)
            {
                subMsesScaled[k] = subblockMses[k] * invFactor;
                dDecayed[k] = dFactor[k] * decayFactor;
            }
            for (int i = 0; i < h; ++i)
                for (int j = 0; j < w; ++j)
                {
                    ulong sumSquareDiff = 0;
                    for (int wi = -2; wi <= 2; ++wi)
                        for (int wj = -2; wj <= 2; ++wj)
                        {
                            int y = Math.Clamp(i + wi, 0, h - 1), x = Math.Clamp(j + wj, 0, w - 1);
                            sumSquareDiff += squareDiff[y * w + x];
                        }
                    sumSquareDiff += plane != 0 ? lumaSseSum[i * w + j] : 0;
                    if (bd > 8) sumSquareDiff = cKernel ? sumSquareDiff >> ((bd - 8) * 2) : (uint)sumSquareDiff >> ((bd - 8) * 2);
                    double windowError = sumSquareDiff * invNumRefPixels;
                    int y32 = i / (h / 2), x32 = j / (w / 2), y16 = (i % (h / 2)) / (h / 4), x16 = (j % (w / 2)) / (w / 4);
                    int sbIdx = (y32 * 2 + x32) * 4 + (y16 * 2 + x16);
                    double scaledError;
                    if (cKernel)
                    {
                        double combined = weightFactor * windowError + (double)subblockMses[sbIdx] * invFactor;
                        scaledError = combined * dFactor[sbIdx] * decayFactor;
                    }
                    else
                    {
                        double combined = weightFactor * windowError + subMsesScaled[sbIdx];
                        scaledError = combined * dDecayed[sbIdx];
                    }
                    scaledError = Math.Min(scaledError, 7);
                    int weight;
                    if (tfWgtCalcLvl == 0) weight = (int)(Math.Exp(-scaledError) * TF_WEIGHT_SCALE);
                    else
                    {
                        float fw = ApproxExp((float)-scaledError) * TF_WEIGHT_SCALE;
                        weight = (int)(fw + 0.5f);
                    }
                    int idx = planeOffset + i * w + j;
                    int predValue = hbd ? pred16[idx] : pred8[idx];
                    accum[idx] += (uint)(weight * predValue);
                    count[idx] += (ushort)weight;
                }
            planeOffset += h * w;
        }
    }

    /// <summary>approx_exp.</summary>
    private static float ApproxExp(float y)
    {
        const float A = (1 << 23) / 0.69314718056f;
        int i = (int)(y * A) + ((127 << 23) - 60801);
        return BitConverter.Int32BitsToSingle(i);
    }

    /// <summary>av1_tf_do_filtering_row.</summary>
    private void TfDoFilteringRow(AomComp cpi, AomMacroblock x, TfCtx ctx, int mbRow, uint[] accum, ushort[] count, byte[] pred8, ushort[] pred16)
    {
        var frames = ctx.Frames;
        int numFrames = ctx.NumFrames, filterIdx = ctx.FilterFrameIdx;
        var frameToFilter = frames[filterIdx];
        int numPlanes = _cfg.Monochrome ? 1 : 3;
        int wgtLvl = cpi.Sf.hl_sf.weight_calc_level_in_tf;
        int filterStrength = ArnrStrength;
        int frameType = _gfGroup.FrameType[_gfFrameIndex];
        TfSetRowLimits(cpi, x, mbRow << 4, TF_BLOCK >> 2);
        for (int mbCol = 0; mbCol < ctx.MbCols; mbCol++)
        {
            TfSetColLimits(cpi, x, mbCol << 4, TF_BLOCK >> 2);
            Array.Clear(accum);
            Array.Clear(count);
            var refMv = default(AomMv);
            bool allowMeForSubBlks = true;
            if (cpi.Sf.hl_sf.allow_sub_blk_me_in_tf != 0)
            {
                // get_log_var_4x4sub_blk
                int varMin = int.MaxValue, varMax = 0;
                int stride = frameToFilter.Strides[0];
                int yOff = frameToFilter.Offsets[0] + mbRow * TF_BLOCK * stride + mbCol * TF_BLOCK;
                bool hbd = frameToFilter.Hbd;
                var zeros16 = hbd ? new ushort[16] : null;
                for (int i = 0; i < TF_BLOCK; i += 4)
                    for (int j = 0; j < TF_BLOCK; j += 4)
                    {
                        int v = (int)TfVariance(frameToFilter, hbd ? null : frameToFilter.Buffers[0], hbd ? frameToFilter.Buffers16[0] : null, yOff + i * stride + j,
                            stride, null, zeros16, 0, 0, 4, 4, _cfg.BitDepth, out _);
                        varMin = Math.Min(varMin, v);
                        varMax = Math.Max(varMax, v);
                    }
                double vmin = Math.Log(1 + varMin / 16.0), vmax = Math.Log(1 + varMax / 16.0);
                if (vmax - vmin <= 4.0) allowMeForSubBlks = false;
            }
            for (int frame = 0; frame < numFrames; frame++)
            {
                var subblockMvs = new AomMv[NUM_16X16];
                var subblockMses = new int[NUM_16X16];
                Array.Fill(subblockMses, int.MaxValue);
                bool isDcDiffLarge = false, isLowCntras = false;
                if (frame == filterIdx) refMv = new AomMv(-refMv.Row, -refMv.Col);
                else
                    TfMotionSearch(cpi, x, frameToFilter, frames[frame], mbRow, mbCol, ref refMv, allowMeForSubBlks, subblockMvs, subblockMses,
                        out isDcDiffLarge, out isLowCntras, ctx.QFactor);
                if (KfFilteringEnabled == 1 && frameType == KEY_FRAME && isDcDiffLarge) filterStrength = Math.Min(filterStrength, 1);
                if (cpi.Sharpness == 3 && isLowCntras) filterStrength = Math.Min(filterStrength, 3);
                if (frame == filterIdx)
                {
                    // tf_apply_temporal_filter_self
                    int po = 0;
                    for (int plane = 0; plane < numPlanes; ++plane)
                    {
                        int ssy = plane == 0 ? 0 : _cfg.SsY, ssx = plane == 0 ? 0 : _cfg.SsX;
                        int h = TF_BLOCK >> ssy, w = TF_BLOCK >> ssx;
                        var f = frames[frame];
                        int fs = f.Strides[plane];
                        int fo = f.Offsets[plane] + mbRow * h * fs + mbCol * w;
                        for (int i = 0; i < h; ++i)
                            for (int j = 0; j < w; ++j)
                            {
                                int v = f.Hbd ? f.Buffers16[plane][fo + i * fs + j] : f.Buffers[plane][fo + i * fs + j];
                                accum[po + i * w + j] += (uint)(TF_WEIGHT_SCALE * v);
                                count[po + i * w + j] += TF_WEIGHT_SCALE;
                            }
                        po += h * w;
                    }
                }
                else
                {
                    TfBuildPredictor(frames[frame], mbRow, mbCol, numPlanes, subblockMvs, pred8, pred16);
                    TfApplyTemporalFilter(frameToFilter, mbRow, mbCol, numPlanes, ctx.NoiseLevels, subblockMvs, subblockMses, ctx.QFactor, filterStrength, wgtLvl,
                        pred8, pred16, accum, count);
                }
            }
            // tf_normalize_filtered_frame
            var outF = ctx.Output;
            int planeOffset = 0;
            for (int plane = 0; plane < numPlanes; ++plane)
            {
                int ssy = plane == 0 ? 0 : _cfg.SsY, ssx = plane == 0 ? 0 : _cfg.SsX;
                int ph = TF_BLOCK >> ssy, pw = TF_BLOCK >> ssx;
                int fs = outF.Strides[plane];
                int fo = outF.Offsets[plane] + mbRow * ph * fs + mbCol * pw;
                for (int i = 0; i < ph; ++i)
                    for (int j = 0; j < pw; ++j)
                    {
                        int idx = planeOffset + i * pw + j;
                        uint rounding = (uint)(count[idx] >> 1);
                        uint v = (accum[idx] + rounding) / count[idx];
                        if (outF.Hbd) outF.Buffers16[plane][fo + i * fs + j] = (ushort)v;
                        else outF.Buffers[plane][fo + i * fs + j] = (byte)v;
                    }
                planeOffset += ph * pw;
            }
            if (ctx.ComputeDiff)
            {
                int ss = frameToFilter.Strides[0], os = outF.Strides[0];
                int so = frameToFilter.Offsets[0] + mbRow * TF_BLOCK * ss + mbCol * TF_BLOCK;
                int oo = outF.Offsets[0] + mbRow * TF_BLOCK * os + mbCol * TF_BLOCK;
                TfVariance(frameToFilter, frameToFilter.Hbd ? null : frameToFilter.Buffers[0], frameToFilter.Hbd ? frameToFilter.Buffers16[0] : null, so, ss,
                    outF.Hbd ? null : outF.Buffers[0], outF.Hbd ? outF.Buffers16[0] : null, oo, os, TF_BLOCK, TF_BLOCK, _cfg.BitDepth, out uint sse);
                ctx.DiffSum += sse;
                ctx.DiffSse += (long)sse * sse;
            }
        }
    }
}
