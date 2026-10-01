using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>The AV1_COMP / AV1_COMMON state the ported search reads (cpi->sf, cpi->oxcf, cm->features, frame probs).
/// Grows as the port does; fields keep libaom's names in PascalCase with the owning struct noted.</summary>
internal sealed partial class AomComp
{
    public readonly AomSpeedFeatures Sf = new();

    // cm->seq_params: sb_size, enable_intra_edge_filter
    public int SbSize = BLOCK_64X64;
    public bool EnableIntraEdgeFilter = true;
    // oxcf.txfm_cfg
    public bool EnableTx64 = true, EnableRectTx = true, EnableFlipIdtx = true, UseIntraDctOnly;
    // oxcf.q_cfg / algo_cfg / tune_cfg
    public int QuantBAdapt;
    public int Sharpness;
    // oxcf.tune_cfg.tuning
    public AomTune Tune;
    public bool TuneIq => Tune == AomTune.Iq;
    /// <summary>AOM_TUNE_SSIM / AOM_TUNE_IQ: av1_set_mb_ssim_rdmult_scaling and av1_set_ssim_rdmult.</summary>
    public bool SsimRdmult => Tune is AomTune.Ssim or AomTune.Iq;
    // cpi->use_screen_content_tools, cm->features.reduced_tx_set_used
    public bool UseScreenContentTools;
    public int ReducedTxSetUsed;
    // cpi->optimize_seg_arr (TRELLIS_OPT_TYPE per segment)
    public readonly int[] OptimizeSegArr = new int[8];
    // gf_group update type of the current frame; cpi->ppi->frame_probs.tx_type_probs [FRAME_UPDATE_TYPES][TX_SIZES_ALL][TX_TYPES]
    public int UpdateType = KF_UPDATE;
    public readonly int[] TxTypeProbs = (int[])DefaultTxTypeProbs.Clone();

    /// <summary>av1_dc_quant_QTX.</summary>
    public static int DcQuantQtx(int qindex, int delta, int bitDepth)
        => Av1Tables.DequantTable[bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2, Math.Clamp(qindex + delta, 0, 255), 0];

    /// <summary>frame_probs.tx_type_probs[update_type][tx_size] row offset.</summary>
    public int TxTypeProbsOffset(int txSize) => (UpdateType * 19 + txSize) * 16;
}
