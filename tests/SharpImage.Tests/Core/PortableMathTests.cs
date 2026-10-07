using SharpImage.Core;

namespace SharpImage.Tests.Core;

// PortableMath: libm-free float pow / exp / log, bit-identical on every OS (MathF.Pow & co. differ in the last bit between
// UCRT and glibc for ~0.1% of inputs, which broke the AVIF gain map goldens on Linux).
public class PortableMathTests
{
    // Every result equals the double-precision value rounded to float on this sample, and the checksum of all results is
    // pinned, so a platform that computes even one of them differently fails here.
    [Test]
    public async Task Sample_MatchesDoubleRounded_AndIsPlatformIndependent()
    {
        var rng = new Random(42);
        int mismatches = 0;
        ulong h = 1469598103934665603UL;
        void Mix(float v) => h = (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;
        for (int i = 0; i < 200_000; i++)
        {
            float x = BitConverter.Int32BitsToSingle(0x30000000 + rng.Next(0x18000000));
            float y = (float)(rng.NextDouble() * 4.0 - 2.0);
            float z = (float)rng.NextDouble();
            float a = PortableMath.Pow(z, y);
            float b = PortableMath.Pow(2.0f, y * 30);
            float c = PortableMath.Exp(y * 40);
            float d = PortableMath.Log(x);
            float e = PortableMath.Log2(x);
            float f = PortableMath.Log10(x);
            Mix(a); Mix(b); Mix(c); Mix(d); Mix(e); Mix(f);
            if (a != (float)Math.Pow(z, y) || b != (float)Math.Pow(2.0, y * 30) || c != (float)Math.Exp(y * 40)
                || d != (float)Math.Log(x) || e != (float)Math.Log2(x) || f != (float)Math.Log10(x))
            {
                mismatches++;
            }
        }
        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(h.ToString("x16")).IsEqualTo("3f4cdc719c83007a");
    }

    [Test]
    public async Task ExactCases_AreExact()
    {
        int bad = 0;
        for (int k = -149; k < 128; k++)
        {
            float p = MathF.ScaleB(1.0f, k);
            if (PortableMath.Log2(p) != k || PortableMath.Pow(2.0f, k) != p)
            {
                bad++;
            }
        }
        for (int k = 0; k < 10; k++)
        {
            if (PortableMath.Log10(MathF.Pow(10.0f, k)) != k)
            {
                bad++;
            }
        }
        var rng = new Random(7);
        for (int i = 0; i < 100_000; i++)
        {
            float v = (float)rng.NextDouble() * 100.0f;
            if (PortableMath.Pow(v, 1.0f) != v || PortableMath.Pow(v, 2.0f) != v * v)
            {
                bad++;
            }
        }
        await Assert.That(bad).IsEqualTo(0);
    }

    [Test]
    public async Task SpecialCases_MatchIeeePow()
    {
        float[] xs = [0.0f, -0.0f, 1.0f, -1.0f, 2.0f, -2.0f, 0.5f, -0.5f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, float.Epsilon];
        float[] ys = [0.0f, -0.0f, 1.0f, -1.0f, 2.0f, 3.0f, -3.0f, 0.5f, -0.5f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 200.0f, -200.0f];
        var bad = new List<string>();
        foreach (float x in xs)
        {
            foreach (float y in ys)
            {
                float p = PortableMath.Pow(x, y), q = MathF.Pow(x, y);
                bool same = BitConverter.SingleToInt32Bits(p) == BitConverter.SingleToInt32Bits(q) || (float.IsNaN(p) && float.IsNaN(q));
                if (!same)
                {
                    bad.Add($"pow({x}, {y}) = {p}, IEEE {q}");
                }
            }
        }
        await Assert.That(string.Join("; ", bad)).IsEqualTo("");
        await Assert.That(float.IsNaN(PortableMath.Log(-1.0f))).IsTrue();
        await Assert.That(PortableMath.Log(0.0f)).IsEqualTo(float.NegativeInfinity);
        await Assert.That(PortableMath.Log2(float.PositiveInfinity)).IsEqualTo(float.PositiveInfinity);
        await Assert.That(PortableMath.Exp(-200.0f)).IsEqualTo(0.0f);
        await Assert.That(PortableMath.Exp(200.0f)).IsEqualTo(float.PositiveInfinity);
    }
}
