using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;

/// <summary>The vector directional predictors (no edge upsampling) against the scalar ports: equal predictions for every
/// tx size and every allowed angle over random edges.</summary>
public sealed class AomDrPredSimdTests
{
    [Test]
    public async Task VectorDirectionalPredictorsMatchScalar() => await Assert.That(Run()).IsEqualTo(0);

    [Test]
    public async Task VectorEdgeFilterMatchesReference() => await Assert.That(RunEdge()).IsEqualTo(0);

    // av1_filter_intra_edge_c plus the SSE4.1 kernel's p[-1] / p[sz .. sz + 15] extension, over the whole buffer
    private static unsafe int RunEdge()
    {
        var rng = new Random(9);
        byte[] kernel = { 0, 4, 8, 4, 0, 0, 5, 6, 5, 0, 2, 4, 4, 4, 2 };
        int mismatches = 0;
        byte* a = stackalloc byte[200], b = stackalloc byte[200], orig = stackalloc byte[200];
        for (int sz = 2; sz <= 129; sz++)
            for (int strength = 1; strength <= 3; strength++)
                for (int trial = 0; trial < 6; trial++)
                {
                    for (int i = 0; i < 200; i++) orig[i] = a[i] = b[i] = (byte)rng.Next(256);
                    byte* p = a + 8;
                    byte* e = orig + 8;
                    for (int i = 1; i < sz; i++)
                    {
                        int s = 0;
                        for (int j = 0; j < 5; j++) s += e[Math.Clamp(i - 2 + j, 0, sz - 1)] * kernel[(strength - 1) * 5 + j];
                        p[i] = (byte)((s + 8) >> 4);
                    }
                    p[-1] = e[0];
                    for (int i = 0; i < 16; i++) p[sz + i] = e[sz - 1];
                    AomReconIntra.FilterIntraEdge(b + 8, sz, strength);
                    if (!new Span<byte>(a, 200).SequenceEqual(new Span<byte>(b, 200)) && mismatches++ < 10)
                        Console.WriteLine($"edge sz {sz} strength {strength} differs");
                }
        return mismatches;
    }

    private static unsafe int Run()
    {
        var rng = new Random(5);
        int cases = 0, mismatches = 0;
        int[] bases = { 45, 67, 113, 135, 157, 203 , 90, 180 };
        const int N = AomReconIntra.NUM_INTRA_NEIGHBOUR_PIXELS;
        byte* aboveData = stackalloc byte[N], leftData = stackalloc byte[N];
        byte* a = stackalloc byte[64 * 64], b = stackalloc byte[64 * 64];
        for (int txSize = 0; txSize < AomTables.TxSizeWide.Length; txSize++)
        {
            int bw = AomTables.TxSizeWide[txSize], bh = AomTables.TxSizeHigh[txSize];
            foreach (int bse in bases)
                for (int delta = -3; delta <= 3; delta++)
                {
                    int angle = bse + 3 * delta;
                    if (angle == 90 || angle == 180) continue;
                    for (int trial = 0; trial < 8; trial++)
                    {
                        for (int i = 0; i < N; i++) { aboveData[i] = (byte)rng.Next(256); leftData[i] = (byte)rng.Next(256); }
                        byte* above = aboveData + 16, left = leftData + 16;
                        int dx = AomReconIntra.GetDx(angle), dy = AomReconIntra.GetDy(angle);
                        new Span<byte>(a, 64 * 64).Fill(1); new Span<byte>(b, 64 * 64).Fill(1);
                        if (angle < 90)
                        {
                            AomReconIntra.DrPredictionZ1(a, 64, bw, bh, above, left, 0, dx, dy);
                            AomReconIntra.DrPredictionZ1Simd(b, 64, bw, bh, above, dx);
                        }
                        else if (angle < 180)
                        {
                            AomReconIntra.DrPredictionZ2(a, 64, bw, bh, above, left, 0, 0, dx, dy);
                            AomReconIntra.DrPredictionZ2Simd(b, 64, bw, bh, above, left, dx, dy);
                        }
                        else
                        {
                            AomReconIntra.DrPredictionZ3(a, 64, bw, bh, above, left, 0, dx, dy);
                            AomReconIntra.DrPredictionZ3Simd(b, 64, bw, bh, left, dy);
                        }
                        cases++;
                        if (!new Span<byte>(a, 64 * 64).SequenceEqual(new Span<byte>(b, 64 * 64)) && mismatches++ < 10)
                            Console.WriteLine($"txSize {txSize} ({bw}x{bh}) angle {angle} differs");
                    }
                }
        }
        Console.WriteLine($"{cases} cases, {mismatches} mismatches");
        return mismatches;
    }
}
