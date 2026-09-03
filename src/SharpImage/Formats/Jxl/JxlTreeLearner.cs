// JPEG XL Modular MA-tree learner, ported from libjxl (modular/encoding/enc_ma.cc,
// enc_encoding.cc). Given the residual channels, it greedily grows a decision tree that splits on the
// most useful of the decoder's pixel properties (channel, WP error, gradient, neighbour differences)
// at learned thresholds, choosing the better predictor (weighted or gradient) per leaf, so long as a
// split saves more than a threshold number of entropy bits. The resulting tree is emitted for the
// decoder, which already evaluates these exact properties — no decoder change is needed.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

/// <summary>A Modular MA-tree node (encoder side). Left is taken when property &gt; SplitVal.</summary>
internal sealed class MaTreeNode
{
    public int Property = -1;   // -1 => leaf
    public int SplitVal;
    public int Predictor = 6;   // leaf predictor: 6 = weighted, 5 = gradient
    public int Ctx;             // leaf context index (assigned when serialised)
    public MaTreeNode? Left;    // property > SplitVal
    public MaTreeNode? Right;   // property <= SplitVal
}

/// <summary>A residual channel to learn/encode from: pixel data, dimensions, channel and group ids,
/// plus the same-size earlier channels (chan-1, chan-2, ...) used for reference properties.</summary>
internal readonly record struct EncChannelRef(int[] Data, int W, int H, int Chan, int GroupId, int[][] Refs);

internal static class JxlTreeLearner
{
    public const int WeightedPredictor = 6;
    public const int GradientPredictor = 5;

    // Properties (decoder indices) the tree may split on, in libjxl's priority order minus the group
    // property (we use one global tree). 0 = channel, 15 = WP error, 9 = gradient, 10..14 = neighbour
    // differences, 2 = y.
    // Non-reference properties only: after the RCT decorrelates the colour channels, cross-channel
    // reference properties (16+) add no signal and just let the learner overfit — measured worse.
    private static readonly int[] UsedProperties = { 0, 15, 9, 10, 11, 12, 13, 14, 2, 4, 5, 6, 7, 8 };

    // libjxl's fixed weighted-predictor-error thresholds (the "< 32 values" set).
    private static readonly int[] WpThresholds =
        { -127, -63, -31, -15, -7, -3, -1, 0, 1, 3, 7, 15, 31, 63, 127 };

    private const int MaxPropertyValues = 48; // quantile buckets for data-driven properties
    private const int MaxSamples = 1 << 21;   // cap learning cost on very large images
    private const int MaxLeaves = 256;         // safety cap on tree size

    // Candidate split thresholds (min entropy-bits a split must save). The right value is content-
    // dependent — busy photographs want a higher threshold (fewer splits: the subsampled cost model
    // over-estimates split benefit since later histogram clustering re-merges contexts), flat/synthetic
    // content wants a lower one. The encoder learns a tree at each and keeps the smaller output.
    public static readonly float[] NodeThresholds = { 160f, 400f };

    /// <summary>Learns a global MA tree for the given residual channels at the given split threshold.</summary>
    public static MaTreeNode Learn(List<EncChannelRef> channels, WpHeader wpHeader, float nodeThreshold)
    {
        int[][] thresholds = BuildThresholds(channels);
        var samples = CollectSamples(channels, thresholds, wpHeader);
        var root = new MaTreeNode { Property = -1, Predictor = WeightedPredictor };
        if (samples.Count > 1)
        {
            FindBestSplit(samples, thresholds, root, nodeThreshold);
        }

        return root;
    }

    // ─── Sample collection ─────────────────────────────────────────────────────────────────────────

    private sealed class Samples
    {
        public int P;                 // number of properties
        public int NP;                // number of candidate predictors
        public byte[][] Prop = null!; // [P][sample] quantised bucket index
        public int[][] Tok = null!;   // [NP][sample] predictor token
        public byte[][] Nb = null!;   // [NP][sample] extra bits count
        public int Count;

        public void Swap(int a, int b)
        {
            for (int i = 0; i < P; i++)
            {
                (Prop[i][a], Prop[i][b]) = (Prop[i][b], Prop[i][a]);
            }

            for (int i = 0; i < NP; i++)
            {
                (Tok[i][a], Tok[i][b]) = (Tok[i][b], Tok[i][a]);
                (Nb[i][a], Nb[i][b]) = (Nb[i][b], Nb[i][a]);
            }
        }
    }

    private static Samples CollectSamples(List<EncChannelRef> channels, int[][] thresholds, WpHeader wpHeader)
    {
        int total = 0;
        foreach (EncChannelRef ch in channels)
        {
            total += ch.W * ch.H;
        }

        int stride = total > MaxSamples ? (total / MaxSamples) + 1 : 1;
        int p = UsedProperties.Length;
        int np = CandidatePredictors.Length;
        var propLists = new List<byte>[p];
        for (int i = 0; i < p; i++)
        {
            propLists[i] = new List<byte>();
        }

        var tokLists = new List<int>[np];
        var nbLists = new List<byte>[np];
        for (int i = 0; i < np; i++)
        {
            tokLists[i] = new List<int>();
            nbLists[i] = new List<byte>();
        }

        int counter = 0;
        int[] props = new int[16 + (4 * MaxRefChannels)];
        long[] guesses = new long[np];
        var wpBuf = new List<long>(1);
        foreach (EncChannelRef ch in channels)
        {
            int w = ch.W, h = ch.H;
            var wp = new WpState(wpHeader, w);
            int prevGrad = 0;
            for (int y = 0; y < h; y++)
            {
                prevGrad = 0;
                for (int x = 0; x < w; x++)
                {
                    ComputePixel(ch.Data, w, ch.Chan, ch.GroupId, x, y, wp, wpBuf, props, ref prevGrad, guesses, ch.Refs);
                    int pixel = ch.Data[(y * w) + x];
                    if (counter++ % stride == 0)
                    {
                        for (int i = 0; i < p; i++)
                        {
                            propLists[i].Add((byte)Bucket(props[UsedProperties[i]], thresholds[i]));
                        }

                        for (int i = 0; i < np; i++)
                        {
                            (int tk, byte nb) = Tokenize(pixel - (int)guesses[i]);
                            tokLists[i].Add(tk);
                            nbLists[i].Add(nb);
                        }
                    }

                    wp.Update(pixel, x, y);
                }
            }
        }

        var s = new Samples { P = p, NP = np, Count = tokLists[0].Count, Prop = new byte[p][], Tok = new int[np][], Nb = new byte[np][] };
        for (int i = 0; i < p; i++)
        {
            s.Prop[i] = propLists[i].ToArray();
        }

        for (int i = 0; i < np; i++)
        {
            s.Tok[i] = tokLists[i].ToArray();
            s.Nb[i] = nbLists[i].ToArray();
        }

        return s;
    }

    // Candidate leaf predictors the learner chooses between (decoder ids). The full modular set is
    // supported by PredictGuess, but using all of them overfits the subsampled cost model; this focused
    // set (weighted, gradient, select, average, top, left) measured best.
    public static readonly int[] CandidatePredictors = { 6, 5, 4, 3, 2, 1 };

    public static int PredictorIndex(int pred)
    {
        for (int i = 0; i < CandidatePredictors.Length; i++)
        {
            if (CandidatePredictors[i] == pred)
            {
                return i;
            }
        }

        return 0;
    }

    // Number of reference channels the encoder/learner consider (matches the props we may split on).
    public const int MaxRefChannels = 2;

    // Computes props[0..15] (non-reference) and props[16+] (reference/cross-channel, from refChannels,
    // matching JxlModular.DecodeChannel) and fills `guesses` with each candidate predictor's prediction.
    // Shared by the learner and the encoder so they never diverge.
    public static void ComputePixel(
        int[] px, int w, int chan, int groupId, int x, int y, WpState wp, List<long> wpBuf, int[] props, ref int prevGrad, long[] guesses, int[][] refChannels)
    {
        long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
        long top = y > 0 ? px[((y - 1) * w) + x] : left;
        long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
        long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
        long leftleft = x > 1 ? px[(y * w) + x - 2] : left;
        long toptop = y > 1 ? px[((y - 2) * w) + x] : top;
        long toprr = (x + 2 < w && y > 0) ? px[((y - 1) * w) + x + 2] : topright;

        props[0] = chan;
        props[1] = groupId;
        props[2] = y;
        props[3] = x;
        props[4] = (int)Math.Abs(top);
        props[5] = (int)Math.Abs(left);
        props[6] = (int)top;
        props[7] = (int)left;
        props[8] = (int)left - prevGrad;
        int grad = (int)(left + top - topleft);
        props[9] = grad;
        prevGrad = grad;
        props[10] = (int)(left - topleft);
        props[11] = (int)(topleft - top);
        props[12] = (int)(top - topright);
        props[13] = (int)(top - toptop);
        props[14] = (int)(left - leftleft);

        wpBuf.Clear();
        long wpPred = wp.Predict(x, y, top, left, topright, topleft, toptop, wpBuf);
        props[15] = (int)wpBuf[0];

        int roff = 16;
        for (int k = 0; k < refChannels.Length && roff + 3 < props.Length; k++)
        {
            int[] rp = refChannels[k];
            long v = rp[(y * w) + x];
            long vleft = x > 0 ? rp[(y * w) + x - 1] : 0;
            long vtop = y > 0 ? rp[((y - 1) * w) + x] : vleft;
            long vtopleft = (x > 0 && y > 0) ? rp[((y - 1) * w) + x - 1] : vleft;
            long vpred = ClampedGradient(vleft, vtop, vtopleft);
            props[roff] = (int)Math.Abs(v);
            props[roff + 1] = (int)v;
            props[roff + 2] = (int)Math.Abs(v - vpred);
            props[roff + 3] = (int)(v - vpred);
            roff += 4;
        }

        for (int i = 0; i < CandidatePredictors.Length; i++)
        {
            guesses[i] = PredictGuess(CandidatePredictors[i], left, top, toptop, topleft, topright, leftleft, toprr, wpPred);
        }
    }

    // Mirrors JxlModular.PredictOne for all modular predictors (0..13).
    private static long PredictGuess(int pr, long left, long top, long toptop, long topleft, long topright, long leftleft, long toprr, long wp) => pr switch
    {
        0 => 0,
        1 => left,
        2 => top,
        3 => (left + top) / 2,
        4 => Select(left, top, topleft),
        5 => ClampedGradient(left, top, topleft),
        6 => wp,
        7 => topright,
        8 => topleft,
        9 => leftleft,
        10 => (left + topleft) / 2,
        11 => (topleft + top) / 2,
        12 => (top + topright) / 2,
        13 => ((6 * top) - (2 * toptop) + (7 * left) + leftleft + toprr + (3 * topright) + 8) / 16,
        _ => 0,
    };

    private static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        return Math.Abs(p - a) < Math.Abs(p - b) ? a : b;
    }

    public static long ClampedGradient(long n, long w, long l)
    {
        long m = Math.Min(n, w);
        long mx = Math.Max(n, w);
        long grad = n + w - l;
        long gcm = l < m ? mx : grad;
        return l > mx ? m : gcm;
    }

    // ─── Property quantisation ───────────────────────────────────────────────────────────────────────

    private static int[][] BuildThresholds(List<EncChannelRef> channels)
    {
        int total = 0;
        foreach (EncChannelRef ch in channels)
        {
            total += ch.W * ch.H;
        }

        int stride = Math.Max(1, total / (1 << 18));
        var diffs = new List<int>();
        var pixels = new List<int>();
        int cnt = 0;
        foreach (EncChannelRef ch in channels)
        {
            int w = ch.W, h = ch.H;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (cnt++ % stride == 0)
                    {
                        pixels.Add(ch.Data[(y * w) + x]);
                        if (x > 0)
                        {
                            diffs.Add(ch.Data[(y * w) + x] - ch.Data[(y * w) + x - 1]);
                        }
                    }
                }
            }
        }

        int[] diffThresholds = QuantizeSamples(diffs, MaxPropertyValues);
        int[] pixelThresholds = QuantizeSamples(pixels, MaxPropertyValues);
        var absPixels = new List<int>(pixels.Count);
        var absDiffs = new List<int>(diffs.Count);
        foreach (int v in pixels)
        {
            absPixels.Add(Math.Abs(v));
        }

        foreach (int v in diffs)
        {
            absDiffs.Add(Math.Abs(v));
        }

        int[] absPixelThresholds = QuantizeSamples(absPixels, MaxPropertyValues);
        int[] absDiffThresholds = QuantizeSamples(absDiffs, MaxPropertyValues);

        int[][] thr = new int[UsedProperties.Length][];
        for (int i = 0; i < UsedProperties.Length; i++)
        {
            int p = UsedProperties[i];
            // Reference properties (16+): the 4 sub-properties per channel are |v|, v, |v-grad|, v-grad,
            // quantised like abs-pixel / pixel / abs-diff / diff respectively (libjxl PreQuantizeProperties).
            int refKind = p >= 16 ? (p - 16) % 4 : -1;
            thr[i] = p switch
            {
                0 => new[] { 0, 1 },
                15 => WpThresholds,
                2 or 3 => QuantizeCoordinate(),
                4 or 5 => absPixelThresholds, // |top|, |left|
                6 or 7 => pixelThresholds,    // top, left
                _ when refKind == 0 => absPixelThresholds,
                _ when refKind == 1 => pixelThresholds,
                _ when refKind == 2 => absDiffThresholds,
                _ when refKind == 3 => diffThresholds,
                _ => diffThresholds,          // gradient + neighbour differences (8..14)
            };
        }

        return thr;
    }

    private static int[] QuantizeCoordinate()
    {
        const int n = 8;
        int[] t = new int[n - 1];
        for (int i = 0; i + 1 < n; i++)
        {
            t[i] = ((i + 1) * 256 / n) - 1;
        }

        return t;
    }

    // Bucket = number of thresholds strictly exceeded by value (index between ascending thresholds).
    private static int Bucket(int value, int[] thresholds)
    {
        int b = 0;
        while (b < thresholds.Length && value > thresholds[b])
        {
            b++;
        }

        return b;
    }

    private static int[] QuantizeSamples(List<int> samples, int numChunks)
    {
        if (samples.Count == 0)
        {
            return Array.Empty<int>();
        }

        const int range = 512;
        int min = int.MaxValue;
        foreach (int v in samples)
        {
            min = Math.Min(min, v);
        }

        min = Math.Clamp(min, -range, range);
        long[] counts = new long[(2 * range) + 1];
        foreach (int v in samples)
        {
            counts[Math.Clamp(v, -range, range) - min]++;
        }

        int[] th = QuantizeHistogram(counts, numChunks);
        for (int i = 0; i < th.Length; i++)
        {
            th[i] += min;
        }

        return th;
    }

    private static int[] QuantizeHistogram(long[] histogram, int numChunks)
    {
        long sum = 0;
        foreach (long v in histogram)
        {
            sum += v;
        }

        if (sum == 0)
        {
            return Array.Empty<int>();
        }

        var thresholds = new List<int>();
        long cumsum = 0;
        long threshold = 1;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumsum += histogram[i];
            if (cumsum * numChunks >= threshold * sum)
            {
                thresholds.Add(i);
                while (cumsum * numChunks >= threshold * sum)
                {
                    threshold++;
                }
            }
        }

        if (thresholds.Count > 0)
        {
            thresholds.RemoveAt(thresholds.Count - 1);
        }

        return thresholds.ToArray();
    }

    // ─── Cost model ────────────────────────────────────────────────────────────────────────────────

    private static readonly HybridUint TokenConfig = new(4, 1, 2);

    private static (int Token, byte NBits) Tokenize(int residual)
    {
        uint packed = (uint)((residual << 1) ^ (residual >> 31));
        (int tok, int nbits) = TokenConfig.Encode(packed);
        return (tok, (byte)nbits);
    }

    private readonly struct HybridUint
    {
        private readonly int splitExp, splitToken, msb, lsb;
        public HybridUint(int splitExp, int msb, int lsb)
        {
            this.splitExp = splitExp;
            splitToken = 1 << splitExp;
            this.msb = msb;
            this.lsb = lsb;
        }

        public (int Token, int NBits) Encode(uint value)
        {
            if (value < splitToken)
            {
                return ((int)value, 0);
            }

            int n = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
            uint m = value - (1u << n);
            int token = splitToken + ((n - splitExp) << (msb + lsb)) + (int)((m >> (n - msb)) << lsb) + (int)(m & ((1u << lsb) - 1));
            int nbits = n - msb - lsb;
            return (token, nbits);
        }
    }

    private static double EstimateBits(int[] counts, int len, long total)
    {
        if (total == 0)
        {
            return 0;
        }

        const double minprob = 1.0 / 4096.0;
        double invTotal = 1.0 / total;
        double invLog2 = 1.0 / Math.Log(2);
        double bits = 0;
        for (int i = 0; i < len; i++)
        {
            int c = counts[i];
            if (c == 0 || c == total)
            {
                continue;
            }

            double pr = Math.Max(c * invTotal, minprob);
            bits -= c * Math.Log(pr) * invLog2;
        }

        return bits;
    }

    // ─── Greedy split search (libjxl FindBestSplit, simplified: no static-multiplier forcing) ────────

    private static void FindBestSplit(Samples s, int[][] thresholds, MaTreeNode root, double nodeThreshold)
    {
        var stack = new Stack<(int Begin, int End, MaTreeNode Node)>();
        stack.Push((0, s.Count, root));
        int leaves = 1;

        int np = s.NP;
        while (stack.Count > 0)
        {
            (int b, int e, MaTreeNode cur) = stack.Pop();
            if (e - b <= 1 || leaves >= MaxLeaves)
            {
                continue;
            }

            int maxSym = 1;
            for (int i = b; i < e; i++)
            {
                for (int q = 0; q < np; q++)
                {
                    maxSym = Math.Max(maxSym, s.Tok[q][i] + 1);
                }
            }

            long rangeTotal = e - b;

            // Base histograms for the whole range, per predictor.
            int[][] baseH = new int[np][];
            long[] baseExtra = new long[np];
            for (int q = 0; q < np; q++)
            {
                baseH[q] = new int[maxSym];
            }

            for (int i = b; i < e; i++)
            {
                for (int q = 0; q < np; q++)
                {
                    baseH[q][s.Tok[q][i]]++;
                    baseExtra[q] += s.Nb[q][i];
                }
            }

            int curIdx = PredictorIndex(cur.Predictor);
            double baseBits = EstimateBits(baseH[curIdx], maxSym, rangeTotal) + baseExtra[curIdx];

            double bestCost = double.MaxValue;
            int bestPropIdx = -1, bestBucket = -1, bestLPred = WeightedPredictor, bestRPred = WeightedPredictor;

            for (int pi = 0; pi < s.P; pi++)
            {
                int nb = thresholds[pi].Length + 1;
                if (nb <= 1)
                {
                    continue;
                }

                byte[] bucket = s.Prop[pi];

                // Per-bucket histograms for each predictor.
                int[][][] bkH = new int[np][][];
                long[][] bkExtra = new long[np][];
                long[] bkTotal = new long[nb];
                for (int q = 0; q < np; q++)
                {
                    bkH[q] = new int[nb][];
                    bkExtra[q] = new long[nb];
                    for (int k = 0; k < nb; k++)
                    {
                        bkH[q][k] = new int[maxSym];
                    }
                }

                for (int i = b; i < e; i++)
                {
                    int k = bucket[i];
                    for (int q = 0; q < np; q++)
                    {
                        bkH[q][k][s.Tok[q][i]]++;
                        bkExtra[q][k] += s.Nb[q][i];
                    }

                    bkTotal[k]++;
                }

                // Sweep the split point; accumulate "below" (property <= threshold[bk]) from low buckets.
                int[][] below = new int[np][];
                int[][] above = new int[np][];
                long[] belowExtra = new long[np];
                long[] aboveExtra = new long[np];
                for (int q = 0; q < np; q++)
                {
                    below[q] = new int[maxSym];
                    above[q] = (int[])baseH[q].Clone();
                    aboveExtra[q] = baseExtra[q];
                }

                long belowTotal = 0, aboveTotal = rangeTotal;

                for (int bk = 0; bk + 1 < nb; bk++)
                {
                    for (int q = 0; q < np; q++)
                    {
                        int[] src = bkH[q][bk];
                        int[] bl = below[q];
                        int[] ab = above[q];
                        for (int sym = 0; sym < maxSym; sym++)
                        {
                            bl[sym] += src[sym];
                            ab[sym] -= src[sym];
                        }

                        belowExtra[q] += bkExtra[q][bk];
                        aboveExtra[q] -= bkExtra[q][bk];
                    }

                    belowTotal += bkTotal[bk];
                    aboveTotal -= bkTotal[bk];

                    if (belowTotal == 0 || aboveTotal == 0)
                    {
                        continue;
                    }

                    double lc = double.MaxValue, rc = double.MaxValue;
                    int lp = WeightedPredictor, rp = WeightedPredictor;
                    for (int q = 0; q < np; q++)
                    {
                        double l = EstimateBits(below[q], maxSym, belowTotal) + belowExtra[q];
                        double rr = EstimateBits(above[q], maxSym, aboveTotal) + aboveExtra[q];
                        if (l < lc)
                        {
                            lc = l;
                            lp = CandidatePredictors[q];
                        }

                        if (rr < rc)
                        {
                            rc = rr;
                            rp = CandidatePredictors[q];
                        }
                    }

                    if (lc + rc < bestCost)
                    {
                        bestCost = lc + rc;
                        bestPropIdx = pi;
                        bestBucket = bk;
                        bestLPred = lp;
                        bestRPred = rp;
                    }
                }
            }

            if (bestPropIdx >= 0 && bestCost + nodeThreshold < baseBits)
            {
                int splitPos = Partition(s, b, e, bestPropIdx, bestBucket);
                if (splitPos <= b || splitPos >= e)
                {
                    continue; // degenerate; keep as leaf
                }

                cur.Property = UsedProperties[bestPropIdx];
                cur.SplitVal = thresholds[bestPropIdx][bestBucket];
                cur.Left = new MaTreeNode { Property = -1, Predictor = bestLPred };
                cur.Right = new MaTreeNode { Property = -1, Predictor = bestRPred };
                leaves++; // one leaf becomes two
                stack.Push((splitPos, e, cur.Left));   // property > SplitVal
                stack.Push((b, splitPos, cur.Right));  // property <= SplitVal
            }
        }
    }

    // Partition [b,e) so buckets <= bestBucket (property <= threshold) come first, the rest after.
    private static int Partition(Samples s, int b, int e, int propIdx, int bestBucket)
    {
        byte[] bucket = s.Prop[propIdx];
        int lo = b, hi = e - 1;
        while (lo <= hi)
        {
            if (bucket[lo] <= bestBucket)
            {
                lo++;
            }
            else
            {
                s.Swap(lo, hi);
                hi--;
            }
        }

        return lo;
    }
}
