using System;
using static SharpImage.Formats.Av1.AomTables;
using SharpImage.Core;

namespace SharpImage.Formats.Av1;

// rd.c's model functions and model_rd.h (calculate_sse, compute_sse_plane, model_rd_from_sse, model_rd_with_curvfit,
// model_rd_for_sb, model_rd_for_sb_with_curvfit).
internal static partial class AomModelRd
{
    public const int MODELRD_LEGACY = 0, MODELRD_CURVFIT = 1;
    // MODELRD_TYPE_* (model_rd.h): every caller's model is the curve fit
    public const int MODELRD_TYPE_INTERP_FILTER = 1, MODELRD_TYPE_TX_SEARCH_PRUNE = 1, MODELRD_TYPE_MASKED_COMPOUND = 1,
        MODELRD_TYPE_INTERINTRA = 1, MODELRD_TYPE_INTRA = 1, MODELRD_TYPE_MOTION_MODE_RD = 1;

    private static int GetMsb(uint n) => 31 - System.Numerics.BitOperations.LeadingZeroCount(n);

    /// <summary>model_rd_norm.</summary>
    private static void ModelRdNorm(int xsqQ10, out int rQ10, out int dQ10)
    {
        int tmp = (xsqQ10 >> 2) + 8;
        int k = GetMsb((uint)tmp) - 3;
        int xq = (k << 3) + ((tmp >> k) & 0x7);
        const int oneQ10 = 1 << 10;
        int aQ10 = ((xsqQ10 - XsqIqQ10[xq]) << 10) >> (2 + k);
        int bQ10 = oneQ10 - aQ10;
        rQ10 = (RateTabQ10[xq] * bQ10 + RateTabQ10[xq + 1] * aQ10) >> 10;
        dQ10 = (DistTabQ10[xq] * bQ10 + DistTabQ10[xq + 1] * aQ10) >> 10;
    }

    /// <summary>av1_model_rd_from_var_lapndz.</summary>
    public static void FromVarLapndz(long var, int nLog2, uint qstep, out int rate, out long dist)
    {
        if (var == 0) { rate = 0; dist = 0; return; }
        const uint MAX_XSQ_Q10 = 245727;
        ulong xsq64 = (((ulong)qstep * qstep << (nLog2 + 10)) + (ulong)(var >> 1)) / (ulong)var;
        int xsqQ10 = (int)Math.Min(xsq64, MAX_XSQ_Q10);
        ModelRdNorm(xsqQ10, out int rQ10, out int dQ10);
        rate = ((rQ10 << nLog2) + (1 << (10 - AomCost.ProbCostShift - 1))) >> (10 - AomCost.ProbCostShift);
        dist = (var * dQ10 + 512) >> 10;
    }

    private static double InterpCubic(double[,] g, int row, int i, double x)
    {
        double p0 = g[row, i], p1 = g[row, i + 1], p2 = g[row, i + 2], p3 = g[row, i + 3];
        return p1 + 0.5 * x * (p2 - p0 + x * (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3 + x * (3.0 * (p1 - p2) + p3 - p0)));
    }

    /// <summary>av1_model_rd_curvfit.</summary>
    public static void Curvfit(int bsize, double sseNorm, double xqr, out double rateF, out double distbysseF)
    {
        const double xStart = -15.5, xEnd = 16.5, xStep = 0.5, epsilon = 1e-6;
        int rcat = BsizeCurvfitModelCatLookup[bsize];
        int dcat = sseNorm > 16.0 ? 1 : 0;
        xqr = Math.Max(xqr, xStart + xStep + epsilon);
        xqr = Math.Min(xqr, xEnd - xStep - epsilon);
        double x = (xqr - xStart) / xStep;
        int xi = (int)Math.Floor(x);
        double xo = x - xi;
        rateF = InterpCubic(InterpRgridCurv, rcat, xi - 1, xo);
        distbysseF = InterpCubic(InterpDgridCurv, dcat, xi - 1, xo);
    }

    /// <summary>calculate_sse: the source / dst SSE at the 8-bit scale.</summary>
    public static long CalculateSse(AomMacroblockD xd, AomMbPlane p, AomMbdPlane pd, int bw, int bh)
    {
        long sse;
        int shift = xd.Bd - 8;
        if (xd.IsHbd) sse = AomHbd.Sse(p.Src.Buf16, p.Src.Offset, p.Src.Stride, pd.Dst.Buf16, pd.Dst.Offset, pd.Dst.Stride, bw, bh);
        else sse = AomEncodeMb.Sse(p.Src.Buf, p.Src.Offset, p.Src.Stride, pd.Dst.Buf, pd.Dst.Offset, pd.Dst.Stride, bw, bh);
        if (shift > 0) sse = (sse + (1L << (shift * 2 - 1))) >> (shift * 2);
        return sse;
    }

    /// <summary>compute_sse_plane.</summary>
    public static long ComputeSsePlane(AomMacroblock x, AomMacroblockD xd, int plane, int bsize)
    {
        var pd = xd.Plane[plane];
        int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
        AomEncodeMb.TxbDimensions(xd, plane, planeBsize, 0, 0, planeBsize, out _, out _, out int bw, out int bh);
        return CalculateSse(xd, x.Plane[plane], pd, bw, bh);
    }

    private static int DequantShift(AomMacroblockD xd) => xd.IsHbd ? xd.Bd - 5 : 3;

    /// <summary>model_rd_from_sse.</summary>
    public static void FromSse(AomComp cpi, AomMacroblock x, int planeBsize, int plane, long sse, int numSamples, out int rate, out long dist)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        int dqShift = DequantShift(xd);
        if (cpi.Sf.rd_sf.simple_model_rd_from_var != 0)
        {
            long squareError = sse;
            int quantizer = p.Dequant1 >> dqShift;
            rate = quantizer < 120 ? (int)Math.Min((squareError * (280 - quantizer)) >> (16 - AomCost.ProbCostShift), int.MaxValue) : 0;
            dist = (squareError * quantizer) >> 8;
        }
        else FromVarLapndz(sse, NumPelsLog2Lookup[planeBsize], (uint)(p.Dequant1 >> dqShift), out rate, out dist);
        dist <<= 4;
    }

    /// <summary>model_rd_with_curvfit.</summary>
    public static void WithCurvfit(AomComp cpi, AomMacroblock x, int planeBsize, int plane, long sse, int numSamples, out int rate, out long dist)
    {
        var xd = x.E;
        var p = x.Plane[plane];
        int dqShift = DequantShift(xd);
        int qstep = Math.Max(p.Dequant1 >> dqShift, 1);
        if (sse == 0) { rate = 0; dist = 0; return; }
        double sseNorm = (double)sse / numSamples;
        double qstepsqr = (double)qstep * qstep;
        double xqr = PortableMathD.Log2(sseNorm / qstepsqr);
        Curvfit(planeBsize, sseNorm, xqr, out double rateF, out double distBySseNormF);
        double distF = distBySseNormF * sseNorm;
        int rateI = (int)(Math.Max(0.0, rateF * numSamples) + 0.5);
        long distI = (long)(Math.Max(0.0, distF * numSamples) + 0.5);
        if (rateI == 0) distI = sse << 4;
        else if (AomRd.RdCost(x.Rdmult, rateI, distI) >= AomRd.RdCost(x.Rdmult, 0, sse << 4))
        {
            rateI = 0;
            distI = sse << 4;
        }
        rate = rateI;
        dist = distI;
    }

    /// <summary>model_rd_sse_fn[type].</summary>
    public static void SseFn(int type, AomComp cpi, AomMacroblock x, int planeBsize, int plane, long sse, int numSamples, out int rate, out long dist)
    {
        if (type == MODELRD_CURVFIT) WithCurvfit(cpi, x, planeBsize, plane, sse, numSamples, out rate, out dist);
        else FromSse(cpi, x, planeBsize, plane, sse, numSamples, out rate, out dist);
    }

    /// <summary>model_rd_sb_fn[type] (model_rd_for_sb / model_rd_for_sb_with_curvfit). planeRate / planeSse /
    /// planeDist may be null.</summary>
    public static void SbFn(int type, AomComp cpi, int bsize, AomMacroblock x, AomMacroblockD xd, int planeFrom, int planeTo, out int outRateSum,
        out long outDistSum, out byte skipTxfmSb, out long skipSseSb, int[]? planeRate, long[]? planeSse, long[]? planeDist)
    {
        int refFrame = xd.Mi0.RefFrame0;
        long rateSum = 0, distSum = 0, totalSse = 0;
        for (int plane = planeFrom; plane <= planeTo; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            var p = x.Plane[plane];
            var pd = xd.Plane[plane];
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            int bw, bh;
            if (type == MODELRD_CURVFIT) AomEncodeMb.TxbDimensions(xd, plane, planeBsize, 0, 0, planeBsize, out _, out _, out bw, out bh);
            else { bw = BlockSizeWide[planeBsize]; bh = BlockSizeHigh[planeBsize]; }
            long sse = CalculateSse(xd, p, pd, bw, bh);
            int rate;
            long dist;
            if (type == MODELRD_CURVFIT) WithCurvfit(cpi, x, planeBsize, plane, sse, bw * bh, out rate, out dist);
            else FromSse(cpi, x, planeBsize, plane, sse, bw * bh, out rate, out dist);
            if (plane == 0) x.PredSse[refFrame] = (uint)Math.Min(sse, uint.MaxValue);
            totalSse += sse;
            rateSum += rate;
            distSum += dist;
            if (planeRate != null) planeRate[plane] = rate;
            if (planeSse != null) planeSse[plane] = sse;
            if (planeDist != null) planeDist[plane] = dist;
        }
        skipTxfmSb = (byte)(type == MODELRD_CURVFIT ? (rateSum == 0 ? 1 : 0) : (totalSse == 0 ? 1 : 0));
        skipSseSb = totalSse << 4;
        if (type != MODELRD_CURVFIT) rateSum = Math.Min(rateSum, int.MaxValue);
        outRateSum = (int)rateSum;
        outDistSum = distSum;
    }
}
