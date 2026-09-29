using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

/// <summary>
/// The restoration search's self-guided filter: one radius over a whole unit, as t = flt - (u &lt;&lt; 4) at the filter's 4
/// fractional bits (dav1d's finish-filter output), so the search weighs candidates with the decoder's exact
/// out = clip(u + ((w0·t0 + w1·t1 + 2^10) &gt;&gt; 11)). Same arithmetic as Av1LoopRestoration.Sgr3x3 / Sgr5x5 (box sums,
/// sgr_calc_row_ab, finish filter; A/B of the 5x5 on the odd rows from the unit top) with AVX2 rows, on a unit padded
/// 3 pixels each side (replicated where the kernels have no neighbour).
/// </summary>
internal static unsafe class Av1LrSgrFast
{
    private static readonly int[] XByX = Array.ConvertAll(Av1LoopRestoration.SgrXByX, b => (int)b);

    private sealed class Buf { public int[] Hs = [], Hq = [], Xa = [], Bv = []; }
    [ThreadStatic] private static Buf? t_buf;
    private static int[] Get(ref int[] a, int n) { if (a.Length < n) a = new int[n]; return a; }

    internal static bool Supported => Avx2.IsSupported;

    /// <summary>Row stride of the unit arrays (A/B column j = -1..uw at j + 1, plus vector slack); the padded input's
    /// stride is Stride + 8.</summary>
    internal static int Stride(int uw) => ((uw + 2 + 7) & ~7) + 8;

    /// <summary>Pads a unit working copy (stride <paramref name="stride"/>, <paramref name="rightCols"/> real columns
    /// past the right edge, left[] = the 4 columns left of it when <paramref name="haveLeft"/>) into p:
    /// (uh + 6) rows of Stride(uw) + 8 ints, the unit at (3, 3).</summary>
    internal static void Pad(int[] p, ushort[] buf, int stride, ushort[] left, bool haveLeft, int rightCols, int uw, int uh)
    {
        int pw = Stride(uw) + 8, last = uw + rightCols - 1;
        for (int y = 0; y < uh + 6; y++)
        {
            int sy = Math.Clamp(y - 3, 0, uh - 1), so = sy * stride, po = y * pw;
            for (int k = 0; k < 3; k++) p[po + k] = haveLeft ? left[sy * 4 + 1 + k] : buf[so];
            for (int x = 0; x <= last; x++) p[po + 3 + x] = buf[so + x];
            int e = buf[so + last];
            for (int x = last + 1; x < pw - 3; x++) p[po + 3 + x] = e;
        }
    }

    /// <summary>The projection's normal equations over the unit: m = { Σa², Σab, Σb², Σa·e, Σb·e } of the filter
    /// outputs a, b (b null: its terms 0) and e = src - u (sv: the source unit at Stride(uw)). Exact (integer sums
    /// well inside a double's mantissa).</summary>
    internal static void Moments(int[] p, int[] sv, int[] ta, int[]? tb, int uw, int uh, Span<double> m)
    {
        int S = Stride(uw), pw = S + 8, xv = uw & ~7;
        var aa = Vector256<double>.Zero; var ab = aa; var bb = aa; var ae = aa; var be = aa;
        long saa = 0, sab = 0, sbb = 0, sae = 0, sbe = 0;
        fixed (int* pP = p, pS = sv, pA = ta, pB = tb)
        {
            for (int y = 0; y < uh; y++)
            {
                int* u = pP + (y + 3) * pw + 3, s = pS + y * S, a = pA + y * S, b = pB + y * S;
                int x = 0;
                for (; x < xv; x += 8)
                {
                    var e = Avx.LoadVector256(s + x) - Avx.LoadVector256(u + x);
                    var va = Avx.LoadVector256(a + x);
                    var e0 = Avx.ConvertToVector256Double(e.GetLower()); var e1 = Avx.ConvertToVector256Double(e.GetUpper());
                    var a0 = Avx.ConvertToVector256Double(va.GetLower()); var a1 = Avx.ConvertToVector256Double(va.GetUpper());
                    aa += a0 * a0 + a1 * a1; ae += a0 * e0 + a1 * e1;
                    if (pB != null)
                    {
                        var vb = Avx.LoadVector256(b + x);
                        var b0 = Avx.ConvertToVector256Double(vb.GetLower()); var b1 = Avx.ConvertToVector256Double(vb.GetUpper());
                        ab += a0 * b0 + a1 * b1; bb += b0 * b0 + b1 * b1; be += b0 * e0 + b1 * e1;
                    }
                }
                for (; x < uw; x++)
                {
                    long e = s[x] - u[x], va = a[x], vb = pB != null ? b[x] : 0;
                    saa += va * va; sab += va * vb; sbb += vb * vb; sae += va * e; sbe += vb * e;
                }
            }
        }
        m[0] = Vector256.Sum(aa) + saa; m[1] = Vector256.Sum(ab) + sab; m[2] = Vector256.Sum(bb) + sbb;
        m[3] = Vector256.Sum(ae) + sae; m[4] = Vector256.Sum(be) + sbe;
    }

    /// <summary>SSE against the source of the decoder's output clip(u + ((wa·a + wb·b + 2^10) &gt;&gt; 11)) (a / b null:
    /// weight 0).</summary>
    internal static long Sse(int[] p, int[] sv, int[]? ta, int wa, int[]? tb, int wb, int uw, int uh, int pixelMax)
    {
        int S = Stride(uw), pw = S + 8, xv = uw & ~7;
        if (ta == null) wa = 0;
        if (tb == null) wb = 0;
        var waV = Vector256.Create(wa); var wbV = Vector256.Create(wb); var rnd = Vector256.Create(1 << 10);
        var maxV = Vector256.Create(pixelMax);
        long sum = 0;
        fixed (int* pP = p, pS = sv, pA = ta, pB = tb)
        {
            for (int y = 0; y < uh; y++)
            {
                int* u = pP + (y + 3) * pw + 3, s = pS + y * S, a = pA + y * S, b = pB + y * S;
                var acc = Vector256<int>.Zero;   // <= 48 lanes-worth of 12-bit squares per row: within int
                int x = 0;
                for (; x < xv; x += 8)
                {
                    var v = rnd;
                    if (pA != null) v += waV * Avx.LoadVector256(a + x);
                    if (pB != null) v += wbV * Avx.LoadVector256(b + x);
                    var o = Avx2.Min(Avx2.Max(Avx.LoadVector256(u + x) + Avx2.ShiftRightArithmetic(v, 11), Vector256<int>.Zero), maxV);
                    var d = o - Avx.LoadVector256(s + x);
                    acc += d * d;
                }
                long rs = 0;
                for (; x < uw; x++)
                {
                    int v = 1 << 10;
                    if (pA != null) v += wa * a[x];
                    if (pB != null) v += wb * b[x];
                    int d = Math.Clamp(u[x] + (v >> 11), 0, pixelMax) - s[x];
                    rs += (long)d * d;
                }
                for (int k = 0; k < 8; k++) rs += acc.GetElement(k);
                sum += rs;
            }
        }
        return sum;
    }

    /// <summary>t = flt - (u &lt;&lt; 4) of the radius-<paramref name="r"/> filter with strength <paramref name="s"/> into
    /// t (uh rows of Stride(uw)).</summary>
    [SkipLocalsInit]
    internal static void Flt(int[] p, int uw, int uh, int r, int s, int bitDepth, int[] t)
    {
        int S = Stride(uw), pw = S + 8;
        var b = t_buf ??= new Buf();
        var hs = Get(ref b.Hs, (uh + 6) * S); var hq = Get(ref b.Hq, (uh + 6) * S);
        var xa = Get(ref b.Xa, (uh + 2) * S); var bv = Get(ref b.Bv, (uh + 2) * S);
        if (t.Length < uh * S) throw new ArgumentException("t too small");
        int bd8 = bitDepth - 8;
        var nV = Vector256.Create(r == 1 ? 9 : 25);
        var sV = Vector256.Create(s);
        var oneV = Vector256.Create(r == 1 ? 455 : 164);
        var r19 = Vector256.Create(1 << 19); var c255 = Vector256.Create(255); var r11 = Vector256.Create(1 << 11);
        var ra = Vector256.Create((1 << (2 * bd8)) >> 1); var rb = Vector256.Create((1 << bd8) >> 1);
        byte sha = (byte)(2 * bd8), shb = (byte)bd8;
        fixed (int* pP = p, pHs = hs, pHq = hq, pXa = xa, pBv = bv, pT = t, pTab = XByX)
        {
            // horizontal (2r + 1)-sums of the padded rows the A/B boxes read (column c = unit x + 1)
            for (int y = 2 - r; y <= uh + 3 + r; y++)
            {
                int* src = pP + y * pw + 2 - r;
                int* os = pHs + y * S, oq = pHq + y * S;
                for (int c = 0; c < S; c += 8)
                {
                    var v0 = Avx.LoadVector256(src + c); var v1 = Avx.LoadVector256(src + c + 1); var v2 = Avx.LoadVector256(src + c + 2);
                    var sum = v0 + v1 + v2; var sq = v0 * v0 + v1 * v1 + v2 * v2;
                    if (r == 2)
                    {
                        var v3 = Avx.LoadVector256(src + c + 3); var v4 = Avx.LoadVector256(src + c + 4);
                        sum += v3 + v4; sq += v3 * v3 + v4 * v4;
                    }
                    Avx.Store(os + c, sum); Avx.Store(oq + c, sq);
                }
            }
            // A/B rows i = -1..uh (5x5: the odd ones), at row i + 1
            for (int i = -1; i <= uh; i += r)
            {
                int yc = i + 3;
                int* xo = pXa + (i + 1) * S, bo = pBv + (i + 1) * S;
                int* s0 = pHs + (yc - 1) * S, s1 = s0 + S, s2 = s1 + S;
                int* q0 = pHq + (yc - 1) * S, q1 = q0 + S, q2 = q1 + S;
                for (int c = 0; c < S; c += 8)
                {
                    var bs = Avx.LoadVector256(s0 + c) + Avx.LoadVector256(s1 + c) + Avx.LoadVector256(s2 + c);
                    var a = Avx.LoadVector256(q0 + c) + Avx.LoadVector256(q1 + c) + Avx.LoadVector256(q2 + c);
                    if (r == 2)
                    {
                        bs += Avx.LoadVector256(s0 - S + c) + Avx.LoadVector256(s2 + S + c);
                        a += Avx.LoadVector256(q0 - S + c) + Avx.LoadVector256(q2 + S + c);
                    }
                    var aa = a; var bb = bs;
                    if (bd8 > 0) { aa = Avx2.ShiftRightLogical(aa + ra, sha); bb = Avx2.ShiftRightLogical(bb + rb, shb); }
                    var pv = Avx2.Max(aa * nV - bb * bb, Vector256<int>.Zero);
                    var z = Avx2.Min(Avx2.ShiftRightLogical(pv * sV + r19, 20), c255);
                    var x = Avx2.GatherVector256(pTab, z, 4);
                    Avx.Store(xo + c, x);
                    Avx.Store(bo + c, Avx2.ShiftRightLogical(x * bs * oneV + r11, 12));
                }
            }
            // finish filter
            var c256 = Vector256.Create(256); var c128 = Vector256.Create(128);
            for (int y = 0; y < uh; y++)
            {
                int* u = pP + (y + 3) * pw + 3;
                int* to = pT + y * S;
                if (r == 1)
                {
                    int* x0 = pXa + y * S + 1, x1 = x0 + S, x2 = x1 + S;
                    int* b0 = pBv + y * S + 1, b1 = b0 + S, b2 = b1 + S;
                    for (int x = 0; x < uw; x += 8)
                    {
                        var a4 = Avx.LoadVector256(x1 + x) + Avx.LoadVector256(x1 + x - 1) + Avx.LoadVector256(x1 + x + 1)
                            + Avx.LoadVector256(x0 + x) + Avx.LoadVector256(x2 + x);
                        var a3 = Avx.LoadVector256(x0 + x - 1) + Avx.LoadVector256(x2 + x - 1) + Avx.LoadVector256(x0 + x + 1) + Avx.LoadVector256(x2 + x + 1);
                        var b4 = Avx.LoadVector256(b1 + x) + Avx.LoadVector256(b1 + x - 1) + Avx.LoadVector256(b1 + x + 1)
                            + Avx.LoadVector256(b0 + x) + Avx.LoadVector256(b2 + x);
                        var b3 = Avx.LoadVector256(b0 + x - 1) + Avx.LoadVector256(b2 + x - 1) + Avx.LoadVector256(b0 + x + 1) + Avx.LoadVector256(b2 + x + 1);
                        var av = Avx2.ShiftLeftLogical(a4, 2) + Avx2.ShiftLeftLogical(a3, 1) + a3;
                        var bw = Avx2.ShiftLeftLogical(b4, 2) + Avx2.ShiftLeftLogical(b3, 1) + b3;
                        Avx.Store(to + x, Avx2.ShiftRightArithmetic(bw - av * Avx.LoadVector256(u + x) + c256, 9));
                    }
                }
                else if ((y & 1) == 0)
                {
                    int* x0 = pXa + y * S + 1, x2 = x0 + 2 * S;
                    int* b0 = pBv + y * S + 1, b2 = b0 + 2 * S;
                    for (int x = 0; x < uw; x += 8)
                    {
                        var a6 = Avx.LoadVector256(x0 + x) + Avx.LoadVector256(x2 + x);
                        var a5 = Avx.LoadVector256(x0 + x - 1) + Avx.LoadVector256(x2 + x - 1) + Avx.LoadVector256(x0 + x + 1) + Avx.LoadVector256(x2 + x + 1);
                        var b6 = Avx.LoadVector256(b0 + x) + Avx.LoadVector256(b2 + x);
                        var b5 = Avx.LoadVector256(b0 + x - 1) + Avx.LoadVector256(b2 + x - 1) + Avx.LoadVector256(b0 + x + 1) + Avx.LoadVector256(b2 + x + 1);
                        var av = Avx2.ShiftLeftLogical(a6, 2) + Avx2.ShiftLeftLogical(a6, 1) + Avx2.ShiftLeftLogical(a5, 2) + a5;
                        var bw = Avx2.ShiftLeftLogical(b6, 2) + Avx2.ShiftLeftLogical(b6, 1) + Avx2.ShiftLeftLogical(b5, 2) + b5;
                        Avx.Store(to + x, Avx2.ShiftRightArithmetic(bw - av * Avx.LoadVector256(u + x) + c256, 9));
                    }
                }
                else
                {
                    int* x1 = pXa + (y + 1) * S + 1, b1 = pBv + (y + 1) * S + 1;
                    for (int x = 0; x < uw; x += 8)
                    {
                        var a6 = Avx.LoadVector256(x1 + x);
                        var a5 = Avx.LoadVector256(x1 + x - 1) + Avx.LoadVector256(x1 + x + 1);
                        var b6 = Avx.LoadVector256(b1 + x);
                        var b5 = Avx.LoadVector256(b1 + x - 1) + Avx.LoadVector256(b1 + x + 1);
                        var av = Avx2.ShiftLeftLogical(a6, 2) + Avx2.ShiftLeftLogical(a6, 1) + Avx2.ShiftLeftLogical(a5, 2) + a5;
                        var bw = Avx2.ShiftLeftLogical(b6, 2) + Avx2.ShiftLeftLogical(b6, 1) + Avx2.ShiftLeftLogical(b5, 2) + b5;
                        Avx.Store(to + x, Avx2.ShiftRightArithmetic(bw - av * Avx.LoadVector256(u + x) + c128, 8));
                    }
                }
            }
        }
    }
}
