using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// encodemb.c's pieces (forward transform, scans) and the distortion kernels against libaom's dispatched versions.
public sealed partial class AomTwinTests
{
    private static unsafe partial class Native
    {
        [DllImport("aomtwin")] public static extern void twin_fwd_txfm(short* srcDiff, int stride, int txSize, int txType, int lossless, int* coeff);
        [DllImport("aomtwin")] public static extern long twin_block_error(int* coeff, int* dqcoeff, nint n, long* ssz);
        [DllImport("aomtwin")] public static extern ulong twin_sum_squares_2d_i16(short* src, int stride, int w, int h);
        [DllImport("aomtwin")] public static extern ulong twin_sum_sse_2d_i16(short* src, int stride, int w, int h, int* sum);
        [DllImport("aomtwin")] public static extern long twin_sse(byte* a, int aStride, byte* b, int bStride, int w, int h);
        [DllImport("aomtwin")] public static extern int twin_satd(int* coeff, int n);
        [DllImport("aomtwin")] public static extern void twin_hadamard(int txSize, short* src, int stride, int* coeff);
        [DllImport("aomtwin")] public static extern void twin_inv_txfm_add(int* dqcoeff, byte* dst, int stride, int txSize, int txType, int eob);
    }

    // residual generators: uniform, the extremes, smooth ramps, sparse spikes (the transforms' overflow corners)
    private static void FillResidual(Random rng, short[] d, int stride, int w, int h, int kind)
    {
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int v = kind switch
                {
                    0 => rng.Next(-255, 256),
                    1 => rng.Next(2) == 0 ? -255 : 255,
                    2 => ((r + c) & 1) == 0 ? 255 : -255,
                    3 => Math.Clamp((r * 37 + c * 23) % 511 - 255, -255, 255),
                    4 => rng.Next(16) == 0 ? rng.Next(-255, 256) : 0,
                    5 => rng.Next(-8, 9),
                    _ => 255,
                };
                d[r * stride + c] = (short)v;
            }
    }

    [Test]
    public async Task ForwardTransform_AllSizesTypes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(7);
        const int stride = 64;
        var diff = new short[64 * stride];
        var a = new int[64 * 64];
        var b = new int[64 * 64];
        int mismatches = 0; string first = "";
        for (int tx = 0; tx < 19; tx++)
        {
            int w = AomTables.TxSizeWide[tx], h = AomTables.TxSizeHigh[tx];
            int n = Math.Min(w, 32) * Math.Min(h, 32);
            for (int txType = 0; txType < 16; txType++)
            {
                if (Math.Max(w, h) == 32 && txType != AomTables.DCT_DCT && txType != AomTables.IDTX) continue;
                if (Math.Max(w, h) == 64 && txType != AomTables.DCT_DCT) continue;
                for (int iter = 0; iter < 300; iter++)
                {
                    FillResidual(rng, diff, stride, w, h, iter % 7);
                    Array.Clear(a); Array.Clear(b);
                    unsafe { fixed (short* pd = diff) fixed (int* pa = a) Native.twin_fwd_txfm(pd, stride, tx, txType, 0, pa); }
                    AomEncodeMb.TxTypeKinds(txType, out int hk, out int vk, out bool fu, out bool fl);
                    Av1FwdTxfmAom.ForwardRaw(diff, stride, w, h, tx, hk, vk, fu, fl, b.AsSpan(0, n));
                    for (int i = 0; i < n; i++)
                        if (a[i] != b[i])
                        {
                            if (mismatches++ == 0) first = $"tx {tx} type {txType} kind {iter % 7} i {i}: libaom {a[i]} ours {b[i]}";
                            break;
                        }
                }
            }
        }
        await Assert.That(first).IsEqualTo("");
    }

    [Test]
    public async Task Scans_AllSizesTypes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        for (int tx = 0; tx < 19; tx++)
            for (int txType = 0; txType < 16; txType++)
            {
                var lib = LibaomScan(tx, txType);
                var ours = AomEncodeMb.ScanOf(tx, txType);
                await Assert.That(ours.AsSpan(0, lib.Length).SequenceEqual(lib)).IsTrue();
                var iscan = AomEncodeMb.IScanOf(tx, txType);
                for (int i = 0; i < lib.Length; i++) await Assert.That((int)iscan[lib[i]]).IsEqualTo(i);
            }
    }

    [Test]
    public async Task DistortionKernels_Random()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(11);
        var c = new int[4096];
        var d = new int[4096];
        var s16 = new short[64 * 64];
        var p0 = new byte[64 * 80];
        var p1 = new byte[64 * 80];
        int[] sizes = { 16, 32, 64, 128, 256, 512, 1024 };
        for (int iter = 0; iter < 20000; iter++)
        {
            int n = sizes[rng.Next(sizes.Length)];
            int range = iter % 4 switch { 0 => 64, 1 => 4000, 2 => 40000, _ => 200000 };
            for (int i = 0; i < n; i++)
            {
                c[i] = rng.Next(-range, range + 1);
                d[i] = rng.Next(4) == 0 ? 0 : c[i] + rng.Next(-range / 8 - 1, range / 8 + 2);
            }
            long lssz, lerr;
            unsafe { fixed (int* pc = c) fixed (int* pd = d) lerr = Native.twin_block_error(pc, pd, n, &lssz); }
            long oerr = AomEncodeMb.BlockErrorAvx2(c, d, n, out long ossz);
            if (oerr != lerr || ossz != lssz) await Assert.That((oerr, ossz)).IsEqualTo((lerr, lssz));
            int satdL; unsafe { fixed (int* pc = c) satdL = Native.twin_satd(pc, n); }
            if (AomEncodeMb.Satd(c, n) != satdL) await Assert.That(AomEncodeMb.Satd(c, n)).IsEqualTo(satdL);

            int w = 4 << rng.Next(5), h = 4 << rng.Next(5);
            for (int i = 0; i < w * h; i++) s16[i] = (short)rng.Next(-1023, 1024);
            ulong ssL; unsafe { fixed (short* ps = s16) ssL = Native.twin_sum_squares_2d_i16(ps, w, w, h); }
            if (AomEncodeMb.SumSquares2dI16(s16, 0, w, w, h) != ssL) await Assert.That(AomEncodeMb.SumSquares2dI16(s16, 0, w, w, h)).IsEqualTo(ssL);
            int sumL = 0, sumO = 0; ulong sseL;
            unsafe { fixed (short* ps = s16) sseL = Native.twin_sum_sse_2d_i16(ps, w, w, h, &sumL); }
            ulong sseO = AomEncodeMb.SumSse2dI16(s16, 0, w, w, h, ref sumO);
            if (sseO != sseL || sumO != sumL) await Assert.That((sseO, sumO)).IsEqualTo((sseL, sumL));
            rng.NextBytes(p0); rng.NextBytes(p1);
            long sL; unsafe { fixed (byte* a = p0) fixed (byte* b = p1) sL = Native.twin_sse(a, 72, b, 80, w, h); }
            long sO = AomEncodeMb.Sse(p0, 0, 72, p1, 0, 80, w, h);
            if (sO != sL) await Assert.That(sO).IsEqualTo(sL);
        }
    }

    [Test]
    public async Task InverseTransform_AllSizesTypes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(13);
        const int stride = 64;
        var diff = new short[64 * stride];
        var coeff = new int[64 * 64];
        var dq = new int[64 * 64];
        var dstA = new byte[64 * stride];
        var dstB = new byte[64 * stride];
        string first = "";
        for (int tx = 0; tx < 19 && first == ""; tx++)
        {
            int w = AomTables.TxSizeWide[tx], h = AomTables.TxSizeHigh[tx];
            int n = Math.Min(w, 32) * Math.Min(h, 32);
            for (int txType = 0; txType < 16 && first == ""; txType++)
            {
                if (Math.Max(w, h) == 32 && txType != AomTables.DCT_DCT && txType != AomTables.IDTX) continue;
                if (Math.Max(w, h) == 64 && txType != AomTables.DCT_DCT) continue;
                var scan = AomEncodeMb.ScanOf(tx, txType);
                for (int iter = 0; iter < 400 && first == ""; iter++)
                {
                    Array.Clear(coeff);
                    // the coefficients the encoder can produce: its own transform of 8-bit residuals, quantised. (libaom's
                    // 16-bit SIMD inverse saturates where the spec clamps, so arbitrary out-of-range input is not compared.)
                    FillResidual(rng, diff, stride, w, h, iter % 7);
                    AomEncodeMb.TxTypeKinds(txType, out int hk, out int vk, out bool fu, out bool fl);
                    Av1FwdTxfmAom.ForwardRaw(diff, stride, w, h, tx, hk, vk, fu, fl, coeff.AsSpan(0, n));
                    int step = 4 + rng.Next(1500);
                    for (int i = 0; i < n; i++) dq[i] = (int)Math.Round(coeff[i] / (double)step) * step;
                    int eob = 0;
                    for (int i = 0; i < n; i++) if (dq[scan[i]] != 0) eob = i + 1;
                    if (eob == 0) continue;
                    // the kernels read only the scan's first eob positions
                    for (int i = eob; i < n; i++) dq[scan[i]] = 0;
                    rng.NextBytes(dstA);
                    Array.Copy(dstA, dstB, dstA.Length);
                    unsafe { fixed (int* pq = dq) fixed (byte* pd = dstA) Native.twin_inv_txfm_add(pq, pd, stride, tx, txType, eob); }
                    AomEncodeMb.InverseTransformBlock(dq, 0, txType, tx, dstB, 0, stride, eob);
                    for (int r = 0; r < h && first == ""; r++)
                        for (int c = 0; c < w; c++)
                            if (dstA[r * stride + c] != dstB[r * stride + c])
                            {
                                first = $"tx {tx} type {txType} iter {iter} eob {eob} ({r},{c}): libaom {dstA[r * stride + c]} ours {dstB[r * stride + c]}";
                                break;
                            }
                }
            }
        }
        await Assert.That(first).IsEqualTo("");
    }

    [Test]
    public async Task Hadamard_AllSizes()
    {
        AomTwinNative.SkipUnless(Available, EnvVar);
        var rng = new Random(17);
        const int stride = 64;
        var diff = new short[64 * stride];
        var a = new int[1024];
        var b = new int[1024];
        int[] sizes = { AomTables.TX_4X4, AomTables.TX_8X8, AomTables.TX_16X16, AomTables.TX_32X32 };
        foreach (int tx in sizes)
        {
            int n = AomTables.TxSize2d[tx], w = AomTables.TxSizeWide[tx];
            for (int iter = 0; iter < 2000; iter++)
            {
                FillResidual(rng, diff, stride, w, w, iter % 7);
                unsafe { fixed (short* pd = diff) fixed (int* pa = a) Native.twin_hadamard(tx, pd, stride, pa); }
                AomHadamard.WhtFwdTxfm(tx, diff, stride, b);
                if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)))
                    await Assert.That(string.Join(",", b.AsSpan(0, n).ToArray())).IsEqualTo(string.Join(",", a.AsSpan(0, n).ToArray()));
            }
        }
    }
}
