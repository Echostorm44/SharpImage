// JPEG XL Modular (lossless) mode decoder: MA decision tree, per-pixel predictors including the
// self-correcting weighted predictor, and the RCT / Palette inverse transforms. Ported from
// libjxl (dec_ma.cc, encoding.cc, context_predict.h, transform/rct.cc, transform/palette.*) and
// validated bit-exactly against a Python prototype across many libjxl-produced lossless images.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal sealed class JxlChannel
{
    public int W;
    public int H;
    public int[] Px;
    public bool Meta;

    public JxlChannel(int w, int h, bool meta = false)
    {
        W = w;
        H = h;
        Px = new int[w * h];
        Meta = meta;
    }

    public int Get(int x, int y) => Px[(y * W) + x];

    public void Set(int x, int y, int v) => Px[(y * W) + x] = v;
}

internal struct MaNode
{
    public int Prop;
    public int SplitVal;
    public int LChild;
    public int RChild;
    public int Predictor;
    public int Offset;
    public int Mult;
    public int Ctx;
}

internal struct Transform
{
    public int Id;
    public int BeginC;
    public int RctType;
    public int NumC;
    public int NbColors;
    public int NbDeltas;
    public int Predictor;
}

/// <summary>Self-correcting weighted predictor state (libjxl weighted::State).</summary>
internal sealed class WpState
{
    private const int PredExtraBits = 3;
    private const int PredictionRound = ((1 << PredExtraBits) >> 1) - 1;

    private static readonly long[] DivLookup = BuildDivLookup();

    private static long[] BuildDivLookup()
    {
        var d = new long[64];
        for (int i = 0; i < 64; i++)
        {
            d[i] = (1L << 24) / (i + 1);
        }

        return d;
    }

    private readonly int p1C, p2C, p3Ca, p3Cb, p3Cc, p3Cd, p3Ce;
    private readonly int[] w;
    private readonly int xsize;
    private readonly long[][] predErrors;
    private readonly long[] error;
    private readonly long[] prediction = new long[4];
    private long pred;

    public WpState(WpHeader hdr, int xsize)
    {
        p1C = hdr.P1C; p2C = hdr.P2C; p3Ca = hdr.P3Ca; p3Cb = hdr.P3Cb; p3Cc = hdr.P3Cc; p3Cd = hdr.P3Cd; p3Ce = hdr.P3Ce;
        w = hdr.W;
        this.xsize = xsize;
        int n = (xsize + 2) * 2;
        predErrors = new long[4][];
        for (int i = 0; i < 4; i++)
        {
            predErrors[i] = new long[n];
        }

        error = new long[n];
    }

    private static int FloorLog2(long x) => 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)x);

    private long ErrorWeight(long x, int maxw)
    {
        int shift = FloorLog2(x + 1) - 5;
        if (shift < 0)
        {
            shift = 0;
        }

        return 4 + ((maxw * DivLookup[x >> shift]) >> shift);
    }

    private long WeightedAvg(long[] p, long[] wt)
    {
        long ws = wt[0] + wt[1] + wt[2] + wt[3];
        int logw = FloorLog2(ws);
        for (int i = 0; i < 4; i++)
        {
            wt[i] >>= logw - 4;
        }

        ws = wt[0] + wt[1] + wt[2] + wt[3];
        long s = (ws >> 1) - 1;
        for (int i = 0; i < 4; i++)
        {
            s += p[i] * wt[i];
        }

        return (s * DivLookup[ws - 1]) >> 24;
    }

    public long Predict(int x, int y, long n, long ww, long ne, long nw, long nn, List<long>? props)
    {
        int cur = (y & 1) != 0 ? 0 : (xsize + 2);
        int prevRow = (y & 1) != 0 ? (xsize + 2) : 0;
        int pN = prevRow + x;
        int pNE = x < xsize - 1 ? pN + 1 : pN;
        int pNW = x > 0 ? pN - 1 : pN;
        var weights = new long[4];
        for (int i = 0; i < 4; i++)
        {
            long wv = predErrors[i][pN] + predErrors[i][pNE] + predErrors[i][pNW];
            weights[i] = ErrorWeight(wv, w[i]);
        }

        n <<= PredExtraBits;
        ww <<= PredExtraBits;
        ne <<= PredExtraBits;
        nw <<= PredExtraBits;
        nn <<= PredExtraBits;
        long teW = x == 0 ? 0 : error[cur + x - 1];
        long teN = error[pN];
        long teNW = error[pNW];
        long teNE = error[pNE];
        long sumWN = teN + teW;
        if (props != null)
        {
            long pmax = teW;
            if (Math.Abs(teN) > Math.Abs(pmax)) { pmax = teN; }
            if (Math.Abs(teNW) > Math.Abs(pmax)) { pmax = teNW; }
            if (Math.Abs(teNE) > Math.Abs(pmax)) { pmax = teNE; }
            props.Add(pmax);
        }

        prediction[0] = ww + ne - n;
        prediction[1] = n - (((sumWN + teNE) * p1C) >> 5);
        prediction[2] = ww - (((sumWN + teNW) * p2C) >> 5);
        prediction[3] = n - (((teNW * p3Ca) + (teN * p3Cb) + (teNE * p3Cc) + ((nn - n) * p3Cd) + ((nw - ww) * p3Ce)) >> 5);
        pred = WeightedAvg(prediction, weights);
        if (((teN ^ teW) | (teN ^ teNW)) > 0)
        {
            return (pred + PredictionRound) >> PredExtraBits;
        }

        long mx = Math.Max(ww, Math.Max(ne, n));
        long mn = Math.Min(ww, Math.Min(ne, n));
        pred = Math.Max(mn, Math.Min(mx, pred));
        return (pred + PredictionRound) >> PredExtraBits;
    }

    public void Update(long val, int x, int y)
    {
        int cur = (y & 1) != 0 ? 0 : (xsize + 2);
        int prevRow = (y & 1) != 0 ? (xsize + 2) : 0;
        val <<= PredExtraBits;
        error[cur + x] = pred - val;
        for (int i = 0; i < 4; i++)
        {
            long err = (Math.Abs(prediction[i] - val) + PredictionRound) >> PredExtraBits;
            predErrors[i][cur + x] = err;
            predErrors[i][prevRow + x + 1] += err;
        }
    }
}

internal struct WpHeader
{
    public int P1C, P2C, P3Ca, P3Cb, P3Cc, P3Cd, P3Ce;
    public int[] W;

    public static WpHeader Default() => new()
    {
        P1C = 16, P2C = 10, P3Ca = 7, P3Cb = 7, P3Cc = 7, P3Cd = 0, P3Ce = 0, W = [0xd, 0xc, 0xc, 0xc],
    };
}

internal static class JxlModular
{
    // MA tree contexts.
    private const int KSplitVal = 0, KProp = 1, KPred = 2, KOffset = 3, KMulLog = 4, KMulBits = 5;
    private const int NumTreeContexts = 6;

    private static int Idiv2(int x) => x < 0 ? -((-x) / 2) : x / 2;

    private static long ClampedGradient(long n, long w, long l)
    {
        long m = Math.Min(n, w);
        long mx = Math.Max(n, w);
        long grad = n + w - l;
        long gcm = l < m ? mx : grad;
        return l > mx ? m : gcm;
    }

    private static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        return Math.Abs(p - a) < Math.Abs(p - b) ? a : b;
    }

    private static long PredictOne(int pr, long left, long top, long toptop, long topleft, long topright, long leftleft, long toprr, long wp)
    {
        return pr switch
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
    }

    public static List<MaNode> DecodeTree(JxlBitReader br)
    {
        JxlAnsCode code = JxlEntropy.DecodeHistograms(NumTreeContexts, br);
        var rd = new JxlAnsReader(code, br);
        var tree = new List<MaNode>();
        int toDecode = 1;
        int leafId = 0;
        while (toDecode > 0)
        {
            toDecode--;
            int prop1 = (int)rd.ReadHybridUintCtx(KProp);
            int prop = prop1 - 1;
            if (prop == -1)
            {
                int predictor = (int)rd.ReadHybridUintCtx(KPred);
                int offset = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(KOffset));
                int mulLog = (int)rd.ReadHybridUintCtx(KMulLog);
                int mulBits = (int)rd.ReadHybridUintCtx(KMulBits);
                int mult = (mulBits + 1) << mulLog;
                tree.Add(new MaNode { Prop = -1, Predictor = predictor, Offset = offset, Mult = mult, Ctx = leafId++ });
            }
            else
            {
                int splitval = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(KSplitVal));
                int l = tree.Count + toDecode + 1;
                int r = tree.Count + toDecode + 2;
                tree.Add(new MaNode { Prop = prop, SplitVal = splitval, LChild = l, RChild = r, Mult = 1 });
                toDecode += 2;
            }
        }

        rd.CheckFinal();
        return tree;
    }

    private static int TreeLeaf(List<MaNode> tree, int[] props)
    {
        int nodeIdx = 0;
        while (tree[nodeIdx].Prop != -1)
        {
            MaNode nd = tree[nodeIdx];
            nodeIdx = props[nd.Prop] > nd.SplitVal ? nd.LChild : nd.RChild;
        }

        return nodeIdx;
    }

    private static int prevGrad;

    private static void DecodeChannel(JxlAnsReader reader, List<MaNode> tree, byte[] ctxMap, WpHeader wpHdr, int chan, JxlChannel ch)
    {
        int w = ch.W, h = ch.H;
        if (w == 0 || h == 0)
        {
            return;
        }

        int[] px = ch.Px;
        var wp = new WpState(wpHdr, w);
        bool singleWp = tree.Count == 1;
        var props = new int[16];
        var wpPropsBuf = new List<long>(1);
        for (int y = 0; y < h; y++)
        {
            prevGrad = 0;
            for (int x = 0; x < w; x++)
            {
                long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                long top = y > 0 ? px[((y - 1) * w) + x] : left;
                long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
                long leftleft = x > 1 ? px[(y * w) + x - 2] : left;
                long toptop = y > 1 ? px[((y - 2) * w) + x] : top;
                long toprr = (x + 2 < w && y > 0) ? px[((y - 1) * w) + x + 2] : topright;

                MaNode node;
                if (singleWp)
                {
                    node = tree[0];
                    long wpOnly = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
                    long guess0 = node.Offset + PredictOne(node.Predictor, left, top, toptop, topleft, topright, leftleft, toprr, wpOnly);
                    uint v0 = reader.ReadHybridUintClustered(ctxMap[node.Ctx]);
                    long val0 = ((long)JxlBits.UnpackSigned(v0) * node.Mult) + guess0;
                    px[(y * w) + x] = (int)val0;
                    wp.Update(val0, x, y);
                    continue;
                }

                // General tree: build full property vector.
                props[0] = chan;
                props[1] = 0;
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
                wpPropsBuf.Clear();
                long wpPred = wp.Predict(x, y, top, left, topright, topleft, toptop, wpPropsBuf);
                props[15] = (int)wpPropsBuf[0];
                node = tree[TreeLeaf(tree, props)];
                uint v = reader.ReadHybridUintClustered(ctxMap[node.Ctx]);
                long guess = node.Offset + PredictOne(node.Predictor, left, top, toptop, topleft, topright, leftleft, toprr, wpPred);
                long val = ((long)JxlBits.UnpackSigned(v) * node.Mult) + guess;
                px[(y * w) + x] = (int)val;
                wp.Update(val, x, y);
            }
        }
    }

    private static void InvRct(List<JxlChannel> chans, int beginC, int rctType)
    {
        if (rctType == 0)
        {
            return;
        }

        int perm = rctType / 7;
        int custom = rctType % 7;
        JxlChannel c0 = chans[beginC], c1 = chans[beginC + 1], c2 = chans[beginC + 2];
        int w = c0.W, h = c0.H;
        int[] outIdx = { beginC + (perm % 3), beginC + ((perm + 1 + (perm / 3)) % 3), beginC + ((perm + 2 - (perm / 3)) % 3) };
        int second = custom >> 1;
        int third = custom & 1;
        var newPx = new Dictionary<int, int[]>();
        foreach (int o in outIdx)
        {
            newPx[o] = new int[w * h];
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                int o0, o1, o2;
                if (custom == 6)
                {
                    int yy = c0.Px[p], co = c1.Px[p], cg = c2.Px[p];
                    int tmp = yy - (cg >> 1);
                    int g = cg + tmp;
                    int b = tmp - (co >> 1);
                    int r = b + co;
                    o0 = r; o1 = g; o2 = b;
                }
                else
                {
                    int f = c0.Px[p], s = c1.Px[p], t = c2.Px[p];
                    if (third != 0)
                    {
                        t += f;
                    }

                    if (second == 1)
                    {
                        s += f;
                    }
                    else if (second == 2)
                    {
                        s += (f + t) >> 1;
                    }

                    o0 = f; o1 = s; o2 = t;
                }

                newPx[outIdx[0]][p] = o0;
                newPx[outIdx[1]][p] = o1;
                newPx[outIdx[2]][p] = o2;
            }
        }

        foreach (int o in outIdx)
        {
            chans[o].Px = newPx[o];
        }
    }

    private static readonly int[][] DeltaPalette =
    [
        [0,0,0],[4,4,4],[11,0,0],[0,0,-13],[0,-12,0],[-10,-10,-10],[-18,-18,-18],[-27,-27,-27],[-18,-18,0],[0,0,-32],[-32,0,0],[-37,-37,-37],[0,-32,-32],[24,24,45],[50,50,50],[-45,-24,-24],[-24,-45,-45],[0,-24,-24],[-34,-34,0],[-24,0,-24],[-45,-45,-24],[64,64,64],[-32,0,-32],[0,-32,0],[-32,0,32],[-24,-45,-24],[45,24,45],[24,-24,-45],[-45,-24,24],[80,80,80],[64,0,0],[0,0,-64],[0,-64,-64],[-24,-24,45],[96,96,96],[64,64,0],[45,-24,-24],[34,-34,0],[112,112,112],[24,-45,-45],[45,45,-24],[0,-32,32],[24,-24,45],[0,96,96],[45,-24,24],[24,-45,-24],[-24,-45,24],[0,-64,0],[96,0,0],[128,128,128],[64,0,64],[144,144,144],[96,96,0],[-36,-36,36],[45,-24,-45],[45,-45,-24],[0,0,-96],[0,128,128],[0,96,0],[45,24,-45],[-128,0,0],[24,-45,24],[-45,24,-45],[64,0,-64],[64,-64,-64],[96,0,96],[45,-45,24],[24,45,-45],[64,64,-64],[128,128,0],[0,0,-128],[-24,45,-45],
    ];

    private static int GetPaletteValue(JxlChannel pal, int index, int c, int paletteSize, int bitDepth)
    {
        const int rgb = 3, largeCube = 5, smallCube = 4, smallCubeBits = 2;
        int largeCubeOffset = smallCube * smallCube * smallCube;
        long ScaleV(long value) => (value * ((1L << bitDepth) - 1)) >> 2;
        if (index < 0)
        {
            if (c >= rgb)
            {
                return 0;
            }

            index = -(index + 1);
            index %= 1 + (2 * (DeltaPalette.Length - 1));
            int result = DeltaPalette[(index + 1) >> 1][c] * ((index & 1) == 0 ? -1 : 1);
            if (bitDepth > 8)
            {
                result *= 1 << (bitDepth - 8);
            }

            return result;
        }

        if (paletteSize <= index && index < paletteSize + largeCubeOffset)
        {
            if (c >= rgb)
            {
                return 0;
            }

            index -= paletteSize;
            index >>= c * smallCubeBits;
            return (int)ScaleV(index % smallCube) + (1 << Math.Max(0, bitDepth - 3));
        }

        if (index >= paletteSize + largeCubeOffset)
        {
            if (c >= rgb)
            {
                return 0;
            }

            index -= paletteSize + largeCubeOffset;
            if (c == 1)
            {
                index /= largeCube;
            }
            else if (c == 2)
            {
                index /= largeCube * largeCube;
            }

            return (int)ScaleV(index % largeCube);
        }

        return pal.Px[(c * pal.W) + index];
    }

    private static void MetaApply(List<JxlChannel> chans, ref int metaCount, Transform t)
    {
        if (t.Id == 0)
        {
            return; // RCT — no structural change.
        }

        if (t.Id == 1)
        {
            int beginC = t.BeginC, numC = t.NumC, endC = beginC + numC - 1;
            int nb = numC;
            if (beginC >= metaCount)
            {
                metaCount++;
            }
            else
            {
                metaCount += 2 - nb;
            }

            chans.RemoveRange(beginC + 1, endC - beginC);
            var pal = new JxlChannel(t.NbColors + t.NbDeltas, nb, meta: true);
            chans.Insert(0, pal);
            return;
        }

        throw new NotSupportedException($"JXL modular transform {t.Id} not supported.");
    }

    private static long PredictNoTree(int[] px, int x, int y, int w, int predictor, WpState? wp)
    {
        long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
        long top = y > 0 ? px[((y - 1) * w) + x] : left;
        long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
        long topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
        long leftleft = x > 1 ? px[(y * w) + x - 2] : left;
        long toptop = y > 1 ? px[((y - 2) * w) + x] : top;
        long toprr = (x + 2 < w && y > 0) ? px[((y - 1) * w) + x + 2] : topright;
        long wpp = 0;
        if (predictor == 6 && wp != null)
        {
            wpp = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
        }

        return PredictOne(predictor, left, top, toptop, topleft, topright, leftleft, toprr, wpp);
    }

    private static void InvPalette(List<JxlChannel> chans, Transform t, WpHeader wpHdr, int bitDepth)
    {
        int beginC = t.BeginC, numC = t.NumC, nb = numC;
        int nbDeltas = t.NbDeltas, predictor = t.Predictor;
        JxlChannel pal = chans[0];
        int c0 = beginC + 1;
        JxlChannel idxCh = chans[c0];
        int w = idxCh.W, h = idxCh.H;
        int paletteSize = pal.W;
        var outs = new List<JxlChannel> { idxCh };
        for (int i = 0; i < nb - 1; i++)
        {
            outs.Add(new JxlChannel(w, h));
        }

        int[] indices = (int[])idxCh.Px.Clone();
        if (nbDeltas == 0 && predictor == 0)
        {
            for (int c = 0; c < nb; c++)
            {
                JxlChannel oc = outs[c];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int index = indices[(y * w) + x];
                        if (nb == 1)
                        {
                            index = Math.Max(0, Math.Min(paletteSize - 1, index));
                        }

                        oc.Px[(y * w) + x] = GetPaletteValue(pal, index, c, paletteSize, bitDepth);
                    }
                }
            }
        }
        else
        {
            for (int c = 0; c < nb; c++)
            {
                JxlChannel oc = outs[c];
                WpState? wp = predictor == 6 ? new WpState(wpHdr, w) : null;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int index = indices[(y * w) + x];
                        int pe = GetPaletteValue(pal, index, c, paletteSize, bitDepth);
                        long val;
                        if (index < nbDeltas)
                        {
                            long guess = PredictNoTree(oc.Px, x, y, w, predictor, wp);
                            val = guess + pe;
                        }
                        else
                        {
                            val = pe;
                        }

                        oc.Px[(y * w) + x] = (int)val;
                        wp?.Update(val, x, y);
                    }
                }
            }
        }

        chans.RemoveAt(0); // palette
        chans.RemoveRange(beginC, 1);
        chans.InsertRange(beginC, outs);
    }

    private static Transform ReadTransform(JxlBitReader br)
    {
        var t = default(Transform);
        t.Id = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(0), JxlBitReader.U32Enc.Val(1), JxlBitReader.U32Enc.Val(2), JxlBitReader.U32Enc.Val(3));
        if (t.Id is 0 or 1)
        {
            t.BeginC = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.BitsOff(3, 0), JxlBitReader.U32Enc.BitsOff(6, 8), JxlBitReader.U32Enc.BitsOff(10, 72), JxlBitReader.U32Enc.BitsOff(13, 1096));
        }

        if (t.Id == 0)
        {
            t.RctType = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(6), JxlBitReader.U32Enc.BitsOff(2, 0), JxlBitReader.U32Enc.BitsOff(4, 2), JxlBitReader.U32Enc.BitsOff(6, 10));
        }

        if (t.Id == 1)
        {
            t.NumC = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(1), JxlBitReader.U32Enc.Val(3), JxlBitReader.U32Enc.Val(4), JxlBitReader.U32Enc.BitsOff(13, 1));
            t.NbColors = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.BitsOff(8, 0), JxlBitReader.U32Enc.BitsOff(10, 256), JxlBitReader.U32Enc.BitsOff(12, 1280), JxlBitReader.U32Enc.BitsOff(16, 5376));
            t.NbDeltas = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(0), JxlBitReader.U32Enc.BitsOff(8, 1), JxlBitReader.U32Enc.BitsOff(10, 257), JxlBitReader.U32Enc.BitsOff(16, 1281));
            t.Predictor = (int)br.ReadBits(4);
        }

        if (t.Id == 2)
        {
            throw new NotSupportedException("JXL Squeeze transform not yet supported.");
        }

        return t;
    }

    private static WpHeader ReadWpHeader(JxlBitReader br)
    {
        if (br.ReadBits(1) != 0)
        {
            return WpHeader.Default();
        }

        int[] p = new int[7];
        for (int i = 0; i < 7; i++)
        {
            p[i] = (int)br.ReadBits(5);
        }

        int[] w = new int[4];
        for (int i = 0; i < 4; i++)
        {
            w[i] = (int)br.ReadBits(4);
        }

        return new WpHeader { P1C = p[0], P2C = p[1], P3Ca = p[2], P3Cb = p[3], P3Cc = p[4], P3Cd = p[5], P3Ce = p[6], W = w };
    }

    /// <summary>
    /// Decodes the global Modular stream (single-group lossless image). <paramref name="startByte"/> is the
    /// byte-aligned start of the LfGlobal section; <paramref name="dcBits"/> accounts for the
    /// DequantMatrices::DecodeDC all-default bit that precedes has_tree even for modular frames.
    /// </summary>
    public static List<JxlChannel> DecodeGlobalModular(byte[] data, int startByte, int w, int h, int nbChans, int bitDepth, int dcBits = 1)
    {
        var br = new JxlBitReader(data, startByte);
        for (int i = 0; i < dcBits; i++)
        {
            br.ReadBits(1);
        }

        bool hasTree = br.ReadBits(1) != 0;
        List<MaNode>? globalTree = null;
        JxlAnsCode? globalCode = null;
        if (hasTree)
        {
            globalTree = DecodeTree(br);
            globalCode = JxlEntropy.DecodeHistograms((globalTree.Count + 1) / 2, br);
        }

        var chans = new List<JxlChannel>();
        for (int i = 0; i < nbChans; i++)
        {
            chans.Add(new JxlChannel(w, h));
        }

        bool useGlobal = br.ReadBool();
        WpHeader wpHdr = ReadWpHeader(br);
        int nt = (int)JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(0), JxlBitReader.U32Enc.Val(1), JxlBitReader.U32Enc.BitsOff(4, 2), JxlBitReader.U32Enc.BitsOff(8, 18));
        var transforms = new List<Transform>();
        for (int i = 0; i < nt; i++)
        {
            transforms.Add(ReadTransform(br));
        }

        int metaCount = 0;
        foreach (Transform t in transforms)
        {
            MetaApply(chans, ref metaCount, t);
        }

        int distMult = 0;
        foreach (JxlChannel c in chans)
        {
            distMult = Math.Max(distMult, c.W);
        }

        List<MaNode> tree;
        JxlAnsCode code;
        if (useGlobal)
        {
            tree = globalTree ?? throw new InvalidOperationException("use_global_tree set but no global tree present.");
            code = globalCode!;
        }
        else
        {
            tree = DecodeTree(br);
            code = JxlEntropy.DecodeHistograms((tree.Count + 1) / 2, br);
        }

        var reader = new JxlAnsReader(code, br, distMult);
        for (int ci = 0; ci < chans.Count; ci++)
        {
            DecodeChannel(reader, tree, code.ContextMap, wpHdr, ci, chans[ci]);
        }

        if (!reader.CheckFinal())
        {
            throw new InvalidOperationException("JXL modular ANS stream did not end in the expected state.");
        }

        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            Transform t = transforms[i];
            if (t.Id == 0)
            {
                InvRct(chans, t.BeginC, t.RctType);
            }
            else if (t.Id == 1)
            {
                InvPalette(chans, t, wpHdr, bitDepth);
            }
        }

        return chans;
    }
}
