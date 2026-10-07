using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;
// LOCAL oracle-diff harness for the good-quality / real-time libaom port. Mirrors aomoracle_gq:
// GQ_YUV GQ_W GQ_H GQ_FRAMES GQ_SPEED GQ_Q="q0,q1" GQ_LAYERS GQ_SCALE="s0,s1" GQ_TUNE GQ_BD GQ_FMT GQ_THREADS GQ_OUT=<prefix>
// It encodes a developer-supplied YUV file and writes trace files for an external diff against libaom, so on its own it
// checks nothing: [Explicit] keeps it out of the normal (CI) run; select it by name with the GQ_* variables set.
public sealed class AomGqSmoke
{
    private static int ListAt(string? s, int i, int def)
    {
        if (string.IsNullOrEmpty(s)) return def;
        var parts = s.Split(',');
        return int.Parse(parts[Math.Min(i, parts.Length - 1)]);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
        ? v : throw new InvalidOperationException($"{name} is not set (see the GQ_* list above)");

    [Test, Explicit]
    public async Task EncodesSequence()
    {
        string yuvPath = Required("GQ_YUV");
        int w = int.Parse(Required("GQ_W")), h = int.Parse(Required("GQ_H"));
        int frames = int.Parse(Environment.GetEnvironmentVariable("GQ_FRAMES") ?? "1");
        int speed = int.Parse(Environment.GetEnvironmentVariable("GQ_SPEED") ?? "6");
        string? qs = Environment.GetEnvironmentVariable("GQ_Q"), scs = Environment.GetEnvironmentVariable("GQ_SCALE");
        int layers = int.Parse(Environment.GetEnvironmentVariable("GQ_LAYERS") is { Length: > 0 } l ? l : "1");
        string fmt = Environment.GetEnvironmentVariable("GQ_FMT") ?? "420";
        bool alpha = Environment.GetEnvironmentVariable("GQ_ALPHA") == "1";
        bool is444 = fmt == "444", is422 = fmt == "422", mono = fmt == "mono" || alpha;
        int bd = int.Parse(Environment.GetEnvironmentVariable("GQ_BD") is { Length: > 0 } b ? b : "8");
        int cw = is444 ? w : (w + 1) / 2, ch = is444 || is422 ? h : (h + 1) / 2;
        var tune = Environment.GetEnvironmentVariable("GQ_TUNE") switch
        {
            "iq" => AomTune.Iq, "ssim" => AomTune.Ssim, "psnr" => AomTune.Psnr,
            _ => alpha ? AomTune.Psnr : layers > 1 ? AomTune.Iq : AomTune.Ssim,
        };
        var cfg = new AomGqConfig
        {
            Width = w, Height = h, SsX = is444 ? 0 : 1, SsY = is444 || is422 ? 0 : 1, Monochrome = mono, BitDepth = bd, Speed = speed, Tune = tune,
            Threads = int.Parse(Environment.GetEnvironmentVariable("GQ_THREADS") ?? "1"),
            LagInFrames = speed >= 7 || layers > 1 || Environment.GetEnvironmentVariable("GQ_HASALPHA") == "1" || alpha ? 0 : 35,
            Limit = layers > 1 ? layers : 0, NumSpatialLayers = layers, UseFixedQpOffsets = layers > 1 ? 2 : 0, EnableRestoration = bd != 12,
            Color = new AomSequenceConfig { ColorRange = 1 },
        };
        var enc = new AomGqEncoder(cfg);
        string? bsPath = Environment.GetEnvironmentVariable("GQ_BSTRACE");
        using var bsWriter = bsPath != null ? new StreamWriter(bsPath) : null;
        enc.BitstreamTrace = bsWriter;
        string? tracePath = Environment.GetEnvironmentVariable("AOM_TRACE");
        using var traceWriter = tracePath != null ? new StreamWriter(tracePath) : null;
        AomTrace.Out = traceWriter;
        string outp = Required("GQ_OUT");
        using var dump = new StreamWriter(outp + ".txt");
        int frameNo = 0;
        enc.OnFrameEncoded = (cpi, fh) => Dump(dump, frameNo++, cpi, fh, enc.RcTraceLine);
        var all = File.ReadAllBytes(yuvPath);
        int bps = bd > 8 ? 2 : 1;
        int frameBytes = (w * h + (mono ? 0 : 2 * cw * ch)) * bps;
        using var obu = File.Create(outp + ".obu");
        using var sizes = new StreamWriter(outp + ".sizes");
        for (int fr = 0; fr < frames; fr++)
        {
            int src = layers > 1 ? 0 : fr;
            int off = src * frameBytes;
            var input = new AomGqFrameInput { Strides = mono ? new[] { w } : new[] { w, cw, cw }, Quantizer = ListAt(qs, fr, 30),
                SpatialLayerId = layers > 1 ? fr : 0, ScaleModeH = ListAt(scs, fr, 0), ScaleModeV = ListAt(scs, fr, 0),
                Flags = layers > 1 && fr > 0 ? AomGqEncoder.FlagsLayerUpper : 0 };
            if (bps == 1)
            {
                input.Planes = mono ? new[] { all.AsSpan(off, w * h).ToArray() }
                    : new[] { all.AsSpan(off, w * h).ToArray(), all.AsSpan(off + w * h, cw * ch).ToArray(), all.AsSpan(off + w * h + cw * ch, cw * ch).ToArray() };
            }
            else
            {
                var s16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(all.AsSpan(off, frameBytes));
                input.Planes16 = mono ? new[] { s16.Slice(0, w * h).ToArray() }
                    : new[] { s16.Slice(0, w * h).ToArray(), s16.Slice(w * h, cw * ch).ToArray(), s16.Slice(w * h + cw * ch, cw * ch).ToArray() };
            }
            foreach (var p in enc.Encode(input))
            {
                obu.Write(p.Data);
                sizes.WriteLine($"{p.Data.Length} {(p.IsKey ? 1 : 0)}");
            }
        }
        for (;;)
        {
            var ps = enc.Encode(null);
            if (ps.Count == 0) break;
            foreach (var p in ps)
            {
                obu.Write(p.Data);
                sizes.WriteLine($"{p.Data.Length} {(p.IsKey ? 1 : 0)}");
            }
        }
        await Assert.That(frameNo).IsGreaterThan(0);
    }

    private static void Dump(StreamWriter f, int frameNo, AomComp cpi, AomGqFrameHeader fh, string? rcLine)
    {
        var cm = cpi.Cm;
        f.WriteLine($"frame {frameNo} type {fh.FrameType} show {(fh.ShowFrame ? 1 : 0)} w {cm.Width} h {cm.Height} order {fh.OrderHint} qindex {cm.BaseQindex} refresh {fh.RefreshFrameFlags:x2} primary {fh.PrimaryRefFrame} refsel {(fh.ReferenceSelect ? 1 : 0)} skipmode {(fh.SkipModeFlag ? 1 : 0)} interp {cm.InterpFilter} warp {(fh.AllowWarpedMotion ? 1 : 0)} hp {(cm.AllowHighPrecisionMv ? 1 : 0)} swf {(cm.SwitchableMotionMode ? 1 : 0)} split {cpi.TxbSplitCount}");
        f.WriteLine($"gf rdmult {cpi.RdRdmult} sb {cm.SbSize}");
        if (rcLine != null) f.WriteLine(rcLine);
        {
            var src = cpi.Source;
            ulong hh = 1469598103934665603UL;
            for (int pl = 0; pl < cm.NumPlanes; pl++)
            {
                int isUv = pl > 0 ? 1 : 0;
                for (int r = 0; r < src.CropHeights[isUv]; r++)
                    for (int c = 0; c < src.CropWidths[isUv]; c++)
                    {
                        int v = src.Hbd ? src.Buffers16[pl][src.Offsets[pl] + r * src.Strides[pl] + c] : src.Buffers[pl][src.Offsets[pl] + r * src.Strides[pl] + c];
                        hh = (hh ^ (uint)v) * 1099511628211UL;
                    }
            }
            var sf = cpi.Sf;
            f.WriteLine($"src {hh:x16} sf {sf.intra_sf.intra_pruning_with_hog} {sf.intra_sf.chroma_intra_pruning_with_hog} {sf.part_sf.intra_cnn_based_part_prune_level} {sf.part_sf.ml_prune_partition} {(sf.tx_sf.prune_intra_tx_depths_using_nn ? 1 : 0)} {sf.rd_sf.perform_coeff_opt} {sf.tx_sf.tx_type_search.use_reduced_intra_txset} {sf.intra_sf.prune_filter_intra_level} {sf.tx_sf.intra_tx_size_search_init_depth_sqr} {sf.tx_sf.intra_tx_size_search_init_depth_rect} {sf.winner_mode_sf.multi_winner_mode_type} {sf.part_sf.partition_search_type} {sf.intra_sf.disable_smooth_intra} {sf.tx_sf.tx_type_search.fast_intra_tx_type_search} {sf.rd_sf.tx_domain_dist_level} {sf.part_sf.prune_rectangular_split_based_on_qidx}");
        }
        {
            var rb = cm.CurFrame;
            ulong hh = 1469598103934665603UL;
            for (int pl = 0; pl < cm.NumPlanes; pl++)
            {
                int isUv = pl > 0 ? 1 : 0;
                for (int r = 0; r < rb.CropHeights[isUv]; r++)
                    for (int c = 0; c < rb.CropWidths[isUv]; c++)
                    {
                        int v = rb.Hbd ? rb.Buffers16[pl][rb.Offsets[pl] + r * rb.Strides[pl] + c] : rb.Buffers[pl][rb.Offsets[pl] + r * rb.Strides[pl] + c];
                        hh = (hh ^ (uint)v) * 1099511628211UL;
                    }
            }
            f.WriteLine($"recon {hh:x16}");
            for (int g = 1; g <= 7; g++)
            {
                var gm = cpi.Cm.GlobalMotion[g];
                if (gm.WmType != 0) f.WriteLine($"gm {g} type {gm.WmType} {gm.WmMat[0]} {gm.WmMat[1]} {gm.WmMat[2]} {gm.WmMat[3]} {gm.WmMat[4]} {gm.WmMat[5]}");
            }
        }
        {
            var sb = cpi.ScaledRefBufs[1];
            if (sb != null)
            {
                ulong hh = 1469598103934665603UL;
                for (int pl = 0; pl < cm.NumPlanes; pl++)
                {
                    int isUv = pl > 0 ? 1 : 0;
                    for (int r = -4; r < sb.CropHeights[isUv] + 4; r++)
                        for (int c = -4; c < sb.CropWidths[isUv] + 4; c++)
                        {
                            int v = sb.Hbd ? sb.Buffers16[pl][sb.Offsets[pl] + r * sb.Strides[pl] + c] : sb.Buffers[pl][sb.Offsets[pl] + r * sb.Strides[pl] + c];
                            hh = (hh ^ (uint)v) * 1099511628211UL;
                        }
                }
                f.WriteLine($"sref 1 {sb.CropWidths[0]}x{sb.CropHeights[0]} {hh:x16}");
            }
        }
        var lf = cpi.PostFilter!.LoopFilter;
        f.WriteLine($"lf {lf.FilterLevel[0]} {lf.FilterLevel[1]} {lf.FilterLevelU} {lf.FilterLevelV} sharp {lf.SharpnessLevel}");
        for (int r = 0; r < cm.MiRows; r++)
            for (int c = 0; c < cm.MiCols; c++)
            {
                var mi = cm.MiGridBase[r * cm.MiStride + c]!;
                if ((r > 0 && cm.MiGridBase[(r - 1) * cm.MiStride + c] == mi) || (c > 0 && cm.MiGridBase[r * cm.MiStride + c - 1] == mi)) continue;
                f.WriteLine($"b {r} {c} bs {mi.Bsize} y {mi.Mode} uv {mi.UvMode} ad {mi.AngleDelta[0]} {mi.AngleDelta[1]} fi {(mi.UseFilterIntra != 0 ? 1 + mi.FilterIntraMode : 0)} cfl {mi.CflAlphaIdx} {mi.CflAlphaSigns} pal {mi.Palette.PaletteSize0} {mi.Palette.PaletteSize1} tx {mi.TxSize} skip {mi.SkipTxfm} ref {mi.RefFrame0} {mi.RefFrame1} mv {mi.Mv0.Row} {mi.Mv0.Col} {mi.Mv1.Row} {mi.Mv1.Col} if {mi.InterpFilters:x} mm {mi.MotionMode} rmi {mi.RefMvIdx}");
            }
        for (int r = 0; r < cm.MiRows; r += cm.MibSize)
            for (int c = 0; c < cm.MiCols; c += cm.MibSize)
                f.WriteLine($"sbq {r} {c} q {cm.MiGridBase[r * cm.MiStride + c]!.CurrentQindex} cdef {cm.MiGridBase[r * cm.MiStride + c]!.CdefStrength}");
        for (int r = 0; r < cm.MiRows; r++)
            f.WriteLine($"txt {r} " + string.Join(' ', Enumerable.Range(0, cm.MiCols).Select(c => cm.TxTypeMap[r * cm.MiStride + c])));
        var cd = cpi.PostFilter.Cdef;
        if (cd != null)
            f.WriteLine($"cdef 1 damp {cd.CdefDamping} bits {cd.CdefBits} " + string.Join(' ', Enumerable.Range(0, cd.NbCdefStrengths).Select(i => $"{cd.CdefStrengths[i]}/{cd.CdefUvStrengths[i]}")));
        var rst = cpi.PostFilter.Restoration;
        for (int p = 0; p < cm.NumPlanes; p++)
            f.WriteLine($"lr {p} type {(rst != null ? rst[p].FrameRestorationType : 0)} size {(rst != null ? rst[p].RestorationUnitSize : 0)}");
        f.Flush();
    }
}
