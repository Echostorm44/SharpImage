// AV1 intra prediction modes for the decoder
// Ported from dav1d: src/ipred_tmpl.c + src/ipred_prepare_tmpl.c (VideoLAN dav1d, BSD-2-Clause)
// Implements DC, V, H, Paeth, Smooth, directional (Z1/Z2/Z3), filter intra,
// chroma-from-luma (CFL), and palette prediction.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 intra prediction. All prediction functions take a topleft buffer where
/// index 0 = top-left corner sample, positive indices = top/top-right samples,
/// negative indices = left/bottom-left samples (stored at <c>topleft[centerOffset - i]</c>).
/// The <see cref="PrepareIntraEdges"/> method builds this buffer from the
/// reconstruction surface.
/// </summary>
public static class Av1IntraPred
{
    // Temporary debug flag — set to true from reconstruction for the first error block
    public static bool DbgZ2;
    public static bool DbgSmoothVDump;

    // ========================================================================
    // Edge preparation
    // ========================================================================

    /// <summary>
    /// Intra prediction mode after edge availability mapping (implementation modes).
    /// These extend the AV1 standard intra modes with derived directional variants.
    /// </summary>
    public enum ImplPredMode
    {
        Dc = 0,
        Vert,
        Hor,
        DiagDownLeft,
        DiagDownRight,
        VertRight,
        HorDown,
        HorUp,
        VertLeft,
        Paeth,
        Smooth,
        SmoothV,
        SmoothH,
        // Implementation-only modes:
        LeftDc,
        TopDc,
        Dc128,
        Z1,
        Z2,
        Z3,
        Filter,
        Count
    }

    /// <summary>
    /// Map from standard intra mode + angle delta → angle in degrees.
    /// Modes 0-7 (V..VL) map to base angles; delta is added as 3× step.
    /// </summary>
    private static ReadOnlySpan<byte> ModeToAngle => new byte[]
    {
        90, 180, 45, 135, 113, 157, 203, 67
    };

    /// <summary>
    /// Mode conversion for DC/Paeth when left or top edges are unavailable.
    /// [mode][haveLeft][haveTop] → implementation mode.
    /// Only DC (0) and Paeth (9) have fallbacks.
    /// </summary>
    private static readonly ImplPredMode[,,] ModeConversion = new ImplPredMode[,,,]
    {
        // Unused outer dimension to simplify indexing
    }.Length == 0 ? InitModeConversion() : InitModeConversion();

    private static ImplPredMode[,,] InitModeConversion()
    {
        // [2][2][2] — [mode_idx (0=DC, 1=Paeth)][haveLeft][haveTop]
        var table = new ImplPredMode[2, 2, 2];
        // DC: no_left+no_top=128, no_left+top=TopDC, left+no_top=LeftDC, both=DC
        table[0, 0, 0] = ImplPredMode.Dc128;
        table[0, 0, 1] = ImplPredMode.TopDc;
        table[0, 1, 0] = ImplPredMode.LeftDc;
        table[0, 1, 1] = ImplPredMode.Dc;
        // Paeth: no_left+no_top=128, no_left+top=Vert, left+no_top=Hor, both=Paeth
        table[1, 0, 0] = ImplPredMode.Dc128;
        table[1, 0, 1] = ImplPredMode.Vert;
        table[1, 1, 0] = ImplPredMode.Hor;
        table[1, 1, 1] = ImplPredMode.Paeth;
        return table;
    }

    /// <summary>
    /// Edge availability requirements per implementation mode.
    /// Bits: 0=needsLeft, 1=needsTop, 2=needsTopLeft, 3=needsTopRight, 4=needsBottomLeft
    /// </summary>
    private static ReadOnlySpan<byte> EdgeNeeds => new byte[]
    {
        // Dc:    left+top
        0b00011,
        // Vert:  top
        0b00010,
        // Hor:   left
        0b00001,
        // DiagDownLeft..VertLeft: unused (mapped to Z1/Z2/Z3)
        0, 0, 0, 0, 0, 0,
        // Paeth: left+top+topleft
        0b00111,
        // Smooth: left+top
        0b00011,
        // SmoothV: left+top
        0b00011,
        // SmoothH: left+top
        0b00011,
        // LeftDc: left
        0b00001,
        // TopDc: top
        0b00010,
        // Dc128: nothing
        0b00000,
        // Z1: top+topright+topleft
        0b01110,
        // Z2: left+top+topleft
        0b00111,
        // Z3: left+bottomleft+topleft
        0b10101,
        // Filter: left+top+topleft
        0b00111,
    };

    private const int NeedsLeft = 1;
    private const int NeedsTop = 2;
    private const int NeedsTopLeft = 4;
    private const int NeedsTopRight = 8;
    private const int NeedsBottomLeft = 16;

    /// <summary>
    /// Edge availability flags for current block position.
    /// </summary>
    [Flags]
    public enum EdgeFlags
    {
        None = 0,
        LeftHasBottom = 1,
        TopHasRight = 2,
    }

    /// <summary>
    /// Prepares the edge buffer for intra prediction and returns the resolved
    /// implementation mode. The <paramref name="edgeBuf"/> must have at least
    /// <c>2 * 64 + 1</c> (129) elements, with the center at index 64.
    /// </summary>
    /// <param name="x">Block column in units of 4-sample blocks.</param>
    /// <param name="haveLeft">Whether left samples are available.</param>
    /// <param name="y">Block row in units of 4-sample blocks.</param>
    /// <param name="haveTop">Whether top samples are available.</param>
    /// <param name="w">Picture width in 4-sample blocks.</param>
    /// <param name="h">Picture height in 4-sample blocks.</param>
    /// <param name="edgeFlags">Indicates if bottom-left or top-right neighbors exist.</param>
    /// <param name="dst">Reconstruction buffer at current block position.</param>
    /// <param name="dstStride">Stride of the reconstruction buffer in samples.</param>
    /// <param name="prefilterTopEdge">Pre-loop-filter top edge (null if none).</param>
    /// <param name="mode">The signaled intra prediction mode (0..13).</param>
    /// <param name="angle">On input: angle delta (-3..+3). On output: resolved angle.</param>
    /// <param name="tw">Transform width in 4-sample blocks.</param>
    /// <param name="th">Transform height in 4-sample blocks.</param>
    /// <param name="enableEdgeFilter">Whether intra edge filtering is enabled.</param>
    /// <param name="edgeBuf">Output: edge buffer. Center (topleft) at index <paramref name="centerOffset"/>.</param>
    /// <param name="centerOffset">The index in edgeBuf that represents the top-left corner sample.</param>
    /// <param name="bitDepth">Bit depth (8, 10, or 12).</param>
    /// <returns>The resolved implementation prediction mode.</returns>
    public static ImplPredMode PrepareIntraEdges(
        int x, bool haveLeft, int y, bool haveTop,
        int w, int h,
        EdgeFlags edgeFlags,
        ReadOnlySpan<byte> dst, int dstStride,
        ReadOnlySpan<byte> prefilterTopEdge,
        int mode, ref int angle,
        int tw, int th, bool enableEdgeFilter,
        Span<byte> edgeBuf, int centerOffset,
        int bitDepth)
    {
        var implMode = ResolveMode(mode, haveLeft, haveTop, ref angle);
        int needs = EdgeNeeds[(int)implMode];

        ReadOnlySpan<byte> dstTop = default;
        if (haveTop && ((needs & NeedsTop) != 0 || (needs & NeedsTopLeft) != 0 ||
                        ((needs & NeedsLeft) != 0 && !haveLeft)))
        {
            dstTop = prefilterTopEdge.IsEmpty
                ? dst.Slice(-dstStride)
                : prefilterTopEdge.Slice(x * 4);
        }

        // Fill left edge samples
        if ((needs & NeedsLeft) != 0)
        {
            int sz = th << 2;
            int leftBase = centerOffset - sz; // edgeBuf index for leftmost sample

            if (haveLeft)
            {
                int pxHave = Math.Min(sz, (h - y) << 2);
                for (int i = 0; i < pxHave; i++)
                    edgeBuf[centerOffset - 1 - i] = dst[dstStride * i - 1];
                if (pxHave < sz)
                    edgeBuf.Slice(leftBase, sz - pxHave).Fill(edgeBuf[centerOffset - pxHave]);
            }
            else
            {
                byte fill = haveTop ? dstTop[0] : (byte)(((1 << bitDepth) >> 1) + 1);
                edgeBuf.Slice(leftBase, sz).Fill(fill);
            }

            // Bottom-left extension
            if ((needs & NeedsBottomLeft) != 0)
            {
                bool haveBottomLeft = haveLeft && (y + th < h) &&
                                      (edgeFlags & EdgeFlags.LeftHasBottom) != 0;
                if (haveBottomLeft)
                {
                    int pxHave = Math.Min(sz, (h - y - th) << 2);
                    for (int i = 0; i < pxHave; i++)
                        edgeBuf[leftBase - 1 - i] = dst[(sz + i) * dstStride - 1];
                    if (pxHave < sz)
                        edgeBuf.Slice(leftBase - sz, sz - pxHave).Fill(edgeBuf[leftBase - pxHave]);
                }
                else
                {
                    edgeBuf.Slice(leftBase - sz, sz).Fill(edgeBuf[leftBase]);
                }
            }
        }

        // Fill top edge samples
        if ((needs & NeedsTop) != 0)
        {
            int sz = tw << 2;
            int topBase = centerOffset + 1; // edgeBuf index for first top sample

            if (haveTop)
            {
                int pxHave = Math.Min(sz, (w - x) << 2);
                dstTop.Slice(0, pxHave).CopyTo(edgeBuf.Slice(topBase, pxHave));
                if (pxHave < sz)
                    edgeBuf.Slice(topBase + pxHave, sz - pxHave).Fill(edgeBuf[topBase + pxHave - 1]);
            }
            else
            {
                byte fill = haveLeft ? dst[-1] : (byte)(((1 << bitDepth) >> 1) - 1);
                edgeBuf.Slice(topBase, sz).Fill(fill);
            }

            // Top-right extension
            if ((needs & NeedsTopRight) != 0)
            {
                bool haveTopRight = haveTop && (x + tw < w) &&
                                    (edgeFlags & EdgeFlags.TopHasRight) != 0;
                if (haveTopRight)
                {
                    int pxHave = Math.Min(sz, (w - x - tw) << 2);
                    dstTop.Slice(sz, pxHave).CopyTo(edgeBuf.Slice(topBase + sz, pxHave));
                    if (pxHave < sz)
                        edgeBuf.Slice(topBase + sz + pxHave, sz - pxHave)
                               .Fill(edgeBuf[topBase + sz + pxHave - 1]);
                }
                else
                {
                    edgeBuf.Slice(topBase + sz, sz).Fill(edgeBuf[topBase + sz - 1]);
                }
            }
        }

        // Fill top-left corner sample
        if ((needs & NeedsTopLeft) != 0)
        {
            if (haveLeft)
                edgeBuf[centerOffset] = haveTop ? dstTop[-1] : dst[-1];
            else
                edgeBuf[centerOffset] = haveTop ? dstTop[0] : (byte)((1 << bitDepth) >> 1);

            // Z2 smoothing of topleft
            if (implMode == ImplPredMode.Z2 && tw + th >= 6 && enableEdgeFilter)
            {
                edgeBuf[centerOffset] = (byte)(((edgeBuf[centerOffset - 1] +
                    edgeBuf[centerOffset + 1]) * 5 + edgeBuf[centerOffset] * 6 + 8) >> 4);
            }
        }

        return implMode;
    }

    /// <summary>
    /// Resolves signaled intra mode to implementation mode, updating angle.
    /// </summary>
    private static ImplPredMode ResolveMode(int mode, bool haveLeft, bool haveTop, ref int angle)
    {
        // Directional modes (1-8): map to angle, then to Z1/Z2/Z3
        if (mode >= 1 && mode <= 8)
        {
            angle = ModeToAngle[mode - 1] + 3 * angle;
            if (angle <= 90)
                return angle < 90 && haveTop ? ImplPredMode.Z1 : ImplPredMode.Vert;
            if (angle < 180)
                return ImplPredMode.Z2;
            return angle > 180 && haveLeft ? ImplPredMode.Z3 : ImplPredMode.Hor;
        }

        // DC (0) and Paeth (12): convert based on edge availability
        if (mode == 0)
            return ModeConversion[0, haveLeft ? 1 : 0, haveTop ? 1 : 0];
        if (mode == (int)Av1IntraPredMode.Paeth)
            return ModeConversion[1, haveLeft ? 1 : 0, haveTop ? 1 : 0];

        // Smooth modes (9-11) and Filter/CFL (13) pass through directly
        return mode switch
        {
            (int)Av1IntraPredMode.Smooth  => ImplPredMode.Smooth,
            (int)Av1IntraPredMode.SmoothV => ImplPredMode.SmoothV,
            (int)Av1IntraPredMode.SmoothH => ImplPredMode.SmoothH,
            (int)Av1IntraPredMode.ChromaFromLuma => ImplPredMode.Filter,
            _ => ImplPredMode.Dc128
        };
    }

    // ========================================================================
    // Prediction dispatcher (called from Av1Reconstruction)
    // ========================================================================

    /// <summary>
    /// Dispatch intra prediction by implementation mode index.
    /// Mode indices: 0=Dc, 1=V, 2=H, 3=Paeth, 4=Smooth, 5=SmoothV, 6=SmoothH,
    /// 7=LeftDc, 8=TopDc, 9=Dc128, 10=Z1, 11=Z2, 12=Z3, 13=Filter.
    /// </summary>
    public static void Predict(int implMode,
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height, int angle,
        int maxWidth, int maxHeight, int bitDepth = 8)
    {
        bool enableEdgeFilter = (angle & (1 << 10)) != 0;
        switch (implMode)
        {
            case 0: PredDc(dst, dstStride, edgeBuf, center, width, height); break;
            case 1: PredV(dst, dstStride, edgeBuf, center, width, height); break;
            case 2: PredH(dst, dstStride, edgeBuf, center, width, height); break;
            case 3: PredPaeth(dst, dstStride, edgeBuf, center, width, height); break;
            case 4: PredSmooth(dst, dstStride, edgeBuf, center, width, height); break;
            case 5: PredSmoothV(dst, dstStride, edgeBuf, center, width, height); break;
            case 6: PredSmoothH(dst, dstStride, edgeBuf, center, width, height); break;
            case 7: PredDcLeft(dst, dstStride, edgeBuf, center, width, height); break;
            case 8: PredDcTop(dst, dstStride, edgeBuf, center, width, height); break;
            case 9: PredDc128(dst, dstStride, width, height, bitDepth); break;
            case 10: PredZ1(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter); break;
            case 11: PredZ2(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter, maxWidth, maxHeight); break;
            case 12: PredZ3(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter); break;
            case 13: PredFilter(dst, dstStride, edgeBuf, center, width, height, angle); break;
        }
    }

    // ========================================================================
    // DC prediction
    // ========================================================================

    /// <summary>
    /// DC prediction: average of top and left samples.
    /// </summary>
    public static void PredDc(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        int dc = DcGenBoth(edgeBuf, center, width, height);
        SplatDc(dst, dstStride, width, height, dc);
    }

    /// <summary>
    /// DC prediction using only top samples.
    /// </summary>
    public static void PredDcTop(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        int dc = DcGenTop(edgeBuf, center, width);
        SplatDc(dst, dstStride, width, height, dc);
    }

    /// <summary>
    /// DC prediction using only left samples.
    /// </summary>
    public static void PredDcLeft(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        int dc = DcGenLeft(edgeBuf, center, height);
        SplatDc(dst, dstStride, width, height, dc);
    }

    /// <summary>
    /// DC 128 prediction (no neighbor samples available).
    /// </summary>
    public static void PredDc128(
        Span<byte> dst, int dstStride,
        int width, int height, int bitDepth)
    {
        int dc = bitDepth == 8 ? 128 : (1 << bitDepth) >> 1;
        SplatDc(dst, dstStride, width, height, dc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenTop(ReadOnlySpan<byte> edgeBuf, int center, int width)
    {
        int dc = width >> 1;
        for (int i = 0; i < width; i++)
            dc += edgeBuf[center + 1 + i];
        return dc >> BitOperations.TrailingZeroCount((uint)width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenLeft(ReadOnlySpan<byte> edgeBuf, int center, int height)
    {
        int dc = height >> 1;
        for (int i = 0; i < height; i++)
            dc += edgeBuf[center - 1 - i];
        return dc >> BitOperations.TrailingZeroCount((uint)height);
    }

    private static int DcGenBoth(ReadOnlySpan<byte> edgeBuf, int center, int width, int height)
    {
        int dc = (width + height) >> 1;
        for (int i = 0; i < width; i++)
            dc += edgeBuf[center + 1 + i];
        for (int i = 0; i < height; i++)
            dc += edgeBuf[center - 1 - i];
        dc >>= BitOperations.TrailingZeroCount((uint)(width + height));

        // Non-square correction
        if (width != height)
        {
            int multiplier = (width > height * 2 || height > width * 2)
                ? 0x3334    // MULTIPLIER_1x4 (8-bit)
                : 0x5556;   // MULTIPLIER_1x2 (8-bit)
            dc = (int)(((uint)dc * (uint)multiplier) >> 16); // BASE_SHIFT=16 for 8-bit
        }
        return dc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SplatDc(Span<byte> dst, int dstStride, int width, int height, int dc)
    {
        byte dcByte = (byte)dc;
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(dcByte);
    }

    // ========================================================================
    // Vertical / Horizontal
    // ========================================================================

    /// <summary>
    /// Vertical prediction: copy top samples to every row.
    /// </summary>
    public static void PredV(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        var top = edgeBuf.Slice(center + 1, width);
        for (int y = 0; y < height; y++)
            top.CopyTo(dst.Slice(y * dstStride, width));
    }

    /// <summary>
    /// Horizontal prediction: fill each row with the corresponding left sample.
    /// </summary>
    public static void PredH(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(edgeBuf[center - 1 - y]);
    }

    // ========================================================================
    // Paeth prediction
    // ========================================================================

    /// <summary>
    /// Paeth prediction: pick the neighbor (left, top, or top-left) whose value
    /// is closest to <c>left + top − topleft</c>.
    /// </summary>
    public static void PredPaeth(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        int tl = edgeBuf[center];
        for (int y = 0; y < height; y++)
        {
            int left = edgeBuf[center - 1 - y];
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int top = edgeBuf[center + 1 + x];
                int @base = left + top - tl;
                int ldiff = Math.Abs(left - @base);
                int tdiff = Math.Abs(top - @base);
                int tldiff = Math.Abs(tl - @base);
                row[x] = (byte)(ldiff <= tdiff && ldiff <= tldiff ? left :
                                tdiff <= tldiff ? top : tl);
            }
        }
    }

    // ========================================================================
    // Smooth prediction
    // ========================================================================

    /// <summary>
    /// Smooth prediction: weighted blend of top/bottom and left/right edges.
    /// </summary>
    public static void PredSmooth(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int right = edgeBuf[center + width];
        int bottom = edgeBuf[center - height];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * edgeBuf[center + 1 + x] +
                           (256 - weightsV[y]) * bottom +
                           weightsH[x] * edgeBuf[center - 1 - y] +
                           (256 - weightsH[x]) * right;
                row[x] = (byte)((pred + 256) >> 9);
            }
        }
    }

    /// <summary>
    /// Smooth vertical prediction: weighted blend of top and bottom.
    /// </summary>
    public static int DbgPredCount = 0;
    public static int DbgCurFrame = 0;

    public static void PredSmoothV(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        DbgPredCount++;
        if (DbgPredCount <= 6)
        {
            AvDbg.W($"[SV-ERR #{DbgPredCount}] f={DbgCurFrame} w={width} h={height}");
            AvDbg.W($"[SV-ERR #{DbgPredCount}] edgeBuf left: {edgeBuf[center-1]:x2} {edgeBuf[center-2]:x2} {edgeBuf[center-3]:x2} {edgeBuf[center-4]:x2} {edgeBuf[center-5]:x2} {edgeBuf[center-6]:x2} {edgeBuf[center-7]:x2} {edgeBuf[center-8]:x2}");
            AvDbg.W($"[SV-ERR #{DbgPredCount}] edgeBuf top: {edgeBuf[center+1]:x2} {edgeBuf[center+2]:x2} {edgeBuf[center+3]:x2} {edgeBuf[center+4]:x2} {edgeBuf[center+5]:x2} {edgeBuf[center+6]:x2} {edgeBuf[center+7]:x2} {edgeBuf[center+8]:x2}");
            AvDbg.W($"[SV-ERR #{DbgPredCount}] edgeBuf tl={edgeBuf[center]:x2} bottom={edgeBuf[center-height]:x2}");
        }

        if (DbgSmoothVDump && width <= 16 && height <= 8)
        {
            AvDbg.W($"[SV-DUMP] w={width} h={height} bottom={edgeBuf[center-height]:x2}");
            AvDbg.W("[SV-DUMP] top: ");
            for (int x = 0; x < width; x++) AvDbg.W($" {edgeBuf[center+1+x]:x2}");
            AvDbg.W();
        }

        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int bottom = edgeBuf[center - height];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * edgeBuf[center + 1 + x] +
                           (256 - weightsV[y]) * bottom;
                row[x] = (byte)((pred + 128) >> 8);
            }
        }

        if (DbgSmoothVDump && width <= 16 && height <= 8)
        {
            AvDbg.W("[SV-OUT] pred: ");
            for (int y = 0; y < height; y++)
            {
                if (y > 0) AvDbg.W(" | ");
                for (int x = 0; x < width; x++)
                    AvDbg.W($" {dst[y * dstStride + x]:x2}");
            }
            AvDbg.W();
        }
    }

    /// <summary>
    /// Smooth horizontal prediction: weighted blend of left and right.
    /// </summary>
    public static void PredSmoothH(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height)
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        int right = edgeBuf[center + width];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsH[x] * edgeBuf[center - 1 - y] +
                           (256 - weightsH[x]) * right;
                row[x] = (byte)((pred + 128) >> 8);
            }
        }
    }

    // ========================================================================
    // Directional prediction (Z1, Z2, Z3)
    // ========================================================================

    /// <summary>
    /// Z1 prediction: top-right diagonal direction (angle &lt; 90°).
    /// </summary>
    public static void PredZ1(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter)
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dx = Av1Tables.DrIntraDerivative[angle >> 1];
        int maxBaseX;

        // Determine if we need upsampling or filtering
        Span<byte> topBuf = stackalloc byte[128];
        bool upsample = enableEdgeFilter && GetUpsample(width + height, 90 - angle, isSm);
        bool useTopBuf; // if true, read from topBuf; else from edgeBuf[center+1..]
        int topOffset = 0; // offset into edgeBuf when useTopBuf=false

        if (upsample)
        {
            UpsampleEdge(topBuf, width + height,
                         edgeBuf, center + 1, -1,
                         width + Math.Min(width, height));
            useTopBuf = true;
            maxBaseX = 2 * (width + height) - 2;
            dx <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, 90 - angle, isSm) : 0;

            if (filterStrength > 0)
            {
                FilterEdge(topBuf, width + height, 0, width + height,
                           edgeBuf, center + 1, -1,
                           width + Math.Min(width, height), filterStrength);
                useTopBuf = true;
                maxBaseX = width + height - 1;
            }
            else
            {
                useTopBuf = false;
                topOffset = center + 1;
                maxBaseX = width + Math.Min(width, height) - 1;
            }
        }

        int baseInc = 1 + (upsample ? 1 : 0);
        for (int y = 0, xpos = dx; y < height; y++, xpos += dx)
        {
            var row = dst.Slice(y * dstStride, width);
            int frac = xpos & 0x3E;

            for (int x = 0, @base = xpos >> 6; x < width; x++, @base += baseInc)
            {
                if (@base < maxBaseX)
                {
                    int s0 = useTopBuf ? topBuf[@base] : edgeBuf[topOffset + @base];
                    int s1 = useTopBuf ? topBuf[@base + 1] : edgeBuf[topOffset + @base + 1];
                    int v = s0 * (64 - frac) + s1 * frac;
                    row[x] = (byte)((v + 32) >> 6);
                }
                else
                {
                    byte fill = useTopBuf ? topBuf[maxBaseX] : edgeBuf[topOffset + maxBaseX];
                    row.Slice(x, width - x).Fill(fill);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Z2 prediction: roughly horizontal/vertical diagonal (90° &lt; angle &lt; 180°).
    /// Uses both top and left edge samples.
    /// </summary>
    public static void PredZ2(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter,
        int maxWidth, int maxHeight)
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dy = Av1Tables.DrIntraDerivative[(angle - 90) >> 1];
        int dx = Av1Tables.DrIntraDerivative[(180 - angle) >> 1];

        bool upsampleLeft = enableEdgeFilter && GetUpsample(width + height, 180 - angle, isSm);
        bool upsampleAbove = enableEdgeFilter && GetUpsample(width + height, angle - 90, isSm);

        // Build a local edge buffer centered for Z2 processing
        Span<byte> edge = stackalloc byte[129]; // 64 + 1 + 64
        int edgeCenter = 64;

        // Fill above
        if (upsampleAbove)
        {
            UpsampleEdge(edge.Slice(edgeCenter), width + 1,
                         edgeBuf, center, 0, width + 1);
            dx <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, angle - 90, isSm) : 0;
            if (filterStrength > 0)
            {
                FilterEdge(edge.Slice(edgeCenter + 1), width, 0, maxWidth,
                           edgeBuf, center + 1, -1, width, filterStrength);
            }
            else
            {
                edgeBuf.Slice(center + 1, width).CopyTo(edge.Slice(edgeCenter + 1, width));
            }
        }

        // Fill left
        if (upsampleLeft)
        {
            UpsampleEdge(edge.Slice(edgeCenter - height * 2), height + 1,
                         edgeBuf, center - height, 0, height + 1);
            dy <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, 180 - angle, isSm) : 0;
            if (filterStrength > 0)
            {
                FilterEdge(edge.Slice(edgeCenter - height), height,
                           height - maxHeight, height,
                           edgeBuf, center - height, 0, height + 1, filterStrength);
            }
            else
            {
                for (int i = 0; i < height; i++)
                    edge[edgeCenter - height + i] = edgeBuf[center - height + i];
            }
        }

        edge[edgeCenter] = edgeBuf[center]; // topleft

        // Debug: dump internal edge for the target block
        if (DbgZ2 && width == 4 && height == 8 && (angle & 511) == 157)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[DBG-Z2-EDGE] dx=").Append(dx).Append(" dy=").Append(dy);
            sb.Append(" uA=").Append(upsampleAbove ? 1 : 0);
            sb.Append(" uL=").Append(upsampleLeft ? 1 : 0);
            sb.Append("\n  above:");
            for (int i = 0; i <= width * (upsampleAbove ? 2 : 1); i++)
                sb.Append($" {edge[edgeCenter + i]:x2}");
            sb.Append("\n  left:");
            for (int i = 1; i <= height * (upsampleLeft ? 2 : 1); i++)
                sb.Append($" {edge[edgeCenter - i]:x2}");
            AvDbg.W(sb.ToString());
            DbgZ2 = false;
        }

        int baseIncX = 1 + (upsampleAbove ? 1 : 0);
        int leftStep = 1 + (upsampleLeft ? 1 : 0);

        for (int y = 0, xpos = ((1 + (upsampleAbove ? 1 : 0)) << 6) - dx;
             y < height; y++, xpos -= dx)
        {
            var row = dst.Slice(y * dstStride, width);
            int baseX = xpos >> 6;
            int fracX = xpos & 0x3E;

            for (int x = 0, ypos = (y << (6 + (upsampleLeft ? 1 : 0))) - dy;
                 x < width; x++, baseX += baseIncX, ypos -= dy)
            {
                int v;
                if (baseX >= 0)
                {
                    v = edge[edgeCenter + baseX] * (64 - fracX) +
                        edge[edgeCenter + baseX + 1] * fracX;
                }
                else
                {
                    int baseY = ypos >> 6;
                    int fracY = ypos & 0x3E;
                    v = edge[edgeCenter - leftStep - baseY] * (64 - fracY) +
                        edge[edgeCenter - leftStep - baseY - 1] * fracY;
                }
                row[x] = (byte)((v + 32) >> 6);
            }
        }
    }

    /// <summary>
    /// Z3 prediction: bottom-left diagonal direction (angle &gt; 180°).
    /// </summary>
    public static void PredZ3(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter)
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dy = Av1Tables.DrIntraDerivative[(270 - angle) >> 1];

        Span<byte> leftBuf = stackalloc byte[128];
        int maxBaseY;
        bool upsample = enableEdgeFilter && GetUpsample(width + height, angle - 180, isSm);
        bool useLeftBuf;
        int leftOffset = 0; // "left[0]" location: samples go backwards from here

        if (upsample)
        {
            UpsampleEdge(leftBuf, width + height,
                         edgeBuf, center - (width + height),
                         Math.Max(width - height, 0), width + height + 1);
            useLeftBuf = true;
            leftOffset = 2 * (width + height) - 2; // index into leftBuf
            maxBaseY = 2 * (width + height) - 2;
            dy <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, angle - 180, isSm) : 0;

            if (filterStrength > 0)
            {
                FilterEdge(leftBuf, width + height, 0, width + height,
                           edgeBuf, center - (width + height),
                           Math.Max(width - height, 0), width + height + 1, filterStrength);
                useLeftBuf = true;
                leftOffset = width + height - 1; // index into leftBuf
                maxBaseY = width + height - 1;
            }
            else
            {
                useLeftBuf = false;
                leftOffset = center - 1; // index into edgeBuf
                maxBaseY = height + Math.Min(width, height) - 1;
            }
        }

        int baseInc = 1 + (upsample ? 1 : 0);
        for (int x = 0, ypos = dy; x < width; x++, ypos += dy)
        {
            int frac = ypos & 0x3E;
            for (int y = 0, @base = ypos >> 6; y < height; y++, @base += baseInc)
            {
                if (@base < maxBaseY)
                {
                    int s0 = useLeftBuf ? leftBuf[leftOffset - @base] : edgeBuf[leftOffset - @base];
                    int s1 = useLeftBuf ? leftBuf[leftOffset - @base - 1] : edgeBuf[leftOffset - @base - 1];
                    int v = s0 * (64 - frac) + s1 * frac;
                    dst[y * dstStride + x] = (byte)((v + 32) >> 6);
                }
                else
                {
                    byte fill = useLeftBuf ? leftBuf[leftOffset - maxBaseY] : edgeBuf[leftOffset - maxBaseY];
                    for (; y < height; y++)
                        dst[y * dstStride + x] = fill;
                    break;
                }
            }
        }
    }

    // ========================================================================
    // Directional helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GetUpsample(int wh, int angle, bool isSm)
    {
        return angle < 40 && wh <= (isSm ? 8 : 16);
    }

    private static int GetFilterStrength(int wh, int angle, bool isSm)
    {
        if (isSm)
        {
            if (wh <= 8)
            {
                if (angle >= 64) return 2;
                if (angle >= 40) return 1;
            }
            else if (wh <= 16)
            {
                if (angle >= 48) return 2;
                if (angle >= 20) return 1;
            }
            else if (wh <= 24)
            {
                if (angle >= 4) return 3;
            }
            else return 3;
        }
        else
        {
            if (wh <= 8)
            {
                if (angle >= 56) return 1;
            }
            else if (wh <= 16)
            {
                if (angle >= 40) return 1;
            }
            else if (wh <= 24)
            {
                if (angle >= 32) return 3;
                if (angle >= 16) return 2;
                if (angle >= 8) return 1;
            }
            else if (wh <= 32)
            {
                if (angle >= 32) return 3;
                if (angle >= 4) return 2;
                return 1;
            }
            else return 3;
        }
        return 0;
    }

    /// <summary>
    /// Edge filtering kernel for directional prediction.
    /// Applies 3-tap or 5-tap smoothing to edge samples.
    /// </summary>
    private static void FilterEdge(
        Span<byte> output, int sz,
        int limFrom, int limTo,
        ReadOnlySpan<byte> input, int inputOffset,
        int from, int to, int strength)
    {
        ReadOnlySpan<byte> kernel0 = stackalloc byte[] { 0, 4, 8, 4, 0 };
        ReadOnlySpan<byte> kernel1 = stackalloc byte[] { 0, 5, 6, 5, 0 };
        ReadOnlySpan<byte> kernel2 = stackalloc byte[] { 2, 4, 4, 4, 2 };
        var kernel = strength switch
        {
            1 => kernel0,
            2 => kernel1,
            _ => kernel2
        };

        int i = 0;
        for (; i < Math.Min(sz, limFrom); i++)
            output[i] = input[inputOffset + Math.Clamp(i, from, to - 1)];
        for (; i < Math.Min(limTo, sz); i++)
        {
            int s = 0;
            for (int j = 0; j < 5; j++)
                s += input[inputOffset + Math.Clamp(i - 2 + j, from, to - 1)] * kernel[j];
            output[i] = (byte)((s + 8) >> 4);
        }
        for (; i < sz; i++)
            output[i] = input[inputOffset + Math.Clamp(i, from, to - 1)];
    }

    /// <summary>
    /// Edge upsampling for directional prediction (doubles resolution).
    /// </summary>
    private static void UpsampleEdge(
        Span<byte> output, int hsz,
        ReadOnlySpan<byte> input, int inputOffset,
        int from, int to)
    {
        ReadOnlySpan<sbyte> kernel = stackalloc sbyte[] { -1, 9, 9, -1 };
        int i;
        for (i = 0; i < hsz - 1; i++)
        {
            output[i * 2] = input[inputOffset + Math.Clamp(i, from, to - 1)];
            int s = 0;
            for (int j = 0; j < 4; j++)
                s += input[inputOffset + Math.Clamp(i + j - 1, from, to - 1)] * kernel[j];
            output[i * 2 + 1] = (byte)Math.Clamp((s + 8) >> 4, 0, 255);
        }
        output[i * 2] = input[inputOffset + Math.Clamp(i, from, to - 1)];
    }

    // ========================================================================
    // Filter intra prediction
    // ========================================================================

    /// <summary>
    /// Filter intra prediction. Uses one of 5 filter sets applied in 4×2 sub-blocks.
    /// Max block size: 32×32.
    /// </summary>
    public static void PredFilter(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height, int filterIndex)
    {
        filterIndex &= 511;
        int topIdx = center + 1;
        int dstOffset = 0;

        for (int y = 0; y < height; y += 2)
        {
            int topleftEdgeIdx = center - y;
            for (int x = 0; x < width; x += 4)
            {
                int p0, p1, p2, p3, p4, p5, p6;

                if (y == 0)
                    p0 = edgeBuf[topleftEdgeIdx];
                else if (x == 0)
                    p0 = edgeBuf[center - y];
                else
                    p0 = dst[dstOffset - dstStride + x - 1]; // reconstruction

                // Top samples
                if (y == 0)
                {
                    p1 = edgeBuf[topIdx + x];
                    p2 = edgeBuf[topIdx + x + 1];
                    p3 = edgeBuf[topIdx + x + 2];
                    p4 = edgeBuf[topIdx + x + 3];
                }
                else
                {
                    p1 = dst[dstOffset - dstStride + x];
                    p2 = dst[dstOffset - dstStride + x + 1];
                    p3 = dst[dstOffset - dstStride + x + 2];
                    p4 = dst[dstOffset - dstStride + x + 3];
                }

                // Left samples
                if (x == 0)
                {
                    p5 = edgeBuf[center - y - 1];
                    p6 = edgeBuf[center - y - 2];
                }
                else
                {
                    p5 = dst[dstOffset + x - 1];
                    p6 = dst[dstOffset + dstStride + x - 1];
                }

                // Apply 7-tap filter for each of 8 positions (4×2 block)
                int fltPos = 0;
                for (int yy = 0; yy < 2; yy++)
                {
                    for (int xx = 0; xx < 4; xx++, fltPos++)
                    {
                        // Non-x86 layout: taps at offsets 0, 8, 16, 24, 32, 40, 48
                        int acc = Av1Tables.FilterIntraTaps[filterIndex, fltPos] * p0 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 8] * p1 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 16] * p2 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 24] * p3 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 32] * p4 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 40] * p5 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 48] * p6;
                        dst[dstOffset + yy * dstStride + x + xx] = (byte)Math.Clamp((acc + 8) >> 4, 0, 255);
                    }
                }

                // Update top-left for next 4-column block (y==0 only; y>0 uses reconstruction)
                if (y == 0)
                    topleftEdgeIdx = topIdx + x + 3;
            }

            dstOffset += dstStride * 2;
        }
    }

    // ========================================================================
    // Chroma-from-Luma (CFL) prediction
    // ========================================================================

    /// <summary>
    /// CFL prediction: DC + scaled AC component from luma.
    /// </summary>
    public static void PredCfl(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha)
    {
        int dc = DcGenBoth(edgeBuf, center, width, height);
        CflPred(dst, dstStride, width, height, dc, ac, alpha);
    }

    /// <summary>
    /// CFL prediction using only top DC.
    /// </summary>
    public static void PredCflTop(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha)
    {
        int dc = DcGenTop(edgeBuf, center, width);
        CflPred(dst, dstStride, width, height, dc, ac, alpha);
    }

    /// <summary>
    /// CFL prediction using only left DC.
    /// </summary>
    public static void PredCflLeft(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha)
    {
        int dc = DcGenLeft(edgeBuf, center, height);
        CflPred(dst, dstStride, width, height, dc, ac, alpha);
    }

    /// <summary>
    /// CFL prediction with DC=128 (no neighbors).
    /// </summary>
    public static void PredCfl128(
        Span<byte> dst, int dstStride,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha)
    {
        CflPred(dst, dstStride, width, height, 128, ac, alpha);
    }

    private static void CflPred(
        Span<byte> dst, int dstStride,
        int width, int height, int dc,
        ReadOnlySpan<short> ac, int alpha)
    {
        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            var acRow = ac.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                int diff = alpha * acRow[x];
                int sign = diff >> 31;
                int absDiff = (Math.Abs(diff) + 32) >> 6;
                row[x] = (byte)Math.Clamp(dc + (absDiff ^ sign) - sign, 0, 255);
            }
        }
    }

    // ========================================================================
    // CFL AC generation
    // ========================================================================

    /// <summary>
    /// Generate AC (alternating component) values from luma samples for CFL prediction.
    /// Subsamples luma according to chroma format and subtracts the DC.
    /// </summary>
    /// <param name="ac">Output AC values (width × height).</param>
    /// <param name="luma">Luma reconstruction buffer.</param>
    /// <param name="lumaStride">Luma stride in bytes.</param>
    /// <param name="wPad">Horizontal padding blocks (each = 4 samples).</param>
    /// <param name="hPad">Vertical padding blocks (each = 4 samples).</param>
    /// <param name="width">Chroma block width.</param>
    /// <param name="height">Chroma block height.</param>
    /// <param name="ssHor">Horizontal subsampling (0=444, 1=420/422).</param>
    /// <param name="ssVer">Vertical subsampling (0=444/422, 1=420).</param>
    public static void CflAc(
        Span<short> ac,
        ReadOnlySpan<byte> luma, int lumaStride,
        int wPad, int hPad,
        int width, int height,
        int ssHor, int ssVer)
    {
        int acIdx = 0;

        for (int y = 0; y < height - 4 * hPad; y++)
        {
            int x;
            for (x = 0; x < width - 4 * wPad; x++)
            {
                int sum = luma[y * (lumaStride << ssVer) + (x << ssHor)];
                if (ssHor != 0) sum += luma[y * (lumaStride << ssVer) + x * 2 + 1];
                if (ssVer != 0)
                {
                    sum += luma[(y * (lumaStride << ssVer)) + lumaStride + (x << ssHor)];
                    if (ssHor != 0) sum += luma[(y * (lumaStride << ssVer)) + lumaStride + x * 2 + 1];
                }
                ac[acIdx + x] = (short)(sum << (1 + (ssVer == 0 ? 1 : 0) + (ssHor == 0 ? 1 : 0)));
            }
            // Pad right
            for (; x < width; x++)
                ac[acIdx + x] = ac[acIdx + x - 1];
            acIdx += width;
        }

        // Pad bottom
        for (int y = height - 4 * hPad; y < height; y++)
        {
            ac.Slice(acIdx - width, width).CopyTo(ac.Slice(acIdx, width));
            acIdx += width;
        }

        // Subtract DC
        int log2sz = BitOperations.TrailingZeroCount((uint)width) +
                     BitOperations.TrailingZeroCount((uint)height);
        int dcSum = (1 << log2sz) >> 1;
        for (int i = 0; i < width * height; i++)
            dcSum += ac[i];
        dcSum >>= log2sz;
        for (int i = 0; i < width * height; i++)
            ac[i] -= (short)dcSum;
    }

    // ========================================================================
    // Palette prediction
    // ========================================================================

    /// <summary>
    /// Palette prediction: map palette indices to pixel values.
    /// Indices are packed as two 4-bit values per byte (low nibble = even x, high nibble = odd x).
    /// </summary>
    public static void PredPalette(
        Span<byte> dst, int dstStride,
        ReadOnlySpan<byte> palette,
        ReadOnlySpan<byte> indices,
        int width, int height)
    {
        int idxPos = 0;
        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x += 2)
            {
                byte packed = indices[idxPos++];
                row[x] = palette[packed & 7];
                row[x + 1] = palette[packed >> 4];
            }
        }
    }

    // ========================================================================
    // High bit depth variants (16-bit)
    // ========================================================================

    /// <summary>
    /// DC prediction for high bit depth (10/12-bit).
    /// </summary>
    public static void PredDc16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height, int bitDepth)
    {
        int dc = DcGenBoth16(edgeBuf, center, width, height, bitDepth);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDcTop16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        int dc = DcGenTop16(edgeBuf, center, width);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDcLeft16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        int dc = DcGenLeft16(edgeBuf, center, height);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDc12816(
        Span<ushort> dst, int dstStride,
        int width, int height, int bitDepth)
    {
        int dc = ((1 << bitDepth) + 1) >> 1;
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredV16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        var top = edgeBuf.Slice(center + 1, width);
        for (int y = 0; y < height; y++)
            top.CopyTo(dst.Slice(y * dstStride, width));
    }

    public static void PredH16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(edgeBuf[center - 1 - y]);
    }

    public static void PredPaeth16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        int tl = edgeBuf[center];
        for (int y = 0; y < height; y++)
        {
            int left = edgeBuf[center - 1 - y];
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int top = edgeBuf[center + 1 + x];
                int @base = left + top - tl;
                int ldiff = Math.Abs(left - @base);
                int tdiff = Math.Abs(top - @base);
                int tldiff = Math.Abs(tl - @base);
                row[x] = (ushort)(ldiff <= tdiff && ldiff <= tldiff ? left :
                                  tdiff <= tldiff ? top : tl);
            }
        }
    }

    public static void PredSmooth16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int right = edgeBuf[center + width];
        int bottom = edgeBuf[center - height];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * edgeBuf[center + 1 + x] +
                           (256 - weightsV[y]) * bottom +
                           weightsH[x] * edgeBuf[center - 1 - y] +
                           (256 - weightsH[x]) * right;
                row[x] = (ushort)((pred + 256) >> 9);
            }
        }
    }

    public static void PredSmoothV16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int bottom = edgeBuf[center - height];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * edgeBuf[center + 1 + x] +
                           (256 - weightsV[y]) * bottom;
                row[x] = (ushort)((pred + 128) >> 8);
            }
        }
    }

    public static void PredSmoothH16(
        Span<ushort> dst, int dstStride,
        ReadOnlySpan<ushort> edgeBuf, int center,
        int width, int height)
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        int right = edgeBuf[center + width];

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsH[x] * edgeBuf[center - 1 - y] +
                           (256 - weightsH[x]) * right;
                row[x] = (ushort)((pred + 128) >> 8);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenTop16(ReadOnlySpan<ushort> edgeBuf, int center, int width)
    {
        int dc = width >> 1;
        for (int i = 0; i < width; i++)
            dc += edgeBuf[center + 1 + i];
        return dc >> BitOperations.TrailingZeroCount((uint)width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenLeft16(ReadOnlySpan<ushort> edgeBuf, int center, int height)
    {
        int dc = height >> 1;
        for (int i = 0; i < height; i++)
            dc += edgeBuf[center - 1 - i];
        return dc >> BitOperations.TrailingZeroCount((uint)height);
    }

    private static int DcGenBoth16(ReadOnlySpan<ushort> edgeBuf, int center, int width, int height, int bitDepth)
    {
        int dc = (width + height) >> 1;
        for (int i = 0; i < width; i++)
            dc += edgeBuf[center + 1 + i];
        for (int i = 0; i < height; i++)
            dc += edgeBuf[center - 1 - i];
        dc >>= BitOperations.TrailingZeroCount((uint)(width + height));

        if (width != height)
        {
            // High bit depth uses different multipliers (MULTIPLIER shifted by 17)
            int multiplier = (width > height * 2 || height > width * 2)
                ? 0x6667    // MULTIPLIER_1x4 (16-bit)
                : 0xAAAB;   // MULTIPLIER_1x2 (16-bit)
            dc = (int)(((uint)dc * (uint)multiplier) >> 17); // BASE_SHIFT=17 for 16-bit
        }
        return dc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SplatDc16(Span<ushort> dst, int dstStride, int width, int height, int dc)
    {
        ushort dcVal = (ushort)dc;
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(dcVal);
    }
}
