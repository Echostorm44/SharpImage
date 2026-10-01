using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;
// LOCAL smoke / oracle-diff harness for the libaom-port frame encoder (not committed yet).
// AOM_SMOKE_YUV=<file.yuv> AOM_SMOKE_W / _H / _Q (qindex) / _SPEED / _OUT (dump path), AOM_SMOKE_NOML=1
public sealed class AomEncoderSmoke
{
    [Test]
    public async Task EncodesFrame()
    {
        string? yuvPath = Environment.GetEnvironmentVariable("AOM_SMOKE_YUV");
        int w = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_W") ?? "200"), h = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_H") ?? "150");
        // AOM_SMOKE_FMT=444 (4:4:4 planes in the file) / 422 (chroma halved horizontally) / mono (luma only is read)
        string fmt = Environment.GetEnvironmentVariable("AOM_SMOKE_FMT") ?? "420";
        bool is444 = fmt == "444", is422 = fmt == "422", mono = fmt == "mono";
        int cw = is444 ? w : (w + 1) / 2, ch = is444 || is422 ? h : (h + 1) / 2;
        byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
        // AOM_SMOKE_BD=10 / 12: 16-bit little-endian samples in the file (AomEncodeInput.Planes16)
        int bd = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_BD") ?? "8");
        ushort[] y16 = new ushort[w * h], u16 = new ushort[cw * ch], v16 = new ushort[cw * ch];
        if (bd > 8)
        {
            var all = File.ReadAllBytes(yuvPath!);
            var s16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(all);
            s16.Slice(0, y16.Length).CopyTo(y16);
            if (!mono) { s16.Slice(y16.Length, u16.Length).CopyTo(u16); s16.Slice(y16.Length + u16.Length, v16.Length).CopyTo(v16); }
            else { Array.Fill(u16, (ushort)(1 << (bd - 1))); Array.Fill(v16, (ushort)(1 << (bd - 1))); }
            for (int i = 0; i < y.Length; i++) y[i] = (byte)(y16[i] >> (bd - 8));
        }
        else if (yuvPath != null && File.Exists(yuvPath))
        {
            var all = File.ReadAllBytes(yuvPath);
            Array.Copy(all, 0, y, 0, y.Length);
            if (!mono) { Array.Copy(all, y.Length, u, 0, u.Length); Array.Copy(all, y.Length + u.Length, v, 0, v.Length); }
        }
        else
        {
            var rng = new Random(3);
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) y[r * w + c] = (byte)Math.Clamp((r * 2 + c) / 2 + (((r / 16) + (c / 16)) & 1) * 60 + rng.Next(-8, 9), 0, 255);
            for (int i = 0; i < u.Length; i++) { u[i] = (byte)(100 + (i % 37)); v[i] = (byte)(140 - (i % 23)); }
            if (yuvPath != null) { using var fs = File.Create(yuvPath); fs.Write(y); fs.Write(u); fs.Write(v); }
        }
        bool noMl = Environment.GetEnvironmentVariable("AOM_SMOKE_NOML") == "1";
        var input = new AomEncodeInput { Width = w, Height = h, Planes = new[] { y, u, v }, Strides = new[] { w, cw, cw },
            BitDepth = bd, Planes16 = bd > 8 ? new[] { y16, u16, v16 } : null, EnableRestoration = bd != 12,
            SsX = is444 ? 0 : 1, SsY = is444 || is422 ? 0 : 1, Monochrome = mono,
            EnableIntrabc = Environment.GetEnvironmentVariable("AOM_SMOKE_NOIBC") != "1",
            BaseQindex = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_Q") ?? "112"),
            Speed = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_SPEED") ?? "6"),
            // AOM_SMOKE_THREADS=N: cfg.g_threads (row-MT from 2)
            Threads = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_THREADS") ?? "1"),
            // AOM_SMOKE_TUNE=iq / ssim (AOM_TUNE_IQ / AOM_TUNE_SSIM), AOM_SMOKE_IQOFF=<letters> (tune-iq sub-features switched back off, as AOMORACLE_IQOFF)
            Tune = Environment.GetEnvironmentVariable("AOM_SMOKE_TUNE") switch { "iq" => AomTune.Iq, "ssim" => AomTune.Ssim, _ => AomTune.Psnr },
            IqOff = Environment.GetEnvironmentVariable("AOM_SMOKE_IQOFF"),
            SfOverride = noMl ? sf => { sf.intra_sf.intra_pruning_with_hog = 0; sf.intra_sf.chroma_intra_pruning_with_hog = 0;
                sf.part_sf.intra_cnn_based_part_prune_level = 0; sf.part_sf.ml_prune_partition = 0; sf.tx_sf.prune_intra_tx_depths_using_nn = false; } : null };
        string? tracePath = Environment.GetEnvironmentVariable("AOM_TRACE");
        using var traceWriter = tracePath != null ? new StreamWriter(tracePath) : null;
        AomTrace.Out = traceWriter;
        int reps = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_REPS") ?? "1");
        AomComp cpi = null!; AomMacroblock x = null!;
        if (reps > 1)
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
        }
        var sw = new System.Diagnostics.Stopwatch();
        long searchMs = 0, postMs = 0;
        double lpfMs = 0, rstMs = 0, cdefMs = 0;
        double totalMs = 0, allocMb = 0; int gcs = 0;
        for (int rep = 0; rep < reps; rep++)
        {
            sw.Restart();
            (cpi, x) = AomEncoder.EncodeFrame(input);
            searchMs = rep == 0 ? sw.ElapsedMilliseconds : Math.Min(searchMs, sw.ElapsedMilliseconds);
            sw.Restart();
            // AOM_SMOKE_APPLYLR=1: apply the chosen loop restoration to the reconstruction (what a decoder outputs)
            // AOM_SMOKE_NOPF=1: stop after the search (no post filters, no packet)
            if (Environment.GetEnvironmentVariable("AOM_SMOKE_NOPF") != "1")
                AomEncoder.RunPostFilter(cpi, x, Environment.GetEnvironmentVariable("AOM_SMOKE_APPLYLR") == "1");
            postMs = rep == 0 ? sw.ElapsedMilliseconds : Math.Min(postMs, sw.ElapsedMilliseconds);
            double l = AomPostFilter.LastLpfTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double rr = AomPostFilter.LastRstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double cc = AomPostFilter.LastCdefTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            cdefMs = rep == 0 ? cc : Math.Min(cdefMs, cc);
            lpfMs = rep == 0 ? l : Math.Min(lpfMs, l);
            rstMs = rep == 0 ? rr : Math.Min(rstMs, rr);
            if (reps > 1)
            {
                // the whole aom_codec_encode equivalent: encode + post filter + pack
                long a0 = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var (c2, x2) = AomEncoder.EncodeFrame(input);
                AomEncoder.RunPostFilter(c2, x2);
                AomBitstream.PackFrame(c2, new AomSequenceConfig());
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                totalMs = rep == 0 ? ms : Math.Min(totalMs, ms);
                allocMb = (GC.GetAllocatedBytesForCurrentThread() - a0) / 1048576.0; gcs = GC.CollectionCount(0) - g0;
            }
        }
        Console.WriteLine($"timing (min of {reps}): search {searchMs} ms, post filter {postMs} ms (deblock {lpfMs:F1} ms, cdef {cdefMs:F1} ms, restoration {rstMs:F1} ms), total {totalMs:F1} ms, alloc {allocMb:F1} MB gen0 {gcs}");
        // AOM_SMOKE_PFREPS=n: n more post-filter runs (profiling)
        int pfReps = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_PFREPS") ?? "0");
        for (int rep = 0; rep < pfReps; rep++) AomEncoder.RunPostFilter(cpi, x);
        sw.Restart(); sw.Stop();
        AomTrace.Out = null;
        var cm = cpi.Cm; var rec = cm.CurFrame;
        double se = 0;
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++)
            {
                int d = bd > 8 ? (rec.Buffers16[0][rec.Offsets[0] + r * rec.Strides[0] + c] >> (bd - 8)) - y[r * w + c]
                    : rec.Buffers[0][rec.Offsets[0] + r * rec.Strides[0] + c] - y[r * w + c];
                se += d * d;
            }
        double psnr = 10 * Math.Log10(255.0 * 255 / (se / (w * h)));
        Console.WriteLine($"smoke: luma PSNR {psnr:F2}");
        string? outPath = Environment.GetEnvironmentVariable("AOM_SMOKE_OUT");
        if (outPath != null) File.WriteAllText(outPath, Dump(cpi));
        // AOM_SMOKE_OBU=<path>: the libaom-exact packet (AomBitstream.PackFrame); AOM_SMOKE_BSTRACE=<path>: its symbol trace
        string? obuPath = Environment.GetEnvironmentVariable("AOM_SMOKE_OBU");
        if (obuPath != null && Environment.GetEnvironmentVariable("AOM_SMOKE_NOPF") != "1")
        {
            string? bsTracePath = Environment.GetEnvironmentVariable("AOM_SMOKE_BSTRACE");
            using var bsTrace = bsTracePath != null ? new StreamWriter(bsTracePath) : null;
            // AOM_SMOKE_NEG=1 (negative control): pack with disable_cdf_update, which must no longer match libaom
            if (Environment.GetEnvironmentVariable("AOM_SMOKE_NEG") == "1") cpi.DisableCdfUpdate = true;
            // AOM_SMOKE_CICP=cp,tc,mc / AOM_SMOKE_RANGE=0 (studio) / AOM_SMOKE_CSP=<chroma sample position>
            var seqCfg = new AomSequenceConfig();
            string? cicp = Environment.GetEnvironmentVariable("AOM_SMOKE_CICP");
            if (cicp != null)
            {
                var parts = cicp.Split(',').Select(int.Parse).ToArray();
                (seqCfg.ColorPrimaries, seqCfg.TransferCharacteristics, seqCfg.MatrixCoefficients) = (parts[0], parts[1], parts[2]);
            }
            if (Environment.GetEnvironmentVariable("AOM_SMOKE_RANGE") == "0") seqCfg.ColorRange = 0;
            if (Environment.GetEnvironmentVariable("AOM_SMOKE_CSP") is string csp) seqCfg.ChromaSamplePosition = int.Parse(csp);
            byte[] packet = AomBitstream.PackFrame(cpi, seqCfg, bsTrace);
            File.WriteAllBytes(obuPath, packet);
            // AOM_SMOKE_SIDEC=<path>: SharpImage's own AV1 decoder's output of the packet (planes at the frame size)
            if (Environment.GetEnvironmentVariable("AOM_SMOKE_SIDEC") is string siPath)
            {
                var dec = new Av1Decoder();
                using var f = dec.Decode(packet, 0, isKeyframe: true) ?? throw new InvalidOperationException("SharpImage decode failed: " + Av1Decoder.LastDecodeError);
                using var fs = File.Create(siPath);
                for (int p = 0; p < cm.NumPlanes; p++)
                {
                    var plane = p == 0 ? f.YPlane : p == 1 ? f.UPlane : f.VPlane;
                    int stride = p == 0 ? f.YStride : p == 1 ? f.UStride : f.VStride;
                    int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
                    for (int r = 0; r < ph; r++) fs.Write(plane.Span.Slice(r * stride, pw));
                }
            }
        }
        // AOM_SMOKE_RECON=<path>: the reconstruction as 8-bit 4:2:0 planes (compare with a decoder's output)
        string? reconPath = Environment.GetEnvironmentVariable("AOM_SMOKE_RECON");
        if (reconPath != null)
        {
            using var fs = File.Create(reconPath);
            for (int p = 0; p < cm.NumPlanes; p++)
            {
                int pw = p == 0 ? w : cw, ph = p == 0 ? h : ch;
                for (int r = 0; r < ph; r++) fs.Write(rec.Buffers[p], rec.Offsets[p] + r * rec.Strides[p], pw);
            }
        }
        // a sanity floor only (the packet is what's compared): the synthetic intrabc image at q55 / speed 9 sits near 15 dB
        await Assert.That(psnr).IsGreaterThan(12.0);
    }

    private static string Dump(AomComp cpi)
    {
        var cm = cpi.Cm;
        var sb = new System.Text.StringBuilder();
        sb.Append($"frame {cm.Width} {cm.Height} qindex {cm.BaseQindex} mi {cm.MiRows} {cm.MiCols}\n");
        sb.Append($"hdr sct {(cpi.AllowScreenContentTools ? 1 : 0)} ibc {(cpi.AllowIntrabc ? 1 : 0)}\n");
        var ci = cpi.PostFilter?.Cdef;
        sb.Append($"qp ydc {cm.YDcDeltaQ} udc {cm.UDcDeltaQ} uac {cm.UAcDeltaQ} vdc {cm.VDcDeltaQ} vac {cm.VAcDeltaQ} qm {(cm.UsingQmatrix ? 1 : 0)} {cm.QmLevelY} {cm.QmLevelU} {cm.QmLevelV} dq {(cpi.DeltaQPresentFlag ? 1 : 0)} res {cpi.DeltaQRes} sharp {cpi.PostFilter?.LoopFilter.SharpnessLevel}\n");
        sb.Append($"cdef {(cpi.CdefControl != 0 ? 1 : 0)} damp {ci?.CdefDamping ?? 0} bits {ci?.CdefBits ?? 0}");
        for (int i = 0; i < (ci?.NbCdefStrengths ?? 1); i++) sb.Append($" {ci?.CdefStrengths[i] ?? 0}/{ci?.CdefUvStrengths[i] ?? 0}");
        sb.Append('\n');
        for (int r = 0; r < cm.MiRows; r += cm.MibSize)
            for (int c = 0; c < cm.MiCols; c += cm.MibSize)
            {
                var m = cm.MiGridBase[r * cm.MiStride + c]!;
                sb.Append($"sbq {r} {c} q {m.CurrentQindex} cdef {m.CdefStrength}\n");
            }
        for (int r = 0; r < cm.MiRows; r++)
            for (int c = 0; c < cm.MiCols; c++)
            {
                var mi = cm.MiGridBase[r * cm.MiStride + c]!;
                if ((r > 0 && cm.MiGridBase[(r - 1) * cm.MiStride + c] == mi) || (c > 0 && cm.MiGridBase[r * cm.MiStride + c - 1] == mi)) continue;
                sb.Append($"b {r} {c} bs {mi.Bsize} part {mi.Partition} y {mi.Mode} uv {mi.UvMode} ad {mi.AngleDelta[0]} {mi.AngleDelta[1]} fi {mi.UseFilterIntra} {mi.FilterIntraMode} cfl {mi.CflAlphaIdx} {mi.CflAlphaSigns} pal {mi.Palette.PaletteSize0} {mi.Palette.PaletteSize1} tx {mi.TxSize} skip {mi.SkipTxfm} ibc {mi.UseIntrabc} dv {mi.Mv0.Row} {mi.Mv0.Col}\n");
            }
        if (Environment.GetEnvironmentVariable("AOMORACLE_TCOEFF") != null)
            for (int r = 0; r < cm.MiRows; r++)
                for (int c = 0; c < cm.MiCols; c++)
                {
                    var mi = cm.MiGridBase[r * cm.MiStride + c]!;
                    if ((r > 0 && cm.MiGridBase[(r - 1) * cm.MiStride + c] == mi) || (c > 0 && cm.MiGridBase[r * cm.MiStride + c - 1] == mi)) continue;
                    sb.Append($"cbo {r} {c} {cpi.ExtCbOffset[(r * cm.MiStride + c) * 2]} {cpi.ExtCbOffset[(r * cm.MiStride + c) * 2 + 1]}\n");
                }
        for (int r = 0; r < cm.MiRows; r++)
        {
            sb.Append($"txt {r}");
            for (int c = 0; c < cm.MiCols; c++) sb.Append(' ').Append(cm.TxTypeMap[r * cm.MiStride + c]);
            sb.Append('\n');
        }
        var pf = cpi.PostFilter;
        if (pf != null)
        {
            sb.Append($"lfl {pf.LoopFilter.FilterLevel[0]} {pf.LoopFilter.FilterLevel[1]} {pf.LoopFilter.FilterLevelU} {pf.LoopFilter.FilterLevelV}\n");
            for (int p = 0; p < cm.NumPlanes && pf.Restoration == null; p++) sb.Append($"lr {p} type 0 size 0 units 0" + (char)10);
            for (int p = 0; p < cm.NumPlanes && pf.Restoration != null; p++)
            {
                var rsi = pf.Restoration[p];
                bool none = rsi.FrameRestorationType == AomRestoration.RestoreNone;
                sb.Append($"lr {p} type {rsi.FrameRestorationType} size {rsi.RestorationUnitSize} units {(none ? 0 : rsi.NumRestUnits)}\n");
                if (none) continue;
                for (int u = 0; u < rsi.NumRestUnits; u++)
                {
                    var ui = rsi.UnitInfo[u];
                    sb.Append($"lru {p} {u} t {ui.Type}");
                    if (ui.Type == AomRestoration.RestoreWiener)
                        for (int i = 0; i < 8; i++) sb.Append($" {ui.Wiener.V[i]} {ui.Wiener.H[i]}");
                    if (ui.Type == AomRestoration.RestoreSgrproj) sb.Append($" ep {ui.Sgrproj.Ep} xqd {ui.Sgrproj.Xqd0} {ui.Sgrproj.Xqd1}");
                    sb.Append('\n');
                }
            }
        }
        int sbMi = cm.MibSize, sbRows = (cm.MiRows + sbMi - 1) / sbMi, sbCols = (cm.MiCols + sbMi - 1) / sbMi;
        int npix = 1 << AomTables.NumPelsLog2Lookup[cm.SbSize];
        for (int sr = 0; sr < sbRows; sr++)
            for (int sc = 0; sc < sbCols; sc++)
            {
                var cb = cpi.CbCoeffBuffers[sr * sbCols + sc];
                for (int p = 0; p < cm.NumPlanes; p++)
                {
                    int n = p != 0 ? npix >> (cm.SsX + cm.SsY) : npix;
                    ulong th = 1469598103934665603UL, ch = 1469598103934665603UL;
                    for (int i = 0; i < n; i++) th = (th ^ (uint)cb.Tcoeff[p][i]) * 1099511628211UL;
                    for (int i = 0; i < n / 16; i++) ch = (ch ^ cb.EntropyCtx[p][i]) * 1099511628211UL;
                    sb.Append($"cbh {sr} {sc} p {p} tcoeff {th:x16} ctx {ch:x16}\n");
                    if (Environment.GetEnvironmentVariable("AOMORACLE_TCOEFF") != null)
                    {
                        sb.Append($"tc {sr} {sc} p {p}");
                        for (int i = 0; i < n; i++) sb.Append(' ').Append(cb.Tcoeff[p][i]);
                        sb.Append('\n');
                    }
                    sb.Append($"cb {sr} {sc} p {p} eobs");
                    for (int i = 0; i < n / 16; i++) sb.Append(' ').Append(cb.Eobs[p][i]);
                    sb.Append('\n');
                }
            }
        return sb.ToString();
    }
}
