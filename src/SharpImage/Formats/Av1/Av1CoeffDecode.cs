using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 coefficient decode and dequantization.
/// Ported from dav1d recon_tmpl.c: decode_coefs, read_coef_tree, and helpers.
/// </summary>
public static class Av1CoeffDecode
{
    public static int DbgCoefDecCount;
    public static int DbgTxtpCount;
    public static int DbgBx, DbgBy;
    public static int DbgReconCount;
    public static int DbgFirstBlock = 0;
    public static bool DbgFirstBlockDone = false;
    // ========================================================================
    // read_golomb — Exp-Golomb coded value from MSAC
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadGolomb(ref Av1Msac msac)
    {
        int len = 0;
        uint val = 1;

        while (msac.DecodeBoolEqui() == 0 && len < 32) len++;
        while (len-- > 0) val = (val << 1) + msac.DecodeBoolEqui();

        return val - 1;
    }

    // ========================================================================
    // get_skip_ctx — Coefficient skip context
    // ========================================================================

    /// <summary>
    /// Determines the skip context for coefficient coding.
    /// Ported from dav1d recon_tmpl.c get_skip_ctx().
    /// </summary>
    public static int GetSkipCtx(
        ref readonly Av1TxfmInfo tDim,
        int bs,
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> l,
        int chroma,
        int layout)
    {
        int bw4 = Av1Tables.BlockDimensions[bs, 0];
        int bh4 = Av1Tables.BlockDimensions[bs, 1];

        if (chroma != 0)
        {
            // dav1d uses log2 values for notOneBlk: b_dim[2], b_dim[3], t_dim->lw, t_dim->lh
            int bw4Log2 = Av1Tables.BlockDimensions[bs, 2];
            int bh4Log2 = Av1Tables.BlockDimensions[bs, 3];
            int ssVer = layout == 1 ? 1 : 0; // I420
            int ssHor = layout != 3 ? 1 : 0; // not I444
            int notOneBlk = (bw4Log2 - ((bw4Log2 != 0 ? 1 : 0) & ssHor) > tDim.Lw ||
                              bh4Log2 - ((bh4Log2 != 0 ? 1 : 0) & ssVer) > tDim.Lh) ? 1 : 0;

            // Merge above context
            int ca = MergeCtxChroma(a, tDim.Lw);
            // Merge left context
            int cl = MergeCtxChroma(l, tDim.Lh);

            return 7 + notOneBlk * 3 + ca + cl;
        }

        if (bw4 == tDim.W && bh4 == tDim.H)
            return 0;

        // Merge luma above context (returns packed value matching dav1d's MERGE_CTX)
        uint la = MergeCtxLuma(a, tDim.Lw);
        // Merge luma left context (returns packed value matching dav1d's MERGE_CTX)
        uint ll = MergeCtxLuma(l, tDim.Lh);

        return Av1Tables.SkipCtx[Math.Min((int)(la & 0x3F), 4), Math.Min((int)(ll & 0x3F), 4)];
    }

    /// <summary>Merge chroma coefficient context bytes. Returns 0 if all 0x40, else 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MergeCtxChroma(ReadOnlySpan<byte> ctx, int logSize)
    {
        switch (logSize)
        {
            case 0: // TX_4X4
                return ctx[0] != 0x40 ? 1 : 0;
            case 1: // TX_8X8
            {
                ushort v = MemoryMarshal.Read<ushort>(ctx);
                return v != 0x4040 ? 1 : 0;
            }
            case 2: // TX_16X16
            {
                uint v = MemoryMarshal.Read<uint>(ctx);
                return v != 0x40404040u ? 1 : 0;
            }
            case 3: // TX_32X32
            {
                ulong v = MemoryMarshal.Read<ulong>(ctx);
                return v != 0x4040404040404040uL ? 1 : 0;
            }
            default:
                return 0;
        }
    }

    /// <summary>Merge luma coefficient context bytes using dav1d's MERGE_CTX algorithm.
    /// Returns packed value; caller applies &amp; 0x3F and clamps to 0-4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MergeCtxLuma(ReadOnlySpan<byte> ctx, int logSize)
    {
        uint v;
        switch (logSize)
        {
            case 0: // TX_4X4 — 1 byte
                v = ctx[0];
                break;
            case 1: // TX_8X8 — 2 bytes, OR upper into lower
                v = MemoryMarshal.Read<ushort>(ctx);
                v |= v >> 8;
                break;
            case 2: // TX_16X16 — 4 bytes, OR upper word into lower word, then OR upper byte into lower
                v = MemoryMarshal.Read<uint>(ctx);
                v |= v >> 16;
                v |= v >> 8;
                break;
            case 3: // TX_32X32 — 8 bytes
                ulong tmp = MemoryMarshal.Read<ulong>(ctx);
                tmp |= MemoryMarshal.Read<ulong>(ctx.Slice(4));
                v = (uint)(tmp >> 32) | (uint)tmp;
                v |= v >> 8;
                break;
            default:
                v = 0;
                break;
        }
        return v;
    }

    // ========================================================================
    // get_dc_sign_ctx — DC coefficient sign context
    // ========================================================================

    /// <summary>
    /// Computes DC sign context from above and left coefficient context.
    /// Ported from dav1d recon_tmpl.c get_dc_sign_ctx().
    /// Returns 0 (negative), 1 (zero), or 2 (positive).
    /// </summary>
    public static int GetDcSignCtx(int tx, ReadOnlySpan<byte> a, ReadOnlySpan<byte> l)
    {
        const uint mask32 = 0xC0C0C0C0u;
        const ulong mask64 = 0xC0C0C0C0C0C0C0C0uL;
        const uint mul32 = 0x01010101u;
        const ulong mul64 = 0x0101010101010101uL;
        int s;

        switch (tx)
        {
            case 0: // TX_4X4
            {
                int t = a[0] >> 6;
                t += l[0] >> 6;
                s = t - 1 - 1;
                break;
            }
            case 1: // TX_8X8
            {
                uint t = MemoryMarshal.Read<ushort>(a) & mask32;
                t += MemoryMarshal.Read<ushort>(l) & mask32;
                t *= 0x04040404u;
                s = (int)(t >> 24) - 2 - 2;
                break;
            }
            case 2: // TX_16X16
            {
                uint t = (MemoryMarshal.Read<uint>(a) & mask32) >> 6;
                t += (MemoryMarshal.Read<uint>(l) & mask32) >> 6;
                t *= mul32;
                s = (int)(t >> 24) - 4 - 4;
                break;
            }
            case 3: // TX_32X32
            {
                ulong t = (MemoryMarshal.Read<ulong>(a) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l) & mask64) >> 6;
                t *= mul64;
                s = (int)(t >> 56) - 8 - 8;
                break;
            }
            case 4: // TX_64X64
            {
                ulong t = (MemoryMarshal.Read<ulong>(a) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(a.Slice(8)) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l.Slice(8)) & mask64) >> 6;
                t *= mul64;
                s = (int)(t >> 56) - 16 - 16;
                break;
            }
            default:
                // Rectangular transform sizes
                s = GetDcSignCtxRect(tx, a, l);
                break;
        }

        return (s != 0 ? 1 : 0) + (s > 0 ? 1 : 0);
    }

    /// <summary>DC sign context for rectangular transforms (RTX_4X8 through RTX_64X16).</summary>
    private static int GetDcSignCtxRect(int tx, ReadOnlySpan<byte> a, ReadOnlySpan<byte> l)
    {
        const uint mask32 = 0xC0C0C0C0u;
        const ulong mask64 = 0xC0C0C0C0C0C0C0C0uL;
        const uint mul32 = 0x01010101u;
        const ulong mul64 = 0x0101010101010101uL;

        switch (tx)
        {
            case 5: // RTX_4X8
            {
                uint t = a[0] & mask32;
                t += MemoryMarshal.Read<ushort>(l) & mask32;
                t *= 0x04040404u;
                return (int)(t >> 24) - 1 - 2;
            }
            case 6: // RTX_8X4
            {
                uint t = MemoryMarshal.Read<ushort>(a) & mask32;
                t += l[0] & mask32;
                t *= 0x04040404u;
                return (int)(t >> 24) - 2 - 1;
            }
            case 7: // RTX_8X16
            {
                uint t = MemoryMarshal.Read<ushort>(a) & mask32;
                t += MemoryMarshal.Read<uint>(l) & mask32;
                t = (t >> 6) * mul32;
                return (int)(t >> 24) - 2 - 4;
            }
            case 8: // RTX_16X8
            {
                uint t = MemoryMarshal.Read<uint>(a) & mask32;
                t += MemoryMarshal.Read<ushort>(l) & mask32;
                t = (t >> 6) * mul32;
                return (int)(t >> 24) - 4 - 2;
            }
            case 9: // RTX_16X32
            {
                ulong t = MemoryMarshal.Read<uint>(a) & mask32;
                t += MemoryMarshal.Read<ulong>(l) & mask64;
                t = (t >> 6) * mul64;
                return (int)(t >> 56) - 4 - 8;
            }
            case 10: // RTX_32X16
            {
                ulong t = MemoryMarshal.Read<ulong>(a) & mask64;
                t += MemoryMarshal.Read<uint>(l) & mask32;
                t = (t >> 6) * mul64;
                return (int)(t >> 56) - 8 - 4;
            }
            case 11: // RTX_32X64
            {
                ulong t = (MemoryMarshal.Read<ulong>(a) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l.Slice(8)) & mask64) >> 6;
                t *= mul64;
                return (int)(t >> 56) - 8 - 16;
            }
            case 12: // RTX_64X32
            {
                ulong t = (MemoryMarshal.Read<ulong>(a) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(a.Slice(8)) & mask64) >> 6;
                t += (MemoryMarshal.Read<ulong>(l) & mask64) >> 6;
                t *= mul64;
                return (int)(t >> 56) - 16 - 8;
            }
            case 13: // RTX_4X16
            {
                uint t = a[0] & mask32;
                t += MemoryMarshal.Read<uint>(l) & mask32;
                t = (t >> 6) * mul32;
                return (int)(t >> 24) - 1 - 4;
            }
            case 14: // RTX_16X4
            {
                uint t = MemoryMarshal.Read<uint>(a) & mask32;
                t += l[0] & mask32;
                t = (t >> 6) * mul32;
                return (int)(t >> 24) - 4 - 1;
            }
            case 15: // RTX_8X32
            {
                ulong t = MemoryMarshal.Read<ushort>(a) & mask32;
                t += MemoryMarshal.Read<ulong>(l) & mask64;
                t = (t >> 6) * mul64;
                return (int)(t >> 56) - 2 - 8;
            }
            case 16: // RTX_32X8
            {
                ulong t = MemoryMarshal.Read<ulong>(a) & mask64;
                t += MemoryMarshal.Read<ushort>(l) & mask32;
                t = (t >> 6) * mul64;
                return (int)(t >> 56) - 8 - 2;
            }
            case 17: // RTX_16X64
            {
                ulong t = MemoryMarshal.Read<uint>(a) & mask32;
                t += MemoryMarshal.Read<ulong>(l) & mask64;
                t = (t >> 6) + ((MemoryMarshal.Read<ulong>(l.Slice(8)) & mask64) >> 6);
                t *= mul64;
                return (int)(t >> 56) - 4 - 16;
            }
            case 18: // RTX_64X16
            {
                ulong t = MemoryMarshal.Read<ulong>(a) & mask64;
                t += MemoryMarshal.Read<uint>(l) & mask32;
                t = (t >> 6) + ((MemoryMarshal.Read<ulong>(a.Slice(8)) & mask64) >> 6);
                t *= mul64;
                return (int)(t >> 56) - 16 - 4;
            }
            default:
                return 0;
        }
    }

    // ========================================================================
    // get_lo_ctx — Low-range token context
    // ========================================================================

    /// <summary>
    /// Computes low-range token context from neighboring coefficient levels.
    /// Ported from dav1d recon_tmpl.c get_lo_ctx().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLoCtx(
        ReadOnlySpan<byte> levels,
        Av1TxClass txClass,
        out uint hiMag,
        int loCtxOffsetsIdx,
        int x, int y,
        int stride)
    {
        uint mag = (uint)levels[0 * stride + 1] + levels[1 * stride + 0];
        int offset;

        if (txClass == Av1TxClass.TwoD)
        {
            mag += levels[1 * stride + 1];
            hiMag = mag;
            mag += (uint)levels[0 * stride + 2] + levels[2 * stride + 0];
            offset = Av1Tables.LoCtxOffsets[loCtxOffsetsIdx,
                Math.Min(y, 4), Math.Min(x, 4)];
        }
        else
        {
            mag += levels[0 * stride + 2];
            hiMag = mag;
            mag += (uint)levels[0 * stride + 3] + levels[0 * stride + 4];
            offset = 26 + (y > 1 ? 10 : y * 5);
        }

        return offset + (mag > 512 ? 4 : (int)((mag + 64) >> 7));
    }

    // ========================================================================
    // get_uv_inter_txtp — Derive chroma txfm type from luma for inter blocks
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Av1TxType GetUvInterTxtp(ref readonly Av1TxfmInfo uvtDim, Av1TxType yTxtp)
    {
        if (uvtDim.Max == (byte)Av1TxSize.Tx32x32)
            return yTxtp == Av1TxType.Identity ? Av1TxType.Identity : Av1TxType.DctDct;

        if (uvtDim.Min == (byte)Av1TxSize.Tx16x16)
        {
            uint flipMask = (1u << (int)Av1TxType.HFlipAdst) | (1u << (int)Av1TxType.VFlipAdst) |
                        (1u << (int)Av1TxType.HAdst) | (1u << (int)Av1TxType.VAdst);
            if (((1u << (int)yTxtp) & flipMask) != 0)
                return Av1TxType.DctDct;
        }

        return yTxtp;
    }

    // ========================================================================
    // DecodeCoefs — Main coefficient decode function
    // ========================================================================

    /// <summary>
    /// Decodes and dequantizes transform coefficients for a single transform block.
    /// Returns the end-of-block position (eob), or -1 if all coefficients are zero.
    /// Ported from dav1d recon_tmpl.c decode_coefs().
    /// </summary>
    /// <param name="msac">Arithmetic coder state.</param>
    /// <param name="coefCdf">CDF coefficient context.</param>
    /// <param name="modeCdf">CDF mode context (for txtp CDFs).</param>
    /// <param name="a">Above coefficient context pointer.</param>
    /// <param name="l">Left coefficient context pointer.</param>
    /// <param name="tx">Transform size (RectTxfmSize index).</param>
    /// <param name="bs">Block size.</param>
    /// <param name="segId">Segment ID.</param>
    /// <param name="yMode">Luma intra prediction mode.</param>
    /// <param name="uvMode">Chroma prediction mode.</param>
    /// <param name="yAngle">Luma angle delta (for filter_pred).</param>
    /// <param name="intra">1 if intra block, 0 for inter.</param>
    /// <param name="plane">0=luma, 1/2=chroma.</param>
    /// <param name="cf">Output coefficient buffer.</param>
    /// <param name="txtp">In/out transform type.</param>
    /// <param name="resCtx">Output residual context for neighbor update.</param>
    /// <param name="dqTable">Dequantization table for this segment+plane [2] (DC, AC).</param>
    /// <param name="qmTable">Quantization matrix (null if not using QM).</param>
    /// <param name="lossless">True if lossless mode.</param>
    /// <param name="reducedTxtpSet">True if reduced transform type set.</param>
    /// <param name="segQIdx">Segment quantizer index.</param>
    /// <param name="bitDepth">Bit depth (8 or 10).</param>
    /// <param name="levels">Scratch buffer for coefficient levels (must be large enough).</param>
    /// <returns>End of block position, or -1 if all-skip.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int DecodeCoefs(
        ref Av1Msac msac,
        Av1CdfCoefContext coefCdf,
        Av1CdfModeContext modeCdf,
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> l,
        int tx,
        int bs,
        int segId,
        int yMode,
        int uvMode,
        int yAngle,
        int intra,
        int plane,
        Span<int> cf,
        ref Av1TxType txtp,
        out byte resCtx,
        ReadOnlySpan<ushort> dqTable,
        ReadOnlySpan<byte> qmTable,
        bool lossless,
        bool reducedTxtpSet,
        int segQIdx,
        int bitDepth,
        Span<byte> levels,
        int layout)
    {
        int chroma = plane != 0 ? 1 : 0;
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];

        // Does this block have any non-zero coefficients?
        int sctx = GetSkipCtx(in tDim, bs, a, l, chroma, layout);

        int cdfIdx = tDim.Ctx * 13 + sctx;
        uint preRng = msac.DebugRng;
        ushort preCdf0 = coefCdf.CoefSkip[cdfIdx][0];
        Av1Msac.DbgLabel = $"allskip_tx{tx}_pl{plane}";
        int allSkip = (int)msac.DecodeBoolAdapt(coefCdf.CoefSkip[cdfIdx]);

        if (DbgCoefDecCount < 100)
        {
            DbgCoefDecCount++;
            AvDbg.W($"[COEF-DBG] #{DbgCoefDecCount} plane={plane} bs={(int)bs} tx={(int)tx} sctx={sctx} cdfIdx={cdfIdx} preCdf0={preCdf0} allskip={allSkip} preRng={preRng} rng={msac.DebugRng} bx={DbgBx} by={DbgBy}");
        }

        if (allSkip != 0)
        {
            resCtx = 0x40;
            txtp = lossless ? Av1TxType.WhtWht : Av1TxType.DctDct;
            if (DbgCoefDecCount <= 100)
                AvDbg.W($"[COEF-END] #{DbgCoefDecCount} eob=-1 rng={msac.DebugRng}");
            return -1;
        }

        // Transform type determination
        if (lossless)
        {
            txtp = Av1TxType.WhtWht;
        }
        else if (tDim.Max + intra >= (int)Av1TxSize.Tx64x64)
        {
            txtp = Av1TxType.DctDct;
        }
        else if (chroma != 0)
        {
            if (intra != 0)
                txtp = (Av1TxType)Av1Tables.TxTypeFromUvMode[uvMode];
            else
                txtp = GetUvInterTxtp(in tDim, txtp);
        }
        else if (segQIdx == 0)
        {
            txtp = Av1TxType.DctDct;
        }
        else
        {
            uint idx;
            if (intra != 0)
            {
                int yModeNoFilt = yMode == (int)Av1IntraPredMode.Filter
                    ? Av1Tables.FilterModeToYMode[yAngle]
                    : yMode;

                if (reducedTxtpSet || tDim.Min == (byte)Av1TxSize.Tx16x16)
                {
                    idx = msac.DecodeSymbolAdapt8(
                        modeCdf.TxtpIntra2[tDim.Min * 13 + yModeNoFilt], 4);
                    txtp = (Av1TxType)Av1Tables.TxTypesPerSet[idx + 0];
                }
                else
                {
                    idx = msac.DecodeSymbolAdapt8(
                        modeCdf.TxtpIntra1[tDim.Min * 13 + yModeNoFilt], 6);
                    txtp = (Av1TxType)Av1Tables.TxTypesPerSet[idx + 5];
                }

                if (DbgTxtpCount < 20)
                {
                    DbgTxtpCount++;
                    AvDbg.W($"[TXTP-DBG] #{DbgTxtpCount} intra idx={idx} txtp={(int)txtp} y_mode_nofilt={yModeNoFilt} t_dim_min={tDim.Min} rng={msac.DebugRng}");
                }
            }
            else
            {
                if (reducedTxtpSet || tDim.Max == (byte)Av1TxSize.Tx32x32)
                {
                    idx = msac.DecodeBoolAdapt(modeCdf.TxtpInter3[tDim.Min]);
                    txtp = (Av1TxType)((idx - 1) & (uint)Av1TxType.Identity);
                }
                else if (tDim.Min == (byte)Av1TxSize.Tx16x16)
                {
                    idx = msac.DecodeSymbolAdapt16(modeCdf.TxtpInter2, 11);
                    txtp = (Av1TxType)Av1Tables.TxTypesPerSet[idx + 12];
                }
                else
                {
                    idx = msac.DecodeSymbolAdapt16(
                        modeCdf.TxtpInter1[tDim.Min], 15);
                    txtp = (Av1TxType)Av1Tables.TxTypesPerSet[idx + 24];
                }
            }
        }

        // Find end-of-block (eob)
        int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32);
        int slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
        int tx2dSzCtx = slw + slh;
        var txClass = (Av1TxClass)Av1Tables.TxTypeClass[(int)txtp];
        int is1d = txClass != Av1TxClass.TwoD ? 1 : 0;

        bool dbgEntry = DbgCoefDecCount == 10;
        if (dbgEntry) AvDbg.W($"[COEF-STEP] #10 pre-eob txtp={(int)txtp} txClass={(int)txClass} rng={msac.DebugRng}");

        int eob = DecodeEobBin(ref msac, coefCdf, chroma, is1d, tx2dSzCtx);

        if (eob > 1)
        {
            int eobBin = eob - 2;
            Span<ushort> eobHiBitCdf = coefCdf.EobHiBit[tDim.Ctx * 2 * 9 + chroma * 9 + eobBin];
            int eobHiBit = (int)msac.DecodeBoolAdapt(eobHiBitCdf);
            eob = ((eobHiBit | 2) << eobBin) | (int)msac.DecodeBools(eobBin);
        }

        if (dbgEntry) AvDbg.W($"[COEF-STEP] #10 post-eob eob={eob} rng={msac.DebugRng}");

        // Base tokens
        int eobBaseTokIdx = tDim.Ctx * 2 * 4 + chroma * 4;
        int brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21 + chroma * 21;
        uint rc;
        uint dcTok = 0;

        if (DbgCoefDecCount <= 100 && eob == 0)
            AvDbg.W($"[EOB-CHK2] right before if(eob!=0): eob={eob} dcTok={dcTok}");

        if (eob != 0)
        {
            int baseTokIdx = tDim.Ctx * 2 * 41 + chroma * 41;

            // eob token
            uint ctx = (uint)(1 + (eob > 2 << tx2dSzCtx ? 1 : 0) + (eob > 4 << tx2dSzCtx ? 1 : 0));
            int eobTok = (int)msac.DecodeSymbolAdapt4(coefCdf.EobBaseTok[eobBaseTokIdx + ctx], 2);
            int tok = eobTok + 1;
            int levelTok = tok * 0x41;

            // Prepare scan/stride parameters based on tx class
            int stride, shift, shift2, mask;
            ushort[]? scan = null;
            int loCtxOffsetsIdx = -1;

            switch (txClass)
            {
                case Av1TxClass.TwoD:
                {
                    int nonsquareTx = tx >= (int)Av1RectTxSize.Rtx4x8 ? 1 : 0;
                    loCtxOffsetsIdx = nonsquareTx + (tx & nonsquareTx);
                    scan = Av1Tables.Scans[tx];
                    stride = 4 << slh;
                    shift = slh + 2;
                    shift2 = 0;
                    mask = (4 << slh) - 1;
                    levels.Slice(0, stride * ((4 << slw) + 2)).Clear();
                    break;
                }
                case Av1TxClass.Horizontal:
                {
                    stride = 16;
                    shift = slh + 2;
                    shift2 = 0;
                    mask = (4 << slh) - 1;
                    levels.Slice(0, stride * ((4 << slh) + 2)).Clear();
                    break;
                }
                default: // Vertical
                {
                    stride = 16;
                    shift = slw + 2;
                    shift2 = slh + 2;
                    mask = (4 << slw) - 1;
                    levels.Slice(0, stride * ((4 << slw) + 2)).Clear();
                    break;
                }
            }

            // Process eob coefficient
            uint x, y;
            int levelIdx;

            if (txClass == Av1TxClass.TwoD)
            {
                rc = scan![eob];
                x = rc >> shift;
                y = rc & (uint)mask;
            }
            else if (txClass == Av1TxClass.Horizontal)
            {
                x = (uint)(eob & mask);
                y = (uint)(eob >> shift);
                rc = (uint)eob;
            }
            else // Vertical
            {
                x = (uint)(eob & mask);
                y = (uint)(eob >> shift);
                rc = (x << shift2) | y;
            }

            if (eobTok == 2)
            {
                uint hiCtx = (uint)((txClass == Av1TxClass.TwoD ? (x | y) > 1 : y != 0) ? 14 : 7);
                tok = (int)msac.DecodeHiTok(coefCdf.BrTok[brTokIdx + hiCtx]);
                levelTok = tok + (3 << 6);
            }

            cf[(int)rc] = tok << 11;

            if (txClass == Av1TxClass.TwoD)
                levelIdx = (int)rc;
            else
                levelIdx = (int)(x * (uint)stride + y);
            levels[levelIdx] = (byte)levelTok;

            // AC coefficients (eob-1 down to 1)
            for (int i = eob - 1; i > 0; i--)
            {
                uint rcI;
                if (txClass == Av1TxClass.TwoD)
                {
                    rcI = scan![i];
                    x = rcI >> shift;
                    y = rcI & (uint)mask;
                }
                else if (txClass == Av1TxClass.Horizontal)
                {
                    x = (uint)(i & mask);
                    y = (uint)(i >> shift);
                    rcI = (uint)i;
                }
                else // Vertical
                {
                    x = (uint)(i & mask);
                    y = (uint)(i >> shift);
                    rcI = (x << shift2) | y;
                }

                if (txClass == Av1TxClass.TwoD)
                    levelIdx = (int)rcI;
                else
                    levelIdx = (int)(x * (uint)stride + y);

                uint hiMag;
                int loCtx = GetLoCtx(levels.Slice(levelIdx), txClass, out hiMag,
                    loCtxOffsetsIdx, (int)x, (int)y, stride);

                uint yForCtx = txClass == Av1TxClass.TwoD ? (y | x) : y;

                tok = (int)msac.DecodeSymbolAdapt4(coefCdf.BaseTok[baseTokIdx + loCtx], 3);

                if (dbgEntry) AvDbg.W($"[COEF-AC] i={i} x={x} y={y} rcI={rcI} loCtx={loCtx} tok={tok} rng={msac.DebugRng}");

                if (tok == 3)
                {
                    hiMag &= 63;
                    uint hiCtx = (uint)((yForCtx > (txClass == Av1TxClass.TwoD ? 1u : 0u) ? 14 : 7) +
                        (hiMag > 12 ? 6 : (hiMag + 1) >> 1));
                    tok = (int)msac.DecodeHiTok(coefCdf.BrTok[brTokIdx + hiCtx]);
                    if (dbgEntry) AvDbg.W($"[COEF-AC-HI] i={i} hiCtx={hiCtx} hiMag={hiMag} tok={tok} rng={msac.DebugRng}");
                    levels[levelIdx] = (byte)(tok + (3 << 6));
                    cf[(int)rcI] = (tok << 11) | (int)rc;
                    rc = rcI;
                }
                else
                {
                    // 0x1 for tok, 0x7ff as bitmask for rc, 0x41 for level_tok
                    uint tokU = (uint)tok * 0x17ff41u;
                    levels[levelIdx] = (byte)tokU;
                    // tok ? (tok << 11) | rc : 0
                    uint cfVal = (tokU >> 9) & (rc + ~0x7ffu);
                    if (cfVal != 0) rc = rcI;
                    cf[(int)rcI] = (int)cfVal;
                }
            }

            if (dbgEntry) AvDbg.W($"[COEF-STEP] #10 post-ac-loop rng={msac.DebugRng}");

            // DC coefficient
            int dcCtx;
            uint dcHiMag = 0;
            if (txClass == Av1TxClass.TwoD)
            {
                dcCtx = 0;
            }
            else
            {
                dcCtx = GetLoCtx(levels, txClass, out dcHiMag, loCtxOffsetsIdx, 0, 0, stride);
            }

            dcTok = msac.DecodeSymbolAdapt4(coefCdf.BaseTok[baseTokIdx + dcCtx], 3);

            if (dbgEntry) AvDbg.W($"[COEF-DC] #10 dcCtx={dcCtx} dcTok={dcTok} rng={msac.DebugRng}");

            if (dcTok == 3)
            {
                uint mag;
                if (txClass == Av1TxClass.TwoD)
                    mag = (uint)levels[0 * stride + 1] + levels[1 * stride + 0] + levels[1 * stride + 1];
                else
                    mag = dcHiMag;
                mag &= 63;
                uint hiCtx = mag > 12 ? 6 : (mag + 1) >> 1;
                dcTok = msac.DecodeHiTok(coefCdf.BrTok[brTokIdx + hiCtx]);
            }
        }
        else
        {
            // DC-only
            uint tokBr = msac.DecodeSymbolAdapt4(coefCdf.EobBaseTok[eobBaseTokIdx + 0], 2);
            dcTok = 1 + tokBr;

            if (tokBr == 2)
            {
                dcTok = msac.DecodeHiTok(coefCdf.BrTok[brTokIdx + 0]);
            }

            rc = 0;
        }

        if (dbgEntry) AvDbg.W($"[COEF-STEP] #10 post-dcTok dcTok={dcTok} rng={msac.DebugRng}");

        // Residual and sign — dequantization
        int dqShift = Math.Max(0, tDim.Ctx - 2);
        int cfMax = ~(~127 << (bitDepth == 8 ? 8 : bitDepth));
        uint culLevel;
        uint dcSignLevel;

        if (DbgCoefDecCount <= 100)
            AvDbg.W($"[DC-CHK] eob={eob} dcTok={dcTok} rc={rc} (about to check dcTok==0)");

        if (dcTok == 0)
        {
            culLevel = 0;
            dcSignLevel = 1 << 6;
            // Apply AC dequant
            if (qmTable.Length > 0)
                DequantAcQm(ref msac, cf, rc, dqTable[1], qmTable, dqShift, cfMax, ref culLevel);
            else
                DequantAcNoQm(ref msac, cf, rc, dqTable[1], dqShift, cfMax, ref culLevel);

            resCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);
            if (DbgCoefDecCount <= 100)
                AvDbg.W($"[COEF-END] #{DbgCoefDecCount} eob={eob} rng={msac.DebugRng}");
            return eob;
        }

        // DC sign
        int dcSignCtx = GetDcSignCtx(tx, a, l);
        Span<ushort> dcSignCdf = coefCdf.DcSign[chroma * 3 + dcSignCtx];
        int dcSign = (int)msac.DecodeBoolAdapt(dcSignCdf);

        int dcDq = dqTable[0];
        dcSignLevel = (uint)((dcSign - 1) & (2 << 6));

        if (DbgCoefDecCount <= 30)
            AvDbg.W($"[DC-RAW] #{DbgCoefDecCount} dcTok={dcTok} dcSign={dcSign} dcDq={dcDq} dqShift={dqShift} dcDqFinal={(dcDq * (int)dcTok) >> dqShift}");

        if (qmTable.Length > 0)
        {
            dcDq = (dcDq * qmTable[0] + 16) >> 5;

            if (dcTok == 15)
            {
                dcTok = ReadGolomb(ref msac) + 15;
                dcTok &= 0xfffff;
                dcDq = (dcDq * (int)dcTok) & 0xffffff;
            }
            else
            {
                dcDq *= (int)dcTok;
            }

            culLevel = dcTok;
            dcDq >>= dqShift;
            dcDq = Math.Min(dcDq, cfMax + dcSign);
            cf[0] = dcSign != 0 ? -dcDq : dcDq;

            DequantAcQm(ref msac, cf, rc, dqTable[1], qmTable, dqShift, cfMax, ref culLevel);
        }
        else
        {
            // Non-qmatrix (common case)
            if (dcTok == 15)
            {
                dcTok = ReadGolomb(ref msac) + 15;
                dcTok &= 0xfffff;
                dcDq = ((dcDq * (int)dcTok) & 0xffffff) >> dqShift;
                dcDq = Math.Min(dcDq, cfMax + dcSign);
            }
            else
            {
                dcDq = (dcDq * (int)dcTok) >> dqShift;
            }

            culLevel = dcTok;
            cf[0] = dcSign != 0 ? -dcDq : dcDq;

            DequantAcNoQm(ref msac, cf, rc, dqTable[1], dqShift, cfMax, ref culLevel);
        }

        resCtx = (byte)(Math.Min(culLevel, 63) | dcSignLevel);
        if (DbgCoefDecCount <= 30)
            AvDbg.W($"[COEF-END] #{DbgCoefDecCount} eob={eob} rng={msac.DebugRng}");
        return eob;
    }

    /// <summary>Dequantize AC coefficients with quantization matrix.</summary>
    private static void DequantAcQm(
        ref Av1Msac msac, Span<int> cf, uint rc,
        int acDqBase, ReadOnlySpan<byte> qmTable,
        int dqShift, int cfMax, ref uint culLevel)
    {
        while (rc != 0)
        {
            int sign = (int)msac.DecodeBoolEqui();
            uint rcTok = (uint)cf[(int)rc];
            uint tok;
            int dq = (acDqBase * qmTable[(int)rc] + 16) >> 5;

            if (rcTok >= (15u << 11))
            {
                tok = ReadGolomb(ref msac) + 15;
                tok &= 0xfffff;
                dq = (dq * (int)tok) & 0xffffff;
            }
            else
            {
                tok = rcTok >> 11;
                dq *= (int)tok;
            }

            culLevel += tok;
            dq >>= dqShift;
            int dqSat = Math.Min(dq, cfMax + sign);
            cf[(int)rc] = sign != 0 ? -dqSat : dqSat;

            rc = rcTok & 0x3ff;
        }
    }

    /// <summary>Dequantize AC coefficients without quantization matrix (common path).</summary>
    private static void DequantAcNoQm(
        ref Av1Msac msac, Span<int> cf, uint rc,
        int acDqBase, int dqShift, int cfMax, ref uint culLevel)
    {
        while (rc != 0)
        {
            int sign = (int)msac.DecodeBoolEqui();
            uint rcTok = (uint)cf[(int)rc];
            uint tok;
            int dq;

            if (rcTok >= (15u << 11))
            {
                tok = ReadGolomb(ref msac) + 15;
                tok &= 0xfffff;
                dq = ((acDqBase * (int)tok) & 0xffffff) >> dqShift;
                dq = Math.Min(dq, cfMax + sign);
            }
            else
            {
                tok = rcTok >> 11;
                dq = (acDqBase * (int)tok) >> dqShift;
            }

            culLevel += tok;
            cf[(int)rc] = sign != 0 ? -dq : dq;

            rc = rcTok & 0x3ff;
        }
    }

    // ========================================================================
    // DecodeEobBin — EOB bin decode (handles the size-dependent CDF switch)
    // ========================================================================

    /// <summary>Decode the EOB bin value from the appropriate size-dependent CDF.</summary>
    private static int DecodeEobBin(
        ref Av1Msac msac, Av1CdfCoefContext coefCdf,
        int chroma, int is1d, int tx2dSzCtx)
    {
        switch (tx2dSzCtx)
        {
            case 0: // 16 coeffs
                return (int)msac.DecodeSymbolAdapt8(
                    coefCdf.EobBin16[chroma * 2 + is1d], 4 + 0);
            case 1: // 32 coeffs
                return (int)msac.DecodeSymbolAdapt8(
                    coefCdf.EobBin32[chroma * 2 + is1d], 4 + 1);
            case 2: // 64 coeffs
                return (int)msac.DecodeSymbolAdapt8(
                    coefCdf.EobBin64[chroma * 2 + is1d], 4 + 2);
            case 3: // 128 coeffs
                return (int)msac.DecodeSymbolAdapt8(
                    coefCdf.EobBin128[chroma * 2 + is1d], 4 + 3);
            case 4: // 256 coeffs
                return (int)msac.DecodeSymbolAdapt16(
                    coefCdf.EobBin256[chroma * 2 + is1d], 4 + 4);
            case 5: // 512 coeffs
                return (int)msac.DecodeSymbolAdapt16(
                    coefCdf.EobBin512[chroma], 4 + 5);
            case 6: // 1024 coeffs
                return (int)msac.DecodeSymbolAdapt16(
                    coefCdf.EobBin1024[chroma], 4 + 6);
            default:
                return 0;
        }
    }

    // ========================================================================
    // ReadCoefTree — Transform split tree traversal
    // ========================================================================

    /// <summary>
    /// Recursively traverses the transform split tree and decodes coefficients.
    /// Ported from dav1d recon_tmpl.c read_coef_tree().
    /// </summary>
    public static void ReadCoefTree(
        ref Av1Msac msac,
        Av1CdfCoefContext coefCdf,
        Av1CdfModeContext modeCdf,
        Av1TileState ts,
        int bs,
        ref Av1Block b,
        int ytx,
        int depth,
        ReadOnlySpan<ushort> txSplit,
        int xOff, int yOff,
        Span<int> cf,
        Span<byte> aboveLcoef,
        Span<byte> leftLcoef,
        int bx4, int by4,
        int bw, int bh,
        ReadOnlySpan<ushort> dqTable,
        ReadOnlySpan<byte> qmTable,
        bool lossless,
        bool reducedTxtpSet,
        int segQIdx,
        int bitDepth,
        Span<byte> levels)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[ytx];
        int sub = tDim.Sub;

        // Check if this transform is split
        if (depth < 2 && (txSplit[depth + 1] & (ushort)(1 << (yOff * 4 + xOff))) != 0)
        {
            ref readonly Av1TxfmInfo subDim = ref Av1Tables.TxfmDimensions[sub];

            // Recurse into sub-transforms
            ReadCoefTree(ref msac, coefCdf, modeCdf, ts, bs, ref b, sub, depth + 1,
                txSplit, xOff * 2, yOff * 2, cf, aboveLcoef, leftLcoef,
                bx4, by4, bw, bh, dqTable, qmTable, lossless, reducedTxtpSet,
                segQIdx, bitDepth, levels);

            if (xOff * 2 + 1 < (bw >> subDim.Lw))
                ReadCoefTree(ref msac, coefCdf, modeCdf, ts, bs, ref b, sub, depth + 1,
                    txSplit, xOff * 2 + 1, yOff * 2, cf, aboveLcoef, leftLcoef,
                    bx4, by4, bw, bh, dqTable, qmTable, lossless, reducedTxtpSet,
                    segQIdx, bitDepth, levels);

            if (yOff * 2 + 1 < (bh >> subDim.Lh))
            {
                ReadCoefTree(ref msac, coefCdf, modeCdf, ts, bs, ref b, sub, depth + 1,
                    txSplit, xOff * 2, yOff * 2 + 1, cf, aboveLcoef, leftLcoef,
                    bx4, by4, bw, bh, dqTable, qmTable, lossless, reducedTxtpSet,
                    segQIdx, bitDepth, levels);

                if (xOff * 2 + 1 < (bw >> subDim.Lw))
                    ReadCoefTree(ref msac, coefCdf, modeCdf, ts, bs, ref b, sub, depth + 1,
                        txSplit, xOff * 2 + 1, yOff * 2 + 1, cf, aboveLcoef, leftLcoef,
                        bx4, by4, bw, bh, dqTable, qmTable, lossless, reducedTxtpSet,
                        segQIdx, bitDepth, levels);
            }

            return;
        }

        // Leaf node — decode coefficients
        int txw = tDim.W;
        int txh = tDim.H;

        int x = xOff * txw;
        int y = yOff * txh;

        // Compute above/left context slices
        ReadOnlySpan<byte> aCtx = aboveLcoef.Slice(bx4 + x);
        ReadOnlySpan<byte> lCtx = leftLcoef.Slice(by4 + y);

        Av1TxType txtp = default;
        byte cfCtx;

        int eob = DecodeCoefs(ref msac, coefCdf, modeCdf,
            aCtx, lCtx, ytx, bs,
            b.SegId, b.YMode, b.UvMode, b.YAngle,
            1, 0, cf, ref txtp, out cfCtx,
            dqTable, qmTable, lossless, reducedTxtpSet, segQIdx, bitDepth, levels, 0);

        // Update context
        int ctxW = Math.Min(txw, bw - x);
        int ctxH = Math.Min(txh, bh - y);
        aboveLcoef.Slice(bx4 + x, ctxW).Fill(cfCtx);
        leftLcoef.Slice(by4 + y, ctxH).Fill(cfCtx);
    }

    // ========================================================================
    // Palette decoding — AV1 spec section 6.8.20
    // Ported from dav1d: recon_tmpl.c (dav1d_read_pal_plane, dav1d_read_pal_uv)
    // and decode.c (read_pal_indices, order_palette)
    // ========================================================================

    /// <summary>
    /// Decode luma palette colors with neighbor cache prediction.
    /// Ported from dav1d_read_pal_plane() in recon_tmpl.c.
    /// </summary>
    public static void DecodeLumaPalette(
        ref Av1Msac msac,
        Av1CdfModeContext modeCdf,
        Av1TaskContext t,
        ref Av1Block b,
        int szCtx, int bx4, int by4,
        int bitDepth)
    {
        try
        {
        int palSz = b.PalSzY;
        if (palSz == 0) return;
        if (palSz > 8) palSz = 8;
        int bpc = bitDepth;
        int maxVal = (1 << bpc) - 1;

        // Step 1: Build predictor cache from left and above neighbors
        int lCacheSz = 0;
        Span<byte> lCache = stackalloc byte[8];
        int bi4 = by4 < 32 ? by4 : 31;
        int leftPalSz = t.Left.PalSz[bi4];
        if (leftPalSz > 8) leftPalSz = 0; // safety
        for (int ci = 0; ci < leftPalSz && ci < 8; ci++)
            lCache[lCacheSz++] = t.PalPrevY[1, bi4, 0 * 8 + ci];

        int aCacheSz = 0;
        Span<byte> aCache = stackalloc byte[8];
        int bj4 = bx4 < 32 ? bx4 : 31;
        int abovePalSz = t.Above.PalSz[bj4];
        if (abovePalSz > 8) abovePalSz = 0; // safety
        for (int ci = 0; ci < abovePalSz && ci < 8; ci++)
            aCache[aCacheSz++] = t.PalPrevY[0, bj4, 0 * 8 + ci];

        // Full instrumentation for first-row blocks and block #3
        bool dbgThis = (by4 == 0 && bx4 <= 28) || (bx4 == 12 && by4 < 12);
        if (dbgThis)
        {
            var sb = new System.Text.StringBuilder($"[PAL-STEP] recon#{DbgReconCount} Y bx4={bx4} by4={by4} palSz={palSz}");
            sb.Append($" left[{bi4}].PalSz={leftPalSz} lCache=[");
            for (int ci = 0; ci < lCacheSz; ci++) sb.Append($" {lCache[ci]:x2}");
            sb.Append($" ] above[{bj4}].PalSz={abovePalSz} aCache=[");
            for (int ci = 0; ci < aCacheSz; ci++) sb.Append($" {aCache[ci]:x2}");
            sb.Append(" ]");
            AvDbg.W(sb.ToString());
        }

        // Merge into sorted, deduplicated cache
        Span<byte> cache = stackalloc byte[8];
        int nCache = 0;
        int li2 = 0, ai2 = 0;
        while (li2 < lCacheSz && ai2 < aCacheSz)
        {
            if (lCache[li2] < aCache[ai2])
            {
                if (nCache == 0 || cache[nCache - 1] != lCache[li2]) cache[nCache++] = lCache[li2];
                li2++;
            }
            else
            {
                if (aCache[ai2] == lCache[li2]) li2++;
                if (nCache == 0 || cache[nCache - 1] != aCache[ai2]) cache[nCache++] = aCache[ai2];
                ai2++;
            }
        }
        while (li2 < lCacheSz) { if (nCache == 0 || cache[nCache - 1] != lCache[li2]) cache[nCache++] = lCache[li2]; li2++; }
        while (ai2 < aCacheSz) { if (nCache == 0 || cache[nCache - 1] != aCache[ai2]) cache[nCache++] = aCache[ai2]; ai2++; }

        if (dbgThis && nCache > 0)
        {
            var sb = new System.Text.StringBuilder($"[PAL-STEP]   mergedCache=[");
            for (int ci = 0; ci < nCache; ci++) sb.Append($" {cache[ci]:x2}");
            sb.Append($" ] nCache={nCache}");
            AvDbg.W(sb.ToString());
        }

        // Step 2: Select which cache entries to reuse
        Span<byte> usedCache = stackalloc byte[8];
        int nUsedCache = 0;
        for (int ci = 0; ci < nCache && nUsedCache < palSz; ci++)
        {
            ulong preD = msac.DebugDif;
            uint preR = msac.DebugRng;
            int preC = msac.Cnt;
            Av1Msac.TraceLabel = "pal-sel";
            uint bit = msac.DecodeBoolEqui();
            Av1Msac.TraceLabel = null;
            if (bit != 0) usedCache[nUsedCache++] = cache[ci];
            if (dbgThis)
                AvDbg.W($"[PAL-STEP]   equi[{ci}/{nCache}] pre: dif=0x{preD:X16} rng=0x{preR:X4} cnt={preC} bit={bit} used={bit!=0} val={cache[ci]:x2}");
            if (t.Bx == 0 && t.By <= 2 && !dbgThis)
                AvDbg.W($"[CACHE-SEL] block({t.Bx},{t.By}) ci={ci}/{nCache} pre: dif=0x{preD:X16} rng=0x{preR:X4} cnt={preC} bit={bit} used={bit!=0}");
        }

        if (dbgThis)
        {
            var sb = new System.Text.StringBuilder($"[PAL-STEP]   usedCache=[");
            for (int ci = 0; ci < nUsedCache; ci++) sb.Append($" {usedCache[ci]:x2}");
            sb.Append($" ] nUsed={nUsedCache}");
            AvDbg.W(sb.ToString());
        }

        // Step 3: Decode new palette entries (delta coding)
        Span<byte> newPal = stackalloc byte[8];
        int nNew = 0;
        int cnt = nUsedCache;

        if (cnt < palSz)
        {
            ulong preFirstD = msac.DebugDif;
            uint preFirstR = msac.DebugRng;
            int preFirstC = msac.Cnt;
            Av1Msac.TraceLabel = "pal-new";
            newPal[nNew++] = (byte)msac.DecodeBools(bpc);
            Av1Msac.TraceLabel = null;
            if (dbgThis)
                AvDbg.W($"[PAL-STEP]   firstNew pre: dif=0x{preFirstD:X16} rng=0x{preFirstR:X4} cnt={preFirstC} bpc={bpc} result=0x{newPal[0]:x2}");
            if (!DbgFirstBlockDone && palSz > 1)
            {
                DbgFirstBlockDone = true;
                AvDbg.W($"[FIRST-NEW-L] bx={bx4} by={by4} palSz={palSz} nCache={nCache} pre: dif=0x{preFirstD:X16} rng=0x{preFirstR:X4} cnt={preFirstC} result=0x{newPal[0]:x2} reconCount={Av1Reconstruction.DbgBlockCount}");
            }
            cnt++;

            if (cnt < palSz)
            {
                Av1Msac.TraceLabel = "pal-bits";
                int bits = bpc - 3 + (int)msac.DecodeBools(2);
                Av1Msac.TraceLabel = null;
                int prev = newPal[nNew - 1];

                do
                {
                    ulong preDeltaD = msac.DebugDif;
                    uint preDeltaR = msac.DebugRng;
                    int preDeltaC = msac.Cnt;
                    Av1Msac.TraceLabel = "pal-dlt";
                     int delta = (int)msac.DecodeBools(bits);
                    Av1Msac.TraceLabel = null;
                    if (dbgThis)
                        AvDbg.W($"[PAL-STEP]   delta#{nNew} pre: dif=0x{preDeltaD:X16} rng=0x{preDeltaR:X4} cnt={preDeltaC} bits={bits} delta={delta} prev={prev} -> {Math.Min(prev + delta + 1, maxVal)}");
                    prev = Math.Min(prev + delta + 1, maxVal);
                    newPal[nNew++] = (byte)prev;
                    cnt++;

                    if (prev + 1 >= maxVal)
                    {
                        while (cnt < palSz) newPal[nNew++] = (byte)maxVal;
                        cnt++;
                        break;
                    }
                    int ulog2 = 0, tmp = maxVal - prev - 1;
                    while (tmp > 1) { tmp >>= 1; ulog2++; }
                    bits = Math.Min(bits, 1 + ulog2);
                } while (cnt < palSz);
            }
        }

        // Step 4: Merge cache and new entries into sorted final palette
        int nn2 = 0, mm2 = 0;
        for (int ci = 0; ci < palSz; ci++)
        {
            if (nn2 < nUsedCache && (mm2 >= nNew || usedCache[nn2] <= newPal[mm2]))
                t.PalColorsY[ci] = usedCache[nn2++];
            else
                t.PalColorsY[ci] = newPal[mm2++];
        }

        // Debug: log palette colors for key blocks
        if (dbgThis || bx4 <= 8 && by4 <= 2 || bx4 == 0 && by4 == 8 || bx4 == 14 && by4 >= 8)
        {
            var sb = new System.Text.StringBuilder($"[PAL-DUMP] Y bx4={bx4} by4={by4} palSz={palSz} colors=[");
            for (int ci = 0; ci < palSz; ci++)
                sb.Append($" {t.PalColorsY[ci]:x2}");
            sb.Append(" ] cacheSz=");
            sb.Append($"l={lCacheSz}/a={aCacheSz} used={nUsedCache} new={nNew}");
            AvDbg.W(sb.ToString());
            // Also print first 16 palette indices for this block
            sb.Clear();
            sb.Append($"[PAL-IDX] Y bx4={bx4} by4={by4}:");
            for (int ci = 0; ci < Math.Min(16, t.PalIdxY.Length); ci++)
                sb.Append($" {t.PalIdxY[ci]}");
            AvDbg.W(sb.ToString());
        }

        // Store for future prediction with propagation to all cols/rows within block
        int bw = Av1Tables.BlockDimensions[b.BlockSize, 0];
        int bh = Av1Tables.BlockDimensions[b.BlockSize, 1];
        if (dbgThis)
            AvDbg.W($"[PAL-STEP]   store: bw={bw} bh={bh} bj4={bj4} bi4={bi4} -> above cols[{bj4}..{bj4+bw-1}] left rows[{bi4}..{bi4+bh-1}]");

        for (int dx = 0; dx < bw && bj4 + dx < 32; dx++)
        {
            int col = bj4 + dx;
            for (int ci = 0; ci < palSz; ci++)
                t.PalPrevY[0, col, 0 * 8 + ci] = t.PalColorsY[ci];
            t.PalPrevSz[0, col] = (byte)palSz;
        }
        for (int dy = 0; dy < bh && bi4 + dy < 32; dy++)
        {
            int row = bi4 + dy;
            for (int ci = 0; ci < palSz; ci++)
                t.PalPrevY[1, row, 0 * 8 + ci] = t.PalColorsY[ci];
            t.PalPrevSz[1, row] = (byte)palSz;
        }
        }
        catch (Exception ex)
        {
            AvDbg.W($"[PAL-CRASH-DETAIL] bx4={bx4} by4={by4} palSz={b.PalSzY}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode chroma (U/V) palette colors.
    /// Ported from dav1d_read_pal_uv() in recon_tmpl.c.
    /// </summary>
    public static void DecodeChromaPalette(
        ref Av1Msac msac,
        Av1CdfModeContext modeCdf,
        Av1TaskContext t,
        ref Av1Block b,
        int bitDepth)
    {
        int palSz = b.PalSzUv;
        if (palSz == 0) return;
        int bpc = bitDepth;
        int maxVal = (1 << bpc) - 1;

        // U plane
        {
            int lCacheSz = 0, aCacheSz = 0;
            Span<byte> lCache = stackalloc byte[8];
            Span<byte> aCache = stackalloc byte[8];
            int leftPalSz = t.PalSzUv[1, t.By & 31];
            int abovePalSz = t.PalSzUv[0, t.Bx & 31];
            for (int ci = 0; ci < leftPalSz && ci < 8; ci++) lCache[lCacheSz++] = t.PalPrevY[1, t.By & 31, 1 * 8 + ci];
            for (int ci = 0; ci < abovePalSz && ci < 8; ci++) aCache[aCacheSz++] = t.PalPrevY[0, t.Bx & 31, 1 * 8 + ci];

            Span<byte> cache = stackalloc byte[8];
            int nCache = 0;
            int li2 = 0, ai2 = 0;
            while (li2 < lCacheSz && ai2 < aCacheSz)
            {
                if (lCache[li2] < aCache[ai2]) { if (nCache == 0 || cache[nCache - 1] != lCache[li2]) cache[nCache++] = lCache[li2]; li2++; }
                else { if (aCache[ai2] == lCache[li2]) li2++; if (nCache == 0 || cache[nCache - 1] != aCache[ai2]) cache[nCache++] = aCache[ai2]; ai2++; }
            }
            while (li2 < lCacheSz) { if (nCache == 0 || cache[nCache - 1] != lCache[li2]) cache[nCache++] = lCache[li2]; li2++; }
            while (ai2 < aCacheSz) { if (nCache == 0 || cache[nCache - 1] != aCache[ai2]) cache[nCache++] = aCache[ai2]; ai2++; }

            Span<byte> usedCache = stackalloc byte[8];
            int nUsedCache = 0;
            for (int ci = 0; ci < nCache && nUsedCache < palSz; ci++)
                if (msac.DecodeBoolEqui() != 0) usedCache[nUsedCache++] = cache[ci];

            Span<byte> newPal = stackalloc byte[8];
            int nNew = 0;
            int cnt = nUsedCache;
            if (cnt < palSz)
            {
            ulong preFirstD = msac.DebugDif;
            uint preFirstR = msac.DebugRng;
            int preFirstC = msac.Cnt;
            newPal[nNew++] = (byte)msac.DecodeBools(bpc);
            if (t.Bx == 0 && t.By == 0 && nUsedCache == 0)
                AvDbg.W($"[FIRST-NEW-C] bx={t.Bx} by={t.By} palSz={palSz} nCache={nCache} pre: dif=0x{preFirstD:X16} rng=0x{preFirstR:X4} cnt={preFirstC} result=0x{newPal[0]:x2}");
                cnt++;
                if (cnt < palSz)
                {
                ulong preBitsState_dif = msac.DebugDif;
                uint preBitsState_rng = msac.DebugRng;
                int preBitsState_cnt = msac.Cnt;
                int bits = bpc - 3 + (int)msac.DecodeBools(2);
                if (t.Bx == 0 && t.By == 0)
                    AvDbg.W($"[BITS-TRACE] block({t.Bx},{t.By}) bitsPre: dif=0x{preBitsState_dif:X16} rng=0x{preBitsState_rng:X4} cnt={preBitsState_cnt} bits={bits}");
                    int prev = newPal[nNew - 1];
                    do
                    {
                        int delta = (int)msac.DecodeBools(bits);
                        prev = Math.Min(prev + delta, maxVal);
                        newPal[nNew++] = (byte)prev;
                        cnt++;
                        if (prev >= maxVal) { while (cnt < palSz) newPal[nNew++] = (byte)maxVal; cnt++; break; }
                        int ulog2 = 0, tmp = maxVal - prev;
                        while (tmp > 1) { tmp >>= 1; ulog2++; }
                        bits = Math.Min(bits, 1 + ulog2);
                    } while (cnt < palSz);
                }
            }

            int nn2 = 0, mm2 = 0;
            for (int ci = 0; ci < palSz; ci++)
            {
                if (nn2 < nUsedCache && (mm2 >= nNew || usedCache[nn2] <= newPal[mm2]))
                    t.PalColorsU[ci] = usedCache[nn2++];
                else
                    t.PalColorsU[ci] = newPal[mm2++];
            }
            // Store U-plane palette with propagation
            {
                int bj4u = (t.Bx & 31);
                int bi4u = (t.By & 31);
                int bwU = Av1Tables.BlockDimensions[b.BlockSize, 0];
                int bhU = Av1Tables.BlockDimensions[b.BlockSize, 1];
                for (int dx = 0; dx < bwU && bj4u + dx < 32; dx++)
                {
                    int col = bj4u + dx;
                    for (int ci = 0; ci < palSz; ci++)
                        t.PalPrevY[0, col, 1 * 8 + ci] = t.PalColorsU[ci];
                    t.PalPrevSz[0, col] = (byte)palSz;
                }
                for (int dy = 0; dy < bhU && bi4u + dy < 32; dy++)
                {
                    int row = bi4u + dy;
                    for (int ci = 0; ci < palSz; ci++)
                        t.PalPrevY[1, row, 1 * 8 + ci] = t.PalColorsU[ci];
                    t.PalPrevSz[1, row] = (byte)palSz;
                }
            }
        }

        // V plane — coded independently
        {
            if (msac.DecodeBoolEqui() != 0)
            {
                int bits = bpc - 4 + (int)msac.DecodeBools(2);
                int prev = (int)msac.DecodeBools(bpc);
                t.PalColorsV[0] = (byte)prev;
                for (int ci = 1; ci < palSz; ci++)
                {
                    int delta = (int)msac.DecodeBools(bits);
                    if (delta != 0 && msac.DecodeBoolEqui() != 0) delta = -delta;
                    prev = (prev + delta) & maxVal;
                    t.PalColorsV[ci] = (byte)prev;
                }
            }
            else
            {
                for (int ci = 0; ci < palSz; ci++)
                    t.PalColorsV[ci] = (byte)msac.DecodeBools(bpc);
            }
            // Store V-plane palette with propagation
            {
                int bj4v = (t.Bx & 31);
                int bi4v = (t.By & 31);
                int bwV = Av1Tables.BlockDimensions[b.BlockSize, 0];
                int bhV = Av1Tables.BlockDimensions[b.BlockSize, 1];
                for (int dx = 0; dx < bwV && bj4v + dx < 32; dx++)
                {
                    int col = bj4v + dx;
                    for (int ci = 0; ci < palSz; ci++)
                        t.PalPrevY[0, col, 2 * 8 + ci] = t.PalColorsV[ci];
                    t.PalPrevSz[0, col] = (byte)palSz;
                }
                for (int dy = 0; dy < bhV && bi4v + dy < 32; dy++)
                {
                    int row = bi4v + dy;
                    for (int ci = 0; ci < palSz; ci++)
                        t.PalPrevY[1, row, 2 * 8 + ci] = t.PalColorsV[ci];
                    t.PalPrevSz[1, row] = (byte)palSz;
                }
            }
        }
    }

    /// <summary>
    /// Decode palette indices in wavefront diagonal pattern.
    /// Ported from read_pal_indices() and order_palette() in dav1d decode.c.
    /// </summary>
    public static void DecodePaletteIndices(
        ref Av1Msac msac,
        Av1CdfModeContext modeCdf,
        Av1TaskContext t,
        int palSize,
        int width, int height,
        int blockWidth4, int blockHeight4,
        bool isLuma)
    {
        if (palSize <= 1)
        {
            byte[] dst = isLuma ? t.PalIdxY : t.PalIdxUv;
            for (int i = 0; i < width * height; i++) dst[i] = 0;
            return;
        }

        // Debug: dump initial ColorMap CDF for block (0,0)
        if (isLuma && t.Bx == 0 && t.By == 0)
        {
            int plane = 0;
            int cdi = plane * 35 + (palSize - 2) * 5;
            AvDbg.W($"[PAL-CDF-INIT] bx=0 by=0 palSize={palSize} cdfIdxBase={cdi}");
            for (int c = 0; c < 5; c++)
            {
                var cdfArr = modeCdf.ColorMap[cdi + c];
                AvDbg.W($"  ctx={c}: [{cdfArr[0]} {cdfArr[1]} {cdfArr[2]} {cdfArr[3]} ...]");
            }
        }

        // Save pre-state for DLL comparison
        ulong preDllDif = msac.DebugDif;
        uint preDllRng = msac.DebugRng;
        int preDllCnt = msac.Cnt;
        int preDllPos = msac.DebugPos;

        byte[] palTmp = isLuma ? t.PalIdxY : t.PalIdxUv;
        // stride is the full block width in pixels (bw4 * 4)
        int stride = blockWidth4 * 4;
        int totalPixels = width * height;

        // Safety check
        if (palTmp.Length < stride * blockHeight4 * 4)
        {
            AvDbg.W($"[PAL-ERR] Buffer too small: need {stride * blockHeight4 * 4}, have {palTmp.Length}");
            return;
        }

        // DLL comparison: call dav1d first with pre-state
        var palCmp = Av1Msac.DllComparePalIdxFunc;
        byte[]? dllPalIdx = null;
        if (palCmp != null)
        {
            int plane = isLuma ? 0 : 1;
            AvDbg.W($"[PAL-INFO] bx={t.Bx} by={t.By} isLuma={isLuma} palSize={palSize} w={width} h={height} bw4={blockWidth4} bh4={blockHeight4}");
            ushort[] colorMapCdf = new ushort[5 * 8];
            for (int ctx = 0; ctx < 5; ctx++)
            {
                int srcIdx = plane * 35 + (palSize - 2) * 5 + ctx;
                if (srcIdx < modeCdf.ColorMap.Length)
                {
                    var src = modeCdf.ColorMap[srcIdx];
                    for (int i = 0; i < Math.Min(8, src.Length); i++)
                        colorMapCdf[ctx * 8 + i] = src[i];
                }
            }
            byte[] ourDummy = new byte[width * height];
            dllPalIdx = palCmp(
                preDllRng, (uint)preDllDif, (uint)(preDllDif >> 32), preDllCnt,
                palSize, width, height, blockWidth4, blockHeight4, isLuma,
                colorMapCdf,
                msac.DebugData, preDllPos, msac.DebugDataEnd,
                ourDummy);
        }
        // Decode top-left pixel using uniform distribution
        ulong preUniformDif = msac.DebugDif;
        uint preUniformRng = msac.DebugRng;
        int preUniformCnt = msac.Cnt;
        Av1Msac.TraceLabel = "pal-uni";
        palTmp[0] = (byte)msac.DecodeUniform((uint)palSize);
        Av1Msac.TraceLabel = null;
        if (isLuma)
        {
            AvDbg.W($"[UNI-TRACE] block({t.Bx},{t.By}) palSize={palSize} pre: dif=0x{preUniformDif:X16} rng=0x{preUniformRng:X4} cnt={preUniformCnt} result={palTmp[0]} post: rng=0x{msac.DebugRng:X4} cnt={msac.Cnt}");
        }

        // Step 2: Decode remaining pixels diagonal by diagonal
        // dav1d: for (int i = 1; i < 4 * (w4 + h4) - 1; i++)
        Span<byte> order = stackalloc byte[8];
        int maxDiag = 4 * (blockWidth4 + blockHeight4) - 1;
        for (int diag = 1; diag < maxDiag; diag++)
        {
            int first = Math.Min(diag, width - 1);
            int last = Math.Max(0, diag - height + 1);

            for (int x = first; x >= last; x--)
            {
                int y = diag - x;
                // dav1d index: (i - j) * stride + j where i=diag, j=x
                int idx = (diag - x) * stride + x;

                int l = x > 0 ? palTmp[y * stride + x - 1] : (byte)0xFF;
                int tt = y > 0 ? palTmp[(y - 1) * stride + x] : (byte)0xFF;
                int tl = (x > 0 && y > 0) ? palTmp[(y - 1) * stride + x - 1] : (byte)0xFF;

                // Build color order based on context
                int ctx = BuildColorOrder(order, palSize, l, tt, tl);

                // Decode color index
                // ColorMap is indexed as [plane][palSize-2][ctx], flattened to [plane*35 + (palSize-2)*5 + ctx]
                // dav1d: color_map[2][7][5][8] -> plane stride = 7*5 = 35
                int plane = isLuma ? 0 : 1;
                int cdfIdx = plane * 35 + (palSize - 2) * 5 + ctx;
                if (cdfIdx < 0 || cdfIdx >= modeCdf.ColorMap.Length)
                {
                    AvDbg.W($"[PAL-ERR] ColorMap index out of bounds: cdfIdx={cdfIdx} ctx={ctx} palSize={palSize}");
                    palTmp[idx] = 0;
                    continue;
                }
                uint preDecodeRng = msac.DebugRng;
                int preDecodeCnt = msac.Cnt;
                int colorIdx = (int)msac.DecodeSymbolAdapt8(modeCdf.ColorMap[cdfIdx], palSize - 1);
                if (t.Bx == 0 && t.By == 0 && isLuma)
                {
                    var cdf = modeCdf.ColorMap[cdfIdx];
                    AvDbg.W($"[OUR-SYM] pixel({x},{diag-x}) ctx={ctx} pre: rng=0x{preDecodeRng:X4} cnt={preDecodeCnt} post: rng=0x{msac.DebugRng:X4} cnt={msac.Cnt} result={colorIdx} nSym={palSize-1} cdf0={cdf[0]} cdf1={cdf[1]}");
                }
                if (colorIdx < 0 || colorIdx >= palSize)
                {
                    AvDbg.W($"[PAL-ERR] colorIdx out of bounds: {colorIdx} palSize={palSize}");
                    colorIdx = 0;
                }
                palTmp[idx] = order[colorIdx];
            }
        }
        
        // Compare with DLL results
        if (dllPalIdx != null)
        {
            int mismatchCount = 0;
            int firstMismatch = -1;
            for (int i = 0; i < width * height; i++)
            {
                if (palTmp[i] != dllPalIdx[i])
                {
                    if (mismatchCount == 0) firstMismatch = i;
                    mismatchCount++;
                }
            }
            if (mismatchCount > 0)
            {
                AvDbg.W($"[PAL-DIFF] block bx={t.Bx} by={t.By} isLuma={isLuma} palSize={palSize} {width}x{height} mismatches={mismatchCount}/{width*height} firstMismatch at pixel {firstMismatch}: our={palTmp[firstMismatch]} dll={dllPalIdx[firstMismatch]} (our first 8: {string.Join(",", palTmp.Take(8))} dll first 8: {string.Join(",", dllPalIdx.Take(8))})");
            }
        }

        // Dump decoded indices for block (4,0)
        if (isLuma && t.Bx == 4 && t.By == 0)
        {
            AvDbg.W($"[PIDX-DUMP] block({t.Bx},{t.By}) palSize={palSize} stride={stride} w={width} h={height}");
            for (int dy = 0; dy < height; dy++)
            {
                AvDbg.W($"  row{dy}:");
                for (int dx = 0; dx < width; dx++)
                    AvDbg.W($" {palTmp[dy * stride + dx]}");
                AvDbg.W();
            }
        }
    }

    /// <summary>
    /// Build the color order array based on spatial neighbor context.
    /// Ported from order_palette() in dav1d decode.c.
    /// </summary>
    private static int BuildColorOrder(Span<byte> order, int palSize, int l, int t, int tl)
    {
        int ctx;
        int oIdx = 0;
        uint mask = 0;

        if (l == 0xFF || t == 0xFF)
        {
            // No left or no top neighbor
            ctx = 0;
            int pred = l != 0xFF ? l : t;
            if (pred < palSize) { order[oIdx++] = (byte)pred; mask |= 1u << pred; }
        }
        else if (l == t && t == tl)
        {
            // All same
            ctx = 4;
            order[oIdx++] = (byte)t;
            mask |= 1u << t;
        }
        else if (l == t)
        {
            // Left equals top
            ctx = 3;
            order[oIdx++] = (byte)t;
            mask |= 1u << t;
            if (tl < palSize && (mask & (1u << tl)) == 0) { order[oIdx++] = (byte)tl; mask |= 1u << tl; }
        }
        else if (t == tl || l == tl)
        {
            // Top equals top-left or left equals top-left
            ctx = 2;
            order[oIdx++] = (byte)tl;
            mask |= 1u << tl;
            int other = (t == tl) ? l : t;
            if (other < palSize && (mask & (1u << other)) == 0) { order[oIdx++] = (byte)other; mask |= 1u << other; }
        }
        else
        {
            // All different
            ctx = 1;
            int lo = Math.Min(t, l);
            int hi = Math.Max(t, l);
            if (lo < palSize) { order[oIdx++] = (byte)lo; mask |= 1u << lo; }
            if (hi < palSize && (mask & (1u << hi)) == 0) { order[oIdx++] = (byte)hi; mask |= 1u << hi; }
            if (tl < palSize && (mask & (1u << tl)) == 0) { order[oIdx++] = (byte)tl; mask |= 1u << tl; }
        }

        // Fill remaining colors
        for (int bit = 0; bit < 8 && oIdx < palSize; bit++)
        {
            if ((mask & (1u << bit)) == 0)
                order[oIdx++] = (byte)bit;
        }

        if (_palCtxCount < 12) { _palCtxCount++; AvDbg.W($"[CTX #{_palCtxCount}] l={l} t={t} tl={tl} ps={palSize} ctx={ctx}"); }
        return ctx;
    }

    static int _palCtxCount = 0;
}
