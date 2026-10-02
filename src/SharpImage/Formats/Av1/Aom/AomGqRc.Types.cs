using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>FIRSTPASS_STATS (av1/encoder/firstpass.h).</summary>
internal struct AomFpStats
{
    public double Frame, Weight, IntraError, FrameAvgWaveletEnergy, CodedError, SrCodedError, LtCodedError, PcntInter, PcntMotion,
        PcntSecondRef, PcntNeutral, IntraSkipPct, InactiveZoneRows, InactiveZoneCols, MVr, MvrAbs, MVc, MvcAbs, MVrv, MVcv, MvInOutCount,
        NewMvCount, Duration, Count, RawErrorStdev;
    public long IsFlash;
    public double NoiseVar, CorCoeff, LogIntraError, LogCodedError;

    /// <summary>av1_twopass_zero_stats.</summary>
    public static AomFpStats Zeroed() => new() { Duration = 1.0, CorCoeff = 1.0 };

    /// <summary>av1_accumulate_stats.</summary>
    public void Accumulate(in AomFpStats f)
    {
        Frame += f.Frame;
        Weight += f.Weight;
        IntraError += f.IntraError;
        LogIntraError += Math.Log(1 + f.IntraError);   // log1p
        LogCodedError += Math.Log(1 + f.CodedError);
        FrameAvgWaveletEnergy += f.FrameAvgWaveletEnergy;
        CodedError += f.CodedError;
        SrCodedError += f.SrCodedError;
        LtCodedError += f.LtCodedError;
        PcntInter += f.PcntInter;
        PcntMotion += f.PcntMotion;
        PcntSecondRef += f.PcntSecondRef;
        PcntNeutral += f.PcntNeutral;
        IntraSkipPct += f.IntraSkipPct;
        InactiveZoneRows += f.InactiveZoneRows;
        InactiveZoneCols += f.InactiveZoneCols;
        MVr += f.MVr;
        MvrAbs += f.MvrAbs;
        MVc += f.MVc;
        MvcAbs += f.MvcAbs;
        MVrv += f.MVrv;
        MVcv += f.MVcv;
        MvInOutCount += f.MvInOutCount;
        NewMvCount += f.NewMvCount;
        Count += f.Count;
        Duration += f.Duration;
    }

    /// <summary>subtract_stats (encoder.c).</summary>
    public void Subtract(in AomFpStats f)
    {
        Frame -= f.Frame;
        Weight -= f.Weight;
        IntraError -= f.IntraError;
        FrameAvgWaveletEnergy -= f.FrameAvgWaveletEnergy;
        CodedError -= f.CodedError;
        SrCodedError -= f.SrCodedError;
        LtCodedError -= f.LtCodedError;
        PcntInter -= f.PcntInter;
        PcntMotion -= f.PcntMotion;
        PcntSecondRef -= f.PcntSecondRef;
        PcntNeutral -= f.PcntNeutral;
        IntraSkipPct -= f.IntraSkipPct;
        InactiveZoneRows -= f.InactiveZoneRows;
        InactiveZoneCols -= f.InactiveZoneCols;
        MVr -= f.MVr;
        MvrAbs -= f.MvrAbs;
        MVc -= f.MVc;
        MvcAbs -= f.MvcAbs;
        MVrv -= f.MVrv;
        MVcv -= f.MVcv;
        MvInOutCount -= f.MvInOutCount;
        NewMvCount -= f.NewMvCount;
        Count -= f.Count;
        Duration -= f.Duration;
    }
}

/// <summary>FIRSTPASS_INFO: the circular queue of first pass stats (static buffer of MAX_LAP_BUFFERS + 1).</summary>
internal sealed class AomFirstpassInfo
{
    public const int StaticBufSize = 48 + 1;
    public readonly AomFpStats[] StatsBuf = new AomFpStats[StaticBufSize];
    public int StatsBufSize = StaticBufSize, StartIndex, StatsCount, CurIndex, FutureStatsCount, PastStatsCount;
    public AomFpStats TotalStats;

    /// <summary>av1_firstpass_info_init(info, NULL, 0).</summary>
    public void Init()
    {
        StatsBufSize = StaticBufSize;
        StartIndex = CurIndex = StatsCount = FutureStatsCount = PastStatsCount = 0;
        TotalStats = default;
    }

    public bool MoveCurIndex()
    {
        if (FutureStatsCount > 1)
        {
            CurIndex = (CurIndex + 1) % StatsBufSize;
            --FutureStatsCount;
            ++PastStatsCount;
            return true;
        }
        return false;
    }

    public bool Pop()
    {
        if (StatsCount > 0 && PastStatsCount > 0)
        {
            StartIndex = (StartIndex + 1) % StatsBufSize;
            --StatsCount;
            --PastStatsCount;
            return true;
        }
        return false;
    }

    public bool MoveCurIndexAndPop() => MoveCurIndex() && Pop();

    public bool Push(in AomFpStats s)
    {
        if (StatsCount < StatsBufSize)
        {
            int next = (StartIndex + StatsCount) % StatsBufSize;
            StatsBuf[next] = s;
            ++StatsCount;
            ++FutureStatsCount;
            TotalStats.Accumulate(s);
            return true;
        }
        return false;
    }

    /// <summary>av1_firstpass_info_peek: the stats index, or -1.</summary>
    public int Peek(int offsetFromCur)
    {
        if (offsetFromCur >= -PastStatsCount && offsetFromCur < FutureStatsCount)
            return (CurIndex + offsetFromCur) % StatsBufSize;
        return -1;
    }

    public int FutureCount(int offsetFromCur) => offsetFromCur < FutureStatsCount ? FutureStatsCount - offsetFromCur : 0;
}

/// <summary>REGIONS.</summary>
internal struct AomRegion
{
    public int Start, Last;
    public double AvgNoiseVar, AvgCorCoeff, AvgSrFrRatio, AvgIntraErr, AvgCodedErr;
    public int Type;
}

/// <summary>GF_GROUP_STATS.</summary>
internal struct AomGfGroupStats
{
    public double GfGroupErr, GfGroupRawError, GfGroupSkipPct, GfGroupInactiveZoneRows, MvRatioAccumulator, DecayAccumulator,
        ZeroMotionAccumulator, LoopDecayRate, LastLoopDecayRate, ThisFrameMvInOut, MvInOutAccumulator, AbsMvInOutAccumulator,
        AvgSrCodedError, AvgPcntSecondRef, AvgNewMvCount, AvgWaveletEnergy, AvgRawErrStdev;
    public int NonZeroStdevCount;
}

/// <summary>GF_GROUP (MAX_STATIC_GF_GROUP_LENGTH entries).</summary>
internal sealed class AomGfGroup
{
    public const int MaxLen = 250;   // MAX_STATIC_GF_GROUP_LENGTH
    public readonly int[] UpdateType = new int[MaxLen], ArfSrcOffset = new int[MaxLen], CurFrameIdx = new int[MaxLen],
        LayerDepth = new int[MaxLen], ArfBoost = new int[MaxLen], QVal = new int[MaxLen], RdmultVal = new int[MaxLen],
        BitAllocation = new int[MaxLen], FrameType = new int[MaxLen], RefbufState = new int[MaxLen], SrcOffset = new int[MaxLen],
        DisplayIdx = new int[MaxLen], UpdateRefIdx = new int[MaxLen], PrimaryRefIdx = new int[MaxLen], FrameParallelLevel = new int[MaxLen],
        SkipFrameAsRef = new int[MaxLen];
    public readonly bool[] IsFrameNonRef = new bool[MaxLen], IsFrameDropped = new bool[MaxLen];
    public readonly int[,] SkipFrameRefresh = new int[MaxLen, REF_FRAMES];
    public int MaxLayerDepth, MaxLayerDepthAllowed, ArfIndex, Size;
    public bool IsSframeDue;

    /// <summary>av1_zero(gf_group).</summary>
    public void Zero()
    {
        Array.Clear(UpdateType); Array.Clear(ArfSrcOffset); Array.Clear(CurFrameIdx); Array.Clear(LayerDepth); Array.Clear(ArfBoost);
        Array.Clear(QVal); Array.Clear(RdmultVal); Array.Clear(BitAllocation); Array.Clear(FrameType); Array.Clear(RefbufState);
        Array.Clear(SrcOffset); Array.Clear(DisplayIdx); Array.Clear(UpdateRefIdx); Array.Clear(PrimaryRefIdx);
        Array.Clear(FrameParallelLevel); Array.Clear(SkipFrameAsRef); Array.Clear(IsFrameNonRef); Array.Clear(IsFrameDropped);
        Array.Clear(SkipFrameRefresh);
        MaxLayerDepth = MaxLayerDepthAllowed = ArfIndex = Size = 0;
        IsSframeDue = false;
    }
}

/// <summary>RATE_CONTROL (the fields the good-quality AOM_Q path reads).</summary>
internal sealed class AomRcState
{
    public int BaseFrameTarget, ThisFrameTarget, ProjectedFrameSize, Sb64TargetRate, FramesSinceGolden, FramesTillGfUpdateDue,
        IntervalsTillGfCalculateDue, MinGfInterval, MaxGfInterval, StaticSceneMaxGfInterval, FramesToKey, FramesSinceKey, FramesToFwdKf,
        IsSrcFrameAltRef, AvgFrameBandwidth, MinFrameBandwidth, MaxFrameBandwidth, PrevAvgFrameBandwidth, NiAvQi, NiTotQi, WorstQuality,
        BestQuality, ActiveWorstQuality, FramesSinceSceneChange, FrameNumLastGfRefresh, PrevCodedWidth, PrevCodedHeight,
        FrameLevelFastExtraBits, LastEncodedSizeKeyframe, LastTargetSizeKeyframe;
    public uint FrameNumberEncoded;
    public readonly ulong[] FrameSourceSadLag = new ulong[32];
    public ulong FrameSourceSad = ulong.MaxValue;
    public uint LastFrameLowSourceSad;
}

/// <summary>PRIMARY_RATE_CONTROL.</summary>
internal sealed class AomPrimaryRc
{
    public long GfGroupBits;
    public int BaseLayerQp;
    public int KfBoost, GfuBoost, GfuBoostAverage, CurGfIndex, NumRegions, RegionsOffset, FramesTillRegionsUpdate, BaselineGfInterval,
        ConstrainedGfGroup, ThisKeyFrameForced, NextKeyFrameForced, ArfQ, NumStatsUsedForKfBoost, NumStatsUsedForGfuBoost,
        NumStatsRequiredForGfuBoost, EnableScenecutDetection, UseArfInThisKfGroup, NiFrames, LastKfQindex, LastBoostedQindex,
        RateErrorEstimate, RollingTargetBits, RollingActualBits;
    public readonly int[] GfIntervals = new int[15];   // MAX_NUM_GF_INTERVALS
    public readonly AomRegion[] Regions = new AomRegion[150];   // MAX_FIRSTPASS_ANALYSIS_FRAMES
    public float ArfBoostFactor;
    public double TotQ, AvgQ;
    public readonly int[] AvgFrameQindex = new int[FRAME_TYPES], LastQ = new int[FRAME_TYPES];
    public readonly int[] ActiveBestQuality = new int[MAX_ARF_LAYERS + 1];
    public long TotalActualBits, TotalTargetBits, VbrBitsOffTarget, VbrBitsOffTargetFast, BitsOffTarget, StartingBufferLevel, BufferLevel;
}

/// <summary>TWO_PASS + STATS_BUFFER_CTX + TWO_PASS_FRAME: the stats buffer pointers are indices into Buf.</summary>
internal sealed class AomTwoPassState
{
    public uint SectionIntraRating;
    public AomFpStats[] Buf = Array.Empty<AomFpStats>();   // frame_stats_buffer (frame_stats_arr[i] = &Buf[i])
    public int InStart, InEnd, InBufEnd;                     // stats_in_start / stats_in_end / stats_in_buf_end
    public AomFpStats TotalStats, TotalLeftStats;
    public readonly AomFirstpassInfo FirstpassInfo = new();
    public bool FirstPassDone;
    public long BitsLeft;
    public double ModifiedErrorMin, ModifiedErrorMax, ModifiedErrorLeft;
    public long KfGroupBits;
    public double KfGroupErrorLeft, BpmFactor;
    public int RollingArfGroupTargetBits, RollingArfGroupActualBits, SrUpdateLag, KfZeromotionPct, LastKfgroupZeromotionPct, ExtendMinq,
        ExtendMaxq;
    // TWO_PASS_FRAME (cpi->twopass_frame)
    public int StatsIn;
    public int ThisFrame = -1;
    public double MbAvEnergy, FrameAvgHaarEnergy;
    public int FrContentType;
}
