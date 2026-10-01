using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V128 = System.Runtime.Intrinsics.Vector128<short>;
using V256 = System.Runtime.Intrinsics.Vector256<short>;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1's lowbd (8-bit) inverse transform + add as an AVX2 machine runs it: av1_lowbd_inv_txfm2d_add_avx2
// (av1/common/x86/av1_inv_txfm_avx2.c) and the SSSE3 paths it hands the small / 4- and 8-wide sizes to
// (av1_inv_txfm_ssse3.c). Every value lives in 16-bit lanes (saturating adds, mulhrs roundings, packs saturation)
// exactly as there; the eob picks eobx / eoby (the av1_eob_to_eobxy_* tables / eob_fill), which select the zero-tail
// 1D kernels (*_low1 / _low8 / _low16 / _low32) and bound the rows loaded, and the tx type picks the identity /
// h-identity / v-identity / no-identity 2D driver. The 1D kernels are AomInvTxfmLbd.Kernels.cs (generated from the C).
[SkipLocalsInit]
internal static unsafe partial class AomInvTxfmLbd
{
    internal static readonly bool Supported = Avx2.IsSupported && Ssse3.IsSupported;

    private const int NewSqrt2Bits = 12, NewSqrt2 = 5793, NewInvSqrt2 = 2896;
    private const int TX_4X4 = 0, TX_8X8 = 1, TX_4X8 = 5, TX_8X4 = 6, TX_8X16 = 7, TX_16X8 = 8, TX_4X16 = 13, TX_16X4 = 14,
        TX_8X32 = 15, TX_32X8 = 16;
    private const int DCT_DCT = 0, ADST_DCT = 1, DCT_ADST = 2, ADST_ADST = 3, FLIPADST_DCT = 4, DCT_FLIPADST = 5,
        FLIPADST_FLIPADST = 6, ADST_FLIPADST = 7, FLIPADST_ADST = 8, IDTX = 9, V_DCT = 10, H_DCT = 11, V_ADST = 12,
        H_ADST = 13, V_FLIPADST = 14, H_FLIPADST = 15;
    private const int IDCT_1D = 0, IADST_1D = 1, IIDENTITY_1D = 2;

    // Sqrt2, Sqrt2^2, Sqrt2^3, Sqrt2^4, Sqrt2^5
    private static readonly int[] NewSqrt2list = { 5793, 2 * 4096, 2 * 5793, 4 * 4096, 4 * 5793 };
    private static readonly int[] TxW = { 4, 8, 16, 32, 64, 4, 8, 8, 16, 16, 32, 32, 64, 4, 16, 8, 32, 16, 64 };
    private static readonly int[] TxH = { 4, 8, 16, 32, 64, 8, 4, 16, 8, 32, 16, 64, 32, 16, 4, 32, 8, 64, 16 };
    private static readonly int[] TxWLog2 = { 2, 3, 4, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 2, 4, 3, 5, 4, 6 };
    private static readonly int[] TxHLog2 = { 2, 3, 4, 5, 6, 3, 2, 4, 3, 5, 4, 6, 5, 4, 2, 5, 3, 6, 4 };
    // av1_inv_txfm_shift_ls
    private static readonly int[] Shift0 = { 0, -1, -2, -2, -2, 0, 0, -1, -1, -1, -1, -1, -1, -1, -1, -2, -2, -2, -2 };
    private const int Shift1 = -4;  // every size's shift[1]

    private static readonly byte[] VitxTab = { IDCT_1D, IADST_1D, IDCT_1D, IADST_1D, IADST_1D, IDCT_1D, IADST_1D, IADST_1D,
        IADST_1D, IIDENTITY_1D, IDCT_1D, IIDENTITY_1D, IADST_1D, IIDENTITY_1D, IADST_1D, IIDENTITY_1D };
    private static readonly byte[] HitxTab = { IDCT_1D, IDCT_1D, IADST_1D, IADST_1D, IDCT_1D, IADST_1D, IADST_1D, IADST_1D,
        IADST_1D, IIDENTITY_1D, IIDENTITY_1D, IDCT_1D, IIDENTITY_1D, IADST_1D, IIDENTITY_1D, IADST_1D };

    private static readonly short[] EobXY8x8 = { 0x0707, 0x0707, 0x0707, 0x0707, 0x0707, 0x0707, 0x0707, 0x0707 };
    private static readonly short[] EobXY16x16 = { 0x0707, 0x0707, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f,
        0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f };
    private static readonly short[] EobXY32x32 = { 0x0707, 0x0f0f, 0x0f0f, 0x0f0f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f,
        0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f,
        0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f, 0x1f1f };
    private static readonly short[] EobXY8x16 = { 0x0707, 0x0707, 0x0707, 0x0707, 0x0707, 0x0f07, 0x0f07, 0x0f07,
        0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x0f07 };
    private static readonly short[] EobXY16x8 = { 0x0707, 0x0707, 0x070f, 0x070f, 0x070f, 0x070f, 0x070f, 0x070f };
    private static readonly short[] EobXY16x32 = { 0x0707, 0x0707, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f0f,
        0x0f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f,
        0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f, 0x1f0f };
    private static readonly short[] EobXY32x16 = { 0x0707, 0x0f0f, 0x0f0f, 0x0f0f, 0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f,
        0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f, 0x0f1f };
    private static readonly short[] EobXY8x32 = { 0x0707, 0x0707, 0x0707, 0x0707, 0x0707, 0x0f07, 0x0f07, 0x0f07,
        0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x0f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07,
        0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07, 0x1f07 };
    private static readonly short[] EobXY32x8 = { 0x0707, 0x070f, 0x070f, 0x071f, 0x071f, 0x071f, 0x071f, 0x071f };
    // av1_eob_to_eobxy_default
    private static readonly short[]?[] EobToEobXYDefault = { null, EobXY8x8, EobXY16x16, EobXY32x32, EobXY32x32, null,
        null, EobXY8x16, EobXY16x8, EobXY16x32, EobXY32x16, EobXY32x32, EobXY32x32, null, null, EobXY8x32, EobXY32x8,
        EobXY16x32, EobXY32x16 };

    private static readonly int[] ZerosIdx = { 0, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3 };
    // tx_size_wide_log2_eob (64 maps to 32)
    private static readonly int[] TxWideLog2Eob = { 2, 3, 4, 5, 5, 2, 3, 3, 4, 4, 5, 5, 5, 2, 4, 3, 5, 4, 5 };
    private static readonly int[] EobFill = { 0, 7, 7, 7, 7, 7, 7, 7, 15, 15, 15, 15, 15, 15, 15, 15,
        31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31 };

    private static void GetEobxEobyScanDefault(out int eobx, out int eoby, int txSize, int eob)
    {
        if (eob == 1) { eobx = 0; eoby = 0; return; }
        int txWLog2 = TxWideLog2Eob[txSize];
        int eobRow = (eob - 1) >> txWLog2;
        int eobxy = EobToEobXYDefault[txSize]![eobRow];
        eobx = eobxy & 0xFF;
        eoby = eobxy >> 8;
    }

    private static void GetEobxEobyScanHIdentity(out int eobx, out int eoby, int txSize, int eob)
    {
        eob -= 1;
        int eobxMax = Math.Min(32, TxW[txSize]) - 1;
        eobx = eob >= eobxMax ? eobxMax : EobFill[eob];
        int tempEoby = eob / (eobxMax + 1);
        eoby = EobFill[tempEoby];
    }

    private static void GetEobxEobyScanVIdentity(out int eobx, out int eoby, int txSize, int eob)
    {
        eob -= 1;
        int eobyMax = Math.Min(32, TxH[txSize]) - 1;
        eobx = EobFill[eob / (eobyMax + 1)];
        eoby = eob >= eobyMax ? eobyMax : EobFill[eob];
    }

    private static void GetFlipCfg(int txType, out int udFlip, out int lrFlip)
    {
        switch (txType)
        {
            case FLIPADST_DCT: case FLIPADST_ADST: case V_FLIPADST: udFlip = 1; lrFlip = 0; break;
            case DCT_FLIPADST: case ADST_FLIPADST: case H_FLIPADST: udFlip = 0; lrFlip = 1; break;
            case FLIPADST_FLIPADST: udFlip = 1; lrFlip = 1; break;
            default: udFlip = 0; lrFlip = 0; break;
        }
    }

    private static int GetRectTxLogRatio(int col, int row)
    {
        if (col == row) return 0;
        if (col > row) return col == row * 2 ? 1 : col == row * 4 ? 2 : 0;
        return row == col * 2 ? -1 : row == col * 4 ? -2 : 0;
    }

    private static int GetTxwIdx(int txSize) => TxWLog2[txSize] - 2;
    private static int GetTxhIdx(int txSize) => TxHLog2[txSize] - 2;

    // ---- the butterfly macros / inline functions of txfm_common_avx2.h, av1_txfm_sse2.h, av1_inv_txfm_ssse3.h ----

    /// <summary>pair_set_w16_epi16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V256 Pair256(int a, int b) => Vector256.Create((int)((ushort)(short)a | ((uint)(ushort)(short)b << 16))).AsInt16();

    /// <summary>pair_set_epi16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V128 Pair128(int a, int b) => Vector128.Create((int)((ushort)(short)a | ((uint)(ushort)(short)b << 16))).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static V256 SubS(V256 a, V256 b) => Avx2.SubtractSaturate(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static V128 SubS(V128 a, V128 b) => Sse2.SubtractSaturate(a, b);

    /// <summary>btf_16_w16_avx2 (INV_COS_BIT).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Btf16(V256 w0, V256 w1, ref V256 in0, ref V256 in1)
    {
        V256 t0 = Avx2.UnpackLow(in0, in1), t1 = Avx2.UnpackHigh(in0, in1);
        Vector256<int> r = Vector256.Create(1 << 11);
        Vector256<int> c0 = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(t0, w0), r), 12);
        Vector256<int> c1 = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(t1, w0), r), 12);
        Vector256<int> d0 = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(t0, w1), r), 12);
        Vector256<int> d1 = Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(t1, w1), r), 12);
        in0 = Avx2.PackSignedSaturate(c0, c1);
        in1 = Avx2.PackSignedSaturate(d0, d1);
    }

    /// <summary>btf_16_sse2 (INV_COS_BIT), in0 / in1 the outputs too.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Btf16(V128 w0, V128 w1, ref V128 in0, ref V128 in1)
    {
        V128 t0 = Sse2.UnpackLow(in0, in1), t1 = Sse2.UnpackHigh(in0, in1);
        Vector128<int> r = Vector128.Create(1 << 11);
        Vector128<int> c0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, w0), r), 12);
        Vector128<int> c1 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, w0), r), 12);
        Vector128<int> d0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, w1), r), 12);
        Vector128<int> d1 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t1, w1), r), 12);
        in0 = Sse2.PackSignedSaturate(c0, c1);
        in1 = Sse2.PackSignedSaturate(d0, d1);
    }

    /// <summary>btf_16_4p_sse2 (INV_COS_BIT): the low four lanes, duplicated into the high four.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Btf16_4p(V128 w0, V128 w1, ref V128 in0, ref V128 in1)
    {
        V128 t0 = Sse2.UnpackLow(in0, in1);
        Vector128<int> r = Vector128.Create(1 << 11);
        Vector128<int> c0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, w0), r), 12);
        Vector128<int> d0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.MultiplyAddAdjacent(t0, w1), r), 12);
        in0 = Sse2.PackSignedSaturate(c0, c0);
        in1 = Sse2.PackSignedSaturate(d0, d0);
    }

    /// <summary>btf_16_w16_0_avx2: half the input zero, out = mulhrs(in, w * 8).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Btf16Half(int w0, int w1, V256 input, out V256 out0, out V256 out1)
    {
        V256 vw0 = Vector256.Create((short)(w0 * 8)), vw1 = Vector256.Create((short)(w1 * 8));
        out0 = Avx2.MultiplyHighRoundScale(input, vw0);
        out1 = Avx2.MultiplyHighRoundScale(input, vw1);
    }

    /// <summary>btf_16_ssse3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Btf16Half(int w0, int w1, V128 input, out V128 out0, out V128 out1)
    {
        V128 vw0 = Vector128.Create((short)(w0 * 8)), vw1 = Vector128.Create((short)(w1 * 8));
        out0 = Ssse3.MultiplyHighRoundScale(input, vw0);
        out1 = Ssse3.MultiplyHighRoundScale(input, vw1);
    }

    /// <summary>btf_16_adds_subs_avx2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddsSubs(ref V256 in0, ref V256 in1)
    {
        V256 a = in0, b = in1;
        in0 = Avx2.AddSaturate(a, b);
        in1 = Avx2.SubtractSaturate(a, b);
    }

    /// <summary>btf_16_adds_subs_sse2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddsSubs(ref V128 in0, ref V128 in1)
    {
        V128 a = in0, b = in1;
        in0 = Sse2.AddSaturate(a, b);
        in1 = Sse2.SubtractSaturate(a, b);
    }

    /// <summary>btf_16_subs_adds_sse2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubsAdds(ref V128 in0, ref V128 in1)
    {
        V128 a = in0, b = in1;
        in1 = Sse2.SubtractSaturate(a, b);
        in0 = Sse2.AddSaturate(a, b);
    }

    /// <summary>btf_16_adds_subs_out_avx2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddsSubsOut(out V256 out0, out V256 out1, V256 in0, V256 in1)
    {
        out0 = Avx2.AddSaturate(in0, in1);
        out1 = Avx2.SubtractSaturate(in0, in1);
    }

    /// <summary>btf_16_adds_subs_out_sse2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddsSubsOut(out V128 out0, out V128 out1, V128 in0, V128 in1)
    {
        out0 = Sse2.AddSaturate(in0, in1);
        out1 = Sse2.SubtractSaturate(in0, in1);
    }

    // ---- the hand-ported SSSE3 kernels (loops): iadst4, the identities ----

    private static void Iadst4Sse2(V128* input, V128* output)
    {
        const int s1 = 1321, s2 = 2482, s3 = 3344, s4 = 3803;  // sinpi_arr(INV_COS_BIT)
        V128 sinpi_p01_p04 = Pair128(s1, s4), sinpi_p02_m01 = Pair128(s2, -s1), sinpi_p03_p02 = Pair128(s3, s2);
        V128 sinpi_p03_m04 = Pair128(s3, -s4), sinpi_p03_m03 = Pair128(s3, -s3), sinpi_0_p03 = Pair128(0, s3);
        V128 sinpi_p04_p02 = Pair128(s4, s2), sinpi_m03_m01 = Pair128(-s3, -s1);
        V128 x00 = input[0], x01 = input[1], x02 = input[2], x03 = input[3];
        V128 u0 = Sse2.UnpackLow(x00, x02), u1 = Sse2.UnpackHigh(x00, x02);
        V128 u2 = Sse2.UnpackLow(x01, x03), u3 = Sse2.UnpackHigh(x01, x03);
        Vector128<int> x1_0 = Sse2.MultiplyAddAdjacent(u0, sinpi_p01_p04), x1_1 = Sse2.MultiplyAddAdjacent(u1, sinpi_p01_p04);
        Vector128<int> x1_2 = Sse2.MultiplyAddAdjacent(u0, sinpi_p02_m01), x1_3 = Sse2.MultiplyAddAdjacent(u1, sinpi_p02_m01);
        Vector128<int> x1_4 = Sse2.MultiplyAddAdjacent(u2, sinpi_p03_p02), x1_5 = Sse2.MultiplyAddAdjacent(u3, sinpi_p03_p02);
        Vector128<int> x1_6 = Sse2.MultiplyAddAdjacent(u2, sinpi_p03_m04), x1_7 = Sse2.MultiplyAddAdjacent(u3, sinpi_p03_m04);
        Vector128<int> x1_8 = Sse2.MultiplyAddAdjacent(u0, sinpi_p03_m03), x1_9 = Sse2.MultiplyAddAdjacent(u1, sinpi_p03_m03);
        Vector128<int> x1_10 = Sse2.MultiplyAddAdjacent(u2, sinpi_0_p03), x1_11 = Sse2.MultiplyAddAdjacent(u3, sinpi_0_p03);
        Vector128<int> x1_12 = Sse2.MultiplyAddAdjacent(u0, sinpi_p04_p02), x1_13 = Sse2.MultiplyAddAdjacent(u1, sinpi_p04_p02);
        Vector128<int> x1_14 = Sse2.MultiplyAddAdjacent(u2, sinpi_m03_m01), x1_15 = Sse2.MultiplyAddAdjacent(u3, sinpi_m03_m01);
        Unsafe.SkipInit(out StackArr8<Vector128<int>> x2SA); Vector128<int>* x2 = (Vector128<int>*)Unsafe.AsPointer(ref x2SA[0]);
        x2[0] = Sse2.Add(x1_0, x1_4);
        x2[1] = Sse2.Add(x1_1, x1_5);
        x2[2] = Sse2.Add(x1_2, x1_6);
        x2[3] = Sse2.Add(x1_3, x1_7);
        x2[4] = Sse2.Add(x1_8, x1_10);
        x2[5] = Sse2.Add(x1_9, x1_11);
        x2[6] = Sse2.Add(x1_12, x1_14);
        x2[7] = Sse2.Add(x1_13, x1_15);
        Vector128<int> rounding = Vector128.Create(1 << 11);
        for (int i = 0; i < 4; ++i)
        {
            Vector128<int> out0 = Sse2.ShiftRightArithmetic(Sse2.Add(x2[2 * i], rounding), 12);
            Vector128<int> out1 = Sse2.ShiftRightArithmetic(Sse2.Add(x2[2 * i + 1], rounding), 12);
            output[i] = Sse2.PackSignedSaturate(out0, out1);
        }
    }

    private static void Iadst4W4Sse2(V128* input, V128* output)
    {
        const int s1 = 1321, s2 = 2482, s3 = 3344, s4 = 3803;
        V128 sinpi_p01_p04 = Pair128(s1, s4), sinpi_p02_m01 = Pair128(s2, -s1), sinpi_p03_p02 = Pair128(s3, s2);
        V128 sinpi_p03_m04 = Pair128(s3, -s4), sinpi_p03_m03 = Pair128(s3, -s3), sinpi_0_p03 = Pair128(0, s3);
        V128 sinpi_p04_p02 = Pair128(s4, s2), sinpi_m03_m01 = Pair128(-s3, -s1);
        V128 x00 = input[0], x01 = input[1], x02 = input[2], x03 = input[3];
        V128 u0 = Sse2.UnpackLow(x00, x02), u1 = Sse2.UnpackLow(x01, x03);
        Vector128<int> x1_0 = Sse2.MultiplyAddAdjacent(u0, sinpi_p01_p04);
        Vector128<int> x1_1 = Sse2.MultiplyAddAdjacent(u0, sinpi_p02_m01);
        Vector128<int> x1_2 = Sse2.MultiplyAddAdjacent(u1, sinpi_p03_p02);
        Vector128<int> x1_3 = Sse2.MultiplyAddAdjacent(u1, sinpi_p03_m04);
        Vector128<int> x1_4 = Sse2.MultiplyAddAdjacent(u0, sinpi_p03_m03);
        Vector128<int> x1_5 = Sse2.MultiplyAddAdjacent(u1, sinpi_0_p03);
        Vector128<int> x1_6 = Sse2.MultiplyAddAdjacent(u0, sinpi_p04_p02);
        Vector128<int> x1_7 = Sse2.MultiplyAddAdjacent(u1, sinpi_m03_m01);
        Unsafe.SkipInit(out StackArr4<Vector128<int>> x2SA); Vector128<int>* x2 = (Vector128<int>*)Unsafe.AsPointer(ref x2SA[0]);
        x2[0] = Sse2.Add(x1_0, x1_2);
        x2[1] = Sse2.Add(x1_1, x1_3);
        x2[2] = Sse2.Add(x1_4, x1_5);
        x2[3] = Sse2.Add(x1_6, x1_7);
        Vector128<int> rounding = Vector128.Create(1 << 11);
        for (int i = 0; i < 4; ++i)
        {
            Vector128<int> out0 = Sse2.ShiftRightArithmetic(Sse2.Add(x2[i], rounding), 12);
            output[i] = Sse2.PackSignedSaturate(out0, out0);
        }
    }

    private static void Iidentity4Ssse3(V128* input, V128* output)
    {
        short scaleFractional = (short)(NewSqrt2 - (1 << NewSqrt2Bits));
        V128 scale = Vector128.Create((short)(scaleFractional << (15 - NewSqrt2Bits)));
        for (int i = 0; i < 4; ++i)
        {
            V128 x = Ssse3.MultiplyHighRoundScale(input[i], scale);
            output[i] = Sse2.AddSaturate(x, input[i]);
        }
    }

    private static void Iidentity8Sse2(V128* input, V128* output)
    {
        for (int i = 0; i < 8; ++i) output[i] = Sse2.AddSaturate(input[i], input[i]);
    }

    private static void Iidentity16Ssse3(V128* input, V128* output)
    {
        short scaleFractional = (short)(2 * (NewSqrt2 - (1 << NewSqrt2Bits)));
        V128 scale = Vector128.Create((short)(scaleFractional << (15 - NewSqrt2Bits)));
        for (int i = 0; i < 16; ++i)
        {
            V128 x = Ssse3.MultiplyHighRoundScale(input[i], scale);
            V128 srcx2 = Sse2.AddSaturate(input[i], input[i]);
            output[i] = Sse2.AddSaturate(x, srcx2);
        }
    }

    // ---- the 1D function tables ----

    // lowbd_txfm_all_1d_zeros_w16_arr[TX_SIZES][ITX_TYPES_1D][4] (AVX2, 16 columns at a time)
    private static readonly delegate*<V256*, V256*, void>[] ZerosW16Arr =
    {
        null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null,
        &Idct16Low1Avx2, &Idct16Low8Avx2, &Idct16Avx2, null,
        &Iadst16Low1Avx2, &Iadst16Low8Avx2, &Iadst16Avx2, null,
        null, null, null, null,
        &Idct32Low1Avx2, &Idct32Low8Avx2, &Idct32Low16Avx2, &Idct32Avx2,
        null, null, null, null, null, null, null, null,
        &Idct64Low1Avx2, &Idct64Low8Avx2, &Idct64Low16Avx2, &Idct64Low32Avx2,
        null, null, null, null, null, null, null, null,
    };

    // lowbd_txfm_all_1d_w8_arr[TX_SIZES][ITX_TYPES_1D] (8 at a time; only the 4 / 8 / 16 rows are reached: 4x8, 8x4,
    // 4x16, 16x4; the 32 / 64 entries, idct32_sse2 / idct64_low32_ssse3, only by SSSE3-only machines' larger sizes)
    private static readonly delegate*<V128*, V128*, void>[] AllW8Arr =
    {
        &Idct4Sse2, &Iadst4Sse2, &Iidentity4Ssse3,
        &Idct8Sse2, &Iadst8Sse2, &Iidentity8Sse2,
        &Idct16Sse2, &Iadst16Sse2, &Iidentity16Ssse3,
        &Idct32Sse2, null, null,
        null, null, null,
    };

    // lowbd_txfm_all_1d_zeros_w8_arr[TX_SIZES][ITX_TYPES_1D][4] (the 64 row, idct64_*_ssse3, is reached only on
    // SSSE3-only machines: an AVX2 one takes every 64-point size through the AVX2 universe)
    private static readonly delegate*<V128*, V128*, void>[] ZerosW8Arr =
    {
        &Idct4Sse2, &Idct4Sse2, null, null,
        &Iadst4Sse2, &Iadst4Sse2, null, null,
        &Iidentity4Ssse3, &Iidentity4Ssse3, null, null,
        &Idct8Low1Ssse3, &Idct8Sse2, null, null,
        &Iadst8Low1Ssse3, &Iadst8Sse2, null, null,
        &Iidentity8Sse2, &Iidentity8Sse2, null, null,
        &Idct16Low1Ssse3, &Idct16Low8Ssse3, &Idct16Sse2, null,
        &Iadst16Low1Ssse3, &Iadst16Low8Ssse3, &Iadst16Sse2, null,
        null, null, null, null,
        &Idct32Low1Ssse3, &Idct32Low8Ssse3, &Idct32Low16Ssse3, &Idct32Sse2,
        null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null,
    };

    // lowbd_txfm_all_1d_w4_arr[TX_SIZES][ITX_TYPES_1D] (4 at a time: 4x4, 4x8, 4x16, 8x4, 16x4)
    private static readonly delegate*<V128*, V128*, void>[] AllW4Arr =
    {
        &Idct4W4Sse2, &Iadst4W4Sse2, &Iidentity4Ssse3,
        &Idct8W4Sse2, &Iadst8W4Sse2, &Iidentity8Sse2,
        &Idct16W4Sse2, &Iadst16W4Sse2, &Iidentity16Ssse3,
        null, null, null,
        null, null, null,
    };

    // lowbd_txfm_all_1d_zeros_8x8_arr[2][2] (av1_inv_txfm_avx2.c)
    private static readonly delegate*<V128*, V128*, void>[] Zeros8x8Arr =
    {
        &Idct8Low1Ssse3, &Idct8Sse2,
        &Iadst8Low1Ssse3, &Iadst8Sse2,
    };

    // ==== entry points ====

    /// <summary>av1_inv_txfm_add_avx2's lowbd branch: av1_lowbd_inv_txfm2d_add_avx2(dqcoeff, dst, stride, tx_type,
    /// tx_size, eob). The coefficients (libaom's tran_low_t layout for the tx block) are only read.</summary>
    internal static void InvTxfm2dAdd(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        switch (txSize)
        {
            case TX_4X4: case TX_4X8: case TX_8X4: case TX_8X16: case TX_16X8: case TX_4X16: case TX_16X4: case TX_8X32:
            case TX_32X8:
                InvTxfm2dAddSsse3(input, output, stride, txType, txSize, eob);
                break;
            case TX_8X8:
                InvTxfm2dAdd8x8Avx2(input, output, stride, txType, txSize, eob);
                break;
            default:
                InvTxfm2dAddUniverseAvx2(input, output, stride, txType, txSize, eob);
                break;
        }
    }

    // ==== AVX2 (av1_inv_txfm_avx2.c / av1_inv_txfm_avx2.h / txfm_common_avx2.h) ====

    /// <summary>load_32bit_to_16bit_w16_avx2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V256 Load32BitTo16BitW16(int* a)
    {
        Vector256<int> aLow = Avx.LoadDquVector256(a);
        V256 b = Avx2.PackSignedSaturate(aLow, Avx.LoadVector256(a + 8));
        return Avx2.Permute4x64(b.AsInt64(), 0xD8).AsInt16();
    }

    private static void LoadBuffer32BitTo16BitW16(int* input, int stride, V256* output, int outSize)
    {
        for (int i = 0; i < outSize; ++i) output[i] = Load32BitTo16BitW16(input + i * stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose2_8x8Avx2(V256* input, V256* output)
    {
        V256 t0 = Avx2.UnpackLow(input[0], input[1]), t1 = Avx2.UnpackHigh(input[0], input[1]);
        V256 t2 = Avx2.UnpackLow(input[2], input[3]), t3 = Avx2.UnpackHigh(input[2], input[3]);
        V256 t4 = Avx2.UnpackLow(input[4], input[5]), t5 = Avx2.UnpackHigh(input[4], input[5]);
        V256 t6 = Avx2.UnpackLow(input[6], input[7]), t7 = Avx2.UnpackHigh(input[6], input[7]);
        Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt32(), t2.AsInt32()), u2 = Avx2.UnpackHigh(t0.AsInt32(), t2.AsInt32());
        Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt32(), t6.AsInt32()), u6 = Avx2.UnpackHigh(t4.AsInt32(), t6.AsInt32());
        Vector256<int> u1 = Avx2.UnpackLow(t1.AsInt32(), t3.AsInt32()), u3 = Avx2.UnpackHigh(t1.AsInt32(), t3.AsInt32());
        Vector256<int> u5 = Avx2.UnpackLow(t5.AsInt32(), t7.AsInt32()), u7 = Avx2.UnpackHigh(t5.AsInt32(), t7.AsInt32());
        output[0] = Avx2.UnpackLow(u0.AsInt64(), u4.AsInt64()).AsInt16();
        output[1] = Avx2.UnpackHigh(u0.AsInt64(), u4.AsInt64()).AsInt16();
        output[4] = Avx2.UnpackLow(u1.AsInt64(), u5.AsInt64()).AsInt16();
        output[5] = Avx2.UnpackHigh(u1.AsInt64(), u5.AsInt64()).AsInt16();
        output[2] = Avx2.UnpackLow(u2.AsInt64(), u6.AsInt64()).AsInt16();
        output[3] = Avx2.UnpackHigh(u2.AsInt64(), u6.AsInt64()).AsInt16();
        output[6] = Avx2.UnpackLow(u3.AsInt64(), u7.AsInt64()).AsInt16();
        output[7] = Avx2.UnpackHigh(u3.AsInt64(), u7.AsInt64()).AsInt16();
    }

    /// <summary>transpose_16bit_16x16_avx2 (in place allowed: every input is read first).</summary>
    private static void Transpose16bit16x16Avx2(V256* input, V256* output)
    {
        Unsafe.SkipInit(out StackArr16<V256> tSA); V256* t = (V256*)Unsafe.AsPointer(ref tSA[0]);
        for (int idx = 0; idx < 8; idx++)
        {
            // LOADL: the low halves of in[idx], in[idx + 8]; LOADR: their high halves
            t[idx] = Avx2.Permute2x128(input[idx], input[idx + 8], 0x20);
            t[8 + idx] = Avx2.Permute2x128(input[idx], input[idx + 8], 0x31);
        }
        Transpose2_8x8Avx2(t, output);
        Transpose2_8x8Avx2(t + 8, output + 8);
    }

    private static void FlipBufAvx2(V256* input, V256* output, int size)
    {
        for (int i = 0; i < size; ++i) output[size - i - 1] = input[i];
    }

    /// <summary>round_shift_16bit_w16_avx2.</summary>
    private static void RoundShift16bitW16Avx2(V256* input, int size, int bit)
    {
        if (bit < 0)
        {
            bit = -bit;
            V256 round = Vector256.Create((short)(1 << (bit - 1)));
            for (int i = 0; i < size; ++i)
            {
                input[i] = Avx2.AddSaturate(input[i], round);
                input[i] = Avx2.ShiftRightArithmetic(input[i], (byte)bit);
            }
        }
        else if (bit > 0)
        {
            for (int i = 0; i < size; ++i) input[i] = Avx2.ShiftLeftLogical(input[i], (byte)bit);
        }
    }

    /// <summary>round_shift_avx2: the rect (2:1) NewInvSqrt2 scaling.</summary>
    private static void RoundShiftAvx2(V256* input, V256* output, int size)
    {
        V256 scale = Vector256.Create((short)(NewInvSqrt2 * 8));
        for (int i = 0; i < size; ++i) output[i] = Avx2.MultiplyHighRoundScale(input[i], scale);
    }

    /// <summary>write_recon_w16_avx2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteReconW16Avx2(V256 res, byte* output)
    {
        Vector128<byte> pred = Sse2.LoadVector128(output);
        V256 u = Avx2.AddSaturate(Avx2.ConvertToVector256Int16(pred), res);
        Vector128<byte> y = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(u, u).AsInt64(), 168).GetLower().AsByte();
        Sse2.Store(output, y);
    }

    private static void LowbdWriteBuffer16xnAvx2(V256* input, byte* output, int stride, int flipud, int height)
    {
        int j = flipud != 0 ? height - 1 : 0;
        int step = flipud != 0 ? -1 : 1;
        for (int i = 0; i < height; ++i, j += step) WriteReconW16Avx2(input[j], output + i * stride);
    }

    private static void IidentityRow16xnAvx2(V256* output, int* input, int stride, int shift, int height, int txwIdx,
        int rectType)
    {
        int* inputRow = input;
        V256 scale = Vector256.Create((short)NewSqrt2list[txwIdx]);
        V256 r = Vector256.Create((short)((1 << (NewSqrt2Bits - 1)) + (1 << (NewSqrt2Bits - shift - 1))));
        V256 one = Vector256.Create((short)1);
        V256 scaleR = Avx2.UnpackLow(scale, r);
        byte sh = (byte)(NewSqrt2Bits - shift);
        if (rectType != 1 && rectType != -1)
        {
            for (int i = 0; i < height; ++i)
            {
                V256 src = Load32BitTo16BitW16(inputRow);
                inputRow += stride;
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(src, one), scaleR);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(src, one), scaleR);
                output[i] = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(lo, sh), Avx2.ShiftRightArithmetic(hi, sh));
            }
        }
        else
        {
            V256 rectScale = Vector256.Create((short)(NewInvSqrt2 << (15 - NewSqrt2Bits)));
            for (int i = 0; i < height; ++i)
            {
                V256 src = Load32BitTo16BitW16(inputRow);
                src = Avx2.MultiplyHighRoundScale(src, rectScale);
                inputRow += stride;
                Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(src, one), scaleR);
                Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(src, one), scaleR);
                output[i] = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(lo, sh), Avx2.ShiftRightArithmetic(hi, sh));
            }
        }
    }

    private static void IidentityCol16xnAvx2(byte* output, int stride, V256* buf, int shift, int height, int txhIdx)
    {
        V256 scale = Vector256.Create((short)NewSqrt2list[txhIdx]);
        V256 scaleR = Vector256.Create((short)(1 << (NewSqrt2Bits - 1)));
        Vector256<int> shiftR = Vector256.Create(1 << (-shift - 1));
        V256 one = Vector256.Create((short)1);
        V256 scaleCoeff = Avx2.UnpackLow(scale, scaleR);
        for (int h = 0; h < height; ++h)
        {
            Vector256<int> lo = Avx2.MultiplyAddAdjacent(Avx2.UnpackLow(buf[h], one), scaleCoeff);
            Vector256<int> hi = Avx2.MultiplyAddAdjacent(Avx2.UnpackHigh(buf[h], one), scaleCoeff);
            lo = Avx2.ShiftRightArithmetic(lo, NewSqrt2Bits);
            hi = Avx2.ShiftRightArithmetic(hi, NewSqrt2Bits);
            lo = Avx2.Add(lo, shiftR);
            hi = Avx2.Add(hi, shiftR);
            lo = Avx2.ShiftRightArithmetic(lo, (byte)-shift);
            hi = Avx2.ShiftRightArithmetic(hi, (byte)-shift);
            V256 x = Avx2.PackSignedSaturate(lo, hi);
            WriteReconW16Avx2(x, output);
            output += stride;
        }
    }

    private static void InvTxfm2dAddIdtxAvx2(int* input, byte* output, int stride, int txSize)
    {
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int colMax = Math.Min(32, txfmSizeCol), rowMax = Math.Min(32, txfmSizeRow);
        int inputStride = rowMax;
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        Unsafe.SkipInit(out StackArr32<V256> bufSA); V256* buf = (V256*)Unsafe.AsPointer(ref bufSA[0]);
        for (int i = 0; i < colMax >> 4; ++i)
        {
            for (int j = 0; j < rowMax >> 4; j++)
            {
                IidentityRow16xnAvx2(buf, input + j * 16 + i * 16 * inputStride, rowMax, shift0, 16, txwIdx, rectType);
                Transpose16bit16x16Avx2(buf, buf);
                IidentityCol16xnAvx2(output + i * 16 + j * 16 * stride, stride, buf, Shift1, 16, txhIdx);
            }
        }
    }

    private static void InvTxfm2dAddHIdentityAvx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        GetEobxEobyScanHIdentity(out int eobx, out int eoby, txSize, eob);
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int inputStride = Math.Min(32, txfmSizeRow);
        int bufSizeWDiv16 = (eobx + 16) >> 4;
        int bufSizeHDiv16 = (eoby + 16) >> 4;
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdxY = ZerosIdx[eoby];
        var colTxfm = ZerosW16Arr[(txhIdx * 3 + VitxTab[txType]) * 4 + funIdxY];
        GetFlipCfg(txType, out int udFlip, out _);
        Unsafe.SkipInit(out StackArr64<V256> buf0SA); V256* buf0 = (V256*)Unsafe.AsPointer(ref buf0SA[0]);
        for (int i = 0; i < bufSizeWDiv16; i++)
        {
            for (int j = 0; j < bufSizeHDiv16; j++)
            {
                V256* buf0Cur = buf0 + j * 16;
                int* inputCur = input + i * 16 * inputStride + j * 16;
                IidentityRow16xnAvx2(buf0Cur, inputCur, inputStride, shift0, 16, txwIdx, rectType);
                Transpose16bit16x16Avx2(buf0Cur, buf0Cur);
            }
            colTxfm(buf0, buf0);
            V256 mshift = Vector256.Create((short)(1 << (15 + Shift1)));
            int k = udFlip != 0 ? txfmSizeRow - 1 : 0;
            int step = udFlip != 0 ? -1 : 1;
            for (int j = 0; j < txfmSizeRow; ++j, k += step)
            {
                V256 res = Avx2.MultiplyHighRoundScale(buf0[k], mshift);
                WriteReconW16Avx2(res, output + (i << 4) + j * stride);
            }
        }
    }

    private static void InvTxfm2dAddVIdentityAvx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        Unsafe.SkipInit(out StackArr64<V256> buf1SA); V256* buf1 = (V256*)Unsafe.AsPointer(ref buf1SA[0]);
        GetEobxEobyScanVIdentity(out int eobx, out int eoby, txSize, eob);
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv16 = txfmSizeCol >> 4;
        int bufSizeHDiv16 = (eoby + 16) >> 4;
        int bufSizeNonzeroW = ((eobx + 8) >> 3) << 3;
        int inputStride = Math.Min(32, txfmSizeRow);
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdxX = ZerosIdx[eobx];
        var rowTxfm = ZerosW16Arr[(txwIdx * 3 + HitxTab[txType]) * 4 + funIdxX];
        GetFlipCfg(txType, out _, out int lrFlip);
        Unsafe.SkipInit(out StackArr64<V256> buf0SA); V256* buf0 = (V256*)Unsafe.AsPointer(ref buf0SA[0]);
        Unsafe.SkipInit(out StackArr16<V256> tempSA); V256* temp = (V256*)Unsafe.AsPointer(ref tempSA[0]);
        for (int i = 0; i < bufSizeHDiv16; i++)
        {
            LoadBuffer32BitTo16BitW16(input + i * 16, inputStride, buf0, bufSizeNonzeroW);
            if (rectType == 1 || rectType == -1) RoundShiftAvx2(buf0, buf0, bufSizeNonzeroW);  // rect special code
            rowTxfm(buf0, buf0);
            RoundShift16bitW16Avx2(buf0, txfmSizeCol, shift0);
            V256* _buf1 = buf1;
            if (lrFlip != 0)
            {
                for (int j = 0; j < bufSizeWDiv16; ++j)
                {
                    FlipBufAvx2(buf0 + 16 * j, temp, 16);
                    Transpose16bit16x16Avx2(temp, _buf1 + 16 * (bufSizeWDiv16 - 1 - j));
                }
            }
            else
            {
                for (int j = 0; j < bufSizeWDiv16; ++j) Transpose16bit16x16Avx2(buf0 + 16 * j, _buf1 + 16 * j);
            }
            for (int j = 0; j < bufSizeWDiv16; ++j)
                IidentityCol16xnAvx2(output + i * 16 * stride + j * 16, stride, buf1 + j * 16, Shift1, 16, txhIdx);
        }
    }

    /// <summary>lowbd_inv_txfm2d_add_no_identity_avx2: only w >= 16, h >= 16.</summary>
    private static void InvTxfm2dAddNoIdentityAvx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        Unsafe.SkipInit(out StackArr1024<V256> buf1SA); V256* buf1 = (V256*)Unsafe.AsPointer(ref buf1SA[0]);
        GetEobxEobyScanDefault(out int eobx, out int eoby, txSize, eob);
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv16 = txfmSizeCol >> 4;
        int bufSizeNonzeroW = ((eobx + 16) >> 4) << 4;
        int bufSizeNonzeroHDiv16 = (eoby + 16) >> 4;
        int inputStride = Math.Min(32, txfmSizeRow);
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdxX = ZerosIdx[eobx], funIdxY = ZerosIdx[eoby];
        var rowTxfm = ZerosW16Arr[(txwIdx * 3 + HitxTab[txType]) * 4 + funIdxX];
        var colTxfm = ZerosW16Arr[(txhIdx * 3 + VitxTab[txType]) * 4 + funIdxY];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        V256 scale0 = Vector256.Create((short)(1 << (15 + shift0)));
        Unsafe.SkipInit(out StackArr64<V256> buf0SA); V256* buf0 = (V256*)Unsafe.AsPointer(ref buf0SA[0]);
        Unsafe.SkipInit(out StackArr16<V256> tempSA); V256* temp = (V256*)Unsafe.AsPointer(ref tempSA[0]);
        for (int i = 0; i < bufSizeNonzeroHDiv16; i++)
        {
            LoadBuffer32BitTo16BitW16(input + 16 * i, inputStride, buf0, bufSizeNonzeroW);
            if (rectType == 1 || rectType == -1) RoundShiftAvx2(buf0, buf0, bufSizeNonzeroW);  // rect special code
            rowTxfm(buf0, buf0);
            for (int j = 0; j < txfmSizeCol; ++j) buf0[j] = Avx2.MultiplyHighRoundScale(buf0[j], scale0);
            V256* buf1Cur = buf1 + (i << 4);
            if (lrFlip != 0)
            {
                for (int j = 0; j < bufSizeWDiv16; ++j)
                {
                    FlipBufAvx2(buf0 + 16 * j, temp, 16);
                    int offset = txfmSizeRow * (bufSizeWDiv16 - 1 - j);
                    Transpose16bit16x16Avx2(temp, buf1Cur + offset);
                }
            }
            else
            {
                for (int j = 0; j < bufSizeWDiv16; ++j) Transpose16bit16x16Avx2(buf0 + 16 * j, buf1Cur + txfmSizeRow * j);
            }
        }
        V256 scale1 = Vector256.Create((short)(1 << (15 + Shift1)));
        for (int i = 0; i < bufSizeWDiv16; i++)
        {
            V256* buf1Cur = buf1 + i * txfmSizeRow;
            colTxfm(buf1Cur, buf1Cur);
            for (int j = 0; j < txfmSizeRow; ++j) buf1Cur[j] = Avx2.MultiplyHighRoundScale(buf1Cur[j], scale1);
        }
        for (int i = 0; i < bufSizeWDiv16; i++)
            LowbdWriteBuffer16xnAvx2(buf1 + i * txfmSizeRow, output + 16 * i, stride, udFlip, txfmSizeRow);
    }

    /// <summary>load_buffer_avx2: 8 rows of 8 coefficients, packed to 16 bits.</summary>
    private static void LoadBufferAvx2(int* input, int stride, V128* output)
    {
        Vector256<int> a = Avx.LoadVector256(input), b = Avx.LoadVector256(input + stride);
        Vector256<int> c = Avx.LoadVector256(input + stride * 2), d = Avx.LoadVector256(input + stride * 3);
        Vector256<int> e = Avx.LoadVector256(input + stride * 4), f = Avx.LoadVector256(input + stride * 5);
        Vector256<int> g = Avx.LoadVector256(input + stride * 6), h = Avx.LoadVector256(input + stride * 7);
        Vector256<long> ab = Avx2.Permute4x64(Avx2.PackSignedSaturate(a, b).AsInt64(), 0xd8);
        Vector256<long> cd = Avx2.Permute4x64(Avx2.PackSignedSaturate(c, d).AsInt64(), 0xd8);
        Vector256<long> ef = Avx2.Permute4x64(Avx2.PackSignedSaturate(e, f).AsInt64(), 0xd8);
        Vector256<long> gh = Avx2.Permute4x64(Avx2.PackSignedSaturate(g, h).AsInt64(), 0xd8);
        output[0] = ab.GetLower().AsInt16();
        output[1] = ab.GetUpper().AsInt16();
        output[2] = cd.GetLower().AsInt16();
        output[3] = cd.GetUpper().AsInt16();
        output[4] = ef.GetLower().AsInt16();
        output[5] = ef.GetUpper().AsInt16();
        output[6] = gh.GetLower().AsInt16();
        output[7] = gh.GetUpper().AsInt16();
    }

    private static void RoundAndTransposeAvx2(V128* input, V128* output, int bit, int lrFlip)
    {
        V256 scale = Vector256.Create((short)(1 << (15 + bit)));
        int j = lrFlip != 0 ? 7 : 0;
        int step = lrFlip != 0 ? -1 : 1;
        V256 bt0 = Vector256.Create(input[j], input[j + 4 * step]);
        j += step;
        V256 bt1 = Vector256.Create(input[j], input[j + 4 * step]);
        j += step;
        V256 bt2 = Vector256.Create(input[j], input[j + 4 * step]);
        j += step;
        V256 bt3 = Vector256.Create(input[j], input[j + 4 * step]);
        bt0 = Avx2.MultiplyHighRoundScale(bt0, scale);
        bt1 = Avx2.MultiplyHighRoundScale(bt1, scale);
        bt2 = Avx2.MultiplyHighRoundScale(bt2, scale);
        bt3 = Avx2.MultiplyHighRoundScale(bt3, scale);
        Vector256<int> unpcklo0 = Avx2.UnpackLow(bt0, bt1).AsInt32();
        Vector256<int> unpckhi0 = Avx2.UnpackHigh(bt0, bt1).AsInt32();
        Vector256<int> unpcklo1 = Avx2.UnpackLow(bt2, bt3).AsInt32();
        Vector256<int> unpckhi1 = Avx2.UnpackHigh(bt2, bt3).AsInt32();
        Vector256<long> unpcklo00 = Avx2.UnpackLow(unpcklo0, unpcklo1).AsInt64();
        Vector256<long> unpckhi00 = Avx2.UnpackHigh(unpcklo0, unpcklo1).AsInt64();
        Vector256<long> unpcklo01 = Avx2.UnpackLow(unpckhi0, unpckhi1).AsInt64();
        Vector256<long> unpckhi01 = Avx2.UnpackHigh(unpckhi0, unpckhi1).AsInt64();
        Vector256<long> reg00 = Avx2.Permute4x64(unpcklo00, 0xd8);
        Vector256<long> reg01 = Avx2.Permute4x64(unpckhi00, 0xd8);
        Vector256<long> reg10 = Avx2.Permute4x64(unpcklo01, 0xd8);
        Vector256<long> reg11 = Avx2.Permute4x64(unpckhi01, 0xd8);
        output[0] = reg00.GetLower().AsInt16();
        output[1] = reg00.GetUpper().AsInt16();
        output[2] = reg01.GetLower().AsInt16();
        output[3] = reg01.GetUpper().AsInt16();
        output[4] = reg10.GetLower().AsInt16();
        output[5] = reg10.GetUpper().AsInt16();
        output[6] = reg11.GetLower().AsInt16();
        output[7] = reg11.GetUpper().AsInt16();
    }

    private static void RoundShiftLowbdWriteBufferAvx2(V128* input, int bit, byte* output, int stride, int flipud)
    {
        int j = flipud != 0 ? 7 : 0;
        int step = flipud != 0 ? -1 : 1;
        V256 scale = Vector256.Create((short)(1 << (15 + bit)));
        V256 in0 = Vector256.Create(input[j], input[j + step]);
        j += 2 * step;
        V256 in1 = Vector256.Create(input[j], input[j + step]);
        j += 2 * step;
        V256 in2 = Vector256.Create(input[j], input[j + step]);
        j += 2 * step;
        V256 in3 = Vector256.Create(input[j], input[j + step]);
        in0 = Avx2.MultiplyHighRoundScale(in0, scale);
        in1 = Avx2.MultiplyHighRoundScale(in1, scale);
        in2 = Avx2.MultiplyHighRoundScale(in2, scale);
        in3 = Avx2.MultiplyHighRoundScale(in3, scale);
        Vector128<byte> v0 = Sse2.LoadScalarVector128((long*)output).AsByte();
        Vector128<byte> v1 = Sse2.LoadScalarVector128((long*)(output + stride)).AsByte();
        Vector128<byte> v2 = Sse2.LoadScalarVector128((long*)(output + 2 * stride)).AsByte();
        Vector128<byte> v3 = Sse2.LoadScalarVector128((long*)(output + 3 * stride)).AsByte();
        Vector128<byte> v4 = Sse2.LoadScalarVector128((long*)(output + 4 * stride)).AsByte();
        Vector128<byte> v5 = Sse2.LoadScalarVector128((long*)(output + 5 * stride)).AsByte();
        Vector128<byte> v6 = Sse2.LoadScalarVector128((long*)(output + 6 * stride)).AsByte();
        Vector128<byte> v7 = Sse2.LoadScalarVector128((long*)(output + 7 * stride)).AsByte();
        Vector256<byte> zero = Vector256<byte>.Zero;
        V256 unpcklo0 = Avx2.UnpackLow(Vector256.Create(v0, v1), zero).AsInt16();
        V256 unpcklo1 = Avx2.UnpackLow(Vector256.Create(v2, v3), zero).AsInt16();
        V256 unpcklo2 = Avx2.UnpackLow(Vector256.Create(v4, v5), zero).AsInt16();
        V256 unpcklo3 = Avx2.UnpackLow(Vector256.Create(v6, v7), zero).AsInt16();
        V256 x0 = Avx2.AddSaturate(in0, unpcklo0);
        V256 x1 = Avx2.AddSaturate(in1, unpcklo1);
        V256 x2 = Avx2.AddSaturate(in2, unpcklo2);
        V256 x3 = Avx2.AddSaturate(in3, unpcklo3);
        Vector256<byte> res0123 = Avx2.PackUnsignedSaturate(x0, x1);
        Vector256<byte> res4567 = Avx2.PackUnsignedSaturate(x2, x3);
        Vector128<long> res02 = res0123.GetLower().AsInt64(), res13 = res0123.GetUpper().AsInt64();
        Vector128<long> res46 = res4567.GetLower().AsInt64(), res57 = res4567.GetUpper().AsInt64();
        Sse2.StoreScalar((long*)output, res02);
        Sse2.StoreScalar((long*)(output + stride), res13);
        Sse2.StoreScalar((long*)(output + 2 * stride), Sse2.UnpackHigh(res02, res02));
        Sse2.StoreScalar((long*)(output + 3 * stride), Sse2.UnpackHigh(res13, res13));
        Sse2.StoreScalar((long*)(output + 4 * stride), res46);
        Sse2.StoreScalar((long*)(output + 5 * stride), res57);
        Sse2.StoreScalar((long*)(output + 6 * stride), Sse2.UnpackHigh(res46, res46));
        Sse2.StoreScalar((long*)(output + 7 * stride), Sse2.UnpackHigh(res57, res57));
    }

    private static void InvTxfm2d8x8NoIdentityAvx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        Unsafe.SkipInit(out StackArr8<V128> buf1SA); V128* buf1 = (V128*)Unsafe.AsPointer(ref buf1SA[0]);
        const int inputStride = 8;
        int shift0 = Shift0[txSize];
        var rowTxfm = Zeros8x8Arr[HitxTab[txType] * 2 + (eob != 1 ? 1 : 0)];
        var colTxfm = Zeros8x8Arr[VitxTab[txType] * 2 + (eob != 1 ? 1 : 0)];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        Unsafe.SkipInit(out StackArr8<V128> buf0SA); V128* buf0 = (V128*)Unsafe.AsPointer(ref buf0SA[0]);
        LoadBufferAvx2(input, inputStride, buf0);
        rowTxfm(buf0, buf0);
        RoundAndTransposeAvx2(buf0, buf1, shift0, lrFlip);
        colTxfm(buf1, buf1);
        RoundShiftLowbdWriteBufferAvx2(buf1, Shift1, output, stride, udFlip);
    }

    private static void InvTxfm2dAdd8x8Avx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        switch (txType)
        {
            case IDTX:
                InvTxfm2dAddIdtxSsse3(input, output, stride, txSize);
                break;
            case V_DCT: case V_ADST: case V_FLIPADST:
                InvTxfm2dAddHIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
            case H_DCT: case H_ADST: case H_FLIPADST:
                InvTxfm2dAddVIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
            default:
                InvTxfm2d8x8NoIdentityAvx2(input, output, stride, txType, txSize, eob);
                break;
        }
    }

    /// <summary>lowbd_inv_txfm2d_add_universe_avx2: 16x16 and up, 16x32, 32x16, 64x16, 16x64.</summary>
    private static void InvTxfm2dAddUniverseAvx2(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        switch (txType)
        {
            case DCT_DCT: case ADST_DCT: case DCT_ADST: case ADST_ADST: case FLIPADST_DCT: case DCT_FLIPADST:
            case FLIPADST_FLIPADST: case ADST_FLIPADST: case FLIPADST_ADST:
                InvTxfm2dAddNoIdentityAvx2(input, output, stride, txType, txSize, eob);
                break;
            case IDTX:
                InvTxfm2dAddIdtxAvx2(input, output, stride, txSize);
                break;
            case V_DCT: case V_ADST: case V_FLIPADST:
                InvTxfm2dAddHIdentityAvx2(input, output, stride, txType, txSize, eob);
                break;
            case H_DCT: case H_ADST: case H_FLIPADST:
                InvTxfm2dAddVIdentityAvx2(input, output, stride, txType, txSize, eob);
                break;
            default:
                InvTxfm2dAddSsse3(input, output, stride, txType, txSize, eob);
                break;
        }
    }

    // ==== SSSE3 (av1_inv_txfm_ssse3.c / av1_txfm_sse2.h / transpose_sse2.h) ====

    /// <summary>load_32bit_to_16bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V128 Load32BitTo16Bit(int* a) => Sse2.PackSignedSaturate(Sse2.LoadVector128(a), Sse2.LoadVector128(a + 4));

    /// <summary>load_32bit_to_16bit_w4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static V128 Load32BitTo16BitW4(int* a)
    {
        Vector128<int> aLow = Sse2.LoadVector128(a);
        return Sse2.PackSignedSaturate(aLow, aLow);
    }

    private static void LoadBuffer32BitTo16Bit(int* input, int stride, V128* output, int outSize)
    {
        for (int i = 0; i < outSize; ++i) output[i] = Load32BitTo16Bit(input + i * stride);
    }

    private static void LoadBuffer32BitTo16BitW4(int* input, int stride, V128* output, int outSize)
    {
        for (int i = 0; i < outSize; ++i) output[i] = Load32BitTo16BitW4(input + i * stride);
    }

    private static void FlipBufSse2(V128* input, V128* output, int size)
    {
        for (int i = 0; i < size; ++i) output[size - i - 1] = input[i];
    }

    /// <summary>round_shift_16bit_ssse3.</summary>
    private static void RoundShift16bitSsse3(V128* input, int size, int bit)
    {
        if (bit < 0)
        {
            V128 scale = Vector128.Create((short)(1 << (15 + bit)));
            for (int i = 0; i < size; ++i) input[i] = Ssse3.MultiplyHighRoundScale(input[i], scale);
        }
        else if (bit > 0)
        {
            for (int i = 0; i < size; ++i) input[i] = Sse2.ShiftLeftLogical(input[i], (byte)bit);
        }
    }

    /// <summary>round_shift_ssse3: the rect (2:1) NewInvSqrt2 scaling.</summary>
    private static void RoundShiftSsse3(V128* input, V128* output, int size)
    {
        V128 scale = Vector128.Create((short)(NewInvSqrt2 * 8));
        for (int i = 0; i < size; ++i) output[i] = Ssse3.MultiplyHighRoundScale(input[i], scale);
    }

    private static void Transpose16bit4x4(V128* input, V128* output)
    {
        Vector128<int> a0 = Sse2.UnpackLow(input[0], input[1]).AsInt32();
        Vector128<int> a1 = Sse2.UnpackLow(input[2], input[3]).AsInt32();
        V128 o0 = Sse2.UnpackLow(a0, a1).AsInt16();
        V128 o2 = Sse2.UnpackHigh(a0, a1).AsInt16();
        output[0] = o0;
        output[1] = Sse2.ShiftRightLogical128BitLane(o0, 8);
        output[2] = o2;
        output[3] = Sse2.ShiftRightLogical128BitLane(o2, 8);
    }

    private static void Transpose16bit4x8(V128* input, V128* output)
    {
        Vector128<int> a0 = Sse2.UnpackLow(input[0], input[1]).AsInt32();
        Vector128<int> a1 = Sse2.UnpackLow(input[2], input[3]).AsInt32();
        Vector128<int> a2 = Sse2.UnpackLow(input[4], input[5]).AsInt32();
        Vector128<int> a3 = Sse2.UnpackLow(input[6], input[7]).AsInt32();
        Vector128<long> b0 = Sse2.UnpackLow(a0, a1).AsInt64();
        Vector128<long> b1 = Sse2.UnpackLow(a2, a3).AsInt64();
        Vector128<long> b2 = Sse2.UnpackHigh(a0, a1).AsInt64();
        Vector128<long> b3 = Sse2.UnpackHigh(a2, a3).AsInt64();
        output[0] = Sse2.UnpackLow(b0, b1).AsInt16();
        output[1] = Sse2.UnpackHigh(b0, b1).AsInt16();
        output[2] = Sse2.UnpackLow(b2, b3).AsInt16();
        output[3] = Sse2.UnpackHigh(b2, b3).AsInt16();
    }

    private static void Transpose16bit8x4(V128* input, V128* output)
    {
        Vector128<int> a0 = Sse2.UnpackLow(input[0], input[1]).AsInt32();
        Vector128<int> a1 = Sse2.UnpackLow(input[2], input[3]).AsInt32();
        Vector128<int> a4 = Sse2.UnpackHigh(input[0], input[1]).AsInt32();
        Vector128<int> a5 = Sse2.UnpackHigh(input[2], input[3]).AsInt32();
        Vector128<long> b0 = Sse2.UnpackLow(a0, a1).AsInt64();
        Vector128<long> b2 = Sse2.UnpackLow(a4, a5).AsInt64();
        Vector128<long> b4 = Sse2.UnpackHigh(a0, a1).AsInt64();
        Vector128<long> b6 = Sse2.UnpackHigh(a4, a5).AsInt64();
        Vector128<long> zeros = Vector128<long>.Zero;
        output[0] = Sse2.UnpackLow(b0, zeros).AsInt16();
        output[1] = Sse2.UnpackHigh(b0, zeros).AsInt16();
        output[2] = Sse2.UnpackLow(b4, zeros).AsInt16();
        output[3] = Sse2.UnpackHigh(b4, zeros).AsInt16();
        output[4] = Sse2.UnpackLow(b2, zeros).AsInt16();
        output[5] = Sse2.UnpackHigh(b2, zeros).AsInt16();
        output[6] = Sse2.UnpackLow(b6, zeros).AsInt16();
        output[7] = Sse2.UnpackHigh(b6, zeros).AsInt16();
    }

    private static void Transpose16bit8x8(V128* input, V128* output)
    {
        Vector128<int> a0 = Sse2.UnpackLow(input[0], input[1]).AsInt32();
        Vector128<int> a1 = Sse2.UnpackLow(input[2], input[3]).AsInt32();
        Vector128<int> a2 = Sse2.UnpackLow(input[4], input[5]).AsInt32();
        Vector128<int> a3 = Sse2.UnpackLow(input[6], input[7]).AsInt32();
        Vector128<int> a4 = Sse2.UnpackHigh(input[0], input[1]).AsInt32();
        Vector128<int> a5 = Sse2.UnpackHigh(input[2], input[3]).AsInt32();
        Vector128<int> a6 = Sse2.UnpackHigh(input[4], input[5]).AsInt32();
        Vector128<int> a7 = Sse2.UnpackHigh(input[6], input[7]).AsInt32();
        Vector128<long> b0 = Sse2.UnpackLow(a0, a1).AsInt64();
        Vector128<long> b1 = Sse2.UnpackLow(a2, a3).AsInt64();
        Vector128<long> b2 = Sse2.UnpackLow(a4, a5).AsInt64();
        Vector128<long> b3 = Sse2.UnpackLow(a6, a7).AsInt64();
        Vector128<long> b4 = Sse2.UnpackHigh(a0, a1).AsInt64();
        Vector128<long> b5 = Sse2.UnpackHigh(a2, a3).AsInt64();
        Vector128<long> b6 = Sse2.UnpackHigh(a4, a5).AsInt64();
        Vector128<long> b7 = Sse2.UnpackHigh(a6, a7).AsInt64();
        output[0] = Sse2.UnpackLow(b0, b1).AsInt16();
        output[1] = Sse2.UnpackHigh(b0, b1).AsInt16();
        output[2] = Sse2.UnpackLow(b4, b5).AsInt16();
        output[3] = Sse2.UnpackHigh(b4, b5).AsInt16();
        output[4] = Sse2.UnpackLow(b2, b3).AsInt16();
        output[5] = Sse2.UnpackHigh(b2, b3).AsInt16();
        output[6] = Sse2.UnpackLow(b6, b7).AsInt16();
        output[7] = Sse2.UnpackHigh(b6, b7).AsInt16();
    }

    /// <summary>lowbd_get_recon_8x8_sse2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LowbdGetRecon8x8(Vector128<byte> pred, V128 res)
    {
        V128 x0 = Sse2.AddSaturate(res, Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16());
        return Sse2.PackUnsignedSaturate(x0, x0);
    }

    private static void LowbdWriteBuffer4xnSse2(V128* input, byte* output, int stride, int flipud, int height)
    {
        int j = flipud != 0 ? height - 1 : 0;
        int step = flipud != 0 ? -1 : 1;
        for (int i = 0; i < height; ++i, j += step)
        {
            Vector128<byte> v = Vector128.CreateScalar(*(int*)(output + i * stride)).AsByte();
            V128 u = Sse2.AddSaturate(input[j], Sse2.UnpackLow(v, Vector128<byte>.Zero).AsInt16());
            Vector128<byte> p = Sse2.PackUnsignedSaturate(u, V128.Zero);
            *(int*)(output + i * stride) = p.AsInt32().ToScalar();
        }
    }

    private static void LowbdWriteBuffer8xnSse2(V128* input, byte* output, int stride, int flipud, int height)
    {
        int j = flipud != 0 ? height - 1 : 0;
        int step = flipud != 0 ? -1 : 1;
        for (int i = 0; i < height; ++i, j += step)
        {
            Vector128<byte> v = Sse2.LoadScalarVector128((long*)(output + i * stride)).AsByte();
            Vector128<byte> u = LowbdGetRecon8x8(v, input[j]);
            Sse2.StoreScalar((long*)(output + i * stride), u.AsInt64());
        }
    }

    private static void LowbdWriteBuffer16xnSse2(V128* input, byte* output, int stride, int flipud, int height)
    {
        int j = flipud != 0 ? height - 1 : 0;
        int step = flipud != 0 ? -1 : 1;
        for (int i = 0; i < height; ++i, j += step)
        {
            Vector128<byte> v = Sse2.LoadVector128(output + i * stride);
            // lowbd_get_recon_16x16_sse2
            V128 x0 = Sse2.UnpackLow(v, Vector128<byte>.Zero).AsInt16();
            V128 x1 = Sse2.UnpackHigh(v, Vector128<byte>.Zero).AsInt16();
            x0 = Sse2.AddSaturate(input[j], x0);
            x1 = Sse2.AddSaturate(input[j + height], x1);
            Sse2.Store(output + i * stride, Sse2.PackUnsignedSaturate(x0, x1));
        }
    }

    private static void IidentityRow8xnSsse3(V128* output, int* input, int stride, int shift, int height, int txwIdx,
        int rectType)
    {
        int* inputRow = input;
        V128 scale = Vector128.Create((short)NewSqrt2list[txwIdx]);
        V128 rounding = Vector128.Create((short)((1 << (NewSqrt2Bits - 1)) + (1 << (NewSqrt2Bits - shift - 1))));
        V128 one = Vector128.Create((short)1);
        V128 scaleRounding = Sse2.UnpackLow(scale, rounding);
        byte sh = (byte)(NewSqrt2Bits - shift);
        if (rectType != 1 && rectType != -1)
        {
            for (int i = 0; i < height; ++i)
            {
                V128 src = Load32BitTo16Bit(inputRow);
                inputRow += stride;
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(src, one), scaleRounding);
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(src, one), scaleRounding);
                output[i] = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(lo, sh), Sse2.ShiftRightArithmetic(hi, sh));
            }
        }
        else
        {
            V128 rectScale = Vector128.Create((short)(NewInvSqrt2 << (15 - NewSqrt2Bits)));
            for (int i = 0; i < height; ++i)
            {
                V128 src = Load32BitTo16Bit(inputRow);
                src = Ssse3.MultiplyHighRoundScale(src, rectScale);
                inputRow += stride;
                Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(src, one), scaleRounding);
                Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(src, one), scaleRounding);
                output[i] = Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(lo, sh), Sse2.ShiftRightArithmetic(hi, sh));
            }
        }
    }

    private static void IidentityCol8xnSsse3(byte* output, int stride, V128* buf, int shift, int height, int txhIdx)
    {
        V128 scale = Vector128.Create((short)NewSqrt2list[txhIdx]);
        V128 scaleRounding = Vector128.Create((short)(1 << (NewSqrt2Bits - 1)));
        Vector128<int> shiftRounding = Vector128.Create(1 << (-shift - 1));
        V128 one = Vector128.Create((short)1);
        V128 scaleCoeff = Sse2.UnpackLow(scale, scaleRounding);
        for (int h = 0; h < height; ++h)
        {
            Vector128<int> lo = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(buf[h], one), scaleCoeff);
            Vector128<int> hi = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(buf[h], one), scaleCoeff);
            lo = Sse2.ShiftRightArithmetic(lo, NewSqrt2Bits);
            hi = Sse2.ShiftRightArithmetic(hi, NewSqrt2Bits);
            lo = Sse2.Add(lo, shiftRounding);
            hi = Sse2.Add(hi, shiftRounding);
            lo = Sse2.ShiftRightArithmetic(lo, (byte)-shift);
            hi = Sse2.ShiftRightArithmetic(hi, (byte)-shift);
            V128 x = Sse2.PackSignedSaturate(lo, hi);
            Vector128<byte> pred = Sse2.LoadScalarVector128((long*)output).AsByte();
            x = Sse2.AddSaturate(x, Sse2.UnpackLow(pred, Vector128<byte>.Zero).AsInt16());
            Vector128<byte> u = Sse2.PackUnsignedSaturate(x, x);
            Sse2.StoreScalar((long*)output, u.AsInt64());
            output += stride;
        }
    }

    private static void InvTxfm2dAddIdtxSsse3(int* input, byte* output, int stride, int txSize)
    {
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int colMax = Math.Min(32, txfmSizeCol), rowMax = Math.Min(32, txfmSizeRow);
        int inputStride = rowMax;
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        Unsafe.SkipInit(out StackArr8<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        for (int i = 0; i < colMax >> 3; ++i)
        {
            for (int j = 0; j < rowMax >> 3; j++)
            {
                IidentityRow8xnSsse3(buf, input + j * 8 + i * 8 * inputStride, rowMax, shift0, 8, txwIdx, rectType);
                Transpose16bit8x8(buf, buf);
                IidentityCol8xnSsse3(output + i * 8 + j * 8 * stride, stride, buf, Shift1, 8, txhIdx);
            }
        }
    }

    private static void InvTxfm2dAdd4x4Ssse3(int* input, byte* output, int stride, int txType)
    {
        Unsafe.SkipInit(out StackArr4<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        const int txSize = TX_4X4;
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        var rowTxfm = AllW4Arr[txwIdx * 3 + HitxTab[txType]];
        var colTxfm = AllW4Arr[txhIdx * 3 + VitxTab[txType]];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        LoadBuffer32BitTo16BitW4(input, txfmSizeRow, buf, txfmSizeCol);
        rowTxfm(buf, buf);
        if (lrFlip != 0)
        {
            Unsafe.SkipInit(out StackArr4<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
            FlipBufSse2(buf, temp, txfmSizeCol);
            Transpose16bit4x4(temp, buf);
        }
        else
        {
            Transpose16bit4x4(buf, buf);
        }
        colTxfm(buf, buf);
        RoundShift16bitSsse3(buf, txfmSizeRow, Shift1);
        LowbdWriteBuffer4xnSse2(buf, output, stride, udFlip, txfmSizeRow);
    }

    private static void InvTxfm2dAddNoIdentitySsse3(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        Unsafe.SkipInit(out StackArr512<V128> buf1SA); V128* buf1 = (V128*)Unsafe.AsPointer(ref buf1SA[0]);
        GetEobxEobyScanDefault(out int eobx, out int eoby, txSize, eob);
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv8 = txfmSizeCol >> 3;
        int bufSizeNonzeroW = ((eobx + 8) >> 3) << 3;
        int bufSizeNonzeroHDiv8 = (eoby + 8) >> 3;
        int inputStride = Math.Min(32, txfmSizeRow);
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdxX = ZerosIdx[eobx], funIdxY = ZerosIdx[eoby];
        var rowTxfm = ZerosW8Arr[(txwIdx * 3 + HitxTab[txType]) * 4 + funIdxX];
        var colTxfm = ZerosW8Arr[(txhIdx * 3 + VitxTab[txType]) * 4 + funIdxY];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        Unsafe.SkipInit(out StackArr64<V128> buf0SA); V128* buf0 = (V128*)Unsafe.AsPointer(ref buf0SA[0]);
        Unsafe.SkipInit(out StackArr8<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
        for (int i = 0; i < bufSizeNonzeroHDiv8; i++)
        {
            LoadBuffer32BitTo16Bit(input + 8 * i, inputStride, buf0, bufSizeNonzeroW);
            if (rectType == 1 || rectType == -1) RoundShiftSsse3(buf0, buf0, bufSizeNonzeroW);  // rect special code
            rowTxfm(buf0, buf0);
            RoundShift16bitSsse3(buf0, txfmSizeCol, shift0);
            V128* _buf1 = buf1 + i * 8;
            if (lrFlip != 0)
            {
                for (int j = 0; j < bufSizeWDiv8; ++j)
                {
                    FlipBufSse2(buf0 + 8 * j, temp, 8);
                    Transpose16bit8x8(temp, _buf1 + txfmSizeRow * (bufSizeWDiv8 - 1 - j));
                }
            }
            else
            {
                for (int j = 0; j < bufSizeWDiv8; ++j) Transpose16bit8x8(buf0 + 8 * j, _buf1 + txfmSizeRow * j);
            }
        }
        for (int i = 0; i < bufSizeWDiv8; i++)
        {
            colTxfm(buf1 + i * txfmSizeRow, buf1 + i * txfmSizeRow);
            RoundShift16bitSsse3(buf1 + i * txfmSizeRow, txfmSizeRow, Shift1);
        }
        if (txfmSizeCol >= 16)
        {
            for (int i = 0; i < txfmSizeCol >> 4; i++)
                LowbdWriteBuffer16xnSse2(buf1 + i * txfmSizeRow * 2, output + 16 * i, stride, udFlip, txfmSizeRow);
        }
        else if (txfmSizeCol == 8)
        {
            LowbdWriteBuffer8xnSse2(buf1, output, stride, udFlip, txfmSizeRow);
        }
    }

    private static void InvTxfm2dAddHIdentitySsse3(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        int shift0 = Shift0[txSize];
        GetEobxEobyScanHIdentity(out int eobx, out int eoby, txSize, eob);
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv8 = (eobx + 8) >> 3;
        int bufSizeHDiv8 = (eoby + 8) >> 3;
        int inputStride = Math.Min(32, txfmSizeRow);
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdx = ZerosIdx[eoby];
        var colTxfm = ZerosW8Arr[(txhIdx * 3 + VitxTab[txType]) * 4 + funIdx];
        GetFlipCfg(txType, out int udFlip, out _);
        Unsafe.SkipInit(out StackArr64<V128> buf0SA); V128* buf0 = (V128*)Unsafe.AsPointer(ref buf0SA[0]);
        for (int i = 0; i < bufSizeWDiv8; i++)
        {
            for (int j = 0; j < bufSizeHDiv8; j++)
            {
                V128* buf0Cur = buf0 + j * 8;
                int* inputCur = input + i * 8 * inputStride + j * 8;
                IidentityRow8xnSsse3(buf0Cur, inputCur, inputStride, shift0, 8, txwIdx, rectType);
                Transpose16bit8x8(buf0Cur, buf0Cur);
            }
            colTxfm(buf0, buf0);
            V128 mshift = Vector128.Create((short)(1 << (15 + Shift1)));
            int k = udFlip != 0 ? txfmSizeRow - 1 : 0;
            int step = udFlip != 0 ? -1 : 1;
            byte* o = output + 8 * i;
            for (int j = 0; j < txfmSizeRow; ++j, k += step)
            {
                Vector128<byte> v = Sse2.LoadScalarVector128((long*)o).AsByte();
                V128 res = Ssse3.MultiplyHighRoundScale(buf0[k], mshift);
                Vector128<byte> u = LowbdGetRecon8x8(v, res);
                Sse2.StoreScalar((long*)o, u.AsInt64());
                o += stride;
            }
        }
    }

    private static void InvTxfm2dAddVIdentitySsse3(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        Unsafe.SkipInit(out StackArr64<V128> buf1SA); V128* buf1 = (V128*)Unsafe.AsPointer(ref buf1SA[0]);
        GetEobxEobyScanVIdentity(out int eobx, out int eoby, txSize, eob);
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv8 = txfmSizeCol >> 3;
        int bufSizeNonzeroW = ((eobx + 8) >> 3) << 3;
        int bufSizeHDiv8 = (eoby + 8) >> 3;
        int inputStride = Math.Min(32, txfmSizeRow);
        int rectType = GetRectTxLogRatio(txfmSizeCol, txfmSizeRow);
        int funIdx = ZerosIdx[eobx];
        var rowTxfm = ZerosW8Arr[(txwIdx * 3 + HitxTab[txType]) * 4 + funIdx];
        GetFlipCfg(txType, out _, out int lrFlip);
        Unsafe.SkipInit(out StackArr64<V128> buf0SA); V128* buf0 = (V128*)Unsafe.AsPointer(ref buf0SA[0]);
        Unsafe.SkipInit(out StackArr8<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
        for (int i = 0; i < bufSizeHDiv8; i++)
        {
            LoadBuffer32BitTo16Bit(input + i * 8, inputStride, buf0, bufSizeNonzeroW);
            if (rectType == 1 || rectType == -1) RoundShiftSsse3(buf0, buf0, bufSizeNonzeroW);  // rect special code
            rowTxfm(buf0, buf0);
            RoundShift16bitSsse3(buf0, txfmSizeCol, shift0);
            V128* _buf1 = buf1;
            if (lrFlip != 0)
            {
                for (int j = 0; j < bufSizeWDiv8; ++j)
                {
                    FlipBufSse2(buf0 + 8 * j, temp, 8);
                    Transpose16bit8x8(temp, _buf1 + 8 * (bufSizeWDiv8 - 1 - j));
                }
            }
            else
            {
                for (int j = 0; j < bufSizeWDiv8; ++j) Transpose16bit8x8(buf0 + 8 * j, _buf1 + 8 * j);
            }
            for (int j = 0; j < bufSizeWDiv8; ++j)
                IidentityCol8xnSsse3(output + i * 8 * stride + j * 8, stride, buf1 + j * 8, Shift1, 8, txhIdx);
        }
    }

    /// <summary>lowbd_inv_txfm2d_add_universe_ssse3 (8x16, 16x8, 8x32, 32x8 on an AVX2 machine).</summary>
    private static void InvTxfm2dAddUniverseSsse3(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        switch (txType)
        {
            case DCT_DCT:
                InvTxfm2dAddNoIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
            case IDTX:
                InvTxfm2dAddIdtxSsse3(input, output, stride, txSize);
                break;
            case V_DCT: case V_ADST: case V_FLIPADST:
                InvTxfm2dAddHIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
            case H_DCT: case H_ADST: case H_FLIPADST:
                InvTxfm2dAddVIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
            default:
                InvTxfm2dAddNoIdentitySsse3(input, output, stride, txType, txSize, eob);
                break;
        }
    }

    private static void InvTxfm2dAdd4x8Ssse3(int* input, byte* output, int stride, int txType)
    {
        Unsafe.SkipInit(out StackArr8<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        const int txSize = TX_4X8;
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        var rowTxfm = AllW8Arr[txwIdx * 3 + HitxTab[txType]];
        var colTxfm = AllW4Arr[txhIdx * 3 + VitxTab[txType]];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        LoadBuffer32BitTo16Bit(input, txfmSizeRow, buf, txfmSizeCol);
        RoundShiftSsse3(buf, buf, txfmSizeCol);  // rect special code
        rowTxfm(buf, buf);
        // round_shift_16bit_ssse3(buf, txfm_size_col, shift[0]); shift[0] is 0
        if (lrFlip != 0)
        {
            Unsafe.SkipInit(out StackArr4<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
            FlipBufSse2(buf, temp, txfmSizeCol);
            Transpose16bit8x4(temp, buf);
        }
        else
        {
            Transpose16bit8x4(buf, buf);
        }
        colTxfm(buf, buf);
        RoundShift16bitSsse3(buf, txfmSizeRow, Shift1);
        LowbdWriteBuffer4xnSse2(buf, output, stride, udFlip, txfmSizeRow);
    }

    private static void InvTxfm2dAdd8x4Ssse3(int* input, byte* output, int stride, int txType)
    {
        Unsafe.SkipInit(out StackArr8<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        const int txSize = TX_8X4;
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        var rowTxfm = AllW4Arr[txwIdx * 3 + HitxTab[txType]];
        var colTxfm = AllW8Arr[txhIdx * 3 + VitxTab[txType]];
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        LoadBuffer32BitTo16BitW4(input, txfmSizeRow, buf, txfmSizeCol);
        RoundShiftSsse3(buf, buf, txfmSizeCol);  // rect special code
        rowTxfm(buf, buf);
        // round_shift_16bit_ssse3(buf, txfm_size_col, shift[0]); shift[0] is 0
        if (lrFlip != 0)
        {
            Unsafe.SkipInit(out StackArr8<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
            FlipBufSse2(buf, temp, txfmSizeCol);
            Transpose16bit4x8(temp, buf);
        }
        else
        {
            Transpose16bit4x8(buf, buf);
        }
        colTxfm(buf, buf);
        RoundShift16bitSsse3(buf, txfmSizeRow, Shift1);
        LowbdWriteBuffer8xnSse2(buf, output, stride, udFlip, txfmSizeRow);
    }

    private static void InvTxfm2dAdd4x16Ssse3(int* input, byte* output, int stride, int txType)
    {
        Unsafe.SkipInit(out StackArr16<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        const int txSize = TX_4X16;
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        var rowTxfm = AllW8Arr[txwIdx * 3 + HitxTab[txType]];
        var colTxfm = AllW4Arr[txhIdx * 3 + VitxTab[txType]];
        // row_txfm == iidentity4_ssse3: the only identity in lowbd_txfm_all_1d_w8_arr[txw_idx = 0]
        bool rowIsIdentity = HitxTab[txType] == IIDENTITY_1D;
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        const int rowOneLoop = 8;
        Unsafe.SkipInit(out StackArr8<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
        for (int i = 0; i < 2; ++i)
        {
            int* inputCur = input + i * rowOneLoop;
            V128* bufCur = buf + i * rowOneLoop;
            LoadBuffer32BitTo16Bit(inputCur, txfmSizeRow, bufCur, txfmSizeCol);
            if (rowIsIdentity)
            {
                V128 scale = Pair128(NewSqrt2, 3 << (NewSqrt2Bits - 1));
                V128 ones = Vector128.Create((short)1);
                for (int j = 0; j < 4; ++j)
                {
                    V128 bufLo = Sse2.UnpackLow(bufCur[j], ones);
                    V128 bufHi = Sse2.UnpackHigh(bufCur[j], ones);
                    Vector128<int> buf32Lo = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(bufLo, scale), NewSqrt2Bits + 1);
                    Vector128<int> buf32Hi = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(bufHi, scale), NewSqrt2Bits + 1);
                    bufCur[j] = Sse2.PackSignedSaturate(buf32Lo, buf32Hi);
                }
            }
            else
            {
                rowTxfm(bufCur, bufCur);
                RoundShift16bitSsse3(bufCur, rowOneLoop, shift0);
            }
            if (lrFlip != 0)
            {
                FlipBufSse2(bufCur, temp, txfmSizeCol);
                Transpose16bit8x4(temp, bufCur);
            }
            else
            {
                Transpose16bit8x4(bufCur, bufCur);
            }
        }
        colTxfm(buf, buf);
        RoundShift16bitSsse3(buf, txfmSizeRow, Shift1);
        LowbdWriteBuffer4xnSse2(buf, output, stride, udFlip, txfmSizeRow);
    }

    private static void InvTxfm2dAdd16x4Ssse3(int* input, byte* output, int stride, int txType)
    {
        Unsafe.SkipInit(out StackArr16<V128> bufSA); V128* buf = (V128*)Unsafe.AsPointer(ref bufSA[0]);
        const int txSize = TX_16X4;
        int shift0 = Shift0[txSize];
        int txwIdx = GetTxwIdx(txSize), txhIdx = GetTxhIdx(txSize);
        int txfmSizeCol = TxW[txSize], txfmSizeRow = TxH[txSize];
        int bufSizeWDiv8 = txfmSizeCol >> 3;
        var rowTxfm = AllW4Arr[txwIdx * 3 + HitxTab[txType]];
        var colTxfm = AllW8Arr[txhIdx * 3 + VitxTab[txType]];
        // row_txfm == iidentity16_ssse3: the only identity in lowbd_txfm_all_1d_w4_arr[txw_idx = 2]
        bool rowIsIdentity = HitxTab[txType] == IIDENTITY_1D;
        GetFlipCfg(txType, out int udFlip, out int lrFlip);
        const int rowOneLoop = 8;
        LoadBuffer32BitTo16BitW4(input, txfmSizeRow, buf, txfmSizeCol);
        if (rowIsIdentity)
        {
            V128 scale = Pair128(2 * NewSqrt2, 3 << (NewSqrt2Bits - 1));
            V128 ones = Vector128.Create((short)1);
            for (int j = 0; j < 16; ++j)
            {
                V128 bufLo = Sse2.UnpackLow(buf[j], ones);
                V128 bufHi = Sse2.UnpackHigh(buf[j], ones);
                Vector128<int> buf32Lo = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(bufLo, scale), NewSqrt2Bits + 1);
                Vector128<int> buf32Hi = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(bufHi, scale), NewSqrt2Bits + 1);
                buf[j] = Sse2.PackSignedSaturate(buf32Lo, buf32Hi);
            }
        }
        else
        {
            rowTxfm(buf, buf);
            RoundShift16bitSsse3(buf, txfmSizeCol, shift0);
        }
        if (lrFlip != 0)
        {
            Unsafe.SkipInit(out StackArr16<V128> tempSA); V128* temp = (V128*)Unsafe.AsPointer(ref tempSA[0]);
            FlipBufSse2(buf, temp, 16);
            Transpose16bit4x8(temp, buf);
            Transpose16bit4x8(temp + 8, buf + 8);
        }
        else
        {
            Transpose16bit4x8(buf, buf);
            Transpose16bit4x8(buf + rowOneLoop, buf + rowOneLoop);
        }
        for (int i = 0; i < bufSizeWDiv8; i++)
        {
            colTxfm(buf + i * rowOneLoop, buf + i * rowOneLoop);
            RoundShift16bitSsse3(buf + i * rowOneLoop, txfmSizeRow, Shift1);
        }
        LowbdWriteBuffer8xnSse2(buf, output, stride, udFlip, 4);
        LowbdWriteBuffer8xnSse2(buf + 8, output + 8, stride, udFlip, 4);
    }

    /// <summary>av1_lowbd_inv_txfm2d_add_ssse3.</summary>
    private static void InvTxfm2dAddSsse3(int* input, byte* output, int stride, int txType, int txSize, int eob)
    {
        switch (txSize)
        {
            case TX_4X4: InvTxfm2dAdd4x4Ssse3(input, output, stride, txType); break;
            case TX_4X8: InvTxfm2dAdd4x8Ssse3(input, output, stride, txType); break;
            case TX_8X4: InvTxfm2dAdd8x4Ssse3(input, output, stride, txType); break;
            case TX_4X16: InvTxfm2dAdd4x16Ssse3(input, output, stride, txType); break;
            case TX_16X4: InvTxfm2dAdd16x4Ssse3(input, output, stride, txType); break;
            default: InvTxfm2dAddUniverseSsse3(input, output, stride, txType, txSize, eob); break;
        }
    }
}
