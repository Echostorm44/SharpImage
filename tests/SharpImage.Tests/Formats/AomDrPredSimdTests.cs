using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;

/// <summary>The vector directional predictors (no edge upsampling) against the scalar ports: equal predictions for every
/// tx size and every allowed angle over random edges.</summary>
public sealed class AomDrPredSimdTests
{
    [Test]
    public async Task VectorDirectionalPredictorsMatchScalar() => await Assert.That(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? Run() : 0).IsEqualTo(0);

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

    [Test]
    public async Task VectorCflPredictMatchesReference() => await Assert.That(RunCfl()).IsEqualTo(0);

    // cfl_predict_lbd_c's clip(dc + ROUND_POWER_OF_TWO_SIGNED(alpha_q3 * ac, 6)) over all alphas and widths
    private static unsafe int RunCfl()
    {
        var rng = new Random(13);
        int mismatches = 0;
        short* ac = stackalloc short[32 * 32];
        byte* a = stackalloc byte[32 * 40], b = stackalloc byte[32 * 40];
        foreach (int w in new[] { 4, 8, 16, 32 })
            foreach (int h in new[] { 4, 8, 16, 32 })
                for (int alpha = -16; alpha <= 16; alpha++)
                {
                    for (int i = 0; i < 32 * 32; i++) ac[i] = (short)rng.Next(-2040, 2041);
                    byte dc = (byte)rng.Next(256);
                    for (int i = 0; i < 32 * 40; i++) a[i] = b[i] = dc;
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            int sl = alpha * ac[j * 32 + i];
                            sl = sl < 0 ? -((-sl + 32) >> 6) : (sl + 32) >> 6;
                            a[j * 40 + i] = (byte)Math.Clamp(sl + dc, 0, 255);
                        }
                    AomCfl.CflPredictLbd(ac, b, 40, alpha, w, h);
                    if (!new Span<byte>(a, 32 * 40).SequenceEqual(new Span<byte>(b, 32 * 40)) && mismatches++ < 10)
                        Console.WriteLine($"cfl {w}x{h} alpha {alpha} differs");
                }
        return mismatches;
    }

    [Test]
    public async Task HighbdVectorDirectionalPredictorsMatchScalar() => await Assert.That(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? RunHbd() : 0).IsEqualTo(0);

    private static unsafe int RunHbd()
    {
        var rng = new Random(7);
        int cases = 0, mismatches = 0;
        int[] bases = { 45, 67, 113, 135, 157, 203 };
        const int N = AomReconIntra.NUM_INTRA_NEIGHBOUR_PIXELS;
        ushort* aboveData = stackalloc ushort[N], leftData = stackalloc ushort[N];
        ushort* a = stackalloc ushort[64 * 64], b = stackalloc ushort[64 * 64];
        for (int txSize = 0; txSize < AomTables.TxSizeWide.Length; txSize++)
        {
            int bw = AomTables.TxSizeWide[txSize], bh = AomTables.TxSizeHigh[txSize];
            foreach (int bse in bases)
                for (int delta = -3; delta <= 3; delta++)
                {
                    int angle = bse + 3 * delta;
                    for (int trial = 0; trial < 6; trial++)
                    {
                        int max = trial % 2 == 0 ? 4095 : 1023;
                        for (int i = 0; i < N; i++) { aboveData[i] = (ushort)rng.Next(max + 1); leftData[i] = (ushort)rng.Next(max + 1); }
                        ushort* above = aboveData + 16, left = leftData + 16;
                        int dx = AomReconIntra.GetDx(angle), dy = AomReconIntra.GetDy(angle);
                        new Span<ushort>(a, 64 * 64).Fill(1); new Span<ushort>(b, 64 * 64).Fill(1);
                        if (angle < 90)
                        {
                            AomReconIntra.HighbdDrPredictionZ1(a, 64, bw, bh, above, 0, dx);
                            AomReconIntra.HighbdDrPredictionZ1Simd(b, 64, bw, bh, above, dx);
                        }
                        else if (angle < 180)
                        {
                            AomReconIntra.HighbdDrPredictionZ2(a, 64, bw, bh, above, left, 0, 0, dx, dy);
                            AomReconIntra.HighbdDrPredictionZ2Simd(b, 64, bw, bh, above, left, dx, dy);
                        }
                        else
                        {
                            AomReconIntra.HighbdDrPredictionZ3(a, 64, bw, bh, left, 0, dy);
                            AomReconIntra.HighbdDrPredictionZ3Simd(b, 64, bw, bh, left, dy);
                        }
                        cases++;
                        if (!new Span<ushort>(a, 64 * 64).SequenceEqual(new Span<ushort>(b, 64 * 64)) && mismatches++ < 10)
                            Console.WriteLine($"hbd txSize {txSize} ({bw}x{bh}) angle {angle} differs");
                    }
                }
        }
        Console.WriteLine($"hbd {cases} cases, {mismatches} mismatches");
        return mismatches;
    }

    [Test]
    public async Task HighbdVectorEdgeFilterMatchesReference() => await Assert.That(RunEdgeHbd()).IsEqualTo(0);

    // av1_highbd_filter_intra_edge_c plus the SSE4.1 kernel's p[-1] / p[sz .. sz + 7] extension, over the whole buffer
    private static unsafe int RunEdgeHbd()
    {
        var rng = new Random(11);
        byte[] kernel = { 0, 4, 8, 4, 0, 0, 5, 6, 5, 0, 2, 4, 4, 4, 2 };
        int mismatches = 0;
        ushort* a = stackalloc ushort[200], b = stackalloc ushort[200], orig = stackalloc ushort[200];
        for (int sz = 2; sz <= 129; sz++)
            for (int strength = 1; strength <= 3; strength++)
                for (int trial = 0; trial < 6; trial++)
                {
                    int max = trial % 2 == 0 ? 4095 : 1023;
                    for (int i = 0; i < 200; i++) orig[i] = a[i] = b[i] = (ushort)(trial == 4 ? max : rng.Next(max + 1));
                    ushort* p = a + 8;
                    ushort* e = orig + 8;
                    for (int i = 1; i < sz; i++)
                    {
                        int s = 0;
                        for (int j = 0; j < 5; j++) s += e[Math.Clamp(i - 2 + j, 0, sz - 1)] * kernel[(strength - 1) * 5 + j];
                        p[i] = (ushort)((s + 8) >> 4);
                    }
                    p[-1] = e[0];
                    for (int i = 0; i < 8; i++) p[sz + i] = e[sz - 1];
                    AomReconIntra.HighbdFilterIntraEdge(b + 8, sz, strength);
                    if (!new Span<ushort>(a, 200).SequenceEqual(new Span<ushort>(b, 200)) && mismatches++ < 10)
                        Console.WriteLine($"hbd edge sz {sz} strength {strength} differs");
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

    [Test]
    public async Task VectorUpsampledDirectionalPredictorsMatchScalar() => await Assert.That(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? RunUp() : 0).IsEqualTo(0);

    // the upsampled-edge sizes (bw, bh <= 8), every angle, every upsample combination zone 2 can take
    private static unsafe int RunUp()
    {
        var rng = new Random(23);
        int cases = 0, mismatches = 0;
        const int N = AomReconIntra.NUM_INTRA_NEIGHBOUR_PIXELS;
        byte* aboveData = stackalloc byte[N], leftData = stackalloc byte[N];
        byte* a = stackalloc byte[64 * 16], b = stackalloc byte[64 * 16];
        foreach (int bw in new[] { 4, 8 })
            foreach (int bh in new[] { 4, 8, 16 })
                for (int angle = 3; angle < 270; angle += 3)
                {
                    if (angle == 90 || angle == 180) continue;
                    for (int ups = 0; ups < 4; ups++)
                        for (int trial = 0; trial < 6; trial++)
                        {
                            int upA = ups & 1, upL = ups >> 1;
                            if (angle < 90 && (upA == 0 || bh > 8 || upL != 0)) continue;
                            if (angle > 180 && (upL == 0 || bh > 8 || upA != 0)) continue;
                            if (angle > 90 && angle < 180 && ups == 0) continue;
                            for (int i = 0; i < N; i++) { aboveData[i] = (byte)rng.Next(256); leftData[i] = (byte)rng.Next(256); }
                            byte* above = aboveData + 16, left = leftData + 16;
                            int dx = AomReconIntra.GetDx(angle), dy = AomReconIntra.GetDy(angle);
                            new Span<byte>(a, 64 * 16).Fill(1); new Span<byte>(b, 64 * 16).Fill(1);
                            if (angle < 90)
                            {
                                AomReconIntra.DrPredictionZ1(a, 64, bw, bh, above, left, 1, dx, dy);
                                AomReconIntra.DrPredictionZ1UpSimd(b, 64, bw, bh, above, dx);
                            }
                            else if (angle < 180)
                            {
                                AomReconIntra.DrPredictionZ2(a, 64, bw, bh, above, left, upA, upL, dx, dy);
                                AomReconIntra.DrPredictionZ2UpSimd(b, 64, bw, bh, above, left, upA, upL, dx, dy);
                            }
                            else
                            {
                                AomReconIntra.DrPredictionZ3(a, 64, bw, bh, above, left, 1, dx, dy);
                                AomReconIntra.DrPredictionZ3UpSimd(b, 64, bw, bh, left, dy);
                            }
                            cases++;
                            if (!new Span<byte>(a, 64 * 16).SequenceEqual(new Span<byte>(b, 64 * 16)) && mismatches++ < 10)
                                Console.WriteLine($"{bw}x{bh} angle {angle} up {upA}{upL} differs");
                        }
                }
        Console.WriteLine($"upsampled: {cases} cases, {mismatches} mismatches");
        return mismatches;
    }
}
