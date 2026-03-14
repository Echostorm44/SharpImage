using System.Runtime.CompilerServices;

namespace SharpImage.Compression;

/// <summary>
/// Forward and Inverse 8x8 Discrete Cosine Transform (DCT) for JPEG. Uses floating-point AAN algorithm (Arai, Agui,
/// Nakajima) matching the JPEG reference implementation for correctness.
/// </summary>
public static class Dct
{
    // Precomputed cosine values for 8-point DCT
    private static readonly double[] CosTable;
    private const double InvSqrt2 = 0.7071067811865475; // 1/sqrt(2)

    static Dct()
    {
        CosTable = new double[64];
        for (int u = 0;u < 8;u++)
        {
            for (int x = 0;x < 8;x++)
            {
                CosTable[u * 8 + x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
            }
        }
    }

    /// <summary>
    /// Inverse DCT: transforms 8x8 frequency-domain coefficients to spatial-domain samples. Input: dequantized
    /// coefficients. Output: pixel values (level-shifted, centered at 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void InverseDct(Span<int> block)
    {
        Span<double> workspace = stackalloc double[64];

        // Copy to workspace
        for (int i = 0;i < 64;i++)
        {
            workspace[i] = block[i];
        }

        // Row pass
        for (int row = 0;row < 8;row++)
        {
            Idct1D(workspace, row * 8);
        }

        // Column pass (transpose access)
        Span<double> column = stackalloc double[8];
        for (int col = 0;col < 8;col++)
        {
            for (int i = 0;i < 8;i++)
            {
                column[i] = workspace[i * 8 + col];
            }

            Idct1D(column, 0);

            for (int i = 0;i < 8;i++)
            {
                block[i * 8 + col] = (int)Math.Round(column[i]);
            }
        }
    }

    /// <summary>
    /// Forward DCT: transforms 8x8 spatial-domain samples to frequency-domain coefficients. Input: pixel values (level-
    /// shifted by -128). Output: DCT coefficients.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ForwardDct(Span<int> block)
    {
        Span<double> workspace = stackalloc double[64];

        // Copy to workspace
        for (int i = 0;i < 64;i++)
        {
            workspace[i] = block[i];
        }

        // Row pass
        for (int row = 0;row < 8;row++)
        {
            Fdct1D(workspace, row * 8);
        }

        // Column pass (transpose access)
        Span<double> column = stackalloc double[8];
        for (int col = 0;col < 8;col++)
        {
            for (int i = 0;i < 8;i++)
            {
                column[i] = workspace[i * 8 + col];
            }

            Fdct1D(column, 0);

            for (int i = 0;i < 8;i++)
            {
                block[i * 8 + col] = (int)Math.Round(column[i]);
            }
        }
    }

    /// <summary>
    /// 1D 8-point IDCT using the direct formula. f(x) = sum_{u=0..7} C(u) * F(u) * cos((2x+1)*u*pi/16) / 2
    /// </summary>
    private static void Idct1D(Span<double> data, int offset)
    {
        Span<double> result = stackalloc double[8];
        double s0 = data[offset]; // DC with C(0) = 1/sqrt(2)

        for (int x = 0;x < 8;x++)
        {
            double sum = s0 * CosTable[x] * InvSqrt2;
            for (int u = 1;u < 8;u++)
            {
                sum += data[offset + u] * CosTable[u * 8 + x];
            }

            result[x] = sum * 0.5;
        }

        for (int x = 0;x < 8;x++)
        {
            data[offset + x] = result[x];
        }
    }

    /// <summary>
    /// 1D 8-point forward DCT using the direct formula. F(u) = C(u) * sum_{x=0..7} f(x) * cos((2x+1)*u*pi/16) / 2
    /// </summary>
    private static void Fdct1D(Span<double> data, int offset)
    {
        Span<double> result = stackalloc double[8];

        for (int u = 0;u < 8;u++)
        {
            double sum = 0;
            for (int x = 0;x < 8;x++)
            {
                sum += data[offset + x] * CosTable[u * 8 + x];
            }

            double cu = u == 0 ? InvSqrt2 : 1.0;
            result[u] = cu * sum * 0.5;
        }

        for (int u = 0;u < 8;u++)
        {
            data[offset + u] = result[u];
        }
    }
}
