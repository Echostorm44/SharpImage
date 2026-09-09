// Forward transform + quantization for the AV1 encoder. The AV1 standard specifies only the INVERSE transform
// (Av1InvTransform); the forward transform is the encoder's choice. We use an orthonormal 2D DCT-II and scale
// so the decoder's normative inverse reconstructs the residual.
//
// Scaling derivation (square DCT_DCT): the decoder's inverse chain applies gain G_dec from dequantized
// coefficients to residual — measured from the DC fast path, residual ≈ dequant_DC * 181^2 / (256*4096*2^shift).
// Against an orthonormal DC (C[0,0] = N*mean ⇒ orthonormal inverse = C/N), G_dec = N * 181^2 /
// (256*4096*2^TxShift). Combined with the decoder dequant `coeff = quant*dq >> dqShift`, the round-trip
// quant = round( orthonormal_forward * 2^dqShift / (G_dec * dq) ) reduces to a single constant K = 8 for every
// square size (4..64): dqShift and G_dec both scale with size so K stays 8. Verified end-to-end vs the decoder.
using System;

namespace SharpImage.Formats.Av1;

internal static class Av1FwdTransform
{
    // Universal forward-quant scale: 2^dqShift / G_dec == 8 for all square DCT sizes (see header).
    private const double QuantScaleK = 8.0;

    /// <summary>Forward DCT + quantize a square residual block into the rc-indexed coefficient array the
    /// coefficient encoder consumes. <paramref name="residual"/> is row-major NxN (pixel - prediction). Only the
    /// lowest min(N,32) frequencies per axis are kept (AV1 codes at most 32x32 even for a 64x64 transform).
    /// <paramref name="dcDq"/>/<paramref name="acDq"/> are the decoder's DC/AC dequant values for this qindex.
    /// Returns levels indexed rc = kx*sh + ky (kx=horizontal freq, ky=vertical freq, sh=min(N,32)).</summary>
    internal static int[] ForwardQuantSquare(ReadOnlySpan<int> residual, int n, int dcDq, int acDq, int rcCount)
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
                int q = (int)Math.Round(acc * QuantScaleK / dq, MidpointRounding.AwayFromZero);
                levels[kx * sh + ky] = q;
            }
        }

        return levels;
    }

    // Orthonormal DCT-II basis: basis[k, n] = a(k) * sqrt(2/N) * cos(pi*(2n+1)*k / (2N)), a(0)=1/sqrt2 else 1.
    private static double[,] DctBasis(int n)
    {
        var b = new double[n, n];
        double s = Math.Sqrt(2.0 / n);
        for (int k = 0; k < n; k++)
        {
            double ak = k == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (int i = 0; i < n; i++)
            {
                b[k, i] = ak * s * Math.Cos(Math.PI * (2 * i + 1) * k / (2.0 * n));
            }
        }

        return b;
    }
}
