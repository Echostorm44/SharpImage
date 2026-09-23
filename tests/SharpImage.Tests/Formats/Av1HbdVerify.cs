using System;
using System.IO;
using System.Linq;
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
            string stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f));
            ImageFrame img;
            try { img = HeifCoder.Decode(File.ReadAllBytes(f)); }
            catch (Exception e) { File.WriteAllText(stem + ".err", e.GetType().Name + ": " + e.Message); continue; }
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
        foreach (int pbd in new[] { 8, 10 })
        {
            var pr = Gradient(96, 64, alpha: true, gray: false);
            byte[] pf = HeifCoder.EncodeAvif(pr, new AvifEncodeOptions { BitDepth = pbd, PremultiplyAlpha = true, Qp = 10 });
            File.WriteAllBytes(Path.Combine(outDir, $"ours_prem{pbd}.avif"), pf);
            File.WriteAllBytes(Path.Combine(outDir, $"ours_prem{pbd}.rgb48"), Rgb48(HeifCoder.Decode(pf)));
            File.WriteAllBytes(Path.Combine(outDir, $"ours_prem{pbd}.src.rgb48"), Rgb48(pr));
        }
        var hdr = Gradient(96, 64, alpha: false, gray: false);
        File.WriteAllBytes(Path.Combine(outDir, "ours_hdr.avif"), HeifCoder.EncodeAvif(hdr, new AvifEncodeOptions
        {
            BitDepth = 10, ColorPrimaries = 9, TransferCharacteristics = 16, MatrixCoefficients = 9,
            ContentLightLevel = new SharpImage.Metadata.ContentLightLevel(1000, 400),
            // BT.2020 primaries (G, B, R) / D65, 1000 cd/m2 max, 0.005 min — the classic HDR10 example
            MasteringDisplay = new SharpImage.Metadata.MasteringDisplayColourVolume(8500, 39850, 6550, 2300, 35400, 14600, 15635, 16450, 10000000, 50),
        }));
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

    // Dumps every av01 item of the files listed in hbd_items.txt as native planes (<file>.item<ID>.yuv, u16 LE) for an
    // exact per-item comparison with ffmpeg/dav1d (which decodes the primary/colour item).
    [Test, NotInParallel]
    public void DecodeItems()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_items.txt");
        if (!File.Exists(trig)) return;
        var files = File.ReadAllLines(trig);
        File.Delete(trig);
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            var c = HeifContainer.Parse(File.ReadAllBytes(f));
            foreach (var item in c.Items.Values)
            {
                if (item.Type != "av01" || c.ItemData(item.Id) is not { } coded) continue;
                string dump = Path.ChangeExtension(f, null) + $".item{item.Id}.yuv";
                Environment.SetEnvironmentVariable("AV1_DUMP10", dump);
                try { new Av1Decoder().Decode(coded, 0, isKeyframe: true)?.Dispose(); }
                catch (Exception e) { File.WriteAllText(dump + ".err", e.ToString() + Environment.NewLine + Av1Decoder.LastDecodeError); }
                finally { Environment.SetEnvironmentVariable("AV1_DUMP10", null); }
            }
        }
    }

    // Concurrency probe (trigger hbd_race.txt, one file path): decodes the file on 8 threads, 20 rounds, and logs how
    // many results differ from a single-threaded decode (race detector for shared decoder state).
    [Test, NotInParallel]
    public void Race()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_race.txt");
        if (!File.Exists(trig)) return;
        string f = File.ReadAllText(trig).Trim();
        File.Delete(trig);
        byte[] data = File.ReadAllBytes(f);
        byte[] reference = Rgb48(HeifCoder.Decode(data));
        int bad = 0, total = 0;
        for (int round = 0; round < 20; round++)
        {
            var results = new byte[8][];
            System.Threading.Tasks.Parallel.For(0, 8, i => results[i] = Rgb48(HeifCoder.Decode(data)));
            foreach (var r in results) { total++; if (!r.AsSpan().SequenceEqual(reference)) bad++; }
        }
        // Dirty-pool probe: fill the shared pools with random data, then decode single-threaded.
        {
            var rng = new Random(5);
            for (int k = 0; k < 64; k++)
            {
                int n = 1 << rng.Next(6, 20);
                var bb = System.Buffers.ArrayPool<byte>.Shared.Rent(n); rng.NextBytes(bb); System.Buffers.ArrayPool<byte>.Shared.Return(bb);
                var us = System.Buffers.ArrayPool<ushort>.Shared.Rent(n); for (int q = 0; q < us.Length; q++) us[q] = (ushort)rng.Next(); System.Buffers.ArrayPool<ushort>.Shared.Return(us);
            }
            for (int sz = 16; sz <= 1 << 22; sz <<= 1)
            {
                var bb = System.Buffers.ArrayPool<byte>.Shared.Rent(sz); Array.Fill(bb, (byte)0xAB); System.Buffers.ArrayPool<byte>.Shared.Return(bb);
                var us = System.Buffers.ArrayPool<ushort>.Shared.Rent(sz); Array.Fill(us, (ushort)0x1234); System.Buffers.ArrayPool<ushort>.Shared.Return(us);
            }
            bool dirtyDiff = !Rgb48(HeifCoder.Decode(data)).AsSpan().SequenceEqual(reference);
            byte[]? onWorker = null;
            System.Threading.Tasks.Task.Run(() => onWorker = Rgb48(HeifCoder.Decode(data))).Wait();
            File.AppendAllText(Path.Combine(Scratch, "race_dirty.txt"), $"single worker-thread decode differs: {!onWorker!.AsSpan().SequenceEqual(reference)}" + Environment.NewLine);
            File.AppendAllText(Path.Combine(Scratch, "race_dirty.txt"), $"dirty-pool single-thread differs: {dirtyDiff}" + Environment.NewLine);
        }
        // Input-mutation probe: does a decode modify the caller's byte[]? And do private copies stop the race?
        {
            byte[] copy = (byte[])data.Clone();
            HeifCoder.Decode(copy);
            bool mutated = !copy.AsSpan().SequenceEqual(data);
            int privBad = 0;
            for (int round = 0; round < 10; round++)
            {
                var rs = new byte[8][];
                System.Threading.Tasks.Parallel.For(0, 8, i => rs[i] = Rgb48(HeifCoder.Decode((byte[])data.Clone())));
                foreach (var r in rs) if (!r.AsSpan().SequenceEqual(reference)) privBad++;
            }
            File.AppendAllText(Path.Combine(Scratch, "race_dirty.txt"), $"input mutated by decode: {mutated}; private-copy race: {privBad}/80" + Environment.NewLine);
        }
        // Cross-file probe: decode this file on one thread while another thread decodes an ordinary AVIF.
        {
            byte[] other = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "avif_conformance", "sharpimage_8bit_420_257x131_lf_cdef.avif"));
            int crossBad = 0;
            for (int round = 0; round < 40; round++)
            {
                byte[]? mine = null;
                System.Threading.Tasks.Parallel.Invoke(
                    () => mine = Rgb48(HeifCoder.Decode(data)),
                    () => { for (int k = 0; k < 3; k++) HeifCoder.Decode(other); });
                if (!mine!.AsSpan().SequenceEqual(reference)) crossBad++;
            }
            File.AppendAllText(Path.Combine(Scratch, "race_dirty.txt"), $"cross-file differs: {crossBad}/40" + Environment.NewLine);
        }
        // Per-item, raw decoder: which AV1 item's planes race?
        var c = HeifContainer.Parse(data);
        var sb = new System.Text.StringBuilder($"{bad}/{total} differ;");
        foreach (var item in c.Items.Values)
        {
            if (item.Type != "av01" || c.ItemData(item.Id) is not { } coded) continue;
            static byte[] Planes(byte[] coded)
            {
                using var y = new Av1Decoder().Decode(coded, 0, isKeyframe: true)!;
                var o = new System.Collections.Generic.List<byte>();
                o.AddRange(y.YPlane.ToArray()); o.AddRange(y.UPlane.ToArray()); o.AddRange(y.VPlane.ToArray());
                return o.ToArray();
            }
            byte[] refp = Planes(coded);
            int ibad = 0;
            for (int round = 0; round < 20; round++)
            {
                var rs = new byte[8][];
                System.Threading.Tasks.Parallel.For(0, 8, i => rs[i] = Planes(coded));
                foreach (var r in rs)
                    if (!r.AsSpan().SequenceEqual(refp))
                    {
                        ibad++;
                        if (ibad <= 4)
                        {
                            int first = -1, n = 0;
                            for (int k = 0; k < r.Length; k++) if (r[k] != refp[k]) { n++; if (first < 0) first = k; }
                            sb.Append($" [item{item.Id} first={first} n={n} len={r.Length} bad[0..6]={string.Join(",", r.Take(6))} ref[0..6]={string.Join(",", refp.Take(6))}]");
                        }
                    }
            }
            sb.Append($" item{item.Id}:{ibad}/160");
        }
        File.WriteAllText(Path.Combine(Scratch, "race.txt"), sb.ToString());
    }

    // Static-state detector (trigger hbd_statics.txt, one file): hashes every static field of the AV1/HEIF types before
    // and after a decode; any static whose value/content changes is shared mutable state (thread-safety hazard).
    [Test, NotInParallel]
    public void Statics()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_statics.txt");
        if (!File.Exists(trig)) return;
        string f = File.ReadAllText(trig).Trim();
        File.Delete(trig);
        var asm = typeof(HeifCoder).Assembly;
        var fields = new System.Collections.Generic.List<System.Reflection.FieldInfo>();
        foreach (var ty in asm.GetTypes())
            if (ty.Namespace is "SharpImage.Formats.Av1" or "SharpImage.Formats" && !ty.ContainsGenericParameters)
                foreach (var fi in ty.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    if (!fi.IsLiteral) fields.Add(fi);
        static long H(object? o)
        {
            if (o is null) return 0;
            if (o is Array a)
            {
                long h = a.Length;
                foreach (var e in a) h = h * 31 + (e is Array inner ? H(inner) : e?.GetHashCode() ?? 0);
                return h;
            }
            return o.GetHashCode();
        }
        var before = new long[fields.Count];
        for (int i = 0; i < fields.Count; i++) { try { before[i] = H(fields[i].GetValue(null)); } catch { before[i] = -1; } }
        HeifCoder.Decode(File.ReadAllBytes(f));
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < fields.Count; i++)
        {
            long h; try { h = H(fields[i].GetValue(null)); } catch { h = -1; }
            if (h != before[i]) sb.AppendLine($"{fields[i].DeclaringType!.Name}.{fields[i].Name}");
        }
        File.WriteAllText(Path.Combine(Scratch, "statics.txt"), sb.Length == 0 ? "none" : sb.ToString());
    }

    // Large-frame probe (trigger hbd_large.txt: lines "WxH"): encodes a smooth gradient at each size to scratch/large/.
    [Test, NotInParallel]
    public void Large()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_large.txt");
        if (!File.Exists(trig)) return;
        var sizes = File.ReadAllLines(trig);
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "large");
        Directory.CreateDirectory(outDir);
        foreach (var s in sizes)
        {
            var parts = s.Trim().Split(' ')[0].Split('x');
            if (parts.Length != 2) continue;
            int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
            // optional 3rd/4th fields: bit depth, "a" (alpha) / "444"
            var extra = s.Trim().Split(' ');
            int bd = extra.Length > 1 ? int.Parse(extra[1]) : 8;
            bool alpha = extra.Contains("a"), yuv444 = extra.Contains("444"), gray = extra.Contains("g");
            string stem = $"g{w}x{h}_b{bd}{(alpha ? "a" : "")}{(yuv444 ? "444" : "")}{(gray ? "g" : "")}";
            try
            {
                var img = Gradient(w, h, alpha: alpha, gray: gray);
                byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Qp = 30, BitDepth = bd,
                    ChromaSubsampling = yuv444 ? AvifChromaSubsampling.Yuv444 : AvifChromaSubsampling.Auto });
                File.WriteAllBytes(Path.Combine(outDir, stem + ".avif"), avif);
                if (!alpha) Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, stem + ".item1.yuv"));
                var dec = HeifCoder.Decode(avif);
                Environment.SetEnvironmentVariable("AV1_DUMP10", null);
                double se = 0; long n = 0;
                for (int yy = 0; yy < h; yy += 7) { var r0 = img.GetPixelRow(yy); var r1 = dec.GetPixelRow(yy); for (int k = 0; k < r0.Length; k += 5) { double d = r0[k] - r1[k]; se += d * d; n++; } }
                File.WriteAllText(Path.Combine(outDir, stem + ".txt"), $"{stem} bytes={avif.Length} psnr={10 * Math.Log10(65535.0 * 65535.0 / (se / n)):F2}");
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(outDir, stem + ".err"), e.GetType().Name + ": " + e.Message); }
        }
    }

    // Lossless probe (trigger hbd_lossless.txt): encodes images losslessly, checks our decode is bit-exact at the coded
    // depth, and dumps .avif / .src.rgb48 / .item1.yuv for dav1d + libavif checks. Manifest lines: stem WxH bd exact.
    [Test, NotInParallel]
    public void Lossless()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_lossless.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "lossless");
        Directory.CreateDirectory(outDir);
        var rng = new Random(9);
        ImageFrame Noisy(int w, int h, bool alpha, bool gray, int bits)
        {
            var f = Gradient(w, h, alpha, gray);
            int ch = f.NumberOfChannels, q = 16 - bits;
            for (int y = 0; y < h; y++)
            {
                var row = f.GetPixelRowForWrite(y);
                for (int i = 0; i < w * ch; i++)
                {
                    int v = Math.Clamp(row[i] + rng.Next(-3000, 3000), 0, 65535) >> q;          // value at `bits` depth
                    row[i] = (ushort)Math.Round(v * 65535.0 / ((1 << bits) - 1));              // exact at that depth
                }
                if (gray) for (int x = 0; x < w; x++) { row[x * ch + 1] = row[x * ch]; row[x * ch + 2] = row[x * ch]; }
            }
            return f;
        }
        var log = new System.Text.StringBuilder();
        var cases = new (string Name, int W, int H, bool A, bool G, int Bd, int? Mc, AvifChromaSubsampling Ss)[]
        {
            ("rgb8", 257, 131, false, false, 8, null, AvifChromaSubsampling.Auto),
            ("rgba8", 160, 96, true, false, 8, null, AvifChromaSubsampling.Auto),
            ("rgb10", 130, 70, false, false, 10, null, AvifChromaSubsampling.Auto),
            ("rgb12", 96, 64, false, false, 12, null, AvifChromaSubsampling.Auto),
            ("gray8", 99, 67, false, true, 8, null, AvifChromaSubsampling.Auto),
            ("ycgcore10", 120, 80, false, false, 10, 16, AvifChromaSubsampling.Yuv444),
            ("yuv420_601", 128, 96, false, false, 8, 6, AvifChromaSubsampling.Yuv420),
        };
        foreach (var c in cases)
        {
            int srcBits = c.Mc == 16 ? c.Bd - 2 : c.Bd;
            var img = Noisy(c.W, c.H, c.A, c.G, srcBits);
            byte[] avif = HeifCoder.EncodeAvif(img, new AvifEncodeOptions { Lossless = true, BitDepth = c.Bd, MatrixCoefficients = c.Mc, ChromaSubsampling = c.Ss });
            File.WriteAllBytes(Path.Combine(outDir, c.Name + ".avif"), avif);
            File.WriteAllBytes(Path.Combine(outDir, c.Name + ".src.rgb48"), Rgb48(img));
            if (!c.A) Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, c.Name + ".item1.yuv"));
            ImageFrame dec;
            try { dec = HeifCoder.Decode(avif); }
            finally { Environment.SetEnvironmentVariable("AV1_DUMP10", null); }
            File.WriteAllBytes(Path.Combine(outDir, c.Name + ".ours.rgb48"), Rgb48(dec));
            long diffs = 0;
            int nch = img.NumberOfChannels;
            for (int y = 0; y < c.H; y++)
            {
                var r0 = img.GetPixelRow(y); var r1 = dec.GetPixelRow(y);
                for (int i = 0; i < c.W * nch; i++) if (r0[i] != r1[i]) diffs++;
            }
            log.AppendLine($"{c.Name} {c.W}x{c.H} bd={c.Bd} bytes={avif.Length} raw={c.W * c.H * nch * srcBits / 8} ours_exact={(diffs == 0)} diffs={diffs}");
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Film grain encode probe (trigger hbd_fg.txt): encodes with libaom test vectors / tables across layouts, depths,
    // odd sizes, grey and alpha; dumps .avif, our native colour planes with grain (.ours.yuv, u16 LE) and without
    // (.ours_ng.yuv), and our RGBA8 decode (.ours.rgba). Manifest: stem pixfmt WxH.
    [Test, NotInParallel]
    public void FilmGrainEncode()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_fg.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string outDir = Path.Combine(Scratch, "fgenc");
        Directory.CreateDirectory(outDir);
        var full = FormatRegistry.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample1.dng"));
        ImageFrame Crop(int w, int h) => Geometry.Crop(full, ((int)full.Columns - w) / 2, ((int)full.Rows - h) / 2, w, h);
        ImageFrame Grey(ImageFrame s)
        {
            int n = s.NumberOfChannels;
            for (int y = 0; y < s.Rows; y++)
            {
                var row = s.GetPixelRowForWrite(y);
                for (int x = 0; x < s.Columns; x++)
                {
                    ushort v = (ushort)((row[x * n] + row[x * n + 1] + row[x * n + 2]) / 3);
                    row[x * n] = row[x * n + 1] = row[x * n + 2] = v;
                }
            }
            return s;
        }
        var log = new System.Text.StringBuilder();
        var cases = new System.Collections.Generic.List<(string Stem, ImageFrame Img, AvifEncodeOptions O, string PixFmt)>();
        for (int v = 1; v <= 16; v++)
            cases.Add(($"v{v:D2}_8_420", Crop(257, 131), new AvifEncodeOptions { BitDepth = 8, FilmGrain = AvifFilmGrain.TestVector(v) }, "yuv420p"));
        cases.Add(("v01_10_420", Crop(384, 256), new AvifEncodeOptions { BitDepth = 10, FilmGrain = AvifFilmGrain.TestVector(1) }, "yuv420p10le"));
        cases.Add(("v03_12_444", Crop(131, 97), new AvifEncodeOptions { BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv444, FilmGrain = AvifFilmGrain.TestVector(3) }, "yuv444p12le"));
        cases.Add(("v05_10_422", Crop(257, 131), new AvifEncodeOptions { BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv422, FilmGrain = AvifFilmGrain.TestVector(5) }, "yuv422p10le"));
        cases.Add(("v02_8_444_lim709", Crop(200, 120), new AvifEncodeOptions { BitDepth = 8, ChromaSubsampling = AvifChromaSubsampling.Yuv444, MatrixCoefficients = 1, FullRange = false, FilmGrain = AvifFilmGrain.TestVector(2) }, "yuv444p"));
        cases.Add(("v07_8_gray", Gradient(200, 120, alpha: false, gray: true), new AvifEncodeOptions { BitDepth = 8, FilmGrain = AvifFilmGrain.TestVector(7) }, "gray"));
        cases.Add(("v01_10_gray", Gradient(131, 97, alpha: false, gray: true), new AvifEncodeOptions { BitDepth = 10, FilmGrain = AvifFilmGrain.TestVector(1) }, "gray10le"));
        cases.Add(("v01_8_420_alpha", Gradient(160, 96, alpha: true, gray: false), new AvifEncodeOptions { BitDepth = 8, FilmGrain = AvifFilmGrain.TestVector(1) }, "yuv420p"));
        cases.Add(("dn_8_420", Crop(384, 256), new AvifEncodeOptions { BitDepth = 8, DenoiseNoiseLevel = 25 }, "yuv420p"));
        cases.Add(("dn_10_420_odd", Crop(257, 131), new AvifEncodeOptions { BitDepth = 10, DenoiseNoiseLevel = 25 }, "yuv420p10le"));
        cases.Add(("dn_8_444_req", Crop(200, 120), new AvifEncodeOptions { BitDepth = 8, ChromaSubsampling = AvifChromaSubsampling.Yuv444, DenoiseNoiseLevel = 30, DenoiseUseRequestedLevel = true }, "yuv444p"));
        cases.Add(("dn_12_444_bs16", Crop(131, 97), new AvifEncodeOptions { BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv444, DenoiseNoiseLevel = 25, DenoiseBlockSize = 16 }, "yuv444p12le"));
        cases.Add(("dn_8_gray", Grey(Crop(200, 120)), new AvifEncodeOptions { BitDepth = 8, DenoiseNoiseLevel = 25 }, "gray"));
        cases.Add(("dn_8_420_noapply", Crop(160, 96), new AvifEncodeOptions { BitDepth = 8, DenoiseNoiseLevel = 25, DenoiseApply = false }, "yuv420p"));
        var tbl = AvifFilmGrain.ParseTable(AvifFilmGrain.TestVector(4).ToTable());
        cases.Add(("table4_8_420", Crop(257, 131), new AvifEncodeOptions { BitDepth = 8, FilmGrain = tbl }, "yuv420p"));
        foreach (var c in cases)
        {
            try
            {
                byte[] avif = HeifCoder.EncodeAvif(c.Img, c.O);
                File.WriteAllBytes(Path.Combine(outDir, c.Stem + ".avif"), avif);
                var box = HeifContainer.Parse(avif);
                byte[] item = box.ItemData(box.PrimaryId)!;
                {
                    var pd = new Av1Decoder();
                    using (pd.Decode(item, 0, isKeyframe: true)) { }
                    File.WriteAllText(Path.Combine(outDir, c.Stem + ".grain.tbl"),
                        pd.LastFilmGrain is { } lf ? AvifFilmGrain.FromAv1(lf).ToTable() : "none");
                }
                foreach (bool g in new[] { true, false })
                {
                    Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(outDir, c.Stem + (g ? ".ours.yuv" : ".ours_ng.yuv")));
                    try { using var f = new Av1Decoder { ApplyFilmGrain = g }.Decode(item, 0, isKeyframe: true); }
                    finally { Environment.SetEnvironmentVariable("AV1_DUMP10", null); }
                }
                var dec = HeifCoder.Decode(avif);
                int w = (int)dec.Columns, h = (int)dec.Rows, ch = dec.NumberOfChannels;
                var rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    var row = dec.GetPixelRow(y);
                    for (int x = 0; x < w; x++)
                        for (int k = 0; k < 4; k++)
                            rgba[(y * w + x) * 4 + k] = k < 3 ? (byte)Math.Round(row[x * ch + Math.Min(k, ch - 1 - (dec.HasAlpha ? 1 : 0))] * 255.0 / 65535)
                                : dec.HasAlpha ? (byte)Math.Round(row[x * ch + ch - 1] * 255.0 / 65535) : (byte)255;
                }
                File.WriteAllBytes(Path.Combine(outDir, c.Stem + ".ours.rgba"), rgba);
                log.AppendLine($"{c.Stem} {c.PixFmt} {w}x{h} bytes={avif.Length} {Av1NoiseModel.LastStatus}");
            }
            catch (Exception e) { log.AppendLine($"{c.Stem} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(outDir, "manifest.txt"), log.ToString());
    }

    // Grain estimation parity probe (trigger hbd_fgest.txt): for each raw YUV in fgest/manifest.txt ("name w h bd ssx
    // ssy mono level", level < 0 = all-intra estimate) runs our libaom noise-model port and writes name.ours.tbl; the
    // libaom stream name.aom.avif (same YUV, ffmpeg libaom-av1 --denoise-noise-level) is parsed to name.aom.tbl.
    [Test, NotInParallel]
    public void GrainEstimate()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_fgest.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string dir = Path.Combine(Scratch, "fgest");
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "manifest.txt")))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 8) continue;
            string name = p[0];
            int w = int.Parse(p[1]), h = int.Parse(p[2]), bd = int.Parse(p[3]), ssx = int.Parse(p[4]), ssy = int.Parse(p[5]);
            bool mono = p[6] == "1";
            float level = float.Parse(p[7], System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                byte[] raw = File.ReadAllBytes(Path.Combine(dir, name + ".yuv"));
                int cw = (w + ssx) >> ssx, chh = (h + ssy) >> ssy;
                int pos = 0;
                ushort[] Plane(int n)
                {
                    var a = new ushort[n];
                    for (int i = 0; i < n; i++) { a[i] = bd > 8 ? BitConverter.ToUInt16(raw, pos) : raw[pos]; pos += bd > 8 ? 2 : 1; }
                    return a;
                }
                ushort[] y = Plane(w * h);
                ushort[]? u = mono ? null : Plane(cw * chh), v = mono ? null : Plane(cw * chh);
                if (level < 0) level = Av1NoiseModel.AllIntraNoiseLevel(y, w, h, bd);
                var g = Av1NoiseModel.DenoiseAndModel(y, u, v, w, h, ssx, ssy, bd, level, 32, out var den);
                File.WriteAllText(Path.Combine(dir, name + ".ours.tbl"), $"level={level:R}\n" + (g?.ToTable() ?? "none\n"));
                if (g != null)
                    using (var fs = File.Create(Path.Combine(dir, name + ".ours_den.yuv")))
                        foreach (var pl in den)
                            if (pl != null)
                                foreach (var s in pl)
                                {
                                    fs.WriteByte((byte)s);
                                    if (bd > 8) fs.WriteByte((byte)(s >> 8));
                                }
                string aom = Path.Combine(dir, name + ".aom.avif");
                if (File.Exists(aom))
                {
                    var box = HeifContainer.Parse(File.ReadAllBytes(aom));
                    var dec = new Av1Decoder();
                    using (dec.Decode(box.ItemData(box.PrimaryId)!, 0, true)) { }
                    var fg = dec.LastFilmGrain;
                    File.WriteAllText(Path.Combine(dir, name + ".aom.tbl"), fg is { } f ? AvifFilmGrain.FromAv1(f).ToTable() : "none\n");
                }
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, name + ".ours.tbl"), "ERROR " + e); }
        }
    }

    // Progressive encode probe (trigger hbd_progenc.txt): writes progressive AVIFs made by our encoder to progenc/.
    [Test, NotInParallel]
    public void ProgressiveEncode()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_progenc.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string dir = Path.Combine(Scratch, "progenc");
        Directory.CreateDirectory(dir);
        var full = FormatRegistry.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample1.dng"));
        ImageFrame Crop(int w, int h) => Geometry.Crop(full, ((int)full.Columns - w) / 2, ((int)full.Rows - h) / 2, w, h);
        var cases = new (string Name, ImageFrame Img, AvifEncodeOptions O)[]
        {
            ("ours_prog_8_420", Crop(384, 256), new AvifEncodeOptions { BitDepth = 8, Progressive = true }),
            ("ours_prog_10_444_odd", Crop(257, 131), new AvifEncodeOptions { BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444, Progressive = true }),
            ("ours_prog_12_422", Crop(200, 120), new AvifEncodeOptions { BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv422, Progressive = true }),
            ("ours_prog_8_gray", Gradient(160, 97, alpha: false, gray: true), new AvifEncodeOptions { BitDepth = 8, Progressive = true }),
            ("ours_prog_8_420_alpha", Gradient(161, 97, alpha: true, gray: false), new AvifEncodeOptions { BitDepth = 8, Progressive = true }),
            ("ours_prog_8_420_grain", Crop(256, 160), new AvifEncodeOptions { BitDepth = 8, Progressive = true, FilmGrain = AvifFilmGrain.TestVector(1) }),
            ("ours_single_8_420_alpha", Gradient(161, 97, alpha: true, gray: false), new AvifEncodeOptions { BitDepth = 8 }),
        };
        foreach (var c in cases)
            File.WriteAllBytes(Path.Combine(dir, c.Name + ".avif"), HeifCoder.EncodeAvif(c.Img, c.O));
    }

    // Progressive decode probe (trigger hbd_prog.txt): HeifCoder.DecodeProgressive on every prog/*.avif, each layer
    // dumped as raw 16-bit RGBA (name_Li.ours.rgba64, little-endian) with a manifest "name layers WxH".
    [Test, NotInParallel]
    public void ProgressiveDecode()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_prog.txt");
        if (!File.Exists(trig)) return;
        string sub = File.ReadAllText(trig).Trim();
        File.Delete(trig);
        string dir = Path.Combine(Scratch, sub.Length > 0 ? sub : "prog");
        var log = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.avif"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var layers = HeifCoder.DecodeProgressive(File.ReadAllBytes(file));
                for (int i = 0; i < layers.Count; i++)
                {
                    var f = layers[i];
                    int w = (int)f.Columns, h = (int)f.Rows, ch = f.NumberOfChannels;
                    var buf = new byte[w * h * 8];
                    for (int y = 0; y < h; y++)
                    {
                        var row = f.GetPixelRow(y);
                        for (int x = 0; x < w; x++)
                            for (int k = 0; k < 4; k++)
                            {
                                ushort v = k < 3 ? row[x * ch + Math.Min(k, ch - 1 - (f.HasAlpha ? 1 : 0))] : f.HasAlpha ? row[x * ch + ch - 1] : (ushort)65535;
                                buf[(y * w + x) * 8 + k * 2] = (byte)v;
                                buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                            }
                    }
                    File.WriteAllBytes(Path.Combine(dir, $"{name}_L{i}.ours.rgba64"), buf);
                }
                log.AppendLine($"{name} {layers.Count} {layers[0].Columns}x{layers[0].Rows}");
            }
            catch (Exception e) { log.AppendLine($"{name} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "manifest.txt"), log.ToString());
    }

    // Sequence decode probe (trigger hbd_seq.txt naming a scratch subdir): HeifCoder.DecodeSequence on every *.avif, each
    // frame dumped as name_Li.ours.rgba64 with a manifest "name frames WxH timescale loop d0,d1,..".
    [Test, NotInParallel]
    public void SequenceDecode()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_seq.txt");
        if (!File.Exists(trig)) return;
        string dir = Path.Combine(Scratch, File.ReadAllText(trig).Trim());
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.avif"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var seq = HeifCoder.DecodeSequence(File.ReadAllBytes(file));
                for (int i = 0; i < seq.Count; i++)
                {
                    var f = seq[i];
                    int w = (int)f.Columns, h = (int)f.Rows, ch = f.NumberOfChannels;
                    var buf = new byte[w * h * 8];
                    for (int y = 0; y < h; y++)
                    {
                        var row = f.GetPixelRow(y);
                        for (int x = 0; x < w; x++)
                            for (int k = 0; k < 4; k++)
                            {
                                ushort v = k < 3 ? row[x * ch + Math.Min(k, ch - 1 - (f.HasAlpha ? 1 : 0))] : f.HasAlpha ? row[x * ch + ch - 1] : (ushort)65535;
                                buf[(y * w + x) * 8 + k * 2] = (byte)v;
                                buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                            }
                    }
                    File.WriteAllBytes(Path.Combine(dir, $"{name}_L{i}.ours.rgba64"), buf);
                }
                var durs = string.Join(",", Enumerable.Range(0, seq.Count).Select(i => seq[i].DurationTicks));
                log.AppendLine($"{name} {seq.Count} {seq[0].Columns}x{seq[0].Rows} {seq.Timescale} {seq.LoopCount} {durs}");
            }
            catch (Exception e) { log.AppendLine($"{name} ERROR {e.ToString().ReplaceLineEndings(" | ")} || INNER {SharpImage.Formats.Av1.Av1Decoder.LastDecodeError?.ReplaceLineEndings(" | ")}"); }
        }
        File.WriteAllText(Path.Combine(dir, "manifest.txt"), log.ToString());
    }

    // Gain map probe (trigger hbd_gm.txt naming a scratch subdir): HeifCoder.DecodeGainMap on every *.avif; the gain map
    // image dumped as name.gm.rgba64, metadata lines in manifest.txt (fractions as n/d, like avifgainmaputil).
    [Test, NotInParallel]
    public void GainMapProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_gm.txt");
        if (!File.Exists(trig)) return;
        string dir = Path.Combine(Scratch, File.ReadAllText(trig).Trim());
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.avif"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var gm = HeifCoder.DecodeGainMap(File.ReadAllBytes(file));
                if (gm == null) { log.AppendLine($"{name} NONE"); continue; }
                string F(GainMapFraction[] a) => string.Join(" ", a.Select(f => $"{f.Numerator}/{f.Denominator}"));
                log.AppendLine($"{name} base={gm.BaseHdrHeadroom.Numerator}/{gm.BaseHdrHeadroom.Denominator} alt={gm.AlternateHdrHeadroom.Numerator}/{gm.AlternateHdrHeadroom.Denominator}" +
                    $" min={F(gm.Min)} max={F(gm.Max)} boff={F(gm.BaseOffset)} aoff={F(gm.AlternateOffset)}" +
                    $" gamma={string.Join(" ", gm.Gamma.Select(f => $"{f.Numerator}/{f.Denominator}"))} usebase={gm.UseBaseColorSpace}" +
                    $" altcicp={gm.AlternateCicp} altclli={gm.AlternateContentLightLevel} altpixi={gm.AlternatePlaneCount}x{gm.AlternateDepth}" +
                    $" icc={gm.AlternateIccProfile?.Length ?? 0} img={gm.Image!.Columns}x{gm.Image.Rows} depth={gm.Image.Depth}");
                var f = gm.Image;
                int w = (int)f.Columns, h = (int)f.Rows, ch = f.NumberOfChannels;
                var buf = new byte[w * h * 8];
                for (int y = 0; y < h; y++)
                {
                    var row = f.GetPixelRow(y);
                    for (int x = 0; x < w; x++)
                        for (int k = 0; k < 4; k++)
                        {
                            ushort v = k < 3 ? row[x * ch + Math.Min(k, ch - 1 - (f.HasAlpha ? 1 : 0))] : (ushort)65535;
                            buf[(y * w + x) * 8 + k * 2] = (byte)v;
                            buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                        }
                }
                File.WriteAllBytes(Path.Combine(dir, $"{name}.gm.rgba64"), buf);
            }
            catch (Exception e) { log.AppendLine($"{name} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "manifest.txt"), log.ToString());
    }

    // libyuv scaler probe (trigger hbd_yuvscale.txt naming a cases.bin from scalecases.c): LibyuvScale.ScalePlane vs
    // libyuv's ScalePlane/ScalePlane_12 (kFilterBox) per case, report in yuvscale.txt next to it.
    [Test, NotInParallel]
    public void YuvScaleProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_yuvscale.txt");
        if (!File.Exists(trig)) return;
        string bin = File.ReadAllText(trig).Trim();
        File.Delete(trig);
        byte[] d = File.ReadAllBytes(bin);
        var log = new System.Text.StringBuilder();
        int pos = 0;
        while (pos < d.Length)
        {
            int I(int k) => BitConverter.ToInt32(d, pos + 4 * k);
            int sw = I(0), sh = I(1), dw = I(2), dh = I(3); bool hbd = I(4) != 0;
            pos += 20;
            var src = new ushort[sw * sh];
            Buffer.BlockCopy(d, pos, src, 0, sw * sh * 2); pos += sw * sh * 2;
            var want = new ushort[dw * dh];
            Buffer.BlockCopy(d, pos, want, 0, dw * dh * 2); pos += dw * dh * 2;
            var got = SharpImage.Formats.LibyuvScale.ScalePlane(src, sw, sw, sh, dw, dh, hbd);
            int bad = 0, maxd = 0, first = -1;
            for (int i = 0; i < want.Length; i++)
            {
                int df = Math.Abs(got[i] - want[i]);
                if (df > 0) { bad++; if (first < 0) first = i; }
                maxd = Math.Max(maxd, df);
            }
            log.AppendLine($"{sw}x{sh}->{dw}x{dh} hbd={hbd} bad={bad} max={maxd}" + (first >= 0 ? $" first=({first % dw},{first / dw}) got {got[first]} want {want[first]}" : ""));
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(bin)!, "yuvscale.txt"), log.ToString());
    }

    // Gain map encode probe (trigger hbd_gmenc.txt: lines "base.avif|alt.avif|out.avif|downscaling|depth|single(0/1)|444/420"):
    // the avifgainmaputil combine equivalent — ComputeGainMap on the two decodes, then a lossless base + lossless gain map
    // encode. Manifest gmenc_manifest.txt next to the outputs.
    [Test, NotInParallel]
    public void GainMapEncodeProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_gmenc.txt");
        if (!File.Exists(trig)) return;
        var lines = File.ReadAllLines(trig);
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        string dir = "";
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var p = line.Trim().Split('|');
            dir = Path.GetDirectoryName(p[2])!;
            try
            {
                var b = HeifCoder.DecodeNativeDepth(File.ReadAllBytes(p[0]));
                var a = HeifCoder.DecodeNativeDepth(File.ReadAllBytes(p[1]));
                var gm = HeifCoder.ComputeGainMap(b, a, int.Parse(p[4]), p[5] == "1");
                var opt = new AvifEncodeOptions
                {
                    Lossless = true, GainMap = gm, GainMapLossless = true, GainMapDownscaling = int.Parse(p[3]),
                    GainMapChromaSubsampling = p[6] == "420" ? AvifChromaSubsampling.Yuv420 : AvifChromaSubsampling.Yuv444,
                };
                if (p.Length > 7) { var gg = p[7].Split('x'); opt.Grid = (int.Parse(gg[0]), int.Parse(gg[1])); }
                File.WriteAllBytes(p[2], HeifCoder.EncodeAvif(b, opt));
                log.AppendLine($"{Path.GetFileName(p[2])} ok");
            }
            catch (Exception e) { log.AppendLine($"{Path.GetFileName(p[2])} ERROR {e.ToString().ReplaceLineEndings(" | ")}"); }
        }
        File.WriteAllText(Path.Combine(dir, "gmenc_manifest.txt"), log.ToString());
    }

    // Tiny-image probe (trigger hbd_tiny.txt naming a scratch subdir): encodes sizes below 8 px (and odd small ones) in
    // every mode, writing name.avif + our RGBA decode (name.ours.rgba64, 16-bit) for comparison with avifdec.
    [Test, NotInParallel]
    public void TinyProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_tiny.txt");
        if (!File.Exists(trig)) return;
        string dir = Path.Combine(Scratch, File.ReadAllText(trig).Trim());
        File.Delete(trig);
        Directory.CreateDirectory(dir);
        var log = new System.Text.StringBuilder();
        var sizes = new[] { (1, 1), (1, 2), (2, 1), (2, 2), (3, 5), (4, 4), (5, 3), (7, 7), (1, 9), (9, 1), (6, 13), (15, 2), (8, 8), (12, 4) };
        var configs = new (string Name, AvifEncodeOptions Opt, bool Alpha, bool Gray)[]
        {
            ("q420", new AvifEncodeOptions { Qp = 20 }, false, false),
            ("q444", new AvifEncodeOptions { Qp = 20, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }, false, false),
            ("q422", new AvifEncodeOptions { Qp = 20, ChromaSubsampling = AvifChromaSubsampling.Yuv422 }, false, false),
            ("d10", new AvifEncodeOptions { Qp = 20, BitDepth = 10 }, false, false),
            ("d12", new AvifEncodeOptions { Qp = 20, BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv444 }, false, false),
            ("gray", new AvifEncodeOptions { Qp = 20 }, false, true),
            ("alpha", new AvifEncodeOptions { Qp = 20 }, true, false),
            ("ll8", new AvifEncodeOptions { Lossless = true }, false, false),
            ("ll10", new AvifEncodeOptions { Lossless = true, BitDepth = 10 }, true, false),
        };
        foreach (var (w, h) in sizes)
            foreach (var (name, opt, alpha, gray) in configs)
            {
                string stem = $"{name}_{w}x{h}";
                try
                {
                    var f = new ImageFrame();
                    f.Initialize(w, h, ColorspaceType.SRGB, alpha);
                    int ch = f.NumberOfChannels;
                    for (int y = 0; y < h; y++)
                    {
                        var row = f.GetPixelRowForWrite(y);
                        for (int x = 0; x < w; x++)
                        {
                            int v = (x * 37 + y * 91 + 13) % 256;
                            row[x * ch] = (ushort)(v * 257);
                            row[x * ch + 1] = (ushort)((gray ? v : (255 - v)) * 257);
                            row[x * ch + 2] = (ushort)((gray ? v : (v * 3) % 256) * 257);
                            if (alpha) row[x * ch + 3] = (ushort)(((x + y) * 50 + 30) % 256 * 257);
                        }
                    }
                    byte[] avif = HeifCoder.EncodeAvif(f, opt);
                    File.WriteAllBytes(Path.Combine(dir, stem + ".avif"), avif);
                    var d = HeifCoder.Decode(avif);
                    int dch = d.NumberOfChannels;
                    var buf = new byte[w * h * 8];
                    for (int y = 0; y < h; y++)
                    {
                        var row = d.GetPixelRow(y);
                        for (int x = 0; x < w; x++)
                            for (int k = 0; k < 4; k++)
                            {
                                ushort v = k < 3 ? row[x * dch + Math.Min(k, dch - 1 - (d.HasAlpha ? 1 : 0))] : d.HasAlpha ? row[x * dch + dch - 1] : (ushort)65535;
                                buf[(y * w + x) * 8 + k * 2] = (byte)v;
                                buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                            }
                    }
                    File.WriteAllBytes(Path.Combine(dir, stem + ".ours.rgba64"), buf);
                    log.AppendLine($"{stem} ok {avif.Length}");
                }
                catch (Exception e) { log.AppendLine($"{stem} ERROR {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            }
        File.WriteAllText(Path.Combine(dir, "manifest.txt"), log.ToString());
    }

    // Generic encode probe (trigger hbd_enc.txt: lines "input|output.avif|opts", opts space-separated: ll, q=QP, d=DEPTH,
    // y=400|420|422|444, r=limited, mc=N, cp=N, tc=N): FormatRegistry.Read + HeifCoder.EncodeAvif; the decode is dumped
    // as output.ours.rgba64. Manifest enc_manifest.txt next to the outputs.
    [Test, NotInParallel]
    public void EncodeProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_enc.txt");
        if (!File.Exists(trig)) return;
        var lines = File.ReadAllLines(trig);
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        string dir = "";
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var p = line.Trim().Split('|');
            dir = Path.GetDirectoryName(p[1])!;
            try
            {
                var img = FormatRegistry.Read(p[0]);
                var o = new AvifEncodeOptions();
                foreach (var kv in (p.Length > 2 ? p[2] : "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = kv.Split('=');
                    switch (t[0])
                    {
                        case "ll": o.Lossless = true; break;
                        case "q": o.Qp = int.Parse(t[1]); break;
                        case "d": o.BitDepth = int.Parse(t[1]); break;
                        case "y": o.ChromaSubsampling = t[1] switch { "400" => AvifChromaSubsampling.Yuv400, "420" => AvifChromaSubsampling.Yuv420, "422" => AvifChromaSubsampling.Yuv422, _ => AvifChromaSubsampling.Yuv444 }; break;
                        case "r": o.FullRange = t[1] != "limited"; break;
                        case "mc": o.MatrixCoefficients = int.Parse(t[1]); break;
                        case "cp": o.ColorPrimaries = int.Parse(t[1]); break;
                        case "tc": o.TransferCharacteristics = int.Parse(t[1]); break;
                        case "g": { var gg = t[1].Split('x'); o.Grid = (int.Parse(gg[0]), int.Parse(gg[1])); break; }
                        case "irot": o.Rotation = int.Parse(t[1]); break;
                        case "prem": o.PremultiplyAlpha = true; break;
                    }
                }
                byte[] avif = HeifCoder.EncodeAvif(img, o);
                File.WriteAllBytes(p[1], avif);
                var d = HeifCoder.Decode(avif);
                int w = (int)d.Columns, h = (int)d.Rows, dch = d.NumberOfChannels;
                var buf = new byte[w * h * 8];
                for (int y = 0; y < h; y++)
                {
                    var row = d.GetPixelRow(y);
                    for (int x = 0; x < w; x++)
                        for (int k = 0; k < 4; k++)
                        {
                            ushort v = k < 3 ? row[x * dch + Math.Min(k, dch - 1 - (d.HasAlpha ? 1 : 0))] : d.HasAlpha ? row[x * dch + dch - 1] : (ushort)65535;
                            buf[(y * w + x) * 8 + k * 2] = (byte)v;
                            buf[(y * w + x) * 8 + k * 2 + 1] = (byte)(v >> 8);
                        }
                }
                File.WriteAllBytes(p[1] + ".ours.rgba64", buf);
                log.AppendLine($"{Path.GetFileName(p[1])} ok {avif.Length}");
            }
            catch (Exception e) { log.AppendLine($"{Path.GetFileName(p[1])} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "enc_manifest.txt"), log.ToString());
    }

    // Tone map probe (trigger hbd_tm.txt: lines "file|headroom|outName[|cp/tc|depth]"): HeifCoder.DecodeToneMapped, the
    // result dumped as outName.rgb16 (planar-free little-endian u16 RGB at native depth) + "outName WxH depth cp/tc clli".
    [Test, NotInParallel]
    public void ToneMapProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_tm.txt");
        if (!File.Exists(trig)) return;
        var lines = File.ReadAllLines(trig);
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        string dir = "";
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var p = line.Trim().Split('|');
            dir = Path.GetDirectoryName(p[2])!;
            try
            {
                SharpImage.Metadata.CicpInfo? cicp = null;
                if (p.Length > 3 && p[3].Length > 0) { var t = p[3].Split('/'); cicp = new(int.Parse(t[0]), int.Parse(t[1]), 0, true); }
                int depth = p.Length > 4 ? int.Parse(p[4]) : 0;
                var f = HeifCoder.DecodeToneMapped(File.ReadAllBytes(p[0]), float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), cicp, depth);
                int w = (int)f.Columns, h = (int)f.Rows, ch = f.NumberOfChannels;
                uint max = (1u << f.Depth) - 1;
                var buf = new byte[w * h * 6];
                for (int y = 0; y < h; y++)
                {
                    var row = f.GetPixelRow(y);
                    for (int x = 0; x < w; x++)
                        for (int k = 0; k < 3; k++)
                        {
                            ushort v = (ushort)((row[x * ch + k] * max + 32767u) / 65535u);
                            buf[(y * w + x) * 6 + k * 2] = (byte)v;
                            buf[(y * w + x) * 6 + k * 2 + 1] = (byte)(v >> 8);
                        }
                }
                File.WriteAllBytes(p[2] + ".rgb16", buf);
                var c = f.Metadata.Cicp!;
                log.AppendLine($"{Path.GetFileName(p[2])} {w}x{h} {f.Depth} {c.ColorPrimaries}/{c.TransferCharacteristics} {f.Metadata.ContentLightLevel?.MaxContentLightLevel},{f.Metadata.ContentLightLevel?.MaxFrameAverageLightLevel}");
            }
            catch (Exception e) { log.AppendLine($"{Path.GetFileName(p[2])} ERROR {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "tm_manifest.txt"), log.ToString());
    }

    // Track probe (trigger hbd_track.txt = "file|trackIndex"): decodes every sample of one sequence track with one
    // decoder, dumping each frame's native planes to track_ours_{i}.yuv and the block trace to trace_ours.txt.
    [Test, NotInParallel]
    public void TrackProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_track.txt");
        if (!File.Exists(trig)) return;
        var parts = File.ReadAllText(trig).Trim().Split('|');
        File.Delete(trig);
        byte[] data = File.ReadAllBytes(parts[0]);
        var track = AvifTracks.Parse(data).Tracks[int.Parse(parts[1])];
        var dec = new Av1Decoder();
        var log = new System.Text.StringBuilder();
        using (var trace = new StreamWriter(Path.Combine(Scratch, "trace_ours.txt")))
        {
            Av1Decode.BlockTrace = trace;
            for (int i = 0; i < track.Samples.Count; i++)
            {
                Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(Scratch, $"track_ours_{i}.yuv"));
                var (off, size) = track.Samples[i];
                using var f = dec.Decode(data.AsSpan((int)off, size), i, track.Sync[i]);
                log.AppendLine(f == null ? $"{i} null: {Av1Decoder.LastDecodeError}" : $"{i} {f.Width}x{f.Height}");
            }
            Av1Decode.BlockTrace = null;
            Environment.SetEnvironmentVariable("AV1_DUMP10", null);
        }
        File.WriteAllText(Path.Combine(Scratch, "track.txt"), log.ToString());
    }

    // Raw OBU stream probe (trigger hbd_obu.txt = path of a Section 5 .obu file): splits the stream into temporal units
    // at temporal delimiters, decodes each with one decoder, dumps every output frame to track_ours_{i}.yuv and the block
    // trace to trace_ours.txt (same outputs as TrackProbe, for trackdiff-style comparison with dav1d).
    [Test, NotInParallel]
    public void ObuProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_obu.txt");
        if (!File.Exists(trig)) return;
        byte[] data = File.ReadAllBytes(File.ReadAllText(trig).Trim());
        File.Delete(trig);
        var tus = new List<(int Off, int Len)>();
        int pos = 0, tuStart = -1;
        while (pos < data.Length)
        {
            int hdr = data[pos];
            int type = (hdr >> 3) & 15;
            bool ext = (hdr & 4) != 0;
            int p = pos + 1 + (ext ? 1 : 0);
            long size = 0;
            for (int i = 0; ; i++) { byte v = data[p++]; size |= (long)(v & 0x7f) << (7 * i); if ((v & 0x80) == 0) break; }
            if (type == 2) { if (tuStart >= 0) tus.Add((tuStart, pos - tuStart)); tuStart = pos; }
            pos = p + (int)size;
        }
        if (tuStart >= 0) tus.Add((tuStart, data.Length - tuStart));
        var dec = new Av1Decoder();
        var log = new System.Text.StringBuilder();
        int outIdx = 0;
        using (var trace = new StreamWriter(Path.Combine(Scratch, "trace_ours.txt")))
        {
            Av1Decode.BlockTrace = trace;
            for (int i = 0; i < tus.Count; i++)
            {
                Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(Scratch, $"track_ours_{outIdx}.yuv"));
                using var f = dec.Decode(data.AsSpan(tus[i].Off, tus[i].Len), i, false);
                if (f != null) outIdx++;
                log.AppendLine(f == null ? $"{i} null: {Av1Decoder.LastDecodeError?.ReplaceLineEndings(" | ")}" : $"{i} {f.Width}x{f.Height}");
            }
            Av1Decode.BlockTrace = null;
            Environment.SetEnvironmentVariable("AV1_DUMP10", null);
        }
        File.WriteAllText(Path.Combine(Scratch, "track.txt"), log.ToString());
    }

    // Sequence encode probe (trigger hbd_seqenc.txt): animated AVIFs from a moving pattern -> scratch/seqenc/*.avif.
    [Test, NotInParallel]
    public void SequenceEncode()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_seqenc.txt");
        if (!File.Exists(trig)) return;
        File.Delete(trig);
        string dir = Path.Combine(Scratch, "seqenc");
        Directory.CreateDirectory(dir);
        ImageSequence Make(int w, int h, int n, bool alpha, bool grey)
        {
            var seq = new ImageSequence { Timescale = 30, LoopCount = 3 };
            for (int k = 0; k < n; k++)
            {
                var f = new ImageFrame();
                f.Initialize(w, h, ColorspaceType.SRGB, alpha);
                int ch = f.NumberOfChannels;
                for (int y = 0; y < h; y++)
                {
                    var row = f.GetPixelRowForWrite(y);
                    for (int x = 0; x < w; x++)
                    {
                        double t = k * 0.35;
                        int rr = (int)(32767 + 32767 * Math.Sin((x + 3 * k) * 0.09 + t));
                        int gg = grey ? rr : (int)(32767 + 32767 * Math.Cos((y - 2 * k) * 0.07));
                        int bb = grey ? rr : ((x ^ y) + 9 * k) * 257 & 0xFFFF;
                        row[x * ch] = (ushort)rr; row[x * ch + 1] = (ushort)gg; row[x * ch + 2] = (ushort)bb;
                        if (alpha) row[x * ch + 3] = (ushort)Math.Clamp(65535 - ((x + y + 5 * k) * 700) % 65536, 0, 65535);
                    }
                }
                f.DurationTicks = k == 2 ? 6 : 3;
                seq.AddFrame(f);
            }
            return seq;
        }
        var log = new System.Text.StringBuilder();
        void Save(string name, ImageSequence seq, AvifEncodeOptions o)
        {
            try { File.WriteAllBytes(Path.Combine(dir, name + ".avif"), HeifCoder.EncodeAvifSequence(seq, o)); log.AppendLine(name + " ok"); }
            catch (Exception e) { log.AppendLine($"{name} ERROR {e.ToString().ReplaceLineEndings(" | ")}"); }
        }
        Save("seq_8_420", Make(96, 64, 5, false, false), new AvifEncodeOptions { Qp = 24, BitDepth = 8 });
        Save("seq_8_420_alpha", Make(96, 64, 4, true, false), new AvifEncodeOptions { Qp = 24, BitDepth = 8 });
        Save("seq_10_444", Make(80, 48, 4, false, false), new AvifEncodeOptions { Qp = 20, BitDepth = 10, ChromaSubsampling = AvifChromaSubsampling.Yuv444 });
        Save("seq_12_422_alpha", Make(72, 40, 3, true, false), new AvifEncodeOptions { Qp = 20, BitDepth = 12, ChromaSubsampling = AvifChromaSubsampling.Yuv422 });
        Save("seq_8_grey", Make(64, 64, 3, false, true), new AvifEncodeOptions { Qp = 24, BitDepth = 8 });
        Save("seq_8_lossless", Make(48, 32, 3, true, false), new AvifEncodeOptions { Lossless = true, BitDepth = 8 });
        Save("seq_8_grain", Make(96, 64, 3, false, false), new AvifEncodeOptions { Qp = 30, BitDepth = 8, FilmGrain = AvifFilmGrain.TestVector(1) });
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
    }

    // Layered-stream probe (trigger hbd_layers.txt holding an .avif path): decodes the primary item's payload and writes
    // the outcome / full exception to layers.txt.
    [Test, NotInParallel]
    public void LayersProbe()
    {
        string trig = Path.Combine(Scratch, "corpus", "hbd_layers.txt");
        if (!File.Exists(trig)) return;
        var parts = File.ReadAllText(trig).Trim().Split('|');
        string file = parts[0];
        int? itemId = parts.Length > 1 ? int.Parse(parts[1]) : null;
        File.Delete(trig);
        var log = new System.Text.StringBuilder();
        try
        {
            var box = HeifContainer.Parse(File.ReadAllBytes(file));
            var dec = new Av1Decoder();
            Environment.SetEnvironmentVariable("AV1_DUMP10", Path.Combine(Scratch, "layers_ours.yuv"));
            using var trace = new StreamWriter(Path.Combine(Scratch, "trace_ours.txt"));
            Av1Decode.BlockTrace = trace;
            using var f = dec.Decode(box.ItemData(itemId ?? box.PrimaryId)!, 0, true);
            Av1Decode.BlockTrace = null;
            Environment.SetEnvironmentVariable("AV1_DUMP10", null);
            log.AppendLine(f == null ? "null frame" : $"frame {f.Width}x{f.Height}");
        }
        catch (Exception e) { log.AppendLine(e.ToString()); }
        log.AppendLine("last error: " + Av1Decoder.LastDecodeError);
        try
        {
            var box = HeifContainer.Parse(File.ReadAllBytes(file));
            var dec = new Av1Decoder();
            var frames = dec.DecodeTemporalUnit(box.ItemData(itemId ?? box.PrimaryId)!, 0);
            var fh = dec.CurrentFrameHeader;
            log.AppendLine($"frames={frames.Count} last hdr: type={fh.FrameType} sid={fh.SpatialId} coded={fh.CodedWidth}x{fh.Height} upscaled={fh.SuperResUpscaledWidth} render={fh.RenderWidth}x{fh.RenderHeight} tiles={fh.TileCols}x{fh.TileRows} nbytes={fh.TileNBytes} showFrame={fh.ShowFrame} primRef={fh.PrimaryRefFrame} q={fh.QuantBaseQIdx} lr={fh.GetLrType(0)},{fh.GetLrType(1)},{fh.GetLrType(2)} cdefBits={fh.CdefNBits} uvStr0={fh.GetCdefUvStrength(0)}");
        }
        catch (Exception e) { log.AppendLine("hdr probe: " + e.Message); }
        File.WriteAllText(Path.Combine(Scratch, "layers.txt"), log.ToString());
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
