using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/encoder/firstpass.c (av1_first_pass / av1_first_pass_row, firstpass_intra_prediction,
// firstpass_inter_prediction, first_pass_motion_search, update_firstpass_stats) and the look-ahead processing stage
// (ppi->cpi_lap: av1_get_compressed_data / av1_encode_strategy / av1_encode in LAP_STAGE). The LAP compressor never
// runs av1_get_ref_frames: its remapped_ref_idx stays init_buffer_indices' identity, LAST / LAST2 / GOLDEN being
// ref_frame_map[0] / [1] / [3].
internal sealed partial class AomTileDataEnc
{
    /// <summary>tile_data->firstpass_top_mv.</summary>
    public AomMv FirstpassTopMv;
}

internal sealed partial class AomGqEncoder
{
    private const double FIRST_PASS_Q = 10.0;
    private const int INTRA_MODE_PENALTY = 1024, NEW_MV_MODE_PENALTY = 32, DARK_THRESH = 64, NCOUNT_INTRA_THRESH = 8192,
        NCOUNT_INTRA_FACTOR = 3, UL_INTRA_THRESH = 50, INVALID_ROW = -1, MAX_FULL_PEL_VAL = (1 << 10) - 1;

    private int _lapFrameNumber;
    private readonly AomFrameBuffer?[] _lapMap = new AomFrameBuffer?[REF_FRAMES];
    private bool _lapScreenContent, _lapAllowIntrabc;
    private AomComp? _fpCpi;
    private AomMacroblock? _fpX;
    private AomSearchSiteConfig[]? _fpfSites;

    private struct FpFrameStats
    {
        public long IntraError, FrameAvgWaveletEnergy, CodedError, SrCodedError, LtCodedError;
        public int MvCount, InterCount, SecondRefCount;
        public double NeutralCount;
        public int IntraSkipCount, ImageDataStartRow, NewMvCount, SumInVectors, SumMvr, SumMvc, SumMvrAbs, SumMvcAbs;
        public long SumMvrs, SumMvcs;
        public double IntraFactor, BrightnessFactor;
    }

    /// <summary>The LAP stage of encoder_encode: av1_get_compressed_data(cpi_lap) + av1_post_encode_updates.</summary>
    private void RunLapStage(bool flush)
    {
        if (!flush && LookaheadDepth(LapStage) < LookaheadPopSz(LapStage)) return;
        var source = LookaheadPeek(0, LapStage);
        if (source == null) return;
        // choose_frame_source (stat generation: always the next frame, shown, popped)
        var lastSource = _lapFrameNumber > 0 ? LookaheadPeek(-1, LapStage) : null;
        int updateType0 = _gfGroup.UpdateType[0];
        bool kfRequested = _lapFrameNumber == 0 || _kfKeyFreqMax == 0 || (source.Flags & AOM_EFLAG_FORCE_KF) != 0;
        int frameType = kfRequested && updateType0 != OVERLAY_UPDATE && updateType0 != INTNL_OVERLAY_UPDATE ? KEY_FRAME : INTER_FRAME;
        // av1_encode
        if (frameType == KEY_FRAME && _gfGroup.RefbufState[0] == REFBUF_RESET) _lapFrameNumber = 0;
        // the prev_gop_arf_src copy (the LAP compressor's gf_frame_index stays 0)
        _lapGfIndexOverride = 0;
        UpdatePrevGopArfSrc(source.Img, _lapFrameNumber);
        _lapGfIndexOverride = null;
        FirstPass(source.Img, lastSource?.Img, frameType, source.TsEnd - source.TsStart);
        // av1_post_encode_updates: pop
        LookaheadPopEntry(flush, LapStage);
    }

    private static int FpUnitRows(int fpBsize, int mbRows)
    {
        int h = MiSizeHighLog2[fpBsize], mb = MiSizeHighLog2[BLOCK_16X16];
        return h > mb ? mbRows >> (h - mb) : mbRows << (mb - h);
    }

    private static int FpUnitCols(int fpBsize, int mbCols)
    {
        int w = MiSizeWideLog2[fpBsize], mb = MiSizeWideLog2[BLOCK_16X16];
        return w > mb ? mbCols >> (w - mb) : mbCols << (mb - w);
    }

    private static int FpNumMbs(int fpBsize, int numMbs16x16)
    {
        int w = MiSizeWideLog2[fpBsize], h = MiSizeHighLog2[fpBsize], mw = MiSizeWideLog2[BLOCK_16X16], mh = MiSizeHighLog2[BLOCK_16X16];
        return w > mw ? numMbs16x16 >> ((w - mw) + (h - mh)) : numMbs16x16 << ((mw - w) + (mh - h));
    }

    /// <summary>av1_init_motion_fpf.</summary>
    private static AomSearchSiteConfig InitMotionFpf()
    {
        var cfg = new AomSearchSiteConfig();
        int numSearchSteps = 0;
        int stageIndex = AomSearchSiteConfig.MaxMvSearchSteps - 1;
        cfg.Site[stageIndex, 0] = default;
        for (int radius = 1 << (AomSearchSiteConfig.MaxMvSearchSteps - 1); radius > 0; radius /= 2)
        {
            int tanRadius = Math.Max((int)(0.41 * radius), 1);
            int numSearchPts = radius == 1 ? 8 : 12;
            int[] m =
            {
                0, 0, -radius, 0, radius, 0, 0, -radius, 0, radius, -radius, -tanRadius, radius, tanRadius, -tanRadius, radius,
                tanRadius, -radius, -radius, tanRadius, radius, -tanRadius, tanRadius, radius, -tanRadius, -radius,
            };
            for (int i = 0; i <= numSearchPts; ++i) cfg.Site[stageIndex, i] = new AomMv(m[2 * i], m[2 * i + 1]);
            cfg.SearchesPerStep[stageIndex] = numSearchPts;
            cfg.Radius[stageIndex] = radius;
            --stageIndex;
            ++numSearchSteps;
        }
        cfg.NumSearchSteps = numSearchSteps;
        return cfg;
    }

    /// <summary>The first pass compressor state (cpi_lap's common / MACROBLOCK) set up for a frame.</summary>
    private (AomComp cpi, AomMacroblock x) SetupFirstPassFrame(AomFrameBuffer source, int frameType, int qindex, int fpBsize)
    {
        var c = _cfg;
        int bd = c.BitDepth;
        // (a fresh common per frame: its cur_frame is the frame's reconstruction; the LAP mi_alloc entries only ever
        // carry the fields the first pass sets)
        var cm0 = new AomCommon(c.Width, c.Height, c.SsX, c.SsY, c.Monochrome, SelectSbSize(c), bd);
        cm0.SetTileInfo(c.TileColumnsLog2, c.TileRowsLog2);
        _fpCpi = new AomComp { Cm = cm0, Speed = c.Speed, SbSize = cm0.SbSize, BitDepth = bd, UseHighbitdepth = bd > 8, Mode = GOOD };
        _fpCpi.CdefControl = 1;
        _fpCpi.QmMinLevel = DEFAULT_QM_FIRST; _fpCpi.QmMaxLevel = DEFAULT_QM_LAST;
        _fpCpi.Seq = _seq;
        _fpCpi.BorderInPixels = _resizeModeFixed ? 288 : BlockSizeWide[cm0.SbSize] + 32;
        if (_fpfSites == null)
        {
            var fpf = InitMotionFpf();
            _fpfSites = new AomSearchSiteConfig[NUM_DISTINCT_SEARCH_METHODS];
            for (int i = 0; i < _fpfSites.Length; i++) _fpfSites[i] = fpf;
            _fpX = new AomMacroblock();
        }
        var cpi = _fpCpi;
        var x = _fpX!;
        var cm = cpi.Cm;
        cpi.Source = source;
        cm.FrameType = frameType;
        cm.BaseQindex = qindex;
        cm.AllowHighPrecisionMv = true;   // av1_set_high_precision_mv(cpi, 1, 0)
        cpi.AllowScreenContentTools = cpi.UseScreenContentTools = _lapScreenContent;
        cpi.AllowIntrabc = _lapAllowIntrabc;
        // av1_set_speed_features_framesize_independent (the framesize dependent ones stay at their init_*_sf defaults)
        var sfIn = new AomSpeedFeatureInputs
        {
            Width = c.Width, Height = c.Height, UseHighBitDepth = bd > 8, AllowScreenContentTools = _lapScreenContent, UseScreenContentTools = _lapScreenContent,
            IsScreenContentType = _lapScreenContent, BaseQindex = qindex, FrameType = frameType, UpdateType = _gfGroup.UpdateType[0],
            GfFrameType = _gfGroup.FrameType[0], Mode = GOOD, LapEnabled = true, CompressorStage = LAP_STAGE, NumWorkers = 1,
            Tuning = c.Tune switch { AomTune.Iq => AOM_TUNE_IQ, AomTune.Ssim => AOM_TUNE_SSIM, _ => AOM_TUNE_PSNR },
        };
        if (c.Sharpness is int sh) sfIn.Sharpness = sh;
        cpi.Sf.SetFramesizeIndependent(sfIn, _seqFlags, cpi.WinnerModeParams, c.Speed);
        // av1_set_quantizer / av1_frame_init_quantizer
        AomQuantSetup.SetQuantizer(cpi, qindex);
        cpi.Quants = new AomQuants(bd, cm.YDcDeltaQ, cm.UDcDeltaQ, cm.UAcDeltaQ, cm.VDcDeltaQ, cm.VAcDeltaQ, cpi.Sharpness);
        // av1_default_coef_probs / av1_init_mode_probs / av1_init_mv_probs
        cm.Fc = new Av1CdfContext();
        Av1CdfDefaults.InitializeDefault(cm.Fc, qindex <= 20 ? 0 : qindex <= 60 ? 1 : qindex <= 120 ? 2 : 3);
        cpi.UpdateType = _gfGroup.UpdateType[0];
        cpi.LayerDepth = Math.Min(_gfGroup.LayerDepth[0], 6);
        cpi.FrameType = frameType;
        cpi.IsStatConsumptionStage = false;
        cpi.Tuning = sfIn.Tuning;
        cpi.RdRdmult = cpi.ComputeRdMult(qindex + cm.YDcDeltaQ);
        cpi.TileData = AomTileDataEnc.InitAll(cpi);
        cpi.SearchSites = _fpfSites!;
        // the thread data: quantizers, planes, rd consts (errorperbit, mv costs), sadperbit
        var xd = x.E;
        for (int i = 0; i < 8; ++i) { xd.Lossless[i] = qindex == 0 ? 1 : 0; xd.Qindex[i] = qindex; }
        AomQuantSetup.SetQIndex(cpi, x, qindex);
        for (int p = 0; p < 3; p++)
        {
            xd.Plane[p].PlaneType = p == 0 ? 0 : 1;
            xd.Plane[p].SubsamplingX = p == 0 ? 0 : c.SsX;
            xd.Plane[p].SubsamplingY = p == 0 ? 0 : c.SsY;
        }
        xd.Bd = bd;
        xd.GlobalMotion = cm.GlobalMotion;
        x.Errorperbit = AomRd.ErrorPerBit(cpi.RdRdmult);
        x.MvCosts.Fill(cm.Fc.Mv, false, true);
        x.SadPerBit = AomEncodeFrame.SadPerBit(qindex, bd);
        xd.Cfl.StoreY = 0;
        // av1_set_frame_size: set_ref_ptrs(LAST, LAST) -> the identity scale factors
        xd.BlockRefScaleFactors[0] = AomInterPred.Identity;
        xd.BlockRefScaleFactors[1] = AomInterPred.Identity;
        return (cpi, x);
    }

    /// <summary>av1_first_pass.</summary>
    private void FirstPass(AomFrameBuffer source, AomFrameBuffer? lastSource, int frameType, long tsDuration)
    {
        var c = _cfg;
        int bd = c.BitDepth;
        int qindex = FindQindex(FIRST_PASS_Q, bd, 0, 255);
        bool intraOnly = frameType == KEY_FRAME;
        if (intraOnly)
        {
            // av1_set_screen_content_options (standard detection)
            var (sct, ibc, isSc) = AomEncoder.EstimateScreenContentStandard(source);
            _lapScreenContent = isSc;
            _lapAllowIntrabc = ibc;
        }
        int fpBsize = _lapScreenContent ? BLOCK_8X8 : BLOCK_16X16;
        int miRows = ((c.Height + 7) & ~7) >> 2, miCols = ((c.Width + 7) & ~7) >> 2;
        int mbRows = (miRows + 2) >> 2, mbCols = (miCols + 2) >> 2;
        int unitRows = FpUnitRows(fpBsize, mbRows), unitCols = FpUnitCols(fpBsize, mbCols);
        var mbStats = new FpFrameStats[unitRows * unitCols];
        for (int i = 0; i < mbStats.Length; i++) mbStats[i].ImageDataStartRow = INVALID_ROW;
        var rawMotionErrList = new int[unitRows * unitCols];
        var lastFrame = intraOnly ? null : _lapMap[0];
        var goldenFrame = intraOnly ? null : _lapMap[3];
        var last2Frame = intraOnly ? null : _lapMap[1];
        var (cpi, x) = SetupFirstPassFrame(source, frameType, qindex, fpBsize);
        var cm = cpi.Cm;
        var thisFrame = cm.CurFrame;
        foreach (var td in cpi.TileData)
        {
            td.FirstpassTopMv = default;
            var tile = td.Tile;
            x.E.SetTile(tile);
            int unitHeight = MiSizeHigh[fpBsize], unitHeightLog2 = MiSizeHighLog2[fpBsize];
            for (int miRow = tile.MiRowStart; miRow < tile.MiRowEnd; miRow += unitHeight)
                FirstPassRow(cpi, x, td, miRow >> unitHeightLog2, fpBsize, qindex, thisFrame, lastFrame, goldenFrame, last2Frame, lastSource, mbStats, rawMotionErrList,
                    intraOnly, mbCols, mbRows);
        }

        // accumulate_frame_stats
        var stats = new FpFrameStats { ImageDataStartRow = INVALID_ROW };
        for (int i = 0; i < mbStats.Length; i++)
        {
            ref var m = ref mbStats[i];
            stats.BrightnessFactor += m.BrightnessFactor;
            stats.CodedError += m.CodedError;
            stats.FrameAvgWaveletEnergy += m.FrameAvgWaveletEnergy;
            if (stats.ImageDataStartRow == INVALID_ROW && m.ImageDataStartRow != INVALID_ROW) stats.ImageDataStartRow = m.ImageDataStartRow;
            stats.InterCount += m.InterCount;
            stats.IntraError += m.IntraError;
            stats.IntraFactor += m.IntraFactor;
            stats.IntraSkipCount += m.IntraSkipCount;
            stats.MvCount += m.MvCount;
            stats.NeutralCount += m.NeutralCount;
            stats.NewMvCount += m.NewMvCount;
            stats.SecondRefCount += m.SecondRefCount;
            stats.SrCodedError += m.SrCodedError;
            stats.LtCodedError += m.LtCodedError;
            stats.SumInVectors += m.SumInVectors;
            stats.SumMvc += m.SumMvc;
            stats.SumMvcAbs += m.SumMvcAbs;
            stats.SumMvcs += m.SumMvcs;
            stats.SumMvr += m.SumMvr;
            stats.SumMvrAbs += m.SumMvrAbs;
            stats.SumMvrs += m.SumMvrs;
        }
        int totalRawMotionErrCount = intraOnly ? 0 : unitRows * unitCols;
        double rawErrStdev = RawMotionErrorStdev(rawMotionErrList, totalRawMotionErrCount);
        if (stats.ImageDataStartRow > unitRows / 2 || stats.ImageDataStartRow == INVALID_ROW) stats.ImageDataStartRow = unitRows / 2;
        if (stats.ImageDataStartRow > 0)
            stats.IntraSkipCount = Math.Max(0, stats.IntraSkipCount - stats.ImageDataStartRow * unitCols * 2);
        int numMbs16 = mbRows * mbCols;
        int numMbs = FpNumMbs(fpBsize, numMbs16);
        stats.IntraFactor /= numMbs;
        stats.BrightnessFactor /= numMbs;
        var fs = UpdateFirstpassStats(stats, rawErrStdev, _lapFrameNumber, tsDuration, fpBsize, numMbs16, cm.Width, cm.Height);
        if (fs.PcntInter < 0.2 && last2Frame != null) _lapMap[1] = _lapMap[3];
        if (_twopass.SrUpdateLag > 3 || (_lapFrameNumber > 0 && fs.PcntInter > 0.20 && fs.IntraError / (fs.CodedError + 0.000001) > 2.0))
        {
            if (goldenFrame != null) _lapMap[3] = _lapMap[0];
            _twopass.SrUpdateLag = 1;
        }
        else ++_twopass.SrUpdateLag;
        if (AomTrace.Out != null)
            for (int r = 0; r < c.Height; r += 16)
            {
                ulong h = 0;
                for (int rr = r; rr < r + 16 && rr < c.Height; rr++)
                    for (int cc = 0; cc < c.Width; cc++)
                        h = h * 31 + (thisFrame.Hbd ? thisFrame.Buffers16[0][thisFrame.Offsets[0] + rr * thisFrame.Strides[0] + cc] : (ulong)thisFrame.Buffers[0][thisFrame.Offsets[0] + rr * thisFrame.Strides[0] + cc]);
                AomTrace.Out.Write($"fprec {r} {h:x16}" + (char)10);
            }
        AomResize.ExtendFrameBorders(thisFrame);
        _lapMap[0] = thisFrame;
        if (_lapFrameNumber == 0)
        {
            _lapMap[3] = _lapMap[0];
            _lapMap[1] = _lapMap[0];
        }
        ++_lapFrameNumber;
    }

    private static double RawMotionErrorStdev(int[] list, int count)
    {
        if (count == 0) return 0;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += list[i];
        double avg = (double)sum / count;
        double stdev = 0;
        for (int i = 0; i < count; i++) stdev += (list[i] - avg) * (list[i] - avg);
        return Math.Sqrt(stdev / count);
    }

    /// <summary>update_firstpass_stats (LAP: pushed into firstpass_info, the stats buffer used linearly).</summary>
    private AomFpStats UpdateFirstpassStats(in FpFrameStats stats, double rawErrStdev, int frameNumber, long tsDuration, int fpBsize, int numMbs16,
        int width, int height)
    {
        int numMbs = FpNumMbs(fpBsize, numMbs16);
        double minErr = 200 * Math.Sqrt(numMbs);
        var fps = new AomFpStats();
        fps.Weight = stats.IntraFactor * stats.BrightnessFactor;
        fps.Frame = frameNumber;
        fps.CodedError = (stats.CodedError >> 8) + minErr;
        fps.SrCodedError = (stats.SrCodedError >> 8) + minErr;
        fps.LtCodedError = (stats.LtCodedError >> 8) + minErr;
        fps.IntraError = (stats.IntraError >> 8) + minErr;
        fps.FrameAvgWaveletEnergy = stats.FrameAvgWaveletEnergy;
        fps.Count = 1.0;
        fps.PcntInter = (double)stats.InterCount / numMbs;
        fps.PcntSecondRef = (double)stats.SecondRefCount / numMbs;
        fps.PcntNeutral = stats.NeutralCount / numMbs;
        fps.IntraSkipPct = (double)stats.IntraSkipCount / numMbs;
        fps.InactiveZoneRows = stats.ImageDataStartRow;
        fps.InactiveZoneCols = 0.0;
        fps.RawErrorStdev = rawErrStdev;
        fps.IsFlash = 0;
        fps.NoiseVar = 0.0;
        fps.CorCoeff = 1.0;
        fps.LogCodedError = 0.0;
        fps.LogIntraError = 0.0;
        if (stats.MvCount > 0)
        {
            fps.MVr = (double)stats.SumMvr / stats.MvCount;
            fps.MvrAbs = (double)stats.SumMvrAbs / stats.MvCount;
            fps.MVc = (double)stats.SumMvc / stats.MvCount;
            fps.MvcAbs = (double)stats.SumMvcAbs / stats.MvCount;
            fps.MVrv = ((double)stats.SumMvrs - ((double)stats.SumMvr * stats.SumMvr / stats.MvCount)) / stats.MvCount;
            fps.MVcv = ((double)stats.SumMvcs - ((double)stats.SumMvc * stats.SumMvc / stats.MvCount)) / stats.MvCount;
            fps.MvInOutCount = (double)stats.SumInVectors / (stats.MvCount * 2);
            fps.NewMvCount = stats.NewMvCount;
            fps.PcntMotion = (double)stats.MvCount / numMbs;
        }
        fps.Duration = tsDuration;
        // normalize_firstpass_stats
        double n = numMbs16;
        fps.CodedError /= n;
        fps.SrCodedError /= n;
        fps.LtCodedError /= n;
        fps.IntraError /= n;
        fps.FrameAvgWaveletEnergy /= n;
        fps.LogCodedError = Math.Log(1 + fps.CodedError);
        fps.LogIntraError = Math.Log(1 + fps.IntraError);
        fps.MVr /= height;
        fps.MvrAbs /= height;
        fps.MVc /= width;
        fps.MvcAbs /= width;
        fps.MVrv /= (double)height * height;
        fps.MVcv /= (double)width * width;
        fps.NewMvCount /= n;
        AomTrace.Out?.Write(FormattableString.Invariant(
            $"fpst {fps.Frame:F6} {fps.Weight:F6} {fps.IntraError:F6} {fps.CodedError:F6} {fps.SrCodedError:F6} {fps.LtCodedError:F6} {fps.PcntInter:F6} {fps.PcntMotion:F6} {fps.PcntSecondRef:F6} {fps.PcntNeutral:F6} {fps.IntraSkipPct:F6} {fps.InactiveZoneRows:F6} {fps.MVr:F6} {fps.MvrAbs:F6} {fps.MVc:F6} {fps.MvcAbs:F6} {fps.MVrv:F6} {fps.MVcv:F6} {fps.MvInOutCount:F6} {fps.NewMvCount:F6} {fps.RawErrorStdev:F6}")
            + (char)10);

        _twopass.Buf[_twopass.InEnd] = fps;
        _twopass.FirstpassInfo.Push(fps);
        _twopass.TotalStats.Accumulate(fps);
        _twopass.InEnd++;
        if (_twopass.InEnd >= _twopass.InBufEnd)
        {
            int numValid = _twopass.InEnd - _twopass.StatsIn;
            if (numValid > 0) Array.Copy(_twopass.Buf, _twopass.StatsIn, _twopass.Buf, _twopass.InStart, numValid);
            _twopass.StatsIn = _twopass.InStart;
            _twopass.InEnd = _twopass.InStart + numValid;
        }
        return fps;
    }

    private static int GetFpBsize(int miRows, int miCols, int fpBsize, int unitRow, int unitCol)
    {
        int unitWidth = MiSizeWide[fpBsize], unitHeight = MiSizeHigh[fpBsize];
        bool halfW = unitWidth * unitCol + unitWidth / 2 >= miCols;
        bool halfH = unitHeight * unitRow + unitHeight / 2 >= miRows;
        int maxDim = Math.Max(BlockSizeWide[fpBsize], BlockSizeHigh[fpBsize]);
        int sq = maxDim switch { 4 => 0, 8 => 1, 16 => 2, 32 => 3, 64 => 4, _ => 5 };
        if (halfW && halfH) return SubsizeLookup[PARTITION_SPLIT * 6 + sq];
        if (halfW) return SubsizeLookup[PARTITION_VERT * 6 + sq];
        if (halfH) return SubsizeLookup[PARTITION_HORZ * 6 + sq];
        return fpBsize;
    }

    /// <summary>av1_first_pass_row.</summary>
    private void FirstPassRow(AomComp cpi, AomMacroblock x, AomTileDataEnc td, int unitRow, int fpBsize, int qindex, AomFrameBuffer thisFrame,
        AomFrameBuffer? lastFrame, AomFrameBuffer? goldenFrame, AomFrameBuffer? last2Frame, AomFrameBuffer? lastSource, FpFrameStats[] mbStats, int[] rawMotionErrList, bool intraOnly, int mbCols, int mbRows)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var tile = td.Tile;
        int numPlanes = cm.NumPlanes;
        int fpW = BlockSizeWide[fpBsize], fpH = BlockSizeHigh[fpBsize];
        int unitWidth = MiSizeWide[fpBsize], unitWidthLog2 = MiSizeWideLog2[fpBsize], unitHeightLog2 = MiSizeHighLog2[fpBsize];
        int unitCols = mbCols * 4 / unitWidth;
        int rawMotionErrCounts = 0;
        int unitRowInTile = unitRow - (tile.MiRowStart >> unitHeightLog2);
        int unitColStart = tile.MiColStart >> unitWidthLog2;
        int unitColsInTile = (tile.MiColEnd - tile.MiColStart + (1 << unitWidthLog2) - 1) >> unitWidthLog2;
        int statsBase = unitRow * unitCols + unitColStart;
        int ssX = cm.SsX, ssY = cm.SsY;
        int uvMbWidth = fpW >> ssX, uvMbHeight = fpH >> ssY;   // y_width > uv_width: the chroma subsampling
        var bestRefMv = default(AomMv);
        var lastMv = default(AomMv);
        xd.UpAvailable = unitRowInTile != 0;
        int reconYoffset = unitRow * thisFrame.Strides[0] * fpH + unitColStart * fpW;
        int srcYoffset = unitRow * cpi.Source.Strides[0] * fpH + unitColStart * fpW;
        int reconUvoffset = numPlanes > 1 ? unitRow * thisFrame.Strides[1] * uvMbHeight + unitColStart * uvMbWidth : 0;
        // av1_set_mv_row_limits
        {
            int miRow = unitRow << unitHeightLog2, miHeight = fpH >> 2, border = cpi.BorderInPixels;
            x.MvLimits.RowMin = Math.Max(-(miRow * 4 + border - 8), -((miRow + miHeight) * 4 + 8));
            x.MvLimits.RowMax = Math.Min((cm.MiRows - miRow - miHeight) * 4 + border - 8, (cm.MiRows - miRow) * 4 + 8);
        }
        Array.Clear(x.Plane[0].SrcDiff, 0, 256);
        for (int uc = 0; uc < unitColsInTile; uc++)
        {
            int unitCol = unitColStart + uc;
            if (uc == 0) lastMv = td.FirstpassTopMv;
            ref var st = ref mbStats[statsBase + uc];
            int thisIntraError = FirstpassIntraPrediction(cpi, x, thisFrame, unitRow, unitCol, reconYoffset, reconUvoffset, fpBsize, qindex, ref st);
            if (!intraOnly)
            {
                int thisInterError = FirstpassInterPrediction(cpi, x, lastFrame!, goldenFrame, last2Frame, lastSource!, unitRow, unitCol, reconYoffset, reconUvoffset, srcYoffset,
                    fpBsize, thisIntraError, rawMotionErrList, statsBase + rawMotionErrCounts, bestRefMv, ref bestRefMv, ref lastMv, ref st, mbCols, mbRows);
                if (uc == 0) td.FirstpassTopMv = lastMv;
                st.CodedError += thisInterError;
                ++rawMotionErrCounts;
            }
            else
            {
                st.SrCodedError += thisIntraError;
                st.CodedError += thisIntraError;
                st.LtCodedError += thisIntraError;
            }
            reconYoffset += fpW;
            srcYoffset += fpW;
            reconUvoffset += uvMbWidth;
        }
    }

    /// <summary>set_mi_offsets + the xd plane / block setup of firstpass_intra_prediction.</summary>
    private static void FpSetBlock(AomComp cpi, AomMacroblock x, AomFrameBuffer thisFrame, int miRow, int miCol, int bsize, int reconYoffset,
        int reconUvoffset, int fpBsize)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int gridIdx = miRow * cm.MiStride + miCol;
        cm.MiGridBase[gridIdx] = cm.MiAlloc(gridIdx);
        xd.MiGrid = cm.MiGridBase;
        xd.MiStride = cm.MiStride;
        xd.MiOffset = gridIdx;
        xd.Mi0 = cm.MiAlloc(gridIdx);
        xd.TxTypeMap = cm.TxTypeMap;
        xd.TxTypeMapOffset = gridIdx;
        xd.TxTypeMapStride = cm.MiStride;
        for (int p = 0; p < cm.NumPlanes; p++)
        {
            ref var d = ref xd.Plane[p].Dst;
            d.Buf = thisFrame.Buffers[p]; d.Buf16 = thisFrame.Buffers16[p];
            d.Offset0 = thisFrame.Offsets[p];
            d.Offset = thisFrame.Offsets[p] + (p == 0 ? reconYoffset : reconUvoffset);
            d.Stride = thisFrame.Strides[p];
            d.Width = thisFrame.CropWidths[p > 0 ? 1 : 0]; d.Height = thisFrame.CropHeights[p > 0 ? 1 : 0];
        }
        // the source planes at the unit (av1_setup_src_planes at the row start, advanced per unit)
        AomEncodeFrame.SetupSrcPlanes(cpi, x, miRow, miCol, cm.NumPlanes, fpBsize);
    }

    /// <summary>firstpass_intra_prediction.</summary>
    private int FirstpassIntraPrediction(AomComp cpi, AomMacroblock x, AomFrameBuffer thisFrame, int unitRow, int unitCol, int reconYoffset,
        int reconUvoffset, int fpBsize, int qindex, ref FpFrameStats stats)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int unitScale = MiSizeWide[fpBsize];
        int numPlanes = cm.NumPlanes;
        int bsize = GetFpBsize(cm.MiRows, cm.MiCols, fpBsize, unitRow, unitCol);
        int miRow = unitRow * unitScale, miCol = unitCol * unitScale;
        FpSetBlock(cpi, x, thisFrame, miRow, miCol, bsize, reconYoffset, reconUvoffset, fpBsize);
        var mbmi = xd.Mi0;
        mbmi.Bsize = (byte)bsize;
        mbmi.RefFrame0 = INTRA_FRAME;
        AomEncodeFrame.SetMiRowCol(xd, cm, miRow, MiSizeHigh[bsize], miCol, MiSizeWide[bsize]);
        for (int i = 0; i < numPlanes; i++)
        {
            var pd = xd.Plane[i];
            pd.Width = Math.Max((MiSizeWide[bsize] * 4) >> pd.SubsamplingX, 4);
            pd.Height = Math.Max((MiSizeHigh[bsize] * 4) >> pd.SubsamplingY, 4);
        }
        mbmi.SegmentId = 0;
        xd.Lossless[0] = qindex == 0 ? 1 : 0;
        mbmi.Mode = DC_PRED;
        mbmi.TxSize = TX_4X4;
        mbmi.SkipTxfm = 0;
        if (cpi.Sf.fp_sf.disable_recon != 0) FirstPassPredictIntraBlockForLumaPlane(cpi, x, bsize);
        else AomIntraModeSearch.EncodeIntraBlockPlane(cpi, x, bsize, 0, AomEncodeFrame.DRY_RUN_NORMAL, 0);
        int thisIntraError = GetMbSs(x.Plane[0].SrcDiff);
        if (cm.BitDepth == 10) thisIntraError >>= 4;
        else if (cm.BitDepth == 12) thisIntraError >>= 8;
        AomTrace.Out?.Write($"fpb {unitRow} {unitCol} {thisIntraError}" + (char)10);
        if (thisIntraError < UL_INTRA_THRESH) ++stats.IntraSkipCount;
        else if (unitCol > 0 && stats.ImageDataStartRow == INVALID_ROW) stats.ImageDataStartRow = unitRow;
        double logIntra = LogOnePlus(thisIntraError);
        if (logIntra < 10.0) stats.IntraFactor += 1.0 + ((10.0 - logIntra) * 0.05);
        else stats.IntraFactor += 1.0;
        var src = x.Plane[0].Src;
        int levelSample = src.Buf16 != null ? src.Buf16[src.Offset] : src.Buf[src.Offset];
        if (cm.BitDepth == 10) levelSample >>= 2;
        else if (cm.BitDepth == 12) levelSample >>= 4;
        if (levelSample < DARK_THRESH && logIntra < 9.0) stats.BrightnessFactor += 1.0 + (0.01 * (DARK_THRESH - levelSample));
        else stats.BrightnessFactor += 1.0;
        thisIntraError += INTRA_MODE_PENALTY;
        stats.IntraError += thisIntraError;
        stats.FrameAvgWaveletEnergy = -1;   // INVALID_FP_STATS_TO_PREDICT_FLAT_GOP (deltaq_mode != DELTA_Q_PERCEPTUAL)
        return thisIntraError;
    }

    /// <summary>log1p.</summary>
    private static double LogOnePlus(int v) => Math.Log(1.0 + v);

    /// <summary>aom_get_mb_ss: the sum of squares of the first 256 residuals.</summary>
    private static int GetMbSs(short[] a)
    {
        int sum = 0;
        for (int i = 0; i < 256; i++) sum += a[i] * a[i];
        return sum;
    }

    /// <summary>first_pass_predict_intra_block_for_luma_plane (fp_sf.disable_recon): DC prediction per 4x4 from the
    /// source's neighbours into the recon, the residual, then the source copied into the recon.</summary>
    private static void FirstPassPredictIntraBlockForLumaPlane(AomComp cpi, AomMacroblock x, int bsize)
    {
        var xd = x.E;
        var pd = xd.Plane[0];
        var p = x.Plane[0];
        int planeBsize = bsize;
        int txSize = TX_4X4;
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, 0), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, 0);
        int muBlocksWide = Math.Min(MiSizeWide[BLOCK_64X64], maxBlocksWide), muBlocksHigh = Math.Min(MiSizeHigh[BLOCK_64X64], maxBlocksHigh);
        var src = p.Src;
        var dst = pd.Dst;
        bool hbd = xd.IsHbd;
        for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
        {
            int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
            for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
            {
                int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                for (int blkRow = r; blkRow < unitHeight; blkRow++)
                    for (int blkCol = c; blkCol < unitWidth; blkCol++)
                    {
                        int dOff = dst.Offset + ((blkRow * dst.Stride + blkCol) << 2);
                        int sOff = src.Offset + ((blkRow * src.Stride + blkCol) << 2);
                        if (hbd)
                            AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, pd.Width, pd.Height, txSize, DC_PRED, 0, false,
                                FILTER_INTRA_MODES, src.Buf16, sOff, src.Stride, dst.Buf16, dOff, dst.Stride, blkCol, blkRow, 0);
                        else
                            AomReconIntra.PredictIntraBlock(xd, cpi.SbSize, cpi.EnableIntraEdgeFilter, pd.Width, pd.Height, txSize, DC_PRED, 0, false,
                                FILTER_INTRA_MODES, src.Buf, sOff, src.Stride, dst.Buf, dOff, dst.Stride, blkCol, blkRow, 0);
                        AomEncodeMb.SubtractTxb(x, 0, planeBsize, blkCol, blkRow, txSize);
                    }
            }
        }
        int bw = BlockSizeWide[bsize], bh = BlockSizeHigh[bsize];
        for (int r = 0; r < bh; r++)
            if (hbd) Array.Copy(src.Buf16, src.Offset + r * src.Stride, dst.Buf16, dst.Offset + r * dst.Stride, bw);
            else Array.Copy(src.Buf, src.Offset + r * src.Stride, dst.Buf, dst.Offset + r * dst.Stride, bw);
    }

    /// <summary>get_prediction_error_bitdepth: aom_[highbd_N_]mse{8x8,16x8,8x16,16x16} (the sse).</summary>
    private static int FpPredictionError(int bsize, in AomBuf2d s, int sOff, in AomBuf2d r, int rOff, int bd)
    {
        int w = 16, h = 16;
        if (bsize == BLOCK_8X8) { w = 8; h = 8; }
        else if (bsize == BLOCK_16X8) { w = 16; h = 8; }
        else if (bsize == BLOCK_8X16) { w = 8; h = 16; }
        long sse = 0;
        if (s.Buf16 != null)
        {
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++) { int d = s.Buf16[sOff + i * s.Stride + j] - r.Buf16[rOff + i * r.Stride + j]; sse += d * d; }
            if (bd == 10) sse = (sse + 8) >> 4;
            else if (bd == 12) sse = (sse + 128) >> 8;
            return (int)(uint)sse;
        }
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++) { int d = s.Buf[sOff + i * s.Stride + j] - r.Buf[rOff + i * r.Stride + j]; sse += d * d; }
        return (int)(uint)sse;
    }

    /// <summary>first_pass_motion_search.</summary>
    private void FirstPassMotionSearch(AomComp cpi, AomMacroblock x, AomMv refMv, ref AomMv bestMv, ref int bestMotionErr)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var startMv = refMv.ToFullMv();
        int bsize = xd.Mi0.Bsize;
        int dim = Math.Max(Math.Min(cm.Width, cm.Height), 4);
        int sr = 0;
        while ((dim << sr) < MAX_FULL_PEL_VAL) ++sr;
        int stepParam = cpi.Sf.fp_sf.reduce_mv_step_param + sr;
        bool fineSearchInterval = _lapScreenContent && _lapAllowIntrabc;
        var p = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, refMv, cpi.SearchSites, NSTEP, fineSearchInterval, x.MvLimits, startMv);
        AomMv dummy = default;
        int tmpErr = AomMcomp.FullPixelSearch(startMv, p, stepParam, null, out var thisBestMv, out _, ref dummy, false);
        if (tmpErr < int.MaxValue)
        {
            // av1_get_mvpred_sse: the bsize variance's sse + the mv cost
            var src = p.Src; var rf = p.Ref;
            int rOff = rf.Offset + thisBestMv.Row * rf.Stride + thisBestMv.Col;
            int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
            uint sse;
            if (src.Buf16 != null) AomHbd.Variance(src.Buf16, src.Offset, src.Stride, rf.Buf16!, rOff, rf.Stride, 0, w, h, xd.Bd, out sse);
            else AomSad.Variance(src.Buf, src.Offset, src.Stride, rf.Buf, rOff, rf.Stride, w, h, out sse);
            tmpErr = (int)sse + AomMcomp.MvErrCost(p, thisBestMv.ToMv()) + NEW_MV_MODE_PENALTY;
        }
        if (tmpErr < bestMotionErr)
        {
            bestMotionErr = tmpErr;
            bestMv = thisBestMv;
        }
    }

    private static void SetPre0(AomMacroblockD xd, AomFrameBuffer f, int plane, int off)
    {
        ref var pre = ref xd.Plane[plane].Pre(0);
        pre.Buf = f.Buffers[plane]; pre.Buf16 = f.Buffers16[plane];
        pre.Offset0 = f.Offsets[plane];
        pre.Offset = f.Offsets[plane] + off;
        pre.Stride = f.Strides[plane];
        pre.Width = f.CropWidths[plane > 0 ? 1 : 0]; pre.Height = f.CropHeights[plane > 0 ? 1 : 0];
    }

    /// <summary>firstpass_inter_prediction.</summary>
    private int FirstpassInterPrediction(AomComp cpi, AomMacroblock x, AomFrameBuffer lastFrame, AomFrameBuffer? goldenFrame,
        AomFrameBuffer? last2Frame, AomFrameBuffer lastSource, int unitRow, int unitCol,
        int reconYoffset, int reconUvoffset, int srcYoffset, int fpBsize, int thisIntraError, int[] rawMotionErrList, int rawIdx, AomMv refMv,
        ref AomMv bestMvOut, ref AomMv lastNonZeroMv, ref FpFrameStats stats, int mbCols, int mbRows)
    {
        int thisInterError = thisIntraError;
        var cm = cpi.Cm;
        var xd = x.E;
        int bd = xd.Bd;
        int unitScale = MiSizeWide[fpBsize];
        int bsize = GetFpBsize(cm.MiRows, cm.MiCols, fpBsize, unitRow, unitCol);
        int fpH = BlockSizeWide[fpBsize];
        int unitWidth = MiSizeWide[fpBsize];
        int unitRows = FpUnitRows(fpBsize, mbRows), unitCols = FpUnitCols(fpBsize, mbCols);
        var mv = default(AomMv);
        SetPre0(xd, lastFrame, 0, reconYoffset);
        // av1_set_mv_col_limits
        {
            int miCol = unitCol * unitWidth, miWidth = fpH >> 2, border = cpi.BorderInPixels;
            x.MvLimits.ColMin = Math.Max(-(miCol * 4 + border - 8), -((miCol + miWidth) * 4 + 8));
            x.MvLimits.ColMax = Math.Min((cm.MiCols - miCol - miWidth) * 4 + border - 8, (cm.MiCols - miCol) * 4 + 8);
        }
        var src = x.Plane[0].Src;
        var pre = xd.Plane[0].Pre(0);
        int motionError = FpPredictionError(bsize, src, src.Offset, pre, pre.Offset, bd);
        var ls = new AomBuf2d { Buf = lastSource.Buffers[0], Buf16 = lastSource.Buffers16[0], Stride = lastSource.Strides[0] };
        int rawMotionError = FpPredictionError(bsize, src, src.Offset, ls, lastSource.Offsets[0] + srcYoffset, bd);
        rawMotionErrList[rawIdx] = rawMotionError;
        var fpSf = cpi.Sf.fp_sf;
        if (rawMotionError > fpSf.skip_motion_search_threshold)
        {
            FirstPassMotionSearch(cpi, x, refMv, ref mv, ref motionError);
            if (fpSf.skip_zeromv_motion_search == 0 && (refMv.Row != 0 || refMv.Col != 0))
            {
                var tmpMv = default(AomMv);
                int tmpErr = int.MaxValue;
                FirstPassMotionSearch(cpi, x, default, ref tmpMv, ref tmpErr);
                if (tmpErr < motionError)
                {
                    motionError = tmpErr;
                    mv = tmpMv;
                }
            }
        }
        if (_lapFrameNumber > 2 && last2Frame != null)
        {
            var tmpMv = default(AomMv);
            SetPre0(xd, last2Frame, 0, reconYoffset);
            pre = xd.Plane[0].Pre(0);
            int last2MotionError = FpPredictionError(bsize, src, src.Offset, pre, pre.Offset, bd);
            FirstPassMotionSearch(cpi, x, default, ref tmpMv, ref last2MotionError);
            stats.LtCodedError += Math.Min(last2MotionError, thisIntraError);
        }
        int gfMotionError = motionError;
        if (_lapFrameNumber > 1 && goldenFrame != null)
        {
            var tmpMv = default(AomMv);
            SetPre0(xd, goldenFrame, 0, reconYoffset);
            pre = xd.Plane[0].Pre(0);
            gfMotionError = FpPredictionError(bsize, src, src.Offset, pre, pre.Offset, bd);
            FirstPassMotionSearch(cpi, x, default, ref tmpMv, ref gfMotionError);
        }
        if (gfMotionError < motionError && gfMotionError < thisIntraError) ++stats.SecondRefCount;
        if (_lapFrameNumber > 1 && goldenFrame != null) stats.SrCodedError += Math.Min(gfMotionError, thisIntraError);
        else stats.SrCodedError += motionError;
        SetPre0(xd, lastFrame, 0, reconYoffset);
        if (cm.NumPlanes > 1)
        {
            SetPre0(xd, lastFrame, 1, reconUvoffset);
            SetPre0(xd, lastFrame, 2, reconUvoffset);
        }
        bestMvOut = default;
        if (motionError <= thisIntraError)
        {
            if ((thisIntraError - INTRA_MODE_PENALTY) * 9 <= motionError * 10 && thisIntraError < 2 * INTRA_MODE_PENALTY)
                stats.NeutralCount += 1.0;
            else if (thisIntraError > NCOUNT_INTRA_THRESH && thisIntraError < NCOUNT_INTRA_FACTOR * motionError)
                stats.NeutralCount += (double)motionError / ((double)thisIntraError + 0.000001);   // DOUBLE_DIVIDE_CHECK
            var best = mv.ToMv();
            bestMvOut = best;
            thisInterError = motionError;
            var mbmi = xd.Mi0;
            mbmi.Mode = NEWMV;
            mbmi.Mv0 = best;
            mbmi.TxSize = TX_4X4;
            mbmi.RefFrame0 = LAST_FRAME;
            mbmi.RefFrame1 = NONE_FRAME;
            if (fpSf.disable_recon == 0)
            {
                // the predictor addresses pre[0].buf0, which the LAST2 / GOLDEN searches' av1_setup_pre_planes left at
                // that frame (the reset to LAST only moves pre[0].buf)
                var buf0 = _lapFrameNumber > 1 && goldenFrame != null ? goldenFrame : _lapFrameNumber > 2 && last2Frame != null ? last2Frame : lastFrame;
                SetPre0(xd, buf0, 0, reconYoffset);
                AomInterPred.EncBuildInterPredictor(cm, xd, unitRow * unitScale, unitCol * unitScale, null, bsize, 0, 0, cpi.EnableIntraEdgeFilter);
                EncodeSbyPass1(cpi, x, bsize);
            }
            stats.SumMvr += best.Row;
            stats.SumMvrAbs += Math.Abs(best.Row);
            stats.SumMvc += best.Col;
            stats.SumMvcAbs += Math.Abs(best.Col);
            stats.SumMvrs += best.Row * best.Row;
            stats.SumMvcs += best.Col * best.Col;
            ++stats.InterCount;
            // accumulate_mv_stats
            if (best.Row != 0 || best.Col != 0)
            {
                ++stats.MvCount;
                if (best.AsInt != lastNonZeroMv.AsInt) ++stats.NewMvCount;
                lastNonZeroMv = best;
                if (unitRow < unitRows / 2) { if (mv.Row > 0) --stats.SumInVectors; else if (mv.Row < 0) ++stats.SumInVectors; }
                else if (unitRow > unitRows / 2) { if (mv.Row > 0) ++stats.SumInVectors; else if (mv.Row < 0) --stats.SumInVectors; }
                if (unitCol < unitCols / 2) { if (mv.Col > 0) --stats.SumInVectors; else if (mv.Col < 0) ++stats.SumInVectors; }
                else if (unitCol > unitCols / 2) { if (mv.Col > 0) ++stats.SumInVectors; else if (mv.Col < 0) --stats.SumInVectors; }
            }
        }
        AomTrace.Out?.Write($"fpi {unitRow} {unitCol} {thisInterError} {motionError} {bestMvOut.Row} {bestMvOut.Col}" + (char)10);
        return thisInterError;
    }

    /// <summary>av1_encode_sby_pass1: the luma residual, DCT_DCT transform / quantisation (B quant) and reconstruction per tx block.</summary>
    private static void EncodeSbyPass1(AomComp cpi, AomMacroblock x, int bsize)
    {
        var xd = x.E;
        var pd = xd.Plane[0];
        var p = x.Plane[0];
        AomEncodeMb.SubtractPlane(x, bsize, 0);
        int txSize = AomEncodeMb.GetTxSize(0, xd);
        int txwUnit = TxSizeWideUnit[txSize], txhUnit = TxSizeHighUnit[txSize], step = txwUnit * txhUnit;
        int maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bsize, 0), maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bsize, 0);
        int muBlocksWide = Math.Min(MiSizeWide[BLOCK_64X64], maxBlocksWide), muBlocksHigh = Math.Min(MiSizeHigh[BLOCK_64X64], maxBlocksHigh);
        int block = 0;
        for (int r = 0; r < maxBlocksHigh; r += muBlocksHigh)
        {
            int unitHeight = Math.Min(muBlocksHigh + r, maxBlocksHigh);
            for (int c = 0; c < maxBlocksWide; c += muBlocksWide)
            {
                int unitWidth = Math.Min(muBlocksWide + c, maxBlocksWide);
                for (int blkRow = r; blkRow < unitHeight; blkRow += txhUnit)
                    for (int blkCol = c; blkCol < unitWidth; blkCol += txwUnit)
                    {
                        var qp = AomEncodeMb.SetupQuant(txSize, false, AomXformQuant.B, cpi.QuantBAdapt);
                        AomEncodeMb.SetupQmatrix(x, 0, txSize, DCT_DCT, ref qp);
                        AomEncodeMb.Xform(x, 0, block, blkRow, blkCol, bsize, txSize, DCT_DCT);
                        AomEncodeMb.Quant(x, 0, block, txSize, DCT_DCT, qp);
                        int eob = p.Eobs[block];
                        if (eob > 0)
                        {
                            int dstOff = pd.Dst.Offset + ((blkRow * pd.Dst.Stride + blkCol) << 2);
                            AomEncodeMb.InverseTransformBlockDst(p.Dqcoeff, AomEncodeMb.BlockOffset(block), DCT_DCT, txSize, pd.Dst, dstOff, pd.Dst.Stride, eob,
                                xd.Bd, xd.Lossless[xd.Mi0.SegmentId] != 0);
                        }
                        block += step;
                    }
            }
        }
    }
}
