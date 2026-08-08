// AV1 inverse transform (ITX) kernels for the decoder
// Ported from dav1d: src/itx_1d.c + src/itx_tmpl.c (VideoLAN dav1d, BSD-2-Clause)
// Implements DCT, ADST, flipADST, identity, and WHT for sizes 4–64,
// plus the 2D inv_txfm_add dispatch that combines row + column transforms.

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 inverse transform kernels. All 1D transforms operate on int32 coefficient
/// arrays with a stride, applying butterfly/rotation operations in-place.
/// The 2D entry point (<see cref="InvTxfmAdd"/>) transposes, applies row transforms,
/// shifts, applies column transforms, and adds residuals to the prediction buffer.
/// </summary>
public static class Av1InvTransform
{
    /// <summary>
    /// Intermediate shift between row and column pass, indexed by RectTxfmSize.
    /// Ported from dav1d itx_tmpl.c inv_txfm_fn macro shift arguments.
    /// </summary>
    public static readonly byte[] TxShift = new byte[]
    {
        // Square:     4x4  8x8  16x16 32x32 64x64
                        0,   1,    2,    2,    2,
        // Rect:       4x8  8x4  8x16 16x8 16x32 32x16 32x64 64x32
                        0,   0,    1,   1,    1,    1,    1,    1,
        // Rect:       4x16 16x4 8x32 32x8 16x64 64x16
                        1,   1,    2,   2,    2,    2,
    };

    // 1D transform type enum for dispatch tables
    private const int Dct = 0;
    private const int Adst = 1;
    private const int FlipAdst = 2;
    private const int Identity = 3;

    // Tx1dTypes[txType] = { row_type, col_type } — maps 2D TxType to pair of 1D types
    // Note: AV1 enum names are {VERTICAL}_{HORIZONTAL}, but our row pass is horizontal
    // and column pass is vertical. dav1d handles this via a DSP remap layer; we apply
    // the swap directly in this table.
    private static readonly byte[,] Tx1dTypes = new byte[,]
    {
        { Dct,      Dct },      // DCT_DCT
        { Dct,      Adst },     // ADST_DCT      (ADST-vertical, DCT-horizontal)
        { Adst,     Dct },      // DCT_ADST      (DCT-vertical, ADST-horizontal)
        { Adst,     Adst },     // ADST_ADST
        { Dct,      FlipAdst }, // FLIPADST_DCT  (FLIPADST-vertical, DCT-horizontal)
        { FlipAdst, Dct },      // DCT_FLIPADST  (DCT-vertical, FLIPADST-horizontal)
        { FlipAdst, FlipAdst }, // FLIPADST_FLIPADST
        { FlipAdst, Adst },     // ADST_FLIPADST (ADST-vertical, FLIPADST-horizontal)
        { Adst,     FlipAdst }, // FLIPADST_ADST (FLIPADST-vertical, ADST-horizontal)
        { Identity, Identity }, // IDTX
        { Identity, Dct },      // V_DCT         (DCT-vertical, IDENTITY-horizontal)
        { Dct,      Identity }, // H_DCT         (IDENTITY-vertical, DCT-horizontal)
        { Identity, Adst },     // V_ADST        (ADST-vertical, IDENTITY-horizontal)
        { Adst,     Identity }, // H_ADST        (IDENTITY-vertical, ADST-horizontal)
        { Identity, FlipAdst }, // V_FLIPADST    (FLIPADST-vertical, IDENTITY-horizontal)
        { FlipAdst, Identity }, // H_FLIPADST    (IDENTITY-vertical, FLIPADST-horizontal)
    };

    // ========================================================================
    // 2D Inverse Transform — main entry point
    // ========================================================================

    /// <summary>
    /// Apply inverse transform and add residuals to the destination buffer.
    /// This is the main entry point used by reconstruction.
    /// </summary>
    /// <param name="dst">Destination pixel buffer (prediction to which residuals are added).</param>
    /// <param name="dstStride">Stride in pixels between rows of dst.</param>
    /// <param name="coeffs">Transform coefficients (cleared after use).</param>
    /// <param name="eob">End of block — number of non-zero coefficients minus 1.</param>
    /// <param name="txSizeIdx">Combined tx size index (0..18 covering square + rect).</param>
    /// <param name="shift">Right-shift between row and column pass.</param>
    /// <param name="txType">Transform type (DCT_DCT, ADST_DCT, etc.).</param>
    /// <param name="bitDepth">Bit depth (8, 10, or 12).</param>
    // Debug flag for targeted ITX tracing
    public static bool DbgTrace;

    public static void InvTxfmAdd(
        Span<byte> dst, int dstStride,
        Span<int> coeffs, int eob,
        int txSizeIdx, int shift,
        Av1TxType txType, int bitDepth)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[txSizeIdx];
        int w = 4 * tDim.W;
        int h = 4 * tDim.H;
        bool hasDcOnly = txType == Av1TxType.DctDct;
        bool isRect2 = w * 2 == h || h * 2 == w;
        int rnd = (1 << shift) >> 1;
        int pixelMax = (1 << bitDepth) - 1;

        // DC-only fast path
        if (eob < (hasDcOnly ? 1 : 0))
        {
            int dc = coeffs[0];
            int clearLen = Math.Min(w, 32) * Math.Min(h, 32);
            coeffs.Slice(0, clearLen).Clear(); // clear full range, not just cf[0], to prevent stale data leaking into next full IDCT
            if (isRect2)
                dc = (dc * 181 + 128) >> 8;
            dc = (dc * 181 + 128) >> 8;
            dc = (dc + rnd) >> shift;
            dc = (dc * 181 + 128 + 2048) >> 12;
            for (int y = 0; y < h; y++)
            {
                var row = dst.Slice(y * dstStride, w);
                for (int x = 0; x < w; x++)
                    row[x] = (byte)Math.Clamp(row[x] + dc, 0, pixelMax);
            }
            return;
        }

        int txtp0 = Tx1dTypes[(int)txType, 0]; // row (first pass)
        int txtp1 = Tx1dTypes[(int)txType, 1]; // column (second pass)

        int sh = Math.Min(h, 32);
        int sw = Math.Min(w, 32);

        // Clip ranges per AV1 spec (matches dav1d itx_tmpl.c)
        int rowClipMin = (int)((uint)~pixelMax << 7);
        int colClipMin = (int)((uint)~pixelMax << 5);
        int rowClipMax = ~rowClipMin;
        int colClipMax = ~colClipMin;

        // Working buffer for row-pass output / column-pass input
        Span<int> tmp = stackalloc int[64 * 64];
        tmp.Slice(0, w * sh).Clear();

        // Row pass: read coefficients in scan order, apply 1st 1D transform
        for (int y = 0; y < sh; y++)
        {
            var row = tmp.Slice(y * w, w);
            if (isRect2)
            {
                for (int x = 0; x < sw; x++)
                    row[x] = (coeffs[y + x * sh] * 181 + 128) >> 8;
            }
            else
            {
                for (int x = 0; x < sw; x++)
                    row[x] = coeffs[y + x * sh];
            }
            if (DbgTrace && y == 0)
            {
                AvDbg.W($"[ITX-DBG] pre-row0: ");
                for (int x = 0; x < w; x++) AvDbg.W($"{row[x]} ");
                AvDbg.W($" txtp0={txtp0}");
            }
            Apply1d(row, 1, rowClipMin, rowClipMax, tDim.Lw, txtp0);
            if (DbgTrace && y == 0)
            {
                AvDbg.W($"[ITX-DBG] post-row0: ");
                for (int x = 0; x < w; x++) AvDbg.W($"{row[x]} ");
                AvDbg.W();
            }
        }

        // Clear source coefficients
        coeffs.Slice(0, sw * sh).Clear();

        // Intermediate shift + clip
        for (int i = 0; i < w * sh; i++)
            tmp[i] = Math.Clamp((tmp[i] + rnd) >> shift, colClipMin, colClipMax);

        if (DbgTrace)
        {
            AvDbg.W("[ITX-DBG] after-shift: ");
            for (int y = 0; y < h; y++)
            {
                AvDbg.W("|");
                for (int x = 0; x < w; x++)
                    AvDbg.W($" {tmp[y * w + x]}");
                AvDbg.W(" ");
            }
            AvDbg.W();
        }

        // Column pass
        for (int x = 0; x < w; x++)
            Apply1dStrided(tmp, x, w, colClipMin, colClipMax, tDim.Lh, txtp1, h);

        if (DbgTrace)
        {
            AvDbg.W($"[ITX-DBG] after-col (residuals): ");
            for (int y = 0; y < h; y++)
            {
                AvDbg.W("|");
                for (int x = 0; x < w; x++)
                    AvDbg.W($" {tmp[y * w + x]}");
                AvDbg.W(" ");
            }
            AvDbg.W($" txtp1={txtp1}");
        }

        // Add residuals to destination
        for (int y = 0; y < h; y++)
        {
            var row = dst.Slice(y * dstStride, w);
            var tmpRow = tmp.Slice(y * w, w);
            for (int x = 0; x < w; x++)
                row[x] = (byte)Math.Clamp(row[x] + ((tmpRow[x] + 8) >> 4), 0, pixelMax);
        }
    }

    /// <summary>
    /// Apply inverse transform and add residuals to 16-bit destination (10/12-bit).
    /// </summary>
    public static void InvTxfmAdd16(
        Span<ushort> dst, int dstStride,
        Span<int> coeffs, int eob,
        int txSizeIdx, int shift,
        Av1TxType txType, int bitDepth)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[txSizeIdx];
        int w = 4 * tDim.W;
        int h = 4 * tDim.H;
        bool hasDcOnly = txType == Av1TxType.DctDct;
        bool isRect2 = w * 2 == h || h * 2 == w;
        int rnd = (1 << shift) >> 1;
        int pixelMax = (1 << bitDepth) - 1;

        int rowClipMin = (int)((uint)~pixelMax << 7);
        int colClipMin = (int)((uint)~pixelMax << 5);
        int rowClipMax = ~rowClipMin;
        int colClipMax = ~colClipMin;

        if (eob < (hasDcOnly ? 1 : 0))
        {
            int dc = coeffs[0];
            coeffs[0] = 0;
            if (isRect2)
                dc = (dc * 181 + 128) >> 8;
            dc = (dc * 181 + 128) >> 8;
            dc = (dc + rnd) >> shift;
            dc = (dc * 181 + 128 + 2048) >> 12;
            for (int y = 0; y < h; y++)
            {
                var row = dst.Slice(y * dstStride, w);
                for (int x = 0; x < w; x++)
                    row[x] = (ushort)Math.Clamp(row[x] + dc, 0, pixelMax);
            }
            return;
        }

        int txtp0 = Tx1dTypes[(int)txType, 0];
        int txtp1 = Tx1dTypes[(int)txType, 1];
        int sh = Math.Min(h, 32);
        int sw = Math.Min(w, 32);

        Span<int> tmp = stackalloc int[64 * 64];
        tmp.Slice(0, w * sh).Clear();

        for (int y = 0; y < sh; y++)
        {
            var row = tmp.Slice(y * w, w);
            if (isRect2)
                for (int x = 0; x < sw; x++)
                    row[x] = (coeffs[y + x * sh] * 181 + 128) >> 8;
            else
                for (int x = 0; x < sw; x++)
                    row[x] = coeffs[y + x * sh];
            Apply1d(row, 1, rowClipMin, rowClipMax, tDim.Lw, txtp0);
        }

        coeffs.Slice(0, sw * sh).Clear();
        for (int i = 0; i < w * sh; i++)
            tmp[i] = Math.Clamp((tmp[i] + rnd) >> shift, colClipMin, colClipMax);

        for (int x = 0; x < w; x++)
            Apply1dStrided(tmp, x, w, colClipMin, colClipMax, tDim.Lh, txtp1, h);

        for (int y = 0; y < h; y++)
        {
            var row = dst.Slice(y * dstStride, w);
            var tmpRow = tmp.Slice(y * w, w);
            for (int x = 0; x < w; x++)
                row[x] = (ushort)Math.Clamp(row[x] + ((tmpRow[x] + 8) >> 4), 0, pixelMax);
        }
    }

    /// <summary>
    /// WHT 4x4 inverse transform and add (lossless mode only).
    /// </summary>
    public static void InvWhtAdd(
        Span<byte> dst, int dstStride,
        Span<int> coeffs, int bitDepth)
    {
        int pixelMax = (1 << bitDepth) - 1;
        Span<int> tmp = stackalloc int[16];
        for (int y = 0; y < 4; y++)
        {
            var row = tmp.Slice(y * 4, 4);
            for (int x = 0; x < 4; x++)
                row[x] = coeffs[y + x * 4] >> 2;
            InvWht4_1d(row, 1);
        }
        coeffs.Slice(0, 16).Clear();

        for (int x = 0; x < 4; x++)
            InvWht4_1dStrided(tmp, x, 4);

        for (int y = 0; y < 4; y++)
        {
            var row = dst.Slice(y * dstStride, 4);
            var tmpRow = tmp.Slice(y * 4, 4);
            for (int x = 0; x < 4; x++)
                row[x] = (byte)Math.Clamp(row[x] + tmpRow[x], 0, pixelMax);
        }
    }

    // ========================================================================
    // 1D transform dispatch
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Apply1d(Span<int> c, int stride, int min, int max, int logSize, int type)
    {
        switch ((logSize << 2) | type)
        {
            case (0 << 2) | Dct:      InvDct4(c, stride, min, max, false); break;
            case (0 << 2) | Adst:     InvAdst4(c, stride, min, max, c, stride); break;
            case (0 << 2) | FlipAdst: FlipAdstApply(c, stride, min, max, 4); break;
            case (0 << 2) | Identity: InvIdentity4(c, stride); break;
            case (1 << 2) | Dct:      InvDct8(c, stride, min, max, false); break;
            case (1 << 2) | Adst:     InvAdst8(c, stride, min, max, c, stride); break;
            case (1 << 2) | FlipAdst: FlipAdstApply(c, stride, min, max, 8); break;
            case (1 << 2) | Identity: InvIdentity8(c, stride); break;
            case (2 << 2) | Dct:      InvDct16(c, stride, min, max, false); break;
            case (2 << 2) | Adst:     InvAdst16(c, stride, min, max, c, stride); break;
            case (2 << 2) | FlipAdst: FlipAdstApply(c, stride, min, max, 16); break;
            case (2 << 2) | Identity: InvIdentity16(c, stride); break;
            case (3 << 2) | Dct:      InvDct32(c, stride, min, max, false); break;
            case (3 << 2) | Identity: InvIdentity32(c, stride); break;
            case (4 << 2) | Dct:      InvDct64(c, stride, min, max); break;
        }
    }

    /// <summary>
    /// FlipADST: run ADST into a temp buffer, then reverse-copy back.
    /// This avoids negative stride indexing which Span does not support.
    /// </summary>
    private static void FlipAdstApply(Span<int> c, int stride, int min, int max, int size)
    {
        Span<int> tmp = stackalloc int[16]; // max ADST size is 16
        var tmpSlice = tmp.Slice(0, size);

        switch (size)
        {
            case 4:  InvAdst4(c, stride, min, max, tmpSlice, 1); break;
            case 8:  InvAdst8(c, stride, min, max, tmpSlice, 1); break;
            case 16: InvAdst16(c, stride, min, max, tmpSlice, 1); break;
        }

        // Reverse-copy back: output[0] gets tmp[size-1], etc.
        for (int i = 0; i < size; i++)
            c[i * stride] = tmpSlice[size - 1 - i];
    }

    // Strided version for column pass (offset + stride into tmp buffer)
    private static void Apply1dStrided(Span<int> buf, int offset, int stride, int min, int max, int logSize, int type, int size)
    {
        switch ((logSize << 2) | type)
        {
            case (0 << 2) | Dct:      InvDct4(buf.Slice(offset), stride, min, max, false); break;
            case (0 << 2) | Adst:     InvAdst4(buf.Slice(offset), stride, min, max, buf.Slice(offset), stride); break;
            case (0 << 2) | FlipAdst: FlipAdstApplyStrided(buf, offset, stride, min, max, 4); break;
            case (0 << 2) | Identity: InvIdentity4(buf.Slice(offset), stride); break;
            case (1 << 2) | Dct:      InvDct8(buf.Slice(offset), stride, min, max, false); break;
            case (1 << 2) | Adst:     InvAdst8(buf.Slice(offset), stride, min, max, buf.Slice(offset), stride); break;
            case (1 << 2) | FlipAdst: FlipAdstApplyStrided(buf, offset, stride, min, max, 8); break;
            case (1 << 2) | Identity: InvIdentity8(buf.Slice(offset), stride); break;
            case (2 << 2) | Dct:      InvDct16(buf.Slice(offset), stride, min, max, false); break;
            case (2 << 2) | Adst:     InvAdst16(buf.Slice(offset), stride, min, max, buf.Slice(offset), stride); break;
            case (2 << 2) | FlipAdst: FlipAdstApplyStrided(buf, offset, stride, min, max, 16); break;
            case (2 << 2) | Identity: InvIdentity16(buf.Slice(offset), stride); break;
            case (3 << 2) | Dct:      InvDct32(buf.Slice(offset), stride, min, max, false); break;
            case (3 << 2) | Identity: InvIdentity32(buf.Slice(offset), stride); break;
            case (4 << 2) | Dct:      InvDct64(buf.Slice(offset), stride, min, max); break;
        }
    }

    /// <summary>
    /// FlipADST for strided (column) pass: ADST into temp, reverse-copy to strided destination.
    /// </summary>
    private static void FlipAdstApplyStrided(Span<int> buf, int offset, int stride, int min, int max, int size)
    {
        Span<int> tmp = stackalloc int[16];
        var tmpSlice = tmp.Slice(0, size);

        switch (size)
        {
            case 4:  InvAdst4(buf.Slice(offset), stride, min, max, tmpSlice, 1); break;
            case 8:  InvAdst8(buf.Slice(offset), stride, min, max, tmpSlice, 1); break;
            case 16: InvAdst16(buf.Slice(offset), stride, min, max, tmpSlice, 1); break;
        }

        for (int i = 0; i < size; i++)
            buf[offset + i * stride] = tmpSlice[size - 1 - i];
    }

    // ========================================================================
    // Clip helper
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip(int v, int min, int max) => Math.Clamp(v, min, max);

    // ========================================================================
    // DCT kernels
    // ========================================================================

    private static void InvDct4(Span<int> c, int stride, int min, int max, bool tx64)
    {
        int in0 = c[0 * stride], in1 = c[1 * stride];
        int t0, t1, t2, t3;
        if (tx64)
        {
            t0 = t1 = (in0 * 181 + 128) >> 8;
            t2 = (in1 * 1567 + 2048) >> 12;
            t3 = (in1 * 3784 + 2048) >> 12;
        }
        else
        {
            int in2 = c[2 * stride], in3 = c[3 * stride];
            t0 = ((in0 + in2) * 181 + 128) >> 8;
            t1 = ((in0 - in2) * 181 + 128) >> 8;
            t2 = ((in1 * 1567 - in3 * (3784 - 4096) + 2048) >> 12) - in3;
            t3 = ((in1 * (3784 - 4096) + in3 * 1567 + 2048) >> 12) + in1;
        }
        c[0 * stride] = Clip(t0 + t3, min, max);
        c[1 * stride] = Clip(t1 + t2, min, max);
        c[2 * stride] = Clip(t1 - t2, min, max);
        c[3 * stride] = Clip(t0 - t3, min, max);
    }

    private static void InvDct8(Span<int> c, int stride, int min, int max, bool tx64)
    {
        InvDct4(c, stride << 1, min, max, tx64);
        int in1 = c[1 * stride], in3 = c[3 * stride];
        int t4a, t5a, t6a, t7a;
        if (tx64)
        {
            t4a = (in1 * 799 + 2048) >> 12;
            t5a = (in3 * -2276 + 2048) >> 12;
            t6a = (in3 * 3406 + 2048) >> 12;
            t7a = (in1 * 4017 + 2048) >> 12;
        }
        else
        {
            int in5 = c[5 * stride], in7 = c[7 * stride];
            t4a = ((in1 * 799 - in7 * (4017 - 4096) + 2048) >> 12) - in7;
            t5a = (in5 * 1703 - in3 * 1138 + 1024) >> 11;
            t6a = (in5 * 1138 + in3 * 1703 + 1024) >> 11;
            t7a = ((in1 * (4017 - 4096) + in7 * 799 + 2048) >> 12) + in1;
        }
        int t4 = Clip(t4a + t5a, min, max);
        t5a = Clip(t4a - t5a, min, max);
        int t7 = Clip(t7a + t6a, min, max);
        t6a = Clip(t7a - t6a, min, max);
        int t5 = ((t6a - t5a) * 181 + 128) >> 8;
        int t6 = ((t6a + t5a) * 181 + 128) >> 8;

        int r0 = c[0 * stride], r1 = c[2 * stride], r2 = c[4 * stride], r3 = c[6 * stride];
        c[0 * stride] = Clip(r0 + t7, min, max);
        c[1 * stride] = Clip(r1 + t6, min, max);
        c[2 * stride] = Clip(r2 + t5, min, max);
        c[3 * stride] = Clip(r3 + t4, min, max);
        c[4 * stride] = Clip(r3 - t4, min, max);
        c[5 * stride] = Clip(r2 - t5, min, max);
        c[6 * stride] = Clip(r1 - t6, min, max);
        c[7 * stride] = Clip(r0 - t7, min, max);
    }

    private static void InvDct16(Span<int> c, int stride, int min, int max, bool tx64)
    {
        InvDct8(c, stride << 1, min, max, tx64);
        int in1 = c[1 * stride], in3 = c[3 * stride];
        int in5 = c[5 * stride], in7 = c[7 * stride];
        int t8a, t9a, t10a, t11a, t12a, t13a, t14a, t15a;
        if (tx64)
        {
            t8a = (in1 * 401 + 2048) >> 12;
            t9a = (in7 * -2598 + 2048) >> 12;
            t10a = (in5 * 1931 + 2048) >> 12;
            t11a = (in3 * -1189 + 2048) >> 12;
            t12a = (in3 * 3920 + 2048) >> 12;
            t13a = (in5 * 3612 + 2048) >> 12;
            t14a = (in7 * 3166 + 2048) >> 12;
            t15a = (in1 * 4076 + 2048) >> 12;
        }
        else
        {
            int in9 = c[9 * stride], in11 = c[11 * stride];
            int in13 = c[13 * stride], in15 = c[15 * stride];
            t8a = ((in1 * 401 - in15 * (4076 - 4096) + 2048) >> 12) - in15;
            t9a = (in9 * 1583 - in7 * 1299 + 1024) >> 11;
            t10a = ((in5 * 1931 - in11 * (3612 - 4096) + 2048) >> 12) - in11;
            t11a = ((in13 * (3920 - 4096) - in3 * 1189 + 2048) >> 12) + in13;
            t12a = ((in13 * 1189 + in3 * (3920 - 4096) + 2048) >> 12) + in3;
            t13a = ((in5 * (3612 - 4096) + in11 * 1931 + 2048) >> 12) + in5;
            t14a = (in9 * 1299 + in7 * 1583 + 1024) >> 11;
            t15a = ((in1 * (4076 - 4096) + in15 * 401 + 2048) >> 12) + in1;
        }

        int t8 = Clip(t8a + t9a, min, max);
        int t9 = Clip(t8a - t9a, min, max);
        int t10 = Clip(t11a - t10a, min, max);
        int t11 = Clip(t11a + t10a, min, max);
        int t12 = Clip(t12a + t13a, min, max);
        int t13 = Clip(t12a - t13a, min, max);
        int t14 = Clip(t15a - t14a, min, max);
        int t15 = Clip(t15a + t14a, min, max);

        t9a = ((t14 * 1567 - t9 * (3784 - 4096) + 2048) >> 12) - t9;
        t14a = ((t14 * (3784 - 4096) + t9 * 1567 + 2048) >> 12) + t14;
        t10a = ((-(t13 * (3784 - 4096) + t10 * 1567) + 2048) >> 12) - t13;
        t13a = ((t13 * 1567 - t10 * (3784 - 4096) + 2048) >> 12) - t10;

        int t8b = Clip(t8 + t11, min, max);
        t9 = Clip(t9a + t10a, min, max);
        t10 = Clip(t9a - t10a, min, max);
        int t11b = Clip(t8 - t11, min, max);
        int t12b = Clip(t15 - t12, min, max);
        t13 = Clip(t14a - t13a, min, max);
        t14 = Clip(t14a + t13a, min, max);
        int t15b = Clip(t15 + t12, min, max);

        t10a = ((t13 - t10) * 181 + 128) >> 8;
        t13a = ((t13 + t10) * 181 + 128) >> 8;
        t11 = ((t12b - t11b) * 181 + 128) >> 8;
        t12 = ((t12b + t11b) * 181 + 128) >> 8;

        int r0 = c[0 * stride], r1 = c[2 * stride], r2 = c[4 * stride], r3 = c[6 * stride];
        int r4 = c[8 * stride], r5 = c[10 * stride], r6 = c[12 * stride], r7 = c[14 * stride];

        c[0 * stride] = Clip(r0 + t15b, min, max);
        c[1 * stride] = Clip(r1 + t14, min, max);
        c[2 * stride] = Clip(r2 + t13a, min, max);
        c[3 * stride] = Clip(r3 + t12, min, max);
        c[4 * stride] = Clip(r4 + t11, min, max);
        c[5 * stride] = Clip(r5 + t10a, min, max);
        c[6 * stride] = Clip(r6 + t9, min, max);
        c[7 * stride] = Clip(r7 + t8b, min, max);
        c[8 * stride] = Clip(r7 - t8b, min, max);
        c[9 * stride] = Clip(r6 - t9, min, max);
        c[10 * stride] = Clip(r5 - t10a, min, max);
        c[11 * stride] = Clip(r4 - t11, min, max);
        c[12 * stride] = Clip(r3 - t12, min, max);
        c[13 * stride] = Clip(r2 - t13a, min, max);
        c[14 * stride] = Clip(r1 - t14, min, max);
        c[15 * stride] = Clip(r0 - t15b, min, max);
    }

    private static void InvDct32(Span<int> c, int stride, int min, int max, bool tx64)
    {
        InvDct16(c, stride << 1, min, max, tx64);
        int in1 = c[1 * stride], in3 = c[3 * stride];
        int in5 = c[5 * stride], in7 = c[7 * stride];
        int in9 = c[9 * stride], in11 = c[11 * stride];
        int in13 = c[13 * stride], in15 = c[15 * stride];

        int t16a, t17a, t18a, t19a, t20a, t21a, t22a, t23a;
        int t24a, t25a, t26a, t27a, t28a, t29a, t30a, t31a;
        if (tx64)
        {
            t16a = (in1 * 201 + 2048) >> 12;   t17a = (in15 * -2751 + 2048) >> 12;
            t18a = (in9 * 1751 + 2048) >> 12;  t19a = (in7 * -1380 + 2048) >> 12;
            t20a = (in5 * 995 + 2048) >> 12;   t21a = (in11 * -2106 + 2048) >> 12;
            t22a = (in13 * 2440 + 2048) >> 12; t23a = (in3 * -601 + 2048) >> 12;
            t24a = (in3 * 4052 + 2048) >> 12;  t25a = (in13 * 3290 + 2048) >> 12;
            t26a = (in11 * 3513 + 2048) >> 12; t27a = (in5 * 3973 + 2048) >> 12;
            t28a = (in7 * 3857 + 2048) >> 12;  t29a = (in9 * 3703 + 2048) >> 12;
            t30a = (in15 * 3035 + 2048) >> 12; t31a = (in1 * 4091 + 2048) >> 12;
        }
        else
        {
            int in17 = c[17 * stride], in19 = c[19 * stride];
            int in21 = c[21 * stride], in23 = c[23 * stride];
            int in25 = c[25 * stride], in27 = c[27 * stride];
            int in29 = c[29 * stride], in31 = c[31 * stride];

            t16a = ((in1 * 201 - in31 * (4091 - 4096) + 2048) >> 12) - in31;
            t17a = ((in17 * (3035 - 4096) - in15 * 2751 + 2048) >> 12) + in17;
            t18a = ((in9 * 1751 - in23 * (3703 - 4096) + 2048) >> 12) - in23;
            t19a = ((in25 * (3857 - 4096) - in7 * 1380 + 2048) >> 12) + in25;
            t20a = ((in5 * 995 - in27 * (3973 - 4096) + 2048) >> 12) - in27;
            t21a = ((in21 * (3513 - 4096) - in11 * 2106 + 2048) >> 12) + in21;
            t22a = (in13 * 1220 - in19 * 1645 + 1024) >> 11;
            t23a = ((in29 * (4052 - 4096) - in3 * 601 + 2048) >> 12) + in29;
            t24a = ((in29 * 601 + in3 * (4052 - 4096) + 2048) >> 12) + in3;
            t25a = (in13 * 1645 + in19 * 1220 + 1024) >> 11;
            t26a = ((in21 * 2106 + in11 * (3513 - 4096) + 2048) >> 12) + in11;
            t27a = ((in5 * (3973 - 4096) + in27 * 995 + 2048) >> 12) + in5;
            t28a = ((in25 * 1380 + in7 * (3857 - 4096) + 2048) >> 12) + in7;
            t29a = ((in9 * (3703 - 4096) + in23 * 1751 + 2048) >> 12) + in9;
            t30a = ((in17 * 2751 + in15 * (3035 - 4096) + 2048) >> 12) + in15;
            t31a = ((in1 * (4091 - 4096) + in31 * 201 + 2048) >> 12) + in1;
        }

        int t16 = Clip(t16a + t17a, min, max); int t17 = Clip(t16a - t17a, min, max);
        int t18 = Clip(t19a - t18a, min, max); int t19 = Clip(t19a + t18a, min, max);
        int t20 = Clip(t20a + t21a, min, max); int t21 = Clip(t20a - t21a, min, max);
        int t22 = Clip(t23a - t22a, min, max); int t23 = Clip(t23a + t22a, min, max);
        int t24 = Clip(t24a + t25a, min, max); int t25 = Clip(t24a - t25a, min, max);
        int t26 = Clip(t27a - t26a, min, max); int t27 = Clip(t27a + t26a, min, max);
        int t28 = Clip(t28a + t29a, min, max); int t29 = Clip(t28a - t29a, min, max);
        int t30 = Clip(t31a - t30a, min, max); int t31 = Clip(t31a + t30a, min, max);

        t17a = ((t30 * 799 - t17 * (4017 - 4096) + 2048) >> 12) - t17;
        t30a = ((t30 * (4017 - 4096) + t17 * 799 + 2048) >> 12) + t30;
        t18a = ((-(t29 * (4017 - 4096) + t18 * 799) + 2048) >> 12) - t29;
        t29a = ((t29 * 799 - t18 * (4017 - 4096) + 2048) >> 12) - t18;
        t21a = (t26 * 1703 - t21 * 1138 + 1024) >> 11;
        t26a = (t26 * 1138 + t21 * 1703 + 1024) >> 11;
        t22a = (-(t25 * 1138 + t22 * 1703) + 1024) >> 11;
        t25a = (t25 * 1703 - t22 * 1138 + 1024) >> 11;

        t16a = Clip(t16 + t19, min, max);
        t17 = Clip(t17a + t18a, min, max);  t18 = Clip(t17a - t18a, min, max);
        int t19b = Clip(t16 - t19, min, max);
        int t20b = Clip(t23 - t20, min, max);
        t21 = Clip(t22a - t21a, min, max);  t22 = Clip(t22a + t21a, min, max);
        int t23b = Clip(t23 + t20, min, max);
        int t24b = Clip(t24 + t27, min, max);
        t25 = Clip(t25a + t26a, min, max);  t26 = Clip(t25a - t26a, min, max);
        int t27b = Clip(t24 - t27, min, max);
        int t28b = Clip(t31 - t28, min, max);
        t29 = Clip(t30a - t29a, min, max);  t30 = Clip(t30a + t29a, min, max);
        int t31b = Clip(t31 + t28, min, max);

        t18a = ((t29 * 1567 - t18 * (3784 - 4096) + 2048) >> 12) - t18;
        t29a = ((t29 * (3784 - 4096) + t18 * 1567 + 2048) >> 12) + t29;
        t19 = ((t28b * 1567 - t19b * (3784 - 4096) + 2048) >> 12) - t19b;
        t28 = ((t28b * (3784 - 4096) + t19b * 1567 + 2048) >> 12) + t28b;
        t20 = ((-(t27b * (3784 - 4096) + t20b * 1567) + 2048) >> 12) - t27b;
        t27 = ((t27b * 1567 - t20b * (3784 - 4096) + 2048) >> 12) - t20b;
        t21a = ((-(t26 * (3784 - 4096) + t21 * 1567) + 2048) >> 12) - t26;
        t26a = ((t26 * 1567 - t21 * (3784 - 4096) + 2048) >> 12) - t21;

        t16 = Clip(t16a + t23b, min, max);
        t17a = Clip(t17 + t22, min, max);   t18 = Clip(t18a + t21a, min, max);
        int t19c = Clip(t19 + t20, min, max);
        int t20c = Clip(t19 - t20, min, max);
        t21 = Clip(t18a - t21a, min, max);  int t22b = Clip(t17 - t22, min, max);
        t23 = Clip(t16a - t23b, min, max);
        t24 = Clip(t31b - t24b, min, max);
        t25a = Clip(t30 - t25, min, max);   t26 = Clip(t29a - t26a, min, max);
        int t27c = Clip(t28 - t27, min, max);
        int t28c = Clip(t28 + t27, min, max);
        t29 = Clip(t29a + t26a, min, max);  t30a = Clip(t30 + t25, min, max);
        t31 = Clip(t31b + t24b, min, max);

        t20 = ((t27c - t20c) * 181 + 128) >> 8;
        t27 = ((t27c + t20c) * 181 + 128) >> 8;
        t21a = ((t26 - t21) * 181 + 128) >> 8;
        t26a = ((t26 + t21) * 181 + 128) >> 8;
        t22 = ((t25a - t22b) * 181 + 128) >> 8;
        t25 = ((t25a + t22b) * 181 + 128) >> 8;
        int t23c = ((t24 - t23) * 181 + 128) >> 8;
        int t24c = ((t24 + t23) * 181 + 128) >> 8;

        int r0 = c[0 * stride], r1 = c[2 * stride], r2 = c[4 * stride], r3 = c[6 * stride];
        int r4 = c[8 * stride], r5 = c[10 * stride], r6 = c[12 * stride], r7 = c[14 * stride];
        int r8 = c[16 * stride], r9 = c[18 * stride], r10 = c[20 * stride], r11 = c[22 * stride];
        int r12 = c[24 * stride], r13 = c[26 * stride], r14 = c[28 * stride], r15 = c[30 * stride];

        c[0 * stride] = Clip(r0 + t31, min, max);   c[1 * stride] = Clip(r1 + t30a, min, max);
        c[2 * stride] = Clip(r2 + t29, min, max);   c[3 * stride] = Clip(r3 + t28c, min, max);
        c[4 * stride] = Clip(r4 + t27, min, max);   c[5 * stride] = Clip(r5 + t26a, min, max);
        c[6 * stride] = Clip(r6 + t25, min, max);   c[7 * stride] = Clip(r7 + t24c, min, max);
        c[8 * stride] = Clip(r8 + t23c, min, max);  c[9 * stride] = Clip(r9 + t22, min, max);
        c[10 * stride] = Clip(r10 + t21a, min, max); c[11 * stride] = Clip(r11 + t20, min, max);
        c[12 * stride] = Clip(r12 + t19c, min, max); c[13 * stride] = Clip(r13 + t18, min, max);
        c[14 * stride] = Clip(r14 + t17a, min, max); c[15 * stride] = Clip(r15 + t16, min, max);
        c[16 * stride] = Clip(r15 - t16, min, max); c[17 * stride] = Clip(r14 - t17a, min, max);
        c[18 * stride] = Clip(r13 - t18, min, max); c[19 * stride] = Clip(r12 - t19c, min, max);
        c[20 * stride] = Clip(r11 - t20, min, max); c[21 * stride] = Clip(r10 - t21a, min, max);
        c[22 * stride] = Clip(r9 - t22, min, max);  c[23 * stride] = Clip(r8 - t23c, min, max);
        c[24 * stride] = Clip(r7 - t24c, min, max); c[25 * stride] = Clip(r6 - t25, min, max);
        c[26 * stride] = Clip(r5 - t26a, min, max); c[27 * stride] = Clip(r4 - t27, min, max);
        c[28 * stride] = Clip(r3 - t28c, min, max); c[29 * stride] = Clip(r2 - t29, min, max);
        c[30 * stride] = Clip(r1 - t30a, min, max); c[31 * stride] = Clip(r0 - t31, min, max);
    }

    private static void InvDct64(Span<int> c, int stride, int min, int max)
    {
        InvDct32(c, stride << 1, min, max, true);

        int in1 = c[1 * stride], in3 = c[3 * stride], in5 = c[5 * stride], in7 = c[7 * stride];
        int in9 = c[9 * stride], in11 = c[11 * stride], in13 = c[13 * stride], in15 = c[15 * stride];
        int in17 = c[17 * stride], in19 = c[19 * stride], in21 = c[21 * stride], in23 = c[23 * stride];
        int in25 = c[25 * stride], in27 = c[27 * stride], in29 = c[29 * stride], in31 = c[31 * stride];

        int t32a = (in1 * 101 + 2048) >> 12;   int t33a = (in31 * -2824 + 2048) >> 12;
        int t34a = (in17 * 1660 + 2048) >> 12;  int t35a = (in15 * -1474 + 2048) >> 12;
        int t36a = (in9 * 897 + 2048) >> 12;   int t37a = (in23 * -2191 + 2048) >> 12;
        int t38a = (in25 * 2359 + 2048) >> 12;  int t39a = (in7 * -700 + 2048) >> 12;
        int t40a = (in5 * 501 + 2048) >> 12;   int t41a = (in27 * -2520 + 2048) >> 12;
        int t42a = (in21 * 2019 + 2048) >> 12;  int t43a = (in11 * -1092 + 2048) >> 12;
        int t44a = (in13 * 1285 + 2048) >> 12;  int t45a = (in19 * -1842 + 2048) >> 12;
        int t46a = (in29 * 2675 + 2048) >> 12;  int t47a = (in3 * -301 + 2048) >> 12;
        int t48a = (in3 * 4085 + 2048) >> 12;   int t49a = (in29 * 3102 + 2048) >> 12;
        int t50a = (in19 * 3659 + 2048) >> 12;  int t51a = (in13 * 3889 + 2048) >> 12;
        int t52a = (in11 * 3948 + 2048) >> 12;  int t53a = (in21 * 3564 + 2048) >> 12;
        int t54a = (in27 * 3229 + 2048) >> 12;  int t55a = (in5 * 4065 + 2048) >> 12;
        int t56a = (in7 * 4036 + 2048) >> 12;   int t57a = (in25 * 3349 + 2048) >> 12;
        int t58a = (in23 * 3461 + 2048) >> 12;  int t59a = (in9 * 3996 + 2048) >> 12;
        int t60a = (in15 * 3822 + 2048) >> 12;  int t61a = (in17 * 3745 + 2048) >> 12;
        int t62a = (in31 * 2967 + 2048) >> 12;  int t63a = (in1 * 4095 + 2048) >> 12;

        int t32 = Clip(t32a + t33a, min, max); int t33 = Clip(t32a - t33a, min, max);
        int t34 = Clip(t35a - t34a, min, max); int t35 = Clip(t35a + t34a, min, max);
        int t36 = Clip(t36a + t37a, min, max); int t37 = Clip(t36a - t37a, min, max);
        int t38 = Clip(t39a - t38a, min, max); int t39 = Clip(t39a + t38a, min, max);
        int t40 = Clip(t40a + t41a, min, max); int t41 = Clip(t40a - t41a, min, max);
        int t42 = Clip(t43a - t42a, min, max); int t43 = Clip(t43a + t42a, min, max);
        int t44 = Clip(t44a + t45a, min, max); int t45 = Clip(t44a - t45a, min, max);
        int t46 = Clip(t47a - t46a, min, max); int t47 = Clip(t47a + t46a, min, max);
        int t48 = Clip(t48a + t49a, min, max); int t49 = Clip(t48a - t49a, min, max);
        int t50 = Clip(t51a - t50a, min, max); int t51 = Clip(t51a + t50a, min, max);
        int t52 = Clip(t52a + t53a, min, max); int t53 = Clip(t52a - t53a, min, max);
        int t54 = Clip(t55a - t54a, min, max); int t55 = Clip(t55a + t54a, min, max);
        int t56 = Clip(t56a + t57a, min, max); int t57 = Clip(t56a - t57a, min, max);
        int t58 = Clip(t59a - t58a, min, max); int t59 = Clip(t59a + t58a, min, max);
        int t60 = Clip(t60a + t61a, min, max); int t61 = Clip(t60a - t61a, min, max);
        int t62 = Clip(t63a - t62a, min, max); int t63 = Clip(t63a + t62a, min, max);

        // Stage 2
        t33a = ((t33 * (4096 - 4076) + t62 * 401 + 2048) >> 12) - t33;
        t34a = ((t34 * -401 + t61 * (4096 - 4076) + 2048) >> 12) - t61;
        t37a = (t37 * -1299 + t58 * 1583 + 1024) >> 11;
        t38a = (t38 * -1583 + t57 * -1299 + 1024) >> 11;
        t41a = ((t41 * (4096 - 3612) + t54 * 1931 + 2048) >> 12) - t41;
        t42a = ((t42 * -1931 + t53 * (4096 - 3612) + 2048) >> 12) - t53;
        t45a = ((t45 * -1189 + t50 * (3920 - 4096) + 2048) >> 12) + t50;
        t46a = ((t46 * (4096 - 3920) + t49 * -1189 + 2048) >> 12) - t46;
        t49a = ((t46 * -1189 + t49 * (3920 - 4096) + 2048) >> 12) + t49;
        t50a = ((t45 * (3920 - 4096) + t50 * 1189 + 2048) >> 12) + t45;
        t53a = ((t42 * (4096 - 3612) + t53 * 1931 + 2048) >> 12) - t42;
        t54a = ((t41 * 1931 + t54 * (3612 - 4096) + 2048) >> 12) + t54;
        t57a = (t38 * -1299 + t57 * 1583 + 1024) >> 11;
        t58a = (t37 * 1583 + t58 * 1299 + 1024) >> 11;
        t61a = ((t34 * (4096 - 4076) + t61 * 401 + 2048) >> 12) - t34;
        t62a = ((t33 * 401 + t62 * (4076 - 4096) + 2048) >> 12) + t62;

        t32a = Clip(t32 + t35, min, max);  t33 = Clip(t33a + t34a, min, max);
        t34 = Clip(t33a - t34a, min, max); t35a = Clip(t32 - t35, min, max);
        t36a = Clip(t39 - t36, min, max);  t37 = Clip(t38a - t37a, min, max);
        t38 = Clip(t38a + t37a, min, max); t39a = Clip(t39 + t36, min, max);
        t40a = Clip(t40 + t43, min, max);  t41 = Clip(t41a + t42a, min, max);
        t42 = Clip(t41a - t42a, min, max); t43a = Clip(t40 - t43, min, max);
        t44a = Clip(t47 - t44, min, max);  t45 = Clip(t46a - t45a, min, max);
        t46 = Clip(t46a + t45a, min, max); t47a = Clip(t47 + t44, min, max);
        t48a = Clip(t48 + t51, min, max);  t49 = Clip(t49a + t50a, min, max);
        t50 = Clip(t49a - t50a, min, max); t51a = Clip(t48 - t51, min, max);
        t52a = Clip(t55 - t52, min, max);  t53 = Clip(t54a - t53a, min, max);
        t54 = Clip(t54a + t53a, min, max); t55a = Clip(t55 + t52, min, max);
        t56a = Clip(t56 + t59, min, max);  t57 = Clip(t57a + t58a, min, max);
        t58 = Clip(t57a - t58a, min, max); t59a = Clip(t56 - t59, min, max);
        t60a = Clip(t63 - t60, min, max);  t61 = Clip(t62a - t61a, min, max);
        t62 = Clip(t62a + t61a, min, max); t63a = Clip(t63 + t60, min, max);

        // Stage 3
        t34a = ((t34 * (4096 - 4017) + t61 * 799 + 2048) >> 12) - t34;
        t35 = ((t35a * (4096 - 4017) + t60a * 799 + 2048) >> 12) - t35a;
        t36 = ((t36a * -799 + t59a * (4096 - 4017) + 2048) >> 12) - t59a;
        t37a = ((t37 * -799 + t58 * (4096 - 4017) + 2048) >> 12) - t58;
        t42a = (t42 * -1138 + t53 * 1703 + 1024) >> 11;
        t43 = (t43a * -1138 + t52a * 1703 + 1024) >> 11;
        t44 = (t44a * -1703 + t51a * -1138 + 1024) >> 11;
        t45a = (t45 * -1703 + t50 * -1138 + 1024) >> 11;
        t50a = (t45 * -1138 + t50 * 1703 + 1024) >> 11;
        t51 = (t44a * -1138 + t51a * 1703 + 1024) >> 11;
        t52 = (t43a * 1703 + t52a * 1138 + 1024) >> 11;
        t53a = (t42 * 1703 + t53 * 1138 + 1024) >> 11;
        t58a = ((t37 * (4096 - 4017) + t58 * 799 + 2048) >> 12) - t37;
        t59 = ((t36a * (4096 - 4017) + t59a * 799 + 2048) >> 12) - t36a;
        t60 = ((t35a * 799 + t60a * (4017 - 4096) + 2048) >> 12) + t60a;
        t61a = ((t34 * 799 + t61 * (4017 - 4096) + 2048) >> 12) + t61;

        t32 = Clip(t32a + t39a, min, max);  t33a = Clip(t33 + t38, min, max);
        t34 = Clip(t34a + t37a, min, max);  t35a = Clip(t35 + t36, min, max);
        t36a = Clip(t35 - t36, min, max);   t37 = Clip(t34a - t37a, min, max);
        t38a = Clip(t33 - t38, min, max);   t39 = Clip(t32a - t39a, min, max);
        t40 = Clip(t47a - t40a, min, max);  t41a = Clip(t46 - t41, min, max);
        t42 = Clip(t45a - t42a, min, max);  t43a = Clip(t44 - t43, min, max);
        t44a = Clip(t44 + t43, min, max);   t45 = Clip(t45a + t42a, min, max);
        t46a = Clip(t46 + t41, min, max);   t47 = Clip(t47a + t40a, min, max);
        t48 = Clip(t48a + t55a, min, max);  t49a = Clip(t49 + t54, min, max);
        t50 = Clip(t50a + t53a, min, max);  t51a = Clip(t51 + t52, min, max);
        t52a = Clip(t51 - t52, min, max);   t53 = Clip(t50a - t53a, min, max);
        t54a = Clip(t49 - t54, min, max);   t55 = Clip(t48a - t55a, min, max);
        t56 = Clip(t63a - t56a, min, max);  t57a = Clip(t62 - t57, min, max);
        t58 = Clip(t61a - t58a, min, max);  t59a = Clip(t60 - t59, min, max);
        t60a = Clip(t60 + t59, min, max);   t61 = Clip(t61a + t58a, min, max);
        t62a = Clip(t62 + t57, min, max);   t63 = Clip(t63a + t56a, min, max);

        // Stage 4
        t36 = ((t36a * (4096 - 3784) + t59a * 1567 + 2048) >> 12) - t36a;
        t37a = ((t37 * (4096 - 3784) + t58 * 1567 + 2048) >> 12) - t37;
        t38 = ((t38a * (4096 - 3784) + t57a * 1567 + 2048) >> 12) - t38a;
        t39a = ((t39 * (4096 - 3784) + t56 * 1567 + 2048) >> 12) - t39;
        t40a = ((t40 * -1567 + t55 * (4096 - 3784) + 2048) >> 12) - t55;
        t41 = ((t41a * -1567 + t54a * (4096 - 3784) + 2048) >> 12) - t54a;
        t42a = ((t42 * -1567 + t53 * (4096 - 3784) + 2048) >> 12) - t53;
        t43 = ((t43a * -1567 + t52a * (4096 - 3784) + 2048) >> 12) - t52a;
        t52 = ((t43a * (4096 - 3784) + t52a * 1567 + 2048) >> 12) - t43a;
        t53a = ((t42 * (4096 - 3784) + t53 * 1567 + 2048) >> 12) - t42;
        t54 = ((t41a * (4096 - 3784) + t54a * 1567 + 2048) >> 12) - t41a;
        t55a = ((t40 * (4096 - 3784) + t55 * 1567 + 2048) >> 12) - t40;
        t56a = ((t39 * 1567 + t56 * (3784 - 4096) + 2048) >> 12) + t56;
        t57 = ((t38a * 1567 + t57a * (3784 - 4096) + 2048) >> 12) + t57a;
        t58a = ((t37 * 1567 + t58 * (3784 - 4096) + 2048) >> 12) + t58;
        t59 = ((t36a * 1567 + t59a * (3784 - 4096) + 2048) >> 12) + t59a;

        t32a = Clip(t32 + t47, min, max);   t33 = Clip(t33a + t46a, min, max);
        t34a = Clip(t34 + t45, min, max);   t35 = Clip(t35a + t44a, min, max);
        t36a = Clip(t36 + t43, min, max);   t37 = Clip(t37a + t42a, min, max);
        t38a = Clip(t38 + t41, min, max);   t39 = Clip(t39a + t40a, min, max);
        t40 = Clip(t39a - t40a, min, max);  t41a = Clip(t38 - t41, min, max);
        t42 = Clip(t37a - t42a, min, max);  t43a = Clip(t36 - t43, min, max);
        t44 = Clip(t35a - t44a, min, max);  t45a = Clip(t34 - t45, min, max);
        t46 = Clip(t33a - t46a, min, max);  t47a = Clip(t32 - t47, min, max);
        t48a = Clip(t63 - t48, min, max);   t49 = Clip(t62a - t49a, min, max);
        t50a = Clip(t61 - t50, min, max);   t51 = Clip(t60a - t51a, min, max);
        t52a = Clip(t59 - t52, min, max);   t53 = Clip(t58a - t53a, min, max);
        t54a = Clip(t57 - t54, min, max);   t55 = Clip(t56a - t55a, min, max);
        t56 = Clip(t56a + t55a, min, max);  t57a = Clip(t57 + t54, min, max);
        t58 = Clip(t58a + t53a, min, max);  t59a = Clip(t59 + t52, min, max);
        t60 = Clip(t60a + t51a, min, max);  t61a = Clip(t61 + t50, min, max);
        t62 = Clip(t62a + t49a, min, max);  t63a = Clip(t63 + t48, min, max);

        // Stage 5 — isqrt2 butterfly
        t40a = ((t55 - t40) * 181 + 128) >> 8;
        t41 = ((t54a - t41a) * 181 + 128) >> 8;
        t42a = ((t53 - t42) * 181 + 128) >> 8;
        t43 = ((t52a - t43a) * 181 + 128) >> 8;
        t44a = ((t51 - t44) * 181 + 128) >> 8;
        t45 = ((t50a - t45a) * 181 + 128) >> 8;
        t46a = ((t49 - t46) * 181 + 128) >> 8;
        t47 = ((t48a - t47a) * 181 + 128) >> 8;
        t48 = ((t47a + t48a) * 181 + 128) >> 8;
        t49a = ((t46 + t49) * 181 + 128) >> 8;
        t50 = ((t45a + t50a) * 181 + 128) >> 8;
        t51a = ((t44 + t51) * 181 + 128) >> 8;
        t52 = ((t43a + t52a) * 181 + 128) >> 8;
        t53a = ((t42 + t53) * 181 + 128) >> 8;
        t54 = ((t41a + t54a) * 181 + 128) >> 8;
        t55a = ((t40 + t55) * 181 + 128) >> 8;

        // Final butterfly — read even-indexed outputs from previous pass
        int e0 = c[0 * stride], e1 = c[2 * stride], e2 = c[4 * stride], e3 = c[6 * stride];
        int e4 = c[8 * stride], e5 = c[10 * stride], e6 = c[12 * stride], e7 = c[14 * stride];
        int e8 = c[16 * stride], e9 = c[18 * stride], e10 = c[20 * stride], e11 = c[22 * stride];
        int e12 = c[24 * stride], e13 = c[26 * stride], e14 = c[28 * stride], e15 = c[30 * stride];
        int e16 = c[32 * stride], e17 = c[34 * stride], e18 = c[36 * stride], e19 = c[38 * stride];
        int e20 = c[40 * stride], e21 = c[42 * stride], e22 = c[44 * stride], e23 = c[46 * stride];
        int e24 = c[48 * stride], e25 = c[50 * stride], e26 = c[52 * stride], e27 = c[54 * stride];
        int e28 = c[56 * stride], e29 = c[58 * stride], e30 = c[60 * stride], e31 = c[62 * stride];

        c[0 * stride] = Clip(e0 + t63a, min, max);   c[1 * stride] = Clip(e1 + t62, min, max);
        c[2 * stride] = Clip(e2 + t61a, min, max);   c[3 * stride] = Clip(e3 + t60, min, max);
        c[4 * stride] = Clip(e4 + t59a, min, max);   c[5 * stride] = Clip(e5 + t58, min, max);
        c[6 * stride] = Clip(e6 + t57a, min, max);   c[7 * stride] = Clip(e7 + t56, min, max);
        c[8 * stride] = Clip(e8 + t55a, min, max);   c[9 * stride] = Clip(e9 + t54, min, max);
        c[10 * stride] = Clip(e10 + t53a, min, max); c[11 * stride] = Clip(e11 + t52, min, max);
        c[12 * stride] = Clip(e12 + t51a, min, max); c[13 * stride] = Clip(e13 + t50, min, max);
        c[14 * stride] = Clip(e14 + t49a, min, max); c[15 * stride] = Clip(e15 + t48, min, max);
        c[16 * stride] = Clip(e16 + t47, min, max);  c[17 * stride] = Clip(e17 + t46a, min, max);
        c[18 * stride] = Clip(e18 + t45, min, max);  c[19 * stride] = Clip(e19 + t44a, min, max);
        c[20 * stride] = Clip(e20 + t43, min, max);  c[21 * stride] = Clip(e21 + t42a, min, max);
        c[22 * stride] = Clip(e22 + t41, min, max);  c[23 * stride] = Clip(e23 + t40a, min, max);
        c[24 * stride] = Clip(e24 + t39, min, max);  c[25 * stride] = Clip(e25 + t38a, min, max);
        c[26 * stride] = Clip(e26 + t37, min, max);  c[27 * stride] = Clip(e27 + t36a, min, max);
        c[28 * stride] = Clip(e28 + t35, min, max);  c[29 * stride] = Clip(e29 + t34a, min, max);
        c[30 * stride] = Clip(e30 + t33, min, max);  c[31 * stride] = Clip(e31 + t32a, min, max);
        c[32 * stride] = Clip(e31 - t32a, min, max); c[33 * stride] = Clip(e30 - t33, min, max);
        c[34 * stride] = Clip(e29 - t34a, min, max); c[35 * stride] = Clip(e28 - t35, min, max);
        c[36 * stride] = Clip(e27 - t36a, min, max); c[37 * stride] = Clip(e26 - t37, min, max);
        c[38 * stride] = Clip(e25 - t38a, min, max); c[39 * stride] = Clip(e24 - t39, min, max);
        c[40 * stride] = Clip(e23 - t40a, min, max); c[41 * stride] = Clip(e22 - t41, min, max);
        c[42 * stride] = Clip(e21 - t42a, min, max); c[43 * stride] = Clip(e20 - t43, min, max);
        c[44 * stride] = Clip(e19 - t44a, min, max); c[45 * stride] = Clip(e18 - t45, min, max);
        c[46 * stride] = Clip(e17 - t46a, min, max); c[47 * stride] = Clip(e16 - t47, min, max);
        c[48 * stride] = Clip(e15 - t48, min, max);  c[49 * stride] = Clip(e14 - t49a, min, max);
        c[50 * stride] = Clip(e13 - t50, min, max);  c[51 * stride] = Clip(e12 - t51a, min, max);
        c[52 * stride] = Clip(e11 - t52, min, max);  c[53 * stride] = Clip(e10 - t53a, min, max);
        c[54 * stride] = Clip(e9 - t54, min, max);   c[55 * stride] = Clip(e8 - t55a, min, max);
        c[56 * stride] = Clip(e7 - t56, min, max);   c[57 * stride] = Clip(e6 - t57a, min, max);
        c[58 * stride] = Clip(e5 - t58, min, max);   c[59 * stride] = Clip(e4 - t59a, min, max);
        c[60 * stride] = Clip(e3 - t60, min, max);   c[61 * stride] = Clip(e2 - t61a, min, max);
        c[62 * stride] = Clip(e1 - t62, min, max);   c[63 * stride] = Clip(e0 - t63a, min, max);
    }

    // ========================================================================
    // ADST kernels
    // ========================================================================

    private static void InvAdst4(Span<int> inp, int inStride, int min, int max,
                                  Span<int> output, int outStride)
    {
        int in0 = inp[0 * inStride], in1 = inp[1 * inStride];
        int in2 = inp[2 * inStride], in3 = inp[3 * inStride];

        output[0 * outStride] = ((1321 * in0 + (3803 - 4096) * in2 +
            (2482 - 4096) * in3 + (3344 - 4096) * in1 + 2048) >> 12) + in2 + in3 + in1;
        output[1 * outStride] = (((2482 - 4096) * in0 - 1321 * in2 -
            (3803 - 4096) * in3 + (3344 - 4096) * in1 + 2048) >> 12) + in0 - in3 + in1;
        output[2 * outStride] = (209 * (in0 - in2 + in3) + 128) >> 8;
        output[3 * outStride] = (((3803 - 4096) * in0 + (2482 - 4096) * in2 -
            1321 * in3 - (3344 - 4096) * in1 + 2048) >> 12) + in0 + in2 - in1;
    }

    private static void InvAdst8(Span<int> inp, int inStride, int min, int max,
                                  Span<int> output, int outStride)
    {
        int in0 = inp[0 * inStride], in1 = inp[1 * inStride];
        int in2 = inp[2 * inStride], in3 = inp[3 * inStride];
        int in4 = inp[4 * inStride], in5 = inp[5 * inStride];
        int in6 = inp[6 * inStride], in7 = inp[7 * inStride];

        int t0a = (((4076 - 4096) * in7 + 401 * in0 + 2048) >> 12) + in7;
        int t1a = ((401 * in7 - (4076 - 4096) * in0 + 2048) >> 12) - in0;
        int t2a = (((3612 - 4096) * in5 + 1931 * in2 + 2048) >> 12) + in5;
        int t3a = ((1931 * in5 - (3612 - 4096) * in2 + 2048) >> 12) - in2;
        int t4a = (1299 * in3 + 1583 * in4 + 1024) >> 11;
        int t5a = (1583 * in3 - 1299 * in4 + 1024) >> 11;
        int t6a = ((1189 * in1 + (3920 - 4096) * in6 + 2048) >> 12) + in6;
        int t7a = (((3920 - 4096) * in1 - 1189 * in6 + 2048) >> 12) + in1;

        int t0 = Clip(t0a + t4a, min, max); int t1 = Clip(t1a + t5a, min, max);
        int t2 = Clip(t2a + t6a, min, max); int t3 = Clip(t3a + t7a, min, max);
        int t4 = Clip(t0a - t4a, min, max); int t5 = Clip(t1a - t5a, min, max);
        int t6 = Clip(t2a - t6a, min, max); int t7 = Clip(t3a - t7a, min, max);

        t4a = (((3784 - 4096) * t4 + 1567 * t5 + 2048) >> 12) + t4;
        t5a = ((1567 * t4 - (3784 - 4096) * t5 + 2048) >> 12) - t5;
        t6a = (((3784 - 4096) * t7 - 1567 * t6 + 2048) >> 12) + t7;
        t7a = ((1567 * t7 + (3784 - 4096) * t6 + 2048) >> 12) + t6;

        output[0 * outStride] = Clip(t0 + t2, min, max);
        output[7 * outStride] = -Clip(t1 + t3, min, max);
        t2 = Clip(t0 - t2, min, max);
        t3 = Clip(t1 - t3, min, max);
        output[1 * outStride] = -Clip(t4a + t6a, min, max);
        output[6 * outStride] = Clip(t5a + t7a, min, max);
        t6 = Clip(t4a - t6a, min, max);
        t7 = Clip(t5a - t7a, min, max);

        output[3 * outStride] = -(((t2 + t3) * 181 + 128) >> 8);
        output[4 * outStride] = ((t2 - t3) * 181 + 128) >> 8;
        output[2 * outStride] = ((t6 + t7) * 181 + 128) >> 8;
        output[5 * outStride] = -(((t6 - t7) * 181 + 128) >> 8);
    }

    private static void InvAdst16(Span<int> inp, int inStride, int min, int max,
                                   Span<int> output, int outStride)
    {
        int in0 = inp[0 * inStride], in1 = inp[1 * inStride], in2 = inp[2 * inStride], in3 = inp[3 * inStride];
        int in4 = inp[4 * inStride], in5 = inp[5 * inStride], in6 = inp[6 * inStride], in7 = inp[7 * inStride];
        int in8 = inp[8 * inStride], in9 = inp[9 * inStride], in10 = inp[10 * inStride], in11 = inp[11 * inStride];
        int in12 = inp[12 * inStride], in13 = inp[13 * inStride], in14 = inp[14 * inStride], in15 = inp[15 * inStride];

        int t0 = ((in15 * (4091 - 4096) + in0 * 201 + 2048) >> 12) + in15;
        int t1 = ((in15 * 201 - in0 * (4091 - 4096) + 2048) >> 12) - in0;
        int t2 = ((in13 * (3973 - 4096) + in2 * 995 + 2048) >> 12) + in13;
        int t3 = ((in13 * 995 - in2 * (3973 - 4096) + 2048) >> 12) - in2;
        int t4 = ((in11 * (3703 - 4096) + in4 * 1751 + 2048) >> 12) + in11;
        int t5 = ((in11 * 1751 - in4 * (3703 - 4096) + 2048) >> 12) - in4;
        int t6 = (in9 * 1645 + in6 * 1220 + 1024) >> 11;
        int t7 = (in9 * 1220 - in6 * 1645 + 1024) >> 11;
        int t8 = ((in7 * 2751 + in8 * (3035 - 4096) + 2048) >> 12) + in8;
        int t9 = ((in7 * (3035 - 4096) - in8 * 2751 + 2048) >> 12) + in7;
        int t10 = ((in5 * 2106 + in10 * (3513 - 4096) + 2048) >> 12) + in10;
        int t11 = ((in5 * (3513 - 4096) - in10 * 2106 + 2048) >> 12) + in5;
        int t12 = ((in3 * 1380 + in12 * (3857 - 4096) + 2048) >> 12) + in12;
        int t13 = ((in3 * (3857 - 4096) - in12 * 1380 + 2048) >> 12) + in3;
        int t14 = ((in1 * 601 + in14 * (4052 - 4096) + 2048) >> 12) + in14;
        int t15 = ((in1 * (4052 - 4096) - in14 * 601 + 2048) >> 12) + in1;

        int t0a = Clip(t0 + t8, min, max);   int t1a = Clip(t1 + t9, min, max);
        int t2a = Clip(t2 + t10, min, max);  int t3a = Clip(t3 + t11, min, max);
        int t4a = Clip(t4 + t12, min, max);  int t5a = Clip(t5 + t13, min, max);
        int t6a = Clip(t6 + t14, min, max);  int t7a = Clip(t7 + t15, min, max);
        int t8a = Clip(t0 - t8, min, max);   int t9a = Clip(t1 - t9, min, max);
        int t10a = Clip(t2 - t10, min, max); int t11a = Clip(t3 - t11, min, max);
        int t12a = Clip(t4 - t12, min, max); int t13a = Clip(t5 - t13, min, max);
        int t14a = Clip(t6 - t14, min, max); int t15a = Clip(t7 - t15, min, max);

        t8 = ((t8a * (4017 - 4096) + t9a * 799 + 2048) >> 12) + t8a;
        t9 = ((t8a * 799 - t9a * (4017 - 4096) + 2048) >> 12) - t9a;
        t10 = ((t10a * 2276 + t11a * (3406 - 4096) + 2048) >> 12) + t11a;
        t11 = ((t10a * (3406 - 4096) - t11a * 2276 + 2048) >> 12) + t10a;
        t12 = ((t13a * (4017 - 4096) - t12a * 799 + 2048) >> 12) + t13a;
        t13 = ((t13a * 799 + t12a * (4017 - 4096) + 2048) >> 12) + t12a;
        t14 = ((t15a * 2276 - t14a * (3406 - 4096) + 2048) >> 12) - t14a;
        t15 = ((t15a * (3406 - 4096) + t14a * 2276 + 2048) >> 12) + t15a;

        t0 = Clip(t0a + t4a, min, max);   t1 = Clip(t1a + t5a, min, max);
        t2 = Clip(t2a + t6a, min, max);   t3 = Clip(t3a + t7a, min, max);
        t4 = Clip(t0a - t4a, min, max);   t5 = Clip(t1a - t5a, min, max);
        t6 = Clip(t2a - t6a, min, max);   t7 = Clip(t3a - t7a, min, max);
        t8a = Clip(t8 + t12, min, max);   t9a = Clip(t9 + t13, min, max);
        t10a = Clip(t10 + t14, min, max); t11a = Clip(t11 + t15, min, max);
        t12a = Clip(t8 - t12, min, max);  t13a = Clip(t9 - t13, min, max);
        t14a = Clip(t10 - t14, min, max); t15a = Clip(t11 - t15, min, max);

        t4a = ((t4 * (3784 - 4096) + t5 * 1567 + 2048) >> 12) + t4;
        t5a = ((t4 * 1567 - t5 * (3784 - 4096) + 2048) >> 12) - t5;
        t6a = ((t7 * (3784 - 4096) - t6 * 1567 + 2048) >> 12) + t7;
        t7a = ((t7 * 1567 + t6 * (3784 - 4096) + 2048) >> 12) + t6;
        t12 = ((t12a * (3784 - 4096) + t13a * 1567 + 2048) >> 12) + t12a;
        t13 = ((t12a * 1567 - t13a * (3784 - 4096) + 2048) >> 12) - t13a;
        t14 = ((t15a * (3784 - 4096) - t14a * 1567 + 2048) >> 12) + t15a;
        t15 = ((t15a * 1567 + t14a * (3784 - 4096) + 2048) >> 12) + t14a;

        output[0 * outStride] = Clip(t0 + t2, min, max);
        output[15 * outStride] = -Clip(t1 + t3, min, max);
        t2a = Clip(t0 - t2, min, max);
        t3a = Clip(t1 - t3, min, max);
        output[3 * outStride] = -Clip(t4a + t6a, min, max);
        output[12 * outStride] = Clip(t5a + t7a, min, max);
        t6 = Clip(t4a - t6a, min, max);
        t7 = Clip(t5a - t7a, min, max);
        output[1 * outStride] = -Clip(t8a + t10a, min, max);
        output[14 * outStride] = Clip(t9a + t11a, min, max);
        t10 = Clip(t8a - t10a, min, max);
        t11 = Clip(t9a - t11a, min, max);
        output[2 * outStride] = Clip(t12 + t14, min, max);
        output[13 * outStride] = -Clip(t13 + t15, min, max);
        t14a = Clip(t12 - t14, min, max);
        t15a = Clip(t13 - t15, min, max);

        output[7 * outStride] = -(((t2a + t3a) * 181 + 128) >> 8);
        output[8 * outStride] = ((t2a - t3a) * 181 + 128) >> 8;
        output[4 * outStride] = ((t6 + t7) * 181 + 128) >> 8;
        output[11 * outStride] = -(((t6 - t7) * 181 + 128) >> 8);
        output[6 * outStride] = ((t10 + t11) * 181 + 128) >> 8;
        output[9 * outStride] = -(((t10 - t11) * 181 + 128) >> 8);
        output[5 * outStride] = -(((t14a + t15a) * 181 + 128) >> 8);
        output[10 * outStride] = ((t14a - t15a) * 181 + 128) >> 8;
    }

    // ========================================================================
    // Identity kernels
    // ========================================================================

    private static void InvIdentity4(Span<int> c, int stride)
    {
        for (int i = 0; i < 4; i++)
        {
            int v = c[stride * i];
            c[stride * i] = v + ((v * 1697 + 2048) >> 12);
        }
    }

    private static void InvIdentity8(Span<int> c, int stride)
    {
        for (int i = 0; i < 8; i++)
            c[stride * i] *= 2;
    }

    private static void InvIdentity16(Span<int> c, int stride)
    {
        for (int i = 0; i < 16; i++)
        {
            int v = c[stride * i];
            c[stride * i] = 2 * v + ((v * 1697 + 1024) >> 11);
        }
    }

    private static void InvIdentity32(Span<int> c, int stride)
    {
        for (int i = 0; i < 32; i++)
            c[stride * i] *= 4;
    }

    // ========================================================================
    // WHT (Walsh-Hadamard Transform) kernel
    // ========================================================================

    private static void InvWht4_1d(Span<int> c, int stride)
    {
        int in0 = c[0 * stride], in1 = c[1 * stride];
        int in2 = c[2 * stride], in3 = c[3 * stride];
        int t0 = in0 + in1;
        int t2 = in2 - in3;
        int t4 = (t0 - t2) >> 1;
        int t3 = t4 - in3;
        int t1 = t4 - in1;
        c[0 * stride] = t0 - t3;
        c[1 * stride] = t3;
        c[2 * stride] = t1;
        c[3 * stride] = t2 + t1;
    }

    private static void InvWht4_1dStrided(Span<int> buf, int offset, int stride)
    {
        int in0 = buf[offset + 0 * stride], in1 = buf[offset + 1 * stride];
        int in2 = buf[offset + 2 * stride], in3 = buf[offset + 3 * stride];
        int t0 = in0 + in1;
        int t2 = in2 - in3;
        int t4 = (t0 - t2) >> 1;
        int t3 = t4 - in3;
        int t1 = t4 - in1;
        buf[offset + 0 * stride] = t0 - t3;
        buf[offset + 1 * stride] = t3;
        buf[offset + 2 * stride] = t1;
        buf[offset + 3 * stride] = t2 + t1;
    }
}
