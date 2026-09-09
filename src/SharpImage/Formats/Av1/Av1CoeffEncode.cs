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
        int dcSignCtx = 0)
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
            w.EncodeBool(1, coef.CoefSkip[cdfIdx][0]);
            return;
        }

        w.EncodeBool(0, coef.CoefSkip[cdfIdx][0]);

        // --- Transform type ---
        // The decoder codes a tx-type symbol for intra luma when tDim.Max + intra < TX_64X64 (i.e. TX_16X16 and
        // smaller) and segQIdx != 0; TX_32X32/TX_64X64 imply DctDct with no symbol. We always emit DctDct.
        // With reduced_tx_set the symbol is TxtpIntra2[tDim.Min*13 + yMode], and DctDct is index 1 in that set.
        const int intra = 1;
        if (tDim.Max + intra < (int)Av1TxSize.Tx64x64)
        {
            w.EncodeSymbol(modeCdf.TxtpIntra2[tDim.Min * 13 + yMode], 1, 4);
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
            w.EncodeSymbol(coef.EobBaseTok[eobBaseTokIdx + ctx], eobTok, 2);
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
                w.EncodeSymbol(coef.BaseTok[baseTokIdx + loCtx], tok, 3);
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
            w.EncodeSymbol(coef.BaseTok[baseTokIdx + 0], dcTokBase, 3); // dcCtx = 0 for TwoD
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
                EncodeSignAndGolomb(w, coef.DcSign[chroma * 3 + dcSignCtx][0], adaptSign: true, signedLevels[0], dcMag);
            }

            for (int i = 1; i <= eob; i++)
            {
                int rcI = scan[i];
                int mag = Math.Abs(signedLevels[rcI]);
                if (mag != 0)
                {
                    EncodeSignAndGolomb(w, 0, adaptSign: false, signedLevels[rcI], mag);
                }
            }
        }
        else
        {
            // DC-only (eob == 0).
            int dcMag = Math.Abs(signedLevels[0]);
            int tokBr = Math.Min(dcMag, 3) - 1; // dcMag >= 1 here
            w.EncodeSymbol(coef.EobBaseTok[eobBaseTokIdx + 0], tokBr, 2);
            if (tokBr == 2)
            {
                EncodeHiTok(w, coef.BrTok[brTokIdx + 0], dcMag);
            }

            EncodeSignAndGolomb(w, coef.DcSign[chroma * 3 + dcSignCtx][0], adaptSign: true, signedLevels[0], dcMag);
        }
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
    private static void EncodeEob(Av1MsacWriter w, Av1CdfCoefContext coef, int chroma, int tx2dSzCtx, int txCtx, int eob)
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

        int is1d = 0; // TwoD
        switch (tx2dSzCtx)
        {
            case 0: w.EncodeSymbol(coef.EobBin16[chroma * 2 + is1d], eobPt, 4 + 0); break;
            case 1: w.EncodeSymbol(coef.EobBin32[chroma * 2 + is1d], eobPt, 4 + 1); break;
            case 2: w.EncodeSymbol(coef.EobBin64[chroma * 2 + is1d], eobPt, 4 + 2); break;
            case 3: w.EncodeSymbol(coef.EobBin128[chroma * 2 + is1d], eobPt, 4 + 3); break;
            case 4: w.EncodeSymbol(coef.EobBin256[chroma * 2 + is1d], eobPt, 4 + 4); break;
            case 5: w.EncodeSymbol(coef.EobBin512[chroma], eobPt, 4 + 5); break;
            default: w.EncodeSymbol(coef.EobBin1024[chroma], eobPt, 4 + 6); break;
        }

        if (eob > 1)
        {
            int eobBin = eobPt - 2;
            int hi = (eob >> eobBin) & 1;
            // EobHiBit CDF indexing matches DecodeCoefs exactly: [tDim.Ctx * 2 * 9 + chroma * 9 + eobBin].
            Span<ushort> hiCdf = coef.EobHiBit[txCtx * 2 * 9 + chroma * 9 + eobBin];
            w.EncodeBool((uint)hi, hiCdf[0]);
            if (eobBin > 0)
            {
                uint extra = (uint)(eob & ((1 << eobBin) - 1));
                w.EncodeLiteral(extra, eobBin);
            }
        }
    }

    /// <summary>HiTok base-range extension for magnitudes ≥ 3, inverse of Av1Msac.DecodeHiTok (max 4 symbols).</summary>
    private static void EncodeHiTok(Av1MsacWriter w, ReadOnlySpan<ushort> brCdf, int mag)
    {
        int rem = Math.Min(mag, 15) - 3;
        for (int stage = 0; stage < 4; stage++)
        {
            int s = Math.Min(rem, 3);
            w.EncodeSymbol(brCdf, s, 3);
            if (s < 3)
            {
                break;
            }

            rem -= 3;
        }
    }

    /// <summary>Encodes a coefficient's sign then, for |level| ≥ 15, the Golomb tail (level-15). The DC sign is
    /// adaptive (DcSign CDF prob f); AC signs are equiprobable.</summary>
    private static void EncodeSignAndGolomb(Av1MsacWriter w, uint dcSignF, bool adaptSign, int signedLevel, int mag)
    {
        uint sign = signedLevel < 0 ? 1u : 0u;
        if (adaptSign)
        {
            w.EncodeBool(sign, dcSignF);
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
