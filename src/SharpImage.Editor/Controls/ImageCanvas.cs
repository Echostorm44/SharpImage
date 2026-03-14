using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SharpImage.Editor.Models;
using SharpImage.Editor.Tools;
using SharpImage.Image;

namespace SharpImage.Editor.Controls;

/// <summary>
/// Core canvas control that renders the composited document image with:
/// - Checkerboard transparency background
/// - Zoom and pan (centered on cursor, clamped to bounds)
/// - Pixel-accurate cursor tracking
/// - Tool overlay rendering (delegated to active ITool)
/// - Double-buffered WritableBitmap for smooth display
/// </summary>
public sealed class ImageCanvas : Control
{
    // ═══════ Display state ═══════
    private WriteableBitmap? displayBitmap;
    private WriteableBitmap? checkerBitmap;
    private EditorDocument? document;

    // ═══════ Zoom & pan ═══════
    private double zoom = 1.0;
    private Vector panOffset;           // offset in screen pixels from canvas center

    // ═══════ Cached state ═══════
    private bool needsComposite = true; // set when layers change and we need to re-flatten
    private ImageFrame? cachedFlattened; // cached flattened composite

    // ═══════ Channel visibility ═══════
    public bool ShowRedChannel { get; set; } = true;
    public bool ShowGreenChannel { get; set; } = true;
    public bool ShowBlueChannel { get; set; } = true;
    public bool ShowAlphaChannel { get; set; } = true;

    // ═══════ View overlays ═══════
    public bool ShowRulers { get; set; }
    public bool ShowGrid { get; set; }
    public bool ShowPixelGrid { get; set; }

    /// <summary>Grid spacing in pixels.</summary>
    public int GridSpacing { get; set; } = 50;

    private const int RulerThickness = 20;

    // ═══════ Tool interaction ═══════
    private ITool? activeTool;
    private bool isPointerCaptured;

    // ═══════ Brush cursor ═══════
    /// <summary>Brush size in image pixels, set by MainWindow when brush-type tools are active.</summary>
    public double BrushCursorSize { get; set; }
    /// <summary>Whether to show the brush size cursor circle.</summary>
    public bool ShowBrushCursor { get; set; }

    // ═══════ Public events ═══════
    /// <summary>Fires when the cursor moves over the canvas; provides image-space pixel coordinates.</summary>
    public event Action<int, int>? CursorPositionChanged;

    /// <summary>Fires when zoom level changes.</summary>
    public event Action<double>? ZoomChanged;

    // ═══════ Constants ═══════
    private const int CheckerSize = 8;
    private static readonly Color CheckerLight = Color.FromRgb(204, 204, 204);
    private static readonly Color CheckerDark = Color.FromRgb(170, 170, 170);
    private const double MinZoom = 0.01;    // 1%
    private const double MaxZoom = 64.0;    // 6400%

    // ═══════ Selection animation ═══════
    private Avalonia.Threading.DispatcherTimer? selectionAnimTimer;
    private double selectionDashOffset;
    private StreamGeometry? cachedSelectionGeometry;
    private byte[]? cachedSelectionMaskRef;

    public ImageCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        IsHitTestVisible = true;

        // Marching ants animation timer — lightweight, overlay-only repaint
        selectionAnimTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150), // ~7fps for smooth marching ants
        };
        selectionAnimTimer.Tick += (_, _) =>
        {
            if (document?.HasSelection == true)
            {
                selectionDashOffset += 2.0;
                InvalidateVisual();
            }
        };
        selectionAnimTimer.Start();
    }

    // ═══════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════

    public double Zoom => zoom;

    public void SetDocument(EditorDocument? doc)
    {
        document = doc;
        needsComposite = true;
        if (doc is not null)
            FitToView();
        InvalidateVisual();
    }

    public void SetActiveTool(ITool? tool)
    {
        activeTool?.Deactivate();
        activeTool = tool;
        activeTool?.Activate();
        Cursor = activeTool?.ToolCursor ?? Cursor.Default;
        InvalidateVisual();
    }

    public void MarkDirty()
    {
        needsComposite = true;
        cachedSelectionGeometry = null;
        InvalidateVisual();
    }

    public void SetZoom(double newZoom, Point? focusPoint = null)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - zoom) < 0.0001) return;

        if (focusPoint.HasValue)
        {
            // Adjust pan so the point under the cursor stays fixed
            var imagePoint = ScreenToImage(focusPoint.Value);
            zoom = newZoom;
            var newScreen = ImageToScreen(imagePoint);
            panOffset += focusPoint.Value - newScreen;
        }
        else
        {
            zoom = newZoom;
        }

        ZoomChanged?.Invoke(zoom);
        InvalidateVisual();
    }

    public void ZoomIn(Point? focusPoint = null) => SetZoom(zoom * 1.25, focusPoint);
    public void ZoomOut(Point? focusPoint = null) => SetZoom(zoom / 1.25, focusPoint);

    public void FitToView()
    {
        if (document is null || Bounds.Width == 0 || Bounds.Height == 0) return;

        double scaleX = Bounds.Width / document.Width;
        double scaleY = Bounds.Height / document.Height;
        zoom = Math.Min(scaleX, scaleY) * 0.9; // 90% to leave padding
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        panOffset = default;

        ZoomChanged?.Invoke(zoom);
        InvalidateVisual();
    }

    public void ActualPixels()
    {
        zoom = 1.0;
        panOffset = default;
        ZoomChanged?.Invoke(zoom);
        InvalidateVisual();
    }

    /// <summary>Pan the canvas by the given screen-space delta.</summary>
    public void Pan(Vector delta)
    {
        panOffset += delta;
        InvalidateVisual();
    }

    // ═══════════════════════════════════════════════════
    //  Coordinate Transforms
    // ═══════════════════════════════════════════════════

    /// <summary>Convert screen-space point to image pixel coordinates.</summary>
    public Point ScreenToImage(Point screen)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var origin = center + panOffset;

        if (document is null) return default;

        double imageX = (screen.X - origin.X) / zoom + document.Width / 2.0;
        double imageY = (screen.Y - origin.Y) / zoom + document.Height / 2.0;
        return new Point(imageX, imageY);
    }

    /// <summary>Convert image pixel coordinates to screen-space point.</summary>
    public Point ImageToScreen(Point image)
    {
        if (document is null) return default;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var origin = center + panOffset;

        double screenX = (image.X - document.Width / 2.0) * zoom + origin.X;
        double screenY = (image.Y - document.Height / 2.0) * zoom + origin.Y;
        return new Point(screenX, screenY);
    }

    // ═══════════════════════════════════════════════════
    //  Rendering
    // ═══════════════════════════════════════════════════

    public override void Render(DrawingContext context)
    {
        // Background fill
        var bgBrush = this.FindResource("BgCanvasBrush") as IBrush ?? Brushes.DimGray;
        context.DrawRectangle(bgBrush, null, new Rect(Bounds.Size));

        if (document is null) return;

        // Calculate the rect where the document image is drawn on screen
        var imageRect = GetImageScreenRect();

        // 1. Checkerboard background (transparency indicator)
        RenderCheckerboard(context, imageRect);

        // 2. Composited image
        RenderImage(context, imageRect);

        // 3. Image border
        var borderPen = new Pen(Brushes.Black, 1.0 / zoom);
        context.DrawRectangle(null, borderPen, imageRect);

        // 3.5. Selection marching ants overlay
        if (document.HasSelection)
            RenderSelectionOverlay(context, imageRect);

        // 4. Tool overlays
        using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(imageRect.X, imageRect.Y)))
        {
            activeTool?.RenderOverlay(context, zoom);
        }

        // 4.5. Brush cursor (size circle at mouse position)
        RenderBrushCursor(context, imageRect);

        // 5. Pixel grid (only at very high zoom)
        if (ShowPixelGrid && zoom >= 8.0)
            RenderPixelGrid(context, imageRect);

        // 6. Document grid
        if (ShowGrid)
            RenderDocumentGrid(context, imageRect);

        // 7. Rulers
        if (ShowRulers)
            RenderRulers(context, imageRect);
    }

    private Rect GetImageScreenRect()
    {
        if (document is null) return default;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var origin = center + panOffset;

        double w = document.Width * zoom;
        double h = document.Height * zoom;
        double x = origin.X - w / 2;
        double y = origin.Y - h / 2;

        return new Rect(x, y, w, h);
    }

    private void RenderCheckerboard(DrawingContext context, Rect imageRect)
    {
        EnsureCheckerBitmap(document!.Width, document.Height);
        if (checkerBitmap is null) return;

        context.DrawImage(checkerBitmap, new Rect(0, 0, document!.Width, document.Height), imageRect);
    }

    private void RenderImage(DrawingContext context, Rect imageRect)
    {
        if (document is null) return;

        if (needsComposite)
        {
            CompositeAndUpdateBitmap();
            needsComposite = false;
        }

        if (displayBitmap is not null)
        {
            context.DrawImage(displayBitmap, new Rect(0, 0, document.Width, document.Height), imageRect);
        }
    }

    /// <summary>
    /// Renders marching ants around the boundary of the document's selection mask.
    /// Builds a StreamGeometry of boundary edges and draws with animated dashed pen.
    /// </summary>
    private void RenderSelectionOverlay(DrawingContext context, Rect imageRect)
    {
        if (document?.SelectionMask is not { } mask) return;
        int w = document.Width;
        int h = document.Height;
        if (mask.Length != w * h) return;

        // Rebuild geometry cache when the selection mask reference changes
        if (!ReferenceEquals(mask, cachedSelectionMaskRef) || cachedSelectionGeometry is null)
        {
            cachedSelectionMaskRef = mask;
            cachedSelectionGeometry = BuildSelectionBoundaryGeometry(mask, w, h);
        }

        if (cachedSelectionGeometry is null) return;

        // Draw with transform matching the image position on screen
        using (context.PushTransform(
            Matrix.CreateScale(zoom, zoom) *
            Matrix.CreateTranslation(imageRect.X, imageRect.Y)))
        {
            double lineWidth = Math.Max(0.5, 1.0 / zoom);

            // Alternating black/white dashes: draw white dashes first (offset by half), then black on top
            var whitePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), lineWidth)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, selectionDashOffset + 4)
            };
            context.DrawGeometry(null, whitePen, cachedSelectionGeometry);

            var blackPen = new Pen(Brushes.Black, lineWidth)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, selectionDashOffset)
            };
            context.DrawGeometry(null, blackPen, cachedSelectionGeometry);
        }
    }

    private static StreamGeometry BuildSelectionBoundaryGeometry(byte[] mask, int w, int h)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        // Merge consecutive horizontal edges (top and bottom) into longer segments
        for (int y = 0; y <= h; y++)
        {
            int startX = -1;
            for (int x = 0; x < w; x++)
            {
                bool above = y > 0 && mask[(y - 1) * w + x] >= 128;
                bool below = y < h && mask[y * w + x] >= 128;
                bool isEdge = above != below;

                if (isEdge)
                {
                    if (startX < 0) startX = x;
                }
                else
                {
                    if (startX >= 0)
                    {
                        ctx.BeginFigure(new Point(startX, y), false);
                        ctx.LineTo(new Point(x, y));
                        ctx.EndFigure(false);
                        startX = -1;
                    }
                }
            }
            if (startX >= 0)
            {
                ctx.BeginFigure(new Point(startX, y), false);
                ctx.LineTo(new Point(w, y));
                ctx.EndFigure(false);
            }
        }

        // Merge consecutive vertical edges (left and right) into longer segments
        for (int x = 0; x <= w; x++)
        {
            int startY = -1;
            for (int y = 0; y < h; y++)
            {
                bool left = x > 0 && mask[y * w + x - 1] >= 128;
                bool right = x < w && mask[y * w + x] >= 128;
                bool isEdge = left != right;

                if (isEdge)
                {
                    if (startY < 0) startY = y;
                }
                else
                {
                    if (startY >= 0)
                    {
                        ctx.BeginFigure(new Point(x, startY), false);
                        ctx.LineTo(new Point(x, y));
                        ctx.EndFigure(false);
                        startY = -1;
                    }
                }
            }
            if (startY >= 0)
            {
                ctx.BeginFigure(new Point(x, startY), false);
                ctx.LineTo(new Point(x, h));
                ctx.EndFigure(false);
            }
        }

        return geometry;
    }

    private void RenderPixelGrid(DrawingContext context, Rect imageRect)
    {
        if (document is null) return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 0.5);

        double pixelW = zoom;
        int docW = document.Width;
        int docH = document.Height;

        // Vertical lines
        for (int x = 0; x <= docW; x++)
        {
            double sx = imageRect.X + x * pixelW;
            if (sx < 0 || sx > Bounds.Width) continue;
            context.DrawLine(pen, new Point(sx, Math.Max(0, imageRect.Y)), new Point(sx, Math.Min(Bounds.Height, imageRect.Bottom)));
        }
        // Horizontal lines
        for (int y = 0; y <= docH; y++)
        {
            double sy = imageRect.Y + y * pixelW;
            if (sy < 0 || sy > Bounds.Height) continue;
            context.DrawLine(pen, new Point(Math.Max(0, imageRect.X), sy), new Point(Math.Min(Bounds.Width, imageRect.Right), sy));
        }
    }

    private void RenderDocumentGrid(DrawingContext context, Rect imageRect)
    {
        if (document is null || GridSpacing <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 200, 255)), 1);

        double pixelW = zoom;
        int docW = document.Width;
        int docH = document.Height;

        for (int x = GridSpacing; x < docW; x += GridSpacing)
        {
            double sx = imageRect.X + x * pixelW;
            if (sx >= 0 && sx <= Bounds.Width)
                context.DrawLine(pen, new Point(sx, Math.Max(0, imageRect.Y)), new Point(sx, Math.Min(Bounds.Height, imageRect.Bottom)));
        }
        for (int y = GridSpacing; y < docH; y += GridSpacing)
        {
            double sy = imageRect.Y + y * pixelW;
            if (sy >= 0 && sy <= Bounds.Height)
                context.DrawLine(pen, new Point(Math.Max(0, imageRect.X), sy), new Point(Math.Min(Bounds.Width, imageRect.Right), sy));
        }
    }

    private void RenderRulers(DrawingContext context, Rect imageRect)
    {
        if (document is null) return;
        var rulerBg = new SolidColorBrush(Color.FromArgb(220, 50, 50, 50));
        var tickPen = new Pen(Brushes.Gray, 0.5);
        var textBrush = Brushes.LightGray;
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.Normal);

        // Top ruler
        context.DrawRectangle(rulerBg, null, new Rect(0, 0, Bounds.Width, RulerThickness));

        // Left ruler
        context.DrawRectangle(rulerBg, null, new Rect(0, 0, RulerThickness, Bounds.Height));

        // Determine tick spacing based on zoom
        int tickStep = zoom >= 4 ? 10 : zoom >= 1 ? 50 : zoom >= 0.25 ? 100 : 500;
        int docW = document.Width;
        int docH = document.Height;

        // Top ruler ticks
        for (int px = 0; px <= docW; px += tickStep)
        {
            double sx = imageRect.X + px * zoom;
            if (sx < RulerThickness || sx > Bounds.Width) continue;

            double tickH = (px % (tickStep * 5) == 0) ? RulerThickness * 0.7 : RulerThickness * 0.35;
            context.DrawLine(tickPen, new Point(sx, RulerThickness - tickH), new Point(sx, RulerThickness));

            if (px % (tickStep * 5) == 0)
            {
                var text = new FormattedText(px.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 8, textBrush);
                context.DrawText(text, new Point(sx + 2, 1));
            }
        }

        // Left ruler ticks
        for (int py = 0; py <= docH; py += tickStep)
        {
            double sy = imageRect.Y + py * zoom;
            if (sy < RulerThickness || sy > Bounds.Height) continue;

            double tickW = (py % (tickStep * 5) == 0) ? RulerThickness * 0.7 : RulerThickness * 0.35;
            context.DrawLine(tickPen, new Point(RulerThickness - tickW, sy), new Point(RulerThickness, sy));

            if (py % (tickStep * 5) == 0)
            {
                var text = new FormattedText(py.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 8, textBrush);
                // Draw rotated text would be ideal but for simplicity draw horizontally
                context.DrawText(text, new Point(1, sy + 2));
            }
        }

        // Corner box
        context.DrawRectangle(rulerBg, null, new Rect(0, 0, RulerThickness, RulerThickness));
    }

    private void RenderBrushCursor(DrawingContext context, Rect imageRect)
    {
        if (!ShowBrushCursor || BrushCursorSize <= 0 || document is null) return;

        // Get the mouse position in screen space
        var screenPos = lastPointerPos;
        double radius = (BrushCursorSize / 2.0) * zoom;

        // Clamp minimum radius for visibility
        if (radius < 2) radius = 2;

        var cursorPen = new Pen(Brushes.White, 1.0);
        var shadowPen = new Pen(Brushes.Black, 1.0);

        // Draw shadow circle then white circle for visibility on any background
        context.DrawEllipse(null, shadowPen, new Point(screenPos.X, screenPos.Y), radius + 0.5, radius + 0.5);
        context.DrawEllipse(null, cursorPen, new Point(screenPos.X, screenPos.Y), radius, radius);

        // Draw crosshair at center
        double crossSize = Math.Min(6, radius * 0.3);
        if (crossSize >= 2)
        {
            context.DrawLine(cursorPen, new Point(screenPos.X - crossSize, screenPos.Y), new Point(screenPos.X + crossSize, screenPos.Y));
            context.DrawLine(cursorPen, new Point(screenPos.X, screenPos.Y - crossSize), new Point(screenPos.X, screenPos.Y + crossSize));
        }
    }

    // ═══════════════════════════════════════════════════
    //  Pixel Conversion: SharpImage ushort → BGRA8
    // ═══════════════════════════════════════════════════

    private void CompositeAndUpdateBitmap()
    {
        if (document is null) return;

        // Flatten layers into a single RGBA ImageFrame
        cachedFlattened = document.Flatten();

        int w = document.Width;
        int h = document.Height;

        // Create or resize the display bitmap
        if (displayBitmap is null || displayBitmap.PixelSize.Width != w || displayBitmap.PixelSize.Height != h)
        {
            displayBitmap?.Dispose();
            displayBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        }

        // Lock bitmap and copy converted pixel data
        using var framebuffer = displayBitmap.Lock();
        ConvertToBgra8(cachedFlattened, framebuffer.Address, framebuffer.RowBytes, w, h);
    }

    /// <summary>
    /// Converts SharpImage 16-bit per channel RGBA data to 8-bit BGRA premultiplied
    /// suitable for Avalonia WritableBitmap.
    /// </summary>
    private unsafe void ConvertToBgra8(ImageFrame frame, nint destPtr, int destStride, int width, int height)
    {
        var cache = frame.Cache;
        if (cache is null) return;

        int srcChannels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        bool showR = ShowRedChannel, showG = ShowGreenChannel, showB = ShowBlueChannel;
        bool allChannelsVisible = showR && showG && showB;

        // Quantum.MaxValue is 65535 for 16-bit depth
        const double scale = 255.0 / 65535.0;

        for (int y = 0; y < height; y++)
        {
            var srcRow = frame.GetPixelRow(y);
            byte* destRow = (byte*)destPtr + (long)y * destStride;

            for (int x = 0; x < width; x++)
            {
                int srcIdx = x * srcChannels;
                int destIdx = x * 4;

                ushort r = srcRow[srcIdx];
                ushort g = srcChannels > 1 ? srcRow[srcIdx + 1] : r;
                ushort b = srcChannels > 2 ? srcRow[srcIdx + 2] : r;
                ushort a = hasAlpha && srcChannels > 3 ? srcRow[srcIdx + 3] : (ushort)65535;

                // Convert 16-bit → 8-bit
                byte r8 = (byte)(r * scale + 0.5);
                byte g8 = (byte)(g * scale + 0.5);
                byte b8 = (byte)(b * scale + 0.5);
                byte a8 = (byte)(a * scale + 0.5);

                // Channel visibility masking
                if (!allChannelsVisible)
                {
                    if (!showR) r8 = 0;
                    if (!showG) g8 = 0;
                    if (!showB) b8 = 0;
                }

                // Premultiply alpha for BGRA premul format
                if (a8 < 255)
                {
                    r8 = (byte)(r8 * a8 / 255);
                    g8 = (byte)(g8 * a8 / 255);
                    b8 = (byte)(b8 * a8 / 255);
                }

                // BGRA order
                destRow[destIdx]     = b8;
                destRow[destIdx + 1] = g8;
                destRow[destIdx + 2] = r8;
                destRow[destIdx + 3] = a8;
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  Checkerboard Generation
    // ═══════════════════════════════════════════════════

    private void EnsureCheckerBitmap(int width, int height)
    {
        if (checkerBitmap is not null && checkerBitmap.PixelSize.Width == width && checkerBitmap.PixelSize.Height == height)
            return;

        checkerBitmap?.Dispose();
        checkerBitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);

        using var fb = checkerBitmap.Lock();
        GenerateCheckerboard(fb.Address, fb.RowBytes, width, height);
    }

    private static unsafe void GenerateCheckerboard(nint ptr, int stride, int width, int height)
    {
        byte lightR = CheckerLight.R, lightG = CheckerLight.G, lightB = CheckerLight.B;
        byte darkR = CheckerDark.R, darkG = CheckerDark.G, darkB = CheckerDark.B;

        for (int y = 0; y < height; y++)
        {
            byte* row = (byte*)ptr + (long)y * stride;
            int yBlock = (y / CheckerSize) & 1;

            for (int x = 0; x < width; x++)
            {
                int xBlock = (x / CheckerSize) & 1;
                bool isLight = (xBlock ^ yBlock) == 0;

                int idx = x * 4;
                row[idx]     = isLight ? lightB : darkB; // B
                row[idx + 1] = isLight ? lightG : darkG; // G
                row[idx + 2] = isLight ? lightR : darkR; // R
                row[idx + 3] = 255;                       // A
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  Input Handling
    // ═══════════════════════════════════════════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var props = e.GetCurrentPoint(this).Properties;

        // Middle-click pan
        if (props.IsMiddleButtonPressed)
        {
            isPointerCaptured = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (document is null || activeTool is null) return;

        var rawPos = e.GetPosition(this);
        var canvasPoint = ScreenToImage(rawPos);
        // Pass zoom to crop tool for zoom-aware hit testing
        if (activeTool is CropTool cropTool)
            cropTool.CurrentZoom = zoom;

        activeTool.OnPointerPressed(e, canvasPoint);

        // Painting tools modify pixels immediately on first click — recomposite
        if (activeTool is BrushTool or EraserTool or CloneStampTool or DodgeBurnTool
            or BlurBrushTool or SharpenBrushTool or SmudgeTool or HealingBrushTool)
            needsComposite = true;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var screenPos = e.GetPosition(this);

        // Middle-button panning
        if (isPointerCaptured)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsMiddleButtonPressed)
            {
                var delta = screenPos - lastPointerPos;
                panOffset += delta;
                InvalidateVisual();
            }
            lastPointerPos = screenPos;
            e.Handled = true;
            return;
        }

        // Update cursor position in image space
        if (document is not null)
        {
            var imagePos = ScreenToImage(screenPos);
            int px = (int)Math.Floor(imagePos.X);
            int py = (int)Math.Floor(imagePos.Y);
            CursorPositionChanged?.Invoke(px, py);
        }

        if (activeTool is not null && document is not null)
        {
            var canvasPoint = ScreenToImage(screenPos);
            activeTool.OnPointerMoved(e, canvasPoint);

            // For painting tools that modify pixels during drag, recomposite
            if (activeTool is BrushTool or EraserTool or CloneStampTool or DodgeBurnTool
                or BlurBrushTool or SharpenBrushTool or SmudgeTool or HealingBrushTool)
                needsComposite = true;

            InvalidateVisual();
        }

        lastPointerPos = screenPos;
    }

    private Point lastPointerPos;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (isPointerCaptured)
        {
            isPointerCaptured = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (activeTool is not null && document is not null)
        {
            var canvasPoint = ScreenToImage(e.GetPosition(this));
            activeTool.OnPointerReleased(e, canvasPoint);
            needsComposite = true; // tool may have modified pixels
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+Wheel or plain wheel for zoom
        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001) return;

        double factor = delta > 0 ? 1.15 : 1.0 / 1.15;
        SetZoom(zoom * factor, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        activeTool?.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        activeTool?.OnKeyUp(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Re-fit if this is the first layout with a document
        if (document is not null && Math.Abs(zoom - 1.0) < 0.001 && panOffset == default)
            FitToView();
    }
}
