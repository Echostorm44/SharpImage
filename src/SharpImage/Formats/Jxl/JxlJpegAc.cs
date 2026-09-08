// DCT-aware entropy coder for JPEG-recompression coefficients. Reuses the lossy VarDCT AC context model
// (non-zero-count prediction from neighbouring blocks, per-frequency-band coefficient contexts) and the ANS
// entropy machinery to code a JPEG's quantized coefficients far tighter than JPEG's per-block Huffman. DC is
// clamped-gradient predicted; AC is walked in zigzag (frequency) order. Both directions are implemented here
// and controlled entirely by this repo, so byte-exact reconstruction is guaranteed by JpegCoder.RebuildJpeg.
using System;
using System.Collections.Generic;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

internal static partial class JxlEncoder
{
    private const int JpegBlockClusters = 3;                       // one AC block-context per component
    private const int JpegMaxClusters = 64;                        // JPEG coeffs have far more distinct
                                                                   // per-band distributions than the lossy
                                                                   // path — allow more ANS histograms than
                                                                   // MaxHfClusters (context-map cost is tiny
                                                                   // vs the clustering loss it removes).
    private const int JpegAcContexts = 495 * JpegBlockClusters;    // 1485, matches the VarDCT context layout
    private const int JpegDcBuckets = 8;                           // DC residual conditioned on neighbour activity
    private const int JpegDcContextBase = JpegAcContexts;          // DC contexts follow the AC ones
    private const int JpegTotalContexts = JpegDcContextBase + (JpegBlockClusters * JpegDcBuckets);

    private static int ClampGradient(int left, int above, int aboveLeft)
    {
        int g = left + above - aboveLeft;
        int lo = Math.Min(left, above), hi = Math.Max(left, above);
        return Math.Clamp(g, lo, hi);
    }

    // Causal DC context: the local gradient activity predicts the residual magnitude (flat regions code
    // tighter than busy ones). left/above/aboveLeft are all previously decoded, so encoder and decoder agree.
    private static int JpegDcContext(int ci, int left, int above, int aboveLeft)
    {
        int activity = Math.Abs(left - aboveLeft) + Math.Abs(above - aboveLeft);
        int bucket = BitLen(activity);
        if (bucket >= JpegDcBuckets)
        {
            bucket = JpegDcBuckets - 1;
        }

        return JpegDcContextBase + (ci * JpegDcBuckets) + bucket;
    }

    // Per-component AC scan order: position 0 is DC; positions 1..63 are the natural-order coefficient
    // indices sorted by descending nonzero frequency across the component's blocks. Visiting the most-often-
    // nonzero positions first makes the running-nonzeros count fall predictably, tightening the coeff model.
    private static int[][] ComputeOrders(Formats.JpegDctData d)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var orders = new int[d.ComponentCount][];
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            var nzCount = new long[64];
            foreach (int[] block in comp.Blocks)
            {
                for (int oi = 1; oi < 64; oi++)
                {
                    if (block[zz[oi]] != 0)
                    {
                        nzCount[oi]++;
                    }
                }
            }

            // Stable descending sort of AC scan positions 1..63 by nonzero count (ties keep zigzag order).
            var slots = new int[63];
            for (int i = 0; i < 63; i++)
            {
                slots[i] = i + 1;
            }

            Array.Sort(slots, (a, b) => nzCount[a] != nzCount[b] ? nzCount[b].CompareTo(nzCount[a]) : a.CompareTo(b));

            var order = new int[64];
            order[0] = 0; // DC
            for (int i = 0; i < 63; i++)
            {
                order[i + 1] = zz[slots[i]]; // natural-order index visited at scan step i+1
            }

            orders[ci] = order;
        }

        return orders;
    }

    // Builds the raw-value + per-position-context stream for one baseline JPEG's coefficients (all
    // components). Values are the pre-hybrid unsigned integers (PackSigned for signed coeffs/DC residuals,
    // the count for non-zeros); the caller hybrid-packs them (and optionally LZ77s the value stream).
    private static void BuildJpegTokens(Formats.JpegDctData d, int[][] orders, List<int> vals, List<int> ctxs)
    {
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            int blockCtx = ci;
            var nonZerosGrid = new uint[bpr];
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    // DC: clamped-gradient prediction over the block grid (the DC plane is smooth).
                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    ctxs.Add(JpegDcContext(ci, left, above, aboveLeft));
                    vals.Add(PackSigned(block[0] - ClampGradient(left, above, aboveLeft)));

                    // AC: non-zero count (predicted from neighbours), then coefficients in zigzag order.
                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * JpegBlockClusters);

                    int nonZeros = 0, lastNz = 0;
                    for (int oi = 1; oi < 64; oi++)
                    {
                        if (block[order[oi]] != 0)
                        {
                            nonZeros++;
                            lastNz = oi;
                        }
                    }

                    ctxs.Add(nonZerosCtx);
                    vals.Add(nonZeros);
                    nonZerosGrid[bx] = (uint)nonZeros;
                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * JpegBlockClusters);
                    int rem = nonZeros;
                    for (int oi = 1; oi <= lastNz; oi++)
                    {
                        int fidx = oi - 1;
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[rem - 1] + CoeffFreqContext[fidx]) * 2) + isPrevNonzero);
                        ctxs.Add(coeffCtxBase + coeffCtx);
                        int q = block[order[oi]];
                        if (q == 0)
                        {
                            vals.Add(0);
                            isPrevNonzero = 0;
                            continue;
                        }

                        vals.Add(PackSigned(q));
                        isPrevNonzero = 1;
                        if (--rem == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    // Candidate LZ77 minimum match lengths tried in the best-of. The greedy matcher favours longer minimums
    // (short matches inflate the histograms and pre-empt better long matches), and 16 vs 32 win on different
    // content, so try both plus no-LZ77 and keep the smallest.
    private static readonly int[] JpegLz77MinLens = { 16, 32 };
    private const int JpegSeLen = 4;          // hybrid config split for LZ77 length
    private const int JpegSeDist = 4;         // hybrid config split for LZ77 distance

    /// <summary>Entropy-codes a JPEG's quantized DCT coefficients with the DCT-aware context model + ANS.</summary>
    internal static byte[] EncodeJpegCoefficients(Formats.JpegDctData d)
    {
        // Two independent choices, kept best-of: the scan order (custom tightens the model but costs a
        // transmitted table) and LZ77 (catches repeated token runs in graphics but adds a small header).
        byte[]? best = null;
        foreach (bool useCustomOrder in new[] { true, false })
        {
            int[][] orders = useCustomOrder ? ComputeOrders(d) : NaturalOrders(d.ComponentCount);
            var vals = new List<int>();
            var ctxs = new List<int>();
            BuildJpegTokens(d, orders, vals, ctxs);
            int[] valArr = vals.ToArray();
            int[] ctxArr = ctxs.ToArray();
            byte[] plain = EncodeStream(d, orders, useCustomOrder, valArr, ctxArr, 0); // 0 = no LZ77
            if (best == null || plain.Length < best.Length)
            {
                best = plain;
            }

            foreach (int minLen in JpegLz77MinLens)
            {
                byte[] blob = EncodeStream(d, orders, useCustomOrder, valArr, ctxArr, minLen);
                if (blob.Length < best.Length)
                {
                    best = blob;
                }
            }
        }

        return best!;
    }

    private static int[][] NaturalOrders(int componentCount)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var orders = new int[componentCount][];
        for (int ci = 0; ci < componentCount; ci++)
        {
            var order = new int[64];
            for (int oi = 0; oi < 64; oi++)
            {
                order[oi] = zz[oi];
            }

            orders[ci] = order;
        }

        return orders;
    }

    // Writes the container prefix (token count + optional scan-order table), then the entropy-coded value
    // stream — either plain per-context ANS or ANS with LZ77 back-references over the value stream.
    private static byte[] EncodeStream(Formats.JpegDctData d, int[][] orders, bool useCustomOrder, int[] vals, int[] ctxs, int minLen)
    {
        var w = new JxlBitWriter();
        w.WriteBits((uint)vals.Length, 32);
        w.WriteBool(useCustomOrder);
        if (useCustomOrder)
        {
            // Per-component AC scan orders (63 natural-order indices x 6 bits), needed to reverse the reorder.
            for (int ci = 0; ci < d.ComponentCount; ci++)
            {
                for (int oi = 1; oi < 64; oi++)
                {
                    w.WriteBits((uint)orders[ci][oi], 6);
                }
            }
        }

        if (minLen > 0)
        {
            EncodeEntropyLz77(w, vals, ctxs, minLen);
        }
        else
        {
            EncodeEntropyPlain(w, vals, ctxs);
        }

        return w.ToArray();
    }

    // Plain per-context ANS: hybrid-pack each value, cluster the contexts, emit the standard entropy header.
    private static void EncodeEntropyPlain(JxlBitWriter w, int[] vals, int[] ctxs)
    {
        int n = vals.Length;
        var packed = new (int Tok, int NBits, int Bits)[n];
        int maxTok = 0;
        for (int i = 0; i < n; i++)
        {
            packed[i] = PackHybridFull(LitSplit, LitMsb, LitLsb, vals[i]);
            if (packed[i].Tok > maxTok)
            {
                maxTok = packed[i].Tok;
            }
        }

        int alphabet = maxTok + 1;
        int logAlpha = Math.Max(5, BitLen(maxTok));
        var ctxHist = new long[JpegTotalContexts][];
        for (int i = 0; i < JpegTotalContexts; i++)
        {
            ctxHist[i] = new long[alphabet];
        }

        for (int i = 0; i < n; i++)
        {
            ctxHist[ctxs[i]][packed[i].Tok]++;
        }

        (int[] map, int[][] norm, int k) = ClusterContextsTotalCost(ctxHist, alphabet, JpegMaxClusters, logAlpha);

        // Entropy header, byte-for-byte the layout DecodeHistograms consumes: lz77, context map,
        // use_prefix, logAlpha, per-cluster uint configs, per-cluster histograms.
        w.WriteBool(false); // lz77 disabled
        WriteContextMap(w, (int[])map.Clone(), k);
        w.WriteBool(false); // use_prefix_code = false (ANS)
        w.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(w, LitSplit, LitMsb, LitLsb, logAlpha);
        }

        for (int c = 0; c < k; c++)
        {
            JxlEntropy.WriteHistogram(w, norm[c], JxlEntropy.HistShift);
        }

        var ans = new JxlAnsWriter(norm, logAlpha);
        var ansToks = new List<AnsToken>(n);
        for (int i = 0; i < n; i++)
        {
            ansToks.Add(new AnsToken(map[ctxs[i]], packed[i].Tok, (uint)packed[i].Bits, packed[i].NBits));
        }

        ans.Encode(w, ansToks);
    }

    // ANS with LZ77 back-references over the value stream. Length markers live in the literal alphabet above
    // `threshold` (coded in the position's own context); distances use a dedicated extra cluster. The DECODER
    // (JxlAnsReader) handles the copies transparently, so DecodeJpegCoefficients needs no changes.
    private static void EncodeEntropyLz77(JxlBitWriter w, int[] vals, int[] ctxs, int minLen)
    {
        List<Op> ops = FindMatches(vals, minLen);

        int maxLitTok = 0;
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                maxLitTok = Math.Max(maxLitTok, PackHybridFull(LitSplit, LitMsb, LitLsb, op.A).Token);
            }
        }

        int threshold = Math.Max(8, maxLitTok + 1);
        int gmaxLit = maxLitTok, gmaxDist = 0;
        foreach (Op op in ops)
        {
            if (op.Match)
            {
                gmaxLit = Math.Max(gmaxLit, threshold + PackHybridUint(JpegSeLen, op.A - minLen).Token);
                gmaxDist = Math.Max(gmaxDist, PackHybridUint(JpegSeDist, op.B - 1).Token);
            }
        }

        int logAlpha = Math.Max(5, BitLen(Math.Max(gmaxLit, gmaxDist)));
        var ctxHist = new long[JpegTotalContexts][];
        for (int i = 0; i < JpegTotalContexts; i++)
        {
            ctxHist[i] = new long[gmaxLit + 1];
        }

        var distHist = new long[gmaxDist + 1];
        int pos = 0;
        foreach (Op op in ops)
        {
            int ctx = ctxs[pos];
            if (!op.Match)
            {
                ctxHist[ctx][PackHybridFull(LitSplit, LitMsb, LitLsb, op.A).Token]++;
                pos += 1;
            }
            else
            {
                ctxHist[ctx][threshold + PackHybridUint(JpegSeLen, op.A - minLen).Token]++;
                distHist[PackHybridUint(JpegSeDist, op.B - 1).Token]++;
                pos += op.A;
            }
        }

        (int[] map, int[][] norm, int k) = ClusterContextsTotalCost(ctxHist, gmaxLit + 1, JpegMaxClusters, logAlpha);

        // Combined context map: N literal contexts -> their cluster, plus a final distance context -> cluster k.
        var combinedMap = new int[JpegTotalContexts + 1];
        Array.Copy(map, combinedMap, JpegTotalContexts);
        combinedMap[JpegTotalContexts] = k;

        int[] distNorm = JxlEntropy.NormalizeCounts(distHist, JxlEntropy.HistShift);
        var norm2 = new int[k + 1][];
        Array.Copy(norm, norm2, k);
        norm2[k] = distNorm;

        w.WriteBool(true); // lz77 enabled
        w.WriteU32((uint)threshold, E.Val(224), E.Val(512), E.Val(4096), E.BitsOff(15, 8)); // min_symbol
        w.WriteU32((uint)minLen, E.Val(3), E.Val(4), E.BitsOff(2, 5), E.BitsOff(8, 9));    // min_length
        WriteUintConfig(w, JpegSeLen, 0, 0, 8);            // lz77 length config
        WriteContextMap(w, combinedMap, k + 1);
        w.WriteBool(false); // use_prefix_code = false (ANS)
        w.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(w, LitSplit, LitMsb, LitLsb, logAlpha); // literal+length clusters
        }

        WriteUintConfig(w, JpegSeDist, 0, 0, logAlpha); // distance cluster
        for (int c = 0; c <= k; c++)
        {
            JxlEntropy.WriteHistogram(w, norm2[c], JxlEntropy.HistShift);
        }

        var ans = new JxlAnsWriter(norm2, logAlpha);
        var ansToks = new List<AnsToken>();
        pos = 0;
        foreach (Op op in ops)
        {
            int ctx = ctxs[pos];
            if (!op.Match)
            {
                (int tok, int nb, int bits) = PackHybridFull(LitSplit, LitMsb, LitLsb, op.A);
                ansToks.Add(new AnsToken(map[ctx], tok, (uint)bits, nb));
                pos += 1;
            }
            else
            {
                (int lt, int ln, int lb) = PackHybridUint(JpegSeLen, op.A - minLen);
                ansToks.Add(new AnsToken(map[ctx], threshold + lt, (uint)lb, ln));
                (int dt, int dn, int db) = PackHybridUint(JpegSeDist, op.B - 1);
                ansToks.Add(new AnsToken(k, dt, (uint)db, dn));
                pos += op.A;
            }
        }

        ans.Encode(w, ansToks);
    }

    /// <summary>Decodes the coefficients back into d.Components[*].Blocks (BlocksPerRow/Col must be set).</summary>
    internal static void DecodeJpegCoefficients(byte[] data, Formats.JpegDctData d)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var br = new JxlBitReader(data);
        int tokenCount = (int)br.ReadBits(32);
        _ = tokenCount; // blocks are walked by structure; the count is a stored sanity value only
        bool useCustomOrder = br.ReadBool();
        var orders = new int[d.ComponentCount][];
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            var order = new int[64];
            order[0] = 0;
            for (int oi = 1; oi < 64; oi++)
            {
                order[oi] = useCustomOrder ? (int)br.ReadBits(6) : zz[oi];
            }

            orders[ci] = order;
        }

        // DecodeHistograms reads the whole entropy header (lz77, context map, use_prefix, logAlpha, configs, histograms).
        JxlAnsCode code = JxlEntropy.DecodeHistograms(JpegTotalContexts, br);
        var rd = new JxlAnsReader(code, br);

        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            int blockCtx = ci;
            comp.Blocks = new int[bpr * bpc][];
            for (int i = 0; i < comp.Blocks.Length; i++)
            {
                comp.Blocks[i] = new int[64];
            }

            var nonZerosGrid = new uint[bpr];
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    int dcRes = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(JpegDcContext(ci, left, above, aboveLeft)));
                    block[0] = dcRes + ClampGradient(left, above, aboveLeft);

                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * JpegBlockClusters);
                    int nonZeros = (int)rd.ReadHybridUintCtx(nonZerosCtx);
                    nonZerosGrid[bx] = (uint)nonZeros;
                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * JpegBlockClusters);
                    int rem = nonZeros;
                    for (int oi = 1; oi < 64; oi++)
                    {
                        int fidx = oi - 1;
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[rem - 1] + CoeffFreqContext[fidx]) * 2) + isPrevNonzero);
                        int val = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(coeffCtxBase + coeffCtx));
                        if (val == 0)
                        {
                            isPrevNonzero = 0;
                            continue;
                        }

                        block[order[oi]] = val;
                        isPrevNonzero = 1;
                        if (--rem == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
}
