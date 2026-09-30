using System.Text;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Real-frame twins: libaom encodes a still (all-intra, tune=psnr, AOM_Q, one thread) through its public API while
// twin_capture.c dumps its own post-filter inputs and decisions; the port then runs on the same buffers and must
// reproduce every decision and every filtered sample.
public sealed partial class AomLfTwinTests
{
    private sealed class Capture
    {
        public int[] Hdr = Array.Empty<int>();
        public sbyte[] RefDeltas = new sbyte[8], ModeDeltas = new sbyte[2];
        public byte[][] Src = Array.Empty<byte[]>(), Recon = Array.Empty<byte[]>(), Dbk = Array.Empty<byte[]>(), Fin = Array.Empty<byte[]>();
        public byte[][] Dbk1 = Array.Empty<byte[]>();   // the frame after libaom's final deblocking call (any speed)
        public int Dbk1OptLevel = -1;
        public (int w, int h)[] SrcDim = Array.Empty<(int, int)>(), ReconDim = Array.Empty<(int, int)>();
        public byte[] Mi = Array.Empty<byte>();
        public int[] Levels = Array.Empty<int>();   // [0], [1], u, v, sharpness
        public bool HasRst;
        public int[] RstHdr = Array.Empty<int>();
        public int[] SwCost = new int[3], WnCost = new int[2], SgCost = new int[2];
        public int[] RowStart = Array.Empty<int>(), ColStart = Array.Empty<int>();
        public readonly List<(int unitSize, int plane, long[] sse, long[] bits, int[][] units)> Searches = new();
        public readonly List<(int type, int unitSize, int horz, int vert, int[][] units)> RstOut = new();
    }

    private static Capture ReadCapture(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var c = new Capture();
        int np = 0;
        byte[][] Planes(out (int, int)[] dims)
        {
            var planes = new byte[np][];
            dims = new (int, int)[np];
            for (int p = 0; p < np; p++)
            {
                int w = br.ReadInt32(), h = br.ReadInt32();
                dims[p] = (w, h);
                planes[p] = br.ReadBytes(w * h);
            }
            return planes;
        }
        while (true)
        {
            string tag = Encoding.ASCII.GetString(br.ReadBytes(4));
            switch (tag)
            {
                case "LPF0":
                    c.Hdr = Enumerable.Range(0, 40).Select(_ => br.ReadInt32()).ToArray();
                    np = c.Hdr[4];
                    for (int i = 0; i < 8; i++) c.RefDeltas[i] = br.ReadSByte();
                    for (int i = 0; i < 2; i++) c.ModeDeltas[i] = br.ReadSByte();
                    c.Src = Planes(out c.SrcDim);
                    c.Recon = Planes(out c.ReconDim);
                    c.Mi = br.ReadBytes(c.Hdr[13] * c.Hdr[14] * 8);
                    break;
                case "LPF1":
                    c.Levels = Enumerable.Range(0, 5).Select(_ => br.ReadInt32()).ToArray();
                    break;
                case "DBK0":
                    c.Dbk = Planes(out _);
                    break;
                case "DBK1":
                    c.Dbk1OptLevel = br.ReadInt32();
                    c.Dbk1 = Planes(out _);
                    break;
                case "RST0":
                {
                    c.HasRst = true;
                    c.RstHdr = Enumerable.Range(0, 32).Select(_ => br.ReadInt32()).ToArray();
                    for (int i = 0; i < 3; i++) c.SwCost[i] = br.ReadInt32();
                    for (int i = 0; i < 2; i++) c.WnCost[i] = br.ReadInt32();
                    for (int i = 0; i < 2; i++) c.SgCost[i] = br.ReadInt32();
                    c.RowStart = Enumerable.Range(0, c.RstHdr[15] + 1).Select(_ => br.ReadInt32()).ToArray();
                    c.ColStart = Enumerable.Range(0, c.RstHdr[16] + 1).Select(_ => br.ReadInt32()).ToArray();
                    while (true)
                    {
                        int marker = br.ReadInt32();
                        if (marker != 0x55525354)
                        {
                            if (Encoding.ASCII.GetString(BitConverter.GetBytes(marker)) != "RSTE") throw new InvalidDataException("RST0 layout");
                            break;
                        }
                        int us = br.ReadInt32(), plane = br.ReadInt32(), n = br.ReadInt32();
                        var sse = Enumerable.Range(0, 4).Select(_ => br.ReadInt64()).ToArray();
                        var bits = Enumerable.Range(0, 4).Select(_ => br.ReadInt64()).ToArray();
                        var units = new int[n][];
                        for (int u = 0; u < n; u++) units[u] = Enumerable.Range(0, 22).Select(_ => br.ReadInt32()).ToArray();
                        c.Searches.Add((us, plane, sse, bits, units));
                    }
                    break;
                }
                case "RST1":
                    for (int p = 0; p < np; p++)
                    {
                        int type = br.ReadInt32(), us = br.ReadInt32(), hu = br.ReadInt32(), vu = br.ReadInt32(), n = br.ReadInt32();
                        var units = new int[n][];
                        for (int u = 0; u < n; u++) units[u] = Enumerable.Range(0, 20).Select(_ => br.ReadInt32()).ToArray();
                        c.RstOut.Add((type, us, hu, vu, units));
                    }
                    break;
                case "FIN0":
                    c.Fin = Planes(out _);
                    break;
                case "END0":
                    return c;
                default:
                    throw new InvalidDataException("unknown tag " + tag);
            }
        }
    }

    private static (byte[] y, byte[] u, byte[] v, int w, int h) LoadYuv(string name, bool s444, int maxW = 0, int maxH = 0)
    {
        var img = FormatRegistry.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", name));
        int w = (int)img.Columns, h = (int)img.Rows;
        if (maxW > 0) w = Math.Min(w, maxW);
        if (maxH > 0) h = Math.Min(h, maxH);
        int ch = img.NumberOfChannels;
        var Y = new byte[w * h];
        var U = new int[w * h];
        var V = new int[w * h];
        for (int r = 0; r < h; r++)
        {
            var row = img.GetPixelRow(r);
            for (int c = 0; c < w; c++)
            {
                int R, G, B;
                if (ch >= 3) { R = row[c * ch] >> 8; G = row[c * ch + 1] >> 8; B = row[c * ch + 2] >> 8; }
                else R = G = B = row[c * ch] >> 8;
                Y[r * w + c] = (byte)Math.Clamp((77 * R + 150 * G + 29 * B + 128) >> 8, 0, 255);
                U[r * w + c] = Math.Clamp(((-43 * R - 85 * G + 128 * B + 128) >> 8) + 128, 0, 255);
                V[r * w + c] = Math.Clamp(((128 * R - 107 * G - 21 * B + 128) >> 8) + 128, 0, 255);
            }
        }
        if (s444) return (Y, U.Select(x => (byte)x).ToArray(), V.Select(x => (byte)x).ToArray(), w, h);
        int cw = (w + 1) >> 1, chh = (h + 1) >> 1;
        var u2 = new byte[cw * chh];
        var v2 = new byte[cw * chh];
        for (int r = 0; r < chh; r++)
            for (int c = 0; c < cw; c++)
            {
                int su = 0, sv = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int yy = Math.Min(2 * r + dy, h - 1), xx = Math.Min(2 * c + dx, w - 1);
                        su += U[yy * w + xx];
                        sv += V[yy * w + xx];
                    }
                u2[r * cw + c] = (byte)((su + 2) >> 2);
                v2[r * cw + c] = (byte)((sv + 2) >> 2);
            }
        return (Y, u2, v2, w, h);
    }

    private static Capture RunCapture(string name, bool s444, int speed, int quantizer, int maxW = 0, int maxH = 0,
        int tileColsLog2 = 0, int tileRowsLog2 = 0, int sharpness = 0)
    {
        var (y, u, v, w, h) = LoadYuv(name, s444, maxW, maxH);
        string path = Path.Combine(Path.GetTempPath(), $"aomlf_{Environment.ProcessId}_{name}_{s444}_{speed}_{quantizer}.bin");
        int size;
        unsafe
        {
            fixed (byte* py = y)
            fixed (byte* pu = u)
            fixed (byte* pv = v)
                size = Native.twin_capture2(py, pu, pv, w, h, s444 ? 1 : 0, speed, quantizer, 0, tileColsLog2, tileRowsLog2,
                    sharpness, path);
        }
        if (size < 0) throw new InvalidOperationException($"twin_capture failed: {size}");
        try { return ReadCapture(path); }
        finally { File.Delete(path); }
    }

    private static AomYv12 FrameFrom(Capture c, byte[][] planes, (int w, int h)[] dims)
    {
        int w = c.Hdr[0], h = c.Hdr[1], np = c.Hdr[4];
        var f = new AomYv12(w, h, c.Hdr[2], c.Hdr[3], np);
        for (int p = 0; p < np; p++) f.Planes[p].Load(planes[p], dims[p].w, dims[p].h);
        return f;
    }

    private static AomLfMiGrid MiFrom(Capture c)
    {
        int rows = c.Hdr[13], cols = c.Hdr[14];
        var mi = new AomLfMiGrid(rows, cols);
        for (int i = 0; i < rows * cols; i++)
        {
            var m = c.Mi.AsSpan(i * 8, 8);
            if (m[0] == 255) continue;
            mi.Coded[i] = true;
            mi.Bsize[i] = m[0];
            mi.TxSize[i] = m[1];
            mi.Skip[i] = m[2] != 0;
            mi.RefFrame0[i] = (sbyte)m[3];
            mi.Mode[i] = m[4];
            mi.SegmentId[i] = m[5];
            mi.IsInter[i] = (sbyte)m[3] > 0 || m[6] != 0;
        }
        return mi;
    }

    private static AomRstPickConfig RstCfgFrom(Capture c)
    {
        int[] r = c.RstHdr;
        var cfg = new AomRstPickConfig
        {
            Rdmult = r[0], MinLrUnitSize = r[1], MaxLrUnitSize = r[2], DisableLoopRestorationLuma = r[3],
            DisableLoopRestorationChroma = r[4], DisableWienerFilter = r[5] != 0, DisableSgrFilter = r[6] != 0,
            PruneWienerBasedOnSrcVar = r[7], PruneSgrBasedOnWiener = r[8], ReduceWienerWindowSize = r[9],
            EnableSgrEpPruning = r[10], DualSgrPenaltyLevel = r[11], SwitchableLrWithBiasLevel = r[12],
            DisableWienerCoeffRefineSearch = r[13] != 0, UseDownsampledWienerStats = r[14], SbSize = r[17],
            BaseQindex = r[18], TileRowStartSb = c.RowStart, TileColStartSb = c.ColStart,
        };
        c.SwCost.CopyTo(cfg.SwitchableRestoreCost, 0);
        c.WnCost.CopyTo(cfg.WienerRestoreCost, 0);
        c.SgCost.CopyTo(cfg.SgrprojRestoreCost, 0);
        return cfg;
    }

    private static int[] UnitRecord(in AomRestUnitSearchInfo r)
    {
        var a = new int[22];
        a[0] = r.BestRtype0; a[1] = r.BestRtype1; a[2] = r.BestRtype2;
        for (int i = 0; i < 8; i++) { a[3 + i] = r.Wiener.V[i]; a[11 + i] = r.Wiener.H[i]; }
        a[19] = r.Sgrproj.Ep; a[20] = r.Sgrproj.Xqd0; a[21] = r.Sgrproj.Xqd1;
        return a;
    }

    private static int[] OutRecord(in AomRestorationUnitInfo u)
    {
        var a = new int[20];
        a[0] = u.Type;
        for (int i = 0; i < 8; i++)
        {
            a[1 + i] = u.Type == AomRestoration.RestoreWiener ? u.Wiener.V[i] : 0;
            a[9 + i] = u.Type == AomRestoration.RestoreWiener ? u.Wiener.H[i] : 0;
        }
        a[17] = u.Type == AomRestoration.RestoreSgrproj ? u.Sgrproj.Ep : 0;
        a[18] = u.Type == AomRestoration.RestoreSgrproj ? u.Sgrproj.Xqd0 : 0;
        a[19] = u.Type == AomRestoration.RestoreSgrproj ? u.Sgrproj.Xqd1 : 0;
        return a;
    }

    /// <summary>Runs every stage of the port on a capture; returns the mismatch descriptions (empty = bit-exact).</summary>
    private static List<string> CheckCapture(Capture c, string label)
    {
        var errs = new List<string>();
        int np = c.Hdr[4];
        if (c.Hdr[24] != 0) errs.Add($"{label}: CDEF is on in libaom (the port assumes it off)");
        var src = FrameFrom(c, c.Src, c.SrcDim);
        var mi = MiFrom(c);

        // 1) deblocking level search (on the pre-deblock reconstruction)
        var cur = FrameFrom(c, c.Recon, c.ReconDim);
        var lf = new AomLoopFilterParams { ModeRefDeltaEnabled = c.Hdr[16] != 0 };
        c.RefDeltas.CopyTo(lf.RefDeltas, 0);
        c.ModeDeltas.CopyTo(lf.ModeDeltas, 0);
        lf.FilterLevel[0] = c.Hdr[29];
        lf.FilterLevel[1] = c.Hdr[30];
        var lpfCfg = new AomLpfPickConfig
        {
            Method = c.Hdr[8], UseCoarseFilterLevelSearch = c.Hdr[10], TxModeOnly4x4 = c.Hdr[11] == 0, Sharpness = c.Hdr[7],
            SharpnessFromConfig = c.Hdr[26] == ALLINTRA, EnableAdaptiveSharpness = c.Hdr[20] != 0,
            SkipLoopFilterUsingFiltError = c.Hdr[22], BaseQindex = c.Hdr[6], KeyFrame = c.Hdr[12] == KEY_FRAME,
            IntraOnly = true,
        };
        var filter = new AomLoopFilter();
        AomPickLpf.PickFilterLevel(src, cur, mi, lf, lpfCfg, filter);
        int[] ours = { lf.FilterLevel[0], lf.FilterLevel[1], lf.FilterLevelU, lf.FilterLevelV, lf.SharpnessLevel };
        if (!ours.SequenceEqual(c.Levels))
            errs.Add($"{label}: levels ours [{string.Join(",", ours)}] libaom [{string.Join(",", c.Levels)}]");
        for (int p = 0; p < np; p++)
        {
            var pl = cur.Planes[p];
            for (int r = 0; r < c.ReconDim[p].h; r++)
                if (!pl.Buf.AsSpan(pl.At(0, r), c.ReconDim[p].w).SequenceEqual(c.Recon[p].AsSpan(r * c.ReconDim[p].w, c.ReconDim[p].w)))
                {
                    errs.Add($"{label}: the level search did not restore plane {p} row {r}");
                    break;
                }
        }

        // 2) deblocking with libaom's levels (DBK1: after its final av1_loop_filter_frame_mt, at whatever
        // lpf_opt_level the speed uses; DBK0: the frame the restoration saw)
        if (c.Levels[0] != 0 || c.Levels[1] != 0)
        {
            if (c.Dbk1.Length == 0) errs.Add($"{label}: no final deblocking dump");
            else if (c.Dbk.Length > 0 && !c.Dbk.Zip(c.Dbk1).All(t => t.First.AsSpan().SequenceEqual(t.Second)))
                errs.Add($"{label}: libaom's DBK0 and DBK1 differ");
        }
        byte[][] dbkRef = c.Dbk1.Length > 0 ? c.Dbk1 : c.Dbk;
        if (dbkRef.Length > 0 || c.Levels[0] != 0 || c.Levels[1] != 0)
        {
            if (dbkRef.Length == 0) dbkRef = c.Recon;
            var lf2 = lf.Clone();
            lf2.FilterLevel[0] = c.Levels[0];
            lf2.FilterLevel[1] = c.Levels[1];
            lf2.FilterLevelU = c.Levels[2];
            lf2.FilterLevelV = c.Levels[3];
            lf2.SharpnessLevel = c.Levels[4];
            if (lf2.FilterLevel[0] != 0 || lf2.FilterLevel[1] != 0) filter.FilterFrame(cur, mi, lf2, 0, np);
            for (int p = 0; p < np; p++)
            {
                var pl = cur.Planes[p];
                int bad = 0;
                for (int r = 0; r < c.ReconDim[p].h && bad == 0; r++)
                    if (!pl.Buf.AsSpan(pl.At(0, r), c.ReconDim[p].w).SequenceEqual(dbkRef[p].AsSpan(r * c.ReconDim[p].w, c.ReconDim[p].w)))
                        bad = r + 1;
                if (bad != 0) errs.Add($"{label}: deblocked plane {p} differs from row {bad - 1}");
            }
        }

        // 4) end to end through the encoder-facing entry point, from the pre-deblock reconstruction
        {
            var cur4 = FrameFrom(c, c.Recon, c.ReconDim);
            var lf4 = new AomLoopFilterParams { ModeRefDeltaEnabled = c.Hdr[16] != 0 };
            c.RefDeltas.CopyTo(lf4.RefDeltas, 0);
            c.ModeDeltas.CopyTo(lf4.ModeDeltas, 0);
            lf4.FilterLevel[0] = c.Hdr[29];
            lf4.FilterLevel[1] = c.Hdr[30];
            var res = AomPostFilter.Run(src, cur4, mi, lpfCfg, c.HasRst ? RstCfgFrom(c) : null, applyRestoration: true, lf4);
            // (the restoration search extends the frame borders into the aligned padding: compare the crop)
            bool fin = c.HasRst && c.Fin.Length > 0;
            byte[][] expect = fin ? c.Fin : c.Dbk1.Length > 0 ? c.Dbk1 : c.Recon;
            for (int p = 0; p < np; p++)
            {
                var pl = cur4.Planes[p];
                int stride = fin ? pl.CropWidth : c.ReconDim[p].w;
                for (int r = 0; r < pl.CropHeight; r++)
                    if (!pl.Buf.AsSpan(pl.At(0, r), pl.CropWidth).SequenceEqual(expect[p].AsSpan(r * stride, pl.CropWidth)))
                    {
                        errs.Add($"{label}: end-to-end plane {p} differs from row {r}");
                        break;
                    }
            }
            if (res.LoopFilter.FilterLevel[0] != c.Levels[0] || res.LoopFilter.FilterLevel[1] != c.Levels[1])
                errs.Add($"{label}: end-to-end levels differ");
        }

        if (!c.HasRst) return errs;

        // 3) restoration, from libaom's own deblocked frame
        var cfg = RstCfgFrom(c);
        var dgd = FrameFrom(c, c.Dbk, c.ReconDim);
        var rst = new AomRestorationInfo[np];
        for (int p = 0; p < np; p++) rst[p] = new AomRestorationInfo();
        AomRestoration.SaveBoundaryLines(dgd, rst, false);
        AomRestoration.SaveBoundaryLines(dgd, rst, true);

        // 3a) the per-unit searches at every unit size (twin_rst_dump_units order)
        {
            var dgdA = dgd.Clone();
            var pick = new AomPickRst(src, dgdA, rst, cfg);
            pick.PrepareFrame();
            foreach (var s in c.Searches)
            {
                pick.SearchPlane(s.unitSize, s.plane);
                if (!pick.TotalSse.SequenceEqual(s.sse) || !pick.TotalBits.SequenceEqual(s.bits))
                    errs.Add($"{label}: size {s.unitSize} plane {s.plane} totals sse [{string.Join(",", pick.TotalSse)}] vs " +
                             $"[{string.Join(",", s.sse)}] bits [{string.Join(",", pick.TotalBits)}] vs [{string.Join(",", s.bits)}]");
                var infos = pick.UnitSearchInfo(s.plane);
                for (int u = 0; u < s.units.Length; u++)
                {
                    var rec = UnitRecord(infos[u]);
                    if (!rec.SequenceEqual(s.units[u]))
                    {
                        errs.Add($"{label}: size {s.unitSize} plane {s.plane} unit {u} ours [{string.Join(",", rec)}] " +
                                 $"libaom [{string.Join(",", s.units[u])}]");
                        break;
                    }
                }
            }
        }

        // 3b) the frame decision
        var rst2 = new AomRestorationInfo[np];
        for (int p = 0; p < np; p++) rst2[p] = new AomRestorationInfo();
        var dgdB = dgd.Clone();
        AomRestoration.SaveBoundaryLines(dgdB, rst2, false);
        AomRestoration.SaveBoundaryLines(dgdB, rst2, true);
        AomPickRst.PickFilterRestoration(src, dgdB, rst2, cfg);
        for (int p = 0; p < np; p++)
        {
            var o = c.RstOut[p];
            var r = rst2[p];
            if (r.FrameRestorationType != o.type || r.RestorationUnitSize != o.unitSize || r.HorzUnits != o.horz || r.VertUnits != o.vert)
            {
                errs.Add($"{label}: plane {p} frame type/size ours {r.FrameRestorationType}/{r.RestorationUnitSize} libaom {o.type}/{o.unitSize}");
                continue;
            }
            for (int u = 0; u < o.units.Length; u++)
            {
                var rec = OutRecord(r.UnitInfo[u]);
                if (!rec.SequenceEqual(o.units[u]))
                {
                    errs.Add($"{label}: plane {p} unit {u} ours [{string.Join(",", rec)}] libaom [{string.Join(",", o.units[u])}]");
                    break;
                }
            }
        }

        // 3c) the restored frame
        if (c.Fin.Length > 0)
        {
            AomRestoration.FilterFrame(dgdB, rst2);
            for (int p = 0; p < np; p++)
            {
                var pl = dgdB.Planes[p];
                int cw = pl.CropWidth;
                for (int r = 0; r < pl.CropHeight; r++)
                    if (!pl.Buf.AsSpan(pl.At(0, r), cw).SequenceEqual(c.Fin[p].AsSpan(r * cw, cw)))
                    {
                        errs.Add($"{label}: restored plane {p} differs from row {r}");
                        break;
                    }
            }
        }
        return errs;
    }

    private static async Task RunCaptures(string image, bool[] subsamplings, int[] speeds, int[] quantizers, int maxW = 0, int maxH = 0,
        int tileColsLog2 = 0, int tileRowsLog2 = 0, int sharpness = 0)
    {
        if (!Available) return;
        var errs = new List<string>();
        int runs = 0;
        foreach (bool s444 in subsamplings)
            foreach (int speed in speeds)
                foreach (int q in quantizers)
                {
                    string label = $"{image} {(s444 ? "444" : "420")} s{speed} q{q}" +
                                   (tileColsLog2 + tileRowsLog2 > 0 ? $" tiles {tileColsLog2}/{tileRowsLog2}" : "") +
                                   (sharpness > 0 ? $" sharp{sharpness}" : "");
                    var c = RunCapture(image, s444, speed, q, maxW, maxH, tileColsLog2, tileRowsLog2, sharpness);
                    var e = CheckCapture(c, label);
                    Console.WriteLine($"{label}: levels [{string.Join(",", c.Levels)}] lpfopt {c.Dbk1OptLevel} lr " +
                                      (c.HasRst ? string.Join(" ", c.RstOut.Select(o => $"{o.type}/{o.unitSize}")) + $" tiles {c.RstHdr[16]}x{c.RstHdr[15]}" : "off") +
                                      (e.Count == 0 ? " OK" : $" {e.Count} MISMATCHES"));
                    errs.AddRange(e);
                    runs++;
                }
        foreach (var e in errs.Take(40)) Console.WriteLine(e);
        await Assert.That(runs).IsGreaterThan(0);
        await Assert.That(errs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Capture_PhotoSmall() => await RunCaptures("photo_small.png", new[] { false, true }, new[] { 0, 2, 4, 6, 9 }, new[] { 10, 30, 50 });

    [Test]
    public async Task Capture_Texture() => await RunCaptures("texture_pattern.png", new[] { false, true }, new[] { 1, 2, 3, 6, 9 }, new[] { 20, 40 });

    [Test]
    public async Task Capture_Peppers() => await RunCaptures("peppers.jpg", new[] { false, true }, new[] { 0, 2, 3, 4, 6, 9 }, new[] { 15, 30, 45 });

    [Test]
    public async Task Capture_ColorObjects() => await RunCaptures("color_objects.png", new[] { false }, new[] { 2, 6, 9 }, new[] { 10, 35, 55 });

    [Test]
    public async Task Capture_Scene() => await RunCaptures("scene.png", new[] { false, true }, new[] { 2, 6, 9 }, new[] { 25, 45 });

    [Test]
    public async Task Capture_SceneSpeed0() => await RunCaptures("scene.png", new[] { false, true }, new[] { 0 }, new[] { 30 }, 512, 384);

    [Test]
    public async Task Capture_Tiles() => await RunCaptures("scene.png", new[] { false }, new[] { 1, 2 }, new[] { 30, 45 }, 704, 480, 1, 1);

    [Test]
    public async Task Capture_Sharpness()
    {
        await RunCaptures("peppers.jpg", new[] { false }, new[] { 2, 6 }, new[] { 30 }, sharpness: 3);
        await RunCaptures("peppers.jpg", new[] { true }, new[] { 2 }, new[] { 45 }, sharpness: 7);
    }

    [Test]
    public async Task Capture_Landscape() => await RunCaptures("landscape.jpg", new[] { false }, new[] { 1, 3 }, new[] { 20, 40 });
}
