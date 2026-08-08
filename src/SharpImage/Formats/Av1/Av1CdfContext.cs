// Copyright (c) MediaKernel. All rights reserved.
// Port of dav1d CDF context types from src/cdf.h (VideoLAN dav1d, BSD-2-Clause)

using System;
using System.IO;

namespace SharpImage.Formats.Av1;

/// <summary>
/// CDF probability context for AV1 mode signaling.
/// Each array's innermost dimension holds Q15 inverse CDF values followed by
/// an adaptation counter at index [nSymbols]. SIMD padding is preserved to
/// match dav1d array dimensions exactly.
/// </summary>
public sealed class Av1CdfModeContext
{
    // Array dimensions: [outerDims...][cdfSize] where cdfSize includes counter + padding
    // "16" = up to 13 symbols + counter + padding to 16
    // "8"  = up to 7 symbols + counter + padding to 8
    // "4"  = up to 3 symbols + counter + padding to 4
    // "2"  = 1 symbol (boolean) + counter

    public ushort[][] UvMode = Alloc(2 * 13, 16);      // [2][N_INTRA_PRED_MODES][N_UV_INTRA_PRED_MODES + 2]
    public ushort[][] Partition = Alloc(5 * 4, 16);     // [N_BL_LEVELS][4][N_PARTITIONS + 6]
    public ushort[][] CflAlpha = Alloc(6, 16);          // [6][16]
    public ushort[][] TxtpInter1 = Alloc(2, 16);        // [2][16]
    public ushort[] TxtpInter2 = new ushort[16];        // [12 + 4]
    public ushort[][] TxtpIntra1 = Alloc(2 * 13, 8);   // [2][N_INTRA_PRED_MODES][7 + 1]
    public ushort[][] TxtpIntra2 = Alloc(3 * 13, 8);   // [3][N_INTRA_PRED_MODES][5 + 3]
    public ushort[] CflSign = new ushort[8];            // [8]
    public ushort[][] AngleDelta = Alloc(8, 8);         // [8][8]
    public ushort[] FilterIntra = new ushort[8];        // [5 + 3]
    public ushort[][] SegId = Alloc(3, 8);              // [3][DAV1D_MAX_SEGMENTS]
    public ushort[][] PalSz = Alloc(2 * 7, 8);         // [2][7][7 + 1]
    public ushort[][] ColorMap = Alloc(2 * 7 * 5, 8);  // [2][7][5][8]
    public ushort[][] Txsz = Alloc(4 * 3, 4);          // [N_TX_SIZES - 1][3][4]
    public ushort[] DeltaQ = new ushort[4];             // [4]
    public ushort[][] DeltaLf = Alloc(5, 4);            // [5][4]
    public ushort[] RestoreSwitchable = new ushort[4];  // [3 + 1]
    public ushort[] RestoreWiener = new ushort[2];      // [2]
    public ushort[] RestoreSgrproj = new ushort[2];     // [2]
    public ushort[][] TxtpInter3 = Alloc(4, 2);        // [4][2]
    public ushort[][] UseFilterIntra = Alloc(22, 2);    // [N_BS_SIZES][2]
    public ushort[][] Txpart = Alloc(7 * 3, 2);        // [7][3][2]
    public ushort[][] Skip = Alloc(3, 2);               // [3][2]
    public ushort[][] PalY = Alloc(7 * 3, 2);          // [7][3][2]
    public ushort[][] PalUv = Alloc(2, 2);              // [2][2]
    public ushort[] Intrabc = new ushort[2];            // [2]

    // Palette color and index CDFs
    public ushort[] PalColor = new ushort[9];           // [8 + 1] for color delta coding
    public ushort[][] PalIdx = Alloc(3, 2);             // [3][2] for index coding with context

    // Inter/switch mode CDFs
    public ushort[][] YMode = Alloc(4, 16);             // [4][N_INTRA_PRED_MODES + 3]
    public ushort[][] WedgeIdx = Alloc(9, 16);          // [9][16]
    public ushort[][] CompInterMode = Alloc(8, 8);      // [8][N_COMP_INTER_PRED_MODES]
    public ushort[][] Filter = Alloc(2 * 8, 4);         // [2][8][DAV1D_N_SWITCHABLE_FILTERS + 1]
    public ushort[][] InterintraMode = Alloc(4, 4);     // [4][4]
    public ushort[][] MotionMode = Alloc(22, 4);        // [N_BS_SIZES][3 + 1]
    public ushort[][] SkipMode = Alloc(3, 2);           // [3][2]
    public ushort[][] NewmvMode = Alloc(6, 2);          // [6][2]
    public ushort[][] GlobalmvMode = Alloc(2, 2);       // [2][2]
    public ushort[][] RefmvMode = Alloc(6, 2);          // [6][2]
    public ushort[][] DrlBit = Alloc(3, 2);             // [3][2]
    public ushort[][] Intra = Alloc(4, 2);              // [4][2]
    public ushort[][] Comp = Alloc(5, 2);               // [5][2]
    public ushort[][] CompDir = Alloc(5, 2);            // [5][2]
    public ushort[][] JntComp = Alloc(6, 2);            // [6][2]
    public ushort[][] MaskComp = Alloc(6, 2);           // [6][2]
    public ushort[][] WedgeComp = Alloc(9, 2);          // [9][2]
    public ushort[][] Ref = Alloc(6 * 3, 2);            // [6][3][2]
    public ushort[][] CompFwdRef = Alloc(3 * 3, 2);     // [3][3][2]
    public ushort[][] CompBwdRef = Alloc(2 * 3, 2);     // [2][3][2]
    public ushort[][] CompUniRef = Alloc(3 * 3, 2);     // [3][3][2]
    public ushort[][] SegPred = Alloc(3, 2);            // [3][2]
    public ushort[][] Interintra = Alloc(7, 2);         // [7][2]
    public ushort[][] InterintraWedge = Alloc(7, 2);    // [7][2]
    public ushort[][] Obmc = Alloc(22, 2);              // [N_BS_SIZES][2]

    private static ushort[][] Alloc(int rows, int cols)
    {
        var arr = new ushort[rows][];
        for (int i = 0; i < rows; i++)
            arr[i] = new ushort[cols];
        return arr;
    }
}

/// <summary>
/// CDF probability context for AV1 coefficient coding.
/// Four default instances exist, one per quantizer category (qcat 0..3).
/// </summary>
public sealed class Av1CdfCoefContext
{
    public ushort[][] EobBin16 = Alloc(2 * 2, 8);      // [2][2][5 + 3]
    public ushort[][] EobBin32 = Alloc(2 * 2, 8);      // [2][2][6 + 2]
    public ushort[][] EobBin64 = Alloc(2 * 2, 8);      // [2][2][7 + 1]
    public ushort[][] EobBin128 = Alloc(2 * 2, 8);     // [2][2][8]
    public ushort[][] EobBin256 = Alloc(2 * 2, 16);    // [2][2][9 + 7]
    public ushort[][] EobBin512 = Alloc(2, 16);         // [2][10 + 6]
    public ushort[][] EobBin1024 = Alloc(2, 16);        // [2][11 + 5]
    public ushort[][] EobBaseTok = Alloc(5 * 2 * 4, 4); // [N_TX_SIZES][2][4][4]
    public ushort[][] BaseTok = Alloc(5 * 2 * 41, 4);   // [N_TX_SIZES][2][41][4]
    public ushort[][] BrTok = Alloc(4 * 2 * 21, 4);     // [4][2][21][4]
    public ushort[][] EobHiBit = Alloc(5 * 2 * 9, 2);   // [N_TX_SIZES][2][9][2]
    public ushort[][] CoefSkip = Alloc(5 * 13, 2);      // [N_TX_SIZES][13][2]
    public ushort[][] DcSign = Alloc(2 * 3, 2);          // [2][3][2]

    private static ushort[][] Alloc(int rows, int cols)
    {
        var arr = new ushort[rows][];
        for (int i = 0; i < rows; i++)
            arr[i] = new ushort[cols];
        return arr;
    }
}

/// <summary>
/// CDF probability context for a single AV1 motion vector component (horizontal or vertical).
/// </summary>
public sealed class Av1CdfMvComponent
{
    public ushort[] Classes = new ushort[16];        // [11 + 5] — 11 classes + counter + padding
    public ushort[] Sign = new ushort[2];            // [2]
    public ushort[] Class0 = new ushort[2];          // [2]
    public ushort[][] Class0Fp = Alloc(2, 4);        // [2][4]
    public ushort[] Class0Hp = new ushort[2];        // [2]
    public ushort[][] ClassN = Alloc(10, 2);         // [10][2]
    public ushort[] ClassNFp = new ushort[4];        // [4]
    public ushort[] ClassNHp = new ushort[2];        // [2]

    private static ushort[][] Alloc(int rows, int cols)
    {
        var arr = new ushort[rows][];
        for (int i = 0; i < rows; i++)
            arr[i] = new ushort[cols];
        return arr;
    }
}

/// <summary>
/// CDF probability context for AV1 motion vectors (two components + joint type).
/// </summary>
public sealed class Av1CdfMvContext
{
    public Av1CdfMvComponent Comp0 = new();
    public Av1CdfMvComponent Comp1 = new();
    public ushort[] Joint = new ushort[8]; // [N_MV_JOINTS] padded to 8
}

/// <summary>
/// Complete CDF probability context for AV1 decoding.
/// Combines coefficient, mode, motion vector, and keyframe CDFs.
/// One instance is maintained per tile and updated during decoding.
/// </summary>
public sealed class Av1CdfContext
{
    public Av1CdfCoefContext Coef = new();
    public Av1CdfModeContext Mode = new();
    public Av1CdfMvContext Mv = new();
    public ushort[][] Kfym = AllocKfym(); // [5][5][N_INTRA_PRED_MODES + 3] = [25][16]

    private static ushort[][] AllocKfym()
    {
        var arr = new ushort[25][];
        for (int i = 0; i < 25; i++)
            arr[i] = new ushort[16];
        return arr;
    }

    /// <summary>
    /// Copies all CDF data from another context, then resets adaptation counters.
    /// Used at the start of each frame to initialize from the previous frame's CDFs.
    /// </summary>
    public void CopyFrom(Av1CdfContext src)
    {
        CopyCoef(src.Coef, Coef);
        CopyMode(src.Mode, Mode);
        CopyMvComponent(src.Mv.Comp0, Mv.Comp0);
        CopyMvComponent(src.Mv.Comp1, Mv.Comp1);
        Array.Copy(src.Mv.Joint, Mv.Joint, Mv.Joint.Length);
        for (int i = 0; i < 25; i++)
            Array.Copy(src.Kfym[i], Kfym[i], 16);
    }

    /// <summary>
    /// Resets all adaptation counters to zero. Called after copying CDFs from
    /// a reference frame to start fresh adaptation.
    /// </summary>
    public void ResetCounters()
    {
        ResetModeCounters(Mode);
        ResetCoefCounters(Coef);
        ResetMvCounters(Mv);
        ResetJaggedCounter(Kfym, 12); // 13 intra modes → 12 CDF values, counter at [12]
    }

    // ========================================================================
    // CDF Accessor Methods — used by Av1Decode for MSAC symbol decode
    // ========================================================================

    /// <summary>
    /// Get partition CDF for the given block level and context.
    /// Partition[bl * 4 + ctx] where bl ∈ [0..4], ctx ∈ [0..3].
    /// </summary>
    public Span<ushort> GetPartitionCdf(Av1BlockLevel bl, int ctx)
        => Mode.Partition[(int)bl * 4 + ctx];

    /// <summary>Get skip CDF. Skip[ctx] where ctx = above.skip + left.skip ∈ [0..2].</summary>
    public Span<ushort> GetSkipCdf(int ctx) => Mode.Skip[ctx];

    /// <summary>Get intra CDF. Intra[ctx] where ctx ∈ [0..3].</summary>
    public Span<ushort> GetIntraCdf(int ctx) => Mode.Intra[ctx];

    /// <summary>Get delta-Q CDF (single array).</summary>
    public Span<ushort> GetDeltaQCdf() => Mode.DeltaQ;

    /// <summary>Get delta-LF CDF. DeltaLf[idx] where idx ∈ [0..4].</summary>
    public Span<ushort> GetDeltaLfCdf(int idx) => Mode.DeltaLf[idx];

    /// <summary>
    /// Get Y mode CDF for inter frames.
    /// YMode[sizeCtx] where sizeCtx = YmodeSizeContext[blockSize] ∈ [0..3].
    /// </summary>
    public Span<ushort> GetYModeCdf(int sizeCtx) => Mode.YMode[sizeCtx];

    /// <summary>
    /// Get keyframe Y mode CDF.
    /// Kfym[aboveCtx * 5 + leftCtx] where contexts are IntraModeContext values ∈ [0..4].
    /// </summary>
    public Span<ushort> GetKfYModeCdf(int aboveCtx, int leftCtx) => Kfym[aboveCtx * 5 + leftCtx];

    /// <summary>
    /// Get angle delta CDF.
    /// AngleDelta[modeIdx] where modeIdx = mode - V_PRED ∈ [0..7].
    /// </summary>
    public Span<ushort> GetAngleDeltaCdf(int modeIdx) => Mode.AngleDelta[modeIdx];

    /// <summary>
    /// Get UV mode CDF.
    /// UvMode[(cflAllowed ? 13 : 0) + yMode] — 13 modes without CFL, 14 with.
    /// </summary>
    public Span<ushort> GetUvModeCdf(bool cflAllowed, int yMode)
        => Mode.UvMode[(cflAllowed ? 13 : 0) + yMode];

    /// <summary>Get CFL sign CDF (single array).</summary>
    public Span<ushort> GetCflSignCdf() => Mode.CflSign;

    /// <summary>Get CFL alpha CDF. CflAlpha[ctx] where ctx ∈ [0..5].</summary>
    public Span<ushort> GetCflAlphaCdf(int ctx) => Mode.CflAlpha[ctx];

    /// <summary>Get transform partition (txpart) CDF. Txpart[cat * 3 + ctx] where cat ∈ [0..6], ctx ∈ [0..2].</summary>
    public Span<ushort> GetTxPartCdf(int cat, int ctx) => Mode.Txpart[cat * 3 + ctx];

    /// <summary>
    /// Get palette Y CDF.
    /// PalY[szCtx * 3 + palCtx] where szCtx ∈ [0..6], palCtx ∈ [0..2].
    /// </summary>
    public Span<ushort> GetPalYCdf(int szCtx, int palCtx) => Mode.PalY[szCtx * 3 + palCtx];

    /// <summary>Get palette UV CDF. PalUv[palCtx] where palCtx ∈ [0..1].</summary>
    public Span<ushort> GetPalUvCdf(int palCtx) => Mode.PalUv[palCtx];

    /// <summary>Get palette size CDF. PalSz[plane * 7 + szCtx].</summary>
    public Span<ushort> GetPalSzCdf(int plane, int szCtx) => Mode.PalSz[plane * 7 + szCtx];

    /// <summary>Get filter intra CDF. UseFilterIntra[blockSize].</summary>
    public Span<ushort> GetFilterIntraCdf(Av1BlockSize bs) => Mode.UseFilterIntra[(int)bs];

    /// <summary>Get filter intra mode CDF (single array).</summary>
    public Span<ushort> GetFilterIntraModeCdf() => Mode.FilterIntra;

    /// <summary>
    /// Get TX size CDF.
    /// Txsz[(maxTxIdx) * 3 + ctx] where maxTxIdx = tDim.Max - 1 ∈ [0..3], ctx ∈ [0..2].
    /// </summary>
    public Span<ushort> GetTxSzCdf(int maxTxIdx, int ctx) => Mode.Txsz[maxTxIdx * 3 + ctx];

    // ── Inter prediction CDF accessors ──
    public Span<ushort> GetNewmvModeCdf(int ctx) => Mode.NewmvMode[ctx];
    public Span<ushort> GetRefmvModeCdf(int ctx) => Mode.RefmvMode[ctx];
    public Span<ushort> GetGlobalmvModeCdf(int ctx) => Mode.GlobalmvMode[ctx];
    public Span<ushort> GetDrlBitCdf(int ctx) => Mode.DrlBit[ctx];
    public Span<ushort> GetCompCdf(int ctx) => Mode.Comp[ctx];
    public Span<ushort> GetCompDirCdf(int ctx) => Mode.CompDir[ctx];
    public Span<ushort> GetCompFwdRefCdf(int ctx) => Mode.CompFwdRef[ctx];
    public Span<ushort> GetCompBwdRefCdf(int ctx) => Mode.CompBwdRef[ctx];
    public Span<ushort> GetCompUniRefCdf(int ctx) => Mode.CompUniRef[ctx];
    public Span<ushort> GetFilterCdf(int ctx) => Mode.Filter[ctx];
    public Span<ushort> GetCompInterModeCdf(int ctx) => Mode.CompInterMode[ctx];

    private static void CopyJagged(ushort[][] src, ushort[][] dst)
    {
        for (int i = 0; i < src.Length; i++)
            Array.Copy(src[i], dst[i], src[i].Length);
    }

    private static void CopyCoef(Av1CdfCoefContext src, Av1CdfCoefContext dst)
    {
        CopyJagged(src.EobBin16, dst.EobBin16);
        CopyJagged(src.EobBin32, dst.EobBin32);
        CopyJagged(src.EobBin64, dst.EobBin64);
        CopyJagged(src.EobBin128, dst.EobBin128);
        CopyJagged(src.EobBin256, dst.EobBin256);
        CopyJagged(src.EobBin512, dst.EobBin512);
        CopyJagged(src.EobBin1024, dst.EobBin1024);
        CopyJagged(src.EobBaseTok, dst.EobBaseTok);
        CopyJagged(src.BaseTok, dst.BaseTok);
        CopyJagged(src.BrTok, dst.BrTok);
        CopyJagged(src.EobHiBit, dst.EobHiBit);
        CopyJagged(src.CoefSkip, dst.CoefSkip);
        CopyJagged(src.DcSign, dst.DcSign);
    }

    private static void CopyMode(Av1CdfModeContext src, Av1CdfModeContext dst)
    {
        CopyJagged(src.UvMode, dst.UvMode);
        CopyJagged(src.Partition, dst.Partition);
        CopyJagged(src.CflAlpha, dst.CflAlpha);
        CopyJagged(src.TxtpInter1, dst.TxtpInter1);
        Array.Copy(src.TxtpInter2, dst.TxtpInter2, 16);
        CopyJagged(src.TxtpIntra1, dst.TxtpIntra1);
        CopyJagged(src.TxtpIntra2, dst.TxtpIntra2);
        Array.Copy(src.CflSign, dst.CflSign, 8);
        CopyJagged(src.AngleDelta, dst.AngleDelta);
        Array.Copy(src.FilterIntra, dst.FilterIntra, 8);
        CopyJagged(src.SegId, dst.SegId);
        CopyJagged(src.PalSz, dst.PalSz);
        CopyJagged(src.ColorMap, dst.ColorMap);
        CopyJagged(src.Txsz, dst.Txsz);
        Array.Copy(src.DeltaQ, dst.DeltaQ, 4);
        CopyJagged(src.DeltaLf, dst.DeltaLf);
        Array.Copy(src.RestoreSwitchable, dst.RestoreSwitchable, 4);
        Array.Copy(src.RestoreWiener, dst.RestoreWiener, 2);
        Array.Copy(src.RestoreSgrproj, dst.RestoreSgrproj, 2);
        CopyJagged(src.TxtpInter3, dst.TxtpInter3);
        CopyJagged(src.UseFilterIntra, dst.UseFilterIntra);
        CopyJagged(src.Txpart, dst.Txpart);
        CopyJagged(src.Skip, dst.Skip);
        CopyJagged(src.PalY, dst.PalY);
        CopyJagged(src.PalUv, dst.PalUv);
        Array.Copy(src.Intrabc, dst.Intrabc, 2);
        CopyJagged(src.YMode, dst.YMode);
        CopyJagged(src.WedgeIdx, dst.WedgeIdx);
        CopyJagged(src.CompInterMode, dst.CompInterMode);
        CopyJagged(src.Filter, dst.Filter);
        CopyJagged(src.InterintraMode, dst.InterintraMode);
        CopyJagged(src.MotionMode, dst.MotionMode);
        CopyJagged(src.SkipMode, dst.SkipMode);
        CopyJagged(src.NewmvMode, dst.NewmvMode);
        CopyJagged(src.GlobalmvMode, dst.GlobalmvMode);
        CopyJagged(src.RefmvMode, dst.RefmvMode);
        CopyJagged(src.DrlBit, dst.DrlBit);
        CopyJagged(src.Intra, dst.Intra);
        CopyJagged(src.Comp, dst.Comp);
        CopyJagged(src.CompDir, dst.CompDir);
        CopyJagged(src.JntComp, dst.JntComp);
        CopyJagged(src.MaskComp, dst.MaskComp);
        CopyJagged(src.WedgeComp, dst.WedgeComp);
        CopyJagged(src.Ref, dst.Ref);
        CopyJagged(src.CompFwdRef, dst.CompFwdRef);
        CopyJagged(src.CompBwdRef, dst.CompBwdRef);
        CopyJagged(src.CompUniRef, dst.CompUniRef);
        CopyJagged(src.SegPred, dst.SegPred);
        CopyJagged(src.Interintra, dst.Interintra);
        CopyJagged(src.InterintraWedge, dst.InterintraWedge);
        CopyJagged(src.Obmc, dst.Obmc);
    }

    private static void CopyMvComponent(Av1CdfMvComponent src, Av1CdfMvComponent dst)
    {
        Array.Copy(src.Classes, dst.Classes, 16);
        Array.Copy(src.Sign, dst.Sign, 2);
        Array.Copy(src.Class0, dst.Class0, 2);
        CopyJagged(src.Class0Fp, dst.Class0Fp);
        Array.Copy(src.Class0Hp, dst.Class0Hp, 2);
        CopyJagged(src.ClassN, dst.ClassN);
        Array.Copy(src.ClassNFp, dst.ClassNFp, 4);
        Array.Copy(src.ClassNHp, dst.ClassNHp, 2);
    }

    private static void ResetJaggedCounter(ushort[][] arr, int counterIndex)
    {
        for (int i = 0; i < arr.Length; i++)
            arr[i][counterIndex] = 0;
    }

    private static void ResetModeCounters(Av1CdfModeContext m)
    {
        for (int i = 0; i < 13; i++)
            m.UvMode[i][12] = 0; // no CFL: 13 modes, counter at 12
        for (int i = 13; i < 26; i++)
            m.UvMode[i][13] = 0; // CFL: 14 modes, counter at 13
        for (int ctx = 0; ctx < 4; ctx++)
            m.Partition[0 * 4 + ctx][7] = 0;    // BL_128X128
        for (int bl = 1; bl < 4; bl++)
            for (int ctx = 0; ctx < 4; ctx++)
                m.Partition[bl * 4 + ctx][9] = 0; // BL_64X64..BL_16X16
        for (int ctx = 0; ctx < 4; ctx++)
            m.Partition[4 * 4 + ctx][3] = 0;    // BL_8X8
        ResetJaggedCounter(m.CflAlpha, 15);
        ResetJaggedCounter(m.TxtpInter1, 15);
        m.TxtpInter2[11] = 0;
        ResetJaggedCounter(m.TxtpIntra1, 6);
        ResetJaggedCounter(m.TxtpIntra2, 4);
        m.CflSign[7] = 0;
        ResetJaggedCounter(m.AngleDelta, 6);
        m.FilterIntra[4] = 0;
        ResetJaggedCounter(m.SegId, 7);
        ResetJaggedCounter(m.PalSz, 6);
        for (int plane = 0; plane < 2; plane++)
            for (int pal = 0; pal < 7; pal++)
                for (int ctx = 0; ctx < 5; ctx++)
                    m.ColorMap[(plane * 7 + pal) * 5 + ctx][pal + 1] = 0;
        for (int k = 0; k < 4; k++)
            for (int ctx = 0; ctx < 3; ctx++)
                m.Txsz[k * 3 + ctx][Math.Min(k + 1, 2)] = 0;
        m.DeltaQ[3] = 0;
        ResetJaggedCounter(m.DeltaLf, 3);
        m.RestoreSwitchable[2] = 0;
        m.RestoreWiener[1] = 0;
        m.RestoreSgrproj[1] = 0;
        ResetJaggedCounter(m.Txpart, 1);  // 7 cat × 3 ctx, bool cdf (counter at index 1)
        ResetJaggedCounter(m.TxtpInter3, 1);
        ResetJaggedCounter(m.UseFilterIntra, 1);
        ResetJaggedCounter(m.Txpart, 1);
        ResetJaggedCounter(m.Skip, 1);
        ResetJaggedCounter(m.PalY, 1);
        ResetJaggedCounter(m.PalUv, 1);
        m.Intrabc[1] = 0;
        ResetJaggedCounter(m.YMode, 12);
        ResetJaggedCounter(m.WedgeIdx, 15);
        ResetJaggedCounter(m.CompInterMode, 7);
        ResetJaggedCounter(m.Filter, 3);
        ResetJaggedCounter(m.InterintraMode, 3);
        ResetJaggedCounter(m.MotionMode, 2);
        ResetJaggedCounter(m.SkipMode, 1);
        ResetJaggedCounter(m.NewmvMode, 1);
        ResetJaggedCounter(m.GlobalmvMode, 1);
        ResetJaggedCounter(m.RefmvMode, 1);
        ResetJaggedCounter(m.DrlBit, 1);
        ResetJaggedCounter(m.Intra, 1);
        ResetJaggedCounter(m.Comp, 1);
        ResetJaggedCounter(m.CompDir, 1);
        ResetJaggedCounter(m.JntComp, 1);
        ResetJaggedCounter(m.MaskComp, 1);
        ResetJaggedCounter(m.WedgeComp, 1);
        ResetJaggedCounter(m.Ref, 1);
        ResetJaggedCounter(m.CompFwdRef, 1);
        ResetJaggedCounter(m.CompBwdRef, 1);
        ResetJaggedCounter(m.CompUniRef, 1);
        ResetJaggedCounter(m.SegPred, 1);
        ResetJaggedCounter(m.Interintra, 1);
        ResetJaggedCounter(m.InterintraWedge, 1);
        ResetJaggedCounter(m.Obmc, 1);
    }

    private static void ResetCoefCounters(Av1CdfCoefContext c)
    {
        ResetJaggedCounter(c.EobBin16, 4);
        ResetJaggedCounter(c.EobBin32, 5);
        ResetJaggedCounter(c.EobBin64, 6);
        ResetJaggedCounter(c.EobBin128, 7);
        ResetJaggedCounter(c.EobBin256, 8);
        ResetJaggedCounter(c.EobBin512, 9);
        ResetJaggedCounter(c.EobBin1024, 10);
        ResetJaggedCounter(c.EobBaseTok, 2);
        ResetJaggedCounter(c.BaseTok, 3);
        ResetJaggedCounter(c.BrTok, 3);
        ResetJaggedCounter(c.EobHiBit, 1);
        ResetJaggedCounter(c.CoefSkip, 1);
        ResetJaggedCounter(c.DcSign, 1);
    }

    private static void ResetMvCounters(Av1CdfMvContext mv)
    {
        ResetMvComponentCounters(mv.Comp0);
        ResetMvComponentCounters(mv.Comp1);
        mv.Joint[3] = 0;
    }

    private static void ResetMvComponentCounters(Av1CdfMvComponent c)
    {
        c.Classes[10] = 0;
        c.Sign[1] = 0;
        c.Class0[1] = 0;
        ResetJaggedCounter(c.Class0Fp, 3);
        c.Class0Hp[1] = 0;
        ResetJaggedCounter(c.ClassN, 1);
        c.ClassNFp[3] = 0;
        c.ClassNHp[1] = 0;
    }

    /// <summary>
    /// Loads coefficient CDF values from a dav1d text dump file (cdf_snapshot_f*.txt).
    /// Each line is "table[index]=value" where table is skip, eob16, eob32, etc.
    /// </summary>
    public void LoadCoefCdfsFromDav1dDump(string path)
    {
        var coef = this.Coef;
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq);
            if (!ushort.TryParse(line.AsSpan(eq + 1), out ushort value)) continue;

            if (key.StartsWith("skip["))
            {
                int end = key.IndexOf(']', 5);
                if (end < 0) continue;
                int flatIdx = int.Parse(key.Substring(5, end - 5));
                int sub = key.EndsWith(".0") ? 0 : 1;
                coef.CoefSkip[flatIdx][sub] = value;
            }
            else if (key.StartsWith("eob16["))
                ParseEob(key, value, coef.EobBin16);
            else if (key.StartsWith("eob32["))
                ParseEob(key, value, coef.EobBin32);
            else if (key.StartsWith("eob64["))
                ParseEob(key, value, coef.EobBin64);
            else if (key.StartsWith("eob128["))
                ParseEob(key, value, coef.EobBin128);
            else if (key.StartsWith("eob256["))
                ParseEob(key, value, coef.EobBin256);
            else if (key.StartsWith("eob512["))
                ParseEob2D(key, value, coef.EobBin512);
            else if (key.StartsWith("eob1024["))
                ParseEob2D(key, value, coef.EobBin1024);
            else if (key.StartsWith("btok["))
                ParseBtok(key, value, coef.BaseTok);
            else if (key.StartsWith("brtok["))
                ParseBrtok(key, value, coef.BrTok);
            else if (key.StartsWith("dcsign["))
                ParseDcsign(key, value, coef.DcSign);
            else if (key.StartsWith("ebtok["))
                ParseEbtok(key, value, coef.EobBaseTok);
            else if (key.StartsWith("ehbit["))
                ParseEhbit(key, value, coef.EobHiBit);
            // Mode CDF tables
            else if (key.StartsWith("mpart["))
                ParseMpart(key, value, Mode.Partition);
            else if (key.StartsWith("mskip["))
                ParseMskip(key, value, Mode.Skip);
            else if (key.StartsWith("mintra["))
                ParseMintra(key, value, Mode.Intra);
            else if (key.StartsWith("mymode["))
                ParseMymode(key, value, Mode.YMode);
            else if (key.StartsWith("mtxsz["))
                ParseMtxsz(key, value, Mode.Txsz);
            else if (key.StartsWith("mcfla["))
                ParseMcfla(key, value, Mode.CflAlpha);
            // Inter mode CDFs
            else if (key.StartsWith("mnewmv["))
                Parse2D(key, value, Mode.NewmvMode);
            else if (key.StartsWith("mgmv["))
                Parse2D(key, value, Mode.GlobalmvMode);
            else if (key.StartsWith("mrefmv["))
                Parse2D(key, value, Mode.RefmvMode);
            else if (key.StartsWith("mdrl["))
                Parse2D(key, value, Mode.DrlBit);
            else if (key.StartsWith("miintra["))
                Parse2D(key, value, Mode.Interintra);
            else if (key.StartsWith("mref["))
                ParseMref(key, value, Mode.Ref);
            else if (key.StartsWith("mcomp["))
                Parse2D(key, value, Mode.Comp);
            else if (key.StartsWith("mcdir["))
                Parse2D(key, value, Mode.CompDir);
            else if (key.StartsWith("mmot["))
                Parse2D(key, value, Mode.MotionMode);
            else if (key.StartsWith("mtxtpi1["))
                Parse2D(key, value, Mode.TxtpInter1);
            else if (key.StartsWith("mtxtpi2["))
                Parse1D(key, value, Mode.TxtpInter2);
            else if (key.StartsWith("mfilt["))
                Parse2D(key, value, Mode.Filter);
            else if (key.StartsWith("muv["))
                Parse2D(key, value, Mode.UvMode);
            // Additional CDF tables
            else if (key.StartsWith("mtxpart["))
                ParseMtxpart(key, value, Mode.Txpart);
            else if (key.StartsWith("mdeltaq["))
                Parse1D(key, value, Mode.DeltaQ);
            else if (key.StartsWith("mdeltalf["))
                Parse2D(key, value, Mode.DeltaLf);
            else if (key.StartsWith("mrest["))
                Parse1D(key, value, Mode.RestoreSwitchable);
            else if (key.StartsWith("mwien["))
                Parse1D(key, value, Mode.RestoreWiener);
            else if (key.StartsWith("msgr["))
                Parse1D(key, value, Mode.RestoreSgrproj);
            else if (key.StartsWith("msegi["))
                Parse2D(key, value, Mode.SegId);
            else if (key.StartsWith("msegp["))
                Parse2D(key, value, Mode.SegPred);
            else if (key.StartsWith("mpalsz["))
                ParseMpalsz(key, value, Mode.PalSz);
            else if (key.StartsWith("mpaly["))
                ParseMpaly(key, value, Mode.PalY);
            else if (key.StartsWith("mpaluv["))
                Parse2D(key, value, Mode.PalUv);
            else if (key.StartsWith("mcol["))
                ParseMcol(key, value, Mode.ColorMap);
            else if (key.StartsWith("mintrabc["))
                Parse1D(key, value, Mode.Intrabc);
            else if (key.StartsWith("mskipmode["))
                Parse2D(key, value, Mode.SkipMode);
            else if (key.StartsWith("mangle["))
                Parse2D(key, value, Mode.AngleDelta);
            else if (key.StartsWith("mfiltintra["))
                Parse1D(key, value, Mode.FilterIntra);
            else if (key.StartsWith("musefi["))
                Parse2D(key, value, Mode.UseFilterIntra);
            else if (key.StartsWith("mcflsig["))
                Parse1D(key, value, Mode.CflSign);
            else if (key.StartsWith("mtxtpia1["))
                ParseMtXtpIa1(key, value, Mode.TxtpIntra1);
            else if (key.StartsWith("mtxtpia2["))
                ParseMtXtpIa2(key, value, Mode.TxtpIntra2);
            else if (key.StartsWith("mtxtpi3["))
                Parse2D(key, value, Mode.TxtpInter3);
            else if (key.StartsWith("mjntcomp["))
                Parse2D(key, value, Mode.JntComp);
            else if (key.StartsWith("mmaskcomp["))
                Parse2D(key, value, Mode.MaskComp);
            else if (key.StartsWith("mwedgecomp["))
                Parse2D(key, value, Mode.WedgeComp);
            else if (key.StartsWith("mcompfwd["))
                ParseMcompfwd(key, value, Mode.CompFwdRef);
            else if (key.StartsWith("mcompbwd["))
                ParseMcompbwd(key, value, Mode.CompBwdRef);
            else if (key.StartsWith("mcompuni["))
                ParseMcompuni(key, value, Mode.CompUniRef);
            else if (key.StartsWith("minterintraw["))
                Parse2D(key, value, Mode.InterintraWedge);
            else if (key.StartsWith("mwedge["))
                Parse2D(key, value, Mode.WedgeIdx);
            else if (key.StartsWith("mobmc["))
                Parse2D(key, value, Mode.Obmc);
            // MV CDF tables (dav1d CdfMvContext)
            else if (key.StartsWith("mvjoint["))
                Parse1D(key, value, Mv.Joint);
            else if (key.StartsWith("mvcomp0_"))
                ParseMvComp(key, value, Mv.Comp0);
            else if (key.StartsWith("mvcomp1_"))
                ParseMvComp(key, value, Mv.Comp1);
        }
    }

    private static void ParseMvComp(string key, ushort value, Av1CdfMvComponent c)
    {
        int underscore = key.IndexOf('_');
        string sub = key.Substring(underscore + 1);  // e.g. "classes[0]"
        if (sub.StartsWith("classes["))
            Parse1D(key, value, c.Classes);
        else if (sub.StartsWith("sign["))
            Parse1D(key, value, c.Sign);
        else if (sub.StartsWith("class0fp["))
            Parse2D(key, value, c.Class0Fp);
        else if (sub.StartsWith("class0hp["))
            Parse1D(key, value, c.Class0Hp);
        else if (sub.StartsWith("class0["))
            Parse1D(key, value, c.Class0);
        else if (sub.StartsWith("classNfp["))
            Parse1D(key, value, c.ClassNFp);
        else if (sub.StartsWith("classNhp["))
            Parse1D(key, value, c.ClassNHp);
        else if (sub.StartsWith("classN["))
            Parse2D(key, value, c.ClassN);
    }

    private static void Parse1D(string key, ushort value, ushort[] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int v = int.Parse(key.Substring(b1, e1 - b1));
        arr[v] = value;
    }

    private static void ParseMpart(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int bl = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[bl * 4 + ctx][v] = value;
    }

    private static void ParseMskip(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int v = int.Parse(key.Substring(b2, e2 - b2));
        arr[ctx][v] = value;
    }

    private static void ParseMintra(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int v = int.Parse(key.Substring(b2, e2 - b2));
        arr[ctx][v] = value;
    }

    private static void ParseMymode(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int v = int.Parse(key.Substring(b2, e2 - b2));
        arr[ctx][v] = value;
    }

    private static void ParseMtxsz(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int tx = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[tx * 3 + ctx][v] = value;
    }

    private static void ParseMcfla(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int v = int.Parse(key.Substring(b2, e2 - b2));
        arr[ctx][v] = value;
    }

    // Generic 2D parser: key[ctx][v]=value
    private static void Parse2D(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int v = int.Parse(key.Substring(b2, e2 - b2));
        arr[ctx][v] = value;
    }

    // Ref 3D parser: mref[ctx][sctx][v]=value → Ref[ctx*3 + sctx][v]
    private static void ParseMref(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int ctx = int.Parse(key.Substring(b1, e1 - b1));
        int sctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[ctx * 3 + sctx][v] = value;
    }

    private static void ParseEob(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int i = int.Parse(key.Substring(b1, e1 - b1));
        int j = int.Parse(key.Substring(b2, e2 - b2));
        int k = int.Parse(key.Substring(b3, e3 - b3));
        arr[i * 2 + j][k] = value;
    }

    private static void ParseEob2D(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int i = int.Parse(key.Substring(b1, e1 - b1));
        int j = int.Parse(key.Substring(b2, e2 - b2));
        arr[i][j] = value;
    }

    /// <summary>Parse key like "dcsign[c][ctx][v]" into flat arr[c*3+ctx][v].</summary>
    private static void ParseDcsign(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int c = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[c * 3 + ctx][v] = value;
    }

    /// <summary>Parse key like "ebtok[tx][c][k][v]" into flat arr[((tx*2+c)*4+k)][v].</summary>
    private static void ParseEbtok(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int b4 = key.IndexOf('[', e3) + 1;
        int e4 = key.IndexOf(']', b4);
        int tx = int.Parse(key.Substring(b1, e1 - b1));
        int c = int.Parse(key.Substring(b2, e2 - b2));
        int k = int.Parse(key.Substring(b3, e3 - b3));
        int v = int.Parse(key.Substring(b4, e4 - b4));
        arr[(tx * 2 + c) * 4 + k][v] = value;
    }

    /// <summary>Parse key like "ehbit[tx][c][k][v]" into flat arr[((tx*2+c)*9+k)][v].</summary>
    private static void ParseEhbit(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int b4 = key.IndexOf('[', e3) + 1;
        int e4 = key.IndexOf(']', b4);
        int tx = int.Parse(key.Substring(b1, e1 - b1));
        int c = int.Parse(key.Substring(b2, e2 - b2));
        int k = int.Parse(key.Substring(b3, e3 - b3));
        int v = int.Parse(key.Substring(b4, e4 - b4));
        arr[(tx * 2 + c) * 9 + k][v] = value;
    }

    private static void ParseBtok(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int b4 = key.IndexOf('[', e3) + 1;
        int e4 = key.IndexOf(']', b4);
        int tx = int.Parse(key.Substring(b1, e1 - b1));
        int c = int.Parse(key.Substring(b2, e2 - b2));
        int k = int.Parse(key.Substring(b3, e3 - b3));
        int v = int.Parse(key.Substring(b4, e4 - b4));
        arr[tx * 82 + c * 41 + k][v] = value;
    }

    private static void ParseBrtok(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1;
        int e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1;
        int e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1;
        int e3 = key.IndexOf(']', b3);
        int b4 = key.IndexOf('[', e3) + 1;
        int e4 = key.IndexOf(']', b4);
        int i = int.Parse(key.Substring(b1, e1 - b1));
        int c = int.Parse(key.Substring(b2, e2 - b2));
        int k = int.Parse(key.Substring(b3, e3 - b3));
        int v = int.Parse(key.Substring(b4, e4 - b4));
        arr[i * 42 + c * 21 + k][v] = value;
    }

    // Additional table parsers

    // mtxpart[cat][ctx][v] -> Txpart[cat * 3 + ctx][v]
    private static void ParseMtxpart(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int cat = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[cat * 3 + ctx][v] = value;
    }

    // mpalsz[p][sz][v] -> PalSz[(p * 7 + sz)][v]
    private static void ParseMpalsz(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int p = int.Parse(key.Substring(b1, e1 - b1));
        int sz = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[p * 7 + sz][v] = value;
    }

    // mpaly[sz][ctx][v] -> PalY[sz * 3 + ctx][v]
    private static void ParseMpaly(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int sz = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[sz * 3 + ctx][v] = value;
    }

    // mcol[p][sz][ctx][v] -> ColorMap[((p * 7 + sz) * 5 + ctx)][v]
    private static void ParseMcol(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int b4 = key.IndexOf('[', e3) + 1, e4 = key.IndexOf(']', b4);
        int p = int.Parse(key.Substring(b1, e1 - b1));
        int sz = int.Parse(key.Substring(b2, e2 - b2));
        int ctx = int.Parse(key.Substring(b3, e3 - b3));
        int v = int.Parse(key.Substring(b4, e4 - b4));
        arr[(p * 7 + sz) * 5 + ctx][v] = value;
    }

    // mtxtpia1[p][mi][v] -> TxtpIntra1[(p * 13 + mi)][v]
    private static void ParseMtXtpIa1(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int p = int.Parse(key.Substring(b1, e1 - b1));
        int mi = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[p * 13 + mi][v] = value;
    }

    // mtxtpia2[p][mi][v] -> TxtpIntra2[(p * 13 + mi)][v]
    private static void ParseMtXtpIa2(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int p = int.Parse(key.Substring(b1, e1 - b1));
        int mi = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[p * 13 + mi][v] = value;
    }

    // mcompfwd[c][ctx][v] -> CompFwdRef[c * 3 + ctx][v]
    private static void ParseMcompfwd(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int c = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[c * 3 + ctx][v] = value;
    }

    // mcompbwd[c][ctx][v] -> CompBwdRef[c * 3 + ctx][v]
    private static void ParseMcompbwd(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int c = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[c * 3 + ctx][v] = value;
    }

    // mcompuni[c][ctx][v] -> CompUniRef[c * 3 + ctx][v]
    private static void ParseMcompuni(string key, ushort value, ushort[][] arr)
    {
        int b1 = key.IndexOf('[') + 1, e1 = key.IndexOf(']', b1);
        int b2 = key.IndexOf('[', e1) + 1, e2 = key.IndexOf(']', b2);
        int b3 = key.IndexOf('[', e2) + 1, e3 = key.IndexOf(']', b3);
        int c = int.Parse(key.Substring(b1, e1 - b1));
        int ctx = int.Parse(key.Substring(b2, e2 - b2));
        int v = int.Parse(key.Substring(b3, e3 - b3));
        arr[c * 3 + ctx][v] = value;
    }
}
