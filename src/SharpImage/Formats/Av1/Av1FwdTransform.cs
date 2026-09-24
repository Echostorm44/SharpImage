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
    // Tx1dTypes. The reduced intra set is {Identity(IDTX), DctDct, AdstAdst, AdstDct, DctAdst} (no FlipAdst).
    internal enum FwdTxType { DctDct, AdstAdst, AdstDct, DctAdst, Identity, VDct, HDct }

    // Universal forward-quant scale for the orthonormal path: 2^dqShift / G_dec == 8 for all square DCT sizes.
    private const double QuantScaleK = 8.0;

    // Quantization rounding bias: mag = |qf| + 0.5 - DeadzoneBias, so 0.5 is round-to-nearest and lower values
    // round away from zero (keep more coefficient energy). Pure encoder rate-distortion choice — decoder is
    // unaffected. With RDOQ doing the optimal level trimming, a *low* bias wins: a hard deadzone here pre-empts
    // RDOQ's rate-distortion decision suboptimally. Retuned 2026-09 on a 6-image BD-rate corpus (photo/graphics/
    // text/texture) jointly with RdLambdaK=0.004 and RdoqLambdaScale=20 after chroma RDOQ was added: the joint
    // optimum is a broad basin at bias 0.02-0.04 (0.04 best, -6.2% BD-rate vs the prior 0.008/30/0.20 tuning; all
    // six images improved). Bias 0.00 (pure round-to-nearest) slightly overshoots (-5.9%) — a hair of deadzone helps.
    internal static double DeadzoneBias = 0.04;

    // Cached forward 1D matrices F = M^-1 (M = decoder's integer 1D inverse), keyed by (logSize<<2 | type1d).
    private static readonly ConcurrentDictionary<int, double[,]> FwdMatrixCache = new();

    /// <summary>Forward DCT_DCT + quantize a square residual (the common path). Sizes 4/8/16 use the matched
    /// matrix forward; 32/64 use the orthonormal DCT. rc layout: levels[kx*sh + ky], sh = min(N,32).</summary>
    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount)
        => n <= 16
            ? MatrixForward(residual, n, dcDq, acDq, rcCount, FwdTxType.DctDct, null)
            : ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, QuantScaleK, null);

    /// <summary>Forward transform + quantize for a chosen 2D type. ADST types are only valid for sizes 4/8/16;
    /// for 32/64 this falls back to DCT_DCT (orthonormal).</summary>
    internal static int[] ForwardQuantTyped(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, FwdTxType txType)
        => n <= 16
            ? MatrixForward(residual, n, dcDq, acDq, rcCount, txType, null)
            : ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, QuantScaleK, null);

    /// <summary>Forward transform + quantize, additionally returning the pre-quant float coefficients (qfOut,
    /// same rc indexing as the levels) so a caller can run rate-distortion optimized quantization. qfOut is in
    /// dq-normalized units: the dequant reconstruction error of level L is (qfOut[rc] - L)·dq in pixel domain.</summary>
    internal static int[] ForwardQuantTyped(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, FwdTxType txType, double[] qfOut)
        => n <= 16
            ? MatrixForward(residual, n, dcDq, acDq, rcCount, txType, qfOut)
            : ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, QuantScaleK, qfOut);

    // (horizontal/row 1D type, vertical/col 1D type) for a 2D forward type — inverse of the decoder's row(width,
    // txtp0)/col(height, txtp1) split: txtp0 is horizontal, txtp1 is vertical.
    private static (int H, int V) AxisTypes(FwdTxType t) => t switch
    {
        FwdTxType.DctDct => (Av1InvTransform.Type1dDct, Av1InvTransform.Type1dDct),
        FwdTxType.AdstAdst => (Av1InvTransform.Type1dAdst, Av1InvTransform.Type1dAdst),
        FwdTxType.AdstDct => (Av1InvTransform.Type1dDct, Av1InvTransform.Type1dAdst),   // ADST_DCT: H=Dct, V=Adst
        FwdTxType.DctAdst => (Av1InvTransform.Type1dAdst, Av1InvTransform.Type1dDct),   // DCT_ADST: H=Adst, V=Dct
        FwdTxType.VDct => (Av1InvTransform.Type1dIdentity, Av1InvTransform.Type1dDct), // V_DCT: col=Dct, row=Identity
        FwdTxType.HDct => (Av1InvTransform.Type1dDct, Av1InvTransform.Type1dIdentity), // H_DCT: row=Dct, col=Identity
        _ => (Av1InvTransform.Type1dIdentity, Av1InvTransform.Type1dIdentity),          // IDTX: identity both axes
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

    /// <summary>Matched forward transform + quantize for a RECTANGULAR tx (both axes ≤16, i.e. 4x8/8x4/8x16/16x8).
    /// Residual is h rows x w cols (row-major). Coefficient layout matches the decoder: rc = ky + kx*sh, sh =
    /// min(h,32), sw = min(w,32). The scale S = 2^(4+txShift+dqShift) / (isRect2 ? 181/256 : 1) — the same identity
    /// that gives S = 4N for square, with the extra 256/181 undoing the decoder's isRect2 √2 coefficient read.
    /// DCT_DCT only for now (chroma and the common luma case); ADST rect can be added later.</summary>
    internal static int[] ForwardQuantRect(ReadOnlySpan<int> residual, int w, int h, int txSizeIdx,
        int dcDq, int acDq, int rcCount, FwdTxType txType)
        => ForwardQuantRect(residual, w, h, txSizeIdx, dcDq, acDq, rcCount, txType, null);

    internal static int[] ForwardQuantRect(ReadOnlySpan<int> residual, int w, int h, int txSizeIdx,
        int dcDq, int acDq, int rcCount, FwdTxType txType, double[]? qfOut)
    {
        // 64-point axes zero their upper 32 inputs, so the probed matrix is singular — TX_64X64 uses the square
        // orthonormal path (DCT_DCT is the only type there).
        if (w == 64 && h == 64)
            return ForwardQuantSquare(residual, 64, dcDq, acDq, rcCount, QuantScaleK, qfOut);
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[txSizeIdx];
        int logW = tDim.Lw, logH = tDim.Lh;
        (int hType, int vType) = AxisTypes(txType);
        double[,] fh = ForwardMatrix(logW, hType);   // width (horizontal / row) forward
        double[,] fv = ForwardMatrix(logH, vType);   // height (vertical / column) forward
        int sw = Math.Min(w, 32), sh = Math.Min(h, 32);
        bool isRect2 = w * 2 == h || h * 2 == w;
        int dqShift = Math.Max(0, tDim.Ctx - 2);
        double s = (1 << (4 + Av1InvTransform.TxShift[txSizeIdx] + dqShift)) * (isRect2 ? 256.0 / 181.0 : 1.0);

        // Horizontal forward: t[y][kx] = sum_x Fh[kx][x] * res[y][x].
        var t = new double[h, sw];
        for (int y = 0; y < h; y++)
            for (int kx = 0; kx < sw; kx++)
            {
                double acc = 0;
                for (int x = 0; x < w; x++) acc += fh[kx, x] * residual[y * w + x];
                t[y, kx] = acc;
            }

        // Vertical forward + quant: C[ky][kx] = sum_y Fv[ky][y] * t[y][kx]; level = deadzone(C·S/dq).
        var levels = new int[rcCount];
        for (int kx = 0; kx < sw; kx++)
            for (int ky = 0; ky < sh; ky++)
            {
                double acc = 0;
                for (int y = 0; y < h; y++) acc += fv[ky, y] * t[y, kx];
                int dq = (kx == 0 && ky == 0) ? dcDq : acDq;
                double qf = acc * s / dq;
                if (qfOut != null) qfOut[kx * sh + ky] = qf;
                double mag = Math.Abs(qf) + 0.5 - DeadzoneBias;
                levels[kx * sh + ky] = mag < 1.0 ? 0 : (int)(Math.Sign(qf) * Math.Floor(mag));
            }

        return levels;
    }

    // Matched forward for sizes 4/8/16: C[ky][kx] = Fv[ky][·] · res · Fh[kx][·], level = deadzone(C·S/dq), S = 4N.
    private static int[] MatrixForward(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, FwdTxType txType, double[]? qfOut)
    {
        int logSize = System.Numerics.BitOperations.Log2((uint)n) - 2; // 4→0, 8→1, 16→2
        (int hType, int vType) = AxisTypes(txType);
        double[] fh = ForwardMatrixFlat(logSize, hType);
        double[] fv = ForwardMatrixFlat(logSize, vType);
        double s = 4.0 * n;

        // Horizontal forward: t[y][kx] = sum_x Fh[kx][x] * res[y][x], stored transposed (tT[kx][y]) so the vertical
        // pass reads it contiguously. Same products in the same order as the textbook loops: identical doubles.
        Span<double> tT = stackalloc double[n * n];
        for (int y = 0; y < n; y++)
        {
            var row = residual.Slice(y * n, n);
            for (int kx = 0; kx < n; kx++)
            {
                var f = fh.AsSpan(kx * n, n);
                double acc = 0;
                for (int x = 0; x < f.Length; x++) acc += f[x] * row[x];
                tT[kx * n + y] = acc;
            }
        }

        // Vertical forward + quant: C[ky][kx] = sum_y Fv[ky][y] * t[y][kx].
        var levels = new int[rcCount];
        for (int kx = 0; kx < n; kx++)
        {
            var col = tT.Slice(kx * n, n);
            for (int ky = 0; ky < n; ky++)
            {
                var f = fv.AsSpan(ky * n, n);
                double acc = 0;
                for (int y = 0; y < f.Length; y++) acc += f[y] * col[y];
                int dq = (kx == 0 && ky == 0) ? dcDq : acDq;
                double qf = acc * s / dq;
                if (qfOut != null) qfOut[kx * n + ky] = qf;
                double mag = Math.Abs(qf) + 0.5 - DeadzoneBias;
                levels[kx * n + ky] = mag < 1.0 ? 0 : (int)(Math.Sign(qf) * Math.Floor(mag));
            }
        }

        return levels;
    }

    // ForwardMatrix flattened row-major ([k * n + x]); read-only after construction.
    private static readonly ConcurrentDictionary<int, double[]> FwdMatrixFlatCache = new();
    private static double[] ForwardMatrixFlat(int logSize, int type1d) => FwdMatrixFlatCache.GetOrAdd((logSize << 2) | type1d, key =>
    {
        var m = ForwardMatrix(key >> 2, key & 3);
        int n = m.GetLength(0);
        var f = new double[n * n];
        for (int k = 0; k < n; k++) for (int x = 0; x < n; x++) f[k * n + x] = m[k, x];
        return f;
    });

    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, double k)
        => ForwardQuantSquare(residual, n, dcDq, acDq, rcCount, k, null);

    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount, double k, double[]? qfOut)
    {
        int kept = Math.Min(n, 32);
        int sh = kept;

        // Horizontal pass: rows → T[y][kx], stored transposed (tT[kx][y]); products and summation order as the
        // straightforward loops, so the doubles are identical.
        double[] basis = DctBasisFlat(n);
        var tT = t_fwdScratch is { } sc && sc.Length >= n * kept ? sc : (t_fwdScratch = new double[64 * 32]);
        for (int y = 0; y < n; y++)
        {
            var row = residual.Slice(y * n, n);
            for (int kx = 0; kx < kept; kx++)
            {
                var bk = basis.AsSpan(kx * n, n);
                double acc = 0;
                for (int x = 0; x < bk.Length; x++)
                {
                    acc += row[x] * bk[x];
                }

                tT[kx * n + y] = acc;
            }
        }

        // Vertical pass: columns → C[ky][kx].
        var levels = new int[rcCount];
        for (int kx = 0; kx < kept; kx++)
        {
            var col = tT.AsSpan(kx * n, n);
            for (int ky = 0; ky < kept; ky++)
            {
                var bk = basis.AsSpan(ky * n, n);
                double acc = 0;
                for (int y = 0; y < bk.Length; y++)
                {
                    acc += col[y] * bk[y];
                }

                int dq = (kx == 0 && ky == 0) ? dcDq : acDq;
                // Deadzone quantizer: |level| = floor(|qf| + 0.5 - bias), clamped at 0.
                double qf = acc * k / dq;
                if (qfOut != null) qfOut[kx * sh + ky] = qf;
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

    [ThreadStatic] private static double[]? t_fwdScratch;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double[]> BasisFlatCache = new();
    private static double[] DctBasisFlat(int n) => BasisFlatCache.GetOrAdd(n, nn =>
    {
        var b = DctBasis(nn);
        var f = new double[nn * nn];
        for (int k = 0; k < nn; k++) for (int i = 0; i < nn; i++) f[k * nn + i] = b[k, i];
        return f;
    });

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
