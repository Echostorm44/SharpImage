// SVG-like path command parser and curve tessellation.
// Converts path strings (M, L, H, V, C, Q, S, T, A, Z) into polygon points.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace SharpImage.Drawing;

/// <summary>
/// Parses SVG path data strings into sequences of PointD for rasterization. Supports all standard SVG path commands
/// including Bezier curves and arcs.
/// </summary>
public static class PathParser
{
    private const int BezierSegments = 16;
    private const int ArcSegments = 32;

    /// <summary>
    /// Parse an SVG path data string into a list of sub-paths (each a closed or open polygon).
    /// </summary>
    public static List<(PointD[] Points, bool Closed)> Parse(string pathData)
    {
        var subPaths = new List<(PointD[] Points, bool Closed)>();
        var currentPoints = new List<PointD>();
        var tokens = Tokenize(pathData);
        int idx = 0;

        PointD current = default;
        PointD subPathStart = default;
        PointD lastControl = default;
        char lastCommand = '\0';

        while (idx < tokens.Count)
        {
            char cmd = tokens[idx].Command;
            if (cmd == '\0')
            {
                // Implicit repeat of last command
                cmd = lastCommand;
                if (cmd == 'M')
                {
                    cmd = 'L';
                }

                if (cmd == 'm')
                {
                    cmd = 'l';
                }
            }
            else
            {
                idx++;
            }

            bool relative = char.IsLower(cmd);
            char upperCmd = char.ToUpper(cmd);

            switch (upperCmd)
            {
                case 'M':
                {
                    if (currentPoints.Count > 0)
                    {
                        subPaths.Add((currentPoints.ToArray(), false));
                        currentPoints.Clear();
                    }
                    double x = NextNum(tokens, ref idx);
                    double y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    current = new PointD(x, y);
                    subPathStart = current;
                    currentPoints.Add(current);
                    lastCommand = cmd;
                    // Subsequent coordinates are implicit LineTo
                    while (idx < tokens.Count && tokens[idx].Command == '\0')
                    {
                        x = NextNum(tokens, ref idx);
                        y = NextNum(tokens, ref idx);
                        if (relative)
                        {
                            x += current.X;
                            y += current.Y;
                        }
                        current = new PointD(x, y);
                        currentPoints.Add(current);
                    }
                    break;
                }
                case 'L':
                {
                    double x = NextNum(tokens, ref idx);
                    double y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    current = new PointD(x, y);
                    currentPoints.Add(current);
                    lastCommand = cmd;
                    break;
                }
                case 'H':
                {
                    double x = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x += current.X;
                    }

                    current = new PointD(x, current.Y);
                    currentPoints.Add(current);
                    lastCommand = cmd;
                    break;
                }
                case 'V':
                {
                    double y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        y += current.Y;
                    }

                    current = new PointD(current.X, y);
                    currentPoints.Add(current);
                    lastCommand = cmd;
                    break;
                }
                case 'C': // Cubic Bezier
                {
                    double x1 = NextNum(tokens, ref idx), y1 = NextNum(tokens, ref idx);
                    double x2 = NextNum(tokens, ref idx), y2 = NextNum(tokens, ref idx);
                    double x = NextNum(tokens, ref idx), y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x1 += current.X;
                        y1 += current.Y;
                        x2 += current.X;
                        y2 += current.Y;
                        x += current.X;
                        y += current.Y;
                    }
                    TessellateCubicBezier(currentPoints, current,
                        new PointD(x1, y1), new PointD(x2, y2), new PointD(x, y));
                    lastControl = new PointD(x2, y2);
                    current = new PointD(x, y);
                    lastCommand = cmd;
                    break;
                }
                case 'S': // Smooth cubic Bezier
                {
                    double x2 = NextNum(tokens, ref idx), y2 = NextNum(tokens, ref idx);
                    double x = NextNum(tokens, ref idx), y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x2 += current.X;
                        y2 += current.Y;
                        x += current.X;
                        y += current.Y;
                    }
                    // Reflect last control point
                    PointD cp1 = (lastCommand == 'C' || lastCommand == 'c' ||
                                  lastCommand == 'S' || lastCommand == 's')
                        ? new PointD(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y)
                        : current;
                    TessellateCubicBezier(currentPoints, current,
                        cp1, new PointD(x2, y2), new PointD(x, y));
                    lastControl = new PointD(x2, y2);
                    current = new PointD(x, y);
                    lastCommand = cmd;
                    break;
                }
                case 'Q': // Quadratic Bezier
                {
                    double x1 = NextNum(tokens, ref idx), y1 = NextNum(tokens, ref idx);
                    double x = NextNum(tokens, ref idx), y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x1 += current.X;
                        y1 += current.Y;
                        x += current.X;
                        y += current.Y;
                    }
                    TessellateQuadraticBezier(currentPoints, current,
                        new PointD(x1, y1), new PointD(x, y));
                    lastControl = new PointD(x1, y1);
                    current = new PointD(x, y);
                    lastCommand = cmd;
                    break;
                }
                case 'T': // Smooth quadratic Bezier
                {
                    double x = NextNum(tokens, ref idx), y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    PointD cp = (lastCommand == 'Q' || lastCommand == 'q' ||
                                 lastCommand == 'T' || lastCommand == 't')
                        ? new PointD(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y)
                        : current;
                    TessellateQuadraticBezier(currentPoints, current, cp, new PointD(x, y));
                    lastControl = cp;
                    current = new PointD(x, y);
                    lastCommand = cmd;
                    break;
                }
                case 'A': // Elliptical arc
                {
                    double rx = NextNum(tokens, ref idx), ry = NextNum(tokens, ref idx);
                    double rotation = NextNum(tokens, ref idx);
                    double largeArc = NextNum(tokens, ref idx);
                    double sweep = NextNum(tokens, ref idx);
                    double x = NextNum(tokens, ref idx), y = NextNum(tokens, ref idx);
                    if (relative)
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    TessellateArc(currentPoints, current, rx, ry, rotation,
                        largeArc > 0.5, sweep > 0.5, new PointD(x, y));
                    current = new PointD(x, y);
                    lastCommand = cmd;
                    break;
                }
                case 'Z':
                {
                    current = subPathStart;
                    if (currentPoints.Count > 0)
                    {
                        subPaths.Add((currentPoints.ToArray(), true));
                        currentPoints.Clear();
                    }
                    lastCommand = cmd;
                    break;
                }
                default:
                    idx++; // skip unknown
                    break;
            }
        }

        if (currentPoints.Count > 0)
        {
            subPaths.Add((currentPoints.ToArray(), false));
        }

        return subPaths;
    }

    #region Tokenizer

    private readonly struct PathToken
    {
        public readonly char Command; // '\0' for numbers
        public readonly double Value;

        public PathToken(char command)
        {
            Command = command;
            Value = 0;
        }

        public PathToken(double value)
        {
            Command = '\0';
            Value = value;
        }
    }

    private static List<PathToken> Tokenize(string data)
    {
        var tokens = new List<PathToken>();
        int i = 0;
        while (i < data.Length)
        {
            char c = data[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (IsCommand(c))
            {
                tokens.Add(new PathToken(c));
                i++;
                continue;
            }

            // Parse number
            int start = i;
            if (c == '-' || c == '+')
            {
                i++;
            }

            bool hasDot = false;
            while (i < data.Length)
            {
                char ch = data[i];
                if (ch == '.' && !hasDot)
                {
                    hasDot = true;
                    i++;
                }
                else if (char.IsDigit(ch))
                {
                    i++;
                }
                else if ((ch == 'e' || ch == 'E') && i + 1 < data.Length)
                {
                    i++;
                    if (i < data.Length && (data[i] == '+' || data[i] == '-'))
                    {
                        i++;
                    }
                }
                else
                {
                    break;
                }
            }

            if (i > start)
            {
                if (double.TryParse(data.AsSpan(start, i - start),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    tokens.Add(new PathToken(val));
                }
            }
            else
            {
                i++; // skip unrecognized char
            }
        }
        return tokens;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCommand(char c)
        => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v'
            or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't'
            or 'A' or 'a' or 'Z' or 'z';

    private static double NextNum(List<PathToken> tokens, ref int idx)
    {
        while (idx < tokens.Count && tokens[idx].Command != '\0')
        {
            idx++;
        }

        if (idx >= tokens.Count)
        {
            return 0;
        }

        return tokens[idx++].Value;
    }

    #endregion

    #region Curve tessellation

    private static void TessellateCubicBezier(List<PointD> points,
        PointD p0, PointD p1, PointD p2, PointD p3)
    {
        for (int i = 1;i <= BezierSegments;i++)
        {
            double t = i / (double)BezierSegments;
            double u = 1 - t;
            double tt = t * t, uu = u * u;
            double uuu = uu * u, ttt = tt * t;
            points.Add(new PointD(
                uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X,
                uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y));
        }
    }

    private static void TessellateQuadraticBezier(List<PointD> points,
        PointD p0, PointD p1, PointD p2)
    {
        for (int i = 1;i <= BezierSegments;i++)
        {
            double t = i / (double)BezierSegments;
            double u = 1 - t;
            points.Add(new PointD(
                u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
                u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y));
        }
    }

    private static void TessellateArc(List<PointD> points,
        PointD from, double rx, double ry, double rotationDeg,
        bool largeArc, bool sweep, PointD to)
    {
        if (Math.Abs(rx) < 1e-10 || Math.Abs(ry) < 1e-10)
        {
            points.Add(to);
            return;
        }

        // SVG arc implementation (endpoint parameterization to center parameterization)
        double phi = rotationDeg * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

        double dx2 = (from.X - to.X) / 2.0;
        double dy2 = (from.Y - to.Y) / 2.0;
        double x1p = cosPhi * dx2 + sinPhi * dy2;
        double y1p = -sinPhi * dx2 + cosPhi * dy2;

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        double x1p2 = x1p * x1p, y1p2 = y1p * y1p;
        double rx2 = rx * rx, ry2 = ry * ry;

        // Ensure radii are large enough
        double lambda = x1p2 / rx2 + y1p2 / ry2;
        if (lambda > 1)
        {
            double sq = Math.Sqrt(lambda);
            rx *= sq;
            ry *= sq;
            rx2 = rx * rx;
            ry2 = ry * ry;
        }

        double num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2;
        double den = rx2 * y1p2 + ry2 * x1p2;
        double sq2 = Math.Max(0, num / den);
        double factor = Math.Sqrt(sq2) * (largeArc == sweep ? -1 : 1);

        double cxp = factor * rx * y1p / ry;
        double cyp = -factor * ry * x1p / rx;

        double cx = cosPhi * cxp - sinPhi * cyp + (from.X + to.X) / 2.0;
        double cy = sinPhi * cxp + cosPhi * cyp + (from.Y + to.Y) / 2.0;

        double theta1 = VectorAngle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dTheta = VectorAngle((x1p - cxp) / rx, (y1p - cyp) / ry,
                                    (-x1p - cxp) / rx, (-y1p - cyp) / ry);

        if (!sweep && dTheta > 0)
        {
            dTheta -= 2 * Math.PI;
        }
        else if (sweep && dTheta < 0)
        {
            dTheta += 2 * Math.PI;
        }

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI / ArcSegments * 2)));
        for (int i = 1;i <= segments;i++)
        {
            double t = theta1 + dTheta * i / segments;
            double cosT = Math.Cos(t), sinT = Math.Sin(t);
            double px = cosPhi * rx * cosT - sinPhi * ry * sinT + cx;
            double py = sinPhi * rx * cosT + cosPhi * ry * sinT + cy;
            points.Add(new PointD(px, py));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double angle = Math.Acos(Math.Clamp(dot / Math.Max(len, 1e-15), -1, 1));
        return (ux * vy - uy * vx) < 0 ? -angle : angle;
    }

    #endregion

    #region Shape generators

    /// <summary>
    /// Generate polygon points for a rectangle.
    /// </summary>
    public static PointD[] Rectangle(double x, double y, double width, double height) =>[ new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height) ];

    /// <summary>
    /// Generate polygon points for a rounded rectangle.
    /// </summary>
    public static PointD[] RoundedRectangle(double x, double y, double width, double height,
        double rx, double ry)
    {
        rx = Math.Min(rx, width / 2);
        ry = Math.Min(ry, height / 2);
        if (rx < 1e-10 || ry < 1e-10)
        {
            return Rectangle(x, y, width, height);
        }

        var pts = new List<PointD>();
        int cornerSegments = 8;

        // Top-right corner
        for (int i = 0;i <= cornerSegments;i++)
        {
            double t = (Math.PI / 2) * i / cornerSegments;
            pts.Add(new PointD(x + width - rx + rx * Math.Cos(Math.PI * 1.5 + t),
                               y + ry - ry * Math.Cos(t)));
        }
        // Bottom-right corner
        for (int i = 0;i <= cornerSegments;i++)
        {
            double t = (Math.PI / 2) * i / cornerSegments;
            pts.Add(new PointD(x + width - rx + rx * Math.Cos(t),
                               y + height - ry + ry * Math.Sin(t)));
        }
        // Bottom-left corner
        for (int i = 0;i <= cornerSegments;i++)
        {
            double t = (Math.PI / 2) * i / cornerSegments;
            pts.Add(new PointD(x + rx - rx * Math.Cos(Math.PI * 1.5 + t),
                               y + height - ry + ry * Math.Cos(t)));
        }
        // Top-left corner
        for (int i = 0;i <= cornerSegments;i++)
        {
            double t = (Math.PI / 2) * i / cornerSegments;
            pts.Add(new PointD(x + rx - rx * Math.Cos(t),
                               y + ry - ry * Math.Sin(t)));
        }

        return pts.ToArray();
    }

    /// <summary>
    /// Generate polygon points for an ellipse.
    /// </summary>
    public static PointD[] Ellipse(double cx, double cy, double rx, double ry, int segments = 64)
    {
        var pts = new PointD[segments];
        for (int i = 0;i < segments;i++)
        {
            double t = 2 * Math.PI * i / segments;
            pts[i] = new PointD(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t));
        }
        return pts;
    }

    /// <summary>
    /// Generate polygon points for a circle.
    /// </summary>
    public static PointD[] Circle(double cx, double cy, double radius, int segments = 64)
        => Ellipse(cx, cy, radius, radius, segments);

    /// <summary>
    /// Generate polygon points for an elliptical arc sector (from startAngle to endAngle in degrees).
    /// </summary>
    public static PointD[] Arc(double cx, double cy, double rx, double ry,
        double startDeg, double endDeg, int segments = 32)
    {
        var pts = new List<PointD>(segments + 2) { new(cx, cy) };
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = endDeg * Math.PI / 180.0;
        for (int i = 0;i <= segments;i++)
        {
            double t = startRad + (endRad - startRad) * i / segments;
            pts.Add(new PointD(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Generate a regular polygon (e.g. triangle=3, pentagon=5, hexagon=6).
    /// </summary>
    public static PointD[] RegularPolygon(double cx, double cy, double radius, int sides)
    {
        var pts = new PointD[sides];
        for (int i = 0;i < sides;i++)
        {
            double t = 2 * Math.PI * i / sides - Math.PI / 2; // start from top
            pts[i] = new PointD(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t));
        }
        return pts;
    }

    /// <summary>
    /// Generate a star polygon with given inner and outer radii.
    /// </summary>
    public static PointD[] Star(double cx, double cy, double outerRadius, double innerRadius, int points)
    {
        var pts = new PointD[points * 2];
        for (int i = 0;i < points * 2;i++)
        {
            double r = (i % 2 == 0) ? outerRadius : innerRadius;
            double t = Math.PI * i / points - Math.PI / 2;
            pts[i] = new PointD(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
        }
        return pts;
    }

    #endregion
}
