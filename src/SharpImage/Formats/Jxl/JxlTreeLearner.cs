// JPEG XL Modular MA-tree learner, ported from libjxl (modular/encoding/enc_ma.cc,
// enc_encoding.cc). Given the residual channels, it greedily grows a decision tree that splits on the
// most useful of the decoder's pixel properties (channel, WP error, gradient, neighbour differences)
// at learned thresholds, choosing the better predictor (weighted or gradient) per leaf, so long as a
// split saves more than a threshold number of entropy bits. The resulting tree is emitted for the
// decoder, which already evaluates these exact properties — no decoder change is needed.
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using SharpImage.Core;

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
    public double SplitCost, SplitBase; // the learned split's cost and the node's unsplit cost (bits)
}

/// <summary>A residual channel to learn/encode from: pixel data, dimensions, channel and group ids,
/// plus the same-size earlier channels (chan-1, chan-2, ...) used for reference properties.</summary>
internal readonly record struct EncChannelRef(int[] Data, int W, int H, int Chan, int GroupId, int[][] Refs);

/// <summary>What a tree learn may use: the split properties and leaf predictors (indices into
/// <see cref="JxlTreeLearner"/>'s UsedProperties / CandidatePredictors; the weighted predictor must stay in) and the
/// sample cap. <see cref="Default"/> is everything.</summary>
internal sealed class JxlLearnParams
{
    public required int[] PropIdx { get; init; }
    public required int[] PredIdx { get; init; }
    public int MaxSamples { get; init; } = 1 << 21;

    public static readonly JxlLearnParams Default = new()
    {
        PropIdx = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 },
        PredIdx = new[] { 0, 1, 2, 3, 4, 5 },
    };
}

internal static class JxlTreeLearner
{
    public const int WeightedPredictor = 6;
    public const int GradientPredictor = 5;

    // Properties (decoder indices) the tree may split on, in libjxl's priority order. 0 = channel,
    // 1 = group/stream id (per-region adaptation with small groups — libjxl splits on it heavily),
    // 15 = WP error, 9 = gradient, 10..14 = neighbour differences, 2 = y.
    // Non-reference properties only: after the RCT decorrelates the colour channels, cross-channel
    // reference properties (16+) add no signal and just let the learner overfit — measured worse.
    private static readonly int[] UsedProperties = { 0, 1, 15, 9, 10, 11, 12, 13, 14, 2, 4, 5, 6, 7, 8 };

    // libjxl's fixed weighted-predictor-error thresholds (the "< 32 values" set).
    private static readonly int[] WpThresholds =
        { -127, -63, -31, -15, -7, -3, -1, 0, 1, 3, 7, 15, 31, 63, 127 };

    private const int MaxPropertyValues = 48; // quantile buckets for data-driven properties
    private const int MaxSamples = 1 << 21;   // cap learning cost on very large images
    private const int MaxLeaves = 512;         // safety cap on tree size

    // Candidate split thresholds (min entropy-bits a split must save). The right value is content-
    // dependent — busy photographs want a higher threshold (fewer splits: the subsampled cost model
    // over-estimates split benefit since later histogram clustering re-merges contexts), flat/synthetic
    // content wants a lower one. The encoder learns a tree at each and keeps the smaller output.
    public static readonly float[] NodeThresholds = { 96f, 160f };

    /// <summary>Learns a global MA tree for the given residual channels at the given split threshold.</summary>
    /// <summary><see cref="Learn"/> at every node threshold (ascending) from one sample collection: the lowest
    /// threshold's tree is learned and each higher one is that tree with the splits whose gain does not clear it cut
    /// back to leaves (every node's split choice is independent of the threshold, so this is the tree a separate
    /// learn finds), unless the leaf cap cut the low tree short, in which case the higher threshold is learned.</summary>
    public static MaTreeNode[] LearnMulti(List<EncChannelRef> channels, WpHeader wpHeader, float[] nodeThresholds,
        JxlLearnParams? learnParams = null)
    {
        int[][] thresholds = BuildThresholds(channels);
        var samples = CollectSamples(channels, thresholds, wpHeader, learnParams ?? JxlLearnParams.Default);
        var trees = new MaTreeNode[nodeThresholds.Length];
        var root = new MaTreeNode { Property = -1, Predictor = WeightedPredictor };
        int leaves = samples.Count > 1 ? FindBestSplit(samples, thresholds, root, nodeThresholds[0]) : 1;
        trees[0] = root;
        for (int t = 1; t < nodeThresholds.Length; t++)
        {
            if (leaves < MaxLeaves)
            {
                trees[t] = Prune(root, nodeThresholds[t]);
            }
            else
            {
                var r = new MaTreeNode { Property = -1, Predictor = WeightedPredictor };
                if (samples.Count > 1)
                {
                    FindBestSplit(samples, thresholds, r, nodeThresholds[t]);
                }

                trees[t] = r;
            }
        }

        return trees;
    }

    // The tree a learn at nodeThreshold keeps: a split survives when its cost plus the threshold is below the
    // node's unsplit cost (FindBestSplit's test, evaluated identically).
    private static MaTreeNode Prune(MaTreeNode n, double nodeThreshold)
    {
        var c = new MaTreeNode { Property = -1, Predictor = n.Predictor };
        if (n.Property >= 0 && n.SplitCost + nodeThreshold < n.SplitBase)
        {
            c.Property = n.Property;
            c.SplitVal = n.SplitVal;
            c.SplitCost = n.SplitCost;
            c.SplitBase = n.SplitBase;
            c.Left = Prune(n.Left!, nodeThreshold);
            c.Right = Prune(n.Right!, nodeThreshold);
        }

        return c;
    }

    public static MaTreeNode Learn(List<EncChannelRef> channels, WpHeader wpHeader, float nodeThreshold, JxlLearnParams? learnParams = null)
    {
        int[][] thresholds = BuildThresholds(channels);
        var samples = CollectSamples(channels, thresholds, wpHeader, learnParams ?? JxlLearnParams.Default);
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
        public int[] PropMap = null!; // sample property -> UsedProperties index
        public int[] PredMap = null!; // sample predictor -> CandidatePredictors index
        public bool[] Mask = Array.Empty<bool>(); // Partition scratch
        public byte[] TmpB = Array.Empty<byte>();
        public int[] TmpI = Array.Empty<int>();
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

    private static Samples CollectSamples(List<EncChannelRef> channels, int[][] thresholds, WpHeader wpHeader, JxlLearnParams lp)
    {
        int total = 0;
        foreach (EncChannelRef ch in channels)
        {
            total += ch.W * ch.H;
        }

        int stride = total > lp.MaxSamples ? (total / lp.MaxSamples) + 1 : 1;
        int p = lp.PropIdx.Length;
        int np = lp.PredIdx.Length;

        // every stride-th pixel of the channels in order (one running counter over all of them): each channel's first
        // counter value, sample count and output offset are known up front, so the channels run in parallel and
        // write their samples straight into place
        int nc = channels.Count;
        int[] counterStart = new int[nc], outOff = new int[nc];
        int running = 0, count = 0;
        for (int k = 0; k < nc; k++)
        {
            int n = channels[k].W * channels[k].H;
            counterStart[k] = running;
            outOff[k] = count;
            int first = (stride - (running % stride)) % stride;
            count += first < n ? ((n - 1 - first) / stride) + 1 : 0;
            running += n;
        }

        var s = new Samples { P = p, NP = np, PropMap = lp.PropIdx, PredMap = lp.PredIdx, Count = count, Prop = new byte[p][], Tok = new int[np][], Nb = new byte[np][] };
        for (int i = 0; i < p; i++)
        {
            s.Prop[i] = new byte[count];
        }

        for (int i = 0; i < np; i++)
        {
            s.Tok[i] = new int[count];
            s.Nb[i] = new byte[count];
        }

        Parallel.For(0, nc, k =>
        {
            EncChannelRef ch = channels[k];
            int[] props = new int[16 + (4 * MaxRefChannels)];
            long[] guesses = new long[CandidatePredictors.Length];
            var wpBuf = new List<long>(1);
            int w = ch.W, h = ch.H;
            var wp = new WpState(wpHeader, w);
            int counter = counterStart[k], o = outOff[k];
            int prevGrad = 0;
            for (int y = 0; y < h; y++)
            {
                prevGrad = 0;
                for (int x = 0; x < w; x++)
                {
                    int pixel = ch.Data[(y * w) + x];
                    if (counter++ % stride != 0)
                    {
                        AdvanceWp(ch.Data, w, x, y, wp, wpBuf, ref prevGrad);
                    }
                    else
                    {
                        ComputeProps(ch.Data, w, ch.Chan, ch.GroupId, x, y, wp, wpBuf, props, ref prevGrad, ch.Refs, out Neighbors nbr);
                        for (int i = 0; i < p; i++)
                        {
                            s.Prop[i][o] = (byte)Bucket(props[UsedProperties[lp.PropIdx[i]]], thresholds[lp.PropIdx[i]]);
                        }

                        for (int i = 0; i < np; i++)
                        {
                            (int tk, byte nb) = Tokenize(pixel - (int)Guess(CandidatePredictors[lp.PredIdx[i]], in nbr));
                            s.Tok[i][o] = tk;
                            s.Nb[i][o] = nb;
                        }

                        o++;
                    }

                    wp.Update(pixel, x, y);
                }
            }
        });

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
    /// <summary>A pixel's causal neighbours (the predictors' inputs).</summary>
    internal struct Neighbors
    {
        public long Left, Top, TopLeft, TopRight, LeftLeft, TopTop, TopRR, Wp;
    }

    public static void ComputePixel(
        int[] px, int w, int chan, int groupId, int x, int y, WpState wp, List<long> wpBuf, int[] props, ref int prevGrad, long[] guesses, int[][] refChannels)
    {
        ComputeProps(px, w, chan, groupId, x, y, wp, wpBuf, props, ref prevGrad, refChannels, out Neighbors nb);
        for (int i = 0; i < CandidatePredictors.Length; i++)
        {
            guesses[i] = Guess(CandidatePredictors[i], in nb);
        }
    }

    /// <summary>One predictor's guess from the neighbours (<see cref="ComputeProps"/>).</summary>
    public static long Guess(int predictor, in Neighbors nb) =>
        PredictGuess(predictor, nb.Left, nb.Top, nb.TopTop, nb.TopLeft, nb.TopRight, nb.LeftLeft, nb.TopRR, nb.Wp);

    /// <summary>Only the weighted predictor's prediction (its state must see every pixel) and the running
    /// gradient: a pixel whose properties and guesses are not needed.</summary>
    public static void AdvanceWp(int[] px, int w, int x, int y, WpState wp, List<long> wpBuf, ref int prevGrad)
    {
        long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
        long top = y > 0 ? px[((y - 1) * w) + x] : left;
        long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
        long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
        long toptop = y > 1 ? px[((y - 2) * w) + x] : top;
        prevGrad = (int)(left + top - topleft);
        wp.Predict(x, y, top, left, topright, topleft, toptop, out _);
    }

    /// <summary>The pixel's properties (props) and weighted prediction, with its neighbours for <see cref="Guess"/>.</summary>
    public static void ComputeProps(
        int[] px, int w, int chan, int groupId, int x, int y, WpState wp, List<long> wpBuf, int[] props, ref int prevGrad, int[][] refChannels,
        out Neighbors nb)
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

        long wpPred = wp.Predict(x, y, top, left, topright, topleft, toptop, out long wpMaxErr);
        props[15] = (int)wpMaxErr;

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

        nb = new Neighbors { Left = left, Top = top, TopLeft = topleft, TopRight = topright, LeftLeft = leftleft, TopTop = toptop, TopRR = toprr, Wp = wpPred };
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

        // Group property (p1) thresholds: the split values are the distinct group/stream ids themselves,
        // so the tree can isolate any region. "p1 > id_k" separates the groups after id_k.
        var groupIds = new SortedSet<int>();
        foreach (EncChannelRef ch in channels)
        {
            groupIds.Add(ch.GroupId);
        }

        int[] groupThresholds;
        if (groupIds.Count <= 1)
        {
            groupThresholds = Array.Empty<int>();
        }
        else
        {
            var gl = new List<int>(groupIds);
            gl.RemoveAt(gl.Count - 1); // drop the max: "p1 > max" is always empty
            groupThresholds = gl.ToArray();
        }

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
                1 => groupThresholds,
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

    // log2 of the small counts (EstimateBits' inner term without a Log call per symbol)
    private static readonly double[] Log2Small = BuildLog2Small();

    private static double[] BuildLog2Small()
    {
        var t = new double[1 << 16];
        for (int i = 1; i < t.Length; i++)
        {
            t[i] = PortableMathD.Log2(i);
        }

        return t;
    }

    // Shannon bits of a histogram with libjxl's minimum probability 1/4096 (ANS_TAB_SIZE): -c * log2(max(c / total,
    // 1/4096)) summed, as c * (log2 total - log2 c) above the floor and 12 bits per symbol below it.
    private static double EstimateBits(int[] counts, int len, long total) => EstimateBits(counts.AsSpan(0, len), total);

    private static double EstimateBits(ReadOnlySpan<int> counts, long total)
    {
        int len = counts.Length;
        if (total == 0)
        {
            return 0;
        }

        double log2Total = PortableMathD.Log2Fast(total);
        double bits = 0;
        long floorBits = 0;
        for (int i = 0; i < len; i++)
        {
            int c = counts[i];
            if (c == 0)
            {
                continue;
            }

            if ((long)c * 4096 < total)
            {
                floorBits += c;
            }
            else
            {
                bits += c * (log2Total - (c < (1 << 16) ? Log2Small[c] : PortableMathD.Log2Fast(c)));
            }
        }

        return bits + (12.0 * floorBits);
    }

    // ─── Greedy split search (libjxl FindBestSplit, simplified: no static-multiplier forcing) ────────

    // Returns the leaf count (MaxLeaves when the cap stopped the growth).
    private static int FindBestSplit(Samples s, int[][] thresholds, MaTreeNode root, double nodeThreshold)
    {
        var stack = new Stack<(int Begin, int End, MaTreeNode Node)>();
        stack.Push((0, s.Count, root));
        int leaves = 1;

        int np = s.NP;
        int maxNb = 1;
        for (int pi = 0; pi < s.P; pi++)
        {
            maxNb = Math.Max(maxNb, thresholds[s.PropMap[pi]].Length + 1);
        }

        int[] baseH = Array.Empty<int>();
        long[] baseExtra = new long[np];
        var scratch = new SplitScratch(np, maxNb, 1);
        var propBest = new (double Cost, int Bucket, int LPred, int RPred)[s.P];
        while (stack.Count > 0)
        {
            (int b, int e, MaTreeNode cur) = stack.Pop();
            if (e - b <= 1 || leaves >= MaxLeaves)
            {
                continue;
            }

            int maxSym = 1;
            for (int q = 0; q < np; q++)
            {
                int[] tok = s.Tok[q];
                for (int i = b; i < e; i++)
                {
                    maxSym = Math.Max(maxSym, tok[i] + 1);
                }
            }

            if (baseH.Length < np * maxSym)
            {
                baseH = new int[np * maxSym];
            }

            long rangeTotal = e - b;

            // Base histograms for the whole range, per predictor.
            Array.Clear(baseH, 0, np * maxSym);
            for (int q = 0; q < np; q++)
            {
                int[] tok = s.Tok[q];
                byte[] nbq = s.Nb[q];
                int o = q * maxSym;
                long ex = 0;
                for (int i = b; i < e; i++)
                {
                    baseH[o + tok[i]]++;
                    ex += nbq[i];
                }

                baseExtra[q] = ex;
            }

            int curIdx = 0;
            for (int q = 0; q < np; q++)
            {
                if (CandidatePredictors[s.PredMap[q]] == cur.Predictor)
                {
                    curIdx = q;
                }
            }

            double baseBits = EstimateBits(baseH.AsSpan(curIdx * maxSym, maxSym), rangeTotal) + baseExtra[curIdx];

            double bestCost = double.MaxValue;
            int bestPropIdx = -1, bestBucket = -1, bestLPred = WeightedPredictor, bestRPred = WeightedPredictor;

            // the properties' best splits (in parallel on big ranges), reduced in property order: the first strictly
            // cheaper split wins, exactly as one sequential sweep over (property, bucket)
            int nprop = s.P;
            if ((long)(e - b) * nprop >= ParallelSplitWork)
            {
                Parallel.For(0, nprop, () => new SplitScratch(np, maxNb, maxSym), (pi, _, sc) =>
                {
                    propBest[pi] = EvalProperty(s, thresholds, pi, b, e, maxSym, baseH, baseExtra, rangeTotal, sc);
                    return sc;
                }, _ => { });
            }
            else
            {
                if (scratch.Hist.Length < np * maxNb * maxSym)
                {
                    scratch = new SplitScratch(np, maxNb, maxSym);
                }

                for (int pi = 0; pi < nprop; pi++)
                {
                    propBest[pi] = EvalProperty(s, thresholds, pi, b, e, maxSym, baseH, baseExtra, rangeTotal, scratch);
                }
            }

            for (int pi = 0; pi < nprop; pi++)
            {
                var pb = propBest[pi];
                if (pb.Cost < bestCost)
                {
                    bestCost = pb.Cost;
                    bestPropIdx = pi;
                    bestBucket = pb.Bucket;
                    bestLPred = pb.LPred;
                    bestRPred = pb.RPred;
                }
            }

            if (bestPropIdx >= 0 && bestCost + nodeThreshold < baseBits)
            {
                int splitPos = Partition(s, b, e, bestPropIdx, bestBucket);
                if (splitPos <= b || splitPos >= e)
                {
                    continue; // degenerate; keep as leaf
                }

                cur.Property = UsedProperties[s.PropMap[bestPropIdx]];
                cur.SplitVal = thresholds[s.PropMap[bestPropIdx]][bestBucket];
                cur.SplitCost = bestCost;
                cur.SplitBase = baseBits;
                cur.Left = new MaTreeNode { Property = -1, Predictor = bestLPred };
                cur.Right = new MaTreeNode { Property = -1, Predictor = bestRPred };
                leaves++; // one leaf becomes two
                stack.Push((splitPos, e, cur.Left));   // property > SplitVal
                stack.Push((b, splitPos, cur.Right));  // property <= SplitVal
            }
        }

        return leaves;
    }

    // nodes with at least this many (sample, property) pairs search their properties in parallel
    private const long ParallelSplitWork = 1 << 18;

    // A property search's flat buffers: bucket histograms [(q * nb + k) * maxSym + sym], their extra bits
    // [q * nb + k], the sweep's below / above [q * maxSym + sym].
    private sealed class SplitScratch
    {
        public readonly int[] Hist, Below, Above;
        public readonly long[] BkExtra, BkTotal, BelowExtra, AboveExtra;

        public SplitScratch(int np, int maxNb, int maxSym)
        {
            Hist = new int[np * maxNb * maxSym];
            Below = new int[np * maxSym];
            Above = new int[np * maxSym];
            BkExtra = new long[np * maxNb];
            BkTotal = new long[maxNb];
            BelowExtra = new long[np];
            AboveExtra = new long[np];
        }
    }

    // The best split of [b, e) on property pi: its cost (MaxValue if none), bucket and side predictors; the first
    // strictly cheapest bucket.
    private static (double Cost, int Bucket, int LPred, int RPred) EvalProperty(Samples s, int[][] thresholds, int pi, int b, int e,
        int maxSym, int[] baseH, long[] baseExtra, long rangeTotal, SplitScratch sc)
    {
        int np = s.NP;
        int nb = thresholds[s.PropMap[pi]].Length + 1;
        double bestCost = double.MaxValue;
        int bestBucket = -1, bestLPred = WeightedPredictor, bestRPred = WeightedPredictor;
        if (nb <= 1)
        {
            return (bestCost, bestBucket, bestLPred, bestRPred);
        }

        int[] hist = sc.Hist, below = sc.Below, above = sc.Above;
        long[] bkExtra = sc.BkExtra, bkTotal = sc.BkTotal, belowExtra = sc.BelowExtra, aboveExtra = sc.AboveExtra;
        byte[] bucket = s.Prop[pi];

        // Per-bucket histograms for each predictor.
        Array.Clear(hist, 0, np * nb * maxSym);
        Array.Clear(bkExtra, 0, np * nb);
        Array.Clear(bkTotal, 0, nb);
        for (int i = b; i < e; i++)
        {
            bkTotal[bucket[i]]++;
        }

        for (int q = 0; q < np; q++)
        {
            int[] tok = s.Tok[q];
            byte[] nbq = s.Nb[q];
            int qb = q * nb;
            for (int i = b; i < e; i++)
            {
                int k = qb + bucket[i];
                hist[(k * maxSym) + tok[i]]++;
                bkExtra[k] += nbq[i];
            }
        }

        // Sweep the split point; accumulate "below" (property <= threshold[bk]) from low buckets.
        Array.Clear(below, 0, np * maxSym);
        Array.Copy(baseH, above, np * maxSym);
        for (int q = 0; q < np; q++)
        {
            belowExtra[q] = 0;
            aboveExtra[q] = baseExtra[q];
        }

        long belowTotal = 0, aboveTotal = rangeTotal;
        for (int bk = 0; bk + 1 < nb; bk++)
        {
            for (int q = 0; q < np; q++)
            {
                int src = ((q * nb) + bk) * maxSym;
                int dst = q * maxSym;
                for (int sym = 0; sym < maxSym; sym++)
                {
                    int v = hist[src + sym];
                    below[dst + sym] += v;
                    above[dst + sym] -= v;
                }

                belowExtra[q] += bkExtra[(q * nb) + bk];
                aboveExtra[q] -= bkExtra[(q * nb) + bk];
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
                double l = EstimateBits(below.AsSpan(q * maxSym, maxSym), belowTotal) + belowExtra[q];
                double rr = EstimateBits(above.AsSpan(q * maxSym, maxSym), aboveTotal) + aboveExtra[q];
                if (l < lc)
                {
                    lc = l;
                    lp = CandidatePredictors[s.PredMap[q]];
                }

                if (rr < rc)
                {
                    rc = rr;
                    rp = CandidatePredictors[s.PredMap[q]];
                }
            }

            if (lc + rc < bestCost)
            {
                bestCost = lc + rc;
                bestBucket = bk;
                bestLPred = lp;
                bestRPred = rp;
            }
        }

        return (bestCost, bestBucket, bestLPred, bestRPred);
    }

    // Partition [b,e) so buckets <= bestBucket (property <= threshold) come first, the rest after. Stable, column by
    // column through a scratch buffer (the children's histograms do not depend on the samples' order, so the tree is
    // the same as the swap partition's).
    private static int Partition(Samples s, int b, int e, int propIdx, int bestBucket)
    {
        byte[] bucket = s.Prop[propIdx];
        int n = e - b;
        if (s.Mask.Length < n)
        {
            s.Mask = new bool[n];
            s.TmpB = new byte[n + 1];
            s.TmpI = new int[n + 1];
        }

        bool[] left = s.Mask;
        int nl = 0;
        for (int i = 0; i < n; i++)
        {
            bool l = bucket[b + i] <= bestBucket;
            left[i] = l;
            nl += l ? 1 : 0;
        }

        for (int c = 0; c < s.P; c++)
        {
            PartitionColumn(s.Prop[c], s.TmpB, left, b, n, nl);
        }

        for (int q = 0; q < s.NP; q++)
        {
            PartitionColumn(s.Tok[q], s.TmpI, left, b, n, nl);
            PartitionColumn(s.Nb[q], s.TmpB, left, b, n, nl);
        }

        return b + nl;
    }

    // branchless: each value is written at both cursors and only its side's cursor moves; the lefts fill up from 0 and
    // the rights down from n - 1, so every stray write lands on a slot a later real write overwrites (the rights come
    // out reversed, which the children's histograms do not see)
    private static void PartitionColumn<T>(T[] col, T[] tmp, bool[] left, int b, int n, int nl) where T : unmanaged
    {
        ref T c0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(col);
        ref T t0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(tmp);
        ref bool l0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(left);
        int li = 0, ri = n - 1;
        for (int i = 0; i < n; i++)
        {
            T v = System.Runtime.CompilerServices.Unsafe.Add(ref c0, b + i);
            int l = System.Runtime.CompilerServices.Unsafe.As<bool, byte>(ref System.Runtime.CompilerServices.Unsafe.Add(ref l0, i));
            System.Runtime.CompilerServices.Unsafe.Add(ref t0, li) = v;
            System.Runtime.CompilerServices.Unsafe.Add(ref t0, ri < 0 ? 0 : ri) = v;
            li += l;
            ri -= 1 - l;
        }

        Array.Copy(tmp, 0, col, b, n);
    }
}
