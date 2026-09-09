// DCT-aware entropy coder for JPEG-recompression coefficients. Reuses the lossy VarDCT AC context model
// (non-zero-count prediction from neighbouring blocks, per-frequency-band coefficient contexts) and the ANS
// entropy machinery to code a JPEG's quantized coefficients far tighter than JPEG's per-block Huffman. DC is
// clamped-gradient predicted; AC is walked in zigzag (frequency) order. Both directions are implemented here
// and controlled entirely by this repo, so byte-exact reconstruction is guaranteed by JpegCoder.RebuildJpeg.
using System;
using System.Collections.Generic;
using System.Linq;
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

    // AC block clusters: one per component. (Conditioning the AC coefficient context additionally on a causal
    // DC-activity bucket sharpens the raw per-context distributions — measured -2.7KB zeroth-order on the
    // configure screenshot — but the 2-4x context explosion costs more in clustering + LZ77 context-map and
    // histogram overhead than it saves, a net regression at every bucket count tried. Kept at 1/component.)
    private static int JpegNbc(int componentCount) => componentCount;

    private static int JpegDcContextBase(int nbc) => 495 * nbc;

    private static int JpegTotalContexts(int nbc, int componentCount) => (495 * nbc) + (componentCount * JpegDcBuckets);

    private static int ClampGradient(int left, int above, int aboveLeft)
    {
        int g = left + above - aboveLeft;
        int lo = Math.Min(left, above), hi = Math.Max(left, above);
        return Math.Clamp(g, lo, hi);
    }

    // Causal DC context: a bucket of the local predictor-error magnitude (flat regions code tighter than busy
    // ones). `ctxMag` is the gradient activity (gradient predictor) or the WP self-correcting error property
    // (weighted predictor) — both computed from previously-decoded neighbours, so encoder and decoder agree.
    private static int JpegDcContext(int nbc, int ci, long ctxMag)
    {
        long m = ctxMag < 0 ? -ctxMag : ctxMag;
        int bucket = BitLen(m > int.MaxValue ? int.MaxValue : (int)m);
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
            var dcProps = new List<long>(1);
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
                    dcProps.Clear();
                    long wpPred = wp.Predict(bx, by, above, left, aboveRight, aboveLeft, aboveAbove, useWp ? dcProps : null);
                    long pred = useWp ? wpPred : ClampGradient(left, above, aboveLeft);
                    long ctxMag = useWp ? dcProps[0] : (Math.Abs(left - aboveLeft) + Math.Abs(above - aboveLeft));
                    ctxs.Add(JpegDcContext(nbc, ci, ctxMag));
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

    // Encode effort presets. Higher = try more candidate encodings (predictor / scan order / LZ77 length /
    // entropy backend / refinement iterations), keeping the smallest — better ratio, more CPU. The best-of is
    // pure ratio search: every tier is byte-exact (the container self-verifies), tiers only trade speed↔size.
    internal const int JpegEffortFast = 2;      // ~single-pass: gradient DC, custom order, ANS, minimal refine
    internal const int JpegEffortDefault = 5;   // balanced: both orders + {no-LZ77, LZ77} best-of, ANS
    internal const int JpegEffortMax = 9;       // exhaustive: + weighted-DC predictor, +LZ77-32, +prefix codes

    // Phase profiler (env JPEGAC_PROF): accumulates CPU-ticks per named phase across all parallel candidates.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> ProfTicks = new();
    private static readonly bool ProfOn = Environment.GetEnvironmentVariable("JPEGAC_PROF") != null;

    private static long ProfStart() => ProfOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    private static void ProfEnd(string phase, long start)
    {
        if (ProfOn)
        {
            ProfTicks.AddOrUpdate(phase, System.Diagnostics.Stopwatch.GetTimestamp() - start, (_, v) => v + System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>Entropy-codes a JPEG's quantized DCT coefficients with the DCT-aware context model + ANS.</summary>
    internal static byte[] EncodeJpegCoefficients(Formats.JpegDctData d, int effort = JpegEffortDefault)
    {
        // Candidate breadth scales with effort. The per-encode cost is dominated by the clustering + per-cluster
        // config refinement, so the biggest speed lever is simply how many (predictor × order × lz77 × backend)
        // encodings we run — libjxl does one tuned pass; we approximate its config choice by trying a few.
        // Both scan orders are always tried: natural (no table) is essential for tiny files, custom for large —
        // dropping either badly hurts a whole content class for little speed. Likewise {no-LZ77, LZ77-16} is the
        // floor (LZ77 is what crushes graphics/screenshots). Effort scales the knobs with diminishing payoff:
        // the weighted-DC predictor, a second LZ77 length, prefix-code candidates, and refinement iterations.
        bool[] predictors = effort >= 7 ? new[] { false, true } : new[] { false };
        bool[] orderOpts = { true, false };
        int[] lzModes = effort >= 7 ? new[] { 0, 16, 32 } : new[] { 0, 16 };
        bool tryPrefix = effort >= 7;
        int refineIters = effort >= 7 ? 5 : (effort >= 4 ? 3 : 1);

        // Collect every independent candidate encoding as a job, then run them in parallel — the best-of is a
        // pure size race with no shared mutable state, so this is an exact-ratio speedup that scales with cores
        // (the dominant cost is the per-candidate clustering + ANS encode, not the token build). Token streams
        // are built once per (predictor, order) and shared read-only by that group's LZ77/backend candidates.
        var jobs = new List<(int[][] orders, bool custom, bool wp, int[] vals, int[] ctxs, int minLen, bool prefix)>();
        foreach (bool useWp in predictors)
        {
            foreach (bool useCustomOrder in orderOpts)
            {
                long tB = ProfStart();
                int[][] orders = useCustomOrder ? ComputeOrders(d) : NaturalOrders(d.ComponentCount);
                var vals = new List<int>();
                var ctxs = new List<int>();
                BuildJpegTokens(d, orders, useWp, vals, ctxs);
                int[] valArr = vals.ToArray();
                int[] ctxArr = ctxs.ToArray();
                ProfEnd("buildtokens", tB);
                if (!useWp && useCustomOrder && Environment.GetEnvironmentVariable("JPEGAC_STATS") != null)
                {
                    JpegStats(d, valArr, ctxArr);
                }

                // On a LARGE, clearly non-repetitive stream (a photograph) LZ77 cannot win the best-of, but its
                // match search is the single most expensive phase — so skip the LZ77 candidates there. Only
                // gate large streams, where the probe is reliable and the saving is real; small ones always try
                // LZ77 (it is cheap and the probe is noisy on little data). Never changes the winning size.
                bool tryLz = valArr.Length <= 50000 || LikelyRepetitive(valArr);
                foreach (int minLen in lzModes)
                {
                    if (minLen > 0 && !tryLz)
                    {
                        continue;
                    }

                    jobs.Add((orders, useCustomOrder, useWp, valArr, ctxArr, minLen, false));
                    if (tryPrefix)
                    {
                        jobs.Add((orders, useCustomOrder, useWp, valArr, ctxArr, minLen, true));
                    }
                }
            }
        }

        byte[]? best = null;
        if (jobs.Count == 1)
        {
            var j = jobs[0];
            best = EncodeStream(d, j.orders, j.custom, j.wp, j.vals, j.ctxs, j.minLen, refineIters, j.prefix);
        }
        else
        {
            var blobs = new byte[jobs.Count][];
            System.Threading.Tasks.Parallel.For(0, jobs.Count, i =>
            {
                var j = jobs[i];
                blobs[i] = EncodeStream(d, j.orders, j.custom, j.wp, j.vals, j.ctxs, j.minLen, refineIters, j.prefix);
            });

            int bi = 0;
            for (int i = 0; i < blobs.Length; i++)
            {
                if (best == null || blobs[i].Length < best.Length)
                {
                    best = blobs[i];
                    bi = i;
                }
            }

            if (ProfOn)
            {
                var jw = jobs[bi];
                Console.Error.WriteLine($"  PROF winner: wp={jw.wp} custom={jw.custom} lz77={jw.minLen} prefix={jw.prefix} size={best!.Length}");
            }
        }

        if (ProfOn)
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            foreach (var kv in ProfTicks.OrderByDescending(x => x.Value))
            {
                Console.Error.WriteLine($"  PROF {kv.Key,-12} {kv.Value * f,9:F0} ms (CPU, summed over parallel candidates)");
            }

            ProfTicks.Clear();
        }

        return best!;
    }

    // Diagnostic: context-conditioned zeroth-order entropy split by token category (DC residual, AC nonzero
    // count, AC coefficients) plus the raw extra-bits mantissa. Approximates the no-LZ77 ANS coded size and
    // shows where the bytes live. Gated on JPEGAC_STATS.
    private static void JpegStats(Formats.JpegDctData d, int[] vals, int[] ctxs)
    {
        int nbc = JpegNbc(d.ComponentCount);
        int dcBase = JpegDcContextBase(nbc), countMax = 37 * nbc;
        int total = JpegTotalContexts(nbc, d.ComponentCount);
        var hist = new Dictionary<int, long>[total];
        var extra = new double[3];
        var toks = new long[3];
        int Cat(int c) => c >= dcBase ? 0 : (c < countMax ? 1 : 2);
        for (int i = 0; i < vals.Length; i++)
        {
            (int tok, int nb, int _) = PackHybridFull(4, 2, 0, vals[i]);
            int cat = Cat(ctxs[i]);
            extra[cat] += nb;
            toks[cat]++;
            (hist[ctxs[i]] ??= new Dictionary<int, long>()).TryGetValue(tok, out long cur);
            hist[ctxs[i]][tok] = cur + 1;
        }

        var symBits = new double[3];
        for (int c = 0; c < total; c++)
        {
            if (hist[c] == null)
            {
                continue;
            }

            long tot = 0;
            foreach (long v in hist[c].Values)
            {
                tot += v;
            }

            int cat = Cat(c);
            foreach (long v in hist[c].Values)
            {
                symBits[cat] += -v * Math.Log((double)v / tot, 2);
            }
        }

        string[] name = { "DC     ", "ACcount", "ACcoeff" };
        double gt = 0;
        for (int cat = 0; cat < 3; cat++)
        {
            double bytes = (symBits[cat] + extra[cat]) / 8;
            gt += bytes;
            Console.Error.WriteLine($"  {name[cat]}: toks={toks[cat],8}  sym={symBits[cat] / 8,9:F0}B  extra={extra[cat] / 8,9:F0}B  total={bytes,9:F0}B");
        }

        Console.Error.WriteLine($"  (zeroth-order no-LZ77 estimate total = {gt:F0}B)");
    }

    // Cheap repetition probe: sample 4-token windows across the stream and measure how many hash to a bucket
    // already seen. A photograph's coefficient stream is near-unique (low rate); graphics/screenshots repeat
    // (glyphs, borders, flat runs). Deliberately conservative — the threshold is set low so anything that
    // MIGHT benefit from LZ77 still runs it; it only prunes the clearly-incompressible-by-LZ77 case.
    private static bool LikelyRepetitive(int[] v)
    {
        int n = v.Length;
        if (n < 8)
        {
            return true;
        }

        const int tableBits = 16;
        var seen = new bool[1 << tableBits];
        int samples = 0, hits = 0;
        int stride = Math.Max(1, (n - 3) / 8192); // ~8k samples regardless of size
        for (int i = 0; i + 3 < n; i += stride)
        {
            unchecked
            {
                uint h = (uint)v[i];
                h = (h * 2654435761u) + (uint)v[i + 1];
                h = (h * 2654435761u) + (uint)v[i + 2];
                h = (h * 2654435761u) + (uint)v[i + 3];
                int slot = (int)((h * 2654435761u) >> (32 - tableBits));
                if (seen[slot])
                {
                    hits++;
                }
                else
                {
                    seen[slot] = true;
                }

                samples++;
            }
        }

        // >~4% of sampled windows recurring is well above the collision floor of near-unique data.
        return samples > 0 && hits * 100L >= samples * 4L;
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
    private static byte[] EncodeStream(Formats.JpegDctData d, int[][] orders, bool useCustomOrder, bool useWp, int[] vals, int[] ctxs, int minLen, int refineIters, bool usePrefix = false)
    {
        var w = new JxlBitWriter();
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
            EncodeEntropyLz77(w, vals, ctxs, minLen, totalContexts, lazy: true, refineIters, usePrefix);
        }
        else
        {
            EncodeEntropyPlain(w, vals, ctxs, totalContexts, refineIters, usePrefix);
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

    // A curated subset of the candidates that win the vast majority of the time on Laplacian JPEG coefficient
    // clusters — used at low effort to cut OptimizeClusterConfig's inner search ~3x for a negligible ratio cost.
    private static readonly (int S, int M, int L)[] JpegUintCandidatesFast =
    {
        (4, 2, 0), (4, 1, 0), (3, 2, 0), (5, 2, 0), (4, 2, 1), (0, 0, 0),
    };

    private const int JpegMaxToken = 256; // keeps logAlpha <= 8 (fits the 2-bit logAlpha-5 field)

    // Picks the hybrid-uint config minimising ANS population cost + raw extra bits + config signalling for one
    // cluster's value histogram (libjxl ChooseUintConfigs). Returns the config, its normalized token histogram
    // and the max token produced. This is the key to matching libjxl: a cluster of small coefficients can
    // direct-code them (no extra bits), one with a heavy tail can spend extra bits — instead of one fixed config.
    private static (int S, int M, int L, int[] Norm, int MaxTok) OptimizeClusterConfig(long[] valHist, int tokenCap, (int S, int M, int L)[] candidates)
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
        foreach ((int s, int m, int l) in candidates)
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
            // token alphabet, e.g. direct coding, looks free when it actually costs more to transmit). The
            // transmission cost is computed analytically (exact WriteHistogram bit count) rather than by
            // encoding each candidate to a throwaway writer.
            int[] cand = JxlEntropy.NormalizeCounts(tokHist, JxlEntropy.HistShift);
            long probeBits = JxlEntropy.HistogramBitCost(cand, JxlEntropy.HistShift);
            double cost = NormalizedCost(tokHist) + extra + JxlBits.CeilLog2(s + 1) + JxlBits.CeilLog2(s - m + 1) + probeBits;
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

    // Lloyd-style joint refinement of the cluster ASSIGNMENT and the per-cluster hybrid-uint CONFIGS. The
    // initial clustering runs on the default config; once each cluster picks its own optimal config, the
    // real per-context coding cost changes, so contexts may now belong to a different cluster. Iterate:
    // optimize configs -> reassign each context to the cluster that codes its values cheapest -> repeat.
    // Mutates `map`; returns the final configs, normalized histograms and shared logAlpha.
    private static (int[] CfgS, int[] CfgM, int[] CfgL, int[][] Norm, int LogAlpha) RefineClusters(
        Dictionary<int, long>[] ctxVH, int[] map, int k, int maxVal, int refineIters, int tokenCap = JpegMaxToken)
    {
        (int S, int M, int L)[] candidates = refineIters >= 3 ? JpegUintCandidates : JpegUintCandidatesFast;
        int n = ctxVH.Length;
        var cfgS = new int[k];
        var cfgM = new int[k];
        var cfgL = new int[k];
        var norm = new int[k][];
        var mtk = new int[k];

        void Optimize()
        {
            var clHist = new long[k][];
            for (int c = 0; c < k; c++)
            {
                clHist[c] = new long[maxVal + 1];
            }

            for (int i = 0; i < n; i++)
            {
                if (ctxVH[i] != null)
                {
                    long[] h = clHist[map[i]];
                    foreach (var kv in ctxVH[i])
                    {
                        h[kv.Key] += kv.Value;
                    }
                }
            }

            for (int c = 0; c < k; c++)
            {
                (cfgS[c], cfgM[c], cfgL[c], norm[c], mtk[c]) = OptimizeClusterConfig(clHist[c], tokenCap, candidates);
            }
        }

        Optimize();

        double tableSum = JxlBits.AnsTabSize;
        for (int iter = 0; iter < refineIters; iter++)
        {
            // Per-cluster per-token cost = -log2(p); a token absent from a cluster's histogram gets a high
            // fallback cost (the next Optimize adds it if a context actually moves there).
            var tokCost = new double[k][];
            for (int c = 0; c < k; c++)
            {
                int[] nc = norm[c];
                var tc = new double[nc.Length];
                for (int t = 0; t < nc.Length; t++)
                {
                    tc[t] = nc[t] > 0 ? -Math.Log2(nc[t] / tableSum) : 16.0;
                }

                tokCost[c] = tc;
            }

            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                if (ctxVH[i] == null || ctxVH[i].Count == 0)
                {
                    continue;
                }

                int bestC = map[i];
                double bestCost = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double cost = 0;
                    double[] tc = tokCost[c];
                    foreach (var kv in ctxVH[i])
                    {
                        (int tok, int nb, _) = PackHybridFull(cfgS[c], cfgM[c], cfgL[c], kv.Key);
                        cost += kv.Value * ((tok < tc.Length ? tc[tok] : 16.0) + nb);
                    }

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestC = c;
                    }
                }

                if (bestC != map[i])
                {
                    map[i] = bestC;
                    changed++;
                }
            }

            Optimize();
            if (changed == 0)
            {
                break;
            }
        }

        int logAlpha = 5;
        for (int c = 0; c < k; c++)
        {
            logAlpha = Math.Max(logAlpha, Math.Max(BitLen(mtk[c]), cfgS[c]));
        }

        return (cfgS, cfgM, cfgL, norm, logAlpha);
    }

    // Builds per-context value histograms (sparse) for the refinement pass.
    private static Dictionary<int, long>[] BuildCtxValueHists(int[] vals, int[] ctxs, int totalContexts)
    {
        var ctxVH = new Dictionary<int, long>[totalContexts];
        for (int i = 0; i < vals.Length; i++)
        {
            var d = ctxVH[ctxs[i]] ??= new Dictionary<int, long>();
            d.TryGetValue(vals[i], out long cur);
            d[vals[i]] = cur + 1;
        }

        return ctxVH;
    }

    // Plain per-context ANS: cluster the contexts (on the default config), then jointly refine the assignment
    // and per-cluster hybrid-uint configs, and emit the standard entropy header.
    private static void EncodeEntropyPlain(JxlBitWriter w, int[] vals, int[] ctxs, int totalContexts, int refineIters, bool usePrefix = false)
    {
        int n = vals.Length;
        int maxTokDefault = 0, maxVal = 0;
        long t0 = ProfStart();
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

        // Allocate a histogram row only for contexts that actually occur (most of the ~1500 contexts are empty
        // on a small image); the clustering skips empty contexts before ever indexing their row, so unused ones
        // share a single zero-length array. Saves the bulk of the per-candidate allocation on small files.
        var used = new bool[totalContexts];
        for (int i = 0; i < n; i++)
        {
            used[ctxs[i]] = true;
        }

        long[] emptyRow = Array.Empty<long>();
        var ctxHist = new long[totalContexts][];
        for (int i = 0; i < totalContexts; i++)
        {
            ctxHist[i] = used[i] ? new long[maxTokDefault + 1] : emptyRow;
        }

        for (int i = 0; i < n; i++)
        {
            ctxHist[ctxs[i]][packedDefault[i]]++;
        }

        ProfEnd("tokenize", t0);
        t0 = ProfStart();
        (int[] map, int[][] _, int k) = ClusterContextsTotalCost(ctxHist, maxTokDefault + 1, JpegMaxClusters, Math.Max(5, BitLen(maxTokDefault)));
        ProfEnd("cluster", t0);

        // Jointly refine the assignment + per-cluster configs (Lloyd-style) starting from that clustering.
        t0 = ProfStart();
        Dictionary<int, long>[] ctxVH = BuildCtxValueHists(vals, ctxs, totalContexts);
        ProfEnd("buildVH", t0);
        t0 = ProfStart();
        (int[] cfgS, int[] cfgM, int[] cfgL, int[][] norm, int logAlpha) = RefineClusters(ctxVH, map, k, maxVal, refineIters);
        ProfEnd("refine", t0);

        if (usePrefix)
        {
            // Prefix-code backend: fixed logAlpha (PrefixMaxBits), so no 2-bit logAlpha field. Per-cluster raw
            // token histograms -> canonical prefix codes (much cheaper to transmit than ANS distributions).
            var tokHist = new long[k][];
            int[] pfxAlpha = new int[k];
            for (int c = 0; c < k; c++)
            {
                tokHist[c] = new long[JpegMaxToken];
            }

            for (int i = 0; i < n; i++)
            {
                int c = map[ctxs[i]];
                int tok = PackHybridFull(cfgS[c], cfgM[c], cfgL[c], vals[i]).Token;
                tokHist[c][tok]++;
                if (tok + 1 > pfxAlpha[c])
                {
                    pfxAlpha[c] = tok + 1;
                }
            }

            var codes = new JxlPrefixCode[k];
            for (int c = 0; c < k; c++)
            {
                codes[c] = new JxlPrefixCode(tokHist[c], Math.Max(1, pfxAlpha[c]));
            }

            w.WriteBool(false); // lz77 disabled
            WriteContextMap(w, (int[])map.Clone(), k);
            w.WriteBool(true); // use_prefix_code = true
            for (int c = 0; c < k; c++)
            {
                WriteUintConfig(w, cfgS[c], cfgM[c], cfgL[c], JxlHuffman.PrefixMaxBits);
            }

            for (int c = 0; c < k; c++)
            {
                w.WriteVarLenUint16(codes[c].AlphabetSize - 1);
            }

            for (int c = 0; c < k; c++)
            {
                codes[c].WriteHeader(w);
            }

            for (int i = 0; i < n; i++)
            {
                int c = map[ctxs[i]];
                (int tok, int nb, int bits) = PackHybridFull(cfgS[c], cfgM[c], cfgL[c], vals[i]);
                codes[c].WriteSymbol(w, tok);
                w.WriteBits((uint)bits, nb);
            }

            return;
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

        long te = ProfStart();
        var ans = new JxlAnsWriter(norm, logAlpha);
        var ansToks = new List<AnsToken>(n);
        for (int i = 0; i < n; i++)
        {
            int c = map[ctxs[i]];
            (int tok, int nb, int bits) = PackHybridFull(cfgS[c], cfgM[c], cfgL[c], vals[i]);
            ansToks.Add(new AnsToken(c, tok, (uint)bits, nb));
        }

        ans.Encode(w, ansToks);
        ProfEnd("ansEncode", te);
    }

    // Length at which a match is "good enough" to stop searching the chain (zlib's nice_match) — caps the
    // per-position work on highly-repetitive regions without measurably hurting ratio.
    private const int Lz77NiceLen = 512;
    private const int Lz77MaxChain = 96;

    // Lazy LZ77 over the value stream: a hash-chain longest-match search with one-step lookahead — if the
    // next position has a strictly longer match, emit a literal now and take the longer match there. This
    // avoids the greedy matcher pre-empting a long match with a shorter one, which matters on graphics.
    //
    // The hash chain uses a flat power-of-two table (not a Dictionary) and hashes are precomputed once for the
    // whole stream, so each position costs an array read instead of a hash + dictionary probe. The match-length
    // extension is vectorised (256-bit where available, else 128-bit, else scalar) — the dominant cost on
    // repetitive content. The matcher only affects the ENCODED SIZE, never correctness (the decoder replays the
    // emitted literal/copy ops), so any valid match set is safe; the container still self-verifies byte-exact.
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

        const int windowSize = 1 << 20;
        int hashBits = Math.Clamp(BitLen(n), 15, 18);
        int hashShift = 32 - hashBits;
        var head = new int[1 << hashBits];
        Array.Fill(head, -1);
        var prev = new int[Math.Max(1, n)];

        // Precompute a hash per 4-token window in one cache-friendly pass (removes all redundant hashing).
        int hashN = Math.Max(0, n - 3);
        var hashArr = new int[Math.Max(1, hashN)];
        for (int i = 0; i < hashN; i++)
        {
            unchecked
            {
                uint hh = (uint)v[i];
                hh = (hh * 2654435761u) + (uint)v[i + 1];
                hh = (hh * 2654435761u) + (uint)v[i + 2];
                hh = (hh * 2654435761u) + (uint)v[i + 3];
                hashArr[i] = (int)((hh * 2654435761u) >> hashShift);
            }
        }

        // Chain depth is self-tuning: it starts thorough, but if the stream turns out to have almost no matches
        // (a photograph, where LZ77 never wins the best-of anyway) the depth collapses so the cache-missing
        // chain walk stops dominating. Repetitive content (graphics/screenshots) keeps the full depth.
        int chainLimit = Lz77MaxChain;

        void Insert(int i)
        {
            if (i >= hashN)
            {
                return;
            }

            int h = hashArr[i];
            prev[i] = head[h];
            head[h] = i;
        }

        (int Len, int Dist) BestMatch(int idx)
        {
            int bestLen = 0, bestDist = 0;
            if (idx < hashN)
            {
                int p = head[hashArr[idx]];
                int tries = chainLimit;
                int maxl = n - idx;
                while (p >= 0 && tries-- > 0)
                {
                    int dist = idx - p;
                    if (dist >= windowSize)
                    {
                        break;
                    }

                    // Quick reject (zlib's key chain optimisation): a candidate can only beat the current best
                    // if the token one past the current best length also matches — checking that one element
                    // skips the full (vectorised) extension for the vast majority of chain nodes on real data.
                    if (bestLen > 0 && v[p + bestLen] != v[idx + bestLen])
                    {
                        p = prev[p];
                        continue;
                    }

                    int l = MatchLen(v, p, idx, maxl);
                    if (l > bestLen)
                    {
                        bestLen = l;
                        bestDist = dist;
                        if (l >= Lz77NiceLen || l >= maxl)
                        {
                            break;
                        }
                    }

                    p = prev[p];
                }
            }

            return (bestLen, bestDist);
        }

        int i2 = 0;
        long matchedTokens = 0;
        int nextCheck = 1 << 14; // re-evaluate chain depth after a warmup window
        while (i2 < n)
        {
            // After each warmup window, gauge how much of the stream LZ77 is actually covering. Near-zero
            // coverage => collapse the chain depth (photo); healthy coverage => restore the full search.
            if (i2 >= nextCheck)
            {
                double frac = (double)matchedTokens / i2;
                chainLimit = frac < 0.005 ? 4 : (frac < 0.02 ? 24 : Lz77MaxChain);
                nextCheck += 1 << 15;
            }

            (int len, int dist) = BestMatch(i2);
            if (len >= minLen)
            {
                matchedTokens += len;
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

    // Length of the common prefix of v[a..] and v[b..], up to maxl elements. Vectorised: compares a lane-width
    // of ints at a time (256-bit AVX2 where available, else 128-bit), locating the first differing lane via the
    // equality mask; falls back to scalar for the remainder and on non-SIMD hardware.
    private static int MatchLen(int[] v, int a, int b, int maxl)
    {
        ref int r = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(v);
        int l = 0;
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            const int w = 8; // Vector256<int>.Count
            while (l + w <= maxl)
            {
                var va = System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref r, (nuint)(a + l));
                var vb = System.Runtime.Intrinsics.Vector256.LoadUnsafe(ref r, (nuint)(b + l));
                uint eq = System.Runtime.Intrinsics.Vector256.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector256.Equals(va, vb));
                if (eq != 0xFF)
                {
                    return l + System.Numerics.BitOperations.TrailingZeroCount(~eq);
                }

                l += w;
            }
        }
        else if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            const int w = 4; // Vector128<int>.Count
            while (l + w <= maxl)
            {
                var va = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref r, (nuint)(a + l));
                var vb = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref r, (nuint)(b + l));
                uint eq = System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.Equals(va, vb));
                if (eq != 0xF)
                {
                    return l + System.Numerics.BitOperations.TrailingZeroCount(~eq);
                }

                l += w;
            }
        }

        while (l < maxl && System.Runtime.CompilerServices.Unsafe.Add(ref r, a + l) == System.Runtime.CompilerServices.Unsafe.Add(ref r, b + l))
        {
            l++;
        }

        return l;
    }

    // ANS with LZ77 back-references over the value stream. Length markers live in the literal alphabet above
    // `threshold` (coded in the position's own context); distances use a dedicated extra cluster. The DECODER
    // (JxlAnsReader) handles the copies transparently, so DecodeJpegCoefficients needs no changes.
    private static void EncodeEntropyLz77(JxlBitWriter w, int[] vals, int[] ctxs, int minLen, int totalContexts, bool lazy, int refineIters, bool usePrefix = false)
    {
        long tm = ProfStart();
        List<Op> ops = lazy ? FindMatchesLazy(vals, minLen) : FindMatches(vals, minLen);
        ProfEnd("lz77match", tm);

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

        // Lazy histogram rows (see EncodeEntropyPlain): only contexts that actually occur get a row; the rest
        // share a zero-length array and are skipped by the clustering. Big allocation saving on small images.
        var used = new bool[totalContexts];
        int uw = 0;
        foreach (Op op in ops)
        {
            used[ctxs[uw]] = true;
            uw += op.Match ? op.A : 1;
        }

        long[] emptyRow = Array.Empty<long>();
        var ctxHist = new long[totalContexts][];
        for (int i = 0; i < totalContexts; i++)
        {
            ctxHist[i] = used[i] ? new long[gmaxDefault + 1] : emptyRow;
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

        // Per-context LITERAL value histograms, then joint Lloyd-style refinement of assignment + configs.
        // Literal tokens capped at 220 so the length markers still fit below 256 (logAlpha <= 8).
        var ctxVH = new Dictionary<int, long>[totalContexts];
        pos = 0;
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                var d = ctxVH[ctxs[pos]] ??= new Dictionary<int, long>();
                d.TryGetValue(op.A, out long cur);
                d[op.A] = cur + 1;
                pos += 1;
            }
            else
            {
                pos += op.A;
            }
        }

        (int[] cfgS, int[] cfgM, int[] cfgL, int[][] _3, int _4) = RefineClusters(ctxVH, map, k, maxVal, refineIters, tokenCap: 220);

        int threshold = 8;
        pos = 0;
        foreach (Op op in ops)
        {
            if (!op.Match)
            {
                int c = map[ctxs[pos]];
                threshold = Math.Max(threshold, PackHybridFull(cfgS[c], cfgM[c], cfgL[c], op.A).Token + 1);
                pos += 1;
            }
            else
            {
                pos += op.A;
            }
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

        if (usePrefix)
        {
            // Prefix-code backend for the LZ77 stream: k literal+length clusters + 1 distance cluster, each a
            // canonical prefix code (cheaper header than ANS on small streams). No 2-bit logAlpha field.
            var pcodes = new JxlPrefixCode[k + 1];
            for (int c = 0; c < k; c++)
            {
                int alpha = 1;
                for (int s = tokHist[c].Length - 1; s >= 0; s--)
                {
                    if (tokHist[c][s] > 0)
                    {
                        alpha = s + 1;
                        break;
                    }
                }

                pcodes[c] = new JxlPrefixCode(tokHist[c], alpha);
            }

            pcodes[k] = new JxlPrefixCode(distHist, gmaxDist + 1);

            w.WriteBool(true); // lz77 enabled
            w.WriteU32((uint)threshold, E.Val(224), E.Val(512), E.Val(4096), E.BitsOff(15, 8)); // min_symbol
            w.WriteU32((uint)minLen, E.Val(3), E.Val(4), E.BitsOff(2, 5), E.BitsOff(8, 9));    // min_length
            WriteUintConfig(w, JpegSeLen, 0, 0, 8);            // lz77 length config
            WriteContextMap(w, combinedMap, k + 1);
            w.WriteBool(true); // use_prefix_code = true
            for (int c = 0; c < k; c++)
            {
                WriteUintConfig(w, cfgS[c], cfgM[c], cfgL[c], JxlHuffman.PrefixMaxBits);
            }

            WriteUintConfig(w, JpegSeDist, 0, 0, JxlHuffman.PrefixMaxBits);
            for (int c = 0; c <= k; c++)
            {
                w.WriteVarLenUint16(pcodes[c].AlphabetSize - 1);
            }

            for (int c = 0; c <= k; c++)
            {
                pcodes[c].WriteHeader(w);
            }

            pos = 0;
            foreach (Op op in ops)
            {
                int cl = map[ctxs[pos]];
                if (!op.Match)
                {
                    (int tok, int nb, int bits) = PackHybridFull(cfgS[cl], cfgM[cl], cfgL[cl], op.A);
                    pcodes[cl].WriteSymbol(w, tok);
                    w.WriteBits((uint)bits, nb);
                    pos += 1;
                }
                else
                {
                    (int lt, int ln, int lb) = PackHybridUint(JpegSeLen, op.A - minLen);
                    pcodes[cl].WriteSymbol(w, threshold + lt);
                    w.WriteBits((uint)lb, ln);
                    (int dt, int dn, int db) = PackHybridUint(JpegSeDist, op.B - 1);
                    pcodes[k].WriteSymbol(w, dt);
                    w.WriteBits((uint)db, dn);
                    pos += op.A;
                }
            }

            return;
        }

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
            var dcProps = new List<long>(1);
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
                    dcProps.Clear();
                    long wpPred = wp.Predict(bx, by, above, left, aboveRight, aboveLeft, aboveAbove, useWp ? dcProps : null);
                    long pred = useWp ? wpPred : ClampGradient(left, above, aboveLeft);
                    long ctxMag = useWp ? dcProps[0] : (Math.Abs(left - aboveLeft) + Math.Abs(above - aboveLeft));
                    int dcRes = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(JpegDcContext(nbc, ci, ctxMag)));
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
