using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>MSBuffers' compound inputs (second_pred, mask, mask_stride, inv_mask): the second predictor is W x H
/// contiguous (stride = block width).</summary>
internal sealed class AomCompoundRefs
{
    public byte[]? SecondPred8;
    public ushort[]? SecondPred16;
    public byte[]? Mask;
    public int MaskOffset, MaskStride;
    public bool InvMask;
}

internal sealed partial class AomFullPelMsParams
{
    public AomCompoundRefs? Comp;
}

internal sealed partial class AomSubpelMsParams
{
    public AomCompoundRefs? Comp;
}

// libaom 3.14.1 aom_dsp's compound predictors / masked costs for the compound motion searches: aom_comp_avg_pred,
// aom_comp_mask_pred, aom_sad*_avg, aom_masked_sad*, aom_sub_pixel_avg_variance*, aom_masked_sub_pixel_variance* (and
// their high bit depth versions with the fn_ptr's 10 / 12-bit normalisation).
internal static class AomCompound
{
    private static int BlendA64(int m, int a, int b) => (m * a + (64 - m) * b + 32) >> 6;

    /// <summary>aom_comp_avg_pred / aom_comp_mask_pred of a W x H block (stride w in pred) with the second predictor,
    /// into dst (stride w).</summary>
    public static void CompPred(AomCompoundRefs c, byte[] pred, int predOff, int predStride, int w, int h, byte[] dst)
    {
        var sp = c.SecondPred8!;
        if (c.Mask != null)
        {
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    int m = c.Mask[c.MaskOffset + i * c.MaskStride + j];
                    int r = pred[predOff + i * predStride + j], s = sp[i * w + j];
                    // src0 = invert ? pred(second) : ref, src1 = invert ? ref : pred(second)
                    dst[i * w + j] = (byte)(c.InvMask ? BlendA64(m, s, r) : BlendA64(m, r, s));
                }
            return;
        }
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++) dst[i * w + j] = (byte)((pred[predOff + i * predStride + j] + sp[i * w + j] + 1) >> 1);
    }

    public static void CompPred(AomCompoundRefs c, ushort[] pred, int predOff, int predStride, int w, int h, ushort[] dst)
    {
        var sp = c.SecondPred16!;
        if (c.Mask != null)
        {
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    int m = c.Mask[c.MaskOffset + i * c.MaskStride + j];
                    int r = pred[predOff + i * predStride + j], s = sp[i * w + j];
                    dst[i * w + j] = (ushort)(c.InvMask ? BlendA64(m, s, r) : BlendA64(m, r, s));
                }
            return;
        }
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++) dst[i * w + j] = (ushort)((pred[predOff + i * predStride + j] + sp[i * w + j] + 1) >> 1);
    }

    /// <summary>get_mvpred_compound_sad with a second predictor: vfp->msdf (masked SAD) or vfp->sdaf (SAD of the average).</summary>
    public static uint Sad(AomCompoundRefs c, in AomBuf2d src, in AomBuf2d reff, int refOff, int w, int h, int bd)
    {
        if (src.Buf16 != null)
        {
            var comp = new ushort[w * h];
            CompPred(c, reff.Buf16!, refOff, reff.Stride, w, h, comp);
            ulong sad = 0;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++) sad += (ulong)Math.Abs(comp[i * w + j] - src.Buf16[src.Offset + i * src.Stride + j]);
            return (uint)(sad >> (bd - 8));
        }
        var comp8 = new byte[w * h];
        CompPred(c, reff.Buf, refOff, reff.Stride, w, h, comp8);
        return AomSad.Sad(src.Buf, src.Offset, src.Stride, comp8, 0, w, w, h);
    }

    /// <summary>The compound prediction of a W x H block against the source: its variance (vfp->vf on the compound).</summary>
    public static uint VarianceOfComp(AomCompoundRefs c, byte[]? pred8, ushort[]? pred16, int predOff, int predStride, in AomBuf2d src, int w, int h,
        int bd, out uint sse)
    {
        if (pred16 != null)
        {
            var comp = new ushort[w * h];
            CompPred(c, pred16, predOff, predStride, w, h, comp);
            return AomHbd.Variance(comp, 0, w, src.Buf16, src.Offset, src.Stride, 0, w, h, bd, out sse);
        }
        var comp8 = new byte[w * h];
        CompPred(c, pred8!, predOff, predStride, w, h, comp8);
        return AomSad.Variance(comp8, 0, w, src.Buf, src.Offset, src.Stride, w, h, out sse);
    }
}
