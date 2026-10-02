using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/common/resize.c (av1_realloc_and_scale_if_required, av1_resize_and_extend_frame_c,
// av1_has_optimized_scaler) and aom_dsp/aom_convolve.c (aom_scaled_2d_c): the encoder's source / reference scaler.
internal static class AomResize
{
    /// <summary>av1_has_optimized_scaler: the scaler handles ratios from 1/4 to 16 (and the 3/4 SIMD special case is
    /// identical to C).</summary>
    public static bool HasOptimizedScaler(int srcW, int srcH, int dstW, int dstH)
    {
        bool hasOptimizedScaler = (dstW * 4 >= srcW) && (dstH * 4 >= srcH);
        if (hasOptimizedScaler) hasOptimizedScaler = (dstW <= srcW * 16) && (dstH <= srcH * 16);
        return hasOptimizedScaler;
    }

    /// <summary>av1_realloc_and_scale_if_required (use_optimized_scaler): <paramref name="unscaled"/> scaled to
    /// width x height, or itself when the sizes match.</summary>
    public static AomFrameBuffer ScaleIfRequired(AomFrameBuffer unscaled, int width, int height, int filter, int phase, bool useOptimizedScaler)
    {
        if (width == unscaled.CropWidths[0] && height == unscaled.CropHeights[0]) return unscaled;
        var scaled = new AomFrameBuffer(width, height, unscaled.SsX, unscaled.SsY, unscaled.NumPlanes == 1, unscaled.BitDepth);
        bool opt = HasOptimizedScaler(unscaled.CropWidths[0], unscaled.CropHeights[0], width, height);
        if (unscaled.NumPlanes > 1)
            opt = opt && HasOptimizedScaler(unscaled.CropWidths[1], unscaled.CropHeights[1], scaled.CropWidths[1], scaled.CropHeights[1]);
        if (useOptimizedScaler && opt && unscaled.BitDepth == 8) ResizeAndExtendFrame(unscaled, scaled, filter, phase);
        else throw new NotImplementedException("av1_resize_and_extend_frame_nonnormative");
        return scaled;
    }

    private static bool HasNormativeScalerSsse3(int sw, int sh, int dw, int dh)
        => (2 * dw == sw && 2 * dh == sh) || (4 * dw == sw && 4 * dh == sh) || (4 * dw == 3 * sw && 4 * dh == 3 * sh) || (dw == sw * 2 && dh == sh * 2);

    /// <summary>av1_resize_and_extend_frame (the SSSE3 dispatch libaom runs): the 2x upscale takes
    /// scale_plane_1_to_2_phase_0 (the half-pel kernel, the phase ignored); the other ratios match the C scaler.</summary>
    public static void ResizeAndExtendFrame(AomFrameBuffer src, AomFrameBuffer dst, int filter, int phaseScaler)
    {
        bool normative = HasNormativeScalerSsse3(src.CropWidths[0], src.CropHeights[0], dst.CropWidths[0], dst.CropHeights[0]);
        if (src.NumPlanes > 1)
            normative = normative && HasNormativeScalerSsse3(src.CropWidths[1], src.CropHeights[1], dst.CropWidths[1], dst.CropHeights[1]);
        if (normative && dst.CropWidths[0] == 2 * src.CropWidths[0] && dst.CropHeights[0] == 2 * src.CropHeights[0])
        {
            var kernel = AomFilter.ParamsList[filter].Filter;
            for (int i = 0; i < Math.Min(src.NumPlanes, 3); ++i)
            {
                int isUv = i > 0 ? 1 : 0;
                ScalePlane1To2Phase0(src.Buffers[i], src.Offsets[i], src.Strides[i], dst.Buffers[i], dst.Offsets[i], dst.Strides[i],
                    src.CropWidths[isUv], src.CropHeights[isUv], kernel, 8 * 8);
            }
            ExtendFrameBorders(dst);
            return;
        }
        ResizeAndExtendFrameC(src, dst, filter, phaseScaler);
    }

    /// <summary>convolve8_8_ssse3 on one lane: pmaddubsw pairs (saturating), (x0 + x2) + 64 then a saturating add of
    /// (x1 + x3), arithmetic shift by 7, packus.</summary>
    private static byte Conv8Ssse3(ReadOnlySpan<int> p, short[] k, int ko)
    {
        static int Sat16(int v) => v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : v;
        int x0 = Sat16(p[0] * (sbyte)k[ko] + p[1] * (sbyte)k[ko + 1]);
        int x1 = Sat16(p[2] * (sbyte)k[ko + 2] + p[3] * (sbyte)k[ko + 3]);
        int x2 = Sat16(p[4] * (sbyte)k[ko + 4] + p[5] * (sbyte)k[ko + 5]);
        int x3 = Sat16(p[6] * (sbyte)k[ko + 6] + p[7] * (sbyte)k[ko + 7]);
        int sum1 = (short)(x0 + x2);
        int sum2 = (short)(x1 + x3);
        sum1 = (short)(sum1 + 64);
        sum1 = Sat16(sum1 + sum2) >> 7;
        return ClipPixel(sum1);
    }

    /// <summary>scale_plane_1_to_2_phase_0 (resize_ssse3.c): even rows / columns copy the source, the others are the
    /// half-pel kernel's horizontal, vertical and 2-D (through 8-bit rows) interpolations.</summary>
    private static void ScalePlane1To2Phase0(byte[] src, int sOff, int sStride, byte[] dst, int dOff, int dStride, int srcW, int srcH,
        short[] kernel, int ko)
    {
        int maxWidth = (srcW + 7) & ~7;
        var tmp = new byte[9][];
        for (int i = 0; i < 9; i++) tmp[i] = new byte[maxWidth];
        var pix = new int[8];
        void Row(int rowOff, byte[] outRow)
        {
            for (int x = 0; x < maxWidth; x++)
            {
                for (int k = 0; k < 8; k++) pix[k] = src[rowOff - 3 + x + k];
                outRow[x] = Conv8Ssse3(pix, kernel, ko);
            }
        }
        for (int r = 0; r < 7; r++) Row(sOff + (r - 3) * sStride, tmp[r]);
        int s = sOff, d = dOff;
        for (int y = srcH; y > 0; y--)
        {
            Row(s + 4 * sStride, tmp[7]);
            for (int x = 0; x < maxWidth; x++)
            {
                // even rows: the source sample and the horizontal half-pel sample
                dst[d + 2 * x] = src[s + x];
                dst[d + 2 * x + 1] = tmp[3][x];
                // odd rows: the vertical half-pel sample and the 2-D one
                for (int k = 0; k < 8; k++) pix[k] = src[s + x + (k - 3) * sStride];
                dst[d + dStride + 2 * x] = Conv8Ssse3(pix, kernel, ko);
                for (int k = 0; k < 8; k++) pix[k] = tmp[k][x];
                dst[d + dStride + 2 * x + 1] = Conv8Ssse3(pix, kernel, ko);
            }
            s += sStride;
            d += 2 * dStride;
            var t8 = tmp[0];
            for (int k = 0; k < 7; k++) tmp[k] = tmp[k + 1];
            tmp[7] = t8;
        }
    }

    /// <summary>av1_resize_and_extend_frame_c.</summary>
    public static void ResizeAndExtendFrameC(AomFrameBuffer src, AomFrameBuffer dst, int filter, int phaseScaler)
    {
        var kernel = AomFilter.ParamsList[filter].Filter;
        for (int i = 0; i < Math.Min(src.NumPlanes, 3); ++i)
        {
            int isUv = i > 0 ? 1 : 0;
            int srcW = src.CropWidths[isUv], srcH = src.CropHeights[isUv];
            int dstW = dst.CropWidths[isUv], dstH = dst.CropHeights[isUv];
            var sb = src.Buffers[i]; int sOff = src.Offsets[i], sStride = src.Strides[i];
            var db = dst.Buffers[i]; int dOff = dst.Offsets[i], dStride = dst.Strides[i];
            for (int y = 0; y < dstH; y += 16)
            {
                int yQ4 = srcH == dstH ? 0 : y * 16 * srcH / dstH + phaseScaler;
                for (int x = 0; x < dstW; x += 16)
                {
                    int xQ4 = srcW == dstW ? 0 : x * 16 * srcW / dstW + phaseScaler;
                    int srcPtr = sOff + y * srcH / dstH * sStride + x * srcW / dstW;
                    int dstPtr = dOff + y * dStride + x;
                    int workW = Math.Min(16, dstW - x), workH = Math.Min(16, dstH - y);
                    Scaled2d(sb, srcPtr, sStride, db, dstPtr, dStride, kernel, xQ4 & 0xf, 16 * srcW / dstW, yQ4 & 0xf, 16 * srcH / dstH, workW, workH);
                }
            }
        }
        ExtendFrameBorders(dst);
    }

    /// <summary>aom_scaled_2d_c: horizontal pass into a 64 x 135 temp, then vertical.</summary>
    public static void Scaled2d(byte[] src, int srcOff, int srcStride, byte[] dst, int dstOff, int dstStride, short[] filter,
        int x0Q4, int xStepQ4, int y0Q4, int yStepQ4, int w, int h)
    {
        Span<byte> temp = stackalloc byte[64 * 135];
        int intermediateHeight = (((h - 1) * yStepQ4 + y0Q4) >> AomFilter.SUBPEL_BITS) + AomFilter.SUBPEL_TAPS;
        // convolve_horiz(src - src_stride * (SUBPEL_TAPS / 2 - 1), src_stride, temp, 64, ...)
        int s0 = srcOff - srcStride * (AomFilter.SUBPEL_TAPS / 2 - 1) - (AomFilter.SUBPEL_TAPS / 2 - 1);
        for (int y = 0; y < intermediateHeight; ++y)
        {
            int xQ4 = x0Q4;
            int row = s0 + y * srcStride;
            for (int x = 0; x < w; ++x)
            {
                int sx = row + (xQ4 >> AomFilter.SUBPEL_BITS);
                int k = (xQ4 & AomFilter.SUBPEL_MASK) * 8;
                int sum = 0;
                for (int t = 0; t < 8; ++t) sum += src[sx + t] * filter[k + t];
                temp[y * 64 + x] = ClipPixel((sum + 64) >> AomFilter.FILTER_BITS);
                xQ4 += xStepQ4;
            }
        }
        // convolve_vert(temp + 64 * (SUBPEL_TAPS / 2 - 1), 64, dst, ...): src -= stride * (SUBPEL_TAPS / 2 - 1) cancels
        for (int x = 0; x < w; ++x)
        {
            int yQ4 = y0Q4;
            for (int y = 0; y < h; ++y)
            {
                int sy = (yQ4 >> AomFilter.SUBPEL_BITS) * 64 + x;
                int k = (yQ4 & AomFilter.SUBPEL_MASK) * 8;
                int sum = 0;
                for (int t = 0; t < 8; ++t) sum += temp[sy + t * 64] * filter[k + t];
                dst[dstOff + y * dstStride + x] = ClipPixel((sum + 64) >> AomFilter.FILTER_BITS);
                yQ4 += yStepQ4;
            }
        }
    }

    private static byte ClipPixel(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>aom_extend_frame_borders: every plane's crop area replicated into the whole border.</summary>
    public static void ExtendFrameBorders(AomFrameBuffer f)
    {
        for (int p = 0; p < f.NumPlanes; p++)
        {
            int isUv = p > 0 ? 1 : 0;
            int bx = p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> f.SsX;
            int by = p == 0 ? AomFrameBuffer.Border : AomFrameBuffer.Border >> f.SsY;
            if (f.Hbd) AomEncoder.ExtendPlane(f.Buffers16[p], f.Offsets[p], f.Strides[p], f.CropWidths[isUv], f.CropHeights[isUv], bx, by);
            else AomEncoder.ExtendPlane(f.Buffers[p], f.Offsets[p], f.Strides[p], f.CropWidths[isUv], f.CropHeights[isUv], bx, by);
        }
    }
}
