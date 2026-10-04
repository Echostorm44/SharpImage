using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/tpl_model.c: av1_setup_tpl_buffers, av1_init_tpl_stats, av1_tpl_preload_rc_estimate,
// av1_tpl_setup_stats (init_gop_frames_for_tpl, init_mc_flow_dispenser, mc_flow_dispenser / mode_estimation with
// motion_estimation, get_inter_cost, get_rate_distortion, txfm_quant_rdcost, get_quantize_error, rate_estimator,
// tpl_get_satd_cost; mc_flow_synthesizer with tpl_model_update_b and av1_delta_rate_cost; get_frame_importance,
// eval_gop_length). The search runs on the compressor state as libaom leaves it at this point: the previous frame's
// speed features and mi grid (the TPL writes into it like libaom's mode_estimation does).
internal sealed partial class AomGqEncoder
{
    private readonly AomTplData _tpl = new();
    private bool _tplBuffersSetup;
    private Av1CdfContext? _lastFc;
    private AomFrameBuffer? _tplPrevGopArfSrc;

    /// <summary>av1_encode: the last frame of the GOP keeps a copy of its source for the next GOP's TPL
    /// (tpl_data.prev_gop_arf_src; the look-ahead compressor does it too, on the shared gf group).</summary>
    private void UpdatePrevGopArfSrc(AomFrameBuffer source, int displayOrderHint)
    {
        var gf = _gfGroup;
        if (!_enableTplModel || gf.UpdateType[_gfFrameIndexForPrevGop] == OVERLAY_UPDATE || gf.UpdateType[_gfFrameIndexForPrevGop] == INTNL_OVERLAY_UPDATE) return;
        for (int i = 0; i < gf.Size; ++i) if (gf.DisplayIdx[i] > displayOrderHint) return;
        _tplPrevGopArfDispOrder = -1;
        var copy = new AomFrameBuffer(_cfg.Width, _cfg.Height, _cfg.SsX, _cfg.SsY, _cfg.Monochrome, _cfg.BitDepth);
        for (int p = 0; p < copy.NumPlanes; p++)
        {
            int uv = p > 0 ? 1 : 0;
            int w = copy.CropWidths[uv], h = copy.CropHeights[uv];
            for (int r = 0; r < h; r++)
                if (copy.Hbd) Array.Copy(source.Buffers16[p], source.Offsets[p] + r * source.Strides[p], copy.Buffers16[p], copy.Offsets[p] + r * copy.Strides[p], w);
                else Array.Copy(source.Buffers[p], source.Offsets[p] + r * source.Strides[p], copy.Buffers[p], copy.Offsets[p] + r * copy.Strides[p], w);
        }
        AomResize.ExtendFrameBorders(copy);
        _tplPrevGopArfSrc = copy;
        _tplPrevGopArfDispOrder = displayOrderHint;
    }

    private int _gfFrameIndexForPrevGop => _lapGfIndexOverride ?? _gfFrameIndex;
    private int? _lapGfIndexOverride;

    /// <summary>cpi->rd.r0 (set by process_tpl_stats_frame; kept across frames).</summary>
    private double _r0;

    /// <summary>process_tpl_stats_frame (encoder_utils.c).</summary>
    private void ProcessTplStatsFrame()
    {
        var gf = _gfGroup;
        var t = _tpl.Frame(_gfFrameIndex);
        if (!t.IsValid) return;
        double intraCostBase = 0, mcDepCostBase = 0, cbcmpBase = 1;
        int miRows = ((_cmHeight + 7) & ~7) >> 2, miCols = ((_cmWidth + 7) & ~7) >> 2;
        for (int row = 0; row < miRows; row += 4)
            for (int col = 0; col < miCols; col += 4)
            {
                var s = t.Stats![AomTplData.PtrPos(row, col, t.Stride)];
                double cbcmp = s.SrcrfDist;
                long mcDepDelta = AomRd.RdCost(t.BaseRdmult, s.McDepRate, s.McDepDist);
                double distScaled = s.RecrfDist << 7;
                intraCostBase += Math.Log(distScaled) * cbcmp;
                mcDepCostBase += Math.Log(distScaled + mcDepDelta) * cbcmp;
                cbcmpBase += cbcmp;
            }
        if (mcDepCostBase == 0)
        {
            t.IsValid = false;
            return;
        }
        _r0 = Math.Exp((intraCostBase - mcDepCostBase) / cbcmpBase);
        if (!IsFrameTplEligible(gf, _gfFrameIndex)) return;
        if (_lapEnabled)
        {
            double minBoostFactor = Math.Sqrt(_pRc.BaselineGfInterval);
            double factor = GfuBoostProjectionFactor(minBoostFactor, MAX_GFUBOOST_FACTOR, _pRc.NumStatsRequiredForGfuBoost);
            int gfuBoost = (int)Math.Round(factor / _r0, MidpointRounding.ToEven);   // rint
            _pRc.GfuBoost = CombinePriorWithTplBoost(minBoostFactor, 12.0, _pRc.GfuBoost, gfuBoost, _pRc.NumStatsUsedForGfuBoost);
        }
        else
        {
            int gfuBoost = (int)(200.0 * _tpl.R0AdjustFactor / _r0);
            if (_cfg.Sharpness == 3 && gf.UpdateType[_gfFrameIndex] != KF_UPDATE)
                throw new NotImplementedException("process_tpl_stats_frame with sharpness 3 (av1_gop_bit_allocation)");
            _pRc.GfuBoost = CombinePriorWithTplBoost(4.0, 12.0, _pRc.GfuBoost, gfuBoost, _rc.FramesToKey);
        }
    }

    /// <summary>combine_prior_with_tpl_boost.</summary>
    private static int CombinePriorWithTplBoost(double minFactor, double maxFactor, int priorBoost, int tplBoost, int framesToKey)
    {
        double factor = Math.Sqrt((double)framesToKey);
        double range = maxFactor - minFactor;
        factor = Math.Min(factor, maxFactor);
        factor = Math.Max(factor, minFactor);
        factor -= minFactor;
        return (int)((factor * priorBoost + (range - factor) * tplBoost) / range);
    }

    /// <summary>av1_tpl_get_q_index (av1_tpl_get_qstep_ratio + av1_get_q_index_from_qstep_ratio).</summary>
    private int TplGetQIndex(int gfFrameIndex, int leafQindex, int bd)
    {
        double qstepRatio = _tpl.StatsReady(gfFrameIndex) ? Math.Sqrt(1 / GetFrameImportance(gfFrameIndex)) : 1;
        int tq = QIndexFromQstepRatio(leafQindex, qstepRatio, bd);
        AomTrace.Out?.Write(FormattableString.Invariant($"tplq {gfFrameIndex} awq {leafQindex} ratio {qstepRatio:F9} q {tq}") + (char)10);
        return tq;
    }

    /// <summary>av1_get_q_index_from_qstep_ratio.</summary>
    private static int QIndexFromQstepRatio(int leafQindex, double qstepRatio, int bd)
    {
        double leafQstep = AomComp.DcQuantQtx(leafQindex, 0, bd);
        double targetQstep = leafQstep * qstepRatio;
        int qindex;
        if (qstepRatio < 1.0)
        {
            for (qindex = leafQindex; qindex > 0; --qindex)
                if (AomComp.DcQuantQtx(qindex, 0, bd) <= targetQstep) break;
        }
        else
        {
            for (qindex = leafQindex; qindex < 255; ++qindex)
                if (AomComp.DcQuantQtx(qindex, 0, bd) >= targetQstep) break;
        }
        return qindex;
    }

    /// <summary>av1_setup_tpl_buffers.</summary>
    private void TplSetupBuffers()
    {
        _tplBuffersSetup = true;
        int miCols = ((_cfg.Width + 7) & ~7) >> 2, miRows = ((_cfg.Height + 7) & ~7) >> 2;
        int alCols = (miCols + 31) & ~31, alRows = (miRows + 31) & ~31;
        foreach (var f in _tpl.Buffer)
        {
            f.IsValid = false;
            f.Width = alCols >> AomTplData.BlockMisLog2;
            f.Height = alRows >> AomTplData.BlockMisLog2;
            f.Stride = f.Width;
            f.MiRows = miRows;
            f.MiCols = miCols;
        }
        if (_lagInFrames <= 1) return;
        _tpl.StatsPool = new AomTplDepStats[]?[_lagInFrames];
        _tpl.RecPool = new AomFrameBuffer?[_lagInFrames];
        for (int frame = 0; frame < _lagInFrames; frame++)
        {
            var b = _tpl.Buffer[frame];
            var arr = new AomTplDepStats[b.Width * b.Height];
            for (int i = 0; i < arr.Length; i++) arr[i] = new AomTplDepStats();
            _tpl.StatsPool[frame] = arr;
            _tpl.RecPool[frame] = new AomFrameBuffer(_cfg.Width, _cfg.Height, _cfg.SsX, _cfg.SsY, _cfg.Monochrome, _cfg.BitDepth);
        }
        _tplPrevGopArfDispOrder = -1;
    }

    /// <summary>av1_init_tpl_stats.</summary>
    private void TplInitStats()
    {
        _tpl.Ready = false;
        foreach (var f in _tpl.Buffer) f.IsValid = false;
        for (int frameIdx = 0; frameIdx < AomTplData.MAX_LAG_BUFFERS && frameIdx < _tpl.StatsPool.Length; ++frameIdx)
        {
            var pool = _tpl.StatsPool[frameIdx];
            if (pool == null) continue;
            var b = _tpl.Buffer[frameIdx];
            for (int i = 0; i < b.Height * b.Width; i++) pool[i].Clear();
        }
    }

    /// <summary>av1_tpl_preload_rc_estimate.</summary>
    private void TplPreloadRcEstimate(int frameType)
    {
        var gf = _gfGroup;
        _cmFrameType = frameType;   // cm->current_frame.frame_type (left at the last group entry's)
        for (int g = _gfFrameIndex; g < gf.Size; ++g)
        {
            _cmFrameType = gf.FrameType[g];
            gf.QVal[g] = RcPickQAndBounds(_cmWidth, _cmHeight, g, gf.FrameType[g] == KEY_FRAME || gf.FrameType[g] == INTRA_ONLY_FRAME, out _, out _);
        }
    }

    private static bool IsFrameTplEligible(AomGfGroup gf, int index)
        => gf.UpdateType[index] == ARF_UPDATE || gf.UpdateType[index] == GF_UPDATE || gf.UpdateType[index] == KF_UPDATE;

    private int TplGopLength() => Math.Min(_gfGroup.Size, AomTplData.MAX_TPL_FRAME_IDX - 1);

    /// <summary>init_gop_frames_for_tpl.</summary>
    private int InitGopFramesForTpl(FrameParams initFp, out int tplGroupFrames, out int pframeQindex)
    {
        var gf = _gfGroup;
        pframeQindex = 0;
        Span<int> dispOrder = stackalloc int[REF_FRAMES];
        Span<int> pyrLevel = stackalloc int[REF_FRAMES];
        InitRefMapPair(dispOrder, pyrLevel);
        var remapped = new int[REF_FRAMES];
        var refPictureMap = new int[REF_FRAMES];
        for (int i = 0; i < REF_FRAMES; ++i)
        {
            var rf = _tpl.Frame(-i - 1);
            if (initFp.FrameType == KEY_FRAME)
            {
                rf.GfPicture = null;
                rf.RecPicture = null;
                rf.FrameDisplayIndex = 0;
            }
            else
            {
                var b = _refFrameMap[i]!;
                rf.GfPicture = b.DisplayOrderHint == _tplPrevGopArfDispOrder ? _tplPrevGopArfSrc : b.Buf;
                rf.RecPicture = b.Buf;
                rf.FrameDisplayIndex = (uint)b.DisplayOrderHint;
            }
            refPictureMap[i] = -i - 1;
        }
        tplGroupFrames = 0;
        int processFrameCount = 0;
        int gopLength = TplGopLength();
        int g;
        for (g = 0; g < gopLength; ++g)
        {
            var tf = _tpl.Frame(g);
            int ut = gf.UpdateType[g];
            int lookaheadIndex = gf.CurFrameIdx[g] + gf.ArfSrcOffset[g];
            var fp = new FrameParams
            {
                ShowFrame = ut != ARF_UPDATE && ut != INTNL_ARF_UPDATE, ShowExistingFrame = ut == INTNL_OVERLAY_UPDATE || ut == OVERLAY_UPDATE,
                FrameType = gf.FrameType[g],
            };
            if (ut == LF_UPDATE) pframeQindex = gf.QVal[g];
            var buf = LookaheadPeek(lookaheadIndex, EncodeStage);
            if (buf == null) break;
            tf.GfPicture = buf.Img;
            var tfBuf = TfInfoGetFilteredBuf(g, out _, out _);
            if (tfBuf != null) tf.GfPicture = tfBuf;
            tf.FrameDisplayIndex = (uint)(lookaheadIndex + _frameNumber);
            if (ut != OVERLAY_UPDATE && ut != INTNL_OVERLAY_UPDATE)
            {
                tf.RecPicture = _tpl.RecPool[processFrameCount];
                tf.Stats = _tpl.StatsPool[processFrameCount];
                ++processFrameCount;
            }
            int trueDisp = (int)tf.FrameDisplayIndex;
            GetRefFrames(dispOrder, pyrLevel, trueDisp, remapped);
            int refreshMask = GetRefreshFrameFlags(fp, ut, g, trueDisp, dispOrder, pyrLevel);
            if (gf.IsFrameNonRef[g]) refreshMask = 0;
            int refreshIdx = RefreshRefFrameMap(refreshMask);
            if (refreshIdx >= 0 && refreshIdx < REF_FRAMES)
            {
                dispOrder[refreshIdx] = Math.Max(0, trueDisp);
                pyrLevel[refreshIdx] = GetTruePyrLevel(gf.LayerDepth[g], trueDisp, gf.MaxLayerDepth);
            }
            for (int i = LAST_FRAME; i <= ALTREF_FRAME; ++i) tf.RefMapIndex[i - LAST_FRAME] = refPictureMap[remapped[i - LAST_FRAME]];
            if (refreshMask != 0) refPictureMap[refreshIdx] = g;
            ++tplGroupFrames;
        }
        int tplExtend = _lagInFrames - MAX_GF_INTERVAL;
        int extendFrameCount = 0;
        int extendFrameLength = Math.Min(tplExtend, _rc.FramesToKey - _pRc.BaselineGfInterval);
        int frameDisplayIndex = gf.CurFrameIdx[gopLength - 1] + gf.ArfSrcOffset[gopLength - 1] + 1;
        for (; g < AomTplData.MAX_TPL_FRAME_IDX && extendFrameCount < extendFrameLength; ++g)
        {
            var tf = _tpl.Frame(g);
            var fp = new FrameParams { ShowFrame = true, ShowExistingFrame = false, FrameType = INTER_FRAME };
            var buf = LookaheadPeek(frameDisplayIndex, EncodeStage);
            if (buf == null) break;
            tf.GfPicture = buf.Img;
            tf.RecPicture = _tpl.RecPool[processFrameCount];
            tf.Stats = _tpl.StatsPool[processFrameCount];
            tf.FrameDisplayIndex = (uint)(frameDisplayIndex + _frameNumber);
            ++processFrameCount;
            gf.UpdateType[g] = LF_UPDATE;
            gf.QVal[g] = pframeQindex;
            int trueDisp = (int)tf.FrameDisplayIndex;
            GetRefFrames(dispOrder, pyrLevel, trueDisp, remapped);
            int refreshMask = GetRefreshFrameFlags(fp, LF_UPDATE, g, trueDisp, dispOrder, pyrLevel);
            int refreshIdx = RefreshRefFrameMap(refreshMask);
            if (refreshIdx >= 0 && refreshIdx < REF_FRAMES)
            {
                dispOrder[refreshIdx] = Math.Max(0, trueDisp);
                pyrLevel[refreshIdx] = GetTruePyrLevel(gf.LayerDepth[g], trueDisp, gf.MaxLayerDepth);
            }
            for (int i = LAST_FRAME; i <= ALTREF_FRAME; ++i) tf.RefMapIndex[i - LAST_FRAME] = refPictureMap[remapped[i - LAST_FRAME]];
            tf.RefMapIndex[ALTREF_FRAME - LAST_FRAME] = -1;
            tf.RefMapIndex[LAST3_FRAME - LAST_FRAME] = -1;
            tf.RefMapIndex[BWDREF_FRAME - LAST_FRAME] = -1;
            tf.RefMapIndex[ALTREF2_FRAME - LAST_FRAME] = -1;
            if (refreshMask != 0) refPictureMap[refreshIdx] = g;
            ++tplGroupFrames;
            ++extendFrameCount;
            ++frameDisplayIndex;
        }
        return extendFrameCount;
    }

    /// <summary>av1_get_refresh_ref_frame_map.</summary>
    private static int RefreshRefFrameMap(int flags)
    {
        for (int i = 0; i < REF_FRAMES; ++i) if (((flags >> i) & 1) != 0) return i;
        return -1;
    }

    private static bool SkipTplForFrame(AomGfGroup gf, int frameIdx, int gopEval, bool approxGopEval, bool reduceNumFrames, int gopLength)
    {
        int numArfLayers = gopEval == 2 ? 3 : 2;
        if (gf.UpdateType[frameIdx] == INTNL_OVERLAY_UPDATE || gf.UpdateType[frameIdx] == OVERLAY_UPDATE) return true;
        if (approxGopEval && (gf.LayerDepth[frameIdx] > numArfLayers || frameIdx >= gopLength)) return true;
        if (reduceNumFrames && gf.UpdateType[frameIdx] == LF_UPDATE && frameIdx < gopLength) return true;
        return false;
    }

    /// <summary>The TPL's compressor context: the motion search state (speed features, mv costs from the frame
    /// context libaom has at this point, the quantizer of the TPL q).</summary>
    private sealed class TplCtx
    {
        public AomComp Cpi = null!;
        public AomMacroblock X = null!;
        public AomCommon Cm = null!;   // the mi grid the TPL writes into
        public int[] TileMiColEnd = null!;
        public AomTileInfo Tile = null!;
        public byte[] Pred8 = new byte[16 * 16];
        public ushort[] Pred16 = new ushort[16 * 16];
    }

    /// <summary>av1_tpl_setup_stats: returns eval_gop_length (0 without gop evaluation).</summary>
    private int TplSetupStats(int gopEval, FrameParams fp)
    {
        var gf = _gfGroup;
        bool approxGopEval = gopEval > 1;
        int tplGfGroupFrames;
        int pframeQindex;
        int extendedFrameCount = InitGopFramesForTpl(fp, out tplGfGroupFrames, out pframeQindex);
        _pRc.BaseLayerQp = pframeQindex;
        TplInitStats();

        var ctx = new TplCtx();
        ctx.Cpi = TfCompressor();
        var cpi = ctx.Cpi;
        var lastCm = _lastCpi != null && _lastCpi.Cm.Width == _cfg.Width && _lastCpi.Cm.Height == _cfg.Height ? _lastCpi.Cm : null;
        ctx.Cm = lastCm ?? new AomCommon(_cfg.Width, _cfg.Height, _cfg.SsX, _cfg.SsY, _cfg.Monochrome, SelectSbSize(_cfg), _cfg.BitDepth);
        ctx.Tile = ctx.Cm.TileInit(0, 0);
        var x = new AomMacroblock();
        ctx.X = x;
        var xd = x.E;
        xd.Bd = _cfg.BitDepth;
        for (int p = 0; p < 3; p++)
        {
            xd.Plane[p].SubsamplingX = p == 0 ? 0 : _cfg.SsX;
            xd.Plane[p].SubsamplingY = p == 0 ? 0 : _cfg.SsY;
            xd.Plane[p].PlaneType = p == 0 ? 0 : 1;
        }
        xd.SetTile(ctx.Tile);
        xd.BlockRefScaleFactors[0] = AomInterPred.Identity;
        xd.BlockRefScaleFactors[1] = AomInterPred.Identity;
        // av1_fill_mv_costs(&cm->fc->nmvc, ...): key frames start from the default mv probabilities
        var fc = new Av1CdfContext();
        if (fp.FrameType != KEY_FRAME && _lastFc != null) fc.CopyFrom(_lastFc);
        else Av1CdfDefaults.InitializeDefault(fc, 0);
        x.MvCosts.Fill(fc.Mv, false, true);

        bool savedHp = cpi.Cm.AllowHighPrecisionMv;
        int savedUpdateType = cpi.UpdateType;
        var savedQuants = cpi.Quants;
        cpi.Cm.AllowHighPrecisionMv = true;
        cpi.UpdateType = gf.UpdateType[_gfFrameIndex];
        var tplSf = cpi.Sf.tpl_sf;
        int numPlanes = tplSf.use_y_only_rate_distortion != 0 ? 1 : (_cfg.Monochrome ? 1 : 3);
        bool reduceNumFrames = tplSf.reduce_num_frames != 0 && gf.UpdateType[_gfFrameIndex] != KF_UPDATE && gf.MaxLayerDepth > 2;
        _tpl.R0AdjustFactor = reduceNumFrames ? 1.6 : 1.0;
        int gopLength = TplGopLength();
        // frame type for the rdmult: the last of av1_tpl_setup_stats' configure loop
        int frameTypeForRdmult = gf.Size > _gfFrameIndex ? gf.FrameType[gf.Size - 1] : fp.FrameType;
        for (int frameIdx = _gfFrameIndex; frameIdx < tplGfGroupFrames; ++frameIdx)
        {
            if (SkipTplForFrame(gf, frameIdx, gopEval, approxGopEval, reduceNumFrames, gopLength)) continue;
            InitMcFlowDispenser(ctx, frameIdx, pframeQindex, frameTypeForRdmult);
            McFlowDispenser(ctx);
            AomResize.ExtendFrameBorders(_tpl.Frame(frameIdx).RecPicture!);
        }
        for (int frameIdx = tplGfGroupFrames - 1; frameIdx >= _gfFrameIndex; --frameIdx)
        {
            if (SkipTplForFrame(gf, frameIdx, gopEval, approxGopEval, reduceNumFrames, gopLength)) continue;
            McFlowSynthesizer(frameIdx, ctx.Cm.MiRows, ctx.Cm.MiCols);
        }
        cpi.Cm.AllowHighPrecisionMv = savedHp;
        cpi.UpdateType = savedUpdateType;
        cpi.Quants = savedQuants;
        if (AomTrace.Out != null)
            for (int f = _gfFrameIndex; f < tplGfGroupFrames; f++)
            {
                var t = _tpl.Frame(f);
                if (!t.IsValid || t.Stats == null) continue;
                ulong h1 = 0, h2 = 0, h3 = 0;
                for (int r = 0; r < t.MiRows; r += 4)
                    for (int c = 0; c < t.MiCols; c += 4)
                    {
                        var s = t.Stats[AomTplData.PtrPos(r, c, t.Stride)];
                        h1 = h1 * 31 + (ulong)(s.IntraCost + 7L * s.InterCost);
                        h2 = h2 * 31 + (ulong)(s.RecrfDist + s.SrcrfDist + s.RecrfRate + s.SrcrfRate);
                        h3 = h3 * 31 + (ulong)(s.McDepDist + 3 * s.McDepRate);
                    }
                AomTrace.Out.Write($"tpl {f} {t.BaseRdmult} {h1:x} {h2:x} {h3:x}" + (char)10);
            }
        _cmFrameType = fp.FrameType;   // cm->current_frame.frame_type = frame_params->frame_type
        if (!approxGopEval) _tpl.Ready = true;
        if (gf.MaxLayerDepthAllowed == 0) return 1;
        if (gopEval == 0) return 0;
        var beta = new double[2];
        int fi0 = gf.ArfIndex, fi1 = Math.Min(tplGfGroupFrames - 1, gf.ArfIndex + 1);
        beta[0] = GetFrameImportance(fi0);
        beta[1] = GetFrameImportance(fi1);
        return gopEval switch
        {
            1 => beta[0] >= beta[1] + 0.7 && beta[0] > 3.0 ? 1 : 0,
            2 => beta[0] >= beta[1] + 0.4 && beta[0] > 1.6 ? 1 : (beta[0] < beta[1] + 0.1 || beta[0] <= 1.4) ? 0 : 2,
            3 => beta[0] > 1.1 ? 1 : 0,
            _ => 2,
        };
    }

    /// <summary>get_frame_importance.</summary>
    private double GetFrameImportance(int gfFrameIndex)
    {
        var t = _tpl.Frame(gfFrameIndex);
        double intraCostBase = 0, mcDepCostBase = 0, cbcmpBase = 1;
        int step = 1 << AomTplData.BlockMisLog2;
        for (int row = 0; row < t.MiRows; row += step)
            for (int col = 0; col < t.MiCols; col += step)
            {
                var s = t.Stats![AomTplData.PtrPos(row, col, t.Stride)];
                double cbcmp = s.SrcrfDist;
                long mcDepDelta = AomRd.RdCost(t.BaseRdmult, s.McDepRate, s.McDepDist);
                double distScaled = s.RecrfDist << 7;   // RDDIV_BITS
                distScaled = Math.Max(distScaled, 1);
                intraCostBase += Math.Log(distScaled) * cbcmp;
                mcDepCostBase += Math.Log(distScaled + mcDepDelta) * cbcmp;
                cbcmpBase += cbcmp;
            }
        return Math.Exp((mcDepCostBase - intraCostBase) / cbcmpBase);
    }

    /// <summary>init_mc_flow_dispenser.</summary>
    private void InitMcFlowDispenser(TplCtx ctx, int frameIdx, int pframeQindex, int frameType)
    {
        var gf = _gfGroup;
        var cpi = ctx.Cpi;
        var x = ctx.X;
        var tf = _tpl.Frame(frameIdx);
        var tplSf = cpi.Sf.tpl_sf;
        bool refPruningEnabled = cpi.Sf.inter_sf.selective_ref_frame > 0 && tplSf.prune_ref_frames_in_tpl > 0 && !IsFrameTplEligible(gf, frameIdx);
        int gopLength = TplGopLength();
        _tpl.FrameIdx = frameIdx;
        for (int i = 0; i < INTER_REFS_PER_FRAME; i++) { _tpl.RefFrame[i] = null; _tpl.SrcRefFrame[i] = null; }
        int boostIndex = Math.Min(15, _pRc.GfuBoost / 100);
        int layerDepth = Math.Min(gf.LayerDepth[_gfFrameIndex], 6);
        var refDisp = new int[INTER_REFS_PER_FRAME];
        for (int idx = 0; idx < INTER_REFS_PER_FRAME; ++idx)
        {
            var rtf = _tpl.Frame(tf.RefMapIndex[idx]);
            _tpl.RefFrame[idx] = rtf.RecPicture;
            _tpl.SrcRefFrame[idx] = rtf.GfPicture;
            refDisp[idx] = (int)rtf.FrameDisplayIndex;
        }
        // get_ref_frame_flags (all external flags) + enforce_max_ref_frames
        int flags = _extRefFrameFlags;
        for (int i = 1; i < INTER_REFS_PER_FRAME; ++i)
        {
            var thisRef = _tpl.RefFrame[RefFramePriorityOrder[i] - 1];
            for (int j = 0; j < i; ++j)
                if (thisRef == _tpl.RefFrame[RefFramePriorityOrder[j] - 1] && (flags & (1 << (RefFramePriorityOrder[j] - 1))) != 0)
                {
                    flags &= ~(1 << (RefFramePriorityOrder[i] - 1));
                    break;
                }
        }
        {
            int totalValidRefs = 0;
            for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++) if ((flags & (1 << (r - 1))) != 0) totalValidRefs++;
            int numRefsToDisable = 0;
            int sel = cpi.Sf.inter_sf.selective_ref_frame;
            if (sel >= 3)
            {
                numRefsToDisable++;
                if (sel >= 6) numRefsToDisable += 2;
                else if (sel == 5 && (flags & (1 << (LAST2_FRAME - 1))) != 0)
                {
                    int last2Dist = refDisp[LAST2_FRAME - LAST_FRAME] - (int)tf.FrameDisplayIndex;
                    if (Math.Abs(last2Dist) > 2) numRefsToDisable++;
                }
            }
            int maxAllowedRefs = Math.Min(INTER_REFS_PER_FRAME - numRefsToDisable, 7);
            ReadOnlySpan<int> disableOrder = stackalloc int[] { LAST3_FRAME, LAST2_FRAME, ALTREF2_FRAME, BWDREF_FRAME };
            for (int i = 0; i < 4 && totalValidRefs > maxAllowedRefs; ++i)
            {
                int rf = disableOrder[i];
                if ((flags & (1 << (rf - 1))) == 0) continue;
                flags &= rf == BWDREF_FRAME ? ~(1 << (GOLDEN_FRAME - 1)) : ~(1 << (rf - 1));
                --totalValidRefs;
            }
        }
        for (int idx = 0; idx < INTER_REFS_PER_FRAME; ++idx)
            if ((flags & (1 << idx)) == 0) _tpl.RefFrame[idx] = null;
        if (refPruningEnabled && frameIdx < gopLength)
            for (int idx = 0; idx < INTER_REFS_PER_FRAME; ++idx)
                if (AomRdoptInter.PruneRefBySelectiveRefFrame(cpi, null, idx + 1, NONE_FRAME, refDisp)) _tpl.RefFrame[idx] = null;

        int baseQindex = pframeQindex;
        int rdmult = AomRd.ComputeRdMult(baseQindex, _cfg.BitDepth, gf.UpdateType[_gfFrameIndex], layerDepth, boostIndex, frameType, _cfg.UseFixedQpOffsets,
            true, cpi.Tuning, GOOD);
        if (rdmult < 1) rdmult = 1;
        x.Errorperbit = AomRd.ErrorPerBit(rdmult);
        x.SadPerBit = AomEncodeFrame.SadPerBit(baseQindex, _cfg.BitDepth);
        tf.IsValid = true;
        // av1_frame_init_quantizer at base_qindex (no delta q / segmentation)
        cpi.Quants = new AomQuants(_cfg.BitDepth, 0, 0, 0, 0, 0, cpi.Sharpness);
        AomQuantSetup.SetQIndex(cpi, x, baseQindex);
        _prevBaseQindex = baseQindex;   // cm->quant_params.base_qindex = base_qindex
        tf.BaseRdmult = AomRd.ComputeRdMultBasedOnQindex(_cfg.BitDepth, gf.UpdateType[_gfFrameIndex], baseQindex, cpi.Tuning, GOOD) / 6;
        if (tplSf.allow_compound_pred != 0)
        {
            var ext = x.MbmiExtInter;
            foreach (var st in ext.RefMvStack) Array.Clear(st);
            foreach (var w in ext.Weight) Array.Clear(w);
            Array.Clear(ext.RefMvCount); Array.Clear(ext.GlobalMvs); Array.Clear(ext.ModeContext);
        }
        int layerDepthTh = tplSf.use_sad_for_mode_decision == 1 ? 5 : 0;
        tf.UsePredSad = tplSf.use_sad_for_mode_decision != 0 && gf.UpdateType[_gfFrameIndex] != KF_UPDATE && gf.LayerDepth[frameIdx] >= layerDepthTh;
    }

    /// <summary>mc_flow_dispenser.</summary>
    private void McFlowDispenser(TplCtx ctx)
    {
        var cm = ctx.Cm;
        var x = ctx.X;
        const int bsize = BLOCK_16X16, txSize = TX_16X16, miH = 4, miW = 4;
        for (int miRow = 0; miRow < cm.MiRows; miRow += miH)
        {
            x.MvLimits.RowMin = Math.Max(-(miRow * 4 + AomTplData.BorderInPixels - 8), -((miRow + miH) * 4 + 8));
            x.MvLimits.RowMax = Math.Min((cm.MiRows - miRow - miH) * 4 + AomTplData.BorderInPixels - 8, (cm.MiRows - miRow) * 4 + 8);
            var tf = _tpl.Frame(_tpl.FrameIdx);
            var stats = new AomTplDepStats();
            for (int miCol = 0; miCol < cm.MiCols; miCol += miW)
            {
                x.MvLimits.ColMin = Math.Max(-(miCol * 4 + AomTplData.BorderInPixels - 8), -((miCol + miW) * 4 + 8));
                x.MvLimits.ColMax = Math.Min((cm.MiCols - miCol - miW) * 4 + AomTplData.BorderInPixels - 8, (cm.MiCols - miCol) * 4 + 8);
                ModeEstimation(ctx, miRow, miCol, bsize, txSize, stats);
                // tpl_model_store
                var dst = tf.Stats![AomTplData.PtrPos(miRow, miCol, tf.Stride)];
                dst.CopyFrom(stats);
                dst.IntraCost = Math.Max(1, dst.IntraCost);
                dst.InterCost = Math.Max(1, dst.InterCost);
                dst.SrcrfDist = Math.Max(1, dst.SrcrfDist);
                dst.SrcrfSse = Math.Max(1, dst.SrcrfSse);
                dst.RecrfDist = Math.Max(1, dst.RecrfDist);
                dst.SrcrfRate = Math.Max(1, dst.SrcrfRate);
                dst.RecrfRate = Math.Max(1, dst.RecrfRate);
                dst.CmpRecrfDist[0] = Math.Max(1, dst.CmpRecrfDist[0]);
                dst.CmpRecrfDist[1] = Math.Max(1, dst.CmpRecrfDist[1]);
                dst.CmpRecrfRate[0] = Math.Max(1, dst.CmpRecrfRate[0]);
                dst.CmpRecrfRate[1] = Math.Max(1, dst.CmpRecrfRate[1]);
            }
        }
    }

    private static int PlaneAlignedW(AomFrameBuffer f, int plane, AomGqConfig c)
    {
        int aw = (c.Width + 7) & ~7;
        return plane == 0 ? aw : (aw + c.SsX) >> c.SsX;
    }

    private static int PlaneAlignedH(AomFrameBuffer f, int plane, AomGqConfig c)
    {
        int ah = (c.Height + 7) & ~7;
        return plane == 0 ? ah : (ah + c.SsY) >> c.SsY;
    }

    /// <summary>The (fn_ptr) SAD of a block (high bit depth: normalised to 8 bits).</summary>
    private static uint TplSad(AomFrameBuffer? sf, byte[]? s8, ushort[]? s16, int sOff, int sStride, byte[]? r8, ushort[]? r16, int rOff, int rStride, int w, int h,
        int bd)
    {
        if (s16 != null) return AomHbd.Sad(s16, sOff, sStride, r16!, rOff, rStride, w, h) >> (bd - 8);
        return AomSad.Sad(s8!, sOff, sStride, r8!, rOff, rStride, w, h);
    }

    /// <summary>tpl_get_satd_cost: the residual's DCT and aom_satd.</summary>
    private static int TplGetSatdCost(AomMacroblock x, byte[]? s8, ushort[]? s16, int sOff, int sStride, byte[]? d8, ushort[]? d16, int dOff, int dStride,
        int bw, int bh, int txSize, int bsizeForStride)
    {
        var p = x.Plane[0];
        if (s16 != null) AomHbd.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, s16, sOff, sStride, d16!, dOff, dStride);
        else AomEncodeMb.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, s8!, sOff, sStride, d8!, dOff, dStride);
        AomEncodeMb.Xform(x, 0, 0, 0, 0, bsizeForStride, txSize, DCT_DCT);
        return AomEncodeMb.Satd(p.Coeff.AsSpan(0, bw * bh), bw * bh);
    }

    /// <summary>txfm_quant_rdcost (+ get_quantize_error, rate_estimator).</summary>
    private static void TxfmQuantRdcost(AomMacroblock x, byte[]? s8, ushort[]? s16, int sOff, int sStride, AomFrameBuffer dstF, int plane, int dOff, int dStride,
        int bw, int bh, int txSize, int bsizeForStride, bool doRecon, out int rateCost, out long reconError, out long sse)
    {
        var p = x.Plane[0];
        var xd = x.E;
        if (s16 != null) AomHbd.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, s16, sOff, sStride, dstF.Buffers16[plane], dOff, dStride);
        else AomEncodeMb.SubtractBlock(bh, bw, p.SrcDiff, 0, bw, s8!, sOff, sStride, dstF.Buffers[plane], dOff, dStride);
        AomEncodeMb.Xform(x, 0, 0, 0, 0, bsizeForStride, txSize, DCT_DCT);
        // get_quantize_error (plane 0's quantizer, FP, no matrices)
        int pixNum = TxSizeWide[txSize] * TxSizeHigh[txSize];
        int shift = txSize == TX_32X32 ? 0 : 2;
        var qp = AomEncodeMb.SetupQuant(txSize, false, AomXformQuant.Fp, 0);
        AomEncodeMb.Quant(x, 0, 0, txSize, DCT_DCT, qp);
        int eob = p.Eobs[0];
        long err = xd.Bd > 8 ? AomHbd.BlockError(p.Coeff.AsSpan(0, pixNum), p.Dqcoeff.AsSpan(0, pixNum), pixNum, out long ssz, xd.Bd)
            : AomEncodeMb.BlockErrorAvx2(p.Coeff.AsSpan(0, pixNum), p.Dqcoeff.AsSpan(0, pixNum), pixNum, out ssz);
        reconError = Math.Max(err >> shift, 1);
        sse = Math.Max(ssz >> shift, 1);
        // rate_estimator
        var scan = AomEncodeMb.ScanOf(txSize, DCT_DCT);
        int rc = 1;
        for (int idx = 0; idx < eob; ++idx)
        {
            uint absLevel = (uint)Math.Abs(p.Qcoeff[scan[idx]]);
            rc += (31 - System.Numerics.BitOperations.LeadingZeroCount(absLevel + 1)) + 1 + (absLevel > 0 ? 1 : 0);
        }
        rateCost = rc << 9;
        if (doRecon && eob > 0)
        {
            var d = new AomBuf2d { Buf = dstF.Buffers[plane], Buf16 = dstF.Buffers16[plane], Stride = dStride };
            AomEncodeMb.InverseTransformBlockDst(p.Dqcoeff, 0, DCT_DCT, txSize, d, dOff, dStride, eob, xd.Bd, xd.Lossless[0] != 0);
        }
    }

    /// <summary>get_rate_distortion.</summary>
    private void GetRateDistortion(TplCtx ctx, out int rateCost, out long reconError, out long predError, AomFrameBuffer?[] refFramePtr, AomFrameBuffer rec,
        int txSize, int bestMode, int miRow, int miCol, bool useYOnly, bool doRecon)
    {
        var x = ctx.X;
        var xd = x.E;
        var cpi = ctx.Cpi;
        var c = _cfg;
        rateCost = 0;
        reconError = 1;
        predError = 1;
        bool isCompound = bestMode == NEW_NEWMV;
        int numPlanes = useYOnly ? 1 : 3;
        var cur = _tplCurBuf!;
        for (int plane = 0; plane < numPlanes && plane < cur.NumPlanes; ++plane)
        {
            int ssx = plane == 0 ? 0 : c.SsX, ssy = plane == 0 ? 0 : c.SsY;
            int bsizePlane = AomCfl.GetPlaneBlockSize(TxsizeToBsize[txSize], ssx, ssy);
            int pbw = BlockSizeWide[bsizePlane], pbh = BlockSizeHigh[bsizePlane];
            int dstStride = rec.Strides[plane];
            int dstOff = rec.Offsets[plane] + ((miRow * 4 * dstStride) >> ssy) + ((miCol * 4) >> ssx);
            for (int r = 0; r < 1 + (isCompound ? 1 : 0); ++r)
            {
                if (!AomInter.IsInterMode(bestMode))
                {
                    if (rec.Hbd)
                        AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, pbw, pbh, MaxTxsizeRectLookup[bsizePlane], bestMode, 0, false,
                            FILTER_INTRA_MODES, rec.Buffers16[plane], dstOff, dstStride, rec.Buffers16[plane], dstOff, dstStride, 0, 0, plane);
                    else
                        AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, pbw, pbh, MaxTxsizeRectLookup[bsizePlane], bestMode, 0, false,
                            FILTER_INTRA_MODES, rec.Buffers[plane], dstOff, dstStride, rec.Buffers[plane], dstOff, dstStride, 0, 0, plane);
                }
                else
                {
                    var bestMv = r == 0 ? xd.Mi0.Mv0 : xd.Mi0.Mv1;
                    var rf = refFramePtr[r]!;
                    var refBuf = new AomBuf2d
                    {
                        Buf = rf.Buffers[plane], Buf16 = rf.Buffers16[plane], Offset0 = rf.Offsets[plane], Offset = rf.Offsets[plane],
                        Width = PlaneAlignedW(rf, plane, c), Height = PlaneAlignedH(rf, plane, c), Stride = rf.Strides[plane],
                    };
                    var ip = new AomInterPredParams();
                    AomInterPred.InitInterParams(ip, pbw, pbh, (miRow * 4) >> ssy, (miCol * 4) >> ssx, ssx, ssy, xd.Bd, xd.IsHbd, false, AomInterPred.Identity,
                        refBuf, 0);
                    if (isCompound) ip.CompMode = AomInterPredParams.UNIFORM_COMP;
                    ip.ConvParams = AomConvParams.NoRound(r, plane, xd.TmpConvDst, 128, isCompound, xd.Bd);
                    AomInterPred.BuildOneInterPredictor(rec.Hbd ? null : rec.Buffers[plane], rec.Hbd ? rec.Buffers16[plane] : null, dstOff, dstStride, bestMv, ip);
                }
            }
            int srcStride = cur.Strides[plane];
            int srcOff = cur.Offsets[plane] + ((miRow * 4 * srcStride) >> ssy) + ((miCol * 4) >> ssx);
            TxfmQuantRdcost(x, cur.Hbd ? null : cur.Buffers[plane], cur.Hbd ? cur.Buffers16[plane] : null, srcOff, srcStride, rec, plane, dstOff, dstStride, pbw, pbh,
                MaxTxsizeRectLookup[bsizePlane], bsizePlane, doRecon, out int thisRate, out long thisRecon, out long sse);
            reconError += thisRecon;
            predError += sse;
            rateCost += thisRate;
        }
    }

    private AomFrameBuffer? _tplCurBuf;

    private struct CenterMv { public AomMv Mv; public int Sad; }

    /// <summary>motion_estimation.</summary>
    private static uint TplMotionEstimation(TplCtx ctx, AomFrameBuffer cur, int curOff, AomFrameBuffer refF, int refOff, int bsize, AomMv centerMv, out AomMv bestMv)
    {
        var cpi = ctx.Cpi;
        var x = ctx.X;
        var xd = x.E;
        var tplSf = cpi.Sf.tpl_sf;
        var startMv = centerMv.ToFullMv();
        ref var src = ref x.Plane[0].Src;
        src.Buf = cur.Buffers[0]; src.Buf16 = cur.Buffers16[0]; src.Offset = curOff; src.Stride = cur.Strides[0];
        ref var pre = ref xd.Plane[0].Pre(0);
        pre.Buf = refF.Buffers[0]; pre.Buf16 = refF.Buffers16[0]; pre.Offset = refOff; pre.Offset0 = refF.Offsets[0]; pre.Stride = refF.Strides[0];
        int stepParam = Math.Min(tplSf.reduce_first_step_size, AomSearchSiteConfig.MaxMvSearchSteps - 2);
        var fp = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, centerMv, cpi.SearchSites, tplSf.search_method, false, x.MvLimits, startMv);
        var costList = new int[5];
        AomMv dummy = default;
        uint bestsme = (uint)AomMcomp.FullPixelSearch(startMv, fp, stepParam, AomSubpel.CondCostList(cpi, costList), out var bestFull, out var stats, ref dummy, false);
        if (tplSf.subpel_force_stop == AomSubpel.FULL_PEL)
        {
            bestMv = bestFull.ToMv();
            return bestsme;
        }
        var ms = AomSubpel.MakeDefaultSubpelMsParams(cpi, x, bsize, centerMv, costList);
        ms.ForcedStop = tplSf.subpel_force_stop;
        ms.SubpelSearchType = AomSubpel.USE_2_TAPS;
        ms.MvCostType = AomMcomp.MV_COST_NONE;
        stats.ErrCost = 0;
        bestsme = (uint)AomSubpel.FindFractionalMvStep(cpi, ms, bestFull.ToMv(), stats, out bestMv, out _, out _, null);
        return bestsme;
    }

    /// <summary>get_inter_cost.</summary>
    private int TplGetInterCost(TplCtx ctx, AomFrameBuffer cur, int srcOff, int bsize, int txSize, int miRow, int miCol, int rfIdx, AomMv mv, bool usePredSad)
    {
        var x = ctx.X;
        var xd = x.E;
        var refF = _tpl.SrcRefFrame[rfIdx]!;
        int bw = 16, bh = 16;
        int srcStride = cur.Strides[0];
        bool hbd = cur.Hbd;
        if (ctx.Cpi.Sf.tpl_sf.subpel_force_stop != AomSubpel.FULL_PEL)
        {
            var refBuf = new AomBuf2d
            {
                Buf = refF.Buffers[0], Buf16 = refF.Buffers16[0], Offset0 = refF.Offsets[0], Offset = refF.Offsets[0],
                Width = PlaneAlignedW(refF, 0, _cfg), Height = PlaneAlignedH(refF, 0, _cfg), Stride = refF.Strides[0],
            };
            var ip = new AomInterPredParams();
            AomInterPred.InitInterParams(ip, bw, bh, miRow * 4, miCol * 4, 0, 0, xd.Bd, xd.IsHbd, false, AomInterPred.Identity, refBuf, 0);
            ip.ConvParams = AomConvParams.Get(0, 0, xd.Bd);
            AomInterPred.BuildOneInterPredictor(hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw, mv, ip);
            if (usePredSad)
                return (int)TplSad(cur, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw, bw, bh, xd.Bd);
            return TplGetSatdCost(x, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw,
                bw, bh, txSize, bsize);
        }
        int refStride = refF.Strides[0];
        int refMb = refF.Offsets[0] + miRow * 4 * refStride + miCol * 4;
        var full = mv.ToFullMv();
        int roff = refMb + full.Row * refStride + full.Col;
        if (usePredSad)
            return (int)TplSad(cur, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : refF.Buffers[0], hbd ? refF.Buffers16[0] : null,
                roff, refStride, bw, bh, xd.Bd);
        return TplGetSatdCost(x, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : refF.Buffers[0], hbd ? refF.Buffers16[0] : null,
            roff, refStride, bw, bh, txSize, bsize);
    }

    private static bool IsAlikeMv(AomMv candidate, CenterMv[] centerMvs, int count, int skipAlikeStartingMv)
    {
        int thr = skipAlikeStartingMv switch { 0 => 1, 1 => 8 << 3, _ => 16 << 3 };
        for (int i = 0; i < count; i++)
            if (Math.Abs(centerMvs[i].Mv.Col - candidate.Col) < thr && Math.Abs(centerMvs[i].Mv.Row - candidate.Row) < thr) return true;
        return false;
    }

    private static readonly int[,] TplCompRefFrames = { { 0, 4 }, { 0, 6 }, { 3, 6 } };

    /// <summary>mode_estimation.</summary>
    private void ModeEstimation(TplCtx ctx, int miRow, int miCol, int bsize, int txSize, AomTplDepStats tplStats)
    {
        var cpi = ctx.Cpi;
        var x = ctx.X;
        var xd = x.E;
        var cm = ctx.Cm;
        var tplSf = cpi.Sf.tpl_sf;
        var tf = _tpl.Frame(_tpl.FrameIdx);
        int bw = 16, bh = 16;
        var cur = tf.GfPicture!;
        _tplCurBuf = cur;
        bool hbd = cur.Hbd;
        int srcStride = cur.Strides[0];
        int srcOff = cur.Offsets[0] + miRow * 4 * srcStride + miCol * 4;
        var rec = tf.RecPicture!;
        int dstStride = rec.Strides[0];
        int dstOff = rec.Offsets[0] + miRow * 4 * dstStride + miCol * 4;
        bool useYOnly = tplSf.use_y_only_rate_distortion != 0;
        long reconError = 1, predError = 1;
        tplStats.Clear();
        tplStats.RefFrameIndex[0] = -1;
        tplStats.RefFrameIndex[1] = -1;
        int miWidth = 4, miHeight = 4;
        // set_mode_info_offsets / set_mi_row_col / set_plane_n4
        int gridIdx = miRow * cm.MiStride + miCol;
        cm.MiGridBase[gridIdx] = cm.MiAlloc[gridIdx];
        xd.MiGrid = cm.MiGridBase;
        xd.MiStride = cm.MiStride;
        xd.MiOffset = gridIdx;
        xd.Mi0 = cm.MiAlloc[gridIdx];
        xd.TxTypeMap = cm.TxTypeMap;
        xd.TxTypeMapOffset = gridIdx;
        xd.TxTypeMapStride = cm.MiStride;
        AomEncodeFrame.SetMiRowCol(xd, cm, miRow, miHeight, miCol, miWidth);
        for (int i = 0; i < cm.NumPlanes; i++)
        {
            var pd = xd.Plane[i];
            pd.Width = Math.Max((miWidth * 4) >> pd.SubsamplingX, 4);
            pd.Height = Math.Max((miHeight * 4) >> pd.SubsamplingY, 4);
        }
        var mbmi = xd.Mi0;
        mbmi.Bsize = (byte)bsize;
        mbmi.MotionMode = SIMPLE_TRANSLATION;
        mbmi.RefFrame0 = INTRA_FRAME;
        if (xd.LeftAvailable && miRow + TxSizeHighUnit[txSize] < ctx.Tile.MiRowEnd)
        {
            if (hbd)
            {
                var d = rec.Buffers16[0];
                for (int i = 0; i < bw; ++i) d[dstOff + (bw + i) * dstStride - 1] = d[dstOff + (bw - 1) * dstStride - 1];
            }
            else
            {
                var d = rec.Buffers[0];
                for (int i = 0; i < bw; ++i) d[dstOff + (bw + i) * dstStride - 1] = d[dstOff + (bw - 1) * dstStride - 1];
            }
        }
        int lastIntraMode = tplSf.prune_intra_modes != 0 ? D45_PRED : INTRA_MODE_END;
        int bestIntraCost = int.MaxValue, bestMode = DC_PRED;
        for (int mode = INTRA_MODE_START; mode < lastIntraMode; ++mode)
        {
            TplPredictIntra(ctx, rec, dstOff, dstStride, bw, bh, txSize, mode);
            int intraCost = tf.UsePredSad
                ? (int)TplSad(cur, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw, bw, bh, xd.Bd)
                : TplGetSatdCost(x, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw, bw,
                    bh, txSize, bsize);
            if (intraCost < bestIntraCost)
            {
                bestIntraCost = intraCost;
                bestMode = mode;
            }
        }
        if (tf.UsePredSad)
        {
            TplPredictIntra(ctx, rec, dstOff, dstStride, bw, bh, txSize, bestMode);
            bestIntraCost = TplGetSatdCost(x, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null,
                0, bw, bw, bh, txSize, bsize);
        }
        int rateCost = 1;
        mbmi.RefFrame0 = INTRA_FRAME;
        mbmi.RefFrame1 = NONE_FRAME;
        mbmi.CompoundIdx = 1;
        int bestRfIdx = -1;
        var bestMv = new AomMv[2] { AomMv.Invalid, AomMv.Invalid };
        int bestInterCost = int.MaxValue;
        var singleMv = new AomMv[INTER_REFS_PER_FRAME];
        var centerMvs = new CenterMv[4];
        for (int rfIdx = 0; rfIdx < INTER_REFS_PER_FRAME; ++rfIdx)
        {
            singleMv[rfIdx] = AomMv.Invalid;
            if (_tpl.RefFrame[rfIdx] == null || _tpl.SrcRefFrame[rfIdx] == null)
            {
                tplStats.Mv[rfIdx] = AomMv.Invalid;
                continue;
            }
            var refF = _tpl.SrcRefFrame[rfIdx]!;
            int refStride = refF.Strides[0];
            int refMbOff = refF.Offsets[0] + miRow * 4 * refStride + miCol * 4;
            var bestRfidxMv = default(AomMv);
            uint bestsme = uint.MaxValue;
            for (int k = 0; k < 4; k++) centerMvs[k] = new CenterMv { Mv = default, Sad = int.MaxValue };
            int refmvCount = 1;
            if (xd.UpAvailable)
            {
                var rs = tf.Stats![AomTplData.PtrPos(miRow - miHeight, miCol, tf.Stride)];
                if (!IsAlikeMv(rs.Mv[rfIdx], centerMvs, refmvCount, tplSf.skip_alike_starting_mv)) centerMvs[refmvCount++].Mv = rs.Mv[rfIdx];
            }
            if (xd.LeftAvailable)
            {
                var rs = tf.Stats![AomTplData.PtrPos(miRow, miCol - miWidth, tf.Stride)];
                if (!IsAlikeMv(rs.Mv[rfIdx], centerMvs, refmvCount, tplSf.skip_alike_starting_mv)) centerMvs[refmvCount++].Mv = rs.Mv[rfIdx];
            }
            if (xd.UpAvailable && miCol + miWidth < ctx.Tile.MiColEnd)
            {
                var rs = tf.Stats![AomTplData.PtrPos(miRow - miHeight, miCol + miWidth, tf.Stride)];
                if (!IsAlikeMv(rs.Mv[rfIdx], centerMvs, refmvCount, tplSf.skip_alike_starting_mv)) centerMvs[refmvCount++].Mv = rs.Mv[rfIdx];
            }
            if (tplSf.prune_starting_mv != 0 && refmvCount > 1)
            {
                for (int idx = 0; idx < refmvCount; ++idx)
                {
                    var mv = centerMvs[idx].Mv.ToFullMv();
                    int r = Math.Clamp((int)mv.Row, x.MvLimits.RowMin, x.MvLimits.RowMax), cc = Math.Clamp((int)mv.Col, x.MvLimits.ColMin, x.MvLimits.ColMax);
                    centerMvs[idx].Sad = (int)TplSad(cur, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : refF.Buffers[0],
                        hbd ? refF.Buffers16[0] : null, refMbOff + r * refStride + cc, refStride, bw, bh, xd.Bd);
                }
                AomPalette.MsvcrtQsort(centerMvs.AsSpan(0, refmvCount), static (in CenterMv a, in CenterMv b) =>
                {
                    int diff = a.Sad - b.Sad;
                    return diff < 0 ? -1 : diff > 0 ? 1 : 0;
                });
                refmvCount = Math.Min(4 - tplSf.prune_starting_mv, refmvCount);
                if (refmvCount > 1)
                {
                    int lastSad = centerMvs[refmvCount - 1].Sad, secondToLast = centerMvs[refmvCount - 2].Sad;
                    if ((long)(lastSad - secondToLast) * 5 > secondToLast) refmvCount--;
                }
            }
            for (int idx = 0; idx < refmvCount; ++idx)
            {
                uint thissme = TplMotionEstimation(ctx, cur, srcOff, refF, refMbOff, bsize, centerMvs[idx].Mv, out var thisMv);
                if (thissme < bestsme)
                {
                    bestsme = thissme;
                    bestRfidxMv = thisMv;
                }
            }
            tplStats.Mv[rfIdx] = bestRfidxMv;
            singleMv[rfIdx] = bestRfidxMv;
            int interCost = TplGetInterCost(ctx, cur, srcOff, bsize, txSize, miRow, miCol, rfIdx, bestRfidxMv, tf.UsePredSad);
            tplStats.PredError[rfIdx] = Math.Max(1, interCost);
            if (interCost < bestInterCost)
            {
                bestRfIdx = rfIdx;
                bestInterCost = interCost;
                bestMv[0] = bestRfidxMv;
            }
        }
        if (bestInterCost < int.MaxValue && tf.UsePredSad)
            bestInterCost = TplGetInterCost(ctx, cur, srcOff, bsize, txSize, miRow, miCol, bestRfIdx, bestMv[0], false);
        if (bestRfIdx != -1 && bestInterCost < bestIntraCost)
        {
            bestMode = NEWMV;
            mbmi.RefFrame0 = bestRfIdx + LAST_FRAME;
            mbmi.Mv0 = bestMv[0];
        }
        int startRf = 0, endRf = tplSf.allow_compound_pred != 0 ? 3 : 0;
        xd.MiRow = miRow;
        xd.MiCol = miCol;
        int bestCmpRfIdx = -1;
        for (int cmpRfIdx = startRf; cmpRfIdx < endRf; ++cmpRfIdx)
        {
            int rf0 = TplCompRefFrames[cmpRfIdx, 0], rf1 = TplCompRefFrames[cmpRfIdx, 1];
            if (_tpl.RefFrame[rf0] == null || _tpl.SrcRefFrame[rf0] == null || _tpl.RefFrame[rf1] == null || _tpl.SrcRefFrame[rf1] == null) continue;
            var rfp = new[] { _tpl.SrcRefFrame[rf0]!, _tpl.SrcRefFrame[rf1]! };
            mbmi.RefFrame0 = rf0 + LAST_FRAME;
            mbmi.RefFrame1 = rf1 + LAST_FRAME;
            mbmi.Mode = NEW_NEWMV;
            int rft = AomInter.RefFrameType(mbmi.RefFrame0, mbmi.RefFrame1);
            x.MbmiExtInter.RefMvStack[rft][mbmi.RefMvIdx].ThisMv = singleMv[rf0];
            x.MbmiExtInter.RefMvStack[rft][mbmi.RefMvIdx].CompMv = singleMv[rf1];
            for (int i = 0; i < 2; ++i)
                for (int plane = 0; plane < 3 && plane < rfp[i].NumPlanes; ++plane)
                {
                    int ssx = plane == 0 ? 0 : _cfg.SsX, ssy = plane == 0 ? 0 : _cfg.SsY;
                    ref var pr = ref xd.Plane[plane].Pre(i);
                    pr.Buf = rfp[i].Buffers[plane]; pr.Buf16 = rfp[i].Buffers16[plane];
                    pr.Offset0 = rfp[i].Offsets[plane];
                    pr.Stride = rfp[i].Strides[plane];
                    pr.Offset = rfp[i].Offsets[plane] + ((miRow * 4) >> ssy) * pr.Stride + ((miCol * 4) >> ssx);
                    pr.Width = rfp[i].CropWidths[plane > 0 ? 1 : 0]; pr.Height = rfp[i].CropHeights[plane > 0 ? 1 : 0];
                }
            var tmpMv = new[] { singleMv[rf0], singleMv[rf1] };
            var srcB = x.Plane[0].Src;
            x.Plane[0].Src = new AomBuf2d { Buf = cur.Buffers[0], Buf16 = cur.Buffers16[0], Offset = srcOff, Offset0 = cur.Offsets[0], Stride = srcStride };
            AomRdoptInter.JointMotionSearchPublic(cpi, x, bsize, tmpMv, null, 0, out _, cpi.Sf.mv_sf.disable_second_mv == 0, 2);
            x.Plane[0].Src = srcB;
            for (int r = 0; r < 2; ++r)
            {
                var refBuf = new AomBuf2d
                {
                    Buf = rfp[r].Buffers[0], Buf16 = rfp[r].Buffers16[0], Offset0 = rfp[r].Offsets[0], Offset = rfp[r].Offsets[0],
                    Width = PlaneAlignedW(rfp[r], 0, _cfg), Height = PlaneAlignedH(rfp[r], 0, _cfg), Stride = rfp[r].Strides[0],
                };
                var ip = new AomInterPredParams();
                AomInterPred.InitInterParams(ip, bw, bh, miRow * 4, miCol * 4, 0, 0, xd.Bd, xd.IsHbd, false, AomInterPred.Identity, refBuf, 0);
                ip.CompMode = AomInterPredParams.UNIFORM_COMP;
                ip.ConvParams = AomConvParams.NoRound(r, 0, xd.TmpConvDst, 128, true, xd.Bd);
                AomInterPred.BuildOneInterPredictor(hbd ? null : ctx.Pred8, hbd ? ctx.Pred16 : null, 0, bw, tmpMv[r], ip);
            }
            int interCost = TplGetSatdCost(x, hbd ? null : cur.Buffers[0], hbd ? cur.Buffers16[0] : null, srcOff, srcStride, hbd ? null : ctx.Pred8,
                hbd ? ctx.Pred16 : null, 0, bw, bw, bh, txSize, bsize);
            if (interCost < bestInterCost)
            {
                bestCmpRfIdx = cmpRfIdx;
                bestInterCost = interCost;
                bestMv[0] = tmpMv[0];
                bestMv[1] = tmpMv[1];
            }
        }
        if (bestCmpRfIdx != -1 && bestInterCost < bestIntraCost)
        {
            bestMode = NEW_NEWMV;
            mbmi.RefFrame0 = TplCompRefFrames[bestCmpRfIdx, 0] + LAST_FRAME;
            mbmi.RefFrame1 = TplCompRefFrames[bestCmpRfIdx, 1] + LAST_FRAME;
        }
        var refPtr = new AomFrameBuffer?[2];
        if (bestInterCost < int.MaxValue && AomInter.IsInterMode(bestMode))
        {
            mbmi.Mv0 = bestMv[0];
            mbmi.Mv1 = bestMv[1];
            refPtr[0] = bestCmpRfIdx >= 0 ? _tpl.SrcRefFrame[TplCompRefFrames[bestCmpRfIdx, 0]] : _tpl.SrcRefFrame[bestRfIdx];
            refPtr[1] = bestCmpRfIdx >= 0 ? _tpl.SrcRefFrame[TplCompRefFrames[bestCmpRfIdx, 1]] : null;
            rateCost = 1;
            GetRateDistortion(ctx, out rateCost, out reconError, out predError, refPtr, rec, txSize, bestMode, miRow, miCol, useYOnly, false);
            tplStats.SrcrfRate = rateCost;
        }
        bestIntraCost = Math.Max(bestIntraCost, 1);
        bestInterCost = Math.Min(bestIntraCost, bestInterCost);
        tplStats.InterCost = bestInterCost;
        tplStats.IntraCost = bestIntraCost;
        tplStats.SrcrfDist = reconError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
        tplStats.SrcrfSse = predError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
        if (bestMode == NEW_NEWMV)
        {
            refPtr[0] = _tpl.RefFrame[TplCompRefFrames[bestCmpRfIdx, 0]];
            refPtr[1] = _tpl.SrcRefFrame[TplCompRefFrames[bestCmpRfIdx, 1]];
            GetRateDistortion(ctx, out rateCost, out reconError, out predError, refPtr, rec, txSize, bestMode, miRow, miCol, useYOnly, false);
            tplStats.CmpRecrfDist[0] = reconError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
            tplStats.CmpRecrfRate[0] = rateCost;
            refPtr[0] = _tpl.SrcRefFrame[TplCompRefFrames[bestCmpRfIdx, 0]];
            refPtr[1] = _tpl.RefFrame[TplCompRefFrames[bestCmpRfIdx, 1]];
            GetRateDistortion(ctx, out rateCost, out reconError, out predError, refPtr, rec, txSize, bestMode, miRow, miCol, useYOnly, false);
            tplStats.CmpRecrfDist[1] = reconError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
            tplStats.CmpRecrfRate[1] = rateCost;
        }
        if (bestMode == D203_PRED && xd.LeftAvailable && miRow + TxSizeHighUnit[txSize] < ctx.Tile.MiRowEnd)
        {
            int np = useYOnly ? 1 : cm.NumPlanes;
            for (int plane = 1; plane < np; ++plane)
            {
                int ssx = _cfg.SsX, ssy = _cfg.SsY;
                int st = rec.Strides[plane];
                int o = rec.Offsets[plane] + ((miRow * 4) >> ssy) * st + ((miCol * 4) >> ssx);
                int bhUv = bh >> ssy;
                if (hbd) { var d = rec.Buffers16[plane]; for (int i = 0; i < bhUv; ++i) d[o + (bhUv + i) * st - 1] = d[o + (bhUv - 1) * st - 1]; }
                else { var d = rec.Buffers[plane]; for (int i = 0; i < bhUv; ++i) d[o + (bhUv + i) * st - 1] = d[o + (bhUv - 1) * st - 1]; }
            }
        }
        refPtr[0] = bestMode == NEW_NEWMV ? _tpl.RefFrame[TplCompRefFrames[bestCmpRfIdx, 0]] : bestRfIdx >= 0 ? _tpl.RefFrame[bestRfIdx] : null;
        refPtr[1] = bestMode == NEW_NEWMV ? _tpl.RefFrame[TplCompRefFrames[bestCmpRfIdx, 1]] : null;
        GetRateDistortion(ctx, out rateCost, out reconError, out predError, refPtr, rec, txSize, bestMode, miRow, miCol, useYOnly, true);
        tplStats.RecrfDist = reconError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
        tplStats.RecrfSse = predError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
        tplStats.RecrfRate = rateCost;
        if (!AomInter.IsInterMode(bestMode))
        {
            tplStats.SrcrfDist = reconError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
            tplStats.SrcrfRate = rateCost;
            tplStats.SrcrfSse = predError << AomTplData.TPL_DEP_COST_SCALE_LOG2;
        }
        tplStats.RecrfDist = Math.Max(tplStats.SrcrfDist, tplStats.RecrfDist);
        tplStats.RecrfRate = Math.Max(tplStats.SrcrfRate, tplStats.RecrfRate);
        if (bestMode == NEWMV)
        {
            tplStats.Mv[bestRfIdx] = bestMv[0];
            tplStats.RefFrameIndex[0] = (sbyte)bestRfIdx;
            tplStats.RefFrameIndex[1] = NONE_FRAME;
        }
        else if (bestMode == NEW_NEWMV)
        {
            for (int k = 0; k < 2; k++)
            {
                tplStats.CmpRecrfDist[k] = Math.Min(tplStats.RecrfDist, Math.Max(tplStats.SrcrfDist, tplStats.CmpRecrfDist[k]));
                tplStats.CmpRecrfRate[k] = Math.Min(tplStats.RecrfRate, Math.Max(tplStats.SrcrfRate, tplStats.CmpRecrfRate[k]));
            }
            tplStats.RefFrameIndex[0] = (sbyte)TplCompRefFrames[bestCmpRfIdx, 0];
            tplStats.RefFrameIndex[1] = (sbyte)TplCompRefFrames[bestCmpRfIdx, 1];
            tplStats.Mv[tplStats.RefFrameIndex[0]] = bestMv[0];
            tplStats.Mv[tplStats.RefFrameIndex[1]] = bestMv[1];
        }
        for (int idy = 0; idy < miHeight; ++idy)
            for (int idx = 0; idx < miWidth; ++idx)
                if ((xd.MbToRightEdge >> 5) + miWidth > idx && (xd.MbToBottomEdge >> 5) + miHeight > idy)
                    xd.MiGrid[xd.MiOffset + idx + idy * cm.MiStride] = mbmi;
    }

    /// <summary>av1_predict_intra_block of the luma block from the TPL reconstruction into the predictor (stride bw).</summary>
    private static void TplPredictIntra(TplCtx ctx, AomFrameBuffer rec, int dstOff, int dstStride, int bw, int bh, int txSize, int mode)
    {
        var xd = ctx.X.E;
        var cpi = ctx.Cpi;
        if (rec.Hbd)
            AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, bw, bh, txSize, mode, 0, false, FILTER_INTRA_MODES, rec.Buffers16[0], dstOff,
                dstStride, ctx.Pred16, 0, bw, 0, 0, 0);
        else
            AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, bw, bh, txSize, mode, 0, false, FILTER_INTRA_MODES, rec.Buffers[0], dstOff,
                dstStride, ctx.Pred8, 0, bw, 0, 0, 0);
    }

    /// <summary>mc_flow_synthesizer.</summary>
    private void McFlowSynthesizer(int frameIdx, int miRows, int miCols)
    {
        if (frameIdx == 0) return;
        for (int miRow = 0; miRow < miRows; miRow += 4)
            for (int miCol = 0; miCol < miCols; miCol += 4)
            {
                TplModelUpdateB(miRow, miCol, frameIdx, 0);
                TplModelUpdateB(miRow, miCol, frameIdx, 1);
            }
    }

    private static int RoundFloor(int refPos, int bsizePix) => refPos < 0 ? -(1 + (-refPos - 1) / bsizePix) : refPos / bsizePix;

    /// <summary>av1_delta_rate_cost.</summary>
    private static long DeltaRateCost(long deltaRate, long recrfDist, long srcrfDist, int pixNum)
    {
        double beta = (double)srcrfDist / recrfDist;
        long rateCost = deltaRate;
        if (srcrfDist <= 128) return rateCost;
        double dr = (double)(deltaRate >> (AomTplData.TPL_DEP_COST_SCALE_LOG2 + 9)) / pixNum;
        double logDen = Math.Log(beta) / Math.Log(2.0) + 2.0 * dr;
        if (logDen > Math.Log(10.0) / Math.Log(2.0))
        {
            rateCost = (long)((Math.Log(1.0 / beta) * pixNum) / Math.Log(2.0) / 2.0);
            rateCost <<= AomTplData.TPL_DEP_COST_SCALE_LOG2 + 9;
            return rateCost;
        }
        double num = Math.Pow(2.0, logDen);
        double den = num * beta + (1 - beta) * beta;
        rateCost = (long)((pixNum * Math.Log(num / den)) / Math.Log(2.0) / 2.0);
        rateCost <<= AomTplData.TPL_DEP_COST_SCALE_LOG2 + 9;
        return rateCost;
    }

    /// <summary>tpl_model_update_b.</summary>
    private void TplModelUpdateB(int miRow, int miCol, int frameIdx, int r)
    {
        var tfp = _tpl.Frame(frameIdx);
        var tpl0 = _tpl.Frame(0);
        var s = tfp.Stats![AomTplData.PtrPos(miRow, miCol, tpl0.Stride)];
        bool isCompound = s.RefFrameIndex[1] >= 0;
        if (s.RefFrameIndex[r] < 0) return;
        int refFrameIndex = s.RefFrameIndex[r];
        int refMap = tfp.RefMapIndex[refFrameIndex];
        var refTf = _tpl.Frame(refMap);
        if (refMap < 0) return;
        var fullMv = s.Mv[refFrameIndex].ToFullMv();
        int refPosRow = miRow * 4 + fullMv.Row, refPosCol = miCol * 4 + fullMv.Col;
        const int bw = 16, bh = 16, miHeight = 4, miWidth = 4, pixNum = 256;
        int gridPosRowBase = RoundFloor(refPosRow, bh) * bh;
        int gridPosColBase = RoundFloor(refPosCol, bw) * bw;
        long srcrfDist = isCompound ? s.CmpRecrfDist[r == 0 ? 1 : 0] : s.SrcrfDist;
        long srcrfRate = isCompound ? (long)(s.CmpRecrfRate[r == 0 ? 1 : 0] << AomTplData.TPL_DEP_COST_SCALE_LOG2) : (long)(s.SrcrfRate << AomTplData.TPL_DEP_COST_SCALE_LOG2);
        long curDepDist = s.RecrfDist - srcrfDist;
        long mcDepDist = (long)(s.McDepDist * ((double)(s.RecrfDist - srcrfDist) / s.RecrfDist));
        long deltaRate = (long)(s.RecrfRate << AomTplData.TPL_DEP_COST_SCALE_LOG2) - srcrfRate;
        long mcDepRate = DeltaRateCost(s.McDepRate, s.RecrfDist, srcrfDist, pixNum);
        for (int block = 0; block < 4; ++block)
        {
            int gridPosRow = gridPosRowBase + bh * (block >> 1);
            int gridPosCol = gridPosColBase + bw * (block & 1);
            if (gridPosRow >= 0 && gridPosRow < refTf.MiRows * 4 && gridPosCol >= 0 && gridPosCol < refTf.MiCols * 4)
            {
                int minRow = Math.Max(gridPosRow, refPosRow), maxRow = Math.Min(gridPosRow + bh, refPosRow + bh);
                int minCol = Math.Max(gridPosCol, refPosCol), maxCol = Math.Min(gridPosCol + bw, refPosCol + bw);
                int overlapArea = minRow < maxRow && minCol < maxCol ? (maxRow - minRow) * (maxCol - minCol) : 0;
                int refMiRow = RoundFloor(gridPosRow, bh) * miHeight;
                int refMiCol = RoundFloor(gridPosCol, bw) * miWidth;
                var des = refTf.Stats![AomTplData.PtrPos(refMiRow, refMiCol, refTf.Stride)];
                des.McDepDist += ((curDepDist + mcDepDist) * overlapArea) / pixNum;
                des.McDepRate += ((deltaRate + mcDepRate) * overlapArea) / pixNum;
            }
        }
    }
}
