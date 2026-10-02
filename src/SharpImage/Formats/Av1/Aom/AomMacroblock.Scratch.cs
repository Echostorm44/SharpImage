namespace SharpImage.Formats.Av1;

// Per-thread scratch of the search (each worker has its own AomMacroblock): fields instead of [ThreadStatic] statics,
// whose every access is a runtime helper call on the hot paths.
internal sealed partial class AomMacroblock
{
    public readonly byte[] ScratchRecon = new byte[64 * 64];          // dist_block_px_domain's recon
    public ushort[]? ScratchRecon16;                                   // its high bit depth form
    public AomRdcostBlockArgs? ScratchRdArgs;                          // av1_txfm_rd_in_plane's args
    public readonly int[] ScratchCoeff = new int[64 * 64];            // intra_model_rd's coefficients
    public readonly byte[] ScratchLevels = new byte[AomTxb.TxPad2d];  // av1_update_and_record_txb_context
    public readonly sbyte[] ScratchCoeffContexts = new sbyte[64 * 64];
    public AomMbModeInfo? ScratchBestMbmi, ScratchBestUvMbmi;
    public AomRdStats[]? ScratchCflU, ScratchCflV;
    public readonly byte[] ScratchTa = new byte[32], ScratchTl = new byte[32];
}
