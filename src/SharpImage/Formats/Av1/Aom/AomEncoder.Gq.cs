using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal static partial class AomEncoder
{
    /// <summary>estimate_screen_content (AOM_SCREEN_DETECTION_STANDARD, the default outside all-intra / tune iq) on
    /// the unfiltered source's luma over its 8-aligned size. Returns (allow_screen_content_tools, allow_intrabc,
    /// is_screen_content_type).</summary>
    internal static (bool Sct, bool Intrabc, bool IsScreenContentType) EstimateScreenContentStandard(AomFrameBuffer src)
    {
        const int kBlockWidth = 16, kBlockHeight = 16, kBlockArea = kBlockWidth * kBlockHeight;
        const int kColorThresh = 4;
        const uint kVarThresh = 0;
        int width = (src.CropWidths[0] + 7) & ~7, height = (src.CropHeights[0] + 7) & ~7;   // y_width / y_height (aligned)
        long area = (long)width * height;
        int stride = src.Strides[0];
        bool useHbd = src.Hbd;
        int bd = src.BitDepth;
        Span<int> countBuf = stackalloc int[1 << 8];
        int[] valCount = useHbd ? new int[1 << bd] : Array.Empty<int>();
        long counts1 = 0, counts2 = 0;
        for (int r = 0; r + kBlockHeight <= height; r += kBlockHeight)
        {
            for (int c = 0; c + kBlockWidth <= width; c += kBlockWidth)
            {
                int off = src.Offsets[0] + r * stride + c;
                int nColors;
                if (useHbd) AomPalette.CountColorsHighbd(src.Buffers16[0], off, stride, kBlockHeight, kBlockWidth, bd, valCount, countBuf, out nColors);
                else nColors = AomPalette.CountColors(src.Buffers[0], off, stride, kBlockHeight, kBlockWidth, countBuf);
                if (nColors > 1 && nColors <= kColorThresh)
                {
                    ++counts1;
                    uint var = useHbd ? AomHbd.PerpixelVariance(src.Buffers16[0], off, stride, 16, 16, bd) : (uint)PerpixelVariance16x16(src.Buffers[0], off, stride);
                    if (var > kVarThresh) ++counts2;
                }
            }
        }
        bool sct = counts1 * kBlockArea * 10 > area;
        bool intrabc = sct && counts2 * kBlockArea * 12 > area;
        bool isSc = intrabc || (counts1 * kBlockArea * 10 > area * 4 && counts2 * kBlockArea * 30 > area);
        return (sct, intrabc, isSc);
    }
}
