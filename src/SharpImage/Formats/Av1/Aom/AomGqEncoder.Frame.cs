using System;
using System.Collections.Generic;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomGqEncoder
{
    /// <summary>A trace sink for frame-level decisions (test hook).</summary>
    public Action<AomComp, AomGqFrameHeader>? OnFrameEncoded;

    /// <summary>aom_codec_encode with lag_in_frames 0: the frame is coded immediately; returns its packet(s).</summary>
    public List<AomGqPacket> Encode(AomGqFrameInput f)
    {
        if (_cfg.LagInFrames != 0) throw new NotImplementedException("lagged encoding");
        if (f.ScaleModeH != 0 || f.ScaleModeV != 0) SetInternalSize(f.ScaleModeH, f.ScaleModeV);
        var unscaled = MakeSource(f);
        var packets = new List<AomGqPacket>();
        bool forceKf = (f.Flags & AOM_EFLAG_FORCE_KF) != 0;
        int frameType = _frameNumber == 0 || forceKf ? KEY_FRAME : INTER_FRAME;
        if (frameType != KEY_FRAME) throw new NotImplementedException("inter frames");
        byte[] data = EncodeOneFrame(unscaled, f, frameType);
        // av1_cx_iface.c: a temporal delimiter before the first frame of a temporal unit (spatial layer 0)
        if (f.SpatialLayerId == 0)
        {
            var withTd = new byte[data.Length + 2];
            AomBitstream.TemporalDelimiter.CopyTo(withTd, 0);
            data.CopyTo(withTd, 2);
            data = withTd;
        }
        packets.Add(new AomGqPacket(data, frameType == KEY_FRAME));
        _frameNumber++;
        return packets;
    }

    /// <summary>av1_encode_strategy + av1_encode + encode_frame_to_data_rate + av1_post_encode_updates for one frame.</summary>
    private byte[] EncodeOneFrame(AomFrameBuffer unscaled, AomGqFrameInput f, int frameType)
    {
        var cfg = _cfg;
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

        // rate control: AOM_Q with use_fixed_qp_offsets 2 -> q = cq_level; good quality, one pass, no lag
        int qindex = QuantizerToQindex[f.Quantizer];
        bool isKey = frameType == KEY_FRAME;
        var tune = f.Quantizer == 0 ? AomTune.Psnr : cfg.Tune;
        var input = new AomEncodeInput
        {
            Width = width, Height = height, SsX = cfg.Monochrome ? 1 : cfg.SsX, SsY = cfg.Monochrome ? 1 : cfg.SsY, Monochrome = cfg.Monochrome,
            BitDepth = cfg.BitDepth, Mode = cfg.Usage, Speed = cfg.Speed, Tune = tune, Threads = Math.Min(cfg.Threads, 64),
            TileColumns = cfg.TileColumnsLog2, TileRows = cfg.TileRowsLog2, SourceFrame = source, UnfilteredSource = unscaled,
            BaseQindex = qindex, UpdateType = isKey ? KF_UPDATE : LF_UPDATE, GfFrameType = frameType, LayerDepth = isKey ? 0 : 1,
            SbSize = _seq.SbSize, SeqFlags = _seqFlags, TxTypeProbs = _txTypeProbs, EnableRestoration = cfg.EnableRestoration,
            Sharpness = cfg.Sharpness, EnableCdef = cfg.EnableCdef, UseFixedQpOffsets = cfg.UseFixedQpOffsets,
            SsimSource = unscaled, SsimMiRows = _miRows, SsimMiCols = _miCols, SsimBuffer = _ssimFactors,
        };
        _seqFlags.SeqParamsLocked = _seqParamsLocked;
        var (cpi, x) = AomEncoder.EncodeFrame(input);
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

        var fh = new AomGqFrameHeader
        {
            Seq = _seq, FrameType = frameType, ShowFrame = true, OrderHint = _frameNumber & ((1 << (_seq.OrderHintBitsMinus1 + 1)) - 1),
            PrimaryRefFrame = PRIMARY_REF_NONE, RefreshFrameFlags = isKey ? 0xff : 0, SpatialLayerId = f.SpatialLayerId,
            UpscaledWidth = width, UpscaledHeight = height, RenderWidth = cfg.Width, RenderHeight = cfg.Height,
        };
        for (int i = 0; i < REF_FRAMES; i++) { fh.RemappedRefIdx[i] = _remappedRefIdx[i]; fh.RefFrameMap[i] = _refFrameMap[i]; }
        byte[] data = AomBitstream.PackFrameGq(cpi, fh, out int largestTileId);
        OnFrameEncoded?.Invoke(cpi, fh);

        // post-encode: the reconstruction into every refreshed slot, with the frame context of the largest tile
        var buf = new AomRefBuffer
        {
            Buf = cpi.Cm.CurFrame, Width = width, Height = height, RenderWidth = cfg.Width, RenderHeight = cfg.Height,
            OrderHint = fh.OrderHint, DisplayOrderHint = _frameNumber, FrameType = frameType, BaseQindex = qindex,
            MiRows = cpi.Cm.MiRows, MiCols = cpi.Cm.MiCols,
        };
        AomResize.ExtendFrameBorders(buf.Buf);
        buf.FrameContext = new Av1CdfContext();
        buf.FrameContext.CopyFrom(cpi.TileData[largestTileId].Tctx);
        for (int i = 0; i < REF_FRAMES; i++)
            if (((fh.RefreshFrameFlags >> i) & 1) != 0) _refFrameMap[i] = buf;
        _seqParamsLocked = true;
        _miRows = cpi.Cm.MiRows;
        _miCols = cpi.Cm.MiCols;
        return data;
    }
}
