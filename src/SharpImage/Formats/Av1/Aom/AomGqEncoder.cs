using System;
using System.Collections.Generic;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>The aom_codec_enc_cfg_t / av1_extracfg settings libavif 1.4.2 (codec_aom.c) gives libaom for layered
/// images and image sequences.</summary>
internal sealed class AomGqConfig
{
    public int Width, Height, SsX = 1, SsY = 1, BitDepth = 8;
    public bool Monochrome;
    /// <summary>cpu-used; usage GOOD below 7, REALTIME from 7 (libavif's mapping for non-still images).</summary>
    public int Speed = 6;
    public int Usage => Speed >= 7 ? REALTIME : GOOD;
    public AomTune Tune = AomTune.Ssim;
    public int Threads = 1;
    public int TileColumnsLog2, TileRowsLog2;
    /// <summary>g_lag_in_frames (libaom default 35 for good quality; libavif 0 for layered images / alpha).</summary>
    public int LagInFrames = 35;
    /// <summary>kf_max_dist (libaom default 9999; libavif sets the keyframe interval when given).</summary>
    public int KfMaxDist = 9999;
    /// <summary>g_limit (0 = unlimited; libavif: number of layers for layered images).</summary>
    public int Limit;
    /// <summary>AOME_SET_NUMBER_SPATIAL_LAYERS.</summary>
    public int NumSpatialLayers = 1;
    /// <summary>cfg.use_fixed_qp_offsets (libavif: 2 for layered images in AOM_Q).</summary>
    public int UseFixedQpOffsets;
    /// <summary>AV1E_SET_ENABLE_RESTORATION (libavif: 0 for 12-bit).</summary>
    public bool EnableRestoration = true;
    public AomSequenceConfig Color = new();
    public int? Sharpness;
    public bool? EnableCdef;
}

/// <summary>One frame handed to the encoder (aom_codec_encode): full-resolution planes and the per-frame controls
/// libavif sets (cq-level, AOME_SET_SPATIAL_LAYER_ID, AOME_SET_SCALEMODE, encode flags).</summary>
internal sealed class AomGqFrameInput
{
    public byte[][]? Planes;
    public ushort[][]? Planes16;
    public int[] Strides = null!;
    /// <summary>cq-level quantizer 0..63 (AOME_SET_CQ_LEVEL; 0 = lossless).</summary>
    public int Quantizer;
    public int SpatialLayerId;
    /// <summary>AOME_SET_SCALEMODE h / v (AOME_NORMAL 0 = not set).</summary>
    public int ScaleModeH, ScaleModeV;
    /// <summary>AOM_EFLAG_* flags.</summary>
    public long Flags;
}

/// <summary>A compressed frame packet (aom_codec_cx_pkt_t AOM_CODEC_CX_FRAME_PKT).</summary>
internal sealed record AomGqPacket(byte[] Data, bool IsKey);

/// <summary>
/// Port of libaom 3.14.1's encoder for the configurations libavif 1.4.2 uses for layered images and sequences
/// (AOM_USAGE_GOOD_QUALITY / AOM_USAGE_REALTIME): av1_receive_raw_frame, av1_get_compressed_data,
/// av1_encode_strategy, av1_encode, encode_frame_to_data_rate and the post-encode reference updates, writing the
/// same packets aom_codec_get_cx_data returns.
/// </summary>
internal sealed partial class AomGqEncoder
{
    private const long AOM_EFLAG_FORCE_KF = 1 << 0;
    private const long AOM_EFLAG_NO_REF_LAST = 1 << 16, AOM_EFLAG_NO_REF_LAST2 = 1 << 17, AOM_EFLAG_NO_REF_LAST3 = 1 << 18,
        AOM_EFLAG_NO_REF_GF = 1 << 19, AOM_EFLAG_NO_REF_ARF = 1 << 20, AOM_EFLAG_NO_REF_BWD = 1 << 21, AOM_EFLAG_NO_REF_ARF2 = 1 << 22,
        AOM_EFLAG_NO_UPD_LAST = 1 << 23, AOM_EFLAG_NO_UPD_GF = 1 << 24, AOM_EFLAG_NO_UPD_ARF = 1 << 25, AOM_EFLAG_NO_UPD_ENTROPY = 1 << 26,
        AOM_EFLAG_NO_REF_FRAME_MVS = 1 << 27, AOM_EFLAG_ERROR_RESILIENT = 1 << 28, AOM_EFLAG_SET_S_FRAME = 1 << 29,
        AOM_EFLAG_SET_PRIMARY_REF_NONE = 1 << 30;
    public const long FlagsLayerUpper = AOM_EFLAG_NO_REF_GF | AOM_EFLAG_NO_REF_ARF | AOM_EFLAG_NO_REF_BWD | AOM_EFLAG_NO_REF_ARF2 |
        AOM_EFLAG_NO_UPD_GF | AOM_EFLAG_NO_UPD_ARF;
    public const long FlagForceKf = AOM_EFLAG_FORCE_KF;

    private static readonly int[] QuantizerToQindex =
    {
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100,
        104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 148, 152, 156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204,
        208, 212, 216, 220, 224, 228, 232, 236, 240, 244, 249, 255,
    };

    private readonly AomGqConfig _cfg;
    private readonly AomSeqHeader _seq;
    private readonly AomSpeedFeatureSeqFlags _seqFlags = new();
    private bool _seqParamsLocked;
    /// <summary>cm->ref_frame_map.</summary>
    private readonly AomRefBuffer?[] _refFrameMap = new AomRefBuffer?[REF_FRAMES];
    /// <summary>cm->remapped_ref_idx.</summary>
    private readonly int[] _remappedRefIdx = new int[REF_FRAMES];
    /// <summary>ppi->frame_probs (persistent across frames).</summary>
    private readonly int[] _txTypeProbs = new int[DefaultTxTypeProbs.Length];   // ppi is zeroed: copy_frame_prob_info fills what the speed features use
    private int _frameNumber;       // cm->current_frame.frame_number
    private int _resizePendingW, _resizePendingH;
    private bool _pendingTu;          // a packet of the current temporal unit was output (no new TD)
    /// <summary>cm->mi_params rows / cols before the frame size is set up (the previous frame's; initially the
    /// configured size's), and cpi->ssim_rdmult_scaling_factors (allocated for the configured size).</summary>
    private int _miRows, _miCols;
    private readonly double[] _ssimFactors;

    public AomGqEncoder(AomGqConfig cfg)
    {
        _cfg = cfg;
        _seq = InitSequence(cfg);
        _seqFlags.enable_restoration = _seq.EnableRestoration ? 1 : 0;
        _miRows = ((cfg.Height + 7) & ~7) >> 2;
        _miCols = ((cfg.Width + 7) & ~7) >> 2;
        _ssimFactors = new double[((_miRows + 3) / 4) * ((_miCols + 3) / 4)];

        // av1_create_primary_compressor / av1_create_compressor / av1_change_config: rate control and look-ahead
        int numLapBuffers = Math.Min(cfg.LagInFrames, Math.Min(48, cfg.KfMaxDist + SCENE_CUT_KEY_TEST_INTERVAL));
        int lapLagInFrames = cfg.LagInFrames - numLapBuffers >= 17 ? 17 : 0;
        _lapEnabled = numLapBuffers > 0;
        _kfKeyFreqMax = cfg.KfMaxDist;
        _kfKeyFreqMin = 0;
        _kfAutoKey = cfg.KfMaxDist != 0;
        _framesLeft = cfg.Limit;
        _rc.WorstQuality = 255;
        _rc.BestQuality = 0;
        _cmWidth = cfg.Width;
        _cmHeight = cfg.Height;
        _pRc.EnableScenecutDetection = ENABLE_SCENECUT_MODE_2;
        if (_lapEnabled)
        {
            if (numLapBuffers < MAX_GF_LENGTH_LAP + SCENE_CUT_KEY_TEST_INTERVAL + 1 && numLapBuffers >= MAX_GF_LENGTH_LAP + 3)
                _pRc.EnableScenecutDetection = ENABLE_SCENECUT_MODE_1;
            else if (numLapBuffers < MAX_GF_LENGTH_LAP + 3) _pRc.EnableScenecutDetection = DISABLE_SCENECUT;
        }
        RcInit();
        NewFramerate(_initFramerate);
        LookaheadInit(_lapEnabled ? lapLagInFrames : cfg.LagInFrames, numLapBuffers);
        int statsBufSize = numLapBuffers > 0 ? Math.Max(numLapBuffers + 1, MAX_GF_LENGTH_LAP + 1) : 48;
        _twopass.Buf = new AomFpStats[statsBufSize];
        _twopass.InStart = _twopass.InEnd = _twopass.StatsIn = 0;
        _twopass.InBufEnd = statsBufSize;
        _twopass.TotalStats = AomFpStats.Zeroed();
        _twopass.TotalLeftStats = AomFpStats.Zeroed();
        _twopass.FirstpassInfo.Init();
        if (_lapEnabled) InitSinglePassLap();
    }

    public AomSeqHeader Sequence => _seq;

    /// <summary>init_config_sequence / init_seq_coding_tools / av1_set_svc_seq_params / set_bitstream_level_tier.</summary>
    private static AomSeqHeader InitSequence(AomGqConfig cfg)
    {
        var seq = new AomSeqHeader
        {
            BitDepth = cfg.BitDepth, SsX = cfg.Monochrome ? 1 : cfg.SsX, SsY = cfg.Monochrome ? 1 : cfg.SsY, Monochrome = cfg.Monochrome,
            Color = cfg.Color,
        };
        // libavif's profile: 0 for 4:2:0 / monochrome, 1 for 4:4:4, 2 for 4:2:2 and 12-bit
        seq.Profile = cfg.BitDepth == 12 ? 2 : cfg.Monochrome || (cfg.SsX == 1 && cfg.SsY == 1) ? 0 : cfg.SsX == 0 && cfg.SsY == 0 ? 1 : 2;
        seq.StillPicture = cfg.Limit == 1;
        seq.ReducedStillPictureHdr = seq.StillPicture;
        seq.EnableOrderHint = !seq.ReducedStillPictureHdr;
        seq.OrderHintBitsMinus1 = seq.EnableOrderHint ? DEFAULT_EXPLICIT_ORDER_HINT_BITS - 1 : -1;
        seq.MaxFrameWidth = cfg.Width;
        seq.MaxFrameHeight = cfg.Height;
        seq.NumBitsWidth = cfg.Width > 1 ? Log2Floor(cfg.Width - 1) + 1 : 1;
        seq.NumBitsHeight = cfg.Height > 1 ? Log2Floor(cfg.Height - 1) + 1 : 1;
        seq.EnableDistWtdComp &= seq.EnableOrderHint;
        seq.EnableRefFrameMvs &= seq.EnableOrderHint;
        // cdef_control: CDEF_ALL by default outside all-intra, CDEF_ADAPTIVE with tune iq; -a enable-cdef overrides
        seq.EnableCdef = cfg.EnableCdef ?? true;
        seq.EnableRestoration = cfg.EnableRestoration;
        seq.SbSize = SelectSbSize(cfg);
        // operating points: spatial x temporal layers
        int nsl = cfg.NumSpatialLayers, ntl = 1;
        seq.OperatingPointsCntMinus1 = nsl > 1 || ntl > 1 ? nsl * ntl - 1 : 0;
        int level = BitstreamLevel(cfg.Width, cfg.Height, cfg.Color.FrameRate);
        for (int i = 0; i < 32; i++) seq.SeqLevelIdx[i] = level;
        if (seq.OperatingPointsCntMinus1 == 0) { seq.OperatingPointIdc[0] = 0; seq.HasNonzeroOperatingPointIdc = false; }
        else
        {
            int i = 0;
            for (int sl = 0; sl < nsl; sl++)
                for (int tl = 0; tl < ntl; tl++)
                    seq.OperatingPointIdc[i++] = (int)((~(~0u << (nsl - sl)) << 8) | ~(~0u << (ntl - tl)));
            seq.HasNonzeroOperatingPointIdc = true;
        }
        seq.FilmGrainParamsPresent = cfg.Color.FilmGrain != null;
        return seq;
    }

    private static int Log2Floor(int v) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    /// <summary>set_bitstream_level_tier (no target level).</summary>
    private static int BitstreamLevel(int width, int height, double fps)
    {
        static bool Match(int w, int h, double fps, int lw, int lh, double lfps, int mult)
        {
            long lvlLumaPels = (long)lw * lh;
            double lvlDisplaySampleRate = lvlLumaPels * lfps;
            long lumaPels = (long)w * h;
            double displaySampleRate = lumaPels * fps;
            return lumaPels <= lvlLumaPels && displaySampleRate <= lvlDisplaySampleRate && w <= lw * mult && h <= lh * mult;
        }
        if (Match(width, height, fps, 512, 288, 30.0, 4)) return 0;
        if (Match(width, height, fps, 704, 396, 30.0, 4)) return 1;
        if (Match(width, height, fps, 1088, 612, 30.0, 4)) return 4;
        if (Match(width, height, fps, 1376, 774, 30.0, 4)) return 5;
        if (Match(width, height, fps, 2048, 1152, 30.0, 3)) return 8;
        if (Match(width, height, fps, 2048, 1152, 60.0, 3)) return 9;
        if (Match(width, height, fps, 4096, 2176, 30.0, 2)) return 12;
        if (Match(width, height, fps, 4096, 2176, 60.0, 2)) return 13;
        if (Match(width, height, fps, 4096, 2176, 120.0, 2)) return 14;
        if (Match(width, height, fps, 8192, 4352, 30.0, 2)) return 16;
        if (Match(width, height, fps, 8192, 4352, 60.0, 2)) return 17;
        if (Match(width, height, fps, 8192, 4352, 120.0, 2)) return 18;
        return 31;
    }

    /// <summary>av1_select_sb_size (AOM_SUPERBLOCK_SIZE_DYNAMIC, no resize mode / superres, row_mt 1).</summary>
    private static int SelectSbSize(AomGqConfig cfg)
    {
        if (cfg.Tune == AomTune.Iq) return BLOCK_64X64;   // DELTA_Q_VARIANCE_BOOST
        if (cfg.NumSpatialLayers > 1) return BLOCK_64X64;
        int width = cfg.Width, height = cfg.Height;
        if (cfg.Usage == REALTIME) return Math.Min(width, height) > 720 ? BLOCK_128X128 : BLOCK_64X64;
        bool is480pOrLesser = Math.Min(width, height) <= 480;
        if (cfg.Speed >= 1 && is480pOrLesser) return BLOCK_64X64;
        bool is1080pOrLesser = Math.Min(width, height) <= 1080;
        if (!is480pOrLesser && is1080pOrLesser && cfg.Threads > 1 && cfg.Speed >= 5) return BLOCK_64X64;
        return BLOCK_128X128;
    }

    /// <summary>av1_set_internal_size: the pending frame size of an AOME_SET_SCALEMODE.</summary>
    private void SetInternalSize(int horizMode, int vertMode)
    {
        static (int hr, int hs) Ratio(int mode) => mode switch
        {
            1 => (4, 5), 2 => (3, 5), 3 => (3, 4), 4 => (1, 4), 5 => (1, 8), 6 => (1, 2), 7 => (2, 3), 8 => (1, 3), _ => (1, 1),
        };
        var (hr, hs) = Ratio(horizMode);
        var (vr, vs) = Ratio(vertMode);
        _resizePendingW = (hs - 1 + _cfg.Width * hr) / hs;
        _resizePendingH = (vs - 1 + _cfg.Height * vr) / vs;
    }

    /// <summary>The full-resolution source of a frame with libaom's replicated borders (the lookahead's copy).</summary>
    private AomFrameBuffer MakeSource(AomGqFrameInput f)
    {
        var c = _cfg;
        var src = new AomFrameBuffer(c.Width, c.Height, c.SsX, c.SsY, c.Monochrome, c.BitDepth);
        for (int p = 0; p < src.NumPlanes; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int w = src.CropWidths[isUv], h = src.CropHeights[isUv];
            if (c.BitDepth > 8)
                for (int r = 0; r < h; r++) Array.Copy(f.Planes16![p], r * f.Strides[p], src.Buffers16[p], src.Offsets[p] + r * src.Strides[p], w);
            else
                for (int r = 0; r < h; r++) Array.Copy(f.Planes![p], r * f.Strides[p], src.Buffers[p], src.Offsets[p] + r * src.Strides[p], w);
        }
        AomResize.ExtendFrameBorders(src);
        return src;
    }
}
