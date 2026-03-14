using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Analysis;

/// <summary>
/// Connected-component labeling (CCL) for object detection and measurement.
/// Uses a two-pass union-find algorithm on binary/thresholded images.
/// Counts objects, measures area, bounding boxes, and centroids.
/// </summary>
public static class ConnectedComponents
{
    /// <summary>
    /// Information about a single connected component (object).
    /// </summary>
    public struct Component
    {
        /// <summary>Label ID (0 = background).</summary>
        public int Label;

        /// <summary>Number of pixels in this component.</summary>
        public int Area;

        /// <summary>Bounding box (x, y, width, height).</summary>
        public (int X, int Y, int Width, int Height) BoundingBox;

        /// <summary>Centroid position.</summary>
        public (double X, double Y) Centroid;

        /// <summary>Mean intensity of the component (0..1).</summary>
        public double MeanIntensity;
    }

    /// <summary>
    /// Result of connected-component analysis.
    /// </summary>
    public struct CclResult
    {
        /// <summary>Label map — same dimensions as input, each pixel contains its component label.</summary>
        public int[] Labels;

        /// <summary>Image width.</summary>
        public int Width;

        /// <summary>Image height.</summary>
        public int Height;

        /// <summary>Array of detected components (index 0 is background).</summary>
        public Component[] Components;

        /// <summary>Number of foreground objects (excludes background).</summary>
        public int ObjectCount;
    }

    /// <summary>
    /// Perform connected-component labeling on the image.
    /// </summary>
    /// <param name="source">Source image (thresholded to binary recommended).</param>
    /// <param name="threshold">Intensity threshold (0..1) — pixels above this are foreground (default 0.5).</param>
    /// <param name="connectivity">4 or 8-connected (default 8).</param>
    /// <returns>CCL result with label map and component statistics.</returns>
    public static CclResult Analyze(ImageFrame source, double threshold = 0.5, int connectivity = 8)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;

        // Step 1: Create binary image
        bool[] foreground = new bool[width * height];
        float[] intensities = new float[width * height];
        ushort thresh = (ushort)(threshold * Quantum.MaxValue);

        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                ushort val;
                if (channels >= 3)
                {
                    val = (ushort)(0.2126 * row[off] + 0.7152 * row[off + 1] + 0.0722 * row[off + 2]);
                }
                else
                {
                    val = row[off];
                }
                foreground[y * width + x] = val >= thresh;
                intensities[y * width + x] = val * (float)Quantum.Scale;
            }
        }

        // Step 2: Two-pass union-find CCL
        int[] labels = new int[width * height];
        int nextLabel = 1;
        int[] parent = new int[width * height / 2 + 2]; // union-find parent array
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        // First pass: assign provisional labels
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!foreground[y * width + x]) continue;

                // Get neighbor labels
                var neighbors = new List<int>(4);

                if (x > 0 && labels[y * width + (x - 1)] > 0) // West
                    neighbors.Add(labels[y * width + (x - 1)]);
                if (y > 0 && labels[(y - 1) * width + x] > 0) // North
                    neighbors.Add(labels[(y - 1) * width + x]);

                if (connectivity == 8)
                {
                    if (y > 0 && x > 0 && labels[(y - 1) * width + (x - 1)] > 0) // NW
                        neighbors.Add(labels[(y - 1) * width + (x - 1)]);
                    if (y > 0 && x < width - 1 && labels[(y - 1) * width + (x + 1)] > 0) // NE
                        neighbors.Add(labels[(y - 1) * width + (x + 1)]);
                }

                if (neighbors.Count == 0)
                {
                    if (nextLabel >= parent.Length)
                    {
                        Array.Resize(ref parent, parent.Length * 2);
                        for (int i = parent.Length / 2; i < parent.Length; i++) parent[i] = i;
                    }
                    labels[y * width + x] = nextLabel++;
                }
                else
                {
                    int minLabel = int.MaxValue;
                    foreach (int n in neighbors)
                    {
                        int root = Find(parent, n);
                        if (root < minLabel) minLabel = root;
                    }
                    labels[y * width + x] = minLabel;

                    // Union all neighbor labels
                    foreach (int n in neighbors)
                    {
                        Union(parent, minLabel, n);
                    }
                }
            }
        }

        // Second pass: relabel with canonical roots
        var rootToLabel = new Dictionary<int, int>();
        int finalLabel = 1;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == 0) continue;
            int root = Find(parent, labels[i]);
            if (!rootToLabel.TryGetValue(root, out int mapped))
            {
                mapped = finalLabel++;
                rootToLabel[root] = mapped;
            }
            labels[i] = mapped;
        }

        int objectCount = finalLabel - 1;

        // Step 3: Compute component statistics
        long[] sumX = new long[finalLabel];
        long[] sumY = new long[finalLabel];
        int[] area = new int[finalLabel];
        int[] minX = new int[finalLabel];
        int[] minY = new int[finalLabel];
        int[] maxX = new int[finalLabel];
        int[] maxY = new int[finalLabel];
        double[] sumIntensity = new double[finalLabel];

        for (int i = 0; i < finalLabel; i++)
        {
            minX[i] = int.MaxValue;
            minY[i] = int.MaxValue;
            maxX[i] = int.MinValue;
            maxY[i] = int.MinValue;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int lbl = labels[y * width + x];
                if (lbl == 0) continue;
                area[lbl]++;
                sumX[lbl] += x;
                sumY[lbl] += y;
                sumIntensity[lbl] += intensities[y * width + x];
                if (x < minX[lbl]) minX[lbl] = x;
                if (y < minY[lbl]) minY[lbl] = y;
                if (x > maxX[lbl]) maxX[lbl] = x;
                if (y > maxY[lbl]) maxY[lbl] = y;
            }
        }

        var components = new Component[finalLabel];
        components[0] = new Component { Label = 0, Area = width * height - area.Skip(1).Sum() };

        for (int i = 1; i < finalLabel; i++)
        {
            components[i] = new Component
            {
                Label = i,
                Area = area[i],
                BoundingBox = (minX[i], minY[i], maxX[i] - minX[i] + 1, maxY[i] - minY[i] + 1),
                Centroid = (area[i] > 0 ? (double)sumX[i] / area[i] : 0, area[i] > 0 ? (double)sumY[i] / area[i] : 0),
                MeanIntensity = area[i] > 0 ? sumIntensity[i] / area[i] : 0
            };
        }

        return new CclResult
        {
            Labels = labels,
            Width = width,
            Height = height,
            Components = components,
            ObjectCount = objectCount
        };
    }

    /// <summary>
    /// Render labeled components as a color-coded image for visualization.
    /// Each component gets a unique color; background is black.
    /// </summary>
    public static ImageFrame RenderLabels(CclResult result)
    {
        var output = new ImageFrame();
        output.Initialize(result.Width, result.Height, ColorspaceType.SRGB, false);

        // Generate distinct colors for each label
        var colors = GenerateColors(result.ObjectCount + 1);

        for (int y = 0; y < result.Height; y++)
        {
            var row = output.GetPixelRowForWrite(y);
            for (int x = 0; x < result.Width; x++)
            {
                int lbl = result.Labels[y * result.Width + x];
                var (r, g, b) = colors[lbl];
                int off = x * 3;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }

        return output;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // path compression
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
        {
            if (rootA < rootB) parent[rootB] = rootA;
            else parent[rootA] = rootB;
        }
    }

    private static (ushort r, ushort g, ushort b)[] GenerateColors(int count)
    {
        var colors = new (ushort r, ushort g, ushort b)[count];
        colors[0] = (0, 0, 0); // background = black
        for (int i = 1; i < count; i++)
        {
            // HSL with evenly spaced hues
            double hue = (double)(i - 1) / Math.Max(1, count - 1) * 360.0;
            var (r, g, b) = HslToRgb(hue, 0.8, 0.6);
            colors[i] = (r, g, b);
        }
        return colors;
    }

    private static (ushort r, ushort g, ushort b) HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        double r, g, b;

        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return (
            (ushort)((r + m) * Quantum.MaxValue),
            (ushort)((g + m) * Quantum.MaxValue),
            (ushort)((b + m) * Quantum.MaxValue)
        );
    }
}
