using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/pass2_strategy.c for one-pass encoding without stats (lag 0) and with look-ahead
// processing stats (LAP): key frame / GF group placement, boosts and bit allocation. The stats pointers
// (stats_in_start / stats_in_end / twopass_frame.stats_in) are indices into _twopass.Buf.
internal sealed partial class AomGqEncoder
{
    private const int DEFAULT_KF_BOOST = 2300, DEFAULT_GF_BOOST = 2000;
    private const double MIN_ZERO_MOTION = 0.95, MAX_SR_CODED_ERROR = 40, MAX_RAW_ERR_VAR = 2000;
    private const int NORMAL_BOOST = 100;
    private const int STABLE_REGION = 0, HIGH_VAR_REGION = 1, SCENECUT_REGION = 2, BLENDING_REGION = 3;
    private const int DISABLE_SCENECUT = 0, ENABLE_SCENECUT_MODE_1 = 1, ENABLE_SCENECUT_MODE_2 = 2;
    private const int SCENE_CUT_KEY_TEST_INTERVAL = 16;
    private const double MAX_GFUBOOST_FACTOR = 10.0;

    private static double DoubleDivideCheck(double x) => x < 0 ? x - 0.000001 : x + 0.000001;
    private static double Fclamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private ref AomFpStats Stat(int idx) => ref _twopass.Buf[idx];

    private int FrameInfoMbRows => ((((_cfg.Height + 7) & ~7) >> 2) + 2) >> 2;

    private double CalculateActiveArea(in AomFpStats f)
    {
        double activePct = 1.0 - ((f.IntraSkipPct / 2) + ((f.InactiveZoneRows * 2) / (double)FrameInfoMbRows));
        return Fclamp(activePct, 0.5, 1.0);
    }

    private double CalculateModifiedErrNew(in AomFpStats totalStats, in AomFpStats thisStats, int vbrbias, double modifiedErrorMin,
        double modifiedErrorMax)
    {
        double avWeight = totalStats.Weight / totalStats.Count;
        double avErr = (totalStats.CodedError * avWeight) / totalStats.Count;
        double modifiedError = avErr * Math.Pow(thisStats.CodedError * thisStats.Weight / DoubleDivideCheck(avErr), vbrbias / 100.0);
        modifiedError *= Math.Pow(CalculateActiveArea(thisStats), 0.5);
        return Fclamp(modifiedError, modifiedErrorMin, modifiedErrorMax);
    }

    private const int VbrBias = 50;   // rc_2pass_vbr_bias_pct default

    private double CalculateModifiedErr(in AomFpStats thisFrame)
        => CalculateModifiedErrNew(_twopass.TotalStats, thisFrame, VbrBias, _twopass.ModifiedErrorMin, _twopass.ModifiedErrorMax);

    /// <summary>input_stats: copies the next stats and advances stats_in (false at the end).</summary>
    private bool InputStats(out AomFpStats fps)
    {
        if (_twopass.StatsIn >= _twopass.InEnd) { fps = default; return false; }
        fps = _twopass.Buf[_twopass.StatsIn];
        ++_twopass.StatsIn;
        return true;
    }

    /// <summary>input_stats_lap.</summary>
    private bool InputStatsLap(out AomFpStats fps)
    {
        if (_twopass.StatsIn >= _twopass.InEnd) { fps = default; return false; }
        fps = _twopass.Buf[_twopass.StatsIn];
        int n = _twopass.InEnd - _twopass.StatsIn - 1;
        Array.Copy(_twopass.Buf, 1, _twopass.Buf, 0, n);   // memmove(frame_stats_arr[0], frame_stats_arr[1], n)
        _twopass.InEnd--;
        return true;
    }

    /// <summary>read_frame_stats: the index of stats_in + offset, or -1.</summary>
    private int ReadFrameStats(int offset)
    {
        int p = _twopass.StatsIn + offset;
        if ((offset >= 0 && p >= _twopass.InEnd) || (offset < 0 && p < _twopass.InStart)) return -1;
        return p;
    }

    private int FrameMaxBits()
    {
        long maxBits = (long)_rc.AvgFrameBandwidth * 2000 / 100;   // vbrmax_section
        if (maxBits < 0) maxBits = 0;
        else if (maxBits > _rc.MaxFrameBandwidth) maxBits = _rc.MaxFrameBandwidth;
        return (int)maxBits;
    }

    private const double INTRA_PART = 0.005, DEFAULT_DECAY_LIMIT = 0.75, LOW_SR_DIFF_TRHESH = 0.01, NCOUNT_FRAME_II_THRESH = 5.0,
        LOW_CODED_ERR_PER_MB = 0.01;

    private static double GetSrDecayRate(in AomFpStats frame)
    {
        double srDiff = frame.SrCodedError - frame.CodedError;
        double srDecay = 1.0;
        double modifiedPctInter = frame.PcntInter;
        if (frame.CodedError > LOW_CODED_ERR_PER_MB && (frame.IntraError / DoubleDivideCheck(frame.CodedError)) < NCOUNT_FRAME_II_THRESH)
            modifiedPctInter = frame.PcntInter - frame.PcntNeutral;
        double modifiedPcntIntra = 100 * (1.0 - modifiedPctInter);
        if (srDiff > LOW_SR_DIFF_TRHESH)
        {
            double srDiffPart = (srDiff * 0.25) / frame.IntraError;
            srDecay = 1.0 - srDiffPart - (INTRA_PART * modifiedPcntIntra);
        }
        return Math.Max(srDecay, DEFAULT_DECAY_LIMIT);
    }

    private static double GetZeroMotionFactor(in AomFpStats frame)
    {
        double zeroMotionPct = frame.PcntInter - frame.PcntMotion;
        double srDecay = GetSrDecayRate(frame);
        return Math.Min(srDecay, zeroMotionPct);
    }

    private static double GetPredictionDecayRate(in AomFpStats f)
    {
        double srDecayRate = GetSrDecayRate(f);
        double zeroMotionFactor = 0.5 * (f.PcntInter - f.PcntMotion);
        if (zeroMotionFactor > 1.0) zeroMotionFactor = 1.0;
        else if (zeroMotionFactor < 0.0) zeroMotionFactor = 0.0;
        return Math.Max(zeroMotionFactor, srDecayRate + ((1.0 - srDecayRate) * zeroMotionFactor));
    }

    private bool DetectTransitionToStill(int nextStatsIndex, int minGfInterval, int frameInterval, int stillInterval, double loopDecayRate,
        double lastDecayRate)
    {
        var fi = _twopass.FirstpassInfo;
        if (frameInterval > minGfInterval && loopDecayRate >= 0.999 && lastDecayRate < 0.9)
        {
            int statsLeft = fi.FutureCount(nextStatsIndex);
            if (statsLeft >= stillInterval)
            {
                int j;
                for (j = 0; j < stillInterval; ++j)
                {
                    ref var s = ref fi.StatsBuf[fi.Peek(nextStatsIndex + j)];
                    if (s.PcntInter - s.PcntMotion < 0.999) break;
                }
                return j == stillInterval;
            }
        }
        return false;
    }

    private bool DetectFlash(int offset)
    {
        int n = ReadFrameStats(offset);
        if (n < 0) return false;
        ref var next = ref _twopass.Buf[n];
        return next.PcntSecondRef > next.PcntInter && next.PcntSecondRef >= 0.5;
    }

    private static void AccumulateFrameMotionStats(in AomFpStats stats, ref AomGfGroupStats g, double fW, double fH)
    {
        double pct = stats.PcntMotion;
        g.ThisFrameMvInOut = stats.MvInOutCount * pct;
        g.MvInOutAccumulator += g.ThisFrameMvInOut;
        g.AbsMvInOutAccumulator += Math.Abs(g.ThisFrameMvInOut);
        if (pct > 0.05)
        {
            double mvrRatio = Math.Abs(stats.MvrAbs) / DoubleDivideCheck(Math.Abs(stats.MVr));
            double mvcRatio = Math.Abs(stats.MvcAbs) / DoubleDivideCheck(Math.Abs(stats.MVc));
            g.MvRatioAccumulator += pct * (mvrRatio < stats.MvrAbs * fH ? mvrRatio : stats.MvrAbs * fH);
            g.MvRatioAccumulator += pct * (mvcRatio < stats.MvcAbs * fW ? mvcRatio : stats.MvcAbs * fW);
        }
    }

    private static void AccumulateThisFrameStats(in AomFpStats stats, double modFrameErr, ref AomGfGroupStats g)
    {
        g.GfGroupErr += modFrameErr;
        g.GfGroupRawError += stats.CodedError;
        g.GfGroupSkipPct += stats.IntraSkipPct;
        g.GfGroupInactiveZoneRows += stats.InactiveZoneRows;
    }

    private static void AccumulateNextFrameStats(in AomFpStats stats, bool flashDetected, int framesSinceKey, int curIdx, ref AomGfGroupStats g,
        int fW, int fH)
    {
        AccumulateFrameMotionStats(stats, ref g, fW, fH);
        g.AvgSrCodedError += stats.SrCodedError;
        g.AvgPcntSecondRef += stats.PcntSecondRef;
        g.AvgNewMvCount += stats.NewMvCount;
        g.AvgWaveletEnergy += stats.FrameAvgWaveletEnergy;
        if (Math.Abs(stats.RawErrorStdev) > 0.000001)
        {
            g.NonZeroStdevCount++;
            g.AvgRawErrStdev += stats.RawErrorStdev;
        }
        if (!flashDetected)
        {
            g.LastLoopDecayRate = g.LoopDecayRate;
            g.LoopDecayRate = GetPredictionDecayRate(stats);
            g.DecayAccumulator = g.DecayAccumulator * g.LoopDecayRate;
            if ((framesSinceKey + curIdx - 1) > 1)
                g.ZeroMotionAccumulator = Math.Min(g.ZeroMotionAccumulator, GetZeroMotionFactor(stats));
        }
    }

    private static void AverageGfStats(int totalFrame, ref AomGfGroupStats g)
    {
        if (totalFrame != 0)
        {
            g.AvgSrCodedError /= totalFrame;
            g.AvgPcntSecondRef /= totalFrame;
            g.AvgNewMvCount /= totalFrame;
            g.AvgWaveletEnergy /= totalFrame;
        }
        if (g.NonZeroStdevCount != 0) g.AvgRawErrStdev /= g.NonZeroStdevCount;
    }

    private double BaselineErrPerMb() => (uint)(_cfg.Height * _cfg.Width) <= 640 * 360 ? 500.0 : 1000.0;

    private double CalcFrameBoost(in AomFpStats thisFrame, double thisFrameMvInOut, double maxBoost, bool scaleMaxBoost)
    {
        double lq = ConvertQindexToQ(_pRc.AvgFrameQindex[INTER_FRAME], _cfg.BitDepth);
        double boostQCorrection = Math.Min(0.5 + (lq * 0.015), 1.5);
        double activeArea = CalculateActiveArea(thisFrame);
        double frameBoost = Math.Max(BaselineErrPerMb() * activeArea, thisFrame.IntraError * activeArea) / DoubleDivideCheck(thisFrame.CodedError);
        frameBoost = frameBoost * 12.5 * boostQCorrection;
        if (thisFrameMvInOut > 0.0)
        {
            frameBoost += frameBoost * (thisFrameMvInOut * 2.0);
            if (scaleMaxBoost) maxBoost += maxBoost * (thisFrameMvInOut * 2.0);
        }
        else frameBoost += frameBoost * (thisFrameMvInOut / 2.0);
        return Math.Min(frameBoost, maxBoost * boostQCorrection);
    }

    private double CalcKfFrameBoost(in AomFpStats thisFrame, ref double srAccumulator, double maxBoost)
    {
        double lq = ConvertQindexToQ(_pRc.AvgFrameQindex[INTER_FRAME], _cfg.BitDepth);
        double boostQCorrection = Math.Min(0.50 + (lq * 0.015), 2.00);
        double activeArea = CalculateActiveArea(thisFrame);
        double frameBoost = Math.Max(BaselineErrPerMb() * activeArea, thisFrame.IntraError * activeArea) /
            DoubleDivideCheck((thisFrame.CodedError + srAccumulator) * activeArea);
        srAccumulator += thisFrame.SrCodedError - thisFrame.CodedError;
        srAccumulator = Math.Max(0.0, srAccumulator);
        frameBoost = (frameBoost + 40.0) * boostQCorrection;
        return Math.Min(frameBoost, maxBoost * boostQCorrection);
    }

    private static double GfuBoostProjectionFactor(double minFactor, double maxFactor, int frameCount)
    {
        double factor = Math.Sqrt(frameCount);
        factor = Math.Min(factor, maxFactor);
        factor = Math.Max(factor, minFactor);
        return 200.0 + 10.0 * factor;
    }

    private static double KfBoostProjectionFactor(int frameCount)
    {
        double factor = Math.Sqrt(frameCount);
        factor = Math.Min(factor, 10.0);
        factor = Math.Max(factor, 4.0);
        return 75.0 + 14.0 * factor;
    }

    private int GetProjectedGfuBoost(int gfuBoost, int framesToProject, int numStatsUsedForGfuBoost)
    {
        if (numStatsUsedForGfuBoost >= framesToProject) return gfuBoost;
        double minBoostFactor = Math.Sqrt(_pRc.BaselineGfInterval);
        double tplFactor = GfuBoostProjectionFactor(minBoostFactor, MAX_GFUBOOST_FACTOR, framesToProject);
        double tplFactorNumStats = GfuBoostProjectionFactor(minBoostFactor, MAX_GFUBOOST_FACTOR, numStatsUsedForGfuBoost);
        return (int)Math.Round((tplFactor * gfuBoost) / tplFactorNumStats, MidpointRounding.ToEven);   // rint
    }

    /// <summary>av1_calc_arf_boost.</summary>
    private int CalcArfBoost(int offset, int fFrames, int bFrames, ref int numFpstatsUsed, ref int numFpstatsRequired, bool trackUsed,
        bool projectGfuBoost, bool scaleMaxBoost)
    {
        var g = InitGfStats();
        double boostScore = NORMAL_BOOST;
        if (trackUsed) numFpstatsUsed = 0;
        int i;
        for (i = 0; i < fFrames; ++i)
        {
            int t = ReadFrameStats(i + offset);
            if (t < 0) break;
            ref var thisFrame = ref _twopass.Buf[t];
            AccumulateFrameMotionStats(thisFrame, ref g, _cfg.Width, _cfg.Height);
            bool flashDetected = DetectFlash(i + offset) || DetectFlash(i + offset + 1);
            if (!flashDetected)
            {
                g.DecayAccumulator *= GetPredictionDecayRate(thisFrame);
                g.DecayAccumulator = g.DecayAccumulator < 0.01 ? 0.01 : g.DecayAccumulator;
            }
            boostScore += g.DecayAccumulator * CalcFrameBoost(thisFrame, g.ThisFrameMvInOut, 90.0, scaleMaxBoost);
            if (trackUsed) numFpstatsUsed++;
        }
        int arfBoost = (int)boostScore;
        boostScore = 0.0;
        g = InitGfStats();
        for (i = -1; i >= -bFrames; --i)
        {
            int t = ReadFrameStats(i + offset);
            if (t < 0) break;
            ref var thisFrame = ref _twopass.Buf[t];
            AccumulateFrameMotionStats(thisFrame, ref g, _cfg.Width, _cfg.Height);
            bool flashDetected = DetectFlash(i + offset) || DetectFlash(i + offset + 1);
            if (!flashDetected)
            {
                g.DecayAccumulator *= GetPredictionDecayRate(thisFrame);
                g.DecayAccumulator = g.DecayAccumulator < 0.01 ? 0.01 : g.DecayAccumulator;
            }
            boostScore += g.DecayAccumulator * CalcFrameBoost(thisFrame, g.ThisFrameMvInOut, 90.0, scaleMaxBoost);
            if (trackUsed) numFpstatsUsed++;
        }
        arfBoost += (int)boostScore;
        if (projectGfuBoost)
        {
            numFpstatsRequired = fFrames + bFrames;
            arfBoost = GetProjectedGfuBoost(arfBoost, numFpstatsRequired, numFpstatsUsed);
        }
        if (arfBoost < ((bFrames + fFrames) * 50)) arfBoost = (bFrames + fFrames) * 50;
        return arfBoost;
    }

    private int CalcArfBoost(int offset, int fFrames, int bFrames, bool scaleMaxBoost)
    {
        int u = 0, r = 0;
        return CalcArfBoost(offset, fFrames, bFrames, ref u, ref r, false, false, scaleMaxBoost);
    }

    private int CalculateSectionIntraRatio(int begin, int end, int sectionLength)
    {
        int s = begin;
        double intraError = 0.0, codedError = 0.0;
        int i = 0;
        while (s < end && i < sectionLength)
        {
            intraError += _twopass.Buf[s].IntraError;
            codedError += _twopass.Buf[s].CodedError;
            ++s;
            ++i;
        }
        return (int)(intraError / DoubleDivideCheck(codedError));
    }

    private long CalculateTotalGfGroupBits(double gfGroupErr)
    {
        int maxBits = FrameMaxBits();
        long totalGroupBits;
        if (_twopass.KfGroupBits > 0 && _twopass.KfGroupErrorLeft > 0)
            totalGroupBits = (long)(_twopass.KfGroupBits * (gfGroupErr / _twopass.KfGroupErrorLeft));
        else totalGroupBits = 0;
        totalGroupBits = totalGroupBits < 0 ? 0 : totalGroupBits > _twopass.KfGroupBits ? _twopass.KfGroupBits : totalGroupBits;
        if (totalGroupBits > (long)maxBits * _pRc.BaselineGfInterval) totalGroupBits = (long)maxBits * _pRc.BaselineGfInterval;
        return totalGroupBits;
    }

    private static int CalculateBoostBits(int frameCount, int boost, long totalGroupBits)
    {
        if (boost == 0 || totalGroupBits <= 0) return 0;
        if (frameCount <= 0) return (int)Math.Min(totalGroupBits, int.MaxValue);
        int allocationChunks = (frameCount * 100) + boost;
        if (boost > 1023)
        {
            int divisor = boost >> 10;
            boost /= divisor;
            allocationChunks /= divisor;
        }
        return Math.Max((int)(((long)boost * totalGroupBits) / allocationChunks), 0);
    }

    private static readonly double[] LayerFraction = { 1.0, 0.70, 0.55, 0.60, 0.60, 1.0, 1.0 };

    private void AllocateGfGroupBits(long gfGroupBits, int gfArfBits, bool keyFrame, bool useArf)
    {
        var gf = _gfGroup;
        long totalGroupBits = gfGroupBits;
        int gfGroupSize = gf.Size;
        var layerFrames = new int[MAX_ARF_LAYERS + 1];
        int frameIndex = keyFrame ? 1 : 0;
        if (useArf) totalGroupBits -= gfArfBits;
        int numFrames = Math.Max(1, _pRc.BaselineGfInterval - (_rc.FramesSinceKey == 0 ? 1 : 0));
        int baseFrameBits = (int)(totalGroupBits / numFrames);
        int maxArfLayer = gf.MaxLayerDepth - 1;
        for (int idx = frameIndex; idx < gfGroupSize; ++idx)
            if (gf.UpdateType[idx] == ARF_UPDATE || gf.UpdateType[idx] == INTNL_ARF_UPDATE) layerFrames[gf.LayerDepth[idx]]++;
        var layerExtraBits = new int[MAX_ARF_LAYERS + 1];
        for (int i = 1; i <= maxArfLayer; ++i)
        {
            double fraction = i == maxArfLayer ? 1.0 : LayerFraction[i];
            layerExtraBits[i] = (int)((gfArfBits * fraction) / Math.Max(1, layerFrames[i]));
            gfArfBits -= (int)(gfArfBits * fraction);
        }
        for (int idx = frameIndex; idx < gfGroupSize; ++idx)
        {
            switch (gf.UpdateType[idx])
            {
                case ARF_UPDATE:
                case INTNL_ARF_UPDATE:
                    int arfExtraBits = layerExtraBits[gf.LayerDepth[idx]];
                    gf.BitAllocation[idx] = baseFrameBits > int.MaxValue - arfExtraBits ? int.MaxValue : baseFrameBits + arfExtraBits;
                    break;
                case INTNL_OVERLAY_UPDATE:
                case OVERLAY_UPDATE: gf.BitAllocation[idx] = 0; break;
                default: gf.BitAllocation[idx] = baseFrameBits; break;
            }
        }
        if (gfGroupSize < MAX_STATIC_GF_GROUP_LENGTH) gf.BitAllocation[gfGroupSize] = 0;
    }

    private static bool IsAlmostStatic(double gfZeroMotion, int kfZeroMotion, bool isLapEnabled)
        => isLapEnabled ? gfZeroMotion >= 0.999 : (gfZeroMotion >= 0.995) && (kfZeroMotion >= STATIC_KF_GROUP_THRESH);

    private bool DetectGfCut(int frameIndex, int curStart, bool flashDetected, int activeMaxGfInterval, int activeMinGfInterval,
        ref AomGfGroupStats g)
    {
        double mvRatioAccumulatorThresh = (_cmHeight + _cmWidth) / 4.0;
        if (!flashDetected)
        {
            int index = _twopass.StatsIn - _twopass.InStart;
            if (DetectTransitionToStill(index, _rc.MinGfInterval, frameIndex - curStart, 5, g.LoopDecayRate, g.LastLoopDecayRate)) return true;
        }
        if (frameIndex - curStart >= activeMinGfInterval && (_rc.FramesToKey - frameIndex >= _rc.MinGfInterval) &&
            ((frameIndex - curStart) & 1) != 0 && !flashDetected &&
            (g.MvRatioAccumulator > mvRatioAccumulatorThresh || g.AbsMvInOutAccumulator > 4.4))
            return true;
        if ((frameIndex - curStart) >= activeMaxGfInterval + 1 &&
            (_cfg.Usage != REALTIME || !IsAlmostStatic(g.ZeroMotionAccumulator, _twopass.KfZeromotionPct, _lapEnabled)))
            return true;
        return false;
    }

    // ---- regions ----
    private static readonly double[] SmoothFilt = { 0.006, 0.061, 0.242, 0.383, 0.242, 0.061, 0.006 };
    private const int HALF_FILT_LEN = 3, WINDOW_SIZE = 7, HALF_WIN = 3;

    private void SmoothFilterStats(int s, int startIdx, int lastIdx, double[] filtIntraErr, double[] filtCodedErr)
    {
        var st = _twopass.Buf;
        for (int i = startIdx; i <= lastIdx; i++)
        {
            double totalWt = 0;
            for (int j = -HALF_FILT_LEN; j <= HALF_FILT_LEN; j++)
            {
                int idx = Math.Clamp(i + j, startIdx, lastIdx);
                if (st[s + idx].IsFlash != 0) continue;
                filtIntraErr[i] += SmoothFilt[j + HALF_FILT_LEN] * st[s + idx].IntraError;
                totalWt += SmoothFilt[j + HALF_FILT_LEN];
            }
            if (totalWt > 0.01) filtIntraErr[i] /= totalWt;
            else filtIntraErr[i] = st[s + i].IntraError;
        }
        for (int i = startIdx; i <= lastIdx; i++)
        {
            double totalWt = 0;
            for (int j = -HALF_FILT_LEN; j <= HALF_FILT_LEN; j++)
            {
                int idx = Math.Clamp(i + j, startIdx, lastIdx);
                if (st[s + idx].IsFlash != 0 || (idx > 0 && st[s + idx - 1].IsFlash != 0)) continue;
                filtCodedErr[i] += SmoothFilt[j + HALF_FILT_LEN] * st[s + idx].CodedError;
                totalWt += SmoothFilt[j + HALF_FILT_LEN];
            }
            if (totalWt > 0.01) filtCodedErr[i] /= totalWt;
            else filtCodedErr[i] = st[s + i].CodedError;
        }
    }

    private static void GetGradient(double[] values, int start, int last, double[] grad)
    {
        if (start == last) { grad[start] = 0; return; }
        for (int i = start; i <= last; i++)
        {
            int prev = Math.Max(i - 1, start);
            int next = Math.Min(i + 1, last);
            grad[i] = (values[next] - values[prev]) / (next - prev);
        }
    }

    private int FindNextScenecut(int s, int first, int last)
    {
        var st = _twopass.Buf;
        if (last - first == 0) return -1;
        for (int i = first; i <= last; i++)
        {
            if (st[s + i].IsFlash != 0 || (i > 0 && st[s + i - 1].IsFlash != 0)) continue;
            double tempIntra = Math.Max(st[s + i].IntraError, 0.01);
            double thisRatio = st[s + i].CodedError / tempIntra;
            double maxPrevRatio = 0, maxPrevCoded = 0;
            for (int j = Math.Max(first, i - HALF_WIN); j < i; j++)
            {
                if (st[s + j].IsFlash != 0 || (j > 0 && st[s + j - 1].IsFlash != 0)) continue;
                tempIntra = Math.Max(st[s + j].IntraError, 0.01);
                double tempRatio = st[s + j].CodedError / tempIntra;
                if (tempRatio > maxPrevRatio) maxPrevRatio = tempRatio;
                if (st[s + j].CodedError > maxPrevCoded) maxPrevCoded = st[s + j].CodedError;
            }
            double maxNextRatio = 0, maxNextCoded = 0;
            for (int j = i + 1; j <= Math.Min(i + HALF_WIN, last); j++)
            {
                if (st[s + i].IsFlash != 0 || (i > 0 && st[s + i - 1].IsFlash != 0)) continue;
                tempIntra = Math.Max(st[s + j].IntraError, 0.01);
                double tempRatio = st[s + j].CodedError / tempIntra;
                if (tempRatio > maxNextRatio) maxNextRatio = tempRatio;
                if (st[s + j].CodedError > maxNextCoded) maxNextCoded = st[s + j].CodedError;
            }
            if (maxPrevRatio < 0.001 && maxNextRatio < 0.001)
            {
                if (thisRatio < 0.02) continue;
            }
            else
            {
                double maxSr = st[s + i].SrCodedError;
                if (i < last) maxSr = Math.Max(maxSr, st[s + i + 1].SrCodedError);
                double maxSrFrRatio = maxSr / Math.Max(st[s + i].CodedError, 0.01);
                if (maxSrFrRatio > 1.2) continue;
                if (thisRatio < 2 * Math.Max(maxPrevRatio, maxNextRatio) && st[s + i].CodedError < 2 * Math.Max(maxPrevCoded, maxNextCoded))
                    continue;
            }
            return i;
        }
        return -1;
    }

    private static void RemoveRegion(int merge, AomRegion[] regions, ref int numRegions, ref int nextRegion)
    {
        int k = nextRegion;
        if (numRegions == 1) { numRegions = 0; return; }
        if (k == 0) merge = 1;
        else if (k == numRegions - 1) merge = 0;
        int numMerge = merge == 2 ? 2 : 1;
        switch (merge)
        {
            case 0: regions[k - 1].Last = regions[k].Last; nextRegion = k; break;
            case 1: regions[k + 1].Start = regions[k].Start; nextRegion = k + 1; break;
            case 2: regions[k - 1].Last = regions[k + 1].Last; nextRegion = k; break;
        }
        numRegions -= numMerge;
        for (k = nextRegion - (merge == 1 ? 1 : 0); k < numRegions; k++) regions[k] = regions[k + numMerge];
    }

    private static void InsertRegion(int start, int last, int type, AomRegion[] regions, ref int numRegions, ref int curRegionIdx)
    {
        int k = curRegionIdx;
        int thisRegionType = regions[k].Type;
        int thisRegionLast = regions[k].Last;
        int numAdd = (start != regions[k].Start ? 1 : 0) + (last != regions[k].Last ? 1 : 0);
        for (int r = numRegions - 1; r > k; r--) regions[r + numAdd] = regions[r];
        numRegions += numAdd;
        if (start > regions[k].Start)
        {
            regions[k].Last = start - 1;
            k++;
            regions[k].Start = start;
        }
        regions[k].Type = type;
        if (last < thisRegionLast)
        {
            regions[k].Last = last;
            k++;
            regions[k].Start = last + 1;
            regions[k].Last = thisRegionLast;
            regions[k].Type = thisRegionType;
        }
        else regions[k].Last = thisRegionLast;
        curRegionIdx = k;
    }

    private void AnalyzeRegion(int s, int k, AomRegion[] regions)
    {
        var st = _twopass.Buf;
        regions[k].AvgCorCoeff = 0;
        regions[k].AvgSrFrRatio = 0;
        regions[k].AvgIntraErr = 0;
        regions[k].AvgCodedErr = 0;
        int checkFirstSr = k != 0 ? 1 : 0;
        for (int i = regions[k].Start; i <= regions[k].Last; i++)
        {
            if (i > regions[k].Start || checkFirstSr != 0)
            {
                double numFrames = regions[k].Last - regions[k].Start + checkFirstSr;
                double maxCodedError = Math.Max(st[s + i].CodedError, st[s + i - 1].CodedError);
                double thisRatio = st[s + i].SrCodedError / Math.Max(maxCodedError, 0.001);
                regions[k].AvgSrFrRatio += thisRatio / numFrames;
            }
            double len = regions[k].Last - regions[k].Start + 1;
            regions[k].AvgIntraErr += st[s + i].IntraError / len;
            regions[k].AvgCodedErr += st[s + i].CodedError / len;
            regions[k].AvgCorCoeff += Math.Max(st[s + i].CorCoeff, 0.001) / len;
            regions[k].AvgNoiseVar += Math.Max(st[s + i].NoiseVar, 0.001) / len;
        }
    }

    private void GetRegionStats(int s, AomRegion[] regions, int numRegions)
    {
        for (int k = 0; k < numRegions; k++) AnalyzeRegion(s, k, regions);
    }

    private int FindStableRegions(int s, double[] gradCoded, int thisStart, int thisLast, AomRegion[] regions)
    {
        var st = _twopass.Buf;
        int k = 0;
        regions[k].Start = thisStart;
        for (int i = thisStart; i <= thisLast; i++)
        {
            double meanIntra = 0.001, varIntra = 0.001, meanCoded = 0.001, varCoded = 0.001;
            int count = 0;
            for (int j = -HALF_WIN; j <= HALF_WIN; j++)
            {
                int idx = Math.Clamp(i + j, thisStart, thisLast);
                if (st[s + idx].IsFlash != 0 || (idx > 0 && st[s + idx - 1].IsFlash != 0)) continue;
                meanIntra += st[s + idx].IntraError;
                varIntra += st[s + idx].IntraError * st[s + idx].IntraError;
                meanCoded += st[s + idx].CodedError;
                varCoded += st[s + idx].CodedError * st[s + idx].CodedError;
                count++;
            }
            int curType;
            if (count > 0)
            {
                meanIntra /= count;
                varIntra /= count;
                meanCoded /= count;
                varCoded /= count;
                bool isIntraStable = varIntra / (meanIntra * meanIntra) < 1.03;
                bool isCodedStable = (varCoded / (meanCoded * meanCoded) < 1.04 && Math.Abs(gradCoded[i]) / meanCoded < 0.05) ||
                    meanCoded / meanIntra < 0.05;
                bool isCodedSmall = meanCoded < 0.5 * meanIntra;
                curType = isIntraStable && isCodedStable && isCodedSmall ? STABLE_REGION : HIGH_VAR_REGION;
            }
            else curType = HIGH_VAR_REGION;
            if (i == regions[k].Start) regions[k].Type = curType;
            else if (curType != regions[k].Type)
            {
                regions[k].Last = i - 1;
                regions[k + 1].Start = i;
                regions[k + 1].Type = curType;
                k++;
            }
        }
        regions[k].Last = thisLast;
        return k + 1;
    }

    private static void CleanupRegions(AomRegion[] regions, ref int numRegions)
    {
        int k = 0;
        while (k < numRegions)
        {
            if ((k > 0 && regions[k - 1].Type == regions[k].Type && regions[k].Type != SCENECUT_REGION) || regions[k].Last < regions[k].Start)
                RemoveRegion(0, regions, ref numRegions, ref k);
            else k++;
        }
    }

    private static void RemoveShortRegions(AomRegion[] regions, ref int numRegions, int type, int length)
    {
        int k = 0;
        while (k < numRegions && numRegions > 1)
        {
            if (regions[k].Last - regions[k].Start + 1 < length && regions[k].Type == type) RemoveRegion(2, regions, ref numRegions, ref k);
            else k++;
        }
        CleanupRegions(regions, ref numRegions);
    }

    private void AdjustUnstableRegionBounds(int s, AomRegion[] regions, ref int numRegions)
    {
        var st = _twopass.Buf;
        RemoveShortRegions(regions, ref numRegions, STABLE_REGION, HALF_WIN);
        RemoveShortRegions(regions, ref numRegions, HIGH_VAR_REGION, HALF_WIN);
        GetRegionStats(s, regions, numRegions);
        for (int k = 0; k < numRegions; k++)
        {
            if (regions[k].Type == STABLE_REGION) continue;
            if (k > 0)
            {
                double avgIntraErr = 0;
                int starti = Math.Max(regions[k - 1].Last - WINDOW_SIZE + 1, regions[k - 1].Start + 1);
                int lasti = regions[k - 1].Last;
                int counti = 0;
                for (int i = starti; i <= lasti; i++) { avgIntraErr += st[s + i].IntraError; counti++; }
                if (counti > 0)
                {
                    avgIntraErr = Math.Max(avgIntraErr / counti, 0.001);
                    int countCoded = 0, countGrad = 0;
                    for (int j = lasti + 1; j <= regions[k].Last; j++)
                    {
                        bool intraClose = Math.Abs(st[s + j].IntraError - avgIntraErr) / avgIntraErr < 0.1;
                        bool codedSmall = st[s + j].CodedError / avgIntraErr < 0.1;
                        bool coeffClose = st[s + j].CorCoeff > 0.995;
                        if (!coeffClose || !codedSmall) countCoded--;
                        if (intraClose && countCoded >= 0 && countGrad >= 0)
                        {
                            regions[k - 1].Last = j;
                            regions[k].Start = j + 1;
                        }
                        else break;
                    }
                }
            }
            if (k < numRegions - 1)
            {
                double avgIntraErr = 0;
                int starti = regions[k + 1].Start;
                int lasti = Math.Min(regions[k + 1].Last - 1, regions[k + 1].Start + WINDOW_SIZE - 1);
                int counti = 0;
                for (int i = starti; i <= lasti; i++) { avgIntraErr += st[s + i].IntraError; counti++; }
                if (counti > 0)
                {
                    avgIntraErr = Math.Max(avgIntraErr / counti, 0.001);
                    int countCoded = 1, countGrad = 1;
                    for (int j = starti - 1; j >= regions[k].Start; j--)
                    {
                        bool intraClose = Math.Abs(st[s + j].IntraError - avgIntraErr) / avgIntraErr < 0.1;
                        bool codedSmall = st[s + j + 1].CodedError / avgIntraErr < 0.1;
                        bool coeffClose = st[s + j].CorCoeff > 0.995;
                        if (!coeffClose || !codedSmall) countCoded--;
                        if (intraClose && countCoded >= 0 && countGrad >= 0)
                        {
                            regions[k + 1].Start = j;
                            regions[k].Last = j - 1;
                        }
                        else break;
                    }
                }
            }
        }
        CleanupRegions(regions, ref numRegions);
        RemoveShortRegions(regions, ref numRegions, HIGH_VAR_REGION, HALF_WIN);
        GetRegionStats(s, regions, numRegions);
        int kk = 0;
        while (kk < numRegions && numRegions > 1)
        {
            if (regions[kk].Type == STABLE_REGION && (regions[kk].Last - regions[kk].Start + 1) < 2 * WINDOW_SIZE &&
                ((kk > 0 && (regions[kk].AvgCodedErr > regions[kk - 1].AvgCodedErr * 1.01 ||
                             regions[kk].AvgCorCoeff < regions[kk - 1].AvgCorCoeff * 0.999)) &&
                 (kk < numRegions - 1 && (regions[kk].AvgCodedErr > regions[kk + 1].AvgCodedErr * 1.01 ||
                                          regions[kk].AvgCorCoeff < regions[kk + 1].AvgCorCoeff * 0.999))))
            {
                RemoveRegion(2, regions, ref numRegions, ref kk);
                AnalyzeRegion(s, kk - 1, regions);
            }
            else if (regions[kk].Type == HIGH_VAR_REGION && (regions[kk].Last - regions[kk].Start + 1) < 2 * WINDOW_SIZE &&
                     ((kk > 0 && (regions[kk].AvgCodedErr < regions[kk - 1].AvgCodedErr * 0.99 ||
                                  regions[kk].AvgCorCoeff > regions[kk - 1].AvgCorCoeff * 1.001)) &&
                      (kk < numRegions - 1 && (regions[kk].AvgCodedErr < regions[kk + 1].AvgCodedErr * 0.99 ||
                                               regions[kk].AvgCorCoeff > regions[kk + 1].AvgCorCoeff * 1.001))))
            {
                RemoveRegion(2, regions, ref numRegions, ref kk);
                AnalyzeRegion(s, kk - 1, regions);
            }
            else kk++;
        }
        RemoveShortRegions(regions, ref numRegions, STABLE_REGION, WINDOW_SIZE);
        RemoveShortRegions(regions, ref numRegions, HIGH_VAR_REGION, HALF_WIN);
    }

    private int IntraDir(int s, int idx) => (_twopass.Buf[s + idx].IntraError - _twopass.Buf[s + idx - 1].IntraError) > 0 ? 1 : -1;

    private void FindBlendingRegions(int s, AomRegion[] regions, ref int numRegions)
    {
        var st = _twopass.Buf;
        int k = 0;
        int countStable = 0;
        while (k < numRegions)
        {
            if (regions[k].Type == STABLE_REGION) { k++; countStable++; continue; }
            int dir = 0;
            int start = 0, last;
            for (int i = regions[k].Start; i <= regions[k].Last; i++)
            {
                if (k == 0 && i == regions[k].Start) continue;
                if (st[s + i].IsFlash != 0 || (i > 0 && st[s + i - 1].IsFlash != 0)) continue;
                double grad = st[s + i].IntraError - st[s + i - 1].IntraError;
                bool largeChange = Math.Abs(grad) / Math.Max(st[s + i].IntraError, 0.01) > 0.05;
                int thisDir = 0;
                if (largeChange) thisDir = grad > 0 ? 1 : -1;
                if (dir == thisDir) continue;
                if (dir != 0)
                {
                    last = i - 1;
                    InsertRegion(start, last, BLENDING_REGION, regions, ref numRegions, ref k);
                }
                dir = thisDir;
                if (k == 0 && i == regions[k].Start + 1) start = i - 1;
                else start = i;
            }
            if (dir != 0)
            {
                last = regions[k].Last;
                InsertRegion(start, last, BLENDING_REGION, regions, ref numRegions, ref k);
            }
            k++;
        }
        GetRegionStats(s, regions, numRegions);
        for (k = 0; k < numRegions; k++)
        {
            if (regions[k].Type != BLENDING_REGION) continue;
            if (regions[k].Last == regions[k].Start || regions[k].AvgCorCoeff < 0.6 || countStable == 0) regions[k].Type = HIGH_VAR_REGION;
        }
        GetRegionStats(s, regions, numRegions);
        k = 1;
        while (k < numRegions)
        {
            if (k < numRegions - 1 && regions[k].Type == HIGH_VAR_REGION)
            {
                if (regions[k - 1].Type == BLENDING_REGION && regions[k + 1].Type == BLENDING_REGION && regions[k].Last - regions[k].Start < 3)
                {
                    int prevDir = IntraDir(s, regions[k - 1].Last);
                    int nextDir = IntraDir(s, regions[k + 1].Last);
                    if (prevDir < 0 && nextDir > 0)
                    {
                        double ratioThres = Math.Min(regions[k - 1].AvgSrFrRatio, regions[k + 1].AvgSrFrRatio) * 0.95;
                        if (regions[k].AvgSrFrRatio > ratioThres)
                        {
                            regions[k].Type = BLENDING_REGION;
                            RemoveRegion(2, regions, ref numRegions, ref k);
                            AnalyzeRegion(s, k - 1, regions);
                            continue;
                        }
                    }
                }
            }
            if (regions[k - 1].Type == BLENDING_REGION && regions[k].Type == BLENDING_REGION)
            {
                int prevDir = IntraDir(s, regions[k - 1].Last);
                int nextDir = IntraDir(s, regions[k].Last);
                int totalLength = regions[k].Last - regions[k - 1].Start + 1;
                if (totalLength < 4)
                {
                    regions[k - 1].Type = HIGH_VAR_REGION;
                    k++;
                    continue;
                }
                bool toMerge = false;
                if (prevDir < 0 && nextDir > 0)
                {
                    double prevLength = regions[k - 1].Last - regions[k - 1].Start + 1;
                    double lastRatio, ratioThres;
                    int pl = regions[k - 1].Last;
                    if (prevLength < 2.01)
                    {
                        double maxCodedError = Math.Max(st[s + pl].CodedError, st[s + pl - 1].CodedError);
                        lastRatio = st[s + pl].SrCodedError / Math.Max(maxCodedError, 0.001);
                        ratioThres = regions[k].AvgSrFrRatio * 0.95;
                    }
                    else
                    {
                        double maxCodedError = Math.Max(st[s + pl].CodedError, st[s + pl - 1].CodedError);
                        lastRatio = st[s + pl].SrCodedError / Math.Max(maxCodedError, 0.001);
                        double prevRatio = (regions[k - 1].AvgSrFrRatio * prevLength - lastRatio) / (prevLength - 1.0);
                        ratioThres = Math.Min(prevRatio, regions[k].AvgSrFrRatio) * 0.95;
                    }
                    if (lastRatio > ratioThres) toMerge = true;
                }
                if (toMerge)
                {
                    RemoveRegion(0, regions, ref numRegions, ref k);
                    AnalyzeRegion(s, k - 1, regions);
                    continue;
                }
                else
                {
                    int prevK = k - 1;
                    InsertRegion(regions[prevK].Last, regions[prevK].Last, HIGH_VAR_REGION, regions, ref numRegions, ref prevK);
                    AnalyzeRegion(s, prevK, regions);
                    k = prevK + 1;
                    AnalyzeRegion(s, k, regions);
                }
            }
            k++;
        }
        CleanupRegions(regions, ref numRegions);
    }

    private static void CleanupBlendings(AomRegion[] regions, ref int numRegions)
    {
        int k = 0;
        while (k < numRegions && numRegions > 1)
        {
            bool isShortBlending = regions[k].Type == BLENDING_REGION && regions[k].Last - regions[k].Start + 1 < 5;
            bool isShortHv = regions[k].Type == HIGH_VAR_REGION && regions[k].Last - regions[k].Start + 1 < 5;
            int hasStableNeighbor = (k > 0 && regions[k - 1].Type == STABLE_REGION) || (k < numRegions - 1 && regions[k + 1].Type == STABLE_REGION) ? 1 : 0;
            int hasBlendNeighbor = (k > 0 && regions[k - 1].Type == BLENDING_REGION) || (k < numRegions - 1 && regions[k + 1].Type == BLENDING_REGION) ? 1 : 0;
            int totalNeighbors = (k > 0 ? 1 : 0) + (k < numRegions - 1 ? 1 : 0);
            if (isShortBlending || (isShortHv && hasStableNeighbor + hasBlendNeighbor >= totalNeighbors))
            {
                double prevDiff = k > 0 ? Math.Abs(regions[k].AvgCorCoeff - regions[k - 1].AvgCorCoeff) : 1;
                double nextDiff = k < numRegions - 1 ? Math.Abs(regions[k].AvgCorCoeff - regions[k + 1].AvgCorCoeff) : 1;
                int merge = prevDiff > nextDiff ? 1 : 0;
                RemoveRegion(merge, regions, ref numRegions, ref k);
            }
            else k++;
        }
        CleanupRegions(regions, ref numRegions);
    }

    /// <summary>identify_regions: s is the stats_start index (may be "before" the first stat by the caller's
    /// pointer arithmetic, but only indices of real stats are read).</summary>
    private void IdentifyRegions(int s, int totalFrames, int offset, AomRegion[] regions, ref int totalRegions)
    {
        if (totalFrames <= 1) return;
        var tempRegions = new AomRegion[totalFrames];
        var filtIntraErr = new double[totalFrames];
        var filtCodedErr = new double[totalFrames];
        var gradCoded = new double[totalFrames];
        int curRegion = 0, thisStart = 0, thisLast;
        int nextScenecut;
        do
        {
            nextScenecut = FindNextScenecut(s, thisStart, totalFrames - 1);
            thisLast = nextScenecut >= 0 ? nextScenecut - 1 : totalFrames - 1;
            SmoothFilterStats(s, thisStart, thisLast, filtIntraErr, filtCodedErr);
            GetGradient(filtCodedErr, thisStart, thisLast, gradCoded);
            int numRegions = FindStableRegions(s, gradCoded, thisStart, thisLast, tempRegions);
            AdjustUnstableRegionBounds(s, tempRegions, ref numRegions);
            GetRegionStats(s, tempRegions, numRegions);
            FindBlendingRegions(s, tempRegions, ref numRegions);
            CleanupBlendings(tempRegions, ref numRegions);
            int k = 0;
            while (k < numRegions)
            {
                if (tempRegions[k].Type != STABLE_REGION) { k++; continue; }
                int start = tempRegions[k].Start, last = tempRegions[k].Last;
                for (int i = start; i <= last; i++)
                    if (_twopass.Buf[s + i].IsFlash != 0) InsertRegion(i, i, HIGH_VAR_REGION, tempRegions, ref numRegions, ref k);
                k++;
            }
            CleanupRegions(tempRegions, ref numRegions);
            for (k = 0; k < numRegions; k++)
            {
                if (tempRegions[k].Last < tempRegions[k].Start && k == numRegions - 1) { numRegions--; break; }
                regions[k + curRegion] = tempRegions[k];
            }
            curRegion += numRegions;
            if (nextScenecut > -1)
            {
                regions[curRegion].Type = SCENECUT_REGION;
                regions[curRegion].Start = nextScenecut;
                regions[curRegion].Last = nextScenecut;
                curRegion++;
                thisStart = nextScenecut + 1;
            }
        } while (nextScenecut >= 0);
        totalRegions = curRegion;
        GetRegionStats(s, regions, totalRegions);
        for (int k = 0; k < totalRegions; k++)
        {
            if (regions[k].Type != SCENECUT_REGION ||
                regions[k].AvgCorCoeff * (1 - _twopass.Buf[s + regions[k].Start].NoiseVar / regions[k].AvgIntraErr) < 0.8)
                continue;
            regions[k].Type = HIGH_VAR_REGION;
        }
        CleanupRegions(regions, ref totalRegions);
        GetRegionStats(s, regions, totalRegions);
        for (int k = 0; k < totalRegions; k++)
        {
            regions[k].Start += offset;
            regions[k].Last += offset;
        }
    }

    /// <summary>regions[k] as C reads it: regions[-1] is the 56 bytes of PRIMARY_RATE_CONTROL before the array
    /// (gf_intervals[3..14] and cur_gf_index; offsets from the mingw-w64 build's layout).</summary>
    private AomRegion RegionAt(AomRegion[] regions, int k)
    {
        if (k >= 0) return regions[k];
        var g = _pRc.GfIntervals;
        double D(int i) => BitConverter.Int64BitsToDouble((long)(uint)g[i] | ((long)g[i + 1] << 32));
        return new AomRegion
        {
            Start = g[3], Last = g[4], AvgNoiseVar = D(5), AvgCorCoeff = D(7), AvgSrFrRatio = D(9), AvgIntraErr = D(11), AvgCodedErr = D(13),
            Type = _pRc.CurGfIndex,
        };
    }

    private static int FindRegionsIndex(AomRegion[] regions, int numRegions, int frameIdx)
    {
        for (int k = 0; k < numRegions; k++)
            if (regions[k].Start <= frameIdx && regions[k].Last >= frameIdx) return k;
        return -1;
    }

    private static AomGfGroupStats InitGfStats() => new()
    {
        DecayAccumulator = 1.0, ZeroMotionAccumulator = 1.0, LoopDecayRate = 1.0, LastLoopDecayRate = 1.0,
    };

    /// <summary>calculate_gf_length.</summary>
    private void CalculateGfLength(int maxGopLength, int maxIntervals)
    {
        int startPos = _twopass.StatsIn;
        int stats = startPos - (_rc.FramesSinceKey == 0 ? 1 : 0);
        int fW = _cmWidth, fH = _cmHeight;
        if (HasNoStatsStage)
        {
            for (int n = 0; n < MAX_NUM_GF_INTERVALS; n++) _pRc.GfIntervals[n] = Math.Min(_rc.MaxGfInterval, maxGopLength);
            _pRc.CurGfIndex = 0;
            _rc.IntervalsTillGfCalculateDue = MAX_NUM_GF_INTERVALS;
            return;
        }
        int activeMinGfInterval = _rc.MinGfInterval;
        int activeMaxGfInterval = Math.Min(_rc.MaxGfInterval, maxGopLength);
        int minShrinkInt = Math.Max(6, activeMinGfInterval);
        int i = _rc.FramesSinceKey == 0 ? 1 : 0;
        maxIntervals = _lapEnabled ? 1 : maxIntervals;
        int countCuts = 1;
        int curStart = -1 + (_arfGfBoostLst == 0 ? 1 : 0), curLast;
        var cutPos = new int[MAX_NUM_GF_INTERVALS + 1];
        cutPos[0] = -1;
        var g = InitGfStats();
        var st = _twopass.Buf;
        while (countCuts < maxIntervals + 1)
        {
            int cutHere;
            if (i >= _rc.FramesToKey) cutHere = 2;
            else if (i - curStart >= _rc.StaticSceneMaxGfInterval) cutHere = 1;
            else if (!InputStats(out var nextFrame)) cutHere = 2;
            else
            {
                bool flashDetected = DetectFlash(0);
                AccumulateNextFrameStats(nextFrame, flashDetected, _rc.FramesSinceKey, i, ref g, fW, fH);
                cutHere = DetectGfCut(i, curStart, flashDetected, activeMaxGfInterval, activeMinGfInterval, ref g) ? 1 : 0;
            }
            if (cutHere != 0)
            {
                curLast = i - 1;
                int oriLast = curLast;
                int offset = _rc.FramesSinceKey - _pRc.RegionsOffset;
                var regions = _pRc.Regions;
                int numRegions = _pRc.NumRegions;
                int scenecutIdx = -1;
                if (curLast - curStart <= activeMaxGfInterval && curLast > curStart)
                {
                    int kStart = FindRegionsIndex(regions, numRegions, curStart + offset);
                    int kLast = FindRegionsIndex(regions, numRegions, curLast + offset);
                    if (curStart + offset == 0) kStart = 0;
                    for (int r = kStart + 1; r <= kLast; r++)
                        if (regions[r].Type == SCENECUT_REGION && regions[r].Last - offset - curStart > activeMinGfInterval)
                        {
                            scenecutIdx = r;
                            break;
                        }
                    // regions[scenecut_idx] with scenecut_idx == -1 reads regions[-1] in C (the field before the array)
                    if (scenecutIdx >= 0 && regions[numRegions - 1].Last - regions[scenecutIdx].Last < 4) scenecutIdx = -1;
                    else if (scenecutIdx < 0) { }
                    if (scenecutIdx != -1)
                    {
                        bool isMinorSc = regions[scenecutIdx].AvgCorCoeff *
                            (1 - st[stats + regions[scenecutIdx].Start - offset].NoiseVar / regions[scenecutIdx].AvgIntraErr) > 0.6;
                        curLast = regions[scenecutIdx].Last - offset - (isMinorSc ? 0 : 1);
                    }
                    else
                    {
                        bool isLastAnalysed = (kLast == numRegions - 1) && (curLast + offset == RegionAt(regions, kLast).Last);
                        bool notEnoughRegions = kLast - kStart <= 1 + (RegionAt(regions, kStart).Type == SCENECUT_REGION ? 1 : 0);
                        if (!(isLastAnalysed && notEnoughRegions))
                        {
                            const double arfLengthFactor = 0.1;
                            double bestScore = 0;
                            int bestJ = -1;
                            int firstFrame = regions[0].Start - offset;
                            int lastFrame = RegionAt(regions, numRegions - 1).Last - offset;
                            double baseScore = 0.0;
                            int countBase = 0;
                            bool staticFrames = false;
                            for (int j = curStart + 1; j < curStart + minShrinkInt; j++)
                            {
                                if (stats + j >= _twopass.InEnd) break;
                                baseScore = (baseScore + 1.0) * st[stats + j].CorCoeff;
                                countBase++;
                            }
                            if (countBase != 0 && baseScore / countBase > 0.992) staticFrames = true;
                            int metBlending = 0, lastBlending = 0;
                            for (int j = curStart + minShrinkInt; j <= curLast; j++)
                            {
                                if (stats + j >= _twopass.InEnd) break;
                                baseScore = (baseScore + 1.0) * st[stats + j].CorCoeff;
                                int thisReg = FindRegionsIndex(regions, numRegions, j + offset);
                                if (thisReg < 0) continue;
                                if (regions[thisReg].Type == BLENDING_REGION)
                                {
                                    lastBlending = 1;
                                    if (metBlending != 0) break;
                                    baseScore = 0;
                                    continue;
                                }
                                if (lastBlending != 0) metBlending = 1;
                                lastBlending = 0;
                                double thisScore = arfLengthFactor * baseScore;
                                double tempAccuCoeff = 1.0;
                                int countF = 0;
                                for (int n = j + 1; n <= j + 3 && n <= lastFrame; n++)
                                {
                                    if (stats + n >= _twopass.InEnd) break;
                                    tempAccuCoeff *= st[stats + n].CorCoeff;
                                    thisScore += tempAccuCoeff * Math.Sqrt(Math.Max(0.5, 1 - st[stats + n].NoiseVar / Math.Max(st[stats + n].IntraError, 0.001)));
                                    countF++;
                                }
                                tempAccuCoeff = 1.0;
                                for (int n = j; n > j - 3 * 2 + countF && n > firstFrame; n--)
                                {
                                    if (stats + n < _twopass.InStart) break;
                                    tempAccuCoeff *= st[stats + n].CorCoeff;
                                    thisScore += tempAccuCoeff * Math.Sqrt(Math.Max(0.5, 1 - st[stats + n].NoiseVar / Math.Max(st[stats + n].IntraError, 0.001)));
                                }
                                if (thisScore + (staticFrames ? 0.5 : 0) > bestScore)
                                {
                                    bestScore = thisScore;
                                    bestJ = j;
                                }
                            }
                            int bestReg = FindRegionsIndex(regions, numRegions, bestJ + offset);
                            if (bestReg < numRegions - 1 && bestReg > 0)
                            {
                                if (regions[bestReg - 1].Type == BLENDING_REGION && regions[bestReg + 1].Type == BLENDING_REGION)
                                {
                                    if (bestJ + offset == regions[bestReg].Start && bestJ + offset < regions[bestReg].Last) bestJ += 1;
                                    else if (bestJ + offset == regions[bestReg].Last && bestJ + offset > regions[bestReg].Start) bestJ -= 1;
                                }
                            }
                            if (curLast - bestJ < 2) bestJ = curLast;
                            if (bestJ > 0 && bestScore > 0.1) curLast = bestJ;
                        }
                    }
                }
                cutPos[countCuts] = curLast;
                countCuts++;
                _twopass.StatsIn = startPos + curLast;
                curStart = curLast;
                int curRegionIdx = FindRegionsIndex(regions, numRegions, curStart + 1 + offset);
                if (curRegionIdx >= 0 && regions[curRegionIdx].Type == SCENECUT_REGION) curStart++;
                i = curLast;
                if (cutHere > 1 && curLast == oriLast) break;
                g = InitGfStats();
            }
            ++i;
        }
        _rc.IntervalsTillGfCalculateDue = countCuts - 1;
        for (int n = 1; n < countCuts; n++) _pRc.GfIntervals[n - 1] = cutPos[n] - cutPos[n - 1];
        _pRc.CurGfIndex = 0;
        _twopass.StatsIn = startPos;
    }

    /// <summary>correct_frames_to_key.</summary>
    private void CorrectFramesToKey()
    {
        int lookaheadSize = LookaheadDepth(EncodeStage);
        if (lookaheadSize < LookaheadPopSz(EncodeStage)) _rc.FramesToKey = Math.Min(_rc.FramesToKey, lookaheadSize);
        else if (_framesLeft > 0) _rc.FramesToKey = Math.Min(_rc.FramesToKey, _framesLeft);
    }

    /// <summary>define_gf_group_pass0.</summary>
    private void DefineGfGroupPass0(bool isFinalPass)
    {
        var gf = _gfGroup;
        _pRc.BaselineGfInterval = _pRc.GfIntervals[_pRc.CurGfIndex];
        _rc.IntervalsTillGfCalculateDue--;
        _pRc.CurGfIndex++;
        CorrectFramesToKey();
        if (_pRc.BaselineGfInterval > _rc.FramesToKey) _pRc.BaselineGfInterval = _rc.FramesToKey;
        _pRc.GfuBoost = DEFAULT_GF_BOOST;
        _pRc.ConstrainedGfGroup = _pRc.BaselineGfInterval >= _rc.FramesToKey ? 1 : 0;
        if (_gfMaxPyrHeight == 0 || _gfMinPyrHeight == 0) gf.MaxLayerDepthAllowed = 1;
        else gf.MaxLayerDepthAllowed = _gfMaxPyrHeight;
        const ulong threshSad = 8 * 64 * 64;
        if (_pRc.BaselineGfInterval > _lagInFrames || !IsAltrefEnabled || _pRc.BaselineGfInterval < _rc.MinGfInterval ||
            _rc.FrameSourceSadLag[0] > threshSad)
            gf.MaxLayerDepthAllowed = 0;
        GopSetupStructure(isFinalPass);
        for (int curIndex = 0; curIndex < gf.Size; ++curIndex)
        {
            int curUpdateType = gf.UpdateType[curIndex];
            gf.BitAllocation[curIndex] = curUpdateType == KF_UPDATE ? CalcIframeTargetSizeOnePassVbr() : CalcPframeTargetSizeOnePassVbr(curUpdateType);
        }
    }

    /// <summary>accumulate_gop_stats.</summary>
    private void AccumulateGopStats(bool isIntraOnly, int fW, int fH, int startPos, ref AomGfGroupStats g, out int idx)
    {
        g = InitGfStats();
        int i = isIntraOnly ? 1 : 0;
        while (i < _pRc.GfIntervals[_pRc.CurGfIndex])
        {
            if (!InputStats(out var nextFrame)) break;
            double modFrameErr = CalculateModifiedErr(nextFrame);
            AccumulateThisFrameStats(nextFrame, modFrameErr, ref g);
            ++i;
        }
        _twopass.StatsIn = startPos;
        i = isIntraOnly ? 1 : 0;
        InputStats(out _);
        while (i < _pRc.GfIntervals[_pRc.CurGfIndex])
        {
            if (!InputStats(out var nextFrame)) break;
            bool flashDetected = DetectFlash(0);
            AccumulateNextFrameStats(nextFrame, flashDetected, _rc.FramesSinceKey, i, ref g, fW, fH);
            ++i;
        }
        i = _pRc.GfIntervals[_pRc.CurGfIndex];
        AverageGfStats(i, ref g);
        idx = i;
    }

    private void UpdateGopLength(int idx, bool isFinalPass)
    {
        if (isFinalPass)
        {
            _rc.IntervalsTillGfCalculateDue--;
            _pRc.CurGfIndex++;
        }
        _pRc.ConstrainedGfGroup = idx >= _rc.FramesToKey ? 1 : 0;
        _pRc.BaselineGfInterval = idx;
        _rc.FramesTillGfUpdateDue = _pRc.BaselineGfInterval;
    }

    /// <summary>av1_gop_bit_allocation.</summary>
    private void GopBitAllocation(bool isKeyFrame, bool useArf, long gfGroupBits)
    {
        int gfArfBits = CalculateBoostBits(_pRc.BaselineGfInterval - (_rc.FramesSinceKey == 0 ? 1 : 0), _pRc.GfuBoost, gfGroupBits);
        AllocateGfGroupBits(gfGroupBits, gfArfBits, isKeyFrame, useArf);
    }

    /// <summary>set_gop_bits_boost.</summary>
    private void SetGopBitsBoost(int i, bool isIntraOnly, bool isFinalPass, bool useAltRef, int altOffset, int startPos, ref AomGfGroupStats g)
    {
        if (_cfg.Usage != REALTIME)
        {
            int statsInBackup = _twopass.StatsIn;
            int gfuBoostSum = 0, gfuCount = 0, accumulateI = 0;
            if (_rc.FramesSinceKey == 0)
            {
                for (int k = 0; k < MAX_NUM_GF_INTERVALS; k++)
                {
                    if (_pRc.GfIntervals[k] == 0) break;
                    int newI = _pRc.GfIntervals[k];
                    int extLenNew = newI - (k == 0 ? (isIntraOnly ? 1 : 0) : 0);
                    if (useAltRef)
                    {
                        if (accumulateI >= _rc.FramesToKey) break;
                        int forwardFrames = _rc.FramesToKey - accumulateI - newI >= extLenNew ? extLenNew : Math.Max(0, _rc.FramesToKey - accumulateI - newI);
                        if (k != 0)
                        {
                            _twopass.StatsIn += newI;
                            if (_twopass.StatsIn >= _twopass.InEnd) _twopass.StatsIn = _twopass.InEnd;
                        }
                        int used = 0, req = _pRc.NumStatsRequiredForGfuBoost;
                        int gfuBoostTmp = CalcArfBoost(altOffset, forwardFrames, extLenNew, ref used, ref req, true, _lapEnabled, false);
                        _pRc.NumStatsUsedForGfuBoost = used;
                        _pRc.NumStatsRequiredForGfuBoost = req;
                        gfuBoostSum += gfuBoostTmp;
                    }
                    gfuCount++;
                    accumulateI += newI;
                }
                _pRc.GfuBoostAverage = gfuBoostSum / gfuCount;
            }
            _twopass.StatsIn = statsInBackup;
        }
        int extLen = i - (isIntraOnly ? 1 : 0);
        bool scaleMaxBoost = _cfg.Usage != REALTIME;
        if (useAltRef)
        {
            int forwardFrames = _rc.FramesToKey - i >= extLen ? extLen : Math.Max(0, _rc.FramesToKey - i);
            int used = 0, req = _pRc.NumStatsRequiredForGfuBoost;
            _pRc.GfuBoost = CalcArfBoost(altOffset, forwardFrames, extLen, ref used, ref req, true, _lapEnabled, scaleMaxBoost);
            _pRc.NumStatsUsedForGfuBoost = used;
            _pRc.NumStatsRequiredForGfuBoost = req;
        }
        else
        {
            _twopass.StatsIn = startPos;
            int used = 0, req = _pRc.NumStatsRequiredForGfuBoost;
            _pRc.GfuBoost = Math.Min(5400, CalcArfBoost(altOffset, extLen, 0, ref used, ref req, true, _lapEnabled, scaleMaxBoost));
            _pRc.NumStatsUsedForGfuBoost = used;
            _pRc.NumStatsRequiredForGfuBoost = req;
        }
        _pRc.ArfBoostFactor = 1.0f;
        if (useAltRef && !_losslessRequested)
        {
            if (_rc.FramesToKey - extLen == 1 || _rc.FramesToKey - extLen == 0) _pRc.ArfBoostFactor = 0.2f;
        }
        _twopass.StatsIn = startPos;
        if (_lapEnabled) g.GfGroupErr = _pRc.BaselineGfInterval;
        _pRc.GfGroupBits = CalculateTotalGfGroupBits(g.GfGroupErr);
        // GROUP_ADAPTIVE_MAXQ: rc_cfg->mode != AOM_Q only
        if (!_rcModeQ && _pRc.BaselineGfInterval > 1 && isFinalPass) throw new NotImplementedException("non-AOM_Q group max q");
        if (isFinalPass) _twopass.KfGroupErrorLeft -= g.GfGroupErr;
        _twopass.StatsIn = startPos;
        if (_rc.FramesSinceKey != 0)
            _twopass.SectionIntraRating = (uint)CalculateSectionIntraRatio(startPos, _twopass.InEnd, _pRc.BaselineGfInterval);
        GopBitAllocation(_rc.FramesSinceKey == 0, useAltRef, _pRc.GfGroupBits);
        if (isFinalPass)
        {
            _arfGfBoostLst = useAltRef ? 1 : 0;
            _twopass.RollingArfGroupTargetBits = 1;
            _twopass.RollingArfGroupActualBits = 1;
        }
    }

    /// <summary>define_gf_group.</summary>
    private void DefineGfGroup(ref int frameParamsFrameType, ref bool frameParamsShowFrame, bool isFinalPass)
    {
        int startPos = _twopass.StatsIn;
        var gf = _gfGroup;
        int fW = _cmWidth, fH = _cmHeight;
        bool isIntraOnly = _rc.FramesSinceKey == 0;
        _internalAltrefAllowed = _gfMaxPyrHeight > 1;
        if (!isIntraOnly)
        {
            gf.Zero();
            _gfFrameIndex = 0;
        }
        if (HasNoStatsStage)
        {
            DefineGfGroupPass0(isFinalPass);
            return;
        }
        if (_lapEnabled) CorrectFramesToKey();
        var g = new AomGfGroupStats();
        AccumulateGopStats(isIntraOnly, fW, fH, startPos, ref g, out int i);
        bool canDisableArf = _gfMinPyrHeight == 0;
        int activeMinGfInterval = _rc.MinGfInterval;
        bool canDisableInternalArfs = _gfMinPyrHeight <= 1;
        if (canDisableInternalArfs && g.ZeroMotionAccumulator > MIN_ZERO_MOTION && g.AvgSrCodedError < MAX_SR_CODED_ERROR &&
            g.AvgRawErrStdev < MAX_RAW_ERR_VAR)
            _internalAltrefAllowed = false;
        bool useAltRef;
        if (canDisableArf)
            useAltRef = _pRc.UseArfInThisKfGroup != 0 && i < _lagInFrames && i >= MIN_GF_INTERVAL &&
                (_cfg.Usage != REALTIME || !IsAlmostStatic(g.ZeroMotionAccumulator, _twopass.KfZeromotionPct, _lapEnabled));
        else
            useAltRef = _pRc.UseArfInThisKfGroup != 0 && i < _lagInFrames && i > 2;
        gf.MaxLayerDepthAllowed = useAltRef ? _gfMaxPyrHeight : 0;
        int altOffset = 0;
        bool allowGfLengthReduction = ((_rcModeQ && _cqLevel <= 128) || !_internalAltrefAllowed) && !_losslessRequested;
        if (allowGfLengthReduction && useAltRef)
        {
            int nextGfLen = _rc.FramesToKey - i;
            bool singleOverlayLeft = nextGfLen == 0 && i > 4;
            bool unbalancedGf = i > 9 && nextGfLen + 1 < 9 && nextGfLen + 1 >= _rc.MinGfInterval;
            if (singleOverlayLeft || unbalancedGf)
            {
                const int rollBack = 1;
                if (i - rollBack >= activeMinGfInterval + 1)
                {
                    altOffset = -rollBack;
                    i -= rollBack;
                    if (isFinalPass) _rc.IntervalsTillGfCalculateDue = 0;
                    _pRc.GfIntervals[_pRc.CurGfIndex] -= rollBack;
                    _twopass.StatsIn = startPos;
                    AccumulateGopStats(isIntraOnly, fW, fH, startPos, ref g, out i);
                }
            }
        }
        UpdateGopLength(i, isFinalPass);
        GopSetupStructure(isFinalPass);
        SetGopBitsBoost(i, isIntraOnly, isFinalPass, useAltRef, altOffset, startPos, ref g);
        frameParamsFrameType = _rc.FramesSinceKey == 0 ? KEY_FRAME : INTER_FRAME;
        frameParamsShowFrame = !(gf.UpdateType[_gfFrameIndex] == ARF_UPDATE || gf.UpdateType[_gfFrameIndex] == INTNL_ARF_UPDATE);
    }

    // ---- key frames ----
    private static bool SlideTransition(in AomFpStats thisFrame, in AomFpStats lastFrame, in AomFpStats nextFrame)
        => thisFrame.IntraError < thisFrame.CodedError * 1.5 && thisFrame.CodedError > lastFrame.CodedError * 5.0 &&
           thisFrame.CodedError > nextFrame.CodedError * 5.0;

    private static double GetSecondRefUsageThresh(int frameCountSoFar)
    {
        const int adaptUpto = 32;
        const double minThresh = 0.085, maxDelta = 0.035;
        if (frameCountSoFar >= adaptUpto) return minThresh + maxDelta;
        return minThresh + ((double)frameCountSoFar / (adaptUpto - 1)) * maxDelta;
    }

    private bool TestCandidateKf(int thisStatsIndex, int frameCountSoFar, int scenecutMode, int numMbs)
    {
        var fi = _twopass.FirstpassInfo;
        int li = fi.Peek(thisStatsIndex - 1), ti = fi.Peek(thisStatsIndex), ni = fi.Peek(thisStatsIndex + 1);
        if (li < 0 || ti < 0 || ni < 0) return false;
        ref var lastStats = ref fi.StatsBuf[li];
        ref var thisStats = ref fi.StatsBuf[ti];
        ref var nextStats = ref fi.StatsBuf[ni];
        bool isViableKf = false;
        double pcntIntra = 1.0 - thisStats.PcntInter;
        double modifiedPcntInter = thisStats.PcntInter - thisStats.PcntNeutral;
        double secondRefUsageThresh = GetSecondRefUsageThresh(frameCountSoFar);
        int framesToTestAfterCandidateKey = SCENE_CUT_KEY_TEST_INTERVAL;
        int countForTolerablePrediction = 3;
        int statsAfterThisStats = fi.FutureCount(thisStatsIndex) - 1;
        if (scenecutMode == ENABLE_SCENECUT_MODE_1)
        {
            if (statsAfterThisStats < 3) return false;
            framesToTestAfterCandidateKey = 3;
            countForTolerablePrediction = 1;
        }
        framesToTestAfterCandidateKey = Math.Min(framesToTestAfterCandidateKey, statsAfterThisStats);
        if ((!_rcModeQ || frameCountSoFar >= 3) && thisStats.PcntSecondRef < secondRefUsageThresh &&
            nextStats.PcntSecondRef < secondRefUsageThresh &&
            (thisStats.PcntInter < 0.05 || SlideTransition(thisStats, lastStats, nextStats) ||
             (pcntIntra > 0.25 && pcntIntra > 2.0 * modifiedPcntInter &&
              (thisStats.IntraError / DoubleDivideCheck(thisStats.CodedError)) < 1.9 &&
              ((Math.Abs(lastStats.CodedError - thisStats.CodedError) / DoubleDivideCheck(thisStats.CodedError) > 0.4) ||
               (Math.Abs(lastStats.IntraError - thisStats.IntraError) / DoubleDivideCheck(thisStats.IntraError) > 0.4) ||
               ((nextStats.IntraError / DoubleDivideCheck(nextStats.CodedError)) > 3.5)))))
        {
            int i;
            double boostScore = 0.0, oldBoostScore = 0.0, decayAccumulator = 1.0;
            for (i = 1; i <= framesToTestAfterCandidateKey; ++i)
            {
                ref var localNext = ref fi.StatsBuf[fi.Peek(thisStatsIndex + i)];
                if ((localNext.IntraError - thisStats.IntraError) / DoubleDivideCheck(thisStats.IntraError) > 0.1 &&
                    thisStats.CodedError > localNext.CodedError * 6)
                    break;
                double nextIiratio = 12.5 * localNext.IntraError / DoubleDivideCheck(localNext.CodedError);
                if (nextIiratio > 128.0) nextIiratio = 128.0;
                if (localNext.PcntInter > 0.85) decayAccumulator *= localNext.PcntInter;
                else decayAccumulator *= (0.85 + localNext.PcntInter) / 2.0;
                boostScore += decayAccumulator * nextIiratio;
                if (localNext.PcntInter < 0.05 || nextIiratio < 1.5 || ((localNext.PcntInter - localNext.PcntNeutral) < 0.20 && nextIiratio < 3.0) ||
                    (boostScore - oldBoostScore) < 3.0 || localNext.IntraError < 200.0 / numMbs)
                    break;
                oldBoostScore = boostScore;
            }
            isViableKf = boostScore > 30.0 && i > countForTolerablePrediction;
        }
        return isViableKf;
    }

    /// <summary>detect_app_forced_key.</summary>
    private int DetectAppForcedKey() => IsForcedKeyframePending(_lookaheadMaxSz, EncodeStage);

    private int GetProjectedKfBoost()
    {
        if (_pRc.NumStatsUsedForKfBoost >= _rc.FramesToKey) return _pRc.KfBoost;
        double tplFactor = KfBoostProjectionFactor(_rc.FramesToKey);
        double tplFactorNumStats = KfBoostProjectionFactor(_pRc.NumStatsUsedForKfBoost);
        return (int)Math.Round((tplFactor * _pRc.KfBoost) / tplFactorNumStats, MidpointRounding.ToEven);
    }

    private int NumMbs => (((_cmWidth + 7) >> 3 << 1) + 2 >> 2) * (((_cmHeight + 7) >> 3 << 1) + 2 >> 2);

    /// <summary>define_kf_interval.</summary>
    private int DefineKfInterval(int numFramesToDetectScenecut, int searchStartIdx)
    {
        var fi = _twopass.FirstpassInfo;
        var recentLoopDecay = new double[8];
        double decayAccumulator;
        int i, j;
        int framesToKey = searchStartIdx;
        int framesSinceKey = _rc.FramesSinceKey + 1;
        bool scenecutDetected = false;
        int numFramesToNextKey = DetectAppForcedKey();
        if (numFramesToDetectScenecut == 0) return numFramesToNextKey != -1 ? numFramesToNextKey : _rc.FramesToKey;
        if (numFramesToNextKey != -1) numFramesToDetectScenecut = Math.Min(numFramesToDetectScenecut, numFramesToNextKey);
        for (j = 0; j < 8; ++j) recentLoopDecay[j] = 1.0;
        i = 0;
        int numMbs = NumMbs;
        int futureStatsCount = fi.FutureCount(0);
        while (framesToKey < futureStatsCount && framesToKey < numFramesToDetectScenecut)
        {
            if (_pRc.EnableScenecutDetection > 0 && _kfAutoKey && framesToKey + 1 < futureStatsCount)
            {
                if (framesSinceKey >= _kfKeyFreqMin)
                {
                    scenecutDetected = TestCandidateKf(framesToKey, framesSinceKey, _pRc.EnableScenecutDetection, numMbs);
                    if (scenecutDetected)
                    {
                        bool testNextGop = false;
                        for (int idx = 0; idx < 32; ++idx)
                        {
                            int ns = fi.Peek(framesToKey + idx);
                            if (ns < 0) continue;
                            if (_frameNumber + framesToKey + idx > 2 && fi.StatsBuf[ns].LtCodedError * 2.5 < fi.StatsBuf[ns].CodedError) testNextGop = true;
                        }
                        if (!testNextGop) break;
                    }
                }
                ref var nextStats = ref fi.StatsBuf[fi.Peek(framesToKey + 1)];
                double loopDecayRate = GetPredictionDecayRate(nextStats);
                recentLoopDecay[i % 8] = loopDecayRate;
                decayAccumulator = 1.0;
                for (j = 0; j < 8; ++j) decayAccumulator *= recentLoopDecay[j];
                if (framesSinceKey >= _kfKeyFreqMin)
                {
                    scenecutDetected = DetectTransitionToStill(framesToKey + 1, _rc.MinGfInterval, i, _kfKeyFreqMax - i, loopDecayRate, decayAccumulator);
                    if (scenecutDetected)
                    {
                        _pRc.UseArfInThisKfGroup = 0;
                        break;
                    }
                }
                ++framesToKey;
                ++framesSinceKey;
                if (framesToKey >= 2 * _kfKeyFreqMax) break;
            }
            else
            {
                ++framesToKey;
                ++framesSinceKey;
            }
            ++i;
        }
        if (_lapEnabled && !scenecutDetected) framesToKey = numFramesToNextKey;
        return framesToKey;
    }

    private int CalcAvgStats(ref AomFpStats avg)
    {
        int numFrames;
        for (numFrames = 0; numFrames < _rc.FramesToKey - 1; ++numFrames)
        {
            if (!InputStats(out var cur)) break;
            avg.Accumulate(cur);
        }
        if (numFrames < 2) return numFrames;
        avg.Weight /= numFrames; avg.IntraError /= numFrames; avg.FrameAvgWaveletEnergy /= numFrames; avg.CodedError /= numFrames;
        avg.SrCodedError /= numFrames; avg.PcntInter /= numFrames; avg.PcntMotion /= numFrames; avg.PcntSecondRef /= numFrames;
        avg.PcntNeutral /= numFrames; avg.IntraSkipPct /= numFrames; avg.InactiveZoneRows /= numFrames; avg.InactiveZoneCols /= numFrames;
        avg.MVr /= numFrames; avg.MvrAbs /= numFrames; avg.MVc /= numFrames; avg.MvcAbs /= numFrames; avg.MVrv /= numFrames;
        avg.MVcv /= numFrames; avg.MvInOutCount /= numFrames; avg.NewMvCount /= numFrames; avg.Count /= numFrames; avg.Duration /= numFrames;
        return numFrames;
    }

    private double GetKfBoostScore(double kfRawErr, ref double zeroMotionAccumulator, ref double srAccumulator, bool useAvgStat)
    {
        var frameStat = new AomFpStats();
        int numStatUsed = 0;
        double boostScore = 0.0;
        double kfMaxBoost = _rcModeQ ? Fclamp(_rc.FramesToKey * 2.0, 80.0, 128.0) : 128.0;
        if (useAvgStat) numStatUsed = CalcAvgStats(ref frameStat);
        for (int i = numStatUsed; i < _rc.FramesToKey - 1; ++i)
        {
            if (!useAvgStat && !InputStats(out frameStat)) break;
            if (i > 0) zeroMotionAccumulator = Math.Min(zeroMotionAccumulator, GetZeroMotionFactor(frameStat));
            else zeroMotionAccumulator = frameStat.PcntInter - frameStat.PcntMotion;
            if (srAccumulator < kfRawErr * 1.50 && i <= _rc.MaxGfInterval * 2)
            {
                double zmFactor = 0.75 + (zeroMotionAccumulator / 2.0);
                if (i < 2) srAccumulator = 0.0;
                double frameBoost = CalcKfFrameBoost(frameStat, ref srAccumulator, kfMaxBoost);
                boostScore += frameBoost * zmFactor;
            }
        }
        return boostScore;
    }

    /// <summary>find_next_key_frame.</summary>
    private void FindNextKeyFrame(ref AomFpStats thisFrame)
    {
        var gf = _gfGroup;
        var fi = _twopass.FirstpassInfo;
        var firstFrame = thisFrame;
        _rc.FramesSinceKey = 0;
        _pRc.UseArfInThisKfGroup = IsAltrefEnabled ? 1 : 0;
        gf.Zero();
        _gfFrameIndex = 0;
        _rc.FramesTillGfUpdateDue = 0;
        if (HasNoStatsStage)
        {
            int numFramesToAppForcedKey = DetectAppForcedKey();
            _pRc.ThisKeyFrameForced = _frameNumber != 0 && _rc.FramesToKey == 0 ? 1 : 0;
            if (numFramesToAppForcedKey != -1) _rc.FramesToKey = numFramesToAppForcedKey;
            else _rc.FramesToKey = Math.Max(1, _kfKeyFreqMax);
            CorrectFramesToKey();
            _pRc.KfBoost = DEFAULT_KF_BOOST;
            gf.UpdateType[0] = KF_UPDATE;
            return;
        }
        int startPosition = _twopass.StatsIn;
        int kfBits;
        double zeroMotionAccumulator = 1.0, srAccumulator = 0.0;
        int framesToKeyClipped = int.MaxValue;
        long kfGroupBitsClipped = long.MaxValue;
        _pRc.ThisKeyFrameForced = _pRc.NextKeyFrameForced;
        _twopass.KfGroupBits = 0;
        _twopass.KfGroupErrorLeft = 0;
        double kfRawErr = thisFrame.IntraError;
        double kfModErr = CalculateModifiedErr(thisFrame);
        int framesToKey = DefineKfInterval(_kfKeyFreqMax, 1);
        _rc.FramesToKey = framesToKey != -1 ? Math.Min(_kfKeyFreqMax, framesToKey) : _kfKeyFreqMax;
        if (_lapEnabled) CorrectFramesToKey();
        if (_kfAutoKey && _rc.FramesToKey > _kfKeyFreqMax)
        {
            _rc.FramesToKey /= 2;
            _twopass.StatsIn = startPosition;
            for (int i = 0; i < _rc.FramesToKey; ++i) if (!InputStats(out _)) break;
            _pRc.NextKeyFrameForced = 1;
        }
        else if ((_twopass.StatsIn == _twopass.InEnd && IsStatConsumptionStageTwopass) || _rc.FramesToKey >= _kfKeyFreqMax)
            _pRc.NextKeyFrameForced = 1;
        else _pRc.NextKeyFrameForced = 0;
        double kfGroupErr = 0;
        for (int i = 0; i < _rc.FramesToKey; ++i)
        {
            int ts = fi.Peek(i);
            if (ts >= 0)
            {
                kfGroupErr += CalculateModifiedErrNew(fi.TotalStats, fi.StatsBuf[ts], VbrBias, _twopass.ModifiedErrorMin, _twopass.ModifiedErrorMax);
                ++_pRc.NumStatsUsedForKfBoost;
            }
        }
        if ((_twopass.BitsLeft > 0 && _twopass.ModifiedErrorLeft > 0.0) || (_lapEnabled && !_rcModeQ))
        {
            int maxBits = FrameMaxBits();
            _twopass.KfGroupBits = _lapEnabled ? (long)_rc.FramesToKey * _rc.AvgFrameBandwidth
                : (long)(_twopass.BitsLeft * (kfGroupErr / _twopass.ModifiedErrorLeft));
            long maxGrpBits = (long)maxBits * _rc.FramesToKey;
            if (_twopass.KfGroupBits > maxGrpBits) _twopass.KfGroupBits = maxGrpBits;
        }
        else _twopass.KfGroupBits = 0;
        _twopass.KfGroupBits = Math.Max(0, _twopass.KfGroupBits);
        if (_lapEnabled)
        {
            framesToKeyClipped = (int)(5 * _framerate);
            if (_rc.FramesToKey > framesToKeyClipped)
                kfGroupBitsClipped = (long)((double)_twopass.KfGroupBits * framesToKeyClipped / _rc.FramesToKey);
        }
        _twopass.StatsIn = startPosition;
        double boostScore = GetKfBoostScore(kfRawErr, ref zeroMotionAccumulator, ref srAccumulator, false);
        _twopass.StatsIn = startPosition;
        _twopass.KfZeromotionPct = (int)(zeroMotionAccumulator * 100.0);
        _twopass.SectionIntraRating = (uint)CalculateSectionIntraRatio(startPosition, _twopass.InEnd, _rc.FramesToKey);
        _pRc.KfBoost = (int)boostScore;
        if (_lapEnabled)
        {
            if (_rcModeQ) _pRc.KfBoost = GetProjectedKfBoost();
            else
            {
                boostScore = GetKfBoostScore(kfRawErr, ref zeroMotionAccumulator, ref srAccumulator, true);
                _twopass.StatsIn = startPosition;
                _pRc.KfBoost += (int)boostScore;
            }
        }
        if (zeroMotionAccumulator > STATIC_KF_GROUP_FLOAT_THRESH && _rc.FramesToKey > 8) _pRc.KfBoost = Math.Max(_pRc.KfBoost, 5400);
        else
        {
            _pRc.KfBoost = Math.Max(_pRc.KfBoost, _rc.FramesToKey * 3);
            _pRc.KfBoost = Math.Max(_pRc.KfBoost, 600);
        }
        kfBits = CalculateBoostBits(Math.Min(_rc.FramesToKey, framesToKeyClipped) - 1, _pRc.KfBoost, Math.Min(_twopass.KfGroupBits, kfGroupBitsClipped));
        _twopass.KfGroupBits -= kfBits;
        gf.BitAllocation[0] = kfBits;
        gf.UpdateType[0] = KF_UPDATE;
        if (_lapEnabled) _twopass.KfGroupErrorLeft = _rc.FramesToKey - 1;
        else _twopass.KfGroupErrorLeft = kfGroupErr - kfModErr;
        _twopass.ModifiedErrorLeft -= kfGroupErr;
    }

    private void SetTwopassParamsBasedOnFpStats(int thisFramePtr)
    {
        if (thisFramePtr < 0) return;
        ref var f = ref _twopass.Buf[thisFramePtr];
        SetTwopassParamsBasedOnFpStats(f);
    }

    private void SetTwopassParamsBasedOnFpStats(in AomFpStats f)
    {
        _twopass.MbAvEnergy = Math.Log(1 + f.IntraError);
        if (!(_twopass.TotalStats.FrameAvgWaveletEnergy < 0)) _twopass.FrameAvgHaarEnergy = Math.Log(1 + f.FrameAvgWaveletEnergy);
        _twopass.FrContentType = f.IntraSkipPct >= 0.15 ? 1 : 0;
    }

    /// <summary>process_first_pass_stats (AOM_Q: no first-frame worst quality estimate).</summary>
    private void ProcessFirstPassStats(ref AomFpStats thisFrame)
    {
        if (!_rcModeQ && _frameNumber == 0 && _gfFrameIndex == 0) throw new NotImplementedException("non-AOM_Q first frame worst quality");
        if (_twopass.StatsIn < _twopass.InEnd)
        {
            thisFrame = _twopass.Buf[_twopass.StatsIn];
            ++_twopass.StatsIn;
        }
        SetTwopassParamsBasedOnFpStats(thisFrame);
    }

    /// <summary>av1_setup_target_rate.</summary>
    private void SetupTargetRate()
    {
        int targetRate = _gfGroup.BitAllocation[_gfFrameIndex];
        if (HasNoStatsStage) RcSetFrameTarget(targetRate, _cmWidth, _cmHeight);
        _rc.BaseFrameTarget = targetRate;
    }

    private void MarkFlashes(int first, int last)
    {
        var b = _twopass.Buf;
        int t = first;
        while (t < last - 1)
        {
            int n = t + 1;
            b[t].IsFlash = b[n].PcntSecondRef > b[n].PcntInter && b[n].PcntSecondRef >= 0.5 ? 1 : 0;
            t = n;
        }
        if (last - 1 >= first) b[last - 1].IsFlash = 0;
    }

    private void SmoothFilterNoise(int first, int last)
    {
        var b = _twopass.Buf;
        int len = last - first;
        var smoothNoise = new double[len];
        for (int i = 0; i < len; i++)
        {
            double totalNoise = 0, totalWt = 0;
            for (int j = -HALF_FILT_LEN; j <= HALF_FILT_LEN; j++)
            {
                int idx = Math.Clamp(i + j, 0, len - 1);
                if (b[first + idx].IsFlash != 0) continue;
                totalNoise += b[first + idx].NoiseVar;
                totalWt += 1.0;
            }
            if (totalWt > 0.01) totalNoise /= totalWt;
            else totalNoise = b[first + i].NoiseVar;
            smoothNoise[i] = totalNoise;
        }
        for (int i = 0; i < len; i++) b[first + i].NoiseVar = smoothNoise[i];
    }

    private bool FlashAt(int t) { var b = _twopass.Buf; return b[t].IsFlash != 0 || b[t - 1].IsFlash != 0 || b[t - 2].IsFlash != 0; }

    private void EstimateNoise(int first, int last)
    {
        var b = _twopass.Buf;
        for (int t = first + 2; t < last; t++)
        {
            b[t].NoiseVar = 0.0;
            if (FlashAt(t)) continue;
            double c1 = b[t - 1].IntraError * (b[t].IntraError - b[t].CodedError);
            double c2 = b[t - 2].IntraError * (b[t - 1].IntraError - b[t - 1].CodedError);
            double c3 = b[t - 2].IntraError * (b[t].IntraError - b[t].SrCodedError);
            if (c1 <= 0 || c2 <= 0 || c3 <= 0) continue;
            c1 = Math.Sqrt(c1); c2 = Math.Sqrt(c2); c3 = Math.Sqrt(c3);
            double noise = b[t - 1].IntraError - c1 * c2 / c3;
            noise = Math.Max(noise, 0.01);
            b[t].NoiseVar = noise;
        }
        for (int t = first + 2; t < last; t++)
        {
            if (FlashAt(t)) continue;
            if (b[t].NoiseVar < 1.0)
            {
                bool found = false;
                for (int n = t + 1; n < last; n++)
                {
                    if (FlashAt(n) || b[n].NoiseVar < 1.0) continue;
                    found = true;
                    b[t].NoiseVar = b[n].NoiseVar;
                    break;
                }
                if (found) continue;
                for (int n = t - 1; n >= first + 2; n--)
                {
                    if (FlashAt(n) || b[n].NoiseVar < 1.0) continue;
                    b[t].NoiseVar = b[n].NoiseVar;
                    break;
                }
            }
        }
        for (int t = first + 2; t < last; t++)
        {
            if (FlashAt(t))
            {
                bool found = false;
                for (int n = t + 1; n < last; n++)
                {
                    if (FlashAt(n)) continue;
                    found = true;
                    b[t].NoiseVar = b[n].NoiseVar;
                    break;
                }
                if (found) continue;
                for (int n = t - 1; n >= first + 2; n--)
                {
                    if (FlashAt(n)) continue;
                    b[t].NoiseVar = b[n].NoiseVar;
                    break;
                }
            }
        }
        for (int t = first; t < first + 2 && first + 2 < last; t++) b[t].NoiseVar = b[first + 2].NoiseVar;
        SmoothFilterNoise(first, last);
    }

    private void EstimateCoeff(int first, int last)
    {
        var b = _twopass.Buf;
        for (int t = first + 1; t < last; t++)
        {
            double c = Math.Sqrt(Math.Max(b[t - 1].IntraError * (b[t].IntraError - b[t].CodedError), 0.001));
            double corCoeff = c / Math.Max(b[t - 1].IntraError - b[t].NoiseVar, 0.001);
            b[t].CorCoeff = corCoeff * Math.Sqrt(Math.Max(b[t - 1].IntraError - b[t].NoiseVar, 0.001) / Math.Max(b[t].IntraError - b[t].NoiseVar, 0.001));
            b[t].CorCoeff = Fclamp(b[t].CorCoeff, 0.0, 1.0);
        }
        b[first].CorCoeff = 1.0;
    }

    /// <summary>av1_get_second_pass_params.</summary>
    private void GetSecondPassParams(ref int frameParamsFrameType, ref bool frameParamsShowFrame, uint frameFlags)
    {
        var gf = _gfGroup;
        int startPos = _twopass.StatsIn;
        bool updateTotalStats = false;
        if (IsStatConsumptionStage && _twopass.StatsIn < 0) return;
        int framesToNextForcedKey = DetectAppForcedKey();
        if (framesToNextForcedKey == 0)
        {
            _rc.FramesToKey = 0;
            frameFlags &= FRAMEFLAGS_KEY;
        }
        else if (framesToNextForcedKey > 0 && framesToNextForcedKey < _rc.FramesToKey) _rc.FramesToKey = framesToNextForcedKey;
        int updateType = gf.UpdateType[_gfFrameIndex];
        frameParamsFrameType = gf.FrameType[_gfFrameIndex];
        if (_gfFrameIndex < gf.Size && (frameFlags & FRAMEFLAGS_KEY) == 0)
        {
            SetupTargetRate();
            if (updateType == ARF_UPDATE || updateType == INTNL_ARF_UPDATE)
            {
                SetTwopassParamsBasedOnFpStats(ReadFrameStats(gf.ArfSrcOffset[_gfFrameIndex]));
                return;
            }
        }
        if (_rcModeQ) _rc.ActiveWorstQuality = _cqLevel;
        if (_gfFrameIndex == gf.Size)
        {
            if (_lapEnabled && _pRc.EnableScenecutDetection != 0)
            {
                int framesToKey = DefineKfInterval(MAX_GF_LENGTH_LAP + 1, 0);
                if (framesToKey != -1) _rc.FramesToKey = Math.Min(_rc.FramesToKey, framesToKey);
            }
        }
        var thisFrame = new AomFpStats();
        if (IsStatConsumptionStage)
        {
            if (_gfFrameIndex < gf.Size || _rc.FramesToKey == 0)
            {
                ProcessFirstPassStats(ref thisFrame);
                updateTotalStats = true;
            }
        }
        else _rc.ActiveWorstQuality = _cqLevel;
        var thisFrameCopy = thisFrame;
        if (_rc.FramesToKey <= 0)
        {
            frameParamsFrameType = KEY_FRAME;
            FindNextKeyFrame(ref thisFrame);
            thisFrame = thisFrameCopy;
            _tplPrevGopArfDispOrder = -1;
        }
        if (_rc.FramesToFwdKf <= 0) _rc.FramesToFwdKf = -1;   // fwd_kf_dist
        if (_gfFrameIndex == gf.Size)
        {
            // av1_tf_info_reset
            Array.Clear(_tfBufValid);
            Array.Clear(_tfBufGfIndex);
            Array.Clear(_tfBufDisplayIndexOffset);
            int maxGopLength = _lagInFrames >= 32 ? Math.Min(MAX_GF_INTERVAL, _lagInFrames - ArnrMaxFrames / 2) : MAX_GF_LENGTH_LAP;
            if (_lagInFrames == 0) maxGopLength = _rc.MaxGfInterval;
            maxGopLength = Math.Min(maxGopLength, _rc.FramesToKey);
            if (_rc.FramesSinceKey == 0 || _rc.FramesSinceKey == 1 ||
                (_pRc.FramesTillRegionsUpdate - _rc.FramesSinceKey < _rc.FramesToKey &&
                 _pRc.FramesTillRegionsUpdate - _rc.FramesSinceKey < maxGopLength + 1))
            {
                int restFrames = Math.Min(_rc.FramesToKey, MAX_FIRSTPASS_ANALYSIS_FRAMES);
                int availableFrames = _twopass.InEnd - _twopass.StatsIn;
                if (!_lapEnabled) availableFrames += _rc.FramesSinceKey == 0 ? 1 : 0;
                restFrames = Math.Min(restFrames, availableFrames);
                _pRc.FramesTillRegionsUpdate = restFrames;
                if (_lapEnabled)
                {
                    MarkFlashes(_twopass.InStart, _twopass.InEnd);
                    EstimateNoise(_twopass.InStart, _twopass.InEnd);
                    EstimateCoeff(_twopass.InStart, _twopass.InEnd);
                    IdentifyRegions(_twopass.StatsIn, restFrames, 0, _pRc.Regions, ref _pRc.NumRegions);
                }
                else IdentifyRegions(_twopass.StatsIn - (_rc.FramesSinceKey == 0 ? 1 : 0), restFrames, 0, _pRc.Regions, ref _pRc.NumRegions);
            }
            int curRegionIdx = FindRegionsIndex(_pRc.Regions, _pRc.NumRegions, _rc.FramesSinceKey - _pRc.RegionsOffset);
            if ((curRegionIdx >= 0 && _pRc.Regions[curRegionIdx].Type == SCENECUT_REGION) || _rc.FramesSinceKey == 0) _arfGfBoostLst = 0;
            CalculateGfLength(maxGopLength, MAX_NUM_GF_INTERVALS);
            if (maxGopLength > 16 && _enableTplModel && _lagInFrames >= 32 && GopLengthDecisionMethod != 3)
            {
                int thisIdx = _rc.FramesSinceKey + _pRc.GfIntervals[_pRc.CurGfIndex] - _pRc.RegionsOffset - 1;
                int thisRegion = FindRegionsIndex(_pRc.Regions, _pRc.NumRegions, thisIdx);
                int nextRegion = FindRegionsIndex(_pRc.Regions, _pRc.NumRegions, thisIdx + 1);
                bool isLastScenecut = _pRc.GfIntervals[_pRc.CurGfIndex] >= _rc.FramesToKey ||
                    (thisRegion != -1 && _pRc.Regions[thisRegion].Type == SCENECUT_REGION) ||
                    (nextRegion != -1 && _pRc.Regions[nextRegion].Type == SCENECUT_REGION);
                int oriGfInt = _pRc.GfIntervals[_pRc.CurGfIndex];
                if (_pRc.GfIntervals[_pRc.CurGfIndex] > 16 && _rc.MinGfInterval <= 16)
                {
                    DefineGfGroup(ref frameParamsFrameType, ref frameParamsShowFrame, false);
                    TfInfoFiltering();
                    thisFrame = thisFrameCopy;
                    if (IsShorterGfIntervalBetter(frameParamsFrameType, frameParamsShowFrame))
                    {
                        maxGopLength = 16;
                        CalculateGfLength(maxGopLength, 1);
                        if (isLastScenecut && (oriGfInt - _pRc.GfIntervals[_pRc.CurGfIndex] < 4)) _pRc.GfIntervals[_pRc.CurGfIndex] = oriGfInt;
                    }
                }
            }
            DefineGfGroup(ref frameParamsFrameType, ref frameParamsShowFrame, false);
            if (gf.UpdateType[_gfFrameIndex] != ARF_UPDATE && _rc.FramesSinceKey > 0) ProcessFirstPassStats(ref thisFrame);
            DefineGfGroup(ref frameParamsFrameType, ref frameParamsShowFrame, true);
            TfInfoFiltering();
            _rc.FramesTillGfUpdateDue = _pRc.BaselineGfInterval;
        }
        if (gf.UpdateType[_gfFrameIndex] == ARF_UPDATE || gf.UpdateType[_gfFrameIndex] == INTNL_ARF_UPDATE)
        {
            _twopass.StatsIn = startPos;
            SetTwopassParamsBasedOnFpStats(ReadFrameStats(gf.ArfSrcOffset[_gfFrameIndex]));
        }
        else _twopass.ThisFrame = updateTotalStats ? startPos : -1;
        frameParamsFrameType = gf.FrameType[_gfFrameIndex];
        SetupTargetRate();
    }

    /// <summary>av1_init_single_pass_lap.</summary>
    private void InitSinglePassLap()
    {
        _twopass.SrUpdateLag = 1;
        _twopass.BitsLeft = 0;
        _twopass.ModifiedErrorMin = 0.0;
        _twopass.ModifiedErrorMax = 0.0;
        _twopass.ModifiedErrorLeft = 0.0;
        _pRc.VbrBitsOffTarget = 0;
        _pRc.VbrBitsOffTargetFast = 0;
        _pRc.RateErrorEstimate = 0;
        _twopass.KfZeromotionPct = 100;
        _twopass.LastKfgroupZeromotionPct = 100;
        _twopass.BpmFactor = 1.0;
        _twopass.RollingArfGroupTargetBits = 1;
        _twopass.RollingArfGroupActualBits = 1;
    }

    /// <summary>av1_twopass_postencode_update (AOM_Q; frame-parallel prob updates do not apply).</summary>
    private void TwopassPostencodeUpdate(int baseQindex, int frameType)
    {
        if (IsStatConsumptionStage && (_gfFrameIndex < _gfGroup.Size || _rc.FramesToKey == 0))
        {
            int updateType = _gfGroup.UpdateType[_gfFrameIndex];
            if (updateType != ARF_UPDATE && updateType != INTNL_ARF_UPDATE)
            {
                --_twopass.StatsIn;
                if (_lapEnabled) InputStatsLap(out _);
                else InputStats(out _);
            }
            else if (_lapEnabled) _twopass.StatsIn = _twopass.InStart;
        }
        _pRc.VbrBitsOffTarget += _rc.BaseFrameTarget - _rc.ProjectedFrameSize;
        _twopass.BitsLeft = Math.Max(_twopass.BitsLeft - _rc.BaseFrameTarget, 0);
        if (_twopass.RollingArfGroupTargetBits > int.MaxValue - _rc.BaseFrameTarget) _twopass.RollingArfGroupTargetBits = int.MaxValue;
        else _twopass.RollingArfGroupTargetBits += _rc.BaseFrameTarget;
        _twopass.RollingArfGroupActualBits += _rc.ProjectedFrameSize;
        if (_pRc.TotalActualBits != 0)
        {
            _pRc.RateErrorEstimate = (int)((_pRc.VbrBitsOffTarget * 100) / _pRc.TotalActualBits);
            _pRc.RateErrorEstimate = Math.Clamp(_pRc.RateErrorEstimate, -100, 100);
        }
        else _pRc.RateErrorEstimate = 0;
        if (_rc.IsSrcFrameAltRef == 0)
        {
            int pyramidLevel = _gfGroup.LayerDepth[_gfFrameIndex];
            for (int i = pyramidLevel; i <= MAX_ARF_LAYERS; ++i) _pRc.ActiveBestQuality[i] = baseQindex;
        }
        if (frameType != KEY_FRAME)
        {
            _twopass.KfGroupBits -= _rc.BaseFrameTarget;
            _twopass.LastKfgroupZeromotionPct = _twopass.KfZeromotionPct;
        }
        _twopass.KfGroupBits = Math.Max(_twopass.KfGroupBits, 0);
    }
}
