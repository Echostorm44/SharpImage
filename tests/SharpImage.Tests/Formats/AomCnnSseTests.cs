using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>The partition CNN's SSE layers (no AVX2: the default Native AOT instruction set) against the AVX2 ones:
/// bit-identical floats (the same per-lane operation order).</summary>
public sealed class AomCnnSseTests
{
    [Test]
    public async Task SseLayers_MatchAvx2Layers_BitExact()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            Skip.Test("compares against the AVX2 kernels: this CPU has no AVX2");
        }
        var rng = new Random(29);
        int bad = 0, cases = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            // layer 0: 65x65x1 -> 16x16x20 (5x5, stride 4)
            var input = new float[65 * 65 + 16];
            for (int i = 0; i < input.Length; i++) input[i] = (float)(rng.NextDouble() * (trial % 2 == 0 ? 1 : 300) - (trial % 3 == 0 ? 0.5 : 0));
            var w5 = new float[25 * 20]; var b5 = new float[20];
            for (int i = 0; i < w5.Length; i++) w5[i] = (float)(rng.NextDouble() * 2 - 1);
            for (int i = 0; i < b5.Length; i++) b5[i] = (float)(rng.NextDouble() * 2 - 1);
            var oa = new float[20 * 16 * 16]; var os = new float[20 * 16 * 16];
            AomMl.CnnConvolve5x5Avx2(input, 65, 65, 65, 1, 20, w5, b5, oa, 16);
            AomMl.CnnConvolve5x5Sse(input, 65, 65, 65, 1, 20, w5, b5, os, 16);
            cases++;
            if (!Same(oa, os)) bad++;
            // layers 1 / 2: 16x16x20 -> 8x8x20, 8x8x20 -> 4x4x20 (2x2, stride 2)
            foreach (int inSize in new[] { 16, 8 })
            {
                int outSize = inSize / 2;
                var w2 = new float[4 * 20 * 20]; var b2 = new float[20];
                for (int i = 0; i < w2.Length; i++) w2[i] = (float)(rng.NextDouble() * 2 - 1);
                for (int i = 0; i < b2.Length; i++) b2[i] = (float)(rng.NextDouble() * 2 - 1);
                var a = new float[20 * outSize * outSize + 8]; var s = new float[a.Length];
                AomMl.CnnConvolve2x2Avx2(oa, inSize, 20, 20, w2, b2, a, 4, outSize);
                AomMl.CnnConvolve2x2Sse(oa, inSize, 20, 20, w2, b2, s, 4, outSize);
                cases++;
                if (!Same(a, s)) bad++;
            }
        }
        Console.WriteLine($"cnn sse: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }

    private static bool Same(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length; i++) if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
        return true;
    }
}
