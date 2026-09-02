// JPEG XL (lossless Modular) encoder: the inverse of the decoder in this directory. It emits a bare
// codestream (0xFF 0x0A) with an 8-bit sRGB ImageMetadata and a Modular frame that applies the
// reversible YCoCg RCT, predicts with the self-correcting weighted predictor (run through the
// decoder's own WpState so residuals match bit-for-bit), and entropy-codes the residuals with prefix
// (Huffman) codes plus LZ77 back-references (to collapse flat/repeating regions). Large images are
// tiled into groups. Verified by decoding the output with the reference decoders (libjxl / jxl-oxide),
// not just round-tripping.
using System;
using System.Collections.Generic;
using SharpImage.Core;
using SharpImage.Image;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

internal static class JxlEncoder
{
    // MA-tree leaf predictor: 6 = self-correcting weighted predictor (see JxlModular.PredictOne).
    private const int WeightedPredictor = 6;

    /// <summary>Group side when a single group covers the image (group_size_shift 3 => 1024).</summary>
    private const int GroupDim = 1024;
    private const int GroupSizeShift = 3; // 128 << 3 == 1024

    /// <summary>Encodes an image as a lossless RGB JPEG XL codestream.</summary>
    public static byte[] EncodeLossless(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("Cannot encode an empty image.");
        }

        // Extract three 8-bit channels (grayscale is expanded to RGB), then decorrelate colour with the
        // reversible YCoCg transform (RCT type 6) so the residuals are far more compressible.
        int nb = 3;
        int[][] chan = new int[nb][];
        for (int c = 0; c < nb; c++)
        {
            chan[c] = new int[w * h];
        }

        int srcCh = image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh;
                int r, g, b;
                if (srcCh == 1)
                {
                    r = g = b = Quantum.ScaleToByte(row[off]);
                }
                else
                {
                    r = Quantum.ScaleToByte(row[off]);
                    g = Quantum.ScaleToByte(row[off + 1]);
                    b = Quantum.ScaleToByte(row[off + 2]);
                }

                // Forward YCoCg (inverse of JxlModular.InvRct custom==6).
                int co = r - b;
                int tmp = b + (co >> 1);
                int cg = g - tmp;
                int yy = tmp + (cg >> 1);
                int p = (y * w) + x;
                chan[0][p] = yy;
                chan[1][p] = co;
                chan[2][p] = cg;
            }
        }

        // A single group can cover an image up to GroupDim on both sides; larger images are tiled.
        bool single = w <= GroupDim && h <= GroupDim;
        int shift = single ? SmallestShift(Math.Max(w, h)) : GroupSizeShift;
        int groupDim = 128 << shift;

        byte[][] sections = single
            ? new[] { BuildModularSection(chan, w, h, nb) }
            : BuildMultiGroupSections(chan, w, h, nb, groupDim);

        var main = new JxlBitWriter();
        main.WriteBits(0xFF, 8);
        main.WriteBits(0x0A, 8);
        WriteSizeHeader(main, w, h);
        WriteImageMetadata(main);
        main.JumpToByteBoundary();
        WriteFrameHeader(main, shift);
        main.WriteBool(false); // permuted TOC = false
        main.JumpToByteBoundary();

        foreach (byte[] sec in sections)
        {
            main.WriteU32((uint)sec.Length, E.BitsOff(10, 0), E.BitsOff(14, 1024), E.BitsOff(22, 17408), E.BitsOff(30, 4211712));
        }

        main.JumpToByteBoundary();
        foreach (byte[] sec in sections)
        {
            main.AppendBytes(sec);
        }

        return main.ToArray();
    }

    private static int SmallestShift(int side)
    {
        int shift = 0;
        while ((128 << shift) < side && shift < GroupSizeShift)
        {
            shift++;
        }

        return shift;
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    private static void WriteSizeHeader(JxlBitWriter w, int width, int height)
    {
        w.WriteBool(false); // not "small"
        w.WriteU32((uint)height, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        w.WriteBits(0, 3); // aspect ratio 0 => explicit width
        w.WriteU32((uint)width, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
    }

    private static void WriteImageMetadata(JxlBitWriter w)
    {
        w.WriteBool(false); // not all_default
        w.WriteBool(false); // extra_fields = false
        w.WriteBool(false); // bit depth: not floating
        w.WriteU32(8, E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)); // 8 bits per sample
        w.WriteBool(true);  // modular_16bit_buffer_sufficient
        w.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1)); // num_extra_channels = 0
        w.WriteBool(false); // xyb_encoded = false
        w.WriteBool(true);  // colour encoding: all_default (sRGB RGB)
        w.WriteU64(0);      // extensions = none
        w.WriteBool(true);  // default_m (skip opsin / upsampling weights)
    }

    private static void WriteFrameHeader(JxlBitWriter w, int shift)
    {
        w.WriteBool(false);  // not all_default
        w.WriteBits(0, 2);   // frame_type = Regular
        w.WriteBits(1, 1);   // encoding = Modular
        w.WriteU64(0);       // flags = 0
        w.WriteBool(false);  // do_ycbcr = false (non-XYB)
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // upsampling = 1
        w.WriteBits((uint)shift, 2); // group_size_shift
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4)); // num_passes = 1
        w.WriteBool(false);  // have_crop = false
        w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3)); // blending mode = 0 (replace)
        w.WriteBool(true);   // is_last = true
        w.WriteU32(0, E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48)); // name length = 0

        // Loop filter: the all_default filter leaves Gaborish + EPF ON, which libjxl applies even to a
        // Modular frame and would blur a lossless image. Disable both explicitly.
        w.WriteBool(false);  // loop filter: not all_default
        w.WriteBool(false);  // gaborish = off
        w.WriteBits(0, 2);   // epf_iters = 0
        w.WriteU64(0);       // loop-filter extensions = none

        w.WriteU64(0);       // frame-header extensions = none
    }

    // --- Single-group frame: one section holding the tree, the pixel histogram and every pixel token
    // (DecodeGlobalModular). The three channels are decoded through one entropy reader, so LZ77 spans
    // them: the token stream is the channels concatenated in raster order. ---
    private static byte[] BuildModularSection(int[][] chan, int w, int h, int nb)
    {
        int[] stream = ConcatChannelResiduals(chan, w, h, nb, out _);
        var plan = PlanPixels(new List<int[]> { stream }, out List<Op>[] ops);

        var s = new JxlBitWriter();
        s.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s.WriteBits(1, 1); // has_tree = 1
        WriteSingleLeafTree(s);
        WriteLz77Histogram(s, plan);
        WriteGlobalGroupHeader(s);
        EmitOps(s, ops[0], plan);
        return s.ToArray();
    }

    // --- Multi-group frame: LfGlobal (tree + pixel histogram + global RCT), empty LfGroup/HfGlobal
    // sections, then one Modular-AC section per spatial group. All groups share the global pixel code;
    // each group has its own entropy reader/window, so LZ77 runs per group. ---
    private static byte[][] BuildMultiGroupSections(int[][] chan, int w, int h, int nb, int groupDim)
    {
        int gpr = CeilDiv(w, groupDim);
        int numGroups = gpr * CeilDiv(h, groupDim);
        int lfDim = groupDim * 8;
        int numLf = CeilDiv(w, lfDim) * CeilDiv(h, lfDim);

        var streams = new List<int[]>(numGroups);
        for (int g = 0; g < numGroups; g++)
        {
            int rx = (g % gpr) * groupDim;
            int ry = (g / gpr) * groupDim;
            int rw = Math.Min(groupDim, w - rx);
            int rh = Math.Min(groupDim, h - ry);
            int[] concat = new int[rw * rh * nb];
            int dummy = 0;
            for (int c = 0; c < nb; c++)
            {
                int[] tc = ComputeGroupResidualTokens(chan[c], w, rx, ry, rw, rh, ref dummy);
                Array.Copy(tc, 0, concat, c * rw * rh, tc.Length);
            }

            streams.Add(concat);
        }

        var plan = PlanPixels(streams, out List<Op>[] ops);

        // Section 0: LfGlobal.
        var s0 = new JxlBitWriter();
        s0.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s0.WriteBits(1, 1); // has_tree = 1
        WriteSingleLeafTree(s0);
        WriteLz77Histogram(s0, plan);
        WriteGlobalGroupHeader(s0); // use_global + wp + RCT; no channels are small enough to decode here

        var list = new List<byte[]>(1 + numLf + 1 + numGroups) { s0.ToArray() };
        for (int i = 0; i < numLf; i++)
        {
            list.Add(Array.Empty<byte>()); // LfGroup: empty for a Modular frame
        }

        list.Add(Array.Empty<byte>()); // HfGlobal: empty for a Modular frame

        for (int g = 0; g < numGroups; g++)
        {
            var sg = new JxlBitWriter();
            sg.WriteBool(true); // use_global tree + code
            sg.WriteBits(1, 1); // weighted-predictor header: all default
            sg.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0
            EmitOps(sg, ops[g], plan);
            list.Add(sg.ToArray());
        }

        return list.ToArray();
    }

    private static int[] ConcatChannelResiduals(int[][] chan, int w, int h, int nb, out int maxToken)
    {
        maxToken = 0;
        int[] stream = new int[w * h * nb];
        for (int c = 0; c < nb; c++)
        {
            int[] tc = ComputeResidualTokens(chan[c], w, h, ref maxToken);
            Array.Copy(tc, 0, stream, c * w * h, tc.Length);
        }

        return stream;
    }

    // Single-leaf global MA tree: property token 0 (=> leaf), then predictor / offset / mult-log /
    // mult-bits. The tree stream reads six contexts, all mapped to one histogram.
    private static void WriteSingleLeafTree(JxlBitWriter s)
    {
        long[] treeFreq = new long[WeightedPredictor + 1];
        treeFreq[0] = 4;                 // property(0), offset(0), mult-log(0), mult-bits(0)
        treeFreq[WeightedPredictor] = 1; // predictor
        var treeCode = new JxlPrefixCode(treeFreq, WeightedPredictor + 1);
        WriteHistogramsHeader(s, numContexts: 6, treeCode);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, WeightedPredictor);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, 0);
    }

    // ─── LZ77 over the residual-token stream ───────────────────────────────────────────────────────
    // The Modular pixel stream is entropy-coded with LZ77 enabled: a token >= a threshold signals a
    // back-reference (length + distance) instead of a literal residual, which collapses flat and
    // repeating regions. Two histogram clusters are used — cluster 0 for literals + length markers,
    // cluster 1 for distances — matching DecodeHistograms + JxlAnsReader's LZ77 path.
    private readonly struct Op
    {
        public readonly bool Match;
        public readonly int A; // literal value, or match length
        public readonly int B; // match distance
        public Op(bool match, int a, int b) { Match = match; A = a; B = b; }
    }

    private sealed class PixelPlan
    {
        public int Threshold;
        public int SeLit, SeDist, SeLen, MinLen;
        public JxlPrefixCode Code0 = null!; // literals + length markers
        public JxlPrefixCode Code1 = null!; // distances
    }

    private const int Lz77MinLength = 16;   // shortest worthwhile back-reference
    private const int NumSpecialDistances = 120; // JxlEntropy.NumSpecialDistances (distanceMultiplier > 0)

    private static PixelPlan PlanPixels(List<int[]> streams, out List<Op>[] opsOut)
    {
        int minLen = Lz77MinLength;
        int maxToken = 0;
        foreach (int[] st in streams)
        {
            foreach (int v in st)
            {
                if (v > maxToken)
                {
                    maxToken = v;
                }
            }
        }

        var plan = new PixelPlan
        {
            Threshold = Math.Max(8, maxToken + 1),
            SeLit = Math.Min(15, Math.Max(1, BitLen(maxToken))),
            SeDist = 4,
            SeLen = 4,
            MinLen = minLen,
        };

        opsOut = new List<Op>[streams.Count];
        int max0 = 0, max1 = 0;
        for (int i = 0; i < streams.Count; i++)
        {
            opsOut[i] = FindMatches(streams[i], minLen);
            foreach (Op op in opsOut[i])
            {
                if (!op.Match)
                {
                    if (op.A > max0)
                    {
                        max0 = op.A;
                    }
                }
                else
                {
                    int lenSym = plan.Threshold + PackHybridUint(plan.SeLen, op.A - minLen).Token;
                    int dtok = PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1).Token;
                    if (lenSym > max0)
                    {
                        max0 = lenSym;
                    }

                    if (dtok > max1)
                    {
                        max1 = dtok;
                    }
                }
            }
        }

        long[] f0 = new long[max0 + 1];
        long[] f1 = new long[max1 + 1];
        foreach (List<Op> ops in opsOut)
        {
            foreach (Op op in ops)
            {
                if (!op.Match)
                {
                    f0[op.A]++;
                }
                else
                {
                    f0[plan.Threshold + PackHybridUint(plan.SeLen, op.A - minLen).Token]++;
                    f1[PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1).Token]++;
                }
            }
        }

        plan.Code0 = new JxlPrefixCode(f0, max0 + 1);
        plan.Code1 = new JxlPrefixCode(f1, max1 + 1);
        return plan;
    }

    // Greedy longest-match LZ77 over the value stream, using a 4-gram hash with chained positions.
    // Overlapping matches (distance < length) are allowed — they reproduce run-length fills.
    private static List<Op> FindMatches(int[] v, int minLen)
    {
        var ops = new List<Op>();
        int n = v.Length;
        const int WindowMask = (1 << 20) - 1;
        var head = new Dictionary<int, int>();
        int[] prev = new int[Math.Max(1, n)];

        int Hash(int i)
        {
            unchecked
            {
                uint hh = (uint)v[i];
                hh = (hh * 2654435761u) + (uint)v[i + 1];
                hh = (hh * 2654435761u) + (uint)v[i + 2];
                hh = (hh * 2654435761u) + (uint)v[i + 3];
                return (int)(hh & 0x7FFFFFFF);
            }
        }

        void Insert(int i)
        {
            if (i + 4 > n)
            {
                return;
            }

            int hh = Hash(i);
            prev[i] = head.TryGetValue(hh, out int p) ? p : -1;
            head[hh] = i;
        }

        int idx = 0;
        while (idx < n)
        {
            int bestLen = 0, bestDist = 0;
            if (idx + 4 <= n && head.TryGetValue(Hash(idx), out int p))
            {
                int tries = 96;
                while (p >= 0 && tries-- > 0)
                {
                    int dist = idx - p;
                    if (dist > (WindowMask + 1))
                    {
                        break;
                    }

                    int maxl = n - idx;
                    int l = 0;
                    while (l < maxl && v[p + l] == v[idx + l])
                    {
                        l++;
                    }

                    if (l > bestLen)
                    {
                        bestLen = l;
                        bestDist = dist;
                    }

                    p = prev[p];
                }
            }

            if (bestLen >= minLen)
            {
                ops.Add(new Op(true, bestLen, bestDist));
                int end = idx + bestLen;
                for (int j = idx; j < end; j++)
                {
                    Insert(j);
                }

                idx = end;
            }
            else
            {
                ops.Add(new Op(false, v[idx], 0));
                Insert(idx);
                idx++;
            }
        }

        return ops;
    }

    private static void EmitOps(JxlBitWriter s, List<Op> ops, PixelPlan plan)
    {
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                plan.Code0.WriteSymbol(s, op.A);
                continue;
            }

            (int lenBase, int nbLen, int bitsLen) = PackHybridUint(plan.SeLen, op.A - plan.MinLen);
            plan.Code0.WriteSymbol(s, plan.Threshold + lenBase);
            s.WriteBits((uint)bitsLen, nbLen);

            (int dtok, int nbDist, int bitsDist) = PackHybridUint(plan.SeDist, op.B + NumSpecialDistances - 1);
            plan.Code1.WriteSymbol(s, dtok);
            s.WriteBits((uint)bitsDist, nbDist);
        }
    }

    // DecodeHistograms mirror for the pixel stream: LZ77 enabled, a 2-entry context map (literals ->
    // cluster 0, distances -> cluster 1), prefix coding, and the two per-cluster hybrid-uint configs.
    private static void WriteLz77Histogram(JxlBitWriter s, PixelPlan plan)
    {
        s.WriteBool(true); // lz77 enabled
        s.WriteU32((uint)plan.Threshold, E.Val(224), E.Val(512), E.Val(4096), E.BitsOff(15, 8)); // min_symbol
        s.WriteU32((uint)plan.MinLen, E.Val(3), E.Val(4), E.BitsOff(2, 5), E.BitsOff(8, 9)); // min_length
        WriteUintConfig(s, plan.SeLen, 0, 0, 8); // lz77 length config (logAlpha 8)

        // Context map for {literal context, distance context}: simple, 1 bit per entry, [0, 1].
        s.WriteBool(true); // is_simple
        s.WriteBits(1, 2); // bits per entry = 1
        s.WriteBits(0, 1); // context 0 -> cluster 0
        s.WriteBits(1, 1); // context 1 (distance) -> cluster 1

        s.WriteBool(true); // use_prefix_code
        WriteUintConfig(s, plan.SeLit, 0, 0, 15);  // cluster 0
        WriteUintConfig(s, plan.SeDist, 0, 0, 15); // cluster 1
        s.WriteVarLenUint16(plan.Code0.AlphabetSize - 1);
        s.WriteVarLenUint16(plan.Code1.AlphabetSize - 1);
        plan.Code0.WriteHeader(s);
        plan.Code1.WriteHeader(s);
    }

    // Encodes a value under a hybrid-uint config (SplitExp, 0, 0): values below 2^SplitExp are literal
    // tokens; larger values become an exponent token plus mantissa extra bits. Inverse of
    // JxlEntropy.ReadHybridUintConfig.
    private static (int Token, int NBits, int Bits) PackHybridUint(int splitExp, int value)
    {
        int splitToken = 1 << splitExp;
        if (value < splitToken)
        {
            return (value, 0, 0);
        }

        int nb = BitLen(value) - 1;      // floor(log2(value))
        int token = splitToken + (nb - splitExp);
        int bits = value - (1 << nb);
        return (token, nb, bits);
    }

    private static int BitLen(int x) => x <= 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);

    // GroupHeader for the global modular stream: use_global tree+code, default weighted-predictor
    // header, and one transform — the YCoCg RCT, inverted after decode.
    private static void WriteGlobalGroupHeader(JxlBitWriter s)
    {
        s.WriteBool(true);  // use_global tree + code
        s.WriteBits(1, 1);  // weighted-predictor header: all default
        s.WriteU32(1, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 1
        s.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.Val(3)); // transform id = 0 (RCT)
        s.WriteU32(0, E.BitsOff(3, 0), E.BitsOff(6, 8), E.BitsOff(10, 72), E.BitsOff(13, 1096)); // begin_c = 0
        s.WriteU32(6, E.Val(6), E.BitsOff(2, 0), E.BitsOff(4, 2), E.BitsOff(6, 10)); // rct_type = 6
    }

    // Self-correcting weighted-predictor (predictor 6) residuals, packed with the JXL signed->unsigned
    // mapping. The encoder runs the decoder's exact WpState so the predictions — and thus residuals —
    // match the decode bit-for-bit.
    private static int[] ComputeResidualTokens(int[] px, int w, int h, ref int maxToken)
    {
        int[] tokens = new int[w * h];
        int local = maxToken;
        var wp = new WpState(WpHeader.Default(), w);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                int top = y > 0 ? px[((y - 1) * w) + x] : left;
                int topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                int topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
                int toptop = y > 1 ? px[((y - 2) * w) + x] : top;
                long guess = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
                int pixel = px[(y * w) + x];
                int residual = pixel - (int)guess;
                int token = (int)(uint)((residual << 1) ^ (residual >> 31)); // PackSigned
                tokens[(y * w) + x] = token;
                if (token > local)
                {
                    local = token;
                }

                wp.Update(pixel, x, y);
            }
        }

        maxToken = local;
        return tokens;
    }

    // WP residuals for one tile (rx,ry,rw,rh) of a full channel. Prediction is tile-local — pixels
    // outside the tile are treated as edges — exactly as DecodeMultiGroup decodes each group.
    private static int[] ComputeGroupResidualTokens(int[] full, int fullW, int rx, int ry, int rw, int rh, ref int maxToken)
    {
        int[] tokens = new int[rw * rh];
        int local = maxToken;
        var wp = new WpState(WpHeader.Default(), rw);
        int P(int xx, int yy) => full[((ry + yy) * fullW) + rx + xx];
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int left = x > 0 ? P(x - 1, y) : (y > 0 ? P(x, y - 1) : 0);
                int top = y > 0 ? P(x, y - 1) : left;
                int topleft = (x > 0 && y > 0) ? P(x - 1, y - 1) : left;
                int topright = (x + 1 < rw && y > 0) ? P(x + 1, y - 1) : top;
                int toptop = y > 1 ? P(x, y - 2) : top;
                long guess = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
                int pixel = P(x, y);
                int residual = pixel - (int)guess;
                int token = (int)(uint)((residual << 1) ^ (residual >> 31));
                tokens[(y * rw) + x] = token;
                if (token > local)
                {
                    local = token;
                }

                wp.Update(pixel, x, y);
            }
        }

        maxToken = local;
        return tokens;
    }

    // DecodeHistograms mirror for a single-histogram, prefix-coded, LZ77-free entropy code.
    private static void WriteHistogramsHeader(JxlBitWriter w, int numContexts, JxlPrefixCode code)
    {
        w.WriteBool(false); // lz77 disabled
        if (numContexts > 1)
        {
            // Simple context map, bits-per-entry 0 => every context maps to cluster 0 (one histogram).
            w.WriteBool(true);
            w.WriteBits(0, 2);
        }

        w.WriteBool(true); // use_prefix_code
        WriteUintConfig(w, splitExp: 15, msb: 0, lsb: 0, logAlpha: 15);
        w.WriteVarLenUint16(code.AlphabetSize - 1);
        code.WriteHeader(w);
    }

    private static void WriteUintConfig(JxlBitWriter w, int splitExp, int msb, int lsb, int logAlpha)
    {
        w.WriteBits((uint)splitExp, JxlBits.CeilLog2(logAlpha + 1));
        if (splitExp != logAlpha)
        {
            w.WriteBits((uint)msb, JxlBits.CeilLog2(splitExp + 1));
            w.WriteBits((uint)lsb, JxlBits.CeilLog2(splitExp - msb + 1));
        }
    }
}
