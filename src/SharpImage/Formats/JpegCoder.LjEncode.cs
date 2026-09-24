using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

// A port of libjpeg-turbo 3.1's compressor as cjpeg drives it, byte-exact: jcparam.c's defaults and tables,
// jccolor.c's fixed-point colour conversion, jcprepct.c / jcsample.c edge expansion and downsampling (with input
// smoothing), jccoefct.c's whole-image coefficient buffer with its dummy blocks, jcdctmgr.c's islow / ifast / float
// forward DCTs and quantizers (the 8-bit reciprocal divide included), jchuff.c's sequential Huffman coder and optimal
// tables, and jcmarker.c's marker order.
public static partial class JpegCoder
{
    /// <summary>Encodes <paramref name="image"/> as libjpeg-turbo's cjpeg would with the given switches.</summary>
    public static byte[] Encode(ImageFrame image, JpegEncodeOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(image, ms, options ?? new JpegEncodeOptions());
        return ms.ToArray();
    }

    /// <summary>Writes <paramref name="image"/> as libjpeg-turbo's cjpeg would with the given switches.</summary>
    public static void Write(ImageFrame image, Stream stream, JpegEncodeOptions options)
    {
        byte[] data = new LjEncoder(image, options).Run();
        stream.Write(data);
    }

    private sealed class LeComp
    {
        public int Id, H, V, Tq, Td, Ta, Index;
        public int WidthInBlocks, HeightInBlocks, DownW, DownH;
        public int BlocksW, BlocksH;       // the coefficient buffer, padded to whole MCUs / iMCU rows
        public short[] Coef = [];          // BlocksW * BlocksH blocks of 64, natural order
        public int[] Plane = [];           // downsampled samples, PlaneW x PlaneH
        public int PlaneW, PlaneH;
        // per scan (per_scan_setup)
        public int McuW, McuH, LastColWidth, LastRowHeight;
    }

    private sealed class LeBits
    {
        public readonly MemoryStream Out;
        private ulong acc;
        private int n;
        public LeBits(MemoryStream o) => Out = o;

        public void Put(uint code, int size)
        {
            acc = (acc << size) | code;
            n += size;
            while (n >= 8)
            {
                n -= 8;
                byte b = (byte)(acc >> n);
                Out.WriteByte(b);
                if (b == 0xFF) Out.WriteByte(0);
            }
            acc &= (1UL << n) - 1;
        }

        /// <summary>jchuff.c flush_bits: pad the last byte with ones.</summary>
        public void Flush()
        {
            if (n > 0) Put((1u << (8 - n)) - 1, 8 - n);
        }
    }

    private sealed class LeHuff
    {
        public readonly byte[] Bits = new byte[17];     // Bits[1..16]
        public byte[] Vals = [];
        public readonly uint[] Code = new uint[256];
        public readonly byte[] Size = new byte[256];
        public bool Sent;

        public LeHuff(ReadOnlySpan<byte> bits16, ReadOnlySpan<byte> vals)
        {
            bits16.CopyTo(Bits.AsSpan(1));
            Vals = vals.ToArray();
        }

        /// <summary>jpeg_make_c_derived_tbl.</summary>
        public void Derive(int maxSymbol)
        {
            Span<byte> hs = stackalloc byte[257];
            Span<uint> hc = stackalloc uint[257];
            int p = 0;
            for (int l = 1; l <= 16; l++)
            {
                int i = Bits[l];
                if (p + i > 256) throw new InvalidOperationException("Bogus Huffman table definition");
                while (i-- > 0) hs[p++] = (byte)l;
            }
            hs[p] = 0;
            int lastp = p;
            uint code = 0;
            int si = hs[0];
            p = 0;
            while (hs[p] != 0)
            {
                while (hs[p] == si) { hc[p++] = code; code++; }
                if (code >= 1u << si) throw new InvalidOperationException("Bogus Huffman table definition");
                code <<= 1;
                si++;
            }
            Array.Clear(Code);
            Array.Clear(Size);
            for (p = 0; p < lastp; p++)
            {
                int i = Vals[p];
                if (i > maxSymbol || Size[i] != 0) throw new InvalidOperationException("Bogus Huffman table definition");
                Code[i] = hc[p];
                Size[i] = hs[p];
            }
        }

        /// <summary>jpeg_gen_optimal_table (libjpeg-turbo 3.1).</summary>
        public static LeHuff Optimal(long[] freqIn)
        {
            const int MaxClen = 32;
            var freq = (long[])freqIn.Clone();
            var bits = new byte[MaxClen + 2];
            var bitPos = new int[MaxClen + 1];
            var codesize = new int[257];
            var nzIndex = new int[257];
            var others = new int[257];
            Array.Fill(others, -1);
            freq[256] = 1;
            int nnz = 0;
            for (int i = 0; i < 257; i++)
                if (freq[i] != 0) { nzIndex[nnz] = i; freq[nnz] = freq[i]; nnz++; }
            for (;;)
            {
                int c1 = -1, c2 = -1;
                long v = 1000000000L, v2 = 1000000000L;
                for (int i = 0; i < nnz; i++)
                {
                    if (freq[i] <= v2)
                    {
                        if (freq[i] <= v) { c2 = c1; v2 = v; v = freq[i]; c1 = i; }
                        else { v2 = freq[i]; c2 = i; }
                    }
                }
                if (c2 < 0) break;
                freq[c1] += freq[c2];
                freq[c2] = 1000000001L;
                codesize[c1]++;
                while (others[c1] >= 0) { c1 = others[c1]; codesize[c1]++; }
                others[c1] = c2;
                codesize[c2]++;
                while (others[c2] >= 0) { c2 = others[c2]; codesize[c2]++; }
            }
            for (int i = 0; i < nnz; i++)
            {
                if (codesize[i] > MaxClen) throw new InvalidOperationException("Huffman code size table overflow");
                bits[codesize[i]]++;
            }
            int p = 0;
            for (int i = 1; i <= MaxClen; i++) { bitPos[i] = p; p += bits[i]; }
            int k;
            for (k = MaxClen; k > 16; k--)
            {
                while (bits[k] > 0)
                {
                    int j = k - 2;
                    while (bits[j] == 0) j--;
                    bits[k] -= 2;
                    bits[k - 1]++;
                    bits[j + 1] += 2;
                    bits[j]--;
                }
            }
            while (bits[k] == 0) k--;
            bits[k]--;
            var vals = new byte[256];
            for (int i = 0; i < nnz - 1; i++)
            {
                vals[bitPos[codesize[i]]] = (byte)nzIndex[i];
                bitPos[codesize[i]]++;
            }
            int count = 0;
            for (int i = 1; i <= 16; i++) count += bits[i];
            return new LeHuff(bits.AsSpan(1, 16), vals.AsSpan(0, count));
        }
    }

    private sealed class LjEncoder
    {
        private readonly JpegEncodeOptions o;
        private readonly ImageFrame img;
        private readonly int W, H, prec, maxSample;
        private readonly int inKind;                // 0 grey, 1 RGB, 2 CMYK
        private readonly JpegColorSpace cs;
        private readonly bool jfif, adobe, lossless, progressive, arith, optimize;
        private readonly LeComp[] comps;
        private readonly ushort[]?[] qt = new ushort[4][];
        private readonly JpegScanInfo[] scans;
        private readonly int maxH, maxV, totalImcuRows, du;
        private readonly LeHuff?[] dcTbl = new LeHuff?[4], acTbl = new LeHuff?[4];
        private readonly MemoryStream outp = new();
        private int restartInterval, lastRestartInterval;

        public LjEncoder(ImageFrame image, JpegEncodeOptions opt)
        {
            o = opt;
            img = image;
            W = (int)image.Columns;
            H = (int)image.Rows;
            if (W <= 0 || H <= 0) throw new ArgumentException("Empty image");
            if (W > 65500 || H > 65500) throw new ArgumentException("Maximum supported image dimension is 65500 pixels");
            int colorChannels = image.NumberOfChannels - (image.HasAlpha ? 1 : 0);
            inKind = image.Colorspace == ColorspaceType.CMYK && colorChannels >= 4 ? 2 : colorChannels >= 3 ? 1 : 0;

            // The scan script decides the mode (validate_script); otherwise -lossless.
            JpegScanInfo[]? script = opt.Scans;
            bool wantLossless = opt.LosslessPredictor != 0;
            if (script == null && opt.Progressive) wantLossless = false;   // jpeg_simple_progression runs first ...
            if (script is { Length: > 0 })
            {
                var s0 = script[0];
                wantLossless = s0.Ss != 0 && s0.Se == 0;
            }
            lossless = wantLossless;
            prec = opt.Precision;
            if (lossless ? prec is < 2 or > 16 : prec is not (8 or 12))
                throw new ArgumentOutOfRangeException(nameof(opt), $"Unsupported JPEG data precision {prec}");
            maxSample = (1 << prec) - 1;
            du = lossless ? 1 : 8;

            // jpeg_default_colorspace / jpeg_set_colorspace
            var want = opt.ColorSpace;
            if (want == JpegColorSpace.Auto || lossless)
                want = inKind switch { 0 => JpegColorSpace.Grayscale, 2 => JpegColorSpace.Cmyk, _ => lossless ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr };
            if (inKind == 2 && want is not (JpegColorSpace.Cmyk or JpegColorSpace.Ycck))
                throw new NotSupportedException("A CMYK image can be coded as CMYK or YCCK only");
            if (inKind != 2 && want is JpegColorSpace.Cmyk or JpegColorSpace.Ycck)
                throw new NotSupportedException("CMYK / YCCK coding needs a CMYK image");
            cs = want;
            comps = cs switch
            {
                JpegColorSpace.Grayscale => [Comp(0, 1, 1, 1, 0, 0, 0)],
                JpegColorSpace.Rgb => [Comp(0, 0x52, 1, 1, 0, 0, 0), Comp(1, 0x47, 1, 1, 0, 0, 0), Comp(2, 0x42, 1, 1, 0, 0, 0)],
                JpegColorSpace.YCbCr => [Comp(0, 1, 2, 2, 0, 0, 0), Comp(1, 2, 1, 1, 1, 1, 1), Comp(2, 3, 1, 1, 1, 1, 1)],
                JpegColorSpace.Cmyk => [Comp(0, 0x43, 1, 1, 0, 0, 0), Comp(1, 0x4D, 1, 1, 0, 0, 0), Comp(2, 0x59, 1, 1, 0, 0, 0), Comp(3, 0x4B, 1, 1, 0, 0, 0)],
                _ => [Comp(0, 1, 2, 2, 0, 0, 0), Comp(1, 2, 1, 1, 1, 1, 1), Comp(2, 3, 1, 1, 1, 1, 1), Comp(3, 4, 2, 2, 0, 0, 0)],
            };
            jfif = cs is JpegColorSpace.Grayscale or JpegColorSpace.YCbCr;
            adobe = !jfif;

            // Quantization tables (jpeg_set_defaults, then cjpeg's -quality / -qtables).
            int[] scale = [100, 100, 100, 100];
            bool anyQuality = opt.TableQualities is { Length: > 0 } || opt.Quality != null;
            if (opt.TableQualities is { Length: > 0 } tq)
                for (int t = 0; t < 4; t++) scale[t] = QualityScaling(tq[Math.Min(t, tq.Length - 1)]);
            else if (opt.Quality is int q)
                for (int t = 0; t < 4; t++) scale[t] = QualityScaling(q);
            qt[0] = AddQuantTable(JpegTables.LuminanceQuantTable, anyQuality ? scale[0] : 50, !anyQuality || opt.ForceBaseline);
            qt[1] = AddQuantTable(JpegTables.ChrominanceQuantTable, anyQuality ? scale[1] : 50, !anyQuality || opt.ForceBaseline);
            if (opt.QuantTables != null)
            {
                if (opt.QuantTables.Length > 4) throw new ArgumentException("Too many quantization tables");
                for (int t = 0; t < opt.QuantTables.Length; t++)
                {
                    var basic = opt.QuantTables[t];
                    if (basic.Length != 64) throw new ArgumentException("A quantization table has 64 entries");
                    var b = new int[64];
                    for (int i = 0; i < 64; i++) b[i] = basic[i];
                    qt[t] = AddQuantTable(b, scale[t], opt.ForceBaseline);
                }
            }
            if (opt.QuantTableSlots is { Length: > 0 } slots)
                for (int ci = 0; ci < comps.Length; ci++)
                {
                    int v = slots[Math.Min(ci, slots.Length - 1)];
                    if (v is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(opt), "Quantization table slots are 0..3");
                    comps[ci].Tq = v;
                }
            if (opt.SamplingFactors is { } sf)
                for (int ci = 0; ci < comps.Length; ci++)
                {
                    var (h, v) = ci < sf.Length ? sf[ci] : (1, 1);
                    if (h is < 1 or > 4 || v is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(opt), "Sampling factors are 1..4");
                    comps[ci].H = h;
                    comps[ci].V = v;
                }
            if (lossless)
            {
                // jinit_c_master_control: no smoothing, full-size components.
                foreach (var c in comps) c.H = c.V = 1;
            }

            scans = script ?? (opt.Progressive ? SimpleProgression() : [new JpegScanInfo(Enumerable.Range(0, comps.Length).ToArray(),
                lossless ? opt.LosslessPredictor : 0, lossless ? 0 : 63, 0, lossless ? opt.LosslessPointTransform : 0)]);
            progressive = ValidateScript(scans, out bool scriptLossless);
            if (scriptLossless != lossless) throw new ArgumentException("Invalid scan script");
            arith = opt.Arithmetic;
            if (arith) optimize = false;
            else optimize = opt.OptimizeCoding || lossless || progressive || prec == 12;
            if (lossless && arith) throw new NotSupportedException("Sorry, arithmetic coding is not supported");

            // initial_setup
            maxH = comps.Max(c => c.H);
            maxV = comps.Max(c => c.V);
            foreach (var c in comps)
            {
                c.WidthInBlocks = DivUp(W * c.H, maxH * du);
                c.HeightInBlocks = DivUp(H * c.V, maxV * du);
                c.DownW = DivUp(W * c.H, maxH);
                c.DownH = DivUp(H * c.V, maxV);
            }
            totalImcuRows = DivUp(H, maxV * du);
            restartInterval = opt.RestartInterval;
            if (opt.RestartInterval is < 0 or > 65535 || opt.RestartRows is < 0 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(opt), "Restart interval out of range");
            if (opt.Smoothing is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(opt), "Smoothing is 0..100");
        }

        private static LeComp Comp(int index, int id, int h, int v, int tq, int td, int ta) =>
            new() { Index = index, Id = id, H = h, V = v, Tq = tq, Td = td, Ta = ta };

        private static int DivUp(int a, int b) => (a + b - 1) / b;

        /// <summary>jpeg_quality_scaling.</summary>
        private static int QualityScaling(int quality)
        {
            quality = Math.Clamp(quality, 1, 100);
            return quality < 50 ? 5000 / quality : 200 - quality * 2;
        }

        /// <summary>jpeg_add_quant_table.</summary>
        private static ushort[] AddQuantTable(ReadOnlySpan<byte> basic, int scale, bool baseline)
        {
            var b = new int[64];
            for (int i = 0; i < 64; i++) b[i] = basic[i];
            return AddQuantTable(b, scale, baseline);
        }

        private static ushort[] AddQuantTable(int[] basic, int scale, bool baseline)
        {
            var t = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                long temp = ((long)basic[i] * scale + 50L) / 100L;
                if (temp <= 0) temp = 1;
                if (temp > 32767) temp = 32767;
                if (baseline && temp > 255) temp = 255;
                t[i] = (ushort)temp;
            }
            return t;
        }

        /// <summary>jpeg_simple_progression.</summary>
        private JpegScanInfo[] SimpleProgression()
        {
            int n = comps.Length;
            var s = new List<JpegScanInfo>();
            void Dc(int ah, int al)
            {
                if (n <= 4) s.Add(new JpegScanInfo(Enumerable.Range(0, n).ToArray(), 0, 0, ah, al));
                else for (int ci = 0; ci < n; ci++) s.Add(new JpegScanInfo([ci], 0, 0, ah, al));
            }
            void One(int ci, int ss, int se, int ah, int al) => s.Add(new JpegScanInfo([ci], ss, se, ah, al));
            void All(int ss, int se, int ah, int al) { for (int ci = 0; ci < n; ci++) One(ci, ss, se, ah, al); }
            if (n == 3 && cs == JpegColorSpace.YCbCr)
            {
                Dc(0, 1);
                One(0, 1, 5, 0, 2);
                One(2, 1, 63, 0, 1);
                One(1, 1, 63, 0, 1);
                One(0, 6, 63, 0, 2);
                One(0, 1, 63, 2, 1);
                Dc(1, 0);
                One(2, 1, 63, 1, 0);
                One(1, 1, 63, 1, 0);
                One(0, 1, 63, 1, 0);
            }
            else
            {
                Dc(0, 1);
                All(1, 5, 0, 2);
                All(6, 63, 0, 2);
                All(1, 63, 2, 1);
                Dc(1, 0);
                All(1, 63, 1, 0);
            }
            return s.ToArray();
        }

        /// <summary>jcmaster.c validate_script: returns progressive; lossless through the out parameter.</summary>
        private bool ValidateScript(JpegScanInfo[] script, out bool isLossless)
        {
            if (script.Length == 0) throw new ArgumentException("Invalid scan script");
            var first = script[0];
            isLossless = first.Ss != 0 && first.Se == 0;
            bool prog = !isLossless && (first.Ss != 0 || first.Se != 63);
            int n = comps.Length;
            var lastBit = new int[n, 64];
            for (int a = 0; a < n; a++) for (int k = 0; k < 64; k++) lastBit[a, k] = -1;
            var sent = new bool[n];
            foreach (var sc in script)
            {
                int nc = sc.Components.Length;
                if (nc is <= 0 or > 4) throw new ArgumentException("Invalid scan script");
                for (int ci = 0; ci < nc; ci++)
                {
                    int t = sc.Components[ci];
                    if (t < 0 || t >= n || (ci > 0 && t <= sc.Components[ci - 1])) throw new ArgumentException("Invalid scan script");
                }
                int ss = sc.Ss, se = sc.Se, ah = sc.Ah, al = sc.Al;
                if (prog)
                {
                    int maxAhAl = prec == 12 ? 13 : 10;
                    if (ss < 0 || ss >= 64 || se < ss || se >= 64 || ah < 0 || ah > maxAhAl || al < 0 || al > maxAhAl)
                        throw new ArgumentException("Invalid progressive parameters in scan script");
                    if (ss == 0 ? se != 0 : nc != 1) throw new ArgumentException("Invalid progressive parameters in scan script");
                    foreach (int t in sc.Components)
                    {
                        if (ss != 0 && lastBit[t, 0] < 0) throw new ArgumentException("Invalid progressive parameters in scan script");
                        for (int k = ss; k <= se; k++)
                        {
                            if (lastBit[t, k] < 0) { if (ah != 0) throw new ArgumentException("Invalid progressive parameters in scan script"); }
                            else if (ah != lastBit[t, k] || al != ah - 1) throw new ArgumentException("Invalid progressive parameters in scan script");
                            lastBit[t, k] = al;
                        }
                    }
                }
                else
                {
                    if (isLossless)
                    {
                        if (ss is < 1 or > 7 || se != 0 || ah != 0 || al < 0 || al >= prec)
                            throw new ArgumentException("Invalid lossless parameters in scan script");
                    }
                    else if (ss != 0 || se != 63 || ah != 0 || al != 0) throw new ArgumentException("Invalid scan script");
                    foreach (int t in sc.Components)
                    {
                        if (sent[t]) throw new ArgumentException("Invalid scan script");
                        sent[t] = true;
                    }
                }
            }
            for (int ci = 0; ci < n; ci++)
                if (prog ? lastBit[ci, 0] < 0 : !sent[ci]) throw new ArgumentException("Scan script does not transmit all data");
            return prog;
        }

        public byte[] Run()
        {
            var planes = ColorConvert();
            if (!lossless)
            {
                bool context = o.Smoothing > 0 && comps.Any(c => (c.H == maxH && c.V == maxV) || (c.H * 2 == maxH && c.V * 2 == maxV));
                for (int ci = 0; ci < comps.Length; ci++) Downsample(comps[ci], planes[ci], context);
                foreach (var c in comps) ForwardDct(c);
            }

            // jcmarker.c write_file_header (+ cjpeg's ICC profile)
            Marker(0xD8);
            if (jfif)
            {
                Marker(0xE0); Word(16);
                outp.Write("JFIF\0"u8);
                Byte(1); Byte(1);
                Byte(o.DensityUnit); Word(o.XDensity); Word(o.YDensity);
                Byte(0); Byte(0);
            }
            if (adobe)
            {
                Marker(0xEE); Word(14);
                outp.Write("Adobe"u8);
                Word(100); Word(0); Word(0);
                Byte(cs == JpegColorSpace.YCbCr ? 1 : cs == JpegColorSpace.Ycck ? 2 : 0);
            }
            if (o.IccProfile is { Length: > 0 } icc)
            {
                const int maxData = 65533 - 14;
                int count = DivUp(icc.Length, maxData);
                for (int m = 0, off = 0; off < icc.Length; m++)
                {
                    int len = Math.Min(maxData, icc.Length - off);
                    Marker(0xE2); Word(len + 14 + 2);
                    outp.Write("ICC_PROFILE\0"u8);
                    Byte(m + 1); Byte(count);
                    outp.Write(icc, off, len);
                    off += len;
                }
            }

            if (!arith && !optimize)
            {
                // std_huff_tables
                dcTbl[0] = new LeHuff(JpegTables.DcLuminanceBits, JpegTables.DcLuminanceValues);
                acTbl[0] = new LeHuff(JpegTables.AcLuminanceBits, JpegTables.AcLuminanceValues);
                dcTbl[1] = new LeHuff(JpegTables.DcChrominanceBits, JpegTables.DcChrominanceValues);
                acTbl[1] = new LeHuff(JpegTables.AcChrominanceBits, JpegTables.AcChrominanceValues);
            }
            for (int s = 0; s < scans.Length; s++)
            {
                var si = scans[s];
                var sc = SetupScan(si);
                if (lossless)
                {
                    // jcdiffct.c: the statistics and output passes each difference the samples afresh
                    GatherLossless(sc, LosslessDiffs(sc, si, planes));
                    if (s == 0) WriteFrameHeader();
                    WriteScanHeader(sc, si);
                    EncodeLossless(sc, LosslessDiffs(sc, si, planes));
                    continue;
                }
                if (arith)
                {
                    if (s == 0) WriteFrameHeader();
                    WriteScanHeader(sc, si);
                    Arithmetic(sc, si);
                    continue;
                }
                if (progressive)
                {
                    // jcmaster.c: no statistics pass for DC refinement scans (they use no Huffman table)
                    if (optimize && !(si.Ss == 0 && si.Ah != 0)) Progressive(sc, si, gather: true);
                    if (s == 0) WriteFrameHeader();
                    WriteScanHeader(sc, si);
                    Progressive(sc, si, gather: false);
                    continue;
                }
                if (optimize) GatherSequential(sc);
                if (s == 0) WriteFrameHeader();
                WriteScanHeader(sc, si);
                EncodeSequential(sc);
            }
            Marker(0xD9);
            return outp.ToArray();
        }

        // ---- colour conversion (jccolor.c) ----

        private int[][] ColorConvert()
        {
            int n = comps.Length, ch = img.NumberOfChannels;
            var planes = new int[n][];
            for (int ci = 0; ci < n; ci++) planes[ci] = new int[W * H];
            // 16-bit frame samples to the coded precision, as cjpeg's PPM reader rescales a maxval-65535 file.
            long mx = maxSample;
            int S(ushort v) => (int)((v * mx + 32767) / 65535);

            const int scaleBits = 16, oneHalf = 1 << (scaleBits - 1);
            static int Fix(double x) => (int)(x * (1 << scaleBits) + 0.5);
            int center = 1 << (prec - 1);
            int cbcrOffset = center << scaleBits;
            int ry = Fix(0.29900), gy = Fix(0.58700), by = Fix(0.11400), rcb = Fix(0.16874), gcb = Fix(0.33126),
                bcb = Fix(0.5), gcr = Fix(0.41869), bcr = Fix(0.08131);

            for (int y = 0; y < H; y++)
            {
                var row = img.GetPixelRow(y);
                int o0 = y * W;
                for (int x = 0; x < W; x++)
                {
                    int p = x * ch;
                    if (inKind == 0)
                    {
                        int g = S(row[p]);
                        if (cs == JpegColorSpace.Grayscale) planes[0][o0 + x] = g;
                        else RgbTo(g, g, g, o0 + x);
                    }
                    else if (inKind == 1)
                    {
                        int r = S(row[p]), g = S(row[p + 1]), b = S(row[p + 2]);
                        if (cs == JpegColorSpace.Grayscale)
                            planes[0][o0 + x] = (ry * r + gy * g + by * b + oneHalf) >> scaleBits;
                        else RgbTo(r, g, b, o0 + x);
                    }
                    else
                    {
                        // CMYK frames hold ink amounts; Adobe CMYK JPEGs store them inverted.
                        int c = maxSample - S(row[p]), m = maxSample - S(row[p + 1]), yy = maxSample - S(row[p + 2]), k = maxSample - S(row[p + 3]);
                        if (cs == JpegColorSpace.Cmyk)
                        {
                            planes[0][o0 + x] = c; planes[1][o0 + x] = m; planes[2][o0 + x] = yy; planes[3][o0 + x] = k;
                        }
                        else
                        {
                            // cmyk_ycck_convert: YCC of (MAX - C, MAX - M, MAX - Y), K as is.
                            YccTo(maxSample - c, maxSample - m, maxSample - yy, o0 + x);
                            planes[3][o0 + x] = k;
                        }
                    }
                }
            }
            return planes;

            void RgbTo(int r, int g, int b, int at)
            {
                if (cs == JpegColorSpace.Rgb) { planes[0][at] = r; planes[1][at] = g; planes[2][at] = b; }
                else YccTo(r, g, b, at);
            }

            void YccTo(int r, int g, int b, int at)
            {
                planes[0][at] = (ry * r + gy * g + by * b + oneHalf) >> scaleBits;
                planes[1][at] = (-rcb * r - gcb * g + bcb * b + cbcrOffset + oneHalf - 1) >> scaleBits;
                planes[2][at] = (bcb * r - gcr * g - bcr * b + cbcrOffset + oneHalf - 1) >> scaleBits;
            }
        }

        // ---- edge expansion and downsampling (jcprepct.c, jcsample.c) ----

        private void Downsample(LeComp c, int[] src, bool context)
        {
            int hexp = maxH / c.H, vexp = maxV / c.V;
            if (maxH % c.H != 0 || maxV % c.V != 0) throw new NotSupportedException("Fractional sampling not implemented yet");
            c.PlaneW = c.WidthInBlocks * du;
            c.PlaneH = totalImcuRows * c.V * du;
            var dst = c.Plane = new int[c.PlaneW * c.PlaneH];
            int outCols = c.PlaneW;
            int inCols = outCols * hexp;
            bool full = hexp == 1 && vexp == 1, h2v1 = hexp == 2 && vexp == 1, h2v2 = hexp == 2 && vexp == 2;
            bool smooth = o.Smoothing > 0 && !lossless && (full || h2v2);
            int sf = o.Smoothing;

            // Row groups (maxV input rows -> V output rows). Without context rows the image's last group is padded by
            // replicating its last input row and later groups repeat the last output row; with context rows (input
            // smoothing) the input is replicated above and below and every group is downsampled.
            int groups = totalImcuRows * du;
            int realGroups = DivUp(H, maxV);
            var rowBuf = new int[maxV + 2][];
            for (int k = 0; k < rowBuf.Length; k++) rowBuf[k] = new int[inCols + 2];
            for (int g = 0; g < groups; g++)
            {
                if (!context && g >= realGroups)
                {
                    int last = realGroups * c.V - 1;
                    for (int r = 0; r < c.V; r++)
                        Array.Copy(dst, last * outCols, dst, (g * c.V + r) * outCols, outCols);
                    continue;
                }
                // input rows -1 .. maxV (context), extended right to inCols by replicating the last column
                for (int k = -1; k <= maxV; k++)
                {
                    int yy = Math.Clamp(g * maxV + k, 0, H - 1);
                    if (!context && k >= 0 && k < maxV) yy = Math.Min(g * maxV + k, H - 1);
                    var rb = rowBuf[k + 1];
                    int so = yy * W;
                    int n = Math.Min(W, inCols);
                    Array.Copy(src, so, rb, 0, n);
                    for (int x = n; x < inCols; x++) rb[x] = src[so + W - 1];
                }
                for (int r = 0; r < c.V; r++)
                {
                    int outRow = g * c.V + r;
                    var d = dst.AsSpan(outRow * outCols, outCols);
                    if (full)
                    {
                        if (smooth) FullsizeSmooth(rowBuf[r], rowBuf[r + 1], rowBuf[r + 2], d, sf);
                        else rowBuf[r + 1].AsSpan(0, outCols).CopyTo(d);
                    }
                    else if (h2v1)
                    {
                        var ip = rowBuf[r + 1];
                        int bias = 0;
                        for (int x = 0; x < outCols; x++) { d[x] = (ip[2 * x] + ip[2 * x + 1] + bias) >> 1; bias ^= 1; }
                    }
                    else if (h2v2)
                    {
                        if (smooth) H2V2Smooth(rowBuf[2 * r], rowBuf[2 * r + 1], rowBuf[2 * r + 2], rowBuf[2 * r + 3], d, sf);
                        else
                        {
                            var i0 = rowBuf[2 * r + 1]; var i1 = rowBuf[2 * r + 2];
                            int bias = 1;
                            for (int x = 0; x < outCols; x++) { d[x] = (i0[2 * x] + i0[2 * x + 1] + i1[2 * x] + i1[2 * x + 1] + bias) >> 2; bias ^= 3; }
                        }
                    }
                    else
                    {
                        int numpix = hexp * vexp, numpix2 = numpix / 2;
                        for (int x = 0; x < outCols; x++)
                        {
                            long sum = 0;
                            for (int v = 0; v < vexp; v++)
                            {
                                var ip = rowBuf[r * vexp + v + 1];
                                for (int h = 0; h < hexp; h++) sum += ip[x * hexp + h];
                            }
                            d[x] = (int)((sum + numpix2) / numpix);
                        }
                    }
                }
            }
        }

        private static void FullsizeSmooth(int[] above, int[] ip, int[] below, Span<int> d, int sf)
        {
            int outputCols = d.Length;
            long memberscale = 65536L - sf * 512L, neighscale = sf * 64;
            int x = 0;
            int colsum = above[0] + below[0] + ip[0];
            long membersum = ip[0];
            int nextcolsum = above[1] + below[1] + ip[1];
            long neighsum = colsum + (colsum - membersum) + nextcolsum;
            membersum = membersum * memberscale + neighsum * neighscale;
            d[x++] = (int)((membersum + 32768) >> 16);
            int lastcolsum = colsum; colsum = nextcolsum;
            for (int k = outputCols - 2; k > 0; k--, x++)
            {
                membersum = ip[x];
                nextcolsum = above[x + 1] + below[x + 1] + ip[x + 1];
                neighsum = lastcolsum + (colsum - membersum) + nextcolsum;
                membersum = membersum * memberscale + neighsum * neighscale;
                d[x] = (int)((membersum + 32768) >> 16);
                lastcolsum = colsum; colsum = nextcolsum;
            }
            membersum = ip[x];
            neighsum = lastcolsum + (colsum - membersum) + colsum;
            membersum = membersum * memberscale + neighsum * neighscale;
            d[x] = (int)((membersum + 32768) >> 16);
        }

        private static void H2V2Smooth(int[] above, int[] i0, int[] i1, int[] below, Span<int> d, int sf)
        {
            int outputCols = d.Length;
            long memberscale = 16384 - sf * 80, neighscale = sf * 16;
            int p = 0, x = 0;
            long membersum = i0[p] + i0[p + 1] + i1[p] + i1[p + 1];
            long neighsum = above[p] + above[p + 1] + below[p] + below[p + 1] + i0[p] + i0[p + 2] + i1[p] + i1[p + 2];
            neighsum += neighsum;
            neighsum += above[p] + above[p + 2] + below[p] + below[p + 2];
            membersum = membersum * memberscale + neighsum * neighscale;
            d[x++] = (int)((membersum + 32768) >> 16);
            p += 2;
            for (int k = outputCols - 2; k > 0; k--, x++, p += 2)
            {
                membersum = i0[p] + i0[p + 1] + i1[p] + i1[p + 1];
                neighsum = above[p] + above[p + 1] + below[p] + below[p + 1] + i0[p - 1] + i0[p + 2] + i1[p - 1] + i1[p + 2];
                neighsum += neighsum;
                neighsum += above[p - 1] + above[p + 2] + below[p - 1] + below[p + 2];
                membersum = membersum * memberscale + neighsum * neighscale;
                d[x] = (int)((membersum + 32768) >> 16);
            }
            membersum = i0[p] + i0[p + 1] + i1[p] + i1[p + 1];
            neighsum = above[p] + above[p + 1] + below[p] + below[p + 1] + i0[p - 1] + i0[p + 1] + i1[p - 1] + i1[p + 1];
            neighsum += neighsum;
            neighsum += above[p - 1] + above[p + 1] + below[p - 1] + below[p + 1];
            membersum = membersum * memberscale + neighsum * neighscale;
            d[x] = (int)((membersum + 32768) >> 16);
        }

        // ---- forward DCT and quantization (jccoefct.c compress_first_pass, jcdctmgr.c) ----

        private void ForwardDct(LeComp c)
        {
            c.BlocksW = DivUp(c.WidthInBlocks, c.H) * c.H;
            c.BlocksH = totalImcuRows * c.V;
            var coef = c.Coef = new short[c.BlocksW * c.BlocksH * 64];
            var q = qt[c.Tq] ?? throw new InvalidOperationException($"Quantization table 0x{c.Tq:x2} was not defined");
            var method = o.Dct;

            // divisors
            uint[] recip = new uint[64], corr = new uint[64];
            int[] shift = new int[64];
            long[] qdiv = new long[64];
            float[] fdiv = new float[64];
            ReadOnlySpan<short> aan =
            [
                16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520, 22725, 31521, 29692, 26722, 22725, 17855, 12299, 6270,
                21407, 29692, 27969, 25172, 21407, 16819, 11585, 5906, 19266, 26722, 25172, 22654, 19266, 15137, 10426, 5315,
                16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520, 12873, 17855, 16819, 15137, 12873, 10114, 6967, 3552,
                8867, 12299, 11585, 10426, 8867, 6967, 4799, 2446, 4520, 6270, 5906, 5315, 4520, 3552, 2446, 1247,
            ];
            ReadOnlySpan<double> aanf = [1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379];
            for (int i = 0; i < 64; i++)
            {
                if (method == JpegDctMethod.Float)
                {
                    fdiv[i] = (float)(1.0 / ((double)q[i] * aanf[i >> 3] * aanf[i & 7] * 8.0));
                    continue;
                }
                long d = method == JpegDctMethod.IntegerSlow ? (long)q[i] << 3 : ((long)q[i] * aan[i] + (1 << 10)) >> 11;
                if (prec == 8) ComputeReciprocal((ushort)d, out recip[i], out corr[i], out shift[i]);
                else qdiv[i] = d;
            }

            Span<int> ws = stackalloc int[64];
            Span<float> fws = stackalloc float[64];
            int center = 1 << (prec - 1);
            int pw = c.PlaneW;
            int wib = c.WidthInBlocks;
            int ndummy = wib % c.H;
            if (ndummy > 0) ndummy = c.H - ndummy;
            for (int imcu = 0; imcu < totalImcuRows; imcu++)
            {
                int blockRows = c.V;
                if (imcu == totalImcuRows - 1)
                {
                    blockRows = c.HeightInBlocks % c.V;
                    if (blockRows == 0) blockRows = c.V;
                }
                for (int br = 0; br < blockRows; br++)
                {
                    int by = imcu * c.V + br;
                    int rowBase = by * c.BlocksW;
                    for (int bx = 0; bx < wib; bx++)
                    {
                        var blk = coef.AsSpan((rowBase + bx) * 64, 64);
                        int sy = by * 8, sx = bx * 8;
                        if (method == JpegDctMethod.Float)
                        {
                            for (int r = 0; r < 8; r++)
                                for (int k = 0; k < 8; k++) fws[r * 8 + k] = c.Plane[(sy + r) * pw + sx + k] - center;
                            FdctFloat(fws);
                            for (int i = 0; i < 64; i++)
                            {
                                float t = fws[i] * fdiv[i];
                                blk[i] = (short)((int)(t + 16384.5f) - 16384);
                            }
                            continue;
                        }
                        for (int r = 0; r < 8; r++)
                            for (int k = 0; k < 8; k++) ws[r * 8 + k] = c.Plane[(sy + r) * pw + sx + k] - center;
                        if (method == JpegDctMethod.IntegerSlow) FdctIslow(ws, prec == 8 ? 2 : 1);
                        else FdctIfast(ws);
                        if (prec == 8)
                        {
                            for (int i = 0; i < 64; i++)
                            {
                                int temp = ws[i];
                                bool neg = temp < 0;
                                if (neg) temp = -temp;
                                ulong product = (ulong)(uint)(temp + (int)corr[i]) * recip[i];
                                product >>= shift[i] + 32;
                                temp = (int)product;
                                blk[i] = (short)(neg ? -temp : temp);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < 64; i++)
                            {
                                long qval = qdiv[i], temp = ws[i];
                                bool neg = temp < 0;
                                if (neg) temp = -temp;
                                temp += qval >> 1;
                                temp = temp >= qval ? temp / qval : 0;
                                blk[i] = (short)(neg ? -temp : temp);
                            }
                        }
                    }
                    // dummy blocks to fill the last MCU: zero AC, the last real block's DC
                    for (int bi = 0; bi < ndummy; bi++)
                        coef[(rowBase + wib + bi) * 64] = coef[(rowBase + wib - 1) * 64];
                }
                if (imcu == totalImcuRows - 1)
                {
                    // dummy block rows below the image: each MCU's blocks take the DC of the block above-right
                    int mcus = c.BlocksW / c.H;
                    for (int br = blockRows; br < c.V; br++)
                    {
                        int by = imcu * c.V + br;
                        for (int m = 0; m < mcus; m++)
                        {
                            short lastDc = coef[((by - 1) * c.BlocksW + m * c.H + c.H - 1) * 64];
                            for (int bi = 0; bi < c.H; bi++) coef[(by * c.BlocksW + m * c.H + bi) * 64] = lastDc;
                        }
                    }
                }
            }
        }

        /// <summary>jcdctmgr.c compute_reciprocal (8-bit samples, 32-bit DCTELEM).</summary>
        private static void ComputeReciprocal(ushort divisor, out uint recip, out uint corr, out int shift)
        {
            if (divisor <= 1) { recip = 1; corr = 0; shift = -32; return; }
            int b = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)divisor) - 1;   // flss(divisor) - 1
            int r = 32 + b;
            ulong fq = (1UL << r) / divisor, fr = (1UL << r) % divisor;
            uint c = (uint)(divisor / 2);
            if (fr == 0) { fq >>= 1; r--; }
            else if (fr <= (ulong)(divisor / 2)) c++;
            else fq++;
            recip = (uint)fq;
            corr = c;
            shift = r - 32;
        }

        private static void FdctIslow(Span<int> d, int pass1Bits)
        {
            const int constBits = 13;
            const int f0298 = 2446, f0390 = 3196, f0541 = 4433, f0765 = 6270, f0899 = 7373, f1175 = 9633, f1501 = 12299,
                f1847 = 15137, f1961 = 16069, f2053 = 16819, f2562 = 20995, f3072 = 25172;
            unchecked
            {
                for (int p = 0; p < 64; p += 8)
                {
                    int tmp0 = d[p] + d[p + 7], tmp7 = d[p] - d[p + 7];
                    int tmp1 = d[p + 1] + d[p + 6], tmp6 = d[p + 1] - d[p + 6];
                    int tmp2 = d[p + 2] + d[p + 5], tmp5 = d[p + 2] - d[p + 5];
                    int tmp3 = d[p + 3] + d[p + 4], tmp4 = d[p + 3] - d[p + 4];
                    int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                    d[p] = (tmp10 + tmp11) << pass1Bits;
                    d[p + 4] = (tmp10 - tmp11) << pass1Bits;
                    int z1 = (tmp12 + tmp13) * f0541;
                    int s = constBits - pass1Bits;
                    d[p + 2] = Descale(z1 + tmp13 * f0765, s);
                    d[p + 6] = Descale(z1 + tmp12 * -f1847, s);
                    z1 = tmp4 + tmp7;
                    int z2 = tmp5 + tmp6, z3 = tmp4 + tmp6, z4 = tmp5 + tmp7;
                    int z5 = (z3 + z4) * f1175;
                    tmp4 *= f0298; tmp5 *= f2053; tmp6 *= f3072; tmp7 *= f1501;
                    z1 *= -f0899; z2 *= -f2562; z3 *= -f1961; z4 *= -f0390;
                    z3 += z5; z4 += z5;
                    d[p + 7] = Descale(tmp4 + z1 + z3, s);
                    d[p + 5] = Descale(tmp5 + z2 + z4, s);
                    d[p + 3] = Descale(tmp6 + z2 + z3, s);
                    d[p + 1] = Descale(tmp7 + z1 + z4, s);
                }
                for (int p = 0; p < 8; p++)
                {
                    int tmp0 = d[p] + d[p + 56], tmp7 = d[p] - d[p + 56];
                    int tmp1 = d[p + 8] + d[p + 48], tmp6 = d[p + 8] - d[p + 48];
                    int tmp2 = d[p + 16] + d[p + 40], tmp5 = d[p + 16] - d[p + 40];
                    int tmp3 = d[p + 24] + d[p + 32], tmp4 = d[p + 24] - d[p + 32];
                    int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                    d[p] = Descale(tmp10 + tmp11, pass1Bits);
                    d[p + 32] = Descale(tmp10 - tmp11, pass1Bits);
                    int z1 = (tmp12 + tmp13) * f0541;
                    int s = constBits + pass1Bits;
                    d[p + 16] = Descale(z1 + tmp13 * f0765, s);
                    d[p + 48] = Descale(z1 + tmp12 * -f1847, s);
                    z1 = tmp4 + tmp7;
                    int z2 = tmp5 + tmp6, z3 = tmp4 + tmp6, z4 = tmp5 + tmp7;
                    int z5 = (z3 + z4) * f1175;
                    tmp4 *= f0298; tmp5 *= f2053; tmp6 *= f3072; tmp7 *= f1501;
                    z1 *= -f0899; z2 *= -f2562; z3 *= -f1961; z4 *= -f0390;
                    z3 += z5; z4 += z5;
                    d[p + 56] = Descale(tmp4 + z1 + z3, s);
                    d[p + 40] = Descale(tmp5 + z2 + z4, s);
                    d[p + 24] = Descale(tmp6 + z2 + z3, s);
                    d[p + 8] = Descale(tmp7 + z1 + z4, s);
                }
            }

            static int Descale(int x, int n) => unchecked((x + (1 << (n - 1))) >> n);
        }

        private static void FdctIfast(Span<int> d)
        {
            const int f0382 = 98, f0541 = 139, f0707 = 181, f1306 = 334;
            static int M(int v, int c) => unchecked((v * c) >> 8);
            unchecked
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int step = pass == 0 ? 1 : 8, next = pass == 0 ? 8 : 1;
                    for (int q = 0; q < 8; q++)
                    {
                        int p = q * next;
                        int d0 = d[p], d1 = d[p + step], d2 = d[p + 2 * step], d3 = d[p + 3 * step],
                            d4 = d[p + 4 * step], d5 = d[p + 5 * step], d6 = d[p + 6 * step], d7 = d[p + 7 * step];
                        int tmp0 = d0 + d7, tmp7 = d0 - d7, tmp1 = d1 + d6, tmp6 = d1 - d6;
                        int tmp2 = d2 + d5, tmp5 = d2 - d5, tmp3 = d3 + d4, tmp4 = d3 - d4;
                        int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                        d[p] = tmp10 + tmp11;
                        d[p + 4 * step] = tmp10 - tmp11;
                        int z1 = M(tmp12 + tmp13, f0707);
                        d[p + 2 * step] = tmp13 + z1;
                        d[p + 6 * step] = tmp13 - z1;
                        tmp10 = tmp4 + tmp5; tmp11 = tmp5 + tmp6; tmp12 = tmp6 + tmp7;
                        int z5 = M(tmp10 - tmp12, f0382);
                        int z2 = M(tmp10, f0541) + z5;
                        int z4 = M(tmp12, f1306) + z5;
                        int z3 = M(tmp11, f0707);
                        int z11 = tmp7 + z3, z13 = tmp7 - z3;
                        d[p + 5 * step] = z13 + z2;
                        d[p + 3 * step] = z13 - z2;
                        d[p + step] = z11 + z4;
                        d[p + 7 * step] = z11 - z4;
                    }
                }
            }
        }

        private static void FdctFloat(Span<float> d)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int step = pass == 0 ? 1 : 8, next = pass == 0 ? 8 : 1;
                for (int q = 0; q < 8; q++)
                {
                    int p = q * next;
                    float d0 = d[p], d1 = d[p + step], d2 = d[p + 2 * step], d3 = d[p + 3 * step],
                        d4 = d[p + 4 * step], d5 = d[p + 5 * step], d6 = d[p + 6 * step], d7 = d[p + 7 * step];
                    float tmp0 = d0 + d7, tmp7 = d0 - d7, tmp1 = d1 + d6, tmp6 = d1 - d6;
                    float tmp2 = d2 + d5, tmp5 = d2 - d5, tmp3 = d3 + d4, tmp4 = d3 - d4;
                    float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                    d[p] = tmp10 + tmp11;
                    d[p + 4 * step] = tmp10 - tmp11;
                    float z1 = (tmp12 + tmp13) * 0.707106781f;
                    d[p + 2 * step] = tmp13 + z1;
                    d[p + 6 * step] = tmp13 - z1;
                    tmp10 = tmp4 + tmp5; tmp11 = tmp5 + tmp6; tmp12 = tmp6 + tmp7;
                    float z5 = (tmp10 - tmp12) * 0.382683433f;
                    float z2 = 0.541196100f * tmp10 + z5;
                    float z4 = 1.306562965f * tmp12 + z5;
                    float z3 = tmp11 * 0.707106781f;
                    float z11 = tmp7 + z3, z13 = tmp7 - z3;
                    d[p + 5 * step] = z13 + z2;
                    d[p + 3 * step] = z13 - z2;
                    d[p + step] = z11 + z4;
                    d[p + 7 * step] = z11 - z4;
                }
            }
        }

        // ---- scans (jcmaster.c per_scan_setup) ----

        private sealed class ScanCtx
        {
            public LeComp[] Comps = [];
            public int McusPerRow, McuRows;
            public int[] Membership = [];     // scan component per block of an MCU
        }

        private ScanCtx SetupScan(JpegScanInfo s)
        {
            var sc = new ScanCtx { Comps = s.Components.Select(i => comps[i]).ToArray() };
            if (sc.Comps.Length == 1)
            {
                var c = sc.Comps[0];
                sc.McusPerRow = c.WidthInBlocks;
                sc.McuRows = c.HeightInBlocks;
                c.McuW = c.McuH = 1;
                c.LastColWidth = 1;
                sc.Membership = [0];
            }
            else
            {
                sc.McusPerRow = DivUp(W, maxH * du);
                sc.McuRows = DivUp(H, maxV * du);
                var mem = new List<int>();
                for (int ci = 0; ci < sc.Comps.Length; ci++)
                {
                    var c = sc.Comps[ci];
                    c.McuW = c.H;
                    c.McuH = c.V;
                    for (int k = 0; k < c.H * c.V; k++) mem.Add(ci);
                }
                if (mem.Count > 10) throw new ArgumentException("Sampling factors too large for interleaved scan");
                sc.Membership = mem.ToArray();
            }
            if (o.RestartRows > 0) restartInterval = (int)Math.Min((long)o.RestartRows * sc.McusPerRow, 65535L);
            return sc;
        }

        /// <summary>The MCUs of a scan in coding order, each as its blocks' (scan component, block index).</summary>
        private IEnumerable<(int Ci, int Block)[]> Mcus(ScanCtx sc)
        {
            var list = new (int, int)[sc.Membership.Length];
            for (int my = 0; my < sc.McuRows; my++)
                for (int mx = 0; mx < sc.McusPerRow; mx++)
                {
                    int k = 0;
                    for (int ci = 0; ci < sc.Comps.Length; ci++)
                    {
                        var c = sc.Comps[ci];
                        for (int yi = 0; yi < c.McuH; yi++)
                            for (int xi = 0; xi < c.McuW; xi++)
                                list[k++] = (ci, (my * c.McuH + yi) * c.BlocksW + mx * c.McuW + xi);
                    }
                    yield return list;
                }
        }

        private void GatherSequential(ScanCtx sc)
        {
            var dcCount = new long[4][];
            var acCount = new long[4][];
            foreach (var c in sc.Comps)
            {
                dcCount[c.Td] = new long[257];
                acCount[c.Ta] = new long[257];
            }
            var lastDc = new int[sc.Comps.Length];
            int restartsToGo = restartInterval;
            int maxCoefBits = prec + 2;
            foreach (var mcu in Mcus(sc))
            {
                if (restartInterval != 0)
                {
                    if (restartsToGo == 0)
                    {
                        Array.Clear(lastDc);
                        restartsToGo = restartInterval;
                    }
                    restartsToGo--;
                }
                foreach (var (ci, b) in mcu)
                {
                    var c = sc.Comps[ci];
                    var blk = c.Coef.AsSpan(b * 64, 64);
                    int temp = blk[0] - lastDc[ci];
                    if (temp < 0) temp = -temp;
                    int nbits = 0;
                    while (temp != 0) { nbits++; temp >>= 1; }
                    if (nbits > maxCoefBits + 1) throw new InvalidOperationException("DCT coefficient out of range");
                    dcCount[c.Td][nbits]++;
                    int r = 0;
                    for (int k = 1; k < 64; k++)
                    {
                        if ((temp = blk[JpegTables.NaturalOrder[k]]) == 0) { r++; continue; }
                        while (r > 15) { acCount[c.Ta][0xF0]++; r -= 16; }
                        if (temp < 0) temp = -temp;
                        nbits = 1;
                        while ((temp >>= 1) != 0) nbits++;
                        if (nbits > maxCoefBits) throw new InvalidOperationException("DCT coefficient out of range");
                        acCount[c.Ta][(r << 4) + nbits]++;
                        r = 0;
                    }
                    if (r > 0) acCount[c.Ta][0]++;
                    lastDc[ci] = blk[0];
                }
            }
            // finish_pass_gather
            var didDc = new bool[4];
            var didAc = new bool[4];
            foreach (var c in sc.Comps)
            {
                if (!didDc[c.Td]) { dcTbl[c.Td] = LeHuff.Optimal(dcCount[c.Td]); didDc[c.Td] = true; }
                if (!didAc[c.Ta]) { acTbl[c.Ta] = LeHuff.Optimal(acCount[c.Ta]); didAc[c.Ta] = true; }
            }
        }

        private void EncodeSequential(ScanCtx sc)
        {
            foreach (var c in sc.Comps)
            {
                (dcTbl[c.Td] ?? throw new InvalidOperationException("Huffman table was not defined")).Derive(lossless ? 16 : 15);
                (acTbl[c.Ta] ?? throw new InvalidOperationException("Huffman table was not defined")).Derive(255);
            }
            var bits = new LeBits(outp);
            var lastDc = new int[sc.Comps.Length];
            int restartsToGo = restartInterval, nextRestart = 0;
            int maxCoefBits = prec + 2;
            foreach (var mcu in Mcus(sc))
            {
                if (restartInterval != 0 && restartsToGo == 0)
                {
                    bits.Flush();
                    outp.WriteByte(0xFF);
                    outp.WriteByte((byte)(0xD0 + nextRestart));
                    Array.Clear(lastDc);
                }
                foreach (var (ci, b) in mcu)
                {
                    var c = sc.Comps[ci];
                    var dc = dcTbl[c.Td]!;
                    var ac = acTbl[c.Ta]!;
                    var blk = c.Coef.AsSpan(b * 64, 64);
                    int temp = blk[0] - lastDc[ci];
                    int temp2 = temp;
                    if (temp < 0) { temp = -temp; temp2--; }
                    int nbits = temp == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)temp);
                    if (nbits > maxCoefBits + 1) throw new InvalidOperationException("DCT coefficient out of range");
                    bits.Put(dc.Code[nbits], dc.Size[nbits]);
                    if (nbits != 0) bits.Put((uint)temp2 & ((1u << nbits) - 1), nbits);
                    int r = 0;
                    for (int k = 1; k < 64; k++)
                    {
                        if ((temp = blk[JpegTables.NaturalOrder[k]]) == 0) { r++; continue; }
                        while (r > 15) { bits.Put(ac.Code[0xF0], ac.Size[0xF0]); r -= 16; }
                        temp2 = temp;
                        if (temp < 0) { temp = -temp; temp2--; }
                        nbits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)temp);
                        if (nbits > maxCoefBits) throw new InvalidOperationException("DCT coefficient out of range");
                        int sym = (r << 4) + nbits;
                        bits.Put(ac.Code[sym], ac.Size[sym]);
                        bits.Put((uint)temp2 & ((1u << nbits) - 1), nbits);
                        r = 0;
                    }
                    if (r > 0) bits.Put(ac.Code[0], ac.Size[0]);
                    lastDc[ci] = blk[0];
                }
                if (restartInterval != 0)
                {
                    if (restartsToGo == 0)
                    {
                        restartsToGo = restartInterval;
                        nextRestart = (nextRestart + 1) & 7;
                    }
                    restartsToGo--;
                }
            }
            bits.Flush();
        }

        // ---- progressive Huffman (jcphuff.c) ----

        private void Progressive(ScanCtx sc, JpegScanInfo si, bool gather)
        {
            bool dcBand = si.Ss == 0;
            int al = si.Al;
            var counts = new long[4][];
            foreach (var c in sc.Comps)
            {
                if (dcBand && si.Ah != 0) continue;
                int tbl = dcBand ? c.Td : c.Ta;
                if (gather) counts[tbl] = new long[257];
                else ((dcBand ? dcTbl[tbl] : acTbl[tbl]) ?? throw new InvalidOperationException("Huffman table was not defined")).Derive(dcBand ? 15 : 255);
            }
            int acTblNo = dcBand ? 0 : sc.Comps[0].Ta;
            var bits = new LeBits(outp);
            var lastDc = new int[sc.Comps.Length];
            int eobrun = 0, be = 0;
            const int maxCorrBits = 1000;
            var bitBuffer = new byte[maxCorrBits];
            int restartsToGo = restartInterval, nextRestart = 0;
            int maxCoefBits = prec + 2;
            var natural = JpegTables.NaturalOrder;
            Span<int> absv = stackalloc int[64];

            void EmitBits(uint code, int size)
            {
                if (size == 0) throw new InvalidOperationException("Missing Huffman code table entry");
                if (!gather) bits.Put(code & (uint)((1UL << size) - 1), size);
            }
            void EmitSymbol(int tbl, int symbol, bool dc)
            {
                if (gather) counts[tbl][symbol]++;
                else
                {
                    var t = (dc ? dcTbl[tbl] : acTbl[tbl])!;
                    EmitBits(t.Code[symbol], t.Size[symbol]);
                }
            }
            void EmitBuffered(int start, int n)
            {
                if (gather) return;
                for (int i = 0; i < n; i++) EmitBits(bitBuffer[start + i], 1);
            }
            void EmitEobrun()
            {
                if (eobrun <= 0) return;
                int nb = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)eobrun);
                if (nb > 14) throw new InvalidOperationException("Missing Huffman code table entry");
                EmitSymbol(acTblNo, nb << 4, false);
                if (nb != 0) EmitBits((uint)eobrun, nb);
                eobrun = 0;
                EmitBuffered(0, be);
                be = 0;
            }

            foreach (var mcu in Mcus(sc))
            {
                if (restartInterval != 0 && restartsToGo == 0)
                {
                    EmitEobrun();
                    if (!gather)
                    {
                        bits.Flush();
                        outp.WriteByte(0xFF);
                        outp.WriteByte((byte)(0xD0 + nextRestart));
                    }
                    if (dcBand) Array.Clear(lastDc);
                    else { eobrun = 0; be = 0; }
                }
                if (dcBand && si.Ah == 0)
                {
                    foreach (var (ci, b) in mcu)
                    {
                        var c = sc.Comps[ci];
                        int t2 = c.Coef[b * 64] >> al;
                        int temp = t2 - lastDc[ci];
                        lastDc[ci] = t2;
                        int sign = temp >> 31;
                        temp = (temp ^ sign) - sign;
                        t2 = temp ^ sign;
                        int nb = temp == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)temp);
                        if (nb > maxCoefBits + 1) throw new InvalidOperationException("DCT coefficient out of range");
                        EmitSymbol(c.Td, nb, true);
                        if (nb != 0) EmitBits((uint)t2, nb);
                    }
                }
                else if (dcBand)
                {
                    foreach (var (ci, b) in mcu) EmitBits((uint)(sc.Comps[ci].Coef[b * 64] >> al), 1);
                }
                else if (si.Ah == 0)
                {
                    var blk = sc.Comps[0].Coef.AsSpan(mcu[0].Block * 64, 64);
                    int r = 0;
                    for (int k = si.Ss; k <= si.Se; k++)
                    {
                        int temp = blk[natural[k]];
                        if (temp == 0) { r++; continue; }
                        int temp2;
                        if (temp < 0) { temp = -temp; temp >>= al; temp2 = ~temp; }
                        else { temp >>= al; temp2 = temp; }
                        if (temp == 0) { r++; continue; }
                        if (eobrun > 0) EmitEobrun();
                        while (r > 15) { EmitSymbol(acTblNo, 0xF0, false); r -= 16; }
                        int nb = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)temp);
                        if (nb > maxCoefBits) throw new InvalidOperationException("DCT coefficient out of range");
                        EmitSymbol(acTblNo, (r << 4) + nb, false);
                        EmitBits((uint)temp2, nb);
                        r = 0;
                    }
                    if (r > 0)
                    {
                        eobrun++;
                        if (eobrun == 0x7FFF) EmitEobrun();
                    }
                }
                else
                {
                    var blk = sc.Comps[0].Coef.AsSpan(mcu[0].Block * 64, 64);
                    int eob = 0;
                    for (int k = si.Ss; k <= si.Se; k++)
                    {
                        int temp = blk[natural[k]];
                        if (temp < 0) temp = -temp;
                        temp >>= al;
                        absv[k] = temp;
                        if (temp == 1) eob = k;
                    }
                    int r = 0, br = 0, brStart = be;
                    for (int k = si.Ss; k <= si.Se; k++)
                    {
                        int temp = absv[k];
                        if (temp == 0) { r++; continue; }
                        while (r > 15 && k <= eob)
                        {
                            EmitEobrun();
                            EmitSymbol(acTblNo, 0xF0, false);
                            r -= 16;
                            EmitBuffered(brStart, br);
                            brStart = 0;
                            br = 0;
                        }
                        if (temp > 1)
                        {
                            bitBuffer[brStart + br++] = (byte)(temp & 1);
                            continue;
                        }
                        EmitEobrun();
                        EmitSymbol(acTblNo, (r << 4) + 1, false);
                        EmitBits(blk[natural[k]] < 0 ? 0u : 1u, 1);
                        EmitBuffered(brStart, br);
                        brStart = 0;
                        br = 0;
                        r = 0;
                    }
                    if (r > 0 || br > 0)
                    {
                        eobrun++;
                        be += br;
                        if (eobrun == 0x7FFF || be > maxCorrBits - 64 + 1) EmitEobrun();
                    }
                }
                if (restartInterval != 0)
                {
                    if (restartsToGo == 0)
                    {
                        restartsToGo = restartInterval;
                        nextRestart = (nextRestart + 1) & 7;
                    }
                    restartsToGo--;
                }
            }
            EmitEobrun();
            if (!gather) { bits.Flush(); return; }
            var did = new bool[4];
            foreach (var c in sc.Comps)
            {
                if (dcBand && si.Ah != 0) continue;
                int tbl = dcBand ? c.Td : c.Ta;
                if (did[tbl]) continue;
                if (dcBand) dcTbl[tbl] = LeHuff.Optimal(counts[tbl]);
                else acTbl[tbl] = LeHuff.Optimal(counts[tbl]);
                did[tbl] = true;
            }
        }

        // ---- lossless (jclossls.c differencing, jclhuff.c Huffman) ----

        /// <summary>The differences of a lossless scan's components (all full size: one sample per MCU and component),
        /// with the scan's predictor and point transform; every pass restarts the predictors (start_pass_lossless).</summary>
        private int[][] LosslessDiffs(ScanCtx sc, JpegScanInfo si, int[][] planes)
        {
            if (restartInterval % sc.McusPerRow != 0)
                throw new ArgumentException($"Invalid restart interval {restartInterval}; must be an integer multiple of the number of MCUs in an MCU row ({sc.McusPerRow})");
            int psv = si.Ss, al = si.Al;
            int rowsPerRestart = restartInterval / sc.McusPerRow;
            var diffs = new int[sc.Comps.Length][];
            for (int ci = 0; ci < sc.Comps.Length; ci++)
            {
                var src = planes[sc.Comps[ci].Index];
                var d = diffs[ci] = new int[W * H];
                var prev = new int[W];
                var cur = new int[W];
                bool firstRow = true;
                int rowsToGo = rowsPerRestart;
                for (int y = 0; y < H; y++)
                {
                    int o0 = y * W;
                    for (int x = 0; x < W; x++) cur[x] = src[o0 + x] >> al;
                    if (firstRow)
                    {
                        // the first row after the start or a restart: 1-D, from 2^(P - Pt - 1)
                        d[o0] = cur[0] - (1 << (prec - al - 1));
                        for (int x = 1; x < W; x++) d[o0 + x] = cur[x] - cur[x - 1];
                    }
                    else
                    {
                        d[o0] = cur[0] - prev[0];
                        for (int x = 1; x < W; x++)
                        {
                            long ra = cur[x - 1], rb = prev[x], rc = prev[x - 1];
                            long p = psv switch
                            {
                                1 => ra,
                                2 => rb,
                                3 => rc,
                                4 => ra + rb - rc,
                                5 => ra + ((rb - rc) >> 1),
                                6 => rb + ((ra - rc) >> 1),
                                _ => (ra + rb) >> 1,
                            };
                            d[o0 + x] = cur[x] - (int)p;
                        }
                    }
                    firstRow = false;
                    if (restartInterval != 0 && --rowsToGo == 0) { rowsToGo = rowsPerRestart; firstRow = true; }
                    (prev, cur) = (cur, prev);
                }
            }
            return diffs;
        }

        /// <summary>jclhuff.c: a difference's magnitude category and extra bits, modulo 2^16.</summary>
        private static int LosslessCategory(int temp, out int extra)
        {
            if ((temp & 0x8000) != 0)
            {
                temp = -temp & 0x7FFF;
                if (temp == 0) temp = 0x8000;
                extra = ~temp;
            }
            else
            {
                temp &= 0x7FFF;
                extra = temp;
            }
            int nb = 0;
            while (temp != 0) { nb++; temp >>= 1; }
            return nb;
        }

        private void GatherLossless(ScanCtx sc, int[][] diffs)
        {
            var counts = new long[4][];
            foreach (var c in sc.Comps) counts[c.Td] = new long[257];
            for (int i = 0; i < W * H; i++)
                for (int ci = 0; ci < sc.Comps.Length; ci++)
                    counts[sc.Comps[ci].Td][LosslessCategory(diffs[ci][i], out _)]++;
            var did = new bool[4];
            foreach (var c in sc.Comps)
                if (!did[c.Td]) { dcTbl[c.Td] = LeHuff.Optimal(counts[c.Td]); did[c.Td] = true; }
        }

        private void EncodeLossless(ScanCtx sc, int[][] diffs)
        {
            foreach (var c in sc.Comps)
                (dcTbl[c.Td] ?? throw new InvalidOperationException("Huffman table was not defined")).Derive(16);
            var bits = new LeBits(outp);
            int restartsToGo = restartInterval, nextRestart = 0;
            for (int i = 0; i < W * H; i++)
            {
                if (restartInterval != 0 && restartsToGo == 0)
                {
                    bits.Flush();
                    outp.WriteByte(0xFF);
                    outp.WriteByte((byte)(0xD0 + nextRestart));
                }
                for (int ci = 0; ci < sc.Comps.Length; ci++)
                {
                    var t = dcTbl[sc.Comps[ci].Td]!;
                    int nb = LosslessCategory(diffs[ci][i], out int extra);
                    if (t.Size[nb] == 0) throw new InvalidOperationException("Missing Huffman code table entry");
                    bits.Put(t.Code[nb], t.Size[nb]);
                    if (nb != 0 && nb != 16) bits.Put((uint)extra & ((1u << nb) - 1), nb);
                }
                if (restartInterval != 0)
                {
                    if (restartsToGo == 0)
                    {
                        restartsToGo = restartInterval;
                        nextRestart = (nextRestart + 1) & 7;
                    }
                    restartsToGo--;
                }
            }
            bits.Flush();
        }

        // ---- arithmetic coding (jcarith.c) ----

        private sealed class LeArith
        {
            private readonly MemoryStream o;
            private long c, a, sc, zc;
            private int ct, buffer;
            public LeArith(MemoryStream outp) { o = outp; Reset(); }

            public void Reset() { c = 0; a = 0x10000L; sc = 0; zc = 0; ct = 11; buffer = -1; }

            private void Zeros() { while (zc > 0) { o.WriteByte(0); zc--; } }

            public void Encode(byte[] st, int i, int val)
            {
                int sv = st[i];
                long qe = LjAritab[sv & 0x7F];
                int nl = (int)(qe & 0xFF); qe >>= 8;
                int nm = (int)(qe & 0xFF); qe >>= 8;
                a -= qe;
                if (val != (sv >> 7))
                {
                    if (a >= qe) { c += a; a = qe; }
                    st[i] = (byte)((sv & 0x80) ^ nl);
                }
                else
                {
                    if (a >= 0x8000L) return;
                    if (a < qe) { c += a; a = qe; }
                    st[i] = (byte)((sv & 0x80) ^ nm);
                }
                do
                {
                    a <<= 1;
                    c <<= 1;
                    if (--ct == 0)
                    {
                        long temp = c >> 19;
                        if (temp > 0xFF)
                        {
                            if (buffer >= 0)
                            {
                                Zeros();
                                o.WriteByte((byte)(buffer + 1));
                                if (buffer + 1 == 0xFF) o.WriteByte(0);
                            }
                            zc += sc;
                            sc = 0;
                            buffer = (int)(temp & 0xFF);
                        }
                        else if (temp == 0xFF) sc++;
                        else
                        {
                            if (buffer == 0) zc++;
                            else if (buffer >= 0)
                            {
                                Zeros();
                                o.WriteByte((byte)buffer);
                            }
                            if (sc != 0)
                            {
                                Zeros();
                                do { o.WriteByte(0xFF); o.WriteByte(0); } while (--sc != 0);
                            }
                            buffer = (int)(temp & 0xFF);
                        }
                        c &= 0x7FFFFL;
                        ct += 8;
                    }
                } while (a < 0x8000L);
            }

            public void Finish()
            {
                long temp = (a - 1 + c) & 0xFFFF0000L;
                c = temp < c ? temp + 0x8000L : temp;
                c <<= ct;
                if ((c & 0xF8000000L) != 0)
                {
                    if (buffer >= 0)
                    {
                        Zeros();
                        o.WriteByte((byte)(buffer + 1));
                        if (buffer + 1 == 0xFF) o.WriteByte(0);
                    }
                    zc += sc;
                    sc = 0;
                }
                else
                {
                    if (buffer == 0) zc++;
                    else if (buffer >= 0)
                    {
                        Zeros();
                        o.WriteByte((byte)buffer);
                    }
                    if (sc != 0)
                    {
                        Zeros();
                        do { o.WriteByte(0xFF); o.WriteByte(0); } while (--sc != 0);
                    }
                }
                if ((c & 0x7FFF800L) != 0)
                {
                    Zeros();
                    o.WriteByte((byte)((c >> 19) & 0xFF));
                    if (((c >> 19) & 0xFF) == 0xFF) o.WriteByte(0);
                    if ((c & 0x7F800L) != 0)
                    {
                        o.WriteByte((byte)((c >> 11) & 0xFF));
                        if (((c >> 11) & 0xFF) == 0xFF) o.WriteByte(0);
                    }
                }
            }
        }

        private const int ArithDcL = 0, ArithDcU = 1, ArithAcK = 5;   // jcparam.c defaults (DAC)

        private void Arithmetic(ScanCtx sc, JpegScanInfo si)
        {
            var e = new LeArith(outp);
            var dcStats = new byte[4][];
            var acStats = new byte[4][];
            var fixedBin = new byte[] { 113 };
            bool dcPart = !progressive || (si.Ss == 0 && si.Ah == 0), acPart = !progressive || si.Se != 0;
            foreach (var c in sc.Comps)
            {
                if (dcPart) dcStats[c.Td] = new byte[64];
                if (acPart) acStats[c.Ta] = new byte[256];
            }
            var lastDc = new int[sc.Comps.Length];
            var dcContext = new int[sc.Comps.Length];
            int restartsToGo = restartInterval, nextRestart = 0;
            var natural = JpegTables.NaturalOrder;
            int al = si.Al;

            void EncodeDc(int ci, int tbl, int v)
            {
                var stats = dcStats[tbl];
                int st = dcContext[ci];
                if (v == 0)
                {
                    e.Encode(stats, st, 0);
                    dcContext[ci] = 0;
                    return;
                }
                e.Encode(stats, st, 1);
                if (v > 0) { e.Encode(stats, st + 1, 0); st += 2; dcContext[ci] = 4; }
                else { v = -v; e.Encode(stats, st + 1, 1); st += 3; dcContext[ci] = 8; }
                int m = 0;
                if ((v -= 1) != 0)
                {
                    e.Encode(stats, st, 1);
                    m = 1;
                    int v2 = v;
                    st = 20;
                    while ((v2 >>= 1) != 0) { e.Encode(stats, st, 1); m <<= 1; st++; }
                }
                e.Encode(stats, st, 0);
                if (m < (1 << ArithDcL) >> 1) dcContext[ci] = 0;
                else if (m > (1 << ArithDcU) >> 1) dcContext[ci] += 8;
                st += 14;
                while ((m >>= 1) != 0) e.Encode(stats, st, (m & v) != 0 ? 1 : 0);
            }

            // the magnitude category and bits of v >= 1 (after the sign), from context st (jcarith.c)
            void EncodeAcMagnitude(byte[] stats, int st, int k, int v)
            {
                st += 2;
                int m = 0;
                if ((v -= 1) != 0)
                {
                    e.Encode(stats, st, 1);
                    m = 1;
                    int v2 = v;
                    if ((v2 >>= 1) != 0)
                    {
                        e.Encode(stats, st, 1);
                        m <<= 1;
                        st = k <= ArithAcK ? 189 : 217;
                        while ((v2 >>= 1) != 0) { e.Encode(stats, st, 1); m <<= 1; st++; }
                    }
                }
                e.Encode(stats, st, 0);
                st += 14;
                while ((m >>= 1) != 0) e.Encode(stats, st, (m & v) != 0 ? 1 : 0);
            }

            foreach (var mcu in Mcus(sc))
            {
                if (restartInterval != 0)
                {
                    if (restartsToGo == 0)
                    {
                        e.Finish();
                        outp.WriteByte(0xFF);
                        outp.WriteByte((byte)(0xD0 + nextRestart));
                        foreach (var c in sc.Comps)
                        {
                            if (dcPart) Array.Clear(dcStats[c.Td]);
                            if (acPart) Array.Clear(acStats[c.Ta]);
                        }
                        if (dcPart) { Array.Clear(lastDc); Array.Clear(dcContext); }
                        e.Reset();
                        restartsToGo = restartInterval;
                        nextRestart = (nextRestart + 1) & 7;
                    }
                    restartsToGo--;
                }
                if (!progressive)
                {
                    foreach (var (ci, b) in mcu)
                    {
                        var c = sc.Comps[ci];
                        var blk = c.Coef.AsSpan(b * 64, 64);
                        int v = blk[0] - lastDc[ci];
                        if (v != 0) lastDc[ci] = blk[0];
                        EncodeDc(ci, c.Td, v);
                        var stats = acStats[c.Ta];
                        int ke;
                        for (ke = 63; ke > 0; ke--) if (blk[natural[ke]] != 0) break;
                        int k;
                        for (k = 1; k <= ke; k++)
                        {
                            int st = 3 * (k - 1);
                            e.Encode(stats, st, 0);
                            while ((v = blk[natural[k]]) == 0) { e.Encode(stats, st + 1, 0); st += 3; k++; }
                            e.Encode(stats, st + 1, 1);
                            if (v > 0) e.Encode(fixedBin, 0, 0);
                            else { v = -v; e.Encode(fixedBin, 0, 1); }
                            EncodeAcMagnitude(stats, st, k, v);
                        }
                        if (k <= 63) e.Encode(stats, 3 * (k - 1), 1);
                    }
                }
                else if (si.Ss == 0 && si.Ah == 0)
                {
                    foreach (var (ci, b) in mcu)
                    {
                        var c = sc.Comps[ci];
                        int m = c.Coef[b * 64] >> al;
                        int v = m - lastDc[ci];
                        if (v != 0) lastDc[ci] = m;
                        EncodeDc(ci, c.Td, v);
                    }
                }
                else if (si.Ss == 0)
                {
                    foreach (var (ci, b) in mcu) e.Encode(fixedBin, 0, (sc.Comps[ci].Coef[b * 64] >> al) & 1);
                }
                else
                {
                    var c = sc.Comps[0];
                    var blk = c.Coef.AsSpan(mcu[0].Block * 64, 64);
                    var stats = acStats[c.Ta];
                    static int Mag(int x, int sh) => (x < 0 ? -x : x) >> sh;
                    int ke;
                    for (ke = si.Se; ke > 0; ke--) if (Mag(blk[natural[ke]], al) != 0) break;
                    int k;
                    if (si.Ah == 0)
                    {
                        for (k = si.Ss; k <= ke; k++)
                        {
                            int st = 3 * (k - 1);
                            e.Encode(stats, st, 0);
                            int v;
                            for (;;)
                            {
                                int raw = blk[natural[k]];
                                v = Mag(raw, al);
                                if (v != 0)
                                {
                                    e.Encode(stats, st + 1, 1);
                                    e.Encode(fixedBin, 0, raw >= 0 ? 0 : 1);
                                    break;
                                }
                                e.Encode(stats, st + 1, 0); st += 3; k++;
                            }
                            EncodeAcMagnitude(stats, st, k, v);
                        }
                    }
                    else
                    {
                        int kex;
                        for (kex = ke; kex > 0; kex--) if (Mag(blk[natural[kex]], si.Ah) != 0) break;
                        for (k = si.Ss; k <= ke; k++)
                        {
                            int st = 3 * (k - 1);
                            if (k > kex) e.Encode(stats, st, 0);
                            for (;;)
                            {
                                int raw = blk[natural[k]];
                                int v = Mag(raw, al);
                                if (v != 0)
                                {
                                    if ((v >> 1) != 0) e.Encode(stats, st + 2, v & 1);
                                    else
                                    {
                                        e.Encode(stats, st + 1, 1);
                                        e.Encode(fixedBin, 0, raw >= 0 ? 0 : 1);
                                    }
                                    break;
                                }
                                e.Encode(stats, st + 1, 0); st += 3; k++;
                            }
                        }
                    }
                    if (k <= si.Se) e.Encode(stats, 3 * (k - 1), 1);
                }
            }
            e.Finish();
        }

        // ---- markers (jcmarker.c) ----

        private void Byte(int v) => outp.WriteByte((byte)v);
        private void Word(int v) { outp.WriteByte((byte)(v >> 8)); outp.WriteByte((byte)v); }
        private void Marker(int m) { outp.WriteByte(0xFF); outp.WriteByte((byte)m); }

        private readonly bool[] qtSent = new bool[4];

        private void WriteFrameHeader()
        {
            int precAny = 0;
            if (!lossless)
                foreach (var c in comps)
                {
                    var q = qt[c.Tq] ?? throw new InvalidOperationException($"Quantization table 0x{c.Tq:x2} was not defined");
                    int p = q.Any(v => v > 255) ? 1 : 0;
                    precAny += p;
                    if (qtSent[c.Tq]) continue;
                    Marker(0xDB);
                    Word(p != 0 ? 64 * 2 + 1 + 2 : 64 + 1 + 2);
                    Byte(c.Tq + (p << 4));
                    for (int i = 0; i < 64; i++)
                    {
                        int v = q[JpegTables.NaturalOrder[i]];
                        if (p != 0) Byte(v >> 8);
                        Byte(v & 0xFF);
                    }
                    qtSent[c.Tq] = true;
                }
            bool baseline = !(arith || progressive || lossless || prec != 8);
            if (baseline)
            {
                foreach (var c in comps) if (c.Td > 1 || c.Ta > 1) baseline = false;
                if (precAny != 0) baseline = false;
            }
            int sof = arith ? (progressive ? 0xCA : 0xC9) : progressive ? 0xC2 : lossless ? 0xC3 : baseline ? 0xC0 : 0xC1;
            Marker(sof);
            Word(3 * comps.Length + 2 + 5 + 1);
            Byte(prec);
            Word(H);
            Word(W);
            Byte(comps.Length);
            foreach (var c in comps)
            {
                Byte(c.Id);
                Byte((c.H << 4) + c.V);
                Byte(c.Tq);
            }
        }

        private void WriteScanHeader(ScanCtx sc, JpegScanInfo s)
        {
            if (arith)
            {
                // emit_dac: the conditioning of every table the scan uses (libjpeg's defaults L=0 U=1, K=5)
                var dcUse = new bool[4]; var acUse = new bool[4];
                foreach (var c in sc.Comps)
                {
                    if (s.Ss == 0 && s.Ah == 0) dcUse[c.Td] = true;
                    if (s.Se != 0) acUse[c.Ta] = true;
                }
                int n = dcUse.Count(x => x) + acUse.Count(x => x);
                if (n != 0)
                {
                    Marker(0xCC); Word(n * 2 + 2);
                    for (int i = 0; i < 4; i++)
                    {
                        if (dcUse[i]) { Byte(i); Byte(ArithDcL + (ArithDcU << 4)); }
                        if (acUse[i]) { Byte(i + 0x10); Byte(ArithAcK); }
                    }
                }
            }
            else
            {
                foreach (var c in sc.Comps)
                {
                    if ((s.Ss == 0 && s.Ah == 0) || lossless) Dht(c.Td, false);
                    if (s.Se != 0 && !lossless) Dht(c.Ta, true);
                }
            }
            if (restartInterval != lastRestartInterval)
            {
                Marker(0xDD); Word(4); Word(restartInterval);
                lastRestartInterval = restartInterval;
            }
            Marker(0xDA);
            Word(2 * sc.Comps.Length + 2 + 1 + 3);
            Byte(sc.Comps.Length);
            foreach (var c in sc.Comps)
            {
                Byte(c.Id);
                int td = s.Ss == 0 && s.Ah == 0 ? c.Td : 0;
                int ta = s.Se != 0 ? c.Ta : 0;
                Byte((td << 4) + ta);
            }
            Byte(s.Ss); Byte(s.Se); Byte((s.Ah << 4) + s.Al);
        }

        private void Dht(int index, bool isAc)
        {
            var t = (isAc ? acTbl[index] : dcTbl[index]) ?? throw new InvalidOperationException("Huffman table was not defined");
            if (t.Sent) return;
            Marker(0xC4);
            int length = 0;
            for (int i = 1; i <= 16; i++) length += t.Bits[i];
            Word(length + 2 + 1 + 16);
            Byte(index + (isAc ? 0x10 : 0));
            for (int i = 1; i <= 16; i++) Byte(t.Bits[i]);
            for (int i = 0; i < length; i++) Byte(t.Vals[i]);
            t.Sent = true;
        }
    }
}
