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

/// <summary>A residual channel to learn/encode from: pixel data, dimensions, channel and group ids.</summary>
internal readonly record struct EncChannelRef(int[] Data, int W, int H, int Chan, int GroupId);

internal static class JxlTreeLearner
{
    public const int WeightedPredictor = 6;
    public const int GradientPredictor = 5;

    // Properties (decoder indices) the tree may split on, in libjxl's priority order minus the group
    // property (we use one global tree). 0 = channel, 15 = WP error, 9 = gradient, 10..14 = neighbour
    // differences, 2 = y.
    private static readonly int[] UsedProperties = { 0, 15, 9, 10, 11, 12, 13, 14, 2, 4, 5, 6, 7, 8 };

    // libjxl's fixed weighted-predictor-error thresholds (the "< 32 values" set).
    private static readonly int[] WpThresholds =
        { -127, -63, -31, -15, -7, -3, -1, 0, 1, 3, 7, 15, 31, 63, 127 };

    private const int MaxPropertyValues = 48; // quantile buckets for data-driven properties
    private const int MaxSamples = 1 << 21;   // cap learning cost on very large images
    private const float NodeThreshold = 160f;  // min entropy-bits a split must save (libjxl ~75..150)
    private const int MaxLeaves = 256;         // safety cap on tree size

    /// <summary>Learns a global MA tree for the given residual channels (whole-channel prediction).</summary>
    public static MaTreeNode Learn(List<EncChannelRef> channels)
    {
        int[][] thresholds = BuildThresholds(channels);
        var samples = CollectSamples(channels, thresholds);
        var root = new MaTreeNode { Property = -1, Predictor = WeightedPredictor };
        if (samples.Count > 1)
        {
            FindBestSplit(samples, thresholds, root);
        }

        return root;
    }

    // ─── Sample collection ─────────────────────────────────────────────────────────────────────────

    private sealed class Samples
    {
        public int P;
        public byte[][] Prop = null!; // [P][sample] quantised bucket index
        public int[] TokW = null!;    // weighted-predictor token
        public byte[] NbW = null!;
        public int[] TokG = null!;    // gradient-predictor token
        public byte[] NbG = null!;
        public int Count;

        public void Swap(int a, int b)
        {
            for (int i = 0; i < P; i++)
            {
                (Prop[i][a], Prop[i][b]) = (Prop[i][b], Prop[i][a]);
            }

            (TokW[a], TokW[b]) = (TokW[b], TokW[a]);
            (NbW[a], NbW[b]) = (NbW[b], NbW[a]);
            (TokG[a], TokG[b]) = (TokG[b], TokG[a]);
            (NbG[a], NbG[b]) = (NbG[b], NbG[a]);
        }
    }

    private static Samples CollectSamples(List<EncChannelRef> channels, int[][] thresholds)
    {
        int total = 0;
        foreach (EncChannelRef ch in channels)
        {
            total += ch.W * ch.H;
        }

        int stride = total > MaxSamples ? (total / MaxSamples) + 1 : 1;
        int p = UsedProperties.Length;
        var propLists = new List<byte>[p];
        for (int i = 0; i < p; i++)
        {
            propLists[i] = new List<byte>();
        }

        var tokW = new List<int>();
        var nbW = new List<byte>();
        var tokG = new List<int>();
        var nbG = new List<byte>();

        int counter = 0;
        int[] props = new int[16];
        var wpBuf = new List<long>(1);
        foreach (EncChannelRef ch in channels)
        {
            int w = ch.W, h = ch.H;
            var wp = new WpState(WpHeader.Default(), w);
            int prevGrad = 0;
            for (int y = 0; y < h; y++)
            {
                prevGrad = 0;
                for (int x = 0; x < w; x++)
                {
                    (long wpPred, long gradGuess) = ComputePixel(ch.Data, w, ch.Chan, ch.GroupId, x, y, wp, wpBuf, props, ref prevGrad);
                    int pixel = ch.Data[(y * w) + x];
                    if (counter++ % stride == 0)
                    {
                        for (int i = 0; i < p; i++)
                        {
                            propLists[i].Add((byte)Bucket(props[UsedProperties[i]], thresholds[i]));
                        }

                        (int tw, byte nw) = Tokenize(pixel - (int)wpPred);
                        (int tg, byte ng) = Tokenize(pixel - (int)gradGuess);
                        tokW.Add(tw);
                        nbW.Add(nw);
                        tokG.Add(tg);
                        nbG.Add(ng);
                    }

                    wp.Update(pixel, x, y);
                }
            }
        }

        var s = new Samples { P = p, Count = tokW.Count, Prop = new byte[p][], TokW = tokW.ToArray(), NbW = nbW.ToArray(), TokG = tokG.ToArray(), NbG = nbG.ToArray() };
        for (int i = 0; i < p; i++)
        {
            s.Prop[i] = propLists[i].ToArray();
        }

        return s;
    }

    // Computes props[0..15] (matching JxlModular.DecodeChannel's general path) and returns the weighted
    // and gradient predictions. Shared by the learner and the encoder so they never diverge.
    public static (long WpPred, long GradGuess) ComputePixel(
        int[] px, int w, int chan, int groupId, int x, int y, WpState wp, List<long> wpBuf, int[] props, ref int prevGrad)
    {
        long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
        long top = y > 0 ? px[((y - 1) * w) + x] : left;
        long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
        long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
        long leftleft = x > 1 ? px[(y * w) + x - 2] : left;
        long toptop = y > 1 ? px[((y - 2) * w) + x] : top;

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

        long gradGuess = ClampedGradient(left, top, topleft);
        return (wpPred, gradGuess);
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
        foreach (int v in pixels)
        {
            absPixels.Add(Math.Abs(v));
        }

        int[] absPixelThresholds = QuantizeSamples(absPixels, MaxPropertyValues);

        int[][] thr = new int[UsedProperties.Length][];
        for (int i = 0; i < UsedProperties.Length; i++)
        {
            thr[i] = UsedProperties[i] switch
            {
                0 => new[] { 0, 1 },
                15 => WpThresholds,
                2 or 3 => QuantizeCoordinate(),
                4 or 5 => absPixelThresholds, // |top|, |left|
                6 or 7 => pixelThresholds,    // top, left
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

    private static void FindBestSplit(Samples s, int[][] thresholds, MaTreeNode root)
    {
        var stack = new Stack<(int Begin, int End, MaTreeNode Node)>();
        stack.Push((0, s.Count, root));
        int leaves = 1;

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
                maxSym = Math.Max(maxSym, Math.Max(s.TokW[i], s.TokG[i]) + 1);
            }

            // Base histograms for the whole range (both predictors).
            int[] baseW = new int[maxSym];
            int[] baseG = new int[maxSym];
            long baseExtraW = 0, baseExtraG = 0;
            for (int i = b; i < e; i++)
            {
                baseW[s.TokW[i]]++;
                baseExtraW += s.NbW[i];
                baseG[s.TokG[i]]++;
                baseExtraG += s.NbG[i];
            }

            long rangeTotal = e - b;
            double baseBits = cur.Predictor == WeightedPredictor
                ? EstimateBits(baseW, maxSym, rangeTotal) + baseExtraW
                : EstimateBits(baseG, maxSym, rangeTotal) + baseExtraG;

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
                int[][] bkW = new int[nb][];
                int[][] bkG = new int[nb][];
                long[] bkExtraW = new long[nb];
                long[] bkExtraG = new long[nb];
                long[] bkTotal = new long[nb];
                for (int k = 0; k < nb; k++)
                {
                    bkW[k] = new int[maxSym];
                    bkG[k] = new int[maxSym];
                }

                for (int i = b; i < e; i++)
                {
                    int k = bucket[i];
                    bkW[k][s.TokW[i]]++;
                    bkG[k][s.TokG[i]]++;
                    bkExtraW[k] += s.NbW[i];
                    bkExtraG[k] += s.NbG[i];
                    bkTotal[k]++;
                }

                // Sweep the split point; accumulate "below" (property <= threshold[bk]) from low buckets.
                int[] belowW = new int[maxSym];
                int[] belowG = new int[maxSym];
                long belowExtraW = 0, belowExtraG = 0, belowTotal = 0;
                int[] aboveW = (int[])baseW.Clone();
                int[] aboveG = (int[])baseG.Clone();
                long aboveExtraW = baseExtraW, aboveExtraG = baseExtraG, aboveTotal = rangeTotal;

                for (int bk = 0; bk + 1 < nb; bk++)
                {
                    for (int sym = 0; sym < maxSym; sym++)
                    {
                        belowW[sym] += bkW[bk][sym];
                        aboveW[sym] -= bkW[bk][sym];
                        belowG[sym] += bkG[bk][sym];
                        aboveG[sym] -= bkG[bk][sym];
                    }

                    belowExtraW += bkExtraW[bk];
                    aboveExtraW -= bkExtraW[bk];
                    belowExtraG += bkExtraG[bk];
                    aboveExtraG -= bkExtraG[bk];
                    belowTotal += bkTotal[bk];
                    aboveTotal -= bkTotal[bk];

                    if (belowTotal == 0 || aboveTotal == 0)
                    {
                        continue;
                    }

                    double lW = EstimateBits(belowW, maxSym, belowTotal) + belowExtraW;
                    double lG = EstimateBits(belowG, maxSym, belowTotal) + belowExtraG;
                    double rW = EstimateBits(aboveW, maxSym, aboveTotal) + aboveExtraW;
                    double rG = EstimateBits(aboveG, maxSym, aboveTotal) + aboveExtraG;

                    int lp = lW <= lG ? WeightedPredictor : GradientPredictor;
                    int rp = rW <= rG ? WeightedPredictor : GradientPredictor;
                    double lc = Math.Min(lW, lG);
                    double rc = Math.Min(rW, rG);
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

            if (bestPropIdx >= 0 && bestCost + NodeThreshold < baseBits)
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
