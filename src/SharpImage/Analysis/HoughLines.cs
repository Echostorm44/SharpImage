using SharpImage.Core;
using SharpImage.Image;
using System.Runtime.CompilerServices;

namespace SharpImage.Analysis;

/// <summary>
/// Hough transform for straight line detection. Works on binary edge images
/// (e.g., output from Canny). Detects lines in (rho, theta) parameter space.
/// </summary>
public static class HoughLines
{
    /// <summary>
    /// A detected line in Hough space.
    /// </summary>
    public struct DetectedLine
    {
        /// <summary>Distance from origin to the closest point on the line.</summary>
        public double Rho;

        /// <summary>Angle of the normal to the line (radians, 0..PI).</summary>
        public double Theta;

        /// <summary>Number of votes (edge pixels on this line).</summary>
        public int Votes;

        /// <summary>
        /// Get two endpoints of the line for drawing, clipped to image bounds.
        /// </summary>
        public readonly (int x1, int y1, int x2, int y2) GetEndpoints(int width, int height)
        {
            double cosT = Math.Cos(Theta);
            double sinT = Math.Sin(Theta);

            // Find two points far apart on the line: x*cos + y*sin = rho
            int x1, y1, x2, y2;
            if (Math.Abs(sinT) > 0.001)
            {
                // y = (rho - x*cos) / sin
                x1 = 0;
                y1 = (int)Math.Round((Rho - x1 * cosT) / sinT);
                x2 = width - 1;
                y2 = (int)Math.Round((Rho - x2 * cosT) / sinT);
            }
            else
            {
                // Nearly vertical: x = rho / cos (cosT is near ±1 here, so safe)
                double cosAbs = Math.Abs(cosT);
                x1 = x2 = cosAbs > 1e-10 ? (int)Math.Round(Rho / cosT) : 0;
                y1 = 0;
                y2 = height - 1;
            }

            return (x1, y1, x2, y2);
        }
    }

    /// <summary>
    /// Detect lines in a binary/grayscale edge image using the Hough transform.
    /// </summary>
    /// <param name="edgeImage">Binary edge image (white = edge).</param>
    /// <param name="threshold">Minimum vote count for a line to be detected (default: 100).</param>
    /// <param name="thetaResolution">Angular resolution in degrees (default: 1°).</param>
    /// <returns>Array of detected lines sorted by vote count (descending).</returns>
    public static DetectedLine[] Detect(ImageFrame edgeImage, int threshold = 100, double thetaResolution = 1.0)
    {
        int width = (int)edgeImage.Columns;
        int height = (int)edgeImage.Rows;
        int channels = edgeImage.NumberOfChannels;

        // Parameter space
        int thetaSteps = Math.Max(1, (int)Math.Ceiling(180.0 / thetaResolution));
        double thetaStep = Math.PI / thetaSteps;
        double maxRho = Math.Sqrt(width * width + height * height);
        int rhoSteps = (int)Math.Ceiling(2 * maxRho) + 1;

        // Precompute sin/cos tables
        double[] cosTable = new double[thetaSteps];
        double[] sinTable = new double[thetaSteps];
        for (int t = 0; t < thetaSteps; t++)
        {
            double theta = t * thetaStep;
            cosTable[t] = Math.Cos(theta);
            sinTable[t] = Math.Sin(theta);
        }

        // Accumulator
        int[] accumulator = new int[rhoSteps * thetaSteps];

        // Vote
        ushort edgeThreshold = (ushort)(Quantum.MaxValue / 2);
        for (int y = 0; y < height; y++)
        {
            var row = edgeImage.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                // Check if pixel is an edge (any channel above threshold)
                if (row[x * channels] < edgeThreshold) continue;

                for (int t = 0; t < thetaSteps; t++)
                {
                    double rho = x * cosTable[t] + y * sinTable[t];
                    int rhoIdx = (int)Math.Round(rho + maxRho);
                    if (rhoIdx >= 0 && rhoIdx < rhoSteps)
                    {
                        accumulator[rhoIdx * thetaSteps + t]++;
                    }
                }
            }
        }

        // Extract peaks above threshold
        var lines = new List<DetectedLine>();
        for (int r = 0; r < rhoSteps; r++)
        {
            for (int t = 0; t < thetaSteps; t++)
            {
                int votes = accumulator[r * thetaSteps + t];
                if (votes >= threshold)
                {
                    lines.Add(new DetectedLine
                    {
                        Rho = r - maxRho,
                        Theta = t * thetaStep,
                        Votes = votes
                    });
                }
            }
        }

        // Sort by vote count descending
        lines.Sort((a, b) => b.Votes.CompareTo(a.Votes));
        return lines.ToArray();
    }

    /// <summary>
    /// Draw detected lines onto an image for visualization.
    /// </summary>
    /// <param name="image">Image to draw on (modified in place).</param>
    /// <param name="lines">Lines to draw.</param>
    /// <param name="maxLines">Maximum number of lines to draw (default: 20).</param>
    /// <param name="lineColor">RGB color for lines (default: red).</param>
    public static void DrawLines(ImageFrame image, DetectedLine[] lines, int maxLines = 20,
        (ushort r, ushort g, ushort b)? lineColor = null)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;
        var color = lineColor ?? (Quantum.MaxValue, (ushort)0, (ushort)0);

        int count = Math.Min(lines.Length, maxLines);
        for (int i = 0; i < count; i++)
        {
            var (x1, y1, x2, y2) = lines[i].GetEndpoints(width, height);
            DrawLineBresenham(image, x1, y1, x2, y2, color, width, height, channels);
        }
    }

    private static void DrawLineBresenham(ImageFrame image, int x1, int y1, int x2, int y2,
        (ushort r, ushort g, ushort b) color, int width, int height, int channels)
    {
        int dx = Math.Abs(x2 - x1);
        int dy = Math.Abs(y2 - y1);
        int sx = x1 < x2 ? 1 : -1;
        int sy = y1 < y2 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (x1 >= 0 && x1 < width && y1 >= 0 && y1 < height)
            {
                var row = image.GetPixelRowForWrite(y1);
                int off = x1 * channels;
                if (channels >= 3)
                {
                    row[off] = color.r;
                    row[off + 1] = color.g;
                    row[off + 2] = color.b;
                }
                else
                {
                    row[off] = color.r;
                }
            }

            if (x1 == x2 && y1 == y2) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x1 += sx; }
            if (e2 < dx) { err += dx; y1 += sy; }
        }
    }
}
