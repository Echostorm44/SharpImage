using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
// AV1 intra prediction modes for the decoder
// Ported from dav1d: src/ipred_tmpl.c + src/ipred_prepare_tmpl.c (VideoLAN dav1d, BSD-2-Clause)
// Implements DC, V, H, Paeth, Smooth, directional (Z1/Z2/Z3), filter intra,
// chroma-from-luma (CFL), and palette prediction.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        if (DbgPredCount <= 6) DbgPredCount++;   // the debug guard below only reads <= 6
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
    // 16-bit arithmetic variants, generic over the sample type TP (byte for 8-bit content, ushort for 10/12-bit; see Px).
    // The "16" names the arithmetic width, not the storage: the byte instantiation at bit depth 8 is bit-identical to the
    // ushort one.
    // ========================================================================

    /// <summary>
    /// DC prediction.
    /// </summary>
    public static void PredDc16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int bitDepth) where TP : unmanaged
    {
        int dc = DcGenBoth16(edgeBuf, center, width, height, bitDepth);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDcTop16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        int dc = DcGenTop16(edgeBuf, center, width);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDcLeft16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        int dc = DcGenLeft16(edgeBuf, center, height);
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredDc12816<TP>(
        Span<TP> dst, int dstStride,
        int width, int height, int bitDepth) where TP : unmanaged
    {
        int dc = ((1 << bitDepth) + 1) >> 1;
        SplatDc16(dst, dstStride, width, height, dc);
    }

    public static void PredV16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        var top = edgeBuf.Slice(center + 1, width);
        for (int y = 0; y < height; y++)
            top.CopyTo(dst.Slice(y * dstStride, width));
    }

    public static void PredH16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(edgeBuf[center - 1 - y]);
    }

    public static void PredPaeth16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        int tl = Px.I(edgeBuf[center]);
        if (Vector256.IsHardwareAccelerated && width >= 16)
        {
            // 16 lanes: |left - base| = |top - tl|, |top - base| = |left - tl|, |tl - base| = |left + top - 2tl|
            // (within 16 bits at <= 12-bit samples)
            var vtl = Vector256.Create((short)tl);
            for (int y = 0; y < height; y++)
            {
                int left = Px.I(edgeBuf[center - 1 - y]);
                var vl = Vector256.Create((short)left);
                var tdiff = Vector256.Create((short)Math.Abs(left - tl));
                var row = dst.Slice(y * dstStride, width);
                for (int x = 0; x < width; x += 16)
                {
                    var top = Px.Load16(edgeBuf, center + 1 + x).AsInt16();
                    var ldiff = Vector256.Abs(top - vtl);
                    var tldiff = Vector256.Abs(vl + top - vtl - vtl);
                    var useLeft = Vector256.LessThanOrEqual(ldiff, tdiff) & Vector256.LessThanOrEqual(ldiff, tldiff);
                    var useTop = Vector256.LessThanOrEqual(tdiff, tldiff);
                    var v = Vector256.ConditionalSelect(useLeft, vl, Vector256.ConditionalSelect(useTop, top, vtl));
                    Px.Store16(row, x, v.AsUInt16());
                }
            }
            return;
        }
        if (Vector128.IsHardwareAccelerated && width == 8)
        {
            var vtl = Vector128.Create((short)tl);
            var top = Px.Load8(edgeBuf, center + 1).AsInt16();
            var ldiff = Vector128.Abs(top - vtl);
            for (int y = 0; y < height; y++)
            {
                int left = Px.I(edgeBuf[center - 1 - y]);
                var vl = Vector128.Create((short)left);
                var tdiff = Vector128.Create((short)Math.Abs(left - tl));
                var tldiff = Vector128.Abs(vl + top - vtl - vtl);
                var useLeft = Vector128.LessThanOrEqual(ldiff, tdiff) & Vector128.LessThanOrEqual(ldiff, tldiff);
                var useTop = Vector128.LessThanOrEqual(tdiff, tldiff);
                Px.Store8(dst, y * dstStride, Vector128.ConditionalSelect(useLeft, vl, Vector128.ConditionalSelect(useTop, top, vtl)).AsUInt16());
            }
            return;
        }
        for (int y = 0; y < height; y++)
        {
            int left = Px.I(edgeBuf[center - 1 - y]);
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int top = Px.I(edgeBuf[center + 1 + x]);
                int @base = left + top - tl;
                int ldiff = Math.Abs(left - @base);
                int tdiff = Math.Abs(top - @base);
                int tldiff = Math.Abs(tl - @base);
                row[x] = Px.T<TP>(ldiff <= tdiff && ldiff <= tldiff ? left :
                                  tdiff <= tldiff ? top : tl);
            }
        }
    }

    public static void PredSmooth16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int right = Px.I(edgeBuf[center + width]);
        int bottom = Px.I(edgeBuf[center - height]);
        if (Vector256.IsHardwareAccelerated && width >= 8)
        {
            // per column: top, wH and (256 - wH) * right + 256 fixed; per row two broadcasts
            int nc = width >> 3;
            Span<Vector256<int>> colTop = stackalloc Vector256<int>[nc], colW = stackalloc Vector256<int>[nc], colC = stackalloc Vector256<int>[nc];
            Span<int> tmp = stackalloc int[8];
            for (int k = 0; k < nc; k++)
            {
                for (int i = 0; i < 8; i++) tmp[i] = Px.I(edgeBuf[center + 1 + k * 8 + i]);
                colTop[k] = Vector256.Create<int>(tmp);
                for (int i = 0; i < 8; i++) tmp[i] = weightsH[k * 8 + i];
                colW[k] = Vector256.Create<int>(tmp);
                colC[k] = (Vector256.Create(256) - colW[k]) * right + Vector256.Create(256);
            }
            for (int y = 0; y < height; y++)
            {
                var wv = Vector256.Create((int)weightsV[y]);
                var c = Vector256.Create((256 - weightsV[y]) * bottom);
                var l = Vector256.Create(Px.I(edgeBuf[center - 1 - y]));
                var row = dst.Slice(y * dstStride, width);
                for (int k = 0; k < nc; k++)
                    Px.Store8(row, k * 8, Vector256.ShiftRightArithmetic(wv * colTop[k] + c + colW[k] * l + colC[k], 9));
            }
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * Px.I(edgeBuf[center + 1 + x]) +
                           (256 - weightsV[y]) * bottom +
                           weightsH[x] * Px.I(edgeBuf[center - 1 - y]) +
                           (256 - weightsH[x]) * right;
                row[x] = Px.T<TP>((pred + 256) >> 9);
            }
        }
    }

    public static void PredSmoothV16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        var weightsV = Av1Tables.SmoothWeights.AsSpan(height, height);
        int bottom = Px.I(edgeBuf[center - height]);
        if (Vector256.IsHardwareAccelerated && width >= 8)
        {
            int nc = width >> 3;
            Span<Vector256<int>> colTop = stackalloc Vector256<int>[nc];
            Span<int> tmp = stackalloc int[8];
            for (int k = 0; k < nc; k++)
            {
                for (int i = 0; i < 8; i++) tmp[i] = Px.I(edgeBuf[center + 1 + k * 8 + i]);
                colTop[k] = Vector256.Create<int>(tmp);
            }
            for (int y = 0; y < height; y++)
            {
                var wv = Vector256.Create((int)weightsV[y]);
                var c = Vector256.Create((256 - weightsV[y]) * bottom + 128);
                var row = dst.Slice(y * dstStride, width);
                for (int k = 0; k < nc; k++)
                    Px.Store8(row, k * 8, Vector256.ShiftRightArithmetic(wv * colTop[k] + c, 8));
            }
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsV[y] * Px.I(edgeBuf[center + 1 + x]) +
                           (256 - weightsV[y]) * bottom;
                row[x] = Px.T<TP>((pred + 128) >> 8);
            }
        }
    }

    public static void PredSmoothH16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height) where TP : unmanaged
    {
        var weightsH = Av1Tables.SmoothWeights.AsSpan(width, width);
        int right = Px.I(edgeBuf[center + width]);
        if (Vector256.IsHardwareAccelerated && width >= 8)
        {
            int nc = width >> 3;
            Span<Vector256<int>> colW = stackalloc Vector256<int>[nc], colC = stackalloc Vector256<int>[nc];
            Span<int> tmp = stackalloc int[8];
            for (int k = 0; k < nc; k++)
            {
                for (int i = 0; i < 8; i++) tmp[i] = weightsH[k * 8 + i];
                colW[k] = Vector256.Create<int>(tmp);
                colC[k] = (Vector256.Create(256) - colW[k]) * right + Vector256.Create(128);
            }
            for (int y = 0; y < height; y++)
            {
                var l = Vector256.Create(Px.I(edgeBuf[center - 1 - y]));
                var row = dst.Slice(y * dstStride, width);
                for (int k = 0; k < nc; k++)
                    Px.Store8(row, k * 8, Vector256.ShiftRightArithmetic(colW[k] * l + colC[k], 8));
            }
            return;
        }

        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                int pred = weightsH[x] * Px.I(edgeBuf[center - 1 - y]) +
                           (256 - weightsH[x]) * right;
                row[x] = Px.T<TP>((pred + 128) >> 8);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenTop16<TP>(ReadOnlySpan<TP> edgeBuf, int center, int width) where TP : unmanaged
    {
        int dc = width >> 1;
        for (int i = 0; i < width; i++)
            dc += Px.I(edgeBuf[center + 1 + i]);
        return dc >> BitOperations.TrailingZeroCount((uint)width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DcGenLeft16<TP>(ReadOnlySpan<TP> edgeBuf, int center, int height) where TP : unmanaged
    {
        int dc = height >> 1;
        for (int i = 0; i < height; i++)
            dc += Px.I(edgeBuf[center - 1 - i]);
        return dc >> BitOperations.TrailingZeroCount((uint)height);
    }

    private static int DcGenBoth16<TP>(ReadOnlySpan<TP> edgeBuf, int center, int width, int height, int bitDepth) where TP : unmanaged
    {
        int dc = (width + height) >> 1;
        for (int i = 0; i < width; i++)
            dc += Px.I(edgeBuf[center + 1 + i]);
        for (int i = 0; i < height; i++)
            dc += Px.I(edgeBuf[center - 1 - i]);
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
    private static void SplatDc16<TP>(Span<TP> dst, int dstStride, int width, int height, int dc) where TP : unmanaged
    {
        TP dcVal = Px.T<TP>(dc);
        for (int y = 0; y < height; y++)
            dst.Slice(y * dstStride, width).Fill(dcVal);
    }

    // ========================================================================
    // Edge preparation + dispatcher + directional/filter/CFL
    // Mirrors the byte reference path exactly; clamps that used 255 now use (1<<bd)-1.
    // ========================================================================

    /// <summary>Edge preparation. See <see cref="PrepareIntraEdges"/>.</summary>
    public static ImplPredMode PrepareIntraEdges16<TP>(
        int x, bool haveLeft, int y, bool haveTop,
        int w, int h,
        EdgeFlags edgeFlags,
        ReadOnlySpan<TP> dst, int dstStride,
        ReadOnlySpan<TP> prefilterTopEdge,
        int mode, ref int angle,
        int tw, int th, bool enableEdgeFilter,
        Span<TP> edgeBuf, int centerOffset,
        int bitDepth) where TP : unmanaged
    {
        var implMode = ResolveMode(mode, haveLeft, haveTop, ref angle);
        int needs = EdgeNeeds[(int)implMode];

        ReadOnlySpan<TP> dstTop = default;
        if (haveTop && ((needs & NeedsTop) != 0 || (needs & NeedsTopLeft) != 0 ||
                        ((needs & NeedsLeft) != 0 && !haveLeft)))
        {
            dstTop = prefilterTopEdge.IsEmpty
                ? dst.Slice(-dstStride)
                : prefilterTopEdge.Slice(x * 4);
        }

        if ((needs & NeedsLeft) != 0)
        {
            int sz = th << 2;
            int leftBase = centerOffset - sz;

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
                TP fill = haveTop ? dstTop[0] : Px.T<TP>(((1 << bitDepth) >> 1) + 1);
                edgeBuf.Slice(leftBase, sz).Fill(fill);
            }

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

        if ((needs & NeedsTop) != 0)
        {
            int sz = tw << 2;
            int topBase = centerOffset + 1;

            if (haveTop)
            {
                int pxHave = Math.Min(sz, (w - x) << 2);
                dstTop.Slice(0, pxHave).CopyTo(edgeBuf.Slice(topBase, pxHave));
                if (pxHave < sz)
                    edgeBuf.Slice(topBase + pxHave, sz - pxHave).Fill(edgeBuf[topBase + pxHave - 1]);
            }
            else
            {
                TP fill = haveLeft ? dst[-1] : Px.T<TP>(((1 << bitDepth) >> 1) - 1);
                edgeBuf.Slice(topBase, sz).Fill(fill);
            }

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

        if ((needs & NeedsTopLeft) != 0)
        {
            if (haveLeft)
                edgeBuf[centerOffset] = haveTop ? dstTop[-1] : dst[-1];
            else
                edgeBuf[centerOffset] = haveTop ? dstTop[0] : Px.T<TP>((1 << bitDepth) >> 1);

            if (implMode == ImplPredMode.Z2 && tw + th >= 6 && enableEdgeFilter)
            {
                edgeBuf[centerOffset] = Px.T<TP>(((Px.I(edgeBuf[centerOffset - 1]) +
                    Px.I(edgeBuf[centerOffset + 1])) * 5 + Px.I(edgeBuf[centerOffset]) * 6 + 8) >> 4);
            }
        }

        return implMode;
    }

    /// <summary>Prediction dispatcher. See <see cref="Predict"/>.</summary>
    public static void Predict16<TP>(int implMode,
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int angle,
        int maxWidth, int maxHeight, int bitDepth) where TP : unmanaged
    {
        bool enableEdgeFilter = (angle & (1 << 10)) != 0;
        switch (implMode)
        {
            case 0: PredDc16(dst, dstStride, edgeBuf, center, width, height, bitDepth); break;
            case 1: PredV16(dst, dstStride, edgeBuf, center, width, height); break;
            case 2: PredH16(dst, dstStride, edgeBuf, center, width, height); break;
            case 3: PredPaeth16(dst, dstStride, edgeBuf, center, width, height); break;
            case 4: PredSmooth16(dst, dstStride, edgeBuf, center, width, height); break;
            case 5: PredSmoothV16(dst, dstStride, edgeBuf, center, width, height); break;
            case 6: PredSmoothH16(dst, dstStride, edgeBuf, center, width, height); break;
            case 7: PredDcLeft16(dst, dstStride, edgeBuf, center, width, height); break;
            case 8: PredDcTop16(dst, dstStride, edgeBuf, center, width, height); break;
            case 9: PredDc12816(dst, dstStride, width, height, bitDepth); break;
            case 10: PredZ1_16(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter, bitDepth); break;
            case 11: PredZ2_16(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter, maxWidth, maxHeight, bitDepth); break;
            case 12: PredZ3_16(dst, dstStride, edgeBuf, center, width, height, angle, enableEdgeFilter, bitDepth); break;
            case 13: PredFilter16(dst, dstStride, edgeBuf, center, width, height, angle, bitDepth); break;
        }
    }

    /// <summary>dst[y * dstStride + x] = src[x * srcStride + y] for x &lt; w, y &lt; h (a column-major w x h block to rows):
    /// 8 x 8 tiles of 16-bit lanes where both sides allow, scalar at the ragged edges.</summary>
    private static void TransposeU16<TP>(ReadOnlySpan<TP> src, int srcStride, Span<TP> dst, int dstStride, int w, int h) where TP : unmanaged
    {
        int w8 = Sse2.IsSupported ? w & ~7 : 0, h8 = Sse2.IsSupported ? h & ~7 : 0;
        for (int x0 = 0; x0 < w8; x0 += 8)
            for (int y0 = 0; y0 < h8; y0 += 8)
            {
                // rows of the tile = src columns x0..x0+7 (8 samples each at y0..)
                var r0 = Px.Load8(src, (x0 + 0) * srcStride + y0).AsInt16(); var r1 = Px.Load8(src, (x0 + 1) * srcStride + y0).AsInt16();
                var r2 = Px.Load8(src, (x0 + 2) * srcStride + y0).AsInt16(); var r3 = Px.Load8(src, (x0 + 3) * srcStride + y0).AsInt16();
                var r4 = Px.Load8(src, (x0 + 4) * srcStride + y0).AsInt16(); var r5 = Px.Load8(src, (x0 + 5) * srcStride + y0).AsInt16();
                var r6 = Px.Load8(src, (x0 + 6) * srcStride + y0).AsInt16(); var r7 = Px.Load8(src, (x0 + 7) * srcStride + y0).AsInt16();
                var a0 = Sse2.UnpackLow(r0, r1).AsInt32(); var a1 = Sse2.UnpackHigh(r0, r1).AsInt32();
                var a2 = Sse2.UnpackLow(r2, r3).AsInt32(); var a3 = Sse2.UnpackHigh(r2, r3).AsInt32();
                var a4 = Sse2.UnpackLow(r4, r5).AsInt32(); var a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
                var a6 = Sse2.UnpackLow(r6, r7).AsInt32(); var a7 = Sse2.UnpackHigh(r6, r7).AsInt32();
                var b0 = Sse2.UnpackLow(a0, a2).AsInt64(); var b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
                var b2 = Sse2.UnpackLow(a1, a3).AsInt64(); var b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
                var b4 = Sse2.UnpackLow(a4, a6).AsInt64(); var b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
                var b6 = Sse2.UnpackLow(a5, a7).AsInt64(); var b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
                Px.Store8(dst, (y0 + 0) * dstStride + x0, Sse2.UnpackLow(b0, b4).AsUInt16());
                Px.Store8(dst, (y0 + 1) * dstStride + x0, Sse2.UnpackHigh(b0, b4).AsUInt16());
                Px.Store8(dst, (y0 + 2) * dstStride + x0, Sse2.UnpackLow(b1, b5).AsUInt16());
                Px.Store8(dst, (y0 + 3) * dstStride + x0, Sse2.UnpackHigh(b1, b5).AsUInt16());
                Px.Store8(dst, (y0 + 4) * dstStride + x0, Sse2.UnpackLow(b2, b6).AsUInt16());
                Px.Store8(dst, (y0 + 5) * dstStride + x0, Sse2.UnpackHigh(b2, b6).AsUInt16());
                Px.Store8(dst, (y0 + 6) * dstStride + x0, Sse2.UnpackLow(b3, b7).AsUInt16());
                Px.Store8(dst, (y0 + 7) * dstStride + x0, Sse2.UnpackHigh(b3, b7).AsUInt16());
            }
        for (int x = 0; x < w; x++)
            for (int y = x < w8 ? h8 : 0; y < h; y++) dst[y * dstStride + x] = src[x * srcStride + y];
    }

    /// <summary>row[x] = (src[b + x] * (64 - frac) + src[b + x + 1] * frac + 32) >> 6 for x &lt; n. For bit depths up to
    /// 10 the sum stays below 65536, so 16-bit vector lanes give the identical result.</summary>
    private static void InterpRun<TP>(Span<TP> row, ReadOnlySpan<TP> src, int b, int frac, int n, int bitDepth) where TP : unmanaged
    {
        int x = 0;
        if (bitDepth <= 10)
        {
            if (Vector256.IsHardwareAccelerated && n >= 16)
            {
                var w0 = Vector256.Create((ushort)(64 - frac)); var w1 = Vector256.Create((ushort)frac);
                var r32 = Vector256.Create((ushort)32);
                for (; x + 16 <= n; x += 16)
                {
                    var a = Px.Load16(src, b + x);
                    var c = Px.Load16(src, b + x + 1);
                    Px.Store16(row, x, Vector256.ShiftRightLogical(a * w0 + c * w1 + r32, 6));
                }
            }
            if (Vector128.IsHardwareAccelerated && x + 8 <= n)
            {
                var w0 = Vector128.Create((ushort)(64 - frac)); var w1 = Vector128.Create((ushort)frac);
                var r32 = Vector128.Create((ushort)32);
                for (; x + 8 <= n; x += 8)
                {
                    var a = Px.Load8(src, b + x);
                    var c = Px.Load8(src, b + x + 1);
                    Px.Store8(row, x, Vector128.ShiftRightLogical(a * w0 + c * w1 + r32, 6));
                }
            }
            // 4 more (the 8-wide loads stay inside src; only 4 lanes are stored)
            if (Vector128.IsHardwareAccelerated && x + 4 <= n && b + x + 9 <= src.Length)
            {
                var a = Px.Load8(src, b + x);
                var c = Px.Load8(src, b + x + 1);
                Px.Store4(row, x, Vector128.ShiftRightLogical(a * Vector128.Create((ushort)(64 - frac)) + c * Vector128.Create((ushort)frac)
                    + Vector128.Create((ushort)32), 6));
                x += 4;
            }
        }
        for (; x < n; x++)
            row[x] = Px.T<TP>((Px.I(src[b + x]) * (64 - frac) + Px.I(src[b + x + 1]) * frac + 32) >> 6);
    }

    /// <summary>InterpRun for n a multiple of 8 at bit depths up to 10, inlined and unchecked (the caller guarantees
    /// src[0 .. n] and dst[0 .. n - 1]): the whole-vector paths.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterpRun8<TP>(ref TP dst, ref TP src, int frac, int n) where TP : unmanaged
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var w0 = Vector256.Create((ushort)(64 - frac)); var w1 = Vector256.Create((ushort)frac);
            for (; x + 16 <= n; x += 16)
                Px.Store16(ref Unsafe.Add(ref dst, x), Vector256.ShiftRightLogical(Px.Load16(ref Unsafe.Add(ref src, x)) * w0
                    + Px.Load16(ref Unsafe.Add(ref src, x + 1)) * w1 + Vector256.Create((ushort)32), 6));
        }
        if (x < n)
        {
            var w0 = Vector128.Create((ushort)(64 - frac)); var w1 = Vector128.Create((ushort)frac);
            for (; x < n; x += 8)
                Px.Store8(ref Unsafe.Add(ref dst, x), Vector128.ShiftRightLogical(Px.Load8(ref Unsafe.Add(ref src, x)) * w0
                    + Px.Load8(ref Unsafe.Add(ref src, x + 1)) * w1 + Vector128.Create((ushort)32), 6));
        }
    }

    public static void PredZ1_16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter, int bitDepth) where TP : unmanaged
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dx = Av1Tables.DrIntraDerivative[angle >> 1];
        int maxBaseX;

        Span<TP> topBuf = stackalloc TP[128];
        bool upsample = enableEdgeFilter && GetUpsample(width + height, 90 - angle, isSm);
        bool useTopBuf;
        int topOffset = 0;

        if (upsample)
        {
            UpsampleEdge16(topBuf, width + height,
                         edgeBuf, center + 1, -1,
                         width + Math.Min(width, height), bitDepth);
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
                FilterEdge16(topBuf, width + height, 0, width + height,
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
        if (!upsample)
        {
            ReadOnlySpan<TP> src = useTopBuf ? (ReadOnlySpan<TP>)topBuf : edgeBuf.Slice(topOffset);
            TP fill = src[maxBaseX];
            for (int y = 0, xpos = dx; y < height; y++, xpos += dx)
            {
                var row = dst.Slice(y * dstStride, width);
                int b = xpos >> 6, n = Math.Clamp(maxBaseX - b, 0, width);
                InterpRun(row, src, b, xpos & 0x3E, n, bitDepth);
                if (n < width) row.Slice(n).Fill(fill);
            }
            return;
        }
        for (int y = 0, xpos = dx; y < height; y++, xpos += dx)
        {
            var row = dst.Slice(y * dstStride, width);
            int frac = xpos & 0x3E;

            for (int x = 0, @base = xpos >> 6; x < width; x++, @base += baseInc)
            {
                if (@base < maxBaseX)
                {
                    int s0 = Px.I(useTopBuf ? topBuf[@base] : edgeBuf[topOffset + @base]);
                    int s1 = Px.I(useTopBuf ? topBuf[@base + 1] : edgeBuf[topOffset + @base + 1]);
                    int v = s0 * (64 - frac) + s1 * frac;
                    row[x] = Px.T<TP>((v + 32) >> 6);
                }
                else
                {
                    TP fill = useTopBuf ? topBuf[maxBaseX] : edgeBuf[topOffset + maxBaseX];
                    row.Slice(x, width - x).Fill(fill);
                    break;
                }
            }
        }
    }

    public static void PredZ2_16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter,
        int maxWidth, int maxHeight, int bitDepth) where TP : unmanaged
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dy = Av1Tables.DrIntraDerivative[(angle - 90) >> 1];
        int dx = Av1Tables.DrIntraDerivative[(180 - angle) >> 1];

        bool upsampleLeft = enableEdgeFilter && GetUpsample(width + height, 180 - angle, isSm);
        bool upsampleAbove = enableEdgeFilter && GetUpsample(width + height, angle - 90, isSm);

        Span<TP> edge = stackalloc TP[129];
        int edgeCenter = 64;

        if (upsampleAbove)
        {
            UpsampleEdge16(edge.Slice(edgeCenter), width + 1,
                         edgeBuf, center, 0, width + 1, bitDepth);
            dx <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, angle - 90, isSm) : 0;
            if (filterStrength > 0)
            {
                FilterEdge16(edge.Slice(edgeCenter + 1), width, 0, maxWidth,
                           edgeBuf, center + 1, -1, width, filterStrength);
            }
            else
            {
                edgeBuf.Slice(center + 1, width).CopyTo(edge.Slice(edgeCenter + 1, width));
            }
        }

        if (upsampleLeft)
        {
            UpsampleEdge16(edge.Slice(edgeCenter - height * 2), height + 1,
                         edgeBuf, center - height, 0, height + 1, bitDepth);
            dy <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, 180 - angle, isSm) : 0;
            if (filterStrength > 0)
            {
                FilterEdge16(edge.Slice(edgeCenter - height), height,
                           height - maxHeight, height,
                           edgeBuf, center - height, 0, height + 1, filterStrength);
            }
            else
            {
                edgeBuf.Slice(center - height, height).CopyTo(edge.Slice(edgeCenter - height, height));
            }
        }

        edge[edgeCenter] = edgeBuf[center];

        int baseIncX = 1 + (upsampleAbove ? 1 : 0);
        int leftStep = 1 + (upsampleLeft ? 1 : 0);

        if (!upsampleAbove)
        {
            ReadOnlySpan<TP> top = edge.Slice(edgeCenter);
            Span<int> xsRow = stackalloc int[height];
            // Whole vectors (as libaom's dr_prediction_z2_HxW): a row's top run starts 8-aligned at or below xs (the lanes
            // below xs read the left samples just before the corner, in bounds, and are overwritten by the left part).
            bool whole = !upsampleLeft && bitDepth <= 10 && width >= 8 && height >= 8 && Vector128.IsHardwareAccelerated;
            for (int y = 0, xpos = (1 << 6) - dx; y < height; y++, xpos -= dx)
            {
                var row = dst.Slice(y * dstStride, width);
                int baseX = xpos >> 6;
                int xs = xsRow[y] = Math.Clamp(-baseX, 0, width);   // pixels x >= xs read the top edge (baseX + x >= 0)
                if (xs >= width) continue;
                if (whole)
                {
                    int x0 = xs & ~7;
                    InterpRun8(ref Unsafe.Add(ref MemoryMarshal.GetReference(row), x0), ref Unsafe.Add(ref MemoryMarshal.GetReference(edge), edgeCenter + baseX + x0),
                        xpos & 0x3E, width - x0);
                }
                else InterpRun(row.Slice(xs), top, baseX + xs, xpos & 0x3E, width - xs, bitDepth);
            }
            if (whole)
            {
                // The left region by column (see below), each run 8-aligned: it starts at the vector boundary at or above its
                // first left row (a left edge padded by 8 in front covers the extra rows, whose values are discarded), the
                // columns are transposed as whole 8 x 8 tiles, and each row's left part blended in.
                int xsMax = xsRow[height - 1];
                if (xsMax == 0) return;
                int xsMax8 = (xsMax + 7) & ~7;
                Span<TP> revLp = stackalloc TP[height + 9];   // revLp[k + 8] = edge[edgeCenter - k]
                for (int k = 0; k <= height; k++) revLp[k + 8] = edge[edgeCenter - k];
                Span<TP> cols = stackalloc TP[xsMax8 * height];
                for (int x = 0, ys = 0; x < xsMax; x++)
                {
                    while (xsRow[ys] <= x) ys++;
                    int ypos = (ys << 6) - (x + 1) * dy, n = height - ys;
                    if ((ypos >> 6) + 1 < 0 || (ypos >> 6) + 1 + n > height)
                    {
                        // past the left edge's ends (only off the AV1 angle grid): the per-pixel form
                        var col = cols.Slice(x * height + ys, n);
                        for (int j = 0; j < n; j++, ypos += 64)
                        {
                            int baseY = ypos >> 6, fracY = ypos & 0x3E;
                            col[j] = Px.T<TP>((Px.I(edge[edgeCenter - 1 - baseY]) * (64 - fracY) + Px.I(edge[edgeCenter - 2 - baseY]) * fracY + 32) >> 6);
                        }
                        continue;
                    }
                    int ys8 = ys & ~7, ypos8 = ypos - ((ys - ys8) << 6);
                    InterpRun8(ref Unsafe.Add(ref MemoryMarshal.GetReference(cols), x * height + ys8), ref Unsafe.Add(ref MemoryMarshal.GetReference(revLp), (ypos8 >> 6) + 9),
                        ypos & 0x3E, height - ys8);
                }
                Span<TP> rows = stackalloc TP[height * xsMax8];
                TransposeU16<TP>(cols, height, rows, xsMax8, xsMax8, height);
                var lane = Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7);
                ReadOnlySpan<TP> rowsR = rows;
                for (int y = 0; y < height; y++)
                {
                    int xs = xsRow[y], x = 0, o = y * dstStride, ro = y * xsMax8;
                    for (; x + 8 <= xs; x += 8) Px.Store8(dst, o + x, Px.Load8(rowsR, ro + x));
                    if (x < xs)
                    {
                        var m = Vector128.LessThan(lane, Vector128.Create((short)(xs - x))).AsUInt16();
                        Px.Store8(dst, o + x, Vector128.ConditionalSelect(m, Px.Load8(rowsR, ro + x), Px.Load8((ReadOnlySpan<TP>)dst, o + x)));
                    }
                }
                return;
            }
            if (!upsampleLeft)
            {
                // The left-edge pixels by column: down a column the left position steps one sample per row at a fixed
                // fraction, a run over the left edge read upwards (revL[k] = edge[edgeCenter - k], k = baseY + 1). The
                // columns of the left region (x < xsRow[y], widest on the last row) are built column-major, transposed
                // with vector tiles, and each row's left part copied in.
                int xsMax = xsRow[height - 1];
                if (xsMax == 0) return;
                Span<TP> revL = stackalloc TP[height + 1];
                for (int k = 0; k <= height; k++) revL[k] = edge[edgeCenter - k];
                Span<TP> cols = stackalloc TP[xsMax * height];   // column x at x * height (rows above its run unused)
                for (int x = 0, ys = 0; x < xsMax; x++)
                {
                    while (xsRow[ys] <= x) ys++;   // rows ys.. read the left edge at this column (row height - 1 does)
                    int ypos = (ys << 6) - (x + 1) * dy, n = height - ys;
                    var col = cols.Slice(x * height + ys, n);
                    if ((ypos >> 6) + 1 < 0 || (ypos >> 6) + 1 + n > height)
                    {
                        // past the left edge's ends (only off the AV1 angle grid): the per-pixel form
                        for (int j = 0; j < n; j++, ypos += 64)
                        {
                            int baseY = ypos >> 6, fracY = ypos & 0x3E;
                            col[j] = Px.T<TP>((Px.I(edge[edgeCenter - 1 - baseY]) * (64 - fracY) + Px.I(edge[edgeCenter - 2 - baseY]) * fracY + 32) >> 6);
                        }
                        continue;
                    }
                    InterpRun(col, revL, (ypos >> 6) + 1, ypos & 0x3E, n, bitDepth);
                }
                Span<TP> rows = stackalloc TP[height * xsMax];
                TransposeU16<TP>(cols, height, rows, xsMax, xsMax, height);
                for (int y = 0; y < height; y++)
                    if (xsRow[y] > 0) rows.Slice(y * xsMax, xsRow[y]).CopyTo(dst.Slice(y * dstStride, xsRow[y]));
                return;
            }
            for (int y = 0; y < height; y++)
            {
                var row = dst.Slice(y * dstStride, width);
                for (int x = 0, ypos = (y << 7) - dy; x < xsRow[y]; x++, ypos -= dy)
                {
                    int baseY = ypos >> 6;
                    int fracY = ypos & 0x3E;
                    int v = Px.I(edge[edgeCenter - leftStep - baseY]) * (64 - fracY) +
                            Px.I(edge[edgeCenter - leftStep - baseY - 1]) * fracY;
                    row[x] = Px.T<TP>((v + 32) >> 6);
                }
            }
            return;
        }

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
                    v = Px.I(edge[edgeCenter + baseX]) * (64 - fracX) +
                        Px.I(edge[edgeCenter + baseX + 1]) * fracX;
                }
                else
                {
                    int baseY = ypos >> 6;
                    int fracY = ypos & 0x3E;
                    v = Px.I(edge[edgeCenter - leftStep - baseY]) * (64 - fracY) +
                        Px.I(edge[edgeCenter - leftStep - baseY - 1]) * fracY;
                }
                row[x] = Px.T<TP>((v + 32) >> 6);
            }
        }
    }

    public static void PredZ3_16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int angle, bool enableEdgeFilter, int bitDepth) where TP : unmanaged
    {
        bool isSm = ((angle >> 9) & 1) != 0;
        angle &= 511;
        int dy = Av1Tables.DrIntraDerivative[(270 - angle) >> 1];

        Span<TP> leftBuf = stackalloc TP[128];
        int maxBaseY;
        bool upsample = enableEdgeFilter && GetUpsample(width + height, angle - 180, isSm);
        bool useLeftBuf;
        int leftOffset = 0;

        if (upsample)
        {
            UpsampleEdge16(leftBuf, width + height,
                         edgeBuf, center - (width + height),
                         Math.Max(width - height, 0), width + height + 1, bitDepth);
            useLeftBuf = true;
            leftOffset = 2 * (width + height) - 2;
            maxBaseY = 2 * (width + height) - 2;
            dy <<= 1;
        }
        else
        {
            int filterStrength = enableEdgeFilter
                ? GetFilterStrength(width + height, angle - 180, isSm) : 0;

            if (filterStrength > 0)
            {
                FilterEdge16(leftBuf, width + height, 0, width + height,
                           edgeBuf, center - (width + height),
                           Math.Max(width - height, 0), width + height + 1, filterStrength);
                useLeftBuf = true;
                leftOffset = width + height - 1;
                maxBaseY = width + height - 1;
            }
            else
            {
                useLeftBuf = false;
                leftOffset = center - 1;
                maxBaseY = height + Math.Min(width, height) - 1;
            }
        }

        int baseInc = 1 + (upsample ? 1 : 0);
        if (!upsample)
        {
            // rev[i] = left sample i steps down (the original reads src[leftOffset - base] and its successor)
            ReadOnlySpan<TP> srcL = useLeftBuf ? (ReadOnlySpan<TP>)leftBuf : edgeBuf;
            Span<TP> rev = stackalloc TP[maxBaseY + 1];
            for (int i = 0; i <= maxBaseY; i++) rev[i] = srcL[leftOffset - i];
            TP fill = rev[maxBaseY];
            // column x is a run down the left edge: computed as row x of a column-major block, then transposed
            Span<TP> cols = stackalloc TP[width * height];
            for (int x = 0, ypos = dy; x < width; x++, ypos += dy)
            {
                var col = cols.Slice(x * height, height);
                int b = ypos >> 6, n = Math.Clamp(maxBaseY - b, 0, height);
                InterpRun(col, rev, b, ypos & 0x3E, n, bitDepth);
                if (n < height) col.Slice(n).Fill(fill);
            }
            TransposeU16<TP>(cols, height, dst, dstStride, width, height);
            return;
        }
        for (int x = 0, ypos = dy; x < width; x++, ypos += dy)
        {
            int frac = ypos & 0x3E;
            for (int y = 0, @base = ypos >> 6; y < height; y++, @base += baseInc)
            {
                if (@base < maxBaseY)
                {
                    int s0 = Px.I(useLeftBuf ? leftBuf[leftOffset - @base] : edgeBuf[leftOffset - @base]);
                    int s1 = Px.I(useLeftBuf ? leftBuf[leftOffset - @base - 1] : edgeBuf[leftOffset - @base - 1]);
                    int v = s0 * (64 - frac) + s1 * frac;
                    dst[y * dstStride + x] = Px.T<TP>((v + 32) >> 6);
                }
                else
                {
                    TP fill = useLeftBuf ? leftBuf[leftOffset - maxBaseY] : edgeBuf[leftOffset - maxBaseY];
                    for (; y < height; y++)
                        dst[y * dstStride + x] = fill;
                    break;
                }
            }
        }
    }

    private static void FilterEdge16<TP>(
        Span<TP> output, int sz,
        int limFrom, int limTo,
        ReadOnlySpan<TP> input, int inputOffset,
        int from, int to, int strength) where TP : unmanaged
    {
        // taps {k0, k1, k2, k1, k0}: {0,4,8,4,0}, {0,5,6,5,0}, {2,4,4,4,2}
        int k0 = strength >= 3 ? 2 : 0, k1 = strength == 1 ? 4 : strength == 2 ? 5 : 4, k2 = strength == 1 ? 8 : strength == 2 ? 6 : 4;
        // the input clamped to [from, to) once: pad[k] = input[clamp(k - 2)] (+16 slack for the vector tail)
        Span<TP> pad = stackalloc TP[sz + 4 + 16];
        TP lo = input[inputOffset + from], hi = input[inputOffset + to - 1];
        {
            // pad[k] = input[clamp(k - 2, from, to - 1)]: [0, a) = lo, [a, b) copied, [b, end) = hi
            int n = sz + 4 + 16, a = Math.Clamp(from + 2, 0, n), b = Math.Clamp(to + 2, a, n);
            pad.Slice(0, a).Fill(lo);
            if (b > a) input.Slice(inputOffset + a - 2, b - a).CopyTo(pad.Slice(a));
            pad.Slice(b).Fill(hi);
        }
        ReadOnlySpan<TP> p = pad;
        int i = 0, end = Math.Min(limTo, sz);
        for (; i < Math.Min(sz, limFrom); i++) output[i] = pad[i + 2];
        // 16 * 4095 + 8 < 65536: ushort lanes are exact up to 12 bits
        if (Vector256.IsHardwareAccelerated)
        {
            var v0 = Vector256.Create((ushort)k0); var v1 = Vector256.Create((ushort)k1); var v2 = Vector256.Create((ushort)k2);
            var r8 = Vector256.Create((ushort)8);
            for (; i + 16 <= end; i += 16)
            {
                var s = (Px.Load16(p, i) + Px.Load16(p, i + 4)) * v0
                      + (Px.Load16(p, i + 1) + Px.Load16(p, i + 3)) * v1
                      + Px.Load16(p, i + 2) * v2 + r8;
                Px.Store16(output, i, Vector256.ShiftRightLogical(s, 4));
            }
        }
        if (Vector128.IsHardwareAccelerated && i + 8 <= end)
        {
            var s = (Px.Load8(p, i) + Px.Load8(p, i + 4)) * Vector128.Create((ushort)k0)
                  + (Px.Load8(p, i + 1) + Px.Load8(p, i + 3)) * Vector128.Create((ushort)k1)
                  + Px.Load8(p, i + 2) * Vector128.Create((ushort)k2) + Vector128.Create((ushort)8);
            Px.Store8(output, i, Vector128.ShiftRightLogical(s, 4));
            i += 8;
        }
        for (; i < end; i++)
            output[i] = Px.T<TP>(((Px.I(pad[i]) + Px.I(pad[i + 4])) * k0 + (Px.I(pad[i + 1]) + Px.I(pad[i + 3])) * k1 + Px.I(pad[i + 2]) * k2 + 8) >> 4);
        for (; i < sz; i++) output[i] = pad[i + 2];
    }

    private static void UpsampleEdge16<TP>(
        Span<TP> output, int hsz,
        ReadOnlySpan<TP> input, int inputOffset,
        int from, int to, int bitDepth) where TP : unmanaged
    {
        int max = (1 << bitDepth) - 1;
        // p[k] = input[clamp(k - 1, from, to - 1)] for k <= hsz + 2 (+8 slack for the vector loads)
        int np = hsz + 2 + 9;
        Span<TP> p = stackalloc TP[np];
        for (int k = 0; k < np; k++) p[k] = input[inputOffset + Math.Clamp(k - 1, from, to - 1)];
        ReadOnlySpan<TP> pr = p;
        int n = hsz - 1, i = 0;
        // output[2i] = p[i + 1], output[2i + 1] = clip((-p[i] + 9 p[i + 1] + 9 p[i + 2] - p[i + 3] + 8) >> 4) (16-bit exact to 10 bits)
        if (bitDepth <= 10 && Sse2.IsSupported)
            for (; i + 8 <= n; i += 8)
            {
                var a = Px.Load8(pr, i).AsInt16(); var b1 = Px.Load8(pr, i + 1).AsInt16();
                var c = Px.Load8(pr, i + 2).AsInt16(); var d = Px.Load8(pr, i + 3).AsInt16();
                var s = Vector128.ShiftRightArithmetic((b1 + c) * Vector128.Create((short)9) - a - d + Vector128.Create((short)8), 4);
                var f = Vector128.Min(Vector128.Max(s, Vector128<short>.Zero), Vector128.Create((short)max)).AsUInt16();
                Px.Store8(output, 2 * i, Sse2.UnpackLow(b1.AsUInt16(), f));
                Px.Store8(output, 2 * i + 8, Sse2.UnpackHigh(b1.AsUInt16(), f));
            }
        for (; i < n; i++)
        {
            output[i * 2] = p[i + 1];
            int s = -Px.I(p[i]) + 9 * (Px.I(p[i + 1]) + Px.I(p[i + 2])) - Px.I(p[i + 3]);
            output[i * 2 + 1] = Px.T<TP>(Math.Clamp((s + 8) >> 4, 0, max));
        }
        output[i * 2] = p[i + 1];
    }

    public static void PredFilter16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int filterIndex, int bitDepth) where TP : unmanaged
    {
        filterIndex &= 511;
        int max = (1 << bitDepth) - 1;
        int topIdx = center + 1;
        int dstOffset = 0;
        if (Vector256.IsHardwareAccelerated) { PredFilter16V(dst, dstStride, edgeBuf, center, width, height, filterIndex, max); return; }

        for (int y = 0; y < height; y += 2)
        {
            int topleftEdgeIdx = center - y;
            for (int x = 0; x < width; x += 4)
            {
                int p0, p1, p2, p3, p4, p5, p6;

                if (y == 0)
                    p0 = Px.I(edgeBuf[topleftEdgeIdx]);
                else if (x == 0)
                    p0 = Px.I(edgeBuf[center - y]);
                else
                    p0 = Px.I(dst[dstOffset - dstStride + x - 1]);

                if (y == 0)
                {
                    p1 = Px.I(edgeBuf[topIdx + x]);
                    p2 = Px.I(edgeBuf[topIdx + x + 1]);
                    p3 = Px.I(edgeBuf[topIdx + x + 2]);
                    p4 = Px.I(edgeBuf[topIdx + x + 3]);
                }
                else
                {
                    p1 = Px.I(dst[dstOffset - dstStride + x]);
                    p2 = Px.I(dst[dstOffset - dstStride + x + 1]);
                    p3 = Px.I(dst[dstOffset - dstStride + x + 2]);
                    p4 = Px.I(dst[dstOffset - dstStride + x + 3]);
                }

                if (x == 0)
                {
                    p5 = Px.I(edgeBuf[center - y - 1]);
                    p6 = Px.I(edgeBuf[center - y - 2]);
                }
                else
                {
                    p5 = Px.I(dst[dstOffset + x - 1]);
                    p6 = Px.I(dst[dstOffset + dstStride + x - 1]);
                }

                int fltPos = 0;
                for (int yy = 0; yy < 2; yy++)
                {
                    for (int xx = 0; xx < 4; xx++, fltPos++)
                    {
                        int acc = Av1Tables.FilterIntraTaps[filterIndex, fltPos] * p0 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 8] * p1 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 16] * p2 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 24] * p3 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 32] * p4 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 40] * p5 +
                                  Av1Tables.FilterIntraTaps[filterIndex, fltPos + 48] * p6;
                        dst[dstOffset + yy * dstStride + x + xx] = Px.T<TP>(Math.Clamp((acc + 8) >> 4, 0, max));
                    }
                }

                if (y == 0)
                    topleftEdgeIdx = topIdx + x + 3;
            }

            dstOffset += dstStride * 2;
        }
    }

    // FilterIntraTaps rearranged: [index][k 0..6][position 0..7] = tap of neighbour p_k for output position (row 0 x 0-3,
    // row 1 x 0-3), so a 4 x 2 block is seven broadcast multiply-adds.
    private static readonly int[] FilterTapsByNeighbour = BuildFilterTapsByNeighbour();
    private static int[] BuildFilterTapsByNeighbour()
    {
        var t = new int[5 * 7 * 8];
        for (int f = 0; f < 5; f++)
            for (int k = 0; k < 7; k++)
                for (int pos = 0; pos < 8; pos++) t[(f * 7 + k) * 8 + pos] = Av1Tables.FilterIntraTaps[f, pos + 8 * k];
        return t;
    }

    // PredFilter16 with one 4 x 2 block per vector (the blocks stay a serial chain: each reads its left / above neighbours'
    // outputs); identical arithmetic.
    private static void PredFilter16V<TP>(Span<TP> dst, int dstStride, ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height, int filterIndex, int max) where TP : unmanaged
    {
        ref int tp = ref MemoryMarshal.GetArrayDataReference(FilterTapsByNeighbour);
        tp = ref Unsafe.Add(ref tp, filterIndex * 56);
        var k0 = Vector256.LoadUnsafe(ref tp); var k1 = Vector256.LoadUnsafe(ref tp, 8); var k2 = Vector256.LoadUnsafe(ref tp, 16);
        var k3 = Vector256.LoadUnsafe(ref tp, 24); var k4 = Vector256.LoadUnsafe(ref tp, 32); var k5 = Vector256.LoadUnsafe(ref tp, 40);
        var k6 = Vector256.LoadUnsafe(ref tp, 48);
        var r8 = Vector256.Create(8); var vmax = Vector256.Create(max);
        int topIdx = center + 1, dstOffset = 0;
        for (int y = 0; y < height; y += 2)
        {
            int topleftEdgeIdx = center - y;
            for (int x = 0; x < width; x += 4)
            {
                int p0 = Px.I(y == 0 ? edgeBuf[topleftEdgeIdx] : x == 0 ? edgeBuf[center - y] : dst[dstOffset - dstStride + x - 1]);
                int p1, p2, p3, p4;
                if (y == 0) { p1 = Px.I(edgeBuf[topIdx + x]); p2 = Px.I(edgeBuf[topIdx + x + 1]); p3 = Px.I(edgeBuf[topIdx + x + 2]); p4 = Px.I(edgeBuf[topIdx + x + 3]); }
                else
                {
                    int a = dstOffset - dstStride + x;
                    p1 = Px.I(dst[a]); p2 = Px.I(dst[a + 1]); p3 = Px.I(dst[a + 2]); p4 = Px.I(dst[a + 3]);
                }
                int p5, p6;
                if (x == 0) { p5 = Px.I(edgeBuf[center - y - 1]); p6 = Px.I(edgeBuf[center - y - 2]); }
                else { p5 = Px.I(dst[dstOffset + x - 1]); p6 = Px.I(dst[dstOffset + dstStride + x - 1]); }
                var acc = k0 * Vector256.Create(p0) + k1 * Vector256.Create(p1) + k2 * Vector256.Create(p2) + k3 * Vector256.Create(p3)
                        + k4 * Vector256.Create(p4) + k5 * Vector256.Create(p5) + k6 * Vector256.Create(p6);
                var o = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(acc + r8, 4), Vector256<int>.Zero), vmax);
                var n = Vector128.Narrow(o.GetLower().AsUInt32(), o.GetUpper().AsUInt32());
                Px.Store4(dst, dstOffset + x, n);
                Px.Store4(dst, dstOffset + dstStride + x, Vector128.CreateScalarUnsafe(n.AsUInt64().GetElement(1)).AsUInt16());
                if (y == 0) topleftEdgeIdx = topIdx + x + 3;
            }
            dstOffset += dstStride * 2;
        }
    }

    public static void PredCfl16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha, int bitDepth) where TP : unmanaged
    {
        int dc = DcGenBoth16(edgeBuf, center, width, height, bitDepth);
        CflPred16(dst, dstStride, width, height, dc, ac, alpha, bitDepth);
    }

    public static void PredCflTop16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha, int bitDepth) where TP : unmanaged
    {
        int dc = DcGenTop16(edgeBuf, center, width);
        CflPred16(dst, dstStride, width, height, dc, ac, alpha, bitDepth);
    }

    public static void PredCflLeft16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<TP> edgeBuf, int center,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha, int bitDepth) where TP : unmanaged
    {
        int dc = DcGenLeft16(edgeBuf, center, height);
        CflPred16(dst, dstStride, width, height, dc, ac, alpha, bitDepth);
    }

    public static void PredCfl12816<TP>(
        Span<TP> dst, int dstStride,
        int width, int height,
        ReadOnlySpan<short> ac, int alpha, int bitDepth) where TP : unmanaged
    {
        int dc = (1 << bitDepth) >> 1;
        CflPred16(dst, dstStride, width, height, dc, ac, alpha, bitDepth);
    }

    private static void CflPred16<TP>(
        Span<TP> dst, int dstStride,
        int width, int height, int dc,
        ReadOnlySpan<short> ac, int alpha, int bitDepth) where TP : unmanaged
    {
        int max = (1 << bitDepth) - 1;
        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            var acRow = ac.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                int diff = alpha * acRow[x];
                int sign = diff >> 31;
                int absDiff = (Math.Abs(diff) + 32) >> 6;
                row[x] = Px.T<TP>(Math.Clamp(dc + (absDiff ^ sign) - sign, 0, max));
            }
        }
    }

    /// <summary>CFL AC generation from reconstructed luma. See <see cref="CflAc"/>.</summary>
    public static void CflAc16<TP>(
        Span<short> ac,
        ReadOnlySpan<TP> luma, int lumaStride,
        int wPad, int hPad,
        int width, int height,
        int ssHor, int ssVer) where TP : unmanaged
    {
        int acIdx = 0;

        for (int y = 0; y < height - 4 * hPad; y++)
        {
            int x;
            for (x = 0; x < width - 4 * wPad; x++)
            {
                int sum = Px.I(luma[y * (lumaStride << ssVer) + (x << ssHor)]);
                if (ssHor != 0) sum += Px.I(luma[y * (lumaStride << ssVer) + x * 2 + 1]);
                if (ssVer != 0)
                {
                    sum += Px.I(luma[(y * (lumaStride << ssVer)) + lumaStride + (x << ssHor)]);
                    if (ssHor != 0) sum += Px.I(luma[(y * (lumaStride << ssVer)) + lumaStride + x * 2 + 1]);
                }
                ac[acIdx + x] = (short)(sum << (1 + (ssVer == 0 ? 1 : 0) + (ssHor == 0 ? 1 : 0)));
            }
            for (; x < width; x++)
                ac[acIdx + x] = ac[acIdx + x - 1];
            acIdx += width;
        }

        for (int y = height - 4 * hPad; y < height; y++)
        {
            ac.Slice(acIdx - width, width).CopyTo(ac.Slice(acIdx, width));
            acIdx += width;
        }

        int log2sz = BitOperations.TrailingZeroCount((uint)width) +
                     BitOperations.TrailingZeroCount((uint)height);
        int dcSum = (1 << log2sz) >> 1;
        for (int i = 0; i < width * height; i++)
            dcSum += ac[i];
        dcSum >>= log2sz;
        for (int i = 0; i < width * height; i++)
            ac[i] -= (short)dcSum;
    }

    public static void PredPalette16<TP>(
        Span<TP> dst, int dstStride,
        ReadOnlySpan<ushort> palette,
        ReadOnlySpan<byte> indices,
        int width, int height) where TP : unmanaged
    {
        int idxPos = 0;
        for (int y = 0; y < height; y++)
        {
            var row = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x += 2)
            {
                byte packed = indices[idxPos++];
                row[x] = Px.T<TP>(palette[packed & 7]);
                row[x + 1] = Px.T<TP>(palette[packed >> 4]);
            }
        }
    }
}
