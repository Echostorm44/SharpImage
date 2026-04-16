using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SharpImage.Editor.Models;

namespace SharpImage.Editor.Tools;

/// <summary>
/// Magic Wand selection tool. Click to select contiguous or global pixels by
/// color similarity using BFS flood fill. Sets the document's SelectionMask.
/// </summary>
public sealed class MagicWandTool : ITool
{
    public string Name => "Magic Wand";
    public string IconResourceKey => "IconWand";
    public Cursor ToolCursor => new(StandardCursorType.Cross);

    /// <summary>Color tolerance for matching (0–255 in byte scale, applied to 16-bit).</summary>
    public int Tolerance { get; set; } = 32;

    /// <summary>When true, only selects contiguous pixels. When false, selects all matching pixels globally.</summary>
    public bool Contiguous { get; set; } = true;

    private EditorDocument? document;

    /// <summary>Fires when a magic wand selection is completed.</summary>
    public event Action? SelectionCompleted;

    public void SetDocument(EditorDocument? doc) => document = doc;

    public void Activate() { }
    public void Deactivate() { }

    public void OnPointerPressed(PointerPressedEventArgs e, Point canvasPoint)
    {
        if (document is null) return;
        var activeLayer = document.Layers[document.ActiveLayerIndex];
        var frame = activeLayer.Content;
        if (frame is null) return;

        int clickX = (int)Math.Round(canvasPoint.X) - activeLayer.OffsetX;
        int clickY = (int)Math.Round(canvasPoint.Y) - activeLayer.OffsetY;

        if (clickX < 0 || clickX >= (int)frame.Columns || clickY < 0 || clickY >= (int)frame.Rows)
            return;

        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        int channels = frame.NumberOfChannels;

        // Read the seed pixel color
        var seedRow = frame.GetPixelRow(clickY);
        int seedOffset = clickX * channels;
        ushort seedR = seedRow[seedOffset];
        ushort seedG = channels > 1 ? seedRow[seedOffset + 1] : seedR;
        ushort seedB = channels > 2 ? seedRow[seedOffset + 2] : seedR;

        // Scale tolerance from byte range to 16-bit quantum range
        ushort toleranceQ = (ushort)(Tolerance * 257);

        // Allocate selection mask for the full document
        var mask = new byte[document.Width * document.Height];

        if (Contiguous)
        {
            FloodSelect(frame, mask, clickX, clickY, width, height, channels,
                activeLayer.OffsetX, activeLayer.OffsetY, seedR, seedG, seedB, toleranceQ, document.Width);
        }
        else
        {
            GlobalSelect(frame, mask, width, height, channels,
                activeLayer.OffsetX, activeLayer.OffsetY, seedR, seedG, seedB, toleranceQ, document.Width);
        }

        document.SelectionMask = mask;
        SelectionCompleted?.Invoke();
    }

    public void OnPointerMoved(PointerEventArgs e, Point canvasPoint) { }
    public void OnPointerReleased(PointerReleasedEventArgs e, Point canvasPoint) { }
    public void OnKeyDown(KeyEventArgs e) { }
    public void OnKeyUp(KeyEventArgs e) { }
    public void RenderOverlay(DrawingContext context, double zoom) { }
    public Control? BuildOptionsBar() => null;

    private static void FloodSelect(Image.ImageFrame frame, byte[] mask,
        int startX, int startY, int width, int height, int channels,
        int layerOffsetX, int layerOffsetY,
        ushort seedR, ushort seedG, ushort seedB, ushort tolerance, int docWidth)
    {
        FloodSelectCore(frame, mask, startX, startY, width, height, channels,
            layerOffsetX, layerOffsetY, seedR, seedG, seedB, tolerance, docWidth);
    }

    private static void FloodSelectCore(Image.ImageFrame frame, byte[] mask,
        int startX, int startY, int width, int height, int channels,
        int layerOffsetX, int layerOffsetY,
        ushort seedR, ushort seedG, ushort seedB, ushort tolerance, int docWidth)
    {
        var visited = new bool[width * height];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((startX, startY));
        visited[startY * width + startX] = true;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            var row = frame.GetPixelRow(y);
            int offset = x * channels;

            ushort r = row[offset];
            ushort g = channels > 1 ? row[offset + 1] : r;
            ushort b = channels > 2 ? row[offset + 2] : r;

            if (ColorDistance(r, g, b, seedR, seedG, seedB) <= tolerance)
            {
                int docX = x + layerOffsetX;
                int docY = y + layerOffsetY;
                if (docX >= 0 && docX < docWidth && docY >= 0 && docY * docWidth + docX < mask.Length)
                    mask[docY * docWidth + docX] = 255;

                // Enqueue 4-connected neighbors
                if (x > 0 && !visited[y * width + x - 1])
                {
                    visited[y * width + x - 1] = true;
                    queue.Enqueue((x - 1, y));
                }
                if (x < width - 1 && !visited[y * width + x + 1])
                {
                    visited[y * width + x + 1] = true;
                    queue.Enqueue((x + 1, y));
                }
                if (y > 0 && !visited[(y - 1) * width + x])
                {
                    visited[(y - 1) * width + x] = true;
                    queue.Enqueue((x, y - 1));
                }
                if (y < height - 1 && !visited[(y + 1) * width + x])
                {
                    visited[(y + 1) * width + x] = true;
                    queue.Enqueue((x, y + 1));
                }
            }
        }
    }

    private static void GlobalSelect(Image.ImageFrame frame, byte[] mask,
        int width, int height, int channels,
        int layerOffsetX, int layerOffsetY,
        ushort seedR, ushort seedG, ushort seedB, ushort tolerance, int docWidth)
    {
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRow(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                ushort r = row[offset];
                ushort g = channels > 1 ? row[offset + 1] : r;
                ushort b = channels > 2 ? row[offset + 2] : r;

                if (ColorDistance(r, g, b, seedR, seedG, seedB) <= tolerance)
                {
                    int docX = x + layerOffsetX;
                    int docY = y + layerOffsetY;
                    if (docX >= 0 && docX < docWidth && docY >= 0 && docY * docWidth + docX < mask.Length)
                        mask[docY * docWidth + docX] = 255;
                }
            }
        }
    }

    private static ushort ColorDistance(ushort r1, ushort g1, ushort b1, ushort r2, ushort g2, ushort b2)
    {
        int dr = Math.Abs(r1 - r2);
        int dg = Math.Abs(g1 - g2);
        int db = Math.Abs(b1 - b2);
        return (ushort)Math.Max(dr, Math.Max(dg, db));
    }
}
