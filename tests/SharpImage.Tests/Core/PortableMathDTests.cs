using SharpImage.Core;

namespace SharpImage.Tests.Core;

// PortableMathD (double pow / exp / log / log2 / log10 / cbrt and the fast variants) and the literal tables both portable
// classes use: bit-identical on every OS, within an ulp or two (fast variants: a few) of the platform libm.
public class PortableMathDTests
{
    [Test]
    public async Task Tables_MatchTheirDefinitions()
    {
        await Assert.That(PortableMathD.TablesMatchDefinition()).IsTrue();
        await Assert.That(PortableMath.TablesMatchDefinition()).IsTrue();
    }

    private static long Ulps(double a, double b) => Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));

    // The checksum pins every result (a platform computing any of them differently fails here); the ulp bounds against
    // Math.* hold on any libm that is itself within an ulp.
    [Test]
    public async Task Sample_IsPlatformIndependent_AndCloseToLibm()
    {
        var rng = new Random(42);
        long worst = 0, worstCbrt = 0, worstFast = 0;
        ulong h = 1469598103934665603UL;
        void Mix(double v) => h = (h ^ BitConverter.DoubleToUInt64Bits(v)) * 1099511628211UL;
        for (int i = 0; i < 200_000; i++)
        {
            double x = BitConverter.Int64BitsToDouble(0x3000000000000000L + (long)(rng.NextDouble() * 0x1E00000000000000L));
            double y = rng.NextDouble() * 8 - 4, z = rng.NextDouble() * 2, t = rng.NextDouble() * 1400 - 700;
            (double Mine, double Libm)[] cr =
            [
                (PortableMathD.Log(x), Math.Log(x)), (PortableMathD.Log2(x), Math.Log2(x)), (PortableMathD.Log10(x), Math.Log10(x)),
                (PortableMathD.Exp(t), Math.Exp(t)), (PortableMathD.Pow(z, y), Math.Pow(z, y)), (PortableMathD.Cbrt(x - 0.5 * x * (i & 1)), Math.Cbrt(x - 0.5 * x * (i & 1))),
            ];
            (double Mine, double Libm)[] fast =
            [
                (PortableMathD.LogFast(x), Math.Log(x)), (PortableMathD.Log2Fast(x), Math.Log2(x)), (PortableMathD.Log10Fast(x), Math.Log10(x)),
                (PortableMathD.ExpFast(t * 0.1), Math.Exp(t * 0.1)), (PortableMathD.PowFast(0.5 + 0.75 * z, y), Math.Pow(0.5 + 0.75 * z, y)),   // |y ln x| < 3: see PowFast's error bound
            ];
            for (int k = 0; k < cr.Length; k++)
            {
                Mix(cr[k].Mine);
                if (k == 5)
                {
                    worstCbrt = Math.Max(worstCbrt, Ulps(cr[k].Mine, cr[k].Libm));
                }
                else
                {
                    worst = Math.Max(worst, Ulps(cr[k].Mine, cr[k].Libm));
                }
            }
            foreach (var (mine, libm) in fast)
            {
                Mix(mine);
                worstFast = Math.Max(worstFast, Ulps(mine, libm));
            }
        }
        // slack for the libm's own error (glibc's log10 is up to 2 ulp off, UCRT's and glibc's cbrt more)
        await Assert.That(worst).IsLessThanOrEqualTo(3);
        await Assert.That(worstCbrt).IsLessThanOrEqualTo(4);
        await Assert.That(worstFast).IsLessThanOrEqualTo(6);
        await Assert.That(h.ToString("x16")).IsEqualTo("b69111a3534245c6");
    }

    [Test]
    public async Task ExactCases_AreExact()
    {
        int bad = 0;
        for (int k = -1074; k < 1024; k++)
        {
            double p = Math.ScaleB(1.0, k);
            if (PortableMathD.Log2(p) != k || (k > -1022 && PortableMathD.Pow(2.0, k) != p))
            {
                bad++;
            }
        }
        for (int k = 0; k < 23; k++)
        {
            if (PortableMathD.Log10(Math.Pow(10, k)) != k)
            {
                bad++;
            }
        }
        for (int k = -1000; k <= 1000; k++)
        {
            if (PortableMathD.Cbrt((double)k * k * k) != k)
            {
                bad++;
            }
        }
        if (PortableMathD.Log(1.0) != 0.0 || PortableMathD.Exp(0.0) != 1.0 || PortableMathD.Pow(3.0, 2.0) != 9.0 || PortableMathD.Pow(2.25, 0.5) != 1.5)
        {
            bad++;
        }
        await Assert.That(bad).IsEqualTo(0);
    }

    [Test]
    public async Task SpecialCases_MatchIeee()
    {
        double[] xs = [0.0, -0.0, 1.0, -1.0, 2.0, -2.0, 0.5, -0.5, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.Epsilon];
        double[] ys = [0.0, -0.0, 1.0, -1.0, 2.0, 3.0, -3.0, 0.5, -0.5, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 2000.0, -2000.0];
        var bad = new List<string>();
        foreach (double x in xs)
        {
            foreach (double y in ys)
            {
                double p = PortableMathD.Pow(x, y), q = Math.Pow(x, y);
                if (BitConverter.DoubleToInt64Bits(p) != BitConverter.DoubleToInt64Bits(q) && !(double.IsNaN(p) && double.IsNaN(q)))
                {
                    bad.Add($"pow({x}, {y}) = {p}, IEEE {q}");
                }
            }
        }
        // the other functions on their special arguments
        foreach (double x in new[] { 0.0, -0.0, 1.0, -1.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN })
        {
            foreach (var (name, mine, libm) in new[] { ("log", PortableMathD.Log(x), Math.Log(x)), ("log2", PortableMathD.Log2(x), Math.Log2(x)),
                ("log10", PortableMathD.Log10(x), Math.Log10(x)), ("exp", PortableMathD.Exp(x), Math.Exp(x)), ("cbrt", PortableMathD.Cbrt(x), Math.Cbrt(x)) })
            {
                if (BitConverter.DoubleToInt64Bits(mine) != BitConverter.DoubleToInt64Bits(libm) && !(double.IsNaN(mine) && double.IsNaN(libm)))
                {
                    bad.Add($"{name}({x}) = {mine}, IEEE {libm}");
                }
            }
        }
        await Assert.That(string.Join("; ", bad)).IsEqualTo("");
    }
}
