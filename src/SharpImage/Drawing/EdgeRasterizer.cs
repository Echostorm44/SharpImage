// Scanline edge-based polygon rasterizer with sub-pixel anti-aliasing.
// Converts polygon outlines to filled/stroked pixels on an ImageFrame.

using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Drawing;

/// <summary>
/// Rasterizes polygon outlines onto an image using scanline edge analysis. Supports anti-aliased fill and stroke with
/// configurable fill rules.
/// </summary>
public static class EdgeRasterizer
{
    /// <summary>
    /// Fill a polygon defined by points onto the image.
    /// </summary>
    public static void FillPolygon(ImageFrame image, ReadOnlySpan<PointD> points, DrawingState state)
    {
        if (points.Length < 3)
        {
            return;
        }

        var edges = BuildEdges(points);
        if (edges.Count == 0)
        {
            return;
        }

        RasterizeEdges(image, edges, state, state.FillColor);
    }

    /// <summary>
    /// Stroke (outline) a polygon/polyline defined by points onto the image.
    /// </summary>
    public static void StrokePolyline(ImageFrame image, ReadOnlySpan<PointD> points, DrawingState state, bool closed)
    {
        if (points.Length < 2)
        {
            return;
        }

        if (state.StrokeWidth <= 0)
        {
            return;
        }

        var strokePoly = ExpandStroke(points, state, closed);
        if (strokePoly.Length < 3)
        {
            return;
        }

        var edges = BuildEdges(strokePoly.AsSpan());
        if (edges.Count == 0)
        {
            return;
        }

        RasterizeEdges(image, edges, state, state.StrokeColor);
    }

    /// <summary>
    /// Fill AND stroke a polygon.
    /// </summary>
    public static void FillAndStrokePolygon(ImageFrame image, ReadOnlySpan<PointD> points, DrawingState state)
    {
        FillPolygon(image, points, state);
        if (state.StrokeColor.A > 0 && state.StrokeWidth > 0)
        {
            StrokePolyline(image, points, state, closed: true);
        }
    }

    #region Edge building

    private readonly struct Edge
    {
        public readonly double YMin, YMax;
        public readonly double XAtYMin;
        public readonly double InverseSlope;
        public readonly int Direction;

        public Edge(PointD from, PointD to)
        {
            bool ascending = from.Y < to.Y;
            PointD low = ascending ? from : to;
            PointD high = ascending ? to : from;
            YMin = low.Y;
            YMax = high.Y;
            XAtYMin = low.X;
            double dy = high.Y - low.Y;
            InverseSlope = dy > 1e-10 ? (high.X - low.X) / dy : 0;
            Direction = ascending ? 1 : -1;
        }
    }

    private static List<Edge> BuildEdges(ReadOnlySpan<PointD> points)
    {
        var edges = new List<Edge>(points.Length);
        for (int i = 0;i < points.Length;i++)
        {
            int next = (i + 1) % points.Length;
            double dy = Math.Abs(points[i].Y - points[next].Y);
            if (dy < 1e-10)
            {
                continue;
            }

            edges.Add(new Edge(points[i], points[next]));
        }
        edges.Sort((a, b) => a.YMin.CompareTo(b.YMin));
        return edges;
    }

    #endregion

    #region Rasterization

    private static void RasterizeEdges(ImageFrame image, List<Edge> edges, DrawingState state, DrawColor color)
    {
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var e in edges)
        {
            if (e.YMin < yMin)
            {
                yMin = e.YMin;
            }

            if (e.YMax > yMax)
            {
                yMax = e.YMax;
            }
        }

        int scanStart = Math.Max(0, (int)Math.Floor(yMin));
        int scanEnd = Math.Min((int)image.Rows - 1, (int)Math.Ceiling(yMax));
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        var activeEdges = new List<(Edge edge, double xCurrent)>();
        double colorOpacity = state.Opacity * (color.A / 255.0);
        bool antialias = state.Antialias;
        FillRule fillRule = state.FillRule;

        // Pre-convert color to quantum
        ushort srcR = Quantum.ScaleFromByte(color.R);
        ushort srcG = Quantum.ScaleFromByte(color.G);
        ushort srcB = Quantum.ScaleFromByte(color.B);

        for (int y = scanStart;y <= scanEnd;y++)
        {
            double scanY = y + 0.5;

            // Remove expired edges
            for (int i = activeEdges.Count - 1;i >= 0;i--)
            {
                if (activeEdges[i].edge.YMax <= scanY)
                {
                    activeEdges.RemoveAt(i);
                }
            }

            // Add new active edges
            foreach (var e in edges)
            {
                if (e.YMin <= scanY && e.YMax > scanY)
                {
                    bool alreadyActive = false;
                    foreach (var ae in activeEdges)
                    {
                        if (ae.edge.YMin == e.YMin && ae.edge.XAtYMin == e.XAtYMin &&
                                                ae.edge.YMax == e.YMax)
                        {
                            alreadyActive = true;
                            break;
                        }
                    }

                    if (!alreadyActive)
                    {
                        double x = e.XAtYMin + (scanY - e.YMin) * e.InverseSlope;
                        activeEdges.Add((e, x));
                    }
                }
            }

            // Update x positions
            for (int i = 0;i < activeEdges.Count;i++)
            {
                var ae = activeEdges[i];
                double x = ae.edge.XAtYMin + (scanY - ae.edge.YMin) * ae.edge.InverseSlope;
                activeEdges[i] = (ae.edge, x);
            }

            activeEdges.Sort((a, b) => a.xCurrent.CompareTo(b.xCurrent));

            var spans = ComputeSpans(activeEdges, fillRule);

            var row = image.GetPixelRowForWrite(y);

            foreach (var (xStart, xEnd) in spans)
            {
                int x0 = Math.Max(0, (int)Math.Floor(xStart));
                int x1 = Math.Min((int)image.Columns - 1, (int)Math.Ceiling(xEnd));

                for (int x = x0;x <= x1;x++)
                {
                    double coverage = 1.0;
                    if (antialias)
                    {
                        if (x < xStart)
                        {
                            coverage *= Math.Clamp(1.0 - (xStart - x), 0, 1);
                        }

                        if (x + 1 > xEnd)
                        {
                            coverage *= Math.Clamp(xEnd - x, 0, 1);
                        }
                    }

                    double alpha = coverage * colorOpacity;
                    if (alpha < 1.0 / 65535.0)
                    {
                        continue;
                    }

                    int offset = x * channels;
                    CompositePixelQuantum(row, offset, channels, hasAlpha,
                        srcR, srcG, srcB, alpha);
                }
            }
        }
    }

    private static List<(double xStart, double xEnd)> ComputeSpans(
        List<(Edge edge, double xCurrent)> activeEdges, FillRule fillRule)
    {
        var spans = new List<(double, double)>();

        if (fillRule == FillRule.EvenOdd)
        {
            for (int i = 0;i + 1 < activeEdges.Count;i += 2)
            {
                spans.Add((activeEdges[i].xCurrent, activeEdges[i + 1].xCurrent));
            }
        }
        else
        {
            int winding = 0;
            double spanStart = 0;
            for (int i = 0;i < activeEdges.Count;i++)
            {
                int prevWinding = winding;
                winding += activeEdges[i].edge.Direction;

                if (prevWinding == 0 && winding != 0)
                {
                    spanStart = activeEdges[i].xCurrent;
                }
                else if (prevWinding != 0 && winding == 0)
                {
                    spans.Add((spanStart, activeEdges[i].xCurrent));
                }
            }
        }

        return spans;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CompositePixelQuantum(Span<ushort> row, int offset, int channels, bool hasAlpha,
        ushort srcR, ushort srcG, ushort srcB, double alpha)
    {
        ushort srcA = (ushort)(alpha * Quantum.MaxValue);
        double srcANorm = alpha;
        double invSrcA = 1.0 - srcANorm;

        // Red
        row[offset] = (ushort)Math.Clamp(srcR * srcANorm + row[offset] * invSrcA, 0, Quantum.MaxValue);
        // Green
        if (channels > 1)
        {
            row[offset + 1] = (ushort)Math.Clamp(srcG * srcANorm + row[offset + 1] * invSrcA, 0, Quantum.MaxValue);
        }
        // Blue
        if (channels > 2)
        {
            row[offset + 2] = (ushort)Math.Clamp(srcB * srcANorm + row[offset + 2] * invSrcA, 0, Quantum.MaxValue);
        }
        // Alpha
        if (hasAlpha)
        {
            int alphaIdx = offset + channels - 1;
            row[alphaIdx] = (ushort)Math.Clamp(srcA + row[alphaIdx] * invSrcA, 0, Quantum.MaxValue);
        }
    }

    #endregion

    #region Stroke expansion

    private static PointD[] ExpandStroke(ReadOnlySpan<PointD> points, DrawingState state, bool closed)
    {
        double halfWidth = state.StrokeWidth / 2.0;
        int count = points.Length;
        if (count < 2)
        {
            return [];
        }

        var normals = new PointD[count - 1];
        for (int i = 0;i < count - 1;i++)
        {
            double dx = points[i + 1].X - points[i].X;
            double dy = points[i + 1].Y - points[i].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-10)
            {
                normals[i] = new PointD(0, 1);
                continue;
            }
            normals[i] = new PointD(-dy / len, dx / len);
        }

        var left = new List<PointD>(count);
        var right = new List<PointD>(count);

        for (int i = 0;i < count;i++)
        {
            PointD normal;
            if (i == 0)
            {
                normal = normals[0];
            }
            else if (i == count - 1)
            {
                normal = normals[count - 2];
            }
            else
            {
                normal = new PointD(
                    (normals[i - 1].X + normals[i].X) * 0.5,
                    (normals[i - 1].Y + normals[i].Y) * 0.5);
                double len = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
                if (len > 1e-10)
                {
                    normal.X /= len;
                    normal.Y /= len;
                }
            }

            left.Add(new PointD(points[i].X + normal.X * halfWidth, points[i].Y + normal.Y * halfWidth));
            right.Add(new PointD(points[i].X - normal.X * halfWidth, points[i].Y - normal.Y * halfWidth));
        }

        var result = new PointD[left.Count + right.Count];
        for (int i = 0;i < left.Count;i++)
        {
            result[i] = left[i];
        }

        for (int i = 0;i < right.Count;i++)
        {
            result[left.Count + i] = right[right.Count - 1 - i];
        }

        return result;
    }

    #endregion
}
