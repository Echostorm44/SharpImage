using System;
using static SharpImage.Formats.Av1.AomTables;
using SharpImage.Core;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomEncodeInput
{
    /// <summary>One of av1_determine_sc_tools_with_encoding's quick encodes: the speed features come from SfChain and
    /// no further trial runs.</summary>
    internal bool ScTrialPass;
    /// <summary>is_lossless_requested (rc best/worst allowed q both 0); null: the frame q is 0.</summary>
    internal bool? LosslessRequested;
    /// <summary>Replaces the speed-feature setup (cpi->sf persists across the trial encodes in libaom).</summary>
    internal Action<AomSpeedFeatures, AomWinnerModeParams>? SfChain;
    /// <summary>encode_with_recode_loop's copy_frame_prob_info already ran (before the first trial encode).</summary>
    internal bool SkipCopyFrameProbInfo;
    /// <summary>The base qindex the default CDFs were chosen for (av1_setup_frame before the trial's av1_set_quantizer).</summary>
    internal int? CdfQindex;
    /// <summary>cm->quant_params.base_qindex when the frame starts: the last av1_set_quantizer's (the previous encoded
    /// frame's q; 0 before the first frame). The trial's av1_setup_frame picks the default CDFs by it.</summary>
    internal int PrevBaseQindex;

    internal AomEncodeInput CloneForScTrial() => (AomEncodeInput)MemberwiseClone();
}

internal sealed partial class AomComp
{
    /// <summary>cpi->palette_pixel_num (x->palette_pixels summed over the threads).</summary>
    public int PalettePixelNum;
}

// libaom 3.14.1 encoder_utils.c av1_determine_sc_tools_with_encoding (with set_encoding_params_for_screen_content and
// screen_content_tools_determination), run by encode_with_recode_loop for key frames.
internal static partial class AomEncoder
{
    private const double STRICT_PSNR_DIFF_THRESH = 0.9;

    /// <summary>Whether encode_with_recode_loop runs the screen content trial: recode allowed (encode_with_recode_loop
    /// is used), !disable_extra_sc_testing, a key frame without screen content tools, not real-time / non-RD, no
    /// forward key frames or superres.</summary>
    private static bool ScTrialWanted(AomComp cpi, AomEncodeInput input)
        => !input.ScTrialPass && cpi.Sf.hl_sf.recode_loop != DISALLOW_RECODE && cpi.Sf.hl_sf.disable_extra_sc_testing == 0 &&
           cpi.Sf.rt_sf.use_nonrd_pick_mode == 0 && input.Mode != REALTIME && !input.UseScreenContentTools && input.GfFrameType == KEY_FRAME;

    /// <summary>av1_determine_sc_tools_with_encoding: two fast encodes (fixed 32x32 partition, q at least 244)
    /// without and with screen content tools; enables the tools when the PSNR gain is large. Leaves cpi->sf as libaom
    /// does (the framesize sf of the original decision, the qindex-dependent sf re-run for both trial q's and then
    /// the frame's q with the final decision).</summary>
    private static void DetermineScToolsWithEncoding(AomComp cpi, AomEncodeInput input, AomSpeedFeatureInputs sfIn, AomSpeedFeatureSeqFlags seqFlags)
    {
        var cm = cpi.Cm;
        bool origAllow = input.AllowScreenContentTools, origIbc = input.AllowIntrabc, origUse = input.UseScreenContentTools;
        bool origIsSc = input.IsScreenContentType;
        bool losslessRequested = sfIn.BestAllowedQ == 0 && sfIn.WorstAllowedQ == 0;
        int qSc = losslessRequested ? input.BaseQindex : Math.Max(input.BaseQindex, 244);
        int speed = input.Speed;
        var qdepInputs = new AomSpeedFeatureInputs[2];
        var psnr = new double[2];
        int palettePixelNum = 0;
        bool intrabcUsed = false;
        bool trialDeltaQPresent = false;
        for (int pass = 0; pass < 2; pass++)
        {
            // set_encoding_params_for_screen_content
            var t = input.CloneForScTrial();
            t.ScTrialPass = true;
            t.DetectScreenContent = false;
            t.AllowScreenContentTools = t.UseScreenContentTools = pass == 1;
            t.AllowIntrabc = false;
            t.IsScreenContentType = origIsSc;
            t.BaseQindex = qSc;
            t.CdfQindex = input.PrevBaseQindex;
            t.SkipCopyFrameProbInfo = pass > 0;
            var q = sfIn.Clone();
            q.AllowScreenContentTools = q.UseScreenContentTools = pass == 1;
            q.BaseQindex = qSc;
            qdepInputs[pass] = q;
            int upto = pass;
            t.SfChain = (sf, wmp) =>
            {
                sf.SetFramesizeIndependent(sfIn, seqFlags, wmp, speed);
                sf.SetFramesizeDependent(sfIn, seqFlags, speed);
                for (int k = 0; k <= upto; k++) sf.SetQindexDependent(qdepInputs[k], wmp, speed);
                sf.part_sf.partition_search_type = FIXED_PARTITION;
                sf.part_sf.fixed_partition_size = BLOCK_32X32;
            };
            var (tc, _) = EncodeFrame(t);
            psnr[pass] = CalcPsnrTotal(tc.Source!, tc.Cm.CurFrame, cm.NumPlanes);
            if (pass == 1) { palettePixelNum = tc.PalettePixelNum; intrabcUsed = tc.IntrabcUsed; }
            trialDeltaQPresent = tc.DeltaQPresentFlag;
            tc.Cm.Release();
            AomBufferPool.Return(tc.ExtCbOffset);
            foreach (var b in tc.CbCoeffBuffers) AomCbCoeffBuffer.Return(b);
        }

        // screen_content_tools_determination
        double psnrDiff = psnr[1] - psnr[0];
        double paletteRatio = (double)palettePixelNum / (double)(cm.Height * cm.Width);
        bool psnrDiffIsLarge = psnrDiff > STRICT_PSNR_DIFF_THRESH;
        bool ratioIsLarge = paletteRatio >= 0.0001 && psnrDiff / paletteRatio > 4;
        if (psnrDiffIsLarge || ratioIsLarge)
        {
            input.AllowScreenContentTools = true;
            input.AllowIntrabc = intrabcUsed;
            input.UseScreenContentTools = true;
            input.IsScreenContentType = true;
        }
        else
        {
            input.AllowScreenContentTools = origAllow;
            input.AllowIntrabc = origIbc;
            input.UseScreenContentTools = origUse;
            input.IsScreenContentType = origIsSc;
        }
        cpi.AllowScreenContentTools = input.AllowScreenContentTools;
        cpi.UseScreenContentTools = input.UseScreenContentTools;
        cpi.AllowIntrabc = input.AllowIntrabc & input.EnableIntrabc;

        // cpi->sf after the trial: the partition search restored, the qindex-dependent sf of the trial q's then the
        // frame's (encode_with_recode_loop's av1_set_speed_features_qindex_dependent)
        // encode_with_recode_loop's av1_set_quantizer: base_qindex = max(delta_q_present_flag of the last trial, q)
        if (trialDeltaQPresent && input.BaseQindex == 0) cm.BaseQindex = input.BaseQindex = 1;
        var fin = sfIn.Clone();
        fin.BaseQindex = input.BaseQindex;
        fin.AllowScreenContentTools = input.AllowScreenContentTools;
        fin.UseScreenContentTools = input.UseScreenContentTools;
        fin.IsScreenContentType = input.IsScreenContentType;
        var wmp0 = cpi.WinnerModeParams;
        cpi.Sf.SetFramesizeIndependent(sfIn, seqFlags, wmp0, speed);
        cpi.Sf.SetFramesizeDependent(sfIn, seqFlags, speed);
        cpi.Sf.SetQindexDependent(qdepInputs[0], wmp0, speed);
        cpi.Sf.SetQindexDependent(qdepInputs[1], wmp0, speed);
        cpi.Sf.SetQindexDependent(fin, wmp0, speed);
        input.SkipCopyFrameProbInfo = true;
    }

    /// <summary>aom_calc_highbd_psnr / aom_calc_psnr's psnr[0] (all planes, peak at the stream bit depth).</summary>
    /// <remarks>aom_calc_psnr always sums three planes: a monochrome frame's chroma planes are never written by libaom
    /// (the lookahead copy, the filters and the reconstruction skip them) and stay at their zeroed allocation, so they
    /// add samples with no error.</remarks>
    private static double CalcPsnrTotal(AomFrameBuffer a, AomFrameBuffer b, int numPlanes)
    {
        double peak = (1 << a.BitDepth) - 1;
        ulong totalSse = 0;
        uint totalSamples = 0;
        for (int p = 0; p < 3; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int w = a.CropWidths[isUv], h = a.CropHeights[isUv];
            ulong sse = 0;
            if (p >= numPlanes) h = 0;
            for (int r = 0; r < h; r++)
            {
                int ao = a.Offsets[p] + r * a.Strides[p], bo = b.Offsets[p] + r * b.Strides[p];
                if (a.Hbd)
                    for (int c = 0; c < w; c++) { long d = a.Buffers16[p][ao + c] - b.Buffers16[p][bo + c]; sse += (ulong)(d * d); }
                else
                    for (int c = 0; c < w; c++) { long d = a.Buffers[p][ao + c] - b.Buffers[p][bo + c]; sse += (ulong)(d * d); }
            }
            totalSse += sse;
            totalSamples += (uint)(w * (p >= numPlanes ? a.CropHeights[isUv] : h));
        }
        return SseToPsnr(totalSamples, peak, totalSse);
    }

    private static double SseToPsnr(double samples, double peak, double sse)
    {
        if (sse > 0.0)
        {
            double psnr = 10.0 * PortableMathD.Log10(samples * peak * peak / sse);
            return psnr > 100.0 ? 100.0 : psnr;
        }
        return 100.0;
    }
}
