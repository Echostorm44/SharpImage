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
    private const int JpegMaxClusters = 64;                        // JPEG coeffs have far more distinct
                                                                   // per-band distributions than the lossy
                                                                   // path — allow more ANS histograms than
                                                                   // MaxHfClusters (context-map cost is tiny
                                                                   // vs the clustering loss it removes).
    private const int JpegDcBuckets = 8;                           // DC residual conditioned on neighbour activity

    // AC block clusters: one per component. (Conditioning the AC block context additionally on the block's DC
    // magnitude was tried and measured worse — the flat/busy split is already captured by the non-zeros
    // context, so the extra contexts only add histogram overhead.) Runtime so grayscale uses just 1 (not 3).
    private static int JpegNbc(int componentCount) => componentCount;

    private static int JpegDcContextBase(int nbc) => 495 * nbc;

    private static int JpegTotalContexts(int nbc, int componentCount) => (495 * nbc) + (componentCount * JpegDcBuckets);

    private static int ClampGradient(int left, int above, int aboveLeft)
    {
        int g = left + above - aboveLeft;
        int lo = Math.Min(left, above), hi = Math.Max(left, above);
        return Math.Clamp(g, lo, hi);
    }

    // Causal DC context: the local gradient activity predicts the residual magnitude (flat regions code
    // tighter than busy ones). left/above/aboveLeft are all previously decoded, so encoder and decoder agree.
    private static int JpegDcContext(int nbc, int ci, int left, int above, int aboveLeft)
    {
        int activity = Math.Abs(left - aboveLeft) + Math.Abs(above - aboveLeft);
        int bucket = BitLen(activity);
        if (bucket >= JpegDcBuckets)
        {
            bucket = JpegDcBuckets - 1;
        }

        return JpegDcContextBase(nbc) + (ci * JpegDcBuckets) + bucket;
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
    private static void BuildJpegTokens(Formats.JpegDctData d, int[][] orders, bool useWp, List<int> vals, List<int> ctxs)
    {
        int nbc = JpegNbc(d.ComponentCount);
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            var nonZerosGrid = new uint[bpr];
            var wp = new WpState(WpHeader.Default(), bpr);
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    // DC prediction: clamped-gradient (exact for smooth/linear planes, LZ77-friendly residuals)
                    // or the libjxl self-correcting weighted predictor (better on edges/texture) — chosen
                    // best-of per file via the transmitted useWp flag. Context stays the causal-activity bucket.
                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    int aboveRight = bx + 1 < bpr && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx + 1][0] : above;
                    int aboveAbove = by > 1 ? comp.Blocks[((by - 2) * bpr) + bx][0] : above;
                    long wpPred = wp.Predict(bx, by, above, left, aboveRight, aboveLeft, aboveAbove, null);
                    long pred = useWp ? wpPred : ClampGradient(left, above, aboveLeft);
                    ctxs.Add(JpegDcContext(nbc, ci, left, above, aboveLeft));
                    vals.Add(PackSigned((int)(block[0] - pred)));
                    wp.Update(block[0], bx, by);

                    int blockCtx = ci; // per-component AC block context

                    // AC: non-zero count (predicted from neighbours), then coefficients in zigzag order.
                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * nbc);

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
                    int coeffCtxBase = (blockCtx * 458) + (37 * nbc);
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
        // Independent choices, kept best-of: DC predictor (gradient vs weighted), scan order (custom tightens
        // the model but costs a table), and LZ77 (catches repeated token runs but adds a small header).
        byte[]? best = null;
        foreach (bool useWp in new[] { false, true })
        {
            foreach (bool useCustomOrder in new[] { true, false })
            {
                int[][] orders = useCustomOrder ? ComputeOrders(d) : NaturalOrders(d.ComponentCount);
                var vals = new List<int>();
                var ctxs = new List<int>();
                BuildJpegTokens(d, orders, useWp, vals, ctxs);
                int[] valArr = vals.ToArray();
                int[] ctxArr = ctxs.ToArray();
                byte[] plain = EncodeStream(d, orders, useCustomOrder, useWp, valArr, ctxArr, 0); // 0 = no LZ77
                if (best == null || plain.Length < best.Length)
                {
                    best = plain;
                }

                foreach (int minLen in JpegLz77MinLens)
                {
                    byte[] blob = EncodeStream(d, orders, useCustomOrder, useWp, valArr, ctxArr, minLen);
                    if (blob.Length < best.Length)
                    {
                        best = blob;
                    }
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
    private static byte[] EncodeStream(Formats.JpegDctData d, int[][] orders, bool useCustomOrder, bool useWp, int[] vals, int[] ctxs, int minLen)
    {
        var w = new JxlBitWriter();
        w.WriteBits((uint)vals.Length, 32);
        w.WriteBool(useWp);
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

        int totalContexts = JpegTotalContexts(JpegNbc(d.ComponentCount), d.ComponentCount);
        if (minLen > 0)
        {
            EncodeEntropyLz77(w, vals, ctxs, minLen, totalContexts, lazy: true);
        }
        else
        {
            EncodeEntropyPlain(w, vals, ctxs, totalContexts);
        }

        return w.ToArray();
    }

    // Candidate hybrid-uint configs (split_exponent, msb_in_token, lsb_in_token) — a subset of libjxl's
    // ChooseUintConfigs kBest set, bounded so any produced token stays < 256 (i.e. logAlpha <= 8).
    private static readonly (int S, int M, int L)[] JpegUintCandidates =
    {
        (4, 2, 0), (4, 1, 0), (4, 2, 1), (4, 2, 2), (4, 1, 2),
        (5, 2, 0), (5, 1, 0), (5, 2, 1), (5, 2, 2), (5, 1, 2),
        (3, 2, 0), (3, 1, 0), (3, 2, 1), (3, 1, 2),
        (2, 0, 1), (0, 0, 0),
        (6, 0, 0), (6, 1, 5), (6, 2, 4),
        (7, 0, 0), (8, 0, 0),
    };

    private const int JpegMaxToken = 256; // keeps logAlpha <= 8 (fits the 2-bit logAlpha-5 field)

    // Picks the hybrid-uint config minimising ANS population cost + raw extra bits + config signalling for one
    // cluster's value histogram (libjxl ChooseUintConfigs). Returns the config, its normalized token histogram
    // and the max token produced. This is the key to matching libjxl: a cluster of small coefficients can
    // direct-code them (no extra bits), one with a heavy tail can spend extra bits — instead of one fixed config.
    private static (int S, int M, int L, int[] Norm, int MaxTok) OptimizeClusterConfig(long[] valHist, int tokenCap = JpegMaxToken)
    {
        int maxVal = valHist.Length - 1;
        while (maxVal > 0 && valHist[maxVal] == 0)
        {
            maxVal--;
        }

        double best = double.MaxValue;
        (int S, int M, int L) bestCfg = (4, 2, 0);
        int[] bestNorm = new int[1];
        int bestMax = 0;
        foreach ((int s, int m, int l) in JpegUintCandidates)
        {
            int maxTok = 0;
            long extra = 0;
            bool ok = true;
            for (int v = 0; v <= maxVal; v++)
            {
                if (valHist[v] == 0)
                {
                    continue;
                }

                (int tok, int nb, _) = PackHybridFull(s, m, l, v);
                if (tok >= tokenCap)
                {
                    ok = false;
                    break;
                }

                if (tok > maxTok)
                {
                    maxTok = tok;
                }

                extra += valHist[v] * nb;
            }

            if (!ok)
            {
                continue;
            }

            var tokHist = new long[maxTok + 1];
            for (int v = 0; v <= maxVal; v++)
            {
                if (valHist[v] != 0)
                {
                    tokHist[PackHybridFull(s, m, l, v).Token] += valHist[v];
                }
            }

            // Cost = ANS coding + raw extra bits + config signalling + HISTOGRAM TRANSMISSION (else a bigger
            // token alphabet, e.g. direct coding, looks free when it actually costs more to transmit).
            int[] cand = JxlEntropy.NormalizeCounts(tokHist, JxlEntropy.HistShift);
            var probe = new JxlBitWriter();
            JxlEntropy.WriteHistogram(probe, cand, JxlEntropy.HistShift);
            double cost = NormalizedCost(tokHist) + extra + JxlBits.CeilLog2(s + 1) + JxlBits.CeilLog2(s - m + 1) + probe.BitPosition;
            if (cost < best)
            {
                best = cost;
                bestCfg = (s, m, l);
                bestNorm = cand;
                bestMax = maxTok;
            }
        }

        return (bestCfg.S, bestCfg.M, bestCfg.L, bestNorm, bestMax);
    }

    // Plain per-context ANS: cluster the contexts (on the default config), then optimise each cluster's
    // hybrid-uint config independently and emit the standard entropy header.
    private static void EncodeEntropyPlain(JxlBitWriter w, int[] vals, int[] ctxs, int totalContexts)
    {
        int n = vals.Length;
        int maxTokDefault = 0, maxVal = 0;
        var packedDefault = new int[n];
        for (int i = 0; i < n; i++)
        {
            packedDefault[i] = PackHybridFull(4, 2, 0, vals[i]).Token;
            if (packedDefault[i] > maxTokDefault)
            {
                maxTokDefault = packedDefault[i];
            }

            if (vals[i] > maxVal)
            {
                maxVal = vals[i];
            }
        }

        var ctxHist = new long[totalContexts][];
        for (int i = 0; i < totalContexts; i++)
        {
            ctxHist[i] = new long[maxTokDefault + 1];
        }

        for (int i = 0; i < n; i++)
        {
            ctxHist[ctxs[i]][packedDefault[i]]++;
        }

        (int[] map, int[][] _, int k) = ClusterContextsTotalCost(ctxHist, maxTokDefault + 1, JpegMaxClusters, Math.Max(5, BitLen(maxTokDefault)));

        // Per-cluster value histograms, then an optimal config per cluster.
        var valHist = new long[k][];
        for (int c = 0; c < k; c++)
        {
            valHist[c] = new long[maxVal + 1];
        }

        for (int i = 0; i < n; i++)
        {
            valHist[map[ctxs[i]]][vals[i]]++;
        }

        var cfgS = new int[k];
        var cfgM = new int[k];
        var cfgL = new int[k];
        var norm = new int[k][];
        int logAlpha = 5;
        for (int c = 0; c < k; c++)
        {
            (cfgS[c], cfgM[c], cfgL[c], norm[c], int mt) = OptimizeClusterConfig(valHist[c]);
            logAlpha = Math.Max(logAlpha, Math.Max(BitLen(mt), cfgS[c]));
        }

        // Entropy header: lz77, context map, use_prefix, logAlpha, per-cluster uint configs, per-cluster histograms.
        w.WriteBool(false); // lz77 disabled
        WriteContextMap(w, (int[])map.Clone(), k);
        w.WriteBool(false); // use_prefix_code = false (ANS)
        w.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(w, cfgS[c], cfgM[c], cfgL[c], logAlpha);
        }

        for (int c = 0; c < k; c++)
        {
            JxlEntropy.WriteHistogram(w, norm[c], JxlEntropy.HistShift);
        }

        var ans = new JxlAnsWriter(norm, logAlpha);
        var ansToks = new List<AnsToken>(n);
        for (int i = 0; i < n; i++)
        {
            int c = map[ctxs[i]];
            (int tok, int nb, int bits) = PackHybridFull(cfgS[c], cfgM[c], cfgL[c], vals[i]);
            ansToks.Add(new AnsToken(c, tok, (uint)bits, nb));
        }

        ans.Encode(w, ansToks);
    }

    // Lazy LZ77 over the value stream: a hash-chain longest-match search with one-step lookahead — if the
    // next position has a strictly longer match, emit a literal now and take the longer match there. This
    // avoids the greedy matcher pre-empting a long match with a shorter one, which matters on graphics.
    private static List<Op> FindMatchesLazy(int[] v, int minLen)
    {
        var ops = new List<Op>();
        int n = v.Length;
        if (minLen > n)
        {
            for (int i = 0; i < n; i++)
            {
                ops.Add(new Op(false, v[i], 0));
            }

            return ops;
        }

        const int windowMask = (1 << 20) - 1;
        var head = new Dictionary<int, int>();
        var prev = new int[Math.Max(1, n)];

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

        (int Len, int Dist) BestMatch(int idx)
        {
            int bestLen = 0, bestDist = 0;
            if (idx + 4 <= n && head.TryGetValue(Hash(idx), out int p))
            {
                int tries = 96;
                while (p >= 0 && tries-- > 0)
                {
                    int dist = idx - p;
                    if (dist > windowMask + 1)
                    {
                        break;
                    }

                    int maxl = n - idx, l = 0;
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

            return (bestLen, bestDist);
        }

        int i2 = 0;
        while (i2 < n)
        {
            (int len, int dist) = BestMatch(i2);
            if (len >= minLen)
            {
                // Lookahead: if inserting i2 and matching at i2+1 yields a strictly longer match, defer.
                Insert(i2);
                (int nlen, int ndist) = i2 + 1 < n ? BestMatch(i2 + 1) : (0, 0);
                if (nlen > len)
                {
                    ops.Add(new Op(false, v[i2], 0)); // literal at i2, take the longer match at i2+1
                    i2++;
                    (len, dist) = (nlen, ndist);
                }

                ops.Add(new Op(true, len, dist));
                int end = i2 + len;
                for (int j = i2 + 1; j < end; j++)
                {
                    Insert(j);
                }

                i2 = end;
            }
            else
            {
                ops.Add(new Op(false, v[i2], 0));
                Insert(i2);
                i2++;
            }
        }

        return ops;
    }

    // ANS with LZ77 back-references over the value stream. Length markers live in the literal alphabet above
    // `threshold` (coded in the position's own context); distances use a dedicated extra cluster. The DECODER
    // (JxlAnsReader) handles the copies transparently, so DecodeJpegCoefficients needs no changes.
    private static void EncodeEntropyLz77(JxlBitWriter w, int[] vals, int[] ctxs, int minLen, int totalContexts, bool lazy)
    {
        List<Op> ops = lazy ? FindMatchesLazy(vals, minLen) : FindMatches(vals, minLen);

        // --- Cluster on the default config (4,2,0) + a default threshold ---
        int maxLitDefault = 0;
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                maxLitDefault = Math.Max(maxLitDefault, PackHybridFull(4, 2, 0, op.A).Token);
            }
        }

        int thrDefault = Math.Max(8, maxLitDefault + 1);
        int gmaxDefault = maxLitDefault;
        foreach (Op op in ops)
        {
            if (op.Match)
            {
                gmaxDefault = Math.Max(gmaxDefault, thrDefault + PackHybridUint(JpegSeLen, op.A - minLen).Token);
            }
        }

        var ctxHist = new long[totalContexts][];
        for (int i = 0; i < totalContexts; i++)
        {
            ctxHist[i] = new long[gmaxDefault + 1];
        }

        int pos = 0;
        foreach (Op op in ops)
        {
            int ctx = ctxs[pos];
            if (!op.Match)
            {
                ctxHist[ctx][PackHybridFull(4, 2, 0, op.A).Token]++;
                pos += 1;
            }
            else
            {
                ctxHist[ctx][thrDefault + PackHybridUint(JpegSeLen, op.A - minLen).Token]++;
                pos += op.A;
            }
        }

        (int[] map, int[][] _, int k) = ClusterContextsTotalCost(ctxHist, gmaxDefault + 1, JpegMaxClusters, Math.Max(5, BitLen(gmaxDefault)));

        // --- Per-cluster literal value histograms, then an optimal config per cluster (capped so the length
        //     markers still fit below 256 => logAlpha <= 8) ---
        int maxVal = 0;
        foreach (Op op in ops)
        {
            if (!op.Match && op.A > maxVal)
            {
                maxVal = op.A;
            }
        }

        var litHist = new long[k][];
        for (int c = 0; c < k; c++)
        {
            litHist[c] = new long[maxVal + 1];
        }

        pos = 0;
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                litHist[map[ctxs[pos]]][op.A]++;
                pos += 1;
            }
            else
            {
                pos += op.A;
            }
        }

        var cfgS = new int[k];
        var cfgM = new int[k];
        var cfgL = new int[k];
        int threshold = 8;
        for (int c = 0; c < k; c++)
        {
            (cfgS[c], cfgM[c], cfgL[c], int[] _2, int mt) = OptimizeClusterConfig(litHist[c], 220);
            threshold = Math.Max(threshold, mt + 1);
        }

        // --- Build per-cluster token histograms (literals via cfg + length markers) + distance histogram ---
        int gmaxLit = threshold - 1, gmaxDist = 0;
        foreach (Op op in ops)
        {
            if (op.Match)
            {
                gmaxLit = Math.Max(gmaxLit, threshold + PackHybridUint(JpegSeLen, op.A - minLen).Token);
                gmaxDist = Math.Max(gmaxDist, PackHybridUint(JpegSeDist, op.B - 1).Token);
            }
        }

        var tokHist = new long[k][];
        for (int c = 0; c < k; c++)
        {
            tokHist[c] = new long[gmaxLit + 1];
        }

        var distHist = new long[gmaxDist + 1];
        pos = 0;
        foreach (Op op in ops)
        {
            int cl = map[ctxs[pos]];
            if (!op.Match)
            {
                tokHist[cl][PackHybridFull(cfgS[cl], cfgM[cl], cfgL[cl], op.A).Token]++;
                pos += 1;
            }
            else
            {
                tokHist[cl][threshold + PackHybridUint(JpegSeLen, op.A - minLen).Token]++;
                distHist[PackHybridUint(JpegSeDist, op.B - 1).Token]++;
                pos += op.A;
            }
        }

        int logAlpha = Math.Max(5, Math.Max(BitLen(gmaxLit), BitLen(gmaxDist)));
        for (int c = 0; c < k; c++)
        {
            logAlpha = Math.Max(logAlpha, cfgS[c]);
        }

        var norm = new int[k + 1][];
        for (int c = 0; c < k; c++)
        {
            norm[c] = JxlEntropy.NormalizeCounts(tokHist[c], JxlEntropy.HistShift);
        }

        norm[k] = JxlEntropy.NormalizeCounts(distHist, JxlEntropy.HistShift);

        var combinedMap = new int[totalContexts + 1];
        Array.Copy(map, combinedMap, totalContexts);
        combinedMap[totalContexts] = k;

        w.WriteBool(true); // lz77 enabled
        w.WriteU32((uint)threshold, E.Val(224), E.Val(512), E.Val(4096), E.BitsOff(15, 8)); // min_symbol
        w.WriteU32((uint)minLen, E.Val(3), E.Val(4), E.BitsOff(2, 5), E.BitsOff(8, 9));    // min_length
        WriteUintConfig(w, JpegSeLen, 0, 0, 8);            // lz77 length config
        WriteContextMap(w, combinedMap, k + 1);
        w.WriteBool(false); // use_prefix_code = false (ANS)
        w.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(w, cfgS[c], cfgM[c], cfgL[c], logAlpha); // literal+length clusters
        }

        WriteUintConfig(w, JpegSeDist, 0, 0, logAlpha); // distance cluster
        for (int c = 0; c <= k; c++)
        {
            JxlEntropy.WriteHistogram(w, norm[c], JxlEntropy.HistShift);
        }

        var ans = new JxlAnsWriter(norm, logAlpha);
        var ansToks = new List<AnsToken>();
        pos = 0;
        foreach (Op op in ops)
        {
            int cl = map[ctxs[pos]];
            if (!op.Match)
            {
                (int tok, int nb, int bits) = PackHybridFull(cfgS[cl], cfgM[cl], cfgL[cl], op.A);
                ansToks.Add(new AnsToken(cl, tok, (uint)bits, nb));
                pos += 1;
            }
            else
            {
                (int lt, int ln, int lb) = PackHybridUint(JpegSeLen, op.A - minLen);
                ansToks.Add(new AnsToken(cl, threshold + lt, (uint)lb, ln));
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
        bool useWp = br.ReadBool();
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

        int nbc = JpegNbc(d.ComponentCount);
        // DecodeHistograms reads the whole entropy header (lz77, context map, use_prefix, logAlpha, configs, histograms).
        JxlAnsCode code = JxlEntropy.DecodeHistograms(JpegTotalContexts(nbc, d.ComponentCount), br);
        var rd = new JxlAnsReader(code, br);

        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            comp.Blocks = new int[bpr * bpc][];
            for (int i = 0; i < comp.Blocks.Length; i++)
            {
                comp.Blocks[i] = new int[64];
            }

            var nonZerosGrid = new uint[bpr];
            var wp = new WpState(WpHeader.Default(), bpr);
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    int aboveRight = bx + 1 < bpr && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx + 1][0] : above;
                    int aboveAbove = by > 1 ? comp.Blocks[((by - 2) * bpr) + bx][0] : above;
                    long wpPred = wp.Predict(bx, by, above, left, aboveRight, aboveLeft, aboveAbove, null);
                    long pred = useWp ? wpPred : ClampGradient(left, above, aboveLeft);
                    int dcRes = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(JpegDcContext(nbc, ci, left, above, aboveLeft)));
                    block[0] = (int)(dcRes + pred);
                    wp.Update(block[0], bx, by);
                    int blockCtx = ci;

                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * nbc);
                    int nonZeros = (int)rd.ReadHybridUintCtx(nonZerosCtx);
                    nonZerosGrid[bx] = (uint)nonZeros;
                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * nbc);
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
