using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomMbdPlane
{
    /// <summary>pd->pre[0]: the reference of the block (for intrabc, the current frame at the block position).</summary>
    public AomBuf2d Pre0;
}

// Port of libaom 3.14.1 av1_enc_build_inter_predictor (reconinter_enc.c / reconinter_template.inc /
// reconinter.{c,h}) for intrabc blocks: build_inter_predictors_8x8_and_bigger with the current frame as reference
// (is_sub8x8_inter is always false for intrabc, so a sub-8x8 chroma block is predicted whole with this block's DV),
// init_subpel_params (unscaled), and av1_convolve_2d_facade's intrabc kernels (av1_convolve_{2d,x,y}_sr_intrabc_c,
// the bilinear half-sample average libaom has no SIMD for) or the plain copy for integer positions.
internal static class AomReconInter
{
    private const int SUBPEL_BITS = 4, SCALE_SUBPEL_BITS = 10, SCALE_EXTRA_BITS = SCALE_SUBPEL_BITS - SUBPEL_BITS;
    private const int SCALE_EXTRA_OFF = (1 << SCALE_EXTRA_BITS) / 2, SCALE_SUBPEL_MASK = (1 << SCALE_SUBPEL_BITS) - 1;
    private const int AOM_INTERP_EXTEND = 4, AOM_BORDER_IN_PIXELS = 288;

    /// <summary>AOM_LEFT_TOP_MARGIN_SCALED(ss).</summary>
    private static int LeftTopMarginScaled(int ss) => ((AOM_BORDER_IN_PIXELS >> ss) - AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;

    /// <summary>av1_enc_build_inter_predictor for an intrabc block (planes planeFrom..planeTo).</summary>
    public static void BuildIntrabcPredictor(AomCommon cm, AomMacroblockD xd, int miRow, int miCol, int planeFrom, int planeTo)
    {
        var mi = xd.Mi0;
        var cur = cm.CurFrame;
        for (int plane = planeFrom; plane <= planeTo; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            var pd = xd.Plane[plane];
            int ssX = pd.SubsamplingX, ssY = pd.SubsamplingY;
            int bw = pd.Width, bh = pd.Height;
            int miX = miCol * 4, miY = miRow * 4;
            int bsize = mi.Bsize;
            int rowStart = BlockSizeHigh[bsize] == 4 && ssY != 0 ? -1 : 0;
            int colStart = BlockSizeWide[bsize] == 4 && ssX != 0 ? -1 : 0;
            int preX = (miX + 4 * colStart) >> ssX, preY = (miY + 4 * rowStart) >> ssY;
            // init_subpel_params (unscaled: av1_unscaled_value)
            int origPosY = (preY << SUBPEL_BITS) + mi.Mv0.Row * (1 << (1 - ssY));
            int origPosX = (preX << SUBPEL_BITS) + mi.Mv0.Col * (1 << (1 - ssX));
            int posY = origPosY * (1 << SCALE_EXTRA_BITS) + SCALE_EXTRA_OFF;
            int posX = origPosX * (1 << SCALE_EXTRA_BITS) + SCALE_EXTRA_OFF;
            int isUv = plane > 0 ? 1 : 0;
            int bottom = (cur.CropHeights[isUv] + AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;
            int right = (cur.CropWidths[isUv] + AOM_INTERP_EXTEND) << SCALE_SUBPEL_BITS;
            posY = Math.Clamp(posY, -LeftTopMarginScaled(ssY), bottom);
            posX = Math.Clamp(posX, -LeftTopMarginScaled(ssX), right);
            int subpelX = (posX & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS, subpelY = (posY & SCALE_SUBPEL_MASK) >> SCALE_EXTRA_BITS;
            byte[] buf = cur.Buffers[plane];
            int stride = cur.Strides[plane];
            int src = cur.Offsets[plane] + (posY >> SCALE_SUBPEL_BITS) * stride + (posX >> SCALE_SUBPEL_BITS);
            var dst = pd.Dst;
            Convolve(buf, src, stride, dst.Buf, dst.Offset, dst.Stride, bw, bh, subpelX, subpelY);
        }
    }

    /// <summary>av1_convolve_2d_facade with the intrabc filters (2-tap bilinear at half positions) / aom_convolve_copy.</summary>
    private static void Convolve(byte[] s, int so, int ss, byte[] d, int dOff, int ds, int w, int h, int subpelX, int subpelY)
    {
        if (subpelX != 0 && subpelY != 0)
        {
            // av1_convolve_2d_sr_intrabc_c (subpel 8 both ways)
            const int bd = 8;
            var im = new int[(h + 1) * w];
            for (int y = 0; y < h + 1; ++y)
                for (int x = 0; x < w; ++x)
                    im[y * w + x] = (1 << bd) + s[so + y * ss + x] + s[so + y * ss + x + 1];
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                {
                    int sum = (1 << (bd + 2)) + im[y * w + x] + im[(y + 1) * w + x];
                    int res = ((sum + 2) >> 2) - ((1 << bd) + (1 << (bd - 1)));
                    d[dOff + y * ds + x] = (byte)Math.Clamp(res, 0, 255);
                }
        }
        else if (subpelX != 0)
        {
            // av1_convolve_x_sr_intrabc_c
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                    d[dOff + y * ds + x] = (byte)Math.Clamp((s[so + y * ss + x] + s[so + y * ss + x + 1] + 1) >> 1, 0, 255);
        }
        else if (subpelY != 0)
        {
            // av1_convolve_y_sr_intrabc_c
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                    d[dOff + y * ds + x] = (byte)Math.Clamp((s[so + y * ss + x] + s[so + (y + 1) * ss + x] + 1) >> 1, 0, 255);
        }
        else
        {
            for (int y = 0; y < h; ++y) Array.Copy(s, so + y * ss, d, dOff + y * ds, w);
        }
    }
}
