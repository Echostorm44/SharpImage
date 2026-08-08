using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// AV1 block reconstruction — ties together intra prediction, coefficient decode,
/// inverse transforms, and CFL into actual pixel reconstruction.
/// Port of dav1d recon_tmpl.c (recon_b_intra, prepare_intra_edges).
/// </summary>
public static class Av1Reconstruction
{
    // ========================================================================
    // Edge preparation tables (from ipred_prepare_tmpl.c)
    // ========================================================================

    /// <summary>Angular mode base angles (from VERT_PRED through VERT_LEFT_PRED).</summary>
    private static readonly byte[] ModeToAngleMap = { 90, 180, 45, 135, 113, 157, 203, 67 };

    /// <summary>Edge pixel requirements per implementation mode (indexed by implMode 0-13).</summary>
    private static readonly byte[] EdgeNeeds = new byte[14];

    private const byte NeedsLeft = 1;
    private const byte NeedsTop = 2;
    private const byte NeedsTopleft = 4;
    private const byte NeedsTopright = 8;
    private const byte NeedsBottomleft = 16;

    // Implementation mode constants (distinct values, no overlaps)
    private const int ImplDc = 0, ImplVert = 1, ImplHor = 2, ImplPaeth = 3;
    private const int ImplSmooth = 4, ImplSmoothV = 5, ImplSmoothH = 6;
    private const int ImplLeftDc = 7, ImplTopDc = 8, ImplDc128 = 9;
    private const int ImplZ1 = 10, ImplZ2 = 11, ImplZ3 = 12;
    private const int ImplFilter = 13;

    static Av1Reconstruction()
    {
        EdgeNeeds[ImplDc] = NeedsLeft | NeedsTop;
        EdgeNeeds[ImplVert] = NeedsTop;
        EdgeNeeds[ImplHor] = NeedsLeft;
        EdgeNeeds[ImplPaeth] = NeedsLeft | NeedsTop | NeedsTopleft;
        EdgeNeeds[ImplSmooth] = NeedsLeft | NeedsTop;
        EdgeNeeds[ImplSmoothV] = NeedsLeft | NeedsTop;
        EdgeNeeds[ImplSmoothH] = NeedsLeft | NeedsTop;
        EdgeNeeds[ImplLeftDc] = NeedsLeft;
        EdgeNeeds[ImplTopDc] = NeedsTop;
        EdgeNeeds[ImplDc128] = 0;
        EdgeNeeds[ImplZ1] = NeedsTop | NeedsTopright | NeedsTopleft;
        EdgeNeeds[ImplZ2] = NeedsLeft | NeedsTop | NeedsTopleft;
        EdgeNeeds[ImplZ3] = NeedsLeft | NeedsBottomleft | NeedsTopleft;
        EdgeNeeds[ImplFilter] = NeedsLeft | NeedsTop | NeedsTopleft;
    }

    // ========================================================================
    // Prepare intra edges (from ipred_prepare_tmpl.c)
    // ========================================================================

    /// <summary>
    /// Gathers reference edge pixels for intra prediction.
    /// Returns the actual prediction mode to use (may differ from input for DC/paeth/angular).
    /// </summary>
    /// <param name="x">Block x position in 4x4 units.</param>
    /// <param name="haveLeft">True if left neighbor is available.</param>
    /// <param name="y">Block y position in 4x4 units.</param>
    /// <param name="haveTop">True if top neighbor is available.</param>
    /// <param name="w">Frame width in 4x4 units.</param>
    /// <param name="h">Frame height in 4x4 units.</param>
    /// <param name="edgeFlags">Edge availability flags.</param>
    /// <param name="dst">Destination pixel buffer (current block position).</param>
    /// <param name="dstStride">Destination stride in pixels.</param>
    /// <param name="topSbEdge">Pre-filter top superblock edge (null if not at SB boundary).</param>
    /// <param name="mode">Input prediction mode.</param>
    /// <param name="angle">In/out angle (modified for angular modes).</param>
    /// <param name="tw">Transform block width in 4x4 units.</param>
    /// <param name="th">Transform block height in 4x4 units.</param>
    /// <param name="filterEdge">Whether intra edge filtering is enabled.</param>
    /// <param name="topleftOut">Output edge buffer: [-th*4..0..tw*4+tw*4].</param>
    /// <param name="bitdepth">Bit depth (8 or 10).</param>
    public static int PrepareIntraEdges(
        int x, bool haveLeft,
        int y, bool haveTop,
        int w, int h,
        Av1EdgeFlags edgeFlags,
        ReadOnlySpan<byte> dst, int dstOff, int dstStride,
        ReadOnlySpan<byte> topSbEdge,
        Av1IntraPredMode mode, ref int angle,
        int tw, int th, bool filterEdge,
        Span<byte> topleftOut, int topleftCenter,
        int bitdepth)
    {
        // Resolve to implementation mode
        int implMode;
        switch (mode)
        {
            case Av1IntraPredMode.Vertical:
            case Av1IntraPredMode.Horizontal:
            case Av1IntraPredMode.DiagDownLeft:
            case Av1IntraPredMode.DiagDownRight:
            case Av1IntraPredMode.VerticalRight:
            case Av1IntraPredMode.HorizontalDown:
            case Av1IntraPredMode.HorizontalUp:
            case Av1IntraPredMode.VerticalLeft:
            {
                angle = ModeToAngleMap[(int)mode - (int)Av1IntraPredMode.Vertical] + 3 * angle;

                if (angle <= 90)
                    implMode = angle < 90 && haveTop ? ImplZ1 : ImplVert;
                else if (angle < 180)
                    implMode = ImplZ2;
                else
                    implMode = angle > 180 && haveLeft ? ImplZ3 : ImplHor;
                break;
            }
            case Av1IntraPredMode.Dc:
            {
                if (!haveLeft && !haveTop) implMode = ImplDc128;
                else if (!haveLeft) implMode = ImplTopDc;
                else if (!haveTop) implMode = ImplLeftDc;
                else implMode = ImplDc;
                break;
            }
            case Av1IntraPredMode.Paeth:
            {
                if (!haveLeft && !haveTop) implMode = ImplDc128;
                else if (!haveLeft) implMode = ImplVert;
                else if (!haveTop) implMode = ImplHor;
                else implMode = ImplPaeth;
                break;
            }
            case Av1IntraPredMode.Smooth: implMode = ImplSmooth; break;
            case Av1IntraPredMode.SmoothV: implMode = ImplSmoothV; break;
            case Av1IntraPredMode.SmoothH: implMode = ImplSmoothH; break;
            case (Av1IntraPredMode)13: implMode = ImplFilter; break; // Filter intra (same value as ChromaFromLuma)
            default: implMode = ImplDc128; break;
        }

        byte needs = EdgeNeeds[implMode];

        // Resolve top source pointer
        ReadOnlySpan<byte> dstTop = default;
        int dstTopOffset = 0;
        if (haveTop && ((needs & (NeedsTop | NeedsTopleft)) != 0 ||
            ((needs & NeedsLeft) != 0 && !haveLeft)))
        {
            if (!topSbEdge.IsEmpty)
            {
                dstTop = topSbEdge;
                dstTopOffset = x * 4;
            }
            else
            {
                dstTop = dst;
                dstTopOffset = dstOff - dstStride;
            }
        }

        // Left edge pixels
        if ((needs & NeedsLeft) != 0)
        {
            int sz = th * 4;
            int leftStart = topleftCenter - sz; // left[0..sz-1] at topleftOut[topleftCenter - sz .. topleftCenter - 1]

            if (haveLeft)
            {
                int pxHave = Math.Min(sz, (h - y) * 4);
                for (int i = 0; i < pxHave; i++)
                    topleftOut[leftStart + sz - 1 - i] = dst[dstOff + dstStride * i - 1];
                if (pxHave < sz)
                    topleftOut.Slice(leftStart, sz - pxHave).Fill(topleftOut[leftStart + sz - pxHave]);
            }
            else
            {
                byte fillVal = haveTop ? dstTop[dstTopOffset] : (byte)(((1 << bitdepth) >> 1) + 1);
                topleftOut.Slice(leftStart, sz).Fill(fillVal);
            }

            // Bottom-left extension
            if ((needs & NeedsBottomleft) != 0)
            {
                bool haveBottomLeft = (haveLeft && y + th < h) &&
                    (edgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0;

                if (haveBottomLeft)
                {
                    int pxHave = Math.Min(sz, (h - y - th) * 4);
                    for (int i = 0; i < pxHave; i++)
                        topleftOut[leftStart - (i + 1)] = dst[dstOff + (sz + i) * dstStride - 1];
                    if (pxHave < sz)
                        topleftOut.Slice(leftStart - sz, sz - pxHave).Fill(topleftOut[leftStart - pxHave]);
                }
                else
                {
                    topleftOut.Slice(leftStart - sz, sz).Fill(topleftOut[leftStart]);
                }
            }
        }

        // Top edge pixels
        if ((needs & NeedsTop) != 0)
        {
            int sz = tw * 4;
            int topStart = topleftCenter + 1; // top[0..sz-1] at topleftOut[topleftCenter + 1 ..]

            if (haveTop)
            {
                int pxHave = Math.Min(sz, (w - x) * 4);
                dstTop.Slice(dstTopOffset, pxHave).CopyTo(topleftOut.Slice(topStart));
                if (pxHave < sz)
                    topleftOut.Slice(topStart + pxHave, sz - pxHave).Fill(topleftOut[topStart + pxHave - 1]);
            }
            else
            {
                byte fillVal = haveLeft ? dst[dstOff - 1] : (byte)(((1 << bitdepth) >> 1) - 1);
                topleftOut.Slice(topStart, sz).Fill(fillVal);
            }

            // Top-right extension
            if ((needs & NeedsTopright) != 0)
            {
                bool haveTopRight = (haveTop && x + tw < w) &&
                    (edgeFlags & Av1EdgeFlags.I444TopHasRight) != 0;

                if (haveTopRight)
                {
                    int pxHave = Math.Min(sz, (w - x - tw) * 4);
                    dstTop.Slice(dstTopOffset + sz, pxHave).CopyTo(topleftOut.Slice(topStart + sz));
                    if (pxHave < sz)
                        topleftOut.Slice(topStart + sz + pxHave, sz - pxHave).Fill(
                            topleftOut[topStart + sz + pxHave - 1]);
                }
                else
                {
                    topleftOut.Slice(topStart + sz, sz).Fill(topleftOut[topStart + sz - 1]);
                }
            }
        }

        // Top-left corner pixel
        if ((needs & NeedsTopleft) != 0)
        {
            if (haveLeft)
                topleftOut[topleftCenter] = haveTop ? dstTop[dstTopOffset - 1] : dst[dstOff - 1];
            else
                topleftOut[topleftCenter] = haveTop ? dstTop[dstTopOffset] : (byte)((1 << bitdepth) >> 1);

            // Z2 corner filtering
            if (implMode == ImplZ2 && tw + th >= 6 && filterEdge)
            {
                topleftOut[topleftCenter] = (byte)(
                    ((topleftOut[topleftCenter - 1] + topleftOut[topleftCenter + 1]) * 5 +
                     topleftOut[topleftCenter] * 6 + 8) >> 4);
            }
        }

        return implMode;
    }

    // ========================================================================
    // Smooth flag helpers
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SmFlag(Av1BlockContextManaged edge, int off)
    {
        if (edge.Intra[off] == 0) return 0;
        int mode = edge.Mode[off];
        return (mode == (int)Av1IntraPredMode.Smooth ||
                mode == (int)Av1IntraPredMode.SmoothH ||
                mode == (int)Av1IntraPredMode.SmoothV) ? 512 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SmUvFlag(Av1BlockContextManaged edge, int off)
    {
        int mode = edge.UvMode[off];
        // dav1d: ANGLE_SMOOTH_EDGE_FLAG = 512 (bit 9), NOT bit 0
        return (mode == (int)Av1IntraPredMode.Smooth ||
                mode == (int)Av1IntraPredMode.SmoothH ||
                mode == (int)Av1IntraPredMode.SmoothV) ? 512 : 0;
    }

    // ========================================================================
    // Intra block reconstruction
    // ========================================================================

    /// <summary>
    /// Reconstructs an intra-predicted block: prediction + coefficient decode + inverse transform.
    /// Port of dav1d recon_b_intra (recon_tmpl.c lines 1176-1555).
    /// </summary>
    public static int DbgCoefCalls, DbgCoefNonSkip, DbgCoefEobPos;
    public static long DbgTotalCoefSum;
    public static int DbgFirstDqDc, DbgFirstDqAc;
    public static int DbgChromaCoefCalls, DbgChromaEobPos;
    public static bool DbgHasChroma;
    public static int DbgSubCh4, DbgSubCw4;
    public static int[] DbgModeHist = new int[14];
     public static int DbgBlockCount;
    public static bool DbgP14Fixed;
    public static bool DumpBlock0Coeffs;
    public static bool DumpAllCf0;
    public static StreamWriter? Cf0DumpWriter;
    public static bool DumpPixelPred;
    public static StreamWriter? PixelDumpWriter;
    private static byte _lastPixel14;

    public static void ReconBlockIntra(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx,
        Av1BlockSize bs, Av1EdgeFlags intraEdgeFlags, ref Av1Block b,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, Span<byte> vPlane, int uvStride)
    {
        if (b.PalSzY > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"PAL bxy=[{t.Bx},{t.By}] palSz={b.PalSzY}");
            sb.Append(" colors:");
            for (int ci = 0; ci < b.PalSzY; ci++)
                sb.Append($" {t.PalColorsY[ci]}");
            int _bw4 = Av1Tables.BlockDimensions[(int)bs, 0];
            int _bh4 = Av1Tables.BlockDimensions[(int)bs, 1];
            sb.Append(" idx0:");
            for (int di = 0; di < Math.Min(_bw4 * 4, 16); di++)
                sb.Append($" {t.PalIdxY[di]}");
            // Also log the first decoded pixel: we don't have it yet (palette fill happens later)
            // Log after reconstruction at the end of the function
            System.IO.File.AppendAllText(@"C:\Users\adamm\AppData\Local\Temp\ours_pal.txt", sb.ToString() + "\n");
        }
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;
        var seqHdr = ctx.SequenceHeader!;

        int bx4 = t.Bx & 31, by4 = t.By & 31;
        int ssVer = fh.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = fh.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int cbx4 = bx4 >> ssHor, cby4 = by4 >> ssVer;

        int bw4 = Av1Tables.BlockDimensions[(int)bs, 0];
        int bh4 = Av1Tables.BlockDimensions[(int)bs, 1];

        // Log per-block Y pixel sum BEFORE reconstruction
        ulong preYSum = 0;
        for (int dy = 0; dy < bh4 * 4; dy++)
            for (int dx = 0; dx < bw4 * 4; dx++)
                preYSum += yPlane[(by4 * 4 + dy) * yStride + (bx4 * 4 + dx)];

        // Debug: dump pixels for block (0,2) after recon
        bool dumpTarget = (bx4 == 0 && by4 == 2);
        int w4 = Math.Min(bw4, ctx.Bw - t.Bx);
        int h4 = Math.Min(bh4, ctx.Bh - t.By);
        int cw4 = (w4 + ssHor) >> ssHor;
        int ch4 = (h4 + ssVer) >> ssVer;

        bool hasChroma = fh.PixelLayout != Av1PixelLayout.I400 &&
                         (bw4 > ssHor || (t.Bx & 1) != 0) &&
                         (bh4 > ssVer || (t.By & 1) != 0);

        ref readonly var tDim = ref Av1Tables.TxfmDimensions[b.Tx];
        ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[b.UvTx];
        int cbw4 = (bw4 + ssHor) >> ssHor;
        int cbh4 = (bh4 + ssVer) >> ssVer;

        int intraEdgeFilterFlag = (seqHdr.IntraEdgeFilter ? 1 : 0) << 10;

        {
            Av1CoeffDecode.DbgReconCount++;
            if (Av1CoeffDecode.DbgReconCount <= 200)
                AvDbg.W($"[RECON-DBG] #{Av1CoeffDecode.DbgReconCount} bs={(int)bs} tx={b.Tx} uvtx={b.UvTx} y_mode={(int)b.YMode} uv_mode={(int)b.UvMode} bx={t.Bx} by={t.By} hasC={hasChroma} rng={msac.DebugRng} skip={b.Skip} palSzY={b.PalSzY}");
        }

        // Edge buffer: center at index 128, total size 257 (128 left + 1 topleft + 128 right)
        Span<byte> edgeBuf = stackalloc byte[257];
        const int edgeCenter = 128;

        if (Av1CoeffDecode.DbgReconCount <= 3)
            AvDbg.W($"[RECON-LOOP] w4={w4} h4={h4} tDim.W={tDim.W} tDim.H={tDim.H}");

        for (int initY = 0; initY < h4; initY += 16)
        {
            int subH4 = Math.Min(h4, 16 + initY);
            int subCh4 = Math.Min(ch4, (initY + 16) >> ssVer);

            for (int initX = 0; initX < w4; initX += 16)
            {
                int intraFlags = SmFlag(t.Above, bx4) | SmFlag(t.Left, by4) | intraEdgeFilterFlag;

                int sbHasTr = (initX + 16 < w4) ? 1 :
                    (initY != 0) ? 0 :
                    ((intraEdgeFlags & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0);
                int sbHasBl = (initX != 0) ? 0 :
                    (initY + 16 < h4) ? 1 :
                    ((intraEdgeFlags & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0);

                int subW4 = Math.Min(w4, initX + 16);

                // --- Luma reconstruction ---
                for (int yy = initY; yy < subH4; yy += tDim.H)
                {
                    int dstBaseOff = 4 * ((t.By + yy) * yStride + t.Bx);

                    for (int xx = initX; xx < subW4; xx += tDim.W)
                    {
                        int curBx = t.Bx + xx;
                        int curBy = t.By + yy;
                        int dstOff = dstBaseOff + xx * 4;

                        if (DbgBlockCount < 5)
                            AvDbg.W($"[RECON-INNER] Block#{DbgBlockCount} xx={xx} yy={yy} curBx={curBx} curBy={curBy}");

                        DbgBlockCount++;
                        // Watch pixel(14,0) — report who wrote what
                        {
                            byte current14 = yPlane[14];
                            if (DbgBlockCount == 1 || current14 != _lastPixel14)
                            {
                                AvDbg.W($"[P14-WATCH] Block#{DbgBlockCount} bx={curBx} by={curBy} xx={xx} yy={yy} palSzY={b.PalSzY} yPlane[14]=0x{current14:x2}({current14}) old=0x{_lastPixel14:x2}");
                                _lastPixel14 = current14;
                            }
                        }
                        if (b.YMode < DbgModeHist.Length) DbgModeHist[b.YMode]++;

                        // Debug: track writes to pixel (130,1)
                        int blkX0w = curBx * 4;
                        int blkY0w = curBy * 4;
                        bool dbgFirstErr = (130 >= blkX0w && 130 < blkX0w + tDim.W * 4 && 1 >= blkY0w && 1 < blkY0w + tDim.H * 4);

                        // Watchpoint: detect any write to pixel (32,104) = offset 12392
                        byte watchBefore = 0;
                        if (12392 < yPlane.Length)
                            watchBefore = yPlane[12392];

                        if (b.PalSzY == 0) // skip prediction for palette blocks
                        {
                            int localAngle = b.YAngle;
                            var localEdgeFlags =
                                (((yy > initY || sbHasTr == 0) && (xx + tDim.W >= subW4)) ?
                                     0 : Av1EdgeFlags.I444TopHasRight) |
                                ((xx > initX || (sbHasBl == 0 && yy + tDim.H >= subH4)) ?
                                     0 : Av1EdgeFlags.I444LeftHasBottom);

                            ReadOnlySpan<byte> topSbEdge = default;
                            if ((curBy & (ctx.SbStep - 1)) == 0 && curBy > 0 && ctx.IpredEdgeY.Length > 0)
                            {
                                int sby = curBy >> ctx.SbShift;
                                topSbEdge = ctx.IpredEdgeY.AsSpan(ctx.Sb128w * 128 * (sby - 1));
                            }

                            bool haveLeft = curBx > ts.ColStart;
                            bool haveTopN = curBy > ts.RowStart;

                            var yMode = (Av1IntraPredMode)b.YMode;
                            var m = PrepareIntraEdges(
                                curBx, haveLeft,
                                curBy, haveTopN,
                                ts.ColEnd, ts.RowEnd,
                                localEdgeFlags,
                                yPlane, dstOff, yStride,
                                topSbEdge, yMode, ref localAngle,
                                tDim.W, tDim.H,
                                seqHdr.IntraEdgeFilter,
                                edgeBuf, edgeCenter, 8);

                            // Set debug flag for Z2 dump
                            if (dbgFirstErr)
                            {
                                Av1IntraPred.DbgZ2 = true;
                                AvDbg.W($"[PRE-PRED] Block#{DbgBlockCount} bx={curBx} by={curBy} dstOff={dstOff} pixel-before: {yPlane[dstOff]:x2} {yPlane[dstOff+1]:x2} {yPlane[dstOff+2]:x2} {yPlane[dstOff+3]:x2} {yPlane[dstOff+4]:x2} {yPlane[dstOff+5]:x2} {yPlane[dstOff+6]:x2} {yPlane[dstOff+7]:x2}");
                            }

                            // Call intra prediction
                            if (b.PalSzY == 0 && DbgBlockCount >= 5 && DbgBlockCount <= 8)
                            {
                                int _m = (int)b.YMode;
                                AvDbg.W($"[SMOOTH-V-PRE] DbgBlock={DbgBlockCount} bx={curBx} by={curBy} yMode={_m} dstOff={dstOff}");
                                AvDbg.W($"[SMOOTH-V-PRE] yPlane[15]={yPlane[15]:x2}({yPlane[15]}) yPlane[79]={yPlane[79]:x2} yPlane[143]={yPlane[143]:x2} yPlane[207]={yPlane[207]:x2}");
                                AvDbg.W($"[SMOOTH-V-PRE] yPlane[dstOff-1]={yPlane[dstOff-1]:x2} yPlane[dstOff+63]={yPlane[dstOff+63]:x2}");
                            }
                            if (curBx == 15 && curBy == 2 && ctx.FrameHeader?.FrameOffset == 1)
                            {
                                AvDbg.W($"[OUR-IPRED] bx=15 by=2 m={m} ymode={b.YMode} angle={localAngle} edgeflags={localEdgeFlags}");
                                AvDbg.W("  edge: ");
                                for (int z = 0; z < 16; z++) AvDbg.W($" {edgeBuf[z]}");
                                AvDbg.W();
                            }
                            Av1IntraPred.Predict(m,
                                yPlane.Slice(dstOff), yStride,
                                edgeBuf, edgeCenter,
                                tDim.W * 4, tDim.H * 4,
                                localAngle | intraFlags,
                                4 * ctx.Bw - 4 * curBx,
                                4 * ctx.Bh - 4 * curBy);
                            if (curBx == 15 && curBy == 2 && ctx.FrameHeader?.FrameOffset == 1)
                            {
                                for (int r = 0; r < 8; r++)
                                {
                                    var ln = "";
                                    for (int c = 0; c < 4; c++) ln += $" {yPlane[dstOff + r * yStride + c],3}";
                                    AvDbg.W(ln);
                                }
                            }

                            if (DumpPixelPred)
                            {
                                PixelDumpWriter?.WriteLine($"bxy=[{curBx},{curBy}] skip={b.Skip} m={m} pred0={yPlane[dstOff]}");
                                if (curBx == 4 && curBy == 4)
                                {
                                    int twPx = tDim.W * 4;
                                    int thPx = tDim.H * 4;
                                    var sb2 = new System.Text.StringBuilder();
                                    sb2.Append("  EDGE left (bottom->top):");
                                    for (int i = 0; i < thPx; i++)
                                        sb2.Append($" {edgeBuf[edgeCenter - thPx + i]}");
                                    PixelDumpWriter?.WriteLine(sb2.ToString());
                                    PixelDumpWriter?.WriteLine($"  EDGE topleft={edgeBuf[edgeCenter]}");
                                    sb2.Clear();
                                    sb2.Append("  EDGE top (left->right):");
                                    for (int i = 0; i < twPx; i++)
                                        sb2.Append($" {edgeBuf[edgeCenter + 1 + i]}");
                                    PixelDumpWriter?.WriteLine(sb2.ToString());
                                    sb2.Clear();
                                    sb2.Append("  EDGE topright:");
                                    for (int i = 0; i < twPx; i++)
                                        sb2.Append($" {edgeBuf[edgeCenter + 1 + twPx + i]}");
                                    PixelDumpWriter?.WriteLine(sb2.ToString());
                                    PixelDumpWriter?.WriteLine($"  m_raw={b.YMode} m_resolved={m}");
                                }
                            }

                            if (b.PalSzY == 0 && b.YMode == (byte)Av1IntraPredMode.SmoothV && DbgBlockCount < 10)
                            {
                                AvDbg.W($"[SMOOTH-V-POST] Block#{DbgBlockCount}: {yPlane[dstOff]:x2} {yPlane[dstOff+1]:x2} {yPlane[dstOff+2]:x2} {yPlane[dstOff+3]:x2}");
                                // Print edgeBuf values at center region
                                AvDbg.W($"[SMOOTH-V-POST] edgeBuf[center-4..center+4]: {edgeBuf[edgeCenter-4]:x2} {edgeBuf[edgeCenter-3]:x2} {edgeBuf[edgeCenter-2]:x2} {edgeBuf[edgeCenter-1]:x2} [{edgeBuf[edgeCenter]:x2}] {edgeBuf[edgeCenter+1]:x2} {edgeBuf[edgeCenter+2]:x2} {edgeBuf[edgeCenter+3]:x2} {edgeBuf[edgeCenter+4]:x2}");
                            }

                            if (dbgFirstErr)
                            {
                                Av1IntraPred.DbgZ2 = false;
                                AvDbg.W($"[POST-PRED] Block#{DbgBlockCount} bx={curBx} by={curBy} pixel-after: {yPlane[dstOff]:x2} {yPlane[dstOff+1]:x2} {yPlane[dstOff+2]:x2} {yPlane[dstOff+3]:x2} {yPlane[dstOff+4]:x2} {yPlane[dstOff+5]:x2} {yPlane[dstOff+6]:x2} {yPlane[dstOff+7]:x2}");
                            }

                            // Debug: dump prediction + edges for target blocks
                            if (dbgFirstErr)
                            {
                                int pw = tDim.W * 4, ph = tDim.H * 4;
                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine($"[DBG-PRED] Block#{DbgBlockCount} mode={yMode} implMode={m} angle={localAngle} bx={curBx} by={curBy} tx={b.Tx} tw={tDim.W} th={tDim.H}");
                                sb.Append("  pred:");
                                for (int dy = 0; dy < ph; dy++)
                                {
                                    sb.Append("\n    ");
                                    int rowOff = dstOff + dy * yStride;
                                    for (int dx = 0; dx < pw; dx++)
                                        sb.Append($" {yPlane[rowOff + dx]:x2}");
                                }
                                sb.Append("\n  left:");
                                for (int dy = 0; dy < Math.Min(ph * 2, 16); dy++)
                                    sb.Append($" {edgeBuf[edgeCenter - 1 - dy]:x2}");
                                sb.Append("\n  top:");
                                for (int dx = 0; dx < Math.Min(pw * 2, 16); dx++)
                                    sb.Append($" {edgeBuf[edgeCenter + 1 + dx]:x2}");
                                sb.Append($"\n  tl: {edgeBuf[edgeCenter]:x2}");
                                sb.Append($"\n  intraFlags=0x{(localAngle | intraFlags):x} edgeFlags={localEdgeFlags}");
                                sb.Append($"\n  aboveMode={t.Above.Mode[bx4]} aboveIntra={t.Above.Intra[bx4]} leftMode={t.Left.Mode[by4]} leftIntra={t.Left.Intra[by4]}");
                                AvDbg.W(sb);
                                AvDbg.W();
                            }
                        }
                        else
                        {
                            // Palette block: use decoded palette colors and indices
                            int pw = tDim.W * 4, ph = tDim.H * 4;
                            int palStride = bw4 * 4;
                            
                            for (int dy = 0; dy < ph; dy++)
                            {
                                int rowOff = dstOff + dy * yStride;
                                int idxRowOff = (yy * 4 + dy) * palStride + xx * 4;
                                for (int dx = 0; dx < pw; dx++)
                                {
                                    int idx = t.PalIdxY[idxRowOff + dx];
                                    if (idx >= b.PalSzY) idx = 0;
                                    if (b.PalSzY > 0 && DbgBlockCount <= 2 && dx == 0 && dy == 0)
                                        AvDbg.W($"[FILL-X0] DbgBlock={DbgBlockCount} pw={pw} palSzY={b.PalSzY} idx={idx} color={t.PalColorsY[idx]:x2} dst={rowOff}+{dx}={yPlane[rowOff+dx]:x2}");
                                    // Diagnostic: check pixel (14,0) fill
                                    if (bx4 == 0 && by4 == 0 && rowOff + dx == 14 && dy == 0)
                                        AvDbg.W($"[PIXEL-14] palSzY={b.PalSzY} idxRowOff={idxRowOff} dx={dx} idx={idx} color={t.PalColorsY[idx]:x2} => yPlane[{rowOff+dx}]={t.PalColorsY[idx]:x2}");
                                    yPlane[rowOff + dx] = t.PalColorsY[idx];
                                }
                            }
                            // DIAGNOSTIC: track yPlane[14]
                            if (bx4 == 0 && by4 == 0 && xx == 2)
                                AvDbg.W($"[WATCH-14-AFTERFILL] yPlane[14]=0x{yPlane[14]:x2} bw4={bw4}");

                            // Dump palette fill for block at bx4=4
                            if (DumpBlock0Coeffs && bx4 == 4 && by4 == 0 && xx == 0 && curBy == 0)
                            {
                                AvDbg.W($"[PFILL-DUMP] block({bx4},{by4}) palSz={b.PalSzY} palStride={palStride} xx={xx} dstOff={dstOff}");
                                AvDbg.W($"  PalColors: {string.Join(" ", t.PalColorsY.Take(b.PalSzY).Select(c => c.ToString("x2")))}");
                                for (int dy = 0; dy < ph; dy++)
                                {
                                    AvDbg.W($"  read-idx row{dy}:");
                                    int idxRowOff = (yy * 4 + dy) * palStride + xx * 4;
                                    for (int dx = 0; dx < pw; dx++)
                                        AvDbg.W($" {t.PalIdxY[idxRowOff + dx]}");
                                    AvDbg.W();
                                    AvDbg.W($"  yPlane row{dy}:");
                                    for (int dx = 0; dx < pw; dx++)
                                        AvDbg.W($" {yPlane[dstOff + dy * yStride + dx]:x2}");
                                    AvDbg.W();
                                }
                            }
                        }

                        if (DbgBlockCount < 5)
                            AvDbg.W($"[RECON-PAL] Block#{DbgBlockCount} palSzY={b.PalSzY} about to check skip");

                        // Coefficient decode + inverse transform
                        if (DbgBlockCount <= 5)
                            AvDbg.W($"[RECON-COEF] Block#{DbgBlockCount} skip={b.Skip} palSzY={b.PalSzY}");
                        if (b.Skip == 0)
                        {
                            DbgCoefCalls++;
                            Span<int> cf = t.CfBuf;
                            Span<byte> levels = t.Levels;
                            bool lossless = fh.SegmentationLossless[b.SegId];

                            Span<ushort> dqTable = t.DqBuf;
                            dqTable[0] = ts.Dq[b.SegId, 0, 0]; // dc
                            dqTable[1] = ts.Dq[b.SegId, 0, 1]; // ac

                            if (curBx == 0 && curBy == 0 && t.Bx == 0 && t.By == 0)
                                AvDbg.W($"[INTRA-DQ] dqDc={dqTable[0]} dqAc={dqTable[1]} segId={b.SegId} qIdx={fh.QuantBaseQIdx}");

                            if (DumpBlock0Coeffs)
                                AvDbg.W($"[CF-PRE] curBx={curBx} curBy={curBy} cf[0]={cf[0]} (before DecodeCoefs)");

                            int layout = (int)fh.PixelLayout;
                            var txtp = Av1TxType.DctDct;
                            byte cfCtx;

                            int eob = Av1CoeffDecode.DecodeCoefs(
                                ref msac, ts.Cdf.Coef, ts.Cdf.Mode,
                                new ReadOnlySpan<byte>(t.Above.LCoef, bx4 + xx, Math.Max(1, Math.Min(tDim.W, 32 - (bx4 + xx)))),
                                new ReadOnlySpan<byte>(t.Left.LCoef, by4 + yy, Math.Max(1, Math.Min(tDim.H, 32 - (by4 + yy)))),
                                b.Tx, (int)bs, b.SegId,
                                b.YMode, b.UvMode, b.YAngle,
                                1, 0, cf, ref txtp, out cfCtx,
                                dqTable, ReadOnlySpan<byte>.Empty,
                                lossless, fh.ReducedTxSet,
                                fh.SegmentationQIdx[b.SegId], ctx.BitDepth, levels, layout);

                            DbgCoefNonSkip++;
                            bool dbgTarget = dbgFirstErr;
                            if (dbgTarget)
                            {
                                AvDbg.W($"[DBG] coef bx={curBx} by={curBy} eob={eob} skip={b.Skip} tx={b.Tx} txtp={txtp} mode={(Av1IntraPredMode)b.YMode} cf[0]={cf[0]}");
                            }
                            if (eob >= 0)
                            {
                                if (DumpBlock0Coeffs)
                                    AvDbg.W($"[CF-TRACE] curBx={curBx} curBy={curBy} cf[0]={cf[0]} eob={eob}");
                                if (DumpAllCf0)
                                {
                                    var sbc = new System.Text.StringBuilder();
                                    sbc.Append($"bxy=[{curBx},{curBy}] tx={b.Tx} chroma=0 eob={eob}");
                                    for (int ci = 0; ci <= Math.Min(eob, 63); ci++)
                                        sbc.Append($" cf[{ci}]={cf[ci]}");
                                    Cf0DumpWriter?.WriteLine(sbc.ToString());
                                }
                                DbgCoefEobPos++;
                                if (DbgFirstDqDc == 0)
                                {
                                    DbgFirstDqDc = dqTable[0];
                                    DbgFirstDqAc = dqTable[1];
                                }
                                if (DbgBlockCount <= 1)
                                {
                                    DbgFirstDqDc = dqTable[0];
                                    DbgFirstDqAc = dqTable[1];
                                }
                                for (int ci = 0; ci <= eob; ci++)
                                    DbgTotalCoefSum += Math.Abs(cf[ci]);
                            }

                            int ctxW = Math.Max(0, Math.Min(tDim.W, ctx.Bw - curBx));
                            int ctxH = Math.Max(0, Math.Min(tDim.H, ctx.Bh - curBy));
                            if (ctxW > 0) Av1BlockContextManaged.Fill(t.Above.LCoef, bx4 + xx, ctxW, cfCtx);
                            if (ctxH > 0) Av1BlockContextManaged.Fill(t.Left.LCoef, by4 + yy, ctxH, cfCtx);
                            if (DumpBlock0Coeffs)
                                AvDbg.W($"[LCtx] curBx={curBx} curBy={curBy} cfCtx=0x{cfCtx:x2}({cfCtx}) eob={eob}");
                            if (DbgBlockCount == 5 && eob >= 0)
                            {
                                var sb = new System.Text.StringBuilder($"[COEF-DUMP] Block#5 eob={eob} tx={b.Tx} txtp={txtp} pred0=0x{yPlane[dstOff]:x2}({yPlane[dstOff]}) coeffs:");
                                for (int ci = 0; ci <= Math.Min(eob, 10); ci++)
                                    sb.Append($" {cf[ci]}");
                                AvDbg.W(sb.ToString());
                            }

                             if (eob >= 0)
                             {
                                if (DumpBlock0Coeffs)
                                {
                                    AvDbg.W($"[BLK0-EOB] curBx={curBx} curBy={curBy} eob={eob} tx={b.Tx} txtp={txtp} cf[0]={cf[0]}");
                                    AvDbg.W("[BLK0-CF]");
                                    for (int ci = 0; ci < 16; ci++)
                                        AvDbg.W($" {cf[ci]}");
                                    AvDbg.W();
                                }
                             // Debug: dump dq coefs for target blocks
                            if (dbgTarget)
                            {
                                int pw = tDim.W * 4, ph = tDim.H * 4;
                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine($"[DBG-COEF] #{DbgCoefCalls}: eob={eob} tx={b.Tx} txtp={txtp} bx={curBx} by={curBy} mode={b.YMode}");
                                sb.Append("  dq:");
                                for (int dy = 0; dy < ph; dy++)
                                {
                                    sb.Append("\n   ");
                                    for (int dx = 0; dx < pw; dx++)
                                        sb.Append($" {cf[dy * pw + dx],5}");
                                }
                                AvDbg.W(sb);
                                AvDbg.W();
                            }

                                if (lossless)
                                {
                                    Av1InvTransform.InvWhtAdd(
                                        yPlane.Slice(dstOff), yStride, cf, ctx.BitDepth);
                                }
                                else
                                {
                                Av1InvTransform.DbgTrace = (curBx == 2 && curBy == 0) || (eob >= 0 && DbgBlockCount <= 5);
                                int shift = Av1InvTransform.TxShift[b.Tx];
                                    int pred0Val = DumpPixelPred ? yPlane[dstOff] : 0;
                                    Av1InvTransform.InvTxfmAdd(
                                        yPlane.Slice(dstOff), yStride, cf, eob,
                                        b.Tx, shift, txtp, ctx.BitDepth);
                                    if (DumpPixelPred)
                                    {
                                        PixelDumpWriter?.WriteLine($"bxy=[{curBx},{curBy}] tx={b.Tx} txtp={txtp:F} eob={eob} pred0={pred0Val} recon0={yPlane[dstOff]} cf0={cf[0]}");
                                        if (curBx == 0 && curBy == 4)
                                        {
                                            int pw = tDim.W * 4, ph = tDim.H * 4;
                                            PixelDumpWriter?.WriteLine("  Ours ALL pixels:");
                                            for (int dy2 = 0; dy2 < ph; dy2++)
                                            {
                                                var sb = new System.Text.StringBuilder("   ");
                                                for (int dx2 = 0; dx2 < pw; dx2++)
                                                    sb.Append($" {yPlane[dstOff + dy2 * yStride + dx2],4}");
                                                PixelDumpWriter?.WriteLine(sb.ToString());
                                            }
                                        }
                                        if (b.Tx == 0 && curBx == 10 && curBy == 10)
                                        {
                                            int pw = tDim.W * 4;
                                            var sb = new System.Text.StringBuilder("  OURS recon:");
                                            for (int dy = 0; dy < pw; dy++)
                                            {
                                                if (dy > 0) sb.Append(" |");
                                                for (int dx = 0; dx < pw; dx++)
                                                    sb.Append($" {yPlane[dstOff + dy * yStride + dx],4}");
                                            }
                                            PixelDumpWriter?.WriteLine(sb.ToString());
                                            sb.Clear();
                                            sb.Append("  OURS cf:");
                                            int cfSz = Math.Min((int)tDim.W, 8) * Math.Min((int)tDim.H, 8);
                                            for (int ci = 0; ci < cfSz; ci++)
                                            {
                                                if (ci % 4 == 0) sb.Append("\n    ");
                                                sb.Append($" {cf[ci],6}");
                                            }
                                            PixelDumpWriter?.WriteLine(sb.ToString());
                                        }
                                    }
                                    Av1InvTransform.DbgTrace = false;

                                    // Watch pixel (130,1) after write
                                    int blkX0 = curBx * 4;  // pixel column
                                    int blkY0 = curBy * 4;  // pixel row
                                    int blkW = tDim.W * 4;
                                    int blkH = tDim.H * 4;
                                    if (130 >= blkX0 && 130 < blkX0 + blkW && 1 >= blkY0 && 1 < blkY0 + blkH)
                                    {
                                        int wOff = 130 + 1 * yStride;
                                        if (wOff < yPlane.Length)
                                            AvDbg.W($"[WATCH-130-1-AFTER] Block#{DbgBlockCount} bx={curBx} by={curBy} tx={b.Tx} pixel={yPlane[wOff]:x2}");
                                    }
                                }

                                if (dbgTarget)
                                {
                                    int pw2 = tDim.W * 4, ph2 = tDim.H * 4;
                                    var sb2 = new System.Text.StringBuilder();
                                    sb2.Append("  recon:");
                                    for (int dy = 0; dy < ph2; dy++)
                                    {
                                        sb2.Append("\n   ");
                                        int rowOff2 = dstOff + dy * yStride;
                                        for (int dx = 0; dx < pw2; dx++)
                                            sb2.Append($" {yPlane[rowOff2 + dx]:x2}");
                                    }
                                    AvDbg.W(sb2);
                                    AvDbg.W();
                                }
                            }
                        }
                        else
                        {
                            // Skip: set coef contexts to 0x40 (no coefs)
                            Av1BlockContextManaged.Fill(t.Above.LCoef, bx4 + xx,
                                Math.Max(0, Math.Min(1 << tDim.Lw, 32 - (bx4 + xx))), 0x40);
                            Av1BlockContextManaged.Fill(t.Left.LCoef, by4 + yy,
                                Math.Max(0, Math.Min(1 << tDim.Lh, 32 - (by4 + yy))), 0x40);
                        }
                        dstOff += 4 * tDim.W;

                        // Watchpoint: detect modification of pixel (32,104)
                        byte watchAfter = 0;
                        if (12392 < yPlane.Length)
                            watchAfter = yPlane[12392];
                        if (watchAfter != watchBefore && DbgBlockCount > 1)
                            AvDbg.W($"[WATCH] pixel (32,104) changed {watchBefore:x2}->{watchAfter:x2} by Block#{DbgBlockCount} bx={curBx} by={curBy} mode={(Av1IntraPredMode)b.YMode} tw={tDim.W} th={tDim.H} dstOff={dstOff - 4*tDim.W}");

                        // Y-ERR debug dump removed for clean output
                    }
                }

                if (!hasChroma) continue;
                DbgHasChroma = true;
                DbgSubCh4 = subCh4;

                // --- Chroma reconstruction ---
                int smUvFl = SmUvFlag(t.Above, cbx4) | SmUvFlag(t.Left, cby4);

                int uvSbHasTr = ((initX + 16) >> ssHor) < cw4 ? 1 :
                    (initY != 0) ? 0 :
                    ((intraEdgeFlags & (Av1EdgeFlags)(
                        (int)Av1EdgeFlags.I420TopHasRight >> (int)(fh.PixelLayout - 1))) != 0 ? 1 : 0);
                int uvSbHasBl = (initX != 0) ? 0 :
                    ((initY + 16) >> ssVer) < ch4 ? 1 :
                    ((intraEdgeFlags & (Av1EdgeFlags)(
                        (int)Av1EdgeFlags.I420LeftHasBottom >> (int)(fh.PixelLayout - 1))) != 0 ? 1 : 0);

                int subCw4 = Math.Min(cw4, (initX + 16) >> ssHor);

                // CFL prediction (Chroma from Luma)
                if (b.UvMode == (byte)Av1IntraPredMode.ChromaFromLuma)
                {
                    // Compute AC component from luma
                    short[] ac = t.AcBuf;
                    int yOff = 4 * ((t.Bx & ~ssHor) + (t.By & ~ssVer) * yStride);
                    int uvOff = 4 * ((t.Bx >> ssHor) + (t.By >> ssVer) * uvStride);

                    int furthestR = ((cw4 << ssHor) + tDim.W - 1) & ~(tDim.W - 1);
                    int furthestB = ((ch4 << ssVer) + tDim.H - 1) & ~(tDim.H - 1);
                    int wPad = cbw4 - (furthestR >> ssHor);
                    int hPad = cbh4 - (furthestB >> ssVer);

                    ComputeCflAc(ac, yPlane.Slice(yOff), yStride,
                        cbw4 * 4, cbh4 * 4, ssHor, ssVer, wPad, hPad);

                    // DBG: dump first CFL AC values
                    if (DbgBlockCount <= 12)
                    {
                        var sb = new System.Text.StringBuilder($"[CFL-DBG] Block#{DbgBlockCount} ac:");
                        for (int di = 0; di < Math.Min(16, ac.Length); di++)
                            sb.Append($" {ac[di],4}");
                        AvDbg.W(sb);
                    }

                    for (int pl = 0; pl < 2; pl++)
                    {
                        if (b.GetCflAlpha(pl) == 0) continue;

                        int localAngle = 0;
                        ReadOnlySpan<byte> topSbEdge = default;
                        if (((t.By & ~ssVer) & (ctx.SbStep - 1)) == 0 && ctx.IpredEdgeU.Length > 0)
                        {
                            int sby = t.By >> ctx.SbShift;
                            var edgeArr = pl == 0 ? ctx.IpredEdgeU : ctx.IpredEdgeV;
                            topSbEdge = edgeArr.AsSpan(ctx.Sb128w * 128 * (sby - 1));
                        }

                        int xpos = t.Bx >> ssHor, ypos = t.By >> ssVer;
                        int xstart = ts.ColStart >> ssHor;
                        int ystart = ts.RowStart >> ssVer;

                        var uvPlaneSrc = pl == 0 ? uPlane : vPlane;

                        var m = PrepareIntraEdges(
                            xpos, xpos > xstart,
                            ypos, ypos > ystart,
                            ts.ColEnd >> ssHor, ts.RowEnd >> ssVer,
                            0, uvPlaneSrc, uvOff, uvStride, topSbEdge,
                            Av1IntraPredMode.Dc, ref localAngle,
                            uvtDim.W, uvtDim.H, false,
                            edgeBuf, edgeCenter, 8);

                        // CFL prediction: DC prediction + AC scaled component
                        Av1IntraPred.Predict(m,
                            uvPlaneSrc.Slice(uvOff), uvStride,
                            edgeBuf, edgeCenter,
                            uvtDim.W * 4, uvtDim.H * 4,
                            0, // no angle
                            (4 * ctx.Bw + ssHor - 4 * (t.Bx & ~ssHor)) >> ssHor,
                            (4 * ctx.Bh + ssVer - 4 * (t.By & ~ssVer)) >> ssVer);

                        // DBG: dump specific chroma region for block comparison
                        if (t.Bx == 8 && t.By == 0 && pl == 0)
                        {
                            AvDbg.W($"[CFL-BLK31] bx={t.Bx} by={t.By} xpos={xpos} ypos={ypos} hasLeft={xpos > xstart} hasTop={ypos > ystart}");
                            AvDbg.W($"[CFL-BLK31] uvtDim.W={uvtDim.W} uvtDim.H={uvtDim.H} cbw4={cbw4} cbh4={cbh4}");
                            AvDbg.W($"[CFL-BLK31] uvOff={uvOff} uvStride={uvStride} mode={m}");
                            // Dump left neighbors
                            var sb2 = new System.Text.StringBuilder("[CFL-BLK31] left neighbors:");
                            for (int r = 0; r < uvtDim.H * 4; r++)
                                sb2.Append($" {uvPlaneSrc[uvOff - 1 + r * uvStride]:x2}");
                            AvDbg.W(sb2);
                            // Dump the region at chroma cols 12-15 (previous block output)
                            sb2 = new System.Text.StringBuilder("[CFL-BLK31] u-plane cols 12-15 rows 0-3:");
                            for (int r = 0; r < 4; r++)
                            {
                                sb2.Append(" |");
                                for (int c = 12; c < 16; c++)
                                    sb2.Append($" {uvPlaneSrc[r * uvStride + c]:x2}");
                            }
                            AvDbg.W(sb2);
                            // DC prediction
                            sb2 = new System.Text.StringBuilder("[CFL-BLK31] u-cfl-pred:");
                            for (int r = 0; r < uvtDim.H * 4; r++)
                            {
                                sb2.Append(" |");
                                for (int c = 0; c < uvtDim.W * 4; c++)
                                    sb2.Append($" {uvPlaneSrc[uvOff + r * uvStride + c]:x2}");
                            }
                            AvDbg.W(sb2);
                        }

                        // Apply CFL alpha scaling
                        int alpha = b.GetCflAlpha(pl);
                        ApplyCflAlpha(uvPlaneSrc.Slice(uvOff), uvStride, ac, alpha,
                            cbw4 * 4, cbh4 * 4);

                        // DBG: dump after CFL alpha
                        if (DbgBlockCount <= 12)
                        {
                            string pn = pl == 0 ? "u" : "v";
                            var sb = new System.Text.StringBuilder($"[CFL-DBG] Block#{DbgBlockCount} {pn}-after-alpha(a={alpha}):");
                            for (int r = 0; r < Math.Min(4, cbh4 * 4); r++)
                            {
                                sb.Append(" |");
                                for (int c = 0; c < Math.Min(4, cbw4 * 4); c++)
                                    sb.Append($" {uvPlaneSrc[uvOff + r * uvStride + c]:x2}");
                            }
                            AvDbg.W(sb);
                        }
                    }
                }

                // UV palette prediction: fill U and V planes from decoded palette colors & indices
                if (b.PalSzUv > 0)
                {
                    int cw = (bw4 * 4 + 1) >> 1;
                    int ch = (bh4 * 4 + 1) >> 1;
                    int cbw4_pix = cbw4 * 4;
                    int uvDstOff = 4 * ((t.Bx >> 1) + (t.By >> 1) * uvStride);

                    // Fill U plane
                    for (int dy = 0; dy < ch; dy++)
                    {
                        int rowOff = uvDstOff + dy * uvStride;
                        int idxRowOff = dy * cbw4_pix;
                        for (int dx = 0; dx < cw; dx++)
                        {
                            int idx = t.PalIdxUv[idxRowOff + dx];
                            if (idx >= b.PalSzUv) idx = 0;
                            uPlane[rowOff + dx] = t.PalColorsU[idx];
                        }
                    }

                    // Fill V plane
                    for (int dy = 0; dy < ch; dy++)
                    {
                        int rowOff = uvDstOff + dy * uvStride;
                        int idxRowOff = dy * cbw4_pix;
                        for (int dx = 0; dx < cw; dx++)
                        {
                            int idx = t.PalIdxUv[idxRowOff + dx];
                            if (idx >= b.PalSzUv) idx = 0;
                            vPlane[rowOff + dx] = t.PalColorsV[idx];
                        }
                    }
                }

                // Regular UV prediction + transform
                for (int pl = 0; pl < 2; pl++)
                {
                    for (int yy = initY >> ssVer; yy < subCh4; yy += uvtDim.H)
                    {
                        int curBy = t.By + (yy << ssVer);
                        int uvDstBaseOff = 4 * ((curBy >> ssVer) * uvStride +
                                            (t.Bx >> ssHor));
                        var uvPlane = pl == 0 ? uPlane : vPlane;

                        for (int xx = initX >> ssHor; xx < subCw4; xx += uvtDim.W)
                        {
                            int curBx = t.Bx + (xx << ssHor);
                            int uvDstOff = uvDstBaseOff + xx * 4;

                            // Skip if CFL or palette already predicted
                            if ((b.UvMode == (byte)Av1IntraPredMode.ChromaFromLuma && b.GetCflAlpha(pl) != 0) ||
                                b.PalSzUv > 0)
                            {
                                goto skipUvPred;
                            }

                            {
                                int localAngle = b.UvAngle;
                                var localEdgeFlags =
                                    (((yy > (initY >> ssVer) || uvSbHasTr == 0) &&
                                      (xx + uvtDim.W >= subCw4)) ?
                                         0 : Av1EdgeFlags.I444TopHasRight) |
                                    ((xx > (initX >> ssHor) ||
                                      (uvSbHasBl == 0 && yy + uvtDim.H >= subCh4)) ?
                                         0 : Av1EdgeFlags.I444LeftHasBottom);

                                ReadOnlySpan<byte> topSbEdge = default;
                                if (((curBy & ~ssVer) & (ctx.SbStep - 1)) == 0)
                                {
                                    var edgeArr = pl == 0 ? ctx.IpredEdgeU : ctx.IpredEdgeV;
                                    if (edgeArr != null && edgeArr.Length > 0)
                                    {
                                        int sby = curBy >> ctx.SbShift;
                                        if (sby > 0)
                                            topSbEdge = edgeArr.AsSpan(ctx.Sb128w * 128 * (sby - 1));
                                    }
                                }

                                var uvMode = b.UvMode == (byte)Av1IntraPredMode.ChromaFromLuma
                                    ? Av1IntraPredMode.Dc
                                    : (Av1IntraPredMode)b.UvMode;

                                int xpos = curBx >> ssHor, ypos = curBy >> ssVer;
                                int xstart = ts.ColStart >> ssHor;
                                int ystart = ts.RowStart >> ssVer;

                                var m = PrepareIntraEdges(
                                    xpos, xpos > xstart,
                                    ypos, ypos > ystart,
                                    ts.ColEnd >> ssHor, ts.RowEnd >> ssVer,
                                    localEdgeFlags,
                                    uvPlane, uvDstOff, uvStride,
                                    topSbEdge, uvMode, ref localAngle,
                                    uvtDim.W, uvtDim.H,
                                    seqHdr.IntraEdgeFilter,
                                    edgeBuf, edgeCenter, 8);

                                localAngle |= intraEdgeFilterFlag;

                                Av1IntraPred.Predict(m,
                                    uvPlane.Slice(uvDstOff), uvStride,
                                    edgeBuf, edgeCenter,
                                    uvtDim.W * 4, uvtDim.H * 4,
                                    localAngle | smUvFl,
                                    (4 * ctx.Bw + ssHor - 4 * (curBx & ~ssHor)) >> ssHor,
                                    (4 * ctx.Bh + ssVer - 4 * (curBy & ~ssVer)) >> ssVer);

                                // DBG: dump prediction before transform for bx=6 D157
                                if (t.Bx == 6 && t.By == 0 && pl == 0)
                                {
                                    var sb2 = new System.Text.StringBuilder($"[UV-BLK11-PRED] mode={m} angle={localAngle | smUvFl} pred:");
                                    for (int r = 0; r < 4; r++)
                                    {
                                        sb2.Append(" |");
                                        for (int c = 0; c < 4; c++)
                                            sb2.Append($" {uvPlane[uvDstOff + r * uvStride + c]:x2}");
                                    }
                                    AvDbg.W(sb2);
                                    // Dump edge buffer
                                    sb2 = new System.Text.StringBuilder("[UV-BLK11-PRED] edgeBuf left:");
                                    for (int rr = 0; rr < 8; rr++)
                                        sb2.Append($" {edgeBuf[edgeCenter - 1 - rr]:x2}");
                                    AvDbg.W(sb2);
                                    sb2 = new System.Text.StringBuilder("[UV-BLK11-PRED] edgeBuf tl+top:");
                                    sb2.Append($" tl={edgeBuf[edgeCenter]:x2}");
                                    for (int cc = 0; cc < 8; cc++)
                                        sb2.Append($" {edgeBuf[edgeCenter + 1 + cc]:x2}");
                                    AvDbg.W(sb2);
                                }
                            }

                        skipUvPred:
                            DbgChromaCoefCalls++;
                        if (b.Skip == 0)
                            {
                                var aboveCoef = pl == 0 ? t.Above.CCoef0 : t.Above.CCoef1;
                                var leftCoef = pl == 0 ? t.Left.CCoef0 : t.Left.CCoef1;

                                Span<int> cf = t.CfBuf;
                                Span<byte> levels = t.Levels;
                                bool lossless = fh.SegmentationLossless[b.SegId];

                                Span<ushort> dqTable = t.DqBuf;
                                dqTable[0] = ts.Dq[b.SegId, 1 + pl, 0]; // dc
                                dqTable[1] = ts.Dq[b.SegId, 1 + pl, 1]; // ac

                                var txtp = Av1TxType.DctDct;
                                byte cfCtx;
                                int layout = (int)fh.PixelLayout;

                                int aboveOff = cbx4 + xx;
                                int leftOff = cby4 + yy;
                                int aboveLen = Math.Max(1, Math.Min(uvtDim.W, 32 - aboveOff));
                                int leftLen = Math.Max(1, Math.Min(uvtDim.H, 32 - leftOff));

                                int eob = Av1CoeffDecode.DecodeCoefs(
                                    ref msac, ts.Cdf.Coef, ts.Cdf.Mode,
                                    new ReadOnlySpan<byte>(aboveCoef, aboveOff, aboveLen),
                                    new ReadOnlySpan<byte>(leftCoef, leftOff, leftLen),
                                    b.UvTx, (int)bs, b.SegId,
                                    b.YMode, b.UvMode, b.YAngle,
                                    1, 1 + pl, cf, ref txtp, out cfCtx,
                                    dqTable, ReadOnlySpan<byte>.Empty,
                                    lossless, fh.ReducedTxSet,
                                    fh.SegmentationQIdx[b.SegId], ctx.BitDepth, levels, layout);

                                int ctxW = Math.Max(0, Math.Min(uvtDim.W, (ctx.Bw - curBx + ssHor) >> ssHor));
                                int ctxH = Math.Max(0, Math.Min(uvtDim.H, (ctx.Bh - curBy + ssVer) >> ssVer));
                                if (ctxW > 0) Av1BlockContextManaged.Fill(aboveCoef, cbx4 + xx, ctxW, cfCtx);
                                if (ctxH > 0) Av1BlockContextManaged.Fill(leftCoef, cby4 + yy, ctxH, cfCtx);

                            if (eob >= 0)
                            {
                                // Dump coefficients for large non-zero blocks in bottom half
                                if (eob > 5 && curBy >= 4)
                                {
                                    var sb = new System.Text.StringBuilder($"[COEF-BIG] Block#{DbgBlockCount} bx={curBx} by={curBy} tx={b.Tx} eob={eob} coeffs:");
                                    for (int ci = 0; ci <= Math.Min(eob, 20); ci++)
                                        sb.Append($" {cf[ci]}");
                                    AvDbg.W(sb.ToString());
                                }
                                    DbgChromaEobPos++;
                                    
                                    // DBG: dump chroma dq and recon
                                    if (DbgBlockCount <= 12)
                                    {
                                        string pn = pl == 0 ? "U" : "V";
                                        var sb = new System.Text.StringBuilder($"[UV-DBG] Block#{DbgBlockCount} {pn} eob={eob} txtp={txtp} dq:");
                                        for (int di = 0; di < Math.Min(16, cf.Length); di++)
                                            sb.Append($" {cf[di],4}");
                                        AvDbg.W(sb);
                                    }
                                    
                                    if (lossless)
                                    {
                                        Av1InvTransform.InvWhtAdd(
                                            uvPlane.Slice(uvDstOff), uvStride, cf, ctx.BitDepth);
                                    }
                                    else
                                    {
                                        int shift = Av1InvTransform.TxShift[b.UvTx];
                                        Av1InvTransform.InvTxfmAdd(
                                            uvPlane.Slice(uvDstOff), uvStride, cf, eob,
                                            b.UvTx, shift, txtp, ctx.BitDepth);
                                    }
                                    
                                    // DBG: dump chroma recon after inv transform
                                    if (DbgBlockCount <= 12)
                                    {
                                        string pn = pl == 0 ? "U" : "V";
                                        var sb = new System.Text.StringBuilder($"[UV-DBG] Block#{DbgBlockCount} {pn} recon:");
                                        for (int r = 0; r < Math.Min(4, uvtDim.H * 4); r++)
                                        {
                                            sb.Append(" |");
                                            for (int c = 0; c < Math.Min(4, uvtDim.W * 4); c++)
                                                sb.Append($" {uvPlane[uvDstOff + r * uvStride + c]:x2}");
                                        }
                                        AvDbg.W(sb);
                                    }
                                    // DBG: dump block #11 chroma output (bx=6, uv_mode=D157)
                                    if (t.Bx == 6 && t.By == 0 && pl == 0)
                                    {
                                        var sb2 = new System.Text.StringBuilder("[UV-BLK11] U recon (chroma cols 12-15):");
                                        for (int r = 0; r < 4; r++)
                                        {
                                            sb2.Append(" |");
                                            for (int c = 0; c < 4; c++)
                                                sb2.Append($" {uvPlane[uvDstOff + r * uvStride + c]:x2}");
                                        }
                                        AvDbg.W(sb2);
                                        AvDbg.W($"[UV-BLK11] uvDstOff={uvDstOff} uvStride={uvStride} uvtDim=({uvtDim.W},{uvtDim.H}) mode={(Av1IntraPredMode)b.UvMode}");
            }
        }

        if (dumpTarget)
        {
            int dbw4 = Av1Tables.BlockDimensions[(int)bs, 0];
            int dbh4 = Av1Tables.BlockDimensions[(int)bs, 1];
            int dpw = dbw4 * 4, dph = dbh4 * 4;
            AvDbg.W($"[BLK-DUMP] block({bx4},{by4}) bs={(int)bs} palSz={b.PalSzY} skip={b.Skip} yMode={b.YMode} uvMode={b.UvMode} tx={b.Tx}");
            for (int dy = 0; dy < dph; dy++)
            {
                var sb = new System.Text.StringBuilder($"  y{dy}:");
                for (int dx = 0; dx < dpw; dx++)
                    sb.Append($" {yPlane[(by4 * 4 + dy) * yStride + (bx4 * 4 + dx)]:x2}");
                AvDbg.W(sb.ToString());
            }
        }
    }
                            else
                            {
                                var ccoef = pl == 0 ? t.Above.CCoef0 : t.Above.CCoef1;
                                var lcoef = pl == 0 ? t.Left.CCoef0 : t.Left.CCoef1;
                                Av1BlockContextManaged.Fill(ccoef, cbx4 + xx,
                                    Math.Max(0, Math.Min(1 << uvtDim.Lw, 32 - (cbx4 + xx))), 0x40);
                                Av1BlockContextManaged.Fill(lcoef, cby4 + yy,
                                    Math.Max(0, Math.Min(1 << uvtDim.Lh, 32 - (cby4 + yy))), 0x40);
                            }

                            uvDstOff += uvtDim.W * 4;
                }
            }
        }

        // Per-block Y pixel sum after reconstruction
        ulong postYSum = 0;
        for (int dy = 0; dy < bh4 * 4; dy++)
            for (int dx = 0; dx < bw4 * 4; dx++)
                postYSum += yPlane[(by4 * 4 + dy) * yStride + (bx4 * 4 + dx)];
        if (bw4 * 4 == 16 && by4 == 0) // first row 16-wide blocks
            AvDbg.W($"[BLK-SUM] bx={bx4} by={by4} bs={(int)bs} palSz={b.PalSzY} preSum={preYSum} postSum={postYSum}");
    }
        }
    }

    // ========================================================================
    // CFL helpers
    // ========================================================================

    /// <summary>
    /// Computes AC component from reconstructed luma for CFL prediction.
    /// Port of dav1d cfl_ac_c (ipred_tmpl.c).
    /// </summary>
    private static void ComputeCflAc(
        Span<short> ac, ReadOnlySpan<byte> ySrc, int yStride,
        int cw, int ch, int ssHor, int ssVer,
        int wPad, int hPad)
    {
        int idx = 0;
        int shift = 1 + (ssVer == 0 ? 1 : 0) + (ssHor == 0 ? 1 : 0);
        int activeH = ch - 4 * hPad;
        int activeW = cw - 4 * wPad;
        int yOff = 0;

        for (int y = 0; y < activeH; y++)
        {
            for (int x = 0; x < activeW; x++)
            {
                int acSum = ySrc[yOff + (x << ssHor)];
                if (ssHor != 0) acSum += ySrc[yOff + x * 2 + 1];
                if (ssVer != 0)
                {
                    acSum += ySrc[yOff + (x << ssHor) + yStride];
                    if (ssHor != 0) acSum += ySrc[yOff + x * 2 + 1 + yStride];
                }
                ac[idx + x] = (short)(acSum << shift);
            }
            for (int x = activeW; x < cw; x++)
                ac[idx + x] = ac[idx + x - 1];
            idx += cw;
            yOff += yStride << ssVer;
        }
        for (int y = activeH; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
                ac[idx + x] = ac[idx - cw + x];
            idx += cw;
        }

        // Subtract DC
        int log2Sz = BitOperations.TrailingZeroCount(cw) + BitOperations.TrailingZeroCount(ch);
        int sum = (1 << log2Sz) >> 1;
        for (int i = 0; i < cw * ch; i++)
            sum += ac[i];
        sum >>= log2Sz;
        for (int i = 0; i < cw * ch; i++)
            ac[i] = (short)(ac[i] - sum);
    }

    /// <summary>
    /// Applies CFL alpha scaling: dst[i] = clip(dst[i] + ((ac[i] * alpha + 32) >> 6)).
    /// </summary>
    private static void ApplyCflAlpha(
        Span<byte> dst, int stride,
        ReadOnlySpan<short> ac, int alpha,
        int w, int h)
    {
        int acIdx = 0;
        for (int row = 0; row < h; row++)
        {
            int rowOff = row * stride;
            for (int col = 0; col < w; col++)
            {
                // Match dav1d: apply_sign((abs(diff) + 32) >> 6, diff)
                // Rounds toward zero, not toward -∞
                int diff = ac[acIdx] * alpha;
                int sign = diff >> 31;
                int rounded = (Math.Abs(diff) + 32) >> 6;
                int val = dst[rowOff + col] + ((rounded ^ sign) - sign);
                dst[rowOff + col] = (byte)Math.Clamp(val, 0, 255);
                acIdx++;
            }
        }
    }

    // ========================================================================
    // Motion compensation helper
    // ========================================================================

    /// <summary>
    /// Performs single-reference motion compensation for one plane.
    /// Port of dav1d mc() (recon_tmpl.c lines 938-1050).
    /// When dstByte is non-empty, writes final pixels (put path).
    /// When dstShort is non-empty, writes intermediate values (prep path for compound).
    /// </summary>
    private static void Mc(
        Av1TaskContext t, Av1DecoderContext ctx,
        Span<byte> dstByte, Span<short> dstShort, int dstStride,
        int bw4, int bh4, int bx, int by, int pl,
        Av1MotionVector mv, Av1ReferenceFrame refp, int refIdx,
        int filter2d)
    {
        int ssVer = pl != 0 && ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = pl != 0 && ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int hMul = 4 >> ssHor, vMul = 4 >> ssVer;
        int mvx = mv.X, mvy = mv.Y;
        int mx = mvx & (15 >> (ssHor == 0 ? 1 : 0));
        int my = mvy & (15 >> (ssVer == 0 ? 1 : 0));

        var refPlane = refp.Planes[pl];
        int refStride = refp.Strides[pl];

        // Same-size path (no scaling)
        if (refp.Width == (ctx.FrameHeader!.SuperResUpscaledWidth) &&
            refp.Height == ctx.FrameHeader.Height)
        {
            int dx = bx * hMul + (mvx >> (3 + ssHor));
            int dy = by * vMul + (mvy >> (3 + ssVer));
            if (mvx != 0 || mvy != 0)
                AvDbg.W($"[MC-OFFSET] bx={bx} by={by} mv=({mvy},{mvx}) hMul={hMul} vMul={vMul} ssHor={ssHor} ssVer={ssVer} dx={dx} dy={dy} mx={mx} my={my}");
            int w, h;

            if (refPlane != ctx.CurrentPlanes[pl])
            {
                // not intrabc
                w = (ctx.FrameHeader.SuperResUpscaledWidth + ssHor) >> ssHor;
                h = (ctx.FrameHeader.Height + ssVer) >> ssVer;
            }
            else
            {
                w = ctx.Bw * 4 >> ssHor;
                h = ctx.Bh * 4 >> ssVer;
            }

            int mxFlag = mx != 0 ? 1 : 0;
            int myFlag = my != 0 ? 1 : 0;
            int blockW = bw4 * hMul;
            int blockH = bh4 * vMul;

            ReadOnlySpan<byte> refSrc;
            int refSrcStride;

            if (dx < 6 || dy < 6 ||
                dx + blockW + 7 > w ||
                dy + blockH + 7 > h)
            {
                // Out of bounds — use emulated edge with padding
                Av1MotionComp.EmuEdge(
                    blockW + 7, blockH + 7,
                    w, h, dx - 3, dy - 3,
                    t.EmuEdgeBuf, 192,
                    refPlane!, refStride);
                // EmuEdge fills EmuEdgeBuf starting at row 0,col 0 with block + padding
                refSrc = t.EmuEdgeBuf.AsSpan(0);
                refSrcStride = 192;
            }
            else
            {
                // Block is within frame — pass src 3 rows above + 3 cols left
                refSrc = refPlane.AsSpan(refStride * (dy - 3) + (dx - 3));
                refSrcStride = refStride;
            }

            if (!dstByte.IsEmpty)
            {
                // Put path — final pixel output
                if (bx == 10 && by == 10 && pl == 0)
                {
                    AvDbg.W($"[MC-SRC] bx=10 by=10 dx={dx} dy={dy} mx={mx} my={my} f2d={filter2d} blockW={blockW} blockH={blockH} stride={refSrcStride}");
                    for (int r = 0; r < 8; r++)
                    {
                        var line = "";
                        for (int c = 0; c < 8; c++) line += $" {refSrc[r * refSrcStride + c],3}";
                        AvDbg.W(line);
                    }
                }
                Av1MotionComp.Put8Tap(
                    dstByte, dstStride,
                    refSrc, refSrcStride,
                    blockW, blockH,
                    mx << (ssHor == 0 ? 1 : 0), my << (ssVer == 0 ? 1 : 0),
                    filter2d);
            }
            else
            {
                // Prep path — intermediate values for compound
                Av1MotionComp.Prep8Tap(
                    dstShort,
                    refSrc, refSrcStride,
                    blockW, blockH,
                    mx << (ssHor == 0 ? 1 : 0), my << (ssVer == 0 ? 1 : 0),
                    filter2d);
            }
        }
        else
        {
            // Scaled reference path — uses f->svc scaling parameters
            int origPosY = (by * vMul << 4) + mvy * (1 << (ssVer == 0 ? 1 : 0));
            int origPosX = (bx * hMul << 4) + mvx * (1 << (ssHor == 0 ? 1 : 0));

            int scaleX = ctx.Svc[refIdx, 0].Scale;
            int scaleY = ctx.Svc[refIdx, 1].Scale;
            int stepX = ctx.Svc[refIdx, 0].Step;
            int stepY = ctx.Svc[refIdx, 1].Step;

            int posX = ScaleMv(origPosX, scaleX);
            int posY = ScaleMv(origPosY, scaleY);

            int blockW = bw4 * hMul;
            int blockH = bh4 * vMul;

            int left = posX >> 10;
            int top = posY >> 10;
            int right = ((posX + (blockW - 1) * stepX) >> 10) + 1;
            int bottom = ((posY + (blockH - 1) * stepY) >> 10) + 1;

            int w = (refp.Width + ssHor) >> ssHor;
            int h = (refp.Height + ssVer) >> ssVer;

            ReadOnlySpan<byte> refSrc;
            int refSrcStride;

            if (left < 3 || top < 3 || right + 4 > w || bottom + 4 > h)
            {
                Av1MotionComp.EmuEdge(
                    right - left + 7, bottom - top + 7,
                    w, h, left - 3, top - 3,
                    t.EmuEdgeBuf, 320,
                    refPlane!, refStride);
                refSrc = t.EmuEdgeBuf.AsSpan(320 * 3 + 3);
                refSrcStride = 320;
            }
            else
            {
                refSrc = refPlane.AsSpan(refStride * top + left);
                refSrcStride = refStride;
            }

            // TODO: Scaled MC (mc_scaled / mct_scaled) — not yet implemented in Av1MotionComp
            // For now, fall back to unscaled MC with position fractional bits
            if (!dstByte.IsEmpty)
            {
                Av1MotionComp.Put8Tap(dstByte, dstStride, refSrc, refSrcStride,
                    blockW, blockH, posX & 0x3ff, posY & 0x3ff, filter2d);
            }
            else
            {
                Av1MotionComp.Prep8Tap(dstShort, refSrc, refSrcStride,
                    blockW, blockH, posX & 0x3ff, posY & 0x3ff, filter2d);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleMv(int val, int scale)
    {
        long tmp = (long)val * scale + (scale - 0x4000) * 8;
        long abs = Math.Abs(tmp);
        int result = (int)((abs + 128) >> 8);
        if (tmp < 0) result = -result;
        return result + 32;
    }

    // ========================================================================
    // Recursive coefficient tree reader (for inter blocks with variable TX)
    // ========================================================================

    /// <summary>
    /// Recursively reads coefficients from variable-size transform tree for inter luma.
    /// Port of dav1d read_coef_tree (recon_tmpl.c lines 731-822).
    /// </summary>
    private static void ReadCoefTree(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx,
        int bs, ref Av1Block b, int ytx, int depth, ReadOnlySpan<ushort> txSplit,
        int xOff, int yOff, Span<byte> dst, int dstStride, int dstOffset)
    {
        ref readonly var tDim = ref Av1Tables.TxfmDimensions[ytx];
        int txw = tDim.W, txh = tDim.H;

        if (depth < 2 && txSplit.Length > depth && (txSplit[depth] & (1 << (yOff * 4 + xOff))) != 0)
        {
            int sub = tDim.Sub;
            ref readonly var subTDim = ref Av1Tables.TxfmDimensions[sub];
            int txsw = subTDim.W, txsh = subTDim.H;

            ReadCoefTree(t, ref msac, ctx, bs, ref b, sub, depth + 1, txSplit,
                xOff * 2, yOff * 2, dst, dstStride, dstOffset);
            t.Bx += txsw;
            if (txw >= txh && t.Bx < ctx.Bw)
                ReadCoefTree(t, ref msac, ctx, bs, ref b, sub, depth + 1, txSplit,
                    xOff * 2 + 1, yOff * 2, dst, dstStride, dstOffset + 4 * txsw);
            t.Bx -= txsw;
            t.By += txsh;
            if (txh >= txw && t.By < ctx.Bh)
            {
                int subDstOff = dstOffset + 4 * txsh * dstStride;
                ReadCoefTree(t, ref msac, ctx, bs, ref b, sub, depth + 1, txSplit,
                    xOff * 2, yOff * 2 + 1, dst, dstStride, subDstOff);
                t.Bx += txsw;
                if (txw >= txh && t.Bx < ctx.Bw)
                    ReadCoefTree(t, ref msac, ctx, bs, ref b, sub, depth + 1, txSplit,
                        xOff * 2 + 1, yOff * 2 + 1, dst, dstStride, subDstOff + 4 * txsw);
                t.Bx -= txsw;
            }
            t.By -= txsh;
        }
        else
        {
            // Leaf node — decode coefficients and apply inverse transform
            int bx4 = t.Bx & 31, by4 = t.By & 31;

            // Leaf node — decode coefficients and apply inverse transform
            var ts = t.TileState!;
            var fh = ctx.FrameHeader!;
            Span<int> cf = t.CfBuf;
            Span<byte> levels = t.Levels;
            bool lossless = fh.SegmentationLossless[b.SegId];

            Span<ushort> dqTable = t.DqBuf;
            dqTable[0] = ts.Dq[b.SegId, 0, 0]; // dc
            dqTable[1] = ts.Dq[b.SegId, 0, 1]; // ac

            if (t.Bx == 0 && t.By == 0)
                AvDbg.W($"[INTER-DQ] dqDc={dqTable[0]} dqAc={dqTable[1]} ytx={ytx} segId={b.SegId} shift={Av1InvTransform.TxShift[ytx]}");

            var txtp = Av1TxType.DctDct;
            byte cfCtx;
            int layout = (int)fh.PixelLayout;

            int aboveLen = Math.Max(1, Math.Min(txw, 32 - bx4));
            int leftLen = Math.Max(1, Math.Min(txh, 32 - by4));

            if (t.Bx == 0 && t.By == 0)
                AvDbg.W($"[INTER-ALLSKIP] pre rng={msac.DebugRng:X4} dif_lo={(uint)msac.DebugDif:X8} dif_hi={(uint)(msac.DebugDif>>32):X8} cnt={msac.Cnt} ytx={ytx} txw={txw} txh={txh} aboveCtx={string.Join(",", t.Above.LCoef.AsSpan(bx4, aboveLen).ToArray().Select(b => b.ToString("X2")))} leftCtx={string.Join(",", t.Left.LCoef.AsSpan(by4, leftLen).ToArray().Select(b => b.ToString("X2")))}");

            // Dump the actual allskip CDF
            {
                var tDim2 = Av1Tables.TxfmDimensions[ytx];
                int sctx2 = Av1CoeffDecode.GetSkipCtx(in tDim2, (int)bs,
                    t.Above.LCoef.AsSpan(bx4, aboveLen), t.Left.LCoef.AsSpan(by4, leftLen), 0, (int)fh.PixelLayout);
                int cdfIdx2 = tDim2.Ctx * 13 + sctx2;
                AvDbg.W($"[INTER-ALLSKIP] ytx={ytx} tDim.Ctx={tDim2.Ctx} sctx={sctx2} cdfIdx={cdfIdx2} CoefSkipVal={ts.Cdf.Coef.CoefSkip[cdfIdx2][0]} CoefSkipCtr={ts.Cdf.Coef.CoefSkip[cdfIdx2][1]}");
            }

            int eob = Av1CoeffDecode.DecodeCoefs(
                ref msac, ts.Cdf.Coef, ts.Cdf.Mode,
                new ReadOnlySpan<byte>(t.Above.LCoef, bx4, aboveLen),
                new ReadOnlySpan<byte>(t.Left.LCoef, by4, leftLen),
                ytx, (int)bs, b.SegId,
                b.YMode, b.UvMode, b.YAngle,
                0, 0, cf, ref txtp, out cfCtx,
                dqTable, ReadOnlySpan<byte>.Empty,
                lossless, fh.ReducedTxSet,
                fh.SegmentationQIdx[b.SegId], ctx.BitDepth, levels, layout);

            if (t.Bx == 0 && t.By == 0)
            {
                AvDbg.W($"[INTER-EOB] bx=0 by=0 ytx={ytx} eob={eob} txtp={txtp} cfCtx={cfCtx} segQIdx={fh.SegmentationQIdx[b.SegId]} rng={msac.DebugRng}");
                if (eob >= 0)
                {
                    AvDbg.W($"[INTER-CF]");
                    for (int ci = 0; ci <= Math.Min(eob, 15); ci++)
                        AvDbg.W($" {cf[ci]}");
                    AvDbg.W();
                }
            }

            int ctw = Math.Min(txw, ctx.Bw - t.Bx);
            int cth = Math.Min(txh, ctx.Bh - t.By);
            if (ctw > 0) Av1BlockContextManaged.Fill(t.Above.LCoef, bx4, ctw, cfCtx);
            if (cth > 0) Av1BlockContextManaged.Fill(t.Left.LCoef, by4, cth, cfCtx);

            if (eob >= 0)
            {
                int shift = Av1InvTransform.TxShift[ytx];
                Av1InvTransform.InvTxfmAdd(
                    dst.Slice(dstOffset), dstStride, cf, eob,
                    ytx, shift, txtp, ctx.BitDepth);
                if (t.Bx == 0 && t.By == 0)
                {
                    AvDbg.W("[INTER-RESIDUAL] after itx: dst[0..7]=");
                    for (int px = 0; px < 8; px++)
                        AvDbg.W($" {dst[dstOffset+px]:X2}");
                    AvDbg.W($" | residual[0]={dst[dstOffset]:X2}-{dst[dstOffset] - (byte)(dst[dstOffset] > 0x4D ? 0x4D : 0)}={dst[dstOffset]-0x4D}");
                }
            }

            // Store txtp in the txtp map for chroma use (dav1d case_set_upto16 over tx area)
            for (int row = 0; row < txh && by4 + row < 32; row++)
                t.TxtpMap.AsSpan((by4 + row) * 32 + bx4, Math.Min(txw, 32 - bx4)).Fill((byte)txtp);
        }
    }

    // ========================================================================
    // Inter block reconstruction
    // ========================================================================

    /// <summary>
    /// Reconstructs an inter-predicted block: motion compensation + coefficient decode + inverse transform.
    /// Port of dav1d recon_b_inter (recon_tmpl.c lines 1557-1985).
    /// </summary>
    public static void ReconBlockInter(
        Av1TaskContext t, ref Av1Msac msac, Av1DecoderContext ctx,
        int bs, ref Av1Block b,
        Span<byte> yPlane, int yStride,
        Span<byte> uPlane, Span<byte> vPlane, int uvStride)
    {
        var ts = t.TileState!;
        var fh = ctx.FrameHeader!;

        int bx4 = t.Bx & 31, by4 = t.By & 31;
        int ssVer = ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int cbx4 = bx4 >> ssHor, cby4 = by4 >> ssVer;

        int bw4 = Av1Tables.BlockDimensions[bs, 0];
        int bh4 = Av1Tables.BlockDimensions[bs, 1];
        int w4 = Math.Min(bw4, ctx.Bw - t.Bx);
        int h4 = Math.Min(bh4, ctx.Bh - t.By);

        bool hasChroma = ctx.PixelLayout != Av1PixelLayout.I400 &&
                         (bw4 > ssHor || (t.Bx & 1) != 0) &&
                         (bh4 > ssVer || (t.By & 1) != 0);

        int chrLayoutIdx = ctx.PixelLayout == Av1PixelLayout.I400 ? 0 :
            (int)Av1PixelLayout.I444 - (int)ctx.PixelLayout;

        int cbh4 = (bh4 + ssVer) >> ssVer;
        int cbw4 = (bw4 + ssHor) >> ssHor;

        // Compute destination offsets into the plane buffers
        int yDstOff = 4 * (t.By * yStride + t.Bx);
        int uvDstOff = 4 * ((t.Bx >> ssHor) + (t.By >> ssVer) * uvStride);

        Span<byte> dst = yPlane.Slice(yDstOff);
        int filter2d = b.Filter;

        // ────────────────────────────────────────────────────────────────────
        // Prediction
        // ────────────────────────────────────────────────────────────────────

        bool isKeyOrIntra = fh.FrameType == Av1FrameType.Key ||
                            fh.FrameType == Av1FrameType.IntraOnly;

        if (isKeyOrIntra)
        {
            // IntraBC — uses block copy from same frame with bilinear filter
            Mc(t, ctx, dst, Span<short>.Empty, yStride,
                bw4, bh4, t.Bx, t.By, 0,
                b.Mv0, GetSrCurRef(ctx), 0,
                (int)Av1Filter2d.Bilinear);

            if (hasChroma)
            {
                for (int pl = 1; pl <= 2; pl++)
                {
                    var uvDst = (pl == 1 ? uPlane : vPlane).Slice(uvDstOff);
                    Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                        bw4 << (bw4 == ssHor ? 1 : 0), bh4 << (bh4 == ssVer ? 1 : 0),
                        t.Bx & ~ssHor, t.By & ~ssVer, pl,
                        b.Mv0, GetSrCurRef(ctx), 0,
                        (int)Av1Filter2d.Bilinear);
                }
            }
        }
        else if (b.CompType == (byte)Av1CompInterType.None)
        {
            // ──── Single reference ────
            var refp = ctx.RefFrames[b.Ref0];

            if (t.Bx <= 2 && t.By == 0)
                AvDbg.W($"[INTER-RECON] bx={t.Bx} by={t.By} mv0=({b.Mv0.Y},{b.Mv0.X}) ref0={b.Ref0} bs={bs} pred_before_MC: dst[0]={dst[0]}");

            if (Math.Min(bw4, bh4) > 1 &&
                ((b.InterMode == (byte)Av1InterPredMode.GlobalMv && ctx.GmvWarpAllowed[b.Ref0]) ||
                 (b.Motion == (byte)Av1MotionMode.Warp && t.WarpMv.Type > Av1WarpedMotionType.Translation)))
            {
                // Warp affine motion
                WarpAffine(t, ctx, dst, Span<short>.Empty, yStride,
                    bs, 0, refp,
                    b.Motion == (byte)Av1MotionMode.Warp ? t.WarpMv : fh.Gmv[b.Ref0]);
            }
            else
            {
                Mc(t, ctx, dst, Span<short>.Empty, yStride,
                    bw4, bh4, t.Bx, t.By, 0,
                    b.Mv0, refp, b.Ref0, filter2d);

                if (b.Mv0.Y != 0 || b.Mv0.X != 0) {
                    AvDbg.W($"[MC-SUBPEL] bx={t.Bx} by={t.By} mv=({b.Mv0.Y},{b.Mv0.X}) filter={filter2d} dst[0]={dst[0]:X2}({dst[0]}) dst[1]={dst[1]:X2}({dst[1]}) refIdx={b.Ref0}");
                }

                if (t.Bx == 10 && t.By == 10 && b.Motion == (byte)Av1MotionMode.Obmc) {
                    var sb = new System.Text.StringBuilder("[MC-PRE-OBMC] bx=10 by=10:\n");
                    for (int r = 0; r < 8; r++) {
                        for (int c = 0; c < 8; c++) sb.Append($" {dst[r * yStride + c],3}");
                        sb.Append("\n");
                    }
                    AvDbg.W(sb.ToString());
                }

                if (t.Bx == 0 && t.By == 0) {
                    AvDbg.W($"[INTER-MC] after mc mv=({b.Mv0.Y},{b.Mv0.X}) dst[0]={dst[0]:X2}({dst[0]}) dst[8]={dst[8]:X2} refIdx={b.Ref0}");
                }
                if (t.Bx == 2 && t.By == 0) {
                    AvDbg.W($"[INTER-MC-B1] after mc mv=({b.Mv0.Y},{b.Mv0.X}) dst[0]={dst[0]:X2}({dst[0]}) dst[1]={dst[1]:X2}({dst[1]}) dst[2]={dst[2]:X2}({dst[2]}) dst[3]={dst[3]:X2}({dst[3]}) refIdx={b.Ref0}");
                }

                if (b.Motion == (byte)Av1MotionMode.Obmc)
                {
                    Obmc(t, ctx, dst, yStride, bs, 0, bx4, by4, w4, h4);
                }
            }

            // InterIntra blending (if applicable)
            if (b.InterIntraTypeField != 0)
            {
                Span<byte> tlEdge = t.EdgeBuf.AsSpan();
                int edgeCenter = 128;
                Span<byte> tmp = t.InterIntraBuf.AsSpan();

                int iiMode = b.InterIntraMode == (byte)Av1InterIntraPredMode.Smooth
                    ? (int)Av1IntraPredMode.Smooth
                    : b.InterIntraMode;

                int angle = 0;
                ReadOnlySpan<byte> topSbEdge = ReadOnlySpan<byte>.Empty;
                if ((t.By & (ctx.SbStep - 1)) == 0)
                {
                    int sby = t.By >> ctx.SbShift;
                    if (sby > 0)
                        topSbEdge = ctx.IpredEdgeY.AsSpan(ctx.Sb128w * 128 * (sby - 1));
                }

                int implMode = PrepareIntraEdges(
                    t.Bx, t.Bx > ts.ColStart,
                    t.By, t.By > ts.RowStart,
                    ts.ColEnd, ts.RowEnd,
                    0, yPlane, yDstOff, yStride, topSbEdge,
                    (Av1IntraPredMode)iiMode, ref angle,
                    bw4, bh4, false, tlEdge, edgeCenter, ctx.BitDepth);

                Av1IntraPred.Predict(implMode, tmp, bw4 * 4,
                    tlEdge, edgeCenter, bw4 * 4, bh4 * 4, angle, 0, 0, ctx.BitDepth);

                // Blend interintra prediction with inter prediction (dav1d: dsp->mc.blend, II_MASK(0, bs, b))
                {
                    var mask = Av1WedgeMasks.GetMask(0, bs, b.InterIntraTypeField,
                        b.InterIntraTypeField == 2 ? b.WedgeIdx : b.InterIntraMode, out int mw, out int mh);
                    int tmpStride = bw4 * 4;
                    for (int y = 0; y < Math.Min(mh, bh4 * 4); y++)
                    {
                        for (int x = 0; x < Math.Min(mw, bw4 * 4); x++)
                        {
                            int m = mask[y * mw + x];
                            int di = y * yStride + x;
                            dst[di] = (byte)((dst[di] * (64 - m) + tmp[y * tmpStride + x] * m + 32) >> 6);
                        }
                    }
                }
            }

            // Chroma single-reference prediction
            if (!hasChroma) goto SkipInterChromaPred;

            // Sub8×8 chroma derivation
            bool isSub8x8 = bw4 == ssHor || bh4 == ssVer;

            if (isSub8x8 && ssHor == 1 && ctx.Blocks != null)
            {
                // Sub8×8 chroma requires looking at neighboring block MVs
                // to reconstruct the chroma plane from multiple references.
                // This handles 4×4/4×8/8×4 luma blocks where chroma is 2×2/2×4/4×2.
                int rIdx = (t.By & 31) + 5;

                if (bw4 == 1)
                    isSub8x8 &= GetRefMvBlock(t, t.By, t.Bx - 1).Ref.Ref0 > 0;
                if (bh4 == ssVer)
                    isSub8x8 &= GetRefMvBlock(t, t.By - 1, t.Bx).Ref.Ref0 > 0;
                if (bw4 == 1 && bh4 == ssVer)
                    isSub8x8 &= GetRefMvBlock(t, t.By - 1, t.Bx - 1).Ref.Ref0 > 0;
            }

            if (isSub8x8 && ssHor == 1)
            {
                // Sub8×8 chroma MC from multiple neighbor blocks
                int hOff = 0, vOff = 0;

                if (bw4 == 1 && bh4 == ssVer)
                {
                    var nb = GetRefMvBlock(t, t.By - 1, t.Bx - 1);
                    int nbRef = nb.Ref.Ref0 - 1;
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff);
                        Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                            bw4, bh4, t.Bx - 1, t.By - 1, 1 + pl,
                            nb.Mv.Mv0, ctx.RefFrames[nbRef], nbRef,
                            t.Tl4x4Filter);
                    }
                    vOff = 2 * uvStride;
                    hOff = 2;
                }

                if (bw4 == 1)
                {
                    var nb = GetRefMvBlock(t, t.By, t.Bx - 1);
                    int nbRef = nb.Ref.Ref0 - 1;
                    int leftFilter = Av1Tables.Filter2d[t.Left.Filter1[by4], t.Left.Filter0[by4]];
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff + vOff);
                        Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                            bw4, bh4, t.Bx - 1, t.By, 1 + pl,
                            nb.Mv.Mv0, ctx.RefFrames[nbRef], nbRef,
                            leftFilter);
                    }
                    hOff = 2;
                }

                if (bh4 == ssVer)
                {
                    var nb = GetRefMvBlock(t, t.By - 1, t.Bx);
                    int nbRef = nb.Ref.Ref0 - 1;
                    int topFilter = Av1Tables.Filter2d[t.Above.Filter1[bx4], t.Above.Filter0[bx4]];
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff + hOff);
                        Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                            bw4, bh4, t.Bx, t.By - 1, 1 + pl,
                            nb.Mv.Mv0, ctx.RefFrames[nbRef], nbRef,
                            topFilter);
                    }
                    vOff = 2 * uvStride;
                }

                // Current block contribution
                for (int pl = 0; pl < 2; pl++)
                {
                    var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff + hOff + vOff);
                    Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                        bw4, bh4, t.Bx, t.By, 1 + pl,
                        b.Mv0, refp, b.Ref0, filter2d);
                }
            }
            else
            {
                // Normal chroma MC
                if (Math.Min(cbw4, cbh4) > 1 &&
                    ((b.InterMode == (byte)Av1InterPredMode.GlobalMv && ctx.GmvWarpAllowed[b.Ref0]) ||
                     (b.Motion == (byte)Av1MotionMode.Warp && t.WarpMv.Type > Av1WarpedMotionType.Translation)))
                {
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff);
                        WarpAffine(t, ctx, uvDst, Span<short>.Empty, uvStride,
                            bs, 1 + pl, refp,
                            b.Motion == (byte)Av1MotionMode.Warp ? t.WarpMv : fh.Gmv[b.Ref0]);
                    }
                }
                else
                {
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff);
                        Mc(t, ctx, uvDst, Span<short>.Empty, uvStride,
                            bw4 << (bw4 == ssHor ? 1 : 0), bh4 << (bh4 == ssVer ? 1 : 0),
                            t.Bx & ~ssHor, t.By & ~ssVer, 1 + pl,
                            b.Mv0, refp, b.Ref0, filter2d);

                        if (b.Motion == (byte)Av1MotionMode.Obmc)
                        {
                            var uvDstObmc = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff);
                            Obmc(t, ctx, uvDstObmc, uvStride, bs, 1 + pl, bx4, by4, w4, h4);
                        }
                    }
                }

                // InterIntra chroma blending
                if (b.InterIntraTypeField != 0)
                {
                    for (int pl = 0; pl < 2; pl++)
                    {
                        Span<byte> tmp = t.InterIntraBuf.AsSpan();
                        Span<byte> tlEdge = t.EdgeBuf.AsSpan();
                        int edgeCenter = 128;

                        int iiMode = b.InterIntraMode == (byte)Av1InterIntraPredMode.Smooth
                            ? (int)Av1IntraPredMode.Smooth
                            : b.InterIntraMode;
                        int angle = 0;
                        var uvPlaneFull = pl == 0 ? uPlane : vPlane;

                        ReadOnlySpan<byte> topSbEdge = ReadOnlySpan<byte>.Empty;
                        if ((t.By & (ctx.SbStep - 1)) == 0)
                        {
                            var ipredEdge = pl == 0 ? ctx.IpredEdgeU : ctx.IpredEdgeV;
                            int sby = t.By >> ctx.SbShift;
                            if (sby > 0)
                                topSbEdge = ipredEdge.AsSpan(ctx.Sb128w * 128 * (sby - 1));
                        }

                        int implMode = PrepareIntraEdges(
                            t.Bx >> ssHor, (t.Bx >> ssHor) > (ts.ColStart >> ssHor),
                            t.By >> ssVer, (t.By >> ssVer) > (ts.RowStart >> ssVer),
                            ts.ColEnd >> ssHor, ts.RowEnd >> ssVer,
                            0, uvPlaneFull, uvDstOff, uvStride, topSbEdge,
                            (Av1IntraPredMode)iiMode, ref angle,
                            cbw4, cbh4, false, tlEdge, edgeCenter, ctx.BitDepth);

                        Av1IntraPred.Predict(implMode, tmp, cbw4 * 4,
                            tlEdge, edgeCenter, cbw4 * 4, cbh4 * 4, angle, 0, 0, ctx.BitDepth);

                        // Blend with ii_mask (dav1d: dsp->mc.blend, II_MASK(chr_layout_idx, bs, b))
                        {
                            var uvDst = uvPlaneFull.Slice(uvDstOff);
                            var mask = Av1WedgeMasks.GetMask(2, (int)bs, b.InterIntraTypeField,
                                b.InterIntraTypeField == 2 ? b.WedgeIdx : b.InterIntraMode, out int mw, out int mh);
                            int tmpStride = cbw4 * 4;
                            for (int y = 0; y < Math.Min(mh, cbh4 * 4); y++)
                            {
                                for (int x = 0; x < Math.Min(mw, cbw4 * 4); x++)
                                {
                                    int m = mask[y * mw + x];
                                    int di = y * uvStride + x;
                                    uvDst[di] = (byte)((uvDst[di] * (64 - m) + tmp[y * tmpStride + x] * m + 32) >> 6);
                                }
                            }
                        }
                    }
                }
            }

            SkipInterChromaPred:
            t.Tl4x4Filter = (byte)filter2d;
        }
        else
        {
            // ──── Compound reference prediction ────
            Span<short> tmp0 = t.CompInterBuf[0].AsSpan();
            Span<short> tmp1 = t.CompInterBuf[1].AsSpan();

            for (int i = 0; i < 2; i++)
            {
                int refIdx = i == 0 ? b.Ref0 : b.Ref1;
                var refp = ctx.RefFrames[refIdx];
                var mv = i == 0 ? b.Mv0 : b.Mv1;
                var tmpBuf = i == 0 ? tmp0 : tmp1;

                if (b.InterMode == (byte)Av1CompInterPredMode.GlobalGlobal &&
                    ctx.GmvWarpAllowed[refIdx])
                {
                    WarpAffine(t, ctx, Span<byte>.Empty, tmpBuf, bw4 * 4,
                        bs, 0, refp, fh.Gmv[refIdx]);
                }
                else
                {
                    Mc(t, ctx, Span<byte>.Empty, tmpBuf, 0,
                        bw4, bh4, t.Bx, t.By, 0,
                        mv, refp, refIdx, filter2d);
                }
            }

            // Blend the two predictions
            int blockW = bw4 * 4, blockH = bh4 * 4;
            switch ((Av1CompInterType)b.CompType)
            {
                case Av1CompInterType.Average:
                    Av1MotionComp.Avg(dst, yStride, tmp0, tmp1, blockW, blockH);
                    break;

                case Av1CompInterType.WeightedAvg:
                {
                    int jntWeight = ctx.JntWeights[b.Ref0, b.Ref1];
                    Av1MotionComp.WeightedAvg(dst, yStride, tmp0, tmp1, blockW, blockH, jntWeight);
                    break;
                }

                case Av1CompInterType.Seg:
                    Av1MotionComp.WeightedMask(
                        dst, yStride,
                        b.MaskSign != 0 ? tmp1 : tmp0,
                        b.MaskSign != 0 ? tmp0 : tmp1,
                        blockW, blockH, t.SegMask, b.MaskSign,
                        ssHor, ssVer);
                    // segMask is also used as the mask for chroma
                    break;

                case Av1CompInterType.Wedge:
                    // TODO: Wedge mask lookup (requires dav1d_masks table port)
                    // For now, fall back to average
                    Av1MotionComp.Avg(dst, yStride, tmp0, tmp1, blockW, blockH);
                    break;
            }

            // Compound chroma
            if (hasChroma)
            {
                for (int pl = 0; pl < 2; pl++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int refIdx = i == 0 ? b.Ref0 : b.Ref1;
                        var refp = ctx.RefFrames[refIdx];
                        var mv = i == 0 ? b.Mv0 : b.Mv1;
                        var tmpBuf = i == 0 ? tmp0 : tmp1;

                        if (b.InterMode == (byte)Av1CompInterPredMode.GlobalGlobal &&
                            Math.Min(cbw4, cbh4) > 1 && ctx.GmvWarpAllowed[refIdx])
                        {
                            WarpAffine(t, ctx, Span<byte>.Empty, tmpBuf, bw4 * 4 >> ssHor,
                                bs, 1 + pl, refp, fh.Gmv[refIdx]);
                        }
                        else
                        {
                            Mc(t, ctx, Span<byte>.Empty, tmpBuf, 0,
                                bw4, bh4, t.Bx, t.By, 1 + pl,
                                mv, refp, refIdx, filter2d);
                        }
                    }

                    var uvDst = (pl == 0 ? uPlane : vPlane).Slice(uvDstOff);
                    int uvW = bw4 * 4 >> ssHor, uvH = bh4 * 4 >> ssVer;

                    switch ((Av1CompInterType)b.CompType)
                    {
                        case Av1CompInterType.Average:
                            Av1MotionComp.Avg(uvDst, uvStride, tmp0, tmp1, uvW, uvH);
                            break;

                        case Av1CompInterType.WeightedAvg:
                        {
                            int jntWeight = ctx.JntWeights[b.Ref0, b.Ref1];
                            Av1MotionComp.WeightedAvg(uvDst, uvStride, tmp0, tmp1, uvW, uvH, jntWeight);
                            break;
                        }

                        case Av1CompInterType.Wedge:
                        case Av1CompInterType.Seg:
                            // For wedge/seg compound, use the mask for blending
                            // TODO: Proper wedge/seg mask for chroma
                            Av1MotionComp.Avg(uvDst, uvStride, tmp0, tmp1, uvW, uvH);
                            break;
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Coefficient decode + inverse transform
        // ────────────────────────────────────────────────────────────────────

        int cw4 = (w4 + ssHor) >> ssHor, ch4 = (h4 + ssVer) >> ssVer;

        if (b.Skip != 0)
        {
            // Reset coef contexts to "no coefficients"
            int bw4log2 = Av1Tables.BlockDimensions[bs, 2];
            int bh4log2 = Av1Tables.BlockDimensions[bs, 3];
            FillCoefCtx(t.Above.LCoef, bx4, bw4, 0x40);
            FillCoefCtx(t.Left.LCoef, by4, bh4, 0x40);
            if (hasChroma)
            {
                FillCoefCtx(t.Above.CCoef0, cbx4, cbw4, 0x40);
                FillCoefCtx(t.Above.CCoef1, cbx4, cbw4, 0x40);
                FillCoefCtx(t.Left.CCoef0, cby4, cbh4, 0x40);
                FillCoefCtx(t.Left.CCoef1, cby4, cbh4, 0x40);
            }
            return;
        }

        ref readonly var uvtDim = ref Av1Tables.TxfmDimensions[b.UvTx];
        ref readonly var ytDim = ref Av1Tables.TxfmDimensions[b.MaxYTx];
        ushort[] txSplit = [b.TxSplit0, b.TxSplit1];

        for (int initY = 0; initY < bh4; initY += 16)
        {
            for (int initX = 0; initX < bw4; initX += 16)
            {
                // ── Luma coefficient coding & inverse transforms ──
                int yOff = initY != 0 ? 1 : 0;
                int saveBx = t.Bx, saveBy = t.By;

                for (int y = initY; y < Math.Min(h4, initY + 16); y += ytDim.H, yOff++)
                {
                    int xOff = initX != 0 ? 1 : 0;
                    for (int x = initX; x < Math.Min(w4, initX + 16); x += ytDim.W, xOff++)
                    {
                        int lDstOff = yDstOff + (initY + y) * 4 * yStride + x * 4;
                        ReadCoefTree(t, ref msac, ctx, bs, ref b, b.MaxYTx, 0, txSplit,
                            xOff, yOff, yPlane, yStride, lDstOff);
                        t.Bx += ytDim.W;
                    }
                    t.Bx = saveBx;
                    t.By += ytDim.H;
                }
                t.By = saveBy;

                // ── Chroma coefficient coding & inverse transforms ──
                if (hasChroma)
                {
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var uvPlane = pl == 0 ? uPlane : vPlane;
                        var aboveCoef = pl == 0 ? t.Above.CCoef0 : t.Above.CCoef1;
                        var leftCoef = pl == 0 ? t.Left.CCoef0 : t.Left.CCoef1;
                        int saveBx2 = t.Bx, saveBy2 = t.By;

                        for (int y = initY >> ssVer; y < Math.Min(ch4, (initY + 16) >> ssVer); y += uvtDim.H)
                        {
                            for (int x = initX >> ssHor; x < Math.Min(cw4, (initX + 16) >> ssHor); x += uvtDim.W)
                            {
                                // txtp comes from the luma txtp map (dav1d: txtp_map read, recon_tmpl.c:1954)
                                var txtp = (Av1TxType)t.TxtpMap[(by4 + (y << ssVer)) * 32 + bx4 + (x << ssHor)];

                                Span<int> cf = t.CfBuf;
                                Span<byte> levels = t.Levels;
                                bool lossless = fh.SegmentationLossless[b.SegId];

                                Span<ushort> dqTable = t.DqBuf;
                                dqTable[0] = ts.Dq[b.SegId, 1 + pl, 0]; // dc
                                dqTable[1] = ts.Dq[b.SegId, 1 + pl, 1]; // ac

                                int aboveOff = cbx4 + x;
                                int leftOff = cby4 + y;
                                int aboveLen = Math.Max(1, Math.Min(uvtDim.W, 32 - aboveOff));
                                int leftLen = Math.Max(1, Math.Min(uvtDim.H, 32 - leftOff));

                                int eob = Av1CoeffDecode.DecodeCoefs(
                                    ref msac, ts.Cdf.Coef, ts.Cdf.Mode,
                                    new ReadOnlySpan<byte>(aboveCoef, aboveOff, aboveLen),
                                    new ReadOnlySpan<byte>(leftCoef, leftOff, leftLen),
                                    b.UvTx, (int)bs, b.SegId,
                                    b.YMode, b.UvMode, b.YAngle,
                                    0, 1 + pl, cf, ref txtp, out byte cfCtx,
                                    dqTable, ReadOnlySpan<byte>.Empty,
                                    lossless, fh.ReducedTxSet,
                                    fh.SegmentationQIdx[b.SegId], ctx.BitDepth, levels, (int)fh.PixelLayout);

                                int ctw = Math.Min(uvtDim.W, (ctx.Bw - t.Bx + ssHor) >> ssHor);
                                int cth = Math.Min(uvtDim.H, (ctx.Bh - t.By + ssVer) >> ssVer);
                                if (ctw > 0) Av1BlockContextManaged.Fill(aboveCoef, cbx4 + x, ctw, cfCtx);
                                if (cth > 0) Av1BlockContextManaged.Fill(leftCoef, cby4 + y, cth, cfCtx);

                                if (eob >= 0)
                                {
                                    int uvOff = uvDstOff + y * 4 * uvStride + x * 4;
                                    int shift = Av1InvTransform.TxShift[b.UvTx];
                                    Av1InvTransform.InvTxfmAdd(
                                        uvPlane.Slice(uvOff), uvStride, cf, eob,
                                        b.UvTx, shift, txtp, ctx.BitDepth);
                                }

                                t.Bx += uvtDim.W << ssHor;
                            }
                            t.Bx = saveBx2;
                            t.By += uvtDim.H << ssVer;
                        }
                        t.By = saveBy2;
                    }
                }
            }
        }
    }

    // ========================================================================
    // Warp affine helper
    // ========================================================================

    /// <summary>
    /// Applies warp-affine motion compensation for one plane.
    /// Port of dav1d warp_affine (recon_tmpl.c lines 1115-1174).
    /// Processes 8×8 blocks using the affine warp kernel.
    /// </summary>
    private static void WarpAffine(
        Av1TaskContext t, Av1DecoderContext ctx,
        Span<byte> dst8, Span<short> dst16, int dstStride,
        int bs, int pl, Av1ReferenceFrame refp,
        Av1WarpedMotionParams wmp)
    {
        int ssVer = pl != 0 && ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int ssHor = pl != 0 && ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int hMul = 4 >> ssHor, vMul = 4 >> ssVer;

        int bw4 = Av1Tables.BlockDimensions[bs, 0];
        int bh4 = Av1Tables.BlockDimensions[bs, 1];

        int width = (refp.Width + ssHor) >> ssHor;
        int height = (refp.Height + ssVer) >> ssVer;

        ReadOnlySpan<short> abcd = [wmp.Alpha, wmp.Beta, wmp.Gamma, wmp.Delta];

        var refPlane = refp.Planes[pl];
        int refStride = refp.Strides[pl];

        int dst8Off = 0;
        int dst16Off = 0;

        for (int y = 0; y < bh4 * vMul; y += 8)
        {
            int srcY = t.By * 4 + ((y + 4) << ssVer);
            long mat3Y = (long)wmp.Matrix3 * srcY + wmp.Matrix0;
            long mat5Y = (long)wmp.Matrix5 * srcY + wmp.Matrix1;

            for (int x = 0; x < bw4 * hMul; x += 8)
            {
                int srcX = t.Bx * 4 + ((x + 4) << ssHor);
                long mvx = ((long)wmp.Matrix2 * srcX + mat3Y) >> ssHor;
                long mvy = ((long)wmp.Matrix4 * srcX + mat5Y) >> ssVer;

                int dx = (int)(mvx >> 16) - 4;
                int mx = (((int)mvx & 0xffff) - wmp.Alpha * 4 - wmp.Beta * 7) & ~0x3f;
                int dy = (int)(mvy >> 16) - 4;
                int my = (((int)mvy & 0xffff) - wmp.Gamma * 4 - wmp.Delta * 4) & ~0x3f;

                ReadOnlySpan<byte> refSrc;
                int refSrcStride;

                if (dx < 3 || dx + 8 + 4 > width || dy < 3 || dy + 8 + 4 > height)
                {
                    Av1MotionComp.EmuEdge(
                        15, 15, width, height, dx - 3, dy - 3,
                        t.EmuEdgeBuf, 32,
                        refPlane!, refStride);
                    refSrc = t.EmuEdgeBuf.AsSpan(32 * 3 + 3);
                    refSrcStride = 32;
                }
                else
                {
                    refSrc = refPlane.AsSpan(refStride * dy + dx);
                    refSrcStride = refStride;
                }

                if (!dst16.IsEmpty)
                {
                    Av1MotionComp.WarpAffine8x8t(
                        dst16.Slice(dst16Off + x), dstStride,
                        refSrc, refSrcStride, abcd, mx, my);
                }
                else
                {
                    Av1MotionComp.WarpAffine8x8(
                        dst8.Slice(dst8Off + x), dstStride,
                        refSrc, refSrcStride, abcd, mx, my);
                }
            }

            if (!dst8.IsEmpty)
                dst8Off += 8 * dstStride;
            else
                dst16Off += 8 * dstStride;
        }
    }

    // ========================================================================
    // OBMC helper
    // ========================================================================

    /// <summary>
    /// Overlapped block motion compensation — blends current prediction with neighbor predictions.
    /// Port of dav1d obmc (recon_tmpl.c lines 1052-1110).
    /// </summary>
    private static void Obmc(
        Av1TaskContext t, Av1DecoderContext ctx,
        Span<byte> dst, int dstStride,
        int bs, int pl, int bx4, int by4, int w4, int h4)
    {
        // Port of dav1d obmc (recon_tmpl.c lines 1062-1123).
        if ((t.Bx & 1) != 0 || (t.By & 1) != 0) return;

        var rt = t.Rt;
        int rOff = (t.By & 31) + 5;
        int ssHor = pl != 0 && ctx.PixelLayout != Av1PixelLayout.I444 ? 1 : 0;
        int ssVer = pl != 0 && ctx.PixelLayout == Av1PixelLayout.I420 ? 1 : 0;
        int hMul = 4 >> ssHor, vMul = 4 >> ssVer;
        int bw4 = Av1Tables.BlockDimensions[bs, 0];
        int bh4 = Av1Tables.BlockDimensions[bs, 1];
        int bwLog2 = Av1Tables.BlockDimensions[bs, 2];
        int bhLog2 = Av1Tables.BlockDimensions[bs, 3];

        byte[] lapPool = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            // ── Above neighbor OBMC (dav1d recon_tmpl.c:1076-1099) ──
            if (t.By > rt.TileRowStart && (pl == 0 || bw4 * hMul + bh4 * vMul >= 16))
            {
                var aboveRow = rOff - 1 >= 0 && rOff - 1 < rt.R.Length ? rt.R[rOff - 1] : null;
                if (aboveRow != null)
                {
                    for (int i = 0, x = 0; x < w4 && i < Math.Min(bwLog2, 4);)
                    {
                        // only odd blocks are considered for overlap handling, hence +1
                        if (t.Bx + x + 1 >= aboveRow.Length) break;
                        ref var aR = ref aboveRow[t.Bx + x + 1];
                        int step4 = Math.Clamp((int)Av1Tables.BlockDimensions[aR.Bs, 0], 2, 16);

                        if (aR.Ref.Ref0 > 0)
                        {
                            int ow4 = Math.Min(step4, bw4);
                            int oh4 = Math.Min(bh4, 16) >> 1;
                            int mcH = (oh4 * 3 + 3) >> 2;
                            int lapW = ow4 * hMul, lapH = mcH * vMul;
                            Span<byte> lap = lapPool.AsSpan(0, lapW * lapH);
                            int filter2d = Av1Tables.Filter2d[t.Above.Filter1[bx4 + x + 1], t.Above.Filter0[bx4 + x + 1]];
                            var refp = ctx.RefFrames[aR.Ref.Ref0];

                            Mc(t, ctx, lap, default, lapW, ow4, mcH, t.Bx + x, t.By, pl,
                                aR.Mv.Mv0, refp, aR.Ref.Ref0, filter2d);

                            // blend_h: rows [0, (vMul*oh4*3)>>2), per-row mask = ObmcMasks[vMul*oh4 + row]
                            int hpx = vMul * oh4;
                            int blendRows = (hpx * 3) >> 2;
                            for (int dy = 0; dy < blendRows; dy++)
                            {
                                int m = Av1Tables.ObmcMasks[hpx + dy];
                                for (int dx = 0; dx < lapW; dx++)
                                {
                                    int di = dy * dstStride + x * hMul + dx;
                                    int s = lap[dy * lapW + dx];
                                    int d = dst[di];
                                    dst[di] = (byte)((d * (64 - m) + s * m + 32) >> 6);
                                }
                            }
                            i++;
                        }
                        x += step4;
                    }
                }
            }

            // ── Left neighbor OBMC (dav1d recon_tmpl.c:1101-1121) ──
            if (t.Bx > rt.TileColStart)
            {
                for (int i = 0, y = 0; y < h4 && i < Math.Min(bhLog2, 4);)
                {
                    // only odd blocks are considered for overlap handling, hence +1
                    if (rOff + y + 1 >= rt.R.Length) break;
                    var leftRow = rt.R[rOff + y + 1];
                    if (leftRow == null || t.Bx - 1 < 0 || t.Bx - 1 >= leftRow.Length) break;
                    ref var lR = ref leftRow[t.Bx - 1];
                    int step4 = Math.Clamp((int)Av1Tables.BlockDimensions[lR.Bs, 1], 2, 16);

                    if (lR.Ref.Ref0 > 0)
                    {
                        int ow4 = Math.Min(bw4, 16) >> 1;
                        int oh4 = Math.Min(step4, bh4);
                        int lapW = ow4 * hMul, lapH = oh4 * vMul;
                        Span<byte> lap = lapPool.AsSpan(0, lapW * lapH);
                        int filter2d = Av1Tables.Filter2d[t.Left.Filter1[by4 + y + 1], t.Left.Filter0[by4 + y + 1]];
                        var refp = ctx.RefFrames[lR.Ref.Ref0];

                        Mc(t, ctx, lap, default, lapW, ow4, oh4, t.Bx, t.By + y, pl,
                            lR.Mv.Mv0, refp, lR.Ref.Ref0, filter2d);

                        // blend_v: columns [0, (lapW*3)>>2), per-col mask = ObmcMasks[lapW + col]
                        int blendCols = (lapW * 3) >> 2;
                        for (int dy = 0; dy < lapH; dy++)
                        {
                            for (int dx = 0; dx < blendCols; dx++)
                            {
                                int m = Av1Tables.ObmcMasks[lapW + dx];
                                int di = (y * vMul + dy) * dstStride + dx;
                                int s = lap[dy * lapW + dx];
                                int d = dst[di];
                                dst[di] = (byte)((d * (64 - m) + s * m + 32) >> 6);
                            }
                        }
                        i++;
                    }
                    y += step4;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(lapPool);
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>Creates a temporary reference pointing to the current frame (for IntraBC).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Av1ReferenceFrame GetSrCurRef(Av1DecoderContext ctx)
    {
        // IntraBC uses the current frame as the reference
        var r = new Av1ReferenceFrame
        {
            Width = ctx.FrameHeader!.SuperResUpscaledWidth,
            Height = ctx.FrameHeader.Height,
            Planes = ctx.CurrentPlanes,
            Strides = ctx.CurrentStrides
        };
        return r;
    }

    /// <summary>Gets a refmvs block from the spatial MV grid for sub8×8 chroma neighbor lookups (dav1d: t->rt.r).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Av1RefMvsBlock GetRefMvBlock(Av1TaskContext t, int by, int bx)
    {
        return ref t.Rt.R[(by & 31) + 5][bx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillCoefCtx(byte[] ctx, int offset, int count, byte value)
    {
        int end = Math.Min(offset + count, ctx.Length);
        for (int i = offset; i < end; i++)
            ctx[i] = value;
    }
}

