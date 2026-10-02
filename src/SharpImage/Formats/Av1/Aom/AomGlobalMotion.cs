using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 global motion estimation (GLOBAL_MOTION_METHOD_DISFLOW): aom_dsp/pyramid.c,
// third_party/fastfeat (fast.c / nonmax.c; fast_9.c is AomFast9.Generated.cs), aom_dsp/flow_estimation/corner_detect.c,
// disflow.c (the AVX2 flow-at-point is bit-exact with C), ransac.c, av1/encoder/global_motion.c and
// global_motion_facade.c.
internal static partial class AomFast9
{
    internal static int[] MakeOffsets(int s) => new[]
    {
        0 + s * 3, 1 + s * 3, 2 + s * 2, 3 + s * 1, 3, 3 - s, 2 - s * 2, 1 - s * 3, -s * 3, -1 - s * 3, -2 - s * 2, -3 - s, -3, -3 + s, -2 + s * 2,
        -1 + s * 3,
    };

    /// <summary>aom_fast9_detect_nonmax: (x, y) corners and their scores.</summary>
    public static (List<(int x, int y)> corners, List<int> scores) DetectNonmax(byte[] im, int imOff, int xsize, int ysize, int stride, int b)
    {
        var corners = Fast9Detect(im, imOff, xsize, ysize, stride, b);
        int[] pixel = MakeOffsets(stride);
        var scores = new int[corners.Count];
        for (int n = 0; n < corners.Count; n++) scores[n] = Fast9CornerScore(im, imOff + corners[n].y * stride + corners[n].x, pixel, b);
        var retC = new List<(int x, int y)>();
        var retS = new List<int>();
        int sz = corners.Count;
        if (sz < 1) return (retC, retS);
        int lastRow = corners[sz - 1].y;
        var rowStart = new int[lastRow + 1];
        Array.Fill(rowStart, -1);
        int prevRow = -1;
        for (int i = 0; i < sz; i++)
            if (corners[i].y != prevRow) { rowStart[corners[i].y] = i; prevRow = corners[i].y; }
        int pointAbove = 0, pointBelow = 0;
        for (int i = 0; i < sz; i++)
        {
            int score = scores[i];
            var pos = corners[i];
            if (i > 0 && corners[i - 1].x == pos.x - 1 && corners[i - 1].y == pos.y && scores[i - 1] >= score) continue;
            if (i < sz - 1 && corners[i + 1].x == pos.x + 1 && corners[i + 1].y == pos.y && scores[i + 1] >= score) continue;
            bool suppressed = false;
            if (pos.y > 0 && rowStart[pos.y - 1] != -1)
            {
                if (corners[pointAbove].y < pos.y - 1) pointAbove = rowStart[pos.y - 1];
                for (; corners[pointAbove].y < pos.y && corners[pointAbove].x < pos.x - 1; pointAbove++) { }
                for (int j = pointAbove; corners[j].y < pos.y && corners[j].x <= pos.x + 1; j++)
                {
                    int x = corners[j].x;
                    if ((x == pos.x - 1 || x == pos.x || x == pos.x + 1) && scores[j] >= score) { suppressed = true; break; }
                }
                if (suppressed) continue;
            }
            if (pos.y + 1 < lastRow + 1 && rowStart[pos.y + 1] != -1 && pointBelow < sz)
            {
                if (corners[pointBelow].y < pos.y + 1) pointBelow = rowStart[pos.y + 1];
                for (; pointBelow < sz && corners[pointBelow].y == pos.y + 1 && corners[pointBelow].x < pos.x - 1; pointBelow++) { }
                for (int j = pointBelow; j < sz && corners[j].y == pos.y + 1 && corners[j].x <= pos.x + 1; j++)
                {
                    int x = corners[j].x;
                    if ((x == pos.x - 1 || x == pos.x || x == pos.x + 1) && scores[j] >= score) { suppressed = true; break; }
                }
                if (suppressed) continue;
            }
            retC.Add(corners[i]);
            retS.Add(scores[i]);
        }
        return (retC, retS);
    }
}

/// <summary>ImagePyramid of a frame's luma (8-bit layers; layer 0 of an 8-bit frame is the frame itself).</summary>
internal sealed class AomPyramid
{
    public struct Layer { public byte[] Buf; public int Offset, Width, Height, Stride; }
    public Layer[] Layers = Array.Empty<Layer>();
    public int MaxLevels, FilledLevels;
    public int[]? Corners;   // corner list (x, y pairs) once computed
    public int NumCorners;
    private const int PAD = 16;

    public static AomPyramid Create(AomFrameBuffer f)
    {
        int w = f.CropWidths[0], h = f.CropHeights[0];
        int msb = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Min(w, h));
        int n = Math.Max(msb - 3, 1);
        var p = new AomPyramid { MaxLevels = n, Layers = new Layer[n] };
        for (int level = f.Hbd ? 0 : 1; level < n; level++)
        {
            int lw = w >> level, lh = h >> level;
            int stride = (lw + 2 * PAD + 31) & ~31;
            p.Layers[level] = new Layer { Buf = new byte[stride * (lh + 2 * PAD)], Offset = PAD * stride + PAD, Width = lw, Height = lh, Stride = stride };
        }
        return p;
    }

    private static void FillBorder(Layer l)
    {
        var b = l.Buf;
        for (int row = 0; row < l.Height; row++)
        {
            int rs = l.Offset + row * l.Stride;
            byte left = b[rs], right = b[rs + l.Width - 1];
            Array.Fill(b, left, rs - PAD, PAD);
            Array.Fill(b, right, rs + l.Width, PAD);
        }
        for (int row = -PAD; row < 0; row++) Array.Copy(b, l.Offset - PAD, b, l.Offset + row * l.Stride - PAD, l.Width + 2 * PAD);
        int last = l.Offset + (l.Height - 1) * l.Stride;
        for (int row = l.Height; row < l.Height + PAD; row++) Array.Copy(b, last - PAD, b, l.Offset + row * l.Stride - PAD, l.Width + 2 * PAD);
    }

    private static void Down2Symeven(int[] input, int length, int[] output)
    {
        ReadOnlySpan<int> filter = stackalloc int[] { 56, 12, -3, -1 };
        int o = 0;
        for (int i = 0; i < length; i += 2)
        {
            int sum = 64;
            for (int j = 0; j < 4; ++j) sum += (input[Math.Max(i - j, 0)] + input[Math.Min(i + 1 + j, length - 1)]) * filter[j];
            sum >>= 7;
            output[o++] = Math.Clamp(sum, 0, 255);
        }
    }

    /// <summary>aom_compute_pyramid: fills up to n levels; returns the level count.</summary>
    public int Compute(AomFrameBuffer f, int bd, int nLevels)
    {
        nLevels = Math.Min(nLevels, MaxLevels);
        if (FilledLevels >= nLevels) return nLevels;
        if (FilledLevels == 0)
        {
            if (f.Hbd)
            {
                var l = Layers[0];
                for (int y = 0; y < l.Height; y++)
                    for (int x = 0; x < l.Width; x++)
                        l.Buf[l.Offset + y * l.Stride + x] = (byte)(f.Buffers16[0][f.Offsets[0] + y * f.Strides[0] + x] >> (bd - 8));
                FillBorder(l);
            }
            else Layers[0] = new Layer { Buf = f.Buffers[0], Offset = f.Offsets[0], Width = f.CropWidths[0], Height = f.CropHeights[0], Stride = f.Strides[0] };
            FilledLevels = 1;
        }
        for (int level = FilledLevels; level < nLevels; ++level)
        {
            var prev = Layers[level - 1];
            var cur = Layers[level];
            int inW = cur.Width << 1, inH = cur.Height << 1;
            // av1_resize_plane_to_half: rows then columns through down2_symeven
            var intbuf = new int[cur.Width * inH];
            var row = new int[inW];
            var rowOut = new int[cur.Width];
            for (int i = 0; i < inH; i++)
            {
                for (int x = 0; x < inW; x++) row[x] = prev.Buf[prev.Offset + i * prev.Stride + x];
                Down2Symeven(row, inW, rowOut);
                Array.Copy(rowOut, 0, intbuf, i * cur.Width, cur.Width);
            }
            var col = new int[inH];
            var colOut = new int[cur.Height];
            for (int c = 0; c < cur.Width; c++)
            {
                for (int i = 0; i < inH; i++) col[i] = intbuf[i * cur.Width + c];
                Down2Symeven(col, inH, colOut);
                for (int i = 0; i < cur.Height; i++) cur.Buf[cur.Offset + i * cur.Stride + c] = (byte)colOut[i];
            }
            FillBorder(cur);
        }
        FilledLevels = nLevels;
        return nLevels;
    }

    private static readonly ConditionalWeakTable<AomFrameBuffer, AomPyramid> Cache = new();

    /// <summary>The frame's pyramid (frame->y_pyramid), created on first use.</summary>
    public static AomPyramid Of(AomFrameBuffer f) => Cache.GetValue(f, Create);

    /// <summary>aom_invalidate_pyramid + av1_invalidate_corner_list.</summary>
    public static void Invalidate(AomFrameBuffer f)
    {
        if (Cache.TryGetValue(f, out var p)) { p.FilledLevels = 0; p.Corners = null; }
    }
}

internal static class AomGlobalMotion
{
    private const int DOWNSAMPLE_SHIFT = 3, DOWNSAMPLE_FACTOR = 8, FLOW_UPSCALE_TAPS = 4, PATCH = 8, PATCH_CENTER = 3;
    private const int FLOW_BORDER_INNER = (PATCH >> 1) >> DOWNSAMPLE_SHIFT, FLOW_BORDER_OUTER = FLOW_UPSCALE_TAPS / 2;
    private const int UPSAMPLE_CENTER_OFFSET = (DOWNSAMPLE_FACTOR - 1) / 2, INTERP_BITS = 14, DERIV_SCALE_LOG2 = 3, MAX_ITR = 4;
    private const int MAX_CORNERS = 4096, FAST_BARRIER = 18;

    private static readonly double[,] FlowUpscaleFilter =
    {
        { -3 / 128.0, 29 / 128.0, 111 / 128.0, -9 / 128.0 },
        { -9 / 128.0, 111 / 128.0, 29 / 128.0, -3 / 128.0 },
    };

    private static void CubicKernelDbl(double x, Span<double> k)
    {
        double x2 = x * x, x3 = x2 * x;
        k[0] = -0.5 * x + x2 - 0.5 * x3;
        k[1] = 1.0 - 2.5 * x2 + 1.5 * x3;
        k[2] = 0.5 * x + 2.0 * x2 - 1.5 * x3;
        k[3] = -0.5 * x2 + 0.5 * x3;
    }

    private static void CubicKernelInt(double x, Span<int> k)
    {
        Span<double> d = stackalloc double[4];
        CubicKernelDbl(x, d);
        for (int i = 0; i < 4; i++) k[i] = (int)Math.Round(d[i] * (1 << INTERP_BITS), MidpointRounding.ToEven);
    }

    /// <summary>av1_compute_corner_list (corners of pyramid level downsample_level).</summary>
    private static bool ComputeCornerList(AomFrameBuffer frame, AomPyramid pyr, int bd, int downsampleLevel)
    {
        if (pyr.Corners != null) return true;
        int layers = pyr.Compute(frame, bd, downsampleLevel + 1);
        downsampleLevel = layers - 1;
        var l = pyr.Layers[downsampleLevel];
        var (corners, scores) = AomFast9.DetectNonmax(l.Buf, l.Offset, l.Width, l.Height, l.Stride, FAST_BARRIER);
        int num = corners.Count;
        var outC = new List<int>();
        if (num <= MAX_CORNERS)
            foreach (var c in corners) { outC.Add(c.x * (1 << downsampleLevel)); outC.Add(c.y * (1 << downsampleLevel)); }
        else
        {
            var histogram = new int[256];
            for (int i = 0; i < num; i++) histogram[scores[i]] += 1;
            int threshold = -1, found = 0;
            for (int bucket = 255; bucket >= 0; bucket--)
            {
                if (found + histogram[bucket] > MAX_CORNERS) { threshold = bucket; break; }
                found += histogram[bucket];
            }
            for (int i = 0; i < num; i++)
                if (scores[i] > threshold) { outC.Add(corners[i].x * (1 << downsampleLevel)); outC.Add(corners[i].y * (1 << downsampleLevel)); }
        }
        pyr.Corners = outC.ToArray();
        pyr.NumCorners = outC.Count / 2;
        return true;
    }

    // ---- disflow ----
    private static void SobelFilter(byte[] src, int so, int ss, short[] dst, int dir)
    {
        Span<int> tmp = stackalloc int[PATCH * (PATCH + 2)];
        ReadOnlySpan<int> a = stackalloc int[] { 1, 0, -1 };
        ReadOnlySpan<int> b = stackalloc int[] { 1, 2, 1 };
        var h = dir != 0 ? a : b;
        var v = dir != 0 ? b : a;
        for (int y = -1; y < PATCH + 1; ++y)
            for (int x = 0; x < PATCH; ++x)
            {
                int sum = 0;
                for (int k = 0; k < 3; ++k) sum += h[k] * src[so + y * ss + (x + k - 1)];
                tmp[(y + 1) * PATCH + x] = (short)sum;
            }
        for (int y = 0; y < PATCH; ++y)
            for (int x = 0; x < PATCH; ++x)
            {
                int sum = 0;
                for (int k = 0; k < 3; ++k) sum += v[k] * tmp[(y + k) * PATCH + x];
                dst[y * PATCH + x] = (short)sum;
            }
    }

    private static void ComputeFlowVector(byte[] src, int srcBase, byte[] reff, int refBase, int width, int height, int stride, int x, int y,
        double u, double v, short[] dx, short[] dy, Span<int> bvec)
    {
        bvec[0] = bvec[1] = 0;
        int uInt = (int)Math.Floor(u), vInt = (int)Math.Floor(v);
        double uFrac = u - Math.Floor(u), vFrac = v - Math.Floor(v);
        Span<int> hk = stackalloc int[4];
        Span<int> vk = stackalloc int[4];
        CubicKernelInt(uFrac, hk);
        CubicKernelInt(vFrac, vk);
        Span<int> tmp = stackalloc int[PATCH * (PATCH + 3)];
        int x0 = Math.Clamp(x + uInt, -9, width), y0 = Math.Clamp(y + vInt, -9, height);
        for (int i = -1; i < PATCH + 2; ++i)
        {
            int rw = refBase + (y0 + i) * stride;
            for (int j = 0; j < PATCH; ++j)
            {
                int xw = x0 + j;
                int s = hk[0] * reff[rw + xw - 1] + hk[1] * reff[rw + xw] + hk[2] * reff[rw + xw + 1] + hk[3] * reff[rw + xw + 2];
                tmp[(i + 1) * PATCH + j] = (s + (1 << (INTERP_BITS - 6 - 1))) >> (INTERP_BITS - 6);
            }
        }
        const int roundBits = INTERP_BITS + 6 - DERIV_SCALE_LOG2;
        for (int i = 0; i < PATCH; ++i)
            for (int j = 0; j < PATCH; ++j)
            {
                int p = (i + 1) * PATCH + j;
                int result = vk[0] * tmp[p - PATCH] + vk[1] * tmp[p] + vk[2] * tmp[p + PATCH] + vk[3] * tmp[p + 2 * PATCH];
                int warped = (result + (1 << (roundBits - 1))) >> roundBits;
                int srcPx = src[srcBase + (x + j) + (y + i) * stride] << 3;
                int dt = warped - srcPx;
                bvec[0] += dx[i * PATCH + j] * dt;
                bvec[1] += dy[i * PATCH + j] * dt;
            }
    }

    /// <summary>aom_compute_flow_at_point.</summary>
    private static void ComputeFlowAtPoint(byte[] src, int srcBase, byte[] reff, int refBase, int x, int y, int width, int height, int stride,
        ref double u, ref double v)
    {
        var dx = new short[PATCH * PATCH];
        var dy = new short[PATCH * PATCH];
        int so = srcBase + y * stride + x;
        SobelFilter(src, so, stride, dx, 1);
        SobelFilter(src, so, stride, dy, 0);
        int t0 = 0, t1 = 0, t3 = 0;
        for (int i = 0; i < PATCH * PATCH; i++)
        {
            t0 += dx[i] * dx[i];
            t1 += dx[i] * dy[i];
            t3 += dy[i] * dy[i];
        }
        t0 += 1;
        t3 += 1;
        double m0 = t0, m1 = t1, m2 = t1, m3 = t3;
        double det = (m0 * m3) - (m1 * m2);
        double detInv = 1 / det;
        double i0 = m3 * detInv, i1 = -m1 * detInv, i2 = -m2 * detInv, i3 = m0 * detInv;
        Span<int> b = stackalloc int[2];
        for (int itr = 0; itr < MAX_ITR; itr++)
        {
            ComputeFlowVector(src, srcBase, reff, refBase, width, height, stride, x, y, u, v, dx, dy, b);
            double stepU = i0 * b[0] + i1 * b[1];
            double stepV = i2 * b[0] + i3 * b[1];
            u += Math.Clamp(stepU * 1.0, -2, 2);
            v += Math.Clamp(stepV * 1.0, -2, 2);
            if (Math.Abs(stepU) + Math.Abs(stepV) < 1.0 / 8.0) break;
        }
    }

    private sealed class FlowField
    {
        public double[] Buf = null!;
        public int U, V, Width, Height, Stride;
    }

    private static void FillFlowFieldBorders(double[] buf, int f, int width, int height, int stride)
    {
        int left = FLOW_BORDER_INNER, right = width - FLOW_BORDER_INNER - 1, top = FLOW_BORDER_INNER, bottom = height - FLOW_BORDER_INNER - 1;
        for (int i = top; i <= bottom; i++)
        {
            int row = f + i * stride;
            double l = buf[row + left];
            for (int j = -FLOW_BORDER_OUTER; j < left; j++) buf[row + j] = l;
        }
        for (int i = top; i <= bottom; i++)
        {
            int row = f + i * stride;
            double r = buf[row + right];
            for (int j = right + 1; j < width + FLOW_BORDER_OUTER; j++) buf[row + j] = r;
        }
        int topRow = f + top * stride - FLOW_BORDER_OUTER;
        for (int i = -FLOW_BORDER_OUTER; i < top; i++) Array.Copy(buf, topRow, buf, f + i * stride - FLOW_BORDER_OUTER, width + 2 * FLOW_BORDER_OUTER);
        int bottomRow = f + bottom * stride - FLOW_BORDER_OUTER;
        for (int i = bottom + 1; i < height + FLOW_BORDER_OUTER; i++)
            Array.Copy(buf, bottomRow, buf, f + i * stride - FLOW_BORDER_OUTER, width + 2 * FLOW_BORDER_OUTER);
    }

    private static void UpscaleFlowComponent(double[] flow, int f, int curWidth, int curHeight, int stride, double[] tmp, int t)
    {
        const int halfLen = FLOW_UPSCALE_TAPS / 2;
        for (int i = 0; i < curHeight; i++)
            for (int j = 0; j < curWidth; j++)
            {
                double left = 0;
                for (int k = -halfLen; k < halfLen; k++) left += flow[f + i * stride + (j + k)] * FlowUpscaleFilter[0, k + halfLen];
                tmp[t + i * stride + (2 * j)] = 2.0 * left;
                double right = 0;
                for (int k = -(halfLen - 1); k < halfLen + 1; k++) right += flow[f + i * stride + (j + k)] * FlowUpscaleFilter[1, k + (halfLen - 1)];
                tmp[t + i * stride + (2 * j + 1)] = 2.0 * right;
            }
        for (int i = -FLOW_BORDER_OUTER; i < 0; i++) Array.Copy(tmp, t, tmp, t + i * stride, 2 * curWidth);
        int bottomRow = t + (curHeight - 1) * stride;
        for (int i = curHeight; i < curHeight + FLOW_BORDER_OUTER; i++) Array.Copy(tmp, bottomRow, tmp, t + i * stride, 2 * curWidth);
        int upW = curWidth * 2;
        for (int i = 0; i < curHeight; i++)
            for (int j = 0; j < upW; j++)
            {
                double top = 0;
                for (int k = -halfLen; k < halfLen; k++) top += tmp[t + (i + k) * stride + j] * FlowUpscaleFilter[0, k + halfLen];
                flow[f + (2 * i) * stride + j] = top;
                double bottom = 0;
                for (int k = -(halfLen - 1); k < halfLen + 1; k++) bottom += tmp[t + (i + k) * stride + j] * FlowUpscaleFilter[1, k + (halfLen - 1)];
                flow[f + (2 * i + 1) * stride + j] = bottom;
            }
    }

    private static void ComputeFlowField(AomPyramid src, AomPyramid reff, int nLevels, FlowField flow)
    {
        double[]? tmp = null;
        int t = 0;
        if (nLevels >= 2)
        {
            int layer1Height = src.Layers[1].Height >> DOWNSAMPLE_SHIFT;
            tmp = new double[(layer1Height + 2 * FLOW_BORDER_OUTER) * flow.Stride];
            t = FLOW_BORDER_OUTER * flow.Stride;
        }
        var b = flow.Buf;
        for (int level = nLevels - 1; level >= 1; --level)
        {
            var cur = src.Layers[level];
            var rl = reff.Layers[level];
            int fw = cur.Width >> DOWNSAMPLE_SHIFT, fh = cur.Height >> DOWNSAMPLE_SHIFT, fs = flow.Stride;
            for (int i = FLOW_BORDER_INNER; i < fh - FLOW_BORDER_INNER; i++)
                for (int j = FLOW_BORDER_INNER; j < fw - FLOW_BORDER_INNER; j++)
                {
                    int idx = i * fs + j;
                    int tlx = (j << DOWNSAMPLE_SHIFT) + UPSAMPLE_CENTER_OFFSET - PATCH_CENTER;
                    int tly = (i << DOWNSAMPLE_SHIFT) + UPSAMPLE_CENTER_OFFSET - PATCH_CENTER;
                    double u = b[flow.U + idx], v = b[flow.V + idx];
                    ComputeFlowAtPoint(cur.Buf, cur.Offset, rl.Buf, rl.Offset, tlx, tly, cur.Width, cur.Height, cur.Stride, ref u, ref v);
                    b[flow.U + idx] = u;
                    b[flow.V + idx] = v;
                }
            FillFlowFieldBorders(b, flow.U, fw, fh, fs);
            FillFlowFieldBorders(b, flow.V, fw, fh, fs);
            int upW = fw << 1, upH = fh << 1;
            UpscaleFlowComponent(b, flow.U, fw, fh, fs, tmp!, t);
            UpscaleFlowComponent(b, flow.V, fw, fh, fs, tmp!, t);
            var next = src.Layers[level - 1];
            int nfw = next.Width >> DOWNSAMPLE_SHIFT, nfh = next.Height >> DOWNSAMPLE_SHIFT;
            if (nfw > upW)
                for (int i = 0; i < upH; i++)
                {
                    int index = i * fs + upW;
                    b[flow.U + index] = b[flow.U + index - 1];
                    b[flow.V + index] = b[flow.V + index - 1];
                }
            if (nfh > upH)
                for (int j = 0; j < nfw; j++)
                {
                    int index = upH * fs + j;
                    b[flow.U + index] = b[flow.U + index - fs];
                    b[flow.V + index] = b[flow.V + index - fs];
                }
        }
    }

    private struct Correspondence { public double X, Y, Rx, Ry; }

    private static double BicubicInterpOne(double[] arr, int p, int stride, ReadOnlySpan<double> hk, ReadOnlySpan<double> vk)
    {
        Span<double> tmp = stackalloc double[4];
        for (int i = -1; i < 3; ++i)
        {
            int q = p + i * stride - 1;
            tmp[i + 1] = hk[0] * arr[q] + hk[1] * arr[q + 1] + hk[2] * arr[q + 2] + hk[3] * arr[q + 3];
        }
        return vk[0] * tmp[0] + vk[1] * tmp[1] + vk[2] * tmp[2] + vk[3] * tmp[3];
    }

    /// <summary>av1_compute_global_motion_disflow (RANSAC_NUM_MOTIONS 1): false when no model was found.</summary>
    public static bool ComputeGlobalMotionDisflow(AomFrameBuffer src, AomFrameBuffer reff, int bd, int downsampleLevel, double[] outParams,
        out int[] inliers, out int numInliers)
    {
        inliers = Array.Empty<int>();
        numInliers = 0;
        var srcPyr = AomPyramid.Of(src);
        var refPyr = AomPyramid.Of(reff);
        int srcLayers = srcPyr.Compute(src, bd, 12);
        refPyr.Compute(reff, bd, 12);
        ComputeCornerList(src, srcPyr, bd, downsampleLevel);
        int w = srcPyr.Layers[0].Width, h = srcPyr.Layers[0].Height;
        var flow = new FlowField { Width = w >> DOWNSAMPLE_SHIFT, Height = h >> DOWNSAMPLE_SHIFT };
        flow.Stride = flow.Width + 2 * FLOW_BORDER_OUTER;
        int flowSize = flow.Stride * (flow.Height + 2 * FLOW_BORDER_OUTER);
        flow.Buf = new double[2 * flowSize];
        flow.U = FLOW_BORDER_OUTER * flow.Stride + FLOW_BORDER_OUTER;
        flow.V = flow.U + flowSize;
        ComputeFlowField(srcPyr, refPyr, srcLayers, flow);
        // determine_disflow_correspondence
        var corners = srcPyr.Corners!;
        var corr = new List<Correspondence>();
        Span<double> hk = stackalloc double[4];
        Span<double> vk = stackalloc double[4];
        var l0 = srcPyr.Layers[0];
        var r0 = refPyr.Layers[0];
        for (int i = 0; i < srcPyr.NumCorners; ++i)
        {
            int cx = corners[2 * i], cy = corners[2 * i + 1];
            int x = cx - UPSAMPLE_CENTER_OFFSET, y = cy - UPSAMPLE_CENTER_OFFSET;
            int fx = x >> DOWNSAMPLE_SHIFT, fy = y >> DOWNSAMPLE_SHIFT;
            double subX = (x & (DOWNSAMPLE_FACTOR - 1)) / (double)DOWNSAMPLE_FACTOR;
            double subY = (y & (DOWNSAMPLE_FACTOR - 1)) / (double)DOWNSAMPLE_FACTOR;
            if (fx < 1 || fx + 2 >= flow.Width) continue;
            if (fy < 1 || fy + 2 >= flow.Height) continue;
            CubicKernelDbl(subX, hk);
            CubicKernelDbl(subY, vk);
            double u = BicubicInterpOne(flow.Buf, flow.U + fy * flow.Stride + fx, flow.Stride, hk, vk);
            double v = BicubicInterpOne(flow.Buf, flow.V + fy * flow.Stride + fx, flow.Stride, hk, vk);
            ComputeFlowAtPoint(l0.Buf, l0.Offset, r0.Buf, r0.Offset, cx - PATCH_CENTER, cy - PATCH_CENTER, l0.Width, l0.Height, l0.Stride, ref u, ref v);
            corr.Add(new Correspondence { X = cx, Y = cy, Rx = cx + u, Ry = cy + v });
        }
        return Ransac(corr, outParams, out inliers, out numInliers);
    }

    // ---- ransac (ROTZOOM, one motion) ----
    private const double INLIER_THRESHOLD_SQUARED = 1.25 * 1.25;

    private static uint LcgNext(ref uint s) { s = (uint)(s * 1103515245UL + 12345); return s; }

    private static void LcgPick(int n, int k, Span<int> outIdx, ref uint seed)
    {
        for (int i = 0; i < k; i++)
        {
            int v;
        resample:
            v = (int)(((ulong)LcgNext(ref seed) * (uint)n) >> 32);
            for (int j = 0; j < i; j++) if (v == outIdx[j]) goto resample;
            outIdx[i] = v;
        }
    }

    private static bool Linsolve(int n, double[] a, int stride, double[] b, double[] x)
    {
        double c;
        for (int k = 0; k < n - 1; k++)
        {
            for (int i = n - 1; i > k; i--)
                if (Math.Abs(a[(i - 1) * stride + k]) < Math.Abs(a[i * stride + k]))
                {
                    for (int j = 0; j < n; j++) { c = a[i * stride + j]; a[i * stride + j] = a[(i - 1) * stride + j]; a[(i - 1) * stride + j] = c; }
                    c = b[i]; b[i] = b[i - 1]; b[i - 1] = c;
                }
            for (int i = k; i < n - 1; i++)
            {
                if (Math.Abs(a[k * stride + k]) < 1.0E-16) return false;
                c = a[(i + 1) * stride + k] / a[k * stride + k];
                for (int j = 0; j < n; j++) a[(i + 1) * stride + j] -= c * a[k * stride + j];
                b[i + 1] -= c * b[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (Math.Abs(a[i * stride + i]) < 1.0E-16) return false;
            c = 0;
            for (int j = i + 1; j <= n - 1; j++) c += a[i * stride + j] * x[j];
            x[i] = (b[i] - c) / a[i * stride + i];
        }
        return true;
    }

    private static void LsAccumulate(double[] mat, double[] y, ReadOnlySpan<double> a, double b)
    {
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) mat[i * 4 + j] += a[i] * a[j];
        for (int i = 0; i < 4; i++) y[i] += a[i] * b;
    }

    private static bool FindRotzoom(List<Correspondence> pts, ReadOnlySpan<int> indices, int num, double[] p)
    {
        var mat = new double[16];
        var y = new double[4];
        Span<double> a = stackalloc double[4];
        for (int i = 0; i < num; ++i)
        {
            var c = pts[indices[i]];
            a[0] = 1; a[1] = 0; a[2] = c.X; a[3] = c.Y;
            LsAccumulate(mat, y, a, c.Rx);
            a[0] = 0; a[1] = 1; a[2] = c.Y; a[3] = -c.X;
            LsAccumulate(mat, y, a, c.Ry);
        }
        var x = new double[4];
        if (!Linsolve(4, mat, 4, y, x)) return false;
        p[0] = x[0]; p[1] = x[1]; p[2] = x[2]; p[3] = x[3];
        p[4] = -p[3];
        p[5] = p[2];
        return true;
    }

    private sealed class Motion { public int NumInliers; public double Sse; public int[] Idx = null!; }

    private static void ScoreAffine(double[] mat, List<Correspondence> pts, Motion m)
    {
        m.NumInliers = 0;
        m.Sse = 0.0;
        for (int i = 0; i < pts.Count; ++i)
        {
            var c = pts[i];
            double px = mat[2] * c.X + mat[3] * c.Y + mat[0];
            double py = mat[4] * c.X + mat[5] * c.Y + mat[1];
            double dx = px - c.Rx, dy = py - c.Ry;
            double sse = dx * dx + dy * dy;
            if (sse < INLIER_THRESHOLD_SQUARED) { m.Idx[m.NumInliers++] = i; m.Sse += sse; }
        }
    }

    private static int CompareMotions(Motion a, Motion b)
    {
        if (a.NumInliers > b.NumInliers) return -1;
        if (a.NumInliers < b.NumInliers) return 1;
        if (a.Sse < b.Sse) return -1;
        if (a.Sse > b.Sse) return 1;
        return 0;
    }

    private static bool Ransac(List<Correspondence> pts, double[] outParams, out int[] inliers, out int numInliers)
    {
        const int minpts = 2;
        outParams[0] = 0; outParams[1] = 0; outParams[2] = 1; outParams[3] = 0; outParams[4] = 0; outParams[5] = 1;
        inliers = Array.Empty<int>();
        numInliers = 0;
        int npoints = pts.Count;
        if (npoints < minpts * 5 || npoints == 0) return false;
        int minInliers = Math.Max((int)(0.1 * npoints), minpts);
        uint seed = (uint)npoints;
        Span<int> indices = stackalloc int[4];
        var paramsThis = new double[6];
        var motion = new Motion { Idx = new int[npoints] };
        var worst = motion;   // motions[0] (one desired motion)
        var cur = new Motion { Idx = new int[npoints] };
        for (int trial = 0; trial < 20; trial++)
        {
            LcgPick(npoints, minpts, indices, ref seed);
            if (!FindRotzoom(pts, indices, minpts, paramsThis)) continue;
            ScoreAffine(paramsThis, pts, cur);
            if (cur.NumInliers < minInliers) continue;
            if (CompareMotions(cur, worst) < 0)
            {
                worst.NumInliers = cur.NumInliers;
                worst.Sse = cur.Sse;
                (worst.Idx, cur.Idx) = (cur.Idx, worst.Idx);
            }
        }
        if (motion.NumInliers <= 0) return true;
        for (int refine = 0; refine < 5; refine++)
        {
            if (!FindRotzoom(pts, motion.Idx, motion.NumInliers, paramsThis)) return true;   // bad model: identity, no inliers
            ScoreAffine(paramsThis, pts, cur);
            if (cur.NumInliers > motion.NumInliers)
            {
                motion.NumInliers = cur.NumInliers;
                motion.Sse = cur.Sse;
                (motion.Idx, cur.Idx) = (cur.Idx, motion.Idx);
            }
            else break;
        }
        Array.Copy(paramsThis, outParams, 6);
        inliers = new int[2 * motion.NumInliers];
        for (int j = 0; j < motion.NumInliers; j++)
        {
            var c = pts[motion.Idx[j]];
            inliers[2 * j] = (int)Math.Round(c.X, MidpointRounding.ToEven);
            inliers[2 * j + 1] = (int)Math.Round(c.Y, MidpointRounding.ToEven);
        }
        numInliers = motion.NumInliers;
        return true;
    }

    // ---- av1/encoder/global_motion.c ----
    private const int GM_TRANS_PREC_BITS = 6, GM_ALPHA_PREC_BITS = 15, GM_TRANS_DECODE_FACTOR = 1 << (WARPEDMODEL_PREC_BITS - 6),
        GM_ALPHA_DECODE_FACTOR = 1 << (WARPEDMODEL_PREC_BITS - 15), GM_TRANS_MAX = 1 << 12, GM_ALPHA_MAX = 1 << 12,
        GM_TRANS_PREC_DIFF = WARPEDMODEL_PREC_BITS - 6, GM_ALPHA_PREC_DIFF = WARPEDMODEL_PREC_BITS - 15;
    internal const int WARP_ERROR_BLOCK = 32, WARP_ERROR_BLOCK_LOG = 5;
    internal static readonly double[] ErroradvTr = { 0.65, 0.2 };
    private const double ErroradvProdTr = 20000, ErroradvEarlyTr = 0.70;

    /// <summary>av1_convert_model_to_params.</summary>
    public static void ConvertModelToParams(double[] p, AomWarpedMotionParams wm)
    {
        var m = wm.WmMat;
        m[0] = (int)Math.Floor(p[0] * (1 << GM_TRANS_PREC_BITS) + 0.5);
        m[1] = (int)Math.Floor(p[1] * (1 << GM_TRANS_PREC_BITS) + 0.5);
        m[0] = Math.Clamp(m[0], -GM_TRANS_MAX, GM_TRANS_MAX) * GM_TRANS_DECODE_FACTOR;
        m[1] = Math.Clamp(m[1], -GM_TRANS_MAX, GM_TRANS_MAX) * GM_TRANS_DECODE_FACTOR;
        for (int i = 2; i < 6; ++i)
        {
            int diag = (i == 2 || i == 5) ? (1 << GM_ALPHA_PREC_BITS) : 0;
            m[i] = (int)Math.Floor(p[i] * (1 << GM_ALPHA_PREC_BITS) + 0.5);
            m[i] = Math.Clamp(m[i] - diag, -GM_ALPHA_MAX, GM_ALPHA_MAX);
            m[i] = (m[i] + diag) * GM_ALPHA_DECODE_FACTOR;
        }
        wm.WmType = GetWmtype(wm);
        wm.Invalid = false;
    }

    /// <summary>get_wmtype.</summary>
    public static int GetWmtype(AomWarpedMotionParams gm)
    {
        var m = gm.WmMat;
        if (m[5] == (1 << WARPEDMODEL_PREC_BITS) && m[4] == 0 && m[2] == (1 << WARPEDMODEL_PREC_BITS) && m[3] == 0)
            return (m[1] == 0 && m[0] == 0) ? IDENTITY : TRANSLATION;
        if (m[2] == m[5] && m[3] == -m[4]) return ROTZOOM;
        return AFFINE;
    }

    private static int AddParamOffset(int index, int value, int offset)
    {
        int scale = index < 2 ? GM_TRANS_PREC_DIFF : GM_ALPHA_PREC_DIFF;
        int clampV = index < 2 ? GM_TRANS_MAX : GM_ALPHA_MAX;
        int oneCentered = (index == 2 || index == 5) ? 1 : 0;
        value = (value - (oneCentered << WARPEDMODEL_PREC_BITS)) >> scale;
        value += offset;
        value = Math.Clamp(value, -clampV, clampV);
        value *= 1 << scale;
        return value + (oneCentered << WARPEDMODEL_PREC_BITS);
    }

    private static void ForceWmtype(AomWarpedMotionParams wm, int wmtype)
    {
        var m = wm.WmMat;
        switch (wmtype)
        {
            case IDENTITY: m[0] = 0; m[1] = 0; goto case TRANSLATION;
            case TRANSLATION: m[2] = 1 << WARPEDMODEL_PREC_BITS; m[3] = 0; goto case ROTZOOM;
            case ROTZOOM: m[4] = -m[3]; m[5] = m[2]; break;
        }
        wm.WmType = wmtype;
    }

    private static long SadBlock(byte[]? a8, ushort[]? a16, int ao, int astr, byte[]? b8, ushort[]? b16, int bo, int bstr, int w, int h)
    {
        long sad = 0;
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
            {
                int x = a16 != null ? a16[ao + i * astr + j] : a8![ao + i * astr + j];
                int y = b16 != null ? b16[bo + i * bstr + j] : b8![bo + i * bstr + j];
                sad += Math.Abs(x - y);
            }
        return sad;
    }

    /// <summary>av1_segmented_frame_error.</summary>
    public static long SegmentedFrameError(AomFrameBuffer reff, AomFrameBuffer dst, int pWidth, int pHeight, byte[] segMap, int segStride)
    {
        int ebw = Math.Min(pWidth, WARP_ERROR_BLOCK), ebh = Math.Min(pHeight, WARP_ERROR_BLOCK);
        long sum = 0;
        bool hbd = reff.Hbd;
        for (int i = 0; i < pHeight; i += WARP_ERROR_BLOCK)
            for (int j = 0; j < pWidth; j += WARP_ERROR_BLOCK)
            {
                if (segMap[(i >> WARP_ERROR_BLOCK_LOG) * segStride + (j >> WARP_ERROR_BLOCK_LOG)] == 0) continue;
                int pw = Math.Min(ebw, pWidth - j), ph = Math.Min(ebh, pHeight - i);
                sum += SadBlock(hbd ? null : reff.Buffers[0], hbd ? reff.Buffers16[0] : null, reff.Offsets[0] + j + i * reff.Strides[0], reff.Strides[0],
                    hbd ? null : dst.Buffers[0], hbd ? dst.Buffers16[0] : null, dst.Offsets[0] + j + i * dst.Strides[0], dst.Strides[0], pw, ph);
            }
        return sum;
    }

    private static long WarpError(AomWarpedMotionParams wm, AomFrameBuffer reff, AomFrameBuffer dst, int pWidth, int pHeight, int bd,
        long bestError, byte[] segMap, int segStride)
    {
        if (!AomWarp.GetShearParams(wm)) return long.MaxValue;
        long sum = 0;
        int ebw = Math.Min(pWidth, WARP_ERROR_BLOCK), ebh = Math.Min(pHeight, WARP_ERROR_BLOCK);
        bool hbd = reff.Hbd;
        var tmp8 = hbd ? null : new byte[WARP_ERROR_BLOCK * WARP_ERROR_BLOCK];
        var tmp16 = hbd ? new ushort[WARP_ERROR_BLOCK * WARP_ERROR_BLOCK] : null;
        var cp = AomConvParams.NoRound(0, 0, null, 0, false, bd);
        int rw = reff.CropWidths[0], rh = reff.CropHeights[0];
        for (int i = 0; i < pHeight; i += WARP_ERROR_BLOCK)
            for (int j = 0; j < pWidth; j += WARP_ERROR_BLOCK)
            {
                if (segMap[(i >> WARP_ERROR_BLOCK_LOG) * segStride + (j >> WARP_ERROR_BLOCK_LOG)] == 0) continue;
                int ww = Math.Min(ebw, rw - j), wh = Math.Min(ebh, rh - i);
                AomWarp.WarpAffine(wm.WmMat, hbd ? null : reff.Buffers[0], hbd ? reff.Buffers16[0] : null, reff.Offsets[0], rw, rh, reff.Strides[0],
                    tmp8, tmp16, 0, j, i, ww, wh, WARP_ERROR_BLOCK, 0, 0, bd, cp, wm.Alpha, wm.Beta, wm.Gamma, wm.Delta);
                sum += SadBlock(tmp8, tmp16, 0, WARP_ERROR_BLOCK, hbd ? null : dst.Buffers[0], hbd ? dst.Buffers16[0] : null,
                    dst.Offsets[0] + j + i * dst.Strides[0], dst.Strides[0], ww, wh);
                if (sum > bestError) return long.MaxValue;
            }
        return sum;
    }

    /// <summary>av1_refine_integerized_param.</summary>
    public static long RefineIntegerizedParam(AomWarpedMotionParams wm, int wmtype, int bd, AomFrameBuffer reff, AomFrameBuffer dst,
        int nRefinements, long refFrameError, byte[] segMap, int segStride, double gmErroradvTr)
    {
        int nParams = new[] { 0, 2, 4, 6 }[wmtype];
        var pm = wm.WmMat;
        int dW = dst.CropWidths[0], dH = dst.CropHeights[0];
        ForceWmtype(wm, wmtype);
        wm.WmType = GetWmtype(wm);
        if (nRefinements == 0)
        {
            long thr = (long)Math.Round(refFrameError * gmErroradvTr, MidpointRounding.ToEven);
            return WarpError(wm, reff, dst, dW, dH, bd, thr, segMap, segStride);
        }
        long selThr = (long)Math.Round(refFrameError * ErroradvEarlyTr, MidpointRounding.ToEven);
        long bestError = WarpError(wm, reff, dst, dW, dH, bd, selThr, segMap, segStride);
        if (bestError > selThr) return long.MaxValue;
        int step = 1 << (nRefinements - 1);
        for (int i = 0; i < nRefinements; i++, step >>= 1)
            for (int p = 0; p < nParams; ++p)
            {
                int stepDir = 0;
                int currParam = pm[p], bestParam = currParam;
                pm[p] = AddParamOffset(p, currParam, -step);
                ForceWmtype(wm, wmtype);
                long e = WarpError(wm, reff, dst, dW, dH, bd, bestError, segMap, segStride);
                if (e < bestError) { bestError = e; bestParam = pm[p]; stepDir = -1; }
                pm[p] = AddParamOffset(p, currParam, step);
                ForceWmtype(wm, wmtype);
                e = WarpError(wm, reff, dst, dW, dH, bd, bestError, segMap, segStride);
                if (e < bestError) { bestError = e; bestParam = pm[p]; stepDir = 1; }
                while (stepDir != 0)
                {
                    pm[p] = AddParamOffset(p, bestParam, step * stepDir);
                    ForceWmtype(wm, wmtype);
                    e = WarpError(wm, reff, dst, dW, dH, bd, bestError, segMap, segStride);
                    if (e < bestError) { bestError = e; bestParam = pm[p]; }
                    else stepDir = 0;
                }
                pm[p] = bestParam;
                ForceWmtype(wm, wmtype);
            }
        wm.WmType = GetWmtype(wm);
        AomWarp.GetShearParams(wm);
        return bestError;
    }

    /// <summary>av1_compute_feature_segmentation_map.</summary>
    public static void ComputeFeatureSegmentationMap(byte[] segMap, int width, int height, int[] inliers, int numInliers)
    {
        int segCount = 0;
        Array.Clear(segMap, 0, width * height);
        for (int i = 0; i < numInliers; i++)
        {
            int sx = inliers[i * 2] >> WARP_ERROR_BLOCK_LOG, sy = inliers[i * 2 + 1] >> WARP_ERROR_BLOCK_LOG;
            segMap[sy * width + sx] += 1;
        }
        for (int i = 0; i < width * height; i++)
        {
            segMap[i] = (byte)(segMap[i] >= 3 ? 1 : 0);
            segCount += segMap[i];
        }
        if (segCount < 48) Array.Fill(segMap, (byte)1, 0, width * height);
    }

    public static bool IsEnoughErroradvantage(double bestErroradvantage, int paramsCost, double tr)
        => bestErroradvantage < tr && bestErroradvantage * paramsCost < ErroradvProdTr;

    private static int CountPrimitiveRefsubexpfin(int n, int k, int r, int v) => AomPickRst.CountRefSubexpfin(n, k, r, v);

    private static int CountSigned(int n, int k, int r, int v)
    {
        r += n - 1;
        v += n - 1;
        return CountPrimitiveRefsubexpfin((n << 1) - 1, k, r, v);
    }

    /// <summary>gm_get_params_cost.</summary>
    public static int GmGetParamsCost(AomWarpedMotionParams gm, AomWarpedMotionParams refGm, bool allowHp)
    {
        const int SUBEXPFIN_K = 3, GM_ABS_TRANS_ONLY_BITS = 12 - 6 + 3, GM_ABS_TRANS_BITS = 12, GM_TRANS_ONLY_PREC_DIFF = WARPEDMODEL_PREC_BITS - 3;
        int cost = 0;
        var g = gm.WmMat;
        var r = refGm.WmMat;
        if (gm.WmType >= ROTZOOM)
        {
            cost += CountSigned(GM_ALPHA_MAX + 1, SUBEXPFIN_K, (r[2] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS), (g[2] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS));
            cost += CountSigned(GM_ALPHA_MAX + 1, SUBEXPFIN_K, r[3] >> GM_ALPHA_PREC_DIFF, g[3] >> GM_ALPHA_PREC_DIFF);
            if (gm.WmType >= AFFINE)
            {
                cost += CountSigned(GM_ALPHA_MAX + 1, SUBEXPFIN_K, r[4] >> GM_ALPHA_PREC_DIFF, g[4] >> GM_ALPHA_PREC_DIFF);
                cost += CountSigned(GM_ALPHA_MAX + 1, SUBEXPFIN_K, (r[5] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS), (g[5] >> GM_ALPHA_PREC_DIFF) - (1 << GM_ALPHA_PREC_BITS));
            }
        }
        if (gm.WmType >= TRANSLATION)
        {
            int transBits = gm.WmType == TRANSLATION ? GM_ABS_TRANS_ONLY_BITS - (allowHp ? 0 : 1) : GM_ABS_TRANS_BITS;
            int transPrecDiff = gm.WmType == TRANSLATION ? GM_TRANS_ONLY_PREC_DIFF + (allowHp ? 0 : 1) : GM_TRANS_PREC_DIFF;
            cost += CountSigned((1 << transBits) + 1, SUBEXPFIN_K, r[0] >> transPrecDiff, g[0] >> transPrecDiff);
            cost += CountSigned((1 << transBits) + 1, SUBEXPFIN_K, r[1] >> transPrecDiff, g[1] >> transPrecDiff);
        }
        return cost << 9;   // AV1_PROB_COST_SHIFT
    }
}
