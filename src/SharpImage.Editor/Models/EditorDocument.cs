using SharpImage.Image;
using SharpImage.Layers;

namespace SharpImage.Editor.Models;

/// <summary>
/// Represents a single open document in the editor with its image data,
/// layer stack, file path, dirty state, and undo history.
/// </summary>
public sealed class EditorDocument
{
    public string? FilePath { get; set; }
    public string Title => FilePath is not null ? Path.GetFileName(FilePath) : "Untitled";
    public bool IsDirty { get; set; }

    /// <summary>The layer stack for this document. Always has at least one layer.</summary>
    public LayerStack Layers { get; }

    /// <summary>Index of the currently active (selected) layer.</summary>
    public int ActiveLayerIndex { get; set; }

    /// <summary>Document width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Document height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>
    /// Selection mask: byte array of Width×Height values (0 = unselected, 255 = selected).
    /// Null means no selection (everything is selected).
    /// </summary>
    public byte[]? SelectionMask { get; set; }

    /// <summary>True when a selection is active (mask is non-null).</summary>
    public bool HasSelection => SelectionMask is not null;

    public EditorDocument(int width, int height)
    {
        Width = width;
        Height = height;
        Layers = new LayerStack((uint)width, (uint)height);

        var background = new Layer("Background");
        var frame = new ImageFrame();
        frame.Initialize(width, height);
        background.Content = frame;
        Layers.AddLayer(background);
    }

    public EditorDocument(ImageFrame image, string? filePath = null)
    {
        Width = (int)image.Columns;
        Height = (int)image.Rows;
        FilePath = filePath;
        Layers = new LayerStack((uint)image.Columns, (uint)image.Rows);

        var background = new Layer("Background") { Content = image };
        Layers.AddLayer(background);
    }

    /// <summary>Selects all pixels (clears the mask — null means everything selected).</summary>
    public void SelectAll() => SelectionMask = null;

    /// <summary>Deselects all pixels (sets mask to all zeros).</summary>
    public void Deselect()
    {
        SelectionMask = new byte[Width * Height];
    }

    /// <summary>Inverts the current selection. If no selection, selects nothing.</summary>
    public void InvertSelection()
    {
        if (SelectionMask is null)
        {
            Deselect();
            return;
        }
        for (int i = 0; i < SelectionMask.Length; i++)
            SelectionMask[i] = (byte)(255 - SelectionMask[i]);
    }

    /// <summary>Creates a rectangular selection mask.</summary>
    public void SelectRectangle(int x, int y, int width, int height)
    {
        SelectionMask = new byte[Width * Height];
        int xMin = Math.Max(0, x);
        int xMax = Math.Min(Width, x + width);
        int yMin = Math.Max(0, y);
        int yMax = Math.Min(Height, y + height);
        for (int row = yMin; row < yMax; row++)
        {
            int rowOffset = row * Width;
            for (int col = xMin; col < xMax; col++)
                SelectionMask[rowOffset + col] = 255;
        }
    }

    /// <summary>Creates an elliptical selection mask.</summary>
    public void SelectEllipse(int cx, int cy, int rx, int ry)
    {
        SelectionMask = new byte[Width * Height];
        int xMin = Math.Max(0, cx - rx);
        int xMax = Math.Min(Width - 1, cx + rx);
        int yMin = Math.Max(0, cy - ry);
        int yMax = Math.Min(Height - 1, cy + ry);
        double rxSq = (double)rx * rx;
        double rySq = (double)ry * ry;
        for (int row = yMin; row <= yMax; row++)
        {
            double dy = row - cy;
            for (int col = xMin; col <= xMax; col++)
            {
                double dx = col - cx;
                if (dx * dx / rxSq + dy * dy / rySq <= 1.0)
                    SelectionMask[row * Width + col] = 255;
            }
        }
    }

    /// <summary>Creates a selection mask from a polygon (list of points). Uses scan-line fill.</summary>
    public void SelectPolygon(ReadOnlySpan<(int X, int Y)> points)
    {
        if (points.Length < 3) return;
        SelectionMask = new byte[Width * Height];

        // Find Y bounds
        int yMin = int.MaxValue, yMax = int.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].Y < yMin) yMin = points[i].Y;
            if (points[i].Y > yMax) yMax = points[i].Y;
        }
        yMin = Math.Max(0, yMin);
        yMax = Math.Min(Height - 1, yMax);

        // Scan-line fill
        var intersections = new List<int>();
        for (int y = yMin; y <= yMax; y++)
        {
            intersections.Clear();
            int n = points.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int y1 = points[i].Y, y2 = points[j].Y;
                if ((y1 <= y && y2 > y) || (y2 <= y && y1 > y))
                {
                    double t = (double)(y - y1) / (y2 - y1);
                    int x = (int)(points[i].X + t * (points[j].X - points[i].X));
                    intersections.Add(x);
                }
            }
            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                int xStart = Math.Max(0, intersections[i]);
                int xEnd = Math.Min(Width - 1, intersections[i + 1]);
                int rowOffset = y * Width;
                for (int x = xStart; x <= xEnd; x++)
                    SelectionMask[rowOffset + x] = 255;
            }
        }
    }

    /// <summary>
    /// Returns the flattened composite of all visible layers for display.
    /// </summary>
    public ImageFrame Flatten()
    {
        return Layers.Flatten();
    }

    /// <summary>
    /// Returns the active (selected) layer's image data for editing.
    /// </summary>
    public ImageFrame GetActiveLayerImage()
    {
        return Layers[ActiveLayerIndex].Content;
    }

    /// <summary>
    /// Replaces the active layer's image data (used after applying an operation).
    /// </summary>
    public void SetActiveLayerImage(ImageFrame frame)
    {
        Layers[ActiveLayerIndex].Content = frame;
        IsDirty = true;
    }

    public void Resize(int newWidth, int newHeight)
    {
        Width = newWidth;
        Height = newHeight;
    }

    /// <summary>
    /// Modifies the current selection mask. Operations: border, smooth, expand, contract, feather, grow, similar.
    /// </summary>
    public void ModifySelection(string operation, int amount)
    {
        if (SelectionMask is null) return;
        int w = Width, h = Height;

        switch (operation)
        {
            case "expand":
                SelectionMask = DilateErode(SelectionMask, w, h, amount, dilate: true);
                break;
            case "contract":
                SelectionMask = DilateErode(SelectionMask, w, h, amount, dilate: false);
                break;
            case "feather":
                SelectionMask = BoxBlurMask(SelectionMask, w, h, amount);
                break;
            case "smooth":
                SelectionMask = BoxBlurMask(SelectionMask, w, h, amount);
                break;
            case "border":
                var eroded = DilateErode(SelectionMask, w, h, amount, dilate: false);
                for (int i = 0; i < SelectionMask.Length; i++)
                    SelectionMask[i] = (byte)Math.Max(0, SelectionMask[i] - eroded[i]);
                break;
            case "grow":
            case "similar":
                SelectionMask = DilateErode(SelectionMask, w, h, amount, dilate: true);
                break;
        }
    }

    private static byte[] DilateErode(byte[] mask, int w, int h, int radius, bool dilate)
    {
        var result = new byte[mask.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte best = dilate ? (byte)0 : (byte)255;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        byte v = mask[ny * w + nx];
                        best = dilate ? Math.Max(best, v) : Math.Min(best, v);
                    }
                }
                result[y * w + x] = best;
            }
        }
        return result;
    }

    private static byte[] BoxBlurMask(byte[] mask, int w, int h, int radius)
    {
        var result = new byte[mask.Length];
        int kernelSize = (2 * radius + 1);
        int kernelArea = kernelSize * kernelSize;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, h - 1);
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = Math.Clamp(x + dx, 0, w - 1);
                        sum += mask[ny * w + nx];
                    }
                }
                result[y * w + x] = (byte)(sum / kernelArea);
            }
        }
        return result;
    }
}
