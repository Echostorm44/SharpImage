using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>The lane-by-lane emulations the encoder runs without AVX2 (the default Native AOT instruction set, older
/// CPUs) against the AVX2 kernels they stand in for: the same levels, dequantised values and eobs, so an encode is the
/// same bitstream on every CPU.</summary>
public sealed class AomNoAvx2EmulationTests
{
    [Test]
    public async Task QuantizerEmulationsAndSse41_MatchAvx2Kernels()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) return;   // nothing to compare against
        var rng = new Random(5);
        int cases = 0, bad = 0;
        for (int txSize = 0; txSize < 19; txSize++)
        {
            int n = AomEncodeMb.MaxEob(txSize);
            int logScale = AomQuantize.TxScale(txSize);
            foreach (int txType in new[] { 0, 1, 9 })
            {
                if (Math.Max(AomTables.TxSizeWide[txSize], AomTables.TxSizeHigh[txSize]) > 16 && txType != 0) continue;
                short[] iscan = AomEncodeMb.IScanOf(txSize, txType);
                for (int trial = 0; trial < 24; trial++)
                {
                    // magnitudes from all-zero groups to past the int16 saturation (the kernels' packs)
                    int amp = trial % 6 switch { 0 => 3, 1 => 60, 2 => 900, 3 => 9000, 4 => 40000, _ => 1 << 20 };
                    var c = new int[n];
                    for (int k = 0; k < n; k++) c[k] = rng.Next(3) == 0 ? 0 : rng.Next(-amp, amp + 1);
                    if (trial % 7 == 0) c[rng.Next(n)] = short.MinValue;
                    short dq0 = (short)rng.Next(4, 1400), dq1 = (short)rng.Next(4, 1800);
                    short rnd0 = (short)(dq0 * rng.Next(16, 64) >> 7), rnd1 = (short)(dq1 * rng.Next(16, 64) >> 7);
                    short q0 = (short)rng.Next(1, 32767), q1 = (short)rng.Next(1, 32767);
                    short zb0 = (short)(dq0 * rng.Next(20, 90) >> 7), zb1 = (short)(dq1 * rng.Next(20, 90) >> 7);
                    short sh0 = (short)rng.Next(1, 32767), sh1 = (short)rng.Next(1, 32767);
                    var qa = new int[n]; var da = new int[n]; var qe = new int[n]; var de = new int[n];
                    int ea = AomQuantize.QuantizeFpAvx2(c, n, iscan, rnd0, rnd1, q0, q1, dq0, dq1, logScale, qa, da);
                    int ee = AomQuantize.QuantizeFpAvx2Emu(c, n, iscan, rnd0, rnd1, q0, q1, dq0, dq1, logScale, qe, de);
                    cases++;
                    if (ea != ee || !qa.AsSpan().SequenceEqual(qe) || !da.AsSpan().SequenceEqual(de)) bad++;
                    ea = AomQuantize.QuantizeBAvx2(c, n, iscan, zb0, zb1, rnd0, rnd1, q0, q1, sh0, sh1, dq0, dq1, logScale, qa, da);
                    ee = AomQuantize.QuantizeBAvx2Emu(c, n, iscan, zb0, zb1, rnd0, rnd1, q0, q1, sh0, sh1, dq0, dq1, logScale, qe, de);
                    cases++;
                    if (ea != ee || !qa.AsSpan().SequenceEqual(qe) || !da.AsSpan().SequenceEqual(de)) bad++;
                    ee = AomQuantize.QuantizeBSse41(c, n, iscan, zb0, zb1, rnd0, rnd1, q0, q1, sh0, sh1, dq0, dq1, logScale, qe, de);
                    cases++;
                    if (ea != ee || !qa.AsSpan().SequenceEqual(qe) || !da.AsSpan().SequenceEqual(de)) bad++;
                    ea = AomQuantize.QuantizeFpAvx2(c, n, iscan, rnd0, rnd1, q0, q1, dq0, dq1, logScale, qa, da);
                    ee = AomQuantize.QuantizeFpSse41(c, n, iscan, rnd0, rnd1, q0, q1, dq0, dq1, logScale, qe, de);
                    cases++;
                    if (ea != ee || !qa.AsSpan().SequenceEqual(qe) || !da.AsSpan().SequenceEqual(de)) bad++;
                }
            }
        }
        Console.WriteLine($"quantizer emulation: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
}
