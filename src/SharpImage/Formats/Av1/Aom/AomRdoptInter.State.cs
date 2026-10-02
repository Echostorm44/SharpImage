using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>IntraModeSearchState (intra_mode_search.h).</summary>
internal sealed class AomIntraModeSearchState
{
    public int BestIntraMode;
    public bool SkipIntraModes;
    public readonly byte[] DirectionalModeSkipMask = new byte[INTRA_MODES];
    public bool DirModeSkipMaskReady;
    public int RateUvIntra, RateUvTokenonly;
    public long DistUvs;
    public byte SkipUvs;
    public int ModeUv;
    public AomPaletteModeInfo PmiUv = new() { PaletteColors = new ushort[3 * AomPaletteModeInfo.PaletteMaxSize] };
    public sbyte UvAngleDelta;

    /// <summary>init_intra_mode_search_state.</summary>
    public void Init()
    {
        BestIntraMode = DC_PRED;
        SkipIntraModes = false;
        Array.Clear(DirectionalModeSkipMask);
        DirModeSkipMaskReady = false;
        RateUvIntra = int.MaxValue;
        RateUvTokenonly = 0;
        DistUvs = 0;
        SkipUvs = 0;
        ModeUv = UV_DC_PRED;
        PmiUv.PaletteSize0 = PmiUv.PaletteSize1 = 0;
        UvAngleDelta = 0;
    }
}

/// <summary>SingleInterModeState.</summary>
internal struct AomSingleInterModeState
{
    public long Rd;
    public int RefFrame;
    public int Valid;
}

/// <summary>InterModeSearchState (rdopt.c).</summary>
internal sealed class AomInterModeSearchState
{
    public long BestRd;
    public readonly long[] BestSkipRd = new long[2];
    public readonly AomMbModeInfo BestMbmode = new();
    public int BestRateY, BestRateUv;
    public int BestModeSkippable, BestSkip2;
    public int BestModeIndex;
    public int NumAvailableRefs;
    public readonly long[] DistRefs = new long[REF_FRAMES];
    public readonly int[] DistOrderRefs = new int[REF_FRAMES];
    public readonly long[] ModeThreshold = new long[MAX_MODES];
    public long BestIntraRd;
    public uint BestPredSse;
    public readonly long[] BestPredRd = new long[REFERENCE_MODES];
    public readonly AomMv[,] SingleNewmv = new AomMv[MAX_REF_MV_SEARCH, REF_FRAMES];
    public readonly int[,] SingleNewmvRate = new int[MAX_REF_MV_SEARCH, REF_FRAMES];
    public readonly int[,] SingleNewmvValid = new int[MAX_REF_MV_SEARCH, REF_FRAMES];
    public readonly long[,,] ModelledRd = new long[MB_MODE_COUNT, MAX_REF_MV_SEARCH, REF_FRAMES];
    public readonly long[,,] SimpleRd = new long[MB_MODE_COUNT, MAX_REF_MV_SEARCH, REF_FRAMES];
    public readonly long[] BestSingleRd = new long[REF_FRAMES];
    public readonly int[] BestSingleMode = new int[REF_FRAMES];
    public readonly AomSingleInterModeState[,,] SingleState = new AomSingleInterModeState[2, SINGLE_INTER_MODE_NUM, FWD_REFS];
    public readonly int[,] SingleStateCnt = new int[2, SINGLE_INTER_MODE_NUM];
    public readonly AomSingleInterModeState[,,] SingleStateModelled = new AomSingleInterModeState[2, SINGLE_INTER_MODE_NUM, FWD_REFS];
    public readonly int[,] SingleStateModelledCnt = new int[2, SINGLE_INTER_MODE_NUM];
    public readonly int[,,] SingleRdOrder = new int[2, SINGLE_INTER_MODE_NUM, FWD_REFS];
    public readonly AomIntraModeSearchState IntraSearchState = new();
    public AomRdStats BestYRdcost;
}

/// <summary>inter_mode_info (rdopt.h).</summary>
internal struct AomInterModeInfo
{
    public int DrlCost;
    public AomMv FullSearchMv;
    public int FullMvRate, FullMvBestsme;
    public int Skip;
}

/// <summary>HandleInterModeArgs (interp_search.h).</summary>
internal sealed class AomHandleInterModeArgs
{
    public readonly AomBuf2d[] AbovePredBuf = new AomBuf2d[3];
    public readonly AomBuf2d[] LeftPredBuf = new AomBuf2d[3];
    public AomInterModeSearchState State = null!;   // single_newmv / rate / valid, modelled_rd, simple_rd live in the state
    public int RefFrameCost, SingleCompCost;
    public int SkipMotionMode;
    public bool SkipIfs;
    public readonly int[] InterIntraMode = new int[REF_FRAMES];
    public readonly AomInterpFilterStats[] InterpFilterStats = NewStats();
    public readonly AomMv[] StartMvStack = new AomMv[MAX_REF_MV_SEARCH * 2];
    public readonly byte[] RefMvIdxStack = new byte[MAX_REF_MV_SEARCH * 2];
    public int StartMvCnt;
    public int InterpFilterStatsIdx;
    public int WedgeIndex = -1, WedgeSign = -1, DiffwtdIndex = -1;
    public readonly int[] CmpMode = new int[MODE_CTX_REF_FRAMES];
    public readonly uint[] BestSingleSseInRefs = new uint[REF_FRAMES];
    public uint BestPredSse;

    private static AomInterpFilterStats[] NewStats()
    {
        var a = new AomInterpFilterStats[MAX_INTERP_FILTER_STATS];
        for (int i = 0; i < a.Length; i++) a[i] = new AomInterpFilterStats();
        return a;
    }
}

/// <summary>INTERPOLATION_FILTER_STATS.</summary>
internal sealed class AomInterpFilterStats
{
    public uint Filters;
    public readonly AomMv[] Mv = new AomMv[2];
    public readonly int[] RefFrames = new int[2];
    public int CompType, CompGroupIdx;
    public long Rd;
    public uint PredSse;
}

/// <summary>InterModesInfo (encoder.h).</summary>
internal sealed class AomInterModesInfo
{
    public int Num;
    public readonly AomMbModeInfo[] MbmiArr = NewMbmis();
    public readonly int[] ModeRateArr = new int[MAX_INTER_MODES];
    public readonly long[] SseArr = new long[MAX_INTER_MODES];
    public readonly long[] EstRdArr = new long[MAX_INTER_MODES];
    public readonly (int Idx, long Rd)[] RdIdxPairArr = new (int, long)[MAX_INTER_MODES];
    public readonly AomRdStats[] RdCostArr = new AomRdStats[MAX_INTER_MODES];
    public readonly AomRdStats[] RdCostYArr = new AomRdStats[MAX_INTER_MODES];
    public readonly AomRdStats[] RdCostUvArr = new AomRdStats[MAX_INTER_MODES];

    private static AomMbModeInfo[] NewMbmis()
    {
        var a = new AomMbModeInfo[MAX_INTER_MODES];
        for (int i = 0; i < a.Length; i++) a[i] = new AomMbModeInfo();
        return a;
    }
}

/// <summary>motion_mode_candidate.</summary>
internal sealed class AomMotionModeCandidate
{
    public readonly AomMbModeInfo Mbmi = new();
    public int RateMv, Rate2Nocoeff, SkipMotionMode;
    public long RdCost = long.MaxValue;

    public void CopyFrom(AomMotionModeCandidate s)
    {
        Mbmi.CopyFrom(s.Mbmi);
        RateMv = s.RateMv; Rate2Nocoeff = s.Rate2Nocoeff; SkipMotionMode = s.SkipMotionMode; RdCost = s.RdCost;
    }
}

/// <summary>mode_skip_mask_t.</summary>
internal sealed class AomModeSkipMask
{
    public readonly uint[] PredModes = new uint[REF_FRAMES];
    public readonly bool[,] RefCombo = new bool[REF_FRAMES, REF_FRAMES + 1];
}

/// <summary>OBMCBuffer (block.h): the weighted source / mask of the OBMC search and the neighbour predictions.</summary>
internal sealed class AomObmcBuffer
{
    public readonly int[] Wsrc = new int[128 * 128];
    public readonly int[] Mask = new int[128 * 128];
    public readonly byte[] AbovePred = new byte[2 * 128 * 128];
    public readonly byte[] LeftPred = new byte[2 * 128 * 128];
    public readonly ushort[] AbovePred16 = new ushort[2 * 128 * 128];
    public readonly ushort[] LeftPred16 = new ushort[2 * 128 * 128];
}

/// <summary>RefFrameDistanceInfo.</summary>
internal sealed class AomRefFrameDistanceInfo
{
    public readonly int[] RefRelativeDist = new int[INTER_REFS_PER_FRAME];
    public int NearestPastRef = NONE_FRAME, NearestFutureRef = NONE_FRAME;
}

internal sealed partial class AomMacroblock
{
    /// <summary>x->mbmi_ext (all reference types).</summary>
    public readonly AomMbmiExtInter MbmiExtInter = new();
    public readonly uint[] PredSse = new uint[REF_FRAMES];
    public readonly int[] PredMvSad = new int[REF_FRAMES];
    public readonly int[] BestPredMvSad = new int[2];
    public readonly int[] MaxMvContext = new int[REF_FRAMES];
    public readonly int[] PredMvs = new int[REF_FRAMES];
    /// <summary>x->thresh_freq_fact [BLOCK_SIZES_ALL][MAX_MODES].</summary>
    public readonly int[,] ThreshFreqFact = new int[BLOCK_SIZES_ALL, MAX_MODES];
    public AomFullMvLimits MvLimits;
    public AomInterModesInfo InterModesInfo = new();
    public readonly AomObmcBuffer ObmcBuffer = new();
    /// <summary>x->tmp_pred_bufs[2] (three planes of MAX_SB_SQUARE each).</summary>
    public readonly byte[][] TmpPredBufs = { new byte[3 * 128 * 128], new byte[3 * 128 * 128] };
    public readonly ushort[][] TmpPredBufs16 = { new ushort[3 * 128 * 128], new ushort[3 * 128 * 128] };
    public readonly int[] PickedRefFramesMask = new int[32 * 32];
    public readonly long[,] TopInterTxNoSplitRd = new long[MAX_TX_BLOCKS_IN_MAX_SB, TOP_INTER_TX_NO_SPLIT_COUNT];
    public readonly long[] TopCompAvgEstRd = new long[TOP_COMP_AVG_EST_RD_COUNT];
    public readonly AomWarpSampleInfo[] WarpSampleInfo = NewWarpSamples();
    public readonly bool[] TplKeepRefFrame = new bool[REF_FRAMES];
    public int CompoundIdx;
    public int CompRdStatsIdx;
    public bool ReuseInterPred;

    private static AomWarpSampleInfo[] NewWarpSamples()
    {
        var a = new AomWarpSampleInfo[REF_FRAMES];
        for (int i = 0; i < a.Length; i++) a[i] = new AomWarpSampleInfo();
        return a;
    }
}

/// <summary>WARP_SAMPLE_INFO.</summary>
internal sealed class AomWarpSampleInfo
{
    public int Num = -1;
    public readonly int[] Pts = new int[SAMPLES_ARRAY_SIZE];
    public readonly int[] PtsInref = new int[SAMPLES_ARRAY_SIZE];
}

internal sealed partial class AomTileDataEnc
{
    /// <summary>tile_data->inter_mode_rd_models[BLOCK_SIZES_ALL].</summary>
    public readonly AomInterModeRdModel[] InterModeRdModels = NewModels();

    private static AomInterModeRdModel[] NewModels()
    {
        var a = new AomInterModeRdModel[BLOCK_SIZES_ALL];
        for (int i = 0; i < a.Length; i++) a[i] = new AomInterModeRdModel();
        return a;
    }
}

internal sealed partial class AomMacroblock
{
    /// <summary>yv12_mb[REF_FRAMES][MAX_MB_PLANE] of av1_rd_pick_inter_mode.</summary>
    public readonly AomBuf2d[,] Yv12Mb = new AomBuf2d[REF_FRAMES, 3];
}

internal sealed partial class AomPickModeContext
{
    /// <summary>ctx->mbmi_ext_best of an inter-frame block (every reference type).</summary>
    public readonly AomMbmiExtFrameInter MbmiExtBestInter = new();
}
