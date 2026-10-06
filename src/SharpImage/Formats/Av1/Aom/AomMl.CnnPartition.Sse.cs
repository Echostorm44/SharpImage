using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using F4 = System.Runtime.Intrinsics.Vector128<float>;

namespace SharpImage.Formats.Av1;

// The partition CNN's AVX2 layers in 128-bit lanes (no AVX2: the default Native AOT instruction set). Every AVX2 lane
// is the same sequence of IEEE multiplies and adds here (computed as two 4-lane halves), and the horizontal adds pair
// the same lanes, so the outputs are bit-identical to the AVX2 kernels'.
internal static partial class AomMl
{
    // cnn_convolve_no_maxpool_padding_valid_5x5_avx2 in SSE3
    internal static void CnnConvolve5x5Sse(float[] input, int inWidth, int inHeight, int inStride, int inChannels,
        int outChannels, float[] weights, float[] bias, float[] output, int outStride)
    {
        const int fw = 5, skip = 4;
        int cstep = inChannels * outChannels;
        if ((long)inStride * inHeight * inChannels > input.Length || output.Length < outChannels * outStride * outStride)
            throw new ArgumentException("cnn buffers");
        ref float in0 = ref MemoryMarshal.GetArrayDataReference(input);
        Span<float> wbuf = stackalloc float[5 * 8];
        // per row r: sw_r = (w0 w1 w2 w3 | w4 w0 w1 w2), sw_{5+r} = (w3 w4 w0 w1 | w2 w3 w4 w0) as low / high halves
        Span<F4> swl = stackalloc F4[10];
        Span<F4> swh = stackalloc F4[10];
        for (int i = 0; i < outChannels; i++)
        {
            float outChBias = bias[i];
            for (int k = 0; k < inChannels; k++)
            {
                wbuf.Clear();
                int off = k * outChannels + i;
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 5; col++) { wbuf[row * 8 + col] = weights[off]; off += cstep; }
                for (int row = 0; row < 5; row++)
                {
                    float w0 = wbuf[row * 8], w1 = wbuf[row * 8 + 1], w2 = wbuf[row * 8 + 2], w3 = wbuf[row * 8 + 3], w4 = wbuf[row * 8 + 4];
                    swl[row] = Vector128.Create(w0, w1, w2, w3); swh[row] = Vector128.Create(w4, w0, w1, w2);
                    swl[5 + row] = Vector128.Create(w3, w4, w0, w1); swh[5 + row] = Vector128.Create(w2, w3, w4, w0);
                }
                float w04 = wbuf[4], w14 = wbuf[12], w24 = wbuf[20], w34 = wbuf[28], w44 = wbuf[36];
                int plane = k * inStride * inHeight;
                for (int h = 0, u = 0; h < inHeight - fw + 1; h += skip, ++u)
                {
                    int outH = i * outStride * outStride + u * outStride;
                    int v = 0, x = 0, rem = inWidth;
                    while (rem >= skip * 2 + fw)
                    {
                        int p = plane + h * inStride + x;
                        ref float r0 = ref Unsafe.Add(ref in0, p);
                        // acc0 = (block01 inputs) * sw_r, acc1 = (block12 inputs at +7) * sw_{5+r}, rows added in order
                        F4 a0l = F4.Zero, a0h = F4.Zero, a1l = F4.Zero, a1h = F4.Zero;
                        for (int r = 0; r < 5; r++)
                        {
                            F4 x0 = Vector128.LoadUnsafe(ref r0);                       // in[0..3]
                            F4 x4 = Vector128.LoadUnsafe(ref r0, 4);                    // in[4..7]
                            F4 y7 = Vector128.LoadUnsafe(ref r0, 7);                    // in[7..10]
                            F4 y10 = Vector128.LoadUnsafe(ref r0, 10);                  // in[10..13]
                            F4 b01h = Vector128.Shuffle(x4, Vector128.Create(0, 0, 1, 2));   // in[4, 4, 5, 6]
                            F4 b12l = Vector128.Shuffle(y7, Vector128.Create(0, 1, 1, 2));   // in7 + (0, 1, 1, 2)
                            F4 b12h = Vector128.Create(y10.GetElement(0), y10.GetElement(1), y10.GetElement(2), y7.GetElement(0)); // in7 + (3, 4, 5, 0)
                            a0l = Sse.Add(Sse.Multiply(x0, swl[r]), a0l);
                            a0h = Sse.Add(Sse.Multiply(b01h, swh[r]), a0h);
                            a1l = Sse.Add(Sse.Multiply(b12l, swl[5 + r]), a1l);
                            a1h = Sse.Add(Sse.Multiply(b12h, swh[5 + r]), a1h);
                            r0 = ref Unsafe.Add(ref r0, inStride);
                        }
                        // _mm256_hadd_ps(acc0, acc1): low = (a0l pairs, a1l pairs), high = (a0h pairs, a1h pairs)
                        F4 al = Sse3.HorizontalAdd(a0l, a1l), ah = Sse3.HorizontalAdd(a0h, a1h);
                        F4 t2 = Sse.Add(al, a0h);
                        F4 t3 = Sse.Add(a0h, ah);
                        F4 t4 = Sse.Add(a1h, ah);
                        output[outH + v] = outChBias + t2.ToScalar() + al.GetElement(1);
                        output[outH + v + 1] = outChBias + t3.GetElement(1) + al.GetElement(2);
                        output[outH + v + 2] = outChBias + t4.GetElement(2) + al.GetElement(3);
                        v += 3; x += skip * 3; rem -= skip * 3;
                    }
                    while (rem >= fw)
                    {
                        // PERFORM_CONVOLVE_FOR_1_5X5_BLOCK
                        float last = 0;
                        int p = plane + h * inStride + x;
                        var s0 = Vector128.LoadUnsafe(ref in0, (nuint)p); last += Unsafe.Add(ref in0, p + 4) * w04; p += inStride;
                        var s1 = Vector128.LoadUnsafe(ref in0, (nuint)p); last += Unsafe.Add(ref in0, p + 4) * w14; p += inStride;
                        var s2 = Vector128.LoadUnsafe(ref in0, (nuint)p); last += Unsafe.Add(ref in0, p + 4) * w24; p += inStride;
                        var s3 = Vector128.LoadUnsafe(ref in0, (nuint)p); last += Unsafe.Add(ref in0, p + 4) * w34; p += inStride;
                        var s4 = Vector128.LoadUnsafe(ref in0, (nuint)p); last += Unsafe.Add(ref in0, p + 4) * w44;
                        s0 = Sse.Multiply(s0, swl[0]); s1 = Sse.Multiply(s1, swl[1]); s2 = Sse.Multiply(s2, swl[2]);
                        s3 = Sse.Multiply(s3, swl[3]); s4 = Sse.Multiply(s4, swl[4]);
                        var acc = Sse.Add(s0, F4.Zero);
                        s1 = Sse.Add(s1, s2);
                        s3 = Sse.Add(s3, s4);
                        s1 = Sse.Add(s1, s3);
                        acc = Sse.Add(acc, s1);
                        acc = Sse3.HorizontalAdd(acc, acc);
                        output[outH + v] = outChBias + last + acc.ToScalar() + acc.GetElement(1);
                        v += 1; x += skip; rem -= skip;
                    }
                }
            }
        }
    }

    // cnn_convolve_no_maxpool_padding_valid_layer1 / layer2_avx2 in SSE3: each 2x2 block (p00 w0 + p10 w2) +
    // (p01 w1 + p11 w3) by hadd, added per in-channel to the bias in order
    internal static void CnnConvolve2x2Sse(float[] input, int inSize, int inChannels, int outChannels, float[] weights,
        float[] bias, float[] output, int outBase, int outStride)
    {
        int cstep = inChannels * outChannels, outSize = inSize / 2;
        if (input.Length < inChannels * inSize * inSize || output.Length < outBase + outChannels * outStride * outStride)
            throw new ArgumentException("cnn buffers");
        ref float in0 = ref MemoryMarshal.GetArrayDataReference(input);
        ref float out0 = ref MemoryMarshal.GetArrayDataReference(output);
        Span<F4> acc = stackalloc F4[16];   // outSize x outSize outputs, 4 per vector
        int nv = outSize * outSize / 4;
        for (int i = 0; i < outChannels; i++)
        {
            var biasReg = Vector128.Create(bias[i]);
            for (int j = 0; j < nv; j++) acc[j] = biasReg;
            for (int k = 0; k < inChannels; k++)
            {
                int off = k * outChannels + i;
                F4 w0 = Vector128.Create(weights[off], weights[off + cstep], weights[off], weights[off + cstep]);
                F4 w1 = Vector128.Create(weights[off + 2 * cstep], weights[off + 3 * cstep], weights[off + 2 * cstep], weights[off + 3 * cstep]);
                ref float pl = ref Unsafe.Add(ref in0, k * inSize * inSize);
                // each output row y: in rows 2y (w0 pairs) and 2y + 1 (w1 pairs), 4 outputs from 8 inputs per hadd
                for (int y = 0; y < outSize; y++)
                {
                    ref float ra = ref Unsafe.Add(ref pl, 2 * y * inSize);
                    ref float rb = ref Unsafe.Add(ref ra, inSize);
                    for (int x = 0; x < outSize; x += 4)
                    {
                        F4 lo = Sse.Add(Sse.Multiply(Vector128.LoadUnsafe(ref ra, (nuint)(2 * x)), w0), Sse.Multiply(Vector128.LoadUnsafe(ref rb, (nuint)(2 * x)), w1));
                        F4 hi = Sse.Add(Sse.Multiply(Vector128.LoadUnsafe(ref ra, (nuint)(2 * x + 4)), w0), Sse.Multiply(Vector128.LoadUnsafe(ref rb, (nuint)(2 * x + 4)), w1));
                        int a = (y * outSize + x) / 4;
                        acc[a] = Sse.Add(acc[a], Sse3.HorizontalAdd(lo, hi));
                    }
                }
            }
            int ob = outBase + i * outStride * outStride;
            for (int j = 0; j < nv; j++) acc[j].StoreUnsafe(ref out0, (nuint)(ob + 4 * j));
        }
    }
}
