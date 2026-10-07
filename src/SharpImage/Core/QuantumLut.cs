namespace SharpImage.Core;

/// <summary>
/// Tables over every 16-bit quantum value, for per-sample functions that depend on the sample alone (gamma, levels,
/// pow / log operators). Each of the 65536 values is evaluated once, in parallel, with the same expression the
/// per-sample loop would use, so a table lookup returns exactly what the direct evaluation returns.
/// </summary>
internal static class QuantumLut
{
    /// <summary>Images with at least this many channel samples use a table instead of per-sample evaluation.</summary>
    public const long MinSamples = 1L << 17;

    private const int Size = Quantum.MaxValue + 1;
    private const int Chunk = 1024;

    public static ushort[] Build(Func<int, ushort> f)
    {
        var table = new ushort[Size];
        Parallel.For(0, Size / Chunk, c =>
        {
            for (int i = c * Chunk; i < (c + 1) * Chunk; i++)
            {
                table[i] = f(i);
            }
        });
        return table;
    }

    public static double[] BuildDouble(Func<int, double> f)
    {
        var table = new double[Size];
        Parallel.For(0, Size / Chunk, c =>
        {
            for (int i = c * Chunk; i < (c + 1) * Chunk; i++)
            {
                table[i] = f(i);
            }
        });
        return table;
    }
}
