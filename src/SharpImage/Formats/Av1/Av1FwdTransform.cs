// Forward transform + quantization for the AV1 encoder. The AV1 standard specifies only the INVERSE transform
// (Av1InvTransform); the forward is the encoder's choice — it only has to produce coefficients the decoder's
// inverse maps back to the residual.
//
// Two forward paths:
//  * Sizes 4/8/16 (ForwardMatrix): the forward 1D transform is built as the exact inverse of the decoder's own
//    integer 1D inverse (probed via Av1InvTransform.Probe1dInverse). Because residual = (1/(16·2^shift))·M·C·Mᵀ
//    for the decoder's 1D inverse matrix M, the matched forward is C = (16·2^shift·2^dqShift/dq)·F·res·Fᵀ with
//    F = M⁻¹ and the combined scale S = 4·N. This pairs with the integer inverse far better than an idealized
//    basis (lower reconstruction floor) AND lets us pick ADST/DCT per axis (F built from the ADST inverse).
//  * Sizes 32/64 (ForwardQuantSquare): an orthonormal 2D DCT-II with the constant K = 8 (2^dqShift/G_dec == 8 for
//    every square DCT size). ADST is not defined for these sizes, so DCT_DCT is the only type.
using System;
using System.Collections.Concurrent;

namespace SharpImage.Formats.Av1;

internal static class Av1FwdTransform
{
    // Which 1D transform each axis of a 2D type uses, as (horizontal/row, vertical/col) — mirrors the decoder's
    // Tx1dTypes. Only the reduced intra set's ADST/DCT combinations are needed (no FlipAdst / Identity here).
    internal enum FwdTxType { DctDct, AdstAdst, AdstDct, DctAdst }

    // Universal forward-quant scale for the orthonormal path: 2^dqShift / G_dec == 8 for all square DCT sizes.
    private const double QuantScaleK = 8.0;

    // Deadzone quantization bias (AV1/libaom-style): shift the rounding threshold toward zero so marginal
    // coefficients quantize to 0. Pure encoder rate-distortion choice — the decoder is unaffected.
    private const double DeadzoneBias = 0.20;

    // Cached forward 1D matrices F = M^-1 (M = decoder's integer 1D inverse), keyed by (logSize<<2 | type1d).
    private static readonly ConcurrentDictionary<int, double[,]> FwdMatrixCache = new();

    /// <summary>Forward DCT_DCT + quantize a square residual (the common path). Sizes 4/8/16 use the matched
    /// matrix forward; 32/64 use the orthonormal DCT. rc layout: levels[kx*sh + ky], sh = min(N,32).</summary>
    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount)
        => n <= 16
            ? MatrixForward(residual, n, dcDq, acDq, rcCount, FwdTxType.DctDct)
            : ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, QuantScaleK);

    /// <summary>Forward transform + quantize for a chosen 2D type. ADST types are only valid for sizes 4/8/16;
    /// for 32/64 this falls back to DCT_DCT (orthonormal).</summary>
    internal static int[] ForwardQuantTyped(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, FwdTxType txType)
        => n <= 16
            ? MatrixForward(residual, n, dcDq, acDq, rcCount, txType)
            : ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, QuantScaleK);

    // (horizontal/row 1D type, vertical/col 1D type) for a 2D forward type — inverse of the decoder's row(width,
    // txtp0)/col(height, txtp1) split: txtp0 is horizontal, txtp1 is vertical.
    private static (int H, int V) AxisTypes(FwdTxType t) => t switch
    {
        FwdTxType.DctDct => (Av1InvTransform.Type1dDct, Av1InvTransform.Type1dDct),
        FwdTxType.AdstAdst => (Av1InvTransform.Type1dAdst, Av1InvTransform.Type1dAdst),
        FwdTxType.AdstDct => (Av1InvTransform.Type1dDct, Av1InvTransform.Type1dAdst),   // ADST_DCT: H=Dct, V=Adst
        _ => (Av1InvTransform.Type1dAdst, Av1InvTransform.Type1dDct),                    // DCT_ADST: H=Adst, V=Dct
    };

    // Forward 1D matrix F = (decoder's integer 1D inverse)^-1 for a given size and type, cached.
    private static double[,] ForwardMatrix(int logSize, int type1d) =>
        FwdMatrixCache.GetOrAdd((logSize << 2) | type1d, static key =>
        {
            int ls = key >> 2, ty = key & 3, n = 4 << ls;
            var m = new double[n, n];
            const int probe = 1 << 11;
            for (int k = 0; k < n; k++)
            {
                Span<int> c = stackalloc int[n];
                c.Clear();
                c[k] = probe;
                Av1InvTransform.Probe1dInverse(c, ls, ty);
                for (int i = 0; i < n; i++) m[i, k] = c[i] / (double)probe;
            }

            return Invert(m);
        });

    // In-place Gauss-Jordan matrix inverse (small n; matrices are well-conditioned transform bases).
    private static double[,] Invert(double[,] a)
    {
        int n = a.GetLength(0);
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n + i] = 1; }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            for (int j = 0; j < 2 * n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
            double d = m[col, col];
            for (int j = 0; j < 2 * n; j++) m[col, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = m[r, col];
                for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
            }
        }

        var inv = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    // Matched forward for sizes 4/8/16: C[ky][kx] = Fv[ky][·] · res · Fh[kx][·], level = deadzone(C·S/dq), S = 4N.
    private static int[] MatrixForward(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, FwdTxType txType)
    {
        int logSize = System.Numerics.BitOperations.Log2((uint)n) - 2; // 4→0, 8→1, 16→2
        (int hType, int vType) = AxisTypes(txType);
        double[,] fh = ForwardMatrix(logSize, hType);
        double[,] fv = ForwardMatrix(logSize, vType);
        double s = 4.0 * n;

        // Horizontal forward: t[y][kx] = sum_x Fh[kx][x] * res[y][x].
        var t = new double[n, n];
        for (int y = 0; y < n; y++)
            for (int kx = 0; kx < n; kx++)
            {
                double acc = 0;
                for (int x = 0; x < n; x++) acc += fh[kx, x] * residual[y * n + x];
                t[y, kx] = acc;
            }

        // Vertical forward + quant: C[ky][kx] = sum_y Fv[ky][y] * t[y][kx].
        var levels = new int[rcCount];
        for (int kx = 0; kx < n; kx++)
            for (int ky = 0; ky < n; ky++)
            {
                double acc = 0;
                for (int y = 0; y < n; y++) acc += fv[ky, y] * t[y, kx];
                int dq = (kx == 0 && ky == 0) ? dcDq : acDq;
                double qf = acc * s / dq;
                double mag = Math.Abs(qf) + 0.5 - DeadzoneBias;
                levels[kx * n + ky] = mag < 1.0 ? 0 : (int)(Math.Sign(qf) * Math.Floor(mag));
            }

        return levels;
    }

    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, double k)
    {
        int kept = Math.Min(n, 32);
        int sh = kept;

        // Horizontal pass: rows → T[y][kx].
        double[,] basis = DctBasis(n);
        var t = new double[n, kept];
        for (int y = 0; y < n; y++)
        {
            for (int kx = 0; kx < kept; kx++)
            {
                double acc = 0;
                for (int x = 0; x < n; x++)
                {
                    acc += residual[y * n + x] * basis[kx, x];
                }

                t[y, kx] = acc;
            }
        }

        // Vertical pass: columns → C[ky][kx].
        var levels = new int[rcCount];
        for (int kx = 0; kx < kept; kx++)
        {
            for (int ky = 0; ky < kept; ky++)
            {
                double acc = 0;
                for (int y = 0; y < n; y++)
                {
                    acc += t[y, kx] * basis[ky, y];
                }

                int dq = (kx == 0 && ky == 0) ? dcDq : acDq;
                // Deadzone quantizer: |level| = floor(|qf| + 0.5 - bias), clamped at 0.
                double qf = acc * k / dq;
                double mag = Math.Abs(qf) + 0.5 - DeadzoneBias;
                int q = mag < 1.0 ? 0 : (int)(Math.Sign(qf) * Math.Floor(mag));
                levels[kx * sh + ky] = q;
            }
        }

        return levels;
    }

    // Orthonormal DCT-II basis, cached per size (values depend only on n). basis[k, n] = a(k) * sqrt(2/N) *
    // cos(pi*(2n+1)*k / (2N)), a(0)=1/sqrt2 else 1. Read-only after construction, so the cache is safe to share.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double[,]> BasisCache = new();

    private static double[,] DctBasis(int n) => BasisCache.GetOrAdd(n, static nn =>
    {
        var b = new double[nn, nn];
        double s = Math.Sqrt(2.0 / nn);
        for (int k = 0; k < nn; k++)
        {
            double ak = k == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (int i = 0; i < nn; i++)
                b[k, i] = ak * s * Math.Cos(Math.PI * (2 * i + 1) * k / (2.0 * nn));
        }

        return b;
    });
}
