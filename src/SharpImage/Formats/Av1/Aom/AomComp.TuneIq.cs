using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1's AOM_TUNE_IQ state for an all-intra key frame: what handle_tuning (av1_cx_iface.c) turns on and the
// per-frame values the encoder derives from it (rdmult weight, SSIM rdmult scaling, chroma delta q, quantization
// matrices, Variance Boost delta q, CDEF_ADAPTIVE, loop filter sharpness).
internal sealed partial class AomComp
{
    // oxcf.q_cfg: using_qm / qm_minlevel / qm_maxlevel, enable_chroma_deltaq, deltaq_mode (DELTA_Q_VARIANCE_BOOST), deltaq_strength
    public bool UsingQm;
    public int QmMinLevel = 4, QmMaxLevel = 10;
    public bool EnableChromaDeltaq;
    public bool DeltaqVarianceBoost;
    public int DeltaqStrength = 100;
    // oxcf.tune_cfg.dist_metric == AOM_DIST_METRIC_QM_PSNR
    public bool QmPsnrDistMetric;
    // oxcf.algo_cfg.enable_adaptive_sharpness
    public bool EnableAdaptiveSharpness;
    // oxcf.tool_cfg.cdef_control (CDEF_NONE 0, CDEF_ALL 1, CDEF_REFERENCE 2, CDEF_ADAPTIVE 3)
    public int CdefControl;

    // cm->delta_q_info
    public bool DeltaQPresentFlag;
    public int DeltaQRes;
    // cpi->deltaq_used
    public bool DeltaqUsed;

    // cpi->ssim_rdmult_scaling_factors (16x16 units)
    public double[]? SsimRdmultScalingFactors;

    /// <summary>set_rdmult(cpi, x, -1) (av1_get_cb_rdmult without TPL stats): the rdmult of the superblock's delta qindex.</summary>
    public int SetRdmultDeltaQ(AomMacroblock x)
        => ComputeRdMult(Cm.BaseQindex + x.RdmultDeltaQindex + Cm.YDcDeltaQ);

    /// <summary>av1_set_mb_ssim_rdmult_scaling: per 16x16 luma block the mean of its 8x8 per-pixel variances through
    /// the exponential SSIM model, normalised by the geometric mean over the frame.</summary>
    public void SetMbSsimRdmultScaling() => SetMbSsimRdmultScaling(Source, Cm.MiRows, Cm.MiCols, null);

    /// <summary>av1_set_mb_ssim_rdmult_scaling as libaom runs it before the frame size is set up: over cpi->source (the
    /// unscaled source) and the mi grid of cm as it is then (the previous frame's size), into the persistent factor
    /// buffer (entries past this frame's grid keep earlier frames' values).</summary>
    public void SetMbSsimRdmultScaling(AomFrameBuffer src, int miRows, int miCols, double[]? buffer)
    {
        const int numMiW = 4, numMiH = 4;   // BLOCK_16X16
        int numCols = (miCols + numMiW - 1) / numMiW, numRows = (miRows + numMiH - 1) / numMiH;
        var f = SsimRdmultScalingFactors = buffer ?? new double[numRows * numCols];
        double logSum = 0.0;
        byte[] buf = src.Buffers[0];
        int yOff = src.Offsets[0], yStride = src.Strides[0];
        for (int row = 0; row < numRows; ++row)
            for (int col = 0; col < numCols; ++col)
            {
                double var = 0.0, numOfVar = 0.0;
                int index = row * numCols + col;
                for (int miRow = row * numMiH; miRow < miRows && miRow < (row + 1) * numMiH; miRow += 2)
                    for (int miCol = col * numMiW; miCol < miCols && miCol < (col + 1) * numMiW; miCol += 2)
                    {
                        // av1_get_perpixel_variance_facade(BLOCK_8X8, AOM_PLANE_Y): variance vs. AV1_VAR_OFFS, rounded per pixel
                        if (src.Hbd)
                            var += AomHbd.PerpixelVariance(src.Buffers16[0], yOff + (miRow << 2) * yStride + (miCol << 2), yStride, 8, 8, src.BitDepth);
                        else
                        {
                            uint v = AomIntraModeSearch.VarianceVsZero(buf, yOff + (miRow << 2) * yStride + (miCol << 2), yStride, 8, 8, out _);
                            var += (v + 32) >> 6;
                        }
                        numOfVar += 1.0;
                    }
                var = var / numOfVar;
                var = 67.035434 * (1 - Math.Exp(-0.0021489 * var)) + 17.492222;
                f[index] = var;
                logSum += Math.Log(var);
            }
        logSum = Math.Exp(logSum / (double)(numRows * numCols));
        for (int i = 0; i < numRows * numCols; i++) f[i] /= logSum;
    }
}
