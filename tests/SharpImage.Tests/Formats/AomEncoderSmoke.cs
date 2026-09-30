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
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
        if (yuvPath != null && File.Exists(yuvPath))
        {
            var all = File.ReadAllBytes(yuvPath);
            Array.Copy(all, 0, y, 0, y.Length); Array.Copy(all, y.Length, u, 0, u.Length); Array.Copy(all, y.Length + u.Length, v, 0, v.Length);
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
            BaseQindex = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_Q") ?? "112"),
            Speed = int.Parse(Environment.GetEnvironmentVariable("AOM_SMOKE_SPEED") ?? "6"),
            SfOverride = noMl ? sf => { sf.intra_sf.intra_pruning_with_hog = 0; sf.intra_sf.chroma_intra_pruning_with_hog = 0;
                sf.part_sf.intra_cnn_based_part_prune_level = 0; sf.part_sf.ml_prune_partition = 0; sf.tx_sf.prune_intra_tx_depths_using_nn = false; } : null };
        string? tracePath = Environment.GetEnvironmentVariable("AOM_TRACE");
        using var traceWriter = tracePath != null ? new StreamWriter(tracePath) : null;
        AomTrace.Out = traceWriter;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (cpi, x) = AomEncoder.EncodeFrame(input);
        // AOM_SMOKE_APPLYLR=1: apply the chosen loop restoration to the reconstruction (what a decoder outputs)
        AomEncoder.RunPostFilter(cpi, x, Environment.GetEnvironmentVariable("AOM_SMOKE_APPLYLR") == "1");
        sw.Stop();
        AomTrace.Out = null;
        var cm = cpi.Cm; var rec = cm.CurFrame;
        double se = 0;
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) { int d = rec.Buffers[0][rec.Offsets[0] + r * rec.Strides[0] + c] - y[r * w + c]; se += d * d; }
        double psnr = 10 * Math.Log10(255.0 * 255 / (se / (w * h)));
        Console.WriteLine($"smoke: {sw.ElapsedMilliseconds} ms, luma PSNR {psnr:F2}");
        string? outPath = Environment.GetEnvironmentVariable("AOM_SMOKE_OUT");
        if (outPath != null) File.WriteAllText(outPath, Dump(cpi));
        // AOM_SMOKE_OBU=<path>: the libaom-exact packet (AomBitstream.PackFrame); AOM_SMOKE_BSTRACE=<path>: its symbol trace
        string? obuPath = Environment.GetEnvironmentVariable("AOM_SMOKE_OBU");
        if (obuPath != null)
        {
            string? bsTracePath = Environment.GetEnvironmentVariable("AOM_SMOKE_BSTRACE");
            using var bsTrace = bsTracePath != null ? new StreamWriter(bsTracePath) : null;
            File.WriteAllBytes(obuPath, AomBitstream.PackFrame(cpi, trace: bsTrace));
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
        await Assert.That(psnr).IsGreaterThan(20.0);
    }

    private static string Dump(AomComp cpi)
    {
        var cm = cpi.Cm;
        var sb = new System.Text.StringBuilder();
        sb.Append($"frame {cm.Width} {cm.Height} qindex {cm.BaseQindex} mi {cm.MiRows} {cm.MiCols}\n");
        for (int r = 0; r < cm.MiRows; r++)
            for (int c = 0; c < cm.MiCols; c++)
            {
                var mi = cm.MiGridBase[r * cm.MiStride + c]!;
                if ((r > 0 && cm.MiGridBase[(r - 1) * cm.MiStride + c] == mi) || (c > 0 && cm.MiGridBase[r * cm.MiStride + c - 1] == mi)) continue;
                sb.Append($"b {r} {c} bs {mi.Bsize} part {mi.Partition} y {mi.Mode} uv {mi.UvMode} ad {mi.AngleDelta[0]} {mi.AngleDelta[1]} fi {mi.UseFilterIntra} {mi.FilterIntraMode} cfl {mi.CflAlphaIdx} {mi.CflAlphaSigns} pal {mi.Palette.PaletteSize0} {mi.Palette.PaletteSize1} tx {mi.TxSize} skip {mi.SkipTxfm}\n");
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
                    sb.Append($"cb {sr} {sc} p {p} eobs");
                    for (int i = 0; i < n / 16; i++) sb.Append(' ').Append(cb.Eobs[p][i]);
                    sb.Append('\n');
                }
            }
        return sb.ToString();
    }
}
