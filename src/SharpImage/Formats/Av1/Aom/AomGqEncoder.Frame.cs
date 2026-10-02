using System;
using System.Collections.Generic;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomGqEncoder
{
    private const int AOM_LAST_FLAG = 1, AOM_LAST2_FLAG = 2, AOM_LAST3_FLAG = 4, AOM_GOLD_FLAG = 8, AOM_BWD_FLAG = 16, AOM_ALT2_FLAG = 32,
        AOM_ALT_FLAG = 64, AOM_REFFRAME_ALL = 127;

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
    /// <summary>features->allow_screen_content_tools / allow_intrabc and cpi->is_screen_content_type of the last
    /// intra frame (inter frames keep them).</summary>
    private bool _sct, _isScreenContentType;
    /// <summary>ppi->filter_level.</summary>
    private readonly int[] _ppiFilterLevel = new int[4];

    /// <summary>The frame's place in the GF group (gf_group update_type / layer_depth / frame_type).</summary>
    private readonly record struct GfFrame(int UpdateType, int LayerDepth, int FrameType, bool RefbufReset);

    /// <summary>aom_codec_encode with lag_in_frames 0: the frame is coded immediately; returns its packet(s).</summary>
    public List<AomGqPacket> Encode(AomGqFrameInput f)
    {
        if (_cfg.LagInFrames != 0) throw new NotImplementedException("lagged encoding");
        // libavif sets AOME_SET_CQ_LEVEL / AV1E_SET_LOSSLESS when the quality changes; their av1_change_config resets
        // cm's frame size (and mi grid) to the configured size before the next encode
        if (_frameNumber > 0 && f.Quantizer != _prevQuantizer)
        {
            _miRows = ((_cfg.Height + 7) & ~7) >> 2;
            _miCols = ((_cfg.Width + 7) & ~7) >> 2;
        }
        _prevQuantizer = f.Quantizer;
        if (f.ScaleModeH != 0 || f.ScaleModeV != 0)
        {
            SetInternalSize(f.ScaleModeH, f.ScaleModeV);
            _resizeModeFixed = true;   // av1_set_internal_size: resize_mode = RESIZE_FIXED (and the TPL model off)
        }
        var unscaled = MakeSource(f);
        var packets = new List<AomGqPacket>();
        bool forceKf = (f.Flags & AOM_EFLAG_FORCE_KF) != 0;
        var gf = NextGfFrame(forceKf);
        byte[] data = EncodeOneFrame(unscaled, f, gf);
        // av1_cx_iface.c: a temporal delimiter before the first frame of a temporal unit (spatial layer 0)
        if (f.SpatialLayerId == 0)
        {
            var withTd = new byte[data.Length + 2];
            AomBitstream.TemporalDelimiter.CopyTo(withTd, 0);
            data.CopyTo(withTd, 2);
            data = withTd;
        }
        packets.Add(new AomGqPacket(data, gf.FrameType == KEY_FRAME));
        _frameNumber++;
        return packets;
    }

    /// <summary>The GF group position of the next frame without lookahead (av1_gop_setup_structure with
    /// max_layer_depth_allowed 0: the frames after the key frame are LF_UPDATE frames whose layer depth is
    /// set_ld_layer_depth's for the group length).</summary>
    private GfFrame NextGfFrame(bool forceKf)
    {
        if (_frameNumber == 0 || forceKf)
        {
            _gfIndex = 0;
            return new GfFrame(KF_UPDATE, 0, KEY_FRAME, true);
        }
        _gfIndex++;
        if (_cfg.NumSpatialLayers <= 1) throw new NotImplementedException("image sequence GF groups");
        // layered image: one GF group of g_limit frames (baseline_gf_interval = the layer count)
        int gopLength = _cfg.Limit;
        int logGopLength = 0;
        while ((1 << logGopLength) < gopLength) ++logGopLength;
        int count = 0;
        for (; count < MAX_ARF_LAYERS; ++count) if (((_gfIndex >> count) & 1) != 0) break;
        return new GfFrame(LF_UPDATE, Math.Max(logGopLength - count, 0), INTER_FRAME, false);
    }

    private int _gfIndex, _prevQuantizer;
    /// <summary>oxcf->resize_cfg.resize_mode != RESIZE_NONE (av1_is_resize_needed: the encoder border is
    /// AOM_BORDER_IN_PIXELS).</summary>
    private bool _resizeModeFixed;

    /// <summary>av1_encode_strategy + av1_encode + encode_frame_to_data_rate + av1_post_encode_updates for one frame.</summary>
    private byte[] EncodeOneFrame(AomFrameBuffer unscaled, AomGqFrameInput f, GfFrame gf)
    {
        var cfg = _cfg;
        int frameType = gf.FrameType;
        bool isKey = frameType == KEY_FRAME;
        // av1_setup_frame_size: the pending AOME_SET_SCALEMODE size, else the configured size
        int width = cfg.Width, height = cfg.Height;
        if (_resizePendingW != 0 && _resizePendingH != 0) { width = _resizePendingW; height = _resizePendingH; _resizePendingW = _resizePendingH = 0; }

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

        // av1_apply_encoding_flags: the external reference / refresh flags
        long fl = f.Flags;
        int extRefFlags = AOM_REFFRAME_ALL;
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
            extRefFlags = r;
        }
        bool updatePending = (fl & (AOM_EFLAG_NO_UPD_LAST | AOM_EFLAG_NO_UPD_GF | AOM_EFLAG_NO_UPD_ARF)) != 0;
        int upd = AOM_REFFRAME_ALL;
        if ((fl & AOM_EFLAG_NO_UPD_LAST) != 0) upd ^= AOM_LAST_FLAG;
        if ((fl & AOM_EFLAG_NO_UPD_GF) != 0) upd ^= AOM_GOLD_FLAG;
        if ((fl & AOM_EFLAG_NO_UPD_ARF) != 0) { upd ^= AOM_ALT_FLAG; upd ^= AOM_BWD_FLAG; upd ^= AOM_ALT2_FLAG; }
        bool useRefFrameMvs = (fl & AOM_EFLAG_NO_REF_FRAME_MVS) == 0;
        int updateType = gf.UpdateType;
        bool refreshGolden = isKey;
        if (updatePending)
        {
            if ((upd & AOM_GOLD_FLAG) != 0) updateType = GF_UPDATE;
            if ((upd & AOM_ALT_FLAG) != 0) updateType = ARF_UPDATE;
            if ((upd & AOM_BWD_FLAG) != 0) updateType = INTNL_ARF_UPDATE;
            refreshGolden = (upd & AOM_GOLD_FLAG) != 0;
        }
        if (isKey) updateType = KF_UPDATE;

        // av1_get_ref_frames, get_ref_frame_flags, choose_primary_ref_frame, av1_get_refresh_frame_flags
        int curFrameDisp = _frameNumber;
        int refFrameFlags = 0, primaryRefFrame = PRIMARY_REF_NONE, refreshFrameFlags;
        if (isKey)
        {
            for (int i = 0; i < REF_FRAMES; i++) _remappedRefIdx[i] = 0;
            refreshFrameFlags = 0xff;
        }
        else
        {
            GetRefFrames(curFrameDisp);
            var refBufsPrio = new AomRefBuffer?[INTER_REFS_PER_FRAME];
            for (int i = 0; i < INTER_REFS_PER_FRAME; i++) refBufsPrio[i] = _refFrameMap[_remappedRefIdx[RefFramePriorityOrder[i] - LAST_FRAME]];
            refFrameFlags = extRefFlags;
            for (int i = 1; i < INTER_REFS_PER_FRAME; ++i)
                for (int j = 0; j < i; ++j)
                    if (refBufsPrio[i] == refBufsPrio[j] && (refFrameFlags & (1 << (RefFramePriorityOrder[j] - 1))) != 0)
                    {
                        refFrameFlags &= ~(1 << (RefFramePriorityOrder[i] - 1));
                        break;
                    }
            int currentRefType = CurrentFrameRefType(gf.LayerDepth);
            int wantedFb = _fbOfContextType[currentRefType];
            for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++)
                if (_remappedRefIdx[r - LAST_FRAME] == wantedFb) primaryRefFrame = r - LAST_FRAME;
            refreshFrameFlags = GetRefreshFrameFlags(updatePending, upd, updateType, curFrameDisp);
        }

        // rate control: AOM_Q with use_fixed_qp_offsets 2 -> q = cq_level
        int qindex = QuantizerToQindex[f.Quantizer];
        var tune = f.Quantizer == 0 ? AomTune.Psnr : cfg.Tune;
        var refBufs = new AomRefBuffer?[REF_FRAMES];
        if (!isKey) for (int r = LAST_FRAME; r <= ALTREF_FRAME; r++) refBufs[r] = _refFrameMap[_remappedRefIdx[r - LAST_FRAME]];
        var primaryRefBuf = primaryRefFrame != PRIMARY_REF_NONE ? refBufs[primaryRefFrame + LAST_FRAME] : null;
        var input = new AomEncodeInput
        {
            Width = width, Height = height, SsX = cfg.Monochrome ? 1 : cfg.SsX, SsY = cfg.Monochrome ? 1 : cfg.SsY, Monochrome = cfg.Monochrome,
            BitDepth = cfg.BitDepth, Mode = cfg.Usage, Speed = cfg.Speed, Tune = tune, Threads = Math.Min(cfg.Threads, 64),
            TileColumns = cfg.TileColumnsLog2, TileRows = cfg.TileRowsLog2, SourceFrame = source, UnfilteredSource = unscaled,
            BaseQindex = qindex, UpdateType = updateType, GfFrameType = frameType, LayerDepth = gf.LayerDepth,
            SbSize = _seq.SbSize, SeqFlags = _seqFlags, TxTypeProbs = _txTypeProbs, EnableRestoration = cfg.EnableRestoration,
            Sharpness = cfg.Sharpness, EnableCdef = cfg.EnableCdef, UseFixedQpOffsets = cfg.UseFixedQpOffsets,
            SsimSource = unscaled, SsimMiRows = _miRows, SsimMiCols = _miCols, SsimBuffer = _ssimFactors,
            Seq = _seq, ShowFrame = true, OrderHint = _frameNumber & ((1 << (_seq.OrderHintBitsMinus1 + 1)) - 1), DisplayOrderHint = _frameNumber,
            FrameNumber = _frameNumber, RefBufs = refBufs, RefFrameFlags = refFrameFlags, UseRefFrameMvs = useRefFrameMvs,
            PrimaryRefBuf = primaryRefBuf, FrameProbs = _frameProbs, FilterScaler = filterScaler, PhaseScaler = phaseScaler,
            LagInFrames = cfg.LagInFrames, NumSpatialLayers = cfg.NumSpatialLayers, RefreshGolden = refreshGolden, PpiFilterLevel = _ppiFilterLevel, ResizeNeeded = _resizeModeFixed,
        };
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
        var fh = new AomGqFrameHeader
        {
            Seq = _seq, FrameType = frameType, ShowFrame = true, OrderHint = input.OrderHint,
            PrimaryRefFrame = primaryRefFrame, RefreshFrameFlags = refreshFrameFlags, SpatialLayerId = f.SpatialLayerId,
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
        byte[] data = AomBitstream.PackFrameGq(cpi, fh, out int largestTileId, BitstreamTrace);
        OnFrameEncoded?.Invoke(cpi, fh);

        // post-encode: the reconstruction into every refreshed slot, with the frame context of the largest tile
        var buf = new AomRefBuffer
        {
            Buf = cm.CurFrame, Width = width, Height = height, RenderWidth = cfg.Width, RenderHeight = cfg.Height,
            OrderHint = fh.OrderHint, DisplayOrderHint = _frameNumber, FrameType = frameType, BaseQindex = qindex,
            MiRows = cm.MiRows, MiCols = cm.MiCols, PyramidLevel = gf.LayerDepth, Mvs = cm.CurFrameMvs, InterpFilter = cm.InterpFilter,
        };
        for (int i = 0; i < INTER_REFS_PER_FRAME; i++) { buf.RefOrderHints[i] = cm.CurRefOrderHints[i]; buf.RefDisplayOrderHint[i] = cpi.RefDisplayOrderHint[i]; }
        for (int i = LAST_FRAME; i <= ALTREF_FRAME; i++) buf.GlobalMotion[i].CopyFrom(cm.GlobalMotion[i]);
        Array.Copy(cpi.PostFilter!.LoopFilter.FrameFilterLevel, buf.FilterLevel, 2);
        // update_end_of_frame_stats: ppi->filter_level from the frame's searched (backup) levels
        Array.Copy(cpi.PostFilter.LoopFilter.BackupFilterLevel, _ppiFilterLevel, 4);
        Array.Copy(cpi.PostFilter!.LoopFilter.RefDeltas, buf.RefDeltas, 8);
        Array.Copy(cpi.PostFilter.LoopFilter.ModeDeltas, buf.ModeDeltas, 2);
        AomResize.ExtendFrameBorders(buf.Buf);
        buf.FrameContext = new Av1CdfContext();
        buf.FrameContext.CopyFrom(cpi.TileData[largestTileId].Tctx);
        buf.FrameContext.ResetCounters();
        for (int i = 0; i < REF_FRAMES; i++)
            if (((fh.RefreshFrameFlags >> i) & 1) != 0) _refFrameMap[i] = buf;

        // update_fb_of_context_type
        int refType = CurrentFrameRefType(gf.LayerDepth);
        if (isKey)
        {
            for (int i = 0; i < REF_FRAMES; i++) _fbOfContextType[i] = -1;
            _fbOfContextType[refType] = 0;
        }
        else
            for (int i = 0; i < REF_FRAMES; i++)
                if ((refreshFrameFlags & (1 << i)) != 0) { _fbOfContextType[refType] = i; break; }

        _seqParamsLocked = true;
        _miRows = cm.MiRows;
        _miCols = cm.MiCols;
        return data;
    }

    /// <summary>get_current_frame_ref_type.</summary>
    private static int CurrentFrameRefType(int layerDepth) => layerDepth switch
    {
        0 => 0, 1 => 1, MAX_ARF_LAYERS or MAX_ARF_LAYERS + 1 => 4, _ => 7,
    };

    /// <summary>av1_get_refresh_frame_flags (no ducky encode / external rate control / RTC reference structure).</summary>
    private int GetRefreshFrameFlags(bool updatePending, int upd, int updateType, int curDisp)
    {
        int refreshMask = 0;
        if (updatePending)
        {
            // is_frame_droppable
            if ((upd & (AOM_LAST_FLAG | AOM_GOLD_FLAG | AOM_ALT_FLAG | AOM_BWD_FLAG | AOM_ALT2_FLAG)) == 0) return 0;
            refreshMask |= ((upd & AOM_LAST_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[LAST_FRAME - LAST_FRAME];
            refreshMask |= ((upd & AOM_BWD_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[BWDREF_FRAME - LAST_FRAME];   // EXTREF_FRAME
            refreshMask |= ((upd & AOM_ALT2_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[ALTREF2_FRAME - LAST_FRAME];
            if (updateType == OVERLAY_UPDATE)
                refreshMask |= ((upd & AOM_GOLD_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[ALTREF_FRAME - LAST_FRAME];
            else
            {
                refreshMask |= ((upd & AOM_GOLD_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[GOLDEN_FRAME - LAST_FRAME];
                refreshMask |= ((upd & AOM_ALT_FLAG) != 0 ? 1 : 0) << _remappedRefIdx[ALTREF_FRAME - LAST_FRAME];
            }
            return refreshMask;
        }
        throw new NotImplementedException("refresh slot selection without external refresh flags");
    }

    /// <summary>init_ref_map_pair + av1_get_ref_frames.</summary>
    private void GetRefFrames(int curFrameDisp)
    {
        // init_ref_map_pair
        Span<int> dispOrder = stackalloc int[REF_FRAMES];
        Span<int> pyrLevel = stackalloc int[REF_FRAMES];
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

        int[] remapped = _remappedRefIdx;
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
        // qsort by display order (distinct after the duplicate removal)
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
        // set_unmapped_ref
        if (nBufs > ALTREF_FRAME)
        {
            int maxDist = 0, unmappedIdx = -1;
            for (int i = 0; i < nBufs; i++)
            {
                if (used[i]) continue;
                if (lvl[i] != minLevel || nMinLevelRefs >= 5)   // LOW_LEVEL_FRAMES_TR
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
}
