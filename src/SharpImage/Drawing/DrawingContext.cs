// Drawing context with state stack, transforms, and high-level drawing API.
// This is the primary public API for vector drawing on ImageFrame.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Drawing;

/// <summary>
/// A stateful drawing context that renders vector primitives onto an ImageFrame. Supports push/pop state stack, affine
/// transforms, fill/stroke, and SVG-like paths.
/// </summary>
public sealed class DrawingContext
{
    private readonly ImageFrame image;
    private readonly Stack<DrawingState> stateStack = new();
    private DrawingState current;

    public DrawingContext(ImageFrame image)
    {
        this.image = image;
        current = new DrawingState();
    }

    #region State management

    public void PushState() => stateStack.Push(current.Clone());

    public void PopState()
    {
        if (stateStack.Count > 0)
        {
            current = stateStack.Pop();
        }
    }

    public int StateDepth => stateStack.Count;

    #endregion

    #region State setters

    public DrawColor FillColor
    {
        get => current.FillColor;
        set => current.FillColor = value;
    }
    public DrawColor StrokeColor
    {
        get => current.StrokeColor;
        set => current.StrokeColor = value;
    }
    public double StrokeWidth
    {
        get => current.StrokeWidth;
        set => current.StrokeWidth = value;
    }
    public FillRule FillRule
    {
        get => current.FillRule;
        set => current.FillRule = value;
    }
    public LineCap LineCap
    {
        get => current.LineCap;
        set => current.LineCap = value;
    }
    public LineJoin LineJoin
    {
        get => current.LineJoin;
        set => current.LineJoin = value;
    }
    public double MiterLimit
    {
        get => current.MiterLimit;
        set => current.MiterLimit = value;
    }
    public bool Antialias
    {
        get => current.Antialias;
        set => current.Antialias = value;
    }
    public double Opacity
    {
        get => current.Opacity;
        set => current.Opacity = value;
    }

    public void SetStrokeDashPattern(double[] pattern, double offset = 0)
    {
        current.StrokeDashPattern = (double[])pattern.Clone();
        current.StrokeDashOffset = offset;
    }

    #endregion

    #region Transforms

    public void Translate(double tx, double ty)
        => current.Transform = AffineMatrix.CreateTranslation(tx, ty).Compose(current.Transform);

    public void Rotate(double degrees)
        => current.Transform = AffineMatrix.CreateRotation(degrees * Math.PI / 180.0)
            .Compose(current.Transform);

    public void Scale(double sx, double sy)
        => current.Transform = AffineMatrix.CreateScale(sx, sy).Compose(current.Transform);

    public void ResetTransform()
        => current.Transform = AffineMatrix.Identity;

    #endregion

    #region Drawing primitives

    public void DrawPoint(double x, double y)
    {
        var pts = TransformPoints([ new PointD(x, y) ]);
        int px = (int)Math.Round(pts[0].X);
        int py = (int)Math.Round(pts[0].Y);
        if (px >= 0 && px < (int)image.Columns && py >= 0 && py < (int)image.Rows)
        {
            var pixel = new PixelInfo
            {
                Red = Quantum.ScaleFromByte(current.FillColor.R),
                Green = Quantum.ScaleFromByte(current.FillColor.G),
                Blue = Quantum.ScaleFromByte(current.FillColor.B),
                Alpha = Quantum.ScaleFromByte(current.FillColor.A)
            };
            image.SetPixel(px, py, pixel);
        }
    }

    public void DrawLine(double x1, double y1, double x2, double y2)
    {
        var pts = TransformPoints([ new PointD(x1, y1), new PointD(x2, y2) ]);
        var strokeState = current.Clone();
        strokeState.StrokeColor = current.StrokeColor.A > 0 ? current.StrokeColor : current.FillColor;
        EdgeRasterizer.StrokePolyline(image, pts, strokeState, closed: false);
    }

    public void DrawRectangle(double x, double y, double width, double height)
    {
        var pts = TransformPoints(PathParser.Rectangle(x, y, width, height));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawRoundedRectangle(double x, double y, double width, double height,
        double rx, double ry)
    {
        var pts = TransformPoints(PathParser.RoundedRectangle(x, y, width, height, rx, ry));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawCircle(double cx, double cy, double radius)
    {
        var pts = TransformPoints(PathParser.Circle(cx, cy, radius));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawEllipse(double cx, double cy, double rx, double ry)
    {
        var pts = TransformPoints(PathParser.Ellipse(cx, cy, rx, ry));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawArc(double cx, double cy, double rx, double ry,
        double startDeg, double endDeg)
    {
        var pts = TransformPoints(PathParser.Arc(cx, cy, rx, ry, startDeg, endDeg));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawPolygon(ReadOnlySpan<PointD> points)
    {
        var pts = TransformPoints(points);
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawPolyline(ReadOnlySpan<PointD> points)
    {
        var pts = TransformPoints(points);
        EdgeRasterizer.StrokePolyline(image, pts, current, closed: false);
    }

    public void DrawRegularPolygon(double cx, double cy, double radius, int sides)
    {
        var pts = TransformPoints(PathParser.RegularPolygon(cx, cy, radius, sides));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    public void DrawStar(double cx, double cy, double outerRadius, double innerRadius, int points)
    {
        var pts = TransformPoints(PathParser.Star(cx, cy, outerRadius, innerRadius, points));
        EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
    }

    #endregion

    #region SVG Path

    public void DrawPath(string pathData)
    {
        var subPaths = PathParser.Parse(pathData);
        foreach (var (pathPoints, closed) in subPaths)
        {
            var pts = TransformPoints(pathPoints);
            if (closed)
            {
                EdgeRasterizer.FillAndStrokePolygon(image, pts, current);
            }
            else if (pts.Length >= 2)
            {
                EdgeRasterizer.StrokePolyline(image, pts, current, closed: false);
            }
        }
    }

    #endregion

    #region Text

    /// <summary>
    /// Draw text at the specified position using the built-in bitmap font.
    /// </summary>
    public void DrawText(double x, double y, string text, double scale = 1.0)
    {
        var glyphs = BitmapFont.RenderString(text, scale);
        var transform = current.Transform;
        ushort fillR = Quantum.ScaleFromByte(current.FillColor.R);
        ushort fillG = Quantum.ScaleFromByte(current.FillColor.G);
        ushort fillB = Quantum.ScaleFromByte(current.FillColor.B);
        double fillOpacity = current.Opacity * (current.FillColor.A / 255.0);
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        foreach (var (gx, gy) in glyphs)
        {
            double wx = x + gx, wy = y + gy;
            var worldPt = new PointD(transform.Sx * wx + transform.Ry * wy + transform.Tx,
                                     transform.Rx * wx + transform.Sy * wy + transform.Ty);
            int px = (int)Math.Round(worldPt.X);
            int py = (int)Math.Round(worldPt.Y);
            if (px < 0 || px >= (int)image.Columns || py < 0 || py >= (int)image.Rows)
            {
                continue;
            }

            var row = image.GetPixelRowForWrite(py);
            int offset = px * channels;
            double invA = 1.0 - fillOpacity;

            row[offset] = (ushort)Math.Clamp(fillR * fillOpacity + row[offset] * invA, 0, Quantum.MaxValue);
            if (channels > 1)
            {
                row[offset + 1] = (ushort)Math.Clamp(fillG * fillOpacity + row[offset + 1] * invA, 0, Quantum.MaxValue);
            }

            if (channels > 2)
            {
                row[offset + 2] = (ushort)Math.Clamp(fillB * fillOpacity + row[offset + 2] * invA, 0, Quantum.MaxValue);
            }

            if (hasAlpha)
            {
                ushort srcAlphaQ = (ushort)(fillOpacity * Quantum.MaxValue);
                int ai = offset + channels - 1;
                row[ai] = (ushort)Math.Clamp(srcAlphaQ + row[ai] * invA, 0, Quantum.MaxValue);
            }
        }
    }

    #endregion

    #region Flood fill

    /// <summary>
    /// Flood fill starting at (x, y) with the current fill color.
    /// </summary>
    public void FloodFill(int startX, int startY, double fuzzPercent = 0)
    {
        if (startX < 0 || startX >= (int)image.Columns || startY < 0 || startY >= (int)image.Rows)
        {
            return;
        }

        var targetPixel = image.GetPixel(startX, startY);
        ushort targetR = (ushort)targetPixel.Red, targetG = (ushort)targetPixel.Green, targetB = (ushort)targetPixel.Blue;
        ushort fillR = Quantum.ScaleFromByte(current.FillColor.R);
        ushort fillG = Quantum.ScaleFromByte(current.FillColor.G);
        ushort fillB = Quantum.ScaleFromByte(current.FillColor.B);
        ushort fillA = Quantum.ScaleFromByte(current.FillColor.A);

        double fuzz = fuzzPercent / 100.0 * Quantum.MaxValue;
        double fuzzSq = fuzz * fuzz;

        // Already the fill color?
        if (QuantumColorMatch(targetR, targetG, targetB, fillR, fillG, fillB, 0))
        {
            return;
        }

        int width = (int)image.Columns, height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;
        var visited = new bool[width * height];
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        visited[startY * width + startX] = true;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            var row = image.GetPixelRowForWrite(y);
            int offset = x * channels;
            row[offset] = fillR;
            if (channels > 1)
            {
                row[offset + 1] = fillG;
            }

            if (channels > 2)
            {
                row[offset + 2] = fillB;
            }

            if (hasAlpha)
            {
                row[offset + channels - 1] = fillA;
            }

            ReadOnlySpan<(int dx, int dy)> neighbors = [ (0, -1), (0, 1), (-1, 0), (1, 0) ];
            foreach (var (dx, dy) in neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                {
                    continue;
                }

                int ni = ny * width + nx;
                if (visited[ni])
                {
                    continue;
                }

                var nrow = image.GetPixelRow(ny);
                int no = nx * channels;
                ushort nR = nrow[no];
                ushort nG = channels > 1 ? nrow[no + 1] : (ushort)0;
                ushort nB = channels > 2 ? nrow[no + 2] : (ushort)0;

                if (QuantumColorMatch(nR, nG, nB, targetR, targetG, targetB, fuzzSq))
                {
                    visited[ni] = true;
                    queue.Enqueue((nx, ny));
                }
            }
        }
    }

    private static bool QuantumColorMatch(ushort r1, ushort g1, ushort b1,
        ushort r2, ushort g2, ushort b2, double fuzzSq)
    {
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return dr * dr + dg * dg + db * db <= fuzzSq;
    }

    #endregion

    #region Gradient fills

    /// <summary>
    /// Fill a polygon with a linear gradient between two colors.
    /// </summary>
    public void FillLinearGradient(ReadOnlySpan<PointD> polygon,
        double x1, double y1, double x2, double y2,
        DrawColor startColor, DrawColor endColor)
    {
        var pts = TransformPoints(polygon);
        if (pts.Length < 3)
        {
            return;
        }

        double dx = x2 - x1, dy = y2 - y1;
        double gradLenSq = dx * dx + dy * dy;
        if (gradLenSq < 1e-10)
        {
            return;
        }

        // Rasterize polygon to get a mask
        var mask = new ImageFrame();
        mask.Initialize((int)image.Columns, (int)image.Rows, ColorspaceType.SRGB, false);
        var maskState = current.Clone();
        maskState.FillColor = DrawColor.White;
        maskState.StrokeColor = DrawColor.Transparent;
        EdgeRasterizer.FillPolygon(mask, pts, maskState);

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        for (int py = 0;py < (int)image.Rows;py++)
        {
            var maskRow = mask.GetPixelRow(py);
            var imgRow = image.GetPixelRowForWrite(py);

            for (int px = 0;px < (int)image.Columns;px++)
            {
                ushort maskVal = maskRow[px * mask.NumberOfChannels];
                if (maskVal < 256)
                {
                    continue;
                }

                double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / gradLenSq, 0, 1);
                ushort r = (ushort)(Quantum.ScaleFromByte(startColor.R) * (1 - t) + Quantum.ScaleFromByte(endColor.R) * t);
                ushort g = (ushort)(Quantum.ScaleFromByte(startColor.G) * (1 - t) + Quantum.ScaleFromByte(endColor.G) * t);
                ushort b = (ushort)(Quantum.ScaleFromByte(startColor.B) * (1 - t) + Quantum.ScaleFromByte(endColor.B) * t);

                double alpha = current.Opacity * (maskVal * Quantum.Scale);
                double invA = 1.0 - alpha;
                int offset = px * channels;

                imgRow[offset] = (ushort)Math.Clamp(r * alpha + imgRow[offset] * invA, 0, Quantum.MaxValue);
                if (channels > 1)
                {
                    imgRow[offset + 1] = (ushort)Math.Clamp(g * alpha + imgRow[offset + 1] * invA, 0, Quantum.MaxValue);
                }

                if (channels > 2)
                {
                    imgRow[offset + 2] = (ushort)Math.Clamp(b * alpha + imgRow[offset + 2] * invA, 0, Quantum.MaxValue);
                }

                if (hasAlpha)
                {
                    ushort a = (ushort)(Quantum.ScaleFromByte(startColor.A) * (1 - t) + Quantum.ScaleFromByte(endColor.A) * t);
                    int ai = offset + channels - 1;
                    imgRow[ai] = (ushort)Math.Clamp(a * alpha + imgRow[ai] * invA, 0, Quantum.MaxValue);
                }
            }
        }

        mask.Dispose();
    }

    /// <summary>
    /// Fill a polygon with a radial gradient between two colors.
    /// </summary>
    public void FillRadialGradient(ReadOnlySpan<PointD> polygon,
        double cx, double cy, double radius,
        DrawColor centerColor, DrawColor outerColor)
    {
        var pts = TransformPoints(polygon);
        if (pts.Length < 3 || radius < 1e-10)
        {
            return;
        }

        var mask = new ImageFrame();
        mask.Initialize((int)image.Columns, (int)image.Rows, ColorspaceType.SRGB, false);
        var maskState = current.Clone();
        maskState.FillColor = DrawColor.White;
        maskState.StrokeColor = DrawColor.Transparent;
        EdgeRasterizer.FillPolygon(mask, pts, maskState);

        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        for (int py = 0;py < (int)image.Rows;py++)
        {
            var maskRow = mask.GetPixelRow(py);
            var imgRow = image.GetPixelRowForWrite(py);

            for (int px = 0;px < (int)image.Columns;px++)
            {
                ushort maskVal = maskRow[px * mask.NumberOfChannels];
                if (maskVal < 256)
                {
                    continue;
                }

                double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                double t = Math.Clamp(dist / radius, 0, 1);

                ushort r = (ushort)(Quantum.ScaleFromByte(centerColor.R) * (1 - t) + Quantum.ScaleFromByte(outerColor.R) * t);
                ushort g = (ushort)(Quantum.ScaleFromByte(centerColor.G) * (1 - t) + Quantum.ScaleFromByte(outerColor.G) * t);
                ushort b = (ushort)(Quantum.ScaleFromByte(centerColor.B) * (1 - t) + Quantum.ScaleFromByte(outerColor.B) * t);

                double alpha = current.Opacity * (maskVal * Quantum.Scale);
                double invA = 1.0 - alpha;
                int offset = px * channels;

                imgRow[offset] = (ushort)Math.Clamp(r * alpha + imgRow[offset] * invA, 0, Quantum.MaxValue);
                if (channels > 1)
                {
                    imgRow[offset + 1] = (ushort)Math.Clamp(g * alpha + imgRow[offset + 1] * invA, 0, Quantum.MaxValue);
                }

                if (channels > 2)
                {
                    imgRow[offset + 2] = (ushort)Math.Clamp(b * alpha + imgRow[offset + 2] * invA, 0, Quantum.MaxValue);
                }

                if (hasAlpha)
                {
                    ushort a = (ushort)(Quantum.ScaleFromByte(centerColor.A) * (1 - t) + Quantum.ScaleFromByte(outerColor.A) * t);
                    int ai = offset + channels - 1;
                    imgRow[ai] = (ushort)Math.Clamp(a * alpha + imgRow[ai] * invA, 0, Quantum.MaxValue);
                }
            }
        }

        mask.Dispose();
    }

    #endregion

    #region Helpers

    private PointD[] TransformPoints(ReadOnlySpan<PointD> points)
    {
        var result = new PointD[points.Length];
        var t = current.Transform;
        for (int i = 0;i < points.Length;i++)
        {
            var p = points[i];
            result[i] = new PointD(t.Sx * p.X + t.Ry * p.Y + t.Tx,
                                   t.Rx * p.X + t.Sy * p.Y + t.Ty);
        }
        return result;
    }

    #endregion
}
