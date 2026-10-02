using System;
using System.Collections.Generic;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1_cx_iface.c encoder_encode, encoder.c av1_get_compressed_data / av1_post_encode_updates and
// encode_strategy.c av1_encode_strategy for the good-quality one-pass encoder (lag 0 or look-ahead processing).
internal sealed partial class AomGqEncoder
{
    private const int AOM_LAST_FLAG = 1, AOM_LAST2_FLAG = 2, AOM_LAST3_FLAG = 4, AOM_GOLD_FLAG = 8, AOM_BWD_FLAG = 16, AOM_ALT2_FLAG = 32,
        AOM_ALT_FLAG = 64, AOM_REFFRAME_ALL = 127;
    private const uint FRAMEFLAGS_KEY = 1 << 0, FRAMEFLAGS_GOLDEN = 1 << 1, FRAMEFLAGS_BWDREF = 1 << 2, FRAMEFLAGS_ALTREF = 1 << 3;
    private const int EncodeStage = 0, LapStage = 1;
    private const int ArnrMaxFrames = 7, KfFilteringEnabled = 1;

    /// <summary>ref_frame_priority_order.</summary>
    private static readonly int[] RefFramePriorityOrder =
        { LAST_FRAME, ALTREF_FRAME, BWDREF_FRAME, GOLDEN_FRAME, ALTREF2_FRAME, LAST2_FRAME, LAST3_FRAME };

    /// <summary>A trace sink for frame-level decisions (test hook).</summary>
    public Action<AomComp, AomGqFrameHeader>? OnFrameEncoded;
    /// <summary>Test hook: the symbol trace of the bitstream writer (AomWriter.Trace format).</summary>
    public System.IO.TextWriter? BitstreamTrace;

    /// <summary>ppi->fb_of_context_type.</summary>
    private readonly int[] _fbOfContextType = { -1, -1, -1, -1, -1, -1, -1, -1 };
    /// <summary>ppi->frame_probs (obmc / warped / interp filter; tx type probs are _txTypeProbs).</summary>
    private readonly AomFrameProbs _frameProbs = new();
    /// <summary>features->allow_screen_content_tools and cpi->is_screen_content_type of the last intra frame.</summary>
    private bool _sct, _isScreenContentType;
    /// <summary>ppi->filter_level.</summary>
    private readonly int[] _ppiFilterLevel = new int[4];

    // ---- rate control / GOP state (cpi->rc, ppi->p_rc, ppi->twopass, ppi->gf_group) ----
    private readonly AomRcState _rc = new();
    private readonly AomPrimaryRc _pRc = new();
    private readonly AomTwoPassState _twopass = new();
    private readonly AomGfGroup _gfGroup = new();
    private int _gfFrameIndex;
    private int _cmWidth, _cmHeight;
    private double _framerate = 30, _initFramerate = 30;
    private readonly double _targetBandwidth = 256000;
    private readonly bool _lapEnabled;
    private int _framesLeft;
    private int _gfMaxPyrHeight = 5, _gfMinPyrHeight;
    private int _cqLevel;
    private bool _rcModeQ => _cfg.Usage != REALTIME;
    private bool _losslessRequested;
    private int _arfGfBoostLst;
    private bool _internalAltrefAllowed;
    private readonly int _kfKeyFreqMax, _kfKeyFreqMin;
    private readonly bool _kfAutoKey;
    private int _tplPrevGopArfDispOrder = -1;
    private readonly bool _enableTplModel = true;
    private int GopLengthDecisionMethod => 0;
    private bool HasNoStatsStage => !_lapEnabled;
    private bool IsStatConsumptionStage => _lapEnabled;
    private bool IsStatConsumptionStageTwopass => false;
    /// <summary>ppi->show_existing_alt_ref, cpi->time_stamps.</summary>
    private bool _showExistingAltRef;
    private long _firstTsStart = long.MaxValue, _prevTsStart, _prevTsEnd;
    private uint _libFlags;

    // ---- lookahead (av1/encoder/lookahead.c) ----
    private sealed class LaEntry
    {
        public AomFrameBuffer Img = null!;
        public long TsStart, TsEnd, Flags;
        public int DisplayIdx;
        public AomGqFrameInput Input = null!;
    }

    private struct LaReadCtx { public int Sz, PopSz, ReadIdx; public bool Valid; }

    private LaEntry?[] _laBuf = Array.Empty<LaEntry?>();
    private int _lookaheadMaxSz, _laMaxPreFrames, _laWriteIdx, _laPushFrameCount;
    private readonly LaReadCtx[] _laRead = new LaReadCtx[2];

    private void LookaheadInit(int depth, int numLapBuffers)
    {
        int lagInFrames = Math.Max(1, depth);
        int maxPreFrames = 1;   // not all-intra
        depth += numLapBuffers;
        depth = Math.Clamp(depth, 1, 96);   // MAX_TOTAL_BUFFERS
        depth += maxPreFrames;
        _lookaheadMaxSz = depth;
        _laMaxPreFrames = maxPreFrames;
        _laRead[EncodeStage] = new LaReadCtx { PopSz = depth - maxPreFrames, Valid = true };
        if (numLapBuffers != 0) _laRead[LapStage] = new LaReadCtx { PopSz = lagInFrames, Valid = true };
        _laBuf = new LaEntry?[depth];
    }

    private int LaPop(ref int idx)
    {
        int index = idx;
        if (++idx >= _lookaheadMaxSz) idx -= _lookaheadMaxSz;
        return index;
    }

    private void LookaheadPush(AomFrameBuffer img, long tsStart, long tsEnd, long flags, AomGqFrameInput input)
    {
        _laRead[EncodeStage].Sz++;
        if (_laRead[LapStage].Valid) _laRead[LapStage].Sz++;
        int slot = LaPop(ref _laWriteIdx);
        _laBuf[slot] = new LaEntry { Img = img, TsStart = tsStart, TsEnd = tsEnd, Flags = flags, DisplayIdx = _laPushFrameCount, Input = input };
        ++_laPushFrameCount;
    }

    private LaEntry? LookaheadPopEntry(bool drain, int stage)
    {
        ref var rc = ref _laRead[stage];
        if (rc.Sz != 0 && (drain || rc.Sz == rc.PopSz))
        {
            int slot = LaPop(ref rc.ReadIdx);
            rc.Sz--;
            return _laBuf[slot];
        }
        return null;
    }

    private LaEntry? LookaheadPeek(int index, int stage)
    {
        ref var rc = ref _laRead[stage];
        if (index >= 0)
        {
            if (index < rc.Sz)
            {
                index += rc.ReadIdx;
                if (index >= _lookaheadMaxSz) index -= _lookaheadMaxSz;
                return _laBuf[index];
            }
        }
        else if (-index <= _laMaxPreFrames)
        {
            index += rc.ReadIdx;
            if (index < 0) index += _lookaheadMaxSz;
            return _laBuf[index];
        }
        return null;
    }

    private int LookaheadDepth(int stage) => _laRead[stage].Sz;
    private int LookaheadPopSz(int stage) => _laRead[stage].PopSz;

    /// <summary>is_forced_keyframe_pending.</summary>
    private int IsForcedKeyframePending(int upToIndex, int stage)
    {
        for (int i = 0; i <= upToIndex; i++)
        {
            var e = LookaheadPeek(i, stage);
            if (e == null) return -1;
            if (e.Flags == AOM_EFLAG_FORCE_KF) return i;
        }
        return -1;
    }

    /// <summary>aom_codec_encode (f) or the flush call (f null): pushes the frame, runs the encoder until a visible
    /// frame is produced, and returns its packet (the temporal unit) if any.</summary>
    public List<AomGqPacket> Encode(AomGqFrameInput? f)
    {
        var packets = new List<AomGqPacket>();
        bool flush = f == null;
        if (f != null)
        {
            // libavif sets AOME_SET_CQ_LEVEL / AV1E_SET_LOSSLESS when the quality changes (and once at init); their
            // av1_change_config resets cm's frame size (and mi grid) to the configured size before the next encode
            if (_frameNumberPushed > 0 && f.Quantizer != _prevQuantizer)
            {
                _miRows = ((_cfg.Height + 7) & ~7) >> 2;
                _miCols = ((_cfg.Width + 7) & ~7) >> 2;
            }
            _prevQuantizer = f.Quantizer;
            _cqLevel = QuantizerToQindex[f.Quantizer];
            _losslessRequested = f.Quantizer == 0;
            if (f.ScaleModeH != 0 || f.ScaleModeV != 0)
            {
                SetInternalSize(f.ScaleModeH, f.ScaleModeV);
                _resizeModeFixed = true;   // av1_set_internal_size: resize_mode = RESIZE_FIXED (and the TPL model off)
            }
            _spatialLayerId = f.SpatialLayerId;
            // av1_apply_encoding_flags with the call's flags (the frame's own flags are re-applied at encode time)
            ApplyEncodingFlags(f.Flags);
            // av1_receive_raw_frame: pts 0, duration 1 in the 1/30 timebase
            const long tsStart = 0, tsEnd = 10000000 / 30;
            LookaheadPush(MakeSource(f), tsStart, tsEnd, f.Flags, f);
            _frameNumberPushed++;
        }
        if (_lapEnabled) RunLapStage(flush);

        _libFlags = 0;
        byte[]? pending = null;
        bool isFrameVisible = false;
        while (!isFrameVisible)
        {
            var r = GetCompressedData(flush);
            if (r == null) break;
            byte[] data = r;
            if (data.Length == 0) continue;
            // a temporal delimiter before the first frame of a temporal unit (spatial layer 0)
            if (_spatialLayerId == 0 && pending == null)
            {
                var withTd = new byte[data.Length + 2];
                AomBitstream.TemporalDelimiter.CopyTo(withTd, 0);
                data.CopyTo(withTd, 2);
                data = withTd;
            }
            pending = pending == null ? data : Concat(pending, data);
            isFrameVisible = _lastShowFrame;
            if (isFrameVisible)
            {
                _framesLeft = Math.Max(0, _framesLeft - 1);
                packets.Add(new AomGqPacket(pending, _lastFrameWasKey));
                pending = null;
            }
        }
        if (pending != null) _pendingData = pending;   // invisible frames wait for the next visible one
        return packets;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private byte[]? _pendingData;
    private int _frameNumberPushed, _spatialLayerId;
    private bool _lastShowFrame, _lastFrameWasKey;
    private int _prevQuantizer;
    /// <summary>oxcf->resize_cfg.resize_mode != RESIZE_NONE (av1_is_resize_needed: the encoder border is
    /// AOM_BORDER_IN_PIXELS).</summary>
    private bool _resizeModeFixed;

    // ---- cpi->ext_flags ----
    private int _extRefFrameFlags = AOM_REFFRAME_ALL;
    private bool _extUpdatePending, _extUseRefFrameMvs = true, _extUseErrorResilient, _extUsePrimaryRefNone;
    private bool _extRefreshLast, _extRefreshGolden, _extRefreshBwd, _extRefreshAlt2, _extRefreshAlt;

    /// <summary>av1_apply_encoding_flags.</summary>
    private void ApplyEncodingFlags(long fl)
    {
        _extRefFrameFlags = AOM_REFFRAME_ALL;
        if ((fl & (AOM_EFLAG_NO_REF_LAST | AOM_EFLAG_NO_REF_LAST2 | AOM_EFLAG_NO_REF_LAST3 | AOM_EFLAG_NO_REF_GF | AOM_EFLAG_NO_REF_ARF |
                   AOM_EFLAG_NO_REF_BWD | AOM_EFLAG_NO_REF_ARF2)) != 0)
        {
            int r = AOM_REFFRAME_ALL;
            if ((fl & AOM_EFLAG_NO_REF_LAST) != 0) r ^= AOM_LAST_FLAG;
            if ((fl & AOM_EFLAG_NO_REF_LAST2) != 0) r ^= AOM_LAST2_FLAG;
            if ((fl & AOM_EFLAG_NO_REF_LAST3) != 0) r ^= AOM_LAST3_FLAG;
            if ((fl & AOM_EFLAG_NO_REF_GF) != 0) r ^= AOM_GOLD_FLAG;
            if ((fl & AOM_EFLAG_NO_REF_ARF) != 0) { r ^= AOM_ALT_FLAG; r ^= AOM_BWD_FLAG; r ^= AOM_ALT2_FLAG; }
            else
            {
                if ((fl & AOM_EFLAG_NO_REF_BWD) != 0) r ^= AOM_BWD_FLAG;
                if ((fl & AOM_EFLAG_NO_REF_ARF2) != 0) r ^= AOM_ALT2_FLAG;
            }
            _extRefFrameFlags = r;
        }
        if ((fl & (AOM_EFLAG_NO_UPD_LAST | AOM_EFLAG_NO_UPD_GF | AOM_EFLAG_NO_UPD_ARF)) != 0)
        {
            int upd = AOM_REFFRAME_ALL;
            if ((fl & AOM_EFLAG_NO_UPD_LAST) != 0) upd ^= AOM_LAST_FLAG;
            if ((fl & AOM_EFLAG_NO_UPD_GF) != 0) upd ^= AOM_GOLD_FLAG;
            if ((fl & AOM_EFLAG_NO_UPD_ARF) != 0) { upd ^= AOM_ALT_FLAG; upd ^= AOM_BWD_FLAG; upd ^= AOM_ALT2_FLAG; }
            _extRefreshLast = (upd & AOM_LAST_FLAG) != 0;
            _extRefreshGolden = (upd & AOM_GOLD_FLAG) != 0;
            _extRefreshAlt = (upd & AOM_ALT_FLAG) != 0;
            _extRefreshBwd = (upd & AOM_BWD_FLAG) != 0;
            _extRefreshAlt2 = (upd & AOM_ALT2_FLAG) != 0;
            _extUpdatePending = true;
        }
        else _extUpdatePending = false;
        _extUseRefFrameMvs = (fl & AOM_EFLAG_NO_REF_FRAME_MVS) == 0;
        _extUseErrorResilient = (fl & AOM_EFLAG_ERROR_RESILIENT) != 0;
        _extUsePrimaryRefNone = (fl & AOM_EFLAG_SET_PRIMARY_REF_NONE) != 0;
    }

    /// <summary>The frame parameters av1_encode_strategy hands av1_encode (EncodeFrameParams).</summary>
    private sealed class FrameParams
    {
        public int FrameType = INTER_FRAME, PrimaryRefFrame = PRIMARY_REF_NONE, RefFrameFlags, RefreshFrameFlags, OrderOffset;
        public bool ShowFrame = true, ShowExistingFrame, ErrorResilientMode;
        public int ExistingFbIdxToShow = -1;
        public bool RefreshGolden, RefreshBwd, RefreshAlt;
    }

    /// <summary>av1_get_compressed_data + av1_post_encode_updates: one frame's data (null: nothing to encode).</summary>
    private byte[]? GetCompressedData(bool flush)
    {
        var res = EncodeStrategy(flush, out bool popLookahead);
        if (res == null) return null;
        // av1_post_encode_updates
        if (_twopass.ThisFrame >= 0) _twopass.TotalLeftStats.Subtract(_twopass.Buf[_twopass.ThisFrame]);
        RefreshReferenceFrames();
        RcPostencodeUpdate(_curQindex, _curFrameType, _lastShowFrame, res.Length, _curRefresh.RefreshGolden, _curRefresh.RefreshAlt);
        if (popLookahead) LookaheadPopEntry(flush, EncodeStage);
        if (!HasNoStatsStage) TwopassPostencodeUpdate(_curQindex, _curFrameType);
        UpdateFbOfContextType();
        // update_rc_counts
        if (_lastShowFrame && _rc.FramesToKey != 0)
        {
            if (_lapEnabled)
            {
                var fi = _twopass.FirstpassInfo;
                if (fi.PastStatsCount > 1) fi.MoveCurIndexAndPop();
                else fi.MoveCurIndex();
            }
            _rc.FramesSinceKey++;
            _rc.FramesToKey--;
            _rc.FramesToFwdKf--;
            _rc.FramesSinceSceneChange++;
        }
        if (_lastShowFrame && _rc.FramesTillGfUpdateDue > 0) _rc.FramesTillGfUpdateDue--;
        ++_gfFrameIndex;
        if (_gfFrameIndex == MAX_STATIC_GF_GROUP_LENGTH) _gfFrameIndex = 0;
        // update_end_of_frame_stats: ppi->filter_level from the frame's searched (backup) levels
        if (!_curShowExisting) Array.Copy(_curBackupFilterLevel, _ppiFilterLevel, 4);
        return res;
    }

    private int _curQindex, _curFrameType;
    private bool _curShowExisting;
    private FrameParams _curRefresh = new();
    private readonly int[] _curBackupFilterLevel = new int[4];
    private AomRefBuffer? _curFrameBuf;
    private int _curRefreshFrameFlags, _curRefType;

    /// <summary>av1_encode_strategy: the frame's parameters, references and the encode; null when there is no
    /// frame to encode yet.</summary>
    private byte[]? EncodeStrategy(bool flush, out bool popLookahead)
    {
        popLookahead = false;
        if (!flush && LookaheadDepth(EncodeStage) < LookaheadPopSz(EncodeStage)) return null;
        if (LookaheadPeek(0, EncodeStage) == null) return null;
        if (HasNoStatsStage)
        {
            _gfMaxPyrHeight = Math.Min(_gfMaxPyrHeight, 1);   // USE_ALTREF_FOR_ONE_PASS
            _gfMinPyrHeight = Math.Min(_gfMinPyrHeight, _gfMaxPyrHeight);
        }
        if (!_tplBuffersSetup) TplSetupBuffers();   // av1_setup_tpl_buffers (tpl_stats_pool[0] == NULL)
        var fp = new FrameParams();
        _twopass.ThisFrame = -1;
        int ft = fp.FrameType;
        bool sf = fp.ShowFrame;
        GetSecondPassParams(ref ft, ref sf, _libFlags);
        fp.FrameType = ft;
        fp.ShowFrame = sf;

        var gf = _gfGroup;
        int idx = _gfFrameIndex;
        if (gf.UpdateType[idx] == OVERLAY_UPDATE && gf.RefbufState[idx] == REFBUF_RESET) fp.ShowExistingFrame = true;
        else fp.ShowExistingFrame = (_showExistingAltRef && gf.UpdateType[idx] == OVERLAY_UPDATE) || gf.UpdateType[idx] == INTNL_OVERLAY_UPDATE;
        fp.ShowExistingFrame &= AllowShowExisting(_libFlags);
        if (gf.UpdateType[idx] == OVERLAY_UPDATE) _showExistingAltRef = false;

        LaEntry? source, lastSource = null;
        if (fp.ShowExistingFrame)
        {
            source = LookaheadPeek(0, EncodeStage);
            popLookahead = true;
            fp.ShowFrame = true;
        }
        else source = ChooseFrameSource(ref flush, out popLookahead, out lastSource, out fp.ShowFrame);
        if (source == null) return null;
        gf.SrcOffset[idx] = 0;

        ApplyEncodingFlags(source.Flags);
        _libFlags = (source.Flags & AOM_EFLAG_FORCE_KF) != 0 ? FRAMEFLAGS_KEY : 0;
        if (fp.ShowFrame) AdjustFrameRate(source.TsStart, source.TsEnd);

        int frameUpdateType = gf.UpdateType[idx];
        if (fp.ShowExistingFrame && fp.FrameType != KEY_FRAME) fp.FrameType = INTER_FRAME;
        if (HasNoStatsStage)
        {
            bool kfRequested = _frameNumber == 0 || _kfKeyFreqMax == 0 || (_libFlags & FRAMEFLAGS_KEY) != 0;
            if (kfRequested && frameUpdateType != OVERLAY_UPDATE && frameUpdateType != INTNL_OVERLAY_UPDATE) fp.FrameType = KEY_FRAME;
        }
        // set_ext_overrides
        fp.ErrorResilientMode = _extUseErrorResilient && fp.FrameType != KEY_FRAME;
        bool forceRefreshAll = fp.FrameType == KEY_FRAME && fp.ShowFrame && !fp.ShowExistingFrame;
        ConfigureBufferUpdates(fp, frameUpdateType, gf.RefbufState[idx], forceRefreshAll);
        frameUpdateType = gf.UpdateType[idx];   // the ext refresh flags may retype the frame

        // references
        Span<int> dispOrder = stackalloc int[REF_FRAMES];
        Span<int> pyrLevel = stackalloc int[REF_FRAMES];
        InitRefMapPair(dispOrder, pyrLevel);
        int orderOffset = gf.ArfSrcOffset[idx];
        int curFrameDisp = _frameNumber + orderOffset;
        if (!_extUpdatePending) GetRefFrames(dispOrder, pyrLevel, curFrameDisp);
        var refBufsPrio = new AomRefBuffer?[INTER_REFS_PER_FRAME];
        bool hasRefFrames = false;
        for (int i = 0; i < INTER_REFS_PER_FRAME; i++)
        {
            refBufsPrio[i] = _refFrameMap[_remappedRefIdx[RefFramePriorityOrder[i] - LAST_FRAME]];
            if (refBufsPrio[i] != null) hasRefFrames = true;
        }
        if (!hasRefFrames && fp.FrameType == INTER_FRAME) throw new InvalidOperationException("inter frame without references");
        // get_ref_frame_flags
        int flags = _extRefFrameFlags;
        for (int i = 1; i < INTER_REFS_PER_FRAME; ++i)
            for (int j = 0; j < i; ++j)
                if (refBufsPrio[i] == refBufsPrio[j] && (flags & (1 << (RefFramePriorityOrder[j] - 1))) != 0)
                {
                    flags &= ~(1 << (RefFramePriorityOrder[i] - 1));
                    break;
                }
        fp.RefFrameFlags = flags;
        fp.PrimaryRefFrame = gf.IsFrameNonRef[idx] ? PRIMARY_REF_NONE : ChoosePrimaryRefFrame(fp);
        fp.OrderOffset = orderOffset;
        fp.RefreshFrameFlags = GetRefreshFrameFlags(fp, frameUpdateType, idx, curFrameDisp, dispOrder, pyrLevel);
        if (gf.IsFrameNonRef[idx]) fp.RefreshFrameFlags = 0;
        fp.ExistingFbIdxToShow = -1;
        if (fp.ShowExistingFrame)
            for (int frame = 0; frame < REF_FRAMES; frame++)
            {
                var b = _refFrameMap[frame];
                if (b == null) continue;
                if (b.DisplayOrderHint == curFrameDisp) fp.ExistingFbIdxToShow = frame;
            }

        // denoise_and_encode: temporal filtering (key frames / ARFs with look-ahead) and the TPL model
        AomFrameBuffer? filtered = null;
        if (!fp.ShowExistingFrame)
        {
            bool isSecondArf = gf.UpdateType[idx] == INTNL_ARF_UPDATE && gf.ArfSrcOffset[idx] >= TF_LOOKAHEAD_IDX_THR;
            bool applyFiltering = ArnrMaxFrames > 0 && _cfg.LagInFrames > 1;
            if (frameUpdateType != KF_UPDATE && frameUpdateType != ARF_UPDATE && !isSecondArf) applyFiltering = false;
            if (applyFiltering)
            {
                if (fp.FrameType == KEY_FRAME)
                {
                    bool allowKfFiltering = KfFilteringEnabled != 0 && !fp.ShowExistingFrame && !(_losslessRequested);
                    if (allowKfFiltering)
                    {
                        var yNoise = new double[3];
                        EstimateNoiseLevel(source.Img, yNoise, 0, 0, _cfg.BitDepth, NOISE_ESTIMATION_EDGE_THRESHOLD);
                        applyFiltering = yNoise[0] > 0;
                    }
                    else applyFiltering = false;
                }
                else if (isSecondArf) applyFiltering = TfCompressor().Sf.hl_sf.second_alt_ref_filtering != 0;
            }
            if (applyFiltering)
            {
                bool showExistingAltRef = false;
                int qIndex = RcPickQAndBounds(_cfg.Width, _cfg.Height, idx, fp.FrameType == KEY_FRAME || fp.FrameType == INTRA_ONLY_FRAME, out _, out _);
                if (frameUpdateType == KF_UPDATE || frameUpdateType == ARF_UPDATE)
                {
                    var tfBuf = TfInfoGetFilteredBuf(idx, out long dSum, out long dSse);
                    if (tfBuf != null)
                    {
                        filtered = tfBuf;
                        showExistingAltRef = CheckShowFilteredFrame(tfBuf, dSum, dSse, qIndex, _cfg.BitDepth, true, false);
                        _showableFrame = showExistingAltRef;
                    }
                    if (gf.FrameType[idx] != KEY_FRAME) _showExistingAltRef = showExistingAltRef;
                }
                if (isSecondArf)
                {
                    var tfSecond = new AomFrameBuffer(_cfg.Width, _cfg.Height, _cfg.SsX, _cfg.SsY, _cfg.Monochrome, _cfg.BitDepth);
                    TemporalFilter(gf.ArfSrcOffset[idx], idx, true, out _, out _, tfSecond);
                    AomResize.ExtendFrameBorders(tfSecond);
                    filtered = tfSecond;   // av1_check_show_filtered_frame is 1 for the second ARF
                    _showableFrame = true;
                }
            }
        }
        bool setMvParams = fp.FrameType == KEY_FRAME || frameUpdateType == ARF_UPDATE || frameUpdateType == GF_UPDATE;
        if (setMvParams) _mvSearchParamsDue = true;
        if (_gfFrameIndex == 0 && !fp.ShowExistingFrame)
        {
            // perform tpl after filtering
            bool allowTpl = _cfg.LagInFrames > 1 && _enableTplModel;
            if (gf.Size > AomTplData.MAX_LENGTH_TPL_FRAME_STATS) allowTpl = false;
            if (fp.FrameType != KEY_FRAME) allowTpl &= frameUpdateType == ARF_UPDATE || frameUpdateType == GF_UPDATE;
            if (allowTpl)
            {
                TplPreloadRcEstimate();
                TplSetupStats(0, fp);
            }
            else TplInitStats();
        }

        byte[] data = Av1Encode(fp, source, lastSource, filtered);
        _lastShowFrame = fp.ShowFrame;
        _lastFrameWasKey = _curFrameType == KEY_FRAME;
        // update_frame_flags
        if (fp.ShowExistingFrame) _libFlags &= ~(FRAMEFLAGS_GOLDEN | FRAMEFLAGS_BWDREF | FRAMEFLAGS_ALTREF | FRAMEFLAGS_KEY);
        else
        {
            _libFlags = fp.RefreshGolden ? _libFlags | FRAMEFLAGS_GOLDEN : _libFlags & ~FRAMEFLAGS_GOLDEN;
            _libFlags = fp.RefreshAlt ? _libFlags | FRAMEFLAGS_ALTREF : _libFlags & ~FRAMEFLAGS_ALTREF;
            _libFlags = fp.RefreshBwd ? _libFlags | FRAMEFLAGS_BWDREF : _libFlags & ~FRAMEFLAGS_BWDREF;
            _libFlags = _curFrameType == KEY_FRAME ? _libFlags | FRAMEFLAGS_KEY : _libFlags & ~FRAMEFLAGS_KEY;
        }
        return data;
    }

    private bool _mvSearchParamsDue;
    private readonly int[] _maxMvMagnitude = { 0 };
    /// <summary>ppi->valid_gm_model_found [FRAME_UPDATE_TYPES].</summary>
    private readonly int[] _validGmModelFound = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };

    /// <summary>allow_show_existing.</summary>
    private bool AllowShowExisting(uint frameFlags)
    {
        if (_frameNumber == 0) return false;
        var src = LookaheadPeek(0, EncodeStage);
        if (src == null) return true;
        bool isErrorResilient = (src.Flags & AOM_EFLAG_ERROR_RESILIENT) != 0;
        bool isSFrame = (src.Flags & AOM_EFLAG_SET_S_FRAME) != 0;
        bool isKeyFrame = _rc.FramesToKey == 0 || (frameFlags & FRAMEFLAGS_KEY) != 0;
        return !(isErrorResilient || isSFrame) || isKeyFrame;
    }

    /// <summary>choose_frame_source.</summary>
    private LaEntry? ChooseFrameSource(ref bool flush, out bool popLookahead, out LaEntry? lastSource, out bool showFrame)
    {
        var gf = _gfGroup;
        lastSource = null;
        int srcIndex = gf.ArfSrcOffset[_gfFrameIndex];
        if (srcIndex != 0 && IsForcedKeyframePending(srcIndex, EncodeStage) != -1 && !_rcModeQ)
        {
            srcIndex = 0;
            flush = true;
        }
        popLookahead = srcIndex == 0;
        if (popLookahead && KfFilteringEnabled > 1 && gf.UpdateType[_gfFrameIndex] == ARF_UPDATE)
        {
            ref var rc = ref _laRead[EncodeStage];
            if (rc.Sz != 0 && (flush || rc.Sz == rc.PopSz)) popLookahead = false;
        }
        showFrame = popLookahead;
        if (gf.SrcOffset[_gfFrameIndex] != 0) srcIndex = gf.SrcOffset[_gfFrameIndex];
        if (showFrame)
        {
            if (_frameNumber > 0) lastSource = LookaheadPeek(srcIndex - 1, EncodeStage);
            return LookaheadPeek(srcIndex, EncodeStage);
        }
        var s = LookaheadPeek(srcIndex, EncodeStage);
        if (s != null) _showableFrame = true;
        return s;
    }

    private bool _showableFrame;

    /// <summary>adjust_frame_rate.</summary>
    private void AdjustFrameRate(long tsStart, long tsEnd)
    {
        if (tsStart < _firstTsStart) { _firstTsStart = tsStart; _prevTsEnd = tsStart; }
        long thisDuration;
        int step = 0;
        if (tsStart == _firstTsStart)
        {
            thisDuration = tsEnd - tsStart;
            step = 1;
        }
        else
        {
            long lastDuration = _prevTsEnd - _prevTsStart;
            thisDuration = tsEnd - _prevTsEnd;
            if (lastDuration != 0) step = (int)((thisDuration - lastDuration) * 10 / lastDuration);
        }
        if (thisDuration != 0)
        {
            if (step != 0) NewFramerate(10000000.0 / thisDuration);
            else
            {
                double interval = Math.Min((double)(tsEnd - _firstTsStart), 10000000.0);
                double avgDuration = 10000000.0 / _framerate;
                avgDuration *= interval - avgDuration + thisDuration;
                avgDuration /= interval;
                NewFramerate(10000000.0 / avgDuration);
            }
        }
        _prevTsStart = tsStart;
        _prevTsEnd = tsEnd;
    }

    /// <summary>av1_configure_buffer_updates.</summary>
    private void ConfigureBufferUpdates(FrameParams fp, int type, int refbufState, bool forceRefreshAll)
    {
        _rc.IsSrcFrameAltRef = 0;
        void Set(bool g, bool b, bool a) { fp.RefreshGolden = g; fp.RefreshBwd = b; fp.RefreshAlt = a; }
        switch (type)
        {
            case KF_UPDATE: Set(true, true, true); break;
            case LF_UPDATE: Set(false, false, false); break;
            case GF_UPDATE: Set(true, false, false); break;
            case OVERLAY_UPDATE:
                if (refbufState == REFBUF_RESET) Set(true, true, true); else Set(true, false, false);
                _rc.IsSrcFrameAltRef = 1;
                break;
            case ARF_UPDATE:
                if (refbufState == REFBUF_RESET) Set(true, true, true); else Set(false, false, true);
                break;
            case INTNL_OVERLAY_UPDATE: Set(false, false, false); _rc.IsSrcFrameAltRef = 1; break;
            case INTNL_ARF_UPDATE: Set(false, true, false); break;
        }
        if (_extUpdatePending)
        {
            Set(_extRefreshGolden, _extRefreshBwd, _extRefreshAlt);
            if (_extRefreshGolden) _gfGroup.UpdateType[_gfFrameIndex] = GF_UPDATE;
            if (_extRefreshAlt) _gfGroup.UpdateType[_gfFrameIndex] = ARF_UPDATE;
            if (_extRefreshBwd) _gfGroup.UpdateType[_gfFrameIndex] = INTNL_ARF_UPDATE;
        }
        if (forceRefreshAll) Set(true, true, true);
    }

    /// <summary>init_ref_map_pair.</summary>
    private void InitRefMapPair(Span<int> dispOrder, Span<int> pyrLevel)
    {
        if (_gfGroup.UpdateType[_gfFrameIndex] == KF_UPDATE)
        {
            dispOrder.Fill(-1);
            pyrLevel.Fill(-1);
            return;
        }
        dispOrder.Clear();
        pyrLevel.Clear();
        for (int mapIdx = 0; mapIdx < REF_FRAMES; mapIdx++)
        {
            var b = _refFrameMap[mapIdx];
            if (dispOrder[mapIdx] == -1) continue;
            if (b == null) { dispOrder[mapIdx] = -1; pyrLevel[mapIdx] = -1; continue; }
            int refCount = 0;
            for (int k = 0; k < REF_FRAMES; k++) if (_refFrameMap[k] == b) refCount++;
            if (refCount > 1)
                for (int idx2 = mapIdx + 1; idx2 < REF_FRAMES; ++idx2)
                    if (_refFrameMap[idx2] == b) { dispOrder[idx2] = -1; pyrLevel[idx2] = -1; }
            dispOrder[mapIdx] = b.DisplayOrderHint;
            pyrLevel[mapIdx] = b.PyramidLevel;
        }
    }

    /// <summary>choose_primary_ref_frame.</summary>
    private int ChoosePrimaryRefFrame(FrameParams fp)
    {
        bool intraOnly = fp.FrameType == KEY_FRAME || fp.FrameType == INTRA_ONLY_FRAME;
        if (intraOnly || fp.ErrorResilientMode || _extUsePrimaryRefNone) return PRIMARY_REF_NONE;
        int currentRefType = CurrentFrameRefType();
        int wantedFb = _fbOfContextType[currentRefType];
        int primaryRefFrame = PRIMARY_REF_NONE;
        for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++)
            if (_remappedRefIdx[r - LAST_FRAME] == wantedFb) primaryRefFrame = r - LAST_FRAME;
        return primaryRefFrame;
    }

    /// <summary>get_current_frame_ref_type.</summary>
    private int CurrentFrameRefType()
    {
        int layerDepth = _gfGroup.LayerDepth[_gfFrameIndex];
        return layerDepth switch { 0 => 0, 1 => 1, MAX_ARF_LAYERS or MAX_ARF_LAYERS + 1 => 4, _ => 7 };
    }

    /// <summary>av1_get_refresh_frame_flags.</summary>
    private int GetRefreshFrameFlags(FrameParams fp, int frameUpdateType, int gfIndex, int curDispOrder, Span<int> dispOrder, Span<int> pyrLevel)
    {
        var gf = _gfGroup;
        if (gf.RefbufState[gfIndex] == REFBUF_RESET) return 0xff;
        if (fp.FrameType == S_FRAME) return 0xff;
        if (fp.ShowExistingFrame) return 0;
        if (_extUpdatePending)
        {
            // is_frame_droppable
            if (!(_extRefreshLast || _extRefreshGolden || _extRefreshAlt || _extRefreshBwd || _extRefreshAlt2)) return 0;
            int mask = 0;
            mask |= (_extRefreshLast ? 1 : 0) << _remappedRefIdx[LAST_FRAME - LAST_FRAME];
            mask |= (_extRefreshBwd ? 1 : 0) << _remappedRefIdx[BWDREF_FRAME - LAST_FRAME];
            mask |= (_extRefreshAlt2 ? 1 : 0) << _remappedRefIdx[ALTREF2_FRAME - LAST_FRAME];
            if (frameUpdateType == OVERLAY_UPDATE) mask |= (_extRefreshGolden ? 1 : 0) << _remappedRefIdx[ALTREF_FRAME - LAST_FRAME];
            else
            {
                mask |= (_extRefreshGolden ? 1 : 0) << _remappedRefIdx[GOLDEN_FRAME - LAST_FRAME];
                mask |= (_extRefreshAlt ? 1 : 0) << _remappedRefIdx[ALTREF_FRAME - LAST_FRAME];
            }
            return mask;
        }
        int freeFbIndex = -1;
        for (int i = 0; i < REF_FRAMES; ++i) if (dispOrder[i] == -1) { freeFbIndex = i; break; }
        if (frameUpdateType == OVERLAY_UPDATE || frameUpdateType == INTNL_OVERLAY_UPDATE) return 0;
        if (freeFbIndex != -1) return 1 << freeFbIndex;
        bool updateArf = frameUpdateType == ARF_UPDATE;
        int refreshIdx = GetRefreshIdx(dispOrder, pyrLevel, updateArf, gfIndex, true, curDispOrder);
        return 1 << refreshIdx;
    }

    /// <summary>get_refresh_idx.</summary>
    private int GetRefreshIdx(Span<int> dispOrder, Span<int> pyrLevel, bool updateArf, int gfIndex, bool enableRefreshSkip, int curFrameDisp)
    {
        int arfCount = 0, oldestArfOrder = int.MaxValue, oldestArfIdx = -1, oldestFrameOrder = int.MaxValue, oldestIdx = -1;
        for (int mapIdx = 0; mapIdx < REF_FRAMES; mapIdx++)
        {
            if (dispOrder[mapIdx] == -1) continue;
            int frameOrder = dispOrder[mapIdx];
            int level = pyrLevel[mapIdx];
            if (frameOrder > curFrameDisp - 3) continue;
            if (enableRefreshSkip)
            {
                bool skip = false;
                for (int i = 0; i < REF_FRAMES; i++)
                {
                    int toSkip = _gfGroup.SkipFrameRefresh[gfIndex, i];
                    if (toSkip == -1) break;
                    if (frameOrder == toSkip) { skip = true; break; }
                }
                if (skip) continue;
            }
            if (level == 1)
            {
                if (frameOrder < oldestArfOrder) { oldestArfOrder = frameOrder; oldestArfIdx = mapIdx; }
                arfCount++;
                continue;
            }
            if (frameOrder < oldestFrameOrder) { oldestFrameOrder = frameOrder; oldestIdx = mapIdx; }
        }
        if (updateArf && arfCount > 2) return oldestArfIdx;
        if (oldestIdx >= 0) return oldestIdx;
        if (oldestArfIdx >= 0) return oldestArfIdx;
        return oldestArfIdx;
    }

    /// <summary>av1_get_ref_frames (no external reference map / parallel encode).</summary>
    private void GetRefFrames(Span<int> dispOrder, Span<int> pyrLevel, int curFrameDisp, int[]? outRemapped = null)
    {
        int[] remapped = outRemapped ?? _remappedRefIdx;
        for (int i = 0; i < REF_FRAMES; ++i) remapped[i] = -1;
        var mapIdxs = new int[REF_FRAMES];
        var disp = new int[REF_FRAMES];
        var lvl = new int[REF_FRAMES];
        var used = new bool[REF_FRAMES];
        int nBufs = 0, minLevel = MAX_ARF_LAYERS, maxLevel = 0;
        for (int mapIdx = 0; mapIdx < REF_FRAMES; mapIdx++)
        {
            if (dispOrder[mapIdx] == -1) continue;
            int frameOrder = dispOrder[mapIdx];
            bool dup = false;
            for (int i = 0; i < nBufs; i++) if (disp[i] == frameOrder) { dup = true; break; }
            if (dup) continue;
            int level = pyrLevel[mapIdx];
            if (level < minLevel) minLevel = level;
            if (level > maxLevel) maxLevel = level;
            mapIdxs[nBufs] = mapIdx; disp[nBufs] = frameOrder; lvl[nBufs] = level; used[nBufs] = false;
            nBufs++;
        }
        for (int i = 1; i < nBufs; i++)
            for (int j = i; j > 0 && disp[j - 1] > disp[j]; j--)
            {
                (disp[j - 1], disp[j]) = (disp[j], disp[j - 1]);
                (mapIdxs[j - 1], mapIdxs[j]) = (mapIdxs[j], mapIdxs[j - 1]);
                (lvl[j - 1], lvl[j]) = (lvl[j], lvl[j - 1]);
            }
        void AddRef(int i, int frame) { remapped[frame - LAST_FRAME] = mapIdxs[i]; used[i] = true; }

        int nMinLevelRefs = 0, closestPastRef = -1, goldenIdx = -1, altrefIdx = -1;
        for (int i = nBufs - 1; i >= 0; i--)
        {
            if (lvl[i] == minLevel)
            {
                nMinLevelRefs++;
                if (disp[i] < curFrameDisp && goldenIdx == -1 && remapped[GOLDEN_FRAME - LAST_FRAME] == -1) goldenIdx = i;
                else if (disp[i] > curFrameDisp && altrefIdx == -1 && remapped[ALTREF_FRAME - LAST_FRAME] == -1) altrefIdx = i;
            }
            else if (disp[i] == curFrameDisp) AddRef(i, BWDREF_FRAME);
            if (disp[i] < curFrameDisp && closestPastRef < 0) closestPastRef = i;
        }
        if (nMinLevelRefs < nBufs)
        {
            if (goldenIdx > -1) AddRef(goldenIdx, GOLDEN_FRAME);
            if (altrefIdx > -1) AddRef(altrefIdx, ALTREF_FRAME);
        }
        if (nBufs > ALTREF_FRAME)
        {
            int maxDist = 0, unmappedIdx = -1;
            for (int i = 0; i < nBufs; i++)
            {
                if (used[i]) continue;
                if (lvl[i] != minLevel || nMinLevelRefs >= 5)
                {
                    int dist = Math.Abs(curFrameDisp - disp[i]);
                    if (dist > maxDist) { maxDist = dist; unmappedIdx = i; }
                }
            }
            used[unmappedIdx] = true;
        }
        int bufMapIdx;
        for (int frame = LAST_FRAME; frame < GOLDEN_FRAME; frame++)
        {
            if (remapped[frame - LAST_FRAME] != -1) continue;
            int nextBufMax = -1, nextDispOrder = int.MinValue;
            for (bufMapIdx = nBufs - 1; bufMapIdx >= 0; bufMapIdx--)
                if (!used[bufMapIdx] && disp[bufMapIdx] < curFrameDisp && disp[bufMapIdx] > nextDispOrder)
                {
                    nextDispOrder = disp[bufMapIdx];
                    nextBufMax = bufMapIdx;
                }
            bufMapIdx = nextBufMax;
            if (bufMapIdx < 0) break;
            if (used[bufMapIdx]) break;
            AddRef(bufMapIdx, frame);
        }
        for (int frame = BWDREF_FRAME; frame < REF_FRAMES; frame++)
        {
            if (remapped[frame - LAST_FRAME] != -1) continue;
            int nextBufMax = -1, nextDispOrder = int.MaxValue;
            for (bufMapIdx = nBufs - 1; bufMapIdx >= 0; bufMapIdx--)
                if (!used[bufMapIdx] && disp[bufMapIdx] > curFrameDisp && disp[bufMapIdx] < nextDispOrder)
                {
                    nextDispOrder = disp[bufMapIdx];
                    nextBufMax = bufMapIdx;
                }
            bufMapIdx = nextBufMax;
            if (bufMapIdx < 0) break;
            if (used[bufMapIdx]) break;
            AddRef(bufMapIdx, frame);
        }
        bufMapIdx = closestPastRef;
        for (int frame = LAST_FRAME; frame < REF_FRAMES; frame++)
        {
            if (remapped[frame - LAST_FRAME] != -1) continue;
            for (; bufMapIdx >= 0; bufMapIdx--) if (!used[bufMapIdx]) break;
            if (bufMapIdx < 0) break;
            if (used[bufMapIdx]) break;
            AddRef(bufMapIdx, frame);
        }
        bufMapIdx = nBufs - 1;
        for (int frame = ALTREF_FRAME; frame >= LAST_FRAME; frame--)
        {
            if (remapped[frame - LAST_FRAME] != -1) continue;
            for (; bufMapIdx > closestPastRef; bufMapIdx--) if (!used[bufMapIdx]) break;
            if (bufMapIdx < 0) break;
            if (used[bufMapIdx]) break;
            AddRef(bufMapIdx, frame);
        }
        for (int i = 0; i < REF_FRAMES; ++i) if (remapped[i] == -1) remapped[i] = 0;
    }

    /// <summary>get_true_pyr_level.</summary>
    private static int GetTruePyrLevel(int frameLevel, int frameOrder, int maxLayerDepth)
    {
        if (frameOrder == 0) return 1;
        if (frameLevel == MAX_ARF_LAYERS) return maxLayerDepth;
        if (frameLevel == MAX_ARF_LAYERS + 1) return 1;
        return Math.Max(1, frameLevel);
    }

    /// <summary>av1_encode + encode_frame_to_data_rate (no recode in AOM_Q without stats) for one frame.</summary>
    private byte[] Av1Encode(FrameParams fp, LaEntry src, LaEntry? lastSrc, AomFrameBuffer? filtered = null)
    {
        var cfg = _cfg;
        var gf = _gfGroup;
        int frameType = fp.FrameType;
        bool isKey = frameType == KEY_FRAME;
        if (fp.ShowExistingFrame) throw new NotImplementedException("show_existing_frame");
        if (isKey && gf.RefbufState[_gfFrameIndex] == REFBUF_RESET) _frameNumber = 0;
        int orderHintFull = _frameNumber + fp.OrderOffset;
        int displayOrderHint = orderHintFull;
        int orderHint = orderHintFull % (1 << (_seq.OrderHintBitsMinus1 + 1));
        int pyramidLevel = GetTruePyrLevel(gf.LayerDepth[_gfFrameIndex], displayOrderHint, gf.MaxLayerDepth);
        var unscaled = filtered ?? src.Img;   // the temporally filtered source of a key frame / ARF
        var f = src.Input;
        UpdatePrevGopArfSrc(unscaled, displayOrderHint);

        // av1_setup_frame_size: the pending AOME_SET_SCALEMODE size, else the configured size
        int width = cfg.Width, height = cfg.Height;
        if (_resizePendingW != 0 && _resizePendingH != 0) { width = _resizePendingW; height = _resizePendingH; _resizePendingW = _resizePendingH = 0; }
        _cmWidth = width;
        _cmHeight = height;

        // encode_without_recode: the source scaler (not svc: phase 8, the filter by the ratio)
        int filterScaler = EIGHTTAP_SMOOTH, phaseScaler = 8;
        if ((width << 1) == cfg.Width && (height << 1) == cfg.Height)
        {
            filterScaler = BILINEAR;
            if (width * height <= 320 * 180) filterScaler = EIGHTTAP_SMOOTH;
        }
        else if ((width << 2) == cfg.Width && (height << 2) == cfg.Height) filterScaler = EIGHTTAP_SMOOTH;
        else if ((width << 2) == 3 * cfg.Width && (height << 2) == 3 * cfg.Height) filterScaler = EIGHTTAP_REGULAR;
        var source = AomResize.ScaleIfRequired(unscaled, width, height, filterScaler, phaseScaler, true);

        // av1_set_size_dependent_vars: the TPL frame statistics (r0, the gfu boost), q
        if (_enableTplModel && _tpl.StatsReady(_gfFrameIndex)) ProcessTplStatsFrame();
        int qindex;
        if (cfg.UseFixedQpOffsets == 2 && _rcModeQ)
        {
            qindex = _cqLevel;
            _pRc.ArfQ = qindex;
        }
        else
        {
            qindex = RcPickQAndBounds(width, height, _gfFrameIndex, frameType == KEY_FRAME || frameType == INTRA_ONLY_FRAME, out _, out _);
            if (_rcModeQ && _tpl.Frame(_gfFrameIndex).IsValid && !_losslessRequested)
            {
                int tplQ = TplGetQIndex(_gfFrameIndex, _rc.ActiveWorstQuality, cfg.BitDepth);
                qindex = Math.Clamp(tplQ, _rc.BestQuality, _rc.WorstQuality);
                if (gf.UpdateType[_gfFrameIndex] == ARF_UPDATE) _pRc.ArfQ = qindex;
            }
            if (cfg.UseFixedQpOffsets == 1 && _rcModeQ) throw new NotImplementedException("use_fixed_qp_offsets 1");
        }

        var tune = f.Quantizer == 0 ? AomTune.Psnr : cfg.Tune;
        int updateType = gf.UpdateType[_gfFrameIndex];
        var refBufs = new AomRefBuffer?[REF_FRAMES];
        if (!isKey) for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++) refBufs[r] = _refFrameMap[_remappedRefIdx[r - LAST_FRAME]];
        var primaryRefBuf = fp.PrimaryRefFrame != PRIMARY_REF_NONE ? refBufs[fp.PrimaryRefFrame + LAST_FRAME] : null;
        var input = new AomEncodeInput
        {
            Width = width, Height = height, SsX = cfg.Monochrome ? 1 : cfg.SsX, SsY = cfg.Monochrome ? 1 : cfg.SsY, Monochrome = cfg.Monochrome,
            BitDepth = cfg.BitDepth, Mode = cfg.Usage, Speed = cfg.Speed, Tune = tune, Threads = Math.Min(cfg.Threads, 64),
            TileColumns = cfg.TileColumnsLog2, TileRows = cfg.TileRowsLog2, SourceFrame = source, UnfilteredSource = src.Img, Tpl = _cfg.LagInFrames > 1 ? _tpl : null, R0 = _r0,
            DeltaqObjective = cfg.Tune != AomTune.Iq && _enableTplModel,
            BaseQindex = qindex, UpdateType = updateType, GfFrameType = frameType, LayerDepth = gf.LayerDepth[_gfFrameIndex],
            SbSize = _seq.SbSize, SeqFlags = _seqFlags, TxTypeProbs = _txTypeProbs, EnableRestoration = cfg.EnableRestoration,
            Sharpness = cfg.Sharpness, EnableCdef = cfg.EnableCdef, UseFixedQpOffsets = cfg.UseFixedQpOffsets,
            SsimSource = unscaled, SsimMiRows = _miRows, SsimMiCols = _miCols, SsimBuffer = _ssimFactors,
            Seq = _seq, ShowFrame = fp.ShowFrame, OrderHint = orderHint, DisplayOrderHint = displayOrderHint,
            FrameNumber = _frameNumber, RefBufs = refBufs, RefFrameFlags = fp.RefFrameFlags, UseRefFrameMvs = _extUseRefFrameMvs,
            PrimaryRefBuf = primaryRefBuf, FrameProbs = _frameProbs, FilterScaler = filterScaler, PhaseScaler = phaseScaler,
            LagInFrames = cfg.LagInFrames, NumSpatialLayers = cfg.NumSpatialLayers, RefreshGolden = fp.RefreshGolden,
            PpiFilterLevel = _ppiFilterLevel, ResizeNeeded = _resizeModeFixed, IsSrcFrameAltRef = _rc.IsSrcFrameAltRef != 0,
            GfFrameIndex = _gfFrameIndex, GfArfIndex = gf.ArfIndex, CurPyramidLevel = pyramidLevel, ValidGmModelFound = _validGmModelFound,
            MvSearchState = _maxMvMagnitude, SetMvParamsEarly = _mvSearchParamsDue,
        };
        _mvSearchParamsDue = false;
        if (!isKey)
        {
            // screen content detection runs on intra frames only; inter frames keep the flags
            input.DetectScreenContent = false;
            input.AllowScreenContentTools = input.UseScreenContentTools = _sct;
            input.AllowIntrabc = false;
            input.IsScreenContentType = _isScreenContentType;
        }
        _seqFlags.SeqParamsLocked = _seqParamsLocked;
        var (cpi, x) = AomEncoder.EncodeFrame(input);
        _lastCpi = cpi;
        if (isKey) { _sct = cpi.AllowScreenContentTools; _isScreenContentType = input.IsScreenContentType; }
        if (!_seqParamsLocked)
        {
            _seq.EnableDistWtdComp &= _seqFlags.enable_dist_wtd_comp != 0;
            _seq.EnableDualFilter &= _seqFlags.enable_dual_filter != 0;
            _seq.EnableRestoration &= _seqFlags.enable_restoration != 0;
            _seq.EnableInterintraCompound &= _seqFlags.enable_interintra_compound != 0;
            _seq.EnableMaskedCompound &= _seqFlags.enable_masked_compound != 0;
            _seq.EnableCdef = cpi.CdefControl != 0;
        }

        // the frame's loop filters, applied to the reconstruction (good quality keeps them: it is a reference)
        AomEncoder.RunPostFilter(cpi, x, applyRestoration: true);

        var cm = cpi.Cm;
        // update_gm_stats
        {
            bool isGmPresent = false;
            for (int i = LAST_FRAME; i <= ALTREF_FRAME; i++) if (cm.GlobalMotion[i].WmType != IDENTITY) { isGmPresent = true; break; }
            if (_validGmModelFound[updateType] == int.MaxValue) _validGmModelFound[updateType] = isGmPresent ? 1 : 0;
            else _validGmModelFound[updateType] |= isGmPresent ? 1 : 0;
        }
        var fh = new AomGqFrameHeader
        {
            Seq = _seq, FrameType = frameType, ShowFrame = fp.ShowFrame, ShowableFrame = !fp.ShowFrame && _showableFrame, OrderHint = orderHint,
            PrimaryRefFrame = fp.PrimaryRefFrame, RefreshFrameFlags = fp.RefreshFrameFlags, SpatialLayerId = f.SpatialLayerId,
            UpscaledWidth = width, UpscaledHeight = height, RenderWidth = cfg.Width, RenderHeight = cfg.Height,
            AllowHighPrecisionMv = cm.AllowHighPrecisionMv, CurFrameForceIntegerMv = cm.CurFrameForceIntegerMv,
            SwitchableMotionMode = cm.SwitchableMotionMode, AllowRefFrameMvs = cm.AllowRefFrameMvs, AllowWarpedMotion = cm.AllowWarpedMotion,
            InterpFilter = cm.InterpFilter, ReferenceSelect = cm.ReferenceMode == REFERENCE_MODE_SELECT,
            SkipModeAllowed = cm.SkipModeAllowed, SkipModeFlag = cm.SkipModeFlag,
            PrevGlobalMotion = primaryRefBuf?.GlobalMotion,
        };
        if (primaryRefBuf != null)
        {
            Array.Copy(primaryRefBuf.RefDeltas, fh.PrevRefDeltas, 8);
            Array.Copy(primaryRefBuf.ModeDeltas, fh.PrevModeDeltas, 2);
        }
        for (int i = LAST_FRAME; i <= ALTREF_FRAME; i++) fh.GlobalMotion[i].CopyFrom(cm.GlobalMotion[i]);
        for (int i = 0; i < REF_FRAMES; i++) { fh.RemappedRefIdx[i] = _remappedRefIdx[i]; fh.RefFrameMap[i] = _refFrameMap[i]; }
        if (AomTrace.Out != null)
        {
            var tm = cpi.TileData[0].Tctx.Mode;
            var sb = new System.Text.StringBuilder("cic");
            for (int i = 0; i < 5; i++) sb.Append(' ').Append(tm.Comp[i][0]).Append('/').Append(tm.Comp[i][^1]);
            sb.Append(" | ii");
            for (int i = 0; i < 4; i++) sb.Append(' ').Append(tm.Intra[i][0]);
            AomTrace.Out.Write(sb.ToString() + (char)10);
        }
        byte[] data = AomBitstream.PackFrameGq(cpi, fh, out int largestTileId, out var largestTileFc, BitstreamTrace);
        OnFrameEncoded?.Invoke(cpi, fh);
        if (cpi.Sf.mv_sf.auto_mv_step_size != 0) _maxMvMagnitude[0] = Math.Max(_maxMvMagnitude[0], cpi.MaxMvMagnitudeTd);

        // the reconstruction (cur_frame) with the frame context of the largest tile
        var buf = new AomRefBuffer
        {
            Buf = cm.CurFrame, Width = width, Height = height, RenderWidth = cfg.Width, RenderHeight = cfg.Height,
            OrderHint = orderHint, DisplayOrderHint = displayOrderHint, FrameType = frameType, BaseQindex = qindex,
            MiRows = cm.MiRows, MiCols = cm.MiCols, PyramidLevel = pyramidLevel, Mvs = cm.CurFrameMvs, InterpFilter = cm.InterpFilter,
            Showable = !fp.ShowFrame && _showableFrame,
        };
        for (int i = 0; i < INTER_REFS_PER_FRAME; i++) { buf.RefOrderHints[i] = cm.CurRefOrderHints[i]; buf.RefDisplayOrderHint[i] = cpi.RefDisplayOrderHint[i]; }
        for (int i = LAST_FRAME; i <= ALTREF_FRAME; i++) buf.GlobalMotion[i].CopyFrom(cm.GlobalMotion[i]);
        Array.Copy(cpi.PostFilter!.LoopFilter.FrameFilterLevel, buf.FilterLevel, 2);
        Array.Copy(cpi.PostFilter.LoopFilter.BackupFilterLevel, _curBackupFilterLevel, 4);
        Array.Copy(cpi.PostFilter!.LoopFilter.RefDeltas, buf.RefDeltas, 8);
        Array.Copy(cpi.PostFilter.LoopFilter.ModeDeltas, buf.ModeDeltas, 2);
        AomResize.ExtendFrameBorders(buf.Buf);
        buf.FrameContext = new Av1CdfContext();
        buf.FrameContext.CopyFrom(largestTileFc!);   // the writer adapted cpi->tile_data[].tctx (row-mt encodes into td->tctx)
        _lastFc = buf.FrameContext;   // cm->fc
        buf.FrameContext.ResetCounters();

        _curFrameBuf = buf;
        _curRefreshFrameFlags = fp.RefreshFrameFlags;
        _curQindex = qindex;
        _curFrameType = frameType;
        _curShowExisting = fp.ShowExistingFrame;
        _curRefresh = fp;
        _curRefType = CurrentFrameRefType();
        _seqParamsLocked = true;
        _miRows = cm.MiRows;
        _miCols = cm.MiCols;
        _showableFrame = false;
        if (fp.ShowFrame) _frameNumber++;   // update_counters_for_show_frame
        return data;
    }

    /// <summary>refresh_reference_frames.</summary>
    private void RefreshReferenceFrames()
    {
        for (int i = 0; i < REF_FRAMES; i++)
            if (((_curRefreshFrameFlags >> i) & 1) != 0) _refFrameMap[i] = _curFrameBuf;
    }

    /// <summary>update_fb_of_context_type.</summary>
    private void UpdateFbOfContextType()
    {
        int refType = _curRefType;
        bool intraOnly = _curFrameType == KEY_FRAME || _curFrameType == INTRA_ONLY_FRAME;
        if (intraOnly || _curRefresh.ErrorResilientMode || _extUsePrimaryRefNone)
        {
            for (int i = 0; i < REF_FRAMES; i++) _fbOfContextType[i] = -1;
            _fbOfContextType[refType] = _lastShowFrame ? _remappedRefIdx[GOLDEN_FRAME - LAST_FRAME] : _remappedRefIdx[ALTREF_FRAME - LAST_FRAME];
        }
        if (!_curShowExisting)
        {
            if (_curFrameType == KEY_FRAME) _fbOfContextType[refType] = 0;
            else
                for (int i = 0; i < REF_FRAMES; i++)
                    if ((_curRefreshFrameFlags & (1 << i)) != 0) { _fbOfContextType[refType] = i; break; }
        }
    }

    private bool IsShorterGfIntervalBetter(int frameType, bool showFrame) => throw new NotImplementedException("is_shorter_gf_interval_better");
}
