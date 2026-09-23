using System;
using System.IO;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using SharpImage.Image;
using SharpImage.Transform;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// Dev-only high-bit-depth AVIF verification harness (trigger file hbd.txt in the corpus dir; consumed). Encodes
// real camera-RAW crops (genuine 12-14-bit sensor data) and synthetic 16-bit gradients at 8/10/12-bit, and dumps
// for every stream: the .avif, our decoder's native YUV planes (.ours.yuv, little-endian u16 for >8-bit), our
// HeifCoder 16-bit RGB decode (.ours.rgb48) and the 16-bit source (.src.rgb48). An external script then checks
// our-decoder == ffmpeg/dav1d (exact) and our RGB vs libavif (tolerance), plus rate/quality per depth.
public sealed class Av1HbdVerify
{
    const string Scratch = @"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad";

    [Test, NotInParallel]
    public void Run()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "hbd");
        Directory.CreateDirectory(outDir);
        string assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");

        var sources = new System.Collections.Generic.List<(string Name, ImageFrame Img)>();
        foreach (var (file, name) in new[] { ("sample1.dng", "dng"), ("RAW_SONY_A700.ARW", "arw") })
        {
            var full = FormatRegistry.Read(Path.Combine(assets, file));
            int cw = Math.Min(384, (int)full.Columns), ch = Math.Min(256, (int)full.Rows);
            sources.Add((name, Geometry.Crop(full, ((int)full.Columns - cw) / 2, ((int)full.Rows - ch) / 2, cw, ch)));
        }
        sources.Add(("grad", Gradient(257, 131, alpha: false, gray: false)));    // odd dims, smooth ramps (banding)
        sources.Add(("gray", Gradient(200, 120, alpha: false, gray: true)));
        sources.Add(("rgba", Gradient(160, 96, alpha: true, gray: false)));

        var log = new System.Text.StringBuilder();
        foreach (var (name, img) in sources)
        {
            File.WriteAllBytes(Path.Combine(outDir, $"{name}.src.rgb48"), Rgb48(img));
            foreach (int bd in new[] { 8, 10, 12 })
                foreach (int qp in new[] { 10, 28 })
                {
                    string stem = $"{name}_b{bd}_q{qp}";
                    byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = qp, BitDepth = bd });
                    File.WriteAllBytes(Path.Combine(outDir, stem + ".avif"), avif);
                    // Our decoder's native planes (u16 LE, any depth) for the exact ffmpeg/dav1d comparison. Skipped for
                    // RGBA: the alpha item decodes second and would overwrite the dump.
                    if (!img.HasAlpha) Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, stem + ".ours.yuv"));
                    var dec = HeifCoder.Decode(avif);
                    Environment.SetEnvironmentVariable("AV1_DUMP10", null);
                    File.WriteAllBytes(Path.Combine(outDir, stem + ".ours.rgb48"), Rgb48(dec));
                    log.AppendLine($"{stem} {img.Columns}x{img.Rows} bytes={avif.Length}");
                }
        }

        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Chroma-layout verification (trigger hbd_layout.txt): 4:4:4 and 4:2:2 at 8/10/12-bit, same dumps as Run().
    [Test, NotInParallel]
    public void Layouts()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_layout.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "hbd_layout");
        Directory.CreateDirectory(outDir);
        string assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var sources = new System.Collections.Generic.List<(string Name, ImageFrame Img)>();
        foreach (var (file, name) in new[] { ("sample1.dng", "dng"), ("RAW_SONY_A700.ARW", "arw") })
        {
            var full = FormatRegistry.Read(Path.Combine(assets, file));
            int cw = Math.Min(384, (int)full.Columns), ch = Math.Min(256, (int)full.Rows);
            sources.Add((name, Geometry.Crop(full, ((int)full.Columns - cw) / 2, ((int)full.Rows - ch) / 2, cw, ch)));
        }
        sources.Add(("grad", Gradient(257, 131, alpha: false, gray: false)));
        sources.Add(("rgba", Gradient(160, 96, alpha: true, gray: false)));
        var log = new System.Text.StringBuilder();
        foreach (var (name, img) in sources)
        {
            File.WriteAllBytes(Path.Combine(outDir, $"{name}.src.rgb48"), Rgb48(img));
            foreach (var (lay, tag) in new[] { (AvifChromaSubsampling.Yuv444, "444"), (AvifChromaSubsampling.Yuv422, "422") })
                foreach (int bd in new[] { 8, 10, 12 })
                    foreach (int qp in new[] { 10, 28 })
                    {
                        string stem = $"{name}_{tag}_b{bd}_q{qp}";
                        byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = qp, BitDepth = bd, ChromaSubsampling = lay });
                        File.WriteAllBytes(Path.Combine(outDir, stem + ".avif"), avif);
                        if (!img.HasAlpha) Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, stem + ".ours.yuv"));
                        var dec = HeifCoder.Decode(avif);
                        Environment.SetEnvironmentVariable("AV1_DUMP10", null);
                        File.WriteAllBytes(Path.Combine(outDir, stem + ".ours.rgb48"), Rgb48(dec));
                        log.AppendLine($"{stem} {img.Columns}x{img.Rows} bytes={avif.Length}");
                    }
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // CICP verification (trigger hbd_cicp.txt): matrices / ranges / primaries+transfer passthrough, identity & YCgCo,
    // limited-range grey, and a hand-built limited-range alpha item. Manifest: stem WxH bd cp tc mc full.
    [Test, NotInParallel]
    public void Cicp()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_cicp.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "hbd_cicp");
        Directory.CreateDirectory(outDir);
        var log = new System.Text.StringBuilder();
        var grad = Gradient(257, 131, alpha: false, gray: false);
        var gray = Gradient(130, 67, alpha: false, gray: true);
        var rgba = Gradient(160, 96, alpha: true, gray: false);
        File.WriteAllBytes(Path.Combine(outDir, "grad.src.rgb48"), Rgb48(grad));
        File.WriteAllBytes(Path.Combine(outDir, "gray.src.rgb48"), Rgb48(gray));
        File.WriteAllBytes(Path.Combine(outDir, "rgba.src.rgb48"), Rgb48(rgba));
        var cases = new (string Name, ImageFrame Img, int Bd, int? Cp, int? Tc, int? Mc, bool Full, AvifChromaSubsampling Ss)[]
        {
            ("default", grad, 8, null, null, null, true, AvifChromaSubsampling.Auto),
            ("lim601", grad, 8, null, null, 6, false, AvifChromaSubsampling.Auto),
            ("lim709", grad, 8, 1, 1, 1, false, AvifChromaSubsampling.Auto),
            ("full709", grad, 8, 1, 1, 1, true, AvifChromaSubsampling.Yuv444),
            ("lim709_10", grad, 10, 1, 1, 1, false, AvifChromaSubsampling.Yuv422),
            ("pq2020", grad, 10, 9, 16, 9, true, AvifChromaSubsampling.Auto),
            ("lim2020_12", grad, 12, 9, 18, 9, false, AvifChromaSubsampling.Yuv444),
            ("full2020_8", grad, 8, 9, 13, 9, true, AvifChromaSubsampling.Auto),
            ("fcc", grad, 8, 4, 4, 4, true, AvifChromaSubsampling.Auto),
            ("smpte240", grad, 10, 7, 7, 7, false, AvifChromaSubsampling.Auto),
            ("cdncl709", grad, 8, 1, 13, 12, true, AvifChromaSubsampling.Auto),
            ("cdncl2020", grad, 10, 9, 16, 12, false, AvifChromaSubsampling.Auto),
            ("cdnclp3", grad, 8, 12, 13, 12, true, AvifChromaSubsampling.Auto),
            ("identity8", grad, 8, 1, 13, 0, true, AvifChromaSubsampling.Auto),
            ("identity10", grad, 10, 1, 13, 0, true, AvifChromaSubsampling.Auto),
            ("identitylim", grad, 8, 2, 2, 0, false, AvifChromaSubsampling.Auto),
            ("ycgco", grad, 8, 2, 2, 8, true, AvifChromaSubsampling.Yuv444),
            ("ycgco420", grad, 10, 2, 2, 8, true, AvifChromaSubsampling.Auto),
            ("ycgcore", grad, 10, 2, 2, 16, true, AvifChromaSubsampling.Yuv444),
            ("ycgcoro", grad, 10, 2, 2, 17, true, AvifChromaSubsampling.Yuv444),
            ("graylim", gray, 8, 1, 13, 6, false, AvifChromaSubsampling.Auto),
            ("graylim10", gray, 10, 1, 13, 6, false, AvifChromaSubsampling.Auto),
            ("rgbalim", rgba, 8, 1, 13, 6, false, AvifChromaSubsampling.Auto),
        };
        foreach (var c in cases)
        {
            byte[] avif = HeifCoder.EncodeAvif(c.Img, new AvifEncodeOptions
            {
                Qp = 6, BitDepth = c.Bd, ColorPrimaries = c.Cp, TransferCharacteristics = c.Tc, MatrixCoefficients = c.Mc,
                FullRange = c.Full, ChromaSubsampling = c.Ss,
            });
            File.WriteAllBytes(Path.Combine(outDir, c.Name + ".avif"), avif);
            var dec = HeifCoder.Decode(avif);
            File.WriteAllBytes(Path.Combine(outDir, c.Name + ".ours.rgb48"), Rgb48(dec));
            var m = dec.Metadata.Cicp!;
            string src = c.Img == grad ? "grad" : c.Img == gray ? "gray" : "rgba";
            log.AppendLine($"{c.Name} {src} {c.Img.Columns}x{c.Img.Rows} {c.Bd} {m.ColorPrimaries} {m.TransferCharacteristics} {m.MatrixCoefficients} {(m.FullRange ? 1 : 0)}");
        }

        // Limited-range alpha (legal in AVIF 1.0.0): colour item + a studio-range monochrome alpha item.
        foreach (int bd in new[] { 8, 10 })
        {
            int w = 160, h = 96, max = (1 << bd) - 1;
            var yv = new ushort[w * h]; var uv = new ushort[(w / 2) * (h / 2)]; var vv = new ushort[uv.Length]; var av = new ushort[w * h];
            for (int i = 0; i < yv.Length; i++) { yv[i] = (ushort)(max / 2); av[i] = (ushort)((16 << (bd - 8)) + (i % w) * (219 << (bd - 8)) / (w - 1)); }
            Array.Fill(uv, (ushort)(1 << (bd - 1))); Array.Fill(vv, (ushort)(1 << (bd - 1)));
            (byte[] cs, byte[] cf) = Av1StillImageEncoder.BuildColorObus(yv, uv, vv, w, h, 40, bd);
            (byte[] asq, byte[] af) = Av1StillImageEncoder.BuildMonochromeObus(av, w, h, 8, bd, new Av1ObuWriter.Av1ColorDesc(2, 2, 2, false));
            byte[] file = Av1AvifWriter.BuildAvifWithAlpha(cs, cf, asq, af, w, h, colorMonochrome: false, bd);
            string name = $"alphalim{bd}";
            File.WriteAllBytes(Path.Combine(outDir, name + ".avif"), file);
            File.WriteAllBytes(Path.Combine(outDir, name + ".ours.rgb48"), Rgb48(HeifCoder.Decode(file)));
            log.AppendLine($"{name} none {w}x{h} {bd} 2 2 6 1");
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Container metadata verification (trigger hbd_meta.txt): (a) decode every scratch/meta_in/*.avif (made by
    // Pillow/libavif) and dump what we extracted (ICC, Exif, XMP, orientation, CICP); (b) encode a test image carrying
    // meta_in/test.icc (+ test.exif / test.xmp when present) to meta_out/ours*.avif for Pillow to read back.
    [Test, NotInParallel]
    public void Meta()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_meta.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string inDir = Path.Combine(Scratch, "meta_in"), outDir = Path.Combine(Scratch, "meta_out");
        Directory.CreateDirectory(outDir);
        foreach (string f in Directory.GetFiles(inDir, "*.avif"))
        {
            var img = HeifCoder.Decode(File.ReadAllBytes(f));
            string stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f));
            if (img.IccProfile != null) File.WriteAllBytes(stem + ".icc", img.IccProfile);
            if (img.Metadata.Xmp != null) File.WriteAllText(stem + ".xmp", img.Metadata.Xmp);
            if (img.Metadata.ExifProfile is { } ex)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var e in ex.Ifd0Tags) sb.Append(e.Tag).Append(' ').Append(e.GetString() ?? Convert.ToHexString(e.Value)).AppendLine();
                File.WriteAllText(stem + ".exif.txt", sb.ToString());
            }
            File.WriteAllText(stem + ".txt", $"{img.Columns}x{img.Rows} cicp={img.Metadata.Cicp} icc={img.IccProfile?.Length ?? -1}");
            File.WriteAllBytes(stem + ".rgb48", Rgb48(img));
        }
        var src = Gradient(96, 64, alpha: false, gray: false);
        string icc = Path.Combine(inDir, "test.icc");
        if (File.Exists(icc)) src.Metadata.IccProfile = new SharpImage.Metadata.IccProfile(File.ReadAllBytes(icc));
        File.WriteAllBytes(Path.Combine(outDir, "ours_icc.avif"), HeifCoder.EncodeAvif(src));
        string exifIn = Path.Combine(inDir, "test.exif"), xmpIn = Path.Combine(inDir, "test.xmp");
        if (File.Exists(exifIn)) src.Metadata.ExifProfile = SharpImage.Metadata.ExifParser.ParseFromTiff(File.ReadAllBytes(exifIn));
        if (File.Exists(xmpIn)) src.Metadata.Xmp = File.ReadAllText(xmpIn);
        File.WriteAllBytes(Path.Combine(outDir, "ours_meta.avif"), HeifCoder.EncodeAvif(src));
        // Orientation 1..8 → irot/imir (asymmetric 96x64 gradient), plus clap crops.
        for (int o = 1; o <= 8; o++)
        {
            var oi = Gradient(96, 64, alpha: false, gray: false);
            oi.Orientation = (OrientationType)o;
            File.WriteAllBytes(Path.Combine(outDir, $"ours_orient{o}.avif"), HeifCoder.EncodeAvif(oi, new AvifEncodeOptions { Qp = 2 }));
        }
        var ci = Gradient(96, 64, alpha: false, gray: false);
        File.WriteAllBytes(Path.Combine(outDir, "ours_clap.avif"), HeifCoder.EncodeAvif(ci, new AvifEncodeOptions { Qp = 2, CropRect = (10, 7, 51, 33) }));
        var rgba = Gradient(96, 64, alpha: true, gray: false);
        rgba.Metadata = src.Metadata.Clone();
        File.WriteAllBytes(Path.Combine(outDir, "ours_icc_alpha.avif"), HeifCoder.EncodeAvif(rgba, new AvifEncodeOptions { BitDepth = 10 }));
    }

    // Decodes externally produced AVIFs (e.g. libavif references) listed one path per line in hbd_decode.txt,
    // writing our HeifCoder 16-bit RGB(A) (.ours.rgb48) and our decoder's native planes (.ours.yuv) beside each.
    [Test, NotInParallel]
    public void DecodeExternal()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_decode.txt");
        if (!File.Exists(trig)) return;
        var files = File.ReadAllLines(trig);
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            string stem = Path.ChangeExtension(f, null);
            Environment.SetEnvironmentVariable("AV1_DUMP10", stem + ".ours.yuv");
            try
            {
                var img = HeifCoder.Decode(File.ReadAllBytes(f));
                File.WriteAllBytes(stem + ".ours.rgb48", Rgb48(img));
                log.AppendLine($"{Path.GetFileName(f)} {img.Columns}x{img.Rows} alpha={img.HasAlpha}");
            }
            catch (Exception e) { log.AppendLine($"{Path.GetFileName(f)} ERROR {e.GetType().Name}: {e.Message}\n  decoder: {Av1Decoder.LastDecodeError}"); }
            finally { Environment.SetEnvironmentVariable("AV1_DUMP10", null); }
        }
        File.WriteAllText(Path.Combine(Scratch, "hbd", "decode_log.txt"), log.ToString());
    }

    // Isolates in-loop-filter conformance at odd picture edges: encodes odd-size gradients at q28 with deblock only,
    // CDEF only, both, and neither (trigger hbd_edge.txt), dumping .avif + our native planes for the ffmpeg diff.
    [Test, NotInParallel]
    public void EdgeFilters()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_edge.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "hbd_edge");
        Directory.CreateDirectory(outDir);
        var log = new System.Text.StringBuilder();
        bool db0 = Av1StillImageEncoder.UseDeblockSearch, cd0 = Av1StillImageEncoder.UseCdefSearch;
        try
        {
            foreach (var (w, h) in new[] { (257, 131), (200, 131), (256, 130), (129, 67) })
            {
                var img = Gradient(w, h, alpha: false, gray: false);
                foreach (var (db, cd) in new[] { (false, false), (true, false), (false, true), (true, true) })
                {
                    Av1StillImageEncoder.UseDeblockSearch = db;
                    Av1StillImageEncoder.UseCdefSearch = cd;
                    string stem = $"e{w}x{h}_db{(db ? 1 : 0)}_cdef{(cd ? 1 : 0)}";   // case-distinct names collide on NTFS
                    byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = 28, BitDepth = 8 });
                    File.WriteAllBytes(Path.Combine(outDir, stem + ".avif"), avif);
                    Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, stem + ".ours.yuv"));
                    HeifCoder.Decode(avif);
                    Environment.SetEnvironmentVariable("AV1_DUMP10", null);
                    log.AppendLine($"{stem} {w} {h}");
                }
            }
        }
        finally { Av1StillImageEncoder.UseDeblockSearch = db0; Av1StillImageEncoder.UseCdefSearch = cd0; }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Partial-edge conformance isolation (trigger hbd_iso.txt): odd-size gradients, loop filters off, a minimal tool
    // set (DC-only luma, no filter-intra / full tx set / tx-depth / CfL / UV search / rect-ext-sub8 partitions), then
    // one luma-mode family or one tool re-enabled at a time. Outputs .avif + our native planes for edge_check.py.
    [Test, NotInParallel]
    public void EdgeIsolate()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_iso.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "hbd_iso");
        Directory.CreateDirectory(outDir);
        var E = typeof(Av1StillImageEncoder);
        string[] knobs = { "UseDeblockSearch", "UseCdefSearch", "UseFilterIntra", "UseFullIntraTxSet", "UseColorTxDepth",
            "UseUvModeSearch", "UseCfl", "UseRectPartition", "UseExtPartition", "UseSub8Partition", "UseIntraEdgeFilter" };
        var bfS = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var saved = new System.Collections.Generic.Dictionary<string, object?>();
        foreach (var k in knobs) saved[k] = E.GetField(k, bfS)!.GetValue(null);
        void Set(string k, bool v) => E.GetField(k, bfS)!.SetValue(null, v);
        void Minimal() { foreach (var k in knobs) Set(k, false); Set("UseIntraEdgeFilter", (bool)saved["UseIntraEdgeFilter"]!); }
        bool Dir(Av1IntraPredMode m) => m >= Av1IntraPredMode.Vertical && m <= Av1IntraPredMode.VerticalLeft;
        var variants = new (string Name, Func<Av1IntraPredMode, int, bool> Filter, string? Tool)[]
        {
            ("dc", (m, d) => m == Av1IntraPredMode.Dc, null),
            ("dir0", (m, d) => m == Av1IntraPredMode.Dc || (Dir(m) && d == 0), null),
            ("dirAll", (m, d) => m == Av1IntraPredMode.Dc || Dir(m), null),
            ("smooth", (m, d) => m is Av1IntraPredMode.Dc or Av1IntraPredMode.Smooth or Av1IntraPredMode.SmoothV or Av1IntraPredMode.SmoothH, null),
            ("paeth", (m, d) => m is Av1IntraPredMode.Dc or Av1IntraPredMode.Paeth, null),
            ("all", (m, d) => true, null),
            ("dc+fi", (m, d) => m == Av1IntraPredMode.Dc, "UseFilterIntra"),
            ("dc+ftx", (m, d) => m == Av1IntraPredMode.Dc, "UseFullIntraTxSet"),
            ("dc+txd", (m, d) => m == Av1IntraPredMode.Dc, "UseColorTxDepth"),
            ("dc+uv", (m, d) => m == Av1IntraPredMode.Dc, "UseUvModeSearch"),
            ("dc+cfl", (m, d) => m == Av1IntraPredMode.Dc, "UseCfl"),
            ("dc+rect", (m, d) => m == Av1IntraPredMode.Dc, "UseRectPartition"),
            ("dc+sub8", (m, d) => m == Av1IntraPredMode.Dc, "UseSub8Partition"),
            ("dc+ext", (m, d) => m == Av1IntraPredMode.Dc, "UseExtPartition+UseRectPartition"),
            ("full", (m, d) => true, "ALL"),
            ("full-fi", (m, d) => true, "ALL-UseFilterIntra"),
            ("full-ftx", (m, d) => true, "ALL-UseFullIntraTxSet"),
            ("full-txd", (m, d) => true, "ALL-UseColorTxDepth"),
            ("full-uv", (m, d) => true, "ALL-UseUvModeSearch"),
            ("full-cfl", (m, d) => true, "ALL-UseCfl"),
            ("full-ext", (m, d) => true, "ALL-UseExtPartition"),
            ("full-rect", (m, d) => true, "ALL-UseRectPartition-UseExtPartition"),
            ("full-sub8", (m, d) => true, "ALL-UseSub8Partition"),
        };
        var log = new System.Text.StringBuilder();
        try
        {
            foreach (var (w, h) in new[] { (257, 131), (129, 67) })
            {
                var img = Gradient(w, h, alpha: false, gray: false);
                foreach (var (name, filter, tool) in variants)
                {
                    Minimal();
                    if (tool != null && tool.StartsWith("ALL"))
                    {
                        foreach (var k in knobs) if (k is not ("UseDeblockSearch" or "UseCdefSearch")) Set(k, (bool)saved[k]!);
                        foreach (var off in tool.Split('-')[1..]) Set(off, false);
                    }
                    else if (tool != null) foreach (var on in tool.Split('+')) Set(on, true);
                    Av1StillImageEncoder.DbgLumaModeFilter = filter;
                    string stem = $"i{w}x{h}_{name}";
                    byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = 28, BitDepth = 8 });
                    File.WriteAllBytes(Path.Combine(outDir, stem + ".avif"), avif);
                    Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, stem + ".ours.yuv"));
                    HeifCoder.Decode(avif);
                    Environment.SetEnvironmentVariable("AV1_DUMP10", null);
                    log.AppendLine($"{stem} {w} {h}");
                }
            }
        }
        finally
        {
            foreach (var k in knobs) E.GetField(k, bfS)!.SetValue(null, saved[k]);
            Av1StillImageEncoder.DbgLumaModeFilter = null;
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Encoder determinism probe (trigger hbd_det.txt): the same image + config must give identical bytes regardless
    // of what was encoded before in the process.
    [Test, NotInParallel]
    public void Determinism()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_det.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        var a = Gradient(257, 131, alpha: false, gray: false);
        var other = Gradient(129, 67, alpha: false, gray: true);
        string H(byte[] b) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(b))[..8] + $"/{b.Length}";
        var sb = new System.Text.StringBuilder();
        var opt = new AvifEncodeOptions { Qp = 28, BitDepth = 8 };
        sb.AppendLine("a#1 " + H(HeifCoder.EncodeAvif(a, opt)));
        sb.AppendLine("a#2 " + H(HeifCoder.EncodeAvif(a, opt)));
        HeifCoder.EncodeAvif(other, opt);
        sb.AppendLine("a#3 after other " + H(HeifCoder.EncodeAvif(a, opt)));
        Av1StillImageEncoder.UseDeblockSearch = false; Av1StillImageEncoder.UseCdefSearch = false;
        sb.AppendLine("a nofilt#1 " + H(HeifCoder.EncodeAvif(a, opt)));
        sb.AppendLine("a nofilt#2 " + H(HeifCoder.EncodeAvif(a, opt)));
        Av1StillImageEncoder.UseDeblockSearch = true; Av1StillImageEncoder.UseCdefSearch = true;
        File.WriteAllText(Path.Combine(Scratch, "hbd", "det.txt"), sb.ToString());
    }

    // Decoder-state probe for a stream our decoder rejects: decodes the AVIF's mdat payload directly and reports
    // the tile/frame-header state via reflection (trigger hbd_probe.txt = one .avif path).
    [Test, NotInParallel]
    public void ProbeDecoderState()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_probe.txt");
        if (!File.Exists(trig)) return;
        string f = File.ReadAllText(trig).Trim();
        File.Delete(trig);
        byte[] data = File.ReadAllBytes(f);
        int m = 0;
        for (int i = 4; i + 4 <= data.Length; i++)
            if (data[i] == 'm' && data[i + 1] == 'd' && data[i + 2] == 'a' && data[i + 3] == 't') { m = i + 4; break; }
        var dec = new Av1Decoder();
        var fr = dec.Decode(data.AsSpan(m), 0, true);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"frame={(fr == null ? "NULL" : "ok")} err={Av1Decoder.LastDecodeError}");
        foreach (var fld in typeof(Av1Decoder).GetFields(bf))
        {
            if (fld.Name is "tilesCollected" or "tileGroupCount" or "hasSequenceHeader" or "isReady")
                sb.AppendLine($"{fld.Name}={fld.GetValue(dec)}");
            if (fld.Name == "frameHdr")
            {
                var fh = fld.GetValue(dec)!;
                foreach (var p in fh.GetType().GetProperties())
                    if (p.Name is "TileCols" or "TileRows" or "Width" or "Height" or "ShowFrame" or "FrameType" or "TileColsLog2" or "TileRowsLog2" or "UniformTileSpacing")
                        sb.AppendLine($"  fh.{p.Name}={p.GetValue(fh)}");
                foreach (var ff in fh.GetType().GetFields())
                    if (ff.Name is "TileCols" or "TileRows" or "Width" or "Height" or "ShowFrame" or "TileColsLog2" or "TileRowsLog2")
                        sb.AppendLine($"  fh.{ff.Name}={ff.GetValue(fh)}");
            }
        }
        var ctxF = typeof(Av1Decoder).GetField("ctx", bf)?.GetValue(dec);
        var tss = ctxF?.GetType().GetField("TileStates")?.GetValue(ctxF) as Av1TileState[];
        if (tss != null && tss.Length > 0) sb.AppendLine($"tile0 LastQIdx={tss[0].LastQIdx} LastDeltaLf=[{string.Join(",", tss[0].LastDeltaLf)}]");
        // Full header dump (all fields/properties of the sequence + frame header objects).
        foreach (var name in new[] { "frameHdr", "seqHdr" })
        {
            var fld = typeof(Av1Decoder).GetField(name, bf);
            var o = fld?.GetValue(dec);
            if (o == null) continue;
            sb.AppendLine($"== {name}");
            foreach (var p in o.GetType().GetProperties())
            {
                try { var v = p.GetIndexParameters().Length == 0 ? p.GetValue(o) : null; if (v != null && v is not System.Array) sb.AppendLine($"  {p.Name}={v}"); } catch { }
            }
            foreach (var ff in o.GetType().GetFields())
            {
                var v = ff.GetValue(o);
                if (v is System.Array arr) sb.AppendLine($"  {ff.Name}=[{string.Join(",", System.Linq.Enumerable.Take(System.Linq.Enumerable.Cast<object>(arr), 8))}]");
                else sb.AppendLine($"  {ff.Name}={v}");
            }
        }
        File.WriteAllText(Path.Combine(Scratch, "hbd", "probe_state.txt"), sb.ToString());
    }

    // Synthetic 16-bit image: smooth diagonal colour ramps (+ optional smooth alpha), full 16-bit precision.
    private static ImageFrame Gradient(int w, int h, bool alpha, bool gray)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                double t = (x + 0.37 * y) / (w + 0.37 * h);
                double r = t, g = 0.5 + 0.45 * Math.Sin(6.0 * t + y * 0.02), b = 1.0 - 0.8 * t;
                if (gray) g = b = r;
                row[x * ch] = (ushort)Math.Round(r * 65535);
                row[x * ch + 1] = (ushort)Math.Round(g * 65535);
                row[x * ch + 2] = (ushort)Math.Round(b * 65535);
                if (alpha) row[x * ch + 3] = (ushort)Math.Round((0.2 + 0.8 * y / (double)(h - 1)) * 65535);
            }
        }
        return f;
    }

    // Packs an image as little-endian 16-bit RGB (alpha appended as a 4th sample when present).
    private static byte[] Rgb48(ImageFrame img)
    {
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        int outCh = img.HasAlpha ? 4 : 3;
        var o = new byte[w * h * outCh * 2];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < outCh; c++)
                {
                    int src = c < 3 ? (ch >= 3 ? c : 0) : ch - 1;
                    ushort v = row[x * ch + src];
                    o[p++] = (byte)v; o[p++] = (byte)(v >> 8);
                }
            }
        }
        return o;
    }
}
