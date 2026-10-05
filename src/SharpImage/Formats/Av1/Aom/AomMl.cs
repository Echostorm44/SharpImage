using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>NN_CONFIG (av1/encoder/ml.h): a fully connected ReLU network, weights row-major [output][input] per layer.</summary>
internal sealed class AomNnConfig
{
    public const int MaxHiddenLayers = 10, MaxNodesPerLayer = 128;

    public readonly string Name;
    public readonly int NumInputs, NumOutputs;
    public readonly int[] NumHiddenNodes;
    public readonly float[][] Weights, Bias;   // [num_hidden_layers + 1]

    public AomNnConfig(string name, int numInputs, int numOutputs, int[] numHiddenNodes, float[][] weights, float[][] bias)
    {
        Name = name; NumInputs = numInputs; NumOutputs = numOutputs; NumHiddenNodes = numHiddenNodes;
        Weights = weights; Bias = bias;
    }

    public int NumHiddenLayers => NumHiddenNodes.Length;
}

/// <summary>
/// libaom 3.14.1's neural-net inference (av1/encoder/ml.c) with the float operation order of the kernels its run-time
/// dispatch picks on an AVX2 machine (av1_nn_predict_avx2, av1_nn_fast_softmax_16_sse3), reproduced lane for lane in
/// scalar code, plus the libm calls the ML code makes as the mingw-w64 toolchain resolves them.
/// </summary>
internal static partial class AomMl
{
    // ---- x86 conversion semantics (cvttss2si / cvttsd2si: out of range or NaN gives 0x80000000; .NET saturates) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CvttSs2Si(float f) => f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CvttSd2Si(double d) => d > -2147483649.0 && d < 2147483648.0 ? (int)d : int.MinValue;

    // _mm_max_ps(x, 0) / (x > 0 ? x : 0): +0 for -0 and NaN
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // x > 0 ? x : 0 as maxss(x, 0) (libaom's _mm_max_ps; branch-free on data-random signs)
    private static float Relu(float x) => System.Runtime.Intrinsics.X86.Sse.IsSupported
        ? System.Runtime.Intrinsics.Vector128.ToScalar(System.Runtime.Intrinsics.X86.Sse.MaxScalar(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(x), System.Runtime.Intrinsics.Vector128<float>.Zero))
        : x > 0f ? x : 0f;

    // ---- libm as libaom links it (mingw-w64 msvcrt runtime) ----

    /// <summary>expf: mingw-w64's msvcrt expf is (float)exp((double)x).</summary>
    internal static float Expf(float x) => (float)Math.Exp(x);

    /// <summary>log1pf: mingw-w64's log1pf is x87 code (fldln2; fyl2xp1 below |x| 0.29, else fyl2x of 1 + x). A
    /// mingw-w64 executable (avifenc.exe) runs with the x87 control word 0x37F (64-bit extended precision), so its
    /// result is log1p(x) rounded to a 64-bit mantissa and then to float: the correctly rounded float, except where
    /// log1p(x) lies within half an extended ulp of a float midpoint (then the midpoint, rounded ties-to-even; such
    /// cases occur, e.g. log1pf(0x35400003)). Computed here: a double approximation, and where it lies within a few
    /// double ulps of a float midpoint, an exact 320-bit decision.</summary>
    internal static float Log1pf(float x)
    {
        if (x == 0 || float.IsNaN(x) || float.IsPositiveInfinity(x)) return x;
        if (x <= -1) return x == -1 ? float.NegativeInfinity : float.NaN;
        double d = x;
        double y = Math.Abs(d) < 1e-4
            ? d - d * d * (1.0 / 2) + d * d * d * (1.0 / 3) - d * d * d * d * (1.0 / 4) + d * d * d * d * d * (1.0 / 5)
            : Math.Log(1.0 + d);
        float f = (float)y;
        if ((double)f == y) return f;
        float nb = y > f ? MathF.BitIncrement(f) : MathF.BitDecrement(f);
        double mid = ((double)f + nb) * 0.5;
        double ulp = Math.BitIncrement(Math.Abs(y)) - Math.Abs(y);
        if (Math.Abs(y - mid) > 16 * ulp) return f;
        int c = Log1pCompare(d, mid);   // sign(log1p(d) - mid), 0 within half an extended ulp
        if (c == 0) return (float)mid;  // the x87 result is the midpoint itself: ties to even
        return (c > 0) == (nb > f) ? nb : f;
    }

    // sign(log(1 + d) - m), or 0 when log(1 + d) is within half a 64-bit-mantissa ulp of m, so that the x87's extended
    // result is m itself (320-bit fixed point: log(1 + d) = e ln2 + 2 atanh((r - 1) / (r + 1)), r in [0.75, 1.5])
    private static int Log1pCompare(double d, double m)
    {
        const int P = 320;
        var one = System.Numerics.BigInteger.One << P;
        // 1 + d = num / 2^s exactly
        long bits = BitConverter.DoubleToInt64Bits(d);
        int bexp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (bexp == 0) bexp = 1; else mant |= 1L << 52;
        int exp2 = bexp - 1075;                          // |d| = mant * 2^exp2
        System.Numerics.BigInteger num = mant;
        if (d < 0) num = -num;
        int s;
        if (exp2 >= 0) { num = (num << exp2) + 1; s = 0; }
        else { s = -exp2; num += System.Numerics.BigInteger.One << s; }
        // a = 2^e * r, r in [0.75, 1.5)
        int e = (int)(num.GetBitLength() - 1) - s;
        // r * 2^P = num * 2^(P - s - e)
        int sh = P - s - e;
        var r = sh >= 0 ? num << sh : num >> -sh;
        if (r * 2 >= one * 3) { e++; r >>= 1; }
        // atanh is odd: sum the series on |z| (a negative term would never shift down to zero)
        var zn = ((r - one) << P) / (r + one);
        var z = System.Numerics.BigInteger.Abs(zn);
        var z2 = (z * z) >> P;
        System.Numerics.BigInteger sum = 0, term = z;
        for (int k = 0; !term.IsZero; k++)
        {
            sum += term / (2 * k + 1);
            term = (term * z2) >> P;
        }
        if (zn.Sign < 0) sum = -sum;
        var l = e * Ln2Fixed320.Value + 2 * sum;
        // m * 2^P
        long mb = BitConverter.DoubleToInt64Bits(m);
        int me = (int)((mb >> 52) & 0x7FF);
        long mm = mb & 0xFFFFFFFFFFFFFL;
        if (me == 0) me = 1; else mm |= 1L << 52;
        System.Numerics.BigInteger mv = mm;
        int msh = me - 1075 + P;
        mv = msh >= 0 ? mv << msh : mv >> -msh;
        if (m < 0) mv = -mv;
        var diff = l - mv;
        // half an extended ulp at m: 2^(ilogb(m) - 64), in the 2^-P fixed point
        int halfUlpShift = Math.ILogB(m) - 64 + P;
        if (halfUlpShift >= 0 && System.Numerics.BigInteger.Abs(diff) <= System.Numerics.BigInteger.One << halfUlpShift) return 0;
        return diff.Sign;
    }

    // ln 2 * 2^320 (2 atanh(1/3))
    private static readonly Lazy<System.Numerics.BigInteger> Ln2Fixed320 = new(() =>
    {
        var term = (System.Numerics.BigInteger.One << 320) / 3;
        System.Numerics.BigInteger sum = 0;
        for (int k = 0; !term.IsZero; k++)
        {
            sum += term / (2 * k + 1);
            term /= 9;
        }
        return 2 * sum;
    });

    // ---- av1/encoder/ml.c ----

    /// <summary>av1_nn_output_prec_reduce.</summary>
    internal static void NnOutputPrecReduce(Span<float> output, int numOutput)
    {
        const int precBits = 9, prec = 1 << precBits;
        const float invPrec = (float)(1.0 / prec);
        for (int i = 0; i < numOutput; i++)
            output[i] = CvttSd2Si((double)(output[i] * prec) + 0.5) * invPrec;
    }

    /// <summary>av1_nn_predict (dispatched: av1_nn_predict_avx2, which falls back to the SSE3 4-wide helpers for the
    /// non-multiple-of-8 input remainder).</summary>
    internal static void NnPredict(ReadOnlySpan<float> inputNodes, AomNnConfig cfg, bool reducePrec, Span<float> output)
    {
        Span<float> buf0 = stackalloc float[AomNnConfig.MaxNodesPerLayer];
        Span<float> buf1 = stackalloc float[AomNnConfig.MaxNodesPerLayer];
        int bufIndex = 0;
        int numInputs = cfg.NumInputs;
        scoped ReadOnlySpan<float> input = inputNodes;
        scoped Span<float> outputNodes;
        int nh = cfg.NumHiddenLayers;
        for (int layer = 0; layer <= nh; layer++)
        {
            bool isOutputLayer = layer == nh;
            outputNodes = isOutputLayer ? output : bufIndex == 0 ? buf0 : buf1;
            int numOutputs = isOutputLayer ? cfg.NumOutputs : cfg.NumHiddenNodes[layer];
            PropagateLayerAvx2(input, numInputs, cfg.Weights[layer], cfg.Bias[layer], numOutputs, isOutputLayer, outputNodes);
            input = outputNodes;
            numInputs = numOutputs;
            bufIndex = 1 - bufIndex;
        }
        if (reducePrec) NnOutputPrecReduce(output, cfg.NumOutputs);
    }

    // One layer of av1_nn_predict_avx2.
    private static void PropagateLayerAvx2(ReadOnlySpan<float> input, int numInputs, float[] w, float[] bias, int numOutputs,
        bool isOutputLayer, Span<float> output)
    {
        if (numInputs % 8 == 0)
        {
            PropagateInputMultipleOf8(input, w, bias, numInputs, numInputs, isOutputLayer, numOutputs, output);
            return;
        }
        int inMul8 = numInputs / 8;
        int numInputsToProcess = inMul8 * 8;
        bool biasIsConsidered = false;
        if (inMul8 != 0)
        {
            PropagateInputMultipleOf8(input, w, bias, numInputsToProcess, numInputs, isOutputLayer, numOutputs, output);
            biasIsConsidered = true;
        }
        int inputRemaining = numInputs % 8;
        for (int o = 0; o < numOutputs; o++)
        {
            float v = biasIsConsidered ? output[o] : bias[o];
            int row = o * numInputs;
            if (inputRemaining % 4 == 0)
            {
                // av1_nn_propagate_4to8_sse3 / 4to4 / 4to1: every one adds the hadd tree ((p0 + p1) + (p2 + p3))
                for (int i = numInputsToProcess; i < numInputs; i += 4)
                {
                    float p0 = input[i] * w[row + i], p1 = input[i + 1] * w[row + i + 1];
                    float p2 = input[i + 2] * w[row + i + 2], p3 = input[i + 3] * w[row + i + 3];
                    v += (p0 + p1) + (p2 + p3);
                }
            }
            else
            {
                for (int i = numInputsToProcess; i < numInputs; i++) v += input[i] * w[row + i];
            }
            if (!isOutputLayer) v = Relu(v);
            output[o] = v;
        }
    }

    // nn_propagate_input_multiple_of_8 (ml_avx2.c)
    private static void PropagateInputMultipleOf8(ReadOnlySpan<float> input, float[] w, float[] bias, int numInputsToProcess,
        int totNumInputs, bool isOutputLayer, int numOutputs, Span<float> output)
    {
        bool clip = !isOutputLayer && numInputsToProcess == totNumInputs;
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported && input.Length >= numInputsToProcess && output.Length >= numOutputs
            && w.Length >= (numOutputs - 1) * totNumInputs + numInputsToProcess && bias.Length >= numOutputs)
        {
            PropagateInputMultipleOf8Avx(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(input),
                ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(w),
                ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(bias), numInputsToProcess, totNumInputs, clip,
                numOutputs, ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output));
            return;
        }
        if (numOutputs % 4 == 0)
        {
            // nn_propagate_8to8 / 8to4: per output, the 8 products of each input chunk reduce through the hadd
            // tree as ((p0+p1)+(p2+p3)) + ((p4+p5)+(p6+p7)) into a zeroed accumulator; the bias is added last.
            for (int o = 0; o < numOutputs; o++)
            {
                int row = o * totNumInputs;
                float acc = 0f;
                for (int i = 0; i < numInputsToProcess; i += 8)
                {
                    float p0 = input[i] * w[row + i], p1 = input[i + 1] * w[row + i + 1];
                    float p2 = input[i + 2] * w[row + i + 2], p3 = input[i + 3] * w[row + i + 3];
                    float p4 = input[i + 4] * w[row + i + 4], p5 = input[i + 5] * w[row + i + 5];
                    float p6 = input[i + 6] * w[row + i + 6], p7 = input[i + 7] * w[row + i + 7];
                    acc += ((p0 + p1) + (p2 + p3)) + ((p4 + p5) + (p6 + p7));
                }
                float v = acc + bias[o];
                output[o] = clip ? Relu(v) : v;
            }
        }
        else
        {
            // nn_propagate_8to1: 8 lane accumulators, folded high onto low, then (s2+s3) + (s0+s1), added to the bias
            Span<float> lane = stackalloc float[8];
            for (int o = 0; o < numOutputs; o++)
            {
                int row = o * totNumInputs;
                lane.Clear();
                for (int i = 0; i < numInputsToProcess; i += 8)
                    for (int k = 0; k < 8; k++) lane[k] += input[i + k] * w[row + i + k];
                float s0 = lane[0] + lane[4], s1 = lane[1] + lane[5], s2 = lane[2] + lane[6], s3 = lane[3] + lane[7];
                float v = bias[o] + ((s2 + s3) + (s0 + s1));
                output[o] = clip ? Relu(v) : v;
            }
        }
    }

    // nn_propagate_8to8 / 8to4 / 8to1 (ml_avx2.c) with libaom's own reductions (_mm256_hadd_ps trees, the lane folds),
    // so every float sum is formed in the same order
    private static void PropagateInputMultipleOf8Avx(ref float input, ref float w, ref float bias, int numInputsToProcess,
        int totNumInputs, bool clip, int numOutputs, ref float output)
    {
        if (numOutputs % 8 == 0)
        {
            for (int o = 0; o < numOutputs; o += 8)
            {
                var acc = Vector256<float>.Zero;
                for (int i = 0; i < numInputsToProcess; i += 8)
                {
                    var x = Vector256.LoadUnsafe(ref input, (nuint)i);
                    ref float wr = ref Unsafe.Add(ref w, i + o * totNumInputs);
                    var h0 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr), x * Vector256.LoadUnsafe(ref wr, (nuint)totNumInputs));
                    var h1 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr, (nuint)(2 * totNumInputs)), x * Vector256.LoadUnsafe(ref wr, (nuint)(3 * totNumInputs)));
                    var h2 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr, (nuint)(4 * totNumInputs)), x * Vector256.LoadUnsafe(ref wr, (nuint)(5 * totNumInputs)));
                    var h3 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr, (nuint)(6 * totNumInputs)), x * Vector256.LoadUnsafe(ref wr, (nuint)(7 * totNumInputs)));
                    var hh0 = Avx.HorizontalAdd(h0, h1);
                    var hh1 = Avx.HorizontalAdd(h2, h3);
                    acc += Avx.Permute2x128(hh0, hh1, 0x20) + Avx.Permute2x128(hh0, hh1, 0x31);
                }
                acc += Vector256.LoadUnsafe(ref bias, (nuint)o);
                if (clip) acc = Avx.Max(acc, Vector256<float>.Zero);
                acc.StoreUnsafe(ref output, (nuint)o);
            }
        }
        else if (numOutputs % 4 == 0)
        {
            for (int o = 0; o < numOutputs; o += 4)
            {
                var acc = Vector128<float>.Zero;
                for (int i = 0; i < numInputsToProcess; i += 8)
                {
                    var x = Vector256.LoadUnsafe(ref input, (nuint)i);
                    ref float wr = ref Unsafe.Add(ref w, i + o * totNumInputs);
                    var h0 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr), x * Vector256.LoadUnsafe(ref wr, (nuint)totNumInputs));
                    var h1 = Avx.HorizontalAdd(x * Vector256.LoadUnsafe(ref wr, (nuint)(2 * totNumInputs)), x * Vector256.LoadUnsafe(ref wr, (nuint)(3 * totNumInputs)));
                    var sum = Avx.HorizontalAdd(h0, h1);
                    acc += sum.GetLower() + sum.GetUpper();
                }
                acc += Vector128.LoadUnsafe(ref bias, (nuint)o);
                if (clip) acc = Sse.Max(acc, Vector128<float>.Zero);
                acc.StoreUnsafe(ref output, (nuint)o);
            }
        }
        else
        {
            for (int o = 0; o < numOutputs; o++)
            {
                var acc = Vector256<float>.Zero;
                for (int i = 0; i < numInputsToProcess; i += 8)
                    acc += Vector256.LoadUnsafe(ref input, (nuint)i) * Vector256.LoadUnsafe(ref w, (nuint)(i + o * totNumInputs));
                var s0 = acc.GetLower() + acc.GetUpper();
                var s1 = Sse3.HorizontalAdd(s0, s0);
                var tot = Sse.Shuffle(s1, s1, 0x99) + s1;
                float v = Unsafe.Add(ref bias, o) + tot.ToScalar();
                Unsafe.Add(ref output, o) = clip ? Relu(v) : v;
            }
        }
    }

    /// <summary>av1_nn_softmax (C; expf from the runtime).</summary>
    internal static void NnSoftmax(ReadOnlySpan<float> input, Span<float> output, int n)
    {
        float maxInput = input[0];
        for (int i = 1; i < n; i++) maxInput = maxInput > input[i] ? maxInput : input[i];
        float sumOut = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = input[i] - maxInput;
            float normalized = d > -10f ? d : -10f;
            output[i] = Expf(normalized);
            sumOut += output[i];
        }
        for (int i = 0; i < n; i++) output[i] /= sumOut;
    }

    // _mm_max_ps(a, b): a > b ? a : b
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float MaxPs(float a, float b) => a > b ? a : b;

    // approx_exp (ml_sse3.c): Schraudolph with _mm_cvtps_epi32 (round to nearest even)
    private static float ApproxExpSse3(float y)
    {
        const float a = (1 << 23) / 0.69314718056f;
        const int offset = 127 * (1 << 23) - 60801;
        float t = y * a;
        int i = t >= -2147483648f && t < 2147483648f ? (int)MathF.Round(t, MidpointRounding.ToEven) : int.MinValue;
        return BitConverter.Int32BitsToSingle(unchecked(i + offset));
    }

    /// <summary>av1_nn_fast_softmax_16 (dispatched: av1_nn_fast_softmax_16_sse3), 4 lanes x 4 registers. In place is fine.</summary>
    internal static void NnFastSoftmax16(ReadOnlySpan<float> input, Span<float> output)
    {
        Span<float> v = stackalloc float[16];
        input[..16].CopyTo(v);
        Span<float> m = stackalloc float[4], t = stackalloc float[4];
        for (int k = 0; k < 4; k++) m[k] = MaxPs(MaxPs(v[k], v[4 + k]), MaxPs(v[8 + k], v[12 + k]));
        for (int k = 0; k < 4; k++) t[k] = MaxPs(m[k], m[k ^ 2]);   // shuffle 0x4e
        for (int k = 0; k < 4; k++) m[k] = MaxPs(t[k], t[k ^ 1]);   // shuffle 0xb1
        for (int i = 0; i < 16; i++) v[i] = ApproxExpSse3(MaxPs(v[i] - m[i & 3], -10f));
        Span<float> s = stackalloc float[4];
        for (int k = 0; k < 4; k++) s[k] = ((v[k] + v[4 + k]) + v[8 + k]) + v[12 + k];
        for (int k = 0; k < 4; k++) t[k] = s[k] + s[k ^ 2];
        for (int k = 0; k < 4; k++) s[k] = t[k] + t[k ^ 1];
        for (int i = 0; i < 16; i++) output[i] = v[i] / s[i & 3];
    }

    // ---- av1/encoder/sorting_network.h ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(Span<float> k, Span<int> v, int i, int j)
    {
        bool ge = k[i] >= k[j];
        float maxf = ge ? k[i] : k[j], minf = ge ? k[j] : k[i];
        int maxi = ge ? v[i] : v[j], mini = ge ? v[j] : v[i];
        k[i] = maxf; k[j] = minf; v[i] = maxi; v[j] = mini;
    }

    private static readonly byte[] SortNet16 =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0, 2, 1, 3, 4, 6, 5, 7, 8, 10, 9, 11, 12, 14, 13, 15,
        1, 2, 5, 6, 0, 4, 3, 7, 9, 10, 13, 14, 8, 12, 11, 15, 1, 5, 2, 6, 9, 13, 10, 14, 0, 8, 7, 15, 1, 4, 3, 6,
        9, 12, 11, 14, 2, 4, 3, 5, 10, 12, 11, 13, 1, 9, 6, 14, 3, 4, 11, 12, 1, 8, 2, 10, 5, 13, 7, 14, 3, 11, 2, 8,
        4, 12, 7, 13, 3, 10, 5, 12, 3, 9, 6, 12, 3, 8, 7, 12, 5, 9, 6, 10, 4, 8, 7, 11, 5, 8, 7, 10, 6, 8, 7, 9, 7, 8,
    ];

    private static readonly byte[] SortNet8 =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 1, 2, 5, 6, 0, 4, 3, 7, 1, 5, 2, 6, 1, 4, 3, 6, 2, 4, 3, 5, 3, 4,
    ];

    /// <summary>av1_sort_fi32_16: sorts 16 keys (and their values) in descending key order.</summary>
    internal static void SortFi32_16(Span<float> k, Span<int> v)
    {
        for (int i = 0; i < SortNet16.Length; i += 2) Swap(k, v, SortNet16[i], SortNet16[i + 1]);
    }

    /// <summary>av1_sort_fi32_8.</summary>
    internal static void SortFi32_8(Span<float> k, Span<int> v)
    {
        for (int i = 0; i < SortNet8.Length; i += 2) Swap(k, v, SortNet8[i], SortNet8[i + 1]);
    }
}
