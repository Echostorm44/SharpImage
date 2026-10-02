using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/ratectrl.c: the minq tables, q <-> qindex conversions, the rate model and the
// AOM_Q path of av1_rc_pick_q_and_bounds, av1_rc_postencode_update and the frame-rate / gf interval setup.
internal sealed partial class AomGqEncoder
{
    private const int KF_STD = 3, INTER_NORMAL = 0, GF_ARF_LOW = 1, GF_ARF_STD = 2;
    private const int REFBUF_RESET = 0, REFBUF_UPDATE = 1;
    private const int MIN_GF_INTERVAL = 4, MAX_GF_INTERVAL = 32, MAX_GF_LENGTH_LAP = 16, MAX_NUM_GF_INTERVALS = 15;
    private const int MAX_FIRSTPASS_ANALYSIS_FRAMES = 150, MAX_STATIC_GF_GROUP_LENGTH = 250;
    private const int FRAME_OVERHEAD_BITS = 200, MAX_MB_RATE = 250, MAXRATE_1080P = 2025000, BPER_MB_NORMBITS = 9;
    private const int STATIC_KF_GROUP_THRESH = 99, STATIC_MOTION_THRESH = 95;
    private const double STATIC_KF_GROUP_FLOAT_THRESH = 0.99;

    private static readonly int[] RateFactorLevels = { KF_STD, INTER_NORMAL, GF_ARF_STD, GF_ARF_STD, INTER_NORMAL, INTER_NORMAL, GF_ARF_LOW };

    // ---- minq tables (rc_init_minq_luts): [bitdepth idx][mode][res][qindex] ----
    private static readonly int[][,,] KfLowMotionMinq = new int[3][,,], KfHighMotionMinq = new int[3][,,], ArfgfLowMotionMinq = new int[3][,,],
        ArfgfHighMotionMinq = new int[3][,,], InterMinq = new int[3][,,];
    private static readonly int[][] RtcMinq = new int[3][];
    private static readonly double[,,] MinqX1 =
    {
        { { 0.1771, 0.379, 0.3279, 0.6634, 1.385 }, { 0.1917, 0.3760, 0.34570, 0.6916, 1.14820 } },
        { { 0.15, 0.45, 0.30, 0.55, 0.90 }, { 0.15, 0.45, 0.30, 0.55, 0.90 } },
    };

    static AomGqEncoder()
    {
        int[] bds = { 8, 10, 12 };
        for (int b = 0; b < 3; b++)
        {
            int bd = bds[b];
            KfLowMotionMinq[b] = new int[2, 2, QINDEX_RANGE]; KfHighMotionMinq[b] = new int[2, 2, QINDEX_RANGE];
            ArfgfLowMotionMinq[b] = new int[2, 2, QINDEX_RANGE]; ArfgfHighMotionMinq[b] = new int[2, 2, QINDEX_RANGE];
            InterMinq[b] = new int[2, 2, QINDEX_RANGE]; RtcMinq[b] = new int[QINDEX_RANGE];
            for (int mode = 0; mode < 2; mode++)
                for (int res = 0; res < 2; res++)
                    for (int i = 0; i < QINDEX_RANGE; i++)
                    {
                        double maxq = ConvertQindexToQ(i, bd);
                        KfLowMotionMinq[b][mode, res, i] = GetMinqIndex(maxq, 0.000001, -0.0004, MinqX1[mode, res, 0], bd);
                        KfHighMotionMinq[b][mode, res, i] = GetMinqIndex(maxq, 0.0000021, -0.00125, MinqX1[mode, res, 1], bd);
                        ArfgfLowMotionMinq[b][mode, res, i] = GetMinqIndex(maxq, 0.0000015, -0.0009, MinqX1[mode, res, 2], bd);
                        ArfgfHighMotionMinq[b][mode, res, i] = GetMinqIndex(maxq, 0.0000021, -0.00125, MinqX1[mode, res, 3], bd);
                        InterMinq[b][mode, res, i] = GetMinqIndex(maxq, 0.00000271, -0.00113, MinqX1[mode, res, 4], bd);
                        RtcMinq[b][i] = GetMinqIndex(maxq, 0.00000271, -0.00113, 0.70, bd);
                    }
        }
    }

    private static int BdIdx(int bd) => bd == 8 ? 0 : bd == 10 ? 1 : 2;

    private static int GetMinqIndex(double maxq, double x3, double x2, double x1, int bd)
    {
        double minqtarget = Math.Min(((x3 * maxq + x2) * maxq + x1) * maxq, maxq);
        if (minqtarget <= 2.0) return 0;
        return FindQindex(minqtarget, bd, 0, QINDEX_RANGE - 1);
    }

    /// <summary>av1_convert_qindex_to_q.</summary>
    internal static double ConvertQindexToQ(int qindex, int bd)
    {
        int ac = Av1Tables.DequantTable[BdIdx(bd), Math.Clamp(qindex, 0, 255), 1];
        return bd == 8 ? ac / 4.0 : bd == 10 ? ac / 16.0 : ac / 64.0;
    }

    /// <summary>av1_find_qindex.</summary>
    internal static int FindQindex(double desiredQ, int bd, int bestQindex, int worstQindex)
    {
        int low = bestQindex, high = worstQindex;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            double midQ = ConvertQindexToQ(mid, bd);
            if (midQ < desiredQ) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>av1_compute_qdelta.</summary>
    private int ComputeQdelta(double qstart, double qtarget)
    {
        int startIndex = FindQindex(qstart, _cfg.BitDepth, _rc.BestQuality, _rc.WorstQuality);
        int targetIndex = FindQindex(qtarget, _cfg.BitDepth, _rc.BestQuality, _rc.WorstQuality);
        return targetIndex - startIndex;
    }

    /// <summary>av1_rc_bits_per_mb (AOM_Q / VBR: no CBR adjustments).</summary>
    private int RcBitsPerMb(int frameType, int qindex, double correctionFactor)
    {
        double q = ConvertQindexToQ(qindex, _cfg.BitDepth);
        int enumerator = _isScreenContentType ? (frameType == KEY_FRAME ? 1000000 : 750000) : (frameType == KEY_FRAME ? 2000000 : 1500000);
        return (int)(enumerator * correctionFactor / q);
    }

    private int FindQindexByRate(int desiredBitsPerMb, int frameType, int bestQindex, int worstQindex)
    {
        int low = bestQindex, high = worstQindex;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            int midBits = RcBitsPerMb(frameType, mid, 1.0);
            if (midBits > desiredBitsPerMb) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>av1_compute_qdelta_by_rate.</summary>
    private int ComputeQdeltaByRate(int frameType, int qindex, double rateTargetRatio)
    {
        int baseBits = RcBitsPerMb(frameType, qindex, 1.0);
        int targetBits = (int)(rateTargetRatio * baseBits);
        int targetIndex = FindQindexByRate(targetBits, frameType, _rc.BestQuality, _rc.WorstQuality);
        return targetIndex - qindex;
    }

    private static int GetActiveQuality(int q, int gfuBoost, int low, int high, int[,,] lowMotion, int[,,] highMotion, int mode, int res)
    {
        if (gfuBoost > high) return lowMotion[mode, res, q];
        if (gfuBoost < low) return highMotion[mode, res, q];
        int gap = high - low;
        int offset = high - gfuBoost;
        int qdiff = highMotion[mode, res, q] - lowMotion[mode, res, q];
        int adjustment = (offset * qdiff + (gap >> 1)) / gap;
        return lowMotion[mode, res, q] + adjustment;
    }

    private static readonly int[] GfboostThresh = { 4000, 4000, 3000 };

    private int GetKfActiveQuality(int q, int resIdx, bool rtcMode)
    {
        int b = BdIdx(_cfg.BitDepth), r = resIdx > 1 ? 1 : 0, m = rtcMode ? 1 : 0;
        return GetActiveQuality(q, _pRc.KfBoost, rtcMode ? 400 : 553, rtcMode ? 5000 : 8000, KfLowMotionMinq[b], KfHighMotionMinq[b], m, r);
    }

    private int GetGfActiveQuality(int q, int resIdx, bool rtcMode)
    {
        int b = BdIdx(_cfg.BitDepth), r = resIdx > 1 ? 1 : 0, m = rtcMode ? 1 : 0;
        int low, high;
        if (!rtcMode)
        {
            low = _pRc.GfuBoostAverage < GfboostThresh[resIdx] ? 562 : 100;
            high = _pRc.GfuBoostAverage < GfboostThresh[resIdx] ? 2875 : 4994;
        }
        else { low = 300; high = 2400; }
        return GetActiveQuality(q, _pRc.GfuBoost, low, high, ArfgfLowMotionMinq[b], ArfgfHighMotionMinq[b], m, r);
    }

    private int GetGfHighMotionQuality(int q, int resIdx, bool rtcMode)
        => ArfgfHighMotionMinq[BdIdx(_cfg.BitDepth)][rtcMode ? 1 : 0, resIdx > 1 ? 1 : 0, q];

    /// <summary>get_intra_q_and_bounds.</summary>
    private void GetIntraQAndBounds(int width, int height, ref int activeBest, ref int activeWorst, int cqLevel)
    {
        int bd = _cfg.BitDepth;
        int resIdx = (Math.Min(_cmWidth, _cmHeight) >= 480 ? 1 : 0) + (Math.Min(_cmWidth, _cmHeight) >= 608 ? 1 : 0);
        bool rtcMode = _cfg.Usage == REALTIME;
        int activeBestQuality;
        int activeWorstQuality = activeWorst;
        if (_rc.FramesToKey <= 1 && _rcModeQ)
        {
            activeBestQuality = cqLevel;
            activeWorstQuality = cqLevel;
        }
        else if (_pRc.ThisKeyFrameForced != 0)
        {
            int lastBoostedQindex = _pRc.LastBoostedQindex;
            int qindex, deltaQindex;
            double lastBoostedQ;
            if (IsStatConsumptionStageTwopass && _twopass.LastKfgroupZeromotionPct >= STATIC_MOTION_THRESH)
            {
                qindex = Math.Min(_pRc.LastKfQindex, lastBoostedQindex);
                activeBestQuality = qindex;
                lastBoostedQ = ConvertQindexToQ(qindex, bd);
                deltaQindex = ComputeQdelta(lastBoostedQ, lastBoostedQ * 1.25);
                activeWorstQuality = Math.Min(qindex + deltaQindex, activeWorstQuality);
            }
            else
            {
                qindex = lastBoostedQindex;
                lastBoostedQ = ConvertQindexToQ(qindex, bd);
                deltaQindex = ComputeQdelta(lastBoostedQ, lastBoostedQ * 0.50);
                activeBestQuality = Math.Max(qindex + deltaQindex, _rc.BestQuality);
            }
        }
        else
        {
            double qAdjFactor = 1.0;
            activeBestQuality = GetKfActiveQuality(activeWorstQuality, resIdx, rtcMode);
            if (_isScreenContentType) activeBestQuality /= 2;
            if (IsStatConsumptionStageTwopass && _twopass.KfZeromotionPct >= STATIC_KF_GROUP_THRESH) activeBestQuality /= 3;
            if (width * height <= 352 * 288) qAdjFactor -= 0.25;
            if (IsStatConsumptionStageTwopass) qAdjFactor += 0.05 - (0.001 * (double)_twopass.KfZeromotionPct);
            double qVal = ConvertQindexToQ(activeBestQuality, bd);
            activeBestQuality += ComputeQdelta(qVal, qVal * qAdjFactor);
        }
        activeBest = activeBestQuality;
        activeWorst = activeWorstQuality;
    }

    /// <summary>get_active_best_quality (inter frames).</summary>
    private int GetActiveBestQuality(int activeWorstQuality, int cqLevel, int gfIndex)
    {
        int bd = _cfg.BitDepth;
        int updateType = _gfGroup.UpdateType[gfIndex];
        bool isIntrlArfBoost = updateType == INTNL_ARF_UPDATE;
        bool isLeafFrame = !(updateType == ARF_UPDATE || updateType == GF_UPDATE || isIntrlArfBoost);
        int resIdx = (Math.Min(_cmWidth, _cmHeight) >= 480 ? 1 : 0) + (Math.Min(_cmWidth, _cmHeight) >= 608 ? 1 : 0);
        bool rtcMode = _cfg.Usage == REALTIME;
        bool isOverlayFrame = updateType == OVERLAY_UPDATE || updateType == INTNL_OVERLAY_UPDATE;
        if (isLeafFrame || isOverlayFrame)
        {
            if (_rcModeQ) return cqLevel;
            return InterMinq[BdIdx(bd)][rtcMode ? 1 : 0, resIdx > 1 ? 1 : 0, activeWorstQuality];
        }
        int q = activeWorstQuality;
        if (_rc.FramesSinceKey > 1 && _pRc.AvgFrameQindex[INTER_FRAME] < activeWorstQuality) q = _pRc.AvgFrameQindex[INTER_FRAME];
        int activeBestQuality = GetGfActiveQuality(q, resIdx, rtcMode);
        int minBoost = GetGfHighMotionQuality(q, resIdx, rtcMode);
        int boost = minBoost - activeBestQuality;
        activeBestQuality = minBoost - (int)(boost * _pRc.ArfBoostFactor);
        if (!isIntrlArfBoost) return activeBestQuality;
        if (_rcModeQ) activeBestQuality = _pRc.ArfQ;
        int thisHeight = _gfGroup.LayerDepth[gfIndex];
        while (thisHeight > 1)
        {
            activeBestQuality = (activeBestQuality + activeWorstQuality + 1) / 2;
            --thisHeight;
        }
        return activeBestQuality;
    }

    /// <summary>av1_rc_pick_q_and_bounds (AOM_Q: rc_pick_q_and_bounds_q_mode).</summary>
    private int RcPickQAndBounds(int width, int height, int gfIndex, bool frameIsIntraOnly, out int bottomIndex, out int topIndex)
    {
        if (!_rcModeQ) throw new NotImplementedException("non-AOM_Q rate control");
        int cqLevel = _cqLevel;   // get_active_cq_level: no superres
        int activeBestQuality;
        int activeWorstQuality = _rc.ActiveWorstQuality;
        if (frameIsIntraOnly)
        {
            activeBestQuality = 0;
            GetIntraQAndBounds(width, height, ref activeBestQuality, ref activeWorstQuality, cqLevel);
        }
        else activeBestQuality = GetActiveBestQuality(activeWorstQuality, cqLevel, gfIndex);
        if (cqLevel > 0) activeBestQuality = Math.Max(1, activeBestQuality);
        topIndex = Math.Clamp(activeWorstQuality, _rc.BestQuality, _rc.WorstQuality);
        bottomIndex = Math.Clamp(activeBestQuality, _rc.BestQuality, _rc.WorstQuality);
        int q = bottomIndex;
        if (_gfGroup.UpdateType[gfIndex] == ARF_UPDATE) _pRc.ArfQ = q;
        return q;
    }

    /// <summary>av1_rc_set_frame_target.</summary>
    private void RcSetFrameTarget(int target, int width, int height)
    {
        _rc.ThisFrameTarget = target;
        if ((width != _cfg.Width || height != _cfg.Height) && true)
            _rc.ThisFrameTarget = SaturateToInt(_rc.ThisFrameTarget * ((double)(_cfg.Width * _cfg.Height) / (width * height)));
        long sb64 = ((long)_rc.ThisFrameTarget << 12) / (width * height);
        _rc.Sb64TargetRate = (int)Math.Min(sb64, int.MaxValue);
    }

    private static int SaturateToInt(double d) => d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)d;

    /// <summary>av1_rc_get_default_min_gf_interval.</summary>
    private static int DefaultMinGfInterval(int width, int height, double framerate)
    {
        const double factorSafe = 3840 * 2160 * 20.0;
        double factor = (double)width * height * framerate;
        int defaultInterval = Math.Clamp((int)(framerate * 0.125), MIN_GF_INTERVAL, MAX_GF_INTERVAL);
        if (factor <= factorSafe) return defaultInterval;
        return Math.Max(defaultInterval, (int)(MIN_GF_INTERVAL * factor / factorSafe + 0.5));
    }

    private static int DefaultMaxGfInterval(double framerate, int minGfInterval)
    {
        int interval = Math.Min(MAX_GF_INTERVAL, (int)(framerate * 0.75));
        interval += interval & 1;
        interval = Math.Max(MAX_GF_INTERVAL, interval);
        return Math.Max(interval, minGfInterval);
    }

    /// <summary>av1_primary_rc_init + av1_rc_init (target_bandwidth 0 in AOM_Q: libavif leaves rc_target_bitrate at
    /// libaom's default).</summary>
    private void RcInit()
    {
        int worst = _rc.WorstQuality, best = _rc.BestQuality;
        int minGf = DefaultMinGfInterval(_cfg.Width, _cfg.Height, _initFramerate);
        int maxGf = DefaultMaxGfInterval(_initFramerate, minGf);
        _pRc.BaselineGfInterval = (minGf + maxGf) / 2;
        _pRc.ThisKeyFrameForced = 0;
        _pRc.NextKeyFrameForced = 0;
        _pRc.NiFrames = 0;
        _pRc.TotQ = 0.0;
        _pRc.TotalActualBits = 0;
        _pRc.TotalTargetBits = 0;
        _pRc.BufferLevel = _pRc.StartingBufferLevel;
        _pRc.AvgFrameQindex[KEY_FRAME] = (worst + best) / 2;
        _pRc.AvgFrameQindex[INTER_FRAME] = (worst + best) / 2;
        _pRc.AvgQ = ConvertQindexToQ(worst, _cfg.BitDepth);
        _pRc.LastQ[KEY_FRAME] = best;
        _pRc.LastQ[INTER_FRAME] = worst;
        _pRc.BitsOffTarget = _pRc.StartingBufferLevel;
        double bitsPerFrame = _targetBandwidth / _initFramerate;
        _pRc.RollingTargetBits = Math.Max(1, bitsPerFrame > int.MaxValue ? int.MaxValue : (int)bitsPerFrame);
        _pRc.RollingActualBits = _pRc.RollingTargetBits;

        _rc.FramesSinceKey = 8;
        _rc.FramesToFwdKf = -1;   // fwd_kf_dist (default -1)
        _rc.FramesTillGfUpdateDue = 0;
        _rc.NiAvQi = worst;
        _rc.NiTotQi = 0;
        _rc.MinGfInterval = minGf;
        _rc.MaxGfInterval = maxGf;
        _rc.FramesSinceSceneChange = 0;
        _rc.LastFrameLowSourceSad = 0;
    }

    /// <summary>av1_new_framerate + av1_rc_update_framerate + set_gf_interval_range.</summary>
    private void NewFramerate(double framerate)
    {
        _framerate = framerate < 0.1 ? 30 : framerate;
        int mbs = (((_cmWidth + 7) >> 3 << 1) + 2 >> 2) * (((_cmHeight + 7) >> 3 << 1) + 2 >> 2);
        _rc.AvgFrameBandwidth = SaturateToInt(Math.Round(_targetBandwidth / _framerate));
        long vbrMinBits = Math.Min((long)_rc.AvgFrameBandwidth * 0 / 100, int.MaxValue);   // vbrmin_section 0
        _rc.MinFrameBandwidth = Math.Max((int)vbrMinBits, FRAME_OVERHEAD_BITS);
        long vbrMaxBits = Math.Min((long)_rc.AvgFrameBandwidth * 2000 / 100, int.MaxValue);   // vbrmax_section 2000
        _rc.MaxFrameBandwidth = Math.Max(Math.Max(mbs * MAX_MB_RATE, MAXRATE_1080P), (int)vbrMaxBits);
        // set_gf_interval_range
        _rc.MinGfInterval = DefaultMinGfInterval(_cfg.Width, _cfg.Height, _framerate);
        _rc.MaxGfInterval = DefaultMaxGfInterval(_framerate, _rc.MinGfInterval);
        _rc.StaticSceneMaxGfInterval = _lapEnabled ? _rc.MaxGfInterval + 1 : MAX_STATIC_GF_GROUP_LENGTH;
        if (_rc.MaxGfInterval > _rc.StaticSceneMaxGfInterval) _rc.MaxGfInterval = _rc.StaticSceneMaxGfInterval;
        _rc.MinGfInterval = Math.Min(_rc.MinGfInterval, _rc.MaxGfInterval);
    }

    /// <summary>av1_rc_postencode_update (AOM_Q: the rate correction factors and the buffer model do not feed back
    /// into the q decision).</summary>
    private void RcPostencodeUpdate(int qindex, int frameType, bool showFrame, long bytesUsed, bool refreshGolden, bool refreshAltRef)
    {
        bool isIntrnlArf = _gfGroup.UpdateType[_gfFrameIndex] == INTNL_ARF_UPDATE;
        _rc.ProjectedFrameSize = (int)(bytesUsed << 3);
        if (frameType == KEY_FRAME)
        {
            _pRc.LastQ[KEY_FRAME] = qindex;
            _pRc.AvgFrameQindex[KEY_FRAME] = (3 * _pRc.AvgFrameQindex[KEY_FRAME] + qindex + 2) >> 2;
            _rc.LastEncodedSizeKeyframe = _rc.ProjectedFrameSize;
            _rc.LastTargetSizeKeyframe = _rc.ThisFrameTarget;
        }
        else if (_rc.IsSrcFrameAltRef == 0 && !(refreshGolden || isIntrnlArf || refreshAltRef))
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
            (_pRc.ConstrainedGfGroup == 0 && (refreshAltRef || isIntrnlArf || (refreshGolden && _rc.IsSrcFrameAltRef == 0))))
            _pRc.LastBoostedQindex = qindex;
        if (frameType == KEY_FRAME) _pRc.LastKfQindex = qindex;
        _rc.PrevAvgFrameBandwidth = _rc.AvgFrameBandwidth;
        if (_cmWidth != _cfg.Width || _cmHeight != _cfg.Height)
            _rc.ThisFrameTarget = SaturateToInt(_rc.ThisFrameTarget / ((double)(_cfg.Width * _cfg.Height) / (_cmWidth * _cmHeight)));
        if (frameType != KEY_FRAME)
        {
            _pRc.RollingTargetBits = (int)(((long)_pRc.RollingTargetBits * 3 + _rc.ThisFrameTarget + 2) >> 2);
            _pRc.RollingActualBits = (int)(((long)_pRc.RollingActualBits * 3 + _rc.ProjectedFrameSize + 2) >> 2);
        }
        _pRc.TotalActualBits += _rc.ProjectedFrameSize;
        _pRc.TotalTargetBits += showFrame ? _rc.AvgFrameBandwidth : 0;
        if (IsAltrefEnabled && refreshAltRef && frameType != KEY_FRAME) _rc.FramesSinceGolden = 0;
        else if (refreshGolden || _rc.IsSrcFrameAltRef != 0) _rc.FramesSinceGolden = 0;
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

    private bool IsAltrefEnabled => _cfg.LagInFrames >= 3;   // is_altref_enabled(lag, enable_auto_arf = 1)

    /// <summary>av1_calc_pframe_target_size_one_pass_vbr (af_ratio 10).</summary>
    private int CalcPframeTargetSizeOnePassVbr(int frameUpdateType)
    {
        const int afRatio = 10;
        long target;
        if (frameUpdateType == KF_UPDATE || frameUpdateType == GF_UPDATE || frameUpdateType == ARF_UPDATE)
            target = (long)_rc.AvgFrameBandwidth * _pRc.BaselineGfInterval * afRatio / (_pRc.BaselineGfInterval + afRatio - 1);
        else
            target = (long)_rc.AvgFrameBandwidth * _pRc.BaselineGfInterval / (_pRc.BaselineGfInterval + afRatio - 1);
        return ClampPframeTargetSize(target, frameUpdateType);
    }

    private int CalcIframeTargetSizeOnePassVbr() => ClampIframeTargetSize((long)_rc.AvgFrameBandwidth * 25);

    private int ClampPframeTargetSize(long target, int frameUpdateType)
    {
        int minFrameTarget = Math.Max(_rc.MinFrameBandwidth, _rc.AvgFrameBandwidth >> 5);
        if (frameUpdateType == OVERLAY_UPDATE || frameUpdateType == INTNL_OVERLAY_UPDATE) target = minFrameTarget;
        else if (target < minFrameTarget) target = minFrameTarget;
        if (target > _rc.MaxFrameBandwidth) target = _rc.MaxFrameBandwidth;
        return (int)target;   // max_inter_bitrate_pct 0
    }

    private int ClampIframeTargetSize(long target)
    {
        if (target > _rc.MaxFrameBandwidth) target = _rc.MaxFrameBandwidth;   // max_intra_bitrate_pct 0
        return (int)target;
    }
}
