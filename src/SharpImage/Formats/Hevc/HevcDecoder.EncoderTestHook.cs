// Internal test hook: decodes a single residual_coding block in isolation so the residual ENCODER
// can be round-trip-verified against the decoder. Not used by the normal decode path.
using System;

namespace SharpImage.Formats.Hevc;

internal sealed partial class HevcDecoder
{
    internal short[] TestDecodeResidual(byte[] cabacBytes, HevcSequenceParameterSet sps, HevcPictureParameterSet pps,
        int log2TrafoSize, int scanIdx, int cIdx, int sliceQp)
    {
        currentPredMode = HevcPredictionMode.Intra;
        currentCuTransquantBypass = false;
        currentTuIntraPredMode = 0;
        currentTuIntraPredModeC = 0;
        Array.Clear(statCoeff, 0, statCoeff.Length);

        Span<byte> ctx = stackalloc byte[HevcCabacContextIndex.TotalContexts];
        var cabac = new HevcCabacDecoder(cabacBytes, ctx, sliceQp, 0);
        int n = 1 << log2TrafoSize;
        var residual = new short[n * n];
        DecodeResidualCoding(ref cabac, sps, pps, 0, 0, log2TrafoSize, scanIdx, cIdx, residual, out _, out _);
        return residual;
    }
}
