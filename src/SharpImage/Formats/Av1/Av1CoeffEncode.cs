// AV1 coefficient ENCODER — the write-side of Av1CoeffDecode.DecodeCoefs, for the 2D transform class
// (DCT_DCT and other TwoD types). Given a transform block's quantized coefficients (rc-indexed, matching the
// decoder's cf[] layout), it emits the exact MSAC symbol sequence the decoder reads back: all_zero, the EOB
// bin + extra bits, the end-of-block base token, the AC base tokens (with GetLoCtx neighbour contexts and the
// base-range HiTok extension), the DC base token, then the DC sign + AC signs (increasing scan order) with
// Golomb tails for levels >= 15. Verified by round-tripping through DecodeCoefs and end-to-end through dav1d.
//
// Scope: txClass == TwoD only (the caller passes a DctDct-eligible size so no tx-type symbol is coded). Contexts
// are computed with the decoder's own public helpers so the two sides cannot drift.
using System;
using System.Numerics;

namespace SharpImage.Formats.Av1;

internal static class Av1CoeffEncode
{
    /// <summary>Encodes one 2D (DctDct) transform block. <paramref name="signedLevels"/> is indexed by rc (the
    /// same coefficient index the decoder writes cf[] with): signedLevels[rc] is the quantized, signed level at
    /// that position (0 = absent). <paramref name="txCtxOverride"/>… no — everything derives from <paramref
    /// name="tx"/>. Only luma (chroma=0) DctDct is exercised so far.</summary>
    internal static void EncodeCoefs(
        Av1MsacWriter w,
        Av1CdfCoefContext coef,
        Av1CdfModeContext modeCdf,
        int tx,
        int chroma,
        int yMode,
        ReadOnlySpan<int> signedLevels,
        int skipCtx = 0,
        int dcSignCtx = 0,
        int txTypeIdx = 1,
        bool fullSet = false,
        bool lossless = false,
        bool inter = false)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];

        // Coefficient-skip (txb_skip) context. For a block whose size equals its transform (our single-block and
        // full-64 superblock cases) this is 0; callers with sub-block transforms pass the real value.
        int cdfIdx = tDim.Ctx * 13 + skipCtx;

        // Scan / level-buffer geometry (mirrors DecodeCoefs TwoD branch).
        int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32);
        int slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
        int tx2dSzCtx = slw + slh;
        ushort[] scan = Av1Tables.Scans[tx];
        int shift = slh + 2;
        int stride = 4 << slh;
        int mask = (4 << slh) - 1;

        // Find eob = highest scan index holding a nonzero level.
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--)
        {
            if (signedLevels[scan[i]] != 0)
            {
                eob = i;
                break;
            }
        }

        // all_zero (txb_skip): 1 ⇒ no coefficients.
        if (eob < 0)
        {
            w.EncodeBoolAdapt(coef.CoefSkip[cdfIdx], 1);
            return;
        }

        w.EncodeBoolAdapt(coef.CoefSkip[cdfIdx], 0);

        // --- Transform type ---
        // The decoder codes a tx-type symbol only for intra LUMA when tDim.Max + intra < TX_64X64 (i.e. TX_16X16
        // and smaller) and segQIdx != 0; chroma derives its type from the UV mode (no symbol), and
        // TX_32X32/TX_64X64 imply DctDct with no symbol. We always emit DctDct = index 1 in the reduced Intra2
        // set (TxtpIntra2[tDim.Min*13 + yMode]).
        if (inter)
        {
            // Inter luma below 64 points codes its type; with reduced_tx_set (what the inter encoder signals) and for
            // 32-point transforms it is the 2-type Inter3 set: 1 = DCT_DCT, 0 = IDTX. Chroma derives it from luma.
            if (chroma == 0 && !lossless && tDim.Max < (int)Av1TxSize.Tx64x64)
                w.EncodeBoolAdapt(modeCdf.TxtpInter3[tDim.Min], 1);
        }
        else if (chroma == 0 && !lossless && tDim.Max + 1 < (int)Av1TxSize.Tx64x64)   // lossless: WHT_WHT implied
        {
            // Full intra set (reduced_tx_set=0): sub-16x16 luma codes the 7-type Intra1 symbol; larger tx and the
            // reduced set code the 5-type Intra2 symbol. The caller passes the Intra2 index; map it to Intra1 here.
            if (fullSet && tDim.Min < (int)Av1TxSize.Tx16x16)
                w.EncodeSymbolAdapt(modeCdf.TxtpIntra1[tDim.Min * 13 + yMode], Intra2ToIntra1(txTypeIdx), 6);
            else
                w.EncodeSymbolAdapt(modeCdf.TxtpIntra2[tDim.Min * 13 + yMode], txTypeIdx, 4);
        }

        // --- EOB bin + extra bits ---
        EncodeEob(w, coef, chroma, tx2dSzCtx, tDim.Ctx, eob);

        int eobBaseTokIdx = tDim.Ctx * 2 * 4 + chroma * 4;
        int baseTokIdx = tDim.Ctx * 2 * 41 + chroma * 41;
        int brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21 + chroma * 21;

        // Level buffer for GetLoCtx neighbours (same layout/size as decode; padded by +2 in each dim).
        var levels = new byte[stride * ((4 << slw) + 2)];

        if (eob != 0)
        {
            // --- End-of-block coefficient (highest scan index) ---
            int rcEob = scan[eob];
            int xE = rcEob >> shift;
            int yE = rcEob & mask;
            int magEob = Math.Abs(signedLevels[rcEob]);

            // eob token: eobTok = min(mag,3)-1 ∈ {0,1,2}; tok==3 (eobTok==2) extends via HiTok.
            uint ctx = (uint)(1 + (eob > (2 << tx2dSzCtx) ? 1 : 0) + (eob > (4 << tx2dSzCtx) ? 1 : 0));
            int eobTok = Math.Min(magEob, 3) - 1;
            w.EncodeSymbolAdapt(coef.EobBaseTok[eobBaseTokIdx + ctx], eobTok, 2);
            if (eobTok == 2)
            {
                int hiCtx = ((xE | yE) > 1) ? 14 : 7;
                EncodeHiTok(w, coef.BrTok[brTokIdx + hiCtx], magEob);
            }

            levels[rcEob] = LevelByte(magEob);

            // --- AC coefficients from eob-1 down to 1 ---
            for (int i = eob - 1; i > 0; i--)
            {
                int rcI = scan[i];
                int x = rcI >> shift;
                int y = rcI & mask;
                int mag = Math.Abs(signedLevels[rcI]);

                int loCtx = Av1CoeffDecode.GetLoCtx(levels.AsSpan(rcI), Av1TxClass.TwoD, out uint hiMag,
                    LoCtxOffsetsIdx(tx), x, y, stride);

                int tok = Math.Min(mag, 3);
                w.EncodeSymbolAdapt(coef.BaseTok[baseTokIdx + loCtx], tok, 3);
                if (tok == 3)
                {
                    uint yForCtx = (uint)(y | x);
                    hiMag &= 63;
                    int hiCtx = (int)((yForCtx > 1 ? 14u : 7u) + (hiMag > 12 ? 6u : (hiMag + 1) >> 1));
                    EncodeHiTok(w, coef.BrTok[brTokIdx + hiCtx], mag);
                }

                levels[rcI] = LevelByte(mag);
            }

            // --- DC coefficient ---
            int dcMag = Math.Abs(signedLevels[0]);
            int dcTokBase = Math.Min(dcMag, 3);
            w.EncodeSymbolAdapt(coef.BaseTok[baseTokIdx + 0], dcTokBase, 3); // dcCtx = 0 for TwoD
            if (dcTokBase == 3)
            {
                uint mag = (uint)(levels[0 * stride + 1] + levels[1 * stride + 0] + levels[1 * stride + 1]) & 63;
                int hiCtx = (int)(mag > 12 ? 6u : (mag + 1) >> 1);
                EncodeHiTok(w, coef.BrTok[brTokIdx + hiCtx], dcMag);
            }

            // --- Signs + Golomb ---
            // DC sign first (only if DC nonzero), then AC nonzeros in increasing scan order (eob-position last).
            if (dcMag != 0)
            {
                EncodeSignAndGolomb(w, coef.DcSign[chroma * 3 + dcSignCtx], adaptSign: true, signedLevels[0], dcMag);
            }

            for (int i = 1; i <= eob; i++)
            {
                int rcI = scan[i];
                int mag = Math.Abs(signedLevels[rcI]);
                if (mag != 0)
                {
                    EncodeSignAndGolomb(w, default, adaptSign: false, signedLevels[rcI], mag);
                }
            }
        }
        else
        {
            // DC-only (eob == 0).
            int dcMag = Math.Abs(signedLevels[0]);
            int tokBr = Math.Min(dcMag, 3) - 1; // dcMag >= 1 here
            w.EncodeSymbolAdapt(coef.EobBaseTok[eobBaseTokIdx + 0], tokBr, 2);
            if (tokBr == 2)
            {
                EncodeHiTok(w, coef.BrTok[brTokIdx + 0], dcMag);
            }

            EncodeSignAndGolomb(w, coef.DcSign[chroma * 3 + dcSignCtx], adaptSign: true, signedLevels[0], dcMag);
        }
    }

    // Map a reduced-set (Intra2) tx-type index to its full-set (Intra1) index.
    // Intra2 {Identity,DctDct,AdstAdst,AdstDct,DctAdst}=0..4 -> Intra1 {..,VDct,HDct,..}=0,1,4,5,6.
    private static int Intra2ToIntra1(int idx) => idx switch { 0 => 0, 1 => 1, 2 => 4, 3 => 5, 4 => 6, _ => 1 };

    // Index of a 1D-DCT tx type within the full Intra1 set (TxTypesPerSet offset 5:
    // {Identity,DctDct,VDct,HDct,AdstAdst,AdstDct,DctAdst}). Only VDct/HDct reach this path.
    private static int Intra1Index(Av1TxType t) => t switch
    {
        Av1TxType.Identity => 0, Av1TxType.DctDct => 1, Av1TxType.VDct => 2, Av1TxType.HDct => 3,
        Av1TxType.AdstAdst => 4, Av1TxType.AdstDct => 5, Av1TxType.DctAdst => 6, _ => 1,
    };

    // cf[] index for scan position i under a 1D tx class (mirrors DecodeCoefs): Horizontal rc=i; Vertical
    // rc=(x<<shift2)|y with x=i&mask, y=i>>shift.
    private static int Rc1d(int i, Av1TxClass cls, int shift, int shift2, int mask)
        => cls == Av1TxClass.Horizontal ? i : (((i & mask) << shift2) | (i >> shift));

    /// <summary>Encodes one 1D-class (V_DCT / H_DCT) intra luma transform block — the write-side of DecodeCoefs'
    /// Horizontal/Vertical branches. Emits all_zero, the full-set (Intra1) tx-type symbol, EOB (is1d=1), the
    /// eob/AC/DC tokens with the 1D geometry + class contexts, then DC + AC signs (increasing natural scan order).
    /// signedLevels is indexed by the class rc (same as cf[]). Scoped to square tx (4x4/8x8) for now.</summary>
    internal static void EncodeCoefs1D(
        Av1MsacWriter w, Av1CdfCoefContext coef, Av1CdfModeContext modeCdf,
        int tx, int yMode, Av1TxType txtp, ReadOnlySpan<int> signedLevels,
        int skipCtx = 0, int dcSignCtx = 0)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];
        var cls = (Av1TxClass)Av1Tables.TxTypeClass[(int)txtp]; // 1 = Horizontal, 2 = Vertical
        int cdfIdx = tDim.Ctx * 13 + skipCtx;
        int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32);
        int slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
        int tx2dSzCtx = slw + slh;

        const int stride = 16;
        int shift, shift2 = 0, mask;
        if (cls == Av1TxClass.Horizontal) { shift = slh + 2; mask = (4 << slh) - 1; }
        else { shift = slw + 2; shift2 = slh + 2; mask = (4 << slw) - 1; }
        int area = (4 << slw) * (4 << slh);

        int eob = -1;
        for (int i = area - 1; i >= 0; i--)
            if (signedLevels[Rc1d(i, cls, shift, shift2, mask)] != 0) { eob = i; break; }

        if (eob < 0) { w.EncodeBoolAdapt(coef.CoefSkip[cdfIdx], 1); return; }
        w.EncodeBoolAdapt(coef.CoefSkip[cdfIdx], 0);

        // tx-type symbol from the full Intra1 set (7 syms) — only reached for tDim.Min < 16x16 intra luma.
        w.EncodeSymbolAdapt(modeCdf.TxtpIntra1[tDim.Min * 13 + yMode], Intra1Index(txtp), 6);

        EncodeEob(w, coef, 0, tx2dSzCtx, tDim.Ctx, eob, is1d: 1);

        int eobBaseTokIdx = tDim.Ctx * 2 * 4;
        int baseTokIdx = tDim.Ctx * 2 * 41;
        int brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21;
        var levels = new byte[stride * 20];

        if (eob != 0)
        {
            int xE = eob & mask, yE = eob >> shift;
            int rcEob = Rc1d(eob, cls, shift, shift2, mask);
            int magEob = Math.Abs(signedLevels[rcEob]);
            uint ctx = (uint)(1 + (eob > (2 << tx2dSzCtx) ? 1 : 0) + (eob > (4 << tx2dSzCtx) ? 1 : 0));
            int eobTok = Math.Min(magEob, 3) - 1;
            w.EncodeSymbolAdapt(coef.EobBaseTok[eobBaseTokIdx + ctx], eobTok, 2);
            if (eobTok == 2)
                EncodeHiTok(w, coef.BrTok[brTokIdx + (yE != 0 ? 14 : 7)], magEob);
            levels[xE * stride + yE] = LevelByte(magEob);

            for (int i = eob - 1; i > 0; i--)
            {
                int x = i & mask, y = i >> shift, levelIdx = x * stride + y;
                int mag = Math.Abs(signedLevels[Rc1d(i, cls, shift, shift2, mask)]);
                int loCtx = Av1CoeffDecode.GetLoCtx(levels.AsSpan(levelIdx), cls, out uint hiMag, -1, x, y, stride);
                int tok = Math.Min(mag, 3);
                w.EncodeSymbolAdapt(coef.BaseTok[baseTokIdx + loCtx], tok, 3);
                if (tok == 3)
                {
                    hiMag &= 63;
                    int hiCtx = (int)((y > 0 ? 14u : 7u) + (hiMag > 12 ? 6u : (uint)(hiMag + 1) >> 1));
                    EncodeHiTok(w, coef.BrTok[brTokIdx + hiCtx], mag);
                }
                levels[levelIdx] = LevelByte(mag);
            }

            int dcMag = Math.Abs(signedLevels[0]);
            int dcCtx = Av1CoeffDecode.GetLoCtx(levels, cls, out uint dcHiMag, -1, 0, 0, stride);
            int dcTokBase = Math.Min(dcMag, 3);
            w.EncodeSymbolAdapt(coef.BaseTok[baseTokIdx + dcCtx], dcTokBase, 3);
            if (dcTokBase == 3)
            {
                uint mg = dcHiMag & 63;
                EncodeHiTok(w, coef.BrTok[brTokIdx + (int)(mg > 12 ? 6u : (mg + 1) >> 1)], dcMag);
            }

            if (dcMag != 0)
                EncodeSignAndGolomb(w, coef.DcSign[dcSignCtx], adaptSign: true, signedLevels[0], dcMag);
            for (int i = 1; i <= eob; i++)
            {
                int rcI = Rc1d(i, cls, shift, shift2, mask);
                int mag = Math.Abs(signedLevels[rcI]);
                if (mag != 0) EncodeSignAndGolomb(w, default, adaptSign: false, signedLevels[rcI], mag);
            }
        }
        else
        {
            int dcMag = Math.Abs(signedLevels[0]);
            int tokBr = Math.Min(dcMag, 3) - 1;
            w.EncodeSymbolAdapt(coef.EobBaseTok[eobBaseTokIdx + 0], tokBr, 2);
            if (tokBr == 2) EncodeHiTok(w, coef.BrTok[brTokIdx + 0], dcMag);
            EncodeSignAndGolomb(w, coef.DcSign[dcSignCtx], adaptSign: true, signedLevels[0], dcMag);
        }
    }

    /// <summary>Estimated bit cost of a 1D-class (V_DCT/H_DCT) block — the no-side-effect mirror of EncodeCoefs1D,
    /// for the tx-type RD search. Scoped to square tx (4x4/8x8) as EncodeCoefs1D.</summary>
    internal static double EstimateCoefBits1D(Av1CdfCoefContext coef, Av1CdfModeContext modeCdf,
        int tx, int yMode, Av1TxType txtp, ReadOnlySpan<int> signedLevels, int skipCtx, int dcSignCtx)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];
        var cls = (Av1TxClass)Av1Tables.TxTypeClass[(int)txtp];
        int cdfIdx = tDim.Ctx * 13 + skipCtx;
        int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32);
        int slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
        int tx2dSzCtx = slw + slh;
        const int stride = 16;
        int shift, shift2 = 0, mask;
        if (cls == Av1TxClass.Horizontal) { shift = slh + 2; mask = (4 << slh) - 1; }
        else { shift = slw + 2; shift2 = slh + 2; mask = (4 << slw) - 1; }
        int area = (4 << slw) * (4 << slh);

        int eob = -1;
        for (int i = area - 1; i >= 0; i--)
            if (signedLevels[Rc1d(i, cls, shift, shift2, mask)] != 0) { eob = i; break; }

        if (eob < 0) return BoolBits(coef.CoefSkip[cdfIdx][0], 1);
        double bits = BoolBits(coef.CoefSkip[cdfIdx][0], 0);
        bits += SymBits(modeCdf.TxtpIntra1[tDim.Min * 13 + yMode], Intra1Index(txtp));

        // EOB (is1d=1)
        int eobPt = eob <= 1 ? eob : (31 - BitOperations.LeadingZeroCount((uint)eob) - 1) + 2;
        ReadOnlySpan<ushort> eobCdf = tx2dSzCtx switch
        {
            0 => coef.EobBin16[1], 1 => coef.EobBin32[1], 2 => coef.EobBin64[1],
            3 => coef.EobBin128[1], 4 => coef.EobBin256[1], 5 => coef.EobBin512[0], _ => coef.EobBin1024[0],
        };
        bits += SymBits(eobCdf, eobPt);
        if (eob > 1)
        {
            int eb = eobPt - 2, hi = (eob >> eb) & 1;
            bits += BoolBits(coef.EobHiBit[tDim.Ctx * 2 * 9 + eb][0], (uint)hi);
            if (eb > 0) bits += eb;
        }

        int eobBaseTokIdx = tDim.Ctx * 2 * 4;
        int baseTokIdx = tDim.Ctx * 2 * 41;
        int brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21;
        var levels = new byte[stride * 20];

        if (eob != 0)
        {
            int xE = eob & mask, yE = eob >> shift, rcEob = Rc1d(eob, cls, shift, shift2, mask), magEob = Math.Abs(signedLevels[rcEob]);
            uint ctx = (uint)(1 + (eob > (2 << tx2dSzCtx) ? 1 : 0) + (eob > (4 << tx2dSzCtx) ? 1 : 0));
            bits += SymBits(coef.EobBaseTok[eobBaseTokIdx + ctx], Math.Min(magEob, 3) - 1);
            if (Math.Min(magEob, 3) - 1 == 2) bits += HiTokBits(coef.BrTok[brTokIdx + (yE != 0 ? 14 : 7)], magEob);
            levels[xE * stride + yE] = LevelByte(magEob);

            for (int i = eob - 1; i > 0; i--)
            {
                int x = i & mask, y = i >> shift, levelIdx = x * stride + y, mag = Math.Abs(signedLevels[Rc1d(i, cls, shift, shift2, mask)]);
                int loCtx = Av1CoeffDecode.GetLoCtx(levels.AsSpan(levelIdx), cls, out uint hiMag, -1, x, y, stride);
                int tok = Math.Min(mag, 3);
                bits += SymBits(coef.BaseTok[baseTokIdx + loCtx], tok);
                if (tok == 3)
                {
                    hiMag &= 63;
                    int hiCtx = (int)((y > 0 ? 14u : 7u) + (hiMag > 12 ? 6u : (uint)(hiMag + 1) >> 1));
                    bits += HiTokBits(coef.BrTok[brTokIdx + hiCtx], mag);
                }
                levels[levelIdx] = LevelByte(mag);
            }

            int dcMag = Math.Abs(signedLevels[0]);
            int dcCtx = Av1CoeffDecode.GetLoCtx(levels, cls, out uint dcHiMag, -1, 0, 0, stride);
            int dcTokBase = Math.Min(dcMag, 3);
            bits += SymBits(coef.BaseTok[baseTokIdx + dcCtx], dcTokBase);
            if (dcTokBase == 3) { uint mg = dcHiMag & 63; bits += HiTokBits(coef.BrTok[brTokIdx + (int)(mg > 12 ? 6u : (mg + 1) >> 1)], dcMag); }

            if (dcMag != 0) { bits += BoolBits(coef.DcSign[dcSignCtx][0], signedLevels[0] < 0 ? 1u : 0u); if (dcMag >= 15) bits += GolombBits((uint)(dcMag - 15)); }
            for (int i = 1; i <= eob; i++)
            {
                int mag = Math.Abs(signedLevels[Rc1d(i, cls, shift, shift2, mask)]);
                if (mag != 0) { bits += 1; if (mag >= 15) bits += GolombBits((uint)(mag - 15)); }
            }
        }
        else
        {
            int dcMag = Math.Abs(signedLevels[0]);
            bits += SymBits(coef.EobBaseTok[eobBaseTokIdx + 0], Math.Min(dcMag, 3) - 1);
            if (Math.Min(dcMag, 3) - 1 == 2) bits += HiTokBits(coef.BrTok[brTokIdx + 0], dcMag);
            bits += BoolBits(coef.DcSign[dcSignCtx][0], signedLevels[0] < 0 ? 1u : 0u);
            if (dcMag >= 15) bits += GolombBits((uint)(dcMag - 15));
        }
        return bits;
    }

    // ---- Rate estimation: bit cost of coding a coefficient block from the CURRENT CDF probabilities, without
    // encoding or adapting. A faithful mirror of EncodeCoefs used by the encoder's rate-distortion decisions.

    private const double Log2_32768 = 15.0;
    [ThreadStatic] private static byte[]? t_estLevels; // EstimateCoefBits level map (all zero between calls)
    // BitCost[p] = 15 - log2(max(p, 1)) for a Q15 probability p: the same doubles Math.Log2 gives, looked up.
    internal static readonly double[] BitCost = MakeBitCost();
    private static double[] MakeBitCost()
    {
        var t = new double[32769];
        for (int p = 0; p <= 32768; p++) t[p] = Log2_32768 - Math.Log2(Math.Max(p, 1));
        return t;
    }
    /// <summary>Bit cost of coding symbol <paramref name="s"/> from an inverse-CDF's current probabilities.</summary>
    internal static double SymBits(ReadOnlySpan<ushort> icdf, int s)
    {
        int prob = (s == 0 ? 32768 : icdf[s - 1]) - icdf[s];
        return BitCost[Math.Max(prob, 0)];
    }
    internal static double BoolBits(ushort f0, uint val) // f0 = Q15 prob of 0
    {
        int prob = val == 0 ? f0 : 32768 - f0;
        return BitCost[Math.Clamp(prob, 0, 32768)];
    }
    private static double HiTokBits(ReadOnlySpan<ushort> brCdf, int mag)
    {
        double b = 0; int rem = Math.Min(mag, 15) - 3;
        for (int stage = 0; stage < 4; stage++) { int s = Math.Min(rem, 3); b += SymBits(brCdf, s); if (s < 3) break; rem -= 3; }
        return b;
    }
    private static double GolombBits(uint g) { uint x = g + 1; int len = 31 - BitOperations.LeadingZeroCount(x); return 2 * len + 1; }

    /// <summary>Estimated bit cost of coding one 2D transform block's coefficients with the given contexts, from
    /// the current CDF probabilities (no side effects). Mirrors EncodeCoefs symbol-for-symbol.</summary>
    internal static double EstimateCoefBits(Av1CdfCoefContext coef, Av1CdfModeContext modeCdf, int tx, int chroma,
        int yMode, ReadOnlySpan<int> signedLevels, int skipCtx, int dcSignCtx, int txTypeIdx, bool fullSet = false, bool inter = false)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];
        int cdfIdx = tDim.Ctx * 13 + skipCtx;
        int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32);
        int slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
        int tx2dSzCtx = slw + slh;
        ushort[] scan = Av1Tables.Scans[tx];
        int shift = slh + 2, stride = 4 << slh, mask = (4 << slh) - 1;

        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--) if (signedLevels[scan[i]] != 0) { eob = i; break; }

        if (eob < 0) return BoolBits(coef.CoefSkip[cdfIdx][0], 1);
        double bits = BoolBits(coef.CoefSkip[cdfIdx][0], 0);

        if (inter)
        {
            if (chroma == 0 && tDim.Max < (int)Av1TxSize.Tx64x64) bits += BoolBits(modeCdf.TxtpInter3[tDim.Min][0], 1);
        }
        else if (chroma == 0 && tDim.Max + 1 < (int)Av1TxSize.Tx64x64)
            bits += (fullSet && tDim.Min < (int)Av1TxSize.Tx16x16)
                ? SymBits(modeCdf.TxtpIntra1[tDim.Min * 13 + yMode], Intra2ToIntra1(txTypeIdx))
                : SymBits(modeCdf.TxtpIntra2[tDim.Min * 13 + yMode], txTypeIdx);

        // EOB bin + hi-bit + extra bits
        {
            int eobPt = eob <= 1 ? eob : (31 - BitOperations.LeadingZeroCount((uint)eob) - 1) + 2;
            ReadOnlySpan<ushort> eobCdf = tx2dSzCtx switch
            {
                0 => coef.EobBin16[chroma * 2], 1 => coef.EobBin32[chroma * 2], 2 => coef.EobBin64[chroma * 2],
                3 => coef.EobBin128[chroma * 2], 4 => coef.EobBin256[chroma * 2], 5 => coef.EobBin512[chroma],
                _ => coef.EobBin1024[chroma],
            };
            bits += SymBits(eobCdf, eobPt);
            if (eob > 1)
            {
                int eobBin = eobPt - 2, hi = (eob >> eobBin) & 1;
                bits += BoolBits(coef.EobHiBit[tDim.Ctx * 2 * 9 + chroma * 9 + eobBin][0], (uint)hi);
                if (eobBin > 0) bits += eobBin; // equiprobable extra bits
            }
        }

        int eobBaseTokIdx = tDim.Ctx * 2 * 4 + chroma * 4;
        int baseTokIdx = tDim.Ctx * 2 * 41 + chroma * 41;
        int brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21 + chroma * 21;
        var levels = t_estLevels ??= new byte[32 * 34];

        if (eob != 0)
        {
            int rcEob = scan[eob];
            int xE = rcEob >> shift, yE = rcEob & mask, magEob = Math.Abs(signedLevels[rcEob]);
            uint ctx = (uint)(1 + (eob > (2 << tx2dSzCtx) ? 1 : 0) + (eob > (4 << tx2dSzCtx) ? 1 : 0));
            int eobTok = Math.Min(magEob, 3) - 1;
            bits += SymBits(coef.EobBaseTok[eobBaseTokIdx + ctx], eobTok);
            if (eobTok == 2) bits += HiTokBits(coef.BrTok[brTokIdx + (((xE | yE) > 1) ? 14 : 7)], magEob);
            levels[rcEob] = LevelByte(magEob);

            for (int i = eob - 1; i > 0; i--)
            {
                int rcI = scan[i], x = rcI >> shift, y = rcI & mask, mag = Math.Abs(signedLevels[rcI]);
                int loCtx = Av1CoeffDecode.GetLoCtx(levels.AsSpan(rcI), Av1TxClass.TwoD, out uint hiMag, LoCtxOffsetsIdx(tx), x, y, stride);
                int tok = Math.Min(mag, 3);
                bits += SymBits(coef.BaseTok[baseTokIdx + loCtx], tok);
                if (tok == 3)
                {
                    hiMag &= 63;
                    int hiCtx = (int)(((y | x) > 1 ? 14u : 7u) + (hiMag > 12 ? 6u : (uint)(hiMag + 1) >> 1));
                    bits += HiTokBits(coef.BrTok[brTokIdx + hiCtx], mag);
                }
                levels[rcI] = LevelByte(mag);
            }

            int dcMag = Math.Abs(signedLevels[0]);
            int dcTokBase = Math.Min(dcMag, 3);
            bits += SymBits(coef.BaseTok[baseTokIdx + 0], dcTokBase);
            if (dcTokBase == 3)
            {
                uint mg = (uint)(levels[0 * stride + 1] + levels[1 * stride + 0] + levels[1 * stride + 1]) & 63;
                bits += HiTokBits(coef.BrTok[brTokIdx + (int)(mg > 12 ? 6u : (mg + 1) >> 1)], dcMag);
            }

            if (dcMag != 0)
            {
                bits += BoolBits(coef.DcSign[chroma * 3 + dcSignCtx][0], signedLevels[0] < 0 ? 1u : 0u);
                if (dcMag >= 15) bits += GolombBits((uint)(dcMag - 15));
            }
            for (int i = 1; i <= eob; i++)
            {
                int rcI = scan[i];
                levels[rcI] = 0; // leave the shared scratch all-zero
                int mag = Math.Abs(signedLevels[rcI]);
                if (mag != 0) { bits += 1; if (mag >= 15) bits += GolombBits((uint)(mag - 15)); } // AC sign equiprobable
            }
        }
        else
        {
            int dcMag = Math.Abs(signedLevels[0]);
            bits += SymBits(coef.EobBaseTok[eobBaseTokIdx + 0], Math.Min(dcMag, 3) - 1);
            if (Math.Min(dcMag, 3) - 1 == 2) bits += HiTokBits(coef.BrTok[brTokIdx + 0], dcMag);
            bits += BoolBits(coef.DcSign[chroma * 3 + dcSignCtx][0], signedLevels[0] < 0 ? 1u : 0u);
            if (dcMag >= 15) bits += GolombBits((uint)(dcMag - 15));
        }

        return bits;
    }

    /// <summary>Rate-distortion optimized quantization (encoder-only; the decoder is unaffected). Refines the
    /// deadzone-quantized <paramref name="signedLevels"/> in place by weighing coded-rate savings against the
    /// dequant distortion each level carries. Two levers the per-coefficient deadzone cannot see:
    ///  * EOB shrink — dropping a small trailing coefficient moves the end-of-block to a cheaper bin (a global
    ///    rate effect), so we greedily zero trailing coeffs while J = D + λ·R improves.
    ///  * Level-down — lowering a coefficient toward zero trades a little distortion for fewer magnitude bits.
    /// Distortion is measured in the pixel domain: level L on a coefficient with pre-quant float qf and step dq
    /// contributes ((qf-L)·dq)². λ matches the partition RD (pixel SSE vs bits), so the whole pipe is consistent.
    /// qf/signedLevels share the tx's rc indexing; dq is dcDq for rc 0 else acDq.</summary>
    internal static void RdoqOptimize(Av1CdfCoefContext coef, Av1CdfModeContext modeCdf, int tx, int chroma,
        int yMode, int[] signedLevels, double[] qf, int dcDq, int acDq, int skipCtx, int dcSignCtx, int txTypeIdx,
        double lambda)
    {
        if (!Av1StillImageEncoder.UseRdoq) return;   // speed preset: deadzone levels as-is
        ushort[] scan = Av1Tables.Scans[tx];
        int eob = -1;
        for (int i = scan.Length - 1; i >= 0; i--) if (signedLevels[scan[i]] != 0) { eob = i; break; }
        if (eob < 0) return; // all-zero: skip flag already optimal

        double DistOf(int rc, int level)
        {
            double dq = rc == 0 ? dcDq : acDq;
            double e = (qf[rc] - level) * dq;
            return e * e;
        }

        double curBits = EstimateCoefBits(coef, modeCdf, tx, chroma, yMode, signedLevels, skipCtx, dcSignCtx, txTypeIdx);

        // --- EOB shrink: greedily drop trailing nonzero coefficients while it lowers J. ---
        while (eob >= 0)
        {
            int rc = scan[eob];
            int L = signedLevels[rc];
            if (L == 0) { eob--; continue; }
            double dDist = DistOf(rc, 0) - DistOf(rc, L);
            signedLevels[rc] = 0;
            double newBits = EstimateCoefBits(coef, modeCdf, tx, chroma, yMode, signedLevels, skipCtx, dcSignCtx, txTypeIdx);
            if (dDist + lambda * (newBits - curBits) < 0)
            {
                curBits = newBits;                 // accept the drop; move eob to the new last nonzero
                do { eob--; } while (eob >= 0 && signedLevels[scan[eob]] == 0);
            }
            else { signedLevels[rc] = L; break; }  // no further trailing drop helps
        }
        if (eob < 0) return;

        // --- Level-down: nudge each remaining coefficient one step toward zero when J improves. ---
        // With eob >= 1 the trial costs come from RdoqCost, which re-derives only the terms a one-coefficient change
        // touches and re-adds every term in EstimateCoefBits' order (identical doubles); a trial that moves the eob
        // (the last coefficient going to zero) uses the full estimate.
        RdoqCost? inc = null;
        if (eob >= 1) { inc = t_rdoqCost ??= new RdoqCost(); inc.Build(coef, modeCdf, tx, chroma, yMode, signedLevels, skipCtx, dcSignCtx, txTypeIdx, eob); }
        for (int i = 0; i <= eob; i++)
        {
            int rc = scan[i];
            int L = signedLevels[rc];
            if (L == 0) continue;
            int sign = L < 0 ? -1 : 1, mag = L < 0 ? -L : L;
            int cand = sign * (mag - 1);           // mag-1 (may be 0 for interior coeffs)
            double dDist = DistOf(rc, cand) - DistOf(rc, L);
            bool movesEob = inc == null || (i == eob && cand == 0);
            signedLevels[rc] = cand;
            double newBits;
            if (movesEob) newBits = EstimateCoefBits(coef, modeCdf, tx, chroma, yMode, signedLevels, skipCtx, dcSignCtx, txTypeIdx);
            else
            {
                inc!.Set(i, cand);
                newBits = inc.Sum();
                if (RdoqCheck)
                {
                    double full = EstimateCoefBits(coef, modeCdf, tx, chroma, yMode, signedLevels, skipCtx, dcSignCtx, txTypeIdx);
                    if (Math.Abs(full - newBits) > 1e-6) throw new InvalidOperationException($"RDOQ incremental cost {newBits:R} != full {full:R} (tx {tx} i {i} eob {eob})");
                }
            }
            if (dDist + lambda * (newBits - curBits) < 0) curBits = newBits;
            else { signedLevels[rc] = L; if (!movesEob) inc!.Set(i, L); }
        }
    }

    internal static readonly bool RdoqCheck = Environment.GetEnvironmentVariable("AV1_RDOQCHECK") == "1";
    [ThreadStatic] private static RdoqCost? t_rdoqCost;
    private static readonly short[]?[] InvScans = new short[]?[Av1Tables.Scans.Length];

    /// <summary>EstimateCoefBits for a 2D block with eob >= 1, kept as its individual additive terms so a change of
    /// one coefficient below / at the eob (not to zero at the eob) re-derives only its own terms and those of the
    /// positions whose context reads it, and a running total follows every term change (Sum is O(1); it can differ
    /// from the full estimate in the last bits of the double).</summary>
    private sealed class RdoqCost
    {
        private readonly double[] tA = new double[1024], tB = new double[1024], sA = new double[1024], sB = new double[1024];
        private readonly double[] pre = new double[5];
        private double total;
        private void W(double[] a, int idx, double v) { total += v - a[idx]; a[idx] = v; }
        private readonly byte[] lv = new byte[32 * 34];   // LevelByte of every coefficient (all positions, zero past eob)
        private short[] inv = null!;
        private int[] sl = null!;
        private ushort[] scan = null!;
        private ushort[]? prevScan;
        private int prevEob;
        private Av1CdfCoefContext coef = null!;
        private int eob, shift, mask, stride, lcIdx, baseTokIdx, brTokIdx, dcSignIdx, eobBaseTokIdx, tx2dSzCtx, n;

        public void Build(Av1CdfCoefContext coef, Av1CdfModeContext modeCdf, int tx, int chroma, int yMode,
            int[] signedLevels, int skipCtx, int dcSignCtx, int txTypeIdx, int eob)
        {
            ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];
            this.coef = coef; sl = signedLevels; this.eob = eob;
            scan = Av1Tables.Scans[tx];
            int slw = Math.Min((int)tDim.Lw, (int)Av1TxSize.Tx32x32), slh = Math.Min((int)tDim.Lh, (int)Av1TxSize.Tx32x32);
            tx2dSzCtx = slw + slh; shift = slh + 2; stride = 4 << slh; mask = stride - 1;
            n = stride * ((4 << slw) + 2);
            inv = InvScans[tx] ??= MakeInv(scan, n);
            lcIdx = LoCtxOffsetsIdx(tx);
            baseTokIdx = tDim.Ctx * 2 * 41 + chroma * 41;
            brTokIdx = Math.Min((int)tDim.Ctx, 3) * 2 * 21 + chroma * 21;
            eobBaseTokIdx = tDim.Ctx * 2 * 4 + chroma * 4;
            dcSignIdx = chroma * 3 + dcSignCtx;

            // prefix: skip, tx type, eob bin, eob hi bit, eob extra bits (eob is fixed for this evaluator)
            int cdfIdx = tDim.Ctx * 13 + skipCtx;
            pre[0] = BoolBits(coef.CoefSkip[cdfIdx][0], 0);
            pre[1] = chroma == 0 && tDim.Max + 1 < (int)Av1TxSize.Tx64x64 ? SymBits(modeCdf.TxtpIntra2[tDim.Min * 13 + yMode], txTypeIdx) : 0;
            int eobPt = eob <= 1 ? eob : (31 - BitOperations.LeadingZeroCount((uint)eob) - 1) + 2;
            ReadOnlySpan<ushort> eobCdf = tx2dSzCtx switch
            {
                0 => coef.EobBin16[chroma * 2], 1 => coef.EobBin32[chroma * 2], 2 => coef.EobBin64[chroma * 2],
                3 => coef.EobBin128[chroma * 2], 4 => coef.EobBin256[chroma * 2], 5 => coef.EobBin512[chroma],
                _ => coef.EobBin1024[chroma],
            };
            pre[2] = SymBits(eobCdf, eobPt);
            pre[3] = pre[4] = 0;
            if (eob > 1)
            {
                int eobBin = eobPt - 2, hi = (eob >> eobBin) & 1;
                pre[3] = BoolBits(coef.EobHiBit[tDim.Ctx * 2 * 9 + chroma * 9 + eobBin][0], (uint)hi);
                if (eobBin > 0) pre[4] = eobBin;
            }

            // only the previous build's positions (scan[0..eob]) can be set
            if (prevScan != null) for (int i = 0; i <= prevEob; i++) lv[prevScan[i]] = 0;
            prevScan = scan; prevEob = eob;
            Array.Clear(tA, 0, eob + 1); Array.Clear(tB, 0, eob + 1); Array.Clear(sA, 0, eob + 1); Array.Clear(sB, 0, eob + 1);
            total = pre[0] + pre[1] + pre[2] + pre[3] + pre[4];
            for (int i = 0; i <= eob; i++) lv[scan[i]] = LevelByte(Math.Abs(sl[scan[i]]));
            EobTerm();
            for (int i = eob - 1; i > 0; i--) BaseTerm(i);
            DcTerm();
            for (int i = 0; i <= eob; i++) SignTerm(i);
        }

        private static short[] MakeInv(ushort[] scan, int n)
        {
            var a = new short[n];
            Array.Fill(a, short.MaxValue);
            for (int i = 0; i < scan.Length; i++) a[scan[i]] = (short)i;
            return a;
        }

        /// <summary>Sets the coefficient at scan index i (i &lt; eob, or i == eob with a nonzero value) and updates the
        /// terms that depend on it.</summary>
        public void Set(int i, int value)
        {
            int rc = scan[i];
            sl[rc] = value;
            lv[rc] = LevelByte(Math.Abs(value));
            if (i == eob) EobTerm(); else if (i > 0) BaseTerm(i); else DcTerm();
            SignTerm(i);
            // positions whose context reads levels[rc] (GetLoCtx 2D neighbours +1, +2, +stride, +stride+1, +2*stride)
            // and were coded after it (lower scan index)
            Dep(rc - 1, i); Dep(rc - 2, i); Dep(rc - stride, i); Dep(rc - stride - 1, i); Dep(rc - 2 * stride, i);
        }

        private void Dep(int r, int i)
        {
            if (r < 0) return;
            int j = inv[r];
            if (j >= i) return;
            if (j > 0) BaseTerm(j); else DcTerm();
        }

        // the level byte position j's context sees at index r: set only if r was coded before j (higher scan index)
        private uint Lv(int r, int j) => inv[r] > j ? lv[r] : 0u;

        private void EobTerm()
        {
            int rcEob = scan[eob];
            int xE = rcEob >> shift, yE = rcEob & mask, magEob = Math.Abs(sl[rcEob]);
            uint ctx = (uint)(1 + (eob > (2 << tx2dSzCtx) ? 1 : 0) + (eob > (4 << tx2dSzCtx) ? 1 : 0));
            int eobTok = Math.Min(magEob, 3) - 1;
            W(tA, eob, SymBits(coef.EobBaseTok[eobBaseTokIdx + ctx], eobTok));
            W(tB, eob, eobTok == 2 ? HiTokBits(coef.BrTok[brTokIdx + (((xE | yE) > 1) ? 14 : 7)], magEob) : 0);
        }

        private void BaseTerm(int j)
        {
            int rcI = scan[j], x = rcI >> shift, y = rcI & mask, mag = Math.Abs(sl[rcI]);
            uint m = Lv(rcI + 1, j) + Lv(rcI + stride, j);
            m += Lv(rcI + stride + 1, j);
            uint hiMag = m;
            m += Lv(rcI + 2, j) + Lv(rcI + 2 * stride, j);
            int loCtx = Av1Tables.LoCtxOffsets[lcIdx, Math.Min(y, 4), Math.Min(x, 4)] + (m > 512 ? 4 : (int)((m + 64) >> 7));
            int tok = Math.Min(mag, 3);
            W(tA, j, SymBits(coef.BaseTok[baseTokIdx + loCtx], tok));
            if (tok == 3)
            {
                hiMag &= 63;
                int hiCtx = (int)(((y | x) > 1 ? 14u : 7u) + (hiMag > 12 ? 6u : (uint)(hiMag + 1) >> 1));
                W(tB, j, HiTokBits(coef.BrTok[brTokIdx + hiCtx], mag));
            }
            else W(tB, j, 0);
        }

        private void DcTerm()
        {
            int dcMag = Math.Abs(sl[0]);
            int dcTokBase = Math.Min(dcMag, 3);
            W(tA, 0, SymBits(coef.BaseTok[baseTokIdx + 0], dcTokBase));
            if (dcTokBase == 3)
            {
                uint mg = (uint)(lv[1] + lv[stride] + lv[stride + 1]) & 63;
                W(tB, 0, HiTokBits(coef.BrTok[brTokIdx + (int)(mg > 12 ? 6u : (mg + 1) >> 1)], dcMag));
            }
            else W(tB, 0, 0);
        }

        private void SignTerm(int i)
        {
            int v = sl[scan[i]], mag = Math.Abs(v);
            if (i == 0)
            {
                W(sA, 0, mag != 0 ? BoolBits(coef.DcSign[dcSignIdx][0], v < 0 ? 1u : 0u) : 0);
                W(sB, 0, mag >= 15 ? GolombBits((uint)(mag - 15)) : 0);
            }
            else
            {
                W(sA, i, mag != 0 ? 1 : 0);
                W(sB, i, mag >= 15 ? GolombBits((uint)(mag - 15)) : 0);
            }
        }

        public double Sum() => total;
    }

    /// <summary>Level byte stored for GetLoCtx neighbour magnitude, matching DecodeCoefs: mag 1..2 → mag*0x41;
    /// mag ≥ 3 → min(mag,15) + (3&lt;&lt;6).</summary>
    private static byte LevelByte(int mag)
    {
        if (mag == 0)
        {
            return 0;
        }

        return mag <= 2 ? (byte)(mag * 0x41) : (byte)(Math.Min(mag, 15) + (3 << 6));
    }

    private static int LoCtxOffsetsIdx(int tx)
    {
        int nonsquareTx = tx >= (int)Av1RectTxSize.Rtx4x8 ? 1 : 0;
        return nonsquareTx + (tx & nonsquareTx);
    }

    /// <summary>Encodes the EOB bin symbol (size-dependent CDF) plus the hi-bit and extra bits for eob ≥ 2.
    /// Inverse of DecodeEobBin + the reconstruction block in DecodeCoefs.</summary>
    private static void EncodeEob(Av1MsacWriter w, Av1CdfCoefContext coef, int chroma, int tx2dSzCtx, int txCtx, int eob, int is1d = 0)
    {
        // eob (scan index of last nonzero) → eob_pt bin.
        int eobPt;
        if (eob <= 1)
        {
            eobPt = eob; // 0 or 1
        }
        else
        {
            int eobBin = 31 - BitOperations.LeadingZeroCount((uint)eob) - 1; // floor(log2(eob)) - 1
            eobPt = eobBin + 2;
        }

        switch (tx2dSzCtx)
        {
            case 0: w.EncodeSymbolAdapt(coef.EobBin16[chroma * 2 + is1d], eobPt, 4 + 0); break;
            case 1: w.EncodeSymbolAdapt(coef.EobBin32[chroma * 2 + is1d], eobPt, 4 + 1); break;
            case 2: w.EncodeSymbolAdapt(coef.EobBin64[chroma * 2 + is1d], eobPt, 4 + 2); break;
            case 3: w.EncodeSymbolAdapt(coef.EobBin128[chroma * 2 + is1d], eobPt, 4 + 3); break;
            case 4: w.EncodeSymbolAdapt(coef.EobBin256[chroma * 2 + is1d], eobPt, 4 + 4); break;
            case 5: w.EncodeSymbolAdapt(coef.EobBin512[chroma], eobPt, 4 + 5); break;
            default: w.EncodeSymbolAdapt(coef.EobBin1024[chroma], eobPt, 4 + 6); break;
        }

        if (eob > 1)
        {
            int eobBin = eobPt - 2;
            int hi = (eob >> eobBin) & 1;
            // EobHiBit CDF indexing matches DecodeCoefs exactly: [tDim.Ctx * 2 * 9 + chroma * 9 + eobBin].
            Span<ushort> hiCdf = coef.EobHiBit[txCtx * 2 * 9 + chroma * 9 + eobBin];
            w.EncodeBoolAdapt(hiCdf, (uint)hi);
            if (eobBin > 0)
            {
                uint extra = (uint)(eob & ((1 << eobBin) - 1));
                w.EncodeLiteral(extra, eobBin);
            }
        }
    }

    /// <summary>HiTok base-range extension for magnitudes ≥ 3, inverse of Av1Msac.DecodeHiTok (max 4 symbols).</summary>
    private static void EncodeHiTok(Av1MsacWriter w, Span<ushort> brCdf, int mag)
    {
        int rem = Math.Min(mag, 15) - 3;
        for (int stage = 0; stage < 4; stage++)
        {
            int s = Math.Min(rem, 3);
            w.EncodeSymbolAdapt(brCdf, s, 3);
            if (s < 3)
            {
                break;
            }

            rem -= 3;
        }
    }

    /// <summary>Encodes a coefficient's sign then, for |level| ≥ 15, the Golomb tail (level-15). The DC sign is
    /// adaptive (DcSign CDF prob f); AC signs are equiprobable.</summary>
    private static void EncodeSignAndGolomb(Av1MsacWriter w, Span<ushort> dcSignCdf, bool adaptSign, int signedLevel, int mag)
    {
        uint sign = signedLevel < 0 ? 1u : 0u;
        if (adaptSign)
        {
            w.EncodeBoolAdapt(dcSignCdf, sign);
        }
        else
        {
            w.EncodeBoolEqui(sign);
        }

        if (mag >= 15)
        {
            EncodeGolomb(w, (uint)(mag - 15));
        }
    }

    /// <summary>Encodes the luma palette COLOURS for a block — the exact inverse of DecodeLumaPalette. Given the
    /// final sorted (ascending, distinct) palette in <paramref name="colors"/>, it rebuilds the neighbour cache
    /// the decoder will build, signals which cache entries are reused (equi bits), delta-codes the remaining
    /// "new" colours, and updates the palette-prediction neighbour state (PalPrevY/PalPrevSz) exactly as the
    /// decoder does so the next block's cache matches. Verified by round-tripping through DecodeLumaPalette.</summary>
    public static void EncodeLumaPaletteColors(Av1MsacWriter w, Av1TaskContext t, ref Av1Block b,
        ushort[] colors, int palSz, int bx4, int by4, int bitDepth)
    {
        // Rebuild the same left/above neighbour cache the decoder reads from the task context, then delegate to
        // the core; finally update the task-context prediction state (used by the standalone round-trip test).
        int bi4 = by4 < 32 ? by4 : 31, bj4 = bx4 < 32 ? bx4 : 31;
        int leftPalSz = t.Left.PalSz[bi4]; if (leftPalSz > 8) leftPalSz = 0;
        int abovePalSz = ((by4 & 15) != 0) ? t.Above.PalSz[bj4] : 0; if (abovePalSz > 8) abovePalSz = 0;
        Span<ushort> lCol = stackalloc ushort[8]; Span<ushort> aCol = stackalloc ushort[8];
        for (int ci = 0; ci < leftPalSz; ci++) lCol[ci] = t.PalPrevY[1, bi4, ci];
        for (int ci = 0; ci < abovePalSz; ci++) aCol[ci] = t.PalPrevY[0, bj4, ci];

        EncodeLumaPaletteColorsCore(w, colors, palSz, lCol, leftPalSz, aCol, abovePalSz, bitDepth);

        int bw = Av1Tables.BlockDimensions[b.BlockSize, 0], bh = Av1Tables.BlockDimensions[b.BlockSize, 1];
        for (int ci = 0; ci < palSz; ci++) t.PalColorsY[ci] = colors[ci];
        for (int dx = 0; dx < bw && bj4 + dx < 32; dx++)
        {
            for (int ci = 0; ci < palSz; ci++) t.PalPrevY[0, bj4 + dx, ci] = colors[ci];
            t.PalPrevSz[0, bj4 + dx] = (byte)palSz;
        }
        for (int dy = 0; dy < bh && bi4 + dy < 32; dy++)
        {
            for (int ci = 0; ci < palSz; ci++) t.PalPrevY[1, bi4 + dy, ci] = colors[ci];
            t.PalPrevSz[1, bi4 + dy] = (byte)palSz;
        }
    }

    /// <summary>Core of the luma palette colour coder: given the block's palette and its already-resolved left and
    /// above neighbour palettes (colours + sizes; the caller applies the 64px-row above-cache gating), it merges
    /// the sorted/dedup cache, emits cache-selection bits, and delta-codes the new colours. Neighbour-state
    /// storage is the caller's responsibility (task-context test vs. encoder block loop store differently).</summary>
    public static void EncodeLumaPaletteColorsCore(Av1MsacWriter w, ushort[] colors, int palSz,
        ReadOnlySpan<ushort> leftColors, int leftPalSz, ReadOnlySpan<ushort> aboveColors, int abovePalSz, int bitDepth)
    {
        if (palSz < 2 || palSz > 8) throw new ArgumentOutOfRangeException(nameof(palSz));
        int bpc = bitDepth, maxVal = (1 << bpc) - 1;

        Span<ushort> lCache = stackalloc ushort[8]; int lCacheSz = 0;
        for (int ci = 0; ci < leftPalSz && ci < 8; ci++) lCache[lCacheSz++] = leftColors[ci];
        Span<ushort> aCache = stackalloc ushort[8]; int aCacheSz = 0;
        for (int ci = 0; ci < abovePalSz && ci < 8; ci++) aCache[aCacheSz++] = aboveColors[ci];

        Span<ushort> cache = stackalloc ushort[16]; int nCache = 0;
        int li = 0, ai = 0;
        while (li < lCacheSz && ai < aCacheSz)
        {
            if (lCache[li] < aCache[ai])
            { if (nCache == 0 || cache[nCache - 1] != lCache[li]) cache[nCache++] = lCache[li]; li++; }
            else
            { if (aCache[ai] == lCache[li]) li++; if (nCache == 0 || cache[nCache - 1] != aCache[ai]) cache[nCache++] = aCache[ai]; ai++; }
        }
        while (li < lCacheSz) { if (nCache == 0 || cache[nCache - 1] != lCache[li]) cache[nCache++] = lCache[li]; li++; }
        while (ai < aCacheSz) { if (nCache == 0 || cache[nCache - 1] != aCache[ai]) cache[nCache++] = aCache[ai]; ai++; }

        // --- Cache selection: a bit per cache entry (until palSz reached), set iff that colour is in the palette.
        int nUsedCache = 0;
        for (int ci = 0; ci < nCache && nUsedCache < palSz; ci++)
        {
            bool inPal = Contains(colors, palSz, cache[ci]);
            w.EncodeBoolEqui(inPal ? 1u : 0u);
            if (inPal) nUsedCache++;
        }

        // --- New colours = palette colours not taken from the cache, in ascending order. ---
        Span<ushort> newPal = stackalloc ushort[8]; int nNew = 0;
        for (int ci = 0; ci < palSz; ci++)
            if (!InCache(cache, nCache, colors[ci], nUsedCache > 0)) newPal[nNew++] = colors[ci];
        // (Colours present in the cache were signalled above; everything else is coded here.)

        if (nNew > 0)
        {
            w.EncodeLiteral(newPal[0], bpc);
            if (nNew > 1)
            {
                // Pick the smallest 2-bit selector b2 (bits = bpc-3+b2) for which every delta fits its narrowed
                // width, matching the decoder's per-entry narrowing. b2=3 (bits=bpc) always fits.
                int b2 = ChooseDeltaBits(newPal, nNew, bpc, maxVal);
                w.EncodeLiteral((uint)b2, 2);
                int bits = bpc - 3 + b2, prev = newPal[0];
                for (int i = 1; i < nNew; i++)
                {
                    int delta = newPal[i] - prev - 1;
                    w.EncodeLiteral((uint)delta, bits);
                    prev = Math.Min(prev + delta + 1, maxVal);
                    if (prev + 1 >= maxVal) break;   // decoder fills the rest with maxVal (no more distinct colours)
                    int ulog2 = 0, tmp = maxVal - prev - 1;
                    while (tmp > 1) { tmp >>= 1; ulog2++; }
                    bits = Math.Min(bits, 1 + ulog2);
                }
            }
        }

    }

    /// <summary>Encodes a palette colour-index map — the exact inverse of DecodePaletteIndices. `idxMap` holds the
    /// per-pixel palette indices (row-major, stride = blockWidth4*4, same buffer layout the decoder writes). The
    /// top-left is coded with a uniform distribution; every other pixel is coded on the wavefront diagonals: its
    /// neighbour context selects a colour ORDER (BuildColorOrder, shared with the decoder) and we emit the RANK of
    /// the pixel's index within that order through the adaptive ColorMap CDF. The CDF adapts in lockstep with the
    /// decoder because both walk the pixels in identical order with identical symbols.</summary>
    public static void EncodePaletteIndices(Av1MsacWriter w, Av1CdfModeContext modeCdf, byte[] idxMap,
        int palSize, int width, int height, int blockWidth4, int blockHeight4, bool isLuma)
    {
        if (palSize <= 1) return;
        int stride = blockWidth4 * 4;
        int plane = isLuma ? 0 : 1;

        w.EncodeUniform(idxMap[0], (uint)palSize);   // top-left, uniform

        Span<byte> order = stackalloc byte[8];
        int maxDiag = 4 * (blockWidth4 + blockHeight4) - 1;
        for (int diag = 1; diag < maxDiag; diag++)
        {
            int first = Math.Min(diag, width - 1);
            int last = Math.Max(0, diag - height + 1);
            for (int x = first; x >= last; x--)
            {
                int y = diag - x;
                int idx = y * stride + x;
                int l = x > 0 ? idxMap[y * stride + x - 1] : 0xFF;
                int tt = y > 0 ? idxMap[(y - 1) * stride + x] : 0xFF;
                int tl = (x > 0 && y > 0) ? idxMap[(y - 1) * stride + x - 1] : 0xFF;

                int ctx = Av1CoeffDecode.BuildColorOrder(order, palSize, l, tt, tl);
                int target = idxMap[idx];
                int colorIdx = 0;
                while (colorIdx < palSize && order[colorIdx] != target) colorIdx++;
                // order is a full permutation of [0,palSize), so target is always found.
                int cdfIdx = plane * 35 + (palSize - 2) * 5 + ctx;
                w.EncodeSymbolAdapt(modeCdf.ColorMap[cdfIdx], colorIdx, palSize - 1);
            }
        }
    }

    /// <summary>Chroma palette colours — the inverse of Av1CoeffDecode.DecodeChromaPalette. U (ascending) uses the same
    /// neighbour cache as luma but its new colours are coded as non-negative deltas (no +1); V is coded either as
    /// wrapping signed deltas (after a selector bit and width) or raw, whichever is fewer bits.</summary>
    public static void EncodeChromaPaletteColors(Av1MsacWriter w, ushort[] u, ushort[] v, int palSz,
        ReadOnlySpan<ushort> leftU, int leftPalSz, ReadOnlySpan<ushort> aboveU, int abovePalSz, int bitDepth)
    {
        int bpc = bitDepth, maxVal = (1 << bpc) - 1;
        Span<ushort> cache = stackalloc ushort[16];
        int nCache = MergeCache(cache, leftU, leftPalSz, aboveU, abovePalSz);
        // U may repeat a value (pairs sharing U): a selected cache entry covers ONE palette entry; the rest are new.
        Span<bool> taken = stackalloc bool[8];
        int nUsed = 0;
        for (int ci = 0; ci < nCache && nUsed < palSz; ci++)
        {
            int k = TakeOne(u, palSz, cache[ci], taken);
            w.EncodeBoolEqui(k >= 0 ? 1u : 0u);
            if (k >= 0) nUsed++;
        }
        Span<ushort> newPal = stackalloc ushort[8]; int nNew = 0;
        for (int ci = 0; ci < palSz; ci++)
            if (!taken[ci]) newPal[nNew++] = u[ci];
        if (nNew > 0)
        {
            w.EncodeLiteral(newPal[0], bpc);
            if (nNew > 1)
            {
                int b2 = ChooseDeltaBitsU(newPal, nNew, bpc, maxVal);
                w.EncodeLiteral((uint)b2, 2);
                int bits = bpc - 3 + b2, prev = newPal[0];
                for (int i = 1; i < nNew; i++)
                {
                    int delta = newPal[i] - prev;
                    w.EncodeLiteral((uint)delta, bits);
                    prev = Math.Min(prev + delta, maxVal);
                    if (prev >= maxVal) break;
                    int ulog2 = 0, tmp = maxVal - prev;
                    while (tmp > 1) { tmp >>= 1; ulog2++; }
                    bits = Math.Min(bits, 1 + ulog2);
                }
            }
        }

        // V: wrapping deltas when cheaper than raw.
        var (useDelta, vb2) = ChooseVCoding(v, palSz, bpc);
        w.EncodeBoolEqui(useDelta ? 1u : 0u);
        if (useDelta)
        {
            w.EncodeLiteral((uint)vb2, 2);
            int bits = bpc - 4 + vb2;
            w.EncodeLiteral(v[0], bpc);
            int prev = v[0];
            for (int i = 1; i < palSz; i++)
            {
                int d = WrapDelta(v[i] - prev, bpc);
                w.EncodeLiteral((uint)Math.Abs(d), bits);
                if (d != 0) w.EncodeBoolEqui(d < 0 ? 1u : 0u);
                prev = (prev + d) & maxVal;
            }
        }
        else
            for (int i = 0; i < palSz; i++) w.EncodeLiteral(v[i], bpc);
    }

    /// <summary>Bits of <see cref="EncodeChromaPaletteColors"/> / <see cref="EncodeLumaPaletteColorsCore"/> (all
    /// equiprobable), for the palette RD.</summary>
    public static int ChromaPaletteColorBits(ushort[] u, ushort[] v, int palSz, ReadOnlySpan<ushort> leftU, int leftPalSz,
        ReadOnlySpan<ushort> aboveU, int abovePalSz, int bitDepth)
    {
        int bpc = bitDepth, maxVal = (1 << bpc) - 1, bits = 0;
        Span<ushort> cache = stackalloc ushort[16];
        int nCache = MergeCache(cache, leftU, leftPalSz, aboveU, abovePalSz);
        Span<bool> taken = stackalloc bool[8];
        int nUsed = 0;
        for (int ci = 0; ci < nCache && nUsed < palSz; ci++) { bits++; if (TakeOne(u, palSz, cache[ci], taken) >= 0) nUsed++; }
        Span<ushort> newPal = stackalloc ushort[8]; int nNew = 0;
        for (int ci = 0; ci < palSz; ci++) if (!taken[ci]) newPal[nNew++] = u[ci];
        if (nNew > 0)
        {
            bits += bpc;
            if (nNew > 1)
            {
                int b2 = ChooseDeltaBitsU(newPal, nNew, bpc, maxVal);
                bits += 2;
                int bw = bpc - 3 + b2, prev = newPal[0];
                for (int i = 1; i < nNew; i++)
                {
                    int delta = newPal[i] - prev;
                    bits += bw;
                    prev = Math.Min(prev + delta, maxVal);
                    if (prev >= maxVal) break;
                    int ulog2 = 0, tmp = maxVal - prev;
                    while (tmp > 1) { tmp >>= 1; ulog2++; }
                    bw = Math.Min(bw, 1 + ulog2);
                }
            }
        }
        var (useDelta, vb2) = ChooseVCoding(v, palSz, bpc);
        bits += 1 + (useDelta ? VDeltaBits(v, palSz, bpc, vb2) : palSz * bpc);
        return bits;
    }

    public static int LumaPaletteColorBits(ushort[] colors, int palSz, ReadOnlySpan<ushort> leftColors, int leftPalSz,
        ReadOnlySpan<ushort> aboveColors, int abovePalSz, int bitDepth)
    {
        int bpc = bitDepth, maxVal = (1 << bpc) - 1, bits = 0;
        Span<ushort> cache = stackalloc ushort[16];
        int nCache = MergeCache(cache, leftColors, leftPalSz, aboveColors, abovePalSz);
        int nUsed = 0;
        for (int ci = 0; ci < nCache && nUsed < palSz; ci++) { bits++; if (Contains(colors, palSz, cache[ci])) nUsed++; }
        Span<ushort> newPal = stackalloc ushort[8]; int nNew = 0;
        for (int ci = 0; ci < palSz; ci++) if (!InCache(cache, nCache, colors[ci], nUsed > 0)) newPal[nNew++] = colors[ci];
        if (nNew > 0)
        {
            bits += bpc;
            if (nNew > 1)
            {
                int b2 = ChooseDeltaBits(newPal, nNew, bpc, maxVal);
                bits += 2;
                int bw = bpc - 3 + b2, prev = newPal[0];
                for (int i = 1; i < nNew; i++)
                {
                    int delta = newPal[i] - prev - 1;
                    bits += bw;
                    prev = Math.Min(prev + delta + 1, maxVal);
                    if (prev + 1 >= maxVal) break;
                    int ulog2 = 0, tmp = maxVal - prev - 1;
                    while (tmp > 1) { tmp >>= 1; ulog2++; }
                    bw = Math.Min(bw, 1 + ulog2);
                }
            }
        }
        return bits;
    }

    // Marks the first not-yet-taken palette entry equal to v; its index, or -1.
    private static int TakeOne(ushort[] colors, int n, ushort v, Span<bool> taken)
    {
        for (int i = 0; i < n; i++) if (!taken[i] && colors[i] == v) { taken[i] = true; return i; }
        return -1;
    }

    // The decoder's palette cache: left and above colours merged ascending, duplicates removed.
    private static int MergeCache(Span<ushort> cache, ReadOnlySpan<ushort> l, int lSz, ReadOnlySpan<ushort> a, int aSz)
    {
        lSz = Math.Min(lSz, 8); aSz = Math.Min(aSz, 8);
        int n = 0, li = 0, ai = 0;
        while (li < lSz && ai < aSz)
        {
            if (l[li] < a[ai]) { if (n == 0 || cache[n - 1] != l[li]) cache[n++] = l[li]; li++; }
            else { if (a[ai] == l[li]) li++; if (n == 0 || cache[n - 1] != a[ai]) cache[n++] = a[ai]; ai++; }
        }
        while (li < lSz) { if (n == 0 || cache[n - 1] != l[li]) cache[n++] = l[li]; li++; }
        while (ai < aSz) { if (n == 0 || cache[n - 1] != a[ai]) cache[n++] = a[ai]; ai++; }
        return n;
    }

    // U deltas are >= 0 (no +1) and narrow to maxVal - prev.
    private static int ChooseDeltaBitsU(ReadOnlySpan<ushort> newPal, int nNew, int bpc, int maxVal)
    {
        for (int b2 = 0; b2 <= 3; b2++)
        {
            int bits = bpc - 3 + b2, prev = newPal[0]; bool ok = true;
            for (int i = 1; i < nNew; i++)
            {
                int delta = newPal[i] - prev;
                if (bits < 0 || delta >= (1 << bits)) { ok = false; break; }
                prev = Math.Min(prev + delta, maxVal);
                if (prev >= maxVal) break;
                int ulog2 = 0, tmp = maxVal - prev;
                while (tmp > 1) { tmp >>= 1; ulog2++; }
                bits = Math.Min(bits, 1 + ulog2);
            }
            if (ok) return b2;
        }
        return 3;
    }

    // V delta in (-2^(bpc-1), 2^(bpc-1)]: the decoder wraps (prev + delta) & maxVal.
    private static int WrapDelta(int d, int bpc)
    {
        int m = 1 << bpc;
        d = ((d % m) + m) % m;
        return d > m / 2 ? d - m : d;
    }

    private static int VDeltaBits(ushort[] v, int palSz, int bpc, int b2)
    {
        int bits = bpc - 4 + b2, total = 2 + bpc, prev = v[0], maxVal = (1 << bpc) - 1;
        for (int i = 1; i < palSz; i++)
        {
            int d = WrapDelta(v[i] - prev, bpc);
            total += bits + (d != 0 ? 1 : 0);
            prev = (prev + d) & maxVal;
        }
        return total;
    }

    // Delta coding of V (smallest width that fits every |delta|) when it beats raw.
    private static (bool Delta, int B2) ChooseVCoding(ushort[] v, int palSz, int bpc)
    {
        int maxAbs = 0, prev = v[0];
        for (int i = 1; i < palSz; i++) { int d = WrapDelta(v[i] - prev, bpc); maxAbs = Math.Max(maxAbs, Math.Abs(d)); prev = v[i]; }
        for (int b2 = 0; b2 <= 3; b2++)
        {
            int bits = bpc - 4 + b2;
            if (bits < 0 || maxAbs >= (1 << bits)) continue;
            return (VDeltaBits(v, palSz, bpc, b2) < palSz * bpc, b2);
        }
        return (false, 0);
    }

    private static bool Contains(ushort[] colors, int n, ushort v)
    { for (int i = 0; i < n; i++) if (colors[i] == v) return true; return false; }

    // A palette colour was signalled via the cache iff it equals some cache entry (cache holds distinct values).
    private static bool InCache(ReadOnlySpan<ushort> cache, int nCache, ushort v, bool anyUsed)
    { for (int i = 0; i < nCache; i++) if (cache[i] == v) return true; return false; }

    // Smallest 2-bit delta-width selector for which all new-colour deltas fit their (narrowed) widths.
    private static int ChooseDeltaBits(ReadOnlySpan<ushort> newPal, int nNew, int bpc, int maxVal)
    {
        for (int b2 = 0; b2 <= 3; b2++)
        {
            int bits = bpc - 3 + b2, prev = newPal[0]; bool ok = true;
            for (int i = 1; i < nNew; i++)
            {
                int delta = newPal[i] - prev - 1;
                if (bits < 0 || delta >= (1 << bits)) { ok = false; break; }
                prev = Math.Min(prev + delta + 1, maxVal);
                if (prev + 1 >= maxVal) break;
                int ulog2 = 0, tmp = maxVal - prev - 1;
                while (tmp > 1) { tmp >>= 1; ulog2++; }
                bits = Math.Min(bits, 1 + ulog2);
            }
            if (ok) return b2;
        }
        return 3;
    }

    /// <summary>Exp-Golomb, inverse of Av1CoeffDecode.ReadGolomb: for value g, emit len zeros, a one, then the
    /// low len bits of (g+1) MSB-first, where len = floor(log2(g+1)).</summary>
    private static void EncodeGolomb(Av1MsacWriter w, uint g)
    {
        uint x = g + 1;
        int len = 31 - BitOperations.LeadingZeroCount(x);
        for (int i = 0; i < len; i++)
        {
            w.EncodeBoolEqui(0);
        }

        w.EncodeBoolEqui(1);
        for (int i = len - 1; i >= 0; i--)
        {
            w.EncodeBoolEqui((x >> i) & 1u);
        }
    }
}
