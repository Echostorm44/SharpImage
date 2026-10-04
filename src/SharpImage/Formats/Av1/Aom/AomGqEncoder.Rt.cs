using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1's one-pass real-time frame level (ratectrl.c: av1_get_one_pass_rt_params and its helpers, the
// CBR q selection and post-encode update; av1_set_rtc_reference_structure_one_layer): AOM_USAGE_REALTIME with AOM_CBR,
// no lag, one spatial / temporal layer, no SVC, no external rate control, no active map, no dynamic resize, no frame
// dropping (rc_dropframe_thresh 0), NO_AQ.
internal sealed partial class AomGqEncoder
{
    private const int RATE_FACTOR_LEVELS = 4;
    private const double MIN_BPB_FACTOR = 0.005, MAX_BPB_FACTOR = 50;
    private const int FIXED_GF_INTERVAL_RT = 80, MAX_GF_INTERVAL_RT = 160, DEFAULT_KF_BOOST_RT = 2300, DEFAULT_GF_BOOST_RT = 2000;

    /// <summary>The real-time rate control state (RATE_CONTROL / PRIMARY_RATE_CONTROL fields only CBR reads) and the
    /// one-layer reference structure (RTC_REF).</summary>
    private sealed class RtState
    {
        public long OptimalBufferLevel, MaximumBufferSize;
        public readonly double[] RateCorrectionFactors = new double[RATE_FACTOR_LEVELS];
        public int Q1Frame, Q2Frame, Rc1Frame, Rc2Frame, BitEstRatio;
        public bool HighSourceSad, StaticSinceLastSceneChange;
        public ulong AvgSourceSad, PrevAvgSourceSad, MaxBlockSourceSad;
        public int PercentBlocksWithMotion, AvgFrameLowMotion;
        public ulong RecSse = ulong.MaxValue;
        public ulong[]? SrcSadBlk64;
        public readonly int[] RefIdx = new int[INTER_REFS_PER_FRAME];
        public readonly int[] Refresh = new int[REF_FRAMES];
        public int GldIdx1Layer;
        public bool ReduceNumRefBuffers;
        public bool Initialized;
    }

    private readonly RtState _rt = new();
    private const int UnderShootPct = 50, OverShootPct = 50;   // rc_undershoot_pct / rc_overshoot_pct (RT defaults)
    private const int BufInitialMs = 600, BufOptimalMs = 600, BufSizeMs = 1000;

    /// <summary>is_one_pass_rt_params.</summary>
    private bool IsOnePassRtParams => HasNoStatsStage && _lagInFrames == 0 && _cfg.Usage == REALTIME;

    /// <summary>use_rtc_reference_structure_one_layer.</summary>
    private bool UseRtcReferenceStructureOneLayer => IsOnePassRtParams && _cfg.NumSpatialLayers == 1;

    /// <summary>The CBR quantizer range libavif sets (rc_min/max_quantizer = quantizer -/+ 4) and, the first time,
    /// set_primary_rc_buffer_sizes / av1_primary_rc_init's CBR state.</summary>
    private void RtConfigure(int quantizer)
    {
        int minQ = Math.Max(quantizer - 4, 0), maxQ = Math.Min(quantizer + 4, 63);
        _rc.BestQuality = QuantizerToQindex[minQ];
        _rc.WorstQuality = QuantizerToQindex[maxQ];
        if (_rt.Initialized) return;
        _rt.Initialized = true;
        long bandwidth = (long)_targetBandwidth;
        _pRc.StartingBufferLevel = BufInitialMs * bandwidth / 1000;
        _rt.OptimalBufferLevel = BufOptimalMs * bandwidth / 1000;
        _rt.MaximumBufferSize = BufSizeMs * bandwidth / 1000;
        _pRc.BufferLevel = _pRc.StartingBufferLevel;
        _pRc.BitsOffTarget = _pRc.StartingBufferLevel;
        _pRc.AvgFrameQindex[KEY_FRAME] = _rc.WorstQuality;
        _pRc.AvgFrameQindex[INTER_FRAME] = _rc.WorstQuality;
        _pRc.AvgQ = ConvertQindexToQ(_rc.WorstQuality, _cfg.BitDepth);
        _pRc.LastQ[KEY_FRAME] = _rc.BestQuality;
        _pRc.LastQ[INTER_FRAME] = _rc.WorstQuality;
        for (int i = 0; i < RATE_FACTOR_LEVELS; ++i) _rt.RateCorrectionFactors[i] = 0.7;
        _rt.RateCorrectionFactors[KF_STD] = 1.0;
        _rc.NiAvQi = _rc.WorstQuality;
    }

    /// <summary>av1_get_one_pass_rt_params.</summary>
    private void GetOnePassRtParams(ref int frameType, LaEntry source, LaEntry? lastSource, uint frameFlags)
    {
        var gf = _gfGroup;
        int idx = _gfFrameIndex;
        if (SetKeyFrame(frameFlags))
        {
            frameType = KEY_FRAME;
            _pRc.ThisKeyFrameForced = _frameNumber != 0 && _rc.FramesToKey == 0 ? 1 : 0;
            _rc.FramesToKey = _kfKeyFreqMax;
            _pRc.KfBoost = DEFAULT_KF_BOOST_RT;
            gf.UpdateType[idx] = KF_UPDATE;
            gf.FrameType[idx] = KEY_FRAME;
            gf.RefbufState[idx] = REFBUF_RESET;
            _rc.FrameNumberEncoded = 0;
            _rt.StaticSinceLastSceneChange = false;
        }
        else
        {
            frameType = INTER_FRAME;
            gf.UpdateType[idx] = LF_UPDATE;
            gf.FrameType[idx] = INTER_FRAME;
            gf.RefbufState[idx] = REFBUF_UPDATE;
        }
        if (RtSf().check_scene_detection != 0)
        {
            if (_rc.PrevCodedWidth == _cmWidth && _rc.PrevCodedHeight == _cmHeight) RcSceneDetectionOnepassRt(source.Img, lastSource?.Img);
            else _rt.SrcSadBlk64 = null;
        }
        // rc_spatial_act_onepass_rt needs max_intra_bitrate_pct > 0 (0 by default); no dynamic resize
        int gfUpdate = SetGfIntervalUpdateOnepassRt(frameType);
        _rtGfUpdate = gfUpdate != 0;
        int target = frameType == KEY_FRAME || frameType == INTRA_ONLY_FRAME ? CalcIframeTargetSizeOnePassCbr()
            : CalcPframeTargetSizeOnePassCbr(gf.UpdateType[_gfFrameIndex]);
        RcSetFrameTarget(target, _cmWidth, _cmHeight);
        _rc.BaseFrameTarget = target;
        _cmFrameType = frameType;
    }

    private bool _rtGfUpdate;

    /// <summary>The speed features the frame-level real-time decisions read (cpi->sf as the previous frame left it;
    /// for the first frame the create-time framesize-independent set).</summary>
    private AomRealTimeSpeedFeatures RtSf()
    {
        if (_rtSfCache != null) return _rtSfCache;
        var sfIn = RtSfInputs(KEY_FRAME, _cfg.Width, _cfg.Height);
        var sf = new AomSpeedFeatures();
        sf.SetFramesizeIndependent(sfIn, new AomSpeedFeatureSeqFlags(), new AomWinnerModeParams(), _cfg.Speed);
        sf.SetFramesizeDependent(sfIn, new AomSpeedFeatureSeqFlags(), _cfg.Speed);
        return _rtSfCache = sf.rt_sf;
    }

    private AomRealTimeSpeedFeatures? _rtSfCache;

    private AomSpeedFeatureInputs RtSfInputs(int frameType, int width, int height) => new()
    {
        Width = width, Height = height, UseHighBitDepth = _cfg.BitDepth > 8, Mode = REALTIME, FrameType = frameType,
        BaseQindex = 0, RcMode = AOM_CBR, KeyFreqMax = _kfKeyFreqMax, LagInFrames = _lagInFrames, NumSpatialLayers = _cfg.NumSpatialLayers,
        Tuning = _cfg.Tune switch { AomTune.Iq => AOM_TUNE_IQ, AomTune.Ssim => AOM_TUNE_SSIM, _ => AOM_TUNE_PSNR },
    };

    /// <summary>set_key_frame (non-SVC).</summary>
    private bool SetKeyFrame(uint frameFlags)
    {
        if (_frameNumber == 0) return true;
        if ((frameFlags & FRAMEFLAGS_KEY) != 0) return true;
        return _kfAutoKey && _rc.FramesToKey == 0;
    }

    /// <summary>set_gf_interval_update_onepass_rt.</summary>
    private int SetGfIntervalUpdateOnepassRt(int frameType)
    {
        if (_rt.HighSourceSad || _rc.FramesTillGfUpdateDue == 0)
        {
            SetBaselineGfInterval(frameType);
            return 1;
        }
        return 0;
    }

    /// <summary>set_golden_update + set_baseline_gf_interval.</summary>
    private void SetBaselineGfInterval(int frameType)
    {
        // set_golden_update: divisor 10 (no cyclic refresh)
        const int divisor = 10;
        int[] gfLengthMult = { 8, 4 };
        _pRc.BaselineGfInterval = Math.Min(gfLengthMult[RtSf().gf_length_lvl] * (100 / divisor), MAX_GF_INTERVAL_RT);
        if (_rt.AvgFrameLowMotion != 0 && _rt.AvgFrameLowMotion < 40) _pRc.BaselineGfInterval = 16;

        if (_pRc.BaselineGfInterval > _rc.FramesToKey && _kfAutoKey) _pRc.BaselineGfInterval = _rc.FramesToKey;
        _pRc.GfuBoost = DEFAULT_GF_BOOST_RT;
        _pRc.ConstrainedGfGroup = _pRc.BaselineGfInterval >= _rc.FramesToKey && _kfAutoKey ? 1 : 0;
        _rc.FramesTillGfUpdateDue = _pRc.BaselineGfInterval;
        _gfFrameIndex = 0;
        _gfGroup.Size = _pRc.BaselineGfInterval;
        _gfGroup.UpdateType[0] = frameType == KEY_FRAME ? KF_UPDATE : GF_UPDATE;
        _gfGroup.RefbufState[_gfFrameIndex] = frameType == KEY_FRAME ? REFBUF_RESET : REFBUF_UPDATE;
    }

    /// <summary>av1_calc_iframe_target_size_one_pass_cbr.</summary>
    private int CalcIframeTargetSizeOnePassCbr()
    {
        long target;
        if (_frameNumber == 0) target = _pRc.StartingBufferLevel / 2 > int.MaxValue ? int.MaxValue : (int)(_pRc.StartingBufferLevel / 2);
        else
        {
            int kfBoost = 32;
            double framerate = _framerate;
            kfBoost = Math.Max(kfBoost, (int)Math.Round(2 * framerate - 16, MidpointRounding.AwayFromZero));
            if (_rc.FramesSinceKey < framerate / 2) kfBoost = (int)(kfBoost * _rc.FramesSinceKey / (framerate / 2));
            target = ((long)(16 + kfBoost) * _rc.AvgFrameBandwidth) >> 4;
        }
        return ClampIframeTargetSize(target);
    }

    /// <summary>av1_calc_pframe_target_size_one_pass_cbr (gf_cbr_boost_pct 0, max_inter_bitrate_pct 0).</summary>
    private int CalcPframeTargetSizeOnePassCbr(int frameUpdateType)
    {
        long diff = _rt.OptimalBufferLevel - _pRc.BufferLevel;
        long onePctBits = 1 + _rt.OptimalBufferLevel / 100;
        int minFrameTarget = Math.Max(_rc.AvgFrameBandwidth >> 4, FRAME_OVERHEAD_BITS);
        long target = _rc.AvgFrameBandwidth;
        if (diff > 0)
        {
            int pctLow = (int)Math.Min(diff / onePctBits, UnderShootPct);
            target -= target * pctLow / 200;
        }
        else if (diff < 0)
        {
            int pctHigh = (int)Math.Min(-diff / onePctBits, OverShootPct);
            target += target * pctHigh / 200;
        }
        if (target > int.MaxValue) target = int.MaxValue;
        return Math.Max(minFrameTarget, (int)target);
    }

    /// <summary>av1_set_rtc_reference_structure_one_layer (no frame dropping: the frame number is the current one).</summary>
    private void SetRtcReferenceStructureOneLayer(bool gfUpdate)
    {
        var rtSf = RtSf();
        uint frameNumber = (uint)_frameNumber;
        uint lagAlt = 4;
        int lastIdx = 0, lastIdxRefresh, gldIdx, altRefIdx = 0, last2Idx = 0;
        _extUpdatePending = true;
        _extRefFrameFlags = 0;
        _extRefreshLast = true;
        _extRefreshGolden = false;
        _extRefreshAlt = false;
        if (rtSf.sad_based_adp_altref_lag != 0)
        {
            lagAlt = 6;
            ulong[,] thFrameSad = { { 18000, 18000, 18000 }, { 25000, 25000, 25000 }, { 40000, 30000, 20000 }, { 30000, 25000, 20000 } };
            int thIdx = rtSf.sad_based_adp_altref_lag - 1;
            if (_rt.AvgSourceSad > thFrameSad[thIdx, 0]) lagAlt = 3;
            else if (_rt.AvgSourceSad > thFrameSad[thIdx, 1]) lagAlt = 4;
            else if (_rt.AvgSourceSad > thFrameSad[thIdx, 2]) lagAlt = 5;
        }
        for (int i = 0; i < INTER_REFS_PER_FRAME; ++i) _rt.RefIdx[i] = 7;
        for (int i = 0; i < REF_FRAMES; ++i) _rt.Refresh[i] = 0;
        _extRefFrameFlags ^= AOM_LAST_FLAG;
        if (rtSf.force_only_last_ref == 0)
        {
            _extRefFrameFlags ^= AOM_ALT_FLAG;
            _extRefFrameFlags ^= AOM_GOLD_FLAG;
            if (rtSf.ref_frame_comp_nonrd[1] != 0) _extRefFrameFlags ^= AOM_LAST2_FLAG;
        }
        const int sh = 6;
        if (frameNumber > 1) lastIdx = (int)((frameNumber - 1) % sh);
        lastIdxRefresh = (int)(frameNumber % sh);
        gldIdx = 6;
        if (frameNumber > lagAlt) altRefIdx = (int)((frameNumber - lagAlt) % sh);
        if (rtSf.ref_frame_comp_nonrd[1] != 0 && frameNumber > 2) last2Idx = (int)((frameNumber - 2) % sh);
        _rt.RefIdx[0] = lastIdx;
        _rt.RefIdx[1] = lastIdxRefresh;
        if (rtSf.ref_frame_comp_nonrd[1] != 0)
        {
            _rt.RefIdx[1] = last2Idx;
            _rt.RefIdx[2] = lastIdxRefresh;
        }
        _rt.RefIdx[3] = gldIdx;
        _rt.RefIdx[6] = altRefIdx;
        _rt.Refresh[lastIdxRefresh] = 1;
        if (gfUpdate && _cmFrameType != KEY_FRAME)
        {
            _extRefreshGolden = true;
            _rt.Refresh[gldIdx] = 1;
        }
        _rt.GldIdx1Layer = gldIdx;
        _rt.ReduceNumRefBuffers = _rt.RefIdx[0] < 7 && _rt.RefIdx[1] < 7 && _rt.RefIdx[3] < 7 && _rt.RefIdx[6] < 7 &&
                                  (rtSf.ref_frame_comp_nonrd[1] == 0 || _rt.RefIdx[2] < 7);
    }

    /// <summary>The refresh mask of the one-layer real-time structure (av1_get_refresh_frame_flags).</summary>
    private int RtcRefreshMask()
    {
        int mask = 0;
        for (int i = 0; i < INTER_REFS_PER_FRAME; i++)
        {
            int m = _rt.RefIdx[i];
            mask |= _rt.Refresh[m] << m;
        }
        return mask;
    }

    /// <summary>av1_rc_scene_detection_onepass_rt (no active map, content default, one spatial layer).</summary>
    private void RcSceneDetectionOnepassRt(AomFrameBuffer? src, AomFrameBuffer? last)
    {
        if (src == null || last == null) { _rt.SrcSadBlk64 = null; return; }
        if (src.CropWidths[0] != last.CropWidths[0] || src.CropHeights[0] != last.CropHeights[0]) { _rt.SrcSadBlk64 = null; return; }
        _rt.HighSourceSad = false;
        _rt.PercentBlocksWithMotion = 0;
        _rt.MaxBlockSourceSad = 0;
        _rt.PrevAvgSourceSad = _rt.AvgSourceSad;
        int numMiCols = _miCols, numMiRows = _miRows;
        int numZeroTempSad = 0;
        uint minThresh = 10000;
        if (RtSf().higher_thresh_scene_detection != 0)
            minThresh = _cmWidth * _cmHeight <= 320 * 240 && _framerate < 10.0 ? 50000u : 100000u;
        ulong avgSad = 0;
        int numSamples = 0;
        int thresh = _cmWidth * _cmHeight <= 320 * 240 && _framerate < 10.0 ? 5 : 6;
        int sbSizeByMb = _seq.SbSize == BLOCK_128X128 ? (32 >> 1) : 16;
        int sbCols = (numMiCols + sbSizeByMb - 1) / sbSizeByMb;
        int sbRows = (numMiRows + sbSizeByMb - 1) / sbSizeByMb;
        const int border = 0;   // no dropped previous frame, one temporal layer
        if (_rt.SrcSadBlk64 == null || _rt.SrcSadBlk64.Length != sbCols * sbRows) _rt.SrcSadBlk64 = new ulong[sbCols * sbRows];
        int sStride = src.Strides[0], lStride = last.Strides[0];
        for (int r = 0; r < sbRows - border; ++r)
            for (int c = 0; c < sbCols; ++c)
            {
                int so = src.Offsets[0] + (r * 64) * sStride + c * 64, lo = last.Offsets[0] + (r * 64) * lStride + c * 64;
                ulong tmpSad = src.Hbd ? AomHbd.Sad(src.Buffers16[0], so, sStride, last.Buffers16[0], lo, lStride, 64, 64)
                    : AomSad.Sad(src.Buffers[0], so, sStride, last.Buffers[0], lo, lStride, 64, 64);
                _rt.SrcSadBlk64[c + r * sbCols] = tmpSad;
                avgSad += tmpSad;
                numSamples++;
                if (tmpSad == 0) numZeroTempSad++;
                if (tmpSad > _rt.MaxBlockSourceSad) _rt.MaxBlockSourceSad = tmpSad;
            }
        if (numSamples > 0) avgSad /= (ulong)numSamples;
        int threshZeroSadSamples = avgSad > 8 * (ulong)minThresh ? 3 * (numSamples >> 2) : numSamples >> 1;
        _rt.HighSourceSad = avgSad > Math.Max(minThresh, (uint)(_rt.AvgSourceSad * (ulong)thresh)) && _rc.FramesSinceKey > 1 + 1 &&
                            numZeroTempSad < threshZeroSadSamples;
        _rt.AvgSourceSad = (3 * _rt.AvgSourceSad + avgSad) >> 2;
        _rc.FrameSourceSad = avgSad;
        if (numSamples > 0) _rt.PercentBlocksWithMotion = (numSamples - numZeroTempSad) * 100 / numSamples;
        if (_rc.FrameSourceSad > 0) _rt.StaticSinceLastSceneChange = false;
        if (_rt.HighSourceSad)
        {
            _rc.FramesSinceSceneChange = 0;
            _rt.StaticSinceLastSceneChange = true;
        }
    }

    // ---------------------------------------------------------------- CBR q

    private static int GetMbs(int width, int height)
    {
        int miCols = ((width + 7) & ~7) >> 2, miRows = ((height + 7) & ~7) >> 2;
        return ((miCols + 2) >> 2) * ((miRows + 2) >> 2);
    }

    /// <summary>av1_rc_bits_per_mb (CBR).</summary>
    private int RtBitsPerMb(int frameType, int qindex, double correctionFactor, bool accurateEstimate)
    {
        double q = ConvertQindexToQ(qindex, _cfg.BitDepth);
        int enumerator = _isScreenContentType ? (frameType == KEY_FRAME ? 1000000 : 750000) : (frameType == KEY_FRAME ? 2000000 : 1500000);
        if (frameType != KEY_FRAME && accurateEstimate && _rt.RecSse != ulong.MaxValue)
        {
            int mbs = GetMbs(_cmWidth, _cmHeight);
            double sseSqrt = (double)((int)Math.Sqrt((double)_rt.RecSse) << BPER_MB_NORMBITS) / mbs;
            int ratio = _rt.BitEstRatio == 0 ? (int)(300000 / sseSqrt) : _rt.BitEstRatio;
            enumerator = Math.Clamp((int)(ratio * sseSqrt), 20000, 170000);
        }
        // rc_adjust_keyframe needs max_intra_bitrate_pct > 0
        return (int)(enumerator * correctionFactor / q);
    }

    private bool AccurateBitEstimate => _rtHlAccurateBitEstimate;
    private bool _rtHlAccurateBitEstimate, _rtAccurateBitEstimateSf;

    /// <summary>get_rate_correction_factor (one pass, CBR: GF_ARF_STD only with gf_cbr_boost_pct > 20).</summary>
    private double RtGetRateCorrectionFactor(int width, int height)
    {
        double rcf = _cmFrameType == KEY_FRAME ? _rt.RateCorrectionFactors[KF_STD] : _rt.RateCorrectionFactors[INTER_NORMAL];
        rcf *= ResizeRateFactor(width, height);
        return Math.Clamp(rcf, MIN_BPB_FACTOR, MAX_BPB_FACTOR);
    }

    private void RtSetRateCorrectionFactor(double factor, int width, int height)
    {
        factor /= ResizeRateFactor(width, height);
        factor = Math.Clamp(factor, MIN_BPB_FACTOR, MAX_BPB_FACTOR);
        if (_cmFrameType == KEY_FRAME) _rt.RateCorrectionFactors[KF_STD] = factor;
        else _rt.RateCorrectionFactors[INTER_NORMAL] = factor;
    }

    private double ResizeRateFactor(int width, int height) => (double)(_cfg.Width * _cfg.Height) / (width * height);

    /// <summary>av1_estimate_bits_at_q.</summary>
    private int RtEstimateBitsAtQ(int q, double correctionFactor)
    {
        int mbs = GetMbs(_cmWidth, _cmHeight);
        int bpm = RtBitsPerMb(_cmFrameType, q, correctionFactor, AccurateBitEstimate);
        return Math.Max(FRAME_OVERHEAD_BITS, (int)((ulong)(uint)bpm * (ulong)mbs) >> BPER_MB_NORMBITS);
    }

    /// <summary>av1_rc_update_rate_correction_factors (no cyclic refresh).</summary>
    private void RtUpdateRateCorrectionFactors(int baseQindex, int width, int height)
    {
        double correctionFactor = 1.0;
        double rateCorrectionFactor = RtGetRateCorrectionFactor(width, height);
        if (_rc.IsSrcFrameAltRef != 0) return;
        int projectedSizeBasedOnQ = RtEstimateBitsAtQ(baseQindex, rateCorrectionFactor);
        if (projectedSizeBasedOnQ > FRAME_OVERHEAD_BITS) correctionFactor = (double)_rc.ProjectedFrameSize / projectedSizeBasedOnQ;
        correctionFactor = Math.Max(correctionFactor, 0.25);
        _rt.Q2Frame = _rt.Q1Frame;
        _rt.Q1Frame = baseQindex;
        _rt.Rc2Frame = _rt.Rc1Frame;
        _rt.Rc1Frame = correctionFactor > 1.1 ? -1 : correctionFactor < 0.9 ? 1 : 0;
        double adjustmentLimit;
        if (correctionFactor > 0.0)
            adjustmentLimit = _isScreenContentType ? 0.25 + 0.5 * Math.Min(0.5, Math.Abs(Math.Log10(correctionFactor)))
                : 0.25 + 0.75 * Math.Min(0.5, Math.Abs(Math.Log10(correctionFactor)));
        else adjustmentLimit = 0.75;
        if (correctionFactor > 1.01)
        {
            correctionFactor = 1.0 + (correctionFactor - 1.0) * adjustmentLimit;
            rateCorrectionFactor *= correctionFactor;
            if (rateCorrectionFactor > MAX_BPB_FACTOR) rateCorrectionFactor = MAX_BPB_FACTOR;
        }
        else if (correctionFactor < 0.99)
        {
            correctionFactor = 1.0 / correctionFactor;
            correctionFactor = 1.0 + (correctionFactor - 1.0) * adjustmentLimit;
            correctionFactor = 1.0 / correctionFactor;
            rateCorrectionFactor *= correctionFactor;
            if (rateCorrectionFactor < MIN_BPB_FACTOR) rateCorrectionFactor = MIN_BPB_FACTOR;
        }
        RtSetRateCorrectionFactor(rateCorrectionFactor, width, height);
    }

    /// <summary>find_closest_qindex_by_rate.</summary>
    private int FindClosestQindexByRate(int desiredBitsPerMb, double correctionFactor, int bestQindex, int worstQindex)
    {
        int low = bestQindex, high = worstQindex;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (RtBitsPerMb(_cmFrameType, mid, correctionFactor, AccurateBitEstimate) > desiredBitsPerMb) low = mid + 1;
            else high = mid;
        }
        int currQ = low;
        int currBits = RtBitsPerMb(_cmFrameType, currQ, correctionFactor, AccurateBitEstimate);
        int currBitDiff = currBits <= desiredBitsPerMb ? desiredBitsPerMb - currBits : int.MaxValue;
        int prevQ = currQ - 1;
        int prevBitDiff;
        if (currBitDiff == int.MaxValue || currQ == bestQindex) prevBitDiff = int.MaxValue;
        else prevBitDiff = RtBitsPerMb(_cmFrameType, prevQ, correctionFactor, AccurateBitEstimate) - desiredBitsPerMb;
        return currBitDiff <= prevBitDiff ? currQ : prevQ;
    }

    /// <summary>av1_rc_regulate_q (+ adjust_q_cbr).</summary>
    private int RtRegulateQ(int targetBitsPerFrame, int activeBest, int activeWorst, int width, int height, AomRefBuffer? prevFrame)
    {
        int mbs = GetMbs(width, height);
        double cf = RtGetRateCorrectionFactor(width, height);
        int targetBitsPerMb = (int)(((ulong)(uint)targetBitsPerFrame << BPER_MB_NORMBITS) / (ulong)mbs);
        int q = FindClosestQindexByRate(targetBitsPerMb, cf, activeBest, activeWorst);
        return AdjustQCbr(q, activeWorst, width, height, prevFrame);
    }

    /// <summary>adjust_q_cbr (no cyclic refresh, no SVC, no RPS bias).</summary>
    private int AdjustQCbr(int q, int activeWorstQuality, int width, int height, AomRefBuffer? prevFrame)
    {
        bool overshootBufferLow = _rt.Rc1Frame == -1 && _rc.FrameSourceSad > 1000 && _pRc.BufferLevel < (_rt.OptimalBufferLevel >> 1) &&
                                  _rc.FramesSinceKey > 4;
        int maxDeltaUp = overshootBufferLow ? 120 : 20;
        bool changeAvgFrameBandwidth = Math.Abs(_rc.AvgFrameBandwidth - _rc.PrevAvgFrameBandwidth) > 0.1 * _rc.AvgFrameBandwidth;
        int maxDeltaDown = _isScreenContentType ? Math.Clamp(_rt.Q1Frame / 16, 1, 8) : Math.Clamp(_rt.Q1Frame / 8, 1, 16);
        // rc_faster_convergence_static needs cyclic refresh
        bool changeTargetBitsMb = prevFrame != null && (width != prevFrame.Width || height != prevFrame.Height || changeAvgFrameBandwidth);
        bool intraOnly = _cmFrameType == KEY_FRAME || _cmFrameType == INTRA_ONLY_FRAME;
        if (!intraOnly && _rc.FramesSinceKey > 1 && _rt.Q1Frame > 0 && _rt.Q2Frame > 0 && !changeTargetBitsMb)
        {
            if (_rt.Rc1Frame * _rt.Rc2Frame == -1 && _rt.Q1Frame != _rt.Q2Frame && !overshootBufferLow)
            {
                int qclamp = Math.Clamp(q, Math.Min(_rt.Q1Frame, _rt.Q2Frame), Math.Max(_rt.Q1Frame, _rt.Q2Frame));
                if (_rt.Rc1Frame == -1 && q > qclamp && _rc.FramesSinceKey > 10) q = (q + qclamp) >> 1;
                else q = qclamp;
            }
            if (RtSf().check_scene_detection != 0 && _rt.PrevAvgSourceSad > 0 && _rc.FramesSinceKey > 10 && _rc.FrameSourceSad > 0)
            {
                double delta = (double)_rt.AvgSourceSad / _rt.PrevAvgSourceSad - 1.0;
                if (delta < 0.0 && _pRc.BufferLevel > (_rt.OptimalBufferLevel >> 2) && q > (_rc.WorstQuality >> 1))
                {
                    double qAdjFactor = 1.0 + 0.5 * Math.Tanh(4.0 * delta);
                    double qVal = ConvertQindexToQ(q, _cfg.BitDepth);
                    q += ComputeQdelta(qVal, qVal * qAdjFactor);
                }
                else if (_rt.Q1Frame - q > 0 && delta > 0.1 && _pRc.BufferLevel < Math.Min(_rt.MaximumBufferSize, _rt.OptimalBufferLevel << 1))
                    q = (3 * q + _rt.Q1Frame) >> 2;
            }
            if (_rt.Q1Frame - q > maxDeltaDown) q = _rt.Q1Frame - maxDeltaDown;
            else if (q - _rt.Q1Frame > maxDeltaUp) q = _rt.Q1Frame + maxDeltaUp;
        }
        if (prevFrame != null && width * height > 1.5 * prevFrame.Width * prevFrame.Height) q = (q + activeWorstQuality) >> 1;
        return Math.Clamp(q, _rc.BestQuality, _rc.WorstQuality);
    }

    /// <summary>calc_active_worst_quality_no_stats_cbr (no SVC; rc_compute_spatial_var_sc_kf needs max_intra_bitrate_pct).</summary>
    private int CalcActiveWorstQualityNoStatsCbr()
    {
        const uint numFramesWeightKey = 5;
        long criticalLevel = _rt.OptimalBufferLevel >> 3;
        int adjustment = 0;
        int activeWorstQuality;
        if (_cmFrameType == KEY_FRAME || _cmFrameType == INTRA_ONLY_FRAME) return _rc.WorstQuality;
        int avgQindexKey = _pRc.AvgFrameQindex[KEY_FRAME];
        int ambientQp = (uint)_frameNumber < numFramesWeightKey ? Math.Min(_pRc.AvgFrameQindex[INTER_FRAME], avgQindexKey)
            : _pRc.AvgFrameQindex[INTER_FRAME];
        ambientQp = Math.Min(_rc.WorstQuality, ambientQp);
        if (_pRc.BufferLevel > _rt.OptimalBufferLevel)
        {
            activeWorstQuality = Math.Min(_rc.WorstQuality, ambientQp * 5 / 4);
            int maxAdjustmentDown = activeWorstQuality / 3;
            if (maxAdjustmentDown != 0)
            {
                long buffLvlStep = (_rt.MaximumBufferSize - _rt.OptimalBufferLevel) / maxAdjustmentDown;
                if (buffLvlStep != 0) adjustment = (int)((_pRc.BufferLevel - _rt.OptimalBufferLevel) / buffLvlStep);
                activeWorstQuality -= adjustment;
            }
        }
        else if (_pRc.BufferLevel > criticalLevel)
        {
            activeWorstQuality = Math.Min(_rc.WorstQuality, ambientQp);
            if (criticalLevel != 0)
            {
                long buffLvlStep = _rt.OptimalBufferLevel - criticalLevel;
                if (buffLvlStep != 0)
                    adjustment = (int)((_rc.WorstQuality - ambientQp) * (_rt.OptimalBufferLevel - _pRc.BufferLevel) / buffLvlStep);
                activeWorstQuality += adjustment;
            }
        }
        else activeWorstQuality = _rc.WorstQuality;
        return activeWorstQuality;
    }

    /// <summary>calc_active_best_quality_no_stats_cbr (gf_cbr_boost_pct 0).</summary>
    private int CalcActiveBestQualityNoStatsCbr(int activeWorstQuality, int width, int height)
    {
        int bd = _cfg.BitDepth;
        int[] rtcMinq = RtcMinq[BdIdx(bd)];
        int activeBestQuality = _rc.BestQuality;
        bool is608 = Math.Min(_cmWidth, _cmHeight) >= 608, is480 = Math.Min(_cmWidth, _cmHeight) >= 480;
        int resIdx = (is480 ? 1 : 0) + (is608 ? 1 : 0);
        if (_cmFrameType == KEY_FRAME || _cmFrameType == INTRA_ONLY_FRAME)
        {
            if (_pRc.ThisKeyFrameForced != 0)
            {
                int qindex = _pRc.LastBoostedQindex;
                double lastBoostedQ = ConvertQindexToQ(qindex, bd);
                int deltaQindex = ComputeQdelta(lastBoostedQ, lastBoostedQ * 0.75);
                activeBestQuality = Math.Max(qindex + deltaQindex, _rc.BestQuality);
            }
            else if (_frameNumber > 0)
            {
                double qAdjFactor = 1.0;
                activeBestQuality = GetKfActiveQuality(_pRc.AvgFrameQindex[KEY_FRAME], resIdx, true);
                if (width * height <= 352 * 288) qAdjFactor -= 0.25;
                double qVal = ConvertQindexToQ(activeBestQuality, bd);
                activeBestQuality += ComputeQdelta(qVal, qVal * qAdjFactor);
            }
        }
        else
        {
            int frameType = _frameNumber > 1 ? INTER_FRAME : KEY_FRAME;
            activeBestQuality = _pRc.AvgFrameQindex[frameType] < activeWorstQuality ? rtcMinq[_pRc.AvgFrameQindex[frameType]]
                : rtcMinq[activeWorstQuality];
        }
        return activeBestQuality;
    }

    /// <summary>av1_rc_pick_q_and_bounds for one-pass CBR (rc_pick_q_and_bounds_no_stats_cbr).</summary>
    private int RtPickQAndBounds(int width, int height, AomFrameBuffer? unscaledSource, AomRefBuffer? lastRef, AomRefBuffer? prevFrame,
        out int bottomIndex, out int topIndex)
    {
        _rt.RecSse = ulong.MaxValue;
        if (AccurateBitEstimate && _cmFrameType != KEY_FRAME) RcComputeVarianceOnepassRt(unscaledSource, lastRef);
        int activeWorst = CalcActiveWorstQualityNoStatsCbr();
        int activeBest = CalcActiveBestQualityNoStatsCbr(activeWorst, width, height);
        activeBest = Math.Clamp(activeBest, _rc.BestQuality, _rc.WorstQuality);
        activeWorst = Math.Clamp(activeWorst, activeBest, _rc.WorstQuality);
        topIndex = activeWorst;
        bottomIndex = activeBest;
        if (_cmFrameType == KEY_FRAME && _pRc.ThisKeyFrameForced == 0 && _frameNumber != 0)
        {
            int qdelta = ComputeQdeltaByRate(_cmFrameType, activeWorst, 2.0);
            topIndex = activeWorst + qdelta;
            topIndex = Math.Max(topIndex, bottomIndex);
        }
        int q = RtRegulateQ(_rc.ThisFrameTarget, activeBest, activeWorst, width, height, prevFrame);
        if (q > topIndex)
        {
            if (_rc.ThisFrameTarget >= _rc.MaxFrameBandwidth) topIndex = q;
            else q = topIndex;
        }
        _rc.ActiveWorstQuality = topIndex;
        return q;
    }

    /// <summary>rc_compute_variance_onepass_rt: the SSE between the 4x4-averaged source and LAST over 64x64 blocks.</summary>
    private void RcComputeVarianceOnepassRt(AomFrameBuffer? src, AomRefBuffer? lastRef)
    {
        if (src == null || lastRef == null) return;
        var pre = lastRef.Buf;
        if (src.CropWidths[0] != pre.CropWidths[0] || src.CropHeights[0] != pre.CropHeights[0] ||
            src.CropWidths[1] != pre.CropWidths[1] || src.CropHeights[1] != pre.CropHeights[1]) return;
        int sbSizeByMb = _seq.SbSize == BLOCK_128X128 ? 16 : 16;
        int sbCols = (_miCols + sbSizeByMb - 1) / sbSizeByMb, sbRows = (_miRows + sbSizeByMb - 1) / sbSizeByMb;
        ulong fsse = 0;
        int numSamples = 0;
        _rt.RecSse = 0;
        var blk = new byte[64 * 64];
        var blk16 = src.Hbd ? new ushort[64 * 64] : null;
        int ss = src.Strides[0], ps = pre.Strides[0];
        for (int r = 0; r < sbRows; ++r)
            for (int c = 0; c < sbCols; ++c)
            {
                int so = src.Offsets[0] + r * 64 * ss + c * 64, po = pre.Offsets[0] + r * 64 * ps + c * 64;
                for (int i = 0; i < 64; i += 4)
                    for (int j = 0; j < 64; j += 4)
                    {
                        int sum = 0;
                        for (int m = 0; m < 4; m++)
                            for (int n = 0; n < 4; n++)
                                sum += src.Hbd ? src.Buffers16[0][so + (i + m) * ss + j + n] : src.Buffers[0][so + (i + m) * ss + j + n];
                        int avg = (sum + 8) >> 4;
                        for (int m = 0; m < 4; ++m)
                            for (int n = 0; n < 4; ++n)
                            {
                                if (blk16 != null) blk16[i * 64 + j + m * 64 + n] = (ushort)avg;
                                else blk[i * 64 + j + m * 64 + n] = (byte)avg;
                            }
                    }
                uint sse;
                if (blk16 != null) AomHbd.Variance(blk16, 0, 64, pre.Buffers16[0], po, ps, 0, 64, 64, _cfg.BitDepth, out sse);
                else AomSad.Variance(blk, 0, 64, pre.Buffers[0], po, ps, 64, 64, out sse);
                fsse += sse;
                numSamples++;
            }
        if (numSamples > 0) _rt.RecSse = fsse > 0 ? fsse : 1;
    }

    /// <summary>av1_rc_postencode_update for one-pass CBR.</summary>
    private void RtPostencodeUpdate(int qindex, int frameType, bool showFrame, long bytesUsed, bool refreshGolden)
    {
        _rc.ProjectedFrameSize = (int)(bytesUsed << 3);
        RtUpdateRateCorrectionFactors(qindex, _cmWidth, _cmHeight);
        if (frameType != KEY_FRAME && AccurateBitEstimate)
        {
            double q = ConvertQindexToQ(qindex, _cfg.BitDepth);
            int thisBitEstRatio = (int)(_rc.ProjectedFrameSize * q / Math.Sqrt((double)_rt.RecSse));
            _rt.BitEstRatio = _rt.BitEstRatio == 0 ? thisBitEstRatio : (7 * _rt.BitEstRatio + thisBitEstRatio) / 8;
        }
        if (frameType == KEY_FRAME)
        {
            _pRc.LastQ[KEY_FRAME] = qindex;
            _pRc.AvgFrameQindex[KEY_FRAME] = (3 * _pRc.AvgFrameQindex[KEY_FRAME] + qindex + 2) >> 2;
            _rc.LastEncodedSizeKeyframe = _rc.ProjectedFrameSize;
            _rc.LastTargetSizeKeyframe = _rc.ThisFrameTarget;
        }
        else if (_rc.IsSrcFrameAltRef == 0 && !refreshGolden)
        {
            _pRc.LastQ[INTER_FRAME] = qindex;
            _pRc.AvgFrameQindex[INTER_FRAME] = (3 * _pRc.AvgFrameQindex[INTER_FRAME] + qindex + 2) >> 2;
            _pRc.NiFrames++;
            _pRc.TotQ += ConvertQindexToQ(qindex, _cfg.BitDepth);
            _pRc.AvgQ = _pRc.TotQ / _pRc.NiFrames;
            _rc.NiTotQi += qindex;
            _rc.NiAvQi = _rc.NiTotQi / _pRc.NiFrames;
        }
        if (qindex < _pRc.LastBoostedQindex || frameType == KEY_FRAME ||
            (_pRc.ConstrainedGfGroup == 0 && refreshGolden && _rc.IsSrcFrameAltRef == 0))
            _pRc.LastBoostedQindex = qindex;
        if (frameType == KEY_FRAME) _pRc.LastKfQindex = qindex;
        // update_buffer_level
        if (!showFrame) _pRc.BitsOffTarget -= _rc.ProjectedFrameSize;
        else _pRc.BitsOffTarget += _rc.AvgFrameBandwidth - _rc.ProjectedFrameSize;
        _pRc.BitsOffTarget = Math.Min(_pRc.BitsOffTarget, _rt.MaximumBufferSize);
        _pRc.BufferLevel = _pRc.BitsOffTarget;
        _rc.PrevAvgFrameBandwidth = _rc.AvgFrameBandwidth;
        if (_cmWidth != _cfg.Width || _cmHeight != _cfg.Height)
            _rc.ThisFrameTarget = SaturateToInt(_rc.ThisFrameTarget / ResizeRateFactor(_cmWidth, _cmHeight));
        if (frameType != KEY_FRAME)
        {
            _pRc.RollingTargetBits = (int)(((long)_pRc.RollingTargetBits * 3 + _rc.ThisFrameTarget + 2) >> 2);
            _pRc.RollingActualBits = (int)(((long)_pRc.RollingActualBits * 3 + _rc.ProjectedFrameSize + 2) >> 2);
        }
        _pRc.TotalActualBits += _rc.ProjectedFrameSize;
        _pRc.TotalTargetBits += showFrame ? _rc.AvgFrameBandwidth : 0;
        // update_golden_frame_stats (no altref with lag 0)
        if (refreshGolden || _rc.IsSrcFrameAltRef != 0) _rc.FramesSinceGolden = 0;
        else if (showFrame) _rc.FramesSinceGolden++;
        if (frameType == KEY_FRAME)
        {
            _rc.FramesSinceKey = 0;
            _rc.FramesSinceSceneChange = 0;
        }
        if (refreshGolden) _rc.FrameNumLastGfRefresh = _frameNumber;
        if (_rc.FrameSourceSad < 10000) _rc.LastFrameLowSourceSad = _rc.FrameNumberEncoded;
        _rc.PrevCodedWidth = _cmWidth;
        _rc.PrevCodedHeight = _cmHeight;
        _rc.FrameNumberEncoded++;
    }
}
