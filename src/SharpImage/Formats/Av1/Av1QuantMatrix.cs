namespace SharpImage.Formats.Av1;

// AV1 quantizer matrices, a faithful port of dav1d's dav1d_init_qm_tables (src/qm.c). Levels 0..14 carry a
// matrix per plane type (0 = luma, 1 = chroma) and transform size; level 15 means "flat" (no matrix). All sizes
// derive from two base tables (Av1QmTables): the 32x32 matrix stored as its lower triangle, and 32x16. Tables are
// stored in the coefficient (transposed) order the decoder indexes cf[] with — hence the deliberately swapped
// w/h in the assignments below, exactly as in dav1d.
internal static class Av1QuantMatrix
{
    private const int Levels = 15;
    // [level][planeType][txSize] -> matrix (null for sizes with no table).
    private static readonly byte[]?[,,] Tbl = Build();

    /// <summary>The quantizer matrix for a qm level (0..15), plane type (0 luma / 1 chroma) and transform size
    /// ordinal (Av1TxSize / Av1RectTxSize); empty for level 15 (flat).</summary>
    internal static byte[]? Get(int level, int planeType, int txSize)
        => level >= Levels ? null : Tbl[level, planeType, txSize];

    private static byte[]?[,,] Build()
    {
        var t = new byte[]?[Levels, 2, (int)Av1RectTxSize.Count];
        for (int i = 0; i < Levels; i++)
            for (int j = 0; j < 2; j++)
            {
                var q32x16 = new byte[512];
                System.Array.Copy(Av1QmTables.QmTbl32x16, (i * 2 + j) * 512, q32x16, 0, 512);
                var tri = new byte[528];
                System.Array.Copy(Av1QmTables.QmTbl32x32T, (i * 2 + j) * 528, tri, 0, 528);

                var q32x32 = new byte[1024];
                Untriangle(q32x32, tri, 32);
                byte[] q4x4 = Subsample(q32x32, 32 * 3 + 3, 32, 8, 8, 16);
                byte[] q8x4 = Subsample(q32x16, 32 * 1 + 1, 16, 4, 4, 32);
                byte[] q8x8 = Subsample(q32x32, 32 * 1 + 1, 32, 4, 4, 64);
                byte[] q16x4 = Subsample(q32x16, 32 * 1 + 0, 16, 2, 4, 64);
                byte[] q16x8 = Subsample(q32x16, 32 * 0 + 0, 16, 2, 2, 128);
                byte[] q16x16 = Subsample(q32x32, 32 * 0 + 0, 32, 2, 2, 256);
                byte[] q32x8 = Subsample(q32x16, 32 * 0 + 0, 16, 1, 2, 256);
                byte[] q4x8 = Transpose(q8x4, 8, 4);
                byte[] q4x16 = Transpose(q16x4, 16, 4);
                byte[] q8x16 = Transpose(q16x8, 16, 8);
                byte[] q8x32 = Transpose(q32x8, 32, 8);
                byte[] q16x32 = Transpose(q32x16, 32, 16);

                // Note the inverted w/h: coefficients are stored transposed (dav1d).
                t[i, j, (int)Av1RectTxSize.Rtx4x8] = q8x4;
                t[i, j, (int)Av1RectTxSize.Rtx8x4] = q4x8;
                t[i, j, (int)Av1RectTxSize.Rtx4x16] = q16x4;
                t[i, j, (int)Av1RectTxSize.Rtx16x4] = q4x16;
                t[i, j, (int)Av1RectTxSize.Rtx8x16] = q16x8;
                t[i, j, (int)Av1RectTxSize.Rtx16x8] = q8x16;
                t[i, j, (int)Av1RectTxSize.Rtx8x32] = q32x8;
                t[i, j, (int)Av1RectTxSize.Rtx32x8] = q8x32;
                t[i, j, (int)Av1RectTxSize.Rtx16x32] = q32x16;
                t[i, j, (int)Av1RectTxSize.Rtx32x16] = q16x32;
                t[i, j, (int)Av1TxSize.Tx4x4] = q4x4;
                t[i, j, (int)Av1TxSize.Tx8x8] = q8x8;
                t[i, j, (int)Av1TxSize.Tx16x16] = q16x16;
                t[i, j, (int)Av1TxSize.Tx32x32] = q32x32;
                t[i, j, (int)Av1TxSize.Tx64x64] = q32x32;
                t[i, j, (int)Av1RectTxSize.Rtx64x32] = q32x32;
                t[i, j, (int)Av1RectTxSize.Rtx64x16] = q16x32;
                t[i, j, (int)Av1RectTxSize.Rtx32x64] = q32x32;
                t[i, j, (int)Av1RectTxSize.Rtx16x64] = q32x16;
            }
        return t;
    }

    // dav1d subsample(): every hstep-th column (of 32) of every vstep-th row, starting at src[off].
    private static byte[] Subsample(byte[] src, int off, int h, int hstep, int vstep, int size)
    {
        var dst = new byte[size];
        int d = 0;
        for (int y = 0; y < h; y += vstep)
            for (int x = 0; x < 32; x += hstep)
                dst[d++] = src[off + y * 32 + x];
        return dst;
    }

    // dav1d transpose(): dst[x*h + y] = src[y*w + x].
    private static byte[] Transpose(byte[] src, int w, int h)
    {
        var dst = new byte[w * h];
        for (int y = 0, yOff = 0; y < h; y++, yOff += w)
            for (int x = 0, xOff = 0; x < w; x++, xOff += h)
                dst[xOff + y] = src[yOff + x];
        return dst;
    }

    // dav1d untriangle(): expands a lower-triangular symmetric matrix to full sz x sz.
    private static void Untriangle(byte[] dst, byte[] src, int sz)
    {
        int d = 0, s = 0;
        for (int y = 0; y < sz; y++)
        {
            System.Array.Copy(src, s, dst, d, y + 1);
            int p = s + y;
            for (int x = y + 1; x < sz; x++)
            {
                p += x;
                dst[d + x] = src[p];
            }
            d += sz;
            s += y + 1;
        }
    }
}
